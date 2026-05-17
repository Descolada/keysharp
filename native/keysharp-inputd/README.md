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

Run:

```bash
native/keysharp-inputd/build/keysharp-inputd --foreground
```

The default socket path is:

```text
$XDG_RUNTIME_DIR/keysharp/keysharp-inputd.sock
```

The daemon creates `$XDG_RUNTIME_DIR/keysharp` as `0700` and the socket as
`0600`. Override the socket path for local tests with:

```bash
native/keysharp-inputd/build/keysharp-inputd --socket /tmp/keysharp-test.sock
```

`uinput` synthesis requires write access to `/dev/uinput`. On many systems that
means running as root or installing a udev rule/group permission for the daemon.

Binary IPC smoke test:

```bash
native/keysharp-inputd/build/keysharp-inputd-client /tmp/keysharp-inputd.sock
native/keysharp-inputd/build/keysharp-inputd-client /tmp/keysharp-inputd.sock --split
```

## Security model

`keysharp-inputd` is currently designed as a per-user daemon.

- The socket lives under `$XDG_RUNTIME_DIR/keysharp` by default.
- The socket directory is private to the user (`0700`).
- The socket is owner-only (`0600`).
- Accepted clients must have the same Unix uid as the daemon.
- The daemon records peer pid/uid/gid with `SO_PEERCRED`.
- Binary clients must send `CLIENT_HELLO` with requested capabilities.
- The daemon grants only capabilities backed by the current user's device access.

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

Linux device permissions are user/group based. The daemon verifies the
connecting uid and grants only capabilities backed by the daemon user's access
to `/dev/input` and `/dev/uinput`, but it does not yet maintain a per-app trust
database.

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
