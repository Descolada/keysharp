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
		[ThreadStatic]
		private static ulong nativeEventExtraInfo;
		[ThreadStatic]
		private static bool nativeEventHasExtraInfo;

		[StructLayout(LayoutKind.Sequential)]
		private struct CGPoint
		{
			public double X;
			public double Y;
			public CGPoint(double x, double y) { X = x; Y = y; }
		}

		[DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		private static extern int CGWarpMouseCursorPosition(CGPoint newCursorPosition);

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

		// Pull an initially-outside cursor into the clip rectangle. Re-associating cancels the post-warp
		// event-suppression interval so the cursor tracks the mouse again inside the rectangle.
		protected override void WarpCursor(int x, int y)
		{
			_ = CGWarpMouseCursorPosition(new CGPoint(x, y));
			_ = CGAssociateMouseAndMouseCursorPosition(1);
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

		protected override bool MarkSimulatedIfNeeded(HookEventArgs e, uint vk, bool keyUp, out ulong extraInfo)
		{
			if (nativeEventHasExtraInfo)
			{
				extraInfo = nativeEventExtraInfo;
				return extraInfo != 0 || e.IsEventSimulated;
			}

			var simulated = e.IsEventSimulated;
			// macOS CGEvents carry no readable send-level extraInfo, so every artificial keystroke arrives with
			// none. Treat it as send level 0 (KeyIgnore) rather than fully-ignored (KeyIgnoreAllExceptModifier),
			// so an InputHook can observe it — e.g. InputHUD's amber "injected" highlight, or any script watching
			// SendEvent output — while it still doesn't trigger hotkeys (level 0 <= a hotkey's InputLevel).
			extraInfo = simulated ? (ulong)KeyIgnore : 0UL;
			return simulated;
		}

		internal bool ProcessNativeKeyboardEvent(uint type, nint cgEvent)
		{
			if (type != MacNativeInput.kCGEventKeyDown
				&& type != MacNativeInput.kCGEventKeyUp
				&& type != MacNativeInput.kCGEventFlagsChanged)
				return false;

			var sc = (uint)MacNativeInput.CGEventGetIntegerValueField(cgEvent, MacNativeInput.kCGKeyboardEventKeycode);
			var vk = KeyCodes.MapScToVk(sc);
			if (vk == 0)
				return false;

			var flags = MacNativeInput.CGEventGetFlags(cgEvent);
			var keyUp = type == MacNativeInput.kCGEventKeyUp
				|| (type == MacNativeInput.kCGEventFlagsChanged && MacNativeInput.IsKeyUpFromFlagsChanged(vk, flags));
			var extraInfo = unchecked((ulong)MacNativeInput.CGEventGetIntegerValueField(cgEvent, MacNativeInput.kCGEventSourceUserData));
			var mask = MacNativeInput.ToEventMask(flags);
			if (extraInfo != 0)
				mask |= EventMask.SimulatedEvent;

			var args = new KeyboardHookEventArgs(keyUp ? EventType.KeyReleased : EventType.KeyPressed, vk, sc, mask);
			nativeEventExtraInfo = extraInfo;
			nativeEventHasExtraInfo = true;

			try
			{
				if (keyUp)
					OnKeyReleased(this, args);
				else
					OnKeyPressed(this, args);
			}
			finally
			{
				nativeEventExtraInfo = 0;
				nativeEventHasExtraInfo = false;
			}

			return args.SuppressEvent;
		}

		protected override void ChangeHookStateLinux(HookType req, bool changeIsTemporary, long expectedGeneration)
		{
			if (HookDisabledForMac())
			{
				lock (hookStateLock)
				{
					if (hookStateGeneration != expectedGeneration)
						return;

					keyboardEnabled = false;
					mouseEnabled = false;
					kbdHook = 0;
					mouseHook = 0;
					StopNativeHookCore(dispose: true);
				}

				Ks.OutputDebugLine("macOS hook disabled via KEYSHARP_DISABLE_HOOK=1.");
				return;
			}

			var wantKeyboard = (req & HookType.Keyboard) != 0;
			var wantMouse = (req & HookType.Mouse) != 0;

			lock (hookStateLock)
			{
				if (hookStateGeneration != expectedGeneration)
					return;

				var hadKeyboard = keyboardEnabled;
				var hadMouse = mouseEnabled;

				if (!wantKeyboard && !wantMouse)
				{
					keyboardEnabled = false;
					mouseEnabled = false;
					kbdHook = 0;
					mouseHook = 0;

					if (!changeIsTemporary)
						StopNativeHookCore(dispose: true);

					return;
				}

				if (!Script.IsUiInitializationBlocked && nativeEventTap == null)
				{
					_ = Script.TheScript.Permissions.EnsureInputMonitoring(operation: "install keyboard/mouse hooks");
					nativeEventTap = new MacNativeEventTap(this);

					if (!nativeEventTap.Start(HookStartTimeoutMs))
					{
						nativeEventTap.Dispose();
						nativeEventTap = null;
						Ks.OutputDebugLine("Global hook failed on macOS: CGEventTapCreate returned null. Check Accessibility/Input Monitoring permissions.");
					}
				}

				if (!changeIsTemporary)
				{
					if (wantKeyboard && !hadKeyboard)
					{
						keyboardEnabled = false;
						kbdHook = 0;
						ResetHook(false, HookType.Keyboard, true);
					}
					if (wantMouse && !hadMouse)
					{
						mouseEnabled = false;
						mouseHook = 0;
						ResetHook(false, HookType.Mouse, true);
					}
				}

				keyboardEnabled = wantKeyboard;
				mouseEnabled = wantMouse;
				kbdHook = wantKeyboard ? 1 : 0;
				mouseHook = wantMouse ? 1 : 0;
			}
		}

		protected internal override void DeregisterHooks()
		{
			lock (hookStateLock)
			{
				hookStateGeneration++;
				keyboardEnabled = false;
				mouseEnabled = false;
				StopNativeHookCore(dispose: true);
				kbdHook = 0;
				mouseHook = 0;
			}

			SyncHookMutexes(changeIsTemporary: false);
		}

		private void StopNativeHookCore(bool dispose)
		{
			if (!dispose)
				return;

			try { nativeEventTap?.Dispose(); } catch { }
			nativeEventTap = null;
			ResetTrackedInputState(clearSyntheticQueue: false);
			OnMoveSuppressionChanged(false);
		}

		private static bool HookDisabledForMac()
		{
			var env = Environment.GetEnvironmentVariable("KEYSHARP_DISABLE_HOOK");
			return !string.IsNullOrEmpty(env) &&
				   (env.Equals("1") || env.Equals("true", StringComparison.OrdinalIgnoreCase) || env.Equals("yes", StringComparison.OrdinalIgnoreCase));
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
