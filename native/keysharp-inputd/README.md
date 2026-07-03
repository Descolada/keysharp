# keysharp-inputd

`keysharp-inputd` is the privileged input broker for Keysharp on Linux. It owns
physical input devices and virtual input synthesis while Keysharp scripts run as
normal user processes that communicate with it over a Unix socket.

This helper is optional. Portable or source-tree Keysharp runs can omit it, but
Linux input monitoring, input synthesis, and `BlockInput` capabilities will be
reported as unsupported until the helper is installed and configured.

See [docs/protocol.md](docs/protocol.md) for the IPC protocol.

## Build

Dependencies:

| Package | Purpose |
|---|---|
| `cmake`, `make` | build system |
| `build-essential` / `gcc` | C compiler and libc headers |
| `pkg-config` | library discovery |
| `libudev-dev` / `systemd-devel` | device hotplug |
| `libevdev-dev` | evdev event decoding |

```bash
# Ubuntu/Debian
sudo apt install cmake build-essential pkg-config libudev-dev libevdev-dev

# Fedora
sudo dnf install cmake gcc make pkgconf-pkg-config systemd-devel libevdev-devel

# Arch
sudo pacman -S cmake base-devel pkgconf systemd libevdev

# openSUSE
sudo zypper install cmake gcc make pkg-config systemd-devel libevdev-devel
```

```bash
cmake -S native/keysharp-inputd -B native/keysharp-inputd/build
cmake --build native/keysharp-inputd/build
```

This also builds the shared `keysharp-trust` static library from
`native/keysharp-trust-lib`. The screen capture helper is a separate native
target; see [`../keysharp-helper/README.md`](../keysharp-helper/README.md).

## Install

```bash
sudo cmake --install native/keysharp-inputd/build
```

This installs the binary, systemd units, and runs `keysharp-inputd --install-input-access`
to configure device access and enable the socket. The socket unit starts the daemon
on demand; the daemon exits when no clients are connected.

Packaging installs that use `DESTDIR` must run the following from their post-install step:

```bash
keysharp-inputd --install-input-access
```

## Service management

The installed service is socket-activated. The socket stays up permanently; the
daemon process starts on first client connection and exits when idle.

```bash
# Status
systemctl status keysharp-inputd.socket keysharp-inputd.service

# Restart the daemon (keeps the socket; active clients reconnect)
systemctl restart keysharp-inputd.service

# Stop the daemon and prevent it from restarting on new connections
systemctl stop keysharp-inputd.socket keysharp-inputd.service

# Re-enable and start after a stop
systemctl start keysharp-inputd.socket

# Force-kill a stuck daemon (the socket stays up; the daemon restarts on next connection)
systemctl kill keysharp-inputd.service

# View logs
journalctl -u keysharp-inputd.service -f
```

To reload after reinstalling the binary without a full restart:

```bash
sudo systemctl daemon-reload
systemctl restart keysharp-inputd.service
```

If capability requests are denied without a permission prompt after upgrading
from an older inputd build, reinstall the unit and reload systemd so the service
can write the shared trust store:

```bash
sudo cmake --install native/keysharp-inputd/build
sudo systemctl daemon-reload
sudo systemctl restart keysharp-inputd.socket keysharp-inputd.service
```

## Development run

Manual runs use a private socket under `$XDG_RUNTIME_DIR` by default:

```bash
native/keysharp-inputd/build/keysharp-inputd --foreground
```

Override the socket path:

```bash
native/keysharp-inputd/build/keysharp-inputd --socket /tmp/keysharp-test.sock
```

IPC smoke test:

```bash
native/keysharp-inputd/build/keysharp-inputd-client
native/keysharp-inputd/build/keysharp-inputd-client --split
```

## Security model

`keysharp-inputd` is a root-owned system service. Keysharp users do not need and
should not be added to the `input` group.

- The daemon uses `SO_PEERCRED` to record each connecting process's pid/uid/gid.
- It resolves `/proc/<pid>/exe` and hashes the executable digest with the
  argument vector as the persistent app identity.
- Clients send `CLIENT_HELLO` with requested capabilities; the daemon grants only
  what its own device access allows.
- Unknown process identities are prompted before privileged capabilities are granted.
- `Allow always` decisions are stored in the shared root-owned keysharp-trust
  store (`/var/lib/keysharp-trust/permissions.tsv`), partitioned by peer uid,
  and pruned after 60 days.
- `Allow once` decisions last for the current daemon session only.
- The socket is world-connectable, so the daemon bounds abuse from any local
  process: a connection that does not complete its `CLIENT_HELLO` handshake is
  dropped after a short deadline (no silent slot-holding), a single uid cannot
  occupy the last client slots (others stay reachable), and client-forced
  re-prompts are rate-limited.

Capabilities: `KSI_CAP_HOOK_KEYBOARD`, `KSI_CAP_HOOK_MOUSE`,
`KSI_CAP_SYNTH_KEYBOARD`, `KSI_CAP_SYNTH_MOUSE`, `KSI_CAP_BLOCK_INPUT`.
The protocol keeps keyboard and mouse bits separate. Keysharp requests both hook
bits for its user-facing input monitoring permission and both synthesis bits for
its user-facing input synthesis permission.
Denied decisions are persisted in the same store as grants. After a deny the
daemon will not re-prompt for the same capabilities until the user explicitly
clears it. Two ways to clear a persisted deny:

* An explicit Keysharp `RequestCapabilities(...)` call sends a force-prompt
  request so the script can re-ask the user.
* `keysharp-inputd trust list` shows the records stored for the current user, and
  `keysharp-inputd trust reset <hash>` (or `--pid <pid>`) clears allow/deny bits so
  the next prompt re-asks from scratch. The subcommand talks to the daemon over its
  Unix socket, so users do not need root. Forced re-prompts are rate-limited, so a
  hostile process cannot loop `RequestCapabilities` to spam permission dialogs.

Hook subscriptions require hook access. `MODIFY` hook decisions and direct
`SYNTHESIZE_INPUT` requests require synthesis access.

## Architecture

```
Keysharp scripts  ──► IPC client  ──► keysharp-inputd  ──► platform backend  ──► evdev/udev/uinput
```

**Daemon owns:** device discovery and hotplug, exclusive evdev grabs, virtual
input devices, event suppression/replay/synthesis, injected-event tagging,
multiple hook clients, bounded hook dispatch and timeouts, emergency ungrab.

**Keysharp owns:** hotkey/hotstring semantics, script lifecycle, context
evaluation, script callbacks, pass/block/replace decisions for hooked input.
