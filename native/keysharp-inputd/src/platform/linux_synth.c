#include "keysharp_inputd/linux_synth.h"

#include "keysharp_inputd/linux_devices.h"

#include <errno.h>
#include <fcntl.h>
#include <linux/input.h>
#include <linux/uinput.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

#define KSI_UINPUT_PATH "/dev/uinput"

static int uinput_fd = -1;
static bool suppress_replay_events;

static int emit_event(uint16_t type, uint16_t code, int32_t value)
{
    struct input_event event;

    if (uinput_fd < 0) {
        return -1;
    }

    memset(&event, 0, sizeof(event));
    event.type = type;
    event.code = code;
    event.value = value;

    if (write(uinput_fd, &event, sizeof(event)) != (ssize_t)sizeof(event)) {
        return -1;
    }

    if (suppress_replay_events) {
        ksi_linux_devices_suppress_next_replay_event(type, code, value);
    }

    return 0;
}

static int emit_sync(void)
{
    return emit_event(EV_SYN, SYN_REPORT, 0);
}

static int enable_event(int event_type)
{
    if (ioctl(uinput_fd, UI_SET_EVBIT, event_type) < 0) {
        fprintf(stderr, "inputd: uinput UI_SET_EVBIT(%d) failed: %s\n", event_type, strerror(errno));
        return -1;
    }

    return 0;
}

static int enable_key(int key_code)
{
    if (ioctl(uinput_fd, UI_SET_KEYBIT, key_code) < 0) {
        fprintf(stderr, "inputd: uinput UI_SET_KEYBIT(%d) failed: %s\n", key_code, strerror(errno));
        return -1;
    }

    return 0;
}

static int enable_relative(int relative_code)
{
    if (ioctl(uinput_fd, UI_SET_RELBIT, relative_code) < 0) {
        fprintf(stderr, "inputd: uinput UI_SET_RELBIT(%d) failed: %s\n", relative_code, strerror(errno));
        return -1;
    }

    return 0;
}

static int vk_to_evdev_key(uint16_t vk)
{
    if (vk >= 0x41u && vk <= 0x5Au) {
        return KEY_A + (int)(vk - 0x41u);
    }

    if (vk >= 0x31u && vk <= 0x39u) {
        return KEY_1 + (int)(vk - 0x31u);
    }

    switch (vk) {
        case 0x30u:
            return KEY_0;
        case 0x08u:
            return KEY_BACKSPACE;
        case 0x09u:
            return KEY_TAB;
        case 0x0Du:
            return KEY_ENTER;
        case 0x1Bu:
            return KEY_ESC;
        case 0x20u:
            return KEY_SPACE;
        case 0x21u:
            return KEY_PAGEUP;
        case 0x22u:
            return KEY_PAGEDOWN;
        case 0x23u:
            return KEY_END;
        case 0x24u:
            return KEY_HOME;
        case 0x25u:
            return KEY_LEFT;
        case 0x26u:
            return KEY_UP;
        case 0x27u:
            return KEY_RIGHT;
        case 0x28u:
            return KEY_DOWN;
        case 0x2Du:
            return KEY_INSERT;
        case 0x2Eu:
            return KEY_DELETE;
        case 0x5Bu:
            return KEY_LEFTMETA;
        case 0x5Cu:
            return KEY_RIGHTMETA;
        case 0x5Du:
            return KEY_COMPOSE;
        case 0x60u:
        case 0x61u:
        case 0x62u:
        case 0x63u:
        case 0x64u:
        case 0x65u:
        case 0x66u:
        case 0x67u:
        case 0x68u:
        case 0x69u:
            return KEY_KP0 + (int)(vk - 0x60u);
        case 0x6Au:
            return KEY_KPASTERISK;
        case 0x6Bu:
            return KEY_KPPLUS;
        case 0x6Du:
            return KEY_KPMINUS;
        case 0x6Eu:
            return KEY_KPDOT;
        case 0x6Fu:
            return KEY_KPSLASH;
        case 0x2Cu:
            return KEY_SYSRQ;
        case 0x13u:
            return KEY_PAUSE;
        case 0x70u:
        case 0x71u:
        case 0x72u:
        case 0x73u:
        case 0x74u:
        case 0x75u:
        case 0x76u:
        case 0x77u:
        case 0x78u:
        case 0x79u:
        case 0x7Au:
        case 0x7Bu:
            return KEY_F1 + (int)(vk - 0x70u);
        case 0x7Cu:
        case 0x7Du:
        case 0x7Eu:
        case 0x7Fu:
        case 0x80u:
        case 0x81u:
        case 0x82u:
        case 0x83u:
        case 0x84u:
        case 0x85u:
        case 0x86u:
        case 0x87u:
            return KEY_F13 + (int)(vk - 0x7Cu);
        case 0x90u:
            return KEY_NUMLOCK;
        case 0x91u:
            return KEY_SCROLLLOCK;
        case 0xBAu:
            return KEY_SEMICOLON;
        case 0xBBu:
            return KEY_EQUAL;
        case 0xBCu:
            return KEY_COMMA;
        case 0xBDu:
            return KEY_MINUS;
        case 0xBEu:
            return KEY_DOT;
        case 0xBFu:
            return KEY_SLASH;
        case 0xC0u:
            return KEY_GRAVE;
        case 0xDBu:
            return KEY_LEFTBRACE;
        case 0xDCu:
            return KEY_BACKSLASH;
        case 0xDDu:
            return KEY_RIGHTBRACE;
        case 0xDEu:
            return KEY_APOSTROPHE;
        case 0xA0u:
            return KEY_LEFTSHIFT;
        case 0xA1u:
            return KEY_RIGHTSHIFT;
        case 0xA2u:
            return KEY_LEFTCTRL;
        case 0xA3u:
            return KEY_RIGHTCTRL;
        case 0xA4u:
            return KEY_LEFTALT;
        case 0xA5u:
            return KEY_RIGHTALT;
        default:
            return -1;
    }
}

