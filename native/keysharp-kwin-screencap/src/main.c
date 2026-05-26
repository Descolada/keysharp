#include "keysharp_trust/permissions.h"

#include <errno.h>
#include <fcntl.h>
#include <gio/gio.h>
#include <gio/gunixfdlist.h>
#include <grp.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <time.h>
#include <unistd.h>

#define KSS_DBUS_NAME "org.kde.KWin"
#define KSS_DBUS_PATH "/org/kde/KWin/ScreenShot2"
#define KSS_DBUS_INTERFACE "org.kde.KWin.ScreenShot2"
#define KSS_CAPTURE_TIMEOUT_MS 30000
#define KSS_CAPTURE_FILE_READY_TIMEOUT_MS 1000

/* In --serve mode the helper writes a 1-byte status prefix before each response
 * so the C# side can distinguish OK from error without ambiguity. */
#define KSS_SERVE_STATUS_OK   0x00
#define KSS_SERVE_STATUS_ERR  0x01
#define KSS_EXIT_ERROR        1
#define KSS_EXIT_USAGE        2
#define KSS_EXIT_DENIED       3
#define KSS_EXIT_UNSUPPORTED  4

typedef enum {
    KSS_TRUST_TRUSTED = 0,
    KSS_TRUST_DENIED = 1,
    KSS_TRUST_UNAVAILABLE = 2
} kss_trust_result;

/* Long-lived D-Bus connection used in --serve mode so we don't pay the
 * session-bus handshake cost on every capture. */
static GDBusConnection *g_serve_connection = NULL;

static void print_usage(const char *argv0)
{
    fprintf(stderr, "Usage: %s --area X Y WIDTH HEIGHT | --serve | --authorize | --diagnose\n", argv0);
}

static bool parse_int_arg(const char *text, int *value)
{
    char *end = NULL;
    long parsed;

    if (text == NULL || value == NULL) {
        return false;
    }

    errno = 0;
    parsed = strtol(text, &end, 10);

    if (errno != 0 || end == text || *end != '\0'
        || parsed < INT32_MIN || parsed > INT32_MAX) {
        return false;
    }

    *value = (int)parsed;
    return true;
}

static bool get_requester_credentials(pid_t pid, uid_t *uid, gid_t *gid)
{
    char proc_path[64];
    struct stat info;

    if (pid <= 0 || uid == NULL || gid == NULL) {
        return false;
    }

    (void)snprintf(proc_path, sizeof(proc_path), "/proc/%ld", (long)pid);

    if (stat(proc_path, &info) != 0) {
        return false;
    }

    *uid = info.st_uid;
    *gid = info.st_gid;
    return true;
}

static kss_trust_result ensure_trusted(pid_t requester_pid, uid_t requester_uid, gid_t requester_gid)
{
    ksi_permission_store *store = NULL;
    char exe_path[KSI_PERMISSION_MAX_PATH];
    char command_line[KSI_PERMISSION_MAX_COMMAND_LINE];
    char exe_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
    uint32_t allowed;
    uint32_t missing;
    ksi_permission_decision decision;
    kss_trust_result result = KSS_TRUST_DENIED;

    if (ksi_permissions_create(&store) != 0) {
        fprintf(stderr, "keysharp-kwin-screencap: failed to open trust store\n");
        return KSS_TRUST_UNAVAILABLE;
    }

    if (ksi_permissions_identify_process(
            requester_pid,
            exe_path,
            sizeof(exe_path),
            command_line,
            sizeof(command_line),
            exe_hash) != 0) {
        fprintf(stderr, "keysharp-kwin-screencap: failed to identify requester pid=%ld\n", (long)requester_pid);
        result = KSS_TRUST_UNAVAILABLE;
        goto cleanup;
    }

    allowed = ksi_permissions_get_allowed_capabilities(store, requester_uid, exe_hash);
    missing = KST_CAP_SCREEN_CAPTURE & ~allowed;

    if (missing == 0u) {
        ksi_permissions_note_seen(store, requester_uid, exe_hash, exe_path);
        result = KSS_TRUST_TRUSTED;
        goto cleanup;
    }

    decision = ksi_permissions_prompt(
        requester_pid,
        requester_uid,
        requester_gid,
        exe_path,
        command_line,
        exe_hash,
        missing);

    if (decision == KSI_PERMISSION_DECISION_ALLOW_ONCE) {
        result = KSS_TRUST_TRUSTED;
        (void)ksi_permissions_grant_session(store, requester_uid, exe_hash, exe_path, missing);
    } else if (decision == KSI_PERMISSION_DECISION_ALLOW_ALWAYS) {
        if (ksi_permissions_grant_persistent(store, requester_uid, exe_hash, exe_path, missing) == 0) {
            result = KSS_TRUST_TRUSTED;
        } else {
            fprintf(stderr, "keysharp-kwin-screencap: failed to update trust store\n");
            result = KSS_TRUST_UNAVAILABLE;
        }
    }

cleanup:
    ksi_permissions_destroy(store);
    return result;
}

