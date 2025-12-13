#if WINDOWS
using static Keysharp.Core.Common.Keyboard.KeyboardUtils;
using static Keysharp.Core.Common.Mouse.MouseUtils;
using static Keysharp.Core.Common.Keyboard.VirtualKeys;
using static Keysharp.Core.Windows.WindowsAPI;

namespace Keysharp.Core.Windows
{
	[StructLayout(LayoutKind.Explicit)]
	internal struct PlaybackEvent
	{
		[FieldOffset(0)]
		internal uint messagetype;

		[FieldOffset(1)]
		internal ScVk scvk;

		[FieldOffset(1)]
		internal Pt pt;

		[FieldOffset(1)]
		internal uint time_to_wait; // This member is present only when message==0; otherwise, a struct is present.
	}

	/// <summary>
	/// Concrete implementation of KeyboardMouseSender for the Windows platfrom.
	/// </summary>
	internal class WindowsKeyboardMouseSender : KeyboardMouseSender
	{
		internal const int MaxInitialEventsPB = 1500;
		internal const int MaxInitialEventsSI = 500;
		internal int currentEvent;
		internal List<PlaybackEvent> eventPb = new (MaxInitialEventsPB);
		internal List<INPUT> eventSi = new (MaxInitialEventsSI);
		internal bool firstCallForThisEvent;
		// sizeof(INPUT) == 28 as of 2006. Since Send is called so often, and since most Sends are short, reducing the load on the stack is also a deciding factor for these.
		// sizeof(PlaybackEvent) == 8, so more events are justified before resorting to malloc().
		internal uint hooksToRemoveDuringSendInput;

		internal long workaroundHitTest;

		private readonly StringBuilder buf = new (4);

		private readonly List<CachedLayoutType> cachedLayouts = new (10);

		// Below uses a pseudo-random value.  It's best that this be constant so that if multiple instances
		// of the app are running, they will all ignore each other's keyboard & mouse events.  Also, a value
		// close to UINT_MAX might be a little better since it's might be less likely to be used as a pointer
		// value by any apps that send keybd events whose ExtraInfo is really a pointer value.
		//public const uint KEY_IGNORE = 0xFFC3D44F;
		//public const uint KEY_PHYS_IGNORE = (KEY_IGNORE - 1);  // Same as above but marked as physical for other instances of the hook.
		//public const uint KEY_IGNORE_ALL_EXCEPT_MODIFIER = (KEY_IGNORE - 2);  // Non-physical and ignored only if it's not a modifier.
		//public const uint KEY_BLOCK_THIS = (KEY_IGNORE + 1);
		private readonly int[] ctrls = [/*(int)Keys.ShiftKey, */(int)Keys.LShiftKey, (int)Keys.RShiftKey, /*(int)Keys.ControlKey,*/(int)Keys.LControlKey, (int)Keys.RControlKey, (int)Keys.Menu];

		//private static readonly byte[] state = new byte[VKMAX];
		//private readonly nint hookId = 0;
		private bool thisEventHasBeenLogged, thisEventIsScreenCoord;
		//private bool dead;
		//private List<uint> deadKeys;
		//private bool ignore;
		//private nint kbd = GetKeyboardLayout(0);
		//private WindowsAPI.LowLevelKeyboardProc proc;

		private DateTime thisEventTime;

		internal WindowsKeyboardMouseSender()
		{
		}

		internal override bool MouseButtonsSwapped => GetSystemMetrics(SystemMetric.SM_SWAPBUTTON) != 0;

		/// <summary>
		/// Loads and reads the keyboard layout DLL to determine if it has AltGr.
		/// Activates the layout as a side-effect, but reverts it if !aSideEffectsOK.
		/// This is fast enough that there's no need to cache these values on startup.
		/// </summary>
		/// <param name="layout"></param>
		/// <returns></returns>
		internal override ResultType LayoutHasAltGrDirect(nint layout)
		{
			const int KLLF_ALTGR = 0x0001;
			var result = ResultType.Fail;
			var hmod = LoadKeyboardLayoutModule(layout);

			if (hmod != 0)
			{
				var kbdLayerDescriptor = GetProcAddress(hmod, "KbdLayerDescriptor");

				if (kbdLayerDescriptor != 0)
				{
					var func = (KbdTables)Marshal.GetDelegateForFunctionPointer(kbdLayerDescriptor, typeof(KbdTables));
					var kl = func();
					var flags = kl.fLocaleFlags;
					result = (flags & KLLF_ALTGR) != 0 ? ResultType.ConditionTrue : ResultType.ConditionFalse;
				}

				_ = FreeLibrary(hmod);
			}

			return result;
		}

		/// <summary>
		/// AHK says this works for all layouts except one Ukrainian and one North Korean. See line 3940 of keyboard_mouse.cpp
		/// Their solution for those two was far more complex than we'd like to implement here.
		/// So we acknowledge those two layouts won't support AltGr correctly.
		/// Gotten from: https://stackoverflow.com/questions/54588823/detect-if-the-keyboard-layout-has-altgr-on-it-under-windows
		/// </summary>
		/// <param name="layout">The keyboard layout to examine</param>
		/// <returns>True if the layout has AltGr, else false.</returns>
		//private bool LayoutHasAltGr(nint layout)//Unsure if this is usable on linux, where the registry method used below obviously doesn't exist.
		//{
		//  var hasAltGr = false;
		//  for (byte i = 32; i <= 255; ++i)
		//  {
		//      var scancode = WindowsAPI.VkKeyScanEx(i, layout);
		//      if (scancode != -1 && (scancode & 0x600) == 0x600)//Ctrl + Alt means AltGr.
		//      {
		//          hasAltGr = true;
		//          break;
		//      }
		//  }
		//  return hasAltGr;
		//}
		/// <summary>
		/// Loads a keyboard layout DLL and returns its handle.
		/// Activates the layout as a side-effect, but reverts it if !aSideEffectsOK.
		/// </summary>
		/// <param name="layout"></param>
		/// <returns></returns>
		internal static nint LoadKeyboardLayoutModule(nint layout)
		{
			nint hmod = 0;
#if WINDOWS
			// Unfortunately activating the layout seems to be the only way to retrieve it's name.
			// This may have side-effects in general (such as the language selector flickering),
			// but shouldn't have any in our case since we're only changing layouts for our thread,
			// and only if some other window is active (because if our window was active, layout
			// is already the current layout).
			var oldLayout = ActivateKeyboardLayout(layout, 0);

			if (oldLayout != 0)
			{
				var chars = new char[16];

				if (GetKeyboardLayoutName(chars))
				{
					using (var key = Registry.LocalMachine.OpenSubKey($"SYSTEM\\CurrentControlSet\\Control\\Keyboard Layouts\\{new string(chars, 0, System.Array.IndexOf(chars, '\0'))}"))
					{
						if (key != null)
						{
							var o = key.GetValue("Layout File");

							if (o is string s)
								hmod = LoadLibrary(s);
						}
					}
				}

				if (layout.ToInt32() != oldLayout)
					_ = ActivateKeyboardLayout(new nint(oldLayout), 0); // Nothing we can do if it fails.
			}

#endif
			return hmod;
		}

		internal override void CleanupEventArray(long finalKeyDelay)
		{
			if (sendMode == SendModes.Input)
			{
				if (maxEvents > MaxInitialEventsSI)
					eventSi.Clear();
			}
			else if (sendMode == SendModes.Play)
			{
				if (maxEvents > MaxInitialEventsPB)
					eventPb.Clear();
			}

			// The following must be done only after functions called above are done using it.  But it must also be done
			// prior to our caller toggling capslock back on , to avoid the capslock keystroke from going into the array.
			sendMode = SendModes.Event;
			DoKeyDelay(finalKeyDelay); // Do this only after resetting sSendMode above.  Should be okay for mouse events too.
		}

		internal override nint GetFocusedKeybdLayout(nint window)
		{
			var script = Script.TheScript;

			if (window == 0)
				window = WindowManager.GetForegroundWindowHandle();

			nint tempzero = 0;
			return GetKeyboardLayout(WindowManager.GetFocusedCtrlThread(ref tempzero, window));
		}

		//internal ResultType ExpandEventArray()
		//{
		//  if (abortArraySend) // A prior call failed (might be impossible).  Avoid malloc() in this case.
		//      return ResultType.Fail;
		internal override void InitEventArray(int maxEvents, uint modifiersLR)
		{
			eventSi.Clear();
			eventPb.Clear();
			base.maxEvents = maxEvents;
			eventModifiersLR = modifiersLR;
			sendInputCursorPos.X = CoordUnspecified;
			sendInputCursorPos.Y = CoordUnspecified;
			hooksToRemoveDuringSendInput = 0;
			abortArraySend = false; // If KeyEvent() ever sets it to true, that allows us to send nothing at all rather than a partial send.
			firstCallForThisEvent = true;
			// The above isn't a local static inside PlaybackProc because PlaybackProc might get aborted in the
			// middle of a NEXT/SKIP pair by user pressing Ctrl-Esc, etc, which would make it unreliable.
		}

		/// <summary>
		/// Thread-safety: While not thoroughly thread-safe, due to the extreme simplicity of the cache array, even if
		/// a collision occurs it should be inconsequential.
		/// Caller must ensure that layout is a valid layout (special values like 0 aren't supported here).
		/// If aHasAltGr is not at its default of LAYOUT_UNDETERMINED, the specified layout's has_altgr property is
		/// updated to the new value, but only if it is currently undetermined (callers can rely on this).
		/// </summary>
		/// <param name="layout"></param>
		/// <returns></returns>
		internal ResultType LayoutHasAltGr(nint layout)
		{
			// Layouts are cached for performance (to avoid the discovery loop later below).
			for (var i = 0; i < cachedLayouts.Count && cachedLayouts[i].hkl != 0; ++i)
				if (cachedLayouts[i].hkl == layout) // Match Found.
					return cachedLayouts[i].has_altgr;

			// Since above didn't return, this layout isn't cached yet.  So create a new cache entry for it and
			// determine whether this layout has an AltGr key.  An LRU/MRU algorithm (timestamp) isn't used because running out
			// of slots seems too unlikely, and the consequences of running out are merely a slight degradation in performance.
			// The old approach here was to call VkKeyScanEx for each character code and find any that require
			// AltGr.  However, that was unacceptably slow for the wider character range of the Unicode build.
			// It was also unreliable (as noted below), so required additional logic in Send and the hook to
			// compensate.  Instead, read the AltGr value directly from the keyboard layout DLL.
			// This method has been compared to the VkKeyScanEx method and another one using Send and hotkeys,
			// and was found to have 100% accuracy for the 203 standard layouts on Windows 10, whereas the
			// VkKeyScanEx method failed for two layouts:
			//   - N'Ko has AltGr but does not appear to use it for anything.
			//   - Ukrainian has AltGr but only uses it for one character, which is also assigned to a naked
			//     VK (so VkKeyScanEx returns that one).  Likely the key in question is absent from some keyboards.
			var hasaltgr = LayoutHasAltGrDirect(layout);
			cachedLayouts.Add(new CachedLayoutType()
			{
				has_altgr = hasaltgr,
				hkl = layout// This is done here (immediately after has_altgr is set) rather than earlier to minimize the consequences of not being fully thread-safe.
			});
			return hasaltgr;
		}

		internal override void MouseClickPreLRButton(KeyEventTypes eventType) {
			// v1.0.43 The first line below means: We're not in SendInput/Play mode or we are but this
			// will be the first event inside the array.  The latter case also implies that no initial
			// mouse-move was done above (otherwise there would already be a MouseMove event in the array,
			// and thus the click here wouldn't be the first item).  It doesn't seem necessary to support
			// the MouseMove case above because the workaround generally isn't needed in such situations
			// (see detailed comments below).  Furthermore, if the MouseMove were supported in array-mode,
			// it would require that GetCursorPos() below be conditionally replaced with something like
			// the following (since when in array-mode, the cursor hasn't actually moved *yet*):
			//      CoordToScreen(aX_orig, aY_orig, COORD_MODE_MOUSE);  // Moving mouse relative to the active window.
			// Known limitation: the work-around described below isn't as complete for SendPlay as it is
			// for the other modes: because dragging the title bar of one of this thread's windows with a
			// remap such as F1::LButton doesn't work if that remap uses SendPlay internally (the window
			// gets stuck to the mouse cursor).
			if ((sendMode == SendModes.Event || TotalEventCount() == 0) // See above.
					&& (eventType == KeyEventTypes.KeyDown || (eventType == KeyEventTypes.KeyUp && (workaroundVK != 0)))) // i.e. this is a down-only event or up-only event.
			{
				// v1.0.40.01: The following section corrects misbehavior caused by a thread sending
				// simulated mouse clicks to one of its own windows.  A script consisting only of the
				// following two lines can reproduce this issue:
				// F1::LButton
				// F2::RButton
				// The problems came about from the following sequence of events:
				// 1) Script simulates a left-click-down in the title bar's close, minimize, or maximize button.
				// 2) WM_NCLBUTTONDOWN is sent to the window's window proc, which then passes it on to
				//    DefWindowProc or DefDlgProc, which then apparently enters a loop in which no messages
				//    (or a very limited subset) are pumped.
				// 3) Thus, if the user presses a hotkey while the thread is in this state, that hotkey is
				//    queued/buffered until DefWindowProc/DefDlgProc exits its loop.
				// 4) But the buffered hotkey is the very thing that's supposed to exit the loop via sending a
				//    simulated left-click-up event.
				// 5) Thus, a deadlock occurs.
				// 6) A similar situation arises when a right-click-down is sent to the title bar or sys-menu-icon.
				//
				// The following workaround operates by suppressing qualified click-down events until the
				// corresponding click-up occurs, at which time the click-up is transformed into a down+up if the
				// click-up is still in the same cursor position as the down. It seems preferable to fix this here
				// rather than changing each window proc. to always respond to click-down rather vs. click-up
				// because that would make all of the script's windows behave in a non-standard way, possibly
				// producing side-effects and defeating other programs' attempts to interact with them.
				// (Thanks to Shimanov for this solution.)
				//
				// Remaining known limitations:
				// 1) Title bar buttons are not visibly in a pressed down state when a simulated click-down is sent
				//    to them.
				// 2) A window that should not be activated, such as AlwaysOnTop+Disabled, is activated anyway
				//    by SetForegroundWindowEx().  Not yet fixed due to its rarity and minimal consequences.
				// 3) A related problem for which no solution has been discovered (and perhaps it's too obscure
				//    an issue to justify any added code size): If a remapping such as "F1::LButton" is in effect,
				//    pressing and releasing F1 while the cursor is over a script window's title bar will cause the
				//    window to move slightly the next time the mouse is moved.
				// 4) Clicking one of the script's window's title bar with a key/button that has been remapped to
				//    become the left mouse button sometimes causes the button to get stuck down from the window's
				//    point of view.  The reasons are related to those in #1 above.  In both #1 and #2, the workaround
				//    is not at fault because it's not in effect then.  Instead, the issue is that DefWindowProc enters
				//    a non-msg-pumping loop while it waits for the user to drag-move the window.  If instead the user
				//    releases the button without dragging, the loop exits on its own after a 500ms delay or so.
				// 5) Obscure behavior caused by keyboard's auto-repeat feature: Use a key that's been remapped to
				//    become the left mouse button to click and hold the minimize button of one of the script's windows.
				//    Drag to the left.  The window starts moving.  This is caused by the fact that the down-click is
				//    suppressed, thus the remap's hotkey subroutine thinks the mouse button is down, thus its
				//    auto-repeat suppression doesn't work and it sends another click.
				_ = GetCursorPos(out var point); // Assuming success seems harmless.
				// Despite what MSDN says, WindowFromPoint() appears to fetch a non-NULL value even when the
				// mouse is hovering over a disabled control (at least on XP).
				nint childUnderCursor, parentUnderCursor;

				if ((childUnderCursor = WindowFromPoint(point)) != 0
						&& (parentUnderCursor = GetNonChildParent(childUnderCursor)) != 0 // WM_NCHITTEST below probably requires parent vs. child.
						&& GetWindowThreadProcessId(parentUnderCursor, out _) == Script.TheScript.ProcessesData.MainThreadID) // It's one of our thread's windows.
				{
					var hitTest = SendMessage(parentUnderCursor, WM_NCHITTEST, 0, MakeLong((short)point.X, (short)point.Y));

					if (vk == VK_LBUTTON && (hitTest == HTCLOSE || hitTest == HTMAXBUTTON // Title bar buttons: Close, Maximize.
												|| hitTest == HTMINBUTTON || hitTest == HTHELP) // Title bar buttons: Minimize, Help.
							|| vk == VK_RBUTTON && (hitTest == HTCAPTION || hitTest == HTSYSMENU))
					{
						if (eventType == KeyEventTypes.KeyDown)
						{
							// Ignore this event and substitute for it: Activate the window when one
							// of its title bar buttons is down-clicked.
							workaroundVK = vk;
							workaroundHitTest = hitTest;
							_ = WindowItem.SetForegroundWindowEx(TheWindowManager.CreateWindow(parentUnderCursor)); // Try to reproduce customary behavior.
							// For simplicity, aRepeatCount>1 is ignored and DoMouseDelay() is not done.
							return;
						}
						else // KEYUP
						{
							if (workaroundHitTest == hitTest) // To weed out cases where user clicked down on a button then released somewhere other than the button.
								eventType = KeyEventTypes.KeyDownAndUp; // Translate this click-up into down+up to make up for the fact that the down was previously suppressed.

							//else let the click-up occur in case it does something or user wants it.
						}
					}
				} // Work-around for sending mouse clicks to one of our thread's own windows.
			}
		}

