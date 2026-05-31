#ifndef KEYSHARP_INPUTD_PROTOCOL_H
#define KEYSHARP_INPUTD_PROTOCOL_H

#include <stdint.h>

#define KSI_PROTOCOL_MAJOR 0u
#define KSI_PROTOCOL_MINOR 1u
#define KSI_PROTOCOL_NAME "keysharp-inputd/windows-input-v0"
#define KSI_MAX_MESSAGE_SIZE 65536u
#define KSI_SYNTH_DEVICE_NAME "Keysharp Virtual Input"
#define KSI_SYNTH_DEVICE_BUSTYPE 0x06u
#define KSI_SYNTH_DEVICE_VENDOR 0x4b53u
#define KSI_SYNTH_DEVICE_PRODUCT 0x0001u
#define KSI_SYNTH_DEVICE_VERSION 1u

/* Second synthetic device: pure-absolute pointer for absolute mouse moves. */
#define KSI_SYNTH_ABS_DEVICE_NAME "Keysharp Virtual Pointer"
#define KSI_SYNTH_ABS_DEVICE_PRODUCT 0x0002u
#define KSI_XBUTTON1 0x0001u
#define KSI_XBUTTON2 0x0002u

typedef enum ksi_message_type {
    KSI_MESSAGE_CLIENT_HELLO = 1,
    KSI_MESSAGE_CLIENT_GOODBYE = 2,
    KSI_MESSAGE_HEARTBEAT = 3,
    KSI_MESSAGE_SUBSCRIBE_HOOK = 10,
    KSI_MESSAGE_UNSUBSCRIBE_HOOK = 11,
    KSI_MESSAGE_HOOK_EVENT = 12,
    KSI_MESSAGE_HOOK_DECISION = 13,
    KSI_MESSAGE_SYNTHESIZE_INPUT = 20,
    KSI_MESSAGE_SYNTHESIS_RESULT = 21,
    KSI_MESSAGE_EMERGENCY_PASSTHROUGH = 30,
    KSI_MESSAGE_SET_BLOCK_INPUT = 31,
    KSI_MESSAGE_GET_INDICATOR_STATE    = 40,
    KSI_MESSAGE_INDICATOR_STATE_RESULT = 41,
    KSI_MESSAGE_GET_POINTER_POSITION   = 42,
    KSI_MESSAGE_POINTER_POSITION_RESULT = 43,
    /* Physical key state snapshot: KSI_CAP_HOOK_KEYBOARD required.
     * GET_KEY_STATE has no payload. KEY_STATE_RESULT carries a
     * ksi_key_state_payload with the aggregate modifier + indicator state
     * across all currently grabbed keyboard devices. */
    KSI_MESSAGE_GET_KEY_STATE          = 44,
    KSI_MESSAGE_KEY_STATE_RESULT       = 45,
    /* Trust-store administration scoped to input capabilities.
     * LIST streams one ENTRY per stored record that has any input capability
     * bits, followed by a RESULT status terminator. RESET clears allow+deny
     * bits for a single record; the daemon clamps the mask to input caps only.
     * Both are scoped to the caller's uid unless the daemon runs as root and
     * the caller is also root. */
    KSI_MESSAGE_LIST_PERMISSIONS        = 50,
    KSI_MESSAGE_LIST_PERMISSIONS_ENTRY  = 51,
    KSI_MESSAGE_LIST_PERMISSIONS_RESULT = 52,
    KSI_MESSAGE_RESET_PERMISSIONS       = 53,
} ksi_message_type;

/* Bitmask of all capabilities whose trust records are administered via
 * keysharp-inputd. Screen-capture (0x20) is excluded — screencap manages that
 * helper-specific domain itself. */
#define KSI_INPUT_CAPABILITIES 0x0000005Fu

/* Payload for KSI_MESSAGE_INDICATOR_STATE_RESULT. */
typedef struct ksi_indicator_state_payload {
    uint8_t caps_lock;
    uint8_t num_lock;
    uint8_t scroll_lock;
    uint8_t reserved;
} ksi_indicator_state_payload;

/* Payload for KSI_MESSAGE_KEY_STATE_RESULT.
 *
 * modifiers_lr: bitmask of currently physically-held modifier keys,
 *   using the same bit assignments as Keysharp's internal modLR flags:
 *     bit 0 = MOD_LCONTROL, bit 1 = MOD_RCONTROL,
 *     bit 2 = MOD_LALT,     bit 3 = MOD_RALT,
 *     bit 4 = MOD_LSHIFT,   bit 5 = MOD_RSHIFT,
 *     bit 6 = MOD_LWIN,     bit 7 = MOD_RWIN.
 * caps_lock, num_lock, scroll_lock: current LED/toggle state (same as
 *   ksi_indicator_state_payload). */
typedef struct ksi_key_state_payload {
    uint32_t modifiers_lr;
    uint8_t  caps_lock;
    uint8_t  num_lock;
    uint8_t  scroll_lock;
    uint8_t  reserved;
} ksi_key_state_payload;

