#include <errno.h>
#include <fcntl.h>
#include <linux/input-event-codes.h>
#include <linux/uinput.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/time.h>
#include <unistd.h>

#define DEVICE_NAME "Keysharp Physical-Test Source"

static int emit(int fd, unsigned short type, unsigned short code, int value)
{
    struct input_event event;

    memset(&event, 0, sizeof(event));
    event.type = type;
    event.code = code;
    event.value = value;
    return write(fd, &event, sizeof(event)) == (ssize_t)sizeof(event) ? 0 : -1;
}

static int emit_key(int fd, unsigned short code)
{
    return emit(fd, EV_KEY, code, 1) || emit(fd, EV_SYN, SYN_REPORT, 0)
        || emit(fd, EV_KEY, code, 0) || emit(fd, EV_SYN, SYN_REPORT, 0);
}

static int parse_key(const char *text)
{
    static const int keys[] = { KEY_F13, KEY_F14, KEY_F15, KEY_F16, KEY_F17, KEY_F18 };
    char *end;
    long number = strtol(text, &end, 10);

    if (*text == '\0' || *end != '\0' || number < 13 || number > 18) {
        return -1;
    }

    return keys[number - 13];
}

int main(int argc, char **argv)
{
    struct uinput_setup setup;
    const char *acknowledgement = getenv("KEYSHARP_INPUTD_PHYSICAL_TEST");
    int fd;
    int result = 0;

    if (argc < 2 || acknowledgement == NULL || strcmp(acknowledgement, "I_UNDERSTAND") != 0) {
        fprintf(stderr,
            "Usage: KEYSHARP_INPUTD_PHYSICAL_TEST=I_UNDERSTAND %s FKEY [FKEY ...]\n"
            "FKEY must be 13..18. This privileged manual-test tool creates a virtual\n"
            "keyboard which the daemon intentionally treats as a physical source.\n",
            argv[0]);
        return 2;
    }

    fd = open("/dev/uinput", O_WRONLY | O_CLOEXEC);
    if (fd < 0) {
        fprintf(stderr, "cannot open /dev/uinput: %s (input privileges are required)\n", strerror(errno));
        return 1;
    }

    if (ioctl(fd, UI_SET_EVBIT, EV_KEY) < 0 || ioctl(fd, UI_SET_EVBIT, EV_SYN) < 0) {
        perror("configuring uinput event types");
        close(fd);
        return 1;
    }

    for (int key = KEY_F13; key <= KEY_F18; key++) {
        if (ioctl(fd, UI_SET_KEYBIT, key) < 0) {
            perror("configuring uinput keys");
            close(fd);
            return 1;
        }
    }

    memset(&setup, 0, sizeof(setup));
    snprintf(setup.name, sizeof(setup.name), "%s", DEVICE_NAME);
    setup.id.bustype = BUS_VIRTUAL;
    setup.id.vendor = 0x1209;
    setup.id.product = 0x4b54;
    setup.id.version = 1;

    if (ioctl(fd, UI_DEV_SETUP, &setup) < 0 || ioctl(fd, UI_DEV_CREATE) < 0) {
        perror("creating uinput device");
        close(fd);
        return 1;
    }

    /* Let udev publish the node and inputd discover/grab it before emitting. */
    sleep(2);

    for (int i = 1; i < argc; i++) {
        int key = parse_key(argv[i]);

        if (key < 0) {
            fprintf(stderr, "invalid F-key '%s'; expected a number from 13 through 18\n", argv[i]);
            result = 2;
            break;
        }

        if (emit_key(fd, (unsigned short)key) != 0) {
            perror("writing uinput event");
            result = 1;
            break;
        }
        usleep(100000);
    }

    (void)ioctl(fd, UI_DEV_DESTROY);
    close(fd);
    return result;
}
