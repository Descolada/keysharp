/* Per-uid FORCE_PROMPT cooldown. Returns true if this uid asked for a forced
 * prompt within KSI_FORCED_PROMPT_COOLDOWN_MS of its previous accepted one (so
 * the force should be dropped); otherwise records now for this uid and returns
 * false. Keyed by uid so a spamming user cannot suppress other users' re-asks. */
static bool forced_prompt_throttled(ksi_daemon_state *state, uid_t uid)
{
    uint64_t now = monotonic_ms();
    size_t n = sizeof(state->forced_prompt_by_uid) / sizeof(state->forced_prompt_by_uid[0]);
    size_t free_slot = n;
    size_t oldest = 0;
    uint64_t oldest_ms = UINT64_MAX;

    for (size_t i = 0; i < n; i++) {
        if (state->forced_prompt_by_uid[i].in_use && state->forced_prompt_by_uid[i].uid == uid) {
            if (now < state->forced_prompt_by_uid[i].last_ms + KSI_FORCED_PROMPT_COOLDOWN_MS) {
                return true;
            }

            state->forced_prompt_by_uid[i].last_ms = now;
            return false;
        }

        if (!state->forced_prompt_by_uid[i].in_use && free_slot == n) {
            free_slot = i;
        }

        if (state->forced_prompt_by_uid[i].in_use && state->forced_prompt_by_uid[i].last_ms < oldest_ms) {
            oldest_ms = state->forced_prompt_by_uid[i].last_ms;
            oldest = i;
        }
    }

    /* No slot for this uid yet: use a free slot, else evict the least-recent. */
    size_t slot = (free_slot != n) ? free_slot : oldest;
    state->forced_prompt_by_uid[slot].in_use = true;
    state->forced_prompt_by_uid[slot].uid = uid;
    state->forced_prompt_by_uid[slot].last_ms = now;
    return false;
}

static void handle_client_hello(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
	const ksi_client_hello_payload *payload;
	uint32_t requested = 0;
	uint32_t flags = 0u;
	bool force_prompt;
	uint32_t missing = 0u;
	uint32_t granted;

    if (!input_owner_matches_uid(state, client->uid)) {
        client->hello_seen = true;
        send_hello_status(client, message->header->client_id,
            message->header->correlation_id, -1, 0u);
        return;
    }

	if (message->payload_size != sizeof(*payload)) {
		send_hello_status(client, message->header->client_id,
			message->header->correlation_id, -1, 0u);
		return;
	}

	payload = (const ksi_client_hello_payload *)(const void *)message->payload;
	requested = payload->requested_capabilities;
	flags = payload->flags;

	if (payload->role > KSI_CONNECTION_HOOK_STREAM) {
		send_hello_status(client, message->header->client_id,
			message->header->correlation_id, -1, 0u);
		return;
	}

	if (!client->role_initialized) {
		client->connection_role = payload->role;
		client->role_initialized = true;
	} else if (client->connection_role != payload->role) {
		send_hello_status(client, message->header->client_id,
			message->header->correlation_id, -1, 0u);
		return;
	}

	force_prompt = (flags & KSI_CLIENT_HELLO_FLAG_FORCE_PROMPT) != 0u;

	/* Rate-limit client-forced prompts: within the cooldown, drop the force so the
	 * hello is served like a normal one (persistent denies stay suppressed and no
	 * new prompt is spawned). This blocks a FORCE_PROMPT spam loop without breaking a
	 * genuine, well-spaced RequestCapabilities re-ask. The cooldown is keyed by uid
	 * so one user on the shared 0666 system socket cannot suppress another user's
	 * force-prompt re-ask. */
	if (force_prompt && forced_prompt_throttled(state, client->uid)) {
		force_prompt = false;
	}

	/* The client completed its handshake; it is no longer subject to the
	 * unauthenticated-connection deadline. */
	client->hello_seen = true;

    /* Defer process hashing until the client actually asks for a privileged
     * capability. Query-only and short-lived connections need no identity
     * worker job. */
    if (requested != 0u && client->state == KSI_CLIENT_STATE_READY
        && !client->identity_attempted) {
        client->pending_hello_valid = true;
        client->pending_hello_requested = requested;
        client->pending_hello_flags = flags;
        client->pending_hello_correlation_id = message->header->correlation_id;
        client->pending_hello_client_id = message->header->client_id;
        client->identity_attempted = true;
        client->state = KSI_CLIENT_STATE_IDENTIFYING;

        if (!start_identify_task(state->commands, client->pid, client->fd, client->connection_id)) {
            client->pending_hello_valid = false;
            client->state = KSI_CLIENT_STATE_READY;
            send_hello_status(client,
                message->header->client_id, message->header->correlation_id, -1, 0u);
        }

        return;
    }

    /* If identification is still in progress, buffer the hello and defer. */
    if (client->state == KSI_CLIENT_STATE_IDENTIFYING) {
		client->pending_hello_valid = true;
		client->pending_hello_requested = requested;
		client->pending_hello_flags = flags;
		client->pending_hello_correlation_id = message->header->correlation_id;
		client->pending_hello_client_id = message->header->client_id;
        return;
    }

    /* If a prompt is already running for this client, reject the duplicate hello. */
    if (client->state == KSI_CLIENT_STATE_AWAITING_PROMPT) {
        send_hello_status(client, message->header->client_id, message->header->correlation_id, -1, 0);
        return;
    }

    /* Identity is known. Check what is already granted without prompting. */
	prepare_requested_capabilities(state, requested);
	granted = check_capabilities_sync(state, client, requested, force_prompt, &missing);

    /* If there are missing capabilities and a permissions store is configured,
     * start an async prompt. Response will be sent from process_client_prompt_done. */
    if (missing != 0u && state->permissions != NULL) {
        if (!start_prompt_task(state->commands, client->fd, client->connection_id, requested, missing,
                               state->available_capabilities,
                               client->exe_path, client->command_line, client->exe_hash,
                               client->pid, client->uid, client->gid)) {
            send_hello_status(client, message->header->client_id, message->header->correlation_id, -1, granted);
            return;
        }

		client->state = KSI_CLIENT_STATE_AWAITING_PROMPT;
		client->pending_hello_valid = true;
		client->pending_hello_requested = requested;
		client->pending_hello_flags = flags;
		client->pending_hello_correlation_id = message->header->correlation_id;
		client->pending_hello_client_id = message->header->client_id;
        return;
    }

    /* All requested capabilities already granted — respond immediately. */
    if (requested != 0u && (granted & requested) != requested) {
        /* `granted` here is scoped to THIS request only (see
         * check_capabilities_sync: masked by `expanded`), never to capabilities
         * granted earlier on this same connection for something else. Union
         * (not overwrite) so a denied/partial request can never claw back a
         * capability this connection already holds and may be actively using
         * (e.g. an installed hook). Likewise, `authenticated` gates every
         * capability check (client_has_capability), so it must reflect
         * "this connection holds at least one capability", not "the most
         * recent hello was fully satisfied" -- otherwise a later, unrelated,
         * partially-denied request would 403 capabilities it still legitimately
         * holds. */
        client->granted_capabilities |= granted;
        client->authenticated = client->authenticated || client->granted_capabilities != 0u;
        send_hello_status(client, message->header->client_id, message->header->correlation_id, -1, granted);
        return;
    }

	if (granted != 0u && client->has_identity) {
		ksi_permissions_note_seen(state->permissions, client->uid, client->exe_hash, client->exe_path);
	}

    client->authenticated = true;
    client->granted_capabilities |= granted;
    send_hello_status(client, message->header->client_id, message->header->correlation_id, 0, granted);
}

