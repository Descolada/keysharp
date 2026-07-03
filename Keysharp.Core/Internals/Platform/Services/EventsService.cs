namespace Keysharp.Internals
{

	// Backend is a factory, not a cached service: each WinEventManager (one per Script) takes
	// ownership of the returned backend and disposes it, so handing out a memoized instance
	// would give every Script after the first a disposed backend.
#if WINDOWS
	internal sealed class WindowsEvents : IWindowEvents
	{
		public IWindowEventBackend Backend => new Keysharp.Internals.Window.Windows.WindowEventBackend();
	}
#elif LINUX
	/// <summary>
	/// Linux window-event backend selection. On a Wayland session the compositor-native backend is preferred
	/// (it sees both native Wayland and XWayland windows) when the active compositor can actually push events;
	/// otherwise the X11 backend is used, which still observes XWayland windows via the always-present X server.
	/// </summary>
	internal sealed class LinuxEvents : IWindowEvents
	{
		public IWindowEventBackend Backend => Resolve();

		private static IWindowEventBackend Resolve()
		{
			if (IsWaylandSession)
			{
				var wayland = WaylandBackend.Current;

				if (wayland != null && wayland.SupportsWindowEvents)
					return new WaylandWindowEventBackend(wayland);
			}

			return new WindowEventBackend();
		}
	}
#elif OSX
	internal sealed class MacEvents : IWindowEvents
	{
		public IWindowEventBackend Backend => new WindowEventBackend();
	}
#endif
}
