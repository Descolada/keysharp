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

	if (message->payload_size >= sizeof(*payload)) {
		payload = (const ksi_client_hello_payload *)(const void *)message->payload;
		requested = payload->requested_capabilities;
		flags = payload->flags;
	}

	force_prompt = (flags & KSI_CLIENT_HELLO_FLAG_FORCE_PROMPT) != 0u;

    /* Defer process hashing until the client actually asks for a privileged
     * capability. Query-only and short-lived connections need no identity
     * worker job. */
    if (requested != 0u
        && client->state == KSI_CLIENT_STATE_READY
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
            send_hello_status(client->fd,
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
        send_hello_status(client->fd, message->header->client_id, message->header->correlation_id, -1, 0);
        return;
    }

    /* Identity is known. Check what is already granted without prompting. */
	granted = check_capabilities_sync(state, client, requested, force_prompt, &missing);

    /* If there are missing capabilities and a permissions store is configured,
     * start an async prompt. Response will be sent from process_client_prompt_done. */
    if (missing != 0u && state->permissions != NULL) {
        if (!start_prompt_task(state->commands, client->fd, client->connection_id, requested, missing,
                               client->exe_path, client->command_line, client->exe_hash,
                               client->pid, client->uid, client->gid)) {
            send_hello_status(client->fd, message->header->client_id, message->header->correlation_id, -1, granted);
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
        client->authenticated = false;
        client->granted_capabilities = granted;
        send_hello_status(client->fd, message->header->client_id, message->header->correlation_id, -1, granted);
        return;
    }

	if (granted != 0u && client->has_identity) {
		ksi_permissions_note_seen(state->permissions, client->uid, client->exe_hash, client->exe_path);
	}

    client->authenticated = true;
    client->granted_capabilities |= granted;
    send_hello_status(client->fd, message->header->client_id, message->header->correlation_id, 0, granted);
}

static void handle_emergency_passthrough(
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    /* This is a rescue path for hook owners. Do not let an arbitrary
     * zero-capability hello on the public system socket clear other scripts. */
    if (!client->authenticated
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
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    if (!client->authenticated
        || (client->granted_capabilities
            & (KSI_CAP_HOOK_KEYBOARD | KSI_CAP_HOOK_MOUSE)) == 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_INDICATOR_STATE_RESULT, -1, 403);
        return;
    }

    send_indicator_state_result(client->fd, message->header);
}

static void handle_get_pointer_position(
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    if (!client->authenticated
        || (client->granted_capabilities & KSI_CAP_HOOK_MOUSE) == 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_POINTER_POSITION_RESULT, -1, 403);
        return;
    }

    send_pointer_position_result(client->fd, message->header);
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

    if (payload->decision != KSI_HOOK_DECISION_PASS
        && payload->decision != KSI_HOOK_DECISION_BLOCK
        && payload->decision != KSI_HOOK_DECISION_MODIFY) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 4);
        return;
    }

    if (payload->decision == KSI_HOOK_DECISION_MODIFY && payload->input_count == 0) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 7);
        return;
    }

    if (state == NULL) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 2);
        return;
    }

    if (payload->decision == KSI_HOOK_DECISION_MODIFY) {
        if (!client_has_capability(
                client,
                required_synthesis_capabilities(payload->inputs, payload->input_count))) {
            send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 403);
            return;
        }
    }

    /* Locate the lane currently waiting on this event_id.  If neither lane
     * has it as their current event, the decision is stale (timeout already
     * fired or the lane already finalized via BLOCK/MODIFY from someone else). */
    lane = lane_for_event_id(state, payload->event_id);

    if (lane == NULL) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 2);
        return;
    }

    if (atomic_load(&lane->current_responder_fd) != client->fd
        || atomic_load(&lane->current_responder_connection_id) != client->connection_id) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 2);
        return;
    }

    memset(&decision, 0, sizeof(decision));
    decision.event_id = payload->event_id;
    decision.decision = payload->decision;
    decision.responder_fd = client->fd;
    decision.responder_connection_id = client->connection_id;
    decision.input_count = payload->input_count;

    if (payload->input_count > 0u) {
        memcpy(decision.inputs, payload->inputs,
            (size_t)payload->input_count * sizeof(payload->inputs[0]));
    }

    if (!lane_decision_queue_push_with_ack(
            &lane->decision_queue,
            &decision,
            client->fd,
            message->header,
            payload->decision)) {
        send_status(client->fd, message->header, KSI_MESSAGE_HOOK_DECISION, -1, 12);
        return;
    }

    {
        /* Resolve the responder under the connection_id guard so we don't
         * credit success on a freshly-accepted client that happens to have
         * inherited this fd from a prior disconnect. */
        ssize_t resolved = find_client_index_by_fd(state, client->fd);

        if (resolved >= 0
            && state->clients[resolved].connection_id == client->connection_id) {
            record_client_hook_success(state, (nfds_t)resolved);
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

    if (message->payload_size < sizeof(*payload)) {
        send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1, 1);
        return;
    }

    payload = (const ksi_synthesize_input_payload *)(const void *)message->payload;

    if (payload->count > KSI_MAX_SYNTH_INPUTS) {
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

    if (!output_queue_push_synth(&state->output_queue,
            payload->inputs, payload->count, payload->flags, false)) {
        send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, -1, 12);
        return;
    }

    send_status(client->fd, message->header, KSI_MESSAGE_SYNTHESIS_RESULT, 0, 0);
}