static int scan_to_evdev_key(uint16_t scan)
{
    if (scan == 0) {
        return -1;
    }

    if (scan <= KEY_MAX) {
        return scan;
    }

    return -1;
}

static int enable_keyboard_keys(void)
{
    static const int keys[] = {
        KEY_A, KEY_B, KEY_C, KEY_D, KEY_E, KEY_F, KEY_G, KEY_H, KEY_I, KEY_J,
        KEY_K, KEY_L, KEY_M, KEY_N, KEY_O, KEY_P, KEY_Q, KEY_R, KEY_S, KEY_T,
        KEY_U, KEY_V, KEY_W, KEY_X, KEY_Y, KEY_Z,
        KEY_1, KEY_2, KEY_3, KEY_4, KEY_5, KEY_6, KEY_7, KEY_8, KEY_9, KEY_0,
        KEY_ESC, KEY_BACKSPACE, KEY_TAB, KEY_ENTER, KEY_SPACE,
        KEY_LEFTCTRL, KEY_RIGHTCTRL, KEY_LEFTSHIFT, KEY_RIGHTSHIFT,
        KEY_LEFTALT, KEY_RIGHTALT, KEY_LEFTMETA, KEY_RIGHTMETA,
        KEY_COMPOSE, KEY_SYSRQ, KEY_PAUSE,
        KEY_CAPSLOCK, KEY_NUMLOCK, KEY_SCROLLLOCK,
        KEY_INSERT, KEY_DELETE, KEY_HOME, KEY_END, KEY_PAGEUP, KEY_PAGEDOWN,
        KEY_UP, KEY_DOWN, KEY_LEFT, KEY_RIGHT,
        KEY_MINUS, KEY_EQUAL, KEY_LEFTBRACE, KEY_RIGHTBRACE, KEY_BACKSLASH,
        KEY_SEMICOLON, KEY_APOSTROPHE, KEY_GRAVE, KEY_COMMA, KEY_DOT, KEY_SLASH,
        KEY_KP0, KEY_KP1, KEY_KP2, KEY_KP3, KEY_KP4,
        KEY_KP5, KEY_KP6, KEY_KP7, KEY_KP8, KEY_KP9,
        KEY_KPDOT, KEY_KPSLASH, KEY_KPASTERISK, KEY_KPMINUS, KEY_KPPLUS,
        KEY_KPENTER,
        KEY_F1, KEY_F2, KEY_F3, KEY_F4, KEY_F5, KEY_F6,
        KEY_F7, KEY_F8, KEY_F9, KEY_F10, KEY_F11, KEY_F12,
        KEY_F13, KEY_F14, KEY_F15, KEY_F16, KEY_F17, KEY_F18,
        KEY_F19, KEY_F20, KEY_F21, KEY_F22, KEY_F23, KEY_F24,
    };

    for (size_t i = 0; i < sizeof(keys) / sizeof(keys[0]); i++) {
        if (enable_key(keys[i]) != 0) {
            return -1;
        }
    }

    return 0;
}

