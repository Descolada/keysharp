using static Keysharp.Core.Common.Keyboard.KeyboardUtils;
using static Keysharp.Core.Common.Keyboard.ScanCodes;
using static Keysharp.Core.Common.Keyboard.VirtualKeys;
using static Keysharp.Core.Common.Keyboard.KeyboardMouseSender;

namespace Keysharp.Core.Common.Threading
{
	internal abstract class HookThread//Fill in base stuff here later, but this serves as the thread which attaches/detaches the keyboard hooks.
	{
		internal const uint END_KEY_ENABLED = END_KEY_WITH_SHIFT | END_KEY_WITHOUT_SHIFT;
		internal const uint END_KEY_WITH_SHIFT = 0x01;
		internal const uint END_KEY_WITHOUT_SHIFT = 0x02;
		internal const uint INPUT_KEY_DOWN_SUPPRESSED = 0x80;
		internal const uint INPUT_KEY_IGNORE_TEXT = 0x10;
		internal const uint INPUT_KEY_IS_TEXT = 0x40;
		internal const uint INPUT_KEY_NOTIFY = 0x20;
		internal const uint INPUT_KEY_OPTION_MASK = 0x3F;
		internal const uint INPUT_KEY_SUPPRESS = 0x04;
		internal const uint INPUT_KEY_VISIBILITY_MASK = INPUT_KEY_SUPPRESS | INPUT_KEY_VISIBLE;
		internal const uint INPUT_KEY_VISIBLE = 0x08;
		internal const int SC_ARRAY_COUNT = SC_MAX + 1;
		internal const int SC_MAX = 0x1FF;
		internal const int VK_ARRAY_COUNT = VK_MAX + 1;
		internal const int VK_MAX = 0xFF;
		internal const int KSCM_SIZE = (int)((MODLR_MAX + 1) * SC_ARRAY_COUNT);
		internal const int KVKM_SIZE = (int)((MODLR_MAX + 1) * VK_ARRAY_COUNT);

		// Currently Windows-only constants and duplicated in WindowsAPI.cs, but might be useful on other platforms as well
		internal const int WM_HOTKEY = 0x0312;
		internal const int WM_QUIT = 0x0012;

		internal readonly Channel<object> channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
		{
			SingleReader = true
		});

		internal static string MutexName = "Keysharp";
		internal Mutex keybdMutex = null, mouseMutex = null;
		internal string KeybdMutexName = $"{MutexName} Keybd";
		internal Dictionary<string, uint> keyToSc = null;
		internal Dictionary<string, uint>.AlternateLookup<ReadOnlySpan<char>> keyToScAlt;
		internal Dictionary<string, uint> keyToVk = null;
		internal Dictionary<string, uint>.AlternateLookup<ReadOnlySpan<char>> keyToVkAlt;
		internal string MouseMutexName = $"{MutexName} Mouse";
		internal Dictionary<uint, string> vkToKey = [];
		internal bool blockWinKeys = false;
		internal nint hsHwnd = 0;
		internal KeyboardMouseSender kbdMsSender = null;
		internal byte[] physicalKeyState = new byte[VK_ARRAY_COUNT];

		internal bool pendingDeadKeyInvisible;
		internal readonly List<DeadKeyRecord> pendingDeadKeys = [];

		internal class DeadKeyRecord
		{
			internal uint caps;
			internal uint modLR;
			internal uint sc;
			internal uint vk;
		}

		// The prefix key that's currently down (i.e. in effect).
		// It's tracked this way, rather than as a count of the number of prefixes currently down, out of
		// concern that such a count might accidentally wind up above zero (due to a key-up being missed somehow)
		// and never come back down, thus penalizing performance until the program is restarted:
		internal KeyType prefixKey = null;

		protected internal PlatformManagerBase mgr;
		// Whether the alt-tab menu was shown by an AltTab hotkey or alt-tab was detected
		// by the hook.  This might be inaccurate if the menu was displayed before the hook
		// was installed or the keys weren't detected because of UIPI.  If this turns out to
		// be a problem, the accuracy could be improved by additional checks with FindWindow(),
		// keeping in mind that there are at least 3 different window classes to check,
		// depending on OS and the "AltTabSettings" registry value.
		protected internal bool altTabMenuIsVisible = false;
		protected internal Task<Task> channelReadThread;
		protected internal uint channelThreadID = 0u;

		// Whether to disguise the next up-event for lwin/rwin to suppress Start Menu.
		// There is only one variable because even if multiple modifiers are pressed
		// simultaneously and they do not cancel each other out, disguising one will
		// have the effect of disguising all (with the exception that key-repeat can
		// reset LWin/RWin after the other modifier is released, so this variable
		// should not be reset until all Win keys are released).
		// These are made global, rather than static inside the hook function, so that
		// we can ensure they are initialized by the keyboard init function every
		// time it's called (currently it can be only called once):
		protected internal bool disguiseNextMenu = false;
		protected internal bool hookSynced = false;
		protected internal List<uint> hotkeyUp = new (256);
		protected internal nint kbdHook = 0;
		protected internal KeyHistory keyHistory = new ();
		protected internal KeyType[] ksc;
		protected internal uint[] kscm;
		protected internal KeyType[] kvk;
		protected internal uint[] kvkm;
		protected internal nint mouseHook = 0;
		protected internal bool undisguisedMenuInEffect = false;
		protected volatile bool running;

		internal HookThread()
		{
			mgr = Script.TheScript.PlatformProvider.Manager;
			EnsureKeyLookups();
		}

		private void EnsureKeyLookups()
		{
			if (keyToSc == null)
			{
				keyToSc = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
				{
					{"NumpadEnter", NumpadEnter},
					{"Delete", Delete},
					{"Del", Delete},
					{"Insert", Insert},
					{"Ins", Insert},
					//{"Clear", SC_CLEAR},  // Seems unnecessary because there is no counterpart to the Numpad5 clear key?
					{"Up", Up},
					{"Down", Down},
					{"Left", Left},
					{"Right", Right},
					{"Home", Home},
					{"End", End},
					{"PgUp", PgUp},
					{"PgDn", PgDn}
				};
				keyToScAlt = keyToSc.GetAlternateLookup<ReadOnlySpan<char>>();
			}

			if (keyToVk == null)
			{
				// Prefer the more common/long form first so VKtoKeyName() has stable results.
				keyToVk = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
				{
					{"Numpad0", VK_NUMPAD0},
					{"Numpad1", VK_NUMPAD1},
					{"Numpad2", VK_NUMPAD2},
					{"Numpad3", VK_NUMPAD3},
					{"Numpad4", VK_NUMPAD4},
					{"Numpad5", VK_NUMPAD5},
					{"Numpad6", VK_NUMPAD6},
					{"Numpad7", VK_NUMPAD7},
					{"Numpad8", VK_NUMPAD8},
					{"Numpad9", VK_NUMPAD9},
					{"NumpadMult", VK_MULTIPLY},
					{"NumpadDiv", VK_DIVIDE},
					{"NumpadAdd", VK_ADD},
					{"NumpadSub", VK_SUBTRACT},
					{"NumpadDot", VK_DECIMAL},
					{"Numlock", VK_NUMLOCK},
					{"ScrollLock", VK_SCROLL},
					{"CapsLock", VK_CAPITAL},
					{"Escape", VK_ESCAPE}, // So that VKtoKeyName() delivers consistent results, always have the preferred name first.
					{"Esc", VK_ESCAPE},
					{"Tab", VK_TAB},
					{"Space", VK_SPACE},
					{"Backspace", VK_BACK}, // So that VKtoKeyName() delivers consistent results, always have the preferred name first.
					{"BS", VK_BACK},
					// These keys each have a counterpart on the number pad with the same VK.  Use the VK for these,
					// since they are probably more likely to be assigned to hotkeys (thus minimizing the use of the
					// keyboard hook, and use the scan code (SC) for their counterparts.  UPDATE: To support handling
					// these keys with the hook (i.e. the sc_takes_precedence flag in the hook), do them by scan code
					// instead.  This allows Numpad keys such as Numpad7 to be differentiated from NumpadHome, which
					// would otherwise be impossible since both of them share the same scan code (i.e. if the
					// sc_takes_precedence flag is set for the scan code of NumpadHome, that will effectively prevent
					// the hook from telling the difference between it and Numpad7 since the hook is currently set
					// to handle an incoming key by either vk or sc, but not both.
					// For VKs with multiple SCs, such as VK_RETURN, the keyboard hook is made mandatory unless the
					// user specifies the VK by number.  This ensures that Enter:: and NumpadEnter::, for example,
					// only fire when the appropriate key is pressed.
					{"Enter", VK_RETURN}, // So that VKtoKeyName() delivers consistent results, always have the preferred name first.
					// See g_key_to_sc for why these Numpad keys are handled here:
					{"NumpadDel", VK_DELETE},
					{"NumpadIns", VK_INSERT},
					{"NumpadClear", VK_CLEAR}, // same physical key as Numpad5 on most keyboards?
					{"NumpadUp", VK_UP},
					{"NumpadDown", VK_DOWN},
					{"NumpadLeft", VK_LEFT},
					{"NumpadRight", VK_RIGHT},
					{"NumpadHome", VK_HOME},
					{"NumpadEnd", VK_END},
					{"NumpadPgUp", VK_PRIOR},
					{"NumpadPgDn", VK_NEXT},
					{"Up", VK_UP},
					{"Down", VK_DOWN},
					{"Left", VK_LEFT},
					{"Right", VK_RIGHT},
					{"Home", VK_HOME},
					{"End", VK_END},
					{"PgUp", VK_PRIOR},
					{"PageUp", VK_PRIOR},
					{"PgDn", VK_NEXT},
					{"PageDown", VK_NEXT},
					{"PrintScreen", VK_SNAPSHOT},
					{"CtrlBreak", VK_CANCEL}, // Might want to verify this, and whether it has any peculiarities.
					{"Pause", VK_PAUSE}, // So that VKtoKeyName() delivers consistent results, always have the preferred name first.
					{"Help", VK_HELP}, // VK_HELP is probably not the extended HELP key.  Not sure what this one is.
					{"Sleep", VK_SLEEP},
					{"AppsKey", VK_APPS},
					{"Apps", VK_APPS},
					{"ContextMenu", VK_APPS},
					{"LControl", VK_LCONTROL}, // So that VKtoKeyName() delivers consistent results, always have the preferred name first.
					{"RControl", VK_RCONTROL}, // So that VKtoKeyName() delivers consistent results, always have the preferred name first.
					{"LCtrl", VK_LCONTROL}, // Abbreviated versions of the above.
					{"RCtrl", VK_RCONTROL},
					{"LShift", VK_LSHIFT},
					{"RShift", VK_RSHIFT},
					{"LAlt", VK_LMENU},
					{"RAlt", VK_RMENU},
					// These two are always left/right centric and I think their vk's are always supported by the various
					// Windows API calls, unlike VK_RSHIFT, etc. (which are seldom supported):
					{"LWin", VK_LWIN},
					{"RWin", VK_RWIN},
					{"Control", VK_CONTROL}, // So that VKtoKeyName() delivers consistent results, always have the preferred name first.
					{"Ctrl", VK_CONTROL}, // An alternate for convenience.
					{"Alt", VK_MENU},
					{"Shift", VK_SHIFT},
					{"Spacebar", VK_SPACE},
					{"F1", VK_F1}, {"F2", VK_F2}, {"F3", VK_F3}, {"F4", VK_F4}, {"F5", VK_F5}, {"F6", VK_F6},
					{"F7", VK_F7}, {"F8", VK_F8}, {"F9", VK_F9}, {"F10", VK_F10}, {"F11", VK_F11}, {"F12", VK_F12},
					{"F13", VK_F13}, {"F14", VK_F14}, {"F15", VK_F15}, {"F16", VK_F16}, {"F17", VK_F17}, {"F18", VK_F18},
					{"F19", VK_F19}, {"F20", VK_F20}, {"F21", VK_F21}, {"F22", VK_F22}, {"F23", VK_F23}, {"F24", VK_F24},
					// Mouse buttons:
					{"LButton", VK_LBUTTON},
					{"RButton", VK_RBUTTON},
					{"MButton", VK_MBUTTON},
					{"XButton1", VK_XBUTTON1},
					{"XButton2", VK_XBUTTON2},
					// Custom/fake VKs for use by the mouse hook:
					{"Click", VK_LBUTTON},
					{"WheelDown", VK_WHEEL_DOWN},
					{"WheelUp", VK_WHEEL_UP},
					{"WheelLeft", VK_WHEEL_LEFT},
					{"WheelRight", VK_WHEEL_RIGHT},
					{"Browser_Back", VK_BROWSER_BACK},
					{"Browser_Forward", VK_BROWSER_FORWARD},
					{"Browser_Refresh", VK_BROWSER_REFRESH},
					{"Browser_Stop", VK_BROWSER_STOP},
					{"Browser_Search", VK_BROWSER_SEARCH},
					{"Browser_Favorites", VK_BROWSER_FAVORITES},
					{"Browser_Home", VK_BROWSER_HOME},
					{"Volume_Mute", VK_VOLUME_MUTE},
					{"Volume_Down", VK_VOLUME_DOWN},
					{"Volume_Up", VK_VOLUME_UP},
					{"Media_Next", VK_MEDIA_NEXT_TRACK},
					{"Media_Prev", VK_MEDIA_PREV_TRACK},
					{"Media_Stop", VK_MEDIA_STOP},
					{"Media_Play_Pause", VK_MEDIA_PLAY_PAUSE},
					{"Launch_Mail", VK_LAUNCH_MAIL},
					{"Launch_Media", VK_LAUNCH_MEDIA_SELECT},
					{"Launch_App1", VK_LAUNCH_APP1},
					{"Launch_App2", VK_LAUNCH_APP2}
				};
				keyToVkAlt = keyToVk.GetAlternateLookup<ReadOnlySpan<char>>();

				var foundEsc = false;
				foreach (var kv in keyToVk)
				{
					if (foundEsc)
						vkToKey[kv.Value] = kv.Key;
					else if (kv.Key == "Esc")
						foundEsc = true; // prefer Escape over Esc
					else
						vkToKey[kv.Value] = kv.Key;
				}
			}
		}

		public abstract void SimulateKeyPress(uint key);

		internal abstract void AddRemoveHooks(HookType hooksToBeActive, bool changeIsTemporary = false);

