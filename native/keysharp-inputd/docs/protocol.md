# keysharp-inputd IPC protocol

`keysharp-inputd` exposes a Windows-shaped logical input protocol over a Unix
socket. The daemon uses evdev, udev, and uinput internally, but Keysharp sees
the same concepts it already uses for Windows hooks and `SendInput`.

C constants and structs: `include/keysharp_inputd/protocol.h`

## Framing

Every message has a `ksi_message_header` carrying:

- protocol major/minor version
- message type
- client id
- correlation id (for request/response pairs)
- payload byte size

Frames arrive over a stream socket and may be split or coalesced. The daemon
buffers per-client input and disconnects clients that send invalid versions,
invalid sizes, or oversized frames.

Supported message types:

```
CLIENT_HELLO          HEARTBEAT
SUBSCRIBE_HOOK        UNSUBSCRIBE_HOOK
HOOK_EVENT            HOOK_DECISION
SYNTHESIZE_INPUT      SYNTHESIS_RESULT
EMERGENCY_PASSTHROUGH SET_BLOCK_INPUT
GET_INDICATOR_STATE   INDICATOR_STATE_RESULT
GET_POINTER_POSITION  POINTER_POSITION_RESULT
```

## Authorization

Clients connect through the systemd socket at
`/run/keysharp-inputd/keysharp-inputd.sock`. The daemon authenticates via
`SO_PEERCRED` plus a hash of the peer's executable and argument vector.

`CLIENT_HELLO` carries requested capabilities and optional flags:

```
KSI_CAP_HOOK_KEYBOARD   KSI_CAP_HOOK_MOUSE
KSI_CAP_SYNTH_KEYBOARD  KSI_CAP_SYNTH_MOUSE
KSI_CAP_BLOCK_INPUT

KSI_CLIENT_HELLO_FLAG_FORCE_PROMPT
```

The daemon replies with granted capabilities. Unknown identities trigger a
permission prompt; `Allow always` decisions are persisted per uid in the
shared root-owned keysharp-trust store and pruned after 60 days.
Keysharp requests both keyboard and mouse bits for its user-facing input
monitoring permission, and both synthesis bits for its user-facing input
synthesis permission. The protocol still keeps the device bits separate.

`Deny` is also persisted — once denied, the daemon will not prompt for that
capability again until the user explicitly opts back in. There are two ways
to opt back in:

* From inside a script, call `Ks.RequestCapabilities(...)`, which sends
  `KSI_CLIENT_HELLO_FLAG_FORCE_PROMPT` and bypasses the persistent deny.
* Out of band, run `keysharp-trust reset <hash>` (or `--pid <pid>`). The CLI
  speaks `KSI_MESSAGE_LIST_PERMISSIONS` and `KSI_MESSAGE_RESET_PERMISSIONS`
  to the daemon, which clears the allow/deny bits for the matched record.

The list/reset messages are scoped to the caller's uid. Only root (when the
daemon is running as root) can target another user's records.

## Hook model

The daemon implements hook transport only. Keysharp owns all policy. Keyboard
and mouse hook delivery run on independent lane threads, so a stalled keyboard
hook callback no longer holds mouse events back (and vice versa). All
uinput-bound writes go through a single output sequencer thread.

```
evdev / main thread       lane threads             sequencer thread
─────────────────────     ────────────────         ────────────────
classify hook event  ──►  (kbd lane)
                            │  send HOOK_EVENT
                            │  await decision/timeout
                            └─►  action          ──►  uinput
classify hook event  ──►  (mouse lane)
                            │  …
                            └─►  action          ──►
SYNTHESIZE_INPUT     ──────────────────────────────►
```

Each lane has its own decision-timeout deadline and its own pending event,
so per-lane FIFO ordering is preserved but cross-lane ordering is relaxed:
a SYNTHESIZE_INPUT from a client is queued onto the sequencer immediately
and no longer waits for an in-flight hook decision to finalize. This matches
Windows, where SendInput does not block on pending low-level hooks.

Hook subscriptions require hook access **and** the matching synthesis access.
Once `EVIOCGRAB` is active, passed events must be replayable through `uinput`;
if `uinput` is unavailable, subscriptions are denied.

Hook decisions have a one-second timeout; a timeout currently passes the event.
After ten consecutive delivery/timeout failures the daemon removes that client's
subscriptions and releases grabs so a crashed script cannot keep input trapped.