static int configure_uinput_device(void)
{
    struct uinput_setup setup;

    if (enable_event(EV_KEY) != 0 || enable_event(EV_REL) != 0) {
        return -1;
    }

    if (enable_keyboard_keys() != 0) {
        return -1;
    }

    if (enable_key(BTN_LEFT) != 0
        || enable_key(BTN_RIGHT) != 0
        || enable_key(BTN_MIDDLE) != 0
        || enable_key(BTN_SIDE) != 0
        || enable_key(BTN_EXTRA) != 0) {
        return -1;
    }

    if (enable_relative(REL_X) != 0
        || enable_relative(REL_Y) != 0
        || enable_relative(REL_WHEEL) != 0
        || enable_relative(REL_HWHEEL) != 0) {
        return -1;
    }

    memset(&setup, 0, sizeof(setup));
    (void)snprintf(setup.name, sizeof(setup.name), "%s", KSI_SYNTH_DEVICE_NAME);
    setup.id.bustype = KSI_SYNTH_DEVICE_BUSTYPE;
    setup.id.vendor = KSI_SYNTH_DEVICE_VENDOR;
    setup.id.product = KSI_SYNTH_DEVICE_PRODUCT;
    setup.id.version = KSI_SYNTH_DEVICE_VERSION;

    if (ioctl(uinput_fd, UI_DEV_SETUP, &setup) < 0) {
        fprintf(stderr, "inputd: uinput UI_DEV_SETUP failed: %s\n", strerror(errno));
        return -1;
    }

    if (ioctl(uinput_fd, UI_DEV_CREATE) < 0) {
        fprintf(stderr, "inputd: uinput UI_DEV_CREATE failed: %s\n", strerror(errno));
        return -1;
    }

    return 0;
}

int ksi_linux_synth_start(void)
{
    uinput_fd = open(KSI_UINPUT_PATH, O_WRONLY | O_NONBLOCK);

    if (uinput_fd < 0) {
        fprintf(stderr, "inputd: cannot open %s: %s\n", KSI_UINPUT_PATH, strerror(errno));
        return 0;
    }

    if (configure_uinput_device() != 0) {
        close(uinput_fd);
        uinput_fd = -1;
        return 0;
    }

    puts("inputd: uinput virtual device created");
    return 0;
}

void ksi_linux_synth_stop(void)
{
    if (uinput_fd >= 0) {
        (void)ioctl(uinput_fd, UI_DEV_DESTROY);
        close(uinput_fd);
        uinput_fd = -1;
        puts("inputd: uinput virtual device destroyed");
    }
}

static int send_keyboard_input(const ksi_keybdinput *input)
{
    int key_code = -1;
    int value = (input->flags & KSI_KEYEVENTF_KEYUP) != 0 ? 0 : 1;

    if ((input->flags & KSI_KEYEVENTF_UNICODE) != 0) {
        fprintf(stderr, "inputd: unicode KEYBDINPUT synthesis is not implemented yet\n");
        return -1;
    }

    if ((input->flags & KSI_KEYEVENTF_SCANCODE) != 0) {
        key_code = scan_to_evdev_key(input->scan);
    }

    if (key_code < 0
        && input->vk == 0x0Du
        && (input->flags & KSI_KEYEVENTF_EXTENDEDKEY) != 0) {
        key_code = KEY_KPENTER;
    }

    if (key_code < 0) {
        key_code = vk_to_evdev_key(input->vk);
    }

    if (key_code < 0) {
        fprintf(stderr, "inputd: unsupported keyboard input vk=0x%x scan=%u flags=0x%x\n",
            input->vk,
            input->scan,
            input->flags);
        return -1;
    }

    if (emit_event(EV_KEY, (uint16_t)key_code, value) != 0 || emit_sync() != 0) {
        fprintf(stderr, "inputd: failed to emit keyboard input: %s\n", strerror(errno));
        return -1;
    }

    printf("inputd: synth key vk=0x%x scan=%u evdev=%d %s\n",
        input->vk,
        input->scan,
        key_code,
        value == 0 ? "up" : "down");

    return 0;
}

