#include "connection_ref.h"

#include "keysharp_inputd/ipc.h"

#include <pthread.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

/* One connection can subscribe to both hook lanes. Each lane is capped at 512
 * queued events, so 1024 covers the maximum pending order window for that peer. */
#define KSI_HOOK_SEND_REF_MAX_PENDING 1024u

struct ksi_hook_send_ref {
    int fd;
    atomic_uint ref_count;
    /* Per-hook-type quarantine marker. Queued snapshots consult it and skip a
     * timed-out subscriber until authenticated REARM_HOOK clears that lane. */
    atomic_uint stalled_lanes;
    pthread_mutex_t send_mutex;
    pthread_mutex_t order_mutex;
    pthread_cond_t order_cond;
    uint64_t pending_event_ids[KSI_HOOK_SEND_REF_MAX_PENDING];
    size_t pending_event_head;
    size_t pending_event_count;
    struct ksi_hook_send_ref *next;
};

static pthread_mutex_t registry_mutex = PTHREAD_MUTEX_INITIALIZER;
static ksi_hook_send_ref *registry;

ksi_hook_send_ref *hook_send_ref_create(int client_fd)
{
    ksi_hook_send_ref *ref = malloc(sizeof(*ref));
    pthread_condattr_t cond_attr;
    int cond_rc;

    if (ref == NULL) {
        return NULL;
    }

    memset(ref, 0, sizeof(*ref));
    ref->fd = client_fd;
    atomic_init(&ref->ref_count, 1u);

    if (pthread_mutex_init(&ref->send_mutex, NULL) != 0) {
        free(ref);
        return NULL;
    }

    if (pthread_mutex_init(&ref->order_mutex, NULL) != 0) {
        pthread_mutex_destroy(&ref->send_mutex);
        free(ref);
        return NULL;
    }

    if (pthread_condattr_init(&cond_attr) != 0) {
        pthread_mutex_destroy(&ref->order_mutex);
        pthread_mutex_destroy(&ref->send_mutex);
        free(ref);
        return NULL;
    }

    if (pthread_condattr_setclock(&cond_attr, CLOCK_MONOTONIC) != 0) {
        pthread_condattr_destroy(&cond_attr);
        pthread_mutex_destroy(&ref->order_mutex);
        pthread_mutex_destroy(&ref->send_mutex);
        free(ref);
        return NULL;
    }

    cond_rc = pthread_cond_init(&ref->order_cond, &cond_attr);
    pthread_condattr_destroy(&cond_attr);

    if (cond_rc != 0) {
        pthread_mutex_destroy(&ref->order_mutex);
        pthread_mutex_destroy(&ref->send_mutex);
        free(ref);
        return NULL;
    }

    pthread_mutex_lock(&registry_mutex);
    ref->next = registry;
    registry = ref;
    pthread_mutex_unlock(&registry_mutex);
    return ref;
}

bool hook_send_ref_acquire(ksi_hook_send_ref *ref)
{
    if (ref == NULL) {
        return false;
    }

    (void)atomic_fetch_add(&ref->ref_count, 1u);
    return true;
}

int hook_send_ref_fd(const ksi_hook_send_ref *ref)
{
    return ref == NULL ? -1 : ref->fd;
}

bool hook_send_ref_is_stalled(const ksi_hook_send_ref *ref, size_t lane_index)
{
    return ref != NULL && lane_index < 2u
        && (atomic_load(&ref->stalled_lanes) & (1u << lane_index)) != 0u;
}
void hook_send_ref_mark_stalled(ksi_hook_send_ref *ref, size_t lane_index)
{
    if (ref != NULL && lane_index < 2u) {
        (void)atomic_fetch_or(&ref->stalled_lanes, 1u << lane_index);
    }
}

void hook_send_ref_clear_stalled(ksi_hook_send_ref *ref, size_t lane_index)
{
    if (ref != NULL && lane_index < 2u) {
        (void)atomic_fetch_and(&ref->stalled_lanes, ~(1u << lane_index));
    }
}

int hook_send_ref_send(
    ksi_hook_send_ref *ref,
    uint32_t message_type,
    uint32_t client_id,
    uint64_t correlation_id,
    const void *payload,
    size_t payload_size)
{
    int result;

    if (ref == NULL) {
        return -1;
    }

    pthread_mutex_lock(&ref->send_mutex);
    result = ksi_ipc_send_framed_message(
        ref->fd, message_type, client_id, correlation_id, payload, payload_size);
    pthread_mutex_unlock(&ref->send_mutex);
    return result;
}

