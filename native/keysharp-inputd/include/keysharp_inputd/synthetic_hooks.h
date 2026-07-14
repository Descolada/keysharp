#ifndef KEYSHARP_INPUTD_SYNTHETIC_HOOKS_H
#define KEYSHARP_INPUTD_SYNTHETIC_HOOKS_H

#include "keysharp_inputd/protocol.h"

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Maximum number of low-level mouse hook events produced by one ksi_input. */
#define KSI_SYNTHETIC_HOOK_MAX_EXPANSION 11u

size_t ksi_synthetic_hook_input_count(const ksi_input *input);

bool ksi_synthetic_hook_batch_count(
    const ksi_input *inputs,
    uint32_t count,
    size_t limit,
    size_t *expanded_count);

/* outputs must have room for KSI_SYNTHETIC_HOOK_MAX_EXPANSION elements. */
size_t ksi_synthetic_hook_expand_input(const ksi_input *input, ksi_input *outputs);

#endif
