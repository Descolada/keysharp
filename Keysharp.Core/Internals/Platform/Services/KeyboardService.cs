using Keysharp.Internals.Input.Keyboard;
using static Keysharp.Internals.Input.Keyboard.KeyboardUtils;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;

namespace Keysharp.Internals
{
	internal sealed class DefaultKeyboard : IKeyboard
	{
		public bool TryQueryModifierLRState(out uint mods, byte[] keymapBuffer = null)
		{
			mods = 0u;
			return false;
		}

		public bool TryQueryKeyStateLogical(uint vk, out bool isDown)
		{
			isDown = false;
			return false;
		}

		public bool TryQueryKeyStatePhysical(uint vk, out bool isDown)
		{
			isDown = false;
			return false;
		}

		public bool TryGetIndicatorStates(out bool capsOn, out bool numOn, out bool scrollOn)
		{
			capsOn = numOn = scrollOn = false;
			return false;
		}
	}

#if WINDOWS
	internal sealed class WindowsKeyboard : IKeyboard
	{
		public bool TryQueryModifierLRState(out uint mods, byte[] keymapBuffer = null)
		{
			mods = 0u;

			if (TryQueryKeyStateLogical(VK_LSHIFT, out var down) && down) mods |= MOD_LSHIFT;
			if (TryQueryKeyStateLogical(VK_RSHIFT, out down) && down) mods |= MOD_RSHIFT;
			if (TryQueryKeyStateLogical(VK_LCONTROL, out down) && down) mods |= MOD_LCONTROL;
			if (TryQueryKeyStateLogical(VK_RCONTROL, out down) && down) mods |= MOD_RCONTROL;
			if (TryQueryKeyStateLogical(VK_LMENU, out down) && down) mods |= MOD_LALT;
			if (TryQueryKeyStateLogical(VK_RMENU, out down) && down) mods |= MOD_RALT;
			if (TryQueryKeyStateLogical(VK_LWIN, out down) && down) mods |= MOD_LWIN;
			if (TryQueryKeyStateLogical(VK_RWIN, out down) && down) mods |= MOD_RWIN;
			return true;
		}

		public bool TryQueryKeyStateLogical(uint vk, out bool isDown)
		{
			isDown = false;

			if (vk == 0 || vk > int.MaxValue)
				return false;

			isDown = (Keysharp.Internals.Os.Windows.WindowsAPI.GetAsyncKeyState((int)vk) & 0x8000) != 0;
			return true;
		}

		public bool TryQueryKeyStatePhysical(uint vk, out bool isDown)
			=> TryQueryKeyStateLogical(vk, out isDown);

		public bool TryGetIndicatorStates(out bool capsOn, out bool numOn, out bool scrollOn)
		{
			capsOn = (Keysharp.Internals.Os.Windows.WindowsAPI.GetKeyState((int)VK_CAPITAL) & 0x01) != 0;
			numOn = (Keysharp.Internals.Os.Windows.WindowsAPI.GetKeyState((int)VK_NUMLOCK) & 0x01) != 0;
			scrollOn = (Keysharp.Internals.Os.Windows.WindowsAPI.GetKeyState((int)VK_SCROLL) & 0x01) != 0;
			return true;
		}
	}
#endif

#if LINUX
	internal static class LinuxKeyboards
	{
		internal static IKeyboard Resolve()
			=> !Platform.Desktop.IsWaylandSession && Platform.Desktop.IsX11Available
				? new X11Keyboard()
				: new InputdKeyboard();
	}

	internal sealed class InputdKeyboard : IKeyboard
	{
		public bool TryQueryModifierLRState(out uint mods, byte[] keymapBuffer = null)
			=> Keysharp.Internals.Input.Linux.KeysharpInputdManager.TryGetKeyState(out mods, out _, out _, out _);

		public bool TryQueryKeyStateLogical(uint vk, out bool isDown)
		{
			isDown = false;

			if (vk == 0)
				return false;

			if (!Keysharp.Internals.Input.Linux.KeysharpInputdManager.TryGetKeyState(
				out var mods, out _, out _, out _, out var logicalKeys, out _))
				return false;

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

			var evdev = KeyCodes.VkToEvdev(vk);

			if (evdev == 0)
				return false;

			return TryGetEvdevBit(logicalKeys, evdev, out isDown);
		}

		public bool TryQueryKeyStatePhysical(uint vk, out bool isDown)
		{
			isDown = false;

			if (vk == 0)
				return false;

			if (!Keysharp.Internals.Input.Linux.KeysharpInputdManager.TryGetKeyState(
				out _, out _, out _, out _, out _, out var physicalKeys))
				return false;

			return TryGetVkFromEvdevBitmap(vk, physicalKeys, out isDown);
		}

