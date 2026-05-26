/* keysharp-trust: command-line interface for managing the Keysharp trust
 * store.
 *
 * Talks to keysharp-inputd over its Unix domain socket so users do not need
 * to be root; the daemon enforces uid-scoped authorization (callers can only
 * list/reset their own records unless the daemon's effective uid is root and
 * the caller is also root).
 *
 * Subcommands:
 *   keysharp-trust list
 *   keysharp-trust reset <exe-hash> [--caps <hex>]
 *   keysharp-trust reset --pid <pid>  [--caps <hex>]
 *
 * `reset` without --caps clears every capability bit (allow and deny) for the
 * matched record, so the next prompt re-asks the user from scratch.
 */

#include <ctype.h>
#include <errno.h>
#include <inttypes.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

#include "keysharp_inputd/protocol.h"
#include "keysharp_trust/permissions.h"

#define KST_DEFAULT_SOCKET "/run/keysharp-inputd/keysharp-inputd.sock"
#define KST_SOCKET_ENV "KEYSHARP_INPUTD_SOCKET"

static const char *socket_path(void)
{
    const char *override = getenv(KST_SOCKET_ENV);

    return (override != NULL && override[0] != '\0') ? override : KST_DEFAULT_SOCKET;
}

static int connect_daemon(void)
{
    struct sockaddr_un address;
    int fd;
    const char *path = socket_path();
    size_t path_length = strlen(path);

    if (path_length >= sizeof(address.sun_path)) {
        fprintf(stderr, "keysharp-trust: socket path too long: %s\n", path);
        return -1;
    }

    fd = socket(AF_UNIX, SOCK_STREAM, 0);

    if (fd < 0) {
        fprintf(stderr, "keysharp-trust: socket: %s\n", strerror(errno));
        return -1;
    }

    memset(&address, 0, sizeof(address));
    address.sun_family = AF_UNIX;
    memcpy(address.sun_path, path, path_length);

    if (connect(fd, (const struct sockaddr *)&address, sizeof(address)) != 0) {
        fprintf(stderr, "keysharp-trust: cannot connect to %s: %s\n", path, strerror(errno));
        fprintf(stderr, "keysharp-trust: is keysharp-inputd installed and running?\n");
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
        fprintf(stderr, "keysharp-trust: frame too large (%zu bytes)\n", sizeof(header) + payload_size);
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

static int recv_frame(int fd, ksi_message_header *header, uint8_t *payload, size_t payload_capacity, size_t *payload_size)
{
    size_t body_size;

    if (read_all(fd, header, sizeof(*header)) != 0) {
        return -1;
    }

    if (header->size < sizeof(*header) || header->size > KSI_MAX_MESSAGE_SIZE) {
        fprintf(stderr, "keysharp-trust: daemon returned invalid frame size %u\n", header->size);
        return -1;
    }

    body_size = header->size - sizeof(*header);

    if (body_size > payload_capacity) {
        fprintf(stderr, "keysharp-trust: response too large (%zu bytes)\n", body_size);
        return -1;
    }

    if (body_size > 0u && read_all(fd, payload, body_size) != 0) {
        return -1;
    }

    *payload_size = body_size;
    return 0;
}

/* Perform CLIENT_HELLO with no requested capabilities. The daemon will resolve
 * our identity but will not prompt the user. We need to authenticate the
 * connection so subsequent LIST/RESET messages dispatch. */
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
        fprintf(stderr, "keysharp-trust: failed to send CLIENT_HELLO: %s\n", strerror(errno));
        return -1;
    }

    if (recv_frame(fd, &header, payload, sizeof(payload), &payload_size) != 0) {
        fprintf(stderr, "keysharp-trust: failed to read CLIENT_HELLO response\n");
        return -1;
    }

    if (header.type != KSI_MESSAGE_CLIENT_HELLO || payload_size < sizeof(ksi_client_hello_result_payload)) {
        fprintf(stderr, "keysharp-trust: unexpected CLIENT_HELLO response\n");
        return -1;
    }

    return 0;
}

static const char *capability_name(uint32_t bit)
{
    switch (bit) {
        case KSI_CAP_HOOK_KEYBOARD: return "hook-keyboard";
        case KSI_CAP_HOOK_MOUSE:    return "hook-mouse";
        case KSI_CAP_SYNTH_KEYBOARD: return "synth-keyboard";
        case KSI_CAP_SYNTH_MOUSE:   return "synth-mouse";
        case KSI_CAP_BLOCK_INPUT:   return "block-input";
        default: return NULL;
    }
}

