using static Keysharp.Core.Common.Keyboard.KeyboardUtils;
using static Keysharp.Core.Common.Keyboard.VirtualKeys;

namespace Keysharp.Core.Common.Keyboard
{
	/// <summary>
	/// Screen coordinates, which can be negative.  SHORT vs. INT is used because the likelihood
	/// have having a virtual display surface wider or taller than 32,767 seems too remote to
	/// justify increasing the struct size, which would impact the stack space and dynamic memory
	/// used by every script every time it uses the playback method to send keystrokes or clicks.
	/// Note: WM_LBUTTONDOWN uses WORDs vs. SHORTs, but they're not really comparable because
	/// journal playback/record both use screen coordinates but WM_LBUTTONDOWN et. al. use client
	/// coordinates.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	internal struct Pt
	{
		internal short x, y;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct ScVk
	{
		internal ushort sc; // Placed above vk for possibly better member stacking/alignment.
		internal byte vk;
	}

	/// <summary>
	/// Platform independent keyboard Hook base.
	/// This Class is abstract.
	/// </summary>
	internal abstract class KeyboardMouseSender
	{
		//Need to figure out if these should be signed or not. Weird bugs can happen with wraparound comparisons if you get it wrong.//TODO
		internal const int CoordCentered = int.MinValue + 1;

		internal const int CoordModeCaret = 6;
		internal const int CoordModeClient = 0;
		internal const int CoordModeInvalid = -1;
		internal const int CoordModeMask = 3;
		internal const int CoordModeMenu = 8;
		internal const int CoordModeMouse = 2;
		internal const int CoordUnspecified = int.MinValue;
		internal const short CoordUnspecifiedShort = short.MinValue;
		internal const int HookFail = 0xFF;
		internal const int HookKeyboard = 0x01;
		internal const int HookMouse = 0x02;
		internal const long KeyBlockThis = KeyIgnore + 1;

		// Below uses a pseudo-random value.It's best that this be constant so that if multiple instances
		// of the app are running, they will all ignore each other's keyboard & mouse events.  Also, a value
		// close to UINT_MAX might be a little better since it's might be less likely to be used as a pointer
		// value by any apps that send keybd events whose ExtraInfo is really a pointer value.
		internal const long KeyIgnore = 0xFFC3D44F;

		internal const long KeyIgnoreAllExceptModifier = KeyIgnore - 2;
		internal const long KeyIgnoreMax = KeyIgnore;
		internal const long KeyPhysIgnore = KeyIgnore - 1;
		internal const long MaxMouseSpeed = 100L;
		internal const string ModLRString = "<^>^<!>!<+>+<#>#";
		internal const uint MsgOffsetMouseMove = 0x80000000;
		internal const long SendLevelMax = 100L;
		internal const int StateDown = 0x80;
		internal const int StateOn = 0x01;
		internal static char[] bracechars = "{}".ToCharArray();
		internal static string[] CoordModes = ["Client", "Window", "Screen"];
		internal static char[] llChars = "Ll".ToCharArray();
		internal bool abortArraySend = false;
		internal long altGrExtraInfo = 0L;
		internal SearchValues<char> bracecharsSv = SearchValues.Create(bracechars);
		internal SearchValues<char> llCharsSv = SearchValues.Create(llChars);
		internal int maxEvents = 0;
		internal uint eventModifiersLR;
		internal uint modifiersLRCtrlAltDelMask = 0u;
		internal uint modifiersLRLastPressed = 0u;
		internal DateTime modifiersLRLastPressedTime = DateTime.UtcNow;
		internal uint modifiersLRLogical = 0u;
		internal uint modifiersLRLogicalNonIgnored = 0u;
		internal uint modifiersLRNumpadMask = 0u;
		internal uint modifiersLRPhysical = 0u;
		internal KeyType prefixKey = null;
		internal string sendKeyChars = "^+!#{}";
		internal uint thisHotkeyModifiersLR;
		internal bool triedKeyUp;
		internal bool inBlindMode;
		internal uint modifiersLRPersistent;
		internal uint modifiersLRRemapped;
		protected internal PlatformManagerBase mgr = Script.TheScript.PlatformProvider.Manager;
		protected ArrayPool<byte> keyStatePool = ArrayPool<byte>.Create(256, 100);
		protected SendModes sendMode = SendModes.Event;//Note this is different than the one in Accessors and serves as a temporary.
		private const int retention = 1024;
		private readonly StringBuilder caser = new (32);
		private readonly List<HotkeyDefinition> hotkeys;
		private readonly List<HotstringDefinition> hotstrings;
		private readonly Dictionary<Keys, bool> pressed;

		internal nint targetKeybdLayout;
		// Set by SendKeys() for use by the functions it calls directly and indirectly.
		internal ResultType targetLayoutHasAltGr;

		public KeyboardMouseSender()
		{
			hotkeys = [];
			hotstrings = [];
			pressed = [];

			foreach (int i in Enum.GetValues(typeof(Keys)))
				_ = pressed.TryAdd((Keys)i, false);

			RegisterHook();
		}

		public string ApplyCase(string typedstr, string hotstr)
		{
			var typedlen = typedstr.Length;
			var hotlen = hotstr.Length;
			_ = caser.Clear();

			for (int i = 0, j = 0; i < typedlen && j < hotlen;)
			{
				var ch1 = typedstr[i];
				var ch2 = hotstr[j];

				if (char.ToUpperInvariant(ch1) == char.ToUpperInvariant(ch2))
				{
					_ = caser.Append(ch1);
					i++;
				}
				else
					_ = caser.Append(ch2);

				j++;
			}

			return caser.ToString();
		}

		public bool IsPressed(Keys key)
		{
			if (pressed.ContainsKey(key))
				return pressed[key];
			else
			{
				System.Diagnostics.Debug.Fail("Thre should'nt be any key not in this table...");
				return false;
			}
		}

		internal static bool HotInputLevelAllowsFiring(long inputLevel, ulong aEventExtraInfo, ref char? aKeyHistoryChar)
		{
			if (InputLevelFromInfo((long)aEventExtraInfo) <= inputLevel)
			{
				if (aKeyHistoryChar != null)
					aKeyHistoryChar = 'i'; // Mark as ignored in KeyHistory

				return false;
			}

			return true;
		}

		internal static long InputLevelFromInfo(long aExtraInfo)
		{
			if (aExtraInfo >= KeyIgnoreMin() && aExtraInfo <= KeyIgnoreMax)
				return KeyIgnoreLevel(0) - aExtraInfo;

			return SendLevelMax + 1;
		}

		/// <summary>
		/// KEY_PHYS_IGNORE events must be mostly ignored because currently there is no way for a given
		/// hook instance to detect if it sent the event or some other instance.  Therefore, to treat
		/// such events as true physical events might cause infinite loops or other side-effects in
		/// the instance that generated the event.  More review of this is needed if KEY_PHYS_IGNORE
		/// events ever need to be treated as true physical events by the instances of the hook that
		/// didn't originate them. UPDATE: The foregoing can now be accomplished using SendLevel.
		/// </summary>
		/// <param name="aExtraInfo"></param>
		/// <returns></returns>
		internal static bool IsIgnored(ulong val) => val == KeyIgnore || val == KeyPhysIgnore || val == KeyIgnoreAllExceptModifier;

		// Same as above but marked as physical for other instances of the hook.
		// Non-physical and ignored only if it's not a modifier.
		// Same as KEY_IGNORE_ALL_EXCEPT_MODIFIER, but only ignored by Hotkeys & Hotstrings at InputLevel LEVEL and below.
		// The levels are set up to use negative offsets from KEY_IGNORE_ALL_EXCEPT_MODIFIER so that we can leave
		// the values above unchanged and have KEY_IGNORE_LEVEL(0) == KEY_IGNORE_ALL_EXCEPT_MODIFIER.
		//
		// In general, KEY_IGNORE_LEVEL(g->SendLevel) should be used for any input that's meant to be treated as "real",
		// as opposed to input generated for side effects (e.g., masking modifier keys to prevent default OS responses).
		// A lot of the calls that generate input fall into the latter category, so KEY_IGNORE_ALL_EXCEPT_MODIFIER
		// (aka KEY_IGNORE_LEVEL(0)) still gets used quite often.
		//
		// Note that there are no "level" equivalents for KEY_IGNORE or KEY_PHYS_IGNORE (only KEY_IGNORE_ALL_EXCEPT_MODIFIER).
		// For the KEY_IGNORE_LEVEL use cases, there isn't a need to ignore modifiers or differentiate between physical
		// and non-physical, and leaving them out keeps the code much simpler.
		internal static long KeyIgnoreLevel(long level) => KeyIgnoreAllExceptModifier - level;

		internal static long KeyIgnoreMin() => KeyIgnoreLevel(SendLevelMax);

		internal static bool SendLevelIsValid(long level) => level >= 0 && level <= SendLevelMax;

		internal HotkeyDefinition Add(HotkeyDefinition hotkey)
		{
			hotkeys.Add(hotkey);
			return hotkey;
		}

		internal HotstringDefinition Add(HotstringDefinition hotstring)
		{
			hotstrings.Add(hotstring);//This will not check for duplicates.
			return hotstring;
		}

		internal abstract void CleanupEventArray(long aFinalKeyDelay);

		internal abstract void DoKeyDelay(long delay);

		internal abstract void DoMouseDelay();

		internal abstract nint GetFocusedKeybdLayout(nint aWindow);

		internal abstract uint GetModifierLRState(bool aExplicitlyGet = false);

		internal abstract void InitEventArray(int maxEvents, uint aModifiersLR);

		internal string ModifiersLRToText(uint aModifiersLR)
		{
			var sb = new StringBuilder(64);

			if ((aModifiersLR & MOD_LWIN) != 0) _ = sb.Append("LWin ");

			if ((aModifiersLR & MOD_RWIN) != 0) _ = sb.Append("RWin ");

			if ((aModifiersLR & MOD_LSHIFT) != 0) _ = sb.Append("LShift ");

			if ((aModifiersLR & MOD_RSHIFT) != 0) _ = sb.Append("RShift ");

			if ((aModifiersLR & MOD_LCONTROL) != 0) _ = sb.Append("LCtrl ");

			if ((aModifiersLR & MOD_RCONTROL) != 0) _ = sb.Append("RCtrl ");

			if ((aModifiersLR & MOD_LALT) != 0) _ = sb.Append("LAlt ");

			if ((aModifiersLR & MOD_RALT) != 0) _ = sb.Append("RAlt ");

			return sb.ToString();
		}

		internal abstract ResultType LayoutHasAltGrDirect(nint layout);

		internal abstract void MouseClick(uint aVK, int aX, int aY, long aRepeatCount, long aSpeed, KeyEventTypes aEventType, bool aMoveOffset);

		internal abstract void MouseClickDrag(uint vk, int x1, int y1, int x2, int y2, long speed, bool relative);

		internal abstract void MouseEvent(uint aEventFlags, uint aData, int aX = CoordUnspecified, int aY = CoordUnspecified);

		internal abstract void MouseMove(ref int aX, ref int aY, ref uint aEventFlags, long aSpeed, bool aMoveOffset);

		internal abstract int PbEventCount();

		internal void PerformMouseCommon(Actions actionType, uint vk, int x1, int y1, int x2, int y2,
										 long repeatCount, KeyEventTypes eventType, long speed, bool relative)
		{
			// The maximum number of events, which in this case would be from a MouseClickDrag.  To be conservative
			// (even though INPUT is a much larger struct than PlaybackEvent and SendInput doesn't use mouse-delays),
			// include room for the maximum number of mouse delays too.
			// Drag consists of at most:
			// 1) Move; 2) Delay; 3) Down; 4) Delay; 5) Move; 6) Delay; 7) Delay (dupe); 8) Up; 9) Delay.
			const int MAX_PERFORM_MOUSE_EVENTS = 10;
			var script = Script.TheScript;
			var ht = script.HookThread;
			sendMode = ThreadAccessors.A_SendMode;

			if (sendMode == SendModes.Input || sendMode == SendModes.InputThenPlay)
			{
				if (ht.SystemHasAnotherMouseHook()) // See similar section in SendKeys() for details.
					sendMode = (sendMode == SendModes.Input) ? SendModes.Event : SendModes.Play;
				else
					sendMode = SendModes.Input; // Resolve early so that other sections don't have to consider SM_INPUT_FALLBACK_TO_PLAY a valid value.
			}

			if (sendMode != SendModes.Event) // We're also responsible for setting sSendMode to SM_EVENT prior to returning.
				InitEventArray(MAX_PERFORM_MOUSE_EVENTS, 0);

			// Turn it on unconditionally even if it was on, since Ctrl-Alt-Del might have disabled it.
			// Turn it back off only if it wasn't ON before we started.
			var blockinputPrev = script.KeyboardData.blockInput;
			var doSelectiveBlockinput = (script.KeyboardData.blockInputMode == ToggleValueType.Mouse
										 || script.KeyboardData.blockInputMode == ToggleValueType.SendAndMouse)
										&& sendMode == SendModes.Event;

			if (doSelectiveBlockinput) // It seems best NOT to use g_BlockMouseMove for this, since often times the user would want keyboard input to be disabled too, until after the mouse event is done.
				_ = Core.Keyboard.ScriptBlockInput(true); // Turn it on unconditionally even if it was on, since Ctrl-Alt-Del might have disabled it.

			switch (actionType)
			{
				case Actions.ACT_MOUSEMOVE:
					var unused = 0u;
					MouseMove(ref x1, ref y1, ref unused, speed, relative); // Does nothing if coords are invalid.
					break;

				case Actions.ACT_MOUSECLICK:
					MouseClick(vk, x1, y1, repeatCount, speed, eventType, relative); // Does nothing if coords are invalid.
					break;

				case Actions.ACT_MOUSECLICKDRAG:
					MouseClickDrag(vk, x1, y1, x2, y2, speed, relative); // Does nothing if coords are invalid.
					break;
			}

			if (sendMode != SendModes.Event)
			{
				var finalKeyDelay = -1L; // Set default.

				if (!abortArraySend && TotalEventCount() > 0)
					SendEventArray(ref finalKeyDelay, 0); // Last parameter is ignored because keybd hook isn't removed for a pure-mouse SendInput.

				CleanupEventArray(finalKeyDelay);
			}

			if (doSelectiveBlockinput && !blockinputPrev)  // Turn it back off only if it was off before we started.
				_ = Core.Keyboard.ScriptBlockInput(false);
		}

		internal void ProcessHotkey(int wParamVal, int lParamVal, HotkeyVariant variant, uint msg)
		{
			var hkId = wParamVal & HotkeyDefinition.HOTKEY_ID_MASK;
			var script = Script.TheScript;
			var shk = script.HotkeyData.shk;

			if (hkId < shk.Count)//Ensure hotkey ID is valid.
			{
				var hk = shk[hkId];
				// Check if criterion allows firing.
				// For maintainability, this is done here rather than a little further down
				// past the g_MaxThreadsTotal and thread-priority checks.  Those checks hardly
				// ever abort a hotkey launch anyway.
				//
				// If message is WM_HOTKEY, it's either:
				// 1) A joystick hotkey from TriggerJoyHotkeys(), in which case the lParam is ignored.
				// 2) A hotkey message sent by the OS, in which case lParam contains currently-unused info set by the OS.
				//
				// An incoming WM_HOTKEY can be subject to #HotIf Win. at this stage under the following conditions:
				// 1) Joystick hotkey, because it relies on us to do the check so that the check is done only
				//    once rather than twice.
				// 2) #HotIf Win. keybd hotkeys that were made non-hook because they have a non-suspended, global variant.
				//
				// If message is AHK_HOOK_HOTKEY:
				// Rather than having the hook pass the qualified variant to us, it seems preferable
				// to search through all the criteria again and rediscover it.  This is because conditions
				// may have changed since the message was posted, and although the hotkey might still be
				// eligible for firing, a different variant might now be called for (e.g. due to a change
				// in the active window).  Since most criteria hotkeys have at most only a few criteria,
				// and since most such criteria are #HotIf WinActive rather than Exist, the performance will
				// typically not be reduced much at all.  Furthermore, trading performance for greater
				// reliability seems worth it in this case.
				//
				// The inefficiency of calling HotCriterionAllowsFiring() twice for each hotkey --
				// once in the hook and again here -- seems justified for the following reasons:
				// - It only happens twice if the hotkey a hook hotkey (multi-variant keyboard hotkeys
				//   that have a global variant are usually non-hook, even on NT/2k/XP).
				// - The hook avoids doing its first check of WinActive/Exist if it sees that the hotkey
				//   has a non-suspended, global variant.  That way, hotkeys that are hook-hotkeys for
				//   reasons other than #HotIf Win. (such as mouse, overriding OS hotkeys, or hotkeys
				//   that are too fancy for RegisterHotkey) will not have to do the check twice.
				// - It provides the ability to set the last-found-window for #HotIf WinActive/Exist
				//   (though it's not needed for the "Not" counterparts).  This HWND could be passed
				//   via the message, but that would require malloc-there and free-here, and might
				//   result in memory leaks if its ever possible for messages to get discarded by the OS.
				// - It allows hotkeys that were eligible for firing at the time the message was
				//   posted but that have since become ineligible to be aborted.  This seems like a
				//   good precaution for most users/situations because such hotkey subroutines will
				//   often assume (for scripting simplicity) that the specified window is active or
				//   exists when the subroutine executes its first line.
				// - Most criterion hotkeys use #HotIf WinActive(), which is a very fast call.  Also, although
				//   WinText and/or "SetTitleMatchMode 'Slow'" slow down window searches, those are rarely
				//   used too.
				//
				char? dummy = null;
				var criterion_found_hwnd = 0L;

				if (!(variant != null || (variant = hk.CriterionAllowsFiring(ref criterion_found_hwnd, (ulong)(msg == (uint)UserMessages.AHK_HOOK_HOTKEY ? KeyIgnoreLevel((uint)Conversions.HighWord(lParamVal)) : 0L), ref dummy)) != null))
					return;

				if (!script.Threads.AnyThreadsAvailable())//First test global thread count.
					return;

				// If this is AHK_HOOK_HOTKEY, criterion was eligible at time message was posted,
				// but not now.  Seems best to abort (see other comments).
				// Due to the key-repeat feature and the fact that most scripts use a value of 1
				// for their #MaxThreadsPerHotkey, this check will often help average performance
				// by avoiding a lot of unnecessary overhead that would otherwise occur:
				if (!variant.AnyThreadsAvailable())//Then test local thread count.
				{
					// The key is buffered in this case to boost the responsiveness of hotkeys
					// that are being held down by the user to activate the keyboard's key-repeat
					// feature.  This way, there will always be one extra event waiting in the queue,
					// which will be fired almost the instant the previous iteration of the subroutine
					// finishes (this above description applies only when MaxThreadsPerHotkey is 1,
					// which it usually is).
					variant.RunAgainAfterFinished(); // Wheel notch count (g->EventInfo below) should be okay because subsequent launches reuse the same thread attributes to do the repeats.
					return;
				}

				var tv = script.Threads.CurrentThread;

				// Now that above has ensured variant is non-NULL:
				if (variant.priority >= tv.priority)//Finally, test priority.
				{
					// Above also works for RunAgainAfterFinished since that feature reuses the same thread attributes set above.
					hk.PerformInNewThreadMadeByCallerAsync(variant, criterion_found_hwnd, lParamVal);
				}
			}
		}

		internal void Remove(HotkeyDefinition hotkey) => _ = hotkeys.Remove(hotkey);

		internal void Remove(HotstringDefinition hotstring) => _ = hotstrings.Remove(hotstring);

		internal abstract void SendEventArray(ref long aFinalKeyDelay, uint aModsDuringSend);

		internal abstract void SendKey(uint aVK, uint aSC, uint aModifiersLR, uint aModifiersLRPersistent
									   , long aRepeatCount, KeyEventTypes aEventType, uint aKeyAsModifiersLR, nint aTargetWindow
									   , int aX = CoordUnspecified, int aY = CoordUnspecified, bool aMoveOffset = false);

		internal abstract void SendKeyEventMenuMask(KeyEventTypes aEventType, long aExtraInfo = KeyIgnoreAllExceptModifier);

		internal abstract void SendKeys(string aKeys, SendRawModes aSendRaw, SendModes aSendModeOrig, nint aTargetWindow);

		internal abstract int SiEventCount();

		internal abstract ToggleValueType ToggleKeyState(uint vk, ToggleValueType toggleValue);

		internal int TotalEventCount() => PbEventCount() + SiEventCount();

		protected internal abstract void LongOperationUpdate();

		protected internal abstract void LongOperationUpdateForSendKeys();

		//protected internal abstract void Send(string keys);

		//protected internal abstract void Send(Keys key);
		protected internal abstract void SendKeyEvent(KeyEventTypes aEventType, uint aVK, uint aSC = 0u, nint aTargetWindow = default, bool aDoKeyDelay = false, long aExtraInfo = KeyIgnoreAllExceptModifier);

		protected abstract void RegisterHook();

				/// <summary>
		/// This function is designed to be called from only the main thread; it's probably not thread-safe.
		/// Puts modifiers into the specified state, releasing or pressing down keys as needed.
		/// The modifiers are released and pressed down in a very delicate order due to their interactions with
		/// each other and their ability to show the Start Menu, activate the menu bar, or trigger the OS's language
		/// bar hotkeys.  Side-effects like these would occur if a more simple approach were used, such as releasing
		/// all modifiers that are going up prior to pushing down the ones that are going down.
		/// When the target layout has an altgr key, it is tempting to try to simplify things by removing MOD_LCONTROL
		/// from modifiersLRnew whenever modifiersLRnew contains MOD_RALT.  However, this a careful review how that
		/// would impact various places below where sTargetLayoutHasAltGr is checked indicates that it wouldn't help.
		/// Note that by design and as documented for ControlSend, aTargetWindow is not used as the target for the
		/// various calls to KeyEvent() here.  It is only used as a workaround for the GUI window issue described
		/// at the bottom.
		/// </summary>
		/// <param name="modifiersLRnew"></param>
		/// <param name="modifiersLRnow"></param>
		/// <param name="targetWindow"></param>
		/// <param name="disguiseDownWinAlt"></param>
		/// <param name="disguiseUpWinAlt"></param>
		/// <param name="extraInfo"></param>
		internal void SetModifierLRState(uint modifiersLRnew, uint modifiersLRnow, nint targetWindow
										 , bool disguiseDownWinAlt, bool disguiseUpWinAlt, long extraInfo = KeyIgnoreAllExceptModifier)
		{
			if (modifiersLRnow == modifiersLRnew) // They're already in the right state, so avoid doing all the checks.
				return; // Especially avoids the aTargetWindow check at the bottom.

			// Notes about modifier key behavior on Windows XP (these probably apply to NT/2k also, and has also
			// been tested to be true on Win98): The WIN and ALT keys are the problem keys, because if either is
			// released without having modified something (even another modifier), the WIN key will cause the
			// Start Menu to appear, and the ALT key will activate the menu bar of the active window (if it has one).
			// For example, a hook hotkey such as "$#c::Send text" (text must start with a lowercase letter
			// to reproduce the issue, because otherwise WIN would be auto-disguised as a side effect of the SHIFT
			// keystroke) would cause the Start Menu to appear if the disguise method below weren't used.
			//
			// Here are more comments formerly in SetModifierLRStateSpecific(), which has since been eliminated
			// because this function is sufficient:
			// To prevent it from activating the menu bar, the release of the ALT key should be disguised
			// unless a CTRL key is currently down.  This is because CTRL always seems to avoid the
			// activation of the menu bar (unlike SHIFT, which sometimes allows the menu to be activated,
			// though this is hard to reproduce on XP).  Another reason not to use SHIFT is that the OS
			// uses LAlt+Shift as a hotkey to switch languages.  Such a hotkey would be triggered if SHIFT
			// were pressed down to disguise the release of LALT.
			//
			// Alt-down events are also disguised whenever they won't be accompanied by a Ctrl-down.
			// This is necessary whenever our caller does not plan to disguise the key itself.  For example,
			// if "!a::Send Test" is a registered hotkey, two things must be done to avoid complications:
			// 1) Prior to sending the word test, ALT must be released in a way that does not activate the
			//    menu bar.  This is done by sandwiching it between a CTRL-down and a CTRL-up.
			// 2) After the send is complete, SendKeys() will restore the ALT key to the down position if
			//    the user is still physically holding ALT down (this is done to make the logical state of
			//    the key match its physical state, which allows the same hotkey to be fired twice in a row
			//    without the user having to release and press down the ALT key physically).
			// The #2 case above is the one handled below by ctrl_wont_be_down.  It is especially necessary
			// when the user releases the ALT key prior to releasing the hotkey suffix, which would otherwise
			// cause the menu bar (if any) of the active window to be activated.
			//
			// Some of the same comments above for ALT key apply to the WIN key.  More about this issue:
			// Although the disguise of the down-event is usually not needed, it is needed in the rare case
			// where the user releases the WIN or ALT key prior to releasing the hotkey's suffix.
			// Although the hook could be told to disguise the physical release of ALT or WIN in these
			// cases, it's best not to rely on the hook since it is not always installed.
			//
			// Registered WIN and ALT hotkeys that don't use the Send command work okay except ALT hotkeys,
			// which if the user releases ALT prior the hotkey's suffix key, cause the menu bar to be activated.
			// Since it is unusual for users to do this and because it is standard behavior for  ALT hotkeys
			// registered in the OS, fixing it via the hook seems like a low priority, and perhaps isn't worth
			// the added code complexity/size.  But if there is ever a need to do so, the following note applies:
			// If the hook is installed, could tell it to disguise any need-to-be-disguised Alt-up that occurs
			// after receipt of the registered ALT hotkey.  But what if that hotkey uses the send command:
			// there might be interference?  Doesn't seem so, because the hook only disguises non-ignored events.
			// Set up some conditions so that the keystrokes that disguise the release of Win or Alt
			// are only sent when necessary (which helps avoid complications caused by keystroke interaction,
			// while improving performance):
			var script = Script.TheScript;
			var modifiersLRunion = modifiersLRnow | modifiersLRnew; // The set of keys that were or will be down.
			var ctrlNotDown = (modifiersLRnow & (MOD_LCONTROL | MOD_RCONTROL)) == 0; // Neither CTRL key is down now.
			var ctrlWillNotBeDown = (modifiersLRnew & (MOD_LCONTROL | MOD_RCONTROL)) == 0 // Nor will it be.
									&& !(targetLayoutHasAltGr == ResultType.ConditionTrue && ((modifiersLRnew & MOD_RALT) != 0)); // Nor will it be pushed down indirectly due to AltGr.
			var ctrlNorShiftNorAltDown = ctrlNotDown                             // Neither CTRL key is down now.
										 && (modifiersLRnow & (MOD_LSHIFT | MOD_RSHIFT | MOD_LALT | MOD_RALT)) == 0; // Nor is any SHIFT/ALT key.
			var ctrlOrShiftOrAltWillBeDown = !ctrlWillNotBeDown             // CTRL will be down.
											 || (modifiersLRnew & (MOD_LSHIFT | MOD_RSHIFT | MOD_LALT | MOD_RALT)) != 0; // or SHIFT or ALT will be.
			// If the required disguise keys aren't down now but will be, defer the release of Win and/or Alt
			// until after the disguise keys are in place (since in that case, the caller wanted them down
			// as part of the normal operation here):
			var deferWinRelease = ctrlNorShiftNorAltDown && ctrlOrShiftOrAltWillBeDown;
			var deferAltRelease = ctrlNotDown && !ctrlWillNotBeDown;  // i.e. Ctrl not down but it will be.
			var releaseShiftBeforeAltCtrl = deferAltRelease // i.e. Control is moving into the down position or...
											|| (((modifiersLRnow & (MOD_LALT | MOD_RALT)) == 0) && ((modifiersLRnew & (MOD_LALT | MOD_RALT)) != 0)); // ...Alt is moving into the down position.
			// Concerning "release_shift_before_alt_ctrl" above: Its purpose is to prevent unwanted firing of the OS's
			// language bar hotkey.  See the bottom of this function for more explanation.
			// ALT:
			var disguiseAltDown = disguiseDownWinAlt && ctrlNotDown && ctrlWillNotBeDown; // Since this applies to both Left and Right Alt, don't take sTargetLayoutHasAltGr into account here. That is done later below.
			// WIN: The WIN key is successfully disguised under a greater number of conditions than ALT.
			// Since SendPlay can't display Start Menu, there's no need to send the disguise-keystrokes (such
			// keystrokes might cause unwanted effects in certain games):
			var disguiseWinDown = disguiseDownWinAlt && sendMode != SendModes.Play
								  && ctrlNotDown && ctrlWillNotBeDown
								  && (modifiersLRunion & (MOD_LSHIFT | MOD_RSHIFT)) == 0 // And neither SHIFT key is down, nor will it be.
								  && (modifiersLRunion & (MOD_LALT | MOD_RALT)) == 0;    // And neither ALT key is down, nor will it be.
			var releaseLwin = (modifiersLRnow & MOD_LWIN) != 0 && (modifiersLRnew & MOD_LWIN) == 0;
			var releaseRwin = (modifiersLRnow & MOD_RWIN) != 0 && (modifiersLRnew & MOD_RWIN) == 0;
			var releaseLalt = (modifiersLRnow & MOD_LALT) != 0 && (modifiersLRnew & MOD_LALT) == 0;
			var releaseRalt = (modifiersLRnow & MOD_RALT) != 0 && (modifiersLRnew & MOD_RALT) == 0;
			var releaseLshift = (modifiersLRnow & MOD_LSHIFT) != 0 && (modifiersLRnew & MOD_LSHIFT) == 0;
			var releaseRshift = (modifiersLRnow & MOD_RSHIFT) != 0 && (modifiersLRnew & MOD_RSHIFT) == 0;

			// Handle ALT and WIN prior to the other modifiers because the "disguise" methods below are
			// only needed upon release of ALT or WIN.  This is because such releases tend to have a better
			// chance of being "disguised" if SHIFT or CTRL is down at the time of the release.  Thus, the
			// release of SHIFT or CTRL (if called for) is deferred until afterward.
			// ** WIN
			// Must be done before ALT in case it is relying on ALT being down to disguise the release WIN.
			// If ALT is going to be pushed down further below, defer_win_release should be true, which will make sure
			// the WIN key isn't released until after the ALT key is pushed down here at the top.
			// Also, WIN is a little more troublesome than ALT, so it is done first in case the ALT key
			// is down but will be going up, since the ALT key being down might help the WIN key.
			// For example, if you hold down CTRL, then hold down LWIN long enough for it to auto-repeat,
			// then release CTRL before releasing LWIN, the Start Menu would appear, at least on XP.
			// But it does not appear if CTRL is released after LWIN.
			// Also note that the ALT key can disguise the WIN key, but not vice versa.
			if (releaseLwin)
			{
				if (!deferWinRelease)
				{
					// Fixed for v1.0.25: To avoid triggering the system's LAlt+Shift language hotkey, the
					// Control key is now used to suppress LWIN/RWIN (preventing the Start Menu from appearing)
					// rather than the Shift key.  This is definitely needed for ALT, but is done here for
					// WIN also in case ALT is down, which might cause the use of SHIFT as the disguise key
					// to trigger the language switch.
					if (ctrlNorShiftNorAltDown && disguiseUpWinAlt // Nor will they be pushed down later below, otherwise defer_win_release would have been true and we couldn't get to this point.
							&& sendMode != SendModes.Play) // SendPlay can't display Start Menu, so disguise not needed (also, disguise might mess up some games).
						SendKeyEventMenuMask(KeyEventTypes.KeyDownAndUp, extraInfo); // Disguise key release to suppress Start Menu.

					// The above event is safe because if we're here, it means VK_CONTROL will not be
					// pressed down further below.  In other words, we're not defeating the job
					// of this function by sending these disguise keystrokes.
					SendKeyEvent(KeyEventTypes.KeyUp, VK_LWIN, 0, 0, false, extraInfo);
				}

				// else release it only after the normal operation of the function pushes down the disguise keys.
			}
			else if ((modifiersLRnow & MOD_LWIN) == 0 && (modifiersLRnew & MOD_LWIN) != 0) // Press down LWin.
			{
				if (disguiseWinDown)
					SendKeyEventMenuMask(KeyEventTypes.KeyDown, extraInfo); // Ensures that the Start Menu does not appear.

				SendKeyEvent(KeyEventTypes.KeyDown, VK_LWIN, 0, 0, false, extraInfo);

				if (disguiseWinDown)
					SendKeyEventMenuMask(KeyEventTypes.KeyUp, extraInfo); // Ensures that the Start Menu does not appear.
			}

			if (releaseRwin)
			{
				if (!deferWinRelease)
				{
					if (ctrlNorShiftNorAltDown && disguiseUpWinAlt && sendMode != SendModes.Play)
						SendKeyEventMenuMask(KeyEventTypes.KeyDownAndUp, extraInfo); // Disguise key release to suppress Start Menu.

					SendKeyEvent(KeyEventTypes.KeyUp, VK_RWIN, 0, 0, false, extraInfo);
				}

				// else release it only after the normal operation of the function pushes down the disguise keys.
			}
			else if ((modifiersLRnow & MOD_RWIN) == 0 && (modifiersLRnew & MOD_RWIN) != 0) // Press down RWin.
			{
				if (disguiseWinDown)
					SendKeyEventMenuMask(KeyEventTypes.KeyDown, extraInfo); // Ensures that the Start Menu does not appear.

				SendKeyEvent(KeyEventTypes.KeyDown, VK_RWIN, 0, 0, false, extraInfo);

				if (disguiseWinDown)
					SendKeyEventMenuMask(KeyEventTypes.KeyUp, extraInfo); // Ensures that the Start Menu does not appear.
			}

			// ** SHIFT (PART 1 OF 2)
			if (releaseShiftBeforeAltCtrl)
			{
				if (releaseLshift)
					SendKeyEvent(KeyEventTypes.KeyUp, VK_LSHIFT, 0, 0, false, extraInfo);

				if (releaseRshift)
					SendKeyEvent(KeyEventTypes.KeyUp, VK_RSHIFT, 0, 0, false, extraInfo);
			}

			// ** ALT
			if (releaseLalt)
			{
				if (!deferAltRelease)
				{
					if (ctrlNotDown && disguiseUpWinAlt)
						SendKeyEventMenuMask(KeyEventTypes.KeyDownAndUp, extraInfo); // Disguise key release to suppress menu activation.

					SendKeyEvent(KeyEventTypes.KeyUp, VK_LMENU, 0, 0, false, extraInfo);
				}
			}
			else if ((modifiersLRnow & MOD_LALT) == 0 && (modifiersLRnew & MOD_LALT) != 0)
			{
				if (disguiseAltDown)
					SendKeyEventMenuMask(KeyEventTypes.KeyDown, extraInfo); // Ensures that menu bar is not activated.

				SendKeyEvent(KeyEventTypes.KeyDown, VK_LMENU, 0, 0, false, extraInfo);

				if (disguiseAltDown)
					SendKeyEventMenuMask(KeyEventTypes.KeyUp, extraInfo);
			}

			if (releaseRalt)
			{
				if (!deferAltRelease || targetLayoutHasAltGr == ResultType.ConditionTrue) // No need to defer if RAlt==AltGr. But don't change the value of defer_alt_release because LAlt uses it too.
				{
					if (targetLayoutHasAltGr == ResultType.ConditionTrue)
					{
						// Indicate that control is up now, since the release of AltGr will cause that indirectly.
						// Fix for v1.0.43: Unlike the pressing down of AltGr in a later section, which callers want
						// to automatically press down LControl too (by the very nature of AltGr), callers do not want
						// the release of AltGr to release LControl unless they specifically asked for LControl to be
						// released too.  This is because the caller may need LControl down to manifest something
						// like ^c. So don't do: modifiersLRnew &= ~MOD_LCONTROL.
						// Without this fix, a hotkey like <^>!m::Send ^c would send "c" vs. "^c" on the German layout.
						// See similar section below for more details.
						modifiersLRnow &= ~MOD_LCONTROL; ; // To reflect what KeyEvent(KEYUP, VK_RMENU) below will do.
					}
					else // No AltGr, so check if disguise is necessary (AltGr itself never needs disguise).
						if (ctrlNotDown && disguiseUpWinAlt)
							SendKeyEventMenuMask(KeyEventTypes.KeyDownAndUp, extraInfo); // Disguise key release to suppress menu activation.

					SendKeyEvent(KeyEventTypes.KeyUp, VK_RMENU, 0, 0, false, extraInfo);
				}
			}
			else if ((modifiersLRnow & MOD_RALT) == 0 && (modifiersLRnew & MOD_RALT) != 0) // Press down RALT.
			{
				// For the below: There should never be a need to disguise AltGr.  Doing so would likely cause unwanted
				// side-effects. Also, disguise_alt_key does not take sTargetLayoutHasAltGr into account because
				// disguise_alt_key also applies to the left alt key.
				if (disguiseAltDown && targetLayoutHasAltGr != ResultType.ConditionTrue)
				{
					SendKeyEventMenuMask(KeyEventTypes.KeyDown, extraInfo); // Ensures that menu bar is not activated.
					SendKeyEvent(KeyEventTypes.KeyDown, VK_RMENU, 0, 0, false, extraInfo);
					SendKeyEventMenuMask(KeyEventTypes.KeyUp, extraInfo);
				}
				else // No disguise needed.
				{
					// v1.0.43: The following check was added to complement the other .43 fix higher above.
					// It may also fix other things independently of that other fix.
					// The following two lines release LControl before pushing down AltGr because otherwise,
					// the next time RAlt is released (such as by the user), some quirk of the OS or driver
					// prevents it from automatically releasing LControl like it normally does (perhaps
					// because the OS is designed to leave LControl down if it was down before AltGr went down).
					// This would cause LControl to get stuck down for hotkeys in German layout such as:
					//   <^>!a::SendRaw, {
					//   <^>!m::Send ^c
					if (targetLayoutHasAltGr == ResultType.ConditionTrue)
					{
						if ((modifiersLRnow & MOD_LCONTROL) != 0)
							SendKeyEvent(KeyEventTypes.KeyUp, VK_LCONTROL, 0, 0, false, extraInfo);

						if ((modifiersLRnow & MOD_RCONTROL) != 0)
						{
							// Release RCtrl before pressing AltGr, because otherwise the system will not put
							// LCtrl into effect, but it will still inject LCtrl-up when AltGr is released.
							// With LCtrl not in effect and RCtrl being released below, AltGr would instead
							// act as pure RAlt, which would not have the right effect.
							// RCtrl will be put back into effect below if modifiersLRnew & MOD_RCONTROL.
							SendKeyEvent(KeyEventTypes.KeyUp, VK_RCONTROL, 0, 0, false, extraInfo);
							modifiersLRnow &= ~MOD_RCONTROL;
						}
					}

					SendKeyEvent(KeyEventTypes.KeyDown, VK_RMENU, 0, 0, false, extraInfo);

					if (targetLayoutHasAltGr == ResultType.ConditionTrue) // Note that KeyEvent() might have just changed the value of sTargetLayoutHasAltGr.
					{
						// Indicate that control is both down and required down so that the section after this one won't
						// release it.  Without this fix, a hotkey that sends an AltGr char such as "^�:: Send '{Raw}{'"
						// would fail to work under German layout because left-alt would be released after right-alt
						// goes down.
						modifiersLRnow |= MOD_LCONTROL; // To reflect what KeyEvent() did above.
						modifiersLRnew |= MOD_LCONTROL; // All callers want LControl to be down if they wanted AltGr to be down.
					}
				}
			}

			// CONTROL and SHIFT are done only after the above because the above might rely on them
			// being down before for certain early operations.

			// ** CONTROL
			if ((modifiersLRnow & MOD_LCONTROL) != 0 && (modifiersLRnew & MOD_LCONTROL) == 0 // Release LControl.
					// v1.0.41.01: The following line was added to fix the fact that callers do not want LControl
					// released when the new modifier state includes AltGr.  This solves a hotkey such as the following and
					// probably several other circumstances:
					// <^>!a::send \  ; Backslash is solved by this fix; it's manifest via AltGr+Dash on German layout.
					&& !((modifiersLRnew & MOD_RALT) != 0 && targetLayoutHasAltGr == ResultType.ConditionTrue))
				SendKeyEvent(KeyEventTypes.KeyUp, VK_LCONTROL, 0, 0, false, extraInfo);
			else if ((modifiersLRnow & MOD_LCONTROL) == 0 && (modifiersLRnew & MOD_LCONTROL) != 0) // Press down LControl.
				SendKeyEvent(KeyEventTypes.KeyDown, VK_LCONTROL, 0, 0, false, extraInfo);

			if ((modifiersLRnow & MOD_RCONTROL) != 0 && (modifiersLRnew & MOD_RCONTROL) == 0) // Release RControl
				SendKeyEvent(KeyEventTypes.KeyUp, VK_RCONTROL, 0, 0, false, extraInfo);
			else if ((modifiersLRnow & MOD_RCONTROL) == 0 && (modifiersLRnew & MOD_RCONTROL) != 0) // Press down RControl.
				SendKeyEvent(KeyEventTypes.KeyDown, VK_RCONTROL, 0, 0, false, extraInfo);

			// ** SHIFT (PART 2 OF 2)
			// Must follow CTRL and ALT because a release of SHIFT while ALT/CTRL is down-but-soon-to-be-up
			// would switch languages via the OS hotkey.  It's okay if defer_alt_release==true because in that case,
			// CTRL just went down above (by definition of defer_alt_release), which will prevent the language hotkey
			// from firing.
			if (releaseLshift && !releaseShiftBeforeAltCtrl) // Release LShift.
				SendKeyEvent(KeyEventTypes.KeyUp, VK_LSHIFT, 0, 0, false, extraInfo);
			else if ((modifiersLRnow & MOD_LSHIFT) == 0 && (modifiersLRnew & MOD_LSHIFT) != 0) // Press down LShift.
				SendKeyEvent(KeyEventTypes.KeyDown, VK_LSHIFT, 0, 0, false, extraInfo);

			if (releaseRshift && !releaseShiftBeforeAltCtrl) // Release RShift.
				SendKeyEvent(KeyEventTypes.KeyUp, VK_RSHIFT, 0, 0, false, extraInfo);
			else if ((modifiersLRnow & MOD_RSHIFT) == 0 && (modifiersLRnew & MOD_RSHIFT) != 0) // Press down RShift.
				SendKeyEvent(KeyEventTypes.KeyDown, VK_RSHIFT, 0, 0, false, extraInfo);

			// ** KEYS DEFERRED FROM EARLIER
			if (deferWinRelease) // Must be done before ALT because it might rely on ALT being down to disguise release of WIN key.
			{
				if (releaseLwin)
					SendKeyEvent(KeyEventTypes.KeyUp, VK_LWIN, 0, 0, false, extraInfo);

				if (releaseRwin)
					SendKeyEvent(KeyEventTypes.KeyUp, VK_RWIN, 0, 0, false, extraInfo);
			}

			if (deferAltRelease)
			{
				if (releaseLalt)
					SendKeyEvent(KeyEventTypes.KeyUp, VK_LMENU, 0, 0, false, extraInfo);

				if (releaseRalt && targetLayoutHasAltGr != ResultType.ConditionTrue) // If AltGr is present, RAlt would already have been released earlier since defer_alt_release would have been ignored for it.
					SendKeyEvent(KeyEventTypes.KeyUp, VK_RMENU, 0, 0, false, extraInfo);
			}

			// When calling SendKeyEvent(), probably best not to specify a scan code unless
			// absolutely necessary, since some keyboards may have non-standard scan codes
			// which SendKeyEvent() will resolve into the proper vk translations for us.
			// Decided not to Sleep() between keystrokes, even zero, out of concern that this
			// would result in a significant delay (perhaps more than 10ms) while the system
			// is under load.
			// Since the above didn't return early, keybd_event() has been used to change the state
			// of at least one modifier.  As a result, if the caller gave a non-NULL aTargetWindow,
			// it wants us to check if that window belongs to our thread.  If it does, we should do
			// a short msg queue check to prevent an apparent synchronization problem when using
			// ControlSend against the script's own GUI or other windows.  Here is an example of a
			// line whose modifier would not be in effect in time for its keystroke to be modified
			// by it:
			// ControlSend, Edit1, ^{end}, Test Window
			// Update: Another bug-fix for v1.0.21, as was the above: If the keyboard hook is installed,
			// the modifier keystrokes must have a way to get routed through the hook BEFORE the
			// keystrokes get sent via PostMessage().  If not, the correct modifier state will usually
			// not be in effect (or at least not be in sync) for the keys sent via PostMessage() afterward.
			// Notes about the macro below:
			// aTargetWindow!=NULL means ControlSend mode is in effect.
			// The g_KeybdHook check must come first (it should take precedence if both conditions are true).
			// -1 has been verified to be insufficient, at least for the very first letter sent if it is
			// supposed to be capitalized.
			// g_MainThreadID is the only thread of our process that owns any windows.
			var pressDuration = sendMode == SendModes.Play ? ThreadAccessors.A_KeyDurationPlay : ThreadAccessors.A_KeyDuration;

			if (pressDuration > -1) // SM_PLAY does use DoKeyDelay() to store a delay item in the event array.
			{
				// Since modifiers were changed by the above, do a key-delay if the special intra-keystroke
				// delay is in effect.
				// Since there normally isn't a delay between a change in modifiers and the first keystroke,
				// if a PressDuration is in effect, also do it here to improve reliability (I have observed
				// cases where modifiers need to be left alone for a short time in order for the keystrokes
				// that follow to be be modified by the intended set of modifiers).
				DoKeyDelay((int)pressDuration); // It knows not to do the delay for SM_INPUT.
			}
			else // Since no key-delay was done, check if a a delay is needed for any other reason.
			{
				// IMPORTANT UPDATE for v1.0.39: Now that the hooks are in a separate thread from the part
				// of the program that sends keystrokes for the script, you might think synchronization of
				// keystrokes would become problematic or at least change.  However, this is apparently not
				// the case.  MSDN doesn't spell this out, but testing shows that what happens with a low-level
				// hook is that the moment a keystroke comes into a thread (either physical or simulated), the OS
				// immediately calls something similar to SendMessage() from that thread to notify the hook
				// thread that a keystroke has arrived.  However, if the hook thread's priority is lower than
				// some other thread next in line for a timeslice, it might take some time for the hook thread
				// to get a timeslice (that's why the hook thread is given a high priority).
				// The SendMessage() call doesn't return until its timeout expires (as set in the registry for
				// hooks) or the hook thread processes the keystroke (which requires that it call something like
				// GetMessage/PeekMessage followed by a HookProc "return").  This is good news because it serializes
				// keyboard and mouse input to make the presence of the hook transparent to other threads (unless
				// the hook does something to reveal itself, such as suppressing keystrokes). Serialization avoids
				// any chance of synchronization problems such as a program that changes the state of a key then
				// immediately checks the state of that same key via GetAsyncKeyState().  Another way to look at
				// all of this is that in essence, a single-threaded hook program that simulates keystrokes or
				// mouse clicks should behave the same when the hook is moved into a separate thread because from
				// the program's point-of-view, keystrokes & mouse clicks result in a calling the hook almost
				// exactly as if the hook were in the same thread.
				if (targetWindow != 0)
				{
					//if (hookId != 0)
					if (script.HookThread.HasKbdHook())
						Flow.SleepWithoutInterruption(0); // Don't use ternary operator to combine this with next due to "else if".
#if WINDOWS
					else if (WindowsAPI.GetWindowThreadProcessId(targetWindow, out var _) == script.ProcessesData.MainThreadID)
						Flow.SleepWithoutInterruption(-1);
#endif
				}
			}

			// Commented out because a return value is no longer needed by callers (since we do the key-delay here,
			// if appropriate).
			//return modifiersLRnow ^ modifiersLRnew; // Calculate the set of modifiers that changed (currently excludes AltGr's change of LControl's state).
			// NOTES about "release_shift_before_alt_ctrl":
			// If going down on alt or control (but not both, though it might not matter), and shift is to be released:
			//  Release shift first.
			// If going down on shift, and control or alt (but not both) is to be released:
			//  Release ctrl/alt first (this is already the case so nothing needs to be done).
			//
			// Release both shifts before going down on lalt/ralt or lctrl/rctrl (but not necessary if going down on
			// *both* alt+ctrl?
			// Release alt and both controls before going down on lshift/rshift.
			// Rather than the below, do the above (for the reason below).
			// But if do this, don't want to prevent a legit/intentional language switch such as:
			//    Send {LAlt down}{Shift}{LAlt up}.
			// If both Alt and Shift are down, Win or Ctrl (or any other key for that matter) must be pressed before either
			// is released.
			// If both Ctrl and Shift are down, Win or Alt (or any other key) must be pressed before either is released.
			// remind: Despite what the Regional Settings window says, RAlt+Shift (and Shift+RAlt) is also a language hotkey (i.e. not just LAlt), at least when RAlt isn't AltGr!
			// remind: Control being down suppresses language switch only once.  After that, control being down doesn't help if lalt is re-pressed prior to re-pressing shift.
			//
			// Language switch occurs when:
			// alt+shift (upon release of shift)
			// shift+alt (upon release of lalt)
			// ctrl+shift (upon release of shift)
			// shift+ctrl (upon release of ctrl)
			// Because language hotkey only takes effect upon release of Shift, it can be disguised via a Control keystroke if that is ever needed.
			// NOTES: More details about disguising ALT and WIN:
			// Registered Alt hotkeys don't quite work if the Alt key is released prior to the suffix.
			// Key history for Alt-B hotkey released this way, which undesirably activates the menu bar:
			// A4  038      d   0.03    Alt
			// 42  030      d   0.03    B
			// A4  038      u   0.24    Alt
			// 42  030      u   0.19    B
			// Testing shows that the above does not happen for a normal (non-hotkey) alt keystroke such as Alt-8,
			// so the above behavior is probably caused by the fact that B-down is suppressed by the OS's hotkey
			// routine, but not B-up.
			// The above also happens with registered WIN hotkeys, but only if the Send cmd resulted in the WIN
			// modifier being pushed back down afterward to match the fact that the user is still holding it down.
			// This behavior applies to ALT hotkeys also.
			// One solution: if the hook is installed, have it keep track of when the start menu or menu bar
			// *would* be activated.  These tracking vars can be consulted by the Send command, and the hook
			// can also be told when to use them after a registered hotkey has been pressed, so that the Alt-up
			// or Win-up keystroke that belongs to it can be disguised.
			// The following are important ways in which other methods of disguise might not be sufficient:
			// Sequence: shift-down win-down shift-up win-up: invokes Start Menu when WIN is held down long enough
			// to auto-repeat.  Same when Ctrl or Alt is used in lieu of Shift.
			// Sequence: shift-down alt-down alt-up shift-up: invokes menu bar.  However, as long as another key,
			// even Shift, is pressed down *after* alt is pressed down, menu bar is not activated, e.g. alt-down
			// shift-down shift-up alt-up.  In addition, CTRL always prevents ALT from activating the menu bar,
			// even with the following sequences:
			// ctrl-down alt-down alt-up ctrl-up
			// alt-down ctrl-down ctrl-up alt-up
			// (also seems true for all other permutations of Ctrl/Alt)
		}
	}
}