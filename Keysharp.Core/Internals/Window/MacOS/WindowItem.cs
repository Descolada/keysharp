using Keysharp.Builtins;
#if OSX
namespace Keysharp.Internals.Window.MacOS
{
	internal class WindowItem : WindowItemBase
	{
		private const long NativeInfoCacheLifetimeTicks = TimeSpan.TicksPerMillisecond * 200;
		private MacNativeWindowInfo nativeInfoCache;
		private bool hasNativeInfoCache;
		private bool nativeInfoCacheIncludesText;
		private long nativeInfoCacheExpiryTicks;

		internal WindowItem(nint handle) : base(handle)
		{
		}

		internal WindowItem(MacNativeWindowInfo nativeInfo, bool includesTextMetadata) : base((nint)nativeInfo.WindowNumber)
			=> CacheNativeInfo(nativeInfo, includesTextMetadata);

		private void CacheNativeInfo(MacNativeWindowInfo info, bool includesTextMetadata)
		{
			nativeInfoCache = info;
			hasNativeInfoCache = true;
			nativeInfoCacheIncludesText = includesTextMetadata;
			nativeInfoCacheExpiryTicks = DateTime.UtcNow.Ticks + NativeInfoCacheLifetimeTicks;
		}

		private void InvalidateNativeInfoCache()
		{
			hasNativeInfoCache = false;
			nativeInfoCacheIncludesText = false;
			nativeInfoCacheExpiryTicks = 0;
		}

		private bool TryGetNativeInfo(out MacNativeWindowInfo info, bool includeTextMetadata = false)
		{
			var nowTicks = DateTime.UtcNow.Ticks;

			if (hasNativeInfoCache
				&& nowTicks < nativeInfoCacheExpiryTicks
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

		internal override bool Active
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

		internal override bool AlwaysOnTop
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

		internal override bool Bottom
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

		internal override HashSet<WindowItemBase> ChildWindows => [];

		internal override string ClassName
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

		internal override Rectangle ClientLocation => Location;

		internal override bool Enabled
		{
			get => Exists;
			set => Ks.OutputDebugLine("Enabled state is not implemented for macOS windows.");
		}

		internal override bool Exists => TryGetNativeInfo(out _);

		internal override long ExStyle
		{
			get => 0;
			set => Ks.OutputDebugLine("ExStyles are not supported on macOS.");
		}

		internal override bool IsHung => false;

		internal override Rectangle Location
		{
			get => TryGetNativeInfo(out var native) ? native.Bounds : Rectangle.Empty;
			set
			{
				if (!TryGetNativeInfo(out var native))
					return;

				if (!MacAccessibility.TryMoveResizeWindow(native, value, setPosition: true, setSize: false))
					Ks.OutputDebugLine("Move for macOS window failed.");
				else
					InvalidateNativeInfoCache();
			}
		}

		internal override WindowItemBase NonChildParentWindow => this;

		internal override WindowItemBase ParentWindow => null;

		internal override long PID => TryGetNativeInfo(out var native) ? native.OwnerPid : 0;

		internal override string Path
		{
			get
			{
				if (!processPath.IsNullOrEmpty())
					return processPath;

				var pid = PID;

				if (pid <= 0)
					return DefaultErrorString;

				var app = MonoMac.AppKit.NSRunningApplication.GetRunningApplication((int)pid);
				var url = app?.ExecutableUrl;

				if (url?.Path is string path && !path.IsNullOrEmpty())
					return processPath = path;

				return base.Path;
			}
		}

		internal override string ProcessName
		{
			get
			{
				if (!processName.IsNullOrEmpty())
					return processName;

				var path = Path;

				if (!path.IsNullOrEmpty() && path != DefaultErrorString)
					return processName = System.IO.Path.GetFileName(path);

				return base.ProcessName;
			}
		}

		internal override Size Size
		{
			get
			{
				var location = Location;
				return new Size(location.Width, location.Height);
			}
			set
			{
				if (!TryGetNativeInfo(out var native))
					return;

				var rect = native.Bounds;
				rect.Width = value.Width;
				rect.Height = value.Height;

				if (!MacAccessibility.TryMoveResizeWindow(native, rect, setPosition: false, setSize: true))
					Ks.OutputDebugLine("Resize for macOS window failed.");
				else
					InvalidateNativeInfoCache();
			}
		}

		internal override long Style
		{
			get => 0;
			set => Ks.OutputDebugLine("Styles are not supported on macOS.");
		}

		internal override List<string> Text
		{
			get
			{
				var titleText = Title;
				return titleText.IsNullOrEmpty() ? [] : [titleText];
			}
		}

		internal override string Title
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

		internal override object Transparency
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

		internal override object TransparentColor
		{
			get
			{
				Ks.OutputDebugLine("Transparency key/color is not supported on macOS.");
				return 0L;
			}
			set => Ks.OutputDebugLine("Transparency key/color is not supported on macOS.");
		}

		internal override bool Visible
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

		internal override FormWindowState WindowState
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

		internal override void ChildFindPoint(PointAndHwnd pah)
		{
			if (TryGetNativeInfo(out var native) && native.Bounds.Contains(pah.pt.X, pah.pt.Y))
			{
				pah.hwndFound = Handle;
				pah.rectFound = native.Bounds;
			}
		}

		internal override void Click(Point? location = null)
		{
			if (TryGetNativeInfo(out var native) && !MacAccessibility.TryClickWindow(native, location, rightButton: false))
				Ks.OutputDebugLine("Native click failed on macOS window.");
		}

		internal override void ClickRight(Point? location = null)
		{
			if (TryGetNativeInfo(out var native) && !MacAccessibility.TryClickWindow(native, location, rightButton: true))
				Ks.OutputDebugLine("Native right-click failed on macOS window.");
		}

		internal override POINT ClientToScreen()
			=> TryGetNativeInfo(out var native) ? new POINT(native.Bounds.X, native.Bounds.Y) : new POINT();

		internal override bool Close()
		{
			if (!TryGetNativeInfo(out var native))
				return false;

			var closed = MacAccessibility.TryCloseWindow(native);
			if (closed)
				InvalidateNativeInfoCache();
			return closed;
		}

		internal override bool Hide()
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

		internal override bool Kill()
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

		internal override bool Redraw() => Exists;

		internal override bool Show()
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
