#include "wake_pipe.h"

#include <fcntl.h>
#include <stdint.h>
#include <unistd.h>

int ksi_wake_pipe_open(int *read_fd, int *write_fd)
{
    int fds[2];

    if (read_fd == NULL || write_fd == NULL) {
        return -1;
    }

    *read_fd = -1;
    *write_fd = -1;

    if (pipe(fds) != 0) {
        return -1;
    }

    for (int i = 0; i < 2; i++) {
        int flags = fcntl(fds[i], F_GETFL);

        if (flags < 0 || fcntl(fds[i], F_SETFL, flags | O_NONBLOCK) != 0) {
            close(fds[0]);
            close(fds[1]);
            return -1;
        }
    }

    *read_fd = fds[0];
    *write_fd = fds[1];
    return 0;
}

void ksi_wake_pipe_drain(int fd)
{
    uint8_t buffer[64];

    if (fd >= 0) {
        while (read(fd, buffer, sizeof(buffer)) > 0) {
        }
    }
}

void ksi_wake_pipe_close(int *read_fd, int *write_fd)
{
    if (read_fd != NULL && *read_fd >= 0) {
        close(*read_fd);
        *read_fd = -1;
    }

    if (write_fd != NULL && *write_fd >= 0) {
        close(*write_fd);
        *write_fd = -1;
    }
}
