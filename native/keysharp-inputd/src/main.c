#include "keysharp_inputd/daemon.h"
#include "keysharp_inputd/protocol.h"

#include <errno.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#define KSI_DEFAULT_SOCKET_DIR_NAME "keysharp"
#define KSI_DEFAULT_SOCKET_NAME "keysharp-inputd.sock"
#define KSI_SOCKET_PATH_LENGTH 256

static int ensure_private_directory(const char *path)
{
    struct stat info;

    if (mkdir(path, S_IRWXU) != 0 && errno != EEXIST) {
        fprintf(stderr, "failed to create socket directory %s: %s\n", path, strerror(errno));
        return -1;
    }

    if (stat(path, &info) != 0) {
        fprintf(stderr, "failed to stat socket directory %s: %s\n", path, strerror(errno));
        return -1;
    }

    if (!S_ISDIR(info.st_mode)) {
        fprintf(stderr, "socket path parent is not a directory: %s\n", path);
        return -1;
    }

    if ((info.st_mode & (S_IRWXG | S_IRWXO)) != 0 && chmod(path, S_IRWXU) != 0) {
        fprintf(stderr, "failed to chmod socket directory %s: %s\n", path, strerror(errno));
        return -1;
    }

    return 0;
}

static int build_default_socket_path(char *buffer, size_t buffer_size)
{
    const char *runtime_dir = getenv("XDG_RUNTIME_DIR");
    char directory[KSI_SOCKET_PATH_LENGTH];

    if (runtime_dir == NULL || runtime_dir[0] == '\0') {
        fprintf(stderr, "XDG_RUNTIME_DIR is not set; use --socket PATH to override\n");
        return -1;
    }

    if (snprintf(directory, sizeof(directory), "%s/%s", runtime_dir, KSI_DEFAULT_SOCKET_DIR_NAME)
        >= (int)sizeof(directory)) {
        fprintf(stderr, "default socket directory path is too long\n");
        return -1;
    }

    if (ensure_private_directory(directory) != 0) {
        return -1;
    }

    if (snprintf(buffer, buffer_size, "%s/%s", directory, KSI_DEFAULT_SOCKET_NAME)
        >= (int)buffer_size) {
        fprintf(stderr, "default socket path is too long\n");
        return -1;
    }

    return 0;
}

static void print_usage(const char *argv0)
{
    fprintf(stderr,
        "Usage: %s [--foreground] [--socket PATH] [--version]\n"
        "\n"
        "Options:\n"
        "  --foreground   Run in the foreground. This is currently the default.\n"
        "  --socket PATH  Unix domain socket path. Default: $XDG_RUNTIME_DIR/keysharp/keysharp-inputd.sock\n"
        "  --version      Print version information.\n",
        argv0);
}

int main(int argc, char **argv)
{
    setvbuf(stdout, NULL, _IOLBF, 0);

    char default_socket_path[KSI_SOCKET_PATH_LENGTH];
    ksi_daemon_options options = {
        .socket_path = NULL,
        .foreground = true,
    };
    bool socket_path_overridden = false;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--foreground") == 0) {
            options.foreground = true;
        } else if (strcmp(argv[i], "--socket") == 0) {
            if (i + 1 >= argc) {
                fprintf(stderr, "--socket requires a path\n");
                return 2;
            }

            options.socket_path = argv[++i];
            socket_path_overridden = true;
        } else if (strcmp(argv[i], "--version") == 0) {
            puts("keysharp-inputd 0.1.0");
            puts("protocol " KSI_PROTOCOL_NAME " 0.1");
            return 0;
        } else if (strcmp(argv[i], "--help") == 0 || strcmp(argv[i], "-h") == 0) {
            print_usage(argv[0]);
            return 0;
        } else {
            fprintf(stderr, "Unknown option: %s\n", argv[i]);
            print_usage(argv[0]);
            return 2;
        }
    }

    if (!socket_path_overridden) {
        if (build_default_socket_path(default_socket_path, sizeof(default_socket_path)) != 0) {
            return 2;
        }

        options.socket_path = default_socket_path;
    }

    return ksi_daemon_run(&options);
}
