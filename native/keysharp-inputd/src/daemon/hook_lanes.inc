static uint64_t monotonic_ms(void)
{
    struct timespec time_value;

    if (clock_gettime(CLOCK_MONOTONIC, &time_value) != 0) {
        return 0;
    }

    return ((uint64_t)time_value.tv_sec * 1000u) + ((uint64_t)time_value.tv_nsec / 1000000u);
}

/* One FIFO sequencer owns all backend writes, preserving uinput order while
 * keeping slow output off the evdev/IPC loop. */

static int output_queue_init(ksi_output_queue *q)
{
    if (q == NULL) {
        return -1;
    }

    memset(q, 0, sizeof(*q));

    if (pthread_mutex_init(&q->mutex, NULL) != 0) {
        return -1;
    }

    q->mutex_initialized = true;

    if (ksi_wake_pipe_open(&q->wake_read_fd, &q->wake_write_fd) != 0) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
        return -1;
    }

    return 0;
}

static size_t output_action_synth_bytes(const ksi_output_action *action)
{
    return action != NULL && action->type == KSI_OUTPUT_ACTION_SYNTH
        ? (size_t)action->synth_count * sizeof(ksi_input)
        : 0u;
}

static void output_queue_push_action_locked(ksi_output_queue *q, const ksi_output_action *action)
{
    /* Caller holds q->mutex. */
    size_t tail = (q->head + q->count) % KSI_MAX_OUTPUT_ACTIONS;
    q->actions[tail] = *action;
    q->count++;
    q->synth_bytes += output_action_synth_bytes(action);
}

static bool output_queue_pop(ksi_output_queue *q, ksi_output_action *out)
{
    bool found = false;

    if (q == NULL || !q->mutex_initialized) {
        return false;
    }

    pthread_mutex_lock(&q->mutex);

    if (q->count != 0u) {
        *out = q->actions[q->head];
        memset(&q->actions[q->head], 0, sizeof(q->actions[q->head]));
        q->head = (q->head + 1u) % KSI_MAX_OUTPUT_ACTIONS;
        q->count--;
        q->synth_bytes -= output_action_synth_bytes(out);
        found = true;
    }

    pthread_mutex_unlock(&q->mutex);
    return found;
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
    if (q == NULL) {
        return;
    }

    for (size_t i = 0; i < q->count; i++) {
        size_t idx = (q->head + i) % KSI_MAX_OUTPUT_ACTIONS;

        if (q->actions[idx].type == KSI_OUTPUT_ACTION_SYNTH) {
            free(q->actions[idx].synth_inputs);
        }
    }

    q->head = 0u;
    q->count = 0u;
    q->synth_bytes = 0u;

    ksi_wake_pipe_close(&q->wake_read_fd, &q->wake_write_fd);

    if (q->mutex_initialized) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
    }
}

static bool output_queue_push_replay(
    ksi_output_queue *q,
    uint32_t hook_type,
    const ksi_hook_event_payload *payload)
{
    ksi_output_action action;

    if (q == NULL || payload == NULL) {
        return false;
    }

    memset(&action, 0, sizeof(action));
    action.type = KSI_OUTPUT_ACTION_REPLAY;
    action.hook_type = hook_type;
    action.replay_payload = *payload;

    pthread_mutex_lock(&q->mutex);

    if (q->count >= KSI_MAX_OUTPUT_ACTIONS) {
        pthread_mutex_unlock(&q->mutex);
        return false;
    }

    output_queue_push_action_locked(q, &action);
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
    ksi_output_action action;
    size_t synth_bytes;

    if (q == NULL || (count > 0u && inputs == NULL)) {
        return false;
    }

    synth_bytes = (size_t)count * sizeof(*inputs);
    memset(&action, 0, sizeof(action));
    action.type = KSI_OUTPUT_ACTION_SYNTH;
    action.synth_flags = flags;
    action.synth_count = count;

    if (count > 0u) {
        action.synth_inputs = malloc((size_t)count * sizeof(*inputs));

        if (action.synth_inputs == NULL) {
            return false;
        }

        memcpy(action.synth_inputs, inputs, (size_t)count * sizeof(*inputs));
    }

    pthread_mutex_lock(&q->mutex);

    if (q->count >= KSI_MAX_OUTPUT_ACTIONS
        || synth_bytes > KSI_MAX_OUTPUT_SYNTH_BYTES
        || q->synth_bytes > KSI_MAX_OUTPUT_SYNTH_BYTES - synth_bytes) {
        pthread_mutex_unlock(&q->mutex);
        free(action.synth_inputs);
        return false;
    }

    output_queue_push_action_locked(q, &action);

    /* Publish enqueue-time logical state in the same order as queued output. */
    ksi_linux_synth_note_enqueued_synth(inputs, count);

    pthread_mutex_unlock(&q->mutex);

    output_queue_wake(q);
    return true;
}

static bool output_queue_push_simple(ksi_output_queue *q, ksi_output_action_type type)
{
    ksi_output_action action = { .type = type };

    if (q == NULL || (type != KSI_OUTPUT_ACTION_RELEASE_ALL
            && type != KSI_OUTPUT_ACTION_RECREATE_SYNTH)) {
        return false;
    }

    pthread_mutex_lock(&q->mutex);

    if (q->count >= KSI_MAX_OUTPUT_ACTIONS) {
        pthread_mutex_unlock(&q->mutex);
        return false;
    }

    output_queue_push_action_locked(q, &action);

    if (type == KSI_OUTPUT_ACTION_RELEASE_ALL) {
        /* Release-all drops every synthetic key once accepted. */
        ksi_linux_synth_reset_enqueued_synth();
    }

    pthread_mutex_unlock(&q->mutex);

    output_queue_wake(q);
    return true;
}

/* Serialized with normal output so cleanup and device recreation cannot reorder. */
static bool output_queue_push_release_all(ksi_output_queue *q)
{
    return output_queue_push_simple(q, KSI_OUTPUT_ACTION_RELEASE_ALL);
}

static bool output_queue_push_recreate_synth(ksi_output_queue *q)
{
    return output_queue_push_simple(q, KSI_OUTPUT_ACTION_RECREATE_SYNTH);
}

