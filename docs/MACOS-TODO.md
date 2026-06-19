# macOS / Cocoa TODO backlog

macOS-specific gaps and follow-ups. Most surfaced as graceful no-ops that log via
`Ks.OutputDebugLine(...)` rather than throwing, so the app stays usable — but the behavior is
missing. Pick these up from a macOS checkout (the build targets `net10.0` with `OSX` defined and
references `Eto.Mac` / `MonoMac`).

For shared architecture and cross-cutting features that also need a macOS implementation, see
[`CROSSPLATFORM-TODO.md`](CROSSPLATFORM-TODO.md). Linux work is in [`LINUX-TODO.md`](LINUX-TODO.md).

---

## 1. Implementable gaps

- [ ] **Window `Enabled` set.** `Keysharp.Core/Internals/Window/MacOS/WindowItem.cs:152`
  Currently logs "Enabled state is not implemented for macOS windows." Win32 `EnableWindow`
  disables input to a window; the closest Cocoa equivalent is making the `NSWindow` ignore mouse
  events / not accept key input (`ignoresMouseEvents`, or removing it from the key/main chain).
  Decide on the closest faithful behavior and implement, or document as N/A if no good fit.

- [ ] **Window transparency — alpha.** `Keysharp.Core/Internals/Window/MacOS/WindowItem.cs:315`/`:318`
  Per-window alpha (`WinSetTransparent`) maps cleanly to `NSWindow.alphaValue` and should be
  implementable. Note: per-pixel **color-key** transparency (`WinSetTransColor`, Win32
  `LWA_COLORKEY`) has no Cocoa equivalent — keep that part as a documented N/A.

- [ ] **Joystick polling (macOS half).** see `Joystick.cs:101` via [`CROSSPLATFORM-TODO.md`](CROSSPLATFORM-TODO.md)
  New subsystem via IOKit HID or the GameController framework. Mirror the Windows polling logic
  (button-mask XOR → newly-pressed → buffer hotkey messages).

- [ ] **Mouse button swap (macOS half).** see `KeyboardMouseSender.cs:747` via [`CROSSPLATFORM-TODO.md`](CROSSPLATFORM-TODO.md)
  Honor the system's primary/secondary mouse-button setting when sending clicks.

---

## 2. Likely platform-inherent (confirm, then document as N/A)

These currently no-op with a log line; they probably have no faithful Cocoa equivalent, in which
case the action is to document them rather than implement:

- [ ] **Window styles / ex-styles.** `WindowItem.cs:240` (styles) and `:160` (ex-styles)
  Raw Win32 `WS_`/`WS_EX_` style integers — no Cocoa equivalent (same situation as Linux). Confirm
  and document as N/A; expose portable attributes through Eto's typed properties instead.

- [ ] **Drive: CD/DVD status.** `Keysharp.Core/Internals/Mapper/MacOS/Drive.cs:44`
  "Obtaining the status of the CD/DVD drive is not supported on macOS." Modern Macs have no optical
  drive; likely permanent N/A — document.

- [ ] **Drive: eject lock / unlock.** `Drive.cs:60` (lock) and `:71` (unlock)
  No macOS API to lock the eject mechanism; likely permanent N/A — document.

---

## 3. Verify existing fallbacks (robustness, not gaps)

These already have working fallbacks; confirm they behave well on real hardware/permissions:

- [ ] **Window activation fallback.** `Keysharp.Core/Internals/Window/MacOS/MacNativeWindows.cs:368`
  Fallback path for when `NSRunningApplication` activation is unavailable or denied — verify under
  restricted Accessibility/Automation permissions.

- [ ] **Char-mapper ASCII fallback.** `Keysharp.Core/Internals/Input/MacOS/MacCharMapperProvider.cs:505`
  Confirm non-ASCII layouts (e.g. Estonian) translate correctly and the ASCII source is only used
  as a fallback.

- [ ] **Hook snapshot fallback for key state.** `Keysharp.Core/Internals/Input/Hooks/MacOS/MacHookThread.cs:123`
  Falls back to hook snapshots when the native key-state query is unavailable — verify accuracy.

---

## Notes

- macOS shares the "common base + OS-derived" architecture refactors (`InputType`, `WindowItem`
  naming, `StatusBar` PID, hook message refs) tracked in [`CROSSPLATFORM-TODO.md`](CROSSPLATFORM-TODO.md);
  the macOS derivations should fall out of those refactors.
- The macOS build is currently the least-exercised target — a broad pass running the curated suite
  on macOS is worthwhile before relying on any of the above.
