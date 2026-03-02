#if OSX
using System.Runtime.InteropServices;
using AppKit;
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

	internal static partial class MacNativeWindows
	{
		private const uint kCFStringEncodingUTF8 = 0x08000100;

		[StructLayout(LayoutKind.Sequential)]
		private struct CGRectNative
		{
			internal double X;
			internal double Y;
			internal double Width;
			internal double Height;
		}

		private const uint kCGWindowListOptionAll = 0u;
		private const uint kCGWindowListOptionOnScreenOnly = 1u;
		private const uint kCGWindowListExcludeDesktopElements = 16u;

		private static readonly nint kWindowNumber = CreateCFString("kCGWindowNumber");
		private static readonly nint kOwnerPid = CreateCFString("kCGWindowOwnerPID");
		private static readonly nint kOwnerName = CreateCFString("kCGWindowOwnerName");
		private static readonly nint kWindowName = CreateCFString("kCGWindowName");
		private static readonly nint kWindowBounds = CreateCFString("kCGWindowBounds");
		private static readonly nint kWindowLayer = CreateCFString("kCGWindowLayer");
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
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CFDictionaryGetValueIfPresent(nint theDict, nint key, out nint value);

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

				if (!TryGetDictionaryValue(dict, kWindowNumber, out var valueObj)
					|| Runtime.GetNSObject(valueObj) is not NSNumber numberObj)
					continue;

				var windowNumber = numberObj.UInt32Value;
				var ownerPid = TryGetDictionaryValue(dict, kOwnerPid, out valueObj) && Runtime.GetNSObject(valueObj) is NSNumber ownerPidNum ? ownerPidNum.Int32Value : 0;
				var ownerName = TryGetDictionaryValue(dict, kOwnerName, out valueObj) ? Runtime.GetNSObject(valueObj)?.ToString() ?? string.Empty : string.Empty;
				var title = TryGetDictionaryValue(dict, kWindowName, out valueObj) ? Runtime.GetNSObject(valueObj)?.ToString() ?? string.Empty : string.Empty;
				var layer = TryGetDictionaryValue(dict, kWindowLayer, out valueObj) && Runtime.GetNSObject(valueObj) is NSNumber layerNum ? layerNum.Int32Value : 0;
				var alpha = TryGetDictionaryValue(dict, kWindowAlpha, out valueObj) && Runtime.GetNSObject(valueObj) is NSNumber alphaNum ? alphaNum.DoubleValue : 1.0;
				var isOnscreen = TryGetDictionaryValue(dict, kWindowIsOnscreen, out valueObj) && Runtime.GetNSObject(valueObj) is NSNumber onscreenNum && onscreenNum.BoolValue;

				var rect = Rectangle.Empty;
				if (TryGetDictionaryValue(dict, kWindowBounds, out valueObj)
					&& Runtime.GetNSObject(valueObj) is NSDictionary boundsDict
					&& CGRectMakeWithDictionaryRepresentation(boundsDict.Handle, out var cgRect))
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

		private static bool TryGetDictionaryValue(NSDictionary dict, nint key, out nint value)
			=> dict != null && key != 0 && CFDictionaryGetValueIfPresent(dict.Handle, key, out value) && value != 0;

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
