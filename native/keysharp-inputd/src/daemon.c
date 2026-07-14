#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif

#include "keysharp_inputd/daemon.h"

#include "connection_ref.h"
#include "keysharp_inputd/ipc.h"
#include "keysharp_inputd/linux_devices.h"
#include "keysharp_inputd/linux_synth.h"
#include "keysharp_trust/permissions.h"
#include "keysharp_inputd/platform.h"
#include "keysharp_inputd/protocol.h"
#include "keysharp_inputd/synthetic_hooks.h"
#include "pipe_ring.h"
#include "worker_pool.h"
#include "wake_pipe.h"

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <stdbool.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sched.h>
#include <sys/socket.h>
#include <sys/random.h>
#include <time.h>
#include <unistd.h>

_Static_assert(KSI_CAP_HOOK_KEYBOARD == KST_CAP_INPUT_HOOK_KEYBOARD, "trust/input capability mismatch");
_Static_assert(KSI_CAP_HOOK_MOUSE == KST_CAP_INPUT_HOOK_MOUSE, "trust/input capability mismatch");
_Static_assert(KSI_CAP_SYNTH_KEYBOARD == KST_CAP_INPUT_SYNTH_KEYBOARD, "trust/input capability mismatch");
_Static_assert(KSI_CAP_SYNTH_MOUSE == KST_CAP_INPUT_SYNTH_MOUSE, "trust/input capability mismatch");
_Static_assert(KSI_CAP_BLOCK_INPUT == KST_CAP_INPUT_BLOCK, "trust/input capability mismatch");

#define KSI_MAX_CLIENTS 64
#define KSI_MAX_BACKEND_FDS 160
#define KSI_MAX_POLL_FDS (3 + KSI_MAX_BACKEND_FDS + KSI_MAX_CLIENTS)
#define KSI_MAX_PENDING_COMMANDS 256
#define KSI_MAX_SYNTH_INPUTS 1024
#define KSI_MAX_MODIFY_INPUTS KSI_MAX_SYNTH_INPUTS
#define KSI_HOOK_DECISION_TIMEOUT_MS 1000u
/* Per-lane consecutive hook failures before the client is disconnected. */
#define KSI_MAX_CONSECUTIVE_HOOK_FAILURES 5u
#define KSI_MAX_LANE_ACTIONS 512u
/* Cap on read() iterations per client per poll pass. Without it a client that
 * streams unanswered frames (e.g. one-way HEARTBEATs) keeps the evdev-reader
 * thread in the client read loop, starving physical-device ingestion — the
 * device plane already has KSI_MAX_DEVICE_EVENTS_PER_PASS. 8 x 64KB is generous
 * for legitimate bursts; the common case still exits on the first short read. */
#define KSI_MAX_CLIENT_READS_PER_PASS 8
#define KSI_MAX_OUTPUT_ACTIONS 4096u
#define KSI_MAX_SYNTH_HOOK_ACTIONS 4096u

#define KSI_MAX_OUTPUT_SYNTH_BYTES (1024u * 1024u)
#define KSI_MAX_SYNTH_HOOK_STEPS_PER_PASS 256u
#define KSI_MAX_RECURSION_DEPTH 32u
#define KSI_HOOK_SEND_ORDER_WAIT_MS 10
#define KSI_SHUTDOWN_TIMEOUT_MS 5000u
#define KSI_GRAB_LEASE_TIMEOUT_MS 15000u
#define KSI_IDLE_EXIT_MS 30000u
/* CLIENT_HELLO deadline; peers that connect and send nothing cannot pin slots. */
#define KSI_HANDSHAKE_TIMEOUT_MS 10000u
/* Idle deadline for authenticated-but-capability-less connections (no grants, no
 * hook/block subscriptions). Reset by any message, so a live query channel that
 * polls within this window is never reaped; only truly-idle capless connections
 * that would otherwise pin a slot forever are dropped. */
#define KSI_CAPLESS_IDLE_TIMEOUT_MS 300000u
/* Minimum spacing between honored client-forced permission prompts. */
#define KSI_FORCED_PROMPT_COOLDOWN_MS 3000u
/* Reserve a few client slots for other users/root helpers on shared systems. */
#define KSI_MAX_CLIENTS_PER_UID (KSI_MAX_CLIENTS - 8u)
#define KSI_VK_BACK 0x08u
#define KSI_VK_LCONTROL 0xA2u
#define KSI_VK_RCONTROL 0xA3u
#define KSI_VK_LALT 0xA4u
#define KSI_VK_RALT 0xA5u
#define KSI_MOD_LCONTROL 0x01u
#define KSI_MOD_RCONTROL 0x02u
#define KSI_MOD_LALT 0x04u
#define KSI_MOD_RALT 0x08u

static volatile sig_atomic_t keep_running = 1;

/* Fail open if a safety-critical notification cannot reach the main thread.
 * Retaining a stale grab or BlockInput mask is worse than dropping hooks. */
static atomic_int g_fail_open_requested = 0;

/* Self-pipe wake; EAGAIN/EPIPE are harmless because readers drain eagerly. */
static inline void wake_pipe_write(int fd)
{
    uint8_t byte = 1;
    ssize_t written = write(fd, &byte, sizeof(byte));
    (void)written;
}

