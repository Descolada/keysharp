# Keysharp demos

Example scripts that showcase Keysharp APIs. These are **not** part of the Keysharp library
and are **not** bundled into installs — they live outside the `Keysharp/` project so nothing
here is picked up by the build/publish or the installer.

Some demos `#include` shared files (`HotkeyCard.ks` here, or the bundled `../Keysharp/Scripts/OCR.ks`)
with relative paths, so run them from a checkout of this repo. They aim to be genuinely useful and
cross-platform (Windows / Linux X11 + Wayland / macOS); where a capability is platform-limited, the
script's header comment says so.

| Demo | What it does | Keysharp features it shows |
|------|--------------|----------------------------|
| [OCRSnip.ks](OCRSnip.ks) | Region-select OCR snip: drag a box, OCR the text to the clipboard, box each recognized word (hover to see its text). Ctrl+Shift+O select · Ctrl+Shift+Backspace clear · Ctrl+Shift+Esc exit. | `Overlay` selection UI + crosshair, `Image` + `OCR.ks`, `GetKeyState` click-drag polling, LButton blocking, hover tooltips |
| [WindowTiler.ks](WindowTiler.ks) | Snap the active window to halves / quarters / maximize on its current monitor. Ctrl+Alt + a `Q W E / A S D / Z X C` grid (mirrors screen position) · F centre · R restore. | window actions (`WinMove`/`WinRestore`/`WinMaximize`/`WinActivate`), multi-monitor `MonitorGetWorkArea`, dynamic `Hotkey()`, `Highlight` + `ToolTip` feedback |
| [WindowGrab.ks](WindowGrab.ks) | Grab any window from any point without activating it: Super+Left-drag moves it, Super+Right-drag fades it (left = clearer, right = opaque), Super+Right-click toggles 50% / opaque. | `MouseGetPos` window-under-cursor, `WinMove` (no activate), `WinGetTransparent`/`WinSetTransparent`, physical `GetKeyState` drag polling |
| [ClipboardHistory.ks](ClipboardHistory.ks) | Remembers text you copy; Ctrl+Alt+V opens a keyboard-driven **overlay** picker — 1-9 paste instantly, Up/Down + Enter pick, Esc cancels — pasting back into the window you were in. | `OnClipboardChange`, `A_Clipboard`, an `Image`-rendered `Overlay` list, `HotIf`-scoped hotkeys, `Send` paste (Cmd+V on macOS) |
| [InputHUD.ks](InputHUD.ks) | On-screen keyboard + mouse "keycaster": keys and buttons light up as you physically press them; the layout adapts per-OS; Super+drag moves each HUD; Ctrl+Alt+Shift+Q quits. | one `InputHook` capturing keys **and** mouse (non-suppressing), `Overlay` canvases drawn with `Image` + live `SetImage`, `HotIf`-scoped drag |

[HotkeyCard.ks](HotkeyCard.ks) is a small **shared component** (not a standalone demo): each demo
`#include`s it to show its shortcuts as a persistent, click-to-dismiss cheat-sheet overlay in the
bottom-right corner.

Hotkeys are defined near the top of each script and are easy to rebind if they clash with your
desktop's shortcuts.
