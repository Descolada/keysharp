static uint64_t monotonic_ms(void)
{
    struct timespec time_value;

    if (clock_gettime(CLOCK_MONOTONIC, &time_value) != 0) {
        return 0;
    }

    return ((uint64_t)time_value.tv_sec * 1000u) + ((uint64_t)time_value.tv_nsec / 1000000u);
}

/* ------------------------------------------------------------------------
 * Output sequencer
 *
 * All replay/synthesis writes to the platform backend (uinput) are funneled
 * through a single sequencer thread.  Hook-event finalize and SYNTHESIZE_INPUT
 * both enqueue actions here, and the sequencer drains them in arrival order.
 *
 * Decoupling output from the main loop means:
 *  - A SendInput burst from a script is queued as a unit regardless of which
 *    hook lane is currently waiting on a client decision.
 *  - Slow uinput writes do not back-pressure evdev ingress or IPC handling.
 *  - A stall in one hook lane cannot prevent the other lane's output from
 *    reaching uinput.
 *
 * Actions execute in the order they were pushed onto the sequencer queue, so
 * a SYNTH pushed by a script mid-flight no longer waits for an in-progress
 * hook decision to finalize.  This matches the Windows behavior where
 * SendInput does not block on pending low-level hooks.
 *
 * KNOWN BOUND (single strict FIFO, no replay prioritization): physical-event
 * REPLAY and held-key RELEASE_ALL share this one queue with client SYNTHESIZE_INPUT
 * and are dispatched in arrival order. A client that floods Send can therefore push
 * a large paced synth backlog (capped at KSI_MAX_CLIENT_OUTPUT_SYNTH_BYTES) ahead of
 * the user's next physical keystroke, delaying its replay to uinput by up to the
 * drain time of that backlog (worst case order ~tens of seconds for a maximal
 * unicode-heavy backlog at the 0.35ms/16-event pacing rate). This is a BOUNDED,
 * self-clearing latency, NOT a hang/deadlock: every emit is non-blocking (O_NONBLOCK
 * uinput, EINTR-only retry) and pacing is a single bounded clock_nanosleep, and the
 * EVIOCGRAB(off) that restores physical input on ungrab is synchronous and does not
 * wait on this queue. It is accepted as a known limit (only a heavy/adversarial Send
 * loop triggers it). If it ever needs eliminating, split this into a critical queue
 * (REPLAY/RELEASE_ALL/MODIFY+emergency synth) drained to empty before each single
 * client-synth action, with pacing only on the client-synth queue.
 * ----------------------------------------------------------------------- */

static int output_queue_init(ksi_output_queue *q)
{
    int pipe_fds[2];

    if (q == NULL) {
        return -1;
    }

    memset(q, 0, sizeof(*q));
    q->wake_read_fd = -1;
    q->wake_write_fd = -1;

    if (pthread_mutex_init(&q->mutex, NULL) != 0) {
        return -1;
    }

    q->mutex_initialized = true;

    if (pipe(pipe_fds) != 0) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
        return -1;
    }

    q->wake_read_fd = pipe_fds[0];
    q->wake_write_fd = pipe_fds[1];

    {
        int fl;
        fl = fcntl(q->wake_read_fd, F_GETFL);

        if (fl >= 0) {
            (void)fcntl(q->wake_read_fd, F_SETFL, fl | O_NONBLOCK);
        }

        fl = fcntl(q->wake_write_fd, F_GETFL);

        if (fl >= 0) {
            (void)fcntl(q->wake_write_fd, F_SETFL, fl | O_NONBLOCK);
        }
    }

    return 0;
}

static void output_queue_push_node_locked(ksi_output_queue *q, ksi_output_node *node)
{
    /* Caller holds q->mutex. */
    node->next = NULL;

    if (q->tail != NULL) {
        q->tail->next = node;
    } else {
        q->head = node;
    }

    q->tail = node;
    q->count++;

    if (node->action.type == KSI_OUTPUT_ACTION_SYNTH) {
        q->synth_bytes += (size_t)node->action.synth_count * sizeof(ksi_input);
    }
}

