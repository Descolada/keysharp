#if OSX
using System.Runtime.InteropServices;
using System.Text;
using Keysharp.Builtins;
using Keysharp.Internals.Input.Keyboard;
using Keysharp.Internals.Input.Hooks;
using Keysharp.Internals.Input.Hooks.MacOS;
using static Keysharp.Internals.Input.Keyboard.KeyboardMouseSender;
using static Keysharp.Internals.Input.Keyboard.KeyboardUtils;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;

namespace Keysharp.Internals.Input.MacOS
{
	internal static partial class MacNativeInput
	{
		private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
		private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

		internal const uint kCGEventSourceStateCombinedSessionState = 0;
		internal const uint kCGEventSourceStateHIDSystemState = 1;
		internal const uint kCGHIDEventTap = 0;
		internal const uint kCGSessionEventTap = 1;
		internal const uint kCGHeadInsertEventTap = 0;
		internal const uint kCGEventTapOptionDefault = 0;
		internal const uint kCGEventSourceUserData = 42;
		internal const uint kCGKeyboardEventKeycode = 9;
		internal const uint kCGMouseEventButtonNumber = 3;
		internal const uint kCGScrollWheelEventDeltaAxis1 = 11;
		internal const uint kCGScrollWheelEventDeltaAxis2 = 12;

		internal const uint kCGEventLeftMouseDown = 1;
		internal const uint kCGEventLeftMouseUp = 2;
		internal const uint kCGEventRightMouseDown = 3;
		internal const uint kCGEventRightMouseUp = 4;
		internal const uint kCGEventMouseMoved = 5;
		internal const uint kCGEventLeftMouseDragged = 6;
		internal const uint kCGEventRightMouseDragged = 7;
		internal const uint kCGEventKeyDown = 10;
		internal const uint kCGEventKeyUp = 11;
		internal const uint kCGEventFlagsChanged = 12;
		internal const uint kCGEventScrollWheel = 22;
		internal const uint kCGEventOtherMouseDown = 25;
		internal const uint kCGEventOtherMouseUp = 26;
		internal const uint kCGEventOtherMouseDragged = 27;
		internal const uint kCGEventTapDisabledByTimeout = 0xFFFFFFFE;
		internal const uint kCGEventTapDisabledByUserInput = 0xFFFFFFFF;

		private const ulong kCGEventFlagMaskAlphaShift = 1UL << 16;
		private const ulong kCGEventFlagMaskShift = 1UL << 17;
		private const ulong kCGEventFlagMaskControl = 1UL << 18;
		private const ulong kCGEventFlagMaskAlternate = 1UL << 19;
		private const ulong kCGEventFlagMaskCommand = 1UL << 20;

		private static readonly nint runLoopDefaultMode = CreateRunLoopMode("kCFRunLoopDefaultMode");

		[StructLayout(LayoutKind.Sequential)]
		internal struct CGPoint
		{
			public double X;
			public double Y;
			public CGPoint(double x, double y) { X = x; Y = y; }
		}

		internal delegate nint CGEventTapCallBack(nint proxy, uint type, nint cgEvent, nint userInfo);

