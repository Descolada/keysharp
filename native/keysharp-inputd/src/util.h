#pragma once

#include <fcntl.h>
#include <unistd.h>

/* Set the close-on-exec flag on fd so it is not inherited by child processes. */
static inline void set_cloexec(int fd)
{
    int flags = fcntl(fd, F_GETFD);

    if (flags >= 0) {
        (void)fcntl(fd, F_SETFD, flags | FD_CLOEXEC);
    }
}

/* Set non-blocking I/O on fd so daemon threads cannot be held by peers. */
static inline int set_nonblock(int fd)
{
    int flags = fcntl(fd, F_GETFL);

    if (flags < 0) {
        return -1;
    }

    return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}
