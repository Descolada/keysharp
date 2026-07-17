#if LINUX
namespace Keysharp.Internals.Window.Linux.X11
{
	/// <summary>
	/// Describes the native X11 root-pixel desktop and isolates the one exceptional conversion needed by GTK/Eto.
	/// Public Keysharp coordinates on X11 are root-window pixels, matching XQueryPointer, XGetImage and foreign-window
	/// geometry. Toolkit coordinates are converted only at a toolkit boundary such as an Eto overlay window.
	/// </summary>
	internal static class X11DisplayTopology
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct XineramaScreenInfo
		{
			internal int ScreenNumber;
			internal short X;
			internal short Y;
			internal short Width;
			internal short Height;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct XRandRMonitorInfo
		{
			internal nint Name;
			internal int Primary;
			internal int Automatic;
			internal int OutputCount;
			internal int X;
			internal int Y;
			internal int Width;
			internal int Height;
			internal int PhysicalWidth;
			internal int PhysicalHeight;
			internal nint Outputs;
		}

		private readonly record struct Mapping(DisplayInfo Display, ScreenRect ToolkitBounds);

		private sealed class Snapshot
		{
			internal readonly DisplayInfo[] Displays;
			internal readonly ScreenRect[] NativeBounds;
			internal readonly ScreenRect[] ToolkitBounds;

			internal Snapshot(Mapping[] mappings)
			{
				Displays = mappings.Select(m => m.Display).ToArray();
				NativeBounds = mappings.Select(m => m.Display.Bounds).ToArray();
				ToolkitBounds = mappings.Select(m => m.ToolkitBounds).ToArray();
			}
		}

		[DllImport("libXinerama.so.1", EntryPoint = "XineramaIsActive")]
		private static extern int IsActive(nint display);

		[DllImport("libXinerama.so.1", EntryPoint = "XineramaQueryScreens")]
		private static extern nint QueryScreens(nint display, out int count);

		[DllImport("libXrandr.so.2", EntryPoint = "XRRGetMonitors")]
		private static extern nint GetMonitors(nint display, nint window, [MarshalAs(UnmanagedType.Bool)] bool active, out int count);

		[DllImport("libXrandr.so.2", EntryPoint = "XRRFreeMonitors")]
		private static extern void FreeMonitors(nint monitors);

		private static readonly object snapshotLock = new();
		private static Snapshot cachedSnapshot;
		private static long cachedAtMs;
		private const int ConversionCacheLifetimeMs = 500;

		/// <summary>Returns a fresh native topology snapshot. Monitor enumeration must not inherit the short-lived
		/// cache used by the toolkit conversion hot path.</summary>
		internal static IReadOnlyList<DisplayInfo> GetDisplays() => BuildSnapshot().Displays;

		/// <summary>Converts a public X11 root-pixel rectangle for GTK/Eto. Each rectangle endpoint is mapped by
		/// the display containing it so a rectangle spanning differently scaled displays preserves their seam.</summary>
		internal static ScreenRect ToToolkitBounds(ScreenRect bounds)
		{
			var snapshot = CachedSnapshot;
			return MapAcrossDisplays(bounds, snapshot.NativeBounds, snapshot.ToolkitBounds);
		}

		/// <summary>Converts GTK/Eto screen geometry back to public X11 root pixels.</summary>
		internal static ScreenRect FromToolkitBounds(ScreenRect bounds)
		{
			var snapshot = CachedSnapshot;
			return MapAcrossDisplays(bounds, snapshot.ToolkitBounds, snapshot.NativeBounds);
		}

		private static Snapshot CachedSnapshot
		{
			get
			{
				var now = Environment.TickCount64;
				lock (snapshotLock)
				{
					if (cachedSnapshot == null || now - cachedAtMs >= ConversionCacheLifetimeMs)
					{
						cachedSnapshot = BuildSnapshot();
						cachedAtMs = now;
					}

					return cachedSnapshot;
				}
			}
		}

		private static Snapshot BuildSnapshot()
		{
			var toolkit = Forms.Screen.Screens?.Where(s => s != null).ToArray() ?? [];
			var native = QueryNativeScreens();
			var matchedNative = MatchNativeScreens(toolkit, native);
			var mappings = new List<Mapping>();
			var anyPrimary = toolkit.Any(s => s.IsPrimary);
			var toolkitUnion = Union(toolkit.Select(s => ScreenRect.FromRectangle(s.Bounds)));
			var nativeUnion = Union(native);

			for (var i = 0; i < toolkit.Length; i++)
			{
				var screen = toolkit[i];
				var toolkitBounds = ScreenRect.FromRectangle(screen.Bounds);

				if (!toolkitBounds.HasArea)
					continue;

				ScreenRect toolkitWorkArea;
				try { toolkitWorkArea = ScreenRect.FromRectangle(screen.WorkingArea); }
				catch { toolkitWorkArea = toolkitBounds; }

				var nativeBounds = matchedNative[i] ?? (toolkitUnion.HasArea && nativeUnion.HasArea
					? MapRectangle(toolkitBounds, toolkitUnion, nativeUnion) : toolkitBounds);
				// Content scale is a toolkit property, independent of the mapping between GTK coordinates and the
				// X11 root. Geometry ratios are not a reliable scale source in mixed-monitor layouts.
				var contentScale = ScaleFactor.Normalize(screen.LogicalPixelSize);
				var workArea = MapRectangle(toolkitWorkArea, toolkitBounds, nativeBounds);
				var primary = screen.IsPrimary || !anyPrimary && nativeBounds.X == 0 && nativeBounds.Y == 0;
				var display = new DisplayInfo(screen.ID ?? $"display-{i + 1}", nativeBounds, workArea,
					contentScale, primary, (ulong)i);
				mappings.Add(new Mapping(display, toolkitBounds));
			}

			// A headless/minimal GTK startup can briefly expose no monitors. Keep Xlib usable in that interval.
			if (mappings.Count == 0)
				for (var i = 0; i < native.Count; i++)
				{
					var bounds = native[i];
					mappings.Add(new Mapping(new DisplayInfo($"Xinerama-{i}", bounds, bounds, 1,
						i == 0 || bounds.X == 0 && bounds.Y == 0, (ulong)i), bounds));
				}

			if (mappings.Count == 0)
				mappings.Add(new Mapping(new DisplayInfo("X11-root", new ScreenRect(0, 0, 1, 1),
					new ScreenRect(0, 0, 1, 1), 1, true), new ScreenRect(0, 0, 1, 1)));

			return new Snapshot(mappings.ToArray());
		}

		private static ScreenRect?[] MatchNativeScreens(Forms.Screen[] toolkit, List<ScreenRect> native)
		{
			var result = new ScreenRect?[toolkit.Length];

			// One Xinerama rectangle per GDK monitor is the only topology that can be associated losslessly. If a
			// server exposes just the virtual root while GDK exposes several monitors, derive monitor-local raw bounds
			// from GDK instead of incorrectly assigning the whole root to the first monitor.
			if (toolkit.Length == 0 || toolkit.Length != native.Count)
				return result;

			var available = new HashSet<int>(Enumerable.Range(0, native.Count));
			var toolkitUnion = Union(toolkit.Select(screen => ScreenRect.FromRectangle(screen.Bounds)));
			var nativeUnion = Union(native);

			for (var i = 0; i < toolkit.Length; i++)
			{
				var bounds = ScreenRect.FromRectangle(toolkit[i].Bounds);
				var scale = ScaleFactor.Normalize(toolkit[i].LogicalPixelSize);
				var expectedWidth = Math.Max(1, (int)Math.Round(bounds.Width * scale));
				var expectedHeight = Math.Max(1, (int)Math.Round(bounds.Height * scale));
				// Global GTK origins cannot be multiplied by each monitor's independent scale: doing that inserts
				// fictitious gaps in mixed-scale layouts. Use the scale only for size and the normalized topology for
				// relative placement; RandR/Xinerama remains the source of the exact root-pixel rectangle.
				var expectedLayout = MapRectangle(bounds, toolkitUnion, nativeUnion);
				var best = -1;
				var bestScore = double.MaxValue;

				foreach (var candidate in available)
				{
					var raw = native[candidate];
					var sizeError = Math.Abs(raw.Width - expectedWidth) / (double)expectedWidth
						+ Math.Abs(raw.Height - expectedHeight) / (double)expectedHeight;
					var originError = Math.Abs((long)raw.X - expectedLayout.X) / (double)Math.Max(1, nativeUnion.Width)
						+ Math.Abs((long)raw.Y - expectedLayout.Y) / (double)Math.Max(1, nativeUnion.Height);
					var score = sizeError * 8 + originError;

					if (score < bestScore)
					{
						best = candidate;
						bestScore = score;
					}
				}

				if (best >= 0)
				{
					result[i] = native[best];
					available.Remove(best);
				}
			}

			return result;
		}

		private static List<ScreenRect> QueryNativeScreens()
		{
			var result = new List<ScreenRect>();
			var display = Keysharp.Internals.Window.Linux.Proxies.XDisplay.Default;

			if (display == null || display.Handle == 0)
				return result;

			nint monitors = 0;
			try
			{
				monitors = GetMonitors(display.Handle, (nint)display.Root.ID, true, out var count);
				var stride = Marshal.SizeOf<XRandRMonitorInfo>();

				for (var i = 0; monitors != 0 && i < count; i++)
				{
					var info = Marshal.PtrToStructure<XRandRMonitorInfo>(monitors + i * stride);
					var bounds = new ScreenRect(info.X, info.Y, info.Width, info.Height);
					if (bounds.HasArea)
						result.Add(bounds);
				}
			}
			catch (DllNotFoundException) { }
			catch (EntryPointNotFoundException) { }
			finally
			{
				if (monitors != 0)
					FreeMonitors(monitors);
			}

			if (result.Count > 0)
				return result;

			nint screens = 0;
			try
			{
				if (IsActive(display.Handle) != 0)
				{
					screens = QueryScreens(display.Handle, out var count);
					var stride = Marshal.SizeOf<XineramaScreenInfo>();

					for (var i = 0; screens != 0 && i < count; i++)
					{
						var info = Marshal.PtrToStructure<XineramaScreenInfo>(screens + i * stride);
						var bounds = new ScreenRect(info.X, info.Y, info.Width, info.Height);
						if (bounds.HasArea)
							result.Add(bounds);
					}
				}
			}
			catch (DllNotFoundException) { }
			catch (EntryPointNotFoundException) { }
			finally
			{
				if (screens != 0)
					_ = Xlib.XFree(screens);
			}

			if (result.Count == 0)
				try
				{
					var attr = display.Root.Attributes;
					var bounds = new ScreenRect(0, 0, attr.width, attr.height);
					if (bounds.HasArea)
						result.Add(bounds);
				}
				catch { }

			return result;
		}

		private static ScreenRect Union(IEnumerable<ScreenRect> rectangles)
		{
			var any = false;
			long left = 0, top = 0, right = 0, bottom = 0;

			foreach (var rect in rectangles)
			{
				if (!rect.HasArea)
					continue;

				if (!any)
				{
					left = rect.X; top = rect.Y; right = (long)rect.X + rect.Width; bottom = (long)rect.Y + rect.Height;
					any = true;
				}
				else
				{
					left = Math.Min(left, rect.X); top = Math.Min(top, rect.Y);
					right = Math.Max(right, (long)rect.X + rect.Width); bottom = Math.Max(bottom, (long)rect.Y + rect.Height);
				}
			}

			return any ? new ScreenRect(ClampToInt(left), ClampToInt(top),
				Math.Max(0, ClampToInt(right - left)), Math.Max(0, ClampToInt(bottom - top))) : default;
		}

		/// <summary>Maps the inclusive start and exclusive end of a rectangle using the display which owns each
		/// endpoint. The source and destination lists describe the same displays in their respective coordinate
		/// spaces. This is internal so the topology arithmetic can be tested without an X server.</summary>
		internal static ScreenRect MapAcrossDisplays(ScreenRect value, IReadOnlyList<ScreenRect> sources,
			IReadOnlyList<ScreenRect> destinations)
		{
			if (sources == null || destinations == null || sources.Count == 0 || sources.Count != destinations.Count)
				return value;

			var right = value.Right;
			var bottom = value.Bottom;
			var start = FindDisplay(sources, value.X, value.Y);
			// Probe just inside an exclusive edge to keep a rectangle ending at a seam on its source display, while
			// still mapping the actual edge coordinate. A spanning edge naturally selects the display at its far end.
			var endProbeX = value.Width > 0 ? right - 1 : right;
			var endProbeY = value.Height > 0 ? bottom - 1 : bottom;
			var end = FindDisplay(sources, endProbeX, endProbeY);

			MapPoint(value.X, value.Y, sources[start], destinations[start], out var left, out var top);
			MapPoint(right, bottom, sources[end], destinations[end], out var mappedRight, out var mappedBottom);
			return CreateRectangle(left, top, mappedRight, mappedBottom);
		}

		private static int FindDisplay(IReadOnlyList<ScreenRect> displays, long x, long y)
		{
			var nearest = 0;
			var nearestDistance = double.MaxValue;

			for (var i = 0; i < displays.Count; i++)
			{
				var bounds = displays[i];

				if (!bounds.HasArea)
					continue;

				if (x >= bounds.X && x < bounds.Right && y >= bounds.Y && y < bounds.Bottom)
					return i;

				var dx = x < bounds.X ? (long)bounds.X - x : x >= bounds.Right ? x - bounds.Right + 1 : 0;
				var dy = y < bounds.Y ? (long)bounds.Y - y : y >= bounds.Bottom ? y - bounds.Bottom + 1 : 0;
				var distance = (double)dx * dx + (double)dy * dy;

				if (distance < nearestDistance)
				{
					nearest = i;
					nearestDistance = distance;
				}
			}

			return nearest;
		}

		private static ScreenRect MapRectangle(ScreenRect value, ScreenRect source, ScreenRect destination)
		{
			MapPoint(value.X, value.Y, source, destination, out var left, out var top);
			MapPoint(value.Right, value.Bottom, source, destination, out var right, out var bottom);
			return CreateRectangle(left, top, right, bottom);
		}

		private static ScreenRect CreateRectangle(int left, int top, int right, int bottom)
			=> new(left, top, Math.Max(0, ClampToInt((long)right - left)),
				Math.Max(0, ClampToInt((long)bottom - top)));

		private static void MapPoint(long x, long y, ScreenRect source, ScreenRect destination,
			out int mappedX, out int mappedY)
		{
			var sx = source.Width > 0 ? (double)destination.Width / source.Width : 1.0;
			var sy = source.Height > 0 ? (double)destination.Height / source.Height : 1.0;
			mappedX = ClampToInt(destination.X + Math.Round((x - source.X) * sx));
			mappedY = ClampToInt(destination.Y + Math.Round((y - source.Y) * sy));
		}

		private static int ClampToInt(double value)
			=> value <= int.MinValue ? int.MinValue : value >= int.MaxValue ? int.MaxValue : (int)value;

		private static int ClampToInt(long value)
			=> value <= int.MinValue ? int.MinValue : value >= int.MaxValue ? int.MaxValue : (int)value;
	}
}
#endif