		[LibraryImport(ApplicationServices)]
		internal static partial nint CGEventCreateKeyboardEvent(nint source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

		[LibraryImport(ApplicationServices)]
		internal static partial nint CGEventCreateMouseEvent(nint source, uint mouseType, CGPoint mouseCursorPosition, uint mouseButton);

		[DllImport(ApplicationServices)]
		internal static extern nint CGEventCreateScrollWheelEvent(nint source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);

		[LibraryImport(ApplicationServices)]
		internal static partial nint CGEventSourceCreate(uint stateId);

		[LibraryImport(ApplicationServices)]
		internal static partial void CGEventPost(uint tap, nint cgEvent);

		[LibraryImport(ApplicationServices)]
		internal static partial void CGEventSetIntegerValueField(nint cgEvent, uint field, long value);

		[LibraryImport(ApplicationServices)]
		internal static partial long CGEventGetIntegerValueField(nint cgEvent, uint field);

		[LibraryImport(ApplicationServices)]
		internal static partial ulong CGEventGetFlags(nint cgEvent);

		[LibraryImport(ApplicationServices)]
		internal static partial CGPoint CGEventGetLocation(nint cgEvent);

		[LibraryImport(ApplicationServices)]
		internal static partial void CGEventSetFlags(nint cgEvent, ulong flags);

		[LibraryImport(ApplicationServices)]
		internal static partial ulong CGEventSourceFlagsState(uint sourceState);

		[LibraryImport(ApplicationServices)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static partial bool CGEventSourceKeyState(uint sourceState, ushort virtualKey);

		[LibraryImport(ApplicationServices, StringMarshalling = StringMarshalling.Utf16)]
		internal static partial void CGEventKeyboardSetUnicodeString(nint cgEvent, long stringLength, string unicodeString);

		[DllImport(ApplicationServices)]
		internal static extern void CGEventKeyboardGetUnicodeString(nint cgEvent, long maxStringLength, out long actualStringLength,
			[Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] unicodeString);

		[DllImport(ApplicationServices)]
		internal static extern nint CGEventTapCreate(uint tap, uint place, uint options, ulong eventsOfInterest, CGEventTapCallBack callback, nint userInfo);

		[LibraryImport(ApplicationServices)]
		internal static partial void CGEventTapEnable(nint tap, [MarshalAs(UnmanagedType.I1)] bool enable);

		[LibraryImport(CoreFoundation)]
		internal static partial nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);

		[LibraryImport(CoreFoundation)]
		internal static partial nint CFRunLoopGetCurrent();

		[LibraryImport(CoreFoundation)]
		internal static partial void CFRunLoopAddSource(nint rl, nint source, nint mode);

		[LibraryImport(CoreFoundation)]
		internal static partial void CFRunLoopRemoveSource(nint rl, nint source, nint mode);

		[LibraryImport(CoreFoundation)]
		internal static partial void CFRunLoopRun();

		[LibraryImport(CoreFoundation)]
		internal static partial void CFRunLoopStop(nint rl);

		[LibraryImport(CoreFoundation)]
		internal static partial void CFRelease(nint cf);

		[LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
		private static partial nint CFStringCreateWithCString(nint allocator, string cStr, uint encoding);

		internal static nint RunLoopDefaultMode => runLoopDefaultMode;

		internal static ulong KeyboardEventMask =>
			(1UL << (int)kCGEventKeyDown) |
			(1UL << (int)kCGEventKeyUp) |
			(1UL << (int)kCGEventFlagsChanged);

		internal static ulong MouseEventMask =>
			(1UL << (int)kCGEventLeftMouseDown) |
			(1UL << (int)kCGEventLeftMouseUp) |
			(1UL << (int)kCGEventRightMouseDown) |
			(1UL << (int)kCGEventRightMouseUp) |
			(1UL << (int)kCGEventMouseMoved) |
			(1UL << (int)kCGEventLeftMouseDragged) |
			(1UL << (int)kCGEventRightMouseDragged) |
			(1UL << (int)kCGEventScrollWheel) |
			(1UL << (int)kCGEventOtherMouseDown) |
			(1UL << (int)kCGEventOtherMouseUp) |
			(1UL << (int)kCGEventOtherMouseDragged);

		internal static ulong HookEventMask => KeyboardEventMask | MouseEventMask;

		private static nint CreateRunLoopMode(string value)
			=> CFStringCreateWithCString(nint.Zero, value, 0x08000100); // kCFStringEncodingUTF8

		internal static bool IsModifierVk(uint vk) =>
			vk is VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN or VK_CAPITAL;

		internal static bool IsKeyUpFromFlagsChanged(uint vk, ulong flags) => vk switch
		{
			VK_LSHIFT or VK_RSHIFT => (flags & kCGEventFlagMaskShift) == 0,
			VK_LCONTROL or VK_RCONTROL => (flags & kCGEventFlagMaskControl) == 0,
			VK_LMENU or VK_RMENU => (flags & kCGEventFlagMaskAlternate) == 0,
			VK_LWIN or VK_RWIN => (flags & kCGEventFlagMaskCommand) == 0,
			VK_CAPITAL => (flags & kCGEventFlagMaskAlphaShift) == 0,
			_ => false
		};

		internal static EventMask ToEventMask(ulong flags)
		{
			var mask = EventMask.None;

			if ((flags & kCGEventFlagMaskShift) != 0)
				mask |= EventMask.LeftShift | EventMask.RightShift;
			if ((flags & kCGEventFlagMaskControl) != 0)
				mask |= EventMask.LeftCtrl | EventMask.RightCtrl;
			if ((flags & kCGEventFlagMaskAlternate) != 0)
				mask |= EventMask.LeftAlt | EventMask.RightAlt;
			if ((flags & kCGEventFlagMaskCommand) != 0)
				mask |= EventMask.LeftMeta | EventMask.RightMeta;
			if ((flags & kCGEventFlagMaskAlphaShift) != 0)
				mask |= EventMask.CapsLock;

			return mask;
		}

		internal static void PostKeyboard(uint vk, bool keyDown, long extraInfo)
		{
			if (!KeyCodes.TryMapVkToMacCode(vk, out var keyCode))
				return;

			var source = CGEventSourceCreate(kCGEventSourceStateHIDSystemState);
			var ev = CGEventCreateKeyboardEvent(source, (ushort)keyCode, keyDown);

			if (ev != nint.Zero)
			{
				CGEventSetIntegerValueField(ev, kCGEventSourceUserData, extraInfo);
				CGEventPost(kCGHIDEventTap, ev);
				CFRelease(ev);
			}

			if (source != nint.Zero)
				CFRelease(source);
		}

		internal static void PostUnicodeText(string text, long extraInfo)
		{
			if (string.IsNullOrEmpty(text))
				return;

			foreach (var rune in text.EnumerateRunes())
				PostUnicodeScalar(rune.ToString(), extraInfo);
		}

		internal static bool TryGetKeyboardUnicodeString(nint cgEvent, Span<char> buffer, out int length)
		{
			length = 0;
			if (cgEvent == nint.Zero || buffer.Length == 0)
				return false;

			var chars = new char[buffer.Length];
			CGEventKeyboardGetUnicodeString(cgEvent, chars.Length, out var actualLength, chars);
			length = (int)Math.Clamp(actualLength, 0, chars.Length);

			if (length == 0)
				return false;

			chars.AsSpan(0, length).CopyTo(buffer);
			return true;
		}

		private static void PostUnicodeScalar(string scalar, long extraInfo)
		{
			var source = CGEventSourceCreate(kCGEventSourceStateHIDSystemState);

			void Post(bool down)
			{
				var ev = CGEventCreateKeyboardEvent(source, 0, down);
				if (ev == nint.Zero)
					return;

				CGEventKeyboardSetUnicodeString(ev, scalar.Length, scalar);
				CGEventSetIntegerValueField(ev, kCGEventSourceUserData, extraInfo);
				CGEventPost(kCGHIDEventTap, ev);
				CFRelease(ev);
			}

			Post(true);
			Post(false);

			if (source != nint.Zero)
				CFRelease(source);
		}

		internal static void PostMouseMove(int x, int y, long extraInfo)
			=> PostMouseEvent(kCGEventMouseMoved, new CGPoint(x, y), 0, extraInfo);

		internal static void PostMouseMoveRelative(int dx, int dy, long extraInfo)
		{
			if (!Platform.Mouse.TryGetCursorPos(out var x, out var y))
				return;

			PostMouseMove(x + dx, y + dy, extraInfo);
		}

		internal static void PostMouseButton(MouseButton button, bool down, int x, int y, long extraInfo)
		{
			var cgButton = ToCGMouseButton(button);
			if (cgButton == uint.MaxValue)
				return;

			var type = (button, down) switch
			{
				(MouseButton.Button1, true) => kCGEventLeftMouseDown,
				(MouseButton.Button1, false) => kCGEventLeftMouseUp,
				(MouseButton.Button2, true) => kCGEventRightMouseDown,
				(MouseButton.Button2, false) => kCGEventRightMouseUp,
				(_, true) => kCGEventOtherMouseDown,
				_ => kCGEventOtherMouseUp
			};

			PostMouseEvent(type, new CGPoint(x, y), cgButton, extraInfo);
		}

		internal static void PostMouseWheel(short delta, MouseWheelScrollDirection direction, long extraInfo)
		{
			var clicks = delta / (int)MouseUtils.WHEEL_DELTA;
			if (clicks == 0)
				clicks = Math.Sign(delta);

			var source = CGEventSourceCreate(kCGEventSourceStateHIDSystemState);
			var ev = direction == MouseWheelScrollDirection.Vertical
				? CGEventCreateScrollWheelEvent(source, 1, 1, clicks, 0, 0)
				: CGEventCreateScrollWheelEvent(source, 1, 2, 0, clicks, 0);

			if (ev != nint.Zero)
			{
				CGEventSetIntegerValueField(ev, kCGEventSourceUserData, extraInfo);
				CGEventPost(kCGHIDEventTap, ev);
				CFRelease(ev);
			}

			if (source != nint.Zero)
				CFRelease(source);
		}

		private static void PostMouseEvent(uint type, CGPoint point, uint button, long extraInfo)
		{
			var source = CGEventSourceCreate(kCGEventSourceStateHIDSystemState);
			var ev = CGEventCreateMouseEvent(source, type, point, button);

			if (ev != nint.Zero)
			{
				CGEventSetIntegerValueField(ev, kCGEventSourceUserData, extraInfo);
				CGEventPost(kCGHIDEventTap, ev);
				CFRelease(ev);
			}

			if (source != nint.Zero)
				CFRelease(source);
		}

		internal static MouseButton ToMouseButton(uint type, uint buttonNumber) => type switch
		{
			kCGEventLeftMouseDown or kCGEventLeftMouseUp => MouseButton.Button1,
			kCGEventRightMouseDown or kCGEventRightMouseUp => MouseButton.Button2,
			kCGEventOtherMouseDown or kCGEventOtherMouseUp => buttonNumber switch
			{
				2 => MouseButton.Button3,
				3 => MouseButton.Button4,
				4 => MouseButton.Button5,
				_ => MouseButton.NoButton
			},
			_ => MouseButton.NoButton
		};

		private static uint ToCGMouseButton(MouseButton button) => button switch
		{
			MouseButton.Button1 => 0,
			MouseButton.Button2 => 1,
			MouseButton.Button3 => 2,
			MouseButton.Button4 => 3,
			MouseButton.Button5 => 4,
			_ => uint.MaxValue
		};
	}

	internal sealed class MacKeyboardMouseSender : Keysharp.Internals.Input.Unix.UnixKeyboardMouseSender
	{
		protected override void DispatchKeybdEvent(Keysharp.Internals.Input.Hooks.Unix.UnixHookThread lht, KeyEventTypes eventType, uint vk, long extraInfo)
		{
			WithSendScope(lht, () =>
			{
				EnsureInputSendPermission("send keyboard input");

				if (vk == VK_CAPITAL && eventType != KeyEventTypes.KeyUp && MacCapsLockState.TryToggle())
					return;

				switch (eventType)
				{
					case KeyEventTypes.KeyDown:
						MacNativeInput.PostKeyboard(vk, true, extraInfo);
						break;
					case KeyEventTypes.KeyUp:
						if (vk != VK_CAPITAL || !MacCapsLockState.IsAvailable)
							MacNativeInput.PostKeyboard(vk, false, extraInfo);
						break;
					case KeyEventTypes.KeyDownAndUp:
						MacNativeInput.PostKeyboard(vk, true, extraInfo);
						MacNativeInput.PostKeyboard(vk, false, extraInfo);
						break;
				}
			});
		}

		protected override bool TrySendPlatformUnicodeText(Keysharp.Internals.Input.Hooks.Unix.UnixHookThread lht, string text, long extraInfo)
		{
			WithSendScope(lht, () =>
			{
				EnsureInputSendPermission("send text input");
				MacNativeInput.PostUnicodeText(text, extraInfo);
			});

			return true;
		}

		protected override bool TrySendPlatformUnicodeChar(Keysharp.Internals.Input.Hooks.Unix.UnixHookThread lht, char ch, long extraInfo, bool hasMappedKeystroke, uint vk, bool needShift, bool needAltGr)
			=> TrySendPlatformUnicodeText(lht, ch.ToString(), extraInfo);

		protected override void DispatchEventArray(Keysharp.Internals.Input.Hooks.Unix.UnixHookThread lht, InputArrayState state, long extraInfo, double scale)
			=> WithSendScope(lht, () => ReplayMacEventArray(state.Events, extraInfo, scale));

		private void ReplayMacEventArray(List<ArrayEvent> events, long extraInfo, double scale)
		{
			var textBatch = new StringBuilder();

			void FlushText()
			{
				if (textBatch.Length == 0)
					return;

				EmitMacTextWithControls(textBatch.ToString(), extraInfo);
				textBatch.Clear();
			}

			foreach (var ev in events)
			{
				if (ev.Type == ArrayEventType.Text)
				{
					textBatch.Append(ev.Text);
					continue;
				}

				FlushText();

				switch (ev.Type)
				{
					case ArrayEventType.DelayMs:
						if (ev.DelayMs > 0)
							Keysharp.Internals.Flow.SleepWithoutInterruption(ev.DelayMs);
						break;
					case ArrayEventType.KeyDown:
						MacNativeInput.PostKeyboard(ev.Vk, true, extraInfo);
						break;
					case ArrayEventType.KeyUp:
						MacNativeInput.PostKeyboard(ev.Vk, false, extraInfo);
						break;
					case ArrayEventType.MouseMoveRel:
						MacNativeInput.PostMouseMoveRelative(ClampShort(ev.X * scale), ClampShort(ev.Y * scale), extraInfo);
						break;
					case ArrayEventType.MouseMoveAbs:
					{
						int mx = ev.X, my = ev.Y;
						EnsureCoords(ref mx, ref my);
						MacNativeInput.PostMouseMove(ClampShort(mx * scale), ClampShort(my * scale), extraInfo);
						break;
					}
					case ArrayEventType.MousePress:
					case ArrayEventType.MouseRelease:
					{
						int mx = ev.X, my = ev.Y;
						EnsureCoords(ref mx, ref my);
						MacNativeInput.PostMouseButton(ev.Button, ev.Type == ArrayEventType.MousePress, ClampShort(mx * scale), ClampShort(my * scale), extraInfo);
						break;
					}
					case ArrayEventType.MouseWheelV:
						MacNativeInput.PostMouseWheel(ev.WheelDelta, MouseWheelScrollDirection.Vertical, extraInfo);
						break;
					case ArrayEventType.MouseWheelH:
						MacNativeInput.PostMouseWheel(ev.WheelDelta, MouseWheelScrollDirection.Horizontal, extraInfo);
						break;
				}
			}

			FlushText();
		}

		internal override void MouseEvent(uint eventFlags, uint data, int x = CoordUnspecified, int y = CoordUnspecified)
		{
			EnsureInputSendPermission("send mouse input");
			if (sendMode != SendModes.Event)
			{
				PutMouseEventIntoArray(eventFlags, data, x, y);
				return;
			}

			var lht = Script.TheScript.HookThread as Keysharp.Internals.Input.Hooks.Unix.UnixHookThread;
			if (lht == null)
				return;

			WithSendScope(lht, () => ReplayMacMouseEvent(eventFlags, data, x, y, KeyIgnoreLevel(ThreadAccessors.A_SendLevel)));
		}

		internal override void MouseMove(ref int x, ref int y, ref uint eventFlags, long speed, bool moveOffset)
		{
			EnsureInputSendPermission("move mouse");
			if (x == CoordUnspecified || y == CoordUnspecified)
				return;

			if (sendMode == SendModes.Play)
			{
				PutMouseEventIntoArray((uint)MOUSEEVENTF.MOVE | (moveOffset ? MsgOffsetMouseMove : 0), 0, x, y);
				DoMouseDelay();

				if (moveOffset)
				{
					x = CoordUnspecified;
					y = CoordUnspecified;
				}

				return;
			}

			if (moveOffset)
			{
				if (sendMode == SendModes.Input)
				{
					if (sendInputCursorPos.X == CoordUnspecified)
					{
						if (GetCursorPos(out sendInputCursorPos))
						{
							x += sendInputCursorPos.X;
							y += sendInputCursorPos.Y;
						}
					}
					else
					{
						x += sendInputCursorPos.X;
						y += sendInputCursorPos.Y;
					}
				}
				else if (GetCursorPos(out POINT pos))
				{
					x += pos.X;
					y += pos.Y;
				}
			}
			else
			{
				CoordToScreen(ref x, ref y, CoordMode.Mouse);
			}

			if (sendMode == SendModes.Input)
			{
				sendInputCursorPos.X = x;
				sendInputCursorPos.Y = y;
				AddArrayEvent(ArrayEvent.MouseMoveAbs(x, y));
				DoMouseDelay();
				return;
			}

			if (speed < 0)
				speed = 0;
			else if (speed > MaxMouseSpeed)
				speed = MaxMouseSpeed;

			if (speed == 0 || !GetCursorPos(out POINT cursorPos))
			{
				MacNativeInput.PostMouseMove(x, y, KeyIgnoreLevel(ThreadAccessors.A_SendLevel));
				DoMouseDelay();
				return;
			}

			long cx = cursorPos.X;
			long cy = cursorPos.Y;
			const int incrMouseMinSpeed = 32;
			var extraInfo = KeyIgnoreLevel(ThreadAccessors.A_SendLevel);

			void Step(ref long cur, long dest)
			{
				if (cur == dest)
					return;

				var delta = (dest > cur ? dest - cur : cur - dest) / speed;
				if (delta == 0 || delta < incrMouseMinSpeed)
					delta = incrMouseMinSpeed;

				if (dest > cur)
					cur = Math.Min(dest, cur + delta);
				else
					cur = Math.Max(dest, cur - delta);
			}

			while (cx != x || cy != y)
			{
				Step(ref cx, x);
				Step(ref cy, y);
				MacNativeInput.PostMouseMove((int)cx, (int)cy, extraInfo);
				DoMouseDelay();
			}
		}

		private void ReplayMacMouseEvent(uint eventFlags, uint data, int x, int y, long extraInfo)
		{
			if ((eventFlags & 0xFFFF0000) != 0)
			{
				var button = KeyCodes.VkToMouseButton(eventFlags & 0xFFFF);
				var type = (KeyEventTypes)(eventFlags >> 16);
				EmitMacButton(button, type, x, y, extraInfo);
				DoMouseDelay();
				return;
			}

			var actionFlags = eventFlags & (0x1FFFu & ~(uint)MOUSEEVENTF.MOVE);
			var hasMove = (eventFlags & (uint)MOUSEEVENTF.MOVE) != 0;
			var relativeMove = (eventFlags & MsgOffsetMouseMove) != 0;

			if (hasMove)
			{
				if (relativeMove)
					MacNativeInput.PostMouseMoveRelative(x, y, extraInfo);
				else
				{
					EnsureCoords(ref x, ref y);
					MacNativeInput.PostMouseMove(x, y, extraInfo);
				}
			}

			switch (actionFlags)
			{
				case (uint)MOUSEEVENTF.LEFTDOWN:
					EmitMacButton(MouseButton.Button1, KeyEventTypes.KeyDown, x, y, extraInfo);
					break;
				case (uint)MOUSEEVENTF.LEFTUP:
					EmitMacButton(MouseButton.Button1, KeyEventTypes.KeyUp, x, y, extraInfo);
					break;
				case (uint)MOUSEEVENTF.RIGHTDOWN:
					EmitMacButton(MouseButton.Button2, KeyEventTypes.KeyDown, x, y, extraInfo);
					break;
				case (uint)MOUSEEVENTF.RIGHTUP:
					EmitMacButton(MouseButton.Button2, KeyEventTypes.KeyUp, x, y, extraInfo);
					break;
				case (uint)MOUSEEVENTF.MIDDLEDOWN:
					EmitMacButton(MouseButton.Button3, KeyEventTypes.KeyDown, x, y, extraInfo);
					break;
				case (uint)MOUSEEVENTF.MIDDLEUP:
					EmitMacButton(MouseButton.Button3, KeyEventTypes.KeyUp, x, y, extraInfo);
					break;
				case (uint)MOUSEEVENTF.XDOWN:
					EmitMacButton(data == MouseUtils.XBUTTON2 ? MouseButton.Button5 : MouseButton.Button4, KeyEventTypes.KeyDown, x, y, extraInfo);
					break;
				case (uint)MOUSEEVENTF.XUP:
					EmitMacButton(data == MouseUtils.XBUTTON2 ? MouseButton.Button5 : MouseButton.Button4, KeyEventTypes.KeyUp, x, y, extraInfo);
					break;
				case (uint)MOUSEEVENTF.WHEEL:
					MacNativeInput.PostMouseWheel(unchecked((short)data), MouseWheelScrollDirection.Vertical, extraInfo);
					break;
				case (uint)MOUSEEVENTF.HWHEEL:
					MacNativeInput.PostMouseWheel(unchecked((short)data), MouseWheelScrollDirection.Horizontal, extraInfo);
					break;
			}

			DoMouseDelay();
		}

		private static void EmitMacButton(MouseButton button, KeyEventTypes type, int x, int y, long extraInfo)
		{
			if (button == MouseButton.NoButton)
				return;

			EnsureCoords(ref x, ref y);

			if (type != KeyEventTypes.KeyUp)
				MacNativeInput.PostMouseButton(button, true, x, y, extraInfo);
			if (type != KeyEventTypes.KeyDown)
				MacNativeInput.PostMouseButton(button, false, x, y, extraInfo);
		}

		private static void EmitMacTextWithControls(string text, long extraInfo)
		{
			if (string.IsNullOrEmpty(text))
				return;

			var chunk = new StringBuilder();
			bool lastWasCR = false;

			void FlushChunk()
			{
				if (chunk.Length == 0)
					return;

				MacNativeInput.PostUnicodeText(chunk.ToString(), extraInfo);
				chunk.Clear();
			}

			foreach (var ch in text)
			{
				switch (ch)
				{
					case '\r':
						FlushChunk();
						MacNativeInput.PostKeyboard(VK_RETURN, true, extraInfo);
						MacNativeInput.PostKeyboard(VK_RETURN, false, extraInfo);
						lastWasCR = true;
						break;
					case '\n':
						if (lastWasCR)
						{
							lastWasCR = false;
							break;
						}
						FlushChunk();
						MacNativeInput.PostKeyboard(VK_RETURN, true, extraInfo);
						MacNativeInput.PostKeyboard(VK_RETURN, false, extraInfo);
						break;
					case '\t':
						FlushChunk();
						MacNativeInput.PostKeyboard(VK_TAB, true, extraInfo);
						MacNativeInput.PostKeyboard(VK_TAB, false, extraInfo);
						lastWasCR = false;
						break;
					case '\b':
						FlushChunk();
						MacNativeInput.PostKeyboard(VK_BACK, true, extraInfo);
						MacNativeInput.PostKeyboard(VK_BACK, false, extraInfo);
						lastWasCR = false;
						break;
					default:
						chunk.Append(ch);
						lastWasCR = false;
						break;
				}
			}

			FlushChunk();
		}
	}

	internal sealed class MacNativeEventTap : IDisposable
	{
		private readonly MacHookThread owner;
		private readonly MacNativeInput.CGEventTapCallBack callback;
		private readonly ManualResetEventSlim ready = new(false);
		private Thread thread;
		private nint tap;
		private nint source;
		private nint runLoop;
		private volatile bool stopping;

		internal MacNativeEventTap(MacHookThread owner)
		{
			this.owner = owner;
			callback = OnEvent;
		}

		internal bool Start(int timeoutMs)
		{
			if (thread != null)
				return true;

			stopping = false;
			ready.Reset();
			thread = new Thread(Run) { IsBackground = true, Name = "Keysharp macOS CGEvent tap" };
			thread.Start();
			return ready.Wait(timeoutMs) && tap != nint.Zero;
		}

		internal void Stop()
		{
			stopping = true;

			if (runLoop != nint.Zero)
				MacNativeInput.CFRunLoopStop(runLoop);

			if (thread != null && thread.IsAlive)
				_ = thread.Join(250);

			thread = null;
		}

		private void Run()
		{
			runLoop = MacNativeInput.CFRunLoopGetCurrent();
			tap = MacNativeInput.CGEventTapCreate(
				MacNativeInput.kCGSessionEventTap,
				MacNativeInput.kCGHeadInsertEventTap,
				MacNativeInput.kCGEventTapOptionDefault,
				MacNativeInput.HookEventMask,
				callback,
				nint.Zero);

			if (tap == nint.Zero)
			{
				ready.Set();
				return;
			}

			source = MacNativeInput.CFMachPortCreateRunLoopSource(nint.Zero, tap, nint.Zero);
			if (source == nint.Zero)
			{
				ready.Set();
				return;
			}

			MacNativeInput.CFRunLoopAddSource(runLoop, source, MacNativeInput.RunLoopDefaultMode);
			ready.Set();
			MacNativeInput.CFRunLoopRun();

			if (source != nint.Zero)
			{
				MacNativeInput.CFRunLoopRemoveSource(runLoop, source, MacNativeInput.RunLoopDefaultMode);
				MacNativeInput.CFRelease(source);
				source = nint.Zero;
			}

			if (tap != nint.Zero)
			{
				MacNativeInput.CFRelease(tap);
				tap = nint.Zero;
			}

			runLoop = nint.Zero;
		}

		private nint OnEvent(nint proxy, uint type, nint cgEvent, nint userInfo)
		{
			if (type == MacNativeInput.kCGEventTapDisabledByTimeout || type == MacNativeInput.kCGEventTapDisabledByUserInput)
			{
				if (tap != nint.Zero)
					MacNativeInput.CGEventTapEnable(tap, true);
				return cgEvent;
			}

			if (stopping || cgEvent == nint.Zero)
				return cgEvent;

			var suppress = owner.ProcessNativeKeyboardEvent(type, cgEvent)
				|| owner.ProcessNativeMouseEvent(type, cgEvent);
			return suppress ? nint.Zero : cgEvent;
		}

		public void Dispose()
		{
			Stop();
			ready.Dispose();
		}
	}
}
#endif