		/// <summary>
		/// Caller must verify that aWhichHook and aWhichHookAlways accurately reflect the hooks that should
		/// be active when we return.  For example, the caller must have already taken into account which
		/// hotkeys/hotstrings are suspended, disabled, etc.
		///
		/// Caller should always be the main thread, never the hook thread.
		/// One reason is that this function isn't thread-safe.  Another is that new/delete/malloc/free
		/// themselves might not be thread-safe when the single-threaded CRT libraries are in effect
		/// (not using multi-threaded libraries due to a 3.5 KB increase in compressed code size).
		///
		/// The input params are unnecessary because could just access directly by using Hotkey::shk[].
		/// But aHK is a little more concise.
		/// aWhichHookAlways was added to force the hooks to be installed (or stay installed) in the case
		/// of #InstallKeybdHook and #InstallMouseHook.  This is so that these two commands will always
		/// still be in effect even if hotkeys are suspended, so that key history can still be monitored via
		/// the hooks.
		/// Returns the set of hooks that are active after processing is complete.
		/// </summary>
		internal virtual void ChangeHookState(List<HotkeyDefinition> hks, HookType whichHook, HookType whichHookAlways)
		{
			// Determine the set of hooks that should be activated or deactivated.
			var hooksToBeActive = whichHook | whichHookAlways; // Bitwise union.
			var script = Script.TheScript;

			if (hooksToBeActive == 0) // No need to check any further in this case.  Just remove all hooks.
			{
				AddRemoveHooks(0); // Remove all hooks.
				return;
			}

			// Even if hooks_to_be_active indicates no change to hook status, we still need to continue in case
			// this is a suspend or unsuspend operation.  In both of those cases, though the hook(s)
			// may already be active, the hotkey configuration probably needs to be updated.
			// Related: Even if aHK_count is zero, still want to install the hook(s) whenever
			// aWhichHookAlways specifies that they should be.  This is done so that the
			// #InstallKeybdHook and #InstallMouseHook directives can have the hooks installed just
			// for use with something such as the KeyHistory feature, or for Hotstrings, Numlock AlwaysOn,
			// the Input command, and possibly others.

			// Now we know that at least one of the hooks is a candidate for activation.
			// Set up the arrays process all of the hook hotkeys even if the corresponding hook won't
			// become active (which should only happen if g_IsSuspended is true
			// and it turns out there are no suspend-hotkeys that are handled by the hook).

			// These arrays are dynamically allocated so that memory is conserved in cases when
			// the user doesn't need the hook at all (i.e. just normal registered hotkeys).
			// This is a waste of memory if there are no hook hotkeys, but currently the operation
			// of the hook relies upon these being allocated, even if the arrays are all clean
			// slates with nothing in them (it could check if the arrays are NULL but then the
			// performance would be slightly worse for the "average" script).  Presumably, the
			// caller is requesting the keyboard hook with zero hotkeys to support the forcing
			// of Num/Caps/ScrollLock always on or off (a fairly rare situation, probably):
			if (kvk == null || kvk.Length == 0)  // Since it's an initialized global, this indicates that all 4 objects are not yet allocated.
			{
				var kbd = script.KeyboardData;
				kvk = new KeyType[VK_ARRAY_COUNT];
				ksc = new KeyType[SC_ARRAY_COUNT];

				for (var i = 0u; i < kvk.Length; i++)
					kvk[i] = new KeyType(i);

				for (var i = 0u; i < ksc.Length; i++)
					ksc[i] = new KeyType(i);

				kvkm = new uint[KVKM_SIZE];
				kscm = new uint[KSCM_SIZE];
				hotkeyUp = new List<uint>(hks.Count);

				for (var i = 0; i < hks.Count; i++)
					hotkeyUp.Add(0);

				// Below is also a one-time-only init:
				// This attribute is exists for performance reasons (avoids a function call in the hook
				// procedure to determine this value):
				kvk[VK_CONTROL].asModifiersLR = MOD_LCONTROL | MOD_RCONTROL;
				kvk[VK_LCONTROL].asModifiersLR = MOD_LCONTROL;
				kvk[VK_RCONTROL].asModifiersLR = MOD_RCONTROL;
				kvk[VK_MENU].asModifiersLR = MOD_LALT | MOD_RALT;
				kvk[VK_LMENU].asModifiersLR = MOD_LALT;
				kvk[VK_RMENU].asModifiersLR = MOD_RALT;
				kvk[VK_SHIFT].asModifiersLR = MOD_LSHIFT | MOD_RSHIFT;
				kvk[VK_LSHIFT].asModifiersLR = MOD_LSHIFT;
				kvk[VK_RSHIFT].asModifiersLR = MOD_RSHIFT;
				kvk[VK_LWIN].asModifiersLR = MOD_LWIN;
				kvk[VK_RWIN].asModifiersLR = MOD_RWIN;
				// This is a bit iffy because it's far from certain that these particular scan codes
				// are really modifier keys on anything but a standard English keyboard.  However,
				// long years of use haven't shown this to be a problem, and there are certainly other
				// parts of the code that do not support custom layouts remapping the modifier keys.
				ksc[LControl].asModifiersLR = MOD_LCONTROL;
				ksc[RControl].asModifiersLR = MOD_RCONTROL;
				ksc[LAlt].asModifiersLR = MOD_LALT;
				ksc[RAlt].asModifiersLR = MOD_RALT;
				ksc[LShift].asModifiersLR = MOD_LSHIFT;
				ksc[RShift].asModifiersLR = MOD_RSHIFT;
				ksc[LWin].asModifiersLR = MOD_LWIN;
				ksc[RWin].asModifiersLR = MOD_RWIN;
				// Use the address rather than the value, so that if the global var's value
				// changes during runtime, ours will too:
				kvk[VK_SCROLL].forceToggle = kbd.toggleStates;
				kvk[VK_CAPITAL].forceToggle = kbd.toggleStates;
				kvk[VK_NUMLOCK].forceToggle = kbd.toggleStates;
			}

			// Init only those attributes which reflect the hotkey's definition, not those that reflect
			// the key's current status (since those are initialized only if the hook state is changing
			// from OFF to ON (later below):

			foreach (var k in kvk)
				k.ResetKeyTypeAttrib();

			foreach (var k in ksc)
				k.ResetKeyTypeAttrib();// Note: ksc not kvk.

			// Indicate here which scan codes should override their virtual keys:
			foreach (var kv in keyToSc)
				if (kv.Value > 0 && kv.Value <= ksc.Length)
					ksc[kv.Value].scTakesPrecedence = true;

			// These have to be initialized with element value INVALID.
			System.Array.Fill(kvkm, HotkeyDefinition.HOTKEY_ID_INVALID);
			System.Array.Fill(kscm, HotkeyDefinition.HOTKEY_ID_INVALID);

			for (var i = 0; i < hotkeyUp.Count; i++)
				hotkeyUp[i] = HotkeyDefinition.HOTKEY_ID_INVALID;

			KeyType thisKey;
			var shk = script.HotkeyData.shk;
			var hkSorted = new List<HkSortedType>(shk.Count);

			for (var i = 0; i < hks.Count; ++i)
			{
				var hk = hks[i];

				// If it's not a hook hotkey (e.g. it was already registered with RegisterHotkey() or it's a joystick
				// hotkey) don't process it here.  Similarly, if g_IsSuspended is true, we won't include it unless it's
				// exempt from suspension:
				if (!HotkeyDefinition.HK_TYPE_IS_HOOK(hk.type)
						|| (A_IsSuspended && !hk.IsExemptFromSuspend())
						|| hk.IsCompletelyDisabled()) // Listed last for short-circuit performance.
					continue;

				// Rule out the possibility of obnoxious values right away, preventing array-out-of bounds, etc.:
				if ((hk.vk == 0 && hk.sc == 0) || hk.vk > VK_MAX || hk.sc > SC_MAX)
					continue;

				if (hk.vk == 0)
				{
					// scan codes don't need something like the switch stmt below because they can't be neutral.
					// In other words, there's no scan code equivalent for something like VK_CONTROL.
					// In addition, SC_LCONTROL, for example, doesn't also need to change the kvk array
					// for VK_LCONTROL because the hook knows to give the scan code precedence, and thus
					// look it up only in the ksc array in that case.
					thisKey = ksc[hk.sc];
					// For some scan codes this was already set above.  But to support explicit scan code hotkeys,
					// such as "SC102::MsgBox", make sure it's set for every hotkey that uses an explicit scan code.
					thisKey.scTakesPrecedence = true;
				}
				else
				{
					thisKey = kvk[hk.vk];

					// Keys that have a neutral as well as a left/right counterpart must be
					// fully initialized since the hook can receive the left, the right, or
					// the neutral (neutral only if another app calls KeyEvent(), probably).
					// There are several other switch stmts in this function like the below
					// that serve a similar purpose.  The alternative to doing all these
					// switch stmts is to always translate left/right vk's (whose sc's don't
					// take precedence) in the KeyboardProc() itself.  But that would add
					// the overhead of a switch stmt to *every* keypress ever made on the
					// system, so it seems better to set up everything correctly here since
					// this init section is only done once.
					switch (hk.vk)
					{
						case VK_MENU:
							// It's not strictly necessary to init all of these, since the
							// hook currently never handles VK_RMENU, for example, by its
							// vk (it uses sc instead).  But it's safest to do all of them
							// in case future changes ever ruin that assumption:
							kvk[VK_LMENU].usedAsSuffix = true;
							kvk[VK_RMENU].usedAsSuffix = true;
							ksc[LAlt].usedAsSuffix = true;
							ksc[RAlt].usedAsSuffix = true;

							if (hk.keyUp) // Fix for v1.1.07.03: Set only if true in case there was already an "up" hotkey.
							{
								kvk[VK_LMENU].usedAsKeyUp = true;
								kvk[VK_RMENU].usedAsKeyUp = true;
								ksc[LAlt].usedAsKeyUp = true;
								ksc[RAlt].usedAsKeyUp = true;
							}

							break;

						case VK_SHIFT:
							// The neutral key itself is also set to be a suffix further below.
							kvk[VK_LSHIFT].usedAsSuffix = true;
							kvk[VK_RSHIFT].usedAsSuffix = true;
							ksc[LShift].usedAsSuffix = true;
							ksc[RShift].usedAsSuffix = true;

							if (hk.keyUp) // Fix for v1.1.07.03: Set only if true in case there was already an "up" hotkey.
							{
								kvk[VK_LSHIFT].usedAsKeyUp = true;
								kvk[VK_RSHIFT].usedAsKeyUp = true;
								ksc[LShift].usedAsKeyUp = true;
								ksc[RShift].usedAsKeyUp = true;
							}

							break;

						case VK_CONTROL:
							kvk[VK_LCONTROL].usedAsSuffix = true;
							kvk[VK_RCONTROL].usedAsSuffix = true;
							ksc[LControl].usedAsSuffix = true;
							ksc[RControl].usedAsSuffix = true;

							if (hk.keyUp) // Fix for v1.1.07.03: Set only if true in case there was already an "up" hotkey.
							{
								kvk[VK_LCONTROL].usedAsKeyUp = true;
								kvk[VK_RCONTROL].usedAsKeyUp = true;
								ksc[LControl].usedAsKeyUp = true;
								ksc[RControl].usedAsKeyUp = true;
							}

							break;
							// Later might want to add cases for VK_LCONTROL and such, but for right now,
							// these keys should never come up since they're done by scan code?
					}
				}

				thisKey.usedAsSuffix = true;
				var hotkeyIdWithFlags = hk.id;

				if (hk.keyUp)
				{
					thisKey.usedAsKeyUp = true;
					hotkeyIdWithFlags |= HotkeyDefinition.HOTKEY_KEY_UP;
				}

				var hkIsCustomCombo = hk.modifierVK != 0 || hk.modifierSC != 0;

				// If this is a naked (unmodified) modifier key, make it a prefix if it ever modifies any
				// other hotkey.  This processing might be later combined with the hotkeys activation function
				// to eliminate redundancy / improve efficiency, but then that function would probably need to
				// init everything else here as well:
				if (thisKey.asModifiersLR != 0 && hk.modifiersConsolidatedLR == 0 && !hkIsCustomCombo
						&& (hk.noSuppress & HotkeyDefinition.AT_LEAST_ONE_VARIANT_HAS_TILDE) == 0) // v1.0.45.02: ~Alt, ~Control, etc. should fire upon press-down, not release (broken by 1.0.44's PREFIX_FORCED, but I think it was probably broken in pre-1.0.41 too).
					SetModifierAsPrefix(hk.vk, hk.sc);

				if (hkIsCustomCombo)
				{
					if (hk.modifierVK != 0)
					{
						if (kvk[hk.modifierVK].asModifiersLR != 0)
							// The hotkey's ModifierVK is itself a modifier.
							SetModifierAsPrefix(hk.modifierVK, 0, true);
						else
						{
							kvk[hk.modifierVK].usedAsPrefix = KeyType.PREFIX_ACTUAL;

							if ((hk.noSuppress & HotkeyDefinition.NO_SUPPRESS_PREFIX) != 0)
								kvk[hk.modifierVK].noSuppress |= HotkeyDefinition.AT_LEAST_ONE_COMBO_HAS_TILDE;
						}
					}
					else //if (hk.mModifierSC)
					{
						if (ksc[hk.modifierSC].asModifiersLR != 0)  // Fixed for v1.0.35.13 (used to be kvk vs. ksc).
							// The hotkey's ModifierSC is itself a modifier.
							SetModifierAsPrefix(0, hk.modifierSC, true);
						else
						{
							ksc[hk.modifierSC].usedAsPrefix = KeyType.PREFIX_ACTUAL;

							if ((hk.noSuppress & HotkeyDefinition.NO_SUPPRESS_PREFIX) != 0)
								ksc[hk.modifierSC].noSuppress |= HotkeyDefinition.AT_LEAST_ONE_COMBO_HAS_TILDE;

							// For some scan codes this was already set above.  But to support explicit scan code prefixes,
							// such as "SC118 & SC122::MsgBox", make sure it's set for every prefix that uses an explicit
							// scan code:
							ksc[hk.modifierSC].scTakesPrecedence = true;
						}
					}

					// Insert this hotkey at the front of the linked list of hotkeys which use this suffix key.
					hk.nextHotkey = thisKey.firstHotkey;
					thisKey.firstHotkey = hk.id;
					continue;
				}

				// At this point, since the above didn't "continue", this hotkey is one without a ModifierVK/SC.
				// Put it into a temporary array, which will be later sorted:
				var hkst = new HkSortedType
				{
					idWithFlags = hk.hookAction != 0 ? hk.hookAction : hotkeyIdWithFlags,
					vk = hk.vk,
					sc = hk.sc,
					modifiers = hk.modifiers,
					modifiersLR = hk.modifiersLR,
					allowExtraModifiers = hk.allowExtraModifiers
				};
				hkSorted.Add(hkst);
			}

			if (hkSorted.Count != 0)
			{
				// It's necessary to get them into this order to avoid problems that would be caused by
				// AllowExtraModifiers:
				hkSorted.Sort(HkSortedType.SortMostGeneralBeforeLeast);
				// For each hotkey without a ModifierVK/SC (which override normal modifiers), expand its modifiers and
				// modifiersLR into its column in the kvkm or kscm arrays.
				uint modifiers, modifiersMerged;
				uint modifiersLRExcluded;
				uint modifiersLR;  // Don't make this modLR_type to avoid integer overflow, since it's a loop-counter.
				bool prevHkIsKeyUp, thisHkIsKeyUp;
				int prevHkId, thisHkId;

				for (var i = 0; i < hkSorted.Count; ++i)
				{
					var thisHk = hkSorted[i];
					thisHkIsKeyUp = (thisHk.idWithFlags & HotkeyDefinition.HOTKEY_KEY_UP) != 0;
					thisHkId = (int)thisHk.idWithFlags & HotkeyDefinition.HOTKEY_ID_MASK;

					if (thisHkId <= HotkeyDefinition.HOTKEY_ID_MAX) // It's a valid ID and not an ALT_TAB action.
					{
						// Insert this hotkey at the front of the list of hotkeys that use this suffix key.
						// This enables fallback between overlapping hotkeys, such as LCtrl & a, <^+a, ^+a.
						thisKey = thisHk.vk != 0 ? kvk[thisHk.vk] : ksc[thisHk.sc];
						// Insert after any custom combos.
						ref var first = ref thisKey.firstHotkey;

						while (first != HotkeyDefinition.HOTKEY_ID_INVALID && (hks[(int)first].modifierVK != 0 || hks[(int)first].modifierSC != 0))
							first = ref hks[(int)first].nextHotkey;

						hks[thisHkId].nextHotkey = first;
						first = (uint)thisHkId;
					}

					modifiersMerged = thisHk.modifiers;

					if (thisHk.modifiersLR != 0)
						modifiersMerged |= ConvertModifiersLR(thisHk.modifiersLR);

					// Fixed for v1.1.27.00: Calculate the modifiersLR bits which are NOT allowed to be set.
					// This fixes <^A erroneously taking over <>^A, and reduces the work that must be done
					// on each iteration of the loop below.
					modifiersLRExcluded = thisHk.allowExtraModifiers ? 0
										  : ~(thisHk.modifiersLR | ConvertModifiers(thisHk.modifiers));

					for (modifiersLR = 0; modifiersLR <= MODLR_MAX; ++modifiersLR)  // For each possible LR value.
					{
						if ((modifiersLR & modifiersLRExcluded) != 0) // Checked first to avoid the ConvertModifiersLR call in many cases.
							continue;

						modifiers = ConvertModifiersLR(modifiersLR);

						// Below is true if modifiersLR is a superset of i's modifier value.  In other words,
						// modifiersLR has the minimum required keys.  It may also have some extraneous keys,
						// but only if they were not excluded by the check above, in which case they are allowed.
						if (modifiersMerged != (modifiers & modifiersMerged))
							continue;

						// In addition to the above, modifiersLR must also have the *specific* left or right keys
						// found in i's modifiersLR.  In other words, i's modifiersLR must be a perfect subset
						// of modifiersLR:
						if (thisHk.modifiersLR != 0) // make sure that any more specific left/rights are also present.
							if (thisHk.modifiersLR != (modifiersLR & thisHk.modifiersLR))
								continue;

						// scan codes don't need the switch() stmt below because, for example,
						// the hook knows to look up left-control by only SC_LCONTROL, not VK_LCONTROL.
						var doCascade = thisHk.vk != 0;
						// If above didn't "continue", modifiersLR is a valid hotkey combination so set it as such:
						ref var itsTableEntry = ref (thisHk.vk != 0 ? ref Kvkm(modifiersLR, thisHk.vk) : ref Kscm(modifiersLR, thisHk.sc));

						if (itsTableEntry == HotkeyDefinition.HOTKEY_ID_INVALID) // Since there is no ID currently in the slot, key-up/down doesn't matter.
						{
							itsTableEntry = thisHk.idWithFlags;
						}
						else
						{
							prevHkId = (int)(itsTableEntry & HotkeyDefinition.HOTKEY_ID_MASK);

							if (thisHkId >= shk.Count || prevHkId >= shk.Count) // AltTab hotkey.
								continue; // Exclude AltTab hotkeys since hotkey_up[] and shk[] can't be used.

							prevHkIsKeyUp = (itsTableEntry & HotkeyDefinition.HOTKEY_KEY_UP) != 0;

							if (thisHkIsKeyUp && !prevHkIsKeyUp) // Override any existing key-up hotkey for this down hotkey ID, e.g. "LButton Up" takes precedence over "*LButton Up".
							{
								var prevHk = shk[prevHkId];

								// v1.1.33.03: Since modifiers aren't checked when hotkey_to_fire_upon_release is used
								// to fire a key-up hotkey, avoid setting setting this_hk as prev_hk's up hotkey when:
								//   a) prev_hk permits modifiers that this_hk does not permit (i.e. requires to be up).
								//   b) this_hk requires modifiers that prev_hk does not require (i.e. might not be pressed).
								//
								//  a up::    ; Doesn't permit any modifiers.
								//  *a::      ; Permits all modifiers, so shouldn't necessarily fire "a up".
								//  <^b up::  ; Doesn't permit RCtrl.
								//  ^b::      ; Permits RCtrl, so shouldn't necessarily fire "<^b up".
								//  *^c up::  ; Requires Ctrl.
								//  *+c::     ; Doesn't require Ctrl, so shouldn't necessarily fire "^c up".
								//
								// Note that prev_hk.mModifiersConsolidatedLR includes all LR modifiers that CAN be down,
								// but some might not be required, so might not be down (e.g. ^b has MOD_LCTRL|MOD_RCTRL).
								// However, if either LCTRL or RCTRL is set there, we know CTRL will be down, so the result
								// of ConvertModifiersLR() tells us which neutral modifiers will definitely be down.
								// prev_hk.mModifiers is checked first to avoid the function call where possible.
								if (((prevHk.allowExtraModifiers ? MODLR_MAX : prevHk.modifiersConsolidatedLR) & modifiersLRExcluded) == 0
										&& (thisHk.modifiersLR & ~prevHk.modifiersLR) == 0
										&& ((thisHk.modifiers & ~prevHk.modifiers) == 0
											|| (thisHk.modifiers & ~ConvertModifiersLR(prevHk.modifiersConsolidatedLR)) == 0))
								{
									hotkeyUp[prevHkId] = thisHk.idWithFlags;
									doCascade = false;  // Every place the down-hotkey ID already appears, it will point to this same key-up hotkey.
								}
								else
								{
									// v1.1.33.03: Override the lower-priority key-down hotkey which was already present.
									// Hotkey::FindPairedHotkey will be used to locate a key-down hotkey to fire based on
									// current modifier state.
									itsTableEntry = thisHk.idWithFlags;
								}
							}
							else
							{
								uint newUpId;

								if (!thisHkIsKeyUp && prevHkIsKeyUp)
									// Swap them so that the down-hotkey is in the main array and the up in the secondary:
									newUpId = itsTableEntry;
								else if (prevHkIsKeyUp || hotkeyUp[thisHkId] != HotkeyDefinition.HOTKEY_ID_INVALID)
									// Both are key-up hotkeys, or this_hk already has a key-up hotkey, in which case it
									// isn't overwritten since there's no guarantee the new one is more appropriate, and
									// it can cause the effect of swapping hotkey_up[] between two values repeatedly.
									newUpId = HotkeyDefinition.HOTKEY_ID_INVALID;
								else // Both are key-down hotkeys.
									// Fix for v1.0.40.09: Also copy the previous hotkey's corresponding up-hotkey (if any)
									// so that this hotkey will have that same one.  This also solves the issue of a hotkey
									// such as "^!F1" firing twice (once for down and once for up) when "*F1" and "*F1 up"
									// are both hotkeys.  Instead, the "*F1 up" hotkey should fire upon release of "^!F1"
									// so that the behavior is consistent with the case where "*F1" isn't present as a hotkey.
									// This fix doesn't appear to break anything else, most notably it still allows a hotkey
									// such as "^!F1 up" to take precedence over "*F1 up" because in such a case, this
									// code would never have executed because prev_hk_is_key_up would be true but
									// this_hk_is_key_up would be false.  Note also that sort_most_general_before_least()
									// has put key-up hotkeys after their key-down counterparts in the list.
									// v1.1.33.03: Without this "^!F1" won't fire twice, but it also won't fire "*F1 up".
									newUpId = hotkeyUp[prevHkId];

								if (newUpId != HotkeyDefinition.HOTKEY_ID_INVALID)
								{
									var new_up_hk = shk[(int)newUpId & HotkeyDefinition.HOTKEY_ID_MASK];

									// v1.1.33.03: Since modifiers aren't checked when hotkey_to_fire_upon_release is used
									// to fire a key-up hotkey, avoid setting setting new_up_hk as this_hk's up hotkey when:
									//   a) this_hk permits modifiers that new_up_hk does not.
									//   b) new_up_hk requires modifiers that this_hk does not.
									//
									//  <^a up::  ; Does not permit RCtrl.
									//  ^a::      ; Permits RCtrl, so shouldn't necessarily fire "<^a up".
									//  *!1 up::  ; Requires Alt.
									//  *<^1::    ; Doesn't require Alt, so shouldn't necessarily fire "*!1 up".
									//
									// ~i_modifiersLR_excluded already accounts for this_hk.AllowExtraModifiers.
									//if (  !(modLR_type)(~i_modifiersLR_excluded & (new_up_hk.mAllowExtraModifiers ? 0 : ~new_up_hk.mModifiersConsolidatedLR))  )
									if ((new_up_hk.allowExtraModifiers || (~modifiersLRExcluded & ~new_up_hk.modifiersConsolidatedLR) == 0)
											&& (new_up_hk.modifiers & ~modifiersMerged) == 0 && (new_up_hk.modifiersLR & ~thisHk.modifiersLR) == 0)
										hotkeyUp[thisHkId] = newUpId;
								}

								// Either both are key-up hotkeys or both are key-down hotkeys.  this overrides prev.
								itsTableEntry = thisHk.idWithFlags;
							}
						}

						if (doCascade)
						{
							if (thisHk.vk == VK_MENU || thisHk.vk == VK_LMENU)
							{
								Kvkm(modifiersLR, VK_LMENU) = thisHk.idWithFlags;
								Kscm(modifiersLR, LAlt) = thisHk.idWithFlags;
							}

							if (thisHk.vk == VK_MENU || thisHk.vk == VK_RMENU)
							{
								Kvkm(modifiersLR, VK_RMENU) = thisHk.idWithFlags;
								Kscm(modifiersLR, RAlt) = thisHk.idWithFlags;
							}

							if (thisHk.vk == VK_SHIFT || thisHk.vk == VK_LSHIFT)
							{
								Kvkm(modifiersLR, VK_LSHIFT) = thisHk.idWithFlags;
								Kscm(modifiersLR, LShift) = thisHk.idWithFlags;
							}

							if (thisHk.vk == VK_SHIFT || thisHk.vk == VK_RSHIFT)
							{
								Kvkm(modifiersLR, VK_RSHIFT) = thisHk.idWithFlags;
								Kscm(modifiersLR, RShift) = thisHk.idWithFlags;
							}

							if (thisHk.vk == VK_CONTROL || thisHk.vk == VK_LCONTROL)
							{
								Kvkm(modifiersLR, VK_LCONTROL) = thisHk.idWithFlags;
								Kscm(modifiersLR, LControl) = thisHk.idWithFlags;
							}

							if (thisHk.vk == VK_CONTROL || thisHk.vk == VK_RCONTROL)
							{
								Kvkm(modifiersLR, VK_RCONTROL) = thisHk.idWithFlags;
								Kscm(modifiersLR, RControl) = thisHk.idWithFlags;
							}
						} // if (do_cascade)
					}
				}
			}

			// Support "Control", "Alt" and "Shift" as suffix keys by appending their lists of
			// custom combos to the lists used by their left and right versions.  This avoids the
			// need for the hook to detect these keys and perform a search through a second list.
			// This must be done after all custom combos have been processed above, since they
			// might be defined in any order, but the neutral hotkeys must be placed last.
			if (kvk[VK_SHIFT].usedAsSuffix) // Skip the following unless Shift, LShift or RShift was used as a suffix.
				LinkKeysForCustomCombo(VK_SHIFT, VK_LSHIFT, VK_RSHIFT);

			if (kvk[VK_CONTROL].usedAsSuffix)
				LinkKeysForCustomCombo(VK_CONTROL, VK_LCONTROL, VK_RCONTROL);

			if (kvk[VK_MENU].usedAsSuffix)
				LinkKeysForCustomCombo(VK_MENU, VK_LMENU, VK_RMENU);

			// Add or remove hooks, as needed.  No change is made if the hooks are already in the correct state.
			AddRemoveHooks(hooksToBeActive);
		}

