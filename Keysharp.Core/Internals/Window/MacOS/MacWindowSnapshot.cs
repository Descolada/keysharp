using Keysharp.Builtins;
#if OSX
namespace Keysharp.Internals.Window.MacOS
{
	/// <summary>
	/// CoreGraphics/Accessibility read/write helper for a single macOS window, keyed by handle (the analogue of
	/// the Linux X11Window helper). A single kCGWindow fetch fills every field and is held for the instance's
	/// life. It is read 1:1 by a <see cref="MacWindowInfo"/> (the neutral <see cref="WindowInfoBase"/> subtype
	/// presented to callers), and <see cref="MacWindow"/> also drives a fresh instance per live by-handle
	/// read/mutate. (The mutators stay here for now; relocating them onto MacWindow is a Mac-machine task.)
	/// </summary>
	internal sealed class MacWindowSnapshot
	{
		private MacNativeWindowInfo nativeInfoCache;
		private bool hasNativeInfoCache;
		private bool nativeInfoCacheIncludesText;
		private string title;
		private string className;

		public nint Handle { get; }
		private const int Unchanged = WindowInfoBase.Unchanged;
		private bool IsSpecified => Handle != 0;
		private bool IsIconic => WindowState == FormWindowState.Minimized;

		internal MacWindowSnapshot(nint handle) => Handle = handle;

		internal MacWindowSnapshot(MacNativeWindowInfo nativeInfo, bool includesTextMetadata)
		{
			Handle = (nint)nativeInfo.WindowNumber;
			CacheNativeInfo(nativeInfo, includesTextMetadata);
		}

		private void CacheNativeInfo(MacNativeWindowInfo info, bool includesTextMetadata)
		{
			nativeInfoCache = info;
			hasNativeInfoCache = true;
			nativeInfoCacheIncludesText = includesTextMetadata;
		}

		private void InvalidateNativeInfoCache()
		{
			hasNativeInfoCache = false;
			nativeInfoCacheIncludesText = false;
		}

		private bool TryGetNativeInfo(out MacNativeWindowInfo info, bool includeTextMetadata = false)
		{
			// Fetch-once / no-TTL: a held source MacWindowSnapshot is frozen for its WindowInfo's life (coherent snapshot);
			// a live per-call MacWindowSnapshot is freshly fetched each time, so no expiry is needed in either role.
			if (hasNativeInfoCache
				&& (!includeTextMetadata || nativeInfoCacheIncludesText))
			{
				info = nativeInfoCache;
				return true;
			}

			if (MacNativeWindows.TryGetWindowInfo(Handle, out var latest, includeTextMetadata))
			{
				CacheNativeInfo(latest, includeTextMetadata);
				info = latest;
				return true;
			}

			InvalidateNativeInfoCache();
			info = default;
			return false;
		}

		public bool Active
		{
			get => MacAccessibility.TryGetFocusedWindowHandle(out var focused) && focused == Handle;
			set
			{
				if (!value || !TryGetNativeInfo(out var native))
					return;

				// Restore before activating — matches AHK behaviour where a minimized window
				// is un-minimized even if it is already the foreground window.
				if (IsIconic)
					_ = MacAccessibility.TrySetWindowState(native, FormWindowState.Normal);

				_ = MacAccessibility.TryActivateWindow(native);
			}
		}

		public bool AlwaysOnTop
		{
			get
			{
				if (!TryGetNativeInfo(out var native) || native.OwnerPid != Environment.ProcessId)
					return false;

				return MacNativeWindows.TryGetOwnWindowAlwaysOnTop(native.WindowNumber, out var onTop) && onTop;
			}
			set
			{
				if (!TryGetNativeInfo(out var native))
					return;

				if (native.OwnerPid != Environment.ProcessId)
				{
					// macOS provides no API to change another process's window level, and raising
					// the window via Accessibility requires activating its app each time, which
					// steals keyboard focus -- too disruptive to do automatically. Only our own
					// windows support AlwaysOnTop.
					Ks.OutputDebugLine("AlwaysOnTop is only supported for windows owned by this process on macOS.");
					return;
				}

				if (!MacNativeWindows.TrySetOwnWindowAlwaysOnTop(native.WindowNumber, value))
					Ks.OutputDebugLine("AlwaysOnTop failed for this macOS window.");
			}
		}

		public bool Bottom
		{
			set
			{
				if (!TryGetNativeInfo(out var native))
					return;

				if (!value)
				{
					if (!MacAccessibility.TryRaiseWindow(native))
						Ks.OutputDebugLine("Raising macOS window to top failed.");
					else
						InvalidateNativeInfoCache();

					return;
				}

				if (native.OwnerPid == Environment.ProcessId && MacNativeWindows.TrySendOwnWindowToBack(native.WindowNumber))
				{
					InvalidateNativeInfoCache();
					return;
				}

				Ks.OutputDebugLine("Sending a window to the bottom of the Z order is only supported for windows owned by this process on macOS.");
			}
		}

