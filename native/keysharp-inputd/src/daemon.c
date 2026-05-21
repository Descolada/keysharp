#include "keysharp_inputd/daemon.h"

#include "keysharp_inputd/ipc.h"
#include "keysharp_inputd/linux_devices.h"
#include "keysharp_inputd/permissions.h"
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
#include <time.h>
#include <unistd.h>

#define KSI_MAX_CLIENTS 64
#define KSI_MAX_BACKEND_FDS 160
#define KSI_MAX_POLL_FDS (1 + KSI_MAX_BACKEND_FDS + KSI_MAX_CLIENTS)
#define KSI_MAX_PENDING_HOOK_EVENTS 128
#define KSI_MAX_PENDING_COMMANDS 256
#define KSI_MAX_MODIFY_INPUTS 32
#define KSI_MAX_SYNTH_INPUTS 1024
#define KSI_HOOK_DECISION_TIMEOUT_MS 1000u
#define KSI_MAX_CONSECUTIVE_HOOK_FAILURES 10u
#define KSI_IDLE_EXIT_MS 30000u

static volatile sig_atomic_t keep_running = 1;

/* Counts identify_worker and prompt_worker threads currently running.
 * The shutdown path waits for this to reach zero before destroying the
 * command queue those threads hold a pointer to. */
static atomic_int g_worker_threads_running = 0;

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
    pid_t pid;
    uid_t uid;
    gid_t gid;
    ksi_client_state state;
    bool has_identity;
    bool authenticated;
    uint32_t granted_capabilities;
    uint32_t hook_subscriptions;
    uint32_t consecutive_hook_failures;
    char exe_path[KSI_PERMISSION_MAX_PATH];
    char command_line[KSI_PERMISSION_MAX_COMMAND_LINE];
    char exe_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
    /* Buffered CLIENT_HELLO waiting for identification or prompt to complete. */
    bool pending_hello_valid;
    uint32_t pending_hello_requested;
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

typedef struct ksi_pending_hook_event {
    uint64_t order_id;
    uint64_t event_id;
    uint32_t hook_type;
    ksi_hook_event_payload payload;
    size_t payload_size;
} ksi_pending_hook_event;

typedef struct ksi_pending_synth_request {
    uint64_t order_id;
    uint32_t flags;
    uint32_t count;
    ksi_input *inputs;
} ksi_pending_synth_request;

typedef enum ksi_daemon_command_type {
    KSI_DAEMON_COMMAND_CLIENT_CONNECTED,
    KSI_DAEMON_COMMAND_CLIENT_FRAME,
    KSI_DAEMON_COMMAND_CLIENT_DISCONNECTED,
    KSI_DAEMON_COMMAND_CLIENT_IDENTIFIED, /* worker -> main: identity resolution complete */
    KSI_DAEMON_COMMAND_CLIENT_PROMPT_DONE, /* worker → main: user permission prompt complete */
} ksi_daemon_command_type;

