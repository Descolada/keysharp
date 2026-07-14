#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

bool g_verbose = false;

/* The daemon deliberately keeps queue and transaction helpers translation-unit
 * private. Including it here gives this white-box suite direct coverage without
 * widening the production API or duplicating its invariants in test code. */
#include "../src/daemon.c"

#define CHECK(condition) do { \
    if (!(condition)) { \
        fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #condition); \
        return false; \
    } \
} while (0)

static bool test_recursive_transaction_depth_boundary(void)
{
    ksi_daemon_state *state = calloc(1, sizeof(*state));
    ksi_nested_transaction *transaction;

    CHECK(state != NULL);
    state->keyboard_lane.state = state;
    state->keyboard_lane.hook_type = KSI_HOOK_KEYBOARD_LL;
    atomic_store(&state->keyboard_lane.current_hook_session_connection_id, 42u);
    atomic_store(&state->keyboard_lane.current_nesting_depth,
        KSI_MAX_RECURSION_DEPTH - 1u);

    transaction = calloc(1, sizeof(*transaction));
    CHECK(transaction != NULL);
    transaction->depth = KSI_MAX_RECURSION_DEPTH;
    transaction->origin_hook_connection_id = 42u;
    CHECK(lane_process_nested_transaction(
        &state->keyboard_lane, transaction, monotonic_ms() + 1000u));

    transaction = calloc(1, sizeof(*transaction));
    CHECK(transaction != NULL);
    transaction->depth = KSI_MAX_RECURSION_DEPTH + 1u;
    transaction->origin_hook_connection_id = 42u;
    CHECK(!lane_process_nested_transaction(
        &state->keyboard_lane, transaction, monotonic_ms() + 1000u));
    free(state);
    return true;
}

static bool test_subscriber_snapshot_is_newest_first(void)
{
    ksi_daemon_state *state = calloc(1, sizeof(*state));
    ksi_keyboard_hook_event hook_event;
    ksi_lane_event *event;
    int sockets[3][2];

    CHECK(state != NULL);
    memset(&hook_event, 0, sizeof(hook_event));
    state->client_count = 3u;
    state->next_event_id = 1u;

    for (size_t i = 0u; i < 3u; i++) {
        CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets[i]) == 0);
        state->clients[i].fd = sockets[i][0];
        state->clients[i].connection_id = i + 1u;
        state->clients[i].hook_subscriptions = KSI_CAP_HOOK_KEYBOARD;
        state->clients[i].hook_send_ref = hook_send_ref_create(sockets[i][0]);
        CHECK(state->clients[i].hook_send_ref != NULL);
    }

    event = create_hook_lane_event(state, KSI_HOOK_KEYBOARD_LL,
        &hook_event, sizeof(hook_event), NULL, NULL, 0u, 0u);
    CHECK(event != NULL);
    CHECK(event->subscriber_count == 3u);
    CHECK(event->subscribers[0].connection_id == 3u);
    CHECK(event->subscribers[1].connection_id == 2u);
    CHECK(event->subscribers[2].connection_id == 1u);
    lane_event_release_send_refs(event);
    free(event);

    for (size_t i = 0u; i < 3u; i++) {
        hook_send_ref_release(state->clients[i].hook_send_ref);
        close(sockets[i][0]);
        close(sockets[i][1]);
    }

    free(state);
    return true;
}

static bool test_fifth_quarantine_invalidates_session(void)
{
    ksi_daemon_state *state = calloc(1, sizeof(*state));
    int sockets[2];

    CHECK(state != NULL);
    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) == 0);
    CHECK(output_queue_init(&state->output_queue) == 0);
    state->client_count = 1u;
    state->clients[0].fd = sockets[0];
    state->clients[0].connection_id = 77u;
    state->clients[0].hook_subscriptions = KSI_CAP_HOOK_KEYBOARD;
    state->clients[0].hook_send_ref = hook_send_ref_create(sockets[0]);
    CHECK(state->clients[0].hook_send_ref != NULL);

    for (unsigned int strike = 1u;
            strike <= KSI_MAX_CONSECUTIVE_HOOK_FAILURES; strike++) {
        record_client_hook_failure(state, 0u, KSI_HOOK_KEYBOARD_LL,
            strike, 0u, KSI_HOOK_DECISION_TIMEOUT_MS,
            KSI_HOOK_QUARANTINE_REASON_TIMEOUT);

        if (strike < KSI_MAX_CONSECUTIVE_HOOK_FAILURES) {
            CHECK(state->client_count == 1u);
            CHECK(state->clients[0].consecutive_hook_failures[0] == strike);
            state->clients[0].quarantined_hooks &= (uint32_t)~KSI_CAP_HOOK_KEYBOARD;
            hook_send_ref_clear_stalled(state->clients[0].hook_send_ref, 0u);
        }
    }

    CHECK(state->client_count == 0u);
    output_queue_close(&state->output_queue);
    close(sockets[0]);
    close(sockets[1]);
    free(state);
    return true;
}

