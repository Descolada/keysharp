#if WINDOWS
using static Keysharp.Internals.Input.Keyboard.ScanCodes;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;
using static Keysharp.Internals.Os.Windows.WindowsAPI;

namespace Keysharp.Internals.Input.Keyboard
{
	/// <summary>
	/// Windows VK ⇄ scan code conversion. SC here is the Windows make code with our 0x100
	/// extended-key flag. Backed by the OS <c>MapVirtualKey</c> API plus a few special cases.
	/// Part of the unified <see cref="KeyCodes"/> facade.
	/// </summary>
	internal static partial class KeyCodes
	{
		public static uint MapScToVk(uint sc)
		{
			// aSC is actually a combination of the last byte of the keyboard make code combined with
			// 0x100 for the extended-key flag.  Although in most cases the flag corresponds to a prefix
			// byte of 0xE0, it seems it's actually set by the KBDEXT flag in the keyboard layout dll
			// (it's hard to find documentation).  A few keys have the KBDEXT flag inverted, which means
			// we can't tell reliably which scan codes really need the 0xE0 prefix, so just handle them
			// as special cases and hope that the flag never varies between layouts.
			// If this approach ever fails for custom layouts, some alternatives are:
			//  - Load the keyboard layout dll manually and check the scan code conversion tables for
			//    the presence of the KBDEXT flag.
			//  - Convert aSC and (aSC ^ 0x100), check the conversion of VK back to SC, and if it
			//    round-trips use that VK instead.
			// However, it seems that neither MSKLC nor KbdEdit provide a means to change the KBDEXT flag.
			// US layout: https://github.com/microsoft/Windows-driver-samples/blob/master/input/layout/kbdus/kbdus.c
			// Keyboard make codes: http://stanislavs.org/helppc/make_codes.html
			// More low-level keyboard details: https://www.win.tue.nl/~aeb/linux/kbd/scancodes-1.html#ss1.5
			switch (sc)
			{
				// RShift doesn't have the 0xE0 prefix but has KBDEXT.  The US layout sample says
				// "Right-hand Shift key must have KBDEXT bit set", so it's probably always set.
				// KbdEdit seems to follow this rule when VK_RSHIFT is assigned to a non-ext key.
				// It's definitely possible to assign RShift a different VK, but 1) it can't be
				// done with MSKLC, and 2) KbdEdit clears the ext flag (so aSC != SC_RSHIFT).
				case RShift:

				// NumLock doesn't have the 0xE0 prefix but has KBDEXT.  Actually pressing the key
				// will produce VK_PAUSE if CTRL is down, but with SC_NUMLOCK rather than SC_PAUSE.
				case NumpadLock:
					// These cases can be handled by adjusting aSC to reflect the fact that these
					// keys don't really have the 0xE0 prefix, and allowing MapVirtualKey() to be
					// called below in case they have been remapped.
					sc &= 0xFF;
					break;

				// Pause actually generates 0xE1,0x1D,0x45, or in other words, E1,LCtrl,NumLock.
				// kbd.h says "We must convert the E1+LCtrl to BREAK, then ignore the Numlock".
				// So 0xE11D maps to and from VK_PAUSE, and 0x45 is "ignored".  However, the hook
				// receives only 0x45, not 0xE11D (which I guess would be truncated to 0x1D/ctrl).
				// The documentation for KbdEdit also indicates the mapping of Pause is "hard-wired":
				// http://www.kbdedit.com/manual/low_level_edit_vk_mappings.html
				case Pause:
					return VK_PAUSE;
			}

			if ((sc & 0x100) != 0) // Our extended-key flag.
			{
				// Since it wasn't handled above, assume the extended-key flag corresponds to the 0xE0
				// prefix byte.  Passing 0xE000 should work on Vista and up, though it appears to be
				// documented only for MapVirtualKeyEx() as at 2019-10-26.  Details can be found in
				// archives of Michael Kaplan's blog (the original blog has been taken down):
				// https://web.archive.org/web/20070219075710/http://blogs.msdn.com/michkap/archive/2006/08/29/729476.aspx
				sc = 0xE000 | (sc & 0xFF);
			}

			return MapVirtualKey(sc, MAPVK_VSC_TO_VK_EX);
		}

