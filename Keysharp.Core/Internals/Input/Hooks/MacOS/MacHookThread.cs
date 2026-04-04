using Keysharp.Builtins;
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

		private readonly Lock mappingLock = new();
		private readonly Dictionary<uint, uint> rawScToVk = new();
		private readonly Dictionary<uint, uint> vkToRawSc = new();

		protected override bool UseSyntheticEventQueue => false;

		protected override bool MarkSimulatedIfNeeded(HookEventArgs e, uint vk, KeyCode keyCode, bool keyUp, out ulong extraInfo)
		{
			var simulated = e.IsEventSimulated;
			extraInfo = simulated ? (ulong)KeyIgnoreAllExceptModifier : 0UL;
			return simulated;
		}

		protected override void RecordScVkMapping(uint sc, uint vk)
		{
			if (sc == 0 || vk == 0)
				return;

			lock (mappingLock)
			{
				rawScToVk[sc] = vk;
				vkToRawSc[vk] = sc;
			}
		}

		protected override uint MapScToVkPlatform(uint sc)
		{
			if (sc == 0)
				return 0;

			lock (mappingLock)
			{
				if (rawScToVk.TryGetValue(sc, out var vk))
					return vk;
			}

			return base.MapScToVkPlatform(sc);
		}

		protected override uint MapVkToScPlatform(uint vk, bool returnSecondary = false)
		{
			if (vk == 0)
				return 0;

			lock (mappingLock)
			{
				if (vkToRawSc.TryGetValue(vk, out var sc))
					return sc;
			}

			return base.MapVkToScPlatform(vk, returnSecondary);
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
