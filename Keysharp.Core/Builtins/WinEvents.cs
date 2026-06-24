using Keysharp.Internals.Window;

namespace Keysharp.Builtins
{
	public partial class Ks
	{
		/// <summary>
		/// Cross-platform window-event subscriptions, modeled on Descolada's AHK <c>WinEvent</c> library.
		/// Each factory (<see cref="staticActive"/>, <see cref="staticExist"/>, <see cref="staticMove"/>, …)
		/// registers a callback that fires when a matching window event occurs and returns a subscription object
		/// whose <see cref="Stop"/> method cancels it. Every callback has the same shape:
		/// <c>(hook, hwnd, dwmsEventTime)</c>. Event-specific extras are exposed via <c>A_EventInfo</c> — for
		/// <c>Move</c> that's an object with <c>{ x, y, w, h }</c> (the window's position and size, matching
		/// <c>WinGetPos</c>), resolved lazily on first read.
		/// <para>
		/// Appearance/disappearance are reported by the DetectHiddenWindows-aware <see cref="staticExist"/> /
		/// <see cref="staticNotExist"/> pair: there is no separate Create/Close event because they were just Exist /
		/// NotExist with DetectHiddenWindows on (Exist also catches a window that starts matching via a title change,
		/// and NotExist a window that is hidden when DetectHiddenWindows is off).</para>
		/// <para>
		/// Use as: <c>#import "Ks" { WinEvent }</c> then <c>hook := WinEvent.Active(MyCallback, "ahk_exe notepad.exe")</c>.
		/// The argument order mirrors the reference library: <c>(Callback, WinTitle, Count, WinText, ExcludeTitle,
		/// ExcludeText)</c> — <c>Count</c> (default -1 = unlimited) comes right after <c>WinTitle</c>, with the
		/// rarely-used text/exclude criteria last. The subscription auto-stops on <c>__Delete</c>, but because GC
		/// timing is unpredictable, also call <c>hook.Stop()</c> (or let the owning thread tear down) when done.
		/// </para>
		/// </summary>
		public sealed class WinEvent : KeysharpObject
		{
			internal WinEventRegistration reg;

			internal WinEvent() : base() { }

			// ---- event factories (Callback, WinTitle, Count, WinText, ExcludeTitle, ExcludeText) -------------

			/// <summary>Fires when a window becomes the active/foreground window. Script: <c>WinEvent.Active(cb, …)</c>.</summary>
			public static object staticActive(object @this, object callback, object winTitle = null, object count = null, object winText = null, object excludeTitle = null, object excludeText = null)
				=> Subscribe(WindowEventType.Active, callback, winTitle, winText, excludeTitle, excludeText, count);

			/// <summary>Fires when a window matching the criteria appears — created, shown, or its title changed so it
			/// now matches. Fires once per matching window. Respects DetectHiddenWindows. Subsumes the old "Create"
			/// event (which was just Exist with DetectHiddenWindows on). Script: <c>WinEvent.Exist(cb, …)</c>.</summary>
			public static object staticExist(object @this, object callback, object winTitle = null, object count = null, object winText = null, object excludeTitle = null, object excludeText = null)
				=> Subscribe(WindowEventType.Exist, callback, winTitle, winText, excludeTitle, excludeText, count);

			/// <summary>Fires when a window matching the criteria disappears — destroyed, hidden/cloaked, or its title
			/// changed so it no longer matches. DetectHiddenWindows-aware and subsumes the old "Close" event: with
			/// DetectHiddenWindows off a hidden window counts as gone (fires), with it on only destruction does.
			/// Script: <c>WinEvent.NotExist(cb, …)</c>.</summary>
			public static object staticNotExist(object @this, object callback, object winTitle = null, object count = null, object winText = null, object excludeTitle = null, object excludeText = null)
				=> Subscribe(WindowEventType.NotExist, callback, winTitle, winText, excludeTitle, excludeText, count);

			/// <summary>Fires when a window moves or resizes. Every move event is delivered as-is (not coalesced).
			/// <c>A_EventInfo</c> holds the window's new position and size as an object <c>{ x, y, w, h }</c> (matching
			/// <c>WinGetPos</c>), built on first access.</summary>
			public static object staticMove(object @this, object callback, object winTitle = null, object count = null, object winText = null, object excludeTitle = null, object excludeText = null)
				=> Subscribe(WindowEventType.Move, callback, winTitle, winText, excludeTitle, excludeText, count);

			/// <summary>Fires when a window becomes visible (mapped/shown).</summary>
			public static object staticShow(object @this, object callback, object winTitle = null, object count = null, object winText = null, object excludeTitle = null, object excludeText = null)
				=> Subscribe(WindowEventType.Show, callback, winTitle, winText, excludeTitle, excludeText, count);

