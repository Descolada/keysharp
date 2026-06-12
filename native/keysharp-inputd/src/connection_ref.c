#include "connection_ref.h"

#include "keysharp_inputd/ipc.h"

#include <pthread.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

struct ksi_hook_send_ref {
    int fd;
    atomic_uint ref_count;
    pthread_mutex_t send_mutex;
    struct ksi_hook_send_ref *next;
};

static pthread_mutex_t registry_mutex = PTHREAD_MUTEX_INITIALIZER;
static ksi_hook_send_ref *registry;

ksi_hook_send_ref *hook_send_ref_create(int client_fd)
{
    ksi_hook_send_ref *ref = malloc(sizeof(*ref));

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

    pthread_mutex_lock(&ref->send_mutex);
    result = ksi_ipc_send_framed_message(
        ref->fd, message_type, client_id, correlation_id, payload, payload_size);
    pthread_mutex_unlock(&ref->send_mutex);
    hook_send_ref_release(ref);
    return result;
}

void ipc_close_locked(int client_fd)
{
    ksi_ipc_close_client(client_fd);
}
