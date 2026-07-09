using Keysharp.Builtins;
#if OSX
using System.Runtime.InteropServices;
using Keysharp.Internals.Input.Keyboard;
using Keysharp.Internals.Input.MacOS;
using static Keysharp.Internals.Input.Keyboard.KeyboardMouseSender;
using static Keysharp.Internals.Input.Keyboard.KeyboardUtils;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;

namespace Keysharp.Internals.Input.Hooks.MacOS
{
	// macOS uses a native CGEvent sender/tap while still reusing UnixHookThread's
	// higher-level hotkey, hotstring and InputHook decision pipeline.
	internal sealed class MacHookThread : Keysharp.Internals.Input.Hooks.Unix.UnixHookThread
	{
		private const int HookStartTimeoutMs = 3000;
		private MacNativeEventTap nativeEventTap;

		[DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		private static extern int CGAssociateMouseAndMouseCursorPosition(int connected);

		protected override void EnsureCursorClipPermissions()
		{
			base.EnsureCursorClipPermissions();
			_ = Script.TheScript.Permissions.EnsureInputInjection(operation: "ClipCursor");
		}

		protected override bool CanClipCursor(out string reason)
		{
			var active = HasMouseHook();
			reason = active ? "" : "the global mouse hook is not active";
			return active;
		}

		// Freeze/unfreeze physical cursor movement for BlockInput's mouse-move block and an InputHook with
		// VisibleMouseMove := false. libuiohook taps at kCGSessionEventTap, which is downstream of the OS
		// cursor mover, so setting SuppressEvent on a move only hides it from applications -- the visible
		// cursor has already moved. Disassociating the cursor from the mouse is what actually stops it;
		// move events keep flowing to the tap (so callbacks still fire and we can re-associate on release).
		// macOS restores the association automatically when the process exits, and DeregisterHooks resets
		// it, so a decoupled cursor can't outlive the script.
		protected override void OnMoveSuppressionChanged(bool active)
			=> CGAssociateMouseAndMouseCursorPosition(active ? 0 : 1);

		protected override KeyboardMouseSender CreateKbdMsSender()
			=> new MacKeyboardMouseSender();

		// macOS App Switcher uses Cmd+Tab, not Alt+Tab.
		protected override uint AltTabModifierVk => VK_LWIN;
		protected override uint AltTabModifierMask => MOD_LWIN | MOD_RWIN;
		protected override bool IsAltTabModifierVk(uint vk) => vk == VK_LWIN || vk == VK_RWIN;

		protected override uint CurrentAltTabModifierVk
		{
			get
			{
				if ((kbdMsSender.modifiersLRLogical & MOD_LWIN) != 0)
					return VK_LWIN;
				if ((kbdMsSender.modifiersLRLogical & MOD_RWIN) != 0)
					return VK_RWIN;
				return 0;
			}
		}

		// macOS hook SC values are raw kVK codes (resolved via KeyCodes' hardcoded kVK table).
		// Keep these 0 so modifier handling stays VK-based and the SC modifier tables aren't
		// seeded from kVK codes.
		internal override uint SC_LCONTROL => 0;
		internal override uint SC_RCONTROL => 0;
		internal override uint SC_LALT => 0;
		internal override uint SC_RALT => 0;
		internal override uint SC_LSHIFT => 0;
		internal override uint SC_RSHIFT => 0;
		internal override uint SC_LWIN => 0;
		internal override uint SC_RWIN => 0;

		// macOS has no process-local BlockInput equivalent, so the native tap suppresses
		// physical events directly while allowing Keysharp's own injected events through.
		private static bool ShouldSuppressForBlockInput(bool isInjected) =>
			!isInjected && Script.TheScript.KeyboardData.blockInput;

		internal bool ProcessNativeKeyboardEvent(uint type, nint cgEvent)
		{
			if (type != MacNativeInput.kCGEventKeyDown
				&& type != MacNativeInput.kCGEventKeyUp
				&& type != MacNativeInput.kCGEventFlagsChanged)
				return false;

			var flags = MacNativeInput.CGEventGetFlags(cgEvent);
			var extraInfo = unchecked((ulong)MacNativeInput.CGEventGetIntegerValueField(cgEvent, MacNativeInput.kCGEventSourceUserData));
			var unicodeBuffer = new char[8];
			var unicodeLength = 0;
			var isKeysharpInjected = HasKeysharpInjectedExtraInfo(extraInfo);
			var hasSyntheticUnicode = isKeysharpInjected
				&& type == MacNativeInput.kCGEventKeyDown
				&& MacNativeInput.TryGetKeyboardUnicodeString(cgEvent, unicodeBuffer, out unicodeLength);

			if (hasSyntheticUnicode)
				return ProcessSyntheticUnicodeText(unicodeBuffer, unicodeLength, flags, extraInfo);

			if (isKeysharpInjected
				&& type == MacNativeInput.kCGEventKeyUp
				&& MacNativeInput.TryGetKeyboardUnicodeString(cgEvent, unicodeBuffer, out _))
				return false;

			var sc = (uint)MacNativeInput.CGEventGetIntegerValueField(cgEvent, MacNativeInput.kCGKeyboardEventKeycode);
			if (!KeyCodes.TryMapMacCodeToVk(sc, out var vk) || vk == 0)
				return false;

			var keyUp = type == MacNativeInput.kCGEventKeyUp
				|| (type == MacNativeInput.kCGEventFlagsChanged && MacNativeInput.IsKeyUpFromFlagsChanged(vk, flags));
			var mask = MacNativeInput.ToEventMask(flags);
			if (isKeysharpInjected)
				mask |= EventMask.SimulatedEvent;
			Keysharp.Internals.MacKeyboard.UpdateIndicatorSnapshotFromMask(mask);

			var args = new KeyboardHookEventArgs(keyUp ? EventType.KeyReleased : EventType.KeyPressed, vk, sc, mask);
			return ProcessNativeKeyboardEvent(args, vk, sc, keyUp, extraInfo);
		}

		private bool ProcessSyntheticUnicodeText(char[] chars, int length, ulong flags, ulong extraInfo)
		{
			var mask = MacNativeInput.ToEventMask(flags) | EventMask.SimulatedEvent;
			Keysharp.Internals.MacKeyboard.UpdateIndicatorSnapshotFromMask(mask);
			var suppress = false;

			for (var i = 0; i < length; i++)
			{
				var args = new KeyboardHookEventArgs(EventType.KeyPressed, VK_PACKET, chars[i], mask);
				_ = ProcessNativeKeyboardEvent(args, VK_PACKET, chars[i], keyUp: false, extraInfo);
				suppress |= args.SuppressEvent;
			}

			return suppress;
		}

		private bool ProcessNativeKeyboardEvent(KeyboardHookEventArgs args, uint vk, uint sc, bool keyUp, ulong eventExtraInfo)
		{
			if (!keyboardEnabled)
				return false;

			lastHookEventWasKeyboard = true;
			lastKeyboardEventVk = vk;
			var isInjected = HasKeysharpInjectedExtraInfo(eventExtraInfo) || args.IsEventSimulated;

			if (ShouldSuppressForBlockInput(isInjected))
			{
				UpdateObservedPhysicalKeyState(vk, keyUp, isInjected);
				if (!keyUp && !isInjected)
					OnPhysicalKeyDownSuppressed(vk);
				args.SuppressEvent = true;
				return true;
			}

			if (eventExtraInfo == (ulong)KeyBlockThis)
			{
				args.SuppressEvent = true;
				return true;
			}

			var result = LowLevelCommon(args, vk, sc, sc, keyUp, eventExtraInfo, isInjected ? HOOK_EVENT_INJECTED : 0);
			ApplyKeyStateAfterKeyboardDecision(vk, keyUp, isInjected, result);

			if (result != 0)
			{
				if (!keyUp && !isInjected)
					OnPhysicalKeyDownSuppressed(vk);
				args.SuppressEvent = true;
			}

			return args.SuppressEvent;
		}

		internal bool ProcessNativeMouseEvent(uint type, nint cgEvent)
		{
			var script = Script.TheScript;

			if (type != MacNativeInput.kCGEventLeftMouseDown
				&& type != MacNativeInput.kCGEventLeftMouseUp
				&& type != MacNativeInput.kCGEventRightMouseDown
				&& type != MacNativeInput.kCGEventRightMouseUp
				&& type != MacNativeInput.kCGEventMouseMoved
				&& type != MacNativeInput.kCGEventLeftMouseDragged
				&& type != MacNativeInput.kCGEventRightMouseDragged
				&& type != MacNativeInput.kCGEventScrollWheel
				&& type != MacNativeInput.kCGEventOtherMouseDown
				&& type != MacNativeInput.kCGEventOtherMouseUp
				&& type != MacNativeInput.kCGEventOtherMouseDragged)
				return false;

			var flags = MacNativeInput.CGEventGetFlags(cgEvent);
			var extraInfo = unchecked((ulong)MacNativeInput.CGEventGetIntegerValueField(cgEvent, MacNativeInput.kCGEventSourceUserData));
			var mask = MacNativeInput.ToEventMask(flags);
			var isInjected = HasKeysharpInjectedExtraInfo(extraInfo);

			if (isInjected)
				mask |= EventMask.SimulatedEvent;
			Keysharp.Internals.MacKeyboard.UpdateIndicatorSnapshotFromMask(mask);

			var loc = MacNativeInput.CGEventGetLocation(cgEvent);
			var x = (int)Math.Round(loc.X);
			var y = (int)Math.Round(loc.Y);

			if (type == MacNativeInput.kCGEventMouseMoved
				|| type == MacNativeInput.kCGEventLeftMouseDragged
				|| type == MacNativeInput.kCGEventRightMouseDragged
				|| type == MacNativeInput.kCGEventOtherMouseDragged)
			{
				if (!mouseEnabled)
					return false;

				lastHookEventWasKeyboard = false;
				var suppressMove = false;

				if (!isInjected)
				{
					script.timeLastInputPhysical = script.timeLastInputMouse = DateTime.UtcNow;

					if (CursorClipActive)
					{
						var cx = x;
						var cy = y;

						if (ClampToCursorClip(ref cx, ref cy))
						{
							_ = Platform.Mouse.TryMoveAbsolute(cx, cy);
							return true;
						}
					}

					suppressMove = script.KeyboardData.blockMouseMove || script.KeyboardData.blockInput;
				}

				if (script.input != null && !CollectMouseMove(extraInfo, x, y, null))
					suppressMove = true;

				if (!isInjected)
					SetMoveSuppression(suppressMove);

				return suppressMove;
			}

			if (type == MacNativeInput.kCGEventScrollWheel)
			{
				if (!mouseEnabled)
					return false;

				lastHookEventWasKeyboard = false;

				if (!isInjected)
					script.timeLastInputPhysical = script.timeLastInputMouse = DateTime.UtcNow;

				var vertical = (int)MacNativeInput.CGEventGetIntegerValueField(cgEvent, MacNativeInput.kCGScrollWheelEventDeltaAxis1);
				var horizontal = (int)MacNativeInput.CGEventGetIntegerValueField(cgEvent, MacNativeInput.kCGScrollWheelEventDeltaAxis2);
				var useHorizontal = horizontal != 0 && Math.Abs(horizontal) >= Math.Abs(vertical);
				var delta = useHorizontal ? horizontal : vertical;
				var wheelVk = useHorizontal
					? (delta < 0 ? VK_WHEEL_LEFT : VK_WHEEL_RIGHT)
					: (delta < 0 ? VK_WHEEL_DOWN : VK_WHEEL_UP);
				var wheelSc = (uint)delta;
				var args = new MouseWheelHookEventArgs(
					delta,
					useHorizontal ? MouseWheelScrollDirection.Horizontal : MouseWheelScrollDirection.Vertical,
					x,
					y,
					mask);

				if (ShouldSuppressForBlockInput(isInjected))
					return true;

				var result = LowLevelCommon(args, wheelVk, wheelSc, wheelSc, keyUp: false, extraInfo, isInjected ? HOOK_EVENT_INJECTED : 0);
				return result != 0;
			}

			var buttonNumber = (uint)MacNativeInput.CGEventGetIntegerValueField(cgEvent, MacNativeInput.kCGMouseEventButtonNumber);
			var button = MacNativeInput.ToMouseButton(type, buttonNumber);

			if (button == MouseButton.NoButton)
				return false;

			var keyUp = type == MacNativeInput.kCGEventLeftMouseUp
				|| type == MacNativeInput.kCGEventRightMouseUp
				|| type == MacNativeInput.kCGEventOtherMouseUp;

			if (!mouseEnabled)
				return false;

			lastHookEventWasKeyboard = false;

			if (!isInjected)
				script.timeLastInputPhysical = script.timeLastInputMouse = DateTime.UtcNow;

			var buttonVk = button switch
			{
				MouseButton.Button1 => VK_LBUTTON,
				MouseButton.Button2 => VK_RBUTTON,
				MouseButton.Button3 => VK_MBUTTON,
				MouseButton.Button4 => VK_XBUTTON1,
				MouseButton.Button5 => VK_XBUTTON2,
				_ => 0u
			};

			if (buttonVk == 0)
				return false;

			if (ShouldSuppressForBlockInput(isInjected))
				return true;

			var mouseArgs = new MouseHookEventArgs(keyUp ? EventType.MouseReleased : EventType.MousePressed, button, x, y, mask);
			var buttonResult = LowLevelCommon(mouseArgs, buttonVk, 0, 0, keyUp, extraInfo, isInjected ? HOOK_EVENT_INJECTED : 0);
			return buttonResult != 0;
		}

		protected override string PlatformHookDisabledMessage => "macOS hook disabled via KEYSHARP_DISABLE_HOOK=1.";

		protected override bool StartPlatformHookCore(bool wantKeyboard, bool wantMouse, out string message)
		{
			message = string.Empty;

			if (!wantKeyboard && !wantMouse)
				return false;

			if (Script.IsUiInitializationBlocked || nativeEventTap != null)
				return true;

			_ = Script.TheScript.Permissions.EnsureInputMonitoring(operation: "install keyboard/mouse hooks");
			nativeEventTap = new MacNativeEventTap(this);

			if (nativeEventTap.Start(HookStartTimeoutMs))
				return true;

			nativeEventTap.Dispose();
			nativeEventTap = null;
			message = "Global hook failed on macOS: CGEventTapCreate returned null. Check Accessibility/Input Monitoring permissions.";
			return false;
		}

		protected override void StopPlatformHookCore(bool dispose)
		{
			if (!dispose)
				return;

			try { nativeEventTap?.Dispose(); } catch { }
			nativeEventTap = null;
			base.StopPlatformHookCore(dispose);
			OnMoveSuppressionChanged(false);
		}

		protected override void OnPhysicalKeyDownSuppressed(uint vk)
		{
			// The HID driver toggles the CapsLock lock state (and LED) before the
			// event tap sees the key-down, so suppression alone can't prevent the
			// toggle. Undo it so a suppressed CapsLock press (prefix key, remap,
			// AlwaysOn/Off) behaves as if the key was never pressed.
			if (vk == VK_CAPITAL)
				_ = Keysharp.Internals.Input.MacOS.MacCapsLockState.TryToggle();
		}
	}
}
#endif