static void handle_emergency_passthrough(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    /* This is a rescue path for hook owners. Do not let an arbitrary
     * zero-capability hello on the public system socket clear other scripts. */
    if (!input_owner_matches_uid(state, client->uid)
        || !client->authenticated
        || (client->granted_capabilities
            & (KSI_CAP_HOOK_KEYBOARD | KSI_CAP_HOOK_MOUSE)) == 0u) {
        send_status(client->fd, message->header, KSI_MESSAGE_EMERGENCY_PASSTHROUGH, -1, 403);
        return;
    }

    clear_hook_state(state);

    if (update_grab_state(state) != 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_EMERGENCY_PASSTHROUGH, -1, 1);
        return;
    }

    send_status(client->fd, message->header, KSI_MESSAGE_EMERGENCY_PASSTHROUGH, 0, 0);
}

static void handle_get_indicator_state(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    if (!input_owner_matches_uid(state, client->uid)
        || !client->authenticated
        || (client->granted_capabilities
            & (KSI_CAP_HOOK_KEYBOARD | KSI_CAP_HOOK_MOUSE)) == 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_INDICATOR_STATE_RESULT, -1, 403);
        return;
    }

    send_indicator_state_result(client->fd, message->header);
}

static void handle_get_pointer_position(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    if (!input_owner_matches_uid(state, client->uid)
        || !client->authenticated
        || (client->granted_capabilities & KSI_CAP_HOOK_MOUSE) == 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_POINTER_POSITION_RESULT, -1, 403);
        return;
    }

    send_pointer_position_result(client->fd, message->header);
}

static void handle_get_pointer_buttons(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    if (!input_owner_matches_uid(state, client->uid)
        || !client->authenticated
        || (client->granted_capabilities & KSI_CAP_HOOK_MOUSE) == 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_POINTER_BUTTONS_RESULT, -1, 403);
        return;
    }

    send_pointer_buttons_result(client->fd, message->header);
}

static void handle_idle_time(
    const ksi_platform_backend *backend,
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    ksi_idle_time_payload result;

    /* Idle duration is presence metadata, not raw input content. Like desktop
     * idle-monitor D-Bus APIs, it requires an authenticated peer but no hook or
     * synthesis capability, and therefore never opens a trust prompt merely
     * because a script reads A_TimeIdle. */
    if (!input_owner_matches_uid(state, client->uid)
        || !client->authenticated) {
        send_status(client->fd, message->header, KSI_MESSAGE_IDLE_TIME, -1, 403);
        return;
    }

    memset(&result, 0, sizeof(result));

    if (backend != NULL && backend->get_idle_time != NULL) {
        (void)backend->get_idle_time(&result);
    }

    (void)ipc_send_locked(
        client->fd,
        KSI_MESSAGE_IDLE_TIME,
        message->header->client_id,
        message->header->correlation_id,
        &result,
        sizeof(result));
}

typedef struct ksi_list_permissions_context {
    int client_fd;
    uint32_t client_id;
    uint64_t correlation_id;
    bool send_failed;
} ksi_list_permissions_context;

