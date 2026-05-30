#include "keysharp_inputd/daemon.h"

#include "keysharp_inputd/ipc.h"
#include "keysharp_inputd/linux_devices.h"
#include "keysharp_trust/permissions.h"
#include "keysharp_inputd/platform.h"
#include "keysharp_inputd/protocol.h"

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
#include <time.h>
#include <unistd.h>

_Static_assert(KSI_CAP_HOOK_KEYBOARD == KST_CAP_INPUT_HOOK_KEYBOARD, "trust/input capability mismatch");
_Static_assert(KSI_CAP_HOOK_MOUSE == KST_CAP_INPUT_HOOK_MOUSE, "trust/input capability mismatch");
_Static_assert(KSI_CAP_SYNTH_KEYBOARD == KST_CAP_INPUT_SYNTH_KEYBOARD, "trust/input capability mismatch");
_Static_assert(KSI_CAP_SYNTH_MOUSE == KST_CAP_INPUT_SYNTH_MOUSE, "trust/input capability mismatch");
_Static_assert(KSI_CAP_BLOCK_INPUT == KST_CAP_INPUT_BLOCK, "trust/input capability mismatch");

#define KSI_MAX_CLIENTS 64
#define KSI_MAX_BACKEND_FDS 160
#define KSI_MAX_POLL_FDS (1 + KSI_MAX_BACKEND_FDS + KSI_MAX_CLIENTS)
#define KSI_MAX_PENDING_COMMANDS 256
#define KSI_MAX_MODIFY_INPUTS 32
#define KSI_MAX_SYNTH_INPUTS 1024
#define KSI_HOOK_DECISION_TIMEOUT_MS 1000u
#define KSI_MAX_CONSECUTIVE_HOOK_FAILURES 10u
#define KSI_MAX_LANE_ACTIONS 512u
#define KSI_IDLE_EXIT_MS 30000u

static volatile sig_atomic_t keep_running = 1;

/* Counts identify_worker and prompt_worker threads currently running.
 * The shutdown path waits for this to reach zero before destroying the
 * command queue those threads hold a pointer to. */
static atomic_int g_worker_threads_running = 0;

/* Serializes every IPC send so lane threads and the main thread cannot
 * interleave bytes inside a single client's Unix stream.  Acquired around
 * ksi_ipc_send_framed_message and around client-fd close operations to
 * prevent send-after-close from racing across threads. */
static pthread_mutex_t g_ipc_send_mutex = PTHREAD_MUTEX_INITIALIZER;

static int ipc_send_locked(
    int client_fd,
    uint32_t message_type,
    uint32_t client_id,
    uint64_t correlation_id,
    const void *payload,
    size_t payload_size)
{
    int rc;

    pthread_mutex_lock(&g_ipc_send_mutex);
    rc = ksi_ipc_send_framed_message(
        client_fd, message_type, client_id, correlation_id, payload, payload_size);
    pthread_mutex_unlock(&g_ipc_send_mutex);
    return rc;
}

static void ipc_close_locked(int client_fd)
{
    pthread_mutex_lock(&g_ipc_send_mutex);
    ksi_ipc_close_client(client_fd);
    pthread_mutex_unlock(&g_ipc_send_mutex);
}

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

typedef struct ksi_hook_send_ref {
    int fd;
    atomic_uint ref_count;
} ksi_hook_send_ref;

static ksi_hook_send_ref *hook_send_ref_create(int client_fd)
{
    ksi_hook_send_ref *ref;
    int send_fd;

    send_fd = dup(client_fd);

    if (send_fd < 0) {
        return NULL;
    }

    ref = malloc(sizeof(*ref));

    if (ref == NULL) {
        ipc_close_locked(send_fd);
        return NULL;
    }

    ref->fd = send_fd;
    atomic_init(&ref->ref_count, 1u);
    return ref;
}

static bool hook_send_ref_acquire(ksi_hook_send_ref *ref)
{
    if (ref == NULL) {
        return false;
    }

    (void)atomic_fetch_add(&ref->ref_count, 1u);
    return true;
}

static void hook_send_ref_release(ksi_hook_send_ref *ref)
{
    if (ref == NULL) {
        return;
    }

    if (atomic_fetch_sub(&ref->ref_count, 1u) == 1u) {
        ipc_close_locked(ref->fd);
        free(ref);
    }
}

/* IPC-thread-only: holds raw socket I/O state for one connection. */
typedef struct ksi_ipc_slot {
    int fd;
    uint8_t rx_buffer[KSI_MAX_MESSAGE_SIZE];
    size_t rx_used;
} ksi_ipc_slot;

typedef enum ksi_client_state {
    KSI_CLIENT_STATE_IDENTIFYING,     /* process identity resolution running on worker thread */
    KSI_CLIENT_STATE_READY,           /* identity known, waiting for or able to process CLIENT_HELLO */
    KSI_CLIENT_STATE_AWAITING_PROMPT, /* permission prompt running on worker thread */
} ksi_client_state;

/* Main-thread-only: holds application state for one authenticated client. */
typedef struct ksi_client {
    int fd;
    ksi_hook_send_ref *hook_send_ref;
    uint64_t connection_id;
    pid_t pid;
    uid_t uid;
    gid_t gid;
    ksi_client_state state;
    bool has_identity;
    bool authenticated;
    uint32_t granted_capabilities;
    uint32_t hook_subscriptions;
    uint32_t block_input_mask;
    uint32_t consecutive_hook_failures;
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
} ksi_client_identified_result;

typedef enum ksi_daemon_command_type {
    KSI_DAEMON_COMMAND_CLIENT_CONNECTED,
    KSI_DAEMON_COMMAND_CLIENT_FRAME,
    KSI_DAEMON_COMMAND_CLIENT_DISCONNECTED,
    KSI_DAEMON_COMMAND_CLIENT_IDENTIFIED, /* worker -> main: identity resolution complete */
    KSI_DAEMON_COMMAND_CLIENT_PROMPT_DONE, /* worker → main: user permission prompt complete */
    KSI_DAEMON_COMMAND_LANE_HOOK_FAILURE, /* lane → main: send/timeout failure for a client */
} ksi_daemon_command_type;

