#include "keysharp_inputd/protocol.h"
#include "keysharp_trust/permissions.h"

#include <ctype.h>
#include <errno.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

#define KST_DEFAULT_SOCKET "/run/keysharp-inputd/keysharp-inputd.sock"
#define KST_SOCKET_ENV "KEYSHARP_INPUTD_SOCKET"

static const char *get_socket_path(const char *override)
{
    if (override != NULL && override[0] != '\0') {
        return override;
    }

    const char *env = getenv(KST_SOCKET_ENV);
    return (env != NULL && env[0] != '\0') ? env : KST_DEFAULT_SOCKET;
}

static int connect_daemon(const char *socket_path_override)
{
    struct sockaddr_un address;
    const char *path = get_socket_path(socket_path_override);
    size_t path_length = strlen(path);
    int fd;

    if (path_length >= sizeof(address.sun_path)) {
        fprintf(stderr, "keysharp-inputd trust: socket path too long: %s\n", path);
        return -1;
    }

    fd = socket(AF_UNIX, SOCK_STREAM, 0);

    if (fd < 0) {
        fprintf(stderr, "keysharp-inputd trust: socket: %s\n", strerror(errno));
        return -1;
    }

    memset(&address, 0, sizeof(address));
    address.sun_family = AF_UNIX;
    memcpy(address.sun_path, path, path_length);

    if (connect(fd, (const struct sockaddr *)&address, sizeof(address)) != 0) {
        fprintf(stderr, "keysharp-inputd trust: cannot connect to %s: %s\n", path, strerror(errno));
        fprintf(stderr, "keysharp-inputd trust: is keysharp-inputd installed and running?\n");
        close(fd);
        return -1;
    }

    return fd;
}

static int write_all(int fd, const void *data, size_t size)
{
    const uint8_t *cursor = data;
    size_t remaining = size;

    while (remaining > 0u) {
        ssize_t n = write(fd, cursor, remaining);

        if (n > 0) {
            cursor += (size_t)n;
            remaining -= (size_t)n;
            continue;
        }

        if (n < 0 && errno == EINTR) {
            continue;
        }

        return -1;
    }

    return 0;
}

static int read_all(int fd, void *data, size_t size)
{
    uint8_t *cursor = data;
    size_t remaining = size;

    while (remaining > 0u) {
        ssize_t n = read(fd, cursor, remaining);

        if (n > 0) {
            cursor += (size_t)n;
            remaining -= (size_t)n;
            continue;
        }

        if (n == 0) {
            return -1;
        }

        if (n < 0 && errno == EINTR) {
            continue;
        }

        return -1;
    }

    return 0;
}

static int send_frame(
    int fd,
    uint32_t type,
    uint64_t correlation_id,
    const void *payload,
    size_t payload_size)
{
    ksi_message_header header;
    uint32_t total_size = (uint32_t)(sizeof(header) + payload_size);

    if (sizeof(header) + payload_size > KSI_MAX_MESSAGE_SIZE) {
        return -1;
    }

    memset(&header, 0, sizeof(header));
    header.size = total_size;
    header.major = (uint16_t)KSI_PROTOCOL_MAJOR;
    header.minor = (uint16_t)KSI_PROTOCOL_MINOR;
    header.type = type;
    header.correlation_id = correlation_id;

    if (write_all(fd, &header, sizeof(header)) != 0) {
        return -1;
    }

    if (payload_size > 0u && write_all(fd, payload, payload_size) != 0) {
        return -1;
    }

    return 0;
}

static int recv_frame(
    int fd,
    ksi_message_header *header,
    uint8_t *payload,
    size_t payload_capacity,
    size_t *payload_size)
{
    size_t body_size;

    if (read_all(fd, header, sizeof(*header)) != 0) {
        return -1;
    }

    if (header->size < sizeof(*header) || header->size > KSI_MAX_MESSAGE_SIZE) {
        return -1;
    }

    body_size = header->size - sizeof(*header);

    if (body_size > payload_capacity) {
        return -1;
    }

    if (body_size > 0u && read_all(fd, payload, body_size) != 0) {
        return -1;
    }

    *payload_size = body_size;
    return 0;
}

