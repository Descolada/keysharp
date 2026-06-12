#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif

#include "keysharp_inputd/daemon.h"

#include "connection_ref.h"
#include "keysharp_inputd/ipc.h"
#include "keysharp_inputd/linux_devices.h"
#include "keysharp_trust/permissions.h"
#include "keysharp_inputd/platform.h"
#include "keysharp_inputd/protocol.h"
#include "pipe_ring.h"
#include "worker_pool.h"

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <stdbool.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sched.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

_Static_assert(KSI_CAP_HOOK_KEYBOARD == KST_CAP_INPUT_HOOK_KEYBOARD, "trust/input capability mismatch");
_Static_assert(KSI_CAP_HOOK_MOUSE == KST_CAP_INPUT_HOOK_MOUSE, "trust/input capability mismatch");
_Static_assert(KSI_CAP_SYNTH_KEYBOARD == KST_CAP_INPUT_SYNTH_KEYBOARD, "trust/input capability mismatch");
_Static_assert(KSI_CAP_SYNTH_MOUSE == KST_CAP_INPUT_SYNTH_MOUSE, "trust/input capability mismatch");
_Static_assert(KSI_CAP_BLOCK_INPUT == KST_CAP_INPUT_BLOCK, "trust/input capability mismatch");

#define KSI_MAX_CLIENTS 64
#define KSI_MAX_BACKEND_FDS 160
#define KSI_MAX_POLL_FDS (2 + KSI_MAX_BACKEND_FDS + KSI_MAX_CLIENTS)
#define KSI_MAX_PENDING_COMMANDS 256
#define KSI_MAX_MODIFY_INPUTS 32
#define KSI_MAX_SYNTH_INPUTS 1024
#define KSI_HOOK_DECISION_TIMEOUT_MS 1000u
#define KSI_MAX_CONSECUTIVE_HOOK_FAILURES 1u
#define KSI_MAX_LANE_ACTIONS 512u
#define KSI_MAX_OUTPUT_ACTIONS 4096u
#define KSI_MAX_NONCRITICAL_OUTPUT_ACTIONS 4032u
#define KSI_MAX_CLIENT_OUTPUT_ACTIONS 3840u
#define KSI_MAX_OUTPUT_SYNTH_BYTES (1024u * 1024u)
#define KSI_MAX_CLIENT_OUTPUT_SYNTH_BYTES (960u * 1024u)
#define KSI_SHUTDOWN_TIMEOUT_MS 5000u
#define KSI_GRAB_LEASE_TIMEOUT_MS 15000u
#define KSI_IDLE_EXIT_MS 30000u
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

/* Writes a single wake byte to a self-pipe/eventfd. The pipe's buffer is the
 * signal — readers drain it eagerly, so EAGAIN/EPIPE failures here are
 * harmless. The assignment is what actually consumes glibc's
 * warn_unused_result attribute (a bare (void) cast does not). */
static inline void wake_pipe_write(int fd)
{
    uint8_t byte = 1;
    ssize_t written = write(fd, &byte, sizeof(byte));
    (void)written;
}

static ksi_worker_pool g_worker_pool;

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
    ksi_client_state state;
    bool identity_attempted;
    bool has_identity;
    bool authenticated;
    uint32_t granted_capabilities;
    uint32_t hook_subscriptions;
    uint32_t block_input_mask;
    uint32_t consecutive_hook_failures;
    uint64_t lease_expires_ms;
    char exe_path[KSI_PERMISSION_MAX_PATH];
    char command_line[KSI_PERMISSION_MAX_COMMAND_LINE];
    char exe_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
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
        } prompt_done;
        struct {
            char reason[32];
        } hook_failure;
    } data;
} ksi_daemon_command;

static void free_daemon_command(ksi_daemon_command *command);

/* --- Typed queues built on ksi_pipe_ring --- */

/* Forward command queue: IPC thread (and worker threads) → main thread. */
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

static bool command_queue_push(ksi_daemon_command_queue *q, const ksi_daemon_command *cmd)
{
    return q != NULL && ksi_pipe_ring_push(&q->ring, cmd);
}

static bool command_queue_pop(ksi_daemon_command_queue *q, ksi_daemon_command *cmd)
{
    return q != NULL && ksi_pipe_ring_pop(&q->ring, cmd);
}

