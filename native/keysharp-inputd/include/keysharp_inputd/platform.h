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
    bool (*peek_oldest_pending_input)(int *out_fd, uint64_t *out_time_ms);
    bool (*input_to_hook_event)(
        const ksi_input *input,
        uint32_t *hook_type,
        ksi_hook_event_payload *event,
        size_t *event_size);
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
    /* Called periodically (roughly once per second, more often when other fd
     * activity wakes the main loop) from the daemon's main thread. Lets the
     * backend retry anything that failed transiently and was previously only
     * ever re-driven reactively by an unrelated event (or never at all) --
     * e.g. re-attempting a device grab lost to contention, or recreating a
     * synthetic output device after a write failure. Each backend is
     * responsible for its own internal rate-limiting; this may be called far
     * more often than any actual retry should happen. May be NULL. */
    void (*periodic_maintenance)(void);
} ksi_platform_backend;

const ksi_platform_backend *ksi_platform_backend_get(void);

#endif
