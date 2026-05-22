#include "keysharp_inputd/protocol.h"

#include <errno.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#define DEFAULT_SOCKET_PATH "/run/keysharp-inputd/keysharp-inputd.sock"
#define DEFAULT_TARGET_VK 0x7Bu
#define DEFAULT_MODIFY_VK 0x41u

static volatile sig_atomic_t keep_running = 1;

typedef enum test_decision {
    TEST_DECISION_PASS,
    TEST_DECISION_BLOCK,
    TEST_DECISION_MODIFY,
} test_decision;

typedef struct hooktest_options {
    const char *socket_path;
    test_decision decision;
    uint32_t target_vk;
    uint32_t modify_vk;
    uint32_t hook_mask;
    int delay_ms;
    int subscribe_delay_ms;
    int event_limit;
    int send_vk;
    uint32_t send_unicode;
    int emergency;
    int release_modifiers;
    int all_events;
} hooktest_options;

static void handle_signal(int signal_number)
{
    (void)signal_number;
    keep_running = 0;
}

static int write_exact(int fd, const void *buffer, size_t length)
{
    const uint8_t *cursor = buffer;
    size_t remaining = length;

    while (remaining > 0) {
        ssize_t written = write(fd, cursor, remaining);

        if (written < 0) {
            if (errno == EINTR) {
                continue;
            }

            return -1;
        }

        cursor += written;
        remaining -= (size_t)written;
    }

    return 0;
}

static int read_exact(int fd, void *buffer, size_t length)
{
    uint8_t *cursor = buffer;
    size_t remaining = length;

    while (remaining > 0) {
        ssize_t read_count = read(fd, cursor, remaining);

        if (read_count == 0) {
            return 0;
        }

        if (read_count < 0) {
            if (errno == EINTR) {
                continue;
            }

            return -1;
        }

        cursor += read_count;
        remaining -= (size_t)read_count;
    }

    return 1;
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
    size_t payload_size)
{
    ksi_message_header header;

    memset(&header, 0, sizeof(header));
    header.size = (uint32_t)(sizeof(header) + payload_size);
    header.major = KSI_PROTOCOL_MAJOR;
    header.minor = KSI_PROTOCOL_MINOR;
    header.type = type;
    header.client_id = 0;
    header.correlation_id = correlation_id;

    if (write_exact(fd, &header, sizeof(header)) != 0) {
        return -1;
    }

    if (payload_size > 0 && write_exact(fd, payload, payload_size) != 0) {
        return -1;
    }

    return 0;
}

static int read_frame(int fd, uint8_t *buffer, size_t buffer_size, ksi_message_header **header, uint8_t **payload)
{
    int result = read_exact(fd, buffer, sizeof(ksi_message_header));

    if (result <= 0) {
        return result;
    }

    *header = (ksi_message_header *)(void *)buffer;

    if ((*header)->size < sizeof(ksi_message_header) || (*header)->size > buffer_size) {
        fprintf(stderr, "invalid frame size: %u\n", (*header)->size);
        return -1;
    }

    if ((*header)->major != KSI_PROTOCOL_MAJOR || (*header)->minor > KSI_PROTOCOL_MINOR) {
        fprintf(stderr, "unsupported protocol version: %u.%u\n", (*header)->major, (*header)->minor);
        return -1;
    }

    result = read_exact(
        fd,
        buffer + sizeof(ksi_message_header),
        (*header)->size - sizeof(ksi_message_header));

    if (result <= 0) {
        return result;
    }

    *payload = buffer + sizeof(ksi_message_header);
    return 1;
}