static bool output_queue_pop(ksi_output_queue *q, ksi_output_action *out)
{
    ksi_output_node *node = NULL;

    if (q == NULL || !q->mutex_initialized) {
        return false;
    }

    pthread_mutex_lock(&q->mutex);

    if (q->head != NULL) {
        node = q->head;
        q->head = node->next;

        if (q->head == NULL) {
            q->tail = NULL;
        }

        q->count--;

        if (node->action.type == KSI_OUTPUT_ACTION_SYNTH) {
            q->synth_bytes -= (size_t)node->action.synth_count * sizeof(ksi_input);
        }
    }

    pthread_mutex_unlock(&q->mutex);

    if (node == NULL) {
        return false;
    }

    *out = node->action;
    free(node);
    return true;
}

static void output_queue_drain_wake(const ksi_output_queue *q)
{
    uint8_t buffer[64];

    if (q == NULL || q->wake_read_fd < 0) {
        return;
    }

    while (read(q->wake_read_fd, buffer, sizeof(buffer)) > 0) {
    }
}

static void output_queue_wake(const ksi_output_queue *q)
{
    if (q == NULL || q->wake_write_fd < 0) {
        return;
    }

    wake_pipe_write(q->wake_write_fd);
}

static void output_queue_close(ksi_output_queue *q)
{
    ksi_output_node *node;

    if (q == NULL) {
        return;
    }

    /* Free remaining nodes — and their heap-owned synth_inputs payloads —
     * before tearing the wake pipe and mutex down. */
    node = q->head;
    q->head = NULL;
    q->tail = NULL;
    q->count = 0u;
    q->synth_bytes = 0u;

    while (node != NULL) {
        ksi_output_node *next = node->next;

        if (node->action.type == KSI_OUTPUT_ACTION_SYNTH) {
            free(node->action.synth_inputs);
        }

        free(node);
        node = next;
    }

    if (q->wake_read_fd >= 0) {
        close(q->wake_read_fd);
        q->wake_read_fd = -1;
    }

    if (q->wake_write_fd >= 0) {
        close(q->wake_write_fd);
        q->wake_write_fd = -1;
    }

    if (q->mutex_initialized) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
    }
}

static bool output_queue_push_replay(
    ksi_output_queue *q,
    uint32_t hook_type,
    const ksi_hook_event_payload *payload,
    size_t payload_size)
{
    ksi_output_node *node;

    if (q == NULL || payload == NULL || payload_size > sizeof(node->action.replay_payload)) {
        return false;
    }

    node = malloc(sizeof(*node));

    if (node == NULL) {
        return false;
    }

    memset(&node->action, 0, sizeof(node->action));
    node->action.type = KSI_OUTPUT_ACTION_REPLAY;
    node->action.hook_type = hook_type;
    node->action.replay_payload = *payload;
    node->action.replay_payload_size = payload_size;

    pthread_mutex_lock(&q->mutex);

    if (q->count >= KSI_MAX_NONCRITICAL_OUTPUT_ACTIONS) {
        pthread_mutex_unlock(&q->mutex);
        free(node);
        return false;
    }

    output_queue_push_node_locked(q, node);
    pthread_mutex_unlock(&q->mutex);

    output_queue_wake(q);
    return true;
}

static bool output_queue_push_synth(
    ksi_output_queue *q,
    const ksi_input *inputs,
    uint32_t count,
    uint32_t flags,
    bool critical)
{
    ksi_output_node *node;
    size_t synth_bytes;

    if (q == NULL || (count > 0u && inputs == NULL)) {
        return false;
    }

    synth_bytes = (size_t)count * sizeof(*inputs);
    node = malloc(sizeof(*node));

    if (node == NULL) {
        return false;
    }

    memset(&node->action, 0, sizeof(node->action));
    node->action.type = KSI_OUTPUT_ACTION_SYNTH;
    node->action.synth_flags = flags;
    node->action.synth_count = count;

    if (count > 0u) {
        node->action.synth_inputs = malloc((size_t)count * sizeof(*inputs));

        if (node->action.synth_inputs == NULL) {
            free(node);
            return false;
        }

        memcpy(node->action.synth_inputs, inputs, (size_t)count * sizeof(*inputs));
    }

    pthread_mutex_lock(&q->mutex);

    if (q->count >= (critical ? KSI_MAX_OUTPUT_ACTIONS : KSI_MAX_CLIENT_OUTPUT_ACTIONS)
        || synth_bytes > (critical ? KSI_MAX_OUTPUT_SYNTH_BYTES : KSI_MAX_CLIENT_OUTPUT_SYNTH_BYTES)
        || q->synth_bytes > (critical ? KSI_MAX_OUTPUT_SYNTH_BYTES : KSI_MAX_CLIENT_OUTPUT_SYNTH_BYTES)
            - synth_bytes) {
        pthread_mutex_unlock(&q->mutex);
        free(node->action.synth_inputs);
        free(node);
        return false;
    }

    output_queue_push_node_locked(q, node);

    /* Advance the enqueue-time logical key state while still holding the queue
     * lock, so these updates are ordered identically to the queue insertions
     * (a later batch can never be applied before an earlier one). This is what
     * lets GET_KEY_STATE report the post-drain state the instant the batch is
     * accepted, without waiting for the paced uinput drain. */
    ksi_linux_synth_note_enqueued_synth(inputs, count);

    pthread_mutex_unlock(&q->mutex);

    output_queue_wake(q);
    return true;
}