typedef struct ksi_daemon_command {
    ksi_daemon_command_type type;
    int client_fd;
    uint64_t connection_id;
    ksi_ipc_peer_credentials credentials;
    union {
        struct {
            uint8_t *data;
            size_t size;
        } frame;
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

/* Generic pipe-backed ring buffer.  Element bytes are stored inline; the
 * caller provides the element size and capacity at init time.  A non-blocking
 * pipe is used to wake a polling thread when items are available. */
typedef struct ksi_pipe_ring {
    pthread_mutex_t mutex;
    bool mutex_initialized;
    uint8_t *buffer;    /* heap-allocated: element_size * capacity bytes */
    size_t element_size;
    size_t capacity;
    size_t head;
    size_t count;
    int wake_read_fd;
    int wake_write_fd;
} ksi_pipe_ring;

static int ksi_pipe_ring_init(ksi_pipe_ring *ring, size_t element_size, size_t capacity)
{
    int pipe_fds[2];

    if (ring == NULL || element_size == 0 || capacity == 0) {
        return -1;
    }

    memset(ring, 0, sizeof(*ring));
    ring->wake_read_fd = -1;
    ring->wake_write_fd = -1;

    ring->buffer = calloc(capacity, element_size);

    if (ring->buffer == NULL) {
        return -1;
    }

    if (pthread_mutex_init(&ring->mutex, NULL) != 0) {
        free(ring->buffer);
        ring->buffer = NULL;
        return -1;
    }

    ring->mutex_initialized = true;

    if (pipe(pipe_fds) != 0) {
        pthread_mutex_destroy(&ring->mutex);
        ring->mutex_initialized = false;
        free(ring->buffer);
        ring->buffer = NULL;
        return -1;
    }

    ring->element_size = element_size;
    ring->capacity = capacity;
    ring->wake_read_fd = pipe_fds[0];
    ring->wake_write_fd = pipe_fds[1];

    {
        int fl;
        fl = fcntl(ring->wake_read_fd, F_GETFL);
        if (fl >= 0) (void)fcntl(ring->wake_read_fd, F_SETFL, fl | O_NONBLOCK);
        fl = fcntl(ring->wake_write_fd, F_GETFL);
        if (fl >= 0) (void)fcntl(ring->wake_write_fd, F_SETFL, fl | O_NONBLOCK);
    }

    return 0;
}

/* Closes the pipe fds and destroys the mutex. Does NOT free remaining items;
 * callers with heap-pointer elements must drain first. */
static void ksi_pipe_ring_close(ksi_pipe_ring *ring)
{
    if (ring == NULL) {
        return;
    }

    if (ring->wake_read_fd >= 0) {
        close(ring->wake_read_fd);
        ring->wake_read_fd = -1;
    }

    if (ring->wake_write_fd >= 0) {
        close(ring->wake_write_fd);
        ring->wake_write_fd = -1;
    }

    free(ring->buffer);
    ring->buffer = NULL;

    if (ring->mutex_initialized) {
        pthread_mutex_destroy(&ring->mutex);
        ring->mutex_initialized = false;
    }
}

static bool ksi_pipe_ring_push(ksi_pipe_ring *ring, const void *item)
{
    bool pushed = false;

    if (ring == NULL || item == NULL || !ring->mutex_initialized) {
        return false;
    }

    pthread_mutex_lock(&ring->mutex);

    if (ring->count < ring->capacity) {
        size_t tail = (ring->head + ring->count) % ring->capacity;
        memcpy(ring->buffer + tail * ring->element_size, item, ring->element_size);
        ring->count++;
        pushed = true;
    }

    pthread_mutex_unlock(&ring->mutex);

    if (pushed) {
        wake_pipe_write(ring->wake_write_fd);
    }

    return pushed;
}

static bool ksi_pipe_ring_pop(ksi_pipe_ring *ring, void *item)
{
    if (ring == NULL || item == NULL || !ring->mutex_initialized) {
        return false;
    }

    pthread_mutex_lock(&ring->mutex);

    if (ring->count == 0) {
        pthread_mutex_unlock(&ring->mutex);
        return false;
    }

    memcpy(item, ring->buffer + ring->head * ring->element_size, ring->element_size);
    memset(ring->buffer + ring->head * ring->element_size, 0, ring->element_size);
    ring->head = (ring->head + 1) % ring->capacity;
    ring->count--;

    pthread_mutex_unlock(&ring->mutex);
    return true;
}

static void ksi_pipe_ring_wake(const ksi_pipe_ring *ring)
{
    if (ring == NULL || ring->wake_write_fd < 0) {
        return;
    }

    wake_pipe_write(ring->wake_write_fd);
}

static void ksi_pipe_ring_drain_wake(const ksi_pipe_ring *ring)
{
    uint8_t buffer[64];

    if (ring == NULL || ring->wake_read_fd < 0) {
        return;
    }

    while (read(ring->wake_read_fd, buffer, sizeof(buffer)) > 0) {
    }
}

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

/* Reverse command queue: main thread → IPC thread. */
#define KSI_MAX_PENDING_IPC_COMMANDS 128

typedef enum ksi_ipc_command_type {
    KSI_IPC_COMMAND_CLOSE_CLIENT,
} ksi_ipc_command_type;

typedef struct ksi_ipc_command {
    ksi_ipc_command_type type;
    int client_fd;
} ksi_ipc_command;

typedef struct ksi_ipc_command_queue { ksi_pipe_ring ring; } ksi_ipc_command_queue;

static int ipc_command_queue_init(ksi_ipc_command_queue *q)
{
    return q == NULL ? -1
        : ksi_pipe_ring_init(&q->ring, sizeof(ksi_ipc_command), KSI_MAX_PENDING_IPC_COMMANDS);
}

static void ipc_command_queue_destroy(ksi_ipc_command_queue *q)
{
    if (q != NULL) ksi_pipe_ring_close(&q->ring);
}

static bool ipc_command_queue_push(ksi_ipc_command_queue *q, const ksi_ipc_command *cmd)
{
    return q != NULL && ksi_pipe_ring_push(&q->ring, cmd);
}

static bool ipc_command_queue_pop(ksi_ipc_command_queue *q, ksi_ipc_command *cmd)
{
    return q != NULL && ksi_pipe_ring_pop(&q->ring, cmd);
}

static void ipc_command_queue_drain_wake(const ksi_ipc_command_queue *q)
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

/* Output queue is an intrusive linked list with mutex + wake pipe so it is
 * unbounded (except by available RAM) but still pollable.  A bounded ring
 * would be wrong here: a SendInput burst is one logical operation from the
 * script's perspective and must not be partially dropped.  Lock-free MPSC
 * was considered but rejected — the mutex is held for ~10 instructions per
 * push and uinput write latency dominates by orders of magnitude. */
typedef struct ksi_output_node {
    ksi_output_action action;
    struct ksi_output_node *next;
} ksi_output_node;

typedef struct ksi_output_queue {
    pthread_mutex_t mutex;
    bool mutex_initialized;
    ksi_output_node *head;
    ksi_output_node *tail;
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

/* Lane decision queue is also a linked list so the M1 ordering fix can hold
 * g_ipc_send_mutex (not the queue mutex) through the ack write, and so push
 * cannot fail under load. */
typedef struct ksi_lane_decision_node {
    ksi_lane_decision *decision;
    struct ksi_lane_decision_node *next;
} ksi_lane_decision_node;

typedef struct ksi_lane_decision_queue {
    pthread_mutex_t mutex;
    bool mutex_initialized;
    ksi_lane_decision_node *head;
    ksi_lane_decision_node *tail;
    int wake_read_fd;
    int wake_write_fd;
} ksi_lane_decision_queue;

typedef struct ksi_hook_lane {
    uint32_t hook_type;
    pthread_t thread;
    bool thread_started;
    atomic_uint_least64_t current_event_id;  /* 0 = idle */
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
    ksi_ipc_command_queue *reverse_commands;
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

typedef struct ksi_ipc_thread_context {
    ksi_ipc_server *server;
    ksi_daemon_command_queue *commands;
    bool system_service;
    ksi_ipc_command_queue *reverse_commands;
} ksi_ipc_thread_context;

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

    if (command->type == KSI_DAEMON_COMMAND_CLIENT_FRAME) {
        free(command->data.frame.data);
        command->data.frame.data = NULL;
        command->data.frame.size = 0;
    } else if (command->type == KSI_DAEMON_COMMAND_CLIENT_IDENTIFIED) {
        free(command->data.identified.result);
        command->data.identified.result = NULL;
    }
}


/* --- Async worker threads for identification and permission prompting --- */

typedef struct ksi_identify_task {
    ksi_daemon_command_queue *commands;
    pid_t pid;
    int client_fd;
    uint64_t connection_id;
} ksi_identify_task;

typedef struct ksi_prompt_task {
    ksi_daemon_command_queue *commands;
    int client_fd;
    uint64_t connection_id;
    uint32_t requested_capabilities;
    uint32_t missing_capabilities;
    char exe_path[KSI_PERMISSION_MAX_PATH];
    char command_line[KSI_PERMISSION_MAX_COMMAND_LINE];
    char exe_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
    pid_t pid;
    uid_t uid;
    gid_t gid;
} ksi_prompt_task;

static void *identify_worker(void *arg)
{
    ksi_identify_task *task = arg;
    ksi_daemon_command command;
    ksi_client_identified_result *result;

    result = calloc(1, sizeof(*result));

    if (result != NULL) {
        result->has_identity = ksi_permissions_identify_process(
            task->pid,
            result->exe_path, sizeof(result->exe_path),
            result->command_line, sizeof(result->command_line),
            result->exe_hash) == 0;
    }

    memset(&command, 0, sizeof(command));
    command.type = KSI_DAEMON_COMMAND_CLIENT_IDENTIFIED;
    command.client_fd = task->client_fd;
    command.connection_id = task->connection_id;
    command.data.identified.result = result;

    if (!command_queue_push(task->commands, &command)) {
        free(result);
    }

    free(task);
    atomic_fetch_sub(&g_worker_threads_running, 1);
    return NULL;
}

static bool start_identify_task(
    ksi_daemon_command_queue *commands,
    pid_t pid,
    int client_fd,
    uint64_t connection_id)
{
    ksi_identify_task *task;
    pthread_t thread;
    pthread_attr_t attr;

    task = malloc(sizeof(*task));

    if (task == NULL) {
        return false;
    }

    task->commands = commands;
    task->pid = pid;
    task->client_fd = client_fd;
    task->connection_id = connection_id;

    if (pthread_attr_init(&attr) != 0) {
        free(task);
        return false;
    }

    (void)pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);

    /* Account for the worker before it can be delayed by scheduling. Shutdown
     * destroys the command queue only after this count drains. */
    atomic_fetch_add(&g_worker_threads_running, 1);

    if (pthread_create(&thread, &attr, identify_worker, task) != 0) {
        atomic_fetch_sub(&g_worker_threads_running, 1);
        pthread_attr_destroy(&attr);
        free(task);
        return false;
    }

    pthread_attr_destroy(&attr);
    return true;
}

static void *prompt_worker(void *arg)
{
    ksi_prompt_task *task = arg;
    ksi_daemon_command command;

    memset(&command, 0, sizeof(command));
    command.type = KSI_DAEMON_COMMAND_CLIENT_PROMPT_DONE;
    command.client_fd = task->client_fd;
    command.connection_id = task->connection_id;
    command.data.prompt_done.decision =
        ksi_permissions_prompt(
            task->pid, task->uid, task->gid,
            task->exe_path, task->command_line, task->exe_hash, task->missing_capabilities);
    command.data.prompt_done.requested_capabilities = task->requested_capabilities;
    command.data.prompt_done.missing_capabilities = task->missing_capabilities;

    (void)command_queue_push(task->commands, &command);
    free(task);
    atomic_fetch_sub(&g_worker_threads_running, 1);
    return NULL;
}

static bool start_prompt_task(
    ksi_daemon_command_queue *commands,
    int client_fd,
    uint64_t connection_id,
    uint32_t requested,
    uint32_t missing,
    const char *exe_path,
    const char *command_line,
    const char *exe_hash,
    pid_t pid,
    uid_t uid,
    gid_t gid)
{
    ksi_prompt_task *task;
    pthread_t thread;
    pthread_attr_t attr;

    task = malloc(sizeof(*task));

    if (task == NULL) {
        return false;
    }

    task->commands = commands;
    task->client_fd = client_fd;
    task->connection_id = connection_id;
    task->requested_capabilities = requested;
    task->missing_capabilities = missing;
    (void)snprintf(task->exe_path, sizeof(task->exe_path), "%s", exe_path != NULL ? exe_path : "");
    (void)snprintf(
        task->command_line, sizeof(task->command_line), "%s",
        command_line != NULL ? command_line : "");
    (void)snprintf(task->exe_hash, sizeof(task->exe_hash), "%s", exe_hash != NULL ? exe_hash : "");
    task->pid = pid;
    task->uid = uid;
    task->gid = gid;

    if (pthread_attr_init(&attr) != 0) {
        free(task);
        return false;
    }

    (void)pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);

    /* See start_identify_task: prompt workers also push to the command queue. */
    atomic_fetch_add(&g_worker_threads_running, 1);

    if (pthread_create(&thread, &attr, prompt_worker, task) != 0) {
        atomic_fetch_sub(&g_worker_threads_running, 1);
        pthread_attr_destroy(&attr);
        free(task);
        return false;
    }

    pthread_attr_destroy(&attr);
    return true;
}

/* --- End async workers --- */

static void request_close_client(ksi_daemon_state *state, int fd)
{
    ksi_ipc_command cmd;
    memset(&cmd, 0, sizeof(cmd));
    cmd.type = KSI_IPC_COMMAND_CLOSE_CLIENT;
    cmd.client_fd = fd;
    (void)ipc_command_queue_push(state->reverse_commands, &cmd);
}

static void remove_client(ksi_daemon_state *state, nfds_t index)
{
    ksi_client *clients = state->clients;
    nfds_t * const count = &state->client_count;

    request_close_client(state, clients[index].fd);
    hook_send_ref_release(clients[index].hook_send_ref);
    clients[index].hook_send_ref = NULL;

    /* Lane threads work from a snapshot of the subscriber list taken at
     * enqueue time, so removing the client from clients[] does not need to
     * disturb in-flight lane work.  If the lane is currently waiting on a
     * decision from this client, its send/recv will simply fail and the
     * lane will move on to the next snapshot subscriber or time out.
     *
     * "Allow once" session grants live on each client's granted_capabilities
     * field, so they die naturally with the client here. New connections from
     * the same script process inherit grants from sibling clients via
     * check_capabilities_sync; a fresh process has no siblings, so it gets a
     * new prompt. */
    for (nfds_t i = index; i + 1 < *count; i++) {
        clients[i] = clients[i + 1];
    }

    (*count)--;

    if (update_grab_state(state) != 0) {
        fprintf(stderr, "inputd: failed to update grab state after client removal\n");
    }
}

static uint64_t monotonic_ms(void)
{
    struct timespec time_value;

    if (clock_gettime(CLOCK_MONOTONIC, &time_value) != 0) {
        return 0;
    }

    return ((uint64_t)time_value.tv_sec * 1000u) + ((uint64_t)time_value.tv_nsec / 1000000u);
}

/* ------------------------------------------------------------------------
 * Output sequencer
 *
 * All replay/synthesis writes to the platform backend (uinput) are funneled
 * through a single sequencer thread.  Hook-event finalize and SYNTHESIZE_INPUT
 * both enqueue actions here, and the sequencer drains them in arrival order.
 *
 * Decoupling output from the main loop means:
 *  - A SendInput burst from a script is queued as a unit regardless of which
 *    hook lane is currently waiting on a client decision.
 *  - Slow uinput writes do not back-pressure evdev ingress or IPC handling.
 *  - A stall in one hook lane cannot prevent the other lane's output from
 *    reaching uinput.
 *
 * Actions execute in the order they were pushed onto the sequencer queue, so
 * a SYNTH pushed by a script mid-flight no longer waits for an in-progress
 * hook decision to finalize.  This matches the Windows behavior where
 * SendInput does not block on pending low-level hooks.
 * ----------------------------------------------------------------------- */

static int output_queue_init(ksi_output_queue *q)
{
    int pipe_fds[2];

    if (q == NULL) {
        return -1;
    }

    memset(q, 0, sizeof(*q));
    q->wake_read_fd = -1;
    q->wake_write_fd = -1;

    if (pthread_mutex_init(&q->mutex, NULL) != 0) {
        return -1;
    }

    q->mutex_initialized = true;

    if (pipe(pipe_fds) != 0) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
        return -1;
    }

    q->wake_read_fd = pipe_fds[0];
    q->wake_write_fd = pipe_fds[1];

    {
        int fl;
        fl = fcntl(q->wake_read_fd, F_GETFL);

        if (fl >= 0) {
            (void)fcntl(q->wake_read_fd, F_SETFL, fl | O_NONBLOCK);
        }

        fl = fcntl(q->wake_write_fd, F_GETFL);

        if (fl >= 0) {
            (void)fcntl(q->wake_write_fd, F_SETFL, fl | O_NONBLOCK);
        }
    }

    return 0;
}

static void output_queue_push_node_locked(ksi_output_queue *q, ksi_output_node *node)
{
    /* Caller holds q->mutex. */
    node->next = NULL;

    if (q->tail != NULL) {
        q->tail->next = node;
    } else {
        q->head = node;
    }

    q->tail = node;
}

static bool output_queue_pop(ksi_output_queue *q, ksi_output_action *out)
{
    ksi_output_node *node = NULL;

    if (q == NULL || !q->mutex_initialized) {
        return false;
    }

    pthread_mutex_lock(&q->mutex);

    if (q->head != NULL) {
        node = q->head;
        q->head = node->next;

        if (q->head == NULL) {
            q->tail = NULL;
        }
    }

    pthread_mutex_unlock(&q->mutex);

    if (node == NULL) {
        return false;
    }

    *out = node->action;
    free(node);
    return true;
}

static void output_queue_drain_wake(const ksi_output_queue *q)
{
    uint8_t buffer[64];

    if (q == NULL || q->wake_read_fd < 0) {
        return;
    }

    while (read(q->wake_read_fd, buffer, sizeof(buffer)) > 0) {
    }
}

static void output_queue_wake(const ksi_output_queue *q)
{
    if (q == NULL || q->wake_write_fd < 0) {
        return;
    }

    wake_pipe_write(q->wake_write_fd);
}

static void output_queue_close(ksi_output_queue *q)
{
    ksi_output_node *node;

    if (q == NULL) {
        return;
    }

    /* Free remaining nodes — and their heap-owned synth_inputs payloads —
     * before tearing the wake pipe and mutex down. */
    node = q->head;
    q->head = NULL;
    q->tail = NULL;

    while (node != NULL) {
        ksi_output_node *next = node->next;

        if (node->action.type == KSI_OUTPUT_ACTION_SYNTH) {
            free(node->action.synth_inputs);
        }

        free(node);
        node = next;
    }

    if (q->wake_read_fd >= 0) {
        close(q->wake_read_fd);
        q->wake_read_fd = -1;
    }

    if (q->wake_write_fd >= 0) {
        close(q->wake_write_fd);
        q->wake_write_fd = -1;
    }

    if (q->mutex_initialized) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
    }
}

static bool output_queue_push_replay(
    ksi_output_queue *q,
    uint32_t hook_type,
    const ksi_hook_event_payload *payload,
    size_t payload_size)
{
    ksi_output_node *node;

    if (q == NULL || payload == NULL || payload_size > sizeof(node->action.replay_payload)) {
        return false;
    }

    node = malloc(sizeof(*node));

    if (node == NULL) {
        return false;
    }

    memset(&node->action, 0, sizeof(node->action));
    node->action.type = KSI_OUTPUT_ACTION_REPLAY;
    node->action.hook_type = hook_type;
    node->action.replay_payload = *payload;
    node->action.replay_payload_size = payload_size;

    pthread_mutex_lock(&q->mutex);
    output_queue_push_node_locked(q, node);
    pthread_mutex_unlock(&q->mutex);

    output_queue_wake(q);
    return true;
}

static bool output_queue_push_synth(
    ksi_output_queue *q,
    const ksi_input *inputs,
    uint32_t count,
    uint32_t flags)
{
    ksi_output_node *node;

    if (q == NULL) {
        return false;
    }

    node = malloc(sizeof(*node));

    if (node == NULL) {
        return false;
    }

    memset(&node->action, 0, sizeof(node->action));
    node->action.type = KSI_OUTPUT_ACTION_SYNTH;
    node->action.synth_flags = flags;
    node->action.synth_count = count;

    if (count > 0u) {
        node->action.synth_inputs = malloc((size_t)count * sizeof(*inputs));

        if (node->action.synth_inputs == NULL) {
            free(node);
            return false;
        }

        memcpy(node->action.synth_inputs, inputs, (size_t)count * sizeof(*inputs));
    }

    pthread_mutex_lock(&q->mutex);
    output_queue_push_node_locked(q, node);
    pthread_mutex_unlock(&q->mutex);

    output_queue_wake(q);
    return true;
}