static int read_expected_status(int fd, uint32_t expected_type, uint64_t expected_correlation)
{
    uint8_t buffer[KSI_MAX_MESSAGE_SIZE];
    ksi_message_header *header = NULL;
    uint8_t *payload = NULL;

    if (read_frame(fd, buffer, sizeof(buffer), &header, &payload) <= 0) {
        fprintf(stderr, "failed reading status response\n");
        return -1;
    }

    if (header->type != expected_type || header->correlation_id != expected_correlation) {
        fprintf(stderr,
            "unexpected response type=%u correlation=%llu\n",
            header->type,
            (unsigned long long)header->correlation_id);
        return -1;
    }

    if (expected_type == KSI_MESSAGE_CLIENT_HELLO) {
        const ksi_client_hello_result_payload *hello;

        if (header->size != sizeof(*header) + sizeof(*hello)) {
            fprintf(stderr, "unexpected hello response size: %u\n", header->size);
            return -1;
        }

        hello = (const ksi_client_hello_result_payload *)(const void *)payload;
        printf("hello status=%d granted=0x%x\n", hello->status, hello->granted_capabilities);
        return hello->status == 0 ? 0 : -1;
    }

    {
        const ksi_status_payload *status;

        if (header->size != sizeof(*header) + sizeof(*status)) {
            fprintf(stderr, "unexpected status response size: %u\n", header->size);
            return -1;
        }

        status = (const ksi_status_payload *)(const void *)payload;
        printf("status type=%u status=%d detail=%u correlation=%llu\n",
            header->type,
            status->status,
            status->detail,
            (unsigned long long)header->correlation_id);
        return status->status == 0 ? 0 : -1;
    }
}

static int send_hello(int fd)
{
    ksi_client_hello_payload payload = {
        .requested_capabilities =
            KSI_CAP_HOOK_KEYBOARD
            | KSI_CAP_HOOK_MOUSE
            | KSI_CAP_SYNTH_KEYBOARD
            | KSI_CAP_SYNTH_MOUSE,
        .reserved = 0,
    };

    if (send_frame(fd, KSI_MESSAGE_CLIENT_HELLO, 1, &payload, sizeof(payload)) != 0) {
        return -1;
    }

    return read_expected_status(fd, KSI_MESSAGE_CLIENT_HELLO, 1);
}

static int send_emergency_passthrough(int fd)
{
    if (send_frame(fd, KSI_MESSAGE_EMERGENCY_PASSTHROUGH, 2, NULL, 0) != 0) {
        return -1;
    }

    return read_expected_status(fd, KSI_MESSAGE_EMERGENCY_PASSTHROUGH, 2);
}

static int send_key_stroke(int fd, uint32_t vk, uint64_t correlation_id)
{
    struct {
        uint32_t count;
        uint32_t reserved;
        ksi_input inputs[2];
    } payload;

    memset(&payload, 0, sizeof(payload));
    payload.count = 2;
    payload.inputs[0].type = KSI_INPUT_KEYBOARD;
    payload.inputs[0].data.keyboard.vk = (uint16_t)vk;
    payload.inputs[1].type = KSI_INPUT_KEYBOARD;
    payload.inputs[1].data.keyboard.vk = (uint16_t)vk;
    payload.inputs[1].data.keyboard.flags = KSI_KEYEVENTF_KEYUP;

    if (send_frame(fd, KSI_MESSAGE_SYNTHESIZE_INPUT, correlation_id, &payload, sizeof(payload)) != 0) {
        return -1;
    }

    return read_expected_status(fd, KSI_MESSAGE_SYNTHESIS_RESULT, correlation_id);
}

static int send_unicode_codepoint(int fd, uint32_t codepoint, uint64_t correlation_id)
{
    struct {
        uint32_t count;
        uint32_t reserved;
        ksi_input inputs[4];
    } payload;
    uint16_t units[2];
    uint32_t unit_count;

    memset(&payload, 0, sizeof(payload));

    if (codepoint == 0 || codepoint > 0x10FFFFu || (codepoint >= 0xD800u && codepoint <= 0xDFFFu)) {
        fprintf(stderr, "invalid unicode codepoint: 0x%x\n", codepoint);
        return -1;
    }

    if (codepoint <= 0xFFFFu) {
        units[0] = (uint16_t)codepoint;
        unit_count = 1;
    } else {
        uint32_t value = codepoint - 0x10000u;
        units[0] = (uint16_t)(0xD800u + (value >> 10));
        units[1] = (uint16_t)(0xDC00u + (value & 0x3FFu));
        unit_count = 2;
    }

    payload.count = unit_count * 2u;

    for (uint32_t i = 0; i < unit_count; i++) {
        payload.inputs[i * 2u].type = KSI_INPUT_KEYBOARD;
        payload.inputs[i * 2u].data.keyboard.scan = units[i];
        payload.inputs[i * 2u].data.keyboard.flags = KSI_KEYEVENTF_UNICODE;
        payload.inputs[(i * 2u) + 1u].type = KSI_INPUT_KEYBOARD;
        payload.inputs[(i * 2u) + 1u].data.keyboard.scan = units[i];
        payload.inputs[(i * 2u) + 1u].data.keyboard.flags = KSI_KEYEVENTF_UNICODE | KSI_KEYEVENTF_KEYUP;
    }

    if (send_frame(
            fd,
            KSI_MESSAGE_SYNTHESIZE_INPUT,
            correlation_id,
            &payload,
            sizeof(payload.count) + sizeof(payload.reserved) + ((size_t)payload.count * sizeof(payload.inputs[0]))) != 0) {
        return -1;
    }

    return read_expected_status(fd, KSI_MESSAGE_SYNTHESIS_RESULT, correlation_id);
}

