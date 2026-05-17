#ifndef KEYSHARP_INPUTD_DAEMON_H
#define KEYSHARP_INPUTD_DAEMON_H

#include <stdbool.h>

typedef struct ksi_daemon_options {
    const char *socket_path;
    bool foreground;
} ksi_daemon_options;

int ksi_daemon_run(const ksi_daemon_options *options);

#endif
