static void complete_synthetic_dispatch_fallback(
    ksi_daemon_state *state,
    const ksi_lane_egress *egress,
    ksi_synth_completion *completion)
{
    bool ok = true;

    if (completion == NULL) {
        return;
    }

    if (egress != NULL && egress->type == KSI_LANE_EGRESS_SYNTH) {
        ok = output_queue_push_synth(&state->output_queue,
            egress->synth_inputs,
            egress->synth_count,
            egress->synth_flags);
    }

    if (ok) {
        synth_completion_complete(completion);
    } else {
        synth_completion_fail(completion, -1, 12u);
    }

    synthetic_hook_queue_complete_inflight(state);
}

static void dispatch_hook_event_to_lane(
    ksi_daemon_state *state,
    uint32_t hook_type,
    const void *event,
    size_t event_size,
    const ksi_lane_egress *egress,
    ksi_synth_completion *completion)
{
    ksi_hook_lane *lane;
    ksi_lane_event *lane_event;
    uint32_t subscription_bit;

    lane = lane_for_hook_type(state, hook_type);

    if (lane == NULL || event_size > sizeof(lane_event->payload.event)) {
        complete_synthetic_dispatch_fallback(state, egress, completion);
        return;
    }

    subscription_bit = hook_type_to_cap_bit(hook_type);

    if (subscription_bit == 0u) {
        complete_synthetic_dispatch_fallback(state, egress, completion);
        return;
    }

    lane_event = calloc(1, sizeof(*lane_event));

    if (lane_event == NULL) {
        complete_synthetic_dispatch_fallback(state, egress, completion);
        return;
    }

    lane_event->event_id = state->next_event_id++;
    lane_event->hook_type = hook_type;
    lane_event->generation = atomic_load(&lane->flush_generation);
    lane_event->synthetic_completion = completion;

    if (egress != NULL) {
        lane_event->egress = *egress;
    }

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

        if (!hook_send_ref_note_event(c->hook_send_ref, lane_event->event_id)) {
            fprintf(stderr, "inputd: hook subscriber ordering queue full\n");
            hook_send_ref_release(c->hook_send_ref);
            continue;
        }

        lane_event->subscriber_send_refs[lane_event->subscriber_count] = c->hook_send_ref;
        lane_event->subscriber_response_fds[lane_event->subscriber_count] = c->fd;
        lane_event->subscriber_connection_ids[lane_event->subscriber_count] = c->connection_id;
        lane_event->subscriber_count++;
    }

    if (!lane_action_queue_push(&lane->action_queue, lane_event)) {
        bool egress_ok = lane_event_enqueue_egress(lane, lane_event);

        if (!egress_ok) {
            if (lane_event_egress_is_replay(lane_event)) {
                atomic_store(&g_fail_open_requested, 1);
                command_queue_wake(state->commands);
            }
        }

        fprintf(stderr, "inputd: hook lane queue full; bypassed hook event %llu\n",
            (unsigned long long)lane_event->event_id);
        lane_event_release_send_refs(lane_event);

        if (lane_event->synthetic_completion != NULL) {
            if (egress_ok) {
                synth_completion_complete(lane_event->synthetic_completion);
            } else {
                synth_completion_fail(lane_event->synthetic_completion, -1, 12u);
            }

            lane_event->synthetic_completion = NULL;
            synthetic_hook_queue_complete_inflight(state);
        }

        free(lane_event);
    }
}

/* Re-emit the physical Ctrl+Alt+Backspace trigger through uinput after the grab
 * has been released, so the chord reaches the X server (zap) and downstream sees
 * a consistent modifier-down/up sequence even if BlockInput hid the originals.
 * Bypasses the hook (KSI_SYNTH_FLAG_BYPASS_HOOK) since it is a passthrough, not a
 * client synthesis. */
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

    if (!output_queue_push_synth(&state->output_queue, inputs, 6u, KSI_SYNTH_FLAG_BYPASS_HOOK)) {
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

    /* Out-of-band rescue path independent of IPC or responsive clients. */
    if (!is_injected && hook_type == KSI_HOOK_KEYBOARD_LL) {
        const ksi_keyboard_hook_event *keyboard = event;
        uint32_t modifiers = ksi_linux_devices_get_modifier_state();

        if (keyboard->message == KSI_WM_KEYDOWN
            && keyboard->vk_code == KSI_VK_BACK
            && (modifiers & (KSI_MOD_LCONTROL | KSI_MOD_RCONTROL)) != 0u
            && (modifiers & (KSI_MOD_LALT | KSI_MOD_RALT)) != 0u) {
            fprintf(stderr,
                "inputd: physical Ctrl+Alt+Backspace emergency passthrough activated\n");

            /* Ctrl+Alt+Backspace has DUAL system semantics, so it is deliberately
             * NOT swallowed. (a) Emergency latch: fail open -- clear every hook /
             * BlockInput subscription and release all grabs -- so a stuck grab can
             * always be broken out of, exactly like the Ctrl+Alt+Pause panic combo.
             * (b) X11 zap passthrough: after ungrabbing, re-emit the literal chord
             * through uinput so the X server actually sees Ctrl+Alt+Backspace and
             * can restart if the user has zap enabled. Re-emitting AFTER the grab
             * is released means the chord goes out on our (ungrabbed) uinput node
             * and reaches X, not back into the grab. When zap is DISABLED the
             * Backspace instead reaches the focused app -- an accepted side effect
             * of an emergency combo, not worth extra complexity to suppress.
             *
             * NOTE: if more recognized system/global combos are ever added
             * (additional emergency latches, X restart variants, etc.) this ad-hoc
             * check should grow into a small system-hotkey table; only the
             * Ctrl+Alt+Backspace dual behavior is wired here for now. */
            clear_hook_state(state);

            if (update_grab_state(state) != 0) {
                fprintf(stderr, "inputd: emergency passthrough failed to release grabs\n");
            }

            /* BlockInput may have hidden the physical modifier downs from X; emit
             * the full chord so X sees a consistent Ctrl+Alt+Backspace. */
            enqueue_emergency_passthrough_chord(state, modifiers);
            return;
        }
    }

    if (!is_injected && (active_block_input_mask(state) & block_bit) != 0) {
        return;
    }

    if (!any_matching_hook_subscriptions(state, hook_type)) {
        /* Mixed devices may be grabbed for the other input class. */
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

    {
        ksi_lane_egress egress;

        memset(&egress, 0, sizeof(egress));
        egress.type = is_injected ? KSI_LANE_EGRESS_NONE : KSI_LANE_EGRESS_REPLAY;
        dispatch_hook_event_to_lane(state, hook_type, event, event_size, &egress, NULL);
    }
}
