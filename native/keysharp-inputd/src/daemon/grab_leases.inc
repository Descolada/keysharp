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
    uint32_t granted = 0u;
    uint32_t all_process_caps;
    uint32_t missing = 0u;

    if (out_missing != NULL) {
        *out_missing = 0u;
    }

    if (state == NULL || client == NULL || requested == 0u) {
        return 0u;
    }

    if (!client->has_identity || state->permissions == NULL) {
        return 0u;
    }

    expanded = permission_capabilities_for_request(requested) & state->available_capabilities;

    if (expanded != 0u) {
        granted = ksi_permissions_get_allowed_capabilities(
            state->permissions, client->uid, client->exe_hash) & expanded;

        /* Inherit per-process session grants from sibling clients of the same
         * Keysharp process (same uid+pid).  This is how "Allow once" decisions
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

        /* KSI_CAP_BLOCK_INPUT is implied by hook capabilities. */
        if ((expanded & KSI_CAP_BLOCK_INPUT) != 0u &&
            (all_process_caps & (KSI_CAP_HOOK_KEYBOARD | KSI_CAP_HOOK_MOUSE)) != 0u)
            granted |= KSI_CAP_BLOCK_INPUT;

        /* Check the shared PID-keyed session file for physical capabilities.
         * This picks up "Allow once" decisions that were written by any daemon
         * (including a prior keysharp-helper prompt) for this specific Keysharp process. */
        if (client->start_time != 0u) {
            uint32_t pid_session = ksi_permissions_get_session_by_pid(
                client->uid, client->pid, client->start_time);
            granted |= pid_session & expanded;
        }

        missing = expanded & ~granted;

        /* Persistent denials suppress prompts. */
        if (!force_prompt) {
            missing &= ~ksi_permissions_get_denied_capabilities(
                state->permissions, client->uid, client->exe_hash);
        }
    }

    /* Non-physical capabilities (e.g. screen capture) that inputd does not
     * provide directly, but brokers via the combined prompt so that the physical
     * daemon (keysharp-helper) can check the PID session and skip its own prompt.
     * Check persistent trust store and PID session; add to missing if neither
     * covers them, so the prompt includes them in the requested capability list. */
    {
        uint32_t non_physical = requested & ~state->available_capabilities;

        if (non_physical != 0u) {
            uint32_t np_granted = ksi_permissions_get_allowed_capabilities(
                state->permissions, client->uid, client->exe_hash) & non_physical;

            if (client->start_time != 0u)
                np_granted |= ksi_permissions_get_session_by_pid(
                    client->uid, client->pid, client->start_time) & non_physical;

            uint32_t np_missing = non_physical & ~np_granted;

            if (!force_prompt)
                np_missing &= ~ksi_permissions_get_denied_capabilities(
                    state->permissions, client->uid, client->exe_hash);

            missing |= np_missing;
        }
    }

    if (out_missing != NULL) {
        *out_missing = missing;
    }

    return granted;  /* physical capabilities only */
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

    /* Separate physical (inputd-managed) from non-physical (e.g. screen capture)
     * capabilities.  Both appear in `missing` because check_capabilities_sync
     * includes non-physical caps so they appear in the combined prompt. */
    uint32_t physical_missing    = missing &  state->available_capabilities;
    uint32_t non_physical_missing = missing & ~state->available_capabilities;
    uint32_t physical_requested  = requested & state->available_capabilities;

	if (decision == KSI_PERMISSION_DECISION_ALLOW_ALWAYS) {
		/* Grant ALL missing capabilities persistently — physical ones will be
		 * honoured by inputd on future connections; non-physical ones (e.g. screen
		 * capture) are stored in the shared trust store where keysharp-helper reads them. */
		(void)ksi_permissions_grant_persistent(
			state->permissions, client->uid, client->exe_hash, client->exe_path, missing);
	} else if (decision == KSI_PERMISSION_DECISION_PROMPT_UNAVAILABLE) {
		/* The prompt could not actually be shown — don't record a persistent denial. */
	} else if (decision != KSI_PERMISSION_DECISION_ALLOW_ONCE) {
		/* Persistent deny for ALL requested capabilities. */
		(void)ksi_permissions_deny_persistent(
			state->permissions, client->uid, client->exe_hash, client->exe_path, missing);
	}

    if (decision == KSI_PERMISSION_DECISION_ALLOW_ONCE && missing != 0u) {
        /* Write ALL granted capabilities (physical + non-physical) to the shared
         * PID session file.  keysharp-helper and other daemons read this file and skip
         * their own prompts when they find the relevant capability bits set. */
        if (client->start_time != 0u) {
            (void)ksi_permissions_grant_session_for_pid(
                client->uid, client->pid, client->start_time, missing);
        }
    }

    /* Recompute the physical capabilities actually granted for the HELLO response.
     * Non-physical caps are intentionally excluded — inputd cannot provide them. */
    granted = ksi_permissions_get_allowed_capabilities(state->permissions, client->uid, client->exe_hash)
              & (permission_capabilities_for_request(requested) & state->available_capabilities);

    if (decision == KSI_PERMISSION_DECISION_ALLOW_ONCE) {
        /* Physical missing caps are granted in-process; propagate to siblings. */
        granted |= physical_missing;
    }

    (void)non_physical_missing;  /* acknowledged; handled via PID session above */

    if (decision == KSI_PERMISSION_DECISION_DENY
        || decision == KSI_PERMISSION_DECISION_PROMPT_UNAVAILABLE
        || (granted & physical_requested) != physical_requested) {
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
        client->start_time = result->start_time;
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
    bool keyboard_grabbed;
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
    result = state->backend->set_block_input_mask == NULL
        ? (block_mask == 0 ? 0 : -1)
        : state->backend->set_block_input_mask(block_mask);

    if (result != 0) {
        return result;
    }

    /* When the keyboard grab is released, any key the daemon replayed "down" on the
     * uinput virtual device would be stranded there: the user's physical key-up lands
     * on the now-ungrabbed real device and does not cancel the uinput key, so the
     * compositor keeps seeing it held. Enqueue the release on the OUTPUT SEQUENCER
     * thread (not here) so it is serialized with replay/synth -- no race on the synth
     * state -- and the daemon main thread never blocks doing it. Fires only on the
     * held->released edge so a steady grab does not repeatedly release. */
    keyboard_grabbed = (hook_mask & KSI_CAP_HOOK_KEYBOARD) != 0
        || (block_mask & KSI_BLOCK_INPUT_KEYBOARD) != 0;

    if (state->keyboard_grab_active && !keyboard_grabbed) {
        /* Only consume the held->released edge once the release is actually queued.
         * If the enqueue fails (queue saturated / OOM), leave keyboard_grab_active set
         * so the next update_grab_state call -- there are many callers and the queue
         * drains continuously -- retries, rather than stranding a key "down" on uinput
         * with no recovery until the next grab cycle. */
        if (output_queue_push_release_all(&state->output_queue)) {
            state->keyboard_grab_active = false;
        } else {
            fprintf(stderr, "inputd: failed to enqueue synthetic key release on keyboard ungrab; will retry\n");
        }
    } else {
        state->keyboard_grab_active = keyboard_grabbed;
    }

    return 0;
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
        state->clients[i].lease_expires_ms = 0u;
    }
}

