# keysharp-kwin-screencap

`keysharp-kwin-screencap` is the privileged Linux screen capture helper used for KDE
Wayland sessions. It checks the shared keysharp-trust store before calling
`org.kde.KWin.ScreenShot2.CaptureArea`.

This helper is optional. Portable or source-tree Keysharp runs can omit it, but
KDE Wayland screen capture will be reported as unsupported until the helper is
installed with its root-owned desktop file and setuid binary.

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
cmake -S native/keysharp-kwin-screencap -B native/keysharp-kwin-screencap/build
cmake --build native/keysharp-kwin-screencap/build
```

This also builds the shared `keysharp-trust` static library from
`native/keysharp-common`.

## Install

```bash
sudo cmake --install native/keysharp-kwin-screencap/build
```

This installs the binary, installs `/usr/share/applications/keysharp-kwin-screencap.desktop`,
and sets the helper to root-owned setuid mode. The helper must be owned by root
and executable as setuid root because KWin's `org.kde.KWin.ScreenShot2` D-Bus
interface is privileged. KWin expects the desktop file in a root-owned system
application directory, and it must contain an `Exec=` path matching the installed
helper so KWin can authorize the restricted `ScreenShot2` interface.

Verify a local install with:

```bash
grep -R "org.kde.KWin.ScreenShot2" /usr/share/applications
stat -c '%U %G %a %n' /usr/local/bin/keysharp-kwin-screencap
grep '^Exec=' /usr/share/applications/keysharp-kwin-screencap.desktop
```

Packaged installs use `/usr/local/lib/keysharp/keysharp-kwin-screencap` instead and
the Linux installer rewrites the desktop file automatically:

```bash
sudo chown root:root /usr/local/lib/keysharp/keysharp-kwin-screencap
sudo chmod 4755 /usr/local/lib/keysharp/keysharp-kwin-screencap
```

## Trust Store

Persistent `Allow always` decisions are shared with `keysharp-inputd` in:

```text
/var/lib/keysharp-trust/permissions.tsv
```

The store is created automatically on first use and records one entry per
requesting app plus CLI argument identity, with all granted Keysharp native
capabilities accumulated in that entry.

Denied decisions are not persisted. The managed Keysharp process remembers a
screen capture denial for its current process session to avoid repeat prompts;
an explicit `RequestCapabilities("ScreenCapture")` call can prompt again.