/* Enqueue a "release all replayed/synthesized keys" action. Runs on the sequencer
 * thread so the release is serialized with REPLAY/SYNTH writes (no race) and never
 * stalls the caller (the daemon main thread, via update_grab_state). Admitted up to
 * the critical cap: a dropped release would leave a key stranded "down". */
static bool output_queue_push_release_all(ksi_output_queue *q)
{
    ksi_output_node *node;

    if (q == NULL) {
        return false;
    }

    node = malloc(sizeof(*node));

    if (node == NULL) {
        return false;
    }

    memset(&node->action, 0, sizeof(node->action));
    node->action.type = KSI_OUTPUT_ACTION_RELEASE_ALL;

    pthread_mutex_lock(&q->mutex);

    if (q->count >= KSI_MAX_OUTPUT_ACTIONS) {
        pthread_mutex_unlock(&q->mutex);
        free(node);
        return false;
    }

    output_queue_push_node_locked(q, node);

    /* release_all will drop every synthetic key, so the enqueue-time logical
     * state becomes empty the moment this action is accepted. */
    ksi_linux_synth_reset_enqueued_synth();

    pthread_mutex_unlock(&q->mutex);

    output_queue_wake(q);
    return true;
}

/* Sequencer thread.  Drains the output queue in arrival order and dispatches
 * each action to the platform backend (uinput).  Runs until
 * state->sequencer_running drops to zero AND the queue is empty, so any
 * actions enqueued after shutdown signal still complete. */
static void *output_sequencer_thread_main(void *arg)
{
    ksi_daemon_state *state = arg;
    struct pollfd pfd;

    set_realtime_priority("output sequencer");

    if (state == NULL) {
        return NULL;
    }

    pfd.fd = state->output_queue.wake_read_fd;
    pfd.events = POLLIN;

    for (;;) {
        ksi_output_action action;
        bool keep;

        /* Drain wake bytes first, THEN pop.  Reversing this order races: a
         * producer could push (then write wake byte) between an empty pop
         * and a subsequent drain_wake, after which poll() would block even
         * though the queue has work. */
        output_queue_drain_wake(&state->output_queue);

        while (output_queue_pop(&state->output_queue, &action)) {
            if (action.type == KSI_OUTPUT_ACTION_REPLAY) {
                if (state->backend != NULL && state->backend->replay_hook_event != NULL) {
                    if (state->backend->replay_hook_event(action.hook_type, &action.replay_payload) != 0) {
                        fprintf(stderr, "sequencer: replay failed\n");
                    }
                }
            } else if (action.type == KSI_OUTPUT_ACTION_SYNTH) {
                if (state->backend != NULL && state->backend->send_input != NULL) {
                    if (state->backend->send_input(action.synth_inputs, action.synth_count, action.synth_flags) != 0) {
                        fprintf(stderr, "sequencer: synth failed\n");
                    }
                }

                free(action.synth_inputs);
            } else if (action.type == KSI_OUTPUT_ACTION_RELEASE_ALL) {
                if (state->backend != NULL && state->backend->release_synthetic_keys != NULL) {
                    state->backend->release_synthetic_keys();
                }
            }
        }

        keep = atomic_load(&state->sequencer_running) != 0;

        if (!keep) {
            break;
        }

        /* 100ms cap so the shutdown signal is observed promptly even if a
         * wake byte was lost (e.g. write coalesced into an already-full pipe). */
        (void)poll(&pfd, 1, 100);
    }

    return NULL;
}