static void handle_binary_message(
    const ksi_platform_backend *backend,
    ksi_daemon_state *state,
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    (void)backend;

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

            /* Protocol 0.2 permits one-way lease heartbeats on hook-reader
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
            handle_get_indicator_state(client, message);
            return;
        case KSI_MESSAGE_GET_POINTER_POSITION:
            handle_get_pointer_position(client, message);
            return;
        case KSI_MESSAGE_GET_KEY_STATE:
            handle_get_key_state(client, message);
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

        if (header.major != KSI_PROTOCOL_MAJOR || header.minor > KSI_PROTOCOL_MINOR) {
            fprintf(stderr,
                "client %d sent unsupported protocol version %u.%u\n",
                client->fd,
                header.major,
                header.minor);
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
    for (;;) {
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
    ksi_client *client,
    const ksi_binary_message_view *message)
{
    ksi_key_state_payload result;
    bool caps = false, num = false, scroll = false;

    if (!client->authenticated
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
    result.modifiers_lr = ksi_linux_devices_get_modifier_state();
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
        request_close_client(state, client_fd);
        return;
    }

    client = &state->clients[state->client_count];
    memset(client, 0, sizeof(*client));
    client->fd = client_fd;
    client->rx_buffer = malloc(KSI_MAX_MESSAGE_SIZE);

    if (client->rx_buffer == NULL) {
        request_close_client(state, client_fd);
        return;
    }

    client->hook_send_ref = hook_send_ref_create(client_fd);

    if (client->hook_send_ref == NULL) {
        fprintf(stderr, "inputd: failed to create hook send handle for client fd=%d: %s\n",
            client_fd, strerror(errno));
        free(client->rx_buffer);
        client->rx_buffer = NULL;
        request_close_client(state, client_fd);
        return;
    }

    client->connection_id = state->next_connection_id++;

    if (client->connection_id == 0u) {
        client->connection_id = state->next_connection_id++;
    }

    client->pid = credentials->pid;
    client->uid = credentials->uid;
    client->gid = credentials->gid;
    client->state = KSI_CLIENT_STATE_READY;
    state->client_count++;

    if (g_verbose) {
        printf("accepted client fd=%d pid=%ld uid=%ld gid=%ld\n",
            client->fd,
            (long)client->pid,
            (long)client->uid,
            (long)client->gid);
    }
}

static bool accept_client(ksi_daemon_state *state, ksi_ipc_server *server, bool system_service)
{
    ksi_ipc_peer_credentials credentials;
    int client_fd = ksi_ipc_accept_client(server);

    if (client_fd < 0) {
        return false;
    }

    if (state->client_count >= KSI_MAX_CLIENTS
        || ksi_ipc_get_peer_credentials(client_fd, &credentials) != 0) {
        ipc_close_locked(client_fd);
        return true;
    }

    if (!system_service && credentials.uid != getuid()) {
        fprintf(stderr,
            "rejected client pid=%ld uid=%ld gid=%ld: daemon uid is %ld\n",
            (long)credentials.pid,
            (long)credentials.uid,
            (long)credentials.gid,
            (long)getuid());
        ipc_close_locked(client_fd);
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
            ssize_t client_index = find_client_index_by_fd(state, command->client_fd);

            /* connection_id check guards against blaming a reused fd: if the
             * fd was recycled for a new client after the lane posted the
             * failure, skip the failure-count bump for that new client. */
            if (client_index >= 0
                && state->clients[client_index].connection_id == command->connection_id) {
                record_client_hook_failure(state, (nfds_t)client_index,
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

    while (command_queue_pop(state->commands, &command)) {
        process_daemon_command(state, &command);
        free_daemon_command(&command);
    }
}

/* Returns true when the daemon should exit due to the idle timeout. */
