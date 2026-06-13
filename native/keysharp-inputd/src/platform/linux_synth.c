#include "keysharp_inputd/linux_synth.h"

#include "keysharp_inputd/globals.h"
#include "keysharp_inputd/linux_devices.h"
#include "vk_evdev.h"

#include <errno.h>
#include <fcntl.h>
#include <linux/input.h>
#include <linux/uinput.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/ioctl.h>
#include <time.h>
#include <unistd.h>

#define KSI_UINPUT_PATH "/dev/uinput"

/* Synthetic-output pacing.
 *
 * uinput gives the producer no backpressure: write() always succeeds, and when
 * a consumer's per-fd evdev ring fills (≈64 events on a typical keyboard) the
 * kernel discards queued events and raises SYN_DROPPED on *that consumer*. A
 * back-to-back burst (e.g. a long Send) therefore silently loses keystrokes —
 * the compositor never gets a chance to drain between our writes.
 *
 * Since the overflow cannot be observed from here, the only robust fix is to
 * keep the number of in-flight (un-drained) events safely below the ring and
 * briefly yield so the consumer's event loop runs. We count emitted events and
 * clock_nanosleep() once a chunk's worth has been written. The check runs after
 * every event (not per high-level input), so the chunk size IS the per-cycle
 * footprint — there is no extra overshoot from a single input expanding into a
 * burst (e.g. a Unicode char ≈32 events).
 *
 * Sizing: if the consumer misses exactly one of our yield windows, two chunks
 * can be in flight, so 2 * chunk must stay within the ~64-event minimum ring.
 * Hence 32. Short sends never reach the threshold and pay nothing, so the
 * common case runs at full speed. */
#define KSI_SYNTH_PACE_EVENTS 32
#define KSI_SYNTH_PACE_SLEEP_NS (700L * 1000L) /* 0.7 ms */

/* Relative mouse: keyboard keys + BTN_* + REL_X/Y/WHEEL. */
static int uinput_fd = -1;
/* Absolute pointer: ABS_X/Y with INPUT_PROP_POINTER for absolute MouseMove. */
static int uinput_abs_fd = -1;

static bool suppress_replay_events;
static bool synthesized_keys_down[KEY_MAX + 1];
static uint16_t pending_high_surrogate;
static uint64_t current_extra_info;
/* Pacing state, used only while a bulk client synthesis batch is being written
 * (see pacing note). Replays/passthrough are single events and never paced. */
static bool synth_pacing_active;
static unsigned synth_pace_events; /* events emitted since the last yield */

static int emit_event_to(int fd, uint16_t type, uint16_t code, int32_t value)
{
    struct input_event event;
    ssize_t nwritten;

    if (fd < 0) {
        return -1;
    }

    memset(&event, 0, sizeof(event));
    event.type = type;
    event.code = code;
    event.value = value;

    /* Record the suppression entry BEFORE writing so the main thread, which
     * reads evdev concurrently with this sequencer-thread write, can match
     * the loopback event to a recorded synthetic event and tag it INJECTED.
     * Under the old single-threaded daemon write-then-record was fine because
     * the same thread did both; under the lane refactor the main thread can
     * observe the loopback before write() returns to us. */
    ksi_linux_devices_record_synthetic_event(type, code, value, current_extra_info, suppress_replay_events);

    /* Retry on EINTR: a signal delivered during the write syscall returns -1
     * with errno=EINTR.  Without the retry the rest of a synthesis batch is
     * silently dropped, which manifests as only a few characters being typed. */
    do {
        nwritten = write(fd, &event, sizeof(event));
    } while (nwritten < 0 && errno == EINTR);

    if (nwritten != (ssize_t)sizeof(event)) {
        /* The write failed, so no event will loop back to consume this entry.
         * Leaving it in the ring risks a future real event with the same
         * {type, code, value} falsely matching and being suppressed.  Pop it
         * so the ring stays consistent with what's actually pending on the
         * input subsystem. */
        ksi_linux_devices_unrecord_last_synthetic_event(type, code, value);
        return -1;
    }

    /* Pace bulk synthesis against the consumer's finite evdev ring. Checked
     * after every emitted event so the chunk size is the exact per-cycle
     * footprint. Yielding mid-report (between a key and its SYN) is harmless:
     * the consumer applies nothing until the SYN, and we drop nothing. */
    if (synth_pacing_active && ++synth_pace_events >= KSI_SYNTH_PACE_EVENTS) {
        struct timespec pause = { 0, KSI_SYNTH_PACE_SLEEP_NS };
        (void)clock_nanosleep(CLOCK_MONOTONIC, 0, &pause, NULL);
        synth_pace_events = 0;
    }

    return 0;
}