		/// <summary>
		/// If caller passes true for aReturnSecondary, the "extended" scan code will be returned for
		/// virtual keys that have two scan codes and two names (if there's only one, callers rely on
		/// zero being returned).  In those cases, the caller may want to know:
		///  a) Whether the hook needs to be used to identify a hotkey defined by name.
		///  b) Whether InputHook should handle the keys by SC in order to differentiate.
		///  c) Whether to retrieve the key's name by SC rather than VK.
		/// In all of those cases, only keys that we've given multiple names matter.
		/// Custom layouts could assign some other VK to multiple SCs, but there would
		/// be no reason (or way) to differentiate them in this context.
		/// </summary>
		public static uint MapVkToSc(uint vk, bool returnSecondary = false)
		{
			// Try to minimize the number mappings done manually because MapVirtualKey is a more reliable
			// way to get the mapping if user has non-standard or custom keyboard layout.
			var sc = 0u;

			switch (vk)
			{
				// MapVirtualKey() returns 0xE11D, but we want the code normally received by the
				// hook (sc045).  See sc_to_vk() for more comments.
				case VK_PAUSE: sc = Pause; break;

				// PrintScreen: MapVirtualKey() returns 0x54, which is SysReq (produced by pressing
				// Alt+PrintScreen, but still maps to VK_SNAPSHOT).  Use sc137 for consistency with
				// what the hook reports for the naked keypress (and therefore what a hotkey is
				// likely to need).
				case VK_SNAPSHOT: sc = PrintScreen; break;

				// See comments in sc_to_vk().
				case VK_NUMLOCK: sc = NumpadLock; break;
			}

			if (sc != 0) // Above found a match.
				return returnSecondary ? 0 : sc; // Callers rely on zero being returned for VKs that don't have secondary SCs.

			if ((sc = MapVirtualKey(vk, MAPVK_VK_TO_VSC_EX)) == 0u)
				return 0; // Indicate "no mapping".

			if ((sc & 0xE000) != 0u) // Prefix byte E0 or E1 (but E1 should only be possible for Pause/Break, which was already handled above).
				sc = 0x0100 | (sc & 0xFF);

			switch (vk)
			{
				// The following virtual keys have more than one physical key, and thus more than one scan code.
				case VK_RETURN:
				case VK_INSERT:
				case VK_DELETE:
				case VK_PRIOR: // PgUp
				case VK_NEXT:  // PgDn
				case VK_HOME:
				case VK_END:
				case VK_UP:
				case VK_DOWN:
				case VK_LEFT:
				case VK_RIGHT:
					// This is likely to be incorrect for custom layouts where aVK is mapped to two SCs
					// that differ in the low byte.  There seems to be no simple way to fix that;
					// the complex ways would be:
					//  - Build our own conversion table by mapping all SCs to VKs (taking care to detect
					//    changes to the current keyboard layout).  Find the second SC that maps to aVK,
					//    or the first one with the 0xE000 flag.  However, there's no guarantee that it
					//    would correspond to NumpadEnter vs. Enter, or Insert vs. NumpadIns, for example.
					//  - Load the keyboard layout dll manually and search the SC-to-VK conversion tables.
					//    What we actually want is to differentiate Numpad keys from their non-Numpad
					//    counter-parts, and we can do that by checking for the KBDNUMPAD flag.
					// Custom layouts might cause these issues:
					//  - If the scan code of the secondary key is changed, the Hotkey control (and
					//    other sections that don't call this function) may return either "scXXX"
					//    or a name inconsistent with the key's current VK (but if it's a custom
					//    layout + standard keyboard, it should match the key's original function).
					//  - The Hotkey control assumes that the HOTKEYF_EXT flag corresponds to the
					//    secondary key, but either/both/neither could be extended on a custom layout.
					//    If it's both/neither, the control would give no way to distinguish.
					return returnSecondary ? sc | 0x0100 : sc; // Below relies on the fact that these cases return early.

				// See "case SC_RSHIFT:" in sc_to_vk() for comments.
				case VK_RSHIFT:
					sc |= 0x0100;
					break;
			}

			// Since above didn't return, if aReturnSecondary==true, return 0 to indicate "no secondary SC for this VK".
			return returnSecondary ? 0 : sc; // Callers rely on zero being returned for VKs that don't have secondary SCs.
		}
	}
}
#endif
