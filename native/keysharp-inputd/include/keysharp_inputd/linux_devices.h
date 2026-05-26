#ifndef KEYSHARP_INPUTD_LINUX_DEVICES_H
#define KEYSHARP_INPUTD_LINUX_DEVICES_H

#include <poll.h>
#include <stdint.h>

#include "keysharp_inputd/platform.h"

int ksi_linux_devices_start(void);
bool ksi_linux_devices_has_candidates(void);
void ksi_linux_devices_stop(void);
nfds_t ksi_linux_devices_poll_fds(struct pollfd *fds, nfds_t max_fds);
void ksi_linux_devices_process_fd(int fd);
int ksi_linux_devices_set_grab_hook_mask(uint32_t hook_mask);
int ksi_linux_devices_set_block_input_mask(uint32_t block_mask);
void ksi_linux_devices_record_synthetic_event(uint16_t type, uint16_t code, int32_t value, uint64_t extra_info, bool suppress);
void ksi_linux_devices_unrecord_last_synthetic_event(uint16_t type, uint16_t code, int32_t value);
void ksi_linux_devices_set_hook_event_callback(ksi_hook_event_callback callback, void *context);
void ksi_linux_devices_get_indicator_state(bool *caps_lock, bool *num_lock, bool *scroll_lock);
bool ksi_linux_devices_get_pointer_position(ksi_pointer_position_payload *position);

#endif