static bool drop_to_requester(uid_t uid, gid_t gid)
{
    if (geteuid() != 0) {
        return true;
    }

    if (setgroups(0, NULL) != 0
        || setresgid(gid, gid, gid) != 0
        || setresuid(uid, uid, uid) != 0) {
        fprintf(stderr, "keysharp-kwin-screencap: failed to drop privileges: %s\n", strerror(errno));
        return false;
    }

    if (prctl(PR_SET_DUMPABLE, 1, 0, 0, 0) != 0) {
        fprintf(stderr, "keysharp-kwin-screencap: failed to make process inspectable: %s\n", strerror(errno));
        return false;
    }

    return true;
}

static void print_diagnostics(void)
{
    char exe_path[KSI_PERMISSION_MAX_PATH];
    ssize_t length = readlink("/proc/self/exe", exe_path, sizeof(exe_path) - 1u);
    int dumpable = prctl(PR_GET_DUMPABLE, 0, 0, 0, 0);

    if (length >= 0) {
        exe_path[length] = '\0';
    } else {
        (void)snprintf(exe_path, sizeof(exe_path), "(readlink failed: %s)", strerror(errno));
    }

    printf("pid=%ld\n", (long)getpid());
    printf("ppid=%ld\n", (long)getppid());
    printf("uid=%ld euid=%ld gid=%ld egid=%ld\n",
        (long)getuid(), (long)geteuid(), (long)getgid(), (long)getegid());
    printf("dumpable=%d\n", dumpable);
    printf("exe=%s\n", exe_path);
    printf("XDG_RUNTIME_DIR=%s\n", getenv("XDG_RUNTIME_DIR") != NULL ? getenv("XDG_RUNTIME_DIR") : "");
    printf("DBUS_SESSION_BUS_ADDRESS=%s\n",
        getenv("DBUS_SESSION_BUS_ADDRESS") != NULL ? getenv("DBUS_SESSION_BUS_ADDRESS") : "");
}

static void ensure_session_environment(uid_t uid)
{
    char runtime_dir[128];
    char bus_address[160];

    (void)snprintf(runtime_dir, sizeof(runtime_dir), "/run/user/%lu", (unsigned long)uid);
    (void)snprintf(bus_address, sizeof(bus_address), "unix:path=%s/bus", runtime_dir);

    if (getenv("XDG_RUNTIME_DIR") == NULL) {
        (void)setenv("XDG_RUNTIME_DIR", runtime_dir, 1);
    }

    if (getenv("DBUS_SESSION_BUS_ADDRESS") == NULL) {
        (void)setenv("DBUS_SESSION_BUS_ADDRESS", bus_address, 1);
    }
}

static int create_capture_file(void)
{
    char path[] = "/tmp/keysharp-kwin-screencap-XXXXXX";
    int fd = mkstemp(path);

    if (fd >= 0) {
        (void)unlink(path);
    }

    return fd;
}

static bool write_exact(int fd, const void *buffer, size_t size, const char *context)
{
    const uint8_t *cursor = (const uint8_t *)buffer;

    while (size > 0u) {
        ssize_t written = write(fd, cursor, size);

        if (written < 0) {
            if (errno == EINTR) {
                continue;
            }

            fprintf(stderr, "keysharp-kwin-screencap: write failed during %s: %s\n",
                context != NULL ? context : "output", strerror(errno));
            return false;
        }

        if (written == 0) {
            fprintf(stderr, "keysharp-kwin-screencap: short write during %s\n",
                context != NULL ? context : "output");
            return false;
        }

        cursor += written;
        size -= (size_t)written;
    }

    return true;
}