static ksi_synth_completion *synth_completion_create(
    ksi_daemon_state *state,
    ksi_hook_send_ref *send_ref,
    const ksi_message_header *request)
{
    ksi_synth_completion *completion;

    if (send_ref == NULL || request == NULL || !hook_send_ref_acquire(send_ref)) {
        return NULL;
    }

    completion = calloc(1, sizeof(*completion));

    if (completion == NULL) {
        hook_send_ref_release(send_ref);
        return NULL;
    }

    completion->send_ref = send_ref;
    completion->state = state;
    completion->client_id = request->client_id;
    completion->correlation_id = request->correlation_id;
    atomic_init(&completion->remaining, 1u);
    atomic_init(&completion->result_sent, false);
    return completion;
}

static void synth_completion_begin_atomic(ksi_synth_completion *completion)
{
    if (completion == NULL || completion->owns_atomic_transaction) {
        return;
    }

    completion->owns_atomic_transaction = true;
    atomic_fetch_add(&completion->state->active_synthetic_transactions, 1u);
}

static void synth_completion_destroy(ksi_synth_completion *completion)
{
    if (completion == NULL) {
        return;
    }

    hook_send_ref_release(completion->send_ref);
    free(completion);
}

static void synth_completion_send_once(
    ksi_synth_completion *completion,
    int32_t status,
    uint32_t detail)
{
    ksi_status_payload payload;

    bool expected = false;

    if (completion == NULL
        || !atomic_compare_exchange_strong(&completion->result_sent, &expected, true)) {
        return;
    }

    payload.status = status;
    payload.detail = detail;
    (void)hook_send_ref_send(
        completion->send_ref,
        KSI_MESSAGE_SYNTHESIS_RESULT,
        completion->client_id,
        completion->correlation_id,
        &payload,
        sizeof(payload));
}

static void synth_completion_finish(
    ksi_synth_completion *completion,
    int32_t status,
    uint32_t detail,
    bool send_immediately)
{
    unsigned int previous;

    if (completion == NULL) {
        return;
    }

    if (send_immediately) {
        synth_completion_send_once(completion, status, detail);
    }

    previous = atomic_fetch_sub(&completion->remaining, 1u);

    if (previous != 1u) {
        return;
    }

    if (completion->owns_atomic_transaction && completion->state != NULL) {
        atomic_fetch_sub(&completion->state->active_synthetic_transactions, 1u);
    }

    if (!send_immediately) {
        synth_completion_send_once(completion, status, detail);
    }

    synth_completion_destroy(completion);
}

static void synth_completion_complete(ksi_synth_completion *completion)
{
    synth_completion_finish(completion, 0, 0u, false);
}

static void synth_completion_fail(
    ksi_synth_completion *completion, int32_t status, uint32_t detail)
{
    synth_completion_finish(completion, status, detail, true);
}

static int synthetic_hook_queue_init(ksi_synthetic_hook_queue *q)
{
    if (q == NULL) {
        return -1;
    }

    memset(q, 0, sizeof(*q));

    if (pthread_mutex_init(&q->mutex, NULL) != 0) {
        return -1;
    }

    q->mutex_initialized = true;

    if (ksi_wake_pipe_open(&q->wake_read_fd, &q->wake_write_fd) != 0) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
        return -1;
    }

    return 0;
}

static void synthetic_hook_queue_append_locked(
    ksi_synthetic_hook_queue *q,
    const ksi_input *input,
    uint64_t queued_at_ns,
    ksi_synth_completion *completion)
{
    size_t tail = (q->head + q->count) % KSI_MAX_SYNTH_HOOK_ACTIONS;
    q->items[tail].input = *input;
    q->items[tail].queued_at_ns = queued_at_ns;
    q->items[tail].completion = completion;
    q->items[tail].batch_start = false;  /* push() marks the batch's first node */
    q->count++;
}

static void synthetic_hook_queue_append_input_locked(
    ksi_synthetic_hook_queue *q,
    const ksi_input *input,
    uint64_t queued_at_ns,
    ksi_synth_completion *completion)
{
    ksi_input expanded[KSI_SYNTHETIC_HOOK_MAX_EXPANSION];
    size_t count = ksi_synthetic_hook_expand_input(input, expanded);

    for (size_t i = 0u; i < count; i++) {
        synthetic_hook_queue_append_locked(q, &expanded[i], queued_at_ns, completion);
    }
}

static bool synthetic_hook_queue_push(
    ksi_synthetic_hook_queue *q,
    const ksi_input *inputs,
    uint32_t count,
    size_t node_count,
    uint64_t queued_at_ns,
    ksi_synth_completion *completion)
{
    if (q == NULL || (count > 0u && inputs == NULL)) {
        return false;
    }

    pthread_mutex_lock(&q->mutex);

    if (q->count > KSI_MAX_SYNTH_HOOK_ACTIONS - node_count) {
        pthread_mutex_unlock(&q->mutex);
        return false;
    }

    if (completion != NULL) {
        atomic_store(&completion->remaining, (unsigned int)node_count);
    }

    size_t start_tail = (q->head + q->count) % KSI_MAX_SYNTH_HOOK_ACTIONS;

    for (uint32_t i = 0u; i < count; i++) {
        synthetic_hook_queue_append_input_locked(q, &inputs[i], queued_at_ns, completion);
    }

    /* Mark the first node of this client batch so the daemon re-arms its
     * per-batch pending-high-surrogate reset at the batch boundary (fragments
     * otherwise never reset). C# chunking is surrogate-aware, so a well-formed
     * pair is never split across two batches. */
    if (node_count != 0u) {
        q->items[start_tail].batch_start = true;
    }

    pthread_mutex_unlock(&q->mutex);

    if (node_count != 0u && q->wake_write_fd >= 0) {
        wake_pipe_write(q->wake_write_fd);
    }

    return true;
}

static bool synthetic_hook_queue_peek_time(
    ksi_synthetic_hook_queue *q,
    uint64_t *queued_at_ns)
{
    bool found = false;

    if (q == NULL || queued_at_ns == NULL || !q->mutex_initialized) {
        return false;
    }

    pthread_mutex_lock(&q->mutex);

    if (q->count != 0u) {
        *queued_at_ns = q->items[q->head].queued_at_ns;
        found = true;
    }

    pthread_mutex_unlock(&q->mutex);
    return found;
}