/* ------------------------------------------------------------------------
 * Hook lane queues
 *
 * Action queue: main thread pushes ksi_lane_event* onto a capped linked list.
 * Decision queue: main thread pushes ksi_lane_decision* for the single
 * in-flight event being delivered to a hook subscriber.
 * ----------------------------------------------------------------------- */

static int lane_action_queue_init(ksi_lane_action_queue *q)
{
    int pipe_fds[2];

    if (q == NULL) {
        return -1;
    }

    memset(q, 0, sizeof(*q));
    q->wake_read_fd = -1;
    q->wake_write_fd = -1;

    if (pthread_mutex_init(&q->mutex, NULL) != 0) {
        return -1;
    }

    q->mutex_initialized = true;

    if (pipe(pipe_fds) != 0) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
        return -1;
    }

    q->wake_read_fd = pipe_fds[0];
    q->wake_write_fd = pipe_fds[1];

    {
        int fl;
        fl = fcntl(q->wake_read_fd, F_GETFL);
        if (fl >= 0) (void)fcntl(q->wake_read_fd, F_SETFL, fl | O_NONBLOCK);
        fl = fcntl(q->wake_write_fd, F_GETFL);
        if (fl >= 0) (void)fcntl(q->wake_write_fd, F_SETFL, fl | O_NONBLOCK);
    }

    return 0;
}

static bool lane_action_queue_push(ksi_lane_action_queue *q, ksi_lane_event *event)
{
    ksi_lane_action_node *node;

    if (q == NULL || event == NULL || !q->mutex_initialized) {
        return false;
    }

    node = malloc(sizeof(*node));

    if (node == NULL) {
        return false;
    }

    node->event = event;
    node->next = NULL;

    pthread_mutex_lock(&q->mutex);

    if (q->count >= KSI_MAX_LANE_ACTIONS) {
        pthread_mutex_unlock(&q->mutex);
        free(node);
        return false;
    }

    if (q->tail != NULL) {
        q->tail->next = node;
    } else {
        q->head = node;
    }

    q->tail = node;
    q->count++;
    pthread_mutex_unlock(&q->mutex);

    if (q->wake_write_fd >= 0) {
        wake_pipe_write(q->wake_write_fd);
    }

    return true;
}

static bool lane_action_queue_pop(ksi_lane_action_queue *q, ksi_lane_event **out)
{
    ksi_lane_action_node *node;

    if (q == NULL || out == NULL || !q->mutex_initialized) {
        return false;
    }

    pthread_mutex_lock(&q->mutex);

    node = q->head;

    if (node != NULL) {
        q->head = node->next;

        if (q->head == NULL) {
            q->tail = NULL;
        }

        if (q->count > 0u) {
            q->count--;
        }
    }

    pthread_mutex_unlock(&q->mutex);

    if (node == NULL) {
        return false;
    }

    *out = node->event;
    free(node);
    return true;
}

static void lane_action_queue_wake(const ksi_lane_action_queue *q)
{
    if (q == NULL || q->wake_write_fd < 0) {
        return;
    }

    wake_pipe_write(q->wake_write_fd);
}

static void lane_action_queue_drain_wake(const ksi_lane_action_queue *q)
{
    uint8_t buffer[64];

    if (q == NULL || q->wake_read_fd < 0) {
        return;
    }

    while (read(q->wake_read_fd, buffer, sizeof(buffer)) > 0) {
    }
}

static void lane_event_release_send_refs(ksi_lane_event *event)
{
    if (event == NULL) {
        return;
    }

    for (size_t i = 0; i < event->subscriber_count; i++) {
        if (event->subscriber_send_refs[i] != NULL) {
            hook_send_ref_release(event->subscriber_send_refs[i]);
            event->subscriber_send_refs[i] = NULL;
        }
    }
}

static void lane_action_queue_close(ksi_lane_action_queue *q)
{
    ksi_lane_action_node *node;

    if (q == NULL) {
        return;
    }

    node = q->head;
    q->head = NULL;
    q->tail = NULL;
    q->count = 0u;

    while (node != NULL) {
        ksi_lane_action_node *next = node->next;
        lane_event_release_send_refs(node->event);
        free(node->event);
        free(node);
        node = next;
    }

    if (q->wake_read_fd >= 0) {
        close(q->wake_read_fd);
        q->wake_read_fd = -1;
    }

    if (q->wake_write_fd >= 0) {
        close(q->wake_write_fd);
        q->wake_write_fd = -1;
    }

    if (q->mutex_initialized) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
    }
}