static bool copy_fd_to_fd(int source_fd, int target_fd, uint64_t bytes)
{
    uint8_t buffer[65536];

    while (bytes > 0u) {
        size_t requested = bytes < sizeof(buffer) ? (size_t)bytes : sizeof(buffer);
        ssize_t bytes_read = read(source_fd, buffer, requested);

        if (bytes_read < 0) {
            if (errno == EINTR) {
                continue;
            }

            fprintf(stderr, "keysharp-kwin-screencap: failed to read capture data: %s\n", strerror(errno));
            return false;
        }

        if (bytes_read == 0) {
            fprintf(stderr, "keysharp-kwin-screencap: capture data ended early with %llu bytes remaining\n",
                (unsigned long long)bytes);
            return false;
        }

        if (!write_exact(target_fd, buffer, (size_t)bytes_read, "capture payload")) {
            return false;
        }

        bytes -= (uint64_t)bytes_read;
    }

    return true;
}

static bool wait_for_capture_file_size(int fd, uint64_t expected_bytes)
{
    struct timespec start;

    if (clock_gettime(CLOCK_MONOTONIC, &start) != 0) {
        fprintf(stderr, "keysharp-kwin-screencap: failed to get monotonic clock: %s\n", strerror(errno));
        return false;
    }

    for (;;) {
        struct stat info;
        struct timespec now;
        int64_t elapsed_ms;

        if (fstat(fd, &info) != 0) {
            fprintf(stderr, "keysharp-kwin-screencap: failed to stat capture file: %s\n", strerror(errno));
            return false;
        }

        if (info.st_size >= 0 && (uint64_t)info.st_size >= expected_bytes) {
            return true;
        }

        if (clock_gettime(CLOCK_MONOTONIC, &now) != 0) {
            fprintf(stderr, "keysharp-kwin-screencap: failed to get monotonic clock: %s\n", strerror(errno));
            return false;
        }

        elapsed_ms = ((int64_t)(now.tv_sec - start.tv_sec) * 1000LL)
                     + ((int64_t)(now.tv_nsec - start.tv_nsec) / 1000000LL);

        if (elapsed_ms >= KSS_CAPTURE_FILE_READY_TIMEOUT_MS) {
            fprintf(stderr,
                "keysharp-kwin-screencap: capture file reached only %lld of %llu bytes after %d ms\n",
                (long long)info.st_size,
                (unsigned long long)expected_bytes,
                KSS_CAPTURE_FILE_READY_TIMEOUT_MS);
            return false;
        }

        {
            struct timespec delay = {
                .tv_sec = 0,
                .tv_nsec = 10L * 1000L * 1000L,
            };
            (void)nanosleep(&delay, NULL);
        }
    }
}

static bool write_capture_response(int out_fd, int image_fd, guint32 width, guint32 height, guint32 stride, guint32 format)
{
    char magic[8] = { 'K', 'S', 'S', 'C', '1', '\0', '\0', '\0' };
    uint64_t bytes = (uint64_t)stride * (uint64_t)height;

    if (bytes == 0u) {
        fprintf(stderr, "keysharp-kwin-screencap: KWin returned an empty capture (%ux%u stride=%u format=%u)\n",
            width, height, stride, format);
        return false;
    }

    if (!wait_for_capture_file_size(image_fd, bytes)) {
        return false;
    }

    if (lseek(image_fd, 0, SEEK_SET) < 0) {
        fprintf(stderr, "keysharp-kwin-screencap: failed to rewind capture file: %s\n", strerror(errno));
        return false;
    }

    return write_exact(out_fd, magic, sizeof(magic), "response magic")
        && write_exact(out_fd, &width, sizeof(width), "response width")
        && write_exact(out_fd, &height, sizeof(height), "response height")
        && write_exact(out_fd, &stride, sizeof(stride), "response stride")
        && write_exact(out_fd, &format, sizeof(format), "response format")
        && write_exact(out_fd, &bytes, sizeof(bytes), "response byte count")
        && copy_fd_to_fd(image_fd, out_fd, bytes);
}

