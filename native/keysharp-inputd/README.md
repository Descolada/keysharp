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

Run deterministic native tests with:

```bash
ctest --test-dir native/keysharp-inputd/build --output-on-failure
```

The suite covers expansion boundaries, atomic queue rejection, concurrent ring
ordering, and reports the per-event lane allocation benchmark. Configure with
`-DBUILD_TESTING=OFF` to omit the test target from packaging builds.

For interactive diagnosis against an installed daemon, use
`native/keysharp-inputd/build/keysharp-inputd-hooktest --help`. It can observe
keyboard and mouse hooks, pass/block/modify events, delay subscriptions or
decisions, inject keys or Unicode, release stuck modifiers, and trigger the
emergency passthrough. Configure with `-DKEYSHARP_INPUTD_BUILD_HOOKTEST=OFF` to
omit this tool.

Privileged end-to-end evdev/uinput checks are intentionally separate from the
automated suite. See [`docs/physical-live-tests.md`](docs/physical-live-tests.md)
for the opt-in virtual-source harness and the physical device ID, BLOCK, MODIFY,
and non-interspersing test matrix.

## Install

```bash
sudo cmake --install native/keysharp-inputd/build
```

This installs the binary, systemd units, and runs `keysharp-inputd --install-input-access`
to configure device access and enable both the daemon and its socket. The daemon
starts at boot and remains resident so its idle-time counter continues across
separately launched Keysharp processes. It holds no input grabs without an active
hook or BlockInput request. On an idle-only boot it also creates no virtual input
devices: physical devices are observed through independent read-only evdev clients,
and privileged uinput devices are created lazily only when an identified client
requests hook, synthesis, or BlockInput access.

Packaging installs that use `DESTDIR` must run the following from their post-install step:

```bash
keysharp-inputd --install-input-access
```

## Service management

The installed service starts at boot. Its socket remains enabled as a recovery
activation path if the service is stopped or crashes.

```bash
# Status
systemctl status keysharp-inputd.socket keysharp-inputd.service

# Restart the daemon (keeps the socket; active clients reconnect)
systemctl restart keysharp-inputd.service

# Stop the daemon and prevent it from restarting on new connections
systemctl stop keysharp-inputd.socket keysharp-inputd.service

# Re-enable and start after a stop
systemctl start keysharp-inputd.socket keysharp-inputd.service

# Force-kill a stuck daemon (systemd restarts the enabled service)
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

`MODIFY` is the protocol's suppress-and-replace primitive: the original hook
event is blocked and its nonempty replacement input list is emitted in order.
The entire decision is rejected if the list is empty or the client lacks any
required keyboard/mouse synthesis capability. Keysharp itself normally returns
`PASS`/`BLOCK` and performs inline sends as separate recursive
`SYNTHESIZE_INPUT` calls, matching Windows hook behavior; `MODIFY` remains for
protocol clients such as `hooktest`.

`SYNTHESIS_RESULT` uses stable detail values from `ksi_status_detail` in
`protocol.h`: malformed payload (1), input-count limit (2), payload-size
mismatch (3), resource exhaustion/backpressure (12), recursion limit (32),
expanded-input limit (33), cancellation (125), permission denied (403), and
callback timeout (408). Success has detail 0.

## Architecture

```
Keysharp scripts  ──► IPC client  ──► keysharp-inputd  ──► platform backend  ──► evdev/udev/uinput
```

**Daemon owns:** device discovery and hotplug, exclusive evdev grabs, virtual
input devices, event suppression/replay/synthesis, injected-event tagging,
multiple hook clients, bounded hook dispatch and timeouts, emergency ungrab.

**Keysharp owns:** hotkey/hotstring semantics, script lifecycle, context
evaluation, script callbacks, pass/block/replace decisions for hooked input.