/* Sequencer thread.  Drains the output queue in arrival order and dispatches
 * each action to the platform backend (uinput).  Runs until
 * state->sequencer_running drops to zero AND the queue is empty, so any
 * actions enqueued after shutdown signal still complete. */
static void *output_sequencer_thread_main(void *arg)
{
    ksi_daemon_state *state = arg;
    struct pollfd pfd;

    set_realtime_priority("output sequencer");

    if (state == NULL) {
        return NULL;
    }

    pfd.fd = state->output_queue.wake_read_fd;
    pfd.events = POLLIN;

    for (;;) {
        ksi_output_action action;
        bool keep;

        /* Drain wake bytes first, THEN pop.  Reversing this order races: a
         * producer could push (then write wake byte) between an empty pop
         * and a subsequent drain_wake, after which poll() would block even
         * though the queue has work. */
        output_queue_drain_wake(&state->output_queue);

        while (output_queue_pop(&state->output_queue, &action)) {
            if (action.type == KSI_OUTPUT_ACTION_REPLAY) {
                if (state->backend != NULL && state->backend->replay_hook_event != NULL) {
                    if (state->backend->replay_hook_event(action.hook_type, &action.replay_payload) != 0) {
                        fprintf(stderr, "sequencer: replay failed\n");
                    }
                }
            } else if (action.type == KSI_OUTPUT_ACTION_SYNTH) {
                if (state->backend != NULL && state->backend->send_input != NULL) {
                    if (state->backend->send_input(action.synth_inputs, action.synth_count, action.synth_flags) != 0) {
                        fprintf(stderr, "sequencer: synth failed\n");
                    }
                }

                free(action.synth_inputs);
            }
        }

        keep = atomic_load(&state->sequencer_running) != 0;

        if (!keep) {
            break;
        }

        /* 100ms cap so the shutdown signal is observed promptly even if a
         * wake byte was lost (e.g. write coalesced into an already-full pipe). */
        (void)poll(&pfd, 1, 100);
    }

    return NULL;
}

/* ------------------------------------------------------------------------
 * Hook lane queues
 *
 * Action queue: main thread pushes ksi_lane_event* onto a capped linked list.
 * Decision queue: main thread pushes ksi_lane_decision* for the single
 * in-flight event being delivered to a hook subscriber.
 * ----------------------------------------------------------------------- */

static int lane_action_queue_init(ksi_lane_action_queue *q)
{
    int pipe_fds[2];

    if (q == NULL) {
        return -1;
    }

    memset(q, 0, sizeof(*q));
    q->wake_read_fd = -1;
    q->wake_write_fd = -1;

    if (pthread_mutex_init(&q->mutex, NULL) != 0) {
        return -1;
    }

    q->mutex_initialized = true;

    if (pipe(pipe_fds) != 0) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
        return -1;
    }

    q->wake_read_fd = pipe_fds[0];
    q->wake_write_fd = pipe_fds[1];

    {
        int fl;
        fl = fcntl(q->wake_read_fd, F_GETFL);
        if (fl >= 0) (void)fcntl(q->wake_read_fd, F_SETFL, fl | O_NONBLOCK);
        fl = fcntl(q->wake_write_fd, F_GETFL);
        if (fl >= 0) (void)fcntl(q->wake_write_fd, F_SETFL, fl | O_NONBLOCK);
    }

    return 0;
}

static bool lane_action_queue_push(ksi_lane_action_queue *q, ksi_lane_event *event)
{
    ksi_lane_action_node *node;

    if (q == NULL || event == NULL || !q->mutex_initialized) {
        return false;
    }

    node = malloc(sizeof(*node));

    if (node == NULL) {
        return false;
    }

    node->event = event;
    node->next = NULL;

    pthread_mutex_lock(&q->mutex);

    if (q->count >= KSI_MAX_LANE_ACTIONS) {
        pthread_mutex_unlock(&q->mutex);
        free(node);
        return false;
    }

    if (q->tail != NULL) {
        q->tail->next = node;
    } else {
        q->head = node;
    }

    q->tail = node;
    q->count++;
    pthread_mutex_unlock(&q->mutex);

    if (q->wake_write_fd >= 0) {
        wake_pipe_write(q->wake_write_fd);
    }

    return true;
}

static bool lane_action_queue_pop(ksi_lane_action_queue *q, ksi_lane_event **out)
{
    ksi_lane_action_node *node;

    if (q == NULL || out == NULL || !q->mutex_initialized) {
        return false;
    }

    pthread_mutex_lock(&q->mutex);

    node = q->head;

    if (node != NULL) {
        q->head = node->next;

        if (q->head == NULL) {
            q->tail = NULL;
        }

        if (q->count > 0u) {
            q->count--;
        }
    }

    pthread_mutex_unlock(&q->mutex);

    if (node == NULL) {
        return false;
    }

    *out = node->event;
    free(node);
    return true;
}

static void lane_action_queue_wake(const ksi_lane_action_queue *q)
{
    if (q == NULL || q->wake_write_fd < 0) {
        return;
    }

    wake_pipe_write(q->wake_write_fd);
}

static void lane_action_queue_drain_wake(const ksi_lane_action_queue *q)
{
    uint8_t buffer[64];

    if (q == NULL || q->wake_read_fd < 0) {
        return;
    }

    while (read(q->wake_read_fd, buffer, sizeof(buffer)) > 0) {
    }
}

static void lane_event_release_send_refs(ksi_lane_event *event)
{
    if (event == NULL) {
        return;
    }

    for (size_t i = 0; i < event->subscriber_count; i++) {
        if (event->subscriber_send_refs[i] != NULL) {
            hook_send_ref_release(event->subscriber_send_refs[i]);
            event->subscriber_send_refs[i] = NULL;
        }
    }
}

static void lane_action_queue_close(ksi_lane_action_queue *q)
{
    ksi_lane_action_node *node;

    if (q == NULL) {
        return;
    }

    node = q->head;
    q->head = NULL;
    q->tail = NULL;
    q->count = 0u;

    while (node != NULL) {
        ksi_lane_action_node *next = node->next;
        lane_event_release_send_refs(node->event);
        free(node->event);
        free(node);
        node = next;
    }

    if (q->wake_read_fd >= 0) {
        close(q->wake_read_fd);
        q->wake_read_fd = -1;
    }

    if (q->wake_write_fd >= 0) {
        close(q->wake_write_fd);
        q->wake_write_fd = -1;
    }

    if (q->mutex_initialized) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
    }
}

static int lane_decision_queue_init(ksi_lane_decision_queue *q)
{
    int pipe_fds[2];

    if (q == NULL) {
        return -1;
    }

    memset(q, 0, sizeof(*q));
    q->wake_read_fd = -1;
    q->wake_write_fd = -1;

    if (pthread_mutex_init(&q->mutex, NULL) != 0) {
        return -1;
    }

    q->mutex_initialized = true;

    if (pipe(pipe_fds) != 0) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
        return -1;
    }

    q->wake_read_fd = pipe_fds[0];
    q->wake_write_fd = pipe_fds[1];

    {
        int fl;
        fl = fcntl(q->wake_read_fd, F_GETFL);
        if (fl >= 0) (void)fcntl(q->wake_read_fd, F_SETFL, fl | O_NONBLOCK);
        fl = fcntl(q->wake_write_fd, F_GETFL);
        if (fl >= 0) (void)fcntl(q->wake_write_fd, F_SETFL, fl | O_NONBLOCK);
    }

    return 0;
}

static void lane_decision_queue_wake(const ksi_lane_decision_queue *q)
{
    if (q == NULL || q->wake_write_fd < 0) {
        return;
    }

    wake_pipe_write(q->wake_write_fd);
}

static void lane_decision_queue_drain_wake(const ksi_lane_decision_queue *q)
{
    uint8_t buffer[64];

    if (q == NULL || q->wake_read_fd < 0) {
        return;
    }

    while (read(q->wake_read_fd, buffer, sizeof(buffer)) > 0) {
    }
}

/* Pushes the decision and writes the HOOK_DECISION success ack while holding
 * g_ipc_send_mutex, so the lane thread cannot acquire that mutex to send the
 * next HOOK_EVENT on the same stream until the ack has already been written.
 * This preserves the "ack precedes next event" invariant the hook client
 * depends on, without holding the decision-queue mutex through an IPC write. */
static bool lane_decision_queue_push_with_ack(
    ksi_lane_decision_queue *q,
    ksi_lane_decision *decision,
    int client_fd,
    const ksi_message_header *request,
    uint32_t detail)
{
    ksi_lane_decision_node *node;
    ksi_status_payload ack_payload;

    if (q == NULL || decision == NULL || request == NULL || !q->mutex_initialized) {
        return false;
    }

    node = malloc(sizeof(*node));

    if (node == NULL) {
        return false;
    }

    node->decision = decision;
    node->next = NULL;

    ack_payload.status = 0;
    ack_payload.detail = detail;

    pthread_mutex_lock(&g_ipc_send_mutex);

    pthread_mutex_lock(&q->mutex);

    if (q->tail != NULL) {
        q->tail->next = node;
    } else {
        q->head = node;
    }

    q->tail = node;
    pthread_mutex_unlock(&q->mutex);

    /* Ack write is the unlocked variant because we already hold the IPC mutex.
     * The lane can pop the decision the moment we wake it below, but any send
     * it attempts must serialize behind us on g_ipc_send_mutex. */
    (void)ksi_ipc_send_framed_message(
        client_fd, KSI_MESSAGE_HOOK_DECISION,
        request->client_id, request->correlation_id,
        &ack_payload, sizeof(ack_payload));

    pthread_mutex_unlock(&g_ipc_send_mutex);

    lane_decision_queue_wake(q);
    return true;
}

static bool lane_decision_queue_pop(ksi_lane_decision_queue *q, ksi_lane_decision **out)
{
    ksi_lane_decision_node *node;

    if (q == NULL || out == NULL || !q->mutex_initialized) {
        return false;
    }

    pthread_mutex_lock(&q->mutex);

    node = q->head;

    if (node != NULL) {
        q->head = node->next;

        if (q->head == NULL) {
            q->tail = NULL;
        }
    }

    pthread_mutex_unlock(&q->mutex);

    if (node == NULL) {
        return false;
    }

    *out = node->decision;
    free(node);
    return true;
}

static void lane_decision_queue_close(ksi_lane_decision_queue *q)
{
    ksi_lane_decision_node *node;

    if (q == NULL) {
        return;
    }

    node = q->head;
    q->head = NULL;
    q->tail = NULL;

    while (node != NULL) {
        ksi_lane_decision_node *next = node->next;
        free(node->decision);
        free(node);
        node = next;
    }

    if (q->wake_read_fd >= 0) {
        close(q->wake_read_fd);
        q->wake_read_fd = -1;
    }

    if (q->wake_write_fd >= 0) {
        close(q->wake_write_fd);
        q->wake_write_fd = -1;
    }

    if (q->mutex_initialized) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
    }
}

/* Posts a hook delivery failure back to the main thread so it can update the
 * client's consecutive_hook_failures and potentially evict its subscriptions. */
static void lane_post_hook_failure(
    ksi_daemon_state *state,
    int client_fd,
    uint64_t connection_id,
    const char *reason)
{
    ksi_daemon_command cmd;

    if (state == NULL || state->commands == NULL) {
        return;
    }

    memset(&cmd, 0, sizeof(cmd));
    cmd.type = KSI_DAEMON_COMMAND_LANE_HOOK_FAILURE;
    cmd.client_fd = client_fd;
    cmd.connection_id = connection_id;
    (void)snprintf(cmd.data.hook_failure.reason, sizeof(cmd.data.hook_failure.reason),
        "%s", reason != NULL ? reason : "unknown");
    (void)command_queue_push(state->commands, &cmd);
}

/* Called only from the lane's thread (lane_thread_main). Reads decisions and
 * pushes output actions through queues that are themselves thread-safe; does
 * not touch state->clients[] (lanes work from the snapshot in `event`). */
