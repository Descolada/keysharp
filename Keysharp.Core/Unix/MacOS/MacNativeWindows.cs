#if OSX
using System.Runtime.InteropServices;

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
		private const int kCFNumberSInt32Type = 3;
		private const int kCFNumberDoubleType = 13;
		private const uint kCGWindowListOptionAll = 0u;
		private const uint kCGWindowListOptionOnScreenOnly = 1u;
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

		internal static List<MacNativeWindowInfo> Snapshot(bool onScreenOnly = false)
		{
			var list = new List<MacNativeWindowInfo>(256);
			var opts = (onScreenOnly ? kCGWindowListOptionOnScreenOnly : kCGWindowListOptionAll) | kCGWindowListExcludeDesktopElements;
			var arrayRef = CGWindowListCopyWindowInfo(opts, 0);

			if (arrayRef == 0)
				return list;

			try
			{
				var count = CFArrayGetCount(arrayRef);

				for (nint i = 0; i < count; i++)
				{
					var dictRef = CFArrayGetValueAtIndex(arrayRef, i);

					if (!TryGetUInt32(dictRef, kWindowNumber, out var windowNumber))
						continue;

					_ = TryGetInt32(dictRef, kOwnerPid, out var ownerPid);
					_ = TryGetString(dictRef, kOwnerName, out var ownerName);
					_ = TryGetString(dictRef, kWindowName, out var title);
					_ = TryGetInt32(dictRef, kWindowLayer, out var layer);
					_ = TryGetDouble(dictRef, kWindowAlpha, out var alpha);
					_ = TryGetBool(dictRef, kWindowIsOnscreen, out var isOnscreen);

					if (layer != 0)
						continue;

					var rect = Rectangle.Empty;
					if (TryGetDictionaryValue(dictRef, kWindowBounds, out var boundsRef)
						&& CGRectMakeWithDictionaryRepresentation(boundsRef, out var cgRect))
						rect = new Rectangle((int)cgRect.X, (int)cgRect.Y, (int)cgRect.Width, (int)cgRect.Height);

					list.Add(new MacNativeWindowInfo(windowNumber, ownerPid, ownerName, title, rect, isOnscreen, alpha == 0 ? 1.0 : alpha));
				}
			}
			finally
			{
				CFRelease(arrayRef);
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
			_ = pid;
			// Activation fallback intentionally omitted when AppKit is unavailable.
			return false;
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
