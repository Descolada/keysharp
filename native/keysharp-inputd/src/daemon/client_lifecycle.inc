static void remove_client(ksi_daemon_state *state, nfds_t index)
{
    ksi_client *clients = state->clients;
    nfds_t * const count = &state->client_count;
    hook_send_ref_invalidate(clients[index].hook_send_ref);
    hook_send_ref_release(clients[index].hook_send_ref);
    clients[index].hook_send_ref = NULL;
    free(clients[index].rx_buffer);
    clients[index].rx_buffer = NULL;

    /* Snapshot refs remain alive but invalid. Their lane waiters wake immediately,
     * so one disconnect never flushes unrelated scripts or hook types.
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
