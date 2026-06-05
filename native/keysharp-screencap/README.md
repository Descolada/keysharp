# keysharp-screencap

`keysharp-screencap` is the privileged Linux screen capture helper used for Wayland
sessions. It checks the shared keysharp-trust store before performing any capture.

On **KDE Wayland** it runs as a long-lived serve process and calls
`org.kde.KWin.ScreenShot2.CaptureArea` directly.

On **GNOME Wayland** and other compositors it is used only for the trust gate
(`--authorize` mode). The actual capture is performed by the Keysharp GNOME Shell
extension, which runs inside the compositor and does not require a setuid helper.

This helper is optional. Portable or source-tree Keysharp runs can omit it, but
Wayland screen capture (both KWin and GNOME paths) will be reported as unsupported
until the helper is installed with its root-owned desktop file and setuid binary.

## Build

Dependencies:

| Package | Purpose |
|---|---|
| `cmake`, `make` | build system |
| `build-essential` / `gcc` | C compiler and libc headers |
| `pkg-config` | library discovery |
| `libglib2.0-dev` / `glib2-devel` | GIO and GUnixFDList D-Bus support |

```bash
# Ubuntu/Debian
sudo apt install cmake build-essential pkg-config libglib2.0-dev

# Fedora
sudo dnf install cmake gcc make pkgconf-pkg-config glib2-devel

# Arch
sudo pacman -S cmake base-devel pkgconf glib2

# openSUSE
sudo zypper install cmake gcc make pkg-config glib2-devel
```

From the repository root:

```bash
cmake -S native/keysharp-screencap -B native/keysharp-screencap/build
cmake --build native/keysharp-screencap/build
```

This also builds the shared `keysharp-trust` static library from
`native/keysharp-common`.

## Install

```bash
sudo cmake --install native/keysharp-screencap/build
```

This installs the binary, installs `/usr/share/applications/keysharp-screencap.desktop`,
and sets the helper to root-owned setuid mode. The helper must be owned by root
and executable as setuid root because KWin's `org.kde.KWin.ScreenShot2` D-Bus
interface is privileged. KWin expects the desktop file in a root-owned system
application directory, and it must contain an `Exec=` path matching the installed
helper so KWin can authorize the restricted `ScreenShot2` interface.

Verify a local install with:

```bash
grep -R "org.kde.KWin.ScreenShot2" /usr/share/applications
stat -c '%U %G %a %n' /usr/local/bin/keysharp-screencap
grep '^Exec=' /usr/share/applications/keysharp-screencap.desktop
```

Packaged installs use `/usr/local/lib/keysharp/keysharp-screencap` instead and
the Linux installer rewrites the desktop file automatically:

```bash
sudo chown root:root /usr/local/lib/keysharp/keysharp-screencap
sudo chmod 4755 /usr/local/lib/keysharp/keysharp-screencap
```

## Trust Store

Persistent `Allow always` decisions are shared with `keysharp-inputd` in:

```text
/var/lib/keysharp-trust/permissions.tsv
```

The store is created automatically on first use and records one entry per
requesting app plus CLI argument identity, with all granted Keysharp native
capabilities accumulated in that entry.

`Allow once` decisions on KWin are kept in the long-lived `keysharp-screencap
--serve` process. Keysharp starts that persistent helper during screen capture
authorization, so the same helper handles later capture requests without a
second prompt. The decision is lost when the helper or Keysharp process exits.

On GNOME, `Allow once` decisions are kept for the duration of the Keysharp
process by the in-memory cache in `KWinScreenCaptureHelper.AuthorizeOnly`.

Denied decisions are persisted in the same store as grants. After a deny,
Keysharp will not re-prompt for screen capture until the user explicitly clears
or overrides the denial. Two ways to re-ask:

* An explicit Keysharp `RequestCapabilities("ScreenCapture")` call sends a
  force-prompt request.
* `keysharp-trust list` shows the records stored for the current user, and
  `keysharp-trust reset <hash>` (or `--pid <pid>`) clears allow/deny bits so
  the next capture asks from scratch. The CLI talks to `keysharp-inputd`, so
  users do not need root.
