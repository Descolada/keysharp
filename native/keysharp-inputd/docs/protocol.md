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
HOOK_QUARANTINED      REARM_HOOK
SYNTHESIZE_INPUT      SYNTHESIS_RESULT
EMERGENCY_PASSTHROUGH SET_BLOCK_INPUT
GET_INDICATOR_STATE   INDICATOR_STATE_RESULT
GET_POINTER_POSITION  POINTER_POSITION_RESULT
GET_KEY_STATE         KEY_STATE_RESULT
GET_POINTER_BUTTONS   POINTER_BUTTONS_RESULT
IDLE_TIME
LIST_PERMISSIONS      RESET_PERMISSIONS
```

`GET_KEY_STATE` / `KEY_STATE_RESULT` report current logical modifiers, lock-key
state, a logical evdev key bitmap, and an appended physical evdev key bitmap. It
requires `KSI_CAP_HOOK_KEYBOARD`.

`GET_POINTER_BUTTONS` / `POINTER_BUTTONS_RESULT` report mouse button masks. The
legacy `buttons` field remains the physical mask; newer clients read appended
`logical_buttons` and `physical_buttons`. Logical buttons combine the evdev
physical snapshot with Keysharp's queued synthetic button state. It requires
`KSI_CAP_HOOK_MOUSE`.

`IDLE_TIME` is an empty request and a same-type response carrying a validity flag
and milliseconds since the daemon last observed upstream user activity. It
requires an authenticated `CLIENT_HELLO` but no privileged input capability, so
reading `A_TimeIdle` never causes a permission prompt. Until the daemon observes
its first activity event, the validity flag is false. Sharing the request and
response type keeps the addition compatible with older 1.0 daemons: their
8-byte unknown-message status is distinguishable from the 16-byte idle payload.

`LIST_PERMISSIONS` and `RESET_PERMISSIONS` back the `keysharp-inputd trust`
subcommand (see below).

Protocol `1.0` permits a `HEARTBEAT` with correlation id `0` as a one-way grab
lease renewal. The daemon sends no response for that form, so hook-reader
connections can renew without introducing an unexpected receive frame. Other
heartbeat correlation ids retain request/response behavior.

## Authorization

Clients connect through the systemd socket at
`/run/keysharp-inputd/keysharp-inputd.sock`. The daemon authenticates via
`SO_PEERCRED` plus a hash of the peer's executable and argument vector.

Protocol 1.0 is intentionally incompatible with 0.2. `CLIENT_HELLO` carries
requested capabilities, optional flags, and one authenticated connection role:

- `HOOK_STREAM` receives hook events and returns decisions.
- `CALLBACK_RPC` is bound to a hook session and carries RPC made from callbacks.
- `GENERAL_RPC` carries ordinary requests from unrelated script threads.

A successful `HOOK_STREAM` hello returns a random 128-bit session token. A
`CALLBACK_RPC` must present that token and match the hook stream's peer PID and
UID, process start time, executable path, and executable hash. Exactly one callback
RPC may bind to a hook session. Synthesis requests never carry a parent event id or
claim callback ancestry; the daemon decides ancestry from its active callback stack
and the bound socket.

Capabilities and flags are:

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
* Out of band, run `keysharp-inputd trust reset <hash>` (or `--pid <pid>`). The
  subcommand speaks `KSI_MESSAGE_LIST_PERMISSIONS` and `KSI_MESSAGE_RESET_PERMISSIONS`
  to the daemon, which clears the allow/deny bits for the matched record.

The list/reset messages are scoped to the caller's uid. Only root (when the
daemon is running as root) can target another user's records.

## Hook model

The daemon implements hook transport only; Keysharp owns all policy. Keyboard
and mouse decision waits run on independent lane threads, so a stalled keyboard
hook decision does not hold mouse events back (and vice versa). All uinput-bound
writes go through a single output sequencer thread.

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

Each lane has its own decision-timeout deadline and pending event stack. An
ordinary `SYNTHESIZE_INPUT` completes only after every matching low-level hook
has decided and each unsuppressed output has been admitted by the sequencer.
When synthesis arrives on the callback RPC belonging to the active responder,
it is a child transaction: same-lane events are pushed recursively and
cross-lane events are submitted to the other live lane. The complete newest-to-
oldest hook chain runs for every child event before the parent callback resumes.
The managed callback RPC pumps nested `HOOK_EVENT` frames while waiting for its
result and buffers correlation responses which complete child-before-parent.

Recursion is limited to 32 callback transactions and synthesis requests are
limited to 1024 `ksi_input` entries and 4096 expanded low-level hook events. Either
limit rejects the child before hook delivery or uinput output. Recursion-limit and
expanded-size failures use synthesis result details 32 and 33 respectively.

Hook subscriptions require the matching hook access. `MODIFY` decisions and direct
`SYNTHESIZE_INPUT` additionally require the matching synthesis access. Once
`EVIOCGRAB` is active, passed events must be replayable through `uinput`; if
`uinput` is unavailable, subscriptions are denied.

Every subscriber callback has a one-second absolute deadline, including time in
nested synthesis. The first timeout passes the event for that subscriber and
quarantines only that hook type. `HOOK_QUARANTINED` reports the event, generation,
strike and cooldown. Once the overdue callback returns, Keysharp uses the bound
callback RPC to send authenticated `REARM_HOOK`; a heartbeat cannot rearm it.
Cooldowns are 1, 2, 4, 8 and 16 seconds. Sixty seconds without another quarantine
resets the strike history. The fifth consecutive timeout invalidates the hook
session so managed recovery establishes a fresh hook stream and callback RPC.
Keyboard and mouse quarantine state is independent.

`EMERGENCY_PASSTHROUGH` from a client already granted hook access clears all hook
subscriptions, discards pending hook events, and releases all grabs.

Hook subscriptions and non-zero `BlockInput` masks hold a 15-second lease.
Keysharp renews active leases every five seconds using one-way heartbeats.
Expiry clears the client's input state, releases grabs, and disconnects the
client so managed recovery can reconnect or fall back.

Physically pressing `Ctrl+Alt+Backspace` asks the daemon's main event loop to
perform the same fail-open action and enqueue a complete press/release chord for
the display server. It does not depend on a responsive Keysharp client, and it
also clears all `BlockInput` masks. It is not a substitute for killing a daemon
whose main event loop or output path is itself stalled.

`SET_BLOCK_INPUT` requires `KSI_CAP_BLOCK_INPUT` and sets the calling client's
physical input block mask: keyboard, mouse, both, or neither. The daemon drops
blocked physical events while allowing virtual input to continue. Block masks are
client-scoped and are removed when that client disconnects.

The daemon bounds its queues and admits client input atomically. Each hook lane
has a single pending decision slot, so a decision from any client other than the
lane's current responder — or a late decision after the one-second timeout — is
rejected. A `SYNTHESIZE_INPUT` request or `MODIFY` decision that would exceed the
output bounds is rejected with a failure result rather than partially queued, and
capacity is reserved for physical replay and the emergency chord.

## Ordering and timestamps

Synthetic batches receive a trusted daemon monotonic admission timestamp in
nanoseconds. Before starting a batch, the daemon peeks every grabbed physical
device; an earlier kernel `CLOCK_MONOTONIC` event runs first (ties favor the
already-observed physical event). Synthesis also waits for every earlier physical
hook callback to reach output admission. After a synthetic batch starts, its
remaining members retain priority over later physical and unrelated synthetic input.
Nested callback synthesis is the normal exception and preempts its parent.
Emergency passthrough and fail-open recovery may preempt everything.

The caller's `INPUT.time` is never used for scheduling. A nonzero value is copied
to the generated hook payload, while zero is replaced with the current monotonic
millisecond value. Past and future values are therefore visible as metadata but
are delivered immediately. Synthetic events retain `device_id = 0`; physical
events retain their daemon-assigned device id.

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

`IDLE_TIME` is fed by keyboard, button, touch, pen, gamepad/media-key,
relative-motion, wheel, and pointer-axis events from tracked evdev devices.
Devices tracked only for idle time are opened read-only and are never grabbed or
hook-dispatched. Keysharp's own uinput devices are excluded before the idle
timestamp is updated or hooks are dispatched, so replayed and synthetic input
cannot masquerade as new physical activity. Event timestamps use
`CLOCK_MONOTONIC`; if an IPC query races queued device data, the daemon ingests
that data first and computes the elapsed duration from the newest processed
kernel event timestamp. A capless idle query does not create Keysharp's uinput
devices or elevate the observer thread to real-time scheduling; those resources
are activated only for explicitly requested privileged input functionality.

## Injected-event tagging

The daemon's `uinput` device is identified by:

```
name     Keysharp Virtual Input
bustype  BUS_VIRTUAL
vendor   0x0FAC   (masquerades as keyd's vendor so keyd won't re-grab our output; see protocol.h)
product  0x0001
```

The device is identified as *ours* by name **and** vendor (`0x0FAC` is shared with
keyd, so name is the discriminator); keyd's own `keyd virtual keyboard` therefore
does not match and remains a valid interception target for us to grab.

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
