#include "keysharp_inputd/protocol.h"

#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#define DEFAULT_SOCKET_PATH "/tmp/keysharp-inputd.sock"

static int write_exact(int fd, const void *buffer, size_t length)
{
    const uint8_t *cursor = buffer;
    size_t remaining = length;

    while (remaining > 0) {
        ssize_t bytes_written = write(fd, cursor, remaining);

        if (bytes_written < 0) {
            if (errno == EINTR) {
                continue;
            }

            return -1;
        }

        cursor += bytes_written;
        remaining -= (size_t)bytes_written;
    }

    return 0;
}

static int read_exact(int fd, void *buffer, size_t length)
{
    uint8_t *cursor = buffer;
    size_t remaining = length;

    while (remaining > 0) {
        ssize_t bytes_read = read(fd, cursor, remaining);

        if (bytes_read <= 0) {
            return -1;
        }

        cursor += bytes_read;
        remaining -= (size_t)bytes_read;
    }

    return 0;
}

static int connect_socket(const char *socket_path)
{
    struct sockaddr_un address;
    int fd = socket(AF_UNIX, SOCK_STREAM, 0);

    if (fd < 0) {
        fprintf(stderr, "socket failed: %s\n", strerror(errno));
        return -1;
    }

    memset(&address, 0, sizeof(address));
    address.sun_family = AF_UNIX;

    if (strlen(socket_path) >= sizeof(address.sun_path)) {
        fprintf(stderr, "socket path too long: %s\n", socket_path);
        close(fd);
        return -1;
    }

    (void)snprintf(address.sun_path, sizeof(address.sun_path), "%s", socket_path);

    if (connect(fd, (const struct sockaddr *)&address, sizeof(address)) != 0) {
        fprintf(stderr, "connect failed: %s\n", strerror(errno));
        close(fd);
        return -1;
    }

    return fd;
}

static int send_frame(
    int fd,
    uint32_t type,
    uint64_t correlation_id,
    const void *payload,
    size_t payload_size,
    int split_frame)
{
    ksi_message_header header;

    memset(&header, 0, sizeof(header));
    header.size = (uint32_t)(sizeof(header) + payload_size);
    header.major = KSI_PROTOCOL_MAJOR;
    header.minor = KSI_PROTOCOL_MINOR;
    header.type = type;
    header.correlation_id = correlation_id;

    if (split_frame) {
        if (write_exact(fd, &header, sizeof(header) / 2u) != 0) {
            return -1;
        }

        usleep(10000);

        if (write_exact(
                fd,
                ((const uint8_t *)&header) + (sizeof(header) / 2u),
                sizeof(header) - (sizeof(header) / 2u)) != 0) {
            return -1;
        }
    } else {
        if (write_exact(fd, &header, sizeof(header)) != 0) {
            return -1;
        }
    }

    if (payload_size > 0) {
        if (split_frame) {
            usleep(10000);
        }

        if (write_exact(fd, payload, payload_size) != 0) {
            return -1;
        }
    }

    return 0;
}

static int read_status(int fd)
{
    ksi_message_header header;
    ksi_status_payload payload;

    if (read_exact(fd, &header, sizeof(header)) != 0) {
        return -1;
    }

    if (header.size != sizeof(header) + sizeof(payload)) {
        fprintf(stderr, "unexpected response size: %u\n", header.size);
        return -1;
    }

    if (read_exact(fd, &payload, sizeof(payload)) != 0) {
        return -1;
    }

    printf("response type=%u status=%d detail=%u correlation=%llu\n",
        header.type,
        payload.status,
        payload.detail,
        (unsigned long long)header.correlation_id);

    return payload.status == 0 ? 0 : -1;
}

static int send_hello(int fd, int split_frame)
{
    ksi_client_hello_payload payload = {
        .requested_capabilities =
            KSI_CAP_HOOK_KEYBOARD
            | KSI_CAP_HOOK_MOUSE
            | KSI_CAP_SYNTH_KEYBOARD
            | KSI_CAP_SYNTH_MOUSE,
        .reserved = 0,
    };

    if (send_frame(fd, KSI_MESSAGE_CLIENT_HELLO, 1, &payload, sizeof(payload), split_frame) != 0) {
        return -1;
    }

    return read_status(fd);
}

static int send_key_a(int fd)
{
    struct {
        uint32_t count;
        uint32_t reserved;
        ksi_input inputs[2];
    } payload;

    memset(&payload, 0, sizeof(payload));
    payload.count = 2;
    payload.inputs[0].type = KSI_INPUT_KEYBOARD;
    payload.inputs[0].data.keyboard.vk = 0x41u;
    payload.inputs[1].type = KSI_INPUT_KEYBOARD;
    payload.inputs[1].data.keyboard.vk = 0x41u;
    payload.inputs[1].data.keyboard.flags = KSI_KEYEVENTF_KEYUP;

    if (send_frame(fd, KSI_MESSAGE_SYNTHESIZE_INPUT, 2, &payload, sizeof(payload), 0) != 0) {
        return -1;
    }

    return read_status(fd);
}

static int subscribe_keyboard_hook(int fd)
{
    ksi_hook_subscription_payload payload = {
        .hook_type = KSI_HOOK_KEYBOARD_LL,
        .flags = 0,
    };

    if (send_frame(fd, KSI_MESSAGE_SUBSCRIBE_HOOK, 3, &payload, sizeof(payload), 0) != 0) {
        return -1;
    }

    return read_status(fd);
}

static int send_bogus_hook_decision(int fd)
{
    struct {
        uint64_t event_id;
        uint32_t decision;
        uint32_t input_count;
    } payload = {
        .event_id = 999,
        .decision = KSI_HOOK_DECISION_PASS,
        .input_count = 0,
    };

    if (send_frame(fd, KSI_MESSAGE_HOOK_DECISION, 4, &payload, sizeof(payload), 0) != 0) {
        return -1;
    }

    return read_status(fd);
}

int main(int argc, char **argv)
{
    const char *socket_path = argc > 1 ? argv[1] : DEFAULT_SOCKET_PATH;
    int split_frame = argc > 2 && strcmp(argv[2], "--split") == 0;
    int fd = connect_socket(socket_path);
    int result = 0;

    if (fd < 0) {
        return 1;
    }

    if (send_hello(fd, split_frame) != 0) {
        result = 1;
    }

    if (result == 0 && send_key_a(fd) != 0) {
        result = 1;
    }

    if (subscribe_keyboard_hook(fd) != 0) {
        result = 1;
    }

    if (send_bogus_hook_decision(fd) == 0) {
        result = 1;
    }

    close(fd);
    return result;
}
