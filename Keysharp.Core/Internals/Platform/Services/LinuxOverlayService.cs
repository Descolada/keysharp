#if LINUX
using Wl = Keysharp.Internals.Window.Linux.Wayland;

namespace Keysharp.Internals
{
	/// <summary>Linux overlay backing. Image overlays prefer wlr-layer-shell, then a compositor extension
	/// (GNOME/Cinnamon), then the Eto click-through fallback -- chosen per overlay on its first Show.</summary>
	internal sealed class LinuxOverlay : OverlayBase
	{
		public override PixelSize GetCanvasSize(ScreenRect bounds)
		{
			// X11's public coordinates are already root-window pixels, which are also the backing pixels.
			if (!IsWaylandSession)
				return OverlayCanvasSizing.FromScale(bounds, 1.0);

			var client = Wl.WaylandLayerShellClient.Current;
			var segments = client?.GetOutputSegments(bounds);

			if (segments is { Count: > 0 })
			{
				var scale = segments.Max(segment => ScaleFactor.Normalize(segment.Output.BufferScale));
				return OverlayCanvasSizing.FromScale(bounds, scale);
			}

			return OverlayCanvasSizing.FromEtoScreen(bounds);
		}

		protected override IImageOverlayBacking CreateBacking(uint id) => new LinuxImageOverlayBacking(id);
	}

	// Picks wlr-layer-shell / compositor-extension / Eto on the first Show (falling back to Eto if the preferred
	// backing fails), then reuses that concrete backing for every later Show/Move.
	internal sealed class LinuxImageOverlayBacking : IImageOverlayBacking
	{
		private readonly uint id;
		private IImageOverlayBacking inner;

		internal LinuxImageOverlayBacking(uint id) => this.id = id;

		public nint Handle => inner?.Handle ?? 0;

		public bool Show(Bitmap image, ScreenRect bounds, bool clickThrough)
		{
			if (inner != null)
			{
				if (inner.Show(image, bounds, clickThrough))
					return true;

				// A layer-shell connection can disappear on compositor restart. Its children are invalidated before
				// wl_display_disconnect; retire that terminal backing and run normal selection again on the new client.
				if (inner is LayerImageBacking layer && !layer.IsAvailable)
				{
					inner.Dispose();
					inner = null;
					return Show(image, bounds, clickThrough);
				}

				return false;
			}

			var preferred = CreatePreferred();

			// Every backing borrows `image` (copies what it needs, never disposes it), so we can hand the same
			// bitmap to the preferred backing and, if it genuinely can't render it, straight to the Eto fallback
			// -- no clone needed. Crucially, the compositor backing does NOT report a transient D-Bus TIMEOUT as a
			// failure: the shell almost certainly still received the (cold, large) upload and created the actor,
			// so it commits and returns true, and the next Show/Move updates that same shell-owned actor in place.
			// Only a DEFINITIVE failure (the extension rejected the call / is absent) returns false here. That is
			// what prevents the duplicated-overlay bug -- a naive fallback on timeout would leave the shell's actor
			// (correctly positioned) PLUS a second Eto window that native-Wayland GDK cannot position, so it piles
			// up in the screen centre and, being `inner`, steals every live update. See CompositorImageBacking.Show.
			if (preferred.Show(image, bounds, clickThrough))
			{
				inner = preferred;
				return true;
			}

			preferred.Dispose();

			if (preferred is EtoImageOverlay)
				return false;

			var fallback = new EtoImageOverlay();

			if (fallback.Show(image, bounds, clickThrough))
			{
				inner = fallback;
				return true;
			}

			fallback.Dispose();
			return false;
		}

		public bool Move(ScreenRect bounds) => inner?.Move(bounds) ?? false;

		public bool TryHide()
		{
			var backing = inner;

			if (backing == null)
				return true;

			// Keep `inner` on an unconfirmed withdraw so a later retry re-attempts it; only forget it once it is gone.
			if (!backing.TryHide())
				return false;

			inner = null;
			return true;
		}

		public void Dispose()
		{
			inner?.Dispose();
			inner = null;
		}

		private IImageOverlayBacking CreatePreferred()
		{
			var client = Wl.WaylandLayerShellClient.Current;

			if (client != null && client.IsAvailable)
				return new LayerImageBacking();

			if (IsWaylandSession && ShouldAttemptCompositor(Wl.WaylandBackend.Current))
				return new CompositorImageBacking(id);

			return new EtoImageOverlay();
		}

		// A shell service's real Show response is authoritative. Its separate NameHasOwner probe is only a cached
		// availability snapshot and can time out while other calls on the same healthy connection keep succeeding.
		internal static bool ShouldAttemptCompositor(Wl.IWaylandBackend backend)
			=> backend?.CanAttemptImageOverlay == true;
	}

