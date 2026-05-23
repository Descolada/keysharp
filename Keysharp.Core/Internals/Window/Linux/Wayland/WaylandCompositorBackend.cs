#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Singleton chooser for <see cref="IWaylandCompositorBackend"/>. Probes the running
	/// compositor on first access and caches the result. Override probing with
	/// <c>KEYSHARP_WAYLAND_BACKEND=auto|kwin|sway|hyprland|cosmic|none</c>.
	/// </summary>
	internal static class WaylandCompositorBackend
	{
		private static readonly object sync = new();
		private static IWaylandCompositorBackend current;
		private static bool probed;

		internal static IWaylandCompositorBackend Current
		{
			get
			{
				lock (sync)
				{
					if (probed)
						return current;

					probed = true;
					current = Probe();
					return current;
				}
			}
		}

		private static IWaylandCompositorBackend Probe()
		{
			var forced = Environment.GetEnvironmentVariable("KEYSHARP_WAYLAND_BACKEND")?.Trim().ToLowerInvariant();

			if (!string.IsNullOrEmpty(forced))
			{
				return forced switch
				{
					"none" => null,
					"kwin" => new KWinBackend(),
					"sway" => new SwayBackend(),
					"hyprland" => new HyprlandBackend(),
					"cosmic" => new CosmicBackend(),
					_ => AutoProbe()
				};
			}

			return AutoProbe();
		}

		private static IWaylandCompositorBackend AutoProbe()
		{
			// Order matters: prefer the compositor whose IPC is richest where multiple
			// detections might collide (e.g. a session set up to talk to both KWin and
			// COSMIC for transition reasons).
			if (KWinBackend.IsAvailable())
				return new KWinBackend();

			if (SwayBackend.IsAvailable())
				return new SwayBackend();

			if (HyprlandBackend.IsAvailable())
				return new HyprlandBackend();

			if (CosmicBackend.IsAvailable())
				return new CosmicBackend();

			return null;
		}

		private static bool DesktopMatches(string token)
		{
			var current = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");

			if (string.IsNullOrEmpty(current))
				return false;

			foreach (var part in current.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				if (part.Equals(token, StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}

		// ---------- backends ----------
		//
		// Each backend is intentionally minimal right now. The detection is implemented; the
		// actual cursor-position lookup is the next PR. The structure exists so we don't have
		// to refactor when each implementation lands.

		internal sealed class KWinBackend : IWaylandCompositorBackend
		{
			public string Name => "KWin";

			internal static bool IsAvailable() => DesktopMatches("KDE");

			public bool TryGetCursorPos(out int x, out int y)
			{
				x = 0;
				y = 0;
				// TODO: load a KWin script via org.kde.kwin.Scripting.loadScript that reads
				// workspace.cursorPos and callDBus's it back to a small D-Bus service we
				// register here. kdotool implements exactly this; once we add a D-Bus
				// client (e.g. Tmds.DBus or a hand-rolled minimal one), this method is a
				// few dozen lines.
				return false;
			}
		}

		internal sealed class SwayBackend : IWaylandCompositorBackend
		{
			public string Name => "sway";

			internal static bool IsAvailable() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWAYSOCK"));

			public bool TryGetCursorPos(out int x, out int y)
			{
				x = 0;
				y = 0;
				// TODO: sway's IPC over $SWAYSOCK doesn't expose cursor pos directly; we'd
				// need to combine GET_TREE (for output rects) with the focused container's
				// rect, or rely on a future protocol addition. May not be implementable
				// without sway changes.
				return false;
			}
		}

		internal sealed class HyprlandBackend : IWaylandCompositorBackend
		{
			public string Name => "Hyprland";

			internal static bool IsAvailable() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE"));

			public bool TryGetCursorPos(out int x, out int y)
			{
				x = 0;
				y = 0;
				// TODO: open $XDG_RUNTIME_DIR/hypr/$HYPRLAND_INSTANCE_SIGNATURE/.socket.sock,
				// send the literal request "cursorpos", read "X, Y" back. Trivial — would
				// be ~30 lines of socket code.
				return false;
			}
		}

		internal sealed class CosmicBackend : IWaylandCompositorBackend
		{
			public string Name => "COSMIC";

			internal static bool IsAvailable() => DesktopMatches("COSMIC");

			public bool TryGetCursorPos(out int x, out int y)
			{
				x = 0;
				y = 0;
				// TODO: bind zcosmic_toplevel_info_v1 (which already includes geometry) and
				// — separately — find or push for a cursor-position Wayland extension on
				// COSMIC. As of writing there isn't a standard cursor-pos protocol there
				// either; the toplevel-info extension exists but doesn't carry the cursor.
				return false;
			}
		}
	}
}
#endif
