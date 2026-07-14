static void request_close_client(ksi_daemon_state *state, int fd)
{
    (void)state;
    ipc_close_locked(fd);
}

static void remove_client(ksi_daemon_state *state, nfds_t index)
{
    ksi_client *clients = state->clients;
    nfds_t * const count = &state->client_count;
    uint32_t subs = clients[index].hook_subscriptions;

    hook_send_ref_release(clients[index].hook_send_ref);
    clients[index].hook_send_ref = NULL;
    free(clients[index].rx_buffer);
    clients[index].rx_buffer = NULL;

    /* Flush the lanes this client was subscribed to, exactly as the failure and
     * lease-expiry paths do (grab_leases.inc). Otherwise an event already sent to
     * this now-departed subscriber and awaiting its decision keeps the lane parked
     * for the full decision timeout (~1s) before failing open — a clean disconnect
     * from inside a hotkey handler (e.g. ExitApp) would freeze real input meanwhile.
     * The flush bumps the lane generation so any in-flight event finalizes as PASS
     * immediately. Guarded on subs so query-only clients don't trigger a flush. */
    if ((subs & KSI_CAP_HOOK_KEYBOARD) != 0u) {
        lane_flush_passthrough(&state->keyboard_lane);
    }

    if ((subs & KSI_CAP_HOOK_MOUSE) != 0u) {
        lane_flush_passthrough(&state->mouse_lane);
    }

    /* Lane threads work from a snapshot of the subscriber list taken at
     * enqueue time, so removing the client from clients[] does not need to
     * disturb in-flight lane work.  If the lane is currently waiting on a
     * decision from this client, its send/recv will simply fail and the
     * lane will move on to the next snapshot subscriber or time out.
     *
     * "Allow once" session grants live on each client's granted_capabilities
     * field, so they die naturally with the client here. New connections from
     * the same script process inherit grants from sibling clients via
     * check_capabilities_sync; a fresh process has no siblings, so it gets a
     * new prompt. */
    for (nfds_t i = index; i + 1 < *count; i++) {
        clients[i] = clients[i + 1];
    }

    (*count)--;

    if (update_grab_state(state) != 0) {
        fprintf(stderr, "inputd: failed to update grab state after client removal\n");
    }

    /* If the last client just left, release any synthetic keys still held on the
     * shared uinput device. A synth-only client (no hook/block grab) that Sends a
     * {Key down} then crashes/exits would otherwise strand that key until some
     * future grab cycle runs RELEASE_ALL. Doing this only when no clients remain
     * keeps it safe: there is no surviving client whose intentionally-held key we
     * could clobber. (Grab-holding clients are already cleaned up via the grab
     * held->released edge in update_grab_state above.) */
    if (*count == 0) {
        (void)output_queue_push_release_all(&state->output_queue);
    }
}

