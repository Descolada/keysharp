#ifndef KEYSHARP_INPUTD_IPC_H
#define KEYSHARP_INPUTD_IPC_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <sys/types.h>

#include "keysharp_inputd/protocol.h"

typedef struct ksi_ipc_server ksi_ipc_server;

typedef struct ksi_ipc_peer_credentials {
    pid_t pid;
    uid_t uid;
    gid_t gid;
} ksi_ipc_peer_credentials;

int ksi_ipc_server_open(const char *socket_path, ksi_ipc_server **server);
void ksi_ipc_server_close(ksi_ipc_server *server);
int ksi_ipc_server_fd(const ksi_ipc_server *server);
int ksi_ipc_accept_client(ksi_ipc_server *server);
int ksi_ipc_get_peer_credentials(int client_fd, ksi_ipc_peer_credentials *credentials);
int ksi_ipc_read_framed_message(int client_fd, uint8_t *buffer, size_t buffer_size, size_t *message_size);
int ksi_ipc_send_framed_message(
    int client_fd,
    uint32_t message_type,
    uint32_t client_id,
    uint64_t correlation_id,
    const void *payload,
    size_t payload_size);
void ksi_ipc_close_client(int client_fd);

#endif
