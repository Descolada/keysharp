using Keysharp.Builtins;
using System.Data.Common;

#if OSX
using System.Runtime.InteropServices;
using SharpHook;
using SharpHook.Data;
using static Keysharp.Internals.Input.Keyboard.KeyboardMouseSender;
using static Keysharp.Internals.Input.Keyboard.KeyboardUtils;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;

namespace Keysharp.Internals.Input.Hooks.MacOS
{
	// macOS reuses the non-Windows SharpHook pipeline from UnixHookThread.
	// X11-specific behavior remains disabled via PlatformManager.IsX11Available.
	internal sealed class MacHookThread : Keysharp.Internals.Input.Hooks.Unix.UnixHookThread
	{
		// NSEvent modifier bit masks. Using raw bits avoids relying on binding-specific enum names.
		private const nuint ShiftKeyMask = 1u << 17;
		private const nuint ControlKeyMask = 1u << 18;
		private const nuint AlternateKeyMask = 1u << 19;
		private const nuint CommandKeyMask = 1u << 20;
		private const nuint AlphaShiftKeyMask = 1u << 16;
		private const int kCGEventSourceStateCombinedSessionState = 0;

		[DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		private static extern ulong CGEventSourceFlagsState(int sourceState);

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

		protected override bool UseSyntheticEventQueue => false;

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

		protected override bool MarkSimulatedIfNeeded(HookEventArgs e, uint vk, KeyCode keyCode, bool keyUp, out ulong extraInfo)
		{
			var simulated = e.IsEventSimulated;
			extraInfo = simulated ? (ulong)KeyIgnoreAllExceptModifier : 0UL;
			return simulated;
		}

		protected override bool TryQueryModifierLRStatePlatform(out uint mods, byte[] keymapBuffer = null)
		{
			// Native query path so GetKeyState works before any hook events are seen.
			if (TryGetCurrentModifierFlags(out var flags))
			{
				mods = 0u;

				// macOS does not expose left/right in this API. Mirror to both sides so
				// neutral and sided queries both have deterministic behavior.
				if ((flags & ShiftKeyMask) != 0)
					mods |= MOD_LSHIFT | MOD_RSHIFT;
				if ((flags & ControlKeyMask) != 0)
					mods |= MOD_LCONTROL | MOD_RCONTROL;
				if ((flags & AlternateKeyMask) != 0)
					mods |= MOD_LALT | MOD_RALT;
				if ((flags & CommandKeyMask) != 0)
					mods |= MOD_LWIN | MOD_RWIN;

				return true;
			}

			// Fallback to hook snapshots when native query is unavailable.
			mods = 0u;
			if (VK_LSHIFT < logicalKeyState.Length && (logicalKeyState[VK_LSHIFT] & StateDown) != 0) mods |= MOD_LSHIFT;
			if (VK_RSHIFT < logicalKeyState.Length && (logicalKeyState[VK_RSHIFT] & StateDown) != 0) mods |= MOD_RSHIFT;
			if (VK_LCONTROL < logicalKeyState.Length && (logicalKeyState[VK_LCONTROL] & StateDown) != 0) mods |= MOD_LCONTROL;
			if (VK_RCONTROL < logicalKeyState.Length && (logicalKeyState[VK_RCONTROL] & StateDown) != 0) mods |= MOD_RCONTROL;
			if (VK_LMENU < logicalKeyState.Length && (logicalKeyState[VK_LMENU] & StateDown) != 0) mods |= MOD_LALT;
			if (VK_RMENU < logicalKeyState.Length && (logicalKeyState[VK_RMENU] & StateDown) != 0) mods |= MOD_RALT;
			if (VK_LWIN < logicalKeyState.Length && (logicalKeyState[VK_LWIN] & StateDown) != 0) mods |= MOD_LWIN;
			if (VK_RWIN < logicalKeyState.Length && (logicalKeyState[VK_RWIN] & StateDown) != 0) mods |= MOD_RWIN;

			if (mods != 0)
				return true;

			if (VK_LSHIFT < physicalKeyState.Length && (physicalKeyState[VK_LSHIFT] & StateDown) != 0) mods |= MOD_LSHIFT;
			if (VK_RSHIFT < physicalKeyState.Length && (physicalKeyState[VK_RSHIFT] & StateDown) != 0) mods |= MOD_RSHIFT;
			if (VK_LCONTROL < physicalKeyState.Length && (physicalKeyState[VK_LCONTROL] & StateDown) != 0) mods |= MOD_LCONTROL;
			if (VK_RCONTROL < physicalKeyState.Length && (physicalKeyState[VK_RCONTROL] & StateDown) != 0) mods |= MOD_RCONTROL;
			if (VK_LMENU < physicalKeyState.Length && (physicalKeyState[VK_LMENU] & StateDown) != 0) mods |= MOD_LALT;
			if (VK_RMENU < physicalKeyState.Length && (physicalKeyState[VK_RMENU] & StateDown) != 0) mods |= MOD_RALT;
			if (VK_LWIN < physicalKeyState.Length && (physicalKeyState[VK_LWIN] & StateDown) != 0) mods |= MOD_LWIN;
			if (VK_RWIN < physicalKeyState.Length && (physicalKeyState[VK_RWIN] & StateDown) != 0) mods |= MOD_RWIN;
			return mods != 0;
		}

		protected override bool TryQueryKeyStatePlatform(uint vk, out bool isDown)
		{
			isDown = false;

			if (vk == 0)
				return false;

			// Fast path for modifiers from native macOS state.
			if (TryQueryModifierLRStatePlatform(out var mods))
			{
				switch (vk)
				{
					case VK_SHIFT: isDown = (mods & (MOD_LSHIFT | MOD_RSHIFT)) != 0; return true;
					case VK_LSHIFT: isDown = (mods & MOD_LSHIFT) != 0; return true;
					case VK_RSHIFT: isDown = (mods & MOD_RSHIFT) != 0; return true;
					case VK_CONTROL: isDown = (mods & (MOD_LCONTROL | MOD_RCONTROL)) != 0; return true;
					case VK_LCONTROL: isDown = (mods & MOD_LCONTROL) != 0; return true;
					case VK_RCONTROL: isDown = (mods & MOD_RCONTROL) != 0; return true;
					case VK_MENU: isDown = (mods & (MOD_LALT | MOD_RALT)) != 0; return true;
					case VK_LMENU: isDown = (mods & MOD_LALT) != 0; return true;
					case VK_RMENU: isDown = (mods & MOD_RALT) != 0; return true;
					case VK_LWIN: isDown = (mods & MOD_LWIN) != 0; return true;
					case VK_RWIN: isDown = (mods & MOD_RWIN) != 0; return true;
				}
			}

			// Toggle keys are queried through indicator state.
			if (vk == VK_CAPITAL || vk == VK_NUMLOCK || vk == VK_SCROLL)
			{
				if (TryGetIndicatorStates(out var capsOn, out var numOn, out var scrollOn))
				{
					isDown = vk switch
					{
						VK_CAPITAL => capsOn,
						VK_NUMLOCK => numOn,
						VK_SCROLL => scrollOn,
						_ => false
					};
					return true;
				}
			}

			if (vk < logicalKeyState.Length && (logicalKeyState[vk] & StateDown) != 0)
			{
				isDown = true;
				return true;
			}

			if (vk < physicalKeyState.Length)
			{
				isDown = (physicalKeyState[vk] & StateDown) != 0;
				return true;
			}

			return false;
		}

		internal override bool TryGetIndicatorStates(out bool capsOn, out bool numOn, out bool scrollOn)
		{
			var hasSnapshot = base.TryGetIndicatorStates(out var snapCaps, out var snapNum, out var snapScroll);

			// Prefer the HID driver's lock state: it's the ground truth and reflects
			// IOHIDSetModifierLockState changes immediately, whereas the session flag
			// state may lag briefly behind.
			if (Keysharp.Internals.Input.MacOS.MacCapsLockState.TryGet(out var hidCapsOn))
			{
				capsOn = hidCapsOn;
				numOn = hasSnapshot && snapNum;
				scrollOn = hasSnapshot && snapScroll;
				return true;
			}

			if (TryGetCurrentModifierFlags(out var flags))
			{
				capsOn = (flags & AlphaShiftKeyMask) != 0;
				numOn = hasSnapshot && snapNum;
				scrollOn = hasSnapshot && snapScroll;
				return true;
			}

			capsOn = snapCaps;
			numOn = snapNum;
			scrollOn = snapScroll;
			return hasSnapshot;
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

		private static bool TryGetCurrentModifierFlags(out nuint flags)
		{
			try
			{
				flags = (nuint)CGEventSourceFlagsState(kCGEventSourceStateCombinedSessionState);
				return true;
			}
			catch
			{
				flags = 0;
				return false;
			}
		}
	}
}
#endif