	// wlr-layer-shell backing (KWin/wlroots). WaylandImageOverlay copies the pixels into its own SHM buffer on
	// Show, so nothing of the borrowed `image` is retained; a same-size move just repositions that surface.
	internal sealed class LayerImageBacking : IImageOverlayBacking
	{
		private sealed class Fragment
		{
			internal readonly Wl.WaylandImageOverlay Overlay;
			internal Wl.WaylandLayerShellClient.OutputSegment Segment;

			internal Fragment(Wl.WaylandImageOverlay overlay,
				Wl.WaylandLayerShellClient.OutputSegment segment)
			{
				Overlay = overlay;
				Segment = segment;
			}
		}

		private readonly Dictionary<uint, Fragment> fragments = [];
		private int shownW, shownH;

		public nint Handle
		{
			get
			{
				foreach (var fragment in fragments.Values)
					return fragment.Overlay.Handle;

				return 0;
			}
		}

		internal bool IsAvailable
		{
			get
			{
				if (fragments.Count == 0)
					return Wl.WaylandLayerShellClient.Current?.IsAvailable == true;

				foreach (var fragment in fragments.Values)
					if (!fragment.Overlay.IsAvailable)
						return false;

				return true;
			}
		}

		public bool Show(Bitmap image, ScreenRect bounds, bool clickThrough)
		{
			var client = Wl.WaylandLayerShellClient.Current;

			if (client == null || !client.IsAvailable)
				return false;   // borrow: never dispose `image`

			try
			{
				var segments = client.GetOutputSegments(bounds);

				if (segments.Count == 0)
					return false;

				// The steady-state animation path has one unchanged surface: update only its next buffer. Geometry and
				// multi-output changes are staged below because mutating several live fragments sequentially would expose
				// a half-old/half-new frame if a later output failed.
				if (segments.Count == 1 && fragments.Count == 1
						&& fragments.TryGetValue(segments[0].Output.RegistryName, out var existing))
				{
					var segment = segments[0];

					if (!existing.Overlay.Show(image, SourcePixels(image, bounds, segment), segment.Bounds,
							segment.Output, clickThrough))
						return false;

					existing.Segment = segment;
					shownW = bounds.Width;
					shownH = bounds.Height;
					return true;
				}

				var staged = new Dictionary<uint, Fragment>();
				var stagedCommitted = false;

				try
				{
					foreach (var segment in segments)
					{
						var overlay = new Wl.WaylandImageOverlay(client);

						if (!overlay.Prepare(image, SourcePixels(image, bounds, segment), segment.Bounds,
								segment.Output, clickThrough))
						{
							overlay.Dispose();
							return false;
						}

						staged.Add(segment.Output.RegistryName, new Fragment(overlay, segment));
					}

					// Every new surface is configured and has a complete SHM frame before any content is attached. The
					// final commits are necessarily per-output (Wayland has no cross-surface transaction), but a prepare
					// failure can no longer expose a partial replacement.
					foreach (var fragment in staged.Values)
						if (!fragment.Overlay.CommitPrepared())
							return false;

					stagedCommitted = true;
				}
				finally
				{
					if (!stagedCommitted)
						foreach (var fragment in staged.Values)
							fragment.Overlay.Dispose();
				}

				var previous = fragments.Values.ToArray();
				fragments.Clear();

				foreach (var pair in staged)
					fragments.Add(pair.Key, pair.Value);

				shownW = bounds.Width;
				shownH = bounds.Height;

				// Replacements are already configured and mapped, so retiring old surfaces cannot flash a blank frame.
				foreach (var fragment in previous)
					fragment.Overlay.Dispose();

				return true;
			}
			catch
			{
				return false;
			}
		}

		public bool Move(ScreenRect bounds)
		{
			if (fragments.Count != 1)
				return false;

			Fragment fragment = null;

			foreach (var candidate in fragments.Values)
			{
				fragment = candidate;
				break;
			}

			if (fragment == null)
				return false;

			if (!TryResolveSameOutputMove(fragment.Segment, shownW, shownH, bounds, out var segment))
				return false;

			try
			{
				if (!fragment.Overlay.Reposition(segment.Bounds, segment.Output))
					return false;
				fragment.Segment = segment;
				return true;
			}
			catch { return false; }
		}

		internal static bool TryResolveSameOutputMove(Wl.WaylandLayerShellClient.OutputSegment current,
			int shownWidth, int shownHeight, ScreenRect bounds,
			out Wl.WaylandLayerShellClient.OutputSegment moved)
		{
			moved = default;
			var output = current.Output.Bounds;
			var wasWhole = current.SourceOffsetX == 0 && current.SourceOffsetY == 0
				&& current.Bounds.Width == shownWidth && current.Bounds.Height == shownHeight;
			var remainsWhole = bounds.Width == shownWidth && bounds.Height == shownHeight
				&& bounds.X >= output.X && bounds.Y >= output.Y
				&& bounds.Right <= output.Right && bounds.Bottom <= output.Bottom;

			if (!wasWhole || !remainsWhole)
				return false;

			moved = new Wl.WaylandLayerShellClient.OutputSegment(current.Output, bounds, 0, 0);
			return true;
		}