		public IEnumerable<nint> ChildHandles => [];

		public string ClassName
		{
			get
			{
				if (className != null)
					return className;

				return className = TryGetNativeInfo(out var native, includeTextMetadata: true)
					? native.OwnerName.IsNullOrEmpty() ? "NSWindow" : native.OwnerName
					: DefaultErrorString;
			}
		}

		public Rectangle ClientBounds => Bounds;

		public bool Enabled
		{
			get => Exists;
			set => Ks.OutputDebugLine("Enabled state is not implemented for macOS windows.");
		}

		public bool Exists => TryGetNativeInfo(out _);

		public long ExStyle
		{
			get => 0;
			set => Ks.OutputDebugLine("ExStyles are not supported on macOS.");
		}

		public bool IsHung => false;

		public Rectangle Bounds
		{
			get => TryGetNativeInfo(out var native) ? native.Bounds : Rectangle.Empty;
			set
			{
				if (!TryGetNativeInfo(out var native))
					return;

				var setPos  = value.X != Unchanged || value.Y != Unchanged;
				var setSize = value.Width != Unchanged || value.Height != Unchanged;

				if (!setPos && !setSize)
					return;

				var rect = native.Bounds;

				if (value.X != Unchanged) rect.X = value.X;
				if (value.Y != Unchanged) rect.Y = value.Y;
				if (value.Width != Unchanged) rect.Width = value.Width;
				if (value.Height != Unchanged) rect.Height = value.Height;

				if (MacAccessibility.TryMoveResizeWindow(native, rect, setPos, setSize))
					InvalidateNativeInfoCache();
				else
					_ = Errors.OSErrorOccurred("Move/resize for macOS window failed.");
			}
		}

		// macOS windows have no parent/child window tree exposed to us; a window is its own top-level.
		public nint TopLevelHandle => Handle;

		public nint ParentHandle => 0;

		public long PID => TryGetNativeInfo(out var native) ? native.OwnerPid : 0;

		// Subset of Win32 window styles that have a clear NSWindow style-mask equivalent. macOS has no
		// general window-style concept, so only these frame-related bits are translated; unrepresented
		// bits read back as 0 and are left untouched when setting. Only windows owned by this process
		// can be changed -- macOS exposes no API to restyle another application's window.
		private const long WS_CAPTION = 0x00C00000;     // title bar (WS_BORDER | WS_DLGFRAME) -> Titled
		private const long WS_SYSMENU = 0x00080000;     // window menu / close button          -> Closable
		private const long WS_THICKFRAME = 0x00040000;  // sizing border                       -> Resizable
		private const long WS_MINIMIZEBOX = 0x00020000; // minimize box                        -> Miniaturizable

		public long Style
		{
			get
			{
				if (TryGetNativeInfo(out var native)
						&& native.OwnerPid == Environment.ProcessId
						&& MacNativeWindows.TryGetOwnWindowFrameStyle(native.WindowNumber, out var titled, out var closable, out var resizable, out var miniaturizable))
				{
					long style = 0;

					if (titled) style |= WS_CAPTION;
					if (closable) style |= WS_SYSMENU;
					if (resizable) style |= WS_THICKFRAME;
					if (miniaturizable) style |= WS_MINIMIZEBOX;

					return style;
				}

				return 0;
			}
			set
			{
				if (!TryGetNativeInfo(out var native))
					return;

				if (native.OwnerPid != Environment.ProcessId)
				{
					Ks.OutputDebugLine("Window styles are only supported for windows owned by this process on macOS.");
					return;
				}

				if (MacNativeWindows.TrySetOwnWindowFrameStyle(native.WindowNumber,
						(value & WS_CAPTION) == WS_CAPTION,
						(value & WS_SYSMENU) != 0,
						(value & WS_THICKFRAME) != 0,
						(value & WS_MINIMIZEBOX) != 0))
					InvalidateNativeInfoCache();
				else
					Ks.OutputDebugLine("Setting the window style failed for this macOS window.");
			}
		}

		public List<string> GetText(WindowSearchOptions options)
		{
			var titleText = Title;
			return titleText.IsNullOrEmpty() ? [] : [titleText];
		}

		public string Title
		{
			get
			{
				if (title != null)
					return title;

				if (!TryGetNativeInfo(out var native, includeTextMetadata: true))
					return title = string.Empty;

				if (!native.Title.IsNullOrEmpty())
					return title = native.Title;

				return title = string.Empty;
			}
			set
			{
				if (!TryGetNativeInfo(out var native))
					return;

				var ok = native.OwnerPid == Environment.ProcessId
					? MacNativeWindows.TrySetOwnWindowTitle(native.WindowNumber, value)
					: MacAccessibility.TrySetWindowTitle(native, value);

				if (!ok)
				{
					Ks.OutputDebugLine("Setting the window title failed on macOS.");
					return;
				}

				title = value;
				InvalidateNativeInfoCache();
			}
		}