/* Two SEPARATE pools, not one shared pool, so a permission prompt -- which
 * blocks on the user for as long as they take to answer, potentially
 * indefinitely -- can never starve process-identity resolution for an
 * unrelated client out of a worker thread. Both connecting-client identity
 * resolution (fast, frequent) and permission prompts (slow, rare, blocks on a
 * human) used to share g_worker_pool's fixed thread count; two prompts alone
 * could fill it and stall every other client's CLIENT_HELLO indefinitely. */
static ksi_worker_pool g_worker_pool;
static ksi_worker_pool g_identify_worker_pool;

typedef enum ksi_client_state {
    KSI_CLIENT_STATE_IDENTIFYING,     /* process identity resolution running on worker thread */
    KSI_CLIENT_STATE_READY,           /* identity known, waiting for or able to process CLIENT_HELLO */
    KSI_CLIENT_STATE_AWAITING_PROMPT, /* permission prompt running on worker thread */
} ksi_client_state;

/* Main-thread-only: holds application state for one authenticated client. */
typedef struct ksi_client {
    int fd;
    ksi_hook_send_ref *hook_send_ref;
    uint8_t *rx_buffer;
    size_t rx_used;
    uint64_t connection_id;
    pid_t pid;
    uid_t uid;
    gid_t gid;
    uint64_t start_time;   /* field 22 of /proc/<pid>/stat; 0 if not yet known */
    /* Process start time captured at accept, compared against the value the
     * identity worker reads later; a mismatch means the pid was recycled between
     * accept and identify, so /proc no longer describes the connecting process. */
    uint64_t accept_start_time;
    ksi_client_state state;
    bool identity_attempted;
    bool has_identity;
    bool authenticated;
    bool role_initialized;
    uint32_t connection_role;
    uint8_t hook_session_token[KSI_HOOK_SESSION_TOKEN_SIZE];
    uint64_t bound_hook_connection_id;
    /* Set by CLIENT_HELLO; missing handshakes are dropped after the deadline. */
    bool hello_seen;
    uint64_t connected_at_ms;
    uint32_t granted_capabilities;
    /* Session-only "Deny" bits for THIS connection (the managed side keeps one connection
     * per Keysharp run): suppress re-prompting for the rest of the run WITHOUT persisting to
     * disk, so a mis-clicked Deny recovers on the next run. Never serialized; dies with the fd. */
    uint32_t session_denied_capabilities;
    uint32_t hook_subscriptions;
    uint32_t block_input_mask;
    /* Consecutive hook failures per lane: index 0 keyboard, 1 mouse. */
    uint32_t consecutive_hook_failures[2];
    uint32_t quarantined_hooks;
    uint32_t quarantine_generation[2];
    uint64_t quarantine_rearm_after_ms[2];
    uint64_t last_hook_quarantine_ms[2];
    uint64_t lease_expires_ms;
    /* Last time any message was received; drives the capless-idle reaper so an
     * authenticated but capability-less connection cannot pin a slot forever. */
    uint64_t last_activity_ms;
    char exe_path[KSI_PERMISSION_MAX_PATH];
    char command_line[KSI_PERMISSION_MAX_COMMAND_LINE];
    char exe_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
    /* WILDCARD identity (exe portion only, no argv): keys the "Allow for all scripts" grant that
     * covers every script run by this same Keysharp binary. Empty when identity is unknown. */
    char wildcard_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
    /* Buffered CLIENT_HELLO waiting for identification or prompt to complete. */
    bool pending_hello_valid;
    uint32_t pending_hello_requested;
    uint32_t pending_hello_flags;
    uint64_t pending_hello_correlation_id;
    uint32_t pending_hello_client_id;
} ksi_client;

/* Heap-allocated payload for KSI_DAEMON_COMMAND_CLIENT_IDENTIFIED. */
typedef struct ksi_client_identified_result {
    bool has_identity;
    char exe_path[KSI_PERMISSION_MAX_PATH];
    char command_line[KSI_PERMISSION_MAX_COMMAND_LINE];
    char exe_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
    char wildcard_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
    uint64_t start_time;
} ksi_client_identified_result;

typedef enum ksi_daemon_command_type {
    KSI_DAEMON_COMMAND_CLIENT_IDENTIFIED, /* worker -> main: identity resolution complete */
    KSI_DAEMON_COMMAND_CLIENT_PROMPT_DONE, /* worker → main: user permission prompt complete */
    KSI_DAEMON_COMMAND_LANE_HOOK_FAILURE, /* lane → main: send/timeout failure for a client */
} ksi_daemon_command_type;

typedef struct ksi_daemon_command {
    ksi_daemon_command_type type;
    int client_fd;
    uint64_t connection_id;
    union {
        struct {
            ksi_client_identified_result *result; /* heap-allocated; freed by consumer */
        } identified;
        struct {
            ksi_permission_decision decision;
            uint32_t requested_capabilities;
            uint32_t missing_capabilities;
            /* state->available_capabilities as it was WHEN THE PROMPT STARTED,
             * not when it completes. A permission prompt can sit open for
             * seconds; using the current (post-wait) availability here would
             * let a transient hotplug during that window silently shrink what
             * "success" means without ever surfacing an error. See
             * process_client_prompt_done. */
            uint32_t available_capabilities_at_start;
        } prompt_done;
        struct {
            uint32_t hook_type; /* KSI_HOOK_KEYBOARD_LL / KSI_HOOK_MOUSE_LL */
            uint64_t event_id;
            uint64_t hook_session_connection_id;
            uint32_t nesting_depth;
            uint32_t elapsed_ms;
            uint32_t reason;
        } hook_failure;
    } data;
} ksi_daemon_command;