static void lane_process_event(ksi_hook_lane *lane, ksi_lane_event *event)
{
    uint32_t final_decision = KSI_HOOK_DECISION_PASS;
    ksi_input modify_inputs[KSI_MAX_MODIFY_INPUTS];
    uint32_t modify_count = 0u;
    bool finalized = false;

    atomic_store(&lane->current_event_id, event->event_id);

    /* During shutdown skip the per-subscriber decision dance; physical events
     * still need to reach uinput so they replay as PASS. */
    if (atomic_load(&lane->shutting_down)
        || event->generation != atomic_load(&lane->flush_generation)) {
        goto finalize;
    }

    for (size_t i = 0; i < event->subscriber_count && !finalized; i++) {
        int response_fd = event->subscriber_response_fds[i];
        ksi_hook_send_ref *send_ref = event->subscriber_send_refs[i];
        int send_fd = send_ref != NULL ? send_ref->fd : -1;
        uint64_t conn_id = event->subscriber_connection_ids[i];
        bool got_decision = false;
        uint64_t deadline;
        ksi_lane_decision *decision = NULL;

        if (send_fd < 0) {
            lane_post_hook_failure(lane->state, response_fd, conn_id, "send");
            continue;
        }

        if (ipc_send_locked(send_fd, KSI_MESSAGE_HOOK_EVENT, 0,
                event->event_id, &event->payload, event->payload_size) != 0) {
            hook_send_ref_release(send_ref);
            event->subscriber_send_refs[i] = NULL;
            lane_post_hook_failure(lane->state, response_fd, conn_id, "send");
            continue;
        }

        deadline = monotonic_ms() + KSI_HOOK_DECISION_TIMEOUT_MS;

        while (!got_decision) {
            uint64_t now = monotonic_ms();
            int timeout_ms;
            struct pollfd dpfd;

            if (now >= deadline
                || atomic_load(&lane->shutting_down)
                || event->generation != atomic_load(&lane->flush_generation)) {
                break;
            }

            timeout_ms = (int)(deadline - now);

            if (timeout_ms > 100) {
                timeout_ms = 100;
            }

            dpfd.fd = lane->decision_queue.wake_read_fd;
            dpfd.events = POLLIN;
            (void)poll(&dpfd, 1, timeout_ms);
            lane_decision_queue_drain_wake(&lane->decision_queue);

            for (;;) {
                ksi_lane_decision *candidate = NULL;

                if (!lane_decision_queue_pop(&lane->decision_queue, &candidate)) {
                    break;
                }

                if (candidate == NULL) {
                    continue;
                }

                if (candidate->event_id == event->event_id
                    && candidate->responder_fd == response_fd
                    && candidate->responder_connection_id == conn_id) {
                    decision = candidate;
                    got_decision = true;
                    break;
                }

                /* Stale decision (wrong event or wrong responder) — drop. */
                free(candidate);
            }
        }

        if (!got_decision) {
            hook_send_ref_release(send_ref);
            event->subscriber_send_refs[i] = NULL;

            if (atomic_load(&lane->shutting_down)
                || event->generation != atomic_load(&lane->flush_generation)) {
                break;
            }

            lane_post_hook_failure(lane->state, response_fd, conn_id, "timeout");
            continue;
        }

        hook_send_ref_release(send_ref);
        event->subscriber_send_refs[i] = NULL;

        if (decision->decision == KSI_HOOK_DECISION_BLOCK) {
            final_decision = KSI_HOOK_DECISION_BLOCK;
            finalized = true;
        } else if (decision->decision == KSI_HOOK_DECISION_MODIFY) {
            final_decision = KSI_HOOK_DECISION_MODIFY;
            modify_count = decision->input_count;

            if (modify_count > KSI_MAX_MODIFY_INPUTS) {
                modify_count = KSI_MAX_MODIFY_INPUTS;
            }

            if (modify_count > 0u) {
                memcpy(modify_inputs, decision->inputs,
                    (size_t)modify_count * sizeof(modify_inputs[0]));
            }

            finalized = true;
        }
        /* else PASS — fall through to next subscriber. */

        free(decision);
    }

finalize:
    lane_event_release_send_refs(event);

    if (final_decision == KSI_HOOK_DECISION_PASS && !event->is_injected) {
        if (!output_queue_push_replay(&lane->state->output_queue,
                event->hook_type, &event->payload, event->payload_size)) {
            fprintf(stderr, "lane: replay enqueue failed for event %llu\n",
                (unsigned long long)event->event_id);
        }
    } else if (final_decision == KSI_HOOK_DECISION_MODIFY && modify_count > 0u) {
        if (!output_queue_push_synth(&lane->state->output_queue,
                modify_inputs, modify_count, KSI_SYNTH_FLAG_BYPASS_HOOK)) {
            fprintf(stderr, "lane: modify synth enqueue failed for event %llu\n",
                (unsigned long long)event->event_id);
        }
    }

    atomic_store(&lane->current_event_id, (uint_least64_t)0);
}

/* Elevate the calling thread to SCHED_FIFO priority 1 so that hook lane and
 * sequencer threads are not preempted by normal (SCHED_OTHER) user processes.
 * Priority 1 is the lowest real-time priority — it yields to any higher-RT
 * thread (audio, GPU drivers) but preempts all SCHED_OTHER threads.  This
 * keeps hook decision round-trips and uinput writes within a bounded latency
 * even when the system is under CPU load.
 * Failure is non-fatal; the daemon continues with default scheduling. */
static void set_realtime_priority(const char *thread_name)
{
    struct sched_param sp;
    memset(&sp, 0, sizeof(sp));
    sp.sched_priority = 1;

    if (pthread_setschedparam(pthread_self(), SCHED_FIFO, &sp) != 0) {
        fprintf(stderr,
            "inputd: note: could not set SCHED_FIFO for %s: %s\n",
            thread_name, strerror(errno));
    }
}

static void *lane_thread_main(void *arg)
{
    ksi_hook_lane *lane = arg;
    struct pollfd pfd;

    set_realtime_priority("hook lane");

    if (lane == NULL) {
        return NULL;
    }

    pfd.fd = lane->action_queue.wake_read_fd;
    pfd.events = POLLIN;

    for (;;) {
        ksi_lane_event *event = NULL;

        if (!lane_action_queue_pop(&lane->action_queue, &event)) {
            if (atomic_load(&lane->shutting_down)) {
                ksi_lane_decision *decision;

                while (lane_decision_queue_pop(&lane->decision_queue, &decision)) {
                    free(decision);
                }

                return NULL;
            }

            (void)poll(&pfd, 1, 1000);
            lane_action_queue_drain_wake(&lane->action_queue);
            continue;
        }

        lane_process_event(lane, event);
        free(event);
    }
}

static ksi_hook_lane *lane_for_hook_type(ksi_daemon_state *state, uint32_t hook_type)
{
    if (state == NULL) {
        return NULL;
    }

    if (hook_type == KSI_HOOK_KEYBOARD_LL) {
        return &state->kbd_lane;
    }

    if (hook_type == KSI_HOOK_MOUSE_LL) {
        return &state->mouse_lane;
    }

    return NULL;
}

static ksi_hook_lane *lane_for_event_id(ksi_daemon_state *state, uint64_t event_id)
{
    if (state == NULL || event_id == 0u) {
        return NULL;
    }

    if (atomic_load(&state->kbd_lane.current_event_id) == event_id) {
        return &state->kbd_lane;
    }

    if (atomic_load(&state->mouse_lane.current_event_id) == event_id) {
        return &state->mouse_lane;
    }

    return NULL;
}

static int lane_init(ksi_hook_lane *lane, ksi_daemon_state *state, uint32_t hook_type)
{
    if (lane == NULL) {
        return -1;
    }

    memset(lane, 0, sizeof(*lane));
    lane->hook_type = hook_type;
    lane->state = state;

    if (lane_action_queue_init(&lane->action_queue) != 0) {
        return -1;
    }

    if (lane_decision_queue_init(&lane->decision_queue) != 0) {
        lane_action_queue_close(&lane->action_queue);
        return -1;
    }

    atomic_store(&lane->current_event_id, (uint_least64_t)0);
    return 0;
}

static int lane_start(ksi_hook_lane *lane)
{
    if (lane == NULL) {
        return -1;
    }

    if (pthread_create(&lane->thread, NULL, lane_thread_main, lane) != 0) {
        return -1;
    }

    lane->thread_started = true;
    return 0;
}

static void lane_shutdown(ksi_hook_lane *lane)
{
    if (lane == NULL) {
        return;
    }

    if (lane->thread_started) {
        /* Set the shutdown flag first so any in-flight event in lane_process_
         * event breaks out of its decision wait promptly, then wake the lane
         * so it can drain any queued events and exit once the queue is empty. */
        atomic_store(&lane->shutting_down, 1);
        lane_action_queue_wake(&lane->action_queue);
        pthread_join(lane->thread, NULL);
        lane->thread_started = false;
    }

    lane_action_queue_close(&lane->action_queue);
    lane_decision_queue_close(&lane->decision_queue);
}

static void lane_flush_passthrough(ksi_hook_lane *lane)
{
    if (lane == NULL) {
        return;
    }

    atomic_fetch_add(&lane->flush_generation, 1u);
    lane_decision_queue_wake(&lane->decision_queue);
    lane_action_queue_wake(&lane->action_queue);
}

/* ------------------------------------------------------------------------ */

static void send_status(
    int client_fd,
    const ksi_message_header *request,
    uint32_t response_type,
    int32_t status,
    uint32_t detail)
{
    ksi_status_payload payload = {
        .status = status,
        .detail = detail,
    };

    (void)ipc_send_locked(
        client_fd,
        response_type,
        request->client_id,
        request->correlation_id,
        &payload,
        sizeof(payload));
}

static void send_hello_status(
    int client_fd,
    uint32_t client_id,
    uint64_t correlation_id,
    int32_t status,
    uint32_t granted_capabilities)
{
    ksi_client_hello_result_payload payload = {
        .status = status,
        .granted_capabilities = granted_capabilities,
    };

    (void)ipc_send_locked(
        client_fd,
        KSI_MESSAGE_CLIENT_HELLO,
        client_id,
        correlation_id,
        &payload,
        sizeof(payload));
}

static uint32_t permission_capabilities_for_request(uint32_t requested)
{
    return requested;
}

/* Returns the capabilities currently granted from the store without prompting.
 * Sets *out_missing to the grouped permission capabilities not yet granted. */
static uint32_t check_capabilities_sync(
    ksi_daemon_state *state,
    const ksi_client *client,
    uint32_t requested,
    bool force_prompt,
    uint32_t *out_missing)
{
    uint32_t expanded;
    uint32_t granted;
    uint32_t all_process_caps;
    uint32_t missing;

    if (out_missing != NULL) {
        *out_missing = 0u;
    }

    if (state == NULL || client == NULL || requested == 0u) {
        return 0u;
    }

    expanded = permission_capabilities_for_request(requested) & state->available_capabilities;

    if (expanded == 0u || !client->has_identity || state->permissions == NULL) {
        return 0u;
    }

    granted = ksi_permissions_get_allowed_capabilities(state->permissions, client->uid, client->exe_hash) & expanded;

    /* Inherit per-process session grants from sibling clients of the same
     * Keysharp process (same uid+pid). This is how "Allow once" decisions
     * survive across a script's separate hook and synthesis channels without
     * being persisted to disk or surviving past the process.
     * all_process_caps accumulates unfiltered sibling caps for implication checks. */
    all_process_caps = client->granted_capabilities;

    for (nfds_t i = 0; i < state->client_count; i++) {
        const ksi_client *sibling = &state->clients[i];

        if (sibling == client) {
            continue;
        }

        if (!sibling->authenticated || sibling->pid != client->pid || sibling->uid != client->uid) {
            continue;
        }

        granted |= (sibling->granted_capabilities & expanded);
        all_process_caps |= sibling->granted_capabilities;
    }

    /* KSI_CAP_BLOCK_INPUT is implied by hook capabilities — mirrors client_has_capability.
     * This prevents a redundant permission prompt when hook was already granted. */
    if ((expanded & KSI_CAP_BLOCK_INPUT) != 0u &&
        (all_process_caps & (KSI_CAP_HOOK_KEYBOARD | KSI_CAP_HOOK_MOUSE)) != 0u)
        granted |= KSI_CAP_BLOCK_INPUT;

    missing = expanded & ~granted;

    /* Persistent denials suppress prompts until the user explicitly opts back
     * in via RequestCapabilities(forcePrompt=true) or the keysharp-trust CLI. */
    if (!force_prompt) {
        missing &= ~ksi_permissions_get_denied_capabilities(
            state->permissions, client->uid, client->exe_hash);
    }

    if (out_missing != NULL) {
        *out_missing = missing;
    }

    return granted;
}

/* Applies a completed prompt decision and sends the CLIENT_HELLO response. */
static void process_client_prompt_done(ksi_daemon_state *state, ksi_daemon_command *command)
{
    ssize_t client_index;
    ksi_client *client;
    ksi_permission_decision decision;
    uint32_t requested;
    uint32_t missing;
    uint32_t granted;

    if (state == NULL || command == NULL) {
        return;
    }

    decision = command->data.prompt_done.decision;
	requested = command->data.prompt_done.requested_capabilities;
	missing = command->data.prompt_done.missing_capabilities;

    client_index = find_client_index_by_connection(
        state,
        command->client_fd,
        command->connection_id);

    if (client_index < 0) {
        return;
    }

    client = &state->clients[client_index];
    client->state = KSI_CLIENT_STATE_READY;

    if (!client->pending_hello_valid) {
        return;
    }

    client->pending_hello_valid = false;

	if (decision == KSI_PERMISSION_DECISION_ALLOW_ALWAYS) {
		(void)ksi_permissions_grant_persistent(
			state->permissions, client->uid, client->exe_hash, client->exe_path, missing);
	} else if (decision == KSI_PERMISSION_DECISION_PROMPT_UNAVAILABLE) {
		/* The prompt could not actually be shown to the user — don't record a
		 * persistent denial, otherwise every subsequent attempt would be
		 * silently rejected without ever giving the user a chance to decide. */
	} else if (decision != KSI_PERMISSION_DECISION_ALLOW_ONCE) {
		/* Persistent deny: matches the macOS model — the user must clear it
		 * explicitly (via RequestCapabilities(forcePrompt=true) or the
		 * keysharp-trust CLI) before we will prompt again. */
		(void)ksi_permissions_deny_persistent(
			state->permissions, client->uid, client->exe_hash, client->exe_path, missing);
	}
	/* ALLOW_ONCE is intentionally not persisted; the grant lives on this
	 * client's granted_capabilities below and propagates to sibling clients
	 * of the same process via check_capabilities_sync's inheritance loop. */

    granted = ksi_permissions_get_allowed_capabilities(state->permissions, client->uid, client->exe_hash)
              & (permission_capabilities_for_request(requested) & state->available_capabilities);

    if (decision == KSI_PERMISSION_DECISION_ALLOW_ONCE) {
        granted |= missing;
    }

    if (decision == KSI_PERMISSION_DECISION_DENY
        || decision == KSI_PERMISSION_DECISION_PROMPT_UNAVAILABLE
        || (granted & requested) != requested) {
        client->authenticated = false;
        client->granted_capabilities = granted;
        send_hello_status(client->fd,
            client->pending_hello_client_id, client->pending_hello_correlation_id,
            -1, granted);
        return;
    }

	ksi_permissions_note_seen(state->permissions, client->uid, client->exe_hash, client->exe_path);
	client->authenticated = true;
    client->granted_capabilities |= granted;
    send_hello_status(client->fd,
        client->pending_hello_client_id, client->pending_hello_correlation_id,
        0, granted);
}

