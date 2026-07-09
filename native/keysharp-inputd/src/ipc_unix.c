#include "keysharp_inputd/ipc.h"
#include "util.h"

#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/file.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

struct ksi_ipc_server {
    int fd;
    int lock_fd;
    char *socket_path;
};

/* Held for the process lifetime (released automatically on close/exit, so it
 * can never strand a stale lock across a crash) to stop a second instance
 * from silently stealing this instance's socket path out from under it: the
 * old unlink()+bind() sequence below has no "am I alone" check at all, so a
 * manually-launched second `keysharp-inputd --foreground`/`--socket ...`
 * hitting the same path would unlink the live listener's directory entry and
 * bind its own -- the first instance keeps running and accepting on its
 * already-open fd, but new connections silently go to the second instance,
 * and BOTH independently try to EVIOCGRAB devices and create their own
 * uinput virtual devices. Only guards this manual/debug entry point:
 * ksi_ipc_server_from_fd() (the --system-service / systemd socket-activation
 * path the production C# client always uses) needs no such guard --
 * systemd's own unit-instance tracking already prevents two concurrently
 * "active" processes under the same unit name. */
static int acquire_single_instance_lock(const char *socket_path)
{
    char lock_path[PATH_MAX];
    int fd;

    if (snprintf(lock_path, sizeof(lock_path), "%s.lock", socket_path) >= (int)sizeof(lock_path)) {
        fprintf(stderr, "warning: socket path too long to derive a lock file path; "
            "skipping single-instance check for %s\n", socket_path);
        return -1;
    }

    fd = open(lock_path, O_CREAT | O_RDWR | O_CLOEXEC, S_IRUSR | S_IWUSR);

    if (fd < 0) {
        /* Best-effort: don't block a debug/manual launch just because the
         * lock file itself couldn't be created (e.g. an unusual filesystem or
         * permissions setup for a custom --socket path). */
        fprintf(stderr, "warning: cannot open %s: %s; skipping single-instance check\n",
            lock_path, strerror(errno));
        return -1;
    }

    if (flock(fd, LOCK_EX | LOCK_NB) != 0) {
        if (errno == EWOULDBLOCK) {
            fprintf(stderr,
                "another keysharp-inputd instance already holds %s; refusing to start "
                "(starting anyway would silently steal the live socket at %s out from "
                "under the running instance)\n",
                lock_path, socket_path);
        } else {
            fprintf(stderr, "warning: flock(%s) failed: %s; skipping single-instance check\n",
                lock_path, strerror(errno));
            return -1;
        }

        close(fd);
        return errno == EWOULDBLOCK ? -2 : -1;
    }

    return fd;
}

static int validate_socket_path(const char *socket_path)
{
    struct sockaddr_un address;

    if (socket_path == NULL || socket_path[0] == '\0') {
        fprintf(stderr, "socket path is empty\n");
        return -1;
    }

    if (strlen(socket_path) >= sizeof(address.sun_path)) {
        fprintf(stderr, "socket path is too long: %s\n", socket_path);
        return -1;
    }

    return 0;
}

/* Frees a partially-built server on any of ksi_ipc_server_open()'s failure
 * paths, releasing the single-instance lock (if one was acquired) along with
 * everything else so a failed open never leaks it. */
static void abandon_ipc_server(ksi_ipc_server *created, int fd, const char *socket_path, bool unlink_socket)
{
    if (fd >= 0) {
        close(fd);
    }

    if (unlink_socket) {
        (void)unlink(socket_path);
    }

    if (created->lock_fd >= 0) {
        close(created->lock_fd);
    }

    free(created);
}

int ksi_ipc_server_open(const char *socket_path, ksi_ipc_server **server)
{
    struct sockaddr_un address;
    ksi_ipc_server *created = NULL;
    int fd = -1;
    int lock_result;

    if (server == NULL || validate_socket_path(socket_path) != 0) {
        return -1;
    }

    created = calloc(1, sizeof(*created));

    if (created == NULL) {
        fprintf(stderr, "failed to allocate IPC server\n");
        return -1;
    }

    created->lock_fd = -1;

    lock_result = acquire_single_instance_lock(socket_path);

    if (lock_result == -2) {
        /* A live instance holds the lock: bail out before touching the
         * socket path at all (no unlink, no bind) so the running instance is
         * completely undisturbed. */
        free(created);
        return -1;
    }

    if (lock_result >= 0) {
        created->lock_fd = lock_result;
    }
    /* lock_result == -1: best-effort skip, proceed without a lock (see
     * acquire_single_instance_lock's comment). */

    fd = socket(AF_UNIX, SOCK_STREAM, 0);

    if (fd < 0) {
        fprintf(stderr, "failed to create IPC socket: %s\n", strerror(errno));
        abandon_ipc_server(created, -1, socket_path, false);
        return -1;
    }

    set_cloexec(fd);

    memset(&address, 0, sizeof(address));
    address.sun_family = AF_UNIX;
    (void)snprintf(address.sun_path, sizeof(address.sun_path), "%s", socket_path);

    (void)unlink(socket_path);

    if (bind(fd, (struct sockaddr *)&address, sizeof(address)) != 0) {
        fprintf(stderr, "failed to bind IPC socket %s: %s\n", socket_path, strerror(errno));
        abandon_ipc_server(created, fd, socket_path, false);
        return -1;
    }

    if (chmod(socket_path, S_IRUSR | S_IWUSR) != 0) {
        fprintf(stderr, "failed to chmod IPC socket %s: %s\n", socket_path, strerror(errno));
        abandon_ipc_server(created, fd, socket_path, true);
        return -1;
    }

    if (listen(fd, 64) != 0) {
        fprintf(stderr, "failed to listen on IPC socket %s: %s\n", socket_path, strerror(errno));
        abandon_ipc_server(created, fd, socket_path, true);
        return -1;
    }

    if (set_nonblock(fd) != 0) {
        fprintf(stderr, "failed to set IPC server non-blocking: %s\n", strerror(errno));
        abandon_ipc_server(created, fd, socket_path, true);
        return -1;
    }

    created->fd = fd;
    created->socket_path = strdup(socket_path);

    if (created->socket_path == NULL) {
        fprintf(stderr, "failed to copy socket path\n");
        abandon_ipc_server(created, fd, socket_path, true);
        return -1;
    }

    *server = created;
    return 0;
}