static int send_key_up(int fd, uint32_t vk, uint64_t correlation_id)
{
    struct {
        uint32_t count;
        uint32_t reserved;
        ksi_input inputs[1];
    } payload;

    memset(&payload, 0, sizeof(payload));
    payload.count = 1;
    payload.inputs[0].type = KSI_INPUT_KEYBOARD;
    payload.inputs[0].data.keyboard.vk = (uint16_t)vk;
    payload.inputs[0].data.keyboard.flags = KSI_KEYEVENTF_KEYUP;

    if (send_frame(fd, KSI_MESSAGE_SYNTHESIZE_INPUT, correlation_id, &payload, sizeof(payload)) != 0) {
        return -1;
    }

    return read_expected_status(fd, KSI_MESSAGE_SYNTHESIS_RESULT, correlation_id);
}

static int release_modifiers(int fd, uint64_t *correlation)
{
    static const uint32_t modifiers[] = {
        0xA0u, 0xA1u,
        0xA2u, 0xA3u,
        0xA4u, 0xA5u,
        0x5Bu, 0x5Cu,
    };

    for (size_t i = 0; i < sizeof(modifiers) / sizeof(modifiers[0]); i++) {
        if (send_key_up(fd, modifiers[i], (*correlation)++) != 0) {
            return -1;
        }
    }

    return 0;
}

static int subscribe_hook(int fd, uint32_t hook_type, uint64_t correlation_id)
{
    ksi_hook_subscription_payload payload = {
        .hook_type = hook_type,
        .flags = 0,
    };

    if (send_frame(fd, KSI_MESSAGE_SUBSCRIBE_HOOK, correlation_id, &payload, sizeof(payload)) != 0) {
        return -1;
    }

    return read_expected_status(fd, KSI_MESSAGE_SUBSCRIBE_HOOK, correlation_id);
}

static const char *message_name(uint32_t message)
{
    switch (message) {
        case KSI_WM_KEYDOWN:
            return "KEYDOWN";
        case KSI_WM_KEYUP:
            return "KEYUP";
        case KSI_WM_SYSKEYDOWN:
            return "SYSKEYDOWN";
        case KSI_WM_SYSKEYUP:
            return "SYSKEYUP";
        case KSI_WM_MOUSEMOVE:
            return "MOUSEMOVE";
        case KSI_WM_LBUTTONDOWN:
            return "LBUTTONDOWN";
        case KSI_WM_LBUTTONUP:
            return "LBUTTONUP";
        case KSI_WM_RBUTTONDOWN:
            return "RBUTTONDOWN";
        case KSI_WM_RBUTTONUP:
            return "RBUTTONUP";
        case KSI_WM_MBUTTONDOWN:
            return "MBUTTONDOWN";
        case KSI_WM_MBUTTONUP:
            return "MBUTTONUP";
        case KSI_WM_MOUSEWHEEL:
            return "MOUSEWHEEL";
        case KSI_WM_MOUSEHWHEEL:
            return "MOUSEHWHEEL";
        case KSI_WM_XBUTTONDOWN:
            return "XBUTTONDOWN";
        case KSI_WM_XBUTTONUP:
            return "XBUTTONUP";
        default:
            return "UNKNOWN";
    }
}

