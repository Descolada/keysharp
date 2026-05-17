#ifndef KEYSHARP_INPUTD_LINUX_DEVICES_H
#define KEYSHARP_INPUTD_LINUX_DEVICES_H

#include <poll.h>
#include <stdint.h>

#include "keysharp_inputd/platform.h"

int ksi_linux_devices_start(void);
void ksi_linux_devices_stop(void);
nfds_t ksi_linux_devices_poll_fds(struct pollfd *fds, nfds_t max_fds);
void ksi_linux_devices_process_fd(int fd);
int ksi_linux_devices_set_grab_enabled(bool enabled);
void ksi_linux_devices_suppress_next_replay_event(uint16_t type, uint16_t code, int32_t value);
void ksi_linux_devices_set_hook_event_callback(ksi_hook_event_callback callback, void *context);

#endif