		/// <summary>
		/// Having this part outsourced to a function helps remember to use KEY_IGNORE so that our own mouse
		/// events won't be falsely detected as hotkeys by the hooks (if they are installed).
		/// </summary>
		/// <param name="eventFlags"></param>
		/// <param name="data"></param>
		/// <param name="x"></param>
		/// <param name="y"></param>
		internal override void MouseEvent(uint eventFlags, uint data, int x = CoordUnspecified, int y = CoordUnspecified)
		{
			if (sendMode != SendModes.Event)
				PutMouseEventIntoArray(eventFlags, data, x, y);
			else
			{
				//KeysharpEnhancements.OutputDebugLine($"Sending mouse_event() with sendlevel {ThreadAccessors.A_SendLevel}");
				mouse_event(eventFlags
							, x == CoordUnspecified ? 0 : x // v1.0.43.01: Must be zero if no change in position is desired
							, y == CoordUnspecified ? 0 : y // (fixes compatibility with certain apps/games).
							, data, new nint(KeyIgnoreLevel(ThreadAccessors.A_SendLevel)));
			}
		}

		internal override int PbEventCount() => eventPb.Count;

		internal nint PlaybackHandler(int code, nint wParam, ref EventMsg lParam)
		// Journal playback hook.
		{
			var script = Script.TheScript;
			var ht = script.HookThread;
			//var lParam = (EventMsg)Marshal.PtrToStructure(lp, typeof(EventMsg));

			switch (code)
			{
				case HC_GETNEXT:
				{
					if (firstCallForThisEvent)
					{
						// Gather the delay(s) for this event, if any, and calculate the time the keystroke should be sent.
						// NOTE: It must be done this way because testing shows that simply returning the desired delay
						// for the first call of each event is not reliable, at least not for the first few events (they
						// tend to get sent much more quickly than specified).  More details:
						// MSDN says, "When the system ...calls the hook procedure [after the first time] with code set to
						// HC_GETNEXT to retrieve the same message... the return value... should be zero."
						// Apparently the above is overly cautious wording with the intent to warn people not to write code
						// that gets stuck in infinite playback due to never returning 0, because returning non-zero on
						// calls after the first works fine as long as 0 is eventually returned.  Furthermore, I've seen
						// other professional code examples that uses this "countdown" approach, so it seems valid.
						firstCallForThisEvent = false;
						thisEventHasBeenLogged = false;
						thisEventIsScreenCoord = false;

						for (thisEventTime = DateTime.UtcNow;
								eventPb[currentEvent].messagetype == 0;// HC_SKIP has ensured there is a non-delay event, so no need to check sCurrentEvent < sEventCount.
								thisEventTime.AddMilliseconds(eventPb[currentEvent++].time_to_wait)) ; // Overflow is okay.
					}

					// Above has ensured that sThisEventTime is valid regardless of whether this is the first call
					// for this event.  It has also incremented sCurrentEvent, if needed, for use below.
					// Copy the current mouse/keyboard event to the EVENTMSG structure (lParam).
					// MSDN says that HC_GETNEXT can be received multiple times consecutively, in which case the
					// same event should be copied into the structure each time.
					var sourceEvent = eventPb[currentEvent];
					var ev = lParam;  // For convenience, maintainability, and possibly performance.
					// Currently, the following isn't documented entirely accurately at MSDN, but other sources confirm
					// the below are the proper values to store.  In addition, the extended flag as set below has been
					// confirmed to work properly by monitoring the resulting WM_KEYDOWN message in a main message loop.
					//
					// Strip off extra bits early for maintainability.  It must be stripped off the source event itself
					// because if HC_GETNEXT is called again for this same event, don't want to apply the offset again.
					bool hasCoordOffset;

					if (hasCoordOffset = ((sourceEvent.messagetype & MSG_OFFSET_MOUSE_MOVE) != 0))
						sourceEvent.messagetype &= ~MSG_OFFSET_MOUSE_MOVE;

					ev.message = sourceEvent.messagetype;
					// The following members are not set because testing confirms that they're ignored:
					// event.hwnd: ignored even if assigned the HWND of an existing window or control.
					// event.time: Apparently ignored in favor of this playback proc's return value.  Furthermore,
					// testing shows that the posted keystroke message (e.g. WM_KEYDOWN) has the correct timestamp
					// even when event.time is left as a random time, which shows that the member is completely
					// ignored during playback, at least on XP.
					bool isKeyboardNotMouse;

					if (isKeyboardNotMouse = (sourceEvent.messagetype >= WM_KEYFIRST && sourceEvent.messagetype <= WM_KEYLAST)) // Keyboard event.
					{
						ev.paramL = (uint)(sourceEvent.scvk.sc << 8) | sourceEvent.scvk.vk;
						ev.paramH = (uint)(sourceEvent.scvk.sc & 0xFF); // 0xFF omits the extended-key-bit, if present.

						if ((sourceEvent.scvk.sc & 0x100) != 0) // It's an extended key.
							ev.paramH |= 0x8000; // So mark it that way using EVENTMSG's convention.

						// Notes about inability of playback to simulate LWin and RWin in a way that performs their native function:
						// For the following reasons, it seems best not to send LWin/RWin via keybd_event inside the playback hook:
						// 1) Complexities such as having to check for an array that consists entirely of LWin/RWin events,
						//    in which case the playback hook mustn't be activated because it requires that we send
						//    at least one event through it.  Another complexity is that all keys modified by Win would
						//    have to be flagged in the array as needing to be sent via keybd_event.
						// 2) It might preserve some flexibility to be able to send LWin/RWin events directly to a window,
						//    similar to ControlSend (perhaps for shells other than Explorer, who might allow apps to make
						//    use of LWin/RWin internally). The window should receive LWIN/RWIN as WM_KEYDOWN messages when
						//    sent via playback.  Note: unlike the neutral SHIFT/ALT/CTRL keys, which are detectible via the
						//    target thread's call to GetKeyState(), LWin and RWin aren't detectible that way.
						// 3) Code size and complexity.
						//
						// Related: LWin and RWin are released and pressed down during playback for simplicity and also
						// on the off-chance the target window takes note of the incoming WM_KEYDOWN on VK_LWIN/RWIN and
						// changes state until the up-event is received (however, the target thread's call of GetKeyState
						// can't see a any effect for hook-sent LWin/RWin).
						//
						// Related: If LWin or RWin is logically down at start of SendPlay, SendPlay's events won't be
						// able to release it from the POV of the target thread's calls to GetKeyState().  That might mess
						// things up for apps that check the logical state of the Win keys.  But due to rarity: in those
						// cases, a workaround would be to do an explicit old-style Send {Blind} (as the first line of the
						// hotkey) to release the modifier logically prior to SendPlay commands.
						//
						// Related: Although some apps might not like receiving SendPlay's LWin/RWin if shell==Explorer
						// (since there may be no normal way for such keystrokes to arrive as WM_KEYDOWN events) maybe it's
						// best not to omit/ignore LWin/RWin if it is possible in other shells, or adds flexibility.
						// After all, sending {LWin/RWin} via hook should be rare, especially if it has no effect (except
						// for cases where a Win hotkey releases LWin as part of SendPlay, but even that can be worked
						// around via an explicit Send {Blind}{LWin up} beforehand).
					}
					else // MOUSE EVENT.
					{
						// Unlike keybd_event() and SendInput(), explicit coordinates must be specified for each mouse event.
						// The builder of this array must ensure that coordinates are valid or set to COORD_UNSPECIFIED_SHORT.
						if (sourceEvent.pt.x == CoordUnspecifiedShort || hasCoordOffset)
						{
							// For simplicity with calls such as CoordToScreen(), the one who set up this array has ensured
							// that both X and Y are either COORD_UNSPECIFIED_SHORT or not so (i.e. not a combination).
							// Since the user nor anything else can move the cursor during our playback, GetCursorPos()
							// should accurately reflect the position set by any previous mouse-move done by this playback.
							// This seems likely to be true even for DirectInput games, though hasn't been tested yet.
							if (GetCursorPos(out var cursor))
							{
								ev.paramL = (uint)cursor.X;
								ev.paramH = (uint)cursor.Y;
							}

							if (hasCoordOffset) // The specified coordinates are offsets to be applied to the cursor's current position.
							{
								ev.paramL += (uint)(sourceEvent.pt.x);
								ev.paramH += (uint)(sourceEvent.pt.y);
								// Update source array in case HC_GETNEXT is called again for this same event, in which case
								// don't want to apply the offset again (the has-offset flag has already been removed from the
								// source event higher above).
								sourceEvent.pt.x = (short)ev.paramL;
								sourceEvent.pt.y = (short)ev.paramH;
								thisEventIsScreenCoord = true; // Mark the above as absolute vs. relative in case HC_GETNEXT is called again for this event.
							}
						}
						else
						{
							ev.paramL = (uint)sourceEvent.pt.x;
							ev.paramH = (uint)sourceEvent.pt.y;

							if (!thisEventIsScreenCoord) // Coordinates are relative to the window that is active now (during realtime playback).
							{
								var tx = (int)ev.paramL;
								var ty = (int)ev.paramH;
								CoordToScreen(ref tx, ref ty, CoordMode.Mouse);   // Playback uses screen coords.
								ev.paramL = (uint)tx;
								ev.paramH = (uint)ty;
							}
						}
					}

					var timeUntilEvent = (thisEventTime - DateTime.UtcNow).TotalMilliseconds; // Cast to int to avoid loss of negatives from DWORD subtraction.

					if (timeUntilEvent > 0)
						return new nint((int)timeUntilEvent);

					// Otherwise, the event is scheduled to occur immediately (or is overdue).  In case HC_GETNEXT can be
					// called multiple times even when we previously returned 0, ensure the event is logged only once.
					if (!thisEventHasBeenLogged && isKeyboardNotMouse) // Mouse events aren't currently logged for consistency with other send methods.
					{
						// The event is logged here rather than higher above so that its timestamp is accurate.
						// It's also so that events aren't logged if the user cancel's the operation in the middle
						// (by pressing Ctrl-Alt-Del or Ctrl-Esc).
						if (ht.keyHistory is KeyHistory kh)
							kh.UpdateKeyEventHistory(sourceEvent.messagetype == WM_KEYUP || sourceEvent.messagetype == WM_SYSKEYUP, sourceEvent.scvk.vk, sourceEvent.scvk.sc);

						thisEventHasBeenLogged = true;
					}

					return 0; // No CallNextHookEx(). See comments further below.
				} // case HC_GETNEXT.

				case HC_SKIP: // Advance to the next mouse/keyboard event, if any.
					// Advance to the next item, which is either a delay or an event (preps for next HC_GETNEXT).
					++currentEvent;
					// Although caller knows it has to do the tail-end delay (if any) since there's no way to
					// do a trailing delay at the end of playback, it may have put a delay at the end of the
					// array anyway for code simplicity.  For that reason and maintainability:
					// Skip over any delays that are present to discover if there is a next event.
					int u;

					for (u = currentEvent; u < eventPb.Count && eventPb[u].messagetype == 0; ++u) ;

					if (u == eventPb.Count) // No more events.
					{
						// MSDN implies in the following statement that it's acceptable (and perhaps preferable in
						// the case of a playback hook) for the hook to unhook itself: "The hook procedure can be in the
						// state of being called by another thread even after UnhookWindowsHookEx returns."
						script.HookThread.Unhook(script.playbackHook);
						script.playbackHook = 0; // Signal the installer of the hook that it's gone now.
						// The following is an obsolete method from pre-v1.0.44.  Do not reinstate it without adding handling
						// to MainWindowProc() to do "g_PlaybackHook = NULL" upon receipt of WM_CANCELJOURNAL.
						// PostMessage(g_hWnd, WM_CANCELJOURNAL, 0, 0); // v1.0.44: Post it to g_hWnd vs. NULL because it's a little safer (SEE COMMENTS in MsgSleep's WM_CANCELJOURNAL for why it's almost completely safe with NULL).
						// Above: By using WM_CANCELJOURNAL instead of a custom message, the creator of this hook can easily
						// use a message filter to watch for both a system-generated removal of the hook (via the user
						// pressing Ctrl-Esc. or Ctrl-Alt-Del) or one we generate here (though it's currently not implemented
						// that way because it would prevent journal playback to one of our thread's own windows).
					}
					else
						firstCallForThisEvent = true; // Reset to prepare for next HC_GETNEXT.

					return 0; // MSDN: The return value is used only if the hook code is HC_GETNEXT; otherwise, it is ignored.

				default:
					// Covers the following cases:
					//case HC_NOREMOVE: // MSDN: An application has called the PeekMessage function with wRemoveMsg set to PM_NOREMOVE, indicating that the message is not removed from the message queue after PeekMessage processing.
					//case HC_SYSMODALON:  // MSDN: A system-modal dialog box is being displayed. Until the dialog box is destroyed, the hook procedure must stop playing back messages.
					//case HC_SYSMODALOFF: // MSDN: A system-modal dialog box has been destroyed. The hook procedure must resume playing back the messages.
					//case(...aCode < 0...): MSDN docs specify that the hook should return in this case.
					//
					// MS gives some sample code at http://support.microsoft.com/default.aspx?scid=KB;EN-US;124835
					// about the proper values to return to avoid hangs on NT (it seems likely that this implementation
					// is compliant enough if you read between the lines).  Their sample code indicates that
					// "return CallNextHook()"  should be done for basically everything except HC_SKIP/HC_GETNEXT, so
					// as of 1.0.43.08, that is what is done here.
					// Testing shows that when a so-called system modal dialog is displayed (even if it isn't the
					// active window) playback stops automatically, probably because the system doesn't call the hook
					// during such times (only a "MsgBox 4096" has been tested so far).
					//
					// The first parameter uses g_PlaybackHook rather than NULL because MSDN says it's merely
					// "currently ignored", but in the older "Win32 hooks" article, it says that the behavior
					// may change in the future.
					return CallNextHookEx(script.playbackHook, code, wParam, ref lParam);
					// Except for the cases above, CallNextHookEx() is not called for performance and also because from
					// what I can tell from the MSDN docs and other examples, it is neither required nor desirable to do so
					// during playback's SKIP/GETNEXT.
					// MSDN: The return value is used only if the hook code is HC_GETNEXT; otherwise, it is ignored.
			} // switch().

			// Execution should never reach since all cases do their own custom return above.
		}

		/// <summary>
		/// This function is designed to be called from only one thread (the main thread) since it's not thread-safe.
		/// Playback hook only supports sending neutral modifiers.  Caller must ensure that any left/right modifiers
		/// such as VK_RCONTROL are translated into neutral (e.g. VK_CONTROL).
		/// </summary>
		/// <param name="keyAsModifiersLR"></param>
		/// <param name="vk"></param>
		/// <param name="sc"></param>
		/// <param name="eventFlags"></param>
		/// <param name="extraInfo"></param>
		internal override void PutKeybdEventIntoArray(uint keyAsModifiersLR, uint vk, uint sc, uint eventFlags, long extraInfo)
		{
			var key_up = (eventFlags & KEYEVENTF_KEYUP) != 0;

			// To make the SendPlay method identical in output to the other keystroke methods, have it generate
			// a leading down/up LControl event immediately prior to each RAlt event (with no key-delay).
			// This avoids having to add special handling to places like SetModifierLRState() to do AltGr things
			// differently when sending via playback vs. other methods.  The event order recorded by the journal
			// record hook is a little different than what the low-level keyboard hook sees, but I don't think
			// the order should matter in this case:
			//   sc  vk key  msg
			//   138 12 Alt  syskeydown (right vs. left scan code)
			//   01d 11 Ctrl keydown (left scan code) <-- In keyboard hook, normally this precedes Alt, not follows it. Seems inconsequential (testing confirms).
			//   01d 11 Ctrl keyup  (left scan code)
			//   138 12 Alt  syskeyup (right vs. left scan code)
			// Check for VK_MENU not VK_RMENU because caller should have translated it to neutral:
			if (vk == VK_MENU && sc == ScanCodes.RAlt && targetLayoutHasAltGr == ResultType.ConditionTrue && sendMode == SendModes.Play)
				// Must pass VK_CONTROL rather than VK_LCONTROL because playback hook requires neutral modifiers.
				PutKeybdEventIntoArray(MOD_LCONTROL, VK_CONTROL, ScanCodes.LControl, eventFlags, extraInfo); // Recursive call to self.

			// Above must be done prior to the capacity check below because above might add a new array item.

			// Keep track of the predicted modifier state for use in other places:
			if (key_up)
				eventModifiersLR &= ~keyAsModifiersLR;
			else
				eventModifiersLR |= keyAsModifiersLR;

			if (sendMode == SendModes.Input)
			{
				var thisEvent = new INPUT();
				thisEvent.type = INPUT_KEYBOARD;
				thisEvent.i.k.wVk = (ushort)vk;
				thisEvent.i.k.wScan = (ushort)((eventFlags & KEYEVENTF_UNICODE) != 0 ? sc : sc & 0xFF);
				thisEvent.i.k.dwFlags = eventFlags;
				thisEvent.i.k.dwExtraInfo = (ulong)extraInfo; // Although our hook won't be installed (or won't detect, in the case of playback), that of other scripts might be, so set this for them.
				thisEvent.i.k.time = 0; // Let the system provide its own timestamp, which might be more accurate for individual events if this will be a very long SendInput.
				eventSi.Add(thisEvent);
				hooksToRemoveDuringSendInput |= HookKeyboard; // Presence of keyboard hook defeats uninterruptibility of keystrokes.
			}
			else // Playback hook.
			{
				var thisEvent = new PlaybackEvent();

				if (vk == 0 && sc == 0)//Caller is signaling that aExtraInfo contains a delay/sleep event.
				{
					// Although delays at the tail end of the playback array can't be implemented by the playback
					// itself, caller wants them put in too.
					thisEvent.messagetype = 0; // Message number zero flags it as a delay rather than an actual event.
					thisEvent.time_to_wait = (uint)extraInfo;
				}
				else // A normal (non-delay) event for playback.
				{
					// By monitoring incoming events in a message/event loop, the following key combinations were
					// confirmed to be WM_SYSKEYDOWN vs. WM_KEYDOWN (up events weren't tested, so are assumed to
					// be the same as down-events):
					// Alt+Win
					// Alt+Shift
					// Alt+Capslock/Numlock/Scrolllock
					// Alt+AppsKey
					// Alt+F2/Delete/Home/End/Arrow/BS
					// Alt+Space/Enter
					// Alt+Numpad (tested all digits & most other keys, with/without Numlock ON)
					// F10 (by itself) / Win+F10 / Alt+F10 / Shift+F10 (but not Ctrl+F10)
					// By contrast, the following are not SYS: Alt+Ctrl, Alt+Esc, Alt+Tab (the latter two
					// are never received by msg/event loop probably because the system intercepts them).
					// So the rule appears to be: It's a normal (non-sys) key if Alt isn't down and the key
					// isn't F10, or if Ctrl is down. Though a press of the Alt key itself is a syskey unless Ctrl is down.
					// Update: The release of ALT is WM_KEYUP vs. WM_SYSKEYUP when it modified at least one key while it was down.
					if ((eventModifiersLR & (MOD_LCONTROL | MOD_RCONTROL)) != 0 // Control is down...
							|| (eventModifiersLR & (MOD_LALT | MOD_RALT)) == 0   // ... or: Alt isn't down and this key isn't Alt or F10...
							&& vk != VK_F10 && ((keyAsModifiersLR & (MOD_LALT | MOD_RALT)) == 0)
							|| (((eventModifiersLR & (MOD_LALT | MOD_RALT)) != 0) && key_up))//... or this is the release of Alt (for simplicity, assume that Alt modified something while it was down).
						thisEvent.messagetype = (uint)(key_up ? WM_KEYUP : WM_KEYDOWN);
					else
						thisEvent.messagetype = (uint)(key_up ? WM_SYSKEYUP : WM_SYSKEYDOWN);

					thisEvent.scvk.vk = (byte)vk;
					thisEvent.scvk.sc = (ushort)sc; // Don't omit the extended-key-bit because it is used later on.
				}

				eventPb.Add(thisEvent);
			}
		}