static void free_daemon_command(ksi_daemon_command *command);

/* --- Typed queues built on ksi_pipe_ring --- */

/* Worker/lane threads -> main thread. */
typedef struct ksi_daemon_command_queue { ksi_pipe_ring ring; } ksi_daemon_command_queue;

static int command_queue_init(ksi_daemon_command_queue *q)
{
    return q == NULL ? -1
        : ksi_pipe_ring_init(&q->ring, sizeof(ksi_daemon_command), KSI_MAX_PENDING_COMMANDS);
}

static void command_queue_destroy(ksi_daemon_command_queue *q)
{
    ksi_daemon_command cmd;

    if (q == NULL) {
        return;
    }

    /* Drain remaining items to free any heap-allocated frame buffers. */
    while (ksi_pipe_ring_pop(&q->ring, &cmd)) {
        free_daemon_command(&cmd);
    }

    ksi_pipe_ring_close(&q->ring);
}

/* Forward decls for types embedded in ksi_daemon_state.  Definitions and
 * helper functions live later in the file, alongside the sequencer thread. */
typedef enum ksi_output_action_type {
    KSI_OUTPUT_ACTION_REPLAY = 0,
    KSI_OUTPUT_ACTION_SYNTH,
    /* Release keys held by replay/synth, serialized with normal output. */
    KSI_OUTPUT_ACTION_RELEASE_ALL,
    /* Recreate a broken synthetic-output device. Runs the stop+start on the
     * output sequencer thread so it honors the single-thread invariant for the
     * uinput fds / key-down table instead of racing them from the main thread. */
    KSI_OUTPUT_ACTION_RECREATE_SYNTH,
} ksi_output_action_type;

typedef struct ksi_output_action {
    ksi_output_action_type type;
    uint32_t hook_type;
    ksi_hook_event_payload replay_payload;
    uint32_t synth_flags;
    uint32_t synth_count;
    ksi_input *synth_inputs;
} ksi_output_action;

typedef struct ksi_output_queue {
    pthread_mutex_t mutex;
    bool mutex_initialized;
    ksi_output_action actions[KSI_MAX_OUTPUT_ACTIONS];
    size_t head;
    size_t count;
    size_t synth_bytes;
    int wake_read_fd;
    int wake_write_fd;
} ksi_output_queue;

typedef struct ksi_synth_completion {
    ksi_hook_send_ref *send_ref;
    struct ksi_daemon_state *state;
    uint32_t client_id;
    uint64_t correlation_id;
    atomic_uint remaining;
    atomic_bool result_sent;
    bool owns_atomic_transaction;
} ksi_synth_completion;

typedef enum ksi_lane_egress_type {
    KSI_LANE_EGRESS_NONE = 0,
    KSI_LANE_EGRESS_REPLAY,
    KSI_LANE_EGRESS_SYNTH,
} ksi_lane_egress_type;

typedef struct ksi_lane_egress {
    ksi_lane_egress_type type;
    uint32_t synth_flags;
    uint32_t synth_count;
    ksi_input synth_inputs[1];
} ksi_lane_egress;

typedef struct ksi_synthetic_hook_item {
    ksi_input input;
    uint64_t queued_at_ns;
    ksi_synth_completion *completion;
    bool batch_start;  /* first node of a client batch; re-arms the surrogate reset */
} ksi_synthetic_hook_item;

typedef struct ksi_synthetic_hook_queue {
    pthread_mutex_t mutex;
    bool mutex_initialized;
    ksi_synthetic_hook_item items[KSI_MAX_SYNTH_HOOK_ACTIONS];
    size_t head;
    size_t count;
    int wake_read_fd;
    int wake_write_fd;
} ksi_synthetic_hook_queue;

typedef struct ksi_lane_subscriber {
    ksi_hook_send_ref *send_ref;
    uint64_t connection_id;
    uint64_t hook_session_id;
    bool uses_callback_rpc;
} ksi_lane_subscriber;

/* Immutable hook event snapshot. Lanes never touch state->clients[]. The
 * flexible tail stores only the subscribers which actually receive this event. */
typedef struct ksi_lane_event {
    uint64_t event_id;
    uint32_t hook_type;
    uint32_t generation;
    ksi_synth_completion *synthetic_completion;
    ksi_lane_egress egress;
    ksi_hook_event_payload payload;
    size_t payload_size;
    size_t subscriber_count;
    uint32_t nesting_depth;
    uint64_t inherited_deadline_ms;
    struct ksi_nested_event_waiter *nested_waiter;
    bool physical_input;
    ksi_lane_subscriber subscribers[];
} ksi_lane_event;

typedef enum ksi_nested_wait_state {
    KSI_NESTED_WAIT_PENDING = 0,
    KSI_NESTED_WAIT_COMPLETED,
    KSI_NESTED_WAIT_FAILED,
    KSI_NESTED_WAIT_ABANDONED,
} ksi_nested_wait_state;

typedef struct ksi_nested_event_waiter {
    pthread_mutex_t mutex;
    pthread_cond_t condition;
    ksi_nested_wait_state state;
} ksi_nested_event_waiter;

typedef struct ksi_nested_member {
    ksi_input input;
    ksi_lane_event *event;
} ksi_nested_member;

