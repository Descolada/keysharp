#include "keysharp_inputd/linux_devices.h"

#include "keysharp_inputd/protocol.h"

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <linux/input.h>
#include <libevdev/libevdev.h>
#include <libudev.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

#define KSI_INPUT_DIR "/dev/input"
#define KSI_EVENT_PREFIX "event"
#define KSI_DEVICE_NAME_LENGTH 256
#define KSI_MAX_TRACKED_DEVICES 128
#define KSI_MAX_SUPPRESSED_REPLAY_EVENTS 256

#define KSI_BITS_PER_LONG (sizeof(unsigned long) * CHAR_BIT)
#define KSI_BIT_WORD(bit) ((bit) / KSI_BITS_PER_LONG)
#define KSI_BIT_MASK(bit) (1UL << ((bit) % KSI_BITS_PER_LONG))
#define KSI_BIT_ARRAY_LENGTH(max_bit) (((max_bit) / KSI_BITS_PER_LONG) + 1)

typedef struct ksi_linux_device_info {
    char path[PATH_MAX];
    char name[KSI_DEVICE_NAME_LENGTH];
    bool has_keys;
    bool has_relative;
    bool has_absolute;
    bool has_keyboard_keys;
    bool has_mouse_buttons;
    bool has_pointer_axes;
    uint16_t bustype;
    uint16_t vendor;
    uint16_t product;
    uint16_t version;
    bool is_synth_device;
} ksi_linux_device_info;

typedef struct ksi_suppressed_replay_event {
    uint16_t type;
    uint16_t code;
    int32_t value;
} ksi_suppressed_replay_event;

typedef struct ksi_linux_tracked_device {
    char path[PATH_MAX];
    char name[KSI_DEVICE_NAME_LENGTH];
    int fd;
    struct libevdev *evdev;
    uint32_t device_id;
    bool grabbed;
    bool keyboard_candidate;
    bool mouse_candidate;
    bool injected_source;
    bool has_pending_rel;
    int32_t pending_rel_x;
    int32_t pending_rel_y;
    uint64_t pending_rel_time_ms;
} ksi_linux_tracked_device;

static ksi_linux_tracked_device tracked_devices[KSI_MAX_TRACKED_DEVICES];
static ksi_suppressed_replay_event suppressed_replay_events[KSI_MAX_SUPPRESSED_REPLAY_EVENTS];
static size_t tracked_device_count;
static size_t suppressed_replay_event_count;
static struct udev *udev_context;
static struct udev_monitor *udev_monitor;
static uint32_t next_device_id = 1;
static ksi_hook_event_callback hook_event_callback;
static void *hook_event_context;
static bool grab_enabled;

static bool test_bit(const unsigned long *bits, int bit)
{
    return (bits[KSI_BIT_WORD((size_t)bit)] & KSI_BIT_MASK((size_t)bit)) != 0;
}

static bool has_any_key_range(const unsigned long *key_bits, int first, int last)
{
    for (int key = first; key <= last; key++) {
        if (test_bit(key_bits, key)) {
            return true;
        }
    }

    return false;
}

static bool looks_like_keyboard(const unsigned long *key_bits)
{
    return has_any_key_range(key_bits, KEY_A, KEY_Z)
        || has_any_key_range(key_bits, KEY_1, KEY_0)
        || test_bit(key_bits, KEY_ENTER)
        || test_bit(key_bits, KEY_SPACE)
        || test_bit(key_bits, KEY_LEFTCTRL)
        || test_bit(key_bits, KEY_RIGHTCTRL);
}

static bool looks_like_mouse_buttons(const unsigned long *key_bits)
{
    return test_bit(key_bits, BTN_LEFT)
        || test_bit(key_bits, BTN_RIGHT)
        || test_bit(key_bits, BTN_MIDDLE)
        || test_bit(key_bits, BTN_SIDE)
        || test_bit(key_bits, BTN_EXTRA);
}

static bool looks_like_pointer_axes(const unsigned long *rel_bits, const unsigned long *abs_bits)
{
    return (test_bit(rel_bits, REL_X) && test_bit(rel_bits, REL_Y))
        || (test_bit(abs_bits, ABS_X) && test_bit(abs_bits, ABS_Y));
}

