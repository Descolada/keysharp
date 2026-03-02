#if OSX
using Foundation;
using ObjCRuntime;

namespace Keysharp.Core.MacOS
{
	internal static class MacAccessibility
	{
		private const int kAXErrorSuccess = 0;
		private const int kAXValueCGPointType = 1;
		private const int kAXValueCGSizeType = 2;
		private const uint kCGHIDEventTap = 0;

		private enum CGEventType : uint
		{
			LeftMouseDown = 1,
			LeftMouseUp = 2,
			RightMouseDown = 3,
			RightMouseUp = 4
		}

		private enum CGMouseButton : uint
		{
			Left = 0,
			Right = 1
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct CGPointD
		{
			public double X;
			public double Y;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct CGSizeD
		{
			public double Width;
			public double Height;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct CGPointNative
		{
			public double X;
			public double Y;

			public CGPointNative(double x, double y)
			{
				X = x;
				Y = y;
			}
		}

		private static readonly NSString attrWindows = new("AXWindows");
		private static readonly NSString attrPosition = new("AXPosition");
		private static readonly NSString attrSize = new("AXSize");
		private static readonly NSString attrTitle = new("AXTitle");
		private static readonly NSString attrMinimized = new("AXMinimized");
		private static readonly NSString attrZoomed = new("AXZoomed");
		private static readonly NSString attrCloseButton = new("AXCloseButton");
		private static readonly NSString trustedPromptOptionKey = new("AXTrustedCheckOptionPrompt");

		private static readonly NSString actionRaise = new("AXRaise");
		private static readonly NSString actionClose = new("AXClose");
		private static readonly NSString actionPress = new("AXPress");

		private static int loggedTrustFailure;
		private static int promptedTrust;
		private static int loggedListenFailure;
		private static int loggedPostFailure;
		private static int loggedScreenFailure;
		private static int promptedListen;
		private static int promptedPost;
		private static int promptedScreen;

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		private static partial nint AXUIElementCreateApplication(int pid);

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		private static partial int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		private static partial int AXUIElementSetAttributeValue(nint element, nint attribute, nint value);

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		private static partial int AXUIElementPerformAction(nint element, nint action);

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		private static partial int AXUIElementCopyElementAtPosition(nint application, float x, float y, out nint element);

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool AXIsProcessTrustedWithOptions(nint options);

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CGPreflightListenEventAccess();

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CGRequestListenEventAccess();

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CGPreflightPostEventAccess();

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CGRequestPostEventAccess();

		[LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CGPreflightScreenCaptureAccess();

		[LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool CGRequestScreenCaptureAccess();

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool AXValueGetValue(nint value, int theType, out CGPointD point);

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		[return: MarshalAs(UnmanagedType.I1)]
		private static partial bool AXValueGetValue(nint value, int theType, out CGSizeD size);

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices", EntryPoint = "AXValueCreate")]
		private static partial nint AXValueCreatePoint(int theType, in CGPointD point);

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices", EntryPoint = "AXValueCreate")]
		private static partial nint AXValueCreateSize(int theType, in CGSizeD size);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		private static partial void CFRelease(nint cfTypeRef);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		private static partial nint CFRetain(nint cfTypeRef);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		private static partial nint CFArrayGetCount(nint theArray);

		[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
		private static partial nint CFArrayGetValueAtIndex(nint theArray, nint idx);

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		private static partial nint CGEventCreateMouseEvent(nint source, CGEventType mouseType, CGPointNative mouseCursorPosition, CGMouseButton mouseButton);

		[LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
		private static partial void CGEventPost(uint tap, nint @event);

		internal static bool TryActivateWindow(MacNativeWindowInfo info)
		{
			if (!EnsureAccessibilityAccess("activate window", prompt: true))
				return MacNativeWindows.ActivateAppByPid(info.OwnerPid);

			if (!TryFindWindowElement(info, out var windowElement))
				return MacNativeWindows.ActivateAppByPid(info.OwnerPid);

			var ok = AXUIElementPerformAction(windowElement, actionRaise.Handle) == kAXErrorSuccess;
			CFRelease(windowElement);
			return ok || MacNativeWindows.ActivateAppByPid(info.OwnerPid);
		}

		internal static bool TryCloseWindow(MacNativeWindowInfo info)
		{
			if (!EnsureAccessibilityAccess("close window", prompt: true))
				return false;

			if (!TryFindWindowElement(info, out var windowElement))
				return false;

			try
			{
				if (AXUIElementPerformAction(windowElement, actionClose.Handle) == kAXErrorSuccess)
					return true;

				if (TryCopyAttributeValue(windowElement, attrCloseButton.Handle, out var closeButton))
				{
					try
					{
						return AXUIElementPerformAction(closeButton, actionPress.Handle) == kAXErrorSuccess;
					}
					finally
					{
						CFRelease(closeButton);
					}
				}
			}
			finally
			{
				CFRelease(windowElement);
			}

			return false;
		}

		internal static bool TryGetWindowState(MacNativeWindowInfo info, out FormWindowState state)
		{
			state = FormWindowState.Normal;
			if (!EnsureAccessibilityAccess("query window state"))
				return false;

			if (!TryFindWindowElement(info, out var windowElement))
				return false;

			try
			{
				if (TryReadBool(windowElement, attrMinimized.Handle, out var minimized) && minimized)
				{
					state = FormWindowState.Minimized;
					return true;
				}

				if (TryReadBool(windowElement, attrZoomed.Handle, out var zoomed) && zoomed)
				{
					state = FormWindowState.Maximized;
					return true;
				}

				state = FormWindowState.Normal;
				return true;
			}
			finally
			{
				CFRelease(windowElement);
			}
		}

		internal static bool TrySetWindowState(MacNativeWindowInfo info, FormWindowState state)
		{
			if (!EnsureAccessibilityAccess("set window state", prompt: true))
				return false;

			if (!TryFindWindowElement(info, out var windowElement))
				return false;

			try
			{
				switch (state)
				{
					case FormWindowState.Minimized:
						return TryWriteBool(windowElement, attrMinimized.Handle, true);

					case FormWindowState.Maximized:
					{
						var a = TryWriteBool(windowElement, attrMinimized.Handle, false);
						var b = TryWriteBool(windowElement, attrZoomed.Handle, true);
						return a || b;
					}

					default:
					{
						var a = TryWriteBool(windowElement, attrMinimized.Handle, false);
						var b = TryWriteBool(windowElement, attrZoomed.Handle, false);
						_ = AXUIElementPerformAction(windowElement, actionRaise.Handle);
						return a || b;
					}
				}
			}
			finally
			{
				CFRelease(windowElement);
			}
		}

		internal static bool TryMoveResizeWindow(MacNativeWindowInfo info, Rectangle rect, bool setPosition, bool setSize)
		{
			if (!EnsureAccessibilityAccess("move/resize window", prompt: true))
				return false;

			if (!TryFindWindowElement(info, out var windowElement))
				return false;

			try
			{
				var ok = true;

				if (setPosition)
				{
					var point = new CGPointD { X = rect.X, Y = rect.Y };
					var posValue = AXValueCreatePoint(kAXValueCGPointType, in point);
					if (posValue != 0)
					{
						ok &= AXUIElementSetAttributeValue(windowElement, attrPosition.Handle, posValue) == kAXErrorSuccess;
						CFRelease(posValue);
					}
					else
					{
						ok = false;
					}
				}

				if (setSize && rect.Width > 0 && rect.Height > 0)
				{
					var size = new CGSizeD { Width = rect.Width, Height = rect.Height };
					var sizeValue = AXValueCreateSize(kAXValueCGSizeType, in size);
					if (sizeValue != 0)
					{
						ok &= AXUIElementSetAttributeValue(windowElement, attrSize.Handle, sizeValue) == kAXErrorSuccess;
						CFRelease(sizeValue);
					}
					else
					{
						ok = false;
					}
				}

				return ok;
			}
			finally
			{
				CFRelease(windowElement);
			}
		}

		internal static bool TryGetElementBoundsAtPoint(MacNativeWindowInfo info, int x, int y, out Rectangle rect)
		{
			rect = Rectangle.Empty;
			if (!EnsureAccessibilityAccess("query element at point"))
				return false;

			var appElement = AXUIElementCreateApplication(info.OwnerPid);
			if (appElement == 0)
				return false;

			try
			{
				if (AXUIElementCopyElementAtPosition(appElement, x, y, out var element) != kAXErrorSuccess || element == 0)
					return false;

				try
				{
					return TryReadRect(element, out rect);
				}
				finally
				{
					CFRelease(element);
				}
			}
			finally
			{
				CFRelease(appElement);
			}
		}

		internal static bool TryClickWindow(MacNativeWindowInfo info, Point? location, bool rightButton)
		{
			if (!EnsureAccessibilityAccess("post mouse click", prompt: true))
				return false;
			if (!EnsurePostEventAccess("post mouse click", prompt: true))
				return false;

			_ = TryActivateWindow(info);

			var clickX = location?.X ?? (info.Bounds.Width / 2);
			var clickY = location?.Y ?? (info.Bounds.Height / 2);
			var absX = info.Bounds.X + clickX;
			var absY = info.Bounds.Y + clickY;
			var point = new CGPointNative(absX, absY);

			var button = rightButton ? CGMouseButton.Right : CGMouseButton.Left;
			var downType = rightButton ? CGEventType.RightMouseDown : CGEventType.LeftMouseDown;
			var upType = rightButton ? CGEventType.RightMouseUp : CGEventType.LeftMouseUp;

			var down = CGEventCreateMouseEvent(0, downType, point, button);
			if (down == 0)
				return false;

			try
			{
				CGEventPost(kCGHIDEventTap, down);
			}
			finally
			{
				CFRelease(down);
			}

			var up = CGEventCreateMouseEvent(0, upType, point, button);
			if (up == 0)
				return false;

			try
			{
				CGEventPost(kCGHIDEventTap, up);
			}
			finally
			{
				CFRelease(up);
			}

			return true;
		}

		internal static bool EnsureAccessibilityAccess(string operation, bool prompt = false)
		{
			if (AXIsProcessTrustedWithOptions(0))
				return true;

			if (prompt && Interlocked.Exchange(ref promptedTrust, 1) == 0)
			{
				try
				{
					using var dict = NSDictionary.FromObjectAndKey(NSNumber.FromBoolean(true), trustedPromptOptionKey);
					_ = AXIsProcessTrustedWithOptions(dict.Handle);
				}
				catch
				{
				}
			}

			if (Interlocked.Exchange(ref loggedTrustFailure, 1) == 0)
			{
				Ks.OutputDebugLine(
					$"macOS Accessibility permission is required for '{operation}'. " +
					"Grant access in System Settings -> Privacy & Security -> Accessibility, then restart the app.");
			}

			return false;
		}

		internal static bool EnsureInputMonitoringAccess(string operation, bool prompt = false)
		{
			if (CheckListenAccess())
				return true;

			if (prompt && Interlocked.Exchange(ref promptedListen, 1) == 0)
			{
				try
				{
					_ = CGRequestListenEventAccess();
				}
				catch (EntryPointNotFoundException)
				{
					return true;
				}
				catch
				{
				}
			}

			if (Interlocked.Exchange(ref loggedListenFailure, 1) == 0)
			{
				Ks.OutputDebugLine(
					$"macOS Input Monitoring permission is required for '{operation}'. " +
					"Grant access in System Settings -> Privacy & Security -> Input Monitoring, then restart the app.");
			}

			return false;
		}

		internal static bool EnsurePostEventAccess(string operation, bool prompt = false)
		{
			if (CheckPostAccess())
				return true;

			if (prompt && Interlocked.Exchange(ref promptedPost, 1) == 0)
			{
				try
				{
					_ = CGRequestPostEventAccess();
				}
				catch (EntryPointNotFoundException)
				{
					// Older macOS: this API may be unavailable; Accessibility trust is authoritative there.
					return AXIsProcessTrustedWithOptions(0);
				}
				catch
				{
				}
			}

			if (Interlocked.Exchange(ref loggedPostFailure, 1) == 0)
			{
				Ks.OutputDebugLine(
					$"macOS synthetic input permission is required for '{operation}'. " +
					"Grant access in System Settings -> Privacy & Security -> Accessibility, then restart the app.");
			}

			return false;
		}

		internal static bool EnsureScreenCaptureAccess(string operation, bool prompt = false)
		{
			if (CheckScreenCaptureAccess())
				return true;

			if (prompt && Interlocked.Exchange(ref promptedScreen, 1) == 0)
			{
				try
				{
					_ = CGRequestScreenCaptureAccess();
				}
				catch (EntryPointNotFoundException)
				{
					// Older macOS: no dedicated API; do not block here.
					return true;
				}
				catch
				{
				}
			}

			if (Interlocked.Exchange(ref loggedScreenFailure, 1) == 0)
			{
				Ks.OutputDebugLine(
					$"macOS Screen Recording permission is required for '{operation}'. " +
					"Grant access in System Settings -> Privacy & Security -> Screen Recording, then restart the app.");
			}

			return false;
		}

		private static bool CheckListenAccess()
		{
			try
			{
				return CGPreflightListenEventAccess();
			}
			catch (EntryPointNotFoundException)
			{
				// Older macOS: treat Accessibility trust as the closest equivalent.
				return AXIsProcessTrustedWithOptions(0);
			}
			catch
			{
				return false;
			}
		}

		private static bool CheckPostAccess()
		{
			try
			{
				return CGPreflightPostEventAccess();
			}
			catch (EntryPointNotFoundException)
			{
				return AXIsProcessTrustedWithOptions(0);
			}
			catch
			{
				return false;
			}
		}

		private static bool CheckScreenCaptureAccess()
		{
			try
			{
				return CGPreflightScreenCaptureAccess();
			}
			catch (EntryPointNotFoundException)
			{
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static bool TryFindWindowElement(MacNativeWindowInfo info, out nint windowElement)
		{
			windowElement = 0;
			var appElement = AXUIElementCreateApplication(info.OwnerPid);
			if (appElement == 0)
				return false;

			try
			{
				if (!TryCopyAttributeValue(appElement, attrWindows.Handle, out var windowsArray))
					return false;

				try
				{
					var count = CFArrayGetCount(windowsArray);
					if (count <= 0)
						return false;

					nint best = 0;
					double bestScore = double.NegativeInfinity;

					for (nint i = 0; i < count; i++)
					{
						var entry = CFArrayGetValueAtIndex(windowsArray, i);
						if (entry == 0)
							continue;

						var candidate = CFRetain(entry);
						if (candidate == 0)
							continue;

						var score = ScoreWindowElement(candidate, info);
						if (score > bestScore)
						{
							if (best != 0)
								CFRelease(best);

							best = candidate;
							bestScore = score;
						}
						else
						{
							CFRelease(candidate);
						}
					}

					if (best != 0)
					{
						windowElement = best;
						return true;
					}
				}
				finally
				{
					CFRelease(windowsArray);
				}
			}
			finally
			{
				CFRelease(appElement);
			}

			return false;
		}

		private static double ScoreWindowElement(nint windowElement, MacNativeWindowInfo target)
		{
			double score = 0.0;

			if (TryReadString(windowElement, attrTitle.Handle, out var title))
			{
				if (!title.IsNullOrEmpty())
				{
					if (string.Equals(title, target.Title, StringComparison.Ordinal))
						score += 1000.0;
					else if (!target.Title.IsNullOrEmpty() && title.Contains(target.Title, StringComparison.Ordinal))
						score += 500.0;
				}
				else if (target.Title.IsNullOrEmpty())
				{
					score += 200.0;
				}
			}

			if (TryReadRect(windowElement, out var rect))
			{
				var dx = rect.X - target.Bounds.X;
				var dy = rect.Y - target.Bounds.Y;
				var dw = rect.Width - target.Bounds.Width;
				var dh = rect.Height - target.Bounds.Height;
				var distance = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dw) + Math.Abs(dh);
				score += Math.Max(0.0, 400.0 - distance);
			}

			return score;
		}

		private static bool TryReadRect(nint windowElement, out Rectangle rect)
		{
			rect = Rectangle.Empty;
			if (!TryReadPoint(windowElement, attrPosition.Handle, out var x, out var y))
				return false;

			if (!TryReadSize(windowElement, attrSize.Handle, out var w, out var h))
				return false;

			rect = new Rectangle((int)x, (int)y, (int)w, (int)h);
			return true;
		}

		private static bool TryReadPoint(nint element, nint attr, out double x, out double y)
		{
			x = 0;
			y = 0;
			if (!TryCopyAttributeValue(element, attr, out var value))
				return false;

			try
			{
				if (!AXValueGetValue(value, kAXValueCGPointType, out CGPointD p))
					return false;

				x = p.X;
				y = p.Y;
				return true;
			}
			finally
			{
				CFRelease(value);
			}
		}

		private static bool TryReadSize(nint element, nint attr, out double width, out double height)
		{
			width = 0;
			height = 0;
			if (!TryCopyAttributeValue(element, attr, out var value))
				return false;

			try
			{
				if (!AXValueGetValue(value, kAXValueCGSizeType, out CGSizeD s))
					return false;

				width = s.Width;
				height = s.Height;
				return true;
			}
			finally
			{
				CFRelease(value);
			}
		}

		private static bool TryReadString(nint element, nint attr, out string value)
		{
			value = string.Empty;
			if (!TryCopyAttributeValue(element, attr, out var obj))
				return false;

			try
			{
				var ns = Runtime.GetINativeObject<NSString>(obj, false);
				if (ns == null)
					return false;

				value = ns.ToString();
				return true;
			}
			finally
			{
				CFRelease(obj);
			}
		}

		private static bool TryReadBool(nint element, nint attr, out bool value)
		{
			value = false;
			if (!TryCopyAttributeValue(element, attr, out var obj))
				return false;

			try
			{
				var num = Runtime.GetINativeObject<NSNumber>(obj, false);
				if (num == null)
					return false;

				value = num.BoolValue;
				return true;
			}
			finally
			{
				CFRelease(obj);
			}
		}

		private static bool TryWriteBool(nint element, nint attr, bool value)
		{
			using var num = NSNumber.FromBoolean(value);
			return AXUIElementSetAttributeValue(element, attr, num.Handle) == kAXErrorSuccess;
		}

		private static bool TryCopyAttributeValue(nint element, nint attr, out nint value)
		{
			value = 0;
			return AXUIElementCopyAttributeValue(element, attr, out value) == kAXErrorSuccess && value != 0;
		}
	}
}
#endif