		public bool TryHide()
		{
			// Tearing down our own Wayland SHM surface is local and synchronous, so it either succeeds or throws.
			var success = true;

			foreach (var fragment in fragments.Values)
				try { fragment.Overlay.Dispose(); }
				catch { success = false; }

			fragments.Clear();
			shownW = shownH = 0;
			return success;
		}

		public void Dispose() => _ = TryHide();

		private static Rectangle SourcePixels(Bitmap image, ScreenRect whole,
			Wl.WaylandLayerShellClient.OutputSegment segment)
		{
			var source = whole.ScreenToPixelBounds(segment.Bounds, new PixelSize(image.Width, image.Height));

			// An authored image can be smaller than the number of output fragments it spans. Such a fragment still
			// needs one source pixel; duplicating the boundary sample is the only representable nearest-neighbour result.
			if (source.Width == 0)
				source = new Rectangle(Math.Min(source.X, image.Width - 1), source.Y, 1, source.Height);

			if (source.Height == 0)
				source = new Rectangle(source.X, Math.Min(source.Y, image.Height - 1), source.Width, 1);

			return source;
		}
	}

	// Compositor-extension backing (GNOME/Cinnamon): hands the pixels to the shell as a PNG. A move asks the shell
	// to reposition the already-uploaded actor (no re-encode); only when that fast path is unavailable - an older
	// extension, or the actor was dropped - does it fall back to re-encoding and re-sending the current image.
	internal sealed class CompositorImageBacking : IImageOverlayBacking
	{
		private readonly uint id;
		private bool shown;
		private bool hidden;

		internal CompositorImageBacking(uint id) => this.id = id;

		public nint Handle => 0;

		public bool Show(Bitmap image, ScreenRect bounds, bool clickThrough)
		{
			// Compositor-extension actors are passive. Returning false selects the interactive Eto backing instead of
			// falsely claiming that this surface can receive input.
			if (!clickThrough)
				return false;

			try
			{
				// ToPngBytes reads `image` into a PNG (Eto ToByteArray / GDI Save) which is what gets uploaded;
				// `image` itself is never retained or disposed (borrow contract). ToPngBytes / the D-Bus call
				// can throw -- return false without touching `image` on every path.
				var bytes = ImageHelper.ToPngBytes(image);

				if (bytes.Length == 0)
					return false;

				var result = Wl.WaylandBackend.Current?.TryShowImageOverlay(id, bounds.X, bounds.Y, bounds.Width, bounds.Height, bytes)
							 ?? Wl.OverlayShowResult.Failed;

				// A DEFINITIVE Failed means the extension is absent or rejected the call -- report failure so the
				// LinuxImageOverlayBacking wrapper falls back to a (visible) Eto window. A TIMEOUT is NOT a failure:
				// the shell almost certainly still received this (cold, large) upload and created the actor, so we
				// commit to the compositor -- mark it shown (the actor exists, or will momentarily, and the next
				// Show/Move updates it in place) rather than spawning a duplicate Eto window for the same overlay.
				if (result == Wl.OverlayShowResult.Failed)
					return false;

				shown = true;
				hidden = false;
				return true;
			}
			catch
			{
				return false;
			}
		}

		public bool Move(ScreenRect bounds)
		{
			// Byte-free reposition: the compositor keeps the pixels we already uploaded and just moves the actor.
			// If that fast path is unavailable, return false so the overlay re-renders via Show.
			return shown && Wl.WaylandBackend.Current?.TryMoveImageOverlay(id, bounds.X, bounds.Y, bounds.Width, bounds.Height) == true;
		}

		public bool TryHide()
		{
			// Always ask the shell to drop the actor, even when our Show timed out (shown == false): the shell may
			// still have created it, and HideImageOverlay is a no-op for an unknown id, so an unconditional hide
			// reaps a possibly-orphaned actor without risk (the alternative left it on screen until process-death).
			if (hidden)
				return true;

			try
			{
				var backend = Wl.WaylandBackend.Current;

				// No backend, or the extension is gone (disabled/uninstalled mid-session): the shell has already
				// reaped its actors on disable, so there is nothing left to withdraw. Treat that as a CONFIRMED hide
				// and reclaim the backing -- otherwise SendHideImageOverlay returns false (no owner to ack) and the
				// id is kept mapped and retried forever, leaking backings for the rest of the process lifetime.
				if (backend == null || !backend.SupportsImageOverlay)
				{
					hidden = true;
					return true;
				}

				// A definitive ack (actor removed, or unknown id) confirms the withdraw. A dropped / timed-out call
				// returns false so the caller keeps us mapped and retries -- that is what stops a lost hide from
				// orphaning the actor for good.
				if (backend.TryHideImageOverlay(id))
				{
					hidden = true;
					return true;
				}

				return false;
			}
			catch { return false; }
		}

		public void Dispose() => _ = TryHide();
	}
}
#endif
