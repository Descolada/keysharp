namespace Keysharp.Core
{
	/// <summary>
	/// Public interface for monitor-related functions.
	/// </summary>
	public static class Monitor
	{
		/// <summary>
		/// Checks if the specified monitor exists and optionally retrieves its bounding coordinates.
		/// </summary>
		/// <param name="n">If omitted, the primary monitor will be used. Otherwise, specify the monitor number, between 1 and the number returned by <see cref="MonitorGetCount"/>.</param>
		/// <param name="left">The left bounding coordinate of the specified monitor.</param>
		/// <param name="top">The top bounding coordinate of the specified monitor.</param>
		/// <param name="right">The right bounding coordinate of the specified monitor.</param>
		/// <param name="bottom">The bottom bounding coordinate of the specified monitor.</param>
		/// <returns>The monitor number which is the same as n unless n was omitted.</returns>
		public static object MonitorGet(long n = -1, [ByRef] Any left = null, [ByRef] Any top = null, [ByRef] Any right = null, [ByRef] Any bottom = null)
		{
			left ??= VarRef.Empty; top ??= VarRef.Empty; right ??= VarRef.Empty; bottom ??= VarRef.Empty;
			var monitorIndex = n;
			System.Windows.Forms.Screen screen;

			if (monitorIndex > 0 && monitorIndex <= System.Windows.Forms.Screen.AllScreens.Length)
				screen = System.Windows.Forms.Screen.AllScreens[monitorIndex - 1];
			else
				screen = System.Windows.Forms.Screen.PrimaryScreen;

			Script.SetPropertyValue(left, "__Value", (long)screen.Bounds.Left);
            Script.SetPropertyValue(top, "__Value", (long)screen.Bounds.Top);
            Script.SetPropertyValue(right, "__Value", (long)screen.Bounds.Right);
            Script.SetPropertyValue(bottom, "__Value", (long)screen.Bounds.Bottom);
			return monitorIndex > 0L ? monitorIndex : 1L;
		}

		/// <summary>
		/// Returns the total number of monitors.
		/// </summary>
		/// <returns>The total number of monitors.</returns>
		public static long MonitorGetCount() => System.Windows.Forms.Screen.AllScreens.Length;

		/// <summary>
		/// Returns the operating system's name of the specified monitor.
		/// </summary>
		/// <param name="n">If omitted, the primary monitor will be used. Otherwise, specify the monitor number, between 1 and the number returned by <see cref="MonitorGetCount"/>.</param>
		/// <returns>A string</returns>
		public static string MonitorGetName(object n = null)
		{
			var monitorIndex = n.Al(-1L);

			if (monitorIndex > 0 && monitorIndex <= System.Windows.Forms.Screen.AllScreens.Length)
				return System.Windows.Forms.Screen.AllScreens[monitorIndex - 1].DeviceName;

			return System.Windows.Forms.Screen.PrimaryScreen.DeviceName;
		}

		/// <summary>
		/// Returns the number of the primary monitor.
		/// </summary>
		/// <returns>The number of the primary monitor. In a single-monitor system, this will be always 1.</returns>
		public static long MonitorGetPrimary()
		{
			long i;

			for (i = 0L; i < System.Windows.Forms.Screen.AllScreens.Length; i++)
			{
				if (System.Windows.Forms.Screen.AllScreens[i] == System.Windows.Forms.Screen.PrimaryScreen)
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
		public static object MonitorGetWorkArea(long n = -1, [ByRef] Any left = null, [ByRef] Any top = null, [ByRef] Any right = null, [ByRef] Any bottom = null)
		{
			var monitorIndex = n;
			System.Windows.Forms.Screen screen;

			if (monitorIndex > 0 && monitorIndex <= System.Windows.Forms.Screen.AllScreens.Length)
				screen = System.Windows.Forms.Screen.AllScreens[monitorIndex - 1];
			else
				screen = System.Windows.Forms.Screen.PrimaryScreen;

			Script.SetPropertyValue(left, "__Value", (long)screen.WorkingArea.Left);
            Script.SetPropertyValue(top, "__Value", (long)screen.WorkingArea.Top);
            Script.SetPropertyValue(right, "__Value", (long)screen.WorkingArea.Right);
            Script.SetPropertyValue(bottom, "__Value", (long)screen.WorkingArea.Bottom);
			return monitorIndex > 0L ? monitorIndex : 1L;
		}
	}
}