/* Opens the session bus on first call and reuses it for subsequent calls.
 * Caller does not own the returned connection — it lives for the process lifetime. */
static GDBusConnection *get_session_bus(void)
{
    GError *error = NULL;

    if (g_serve_connection != NULL) {
        return g_serve_connection;
    }

    g_serve_connection = g_bus_get_sync(G_BUS_TYPE_SESSION, NULL, &error);

    if (g_serve_connection == NULL) {
        fprintf(stderr, "keysharp-kwin-screencap: failed to connect to session bus: %s\n",
            error != NULL ? error->message : "unknown error");

        if (error != NULL) {
            g_error_free(error);
        }
    }

    return g_serve_connection;
}

/* Captures the requested area via KWin ScreenShot2 and writes the framed response
 * (KSSC1 header + raw pixels) to out_fd. Reuses the cached session-bus connection,
 * so per-call cost is the D-Bus round trip plus a single memfd→out_fd copy. */
static bool capture_area_to_fd(int out_fd, int x, int y, int width, int height)
{
    GError *error = NULL;
    GDBusConnection *connection = NULL;
    GUnixFDList *fd_list = NULL;
    GVariantBuilder options;
    GVariant *reply = NULL;
    GVariant *results = NULL;
    gchar *type = NULL;
    guint32 result_width = 0;
    guint32 result_height = 0;
    guint32 stride = 0;
    guint32 format = 0;
    int image_fd = -1;
    int fd_handle;
    bool success = false;

    image_fd = create_capture_file();

    if (image_fd < 0) {
        fprintf(stderr, "keysharp-kwin-screencap: failed to create capture file: %s\n", strerror(errno));
        return false;
    }

    connection = get_session_bus();

    if (connection == NULL) {
        goto cleanup;
    }

    fd_list = g_unix_fd_list_new();
    fd_handle = g_unix_fd_list_append(fd_list, image_fd, &error);

    if (fd_handle < 0) {
        fprintf(stderr, "keysharp-kwin-screencap: failed to append fd: %s\n",
            error != NULL ? error->message : "unknown error");
        goto cleanup;
    }

    g_variant_builder_init(&options, G_VARIANT_TYPE_VARDICT);
    g_variant_builder_add(&options, "{sv}", "include-cursor", g_variant_new_boolean(FALSE));
    g_variant_builder_add(&options, "{sv}", "native-resolution", g_variant_new_boolean(FALSE));

    reply = g_dbus_connection_call_with_unix_fd_list_sync(
        connection,
        KSS_DBUS_NAME,
        KSS_DBUS_PATH,
        KSS_DBUS_INTERFACE,
        "CaptureArea",
        g_variant_new("(iiuua{sv}h)", x, y, (guint32)width, (guint32)height, &options, fd_handle),
        G_VARIANT_TYPE("(a{sv})"),
        G_DBUS_CALL_FLAGS_NONE,
        KSS_CAPTURE_TIMEOUT_MS,
        fd_list,
        NULL,
        NULL,
        &error);

    if (reply == NULL) {
        fprintf(stderr, "keysharp-kwin-screencap: KWin CaptureArea failed: %s\n",
            error != NULL ? error->message : "unknown error");
        goto cleanup;
    }

    g_variant_get(reply, "(@a{sv})", &results);

    if (!g_variant_lookup(results, "type", "s", &type)) {
        gchar *printed = g_variant_print(results, TRUE);
        fprintf(stderr, "keysharp-kwin-screencap: KWin capture result missing type: %s\n",
            printed != NULL ? printed : "(unprintable)");
        g_free(printed);
        goto cleanup;
    }

    if (strcmp(type, "raw") != 0) {
        fprintf(stderr, "keysharp-kwin-screencap: unsupported KWin capture type '%s'\n", type);
        goto cleanup;
    }

    if (!g_variant_lookup(results, "width", "u", &result_width)
        || !g_variant_lookup(results, "height", "u", &result_height)
        || !g_variant_lookup(results, "stride", "u", &stride)
        || !g_variant_lookup(results, "format", "u", &format)) {
        gchar *printed = g_variant_print(results, TRUE);
        fprintf(stderr, "keysharp-kwin-screencap: KWin raw capture metadata incomplete: %s\n",
            printed != NULL ? printed : "(unprintable)");
        g_free(printed);
        goto cleanup;
    }

    success = write_capture_response(out_fd, image_fd, result_width, result_height, stride, format);

    if (!success) {
        fprintf(stderr, "keysharp-kwin-screencap: failed to stream capture (%ux%u stride=%u format=%u)\n",
            result_width, result_height, stride, format);
    }

cleanup:
    if (type != NULL) {
        g_free(type);
    }

    if (results != NULL) {
        g_variant_unref(results);
    }

    if (reply != NULL) {
        g_variant_unref(reply);
    }

    if (fd_list != NULL) {
        g_object_unref(fd_list);
    }

    /* connection is owned by get_session_bus() and reused across calls — do not unref. */

    if (error != NULL) {
        g_error_free(error);
    }

    if (image_fd >= 0) {
        close(image_fd);
    }

    return success;
}