		internal bool CollectHotstring(ulong extraInfo, char[] ch, int charCount, nint activeWindow,
											  KeyHistoryItem keyHistoryCurr, ref HotstringDefinition hsOut, ref CaseConformModes caseConformMode, ref char endChar)
		{
			var suppressHotstringFinalChar = false; // Set default.
			var hm = Script.TheScript.HotstringManager;

			if (activeWindow != hsHwnd)
			{
				// Since the buffer tends to correspond to the text to the left of the caret in the
				// active window, if the active window changes, it seems best to reset the buffer
				// to avoid misfires.
				hsHwnd = activeWindow;
				hm.ClearBuf();
			}
			else if (hm.hsBuf.Count > 90)
				hm.hsBuf.RemoveRange(0, 45);

			hm.hsBuf.Add(ch[0]);

			if (charCount > 1)
				// MSDN: "This usually happens when a dead-key character (accent or diacritic) stored in the
				// keyboard layout cannot be composed with the specified virtual key to form a single character."
				hm.hsBuf.Add(ch[1]);

			if (hm.MatchHotstring() is HotstringDefinition hs)
			{
				int cpcaseStart, cpcaseEnd;
				int caseCapableCharacters;
				bool firstCharWithCaseIsUpper, firstCharWithCaseHasGoneBy;
				var hsBufSpan = (ReadOnlySpan<char>)CollectionsMarshal.AsSpan(hm.hsBuf);
				var hsLength = hsBufSpan.Length;
				var hsBufCountm1 = hsLength - 1;
				var hsBufCountm2 = hsLength - 2;
				var hasEndChar = hm.defEndChars.Contains(hsBufSpan[hsBufCountm1]);

				if (HotInputLevelAllowsFiring(hs.inputLevel, extraInfo, ref keyHistoryCurr.eventType))
				{
					// Since default KeyDelay is 0, and since that is expected to be typical, it seems
					// best to unconditionally post a message rather than trying to handle the backspacing
					// and replacing here.  This is because a KeyDelay of 0 might be fairly slow at
					// sending keystrokes if the system is under heavy load, in which case we would
					// not be returning to our caller in a timely fashion, which would case the OS to
					// think the hook is unresponsive, which in turn would cause it to timeout and
					// route the key through anyway (testing confirms this).
					if (!hs.conformToCase)
					{
						caseConformMode = CaseConformModes.None;
					}
					else
					{
						// Find out what case the user typed the string in so that we can have the
						// replacement produced in similar case:
						cpcaseEnd = hsLength;

						if (hs.endCharRequired)
							--cpcaseEnd;

						// Bug-fix for v1.0.19: First find out how many of the characters in the abbreviation
						// have upper and lowercase versions (i.e. exclude digits, punctuation, etc):
						for (caseCapableCharacters = 0, firstCharWithCaseIsUpper = firstCharWithCaseHasGoneBy = false
								, cpcaseStart = cpcaseEnd - hs.str.Length
								; cpcaseStart < cpcaseEnd; ++cpcaseStart)
						{
							char chStart = hsBufSpan[cpcaseStart];

							if (char.IsLower(chStart) || char.IsUpper(chStart)) // A case-capable char.
							{
								if (!firstCharWithCaseHasGoneBy)
								{
									firstCharWithCaseHasGoneBy = true;

									if (char.IsUpper(chStart))
										firstCharWithCaseIsUpper = true; // Override default.
								}

								++caseCapableCharacters;
							}
						}

						if (caseCapableCharacters == 0) // All characters in the abbreviation are caseless.
							caseConformMode = CaseConformModes.None;
						else if (caseCapableCharacters == 1)
							// Since there is only a single character with case potential, it seems best as
							// a default behavior to capitalize the first letter of the replacement whenever
							// that character was typed in uppercase.  The behavior can be overridden by
							// turning off the case-conform mode.
							caseConformMode = firstCharWithCaseIsUpper ? CaseConformModes.FirstCap : CaseConformModes.None;
						else // At least two characters have case potential. If all of them are upper, use ALL_CAPS.
						{
							if (!firstCharWithCaseIsUpper) // It can't be either FIRST_CAP or ALL_CAPS.
							{
								caseConformMode = CaseConformModes.None;
							}
							else // First char is uppercase, and if all the others are too, this will be ALL_CAPS.
							{
								caseConformMode = CaseConformModes.FirstCap; // Set default.

								// Bug-fix for v1.0.19: Changed !IsCharUpper() below to IsCharLower() so that
								// caseless characters such as the @ symbol do not disqualify an abbreviation
								// from being considered "all uppercase":
								for (cpcaseStart = cpcaseEnd - hs.str.Length; cpcaseStart < cpcaseEnd; ++cpcaseStart)
									if (char.IsLower(hsBufSpan[cpcaseStart])) // Use IsCharLower to better support chars from non-English languages.
										break; // Any lowercase char disqualifies CASE_CONFORM_ALL_CAPS.

								if (cpcaseStart == cpcaseEnd) // All case-possible characters are uppercase.
									caseConformMode = CaseConformModes.AllCaps;

								//else leave it at the default set above.
							}
						}
					}

					if (hs.doBackspace || hs.omitEndChar && hs.endCharRequired) // Fix for v1.0.37.07: Added hs.mOmitEndChar so that B0+O will omit the ending character.
					{
						// Have caller suppress this final key pressed by the user, since it would have
						// to be backspaced over anyway.  Even if there is a visible Input command in
						// progress, this should still be okay since the input will still see the key,
						// it's just that the active window won't see it, which is okay since once again
						// it would have to be backspaced over anyway.  UPDATE: If an Input is in progress,
						// it should not receive this final key because otherwise the hotstring's backspacing
						// would backspace one too few times from the Input's point of view, thus the input
						// would have one extra, unwanted character left over (namely the first character
						// of the hotstring's abbreviation).  However, this method is not a complete
						// solution because it fails to work under a situation such as the following:
						// A hotstring script is started, followed by a separate script that uses the
						// Input command.  The Input script's hook will take precedence (since it was
						// started most recently), thus when the Hotstring's script's hook does sends
						// its replacement text, the Input script's hook will get a hold of it first
						// before the Hotstring's script has a chance to suppress it.  In other words,
						// The Input command will capture the ending character and then there will
						// be insufficient backspaces sent to clear the abbreviation out of it.  This
						// situation is quite rare so for now it's just mentioned here as a known limitation.
						suppressHotstringFinalChar = true;
					}

					// Post the message rather than sending it, because Send would need
					// SendMessageTimeout(), which is undesirable because the whole point of
					// making this hook thread separate from the main thread is to have it be
					// maximally responsive (especially to prevent mouse cursor lag).
					// Put the end char in the LOWORD and the case_conform_mode in the HIWORD.
					// Casting to UCHAR might be necessary to avoid problems when MAKELONG
					// casts a signed char to an unsigned WORD.
					// UPDATE: In v1.0.42.01, the message is posted later (by our caller) to avoid
					// situations in which the message arrives and is processed by the main thread
					// before we finish processing the hotstring's final keystroke here.  This avoids
					// problems with a script calling GetKeyState() and getting an inaccurate value
					// because the hook thread is either pre-empted or is running in parallel
					// (multiprocessor) and hasn't yet returned 1 or 0 to determine whether the final
					// keystroke is suppressed or passed through to the active window.
					// UPDATE: In v1.0.43, the ending character is not put into the Lparam when
					// hs.mDoBackspace is false.  This is because:
					// 1) When not backspacing, it's more correct that the ending character appear where the
					//    user typed it rather than appearing at the end of the replacement.
					// 2) Two ending characters would appear in pre-1.0.43 versions: one where the user typed
					//    it and one at the end, which is clearly incorrect.
					hsOut = hs;
					endChar = hs.endCharRequired ? hsBufSpan[hsBufCountm1] : (char)0;

					// Clean up.
					// The keystrokes to be sent by the other thread upon receiving the message prepared above
					// will not be received by this function because:
					// 1) CollectInput() is not called for simulated keystrokes.
					// 2) The keyboard hook is absent during a SendInput hotstring.
					// 3) The keyboard hook does not receive SendPlay keystrokes (if hotstring is of that type).
					// Consequently, the buffer should be adjusted below to ensure it's in the right state to work
					// in situations such as the user typing two hotstrings consecutively where the ending
					// character of the first is used as a valid starting character (non-alphanumeric) for the next.
					if (!string.IsNullOrEmpty(hs.replacement))
					{
						// Since the buffer no longer reflects what is actually on screen to the left
						// of the caret position (since a replacement is about to be done), reset the
						// buffer, except for any end-char (since that might legitimately form part
						// of another hot string adjacent to the one just typed).  The end-char
						// sent by DoReplace() won't be captured (since it's "ignored input", which
						// is why it's put into the buffer manually here):
						if (hs.endCharRequired)
							hm.hsBuf.RemoveRange(0, hm.hsBuf.Count - 1);
						else
							hm.ClearBuf();
					}
					else if (hs.doBackspace)
					{
						// It's *not* a replacement, but we're doing backspaces, so adjust buf for backspaces
						// and the fact that the final char of the HS (if no end char) or the end char
						// (if end char required) will have been suppressed and never made it to the
						// active window.  A simpler way to understand is to realize that the buffer now
						// contains (for recognition purposes, in its right side) the hotstring and its
						// end char (if applicable), so remove both:
						hm.hsBuf.RemoveRange(hm.hsBuf.Count - hs.str.Length, hs.str.Length);

						if (hs.endCharRequired)
							hm.hsBuf.RemoveAt(hm.hsBuf.Count - 1);
					}

					// v1.0.38.04: Fixed the following mDoReset section by moving it beneath the above because
					// the above relies on the fact that the buffer has not yet been reset.
					// v1.0.30: mDoReset was added to prevent hotstrings such as the following
					// from firing twice in a row, if you type 11 followed by another 1 afterward:
					//:*?B0:11::
					//MsgBox,0,test,%A_ThisHotkey%,1 ; Show which key was pressed and close the window after a second.
					//return
					// There are probably many other uses for the reset option (albeit obscure, but they have
					// been brought up in the forum at least twice).
					if (hs.doReset)
						hm.ClearBuf(); // Further below, the buffer will be terminated to reflect this change.
				}//for each hotstring for this letter.
			}//if hotstring buffer not empty.

			return !suppressHotstringFinalChar;
		}

