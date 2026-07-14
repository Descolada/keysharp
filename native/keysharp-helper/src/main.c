#include "keysharp_trust/permissions.h"

#include <ctype.h>
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
#define KSS_GNOME_DBUS_NAME "io.github.keysharp.GnomeShell"
#define KSS_GNOME_DBUS_PATH "/io/github/keysharp/GnomeShell"
#define KSS_GNOME_DBUS_INTERFACE "io.github.keysharp.GnomeShell1"
#define KSS_CINNAMON_DBUS_NAME "io.github.keysharp.CinnamonShell"
#define KSS_CINNAMON_DBUS_PATH "/io/github/keysharp/CinnamonShell"
#define KSS_CINNAMON_DBUS_INTERFACE "io.github.keysharp.CinnamonShell1"
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

/* The compositor-extension D-Bus target for the GNOME/Cinnamon capture path. Defaults to the GNOME
 * extension; --serve cinnamon repoints it at the Cinnamon extension. Both expose an identical
 * CaptureArea(iiii)->ay / CaptureWindow(t)->ay, so the same serve loop drives either. */
static const char *g_ext_dbus_name = KSS_GNOME_DBUS_NAME;
static const char *g_ext_dbus_path = KSS_GNOME_DBUS_PATH;
static const char *g_ext_dbus_interface = KSS_GNOME_DBUS_INTERFACE;

