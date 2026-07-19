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

static int output_queue_init(
    ksi_output_queue *q,
    ksi_daemon_state *state)
{
    if (q == NULL) {
        return -1;
    }

    memset(q, 0, sizeof(*q));
    q->state = state;

    if (pthread_mutex_init(&q->mutex, NULL) != 0) {
        return -1;
    }

    q->mutex_initialized = true;

    if (pthread_mutex_init(&q->execution_mutex, NULL) != 0) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
        return -1;
    }

    q->execution_mutex_initialized = true;

    if (ksi_wake_pipe_open(&q->wake_read_fd, &q->wake_write_fd) != 0) {
        pthread_mutex_destroy(&q->execution_mutex);
        q->execution_mutex_initialized = false;
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

static bool synth_batch_is_cleanup(const ksi_input *inputs, uint32_t count)
{
    const uint32_t mouse_down = KSI_MOUSEEVENTF_LEFTDOWN
        | KSI_MOUSEEVENTF_RIGHTDOWN | KSI_MOUSEEVENTF_MIDDLEDOWN
        | KSI_MOUSEEVENTF_XDOWN;
    const uint32_t mouse_up = KSI_MOUSEEVENTF_LEFTUP
        | KSI_MOUSEEVENTF_RIGHTUP | KSI_MOUSEEVENTF_MIDDLEUP
        | KSI_MOUSEEVENTF_XUP;

    if (inputs == NULL || count == 0u) {
        return false;
    }

    for (uint32_t i = 0u; i < count; i++) {
        if (inputs[i].type == KSI_INPUT_KEYBOARD) {
            if ((inputs[i].data.keyboard.flags & KSI_KEYEVENTF_KEYUP) == 0u) {
                return false;
            }
        } else if (inputs[i].type == KSI_INPUT_MOUSE) {
            uint32_t flags = inputs[i].data.mouse.flags;

            if ((flags & mouse_down) != 0u || (flags & mouse_up) == 0u) {
                return false;
            }
        } else {
            return false;
        }
    }

    return true;
}

static bool replay_is_cleanup(
    uint32_t hook_type,
    const ksi_hook_event_payload *payload)
{
    uint32_t message;

    if (hook_type == KSI_HOOK_KEYBOARD_LL) {
        message = payload->event.keyboard.message;
        return message == KSI_WM_KEYUP || message == KSI_WM_SYSKEYUP
            || (payload->event.keyboard.flags & KSI_LLKHF_UP) != 0u;
    }

    if (hook_type != KSI_HOOK_MOUSE_LL) {
        return false;
    }

    message = payload->event.mouse.message;
    return message == KSI_WM_LBUTTONUP || message == KSI_WM_RBUTTONUP
        || message == KSI_WM_MBUTTONUP || message == KSI_WM_XBUTTONUP;
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

    if (q->execution_mutex_initialized) {
        pthread_mutex_destroy(&q->execution_mutex);
        q->execution_mutex_initialized = false;
    }
}

static bool output_queue_push_replay(
    ksi_output_queue *q,
    uint32_t hook_type,
    const ksi_hook_event_payload *payload,
    uint64_t input_generation)
{
    ksi_output_action action;

    if (q == NULL || !q->mutex_initialized || payload == NULL) {
        return false;
    }

    memset(&action, 0, sizeof(action));
    action.type = KSI_OUTPUT_ACTION_REPLAY;
    action.input_generation = input_generation;
    action.hook_type = hook_type;
    action.replay_payload = *payload;

    pthread_mutex_lock(&q->mutex);

    if (q->count >= KSI_MAX_OUTPUT_ACTIONS
            - (replay_is_cleanup(hook_type, payload)
                ? 0u : KSI_OUTPUT_CLEANUP_RESERVE)
        || (input_generation != 0u
            && (q->state == NULL
                || input_generation != current_input_generation(q->state)))) {
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
    uint32_t flags,
    uint64_t input_generation)
{
    ksi_output_action action;
    size_t synth_bytes;
    size_t synth_limit;
    bool cleanup;

    if (q == NULL || !q->mutex_initialized
        || (count > 0u && inputs == NULL)) {
        return false;
    }

    synth_bytes = (size_t)count * sizeof(*inputs);
    cleanup = synth_batch_is_cleanup(inputs, count);
    synth_limit = KSI_MAX_OUTPUT_SYNTH_BYTES
        - (cleanup ? 0u : KSI_OUTPUT_CLEANUP_BYTES);
    memset(&action, 0, sizeof(action));
    action.type = KSI_OUTPUT_ACTION_SYNTH;
    action.input_generation = input_generation;
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

    if ((input_generation != 0u
            && (q->state == NULL
                || input_generation != current_input_generation(q->state)))
        || q->count >= KSI_MAX_OUTPUT_ACTIONS
            - (cleanup ? 0u : KSI_OUTPUT_CLEANUP_RESERVE)
        || synth_bytes > synth_limit
        || q->synth_bytes > synth_limit - synth_bytes) {
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

    if (q == NULL || !q->mutex_initialized
        || (type != KSI_OUTPUT_ACTION_RELEASE_ALL
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

static void output_queue_wait_for_inflight(ksi_output_queue *q)
{
    if (q == NULL || !q->execution_mutex_initialized) {
        return;
    }

    pthread_mutex_lock(&q->execution_mutex);
    pthread_mutex_unlock(&q->execution_mutex);
}

/* Remove deferred client/physical output admitted under a former seat owner.
 * Called only after the active generation has advanced and before RELEASE_ALL
 * is appended. Daemon-internal generation-zero safety actions are retained. */
static void output_queue_discard_stale(
    ksi_output_queue *q,
    uint64_t active_generation)
{
    size_t retained = 0u;
    size_t retained_synth_bytes = 0u;

    if (q == NULL || !q->mutex_initialized) {
        return;
    }

    pthread_mutex_lock(&q->mutex);

    for (size_t i = 0u; i < q->count; i++) {
        size_t source = (q->head + i) % KSI_MAX_OUTPUT_ACTIONS;
        ksi_output_action action = q->actions[source];

        if (action.input_generation != 0u
            && action.input_generation != active_generation) {
            free(action.synth_inputs);
            memset(&q->actions[source], 0, sizeof(q->actions[source]));
            continue;
        }

        size_t destination = (q->head + retained) % KSI_MAX_OUTPUT_ACTIONS;

        if (destination != source) {
            q->actions[destination] = action;
            memset(&q->actions[source], 0, sizeof(q->actions[source]));
        }

        retained_synth_bytes += output_action_synth_bytes(&action);
        retained++;
    }

    q->count = retained;
    q->synth_bytes = retained_synth_bytes;
    pthread_mutex_unlock(&q->mutex);
}

static ksi_synth_completion *synth_completion_create(
    ksi_daemon_state *state,
    ksi_hook_send_ref *send_ref,
    const ksi_message_header *request)
{
    ksi_synth_completion *completion;

    if (state == NULL
        || ((send_ref == NULL) != (request == NULL))
        || (send_ref != NULL && !hook_send_ref_acquire(send_ref))) {
        return NULL;
    }

    completion = calloc(1, sizeof(*completion));

    if (completion == NULL) {
        hook_send_ref_release(send_ref);
        return NULL;
    }

    completion->send_ref = send_ref;
    completion->state = state;

    if (request != NULL) {
        completion->client_id = request->client_id;
        completion->correlation_id = request->correlation_id;
    }
    atomic_init(&completion->remaining, 1u);
    atomic_init(&completion->terminal_result, 0u);
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

static bool synth_completion_begin_recursive_synthesis(
    ksi_synth_completion *completion,
    uint64_t parent_hook_event_id)
{
    if (completion == NULL || completion->is_recursive_synthesis
        || parent_hook_event_id == 0u
        || !hook_send_ref_begin_pump(
            completion->send_ref, parent_hook_event_id)) {
        return false;
    }

    completion->is_recursive_synthesis = true;
    completion->parent_hook_event_id = parent_hook_event_id;
    return true;
}

static void synth_completion_destroy(ksi_synth_completion *completion)
{
    if (completion == NULL) {
        return;
    }

    hook_send_ref_release(completion->send_ref);
    free(completion);
}

static void synth_completion_send_result(
    ksi_synth_completion *completion,
    int32_t status,
    uint32_t detail)
{
    ksi_status_payload payload;

    /* Ordinary SendInput reports queue admission immediately. Its detached
     * completion exists only to keep the accepted batch atomic while hooks run. */
    if (completion == NULL || completion->send_ref == NULL) {
        return;
    }

    bool can_reply = !completion->is_recursive_synthesis
        || hook_send_ref_end_pump(completion->send_ref,
            completion->parent_hook_event_id);

    /* Publish the parent's resumable state before publishing the result bytes.
     * The callback may issue its next synchronous Send as soon as it reads this
     * response (menu-mask down/up deliberately use separate requests). */
    if (can_reply) {
        payload.status = status;
        payload.detail = detail;
        if (hook_send_ref_send(
                completion->send_ref,
                KSI_MESSAGE_SYNTHESIS_RESULT,
                completion->client_id,
                completion->correlation_id,
                &payload,
                sizeof(payload)) != 0) {
            hook_send_ref_invalidate(completion->send_ref);
        }
    } else {
        /* The parent disappeared without unwinding its pump. No future reply on
         * this stream is well-defined, so wake every blocked participant now. */
        hook_send_ref_invalidate(completion->send_ref);
    }
}

static void synth_completion_release_one(ksi_synth_completion *completion)
{
    unsigned int previous;
    uint64_t terminal_result;
    uint32_t status_bits;
    int32_t status;

    if (completion == NULL) {
        return;
    }

    previous = atomic_fetch_sub(&completion->remaining, 1u);

    if (previous != 1u) {
        return;
    }

    if (completion->owns_atomic_transaction && completion->state != NULL) {
        atomic_fetch_sub(&completion->state->active_synthetic_transactions, 1u);
    }

    terminal_result = atomic_load(&completion->terminal_result);
    status_bits = (uint32_t)terminal_result;
    memcpy(&status, &status_bits, sizeof(status));
    synth_completion_send_result(
        completion, status, (uint32_t)(terminal_result >> 32u));
    synth_completion_destroy(completion);
}

static void synth_completion_finish(
    ksi_synth_completion *completion,
    int32_t status,
    uint32_t detail)
{
    uint64_t expected = 0u;

    if (completion == NULL) {
        return;
    }

    if (status != 0) {
        uint64_t result = ((uint64_t)detail << 32u) | (uint32_t)status;
        (void)atomic_compare_exchange_strong(
            &completion->terminal_result, &expected, result);
    }

    synth_completion_release_one(completion);
}

static void synth_completion_complete(ksi_synth_completion *completion)
{
    synth_completion_finish(completion, 0, 0u);
}

static void synth_completion_fail(
    ksi_synth_completion *completion, int32_t status, uint32_t detail)
{
    synth_completion_finish(completion, status, detail);
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
    ksi_synth_completion *completion,
    uint64_t input_generation)
{
    size_t tail = (q->head + q->count) % KSI_MAX_SYNTH_HOOK_ACTIONS;
    q->items[tail].input = *input;
    q->items[tail].queued_at_ns = queued_at_ns;
    q->items[tail].completion = completion;
    q->items[tail].input_generation = input_generation;
    q->items[tail].batch_start = false;  /* push() marks the batch's first node */
    q->count++;
}

static void synthetic_hook_queue_append_input_locked(
    ksi_synthetic_hook_queue *q,
    const ksi_input *input,
    uint64_t queued_at_ns,
    ksi_synth_completion *completion,
    uint64_t input_generation)
{
    ksi_input expanded[KSI_SYNTHETIC_HOOK_MAX_EXPANSION];
    size_t count = ksi_synthetic_hook_expand_input(input, expanded);

    for (size_t i = 0u; i < count; i++) {
        synthetic_hook_queue_append_locked(
            q, &expanded[i], queued_at_ns, completion, input_generation);
    }
}

static bool synthetic_hook_queue_push(
    ksi_synthetic_hook_queue *q,
    const ksi_input *inputs,
    uint32_t count,
    size_t node_count,
    uint64_t queued_at_ns,
    ksi_synth_completion *completion,
    uint64_t input_generation)
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
        synthetic_hook_queue_append_input_locked(
            q, &inputs[i], queued_at_ns, completion, input_generation);
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
    bool *batch_start,
    uint64_t *input_generation)
{
    ksi_synthetic_hook_item item;
    bool found = false;

    if (q == NULL || input == NULL || queued_at_ns == NULL
        || completion == NULL || batch_start == NULL || input_generation == NULL
        || !q->mutex_initialized) {
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
    *input_generation = item.input_generation;
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
            pthread_mutex_lock(&state->output_queue.execution_mutex);

            /* A seat transition increments this epoch before releasing grabs.
             * Drop every delayed action admitted by the former owner. The
             * following unscoped RELEASE_ALL remains in the same FIFO and
             * cleans any key which had already reached uinput. */
            if (action.input_generation != 0u
                && action.input_generation != current_input_generation(state)) {
                free(action.synth_inputs);
                pthread_mutex_unlock(&state->output_queue.execution_mutex);
                continue;
            }

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

            pthread_mutex_unlock(&state->output_queue.execution_mutex);
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

    hook_send_ref_complete_event(subscriber->send_ref, event->event_id);
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

    if (hook_send_ref_send(
            send_ref, KSI_MESSAGE_HOOK_DECISION,
            request->client_id, request->correlation_id,
            &ack_payload, sizeof(ack_payload)) != 0) {
        /* The lane may still consume this already-made decision, but no later
         * callback can be paired safely with a stream which missed its ack. */
        hook_send_ref_invalidate(send_ref);
    }

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
    uint64_t connection_id,
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
    cmd.connection_id = connection_id;
    cmd.data.hook_failure.hook_type = hook_type;
    cmd.data.hook_failure.event_id = event_id;
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
            event->hook_type, &event->payload, event->input_generation);
    }

    if (event->egress.type == KSI_LANE_EGRESS_SYNTH) {
        return output_queue_push_synth(&lane->state->output_queue,
            event->egress.synth_inputs,
            event->egress.synth_count,
            event->egress.synth_flags,
            event->input_generation);
    }

    return false;
}

static bool lane_process_event(ksi_hook_lane *lane, ksi_lane_event *event);

static ksi_hook_lane *lane_for_hook_type(ksi_daemon_state *state, uint32_t hook_type);

typedef struct ksi_lane_context_snapshot {
    uint64_t event_id;
    uint64_t responder_connection_id;
    uint32_t nesting_depth;
} ksi_lane_context_snapshot;

static ksi_lane_context_snapshot lane_context_capture(const ksi_hook_lane *lane)
{
    return (ksi_lane_context_snapshot) {
        .event_id = atomic_load(&lane->current_event_id),
        .responder_connection_id = atomic_load(&lane->current_responder_connection_id),
        .nesting_depth = atomic_load(&lane->current_nesting_depth),
    };
}

static void lane_context_restore(
    ksi_hook_lane *lane,
    const ksi_lane_context_snapshot *snapshot)
{
    atomic_store(&lane->current_event_id, snapshot->event_id);
    atomic_store(&lane->current_responder_connection_id, snapshot->responder_connection_id);
    atomic_store(&lane->current_nesting_depth, snapshot->nesting_depth);
}

static void lane_clear_responder(ksi_hook_lane *lane)
{
    atomic_store(&lane->current_responder_connection_id, 0u);
}

static bool lane_event_cancelled(
    const ksi_hook_lane *lane,
    const ksi_lane_event *event)
{
    return atomic_load(&lane->shutting_down)
        || event->generation != atomic_load(&lane->flush_generation)
        || (event->input_generation != 0u
            && event->input_generation
                != current_input_generation(lane->state));
}

/* An authenticated recursive request is accepted as one transaction. If its
 * parent unwinds or a hook lane is flushed before execution, emit the remaining
 * inputs as one bypass batch; never accept a modifier-down and lose its key-up. */
static bool lane_fail_open_nested_transaction(
    ksi_hook_lane *lane,
    ksi_nested_transaction *transaction,
    uint32_t first)
{
    ksi_input *inputs;
    uint32_t count;
    bool ok;

    if (lane == NULL || transaction == NULL || first > transaction->count) {
        return false;
    }

    count = transaction->count - first;

    if (count == 0u) {
        return true;
    }

    inputs = malloc((size_t)count * sizeof(*inputs));

    if (inputs == NULL) {
        return false;
    }

    for (uint32_t i = first; i < transaction->count; i++) {
        ksi_nested_member *member = &transaction->members[i];
        inputs[i - first] = member->input;

        if (member->event != NULL) {
            lane_event_release_send_refs(member->event);
            free(member->event);
            member->event = NULL;
        }
    }

    ok = output_queue_push_synth(&lane->state->output_queue,
        inputs, count, KSI_SYNTH_FLAG_BYPASS_HOOK,
        transaction->input_generation);
    free(inputs);
    return ok;
}

static bool lane_process_nested_transaction(
    ksi_hook_lane *lane,
    ksi_nested_transaction *transaction)
{
    bool ok = true;
    bool parent_active;

    if (lane == NULL || transaction == NULL) {
        return false;
    }

    /* A seat-owner change is the one cancellation which must not leak old-user
     * output into the new session. Ordinary scheduler invalidation is fail-open. */
    if (transaction->input_generation != 0u
        && transaction->input_generation
            != current_input_generation(lane->state)) {
        ok = false;
        goto complete;
    }

    parent_active = transaction->depth <= KSI_MAX_RECURSION_DEPTH
        && transaction->origin_generation == atomic_load(&lane->flush_generation)
        && atomic_load(&lane->current_event_id)
            == transaction->parent_hook_event_id
        && atomic_load(&lane->current_responder_connection_id)
            == transaction->origin_connection_id
        && atomic_load(&lane->current_nesting_depth) + 1u
            == transaction->depth;

    if (!parent_active) {
        ok = lane_fail_open_nested_transaction(lane, transaction, 0u);
        goto complete;
    }

    for (uint32_t i = 0; i < transaction->count; i++) {
        ksi_nested_member *member = &transaction->members[i];
        ksi_lane_event *event = member->event;

        if (transaction->input_generation != 0u
            && transaction->input_generation
                != current_input_generation(lane->state)) {
            ok = false;
            break;
        }

        if (event == NULL) {
            uint32_t flags = KSI_SYNTH_FLAG_BYPASS_HOOK | KSI_SYNTH_FLAG_BATCH_FRAGMENT
                | (i == 0u ? KSI_SYNTH_FLAG_BATCH_START : 0u);

            if (!output_queue_push_synth(
                    &lane->state->output_queue, &member->input, 1u, flags,
                    transaction->input_generation)) {
                ok = false;
                break;
            }

            continue;
        }

        member->event = NULL;
        ok = lane_process_event(lane, event);
        free(event);

        if (!ok) {
            ok = lane_fail_open_nested_transaction(lane, transaction, i);
            break;
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
} ksi_subscriber_result;

static bool lane_wait_for_matching_decision(
    ksi_hook_lane *lane,
    const ksi_lane_event *event,
    ksi_hook_send_ref *delivery_ref,
    uint64_t delivery_connection_id,
    uint64_t deadline,
    ksi_lane_decision *decision)
{
    for (;;) {
        ksi_nested_transaction *nested = NULL;
        uint64_t now;

        while (ksi_pipe_ring_pop(&lane->nested_queue.ring, &nested)) {
            uint64_t nested_started = monotonic_ms();
            (void)lane_process_nested_transaction(lane, nested);
            nested = NULL;
            /* Synchronous callees receive their own hook-proc timeout. Do not
             * charge their execution to the parent callback's turn. */
            deadline += monotonic_ms() - nested_started;
        }

        now = monotonic_ms();

        if (now >= deadline || lane_event_cancelled(lane, event)
            || !hook_send_ref_is_valid(delivery_ref)) {
            return false;
        }

        lane_decision_queue_wait(&lane->decision_queue,
            (int)((deadline - now) > 100u ? 100u : (deadline - now)));

        for (;;) {
            if (!lane_decision_queue_pop(&lane->decision_queue, decision)) {
                break;
            }

            if (decision->event_id == event->event_id
                && decision->responder_connection_id == delivery_connection_id) {
                return true;
            }
        }
    }
}

static ksi_subscriber_result lane_call_subscriber(
    ksi_hook_lane *lane,
    const ksi_lane_event *event,
    ksi_lane_subscriber *subscriber,
    ksi_lane_decision *decision)
{
    ksi_hook_send_ref *send_ref = subscriber->send_ref;
    uint64_t delivery_connection_id = subscriber->connection_id;
    uint64_t callback_started_ms;
    uint64_t claim_started_ms;
    uint64_t deadline;

    memset(decision, 0, offsetof(ksi_lane_decision, inputs));

    /* Snapshots captured before quarantine was recorded skip immediately. */
    if (hook_send_ref_is_stalled(send_ref, hook_type_to_lane_index(event->hook_type))) {
        return KSI_SUBSCRIBER_SKIPPED;
    }

    claim_started_ms = g_verbose ? monotonic_ms() : 0u;

    if (!hook_send_ref_wait_event_turn(send_ref, event->event_id,
            event->nesting_depth != 0u, KSI_HOOK_DECISION_TIMEOUT_MS)) {
        if (lane_event_cancelled(lane, event)) {
            return KSI_SUBSCRIBER_EVENT_CANCELLED;
        }

        /* The callback was never entered. Its script thread was already busy;
         * the active turn owns any timeout accounting. This event alone passes. */
        return KSI_SUBSCRIBER_SKIPPED;
    }

    /* A flush may have won the race while this callback waited for its script's
     * shared keyboard/mouse turn. Leave the newly-entered frame to the caller's
     * normal release path, but never deliver the now-stale callback. */
    if (lane_event_cancelled(lane, event)) {
        return KSI_SUBSCRIBER_EVENT_CANCELLED;
    }

    if (g_verbose) {
        fprintf(stderr,
            "inputd: hook-chain claim event=%llu depth=%u hook=%u session=%llu waited-ms=%llu\n",
            (unsigned long long)event->event_id,
            event->nesting_depth,
            event->hook_type,
            (unsigned long long)subscriber->connection_id,
            (unsigned long long)(monotonic_ms() - claim_started_ms));
    }

    atomic_store(&lane->current_responder_connection_id, delivery_connection_id);
    callback_started_ms = monotonic_ms();

    if (hook_send_ref_send(send_ref, KSI_MESSAGE_HOOK_EVENT, 0,
            event->event_id, &event->payload, event->payload_size) != 0) {
        lane_clear_responder(lane);
        lane_post_hook_failure(lane->state, event->hook_type,
            event->event_id, subscriber->connection_id, event->nesting_depth, 0u,
            KSI_HOOK_QUARANTINE_REASON_TRANSPORT);
        return KSI_SUBSCRIBER_SKIPPED;
    }

    deadline = callback_started_ms + KSI_HOOK_DECISION_TIMEOUT_MS;

    if (lane_wait_for_matching_decision(
            lane, event, send_ref, delivery_connection_id, deadline, decision)) {
        lane_clear_responder(lane);
        hook_send_ref_clear_stalled(send_ref, hook_type_to_lane_index(event->hook_type));

        if (g_verbose) {
            fprintf(stderr,
                "inputd: hook-chain result event=%llu depth=%u hook=%u session=%llu decision=%u elapsed-ms=%llu\n",
                (unsigned long long)event->event_id,
                event->nesting_depth,
                event->hook_type,
                (unsigned long long)subscriber->connection_id,
                decision->decision,
                (unsigned long long)(monotonic_ms() - callback_started_ms));
        }

        return KSI_SUBSCRIBER_DECIDED;
    }

    lane_clear_responder(lane);

    if (lane_event_cancelled(lane, event)) {
        return KSI_SUBSCRIBER_EVENT_CANCELLED;
    }

    hook_send_ref_mark_stalled(send_ref, hook_type_to_lane_index(event->hook_type));
    lane_post_hook_failure(lane->state, event->hook_type,
        event->event_id, subscriber->connection_id, event->nesting_depth,
        (uint32_t)(monotonic_ms() - callback_started_ms),
        KSI_HOOK_QUARANTINE_REASON_TIMEOUT);

    return KSI_SUBSCRIBER_SKIPPED;
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
        ksi_lane_subscriber *subscriber = &event->subscribers[i];
        ksi_subscriber_result result;

        result = lane_call_subscriber(lane, event, subscriber, &decision);
        /* Leave this script's call stack immediately after its hook proc returns. */
        lane_event_release_subscriber_ref(event, i);

        if (result == KSI_SUBSCRIBER_SKIPPED) {
            continue;
        }

        if (result == KSI_SUBSCRIBER_EVENT_CANCELLED) {
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
                KSI_SYNTH_FLAG_BYPASS_HOOK | KSI_SYNTH_FLAG_BATCH_FRAGMENT,
                event->input_generation)) {
            bool fallback_ok;
            fprintf(stderr, "lane: modify synth enqueue failed for event %llu\n",
                (unsigned long long)event->event_id);

            fallback_ok = lane_event_enqueue_egress(lane, event);
            synthetic_ok = fallback_ok;

            if (!fallback_ok && event->egress.type == KSI_LANE_EGRESS_REPLAY) {
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

    lane_context_restore(lane, &parent_context);

    if (event->physical_input) {
        atomic_fetch_sub(&lane->state->physical_hook_inflight, 1u);
    }

    return synthetic_ok;
}

/* Best-effort low real-time priority for bounded hook/output latency. */
static bool set_realtime_priority(const char *thread_name)
{
    struct sched_param sp;
    memset(&sp, 0, sizeof(sp));
    sp.sched_priority = 1;

    if (pthread_setschedparam(pthread_self(), SCHED_FIFO, &sp) != 0) {
        fprintf(stderr,
            "inputd: note: could not set SCHED_FIFO for %s: %s\n",
            thread_name, strerror(errno));
        return false;
    }

    return true;
}

static bool set_normal_priority(const char *thread_name)
{
    struct sched_param sp;
    memset(&sp, 0, sizeof(sp));

    if (pthread_setschedparam(pthread_self(), SCHED_OTHER, &sp) != 0) {
        fprintf(stderr,
            "inputd: note: could not restore SCHED_OTHER for %s: %s\n",
            thread_name, strerror(errno));
        return false;
    }

    return true;
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
            (void)lane_process_nested_transaction(lane, orphaned);
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
    atomic_store(&lane->current_responder_connection_id, 0u);
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
        lane_decision_queue_wake(&lane->decision_queue);
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