static int is_key_up(uint32_t message)
{
    return message == KSI_WM_KEYUP || message == KSI_WM_SYSKEYUP;
}

static int is_matching_keyboard_event(const hooktest_options *options, const ksi_keyboard_hook_event *event)
{
    return options->all_events || options->target_vk == 0 || event->vk_code == options->target_vk;
}

static int send_hook_decision(
    int fd,
    uint64_t event_id,
    uint32_t decision,
    uint32_t modify_vk,
    uint64_t correlation_id)
{
    struct {
        uint64_t event_id;
        uint32_t decision;
        uint32_t input_count;
        ksi_input inputs[2];
    } payload;

    memset(&payload, 0, sizeof(payload));
    payload.event_id = event_id;
    payload.decision = decision;

    if (decision == KSI_HOOK_DECISION_MODIFY) {
        payload.input_count = 2;
        payload.inputs[0].type = KSI_INPUT_KEYBOARD;
        payload.inputs[0].data.keyboard.vk = (uint16_t)modify_vk;
        payload.inputs[1].type = KSI_INPUT_KEYBOARD;
        payload.inputs[1].data.keyboard.vk = (uint16_t)modify_vk;
        payload.inputs[1].data.keyboard.flags = KSI_KEYEVENTF_KEYUP;
    }

    if (send_frame(
            fd,
            KSI_MESSAGE_HOOK_DECISION,
            correlation_id,
            &payload,
            sizeof(payload.event_id)
                + sizeof(payload.decision)
                + sizeof(payload.input_count)
                + ((size_t)payload.input_count * sizeof(payload.inputs[0]))) != 0) {
        return -1;
    }

    return 0;
}

static uint32_t choose_decision(
    const hooktest_options *options,
    const ksi_hook_event_payload *event,
    uint32_t *modify_vk)
{
    *modify_vk = options->modify_vk;

    if (event->hook_type != KSI_HOOK_KEYBOARD_LL) {
        return options->decision == TEST_DECISION_BLOCK && options->all_events
            ? KSI_HOOK_DECISION_BLOCK
            : KSI_HOOK_DECISION_PASS;
    }

    if (!is_matching_keyboard_event(options, &event->event.keyboard)) {
        return KSI_HOOK_DECISION_PASS;
    }

    if (options->decision == TEST_DECISION_BLOCK) {
        return KSI_HOOK_DECISION_BLOCK;
    }

    if (options->decision == TEST_DECISION_MODIFY) {
        return is_key_up(event->event.keyboard.message)
            ? KSI_HOOK_DECISION_BLOCK
            : KSI_HOOK_DECISION_MODIFY;
    }

    return KSI_HOOK_DECISION_PASS;
}