static bool is_keysharp_synth_device_identity(
    const char *name,
    uint16_t bustype,
    uint16_t vendor,
    uint16_t product)
{
    if (name != NULL && strcmp(name, KSI_SYNTH_DEVICE_NAME) == 0) {
        return true;
    }

    return bustype == KSI_SYNTH_DEVICE_BUSTYPE
        && vendor == KSI_SYNTH_DEVICE_VENDOR
        && product == KSI_SYNTH_DEVICE_PRODUCT;
}

static void set_cloexec(int fd)
{
    int flags = fcntl(fd, F_GETFD);

    if (flags >= 0) {
        (void)fcntl(fd, F_SETFD, flags | FD_CLOEXEC);
    }
}

static int open_event_fd(const char *path)
{
    int fd = open(path, O_RDONLY | O_NONBLOCK);

    if (fd < 0) {
        fprintf(stderr, "inputd: cannot open %s: %s\n", path, strerror(errno));
        return -1;
    }

    set_cloexec(fd);
    return fd;
}

static int read_device_info(const char *path, ksi_linux_device_info *info)
{
    unsigned long event_bits[KSI_BIT_ARRAY_LENGTH(EV_MAX)];
    unsigned long key_bits[KSI_BIT_ARRAY_LENGTH(KEY_MAX)];
    unsigned long rel_bits[KSI_BIT_ARRAY_LENGTH(REL_MAX)];
    unsigned long abs_bits[KSI_BIT_ARRAY_LENGTH(ABS_MAX)];
    struct input_id input_id;
    int fd;

    memset(info, 0, sizeof(*info));
    memset(event_bits, 0, sizeof(event_bits));
    memset(key_bits, 0, sizeof(key_bits));
    memset(rel_bits, 0, sizeof(rel_bits));
    memset(abs_bits, 0, sizeof(abs_bits));

    (void)snprintf(info->path, sizeof(info->path), "%s", path);

    fd = open_event_fd(path);

    if (fd < 0) {
        return -1;
    }

    if (ioctl(fd, EVIOCGNAME(sizeof(info->name)), info->name) < 0) {
        (void)snprintf(info->name, sizeof(info->name), "unknown");
    }

    memset(&input_id, 0, sizeof(input_id));

    if (ioctl(fd, EVIOCGID, &input_id) == 0) {
        info->bustype = input_id.bustype;
        info->vendor = input_id.vendor;
        info->product = input_id.product;
        info->version = input_id.version;
    }

    if (ioctl(fd, EVIOCGBIT(0, sizeof(event_bits)), event_bits) < 0) {
        fprintf(stderr, "inputd: cannot read capabilities for %s: %s\n", path, strerror(errno));
        close(fd);
        return -1;
    }

    info->has_keys = test_bit(event_bits, EV_KEY);
    info->has_relative = test_bit(event_bits, EV_REL);
    info->has_absolute = test_bit(event_bits, EV_ABS);

    if (info->has_keys && ioctl(fd, EVIOCGBIT(EV_KEY, sizeof(key_bits)), key_bits) < 0) {
        fprintf(stderr, "inputd: cannot read key capabilities for %s: %s\n", path, strerror(errno));
        close(fd);
        return -1;
    }

    if (info->has_relative && ioctl(fd, EVIOCGBIT(EV_REL, sizeof(rel_bits)), rel_bits) < 0) {
        fprintf(stderr, "inputd: cannot read relative-axis capabilities for %s: %s\n", path, strerror(errno));
        close(fd);
        return -1;
    }

    if (info->has_absolute && ioctl(fd, EVIOCGBIT(EV_ABS, sizeof(abs_bits)), abs_bits) < 0) {
        fprintf(stderr, "inputd: cannot read absolute-axis capabilities for %s: %s\n", path, strerror(errno));
        close(fd);
        return -1;
    }

    info->has_keyboard_keys = info->has_keys && looks_like_keyboard(key_bits);
    info->has_mouse_buttons = info->has_keys && looks_like_mouse_buttons(key_bits);
    info->has_pointer_axes = looks_like_pointer_axes(rel_bits, abs_bits);
    info->is_synth_device = is_keysharp_synth_device_identity(
        info->name,
        info->bustype,
        info->vendor,
        info->product);

    close(fd);
    return 0;
}

static bool is_event_device_name(const char *name)
{
    return strncmp(name, KSI_EVENT_PREFIX, strlen(KSI_EVENT_PREFIX)) == 0;
}

