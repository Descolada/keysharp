using Keysharp.Builtins;
using Keysharp.Internals.Scripting;

namespace Keysharp.Internals.Window
{
	/// <summary>
	/// Engine-side state for a single <c>Ks.WinEvent</c> subscription: the event type, the parsed window-matching
	/// criteria, the script callback, a remaining-fire counter, and a persistence registration. The script-facing
	/// <c>Ks.WinEvent</c> object wraps one of these, mirroring how <c>InputObject</c> wraps <c>InputType</c>.
	/// </summary>
	internal sealed class WinEventRegistration
	{
		internal readonly WindowEventType type;
		internal readonly SearchCriteria criteria;            // null => match any window
		internal readonly WindowSearchOptions inheritedOptions;
		internal readonly bool detectHidden;                  // effective DetectHiddenWindows for this subscription
		internal readonly IFuncObj callback;
		internal readonly ScriptEventScheduler ownerScheduler;
		internal readonly CallbackRegistration registration;
		internal object scriptObject;                         // the Ks.WinEvent wrapper (callback arg 1)
		internal volatile bool paused;                        // a paused hook stays registered but doesn't fire

		// A Close event arrives after the window is gone, so it can't be matched (or even confirmed top-level)
		// at that point. Mirroring Descolada's WinEvent.MatchingWinList, we track the set of top-level windows
		// that currently satisfy this subscription while they are alive (seeded at registration, kept current by
		// top-level Create/Show/TitleChange events); Close then fires only for a window in this set. Allocated
		// for every Close subscription (both criteria-filtered and match-any).
		internal readonly HashSet<nint> matchingWindows;
		internal readonly Lock matchGate;

		private long remaining;                               // -1 => unlimited
		internal volatile bool active;

		/// <summary>True for a Close subscription (which tracks its matching top-level windows).</summary>
		internal bool IsClose => matchingWindows != null;

		internal WinEventRegistration(WindowEventType type, SearchCriteria criteria, IFuncObj callback, long count, ScriptEventScheduler ownerScheduler)
		{
			this.type = type;
			this.criteria = criteria;
			this.callback = callback;
			this.ownerScheduler = ownerScheduler;
			remaining = count;
			active = true;
			registration = new CallbackRegistration(callback, ownerScheduler, true);

			if (type == WindowEventType.Close)
			{
				matchingWindows = new HashSet<nint>();
				matchGate = new Lock();
			}

			// Snapshot the window-search context from the registering thread, mirroring Descolada's WinEvent
			// (which captures A_DetectHiddenWindows/Text and the title-match mode at registration). Create and
			// Show additionally force hidden detection on, because a freshly created/shown window is often still
			// hidden for a short time. All other event types respect the thread's DetectHiddenWindows setting.
			var forceHidden = type is WindowEventType.Create or WindowEventType.Show;
			detectHidden = forceHidden || ThreadAccessors.A_DetectHiddenWindows;
			inheritedOptions = new WindowSearchOptions
			{
				DetectHiddenWindows = detectHidden,
				DetectHiddenText = forceHidden || ThreadAccessors.A_DetectHiddenText,
				TitleMatchMode = ThreadAccessors.A_TitleMatchMode,
				TitleMatchModeSpeed = ThreadAccessors.A_TitleMatchModeSpeed
			};
		}

		internal long Remaining => Interlocked.Read(ref remaining);

		/// <summary>
		/// Atomically decides whether this subscription may fire once more, consuming one unit of the
		/// remaining-fire budget. Returns false once the budget is exhausted or the subscription is stopped.
		/// </summary>
		internal bool TryConsumeFire()
		{
			if (!active)
				return false;

			while (true)
			{
				var cur = Interlocked.Read(ref remaining);

				if (cur == 0)
					return false;

				if (cur < 0)
					return true;                              // unlimited

				if (Interlocked.CompareExchange(ref remaining, cur - 1, cur) == cur)
					return true;
			}
		}

		/// <summary>True once the fire budget has just been exhausted (so the manager can auto-stop).</summary>
		internal bool IsExhausted => Interlocked.Read(ref remaining) == 0;
	}

