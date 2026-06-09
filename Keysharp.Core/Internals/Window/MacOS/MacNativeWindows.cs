using Keysharp.Builtins;
#if OSX
using System.Runtime.InteropServices;
using MonoMac.AppKit;
using MonoMac.Foundation;

namespace Keysharp.Internals.Window.MacOS
{
	internal readonly struct MacNativeWindowInfo
	{
		internal readonly uint WindowNumber;
		internal readonly int OwnerPid;
		internal readonly string OwnerName;
		internal readonly string Title;
		internal readonly Rectangle Bounds;
		internal readonly bool IsOnScreen;
		internal readonly double Alpha;

		internal MacNativeWindowInfo(uint windowNumber, int ownerPid, string ownerName, string title, Rectangle bounds, bool isOnScreen, double alpha)
		{
			WindowNumber = windowNumber;
			OwnerPid = ownerPid;
			OwnerName = ownerName ?? string.Empty;
			Title = title ?? string.Empty;
			Bounds = bounds;
			IsOnScreen = isOnScreen;
			Alpha = alpha;
		}

		// True for any real window regardless of on-screen state (includes minimized windows in the Dock).
		// Minimized macOS windows have kCGWindowIsOnscreen=false but are NOT "hidden" in the AHK sense.
		internal bool Visible => Alpha > 0.001 && Bounds.Width > 0 && Bounds.Height > 0;

		// True only when the window is physically on screen — used by point-hit-testing.
		internal bool VisibleOnScreen => IsOnScreen && Visible;

		// kCGWindowLayer alone can't separate "system chrome to skip" from "real app window":
		// Eto maps Gui's "+AlwaysOnTop" to NSWindowLevel.PopUpMenu (101), which sits well above
		// the Dock's own overlay layer (20) and even real popup menus. Layer is therefore not a
		// reliable discriminator — match on the owning process instead.
		internal bool IsDockOwned => string.Equals(OwnerName, "Dock", StringComparison.Ordinal);

		// The Dock process owns two very different windows: the visible dock bar (normal-sized,
		// should be matchable like any other window) and a large transparent overlay that covers
		// (essentially) the whole screen and would win every point hit-test if not excluded.
		// Distinguish them by size rather than skipping every Dock-owned window outright.
		internal bool IsFullScreenOverlay
		{
			get
			{
				var screen = Forms.Screen.PrimaryScreen.Bounds;
				return screen.Width > 0 && screen.Height > 0
					   && Bounds.Width >= screen.Width * 0.9
					   && Bounds.Height >= screen.Height * 0.9;
			}
		}
	}

	internal static partial class MacNativeWindows
	{
		private const uint kCFStringEncodingUTF8 = 0x08000100;
		private const int kCFNumberSInt32Type = 3;
		private const int kCFNumberDoubleType = 13;
		private const uint kCGWindowListOptionAll = 0u;
		private const uint kCGWindowListOptionOnScreenOnly = 1u;
		private const uint kCGWindowListOptionIncludingWindow = 8u;
		private const uint kCGWindowListExcludeDesktopElements = 16u;

		[StructLayout(LayoutKind.Sequential)]
		private struct CGRectNative
		{
			internal double X;
			internal double Y;
			internal double Width;
			internal double Height;
		}

		private static readonly nint kWindowNumber = CreateCFString("kCGWindowNumber");
		private static readonly nint kOwnerPid = CreateCFString("kCGWindowOwnerPID");
		private static readonly nint kOwnerName = CreateCFString("kCGWindowOwnerName");
		private static readonly nint kWindowName = CreateCFString("kCGWindowName");
		private static readonly nint kWindowBounds = CreateCFString("kCGWindowBounds");
		private static readonly nint kWindowAlpha = CreateCFString("kCGWindowAlpha");
		private static readonly nint kWindowIsOnscreen = CreateCFString("kCGWindowIsOnscreen");

