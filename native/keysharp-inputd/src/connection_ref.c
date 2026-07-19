#include "connection_ref.h"

#include "keysharp_inputd/ipc.h"

#include <limits.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>

#define KSI_MAX_HOOK_CALL_DEPTH 64u

typedef struct ksi_hook_turn_waiter {
    bool recursive;
    struct ksi_hook_turn_waiter *next;
} ksi_hook_turn_waiter;

typedef struct ksi_hook_call_frame {
    uint64_t event_id;
    bool pumping;
} ksi_hook_call_frame;

/* One ref is one script hook thread, shared by that script's keyboard and
 * mouse subscriptions. The call stack is the complete serialization model:
 * ordinary callbacks enter only at depth zero; nested callbacks may enter
 * while the top callback is synchronously pumping Send. */
struct ksi_hook_send_ref {
    int fd;
    atomic_uint ref_count;
    atomic_bool valid;
    atomic_uint stalled_lanes;
    pthread_mutex_t send_mutex;
    pthread_mutex_t turn_mutex;
    pthread_cond_t turn_condition;
    ksi_hook_turn_waiter *waiters;
    ksi_hook_call_frame calls[KSI_MAX_HOOK_CALL_DEPTH];
    size_t call_depth;
    struct ksi_hook_send_ref *next;
};

static pthread_mutex_t registry_mutex = PTHREAD_MUTEX_INITIALIZER;
static ksi_hook_send_ref *registry;

static bool monotonic_deadline_after_ms(struct timespec *deadline, int timeout_ms)
{
    if (deadline == NULL || clock_gettime(CLOCK_MONOTONIC, deadline) != 0) {
        return false;
    }

    if (timeout_ms < 0) {
        timeout_ms = 0;
    }

    deadline->tv_sec += timeout_ms / 1000;
    deadline->tv_nsec += (long)(timeout_ms % 1000) * 1000000L;

    if (deadline->tv_nsec >= 1000000000L) {
        deadline->tv_sec++;
        deadline->tv_nsec -= 1000000000L;
    }

    return true;
}

ksi_hook_send_ref *hook_send_ref_create(int client_fd)
{
    ksi_hook_send_ref *ref = calloc(1, sizeof(*ref));
    pthread_condattr_t attributes;

    if (ref == NULL) {
        return NULL;
    }

    ref->fd = client_fd;
    atomic_init(&ref->ref_count, 1u);
    atomic_init(&ref->valid, true);

    if (pthread_mutex_init(&ref->send_mutex, NULL) != 0) {
        free(ref);
        return NULL;
    }

    if (pthread_mutex_init(&ref->turn_mutex, NULL) != 0) {
        pthread_mutex_destroy(&ref->send_mutex);
        free(ref);
        return NULL;
    }

    if (pthread_condattr_init(&attributes) != 0) {
        pthread_mutex_destroy(&ref->turn_mutex);
        pthread_mutex_destroy(&ref->send_mutex);
        free(ref);
        return NULL;
    }

    if (pthread_condattr_setclock(&attributes, CLOCK_MONOTONIC) != 0
        || pthread_cond_init(&ref->turn_condition, &attributes) != 0) {
        pthread_condattr_destroy(&attributes);
        pthread_mutex_destroy(&ref->turn_mutex);
        pthread_mutex_destroy(&ref->send_mutex);
        free(ref);
        return NULL;
    }

    pthread_condattr_destroy(&attributes);
    pthread_mutex_lock(&registry_mutex);
    ref->next = registry;
    registry = ref;
    pthread_mutex_unlock(&registry_mutex);
    return ref;
}

bool hook_send_ref_acquire(ksi_hook_send_ref *ref)
{
    unsigned int count;

    if (ref == NULL || !atomic_load(&ref->valid)) {
        return false;
    }

    count = atomic_load(&ref->ref_count);

    while (count != 0u && count != UINT_MAX) {
        if (atomic_compare_exchange_weak(&ref->ref_count, &count, count + 1u)) {
            /* Invalidation may race this increment. Retaining an invalid ref is
             * safe (I/O rejects it) and avoids recursive final-release while the
             * registry mutex is held by ipc_send_locked(). */
            return true;
        }
    }

    return false;
}

