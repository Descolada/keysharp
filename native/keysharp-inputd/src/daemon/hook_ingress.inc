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

static bool enqueue_synth_direct(ksi_daemon_state *state, const ksi_input *input)
{
    return output_queue_push_synth(
        &state->output_queue, input, 1u, KSI_SYNTH_FLAG_BYPASS_HOOK);
}

static bool enqueue_synth_direct_complete(
    ksi_daemon_state *state,
    const ksi_input *input,
    ksi_synth_completion *completion)
{
    bool ok = enqueue_synth_direct(state, input);

    if (completion != NULL) {
        if (ok) {
            synth_completion_complete(completion);
        } else {
            synth_completion_fail(completion, -1, 12u);
        }
    }

    return ok;
}

static bool process_synthetic_hook_input(
    ksi_daemon_state *state,
    const ksi_input *input,
    ksi_synth_completion *completion)
{
    uint32_t hook_type = hook_type_for_input(input);
    ksi_hook_event_payload payload;
    ksi_lane_egress egress;
    size_t event_size;

    if (state == NULL || input == NULL) {
        synth_completion_fail(completion, -1, 1u);
        return false;
    }

    if (hook_type == 0u || !any_matching_hook_subscriptions(state, hook_type)) {
        return enqueue_synth_direct_complete(state, input, completion);
    }

    if (state->backend == NULL
        || state->backend->input_to_hook_event == NULL
        || !state->backend->input_to_hook_event(input, &hook_type, &payload, &event_size)) {
        fprintf(stderr, "inputd: synthetic input could not be represented as a hook event; sending directly\n");
        return enqueue_synth_direct_complete(state, input, completion);
    }

    memset(&egress, 0, sizeof(egress));
    egress.type = KSI_LANE_EGRESS_SYNTH;
    egress.synth_count = 1u;
    egress.synth_flags = KSI_SYNTH_FLAG_BYPASS_HOOK;
    egress.synth_inputs[0] = *input;

    atomic_store(&state->synthetic_hook_inflight, 1);
    dispatch_hook_event_to_lane(
        state,
        hook_type,
        &payload.event,
        event_size,
        &egress,
        completion);
    return true;
}

static bool process_synthetic_hook_queue_one(ksi_daemon_state *state)
{
    ksi_input input;
    uint64_t queued_at_ms;
    ksi_synth_completion *completion = NULL;

    if (state == NULL
        || atomic_load(&state->synthetic_hook_inflight) != 0
        || !synthetic_hook_queue_pop(&state->synthetic_hook_queue,
            &input, &queued_at_ms, &completion)) {
        return false;
    }

    (void)queued_at_ms;

    if (!process_synthetic_hook_input(state, &input, completion)) {
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
        bool has_synthetic = synthetic_hook_queue_peek_time(
            &state->synthetic_hook_queue, &synthetic_time);
        bool has_physical = can_process_physical
            && state->backend->peek_oldest_pending_input(&physical_fd, &physical_time);

        if (!has_synthetic && !has_physical) {
            break;
        }

        if (has_physical && (!has_synthetic || physical_time < synthetic_time)) {
            state->backend->process_fd(physical_fd);
            did_work = true;
        } else if (has_synthetic && !synthetic_inflight && process_synthetic_hook_queue_one(state)) {
            did_work = true;
        } else if (has_synthetic && synthetic_inflight) {
            break;
        } else if (has_physical) {
            state->backend->process_fd(physical_fd);
            did_work = true;
        } else {
            break;
        }
    }

    return did_work;
}
