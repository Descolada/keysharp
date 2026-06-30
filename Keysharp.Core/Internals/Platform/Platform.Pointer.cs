#if OSX
using MonoMac.AppKit;
#endif

namespace Keysharp.Internals
{
	internal static partial class Platform
	{
		/// <summary>Cursor position (delegates to the resolved <see cref="Mouse"/> service) + cursor-shape
		/// get/set. The shape map is compile-time per-OS.</summary>
		internal static class Pointer
		{
			public static bool GetCursorPos(out Keysharp.Internals.Window.POINT lpPoint)
			{
				if (Mouse.TryGetCursorPos(out var x, out var y))
				{
					lpPoint = new Keysharp.Internals.Window.POINT(x, y);
					return true;
				}

				lpPoint = default;
				return false;
			}

#if WINDOWS
			// Names not present here (e.g. Icon, Size) have no direct WinForms equivalent → reported as Unknown.
			private static readonly Dictionary<string, Cursor> cursorMap = new (StringComparer.OrdinalIgnoreCase)
			{
				{ "AppStarting", Cursors.AppStarting },
				{ "Arrow", Cursors.Arrow },
				{ "Cross", Cursors.Cross },
				{ "Help", Cursors.Help },
				{ "IBeam", Cursors.IBeam },
				{ "No", Cursors.No },
				{ "SizeAll", Cursors.SizeAll },
				{ "SizeNESW", Cursors.SizeNESW },
				{ "SizeNS", Cursors.SizeNS },
				{ "SizeNWSE", Cursors.SizeNWSE },
				{ "SizeWE", Cursors.SizeWE },
				{ "UpArrow", Cursors.UpArrow },
				{ "Wait", Cursors.WaitCursor }
			};

			public static string GetCursor()
			{
				var current = Cursor.Current;

				foreach (var (name, cursor) in cursorMap)
					if (current == cursor)
						return name;

				return "Unknown";
			}

			public static void SetCursor(string cursorName)
			{
				if (cursorMap.TryGetValue(cursorName, out var cursor))
					Cursor.Current = cursor;
			}
#elif OSX
			private static readonly Dictionary<string, Func<NSCursor>> cursorMap = new (StringComparer.OrdinalIgnoreCase)
			{
				{ "Arrow", () => NSCursor.ArrowCursor },
				{ "IBeam", () => NSCursor.IBeamCursor },
				{ "Cross", () => NSCursor.CrosshairCursor },
				{ "No", () => NSCursor.OperationNotAllowedCursor },
				{ "SizeWE", () => NSCursor.ResizeLeftRightCursor },
				{ "SizeNS", () => NSCursor.ResizeUpDownCursor }
			};

			public static string GetCursor()
			{
				var current = NSCursor.CurrentSystemCursor;

				if (current != null)
					foreach (var (name, cursor) in cursorMap)
						if (current.Handle == cursor().Handle)
							return name;

				return "Unknown";
			}

			public static void SetCursor(string cursorName)
			{
				if (cursorMap.TryGetValue(cursorName, out var cursor))
					cursor().Set();
			}
#else
			public static string GetCursor() => "Unknown";

			public static void SetCursor(string cursorName)
			{
			}
#endif
		}
	}
}