/* Stores the identification result and, if a CLIENT_HELLO was buffered,
 * processes it immediately or starts the permission prompt. */
static void process_client_identified(ksi_daemon_state *state, ksi_daemon_command *command)
{
    ssize_t client_index;
    ksi_client *client;
    ksi_client_identified_result *result;

    if (state == NULL || command == NULL) {
        return;
    }

    result = command->data.identified.result;
    client_index = find_client_index_by_connection(
        state,
        command->client_fd,
        command->connection_id);

    if (client_index < 0) {
        return;
    }

    client = &state->clients[client_index];

    if (result != NULL && result->has_identity) {
        client->has_identity = true;
        (void)snprintf(client->exe_path, sizeof(client->exe_path), "%s", result->exe_path);
        (void)snprintf(client->command_line, sizeof(client->command_line), "%s", result->command_line);
        (void)snprintf(client->exe_hash, sizeof(client->exe_hash), "%s", result->exe_hash);

        if (g_verbose) {
            printf("identified client fd=%d hash=%s path=%s command=%s\n",
                client->fd, client->exe_hash, client->exe_path, client->command_line);
        }
    } else {
        client->has_identity = false;
        fprintf(stderr, "inputd: unable to identify process for client pid=%ld uid=%ld\n",
            (long)client->pid, (long)client->uid);
    }

    client->state = KSI_CLIENT_STATE_READY;

    if (!client->pending_hello_valid) {
        return;
    }

    /* There is a buffered CLIENT_HELLO — process it now that we have identity. */
	{
		uint32_t requested = client->pending_hello_requested;
		uint32_t flags = client->pending_hello_flags;
		bool force_prompt = (flags & KSI_CLIENT_HELLO_FLAG_FORCE_PROMPT) != 0u;
		uint32_t missing = 0u;
		uint32_t granted = check_capabilities_sync(state, client, requested, force_prompt, &missing);

        if (missing != 0u && state->permissions != NULL) {
            /* Need to prompt. Transition to AWAITING_PROMPT and start the worker. */
            if (!start_prompt_task(state->commands, client->fd, client->connection_id, requested, missing,
                                   client->exe_path, client->command_line, client->exe_hash,
                                   client->pid, client->uid, client->gid)) {
                /* Prompt thread failed; deny immediately. */
                client->pending_hello_valid = false;
                client->state = KSI_CLIENT_STATE_READY;
                send_hello_status(client->fd,
                    client->pending_hello_client_id, client->pending_hello_correlation_id,
                    -1, 0u);
            } else {
                client->state = KSI_CLIENT_STATE_AWAITING_PROMPT;
            }
            return;
        }

        /* No prompt needed — grant immediately. */
        client->pending_hello_valid = false;

        if (requested != 0u && (granted & requested) != requested) {
            client->authenticated = false;
            client->granted_capabilities = granted;
            send_hello_status(client->fd,
                client->pending_hello_client_id, client->pending_hello_correlation_id,
                -1, granted);
            return;
        }

		if (granted != 0u) {
			ksi_permissions_note_seen(state->permissions, client->uid, client->exe_hash, client->exe_path);
		}

        client->authenticated = true;
        client->granted_capabilities |= granted;
        send_hello_status(client->fd,
            client->pending_hello_client_id, client->pending_hello_correlation_id,
            0, granted);
    }
}

static uint32_t daemon_available_capabilities(const ksi_platform_backend *backend)
{
    if (backend->get_available_capabilities != NULL) {
        return backend->get_available_capabilities();
    }

    return 0;
}

static bool client_has_capability(const ksi_client *client, uint32_t capability)
{
    if (!client->authenticated)
        return false;
    return (client->granted_capabilities & capability) == capability;
}

static uint32_t hook_type_to_cap_bit(uint32_t hook_type)
{
    if (hook_type == KSI_HOOK_KEYBOARD_LL) {
        return KSI_CAP_HOOK_KEYBOARD;
    }

    if (hook_type == KSI_HOOK_MOUSE_LL) {
        return KSI_CAP_HOOK_MOUSE;
    }

    return 0;
}


static uint32_t required_synthesis_capabilities(const ksi_input *inputs, size_t count)
{
    uint32_t required = 0;

    for (size_t i = 0; i < count; i++) {
        if (inputs[i].type == KSI_INPUT_KEYBOARD) {
            required |= KSI_CAP_SYNTH_KEYBOARD;
        } else if (inputs[i].type == KSI_INPUT_MOUSE) {
            required |= KSI_CAP_SYNTH_MOUSE;
        }
    }

    return required;
}

static uint32_t active_hook_subscription_mask(const ksi_daemon_state *state)
{
    uint32_t mask = 0;

    if (state == NULL) {
        return 0;
    }

    for (nfds_t i = 0; i < state->client_count; i++) {
        mask |= state->clients[i].hook_subscriptions;
    }

    return mask;
}

static uint32_t active_block_input_mask(const ksi_daemon_state *state)
{
    uint32_t mask = 0;

    if (state == NULL) {
        return 0;
    }

    for (nfds_t i = 0; i < state->client_count; i++) {
        mask |= state->clients[i].block_input_mask;
    }

    return mask;
}

static bool any_matching_hook_subscriptions(const ksi_daemon_state *state, uint32_t hook_type)
{
    uint32_t subscription_bit;

    if (state == NULL) {
        return false;
    }

    subscription_bit = hook_type_to_cap_bit(hook_type);

    if (subscription_bit == 0) {
        return false;
    }

    for (nfds_t i = 0; i < state->client_count; i++) {
        if ((state->clients[i].hook_subscriptions & subscription_bit) != 0) {
            return true;
        }
    }

    return false;
}

static int update_grab_state(ksi_daemon_state *state)
{
    uint32_t hook_mask = active_hook_subscription_mask(state);
    uint32_t block_mask = active_block_input_mask(state);
    int result;

    if (state == NULL || state->backend == NULL) {
        return 0;
    }

    result = state->backend->set_grab_hook_mask == NULL
        ? 0
        : state->backend->set_grab_hook_mask(hook_mask);

    if (result != 0) {
        return result;
    }

    /* Platforms without BlockInput support must not advertise KSI_CAP_BLOCK_INPUT.
     * If a non-zero block mask somehow arrives on such a platform, fail so that
     * handle_set_block_input rolls back and reports an error rather than silently
     * accepting a mask that cannot be enforced. */
    return state->backend->set_block_input_mask == NULL
        ? (block_mask == 0 ? 0 : -1)
        : state->backend->set_block_input_mask(block_mask);
}

static void clear_hook_state(ksi_daemon_state *state)
{
    if (state == NULL) {
        return;
    }

    lane_flush_passthrough(&state->kbd_lane);
    lane_flush_passthrough(&state->mouse_lane);

    for (nfds_t i = 0; i < state->client_count; i++) {
        state->clients[i].hook_subscriptions = 0;
        state->clients[i].block_input_mask = 0;
        state->clients[i].consecutive_hook_failures = 0;
    }
}

static void record_client_hook_success(ksi_daemon_state *state, nfds_t index)
{
    if (state == NULL || index >= state->client_count) {
        return;
    }

    state->clients[index].consecutive_hook_failures = 0;
}

static void record_client_hook_failure(ksi_daemon_state *state, nfds_t index, const char *reason)
{
    ksi_client *client;

    if (state == NULL || index >= state->client_count) {
        return;
    }

    client = &state->clients[index];

    if (client->hook_subscriptions == 0) {
        return;
    }

    client->consecutive_hook_failures++;

    fprintf(stderr,
        "client fd=%d hook callback failed (%s), consecutive failures=%u/%u\n",
        client->fd,
        reason == NULL ? "unknown" : reason,
        client->consecutive_hook_failures,
        KSI_MAX_CONSECUTIVE_HOOK_FAILURES);

    if (client->consecutive_hook_failures < KSI_MAX_CONSECUTIVE_HOOK_FAILURES) {
        return;
    }

    fprintf(stderr,
        "client fd=%d hook subscriptions removed after %u consecutive callback failures\n",
        client->fd,
        client->consecutive_hook_failures);

    client->hook_subscriptions = 0;
    client->consecutive_hook_failures = 0;

    if (update_grab_state(state) != 0) {
        fprintf(stderr, "inputd: failed to update grab state after hook failure eviction\n");
    }
}

/* Constructs a ksi_lane_event snapshotting the current hook subscribers and
 * pushes it onto the matching lane's action queue.  If allocation or enqueue
 * fails, the event is dropped; replaying it would violate the hook contract by
 * turning a backpressured hooked key into delayed text later.
 *
 * Called only from the main thread (via daemon_handle_hook_event from the
 * platform backend).  Reads state->clients[] without locking — that array is
 * owned by the main thread. */
static void dispatch_hook_event_to_lane(
    ksi_daemon_state *state,
    uint32_t hook_type,
    const void *event,
    size_t event_size,
    bool is_injected)
{
    ksi_hook_lane *lane;
    ksi_lane_event *lane_event;
    uint32_t subscription_bit;

    lane = lane_for_hook_type(state, hook_type);

    if (lane == NULL || event_size > sizeof(lane_event->payload.event)) {
        return;
    }

    subscription_bit = hook_type_to_cap_bit(hook_type);

    if (subscription_bit == 0u) {
        return;
    }

    lane_event = calloc(1, sizeof(*lane_event));

    if (lane_event == NULL) {
        return;
    }

    lane_event->event_id = state->next_event_id++;
    lane_event->hook_type = hook_type;
    lane_event->generation = atomic_load(&lane->flush_generation);
    lane_event->is_injected = is_injected;
    lane_event->payload.event_id = lane_event->event_id;
    lane_event->payload.hook_type = hook_type;
    memcpy(&lane_event->payload.event, event, event_size);
    lane_event->payload_size =
        sizeof(lane_event->payload.event_id)
        + sizeof(lane_event->payload.hook_type)
        + sizeof(lane_event->payload.reserved)
        + event_size;

    for (nfds_t i = 0; i < state->client_count
            && lane_event->subscriber_count < KSI_MAX_CLIENTS; i++) {
        const ksi_client *c = &state->clients[i];

        if ((c->hook_subscriptions & subscription_bit) == 0u) {
            continue;
        }

        if (!hook_send_ref_acquire(c->hook_send_ref)) {
            fprintf(stderr, "inputd: hook subscriber has no send handle\n");
            continue;
        }

        lane_event->subscriber_send_refs[lane_event->subscriber_count] = c->hook_send_ref;
        lane_event->subscriber_response_fds[lane_event->subscriber_count] = c->fd;
        lane_event->subscriber_connection_ids[lane_event->subscriber_count] = c->connection_id;
        lane_event->subscriber_granted_caps[lane_event->subscriber_count] = c->granted_capabilities;
        lane_event->subscriber_count++;
    }

    if (!lane_action_queue_push(&lane->action_queue, lane_event)) {
        if (!is_injected) {
            (void)output_queue_push_replay(&state->output_queue,
                hook_type, &lane_event->payload, lane_event->payload_size);
        }

        fprintf(stderr, "inputd: hook lane queue full; bypassed hook event %llu\n",
            (unsigned long long)lane_event->event_id);
        lane_event_release_send_refs(lane_event);
        free(lane_event);
    }
}

static void daemon_handle_hook_event(
    void *context,
    uint32_t hook_type,
    const void *event,
    size_t event_size)
{
    ksi_daemon_state *state = context;
    uint32_t subscription_bit = hook_type_to_cap_bit(hook_type);
    uint32_t block_bit;
    bool is_injected;

    if (state == NULL || subscription_bit == 0) {
        return;
    }

    block_bit = hook_type == KSI_HOOK_KEYBOARD_LL
        ? KSI_BLOCK_INPUT_KEYBOARD
        : KSI_BLOCK_INPUT_MOUSE;
    is_injected = hook_type == KSI_HOOK_KEYBOARD_LL
        ? ((((const ksi_keyboard_hook_event *)event)->flags & KSI_LLKHF_INJECTED) != 0)
        : ((((const ksi_mouse_hook_event *)event)->flags & KSI_LLMHF_INJECTED) != 0);

    if (!is_injected && (active_block_input_mask(state) & block_bit) != 0) {
        return;
    }

    if (!any_matching_hook_subscriptions(state, hook_type)) {
        /* A mixed keyboard/mouse device can be grabbed for the other class of
         * input. Replay nonblocked physical events that have no hook target. */
        if (!is_injected) {
            ksi_hook_event_payload payload;
            memset(&payload, 0, sizeof(payload));
            payload.hook_type = hook_type;
            memcpy(&payload.event, event, event_size);

            if (!output_queue_push_replay(&state->output_queue, hook_type, &payload,
                    sizeof(payload.event_id) + sizeof(payload.hook_type)
                    + sizeof(payload.reserved) + event_size)) {
                fprintf(stderr, "inputd: replay enqueue of unhooked grabbed input failed\n");
            }
        }
        return;
    }

    dispatch_hook_event_to_lane(state, hook_type, event, event_size, is_injected);
}