static int handle_hook_event(
    int fd,
    const hooktest_options *options,
    const ksi_message_header *header,
    const uint8_t *payload,
    size_t payload_size)
{
    const ksi_hook_event_payload *event;
    uint32_t decision;
    uint32_t modify_vk;

    if (payload_size < sizeof(event->event_id) + sizeof(event->hook_type) + sizeof(event->reserved)) {
        fprintf(stderr, "short hook event payload: %zu\n", payload_size);
        return -1;
    }

    event = (const ksi_hook_event_payload *)(const void *)payload;

    if (event->hook_type == KSI_HOOK_KEYBOARD_LL) {
        if (payload_size < 16u + sizeof(event->event.keyboard)) {
            fprintf(stderr, "short keyboard hook event payload: %zu\n", payload_size);
            return -1;
        }

        printf("hook event=%llu keyboard %s vk=0x%02x scan=%u flags=0x%x injected=%s device=%u time=%llu\n",
            (unsigned long long)event->event_id,
            message_name(event->event.keyboard.message),
            event->event.keyboard.vk_code,
            event->event.keyboard.scan_code,
            event->event.keyboard.flags,
            (event->event.keyboard.flags & KSI_LLKHF_INJECTED) != 0 ? "yes" : "no",
            event->event.keyboard.device_id,
            (unsigned long long)event->event.keyboard.time_ms);
    } else if (event->hook_type == KSI_HOOK_MOUSE_LL) {
        if (payload_size < 16u + sizeof(event->event.mouse)) {
            fprintf(stderr, "short mouse hook event payload: %zu\n", payload_size);
            return -1;
        }

        printf("hook event=%llu mouse %s x=%d y=%d data=0x%x flags=0x%x injected=%s device=%u time=%llu\n",
            (unsigned long long)event->event_id,
            message_name(event->event.mouse.message),
            event->event.mouse.x,
            event->event.mouse.y,
            event->event.mouse.mouse_data,
            event->event.mouse.flags,
            (event->event.mouse.flags & KSI_LLMHF_INJECTED) != 0 ? "yes" : "no",
            event->event.mouse.device_id,
            (unsigned long long)event->event.mouse.time_ms);
    } else {
        printf("hook event=%llu unknown hook type=%u\n",
            (unsigned long long)event->event_id,
            event->hook_type);
    }

    if (options->delay_ms > 0) {
        usleep((useconds_t)options->delay_ms * 1000u);
    }

    decision = choose_decision(options, event, &modify_vk);

    printf("decision event=%llu %s%s%s\n",
        (unsigned long long)event->event_id,
        decision == KSI_HOOK_DECISION_PASS ? "PASS" :
            decision == KSI_HOOK_DECISION_BLOCK ? "BLOCK" : "MODIFY",
        decision == KSI_HOOK_DECISION_MODIFY ? " vk=0x" : "",
        decision == KSI_HOOK_DECISION_MODIFY ? "" : "");

    if (decision == KSI_HOOK_DECISION_MODIFY) {
        printf("modify replacement vk=0x%02x\n", modify_vk);
    }

    return send_hook_decision(fd, event->event_id, decision, modify_vk, header->correlation_id);
}

static int event_loop(int fd, const hooktest_options *options)
{
    uint8_t buffer[KSI_MAX_MESSAGE_SIZE];
    int handled_events = 0;
    uint64_t pending_status_correlation = 1000;

    while (keep_running) {
        struct pollfd pfd = {
            .fd = fd,
            .events = POLLIN,
            .revents = 0,
        };
        int poll_result = poll(&pfd, 1, 500);

        if (poll_result < 0) {
            if (errno == EINTR) {
                continue;
            }

            fprintf(stderr, "poll failed: %s\n", strerror(errno));
            return -1;
        }

        if (poll_result == 0) {
            continue;
        }

        if ((pfd.revents & POLLIN) != 0) {
            ksi_message_header *header = NULL;
            uint8_t *payload = NULL;
            size_t payload_size;
            int read_result = read_frame(fd, buffer, sizeof(buffer), &header, &payload);

            if (read_result == 0) {
                puts("daemon disconnected");
                return 0;
            }

            if (read_result < 0) {
                return -1;
            }

            payload_size = header->size - sizeof(*header);

            if (header->type == KSI_MESSAGE_HOOK_EVENT) {
                if (handle_hook_event(fd, options, header, payload, payload_size) != 0) {
                    return -1;
                }

                pending_status_correlation = header->correlation_id;
                handled_events++;

                if (options->event_limit > 0 && handled_events >= options->event_limit) {
                    return 0;
                }
            } else if (header->type == KSI_MESSAGE_HOOK_DECISION) {
                const ksi_status_payload *status;

                if (payload_size != sizeof(*status)) {
                    fprintf(stderr, "unexpected hook decision status size: %zu\n", payload_size);
                    return -1;
                }

                status = (const ksi_status_payload *)(const void *)payload;
                printf("decision status=%d detail=%u correlation=%llu%s\n",
                    status->status,
                    status->detail,
                    (unsigned long long)header->correlation_id,
                    header->correlation_id == pending_status_correlation ? "" : " unexpected-correlation");
            } else {
                printf("message type=%u size=%u correlation=%llu\n",
                    header->type,
                    header->size,
                    (unsigned long long)header->correlation_id);
            }
        }

        if ((pfd.revents & (POLLHUP | POLLERR | POLLNVAL)) != 0) {
            puts("daemon connection closed");
            return 0;
        }
    }

    return 0;
}