static int emit_event(uint16_t type, uint16_t code, int32_t value)
{
    return emit_event_to(uinput_fd, type, code, value);
}

static int emit_abs_event(uint16_t code, int32_t value)
{
    return emit_event_to(uinput_abs_fd, EV_ABS, code, value);
}

static int emit_abs_sync(void)
{
    return emit_event_to(uinput_abs_fd, EV_SYN, SYN_REPORT, 0);
}

static int emit_sync(void)
{
    return emit_event(EV_SYN, SYN_REPORT, 0);
}

static int send_key_code(int key_code, int value)
{
    if (emit_event(EV_KEY, (uint16_t)key_code, value) != 0 || emit_sync() != 0) {
        return -1;
    }

    if (key_code >= 0 && key_code <= KEY_MAX) {
        synthesized_keys_down[key_code] = value != 0;
    }

    return 0;
}

static int send_key_stroke(int key_code)
{
    if (send_key_code(key_code, 1) != 0) {
        return -1;
    }

    return send_key_code(key_code, 0);
}


static int hex_digit_to_key(char digit)
{
    if (digit >= '0' && digit <= '9') {
        return digit == '0' ? KEY_0 : KEY_1 + (digit - '1');
    }

    if (digit >= 'a' && digit <= 'f') {
        switch (digit) {
            case 'a':
                return KEY_A;
            case 'b':
                return KEY_B;
            case 'c':
                return KEY_C;
            case 'd':
                return KEY_D;
            case 'e':
                return KEY_E;
            case 'f':
                return KEY_F;
        }
    }

    if (digit >= 'A' && digit <= 'F') {
        switch (digit) {
            case 'A':
                return KEY_A;
            case 'B':
                return KEY_B;
            case 'C':
                return KEY_C;
            case 'D':
                return KEY_D;
            case 'E':
                return KEY_E;
            case 'F':
                return KEY_F;
        }
    }

    return -1;
}

static int send_unicode_input(uint32_t codepoint)
{
    char hex[9];
    int length;
    int result = 0;

    if (codepoint == 0 || codepoint > 0x10FFFFu) {
        fprintf(stderr, "inputd: unsupported unicode codepoint U+%x\n", codepoint);
        return -1;
    }

    length = snprintf(hex, sizeof(hex), codepoint <= 0xFFFFu ? "%04x" : "%x", codepoint);

    if (length <= 0 || length >= (int)sizeof(hex)) {
        fprintf(stderr, "inputd: failed to format unicode codepoint U+%x\n", codepoint);
        return -1;
    }

    if (send_key_code(KEY_LEFTCTRL, 1) != 0
        || send_key_code(KEY_LEFTSHIFT, 1) != 0
        || send_key_code(KEY_U, 1) != 0) {
        result = -1;
    }

    if (send_key_code(KEY_U, 0) != 0) {
        result = -1;
    }

    if (send_key_code(KEY_LEFTSHIFT, 0) != 0) {
        result = -1;
    }

    if (send_key_code(KEY_LEFTCTRL, 0) != 0) {
        result = -1;
    }

    if (result != 0) {
        fprintf(stderr, "inputd: failed to start unicode input sequence U+%x: %s\n", codepoint, strerror(errno));
        return -1;
    }

    for (int i = 0; i < length; i++) {
        int key_code = hex_digit_to_key(hex[i]);

        if (key_code < 0 || send_key_stroke(key_code) != 0) {
            fprintf(stderr, "inputd: failed to emit unicode hex digit '%c' for U+%x: %s\n", hex[i], codepoint, strerror(errno));
            return -1;
        }
    }

    if (send_key_stroke(KEY_SPACE) != 0) {
        fprintf(stderr, "inputd: failed to commit unicode input U+%x: %s\n", codepoint, strerror(errno));
        return -1;
    }

    if (g_verbose) printf("inputd: synth unicode U+%x via ctrl+shift+u\n", codepoint);
    return 0;
}