static bool test_synthetic_queue_rejects_atomically(void)
{
    ksi_synthetic_hook_queue *queue = calloc(1, sizeof(*queue));
    ksi_input inputs[2];

    CHECK(queue != NULL);
    CHECK(synthetic_hook_queue_init(queue) == 0);
    memset(inputs, 0, sizeof(inputs));
    inputs[0].type = KSI_INPUT_KEYBOARD;
    inputs[1].type = KSI_INPUT_KEYBOARD;
    queue->count = KSI_MAX_SYNTH_HOOK_ACTIONS - 1u;

    CHECK(!synthetic_hook_queue_push(queue, inputs, 2u, 2u, 123u, NULL));
    CHECK(queue->count == KSI_MAX_SYNTH_HOOK_ACTIONS - 1u);

    /* Avoid treating the artificial occupancy as initialized queue entries. */
    queue->count = 0u;
    synthetic_hook_queue_close(queue);
    free(queue);
    return true;
}

static bool test_output_queue_rejects_without_partial_admission(void)
{
    ksi_output_queue *queue = calloc(1, sizeof(*queue));
    ksi_input input;

    CHECK(queue != NULL);
    CHECK(output_queue_init(queue) == 0);
    memset(&input, 0, sizeof(input));
    input.type = KSI_INPUT_KEYBOARD;
    queue->synth_bytes = KSI_MAX_OUTPUT_SYNTH_BYTES;

    CHECK(!output_queue_push_synth(queue, &input, 1u, 0u));
    CHECK(queue->count == 0u);
    CHECK(queue->synth_bytes == KSI_MAX_OUTPUT_SYNTH_BYTES);

    queue->synth_bytes = 0u;
    output_queue_close(queue);
    free(queue);
    return true;
}

static bool test_lane_event_allocation_benchmark(void)
{
    const unsigned int iterations = 100000u;
    ksi_daemon_state *state = calloc(1, sizeof(*state));
    ksi_keyboard_hook_event event;
    struct timespec begin;
    struct timespec end;
    uint64_t elapsed_ns;

    CHECK(state != NULL);
    memset(&event, 0, sizeof(event));
    event.message = KSI_WM_KEYDOWN;
    event.vk_code = 0x41u;
    CHECK(clock_gettime(CLOCK_MONOTONIC, &begin) == 0);

    for (unsigned int i = 0u; i < iterations; i++) {
        ksi_lane_event *lane_event = create_hook_lane_event(
            state, KSI_HOOK_KEYBOARD_LL, &event, sizeof(event),
            NULL, NULL, 0u, 0u);
        CHECK(lane_event != NULL);
        free(lane_event);
    }

    CHECK(clock_gettime(CLOCK_MONOTONIC, &end) == 0);
    elapsed_ns = ((uint64_t)(end.tv_sec - begin.tv_sec) * 1000000000u)
        + (uint64_t)(end.tv_nsec - begin.tv_nsec);
    printf("lane_event allocation: %.1f ns/event (%u iterations)\n",
        (double)elapsed_ns / iterations, iterations);
    free(state);
    return true;
}

int main(void)
{
    const struct {
        const char *name;
        bool (*run)(void);
    } tests[] = {
        { "recursive transaction depth boundary", test_recursive_transaction_depth_boundary },
        { "subscriber snapshot newest first", test_subscriber_snapshot_is_newest_first },
        { "fifth quarantine invalidates session", test_fifth_quarantine_invalidates_session },
        { "synthetic queue atomic rejection", test_synthetic_queue_rejects_atomically },
        { "output queue atomic rejection", test_output_queue_rejects_without_partial_admission },
        { "lane event allocation benchmark", test_lane_event_allocation_benchmark },
    };

    for (size_t i = 0u; i < sizeof(tests) / sizeof(tests[0]); i++) {
        if (!tests[i].run()) {
            return 1;
        }

        printf("PASS %s\n", tests[i].name);
    }

    return 0;
}
