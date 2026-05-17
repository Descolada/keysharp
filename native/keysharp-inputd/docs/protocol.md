# keysharp-inputd IPC protocol

`keysharp-inputd` exposes a Windows-shaped logical input protocol.

The daemon may use evdev, udev, and uinput internally on Linux, but Keysharp
sees the same core concepts it already uses for Windows hooks and `SendInput`.

```text
Linux evdev/uinput
  <-> keysharp-inputd platform backend
  <-> Windows-like Keysharp input protocol
  <-> Keysharp keyboard/mouse hook and send logic
```

The C protocol constants and structs live in:

```text
include/keysharp_inputd/protocol.h
```

The daemon supports these framed binary messages:

- `CLIENT_HELLO`
- `HEARTBEAT`
- `SUBSCRIBE_HOOK`
- `UNSUBSCRIBE_HOOK`
- `HOOK_EVENT`
- `HOOK_DECISION`
- `SYNTHESIZE_INPUT`
- `SYNTHESIS_RESULT`
- `EMERGENCY_PASSTHROUGH`

The daemon socket is binary-only. There is no text command mode.

Frames are read from a stream socket and may arrive split or coalesced. The
daemon buffers per-client input, dispatches all complete frames, and disconnects
clients that send invalid versions, invalid sizes, or oversized frames.

## Authorization

Clients must connect through the per-user Unix socket and pass same-uid peer
credential checks. The daemon uses `SO_PEERCRED` to record peer pid/uid/gid.

Privileged binary commands require a prior `CLIENT_HELLO` message. The hello
payload carries requested capabilities:

```text
KSI_CAP_HOOK_KEYBOARD
KSI_CAP_HOOK_MOUSE
KSI_CAP_SYNTH_KEYBOARD
KSI_CAP_SYNTH_MOUSE
KSI_CAP_BLOCK_INPUT
```

The daemon replies with granted capabilities. A capability is granted only when
the current daemon user has the corresponding device access. For example,
synthesis requires write access to `/dev/uinput`.

## Goals

- Preserve Keysharp's Windows-oriented keyboard/mouse semantics.
- Keep raw evdev codes behind the daemon boundary.
- Allow multiple Keysharp script processes to share one privileged input owner.
- Support blocking hooks, pass-through hooks, and input synthesis.
- Keep hook policy in Keysharp, not in the daemon.

## Message Framing

Every message carries:

- protocol major/minor version
- message type
- client id
- correlation id where a response is expected
- payload byte size

This is represented by `ksi_message_header`.

## Hook Model

The daemon implements low-level hook transport. It does not understand or
evaluate AHK hotkey, hotstring, or context rules. Keysharp owns those semantics.

The daemon's hook path is:

```text
physical event
  -> platform backend normalizes event
  -> daemon dispatches HOOK_EVENT to subscribed Keysharp client(s)
  -> Keysharp returns PASS, BLOCK, or MODIFY
  -> daemon replays, suppresses, or synthesizes accordingly
```

It queues hook events, processes one pending hook event at a time, sends that
event to subscribed clients sequentially, accepts `HOOK_DECISION` replies, and
applies the final decision. Each callback has a one-second decision timeout.
Timeouts currently fall back to `PASS` for that client.

The daemon tracks consecutive hook callback failures per client. Hook delivery
send failures and hook decision timeouts count as failures; a valid hook
decision resets the count. After ten back-to-back failures, the daemon removes
that client's hook subscriptions and recalculates evdev grab state so a broken
client cannot keep the system trapped behind a grab.

When hook subscriptions are active, the daemon enables `EVIOCGRAB` on tracked
devices. Final `PASS` events are replayed through `uinput`; final `BLOCK`
events are suppressed by not replaying them. Final `MODIFY` decisions carry
replacement `ksi_input` records, which the daemon emits through the same
`SendInput`-style synthesis path.

Hook subscriptions require both hook access and the matching synthesis access
because grabbed input cannot be safely passed through without `uinput` replay.

Keyboard hook events are modeled after `KBDLLHOOKSTRUCT` plus the low-level
keyboard hook `wParam` message:

```text
message       WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP
vk_code       Windows virtual-key code
scan_code     Windows-like scan code when known
flags         LLKHF_EXTENDED, LLKHF_INJECTED, LLKHF_ALTDOWN, LLKHF_UP
time_ms       event timestamp
extra_info    equivalent of dwExtraInfo
device_id     daemon-assigned physical device id
native_code   backend-native code, such as Linux evdev KEY_*
```