static uint32_t parse_u32(const char *value)
{
    char *end = NULL;
    unsigned long parsed = strtoul(value, &end, 0);

    if (end == value || *end != '\0' || parsed > UINT32_MAX) {
        fprintf(stderr, "invalid integer: %s\n", value);
        exit(2);
    }

    return (uint32_t)parsed;
}

static void print_usage(const char *argv0)
{
    fprintf(stderr,
        "Usage: %s [SOCKET] [options]\n"
        "\n"
        "Options:\n"
        "  --socket PATH       Connect to PATH. Default: /run/keysharp-inputd/keysharp-inputd.sock\n"
        "  --keyboard          Subscribe keyboard hook. Default when no hook is specified.\n"
        "  --mouse             Subscribe mouse hook.\n"
        "  --decision MODE     pass, block, or modify. Default: pass\n"
        "  --target-vk VK      Key target for block/modify. Default with block/modify: 0x7b (F12)\n"
        "  --modify-vk VK      Replacement key for modify. Default: 0x41 (A)\n"
        "  --all               Allow block/modify to apply to every subscribed event.\n"
        "  --delay-ms MS       Delay hook replies to test daemon timeout handling.\n"
        "  --subscribe-delay-ms MS\n"
        "                     Wait before subscribing hooks, useful when launching from a terminal.\n"
        "  --count N           Exit after N hook events.\n"
        "  --send-vk VK        Send one synthetic key stroke after hello.\n"
        "  --send-a            Shortcut for --send-vk 0x41.\n"
        "  --send-unicode CP   Send one Unicode codepoint, e.g. 0x263A.\n"
        "  --release-modifiers Send key-up events for Ctrl/Shift/Alt/Win and exit unless hooks are requested.\n"
        "  --emergency         Send emergency pass-through and exit.\n",
        argv0);
}

static int parse_args(int argc, char **argv, hooktest_options *options)
{
    memset(options, 0, sizeof(*options));
    options->socket_path = DEFAULT_SOCKET_PATH;
    options->decision = TEST_DECISION_PASS;
    options->target_vk = 0;
    options->modify_vk = DEFAULT_MODIFY_VK;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--socket") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "--socket requires a path\n");
                return -1;
            }

            options->socket_path = argv[i];
        } else if (strcmp(argv[i], "--keyboard") == 0) {
            options->hook_mask |= KSI_CAP_HOOK_KEYBOARD;
        } else if (strcmp(argv[i], "--mouse") == 0) {
            options->hook_mask |= KSI_CAP_HOOK_MOUSE;
        } else if (strcmp(argv[i], "--decision") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "--decision requires a mode\n");
                return -1;
            }

            if (strcmp(argv[i], "pass") == 0) {
                options->decision = TEST_DECISION_PASS;
            } else if (strcmp(argv[i], "block") == 0) {
                options->decision = TEST_DECISION_BLOCK;
            } else if (strcmp(argv[i], "modify") == 0) {
                options->decision = TEST_DECISION_MODIFY;
            } else {
                fprintf(stderr, "unknown decision mode: %s\n", argv[i]);
                return -1;
            }
        } else if (strcmp(argv[i], "--target-vk") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "--target-vk requires a value\n");
                return -1;
            }

            options->target_vk = parse_u32(argv[i]);
        } else if (strcmp(argv[i], "--modify-vk") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "--modify-vk requires a value\n");
                return -1;
            }

            options->modify_vk = parse_u32(argv[i]);
        } else if (strcmp(argv[i], "--all") == 0) {
            options->all_events = 1;
        } else if (strcmp(argv[i], "--delay-ms") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "--delay-ms requires a value\n");
                return -1;
            }

            options->delay_ms = (int)parse_u32(argv[i]);
        } else if (strcmp(argv[i], "--subscribe-delay-ms") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "--subscribe-delay-ms requires a value\n");
                return -1;
            }

            options->subscribe_delay_ms = (int)parse_u32(argv[i]);
        } else if (strcmp(argv[i], "--count") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "--count requires a value\n");
                return -1;
            }

            options->event_limit = (int)parse_u32(argv[i]);
        } else if (strcmp(argv[i], "--send-vk") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "--send-vk requires a value\n");
                return -1;
            }

            options->send_vk = (int)parse_u32(argv[i]);
        } else if (strcmp(argv[i], "--send-a") == 0) {
            options->send_vk = 0x41;
        } else if (strcmp(argv[i], "--send-unicode") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "--send-unicode requires a codepoint\n");
                return -1;
            }

            options->send_unicode = parse_u32(argv[i]);
        } else if (strcmp(argv[i], "--release-modifiers") == 0) {
            options->release_modifiers = 1;
        } else if (strcmp(argv[i], "--emergency") == 0) {
            options->emergency = 1;
        } else if (strcmp(argv[i], "--help") == 0 || strcmp(argv[i], "-h") == 0) {
            print_usage(argv[0]);
            exit(0);
        } else if (argv[i][0] != '-') {
            options->socket_path = argv[i];
        } else {
            fprintf(stderr, "unknown option: %s\n", argv[i]);
            return -1;
        }
    }

    if (options->hook_mask == 0 && !options->release_modifiers) {
        options->hook_mask = KSI_CAP_HOOK_KEYBOARD;
    }

    if ((options->decision == TEST_DECISION_BLOCK || options->decision == TEST_DECISION_MODIFY)
        && options->target_vk == 0
        && !options->all_events) {
        options->target_vk = DEFAULT_TARGET_VK;
    }

    if ((options->decision == TEST_DECISION_BLOCK || options->decision == TEST_DECISION_MODIFY)
        && options->target_vk == 0
        && !options->all_events) {
        fprintf(stderr, "block/modify requires --target-vk or --all\n");
        return -1;
    }

    return 0;
}

