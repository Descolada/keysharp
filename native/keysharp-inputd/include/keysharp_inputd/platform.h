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
    nfds_t (*poll_fds)(struct pollfd *fds, nfds_t max_fds);
    void (*process_fd)(int fd);
    int (*send_input)(const ksi_input *inputs, size_t count);
    int (*replay_hook_event)(uint32_t hook_type, const ksi_hook_event_payload *event);
    int (*set_grab_enabled)(bool enabled);
    void (*set_hook_event_callback)(ksi_hook_event_callback callback, void *context);
} ksi_platform_backend;

const ksi_platform_backend *ksi_platform_backend_get(void);

#endif
