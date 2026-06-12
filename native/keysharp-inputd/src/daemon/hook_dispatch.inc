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
            if (!output_queue_push_replay(&state->output_queue,
                    hook_type, &lane_event->payload, lane_event->payload_size)) {
                atomic_store(&g_fail_open_requested, 1);
                command_queue_wake(state->commands);
            }
        }

        fprintf(stderr, "inputd: hook lane queue full; bypassed hook event %llu\n",
            (unsigned long long)lane_event->event_id);
        lane_event_release_send_refs(lane_event);
        free(lane_event);
    }
}

static void enqueue_emergency_passthrough_chord(ksi_daemon_state *state, uint32_t modifiers)
{
    ksi_input inputs[6];
    uint16_t control_vk = (modifiers & KSI_MOD_LCONTROL) != 0u
        ? KSI_VK_LCONTROL
        : KSI_VK_RCONTROL;
    uint16_t alt_vk = (modifiers & KSI_MOD_LALT) != 0u
        ? KSI_VK_LALT
        : KSI_VK_RALT;

    memset(inputs, 0, sizeof(inputs));

    inputs[0].type = KSI_INPUT_KEYBOARD;
    inputs[0].data.keyboard.vk = control_vk;
    inputs[1].type = KSI_INPUT_KEYBOARD;
    inputs[1].data.keyboard.vk = alt_vk;
    inputs[2].type = KSI_INPUT_KEYBOARD;
    inputs[2].data.keyboard.vk = KSI_VK_BACK;

    inputs[3] = inputs[2];
    inputs[3].data.keyboard.flags = KSI_KEYEVENTF_KEYUP;
    inputs[4] = inputs[1];
    inputs[4].data.keyboard.flags = KSI_KEYEVENTF_KEYUP;
    inputs[5] = inputs[0];
    inputs[5].data.keyboard.flags = KSI_KEYEVENTF_KEYUP;

    if (!output_queue_push_synth(&state->output_queue, inputs, 6u, KSI_SYNTH_FLAG_BYPASS_HOOK, true)) {
        fprintf(stderr, "inputd: failed to enqueue emergency passthrough chord\n");
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

    /* Out-of-band rescue path which does not depend on IPC or a responsive
     * client. The device backend updates physical modifier state before this
     * callback, so Ctrl and Alt are visible when Backspace is pressed. */
    if (!is_injected && hook_type == KSI_HOOK_KEYBOARD_LL) {
        const ksi_keyboard_hook_event *keyboard = event;
        uint32_t modifiers = ksi_linux_devices_get_modifier_state();

        if (keyboard->message == KSI_WM_KEYDOWN
            && keyboard->vk_code == KSI_VK_BACK
            && (modifiers & (KSI_MOD_LCONTROL | KSI_MOD_RCONTROL)) != 0u
            && (modifiers & (KSI_MOD_LALT | KSI_MOD_RALT)) != 0u) {
            fprintf(stderr,
                "inputd: physical Ctrl+Alt+Backspace emergency passthrough activated\n");
            clear_hook_state(state);

            if (update_grab_state(state) != 0) {
                fprintf(stderr, "inputd: emergency passthrough failed to release grabs\n");
            }

            /* BlockInput may have prevented the display server from seeing the
             * preceding modifier downs, so emit the complete chord. */
            enqueue_emergency_passthrough_chord(state, modifiers);
            return;
        }
    }

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
                atomic_store(&g_fail_open_requested, 1);
                command_queue_wake(state->commands);
            }
        }
        return;
    }

    dispatch_hook_event_to_lane(state, hook_type, event, event_size, is_injected);
}

