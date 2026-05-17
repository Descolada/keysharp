#include "keysharp_inputd/daemon.h"

#include "keysharp_inputd/ipc.h"
#include "keysharp_inputd/platform.h"
#include "keysharp_inputd/protocol.h"

#include <errno.h>
#include <poll.h>
#include <signal.h>
#include <stdbool.h>
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
#define KSI_MAX_MODIFY_INPUTS 32
#define KSI_HOOK_DECISION_TIMEOUT_MS 1000u
#define KSI_MAX_CONSECUTIVE_HOOK_FAILURES 10u

static volatile sig_atomic_t keep_running = 1;

typedef struct ksi_client {
    int fd;
    pid_t pid;
    uid_t uid;
    gid_t gid;
    bool authenticated;
    uint32_t granted_capabilities;
    uint32_t hook_subscriptions;
    uint32_t consecutive_hook_failures;
    uint8_t rx_buffer[KSI_MAX_MESSAGE_SIZE];
    size_t rx_used;
} ksi_client;

typedef struct ksi_pending_hook_event {
    uint64_t event_id;
    uint32_t hook_type;
    ksi_hook_event_payload payload;
    size_t payload_size;
} ksi_pending_hook_event;

typedef struct ksi_daemon_state {
    const ksi_platform_backend *backend;
    ksi_client *clients;
    nfds_t *client_count;
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
    size_t hook_queue_count;
} ksi_daemon_state;

typedef struct ksi_binary_message_view {
    const ksi_message_header *header;
    const uint8_t *payload;
    size_t payload_size;
} ksi_binary_message_view;

static bool send_pending_event_to_next_client(ksi_daemon_state *state);
static int update_grab_state(ksi_daemon_state *state);
static void clear_hook_state(ksi_daemon_state *state);
static void record_client_hook_failure(ksi_daemon_state *state, nfds_t index, const char *reason);

static void handle_signal(int signal_number)
{
    (void)signal_number;
    keep_running = 0;
}

static int install_signal_handlers(void)
{
    struct sigaction action;

    memset(&action, 0, sizeof(action));
    action.sa_handler = handle_signal;

    if (sigemptyset(&action.sa_mask) != 0) {
        return -1;
    }

    if (sigaction(SIGINT, &action, NULL) != 0) {
        return -1;
    }

    if (sigaction(SIGTERM, &action, NULL) != 0) {
        return -1;
    }

    return 0;
}

static void remove_client(ksi_daemon_state *state, nfds_t index)
{
    ksi_client *clients = state->clients;
    nfds_t *count = state->client_count;

    ksi_ipc_close_client(clients[index].fd);

    if (state->pending_active && index == state->pending_client_index) {
        state->pending_deadline_ms = 0;
    } else if (state->pending_active && index < state->pending_client_index) {
        state->pending_client_index--;
    }

    for (nfds_t i = index; i + 1 < *count; i++) {
        clients[i] = clients[i + 1];
    }

    (*count)--;

    if (state->pending_active && index == state->pending_client_index) {
        (void)send_pending_event_to_next_client(state);
    }

    (void)update_grab_state(state);
}

static uint64_t monotonic_ms(void)
{
    struct timespec time_value;

    if (clock_gettime(CLOCK_MONOTONIC, &time_value) != 0) {
        return 0;
    }

    return ((uint64_t)time_value.tv_sec * 1000u) + ((uint64_t)time_value.tv_nsec / 1000000u);
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
    const ksi_message_header *request,
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
        request->client_id,
        request->correlation_id,
        &payload,
        sizeof(payload));
}

static uint32_t daemon_available_capabilities(void)
{
    uint32_t capabilities = KSI_CAP_SYNTH_KEYBOARD | KSI_CAP_SYNTH_MOUSE;

    if (access("/dev/input", R_OK | X_OK) == 0) {
        capabilities |= KSI_CAP_HOOK_KEYBOARD | KSI_CAP_HOOK_MOUSE;
    }

    if (access("/dev/uinput", W_OK) != 0) {
        capabilities &= (uint32_t)~(KSI_CAP_SYNTH_KEYBOARD | KSI_CAP_SYNTH_MOUSE);
    }

    return capabilities;
}