static bool synthetic_hook_queue_pop(
    ksi_synthetic_hook_queue *q,
    ksi_input *input,
    uint64_t *queued_at_ns,
    ksi_synth_completion **completion,
    bool *batch_start)
{
    ksi_synthetic_hook_item item;
    bool found = false;

    if (q == NULL || input == NULL || queued_at_ns == NULL
        || completion == NULL || batch_start == NULL || !q->mutex_initialized) {
        return false;
    }

    pthread_mutex_lock(&q->mutex);

    if (q->count != 0u) {
        item = q->items[q->head];
        memset(&q->items[q->head], 0, sizeof(q->items[q->head]));
        q->head = (q->head + 1u) % KSI_MAX_SYNTH_HOOK_ACTIONS;
        q->count--;
        found = true;
    } else {
        memset(&item, 0, sizeof(item));
    }

    pthread_mutex_unlock(&q->mutex);

    if (!found) {
        return false;
    }

    *input = item.input;
    *queued_at_ns = item.queued_at_ns;
    *completion = item.completion;
    *batch_start = item.batch_start;
    return true;
}

static void synthetic_hook_queue_wake(const ksi_synthetic_hook_queue *q)
{
    if (q == NULL || q->wake_write_fd < 0) {
        return;
    }

    wake_pipe_write(q->wake_write_fd);
}

static void synthetic_hook_queue_close(ksi_synthetic_hook_queue *q)
{
    if (q == NULL) {
        return;
    }

    for (size_t i = 0u; i < q->count; i++) {
        size_t idx = (q->head + i) % KSI_MAX_SYNTH_HOOK_ACTIONS;
        synth_completion_fail(q->items[idx].completion, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
        memset(&q->items[idx], 0, sizeof(q->items[idx]));
    }

    q->head = 0u;
    q->count = 0u;

    ksi_wake_pipe_close(&q->wake_read_fd, &q->wake_write_fd);

    if (q->mutex_initialized) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
    }
}

static void synthetic_hook_queue_complete_inflight(ksi_daemon_state *state)
{
    if (state == NULL) {
        return;
    }

    atomic_store(&state->synthetic_hook_inflight, 0);
    synthetic_hook_queue_wake(&state->synthetic_hook_queue);
}

static void request_fail_open(ksi_daemon_state *state)
{
    atomic_store(&g_fail_open_requested, 1);
    ksi_pipe_ring_wake(&state->commands->ring);
}

/* Drain queued backend writes until shutdown is requested and the queue is empty. */
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

        /* Drain before pop to avoid sleeping after a push+wake race. */
        ksi_wake_pipe_drain(state->output_queue.wake_read_fd);

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
            } else if (action.type == KSI_OUTPUT_ACTION_RELEASE_ALL) {
                if (state->backend != NULL && state->backend->release_synthetic_keys != NULL) {
                    state->backend->release_synthetic_keys();
                }
            } else if (action.type == KSI_OUTPUT_ACTION_RECREATE_SYNTH) {
                /* Recover a broken synthetic-output device ON THIS THREAD so the
                 * stop()+start() (which closes/reopens the uinput fds and clears
                 * the key-down table) never races the sequencer's own writes. */
                if (state->backend != NULL && state->backend->recreate_synth != NULL) {
                    state->backend->recreate_synth();
                }
            }
        }

        keep = atomic_load(&state->sequencer_running) != 0;

        if (!keep) {
            break;
        }

        /* Timeout observes shutdown even if a wake byte is lost. */
        (void)poll(&pfd, 1, 100);
    }

    return NULL;
}

/* Hook lane queues: event pointers in, one decision slot out. */

static void synthetic_hook_queue_abort(ksi_synthetic_hook_queue *q)
{
    if (q == NULL || !q->mutex_initialized) {
        return;
    }

    pthread_mutex_lock(&q->mutex);

    for (size_t i = 0u; i < q->count; i++) {
        size_t idx = (q->head + i) % KSI_MAX_SYNTH_HOOK_ACTIONS;
        synth_completion_fail(q->items[idx].completion, -1, KSI_DETAIL_CANCELLED);
        memset(&q->items[idx], 0, sizeof(q->items[idx]));
    }

    q->head = 0u;
    q->count = 0u;
    pthread_mutex_unlock(&q->mutex);
}

static uint64_t monotonic_ns(void)
{
    struct timespec time_value;

    if (clock_gettime(CLOCK_MONOTONIC, &time_value) != 0) {
        return 0;
    }

    return ((uint64_t)time_value.tv_sec * 1000000000u) + (uint64_t)time_value.tv_nsec;
}

static void lane_event_release_subscriber_ref(ksi_lane_event *event, size_t index)
{
    ksi_lane_subscriber *subscriber;

    if (event == NULL || index >= event->subscriber_count) {
        return;
    }

    subscriber = &event->subscribers[index];

    if (subscriber->send_ref == NULL) {
        return;
    }

    if (!subscriber->uses_callback_rpc) {
        hook_send_ref_complete_event(subscriber->send_ref, event->event_id);
    }

    hook_send_ref_release(subscriber->send_ref);
    subscriber->send_ref = NULL;
}

static void lane_event_release_send_refs(ksi_lane_event *event);

static void nested_transaction_destroy(ksi_nested_transaction *transaction)
{
    if (transaction == NULL) {
        return;
    }

    for (uint32_t i = 0; i < transaction->count; i++) {
        if (transaction->members[i].event != NULL) {
            lane_event_release_send_refs(transaction->members[i].event);
            free(transaction->members[i].event);
        }
    }

    free(transaction);
}