bool hook_send_ref_is_valid(const ksi_hook_send_ref *ref)
{
    return ref != NULL && atomic_load(&ref->valid);
}

void hook_send_ref_invalidate(ksi_hook_send_ref *ref)
{
    if (ref == NULL || !atomic_exchange(&ref->valid, false)) {
        return;
    }

    /* shutdown wakes socket I/O without allowing the fd number to be reused
     * while lane snapshots retain this ref. Final release remains the sole close. */
    if (ref->fd >= 0) {
        (void)shutdown(ref->fd, SHUT_RDWR);
    }

    pthread_mutex_lock(&ref->turn_mutex);
    pthread_cond_broadcast(&ref->turn_condition);
    pthread_mutex_unlock(&ref->turn_mutex);
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
    int result = -1;

    if (ref == NULL || !atomic_load(&ref->valid)) {
        return -1;
    }

    pthread_mutex_lock(&ref->send_mutex);

    if (atomic_load(&ref->valid)) {
        result = ksi_ipc_send_framed_message(
            ref->fd, message_type, client_id, correlation_id, payload, payload_size);
    }

    pthread_mutex_unlock(&ref->send_mutex);
    return result;
}

static void append_waiter(
    ksi_hook_send_ref *ref,
    ksi_hook_turn_waiter *waiter)
{
    ksi_hook_turn_waiter **tail = &ref->waiters;

    while (*tail != NULL) {
        tail = &(*tail)->next;
    }

    *tail = waiter;
}

static void remove_waiter(
    ksi_hook_send_ref *ref,
    ksi_hook_turn_waiter *waiter)
{
    for (ksi_hook_turn_waiter **cursor = &ref->waiters;
        *cursor != NULL;
        cursor = &(*cursor)->next) {
        if (*cursor == waiter) {
            *cursor = waiter->next;
            waiter->next = NULL;
            return;
        }
    }
}

static ksi_hook_turn_waiter *best_waiter(const ksi_hook_send_ref *ref)
{
    for (ksi_hook_turn_waiter *waiter = ref->waiters;
        waiter != NULL;
        waiter = waiter->next) {
        if (waiter->recursive) {
            return waiter;
        }
    }

    return ref->waiters;
}

static bool waiter_can_enter(
    const ksi_hook_send_ref *ref,
    const ksi_hook_turn_waiter *waiter)
{
    if (ref->call_depth >= KSI_MAX_HOOK_CALL_DEPTH
        || best_waiter(ref) != waiter) {
        return false;
    }

    if (ref->call_depth == 0u) {
        return true;
    }

    return waiter->recursive
        && ref->calls[ref->call_depth - 1u].pumping;
}

static size_t find_call(const ksi_hook_send_ref *ref, uint64_t event_id)
{
    for (size_t i = ref->call_depth; i > 0u; i--) {
        if (ref->calls[i - 1u].event_id == event_id) {
            return i - 1u;
        }
    }

    return SIZE_MAX;
}

bool hook_send_ref_wait_event_turn(
    ksi_hook_send_ref *ref,
    uint64_t event_id,
    bool recursive,
    int timeout_ms)
{
    ksi_hook_turn_waiter waiter = {
        .recursive = recursive,
    };
    struct timespec deadline;
    bool entered = false;

    if (ref == NULL || event_id == 0u || !atomic_load(&ref->valid)
        || !monotonic_deadline_after_ms(&deadline, timeout_ms)) {
        return false;
    }

    pthread_mutex_lock(&ref->turn_mutex);
    append_waiter(ref, &waiter);

    while (atomic_load(&ref->valid)) {
        if (waiter_can_enter(ref, &waiter)) {
            remove_waiter(ref, &waiter);
            ref->calls[ref->call_depth++].event_id = event_id;
            entered = true;
            break;
        }

        if (pthread_cond_timedwait(
                &ref->turn_condition, &ref->turn_mutex, &deadline) != 0) {
            break;
        }
    }

    if (!entered) {
        remove_waiter(ref, &waiter);
        pthread_cond_broadcast(&ref->turn_condition);
    }

    pthread_mutex_unlock(&ref->turn_mutex);
    return entered;
}