/* Raw absolute axis values from the last evdev ABS_X/ABS_Y pointer report. */
typedef struct ksi_pointer_position_payload {
    uint8_t valid;
    uint8_t reserved[3];
    int32_t x;
    int32_t y;
    int32_t x_min;
    int32_t x_max;
    int32_t y_min;
    int32_t y_max;
} ksi_pointer_position_payload;

typedef enum ksi_client_capability {
    KSI_CAP_HOOK_KEYBOARD = 0x00000001u,
    KSI_CAP_HOOK_MOUSE = 0x00000002u,
    KSI_CAP_SYNTH_KEYBOARD = 0x00000004u,
    KSI_CAP_SYNTH_MOUSE = 0x00000008u,
    KSI_CAP_BLOCK_INPUT = 0x00000010u,
} ksi_client_capability;

typedef enum ksi_block_input_mask {
    KSI_BLOCK_INPUT_KEYBOARD = 0x00000001u,
    KSI_BLOCK_INPUT_MOUSE = 0x00000002u,
} ksi_block_input_mask;

typedef enum ksi_hook_type {
    KSI_HOOK_KEYBOARD_LL = 13,
    KSI_HOOK_MOUSE_LL = 14,
} ksi_hook_type;

typedef enum ksi_hook_decision {
    KSI_HOOK_DECISION_PASS = 0,
    KSI_HOOK_DECISION_BLOCK = 1,
    KSI_HOOK_DECISION_MODIFY = 2,
} ksi_hook_decision;

typedef enum ksi_input_type {
    KSI_INPUT_MOUSE = 0,
    KSI_INPUT_KEYBOARD = 1,
    KSI_INPUT_HARDWARE = 2,
} ksi_input_type;

typedef enum ksi_windows_message {
    KSI_WM_MOUSEMOVE = 0x0200,
    KSI_WM_LBUTTONDOWN = 0x0201,
    KSI_WM_LBUTTONUP = 0x0202,
    KSI_WM_RBUTTONDOWN = 0x0204,
    KSI_WM_RBUTTONUP = 0x0205,
    KSI_WM_MBUTTONDOWN = 0x0207,
    KSI_WM_MBUTTONUP = 0x0208,
    KSI_WM_MOUSEWHEEL = 0x020A,
    KSI_WM_XBUTTONDOWN = 0x020B,
    KSI_WM_XBUTTONUP = 0x020C,
    KSI_WM_MOUSEHWHEEL = 0x020E,
    KSI_WM_KEYDOWN = 0x0100,
    KSI_WM_KEYUP = 0x0101,
    KSI_WM_SYSKEYDOWN = 0x0104,
    KSI_WM_SYSKEYUP = 0x0105,
} ksi_windows_message;

typedef enum ksi_keyboard_hook_flags {
    KSI_LLKHF_EXTENDED = 0x00000001u,
    KSI_LLKHF_LOWER_IL_INJECTED = 0x00000002u,
    /* Bits 0x04 and 0x08 carry the current LED indicator state at the time
     * the key event was generated.  This lets the C# side update its indicator
     * snapshot without a separate round-trip query to the daemon. */
    KSI_LLKHF_CAPS_LOCK_ON = 0x00000004u,
    KSI_LLKHF_NUM_LOCK_ON  = 0x00000008u,
    KSI_LLKHF_INJECTED = 0x00000010u,
    KSI_LLKHF_ALTDOWN = 0x00000020u,
    KSI_LLKHF_SCROLL_LOCK_ON = 0x00000040u,
    KSI_LLKHF_UP = 0x00000080u,
} ksi_keyboard_hook_flags;

typedef enum ksi_mouse_hook_flags {
    KSI_LLMHF_INJECTED = 0x00000001u,
    KSI_LLMHF_LOWER_IL_INJECTED = 0x00000002u,
} ksi_mouse_hook_flags;

typedef enum ksi_keybdinput_flags {
    KSI_KEYEVENTF_EXTENDEDKEY = 0x0001u,
    KSI_KEYEVENTF_KEYUP = 0x0002u,
    KSI_KEYEVENTF_UNICODE = 0x0004u,
    KSI_KEYEVENTF_SCANCODE = 0x0008u,
} ksi_keybdinput_flags;

typedef enum ksi_mouseinput_flags {
    KSI_MOUSEEVENTF_MOVE = 0x0001u,
    KSI_MOUSEEVENTF_LEFTDOWN = 0x0002u,
    KSI_MOUSEEVENTF_LEFTUP = 0x0004u,
    KSI_MOUSEEVENTF_RIGHTDOWN = 0x0008u,
    KSI_MOUSEEVENTF_RIGHTUP = 0x0010u,
    KSI_MOUSEEVENTF_MIDDLEDOWN = 0x0020u,
    KSI_MOUSEEVENTF_MIDDLEUP = 0x0040u,
    KSI_MOUSEEVENTF_XDOWN = 0x0080u,
    KSI_MOUSEEVENTF_XUP = 0x0100u,
    KSI_MOUSEEVENTF_WHEEL = 0x0800u,
    KSI_MOUSEEVENTF_HWHEEL = 0x1000u,
    KSI_MOUSEEVENTF_MOVE_NOCOALESCE = 0x2000u,
    KSI_MOUSEEVENTF_VIRTUALDESK = 0x4000u,
    KSI_MOUSEEVENTF_ABSOLUTE = 0x8000u,
} ksi_mouseinput_flags;