static bool client_has_capability(const ksi_client *client, uint32_t capability)
{
    return client->authenticated && (client->granted_capabilities & capability) == capability;
}

static uint32_t hook_type_to_capability(uint32_t hook_type)
{
    if (hook_type == KSI_HOOK_KEYBOARD_LL) {
        return KSI_CAP_HOOK_KEYBOARD;
    }

    if (hook_type == KSI_HOOK_MOUSE_LL) {
        return KSI_CAP_HOOK_MOUSE;
    }

    return 0;
}

static uint32_t hook_type_to_subscription_bit(uint32_t hook_type)
{
    if (hook_type == KSI_HOOK_KEYBOARD_LL) {
        return KSI_CAP_HOOK_KEYBOARD;
    }

    if (hook_type == KSI_HOOK_MOUSE_LL) {
        return KSI_CAP_HOOK_MOUSE;
    }

    return 0;
}

static uint32_t hook_type_to_replay_capability(uint32_t hook_type)
{
    if (hook_type == KSI_HOOK_KEYBOARD_LL) {
        return KSI_CAP_SYNTH_KEYBOARD;
    }

    if (hook_type == KSI_HOOK_MOUSE_LL) {
        return KSI_CAP_SYNTH_MOUSE;
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

static bool any_hook_subscriptions(const ksi_daemon_state *state)
{
    if (state == NULL || state->clients == NULL || state->client_count == NULL) {
        return false;
    }

    for (nfds_t i = 0; i < *state->client_count; i++) {
        if (state->clients[i].hook_subscriptions != 0) {
            return true;
        }
    }

    return false;
}

static bool any_matching_hook_subscriptions(const ksi_daemon_state *state, uint32_t hook_type)
{
    uint32_t subscription_bit;

    if (state == NULL || state->clients == NULL || state->client_count == NULL) {
        return false;
    }

    subscription_bit = hook_type_to_subscription_bit(hook_type);

    if (subscription_bit == 0) {
        return false;
    }

    for (nfds_t i = 0; i < *state->client_count; i++) {
        if ((state->clients[i].hook_subscriptions & subscription_bit) != 0) {
            return true;
        }
    }

    return false;
}

static int update_grab_state(ksi_daemon_state *state)
{
    bool enabled = any_hook_subscriptions(state);

    if (state == NULL || state->backend == NULL || state->backend->set_grab_enabled == NULL) {
        return 0;
    }

    return state->backend->set_grab_enabled(enabled);
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
    if (state == NULL || state->clients == NULL || state->client_count == NULL) {
        return;
    }

    for (nfds_t i = 0; i < *state->client_count; i++) {
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
    state->hook_queue_count = 0;
    memset(&state->pending_payload, 0, sizeof(state->pending_payload));
    memset(state->pending_modify_inputs, 0, sizeof(state->pending_modify_inputs));
}

static void record_client_hook_success(ksi_daemon_state *state, nfds_t index)
{
    if (state == NULL || state->clients == NULL || state->client_count == NULL || index >= *state->client_count) {
        return;
    }

    state->clients[index].consecutive_hook_failures = 0;
}

static void record_client_hook_failure(ksi_daemon_state *state, nfds_t index, const char *reason)
{
    ksi_client *client;

    if (state == NULL || state->clients == NULL || state->client_count == NULL || index >= *state->client_count) {
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

static void finalize_pending_hook_event(ksi_daemon_state *state, const char *reason)
{
    if (state == NULL || !state->pending_active) {
        return;
    }

    printf("hook event %llu final decision=%u reason=%s\n",
        (unsigned long long)state->pending_event_id,
        state->pending_final_decision,
        reason);

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
                state->pending_modify_input_count) != 0) {
            fprintf(stderr,
                "hook event %llu modify synthesis failed\n",
                (unsigned long long)state->pending_event_id);
        }
    }

    state->pending_active = false;

    if (state->hook_queue_count > 0) {
        state->pending_event_id = state->hook_queue[0].event_id;
        state->pending_hook_type = state->hook_queue[0].hook_type;
        state->pending_final_decision = KSI_HOOK_DECISION_PASS;
        state->pending_client_index = 0;
        state->pending_deadline_ms = 0;
        state->pending_payload = state->hook_queue[0].payload;
        state->pending_payload_size = state->hook_queue[0].payload_size;
        state->pending_modify_input_count = 0;
        state->pending_active = true;

        for (size_t i = 0; i + 1 < state->hook_queue_count; i++) {
            state->hook_queue[i] = state->hook_queue[i + 1];
        }

        state->hook_queue_count--;
        (void)send_pending_event_to_next_client(state);
    }
}

static bool client_matches_pending_event(const ksi_daemon_state *state, nfds_t index)
{
    uint32_t subscription_bit;

    if (state == NULL || state->clients == NULL || state->client_count == NULL) {
        return false;
    }

    if (index >= *state->client_count) {
        return false;
    }

    subscription_bit = hook_type_to_subscription_bit(state->pending_hook_type);

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

    while (state->pending_client_index < *state->client_count) {
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

        if (state->hook_queue_count >= KSI_MAX_PENDING_HOOK_EVENTS) {
            fprintf(stderr, "hook event queue full; dropping event\n");
            return;
        }

        queued_event = &state->hook_queue[state->hook_queue_count++];
        memset(queued_event, 0, sizeof(*queued_event));
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

    printf("hook event %llu timed out waiting for client index %lu\n",
        (unsigned long long)state->pending_event_id,
        (unsigned long)state->pending_client_index);
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
    uint32_t subscription_bit = hook_type_to_subscription_bit(hook_type);

    if (state == NULL || state->clients == NULL || state->client_count == NULL || subscription_bit == 0) {
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
    printf("client %d binary type=%u size=%u correlation=%llu\n",
        client->fd,
        message->header->type,
        message->header->size,
        (unsigned long long)message->header->correlation_id);

    if (message->header->type == KSI_MESSAGE_CLIENT_HELLO) {
        const ksi_client_hello_payload *payload;
        uint32_t requested = 0;
        uint32_t granted;

        if (message->payload_size >= sizeof(*payload)) {
            payload = (const ksi_client_hello_payload *)(const void *)message->payload;
            requested = payload->requested_capabilities;
        }

        granted = requested & daemon_available_capabilities();
        client->authenticated = true;
        client->granted_capabilities = granted;
        send_hello_status(client->fd, message->header, 0, granted);
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
            && state->backend->set_grab_enabled != NULL
            && state->backend->set_grab_enabled(false) != 0) {
            send_status(client->fd, message->header, KSI_MESSAGE_EMERGENCY_PASSTHROUGH, -1, 1);
            return;
        }

        send_status(client->fd, message->header, KSI_MESSAGE_EMERGENCY_PASSTHROUGH, 0, 0);
        return;
    }

    if (message->header->type == KSI_MESSAGE_SUBSCRIBE_HOOK
        || message->header->type == KSI_MESSAGE_UNSUBSCRIBE_HOOK) {
        const ksi_hook_subscription_payload *payload;
        uint32_t capability;
        uint32_t replay_capability;
        uint32_t subscription_bit;
        uint32_t old_subscriptions;

        if (message->payload_size != sizeof(*payload)) {
            send_status(client->fd, message->header, message->header->type, -1, 1);
            return;
        }

        payload = (const ksi_hook_subscription_payload *)(const void *)message->payload;
        capability = hook_type_to_capability(payload->hook_type);
        replay_capability = hook_type_to_replay_capability(payload->hook_type);
        subscription_bit = hook_type_to_subscription_bit(payload->hook_type);

        if (capability == 0 || replay_capability == 0 || subscription_bit == 0) {
            send_status(client->fd, message->header, message->header->type, -1, 2);
            return;
        }

        if (!client_has_capability(client, capability | replay_capability)) {
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

        if (state == NULL || !state->pending_active || payload->event_id != state->pending_event_id) {
            send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 2);
            return;
        }

        client_index = state->pending_client_index;

        if (client_index >= *state->client_count || &state->clients[client_index] != client) {
            send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 3);
            return;
        }

        record_client_hook_success(state, client_index);

        if (payload->decision == KSI_HOOK_DECISION_MODIFY) {
            if (payload->input_count == 0) {
                send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 7);
                return;
            }

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
        } else if (payload->decision != KSI_HOOK_DECISION_PASS) {
            send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 4);
            return;
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
        int result;

        if (message->payload_size < sizeof(*payload)) {
            send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1, 1);
            return;
        }

        payload = (const ksi_synthesize_input_payload *)(const void *)message->payload;

        if (payload->count > 1024u) {
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

        result = backend->send_input == NULL
            ? -1
            : backend->send_input(payload->inputs, payload->count);
        send_status(
            client->fd,
            message->header,
            KSI_MESSAGE_SYNTHESIS_RESULT,
            result == 0 ? 0 : -1,
            0);
        return;
    }

    send_status(client->fd, message->header, message->header->type, -1, 404);
}

static bool process_client_buffer(
    const ksi_platform_backend *backend,
    ksi_daemon_state *state,
    ksi_client *client)
{
    size_t offset = 0;

    while (client->rx_used - offset >= sizeof(ksi_message_header)) {
        const ksi_message_header *header =
            (const ksi_message_header *)(const void *)(client->rx_buffer + offset);
        ksi_binary_message_view message;

        if (header->size < sizeof(*header) || header->size > KSI_MAX_MESSAGE_SIZE) {
            fprintf(stderr, "client %d sent invalid frame size %u\n", client->fd, header->size);
            return false;
        }

        if (header->major != KSI_PROTOCOL_MAJOR || header->minor > KSI_PROTOCOL_MINOR) {
            fprintf(stderr,
                "client %d sent unsupported protocol version %u.%u\n",
                client->fd,
                header->major,
                header->minor);
            return false;
        }

        if (client->rx_used - offset < header->size) {
            break;
        }

        message.header = header;
        message.payload = client->rx_buffer + offset + sizeof(*header);
        message.payload_size = header->size - sizeof(*header);

        handle_binary_message(backend, state, client, &message);
        offset += header->size;
    }

    if (offset > 0) {
        if (offset < client->rx_used) {
            memmove(client->rx_buffer, client->rx_buffer + offset, client->rx_used - offset);
        }

        client->rx_used -= offset;
    }

    return true;
}

static int read_client_frames(
    const ksi_platform_backend *backend,
    ksi_daemon_state *state,
    ksi_client *client)
{
    for (;;) {
        ssize_t bytes_read;
        size_t available = sizeof(client->rx_buffer) - client->rx_used;

        if (available == 0) {
            fprintf(stderr, "client %d receive buffer overflow\n", client->fd);
            return -1;
        }

        bytes_read = read(client->fd, client->rx_buffer + client->rx_used, available);

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

        client->rx_used += (size_t)bytes_read;

        if (!process_client_buffer(backend, state, client)) {
            return -1;
        }

        if ((size_t)bytes_read < available) {
            return 1;
        }
    }
}

int ksi_daemon_run(const ksi_daemon_options *options)
{
    ksi_ipc_server *server = NULL;
    const ksi_platform_backend *backend = ksi_platform_backend_get();

    if (options == NULL || options->socket_path == NULL) {
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

    if (ksi_ipc_server_open(options->socket_path, &server) != 0) {
        backend->stop();
        return 1;
    }

    printf("keysharp-inputd listening on %s using %s backend\n",
        options->socket_path,
        backend->name);

    ksi_client clients[KSI_MAX_CLIENTS];
    nfds_t client_count = 0;
    uid_t daemon_uid = getuid();
    ksi_daemon_state daemon_state = {
        .backend = backend,
        .clients = clients,
        .client_count = &client_count,
        .next_event_id = 1,
        .pending_active = false,
    };

    memset(clients, 0, sizeof(clients));

    if (backend->set_hook_event_callback != NULL) {
        backend->set_hook_event_callback(daemon_handle_hook_event, &daemon_state);
    }

    while (keep_running) {
        struct pollfd fds[KSI_MAX_POLL_FDS];
        nfds_t count = 0;
        nfds_t backend_start;
        nfds_t backend_count;
        nfds_t client_start;

        memset(fds, 0, sizeof(fds));
        fds[count].fd = ksi_ipc_server_fd(server);
        fds[count].events = POLLIN;
        count++;

        backend_start = count;
        backend_count = backend->poll_fds == NULL
            ? 0
            : backend->poll_fds(&fds[count], KSI_MAX_POLL_FDS - count - client_count);
        count += backend_count;

        client_start = count;

        for (nfds_t i = 0; i < client_count; i++) {
            fds[count].fd = clients[i].fd;
            fds[count].events = POLLIN;
            count++;
        }

        int poll_result = poll(fds, count, next_poll_timeout_ms(&daemon_state));

        if (poll_result < 0) {
            if (errno == EINTR) {
                continue;
            }

            fprintf(stderr, "poll failed: %s\n", strerror(errno));
            break;
        }

        if (poll_result == 0) {
            process_hook_timeouts(&daemon_state);
            continue;
        }

        if ((fds[0].revents & POLLIN) != 0) {
            int client_fd = ksi_ipc_accept_client(server);

            if (client_fd >= 0) {
                ksi_ipc_peer_credentials credentials;

                if (client_count >= KSI_MAX_CLIENTS) {
                    ksi_ipc_close_client(client_fd);
                } else if (ksi_ipc_get_peer_credentials(client_fd, &credentials) != 0) {
                    ksi_ipc_close_client(client_fd);
                } else if (credentials.uid != daemon_uid) {
                    fprintf(stderr,
                        "rejected client pid=%ld uid=%ld gid=%ld: daemon uid is %ld\n",
                        (long)credentials.pid,
                        (long)credentials.uid,
                        (long)credentials.gid,
                        (long)daemon_uid);
                    ksi_ipc_close_client(client_fd);
                } else {
                    clients[client_count].fd = client_fd;
                    clients[client_count].pid = credentials.pid;
                    clients[client_count].uid = credentials.uid;
                    clients[client_count].gid = credentials.gid;
                    clients[client_count].authenticated = false;
                    clients[client_count].granted_capabilities = 0;
                    clients[client_count].hook_subscriptions = 0;
                    clients[client_count].consecutive_hook_failures = 0;
                    clients[client_count].rx_used = 0;
                    client_count++;
                    printf("accepted client fd=%d pid=%ld uid=%ld gid=%ld\n",
                        client_fd,
                        (long)credentials.pid,
                        (long)credentials.uid,
                        (long)credentials.gid);
                }
            }
        }

        for (nfds_t i = backend_start; i < backend_start + backend_count; i++) {
            if ((fds[i].revents & (POLLIN | POLLHUP | POLLERR | POLLNVAL)) != 0
                && backend->process_fd != NULL) {
                backend->process_fd(fds[i].fd);
            }
        }

        for (nfds_t i = 0; i < client_count;) {
            struct pollfd *client_poll_fd = &fds[client_start + i];

            if ((client_poll_fd->revents & (POLLHUP | POLLERR | POLLNVAL)) != 0) {
                remove_client(&daemon_state, i);
                continue;
            }

            if ((client_poll_fd->revents & POLLIN) != 0) {
                int read_result = read_client_frames(backend, &daemon_state, &clients[i]);

                if (read_result <= 0) {
                    remove_client(&daemon_state, i);
                    continue;
                }
            }

            i++;
        }

        process_hook_timeouts(&daemon_state);
    }

    for (nfds_t i = 0; i < client_count; i++) {
        ksi_ipc_close_client(clients[i].fd);
    }

    ksi_ipc_server_close(server);
    backend->stop();

    return 0;
}