static void command_queue_wake(const ksi_daemon_command_queue *q)
{
    if (q != NULL) ksi_pipe_ring_wake(&q->ring);
}

static void command_queue_drain_wake(const ksi_daemon_command_queue *q)
{
    if (q != NULL) ksi_pipe_ring_drain_wake(&q->ring);
}

/* Forward decls for types embedded in ksi_daemon_state.  Definitions and
 * helper functions live later in the file, alongside the sequencer thread. */
typedef enum ksi_output_action_type {
    KSI_OUTPUT_ACTION_REPLAY = 0,
    KSI_OUTPUT_ACTION_SYNTH,
} ksi_output_action_type;

typedef struct ksi_output_action {
    ksi_output_action_type type;
    uint32_t hook_type;
    ksi_hook_event_payload replay_payload;
    size_t replay_payload_size;
    uint32_t synth_flags;
    uint32_t synth_count;
    ksi_input *synth_inputs;
} ksi_output_action;

/* Output queue is an intrusive linked list with mutex + wake pipe. Admission
 * is bounded by action count and synthesis bytes; client batches are accepted
 * atomically or rejected, with capacity reserved for the emergency chord. */
typedef struct ksi_output_node {
    ksi_output_action action;
    struct ksi_output_node *next;
} ksi_output_node;

typedef struct ksi_output_queue {
    pthread_mutex_t mutex;
    bool mutex_initialized;
    ksi_output_node *head;
    ksi_output_node *tail;
    size_t count;
    size_t synth_bytes;
    int wake_read_fd;
    int wake_write_fd;
} ksi_output_queue;

/* Snapshot of one hook event handed off from the main thread to a lane thread.
 * The subscriber list is captured at enqueue time so the lane never needs to
 * touch state->clients[] while iterating. */
typedef struct ksi_lane_event {
    uint64_t event_id;
    uint32_t hook_type;
    uint32_t generation;
    bool is_injected;
    ksi_hook_event_payload payload;
    size_t payload_size;
    int subscriber_response_fds[KSI_MAX_CLIENTS];
    ksi_hook_send_ref *subscriber_send_refs[KSI_MAX_CLIENTS];
    uint64_t subscriber_connection_ids[KSI_MAX_CLIENTS];
    uint32_t subscriber_granted_caps[KSI_MAX_CLIENTS];
    size_t subscriber_count;
} ksi_lane_event;

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

/* Lane action queues are capped linked lists.  If a stalled subscriber lets a
 * lane fall too far behind, new physical events bypass hook delivery and are
 * replayed so daemon memory/fd use remains bounded. */
typedef struct ksi_lane_action_node {
    ksi_lane_event *event;
    struct ksi_lane_action_node *next;
} ksi_lane_action_node;

typedef struct ksi_lane_action_queue {
    pthread_mutex_t mutex;
    bool mutex_initialized;
    ksi_lane_action_node *head;
    ksi_lane_action_node *tail;
    size_t count;
    int wake_read_fd;
    int wake_write_fd;
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
    /* Set to 1 by lane_shutdown so the lane bails out of its decision wait
     * loop and any remaining subscribers, instead of paying KSI_HOOK_DECISION_
     * TIMEOUT_MS per subscriber per queued event during shutdown. */
    atomic_int shutting_down;
    /* Incremented by EmergencyPassthrough. Events captured under an older
     * generation skip subscriber callbacks and finalize immediately. */
    atomic_uint flush_generation;
    ksi_lane_action_queue action_queue;
    ksi_lane_decision_queue decision_queue;
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
    /* Output sequencer: thread + queue that funnels every uinput write (PASS
     * replay, MODIFY synthesis, SYNTHESIZE_INPUT) onto a single dedicated
     * thread.  See output_queue helpers above for ordering semantics. */
    ksi_output_queue output_queue;
    pthread_t sequencer_thread;
    bool sequencer_thread_started;
    atomic_int sequencer_running;
    /* Two independent hook lanes so a stalled keyboard hook callback can't
     * back up mouse events (and vice versa).  Per-lane FIFO ordering, no
     * cross-lane ordering. */
    ksi_hook_lane kbd_lane;
    ksi_hook_lane mouse_lane;
} ksi_daemon_state;

