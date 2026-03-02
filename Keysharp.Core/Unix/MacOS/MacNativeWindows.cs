#if OSX
using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace Keysharp.Core.MacOS
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

		internal bool Visible => IsOnScreen && Alpha > 0.001 && Bounds.Width > 0 && Bounds.Height > 0;
	}

	internal static class MacNativeWindows
	{
		private const uint kCGWindowListOptionAll = 0u;
		private const uint kCGWindowListOptionOnScreenOnly = 1u;
		private const uint kCGWindowListExcludeDesktopElements = 16u;

		private static readonly NSString kWindowNumber = new("kCGWindowNumber");
		private static readonly NSString kOwnerPid = new("kCGWindowOwnerPID");
		private static readonly NSString kOwnerName = new("kCGWindowOwnerName");
		private static readonly NSString kWindowName = new("kCGWindowName");
		private static readonly NSString kWindowBounds = new("kCGWindowBounds");
		private static readonly NSString kWindowLayer = new("kCGWindowLayer");
		private static readonly NSString kWindowAlpha = new("kCGWindowAlpha");
		private static readonly NSString kWindowIsOnscreen = new("kCGWindowIsOnscreen");

		[LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
		private static partial nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

		[LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CGRectMakeWithDictionaryRepresentation(nint dict, out CGRect rect);

		internal static List<MacNativeWindowInfo> Snapshot(bool onScreenOnly = false)
		{
			var list = new List<MacNativeWindowInfo>(256);
			var opts = (onScreenOnly ? kCGWindowListOptionOnScreenOnly : kCGWindowListOptionAll) | kCGWindowListExcludeDesktopElements;
			var cfArray = CGWindowListCopyWindowInfo(opts, 0);

			if (cfArray == 0)
				return list;

			using var windows = Runtime.GetINativeObject<NSArray>(cfArray, true);
			if (windows == null)
				return list;

			for (nuint i = 0; i < windows.Count; i++)
			{
				var dictObj = Runtime.GetNSObject(windows.ValueAt(i));
				if (dictObj is not NSDictionary dict)
					continue;

				if (dict[kWindowNumber] is not NSNumber numberObj)
					continue;

				var windowNumber = numberObj.UInt32Value;
				var ownerPid = (dict[kOwnerPid] as NSNumber)?.Int32Value ?? 0;
				var ownerName = (dict[kOwnerName] as NSString)?.ToString() ?? string.Empty;
				var title = (dict[kWindowName] as NSString)?.ToString() ?? string.Empty;
				var layer = (dict[kWindowLayer] as NSNumber)?.Int32Value ?? 0;
				var alpha = (dict[kWindowAlpha] as NSNumber)?.DoubleValue ?? 1.0;
				var isOnscreen = (dict[kWindowIsOnscreen] as NSNumber)?.BoolValue ?? false;

				var rect = Rectangle.Empty;
				if (dict[kWindowBounds] is NSDictionary boundsDict && CGRectMakeWithDictionaryRepresentation(boundsDict.Handle, out var cgRect))
					rect = new Rectangle((int)cgRect.X, (int)cgRect.Y, (int)cgRect.Width, (int)cgRect.Height);

				// Layer 0 is regular app windows; keep others out for WinTitle matching parity.
				if (layer != 0)
					continue;

				list.Add(new MacNativeWindowInfo(windowNumber, ownerPid, ownerName, title, rect, isOnscreen, alpha));
			}

			return list;
		}

		internal static bool TryGetWindowInfo(nint handle, out MacNativeWindowInfo info)
		{
			var id = unchecked((uint)handle.ToInt64());
			var snapshot = Snapshot();

			for (int i = 0; i < snapshot.Count; i++)
			{
				if (snapshot[i].WindowNumber == id)
				{
					info = snapshot[i];
					return true;
				}
			}

			info = default;
			return false;
		}

		internal static bool TryGetWindowAtPoint(POINT location, out MacNativeWindowInfo info)
		{
			var snapshot = Snapshot(onScreenOnly: true);
			for (int i = 0; i < snapshot.Count; i++)
			{
				var w = snapshot[i];
				if (!w.Visible)
					continue;

				if (location.X >= w.Bounds.Left && location.X < w.Bounds.Right
					&& location.Y >= w.Bounds.Top && location.Y < w.Bounds.Bottom)
				{
					info = w;
					return true;
				}
			}

			info = default;
			return false;
		}

		internal static nint GetFrontWindowHandle()
		{
			var snapshot = Snapshot(onScreenOnly: true);
			for (int i = 0; i < snapshot.Count; i++)
			{
				if (snapshot[i].Visible)
					return (nint)snapshot[i].WindowNumber;
			}

			return 0;
		}

		internal static bool ActivateAppByPid(int pid)
		{
			if (pid <= 0)
				return false;

			var apps = NSWorkspace.SharedWorkspace.RunningApplications;
			foreach (var app in apps)
			{
				if (app is NSRunningApplication running && running.ProcessIdentifier == pid)
				{
					_ = running.Activate(NSApplicationActivationOptions.ActivateAllWindows | NSApplicationActivationOptions.ActivateIgnoringOtherApps);
					return true;
				}
			}

			return false;
		}
	}
}
#endif
