using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Window.Linux.Proxies
{
	/// <summary>
	/// Represents a single xwindow proxy for actions affecting x windows.
	/// </summary>
	internal class XWindow : IComparable<XWindow>
	{
		private XWindowAttributes _attributes;

		/// <summary>
		/// The window's attributes. Querying a window that has already been destroyed (a routine race when
		/// watching window events: a transient window can vanish between being noticed and being queried)
		/// makes the X server emit a BadWindow error. That error is asynchronous and, with no handler
		/// installed, the default Xlib handler prints and <c>exit()</c>s the process. So the query is wrapped
		/// in a silent error handler and a failed read returns a zeroed <see cref="XWindowAttributes"/>
		/// (<c>map_state == IsUnmapped</c>, zero geometry) rather than throwing — callers treat a vanished
		/// window as not-viewable/empty, which is the desired behaviour. Use <see cref="TryGetAttributes"/>
		/// when the caller needs to distinguish "gone" from "present".
		/// </summary>
		internal XWindowAttributes Attributes
		{
			get
			{
				_ = TryGetAttributes(out _attributes);
				return _attributes;
			}
		}

		/// <summary>
		/// Reads the window's attributes, returning false (and a zeroed struct) if the window no longer exists
		/// or the read otherwise fails. Suppresses the BadWindow so it never reaches the noisy global handler
		/// or aborts the process.
		/// </summary>
		internal bool TryGetAttributes(out XWindowAttributes attributes)
		{
			attributes = default;

			if (XDisplay == null || XDisplay.Handle == 0 || ID == 0)
				return false;

			var attr = new XWindowAttributes();

			lock (X11Server.xLibLock)
			{
				// Shared, permanently-rooted error handler (see X11Server.BeginErrorTrap): a per-call
				// delegate here races with GTK's Xlib usage on another thread and can SIGSEGV.
				var oldHandler = X11Server.BeginErrorTrap();

				try
				{
					var result = Xlib.XGetWindowAttributes(XDisplay.Handle, ID, ref attr) != 0;
					_ = Xlib.XSync(XDisplay.Handle, false);

					if (X11Server.ErrorTrapped || !result)
						return false;

					attributes = attr;
					_attributes = attr;
					return true;
				}
				finally
				{
					X11Server.EndErrorTrap(oldHandler);
				}
			}
		}

		/// <summary>
		/// ID of the window
		/// </summary>
		internal long ID { get; set; }

		/// <summary>
		/// Backreference to the XDisplay from this Window
		/// </summary>
		internal XDisplay XDisplay { get; } = null;

		internal XWindow(XDisplay display, long window)
		{
			XDisplay = display;
			ID = window;
		}

		public XWindow()
		{
		}

		int IComparable<XWindow>.CompareTo(XWindow other) => ID.CompareTo(other.ID);
	}

	internal class XWindowException : Exception
	{
		//
	}
}

#endif