namespace Keysharp.Internals
{
	/// <summary>Flattens native per-display captures into the single linearly-mapped bitmap promised by IScreen.</summary>
	internal static class ScreenCaptureComposer
	{
		/// <remarks>The method consumes a sole full-rectangle bitmap by removing it from <paramref name="captures"/>.
		/// Otherwise the list retains ownership and the caller disposes every source in its usual finally block.</remarks>
		internal static Bitmap Compose(ScreenRect requested, List<(ScreenRect Bounds, Bitmap Pixels)> captures)
		{
			if (!requested.HasArea || captures == null || captures.Count == 0)
				return null;

			if (captures.Count == 1 && captures[0].Bounds == requested)
			{
				var direct = captures[0].Pixels;
				captures.Clear();
				return direct;
			}

			var scaleX = captures.Max(c => (double)c.Pixels.Width / c.Bounds.Width);
			var scaleY = captures.Max(c => (double)c.Pixels.Height / c.Bounds.Height);
			var width = Math.Round(requested.Width * scaleX);
			var height = Math.Round(requested.Height * scaleY);

			if (!double.IsFinite(width) || !double.IsFinite(height)
					|| width < 1 || height < 1 || width > int.MaxValue || height > int.MaxValue)
				return null;

			var pixels = new PixelSize((int)width, (int)height);
			var result = ImageHelper.NewArgbCanvas(pixels.Width, pixels.Height);

			try
			{
				using var graphics = ImageHelper.MakeGraphics(result, highQuality: false);

				foreach (var capture in captures)
				{
					var destination = requested.ScreenToPixelBounds(capture.Bounds, pixels);

					if (destination.Width > 0 && destination.Height > 0)
						graphics.DrawImage(capture.Pixels, new RectangleF(destination.X, destination.Y,
							destination.Width, destination.Height));
				}

				return result;
			}
			catch
			{
				result.Dispose();
				return null;
			}
		}
	}
}
