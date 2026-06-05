using System.Runtime.InteropServices;

#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Minimal Cairo + Pango bindings used to rasterise tooltip text directly into a wl_shm
	/// buffer. We choose Cairo because its <c>CAIRO_FORMAT_ARGB32</c> pixel layout (BGRA on
	/// little-endian, premultiplied alpha) exactly matches <c>WL_SHM_FORMAT_ARGB8888</c>, so we
	/// can hand it the SHM mapping and skip any intermediate copy. libcairo, libpango and
	/// libpangocairo are bundled with every GTK install (which Eto already requires on Linux),
	/// so no new runtime dependency is introduced.
	/// </summary>
	internal static class CairoText
	{
		private const string LibCairo = "libcairo.so.2";
		private const string LibPango = "libpango-1.0.so.0";
		private const string LibPangoCairo = "libpangocairo-1.0.so.0";
		private const string LibGObject = "libgobject-2.0.so.0";

		internal const int CairoFormatArgb32 = 0;

		// PANGO_SCALE = 1024; pango uses scaled integer units throughout.
		internal const int PangoScale = 1024;

		[DllImport(LibCairo, EntryPoint = "cairo_image_surface_create_for_data")]
		internal static extern nint ImageSurfaceCreateForData(nint data, int format, int width, int height, int stride);

		[DllImport(LibCairo, EntryPoint = "cairo_create")]
		internal static extern nint Create(nint surface);

		[DllImport(LibCairo, EntryPoint = "cairo_destroy")]
		internal static extern void Destroy(nint cr);

		[DllImport(LibCairo, EntryPoint = "cairo_surface_destroy")]
		internal static extern void SurfaceDestroy(nint surface);

		[DllImport(LibCairo, EntryPoint = "cairo_surface_flush")]
		internal static extern void SurfaceFlush(nint surface);

		[DllImport(LibCairo, EntryPoint = "cairo_set_source_rgba")]
		internal static extern void SetSourceRgba(nint cr, double r, double g, double b, double a);

		[DllImport(LibCairo, EntryPoint = "cairo_set_operator")]
		internal static extern void SetOperator(nint cr, int op);

		internal const int CairoOperatorSource = 1;
		internal const int CairoOperatorOver = 2;

		[DllImport(LibCairo, EntryPoint = "cairo_rectangle")]
		internal static extern void Rectangle(nint cr, double x, double y, double width, double height);

		[DllImport(LibCairo, EntryPoint = "cairo_fill")]
		internal static extern void Fill(nint cr);

		[DllImport(LibCairo, EntryPoint = "cairo_stroke")]
		internal static extern void Stroke(nint cr);

		[DllImport(LibCairo, EntryPoint = "cairo_set_line_width")]
		internal static extern void SetLineWidth(nint cr, double width);

		[DllImport(LibCairo, EntryPoint = "cairo_move_to")]
		internal static extern void MoveTo(nint cr, double x, double y);

		[DllImport(LibCairo, EntryPoint = "cairo_paint")]
		internal static extern void Paint(nint cr);

		[DllImport(LibPangoCairo, EntryPoint = "pango_cairo_create_layout")]
		internal static extern nint PangoCairoCreateLayout(nint cr);

		[DllImport(LibPangoCairo, EntryPoint = "pango_cairo_show_layout")]
		internal static extern void PangoCairoShowLayout(nint cr, nint layout);

		[DllImport(LibPango, EntryPoint = "pango_layout_set_text")]
		internal static extern void PangoLayoutSetText(nint layout, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int length);

		[DllImport(LibPango, EntryPoint = "pango_layout_set_font_description")]
		internal static extern void PangoLayoutSetFontDescription(nint layout, nint desc);

		[DllImport(LibPango, EntryPoint = "pango_layout_get_pixel_size")]
		internal static extern void PangoLayoutGetPixelSize(nint layout, out int width, out int height);

		[DllImport(LibPango, EntryPoint = "pango_font_description_from_string")]
		internal static extern nint PangoFontDescriptionFromString([MarshalAs(UnmanagedType.LPUTF8Str)] string str);

		[DllImport(LibPango, EntryPoint = "pango_font_description_free")]
		internal static extern void PangoFontDescriptionFree(nint desc);

		[DllImport(LibGObject, EntryPoint = "g_object_unref")]
		internal static extern void GObjectUnref(nint obj);

		/// <summary>
		/// Performs a synchronous probe of the Cairo/Pango libraries. Returns true if the loader
		/// can resolve <c>cairo_create</c>; if it can't, the layer-shell tooltip is unavailable
		/// and the caller should fall back to the WinForms tooltip path.
		/// </summary>
		internal static bool TryProbe()
		{
			try
			{
				if (!NativeLibrary.TryLoad(LibCairo, out var cairoHandle))
					return false;

				_ = cairoHandle; // we don't release the handle; subsequent P/Invokes reuse it
				return NativeLibrary.TryLoad(LibPangoCairo, out _) && NativeLibrary.TryLoad(LibPango, out _) && NativeLibrary.TryLoad(LibGObject, out _);
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Measures the rendered pixel size of <paramref name="text"/> for the given font, without
		/// performing any drawing. Used to size the SHM buffer before allocating it.
		/// </summary>
		internal static (int Width, int Height) Measure(string text, string fontDescription)
		{
			if (string.IsNullOrEmpty(text))
				return (0, 0);

			// Create a 1x1 stub surface; layout sizing is independent of the surface dimensions.
			var stubData = Marshal.AllocHGlobal(4);

			try
			{
				var surface = ImageSurfaceCreateForData(stubData, CairoFormatArgb32, 1, 1, 4);
				var cr = Create(surface);
				var fontDesc = PangoFontDescriptionFromString(fontDescription);
				var layout = PangoCairoCreateLayout(cr);
				PangoLayoutSetFontDescription(layout, fontDesc);
				PangoLayoutSetText(layout, text, -1);
				PangoLayoutGetPixelSize(layout, out var w, out var h);
				GObjectUnref(layout);
				PangoFontDescriptionFree(fontDesc);
				Destroy(cr);
				SurfaceDestroy(surface);
				return (w, h);
			}
			finally
			{
				Marshal.FreeHGlobal(stubData);
			}
		}

		/// <summary>
		/// Renders a tooltip into <paramref name="bufferData"/>: a rounded rectangle background,
		/// a 1-pixel border, and the text centred inside the padding. The pixel layout written is
		/// ARGB32 (BGRA on little-endian, premultiplied alpha) — directly compatible with
		/// WL_SHM_FORMAT_ARGB8888. <paramref name="width"/>, <paramref name="height"/> and
		/// <paramref name="stride"/> describe the SHM buffer; <paramref name="textPad"/> is the
		/// padding inside the border in pixels.
		/// </summary>
		internal static void RenderTooltip(nint bufferData, int width, int height, int stride,
			string text, string fontDescription, int textPad)
		{
			var surface = ImageSurfaceCreateForData(bufferData, CairoFormatArgb32, width, height, stride);
			var cr = Create(surface);

			try
			{
				// Solid background — pale yellow, the classic tooltip colour.
				SetOperator(cr, CairoOperatorSource);
				SetSourceRgba(cr, 1.0, 1.0, 0.88, 1.0);
				Rectangle(cr, 0, 0, width, height);
				Fill(cr);

				// 1-pixel dark border just inside the surface edge.
				SetOperator(cr, CairoOperatorOver);
				SetSourceRgba(cr, 0.0, 0.0, 0.0, 1.0);
				SetLineWidth(cr, 1.0);
				Rectangle(cr, 0.5, 0.5, width - 1, height - 1);
				Stroke(cr);

				// Black text inside the padding.
				if (!string.IsNullOrEmpty(text))
				{
					var fontDesc = PangoFontDescriptionFromString(fontDescription);
					var layout = PangoCairoCreateLayout(cr);

					try
					{
						PangoLayoutSetFontDescription(layout, fontDesc);
						PangoLayoutSetText(layout, text, -1);
						MoveTo(cr, textPad, textPad);
						SetSourceRgba(cr, 0.0, 0.0, 0.0, 1.0);
						PangoCairoShowLayout(cr, layout);
					}
					finally
					{
						GObjectUnref(layout);
						PangoFontDescriptionFree(fontDesc);
					}
				}

				SurfaceFlush(surface);
			}
			finally
			{
				Destroy(cr);
				SurfaceDestroy(surface);
			}
		}
	}
}
#endif