static int send_unicode_utf16_unit(uint16_t unit)
{
    if (unit >= 0xD800u && unit <= 0xDBFFu) {
        pending_high_surrogate = unit;
        return 0;
    }

    if (unit >= 0xDC00u && unit <= 0xDFFFu) {
        uint16_t high = pending_high_surrogate;
        uint32_t codepoint;

        pending_high_surrogate = 0;

        if (high < 0xD800u || high > 0xDBFFu) {
            fprintf(stderr, "inputd: low unicode surrogate 0x%x without preceding high surrogate\n", unit);
            return -1;
        }

        codepoint = 0x10000u + ((((uint32_t)high - 0xD800u) << 10) | ((uint32_t)unit - 0xDC00u));
        return send_unicode_input(codepoint);
    }

    pending_high_surrogate = 0;
    return send_unicode_input(unit);
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
    return ksi_vk_to_evdev(vk);
}

static int scan_to_evdev_key(uint16_t scan, bool extended)
{
    if (scan == 0) {
        return -1;
    }

    if (extended) {
        /* Map Windows AT set-1 E0-prefixed scan codes to evdev keycodes.
         * These differ from the base (non-extended) numbering used by Linux. */
        switch (scan) {
            case 0x1Cu: return KEY_KPENTER;
            case 0x1Du: return KEY_RIGHTCTRL;
            case 0x35u: return KEY_KPSLASH;
            case 0x37u: return KEY_SYSRQ;
            case 0x38u: return KEY_RIGHTALT;
            case 0x47u: return KEY_HOME;
            case 0x48u: return KEY_UP;
            case 0x49u: return KEY_PAGEUP;
            case 0x4Bu: return KEY_LEFT;
            case 0x4Du: return KEY_RIGHT;
            case 0x4Fu: return KEY_END;
            case 0x50u: return KEY_DOWN;
            case 0x51u: return KEY_PAGEDOWN;
            case 0x52u: return KEY_INSERT;
            case 0x53u: return KEY_DELETE;
            case 0x5Bu: return KEY_LEFTMETA;
            case 0x5Cu: return KEY_RIGHTMETA;
            case 0x5Du: return KEY_COMPOSE;
            default: return -1;
        }
    }

    /* For non-extended PS/2 AT scan codes the numbering is identical to
     * Linux evdev keycodes.  This path also handles replayed hook events
     * where the scan field already contains a raw evdev keycode. */
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
        KEY_COMPOSE, KEY_SYSRQ, KEY_PAUSE, KEY_SLEEP,
        KEY_HENKAN, KEY_MUHENKAN, KEY_MODE,
        KEY_SELECT, KEY_PRINT, KEY_OPEN, KEY_HELP,
        KEY_CAPSLOCK, KEY_NUMLOCK, KEY_SCROLLLOCK,
        KEY_INSERT, KEY_DELETE, KEY_HOME, KEY_END, KEY_PAGEUP, KEY_PAGEDOWN,
        KEY_UP, KEY_DOWN, KEY_LEFT, KEY_RIGHT,
        KEY_MINUS, KEY_EQUAL, KEY_LEFTBRACE, KEY_RIGHTBRACE, KEY_BACKSLASH,
        KEY_SEMICOLON, KEY_APOSTROPHE, KEY_GRAVE, KEY_COMMA, KEY_DOT, KEY_SLASH,
        KEY_KP0, KEY_KP1, KEY_KP2, KEY_KP3, KEY_KP4,
        KEY_KP5, KEY_KP6, KEY_KP7, KEY_KP8, KEY_KP9,
        KEY_KPDOT, KEY_KPSLASH, KEY_KPASTERISK, KEY_KPMINUS, KEY_KPPLUS, KEY_KPCOMMA,
        KEY_KPENTER,
        KEY_F1, KEY_F2, KEY_F3, KEY_F4, KEY_F5, KEY_F6,
        KEY_F7, KEY_F8, KEY_F9, KEY_F10, KEY_F11, KEY_F12,
        KEY_F13, KEY_F14, KEY_F15, KEY_F16, KEY_F17, KEY_F18,
        KEY_F19, KEY_F20, KEY_F21, KEY_F22, KEY_F23, KEY_F24,
        KEY_BACK, KEY_FORWARD, KEY_REFRESH, KEY_STOP, KEY_SEARCH, KEY_FAVORITES,
        KEY_HOMEPAGE, KEY_MUTE, KEY_VOLUMEDOWN, KEY_VOLUMEUP, KEY_NEXTSONG,
        KEY_PREVIOUSSONG, KEY_STOPCD, KEY_PLAYPAUSE, KEY_EMAIL, KEY_MEDIA,
        KEY_PROG1, KEY_PROG2,
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

    /* Intentionally do not enable EV_REP. Keyboard synthesis emits only the
     * explicit down/up transitions requested by the caller. */
    if (enable_event(EV_KEY) != 0
        || enable_event(EV_REL) != 0) {
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

static int configure_abs_uinput_device(void)
{
    struct uinput_setup setup;
    struct uinput_abs_setup abs_setup;

    /* INPUT_PROP_POINTER: tells libinput this is an absolute pointer device
     * (like a drawing tablet in mouse mode), not a touchscreen or trackpad.
     * The compositor maps the ABS range [0, 65535] directly to screen area. */
    if (ioctl(uinput_abs_fd, UI_SET_PROPBIT, INPUT_PROP_POINTER) < 0) {
        fprintf(stderr, "inputd: UI_SET_PROPBIT(INPUT_PROP_POINTER) failed: %s\n", strerror(errno));
        /* Non-fatal: proceed without the property; the device may still work
         * under xf86-input-evdev even if libinput classifies it differently. */
    }

    if (ioctl(uinput_abs_fd, UI_SET_EVBIT, EV_ABS) < 0) {
        fprintf(stderr, "inputd: abs device UI_SET_EVBIT(EV_ABS) failed: %s\n", strerror(errno));
        return -1;
    }

    if (ioctl(uinput_abs_fd, UI_SET_EVBIT, EV_KEY) < 0) {
        fprintf(stderr, "inputd: abs device UI_SET_EVBIT(EV_KEY) failed: %s\n", strerror(errno));
        return -1;
    }

    /* Buttons live on the absolute device too so button+absolute-move events
     * arrive from a single device and can be correctly ordered. */
    if (ioctl(uinput_abs_fd, UI_SET_KEYBIT, BTN_LEFT) < 0
        || ioctl(uinput_abs_fd, UI_SET_KEYBIT, BTN_RIGHT) < 0
        || ioctl(uinput_abs_fd, UI_SET_KEYBIT, BTN_MIDDLE) < 0) {
        fprintf(stderr, "inputd: abs device UI_SET_KEYBIT failed: %s\n", strerror(errno));
        return -1;
    }

    if (ioctl(uinput_abs_fd, UI_SET_ABSBIT, ABS_X) < 0
        || ioctl(uinput_abs_fd, UI_SET_ABSBIT, ABS_Y) < 0) {
        fprintf(stderr, "inputd: abs device UI_SET_ABSBIT failed: %s\n", strerror(errno));
        return -1;
    }

    memset(&setup, 0, sizeof(setup));
    (void)snprintf(setup.name, sizeof(setup.name), "%s", KSI_SYNTH_ABS_DEVICE_NAME);
    setup.id.bustype = KSI_SYNTH_DEVICE_BUSTYPE;
    setup.id.vendor  = KSI_SYNTH_DEVICE_VENDOR;
    setup.id.product = KSI_SYNTH_ABS_DEVICE_PRODUCT;
    setup.id.version = KSI_SYNTH_DEVICE_VERSION;

    if (ioctl(uinput_abs_fd, UI_DEV_SETUP, &setup) < 0) {
        fprintf(stderr, "inputd: abs device UI_DEV_SETUP failed: %s\n", strerror(errno));
        return -1;
    }

    memset(&abs_setup, 0, sizeof(abs_setup));
    abs_setup.absinfo.minimum = 0;
    abs_setup.absinfo.maximum = 65535;
    abs_setup.absinfo.resolution = 1;

    abs_setup.code = ABS_X;
    if (ioctl(uinput_abs_fd, UI_ABS_SETUP, &abs_setup) < 0) {
        fprintf(stderr, "inputd: abs device UI_ABS_SETUP(ABS_X) failed: %s\n", strerror(errno));
        return -1;
    }

    abs_setup.code = ABS_Y;
    if (ioctl(uinput_abs_fd, UI_ABS_SETUP, &abs_setup) < 0) {
        fprintf(stderr, "inputd: abs device UI_ABS_SETUP(ABS_Y) failed: %s\n", strerror(errno));
        return -1;
    }

    if (ioctl(uinput_abs_fd, UI_DEV_CREATE) < 0) {
        fprintf(stderr, "inputd: abs device UI_DEV_CREATE failed: %s\n", strerror(errno));
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

    puts("inputd: uinput relative mouse device created");

    uinput_abs_fd = open(KSI_UINPUT_PATH, O_WRONLY | O_NONBLOCK);

    if (uinput_abs_fd < 0) {
        fprintf(stderr, "inputd: cannot open %s for abs device: %s\n", KSI_UINPUT_PATH, strerror(errno));
        /* Non-fatal: absolute mouse moves won't work but everything else will. */
        return 0;
    }

    if (configure_abs_uinput_device() != 0) {
        close(uinput_abs_fd);
        uinput_abs_fd = -1;
        fprintf(stderr, "inputd: failed to create absolute pointer device\n");
        return 0;
    }

    puts("inputd: uinput absolute pointer device created");
    return 0;
}

void ksi_linux_synth_stop(void)
{
    ksi_linux_synth_release_all();

    if (uinput_fd >= 0) {
        (void)ioctl(uinput_fd, UI_DEV_DESTROY);
        close(uinput_fd);
        uinput_fd = -1;
        puts("inputd: uinput relative mouse device destroyed");
    }

    if (uinput_abs_fd >= 0) {
        (void)ioctl(uinput_abs_fd, UI_DEV_DESTROY);
        close(uinput_abs_fd);
        uinput_abs_fd = -1;
        puts("inputd: uinput absolute pointer device destroyed");
    }
}

bool ksi_linux_synth_is_available(void)
{
    return uinput_fd >= 0;
}

static int send_keyboard_input(const ksi_keybdinput *input)
{
    int key_code = -1;
    int value = (input->flags & KSI_KEYEVENTF_KEYUP) != 0 ? 0 : 1;

    if ((input->flags & KSI_KEYEVENTF_UNICODE) != 0) {
        if ((input->flags & KSI_KEYEVENTF_KEYUP) != 0) {
            return 0;
        }

        return send_unicode_utf16_unit(input->scan);
    }

    if ((input->flags & KSI_KEYEVENTF_SCANCODE) != 0) {
        bool extended = (input->flags & KSI_KEYEVENTF_EXTENDEDKEY) != 0;
        key_code = scan_to_evdev_key(input->scan, extended);
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

    /* SendInput/keybd_event semantics are explicit transitions only. Do not
     * emit EV_KEY value 2: that marks autorepeat from a held physical key. */
    if (send_key_code(key_code, value) != 0) {
        fprintf(stderr, "inputd: failed to emit keyboard input: %s\n", strerror(errno));
        return -1;
    }

    if (g_verbose) {
        printf("inputd: synth key vk=0x%x scan=%u evdev=%d %s\n",
            input->vk,
            input->scan,
            key_code,
            value == 0 ? "up" : "down");
    }

    return 0;
}

void ksi_linux_synth_release_all(void)
{
    bool emitted = false;

    if (uinput_fd < 0) {
        memset(synthesized_keys_down, 0, sizeof(synthesized_keys_down));
        return;
    }

    for (int key_code = 0; key_code <= KEY_MAX; key_code++) {
        if (!synthesized_keys_down[key_code]) {
            continue;
        }

        if (emit_event(EV_KEY, (uint16_t)key_code, 0) == 0) {
            if (g_verbose) printf("inputd: release synthetic key evdev=%d\n", key_code);
            emitted = true;
        }

        synthesized_keys_down[key_code] = false;
    }

    if (emitted && emit_sync() != 0) {
        fprintf(stderr, "inputd: failed to sync synthetic key release: %s\n", strerror(errno));
    }
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
        if ((input->flags & KSI_MOUSEEVENTF_ABSOLUTE) != 0) {
            if (uinput_abs_fd < 0) {
                fprintf(stderr, "inputd: absolute mouse move dropped: abs device unavailable\n");
            } else if (emit_abs_event(ABS_X, input->dx) != 0
                       || emit_abs_event(ABS_Y, input->dy) != 0
                       || emit_abs_sync() != 0) {
                return -1;
            }
        } else {
            if (input->dx != 0 && emit_event(EV_REL, REL_X, input->dx) != 0) {
                return -1;
            }

            if (input->dy != 0 && emit_event(EV_REL, REL_Y, input->dy) != 0) {
                return -1;
            }
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

    if (g_verbose) {
        printf("inputd: synth mouse dx=%d dy=%d data=%u flags=0x%x\n",
            input->dx,
            input->dy,
            input->mouse_data,
            input->flags);
    }

    return 0;
}

int ksi_linux_synth_send_input(const ksi_input *inputs, size_t count, uint32_t flags)
{
    bool old_suppress;
    uint64_t old_extra_info;
    int result = 0;

    if (uinput_fd < 0) {
        fprintf(stderr, "inputd: synthesis unavailable; %s could not be opened\n", KSI_UINPUT_PATH);
        return -1;
    }

    /* When BYPASS_HOOK is set, suppress the evdev echo so the events don't
     * loop back to hook subscribers — the same mechanism used for PASS replays. */
    old_suppress = suppress_replay_events;
    old_extra_info = current_extra_info;

    if ((flags & KSI_SYNTH_FLAG_BYPASS_HOOK) != 0) {
        suppress_replay_events = true;
    }

    /* Enable per-event pacing for this batch (see emit_event_to). */
    synth_pacing_active = true;
    synth_pace_events = 0;

    for (size_t i = 0; i < count; i++) {
        if (inputs[i].type == KSI_INPUT_KEYBOARD) {
            current_extra_info = inputs[i].data.keyboard.extra_info;
            result = send_keyboard_input(&inputs[i].data.keyboard);
        } else if (inputs[i].type == KSI_INPUT_MOUSE) {
            current_extra_info = inputs[i].data.mouse.extra_info;
            result = send_mouse_input(&inputs[i].data.mouse);
        } else {
            fprintf(stderr, "inputd: unsupported input type %u\n", inputs[i].type);
            result = -1;
        }

        if (result != 0) {
            break;
        }
    }

    synth_pacing_active = false;
    suppress_replay_events = old_suppress;
    current_extra_info = old_extra_info;
    return result;
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

    return ksi_linux_synth_send_input(&input, 1, KSI_SYNTH_FLAG_BYPASS_HOOK);
}

static int replay_mouse_hook_event(const ksi_mouse_hook_event *event)
{
    ksi_input input;

    memset(&input, 0, sizeof(input));
    input.type = KSI_INPUT_MOUSE;

    switch (event->message) {
        case KSI_WM_MOUSEMOVE:
            input.data.mouse.flags = KSI_MOUSEEVENTF_MOVE;

            if ((event->mouse_data & KSI_MOUSEEVENTF_ABSOLUTE) != 0) {
                input.data.mouse.flags |= KSI_MOUSEEVENTF_ABSOLUTE;
            }

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

    return ksi_linux_synth_send_input(&input, 1, KSI_SYNTH_FLAG_BYPASS_HOOK);
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