		/// <summary>
		/// This function is designed to be called from only one thread (the main thread) since it's not thread-safe.
		/// If the array-type is journal playback, caller should include MOUSEEVENTF_ABSOLUTE in eventFlags if the
		/// the mouse coordinates aX and aY are relative to the screen rather than the active window.
		/// </summary>
		/// <param name="eventFlags"></param>
		/// <param name="data"></param>
		/// <param name="x"></param>
		/// <param name="y"></param>
		internal override void PutMouseEventIntoArray(uint eventFlags, uint data, int x, int y)
		{
			if (sendMode == SendModes.Input)
			{
				var thisEvent = new INPUT();
				var sendLevel = ThreadAccessors.A_SendLevel;
				thisEvent.type = INPUT_MOUSE;
				thisEvent.i.m.dx = (x == CoordUnspecified) ? 0 : x; // v1.0.43.01: Must be zero if no change in position is
				thisEvent.i.m.dy = (y == CoordUnspecified) ? 0 : y; // desired (fixes compatibility with certain apps/games).
				thisEvent.i.m.dwFlags = eventFlags;
				thisEvent.i.m.mouseData = data;
				thisEvent.i.m.dwExtraInfo = (ulong)KeyIgnoreLevel(sendLevel); // Although our hook won't be installed (or won't detect, in the case of playback), that of other scripts might be, so set this for them.
				thisEvent.i.m.time = 0; // Let the system provide its own timestamp, which might be more accurate for individual events if this will be a very long SendInput.
				eventSi.Add(thisEvent);
				hooksToRemoveDuringSendInput |= HookMouse; // Presence of mouse hook defeats uninterruptibility of mouse clicks/moves.
			}
			else // Playback hook.
			{
				// Note: Delay events (sleeps), which are supported in playback mode but not SendInput, are always inserted
				// via PutKeybdEventIntoArray() rather than this function.
				var thisEvent = new PlaybackEvent();

				// Determine the type of event specified by caller, but also omit MOUSEEVENTF_MOVE so that the
				// follow variations can be differentiated:
				// 1) MOUSEEVENTF_MOVE by itself.
				// 2) MOUSEEVENTF_MOVE with a click event or wheel turn (in this case MOUSEEVENTF_MOVE is permitted but
				//    not required, since all mouse events in playback mode must have explicit coordinates at the
				//    time they're played back).
				// 3) A click event or wheel turn by itself (same remark as above).
				// Bits are isolated in what should be a future-proof way (also omits MSG_OFFSET_MOUSE_MOVE bit).
				switch (eventFlags & (0x1FFF & ~(int)MOUSEEVENTF.MOVE)) // v1.0.48: 0x1FFF vs. 0xFFF to support MOUSEEVENTF_HWHEEL.
				{
					case 0: thisEvent.messagetype = WM_MOUSEMOVE; break; // It's a movement without a click.

					// In cases other than the above, it's a click or wheel turn with optional WM_MOUSEMOVE too.
					case (uint)MOUSEEVENTF.LEFTDOWN: thisEvent.messagetype = WM_LBUTTONDOWN; break;

					case (uint)MOUSEEVENTF.LEFTUP: thisEvent.messagetype = WM_LBUTTONUP; break;

					case (uint)MOUSEEVENTF.RIGHTDOWN: thisEvent.messagetype = WM_RBUTTONDOWN; break;

					case (uint)MOUSEEVENTF.RIGHTUP: thisEvent.messagetype = WM_RBUTTONUP; break;

					case (uint)MOUSEEVENTF.MIDDLEDOWN: thisEvent.messagetype = WM_MBUTTONDOWN; break;

					case (uint)MOUSEEVENTF.MIDDLEUP: thisEvent.messagetype = WM_MBUTTONUP; break;

					case (uint)MOUSEEVENTF.XDOWN: thisEvent.messagetype = WM_XBUTTONDOWN; break;

					case (uint)MOUSEEVENTF.XUP: thisEvent.messagetype = WM_XBUTTONUP; break;

					case (uint)MOUSEEVENTF.WHEEL: thisEvent.messagetype = WM_MOUSEWHEEL; break;

					case (uint)MOUSEEVENTF.HWHEEL: thisEvent.messagetype = WM_MOUSEHWHEEL; break; // v1.0.48
						// WHEEL: No info comes into journal-record about which direction the wheel was turned (nor by how many
						// notches).  In addition, it appears impossible to specify such info when playing back the event.
						// Therefore, playback usually produces downward wheel movement (but upward in some apps like
						// Visual Studio).
				}

				// COORD_UNSPECIFIED_SHORT is used so that the very first event can be a click with unspecified
				// coordinates: it seems best to have the cursor's position fetched during playback rather than
				// here because if done here, there might be time for the cursor to move physically before
				// playback begins (especially if our thread is preempted while building the array).
				thisEvent.pt.x = (x == CoordUnspecified) ? CoordUnspecifiedShort : (short)x;
				thisEvent.pt.y = (y == CoordUnspecified) ? CoordUnspecifiedShort : (short)y;

				if ((eventFlags & MsgOffsetMouseMove) != 0) // Caller wants this event marked as a movement relative to cursor's current position.
					thisEvent.messagetype |= MsgOffsetMouseMove;

				eventPb.Add(thisEvent);
			}
		}

		/// <summary>
		/// aFinalKeyDelay (which the caller should have initialized to -1 prior to calling) may be changed here
		/// to the desired/final delay.  Caller must not act upon it until changing sTypeOfHookToBuild to something
		/// that will allow DoKeyDelay() to do a real delay.
		/// </summary>
		/// <param name=""></param>
		/// <param name=""></param>
		/// <param name="modsDuringSend"></param>
		internal override void SendEventArray(ref long finalKeyDelay, uint modsDuringSend)
		{
			var script = Script.TheScript;
			var ht = script.HookThread;

			if (sendMode == SendModes.Input)
			{
				//if (eventSi.Count == 0)
				//return;
				// Remove hook(s) temporarily because the presence of low-level (LL) keybd hook completely disables
				// the uninterruptibility of SendInput's keystrokes (but the mouse hook doesn't affect them).
				// The converse is also true.  This was tested via:
				//  #space::
				//  SendInput {Click 400, 400, 100}
				//  MsgBox
				//  ExitApp
				// ... and also with BurnK6 running, a CPU maxing utility.  The mouse clicks were sent directly to the
				// BurnK6 window, and were pretty slow, and also I could physically move the mouse cursor a little
				// between each of sendinput's mouse clicks.  But removing the mouse-hook during SendInputs solves all that.
				// Rather than removing both hooks unconditionally, it's better to
				// remove only those that actually have corresponding events in the array.  This avoids temporarily
				// losing visibility of physical key states (especially when the keyboard hook is removed).
				HookType activeHooks;

				if ((activeHooks = ht.GetActiveHooks()) != HookType.None)
					ht.AddRemoveHooks((HookType)((int)activeHooks & ~hooksToRemoveDuringSendInput), true);

				_ = SendInput((uint)eventSi.Count, eventSi.ToArray(), Marshal.SizeOf(typeof(INPUT))); // Must call dynamically-resolved version for Win95/NT compatibility.

				// The return value is ignored because it never seems to be anything other than sEventCount, even if
				// the Send seems to partially fail (e.g. due to hitting 5000 event maximum).
				// Typical speed of SendInput: 10ms or less for short sends (under 100 events).
				// Typically 30ms for 500 events; and typically no more than 200ms for 5000 events (which is
				// the apparent max).
				// Testing shows that when SendInput returns to its caller, all of its key states are in effect
				// even if the target window hasn't yet had time to receive them all.  For example, the
				// following reports that LShift is down:
				//   SendInput {a 4900}{LShift down}
				//   MsgBox % GetKeyState("LShift")
				// Furthermore, if the user manages to physically press or release a key during the call to
				// SendInput, testing shows that such events are in effect immediately when SendInput returns
				// to its caller, perhaps because SendInput clear out any backlog of physical keystrokes prior to
				// returning, or perhaps because the part of the OS that updates key states is a very high priority.
				if (activeHooks != HookType.None)
				{
					if (((int)activeHooks & hooksToRemoveDuringSendInput & HookKeyboard) != 0) // Keyboard hook was actually removed during SendInput.
					{
						// The above call to SendInput() has not only sent its own events, it has also emptied
						// the buffer of any events generated outside but during the SendInput.  Since such
						// events are almost always physical events rather than simulated ones, it seems to do
						// more good than harm on average to consider any such changes to be physical.
						// The g_PhysicalKeyState array is also updated by GetModifierLRState(true), but only
						// for the modifier keys, not for all keys on the keyboard.  Even if adjust all keys
						// is possible, it seems overly complex and it might impact performance more than it's
						// worth given the rarity of the user changing physical key states during a SendInput
						// and then wanting to explicitly retrieve that state via GetKeyState(Key, "P").
						var modsCurrent = GetModifierLRState(true); // This also serves to correct the hook's logical modifiers, since hook was absent during the SendInput.
						var modsChangedPhysicallyDuringSend = modsDuringSend ^ modsCurrent;
						modifiersLRPhysical &= ~(modsChangedPhysicallyDuringSend & modsDuringSend); // Remove those that changed from down to up.
						modifiersLRPhysical |= modsChangedPhysicallyDuringSend & modsCurrent; // Add those that changed from up to down.
						modifiersLRLogical = modifiersLRLogicalNonIgnored = modsCurrent; // Necessary for hotkeys to be recognized correctly if modifiers were sent.
						ht.hsHwnd = GetForegroundWindow(); // An item done by ResetHook() that seems worthwhile here.
						// Most other things done by ResetHook() seem like they would do more harm than good to reset here
						// because of the the time the hook is typically missing is very short, usually under 30ms.
					}

					ht.AddRemoveHooks(activeHooks, true); // Restore the hooks that were active before the SendInput.
				}

				return;
			}

			// Since above didn't return, sSendMode == SM_PLAY.
			// It seems best not to call IsWindowHung() here because:
			// 1) It might improve script reliability to allow playback to a hung window because even though
			//    the entire system would appear frozen, if the window becomes unhung, the keystrokes would
			//    eventually get sent to it as intended (and the script may be designed to rely on this).
			//    Furthermore, the user can press Ctrl-Alt-Del or Ctrl-Esc to unfreeze the system.
			// 2) It might hurt performance.
			//
			// During journal playback, it appears that LL hook receives events in realtime; its just that
			// keystrokes the hook passes through (or generates itself) don't actually hit the active window
			// until after the playback is done.  Preliminary testing shows that the hook's disguise of Alt/Win
			// still function properly for Win/Alt hotkeys that use the playback method.
			currentEvent = 0; // Reset for use by the hook below.  Should be done BEFORE the hook is installed in the next line.
			/*  JOURNAL_RECORD_MODE
			            // To record and analyze events via the above:
			            // - Uncomment the line that defines this in the header file.
			            // - Put breakpoint after the hook removes itself (a few lines below).  Don't try to put breakpoint in RECORD hook
			            //   itself because it tends to freeze keyboard input (must press Ctrl-Alt-Del or Ctrl-Esc to unfreeze).
			            // - Have the script send a keystroke (best to use non-character keystroke such as SendPlay {Shift}).
			            // - It is now recording, so press the desired keys.
			            // - Press Ctrl+Break, Ctrl-Esc, or Ctrl-Alt-Del to stop recording (which should then hit breakpoint below).
			            // - Study contents of the sEventPB array, which contains the keystrokes just recorded.
			            eventCount = 0; // Used by RecordProc().

			            if ((script.TheScript.playbackHook = SetWindowsHookEx(WH_JOURNALRECORD, RecordProc, WindowsAPI.GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName), 0)) == 0)
			                return;

			*/
			_ = ht.Invoke(() => Script.TheScript.playbackHook = SetWindowsHookEx(WH_JOURNALPLAYBACK, PlaybackHandler, Marshal.GetHINSTANCE(typeof(Script).Module), 0));

			if (script.playbackHook == 0)
				return;

			// During playback, have the keybd hook (if it's installed) block presses of the Windows key.
			// This is done because the Windows key is about the only key (other than Ctrl-Esc or Ctrl-Alt-Del)
			// that takes effect immediately rather than getting buffered/postponed until after the playback.
			// It should be okay to set this after the playback hook is installed because playback shouldn't
			// actually begin until we have our thread do its first MsgSleep later below.
			ht.blockWinKeys = true;

			// Otherwise, hook is installed, so:
			// Wait for the hook to remove itself because the script should not be allowed to continue
			// until the Send finishes.
			// GetMessage(single_msg_filter) can't be used because then our thread couldn't playback
			// keystrokes to one of its own windows.  In addition, testing shows that it wouldn't
			// measurably improve performance anyway.
			// Note: User can't activate tray icon with mouse (because mouse is blocked), so there's
			// no need to call our main event loop merely to have the tray menu responsive.
			// Sleeping for 0 performs at least 15% worse than INTERVAL_UNSPECIFIED. I think this is
			// because the journal playback hook can operate only when this thread is in a message-pumping
			// state, and message pumping is far more efficient with GetMessage than PeekMessage.
			// Also note that both registered and hook hotkeys are noticed/caught during journal playback
			// (confirmed through testing).  However, they are kept buffered until the Send finishes
			// because ACT_SEND and such are designed to be uninterruptible by other script threads;
			// also, it would be undesirable in almost any conceivable case.
			//
			// Use a loop rather than a single call to MsgSleep(WAIT_FOR_MESSAGES) because
			// WAIT_FOR_MESSAGES is designed only for use by WinMain().  The loop doesn't measurably
			// affect performance because there used to be the following here in place of the loop,
			// and it didn't perform any better:
			// GetMessage(&msg, NULL, WM_CANCELJOURNAL, WM_CANCELJOURNAL);
			while (script.playbackHook != 0)
				Flow.SleepWithoutInterruption(Flow.intervalUnspecified); // For maintainability, macro is used rather than optimizing/splitting the code it contains.

			ht.blockWinKeys = false;

			// Either the hook unhooked itself or the OS did due to Ctrl-Esc or Ctrl-Alt-Del.
			// MSDN: When an application sees a [system-generated] WM_CANCELJOURNAL message, it can assume
			// two things: the user has intentionally cancelled the journal record or playback mode,
			// and the system has already unhooked any journal record or playback hook procedures.
			if (eventPb.Count > 0 && eventPb[eventPb.Count - 1].messagetype == 0) // Playback hook can't do the final delay, so we do it here.
				finalKeyDelay = (int)eventPb[eventPb.Count - 1].time_to_wait;// Don't do delay right here because the delay would be put into the array instead.

			// GetModifierLRState(true) is not done because keystrokes generated by the playback hook
			// aren't really keystrokes in the sense that they affect global key state or modifier state.
			// They affect only the keystate retrieved when the target thread calls GetKeyState()
			// (GetAsyncKeyState can never see such changes, even if called from the target thread).
			// Furthermore, the hook (if present) continues to operate during journal playback, so it
			// will keep its own modifiers up-to-date if any physical or simulate keystrokes happen to
			// come in during playback (such keystrokes arrive in the hook in real time, but they don't
			// actually hit the active window until the playback finishes).
		}