static int lane_decision_queue_init(ksi_lane_decision_queue *q)
{
    pthread_condattr_t cond_attr;
    int rc;

    if (q == NULL) {
        return -1;
    }

    memset(q, 0, sizeof(*q));

    if (pthread_mutex_init(&q->mutex, NULL) != 0) {
        return -1;
    }

    q->mutex_initialized = true;

    /* Wait on CLOCK_MONOTONIC, not the default CLOCK_REALTIME.  The decision
     * wait deadline is monotonic, and a backward wall-clock step (NTP, or a VM
     * host-clock sync) must never extend a pending hook decision past its 1s
     * bound while physical devices are grabbed — that would present as an input
     * freeze. */
    if (pthread_condattr_init(&cond_attr) != 0) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
        return -1;
    }

    (void)pthread_condattr_setclock(&cond_attr, CLOCK_MONOTONIC);
    rc = pthread_cond_init(&q->condition, &cond_attr);
    pthread_condattr_destroy(&cond_attr);

    if (rc != 0) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
        return -1;
    }

    q->condition_initialized = true;
    return 0;
}

static void lane_decision_queue_wake(ksi_lane_decision_queue *q)
{
    if (q == NULL || !q->mutex_initialized || !q->condition_initialized) {
        return;
    }

    pthread_mutex_lock(&q->mutex);
    pthread_cond_broadcast(&q->condition);
    pthread_mutex_unlock(&q->mutex);
}

static void lane_decision_queue_wait(ksi_lane_decision_queue *q, int timeout_ms)
{
    struct timespec absolute;

    if (q == NULL || !q->mutex_initialized || !q->condition_initialized
        || clock_gettime(CLOCK_MONOTONIC, &absolute) != 0) {
        return;
    }

    absolute.tv_sec += timeout_ms / 1000;
    absolute.tv_nsec += (long)(timeout_ms % 1000) * 1000000L;

    if (absolute.tv_nsec >= 1000000000L) {
        absolute.tv_sec++;
        absolute.tv_nsec -= 1000000000L;
    }

    pthread_mutex_lock(&q->mutex);

    if (!q->has_decision) {
        (void)pthread_cond_timedwait(&q->condition, &q->mutex, &absolute);
    }

    pthread_mutex_unlock(&q->mutex);
}

/* Stores the decision and writes the HOOK_DECISION success ack before waking
 * the lane, preserving the "ack precedes next event" stream invariant. */
static bool lane_decision_queue_push_with_ack(
    ksi_lane_decision_queue *q,
    const ksi_lane_decision *decision,
    int client_fd,
    const ksi_message_header *request,
    uint32_t detail)
{
    ksi_status_payload ack_payload;

    if (q == NULL || decision == NULL || request == NULL || !q->mutex_initialized) {
        return false;
    }

    ack_payload.status = 0;
    ack_payload.detail = detail;

    pthread_mutex_lock(&q->mutex);

    if (q->has_decision) {
        pthread_mutex_unlock(&q->mutex);
        return false;
    }

    q->decision = *decision;
    q->has_decision = true;
    pthread_mutex_unlock(&q->mutex);

    (void)ipc_send_locked(
        client_fd, KSI_MESSAGE_HOOK_DECISION,
        request->client_id, request->correlation_id,
        &ack_payload, sizeof(ack_payload));

    lane_decision_queue_wake(q);
    return true;
}

static bool lane_decision_queue_pop(ksi_lane_decision_queue *q, ksi_lane_decision *out)
{
    if (q == NULL || out == NULL || !q->mutex_initialized) {
        return false;
    }

    pthread_mutex_lock(&q->mutex);

    if (!q->has_decision) {
        pthread_mutex_unlock(&q->mutex);
        return false;
    }

    *out = q->decision;
    memset(&q->decision, 0, sizeof(q->decision));
    q->has_decision = false;
    pthread_mutex_unlock(&q->mutex);
    return true;
}

static void lane_decision_queue_close(ksi_lane_decision_queue *q)
{
    if (q == NULL) {
        return;
    }

    q->has_decision = false;
    memset(&q->decision, 0, sizeof(q->decision));

    if (q->condition_initialized) {
        pthread_cond_destroy(&q->condition);
        q->condition_initialized = false;
    }

    if (q->mutex_initialized) {
        pthread_mutex_destroy(&q->mutex);
        q->mutex_initialized = false;
    }
}

