#if LINUX
using Wl = Keysharp.Internals.Window.Linux.Wayland;
#endif

namespace Keysharp.Internals
{
#if LINUX
	/// <summary>
	/// Linux overlay backing. Genuine click-through is only possible without an Eto Gui: a wlr-layer-shell
	/// surface (KWin/wlroots), or a compositor-drawn overlay (GNOME shell extension). This service owns BOTH
	/// of those toolkit-free backings — the highlight layer surface (like the tooltips below) and the GNOME
	/// compositor overlay — and reports PreferredKind == Eto when neither applies, signalling the builtin to
	/// render the four-edge Eto Gui itself. PreferredKind is computed on demand (not latched at startup)
	/// because the layer-shell probe retries until GDK is ready — a premature latch would wrongly pin the
	/// Eto fallback.
	/// </summary>
	internal sealed class LinuxOverlay : IOverlay
	{
		public OverlayKind PreferredKind
		{
			get
			{
				if (Keysharp.Internals.Window.Linux.Wayland.WaylandLayerShellClient.Current != null)
					return OverlayKind.LayerSurface;

				if (IsWaylandSession
					&& Keysharp.Internals.Window.Linux.Wayland.WaylandBackend.Current?.SupportsHighlight == true)
					return OverlayKind.Compositor;

				return OverlayKind.Eto;
			}
		}

		// Highlight overlays — the service owns BOTH toolkit-free backings (the Eto path is rendered by the
		// builtin and never reaches here). The per-instance click-through layer surfaces, keyed by the builtin's
		// overlay id; the GNOME-compositor path is stateless (the shell extension owns its overlays by id).
		private Dictionary<uint, Wl.WaylandHighlight> highlights;

		// Show or update the outline for `id`. PreferredKind picks the backing per call (it is not latched —
		// see the class doc): a Cairo-drawn wlr-layer-shell surface owned here exactly like the tooltips below,
		// or the GNOME shell extension via the Wayland backend. Returns false where neither applies (X11/Eto),
		// so the builtin renders the four-edge Eto Gui itself.
		public bool TryShowHighlight(uint id, int x, int y, int width, int height, string colorRRGGBB, int thickness)
		{
			switch (PreferredKind)
			{
				case OverlayKind.LayerSurface:
				{
					var client = Wl.WaylandLayerShellClient.Current;

					if (client == null || !client.IsAvailable)
						return false;

					try
					{
						highlights ??= new ();

						if (!highlights.TryGetValue(id, out var hl) || hl == null)
							highlights[id] = hl = new Wl.WaylandHighlight(client);

						var (r, g, b) = ColorRgb(colorRRGGBB);
						hl.Show(x, y, width, height, thickness, r, g, b);
						return true;
					}
					catch
					{
						// Surface blew up at runtime — drop it so a later Show rebuilds (and PreferredKind, being
						// re-evaluated per call, falls to Eto if layer-shell has gone away entirely).
						TryHideHighlight(id);
						return false;
					}
				}

				case OverlayKind.Compositor:
					return Wl.WaylandBackend.Current?.TryShowHighlight(id, x, y, width, height, colorRRGGBB, thickness) == true;

				default:
					return false;   // Eto (X11) and the Windows/Mac null objects render in the builtin.
			}
		}

		// Hide and free the highlight for `id`; idempotent, covers both backings. The layer surface is torn down
		// like a tooltip (WaylandHighlight.Hide already disposes the surface, so a later Show recreates it — no
		// cross-Hide surface reuse is lost), and any compositor backend is told to drop the overlay.
		public bool TryHideHighlight(uint id)
		{
			var hid = false;

			if (highlights != null && highlights.TryGetValue(id, out var hl) && hl != null)
			{
				try { hl.Hide(); } catch { }
				try { hl.Dispose(); } catch { }
				highlights.Remove(id);
				hid = true;
			}

			return Wl.WaylandBackend.Current?.TryHideHighlight(id) == true || hid;
		}

		// Split a canonical "RRGGBB" hex colour into r/g/b components in 0..1 for Cairo (layer-surface path).
		private static (double r, double g, double b) ColorRgb(string hex)
		{
			if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v))
				return (((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0);

			return (1, 0, 0);
		}

		// --- Wayland layer-shell tooltips ---
		// The capability probe (layer-shell + Cairo/Pango text) and the per-slot surface lifecycle live here so
		// the ToolTip builtin never reaches into WaylandLayerShellClient/CairoText itself. Probed once on first
		// use (mirrors the builtin's old latch); a runtime failure latches the path off so the WinForms tooltip
		// fallback takes over. Slots are keyed by the builtin's tooltip index.
		private bool tooltipProbed;
		private bool tooltipUsable;
		private Dictionary<int, Wl.WaylandTooltip> tooltips;

		public bool SupportsTooltip
		{
			get
			{
				if (!tooltipProbed)
				{
					tooltipProbed = true;

					try
					{
						var client = Wl.WaylandLayerShellClient.Current;
						tooltipUsable = client != null && client.IsAvailable && Wl.CairoText.TryProbe();
					}
					catch
					{
						tooltipUsable = false;
					}
				}

				return tooltipUsable;
			}
		}

		public bool TryShowTooltip(int slot, string text, int x, int y)
		{
			if (!SupportsTooltip)
				return false;

			var client = Wl.WaylandLayerShellClient.Current;

			if (client == null || !client.IsAvailable)
				return false;

			try
			{
				tooltips ??= new ();

				if (!tooltips.TryGetValue(slot, out var tip) || tip == null)
					tooltips[slot] = tip = new Wl.WaylandTooltip(client);

				tip.Show(text, x, y);
				return true;
			}
			catch
			{
				// Setup blew up at runtime — disable the path permanently and let the WinForms fallback take over.
				tooltipUsable = false;
				return false;
			}
		}

		public bool TryHideTooltip(int slot)
		{
			if (tooltips != null && tooltips.TryGetValue(slot, out var tip) && tip != null)
			{
				try { tip.Hide(); } catch { }
				try { tip.Dispose(); } catch { }
				tooltips.Remove(slot);
			}

			return true;
		}
	}
#elif WINDOWS
	internal sealed class WindowsOverlay : IOverlay
	{
		public OverlayKind PreferredKind => OverlayKind.Eto;
		public bool TryShowHighlight(uint id, int x, int y, int width, int height, string colorRRGGBB, int thickness) => false;
		public bool TryHideHighlight(uint id) => false;
		public bool SupportsTooltip => false;
		public bool TryShowTooltip(int slot, string text, int x, int y) => false;
		public bool TryHideTooltip(int slot) => false;
	}
#elif OSX
	internal sealed class MacOverlay : IOverlay
	{
		public OverlayKind PreferredKind => OverlayKind.Eto;
		public bool TryShowHighlight(uint id, int x, int y, int width, int height, string colorRRGGBB, int thickness) => false;
		public bool TryHideHighlight(uint id) => false;
		public bool SupportsTooltip => false;
		public bool TryShowTooltip(int slot, string text, int x, int y) => false;
		public bool TryHideTooltip(int slot) => false;
	}
#endif
}
