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
void ksi_linux_devices_retry_incomplete_grabs(void);
bool ksi_linux_devices_peek_oldest_pending_event(int *out_fd, uint64_t *out_time_ns);
void ksi_linux_devices_set_hook_event_callback(ksi_hook_event_callback callback, void *context);
void ksi_linux_devices_get_indicator_state(bool *caps_lock, bool *num_lock, bool *scroll_lock);
void ksi_linux_devices_refresh_indicator_state(void);
bool ksi_linux_devices_get_pointer_position(ksi_pointer_position_payload *position);
bool ksi_linux_devices_get_pointer_buttons(ksi_pointer_buttons_payload *result);
bool ksi_linux_devices_get_key_state(ksi_key_state_payload *result);
uint32_t ksi_linux_devices_get_modifier_state(void);

#endif
