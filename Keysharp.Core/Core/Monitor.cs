namespace Keysharp.Core
{
	/// <summary>
	/// Public interface for monitor-related functions.
	/// </summary>
	public static class Monitor
	{

#if WINDOWS
		private static Forms.Screen[] AllScreens => Forms.Screen.AllScreens;
#else
		private static Forms.Screen[] AllScreens => Forms.Screen.Screens?.ToArray() ?? [];
#endif

		private static Forms.Screen ResolvePrimaryScreen(Forms.Screen[] allScreens)
		{
#if WINDOWS
			return allScreens.FirstOrDefault(s => s?.Primary == true)
				   ?? Forms.Screen.PrimaryScreen
				   ?? allScreens.FirstOrDefault()
				   ?? throw new InvalidOperationException("No monitors are available.");
#else
			return allScreens.FirstOrDefault(s => s?.IsPrimary == true)
				   ?? allScreens.FirstOrDefault()
				   ?? throw new InvalidOperationException("No monitors are available.");
#endif
		}

		private static (Forms.Screen Screen, long MonitorIndex) ResolveScreen(object n)
		{
			var monitorIndex = n.Al(-1L);
			var allScreens = AllScreens;

			if (monitorIndex > 0 && monitorIndex <= allScreens.Length)
				return (allScreens[monitorIndex - 1], monitorIndex);

			return (ResolvePrimaryScreen(allScreens), 1L);
		}

		internal static (long Width, long Height) GetPrimaryScreenSize()
		{
			var (screen, _) = ResolveScreen(null);
			return ((long)screen.Bounds.Width, (long)screen.Bounds.Height);
		}

		internal static (long Width, long Height) GetPrimaryWorkAreaSize()
		{
			var (screen, _) = ResolveScreen(null);
			return ((long)screen.WorkingArea.Width, (long)screen.WorkingArea.Height);
		}

		internal static (long Width, long Height) GetVirtualScreenSize()
		{
			var allScreens = AllScreens;

			if (allScreens.Length == 0)
				return (0L, 0L);

			var left = allScreens.Min(s => s.Bounds.Left);
			var top = allScreens.Min(s => s.Bounds.Top);
			var right = allScreens.Max(s => s.Bounds.Right);
			var bottom = allScreens.Max(s => s.Bounds.Bottom);
			return ((long)(right - left), (long)(bottom - top));
		}

		/// <summary>
		/// Checks if the specified monitor exists and optionally retrieves its bounding coordinates.
		/// </summary>
		/// <param name="n">If omitted, the primary monitor will be used. Otherwise, specify the monitor number, between 1 and the number returned by <see cref="MonitorGetCount"/>.</param>
		/// <param name="left">The left bounding coordinate of the specified monitor.</param>
		/// <param name="top">The top bounding coordinate of the specified monitor.</param>
		/// <param name="right">The right bounding coordinate of the specified monitor.</param>
		/// <param name="bottom">The bottom bounding coordinate of the specified monitor.</param>
		/// <returns>The monitor number which is the same as n unless n was omitted.</returns>
		public static object MonitorGet(object n = null, [ByRef] object left = null, [ByRef] object top = null, [ByRef] object right = null, [ByRef] object bottom = null)
		{
			var (screen, monitorIndex) = ResolveScreen(n);

			if (left != null) Script.SetPropertyValue(left, "__Value", (long)screen.Bounds.Left);
			if (top != null) Script.SetPropertyValue(top, "__Value", (long)screen.Bounds.Top);
			if (right != null) Script.SetPropertyValue(right, "__Value", (long)screen.Bounds.Right);
			if (bottom != null) Script.SetPropertyValue(bottom, "__Value", (long)screen.Bounds.Bottom);
			return monitorIndex;
		}

		/// <summary>
		/// Returns the total number of monitors.
		/// </summary>
		/// <returns>The total number of monitors.</returns>
		public static long MonitorGetCount() => AllScreens.Length;

		/// <summary>
		/// Returns the operating system's name of the specified monitor.
		/// </summary>
		/// <param name="n">If omitted, the primary monitor will be used. Otherwise, specify the monitor number, between 1 and the number returned by <see cref="MonitorGetCount"/>.</param>
		/// <returns>A string</returns>
		public static string MonitorGetName(object n = null)
		{
			var (screen, _) = ResolveScreen(n);
#if WINDOWS
			return screen.DeviceName ?? "";
#else
			return screen.ID ?? "";
#endif
		}

		/// <summary>
		/// Returns the number of the primary monitor.
		/// </summary>
		/// <returns>The number of the primary monitor. In a single-monitor system, this will be always 1.</returns>
		public static long MonitorGetPrimary()
		{
			var allScreens = AllScreens;
			var primaryScreen = ResolvePrimaryScreen(allScreens);

			for (var i = 0; i < allScreens.Length; i++)
			{
				if (ReferenceEquals(allScreens[i], primaryScreen))
					return i + 1L;

#if WINDOWS
				if (allScreens[i]?.Primary == true)
					return i + 1L;
#else
				if (allScreens[i]?.IsPrimary == true)
					return i + 1L;
#endif
			}

			return 1L;
		}

		/// <summary>
		/// Checks if the specified monitor exists and optionally retrieves the bounding coordinates of its working area.
		/// </summary>
		/// <param name="n">If omitted, the primary monitor will be used. Otherwise, specify the monitor number, between 1 and the number returned by <see cref="MonitorGetCount"/>.</param>
		/// <param name="left">The left bounding coordinate of the work area of the specified monitor.</param>
		/// <param name="top">The top bounding coordinate of the work area of the specified monitor.</param>
		/// <param name="right">The right bounding coordinate of the work area of the specified monitor.</param>
		/// <param name="bottom">The bottom bounding coordinate of the work area of the specified monitor.</param>
		/// <returns>The monitor number which is the same as n unless n was omitted.</returns>
		public static object MonitorGetWorkArea(object n = null, [ByRef] object left = null, [ByRef] object top = null, [ByRef] object right = null, [ByRef] object bottom = null)
		{
			var (screen, monitorIndex) = ResolveScreen(n);

			if (left != null) Script.SetPropertyValue(left, "__Value", (long)screen.WorkingArea.Left);
			if (top != null) Script.SetPropertyValue(top, "__Value", (long)screen.WorkingArea.Top);
			if (right != null) Script.SetPropertyValue(right, "__Value", (long)screen.WorkingArea.Right);
			if (bottom != null) Script.SetPropertyValue(bottom, "__Value", (long)screen.WorkingArea.Bottom);
			return monitorIndex;
		}
	}
}