int main(int argc, char **argv)
{
    hooktest_options options;
    int fd;
    int result = 0;
    uint64_t correlation = 10;

    if (parse_args(argc, argv, &options) != 0) {
        print_usage(argv[0]);
        return 2;
    }

    if (signal(SIGINT, handle_signal) == SIG_ERR || signal(SIGTERM, handle_signal) == SIG_ERR) {
        fprintf(stderr, "failed to install signal handler\n");
        return 1;
    }

    fd = connect_socket(options.socket_path);

    if (fd < 0) {
        return 1;
    }

    if (send_hello(fd) != 0) {
        close(fd);
        return 1;
    }

    if (options.emergency) {
        result = send_emergency_passthrough(fd) == 0 ? 0 : 1;
        close(fd);
        return result;
    }

    if (options.release_modifiers && release_modifiers(fd, &correlation) != 0) {
        close(fd);
        return 1;
    }

    if (options.release_modifiers && options.send_vk == 0 && options.hook_mask == 0) {
        close(fd);
        return 0;
    }

    if (options.subscribe_delay_ms > 0) {
        printf("waiting %d ms before subscribing hooks\n", options.subscribe_delay_ms);
        usleep((useconds_t)options.subscribe_delay_ms * 1000u);
    }

    if ((options.hook_mask & KSI_CAP_HOOK_KEYBOARD) != 0
        && subscribe_hook(fd, KSI_HOOK_KEYBOARD_LL, correlation++) != 0) {
        close(fd);
        return 1;
    }

    if ((options.hook_mask & KSI_CAP_HOOK_MOUSE) != 0
        && subscribe_hook(fd, KSI_HOOK_MOUSE_LL, correlation++) != 0) {
        close(fd);
        return 1;
    }

    if (options.send_vk != 0 && send_key_stroke(fd, (uint32_t)options.send_vk, correlation++) != 0) {
        close(fd);
        return 1;
    }

    if (options.send_unicode != 0 && send_unicode_codepoint(fd, options.send_unicode, correlation++) != 0) {
        close(fd);
        return 1;
    }

    printf("hooktest running: socket=%s decision=%s target-vk=0x%x modify-vk=0x%x\n",
        options.socket_path,
        options.decision == TEST_DECISION_PASS ? "pass" :
            options.decision == TEST_DECISION_BLOCK ? "block" : "modify",
        options.target_vk,
        options.modify_vk);

    result = event_loop(fd, &options) == 0 ? 0 : 1;
    close(fd);
    return result;
}