static bool is_event_device_path(const char *path)
{
    const char *last_slash;

    if (path == NULL) {
        return false;
    }

    last_slash = strrchr(path, '/');
    return last_slash != NULL && is_event_device_name(last_slash + 1);
}

static bool is_candidate(const ksi_linux_device_info *info)
{
    return info->has_keyboard_keys || (info->has_mouse_buttons && info->has_pointer_axes);
}

static bool is_keyboard_candidate(const ksi_linux_device_info *info)
{
    return info->has_keyboard_keys;
}

static bool is_mouse_candidate(const ksi_linux_device_info *info)
{
    return info->has_mouse_buttons && info->has_pointer_axes;
}

static void log_device(const ksi_linux_device_info *info, const char *prefix)
{
    printf("inputd: %s %s: \"%s\" candidate=%s%s%s%s%s%s\n",
        prefix,
        info->path,
        info->name,
        is_keyboard_candidate(info) ? "keyboard" : "",
        is_keyboard_candidate(info) && is_mouse_candidate(info) ? "," : "",
        is_mouse_candidate(info) ? "mouse" : "",
        info->has_relative ? " rel" : "",
        info->has_absolute ? " abs" : "",
        info->is_synth_device ? " injected" : "");
}

static ssize_t find_tracked_device(const char *path)
{
    for (size_t i = 0; i < tracked_device_count; i++) {
        if (strcmp(tracked_devices[i].path, path) == 0) {
            return (ssize_t)i;
        }
    }

    return -1;
}

static ssize_t find_tracked_device_by_fd(int fd)
{
    for (size_t i = 0; i < tracked_device_count; i++) {
        if (tracked_devices[i].fd == fd) {
            return (ssize_t)i;
        }
    }

    return -1;
}

static void close_tracked_device(ksi_linux_tracked_device *device)
{
    if (device->grabbed && device->fd >= 0) {
        (void)ioctl(device->fd, EVIOCGRAB, 0);
        device->grabbed = false;
    }

    if (device->evdev != NULL) {
        libevdev_free(device->evdev);
        device->evdev = NULL;
    }

    if (device->fd >= 0) {
        close(device->fd);
        device->fd = -1;
    }
}

static int open_tracked_device(ksi_linux_tracked_device *device)
{
    int fd;
    int result;

    close_tracked_device(device);

    fd = open_event_fd(device->path);

    if (fd < 0) {
        return -1;
    }

    result = libevdev_new_from_fd(fd, &device->evdev);

    if (result < 0) {
        fprintf(stderr, "inputd: cannot initialize libevdev for %s: %s\n",
            device->path,
            strerror(-result));
        close(fd);
        device->evdev = NULL;
        return -1;
    }

    device->fd = fd;
    return 0;
}

static int set_device_grab(ksi_linux_tracked_device *device, bool enabled)
{
    int value = enabled ? 1 : 0;

    if (device->fd < 0 || device->grabbed == enabled) {
        return 0;
    }

    if (device->injected_source) {
        return 0;
    }

    if (ioctl(device->fd, EVIOCGRAB, value) != 0) {
        fprintf(stderr,
            "inputd: EVIOCGRAB(%s) failed for %s: %s\n",
            enabled ? "on" : "off",
            device->path,
            strerror(errno));
        return -1;
    }

    device->grabbed = enabled;
    printf("inputd: %s %s\n", enabled ? "grabbed" : "ungrabbed", device->path);
    return 0;
}

static void track_device(const ksi_linux_device_info *info, const char *reason)
{
    ssize_t existing_index = find_tracked_device(info->path);
    ksi_linux_tracked_device *target;
    bool is_new = existing_index < 0;

    if (!is_candidate(info)) {
        return;
    }

    if (existing_index >= 0) {
        target = &tracked_devices[existing_index];
    } else {
        if (tracked_device_count >= KSI_MAX_TRACKED_DEVICES) {
            fprintf(stderr, "inputd: cannot track %s: device table is full\n", info->path);
            return;
        }

        target = &tracked_devices[tracked_device_count++];
        memset(target, 0, sizeof(*target));
        target->fd = -1;
        target->device_id = next_device_id++;
    }

    (void)snprintf(target->path, sizeof(target->path), "%s", info->path);
    (void)snprintf(target->name, sizeof(target->name), "%s", info->name);
    target->keyboard_candidate = is_keyboard_candidate(info);
    target->mouse_candidate = is_mouse_candidate(info);
    target->injected_source = info->is_synth_device;

    if (is_new || target->fd < 0) {
        if (open_tracked_device(target) != 0) {
            return;
        }
    }

    if (grab_enabled && !target->injected_source) {
        (void)set_device_grab(target, true);
    }

    log_device(info, reason);
}