	/// <summary>
	/// The per-<see cref="Script"/> engine behind <c>Ks.WinEvent</c>. It owns the platform
	/// <see cref="IWindowEventBackend"/> directly, installs/uninstalls native hooks for exactly the categories its
	/// subscriptions need, and for each incoming <see cref="WindowEventRaw"/> performs window-criteria matching,
	/// the remaining-fire count, and marshalling of the script callback onto its owning scheduler (the same path
	/// <c>OnMessage</c> uses, so script code always runs on its own pseudo-thread). Move events are delivered
	/// as-is (no coalescing).
	/// </summary>
	internal sealed class WinEventManager : IDisposable
	{
		private readonly Script script;
		private readonly Lock gate = new();
		private readonly Dictionary<WindowEventType, List<WinEventRegistration>> buckets = new();
		private IWindowEventBackend backend;
		private bool backendInitFailed;
		private WindowEventMask installedMask = WindowEventMask.None;
		private volatile bool globalPaused;
		private bool disposed;

		internal WinEventManager(Script script) => this.script = script;

		// ---- global pause ------------------------------------------------------------------

		/// <summary>True while all hooks are globally paused.</summary>
		internal bool GlobalPaused => globalPaused;

		/// <summary>Pauses (1), unpauses (0) or toggles (-1) all hooks; returns the resulting state.</summary>
		internal bool SetGlobalPause(long newState)
		{
			globalPaused = newState == -1 ? !globalPaused : newState != 0;
			return globalPaused;
		}

		// ---- registration ------------------------------------------------------------------

		internal void Register(WinEventRegistration reg)
		{
			lock (gate)
			{
				if (disposed)
					return;

				if (!buckets.TryGetValue(reg.type, out var list))
					buckets[reg.type] = list = new List<WinEventRegistration>();

				list.Add(reg);

				if (reg.IsClose)
					SeedCloseMatches(reg);

				SyncBackendMask();
			}
		}

		internal void Unregister(WinEventRegistration reg)
		{
			lock (gate)
			{
				RemoveLocked(reg);
				SyncBackendMask();
			}
		}

		private void RemoveLocked(WinEventRegistration reg)
		{
			reg.active = false;
			reg.registration.Clear();

			if (buckets.TryGetValue(reg.type, out var list))
			{
				_ = list.Remove(reg);

				if (list.Count == 0)
					_ = buckets.Remove(reg.type);
			}
		}

		/// <summary>Removes every subscription owned by <paramref name="scheduler"/> (deterministic teardown
		/// when a worker thread/scheduler is disposed — does not rely on GC/__Delete).</summary>
		internal bool RemoveOwned(ScriptEventScheduler scheduler)
		{
			if (scheduler == null)
				return false;

			var removedAny = false;

			lock (gate)
			{
				foreach (var reg in buckets.Values.SelectMany(l => l).Where(r => ReferenceEquals(r.ownerScheduler, scheduler)).ToArray())
				{
					RemoveLocked(reg);
					removedAny = true;
				}

				if (removedAny)
					SyncBackendMask();
			}

			return removedAny;
		}

		// ---- native hook management ---------------------------------------------------------

		/// <summary>Recomputes which event categories are needed and installs/uninstalls native hooks on the
		/// backend to match. Must be called under <see cref="gate"/>.</summary>
		private void SyncBackendMask()
		{
			var desired = WindowEventMask.None;

			foreach (var type in buckets.Keys)
				desired |= type.ToMask();

			// A Close subscription must observe window appearance so it can keep its matching-window set current
			// (a window must be tracked while alive to fire Close once it is gone). Criteria-filtered Close also
			// needs TitleChange, since a title change can make a window start/stop matching the criteria.
			if (buckets.TryGetValue(WindowEventType.Close, out var closeList) && closeList.Count > 0)
			{
				desired |= WindowEventMask.Create | WindowEventMask.Show;

				if (closeList.Any(r => r.criteria != null))
					desired |= WindowEventMask.TitleChange;
			}

			// Active also fires on the active window's title change, so observe TitleChange when any Active sub exists.
			if (buckets.TryGetValue(WindowEventType.Active, out var activeList) && activeList.Count > 0)
				desired |= WindowEventMask.TitleChange;

			if (desired == installedMask)
				return;

			if (desired != WindowEventMask.None)
			{
				var b = EnsureBackend();

				if (b == null)
					return;                                   // unsupported environment; nothing to install

				var toRemove = installedMask & ~desired;
				var toAdd = desired & ~installedMask;

				if (toRemove != WindowEventMask.None)
					b.Stop(toRemove);

				if (toAdd != WindowEventMask.None)
					b.Start(toAdd);
			}
			else
			{
				backend?.Stop(installedMask);
			}

			installedMask = desired;
		}

		private IWindowEventBackend EnsureBackend()
		{
			if (backend != null || backendInitFailed)
				return backend;

			try
			{
				backend = WindowEventBackendProvider.Create();

				if (backend != null)
					backend.Sink = OnNativeEvent;
				else
					backendInitFailed = true;
			}
			catch (Exception ex)
			{
				backendInitFailed = true;
				Ks.OutputDebugLine($"Window event backend creation failed: {ex.Message}");
			}

			return backend;
		}