`EMERGENCY_PASSTHROUGH` from a client already granted hook access clears all hook
subscriptions, discards pending hook events, and releases all grabs.

`SET_BLOCK_INPUT` requires `KSI_CAP_BLOCK_INPUT` and sets the calling client's
physical input block mask: keyboard, mouse, both, or neither. The daemon drops
blocked physical events while allowing virtual input to continue. Block masks are
client-scoped and are removed when that client disconnects.

### Keyboard hook event fields

Modelled after `KBDLLHOOKSTRUCT` + low-level hook `wParam`:

| Field | Description |
|---|---|
| `message` | `WM_KEYDOWN`, `WM_KEYUP`, `WM_SYSKEYDOWN`, `WM_SYSKEYUP` |
| `vk_code` | Windows virtual-key code |
| `scan_code` | Backend low-level key code (e.g. Linux evdev `KEY_*`) |
| `flags` | `LLKHF_EXTENDED`, `LLKHF_INJECTED`, `LLKHF_ALTDOWN`, `LLKHF_UP` |
| `time_ms` | Event timestamp |
| `extra_info` | Equivalent of `dwExtraInfo` |
| `device_id` | Daemon-assigned physical device id |

### Mouse hook event fields

Modelled after `MSLLHOOKSTRUCT` + low-level hook `wParam`:

| Field | Description |
|---|---|
| `message` | `WM_MOUSEMOVE`, `WM_LBUTTONDOWN`, `WM_MOUSEWHEEL`, etc. |
| `x`, `y` | Pointer coordinates where available |
| `mouse_data` | Wheel delta or X-button value |
| `flags` | `LLMHF_INJECTED`-style flags |
| `time_ms` | Event timestamp |
| `extra_info` | Equivalent of `dwExtraInfo` |
| `device_id` | Daemon-assigned physical device id |

### evdev translation

```
KEY_A        → VK_A + scan code KEY_A
BTN_LEFT     → WM_LBUTTONDOWN/UP
REL_WHEEL    → WM_MOUSEWHEEL + WHEEL_DELTA-compatible mouse_data
BTN_SIDE     → WM_XBUTTONDOWN/UP + XBUTTON1-compatible mouse_data
BTN_EXTRA    → WM_XBUTTONDOWN/UP + XBUTTON2-compatible mouse_data
```

Relative X/Y motion is coalesced at `SYN_REPORT` boundaries so one hardware
packet normally becomes one `WM_MOUSEMOVE` event.

## Synthesis model

Input synthesis is modelled after Windows `SendInput` (`ksi_input`).

Keyboard synthesis fields: `vk`, `scan`, `flags` (`KEYEVENTF_*`), `time`, `extra_info`.

Mouse synthesis fields: `dx`, `dy`, `mouse_data`, `flags` (`MOUSEEVENTF_*`), `time`, `extra_info`.

The daemon translates requests into `uinput` events. `extra_info` is present in
the protocol for cross-platform shape compatibility but is not preserved by Linux
`uinput`.

`MODIFY` hook decisions append replacement `ksi_input` entries after the decision
header and require the same synthesis capabilities as direct `SYNTHESIZE_INPUT`.

## Device state queries

`GET_POINTER_POSITION` returns the last absolute pointer sample (raw `ABS_X`,
`ABS_Y`, and each axis's min/max). Keysharp maps this range to screen coordinates
before exposing the value to scripts. Requires `KSI_CAP_HOOK_MOUSE`.

`GET_INDICATOR_STATE` / `INDICATOR_STATE_RESULT` report the current Caps Lock,
Num Lock, and Scroll Lock LED state.

## Injected-event tagging

The daemon's `uinput` device is identified by:

```
name     Keysharp Virtual Input
bustype  BUS_VIRTUAL
vendor   0x4b53
product  0x0001
```

Events from this device set `LLKHF_INJECTED` / `LLMHF_INJECTED` in hook callback
flags. Pass-through replay events are suppressed from re-entering the callback
path; direct `SYNTHESIZE_INPUT` events are not, so Keysharp can apply
`SendLevel`-style policy to its own synthetic input.

## Multiple scripts

Multiple Keysharp processes are modelled as ordered hook clients. Each
subscription carries a client id, hook type, and subscription order. The daemon
owns delivery order, timeout enforcement, device ownership, replay, suppression,
and synthesis. Individual Keysharp processes do not open or grab physical devices
themselves.
