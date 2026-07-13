#ifndef KEYSHARP_INPUTD_LINUX_SYNTH_H
#define KEYSHARP_INPUTD_LINUX_SYNTH_H

#include "keysharp_inputd/protocol.h"

#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>

int ksi_linux_synth_start(void);
void ksi_linux_synth_stop(void);
bool ksi_linux_synth_is_available(void);
/* Main-thread detector: returns true (rate-limited) when a prior write failure
 * has latched the device as broken and a recreation should be requested. Does
 * NOT touch the device itself, so it is safe to call off the sequencer thread. */
bool ksi_linux_synth_needs_recovery(void);
/* Sequencer-thread worker: tear down and recreate the uinput devices after a
 * write failure. MUST run on the output sequencer thread (it does stop()+start(),
 * which mutate the single-thread-owned uinput fds / key-down table). */
void ksi_linux_synth_recreate(void);
int ksi_linux_synth_send_input(const ksi_input *inputs, size_t count, uint32_t flags);
int ksi_linux_synth_replay_hook_event(uint32_t hook_type, const ksi_hook_event_payload *event);
bool ksi_linux_synth_input_to_hook_event(
    const ksi_input *input,
    uint32_t *hook_type,
    ksi_hook_event_payload *event,
    size_t *event_size);
void ksi_linux_synth_release_all(void);
void ksi_linux_synth_add_logical_key_state(uint8_t *keys, size_t key_bytes);
void ksi_linux_synth_add_logical_pointer_button_state(uint32_t *buttons);

/* Enqueue-time logical synthetic key state. note_enqueued_synth is called when a
 * synth batch is accepted into the output queue (so GET_KEY_STATE reflects the
 * post-drain state without waiting for the drain); reset_enqueued_synth clears it
 * when a RELEASE_ALL action is enqueued. */
void ksi_linux_synth_note_enqueued_synth(const ksi_input *inputs, size_t count);
void ksi_linux_synth_reset_enqueued_synth(void);

#endif