typedef struct ksi_nested_transaction {
    uint32_t count;
    uint32_t depth;
    uint32_t origin_generation;
    uint64_t origin_hook_connection_id;
    ksi_synth_completion *completion;
    ksi_nested_member members[];
} ksi_nested_transaction;

typedef struct ksi_nested_transaction_queue {
    ksi_pipe_ring ring;
} ksi_nested_transaction_queue;

/* Decision routed from the main thread into a lane thread. The lane validates
 * that responder_fd matches the current subscriber it is waiting on; mismatched
 * decisions are dropped. */
typedef struct ksi_lane_decision {
    uint64_t event_id;
    uint32_t decision;
    int responder_fd;
    uint64_t responder_connection_id;
    uint32_t input_count;
    ksi_input inputs[KSI_MAX_MODIFY_INPUTS];
} ksi_lane_decision;

/* Bytes actually meaningful in a decision: the header plus input_count inputs.
 * The embedded inputs[] array is ~40KB; the common PASS decision uses 0 inputs,
 * so copying/clearing the whole struct per event is wasted memory traffic on the
 * hot path. Copy only the used prefix. All readers are bounded by input_count. */
static inline size_t ksi_lane_decision_size(uint32_t input_count)
{
    if (input_count > KSI_MAX_MODIFY_INPUTS) {
        input_count = KSI_MAX_MODIFY_INPUTS;
    }

    return offsetof(ksi_lane_decision, inputs) + (size_t)input_count * sizeof(ksi_input);
}

/* If a lane is full, new physical events bypass hooks and replay fail-open. */
typedef struct ksi_lane_action_queue {
    ksi_pipe_ring ring;
} ksi_lane_action_queue;

typedef struct ksi_lane_decision_queue {
    pthread_mutex_t mutex;
    pthread_cond_t condition;
    bool mutex_initialized;
    bool condition_initialized;
    bool has_decision;
    ksi_lane_decision decision;
} ksi_lane_decision_queue;

typedef struct ksi_hook_lane {
    uint32_t hook_type;
    pthread_t thread;
    bool thread_started;
    atomic_uint_least64_t current_event_id;  /* 0 = idle */
    atomic_int current_responder_fd;
    atomic_uint_least64_t current_responder_connection_id;
    /* The HOOK_STREAM session which owns the active callback. Decisions may
     * arrive on its bound CALLBACK_RPC fd/connection instead. */
    atomic_uint_least64_t current_hook_session_connection_id;
    atomic_uint current_nesting_depth;
    /* Set by lane_shutdown to abort waits and drain promptly. */
    atomic_int shutting_down;
    /* Incremented by EmergencyPassthrough. Events captured under an older
     * generation skip subscriber callbacks and finalize immediately. */
    atomic_uint flush_generation;
    ksi_lane_action_queue action_queue;
    ksi_lane_decision_queue decision_queue;
    ksi_nested_transaction_queue nested_queue;
    struct ksi_daemon_state *state;
} ksi_hook_lane;

typedef struct ksi_daemon_state {
    const ksi_platform_backend *backend;
    ksi_client clients[KSI_MAX_CLIENTS];
    nfds_t client_count;
    ksi_daemon_command_queue *commands;
    ksi_permission_store *permissions;
    uint32_t available_capabilities;
    uint64_t next_connection_id;
    uint64_t next_event_id;
    /* Single ordered uinput writer for replay, modify and SendInput output. */
    ksi_output_queue output_queue;
    ksi_synthetic_hook_queue synthetic_hook_queue;
    atomic_int synthetic_hook_inflight;
    atomic_uint active_synthetic_transactions;
    atomic_uint physical_hook_inflight;
    pthread_t sequencer_thread;
    bool sequencer_thread_started;
    atomic_int sequencer_running;
    /* Independent hook lanes; shared subscribers are serialized by hook_send_ref. */
    ksi_hook_lane keyboard_lane;
    ksi_hook_lane mouse_lane;
    /* Used to enqueue RELEASE_ALL on keyboard grab held->released. */
    bool keyboard_grab_active;
    /* Per-uid FORCE_PROMPT throttle. A global timestamp let one uid on the shared
     * 0666 system socket suppress every other uid's forcePrompt re-ask; keying by
     * uid confines the cooldown to the spamming user. LRU-evict when full. */
    struct {
        uid_t uid;
        bool in_use;
        uint64_t last_ms;
    } forced_prompt_by_uid[16];
} ksi_daemon_state;

typedef struct ksi_binary_message_view {
    const ksi_message_header *header;
    const uint8_t *payload;
    size_t payload_size;
} ksi_binary_message_view;

static int update_grab_state(ksi_daemon_state *state);
static void clear_hook_state(ksi_daemon_state *state);
static void record_client_hook_failure(
    ksi_daemon_state *state, nfds_t index, uint32_t hook_type,
    uint64_t event_id, uint32_t nesting_depth, uint32_t elapsed_ms,
    uint32_t reason);
static void lane_flush_passthrough(ksi_hook_lane *lane);
static bool output_queue_push_release_all(ksi_output_queue *q);

/* Per-hook-type failure-counter slot: 0 keyboard, 1 mouse. */
static size_t hook_type_to_lane_index(uint32_t hook_type)
{
    return hook_type == KSI_HOOK_MOUSE_LL ? 1u : 0u;
}
static void send_status(
    int client_fd,
    const ksi_message_header *request,
    uint32_t response_type,
    int32_t status,
    uint32_t detail);