/* Posts a hook delivery failure back to the main thread so it can update the
 * client's consecutive_hook_failures and potentially evict its subscriptions. */
static void lane_post_hook_failure(
    ksi_daemon_state *state,
    int client_fd,
    uint64_t connection_id,
    uint32_t hook_type,
    const char *reason)
{
    ksi_daemon_command cmd;

    if (state == NULL || state->commands == NULL) {
        return;
    }

    memset(&cmd, 0, sizeof(cmd));
    cmd.type = KSI_DAEMON_COMMAND_LANE_HOOK_FAILURE;
    cmd.client_fd = client_fd;
    cmd.connection_id = connection_id;
    cmd.data.hook_failure.hook_type = hook_type;
    (void)snprintf(cmd.data.hook_failure.reason, sizeof(cmd.data.hook_failure.reason),
        "%s", reason != NULL ? reason : "unknown");

    if (!command_queue_push(state->commands, &cmd)) {
        atomic_store(&g_fail_open_requested, 1);
        command_queue_wake(state->commands);
    }
}

/* Called only from the lane's thread (lane_thread_main). Reads decisions and
 * pushes output actions through queues that are themselves thread-safe; does
 * not touch state->clients[] (lanes work from the snapshot in `event`). */
static void lane_process_event(ksi_hook_lane *lane, ksi_lane_event *event)
{
    uint32_t final_decision = KSI_HOOK_DECISION_PASS;
    ksi_input modify_inputs[KSI_MAX_MODIFY_INPUTS];
    uint32_t modify_count = 0u;
    bool finalized = false;

    atomic_store(&lane->current_event_id, event->event_id);

    /* During shutdown skip the per-subscriber decision dance; physical events
     * still need to reach uinput so they replay as PASS. */
    if (atomic_load(&lane->shutting_down)
        || event->generation != atomic_load(&lane->flush_generation)) {
        goto finalize;
    }

    for (size_t i = 0; i < event->subscriber_count && !finalized; i++) {
        int response_fd = event->subscriber_response_fds[i];
        ksi_hook_send_ref *send_ref = event->subscriber_send_refs[i];
        int send_fd = hook_send_ref_fd(send_ref);
        uint64_t conn_id = event->subscriber_connection_ids[i];
        bool got_decision = false;
        uint64_t deadline;
        ksi_lane_decision decision;

        memset(&decision, 0, sizeof(decision));

        if (send_fd < 0) {
            lane_post_hook_failure(lane->state, response_fd, conn_id, event->hook_type, "send");
            continue;
        }

        atomic_store(&lane->current_responder_fd, response_fd);
        atomic_store(&lane->current_responder_connection_id, conn_id);

        if (ipc_send_locked(send_fd, KSI_MESSAGE_HOOK_EVENT, 0,
                event->event_id, &event->payload, event->payload_size) != 0) {
            atomic_store(&lane->current_responder_fd, -1);
            atomic_store(&lane->current_responder_connection_id, 0u);
            hook_send_ref_release(send_ref);
            event->subscriber_send_refs[i] = NULL;
            lane_post_hook_failure(lane->state, response_fd, conn_id, event->hook_type, "send");
            continue;
        }

        deadline = monotonic_ms() + KSI_HOOK_DECISION_TIMEOUT_MS;

        while (!got_decision) {
            uint64_t now = monotonic_ms();
            int timeout_ms;

            if (now >= deadline
                || atomic_load(&lane->shutting_down)
                || event->generation != atomic_load(&lane->flush_generation)) {
                break;
            }

            timeout_ms = (int)(deadline - now);

            if (timeout_ms > 100) {
                timeout_ms = 100;
            }

            lane_decision_queue_wait(&lane->decision_queue, timeout_ms);

            for (;;) {
                ksi_lane_decision candidate;

                if (!lane_decision_queue_pop(&lane->decision_queue, &candidate)) {
                    break;
                }

                if (candidate.event_id == event->event_id
                    && candidate.responder_fd == response_fd
                    && candidate.responder_connection_id == conn_id) {
                    decision = candidate;
                    got_decision = true;
                    break;
                }
            }
        }

        atomic_store(&lane->current_responder_fd, -1);
        atomic_store(&lane->current_responder_connection_id, 0u);

        if (!got_decision) {
            hook_send_ref_release(send_ref);
            event->subscriber_send_refs[i] = NULL;

            if (atomic_load(&lane->shutting_down)
                || event->generation != atomic_load(&lane->flush_generation)) {
                break;
            }

            lane_post_hook_failure(lane->state, response_fd, conn_id, event->hook_type, "timeout");
            continue;
        }

        hook_send_ref_release(send_ref);
        event->subscriber_send_refs[i] = NULL;

        if (decision.decision == KSI_HOOK_DECISION_BLOCK) {
            final_decision = KSI_HOOK_DECISION_BLOCK;
            finalized = true;
        } else if (decision.decision == KSI_HOOK_DECISION_MODIFY) {
            final_decision = KSI_HOOK_DECISION_MODIFY;
            modify_count = decision.input_count;

            if (modify_count > KSI_MAX_MODIFY_INPUTS) {
                modify_count = KSI_MAX_MODIFY_INPUTS;
            }

            if (modify_count > 0u) {
                memcpy(modify_inputs, decision.inputs,
                    (size_t)modify_count * sizeof(modify_inputs[0]));
            }

            finalized = true;
        }
        /* else PASS — fall through to next subscriber. */

    }

finalize:
    lane_event_release_send_refs(event);

    if (final_decision == KSI_HOOK_DECISION_PASS && !event->is_injected) {
        if (!output_queue_push_replay(&lane->state->output_queue,
                event->hook_type, &event->payload, event->payload_size)) {
            fprintf(stderr, "lane: replay enqueue failed for event %llu\n",
                (unsigned long long)event->event_id);
            atomic_store(&g_fail_open_requested, 1);
            command_queue_wake(lane->state->commands);
        }
    } else if (final_decision == KSI_HOOK_DECISION_MODIFY && modify_count > 0u) {
        if (!output_queue_push_synth(&lane->state->output_queue,
                modify_inputs, modify_count, KSI_SYNTH_FLAG_BYPASS_HOOK, false)) {
            fprintf(stderr, "lane: modify synth enqueue failed for event %llu\n",
                (unsigned long long)event->event_id);
            (void)output_queue_push_replay(&lane->state->output_queue,
                event->hook_type, &event->payload, event->payload_size);
            atomic_store(&g_fail_open_requested, 1);
            command_queue_wake(lane->state->commands);
        }
    }

    atomic_store(&lane->current_event_id, (uint_least64_t)0);
    atomic_store(&lane->current_responder_fd, -1);
    atomic_store(&lane->current_responder_connection_id, 0u);
}