static void untrack_device(const char *path)
{
    ssize_t existing_index = find_tracked_device(path);
    size_t index;

    if (existing_index < 0) {
        return;
    }

    index = (size_t)existing_index;

    printf("inputd: remove %s: \"%s\"\n",
        tracked_devices[index].path,
        tracked_devices[index].name);

    close_tracked_device(&tracked_devices[index]);

    for (size_t i = index; i + 1 < tracked_device_count; i++) {
        tracked_devices[i] = tracked_devices[i + 1];
    }

    tracked_device_count--;
}

static void scan_existing_devices(void)
{
    DIR *dir;
    struct dirent *entry;
    int devices_seen = 0;
    int candidates_seen = 0;

    dir = opendir(KSI_INPUT_DIR);

    if (dir == NULL) {
        fprintf(stderr, "inputd: cannot open %s: %s\n", KSI_INPUT_DIR, strerror(errno));
        return;
    }

    while ((entry = readdir(dir)) != NULL) {
        char path[PATH_MAX];
        ksi_linux_device_info info;
        bool candidate;

        if (!is_event_device_name(entry->d_name)) {
            continue;
        }

        if (snprintf(path, sizeof(path), "%s/%s", KSI_INPUT_DIR, entry->d_name) >= (int)sizeof(path)) {
            fprintf(stderr, "inputd: skipping too-long input path for %s\n", entry->d_name);
            continue;
        }

        devices_seen++;

        if (read_device_info(path, &info) != 0) {
            continue;
        }

        candidate = is_candidate(&info);

        if (candidate) {
            candidates_seen++;
            track_device(&info, "existing");
        }
    }

    closedir(dir);

    printf("inputd: scanned %d event devices, found %d keyboard/mouse candidates\n",
        devices_seen,
        candidates_seen);
}

static int start_udev_monitor(void)
{
    udev_context = udev_new();

    if (udev_context == NULL) {
        fprintf(stderr, "inputd: failed to create udev context\n");
        return -1;
    }

    udev_monitor = udev_monitor_new_from_netlink(udev_context, "udev");

    if (udev_monitor == NULL) {
        fprintf(stderr, "inputd: failed to create udev monitor\n");
        return -1;
    }

    if (udev_monitor_filter_add_match_subsystem_devtype(
            udev_monitor,
            "input",
            NULL) < 0) {
        fprintf(stderr, "inputd: failed to install udev input filter\n");
        return -1;
    }

    if (udev_monitor_enable_receiving(udev_monitor) < 0) {
        fprintf(stderr, "inputd: failed to enable udev monitor\n");
        return -1;
    }

    printf("inputd: udev hotplug monitor enabled\n");
    return 0;
}

static void handle_device_add_or_change(const char *path, const char *action)
{
    ksi_linux_device_info info;

    if (read_device_info(path, &info) != 0) {
        return;
    }

    if (!is_candidate(&info)) {
        printf("inputd: %s %s ignored: not a keyboard/mouse candidate\n", action, path);
        return;
    }

    track_device(&info, action);
}

int ksi_linux_devices_start(void)
{
    tracked_device_count = 0;
    suppressed_replay_event_count = 0;
    grab_enabled = false;
    scan_existing_devices();

    if (start_udev_monitor() != 0) {
        ksi_linux_devices_stop();
        return 0;
    }

    return 0;
}

void ksi_linux_devices_stop(void)
{
    for (size_t i = 0; i < tracked_device_count; i++) {
        close_tracked_device(&tracked_devices[i]);
    }

    if (udev_monitor != NULL) {
        udev_monitor_unref(udev_monitor);
        udev_monitor = NULL;
    }

    if (udev_context != NULL) {
        udev_unref(udev_context);
        udev_context = NULL;
    }

    tracked_device_count = 0;
    suppressed_replay_event_count = 0;
}