static void describe_capabilities(uint32_t bits, char *buffer, size_t buffer_size)
{
    size_t offset = 0u;
    bool first = true;

    if (buffer == NULL || buffer_size == 0u) {
        return;
    }

    buffer[0] = '\0';

    if (bits == 0u) {
        (void)snprintf(buffer, buffer_size, "(none)");
        return;
    }

    for (uint32_t bit = 1u; bit != 0u; bit <<= 1) {
        const char *name;

        if ((bits & bit) == 0u) {
            continue;
        }

        name = capability_name(bit);

        if (name == NULL) {
            continue;
        }

        if (offset + strlen(name) + (first ? 0u : 2u) >= buffer_size) {
            break;
        }

        if (!first) {
            buffer[offset++] = ',';
            buffer[offset++] = ' ';
        }

        memcpy(buffer + offset, name, strlen(name));
        offset += strlen(name);
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
    if (send_frame(fd, KSI_MESSAGE_LIST_PERMISSIONS, 2u, NULL, 0u) != 0) {
        fprintf(stderr, "keysharp-trust: failed to send LIST_PERMISSIONS\n");
        return 1;
    }

    bool any = false;

    for (;;) {
        ksi_message_header header;
        uint8_t payload[sizeof(ksi_list_permissions_entry_payload) + KSI_PERMISSION_MAX_PATH];
        size_t payload_size;

        if (recv_frame(fd, &header, payload, sizeof(payload), &payload_size) != 0) {
            return 1;
        }

        if (header.type == KSI_MESSAGE_LIST_PERMISSIONS_RESULT) {
            if (payload_size >= sizeof(ksi_status_payload)) {
                const ksi_status_payload *status =
                    (const ksi_status_payload *)(const void *)payload;

                if (status->status != 0) {
                    fprintf(stderr,
                        "keysharp-trust: list failed with status %d (detail %u)\n",
                        status->status, status->detail);
                    return 1;
                }
            }

            if (!any) {
                printf("No stored Keysharp permissions for this user.\n");
            }

            return 0;
        }

        if (header.type != KSI_MESSAGE_LIST_PERMISSIONS_ENTRY
            || payload_size < sizeof(ksi_list_permissions_entry_payload)) {
            fprintf(stderr, "keysharp-trust: unexpected response frame type=%u\n", header.type);
            return 1;
        }

        const ksi_list_permissions_entry_payload *entry =
            (const ksi_list_permissions_entry_payload *)(const void *)payload;
        size_t path_length = entry->path_length;
        const char *path_bytes = (const char *)(payload + sizeof(*entry));
        char path_buffer[KSI_PERMISSION_MAX_PATH + 1u];
        char allow_text[256];
        char deny_text[256];
        char when_text[64];

        if (path_length > KSI_PERMISSION_MAX_PATH
            || sizeof(*entry) + path_length > payload_size) {
            fprintf(stderr, "keysharp-trust: malformed entry frame\n");
            return 1;
        }

        memcpy(path_buffer, path_bytes, path_length);
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

static int identify_pid(pid_t pid, char hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u])
{
    char path[KSI_PERMISSION_MAX_PATH];
    char command_line[KSI_PERMISSION_MAX_COMMAND_LINE];

    if (ksi_permissions_identify_process(
            pid,
            path, sizeof(path),
            command_line, sizeof(command_line),
            hash) != 0) {
        fprintf(stderr, "keysharp-trust: cannot identify pid %ld\n", (long)pid);
        return -1;
    }

    return 0;
}

static int cmd_reset(int fd, int argc, char **argv)
{
    ksi_reset_permissions_payload payload;
    ksi_message_header header;
    uint8_t response[64];
    size_t response_size;
    uint32_t capabilities = KSI_RESET_PERMISSIONS_CAPS_ALL;
    const char *hash_arg = NULL;
    pid_t pid_arg = 0;

    for (int i = 0; i < argc; i++) {
        const char *arg = argv[i];

        if (strcmp(arg, "--pid") == 0 && i + 1 < argc) {
            char *end;
            unsigned long value;

            errno = 0;
            value = strtoul(argv[++i], &end, 10);

            if (errno != 0 || *end != '\0' || value == 0u) {
                fprintf(stderr, "keysharp-trust: invalid --pid value\n");
                return 1;
            }

            pid_arg = (pid_t)value;
            continue;
        }

        if (strcmp(arg, "--caps") == 0 && i + 1 < argc) {
            if (parse_caps_hex(argv[++i], &capabilities) != 0) {
                fprintf(stderr, "keysharp-trust: invalid --caps value\n");
                return 1;
            }

            continue;
        }

        if (arg[0] == '-') {
            fprintf(stderr, "keysharp-trust: unknown option %s\n", arg);
            return 1;
        }

        if (hash_arg != NULL) {
            fprintf(stderr, "keysharp-trust: too many arguments\n");
            return 1;
        }

        hash_arg = arg;
    }

    memset(&payload, 0, sizeof(payload));
    payload.target_uid = KSI_RESET_PERMISSIONS_UID_SELF;
    payload.capabilities = capabilities;

    if (pid_arg != 0) {
        char hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];

        if (identify_pid(pid_arg, hash) != 0) {
            return 1;
        }

        (void)snprintf(payload.exe_hash, sizeof(payload.exe_hash), "%s", hash);
    } else if (hash_arg != NULL) {
        size_t hash_length = strlen(hash_arg);

        if (hash_length != KSI_PERMISSION_HASH_HEX_LENGTH) {
            fprintf(stderr, "keysharp-trust: hash must be %u hex characters\n",
                KSI_PERMISSION_HASH_HEX_LENGTH);
            return 1;
        }

        for (size_t i = 0; i < hash_length; i++) {
            if (!isxdigit((unsigned char)hash_arg[i])) {
                fprintf(stderr, "keysharp-trust: hash contains non-hex characters\n");
                return 1;
            }
        }

        (void)snprintf(payload.exe_hash, sizeof(payload.exe_hash), "%s", hash_arg);
    } else {
        fprintf(stderr, "keysharp-trust: provide either a hash or --pid <pid>\n");
        return 1;
    }

    if (send_frame(fd, KSI_MESSAGE_RESET_PERMISSIONS, 3u, &payload, sizeof(payload)) != 0) {
        fprintf(stderr, "keysharp-trust: failed to send RESET_PERMISSIONS\n");
        return 1;
    }

    if (recv_frame(fd, &header, response, sizeof(response), &response_size) != 0) {
        return 1;
    }

    if (header.type != KSI_MESSAGE_RESET_PERMISSIONS
        || response_size < sizeof(ksi_status_payload)) {
        fprintf(stderr, "keysharp-trust: unexpected reset response type=%u\n", header.type);
        return 1;
    }

    const ksi_status_payload *status = (const ksi_status_payload *)(const void *)response;

    if (status->status != 0) {
        if (status->detail == 403u) {
            fprintf(stderr, "keysharp-trust: not authorized to reset this record\n");
        } else if (status->detail == 400u) {
            fprintf(stderr, "keysharp-trust: invalid reset request\n");
        } else {
            fprintf(stderr, "keysharp-trust: reset failed (status %d, detail %u)\n",
                status->status, status->detail);
        }

        return 1;
    }

    printf("Cleared Keysharp permissions for %s.\n", payload.exe_hash);
    printf("The next Keysharp request from this process will re-prompt.\n");
    return 0;
}

static void usage(FILE *stream)
{
    fprintf(stream,
        "Usage: keysharp-trust <command> [options]\n"
        "\n"
        "Commands:\n"
        "  list                              List trust records for the current user.\n"
        "  reset <hash> [--caps <mask>]      Clear allow/deny bits by exe-hash.\n"
        "  reset --pid <pid> [--caps <mask>] Clear allow/deny bits for a running pid.\n"
        "\n"
        "Capabilities are a bitmask (default: all). Bits:\n"
        "  0x01 hook-keyboard, 0x02 hook-mouse,\n"
        "  0x04 synth-keyboard, 0x08 synth-mouse,\n"
        "  0x10 block-input.\n"
        "\n"
        "Environment:\n"
        "  %s   override the daemon socket path.\n",
        KST_SOCKET_ENV);
}

int main(int argc, char **argv)
{
    if (argc < 2) {
        usage(stderr);
        return 2;
    }

    if (strcmp(argv[1], "-h") == 0 || strcmp(argv[1], "--help") == 0) {
        usage(stdout);
        return 0;
    }

    int fd = connect_daemon();

    if (fd < 0) {
        return 1;
    }

    int rc = 1;

    if (handshake(fd) != 0) {
        close(fd);
        return 1;
    }

    if (strcmp(argv[1], "list") == 0) {
        rc = cmd_list(fd);
    } else if (strcmp(argv[1], "reset") == 0) {
        rc = cmd_reset(fd, argc - 2, argv + 2);
    } else {
        fprintf(stderr, "keysharp-trust: unknown command '%s'\n", argv[1]);
        usage(stderr);
        rc = 2;
    }

    close(fd);
    return rc;
}
