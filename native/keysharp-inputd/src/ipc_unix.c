#include "keysharp_inputd/ipc.h"

#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>

struct ksi_ipc_server {
    int fd;
    char *socket_path;
};

static void set_cloexec(int fd)
{
    int flags = fcntl(fd, F_GETFD);

    if (flags >= 0) {
        (void)fcntl(fd, F_SETFD, flags | FD_CLOEXEC);
    }
}

static void set_nonblocking(int fd)
{
    int flags = fcntl(fd, F_GETFL);

    if (flags >= 0) {
        (void)fcntl(fd, F_SETFL, flags | O_NONBLOCK);
    }
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

int ksi_ipc_server_open(const char *socket_path, ksi_ipc_server **server)
{
    struct sockaddr_un address;
    ksi_ipc_server *created = NULL;
    int fd = -1;

    if (server == NULL || validate_socket_path(socket_path) != 0) {
        return -1;
    }

    created = calloc(1, sizeof(*created));

    if (created == NULL) {
        fprintf(stderr, "failed to allocate IPC server\n");
        return -1;
    }

    fd = socket(AF_UNIX, SOCK_STREAM, 0);

    if (fd < 0) {
        fprintf(stderr, "failed to create IPC socket: %s\n", strerror(errno));
        free(created);
        return -1;
    }

    set_cloexec(fd);

    memset(&address, 0, sizeof(address));
    address.sun_family = AF_UNIX;
    (void)snprintf(address.sun_path, sizeof(address.sun_path), "%s", socket_path);

    (void)unlink(socket_path);

    if (bind(fd, (struct sockaddr *)&address, sizeof(address)) != 0) {
        fprintf(stderr, "failed to bind IPC socket %s: %s\n", socket_path, strerror(errno));
        close(fd);
        free(created);
        return -1;
    }

    if (chmod(socket_path, S_IRUSR | S_IWUSR) != 0) {
        fprintf(stderr, "failed to chmod IPC socket %s: %s\n", socket_path, strerror(errno));
        close(fd);
        (void)unlink(socket_path);
        free(created);
        return -1;
    }

    if (listen(fd, 16) != 0) {
        fprintf(stderr, "failed to listen on IPC socket %s: %s\n", socket_path, strerror(errno));
        close(fd);
        (void)unlink(socket_path);
        free(created);
        return -1;
    }

    created->fd = fd;
    created->socket_path = strdup(socket_path);

    if (created->socket_path == NULL) {
        fprintf(stderr, "failed to copy socket path\n");
        close(fd);
        (void)unlink(socket_path);
        free(created);
        return -1;
    }

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
        fprintf(stderr, "failed to accept IPC client: %s\n", strerror(errno));
        return -1;
    }

    set_cloexec(client_fd);
    set_nonblocking(client_fd);

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