static bool list_permissions_visitor(
    const ksi_permission_entry *entry,
    void *user_data)
{
    ksi_list_permissions_context *context = user_data;
    uint8_t buffer[sizeof(ksi_list_permissions_entry_payload) + KSI_PERMISSION_MAX_PATH];
    ksi_list_permissions_entry_payload *payload =
        (ksi_list_permissions_entry_payload *)(void *)buffer;
    size_t path_length = 0u;
    size_t total_size;

    if (context == NULL || entry == NULL) {
        return false;
    }

    /* Only stream records that carry at least one input capability bit. */
    if ((entry->persistent_allowed_capabilities & KSI_INPUT_CAPABILITIES) == 0u
        && (entry->persistent_denied_capabilities & KSI_INPUT_CAPABILITIES) == 0u) {
        return true;
    }

    if (entry->exe_path != NULL) {
        path_length = strnlen(entry->exe_path, KSI_PERMISSION_MAX_PATH);
    }

    memset(payload, 0, sizeof(*payload));
    payload->uid = (uint32_t)entry->uid;
    payload->persistent_allowed_capabilities = entry->persistent_allowed_capabilities;
    payload->persistent_denied_capabilities = entry->persistent_denied_capabilities;
    payload->path_length = (uint16_t)path_length;
    payload->last_seen_utc = entry->last_seen_utc;
    (void)snprintf(payload->exe_hash, sizeof(payload->exe_hash), "%s", entry->exe_hash);

    if (path_length > 0u) {
        memcpy(buffer + sizeof(*payload), entry->exe_path, path_length);
    }

    total_size = sizeof(*payload) + path_length;

    if (ipc_send_locked(
            context->client_fd,
            KSI_MESSAGE_LIST_PERMISSIONS_ENTRY,
            context->client_id,
            context->correlation_id,
            buffer,
            total_size) != 0) {
        context->send_failed = true;
        return false;
    }

    return true;
}

static void handle_list_permissions(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    ksi_list_permissions_context context;
    uid_t filter;

    if (state == NULL || client == NULL) {
        return;
    }

    filter = (geteuid() == 0 && client->uid == 0) ? (uid_t)-1 : client->uid;

    context.client_fd = client->fd;
    context.client_id = message->header->client_id;
    context.correlation_id = message->header->correlation_id;
    context.send_failed = false;

    if (state->permissions != NULL) {
        ksi_permissions_for_each(state->permissions, filter, list_permissions_visitor, &context);
    }

    send_status(
        client->fd,
        message->header,
        KSI_MESSAGE_LIST_PERMISSIONS_RESULT,
        context.send_failed ? -1 : 0,
        0u);
}

static void handle_reset_permissions(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    const ksi_reset_permissions_payload *payload;
    uid_t target_uid;
    uint32_t capabilities;
    char exe_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];

    if (state == NULL || client == NULL) {
        return;
    }

    if (state->permissions == NULL) {
        send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, -1, 500);
        return;
    }

    if (message->payload_size < sizeof(*payload)) {
        send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, -1, 400);
        return;
    }

    payload = (const ksi_reset_permissions_payload *)(const void *)message->payload;

    target_uid = (payload->target_uid == KSI_RESET_PERMISSIONS_UID_SELF)
        ? client->uid
        : (uid_t)payload->target_uid;

    /* Clamp to input capabilities — keysharp-helper manages its own domain. */
    capabilities = payload->capabilities & KSI_INPUT_CAPABILITIES;

    if (capabilities == 0u) {
        send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, -1, 400);
        return;
    }

    if (target_uid != client->uid && (geteuid() != 0 || client->uid != 0)) {
        send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, -1, 403);
        return;
    }

    (void)snprintf(exe_hash, sizeof(exe_hash), "%.*s",
        (int)sizeof(payload->exe_hash) - 1,
        payload->exe_hash);

    if (exe_hash[0] == '\0') {
        send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, -1, 400);
        return;
    }

    if (ksi_permissions_clear_persistent(state->permissions, target_uid, exe_hash, capabilities) != 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, -1, 500);
        return;
    }

    send_status(client->fd, message->header, KSI_MESSAGE_RESET_PERMISSIONS, 0, 0u);
}

static void handle_hook_subscription(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    const ksi_hook_subscription_payload *payload;
    uint32_t capability;
    uint32_t subscription_bit;
    uint32_t old_subscriptions;
    uint64_t old_ordinal;
    size_t lane_index;

    if (client->connection_role != KSI_CONNECTION_HOOK_STREAM) {
        send_status(client->fd, message->header, message->header->type, -1, 403);
        return;
    }

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

    if (!client_has_capability(client, capability)) {
        send_status(client->fd, message->header, message->header->type, -1, 403);
        return;
    }

    old_subscriptions = client->hook_subscriptions;
    lane_index = hook_type_to_lane_index(payload->hook_type);
    old_ordinal = client->hook_subscription_ordinal[lane_index];

    if (message->header->type == KSI_MESSAGE_SUBSCRIBE_HOOK) {
        if (!input_owner_matches_uid(state, client->uid)) {
            send_status(client->fd, message->header,
                message->header->type, -1, 403);
            return;
        }

        if ((client->hook_subscriptions & subscription_bit) == 0u) {
            uint64_t ordinal = ++state->next_hook_subscription_ordinal[lane_index];

            if (ordinal == 0u) {
                ordinal = ++state->next_hook_subscription_ordinal[lane_index];
            }

            client->hook_subscription_ordinal[lane_index] = ordinal;
        }

        client->hook_subscriptions |= subscription_bit;
    } else {
        client->hook_subscriptions &= (uint32_t)~subscription_bit;
        client->hook_subscription_ordinal[lane_index] = 0u;
    }

    if (update_grab_state(state) != 0) {
        client->hook_subscriptions = old_subscriptions;
        client->hook_subscription_ordinal[lane_index] = old_ordinal;
        (void)update_grab_state(state);
        send_status(client->fd, message->header, message->header->type, -1, 5);
        return;
    }

    renew_client_lease(client);
    send_status(client->fd, message->header, message->header->type, 0, client->hook_subscriptions);
}