		public bool TryGetIndicatorStates(out bool capsOn, out bool numOn, out bool scrollOn)
			=> Keysharp.Internals.Input.Linux.KeysharpInputdManager.TryGetKeyState(out _, out capsOn, out numOn, out scrollOn);

		private static bool TryGetEvdevBit(byte[] keys, uint evdev, out bool isDown)
		{
			isDown = false;

			if (keys == null || evdev >= keys.Length * 8u)
				return false;

			isDown = (keys[evdev >> 3] & (1 << ((int)evdev & 7))) != 0;
			return true;
		}

		private static bool TryGetVkFromEvdevBitmap(uint vk, byte[] keys, out bool isDown)
		{
			isDown = false;

			switch (vk)
			{
				case VK_SHIFT:
					if (!TryGetEvdevBit(keys, KeyCodes.VkToEvdev(VK_LSHIFT), out var lShift)
						|| !TryGetEvdevBit(keys, KeyCodes.VkToEvdev(VK_RSHIFT), out var rShift))
						return false;
					isDown = lShift || rShift;
					return true;
				case VK_CONTROL:
					if (!TryGetEvdevBit(keys, KeyCodes.VkToEvdev(VK_LCONTROL), out var lCtrl)
						|| !TryGetEvdevBit(keys, KeyCodes.VkToEvdev(VK_RCONTROL), out var rCtrl))
						return false;
					isDown = lCtrl || rCtrl;
					return true;
				case VK_MENU:
					if (!TryGetEvdevBit(keys, KeyCodes.VkToEvdev(VK_LMENU), out var lAlt)
						|| !TryGetEvdevBit(keys, KeyCodes.VkToEvdev(VK_RMENU), out var rAlt))
						return false;
					isDown = lAlt || rAlt;
					return true;
			}

			var evdev = KeyCodes.VkToEvdev(vk);

			if (evdev == 0)
				return false;

			return TryGetEvdevBit(keys, evdev, out isDown);
		}
	}

	internal sealed class X11Keyboard : IKeyboard
	{
		public bool TryQueryModifierLRState(out uint mods, byte[] keymapBuffer = null)
		{
			mods = 0u;

			if (!TryQueryKeymap(out var keymap, keymapBuffer))
				return false;

			var display = Keysharp.Internals.Window.Linux.Proxies.XDisplay.Default;

			for (var keycode = 8; keycode < 256; keycode++)
			{
				if (!KeymapHas(keymap, keycode))
					continue;

				var keysym = (ulong)display.XKeycodeToKeysym(keycode, 0);

				switch (keysym)
				{
					case 0xFFE1: mods |= MOD_LSHIFT; break;
					case 0xFFE2: mods |= MOD_RSHIFT; break;
					case 0xFFE3: mods |= MOD_LCONTROL; break;
					case 0xFFE4: mods |= MOD_RCONTROL; break;
					case 0xFFE9: mods |= MOD_LALT; break;
					case 0xFFEA: mods |= MOD_RALT; break;
					case 0xFFEB: mods |= MOD_LWIN; break;
					case 0xFFEC: mods |= MOD_RWIN; break;
				}
			}

			return true;
		}

		public bool TryQueryKeyStateLogical(uint vk, out bool isDown)
		{
			isDown = false;

			if (vk == 0)
				return false;

			if (!TryQueryKeymap(out var keymap))
				return false;

			var expected = KeyCodes.VkToKeysyms(vk);

			if (expected.Count == 0)
				return false;

			var display = Keysharp.Internals.Window.Linux.Proxies.XDisplay.Default;

			for (var keycode = 8; keycode < 256; keycode++)
			{
				if (!KeymapHas(keymap, keycode))
					continue;

				var keysym = (ulong)display.XKeycodeToKeysym(keycode, 0);

				foreach (var candidate in expected)
				{
					if ((ulong)candidate != keysym)
						continue;

					isDown = true;
					return true;
				}
			}

			return true;
		}

		public bool TryQueryKeyStatePhysical(uint vk, out bool isDown)
			=> TryQueryKeyStateLogical(vk, out isDown);

