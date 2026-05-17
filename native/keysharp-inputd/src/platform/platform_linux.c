#include "keysharp_inputd/platform.h"

#include "keysharp_inputd/linux_devices.h"
#include "keysharp_inputd/linux_synth.h"

#include <stdio.h>

static int linux_start(void)
{
    puts("linux input backend started");

    if (ksi_linux_synth_start() != 0) {
        return -1;
    }

    if (ksi_linux_devices_start() != 0) {
        ksi_linux_synth_stop();
        return -1;
    }

    return 0;
}

static void linux_stop(void)
{
    ksi_linux_devices_stop();
    ksi_linux_synth_stop();
    puts("linux input backend stopped");
}

static nfds_t linux_poll_fds(struct pollfd *fds, nfds_t max_fds)
{
    return ksi_linux_devices_poll_fds(fds, max_fds);
}

static void linux_process_fd(int fd)
{
    ksi_linux_devices_process_fd(fd);
}

static int linux_send_input(const ksi_input *inputs, size_t count)
{
    return ksi_linux_synth_send_input(inputs, count);
}

static int linux_replay_hook_event(uint32_t hook_type, const ksi_hook_event_payload *event)
{
    return ksi_linux_synth_replay_hook_event(hook_type, event);
}

static int linux_set_grab_enabled(bool enabled)
{
    return ksi_linux_devices_set_grab_enabled(enabled);
}

static void linux_set_hook_event_callback(ksi_hook_event_callback callback, void *context)
{
    ksi_linux_devices_set_hook_event_callback(callback, context);
}

static const ksi_platform_backend linux_backend = {
    .name = "linux",
    .start = linux_start,
    .stop = linux_stop,
    .poll_fds = linux_poll_fds,
    .process_fd = linux_process_fd,
    .send_input = linux_send_input,
    .replay_hook_event = linux_replay_hook_event,
    .set_grab_enabled = linux_set_grab_enabled,
    .set_hook_event_callback = linux_set_hook_event_callback,
};

const ksi_platform_backend *ksi_platform_backend_get(void)
{
    return &linux_backend;
}