static void remove_client(ksi_daemon_state *state, nfds_t index);
static void send_indicator_state_result(int client_fd, const ksi_message_header *request);
static void send_pointer_position_result(int client_fd, const ksi_message_header *request);
static void send_pointer_buttons_result(int client_fd, const ksi_message_header *request);
static void handle_get_key_state(ksi_client *client, const ksi_binary_message_view *message);
static void set_realtime_priority(const char *thread_name);
static ssize_t find_client_index_by_fd(const ksi_daemon_state *state, int client_fd);
static ssize_t find_client_index_by_connection(
    const ksi_daemon_state *state,
    int client_fd,
    uint64_t connection_id);
static void process_client_identified(ksi_daemon_state *state, ksi_daemon_command *command);
static void process_client_prompt_done(ksi_daemon_state *state, ksi_daemon_command *command);

static bool fill_hook_session_token(uint8_t token[KSI_HOOK_SESSION_TOKEN_SIZE])
{
    size_t offset = 0u;

    while (offset < KSI_HOOK_SESSION_TOKEN_SIZE) {
        ssize_t got = getrandom(token + offset, KSI_HOOK_SESSION_TOKEN_SIZE - offset, 0);

        if (got > 0) {
            offset += (size_t)got;
            continue;
        }

        if (got < 0 && errno == EINTR) {
            continue;
        }

        memset(token, 0, KSI_HOOK_SESSION_TOKEN_SIZE);
        return false;
    }

    return true;
}

static ksi_client *find_hook_session(
    ksi_daemon_state *state,
    const ksi_client *binding_client,
    const uint8_t token[KSI_HOOK_SESSION_TOKEN_SIZE])
{
    if (state == NULL || binding_client == NULL || token == NULL) {
        return NULL;
    }

    for (nfds_t i = 0; i < state->client_count; i++) {
        ksi_client *candidate = &state->clients[i];

        if (candidate->role_initialized
            && candidate->connection_role == KSI_CONNECTION_HOOK_STREAM
            && candidate->hello_seen
            && candidate->authenticated
            && candidate->has_identity
            && candidate->pid == binding_client->pid
            && candidate->uid == binding_client->uid
            && memcmp(candidate->hook_session_token, token, KSI_HOOK_SESSION_TOKEN_SIZE) == 0) {
            return candidate;
        }
    }

    return NULL;
}

static const ksi_client *find_callback_rpc_for_session(
    const ksi_daemon_state *state,
    uint64_t hook_connection_id,
    uint64_t except_connection_id)
{
    if (state == NULL || hook_connection_id == 0u) {
        return NULL;
    }

    for (nfds_t i = 0; i < state->client_count; i++) {
        const ksi_client *candidate = &state->clients[i];

        if (candidate->connection_id != except_connection_id
            && candidate->role_initialized
            && candidate->connection_role == KSI_CONNECTION_CALLBACK_RPC
            && candidate->bound_hook_connection_id == hook_connection_id) {
            return candidate;
        }
    }

    return NULL;
}

static void handle_signal(int signal_number)
{
    (void)signal_number;
    keep_running = 0;
}

static int install_signal_handlers(void)
{
    struct sigaction action;
    struct sigaction ignore_action;

    memset(&action, 0, sizeof(action));
    action.sa_handler = handle_signal;

    memset(&ignore_action, 0, sizeof(ignore_action));
    ignore_action.sa_handler = SIG_IGN;

    if (sigemptyset(&action.sa_mask) != 0) {
        return -1;
    }

    if (sigemptyset(&ignore_action.sa_mask) != 0) {
        return -1;
    }

    if (sigaction(SIGINT, &action, NULL) != 0) {
        return -1;
    }

    if (sigaction(SIGTERM, &action, NULL) != 0) {
        return -1;
    }

    if (sigaction(SIGPIPE, &ignore_action, NULL) != 0) {
        return -1;
    }

    return 0;
}

static void free_daemon_command(ksi_daemon_command *command)
{
    if (command == NULL) {
        return;
    }

    if (command->type == KSI_DAEMON_COMMAND_CLIENT_IDENTIFIED) {
        free(command->data.identified.result);
        command->data.identified.result = NULL;
    }
}

#include "daemon/privilege_workers.inc"
#include "daemon/client_lifecycle.inc"
#include "daemon/hook_lanes.inc"
#include "daemon/grab_leases.inc"
#include "daemon/hook_dispatch.inc"
#include "daemon/hook_ingress.inc"
#include "daemon/protocol_server.inc"

/* Runs the backend's periodic maintenance, then two follow-ups the daemon owns:
 *   1. If the backend reports its synthetic-output device has failed, enqueue a
 *      recreate action so the stop+start runs on the OUTPUT SEQUENCER thread
 *      (not here on the main thread, which would race the sequencer's writes).
 *   2. If the set of honorable capabilities changed (hotplug, or synth
 *      dying/recovering), re-run update_grab_state so a dead synth releases its
 *      hook grabs (fail open) and a recovered one re-grabs. Gating on an actual
 *      availability change -- rather than every tick -- avoids thrashing grabs. */