		public bool TryGetIndicatorStates(out bool capsOn, out bool numOn, out bool scrollOn)
		{
			capsOn = numOn = scrollOn = false;
			var display = Keysharp.Internals.Window.Linux.Proxies.XDisplay.Default.Handle;

			if (display == nint.Zero)
				return false;

			const uint XkbUseCoreKbd = 0x0100;
			const uint XK_Num_Lock = 0xff7f;
			const uint XK_Scroll_Lock = 0xff14;

			if (Keysharp.Internals.Window.Linux.X11.Xlib.XkbGetState(display, XkbUseCoreKbd, out var st) != 0)
				return false;

			capsOn = (st.locked_mods & (byte)Keysharp.Internals.Window.Linux.X11.KeyMasks.LockMask) != 0;

			var numMask = Keysharp.Internals.Window.Linux.X11.Xlib.XkbKeysymToModifiers(display, XK_Num_Lock);
			var scrollMask = Keysharp.Internals.Window.Linux.X11.Xlib.XkbKeysymToModifiers(display, XK_Scroll_Lock);

			numOn = (st.locked_mods & (byte)numMask) != 0;
			scrollOn = (st.locked_mods & (byte)scrollMask) != 0;
			return true;
		}

		private static bool TryQueryKeymap(out byte[] keymap, byte[] keymapBuffer = null)
		{
			keymap = keymapBuffer is { Length: 32 } buffer ? buffer : new byte[32];
			var display = Keysharp.Internals.Window.Linux.Proxies.XDisplay.Default;

			return display.Handle != nint.Zero && display.XQueryKeymap(keymap) != 0;
		}

		private static bool KeymapHas(byte[] keymap, int keycode)
			=> keycode is >= 0 and < 256 && (keymap[keycode >> 3] & (1 << (keycode & 7))) != 0;
	}
#endif

#if OSX
	internal sealed class MacKeyboard : IKeyboard
	{
		private const ulong ShiftKeyMask = 1UL << 17;
		private const ulong ControlKeyMask = 1UL << 18;
		private const ulong AlternateKeyMask = 1UL << 19;
		private const ulong CommandKeyMask = 1UL << 20;
		private const ulong AlphaShiftKeyMask = 1UL << 16;

		public bool TryQueryModifierLRState(out uint mods, byte[] keymapBuffer = null)
		{
			mods = 0u;

			if (!TryGetCurrentModifierFlags(Keysharp.Internals.Input.MacOS.MacNativeInput.kCGEventSourceStateCombinedSessionState, out var flags))
				return false;

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

		public bool TryQueryKeyStateLogical(uint vk, out bool isDown)
			=> TryQueryMacKeyState(vk, Keysharp.Internals.Input.MacOS.MacNativeInput.kCGEventSourceStateCombinedSessionState, useIndicators: true, out isDown);

		public bool TryQueryKeyStatePhysical(uint vk, out bool isDown)
			=> TryQueryMacKeyState(vk, Keysharp.Internals.Input.MacOS.MacNativeInput.kCGEventSourceStateHIDSystemState, useIndicators: false, out isDown);

		public bool TryGetIndicatorStates(out bool capsOn, out bool numOn, out bool scrollOn)
		{
			numOn = false;
			scrollOn = false;

			if (Keysharp.Internals.Input.MacOS.MacCapsLockState.TryGet(out capsOn))
				return true;

			if (TryGetCurrentModifierFlags(Keysharp.Internals.Input.MacOS.MacNativeInput.kCGEventSourceStateCombinedSessionState, out var flags))
			{
				capsOn = (flags & AlphaShiftKeyMask) != 0;
				return true;
			}

			capsOn = false;
			return false;
		}

		private bool TryQueryMacKeyState(uint vk, uint sourceState, bool useIndicators, out bool isDown)
		{
			isDown = false;

			if (vk == 0)
				return false;

			if (TryQueryModifierLRState(sourceState, out var mods))
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

			if (useIndicators && (vk == VK_CAPITAL || vk == VK_NUMLOCK || vk == VK_SCROLL))
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

			if (!KeyCodes.TryMapVkToMacCode(vk, out var macCode))
				return false;

			try
			{
				isDown = Keysharp.Internals.Input.MacOS.MacNativeInput.CGEventSourceKeyState(sourceState, (ushort)macCode);
				return true;
			}
			catch
			{
				isDown = false;
				return false;
			}
		}

		private static bool TryQueryModifierLRState(uint sourceState, out uint mods)
		{
			mods = 0u;

			if (!TryGetCurrentModifierFlags(sourceState, out var flags))
				return false;

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

		private static bool TryGetCurrentModifierFlags(uint sourceState, out ulong flags)
		{
			try
			{
				flags = Keysharp.Internals.Input.MacOS.MacNativeInput.CGEventSourceFlagsState(sourceState);
				return true;
			}
			catch
			{
				flags = 0;
				return false;
			}
		}
	}
#endif
}