static void handle_set_block_input(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    const ksi_block_input_payload *payload;
    uint32_t old_mask;

    if (message->payload_size != sizeof(*payload)) {
        send_status(client->fd, message->header, KSI_MESSAGE_SET_BLOCK_INPUT, -1, 1);
        return;
    }

    payload = (const ksi_block_input_payload *)(const void *)message->payload;

    if ((payload->block_mask & (uint32_t)~(KSI_BLOCK_INPUT_KEYBOARD | KSI_BLOCK_INPUT_MOUSE)) != 0u) {
        send_status(client->fd, message->header, KSI_MESSAGE_SET_BLOCK_INPUT, -1, 2);
        return;
    }

    if (!client_has_capability(client, KSI_CAP_BLOCK_INPUT)) {
        send_status(client->fd, message->header, KSI_MESSAGE_SET_BLOCK_INPUT, -1, 403);
        return;
    }

    if (payload->block_mask != 0u
        && !input_owner_matches_uid(state, client->uid)) {
        send_status(client->fd, message->header,
            KSI_MESSAGE_SET_BLOCK_INPUT, -1, 403);
        return;
    }

    old_mask = client->block_input_mask;
    client->block_input_mask = payload->block_mask;

    if (update_grab_state(state) != 0) {
        client->block_input_mask = old_mask;
        (void)update_grab_state(state);
        send_status(client->fd, message->header, KSI_MESSAGE_SET_BLOCK_INPUT, -1, 5);
        return;
    }

    renew_client_lease(client);
    send_status(client->fd, message->header, KSI_MESSAGE_SET_BLOCK_INPUT, 0, client->block_input_mask);
}

/* Validates the decision payload on the main thread, then routes it to the
 * lane that is currently processing the matching event_id.  The success ack is
 * written before the lane can observe the queued decision, preserving stream
 * order for clients which read HOOK_DECISION status before the next HOOK_EVENT. */
static void handle_hook_decision(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    const ksi_hook_decision_payload *payload;
    size_t expected_size;
    ksi_hook_lane *lane;
    ksi_lane_decision decision;

    if (message->payload_size < sizeof(*payload)) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1,
            KSI_HOOK_DETAIL_PAYLOAD_TOO_SMALL);
        return;
    }

    payload = (const ksi_hook_decision_payload *)(const void *)message->payload;

    if (payload->input_count > KSI_MAX_MODIFY_INPUTS) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1,
            KSI_HOOK_DETAIL_INPUT_COUNT_LIMIT);
        return;
    }

    expected_size = sizeof(*payload) + ((size_t)payload->input_count * sizeof(payload->inputs[0]));

    if (message->payload_size != expected_size) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1,
            KSI_HOOK_DETAIL_PAYLOAD_SIZE_MISMATCH);
        return;
    }

    if (payload->decision != KSI_HOOK_DECISION_PASS
        && payload->decision != KSI_HOOK_DECISION_BLOCK
        && payload->decision != KSI_HOOK_DECISION_MODIFY) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1,
            KSI_HOOK_DETAIL_INVALID_DECISION);
        return;
    }

    if (payload->decision == KSI_HOOK_DECISION_MODIFY && payload->input_count == 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1,
            KSI_HOOK_DETAIL_EMPTY_MODIFY);
        return;
    }

    if (state == NULL) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1,
            KSI_HOOK_DETAIL_STALE_OR_WRONG_RESPONDER);
        return;
    }

    if (!input_owner_matches_uid(state, client->uid)) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1,
            KSI_HOOK_DETAIL_STALE_OR_WRONG_RESPONDER);
        return;
    }

    if (payload->decision == KSI_HOOK_DECISION_MODIFY) {
        if (!client_has_capability(
                client,
                required_synthesis_capabilities(payload->inputs, payload->input_count))) {
            send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1,
                KSI_DETAIL_PERMISSION_DENIED);
            return;
        }
    }

    /* Locate the lane currently waiting on this event_id.  If neither lane
     * has it as their current event, the decision is stale (timeout already
     * fired or the lane already finalized via BLOCK/MODIFY from someone else). */
    lane = lane_for_event_id(state, payload->event_id);

    if (lane == NULL) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1,
            KSI_HOOK_DETAIL_STALE_OR_WRONG_RESPONDER);
        return;
    }

    if (atomic_load(&lane->current_responder_connection_id) != client->connection_id) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1,
            KSI_HOOK_DETAIL_STALE_OR_WRONG_RESPONDER);
        return;
    }

    /* Header only; the inputs tail is filled (bounded) by the memcpy below and
     * only ever read up to input_count. */
    memset(&decision, 0, offsetof(ksi_lane_decision, inputs));
    decision.event_id = payload->event_id;
    decision.decision = payload->decision;
    decision.responder_connection_id = client->connection_id;
    decision.input_count = payload->input_count;

    if (payload->input_count > 0u) {
        memcpy(decision.inputs, payload->inputs,
            (size_t)payload->input_count * sizeof(payload->inputs[0]));
    }

    if (!lane_decision_queue_push_with_ack(
            &lane->decision_queue,
            &decision,
            client->hook_send_ref,
            message->header,
            payload->decision)) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1,
            KSI_DETAIL_RESOURCE_EXHAUSTED);
        return;
    }

    {
        /* Resolve the responder under the connection_id guard so we don't
         * credit success on a freshly-accepted client that happens to have
         * inherited this fd from a prior disconnect. */
        ssize_t resolved = find_client_index_by_fd(state, client->fd);

        if (resolved >= 0
            && state->clients[resolved].connection_id == client->connection_id) {
            record_client_hook_success(state, (nfds_t)resolved, lane->hook_type);
            renew_client_lease(&state->clients[resolved]);
        }
    }

    /* Success ack was sent by lane_decision_queue_push_with_ack before waking
     * the lane, so no subsequent HOOK_EVENT can overtake it on this stream. */
}