		internal bool CollectKeyUp(ulong extraInfo, uint vk, uint sc, bool early)
		// Caller is responsible for having initialized aHotstringWparamToPost to HOTSTRING_INDEX_INVALID.
		// Returns true if the caller should treat the key as visible (non-suppressed).
		// Always use the parameter vk rather than event.vkCode because the caller or caller's caller
		// might have adjusted vk, namely to make it a left/right specific modifier key rather than a
		// neutral one.
		{
			for (var input = Script.TheScript.input; input != null; input = input.prev)
			{
				if (input.BeforeHotkeys == early && input.IsInteresting(extraInfo) && input.InProgress())
				{
					if (input.scriptObject != null && input.scriptObject.OnKeyUp != null
							&& (((input.keySC[sc] | input.keyVK[vk]) & INPUT_KEY_NOTIFY) != 0
								|| (input.notifyNonText && (input.keyVK[vk] & INPUT_KEY_IS_TEXT) == 0)))
					{
						_ = channel.Writer.TryWrite(new KeysharpMsg()
						{
							message = (uint)UserMessages.AHK_INPUT_KEYUP,
							obj = input,
							lParam = new nint(vk),
							wParam = new nint(sc)
						});
					}

					if ((input.keySC[sc] & INPUT_KEY_DOWN_SUPPRESSED) != 0)
					{
						input.keySC[sc] &= ~INPUT_KEY_DOWN_SUPPRESSED;
						return false;
					}

					if ((input.keyVK[vk] & INPUT_KEY_DOWN_SUPPRESSED) != 0)
					{
						input.keyVK[vk] &= ~INPUT_KEY_DOWN_SUPPRESSED;
						return false;
					}
				}
			}

			return true;
		}

		internal bool CollectInputHook(ulong extraInfo, uint vk, uint sc, char[] ch, int charCount, bool early)
		{
			var input = Script.TheScript.input;

			for (; input != null; input = input.prev)
			{
				if (!(input.BeforeHotkeys == early && input.IsInteresting(extraInfo) && input.InProgress()))
					continue;

				var keyFlags = input.keyVK[vk] | input.keySC[sc];
				// aCharCount is negative for dead keys, which are treated as text but not collected.
				var treatAsText = charCount != 0 && (keyFlags & INPUT_KEY_IGNORE_TEXT) == 0;
				var collectChars = treatAsText && charCount > 0;
				// Determine visibility based on options and whether the key produced text.
				// Negative aCharCount (dead key) is treated as text in this context.
				bool visible;

				if ((keyFlags & INPUT_KEY_VISIBILITY_MASK) != 0)
					visible = (keyFlags & INPUT_KEY_VISIBLE) != 0;
				else if (kvk[vk].asModifiersLR != 0 || kvk[vk].forceToggle != null)
					visible = true; // Do not suppress modifiers or toggleable keys unless specified by KeyOpt().
				else
					visible = treatAsText ? input.visibleText : input.visibleNonText;

				if ((keyFlags & END_KEY_ENABLED) != 0) // A terminating keystroke has now occurred unless the shift state isn't right.
				{
					var end_if_shift_is_down = (keyFlags & END_KEY_WITH_SHIFT) != 0;
					var end_if_shift_is_not_down = (keyFlags & END_KEY_WITHOUT_SHIFT) != 0;
					var shift_is_down = (kbdMsSender.modifiersLRLogical & (MOD_LSHIFT | MOD_RSHIFT)) != 0;

					if (shift_is_down ? end_if_shift_is_down : end_if_shift_is_not_down)
					{
						// The shift state is correct to produce the desired end-key.
						input.EndByKey(vk, sc, input.keySC[sc] != 0 && (sc != 0 || input.keyVK[vk] == 0), shift_is_down && !end_if_shift_is_not_down);

						if (!visible)
							break;

						continue;
					}
				}

				// Collect before backspacing, so if VK_BACK was preceded by a dead key, we delete it instead of the
				// previous char.  For example, {vkDE}{BS} on the US-Intl layout produces '\b (but we discarded \b).
				if (collectChars)
					input.CollectChar(new string(ch), charCount);

				// Fix for v2.0: Shift is allowed as it generally has no effect on the native function of Backspace.
				// This is probably connected with the fact that Shift+BS is also transcribed to `b, which we don't want.
				if (vk == VK_BACK && input.backspaceIsUndo
						&& (kbdMsSender.modifiersLRLogical & ~(MOD_LSHIFT | MOD_RSHIFT)) == 0)
				{
					if (input.buffer.Length != 0)
						input.buffer = input.buffer.Substring(0, input.buffer.Length - 1);

					if ((keyFlags & INPUT_KEY_VISIBILITY_MASK) == 0)// If +S and +V haven't been applied to Backspace...
						visible = input.visibleText; // Override VisibleNonText.

					// Fall through to the check below in case this {BS} completed a dead key sequence.
				}

				if (input.notifyNonText)
				{
					// These flags enable key-up events to be classified as text or non-text based on
					// whether key-down produced text.
					if (treatAsText)
						input.keyVK[vk] |= INPUT_KEY_IS_TEXT;
					else
						input.keyVK[vk] &= ~INPUT_KEY_IS_TEXT; // In case keyboard layout has changed or similar.
				}

				// Posting the notifications after CollectChar() might reduce the odds of a race condition.
				if (((keyFlags & INPUT_KEY_NOTIFY) != 0 || (input.notifyNonText && !treatAsText))
						&& input.scriptObject != null && input.scriptObject.OnKeyDown != null)
				{
					// input is passed because the alternative would require the main thread to
					// iterate through the Input chain and determine which ones should be notified.
					// This would mean duplicating much of the logic that's used here, and would be
					// complicated by the possibility of an Input being terminated while OnKeyDown
					// is being executed (and thereby breaking the list).
					// This leaves room only for the bare essential parameters: aVK and aSC.
					_ = channel.Writer.TryWrite(new KeysharpMsg()
					{
						message = (uint)UserMessages.AHK_INPUT_KEYDOWN,
						obj = input,
						lParam = new nint(vk),
						wParam = new nint(sc)
					});
					//PostMessage(Keysharp.Scripting.Script.MainWindowHandle, (uint)UserMessages.AHK_INPUT_KEYDOWN, input, (uint)((sc << 16) | vk));
				}

				// Seems best to not collect dead key chars by default; if needed, OnDeadChar
				// could be added, or the script could mark each dead key for OnKeyDown.
				if (collectChars && input.scriptObject != null && input.scriptObject.OnChar != null)
				{
					_ = channel.Writer.TryWrite(new KeysharpMsg()
					{
						message = (uint)UserMessages.AHK_INPUT_CHAR,
						obj = input,
						lParam = new nint(ch[0]),
						wParam = ch.Length > 1 ? new nint(ch[1]) : 0
					});
					//PostMessage(Keysharp.Scripting.Script.MainWindowHandle, (uint)UserMessages.AHK_INPUT_CHAR, input, (uint)((ch[1] << 16) | ch[0]));
				}

				if (!visible)
				{
					if (charCount < 0 && treatAsText && input.InProgress())
					{
						// This dead key is being treated as text but will be suppressed, so to get the correct
						// result, we will need to replay the dead key sequence when the next key is collected.
						pendingDeadKeyInvisible = true;
					}

					break;
				}
			}

			if (input != null) // Early break (invisible input).
			{
				if (sc != 0)
					input.keySC[sc] |= INPUT_KEY_DOWN_SUPPRESSED;
				else
					input.keyVK[vk] |= INPUT_KEY_DOWN_SUPPRESSED;

				return false;
			}

			return true;
		}

		internal abstract bool EarlyCollectInput(ulong extraInfo, uint rawSC, uint vk, uint sc, bool keyUp, bool isIgnored
										, CollectInputState state, KeyHistoryItem keyHistoryCurr);

