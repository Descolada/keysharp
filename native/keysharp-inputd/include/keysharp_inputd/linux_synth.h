#ifndef KEYSHARP_INPUTD_LINUX_SYNTH_H
#define KEYSHARP_INPUTD_LINUX_SYNTH_H

#include "keysharp_inputd/protocol.h"

#include <stddef.h>

int ksi_linux_synth_start(void);
void ksi_linux_synth_stop(void);
int ksi_linux_synth_send_input(const ksi_input *inputs, size_t count);
int ksi_linux_synth_replay_hook_event(uint32_t hook_type, const ksi_hook_event_payload *event);

#endif
