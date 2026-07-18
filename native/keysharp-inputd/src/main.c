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

/* Shared by install/remove so the two paths always agree on the file to manage.
 * Numbered 70- so it lexically precedes systemd's 73-seat-late.rules, which runs
 * the uaccess builtin — a tag added after that rule would have no effect. */
#define KSI_UACCESS_RULES_PATH "/etc/udev/rules.d/70-keysharp-inputd-uaccess.rules"

/* uinput module auto-load config written by --install-input-access. Shared with
 * --remove-input-access so removal cleans up exactly what install created and the
 * two paths cannot drift. */
#define KSI_UINPUT_MODULES_PATH "/etc/modules-load.d/uinput.conf"

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
		"Usage: %s [--foreground] [--socket PATH] [--system-service] [--verbose] [--install-input-access] [--remove-input-access] [--version]\n"
		"       %s trust <list|reset> [options]\n"
		"\n"
		"Daemon options:\n"
		"  --foreground   Run in the foreground. This is currently the default.\n"
		"  --socket PATH  Unix domain socket path. Default: $XDG_RUNTIME_DIR/keysharp/keysharp-inputd.sock\n"
		"  --system-service\n"
		"                Use the systemd-activated socket passed as fd 3. Must be run by the system unit.\n"
		"  --verbose      Enable per-event debug logging.\n"
		"  --install-input-access\n"
		"                Load uinput, install the uaccess udev rule for the virtual devices, and\n"
		"                (re)enable the installed system socket. Must be run as root.\n"
		"  --remove-input-access\n"
		"                Remove the uaccess udev rule and reload udev. Must be run as root.\n"
		"  --version      Print version information.\n"
		"\n"
		"Trust subcommand: run '%s trust --help' for details.\n",
		argv0, argv0, argv0);
}

/* Body of the uaccess udev rule written by --install-input-access.
 *
 * WHY THIS EXISTS (consumer-access root cause):
 *   keysharp-inputd runs as root, EVIOCGRABs the real input devices, and
 *   re-injects every PASSED (non-hotkey) event onto its own uinput virtual
 *   devices ("Keysharp Virtual Input" / "Keysharp Virtual Pointer"). For those
 *   replayed events to reach applications, the CONSUMER — the user's X/Wayland
 *   server — must be able to READ the virtual /dev/input/eventN nodes. The
 *   daemon being root is NOT enough; it is the consumer that needs access.
 *
 *   The old (removed in commit 4f09a267) approach broadened every input node:
 *       KERNEL=="event*", SUBSYSTEM=="input", GROUP="input", MODE="0660"
 *   and told users to join the 'input' group. On boxes where the user is not in
 *   'input' and there is no uaccess ACL (e.g. rootless X) replay was invisible,
 *   so ALL non-hotkey input died while hotkeys still fired.
 *
 *   This rule grants access narrowly instead: only the daemon's OWN virtual
 *   devices, only to the active-session user, via systemd-logind's uaccess ACL.
 *   No group membership and no broadening of unrelated input devices.
 *
 * Matching notes:
 *   - We match by NAME, not vendor/product. The synthetic devices masquerade as
 *     keyd's vendor 0x0FAC (see protocol.h), so id/vendor no longer uniquely
 *     identifies us — but the names "Keysharp Virtual Input"/"Keysharp Virtual
 *     Pointer" are ours alone. (An id/vendor=="0fac" match would also tag keyd's
 *     own devices; harmless, but name is precise.)
 *   - ATTRS{} walks up from the event* node to the parent input device that
 *     carries the name; the tag lands on the event* node (the one with a device
 *     node), which is what the uaccess builtin needs. */
static const char KSI_UACCESS_RULES_CONTENTS[] =
	"# keysharp-inputd uaccess rule - grants the active-session user an ACL on\n"
	"# the daemon's OWN virtual input devices so replayed (passed-through) events\n"
	"# are readable by the user's X/Wayland server. See src/main.c for the full\n"
	"# rationale. Replaces the removed GROUP=\"input\", MODE=\"0660\" rule.\n"
	"ACTION!=\"add|change\", GOTO=\"keysharp_uaccess_end\"\n"
	"SUBSYSTEM!=\"input\", GOTO=\"keysharp_uaccess_end\"\n"
	"\n"
	"ATTRS{name}==\"Keysharp Virtual Input\", TAG+=\"uaccess\"\n"
	"ATTRS{name}==\"Keysharp Virtual Pointer\", TAG+=\"uaccess\"\n"
	"\n"
	"LABEL=\"keysharp_uaccess_end\"\n";

