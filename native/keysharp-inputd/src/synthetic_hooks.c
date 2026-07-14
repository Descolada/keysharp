#include "keysharp_inputd/synthetic_hooks.h"

static const struct {
    uint32_t trigger;
    uint32_t flags;
    bool mask_flags;
    bool keep_motion;
} mouse_primitives[] = {
    { KSI_MOUSEEVENTF_MOVE, KSI_MOUSEEVENTF_MOVE | KSI_MOUSEEVENTF_MOVE_NOCOALESCE
        | KSI_MOUSEEVENTF_VIRTUALDESK | KSI_MOUSEEVENTF_ABSOLUTE, true, true },
    { KSI_MOUSEEVENTF_WHEEL, KSI_MOUSEEVENTF_WHEEL, false, false },
    { KSI_MOUSEEVENTF_HWHEEL, KSI_MOUSEEVENTF_HWHEEL, false, false },
    { KSI_MOUSEEVENTF_LEFTDOWN, KSI_MOUSEEVENTF_LEFTDOWN, false, false },
    { KSI_MOUSEEVENTF_LEFTUP, KSI_MOUSEEVENTF_LEFTUP, false, false },
    { KSI_MOUSEEVENTF_RIGHTDOWN, KSI_MOUSEEVENTF_RIGHTDOWN, false, false },
    { KSI_MOUSEEVENTF_RIGHTUP, KSI_MOUSEEVENTF_RIGHTUP, false, false },
    { KSI_MOUSEEVENTF_MIDDLEDOWN, KSI_MOUSEEVENTF_MIDDLEDOWN, false, false },
    { KSI_MOUSEEVENTF_MIDDLEUP, KSI_MOUSEEVENTF_MIDDLEUP, false, false },
    { KSI_MOUSEEVENTF_XDOWN, KSI_MOUSEEVENTF_XDOWN, false, false },
    { KSI_MOUSEEVENTF_XUP, KSI_MOUSEEVENTF_XUP, false, false },
};

size_t ksi_synthetic_hook_input_count(const ksi_input *input)
{
    size_t count = 0u;

    if (input == NULL || input->type != KSI_INPUT_MOUSE) {
        return input == NULL ? 0u : 1u;
    }

    for (size_t i = 0u; i < sizeof(mouse_primitives) / sizeof(mouse_primitives[0]); i++) {
        if ((input->data.mouse.flags & mouse_primitives[i].trigger) != 0u) {
            count++;
        }
    }

    return count == 0u ? 1u : count;
}

bool ksi_synthetic_hook_batch_count(
    const ksi_input *inputs,
    uint32_t count,
    size_t limit,
    size_t *expanded_count)
{
    size_t total = 0u;

    if (expanded_count == NULL || (count != 0u && inputs == NULL)) {
        return false;
    }

    for (uint32_t i = 0u; i < count; i++) {
        size_t input_count = ksi_synthetic_hook_input_count(&inputs[i]);

        if (input_count > limit - total) {
            return false;
        }

        total += input_count;
    }

    *expanded_count = total;
    return true;
}

size_t ksi_synthetic_hook_expand_input(const ksi_input *input, ksi_input *outputs)
{
    const ksi_mouseinput *mouse;
    size_t count = 0u;

    if (input == NULL || outputs == NULL) {
        return 0u;
    }

    if (input->type != KSI_INPUT_MOUSE) {
        outputs[0] = *input;
        return 1u;
    }

    mouse = &input->data.mouse;

    for (size_t i = 0u; i < sizeof(mouse_primitives) / sizeof(mouse_primitives[0]); i++) {
        ksi_input *primitive;

        if ((mouse->flags & mouse_primitives[i].trigger) == 0u) {
            continue;
        }

        primitive = &outputs[count++];
        *primitive = *input;
        primitive->data.mouse.flags = mouse_primitives[i].mask_flags
            ? mouse->flags & mouse_primitives[i].flags
            : mouse_primitives[i].flags;

        if (!mouse_primitives[i].keep_motion) {
            primitive->data.mouse.dx = 0;
            primitive->data.mouse.dy = 0;
        }
    }

    if (count == 0u) {
        outputs[0] = *input;
        return 1u;
    }

    return count;
}
