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
		private static Forms.Screen[] AllScreens => Forms.Screen.Screens.ToArray();
#endif

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
			var monitorIndex = n.Al(-1L);
			Forms.Screen screen;

			if (monitorIndex > 0 && monitorIndex <= AllScreens.Length)
				screen = AllScreens[monitorIndex - 1];
			else
				screen = Forms.Screen.PrimaryScreen;

			if (left != null) Script.SetPropertyValue(left, "__Value", (long)screen.Bounds.Left);
            if (top != null) Script.SetPropertyValue(top, "__Value", (long)screen.Bounds.Top);
            if (right != null) Script.SetPropertyValue(right, "__Value", (long)screen.Bounds.Right);
            if (bottom != null) Script.SetPropertyValue(bottom, "__Value", (long)screen.Bounds.Bottom);
			return monitorIndex > 0L ? monitorIndex : 1L;
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
			var monitorIndex = n.Al(-1L);
#if WINDOWS
			if (monitorIndex > 0 && monitorIndex <= AllScreens.Length)
				return AllScreens[monitorIndex - 1].DeviceName ?? "";

			return System.Windows.Forms.Screen.PrimaryScreen.DeviceName;
#else
			if (monitorIndex > 0 && monitorIndex <= AllScreens.Length)
				return AllScreens[monitorIndex - 1].ID ?? "";

			return Forms.Screen.PrimaryScreen.ID ?? "";
#endif
		}

		/// <summary>
		/// Returns the number of the primary monitor.
		/// </summary>
		/// <returns>The number of the primary monitor. In a single-monitor system, this will be always 1.</returns>
		public static long MonitorGetPrimary()
		{
			long i;

			var allScreens = AllScreens;
			for (i = 0L; i < allScreens.Length; i++)
			{
				if (allScreens[i] == Forms.Screen.PrimaryScreen)
					break;
			}

			return i + 1L;
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
			var monitorIndex = n.Al(-1L);
			Forms.Screen screen;

			if (monitorIndex > 0 && monitorIndex <= AllScreens.Length)
				screen = AllScreens[monitorIndex - 1];
			else
				screen = Forms.Screen.PrimaryScreen;

			if (left != null) Script.SetPropertyValue(left, "__Value", (long)screen.WorkingArea.Left);
            if (top != null) Script.SetPropertyValue(top, "__Value", (long)screen.WorkingArea.Top);
            if (right != null) Script.SetPropertyValue(right, "__Value", (long)screen.WorkingArea.Right);
            if (bottom != null) Script.SetPropertyValue(bottom, "__Value", (long)screen.WorkingArea.Bottom);
			return monitorIndex > 0L ? monitorIndex : 1L;
		}
	}
}