static int install_input_access(void)
{
	const char *legacy_rules_path = "/etc/udev/rules.d/99-keysharp-inputd.rules";
	const char *modules_path = KSI_UINPUT_MODULES_PATH;
	FILE *modules;
	FILE *rules;
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

	/* Grant the consuming X/Wayland server read access to the daemon's own
	 * virtual devices via systemd-logind's uaccess ACL (see the rationale on
	 * KSI_UACCESS_RULES_CONTENTS above). */
	rules = fopen(KSI_UACCESS_RULES_PATH, "w");

	if (rules == NULL) {
		fprintf(stderr, "failed to write %s: %s\n", KSI_UACCESS_RULES_PATH, strerror(errno));
		status = 1;
	} else {
		if (fputs(KSI_UACCESS_RULES_CONTENTS, rules) == EOF) {
			fprintf(stderr, "failed to write %s: %s\n", KSI_UACCESS_RULES_PATH, strerror(errno));
			status = 1;
		}

		if (fclose(rules) != 0) {
			fprintf(stderr, "failed to close %s: %s\n", KSI_UACCESS_RULES_PATH, strerror(errno));
			status = 1;
		}
	}

	/* Reload rules and re-trigger so the new uaccess tag is applied to any
	 * already-present virtual devices as well as future ones. */
	if (system("udevadm control --reload-rules && udevadm trigger --subsystem-match=input && udevadm trigger --subsystem-match=misc") != 0) {
		fprintf(stderr, "warning: failed to refresh udev after installing the uaccess rule\n");
		status = 1;
	}

	/* Replace any stale daemon: reload unit definitions, stop a lingering
	 * Type=simple service instance so it is not left serving the new client,
	 * then enable both units and restart the socket before starting the service.
	 * The service remains resident from boot so its idle counter has continuity
	 * even when no Keysharp process is connected.
	 * Tolerate systemctl being absent (e.g. non-systemd hosts): warn, do not
	 * hard-fail — the udev/uinput setup above is still useful without it. */
	if (system("command -v systemctl >/dev/null 2>&1") != 0) {
		/* Benign on non-systemd hosts: the udev/uinput setup above is the part
		 * that actually fixes input access, so don't fail the whole command (and
		 * trigger the installer's scary "did not complete" banner) just for this. */
		fprintf(stderr, "notice: systemctl not found; skipping socket activation refresh\n");
	} else {
		if (system("systemctl daemon-reload") != 0) {
			fprintf(stderr, "warning: systemctl daemon-reload failed\n");
			status = 1;
		}

		/* Stopping an inactive/absent unit can exit non-zero on older systemd;
		 * that's benign here (we re-enable and restart the socket next), so warn
		 * without failing the command. */
		if (system("systemctl stop keysharp-inputd.service") != 0) {
			fprintf(stderr, "notice: keysharp-inputd.service was not running (nothing to stop)\n");
		}

		if (system("systemctl enable keysharp-inputd.socket") != 0) {
			fprintf(stderr, "warning: failed to enable keysharp-inputd.socket\n");
			status = 1;
		}

		if (system("systemctl enable keysharp-inputd.service") != 0) {
			fprintf(stderr, "warning: failed to enable keysharp-inputd.service\n");
			status = 1;
		}

		if (system("systemctl restart keysharp-inputd.socket") != 0) {
			fprintf(stderr, "warning: failed to restart keysharp-inputd.socket\n");
			status = 1;
		}

		if (system("systemctl restart keysharp-inputd.service") != 0) {
			fprintf(stderr, "warning: failed to restart keysharp-inputd.service\n");
			status = 1;
		}
	}

	puts("keysharp-inputd input access setup complete.");
	return status;
}

/* Remove the uaccess rule installed by --install-input-access and reload udev.
 * Exposed as its own flag so the uninstaller script can call it. Mirrors the
 * defensive style of install_input_access: warn + set status, never abort. */
static int remove_input_access(void)
{
	int status = 0;

	if (geteuid() != 0) {
		fprintf(stderr, "--remove-input-access must be run as root\n");
		return 1;
	}

	if (unlink(KSI_UACCESS_RULES_PATH) != 0 && errno != ENOENT) {
		fprintf(stderr, "warning: failed to remove %s: %s\n", KSI_UACCESS_RULES_PATH, strerror(errno));
		status = 1;
	}

	/* Also remove the uinput module-load config install wrote, so removal does
	 * not leave the kernel auto-loading the uinput module every boot. */
	if (unlink(KSI_UINPUT_MODULES_PATH) != 0 && errno != ENOENT) {
		fprintf(stderr, "warning: failed to remove %s: %s\n", KSI_UINPUT_MODULES_PATH, strerror(errno));
		status = 1;
	}

	if (system("udevadm control --reload-rules && udevadm trigger --subsystem-match=input") != 0) {
		fprintf(stderr, "warning: failed to refresh udev after removing the uaccess rule\n");
		status = 1;
	}

	puts("keysharp-inputd uaccess rule removed.");
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
		} else if (strcmp(argv[i], "--remove-input-access") == 0) {
			return remove_input_access();
		} else if (strcmp(argv[i], "--version") == 0) {
            printf("keysharp-inputd %u.%u\n",
                (unsigned)KSI_PROTOCOL_MAJOR, (unsigned)KSI_PROTOCOL_MINOR);
            printf("protocol %s %u.%u\n", KSI_PROTOCOL_NAME,
                (unsigned)KSI_PROTOCOL_MAJOR, (unsigned)KSI_PROTOCOL_MINOR);
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