typedef struct ksi_binary_message_view {
    const ksi_message_header *header;
    const uint8_t *payload;
    size_t payload_size;
} ksi_binary_message_view;

static int update_grab_state(ksi_daemon_state *state);
static void clear_hook_state(ksi_daemon_state *state);
static void record_client_hook_failure(ksi_daemon_state *state, nfds_t index, const char *reason);
static void send_status(
    int client_fd,
    const ksi_message_header *request,
    uint32_t response_type,
    int32_t status,
    uint32_t detail);
static void remove_client(ksi_daemon_state *state, nfds_t index);
static void send_indicator_state_result(int client_fd, const ksi_message_header *request);
static void send_pointer_position_result(int client_fd, const ksi_message_header *request);
static void handle_get_key_state(ksi_client *client, const ksi_binary_message_view *message);
static void set_realtime_priority(const char *thread_name);
static ssize_t find_client_index_by_fd(const ksi_daemon_state *state, int client_fd);
static ssize_t find_client_index_by_connection(
    const ksi_daemon_state *state,
    int client_fd,
    uint64_t connection_id);
static void process_client_identified(ksi_daemon_state *state, ksi_daemon_command *command);
static void process_client_prompt_done(ksi_daemon_state *state, ksi_daemon_command *command);

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
#include "daemon/protocol_server.inc"

