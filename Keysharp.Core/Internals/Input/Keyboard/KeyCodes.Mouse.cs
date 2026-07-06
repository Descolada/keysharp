#if !WINDOWS
using Keysharp.Internals.Input.Hooks;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;

namespace Keysharp.Internals.Input.Keyboard
{
	internal static partial class KeyCodes
	{
		public static MouseButton VkToMouseButton(uint vk)
		{
			return vk switch
			{
				VK_LBUTTON  => MouseButton.Button1,
				VK_RBUTTON  => MouseButton.Button2,
				VK_MBUTTON  => MouseButton.Button3,
				VK_XBUTTON1 => MouseButton.Button4,
				VK_XBUTTON2 => MouseButton.Button5,
				_           => MouseButton.NoButton
			};
		}
	}
}
#endif