static void nested_transaction_queue_close(ksi_nested_transaction_queue *q)
{
    ksi_nested_transaction *transaction = NULL;

    if (q == NULL) {
        return;
    }

    while (ksi_pipe_ring_pop(&q->ring, &transaction)) {
        if (transaction != NULL && transaction->completion != NULL) {
            synth_completion_fail(transaction->completion, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
            transaction->completion = NULL;
        }

        nested_transaction_destroy(transaction);
    }

    ksi_pipe_ring_close(&q->ring);
}

static void lane_event_release_send_refs(ksi_lane_event *event)
{
    if (event == NULL) {
        return;
    }

    for (size_t i = 0; i < event->subscriber_count; i++) {
        lane_event_release_subscriber_ref(event, i);
    }
}

static void lane_action_queue_close(ksi_lane_action_queue *q)
{
    ksi_lane_event *event;

    if (q == NULL) {
        return;
    }

    while (ksi_pipe_ring_pop(&q->ring, &event)) {
        if (event == NULL) {
            continue;
        }

        lane_event_release_send_refs(event);

        if (event->synthetic_completion != NULL) {
            synth_completion_fail(event->synthetic_completion, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
        }

        free(event);
    }

    ksi_pipe_ring_close(&q->ring);
}

static int lane_decision_queue_init(ksi_lane_decision_queue *q)
{
    pthread_condattr_t cond_attr;
    int rc;

    if (q == NULL) {
        return -1;
    }

    memset(q, 0, sizeof(*q));

    if (pthread_mutex_init(&q->mutex, NULL) != 0) {
        return -1;
    }

    q->mutex_initialized = true;

    /* Hook deadlines use CLOCK_MONOTONIC so wall-clock jumps cannot extend grabs. */
    if (pthread_condattr_init(&cond_attr) != 0) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
        return -1;
    }

    (void)pthread_condattr_setclock(&cond_attr, CLOCK_MONOTONIC);
    rc = pthread_cond_init(&q->condition, &cond_attr);
    pthread_condattr_destroy(&cond_attr);

    if (rc != 0) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
        return -1;
    }

    q->condition_initialized = true;
    return 0;
}

static void lane_decision_queue_wake(ksi_lane_decision_queue *q)
{
    if (q == NULL || !q->mutex_initialized || !q->condition_initialized) {
        return;
    }

    pthread_mutex_lock(&q->mutex);
    pthread_cond_broadcast(&q->condition);
    pthread_mutex_unlock(&q->mutex);
}

static void lane_decision_queue_wait(ksi_lane_decision_queue *q, int timeout_ms)
{
    struct timespec absolute;

    if (q == NULL || !q->mutex_initialized || !q->condition_initialized
        || clock_gettime(CLOCK_MONOTONIC, &absolute) != 0) {
        return;
    }

    absolute.tv_sec += timeout_ms / 1000;
    absolute.tv_nsec += (long)(timeout_ms % 1000) * 1000000L;

    if (absolute.tv_nsec >= 1000000000L) {
        absolute.tv_sec++;
        absolute.tv_nsec -= 1000000000L;
    }

    pthread_mutex_lock(&q->mutex);

    if (!q->has_decision) {
        (void)pthread_cond_timedwait(&q->condition, &q->mutex, &absolute);
    }

    pthread_mutex_unlock(&q->mutex);
}

/* Ack before wake so HOOK_DECISION status cannot overtake the next HOOK_EVENT. */
static bool lane_decision_queue_push_with_ack(
    ksi_lane_decision_queue *q,
    const ksi_lane_decision *decision,
    ksi_hook_send_ref *send_ref,
    const ksi_message_header *request,
    uint32_t detail)
{
    ksi_status_payload ack_payload;

    if (q == NULL || decision == NULL || request == NULL || !q->mutex_initialized) {
        return false;
    }

    ack_payload.status = 0;
    ack_payload.detail = detail;

    pthread_mutex_lock(&q->mutex);

    if (q->has_decision) {
        pthread_mutex_unlock(&q->mutex);
        return false;
    }

    memcpy(&q->decision, decision, ksi_lane_decision_size(decision->input_count));
    q->has_decision = true;

    (void)hook_send_ref_send(
        send_ref, KSI_MESSAGE_HOOK_DECISION,
        request->client_id, request->correlation_id,
        &ack_payload, sizeof(ack_payload));

    /* Keep the queue locked through the acknowledgement: a timeout or spurious
     * wake must not let the lane emit the next HOOK_EVENT first. */
    pthread_cond_broadcast(&q->condition);
    pthread_mutex_unlock(&q->mutex);
    return true;
}

static bool lane_decision_queue_pop(ksi_lane_decision_queue *q, ksi_lane_decision *out)
{
    if (q == NULL || out == NULL || !q->mutex_initialized) {
        return false;
    }

    pthread_mutex_lock(&q->mutex);

    if (!q->has_decision) {
        pthread_mutex_unlock(&q->mutex);
        return false;
    }

    memcpy(out, &q->decision, ksi_lane_decision_size(q->decision.input_count));
    /* All readers are bounded by input_count; clearing it bounds any future read
     * to 0 entries without a 40KB memset. */
    q->decision.input_count = 0;
    q->has_decision = false;
    pthread_mutex_unlock(&q->mutex);
    return true;
}

static void lane_decision_queue_close(ksi_lane_decision_queue *q)
{
    if (q == NULL) {
        return;
    }

    q->has_decision = false;
    memset(&q->decision, 0, sizeof(q->decision));

    if (q->condition_initialized) {
        pthread_cond_destroy(&q->condition);
        q->condition_initialized = false;
    }

    if (q->mutex_initialized) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
    }
}

/* Post a lane failure to the main thread for per-client accounting. */
static void lane_post_hook_failure(
    ksi_daemon_state *state,
    uint32_t hook_type,
    uint64_t event_id,
    uint64_t hook_session_connection_id,
    uint32_t nesting_depth,
    uint32_t elapsed_ms,
    uint32_t reason)
{
    ksi_daemon_command cmd;

    if (state == NULL || state->commands == NULL) {
        return;
    }

    memset(&cmd, 0, sizeof(cmd));
    cmd.type = KSI_DAEMON_COMMAND_LANE_HOOK_FAILURE;
    cmd.data.hook_failure.hook_type = hook_type;
    cmd.data.hook_failure.event_id = event_id;
    cmd.data.hook_failure.hook_session_connection_id = hook_session_connection_id;
    cmd.data.hook_failure.nesting_depth = nesting_depth;
    cmd.data.hook_failure.elapsed_ms = elapsed_ms;
    cmd.data.hook_failure.reason = reason;

    if (!ksi_pipe_ring_push(&state->commands->ring, &cmd)) {
        request_fail_open(state);
    }
}

