#ifndef KEYSHARP_INPUTD_CONNECTION_REF_H
#define KEYSHARP_INPUTD_CONNECTION_REF_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct ksi_hook_send_ref ksi_hook_send_ref;

ksi_hook_send_ref *hook_send_ref_create(int client_fd);
bool hook_send_ref_acquire(ksi_hook_send_ref *ref);
/* Invalidating rejects new I/O/dispatch immediately and wakes waiters, but the
 * transport fd is closed only when the final retained snapshot releases it. */
bool hook_send_ref_is_valid(const ksi_hook_send_ref *ref);
void hook_send_ref_invalidate(ksi_hook_send_ref *ref);
bool hook_send_ref_is_stalled(const ksi_hook_send_ref *ref, size_t lane_index);
void hook_send_ref_mark_stalled(ksi_hook_send_ref *ref, size_t lane_index);
void hook_send_ref_clear_stalled(ksi_hook_send_ref *ref, size_t lane_index);
int hook_send_ref_send(
    ksi_hook_send_ref *ref,
    uint32_t message_type,
    uint32_t client_id,
    uint64_t correlation_id,
    const void *payload,
    size_t payload_size);
/* Enter one script's shared hook thread. Keyboard and mouse turns serialize;
 * a recursive turn may re-enter only while the current callback is blocked in
 * a synchronous Send. Ready recursive turns take priority over ordinary ones. */
bool hook_send_ref_wait_event_turn(
    ksi_hook_send_ref *ref,
    uint64_t event_id,
    bool recursive,
    int timeout_ms);
void hook_send_ref_complete_event(ksi_hook_send_ref *ref, uint64_t event_id);
bool hook_send_ref_begin_pump(ksi_hook_send_ref *ref, uint64_t event_id);
/* Close event_id's pump to new sibling callbacks, then wait for already-entered
 * descendants to unwind. Their lane-owned callback deadlines keep this bounded. */
bool hook_send_ref_end_pump(ksi_hook_send_ref *ref, uint64_t event_id);
void hook_send_ref_release(ksi_hook_send_ref *ref);
int ipc_send_locked(
    int client_fd,
    uint32_t message_type,
    uint32_t client_id,
    uint64_t correlation_id,
    const void *payload,
    size_t payload_size);

#endif