int ksi_linux_devices_set_grab_enabled(bool enabled)
{
    int result = 0;

    if (grab_enabled == enabled) {
        return 0;
    }

    for (size_t i = 0; i < tracked_device_count; i++) {
        if (set_device_grab(&tracked_devices[i], enabled) != 0) {
            result = -1;
        }
    }

    if (result != 0 && enabled) {
        for (size_t i = 0; i < tracked_device_count; i++) {
            (void)set_device_grab(&tracked_devices[i], false);
        }
    }

    if (result == 0) {
        grab_enabled = enabled;
    }

    return result;
}

void ksi_linux_devices_suppress_next_replay_event(uint16_t type, uint16_t code, int32_t value)
{
    ksi_suppressed_replay_event *entry;

    if (type == EV_SYN) {
        return;
    }

    if (suppressed_replay_event_count >= KSI_MAX_SUPPRESSED_REPLAY_EVENTS) {
        memmove(
            suppressed_replay_events,
            suppressed_replay_events + 1,
            (KSI_MAX_SUPPRESSED_REPLAY_EVENTS - 1) * sizeof(suppressed_replay_events[0]));
        suppressed_replay_event_count = KSI_MAX_SUPPRESSED_REPLAY_EVENTS - 1;
    }

    entry = &suppressed_replay_events[suppressed_replay_event_count++];
    entry->type = type;
    entry->code = code;
    entry->value = value;
}

static bool consume_suppressed_replay_event(const struct input_event *event)
{
    for (size_t i = 0; i < suppressed_replay_event_count; i++) {
        if (suppressed_replay_events[i].type == event->type
            && suppressed_replay_events[i].code == event->code
            && suppressed_replay_events[i].value == event->value) {
            for (size_t j = i; j + 1 < suppressed_replay_event_count; j++) {
                suppressed_replay_events[j] = suppressed_replay_events[j + 1];
            }

            suppressed_replay_event_count--;
            return true;
        }
    }

    return false;
}

static nfds_t add_poll_fd(struct pollfd *fds, nfds_t max_fds, nfds_t count, int fd)
{
    if (fd < 0 || count >= max_fds) {
        return count;
    }

    fds[count].fd = fd;
    fds[count].events = POLLIN;
    return count + 1;
}

nfds_t ksi_linux_devices_poll_fds(struct pollfd *fds, nfds_t max_fds)
{
    nfds_t count = 0;

    if (udev_monitor != NULL) {
        count = add_poll_fd(fds, max_fds, count, udev_monitor_get_fd(udev_monitor));
    }

    for (size_t i = 0; i < tracked_device_count; i++) {
        count = add_poll_fd(fds, max_fds, count, tracked_devices[i].fd);
    }

    return count;
}

static uint64_t event_time_ms(const struct input_event *event)
{
    uint64_t seconds = (uint64_t)event->input_event_sec;
    uint64_t useconds = (uint64_t)event->input_event_usec;
    return (seconds * 1000u) + (useconds / 1000u);
}

