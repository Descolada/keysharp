namespace Keysharp.Builtins
{
	public partial class Ks
	{
		/// <summary>
		/// A lightweight, caller-owned screen-border overlay for debugging and visualization: outline any screen
		/// rectangle with a colored, click-through border. Construct one, then drive it with
		/// <c>Show</c>/<c>Move</c>/<c>Hide</c>/<c>Destroy</c>. The caller owns the object (like a ToolTip slot —
		/// there is no global registry); calling <c>Destroy</c>, or dropping all references (the overlay is then
		/// torn down on garbage collection via <c>__Delete</c>), frees the window.
		///
		/// <para>Internally it is a single always-on-top, click-through, transparent <c>Gui</c> whose border is
		/// drawn as four colored edge controls (top/bottom/left/right). The window is created lazily on the first
		/// <c>Show</c> and then REUSED: <c>Show</c>/<c>Move</c>/<c>Hide</c> and resizes/recolors update the window
		/// and its four edge controls in place — they never tear the window down (only <c>Destroy</c> does). This
		/// keeps it from churning top-level windows (which matters on Wayland/KWin).</para>
		///
		/// <para>Coordinates are absolute screen pixels and the border is drawn just OUTSIDE the rectangle (so it
		/// frames, rather than covers, the target). DPI scaling is disabled, matching the screen-pixel
		/// coordinates that callers such as OCR/Ax/AtSpi produce.</para>
		///
		/// <code>
		/// hl := Highlight(100, 100, 200, 50)   ; build (not shown yet)
		/// hl.Show()                            ; outline (100,100,200,50), non-blocking
		/// hl.Move(140, 160)                    ; reposition without recreating the window
		/// hl.Show(300, 300, 120, 120)          ; resize in place (no teardown)
		/// hl.Color := "Lime"                   ; recolor in place
		/// hl.Hide()                            ; keep the window for the next Show
		/// hl.Destroy()                         ; free it
		///
		/// h := Highlight()                     ; or construct empty and supply geometry/style at Show time
		/// h.Show(100, 100, 200, 200)
		/// </code>
		/// </summary>
		[UserDeclaredName("Highlight")]
		public class KeysharpHighlight : KeysharpObject
		{
			// The single reusable overlay window (null until the first Show, and after Destroy) and its four
			// colored edge controls (top/bottom/left/right), kept so they can be recolored/resized in place.
			private Gui gui;
			private Gui.Control top, bottom, left, right;

			// The requested target rectangle (screen pixels) and border style. `color` is stored normalized to a
			// 6-hex-digit string (like Gui.BackColor): a name/number set on it reads back as e.g. "FF0000".
			private int rx, ry, rw, rh, thickness = 2;
			private string color = "FF0000";

			// Signature of the edges currently built into `gui`, so Refresh can tell a pure move (reuse as-is)
			// from a resize (reshape the edges) from a recolor (repaint the edges) — without ever rebuilding.
			private int builtW = int.MinValue, builtH = int.MinValue, builtD = int.MinValue;
			private string builtColor = "";

			// visible = caller intent (Show issued, no intervening Hide/Destroy); shown = window actually mapped.
			private bool visible, shown;

			public KeysharpHighlight(params object[] args) : base(args) { }

			/// <summary>Highlight(x?, y?, w?, h?, color := "Red", thickness := 2) — stores the rectangle/style; no
			/// window is created until the first Show.</summary>
			public override object __New(params object[] args)
			{
				if (args != null)
				{
					if (args.Length > 0 && args[0] != null) rx = args[0].Ai();
					if (args.Length > 1 && args[1] != null) ry = args[1].Ai();
					if (args.Length > 2 && args[2] != null) rw = args[2].Ai();
					if (args.Length > 3 && args[3] != null) rh = args[3].Ai();
					if (args.Length > 4 && args[4] != null) color = NormalizeColor(args[4]);
					if (args.Length > 5 && args[5] != null) thickness = Math.Max(0, args[5].Ai());
				}

				return DefaultObject;
			}

			#region Properties

			/// <summary>Left edge of the outlined rectangle, in screen pixels. Updates live while shown.</summary>
			public object X { get => (long)rx; set { rx = value.Ai(); Refresh(); } }

			/// <summary>Top edge of the outlined rectangle, in screen pixels. Updates live while shown.</summary>
			public object Y { get => (long)ry; set { ry = value.Ai(); Refresh(); } }

			/// <summary>Width of the outlined rectangle, in pixels. Updates live while shown.</summary>
			public object W { get => (long)rw; set { rw = value.Ai(); Refresh(); } }

			/// <summary>Height of the outlined rectangle, in pixels. Updates live while shown.</summary>
			public object H { get => (long)rh; set { rh = value.Ai(); Refresh(); } }

			/// <summary>Border color: set with a color name ("Red"), a 0xRRGGBB integer, or a hex string; it is
			/// normalized to and read back as a 6-hex-digit string (e.g. "FF0000"), matching Gui.BackColor. Updates
			/// live while shown.</summary>
			public object Color { get => color; set { color = NormalizeColor(value); Refresh(); } }

			/// <summary>Border thickness in pixels (0 hides the border). Updates live while shown.</summary>
			public object Thickness { get => (long)thickness; set { thickness = Math.Max(0, value.Ai()); Refresh(); } }

			/// <summary>Whether the overlay is currently on screen.</summary>
			public object Visible => shown;

			/// <summary>Native handle of the overlay window (0 before the first Show or after Destroy).</summary>
			public object Hwnd => gui != null ? gui.Hwnd : 0L;

			#endregion

			#region Methods

			/// <summary>
			/// Shows the overlay (creating the window on first use), returning immediately. Optional x/y/w/h set the
			/// rectangle first, so Show doubles as move/resize. The window is reused; a move or resize updates it and
			/// its edge controls in place rather than recreating it.
			/// </summary>
			public object Show(object x = null, object y = null, object w = null, object h = null)
			{
				if (x != null) rx = x.Ai();
				if (y != null) ry = y.Ai();
				if (w != null) rw = w.Ai();
				if (h != null) rh = h.Ai();
				visible = true;
				Refresh();
				return this;
			}

			/// <summary>Repositions/resizes the overlay in place — and only that. Unlike <c>Show</c>, <c>Move</c>
			/// never creates, shows or hides the window and never changes visibility: it updates the stored
			/// rectangle, and if the overlay is currently on screen it repositions it (reshaping the border edges
			/// when the size changes). On a hidden or not-yet-shown overlay it just records the new geometry for the
			/// next <c>Show</c>. All args optional, so <c>Move(, , w, h)</c> resizes only.</summary>
			public object Move(object x = null, object y = null, object w = null, object h = null)
			{
				if (x != null) rx = x.Ai();
				if (y != null) ry = y.Ai();
				if (w != null) rw = w.Ai();
				if (h != null) rh = h.Ai();

				// Touch the window only when it is actually on screen. Refresh's `shown` branch repositions/resizes
				// (and reshapes the edges on a size change) in place without re-showing, and `visible` is left
				// untouched, so Move can never un-hide a hidden overlay or spin one up from nothing.
				if (shown)
					Refresh();

				return this;
			}

			/// <summary>Hides the overlay but keeps the window alive for the next Show.</summary>
			public object Hide()
			{
				visible = false;
				shown = false;

				if (gui != null)
				{
					var g = gui;
					Script.InvokeOnUIThread(() => g.Hide());
				}

				return this;
			}

			/// <summary>Destroys the overlay window and frees its resources. Idempotent; the object can be reused
			/// (a later Show rebuilds it).</summary>
			public object Destroy()
			{
				visible = false;
				shown = false;

				if (gui != null)
				{
					gui.Destroy();
					gui = null;
				}

				top = bottom = left = right = null;
				builtW = builtH = builtD = int.MinValue;
				builtColor = "";
				return DefaultObject;
			}

			// __Delete is invoked by the destructor pump on the main thread when this object is collected, so a
			// caller that just drops the overlay still gets its window freed (no explicit Destroy required).
			public override object __Delete() => Destroy();

			#endregion

			// Applies the current rectangle/style, reusing the window. The first call (gui == null) builds it; every
			// later call moves/resizes the window and reshapes/recolors the four edge controls IN PLACE — no
			// teardown. A no-op when hidden; hides (without destroying) when the rectangle is empty. All Gui access
			// is marshaled to the UI thread because KeysharpHighlight is not a Gui type, so script calls into it are
			// not auto-marshaled the way direct Gui calls are.
			private void Refresh()
			{
				if (!visible)
					return;

				if (rw < 1 || rh < 1)
				{
					shown = false;

					if (gui != null)
					{
						var g = gui;
						Script.InvokeOnUIThread(() => g.Hide());
					}

					return;
				}

				var d = thickness;
				int bw = rw + 2 * d, bh = rh + 2 * d, bx = rx - d, by = ry - d;
				var c = color;

				Script.InvokeOnUIThread(() =>
				{
					if (gui == null)
						Build();

					// Reshape the four edges in place on a size/thickness change.
					bool resized = bw != builtW || bh != builtH || d != builtD;

					if (resized)
						ReshapeEdges(bw, bh, d);

					builtW = bw;
					builtH = bh;
					builtD = d;

					// Position/size the window (reusing it). "NA" keeps the overlay from stealing focus on first
					// show; once shown, Move repositions/resizes in place without re-parsing an option string.
					if (shown)
						_ = gui.Move(bx, by, bw, bh);
					else
					{
						_ = gui.Show($"NA x{bx} y{by} w{bw} h{bh}");
						shown = true;
					}

#if WINDOWS
					// Carve the interior out of the window (after it has its final size) so the middle is a real
					// hole — physically not part of the window, hence never painted and never hit-tested. This makes
					// the inside reliably transparent and click-through regardless of layered-window/color-key repaint
					// quirks, double-buffering, or HiDPI scaling, any of which could otherwise leave it opaque
					// (black/white) after a resize. Only the d-thick border ring (the edge controls) remains.
					if (resized)
						ApplyFrameRegion(bw, bh, d);
#endif

					// Color the edges AFTER the window is shown: a control's BackColor only repaints once its parent
					// window is visible, so coloring before the show above would leave the first frame uncolored.
					if (c != builtColor)
						Recolor(c);

					builtColor = c;
				});
			}

			// Creates the transparent, click-through overlay window with the border drawn as four colored edge
			// controls. Mirrors the single-window technique proven in the OCR/Ax/AtSpi libraries (one top-level
			// window, not four), which keeps compositors — notably KWin on Wayland — stable. Runs only on first
			// Show (or after Destroy); resizes/recolors thereafter reuse the controls.
			private void Build()
			{
				const string opts = "+AlwaysOnTop -Caption +ToolWindow -DPIScale +ClickThrough";
				// HiDPI Windows: -DPIScale now also forces the form to AutoScaleMode.None (see the "DPIScale" Gui
				// option handler), so WinForms no longer rescales this overlay's client/edge controls at 150%/200%.
				// Without that, raw-pixel positioning fought WinForms' 2x scaling and a resize left the middle
				// unpainted (black/white/opaque, breaking click-through). The fix lives at the -DPIScale layer so
				// every -DPIScale Gui benefits, not just here. Eto/macOS are unaffected.
				// Pass an explicit object[] so this binds to Gui(params object[]) — which runs __Init/__New and
				// builds a real window. A single bare arg (new Gui(opts)) would instead resolve to the internal
				// Gui(object, object, object, object) main-window-wrapper ctor and create a broken, form-less Gui.
				var g = new Gui(new object[] { opts });

				try
				{
					// An 8-digit-hex STRING (alpha byte 0) is the documented transparent-background form; using a
					// non-white key (magenta) means a white/0xFFFFFF border isn't keyed out on Windows. Must be set
					// before the controls/Show so the backend picks a per-pixel-alpha visual at realize time.
					g.BackColor = "0x00FF00FF";
					top = EdgeControl(g);
					bottom = EdgeControl(g);
					left = EdgeControl(g);
					right = EdgeControl(g);
				}
				catch
				{
					// Reclaim the already-constructed (registered, off-screen) window so a failed Build can't leak it.
					try { _ = g.Destroy(); } catch { }
					top = bottom = left = right = null;
					throw;
				}

				gui = g;
				shown = false;
				// Force the first Refresh after Build to size (ReshapeEdges) and color (Recolor) the fresh controls.
				builtW = builtH = builtD = int.MinValue;
				builtColor = "";
			}

			// Adds one zero-size, uncolored Text control (sized by ReshapeEdges and colored by Recolor — the latter
			// after the window is shown, since a control's BackColor only paints once its parent window is visible).
			private static Gui.Control EdgeControl(Gui g) => (Gui.Control)g.Add("Text", "x0 y0 w0 h0");

			// Lays the four edges out for a border box of (bw x bh) with thickness d: top/bottom span the full width,
			// left/right fill the gap between them (inner height bh - 2d).
			private void ReshapeEdges(int bw, int bh, int d)
			{
				SetEdge(top, 0, 0, bw, d);
				SetEdge(bottom, 0, bh - d, bw, d);
				SetEdge(left, 0, d, d, bh - 2 * d);
				SetEdge(right, bw - d, d, d, bh - 2 * d);
			}

			// Sets one edge control's bounds within the window, in raw pixels (the overlay disables DPI scaling).
			private void SetEdge(Gui.Control c, int x, int y, int w, int h)
			{
				var nc = c.Ctrl;
#if WINDOWS
				nc.SetBounds(x, y, w, h);
#else
				// Match how the Gui itself positions/sizes controls in its PixelLayout (EtoExtensions): the static
				// PixelLayout.SetLocation attached-property setter, plus the control's own Size.
				PixelLayout.SetLocation(nc, new Point(x, y));
				nc.Size = new Size(w, h);
#endif
			}

			// Repaints the four edges with a new color, in place (no rebuild).
			private void Recolor(string c)
			{
				top.BackColor = c;
				bottom.BackColor = c;
				left.BackColor = c;
				right.BackColor = c;
			}

#if WINDOWS
			// Sets the window region to the d-thick border ring (outer rect minus interior), turning the middle into
			// a true hole. Re-applied on every resize because the region is in window pixels. SetWindowRgn takes
			// ownership of the region handle (and frees the previous one), so the combined region is not deleted here;
			// only the temporary interior region is. A degenerate size (no room for a hole) clears the region.
			private void ApplyFrameRegion(int bw, int bh, int d)
			{
				var hwnd = gui != null ? gui.form.Handle : 0;

				if (hwnd == 0)
					return;

				if (d <= 0 || bw <= 2 * d || bh <= 2 * d)
				{
					_ = WindowsAPI.SetWindowRgn(hwnd, 0, true);   // whole window, no hole
					return;
				}

				var outer = WindowsAPI.CreateRectRgn(0, 0, bw, bh);
				var inner = WindowsAPI.CreateRectRgn(d, d, bw - d, bh - d);
				_ = WindowsAPI.CombineRgn(outer, outer, inner, 4 /* RGN_DIFF */);
				_ = WindowsAPI.DeleteObject(inner);
				_ = WindowsAPI.SetWindowRgn(hwnd, outer, true);
			}
#endif

			// Normalizes a color (a name like "Red", a 0xRRGGBB integer, or a "#RRGGBB"/"0xRRGGBB"/bare-hex string)
			// to the canonical 6-hex-digit string, matching how Gui.BackColor reports colors (Color := "Red" reads
			// back "FF0000"). Anything unparseable defaults to red.
			private static string NormalizeColor(object color)
			{
				if (color is string s && Conversions.TryParseColor(s, out var c))
					return (c.ToArgb() & 0xFFFFFF).ToString("X6");

				if (color is long || color is int || color is double)
					return (color.Al() & 0xFFFFFF).ToString("X6");

				return "FF0000";
			}
		}
	}
}