		/// <summary>
		/// thisHotkeyModifiersLR, if non-zero,
		/// should be the set of modifiers used to trigger the hotkey that called the subroutine
		/// containing the Send that got us here.  If any of those modifiers are still down,
		/// they will be released prior to sending the batch of keys specified in <keys>.
		/// v1.0.43: sendModeOrig was added.
		/// </summary>
		/// <param name="keys"></param>
		/// <param name="sendRaw"></param>
		/// <param name="sendModeOrig"></param>
		/// <param name="targetWindow"></param>
		internal override void SendKeys(string keys, SendRawModes sendRaw, SendModes sendModeOrig, nint targetWindow)
		{
			if (keys?.Length == 0)
				return;

			var script = Script.TheScript;
			var origLastPeekTime = script.lastPeekTime;
			var modsExcludedFromBlind = 0u;// For performance and also to reserve future flexibility, recognize {Blind} only when it's the first item in the string.
			var i = 0;
			var sub = keys.AsSpan();
			var ht = script.HookThread;

			if (inBlindMode = ((sendRaw == SendRawModes.NotRaw) && keys.StartsWith("{Blind", StringComparison.OrdinalIgnoreCase))) // Don't allow {Blind} while in raw mode due to slight chance {Blind} is intended to be sent as a literal string.
			{
				// Blind Mode (since this seems too obscure to document, it's mentioned here):  Blind Mode relies
				// on modifiers already down for something like ^c because ^c is saying "manifest a ^c", which will
				// happen if ctrl is already down.  By contrast, Blind does not release shift to produce lowercase
				// letters because avoiding that adds flexibility that couldn't be achieved otherwise.
				// Thus, ^c::Send {Blind}c produces the same result when ^c is substituted for the final c.
				// But Send {Blind}{LControl down} will generate the extra events even if ctrl already down.
				var modMask = MODLR_MASK;
				var keySpan = keys.AsSpan(6);

				for (i = 0; i < keySpan.Length && keySpan[i] != '}'; ++i)
				{
					uint mod = 0;

					switch (keySpan[i])
					{
						case '<': modMask = MODLR_LMASK; continue;

						case '>': modMask = MODLR_RMASK; continue;

						case '^': mod = MOD_LCONTROL | MOD_RCONTROL; break;

						case '+': mod = MOD_LSHIFT | MOD_RSHIFT; break;

						case '!': mod = MOD_LALT | MOD_RALT; break;

						case '#': mod = MOD_LWIN | MOD_RWIN; break;

						case '\0': return; // Just ignore the error.
					}

					modsExcludedFromBlind |= mod & modMask;
					modMask = MODLR_MASK; // Reset for the next modifier.
				}

				sub = keySpan.Slice(i);
			}

			if ((sendRaw == SendRawModes.NotRaw) && sub.StartsWith("{Text}", StringComparison.OrdinalIgnoreCase))
			{
				// Setting this early allows CapsLock and the Win+L workaround to be skipped:
				sendRaw = SendRawModes.RawText;
				sub = sub.Slice(6);
			}

			var tv = script.Threads.CurrentThread.configData;
			var origKeyDelay = tv.keyDelay;
			var origPressDuration = tv.keyDuration;

			if (sendModeOrig == SendModes.Input || sendModeOrig == SendModes.InputThenPlay) // Caller has ensured aTargetWindow==NULL for SendInput and SendPlay modes.
			{
				// Both of these modes fall back to a different mode depending on whether some other script
				// is running with a keyboard/mouse hook active.  Of course, the detection of this isn't foolproof
				// because older versions of AHK may be running and/or other apps with LL keyboard hooks. It's
				// just designed to add a lot of value for typical usage because SendInput is preferred due to it
				// being considerably faster than SendPlay, especially for long replacements when the CPU is under
				// heavy load.
				if (ht.SystemHasAnotherKeybdHook() // This function has been benchmarked to ensure it doesn't yield our timeslice, etc.  200 calls take 0ms according to tick-count, even when CPU is maxed.
						|| ((sendRaw == SendRawModes.NotRaw) && ht.SystemHasAnotherMouseHook() && sub.Contains("{Click", StringComparison.OrdinalIgnoreCase))) // Ordered for short-circuit boolean performance.  v1.0.43.09: Fixed to be strcasestr vs. !strcasestr
				{
					// Need to detect in advance what type of array to build (for performance and code size).  That's why
					// it done this way, and here are the comments about it:
					// strcasestr() above has an unwanted amount of overhead if aKeys is huge, but it seems acceptable
					// because it's called only when system has another mouse hook but *not* another keybd hook (very rare).
					// Also, for performance reasons, {LButton and such are not checked for, which is documented and seems
					// justified because the new {Click} method is expected to become prevalent, especially since this
					// whole section only applies when the new SendInput mode is in effect.
					// Finally, checking aSendRaw isn't foolproof because the string might contain {Raw} prior to {Click,
					// but the complexity and performance of checking for that seems unjustified given the rarity,
					// especially since there are almost never any consequences to reverting to hook mode vs. SendInput.
					if (sendModeOrig == SendModes.InputThenPlay)
						sendModeOrig = SendModes.Play;
					else // aSendModeOrig == SM_INPUT, so fall back to EVENT.
					{
						sendModeOrig = SendModes.Event;
						// v1.0.43.08: When SendInput reverts to SendEvent mode, the majority of users would want
						// a fast sending rate that is more comparable to SendInput's speed that the default KeyDelay
						// of 10ms.  PressDuration may be generally superior to KeyDelay because it does a delay after
						// each changing of modifier state (which tends to improve reliability for certain apps).
						// The following rules seem likely to be the best benefit in terms of speed and reliability:
						// KeyDelay 0+,-1+ --> -1, 0
						// KeyDelay -1, 0+ --> -1, 0
						// KeyDelay -1,-1 --> -1, -1
						tv.keyDuration = (tv.keyDelay < 0L && tv.keyDuration < 0L) ? -1L : 0L;
						tv.keyDelay = -1L; // Above line must be done before this one.
					}
				}
				else // SendInput is available and no other impacting hooks are obviously present on the system, so use SendInput unconditionally.
					sendModeOrig = SendModes.Input; // Resolve early so that other sections don't have to consider SM_INPUT_FALLBACK_TO_PLAY a valid value.
			}

			// Might be better to do this prior to changing capslock state.  UPDATE: In v1.0.44.03, the following section
			// has been moved to the top of the function because:
			// 1) For ControlSend, GetModifierLRState() might be more accurate if the threads are attached beforehand.
			// 2) Determines sTargetKeybdLayout and sTargetLayoutHasAltGr early (for maintainability).
			var threadsAreAttached = false; // Set default.
			uint keybdLayoutThread = 0;     //
			uint targetThread = 0;
			var tempitem = WindowManager.CreateWindow(targetWindow);
			var pd = script.ProcessesData;

			if (targetWindow != 0) // Caller has ensured this is NULL for SendInput and SendPlay modes.
			{
				if ((targetThread = GetWindowThreadProcessId(targetWindow, out _)) != 0 // Assign.
						&& targetThread != pd.MainThreadID && !tempitem.IsHung)
				{
					threadsAreAttached = AttachThreadInput(pd.MainThreadID, targetThread, true);
					keybdLayoutThread = targetThread; // Testing shows that ControlSend benefits from the adapt-to-layout technique too.
				}

				//else no target thread, or it's our thread, or it's hung; so keep keybd_layout_thread at its default.
			}
			else
			{
				// v1.0.48.01: On Vista or later, work around the fact that an "L" keystroke (physical or artificial) will
				// lock the computer whenever either Windows key is physically pressed down (artificially releasing the
				// Windows key isn't enough to solve it because Win+L is apparently detected aggressively like
				// Ctrl-Alt-Delete.  Unlike the handling of SM_INPUT in another section, this one here goes into
				// effect for all Sends because waiting for an "L" keystroke to be sent would be too late since the
				// Windows would have already been artificially released by then, so IsKeyDownAsync() wouldn't be
				// able to detect when the user physically releases the key.
				if ((thisHotkeyModifiersLR & (MOD_LWIN | MOD_RWIN)) != 0 // Limit the scope to only those hotkeys that have a Win modifier, since anything outside that scope hasn't been fully analyzed.
						&& (DateTime.UtcNow - script.thisHotkeyStartTime).TotalMilliseconds < 50 // Ensure g_script.mThisHotkeyModifiersLR is up-to-date enough to be reliable.
						&& sendModeOrig != SendModes.Play // SM_PLAY is reported to be incapable of locking the computer.
						&& !inBlindMode // The philosophy of blind-mode is that the script should have full control, so don't do any waiting during blind mode.
						&& sendRaw != SendRawModes.RawText // {Text} mode does not trigger Win+L.
						&& mgr.CurrentThreadId() == pd.MainThreadID // Exclude the hook thread because it isn't allowed to call anything like MsgSleep, nor are any calls from the hook thread within the understood/analyzed scope of this workaround.
				   )
				{
					var waitForWinKeyRelease = false;

					if (sendRaw != SendRawModes.NotRaw)
					{
						waitForWinKeyRelease = sub.IndexOfAny(llChars) != -1; // StrChrAny(aKeys, ("Ll")) != NULL;
					}
					else
					{
						// It seems worthwhile to scan for any "L" characters to avoid waiting for the release
						// of the Windows key when there are no L's.  For performance and code size, the check
						// below isn't comprehensive (e.g. it fails to consider things like {L} and #L).
						// Although RegExMatch() could be used instead of the below, that would use up one of
						// the RegEx cache entries, plus it would probably perform worse.  So scan manually.
						int L_pos, brace_pos = 0;
						var bracesub = sub;

						for (waitForWinKeyRelease = false; (L_pos = bracesub.IndexOfAny(llCharsSv, brace_pos)) != -1;)
						{
							// Encountering a #L seems too rare, and the consequences too mild (or nonexistent), to
							// justify the following commented-out section:
							//if (L_pos > aKeys && L_pos[-1] == '#') // A simple check; it won't detect things like #+L.
							//  brace_pos = L_pos + 1;
							//else
							brace_pos = bracesub.IndexOfAny(bracecharsSv, L_pos + 1);

							if (brace_pos == -1 || bracesub[brace_pos] == '{') // See comment below.
							{
								waitForWinKeyRelease = true;
								break;
							}

							//else it found a '}' without a preceding '{', which means this "L" is inside braces.
							// For simplicity, ignore such L's (probably not a perfect check, but seems worthwhile anyway).
						}
					}

					if (waitForWinKeyRelease)
						while (ht.IsKeyDownAsync(VK_LWIN) || ht.IsKeyDownAsync(VK_RWIN)) // Even if the keyboard hook is installed, it seems best to use IsKeyDownAsync() vs. g_PhysicalKeyState[] because it's more likely to produce consistent behavior.
							Flow.SleepWithoutInterruption(Flow.intervalUnspecified); // Seems best not to allow other threads to launch, for maintainability and because SendKeys() isn't designed to be interruptible.
				}

				// v1.0.44.03: The following change is meaningful only to people who use more than one keyboard layout.
				// It seems that the vast majority of them would want the Send command (as well as other features like
				// Hotstrings and the Input command) to adapt to the keyboard layout of the active window (or target window
				// in the case of ControlSend) rather than sticking with the script's own keyboard layout.  In addition,
				// testing shows that this adapt-to-layout method costs almost nothing in performance, especially since
				// the active window, its thread, and its layout are retrieved only once for each Send rather than once
				// for each keystroke.
				// v1.1.27.01: Use the thread of the focused control, which may differ from the active window.
				nint tempzero = 0;
				keybdLayoutThread = WindowManager.GetFocusedCtrlThread(ref tempzero, 0);
			}

			targetKeybdLayout = GetKeyboardLayout(keybdLayoutThread); // If keybd_layout_thread==0, this will get our thread's own layout, which seems like the best/safest default.
			targetLayoutHasAltGr = LayoutHasAltGr(targetKeybdLayout);  // Note that WM_INPUTLANGCHANGEREQUEST is not monitored by MsgSleep for the purpose of caching our thread's keyboard layout.  This is because it would be unreliable if another msg pump such as MsgBox is running.  Plus it hardly helps perf. at all, and hurts maintainability.
			// Below is now called with "true" so that the hook's modifier state will be corrected (if necessary)
			// prior to every send.
			var modsCurrent = GetModifierLRState(true); // Current "logical" modifier state.
			// For any modifiers put in the "down" state by {xxx DownR}, keep only those which
			// are still logically down before each Send starts.  Otherwise each Send would reset
			// the modifier to "down" even after the user "releases" it by some other means.
			modifiersLRRemapped &= modsCurrent;
			// Make a best guess of what the physical state of the keys is prior to starting (there's no way
			// to be certain without the keyboard hook). Note: It's possible for a key to be down physically
			// but not logically such as when RControl, for example, is a suffix hotkey and the user is
			// physically holding it down.
			uint modsDownPhysicallyOrig, modsDownPhysicallyButNotLogicallyOrig;
			var ad = script.AccessorData;

			//if (hookId != 0)
			if (ht.HasKbdHook())
			{
				// Since hook is installed, use its more reliable tracking to determine which
				// modifiers are down.
				modsDownPhysicallyOrig = modifiersLRPhysical;
				modsDownPhysicallyButNotLogicallyOrig = modifiersLRPhysical & ~modifiersLRLogical;
			}
			else // Use best-guess instead.
			{
				// Even if TickCount has wrapped due to system being up more than about 49 days,
				// DWORD subtraction still gives the right answer as long as g_script.mThisHotkeyStartTime
				// itself isn't more than about 49 days ago:
				if ((DateTime.UtcNow - script.thisHotkeyStartTime).TotalMilliseconds < ad.hotkeyModifierTimeout) // Elapsed time < timeout-value
					modsDownPhysicallyOrig = modsCurrent & thisHotkeyModifiersLR; // Bitwise AND is set intersection.
				else
					// Since too much time as passed since the user pressed the hotkey, it seems best,
					// based on the action that will occur below, to assume that no hotkey modifiers
					// are physically down:
					modsDownPhysicallyOrig = 0;

				modsDownPhysicallyButNotLogicallyOrig = 0; // There's no way of knowing, so assume none.
			}

			// Any of the external modifiers that are down but NOT due to the hotkey are probably
			// logically down rather than physically (perhaps from a prior command such as
			// `Send "{CtrlDown}"`.  Since there's no way to be sure without the keyboard hook
			// or some driver-level monitoring, it seems best to assume that they are logically
			// vs. physically down.
			// persistent_modifiers_for_this_SendKeys contains the modifiers that we will not
			// attempt to change (e.g. `Send "A"` will not release LWin before sending "A" if
			// this value indicates that LWin is down).
			// This used to be = modifiersLR_current & ~modifiersLR_down_physically_and_logically,
			// but v1.0.13 added g_modifiersLR_persistent to limit it to only mods which the script
			// put into effect.  Old comment explains: [[ To improve the above, we now exclude from
			// the set of persistent modifiers any that weren't made persistent by this script.
			// Such a policy seems likely to do more good than harm as there have been cases where
			// a modifier was detected as persistent just because A_HotkeyModifierTimeout expired
			// while the user was still holding down the key, but then when the user released it,
			// this logic here would think it's still persistent and push it back down again
			// to enforce it as "always-down" during the send operation.  Thus, the key would
			// basically get stuck down even after the send was over. ]]
			// v2.0.13: The original bitmask ~modifiersLR_down_physically_and_logically is removed.
			// It was originally described as "all the down-keys in mods_current except any that are
			// physically down due to the hotkey itself".  For example, ^a::Send,b requires the user
			// to hold Ctrl to activate the hotkey, so he probably doesn't want to send ^b.
			// However, sModifiersLR_persistent achieves the purpose of preventing Ctrl from being
			// sent in such cases, while the original bitmask only serves to prevent {Ctrl Down} from
			// being persistent if the user happens to physically hold Ctrl (for any purpose).
			// For example, when Send "{Shift Down}" and *KEY::Send "a" are used in combination,
			// the result should be "A" regardless of whether KEY = Shift.
			modifiersLRPersistent &= modsCurrent;
			uint persistentModifiersForThisSendKeys;
			var modsReleasedForSelectiveBlind = 0u;

			if (inBlindMode)
			{
				// The following value is usually zero unless the user is currently holding down
				// some modifiers as part of a hotkey. These extra modifiers are the ones that
				// this send operation (along with all its calls to SendKey and similar) should
				// consider to be down for the duration of the Send (unless they go up via an
				// explicit {LWin up}, etc.)
				persistentModifiersForThisSendKeys = modsCurrent;

				if (modsExcludedFromBlind != 0) // Caller specified modifiers to exclude from Blind treatment.
				{
					persistentModifiersForThisSendKeys &= ~modsExcludedFromBlind;
					modsReleasedForSelectiveBlind = modsCurrent ^ persistentModifiersForThisSendKeys;
				}
			}
			else
			{
				persistentModifiersForThisSendKeys = modifiersLRPersistent;
			}

			// Above:
			// Keep sModifiersLR_persistent and persistent_modifiers_for_this_SendKeys in sync with each other from now on.
			// By contrast to persistent_modifiers_for_this_SendKeys, sModifiersLR_persistent is the lifetime modifiers for
			// this script that stay in effect between sends.  For example, "Send {LAlt down}" leaves the alt key down
			// even after the Send ends, by design.
			//
			// It seems best not to change persistent_modifiers_for_this_SendKeys in response to the user making physical
			// modifier changes during the course of the Send.  This is because it seems more often desirable that a constant
			// state of modifiers be kept in effect for the entire Send rather than having the user's release of a hotkey
			// modifier key, which typically occurs at some unpredictable time during the Send, to suddenly alter the nature
			// of the Send in mid-stride.  Another reason is to make the behavior of Send consistent with that of SendInput.
			// The default behavior is to turn the capslock key off prior to sending any keys
			// because otherwise lowercase letters would come through as uppercase and vice versa.
			// Remember that apps like MS Word have an auto-correct feature that might make it
			// wrongly seem that the turning off of Capslock below needs a Sleep(0) to take effect.
			var priorCapslockState = tv.storeCapsLockMode && !inBlindMode && sendRaw != SendRawModes.RawText
									 ? ToggleKeyState(VK_CAPITAL, ToggleValueType.Off)
									 : ToggleValueType.Invalid; // In blind mode, don't do store capslock (helps remapping and also adds flexibility).
			// sendMode must be set only after setting Capslock state above, because the hook method
			// is incapable of changing the on/off state of toggleable keys like Capslock.
			// However, it can change Capslock state as seen in the window to which playback events are being
			// sent; but the behavior seems inconsistent and might vary depending on OS type, so it seems best
			// not to rely on it.
			sendMode = sendModeOrig;

			if (sendMode != SendModes.Event) // Build an array.  We're also responsible for setting sendMode to SM_EVENT prior to returning.
			{
				maxEvents = sendMode == SendModes.Input ? MaxInitialEventsSI : MaxInitialEventsPB;
				InitEventArray(maxEvents, modsCurrent);
			}

			var kbd = script.KeyboardData;
			var blockinputPrev = kbd.blockInput;
			var doSelectiveBlockInput = (kbd.blockInputMode == ToggleValueType.Send || kbd.blockInputMode == ToggleValueType.SendAndMouse)
										&& sendMode == SendModes.Event && targetWindow == 0;

			if (doSelectiveBlockInput)
				_ = Keyboard.ScriptBlockInput(true); // Turn it on unconditionally even if it was on, since Ctrl-Alt-Del might have disabled it.

			var vk = 0u;
			var sc = 0u;
			var keyAsModifiersLR = 0u;
			uint? modsForNextKey = 0u;
			// Above: For v1.0.35, it was changed to modLR vs. mod so that AltGr keys such as backslash and '{'
			// are supported on layouts such as German when sending to apps such as Putty that are fussy about
			// which ALT key is held down to produce the character.
			var thisEventModifierDown = 0u;
			var keyTextLength = 0;
			var keyNameLength = 0;
			int endPos;//, spacePos;
			//char oldChar;
			var keyDownType = KeyDownTypes.Temp;
			var eventType = KeyEventTypes.KeyDown;
			long repeatCount = 0;
			int clickX = 0, clickY = 0;
			var moveOffset = false;
			uint placeholder = 0;
			//var msg = new Msg();//May not be needed.//TOOD
			var keyIndex = 0;

			//for (; *aKeys; ++aKeys, prevEventModifierDown = this_event_modifier_down)
			for (; keyIndex < sub.Length; ++keyIndex, prevEventModifierDown = thisEventModifierDown)
			{
				thisEventModifierDown = 0; // Set default for this iteration, overridden selectively below.

				if (sendMode == SendModes.Event)
					LongOperationUpdateForSendKeys(); // This does not measurably affect the performance of SendPlay/Event.

				var ch = sub[keyIndex];

				if (sendRaw == SendRawModes.NotRaw && sendKeyChars.Contains(ch))//  _tcschr(("^+!#{}"), *aKeys))
				{
					switch (ch)
					{
						case '^':
							if ((persistentModifiersForThisSendKeys & (MOD_LCONTROL | MOD_RCONTROL)) == 0)
								modsForNextKey |= MOD_LCONTROL;

							// else don't add it, because the value of mods_for_next_key may also used to determine
							// which keys to release after the key to which this modifier applies is sent.
							// We don't want persistent modifiers to ever be released because that's how
							// AutoIt2 behaves and it seems like a reasonable standard.
							continue;

						case '+':
							if ((persistentModifiersForThisSendKeys & (MOD_LSHIFT | MOD_RSHIFT)) == 0)
								modsForNextKey |= MOD_LSHIFT;

							continue;

						case '!':
							if ((persistentModifiersForThisSendKeys & (MOD_LALT | MOD_RALT)) == 0)
								modsForNextKey |= MOD_LALT;

							continue;

						case '#':
							if ((persistentModifiersForThisSendKeys & (MOD_LWIN | MOD_RWIN)) == 0)
								modsForNextKey |= MOD_LWIN;

							continue;

						case '}': continue;  // Important that these be ignored.  Be very careful about changing this, see below.

						case '{':
						{
							if ((endPos = sub.IndexOf('}', keyIndex + 1)) == -1) // Ignore it and due to rarity, don't reset mods_for_next_key.
								continue; // This check is relied upon by some things below that assume a '}' is present prior to the terminator.

							keyIndex = sub.FindFirstNotOf(SpaceTab, keyIndex + 1);

							if ((keyTextLength = (endPos - keyIndex)) == 0)
							{
								var lenok = sub.Length > endPos + 1;

								if (lenok && sub[endPos + 1] == '}')
								{
									// The literal string "{}}" has been encountered, which is interpreted as a single "}".
									++endPos;
									keyTextLength = 1;
								}
								else
								{
									var nextWord = ReadOnlySpan<char>.Empty;
									var braceTabIndex = sub.IndexOfAny(SpaceTab);

									if (braceTabIndex != -1)
										nextWord = sub.Slice(braceTabIndex).TrimStart();

									if (nextWord.Length > 0)// v1.0.48: Support "{} down}", "{} downtemp}" and "{} up}".
									{
										if (nextWord.StartsWith("Down", StringComparison.OrdinalIgnoreCase) // "Down" or "DownTemp" (or likely enough).
												|| nextWord.StartsWith("Up", StringComparison.OrdinalIgnoreCase))
										{
											if ((endPos = sub.IndexOf('}', keyIndex + 2)) == -1)//See comments at similar section above.
												continue;

											keyTextLength = endPos - keyIndex; // This result must be non-zero due to the checks above.
										}
										else
											goto bracecaseend;  // The loop's ++aKeys will now skip over the '}', ignoring it.
									}
									else // Empty braces {} were encountered (or all whitespace, but literal whitespace isn't sent).
										goto bracecaseend;  // The loop's ++aKeys will now skip over the '}', ignoring it.
								}
							}

							var subspan = sub.Slice(keyIndex, keyTextLength);

							if (subspan.StartsWith("Click", StringComparison.OrdinalIgnoreCase))
							{
								HookThread.ParseClickOptions(subspan.Slice(5).TrimStart(SpaceTab), ref clickX, ref clickY, ref vk
													 , ref eventType, ref repeatCount, ref moveOffset);

								if (repeatCount < 1) // Allow {Click 100, 100, 0} to do a mouse-move vs. click (but modifiers like ^{Click..} aren't supported in this case.
									MouseMove(ref clickX, ref clickY, ref placeholder, tv.defaultMouseSpeed, moveOffset);
								else // Use SendKey because it supports modifiers (e.g. ^{Click}) SendKey requires repeat_count>=1.
									SendKey(vk, 0, modsForNextKey.Value, persistentModifiersForThisSendKeys
											, repeatCount, eventType, 0, targetWindow, clickX, clickY, moveOffset);

								goto bracecaseend; // This {} item completely handled, so move on to next.
							}
							else if (subspan.StartsWith("Raw", StringComparison.OrdinalIgnoreCase)) // This is used by auto-replace hotstrings too.
							{
								// As documented, there's no way to switch back to non-raw mode afterward since there's no
								// correct way to support special (non-literal) strings such as {Raw Off} while in raw mode.
								sendRaw = SendRawModes.Raw;
								goto bracecaseend; // This {} item completely handled, so move on to next.
							}
							else if (subspan.StartsWith("Text", StringComparison.OrdinalIgnoreCase)) // Added in v1.1.27
							{
								if (subspan.Slice(4).TrimStart(SpaceTab).Length == 0)//Pointing at the closing '}'.
									sendRaw = SendRawModes.RawText;

								//else: ignore this {Text something} to reserve for future use.
								goto bracecaseend; // This {} item completely handled, so move on to next.
							}

							// Since above didn't "goto", this item isn't {Click}.
							eventType = KeyEventTypes.KeyDownAndUp;         // Set defaults.
							repeatCount = 1L;
							keyNameLength = keyTextLength;
							var splitct = 0;
							var firstSplit = ReadOnlySpan<char>.Empty;

							foreach (Range r in subspan.SplitAny(SpaceTab))
							{
								var split = subspan[r].Trim();

								if (split.Length > 0)
								{
									if (splitct == 0)
									{
										keyNameLength = split.Length;
										firstSplit = split;
									}
									else
									{
										var nextWord = split;
										subspan = firstSplit;

										if (nextWord.StartsWith("Down", StringComparison.OrdinalIgnoreCase))
										{
											eventType = KeyEventTypes.KeyDown;

											// v1.0.44.05: Added key_down_is_persistent (which is not initialized except here because
											// it's only applicable when event_type==KEYDOWN).  It avoids the following problem:
											// When a key is remapped to become a modifier (such as F1::Control), launching one of
											// the script's own hotkeys via F1 would lead to bad side-effects if that hotkey uses
											// the Send command. This is because the Send command assumes that any modifiers pressed
											// down by the script itself (such as Control) are intended to stay down during all
											// keystrokes generated by that script. To work around this, something like KeyWait F1
											// would otherwise be needed. within any hotkey triggered by the F1 key.
											if (nextWord.StartsWith("DownTemp", StringComparison.OrdinalIgnoreCase)) // "DownTemp" means non-persistent.
												keyDownType = KeyDownTypes.Temp;
											else if (nextWord.Length > 4 && char.ToUpper(nextWord[4]) == 'R') // "DownR" means treated as a physical modifier (R = remap); i.e. not kept down during Send, but restored after Send (unlike Temp).
												keyDownType = KeyDownTypes.Remap;
											else
												keyDownType = KeyDownTypes.Persistent;
										}
										else if (nextWord.StartsWith("Up", StringComparison.OrdinalIgnoreCase))
										{
											eventType = KeyEventTypes.KeyUp;
										}
										else if (!subspan.StartsWith("ASC", StringComparison.OrdinalIgnoreCase))
										{
											if (long.TryParse(nextWord, out var templ))
											{
												repeatCount = templ;//.Value;
											}
											else
											{
												_ = Dialogs.MsgBox($"Invalid character passed to Send(): {nextWord}", null, "16");
												return;
											}
										}
									}

									splitct++;
									// Above: If negative or zero, that is handled further below.
									// There is no complaint for values <1 to support scripts that want to conditionally send
									// zero keystrokes, e.g. Send {a %Count%}
								}
							}

							_ = ht.TextToVKandSC(subspan, ref vk, ref sc, ref modsForNextKey, targetKeybdLayout);

							if (repeatCount < 1L)
								goto bracecaseend; // Gets rid of one level of indentation. Well worth it.

							subspan = sub.Slice(1).TrimStart(SpaceTab);//Consider the entire string, minus the first {, below.

							if (vk != 0 || sc != 0)
							{
								bool? b = null;

								if ((keyAsModifiersLR = ht.KeyToModifiersLR(vk, sc, ref b)) != 0) // Assign
								{
									if (targetWindow == 0)
									{
										if (eventType == KeyEventTypes.KeyDown) // i.e. make {Shift down} have the same effect {ShiftDown}
										{
											thisEventModifierDown = vk;

											if (keyDownType == KeyDownTypes.Persistent) // v1.0.44.05.
												modifiersLRPersistent |= keyAsModifiersLR;
											else if (keyDownType == KeyDownTypes.Remap) // v1.1.27.00
												modifiersLRRemapped |= keyAsModifiersLR;

											persistentModifiersForThisSendKeys |= keyAsModifiersLR; // v1.0.44.06: Added this line to fix the fact that "DownTemp" should keep the key pressed down after the send.
										}
										else if (eventType == KeyEventTypes.KeyUp) // *not* KEYDOWNANDUP, since that would be an intentional activation of the Start Menu or menu bar.
										{
											DisguiseWinAltIfNeeded(vk);
											modifiersLRPersistent &= ~keyAsModifiersLR;
											modifiersLRRemapped &= ~keyAsModifiersLR;
											persistentModifiersForThisSendKeys &= ~keyAsModifiersLR;

											// Fix for v1.0.43: Also remove LControl if this key happens to be AltGr.
											if (vk == VK_RMENU && targetLayoutHasAltGr == ResultType.ConditionTrue) // It is AltGr.
												persistentModifiersForThisSendKeys &= ~MOD_LCONTROL;
										}

										// else must never change sModifiersLR_persistent in response to KEYDOWNANDUP
										// because that would break existing scripts.  This is because that same
										// modifier key may have been pushed down via {ShiftDown} rather than "{Shift Down}".
										// In other words, {Shift} should never undo the effects of a prior {ShiftDown}
										// or {Shift down}.
									}

									//else don't add this event to sModifiersLR_persistent because it will not be
									// manifest via keybd_event.  Instead, it will done via less intrusively
									// (less interference with foreground window) via SetKeyboardState() and
									// PostMessage().  This change is for ControlSend in v1.0.21 and has been
									// documented.
								}

								// Below: sModifiersLR_persistent stays in effect (pressed down) even if the key
								// being sent includes that same modifier.  Surprisingly, this is how AutoIt2
								// behaves also, which is good.  Example: Send, {AltDown}!f  ; this will cause
								// Alt to still be down after the command is over, even though F is modified
								// by Alt.
								SendKey(vk, sc, modsForNextKey.Value, persistentModifiersForThisSendKeys
										, repeatCount, eventType, keyAsModifiersLR, targetWindow);
							}
							else if (keyNameLength == 1) // No vk/sc means a char of length one is sent via special method.
							{
								// v1.0.40: SendKeySpecial sends only keybd_event keystrokes, not ControlSend style
								// keystrokes.
								// v1.0.43.07: Added check of event_type!=KEYUP, which causes something like Send {� up} to
								// do nothing if the curr. keyboard layout lacks such a key.  This is relied upon by remappings
								// such as F1::� (i.e. a destination key that doesn't have a VK, at least in English).
								if (eventType != KeyEventTypes.KeyUp) // In this mode, mods_for_next_key and event_type are ignored due to being unsupported.
								{
									if (targetWindow != 0)
									{
										// Although MSDN says WM_CHAR uses UTF-16, it seems to really do automatic
										// translation between ANSI and UTF-16; we rely on this for correct results:
										for (var ii = 0L; ii < repeatCount; ++ii)
											_ = PostMessage(targetWindow, WM_CHAR, subspan[0], 0);
									}
									else
										SendKeySpecial(subspan[0], repeatCount, modsForNextKey.Value | persistentModifiersForThisSendKeys);
								}
							}
							// See comment "else must never change sModifiersLR_persistent" above about why
							// !aTargetWindow is used below:
							else if ((vk = ht.TextToSpecial(subspan, ref eventType
															, ref persistentModifiersForThisSendKeys, targetWindow == 0)) != 0) // Assign.
							{
								if (targetWindow == 0)
								{
									if (eventType == KeyEventTypes.KeyDown)
										thisEventModifierDown = vk;
									else // It must be KEYUP because TextToSpecial() never returns KEYDOWNANDUP.
										DisguiseWinAltIfNeeded(vk);
								}

								// Since we're here, repeat_count > 0.
								// v1.0.42.04: A previous call to SendKey() or SendKeySpecial() might have left modifiers
								// in the wrong state (e.g. Send +{F1}{ControlDown}).  Since modifiers can sometimes affect
								// each other, make sure they're in the state intended by the user before beginning:
								SetModifierLRState(persistentModifiersForThisSendKeys
												   , sendMode == SendModes.Event ? eventModifiersLR : GetModifierLRState()
												   , targetWindow, false, false); // It also does DoKeyDelay(g->PressDuration).

								for (var ii = 0L; ii < repeatCount; ++ii)
								{
									// Don't tell it to save & restore modifiers because special keys like this one
									// should have maximum flexibility (i.e. nothing extra should be done so that the
									// user can have more control):
									SendKeyEvent(eventType, vk, 0, targetWindow, true);

									if (sendMode == SendModes.Event)
										LongOperationUpdateForSendKeys();
								}
							}
							else if (keyTextLength > 4 && subspan.StartsWith("ASC ", StringComparison.OrdinalIgnoreCase) && targetWindow == 0) // {ASC nnnnn}
							{
								// Include the trailing space in "ASC " to increase uniqueness (selectivity).
								// Also, sending the ASC sequence to window doesn't work, so don't even try:
								//GetBytes() should really work with spans but for some reason it doesn't.//.NET 9
								SendASC(Encoding.ASCII.GetBytes(subspan.Slice(3).TrimStart().ToString()));
								// Do this only once at the end of the sequence:
								DoKeyDelay(); // It knows not to do the delay for SM_INPUT.
							}
							else if (keyTextLength > 2 && subspan.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
							{
								// L24: Send a unicode value as shown by Character Map.
								var hexstop = subspan.FirstIndexOf(ch => !ch.IsHex(), 2);
								var hexsub = subspan.Slice(2, hexstop == -1 ? subspan.Length - 2 : hexstop - 2);

								if (long.TryParse(hexsub, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var uCode))
								{
									char wc1, wc2;

									if (uCode >= 0x10000)
									{
										// Supplementary characters are encoded as UTF-16 and split into two messages.
										uCode -= 0x10000;
										wc1 = (char)(0xd800 + ((uCode >> 10) & 0x3ff));
										wc2 = (char)(0xdc00 + (uCode & 0x3ff));
									}
									else
									{
										wc1 = (char)uCode;
										wc2 = (char)0;
									}

									if (targetWindow != 0)
									{
										// Although MSDN says WM_CHAR uses UTF-16, PostMessageA appears to truncate it to 8-bit.
										// This probably means it does automatic translation between ANSI and UTF-16.  Since we
										// specifically want to send a Unicode character value, use PostMessageW:
										_ = PostMessage(targetWindow, WM_CHAR, wc1, 0);

										if (wc2 != 0)
											_ = PostMessage(targetWindow, WM_CHAR, wc2, 0);
									}
									else
									{
										// Use SendInput in unicode mode if available, otherwise fall back to SendASC.
										// To know why the following requires sendMode != SM_PLAY, see SendUnicodeChar.
										if (sendMode != SendModes.Play)
										{
											SendUnicodeChar(wc1, modsForNextKey.Value | persistentModifiersForThisSendKeys);

											if (wc2 != 0)
												SendUnicodeChar(wc2, modsForNextKey.Value | persistentModifiersForThisSendKeys);
										}
										else // Note that this method generally won't work with Unicode characters except
										{
											// with specific controls which support it, such as RichEdit (tested on WordPad).
											var asc = new byte[8];
											asc[0] = (byte)'0';
											var str = ((int)uCode).ToString();
											var bytes = Encoding.ASCII.GetBytes(str);
											System.Array.Copy(bytes, 0, asc, 1, Math.Min(asc.Length - 1, bytes.Length));
											SendASC(asc);
										}
									}

									DoKeyDelay();
								}
								else
								{
									_ = Errors.ErrorOccurred($"Could not parse {hexsub} as a hexadecimal number when trying to send a unicode character.");
									return;
								}
							}

							//else do nothing since it isn't recognized as any of the above "else if" cases (see below).
							// If what's between {} is unrecognized, such as {Bogus}, it's safest not to send
							// the contents literally since that's almost certainly not what the user intended.
							// In addition, reset the modifiers, since they were intended to apply only to
							// the key inside {}.  Also, the below is done even if repeat-count is zero.
							bracecaseend: // This label is used to simplify the code without sacrificing performance.
							keyIndex = endPos;
							modsForNextKey = 0;
							continue;
						} // case '{'
					} // switch()
				} // if (!aSendRaw && strchr("^+!#{}", *aKeys))
				else // Encountered a character other than ^+!#{} ... or we're in raw mode.
				{
					if (sendRaw == SendRawModes.RawText)
					{
						// \b needs to produce VK_BACK for auto-replace hotstrings to work (this is more useful anyway).
						// \r and \n need to produce VK_RETURN for decent compatibility.  SendKeySpecial('\n') works for
						// some controls (such as Scintilla) but has no effect in other common applications.
						// \t has more utility if translated to VK_TAB.  SendKeySpecial('\t') has no effect in many
						// common cases, and seems to only work in cases where {tab} would work just as well.
						if (sub[keyIndex] == '\r' && sub[keyIndex + 1] == '\n') // Translate \r but ignore any trailing \n, since \r\n -> {Enter 2} is counter-intuitive.
							keyIndex++;

						vk = sub[keyIndex] switch
					{
							'\n' => VK_RETURN,
							'\b' => VK_BACK,
							'\t' => VK_TAB,
							_ => 0,
					};
				}
				else
				{
					// Best to call this separately, rather than as first arg in SendKey, since it changes the
					// value of modifiers and the updated value is *not* guaranteed to be passed.
					// In other words, SendKey(TextToVK(...), modifiers, ...) would often send the old
					// value for modifiers.
					vk = ht.CharToVKAndModifiers(sub[keyIndex], ref modsForNextKey, targetKeybdLayout
												 , (modsForNextKey | persistentModifiersForThisSendKeys) != 0 && sendRaw == SendRawModes.NotRaw); // v1.1.27.00: Disable the a-z to vk41-vk5A fallback translation when modifiers are present since it would produce the wrong printable characters.
						// CharToVKAndModifiers() takes no measurable time compared to the amount of time SendKey takes.
					}

					if (vk != 0)
						SendKey(vk, 0, modsForNextKey.Value, persistentModifiersForThisSendKeys, 1, KeyEventTypes.KeyDownAndUp, 0, targetWindow);
					else // Try to send it by alternate means.
					{
						// In this mode, mods_for_next_key is ignored due to being unsupported.
						if (targetWindow != 0)
							// Although MSDN says WM_CHAR uses UTF-16, it seems to really do automatic
							// translation between ANSI and UTF-16; we rely on this for correct results:
							_ = PostMessage(targetWindow, WM_CHAR, sub[keyIndex], 0);
						else
							SendKeySpecial(sub[keyIndex], 1, modsForNextKey.Value | persistentModifiersForThisSendKeys);
					}

					modsForNextKey = 0;  // Safest to reset this regardless of whether a key was sent.
				}
			} // for()

			uint modsToSet;

			if (sendMode != SendModes.Event)
			{
				var finalKeyDelay = -1L;  // Set default.

				if (!abortArraySend && TotalEventCount() > 0) // Check for zero events for performance, but more importantly because playback hook will not operate correctly with zero.
				{
					// Add more events to the array (prior to sending) to support the following:
					// Restore the modifiers to match those the user is physically holding down, but do it as *part*
					// of the single SendInput/Play call.  The reasons it's done here as part of the array are:
					// 1) It avoids the need for A_HotkeyModifierTimeout (and it's superior to it) for both SendInput
					//    and SendPlay.
					// 2) The hook will not be present during the SendInput, nor can it be reinstalled in time to
					//    catch any physical events generated by the user during the Send. Consequently, there is no
					//    known way to reliably detect physical keystate changes.
					// 3) Changes made to modifier state by SendPlay are seen only by the active window's thread.
					//    Thus, it would be inconsistent and possibly incorrect to adjust global modifier state
					//    after (or during) a SendPlay.
					// So rather than resorting to A_HotkeyModifierTimeout, we can restore the modifiers within the
					// protection of SendInput/Play's uninterruptibility, allowing the user's buffered keystrokes
					// (if any) to hit against the correct modifier state when the SendInput/Play completes.
					// For example, if #c:: is a hotkey and the user releases Win during the SendInput/Play, that
					// release would hit after SendInput/Play restores Win to the down position, and thus Win would
					// not be stuck down.  Furthermore, if the user didn't release Win, Win would be in the
					// correct/intended position.
					// This approach has a few weaknesses (but the strengths appear to outweigh them):
					// 1) Hitting SendInput's 5000 char limit would omit the tail-end keystrokes, which would mess up
					//    all the assumptions here.  But hitting that limit should be very rare, especially since it's
					//    documented and thus scripts will avoid it.
					// 2) SendInput's assumed uninterruptibility is false if any other app or script has an LL hook
					//    installed.  This too is documented, so scripts should generally avoid using SendInput when
					//    they know there are other LL hooks in the system.  In any case, there's no known solution
					//    for it, so nothing can be done.
					modsToSet = persistentModifiersForThisSendKeys
								| modifiersLRRemapped // Restore any modifiers which were put in the down state by remappings or {key DownR} prior to this Send.
								| (inBlindMode ? modsReleasedForSelectiveBlind
								   : (modsDownPhysicallyOrig & ~modsDownPhysicallyButNotLogicallyOrig)); // The last item is usually 0.
					// Above: When in blind mode, don't restore physical modifiers.  This is done to allow a hotkey
					// such as the following to release Shift:
					//    +space::SendInput/Play {Blind}{Shift up}
					// Note that SendPlay can make such a change only from the POV of the target window; i.e. it can
					// release shift as seen by the target window, but not by any other thread; so the shift key would
					// still be considered to be down for the purpose of firing hotkeys (it can't change global key state
					// as seen by GetAsyncKeyState).
					// For more explanation of above, see a similar section for the non-array/old Send below.
					SetModifierLRState(modsToSet, eventModifiersLR, 0, true, true); // Disguise in case user released or pressed Win/Alt during the Send (seems best to do it even for SendPlay, though it probably needs only Alt, not Win).
					// mods_to_set is used further below as the set of modifiers that were explicitly put into effect at the tail end of SendInput.
					SendEventArray(ref finalKeyDelay, modsToSet);
				}

				CleanupEventArray(finalKeyDelay);
			}
			else // A non-array send is in effect, so a more elaborate adjustment to logical modifiers is called for.
			{
				// Determine (or use best-guess, if necessary) which modifiers are down physically now as opposed
				// to right before the Send began.
				var modsDownPhysically = 0u; // As compared to modsDownPhysicallyOrig.

				if (ht.HasKbdHook())
					modsDownPhysically = modifiersLRPhysical;
				else // No hook, so consult g_HotkeyModifierTimeout to make the determination.
					// Assume that the same modifiers that were phys+logically down before the Send are still
					// physically down (though not necessarily logically, since the Send may have released them),
					// but do this only if the timeout period didn't expire (or the user specified that it never
					// times out; i.e. elapsed time < timeout-value; DWORD subtraction gives the right answer even if
					// tick-count has wrapped around).
					modsDownPhysically = (ad.hotkeyModifierTimeout < 0 // It never times out or...
										  || (DateTime.UtcNow - script.thisHotkeyStartTime).TotalMilliseconds < ad.hotkeyModifierTimeout) // It didn't time out.
										 ? modsDownPhysicallyOrig : 0;

				// Put any modifiers in sModifiersLR_remapped back into effect, as if they were physically down.
				modsDownPhysically |= modifiersLRRemapped;
				// Restore the state of the modifiers to be those the user is physically holding down right now.
				// Any modifiers that are logically "persistent", as detected upon entrance to this function
				// (e.g. due to something such as a prior "Send, {LWinDown}"), are also pushed down if they're not already.
				// Don't press back down the modifiers that were used to trigger this hotkey if there's
				// any doubt that they're still down, since doing so when they're not physically down
				// would cause them to be stuck down, which might cause unwanted behavior when the unsuspecting
				// user resumes typing.
				// v1.0.42.04: Now that SendKey() is lazy about releasing Ctrl and/or Shift (but not Win/Alt),
				// the section below also releases Ctrl/Shift if appropriate.  See SendKey() for more details.
				modsToSet = persistentModifiersForThisSendKeys; // Set default.

				if (inBlindMode) // This section is not needed for the array-sending modes because they exploit uninterruptibility to perform a more reliable restoration.
				{
					// For selective {Blind!#^+}, restore any modifiers that were automatically released at the
					// start, such as for *^1::Send "{Blind^}2" when Ctrl+Alt+1 is pressed (Ctrl is released).
					// But do this before the below so that if the key was physically down at the start and has
					// since been released, it won't be pushed back down.
					modsToSet |= modsReleasedForSelectiveBlind;
					// At the end of a blind-mode send, modifiers are restored differently than normal. One
					// reason for this is to support the explicit ability for a Send to turn off a hotkey's
					// modifiers even if the user is still physically holding them down.  For example:
					//   #space::Send {LWin up}  ; Fails to release it, by design and for backward compatibility.
					//   #space::Send {Blind}{LWin up}  ; Succeeds, allowing LWin to be logically up even though it's physically down.
					var modsChangedPhysicallyDuringSend = modsDownPhysicallyOrig ^ modsDownPhysically;
					// Fix for v1.0.42.04: To prevent keys from getting stuck down, compensate for any modifiers
					// the user physically pressed or released during the Send (especially those released).
					// Remove any modifiers physically released during the send so that they don't get pushed back down:
					modsToSet &= ~(modsChangedPhysicallyDuringSend & modsDownPhysicallyOrig); // Remove those that changed from down to up.
					// Conversely, add any modifiers newly, physically pressed down during the Send, because in
					// most cases the user would want such modifiers to be logically down after the Send.
					// Obsolete comment from v1.0.40: For maximum flexibility and minimum interference while
					// in blind mode, never restore modifiers to the down position then.
					modsToSet |= modsChangedPhysicallyDuringSend & modsDownPhysically; // Add those that changed from up to down.
				}
				else // Regardless of whether the keyboard hook is present, the following formula applies.
					modsToSet |= modsDownPhysically & ~modsDownPhysicallyButNotLogicallyOrig; // The second item is usually 0.

				// Above takes into account the fact that the user may have pressed and/or released some modifiers
				// during the Send.
				// So it includes all keys that are physically down except those that were down physically but not
				// logically at the *start* of the send operation (since the send operation may have changed the
				// logical state).  In other words, we want to restore the keys to their former logical-down
				// position to match the fact that the user is still holding them down physically.  The
				// previously-down keys we don't do this for are those that were physically but not logically down,
				// such as a naked Control key that's used as a suffix without being a prefix.  More details:
				// mods_down_physically_but_not_logically_orig is used to distinguish between the following two cases,
				// allowing modifiers to be properly restored to the down position when the hook is installed:
				// 1) A naked modifier key used only as suffix: when the user phys. presses it, it isn't
				//    logically down because the hook suppressed it.
				// 2) A modifier that is a prefix, that triggers a hotkey via a suffix, and that hotkey sends
				//    that modifier.  The modifier will go back up after the SEND, so the key will be physically
				//    down but not logically.
				// Use KEY_IGNORE_ALL_EXCEPT_MODIFIER to tell the hook to adjust g_modifiersLR_logical_non_ignored
				// because these keys being put back down match the physical pressing of those same keys by the
				// user, and we want such modifiers to be taken into account for the purpose of deciding whether
				// other hotkeys should fire (or the same one again if auto-repeating):
				// v1.0.42.04: A previous call to SendKey() might have left Shift/Ctrl in the down position
				// because by procrastinating, extraneous keystrokes in examples such as "Send ABCD" are
				// eliminated (previously, such that example released the shift key after sending each key,
				// only to have to press it down again for the next one.  For this reason, some modifiers
				// might get released here in addition to any that need to get pressed down.  That's why
				// SetModifierLRState() is called rather than the old method of pushing keys down only,
				// never releasing them.
				// Put the modifiers in mods_to_set into effect.  Although "true" is passed to disguise up-events,
				// there generally shouldn't be any up-events for Alt or Win because SendKey() would have already
				// released them.  One possible exception to this is when the user physically released Alt or Win
				// during the send (perhaps only during specific sensitive/vulnerable moments).
				// g_modifiersLR_numpad_mask is used to work around an issue where our changes to shift-key state
				// trigger the system's shift-numpad handling (usually in combination with actual user input),
				// which in turn causes the Shift key to stick down.  If non-zero, the Shift key is currently "up"
				// but should be "released" anyway, since the system will inject Shift-down either before the next
				// keyboard event or after the Numpad key is released.  Find "fake shift" for more details.
				SetModifierLRState(modsToSet, GetModifierLRState() | modifiersLRNumpadMask, targetWindow, true, true); // It also does DoKeyDelay(g->PressDuration).
			} // End of non-array Send.

			// For peace of mind and because that's how it was tested originally, the following is done
			// only after adjusting the modifier state above (since that adjustment might be able to
			// affect the global variables used below in a meaningful way).
			if (ht.HasKbdHook())
			{
				// Ensure that g_modifiersLR_logical_non_ignored does not contain any down-modifiers
				// that aren't down in g_modifiersLR_logical.  This is done mostly for peace-of-mind,
				// since there might be ways, via combinations of physical user input and the Send
				// commands own input (overlap and interference) for one to get out of sync with the
				// other.  The below uses ^ to find the differences between the two, then uses & to
				// find which are down in non_ignored that aren't in logical, then inverts those bits
				// in g_modifiersLR_logical_non_ignored, which sets those keys to be in the up position:
				modifiersLRLogicalNonIgnored &= ~((modifiersLRLogical ^ modifiersLRLogicalNonIgnored)
												  & modifiersLRLogicalNonIgnored);
			}

			if (priorCapslockState == ToggleValueType.AlwaysOn) // The current user setting requires us to turn it back on.
				_ = ToggleKeyState(VK_CAPITAL, ToggleValueType.AlwaysOn);

			// Might be better to do this after changing capslock state, since having the threads attached
			// tends to help with updating the global state of keys (perhaps only under Win9x in this case):
			if (threadsAreAttached)
				_ = AttachThreadInput(pd.MainThreadID, targetThread, false);

			if (doSelectiveBlockInput && !blockinputPrev) // Turn it back off only if it was off before we started.
				_ = Keyboard.ScriptBlockInput(false);

			//THIS IS PROBABLY NOT NEEDED, SINCE WE PROCESS HOTKEYS ON A DIFFERENT THREAD ANYWAY, SO THERE SHOULDN'T BE ANY NON-CRITICAL BUFFERING.//TODO
			// The following MsgSleep(-1) solves unwanted buffering of hotkey activations while SendKeys is in progress
			// in a non-Critical thread.  Because SLEEP_WITHOUT_INTERRUPTION is used to perform key delays, any incoming
			// hotkey messages would be left in the queue.  It is not until the next interruptible sleep that hotkey
			// messages may be processed, and potentially discarded due to #MaxThreadsPerHotkey (even #MaxThreadsBuffer
			// should only allow one buffered activation).  But if the hotkey thread just calls Send in a loop and then
			// returns, it never performs an interruptible sleep, so the hotkey messages are processed one by one after
			// each new hotkey thread returns, even though Critical was not used.  Also note SLEEP_WITHOUT_INTERRUPTION
			// causes g_script.mLastScriptRest to be reset, so it's unlikely that a sleep would occur between Send calls.
			// To solve this, call MsgSleep(-1) now (unless no delays were performed, or the thread is uninterruptible):
			if (sendModeOrig == SendModes.Event && script.lastPeekTime != origLastPeekTime && script.Threads.IsInterruptible())
				_ = Flow.Sleep(0); // MsgSleep(-1);//MsgSleep() is going to be extremely hard to implement, so just do regular sleep for now until we get real threads implemented.//TODO

			// v1.0.43.03: Someone reported that when a non-autoreplace hotstring calls us to do its backspacing, the
			// hotstring's subroutine can execute a command that activates another window owned by the script before
			// the original window finished receiving its backspaces.  Although I can't reproduce it, this behavior
			// fits with expectations since our thread won't necessarily have a chance to process the incoming
			// keystrokes before executing the command that comes after SendInput.  If those command(s) activate
			// another of this thread's windows, that window will most likely intercept the keystrokes (assuming
			// that the message pump dispatches buffered keystrokes to whichever window is active at the time the
			// message is processed).
			// This fix does not apply to the SendPlay or SendEvent modes, the former due to the fact that it sleeps
			// a lot while the playback is running, and the latter due to key-delay and because testing has never shown
			// a need for it.
			if (sendModeOrig == SendModes.Input && GetWindowThreadProcessId(GetForegroundWindow(), out _) == pd.MainThreadID) // GetWindowThreadProcessId() tolerates a NULL hwnd.
				Flow.SleepWithoutInterruption(-1);

			// v1.0.43.08: Restore the original thread key-delay values in case above temporarily overrode them.
			tv.keyDelay = origKeyDelay;
			tv.keyDuration = origPressDuration;
		}

		internal void SendUnicodeChar(char ch, uint modifiers)
		{
			// Set modifier keystate as specified by caller.  Generally this will be 0, since
			// key combinations with Unicode packets either do nothing at all or do the same as
			// without the modifiers.  All modifiers are known to interfere in some applications.
			SetModifierLRState(modifiers, sendMode != SendModes.Event ? eventModifiersLR : GetModifierLRState(), 0, false, true, KeyIgnore);
			var sendLevel = ThreadAccessors.A_SendLevel;

			if (sendMode == SendModes.Input)
			{
				// Calling SendInput() now would cause characters to appear out of sequence.
				// Instead, put them into the array and allow them to be sent in sequence.
				PutKeybdEventIntoArray(0, 0, ch, KEYEVENTF_UNICODE, KeyIgnoreLevel(sendLevel));
				PutKeybdEventIntoArray(0, 0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, KeyIgnoreLevel(sendLevel));
				return;
			}

			//else caller has ensured sendMode is SM_EVENT. In that mode, events are sent one at a time,
			// so it is safe to immediately call SendInput(). SM_PLAY is not supported; for simplicity,
			// SendASC() is called instead of this function. Although this means Unicode chars probably
			// won't work, it seems better than sending chars out of order. One possible alternative could
			// be to "flush" the event array, but since SendInput and SendEvent are probably much more common,
			// this is left for a future version.
			var uInput = new INPUT[2];
			uInput[0].type = INPUT_KEYBOARD;
			uInput[0].i.k.wVk = 0;
			uInput[0].i.k.wScan = ch;
			uInput[0].i.k.dwFlags = KEYEVENTF_UNICODE;
			uInput[0].i.k.time = 0;
			// L25: Set dwExtraInfo to ensure AutoHotkey ignores the event; otherwise it may trigger a SCxxx hotkey (where xxx is u_code).
			uInput[0].i.k.dwExtraInfo = (ulong)KeyIgnoreLevel(sendLevel);
			uInput[1].type = INPUT_KEYBOARD;
			uInput[1].i.k.wVk = 0;
			uInput[1].i.k.wScan = ch;
			uInput[1].i.k.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
			uInput[1].i.k.time = 0;
			uInput[1].i.k.dwExtraInfo = (ulong)KeyIgnoreLevel(sendLevel);
			_ = SendInput(2, uInput, 40);// sizeof(INPUT));
		}

		internal override int SiEventCount() => eventSi.Count;

		/// <summary>
		/// Toggle the given vk into another state.  For performance, it is the caller's responsibility to
		/// ensure that vk is a toggleable key such as capslock, numlock, insert, or scrolllock.
		/// Returns the state the key was in before it was changed.
		/// </summary>
		/// <param name="vk"></param>
		/// <param name="toggleValue"></param>
		/// <returns></returns>
		internal override ToggleValueType ToggleKeyState(uint vk, ToggleValueType toggleValue)
		{
			var script = Script.TheScript;
			// Can't use IsKeyDownAsync/GetAsyncKeyState() because it doesn't have this info:
			var startingState = script.HookThread.IsKeyToggledOn(vk) ? ToggleValueType.On : ToggleValueType.Off;

			if (toggleValue != ToggleValueType.On && toggleValue != ToggleValueType.Off) // Shouldn't be called this way.
				return startingState;

			if (startingState == toggleValue) // It's already in the desired state, so just return the state.
				return startingState;

			//if (vk == VK_NUMLOCK) // v1.1.22.05: This also applies to CapsLock and ScrollLock.
			{
				// If the key is being held down, sending a KEYDOWNANDUP won't change its toggle
				// state unless the key is "released" first.  This has been confirmed for NumLock,
				// CapsLock and ScrollLock on Windows 2000 (in a VM) and Windows 10.
				// Examples where problems previously occurred:
				//   ~CapsLock & x::Send abc  ; Produced "ABC"
				//   ~CapsLock::Send abc  ; Alternated between "abc" and "ABC", even without {Blind}
				//   ~ScrollLock::SetScrollLockState Off  ; Failed to change state
				// The behavior can still be observed by sending the keystrokes manually:
				//   ~NumLock::Send {NumLock}  ; No effect
				//   ~NumLock::Send {NumLock up}{NumLock}  ; OK
				// OLD COMMENTS:
				// Sending an extra up-event first seems to prevent the problem where the Numlock
				// key's indicator light doesn't change to reflect its true state (and maybe its
				// true state doesn't change either).  This problem tends to happen when the key
				// is pressed while the hook is forcing it to be either ON or OFF (or it suppresses
				// it because it's a hotkey).  Needs more testing on diff. keyboards & OSes:
				if (script.HookThread.IsKeyDown(vk))
					SendKeyEvent(KeyEventTypes.KeyUp, vk);
			}
			// Since it's not already in the desired state, toggle it:
			SendKeyEvent(KeyEventTypes.KeyDownAndUp, vk);
			// Fix for v1.0.40: IsKeyToggledOn()'s call to GetKeyState() relies on our thread having
			// processed messages.  Confirmed necessary 100% of the time if our thread owns the active window.
			// v1.0.43: Extended the above fix to include all toggleable keys (not just Capslock) and to apply
			// to both directions (ON and OFF) since it seems likely to be needed for them all.
			bool ourThreadIsForeground;

			if (ourThreadIsForeground = GetWindowThreadProcessId(GetForegroundWindow(), out var _) == script.ProcessesData.MainThreadID) // GetWindowThreadProcessId() tolerates a NULL hwnd.
				Flow.SleepWithoutInterruption(-1);

			if (vk == VK_CAPITAL && toggleValue == ToggleValueType.Off && script.HookThread.IsKeyToggledOn(vk))
			{
				// Fix for v1.0.36.06: Since it's Capslock and it didn't turn off as attempted, it's probably because
				// the OS is configured to turn Capslock off only in response to pressing the SHIFT key (via Ctrl Panel's
				// Regional settings).  So send shift to do it instead:
				SendKeyEvent(KeyEventTypes.KeyDownAndUp, VK_SHIFT);

				if (ourThreadIsForeground) // v1.0.43: Added to try to achieve 100% reliability in all situations.
					Flow.SleepWithoutInterruption(-1); // Check msg queue to put SHIFT's turning off of Capslock into effect from our thread's POV.
			}

			return startingState;
		}

		protected internal override void LongOperationUpdate()
		{
			var msg = new Msg();
			var now = DateTime.UtcNow;
			var script = Script.TheScript;

			if ((now - script.lastPeekTime).TotalMilliseconds > ThreadAccessors.A_PeekFrequency)
			{
				if (PeekMessage(out msg, 0, 0, 0, PM_NOREMOVE))
					_ = Flow.Sleep(-1);

				now = DateTime.UtcNow;
				script.lastPeekTime = now;
			}
		}

		/// <summary>
		/// Same as the above except for SendKeys() and related functions (uses SLEEP_WITHOUT_INTERRUPTION vs. MsgSleep).
		/// </summary>
		protected internal override void LongOperationUpdateForSendKeys()
		{
			var msg = new Msg();
			var now = DateTime.UtcNow;
			var script = Script.TheScript;

			if ((now - script.lastPeekTime).TotalMilliseconds > ThreadAccessors.A_PeekFrequency)
			{
				if (PeekMessage(out msg, 0, 0, 0, PM_NOREMOVE))
					Flow.SleepWithoutInterruption(-1);

				now = DateTime.UtcNow;
				script.lastPeekTime = now;
			}
		}

		/*
		    protected internal override void Send(string keys)
		    {
		    if (keys.Length == 0)
		        return;

		    var len = keys.Length * 2;
		    var inputs = new INPUT[len];

		    for (var i = 0; i < keys.Length; i++)
		    {
		        uint flag = WindowsAPI.KEYEVENTF_UNICODE;

		        if ((keys[i] & 0xff00) == 0xe000)
		            flag |= WindowsAPI.KEYEVENTF_EXTENDEDKEY;

		        var down = new INPUT { type = WindowsAPI.INPUT_KEYBOARD };
		        down.i.k = new KEYBDINPUT { wScan = keys[i], dwFlags = flag };
		        var up = new INPUT { type = WindowsAPI.INPUT_KEYBOARD };
		        up.i.k = new KEYBDINPUT { wScan = keys[i], dwFlags = flag | WindowsAPI.KEYEVENTF_KEYUP };
		        var x = i * 2;
		        inputs[x] = down;
		        inputs[x + 1] = up;
		    }

		    ignore = true;
		    _ = WindowsAPI.SendInput((uint)len, inputs, Marshal.SizeOf(typeof(INPUT)));
		    ignore = false;
		    }
		*/

		/*
		    protected internal override void Send(Keys key)
		    {
		    //This is supposed to prevent modifer keys currently pressed from applying to the key which is sent, but it doesn't seem to work.
		    key &= ~Keys.Modifiers;

		    if (key == Keys.None)
		        return;

		    uint flag = WindowsAPI.KEYEVENTF_UNICODE;
		    var vk = (ushort)key;

		    if ((vk & 0xff00) == 0xe000)
		        flag |= WindowsAPI.KEYEVENTF_EXTENDEDKEY;

		    var down = new INPUT { type = WindowsAPI.INPUT_KEYBOARD };
		    down.i.k = new KEYBDINPUT
		    {
		        wVk = vk,//Was
		        //wVk = 0,
		        //wScan = vk,
		        dwFlags = flag
		    };
		    var up = new INPUT { type = WindowsAPI.INPUT_KEYBOARD };
		    up.i.k = new KEYBDINPUT
		    {
		        wVk = vk,//Was
		        //wVk = 0,
		        //wScan = vk,
		        dwFlags = flag | WindowsAPI.KEYEVENTF_KEYUP
		    };
		    var inputs = new[] { down, up };
		    ignore = true;
		    _ = WindowsAPI.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
		    ignore = false;
		    }
		*/

		/// <summary>
		/// aSC or vk (but not both), can be zero to cause the default to be used.
		/// For keys like NumpadEnter -- that have a unique scancode but a non-unique virtual key --
		/// caller can just specify the sc.  In addition, the scan code should be specified for keys
		/// like NumpadPgUp and PgUp.  In that example, the caller would send the same scan code for
		/// both except that PgUp would be extended.   sc_to_vk() would map both of them to the same
		/// virtual key, which is fine since it's the scan code that matters to apps that can
		/// differentiate between keys with the same vk.
		///
		/// Thread-safe: This function is not fully thread-safe because keystrokes can get interleaved,
		/// but that's always a risk with anything short of SendInput.  In fact,
		/// when the hook ISN'T installed, keystrokes can occur in another thread, causing the key state to
		/// change in the middle of KeyEvent, which is the same effect as not having thread-safe key-states
		/// such as GetKeyboardState in here.  Also, the odds of both our threads being in here simultaneously
		/// is greatly reduced by the fact that the hook thread never passes "true" for aDoKeyDelay, thus
		/// its calls should always be very fast.  Even if a collision does occur, there are thread-safety
		/// things done in here that should reduce the impact to nearly as low as having a dedicated
		/// KeyEvent function solely for calling by the hook thread (which might have other problems of its own).
		/// </summary>
		/// <param name="eventType"></param>
		/// <param name="vk"></param>
		/// <param name="sc"></param>
		/// <param name="targetWindow"></param>
		/// <param name="doKeyDelay"></param>
		/// <param name="extraInfo"></param>
		///
		protected internal override void SendKeyEvent(KeyEventTypes eventType, uint vk, uint sc = 0u, nint targetWindow = default, bool doKeyDelay = false, long extraInfo = KeyIgnoreAllExceptModifier)
		{
			if ((vk | sc) == 0)//If neither VK nor SC was specified, return.
				return;

			if (extraInfo == 0) // Shouldn't be called this way because 0 is considered false in some places below (search on " = aExtraInfo" to find them).
				extraInfo = KeyIgnoreAllExceptModifier; // Seems slightly better to use a standard default rather than something arbitrary like 1.

			// Since calls from the hook thread could come in even while the SendInput array is being constructed,
			// don't let those events get interspersed with the script's explicit use of SendInput.
			var ht = Script.TheScript.HookThread;
			//Note that the threading model in Keysharp is different than AHK, so this doesn't apply.
			//In AHK, the low level keyboard proc runs in its own thread, however in Keysharp it turns on the main window thread.
			//Further, after hours of extreme scrutiny in AHK, there seems to be no code in HookThreadProc() where a keyboard event could be sent here.
			//So just hard code callerIsKeybdHook to false.
			var callerIsKeybdHook = false;// WindowsAPI.GetCurrentThreadId() == Keysharp.Core.Processes.MainThreadID;//Hook runs on the main window thread.
			var putEventIntoArray = sendMode != SendModes.Event && !callerIsKeybdHook;

			if (sendMode == SendModes.Input || callerIsKeybdHook) // First check is necessary but second is just for maintainability.
				doKeyDelay = false;

			// Even if the sc_to_vk() mapping results in a zero-value vk, don't return.
			// I think it may be valid to send keybd_events that have a zero vk.
			// In any case, it's unlikely to hurt anything:
			if (vk == 0)
				vk = ht.MapScToVk(sc);
			else if (sc == 0)
				// In spite of what the MSDN docs say, the scan code parameter *is* used, as evidenced by
				// the fact that the hook receives the proper scan code as sent by the below, rather than
				// zero like it normally would.  Even though the hook would try to use MapVirtualKey() to
				// convert zero-value scan codes, it's much better to send it here also for full compatibility
				// with any apps that may rely on scan code (and such would be the case if the hook isn't
				// active because the user doesn't need it; also for some games maybe).
				sc = ht.MapVkToSc(vk);

			var scLowByte = sc & 0xFF;
			var eventFlags = ((sc >> 8) & 0xFF) != 0 ? KEYEVENTF_EXTENDEDKEY : 0u;

			// v1.0.43: Apparently, the journal playback hook requires neutral modifier keystrokes
			// rather than left/right ones.  Otherwise, the Shift key can't capitalize letters, etc.
			if (sendMode == SendModes.Play)
			{
				switch (vk)
				{
					case VK_LCONTROL:
					case VK_RCONTROL: vk = VK_CONTROL; break; // But leave scan code set to a left/right specific value rather than converting it to "left" unconditionally.

					case VK_LSHIFT:
					case VK_RSHIFT: vk = VK_SHIFT; break;

					case VK_LMENU:
					case VK_RMENU: vk = VK_MENU; break;
				}
			}

			// aTargetWindow is almost always passed in as NULL by our caller, even if the overall command
			// being executed is ControlSend.  This is because of the following reasons:
			// 1) Modifiers need to be logically changed via keybd_event() when using ControlSend against
			//    a cmd-prompt, console, or possibly other types of windows.
			// 2) If a hotkey triggered the ControlSend that got us here and that hotkey is a naked modifier
			//    such as RAlt:: or modified modifier such as ^#LShift, that modifier would otherwise auto-repeat
			//    an possibly interfere with the send operation.  This auto-repeat occurs because unlike a normal
			//    send, there are no calls to keybd_event() (keybd_event() stop the auto-repeat as a side-effect).
			// One exception to this is something like "ControlSend, Edit1, {Control down}", which explicitly
			// calls us with a target window.  This exception is by design and has been bug-fixed and documented
			// in ControlSend for v1.0.21:
			if (targetWindow != 0) // This block shouldn't affect overall thread-safety because hook thread never calls it in this mode.
			{
				bool? b = null;

				if (ht.KeyToModifiersLR(vk, sc, ref b) != 0)
				{
					// When sending modifier keystrokes directly to a window, use the AutoIt3 SetKeyboardState()
					// technique to improve the reliability of changes to modifier states.  If this is not done,
					// sometimes the state of the SHIFT key (and perhaps other modifiers) will get out-of-sync
					// with what's intended, resulting in uppercase vs. lowercase problems (and that's probably
					// just the tip of the iceberg).  For this to be helpful, our caller must have ensured that
					// our thread is attached to aTargetWindow's (but it seems harmless to do the below even if
					// that wasn't done for any reason).  Doing this here in this function rather than at a
					// higher level probably isn't best in terms of performance (e.g. in the case where more
					// than one modifier is being changed, the multiple calls to Get/SetKeyboardState() could
					// be consolidated into one call), but it is much easier to code and maintain this way
					// since many different functions might call us to change the modifier state:
					var state = keyStatePool.Rent(256);//Original did not clear state here, so presumably GetKeyboardState() overwrites all elements.
					_ = GetKeyboardState(state);

					if (eventType == KeyEventTypes.KeyDown)
						state[vk] |= 0x80;
					else if (eventType == KeyEventTypes.KeyUp)
						state[vk] &= 0x7F;//This is probably better, no error.

					//state[vk] &= ~0x80;//Compiler says this is an error.
					// else KEYDOWNANDUP, in which case it seems best (for now) not to change the state at all.
					// It's rarely if ever called that way anyway.

					// If vk is a left/right specific key, be sure to also update the state of the neutral key:
					switch (vk)
					{
						case VK_LCONTROL:
						case VK_RCONTROL:
							if ((state[VK_LCONTROL] & 0x80) != 0 || (state[VK_RCONTROL] & 0x80) != 0)
								state[VK_CONTROL] |= 0x80;
							else
								state[VK_CONTROL] &= 0x7F;

							break;

						case VK_LSHIFT:
						case VK_RSHIFT:
							if ((state[VK_LSHIFT] & 0x80) != 0 || (state[VK_RSHIFT] & 0x80) != 0)
								state[VK_SHIFT] |= 0x80;
							else
								state[VK_SHIFT] &= 0x7F;

							break;

						case VK_LMENU:
						case VK_RMENU:
							if ((state[VK_LMENU] & 0x80) != 0 || (state[VK_RMENU] & 0x80) != 0)
								state[VK_MENU] |= 0x80;
							else
								state[VK_MENU] &= 0x7F;

							break;
					}

					_ = SetKeyboardState(state);
					keyStatePool.Return(state);
					// Even after doing the above, we still continue on to send the keystrokes
					// themselves to the window, for greater reliability (same as AutoIt3).
				}

				// lowest 16 bits: repeat count: always 1 for up events, probably 1 for down in our case.
				// highest order bits: 11000000 (0xC0) for keyup, usually 00000000 (0x00) for keydown.
				var lParam = (long)(sc << 16);

				if (eventType != KeyEventTypes.KeyUp)  // i.e. always do it for KEYDOWNANDUP
					_ = PostMessage(targetWindow, WM_KEYDOWN, vk, (uint)(lParam | 0x00000001));

				// The press-duration delay is done only when this is a down-and-up because otherwise,
				// the normal g->KeyDelay will be in effect.  In other words, it seems undesirable in
				// most cases to do both delays for only "one half" of a keystroke:
				if (doKeyDelay && eventType == KeyEventTypes.KeyDownAndUp)
					DoKeyDelay(ThreadAccessors.A_KeyDuration); // Since aTargetWindow!=NULL, sendMode!=SM_PLAY, so no need for to ever use the SendPlay press-duration.

				if (eventType != KeyEventTypes.KeyDown)
					_ = PostMessage(targetWindow, WM_KEYUP, vk, (uint)(lParam | 0xC0000001));
			}
			else // Keystrokes are to be sent with keybd_event() or the event array rather than PostMessage().
			{
				// The following static variables are intentionally NOT thread-safe because their whole purpose
				// is to watch the combined stream of keystrokes from all our   Due to our threads'
				// keystrokes getting interleaved with the user's and those of other threads, this kind of
				// monitoring is never 100% reliable.  All we can do is aim for an astronomically low chance
				// of failure.
				// Users of the below want them updated only for keybd_event() keystrokes (not PostMessage ones):
				prevEventType = eventType;
				prevVK = vk;
				var tempTargetLayoutHasAltGr = (callerIsKeybdHook ? LayoutHasAltGr(GetFocusedKeybdLayout(0)) : targetLayoutHasAltGr) == ResultType.ConditionTrue; // i.e. not CONDITION_FALSE (which is nonzero) or FAIL (zero).
				var hookableAltGr = (vk == VK_RMENU) && tempTargetLayoutHasAltGr && !putEventIntoArray && ht.HasKbdHook();// hookId != 0;
				// Calculated only once for performance (and avoided entirely if not needed):
				bool? b = null;
				var keyAsModifiersLR = putEventIntoArray ? ht.KeyToModifiersLR(vk, sc, ref b) : 0;
				var doKeyHistory = !callerIsKeybdHook // If caller is hook, don't log because it does.
								   && sendMode != SendModes.Play// In playback mode, the journal hook logs so that timestamps are accurate.
								   && (!ht.HasKbdHook() || sendMode == SendModes.Input); // In the remaining cases, log only when the hook isn't installed or won't be at the time of the event.

				if (eventType != KeyEventTypes.KeyUp)  // i.e. always do it for KEYDOWNANDUP
				{
					if (putEventIntoArray)
						PutKeybdEventIntoArray(keyAsModifiersLR, vk, sc, eventFlags, extraInfo);
					else
					{
						// The following global is used to flag as our own the keyboard driver's LCtrl-down keystroke
						// that is triggered by RAlt-down (AltGr).  The hook prevents those keystrokes from triggering
						// hotkeys such as "*Control::" anyway, but this ensures the LCtrl-down is marked as "ignored"
						// and given the correct SendLevel.  It may fix other obscure side-effects and bugs, since the
						// event should be considered script-generated even though indirect.  Note: The problem with
						// having the hook detect AltGr's automatic LControl-down is that the keyboard driver seems
						// to generate the LControl-down *before* notifying the system of the RAlt-down.  That makes
						// it impossible for the hook to automatically adjust its SendLevel based on the RAlt-down.
						if (hookableAltGr)
							altGrExtraInfo = extraInfo;

						keybd_event((byte)vk, (byte)scLowByte, eventFlags, (uint)extraInfo);// naked scan code (the 0xE0 prefix, if any, is omitted)
						altGrExtraInfo = 0; // Unconditional reset.
					}

					if (doKeyHistory && ht.keyHistory is KeyHistory kh)
						kh.UpdateKeyEventHistory(false, vk, sc); // Should be thread-safe since if no hook means only one thread ever sends keystrokes (with possible exception of mouse hook, but that seems too rare).
				}

				// The press-duration delay is done only when this is a down-and-up because otherwise,
				// the normal g->KeyDelay will be in effect.  In other words, it seems undesirable in
				// most cases to do both delays for only "one half" of a keystroke:
				if (doKeyDelay && eventType == KeyEventTypes.KeyDownAndUp) // Hook should never specify a delay, so no need to check if caller is hook.
					DoKeyDelay(sendMode == SendModes.Play ? ThreadAccessors.A_KeyDurationPlay : ThreadAccessors.A_KeyDuration); // DoKeyDelay() is not thread safe but since the hook thread should never pass true for aKeyDelay, it shouldn't be an issue.

				if (eventType != KeyEventTypes.KeyDown)  // i.e. always do it for KEYDOWNANDUP
				{
					eventFlags |= KEYEVENTF_KEYUP;

					if (putEventIntoArray)
						PutKeybdEventIntoArray(keyAsModifiersLR, vk, sc, eventFlags, extraInfo);
					else
					{
						if (hookableAltGr) // See comments in similar section above for details.
							altGrExtraInfo = extraInfo;

						keybd_event((byte)vk, (byte)scLowByte, eventFlags, (uint)extraInfo);
						altGrExtraInfo = 0; // Unconditional reset.
					}

					// The following is done to avoid an extraneous artificial {LCtrl Up} later on,
					// since the keyboard driver should insert one in response to this {RAlt Up}:
					if (targetLayoutHasAltGr == ResultType.ConditionTrue && sc == ScanCodes.RAlt)
						eventModifiersLR &= ~MOD_LCONTROL;

					if (doKeyHistory && ht.keyHistory is KeyHistory kh)
						kh.UpdateKeyEventHistory(true, vk, sc);
				}
			}

			if (doKeyDelay) // SM_PLAY also uses DoKeyDelay(): it stores the delay item in the event array.
				DoKeyDelay(); // Thread-safe because only called by main thread in this mode.  See notes above.
		}

		//protected override void Backspace(int length)
		//{
		//  length *= 2;
		//  var inputs = new INPUT[length];
		//
		//  for (var i = 0; i < length; i += 2)
		//  {
		//      var down = new INPUT { type = WindowsAPI.INPUT_KEYBOARD };
		//      down.i.k = new KEYBDINPUT { wVk = VK_BACK };
		//      var up = new INPUT { type = WindowsAPI.INPUT_KEYBOARD };
		//      up.i.k = new KEYBDINPUT { wVk = VK_BACK, dwFlags = WindowsAPI.KEYEVENTF_KEYUP };
		//      inputs[i] = down;
		//      inputs[i + 1] = up;
		//  }
		//
		//  ignore = true;
		//  _ = WindowsAPI.SendInput((uint)length, inputs, Marshal.SizeOf(typeof(INPUT)));
		//  ignore = false;
		//}

		//protected override void DeregisterHook()
		//{
		//  _ = WindowsAPI.UnhookWindowsHookEx(hookId);
		//}
		protected override void RegisterHook()
		{
		}

		/// <summary>
		/// Convert the specified screen coordinates to mouse event coordinates (MOUSEEVENTF_ABSOLUTE).
		/// MSDN: "In a multimonitor system, [MOUSEEVENTF_ABSOLUTE] coordinates map to the primary monitor."
		/// The above implies that values greater than 65535 or less than 0 are appropriate, but only on
		/// multi-monitor systems.  For simplicity, performance, and backward compatibility, no check for
		/// multi-monitor is done here. Instead, the system's default handling for out-of-bounds coordinates
		/// is used; for example, mouse_event() stops the cursor at the edge of the screen.
		/// UPDATE: In v1.0.43, the following formula was fixed (actually broken, see below) to always yield an
		/// in-range value. The previous formula had a +1 at the end:
		/// aX|Y = ((65535 * aX|Y) / (screen_width|height - 1)) + 1;
		/// The extra +1 would produce 65536 (an out-of-range value for a single-monitor system) if the maximum
		/// X or Y coordinate was input (e.g. x-position 1023 on a 1024x768 screen).  Although this correction
		/// seems inconsequential on single-monitor systems, it may fix certain misbehaviors that have been reported
		/// on multi-monitor systems. Update: according to someone I asked to test it, it didn't fix anything on
		/// multimonitor systems, at least those whose monitors vary in size to each other.  In such cases, he said
		/// that only SendPlay or DllCall("SetCursorPos") make mouse movement work properly.
		/// FIX for v1.0.44: Although there's no explanation yet, the v1.0.43 formula is wrong and the one prior
		/// to it was correct; i.e. unless +1 is present below, a mouse movement to coords near the upper-left corner of
		/// the screen is typically off by one pixel (only the y-coordinate is affected in 1024x768 resolution, but
		/// in other resolutions both might be affected).
		/// v1.0.44.07: The following old formula has been replaced:
		/// (((65535 * coord) / (width_or_height - 1)) + 1)
		/// ... with the new one below.  This is based on numEric's research, which indicates that mouse_event()
		/// uses the following inverse formula internally:
		/// x_or_y_coord = (x_or_y_abs_coord * screen_width_or_height) / 65536
		/// </summary>
		/// <param name="coord"></param>
		/// <param name="width_or_height"></param>
		/// <returns></returns>
		internal override int MouseCoordToAbs(int coord, int width_or_height) => ((65536 * coord) / width_or_height) + (coord < 0 ? -1 : 1);

		/*  JOURNAL_RECORD_MODE
		        internal nint PlaybackProc(int nCode, nint wParam, ref KBDLLHOOKSTRUCT lParam)
		        {
		            switch (aCode)
		            {
		                case HC_ACTION:
		                {
		                    EVENTMSG& event = *(PEVENTMSG) lParam;
		                    PlaybackEvent& dest_event = sEventPB[sEventCount];
		                    dest_event.message = event.message;

		                    if (event.message >= WM_MOUSEFIRST && event.message <= WM_MOUSELAST) // Mouse event, including wheel.
		                    {
		                        if (event.message != WM_MOUSEMOVE)
		                        {
		                            // WHEEL: No info comes in about which direction the wheel was turned (nor by how many notches).
		                            // In addition, it appears impossible to specify such info when playing back the event.
		                            // Therefore, playback usually produces downward wheel movement (but upward in some apps like
		                            // Visual Studio).
		                            dest_event.x = event.paramL;
		                            dest_event.y = event.paramH;
		                            ++sEventCount;
		                        }
		                    }
		                    else // Keyboard event.
		                    {
		                        dest_event.vk = event.paramL & 0x00FF;
		                        dest_event.sc = (event.paramL & 0xFF00) >> 8;

		                        if (event.paramH & 0x8000) // Extended key.
		                            dest_event.sc |= 0x100;

		                        if (dest_event.vk == VK_CANCEL) // Ctrl+Break.
		                        {
		                            UnhookWindowsHookEx(g_PlaybackHook);
		                            g_PlaybackHook = NULL; // Signal the installer of the hook that it's gone now.
		                            // Obsolete method, pre-v1.0.44:
		                            //PostMessage(g_hWnd, WM_CANCELJOURNAL, 0, 0); // v1.0.44: Post it to g_hWnd vs. NULL so that it isn't lost when script is displaying a MsgBox or other dialog.
		                        }

		                        ++sEventCount;
		                    }

		                    break;
		                }

		                    //case HC_SYSMODALON:  // A system-modal dialog box is being displayed. Until the dialog box is destroyed, the hook procedure must stop playing back messages.
		                    //case HC_SYSMODALOFF: // A system-modal dialog box has been destroyed. The hook procedure must resume playing back the messages.
		                    //  break;
		            }

		            // Unlike the playback hook, it seems more correct to call CallNextHookEx() unconditionally so that
		            // any other journal record hooks can also record the event.  But MSDN is quite vague about this.
		            return CallNextHookEx(g_PlaybackHook, aCode, wParam, lParam);
		            // Return value is ignored, except possibly when aCode < 0 (MSDN is unclear).
		        }
		*/
		/*
		    private string MapKey(uint vk, uint sc)
		    {
		    _ = buf.Clear();
		    _ = WindowsAPI.GetKeyboardState(state);

		    foreach (var key in ctrls)
		    {
		        const byte d = 0x80;
		        const byte u = d - 1;
		        //var s = WindowsAPI.GetKeyState(key) >> 8 != 0;
		        var s = WindowsAPI.GetAsyncKeyState(key) >> 8 != 0;
		        state[key] &= s ? d : u;

		        if (s)
		        {
		            if ((Keys)key == Keys.LShiftKey || (Keys)key == Keys.RShiftKey)//For some reason, neither GetKeyboardState() or GetKeyState() properly sets these.
		                state[(int)Keys.ShiftKey] = 0x80;
		            else if ((Keys)key == Keys.LControlKey || (Keys)key == Keys.RControlKey)
		                state[(int)Keys.ControlKey] = 0x80;
		        }
		    }

		    _ = WindowsAPI.ToUnicodeEx(vk, sc, state, buf, buf.Capacity, 0, kbd);
		    return buf.ToString();
		    }
		*/
		/*
		    private void ScanDeadKeys()
		    {
		    //var kbd = WindowsAPI.GetKeyboardLayout(0);
		    _ = buf.Clear();
		    deadKeys = new List<uint>();

		    for (var i = 0u; i < VKMAX; i++)
		    {
		        var result = WindowsAPI.ToUnicodeEx(i, 0, state, buf, buf.Capacity, 0, kbd);

		        if (result == -1)
		            deadKeys.Add(i);
		    }
		    }
		*/

		internal class CachedLayoutType
		{
			internal ResultType has_altgr = ResultType.Fail;
			internal nint hkl = 0;
		};

		private delegate KBDTABLES64 KbdTables();

		/*  can also try this from https://stackoverflow.com/questions/318777/c-sharp-how-to-translate-virtual-keycode-to-char/38787314#38787314

		    public string KeyCodeToUnicode(Keys key)
		    {
		    byte[] keyboardState = new byte[255];
		    bool keyboardStateStatus = GetKeyboardState(keyboardState);

		    if (!keyboardStateStatus)
		    {
		        return DefaultObject;
		    }

		    uint virtualKeyCode = (uint)key;
		    uint scanCode = MapVirtualKey(virtualKeyCode, 0);
		    nint inputLocaleIdentifier = GetKeyboardLayout(0);

		    StringBuilder result = new StringBuilder();
		    ToUnicodeEx(virtualKeyCode, scanCode, keyboardState, result, (int)5, (uint)0, inputLocaleIdentifier);

		    return result.ToString();
		    }

		    [DllImport("user32.dll")]
		    static extern bool GetKeyboardState(byte[] lpKeyState);

		    [DllImport("user32.dll")]
		    static extern uint MapVirtualKey(uint uCode, uint uMapType);

		    [DllImport("user32.dll")]
		    static extern nint GetKeyboardLayout(uint idThread);

		    [DllImport("user32.dll")]
		    static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags, nint dwhkl);

		*/
	}
}

#endif