static bool lane_event_enqueue_egress(ksi_hook_lane *lane, const ksi_lane_event *event)
{
    if (lane == NULL || event == NULL) {
        return false;
    }

    if (event->egress.type == KSI_LANE_EGRESS_NONE) {
        return true;
    }

    if (event->egress.type == KSI_LANE_EGRESS_REPLAY) {
        return output_queue_push_replay(&lane->state->output_queue,
            event->hook_type, &event->payload);
    }

    if (event->egress.type == KSI_LANE_EGRESS_SYNTH) {
        return output_queue_push_synth(&lane->state->output_queue,
            event->egress.synth_inputs,
            event->egress.synth_count,
            event->egress.synth_flags);
    }

    return false;
}

static ksi_nested_event_waiter *nested_event_waiter_create(void)
{
    ksi_nested_event_waiter *waiter = calloc(1, sizeof(*waiter));
    pthread_condattr_t attributes;

    if (waiter == NULL) {
        return NULL;
    }

    if (pthread_mutex_init(&waiter->mutex, NULL) != 0) {
        free(waiter);
        return NULL;
    }

    if (pthread_condattr_init(&attributes) != 0) {
        pthread_mutex_destroy(&waiter->mutex);
        free(waiter);
        return NULL;
    }

    if (pthread_condattr_setclock(&attributes, CLOCK_MONOTONIC) != 0) {
        pthread_condattr_destroy(&attributes);
        pthread_mutex_destroy(&waiter->mutex);
        free(waiter);
        return NULL;
    }

    if (pthread_cond_init(&waiter->condition, &attributes) != 0) {
        pthread_condattr_destroy(&attributes);
        pthread_mutex_destroy(&waiter->mutex);
        free(waiter);
        return NULL;
    }

    pthread_condattr_destroy(&attributes);

    return waiter;
}

static void nested_event_waiter_destroy(ksi_nested_event_waiter *waiter)
{
    if (waiter == NULL) {
        return;
    }

    pthread_cond_destroy(&waiter->condition);
    pthread_mutex_destroy(&waiter->mutex);
    free(waiter);
}

/* Returns true when the waiting transaction owns waiter and must destroy it.
 * On timeout ownership transfers to the target lane, which frees it after the
 * already-admitted event reaches fail-open completion. */
static bool nested_event_waiter_wait(
    ksi_nested_event_waiter *waiter,
    uint64_t absolute_deadline_ms,
    bool *ok)
{
    struct timespec deadline = {
        .tv_sec = (time_t)(absolute_deadline_ms / 1000u),
        .tv_nsec = (long)((absolute_deadline_ms % 1000u) * 1000000u),
    };
    bool completed;

    pthread_mutex_lock(&waiter->mutex);

    while (waiter->state == KSI_NESTED_WAIT_PENDING) {
        int wait_result = pthread_cond_timedwait(
            &waiter->condition, &waiter->mutex, &deadline);

        if (wait_result != 0) {
            break;
        }
    }

    completed = waiter->state == KSI_NESTED_WAIT_COMPLETED
        || waiter->state == KSI_NESTED_WAIT_FAILED;

    if (completed) {
        *ok = waiter->state == KSI_NESTED_WAIT_COMPLETED;
    } else {
        waiter->state = KSI_NESTED_WAIT_ABANDONED;
        *ok = false;
    }

    pthread_mutex_unlock(&waiter->mutex);
    return completed;
}

/* Lock the waiter across output admission and completion publication. If the
 * origin lane won the deadline race, the target must suppress late output. */
static bool nested_event_waiter_begin_completion(ksi_nested_event_waiter *waiter)
{
    if (waiter == NULL) {
        return true;
    }

    pthread_mutex_lock(&waiter->mutex);
    return waiter->state != KSI_NESTED_WAIT_ABANDONED;
}

static void nested_event_waiter_finish_completion(
    ksi_nested_event_waiter *waiter,
    bool ok)
{
    bool destroy;

    if (waiter == NULL) {
        return;
    }

    destroy = waiter->state == KSI_NESTED_WAIT_ABANDONED;

    if (!destroy) {
        waiter->state = ok ? KSI_NESTED_WAIT_COMPLETED : KSI_NESTED_WAIT_FAILED;
    }

    pthread_cond_broadcast(&waiter->condition);
    pthread_mutex_unlock(&waiter->mutex);

    if (destroy) {
        nested_event_waiter_destroy(waiter);
    }
}

static bool lane_process_event(ksi_hook_lane *lane, ksi_lane_event *event);
static ksi_hook_lane *lane_for_hook_type(ksi_daemon_state *state, uint32_t hook_type);

typedef struct ksi_lane_context_snapshot {
    uint64_t event_id;
    int responder_fd;
    uint64_t responder_connection_id;
    uint64_t hook_session_id;
    uint32_t nesting_depth;
} ksi_lane_context_snapshot;

static ksi_lane_context_snapshot lane_context_capture(const ksi_hook_lane *lane)
{
    return (ksi_lane_context_snapshot) {
        .event_id = atomic_load(&lane->current_event_id),
        .responder_fd = atomic_load(&lane->current_responder_fd),
        .responder_connection_id = atomic_load(&lane->current_responder_connection_id),
        .hook_session_id = atomic_load(&lane->current_hook_session_connection_id),
        .nesting_depth = atomic_load(&lane->current_nesting_depth),
    };
}

static void lane_context_restore(
    ksi_hook_lane *lane,
    const ksi_lane_context_snapshot *snapshot)
{
    atomic_store(&lane->current_event_id, snapshot->event_id);
    atomic_store(&lane->current_responder_fd, snapshot->responder_fd);
    atomic_store(&lane->current_responder_connection_id, snapshot->responder_connection_id);
    atomic_store(&lane->current_hook_session_connection_id, snapshot->hook_session_id);
    atomic_store(&lane->current_nesting_depth, snapshot->nesting_depth);
}

