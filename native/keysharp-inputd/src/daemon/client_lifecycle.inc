static void request_close_client(ksi_daemon_state *state, int fd)
{
    (void)state;
    ipc_close_locked(fd);
}

static void remove_client(ksi_daemon_state *state, nfds_t index)
{
    ksi_client *clients = state->clients;
    nfds_t * const count = &state->client_count;

    hook_send_ref_release(clients[index].hook_send_ref);
    clients[index].hook_send_ref = NULL;
    free(clients[index].rx_buffer);
    clients[index].rx_buffer = NULL;

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
}

