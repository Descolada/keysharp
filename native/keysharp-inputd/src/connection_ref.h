#ifndef KEYSHARP_INPUTD_CONNECTION_REF_H
#define KEYSHARP_INPUTD_CONNECTION_REF_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct ksi_hook_send_ref ksi_hook_send_ref;

ksi_hook_send_ref *hook_send_ref_create(int client_fd);
bool hook_send_ref_acquire(ksi_hook_send_ref *ref);
int hook_send_ref_fd(const ksi_hook_send_ref *ref);
void hook_send_ref_release(ksi_hook_send_ref *ref);
int ipc_send_locked(
    int client_fd,
    uint32_t message_type,
    uint32_t client_id,
    uint64_t correlation_id,
    const void *payload,
    size_t payload_size);
void ipc_close_locked(int client_fd);

#endif