/* Elevate the calling thread to SCHED_FIFO priority 1 so that hook lane and
 * sequencer threads are not preempted by normal (SCHED_OTHER) user processes.
 * Priority 1 is the lowest real-time priority — it yields to any higher-RT
 * thread (audio, GPU drivers) but preempts all SCHED_OTHER threads.  This
 * keeps hook decision round-trips and uinput writes within a bounded latency
 * even when the system is under CPU load.
 * Failure is non-fatal; the daemon continues with default scheduling. */
static void set_realtime_priority(const char *thread_name)
{
    struct sched_param sp;
    memset(&sp, 0, sizeof(sp));
    sp.sched_priority = 1;

    if (pthread_setschedparam(pthread_self(), SCHED_FIFO, &sp) != 0) {
        fprintf(stderr,
            "inputd: note: could not set SCHED_FIFO for %s: %s\n",
            thread_name, strerror(errno));
    }
}

static void *lane_thread_main(void *arg)
{
    ksi_hook_lane *lane = arg;
    struct pollfd pfd;

    set_realtime_priority("hook lane");

    if (lane == NULL) {
        return NULL;
    }

    pfd.fd = lane->action_queue.wake_read_fd;
    pfd.events = POLLIN;

    for (;;) {
        ksi_lane_event *event = NULL;

        if (!lane_action_queue_pop(&lane->action_queue, &event)) {
            if (atomic_load(&lane->shutting_down)) {
                ksi_lane_decision decision;

                while (lane_decision_queue_pop(&lane->decision_queue, &decision)) {
                }

                return NULL;
            }

            (void)poll(&pfd, 1, 1000);
            lane_action_queue_drain_wake(&lane->action_queue);
            continue;
        }

        lane_process_event(lane, event);
        free(event);
    }
}