static int send_mouse_button(uint16_t button, bool down)
{
    if (emit_event(EV_KEY, button, down ? 1 : 0) != 0) {
        return -1;
    }

    return 0;
}

static uint16_t mouse_data_to_xbutton(uint32_t mouse_data)
{
    uint32_t xbutton = mouse_data >> 16;

    if (xbutton == KSI_XBUTTON1) {
        return BTN_SIDE;
    }

    if (xbutton == KSI_XBUTTON2) {
        return BTN_EXTRA;
    }

    return 0;
}

static int send_mouse_input(const ksi_mouseinput *input)
{
    uint16_t xbutton;

    if ((input->flags & KSI_MOUSEEVENTF_MOVE) != 0) {
        if (input->dx != 0 && emit_event(EV_REL, REL_X, input->dx) != 0) {
            return -1;
        }

        if (input->dy != 0 && emit_event(EV_REL, REL_Y, input->dy) != 0) {
            return -1;
        }
    }

    if ((input->flags & KSI_MOUSEEVENTF_WHEEL) != 0) {
        int32_t delta = (int32_t)input->mouse_data / 120;

        if (delta == 0 && input->mouse_data != 0) {
            delta = input->mouse_data > 0 ? 1 : -1;
        }

        if (emit_event(EV_REL, REL_WHEEL, delta) != 0) {
            return -1;
        }
    }

    if ((input->flags & KSI_MOUSEEVENTF_HWHEEL) != 0) {
        int32_t delta = (int32_t)input->mouse_data / 120;

        if (delta == 0 && input->mouse_data != 0) {
            delta = input->mouse_data > 0 ? 1 : -1;
        }

        if (emit_event(EV_REL, REL_HWHEEL, delta) != 0) {
            return -1;
        }
    }

    if ((input->flags & KSI_MOUSEEVENTF_LEFTDOWN) != 0 && send_mouse_button(BTN_LEFT, true) != 0) {
        return -1;
    }

    if ((input->flags & KSI_MOUSEEVENTF_LEFTUP) != 0 && send_mouse_button(BTN_LEFT, false) != 0) {
        return -1;
    }

    if ((input->flags & KSI_MOUSEEVENTF_RIGHTDOWN) != 0 && send_mouse_button(BTN_RIGHT, true) != 0) {
        return -1;
    }

    if ((input->flags & KSI_MOUSEEVENTF_RIGHTUP) != 0 && send_mouse_button(BTN_RIGHT, false) != 0) {
        return -1;
    }

    if ((input->flags & KSI_MOUSEEVENTF_MIDDLEDOWN) != 0 && send_mouse_button(BTN_MIDDLE, true) != 0) {
        return -1;
    }

    if ((input->flags & KSI_MOUSEEVENTF_MIDDLEUP) != 0 && send_mouse_button(BTN_MIDDLE, false) != 0) {
        return -1;
    }

    xbutton = mouse_data_to_xbutton(input->mouse_data);

    if ((input->flags & KSI_MOUSEEVENTF_XDOWN) != 0) {
        if (xbutton == 0 || send_mouse_button(xbutton, true) != 0) {
            return -1;
        }
    }

    if ((input->flags & KSI_MOUSEEVENTF_XUP) != 0) {
        if (xbutton == 0 || send_mouse_button(xbutton, false) != 0) {
            return -1;
        }
    }

    if (emit_sync() != 0) {
        fprintf(stderr, "inputd: failed to emit mouse input: %s\n", strerror(errno));
        return -1;
    }

    printf("inputd: synth mouse dx=%d dy=%d data=%u flags=0x%x\n",
        input->dx,
        input->dy,
        input->mouse_data,
        input->flags);

    return 0;
}

