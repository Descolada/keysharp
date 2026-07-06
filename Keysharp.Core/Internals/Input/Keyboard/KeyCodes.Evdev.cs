#if LINUX
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;

namespace Keysharp.Internals.Input.Keyboard
{
	/// <summary>
	/// Windows VK ⇄ Linux evdev (inputd) scan code tables. Fixed mappings used by the
	/// inputd backend. Part of the unified <see cref="KeyCodes"/> facade.
	/// </summary>
	internal static partial class KeyCodes
	{
		internal static uint VkToEvdev(uint vk, bool returnSecondary = false)
		{
			return vk switch
			{
				VK_RETURN => 28u,
				VK_INSERT => 110u,
				VK_DELETE => 111u,
				VK_HOME => 102u,
				VK_END => 107u,
				VK_PRIOR => 104u,
				VK_NEXT => 109u,
				VK_UP => 103u,
				VK_DOWN => 108u,
				VK_LEFT => 105u,
				VK_RIGHT => 106u,
				VK_LCONTROL or VK_CONTROL => 29u,
				VK_RCONTROL => 97u,
				VK_LSHIFT or VK_SHIFT => 42u,
				VK_RSHIFT => 54u,
				VK_LMENU or VK_MENU => 56u,
				VK_RMENU => 100u,
				VK_LWIN => 125u,
				VK_RWIN => 126u,
				// Volume / media / browser / application keys (input-event-codes.h KEY_*)
				VK_VOLUME_MUTE => 113u,         // KEY_MUTE
				VK_VOLUME_DOWN => 114u,         // KEY_VOLUMEDOWN
				VK_VOLUME_UP => 115u,           // KEY_VOLUMEUP
				VK_MEDIA_PLAY_PAUSE => 164u,    // KEY_PLAYPAUSE
				VK_MEDIA_NEXT_TRACK => 163u,    // KEY_NEXTSONG
				VK_MEDIA_PREV_TRACK => 165u,    // KEY_PREVIOUSSONG
				VK_MEDIA_STOP => 166u,          // KEY_STOPCD
				VK_BROWSER_STOP => 128u,        // KEY_STOP
				VK_BROWSER_BACK => 158u,        // KEY_BACK
				VK_BROWSER_FORWARD => 159u,     // KEY_FORWARD
				VK_BROWSER_REFRESH => 173u,     // KEY_REFRESH
				VK_BROWSER_SEARCH => 217u,      // KEY_SEARCH
				VK_BROWSER_FAVORITES => 156u,   // KEY_BOOKMARKS
				VK_BROWSER_HOME => 172u,        // KEY_HOMEPAGE
				VK_LAUNCH_MAIL => 155u,         // KEY_MAIL
				VK_LAUNCH_MEDIA_SELECT => 226u, // KEY_MEDIA
				VK_LAUNCH_APP1 => 148u,         // KEY_PROG1
				VK_LAUNCH_APP2 => 149u,         // KEY_PROG2
				VK_HELP => 138u,                // KEY_HELP
				VK_SLEEP => 142u,               // KEY_SLEEP
				VK_CANCEL => 223u,              // KEY_CANCEL
\t\t\t\t_ => 0u
			};
		}

		internal static uint EvdevToVk(uint sc)
		{
			return sc switch
			{
				28u or 96u => VK_RETURN,
				110u => VK_INSERT,
				111u => VK_DELETE,
				102u => VK_HOME,
				107u => VK_END,
				104u => VK_PRIOR,
				109u => VK_NEXT,
				103u => VK_UP,
				108u => VK_DOWN,
				105u => VK_LEFT,
				106u => VK_RIGHT,
				29u => VK_LCONTROL,
				97u => VK_RCONTROL,
				42u => VK_LSHIFT,
				54u => VK_RSHIFT,
				56u => VK_LMENU,
				100u => VK_RMENU,
				125u => VK_LWIN,
				126u => VK_RWIN,
				// Volume / media / browser / application keys (input-event-codes.h KEY_*)
				113u => VK_VOLUME_MUTE,
				114u => VK_VOLUME_DOWN,
				115u => VK_VOLUME_UP,
				164u => VK_MEDIA_PLAY_PAUSE,
				163u => VK_MEDIA_NEXT_TRACK,
				165u => VK_MEDIA_PREV_TRACK,
				166u => VK_MEDIA_STOP,
				128u => VK_BROWSER_STOP,
				158u => VK_BROWSER_BACK,
				159u => VK_BROWSER_FORWARD,
				173u => VK_BROWSER_REFRESH,
				217u => VK_BROWSER_SEARCH,
				156u => VK_BROWSER_FAVORITES,
				172u => VK_BROWSER_HOME,
				155u => VK_LAUNCH_MAIL,
				226u => VK_LAUNCH_MEDIA_SELECT,
				148u => VK_LAUNCH_APP1,
				149u => VK_LAUNCH_APP2,
				138u => VK_HELP,
				142u => VK_SLEEP,
				223u => VK_CANCEL,
				_ => 0u
			};
		}
	}
}
#endif
