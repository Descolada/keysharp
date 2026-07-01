using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Positions Keysharp's OWN top-level windows on Wayland. A GTK/Eto client cannot set its own
	/// xdg-toplevel position (Eto's <c>window.Location</c> is a silent no-op on Wayland), so — just
	/// like <c>WinMove</c> does for foreign windows — we drive the active compositor backend (KWin
	/// scripting, the GNOME Shell extension, or Cinnamon eval) to move the window once it has been
	/// mapped.
	///
	/// <para>The tricky part is correlating our just-shown Eto window with the compositor's window
	/// id. We first stamp the window with a unique temporary Wayland app_id and match that exact
	/// value in the compositor's list. If a backend can't observe app_id, we fall back to a strict
	/// unique metadata match (title/size/PID/active) while tracking claimed compositor ids so two
	/// Keysharp windows never resolve to the same one. The resolved id is cached per form, so only
	/// the first Show pays the correlation/polling cost; later Move calls reuse it.</para>
	///
	/// <para>Each move is a compositor round-trip, so moves run on a background thread and are
	/// coalesced per form (latest position wins). A rapid stream of Moves — e.g. a Highlight
	/// tracking a moving target — collapses to the most recent position instead of queuing.</para>
	///
	/// <para>This is best-effort: a brief map-then-move is unavoidable (Wayland maps the window
	/// where the compositor chooses, then we move it), and on compositors that cannot move windows
	/// (foreign-toplevel-only: sway/Hyprland/COSMIC) it degrades to a no-op.</para>
	/// </summary>
	internal static class WaylandSelfPositioner
	{
		private const string NormalAppId = "keysharp";
		private const string CorrelationAppIdPrefix = "keysharp.self.";

		private sealed class FormState
		{
			internal nint FormHandle;
			internal Eto.Forms.Form Form;
			internal nint CompositorHandle;          // resolved compositor window handle; 0 until correlated
			internal string CompositorId = "";       // its id, kept for claim bookkeeping
			internal string Title = "";
			internal int TargetX = WindowInfoBase.Unchanged;
			internal int TargetY = WindowInfoBase.Unchanged;
			internal int MatchW;
			internal int MatchH;
			internal bool Busy;                       // a worker is currently servicing this form
			internal bool RemoveBorder;               // strip the server-side titlebar once correlated
			internal bool BorderRemoved;              // ...and we have done so
			internal bool KeepAbove;                  // assert keep-above (Eto +AlwaysOnTop is a no-op on Wayland)
			internal bool KeptAbove;                  // ...and we have done so
			internal FormWindowState? PendingWindowState; // maximize/minimize/restore to reassert via the backend
		}

		private static readonly object sync = new();
		private static readonly Dictionary<nint, FormState> states = new();
		private static readonly HashSet<string> claimedIds = new();

		// A freshly-shown window may not be in the compositor's list instantly; poll briefly.
		private const int CorrelateTimeoutMs = 1000;
		private const int CorrelatePollMs = 20;
		// Allow the frame (which includes server-side decorations) to be larger than the requested
		// client size when matching by size.
		private const int SizeTolerance = 120;

		internal static bool IsSupported => Platform.Desktop.IsWaylandSession && WaylandBackend.Current != null;

		/// <summary>
		/// Request that our own window (identified by <paramref name="form"/>) be
		/// moved so its top-left sits at screen (<paramref name="x"/>, <paramref name="y"/>). Either
		/// coordinate may be <see cref="WindowInfoBase.Unchanged"/> to leave it untouched.
		/// <paramref name="title"/> and the match size are used only to correlate the window the
		/// first time. Returns immediately; the move runs asynchronously. No-op off Wayland or when
		/// there is no capable backend.
		/// </summary>
		internal static void Position(Eto.Forms.Form form, string title, int x, int y, int matchW, int matchH, bool removeBorder = false, bool keepAbove = false)
		{
			var formHandle = form?.Handle ?? 0;

			if (formHandle == 0 || !IsSupported)
				return;

			// Nothing to do if there's no position to apply, no border to strip and no keep-above to assert.
			if (x == WindowInfoBase.Unchanged && y == WindowInfoBase.Unchanged && !removeBorder && !keepAbove)
				return;

			var startWorker = false;
			FormState state;

			lock (sync)
			{
				state = GetOrCreateState(formHandle, form);

				if (x != WindowInfoBase.Unchanged) state.TargetX = x;
				if (y != WindowInfoBase.Unchanged) state.TargetY = y;
				state.Title = title ?? "";
				if (matchW > 0) state.MatchW = matchW;
				if (matchH > 0) state.MatchH = matchH;
				if (removeBorder) state.RemoveBorder = true;
				if (keepAbove) state.KeepAbove = true;

				if (!state.Busy)
					startWorker = state.Busy = true;
			}

			if (startWorker)
				_ = Task.Run(() => Worker(state));
		}

		/// <summary>
		/// Resolve one of our OWN top-level windows to the active compositor's window handle, correlating once
		/// and caching the result. Lets the synchronous
		/// window verbs (WinMove / WinGetPos against a Gui object) take the same compositor path foreign windows
		/// use, instead of Eto's self-position/-query which is a no-op on Wayland. Returns false off Wayland,
		/// without a capable backend, or when the window can't be correlated (e.g. foreign-toplevel-only
		/// compositors).
		/// </summary>
		internal static bool TryGetCompositorHandle(Eto.Forms.Form form, string title, int matchW, int matchH, out nint compositorHandle)
		{
			compositorHandle = 0;
			var formHandle = form?.Handle ?? 0;

			if (formHandle == 0 || !IsSupported)
				return false;

			var backend = WaylandBackend.Current;

			if (backend == null)
				return false;

			FormState state;
			nint cachedHandle;

			lock (sync)
			{
				state = GetOrCreateState(formHandle, form);

				cachedHandle = state.CompositorHandle;
			}

			if (cachedHandle != 0)
			{
				if (backend.TryGetWindow(cachedHandle, out _))
				{
					compositorHandle = cachedHandle;
					return true;
				}

				lock (sync)
				{
					if (state.CompositorHandle == cachedHandle)
					{
						if (state.CompositorId.Length > 0)
							_ = claimedIds.Remove(state.CompositorId);

						state.CompositorHandle = 0;
						state.CompositorId = "";
					}
				}
			}

			lock (sync)
			{
				state.Title = title ?? "";
				if (matchW > 0) state.MatchW = matchW;
				if (matchH > 0) state.MatchH = matchH;
			}

			if (!Correlate(backend, state))
				return false;

			lock (sync)
				compositorHandle = state.CompositorHandle;

			return compositorHandle != 0;
		}

		/// <summary>
		/// Reassert a window state (maximize / minimize / restore) on our own window through the compositor
		/// backend. Eto's <c>WindowState</c> setter (gtk_window_(un)maximize / iconify, i.e. an xdg-toplevel
		/// request) is the primary path and works on most compositors, but some drop a client's request, so
		/// driving the backend too makes it stick. Best-effort and asynchronous; no-op off Wayland or without
		/// a capable backend. <paramref name="title"/> and the match size correlate the window the first time,
		/// exactly like <see cref="Position"/>.
		/// </summary>
		internal static void SetWindowState(Eto.Forms.Form form, string title, int matchW, int matchH, FormWindowState windowState)
		{
			var formHandle = form?.Handle ?? 0;

			if (formHandle == 0 || !IsSupported)
				return;

			var startWorker = false;
			FormState state;

			lock (sync)
			{
				state = GetOrCreateState(formHandle, form);

				state.Title = title ?? "";
				if (matchW > 0) state.MatchW = matchW;
				if (matchH > 0) state.MatchH = matchH;
				state.PendingWindowState = windowState;

				if (!state.Busy)
					startWorker = state.Busy = true;
			}

			if (startWorker)
				_ = Task.Run(() => Worker(state));
		}

		/// <summary>
		/// Notify that some other path (e.g. <c>WinMove</c> via the neutral <see cref="WindowInfo"/>) just moved a
		/// compositor window. If it's one of ours that is still being placed, fold the new position into our
		/// target so a pending background placement converges to it rather than reverting to the original Show
		/// position. Harmless for windows we aren't tracking (foreign windows, or ones already settled).
		/// </summary>
		internal static void NotifyExternalMove(nint compositorHandle, int x, int y)
		{
			if (compositorHandle == 0 || (x == WindowInfoBase.Unchanged && y == WindowInfoBase.Unchanged))
				return;

			lock (sync)
			{
				foreach (var state in states.Values)
				{
					if (state.CompositorHandle != compositorHandle)
						continue;

					if (x != WindowInfoBase.Unchanged) state.TargetX = x;
					if (y != WindowInfoBase.Unchanged) state.TargetY = y;
					break;
				}
			}
		}

		/// <summary>Drops cached correlation for a destroyed form so a recycled native handle can't
		/// inherit a stale compositor window.</summary>
		internal static void Forget(nint formHandle)
		{
			lock (sync)
			{
				if (states.Remove(formHandle, out var state) && state.CompositorId.Length > 0)
					_ = claimedIds.Remove(state.CompositorId);
			}
		}

		private static FormState GetOrCreateState(nint formHandle, Eto.Forms.Form form)
		{
			if (!states.TryGetValue(formHandle, out var state))
				states[formHandle] = state = new FormState { FormHandle = formHandle };

			state.Form ??= form;
			return state;
		}

		private static void Worker(FormState state)
		{
			try
			{
				var backend = WaylandBackend.Current;

				if (backend == null || (state.CompositorHandle == 0 && !Correlate(backend, state)))
				{
					lock (sync) state.Busy = false;
					return;
				}

				var borderDone = false;

				while (true)
				{
					int tx, ty;
					FormWindowState? ws;

					lock (sync)
					{
						tx = state.TargetX;
						ty = state.TargetY;
						ws = state.PendingWindowState;
						state.PendingWindowState = null;
					}

					// Reassert maximize/minimize/restore via the backend (Eto already issued the GTK request;
					// this makes it stick on compositors that drop a client's xdg-toplevel state request).
					if (ws.HasValue)
						_ = backend.TrySetWindowState(state.CompositorHandle, ws.Value);

					if (tx != WindowInfoBase.Unchanged || ty != WindowInfoBase.Unchanged)
					{
						var rect = new Rectangle(tx, ty, WindowInfoBase.Unchanged, WindowInfoBase.Unchanged);
						_ = backend.TryMoveResizeWindow(state.CompositorHandle, rect, true, false);
					}

					// Strip the titlebar AFTER the first move: a freshly mapped window may not be fully
					// decorated by the compositor at correlation time, so removing the border before it is
					// drawn doesn't stick. Doing it once the move round-trip has settled the window does.
					if (!borderDone)
					{
						borderDone = true;
						bool removeBorder, keepAbove;
						lock (sync)
						{
							removeBorder = state.RemoveBorder && !state.BorderRemoved;
							keepAbove = state.KeepAbove && !state.KeptAbove;
						}

						if (removeBorder)
						{
							_ = backend.TrySetNoBorder(state.CompositorHandle, true);
							lock (sync) state.BorderRemoved = true;
						}

						// Eto's +AlwaysOnTop (gtk keep-above) is a no-op on Wayland — a client can't keep
						// itself above — so assert it through the compositor.
						if (keepAbove)
						{
							_ = backend.TrySetAlwaysOnTop(state.CompositorHandle, true);
							lock (sync) state.KeptAbove = true;
						}
					}

					lock (sync)
					{
						// Done only when no newer target or window-state request arrived while we were working;
						// otherwise the lock guarantees a fresh Position()/SetWindowState() either sees Busy and
						// lets us loop, or (once we clear Busy here) starts its own worker. No update is lost.
						if (tx == state.TargetX && ty == state.TargetY && !state.PendingWindowState.HasValue)
						{
							state.Busy = false;
							return;
						}
					}
				}
			}
			catch
			{
				lock (sync) state.Busy = false;
			}
		}

		// Locate our window in the compositor's list and claim it. Polls because a just-mapped
		// window may not be reported on the first list.
		private static bool Correlate(IWaylandBackend backend, FormState state)
		{
			var pid = (long)Environment.ProcessId;
			string title, token;
			Eto.Forms.Form form;
			int mw, mh;

			lock (sync)
			{
				title = state.Title;
				mw = state.MatchW;
				mh = state.MatchH;
				form = state.Form;
				token = $"{CorrelationAppIdPrefix}{pid}.{state.FormHandle.ToInt64():x}.{Guid.NewGuid():N}";
			}

			var deadline = Environment.TickCount64 + CorrelateTimeoutMs;
			var stamped = false;

			try
			{
				while (true)
				{
					string existingId;

					lock (sync)
					{
						if (state.CompositorHandle != 0)
							return true;

						existingId = state.CompositorId;
					}

					// Retry the stamp only until it takes (the window must be realized for the app_id to stick);
					// once stamped, don't re-invoke the UI-thread setter every 20ms poll.
					if (!stamped)
						stamped = TrySetAppIdOnUiThread(form, token);

					if (backend.TryListWindows(true, out var windows) && windows != null
						&& Pick(windows, pid, title, mw, mh, existingId, stamped ? token : "") is WaylandWindowInfo pick)
					{
						Claim(state, pick);
						return true;
					}

					if (Environment.TickCount64 >= deadline)
					{
						// If the client accepted the temporary app_id but this backend doesn't expose it, allow one
						// final conservative metadata match before giving up. During the normal polling window, an
						// accepted app_id disables fallback so we don't race app_id propagation and pick the wrong
						// same-title/same-size window.
						if (stamped && backend.TryListWindows(true, out windows) && windows != null
							&& Pick(windows, pid, title, mw, mh, existingId, "") is { } fallback)
						{
							Claim(state, fallback);
							return true;
						}

						return false;
					}

					Thread.Sleep(CorrelatePollMs);
				}
			}
			finally
			{
				if (stamped)
					_ = TrySetAppIdOnUiThread(form, NormalAppId);
			}
		}

		private static void Claim(FormState state, WaylandWindowInfo pick)
		{
			lock (sync)
			{
				if (state.CompositorId.Length > 0 && state.CompositorId != pick.CompositorId)
					_ = claimedIds.Remove(state.CompositorId);

				state.CompositorHandle = pick.Handle;
				state.CompositorId = pick.CompositorId;
				_ = claimedIds.Add(pick.CompositorId);
			}
		}

		private static bool TrySetAppIdOnUiThread(Eto.Forms.Form form, string appId)
		{
			if (form == null || string.IsNullOrEmpty(appId))
				return false;

			try
			{
				var app = Eto.Forms.Application.Instance;

				if (app == null || app.IsUIThread)
					return Eto.Forms.EtoExtensions.SetWaylandAppId(form, appId);

				return app.Invoke(() => Eto.Forms.EtoExtensions.SetWaylandAppId(form, appId));
			}
			catch
			{
				return false;
			}
		}

		private static WaylandWindowInfo Pick(IReadOnlyList<WaylandWindowInfo> windows, long pid, string title, int matchW, int matchH, string existingId, string appIdToken)
		{
			List<WaylandWindowInfo> candidates;

			lock (sync)
				candidates = windows.Where(w => w != null
					&& !string.IsNullOrEmpty(w.CompositorId)
					&& (w.CompositorId == existingId || !claimedIds.Contains(w.CompositorId))).ToList();

			if (candidates.Count == 0)
				return null;

			WaylandWindowInfo Unique(Func<WaylandWindowInfo, bool> predicate)
			{
				WaylandWindowInfo match = null;

				foreach (var candidate in candidates)
				{
					if (!predicate(candidate))
						continue;

					if (match != null)
						return null;

					match = candidate;
				}

				return match;
			}

			if (!string.IsNullOrEmpty(appIdToken))
				return Unique(w => string.Equals(w.AppId, appIdToken, StringComparison.Ordinal));

			bool TitleMatch(WaylandWindowInfo w) =>
				!string.IsNullOrEmpty(title) && string.Equals(w.Title, title, StringComparison.Ordinal);

			bool SizeMatch(WaylandWindowInfo w) =>
				matchW > 0 && matchH > 0
				&& Math.Abs(w.FrameGeometry.Width - matchW) <= SizeTolerance
				&& Math.Abs(w.FrameGeometry.Height - matchH) <= SizeTolerance;

			return Unique(w => w.PID == pid && TitleMatch(w) && SizeMatch(w))
				?? Unique(w => TitleMatch(w) && SizeMatch(w))
				?? Unique(w => w.PID == pid && TitleMatch(w))
				?? Unique(w => w.PID == pid && SizeMatch(w))
				?? Unique(w => w.Active && TitleMatch(w))
				?? Unique(w => w.Active && SizeMatch(w))
				?? Unique(TitleMatch)
				?? Unique(SizeMatch);
		}
	}
}
#endif
