#if OSX
namespace Keysharp.Internals.Window.MacOS
{
	/// <summary>
	/// macOS <see cref="IWindowEventBackend"/>. macOS has no single global window-event hook, so the actual work is
	/// done by <see cref="MacAccessibility"/>'s per-application <c>AXObserver</c> stream (see
	/// <c>MacWindowEventObserver.cs</c>), which reports every <see cref="WindowEventType"/> — Active (app switches
	/// and focused-window changes), Create, Close, Move, Show, Minimize, Restore and TitleChange — as CGWindowID
	/// handles. Because that stream is a single shared AX/run-loop installation rather than per-category hooks, this
	/// backend treats the mask as all-or-nothing: it spins the stream up when the first category is requested and
	/// tears it down when the last one is removed. CaretMove is the sole exception — its AXSelectedTextChanged
	/// notification fires per keystroke, so it is registered and removed on its own. The full Accessibility permission
	/// is required; without it the observer stream logs guidance and stays empty.
	/// </summary>
	internal sealed class WindowEventBackend : IWindowEventBackend
	{
		private WindowEventMask installed = WindowEventMask.None;
		private bool disposed;

		public Action<WindowEventRaw> Sink { get; set; }

		// Start/Stop run under WinEventManager's lock; the observer install/teardown itself is posted to the main
		// thread (AX observer and NSWorkspace registration must happen there, and a synchronous round trip under the
		// lock could deadlock against the main-thread callback, which re-enters the manager).
		public void Start(WindowEventMask mask)
		{
			if (disposed || mask == WindowEventMask.None)
				return;

			var wasEmpty = installed == WindowEventMask.None;
			installed |= mask;

			if (wasEmpty && installed != WindowEventMask.None)
			{
				var sink = Sink;
				Script.PostToUIThread(() => MacAccessibility.StartWindowEvents(sink));
			}

			// CaretMove is the one category that isn't all-or-nothing: AXSelectedTextChanged fires per keystroke, so
			// it is registered on (and removed from) the observers separately rather than always being part of the
			// stream. Posted after any start above, so the observers exist by the time it runs.
			if ((mask & WindowEventMask.CaretMove) != 0)
				Script.PostToUIThread(() => MacAccessibility.SetCaretEvents(true));
		}

		public void Stop(WindowEventMask mask)
		{
			installed &= ~mask;

			if ((mask & WindowEventMask.CaretMove) != 0)
				Script.PostToUIThread(() => MacAccessibility.SetCaretEvents(false));

			if (installed == WindowEventMask.None)
				Script.PostToUIThread(MacAccessibility.StopWindowEvents);
		}

		public void Dispose()
		{
			disposed = true;
			installed = WindowEventMask.None;
			Script.PostToUIThread(() => MacAccessibility.SetCaretEvents(false));
			Script.PostToUIThread(MacAccessibility.StopWindowEvents);
		}
	}
}
#endif
