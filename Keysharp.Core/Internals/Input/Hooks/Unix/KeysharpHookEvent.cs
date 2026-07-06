#if !WINDOWS
using System;

namespace Keysharp.Internals.Input.Hooks
{
	[Flags]
	internal enum EventMask : uint
	{
		None = 0,
		LeftShift = 1u << 0,
		LeftCtrl = 1u << 1,
		LeftMeta = 1u << 2,
		LeftAlt = 1u << 3,
		RightShift = 1u << 4,
		RightCtrl = 1u << 5,
		RightMeta = 1u << 6,
		RightAlt = 1u << 7,
		Shift = LeftShift | RightShift,
		Ctrl = LeftCtrl | RightCtrl,
		Meta = LeftMeta | RightMeta,
		Alt = LeftAlt | RightAlt,
		Button1 = 1u << 8,
		Button2 = 1u << 9,
		Button3 = 1u << 10,
		Button4 = 1u << 11,
		Button5 = 1u << 12,
		NumLock = 1u << 13,
		CapsLock = 1u << 14,
		ScrollLock = 1u << 15,
		SimulatedEvent = 1u << 30,
		SuppressEvent = 1u << 31
	}

	internal enum EventType : uint
	{
		KeyPressed,
		KeyReleased,
		KeyTyped,
		MousePressed,
		MouseReleased,
		MouseMoved,
		MouseDragged,
		MouseWheel
	}

	internal enum MouseButton : ushort
	{
		NoButton = 0,
		Button1 = 1,
		Button2 = 2,
		Button3 = 3,
		Button4 = 4,
		Button5 = 5
	}

	internal enum MouseWheelScrollDirection
	{
		Vertical,
		Horizontal
	}

	internal enum MouseWheelScrollType
	{
		UnitScroll
	}

	internal struct KeyboardEventData
	{
		internal const ushort RawUndefinedChar = 0xFFFF;
		internal uint VkCode;
		internal uint KeyCode;
		internal ushort RawCode;
		internal ushort RawKeyChar;
	}

	internal struct MouseEventData
	{
		internal MouseButton Button;
		internal ushort Clicks;
		internal short X;
		internal short Y;
	}

	internal struct MouseWheelEventData
	{
		internal MouseWheelScrollType Type;
		internal short Rotation;
		internal ushort Delta;
		internal MouseWheelScrollDirection Direction;
		internal short X;
		internal short Y;
	}

	internal struct UioHookEvent
	{
		internal EventType Type;
		internal ulong Time;
		internal EventMask Mask;
		internal KeyboardEventData Keyboard;
		internal MouseEventData Mouse;
		internal MouseWheelEventData Wheel;
	}

	internal class HookEventArgs : EventArgs
	{
		internal HookEventArgs(UioHookEvent rawEvent) => RawEvent = rawEvent;
		internal UioHookEvent RawEvent { get; set; }
		internal DateTimeOffset EventTime => DateTimeOffset.FromUnixTimeMilliseconds(unchecked((long)RawEvent.Time));
		internal bool IsEventSimulated => (RawEvent.Mask & EventMask.SimulatedEvent) != 0;
		internal bool SuppressEvent { get; set; }
	}

	internal sealed class KeyboardHookEventArgs : HookEventArgs
	{
		internal KeyboardHookEventArgs(UioHookEvent rawEvent) : base(rawEvent) { }
		internal KeyboardEventData Data => RawEvent.Keyboard;
	}

	internal sealed class MouseHookEventArgs : HookEventArgs
	{
		internal MouseHookEventArgs(UioHookEvent rawEvent) : base(rawEvent) { }
		internal MouseEventData Data => RawEvent.Mouse;
	}

	internal sealed class MouseWheelHookEventArgs : HookEventArgs
	{
		internal MouseWheelHookEventArgs(UioHookEvent rawEvent) : base(rawEvent) { }
		internal MouseWheelEventData Data => RawEvent.Wheel;
	}
}
#endif
