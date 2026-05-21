# keysharp-inputd

`keysharp-inputd` is the privileged input broker for Keysharp on Linux.

The daemon is intentionally separate from the Keysharp runtime. It owns physical input devices and virtual input synthesis while Keysharp scripts remain normal user processes that communicate with it over IPC.

## Current State

The daemon currently:

- starts a foreground daemon process
- creates a Unix domain socket
- accepts multiple clients
- defines a Windows-like hook and synthesis protocol
- keeps platform input code behind a small backend interface
- scans `/dev/input/event*`
- maintains a small in-memory table of candidate input devices
- queries Linux input device names and capabilities
- logs candidate keyboard and mouse devices
- monitors udev hotplug events for added, changed, and removed input devices
- opens candidate devices without grabbing them
- reads evdev events through `libevdev`
- emits Windows-shaped keyboard and mouse hook events
- creates a `uinput` virtual keyboard/mouse device when permitted
- handles framed binary IPC messages for hello, hooks, decisions, synthesis, and emergency pass-through
- buffers binary IPC frames across partial socket reads
- handles low-level keyboard/mouse hook subscribe and unsubscribe messages
- forwards captured hook events to subscribed binary IPC clients
- queues hook events and resolves one pending hook event at a time
- accepts hook decision replies with a one-second timeout
- removes a client's hook subscriptions after ten consecutive callback delivery/timeout failures
- enables `EVIOCGRAB` while hook subscriptions are active
- handles emergency pass-through by clearing hook subscriptions and releasing grabs
- replays final `PASS` hook events through `uinput`
- suppresses final `BLOCK` hook events by not replaying them
- emits replacement `ksi_input` records for final `MODIFY` hook decisions
- marks events from the daemon's own virtual input device as injected
- suppresses pass-through replay events from re-entering the hook callback path
- coalesces relative X/Y mouse motion at evdev `SYN_REPORT` boundaries
- maps common alphanumeric, punctuation, navigation, numpad, function, modifier, and X-button input
- builds a local binary IPC smoke-test client

Real native-Linux validation across physical keyboards, mice, layouts, and
compositors is still pending.

## Build

Linux build dependencies:

- `cmake`: generates the native build files
- `build-essential`: provides `cc`, libc headers, and `make`
- `pkg-config`: lets CMake locate native libraries
- `libudev-dev`: udev device discovery and hotplug headers/library
- `libevdev-dev`: evdev event decoding headers/library

Ubuntu/Debian:

```bash
sudo apt install cmake build-essential pkg-config libudev-dev libevdev-dev
```

Fedora:

```bash
sudo dnf install cmake gcc gcc-c++ make pkgconf-pkg-config systemd-devel libevdev-devel
```

Arch:

```bash
sudo pacman -S cmake base-devel pkgconf systemd libevdev
```

openSUSE:

```bash
sudo zypper install cmake gcc make pkg-config systemd-devel libevdev-devel
```

```bash
cmake -S native/keysharp-inputd -B native/keysharp-inputd/build
cmake --build native/keysharp-inputd/build
```

Development run:

```bash
native/keysharp-inputd/build/keysharp-inputd --foreground
```

An installed daemon is a systemd socket-activated system service:

```text
/run/keysharp-inputd/keysharp-inputd.sock
```

Install as root to install the binary and units, load `uinput`, and enable the
system socket:

```bash
sudo cmake --install native/keysharp-inputd/build
```

Packaging installs that use `DESTDIR` should run the installed
`keysharp-inputd --install-input-access` command from their post-install step.
The system socket starts the daemon only when a client connects. The daemon
exits after it has been idle with no connected clients; it only grabs evdev
sources while a granted hook subscription is active.

Manual development runs still use the private `$XDG_RUNTIME_DIR` socket by
default. Override that socket path for local tests with:

```bash
native/keysharp-inputd/build/keysharp-inputd --socket /tmp/keysharp-test.sock
```

The installed service owns evdev and uinput access. Keysharp users do not need
and should not be added to the `input` group for `keysharp-inputd`.

Binary IPC smoke test:

```bash
native/keysharp-inputd/build/keysharp-inputd-client
native/keysharp-inputd/build/keysharp-inputd-client --split
```

## Security model

`keysharp-inputd` is designed as a root-owned system service.

- systemd owns the socket at `/run/keysharp-inputd/keysharp-inputd.sock` and starts the daemon on demand
- the service, not Keysharp user code, initiates permission prompts
- The daemon records peer pid/uid/gid with `SO_PEERCRED`.
- The daemon resolves the connecting process executable via `/proc/<pid>/exe`.
- The daemon reads `/proc/<pid>/cmdline` and hashes the executable digest with
  the raw argument vector as the persistent app identity.
- Binary clients must send `CLIENT_HELLO` with requested capabilities.
- The daemon grants only capabilities backed by service-owned device access.
- Unknown process identities are prompted before privileged capabilities are granted.
- `Allow always` decisions are stored in the root-owned system trust store,
  partitioned by peer uid and pruned after 60 days without being seen.
- `Allow once` decisions live only for the current daemon session.

Initial capability flags are:

- `KSI_CAP_HOOK_KEYBOARD`
- `KSI_CAP_HOOK_MOUSE`
- `KSI_CAP_SYNTH_KEYBOARD`
- `KSI_CAP_SYNTH_MOUSE`
- `KSI_CAP_BLOCK_INPUT`

Hook subscriptions require both hook access and the matching synthesis access.
This is intentional: once `EVIOCGRAB` is active, allowed input must be replayed
through `uinput`. If `/dev/uinput` is unavailable, hook subscriptions are denied
instead of risking unreplayed grabbed input.

The system service replaces raw evdev/uinput group access for Keysharp. A
requesting process still needs a daemon permission grant for its uid,
executable digest, and argument-vector identity before privileged hook or
synthesis operations are accepted.

## Architecture

```text
Keysharp scripts
  -> IPC client
  -> keysharp-inputd
  -> platform backend
  -> evdev/udev/uinput
```

The daemon owns:

- device discovery and hotplug
- exclusive Linux evdev grabs
- virtual keyboard and mouse devices
- suppression, replay, and synthesis
- physical input state
- injected-event tagging for `Keysharp Virtual Input`
- multiple Keysharp hook clients
- bounded hook callback dispatch and timeouts
- emergency pass-through/ungrab behavior

Keysharp owns:

- AHK hotkey/hotstring semantics
- script lifecycle
- dynamic context evaluation
- script callbacks and actions
- deciding whether hooked input is passed through, blocked, or replaced

## Protocol

The daemon exposes a Windows-shaped logical input model instead of raw evdev events.
This keeps Linux input compatible with Keysharp's existing Windows-oriented
keyboard/mouse hook and `SendInput` logic.

The protocol surface is intentionally small and explicit:

- client hello/version
- install/remove low-level hook subscription
- low-level keyboard/mouse hook event notification
- hook decision response
- `SendInput`-style synthesize input request
- heartbeat/disconnect
- emergency pass-through/ungrab

The C structs and constants live in `include/keysharp_inputd/protocol.h`.

Avoid exposing raw privileged operations directly. The daemon does not evaluate
AHK hook rules locally. It captures normalized input, dispatches hook callbacks
to Keysharp, applies Keysharp's response, and performs requested synthesis.