static ksi_hook_lane *lane_for_hook_type(ksi_daemon_state *state, uint32_t hook_type)
{
    if (state == NULL) {
        return NULL;
    }

    if (hook_type == KSI_HOOK_KEYBOARD_LL) {
        return &state->kbd_lane;
    }

    if (hook_type == KSI_HOOK_MOUSE_LL) {
        return &state->mouse_lane;
    }

    return NULL;
}

static ksi_hook_lane *lane_for_event_id(ksi_daemon_state *state, uint64_t event_id)
{
    if (state == NULL || event_id == 0u) {
        return NULL;
    }

    if (atomic_load(&state->kbd_lane.current_event_id) == event_id) {
        return &state->kbd_lane;
    }

    if (atomic_load(&state->mouse_lane.current_event_id) == event_id) {
        return &state->mouse_lane;
    }

    return NULL;
}

static int lane_init(ksi_hook_lane *lane, ksi_daemon_state *state, uint32_t hook_type)
{
    if (lane == NULL) {
        return -1;
    }

    memset(lane, 0, sizeof(*lane));
    lane->hook_type = hook_type;
    lane->state = state;

    if (lane_action_queue_init(&lane->action_queue) != 0) {
        return -1;
    }

    if (lane_decision_queue_init(&lane->decision_queue) != 0) {
        lane_action_queue_close(&lane->action_queue);
        return -1;
    }

    atomic_store(&lane->current_event_id, (uint_least64_t)0);
    atomic_store(&lane->current_responder_fd, -1);
    atomic_store(&lane->current_responder_connection_id, 0u);
    return 0;
}

static int lane_start(ksi_hook_lane *lane)
{
    if (lane == NULL) {
        return -1;
    }

    if (pthread_create(&lane->thread, NULL, lane_thread_main, lane) != 0) {
        return -1;
    }

    lane->thread_started = true;
    return 0;
}

static bool join_thread_before(pthread_t thread, uint64_t deadline_ms)
{
    struct timespec absolute;
    uint64_t now = monotonic_ms();
    uint64_t remaining_ms;

    if (deadline_ms == 0u) {
        return pthread_join(thread, NULL) == 0;
    }

    /* pthread_timedjoin_np only honors CLOCK_REALTIME, so a backward wall-clock
     * step could extend this wait.  That is acceptable here: this is a
     * shutdown-only deadline reached after grabs are already released, so the
     * worst case is a slower shutdown, never a held-grab input freeze.  (The
     * hook-decision wait, which CAN hold grabs, uses CLOCK_MONOTONIC instead.) */
    if (now >= deadline_ms || clock_gettime(CLOCK_REALTIME, &absolute) != 0) {
        return false;
    }

    remaining_ms = deadline_ms - now;
    absolute.tv_sec += (time_t)(remaining_ms / 1000u);
    absolute.tv_nsec += (long)((remaining_ms % 1000u) * 1000000u);

    if (absolute.tv_nsec >= 1000000000L) {
        absolute.tv_sec++;
        absolute.tv_nsec -= 1000000000L;
    }

    return pthread_timedjoin_np(thread, NULL, &absolute) == 0;
}

static bool lane_shutdown(ksi_hook_lane *lane, uint64_t deadline_ms)
{
    if (lane == NULL) {
        return true;
    }

    if (lane->thread_started) {
        /* Set the shutdown flag first so any in-flight event in lane_process_
         * event breaks out of its decision wait promptly, then wake the lane
         * so it can drain any queued events and exit once the queue is empty. */
        atomic_store(&lane->shutting_down, 1);
        lane_action_queue_wake(&lane->action_queue);

        if (!join_thread_before(lane->thread, deadline_ms)) {
            return false;
        }

        lane->thread_started = false;
    }

    lane_action_queue_close(&lane->action_queue);
    lane_decision_queue_close(&lane->decision_queue);
    return true;
}

static void lane_flush_passthrough(ksi_hook_lane *lane)
{
    if (lane == NULL) {
        return;
    }

    atomic_fetch_add(&lane->flush_generation, 1u);
    lane_decision_queue_wake(&lane->decision_queue);
    lane_action_queue_wake(&lane->action_queue);
}

/* ------------------------------------------------------------------------ */

