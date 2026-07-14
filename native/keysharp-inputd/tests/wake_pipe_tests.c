#include "wake_pipe.h"

#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>

int main(void)
{
    int read_fd;
    int write_fd;
    uint8_t byte = 1u;

    if (ksi_wake_pipe_open(&read_fd, &write_fd) != 0
        || write(write_fd, &byte, sizeof(byte)) != (ssize_t)sizeof(byte)) {
        fputs("FAIL wake pipe open/write\n", stderr);
        return EXIT_FAILURE;
    }

    ksi_wake_pipe_drain(read_fd);
    errno = 0;

    if (read(read_fd, &byte, sizeof(byte)) >= 0 || errno != EAGAIN) {
        fputs("FAIL wake pipe drain/nonblocking\n", stderr);
        return EXIT_FAILURE;
    }

    ksi_wake_pipe_close(&read_fd, &write_fd);

    if (read_fd != -1 || write_fd != -1) {
        fputs("FAIL wake pipe close\n", stderr);
        return EXIT_FAILURE;
    }

    puts("PASS wake pipe lifecycle");
    return EXIT_SUCCESS;
}