static bool check_idle_exit(
    const ksi_daemon_options *options,
    const ksi_daemon_state *state,
    uint64_t *idle_since_ms)
{
    uint64_t now;

    if (!options->system_service
        || state->client_count != 0
        || ksi_worker_pool_has_work(&g_worker_pool)) {
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

    /* Sequencer thread must be running before the hook callback fires for the
     * first time so the queue always has a draining consumer. */
    atomic_store(&daemon_state->sequencer_running, 1);

    if (pthread_create(&daemon_state->sequencer_thread, NULL,
            output_sequencer_thread_main, daemon_state) != 0) {
        atomic_store(&daemon_state->sequencer_running, 0);
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
    if (lane_init(&daemon_state->kbd_lane, daemon_state, KSI_HOOK_KEYBOARD_LL) != 0
        || lane_init(&daemon_state->mouse_lane, daemon_state, KSI_HOOK_MOUSE_LL) != 0
        || lane_start(&daemon_state->kbd_lane) != 0
        || lane_start(&daemon_state->mouse_lane) != 0) {
        fprintf(stderr, "inputd: failed to start hook lanes\n");
        (void)lane_shutdown(&daemon_state->kbd_lane, 0u);
        (void)lane_shutdown(&daemon_state->mouse_lane, 0u);
        atomic_store(&daemon_state->sequencer_running, 0);
        output_queue_wake(&daemon_state->output_queue);
        pthread_join(daemon_state->sequencer_thread, NULL);
        daemon_state->sequencer_thread_started = false;
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

    if (ksi_worker_pool_init(&g_worker_pool) != 0) {
        fprintf(stderr, "inputd: failed to start worker pool\n");

        if (backend->set_hook_event_callback != NULL) {
            backend->set_hook_event_callback(NULL, NULL);
        }

        (void)lane_shutdown(&daemon_state->kbd_lane, 0u);
        (void)lane_shutdown(&daemon_state->mouse_lane, 0u);
        atomic_store(&daemon_state->sequencer_running, 0);
        output_queue_wake(&daemon_state->output_queue);
        pthread_join(daemon_state->sequencer_thread, NULL);
        daemon_state->sequencer_thread_started = false;
        output_queue_close(&daemon_state->output_queue);
        command_queue_destroy(&command_queue);
        free(daemon_state);
        ksi_ipc_server_close(server);
        ksi_permissions_destroy(permissions);
        backend->stop();
        return 1;
    }

    /* The main thread is the evdev reader: for grabbed devices, every physical
     * event must travel main-thread -> lane -> sequencer -> uinput before it
     * reaches the display server.  The lane and sequencer threads already run
     * at SCHED_FIFO priority 1, so leaving this producing hop at SCHED_OTHER
     * makes it the lowest-priority stage in the pipeline: under CPU load it can
     * be starved while its real-time consumers sit idle, stalling grabbed input.
     * Elevate it to the same priority so the whole replay path is real-time. */
    set_realtime_priority("evdev reader");

    uint64_t idle_since_ms = 0u;

    while (keep_running) {
        struct pollfd fds[KSI_MAX_POLL_FDS];
        nfds_t count = 0;
        nfds_t server_index;
        nfds_t backend_start;
        nfds_t backend_count;
        nfds_t client_start;
        nfds_t polled_client_count;

        memset(fds, 0, sizeof(fds));
        fds[count].fd = ksi_pipe_ring_wake_fd(&command_queue.ring);
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

        /* Lanes own their own decision-timeout deadlines now, so the main
         * loop just blocks until evdev or the command queue has work. */
        int poll_result = poll(fds, count, 1000);

        if (poll_result < 0) {
            if (errno == EINTR) {
                continue;
            }

            fprintf(stderr, "poll failed: %s\n", strerror(errno));
            break;
        }

        if (poll_result == 0) {
            apply_fail_open_if_requested(daemon_state);
            process_daemon_commands(daemon_state);
            expire_client_leases(daemon_state);

            if (check_idle_exit(options, daemon_state, &idle_since_ms)) {
                keep_running = 0;
            }

            continue;
        }

        if ((fds[0].revents & POLLIN) != 0) {
            command_queue_drain_wake(&command_queue);
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

        for (nfds_t i = backend_start; i < backend_start + backend_count; i++) {
            if ((fds[i].revents & (POLLIN | POLLHUP | POLLERR | POLLNVAL)) != 0
                && backend->process_fd != NULL) {
                backend->process_fd(fds[i].fd);
            }
        }

        apply_fail_open_if_requested(daemon_state);
        process_daemon_commands(daemon_state);
        expire_client_leases(daemon_state);

        if (options->system_service && daemon_state->client_count == 0
            && !ksi_worker_pool_has_work(&g_worker_pool)) {
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

    /* Release all physical grabs before joining or waiting on any thread.
     * Later shutdown work may block, but it must never keep user input held. */
    clear_hook_state(daemon_state);

    if (update_grab_state(daemon_state) != 0) {
        fprintf(stderr, "inputd: failed to release grabs before shutdown waits\n");
    }

    uint64_t shutdown_deadline_ms = monotonic_ms() + KSI_SHUTDOWN_TIMEOUT_MS;

    /* Shut down both hook lanes.  lane_shutdown sets a flag the lane threads
     * observe to bail out of any pending decision wait, then pushes a NULL
     * sentinel.  Anything still queued is finalized as PASS so physical events
     * reach the sequencer for replay rather than being lost. */
    if (!lane_shutdown(&daemon_state->kbd_lane, shutdown_deadline_ms)
        || !lane_shutdown(&daemon_state->mouse_lane, shutdown_deadline_ms)) {
        fprintf(stderr, "inputd: shutdown deadline exceeded waiting for hook lanes; terminating\n");
        fflush(NULL);
        _exit(EXIT_FAILURE);
    }

    /* Signal any in-progress permission prompts to abort so their worker
     * threads finish promptly rather than waiting up to 60 seconds. */
    ksi_permissions_cancel();
    ksi_worker_pool_request_stop(&g_worker_pool);

    /* Wake the main loop so it observes keep_running=0. */
    command_queue_wake(&command_queue);

    if (!ksi_worker_pool_join_before(&g_worker_pool, shutdown_deadline_ms)) {
        fprintf(stderr, "inputd: shutdown deadline exceeded waiting for worker pool; terminating\n");
        fflush(NULL);
        _exit(EXIT_FAILURE);
    }

    ksi_worker_pool_destroy(&g_worker_pool);

    while (daemon_state->client_count > 0u) {
        remove_client(daemon_state, daemon_state->client_count - 1u);
    }

    /* Stop and drain the output sequencer.  The shutdown replay above queued
     * the last batch of physical events through it, so we wait for the thread
     * to drain them before tearing down the backend. */
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

    output_queue_close(&daemon_state->output_queue);
    command_queue_destroy(&command_queue);
    free(daemon_state);
    ksi_ipc_server_close(server);
    ksi_permissions_destroy(permissions);
    backend->stop();

    return 0;
}