static bool capture_area(int x, int y, int width, int height)
{
    return capture_area_to_fd(STDOUT_FILENO, x, y, width, height);
}

/* Reads a single line (terminated by '\n' or EOF) from in_fd into buf, returning the
 * length excluding the terminator. Returns 0 on EOF with no data, -1 on error. */
/* Line-buffered reader for serve_loop. Holds a small static refill buffer so request
 * lines don't cost one read() syscall per byte (~16 syscalls/request previously);
 * single-instance state is fine because serve_loop runs in a single thread. Returns
 * the line length excluding the terminator, 0 on EOF with no buffered data, -1 on
 * read error. Caller must ensure cap >= 2 (room for one char + null). */
static ssize_t read_line(int in_fd, char *buf, size_t cap)
{
    static uint8_t refill[512];
    static size_t refill_len = 0;
    static size_t refill_pos = 0;
    size_t length = 0;

    if (cap == 0) {
        return -1;
    }

    while (length + 1u < cap) {
        if (refill_pos >= refill_len) {
            ssize_t result;

            do {
                result = read(in_fd, refill, sizeof(refill));
            } while (result < 0 && errno == EINTR);

            if (result < 0) {
                return -1;
            }

            if (result == 0) {
                if (length == 0) {
                    return 0;
                }
                break;
            }

            refill_len = (size_t)result;
            refill_pos = 0;
        }

        char ch = (char)refill[refill_pos++];

        if (ch == '\n') {
            break;
        }

        buf[length++] = ch;
    }

    buf[length] = '\0';
    return (ssize_t)length;
}

static bool write_serve_status(int out_fd, uint8_t status)
{
    return write_exact(out_fd, &status, sizeof(status), "serve status");
}

static bool write_serve_error(int out_fd, const char *message)
{
    uint32_t length = (uint32_t)strlen(message);
    return write_serve_status(out_fd, KSS_SERVE_STATUS_ERR)
        && write_exact(out_fd, &length, sizeof(length), "serve error length")
        && (length == 0u || write_exact(out_fd, message, length, "serve error body"));
}

/* Long-lived stdin/stdout protocol. Spawned once per Keysharp process; each capture
 * is a "area X Y W H\n" line on stdin and a status-prefixed framed response on stdout.
 * Permissions, privilege drop, and session-bus connection are all done before entering
 * this loop, so a request costs only the KWin D-Bus round trip plus one memfd→pipe copy.
 *
 * Shutdown is EOF-driven: when the parent dies (normal exit or SIGKILL alike), the
 * kernel closes our stdin pipe, the next read returns 0, and we exit cleanly. No
 * explicit idle timeout — an idle helper costs nothing while blocked in read(). */