static uint32_t evdev_key_to_vk(unsigned int code)
{
    if (code >= KEY_A && code <= KEY_Z) {
        return 0x41u + (uint32_t)(code - KEY_A);
    }

    if (code >= KEY_1 && code <= KEY_9) {
        return 0x31u + (uint32_t)(code - KEY_1);
    }

    switch (code) {
        case KEY_0:
            return 0x30u;
        case KEY_ESC:
            return 0x1Bu;
        case KEY_BACKSPACE:
            return 0x08u;
        case KEY_TAB:
            return 0x09u;
        case KEY_ENTER:
        case KEY_KPENTER:
            return 0x0Du;
        case KEY_LEFTCTRL:
            return 0xA2u;
        case KEY_RIGHTCTRL:
            return 0xA3u;
        case KEY_LEFTSHIFT:
            return 0xA0u;
        case KEY_RIGHTSHIFT:
            return 0xA1u;
        case KEY_LEFTALT:
            return 0xA4u;
        case KEY_RIGHTALT:
            return 0xA5u;
        case KEY_LEFTMETA:
            return 0x5Bu;
        case KEY_RIGHTMETA:
            return 0x5Cu;
        case KEY_COMPOSE:
            return 0x5Du;
        case KEY_SPACE:
            return 0x20u;
        case KEY_SYSRQ:
            return 0x2Cu;
        case KEY_PAUSE:
            return 0x13u;
        case KEY_CAPSLOCK:
            return 0x14u;
        case KEY_NUMLOCK:
            return 0x90u;
        case KEY_SCROLLLOCK:
            return 0x91u;
        case KEY_INSERT:
            return 0x2Du;
        case KEY_DELETE:
            return 0x2Eu;
        case KEY_HOME:
            return 0x24u;
        case KEY_END:
            return 0x23u;
        case KEY_PAGEUP:
            return 0x21u;
        case KEY_PAGEDOWN:
            return 0x22u;
        case KEY_UP:
            return 0x26u;
        case KEY_DOWN:
            return 0x28u;
        case KEY_LEFT:
            return 0x25u;
        case KEY_RIGHT:
            return 0x27u;
        case KEY_KP0:
        case KEY_KP1:
        case KEY_KP2:
        case KEY_KP3:
        case KEY_KP4:
        case KEY_KP5:
        case KEY_KP6:
        case KEY_KP7:
        case KEY_KP8:
        case KEY_KP9:
            return 0x60u + (uint32_t)(code - KEY_KP0);
        case KEY_KPASTERISK:
            return 0x6Au;
        case KEY_KPPLUS:
            return 0x6Bu;
        case KEY_KPMINUS:
            return 0x6Du;
        case KEY_KPDOT:
            return 0x6Eu;
        case KEY_KPSLASH:
            return 0x6Fu;
        case KEY_F1:
        case KEY_F2:
        case KEY_F3:
        case KEY_F4:
        case KEY_F5:
        case KEY_F6:
        case KEY_F7:
        case KEY_F8:
        case KEY_F9:
        case KEY_F10:
        case KEY_F11:
        case KEY_F12:
            return 0x70u + (uint32_t)(code - KEY_F1);
        case KEY_F13:
        case KEY_F14:
        case KEY_F15:
        case KEY_F16:
        case KEY_F17:
        case KEY_F18:
        case KEY_F19:
        case KEY_F20:
        case KEY_F21:
        case KEY_F22:
        case KEY_F23:
        case KEY_F24:
            return 0x7Cu + (uint32_t)(code - KEY_F13);
        case KEY_SEMICOLON:
            return 0xBAu;
        case KEY_EQUAL:
            return 0xBBu;
        case KEY_COMMA:
            return 0xBCu;
        case KEY_MINUS:
            return 0xBDu;
        case KEY_DOT:
            return 0xBEu;
        case KEY_SLASH:
            return 0xBFu;
        case KEY_GRAVE:
            return 0xC0u;
        case KEY_LEFTBRACE:
            return 0xDBu;
        case KEY_BACKSLASH:
            return 0xDCu;
        case KEY_RIGHTBRACE:
            return 0xDDu;
        case KEY_APOSTROPHE:
            return 0xDEu;
        default:
            return 0u;
    }
}

static uint32_t evdev_key_to_message(const struct input_event *event)
{
    if (event->value == 0) {
        return KSI_WM_KEYUP;
    }

    return KSI_WM_KEYDOWN;
}

static uint32_t evdev_key_to_flags(const struct input_event *event)
{
    uint32_t flags = 0;

    switch (event->code) {
        case KEY_RIGHTCTRL:
        case KEY_RIGHTALT:
        case KEY_INSERT:
        case KEY_DELETE:
        case KEY_HOME:
        case KEY_END:
        case KEY_PAGEUP:
        case KEY_PAGEDOWN:
        case KEY_UP:
        case KEY_DOWN:
        case KEY_LEFT:
        case KEY_RIGHT:
        case KEY_KPSLASH:
        case KEY_KPENTER:
            flags |= KSI_LLKHF_EXTENDED;
            break;
        default:
            break;
    }

    if (event->value == 0) {
        flags |= KSI_LLKHF_UP;
    }

    return flags;
}

static uint32_t keyboard_injected_flags(const ksi_linux_tracked_device *device)
{
    return device->injected_source ? KSI_LLKHF_INJECTED : 0u;
}

static uint32_t mouse_injected_flags(const ksi_linux_tracked_device *device)
{
    return device->injected_source ? KSI_LLMHF_INJECTED : 0u;
}