		internal bool CollectInput(ulong extraInfo, uint rawSC, uint vk, uint sc, bool keyUp, bool isIgnored
								   , CollectInputState state, KeyHistoryItem keyHistoryCurr, ref HotstringDefinition hsOut
								   , ref CaseConformModes caseConformMode, ref char endChar
								  )
		// Caller is responsible for having initialized aHotstringWparamToPost to HOTSTRING_INDEX_INVALID.
		// Returns true if the caller should treat the key as visible (non-suppressed).
		// Always use the parameter vk rather than event.vkCode because the caller or caller's caller
		// might have adjusted vk, namely to make it a left/right specific modifier key rather than a
		// neutral one.
		{
			if (!state.earlyCollected && !EarlyCollectInput(extraInfo, rawSC, vk, sc, keyUp, isIgnored, state, keyHistoryCurr))
				return false;

			if (keyUp)
				return CollectKeyUp(extraInfo, vk, sc, false);

			int charCount = state.charCount;
			var activeWindow = state.activeWindow;
			var activeWindowKeybdLayout = state.keyboardLayout;
			var ch = state.ch;
			var sb = new StringBuilder(8);
			var hm = Script.TheScript.HotstringManager;
			var hsBuf = hm.hsBuf;

			if (!CollectInputHook(extraInfo, vk, sc, ch, charCount, false))
				return false; // Suppress.

			// Hotstrings monitor neither ignored input nor input that is invisible due to suppression by
			// the Input command.  One reason for not monitoring ignored input is to avoid any chance of
			// an infinite loop of keystrokes caused by one hotstring triggering itself directly or
			// indirectly via a different hotstring:
			if (hm.enabledCount != 0 && !isIgnored)
			{
				switch (vk)
				{
					case VK_LEFT:
					case VK_RIGHT:
					case VK_DOWN:
					case VK_UP:
					case VK_NEXT:
					case VK_PRIOR:
					case VK_HOME:
					case VK_END:

						// Reset hotstring detection if the user seems to be navigating within an editor.  This is done
						// so that hotstrings do not fire in unexpected places.
						if (hsBuf.Count > 0)
							hm.ClearBuf();

						break;

					case VK_BACK:

						// v1.0.21: Only true (unmodified) backspaces are recognized by the below.  Another reason to do
						// this is that ^backspace has a native function (delete word) different than backspace in many editors.
						// Fix for v1.0.38: Below now uses kbdMsSender.modifiersLR_logical vs. physical because it's the logical state
						// that determines whether the backspace behaves like an unmodified backspace.  This solves the issue
						// of the Input command collecting simulated backspaces as real characters rather than recognizing
						// them as a means to erase the previous character in the buffer.
						if (kbdMsSender.modifiersLRLogical == 0 && hsBuf.Count > 0)
							hsBuf.RemoveAt(hsBuf.Count - 1);

						// Fall through to the check below in case this {BS} completed a dead key sequence.
						break;
				}

				if (charCount > 0
						&& !CollectHotstring(extraInfo, ch, charCount, activeWindow, keyHistoryCurr,
											 ref hsOut, ref caseConformMode, ref endChar))
				{
					var ignored = new char[8];

					if (state.used_dead_key_non_destructively)
					{
						// There's still a dead key in the keyboard layout's internal buffer, and it's supposed to apply to
						// this keystroke which we're suppressing.  Flush it out, otherwise a hotstring like the following
						// would insert an extra accent character:
						//   :*:jsá::jsmith@somedomain.com
						System.Array.Clear(ignored, 0, ignored.Length);

						var platformManager = Script.TheScript.PlatformProvider.Manager;
						while (platformManager.ToUnicode(VK_DECIMAL, 0, physicalKeyState, ignored, 1, activeWindowKeybdLayout) == -1) ;
					}

					return false; // Suppress.
				}
			}

			return true; // Visible.
		}

		internal abstract uint CharToVKAndModifiers(char ch, ref uint? modifiersLR, nint keybdLayout, bool enableAZFallback = false);

		internal static uint ConvertMouseButton(string buf, bool allowWheel = true) => ConvertMouseButton(buf.AsSpan(), allowWheel);

		internal static uint ConvertMouseButton(ReadOnlySpan<char> buf, bool allowWheel = true)
		{
			if (buf.Length == 0 || buf.StartsWith("Left", StringComparison.OrdinalIgnoreCase) || buf.StartsWith("L", StringComparison.OrdinalIgnoreCase))
				return VK_LBUTTON; // Some callers rely on this default when buf is empty.

			if (buf.StartsWith("Right", StringComparison.OrdinalIgnoreCase) || buf.StartsWith("R", StringComparison.OrdinalIgnoreCase)) return VK_RBUTTON;

			if (buf.StartsWith("Middle", StringComparison.OrdinalIgnoreCase) || buf.StartsWith("M", StringComparison.OrdinalIgnoreCase)) return VK_MBUTTON;

			if (buf.StartsWith("X1", StringComparison.OrdinalIgnoreCase)) return VK_XBUTTON1;

			if (buf.StartsWith("X2", StringComparison.OrdinalIgnoreCase)) return VK_XBUTTON2;

			if (allowWheel)
			{
				if (buf.StartsWith("WheelUp", StringComparison.OrdinalIgnoreCase) || buf.StartsWith("WU", StringComparison.OrdinalIgnoreCase)) return VK_WHEEL_UP;

				if (buf.StartsWith("WheelDown", StringComparison.OrdinalIgnoreCase) || buf.StartsWith("WD", StringComparison.OrdinalIgnoreCase)) return VK_WHEEL_DOWN;

				// Lexikos: Support horizontal scrolling in Windows Vista and later.
				if (buf.StartsWith("WheelLeft", StringComparison.OrdinalIgnoreCase) || buf.StartsWith("WL", StringComparison.OrdinalIgnoreCase)) return VK_WHEEL_LEFT;

				if (buf.StartsWith("WheelRight", StringComparison.OrdinalIgnoreCase) || buf.StartsWith("WR", StringComparison.OrdinalIgnoreCase)) return VK_WHEEL_RIGHT;
			}

			return 0;
		}

		internal HookType GetActiveHooks()
		{
			var hookscurrentlyactive = HookType.None;

			if (HasKbdHook())
				hookscurrentlyactive |= HookType.Keyboard;

			if (HasMouseHook())
				hookscurrentlyactive |= HookType.Mouse;

			return hookscurrentlyactive;
		}

		internal string GetHookStatus()
		{
			var sb = new StringBuilder(2048);
			_ = sb.AppendLine($"Modifiers (Hook's Logical) = {kbdMsSender.ModifiersLRToText(kbdMsSender.modifiersLRLogical)}");
			_ = sb.AppendLine($"Modifiers (Hook's Physical) = {kbdMsSender.ModifiersLRToText(kbdMsSender.modifiersLRPhysical)}");
			_ = sb.AppendLine($"Prefix key is down: {(prefixKey != null ? "yes" : "no")}");

			if (!HasKbdHook())
			{
				_ = sb.Append("NOTE: Only the script's own keyboard events are shown");
				_ = sb.Append("(not the user's), because the keyboard hook isn't installed.");
			}

			// Add the below even if key history is already disabled so that the column headings can be seen.
			_ = sb.AppendLine();
			_ = sb.Append("NOTE: To disable the key history shown below, call KeyHistory(0). ");
			_ = sb.Append("The same method can be used to change the size of the history buffer. ");
			_ = sb.AppendLine("For example: KeyHistory 100 (Default is 40, Max is 500)");
			_ = sb.AppendLine();
			_ = sb.Append("The oldest are listed first. VK=Virtual Key, SC=Scan Code, Elapsed=Seconds since the previous event");
			_ = sb.Append(". Types: h=Hook Hotkey, s=Suppressed (blocked), i=Ignored because it was generated by an AHK script");
			_ = sb.AppendLine(", a=Artificial, #=Disabled via #HotIf, U=Unicode character (SendInput).");
			_ = sb.AppendLine();
			_ = sb.AppendLine("VK  SC\tType\tUp/Dn\tElapsed\tKey\t\tWindow");
			_ = sb.Append("-------------------------------------------------------------------------------------------------------------");

			if (keyHistory != null && keyHistory.Size > 0)
				_ = sb.Append(keyHistory.ToString());

			return sb.ToString();
		}

		internal bool HasEitherHook() => GetActiveHooks() != HookType.None;

		internal bool HasKbdHook() => kbdHook != 0;

		internal bool HasMouseHook() => mouseHook != 0;

		internal virtual object Invoke(Func<object> f) => f();

		internal virtual bool IsHotstringWordChar(char ch) => char.IsLetterOrDigit(ch) ? true : !char.IsWhiteSpace(ch);

		internal abstract bool IsKeyDown(uint vk);

		internal abstract bool IsKeyDownAsync(uint vk);

		internal abstract bool IsKeyToggledOn(uint vk);

		internal abstract bool IsMouseVK(uint vk);

		internal virtual bool IsHookThreadRunning() => false;

		internal bool IsReadThreadCompleted()
		=> channelReadThread != null&&
		channelReadThread.Result != null&&
		channelReadThread.Result.IsCompleted&&
		channelReadThread.IsCompleted;

		internal bool IsReadThreadRunning()
		=> running&&
		channelReadThread != null&&
		channelReadThread.Result != null&&
		!channelReadThread.Result.IsCompleted;

		internal abstract bool IsWheelVK(uint vk);

		/// <summary>
		/// Always use the parameter vk rather than event.vkCode because the caller or caller's caller
		/// might have adjusted vk, namely to make it a left/right specific modifier key rather than a
		/// neutral one.
		/// Will need to figure out how to manage fake shift keyup when using shift and numpad (it makes shift not normally apply).
		/// </summary>
		/// <param name="eventFlags"></param>
		/// <param name="vk"></param>
		/// <param name="keyUp"></param>
		/// <returns></returns>
		internal virtual bool KeybdEventIsPhysical(bool isArtificial, uint vk, bool keyUp)
		{
			// MSDN: "The keyboard input can come from the local keyboard driver or from calls to the keybd_event
			// function. If the input comes from a call to keybd_event, the input was "injected"".
			// My: This also applies to mouse events, so use it for them too:
			if (isArtificial)
				return false;

			// So now we know it's a physical event.  But certain SHIFT key-down events are driver-generated.
			// We want to be able to tell the difference because the Send command and other aspects
			// of keyboard functionality need us to be accurate about which keys the user is physically
			// holding down at any given time:
			if ((vk == VK_LSHIFT || vk == VK_RSHIFT) && !keyUp)
			{
				// If the corresponding mask bit is set, the key was temporarily "released" by the system
				// as part of translating a shift-numpad combination to its unshifted counterpart, and this
				// event is the fake key-down which follows the release of the numpad key.  The system uses
				// standard scancodes for this specific case, not SC_FAKE_LSHIFT or SC_FAKE_RSHIFT.
				if ((kbdMsSender.modifiersLRNumpadMask & (vk == VK_LSHIFT ? MOD_LSHIFT : MOD_RSHIFT)) != 0)
					return false;
			}

			var script = Script.TheScript;
			// Otherwise, it's physical.
			// v1.0.42.04:
			// The time member of the incoming event struct has been observed to be wrongly zero sometimes, perhaps only
			// for AltGr keyboard layouts that generate LControl events when RAlt is pressed (but in such cases, I think
			// it's only sometimes zero, not always).  It might also occur during simulation of Alt+Numpad keystrokes
			// to support {Asc NNNN}.  In addition, SendInput() is documented to have the ability to set its own timestamps;
			// if it's callers put in a bad timestamp, it will probably arrive here that way too.  Thus, use GetTickCount().
			// More importantly, when a script or other application simulates an AltGr keystroke (either down or up),
			// the LControl event received here is marked as physical by the OS or keyboard driver.  This is undesirable
			// primarily because it makes g_TimeLastInputPhysical inaccurate, but also because falsely marked physical
			// events can impact the script's calls to GetKeyState("LControl", "P"), etc.
			script.timeLastInputPhysical = script.timeLastInputKeyboard = DateTime.UtcNow;
			return true;
		}

		/// <summary>
		/// Convert the given virtual key / scan code to its equivalent bitwise modLR value.
		/// Callers rely upon the fact that we convert a neutral key such as VK_SHIFT into MOD_LSHIFT,
		/// not the bitwise combo of MOD_LSHIFT|MOD_RSHIFT.
		/// v1.0.43: VK_SHIFT should yield MOD_RSHIFT if the caller explicitly passed the right vs. left scan code.
		/// The SendPlay method relies on this to properly release AltGr, such as after "SendPlay @" in German.
		/// Other things may also rely on it because it is more correct.
		/// </summary>
		/// <param name="vk"></param>
		/// <param name="sc"></param>
		/// <param name="pIsNeutral"></param>
		/// <returns></returns>
		internal virtual uint KeyToModifiersLR(uint vk, uint sc, ref bool? isNeutral)
		{
			if (vk == 0 && sc == 0)
				return 0;

			if (vk != 0) // Have vk take precedence over any non-zero sc.
			{
				switch (vk)
				{
					case VK_SHIFT:
						if (sc == ScanCodes.RShift)
							return MOD_RSHIFT;

						//else aSC is omitted (0) or SC_LSHIFT.  Either way, most callers would probably want that considered "neutral".
						if (isNeutral != null)
							isNeutral = true;

						return MOD_LSHIFT;

					case VK_LSHIFT: return MOD_LSHIFT;

					case VK_RSHIFT: return MOD_RSHIFT;

					case VK_CONTROL:
						if (sc == ScanCodes.RControl)
							return MOD_RCONTROL;

						//else aSC is omitted (0) or SC_LCONTROL.  Either way, most callers would probably want that considered "neutral".
						if (isNeutral != null)
							isNeutral = true;

						return MOD_LCONTROL;

					case VK_LCONTROL: return MOD_LCONTROL;

					case VK_RCONTROL: return MOD_RCONTROL;

					case VK_MENU:
						if (sc == ScanCodes.RAlt)
							return MOD_RALT;

						//else aSC is omitted (0) or SC_LALT.  Either way, most callers would probably want that considered "neutral".
						if (isNeutral != null)
							isNeutral = true;

						return MOD_LALT;

					case VK_LMENU: return MOD_LALT;

					case VK_RMENU: return MOD_RALT;

					case VK_LWIN: return MOD_LWIN;

					case VK_RWIN: return MOD_RWIN;

					default:
						return 0;
				}
			}

			// If above didn't return, rely on the scan code instead, which is now known to be non-zero.

			return sc switch
		{
				ScanCodes.LShift => MOD_LSHIFT,
				ScanCodes.RShift => MOD_RSHIFT,
				ScanCodes.LControl => MOD_LCONTROL,
				ScanCodes.RControl => MOD_RCONTROL,
				ScanCodes.LAlt => MOD_LALT,
				ScanCodes.RAlt => MOD_RALT,
				ScanCodes.LWin => MOD_LWIN,
				ScanCodes.RWin => MOD_RWIN,
				_ => 0,
		};
	}

	internal ref uint Kscm(uint i, uint j) => ref kscm[(i * SC_ARRAY_COUNT) + j];

		internal ref uint Kvkm(uint i, uint j) => ref kvkm[(i * VK_ARRAY_COUNT) + j];

		internal void LinkKeysForCustomCombo(uint neutral, uint left, uint right)
		{
			var first_neutral = kvk[neutral].firstHotkey;

			if (first_neutral == HotkeyDefinition.HOTKEY_ID_INVALID)
				return;

			// Append the neutral key's list to the lists of the left and right keys.
			HotkeyDefinition.CustomComboLast(ref kvk[left].firstHotkey) = first_neutral;
			HotkeyDefinition.CustomComboLast(ref kvk[right].firstHotkey) = first_neutral;
		}

		internal abstract uint MapScToVk(uint sc);

		internal abstract uint MapVkToSc(uint vk, bool returnSecondary = false);

		internal void ParseClickOptions(string options, ref int x, ref int y, ref uint vk, ref KeyEventTypes eventType, ref long repeatCount, ref bool moveOffset) =>
		ParseClickOptions(options.AsSpan(), ref x, ref y, ref vk, ref eventType, ref repeatCount, ref moveOffset);