static void handle_client_hello(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
	const ksi_client_hello_payload *payload;
	uint32_t requested = 0;
	uint32_t flags = 0u;
	bool force_prompt;
	uint32_t missing = 0u;
	uint32_t granted;

	if (message->payload_size >= sizeof(*payload)) {
		payload = (const ksi_client_hello_payload *)(const void *)message->payload;
		requested = payload->requested_capabilities;
		flags = payload->flags;
	}

	force_prompt = (flags & KSI_CLIENT_HELLO_FLAG_FORCE_PROMPT) != 0u;

    /* If identification is still in progress, buffer the hello and defer. */
    if (client->state == KSI_CLIENT_STATE_IDENTIFYING) {
		client->pending_hello_valid = true;
		client->pending_hello_requested = requested;
		client->pending_hello_flags = flags;
		client->pending_hello_correlation_id = message->header->correlation_id;
		client->pending_hello_client_id = message->header->client_id;
        return;
    }

    /* If a prompt is already running for this client, reject the duplicate hello. */
    if (client->state == KSI_CLIENT_STATE_AWAITING_PROMPT) {
        send_hello_status(client->fd, message->header->client_id, message->header->correlation_id, -1, 0);
        return;
    }

    /* Identity is known. Check what is already granted without prompting. */
	granted = check_capabilities_sync(state, client, requested, force_prompt, &missing);

    /* If there are missing capabilities and a permissions store is configured,
     * start an async prompt. Response will be sent from process_client_prompt_done. */
    if (missing != 0u && state->permissions != NULL) {
        if (!start_prompt_task(state->commands, client->fd, client->connection_id, requested, missing,
                               client->exe_path, client->command_line, client->exe_hash,
                               client->pid, client->uid, client->gid)) {
            send_hello_status(client->fd, message->header->client_id, message->header->correlation_id, -1, granted);
            return;
        }

		client->state = KSI_CLIENT_STATE_AWAITING_PROMPT;
		client->pending_hello_valid = true;
		client->pending_hello_requested = requested;
		client->pending_hello_flags = flags;
		client->pending_hello_correlation_id = message->header->correlation_id;
		client->pending_hello_client_id = message->header->client_id;
        return;
    }

    /* All requested capabilities already granted — respond immediately. */
    if (requested != 0u && (granted & requested) != requested) {
        client->authenticated = false;
        client->granted_capabilities = granted;
        send_hello_status(client->fd, message->header->client_id, message->header->correlation_id, -1, granted);
        return;
    }

	if (granted != 0u && client->has_identity) {
		ksi_permissions_note_seen(state->permissions, client->uid, client->exe_hash, client->exe_path);
	}

    client->authenticated = true;
    client->granted_capabilities |= granted;
    send_hello_status(client->fd, message->header->client_id, message->header->correlation_id, 0, granted);
}

static void handle_emergency_passthrough(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    /* This is a rescue path for hook owners. Do not let an arbitrary
     * zero-capability hello on the public system socket clear other scripts. */
    if (!client->authenticated
        || (client->granted_capabilities
            & (KSI_CAP_HOOK_KEYBOARD | KSI_CAP_HOOK_MOUSE)) == 0u) {
        send_status(client->fd, message->header, KSI_MESSAGE_EMERGENCY_PASSTHROUGH, -1, 403);
        return;
    }

    clear_hook_state(state);

    if (update_grab_state(state) != 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_EMERGENCY_PASSTHROUGH, -1, 1);
        return;
    }

    send_status(client->fd, message->header, KSI_MESSAGE_EMERGENCY_PASSTHROUGH, 0, 0);
}

static void handle_get_indicator_state(
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    if (!client->authenticated
        || (client->granted_capabilities
            & (KSI_CAP_HOOK_KEYBOARD | KSI_CAP_HOOK_MOUSE)) == 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_INDICATOR_STATE_RESULT, -1, 403);
        return;
    }

    send_indicator_state_result(client->fd, message->header);
}

static void handle_get_pointer_position(
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    if (!client->authenticated
        || (client->granted_capabilities & KSI_CAP_HOOK_MOUSE) == 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_POINTER_POSITION_RESULT, -1, 403);
        return;
    }

    send_pointer_position_result(client->fd, message->header);
}

typedef struct ksi_list_permissions_context {
    int client_fd;
    uint32_t client_id;
    uint64_t correlation_id;
    bool send_failed;
} ksi_list_permissions_context;

static bool list_permissions_visitor(
    const ksi_permission_entry *entry,
    void *user_data)
{
    ksi_list_permissions_context *context = user_data;
    uint8_t buffer[sizeof(ksi_list_permissions_entry_payload) + KSI_PERMISSION_MAX_PATH];
    ksi_list_permissions_entry_payload *payload =
        (ksi_list_permissions_entry_payload *)(void *)buffer;
    size_t path_length = 0u;
    size_t total_size;

    if (context == NULL || entry == NULL) {
        return false;
    }

    /* Only stream records that carry at least one input capability bit. */
    if ((entry->persistent_allowed_capabilities & KSI_INPUT_CAPABILITIES) == 0u
        && (entry->persistent_denied_capabilities & KSI_INPUT_CAPABILITIES) == 0u) {
        return true;
    }

    if (entry->exe_path != NULL) {
        path_length = strnlen(entry->exe_path, KSI_PERMISSION_MAX_PATH);
    }

    memset(payload, 0, sizeof(*payload));
    payload->uid = (uint32_t)entry->uid;
    payload->persistent_allowed_capabilities = entry->persistent_allowed_capabilities;
    payload->persistent_denied_capabilities = entry->persistent_denied_capabilities;
    payload->path_length = (uint16_t)path_length;
    payload->last_seen_utc = entry->last_seen_utc;
    (void)snprintf(payload->exe_hash, sizeof(payload->exe_hash), "%s", entry->exe_hash);

    if (path_length > 0u) {
        memcpy(buffer + sizeof(*payload), entry->exe_path, path_length);
    }

    total_size = sizeof(*payload) + path_length;

    if (ipc_send_locked(
            context->client_fd,
            KSI_MESSAGE_LIST_PERMISSIONS_ENTRY,
            context->client_id,
            context->correlation_id,
            buffer,
            total_size) != 0) {
        context->send_failed = true;
        return false;
    }

    return true;
}

static void handle_list_permissions(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    ksi_list_permissions_context context;
    uid_t filter;

    if (state == NULL || client == NULL) {
        return;
    }

    filter = (geteuid() == 0 && client->uid == 0) ? (uid_t)-1 : client->uid;

    context.client_fd = client->fd;
    context.client_id = message->header->client_id;
    context.correlation_id = message->header->correlation_id;
    context.send_failed = false;

    if (state->permissions != NULL) {
        ksi_permissions_for_each(state->permissions, filter, list_permissions_visitor, &context);
    }

    send_status(
        client->fd,
        message->header,
        KSI_MESSAGE_LIST_PERMISSIONS_RESULT,
        context.send_failed ? -1 : 0,
        0u);
}

static void handle_reset_permissions(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    const ksi_reset_permissions_payload *payload;
    uid_t target_uid;
    uint32_t capabilities;
    char exe_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];

    if (state == NULL || client == NULL) {
        return;
    }

    if (state->permissions == NULL) {
        send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, -1, 500);
        return;
    }

    if (message->payload_size < sizeof(*payload)) {
        send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, -1, 400);
        return;
    }

    payload = (const ksi_reset_permissions_payload *)(const void *)message->payload;

    target_uid = (payload->target_uid == KSI_RESET_PERMISSIONS_UID_SELF)
        ? client->uid
        : (uid_t)payload->target_uid;

    /* Clamp to input capabilities — screencap manages its own domain. */
    capabilities = payload->capabilities & KSI_INPUT_CAPABILITIES;

    if (capabilities == 0u) {
        send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, -1, 400);
        return;
    }

    if (target_uid != client->uid && (geteuid() != 0 || client->uid != 0)) {
        send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, -1, 403);
        return;
    }

    (void)snprintf(exe_hash, sizeof(exe_hash), "%.*s",
        (int)sizeof(payload->exe_hash) - 1,
        payload->exe_hash);

    if (exe_hash[0] == '\0') {
        send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, -1, 400);
        return;
    }

    if (ksi_permissions_clear_persistent(state->permissions, target_uid, exe_hash, capabilities) != 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, -1, 500);
        return;
    }

    send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, 0, 0u);
}

static void handle_hook_subscription(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    const ksi_hook_subscription_payload *payload;
    uint32_t capability;
    uint32_t subscription_bit;
    uint32_t old_subscriptions;

    if (message->payload_size != sizeof(*payload)) {
        send_status(client->fd, message->header, message->header->type, -1, 1);
        return;
    }

    payload = (const ksi_hook_subscription_payload *)(const void *)message->payload;
    capability = subscription_bit = hook_type_to_cap_bit(payload->hook_type);

    if (capability == 0 || subscription_bit == 0) {
        send_status(client->fd, message->header, message->header->type, -1, 2);
        return;
    }

    if (!client_has_capability(client, capability)) {
        send_status(client->fd, message->header, message->header->type, -1, 403);
        return;
    }

    old_subscriptions = client->hook_subscriptions;

    if (message->header->type == KSI_MESSAGE_SUBSCRIBE_HOOK) {
        client->hook_subscriptions |= subscription_bit;
        client->consecutive_hook_failures = 0;
    } else {
        client->hook_subscriptions &= (uint32_t)~subscription_bit;

        if (client->hook_subscriptions == 0) {
            client->consecutive_hook_failures = 0;
        }
    }

    if (update_grab_state(state) != 0) {
        client->hook_subscriptions = old_subscriptions;
        (void)update_grab_state(state);
        send_status(client->fd, message->header, message->header->type, -1, 5);
        return;
    }

    send_status(client->fd, message->header, message->header->type, 0, client->hook_subscriptions);
}

static void handle_set_block_input(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    const ksi_block_input_payload *payload;
    uint32_t old_mask;

    if (message->payload_size != sizeof(*payload)) {
        send_status(client->fd, message->header, KSI_MESSAGE_SET_BLOCK_INPUT, -1, 1);
        return;
    }

    payload = (const ksi_block_input_payload *)(const void *)message->payload;

    if ((payload->block_mask & (uint32_t)~(KSI_BLOCK_INPUT_KEYBOARD | KSI_BLOCK_INPUT_MOUSE)) != 0u) {
        send_status(client->fd, message->header, KSI_MESSAGE_SET_BLOCK_INPUT, -1, 2);
        return;
    }

    if (!client_has_capability(client, KSI_CAP_BLOCK_INPUT)) {
        send_status(client->fd, message->header, KSI_MESSAGE_SET_BLOCK_INPUT, -1, 403);
        return;
    }

    old_mask = client->block_input_mask;
    client->block_input_mask = payload->block_mask;

    if (update_grab_state(state) != 0) {
        client->block_input_mask = old_mask;
        (void)update_grab_state(state);
        send_status(client->fd, message->header, KSI_MESSAGE_SET_BLOCK_INPUT, -1, 5);
        return;
    }

    send_status(client->fd, message->header, KSI_MESSAGE_SET_BLOCK_INPUT, 0, client->block_input_mask);
}

/* Validates the decision payload on the main thread, then routes it to the
 * lane that is currently processing the matching event_id.  The success ack is
 * written before the lane can observe the queued decision, preserving stream
 * order for clients which read HOOK_DECISION status before the next HOOK_EVENT. */
