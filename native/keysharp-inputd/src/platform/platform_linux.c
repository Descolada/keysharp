#include "keysharp_inputd/platform.h"

#include "keysharp_inputd/linux_devices.h"
#include "keysharp_inputd/linux_synth.h"

#include <stdbool.h>
#include <stdint.h>
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

static uint32_t linux_get_available_capabilities(void)
{
    uint32_t caps = 0;
    bool synth_available = ksi_linux_synth_is_available();

    if (synth_available) {
        caps |= KSI_CAP_SYNTH_KEYBOARD | KSI_CAP_SYNTH_MOUSE;
    }

    if (synth_available && ksi_linux_devices_has_candidates()) {
        caps |= KSI_CAP_HOOK_KEYBOARD | KSI_CAP_HOOK_MOUSE | KSI_CAP_BLOCK_INPUT;
    }

    return caps;
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

static int linux_send_input(const ksi_input *inputs, size_t count, uint32_t flags)
{
    return ksi_linux_synth_send_input(inputs, count, flags);
}

static int linux_replay_hook_event(uint32_t hook_type, const ksi_hook_event_payload *event)
{
    return ksi_linux_synth_replay_hook_event(hook_type, event);
}

static int linux_set_grab_hook_mask(uint32_t hook_mask)
{
    /* Releasing keys that were replayed "down" when the keyboard is ungrabbed is no
     * longer done here: it now runs on the output sequencer thread via a
     * KSI_OUTPUT_ACTION_RELEASE_ALL action enqueued by update_grab_state when the
     * keyboard grab is dropped. That keeps release serialized with replay/synth (no
     * race) and off this (daemon-main-thread) call path (no stall). */
    return ksi_linux_devices_set_grab_hook_mask(hook_mask);
}

static void linux_release_synthetic_keys(void)
{
    ksi_linux_synth_release_all();
}

static int linux_set_block_input_mask(uint32_t block_mask)
{
    return ksi_linux_devices_set_block_input_mask(block_mask);
}

static void linux_set_hook_event_callback(ksi_hook_event_callback callback, void *context)
{
    ksi_linux_devices_set_hook_event_callback(callback, context);
}

static const ksi_platform_backend linux_backend = {
    .name = "linux",
    .start = linux_start,
    .stop = linux_stop,
    .get_available_capabilities = linux_get_available_capabilities,
    .poll_fds = linux_poll_fds,
    .process_fd = linux_process_fd,
    .send_input = linux_send_input,
    .replay_hook_event = linux_replay_hook_event,
    .set_grab_hook_mask = linux_set_grab_hook_mask,
    .set_block_input_mask = linux_set_block_input_mask,
    .set_hook_event_callback = linux_set_hook_event_callback,
    .release_synthetic_keys = linux_release_synthetic_keys,
};

const ksi_platform_backend *ksi_platform_backend_get(void)
{
    return &linux_backend;
}