		internal static void ParseClickOptions(ReadOnlySpan<char> options, ref int x, ref int y, ref uint vk, ref KeyEventTypes eventType, ref long repeatCount, ref bool moveOffset)
		{
			// Set defaults for all output parameters for caller.
			x = CoordUnspecified;
			y = CoordUnspecified;
			vk = VK_LBUTTON;
			eventType = KeyEventTypes.KeyDownAndUp;
			repeatCount = 1L;
			moveOffset = false;
			uint temp_vk;

			foreach (Range r in options.SplitAny(SpaceTabComma))
			{
				var opt = options[r].Trim();

				if (opt.Length > 0)
				{
					// Parameters can occur in almost any order to enhance usability (at the cost of
					// slightly diminishing the ability to unambiguously add more parameters in the future).
					// Seems okay to support floats because ATOI() will just omit the decimal portion.
					if (double.TryParse(opt, NumberStyles.Float, Parser.inv, out var d))
					{
						var val = (int)d;

						// Any numbers present must appear in the order: X, Y, RepeatCount
						// (optionally with other options between them).
						if (x == CoordUnspecified) // This will be converted into repeat-count if it is later discovered there's no Y coordinate.
							x = val;
						else if (y == CoordUnspecified)
							y = val;
						else // Third number is the repeat-count (but if there's only one number total, that's repeat count too, see further below).
							repeatCount = val;
					}
					else // Mouse button/name and/or Down/Up/Repeat-count is present.
					{
						if ((temp_vk = ConvertMouseButton(opt, true)) != 0)
						{
							vk = temp_vk;
						}
						else
						{
							switch (char.ToUpper(opt[0]))
							{
								case 'D': eventType = KeyEventTypes.KeyDown; break;

								case 'U': eventType = KeyEventTypes.KeyUp; break;

								case 'R': moveOffset = true; break; // Since it wasn't recognized as the right mouse button, it must have other letters after it, e.g. Rel/Relative.
									// default: Ignore anything else to reserve them for future use.
							}
						}
					}
				}
			}

			if (x != CoordUnspecified && y == CoordUnspecified)
			{
				// When only one number is present (e.g. {Click 2}, it's assumed to be the repeat count.
				repeatCount = x;
				x = CoordUnspecified;
			}
			else if (x == CoordUnspecified && y == CoordUnspecified)//Neither was specified, so just use the cursor position.
			{
				var pos = Cursor.Position;
				x = pos.X;
				y = pos.Y;
			}
		}

		internal bool PostMessage(KeysharpMsg msg)
		=> IsReadThreadRunning()&& channel.Writer.TryWrite(msg);

		internal void SetModifierAsPrefix(uint vk, uint sc, bool alwaysSetAsPrefix = false)
		// The caller has already ensured that vk and/or sc is a modifier such as VK_CONTROL.
		{
			if (vk != 0)
			{
				switch (vk)
				{
					case VK_MENU:
					case VK_SHIFT:
					case VK_CONTROL:

						// Since the user is configuring both the left and right counterparts of a key to perform a suffix action,
						// it seems best to always consider those keys to be prefixes so that their suffix action will only fire
						// when the key is released.  That way, those keys can still be used as normal modifiers.
						// UPDATE for v1.0.29: But don't do it if there is a corresponding key-up hotkey for this neutral
						// modifier, which allows a remap such as the following to succeed:
						// Control::Send {LWin down}
						// Control up::Send {LWin up}
						if (!alwaysSetAsPrefix)
						{
							var shk = Script.TheScript.HotkeyData.shk;

							for (var i = 0; i < shk.Count; ++i)
							{
								var h = shk[i];

								if (h.vk == vk && h.keyUp && h.modifiersConsolidatedLR == 0 && h.modifierVK == 0 && h.modifierSC == 0 && !h.IsCompletelyDisabled())
									return; // Since caller didn't specify aAlwaysSetAsPrefix==true, don't make this key a prefix.
							}
						}

						switch (vk)
						{
							case VK_MENU:
								kvk[VK_MENU].usedAsPrefix = KeyType.PREFIX_FORCED;
								kvk[VK_LMENU].usedAsPrefix = KeyType.PREFIX_FORCED;
								kvk[VK_RMENU].usedAsPrefix = KeyType.PREFIX_FORCED;
								ksc[LAlt].usedAsPrefix = KeyType.PREFIX_FORCED;
								ksc[RAlt].usedAsPrefix = KeyType.PREFIX_FORCED;
								break;

							case VK_SHIFT:
								kvk[VK_SHIFT].usedAsPrefix = KeyType.PREFIX_FORCED;
								kvk[VK_LSHIFT].usedAsPrefix = KeyType.PREFIX_FORCED;
								kvk[VK_RSHIFT].usedAsPrefix = KeyType.PREFIX_FORCED;
								ksc[LShift].usedAsPrefix = KeyType.PREFIX_FORCED;
								ksc[RShift].usedAsPrefix = KeyType.PREFIX_FORCED;
								break;

							case VK_CONTROL:
								kvk[VK_CONTROL].usedAsPrefix = KeyType.PREFIX_FORCED;
								kvk[VK_LCONTROL].usedAsPrefix = KeyType.PREFIX_FORCED;
								kvk[VK_RCONTROL].usedAsPrefix = KeyType.PREFIX_FORCED;
								ksc[LControl].usedAsPrefix = KeyType.PREFIX_FORCED;
								ksc[RControl].usedAsPrefix = KeyType.PREFIX_FORCED;
								break;
						}

						break;

					default:  // vk is a left/right modifier key such as VK_LCONTROL or VK_LWIN:
						if (alwaysSetAsPrefix)
							kvk[vk].usedAsPrefix = KeyType.PREFIX_ACTUAL;
						else if (HotkeyDefinition.FindHotkeyContainingModLR(kvk[vk].asModifiersLR) != null) // Fixed for v1.0.35.13 (used to be aSC vs. aVK).
							kvk[vk].usedAsPrefix = KeyType.PREFIX_ACTUAL;

						break;
						// else allow its suffix action to fire when key is pressed down,
						// under the fairly safe assumption that the user hasn't configured
						// the opposite key to also be a key-down suffix-action (but even
						// if the user has done this, it's an explicit override of the
						// safety checks here, so probably best to allow it).
				}

				return;
			}

			// Since above didn't return, using scan code instead of virtual key:
			if (alwaysSetAsPrefix)
				ksc[sc].usedAsPrefix = KeyType.PREFIX_ACTUAL;
			else if (HotkeyDefinition.FindHotkeyContainingModLR(ksc[sc].asModifiersLR) != null)
				ksc[sc].usedAsPrefix = KeyType.PREFIX_ACTUAL;
		}

		internal string SCtoKeyName(uint sc, bool useFallback)
		{
			foreach (var kv in keyToSc)
				if (kv.Value == sc)
					return kv.Key;

			// Since above didn't return, no match was found.  Use the default format for an unknown scan code:
			return useFallback ? "sc" + sc.ToString("X3") : "";
		}

		protected internal virtual void Stop()
		{
			if (running)
			{
				try
				{
					channel?.Writer?.Complete();
				}
				catch (ChannelClosedException) { }

				if (channelReadThread != null && channelReadThread.Result != null && channelReadThread.Result.Status != TaskStatus.WaitingForActivation && !channelReadThread.Result.IsCompleted)
					channelReadThread?.Result?.Wait();

				channelReadThread?.Wait();
				channelReadThread = null;
				running = false;
			}
		}

		//This is relied upon to be unsigned; e.g. many places omit a check for ID < 0.
		internal abstract bool SystemHasAnotherKeybdHook();

		internal abstract bool SystemHasAnotherMouseHook();

		internal uint TextToSC(string text, ref bool? specifiedByNumber) =>
		TextToSC(text.AsSpan(), ref specifiedByNumber);

		internal virtual uint TextToSC(ReadOnlySpan<char> text, ref bool? specifiedByNumber)
		{
			if (text.Length == 0)
				return 0u;

			if (keyToScAlt.TryGetValue(text, out var val))
				return val;

			// Do this only after the above, in case any valid key names ever start with SC:
			if (char.ToUpper(text[0]) == 'S' && char.ToUpper(text[1]) == 'C')
			{
				var s = text.Slice(2);
				var digits = 0;

				foreach (var ch in s)
					if (ch.IsHex())
						digits++;

				var ok = uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var ii);

				if (!ok || (2 + digits < text.Length))
					return 0; // Fixed for v1.1.27: Disallow any invalid suffix so that hotkeys like a::scb() are not misinterpreted as remappings.

				if (specifiedByNumber != null)
					specifiedByNumber = true; // Override caller-set default.

				return ii;
			}