Mouse hook events are modeled after `MSLLHOOKSTRUCT` plus the low-level mouse
hook `wParam` message:

```text
message       WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_MOUSEWHEEL, etc.
x, y          pointer coordinates where available
mouse_data    wheel delta or X button value
flags         LLMHF_INJECTED-style flags
time_ms       event timestamp
extra_info    equivalent of dwExtraInfo
device_id     daemon-assigned physical device id
native_code   backend-native code, such as Linux evdev BTN_*
```

The daemon translates platform input into this model:

```text
evdev KEY_A       -> VK_A + scan code + native KEY_A
evdev BTN_LEFT    -> WM_LBUTTONDOWN/UP
evdev REL_WHEEL   -> WM_MOUSEWHEEL + WHEEL_DELTA-compatible mouse_data
evdev BTN_SIDE    -> WM_XBUTTONDOWN/UP + XBUTTON1-compatible mouse_data
evdev BTN_EXTRA   -> WM_XBUTTONDOWN/UP + XBUTTON2-compatible mouse_data
```

Linux relative X/Y motion is coalesced at evdev `SYN_REPORT` boundaries so one
hardware packet normally becomes one `WM_MOUSEMOVE` hook event.

The Linux backend recognizes the daemon's own `uinput` device by both name and
input id:

```text
name      Keysharp Virtual Input
bustype   BUS_VIRTUAL
vendor    0x4b53
product   0x0001
```

For now, only events from `Keysharp Virtual Input` are marked artificial. Those
events set `LLKHF_INJECTED` or `LLMHF_INJECTED` in callback flags.

Allowed grabbed physical events are replayed through `Keysharp Virtual Input`,
but those replay events are suppressed from the callback path. Synthetic input
requested directly through `SYNTHESIZE_INPUT` is not suppressed; if matching
hooks are installed, it re-enters the callback path as injected input so
Keysharp can apply SendLevel-style policy.

## Synthesis Model

Input synthesis is modeled after Windows `SendInput`:

```text
ksi_input
  type = INPUT_KEYBOARD | INPUT_MOUSE | INPUT_HARDWARE
  keyboard = KEYBDINPUT-like payload
  mouse = MOUSEINPUT-like payload
```

Keyboard synthesis carries:

```text
vk            Windows virtual-key code
scan          Windows-like scan code
flags         KEYEVENTF_KEYUP, SCANCODE, UNICODE, EXTENDEDKEY
time          requested event time, usually zero
extra_info    marker used for injected/self-generated filtering
```

Mouse synthesis carries:

```text
dx, dy        relative or absolute movement
mouse_data    wheel delta or X button value
flags         MOUSEEVENTF_* flags
time          requested event time, usually zero
extra_info    marker used for injected/self-generated filtering
```

The daemon translates these requests into `uinput` events. Linux `uinput` does
not preserve Windows `dwExtraInfo`; the field is still present in the protocol
so Keysharp can keep the same request shape across platforms.

## Hook Decisions

Hook return values are explicit over IPC:

```text
PASS       allow the event to continue
BLOCK      suppress the event
MODIFY     replace with daemon/client-provided synthetic input
```

Do not rely on implicit Windows callback return conventions across IPC.

`MODIFY` decisions append replacement `ksi_input` entries after the decision
header. The replacement inputs require the same synthesis capabilities as direct
`SYNTHESIZE_INPUT` requests.

## Hook Callback Timing

Hook callbacks have a strict one-second timeout. A slow or crashed script must
not make the user's keyboard or mouse unusable.

Timeouts currently pass the event to the next hook client. The global emergency
pass-through command is available for explicit recovery.

`EMERGENCY_PASSTHROUGH` is a global safety valve. An authenticated client can
send it to clear hook subscriptions, discard pending hook events, and release
all active evdev grabs. This favors returning device control to the desktop over
preserving queued hook events.

## Multiple Scripts

Multiple scripts are modeled as ordered hook clients. Each hook subscription
is represented by:

- client id
- hook type
- subscription order

The daemon owns delivery order, timeout enforcement, device ownership, replay,
suppression, and synthesis. Keysharp owns the policy decision. Individual
Keysharp processes do not open/grab physical input devices themselves.

## Native Codes

Windows-compatible fields are the main protocol surface. Native backend codes
are still carried for diagnostics and fallback behavior:

- Linux: evdev `KEY_*`, `BTN_*`, `REL_*`, `ABS_*`

Keysharp uses native codes only when Windows-compatible translation is
insufficient.
