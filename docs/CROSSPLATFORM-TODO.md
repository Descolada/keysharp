# Cross-platform TODO backlog

Shared, OS-agnostic work from the pre-release source-TODO triage (2026-06-19): architectural
abstractions and features that aren't tied to one platform but need a per-OS implementation.

OS-specific implementation tasks are tracked separately:
- **Linux / X11 / Wayland** → [`LINUX-TODO.md`](LINUX-TODO.md)
- **macOS / Cocoa** → [`MACOS-TODO.md`](MACOS-TODO.md)

---

## Architecture: "Windows-first → common base + OS-derived" refactors

Several core classes were written Windows-first with a note to factor out a platform-neutral
base and OS-specific derivations. These are the highest-value structural cleanups, but they
touch core subsystems — do them post-baseline and run the curated suite on **both** Windows and
Linux (and macOS where possible).

- [ ] **`InputType` → common base + OS-derived.** `Keysharp.Core/Internals/Input/InputType.cs:20`
  Most fields (buffer, end-chars, modifiers) are platform-agnostic; isolate the Windows-specific
  parts into a derived class so Linux/macOS can derive their own.

- [ ] **Hook message queue: pass object references, not array indices.**
  `Keysharp.Core/Internals/Input/Hooks/HookThread.cs:3631`
  `AHK_HOOK_HOTKEY` carries `hotkeyIDToPost` as a `wParam` index into `shk[]`. Passing the resolved
  object is cleaner and removes index/stale-array hazards. Deep change to the messaging path —
  test hotkey delivery thoroughly on every platform.

- [ ] **Abstract `StatusBar` thread/PID lookup into the common base.**
  `Keysharp.Core/Internals/Window/Windows/StatusBar.cs:96`
  `GetOwningPid` uses Win32 `GetWindowThreadProcessId`; could be hoisted to `StatusBarBase` if the
  PID/thread lookup is abstracted per OS. Minor.


---

## Features that need a non-Windows implementation

- [ ] **Mouse button swap (logical → physical) on non-Windows.**
  `Keysharp.Core/Internals/Input/Keyboard/KeyboardMouseSender.cs:747`
  Resolved on Windows via `GetSystemMetrics(SM_SWAPBUTTON)`. Needs the equivalent in the Unix
  sender (and macOS) so `Send {Click}` honors swapped buttons there.
  - Linux part: see [`LINUX-TODO.md`](LINUX-TODO.md)
  - macOS part: see [`MACOS-TODO.md`](MACOS-TODO.md)

- [ ] **Joystick polling on non-Windows.** `Keysharp.Core/Internals/Input/Joystick/Joystick.cs:101`
  `PollJoysticks` is `#if WINDOWS` only (`joyGetPosEx`). No non-Windows joystick layer exists yet.
  Mirror the Windows logic (XOR previous/current button masks to find newly-pressed buttons, buffer
  hotkey messages). New subsystem on each OS:
  - Linux: evdev (`/dev/input/js*` or `event*`) or SDL — see [`LINUX-TODO.md`](LINUX-TODO.md)
  - macOS: IOKit HID / GameController — see [`MACOS-TODO.md`](MACOS-TODO.md)

---

## .NET / managed interop — already covered by the `Clr` class

- [x] **Calling into .NET DLLs.** (`Keysharp.Core/Builtins/Dll.cs:87` TODO resolved)
  This is already implemented — not via `DllCall`, but by the dedicated `Clr` class
  (`Keysharp.Core/Builtins/Clr/Clr.cs`). `Clr.Load(...)` loads a managed assembly and reflects over
  its namespaces, types, instances, generics, indexers, `for`-in enumerators, and delegates — more
  dynamic than overloading `DllCall` would be. Usage example in
  `Keysharp.Tests/Code/external-clr.ahk` (`System := Clr.Load("System")`, then
  `System.Text.StringBuilder(...)`, etc.). A `DllCall`-syntax shim for managed methods would be
  redundant sugar at best; `DllCall` stays focused on native (C ABI) calls.