static int handshake(int fd)
{
    ksi_client_hello_payload hello;
    ksi_message_header header;
    uint8_t payload[256];
    size_t payload_size;

    memset(&hello, 0, sizeof(hello));
    hello.requested_capabilities = 0u;
    hello.flags = 0u;

    if (send_frame(fd, KSI_MESSAGE_CLIENT_HELLO, 1u, &hello, sizeof(hello)) != 0) {
        fprintf(stderr, "keysharp-inputd trust: failed to send CLIENT_HELLO\n");
        return -1;
    }

    if (recv_frame(fd, &header, payload, sizeof(payload), &payload_size) != 0
        || header.type != KSI_MESSAGE_CLIENT_HELLO
        || payload_size < sizeof(ksi_client_hello_result_payload)) {
        fprintf(stderr, "keysharp-inputd trust: unexpected CLIENT_HELLO response\n");
        return -1;
    }

    return 0;
}

static void describe_capabilities(uint32_t bits, char *buffer, size_t buffer_size)
{
    static const struct { uint32_t bit; const char *name; } caps[] = {
        { KST_CAP_INPUT_HOOK_KEYBOARD,  "hook-keyboard" },
        { KST_CAP_INPUT_HOOK_MOUSE,     "hook-mouse" },
        { KST_CAP_INPUT_SYNTH_KEYBOARD, "synth-keyboard" },
        { KST_CAP_INPUT_SYNTH_MOUSE,    "synth-mouse" },
        { KST_CAP_INPUT_BLOCK,          "block-input" },
        { KST_CAP_SCREEN_CAPTURE,       "screen-capture" },
        { KST_CAP_ACCESSIBILITY_AUTOMATION, "accessibility-automation" },
    };
    size_t n = sizeof(caps) / sizeof(caps[0]);
    size_t offset = 0;
    bool first = true;
    size_t i;

    if (buffer == NULL || buffer_size == 0) {
        return;
    }

    buffer[0] = '\0';

    if (bits == 0) {
        (void)snprintf(buffer, buffer_size, "(none)");
        return;
    }

    for (i = 0; i < n; i++) {
        size_t name_len;

        if ((bits & caps[i].bit) == 0) {
            continue;
        }

        name_len = strlen(caps[i].name);

        if (!first) {
            if (offset + 2u + 1u >= buffer_size) {
                break;
            }

            buffer[offset++] = ',';
            buffer[offset++] = ' ';
        }

        if (offset + name_len + 1u >= buffer_size) {
            break;
        }

        memcpy(buffer + offset, caps[i].name, name_len);
        offset += name_len;
        first = false;
    }

    buffer[offset] = '\0';
}

static void format_timestamp(uint64_t utc_seconds, char *buffer, size_t buffer_size)
{
    time_t when = (time_t)utc_seconds;
    struct tm tm_value;

    if (gmtime_r(&when, &tm_value) == NULL) {
        (void)snprintf(buffer, buffer_size, "?");
        return;
    }

    if (strftime(buffer, buffer_size, "%Y-%m-%d %H:%M UTC", &tm_value) == 0) {
        (void)snprintf(buffer, buffer_size, "?");
    }
}

