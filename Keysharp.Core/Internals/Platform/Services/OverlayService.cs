namespace Keysharp.Internals
{
	/// <summary>
	/// One click-through image overlay's platform backing. Ownership rule: <see cref="Show"/> BORROWS the bitmap
	/// it is handed -- it copies only what it needs to display (and to satisfy same-size moves), synchronously,
	/// and must NOT store, retain a reference to, or dispose the passed bitmap; the caller keeps ownership.
	/// <see cref="Move"/> repositions the last shown content as cheaply as the backing can; if it cannot satisfy
	/// the request without new pixels (e.g. a resize), it returns false and the caller re-renders via <see cref="Show"/>.
	/// </summary>
	internal interface IImageOverlayBacking : IDisposable
	{
		/// <summary><paramref name="clickThrough"/> true (the default for a passive HUD/highlight) makes the surface
		/// transparent to mouse input; false makes it RECEIVE mouse input, where the backing supports an interactive
		/// mode (Windows layered form, Eto window, or a layer surface with its default input region). A backing which
		/// cannot be interactive must return false so selection can fall back to one which can.</summary>
		bool Show(Bitmap image, ScreenRect bounds, bool clickThrough);
		bool Move(ScreenRect bounds);

		/// <summary>
		/// Withdraw the on-screen surface. Returns true iff it is CONFIRMED gone (so the caller may forget the id);
		/// false means the withdraw could not be confirmed -- e.g. a dropped / timed-out compositor call -- and the
		/// caller must keep the backing mapped so a later Hide can re-attempt rather than orphaning the surface.
		/// Must be idempotent: a call after a successful withdraw returns true without doing more work.
		/// </summary>
		bool TryHide();

		nint Handle { get; }
	}

	/// <summary>
	/// Platform-neutral overlay service: owns the id-to-backing map, its lock, and the show/move/hide/hide-all
	/// orchestration, all in terms of one abstract per-platform <see cref="IImageOverlayBacking"/>. Highlight,
	/// ToolTip (Linux/macOS) and the user-facing Overlay builtin all render through this single image primitive,
	/// so there is no separate highlight/tooltip surface here. The map lock protects membership; each slot has its
	/// own gate so native calls for one id are ordered without blocking unrelated overlays.
	/// </summary>
	internal abstract class OverlayBase : IOverlay
	{
		private sealed class OverlaySlot
		{
			internal readonly object Gate = new();
			internal readonly IImageOverlayBacking Backing;
			internal bool Retired;

			internal OverlaySlot(IImageOverlayBacking backing) => Backing = backing;
		}

		private readonly object sync = new ();
		private readonly Dictionary<uint, OverlaySlot> overlays = new ();

		public abstract PixelSize GetCanvasSize(ScreenRect bounds);

		/// <summary>Create the backing for a new overlay id (called under the map lock; must not do UI/IO work).</summary>
		protected abstract IImageOverlayBacking CreateBacking(uint id);

		public bool TryShowImageOverlay(uint id, ScreenRect bounds, Bitmap image, bool clickThrough)
		{
			if (id == 0 || image == null)
				return false;

			if (bounds.Width <= 0) bounds = bounds with { Width = image.Width };
			if (bounds.Height <= 0) bounds = bounds with { Height = image.Height };

			if (!bounds.HasArea)
				return TryHideImageOverlay(id);   // nothing to show; the caller still owns `image`

			OverlaySlot slot;
			var created = false;

			lock (sync)
			{
				if (!overlays.TryGetValue(id, out slot))
				{
					overlays[id] = slot = new OverlaySlot(CreateBacking(id));
					created = true;
				}
			}

			// Outside the lock (may hit the UI thread / D-Bus). The backing BORROWS `image` -- it copies what it
			// needs, synchronously, and never stores or disposes it. The caller (the Overlay) keeps ownership of
			// its canvas bitmap, so there is nothing to clean up here on either path.
			lock (slot.Gate)
			{
				if (!IsCurrent(id, slot))
					return false;

				if (slot.Backing.Show(image, bounds, clickThrough))
					return IsCurrent(id, slot);

				if (created)
				{
					try { slot.Backing.Dispose(); } catch { }
					Retire(id, slot);
				}

				return false;
			}
		}

		public bool TryMoveImageOverlay(uint id, ScreenRect bounds)
		{
			OverlaySlot slot;

			lock (sync)
			{
				if (!overlays.TryGetValue(id, out slot))
					return false;   // no surface exists to move; the caller must recreate it with Show
			}

			lock (slot.Gate)
			{
				if (!IsCurrent(id, slot) || !slot.Backing.Move(bounds))
					return false;

				return IsCurrent(id, slot);
			}
		}

		public bool TryHideImageOverlay(uint id)
		{
			OverlaySlot slot;

			lock (sync)
			{
				if (!overlays.TryGetValue(id, out slot))
					return true;   // already absent is a confirmed, idempotent hide
			}

			// Verify the withdraw BEFORE forgetting the id. If the surface can't be confirmed gone (a dropped or
			// timed-out compositor hide), keep the backing mapped so a later Hide re-attempts -- dropping the id here
			// while the surface is still painted is exactly what turned a transient hiccup into a permanent orphan.
			lock (slot.Gate)
			{
				if (!IsCurrent(id, slot))
					return true;

				if (!slot.Backing.TryHide())
					return false;

				Retire(id, slot);
				return true;
			}
		}

		public void DisposeImageOverlay(uint id)
		{
			OverlaySlot slot;

			// Unconditional force-reap (no confirm-gating): remove the backing from the map, then dispose it outside
			// the lock. This is Destroy's escape hatch for a backing whose confirm-gated TryHide never succeeded -- it
			// must not be left mapped forever with no owner to retry the withdraw.
			lock (sync)
			{
				if (!overlays.TryGetValue(id, out slot))
					return;
			}

			lock (slot.Gate)
			{
				if (!IsCurrent(id, slot))
					return;

				try { slot.Backing.Dispose(); } catch { }
				Retire(id, slot);
			}
		}

		public bool TryHideAllImageOverlays()
		{
			OverlaySlot[] all;

			// Clearing the map is HideAll's linearization point. Show and Move recheck membership after their native
			// call, so an operation already holding a slot gate cannot report success after this point.
			lock (sync)
			{
				if (overlays.Count == 0)
					return false;

				all = overlays.Values.ToArray();

				foreach (var slot in all)
					slot.Retired = true;

				overlays.Clear();
			}

			foreach (var slot in all)
			{
				lock (slot.Gate)
					try { slot.Backing.Dispose(); } catch { }
			}

			return true;
		}

		public nint GetImageOverlayHandle(uint id)
		{
			OverlaySlot slot;

			lock (sync)
				if (!overlays.TryGetValue(id, out slot))
					return 0;

			lock (slot.Gate)
				return IsCurrent(id, slot) ? slot.Backing.Handle : 0;
		}

		private bool IsCurrent(uint id, OverlaySlot slot)
		{
			lock (sync)
				return !slot.Retired && overlays.TryGetValue(id, out var current)
					&& ReferenceEquals(current, slot);
		}

		private void Retire(uint id, OverlaySlot slot)
		{
			lock (sync)
			{
				slot.Retired = true;

				if (overlays.TryGetValue(id, out var current) && ReferenceEquals(current, slot))
					_ = overlays.Remove(id);
			}
		}
	}
}
