using Keysharp.Internals;

namespace Keysharp.Builtins
{
	/// <summary>Public interface for monitor-related functions.</summary>
	public static class Monitor
	{
		// Platform.Screen is the only monitor-topology source. It returns a fresh snapshot because monitor
		// identity, bounds, work areas and scale can all change while a script is running.
		private static DisplayInfo[] AllDisplays => Platform.Screen.GetDisplays().ToArray();

		private static DisplayInfo ResolvePrimaryDisplay(DisplayInfo[] displays)
		{
			if (displays.Length == 0)
				throw new InvalidOperationException("No monitors are available.");

			foreach (var display in displays)
				if (display.IsPrimary)
					return display;

			// GDK/Wayland does not always expose a designated primary output. Compositors conventionally put it
			// at the logical origin; the first output is the final deterministic fallback.
			foreach (var display in displays)
				if (display.Bounds.X == 0 && display.Bounds.Y == 0)
					return display;

			return displays[0];
		}

		internal static (DisplayInfo Display, long MonitorIndex) ResolveDisplay(object n)
		{
			var monitorIndex = n.Al(-1L);
			var displays = AllDisplays;

			if (monitorIndex > 0 && monitorIndex <= displays.Length)
				return (displays[monitorIndex - 1], monitorIndex);

			var primary = ResolvePrimaryDisplay(displays);

			for (var i = 0; i < displays.Length; i++)
				if (displays[i].Equals(primary))
					return (primary, i + 1L);

			return (primary, 1L);
		}

		internal static (DisplayInfo Display, long MonitorIndex) ResolveDisplayForPoint(int x, int y)
		{
			var displays = AllDisplays;

			if (!DisplayTopology.TryFind(displays, new ScreenRect(x, y, 0, 0), out var selected))
				throw new InvalidOperationException("No monitors are available.");

			for (var i = 0; i < displays.Length; i++)
				if (displays[i].Equals(selected))
					return (selected, i + 1L);

			return (selected, 1L);
		}

		internal static (long Width, long Height) GetPrimaryScreenSize()
		{
			var (display, _) = ResolveDisplay(null);
			return (display.Bounds.Width, display.Bounds.Height);
		}

		/// <summary>Returns one display's bounds in native screen coordinates.</summary>
		internal static (long Left, long Top, long Width, long Height) GetMonitorBounds(object n)
		{
			var (display, _) = ResolveDisplay(n);
			var bounds = display.Bounds;
			return (bounds.X, bounds.Y, bounds.Width, bounds.Height);
		}

		internal static (long Width, long Height) GetPrimaryWorkAreaSize()
		{
			var wa = GetPrimaryWorkArea();
			return (wa.Width, wa.Height);
		}

		/// <summary>The primary display's working area in native screen coordinates.</summary>
		internal static Rectangle GetPrimaryWorkArea()
		{
			var (display, _) = ResolveDisplay(null);
			return display.WorkArea.ToRectangle();
		}

		internal static (long Width, long Height) GetVirtualScreenSize()
		{
			var (_, _, width, height) = GetVirtualScreenBounds();
			return (width, height);
		}

		/// <summary>
		/// Returns the union of all displays, including its possibly-negative origin, in native screen coordinates.
		/// </summary>
		internal static (long Left, long Top, long Width, long Height) GetVirtualScreenBounds()
		{
			var displays = AllDisplays;

			if (displays.Length == 0)
				return (0L, 0L, 0L, 0L);

			var left = displays.Min(s => s.Bounds.X);
			var top = displays.Min(s => s.Bounds.Y);
			var right = displays.Max(s => s.Bounds.Right);
			var bottom = displays.Max(s => s.Bounds.Bottom);
			return (left, top, right - left, bottom - top);
		}

		/// <summary>Gets one monitor's native screen-coordinate bounds.</summary>
		public static object MonitorGet(object n = null, [ByRef] object left = null, [ByRef] object top = null,
			[ByRef] object right = null, [ByRef] object bottom = null)
		{
			var (display, monitorIndex) = ResolveDisplay(n);
			var bounds = display.Bounds;

			if (left != null) Script.SetPropertyValue(left, "__Value", (long)bounds.X);
			if (top != null) Script.SetPropertyValue(top, "__Value", (long)bounds.Y);
			if (right != null) Script.SetPropertyValue(right, "__Value", (long)bounds.Right);
			if (bottom != null) Script.SetPropertyValue(bottom, "__Value", (long)bounds.Bottom);
			return monitorIndex;
		}

		/// <summary>Returns the current number of displays.</summary>
		public static long MonitorGetCount() => AllDisplays.Length;

		/// <summary>Returns the platform's stable name for one display snapshot.</summary>
		public static string MonitorGetName(object n = null)
		{
			var (display, _) = ResolveDisplay(n);
			return display.Name ?? "";
		}

		/// <summary>Returns the current primary monitor index.</summary>
		public static long MonitorGetPrimary()
		{
			var displays = AllDisplays;
			var primary = ResolvePrimaryDisplay(displays);

			for (var i = 0; i < displays.Length; i++)
				if (displays[i].Equals(primary))
					return i + 1L;

			return 1L;
		}

		/// <summary>Gets one monitor's work-area bounds in native screen coordinates.</summary>
		public static object MonitorGetWorkArea(object n = null, [ByRef] object left = null, [ByRef] object top = null,
			[ByRef] object right = null, [ByRef] object bottom = null)
		{
			var (display, monitorIndex) = ResolveDisplay(n);
			var workArea = display.WorkArea;

			if (left != null) Script.SetPropertyValue(left, "__Value", (long)workArea.X);
			if (top != null) Script.SetPropertyValue(top, "__Value", (long)workArea.Y);
			if (right != null) Script.SetPropertyValue(right, "__Value", (long)workArea.Right);
			if (bottom != null) Script.SetPropertyValue(bottom, "__Value", (long)workArea.Bottom);
			return monitorIndex;
		}
	}

	/// <summary>Keysharp monitor extensions which are not part of the AutoHotkey v2 global function set.</summary>
	public partial class Ks
	{
		/// <summary>Returns the monitor containing a native virtual-desktop point, or the nearest monitor when the
		/// point lies in a gap between displays. Import from the KS module.</summary>
		public static long MonitorFromPoint(object x, object y)
			=> Monitor.ResolveDisplayForPoint(x.Ai(), y.Ai()).MonitorIndex;

		/// <summary>Returns one monitor's authored-size scale. 1.0 is 100%, 1.5 is 150%. The value maps
		/// deliberately authored UI sizes into native screen units and must never be applied to absolute positions.</summary>
		public static double MonitorGetScale(object n = null)
			=> ScaleFactor.Normalize(Monitor.ResolveDisplay(n).Display.SizeScale);
	}
}