			/// <summary>Fires when a window is minimized.</summary>
			public static object staticMinimize(object @this, object callback, object winTitle = null, object count = null, object winText = null, object excludeTitle = null, object excludeText = null)
				=> Subscribe(WindowEventType.Minimize, callback, winTitle, winText, excludeTitle, excludeText, count);

			/// <summary>Fires when a window is restored from the minimized state.</summary>
			public static object staticRestore(object @this, object callback, object winTitle = null, object count = null, object winText = null, object excludeTitle = null, object excludeText = null)
				=> Subscribe(WindowEventType.Restore, callback, winTitle, winText, excludeTitle, excludeText, count);

			/// <summary>Fires when a window's title changes.</summary>
			public static object staticTitleChange(object @this, object callback, object winTitle = null, object count = null, object winText = null, object excludeTitle = null, object excludeText = null)
				=> Subscribe(WindowEventType.TitleChange, callback, winTitle, winText, excludeTitle, excludeText, count);

			// ---- global pause -------------------------------------------------------------------------------

			/// <summary>Pauses (1), unpauses (0) or toggles (-1) all event hooks. Returns the resulting paused state.</summary>
			public static object staticPause(object @this, object newState = null)
				=> Script.TheScript.WinEventManager.SetGlobalPause(newState.Al(1L));

			/// <summary>Gets whether all event hooks are paused (script: <c>WinEvent.IsPaused</c>).</summary>
			public static object staticget_IsPaused(object @this) => Script.TheScript.WinEventManager.GlobalPaused;

			/// <summary>Sets whether all event hooks are paused (script: <c>WinEvent.IsPaused := …</c>).</summary>
			public static object staticset_IsPaused(object @this, object value)
				=> Script.TheScript.WinEventManager.SetGlobalPause(value.Ab() ? 1L : 0L);

			// ---- instance surface ----------------------------------------------------------------------------

			/// <summary>The event type this subscription listens for (e.g. "Active", "Move").</summary>
			public string EventType => reg?.type.ToString() ?? "";

			/// <summary>True while the subscription is still receiving events.</summary>
			public bool IsActive => reg?.active ?? false;

			/// <summary>Remaining number of times the callback will fire (-1 = unlimited).</summary>
			public long Count => reg?.Remaining ?? 0L;

			/// <summary>Gets or sets whether this hook is paused (paused hooks stay registered but don't fire).</summary>
			public bool IsPaused
			{
				get => reg?.paused ?? false;
				set { if (reg != null) reg.paused = value; }
			}

			/// <summary>Pauses (1), unpauses (0) or toggles (-1) this hook. Returns the resulting paused state.</summary>
			public object Pause(object newState = null)
			{
				var r = reg;

				if (r == null)
					return false;

				var ns = newState.Al(1L);
				r.paused = ns == -1 ? !r.paused : ns != 0;
				return r.paused;
			}

			/// <summary>Cancels the subscription so the callback no longer fires.</summary>
			public object Stop()
			{
				var r = reg;

				if (r != null && r.active)
					Script.TheScript.WinEventManager.Unregister(r);

				return DefaultObject;
			}

			public override object __Delete()
			{
				_ = Stop();
				return base.__Delete();
			}

			// ---- helpers -------------------------------------------------------------------------------------

			private static object Subscribe(WindowEventType type, object callback, object winTitle, object winText, object excludeTitle, object excludeText, object count)
			{
				var fo = Functions.GetFuncObj(callback, null, null, true);

				if (fo == null)
					return Errors.TypeErrorOccurred(callback, typeof(FuncObj));

				var criteria = BuildCriteria(winTitle, winText, excludeTitle, excludeText);
				var remaining = count.Al(-1L);
				var scheduler = Script.TheScript.EventScheduler;
				var reg = new WinEventRegistration(type, criteria, fo, remaining, scheduler);
				var we = new WinEvent { reg = reg };
				reg.scriptObject = we;
				Script.TheScript.WinEventManager.Register(reg);
				return we;
			}

			/// <summary>Returns null (match-any) when no window filter is supplied, otherwise parses the standard AHK WinTitle criteria.</summary>
			private static SearchCriteria BuildCriteria(object winTitle, object winText, object excludeTitle, object excludeText)
			{
				if (string.IsNullOrEmpty(winTitle.As()) && string.IsNullOrEmpty(winText.As())
					&& string.IsNullOrEmpty(excludeTitle.As()) && string.IsNullOrEmpty(excludeText.As()))
					return null;

				return SearchCriteria.FromString(winTitle, winText, excludeTitle, excludeText);
			}
		}
	}
}