static int cmd_list(int fd)
{
    bool any = false;

    if (send_frame(fd, KSI_MESSAGE_LIST_PERMISSIONS, 2u, NULL, 0u) != 0) {
        fprintf(stderr, "keysharp-inputd trust: failed to send LIST_PERMISSIONS\n");
        return 1;
    }

    for (;;) {
        ksi_message_header header;
        uint8_t payload[sizeof(ksi_list_permissions_entry_payload) + KSI_PERMISSION_MAX_PATH];
        size_t payload_size;

        if (recv_frame(fd, &header, payload, sizeof(payload), &payload_size) != 0) {
            return 1;
        }

        if (header.type == KSI_MESSAGE_LIST_PERMISSIONS_RESULT) {
            if (!any) {
                printf("No stored input permissions for this user.\n");
            }

            return 0;
        }

        if (header.type != KSI_MESSAGE_LIST_PERMISSIONS_ENTRY
            || payload_size < sizeof(ksi_list_permissions_entry_payload)) {
            fprintf(stderr, "keysharp-inputd trust: unexpected response type=%u\n", header.type);
            return 1;
        }

        const ksi_list_permissions_entry_payload *entry =
            (const ksi_list_permissions_entry_payload *)(const void *)payload;
        size_t path_length = entry->path_length;
        char path_buffer[KSI_PERMISSION_MAX_PATH + 1u];
        char allow_text[256];
        char deny_text[256];
        char when_text[64];

        if (path_length > KSI_PERMISSION_MAX_PATH
            || sizeof(*entry) + path_length > payload_size) {
            fprintf(stderr, "keysharp-inputd trust: malformed entry\n");
            return 1;
        }

        memcpy(path_buffer, (const uint8_t *)payload + sizeof(*entry), path_length);
        path_buffer[path_length] = '\0';

        describe_capabilities(entry->persistent_allowed_capabilities, allow_text, sizeof(allow_text));
        describe_capabilities(entry->persistent_denied_capabilities, deny_text, sizeof(deny_text));
        format_timestamp(entry->last_seen_utc, when_text, sizeof(when_text));

        if (any) {
            printf("\n");
        }

        printf("uid:        %u\n", entry->uid);
        printf("hash:       %s\n", entry->exe_hash);
        printf("exe:        %s\n", path_buffer[0] != '\0' ? path_buffer : "(unknown)");
        printf("allowed:    %s\n", allow_text);
        printf("denied:     %s\n", deny_text);
        printf("last seen:  %s\n", when_text);

        any = true;
    }
}

typedef struct {
    char (*hashes)[KSI_PROTOCOL_HASH_HEX_BUFFER];
    size_t count;
    size_t capacity;
} hash_list;

static int hash_list_append(hash_list *list, const char *hash)
{
    if (list->count >= list->capacity) {
        size_t new_cap = list->capacity == 0u ? 8u : list->capacity * 2u;
        void *resized = realloc(list->hashes, new_cap * sizeof(*list->hashes));

        if (resized == NULL) {
            return -1;
        }

        list->hashes = resized;
        list->capacity = new_cap;
    }

    memcpy(list->hashes[list->count], hash, KSI_PROTOCOL_HASH_HEX_BUFFER);
    list->count++;
    return 0;
}

static int collect_hashes(int fd, hash_list *list)
{
    if (send_frame(fd, KSI_MESSAGE_LIST_PERMISSIONS, 2u, NULL, 0u) != 0) {
        return -1;
    }

    for (;;) {
        ksi_message_header header;
        uint8_t payload[sizeof(ksi_list_permissions_entry_payload) + KSI_PERMISSION_MAX_PATH];
        size_t payload_size;

        if (recv_frame(fd, &header, payload, sizeof(payload), &payload_size) != 0) {
            return -1;
        }

        if (header.type == KSI_MESSAGE_LIST_PERMISSIONS_RESULT) {
            return 0;
        }

        if (header.type != KSI_MESSAGE_LIST_PERMISSIONS_ENTRY
            || payload_size < sizeof(ksi_list_permissions_entry_payload)) {
            return -1;
        }

        const ksi_list_permissions_entry_payload *entry =
            (const ksi_list_permissions_entry_payload *)(const void *)payload;

        if (hash_list_append(list, entry->exe_hash) != 0) {
            return -1;
        }
    }
}