void hook_send_ref_complete_event(ksi_hook_send_ref *ref, uint64_t event_id)
{
    if (ref == NULL || event_id == 0u) {
        return;
    }

    pthread_mutex_lock(&ref->turn_mutex);

    size_t index = find_call(ref, event_id);

    if (index != SIZE_MAX) {
        if (index + 1u < ref->call_depth) {
            memmove(&ref->calls[index], &ref->calls[index + 1u],
                (ref->call_depth - index - 1u) * sizeof(ref->calls[0]));
        }

        memset(&ref->calls[--ref->call_depth], 0, sizeof(ref->calls[0]));
    }

    pthread_cond_broadcast(&ref->turn_condition);
    pthread_mutex_unlock(&ref->turn_mutex);
}

bool hook_send_ref_begin_pump(ksi_hook_send_ref *ref, uint64_t event_id)
{
    bool ok = false;

    if (ref == NULL || event_id == 0u || !atomic_load(&ref->valid)) {
        return false;
    }

    pthread_mutex_lock(&ref->turn_mutex);

    if (ref->call_depth != 0u
        && ref->calls[ref->call_depth - 1u].event_id == event_id
        && !ref->calls[ref->call_depth - 1u].pumping) {
        ref->calls[ref->call_depth - 1u].pumping = true;
        ok = true;
        pthread_cond_broadcast(&ref->turn_condition);
    }

    pthread_mutex_unlock(&ref->turn_mutex);
    return ok;
}

bool hook_send_ref_end_pump(ksi_hook_send_ref *ref, uint64_t event_id)
{
    size_t index;
    bool ended = false;

    if (ref == NULL || event_id == 0u || !atomic_load(&ref->valid)) {
        return false;
    }

    pthread_mutex_lock(&ref->turn_mutex);
    index = find_call(ref, event_id);

    if (index != SIZE_MAX && ref->calls[index].pumping) {
        /* Stop admitting sibling recursive turns before waiting for recursive
         * callbacks which already entered above this parent. Descendants may
         * still pump their own Sends and unwind normally. */
        ref->calls[index].pumping = false;
        pthread_cond_broadcast(&ref->turn_condition);

        while (atomic_load(&ref->valid)
            && ref->call_depth > index + 1u
            && ref->calls[index].event_id == event_id) {
            (void)pthread_cond_wait(&ref->turn_condition, &ref->turn_mutex);
        }

        ended = atomic_load(&ref->valid)
            && ref->call_depth == index + 1u
            && ref->calls[index].event_id == event_id;
    }

    pthread_cond_broadcast(&ref->turn_condition);
    pthread_mutex_unlock(&ref->turn_mutex);
    return ended;
}

void hook_send_ref_release(ksi_hook_send_ref *ref)
{
    if (ref == NULL || atomic_fetch_sub(&ref->ref_count, 1u) != 1u) {
        return;
    }

    pthread_mutex_lock(&registry_mutex);

    for (ksi_hook_send_ref **cursor = &registry;
        *cursor != NULL;
        cursor = &(*cursor)->next) {
        if (*cursor == ref) {
            *cursor = ref->next;
            break;
        }
    }

    pthread_mutex_unlock(&registry_mutex);
    ksi_ipc_close_client(ref->fd);
    pthread_cond_destroy(&ref->turn_condition);
    pthread_mutex_destroy(&ref->turn_mutex);
    pthread_mutex_destroy(&ref->send_mutex);
    free(ref);
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

    result = hook_send_ref_send(
        ref, message_type, client_id, correlation_id, payload, payload_size);
    hook_send_ref_release(ref);
    return result;
}
