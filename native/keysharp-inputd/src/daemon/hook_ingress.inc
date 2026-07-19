static uint32_t hook_type_for_input(const ksi_input *input)
{
    if (input == NULL) {
        return 0u;
    }

    switch (input->type) {
        case KSI_INPUT_KEYBOARD:
            return KSI_HOOK_KEYBOARD_LL;
        case KSI_INPUT_MOUSE:
            return KSI_HOOK_MOUSE_LL;
        default:
            return 0u;
    }
}

static bool synth_batch_needs_hook_queue(
    const ksi_daemon_state *state,
    const ksi_input *inputs,
    uint32_t count,
    uint32_t flags)
{
    uint32_t active_hooks;

    if ((flags & KSI_SYNTH_FLAG_BYPASS_HOOK) != 0u || state == NULL || inputs == NULL) {
        return false;
    }

    active_hooks = active_hook_subscription_mask(state);

    for (uint32_t i = 0; i < count; i++) {
        if ((hook_type_to_cap_bit(hook_type_for_input(&inputs[i])) & active_hooks) != 0u) {
            return true;
        }
    }

    return false;
}

static bool enqueue_synth_direct(
    ksi_daemon_state *state,
    const ksi_input *input,
    bool batch_start,
    uint64_t input_generation)
{
    /* BATCH_FRAGMENT: this is one event of an already-received client batch (the
     * synthetic hook queue re-emits them individually), so per-batch synthesis
     * state (the pending unicode high surrogate) must carry across to the next
     * fragment rather than being reset (see ksi_linux_synth_send_input).
     * BATCH_START on the first fragment re-arms that reset at the batch boundary. */
    uint32_t flags = KSI_SYNTH_FLAG_BYPASS_HOOK | KSI_SYNTH_FLAG_BATCH_FRAGMENT
        | (batch_start ? KSI_SYNTH_FLAG_BATCH_START : 0u);

    return output_queue_push_synth(
        &state->output_queue, input, 1u, flags, input_generation);
}

static bool enqueue_synth_direct_complete(
    ksi_daemon_state *state,
    const ksi_input *input,
    ksi_synth_completion *completion,
    bool batch_start,
    uint64_t input_generation)
{
    bool ok = enqueue_synth_direct(
        state, input, batch_start, input_generation);

    if (completion != NULL) {
        if (ok) {
            synth_completion_complete(completion);
        } else {
            synth_completion_fail(completion, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
        }
    }

    return ok;
}

/* Builds the hook-visible form shared by ordinary and callback-recursive
 * synthesis. A null event means the input has no active/representable hook and
 * should go straight to output; false means event allocation failed. */
static bool create_synthetic_lane_event(
    ksi_daemon_state *state,
    const ksi_input *input,
    bool batch_start,
    uint32_t nesting_depth,
    ksi_synth_completion *completion,
    ksi_lane_event **event,
    uint64_t input_generation)
{
    uint32_t hook_type = hook_type_for_input(input);
    ksi_hook_event_payload payload;
    ksi_lane_egress egress = {
        .type = KSI_LANE_EGRESS_SYNTH,
        .synth_count = 1u,
        .synth_flags = KSI_SYNTH_FLAG_BYPASS_HOOK | KSI_SYNTH_FLAG_BATCH_FRAGMENT
            | (batch_start ? KSI_SYNTH_FLAG_BATCH_START : 0u),
    };
    size_t event_size = 0u;

    *event = NULL;

    if (hook_type == 0u || !any_matching_hook_subscriptions(state, hook_type)
        || state->backend == NULL || state->backend->input_to_hook_event == NULL
        || !state->backend->input_to_hook_event(
            input, &hook_type, &payload, &event_size)) {
        return true;
    }

    egress.synth_inputs[0] = *input;
    *event = create_hook_lane_event(state, hook_type, &payload.event, event_size,
        &egress, completion, nesting_depth,
        input_generation);
    return *event != NULL;
}

static bool process_synthetic_hook_input(
    ksi_daemon_state *state,
    const ksi_input *input,
    ksi_synth_completion *completion,
    bool batch_start,
    uint64_t input_generation)
{
    ksi_lane_event *event;

    if (state == NULL || input == NULL) {
        synth_completion_fail(completion, -1, 1u);
        return false;
    }

    if (!create_synthetic_lane_event(state, input, batch_start, 0u,
            completion, &event, input_generation)) {
        synth_completion_fail(completion, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
        return false;
    }

    if (event == NULL) {
        return enqueue_synth_direct_complete(
            state, input, completion, batch_start, input_generation);
    }

    atomic_store(&state->synthetic_hook_inflight, 1);
    enqueue_hook_lane_event(state, event);
    return true;
}

static bool process_synthetic_hook_queue_one(ksi_daemon_state *state)
{
    ksi_input input;
    uint64_t queued_at_ns;
    ksi_synth_completion *completion = NULL;
    bool batch_start = false;
    uint64_t input_generation = 0u;

    if (state == NULL
        || atomic_load(&state->synthetic_hook_inflight) != 0
        || !synthetic_hook_queue_pop(&state->synthetic_hook_queue,
            &input, &queued_at_ns, &completion, &batch_start,
            &input_generation)) {
        return false;
    }

    (void)queued_at_ns;

    if (input_generation != 0u
        && input_generation != current_input_generation(state)) {
        synth_completion_fail(completion, -1, KSI_DETAIL_CANCELLED);
        return true;
    }

    if (batch_start) {
        synth_completion_begin_atomic(completion);
    }

    if (!process_synthetic_hook_input(
            state, &input, completion, batch_start, input_generation)) {
        fprintf(stderr, "inputd: failed to route synthetic hook input\n");
    }

    return true;
}

static bool process_hook_ingress(ksi_daemon_state *state)
{
    bool did_work = false;

    if (state == NULL) {
        return false;
    }

    for (size_t steps = 0; steps < KSI_MAX_SYNTH_HOOK_STEPS_PER_PASS; steps++) {
        uint64_t synthetic_time = 0u;
        uint64_t physical_time = 0u;
        int physical_fd = -1;
        bool can_process_physical = state->backend != NULL
            && state->backend->process_fd != NULL
            && state->backend->peek_oldest_pending_input != NULL;
        bool synthetic_inflight = atomic_load(&state->synthetic_hook_inflight) != 0;
        bool transaction_active = atomic_load(&state->active_synthetic_transactions) != 0u;
        bool physical_inflight = atomic_load(&state->physical_hook_inflight) != 0u;
        bool has_synthetic = synthetic_hook_queue_peek_time(
            &state->synthetic_hook_queue, &synthetic_time);
        bool has_physical = can_process_physical
            && state->backend->peek_oldest_pending_input(&physical_fd, &physical_time);

        if (!has_synthetic && !has_physical) {
            break;
        }

        if (has_physical && !transaction_active
            && (!has_synthetic || physical_time <= synthetic_time)) {
            state->backend->process_fd(physical_fd);
            did_work = true;
        } else if (has_synthetic && !synthetic_inflight && !physical_inflight
            && process_synthetic_hook_queue_one(state)) {
            did_work = true;
        } else if (has_synthetic && synthetic_inflight) {
            break;
        } else if (has_physical && !has_synthetic && !transaction_active) {
            state->backend->process_fd(physical_fd);
            did_work = true;
        } else {
            break;
        }
    }

    return did_work;
}