		// ---- native event intake (arbitrary thread, from the hub) ---------------------------

		private void OnNativeEvent(WindowEventRaw raw)
		{
			if (disposed)
				return;

			// Keep Close subscriptions' matching-window sets current as windows appear or change identity. This
			// runs even when there are no subscribers for raw.Type itself (e.g. a Create event observed only to
			// maintain a Close subscription).
			if (raw.Type is WindowEventType.Create or WindowEventType.Show or WindowEventType.TitleChange)
				UpdateCloseMatches(raw.Hwnd);

			// Like the reference, Active also re-fires when the active window's title changes (so criteria that
			// only become true after the title is set are still caught).
			if (raw.Type == WindowEventType.TitleChange)
				DispatchActiveOnTitleChange(raw.Hwnd, raw.TimeMs);

			WinEventRegistration[] snapshot;

			lock (gate)
			{
				if (!buckets.TryGetValue(raw.Type, out var list) || list.Count == 0)
					return;

				snapshot = list.ToArray();
			}

			if (raw.Type == WindowEventType.Close)
			{
				foreach (var reg in snapshot)
					if (reg.active)
						HandleClose(reg, raw.Hwnd, raw.TimeMs);

				return;
			}

			foreach (var reg in snapshot)
			{
				if (reg.active && Matches(reg, raw.Hwnd))
					FireOnce(reg, raw.Hwnd, raw.TimeMs);
			}
		}

		/// <summary>Handles a destroy/hide/cloak event for one Close subscription: fires only for a window we were
		/// tracking that no longer exists from this hook's DetectHiddenWindows perspective (so a hidden window
		/// fires Close when DHW is off, but not when DHW is on — matching WinExist semantics), and updates tracking.</summary>
		private void HandleClose(WinEventRegistration reg, nint hwnd, long timeMs)
		{
			bool tracked;

			lock (reg.matchGate)
				tracked = reg.matchingWindows.Contains(hwnd);

			if (!tracked)
				return;     // never matched/tracked (e.g. a child control hide) — nothing to do

			if (WindowExists(hwnd, reg.detectHidden))
				return;     // still exists (e.g. merely hidden with DetectHiddenWindows on) — keep tracking, don't fire

			FireOnce(reg, hwnd, timeMs);

			lock (reg.matchGate)
				_ = reg.matchingWindows.Remove(hwnd);
		}

		/// <summary>Whether the window still exists from a hook's DetectHiddenWindows perspective: a valid handle is
		/// enough when DHW is on; when off it must also be visible.</summary>
		private static bool WindowExists(nint hwnd, bool detectHidden)
		{
			if (!WindowManager.IsWindow(hwnd))
				return false;

			if (detectHidden)
				return true;

			var win = WindowManager.CreateWindow(hwnd);
			return win != null && win.IsSpecified && win.Visible;
		}

		/// <summary>Fires Active subscriptions for the active window when its title changes.</summary>
		private void DispatchActiveOnTitleChange(nint hwnd, long timeMs)
		{
			if (hwnd == 0 || hwnd != WindowManager.GetForegroundWindowHandle())
				return;

			WinEventRegistration[] activeSubs;

			lock (gate)
			{
				if (!buckets.TryGetValue(WindowEventType.Active, out var list) || list.Count == 0)
					return;

				activeSubs = list.ToArray();
			}

			foreach (var reg in activeSubs)
				if (reg.active && Matches(reg, hwnd))
					FireOnce(reg, hwnd, timeMs);
		}

		private static bool Matches(WinEventRegistration reg, nint hwnd)
		{
			if (hwnd == 0)
				return false;

			if (reg.criteria == null)
			{
				// Match-any: respect the registration-time DetectHiddenWindows setting so the callback isn't
				// flooded with transient/hidden windows when DHW is off.
				if (reg.detectHidden)
					return true;

				var w = WindowManager.CreateWindow(hwnd);
				return w != null && w.IsSpecified && w.Visible;
			}

			var win = WindowManager.CreateWindow(hwnd);
			return win != null && win.Equals(reg.criteria, reg.inheritedOptions);
		}

