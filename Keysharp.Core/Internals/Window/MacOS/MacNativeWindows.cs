using Keysharp.Builtins;
#if OSX
using System.Runtime.InteropServices;
using MonoMac.AppKit;

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

		internal bool Visible => IsOnScreen && Alpha > 0.001 && Bounds.Width > 0 && Bounds.Height > 0;
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

		internal static List<MacNativeWindowInfo> SnapshotBasic(bool onScreenOnly = false) => SnapshotCore(onScreenOnly, includeTextMetadata: false);

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
			var snapshot = SnapshotBasic(onScreenOnly: true);

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
			var snapshot = SnapshotBasic(onScreenOnly: true);
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

		private static List<MacNativeWindowInfo> SnapshotCore(bool onScreenOnly, bool includeTextMetadata, bool includeSingleWindow = false, uint relativeToWindow = 0)
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

					if (!TryGetInt32(dictRef, kWindowLayer, out var layer) || layer != 0)
						continue;

					_ = TryGetInt32(dictRef, kOwnerPid, out var ownerPid);
					var ownerName = string.Empty;
					var title = string.Empty;

					if (includeTextMetadata)
					{
						_ = TryGetString(dictRef, kOwnerName, out ownerName);
						_ = TryGetString(dictRef, kWindowName, out title);
					}

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