static void run_backend_maintenance(ksi_daemon_state *state)
{
    const ksi_platform_backend *backend;
    uint32_t previous_caps;
    uint32_t new_caps;

    if (state == NULL || (backend = state->backend) == NULL) {
        return;
    }

    if (backend->periodic_maintenance != NULL) {
        backend->periodic_maintenance();
    }

    if (backend->synth_needs_recovery != NULL
        && backend->synth_needs_recovery()
        && !output_queue_push_recreate_synth(&state->output_queue)) {
        fprintf(stderr, "inputd: failed to enqueue synthetic output device recovery\n");
    }

    previous_caps = state->available_capabilities;
    new_caps = daemon_available_capabilities(backend);
    state->available_capabilities = new_caps;

    if (new_caps != previous_caps && update_grab_state(state) != 0) {
        fprintf(stderr, "inputd: failed to refresh grabs after capability change\n");
    }
}

static bool check_idle_exit(
    const ksi_daemon_options *options,
    const ksi_daemon_state *state,
    uint64_t *idle_since_ms)
{
    uint64_t now;

    if (!options->system_service
        || state->client_count != 0
        || ksi_worker_pool_has_work(&g_worker_pool)
        || ksi_worker_pool_has_work(&g_identify_worker_pool)) {
        *idle_since_ms = 0u;
        return false;
    }

    now = monotonic_ms();

    if (*idle_since_ms == 0u) {
        *idle_since_ms = now;
        return false;
    }

    return now >= *idle_since_ms && now - *idle_since_ms >= KSI_IDLE_EXIT_MS;
}