static void handle_hook_decision(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    const ksi_hook_decision_payload *payload;
    size_t expected_size;
    ksi_hook_lane *lane;
    ksi_lane_decision *decision;

    if (message->payload_size < sizeof(*payload)) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 1);
        return;
    }

    payload = (const ksi_hook_decision_payload *)(const void *)message->payload;

    if (payload->input_count > KSI_MAX_MODIFY_INPUTS) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 5);
        return;
    }

    expected_size = sizeof(*payload) + ((size_t)payload->input_count * sizeof(payload->inputs[0]));

    if (message->payload_size != expected_size) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 6);
        return;
    }

    if (payload->decision != KSI_HOOK_DECISION_PASS
        && payload->decision != KSI_HOOK_DECISION_BLOCK
        && payload->decision != KSI_HOOK_DECISION_MODIFY) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 4);
        return;
    }

    if (payload->decision == KSI_HOOK_DECISION_MODIFY && payload->input_count == 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 7);
        return;
    }

    if (state == NULL) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 2);
        return;
    }

    if (payload->decision == KSI_HOOK_DECISION_MODIFY) {
        if (!client_has_capability(
                client,
                required_synthesis_capabilities(payload->inputs, payload->input_count))) {
            send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 403);
            return;
        }
    }

    /* Locate the lane currently waiting on this event_id.  If neither lane
     * has it as their current event, the decision is stale (timeout already
     * fired or the lane already finalized via BLOCK/MODIFY from someone else). */
    lane = lane_for_event_id(state, payload->event_id);

    if (lane == NULL) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 2);
        return;
    }

    decision = calloc(1, sizeof(*decision));

    if (decision == NULL) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 500);
        return;
    }

    decision->event_id = payload->event_id;
    decision->decision = payload->decision;
    decision->responder_fd = client->fd;
    decision->responder_connection_id = client->connection_id;
    decision->input_count = payload->input_count;

    if (payload->input_count > 0u) {
        memcpy(decision->inputs, payload->inputs,
            (size_t)payload->input_count * sizeof(payload->inputs[0]));
    }

    if (!lane_decision_queue_push_with_ack(
            &lane->decision_queue,
            decision,
            client->fd,
            message->header,
            payload->decision)) {
        free(decision);
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 12);
        return;
    }

    {
        /* Resolve the responder under the connection_id guard so we don't
         * credit success on a freshly-accepted client that happens to have
         * inherited this fd from a prior disconnect. */
        ssize_t resolved = find_client_index_by_fd(state, client->fd);

        if (resolved >= 0
            && state->clients[resolved].connection_id == client->connection_id) {
            record_client_hook_success(state, (nfds_t)resolved);
        }
    }

    /* Success ack was sent by lane_decision_queue_push_with_ack before waking
     * the lane, so no subsequent HOOK_EVENT can overtake it on this stream. */
}

static void handle_synthesize_input(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    const ksi_synthesize_input_payload *payload;
    size_t expected_size;

    if (message->payload_size < sizeof(*payload)) {
        send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1, 1);
        return;
    }

    payload = (const ksi_synthesize_input_payload *)(const void *)message->payload;

    if (payload->count > KSI_MAX_SYNTH_INPUTS) {
        send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1, 2);
        return;
    }

    expected_size = sizeof(*payload) + ((size_t)payload->count * sizeof(payload->inputs[0]));

    if (message->payload_size != expected_size) {
        send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1, 3);
        return;
    }

    if (!client_has_capability(
            client,
            required_synthesis_capabilities(payload->inputs, payload->count))) {
        send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1, 403);
        return;
    }

    if (!output_queue_push_synth(&state->output_queue,
            payload->inputs, payload->count, payload->flags)) {
        send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1, 12);
        return;
    }

    send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, 0, 0);
}

static void handle_binary_message(
    const ksi_platform_backend *backend,
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    (void)backend;

    if (g_verbose) {
        printf("client %d binary type=%u size=%u correlation=%llu\n",
            client->fd,
            message->header->type,
            message->header->size,
            (unsigned long long)message->header->correlation_id);
    }

    switch (message->header->type) {
        case KSI_MESSAGE_CLIENT_HELLO:
            handle_client_hello(state, client, message);
            return;
        case KSI_MESSAGE_HEARTBEAT:
            send_status(client->fd, message->header, KSI_MESSAGE_HEARTBEAT, 0, 0);
            return;
        case KSI_MESSAGE_EMERGENCY_PASSTHROUGH:
            handle_emergency_passthrough(state, client, message);
            return;
        case KSI_MESSAGE_SET_BLOCK_INPUT:
            handle_set_block_input(state, client, message);
            return;
        case KSI_MESSAGE_GET_INDICATOR_STATE:
            handle_get_indicator_state(client, message);
            return;
        case KSI_MESSAGE_GET_POINTER_POSITION:
            handle_get_pointer_position(client, message);
            return;
        case KSI_MESSAGE_GET_KEY_STATE:
            handle_get_key_state(client, message);
            return;
        case KSI_MESSAGE_LIST_PERMISSIONS:
            handle_list_permissions(state, client, message);
            return;
        case KSI_MESSAGE_RESET_PERMISSIONS:
            handle_reset_permissions(state, client, message);
            return;
        case KSI_MESSAGE_SUBSCRIBE_HOOK:
        case KSI_MESSAGE_UNSUBSCRIBE_HOOK:
            handle_hook_subscription(state, client, message);
            return;
        case KSI_MESSAGE_HOOK_DECISION:
            handle_hook_decision(state, client, message);
            return;
        case KSI_MESSAGE_SYNTHESIZE_INPUT:
            handle_synthesize_input(state, client, message);
            return;
        default:
            send_status(client->fd, message->header, message->header->type, -1, 404);
            return;
    }
}

static bool process_client_buffer(ksi_daemon_command_queue *commands, ksi_ipc_slot *slot)
{
    size_t offset = 0;

    while (slot->rx_used - offset >= sizeof(ksi_message_header)) {
        const ksi_message_header *header =
            (const ksi_message_header *)(const void *)(slot->rx_buffer + offset);
        ksi_daemon_command command;
        uint8_t *frame;

        if (header->size < sizeof(*header) || header->size > KSI_MAX_MESSAGE_SIZE) {
            fprintf(stderr, "client %d sent invalid frame size %u\n", slot->fd, header->size);
            return false;
        }

        if (header->major != KSI_PROTOCOL_MAJOR || header->minor > KSI_PROTOCOL_MINOR) {
            fprintf(stderr,
                "client %d sent unsupported protocol version %u.%u\n",
                slot->fd,
                header->major,
                header->minor);
            return false;
        }

        if (slot->rx_used - offset < header->size) {
            break;
        }

        frame = malloc(header->size);

        if (frame == NULL) {
            return false;
        }

        memcpy(frame, slot->rx_buffer + offset, header->size);

        memset(&command, 0, sizeof(command));
        command.type = KSI_DAEMON_COMMAND_CLIENT_FRAME;
        command.client_fd = slot->fd;
        command.data.frame.data = frame;
        command.data.frame.size = header->size;

        if (!command_queue_push(commands, &command)) {
            free(frame);
            return false;
        }

        offset += header->size;
    }

    if (offset > 0) {
        if (offset < slot->rx_used) {
            memmove(slot->rx_buffer, slot->rx_buffer + offset, slot->rx_used - offset);
        }

        slot->rx_used -= offset;
    }

    return true;
}

static int read_client_frames(ksi_daemon_command_queue *commands, ksi_ipc_slot *slot)
{
    for (;;) {
        ssize_t bytes_read;
        size_t available = sizeof(slot->rx_buffer) - slot->rx_used;

        if (available == 0) {
            fprintf(stderr, "client %d receive buffer overflow\n", slot->fd);
            return -1;
        }

        bytes_read = read(slot->fd, slot->rx_buffer + slot->rx_used, available);

        if (bytes_read == 0) {
            return 0;
        }

        if (bytes_read < 0) {
            if (errno == EINTR) {
                continue;
            }

            if (errno == EAGAIN || errno == EWOULDBLOCK) {
                return 1;
            }

            return -1;
        }

        slot->rx_used += (size_t)bytes_read;

        if (!process_client_buffer(commands, slot)) {
            return -1;
        }

        if ((size_t)bytes_read < available) {
            return 1;
        }
    }
}

static void send_indicator_state_result(int client_fd, const ksi_message_header *request)
{
    ksi_indicator_state_payload result;
    bool caps = false;
    bool num = false;
    bool scroll = false;

    ksi_linux_devices_get_indicator_state(&caps, &num, &scroll);

    memset(&result, 0, sizeof(result));
    result.caps_lock = caps ? 1u : 0u;
    result.num_lock = num ? 1u : 0u;
    result.scroll_lock = scroll ? 1u : 0u;

    (void)ipc_send_locked(
        client_fd,
        KSI_MESSAGE_INDICATOR_STATE_RESULT,
        request->client_id,
        request->correlation_id,
        &result,
        sizeof(result));
}

static void handle_get_key_state(
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    ksi_key_state_payload result;
    bool caps = false, num = false, scroll = false;

    if (!client->authenticated
        || (client->granted_capabilities & KSI_CAP_HOOK_KEYBOARD) == 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_KEY_STATE_RESULT, -1, 403);
        return;
    }

    /* Refresh the indicator state directly from hardware (EVIOCGLED) before
     * reading it, so callers get the current LED state rather than the
     * potentially stale EV_LED-derived cache. This is important for toggle-key
     * checks immediately after a synthetic CapsLock/NumLock/ScrollLock event
     * (e.g. the ToggleKeyState call in SendKeys), where the compositor's LED
     * acknowledgement may not yet have produced an EV_LED event. */
    ksi_linux_devices_refresh_indicator_state();

    memset(&result, 0, sizeof(result));
    result.modifiers_lr = ksi_linux_devices_get_modifier_state();
    ksi_linux_devices_get_indicator_state(&caps, &num, &scroll);
    result.caps_lock   = caps   ? 1u : 0u;
    result.num_lock    = num    ? 1u : 0u;
    result.scroll_lock = scroll ? 1u : 0u;

    (void)ipc_send_locked(
        client->fd,
        KSI_MESSAGE_KEY_STATE_RESULT,
        message->header->client_id,
        message->header->correlation_id,
        &result,
        sizeof(result));
}

static void send_pointer_position_result(int client_fd, const ksi_message_header *request)
{
    ksi_pointer_position_payload result;

    memset(&result, 0, sizeof(result));
    (void)ksi_linux_devices_get_pointer_position(&result);

    (void)ipc_send_locked(
        client_fd,
        KSI_MESSAGE_POINTER_POSITION_RESULT,
        request->client_id,
        request->correlation_id,
        &result,
        sizeof(result));
}

static ssize_t find_client_index_by_fd(const ksi_daemon_state *state, int client_fd)
{
    if (state == NULL) {
        return -1;
    }

    for (nfds_t i = 0; i < state->client_count; i++) {
        if (state->clients[i].fd == client_fd) {
            return (ssize_t)i;
        }
    }

    return -1;
}

static ssize_t find_client_index_by_connection(
    const ksi_daemon_state *state,
    int client_fd,
    uint64_t connection_id)
{
    ssize_t index = find_client_index_by_fd(state, client_fd);

    if (index < 0 || connection_id == 0u) {
        return -1;
    }

    return state->clients[index].connection_id == connection_id ? index : -1;
}

static void add_client(ksi_daemon_state *state, const ksi_daemon_command *command)
{
    ksi_client *client;

    if (state == NULL || command == NULL) {
        return;
    }

    if (state->client_count >= KSI_MAX_CLIENTS) {
        request_close_client(state, command->client_fd);
        return;
    }

    client = &state->clients[state->client_count];
    memset(client, 0, sizeof(*client));
    client->fd = command->client_fd;
    client->hook_send_ref = hook_send_ref_create(command->client_fd);

    if (client->hook_send_ref == NULL) {
        fprintf(stderr, "inputd: failed to create hook send handle for client fd=%d: %s\n",
            command->client_fd, strerror(errno));
        request_close_client(state, command->client_fd);
        return;
    }

    client->connection_id = state->next_connection_id++;

    if (client->connection_id == 0u) {
        client->connection_id = state->next_connection_id++;
    }

    client->pid = command->credentials.pid;
    client->uid = command->credentials.uid;
    client->gid = command->credentials.gid;
    client->state = KSI_CLIENT_STATE_IDENTIFYING;
    state->client_count++;

    if (g_verbose) {
        printf("accepted client fd=%d pid=%ld uid=%ld gid=%ld (identifying...)\n",
            client->fd,
            (long)client->pid,
            (long)client->uid,
            (long)client->gid);
    }

    if (!start_identify_task(state->commands, client->pid, client->fd, client->connection_id)) {
        /* Worker thread failed to start; treat client as unidentifiable.
         * It will be refused capabilities at CLIENT_HELLO time. */
        fprintf(stderr,
            "inputd: failed to start identification thread for client pid=%ld\n",
            (long)client->pid);
        client->state = KSI_CLIENT_STATE_READY;
        client->has_identity = false;
    }
}

static void process_client_frame_command(ksi_daemon_state *state, const ksi_daemon_command *command)
{
    ssize_t client_index;
    ksi_binary_message_view message;

    if (state == NULL || command == NULL || command->data.frame.data == NULL) {
        return;
    }

    client_index = find_client_index_by_fd(state, command->client_fd);

    if (client_index < 0) {
        return;
    }

    if (command->data.frame.size < sizeof(ksi_message_header)) {
        remove_client(state, (nfds_t)client_index);
        return;
    }

    message.header = (const ksi_message_header *)(const void *)command->data.frame.data;
    message.payload = command->data.frame.data + sizeof(*message.header);
    message.payload_size = command->data.frame.size - sizeof(*message.header);
    handle_binary_message(state->backend, state, &state->clients[client_index], &message);
}

static void process_daemon_command(ksi_daemon_state *state, ksi_daemon_command *command)
{
    if (state == NULL || command == NULL) {
        return;
    }

    switch (command->type) {
        case KSI_DAEMON_COMMAND_CLIENT_CONNECTED:
            add_client(state, command);
            break;

        case KSI_DAEMON_COMMAND_CLIENT_FRAME:
            process_client_frame_command(state, command);
            break;

        case KSI_DAEMON_COMMAND_CLIENT_DISCONNECTED: {
            ssize_t client_index = find_client_index_by_fd(state, command->client_fd);

            if (client_index >= 0) {
                remove_client(state, (nfds_t)client_index);
            }
            break;
        }

        case KSI_DAEMON_COMMAND_CLIENT_IDENTIFIED:
            process_client_identified(state, command);
            break;

        case KSI_DAEMON_COMMAND_CLIENT_PROMPT_DONE:
            process_client_prompt_done(state, command);
            break;

        case KSI_DAEMON_COMMAND_LANE_HOOK_FAILURE: {
            ssize_t client_index = find_client_index_by_fd(state, command->client_fd);

            /* connection_id check guards against blaming a reused fd: if the
             * fd was recycled for a new client after the lane posted the
             * failure, skip the failure-count bump for that new client. */
            if (client_index >= 0
                && state->clients[client_index].connection_id == command->connection_id) {
                record_client_hook_failure(state, (nfds_t)client_index,
                    command->data.hook_failure.reason);
            }

            break;
        }
    }
}

