#ifndef KEYSHARP_INPUTD_PLATFORM_H
#define KEYSHARP_INPUTD_PLATFORM_H

#include <poll.h>
#include <stdbool.h>
#include <stddef.h>

#include "keysharp_inputd/protocol.h"

typedef void (*ksi_hook_event_callback)(
    void *context,
    uint32_t hook_type,
    const void *event,
    size_t event_size);

typedef struct ksi_platform_backend {
    const char *name;
    int (*start)(void);
    void (*stop)(void);
    uint32_t (*get_available_capabilities)(void);
    nfds_t (*poll_fds)(struct pollfd *fds, nfds_t max_fds);
    void (*process_fd)(int fd);
    int (*send_input)(const ksi_input *inputs, size_t count, uint32_t flags);
    int (*replay_hook_event)(uint32_t hook_type, const ksi_hook_event_payload *event);
    int (*set_grab_hook_mask)(uint32_t hook_mask);
    int (*set_block_input_mask)(uint32_t block_mask);
    void (*set_hook_event_callback)(ksi_hook_event_callback callback, void *context);
    /* Release every key the backend has replayed/synthesized "down" on its virtual
     * device. Invoked ONLY from the output sequencer thread (via a
     * KSI_OUTPUT_ACTION_RELEASE_ALL action) so it is serialized with replay/synth and
     * never races them or stalls the daemon main thread. May be NULL. */
    void (*release_synthetic_keys)(void);
} ksi_platform_backend;

const ksi_platform_backend *ksi_platform_backend_get(void);

#endif