int ksi_daemon_run(const ksi_daemon_options *options)
{
    ksi_ipc_server *server = NULL;
    const ksi_platform_backend *backend = ksi_platform_backend_get();
    ksi_permission_store *permissions = NULL;
    ksi_daemon_command_queue command_queue;

    if (options == NULL || (!options->system_service && options->socket_path == NULL)) {
        fprintf(stderr, "daemon options are invalid\n");
        return 2;
    }

    if (install_signal_handlers() != 0) {
        fprintf(stderr, "failed to install signal handlers: %s\n", strerror(errno));
        return 1;
    }

    /* Lock our pages resident. The hook lanes/sequencer run SCHED_FIFO, but that
     * does not prevent a major page fault from stalling a latency-critical thread
     * under memory pressure — and a stall here becomes system-wide keystroke lag
     * since all input is grabbed. Best-effort: the system service runs as root
     * (CAP_IPC_LOCK bypasses RLIMIT_MEMLOCK); a non-root/--foreground dev run may
     * lack the cap, so a failure is a note, not fatal. The resident set is small
     * and bounded (fixed lane/worker pools, ring buffers). */
    if (mlockall(MCL_CURRENT | MCL_FUTURE) != 0) {
        fprintf(stderr,
            "inputd: note: mlockall failed (%s); hook latency may degrade under "
            "memory pressure\n", strerror(errno));
    }

    if (backend->start() != 0) {
        fprintf(stderr, "failed to start %s input backend\n", backend->name);
        return 1;
    }

    /* Snapshot capabilities based on what the service opened successfully. */
    uint32_t available_capabilities = daemon_available_capabilities(backend);

    if (ksi_permissions_create(&permissions) != 0) {
        fprintf(stderr,
            "inputd: warning: failed to initialize permissions store; "
            "all capability requests will be denied\n");
        permissions = NULL;
    }

    if ((options->system_service
            ? ksi_ipc_server_from_fd(3, &server)
            : ksi_ipc_server_open(options->socket_path, &server)) != 0) {
        ksi_permissions_destroy(permissions);
        backend->stop();
        return 1;
    }

    fprintf(stderr, "keysharp-inputd listening on %s using %s backend\n",
        options->system_service ? "systemd socket" : options->socket_path,
        backend->name);

    ksi_daemon_state *daemon_state = calloc(1, sizeof(*daemon_state));

    if (daemon_state == NULL) {
        fprintf(stderr, "inputd: failed to allocate daemon state\n");
        ksi_ipc_server_close(server);
        ksi_permissions_destroy(permissions);
        backend->stop();
        return 1;
    }

    daemon_state->backend = backend;
    daemon_state->commands = &command_queue;
    daemon_state->permissions = permissions;
    daemon_state->available_capabilities = available_capabilities;
    daemon_state->next_connection_id = 1;
    daemon_state->next_event_id = 1;

    if (command_queue_init(&command_queue) != 0) {
        free(daemon_state);
        ksi_ipc_server_close(server);
        ksi_permissions_destroy(permissions);
        backend->stop();
        return 1;
    }

    if (output_queue_init(&daemon_state->output_queue) != 0) {
        command_queue_destroy(&command_queue);
        free(daemon_state);
        ksi_ipc_server_close(server);
        ksi_permissions_destroy(permissions);
        backend->stop();
        return 1;
    }

    if (synthetic_hook_queue_init(&daemon_state->synthetic_hook_queue) != 0) {
        output_queue_close(&daemon_state->output_queue);
        command_queue_destroy(&command_queue);
        free(daemon_state);
        ksi_ipc_server_close(server);
        ksi_permissions_destroy(permissions);
        backend->stop();
        return 1;
    }

    atomic_store(&daemon_state->synthetic_hook_inflight, 0);

    /* Sequencer thread must be running before the hook callback fires for the
     * first time so the queue always has a draining consumer. */
    atomic_store(&daemon_state->sequencer_running, 1);

    if (pthread_create(&daemon_state->sequencer_thread, NULL,
            output_sequencer_thread_main, daemon_state) != 0) {
        atomic_store(&daemon_state->sequencer_running, 0);
        synthetic_hook_queue_close(&daemon_state->synthetic_hook_queue);
        output_queue_close(&daemon_state->output_queue);
        command_queue_destroy(&command_queue);
        free(daemon_state);
        ksi_ipc_server_close(server);
        ksi_permissions_destroy(permissions);
        backend->stop();
        return 1;
    }

    daemon_state->sequencer_thread_started = true;

    /* Initialize and start both hook lanes before the backend can start
     * delivering events.  Errors here unwind the sequencer thread cleanly. */
    if (lane_init(&daemon_state->keyboard_lane, daemon_state, KSI_HOOK_KEYBOARD_LL) != 0
        || lane_init(&daemon_state->mouse_lane, daemon_state, KSI_HOOK_MOUSE_LL) != 0
        || lane_start(&daemon_state->keyboard_lane) != 0
        || lane_start(&daemon_state->mouse_lane) != 0) {
        fprintf(stderr, "inputd: failed to start hook lanes\n");
        (void)lane_shutdown(&daemon_state->keyboard_lane, 0u);
        (void)lane_shutdown(&daemon_state->mouse_lane, 0u);
        atomic_store(&daemon_state->sequencer_running, 0);
        output_queue_wake(&daemon_state->output_queue);
        pthread_join(daemon_state->sequencer_thread, NULL);
        daemon_state->sequencer_thread_started = false;
        synthetic_hook_queue_close(&daemon_state->synthetic_hook_queue);
        output_queue_close(&daemon_state->output_queue);
        command_queue_destroy(&command_queue);
        free(daemon_state);
        ksi_ipc_server_close(server);
        ksi_permissions_destroy(permissions);
        backend->stop();
        return 1;
    }

    if (backend->set_hook_event_callback != NULL) {
        backend->set_hook_event_callback(daemon_handle_hook_event, daemon_state);
    }

    if (ksi_worker_pool_init(&g_worker_pool) != 0
        || ksi_worker_pool_init(&g_identify_worker_pool) != 0) {
        fprintf(stderr, "inputd: failed to start worker pool\n");
        ksi_worker_pool_destroy(&g_worker_pool);
        ksi_worker_pool_destroy(&g_identify_worker_pool);

        if (backend->set_hook_event_callback != NULL) {
            backend->set_hook_event_callback(NULL, NULL);
        }

        (void)lane_shutdown(&daemon_state->keyboard_lane, 0u);
        (void)lane_shutdown(&daemon_state->mouse_lane, 0u);
        atomic_store(&daemon_state->sequencer_running, 0);
        output_queue_wake(&daemon_state->output_queue);
        pthread_join(daemon_state->sequencer_thread, NULL);
        daemon_state->sequencer_thread_started = false;
        synthetic_hook_queue_close(&daemon_state->synthetic_hook_queue);
        output_queue_close(&daemon_state->output_queue);
        command_queue_destroy(&command_queue);
        free(daemon_state);
        ksi_ipc_server_close(server);
        ksi_permissions_destroy(permissions);
        backend->stop();
        return 1;
    }

    /* Keep the grabbed-input producer at the same priority as lanes/sequencer. */
    set_realtime_priority("evdev reader");

    /* This thread also writes request/response frames to clients. Bound how long a
     * non-reading client can stall physical-input dispatch: tear its reply stream
     * down after ~10ms of backpressure instead of the default 100ms. Lane/worker
     * threads keep the full budget (their writes never gate device ingestion). */
    ksi_ipc_set_write_drain_budget_ms(10);

    uint64_t idle_since_ms = 0u;

    while (keep_running) {
        struct pollfd fds[KSI_MAX_POLL_FDS];
        nfds_t count = 0;
        nfds_t synthetic_index;
        nfds_t server_index;
        nfds_t backend_start;
        nfds_t backend_count;
        nfds_t client_start;
        nfds_t polled_client_count;

        memset(fds, 0, sizeof(fds));
        fds[count].fd = ksi_pipe_ring_wake_fd(&command_queue.ring);
        fds[count].events = POLLIN;
        count++;

        synthetic_index = count;
        fds[count].fd = daemon_state->synthetic_hook_queue.wake_read_fd;
        fds[count].events = POLLIN;
        count++;

        server_index = count;
        fds[count].fd = ksi_ipc_server_fd(server);
        fds[count].events = POLLIN;
        count++;

        backend_start = count;
        backend_count = backend->poll_fds == NULL
            ? 0
            : backend->poll_fds(&fds[count], KSI_MAX_POLL_FDS - count);
        count += backend_count;

        client_start = count;
        polled_client_count = daemon_state->client_count;

        for (nfds_t i = 0; i < polled_client_count; i++) {
            fds[count].fd = daemon_state->clients[i].fd;
            fds[count].events = POLLIN;
            count++;
        }

        /* Lane threads own hook-decision deadlines. */
        int poll_result = poll(fds, count, 1000);

        if (poll_result < 0) {
            if (errno == EINTR) {
                continue;
            }

            fprintf(stderr, "poll failed: %s\n", strerror(errno));
            break;
        }

        if (poll_result == 0) {
            (void)process_hook_ingress(daemon_state);
            apply_fail_open_if_requested(daemon_state);
            process_daemon_commands(daemon_state);
            expire_client_leases(daemon_state);
            expire_unauthenticated_clients(daemon_state);
            run_backend_maintenance(daemon_state);

            if (check_idle_exit(options, daemon_state, &idle_since_ms)) {
                keep_running = 0;
            }

            continue;
        }

        if ((fds[0].revents & POLLIN) != 0) {
            ksi_pipe_ring_drain_wake(&command_queue.ring);
        }

        if ((fds[synthetic_index].revents & POLLIN) != 0) {
            ksi_wake_pipe_drain(daemon_state->synthetic_hook_queue.wake_read_fd);
        }

        if ((fds[server_index].revents & POLLIN) != 0) {
            for (size_t accepted = 0; accepted < KSI_MAX_CLIENTS
                && accept_client(daemon_state, server, options->system_service);
                accepted++) {
            }
        }

        for (nfds_t i = polled_client_count; i > 0; i--) {
            nfds_t index = i - 1u;
            short revents = fds[client_start + index].revents;

            if ((revents & (POLLHUP | POLLERR | POLLNVAL)) != 0
                || ((revents & POLLIN) != 0
                    && read_client_frames_direct(daemon_state, &daemon_state->clients[index]) <= 0)) {
                remove_client(daemon_state, index);
            }
        }

        (void)process_hook_ingress(daemon_state);

        for (nfds_t i = backend_start; i < backend_start + backend_count; i++) {
            if ((fds[i].revents & (POLLIN | POLLHUP | POLLERR | POLLNVAL)) != 0
                && backend->process_fd != NULL) {
                backend->process_fd(fds[i].fd);
            }
        }

        (void)process_hook_ingress(daemon_state);

        /* Runs backend maintenance, requests synth recovery on the sequencer if
         * needed, refreshes available_capabilities (hotplug can change physical
         * hook/block capabilities) and re-applies grabs when availability changes. */
        run_backend_maintenance(daemon_state);

        apply_fail_open_if_requested(daemon_state);
        process_daemon_commands(daemon_state);
        expire_client_leases(daemon_state);
        expire_unauthenticated_clients(daemon_state);

        if (options->system_service && daemon_state->client_count == 0
            && !ksi_worker_pool_has_work(&g_worker_pool)
            && !ksi_worker_pool_has_work(&g_identify_worker_pool)) {
            uint64_t now = monotonic_ms();

            if (idle_since_ms == 0u) {
                idle_since_ms = now;
            } else if (now >= idle_since_ms && now - idle_since_ms >= KSI_IDLE_EXIT_MS) {
                keep_running = 0;
            }
        } else {
            idle_since_ms = 0u;
        }
    }

    keep_running = 0;

    if (backend->set_hook_event_callback != NULL) {
        backend->set_hook_event_callback(NULL, NULL);
    }

    /* Release grabs before any shutdown step that may block. */
    clear_hook_state(daemon_state);

    if (update_grab_state(daemon_state) != 0) {
        fprintf(stderr, "inputd: failed to release grabs before shutdown waits\n");
    }

    uint64_t shutdown_deadline_ms = monotonic_ms() + KSI_SHUTDOWN_TIMEOUT_MS;

    /* Queued physical events finalize as PASS during lane shutdown. */
    if (!lane_shutdown(&daemon_state->keyboard_lane, shutdown_deadline_ms)
        || !lane_shutdown(&daemon_state->mouse_lane, shutdown_deadline_ms)) {
        fprintf(stderr, "inputd: shutdown deadline exceeded waiting for hook lanes; terminating\n");
        fflush(NULL);
        _exit(EXIT_FAILURE);
    }

    /* Abort in-progress prompts so worker threads exit promptly. */
    ksi_permissions_cancel();
    ksi_worker_pool_request_stop(&g_worker_pool);
    ksi_worker_pool_request_stop(&g_identify_worker_pool);

    /* Wake the main loop so it observes keep_running=0. */
    ksi_pipe_ring_wake(&command_queue.ring);

    if (!ksi_worker_pool_join_before(&g_worker_pool, shutdown_deadline_ms)
        || !ksi_worker_pool_join_before(&g_identify_worker_pool, shutdown_deadline_ms)) {
        fprintf(stderr, "inputd: shutdown deadline exceeded waiting for worker pool; terminating\n");
        fflush(NULL);
        _exit(EXIT_FAILURE);
    }

    ksi_worker_pool_destroy(&g_worker_pool);
    ksi_worker_pool_destroy(&g_identify_worker_pool);

    while (daemon_state->client_count > 0u) {
        remove_client(daemon_state, daemon_state->client_count - 1u);
    }

    /* Drain final replay/synth output before tearing down the backend. */
    if (daemon_state->sequencer_thread_started) {
        atomic_store(&daemon_state->sequencer_running, 0);
        output_queue_wake(&daemon_state->output_queue);

        if (!join_thread_before(daemon_state->sequencer_thread, shutdown_deadline_ms)) {
            fprintf(stderr, "inputd: shutdown deadline exceeded waiting for output sequencer; terminating\n");
            fflush(NULL);
            _exit(EXIT_FAILURE);
        }

        daemon_state->sequencer_thread_started = false;
    }

    synthetic_hook_queue_close(&daemon_state->synthetic_hook_queue);
    output_queue_close(&daemon_state->output_queue);
    command_queue_destroy(&command_queue);
    free(daemon_state);
    ksi_ipc_server_close(server);
    ksi_permissions_destroy(permissions);
    backend->stop();

    return 0;
}