static bool evdev_button_to_mouse_message(unsigned int code, int value, uint32_t *message)
{
    if (value != 0 && value != 1) {
        return false;
    }

    switch (code) {
        case BTN_LEFT:
            *message = value != 0 ? KSI_WM_LBUTTONDOWN : KSI_WM_LBUTTONUP;
            return true;
        case BTN_RIGHT:
            *message = value != 0 ? KSI_WM_RBUTTONDOWN : KSI_WM_RBUTTONUP;
            return true;
        case BTN_MIDDLE:
            *message = value != 0 ? KSI_WM_MBUTTONDOWN : KSI_WM_MBUTTONUP;
            return true;
        case BTN_SIDE:
        case BTN_EXTRA:
            *message = value != 0 ? KSI_WM_XBUTTONDOWN : KSI_WM_XBUTTONUP;
            return true;
        default:
            return false;
    }
}

static uint32_t evdev_button_mouse_data(unsigned int code)
{
    if (code == BTN_SIDE) {
        return KSI_XBUTTON1 << 16;
    }

    if (code == BTN_EXTRA) {
        return KSI_XBUTTON2 << 16;
    }

    return 0u;
}

static void dispatch_keyboard_event(const ksi_linux_tracked_device *device, const struct input_event *event)
{
    ksi_keyboard_hook_event hook_event = {
        .message = evdev_key_to_message(event),
        .vk_code = evdev_key_to_vk((unsigned int)event->code),
        .scan_code = (uint32_t)event->code,
        .flags = evdev_key_to_flags(event) | keyboard_injected_flags(device),
        .time_ms = event_time_ms(event),
        .extra_info = 0,
        .device_id = device->device_id,
        .native_code = (uint32_t)event->code,
    };

    printf("inputd: key %s vk=0x%02x scan=%u native=%u value=%d flags=0x%x time=%llu device=\"%s\"\n",
        hook_event.message == KSI_WM_KEYUP ? "up" : "down",
        hook_event.vk_code,
        hook_event.scan_code,
        hook_event.native_code,
        event->value,
        hook_event.flags,
        (unsigned long long)hook_event.time_ms,
        device->name);

    if (hook_event_callback != NULL) {
        hook_event_callback(
            hook_event_context,
            KSI_HOOK_KEYBOARD_LL,
            &hook_event,
            sizeof(hook_event));
    }
}

static void dispatch_mouse_button_event(const ksi_linux_tracked_device *device, const struct input_event *event)
{
    uint32_t message;

    if (!evdev_button_to_mouse_message((unsigned int)event->code, event->value, &message)) {
        return;
    }

    ksi_mouse_hook_event hook_event = {
        .message = message,
        .x = 0,
        .y = 0,
        .mouse_data = evdev_button_mouse_data((unsigned int)event->code),
        .flags = mouse_injected_flags(device),
        .time_ms = event_time_ms(event),
        .extra_info = 0,
        .device_id = device->device_id,
        .native_code = (uint32_t)event->code,
    };

    printf("inputd: mouse button message=0x%x data=0x%x native=%u time=%llu device=\"%s\"\n",
        hook_event.message,
        hook_event.mouse_data,
        hook_event.native_code,
        (unsigned long long)hook_event.time_ms,
        device->name);

    if (hook_event_callback != NULL) {
        hook_event_callback(
            hook_event_context,
            KSI_HOOK_MOUSE_LL,
            &hook_event,
            sizeof(hook_event));
    }
}

static void dispatch_pending_mouse_move(ksi_linux_tracked_device *device)
{
    ksi_mouse_hook_event hook_event;

    if (!device->has_pending_rel) {
        return;
    }

    memset(&hook_event, 0, sizeof(hook_event));
    hook_event.message = KSI_WM_MOUSEMOVE;
    hook_event.x = device->pending_rel_x;
    hook_event.y = device->pending_rel_y;
    hook_event.flags = mouse_injected_flags(device);
    hook_event.time_ms = device->pending_rel_time_ms;
    hook_event.device_id = device->device_id;

    printf("inputd: mouse move dx=%d dy=%d time=%llu device=\"%s\"\n",
        hook_event.x,
        hook_event.y,
        (unsigned long long)hook_event.time_ms,
        device->name);

    device->has_pending_rel = false;
    device->pending_rel_x = 0;
    device->pending_rel_y = 0;
    device->pending_rel_time_ms = 0;

    if (hook_event_callback != NULL) {
        hook_event_callback(
            hook_event_context,
            KSI_HOOK_MOUSE_LL,
            &hook_event,
            sizeof(hook_event));
    }
}

