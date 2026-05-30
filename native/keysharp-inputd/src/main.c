#include "keysharp_inputd/daemon.h"
#include "keysharp_inputd/protocol.h"

int trust_cli_main(int argc, char **argv);

#include <errno.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

bool g_verbose = false;

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
		"Usage: %s [--foreground] [--socket PATH] [--system-service] [--verbose] [--install-input-access] [--version]\n"
		"       %s trust <list|reset> [options]\n"
		"\n"
		"Daemon options:\n"
		"  --foreground   Run in the foreground. This is currently the default.\n"
		"  --socket PATH  Unix domain socket path. Default: $XDG_RUNTIME_DIR/keysharp/keysharp-inputd.sock\n"
		"  --system-service\n"
		"                Use the systemd-activated socket passed as fd 3. Must be run by the system unit.\n"
		"  --verbose      Enable per-event debug logging.\n"
		"  --install-input-access\n"
		"                Load uinput and enable the installed system socket. Must be run as root.\n"
		"  --version      Print version information.\n"
		"\n"
		"Trust subcommand: run '%s trust --help' for details.\n",
		argv0, argv0, argv0);
}

static int install_input_access(void)
{
	const char *legacy_rules_path = "/etc/udev/rules.d/99-keysharp-inputd.rules";
	const char *modules_path = "/etc/modules-load.d/uinput.conf";
	FILE *modules;
	int status = 0;

	if (geteuid() != 0) {
		fprintf(stderr, "--install-input-access must be run as root\n");
		return 1;
	}

	modules = fopen(modules_path, "w");

	if (modules == NULL) {
		fprintf(stderr, "failed to write %s: %s\n", modules_path, strerror(errno));
		status = 1;
	} else {
		fprintf(modules, "uinput\n");
		fclose(modules);
	}

	if (system("modprobe uinput") != 0) {
		fprintf(stderr, "warning: modprobe uinput failed\n");
		status = 1;
	}

	if (unlink(legacy_rules_path) != 0 && errno != ENOENT) {
		fprintf(stderr, "warning: failed to remove legacy rule %s: %s\n", legacy_rules_path, strerror(errno));
		status = 1;
	}

	if (system("udevadm control --reload-rules && udevadm trigger --subsystem-match=input && udevadm trigger --subsystem-match=misc") != 0) {
		fprintf(stderr, "warning: failed to refresh udev after legacy input rule removal\n");
		status = 1;
	}

	if (system("systemctl daemon-reload && systemctl enable --now keysharp-inputd.socket") != 0) {
		fprintf(stderr, "warning: failed to enable keysharp-inputd.socket\n");
		status = 1;
	}

	puts("keysharp-inputd input access setup complete.");
	return status;
}

static int validate_systemd_socket_activation(void)
{
	const char *listen_pid = getenv("LISTEN_PID");
	const char *listen_fds = getenv("LISTEN_FDS");
	char *end = NULL;
	long pid_value;

	if (geteuid() != 0 || listen_pid == NULL || listen_fds == NULL || strcmp(listen_fds, "1") != 0) {
		fprintf(stderr, "--system-service requires one systemd socket and root service context\n");
		return -1;
	}

	errno = 0;
	pid_value = strtol(listen_pid, &end, 10);

	if (errno != 0 || end == listen_pid || *end != '\0' || pid_value != (long)getpid()) {
		fprintf(stderr, "--system-service LISTEN_PID does not match this daemon\n");
		return -1;
	}

	unsetenv("LISTEN_PID");
	unsetenv("LISTEN_FDS");
	unsetenv("LISTEN_FDNAMES");
	return 0;
}

int main(int argc, char **argv)
{
    setvbuf(stdout, NULL, _IOLBF, 0);

    if (argc >= 2 && strcmp(argv[1], "trust") == 0) {
        return trust_cli_main(argc - 1, argv + 1);
    }

    char default_socket_path[KSI_SOCKET_PATH_LENGTH];
    ksi_daemon_options options = {
        .socket_path = NULL,
        .foreground = true,
        .system_service = false,
    };
    bool socket_path_overridden = false;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--foreground") == 0) {
            options.foreground = true;
        } else if (strcmp(argv[i], "--verbose") == 0) {
            g_verbose = true;
		} else if (strcmp(argv[i], "--socket") == 0) {
			if (i + 1 >= argc) {
				fprintf(stderr, "--socket requires a path\n");
				return 2;
			}

			options.socket_path = argv[++i];
			socket_path_overridden = true;
		} else if (strcmp(argv[i], "--system-service") == 0) {
			options.system_service = true;
		} else if (strcmp(argv[i], "--install-input-access") == 0) {
			return install_input_access();
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

    if (options.system_service) {
        if (socket_path_overridden) {
            fprintf(stderr, "--socket cannot be combined with --system-service\n");
            return 2;
        }

        if (validate_systemd_socket_activation() != 0) {
            return 2;
        }
    } else if (!socket_path_overridden) {
        if (build_default_socket_path(default_socket_path, sizeof(default_socket_path)) != 0) {
            return 2;
        }

        options.socket_path = default_socket_path;
    }

    return ksi_daemon_run(&options);
}
