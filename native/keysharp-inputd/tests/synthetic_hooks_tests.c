#include "keysharp_inputd/synthetic_hooks.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CHECK(condition) do { \
    if (!(condition)) { \
        fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #condition); \
        return EXIT_FAILURE; \
    } \
} while (0)

int main(void)
{
    enum { input_count = 1024, expansion_limit = 4096 };
    ksi_input mouse;
    ksi_input expanded[KSI_SYNTHETIC_HOOK_MAX_EXPANSION];
    ksi_input *inputs;
    size_t count = 0u;

    memset(&mouse, 0, sizeof(mouse));
    mouse.type = KSI_INPUT_MOUSE;
    mouse.data.mouse.dx = 17;
    mouse.data.mouse.dy = -9;
    mouse.data.mouse.flags = KSI_MOUSEEVENTF_MOVE
        | KSI_MOUSEEVENTF_LEFTDOWN | KSI_MOUSEEVENTF_LEFTUP
        | KSI_MOUSEEVENTF_WHEEL;

    CHECK(ksi_synthetic_hook_input_count(&mouse) == 4u);
    CHECK(ksi_synthetic_hook_expand_input(&mouse, expanded) == 4u);
    CHECK(expanded[0].data.mouse.flags == KSI_MOUSEEVENTF_MOVE);
    CHECK(expanded[1].data.mouse.dx == 0);
    CHECK(expanded[1].data.mouse.dy == 0);

    inputs = calloc(input_count, sizeof(*inputs));
    CHECK(inputs != NULL);

    for (size_t i = 0u; i < input_count; i++) {
        inputs[i] = mouse;
    }

    CHECK(ksi_synthetic_hook_batch_count(inputs, input_count,
        expansion_limit, &count));
    CHECK(count == expansion_limit);
    inputs[0].data.mouse.flags |= KSI_MOUSEEVENTF_RIGHTDOWN;
    CHECK(!ksi_synthetic_hook_batch_count(inputs, input_count,
        expansion_limit, &count));
    free(inputs);
    puts("PASS synthetic hook expansion boundaries");
    return EXIT_SUCCESS;
}