static void process_daemon_commands(ksi_daemon_state *state)
{
    ksi_daemon_command command;

    if (state == NULL || state->commands == NULL) {
        return;
    }

    while (command_queue_pop(state->commands, &command)) {
        process_daemon_command(state, &command);
        free_daemon_command(&command);
    }
}

static void ipc_thread_remove_slot(ksi_ipc_slot *slots, nfds_t *count, nfds_t index)
{
    for (nfds_t j = index; j + 1 < *count; j++) {
        slots[j] = slots[j + 1];
    }

    (*count)--;
}

static void *ipc_thread_main(void *context)
{
    ksi_ipc_thread_context *ipc_context = context;
    ksi_daemon_command_queue *commands = ipc_context->commands;
    ksi_ipc_slot *ipc_slots;
    nfds_t ipc_slot_count = 0;

    ipc_slots = calloc(KSI_MAX_CLIENTS, sizeof(*ipc_slots));

    if (ipc_slots == NULL) {
        fprintf(stderr, "IPC thread: failed to allocate slot array\n");
        return NULL;
    }

    while (keep_running) {
        /* Layout: [server_fd, reverse_queue_fd, client_0_fd, ...] */
        struct pollfd fds[2 + KSI_MAX_CLIENTS];
        nfds_t count = 0;
        nfds_t reverse_queue_idx;
        nfds_t client_start;
        int poll_result;

        memset(fds, 0, sizeof(fds));

        fds[count].fd = ksi_ipc_server_fd(ipc_context->server);
        fds[count].events = POLLIN;
        count++;

        fds[count].fd = ipc_context->reverse_commands->ring.wake_read_fd;
        fds[count].events = POLLIN;
        reverse_queue_idx = count;
        count++;

        client_start = count;

        for (nfds_t i = 0; i < ipc_slot_count; i++) {
            fds[count].fd = ipc_slots[i].fd;
            fds[count].events = POLLIN;
            count++;
        }

        poll_result = poll(fds, count, 250);

        if (poll_result < 0) {
            if (errno == EINTR) {
                continue;
            }

            fprintf(stderr, "IPC poll failed: %s\n", strerror(errno));
            break;
        }

        if (poll_result == 0) {
            continue;
        }

        /* New connections */
        if ((fds[0].revents & POLLIN) != 0) {
            int client_fd = ksi_ipc_accept_client(ipc_context->server);

            if (client_fd >= 0) {
                ksi_ipc_peer_credentials credentials;
                ksi_daemon_command command;

                if (ipc_slot_count >= KSI_MAX_CLIENTS) {
                    /* Never registered with main thread; safe to close directly. */
                    ipc_close_locked(client_fd);
                } else if (ksi_ipc_get_peer_credentials(client_fd, &credentials) != 0) {
                    ipc_close_locked(client_fd);
                } else if (!ipc_context->system_service && credentials.uid != getuid()) {
                    fprintf(stderr,
                        "rejected client pid=%ld uid=%ld gid=%ld: daemon uid is %ld\n",
                        (long)credentials.pid,
                        (long)credentials.uid,
                        (long)credentials.gid,
                        (long)getuid());
                    ipc_close_locked(client_fd);
                } else {
                    memset(&command, 0, sizeof(command));
                    command.type = KSI_DAEMON_COMMAND_CLIENT_CONNECTED;
                    command.client_fd = client_fd;
                    command.credentials = credentials;

                    if (!command_queue_push(commands, &command)) {
                        /* Never successfully registered; close directly. */
                        ipc_close_locked(client_fd);
                    } else {
                        memset(&ipc_slots[ipc_slot_count], 0, sizeof(ipc_slots[ipc_slot_count]));
                        ipc_slots[ipc_slot_count].fd = client_fd;
                        ipc_slot_count++;
                    }
                }
            }
        }

        /* Reverse commands from main thread (e.g. CLOSE_CLIENT) */
        if ((fds[reverse_queue_idx].revents & POLLIN) != 0) {
            ksi_ipc_command rcmd;
            ipc_command_queue_drain_wake(ipc_context->reverse_commands);

            while (ipc_command_queue_pop(ipc_context->reverse_commands, &rcmd)) {
                if (rcmd.type == KSI_IPC_COMMAND_CLOSE_CLIENT && rcmd.client_fd >= 0) {
                    for (nfds_t j = 0; j < ipc_slot_count; j++) {
                        if (ipc_slots[j].fd == rcmd.client_fd) {
                            ipc_close_locked(ipc_slots[j].fd);
                            ipc_thread_remove_slot(ipc_slots, &ipc_slot_count, j);
                            break;
                        }
                    }
                }
            }
        }

        /* Client I/O */
        for (nfds_t i = 0; i < ipc_slot_count;) {
            struct pollfd *slot_poll = &fds[client_start + i];

            if ((slot_poll->revents & (POLLHUP | POLLERR | POLLNVAL)) != 0) {
                ksi_daemon_command command;

                memset(&command, 0, sizeof(command));
                command.type = KSI_DAEMON_COMMAND_CLIENT_DISCONNECTED;
                command.client_fd = ipc_slots[i].fd;
                (void)command_queue_push(commands, &command);

                /* Remove slot immediately so we stop polling this fd.
                 * Do NOT close(fd) — main thread will request_close_client
                 * which sends KSI_IPC_COMMAND_CLOSE_CLIENT back to us. */
                ipc_thread_remove_slot(ipc_slots, &ipc_slot_count, i);
                continue;
            }

            if ((slot_poll->revents & POLLIN) != 0) {
                int read_result = read_client_frames(commands, &ipc_slots[i]);

                if (read_result <= 0) {
                    ksi_daemon_command command;

                    memset(&command, 0, sizeof(command));
                    command.type = KSI_DAEMON_COMMAND_CLIENT_DISCONNECTED;
                    command.client_fd = ipc_slots[i].fd;
                    (void)command_queue_push(commands, &command);

                    ipc_thread_remove_slot(ipc_slots, &ipc_slot_count, i);
                    continue;
                }
            }

            i++;
        }
    }

    free(ipc_slots);
    return NULL;
}

/* Returns true when the daemon should exit due to the idle timeout. */
static bool check_idle_exit(
    const ksi_daemon_options *options,
    const ksi_daemon_state *state,
    uint64_t *idle_since_ms)
{
    uint64_t now;

    if (!options->system_service
        || state->client_count != 0
        || atomic_load(&g_worker_threads_running) != 0) {
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
    ksi_ipc_command_queue reverse_command_queue;
    pthread_t ipc_thread;
    bool ipc_thread_started = false;

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
    daemon_state->reverse_commands = &reverse_command_queue;
    daemon_state->permissions = permissions;
    daemon_state->available_capabilities = available_capabilities;
    daemon_state->next_connection_id = 1;
    daemon_state->next_event_id = 1;

    ksi_ipc_thread_context ipc_context = {
        .server = server,
        .commands = &command_queue,
        .system_service = options->system_service,
        .reverse_commands = &reverse_command_queue,
    };

    if (command_queue_init(&command_queue) != 0) {
        free(daemon_state);
        ksi_ipc_server_close(server);
        ksi_permissions_destroy(permissions);
        backend->stop();
        return 1;
    }

    if (ipc_command_queue_init(&reverse_command_queue) != 0) {
        command_queue_destroy(&command_queue);
        free(daemon_state);
        ksi_ipc_server_close(server);
        ksi_permissions_destroy(permissions);
        backend->stop();
        return 1;
    }

    if (output_queue_init(&daemon_state->output_queue) != 0) {
        ipc_command_queue_destroy(&reverse_command_queue);
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
        ipc_command_queue_destroy(&reverse_command_queue);
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
        lane_shutdown(&daemon_state->kbd_lane);
        lane_shutdown(&daemon_state->mouse_lane);
        atomic_store(&daemon_state->sequencer_running, 0);
        output_queue_wake(&daemon_state->output_queue);
        pthread_join(daemon_state->sequencer_thread, NULL);
        daemon_state->sequencer_thread_started = false;
        output_queue_close(&daemon_state->output_queue);
        ipc_command_queue_destroy(&reverse_command_queue);
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

    if (pthread_create(&ipc_thread, NULL, ipc_thread_main, &ipc_context) != 0) {
        if (backend->set_hook_event_callback != NULL) {
            backend->set_hook_event_callback(NULL, NULL);
        }
        lane_shutdown(&daemon_state->kbd_lane);
        lane_shutdown(&daemon_state->mouse_lane);
        atomic_store(&daemon_state->sequencer_running, 0);
        output_queue_wake(&daemon_state->output_queue);
        pthread_join(daemon_state->sequencer_thread, NULL);
        daemon_state->sequencer_thread_started = false;
        output_queue_close(&daemon_state->output_queue);
        ipc_command_queue_destroy(&reverse_command_queue);
        command_queue_destroy(&command_queue);
        free(daemon_state);
        ksi_ipc_server_close(server);
        ksi_permissions_destroy(permissions);
        backend->stop();
        return 1;
    }

    ipc_thread_started = true;

    uint64_t idle_since_ms = 0u;

    while (keep_running) {
        struct pollfd fds[KSI_MAX_POLL_FDS];
        nfds_t count = 0;
        nfds_t backend_start;
        nfds_t backend_count;

        memset(fds, 0, sizeof(fds));
        fds[count].fd = command_queue.ring.wake_read_fd;
        fds[count].events = POLLIN;
        count++;

        backend_start = count;
        backend_count = backend->poll_fds == NULL
            ? 0
            : backend->poll_fds(&fds[count], KSI_MAX_POLL_FDS - count);
        count += backend_count;

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
            process_daemon_commands(daemon_state);

            if (check_idle_exit(options, daemon_state, &idle_since_ms)) {
                keep_running = 0;
            }

            continue;
        }

        if ((fds[0].revents & POLLIN) != 0) {
            command_queue_drain_wake(&command_queue);
        }

        for (nfds_t i = backend_start; i < backend_start + backend_count; i++) {
            if ((fds[i].revents & (POLLIN | POLLHUP | POLLERR | POLLNVAL)) != 0
                && backend->process_fd != NULL) {
                backend->process_fd(fds[i].fd);
            }
        }

        process_daemon_commands(daemon_state);

        if (options->system_service && daemon_state->client_count == 0
            && atomic_load(&g_worker_threads_running) == 0) {
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

    /* Shut down both hook lanes.  lane_shutdown sets a flag the lane threads
     * observe to bail out of any pending decision wait, then pushes a NULL
     * sentinel.  Anything still queued is finalized as PASS so physical events
     * reach the sequencer for replay rather than being lost. */
    lane_shutdown(&daemon_state->kbd_lane);
    lane_shutdown(&daemon_state->mouse_lane);

    /* Signal any in-progress permission prompts to abort so their worker
     * threads finish promptly rather than waiting up to 60 seconds. */
    ksi_permissions_cancel();

    /* Wake the main loop and the IPC thread so they observe keep_running=0. */
    command_queue_wake(&command_queue);
    {
        ksi_ipc_command wake_cmd;
        memset(&wake_cmd, 0, sizeof(wake_cmd));
        wake_cmd.type = KSI_IPC_COMMAND_CLOSE_CLIENT;
        wake_cmd.client_fd = -1;
        (void)ipc_command_queue_push(&reverse_command_queue, &wake_cmd);
    }

    if (ipc_thread_started) {
        pthread_join(ipc_thread, NULL);
    }

    /* Wait for all identify/prompt worker threads to finish before destroying
     * the command queue — workers hold a pointer to it and will use-after-free
     * if we destroy it while they are still running. */
    while (atomic_load(&g_worker_threads_running) > 0) {
        usleep(10000);
    }

    /* IPC thread has exited; safe to close remaining fds directly. */
    for (nfds_t i = 0; i < daemon_state->client_count; i++) {
        hook_send_ref_release(daemon_state->clients[i].hook_send_ref);
        daemon_state->clients[i].hook_send_ref = NULL;
        ipc_close_locked(daemon_state->clients[i].fd);
    }

    /* Stop and drain the output sequencer.  The shutdown replay above queued
     * the last batch of physical events through it, so we wait for the thread
     * to drain them before tearing down the backend. */
    if (daemon_state->sequencer_thread_started) {
        atomic_store(&daemon_state->sequencer_running, 0);
        output_queue_wake(&daemon_state->output_queue);
        pthread_join(daemon_state->sequencer_thread, NULL);
        daemon_state->sequencer_thread_started = false;
    }

    output_queue_close(&daemon_state->output_queue);
    ipc_command_queue_destroy(&reverse_command_queue);
    command_queue_destroy(&command_queue);
    free(daemon_state);
    ksi_ipc_server_close(server);
    ksi_permissions_destroy(permissions);
    backend->stop();

    return 0;
}