		public object Transparency
		{
			get => TryGetNativeInfo(out var native) ? (long)Math.Clamp(Convert.ToInt32(native.Alpha * 255.0), 0, 255) : -1L;
			set
			{
				if (!TryGetNativeInfo(out var native))
					return;

				if (native.OwnerPid != Environment.ProcessId)
				{
					// macOS provides no public API to change another process's window opacity.
					Ks.OutputDebugLine("Opacity control is only supported for windows owned by this process on macOS.");
					return;
				}

				var alpha = Math.Clamp(value.Al(), 0, 255) / 255.0;

				if (!MacNativeWindows.TrySetOwnWindowAlpha(native.WindowNumber, alpha))
					Ks.OutputDebugLine("Opacity control failed for this macOS window.");
				else
					InvalidateNativeInfoCache();
			}
		}

		public object TransparentColor
		{
			get
			{
				Ks.OutputDebugLine("Transparency key/color is not supported on macOS.");
				return 0L;
			}
			set => Ks.OutputDebugLine("Transparency key/color is not supported on macOS.");
		}

		public bool Visible
		{
			get => TryGetNativeInfo(out var native) && native.Visible;
			set
			{
				if (value)
					_ = Show();
				else
					_ = Hide();
			}
		}

		public FormWindowState WindowState
		{
			get
			{
				if (!TryGetNativeInfo(out var native))
					return FormWindowState.Normal;

				return MacAccessibility.TryGetWindowState(native, out var state)
					? state
					: native.VisibleOnScreen ? FormWindowState.Normal : FormWindowState.Minimized;
			}
			set
			{
				if (!TryGetNativeInfo(out var native))
					return;

				if (!MacAccessibility.TrySetWindowState(native, value))
					Ks.OutputDebugLine("WindowState for macOS window failed.");
				else
					InvalidateNativeInfoCache();
			}
		}

		public void ChildFindPoint(PointAndHwnd pah)
		{
			if (TryGetNativeInfo(out var native) && native.Bounds.Contains(pah.pt.X, pah.pt.Y))
			{
				pah.hwndFound = Handle;
				pah.rectFound = native.Bounds;
			}
		}

		public void Click(Point? location = null)
		{
			if (TryGetNativeInfo(out var native) && !MacAccessibility.TryClickWindow(native, location, rightButton: false))
				Ks.OutputDebugLine("Native click failed on macOS window.");
		}

		public void ClickRight(Point? location = null)
		{
			if (TryGetNativeInfo(out var native) && !MacAccessibility.TryClickWindow(native, location, rightButton: true))
				Ks.OutputDebugLine("Native right-click failed on macOS window.");
		}

		public POINT ClientToScreen()
			=> TryGetNativeInfo(out var native) ? new POINT(native.Bounds.X, native.Bounds.Y) : new POINT();

		public bool Close()
		{
			if (!TryGetNativeInfo(out var native))
				return false;

			var closed = MacAccessibility.TryCloseWindow(native);
			if (closed)
				InvalidateNativeInfoCache();
			return closed;
		}

		public bool Hide()
		{
			if (!TryGetNativeInfo(out var native))
				return false;

			bool hidden;

			if (native.OwnerPid == Environment.ProcessId)
			{
				// One of our own windows: order it out of the window server entirely so only this
				// window disappears, leaving any other windows of ours untouched. Falls back to
				// minimizing if for some reason the window can't be located.
				hidden = MacNativeWindows.TryHideOwnWindow(native.WindowNumber, native)
						 || MacAccessibility.TrySetWindowState(native, FormWindowState.Minimized);
			}
			else
			{
				// A window belonging to another app: macOS gives us no way to hide just one of
				// another app's windows, so hide the whole app instead. This hides every window
				// of that app, which is the closest available equivalent to AHK's WinHide.
				hidden = MacNativeWindows.HideApplication(native.OwnerPid)
						 || MacAccessibility.TrySetWindowState(native, FormWindowState.Minimized);
			}

			if (hidden)
				InvalidateNativeInfoCache();

			return hidden;
		}

		public bool Kill()
		{
			_ = Close();
			var i = 0;

			while (Exists && i++ < 5)
				System.Threading.Thread.Sleep(0);

			if (!Exists)
				return true;

			try
			{
				using var proc = Process.GetProcessById((int)PID);
				proc.Kill();
			}
			catch
			{
			}

			return !Exists;
		}

		public bool Redraw() => Exists;

		public bool Show()
		{
			if (!TryGetNativeInfo(out var native))
				return false;

			var restored = native.OwnerPid == Environment.ProcessId
							? MacNativeWindows.TryShowOwnWindow(native.WindowNumber)
							: MacNativeWindows.UnhideApplication(native.OwnerPid);

			var stateOk = MacAccessibility.TrySetWindowState(native, FormWindowState.Normal);
			var activated = MacAccessibility.TryActivateWindow(native);

			if (restored || stateOk || activated)
				InvalidateNativeInfoCache();

			return restored || activated;
		}
	}
}
#endif