		/// <summary>Re-evaluates a now-top-level window against every Close subscription and adds/removes it from
		/// that subscription's matching-window set. Called when a window appears (Create/Show) or, for criteria
		/// subscriptions, when its title changes. <paramref name="hwnd"/> is already known to be top-level because
		/// the backend only emits these events for top-level windows.</summary>
		private void UpdateCloseMatches(nint hwnd)
		{
			if (hwnd == 0)
				return;

			WinEventRegistration[] closeSubs;

			lock (gate)
			{
				if (!buckets.TryGetValue(WindowEventType.Close, out var list) || list.Count == 0)
					return;

				closeSubs = list.ToArray();
			}

			var win = WindowManager.CreateWindow(hwnd);

			if (win == null || !win.IsSpecified)
				return;

			foreach (var reg in closeSubs)
			{
				var matches = CloseTracks(reg, win);

				lock (reg.matchGate)
				{
					if (matches)
						_ = reg.matchingWindows.Add(hwnd);
					else
						_ = reg.matchingWindows.Remove(hwnd);
				}
			}
		}

		/// <summary>Seeds a Close subscription with the windows that already satisfy it, so windows that existed
		/// before the subscription was created still fire Close. Called under <see cref="gate"/>.</summary>
		private static void SeedCloseMatches(WinEventRegistration reg)
		{
			try
			{
				// EnumerateWindows already yields top-level windows respecting the captured DetectHiddenWindows.
				foreach (var win in WindowManager.EnumerateWindows(reg.detectHidden))
				{
					if (win.IsSpecified && CloseTracks(reg, win))
						lock (reg.matchGate)
							_ = reg.matchingWindows.Add(win.Handle);
				}
			}
			catch (Exception ex)
			{
				Ks.OutputDebugLine($"WinEvent Close seed failed: {ex.Message}");
			}
		}

		/// <summary>Whether <paramref name="win"/> (a top-level window) should be tracked by Close subscription
		/// <paramref name="reg"/>: criteria subscriptions match the criteria; match-any subscriptions track any
		/// top-level window, respecting the captured DetectHiddenWindows setting.</summary>
		private static bool CloseTracks(WinEventRegistration reg, WindowItemBase win)
			=> reg.criteria != null
				? win.Equals(reg.criteria, reg.inheritedOptions)
				: reg.detectHidden || win.Visible;

		// ---- dispatch -----------------------------------------------------------------------

		private void FireOnce(WinEventRegistration reg, nint hwnd, long timeMs)
		{
			// A paused hook (or globally paused manager) stays registered and keeps its matching-window set
			// current, but doesn't fire or consume its remaining-count budget.
			if (globalPaused || reg.paused)
				return;

			if (!reg.TryConsumeFire())
				return;

			var scheduler = reg.ownerScheduler ?? script.EventScheduler;

			if (scheduler == null || scheduler.IsDisposed)
				return;

			object[] args = [reg.scriptObject, hwnd.ToInt64(), timeMs];
			_ = scheduler.Enqueue(ScriptEventQueue.Normal, 0, () => RunOnSchedulerThread(scheduler, reg, hwnd, timeMs, args));

			if (reg.IsExhausted)
				Unregister(reg);
		}

		private ScriptEventExecutionResult RunOnSchedulerThread(ScriptEventScheduler scheduler, WinEventRegistration reg, nint hwnd, long timeMs, object[] args)
		{
			// No reg.active re-check here: TryConsumeFire (at enqueue time) is the authoritative gate. Re-checking
			// would drop callbacks that were already consumed/queued when the subscription auto-stops on its last
			// allowed fire (Count reaching 0 deactivates the registration before the queued callback runs).
			using var thread = scheduler.StartPseudoThreadScope(0, false, false, false);

			if (!thread.Started)
				return thread.Result;

			try
			{
				var tv = thread.ThreadVariables;
				tv.eventInfo = timeMs;
				tv.hwndLastUsed = hwnd.ToInt64();
				_ = reg.callback.Call(args);
			}
			catch (Exception ex)
			{
				_ = Keysharp.Internals.Flow.HandleCaughtException(ex);
			}
			finally
			{
				script.ExitIfNotPersistent();
			}

			return ScriptEventExecutionResult.Executed;
		}

		// ---- teardown -----------------------------------------------------------------------

		public void Dispose()
		{
			List<WinEventRegistration> all;

			IWindowEventBackend toDispose;

			lock (gate)
			{
				if (disposed)
					return;

				disposed = true;
				all = buckets.Values.SelectMany(l => l).ToList();
				buckets.Clear();
				toDispose = backend;
				backend = null;
				installedMask = WindowEventMask.None;
			}

			foreach (var reg in all)
			{
				reg.active = false;
				reg.registration.Clear();
			}

			try
			{
				toDispose?.Dispose();
			}
			catch (Exception ex)
			{
				Ks.OutputDebugLine($"Window event backend dispose failed: {ex.Message}");
			}
		}
	}
}