typedef struct ksi_daemon_command {
    ksi_daemon_command_type type;
    int client_fd;
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
    (void)fcntl(ring->wake_read_fd, F_SETFL, fcntl(ring->wake_read_fd, F_GETFL) | O_NONBLOCK);
    (void)fcntl(ring->wake_write_fd, F_SETFL, fcntl(ring->wake_write_fd, F_GETFL) | O_NONBLOCK);
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
        uint8_t byte = 1;
        (void)write(ring->wake_write_fd, &byte, sizeof(byte));
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
    uint8_t byte = 1;

    if (ring == NULL || ring->wake_write_fd < 0) {
        return;
    }

    (void)write(ring->wake_write_fd, &byte, sizeof(byte));
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

typedef struct ksi_daemon_state {
    const ksi_platform_backend *backend;
    ksi_client clients[KSI_MAX_CLIENTS];
    nfds_t client_count;
    ksi_daemon_command_queue *commands;
    ksi_ipc_command_queue *reverse_commands;
    ksi_permission_store *permissions;
    uint32_t available_capabilities;
    uint64_t next_order_id;
    uint64_t next_event_id;
    bool pending_active;
    uint64_t pending_event_id;
    uint32_t pending_hook_type;
    uint32_t pending_final_decision;
    nfds_t pending_client_index;
    uint64_t pending_deadline_ms;
    ksi_hook_event_payload pending_payload;
    size_t pending_payload_size;
    ksi_input pending_modify_inputs[KSI_MAX_MODIFY_INPUTS];
    size_t pending_modify_input_count;
    ksi_pending_hook_event hook_queue[KSI_MAX_PENDING_HOOK_EVENTS];
    size_t hook_queue_head;
    size_t hook_queue_count;
    ksi_pending_synth_request synth_queue[KSI_MAX_PENDING_HOOK_EVENTS];
    size_t synth_queue_head;
    size_t synth_queue_count;
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

static bool send_pending_event_to_next_client(ksi_daemon_state *state);
static int update_grab_state(ksi_daemon_state *state);
static void clear_hook_state(ksi_daemon_state *state);
static void record_client_hook_failure(ksi_daemon_state *state, nfds_t index, const char *reason);
static void remove_client(ksi_daemon_state *state, nfds_t index);
static void send_indicator_state_result(int client_fd, const ksi_message_header *request);
static void send_pointer_position_result(int client_fd, const ksi_message_header *request);
static ssize_t find_client_index_by_fd(const ksi_daemon_state *state, int client_fd);
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
} ksi_identify_task;

typedef struct ksi_prompt_task {
    ksi_daemon_command_queue *commands;
    int client_fd;
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

    atomic_fetch_add(&g_worker_threads_running, 1);
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
    command.data.identified.result = result;

    if (!command_queue_push(task->commands, &command)) {
        free(result);
    }

    free(task);
    atomic_fetch_sub(&g_worker_threads_running, 1);
    return NULL;
}

static bool start_identify_task(ksi_daemon_command_queue *commands, pid_t pid, int client_fd)
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

    if (pthread_attr_init(&attr) != 0) {
        free(task);
        return false;
    }

    (void)pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);

    if (pthread_create(&thread, &attr, identify_worker, task) != 0) {
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

    atomic_fetch_add(&g_worker_threads_running, 1);
    memset(&command, 0, sizeof(command));
    command.type = KSI_DAEMON_COMMAND_CLIENT_PROMPT_DONE;
    command.client_fd = task->client_fd;
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

    if (pthread_create(&thread, &attr, prompt_worker, task) != 0) {
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

    if (state->pending_active && index == state->pending_client_index) {
        state->pending_deadline_ms = 0;
    } else if (state->pending_active && index < state->pending_client_index) {
        state->pending_client_index--;
    }

    for (nfds_t i = index; i + 1 < *count; i++) {
        clients[i] = clients[i + 1];
    }

    (*count)--;

    (void)update_grab_state(state);

    if (state->pending_active) {
        (void)send_pending_event_to_next_client(state);
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

static void clear_synth_queue(ksi_daemon_state *state)
{
    if (state == NULL) {
        return;
    }

    for (size_t i = 0; i < state->synth_queue_count; i++) {
        size_t idx = (state->synth_queue_head + i) % KSI_MAX_PENDING_HOOK_EVENTS;
        free(state->synth_queue[idx].inputs);
        state->synth_queue[idx].inputs = NULL;
    }

    state->synth_queue_head = 0;
    state->synth_queue_count = 0;
}

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

    (void)ksi_ipc_send_framed_message(
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

    (void)ksi_ipc_send_framed_message(
        client_fd,
        KSI_MESSAGE_CLIENT_HELLO,
        client_id,
        correlation_id,
        &payload,
        sizeof(payload));
}

static uint32_t expand_requested_capabilities(uint32_t requested)
{
    if ((requested & KSI_CAP_HOOK_KEYBOARD) != 0u) {
        requested |= KSI_CAP_SYNTH_KEYBOARD;
    }

    if ((requested & KSI_CAP_HOOK_MOUSE) != 0u) {
        requested |= KSI_CAP_SYNTH_MOUSE;
    }

    return requested;
}

/* Returns the capabilities currently granted from the store without prompting.
 * Sets *out_missing to the subset of expanded_requested that is not yet granted. */
static uint32_t check_capabilities_sync(
    ksi_daemon_state *state,
    const ksi_client *client,
    uint32_t requested,
    uint32_t *out_missing)
{
    uint32_t expanded;
    uint32_t granted;

    if (out_missing != NULL) {
        *out_missing = 0u;
    }

    if (state == NULL || client == NULL || requested == 0u) {
        return 0u;
    }

    expanded = expand_requested_capabilities(requested) & state->available_capabilities;

    if (expanded == 0u || !client->has_identity || state->permissions == NULL) {
        return 0u;
    }

    granted = ksi_permissions_get_allowed_capabilities(state->permissions, client->uid, client->exe_hash) & expanded;

    if (out_missing != NULL) {
        *out_missing = expanded & ~granted;
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

    client_index = find_client_index_by_fd(state, command->client_fd);

    if (client_index < 0) {
        return;
    }

    client = &state->clients[client_index];
    client->state = KSI_CLIENT_STATE_READY;

    if (!client->pending_hello_valid) {
        return;
    }

    client->pending_hello_valid = false;

    if (decision == KSI_PERMISSION_DECISION_ALLOW_ONCE) {
        (void)ksi_permissions_grant_session(
            state->permissions, client->uid, client->exe_hash, client->exe_path, missing);
    } else if (decision == KSI_PERMISSION_DECISION_ALLOW_ALWAYS) {
        (void)ksi_permissions_grant_persistent(
            state->permissions, client->uid, client->exe_hash, client->exe_path, missing);
    }

    granted = ksi_permissions_get_allowed_capabilities(state->permissions, client->uid, client->exe_hash)
              & (expand_requested_capabilities(requested) & state->available_capabilities);

    if (decision == KSI_PERMISSION_DECISION_DENY || (granted & requested) != requested) {
        client->authenticated = false;
        client->granted_capabilities = granted;
        send_hello_status(client->fd,
            client->pending_hello_client_id, client->pending_hello_correlation_id,
            -1, granted);
        return;
    }

    ksi_permissions_note_seen(state->permissions, client->uid, client->exe_hash, client->exe_path);
    client->authenticated = true;
    client->granted_capabilities = granted;
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
    client_index = find_client_index_by_fd(state, command->client_fd);

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
        uint32_t missing = 0u;
        uint32_t granted = check_capabilities_sync(state, client, requested, &missing);

        if (missing != 0u && state->permissions != NULL) {
            /* Need to prompt. Transition to AWAITING_PROMPT and start the worker. */
            if (!start_prompt_task(state->commands, client->fd, requested, missing,
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
        client->granted_capabilities = granted;
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
    return client->authenticated && (client->granted_capabilities & capability) == capability;
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

    if (state == NULL || state->backend == NULL || state->backend->set_grab_hook_mask == NULL) {
        return 0;
    }

    return state->backend->set_grab_hook_mask(hook_mask);
}

static bool pending_hook_event_is_injected(const ksi_daemon_state *state)
{
    if (state == NULL || !state->pending_active) {
        return false;
    }

    if (state->pending_hook_type == KSI_HOOK_KEYBOARD_LL) {
        return (state->pending_payload.event.keyboard.flags & KSI_LLKHF_INJECTED) != 0;
    }

    if (state->pending_hook_type == KSI_HOOK_MOUSE_LL) {
        return (state->pending_payload.event.mouse.flags & KSI_LLMHF_INJECTED) != 0;
    }

    return false;
}

static void clear_hook_state(ksi_daemon_state *state)
{
    if (state == NULL) {
        return;
    }

    for (nfds_t i = 0; i < state->client_count; i++) {
        state->clients[i].hook_subscriptions = 0;
        state->clients[i].consecutive_hook_failures = 0;
    }

    state->pending_active = false;
    state->pending_event_id = 0;
    state->pending_hook_type = 0;
    state->pending_final_decision = KSI_HOOK_DECISION_PASS;
    state->pending_client_index = 0;
    state->pending_deadline_ms = 0;
    state->pending_payload_size = 0;
    state->pending_modify_input_count = 0;
    state->hook_queue_head = 0;
    state->hook_queue_count = 0;
    clear_synth_queue(state);
    memset(&state->pending_payload, 0, sizeof(state->pending_payload));
    memset(state->pending_modify_inputs, 0, sizeof(state->pending_modify_inputs));
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
    (void)update_grab_state(state);
}

static void process_synth_request(ksi_daemon_state *state, ksi_pending_synth_request *request)
{
    if (state == NULL || request == NULL) {
        return;
    }

    if (state->backend != NULL && state->backend->send_input != NULL) {
        if (state->backend->send_input(request->inputs, request->count, request->flags) != 0) {
            fprintf(stderr, "inputd: queued synthesis request failed\n");
        }
    }

    free(request->inputs);
    request->inputs = NULL;
}

static bool enqueue_synth_request(
    ksi_daemon_state *state,
    const ksi_input *inputs,
    uint32_t count,
    uint32_t flags)
{
    ksi_pending_synth_request *request;

    if (state == NULL) {
        return false;
    }

    if (state->synth_queue_count >= KSI_MAX_PENDING_HOOK_EVENTS) {
        return false;
    }

    {
        size_t tail = (state->synth_queue_head + state->synth_queue_count) % KSI_MAX_PENDING_HOOK_EVENTS;
        request = &state->synth_queue[tail];
        state->synth_queue_count++;
    }
    memset(request, 0, sizeof(*request));
    request->order_id = state->next_order_id++;
    request->flags = flags;
    request->count = count;

    if (count != 0) {
        request->inputs = malloc((size_t)count * sizeof(inputs[0]));

        if (request->inputs == NULL) {
            state->synth_queue_count--;
            return false;
        }

        memcpy(request->inputs, inputs, (size_t)count * sizeof(inputs[0]));
    }

    return true;
}

static void pop_first_hook_event(ksi_daemon_state *state)
{
    const ksi_pending_hook_event *head;

    if (state == NULL || state->hook_queue_count == 0) {
        return;
    }

    head = &state->hook_queue[state->hook_queue_head];

    state->pending_event_id = head->event_id;
    state->pending_hook_type = head->hook_type;
    state->pending_final_decision = KSI_HOOK_DECISION_PASS;
    state->pending_client_index = 0;
    state->pending_deadline_ms = 0;
    state->pending_payload = head->payload;
    state->pending_payload_size = head->payload_size;
    state->pending_modify_input_count = 0;
    state->pending_active = true;

    state->hook_queue_head = (state->hook_queue_head + 1) % KSI_MAX_PENDING_HOOK_EVENTS;
    state->hook_queue_count--;
    (void)send_pending_event_to_next_client(state);
}

static void pop_first_synth_request(ksi_daemon_state *state)
{
    ksi_pending_synth_request request;

    if (state == NULL || state->synth_queue_count == 0) {
        return;
    }

    request = state->synth_queue[state->synth_queue_head];
    state->synth_queue_head = (state->synth_queue_head + 1) % KSI_MAX_PENDING_HOOK_EVENTS;
    state->synth_queue_count--;
    process_synth_request(state, &request);
}

static void process_next_queued_input(ksi_daemon_state *state)
{
    bool have_hook;
    bool have_synth;

    if (state == NULL) {
        return;
    }

    /* Loop so that consecutive synthesis requests are all drained before returning.
     * Without this loop, only the first queued synthesis item was processed per call:
     * the second synthesis request (e.g. the KeyDownAndUp that restores a toggle key)
     * would be stranded until a future finalize_pending_hook_event fired — which
     * never happens when the first synthesis used BypassHook (no loopback events). */
    while (!state->pending_active) {
        have_hook = state->hook_queue_count > 0;
        have_synth = state->synth_queue_count > 0;

        if (!have_hook && !have_synth) {
            return;
        }

        if (have_hook
            && (!have_synth || state->hook_queue[state->hook_queue_head].order_id
                               <= state->synth_queue[state->synth_queue_head].order_id)) {
            pop_first_hook_event(state);
        } else {
            pop_first_synth_request(state);
        }
    }
}

static void finalize_pending_hook_event(ksi_daemon_state *state, const char *reason)
{
    if (state == NULL || !state->pending_active) {
        return;
    }

    if (g_verbose) {
        printf("hook event %llu final decision=%u reason=%s\n",
            (unsigned long long)state->pending_event_id,
            state->pending_final_decision,
            reason);
    }

    if (state->pending_final_decision == KSI_HOOK_DECISION_PASS
        && !pending_hook_event_is_injected(state)
        && state->backend != NULL
        && state->backend->replay_hook_event != NULL) {
        if (state->backend->replay_hook_event(
                state->pending_hook_type,
                &state->pending_payload) != 0) {
            fprintf(stderr,
                "hook event %llu replay failed\n",
                (unsigned long long)state->pending_event_id);
        }
    } else if (state->pending_final_decision == KSI_HOOK_DECISION_MODIFY
        && state->backend != NULL
        && state->backend->send_input != NULL
        && state->pending_modify_input_count > 0) {
        if (state->backend->send_input(
                state->pending_modify_inputs,
                state->pending_modify_input_count,
                KSI_SYNTH_FLAG_BYPASS_HOOK) != 0) {
            fprintf(stderr,
                "hook event %llu modify synthesis failed\n",
                (unsigned long long)state->pending_event_id);
        }
    }

    state->pending_active = false;
    process_next_queued_input(state);
}

static bool client_matches_pending_event(const ksi_daemon_state *state, nfds_t index)
{
    uint32_t subscription_bit;

    if (state == NULL) {
        return false;
    }

    if (index >= state->client_count) {
        return false;
    }

    subscription_bit = hook_type_to_cap_bit(state->pending_hook_type);

    if (subscription_bit == 0) {
        return false;
    }

    return (state->clients[index].hook_subscriptions & subscription_bit) != 0;
}

static bool send_pending_event_to_next_client(ksi_daemon_state *state)
{
    if (state == NULL || !state->pending_active) {
        return false;
    }

    while (state->pending_client_index < state->client_count) {
        ksi_client *client = &state->clients[state->pending_client_index];

        if (!client_matches_pending_event(state, state->pending_client_index)) {
            state->pending_client_index++;
            continue;
        }

        if (ksi_ipc_send_framed_message(
                client->fd,
                KSI_MESSAGE_HOOK_EVENT,
                0,
                state->pending_event_id,
                &state->pending_payload,
                state->pending_payload_size) != 0) {
            record_client_hook_failure(state, state->pending_client_index, "send");
            state->pending_client_index++;
            continue;
        }

        state->pending_deadline_ms = monotonic_ms() + KSI_HOOK_DECISION_TIMEOUT_MS;
        return true;
    }

    finalize_pending_hook_event(state, "complete");
    return false;
}

static void start_pending_hook_event(
    ksi_daemon_state *state,
    uint32_t hook_type,
    const void *event,
    size_t event_size)
{
    if (state == NULL || event_size > sizeof(state->pending_payload.event)) {
        return;
    }

    if (state->pending_active) {
        ksi_pending_hook_event *queued_event;

        /* Coalesce consecutive relative MOUSEMOVE events in the queue.
         * At high polling rates (125–1000 Hz) many EV_REL frames can
         * accumulate while a keyboard hook decision is in flight.  Each
         * queued MOUSEMOVE requires a full C# round-trip (~5–10 ms), so
         * 100 queued moves = ~1 second of apparent mouse freeze after a
         * keyboard remap fires.  Merging them into a single entry with
         * the summed delta eliminates the backlog. */
        if (hook_type == KSI_HOOK_MOUSE_LL
            && state->hook_queue_count > 0)
        {
            size_t tail_idx = (state->hook_queue_head + state->hook_queue_count - 1)
                              % KSI_MAX_PENDING_HOOK_EVENTS;
            ksi_pending_hook_event *tail = &state->hook_queue[tail_idx];
            const ksi_mouse_hook_event *incoming =
                (const ksi_mouse_hook_event *)(const void *)event;

            if (tail->hook_type == KSI_HOOK_MOUSE_LL
                && tail->payload.event.mouse.message == KSI_WM_MOUSEMOVE
                && incoming->message == KSI_WM_MOUSEMOVE
                /* Only merge non-absolute, non-injected relative moves. */
                && (incoming->mouse_data & KSI_MOUSEEVENTF_ABSOLUTE) == 0
                && (incoming->flags & KSI_LLMHF_INJECTED) == 0
                && (tail->payload.event.mouse.flags & KSI_LLMHF_INJECTED) == 0)
            {
                tail->payload.event.mouse.x += incoming->x;
                tail->payload.event.mouse.y += incoming->y;
                tail->payload.event.mouse.time_ms = incoming->time_ms;
                return;
            }
        }

        if (state->hook_queue_count >= KSI_MAX_PENDING_HOOK_EVENTS) {
            fprintf(stderr, "hook event queue full; dropping event\n");
            return;
        }

        {
            size_t enq_idx = (state->hook_queue_head + state->hook_queue_count)
                             % KSI_MAX_PENDING_HOOK_EVENTS;
            queued_event = &state->hook_queue[enq_idx];
            state->hook_queue_count++;
        }
        memset(queued_event, 0, sizeof(*queued_event));
        queued_event->order_id = state->next_order_id++;
        queued_event->event_id = state->next_event_id++;
        queued_event->hook_type = hook_type;
        queued_event->payload.event_id = queued_event->event_id;
        queued_event->payload.hook_type = hook_type;
        memcpy(&queued_event->payload.event, event, event_size);
        queued_event->payload_size =
            sizeof(queued_event->payload.event_id)
            + sizeof(queued_event->payload.hook_type)
            + sizeof(queued_event->payload.reserved)
            + event_size;
        return;
    }

    memset(&state->pending_payload, 0, sizeof(state->pending_payload));
    state->pending_event_id = state->next_event_id++;
    state->next_order_id++;
    state->pending_hook_type = hook_type;
    state->pending_final_decision = KSI_HOOK_DECISION_PASS;
    state->pending_client_index = 0;
    state->pending_deadline_ms = 0;
    state->pending_payload.event_id = state->pending_event_id;
    state->pending_payload.hook_type = hook_type;
    memcpy(&state->pending_payload.event, event, event_size);
    state->pending_payload_size =
        sizeof(state->pending_payload.event_id)
        + sizeof(state->pending_payload.hook_type)
        + sizeof(state->pending_payload.reserved)
        + event_size;
    state->pending_modify_input_count = 0;
    state->pending_active = true;

    (void)send_pending_event_to_next_client(state);
}

static void process_hook_timeouts(ksi_daemon_state *state)
{
    if (state == NULL || !state->pending_active || state->pending_deadline_ms == 0) {
        return;
    }

    if (monotonic_ms() < state->pending_deadline_ms) {
        return;
    }

    if (g_verbose) {
        printf("hook event %llu timed out waiting for client index %lu\n",
            (unsigned long long)state->pending_event_id,
            (unsigned long)state->pending_client_index);
    }
    record_client_hook_failure(state, state->pending_client_index, "timeout");
    state->pending_client_index++;
    state->pending_deadline_ms = 0;
    (void)send_pending_event_to_next_client(state);
}

static int next_poll_timeout_ms(const ksi_daemon_state *state)
{
    uint64_t now;

    if (state == NULL || !state->pending_active || state->pending_deadline_ms == 0) {
        return 1000;
    }

    now = monotonic_ms();

    if (now >= state->pending_deadline_ms) {
        return 0;
    }

    if (state->pending_deadline_ms - now > 1000u) {
        return 1000;
    }

    return (int)(state->pending_deadline_ms - now);
}

static void daemon_handle_hook_event(
    void *context,
    uint32_t hook_type,
    const void *event,
    size_t event_size)
{
    ksi_daemon_state *state = context;
    uint32_t subscription_bit = hook_type_to_cap_bit(hook_type);

    if (state == NULL || subscription_bit == 0) {
        return;
    }

    if (!any_matching_hook_subscriptions(state, hook_type)) {
        return;
    }

    start_pending_hook_event(state, hook_type, event, event_size);
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

    if (message->header->type == KSI_MESSAGE_CLIENT_HELLO) {
        const ksi_client_hello_payload *payload;
        uint32_t requested = 0;
        uint32_t missing = 0u;
        uint32_t granted;

        if (message->payload_size >= sizeof(*payload)) {
            payload = (const ksi_client_hello_payload *)(const void *)message->payload;
            requested = payload->requested_capabilities;
        }

        /* If identification is still in progress, buffer the hello and defer. */
        if (client->state == KSI_CLIENT_STATE_IDENTIFYING) {
            client->pending_hello_valid = true;
            client->pending_hello_requested = requested;
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
        granted = check_capabilities_sync(state, client, requested, &missing);

        /* If there are missing capabilities and a permissions store is configured,
         * start an async prompt. Response will be sent from process_client_prompt_done. */
        if (missing != 0u && state->permissions != NULL) {
            if (!start_prompt_task(state->commands, client->fd, requested, missing,
                                   client->exe_path, client->command_line, client->exe_hash,
                                   client->pid, client->uid, client->gid)) {
                send_hello_status(client->fd, message->header->client_id, message->header->correlation_id, -1, granted);
                return;
            }

            client->state = KSI_CLIENT_STATE_AWAITING_PROMPT;
            client->pending_hello_valid = true;
            client->pending_hello_requested = requested;
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
        client->granted_capabilities = granted;
        send_hello_status(client->fd, message->header->client_id, message->header->correlation_id, 0, granted);
        return;
    }

    if (message->header->type == KSI_MESSAGE_HEARTBEAT) {
        send_status(client->fd, message->header, KSI_MESSAGE_HEARTBEAT, 0, 0);
        return;
    }

    if (message->header->type == KSI_MESSAGE_EMERGENCY_PASSTHROUGH) {
        if (!client->authenticated) {
            send_status(client->fd, message->header, KSI_MESSAGE_EMERGENCY_PASSTHROUGH, -1, 403);
            return;
        }

        clear_hook_state(state);

        if (state != NULL
            && state->backend != NULL
            && state->backend->set_grab_hook_mask != NULL
            && state->backend->set_grab_hook_mask(0) != 0) {
            send_status(client->fd, message->header, KSI_MESSAGE_EMERGENCY_PASSTHROUGH, -1, 1);
            return;
        }

        send_status(client->fd, message->header, KSI_MESSAGE_EMERGENCY_PASSTHROUGH, 0, 0);
        return;
    }

    if (message->header->type == KSI_MESSAGE_GET_INDICATOR_STATE) {
        /* Require hook capability: indicator/key state queries can expose physical
         * input information and must not be available to unprivileged connections. */
        if (!client->authenticated
            || (client->granted_capabilities
                & (KSI_CAP_HOOK_KEYBOARD | KSI_CAP_HOOK_MOUSE)) == 0) {
            send_status(client->fd, message->header, KSI_MESSAGE_INDICATOR_STATE_RESULT, -1, 403);
            return;
        }

        send_indicator_state_result(client->fd, message->header);
        return;
    }

    if (message->header->type == KSI_MESSAGE_GET_POINTER_POSITION) {
        /* A pointer query exposes physical input state just like hook events. */
        if (!client->authenticated
            || (client->granted_capabilities & KSI_CAP_HOOK_MOUSE) == 0) {
            send_status(client->fd, message->header, KSI_MESSAGE_POINTER_POSITION_RESULT, -1, 403);
            return;
        }

        send_pointer_position_result(client->fd, message->header);
        return;
    }

    if (message->header->type == KSI_MESSAGE_SUBSCRIBE_HOOK
        || message->header->type == KSI_MESSAGE_UNSUBSCRIBE_HOOK) {
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

        /* Only the hook capability is required to subscribe. The backend exposes
         * hook capability only when it can replay grabbed pass-through events.
         * Arbitrary input synthesis remains a separate client capability. */
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
        return;
    }

    if (message->header->type == KSI_MESSAGE_HOOK_DECISION) {
        const ksi_hook_decision_payload *payload;
        size_t expected_size;
        nfds_t client_index;

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

        if (state == NULL || !state->pending_active || payload->event_id != state->pending_event_id) {
            send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 2);
            return;
        }

        client_index = state->pending_client_index;

        if (client_index >= state->client_count || &state->clients[client_index] != client) {
            send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 3);
            return;
        }

        record_client_hook_success(state, client_index);

        if (payload->decision == KSI_HOOK_DECISION_MODIFY) {
            if (!client_has_capability(
                    client,
                    required_synthesis_capabilities(payload->inputs, payload->input_count))) {
                send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 403);
                return;
            }

            memcpy(
                state->pending_modify_inputs,
                payload->inputs,
                (size_t)payload->input_count * sizeof(payload->inputs[0]));
            state->pending_modify_input_count = payload->input_count;
            state->pending_final_decision = payload->decision;
        } else if (payload->decision == KSI_HOOK_DECISION_BLOCK) {
            state->pending_modify_input_count = 0;
            state->pending_final_decision = payload->decision;
        } else {
            state->pending_modify_input_count = 0;
        }

        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, 0, state->pending_final_decision);

        if (state->pending_final_decision == KSI_HOOK_DECISION_BLOCK
            || state->pending_final_decision == KSI_HOOK_DECISION_MODIFY) {
            finalize_pending_hook_event(state, "client-decision");
            return;
        }

        state->pending_client_index++;
        state->pending_deadline_ms = 0;
        (void)send_pending_event_to_next_client(state);
        return;
    }

    if (message->header->type == KSI_MESSAGE_SYNTHESIZE_INPUT) {
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

        if (!enqueue_synth_request(state, payload->inputs, payload->count, payload->flags)) {
            send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1, 12);
            return;
        }

        send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, 0, 0);

        process_next_queued_input(state);
        return;
    }

    send_status(client->fd, message->header, message->header->type, -1, 404);
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

    (void)ksi_ipc_send_framed_message(
        client_fd,
        KSI_MESSAGE_INDICATOR_STATE_RESULT,
        request->client_id,
        request->correlation_id,
        &result,
        sizeof(result));
}

static void send_pointer_position_result(int client_fd, const ksi_message_header *request)
{
    ksi_pointer_position_payload result;

    memset(&result, 0, sizeof(result));
    (void)ksi_linux_devices_get_pointer_position(&result);

    (void)ksi_ipc_send_framed_message(
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

    if (!start_identify_task(state->commands, client->pid, client->fd)) {
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
                    ksi_ipc_close_client(client_fd);
                } else if (ksi_ipc_get_peer_credentials(client_fd, &credentials) != 0) {
                    ksi_ipc_close_client(client_fd);
                } else if (!ipc_context->system_service && credentials.uid != getuid()) {
                    fprintf(stderr,
                        "rejected client pid=%ld uid=%ld gid=%ld: daemon uid is %ld\n",
                        (long)credentials.pid,
                        (long)credentials.uid,
                        (long)credentials.gid,
                        (long)getuid());
                    ksi_ipc_close_client(client_fd);
                } else {
                    memset(&command, 0, sizeof(command));
                    command.type = KSI_DAEMON_COMMAND_CLIENT_CONNECTED;
                    command.client_fd = client_fd;
                    command.credentials = credentials;

                    if (!command_queue_push(commands, &command)) {
                        /* Never successfully registered; close directly. */
                        ksi_ipc_close_client(client_fd);
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
                            close(ipc_slots[j].fd);
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

    printf("keysharp-inputd listening on %s using %s backend\n",
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
    daemon_state->next_order_id = 1;
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

    if (backend->set_hook_event_callback != NULL) {
        backend->set_hook_event_callback(daemon_handle_hook_event, daemon_state);
    }

    if (pthread_create(&ipc_thread, NULL, ipc_thread_main, &ipc_context) != 0) {
        ipc_command_queue_destroy(&reverse_command_queue);
        command_queue_destroy(&command_queue);
        free(daemon_state);
        ksi_ipc_server_close(server);
        ksi_permissions_destroy(permissions);
        backend->stop();
        return 1;
    }

    ipc_thread_started = true;

    uint64_t idle_since_ms = monotonic_ms();

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

        int poll_result = poll(fds, count, next_poll_timeout_ms(daemon_state));

        if (poll_result < 0) {
            if (errno == EINTR) {
                continue;
            }

            fprintf(stderr, "poll failed: %s\n", strerror(errno));
            break;
        }

        if (poll_result == 0) {
            process_hook_timeouts(daemon_state);
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

        process_hook_timeouts(daemon_state);
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
        ksi_ipc_close_client(daemon_state->clients[i].fd);
    }

    clear_synth_queue(daemon_state);
    ipc_command_queue_destroy(&reverse_command_queue);
    command_queue_destroy(&command_queue);
    free(daemon_state);
    ksi_ipc_server_close(server);
    ksi_permissions_destroy(permissions);
    backend->stop();

    return 0;
}