static void renew_client_lease(ksi_client *client)
{
    if (client == NULL) {
        return;
    }

    if (client->hook_subscriptions != 0u || client->block_input_mask != 0u) {
        client->lease_expires_ms = monotonic_ms() + KSI_GRAB_LEASE_TIMEOUT_MS;
    } else {
        client->lease_expires_ms = 0u;
    }
}

static void expire_client_leases(ksi_daemon_state *state)
{
    uint64_t now;

    if (state == NULL) {
        return;
    }

    now = monotonic_ms();

    for (nfds_t i = state->client_count; i > 0; i--) {
        nfds_t index = i - 1u;
        ksi_client *client = &state->clients[index];
        uint32_t expired_subscriptions;

        if (client->lease_expires_ms == 0u || now < client->lease_expires_ms) {
            continue;
        }

        expired_subscriptions = client->hook_subscriptions;
        fprintf(stderr,
            "inputd: client fd=%d grab lease expired; failing open and disconnecting\n",
            client->fd);

        if ((expired_subscriptions & KSI_CAP_HOOK_KEYBOARD) != 0u) {
            lane_flush_passthrough(&state->kbd_lane);
        }

        if ((expired_subscriptions & KSI_CAP_HOOK_MOUSE) != 0u) {
            lane_flush_passthrough(&state->mouse_lane);
        }

        remove_client(state, index);
    }
}

static void apply_fail_open_if_requested(ksi_daemon_state *state)
{
    if (state == NULL || atomic_exchange(&g_fail_open_requested, 0) == 0) {
        return;
    }

    fprintf(stderr,
        "inputd: safety-critical command queue overflow; clearing hooks and BlockInput\n");
    clear_hook_state(state);

    if (update_grab_state(state) != 0) {
        fprintf(stderr, "inputd: failed to release grabs during fail-open recovery\n");
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
    uint32_t failed_subscriptions;

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

    failed_subscriptions = client->hook_subscriptions;
    client->hook_subscriptions = 0;
    client->consecutive_hook_failures = 0;
    renew_client_lease(client);

    /* Invalidate queued snapshots immediately. A failed callback must not
     * cause every already-queued event to wait for the same dead client. */
    if ((failed_subscriptions & KSI_CAP_HOOK_KEYBOARD) != 0) {
        lane_flush_passthrough(&state->kbd_lane);
    }

    if ((failed_subscriptions & KSI_CAP_HOOK_MOUSE) != 0) {
        lane_flush_passthrough(&state->mouse_lane);
    }

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