int ksi_linux_synth_send_input(const ksi_input *inputs, size_t count)
{
    if (uinput_fd < 0) {
        fprintf(stderr, "inputd: synthesis unavailable; %s could not be opened\n", KSI_UINPUT_PATH);
        return -1;
    }

    for (size_t i = 0; i < count; i++) {
        int result;

        if (inputs[i].type == KSI_INPUT_KEYBOARD) {
            result = send_keyboard_input(&inputs[i].data.keyboard);
        } else if (inputs[i].type == KSI_INPUT_MOUSE) {
            result = send_mouse_input(&inputs[i].data.mouse);
        } else {
            fprintf(stderr, "inputd: unsupported input type %u\n", inputs[i].type);
            result = -1;
        }

        if (result != 0) {
            return -1;
        }
    }

    return 0;
}

static int replay_keyboard_hook_event(const ksi_keyboard_hook_event *event)
{
    ksi_input input;

    memset(&input, 0, sizeof(input));
    input.type = KSI_INPUT_KEYBOARD;
    input.data.keyboard.vk = (uint16_t)event->vk_code;
    input.data.keyboard.scan = (uint16_t)event->scan_code;
    input.data.keyboard.flags = KSI_KEYEVENTF_SCANCODE;

    if ((event->flags & KSI_LLKHF_UP) != 0) {
        input.data.keyboard.flags |= KSI_KEYEVENTF_KEYUP;
    }

    return ksi_linux_synth_send_input(&input, 1);
}

static int replay_mouse_hook_event(const ksi_mouse_hook_event *event)
{
    ksi_input input;

    memset(&input, 0, sizeof(input));
    input.type = KSI_INPUT_MOUSE;

    switch (event->message) {
        case KSI_WM_MOUSEMOVE:
            input.data.mouse.flags = KSI_MOUSEEVENTF_MOVE;
            input.data.mouse.dx = event->x;
            input.data.mouse.dy = event->y;
            break;
        case KSI_WM_LBUTTONDOWN:
            input.data.mouse.flags = KSI_MOUSEEVENTF_LEFTDOWN;
            break;
        case KSI_WM_LBUTTONUP:
            input.data.mouse.flags = KSI_MOUSEEVENTF_LEFTUP;
            break;
        case KSI_WM_RBUTTONDOWN:
            input.data.mouse.flags = KSI_MOUSEEVENTF_RIGHTDOWN;
            break;
        case KSI_WM_RBUTTONUP:
            input.data.mouse.flags = KSI_MOUSEEVENTF_RIGHTUP;
            break;
        case KSI_WM_MBUTTONDOWN:
            input.data.mouse.flags = KSI_MOUSEEVENTF_MIDDLEDOWN;
            break;
        case KSI_WM_MBUTTONUP:
            input.data.mouse.flags = KSI_MOUSEEVENTF_MIDDLEUP;
            break;
        case KSI_WM_MOUSEWHEEL:
            input.data.mouse.flags = KSI_MOUSEEVENTF_WHEEL;
            input.data.mouse.mouse_data = (uint32_t)((int32_t)event->mouse_data >> 16);
            break;
        case KSI_WM_MOUSEHWHEEL:
            input.data.mouse.flags = KSI_MOUSEEVENTF_HWHEEL;
            input.data.mouse.mouse_data = (uint32_t)((int32_t)event->mouse_data >> 16);
            break;
        case KSI_WM_XBUTTONDOWN:
            input.data.mouse.flags = KSI_MOUSEEVENTF_XDOWN;
            input.data.mouse.mouse_data = event->mouse_data;
            break;
        case KSI_WM_XBUTTONUP:
            input.data.mouse.flags = KSI_MOUSEEVENTF_XUP;
            input.data.mouse.mouse_data = event->mouse_data;
            break;
        default:
            fprintf(stderr, "inputd: unsupported mouse hook replay message=0x%x\n", event->message);
            return -1;
    }

    return ksi_linux_synth_send_input(&input, 1);
}

int ksi_linux_synth_replay_hook_event(uint32_t hook_type, const ksi_hook_event_payload *event)
{
    bool old_suppress_replay_events;
    int result;

    if (event == NULL) {
        return -1;
    }

    old_suppress_replay_events = suppress_replay_events;
    suppress_replay_events = true;

    if (hook_type == KSI_HOOK_KEYBOARD_LL) {
        result = replay_keyboard_hook_event(&event->event.keyboard);
    } else if (hook_type == KSI_HOOK_MOUSE_LL) {
        result = replay_mouse_hook_event(&event->event.mouse);
    } else {
        result = -1;
    }

    suppress_replay_events = old_suppress_replay_events;
    return result;
}