static void lane_clear_responder(ksi_hook_lane *lane)
{
    atomic_store(&lane->current_responder_fd, -1);
    atomic_store(&lane->current_responder_connection_id, 0u);
    atomic_store(&lane->current_hook_session_connection_id, 0u);
}

static bool lane_event_cancelled(
    const ksi_hook_lane *lane,
    const ksi_lane_event *event)
{
    return atomic_load(&lane->shutting_down)
        || event->generation != atomic_load(&lane->flush_generation);
}

static bool lane_event_deadline_expired(const ksi_lane_event *event)
{
    return event->inherited_deadline_ms != 0u
        && monotonic_ms() >= event->inherited_deadline_ms;
}

static bool lane_process_nested_transaction(
    ksi_hook_lane *lane,
    ksi_nested_transaction *transaction,
    uint64_t parent_deadline_ms)
{
    bool ok = true;

    if (lane == NULL || transaction == NULL
        || transaction->depth > KSI_MAX_RECURSION_DEPTH
        || transaction->origin_generation != atomic_load(&lane->flush_generation)
        || atomic_load(&lane->current_hook_session_connection_id)
            != transaction->origin_hook_connection_id
        || atomic_load(&lane->current_nesting_depth) + 1u != transaction->depth) {
        ok = false;
        goto complete;
    }

    for (uint32_t i = 0; i < transaction->count; i++) {
        ksi_nested_member *member = &transaction->members[i];
        ksi_lane_event *event = member->event;

        if (transaction->origin_generation != atomic_load(&lane->flush_generation)
            || monotonic_ms() >= parent_deadline_ms) {
            ok = false;
            break;
        }

        if (event == NULL) {
            uint32_t flags = KSI_SYNTH_FLAG_BYPASS_HOOK | KSI_SYNTH_FLAG_BATCH_FRAGMENT
                | (i == 0u ? KSI_SYNTH_FLAG_BATCH_START : 0u);

            if (!output_queue_push_synth(
                    &lane->state->output_queue, &member->input, 1u, flags)) {
                ok = false;
                break;
            }

            continue;
        }

        member->event = NULL;
        event->inherited_deadline_ms = parent_deadline_ms;

        if (event->hook_type == lane->hook_type) {
            bool member_ok = lane_process_event(lane, event);
            free(event);

            if (!member_ok) {
                ok = false;
                break;
            }
        } else {
            ksi_hook_lane *target = lane_for_hook_type(lane->state, event->hook_type);
            ksi_nested_event_waiter *waiter = nested_event_waiter_create();
            bool member_ok = false;

            if (target == NULL || waiter == NULL) {
                nested_event_waiter_destroy(waiter);
                lane_event_release_send_refs(event);
                free(event);
                ok = false;
                break;
            }

            event->nested_waiter = waiter;

            if (!ksi_pipe_ring_push(&target->action_queue.ring, &event)) {
                event->nested_waiter = NULL;
                nested_event_waiter_destroy(waiter);
                lane_event_release_send_refs(event);
                free(event);
                ok = false;
                break;
            }

            if (nested_event_waiter_wait(waiter, parent_deadline_ms, &member_ok)) {
                nested_event_waiter_destroy(waiter);
            }

            if (!member_ok) {
                ok = false;
                break;
            }
        }
    }

complete:
    if (transaction != NULL && transaction->completion != NULL) {
        if (ok) {
            synth_completion_complete(transaction->completion);
        } else {
            synth_completion_fail(transaction->completion, -1, KSI_DETAIL_CALLBACK_TIMEOUT);
        }

        transaction->completion = NULL;
    }

    nested_transaction_destroy(transaction);
    return ok;
}

typedef enum ksi_subscriber_result {
    KSI_SUBSCRIBER_SKIPPED = 0,
    KSI_SUBSCRIBER_DECIDED,
    KSI_SUBSCRIBER_EVENT_CANCELLED,
    KSI_SUBSCRIBER_PARENT_DEADLINE,
} ksi_subscriber_result;

static bool lane_wait_for_matching_decision(
    ksi_hook_lane *lane,
    const ksi_lane_event *event,
    const ksi_lane_subscriber *subscriber,
    uint64_t deadline,
    ksi_lane_decision *decision)
{
    for (;;) {
        ksi_nested_transaction *nested = NULL;
        uint64_t now;

        while (ksi_pipe_ring_pop(&lane->nested_queue.ring, &nested)) {
            (void)lane_process_nested_transaction(lane, nested, deadline);
            nested = NULL;

            if (monotonic_ms() >= deadline) {
                break;
            }
        }

        now = monotonic_ms();

        if (now >= deadline || lane_event_cancelled(lane, event)) {
            return false;
        }

        lane_decision_queue_wait(&lane->decision_queue,
            (int)((deadline - now) > 100u ? 100u : (deadline - now)));

        for (;;) {
            if (!lane_decision_queue_pop(&lane->decision_queue, decision)) {
                break;
            }

            if (decision->event_id == event->event_id
                && decision->responder_fd == hook_send_ref_fd(subscriber->send_ref)
                && decision->responder_connection_id == subscriber->connection_id) {
                return true;
            }
        }
    }
}