int ksi_ipc_server_from_fd(int fd, ksi_ipc_server **server)
{
    ksi_ipc_server *created;
    int type = 0;
    socklen_t type_length = sizeof(type);

    if (server == NULL || fd < 0
        || getsockopt(fd, SOL_SOCKET, SO_TYPE, &type, &type_length) != 0
        || type != SOCK_STREAM) {
        return -1;
    }

    created = calloc(1, sizeof(*created));

    if (created == NULL) {
        return -1;
    }

    /* No single-instance lock in the socket-activation path (see
     * acquire_single_instance_lock's comment) -- must still be -1, not 0
     * (calloc's zero-init), or ksi_ipc_server_close() would later close fd 0. */
    created->lock_fd = -1;

    set_cloexec(fd);

    if (set_nonblock(fd) != 0) {
        free(created);
        return -1;
    }

    created->fd = fd;
    *server = created;
    return 0;
}

void ksi_ipc_server_close(ksi_ipc_server *server)
{
    if (server == NULL) {
        return;
    }

    if (server->fd >= 0) {
        close(server->fd);
    }

    if (server->socket_path != NULL) {
        (void)unlink(server->socket_path);
        free(server->socket_path);
    }

    /* Releases the single-instance flock (held by keeping the fd open, per
     * acquire_single_instance_lock) so a later instance can start cleanly. */
    if (server->lock_fd >= 0) {
        close(server->lock_fd);
    }

    free(server);
}

int ksi_ipc_server_fd(const ksi_ipc_server *server)
{
    return server == NULL ? -1 : server->fd;
}

int ksi_ipc_accept_client(ksi_ipc_server *server)
{
    int client_fd;

    if (server == NULL) {
        return -1;
    }

    client_fd = accept(server->fd, NULL, NULL);

    if (client_fd < 0) {
        if (errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR) {
            fprintf(stderr, "failed to accept IPC client: %s\n", strerror(errno));
        }

        return -1;
    }

    set_cloexec(client_fd);

    if (set_nonblock(client_fd) != 0) {
        fprintf(stderr, "failed to set IPC client non-blocking: %s\n", strerror(errno));
        close(client_fd);
        return -1;
    }

    /* Enlarge the send buffer so a brief consumer hiccup (a client thread
     * descheduled during a burst of hook events, or JIT/GC right after
     * autostart) does not immediately fill the socket and trip a teardown.
     * write_exact still bounds how long it waits on a genuinely stuck peer. */
    {
        int send_buffer_bytes = 256 * 1024;
        (void)setsockopt(client_fd, SOL_SOCKET, SO_SNDBUF,
            &send_buffer_bytes, sizeof(send_buffer_bytes));
    }

    /* Client sockets share long-lived broker threads. Non-blocking I/O makes
     * slow peers fail their own stream instead of holding device dispatch or
     * the IPC poller. */

    return client_fd;
}

int ksi_ipc_get_peer_credentials(int client_fd, ksi_ipc_peer_credentials *credentials)
{
#if defined(__linux__)
    struct ucred peer;
    socklen_t length = sizeof(peer);

    if (credentials == NULL) {
        return -1;
    }

    if (getsockopt(client_fd, SOL_SOCKET, SO_PEERCRED, &peer, &length) != 0) {
        return -1;
    }

    credentials->pid = peer.pid;
    credentials->uid = peer.uid;
    credentials->gid = peer.gid;
    return 0;
#else
    (void)client_fd;
    (void)credentials;
    return -1;
#endif
}

static int read_exact(int fd, void *buffer, size_t length)
{
    uint8_t *cursor = buffer;
    size_t remaining = length;

    while (remaining > 0) {
        ssize_t bytes_read = read(fd, cursor, remaining);

        if (bytes_read == 0) {
            return 0;
        }

        if (bytes_read < 0) {
            if (errno == EINTR) {
                continue;
            }

            return -1;
        }

        cursor += bytes_read;
        remaining -= (size_t)bytes_read;
    }

    return 1;
}

