using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Keysharp.Internals.Os.Unix;

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
	/// id. We do it reliably by listing the compositor's windows and matching on OUR process id
	/// (<see cref="Environment.ProcessId"/>) plus the window's title and size, while tracking which
	/// compositor ids are already claimed so two Keysharp windows never resolve to the same one —
	/// far more robust than a fuzzy title-only lookup. The resolved id is cached per form, so only
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
		private sealed class FormState
		{
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
		/// Request that our own window (identified by its native <paramref name="formHandle"/>) be
		/// moved so its top-left sits at screen (<paramref name="x"/>, <paramref name="y"/>). Either
		/// coordinate may be <see cref="WindowInfoBase.Unchanged"/> to leave it untouched.
		/// <paramref name="title"/> and the match size are used only to correlate the window the
		/// first time. Returns immediately; the move runs asynchronously. No-op off Wayland or when
		/// there is no capable backend.
		/// </summary>
		internal static void Position(nint formHandle, string title, int x, int y, int matchW, int matchH, bool removeBorder = false, bool keepAbove = false)
		{
			if (formHandle == 0 || !IsSupported)
				return;

			// Nothing to do if there's no position to apply, no border to strip and no keep-above to assert.
			if (x == WindowInfoBase.Unchanged && y == WindowInfoBase.Unchanged && !removeBorder && !keepAbove)
				return;

			var startWorker = false;
			FormState state;

			lock (sync)
			{
				if (!states.TryGetValue(formHandle, out state))
					states[formHandle] = state = new FormState();

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
		/// Reassert a window state (maximize / minimize / restore) on our own window through the compositor
		/// backend. Eto's <c>WindowState</c> setter (gtk_window_(un)maximize / iconify, i.e. an xdg-toplevel
		/// request) is the primary path and works on most compositors, but some drop a client's request, so
		/// driving the backend too makes it stick. Best-effort and asynchronous; no-op off Wayland or without
		/// a capable backend. <paramref name="title"/> and the match size correlate the window the first time,
		/// exactly like <see cref="Position"/>.
		/// </summary>
		internal static void SetWindowState(nint formHandle, string title, int matchW, int matchH, FormWindowState windowState)
		{
			if (formHandle == 0 || !IsSupported)
				return;

			var startWorker = false;
			FormState state;

			lock (sync)
			{
				if (!states.TryGetValue(formHandle, out state))
					states[formHandle] = state = new FormState();

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
			string title;
			int mw, mh;

			lock (sync)
			{
				title = state.Title;
				mw = state.MatchW;
				mh = state.MatchH;
			}

			var deadline = Environment.TickCount64 + CorrelateTimeoutMs;

			while (true)
			{
				if (backend.TryListWindows(true, out var windows) && windows != null && Pick(windows, pid, title, mw, mh) is WaylandWindowInfo pick)
				{
					lock (sync)
					{
						state.CompositorHandle = pick.Handle;
						state.CompositorId = pick.CompositorId;
						_ = claimedIds.Add(pick.CompositorId);
					}
					return true;
				}

				if (Environment.TickCount64 >= deadline)
					return false;

				Thread.Sleep(CorrelatePollMs);
			}
		}

		private static WaylandWindowInfo Pick(IReadOnlyList<WaylandWindowInfo> windows, long pid, string title, int matchW, int matchH)
		{
			List<WaylandWindowInfo> mine;

			lock (sync)
				mine = windows.Where(w => w != null
					&& w.PID == pid
					&& !string.IsNullOrEmpty(w.CompositorId)
					&& !claimedIds.Contains(w.CompositorId)).ToList();

			if (mine.Count == 0)
				return null;

			bool TitleMatch(WaylandWindowInfo w) =>
				!string.IsNullOrEmpty(title) && string.Equals(w.Title, title, StringComparison.Ordinal);

			bool SizeMatch(WaylandWindowInfo w) =>
				matchW > 0 && matchH > 0
				&& Math.Abs(w.FrameGeometry.Width - matchW) <= SizeTolerance
				&& Math.Abs(w.FrameGeometry.Height - matchH) <= SizeTolerance;

			// Prefer the strongest evidence: title+size, then title, then size; only fall back to a
			// lone candidate when nothing else distinguishes them.
			return mine.FirstOrDefault(w => TitleMatch(w) && SizeMatch(w))
				?? mine.FirstOrDefault(TitleMatch)
				?? mine.FirstOrDefault(SizeMatch)
				?? (mine.Count == 1 ? mine[0] : null);
		}
	}
}
#endif