static void handle_synthesize_input(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    const ksi_synthesize_input_payload *payload;
    size_t expected_size;
    uint64_t input_generation;
    uint64_t parent_hook_event_id;

    if (!input_owner_matches_uid(state, client->uid)) {
        send_status(client->fd, message->header,
            KSI_MESSAGE_SYNTHESIS_RESULT, -1, KSI_DETAIL_PERMISSION_DENIED);
        return;
    }

    input_generation = current_input_generation(state);

    if (message->payload_size < sizeof(*payload)) {
        send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1,
            KSI_DETAIL_PAYLOAD_TOO_SMALL);
        return;
    }

    payload = (const ksi_synthesize_input_payload *)(const void *)message->payload;

    if (payload->count > KSI_MAX_SYNTH_INPUTS) {
        send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1,
            KSI_DETAIL_INPUT_COUNT_LIMIT);
        return;
    }

    expected_size = sizeof(*payload) + ((size_t)payload->count * sizeof(payload->inputs[0]));

    if (message->payload_size != expected_size) {
        send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1,
            KSI_DETAIL_PAYLOAD_SIZE_MISMATCH);
        return;
    }

    if (!client_has_capability(
            client,
            required_synthesis_capabilities(payload->inputs, payload->count))) {
        send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1,
            KSI_DETAIL_PERMISSION_DENIED);
        return;
    }

    /* BYPASS_HOOK is the only client-legal flag. BATCH_FRAGMENT/REPLAY/BATCH_START
     * are daemon-internal (they gate the surrogate-reset); strip anything else a
     * client sets so it cannot splice surrogate state or reach internal paths. */
    uint32_t synth_flags = payload->flags & KSI_SYNTH_FLAG_BYPASS_HOOK;
    parent_hook_event_id = client->connection_role == KSI_CONNECTION_HOOK_STREAM
        ? message->header->correlation_id
        : 0u;

    if (client->connection_role == KSI_CONNECTION_HOOK_STREAM) {
        ksi_hook_lane *active_lane = lane_for_event_id(
            state, parent_hook_event_id);
        ksi_nested_transaction *transaction = NULL;
        ksi_synth_completion *completion = NULL;
        size_t expanded_count = 0u;
        uint32_t depth;

        if (parent_hook_event_id == 0u) {
            send_status(client->fd, message->header,
                KSI_MESSAGE_SYNTHESIS_RESULT, -1, KSI_DETAIL_CALLBACK_TIMEOUT);
            return;
        }

        if (active_lane == NULL
            || atomic_load(&active_lane->current_responder_connection_id)
                != client->connection_id) {
            /* A late callback may still owe a modifier release. Preserve it, but
             * never route stale work back into the now-idle HookStream. */
            if (!output_queue_push_synth(&state->output_queue, payload->inputs,
                    payload->count, synth_flags | KSI_SYNTH_FLAG_BYPASS_HOOK,
                    input_generation)) {
                send_status(client->fd, message->header,
                    KSI_MESSAGE_SYNTHESIS_RESULT, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
                return;
            }

            send_status(client->fd, message->header,
                KSI_MESSAGE_SYNTHESIS_RESULT, 0, 0u);
            return;
        }

        depth = atomic_load(&active_lane->current_nesting_depth) + 1u;

        if (depth > KSI_MAX_RECURSION_DEPTH) {
            send_status(client->fd, message->header,
                KSI_MESSAGE_SYNTHESIS_RESULT, -1, KSI_DETAIL_RECURSION_LIMIT);
            return;
        }

        if ((synth_flags & KSI_SYNTH_FLAG_BYPASS_HOOK) != 0u
            || payload->count == 0u) {
            if (!output_queue_push_synth(&state->output_queue, payload->inputs,
                    payload->count, synth_flags, input_generation)) {
                send_status(client->fd, message->header,
                    KSI_MESSAGE_SYNTHESIS_RESULT, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
                return;
            }

            send_status(client->fd, message->header,
                KSI_MESSAGE_SYNTHESIS_RESULT, 0, 0u);
            return;
        }

        if (!ksi_synthetic_hook_batch_count(payload->inputs, payload->count,
                KSI_MAX_SYNTH_HOOK_ACTIONS, &expanded_count)) {
            send_status(client->fd, message->header,
                KSI_MESSAGE_SYNTHESIS_RESULT, -1, KSI_DETAIL_EXPANDED_INPUT_LIMIT);
            return;
        }

        completion = synth_completion_create(
            state, client->hook_send_ref, message->header);
        transaction = calloc(1, sizeof(*transaction)
            + expanded_count * sizeof(transaction->members[0]));

        if (completion == NULL || transaction == NULL) {
            if (completion != NULL) {
                synth_completion_fail(completion, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
            } else {
                send_status(client->fd, message->header,
                    KSI_MESSAGE_SYNTHESIS_RESULT, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
            }

            free(transaction);
            return;
        }

        transaction->count = (uint32_t)expanded_count;
        transaction->depth = depth;
        transaction->origin_generation = atomic_load(&active_lane->flush_generation);
        transaction->input_generation = input_generation;
        transaction->origin_connection_id = client->connection_id;
        transaction->parent_hook_event_id = parent_hook_event_id;
        transaction->completion = completion;

        if (!synth_completion_begin_recursive_synthesis(
                completion, parent_hook_event_id)) {
            synth_completion_fail(completion, -1, KSI_DETAIL_CALLBACK_TIMEOUT);
            transaction->completion = NULL;
            nested_transaction_destroy(transaction);
            return;
        }

        for (uint32_t i = 0u, offset = 0u; i < payload->count; i++) {
            ksi_input expanded[KSI_SYNTHETIC_HOOK_MAX_EXPANSION];
            size_t count = ksi_synthetic_hook_expand_input(&payload->inputs[i], expanded);

            for (size_t j = 0u; j < count; j++) {
                transaction->members[offset++].input = expanded[j];
            }
        }

        for (uint32_t i = 0u; i < transaction->count; i++) {
            ksi_nested_member *member = &transaction->members[i];

            if (!create_synthetic_lane_event(state, &member->input, i == 0u,
                    depth, NULL, &member->event,
                    input_generation)) {
                synth_completion_fail(completion, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
                transaction->completion = NULL;
                nested_transaction_destroy(transaction);
                return;
            }
        }

        synth_completion_begin_atomic(completion);

        if (!ksi_pipe_ring_push(&active_lane->nested_queue.ring, &transaction)) {
            synth_completion_fail(completion, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
            transaction->completion = NULL;
            nested_transaction_destroy(transaction);
            return;
        }

        lane_decision_queue_wake(&active_lane->decision_queue);
        return;
    }

    if (synth_batch_needs_hook_queue(state, payload->inputs, payload->count, synth_flags)) {
        size_t expanded_count = 0u;

        if (!ksi_synthetic_hook_batch_count(payload->inputs, payload->count,
                KSI_MAX_SYNTH_HOOK_ACTIONS, &expanded_count)) {
            send_status(client->fd, message->header,
                KSI_MESSAGE_SYNTHESIS_RESULT, -1, KSI_DETAIL_EXPANDED_INPUT_LIMIT);
            return;
        }

        /* Like Win32 SendInput, an ordinary sender waits only for admission to
         * the input queue. The detached completion keeps the accepted batch
         * atomic, but never holds this RPC open while hook callbacks run. */
        ksi_synth_completion *completion = synth_completion_create(
            state, NULL, NULL);

        if (completion == NULL) {
            send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1,
                KSI_DETAIL_RESOURCE_EXHAUSTED);
            return;
        }

        if (!synthetic_hook_queue_push(
                &state->synthetic_hook_queue,
                payload->inputs,
                payload->count,
                expanded_count,
                monotonic_ns(),
                completion,
                input_generation)) {
            synth_completion_fail(completion, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
            send_status(client->fd, message->header,
                KSI_MESSAGE_SYNTHESIS_RESULT, -1, KSI_DETAIL_RESOURCE_EXHAUSTED);
            return;
        }

        send_status(client->fd, message->header,
            KSI_MESSAGE_SYNTHESIS_RESULT, 0, 0u);
        return;
    } else {
        if (!output_queue_push_synth(&state->output_queue,
                payload->inputs, payload->count, synth_flags,
                input_generation)) {
            send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1,
                KSI_DETAIL_RESOURCE_EXHAUSTED);
            return;
        }
    }

    send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, 0, 0);
}

static void handle_binary_message(
    const ksi_platform_backend *backend,
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    /* Any message keeps the connection out of the capless-idle reaper. */
    client->last_activity_ms = monotonic_ms();

    if (g_verbose) {
        printf("client %d binary type=%u size=%u correlation=%llu\n",
            client->fd,
            message->header->type,
            message->header->size,
            (unsigned long long)message->header->correlation_id);
    }

    switch (message->header->type) {
        case KSI_MESSAGE_CLIENT_HELLO:
            handle_client_hello(state, client, message);
            return;
        case KSI_MESSAGE_HEARTBEAT:
            renew_client_lease(client);

            /* Protocol 1.0 permits one-way lease heartbeats on hook-reader
             * connections. Correlation zero deliberately has no response. */
            if (message->header->correlation_id != 0u) {
                send_status(client->fd, message->header, KSI_MESSAGE_HEARTBEAT, 0, 0);
            }
            return;
        case KSI_MESSAGE_EMERGENCY_PASSTHROUGH:
            handle_emergency_passthrough(state, client, message);
            return;
        case KSI_MESSAGE_SET_BLOCK_INPUT:
            handle_set_block_input(state, client, message);
            return;
        case KSI_MESSAGE_GET_INDICATOR_STATE:
            handle_get_indicator_state(state, client, message);
            return;
        case KSI_MESSAGE_GET_POINTER_POSITION:
            handle_get_pointer_position(state, client, message);
            return;
        case KSI_MESSAGE_GET_POINTER_BUTTONS:
            handle_get_pointer_buttons(state, client, message);
            return;
        case KSI_MESSAGE_GET_KEY_STATE:
            handle_get_key_state(state, client, message);
            return;
        case KSI_MESSAGE_IDLE_TIME:
            handle_idle_time(backend, state, client, message);
            return;
        case KSI_MESSAGE_LIST_PERMISSIONS:
            handle_list_permissions(state, client, message);
            return;
        case KSI_MESSAGE_RESET_PERMISSIONS:
            handle_reset_permissions(state, client, message);
            return;
        case KSI_MESSAGE_SUBSCRIBE_HOOK:
        case KSI_MESSAGE_UNSUBSCRIBE_HOOK:
            handle_hook_subscription(state, client, message);
            return;
        case KSI_MESSAGE_HOOK_DECISION:
            handle_hook_decision(state, client, message);
            return;
        case KSI_MESSAGE_SYNTHESIZE_INPUT:
            handle_synthesize_input(state, client, message);
            return;
        default:
            send_status(client->fd, message->header, message->header->type, -1, 404);
            return;
    }
}

static bool process_client_buffer_direct(ksi_daemon_state *state, ksi_client *client)
{
    size_t offset = 0u;

    while (client->rx_used - offset >= sizeof(ksi_message_header)) {
        ksi_message_header header;
        ksi_binary_message_view message;

        memcpy(&header, client->rx_buffer + offset, sizeof(header));

        if (header.size < sizeof(header) || header.size > KSI_MAX_MESSAGE_SIZE) {
            fprintf(stderr, "client %d sent invalid frame size %u\n", client->fd, header.size);
            return false;
        }

        if (header.major != KSI_PROTOCOL_MAJOR || header.minor != KSI_PROTOCOL_MINOR) {
            /* Name the daemon's own version and build so a mismatch is visible
             * from the daemon log (e.g. a newer client hitting a stale daemon). */
            fprintf(stderr,
                "client %d sent unsupported protocol version %u.%u; "
                "this daemon speaks %u.%u (%s)\n",
                client->fd,
                header.major,
                header.minor,
                (unsigned)KSI_PROTOCOL_MAJOR,
                (unsigned)KSI_PROTOCOL_MINOR,
                KSI_BUILD_VERSION);
            return false;
        }

        if (client->rx_used - offset < header.size) {
            break;
        }

        message.header = (const ksi_message_header *)(const void *)(client->rx_buffer + offset);
        message.payload = client->rx_buffer + offset + sizeof(header);
        message.payload_size = header.size - sizeof(header);
        handle_binary_message(state->backend, state, client, &message);
        offset += header.size;
    }

    if (offset > 0u) {
        if (offset < client->rx_used) {
            memmove(client->rx_buffer, client->rx_buffer + offset, client->rx_used - offset);
        }

        client->rx_used -= offset;
    }

    return true;
}

static int read_client_frames_direct(ksi_daemon_state *state, ksi_client *client)
{
    for (int reads = 0; reads < KSI_MAX_CLIENT_READS_PER_PASS; reads++) {
        ssize_t bytes_read;
        size_t available = KSI_MAX_MESSAGE_SIZE - client->rx_used;

        if (available == 0u) {
            fprintf(stderr, "client %d receive buffer overflow\n", client->fd);
            return -1;
        }

        bytes_read = read(client->fd, client->rx_buffer + client->rx_used, available);

        if (bytes_read == 0) {
            return 0;
        }

        if (bytes_read < 0) {
            if (errno == EINTR) {
                reads--;  /* EINTR is not a real read; don't spend budget on it */
                continue;
            }

            return errno == EAGAIN || errno == EWOULDBLOCK ? 1 : -1;
        }

        client->rx_used += (size_t)bytes_read;

        if (!process_client_buffer_direct(state, client)) {
            return -1;
        }

        if ((size_t)bytes_read < available) {
            return 1;
        }
    }

    /* Budget exhausted while the client still had a full buffer of data each
     * read. Keep the client (return 1, not 0/-1): unread bytes stay in the kernel
     * recv buffer and the fd remains POLLIN-readable, so the next poll iteration
     * re-enters here — now interleaved with device/hook ingress. */
    return 1;
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

    (void)ipc_send_locked(
        client_fd,
        KSI_MESSAGE_INDICATOR_STATE_RESULT,
        request->client_id,
        request->correlation_id,
        &result,
        sizeof(result));
}

static void handle_get_key_state(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    ksi_key_state_payload result;
    bool caps = false, num = false, scroll = false;

    if (!input_owner_matches_uid(state, client->uid)
        || !client->authenticated
        || (client->granted_capabilities & KSI_CAP_HOOK_KEYBOARD) == 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_KEY_STATE_RESULT, -1, 403);
        return;
    }

    /* Refresh the indicator state directly from hardware (EVIOCGLED) before
     * reading it, so callers get the current LED state rather than the
     * potentially stale EV_LED-derived cache. This is important for toggle-key
     * checks immediately after a synthetic CapsLock/NumLock/ScrollLock event
     * (e.g. the ToggleKeyState call in SendKeys), where the compositor's LED
     * acknowledgement may not yet have produced an EV_LED event. */
    ksi_linux_devices_refresh_indicator_state();

    memset(&result, 0, sizeof(result));
    (void)ksi_linux_devices_get_key_state(&result);
    ksi_linux_devices_get_indicator_state(&caps, &num, &scroll);
    result.caps_lock   = caps   ? 1u : 0u;
    result.num_lock    = num    ? 1u : 0u;
    result.scroll_lock = scroll ? 1u : 0u;

    (void)ipc_send_locked(
        client->fd,
        KSI_MESSAGE_KEY_STATE_RESULT,
        message->header->client_id,
        message->header->correlation_id,
        &result,
        sizeof(result));
}

static void send_pointer_position_result(int client_fd, const ksi_message_header *request)
{
    ksi_pointer_position_payload result;

    memset(&result, 0, sizeof(result));
    (void)ksi_linux_devices_get_pointer_position(&result);

    (void)ipc_send_locked(
        client_fd,
        KSI_MESSAGE_POINTER_POSITION_RESULT,
        request->client_id,
        request->correlation_id,
        &result,
        sizeof(result));
}

static void send_pointer_buttons_result(int client_fd, const ksi_message_header *request)
{
    ksi_pointer_buttons_payload result;

    memset(&result, 0, sizeof(result));
    (void)ksi_linux_devices_get_pointer_buttons(&result);

    (void)ipc_send_locked(
        client_fd,
        KSI_MESSAGE_POINTER_BUTTONS_RESULT,
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

static ssize_t find_client_index_by_connection(
    const ksi_daemon_state *state,
    int client_fd,
    uint64_t connection_id)
{
    ssize_t index = find_client_index_by_fd(state, client_fd);

    if (index < 0 || connection_id == 0u) {
        return -1;
    }

    return state->clients[index].connection_id == connection_id ? index : -1;
}

static void add_client(
    ksi_daemon_state *state,
    int client_fd,
    const ksi_ipc_peer_credentials *credentials)
{
    ksi_client *client;

    if (state == NULL || credentials == NULL) {
        return;
    }

    if (state->client_count >= KSI_MAX_CLIENTS) {
        ksi_ipc_close_client(client_fd);
        return;
    }

    /* Reserve headroom so one uid cannot occupy every slot and lock out others. */
    {
        nfds_t same_uid = 0;

        for (nfds_t i = 0; i < state->client_count; i++) {
            if (state->clients[i].uid == credentials->uid) {
                same_uid++;
            }
        }

        if (same_uid >= KSI_MAX_CLIENTS_PER_UID) {
            fprintf(stderr,
                "inputd: rejecting client fd=%d uid=%ld: per-uid connection limit (%u) reached\n",
                client_fd, (long)credentials->uid, (unsigned)KSI_MAX_CLIENTS_PER_UID);
            ksi_ipc_close_client(client_fd);
            return;
        }
    }

    client = &state->clients[state->client_count];
    memset(client, 0, sizeof(*client));
    client->fd = client_fd;
    client->rx_buffer = malloc(KSI_MAX_MESSAGE_SIZE);

    if (client->rx_buffer == NULL) {
        ksi_ipc_close_client(client_fd);
        return;
    }

    client->hook_send_ref = hook_send_ref_create(client_fd);

    if (client->hook_send_ref == NULL) {
        fprintf(stderr, "inputd: failed to create hook send handle for client fd=%d: %s\n",
            client_fd, strerror(errno));
        free(client->rx_buffer);
        client->rx_buffer = NULL;
        ksi_ipc_close_client(client_fd);
        return;
    }

    client->connection_id = state->next_connection_id++;

    if (client->connection_id == 0u) {
        client->connection_id = state->next_connection_id++;
    }

    client->pid = credentials->pid;
    client->uid = credentials->uid;
    client->gid = credentials->gid;
    /* Snapshot the process start time now; the identity worker re-reads it later
     * and process_client_identified rejects the identity if it changed (pid
     * recycled between accept and the /proc read). */
    client->accept_start_time = ksi_permissions_get_process_start_time(credentials->pid);
    client->state = KSI_CLIENT_STATE_READY;
    client->connected_at_ms = monotonic_ms();
    client->last_activity_ms = client->connected_at_ms;
    client->hello_seen = false;
    state->client_count++;

    if (g_verbose) {
        printf("accepted client fd=%d pid=%ld uid=%ld gid=%ld\n",
            client->fd,
            (long)client->pid,
            (long)client->uid,
            (long)client->gid);
    }
}

static bool peer_uid_allowed(
    const ksi_daemon_state *state,
    uid_t peer_uid,
    bool system_service)
{
    return system_service
        ? input_owner_matches_uid(state, peer_uid)
        : peer_uid == getuid();
}

/* A switched-away user's transports are retained for seamless reactivation,
 * but they must never prevent the newly active uid from reaching the broker.
 * clients[] remains in acceptance order, so the first inactive entry is the
 * oldest eviction candidate. Active-owner clients are never displaced here;
 * their existing per-uid limit remains the admission bound. */
static bool ensure_active_client_slot(ksi_daemon_state *state)
{
    if (state == NULL) {
        return false;
    }

    if (state->client_count < KSI_MAX_CLIENTS) {
        return true;
    }

    for (nfds_t i = 0u; i < state->client_count; i++) {
        if (!input_owner_matches_uid(state, state->clients[i].uid)) {
            fprintf(stderr,
                "inputd: evicting oldest inactive client fd=%d uid=%ld "
                "to admit active seat0 user\n",
                state->clients[i].fd, (long)state->clients[i].uid);
            remove_client(state, i);
            return state->client_count < KSI_MAX_CLIENTS;
        }
    }

    return false;
}

static bool accept_client(ksi_daemon_state *state, ksi_ipc_server *server, bool system_service)
{
    ksi_ipc_peer_credentials credentials;
    int client_fd = ksi_ipc_accept_client(server);

    if (client_fd < 0) {
        return false;
    }

    if (ksi_ipc_get_peer_credentials(client_fd, &credentials) != 0) {
        ksi_ipc_close_client(client_fd);
        return true;
    }

    if (!peer_uid_allowed(state, credentials.uid, system_service)) {
        if (!system_service) {
            fprintf(stderr,
                "rejected client pid=%ld uid=%ld gid=%ld: daemon uid is %ld\n",
                (long)credentials.pid,
                (long)credentials.uid,
                (long)credentials.gid,
                (long)getuid());
            ksi_ipc_close_client(client_fd);
            return true;
        }

        fprintf(stderr,
            "rejected client pid=%ld uid=%ld gid=%ld: not active seat0 uid\n",
            (long)credentials.pid,
            (long)credentials.uid,
            (long)credentials.gid);
        ksi_ipc_close_client(client_fd);
        return true;
    }

    if (!ensure_active_client_slot(state)) {
        fprintf(stderr,
            "inputd: rejecting active client pid=%ld uid=%ld: global connection limit reached\n",
            (long)credentials.pid, (long)credentials.uid);
        ksi_ipc_close_client(client_fd);
        return true;
    }

    add_client(state, client_fd, &credentials);
    return true;
}

static void process_daemon_command(ksi_daemon_state *state, ksi_daemon_command *command)
{
    if (state == NULL || command == NULL) {
        return;
    }

    switch (command->type) {
        case KSI_DAEMON_COMMAND_CLIENT_IDENTIFIED:
            process_client_identified(state, command);
            break;

        case KSI_DAEMON_COMMAND_CLIENT_PROMPT_DONE:
            process_client_prompt_done(state, command);
            break;

        case KSI_DAEMON_COMMAND_LANE_HOOK_FAILURE: {
            ssize_t client_index = -1;

            for (nfds_t i = 0; i < state->client_count; i++) {
                if (state->clients[i].connection_id
                        == command->connection_id) {
                    client_index = (ssize_t)i;
                    break;
                }
            }

            if (client_index >= 0) {
                record_client_hook_failure(state, (nfds_t)client_index,
                    command->data.hook_failure.hook_type,
                    command->data.hook_failure.event_id,
                    command->data.hook_failure.nesting_depth,
                    command->data.hook_failure.elapsed_ms,
                    command->data.hook_failure.reason);
            }

            break;
        }
    }
}

static void process_daemon_commands(ksi_daemon_state *state)
{
    ksi_daemon_command command;

    if (state == NULL || state->commands == NULL) {
        return;
    }

    while (ksi_pipe_ring_pop(&state->commands->ring, &command)) {
        process_daemon_command(state, &command);
        free_daemon_command(&command);
    }
}

/* Returns true when the daemon should exit due to the idle timeout. */