typedef struct ksi_message_header {
    uint32_t size;
    uint16_t major;
    uint16_t minor;
    uint32_t type;
    uint32_t client_id;
    uint64_t correlation_id;
} ksi_message_header;

typedef struct ksi_keyboard_hook_event {
    uint32_t message;
    uint32_t vk_code;
    uint32_t scan_code;
    uint32_t flags;
    uint64_t time_ms;
    uint64_t extra_info;
    uint32_t device_id;
    uint32_t reserved;
} ksi_keyboard_hook_event;

typedef struct ksi_mouse_hook_event {
    uint32_t message;
    int32_t x;
    int32_t y;
    uint32_t mouse_data;
    uint32_t flags;
    uint64_t time_ms;
    uint64_t extra_info;
    uint32_t device_id;
    uint32_t reserved;
} ksi_mouse_hook_event;

typedef struct ksi_keybdinput {
    uint16_t vk;
    uint16_t scan;
    uint32_t flags;
    uint32_t time;
    uint64_t extra_info;
} ksi_keybdinput;

typedef struct ksi_mouseinput {
    int32_t dx;
    int32_t dy;
    uint32_t mouse_data;
    uint32_t flags;
    uint32_t time;
    uint64_t extra_info;
} ksi_mouseinput;

typedef struct ksi_input {
    uint32_t type;
    uint32_t reserved;
    union {
        ksi_mouseinput mouse;
        ksi_keybdinput keyboard;
    } data;
} ksi_input;

typedef struct ksi_status_payload {
    int32_t status;
    uint32_t detail;
} ksi_status_payload;

typedef struct ksi_client_hello_payload {
    uint32_t requested_capabilities;
    uint32_t flags;
} ksi_client_hello_payload;

#define KSI_CLIENT_HELLO_FLAG_FORCE_PROMPT 0x00000001u

typedef struct ksi_client_hello_result_payload {
    int32_t status;
    uint32_t granted_capabilities;
} ksi_client_hello_result_payload;

/* Flags for ksi_synthesize_input_payload.flags */
#define KSI_SYNTH_FLAG_BYPASS_HOOK 0x00000001u  /* suppress events from the hook chain */

typedef struct ksi_synthesize_input_payload {
    uint32_t count;
    uint32_t flags;
    ksi_input inputs[];
} ksi_synthesize_input_payload;

typedef struct ksi_hook_subscription_payload {
    uint32_t hook_type;
    uint32_t flags;
} ksi_hook_subscription_payload;

typedef struct ksi_block_input_payload {
    uint32_t block_mask;
    uint32_t reserved;
} ksi_block_input_payload;

typedef struct ksi_hook_event_payload {
    uint64_t event_id;
    uint32_t hook_type;
    uint32_t reserved;
    union {
        ksi_keyboard_hook_event keyboard;
        ksi_mouse_hook_event mouse;
    } event;
} ksi_hook_event_payload;

typedef struct ksi_hook_decision_payload {
    uint64_t event_id;
    uint32_t decision;
    uint32_t input_count;
    ksi_input inputs[];
} ksi_hook_decision_payload;

/* Length of the hex-encoded SHA-256 process identity, including the trailing
 * NUL byte. Mirrors KSI_PERMISSION_HASH_HEX_LENGTH from permissions.h. */
#define KSI_PROTOCOL_HASH_HEX_BUFFER 65u

/* One entry in a streamed LIST_PERMISSIONS response. The path is appended
 * inline after the fixed header and is NOT NUL-terminated; its length is
 * carried by path_length. Only records with input capability bits are sent. */
typedef struct ksi_list_permissions_entry_payload {
    uint32_t uid;
    uint32_t persistent_allowed_capabilities;
    uint32_t persistent_denied_capabilities;
    uint16_t path_length;
    uint16_t reserved;
    uint64_t last_seen_utc;
    char exe_hash[KSI_PROTOCOL_HASH_HEX_BUFFER];
    /* uint8_t exe_path[path_length] follows here. */
} ksi_list_permissions_entry_payload;

/* Request payload for KSI_MESSAGE_RESET_PERMISSIONS.
 * target_uid: uid to clear; KSI_RESET_PERMISSIONS_UID_SELF = caller's uid.
 * capabilities: bits to clear, clamped by the daemon to KSI_INPUT_CAPABILITIES. */
#define KSI_RESET_PERMISSIONS_UID_SELF 0xFFFFFFFFu

typedef struct ksi_reset_permissions_payload {
    uint32_t target_uid;
    uint32_t capabilities;
    char exe_hash[KSI_PROTOCOL_HASH_HEX_BUFFER];
    uint8_t reserved[3];
} ksi_reset_permissions_payload;

#endif