static int serve_loop(void)
{
    /* SIGPIPE on stdout (parent died) becomes EPIPE we can handle, not a fatal signal. */
    (void)signal(SIGPIPE, SIG_IGN);

    for (;;) {
        char line[256];
        ssize_t length = read_line(STDIN_FILENO, line, sizeof(line));
        int x;
        int y;
        int width;
        int height;
        char *token;
        char *save;

        if (length < 0) {
            return 1;
        }

        if (length == 0) {
            /* Parent closed stdin — orderly shutdown. */
            return 0;
        }

        token = strtok_r(line, " \t", &save);

        if (token == NULL) {
            (void)write_serve_error(STDOUT_FILENO, "empty request");
            continue;
        }

        if (strcmp(token, "quit") == 0) {
            return 0;
        }

        if (strcmp(token, "ping") == 0) {
            (void)write_serve_status(STDOUT_FILENO, KSS_SERVE_STATUS_OK);
            continue;
        }

        if (strcmp(token, "area") != 0) {
            (void)write_serve_error(STDOUT_FILENO, "unknown command");
            continue;
        }

        if (!parse_int_arg(strtok_r(NULL, " \t", &save), &x)
            || !parse_int_arg(strtok_r(NULL, " \t", &save), &y)
            || !parse_int_arg(strtok_r(NULL, " \t", &save), &width)
            || !parse_int_arg(strtok_r(NULL, " \t", &save), &height)
            || width <= 0
            || height <= 0) {
            (void)write_serve_error(STDOUT_FILENO, "bad area coordinates");
            continue;
        }

        if (!write_serve_status(STDOUT_FILENO, KSS_SERVE_STATUS_OK)) {
            return 1;
        }

        if (!capture_area_to_fd(STDOUT_FILENO, x, y, width, height)) {
            /* Header byte already sent. Truncated response on the wire means the
             * parent will fail to parse and recover (or kill us). Without an escape
             * mechanism we have to rely on the parent's read-side framing here. */
            return 1;
        }
    }
}

int main(int argc, char **argv)
{
    int x;
    int y;
    int width;
    int height;
    bool serve_mode = false;
    bool authorize_only = false;
    pid_t requester_pid = getppid();
    uid_t requester_uid;
    gid_t requester_gid;
    kss_trust_result trust;

    if (argc == 2 && strcmp(argv[1], "--diagnose") == 0) {
        if (get_requester_credentials(requester_pid, &requester_uid, &requester_gid)
            && drop_to_requester(requester_uid, requester_gid)) {
            ensure_session_environment(requester_uid);
        }

        print_diagnostics();
        return 0;
    }

    if (argc == 2 && strcmp(argv[1], "--serve") == 0) {
        serve_mode = true;
    } else if (argc == 2 && strcmp(argv[1], "--authorize") == 0) {
        authorize_only = true;
    } else if (argc == 6 && strcmp(argv[1], "--area") == 0
        && parse_int_arg(argv[2], &x)
        && parse_int_arg(argv[3], &y)
        && parse_int_arg(argv[4], &width)
        && parse_int_arg(argv[5], &height)
        && width > 0
        && height > 0) {
        /* one-shot mode, args parsed */
    } else {
        print_usage(argv[0]);
        return KSS_EXIT_USAGE;
    }

    if (!get_requester_credentials(requester_pid, &requester_uid, &requester_gid)) {
        fprintf(stderr, "keysharp-kwin-screencap: failed to get requester credentials\n");
        return KSS_EXIT_ERROR;
    }

    trust = ensure_trusted(requester_pid, requester_uid, requester_gid);

    if (trust != KSS_TRUST_TRUSTED) {
        if (trust == KSS_TRUST_UNAVAILABLE) {
            return KSS_EXIT_UNSUPPORTED;
        }

        fprintf(stderr, "keysharp-kwin-screencap: screen capture permission denied\n");
        return KSS_EXIT_DENIED;
    }

    if (authorize_only) {
        return 0;
    }

    if (!drop_to_requester(requester_uid, requester_gid)) {
        return KSS_EXIT_ERROR;
    }

    ensure_session_environment(requester_uid);

    /* Warm the cached session-bus connection before entering serve_loop so the
     * first request doesn't pay the handshake cost. */
    if (get_session_bus() == NULL) {
        return KSS_EXIT_ERROR;
    }

    if (serve_mode) {
        return serve_loop();
    }

    return capture_area(x, y, width, height) ? 0 : KSS_EXIT_ERROR;
}