static int reset_one(int fd, const char *hash, uint32_t capabilities)
{
    ksi_reset_permissions_payload payload;
    ksi_message_header header;
    uint8_t response[64];
    size_t response_size;

    memset(&payload, 0, sizeof(payload));
    payload.target_uid = KSI_RESET_PERMISSIONS_UID_SELF;
    payload.capabilities = capabilities;
    (void)snprintf(payload.exe_hash, sizeof(payload.exe_hash), "%s", hash);

    if (send_frame(fd, KSI_MESSAGE_RESET_PERMISSIONS, 3u, &payload, sizeof(payload)) != 0) {
        return -1;
    }

    if (recv_frame(fd, &header, response, sizeof(response), &response_size) != 0) {
        return -1;
    }

    if (header.type != KSI_MESSAGE_RESET_PERMISSIONS
        || response_size < sizeof(ksi_status_payload)) {
        return -1;
    }

    const ksi_status_payload *status = (const ksi_status_payload *)(const void *)response;
    return status->status;
}

static int parse_caps_hex(const char *text, uint32_t *out)
{
    char *end;
    unsigned long value;

    errno = 0;
    value = strtoul(text, &end, 0);

    if (errno != 0 || end == text || *end != '\0' || value > 0xFFFFFFFFu) {
        return -1;
    }

    *out = (uint32_t)value;
    return 0;
}

static int cmd_reset(int fd, int argc, char **argv)
{
    uint32_t capabilities = KSI_INPUT_CAPABILITIES;
    const char *hash_arg = NULL;
    pid_t pid_arg = 0;
    bool all = false;
    char hash_buf[KSI_PROTOCOL_HASH_HEX_BUFFER];
    int i;

    for (i = 0; i < argc; i++) {
        const char *arg = argv[i];

        if (strcmp(arg, "--all") == 0) {
            all = true;
            continue;
        }

        if (strcmp(arg, "--pid") == 0 && i + 1 < argc) {
            char *end;
            unsigned long value;

            errno = 0;
            value = strtoul(argv[++i], &end, 10);

            if (errno != 0 || *end != '\0' || value == 0u) {
                fprintf(stderr, "keysharp-inputd trust: invalid --pid value\n");
                return 1;
            }

            pid_arg = (pid_t)value;
            continue;
        }

        if (strcmp(arg, "--caps") == 0 && i + 1 < argc) {
            if (parse_caps_hex(argv[++i], &capabilities) != 0) {
                fprintf(stderr, "keysharp-inputd trust: invalid --caps value\n");
                return 1;
            }

            continue;
        }

        if (arg[0] == '-') {
            fprintf(stderr, "keysharp-inputd trust: unknown option %s\n", arg);
            return 1;
        }

        if (hash_arg != NULL) {
            fprintf(stderr, "keysharp-inputd trust: too many arguments\n");
            return 1;
        }

        hash_arg = arg;
    }

    if (all && (hash_arg != NULL || pid_arg != 0)) {
        fprintf(stderr, "keysharp-inputd trust: --all cannot be combined with a hash or --pid\n");
        return 1;
    }

    if (!all && hash_arg == NULL && pid_arg == 0) {
        fprintf(stderr, "keysharp-inputd trust: provide a hash, --pid <pid>, or --all\n");
        return 1;
    }

    if (pid_arg != 0) {
        char path[KSI_PERMISSION_MAX_PATH];
        char command_line[KSI_PERMISSION_MAX_COMMAND_LINE];

        if (ksi_permissions_identify_process(
                pid_arg,
                path, sizeof(path),
                command_line, sizeof(command_line),
                hash_buf, NULL) != 0) {
            fprintf(stderr, "keysharp-inputd trust: cannot identify pid %ld\n", (long)pid_arg);
            return 1;
        }

        hash_arg = hash_buf;
    } else if (!all) {
        size_t hash_length = strlen(hash_arg);
        size_t j;

        if (hash_length != KSI_PERMISSION_HASH_HEX_LENGTH) {
            fprintf(stderr, "keysharp-inputd trust: hash must be %u hex characters\n",
                (unsigned int)KSI_PERMISSION_HASH_HEX_LENGTH);
            return 1;
        }

        for (j = 0; j < hash_length; j++) {
            if (!isxdigit((unsigned char)hash_arg[j])) {
                fprintf(stderr, "keysharp-inputd trust: hash contains non-hex characters\n");
                return 1;
            }
        }
    }

    if (all) {
        hash_list list = { NULL, 0, 0 };
        size_t j;
        int rc = 0;

        if (collect_hashes(fd, &list) != 0) {
            fprintf(stderr, "keysharp-inputd trust: failed to enumerate records\n");
            free(list.hashes);
            return 1;
        }

        for (j = 0; j < list.count; j++) {
            if (reset_one(fd, list.hashes[j], capabilities) != 0) {
                fprintf(stderr, "keysharp-inputd trust: failed to reset %s\n", list.hashes[j]);
                rc = 1;
            }
        }

        free(list.hashes);

        if (rc == 0) {
            printf("Cleared all input permissions for this user.\n");
        }

        return rc;
    }

    int status = reset_one(fd, hash_arg, capabilities);

    if (status == 0) {
        printf("Cleared input permissions for %s.\n", hash_arg);
        printf("The next Keysharp request from this process will re-prompt.\n");
        return 0;
    }

    if (status == 403) {
        fprintf(stderr, "keysharp-inputd trust: not authorized to reset this record\n");
    } else {
        fprintf(stderr, "keysharp-inputd trust: reset failed (status %d)\n", status);
    }

    return 1;
}

