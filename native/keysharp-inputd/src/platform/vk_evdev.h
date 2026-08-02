#pragma once

#include <stddef.h>
#include <stdint.h>

/*
 * Bidirectional VK ↔ evdev KEY_* mapping.
 *
 * ksi_vk_to_evdev  – Windows virtual-key code  → evdev KEY_* (for synthesis)
 * ksi_evdev_to_vk  – evdev KEY_* code           → Windows VK  (for hook events)
 *
 * key_count/key_at expose the same table to uinput capability setup.
 * The conversion functions return 0 / -1 when no mapping exists.
 */

int          ksi_vk_to_evdev(uint16_t vk);
uint32_t     ksi_evdev_to_vk(unsigned int evdev_code);
size_t       ksi_vk_evdev_key_count(void);
unsigned int ksi_vk_evdev_key_at(size_t index);
