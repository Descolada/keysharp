# Linux / X11 / Wayland TODO backlog

Deferred Linux-specific work from the pre-release source-TODO triage (2026-06-19). These could
**not** be implemented or validated from a Windows host: the Linux build targets `net10.0` (not
`net10.0-windows`), pulls in `Eto.Gtk` / `Tmds.DBus`, and only defines the `LINUX` constant on a
Linux OS — so none of this code compiles or runs under the Windows build.

For shared architecture and cross-cutting features that also need a Linux implementation
(InputType base/derived split, hook message refs, StatusBar PID abstraction, mouse button swap,
joystick polling, `DllCall` via reflection, derived-class naming), see
[`CROSSPLATFORM-TODO.md`](CROSSPLATFORM-TODO.md). macOS work is in [`MACOS-TODO.md`](MACOS-TODO.md).

---

## 0. Foundation (do first)

- [ ] **Build the Linux target and confirm a clean baseline.**
  `net10.0`, with `Eto.Gtk` and `Tmds.DBus` restored. Establishes that the tree compiles on Linux
  before touching anything.

- [ ] **Verify the Hyprland cursor-position implementation.**
  `Keysharp.Core/Internals/Window/Linux/Wayland/WaylandBackend.cs` (~953, `HyprlandBackend.TryGetCursorPos`).
  Implemented but **unverified** — written on Windows. Test on a real Hyprland session: connect to
  `$XDG_RUNTIME_DIR/hypr/$HYPRLAND_INSTANCE_SIGNATURE/.socket.sock` (with `/tmp/hypr/...` fallback),
  send `cursorpos`, parse the `X, Y` reply. Confirm `MouseGetPos` returns correct coordinates.

---

## 1. Genuine Linux platform gaps (real missing functionality — testable wins)

- [ ] **X11 control focus.** `Keysharp.Core/Internals/Platform/Unix/ControlManager.cs:336`
  `item.Active = true` activates the *containing top-level window*, not the child control, so
  `ControlFocus` doesn't actually focus a sub-control on X11. Needs an `XSetInputFocus` P/Invoke
  (not yet declared in `Keysharp.Core/Internals/Window/Linux/X11/Xlib.cs` — only `XRaiseWindow`
  exists). Watch for `BadMatch` on non-viewable windows and pick the right `RevertTo` value.
  Compare against the Windows path (`AttachThreadInput` + `SetFocus`).

- [ ] **`Enabled` get/set for native (non-Eto) controls.**
  `Keysharp.Core/Internals/Window/Linux/WindowItem.cs:268` (get) and `:275` (set).
  Only works when the handle maps to an Eto/`Control.FromHandle` control; native X11 windows fall
  through. Needs an X11 mechanism for enable/disable state.

- [ ] **GTK styles / Keyview tearing.** `Keysharp.Core/Builtins/Gui/WindowX.cs:302` and `:304`
  Setting `MONO_VISUAL_STYLES=gtkplus` is needed for GTK styles under Mono but causes tearing in
  Keyview. Needs a Linux GUI to reproduce and investigate. Also (`:302`) GTK isn't working on the
  Windows build yet — low priority unless GTK-on-Windows becomes a goal.

---

## 2. Linux implementations of cross-cutting features

Umbrellas (Windows logic to mirror) are in [`CROSSPLATFORM-TODO.md`](CROSSPLATFORM-TODO.md); the
Linux-specific implementation work is:

- [ ] **Joystick polling — Linux backend.** `Keysharp.Core/Internals/Input/Joystick/Joystick.cs:101`
  No Linux joystick layer exists yet. Implement via evdev (`/dev/input/js*` or `event*`) or SDL,
  mirroring the Windows polling logic (XOR previous/current button masks → newly-pressed → buffer
  hotkey messages). See the method's own docs for why polling (vs. capture) is preferred.

- [ ] **Mouse button swap — Unix sender.** `Keysharp.Core/Internals/Input/Keyboard/KeyboardMouseSender.cs:747`
  Implement the logical→physical button swap in the Unix sender (Windows uses
  `GetSystemMetrics(SM_SWAPBUTTON)`).

---

## 3. Linux-specific refactor

- [ ] **Convert the X singleton into an instance data class.**
  `Keysharp.Core/Internals/Window/Linux/XConnection.cs:16`
  `XConnectionSingleton` uses a `static instance`; make it an instance owned like the Windows side
  does. Also revisit the related `BadWindow` suppression hack at `XConnection.cs:25` (works, but is
  a documented workaround).

---

## Out of scope (externally blocked — nothing to do until upstream changes)

- **sway cursor position** — `WaylandBackend.cs:935`. sway's IPC (`$SWAYSOCK`) doesn't expose the
  cursor position; not implementable without sway-side support.
- **COSMIC cursor position** — `WaylandBackend.cs:970`. No standard cursor-position Wayland protocol
  exists on COSMIC yet (`zcosmic_toplevel_info_v1` carries geometry, not the cursor).