static uint64_t ipc_monotonic_ms(void)
{
    struct timespec ts;

    if (clock_gettime(CLOCK_MONOTONIC, &ts) != 0) {
        return 0;
    }

    return ((uint64_t)ts.tv_sec * 1000u) + ((uint64_t)ts.tv_nsec / 1000000u);
}

/* Total time write_exact waits for a backpressured (non-blocking) peer to drain
 * its receive buffer before giving up and tearing the stream down.  Small enough
 * to bound how long one slow client can stall device dispatch on the calling
 * thread, long enough to ride out a transient scheduler/GC hiccup in an
 * otherwise-healthy client (e.g. JIT warmup right after autostart) instead of
 * forcing a reconnect storm.  Only contends on the per-connection send mutex, so
 * a slow client never blocks sends to other clients. */
#define KSI_WRITE_DRAIN_BUDGET_MS 100

static int write_exact(int fd, const void *buffer, size_t length)
{
    const uint8_t *cursor = buffer;
    size_t remaining = length;
    uint64_t drain_deadline_ms = 0u;

    while (remaining > 0) {
        ssize_t bytes_written = write(fd, cursor, remaining);

        if (bytes_written > 0) {
            cursor += bytes_written;
            remaining -= (size_t)bytes_written;
            continue;
        }

        if (bytes_written == 0) {
            (void)shutdown(fd, SHUT_RDWR);
            return -1;
        }

        if (errno == EINTR) {
            continue;
        }

        if (errno == EAGAIN || errno == EWOULDBLOCK) {
            /* Peer is applying backpressure.  Wait briefly for its receive
             * buffer to drain rather than dropping the connection on the first
             * full-buffer hiccup. */
            struct pollfd pfd;
            uint64_t now_ms = ipc_monotonic_ms();
            int poll_result;

            if (drain_deadline_ms == 0u) {
                drain_deadline_ms = now_ms + KSI_WRITE_DRAIN_BUDGET_MS;
            }

            if (now_ms >= drain_deadline_ms) {
                (void)shutdown(fd, SHUT_RDWR);
                return -1;
            }

            pfd.fd = fd;
            pfd.events = POLLOUT;
            pfd.revents = 0;
            poll_result = poll(&pfd, 1, (int)(drain_deadline_ms - now_ms));

            if (poll_result > 0 && (pfd.revents & POLLOUT) != 0) {
                continue;
            }

            if (poll_result < 0 && errno == EINTR) {
                continue;
            }

            /* Timed out, peer hung up, or poll failed: a non-blocking fd can be
             * left with a partial frame, so tear the stream down rather than let
             * either side parse a truncated response as a later frame. */
            (void)shutdown(fd, SHUT_RDWR);
            return -1;
        }

        /* A genuine write error; same partial-frame reasoning as above. */
        (void)shutdown(fd, SHUT_RDWR);
        return -1;
    }

    return 0;
}

int ksi_ipc_read_framed_message(int client_fd, uint8_t *buffer, size_t buffer_size, size_t *message_size)
{
    ksi_message_header header;
    int read_result;

    if (buffer == NULL || message_size == NULL || buffer_size < sizeof(header)) {
        return -1;
    }

    read_result = read_exact(client_fd, &header, sizeof(header));

    if (read_result <= 0) {
        return read_result;
    }

    if (header.size < sizeof(header)
        || header.size > KSI_MAX_MESSAGE_SIZE
        || header.size > buffer_size) {
        fprintf(stderr, "invalid IPC frame size: %u\n", header.size);
        return -1;
    }

    memcpy(buffer, &header, sizeof(header));
    read_result = read_exact(client_fd, buffer + sizeof(header), header.size - sizeof(header));

    if (read_result <= 0) {
        return read_result;
    }

    *message_size = header.size;
    return 1;
}

int ksi_ipc_send_framed_message(
    int client_fd,
    uint32_t message_type,
    uint32_t client_id,
    uint64_t correlation_id,
    const void *payload,
    size_t payload_size)
{
    ksi_message_header header;

    if (payload_size > KSI_MAX_MESSAGE_SIZE - sizeof(header)) {
        return -1;
    }

    memset(&header, 0, sizeof(header));
    header.size = (uint32_t)(sizeof(header) + payload_size);
    header.major = KSI_PROTOCOL_MAJOR;
    header.minor = KSI_PROTOCOL_MINOR;
    header.type = message_type;
    header.client_id = client_id;
    header.correlation_id = correlation_id;

    if (write_exact(client_fd, &header, sizeof(header)) != 0) {
        return -1;
    }

    if (payload_size > 0 && write_exact(client_fd, payload, payload_size) != 0) {
        return -1;
    }

    return 0;
}

void ksi_ipc_close_client(int client_fd)
{
    if (client_fd >= 0) {
        close(client_fd);
    }
}
