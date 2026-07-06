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

			return TryMapVkToMacCode(vk, out var sc) ? sc : 0;
		}
#endif
	}
}
#endif
