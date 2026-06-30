namespace Keysharp.Internals
{

#if OSX
	internal sealed class MacWindow : WindowBase
	{
		private const int Unchanged = WindowInfoBase.Unchanged;
		private const long WS_CAPTION = 0x00C00000;
		private const long WS_SYSMENU = 0x00080000;
		private const long WS_THICKFRAME = 0x00040000;
		private const long WS_MINIMIZEBOX = 0x00020000;

		// A by-handle MacWindowInfo for the live read path (Get*/GetText/hit-test) — its scalar reads land on the
		// neutral subtype directly. Actions go through the Try* methods below, which fetch a fresh descriptor.
		private static MacWindowInfo Item(nint h) => new (h);

		// macOS off-process titles come from the kCGWindow batch (unavailable when re-queried by handle), so a
		// by-handle MacWindowInfo lazily fetches its descriptor once; enumerate seeds each from the batch.
		public override WindowInfoBase CreateWindow(nint id) => new MacWindowInfo(id);

		public override WindowInfoBase ActiveWindow()
		{
			var fh = GetForegroundHandle();
			return fh == 0 ? new WindowInfo(0) : CreateWindow(fh);
		}

		// --- granular live reads (rare: WindowInfo reads from its held source; these serve by-handle lookups) ---
		public override string GetTitle(nint h) => Item(h).Title;
		public override string GetClassName(nint h) => Item(h).ClassName;
		public override long GetPid(nint h) => Item(h).PID;
		public override Rectangle GetBounds(nint h) => Item(h).Bounds;
		public override Rectangle GetClientBounds(nint h) => Item(h).ClientBounds;
		public override long GetStyle(nint h) => Item(h).Style;
		public override long GetExStyle(nint h) => Item(h).ExStyle;
		public override bool GetActive(nint h) => Item(h).Active;
		public override bool GetVisible(nint h) => Item(h).Visible;
		public override bool GetEnabled(nint h) => Item(h).Enabled;
		public override bool GetHung(nint h) => Item(h).IsHung;
		public override bool GetExists(nint h) => Item(h).Exists;
		public override FormWindowState GetWindowState(nint h) => Item(h).WindowState;
		public override bool GetAlwaysOnTop(nint h) => Item(h).AlwaysOnTop;
		public override object GetTransparency(nint h) => Item(h).Transparency;
		public override object GetTransparentColor(nint h) => Item(h).TransparentColor;
		public override POINT ClientToScreen(nint h) => Item(h).ClientToScreen();
		public override bool IsWindow(nint h) => Item(h).Exists;

		public override bool TryGetText(nint h, bool detectHidden, out List<string> text)
		{
			text = Item(h).GetText(new WindowSearchOptions { DetectHiddenText = detectHidden });
			return true;
		}

		public override void ChildFindPoint(nint h, PointAndHwnd pah) => Item(h).ChildFindPoint(pah);

		public override bool TryClientToScreen(nint h, ref Point pt)
		{
			var o = Item(h).ClientToScreen();
			pt = new Point(pt.X + o.X, pt.Y + o.Y);
			return true;
		}

		// macOS exposes no window tree to us: every window is its own top-level with no children.
		public override bool TryGetParent(nint h, out nint parent) { parent = 0; return false; }
		public override bool TryGetTopLevel(nint h, out nint top) { top = h; return h != 0; }
		public override bool TryEnumerateChildren(nint h, out IReadOnlyList<nint> children) { children = []; return true; }

		// --- control: fetch the descriptor by handle and drive MacAccessibility/MacNativeWindows directly ---
		public override bool TrySetAlwaysOnTop(nint h, bool onTop)
		{
			if (!MacNativeWindows.TryGetWindowInfo(h, out var native))
				return true;

			if (native.OwnerPid != Environment.ProcessId)
			{
				// macOS has no API to change another process's window level (raising via Accessibility would steal
				// focus). Only our own windows support AlwaysOnTop.
				Ks.OutputDebugLine("AlwaysOnTop is only supported for windows owned by this process on macOS.");
				return false;
			}

			if (!MacNativeWindows.TrySetOwnWindowAlwaysOnTop(native.WindowNumber, onTop))
				Ks.OutputDebugLine("AlwaysOnTop failed for this macOS window.");

			return true;
		}

		public override bool TryMoveResize(nint h, Rectangle bounds, bool setPos, bool setSize)
		{
			if (!MacNativeWindows.TryGetWindowInfo(h, out var native))
				return true;

			var rect = native.Bounds;

			if (bounds.X != Unchanged) rect.X = bounds.X;
			if (bounds.Y != Unchanged) rect.Y = bounds.Y;
			if (bounds.Width != Unchanged) rect.Width = bounds.Width;
			if (bounds.Height != Unchanged) rect.Height = bounds.Height;

			if (!MacAccessibility.TryMoveResizeWindow(native, rect, setPos, setSize))
				_ = Errors.OSErrorOccurred("Move/resize for macOS window failed.");

			return true;
		}

		public override bool TrySetState(nint h, FormWindowState state)
		{
			if (!MacNativeWindows.TryGetWindowInfo(h, out var native))
				return true;

			if (!MacAccessibility.TrySetWindowState(native, state))
				Ks.OutputDebugLine("WindowState for macOS window failed.");

			return true;
		}

		public override bool TrySetStyle(nint h, long style)
		{
			if (!MacNativeWindows.TryGetWindowInfo(h, out var native))
				return true;

			if (native.OwnerPid != Environment.ProcessId)
			{
				Ks.OutputDebugLine("Window styles are only supported for windows owned by this process on macOS.");
				return false;
			}

			if (!MacNativeWindows.TrySetOwnWindowFrameStyle(native.WindowNumber,
					(style & WS_CAPTION) == WS_CAPTION,
					(style & WS_SYSMENU) != 0,
					(style & WS_THICKFRAME) != 0,
					(style & WS_MINIMIZEBOX) != 0))
				Ks.OutputDebugLine("Setting the window style failed for this macOS window.");

			return true;
		}

		public override bool TrySetExStyle(nint h, long exStyle)
		{
			Ks.OutputDebugLine("ExStyles are not supported on macOS.");
			return false;
		}

		public override bool TrySetTransparency(nint h, object alpha)
		{
			if (!MacNativeWindows.TryGetWindowInfo(h, out var native))
				return true;

			if (native.OwnerPid != Environment.ProcessId)
			{
				Ks.OutputDebugLine("Opacity control is only supported for windows owned by this process on macOS.");
				return false;
			}

			var a = Math.Clamp(alpha.Al(), 0, 255) / 255.0;

			if (!MacNativeWindows.TrySetOwnWindowAlpha(native.WindowNumber, a))
				Ks.OutputDebugLine("Opacity control failed for this macOS window.");

			return true;
		}

		public override bool TryActivate(nint h)
		{
			if (!MacNativeWindows.TryGetWindowInfo(h, out var native))
				return true;

			// Restore before activating — a minimized window is un-minimized even if already foreground.
			if (MacAccessibility.TryGetWindowState(native, out var st) && st == FormWindowState.Minimized)
				_ = MacAccessibility.TrySetWindowState(native, FormWindowState.Normal);

			_ = MacAccessibility.TryActivateWindow(native);
			return true;
		}

		public override bool TrySetZOrder(nint h, ZOrder z)
		{
			if (!MacNativeWindows.TryGetWindowInfo(h, out var native))
				return true;

			if (z != ZOrder.Bottom)   // raise to top
			{
				if (!MacAccessibility.TryRaiseWindow(native))
					Ks.OutputDebugLine("Raising macOS window to top failed.");

				return true;
			}

			if (native.OwnerPid == Environment.ProcessId && MacNativeWindows.TrySendOwnWindowToBack(native.WindowNumber))
				return true;

			Ks.OutputDebugLine("Sending a window to the bottom of the Z order is only supported for windows owned by this process on macOS.");
			return false;
		}

		public override bool TryClose(nint h)
			=> MacNativeWindows.TryGetWindowInfo(h, out var native) && MacAccessibility.TryCloseWindow(native);

		public override bool TryKill(nint h)
		{
			_ = TryClose(h);
			var i = 0;

			while (MacNativeWindows.TryGetWindowInfo(h, out _) && i++ < 5)
				System.Threading.Thread.Sleep(0);

			if (!MacNativeWindows.TryGetWindowInfo(h, out var native))
				return true;

			try
			{
				using var proc = Process.GetProcessById(native.OwnerPid);
				proc.Kill();
			}
			catch
			{
			}

			return !MacNativeWindows.TryGetWindowInfo(h, out _);
		}

		public override bool TryHide(nint h)
		{
			if (!MacNativeWindows.TryGetWindowInfo(h, out var native))
				return false;

			if (native.OwnerPid == Environment.ProcessId)
				// One of our own windows: order it out of the window server so only this window disappears.
				return MacNativeWindows.TryHideOwnWindow(native.WindowNumber, native)
					   || MacAccessibility.TrySetWindowState(native, FormWindowState.Minimized);

			// Another app's window: macOS gives no way to hide just one, so hide the whole app (closest to WinHide).
			return MacNativeWindows.HideApplication(native.OwnerPid)
				   || MacAccessibility.TrySetWindowState(native, FormWindowState.Minimized);
		}

		public override bool TryShow(nint h)
		{
			if (!MacNativeWindows.TryGetWindowInfo(h, out var native))
				return false;

			var restored = native.OwnerPid == Environment.ProcessId
							? MacNativeWindows.TryShowOwnWindow(native.WindowNumber)
							: MacNativeWindows.UnhideApplication(native.OwnerPid);

			_ = MacAccessibility.TrySetWindowState(native, FormWindowState.Normal);
			var activated = MacAccessibility.TryActivateWindow(native);
			return restored || activated;
		}

		public override bool TryRedraw(nint h) => MacNativeWindows.TryGetWindowInfo(h, out _);

		public override bool TryClick(nint h, Point at, uint button, int count)
		{
			if (!MacNativeWindows.TryGetWindowInfo(h, out var native))
				return true;

			for (var i = 0; i < count; i++)
				if (!MacAccessibility.TryClickWindow(native, at, rightButton: button == 2))
					Ks.OutputDebugLine("Native click failed on macOS window.");

			return true;
		}

		public override bool TrySetTitle(nint h, string title)
		{
			if (!MacNativeWindows.TryGetWindowInfo(h, out var native))
				return true;

			var ok = native.OwnerPid == Environment.ProcessId
				? MacNativeWindows.TrySetOwnWindowTitle(native.WindowNumber, title)
				: MacAccessibility.TrySetWindowTitle(native, title);

			if (!ok)
				Ks.OutputDebugLine("Setting the window title failed on macOS.");

			return true;
		}

		public override bool TrySetVisible(nint h, bool visible) => visible ? TryShow(h) : TryHide(h);

		public override bool TrySetEnabled(nint h, bool enabled)
		{
			Ks.OutputDebugLine("Enabled state is not implemented for macOS windows.");
			return false;
		}

		public override bool TrySetTransparentColor(nint h, object color)
		{
			Ks.OutputDebugLine("Transparency key/color is not supported on macOS.");
			return false;
		}

		public override nint GetForegroundHandle()
			=> MacAccessibility.TryGetFocusedWindowHandle(out var h) && h != 0
				? h
				: 0;

		public override IReadOnlyList<WindowInfoBase> Enumerate(bool includeHidden)
		{
			// Snapshot() requests kCGWindowName, omitted for other processes' windows unless Screen Recording
			// access is granted; without it title-based matching silently finds nothing.
			_ = MacAccessibility.EnsureScreenCaptureAccess("enumerate window titles", prompt: true);

			var list = new List<WindowInfoBase>();
			var wins = MacNativeWindows.Snapshot();

			for (int i = 0; i < wins.Count; i++)
			{
				var info = wins[i];

				// Off-screen entries some apps register for menu-bar/status items aren't real windows and can't be
				// queried individually; only exclude them when hidden windows aren't being searched.
				if (!includeHidden && !info.IsOnScreen && !MacNativeWindows.TryGetWindowInfo((nint)info.WindowNumber, out _))
					continue;

				if (includeHidden || info.Visible)
					list.Add(new MacWindowInfo(info, includesTextMetadata: true));   // seeded from the batch
			}

			return list;
		}

		public override bool TryGetAt(int x, int y, out nint child)
		{
			if (MacNativeWindows.TryGetWindowAtPoint(new POINT(x, y), out var native))
			{
				child = (nint)native.WindowNumber;
				return true;
			}

			child = default;
			return false;
		}

		public override uint GetFocusedControlThread(nint window, out nint control)
		{
			control = 0;
			var hwnd = window != 0 ? window : GetForegroundHandle();

			if (hwnd == 0)
				return 0;

			if (MacNativeWindows.TryGetWindowInfo(hwnd, out var native, includeTextMetadata: false))
				return (uint)Math.Max(0, native.OwnerPid);

			return 0;
		}
	}
#endif
}