static void print_usage(const char *argv0)
{
    fprintf(stderr,
        "Usage: %s --area X Y WIDTH HEIGHT\n"
        "       %s --serve kwin|gnome|cinnamon [--force-prompt]\n"
        "       %s --authorize [--force-prompt]\n"
        "       %s --authorize-pid PID [--force-prompt]\n"
        "       %s --diagnose\n"
        "       %s --trust <list|reset> [options]\n",
        argv0, argv0, argv0, argv0, argv0, argv0);
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

static bool parse_uint64_arg(const char *text, uint64_t *value)
{
    char *end = NULL;
    unsigned long long parsed;

    if (text == NULL || value == NULL) {
        return false;
    }

    errno = 0;
    parsed = strtoull(text, &end, 10);

    if (errno != 0 || end == text || *end != '\0') {
        return false;
    }

    *value = (uint64_t)parsed;
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

static kss_trust_result ensure_trusted(pid_t requester_pid, uid_t requester_uid, gid_t requester_gid, bool force_prompt)
{
    ksi_permission_store *store = NULL;
    char exe_path[KSI_PERMISSION_MAX_PATH];
    char command_line[KSI_PERMISSION_MAX_COMMAND_LINE];
    char exe_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
    /* WILDCARD identity (exe portion only): keys the "Allow for all scripts" grant
     * so screen capture honors it exactly like keysharp-inputd does for input caps. */
    char wildcard_hash[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
    uint32_t allowed;
    uint32_t missing;
    uint32_t denied;
    ksi_permission_decision decision;
    kss_trust_result result = KSS_TRUST_DENIED;

    if (ksi_permissions_create(&store) != 0) {
        fprintf(stderr, "keysharp-helper: failed to open trust store\n");
        return KSS_TRUST_UNAVAILABLE;
    }

    if (ksi_permissions_identify_process(
            requester_pid,
            exe_path,
            sizeof(exe_path),
            command_line,
            sizeof(command_line),
            exe_hash,
            wildcard_hash) != 0) {
        fprintf(stderr, "keysharp-helper: failed to identify requester pid=%ld\n", (long)requester_pid);
        result = KSS_TRUST_UNAVAILABLE;
        goto cleanup;
    }

    /* Grants can be keyed on the per-script identity OR the wildcard (all-scripts)
     * identity — check both, mirroring keysharp-inputd's check_capabilities_sync. */
    allowed = ksi_permissions_get_allowed_capabilities(store, requester_uid, exe_hash);

    if (wildcard_hash[0] != '\0')
        allowed |= ksi_permissions_get_allowed_capabilities(store, requester_uid, wildcard_hash);

    missing = KST_CAP_SCREEN_CAPTURE & ~allowed;

    /* Also check the shared PID-keyed session file.  This is written by any daemon
     * that showed a combined prompt covering screen capture (e.g. keysharp-inputd),
     * allowing subsequent keysharp-helper invocations to skip their own prompt. */
    if (missing != 0u) {
        uint64_t requester_start_time = ksi_permissions_get_process_start_time(requester_pid);
        uint32_t pid_session = ksi_permissions_get_session_by_pid(
            requester_uid, requester_pid, requester_start_time);
        missing &= ~pid_session;
    }

    if (missing == 0u) {
        ksi_permissions_note_seen(store, requester_uid, exe_hash, exe_path);
        result = KSS_TRUST_TRUSTED;
        goto cleanup;
    }

    denied = ksi_permissions_get_denied_capabilities(store, requester_uid, exe_hash) & missing;

    if (!force_prompt && denied != 0u) {
        ksi_permissions_note_seen(store, requester_uid, exe_hash, exe_path);
        goto cleanup;
    }

    if (force_prompt) {
        (void)ksi_permissions_clear_persistent(store, requester_uid, exe_hash, missing);
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
        /* Also write to the shared PID session file so sibling daemons see the grant. */
        {
            uint64_t requester_start_time = ksi_permissions_get_process_start_time(requester_pid);
            (void)ksi_permissions_grant_session_for_pid(
                requester_uid, requester_pid, requester_start_time, KST_CAP_SCREEN_CAPTURE);
        }
    } else if (decision == KSI_PERMISSION_DECISION_ALLOW_ALWAYS) {
        if (ksi_permissions_grant_persistent(store, requester_uid, exe_hash, exe_path, missing) == 0) {
            result = KSS_TRUST_TRUSTED;
        } else {
            fprintf(stderr, "keysharp-helper: failed to update trust store\n");
            result = KSS_TRUST_UNAVAILABLE;
        }
    } else if (decision == KSI_PERMISSION_DECISION_ALLOW_ALL_SCRIPTS) {
        /* "Allow for all scripts": persist under the WILDCARD identity (exe portion
         * only) so the grant covers every script run by this same binary, not just
         * the one that prompted. Falls back to the per-script hash if the wildcard
         * identity is unknown. Mirrors keysharp-inputd's ALLOW_ALL_SCRIPTS handling. */
        const char *grant_hash = wildcard_hash[0] != '\0' ? wildcard_hash : exe_hash;

        if (ksi_permissions_grant_persistent(store, requester_uid, grant_hash, exe_path, missing) == 0) {
            result = KSS_TRUST_TRUSTED;
        } else {
            fprintf(stderr, "keysharp-helper: failed to update trust store\n");
            result = KSS_TRUST_UNAVAILABLE;
        }
    } else if (decision == KSI_PERMISSION_DECISION_PROMPT_UNAVAILABLE) {
        /* No dialog backend was reachable — treat as transient deny so the
         * user gets another chance once a prompt UI becomes available. */
        result = KSS_TRUST_UNAVAILABLE;
    } else {
        /* Deny is SESSION-ONLY: do NOT persist it, so a re-run of the app prompts
         * again (matching the daemon's session-only Deny and the Allow/Deny model).
         * Within this run the C# side caches the denial (HelperClient consent
         * cache) so the helper is not re-invoked. result stays KSS_TRUST_DENIED. */
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
        fprintf(stderr, "keysharp-helper: failed to drop privileges: %s\n", strerror(errno));
        return false;
    }

    if (prctl(PR_SET_DUMPABLE, 1, 0, 0, 0) != 0) {
        fprintf(stderr, "keysharp-helper: failed to make process inspectable: %s\n", strerror(errno));
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
    char path[] = "/tmp/keysharp-helper-XXXXXX";
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

            fprintf(stderr, "keysharp-helper: write failed during %s: %s\n",
                context != NULL ? context : "output", strerror(errno));
            return false;
        }

        if (written == 0) {
            fprintf(stderr, "keysharp-helper: short write during %s\n",
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

            fprintf(stderr, "keysharp-helper: failed to read capture data: %s\n", strerror(errno));
            return false;
        }

        if (bytes_read == 0) {
            fprintf(stderr, "keysharp-helper: capture data ended early with %llu bytes remaining\n",
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
        fprintf(stderr, "keysharp-helper: failed to get monotonic clock: %s\n", strerror(errno));
        return false;
    }

    for (;;) {
        struct stat info;
        struct timespec now;
        int64_t elapsed_ms;

        if (fstat(fd, &info) != 0) {
            fprintf(stderr, "keysharp-helper: failed to stat capture file: %s\n", strerror(errno));
            return false;
        }

        if (info.st_size >= 0 && (uint64_t)info.st_size >= expected_bytes) {
            return true;
        }

        if (clock_gettime(CLOCK_MONOTONIC, &now) != 0) {
            fprintf(stderr, "keysharp-helper: failed to get monotonic clock: %s\n", strerror(errno));
            return false;
        }

        elapsed_ms = ((int64_t)(now.tv_sec - start.tv_sec) * 1000LL)
                     + ((int64_t)(now.tv_nsec - start.tv_nsec) / 1000000LL);

        if (elapsed_ms >= KSS_CAPTURE_FILE_READY_TIMEOUT_MS) {
            fprintf(stderr,
                "keysharp-helper: capture file reached only %lld of %llu bytes after %d ms\n",
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
        fprintf(stderr, "keysharp-helper: KWin returned an empty capture (%ux%u stride=%u format=%u)\n",
            width, height, stride, format);
        return false;
    }

    if (!wait_for_capture_file_size(image_fd, bytes)) {
        return false;
    }

    if (lseek(image_fd, 0, SEEK_SET) < 0) {
        fprintf(stderr, "keysharp-helper: failed to rewind capture file: %s\n", strerror(errno));
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
        fprintf(stderr, "keysharp-helper: failed to connect to session bus: %s\n",
            error != NULL ? error->message : "unknown error");

        if (error != NULL) {
            g_error_free(error);
        }
    }

    return g_serve_connection;
}

/* Defined further down; forward-declared so the KWin capture functions below can own the status byte
 * (write OK + frame on success, or a recoverable error frame on failure) like the GNOME serve path. */
static bool write_serve_status(int out_fd, uint8_t status);
static bool write_serve_error(int out_fd, const char *message);

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
    bool captured = false;
    const char *errmsg = "KWin CaptureArea failed";
    bool io_ok;

    image_fd = create_capture_file();

    if (image_fd < 0) {
        fprintf(stderr, "keysharp-helper: failed to create capture file: %s\n", strerror(errno));
        return write_serve_error(out_fd, "failed to create capture file");
    }

    connection = get_session_bus();

    if (connection == NULL) {
        errmsg = "failed to connect to session bus";
        goto cleanup;
    }

    fd_list = g_unix_fd_list_new();
    fd_handle = g_unix_fd_list_append(fd_list, image_fd, &error);

    if (fd_handle < 0) {
        fprintf(stderr, "keysharp-helper: failed to append fd: %s\n",
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
        fprintf(stderr, "keysharp-helper: KWin CaptureArea failed: %s\n",
            error != NULL ? error->message : "unknown error");
        goto cleanup;
    }

    g_variant_get(reply, "(@a{sv})", &results);

    if (!g_variant_lookup(results, "type", "s", &type)) {
        gchar *printed = g_variant_print(results, TRUE);
        fprintf(stderr, "keysharp-helper: KWin capture result missing type: %s\n",
            printed != NULL ? printed : "(unprintable)");
        g_free(printed);
        goto cleanup;
    }

    if (strcmp(type, "raw") != 0) {
        fprintf(stderr, "keysharp-helper: unsupported KWin capture type '%s'\n", type);
        goto cleanup;
    }

    if (!g_variant_lookup(results, "width", "u", &result_width)
        || !g_variant_lookup(results, "height", "u", &result_height)
        || !g_variant_lookup(results, "stride", "u", &stride)
        || !g_variant_lookup(results, "format", "u", &format)) {
        gchar *printed = g_variant_print(results, TRUE);
        fprintf(stderr, "keysharp-helper: KWin raw capture metadata incomplete: %s\n",
            printed != NULL ? printed : "(unprintable)");
        g_free(printed);
        goto cleanup;
    }

    captured = true;

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

    /* Own the status byte here (like the GNOME serve path) rather than letting serve_loop pre-send OK:
     * a D-Bus/metadata failure above wrote nothing to out_fd, so report it as a recoverable per-request
     * error and keep the long-lived helper alive. Only commit OK once we are about to stream the frame. */
    if (captured) {
        io_ok = write_serve_status(out_fd, KSS_SERVE_STATUS_OK)
            && write_capture_response(out_fd, image_fd, result_width, result_height, stride, format);

        if (!io_ok) {
            fprintf(stderr, "keysharp-helper: failed to stream capture (%ux%u stride=%u format=%u)\n",
                result_width, result_height, stride, format);
        }
    } else {
        io_ok = write_serve_error(out_fd, errmsg);
    }

    if (image_fd >= 0) {
        close(image_fd);
    }

    return io_ok;
}

/* Captures a single window via KWin ScreenShot2.CaptureWindow(handle) and writes the framed KSSC1
 * response (header + raw pixels) to out_fd. `handle` is the window's KWin internalId UUID string
 * (e.g. "{xxxxxxxx-....}"). Occlusion-independent: KWin re-renders the window off-screen. Mirrors
 * capture_area_to_fd, differing only in the D-Bus method and its arguments. */
static bool capture_window_to_fd(int out_fd, const char *handle, bool include_decoration)
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
    bool captured = false;
    const char *errmsg = "KWin CaptureWindow failed";
    bool io_ok;

    image_fd = create_capture_file();

    if (image_fd < 0) {
        fprintf(stderr, "keysharp-helper: failed to create capture file: %s\n", strerror(errno));
        return write_serve_error(out_fd, "failed to create capture file");
    }

    connection = get_session_bus();

    if (connection == NULL) {
        errmsg = "failed to connect to session bus";
        goto cleanup;
    }

    fd_list = g_unix_fd_list_new();
    fd_handle = g_unix_fd_list_append(fd_list, image_fd, &error);

    if (fd_handle < 0) {
        fprintf(stderr, "keysharp-helper: failed to append fd: %s\n",
            error != NULL ? error->message : "unknown error");
        goto cleanup;
    }

    g_variant_builder_init(&options, G_VARIANT_TYPE_VARDICT);
    g_variant_builder_add(&options, "{sv}", "include-cursor", g_variant_new_boolean(FALSE));
    g_variant_builder_add(&options, "{sv}", "native-resolution", g_variant_new_boolean(FALSE));
    /* Title bar + borders. Including them makes the captured image frame-aligned (its top-left is the
     * window's outer top-left), matching how Windows/macOS capture a whole window, and lets the caller
     * map result coordinates back to screen with the frame origin. KWin defaults this to FALSE (client
     * area only), which would shift those coordinates up-left by the decoration size. */
    g_variant_builder_add(&options, "{sv}", "include-decoration", g_variant_new_boolean(include_decoration ? TRUE : FALSE));

    reply = g_dbus_connection_call_with_unix_fd_list_sync(
        connection,
        KSS_DBUS_NAME,
        KSS_DBUS_PATH,
        KSS_DBUS_INTERFACE,
        "CaptureWindow",
        g_variant_new("(sa{sv}h)", handle, &options, fd_handle),
        G_VARIANT_TYPE("(a{sv})"),
        G_DBUS_CALL_FLAGS_NONE,
        KSS_CAPTURE_TIMEOUT_MS,
        fd_list,
        NULL,
        NULL,
        &error);

    if (reply == NULL) {
        fprintf(stderr, "keysharp-helper: KWin CaptureWindow failed: %s\n",
            error != NULL ? error->message : "unknown error");
        goto cleanup;
    }

    g_variant_get(reply, "(@a{sv})", &results);

    if (!g_variant_lookup(results, "type", "s", &type)) {
        gchar *printed = g_variant_print(results, TRUE);
        fprintf(stderr, "keysharp-helper: KWin capture result missing type: %s\n",
            printed != NULL ? printed : "(unprintable)");
        g_free(printed);
        goto cleanup;
    }

    if (strcmp(type, "raw") != 0) {
        fprintf(stderr, "keysharp-helper: unsupported KWin capture type '%s'\n", type);
        goto cleanup;
    }

    if (!g_variant_lookup(results, "width", "u", &result_width)
        || !g_variant_lookup(results, "height", "u", &result_height)
        || !g_variant_lookup(results, "stride", "u", &stride)
        || !g_variant_lookup(results, "format", "u", &format)) {
        gchar *printed = g_variant_print(results, TRUE);
        fprintf(stderr, "keysharp-helper: KWin raw capture metadata incomplete: %s\n",
            printed != NULL ? printed : "(unprintable)");
        g_free(printed);
        goto cleanup;
    }

    captured = true;

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

    if (error != NULL) {
        g_error_free(error);
    }

    /* Own the status byte here (like the GNOME serve path): an early D-Bus/metadata failure wrote nothing
     * to out_fd, so report it as a recoverable per-request error and keep the helper alive. A window that
     * vanished mid-capture no longer tears the helper down (and forces a costly restart). */
    if (captured) {
        io_ok = write_serve_status(out_fd, KSS_SERVE_STATUS_OK)
            && write_capture_response(out_fd, image_fd, result_width, result_height, stride, format);

        if (!io_ok) {
            fprintf(stderr, "keysharp-helper: failed to stream window capture (%ux%u stride=%u format=%u)\n",
                result_width, result_height, stride, format);
        }
    } else {
        io_ok = write_serve_error(out_fd, errmsg);
    }

    if (image_fd >= 0) {
        close(image_fd);
    }

    return io_ok;
}

static bool gnome_capture_area_to_fd(int out_fd, int x, int y, int width, int height);

static bool is_dbus_name_available(GDBusConnection *connection, const char *name)
{
    GError *error = NULL;
    GVariant *result;
    gboolean has_owner = FALSE;

    result = g_dbus_connection_call_sync(
        connection,
        "org.freedesktop.DBus",
        "/org/freedesktop/DBus",
        "org.freedesktop.DBus",
        "NameHasOwner",
        g_variant_new("(s)", name),
        G_VARIANT_TYPE("(b)"),
        G_DBUS_CALL_FLAGS_NONE,
        5000,
        NULL,
        &error);

    if (result == NULL) {
        if (error != NULL) {
            g_error_free(error);
        }
        return false;
    }

    g_variant_get(result, "(b)", &has_owner);
    g_variant_unref(result);
    return (bool)has_owner;
}

static bool capture_area(int x, int y, int width, int height)
{
    GDBusConnection *connection = get_session_bus();

    if (connection == NULL) {
        return false;
    }

    if (is_dbus_name_available(connection, KSS_DBUS_NAME)) {
        return capture_area_to_fd(STDOUT_FILENO, x, y, width, height);
    }

    if (is_dbus_name_available(connection, KSS_GNOME_DBUS_NAME)) {
        return gnome_capture_area_to_fd(STDOUT_FILENO, x, y, width, height);
    }

    fprintf(stderr, "keysharp-helper: no supported Wayland compositor found (tried KWin and GNOME)\n");
    return false;
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

/* Calls the GNOME Shell extension's CaptureArea and writes a KSSG1-framed response
 * (8-byte magic + uint64 PNG byte count + PNG data) to out_fd.
 * The extension captures via Shell.Screenshot — no KWin D-Bus involved. */
static bool gnome_capture_area_to_fd(int out_fd, int x, int y, int width, int height)
{
    GError *error = NULL;
    GDBusConnection *connection = NULL;
    GVariant *reply = NULL;
    GVariant *png_variant = NULL;
    const uint8_t *png_data = NULL;
    gsize png_size = 0;
    char magic[8] = { 'K', 'S', 'S', 'G', '1', '\0', '\0', '\0' };
    uint64_t byte_count;
    bool success = false;

    connection = get_session_bus();

    if (connection == NULL) {
        goto cleanup;
    }

    reply = g_dbus_connection_call_sync(
        connection,
        g_ext_dbus_name,
        g_ext_dbus_path,
        g_ext_dbus_interface,
        "CaptureArea",
        g_variant_new("(iiii)", x, y, width, height),
        G_VARIANT_TYPE("(ay)"),
        G_DBUS_CALL_FLAGS_NONE,
        KSS_CAPTURE_TIMEOUT_MS,
        NULL,
        &error);

    if (reply == NULL) {
        fprintf(stderr, "keysharp-helper: GNOME CaptureArea failed: %s\n",
            error != NULL ? error->message : "unknown error");
        goto cleanup;
    }

    g_variant_get(reply, "(@ay)", &png_variant);
    png_data = (const uint8_t *)g_variant_get_fixed_array(png_variant, &png_size, sizeof(uint8_t));

    if (png_data == NULL || png_size == 0) {
        fprintf(stderr, "keysharp-helper: GNOME CaptureArea returned empty PNG\n");
        goto cleanup;
    }

    byte_count = (uint64_t)png_size;
    success = write_exact(out_fd, magic, sizeof(magic), "KSSG1 magic")
        && write_exact(out_fd, &byte_count, sizeof(byte_count), "KSSG1 byte count")
        && write_exact(out_fd, png_data, png_size, "KSSG1 PNG data");

cleanup:
    if (png_variant != NULL) {
        g_variant_unref(png_variant);
    }

    if (reply != NULL) {
        g_variant_unref(reply);
    }

    if (error != NULL) {
        g_error_free(error);
    }

    return success;
}

/* Single GNOME serve-mode request: capture and write the complete response to
 * out_fd as either [status-OK][KSSG1 frame] or [status-ERR][length][message].
 * Returns true if a complete response was written (loop can continue).
 * Returns false only on a write failure (broken pipe — loop should exit).
 *
 * Writing the status byte after the capture result is known avoids the
 * desync that occurred when status-OK was written first and the capture then
 * failed: the C# reader was left waiting for a KSSG1 header that never came,
 * producing a 30-second timeout before it could recover. */
static bool gnome_serve_one(int out_fd, int x, int y, int width, int height)
{
    GError *error = NULL;
    GDBusConnection *connection = NULL;
    GVariant *reply = NULL;
    GVariant *png_variant = NULL;
    const uint8_t *png_data = NULL;
    gsize png_size = 0;
    bool io_ok;

    connection = get_session_bus();

    if (connection == NULL) {
        io_ok = write_serve_error(out_fd, "failed to connect to session bus");
        goto done;
    }

    reply = g_dbus_connection_call_sync(
        connection,
        g_ext_dbus_name,
        g_ext_dbus_path,
        g_ext_dbus_interface,
        "CaptureArea",
        g_variant_new("(iiii)", x, y, width, height),
        G_VARIANT_TYPE("(ay)"),
        G_DBUS_CALL_FLAGS_NONE,
        KSS_CAPTURE_TIMEOUT_MS,
        NULL,
        &error);

    if (reply == NULL) {
        fprintf(stderr, "keysharp-helper: GNOME CaptureArea failed: %s\n",
            error != NULL ? error->message : "unknown error");
        io_ok = write_serve_error(out_fd, "GNOME CaptureArea failed");
        goto cleanup;
    }

    g_variant_get(reply, "(@ay)", &png_variant);
    png_data = (const uint8_t *)g_variant_get_fixed_array(png_variant, &png_size, sizeof(uint8_t));

    if (png_data == NULL || png_size == 0) {
        fprintf(stderr, "keysharp-helper: GNOME CaptureArea returned empty PNG\n");
        io_ok = write_serve_error(out_fd, "GNOME CaptureArea returned empty data");
        goto cleanup;
    }

    {
        char magic[8] = { 'K', 'S', 'S', 'G', '1', '\0', '\0', '\0' };
        uint64_t byte_count = (uint64_t)png_size;
        io_ok = write_serve_status(out_fd, KSS_SERVE_STATUS_OK)
            && write_exact(out_fd, magic, sizeof(magic), "KSSG1 magic")
            && write_exact(out_fd, &byte_count, sizeof(byte_count), "KSSG1 byte count")
            && write_exact(out_fd, png_data, png_size, "KSSG1 PNG data");
    }

cleanup:
    if (png_variant != NULL)
        g_variant_unref(png_variant);

    if (reply != NULL)
        g_variant_unref(reply);

done:
    if (error != NULL)
        g_error_free(error);

    return io_ok;
}

/* Single GNOME window-capture request: calls the extension's CaptureWindow (which images the
 * window actor's own buffer via meta_window_actor_get_image, so occluded windows still capture)
 * and writes a status-prefixed KSSG1 (PNG) frame — the same wire format as the area path, so the
 * C# reader is identical. handle is the window's stable_sequence. */
static bool gnome_window_serve_one(int out_fd, uint64_t handle)
{
    GError *error = NULL;
    GDBusConnection *connection = NULL;
    GVariant *reply = NULL;
    GVariant *png_variant = NULL;
    const uint8_t *png_data = NULL;
    gsize png_size = 0;
    bool io_ok;

    connection = get_session_bus();

    if (connection == NULL) {
        io_ok = write_serve_error(out_fd, "failed to connect to session bus");
        goto done;
    }

    reply = g_dbus_connection_call_sync(
        connection,
        g_ext_dbus_name,
        g_ext_dbus_path,
        g_ext_dbus_interface,
        "CaptureWindow",
        g_variant_new("(t)", handle),
        G_VARIANT_TYPE("(ay)"),
        G_DBUS_CALL_FLAGS_NONE,
        KSS_CAPTURE_TIMEOUT_MS,
        NULL,
        &error);

    if (reply == NULL) {
        fprintf(stderr, "keysharp-helper: GNOME CaptureWindow failed: %s\n",
            error != NULL ? error->message : "unknown error");
        io_ok = write_serve_error(out_fd, "GNOME CaptureWindow failed");
        goto cleanup;
    }

    g_variant_get(reply, "(@ay)", &png_variant);
    png_data = (const uint8_t *)g_variant_get_fixed_array(png_variant, &png_size, sizeof(uint8_t));

    if (png_data == NULL || png_size == 0) {
        /* Empty result: unknown handle, or a minimized window with no live texture. The C# side
         * treats a failure here as "no true capture" and falls back to a rectangle grab. */
        io_ok = write_serve_error(out_fd, "GNOME CaptureWindow returned empty data");
        goto cleanup;
    }

    {
        char magic[8] = { 'K', 'S', 'S', 'G', '1', '\0', '\0', '\0' };
        uint64_t byte_count = (uint64_t)png_size;
        io_ok = write_serve_status(out_fd, KSS_SERVE_STATUS_OK)
            && write_exact(out_fd, magic, sizeof(magic), "KSSG1 magic")
            && write_exact(out_fd, &byte_count, sizeof(byte_count), "KSSG1 byte count")
            && write_exact(out_fd, png_data, png_size, "KSSG1 PNG data");
    }

cleanup:
    if (png_variant != NULL)
        g_variant_unref(png_variant);

    if (reply != NULL)
        g_variant_unref(reply);

done:
    if (error != NULL)
        g_error_free(error);

    return io_ok;
}

/* GNOME serve loop — identical request protocol to serve_loop but calls the GNOME
 * Shell extension instead of KWin, and emits KSSG1 (PNG) frames instead of KSSC1. */
static int gnome_serve_loop(void)
{
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

        if (strcmp(token, "window") == 0) {
            uint64_t handle;

            if (!parse_uint64_arg(strtok_r(NULL, " \t", &save), &handle)) {
                (void)write_serve_error(STDOUT_FILENO, "bad window handle");
                continue;
            }

            if (!gnome_window_serve_one(STDOUT_FILENO, handle)) {
                return 1;
            }

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

        if (!gnome_serve_one(STDOUT_FILENO, x, y, width, height)) {
            return 1;
        }
    }
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

        if (strcmp(token, "window") == 0) {
            char *handle = strtok_r(NULL, " \t", &save);
            char *deco_token;
            bool include_decoration;

            if (handle == NULL || handle[0] == '\0') {
                (void)write_serve_error(STDOUT_FILENO, "bad window handle");
                continue;
            }

            /* Optional trailing flag: "0" excludes the title bar/borders, anything else (or absent)
             * includes them. Default-on keeps results frame-aligned for callers that don't pass it. */
            deco_token = strtok_r(NULL, " \t", &save);
            include_decoration = (deco_token == NULL) || (deco_token[0] != '0');

            /* capture_window_to_fd owns the status byte: OK + frame on success, or a recoverable error
             * frame on capture failure, returning false only on a fatal stdout I/O error (parent gone).
             * So a per-capture failure (e.g. the window vanished) no longer tears down the helper. */
            if (!capture_window_to_fd(STDOUT_FILENO, handle, include_decoration)) {
                return 1;
            }

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

        /* capture_area_to_fd owns the status byte (OK + frame, or a recoverable error frame), returning
         * false only on a fatal stdout I/O error, so a failed capture keeps the helper alive. */
        if (!capture_area_to_fd(STDOUT_FILENO, x, y, width, height)) {
            return 1;
        }
    }
}

/* ---- trust subcommand (--trust list | --trust reset ...) ---------------
 * Uses the library directly (this binary is setuid root) and is scoped to
 * KST_CAP_SCREEN_CAPTURE. Uses the real caller UID for record scoping. */

static void kss_describe_capabilities(uint32_t bits, char *buffer, size_t buffer_size)
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

static void kss_format_timestamp(uint64_t utc_seconds, char *buffer, size_t buffer_size)
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

typedef struct {
    bool any;
} kss_list_ctx;

static bool kss_print_entry(const ksi_permission_entry *entry, void *user_data)
{
    kss_list_ctx *ctx = (kss_list_ctx *)user_data;
    char allow_text[256];
    char deny_text[256];
    char when_text[64];

    /* Only show records that have a screen-capture decision. */
    if ((entry->persistent_allowed_capabilities & KST_CAP_SCREEN_CAPTURE) == 0u
        && (entry->persistent_denied_capabilities & KST_CAP_SCREEN_CAPTURE) == 0u) {
        return true;
    }

    kss_describe_capabilities(entry->persistent_allowed_capabilities, allow_text, sizeof(allow_text));
    kss_describe_capabilities(entry->persistent_denied_capabilities, deny_text, sizeof(deny_text));
    kss_format_timestamp(entry->last_seen_utc, when_text, sizeof(when_text));

    if (ctx->any) {
        printf("\n");
    }

    printf("uid:        %u\n", (unsigned int)entry->uid);
    printf("hash:       %s\n", entry->exe_hash);
    printf("exe:        %s\n",
        entry->exe_path != NULL && entry->exe_path[0] != '\0'
            ? entry->exe_path : "(unknown)");
    printf("allowed:    %s\n", allow_text);
    printf("denied:     %s\n", deny_text);
    printf("last seen:  %s\n", when_text);

    ctx->any = true;
    return true;
}

static int kss_parse_caps_hex(const char *text, uint32_t *out)
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

static void trust_usage(FILE *stream, const char *argv0)
{
    fprintf(stream,
        "Usage: %s --trust list\n"
        "       %s --trust reset <hash> [--caps <mask>]\n"
        "       %s --trust reset --pid <pid> [--caps <mask>]\n"
        "       %s --trust reset --all [--caps <mask>]\n"
        "\n"
        "Capabilities are a hex bitmask (default: 0x%02x = screen-capture).\n",
        argv0, argv0, argv0, argv0, KST_CAP_SCREEN_CAPTURE);
}

static int trust_main(int argc, char **argv, const char *argv0)
{
    ksi_permission_store *store = NULL;
    uid_t caller_uid = getuid();
    int rc = 1;

    if (argc < 1 || strcmp(argv[0], "-h") == 0 || strcmp(argv[0], "--help") == 0) {
        trust_usage(argc < 1 ? stderr : stdout, argv0);
        return argc < 1 ? 2 : 0;
    }

    if (ksi_permissions_create(&store) != 0) {
        fprintf(stderr, "keysharp-helper: failed to open trust store\n");
        return 1;
    }

    if (strcmp(argv[0], "list") == 0) {
        uid_t filter = (caller_uid == 0) ? (uid_t)-1 : caller_uid;
        kss_list_ctx ctx = { false };

        ksi_permissions_for_each(store, filter, kss_print_entry, &ctx);

        if (!ctx.any) {
            printf("No stored screen-capture permissions%s.\n",
                caller_uid != 0 ? " for this user" : "");
        }

        rc = 0;
    } else if (strcmp(argv[0], "reset") == 0) {
        uint32_t capabilities = KST_CAP_SCREEN_CAPTURE;
        const char *hash_arg = NULL;
        pid_t pid_arg = 0;
        bool all = false;
        char hash_buf[KSI_PERMISSION_HASH_HEX_LENGTH + 1u];
        int i;

        for (i = 1; i < argc; i++) {
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
                    fprintf(stderr, "keysharp-helper: invalid --pid value\n");
                    goto done;
                }

                pid_arg = (pid_t)value;
                continue;
            }

            if (strcmp(arg, "--caps") == 0 && i + 1 < argc) {
                if (kss_parse_caps_hex(argv[++i], &capabilities) != 0) {
                    fprintf(stderr, "keysharp-helper: invalid --caps value\n");
                    goto done;
                }

                continue;
            }

            if (arg[0] == '-') {
                fprintf(stderr, "keysharp-helper: unknown trust option %s\n", arg);
                goto done;
            }

            if (hash_arg != NULL) {
                fprintf(stderr, "keysharp-helper: too many arguments\n");
                goto done;
            }

            hash_arg = arg;
        }

        if (all && (hash_arg != NULL || pid_arg != 0)) {
            fprintf(stderr, "keysharp-helper: --all cannot be combined with a hash or --pid\n");
            goto done;
        }

        if (!all && hash_arg == NULL && pid_arg == 0) {
            fprintf(stderr, "keysharp-helper: provide a hash, --pid <pid>, or --all\n");
            goto done;
        }

        /* Clamp to screen-capture domain. */
        capabilities &= KST_CAP_SCREEN_CAPTURE;

        if (capabilities == 0u) {
            fprintf(stderr, "keysharp-helper: --caps mask has no screen-capture bits\n");
            goto done;
        }

        if (pid_arg != 0) {
            char path[KSI_PERMISSION_MAX_PATH];
            char command_line[KSI_PERMISSION_MAX_COMMAND_LINE];

            if (ksi_permissions_identify_process(
                    pid_arg,
                    path, sizeof(path),
                    command_line, sizeof(command_line),
                    hash_buf,
                    NULL) != 0) {
                fprintf(stderr, "keysharp-helper: cannot identify pid %ld\n", (long)pid_arg);
                goto done;
            }

            hash_arg = hash_buf;
        } else if (!all) {
            size_t hash_length = strlen(hash_arg);
            size_t j;

            if (hash_length != KSI_PERMISSION_HASH_HEX_LENGTH) {
                fprintf(stderr, "keysharp-helper: hash must be %u hex characters\n",
                    (unsigned int)KSI_PERMISSION_HASH_HEX_LENGTH);
                goto done;
            }

            for (j = 0; j < hash_length; j++) {
                if (!isxdigit((unsigned char)hash_arg[j])) {
                    fprintf(stderr, "keysharp-helper: hash contains non-hex characters\n");
                    goto done;
                }
            }
        }

        if (all) {
            uid_t target = (caller_uid == 0) ? (uid_t)-1 : caller_uid;

            if (ksi_permissions_clear_all(store, target, capabilities) == 0) {
                printf("Cleared screen-capture permissions%s.\n",
                    caller_uid != 0 ? " for this user" : "");
                rc = 0;
            } else {
                fprintf(stderr, "keysharp-helper: failed to clear permissions\n");
            }
        } else {
            if (ksi_permissions_clear_persistent(store, caller_uid, hash_arg, capabilities) == 0) {
                printf("Cleared screen-capture permissions for %s.\n", hash_arg);
                printf("The next Keysharp request from this process will re-prompt.\n");
                rc = 0;
            } else {
                fprintf(stderr, "keysharp-helper: failed to clear permissions\n");
            }
        }
    } else {
        fprintf(stderr, "keysharp-helper: unknown trust command '%s'\n", argv[0]);
        trust_usage(stderr, argv0);
        rc = 2;
    }

done:
    ksi_permissions_destroy(store);
    return rc;
}

/* ----------------------------------------------------------------------- */

int main(int argc, char **argv)
{
    int x;
    int y;
    int width;
    int height;
    bool serve_mode = false;
    bool gnome_backend = false;
    bool authorize_only = false;
    bool force_prompt = false;
    pid_t requester_pid = getppid();
    uid_t requester_uid;
    gid_t requester_gid;
    kss_trust_result trust;

    if (argc >= 2 && strcmp(argv[1], "--trust") == 0) {
        return trust_main(argc - 2, argv + 2, argv[0]);
    }

    if (argc == 2 && strcmp(argv[1], "--diagnose") == 0) {
        if (get_requester_credentials(requester_pid, &requester_uid, &requester_gid)
            && drop_to_requester(requester_uid, requester_gid)) {
            ensure_session_environment(requester_uid);
        }

        print_diagnostics();
        return 0;
    }

    if ((argc == 3 || argc == 4) && strcmp(argv[1], "--serve") == 0) {
        if (strcmp(argv[2], "kwin") == 0) {
            serve_mode = true;
        } else if (strcmp(argv[2], "gnome") == 0) {
            serve_mode = true;
            gnome_backend = true;
        } else if (strcmp(argv[2], "cinnamon") == 0) {
            /* Same extension serve loop and CaptureArea protocol as GNOME; only the D-Bus target differs. */
            serve_mode = true;
            gnome_backend = true;
            g_ext_dbus_name = KSS_CINNAMON_DBUS_NAME;
            g_ext_dbus_path = KSS_CINNAMON_DBUS_PATH;
            g_ext_dbus_interface = KSS_CINNAMON_DBUS_INTERFACE;
        } else {
            print_usage(argv[0]);
            return KSS_EXIT_USAGE;
        }

        if (argc == 4) {
            if (strcmp(argv[3], "--force-prompt") != 0) {
                print_usage(argv[0]);
                return KSS_EXIT_USAGE;
            }

            force_prompt = true;
        }
    } else if ((argc == 2 || argc == 3) && strcmp(argv[1], "--authorize") == 0) {
        authorize_only = true;

        if (argc == 3) {
            if (strcmp(argv[2], "--force-prompt") != 0) {
                print_usage(argv[0]);
                return KSS_EXIT_USAGE;
            }

            force_prompt = true;
        }
    } else if ((argc == 3 || argc == 4) && strcmp(argv[1], "--authorize-pid") == 0) {
        /* Trust check against a caller-specified PID instead of getppid(). Used by the
         * GNOME Shell extension, where the helper's parent is gnome-shell, not the
         * requesting Keysharp process. The PID must be resolved by a trusted intermediary
         * (e.g. the D-Bus daemon via GetConnectionUnixProcessID) — never pass through a
         * value supplied directly by the requester. The trust check itself reads
         * /proc/<pid>/exe to identify the binary, so even a spoofed PID can only escalate
         * to whichever exe that PID currently runs. */
        int pid_arg;

        authorize_only = true;

        if (!parse_int_arg(argv[2], &pid_arg) || pid_arg <= 0) {
            print_usage(argv[0]);
            return KSS_EXIT_USAGE;
        }

        requester_pid = (pid_t)pid_arg;

        if (argc == 4) {
            if (strcmp(argv[3], "--force-prompt") != 0) {
                print_usage(argv[0]);
                return KSS_EXIT_USAGE;
            }

            force_prompt = true;
        }
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
        fprintf(stderr, "keysharp-helper: failed to get requester credentials\n");
        return KSS_EXIT_ERROR;
    }

    trust = ensure_trusted(requester_pid, requester_uid, requester_gid, force_prompt);

    if (trust != KSS_TRUST_TRUSTED) {
        if (trust == KSS_TRUST_UNAVAILABLE) {
            if (serve_mode) {
                (void)write_serve_error(STDOUT_FILENO, "screen capture authorization unavailable");
            }

            return KSS_EXIT_UNSUPPORTED;
        }

        fprintf(stderr, "keysharp-helper: screen capture permission denied\n");

        if (serve_mode) {
            (void)write_serve_error(STDOUT_FILENO, "screen capture permission denied");
        }

        return KSS_EXIT_DENIED;
    }

    if (authorize_only) {
        return 0;
    }

    if (!drop_to_requester(requester_uid, requester_gid)) {
        return KSS_EXIT_ERROR;
    }

    ensure_session_environment(requester_uid);

    if (get_session_bus() == NULL) {
        if (serve_mode) {
            (void)write_serve_error(STDOUT_FILENO, "failed to connect to the session bus");
        }

        return KSS_EXIT_ERROR;
    }

    if (serve_mode) {
        if (!write_serve_status(STDOUT_FILENO, KSS_SERVE_STATUS_OK)) {
            return KSS_EXIT_ERROR;
        }

        return gnome_backend ? gnome_serve_loop() : serve_loop();
    }

    return capture_area(x, y, width, height) ? 0 : KSS_EXIT_ERROR;
}