		[LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
		private static partial nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

		[LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CGRectMakeWithDictionaryRepresentation(nint dict, out CGRectNative rect);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", StringMarshalling = StringMarshalling.Utf8)]
		private static partial nint CFStringCreateWithCString(nint alloc, string cStr, uint encoding);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		private static partial void CFRelease(nint cfTypeRef);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		private static partial nint CFArrayGetCount(nint theArray);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		private static partial nint CFArrayGetValueAtIndex(nint theArray, nint idx);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CFDictionaryGetValueIfPresent(nint theDict, nint key, out nint value);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		private static partial nint CFGetTypeID(nint cf);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		private static partial nint CFStringGetTypeID();

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		private static partial nint CFStringGetLength(nint theString);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		private static partial nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CFStringGetCString(nint theString, byte[] buffer, nint bufferSize, uint encoding);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		private static partial nint CFNumberGetTypeID();

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CFNumberGetValue(nint number, int theType, out int value);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CFNumberGetValue(nint number, int theType, out double value);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		private static partial nint CFBooleanGetTypeID();

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CFBooleanGetValue(nint boolean);

		internal static List<MacNativeWindowInfo> Snapshot(bool onScreenOnly = false) => SnapshotCore(onScreenOnly, includeTextMetadata: true);

		internal static bool TryGetWindowInfo(nint handle, out MacNativeWindowInfo info) => TryGetWindowInfo(handle, out info, includeTextMetadata: true);

		internal static bool TryGetWindowInfo(nint handle, out MacNativeWindowInfo info, bool includeTextMetadata)
		{
			if (handle == 0)
			{
				info = default;
				return false;
			}

			var id = unchecked((uint)handle.ToInt64());
			var snapshot = SnapshotCore(onScreenOnly: false, includeTextMetadata, includeSingleWindow: true, relativeToWindow: id);

			if (snapshot.Count > 0)
			{
				info = snapshot[0];
				return true;
			}

			info = default;
			return false;
		}

		internal static bool TryGetWindowAtPoint(POINT location, out MacNativeWindowInfo info)
		{
			// Owner name is needed to recognize the Dock's full-screen overlay window below.
			var snapshot = SnapshotCore(
				onScreenOnly: true,
				includeTextMetadata: false,
				includeOwnerName: true);

			// The Dock's transparent full-screen overlay sits in front of (and covers) the entire
			// desktop, including the actual dock bar — there's no separate "real" window to prefer
			// it over. Rather than excluding it outright (which would make the Dock uninspectable),
			// defer it: prefer any other containing window first, and only report the overlay — as
			// "Dock" — when the point genuinely isn't over anything else (e.g. the dock bar itself).
			MacNativeWindowInfo? deferredOverlay = null;

			foreach (var w in snapshot)
			{
				if (!w.VisibleOnScreen || !w.Bounds.Contains(location.X, location.Y))
					continue;

				if (w.IsDockOwned && w.IsFullScreenOverlay)
				{
					deferredOverlay ??= w;
					continue;
				}

				info = w;
				return true; // front-to-back order, first real containing window wins
			}

			if (deferredOverlay.HasValue)
			{
				info = deferredOverlay.Value;
				return true;
			}

			info = default;
			return false;
		}

		// Coalesces rapid native window visibility changes (e.g. a window that is shown and then
		// immediately self-hidden, such as Keysharp's MainWindow when AllowShowDisplay is false)
		// into a single activation-policy update, so the Dock icon never flickers on for windows
		// the user never actually sees.
		private static System.Threading.Timer activationPolicyDebounceTimer;
		private static readonly object activationPolicyLock = new();

		internal static void SetActivationPolicy(bool accessory)
		{
			try
			{
				NSApplication.SharedApplication.ActivationPolicy =
					accessory
					? NSApplicationActivationPolicy.Accessory
					: NSApplicationActivationPolicy.Regular;
			}
			catch { }
		}

		// Counts native dialogs shown via Dialogs.RunInterruptibleDialog (MsgBox, InputBox,
		// FileSelect, DirSelect, ...). These render as native NSAlert/NSOpenPanel-backed windows
		// that Eto never registers in Application.Instance.Windows, so RequestActivationPolicyUpdate
		// can't see them through the Eto window list below -- they must be tracked explicitly.
		// See Dialogs.RunInterruptibleDialog for the increment/decrement.
		internal static int ActiveNativeDialogs;

		// AppKit creates plenty of NSWindow instances we never want to put the app in the Dock for
		// -- status-item context menus, tooltips, popovers, and the like -- and none of those are
		// ever registered as Eto windows. Conversely, every window Keysharp itself creates IS an
		// Eto window, and already carries the right intent via ShowInTaskbar (set to false for
		// ToolTips and Gui +ToolWindow, true for ordinary windows). So: a native window only counts
		// as user-facing if it corresponds to a tracked Eto window that wants to be in the taskbar.
		private static bool IsUserFacingWindow(NSWindow native)
		{
			var app = Eto.Forms.Application.Instance;

			if (app == null)
				return false;

			foreach (var window in app.Windows)
			{
				// Compare native handles rather than the managed wrapper objects -- MonoMac can hand
				// back distinct wrapper instances for the same underlying NSWindow* across calls, so
				// ReferenceEquals(window.ControlObject, native) silently never matches.
				if (window.ControlObject is NSObject nativeWindow && nativeWindow.Handle == native.Handle)
					return window.ShowInTaskbar;
			}

			return false;
		}

		// Re-derives the correct policy from the actual, current native window list -- rather
		// than from any cached/forced state -- and applies it after a short debounce. Because
		// the check always looks at ground truth at the moment it runs, no call site needs to
		// (or should) force the policy directly; just request a re-evaluation when something
		// might have changed.
		internal static void RequestActivationPolicyUpdate()
		{
			lock (activationPolicyLock)
			{
				activationPolicyDebounceTimer?.Dispose();
				activationPolicyDebounceTimer = new System.Threading.Timer(_ =>
				{
					// Timer callbacks run on a thread-pool worker thread, but IsUserFacingWindow reads
					// Eto's window.ShowInTaskbar (and walks the native NSWindow list), both of which
					// must only be touched on the UI thread -- so marshal the check+apply over to it.
					try
					{
						_ = Eto.Forms.Application.Instance.InvokeAsync(() =>
						{
							try
							{
								var anyUserFacingWindowVisible = ActiveNativeDialogs > 0
									|| NSApplication.SharedApplication.Windows.Any(w => w.IsVisible && IsUserFacingWindow(w));
								SetActivationPolicy(accessory: !anyUserFacingWindowVisible);
							}
							catch { }
						});
					}
					catch { }
				}, null, 150, System.Threading.Timeout.Infinite);
			}
		}

		// Registers observers covering every way a window's effective visibility to the user can
		// change: appearing/becoming key, resigning key, miniaturizing/restoring, occlusion-state
		// changes (e.g. moving on/off screen, behind other apps), and closing. Each one simply
		// requests a re-evaluation rather than assuming what the resulting state should be.
		internal static void RegisterWindowPolicyObservers()
		{
			try
			{
				var nc = MonoMac.Foundation.NSNotificationCenter.DefaultCenter;
				Action<MonoMac.Foundation.NSNotification> onChange = _ => RequestActivationPolicyUpdate();

				nc.AddObserver("NSWindowDidBecomeKeyNotification", onChange);
				nc.AddObserver("NSWindowDidResignKeyNotification", onChange);
				nc.AddObserver("NSWindowDidMiniaturizeNotification", onChange);
				nc.AddObserver("NSWindowDidDeminiaturizeNotification", onChange);
				nc.AddObserver("NSWindowDidChangeOcclusionStateNotification", onChange);
				nc.AddObserver("NSWindowWillCloseNotification", onChange);
			}
			catch { }
		}

		internal static bool ActivateAppByPid(int pid)
		{
			if (pid <= 0)
				return false;

			try
			{
				var app = NSRunningApplication.GetRunningApplication(pid);
				if (app != null)
				{
					// Some MonoMac/Xamarin.Mac bindings do not expose ActivateIgnoringOtherApps by name.
					// Use the documented flag value (2) for compatibility across binding versions.
					var ignoreOtherApps = (NSApplicationActivationOptions)2;
					return app.Activate(NSApplicationActivationOptions.ActivateAllWindows | ignoreOtherApps);
				}
			}
			catch
			{
			}

			// Fallback for cases where NSRunningApplication activation is unavailable or denied.
			try
			{
				var script = $"tell application \"System Events\" to set frontmost of first process whose unix id is {pid} to true";
				return script.AppleScript(wait: true) == 0;
			}
			catch
			{
				return false;
			}
		}

		private static List<MacNativeWindowInfo> SnapshotCore(bool onScreenOnly, bool includeTextMetadata, bool includeSingleWindow = false, uint relativeToWindow = 0, bool includeOwnerName = false)
		{
			var options = includeSingleWindow
				? kCGWindowListOptionIncludingWindow | kCGWindowListExcludeDesktopElements
				: (onScreenOnly ? kCGWindowListOptionOnScreenOnly : kCGWindowListOptionAll) | kCGWindowListExcludeDesktopElements;
			var arrayRef = CGWindowListCopyWindowInfo(options, relativeToWindow);
			if (arrayRef == 0)
				return [];

			try
			{
				var count = CFArrayGetCount(arrayRef);
				var capacity = count > int.MaxValue ? int.MaxValue : (int)count;
				var list = new List<MacNativeWindowInfo>(capacity);

				for (nint i = 0; i < count; i++)
				{
					var dictRef = CFArrayGetValueAtIndex(arrayRef, i);
					if (!TryGetUInt32(dictRef, kWindowNumber, out var windowNumber))
						continue;

					_ = TryGetInt32(dictRef, kOwnerPid, out var ownerPid);
					var ownerName = string.Empty;
					var title = string.Empty;

					if (includeTextMetadata || includeOwnerName)
						_ = TryGetString(dictRef, kOwnerName, out ownerName);

					if (includeTextMetadata)
						_ = TryGetString(dictRef, kWindowName, out title);

					var rect = Rectangle.Empty;
					if (TryGetDictionaryValue(dictRef, kWindowBounds, out var boundsRef)
						&& CGRectMakeWithDictionaryRepresentation(boundsRef, out var cgRect))
					{
						rect = new Rectangle(
							Convert.ToInt32(cgRect.X),
							Convert.ToInt32(cgRect.Y),
							Convert.ToInt32(cgRect.Width),
							Convert.ToInt32(cgRect.Height));
					}

					var hasAlpha = TryGetDouble(dictRef, kWindowAlpha, out var alpha);
					var hasIsOnScreen = TryGetBool(dictRef, kWindowIsOnscreen, out var isOnscreen);
					var effectiveAlpha = hasAlpha ? alpha : 1.0;
					var effectiveOnScreen = hasIsOnScreen ? isOnscreen : onScreenOnly;
					list.Add(new MacNativeWindowInfo(windowNumber, ownerPid, ownerName, title, rect, effectiveOnScreen, effectiveAlpha));
				}

				return list;
			}
			finally
			{
				CFRelease(arrayRef);
			}
		}

		private static bool TryGetDictionaryValue(nint dictRef, nint key, out nint value)
		{
			value = 0;
			return dictRef != 0 && key != 0 && CFDictionaryGetValueIfPresent(dictRef, key, out value) && value != 0;
		}

		private static bool TryGetInt32(nint dictRef, nint key, out int value)
		{
			value = 0;
			if (!TryGetDictionaryValue(dictRef, key, out var numRef))
				return false;
			if (CFGetTypeID(numRef) != CFNumberGetTypeID())
				return false;
			return CFNumberGetValue(numRef, kCFNumberSInt32Type, out value);
		}

		private static bool TryGetUInt32(nint dictRef, nint key, out uint value)
		{
			value = 0;
			if (!TryGetInt32(dictRef, key, out var temp))
				return false;
			value = unchecked((uint)temp);
			return true;
		}

		private static bool TryGetDouble(nint dictRef, nint key, out double value)
		{
			value = 0.0;
			if (!TryGetDictionaryValue(dictRef, key, out var numRef))
				return false;
			if (CFGetTypeID(numRef) != CFNumberGetTypeID())
				return false;
			return CFNumberGetValue(numRef, kCFNumberDoubleType, out value);
		}

		private static bool TryGetBool(nint dictRef, nint key, out bool value)
		{
			value = false;
			if (!TryGetDictionaryValue(dictRef, key, out var boolRef))
				return false;
			if (CFGetTypeID(boolRef) != CFBooleanGetTypeID())
				return false;
			value = CFBooleanGetValue(boolRef);
			return true;
		}

		private static bool TryGetString(nint dictRef, nint key, out string value)
		{
			value = string.Empty;
			if (!TryGetDictionaryValue(dictRef, key, out var stringRef))
				return false;
			if (CFGetTypeID(stringRef) != CFStringGetTypeID())
				return false;

			var len = CFStringGetLength(stringRef);
			var maxSize = CFStringGetMaximumSizeForEncoding(len, kCFStringEncodingUTF8) + 1;
			var buffer = new byte[(int)maxSize];

			if (!CFStringGetCString(stringRef, buffer, maxSize, kCFStringEncodingUTF8))
				return false;

			var terminator = System.Array.IndexOf(buffer, (byte)0);
			if (terminator < 0)
				terminator = buffer.Length;

			value = System.Text.Encoding.UTF8.GetString(buffer, 0, terminator);
			return true;
		}

		private static nint CreateCFString(string value)
		{
			try
			{
				return CFStringCreateWithCString(0, value, kCFStringEncodingUTF8);
			}
			catch
			{
				return 0;
			}
		}
	}
}
#endif