static ksi_subscriber_result lane_call_subscriber(
    ksi_hook_lane *lane,
    const ksi_lane_event *event,
    const ksi_lane_subscriber *subscriber,
    ksi_lane_decision *decision)
{
    ksi_hook_send_ref *send_ref = subscriber->send_ref;
    uint64_t callback_started_ms;
    uint64_t deadline;

    memset(decision, 0, offsetof(ksi_lane_decision, inputs));

    /* Snapshots captured before quarantine was recorded skip immediately. */
    if (hook_send_ref_is_stalled(send_ref, hook_type_to_lane_index(event->hook_type))) {
        return KSI_SUBSCRIBER_SKIPPED;
    }

    if (hook_send_ref_fd(send_ref) < 0) {
        lane_post_hook_failure(lane->state, event->hook_type,
            event->event_id, subscriber->hook_session_id, event->nesting_depth, 0u,
            KSI_HOOK_QUARANTINE_REASON_TRANSPORT);
        return KSI_SUBSCRIBER_SKIPPED;
    }

    while (!subscriber->uses_callback_rpc
        && !hook_send_ref_wait_event_turn(
            send_ref, event->event_id, KSI_HOOK_SEND_ORDER_WAIT_MS)) {
        if (lane_event_cancelled(lane, event) || lane_event_deadline_expired(event)) {
            break;
        }
    }

    if (lane_event_cancelled(lane, event)) {
        return KSI_SUBSCRIBER_EVENT_CANCELLED;
    }

    if (lane_event_deadline_expired(event)) {
        return KSI_SUBSCRIBER_PARENT_DEADLINE;
    }

    atomic_store(&lane->current_responder_fd, hook_send_ref_fd(subscriber->send_ref));
    atomic_store(&lane->current_responder_connection_id, subscriber->connection_id);
    atomic_store(&lane->current_hook_session_connection_id, subscriber->hook_session_id);
    callback_started_ms = monotonic_ms();

    if (hook_send_ref_send(send_ref, KSI_MESSAGE_HOOK_EVENT, 0,
            event->event_id, &event->payload, event->payload_size) != 0) {
        lane_clear_responder(lane);
        lane_post_hook_failure(lane->state, event->hook_type,
            event->event_id, subscriber->hook_session_id, event->nesting_depth, 0u,
            KSI_HOOK_QUARANTINE_REASON_TRANSPORT);
        return KSI_SUBSCRIBER_SKIPPED;
    }

    /* This absolute deadline includes all recursive synthesis. */
    deadline = callback_started_ms + KSI_HOOK_DECISION_TIMEOUT_MS;

    if (event->inherited_deadline_ms != 0u && event->inherited_deadline_ms < deadline) {
        deadline = event->inherited_deadline_ms;
    }

    if (lane_wait_for_matching_decision(lane, event, subscriber, deadline, decision)) {
        lane_clear_responder(lane);
        hook_send_ref_clear_stalled(send_ref, hook_type_to_lane_index(event->hook_type));
        return KSI_SUBSCRIBER_DECIDED;
    }

    lane_clear_responder(lane);

    if (lane_event_cancelled(lane, event)) {
        return KSI_SUBSCRIBER_EVENT_CANCELLED;
    }

    hook_send_ref_mark_stalled(send_ref, hook_type_to_lane_index(event->hook_type));
    lane_post_hook_failure(lane->state, event->hook_type,
        event->event_id, subscriber->hook_session_id, event->nesting_depth,
        (uint32_t)(monotonic_ms() - callback_started_ms),
        KSI_HOOK_QUARANTINE_REASON_TIMEOUT);

    return lane_event_deadline_expired(event)
        ? KSI_SUBSCRIBER_PARENT_DEADLINE
        : KSI_SUBSCRIBER_SKIPPED;
}

/* Lane thread only; uses the subscriber snapshot captured in `event`. */
static bool lane_process_event(ksi_hook_lane *lane, ksi_lane_event *event)
{
    uint32_t final_decision = KSI_HOOK_DECISION_PASS;
    ksi_lane_decision decision;
    bool finalized = false;
    bool synthetic_ok = true;
    ksi_lane_context_snapshot parent_context = lane_context_capture(lane);

    atomic_store(&lane->current_event_id, event->event_id);
    atomic_store(&lane->current_nesting_depth, event->nesting_depth);

    /* During shutdown skip callbacks; physical events still replay as PASS. */
    if (lane_event_cancelled(lane, event)) {
        if (event->egress.type == KSI_LANE_EGRESS_SYNTH) {
            final_decision = KSI_HOOK_DECISION_BLOCK;
            synthetic_ok = false;
        }

        goto finalize;
    }

    for (size_t i = 0; i < event->subscriber_count && !finalized; i++) {
        const ksi_lane_subscriber *subscriber = &event->subscribers[i];
        ksi_subscriber_result result;

        if (lane_event_deadline_expired(event)) {
            final_decision = KSI_HOOK_DECISION_BLOCK;
            synthetic_ok = false;
            break;
        }

        result = lane_call_subscriber(lane, event, subscriber, &decision);

        if (result == KSI_SUBSCRIBER_SKIPPED) {
            continue;
        }

        if (result == KSI_SUBSCRIBER_EVENT_CANCELLED) {
            break;
        }

        if (result == KSI_SUBSCRIBER_PARENT_DEADLINE) {
            final_decision = KSI_HOOK_DECISION_BLOCK;
            synthetic_ok = false;
            break;
        }

        if (decision.decision == KSI_HOOK_DECISION_BLOCK) {
            final_decision = KSI_HOOK_DECISION_BLOCK;
            finalized = true;
        } else if (decision.decision == KSI_HOOK_DECISION_MODIFY) {
            final_decision = KSI_HOOK_DECISION_MODIFY;
            finalized = true;
        }
        /* else PASS — fall through to next subscriber. */

    }

finalize:
    if (event->egress.type == KSI_LANE_EGRESS_SYNTH
        && lane_event_cancelled(lane, event)) {
        final_decision = KSI_HOOK_DECISION_BLOCK;
        synthetic_ok = false;
    }

    if (event->nested_waiter != NULL) {
        if (!nested_event_waiter_begin_completion(event->nested_waiter)) {
            final_decision = KSI_HOOK_DECISION_BLOCK;
            synthetic_ok = false;
        }
    }

    if (event->egress.type == KSI_LANE_EGRESS_SYNTH
        && lane_event_deadline_expired(event)) {
        final_decision = KSI_HOOK_DECISION_BLOCK;
        synthetic_ok = false;
    }

    if (final_decision == KSI_HOOK_DECISION_PASS) {
        if (!lane_event_enqueue_egress(lane, event)) {
            synthetic_ok = false;
            fprintf(stderr, "lane: egress enqueue failed for event %llu\n",
                (unsigned long long)event->event_id);

            if (event->egress.type == KSI_LANE_EGRESS_REPLAY) {
                request_fail_open(lane->state);
            }
        }
    } else if (final_decision == KSI_HOOK_DECISION_MODIFY
        && decision.input_count > 0u) {
        /* BATCH_FRAGMENT: the modified event may be one fragment of a synthetic
         * client batch being routed through this hook (e.g. a SendText emoji's
         * low-surrogate unit). Carrying the flag keeps the daemon's per-batch
         * pending-high-surrogate reset from firing between the pair's fragments,
         * mirroring the PASS synth-egress sites (hook_ingress.inc). */
        if (!output_queue_push_synth(&lane->state->output_queue,
                decision.inputs, decision.input_count,
                KSI_SYNTH_FLAG_BYPASS_HOOK | KSI_SYNTH_FLAG_BATCH_FRAGMENT)) {
            synthetic_ok = false;
            fprintf(stderr, "lane: modify synth enqueue failed for event %llu\n",
                (unsigned long long)event->event_id);

            (void)lane_event_enqueue_egress(lane, event);

            if (event->egress.type == KSI_LANE_EGRESS_REPLAY) {
                request_fail_open(lane->state);
            }
        }
    }

    lane_event_release_send_refs(event);

    if (event->synthetic_completion != NULL) {
        if (synthetic_ok) {
            synth_completion_complete(event->synthetic_completion);
        } else {
            synth_completion_fail(event->synthetic_completion, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
        }

        event->synthetic_completion = NULL;
        synthetic_hook_queue_complete_inflight(lane->state);
    }

    if (event->nested_waiter != NULL) {
        nested_event_waiter_finish_completion(event->nested_waiter, synthetic_ok);
        event->nested_waiter = NULL;
    }

    lane_context_restore(lane, &parent_context);

    if (event->physical_input) {
        atomic_fetch_sub(&lane->state->physical_hook_inflight, 1u);
    }

    return synthetic_ok;
}

/* Best-effort low real-time priority for bounded hook/output latency. */
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

    pfd.fd = ksi_pipe_ring_wake_fd(&lane->action_queue.ring);
    pfd.events = POLLIN;

    for (;;) {
        ksi_lane_event *event = NULL;
        ksi_nested_transaction *orphaned = NULL;

        while (ksi_pipe_ring_pop(&lane->nested_queue.ring, &orphaned)) {
            (void)lane_process_nested_transaction(lane, orphaned, monotonic_ms());
            orphaned = NULL;
        }

        if (!ksi_pipe_ring_pop(&lane->action_queue.ring, &event)) {
            if (atomic_load(&lane->shutting_down)) {
                ksi_lane_decision decision;

                while (lane_decision_queue_pop(&lane->decision_queue, &decision)) {
                }

                return NULL;
            }

            (void)poll(&pfd, 1, 1000);
            ksi_pipe_ring_drain_wake(&lane->action_queue.ring);
            continue;
        }

        (void)lane_process_event(lane, event);
        free(event);
    }
}