static void trust_usage(FILE *stream)
{
    fprintf(stream,
        "Usage: keysharp-inputd trust <command> [options]\n"
        "\n"
        "Commands:\n"
        "  list                              List input permission records for this user.\n"
        "  reset <hash> [--caps <mask>]      Clear allow/deny bits by exe-hash.\n"
        "  reset --pid <pid> [--caps <mask>] Clear allow/deny bits for a running pid.\n"
        "  reset --all [--caps <mask>]       Clear allow/deny bits for all records.\n"
        "\n"
        "Capabilities are a hex bitmask (default: all input caps). Bits:\n"
        "  0x01 hook-keyboard, 0x02 hook-mouse,\n"
        "  0x04 synth-keyboard, 0x08 synth-mouse, 0x10 block-input,\n"
        "  0x40 accessibility-automation.\n"
        "\n"
        "Options:\n"
        "  --socket PATH  Override daemon socket path (default: %s).\n"
        "\n"
        "Environment:\n"
        "  %s  Override the daemon socket path.\n",
        KST_DEFAULT_SOCKET,
        KST_SOCKET_ENV);
}

int trust_cli_main(int argc, char **argv)
{
    const char *socket_override = NULL;
    int i;
    int cmd_argc;
    char **cmd_argv;
    int fd;
    int rc;

    /* Strip leading "trust" word if present (called as keysharp-inputd trust ...) */
    if (argc >= 1 && strcmp(argv[0], "trust") == 0) {
        argc--;
        argv++;
    }

    /* Extract --socket before dispatching. */
    for (i = 0; i < argc; i++) {
        if (strcmp(argv[i], "--socket") == 0 && i + 1 < argc) {
            socket_override = argv[i + 1];
            /* Shift these two args out. */
            memmove(&argv[i], &argv[i + 2], (size_t)(argc - i - 2) * sizeof(*argv));
            argc -= 2;
            i--;
        }
    }

    if (argc < 1 || strcmp(argv[0], "-h") == 0 || strcmp(argv[0], "--help") == 0) {
        trust_usage(argc < 1 ? stderr : stdout);
        return argc < 1 ? 2 : 0;
    }

    fd = connect_daemon(socket_override);

    if (fd < 0) {
        return 1;
    }

    if (handshake(fd) != 0) {
        close(fd);
        return 1;
    }

    cmd_argc = argc - 1;
    cmd_argv = argv + 1;

    if (strcmp(argv[0], "list") == 0) {
        rc = cmd_list(fd);
    } else if (strcmp(argv[0], "reset") == 0) {
        rc = cmd_reset(fd, cmd_argc, cmd_argv);
    } else {
        fprintf(stderr, "keysharp-inputd trust: unknown command '%s'\n", argv[0]);
        trust_usage(stderr);
        rc = 2;
    }

    close(fd);
    return rc;
}
