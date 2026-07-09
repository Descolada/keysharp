#if !WINDOWS
namespace Keysharp.Internals.Input.Keyboard
{
	/// <summary>
	/// VK/SC dispatch for the non-Windows backends. On Linux SC is an inputd evdev
	/// code; on macOS SC is the kVK code.
	/// </summary>
	internal static partial class KeyCodes
	{
#if LINUX
		public static uint MapScToVk(uint sc)
		{
			return sc == 0 ? 0 : EvdevToVk(sc);
		}

		public static uint MapVkToSc(uint vk, bool returnSecondary = false)
		{
			return vk == 0 ? 0 : VkToEvdev(vk, returnSecondary);
		}
#endif

#if OSX
		public static uint MapScToVk(uint sc)
		{
			if (sc == 0)
				return 0;

			return TryMapMacCodeToVk(sc, out var vk) ? vk : 0;
		}

		public static uint MapVkToSc(uint vk, bool returnSecondary = false)
		{
			if (vk == 0)
				return 0;

			// Only VK_RETURN maps to two Mac key codes — Return (0x24) and the keypad Enter (0x4C); both resolve
			// back to VK_RETURN (see MacCharMapperProvider). Every other VK has a single code, so no secondary.
			// Mirroring the Windows/Linux mappers, a secondary request for a VK that has none returns 0 — callers
			// use that both as the "this VK maps to two scan codes" test and to fall back to the primary code.
			if (returnSecondary)
				return vk == VirtualKeys.VK_RETURN ? 0x4Cu : 0u;

			return TryMapVkToMacCode(vk, out var sc) ? sc : 0;
		}
#endif
	}
}
#endif
