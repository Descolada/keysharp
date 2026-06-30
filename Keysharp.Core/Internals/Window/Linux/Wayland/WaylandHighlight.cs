#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// A click-through rectangle-outline overlay rendered as a <c>zwlr_layer_shell_v1</c> surface, the
	/// layer-shell counterpart of the Eto-Gui <see cref="Keysharp.Builtins.Ks.KeysharpHighlight"/>. On
	/// layer-shell compositors (KWin, sway, Hyprland, COSMIC) this is the only way to get a genuinely
	/// click-through overlay: the compositor honors a surface's empty input region for a layer surface
	/// (a non-managed surface) but not for a managed GTK toplevel. It also gets no titlebar, precise
	/// positioning (anchor top-left + margins) and always-on-top (Overlay layer) for free, with none of
	/// the GTK-toplevel workarounds (noBorder / keepAbove / input-shape).
	///
	/// Mirrors <see cref="WaylandTooltip"/>: one reusable layer surface fed an SHM buffer drawn with
	/// Cairo (a hollow border instead of text). Keyboard interactivity None and an empty input region
	/// make it fully click-through.
	/// </summary>
	internal sealed class WaylandHighlight : IDisposable
	{
		private const string LayerNamespace = "keysharp-highlight";
		private const int ConfigureTimeoutMs = 1000;

		private readonly WaylandLayerShellClient client;
		private WaylandLayerSurface surface;
		private WaylandShmBuffer buffer;
		private nint emptyRegion;
		private int width;
		private int height;
		private int marginLeft;
		private int marginTop;
		private int thickness = -1;
		private double r, g, b;
		private bool disposed;

		internal WaylandHighlight(WaylandLayerShellClient client)
		{
			this.client = client ?? throw new ArgumentNullException(nameof(client));
		}

		/// <summary>Show or update the outline at the given screen-space top-left, size, thickness and colour
		/// (r/g/b in 0..1). Safe to call repeatedly; reuses the surface and only reallocates the buffer when
		/// the size changes.</summary>
		internal void Show(int x, int y, int w, int h, int thickness, double r, double g, double b)
		{
			if (disposed)
				return;

			if (w < 1 || h < 1)
			{
				Hide();
				return;
			}

			EnsureSurface();

			if (surface == null)
				return;

			var sizeChanged = w != width || h != height;
			var positionChanged = x != marginLeft || y != marginTop;
			var styleChanged = thickness != this.thickness || r != this.r || g != this.g || b != this.b;

			if (sizeChanged)
			{
				width = w;
				height = h;
				surface.SetSize((uint)width, (uint)height);
			}

			if (positionChanged || sizeChanged)
			{
				marginLeft = x;
				marginTop = y;
				// Anchored top|left, so margin-left=x and margin-top=y place the surface at absolute (x,y).
				surface.SetMargin(y, 0, 0, x);
			}

			this.thickness = thickness;
			this.r = r;
			this.g = g;
			this.b = b;

			if (!surface.IsConfigured)
			{
				surface.Commit();

				if (!surface.WaitForConfigure(ConfigureTimeoutMs))
				{
					Hide();
					return;
				}
			}
			else if (sizeChanged || positionChanged)
			{
				surface.Commit();
				_ = surface.WaitForConfigure(ConfigureTimeoutMs);
			}

			if (sizeChanged || buffer == null)
			{
				buffer?.Dispose();
				buffer = null;

				lock (WaylandLayerShellClient.Sync)
					buffer = WaylandShmBuffer.Create(client.Shm, width, height);
			}

			if (sizeChanged || styleChanged || buffer != null)
				CairoText.RenderBorder(buffer.Data, buffer.Width, buffer.Height, buffer.Stride, thickness, r, g, b);

			surface.AttachBuffer(buffer);
			surface.Commit();
		}

		internal void Hide()
		{
			TeardownSurface();
			width = height = 0;
			thickness = -1;
		}

		private void EnsureSurface()
		{
			if (surface != null || client == null || !client.IsAvailable)
				return;

			try
			{
				surface = new WaylandLayerSurface(client, WaylandNative.LayerOverlay, LayerNamespace);
				surface.SetAnchor(WaylandNative.AnchorTop | WaylandNative.AnchorLeft);
				surface.SetExclusiveZone(-1);
				surface.SetKeyboardInteractivity(WaylandNative.KeyboardInteractivityNone);

				lock (WaylandLayerShellClient.Sync)
				{
					// Empty input region => fully click-through; without it the outline would swallow clicks.
					emptyRegion = WaylandNative.CompositorCreateRegion(client.Compositor);

					if (emptyRegion != 0 && surface.Surface != 0)
						WaylandNative.SurfaceSetInputRegion(surface.Surface, emptyRegion);
				}
			}
			catch
			{
				TeardownSurface();
			}
		}

		private void TeardownSurface()
		{
			lock (WaylandLayerShellClient.Sync)
			{
				if (buffer != null)
				{
					buffer.Dispose();
					buffer = null;
				}

				if (emptyRegion != 0)
				{
					WaylandNative.RegionDestroy(emptyRegion);
					emptyRegion = 0;
				}
			}

			surface?.Dispose();
			surface = null;
		}

		public void Dispose()
		{
			if (disposed)
				return;

			disposed = true;
			TeardownSurface();
		}
	}
}
#endif
