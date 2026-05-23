#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// One tooltip rendered as a <c>zwlr_layer_shell_v1</c> overlay surface. Unlike a regular
	/// xdg-popup (which is dismissed the moment the parent loses focus), a layer-shell surface
	/// on the Overlay layer stays visible across focus changes and can be repositioned/updated
	/// while another application owns the keyboard. This makes it suitable for AHK's
	/// <c>ToolTip()</c> semantics on Wayland.
	///
	/// Input-region is set to empty so clicks pass through to whatever sits behind the tooltip.
	/// Keyboard interactivity is None for the same reason.
	/// </summary>
	internal sealed class WaylandTooltip : IDisposable
	{
		private const string DefaultFontDescription = "Sans 10";
		private const int Padding = 6;
		private const string LayerNamespace = "keysharp-tooltip";
		private const int ConfigureTimeoutMs = 1000;

		private readonly WaylandLayerShellClient client;
		private WaylandLayerSurface surface;
		private WaylandShmBuffer buffer;
		private nint emptyRegion;
		private string fontDescription = DefaultFontDescription;
		private int width;
		private int height;
		private int marginLeft;
		private int marginTop;
		private bool disposed;

		internal nint Handle => surface?.Surface ?? 0;

		internal WaylandTooltip(WaylandLayerShellClient client)
		{
			this.client = client ?? throw new ArgumentNullException(nameof(client));
		}

		internal void SetFont(string description)
		{
			if (!string.IsNullOrWhiteSpace(description))
				fontDescription = description;
		}

		/// <summary>
		/// Show or update the tooltip with the given text at the given screen-space top-left
		/// coordinates. Safe to call repeatedly; subsequent calls reuse the existing layer
		/// surface and only reallocate the SHM buffer when the text size changes.
		/// </summary>
		internal void Show(string text, int x, int y)
		{
			if (disposed)
				return;

			if (string.IsNullOrEmpty(text))
			{
				Hide();
				return;
			}

			var (textWidth, textHeight) = CairoText.Measure(text, fontDescription);

			if (textWidth <= 0 || textHeight <= 0)
			{
				Hide();
				return;
			}

			var newWidth = textWidth + (Padding * 2);
			var newHeight = textHeight + (Padding * 2);

			EnsureSurface();

			if (surface == null)
				return;

			var sizeChanged = newWidth != width || newHeight != height;
			var positionChanged = x != marginLeft || y != marginTop;

			if (sizeChanged)
			{
				width = newWidth;
				height = newHeight;
				surface.SetSize((uint)width, (uint)height);
			}

			if (positionChanged || sizeChanged)
			{
				marginLeft = x;
				marginTop = y;
				surface.SetMargin(y, 0, 0, x);
			}

			if (!surface.IsConfigured)
			{
				// Empty commit so the compositor processes our set_size/set_anchor/set_margin
				// requests and replies with a configure event. surface.Commit() handles the
				// ack_configure ordering on subsequent commits.
				surface.Commit();

				if (!surface.WaitForConfigure(ConfigureTimeoutMs))
				{
					// Compositor never configured us — give up cleanly.
					Hide();
					return;
				}
			}
			else if (sizeChanged || positionChanged)
			{
				// Triggers a fresh configure cycle so the compositor agrees on the new size.
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

			CairoText.RenderTooltip(buffer.Data, buffer.Width, buffer.Height, buffer.Stride,
				text, fontDescription, Padding);

			surface.AttachBuffer(buffer);
			surface.Commit();
		}

		internal void Hide()
		{
			TeardownSurface();
			width = height = 0;
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
					// Empty input region so the tooltip is fully click-through; without this,
					// pointer events hitting the tooltip would be swallowed instead of reaching
					// the underlying window.
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