static ksi_hook_lane *lane_for_hook_type(ksi_daemon_state *state, uint32_t hook_type)
{
    if (state == NULL) {
        return NULL;
    }

    if (hook_type == KSI_HOOK_KEYBOARD_LL) {
        return &state->keyboard_lane;
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

    if (atomic_load(&state->keyboard_lane.current_event_id) == event_id) {
        return &state->keyboard_lane;
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

    if (ksi_pipe_ring_init(&lane->action_queue.ring,
            sizeof(ksi_lane_event *), KSI_MAX_LANE_ACTIONS) != 0) {
        return -1;
    }

    if (lane_decision_queue_init(&lane->decision_queue) != 0) {
        lane_action_queue_close(&lane->action_queue);
        return -1;
    }

    if (ksi_pipe_ring_init(&lane->nested_queue.ring,
            sizeof(ksi_nested_transaction *), KSI_MAX_LANE_ACTIONS) != 0) {
        lane_decision_queue_close(&lane->decision_queue);
        lane_action_queue_close(&lane->action_queue);
        return -1;
    }

    atomic_store(&lane->current_event_id, (uint_least64_t)0);
    atomic_store(&lane->current_responder_fd, -1);
    atomic_store(&lane->current_responder_connection_id, 0u);
    atomic_store(&lane->current_hook_session_connection_id, 0u);
    atomic_store(&lane->current_nesting_depth, 0u);
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

static bool join_thread_before(pthread_t thread, uint64_t deadline_ms)
{
    struct timespec absolute;
    uint64_t now = monotonic_ms();
    uint64_t remaining_ms;

    if (deadline_ms == 0u) {
        return pthread_join(thread, NULL) == 0;
    }

    /* Shutdown-only wait; grabs have already been released. */
    if (now >= deadline_ms || clock_gettime(CLOCK_REALTIME, &absolute) != 0) {
        return false;
    }

    remaining_ms = deadline_ms - now;
    absolute.tv_sec += (time_t)(remaining_ms / 1000u);
    absolute.tv_nsec += (long)((remaining_ms % 1000u) * 1000000u);

    if (absolute.tv_nsec >= 1000000000L) {
        absolute.tv_sec++;
        absolute.tv_nsec -= 1000000000L;
    }

    return pthread_timedjoin_np(thread, NULL, &absolute) == 0;
}

static bool lane_shutdown(ksi_hook_lane *lane, uint64_t deadline_ms)
{
    if (lane == NULL) {
        return true;
    }

    if (lane->thread_started) {
        /* Abort decision waits, then wake the lane to drain and exit. */
        atomic_store(&lane->shutting_down, 1);
        ksi_pipe_ring_wake(&lane->action_queue.ring);

        if (!join_thread_before(lane->thread, deadline_ms)) {
            return false;
        }

        lane->thread_started = false;
    }

    lane_action_queue_close(&lane->action_queue);
    nested_transaction_queue_close(&lane->nested_queue);
    lane_decision_queue_close(&lane->decision_queue);
    return true;
}

static void lane_flush_passthrough(ksi_hook_lane *lane)
{
    if (lane == NULL) {
        return;
    }

    atomic_fetch_add(&lane->flush_generation, 1u);
    lane_decision_queue_wake(&lane->decision_queue);
    ksi_pipe_ring_wake(&lane->action_queue.ring);
}

/* ------------------------------------------------------------------------ */