bool hook_send_ref_note_event(ksi_hook_send_ref *ref, uint64_t event_id)
{
    bool ok = false;

    if (ref == NULL || event_id == 0u) {
        return false;
    }

    pthread_mutex_lock(&ref->order_mutex);

    if (ref->pending_event_count < KSI_HOOK_SEND_REF_MAX_PENDING) {
        size_t tail = (ref->pending_event_head + ref->pending_event_count)
            % KSI_HOOK_SEND_REF_MAX_PENDING;
        ref->pending_event_ids[tail] = event_id;
        ref->pending_event_count++;
        ok = true;
    }

    pthread_mutex_unlock(&ref->order_mutex);
    return ok;
}

/* Lane threads call this before writing HOOK_EVENT. Event ids are registered by
 * the daemon main thread in chronological dispatch order, so waiting for the
 * FIFO head gives one hook connection Windows-like keyboard+mouse serialization
 * without coupling unrelated connections or hook lanes. */
bool hook_send_ref_wait_event_turn(ksi_hook_send_ref *ref, uint64_t event_id, int timeout_ms)
{
    struct timespec absolute;
    bool is_turn;

    if (ref == NULL || event_id == 0u) {
        return true;
    }

    if (clock_gettime(CLOCK_MONOTONIC, &absolute) != 0) {
        return false;
    }

    if (timeout_ms < 0) {
        timeout_ms = 0;
    }

    absolute.tv_sec += timeout_ms / 1000;
    absolute.tv_nsec += (long)(timeout_ms % 1000) * 1000000L;

    if (absolute.tv_nsec >= 1000000000L) {
        absolute.tv_sec++;
        absolute.tv_nsec -= 1000000000L;
    }

    pthread_mutex_lock(&ref->order_mutex);
    while (ref->pending_event_count != 0u
        && ref->pending_event_ids[ref->pending_event_head] != event_id) {
        if (pthread_cond_timedwait(&ref->order_cond, &ref->order_mutex, &absolute) != 0) {
            break;
        }
    }

    is_turn = ref->pending_event_count == 0u
        || ref->pending_event_ids[ref->pending_event_head] == event_id;
    pthread_mutex_unlock(&ref->order_mutex);
    return is_turn;
}

void hook_send_ref_complete_event(ksi_hook_send_ref *ref, uint64_t event_id)
{
    if (ref == NULL || event_id == 0u) {
        return;
    }

    pthread_mutex_lock(&ref->order_mutex);

    for (size_t i = 0u; i < ref->pending_event_count; i++) {
        size_t idx = (ref->pending_event_head + i) % KSI_HOOK_SEND_REF_MAX_PENDING;

        if (ref->pending_event_ids[idx] == event_id) {
            ref->pending_event_ids[idx] = 0u;
            break;
        }
    }

    while (ref->pending_event_count != 0u
        && ref->pending_event_ids[ref->pending_event_head] == 0u) {
        ref->pending_event_head = (ref->pending_event_head + 1u)
            % KSI_HOOK_SEND_REF_MAX_PENDING;
        ref->pending_event_count--;
    }

    if (ref->pending_event_count == 0u) {
        ref->pending_event_head = 0u;
    }

    pthread_cond_broadcast(&ref->order_cond);
    pthread_mutex_unlock(&ref->order_mutex);
}

void hook_send_ref_release(ksi_hook_send_ref *ref)
{
    if (ref == NULL) {
        return;
    }

    if (atomic_fetch_sub(&ref->ref_count, 1u) == 1u) {
        pthread_mutex_lock(&registry_mutex);

        for (ksi_hook_send_ref **cursor = &registry; *cursor != NULL; cursor = &(*cursor)->next) {
            if (*cursor == ref) {
                *cursor = ref->next;
                break;
            }
        }

        pthread_mutex_unlock(&registry_mutex);
        ksi_ipc_close_client(ref->fd);
        pthread_cond_destroy(&ref->order_cond);
        pthread_mutex_destroy(&ref->order_mutex);
        pthread_mutex_destroy(&ref->send_mutex);
        free(ref);
    }
}

int ipc_send_locked(
    int client_fd,
    uint32_t message_type,
    uint32_t client_id,
    uint64_t correlation_id,
    const void *payload,
    size_t payload_size)
{
    ksi_hook_send_ref *ref = NULL;
    int result;

    pthread_mutex_lock(&registry_mutex);

    for (ksi_hook_send_ref *candidate = registry;
        candidate != NULL;
        candidate = candidate->next) {
        if (candidate->fd == client_fd && hook_send_ref_acquire(candidate)) {
            ref = candidate;
            break;
        }
    }

    pthread_mutex_unlock(&registry_mutex);

    if (ref == NULL) {
        return -1;
    }

    result = hook_send_ref_send(ref, message_type, client_id, correlation_id, payload, payload_size);
    hook_send_ref_release(ref);
    return result;
}