static void queue_relative_motion(ksi_linux_tracked_device *device, const struct input_event *event)
{
    if (event->code == REL_X) {
        device->pending_rel_x += event->value;
    } else {
        device->pending_rel_y += event->value;
    }

    device->pending_rel_time_ms = event_time_ms(event);
    device->has_pending_rel = true;
}

static void dispatch_relative_event(ksi_linux_tracked_device *device, const struct input_event *event)
{
    if (event->code == REL_X || event->code == REL_Y) {
        queue_relative_motion(device, event);
        return;
    }

    if (event->code == REL_WHEEL || event->code == REL_HWHEEL) {
        ksi_mouse_hook_event hook_event = {
            .message = event->code == REL_WHEEL ? KSI_WM_MOUSEWHEEL : KSI_WM_MOUSEHWHEEL,
            .x = 0,
            .y = 0,
            .mouse_data = (uint32_t)((int32_t)event->value * 120) << 16,
            .flags = mouse_injected_flags(device),
            .time_ms = event_time_ms(event),
            .extra_info = 0,
            .device_id = device->device_id,
            .native_code = (uint32_t)event->code,
        };

        dispatch_pending_mouse_move(device);

        printf("inputd: mouse wheel message=0x%x delta=%d native=%u time=%llu device=\"%s\"\n",
            hook_event.message,
            event->value * 120,
            hook_event.native_code,
            (unsigned long long)hook_event.time_ms,
            device->name);

        if (hook_event_callback != NULL) {
            hook_event_callback(
                hook_event_context,
                KSI_HOOK_MOUSE_LL,
                &hook_event,
                sizeof(hook_event));
        }
    }
}

static void handle_input_event(ksi_linux_tracked_device *device, const struct input_event *event)
{
    if (device->injected_source && consume_suppressed_replay_event(event)) {
        return;
    }

    if (event->type == EV_SYN) {
        if (event->code == SYN_REPORT) {
            dispatch_pending_mouse_move(device);
        }
    } else if (event->type == EV_KEY) {
        dispatch_pending_mouse_move(device);

        if (event->code >= BTN_MOUSE) {
            dispatch_mouse_button_event(device, event);
        } else {
            dispatch_keyboard_event(device, event);
        }
    } else if (event->type == EV_REL) {
        dispatch_relative_event(device, event);
    }
}

void ksi_linux_devices_set_hook_event_callback(ksi_hook_event_callback callback, void *context)
{
    hook_event_callback = callback;
    hook_event_context = context;
}

static void process_device_events(ksi_linux_tracked_device *device)
{
    for (;;) {
        struct input_event event;
        int result;

        if (device->evdev == NULL) {
            return;
        }

        result = libevdev_next_event(device->evdev, LIBEVDEV_READ_FLAG_NORMAL, &event);

        if (result == 0) {
            handle_input_event(device, &event);
            continue;
        }

        if (result == -EAGAIN) {
            dispatch_pending_mouse_move(device);
            return;
        }

        if (result == LIBEVDEV_READ_STATUS_SYNC) {
            continue;
        }

        fprintf(stderr, "inputd: failed reading %s: %s\n",
            device->path,
            strerror(-result));
        close_tracked_device(device);
        return;
    }
}

static void process_udev_events(void)
{
    for (;;) {
        struct udev_device *device;
        const char *action;
        const char *devnode;

        if (udev_monitor == NULL) {
            return;
        }

        device = udev_monitor_receive_device(udev_monitor);

        if (device == NULL) {
            return;
        }

        action = udev_device_get_action(device);
        devnode = udev_device_get_devnode(device);

        if (devnode != NULL && is_event_device_path(devnode)) {
            if (action != NULL && strcmp(action, "remove") == 0) {
                untrack_device(devnode);
            } else if (action != NULL && (strcmp(action, "add") == 0 || strcmp(action, "change") == 0)) {
                handle_device_add_or_change(devnode, action);
            }
        }

        udev_device_unref(device);
    }
}

void ksi_linux_devices_process_fd(int fd)
{
    ssize_t device_index;

    if (udev_monitor != NULL && fd == udev_monitor_get_fd(udev_monitor)) {
        process_udev_events();
        return;
    }

    device_index = find_tracked_device_by_fd(fd);

    if (device_index >= 0) {
        process_device_events(&tracked_devices[device_index]);
    }
}