			return 0u; // Indicate "not found".
		}

		internal uint TextToSpecial(string text, ref KeyEventTypes eventType, ref uint modifiersLR, bool updatePersistent) =>
		TextToSpecial(text.AsSpan(), ref eventType, ref modifiersLR, updatePersistent);

		/// <summary>
		/// Returns vk for key-down, negative vk for key-up, or zero if no translation.
		/// We also update whatever's in *pModifiers and *pModifiersLR to reflect the type of key-action
		/// specified in <aText>.  This makes it so that {altdown}{esc}{altup} behaves the same as !{esc}.
		/// Note that things like LShiftDown are not supported because: 1) they are rarely needed; and 2)
		/// they can be down via "lshift down".
		/// </summary>
		/// <param name="text"></param>
		/// <param name="eventType"></param>
		/// <param name="modifiersLR"></param>
		/// <param name="updatePersistent"></param>
		/// <returns></returns>
		internal virtual uint TextToSpecial(ReadOnlySpan<char> text, ref KeyEventTypes eventType, ref uint modifiersLR, bool updatePersistent)
		{
			if (text.StartsWith("ALTDOWN", StringComparison.OrdinalIgnoreCase))
			{
				if (updatePersistent)
					if ((modifiersLR & (MOD_LALT | MOD_RALT)) == 0) // i.e. do nothing if either left or right is already present.
						modifiersLR |= MOD_LALT; // If neither is down, use the left one because it's more compatible.

				eventType = KeyEventTypes.KeyDown;
				return VK_MENU;
			}

			if (text.StartsWith("ALTUP", StringComparison.OrdinalIgnoreCase))
			{
				// Unlike for Lwin/Rwin, it seems best to have these neutral keys (e.g. ALT vs. LALT or RALT)
				// restore either or both of the ALT keys into the up position.  The user can use {LAlt Up}
				// to be more specific and avoid this behavior:
				if (updatePersistent)
					modifiersLR &= 0xF3;// ~(MOD_LALT | MOD_RALT);

				eventType = KeyEventTypes.KeyUp;
				return VK_MENU;
			}

			if (text.StartsWith("SHIFTDOWN", StringComparison.OrdinalIgnoreCase))
			{
				if (updatePersistent)
					if ((modifiersLR & (MOD_LSHIFT | MOD_RSHIFT)) == 0) // i.e. do nothing if either left or right is already present.
						modifiersLR |= MOD_LSHIFT; // If neither is down, use the left one because it's more compatible.

				eventType = KeyEventTypes.KeyDown;
				return VK_SHIFT;
			}

			if (text.StartsWith("SHIFTUP", StringComparison.OrdinalIgnoreCase))
			{
				if (updatePersistent)
					modifiersLR &= 0x49;// ~(MOD_LSHIFT | MOD_RSHIFT); // See "ALTUP" for explanation.

				eventType = KeyEventTypes.KeyUp;
				return VK_SHIFT;
			}

			if (text.StartsWith("CTRLDOWN", StringComparison.OrdinalIgnoreCase) || text.StartsWith("CONTROLDOWN", StringComparison.OrdinalIgnoreCase))
			{
				if (updatePersistent)
					if ((modifiersLR & (MOD_LCONTROL | MOD_RCONTROL)) == 0) // i.e. do nothing if either left or right is already present.
						modifiersLR |= MOD_LCONTROL; // If neither is down, use the left one because it's more compatible.

				eventType = KeyEventTypes.KeyDown;
				return VK_CONTROL;
			}

			if (text.StartsWith("CTRLUP", StringComparison.OrdinalIgnoreCase) || text.StartsWith("CONTROLUP", StringComparison.OrdinalIgnoreCase))
			{
				if (updatePersistent)
					modifiersLR &= 0xFC;// ~(MOD_LCONTROL | MOD_RCONTROL); // See "ALTUP" for explanation.

				eventType = KeyEventTypes.KeyUp;
				return VK_CONTROL;
			}

			if (text.StartsWith("LWINDOWN", StringComparison.OrdinalIgnoreCase))
			{
				if (updatePersistent)
					modifiersLR |= MOD_LWIN;

				eventType = KeyEventTypes.KeyDown;
				return VK_LWIN;
			}

			if (text.StartsWith("LWINUP", StringComparison.OrdinalIgnoreCase))
			{
				if (updatePersistent)
					modifiersLR &= 0xBF;// ~MOD_LWIN;

				eventType = KeyEventTypes.KeyUp;
				return VK_LWIN;
			}

			if (text.StartsWith("RWINDOWN", StringComparison.OrdinalIgnoreCase))
			{
				if (updatePersistent)
					modifiersLR |= MOD_RWIN;

				eventType = KeyEventTypes.KeyDown;
				return VK_RWIN;
			}

			if (text.StartsWith("RWINUP", StringComparison.OrdinalIgnoreCase))
			{
				if (updatePersistent)
					modifiersLR &= 0x7F;// ~MOD_RWIN;

				eventType = KeyEventTypes.KeyUp;
				return VK_RWIN;
			}

			// Otherwise, leave aEventType unchanged and return zero to indicate failure:
			return 0;
		}

		internal uint TextToVK(string text, ref uint? modifiersLR, bool excludeThoseHandledByScanCode, bool allowExplicitVK, nint keybdLayout) =>
		TextToVK(text.AsSpan(), ref modifiersLR, excludeThoseHandledByScanCode, allowExplicitVK, keybdLayout);

		/// <summary>
		/// If modifiers_p is non-NULL, place the modifiers that are needed to realize the key in there.
		/// e.g. M is really +m (shift-m), # is really shift-3.
		/// HOWEVER, this function does not completely overwrite the contents of pModifiersLR; instead, it just
		/// adds the required modifiers into whatever is already there.
		/// </summary>
		/// <param name="text"></param>
		/// <param name="modifiersLR"></param>
		/// <param name="excludeThoseHandledByScanCode"></param>
		/// <param name="allowExplicitVK"></param>
		/// <param name="keybdLayout"></param>
		/// <returns></returns>
		internal virtual uint TextToVK(ReadOnlySpan<char> text, ref uint? modifiersLR, bool excludeThoseHandledByScanCode, bool allowExplicitVK, nint keybdLayout)
		{
			if (text.Length == 0)
				return 0;

			if (keybdLayout == 0)
				keybdLayout = Script.TheScript.PlatformProvider.Manager.GetKeyboardLayout(0);

			// Don't trim() aText or modify it because that will mess up the caller who expects it to be unchanged.
			// Instead, for now, just check it as-is.  The only extra whitespace that should exist, due to trimming
			// of text during load, is that on either side of the COMPOSITE_DELIMITER (e.g. " then ").

			if (text.Length == 1) // _tcslen(aText) == 1
				return CharToVKAndModifiers(text[0], ref modifiersLR, keybdLayout); // Making this a function simplifies things because it can do early return, etc.

			if (allowExplicitVK && char.ToUpper(text[0]) == 'V' && char.ToUpper(text[1]) == 'K')
			{
				var s = text.Slice(2);
				var digits = 0;

				foreach (var ch in s)
					if (ch.IsHex())
						digits++;

				var ok = uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var ii);
				return !ok || (2 + digits < text.Length) ? 0 : ii; // Fixed for v1.1.27: Disallow any invalid suffix so that hotkeys like a::vkb() are not misinterpreted as remappings.
			}

			if (keyToVkAlt.TryGetValue(text, out var val))
				return val;

			if (excludeThoseHandledByScanCode)
				return 0; // Zero is not a valid virtual key, so it should be a safe failure indicator.

			// Otherwise check if aText is the name of a key handled by scan code and if so, map that
			// scan code to its corresponding virtual key:
			bool? dummy = null;
			var sc = TextToSC(text, ref dummy);
			return sc != 0 ? MapScToVk(sc) : 0;
		}

		internal bool TextToVKandSC(string text, ref uint vk, ref uint sc, ref uint? modifiersLR, nint keybdLayout) =>
		TextToVKandSC(text.AsSpan(), ref vk, ref sc, ref modifiersLR, keybdLayout);

		internal virtual bool TextToVKandSC(ReadOnlySpan<char> text, ref uint vk, ref uint sc, ref uint? modifiersLR, nint keybdLayout)
		{
			if ((vk = TextToVK(text, ref modifiersLR, true, true, keybdLayout)) != 0)
			{
				sc = 0; // Caller should call vk_to_sc(aVK) if needed.
				return true;
			}

			bool? dummy = null;

			if ((sc = TextToSC(text, ref dummy)) != 0)
			{
				return true;// Leave aVK set to 0.  Caller should call sc_to_vk(aSC) if needed.
			}

			if (text.StartsWith("vk", StringComparison.OrdinalIgnoreCase)) // Could be vkXXscXXX, which TextToVK() does not permit in v1.1.27+.
			{
				var vkIndex = text.IndexOf("vk", StringComparison.OrdinalIgnoreCase);
				var scIndex = text.IndexOf("sc", StringComparison.OrdinalIgnoreCase);

				if (vkIndex == 0 && scIndex > 2)
				{
					var vkStart = vkIndex + 2;
					var vkSpan = text.Slice(vkStart, scIndex - vkStart);
					var scStart = scIndex;
					var scSpan = text.Slice(scStart + 2);

					if (uint.TryParse(vkSpan, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var t1) &&
							uint.TryParse(scSpan, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var t2))
					{
						vk = t1;
						sc = t2;
						return true;
					}
				}
			}

			return false;
		}

		internal abstract void Unhook();

		internal abstract void Unhook(nint hook);

		internal void ResetHook(bool allModifiersUp = false, HookType whichHook = HookType.Keyboard | HookType.Mouse, bool resetKVKandKSC = false)
		// Caller should ensure that aWhichHook indicates at least one of the hooks (not none).
		{
			if (prefixKey != null)
			{
				// Reset pPrefixKey only if the corresponding hook is being reset.  This fixes
				// custom combo mouse hotkeys breaking when the prefix key does something which
				// causes the keyboard hook to be reset, or vice versa.
				bool isMouseKey = IsMouseVK(prefixKey.Pos);

				if ((whichHook & (isMouseKey ? HookType.Mouse : HookType.Keyboard)) != 0)
					prefixKey = null;
			}

			if (((uint)whichHook & HookMouse) != 0)
			{
				// Initialize some things, a very limited subset of what is initialized when the
				// keyboard hook is installed (see its comments).  This is might not everything
				// we should initialize, so further study is justified in the future:
				// FUTURE_USE_MOUSE_BUTTONS_LOGICAL
				//              g_mouse_buttons_logical = 0;
				//#endif
				physicalKeyState[VK_LBUTTON] = 0;
				physicalKeyState[VK_RBUTTON] = 0;
				physicalKeyState[VK_MBUTTON] = 0;
				physicalKeyState[VK_XBUTTON1] = 0;
				physicalKeyState[VK_XBUTTON2] = 0;
				// These are not really valid, since they can't be in a physically down state, but it's
				// probably better to have a false value in them:
				physicalKeyState[VK_WHEEL_DOWN] = 0;
				physicalKeyState[VK_WHEEL_UP] = 0;
				// Lexikos: Support horizontal scrolling in Windows Vista and later.
				physicalKeyState[VK_WHEEL_LEFT] = 0;
				physicalKeyState[VK_WHEEL_RIGHT] = 0;

				if (resetKVKandKSC)
				{
					kvk[VK_LBUTTON].ResetKeyTypeState();
					kvk[VK_RBUTTON].ResetKeyTypeState();
					kvk[VK_MBUTTON].ResetKeyTypeState();
					kvk[VK_XBUTTON1].ResetKeyTypeState();
					kvk[VK_XBUTTON2].ResetKeyTypeState();
					kvk[VK_WHEEL_DOWN].ResetKeyTypeState();
					kvk[VK_WHEEL_UP].ResetKeyTypeState();
					// Lexikos: Support horizontal scrolling in Windows Vista and later.
					kvk[VK_WHEEL_LEFT].ResetKeyTypeState();
					kvk[VK_WHEEL_RIGHT].ResetKeyTypeState();
				}
			}

			if (((uint)whichHook & HookKeyboard) != 0)
			{
				// Doesn't seem necessary to ever init g_KeyHistory or g_KeyHistoryNext here, since they were
				// zero-filled on startup.  But we do want to reset the below whenever the hook is being
				// installed after a (probably long) period during which it wasn't installed.  This is
				// because we don't know the current physical state of the keyboard and such:
				kbdMsSender.modifiersLRPhysical = 0;  // Best to make this zero, otherwise keys might get stuck down after a Send.
				kbdMsSender.modifiersLRNumpadMask = 0;
				kbdMsSender.modifiersLRCtrlAltDelMask = 0;
				kbdMsSender.modifiersLRLogical = kbdMsSender.modifiersLRLogicalNonIgnored = (allModifiersUp ? 0 : kbdMsSender.GetModifierLRState(true));
				System.Array.Clear(physicalKeyState, 0, physicalKeyState.Length);
				disguiseNextMenu = false;
				undisguisedMenuInEffect = false;
				// On Windows Vista and later, this definitely only works if the classic alt-tab menu
				// has been restored via the registry.  A non-NULL result is probably only helpful for
				// enabling the Esc key workaround in the hook (even though that isn't as critical on
				// Windows 7 as it was on XP, since on 7 the user can dismiss it with physical Esc).
				// A NULL result is probably more common, such as if it's been a while since the hook
				// was removed (or Alt was released).  If the *classic* alt-tab menu isn't in use,
				// this at least serves to reset altTabMenuIsVisible to false:
				altTabMenuIsVisible = GetAltTabMenuHandle() != 0;
				pendingDeadKeys.Clear();
				pendingDeadKeyInvisible = false;
				Script.TheScript.HotstringManager.ClearBuf();
				hsHwnd = 0; // It isn't necessary to determine the actual window/control at this point since the buffer is already empty.

				if (resetKVKandKSC)
				{
					for (var i = 0u; i < kvk.Length; ++i)
						if (!IsMouseVK(i))  // Don't do mouse VKs since those must be handled by the mouse section.
							kvk[i].ResetKeyTypeState();

					for (var i = 0; i < ksc.Length; ++i)
						ksc[i].ResetKeyTypeState();
				}
			}
		}

		/// <summary>
		/// Caller has ensured that vk has been translated from neutral to left/right if necessary.
		/// Always use the parameter vk rather than event.vkCode because the caller or caller's caller
		/// might have adjusted vk, namely to make it a left/right specific modifier key rather than a
		/// neutral one.
		/// </summary>
		internal void UpdateKeybdState(uint hookSC, ulong hookExtraInfo, bool isArtificial, uint vk, uint sc, bool keyUp, bool isSuppressed)
		{
			// If this function was called from SuppressThisKey(), these comments apply:
			// Currently SuppressThisKey is only called with a modifier in the rare case
			// when sDisguiseNextLWinUp/RWinUp is in effect.  But there may be other cases in the
			// future, so we need to make sure the physical state of the modifiers is updated
			// in our tracking system even though the key is being suppressed:
			uint modLR;

			if ((modLR = kvk[vk].asModifiersLR) != 0) // Update our tracking of LWIN/RWIN/RSHIFT etc.
			{
				// Caller has ensured that vk has been translated from neutral to left/right if necessary
				// (e.g. VK_CONTROL -> VK_LCONTROL). For this reason, always use the parameter vk rather
				// than the raw event.vkCode.
				// Below excludes KEY_IGNORE_ALL_EXCEPT_MODIFIER since that type of event shouldn't be ignored by
				// this function.  UPDATE: KEY_PHYS_IGNORE is now considered to be something that shouldn't be
				// ignored in this case because if more than one instance has the hook installed, it is
				// possible for kbdMsSender.modifiersLR_logical_non_ignored to say that a key is down in one instance when
				// that instance's kbdMsSender.modifiersLR_logical doesn't say it's down, which is definitely wrong.  So it
				// is now omitted below:
				var isNotIgnored = hookExtraInfo != KeyIgnore;
				var isFakeShift = hookSC == FakeLShift || hookSC == FakeRShift;
				var isFakeCtrl = hookSC == FakeLControl; // AltGr.
				var eventIsPhysical = !isFakeShift && KeybdEventIsPhysical(isArtificial, vk, keyUp);// For backward-compatibility, fake LCtrl is marked as physical.

				if (keyUp)
				{
					// Keep track of system-generated Shift-up events (as part of a workaround for
					// Shift becoming stuck due to interaction between Send and the system handling
					// of shift-numpad combinations).  Find "fake shift" for more details.
					if (isFakeShift)
						kbdMsSender.modifiersLRNumpadMask |= modLR;

					if (!isSuppressed)
					{
						kbdMsSender.modifiersLRLogical &= ~modLR;

						// Even if is_not_ignored == true, this is updated unconditionally on key-up events
						// to ensure that kbdMsSender.modifiersLR_logical_non_ignored never says a key is down when
						// kbdMsSender.modifiersLR_logical says its up, which might otherwise happen in cases such
						// as alt-tab.  See this comment further below, where the operative word is "relied":
						// "key pushed ALT down, or relied upon it already being down, so go up".  UPDATE:
						// The above is no longer a concern because KeyEvent() now defaults to the mode
						// which causes our var "is_not_ignored" to be true here.  Only the Send command
						// overrides this default, and it takes responsibility for ensuring that the older
						// comment above never happens by forcing any down-modifiers to be up if they're
						// not logically down as reflected in kbdMsSender.modifiersLR_logical.  There's more
						// explanation for kbdMsSender.modifiersLR_logical_non_ignored in keyboard_mouse.h:
						if (isNotIgnored)
							kbdMsSender.modifiersLRLogicalNonIgnored &= ~modLR;
					}

					if (eventIsPhysical) // Note that ignored events can be physical via KEYEVENT_PHYS()
					{
						kbdMsSender.modifiersLRPhysical &= ~modLR;
						physicalKeyState[vk] = 0;

						if (!isFakeCtrl)
							kbdMsSender.modifiersLRCtrlAltDelMask &= ~modLR;

						// If a modifier with an available neutral VK has been released, update the state
						// of the neutral VK to be that of the opposite key (the one that wasn't released):
						switch (vk)
						{
							case VK_LSHIFT: physicalKeyState[VK_SHIFT] = physicalKeyState[VK_RSHIFT]; break;

							case VK_RSHIFT: physicalKeyState[VK_SHIFT] = physicalKeyState[VK_LSHIFT]; break;

							case VK_LCONTROL: physicalKeyState[VK_CONTROL] = physicalKeyState[VK_RCONTROL]; break;

							case VK_RCONTROL: physicalKeyState[VK_CONTROL] = physicalKeyState[VK_LCONTROL]; break;

							case VK_LMENU: physicalKeyState[VK_MENU] = physicalKeyState[VK_RMENU]; break;

							case VK_RMENU: physicalKeyState[VK_MENU] = physicalKeyState[VK_LMENU]; break;
						}
					}

					kbdMsSender.modifiersLRLastPressed = 0;
				}
				else // Modifier key was pressed down.
				{
					kbdMsSender.modifiersLRNumpadMask &= ~modLR;

					if (!isSuppressed)
					{
						kbdMsSender.modifiersLRLogical |= modLR;

						if (isNotIgnored)
							kbdMsSender.modifiersLRLogicalNonIgnored |= modLR;
					}

					if (eventIsPhysical)
					{
						kbdMsSender.modifiersLRPhysical |= modLR;
						physicalKeyState[vk] = StateDown;

						if (!isFakeCtrl)
							kbdMsSender.modifiersLRCtrlAltDelMask |= modLR;

						// If a modifier with an available neutral VK has been pressed down (unlike LWIN & RWIN),
						// update the state of the neutral VK to be down also:
						switch (vk)
						{
							case VK_LSHIFT:
							case VK_RSHIFT: physicalKeyState[VK_SHIFT] = StateDown; break;

							case VK_LCONTROL:
							case VK_RCONTROL: physicalKeyState[VK_CONTROL] = StateDown; break;

							case VK_LMENU:
							case VK_RMENU: physicalKeyState[VK_MENU] = StateDown; break;
						}
					}

					// See comments in GetModifierLRState() for details about the following.
					kbdMsSender.modifiersLRLastPressed = modLR;
					kbdMsSender.modifiersLRLastPressedTime = DateTime.UtcNow;
				}
			} // vk is a modifier key.
		}

		/// <summary>
		/// Given a VK code, returns the character that an unmodified keypress would produce
		/// on the given keyboard layout.  Defaults to the script's own layout if omitted.
		/// Using this rather than MapVirtualKey() fixes some inconsistency that used to
		/// exist between 'A'-'Z' and every other key.
		/// </summary>
		/// <param name="vk"></param>
		/// <param name="keybdLayout"></param>
		/// <returns></returns>
		internal virtual char VKtoChar(uint vk, nint keybdLayout)
		{
			var platformManager = Script.TheScript.PlatformProvider.Manager;
			if (keybdLayout == 0)
				keybdLayout = platformManager.GetKeyboardLayout(0);

			// MapVirtualKeyEx() always produces 'A'-'Z' for those keys regardless of keyboard layout,
			// but for any other keys it produces the correct results, so we'll use it:
			if (vk > 'Z' || vk < 'A')
				return (char)platformManager.MapVirtualKeyToChar(vk, keybdLayout);

			// For any other keys,
			var ch = new char[3];
			var chNotUsed = new char[3];
			var keyState = new byte[256];
			var deadChar = (char)0;
			int n;

			// If there's a pending dead-key char in aKeybdLayout's buffer, it would modify the result.
			// We don't want that to happen, so as a workaround we pass a key-code which doesn't combine
			// with any dead chars, and will therefore pull it out.  VK_DECIMAL is used because it is
			// almost always valid; see http://www.siao2.com/2007/10/27/5717859.aspx
			if (platformManager.ToUnicode(VK_DECIMAL, 0, keyState, ch, 0, keybdLayout) == 2)
			{
				// Save the char to be later re-injected.
				deadChar = ch[0];
			}

			// Retrieve the character that corresponds to aVK, if any.
			n = platformManager.ToUnicode(vk, 0, keyState, ch, 0, keybdLayout);

			if (n < 0) // aVK is a dead key, and we've just placed it into aKeybdLayout's buffer.
			{
				// Flush it out in the same manner as before (see above).
				_ = platformManager.ToUnicode(VK_DECIMAL, 0, keyState, chNotUsed, 0, keybdLayout);
			}

			if (deadChar != (char)0)
			{
				// Re-inject the dead-key char so that user input is not interrupted.
				// To do this, we need to find the right VK and modifier key combination:
				uint? modLR = 0u;
				var dead_vk = CharToVKAndModifiers(deadChar, ref modLR, keybdLayout);

				if (dead_vk != 0)
				{
					AdjustKeyState(keyState, modLR.Value);
					_ = platformManager.ToUnicode(dead_vk, 0, keyState, chNotUsed, 0, keybdLayout);
				}

				//else: can't do it.
			}

			// ch[0] is set even for n < 0, but might not be for n == 0.
			return n != 0 ? ch[0] : (char)0;
		}

		internal string VKtoKeyName(uint vk, bool useFallback)
		{
			if (vkToKey.TryGetValue(vk, out var name))
				return name;

			// Since above didn't return, no match was found.  Try to map it to
			// a character or use the default format for an unknown key code:
			var ch = VKtoChar(vk, 0);

			if (ch != (char)0)
				return ch.ToString();
			else if (useFallback && vk != 0)
				return "vk" + vk.ToString("X2");
			else
				return DefaultObject;
		}

		internal virtual nint GetAltTabMenuHandle() => 0;

		internal virtual void WaitHookIdle()
		// Wait until the hook has reached a known idle state (i.e. finished any processing
		// that it was in the middle of, though it could start something new immediately after).
		{
			//Make sure this is not called within the channel thread because it would deadlock if so.
			if (channelThreadID != mgr.CurrentThreadId() && IsReadThreadRunning())
			{
				hookSynced = false;

				if (channel.Writer.TryWrite(new KeysharpMsg()
			{
				message = (uint)UserMessages.AHK_HOOK_SYNC
				}))
				{
					while (!hookSynced)
						Flow.SleepWithoutInterruption();
				}
			}
		}

		protected internal uint ChannelThreadId() => channelThreadID;

		protected internal abstract void DeregisterHooks();

		protected internal void FreeHookMem()
		{
			kvkm = [];
			kscm = [];
			hotkeyUp.Clear();
			kvk = [];
			ksc = [];
		}

		protected internal virtual void Start()
		{
			//If it's running there is no reason to start it again.
			if (IsReadThreadRunning())
				return;

			running = true;
			channelThreadID = 0;
			//This is a consolidation of the main windows proc, message sleep and the thread which they keyboard hook is created on.
			//Unsure how much of this is windows specific or can be cross platform. Will need to determine when we begin linux work.//TODO
			//If Start() is called while this thread is already running, the foreach will exit, and thus the previous thread will exit.
			channelReadThread = Task.Factory.StartNew(async () =>
			{
				try
				{
					Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;//AHK Sets this to critical which seems extreme.
					var reader = channel.Reader;
					channelThreadID = mgr.CurrentThreadId();

					await foreach (var item in reader.ReadAllAsync())//This should be totally reworked to use object types/casting rather than packing all manner of obscure meaning into bits and bytes of wparam and lparam.
						//while (true)
					{
						//if (!reader.TryRead(out var item))
						//{
						//  Flow.Sleep(10L);
						//  continue;
						//}
						//var item = await reader.ReadAsync();
						//var theasyncfunc = async () =>
						nint criterion_found_hwnd = 0;
						channelThreadID = mgr.CurrentThreadId();

						if (item is KeysharpMsg msg)
						{
							if (msg.message == WM_QUIT)//Needed to be pulled out of the case statement because it uses fallthrough logic which isn't allowed in C#.
								// After this message, fall through to the next case below so that the hooks will be removed before
								// exiting this thread.
								msg.wParam = 0; // Indicate to AHK_CHANGE_HOOK_STATE that both hooks should be deactivated.

							var wParamVal = msg.wParam.ToInt64();
							var lParamVal = msg.lParam.ToInt64();
							// ********
							// NO BREAK IN ABOVE, FALL INTO NEXT CASE:
							// ********
							var script = Script.TheScript;
							var tv = script.Threads.CurrentThread;
							tv.WaitForCriticalToFinish();//Must wait until the previous critical task finished before proceeding.

							switch (msg.message)
							{
								case (uint)UserMessages.AHK_CHANGE_HOOK_STATE: // No blank line between this in the above to indicate fall-through.
									// In this case, wParam contains the bitwise set of hooks that should be active.
									//Legacy, do nothing. Changing the hook state is handled elsewhere.
									break;

								case (uint)UserMessages.AHK_HOOK_SYNC:
									hookSynced = true;
									break;

								case (uint)UserMessages.AHK_HOOK_SET_KEYHISTORY:
									keyHistory = new KeyHistory((int)wParamVal);
									break;

								//These were taken from MsgSleep().
								case (uint)UserMessages.AHK_HOTSTRING:
									if (msg.obj is HotstringMsg hmsg)
									{
										var hs = hmsg.hs;

										if (hs.hotCriterion != null)
										{
											// For details, see comments in the hotkey section of this switch().
											criterion_found_hwnd = new nint(HotkeyDefinition.HotCriterionAllowsFiring(hs.hotCriterion, hs.Name));

											if (criterion_found_hwnd == 0L)
												// Hotstring is no longer eligible to fire even though it was when the hook sent us
												// the message.  Abort the firing even though the hook may have already started
												// executing the hotstring by suppressing the final end-character or other actions.
												// It seems preferable to abort midway through the execution than to continue sending
												// keystrokes to the wrong window, or when the hotstring has become suspended.
												continue;

											if (hs.hotCriterion is IFuncObj fc && !(string.Compare(fc.Name, "HotIfWinNotActivePrivate", true) == 0 || string.Compare(fc.Name, "HotIfWinNotExistPrivate", true) == 0))
												criterion_found_hwnd = 0;
											else if (hs.HotIfRequiresEval())
												criterion_found_hwnd = script.hotExprLFW;// For #if WinExist(WinTitle) and similar.
										}
										else // No criterion, so it's a global hotstring.  It can always fire, but it has no "last found window".
											criterion_found_hwnd = 0;

										// Do a simple replacement for the hotstring if that's all that's called for.
										// Don't create a new quasi-thread or any of that other complexity done further
										// below.  But also do the backspacing (if specified) for a non-autoreplace hotstring,
										// even if it can't launch due to MaxThreads, MaxThreadsPerHotkey, or some other reason:
										//Any key sending must be on the main thread else keys will come in out of order.
										//Does only the backspacing if it's not an auto-replace hotstring.
										script.mainWindow.CheckedInvoke(() => hs.DoReplace(hmsg.caseMode, hmsg.endChar), true);

										if (string.IsNullOrEmpty(hs.replacement))
										{
											// Otherwise, continue on and let a new thread be created to handle this hotstring.
											// Since this isn't an auto-replace hotstring, set this value to support
											// the built-in variable A_EndChar:
											_ = hs.PerformInNewThreadMadeByCaller(criterion_found_hwnd, hmsg.endChar.ToString());
										}
										else
											continue;
									}

									break;

								case WM_HOTKEY://Some hotkeys are handled directly by windows using WndProc(), others, such as those with left/right modifiers, are handled directly by us.
								case (uint)UserMessages.AHK_HOOK_HOTKEY://Some hotkeys are handled directly by windows using WndProc(), others, such as those with left/right modifiers, are handled directly by us.
								{
									script.HookThread.kbdMsSender.ProcessHotkey((int)wParamVal, (int)lParamVal, msg.obj as HotkeyVariant, msg.message);
									break;
								}

								//case (uint)UserMessages.AHK_HOTSTRING: // Added for v1.0.36.02 so that hotstrings work even while an InputBox or other non-standard msg pump is running.
								//case (uint)UserMessages.AHK_CLIPBOARD_CHANGE: //Probably not needed because we handle OnClipboardChange() differently. Added for v1.0.44 so that clipboard notifications aren't lost while the script is displaying a MsgBox or other dialog.
								case (uint)UserMessages.AHK_INPUT_END:

									// If the following facts are ever confirmed, there would be no need to post the message in cases where
									// the MsgSleep() won't be done:
									// 1) The mere fact that any of the above messages has been received here in MainWindowProc means that a
									//    message pump other than our own main one is running (i.e. it is the closest pump on the call stack).
									//    This is because our main message pump would never have dispatched the types of messages above because
									//    it is designed to fully handle then discard them.
									// 2) All of these types of non-main message pumps would discard a message with a NULL hwnd.
									//
									// One source of confusion is that there are quite a few different types of message pumps that might
									// be running:
									// - InputBox/MsgBox, or other dialog
									// - Popup menu (tray menu, popup menu from Menu command, or context menu of an Edit/MonthCal, including
									//   our main window's edit control g_hWndEdit).
									// - Probably others, such as ListView marquee-drag, that should be listed here as they are
									//   remembered/discovered.
									//
									// Due to maintainability and the uncertainty over backward compatibility (see comments above), the
									// following message is posted even when INTERRUPTIBLE==false.
									// Post it with a NULL hwnd (update: also for backward compatibility) to avoid any chance that our
									// message pump will dispatch it back to us.  We want these events to always be handled there,
									// where almost all new quasi-threads get launched.  Update: Even if it were safe in terms of
									// backward compatibility to change NULL to gHwnd, testing shows it causes problems when a hotkey
									// is pressed while one of the script's menus is displayed (at least a menu bar).  For example:
									// *LCtrl::Send {Blind}{Ctrl up}{Alt down}
									// *LCtrl up::Send {Blind}{Alt up}
									//PostMessage(NULL, iMsg, wParam, lParam);
									//
									//if (IsInterruptible())
									//  MsgSleep(-1, RETURN_AFTER_MESSAGES_SPECIAL_FILTER);
									//else let the other pump discard this hotkey event since in most cases it would do more harm than good
									// (see comments above for why the message is posted even when it is 90% certain it will be discarded
									// in all cases where MsgSleep isn't done).
									//return 0;
									if (tv.priority == 0 && script.Threads.AnyThreadsAvailable())
									{
										if (msg.obj is InputType it
												&& it.InputRelease() is InputType inputHook
												&& inputHook.scriptObject is InputObject so)
										{
											if (so.OnEnd is Any kso)
											{
												script.Threads.LaunchThreadInMain(() => Script.Invoke(kso, "Call", so));
											}
										}
										else
											continue;
									}
									else
										//continue;
										continue;

									break;

								case (uint)UserMessages.AHK_INPUT_KEYDOWN:
								case (uint)UserMessages.AHK_INPUT_CHAR:
								case (uint)UserMessages.AHK_INPUT_KEYUP:
								{
									InputType input_hook;
									var inputHookParam = msg.obj as InputType;

									for (input_hook = script.input; input_hook != null && input_hook != inputHookParam; input_hook = input_hook.prev)
									{
									}

									if (input_hook == null)
										continue;

									if ((msg.message == (uint)UserMessages.AHK_INPUT_KEYDOWN ? input_hook.scriptObject.OnKeyDown
											: msg.message == (uint)UserMessages.AHK_INPUT_KEYUP ? input_hook.scriptObject.OnKeyUp
											: input_hook.scriptObject.OnChar) is Any kso
											&& script.Threads.AnyThreadsAvailable())
									{
										var args = msg.message == (uint)UserMessages.AHK_INPUT_CHAR ?//AHK_INPUT_CHAR passes the chars as a string, whereas the rest pass them individually.
												   new object[] { input_hook.scriptObject, new string(wParamVal == 0 ? new char[] { (char)lParamVal } : new char[] { (char)lParamVal, (char)wParamVal }) }
												   : [input_hook.scriptObject, lParamVal, wParamVal];
											script.Threads.LaunchThreadInMain(() => Script.Invoke(kso, "Call", args));
									}
									else
										continue;
								}
								break;

								default:
									break;
							}

							//This is not going to work. It's always going to comapre against this queue thread's priority, not the priority of whichever hotkey/string is currently executing.
							//None of this will work until we implement real threads.
							//TODO
							//if (priority < (long)Accessors.A_Priority)
							//  continue;
							//Original tries to do some type of thread init here.//TOOD
							script.lastPeekTime = DateTime.UtcNow;
						}
					}

					System.Diagnostics.Debug.WriteLine("Exiting reader channel.");
				}
				catch (Exception ex)
				{
					_ = KeysharpEnhancements.OutputDebugLine($"Windows hook thread exited unexpectedly: {ex}");
				}
				finally
				{
					running = false;
					Thread.CurrentThread.Priority = ThreadPriority.Normal;
				}
			}
			//Unsure if this will work or is needed here.
			//,CancellationToken.None, TaskCreationOptions.None,
			//SynchronizationContext.Current != null ? TaskScheduler.FromCurrentSynchronizationContext() : TaskScheduler.Current
													 );

			while (channelThreadID == 0)//Give it some time to startup before proceeding.
				Thread.Sleep(10);
		}
	}

	// WM_USER (0x0400) is the lowest number that can be a user-defined message.  Anything above that is also valid.
	// NOTE: Any msg about WM_USER will be kept buffered (unreplied-to) whenever the script is uninterruptible.
	// If this is a problem, try making the msg have an ID less than WM_USER via a technique such as that used
	// for AHK_USER_MENU (perhaps WM_COMMNOTIFY can be "overloaded" to contain more than one type of msg).
	// Also, it has been announced in OnMessage() that message numbers between WM_USER and 0x1000 are earmarked
	// for possible future use by the program, so don't use a message above 0x1000 without good reason.
	//Make sure the minimum value of WM_USER (0x0400) is ok for linux.//TODO
	internal enum UserMessages : uint
	{
		AHK_HOOK_HOTKEY = 0x0400, AHK_HOTSTRING, AHK_USER_MENU, AHK_DIALOG, AHK_NOTIFYICON
		, AHK_UNUSED_MSG, AHK_EXIT_BY_RELOAD, AHK_EXIT_BY_SINGLEINSTANCE, AHK_CHECK_DEBUGGER

		// Allow some room here in between for more "exit" type msgs to be added in the future (see below comment).
		, AHK_GUI_ACTION = 0x0400 + 20 // Avoid WM_USER+100/101 and vicinity.  See below comment.

		// v1.0.43.05: On second thought, it seems better to stay close to WM_USER because the OnMessage page
		// documents, "it is best to choose a number greater than 4096 (0x1000) to the extent you have a choice.
		// This reduces the chance of interfering with messages used internally by current and future versions..."
		// v1.0.43.03: Changed above msg number because Micha reports that previous number (WM_USER+100) conflicts
		// with msgs sent by HTML control (AHK_CLIPBOARD_CHANGE) and possibly others (I think WM_USER+100 may be the
		// start of a range used by other common controls too).  So trying a higher number that's (hopefully) very
		// unlikely to be used by OS features.
		, AHK_CLIPBOARD_CHANGE, AHK_HOOK_TEST_MSG, AHK_CHANGE_HOOK_STATE, AHK_GETWINDOWTEXT

		, AHK_HOT_IF_EVAL   // HotCriterionAllowsFiring uses this to ensure expressions are evaluated only on the main thread.
		, AHK_HOOK_SYNC // For WaitHookIdle().
		, AHK_INPUT_END, AHK_INPUT_KEYDOWN, AHK_INPUT_CHAR, AHK_INPUT_KEYUP
		, AHK_HOOK_SET_KEYHISTORY
		, AHK_START_LOOP
	}

	// NOTE: TRY NEVER TO CHANGE the specific numbers of the above messages, since some users might be
	// using the Post/SendMessage commands to automate AutoHotkey itself.  Here is the original order
	// that should be maintained:
	// AHK_HOOK_HOTKEY = WM_USER, AHK_HOTSTRING, AHK_USER_MENU, AHK_DIALOG, AHK_NOTIFYICON, AHK_RETURN_PID
}
