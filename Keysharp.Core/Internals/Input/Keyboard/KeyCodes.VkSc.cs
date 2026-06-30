#if !WINDOWS
using System;
using SharpHook.Data;
#if LINUX
#endif

namespace Keysharp.Internals.Input.Keyboard
{
	/// <summary>
	/// VK ⇄ SC dispatch for the non-Windows backends. On Linux SC is either an inputd evdev
	/// code or an X11 XKeycode (chosen at runtime); on macOS SC is the kVK code. Part of the
	/// unified <see cref="KeyCodes"/> facade.
	/// </summary>
	internal static partial class KeyCodes
	{
#if LINUX
		// inputd is the default backend; the legacy X11/SharpHook fallback uses XKeycodes instead.
		private static bool UseInputdScanCodes => Platform.Input.ActiveTransport == InputTransport.Inputd;

		public static uint MapScToVk(uint sc)
		{
			if (sc == 0)
				return 0;

			if (UseInputdScanCodes)
				return EvdevToVk(sc);

			if (!IsX11Available)
				return 0;

			if (TryMapXKeycodeToVk(sc, out var mappedVk))
				return mappedVk;

			var keysym = (ulong)XDisplay.Default.XKeycodeToKeysym((int)sc, 0);
			return keysym != 0 ? KeysymToVk(keysym) : 0;
		}

		public static uint MapVkToSc(uint vk, bool returnSecondary = false)
		{
			if (vk == 0)
				return 0;

			if (UseInputdScanCodes)
				return VkToEvdev(vk, returnSecondary);

			return ResolveVkToXKeycode(vk, out var xcode, returnSecondary) ? xcode : 0;
		}

		/// <summary>
		/// Resolves a VK to an X11 keycode via the layout-aware xkb provider, falling back to a
		/// fixed VK→keysym table + XKeysymToKeycode. Used for X11 input grabs and key simulation.
		/// </summary>
		internal static bool ResolveVkToXKeycode(uint vk, out uint xcode, bool returnSecondary = false)
		{
			xcode = 0;

			if (!IsX11Available || vk == 0)
				return false;

			if (TryMapVkToXKeycode(vk, out xcode, returnSecondary))
				return true;

			ulong keysym = VkToKeysym(vk);

			if (keysym == 0)
				return false;

			xcode = (uint)XDisplay.Default.XKeysymToKeycode((IntPtr)keysym);
			return xcode != 0;
		}
#endif

#if OSX
		public static uint MapScToVk(uint sc)
		{
			if (sc == 0)
				return 0;

			// SC is the raw macOS kVK code (CGKeyCode) delivered by the SharpHook backend; the
			// hardcoded kVK table is canonical. Fall back to the SharpHook KeyCode mapping only as
			// a last resort for codes not in the table.
			if (TryMapMacCodeToVk(sc, out var vk))
				return vk;

			return SharpHookToVk((KeyCode)sc);
		}

		public static uint MapVkToSc(uint vk, bool returnSecondary = false)
		{
			if (vk == 0)
				return 0;

			if (TryMapVkToMacCode(vk, out var sc))
				return sc;

			var kc = VkToSharpHook(vk);
			return kc == KeyCode.VcUndefined ? 0u : (uint)kc;
		}
#endif
	}
}
#endif
