using System.Numerics;
using System.Runtime.Intrinsics;
using Keysharp.Builtins;
namespace Keysharp.Internals.Images
{
	/// <summary>
	/// Class which provides common search Methods to find a Color or a subimage in given Image.
	/// </summary>
	internal class ImageFinder
	{
		private readonly Bitmap sourceImage;
		private readonly int threads = Environment.ProcessorCount;

		internal byte Variation { get; set; }

		/// <summary>
		/// Creates a new Image Finder Instance
		/// </summary>
		/// <param name="source">Source Image where to search in</param>
		internal ImageFinder(Bitmap source) => sourceImage = source;

		internal Point? Find(Bitmap findImage, long trans = -1)
		{
#if WINDOWS
			BitmapData srcdata = null, fnddata = null;
#endif
			if (sourceImage == null || findImage == null)
				throw new InvalidOperationException();

			Point? ret = null;
			var sourceRect = new Rectangle(new Point(0, 0), sourceImage.Size);
			var needleRect = new Rectangle(new Point(0, 0), findImage.Size);
			var transCol = new FastColor();

			if (trans != -1)
			{
				transCol.A = 255;
				transCol.R = (byte)((trans & 0xFF0000) >> 16);//The format comes in as RGB, so we must individually break out the components.
				transCol.G = (byte)((trans & 0x00FF00) >> 8);
				transCol.B = (byte)(trans & 0x0000FF);
			}

			if (sourceRect.Contains(needleRect))
			{
				var maxMovement = new Size(sourceImage.Size.Width - needleRect.Size.Width, sourceImage.Size.Height - needleRect.Size.Height);

				try
				{
#if WINDOWS
					var srcColor = new FastColor();
					var fndColor = new FastColor();
					srcdata = sourceImage.LockBits(new Rectangle(0, 0, sourceImage.Width, sourceImage.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
					fnddata = findImage.LockBits(new Rectangle(0, 0, findImage.Width, findImage.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
					unsafe
					{
						var ptrFirstSrcPixel = (byte*)srcdata.Scan0;
						var ptrFirstFndPixel = (byte*)fnddata.Scan0;
						var srcBytesPerPixel = Image.GetPixelFormatSize(sourceImage.PixelFormat) / 8;
						var findBytesPerPixel = Image.GetPixelFormatSize(findImage.PixelFormat) / 8;
						var srcWidthInBytes = maxMovement.Width * srcBytesPerPixel;
						var fndWidthInBytes = fnddata.Width * findBytesPerPixel;
						//This cannot be parallelized because the region must be searched sequentially from top to bottom, left to right.
						//If there are multiple matches, the first one encountered is supposed to be the one returned.
						for (var row = 0; row < maxMovement.Height; row++)
						{
							for (var col = 0; col < srcWidthInBytes; col += srcBytesPerPixel)
							{
								for (var destRow = 0; destRow < findImage.Size.Height; destRow++)
								{
									var currentSrcLine = ptrFirstSrcPixel + ((row + destRow) * srcdata.Stride) + col;//Add col here just so it doesn't have to get repeatedly added below.
									var currentFndLine = ptrFirstFndPixel + (destRow * fnddata.Stride);

									for (var destCol = 0; destCol < fndWidthInBytes; destCol += findBytesPerPixel)
									{
										srcColor.Value = ((uint*)(currentSrcLine + destCol))[0];
										fndColor.Value = ((uint*)(currentFndLine + destCol))[0];
										//var cdc = col + destCol;
										//srcColor.B = currentLine[cdc];
										//srcColor.G = currentLine[cdc + 1];
										//srcColor.R = currentLine[cdc + 2];
										//srcColor.A = currentLine[cdc + 3];
										//fndColor.B = currentFndLine[destCol];
										//fndColor.G = currentFndLine[destCol + 1];
										//fndColor.R = currentFndLine[destCol + 2];
										//fndColor.A = currentFndLine[destCol + 3];

										if (trans != -1 && fndColor.Value == transCol.Value)
											continue;

										if (!fndColor.CompareWithVar(srcColor, Variation))
											goto NOFIND;
									}
								}

								ret = new Point(col / srcBytesPerPixel, row);
								return ret;
								NOFIND:
								;
							}
						}
					}
#else
					// Force both bitmaps to 32bpp before the search so the per-pixel
					// 4-byte int reads below are valid regardless of the original storage
					// format (Pixbuf is 3bpp for 24-bit images, 4bpp otherwise). Matches
					// AutoHotkey's getbits()-via-GetDIBits step.
					var srcBitmap = Keysharp.Internals.Images.ImageHelper.EnsureOpaque32Bpp(sourceImage);
					var fndBitmap = Keysharp.Internals.Images.ImageHelper.EnsureOpaque32Bpp(findImage);
					try
					{
						using var srcData = srcBitmap.Lock();
						using var fndData = fndBitmap.Lock();

						unsafe
						{
							var srcColor = new FastColor();
							var fndColor = new FastColor();
							var srcPtr = (byte*)srcData.Data;
							var fndPtr = (byte*)fndData.Data;
							var srcStride = srcData.ScanWidth;
							var fndStride = fndData.ScanWidth;
							var srcBpp = srcData.BytesPerPixel;
							var fndBpp = fndData.BytesPerPixel;

							for (int row = 0; row < maxMovement.Height; row++)
							{
								for (int col = 0; col < maxMovement.Width; col++)
								{
									for (int dr = 0; dr < fndBitmap.Height; dr++)
									{
										var srcLine = srcPtr + ((row + dr) * srcStride) + (col * srcBpp);
										var fndLine = fndPtr + (dr * fndStride);

										for (int dc = 0; dc < fndBitmap.Width; dc++)
										{
											var srcPixel = *(int*)(srcLine + dc * srcBpp);
											var fndPixel = *(int*)(fndLine + dc * fndBpp);

											var srcArgb = srcData.TranslateDataToArgb(srcPixel);
											var fndArgb = fndData.TranslateDataToArgb(fndPixel);

											srcColor.Value = (uint)srcArgb;
											fndColor.Value = (uint)fndArgb;

											// trans-color match (caller-supplied) is intentionally RGB-only:
											// the user gives a pure RGB value, no alpha component.
											if (trans != -1 && (fndColor.Value & 0x00FFFFFFu) == (transCol.Value & 0x00FFFFFFu))
												continue;

											if (!fndColor.CompareWithVar(srcColor, Variation))
												goto NOFIND;
										}
									}

									ret = new Point(col, row);
									return ret;
									NOFIND:
									;
								}
							}
						}
					}
					finally
					{
						if (!ReferenceEquals(srcBitmap, sourceImage))
							srcBitmap.Dispose();
						if (!ReferenceEquals(fndBitmap, findImage))
							fndBitmap.Dispose();
					}

#endif
				}
				catch (Exception ex)
				{
					_ = Ks.OutputDebugLine(ex.Message);
				}
				finally
				{
#if WINDOWS
					sourceImage?.UnlockBits(srcdata);
					findImage?.UnlockBits(fnddata);
#endif
				}
			}

			return ret;
		}

		internal Point? Find(Color ColorId, bool ltr, bool ttb)
		{
			// Transparent needle (alpha 0) is a "match anything" sentinel in the legacy path.
			// Honour it by returning the corner pixel without bothering to lock the bitmap.
			if (ColorId.A == 0)
				return new Point(ltr ? 0 : sourceImage.Width - 1, ttb ? 0 : sourceImage.Height - 1);

			var variation = Variation;

#if WINDOWS
			BitmapData locked = null;

			try
			{
				locked = sourceImage.LockBits(
					new Rectangle(0, 0, sourceImage.Width, sourceImage.Height),
					ImageLockMode.ReadOnly,
					PixelFormat.Format32bppArgb);
				// Format32bppArgb stores BGRA in memory, so reading a 4-byte pixel as a
				// little-endian uint yields 0xAARRGGBB — exactly Color.ToArgb's layout.
				var target = unchecked((uint)ColorId.ToArgb());

				unsafe
				{
					return FindMatchingPixel(
						(byte*)locked.Scan0,
						locked.Stride,
						locked.Width,
						locked.Height,
						target,
						variation,
						ltr,
						ttb);
				}
			}
			finally
			{
				if (locked != null)
					sourceImage.UnlockBits(locked);
			}
#else
			// Eto pixbufs may be 24bpp; normalise so the per-pixel read is always 4 bytes.
			var normalized = ImageHelper.EnsureOpaque32Bpp(sourceImage);

			try
			{
				using var data = normalized.Lock();
				// Pixbuf channel order differs between Gtk (RGBA) and Cocoa (BGRA) backends.
				// TranslateArgbToData rewrites the needle into the backend's in-memory layout
				// so the raw uint compare below is correct regardless of platform.
				var target = unchecked((uint)data.TranslateArgbToData(ColorId.ToArgb()));

				unsafe
				{
					return FindMatchingPixel(
						(byte*)data.Data,
						data.ScanWidth,
						normalized.Width,
						normalized.Height,
						target,
						variation,
						ltr,
						ttb);
				}
			}
			finally
			{
				if (!ReferenceEquals(normalized, sourceImage))
					normalized.Dispose();
			}
#endif
		}

		// Vector128.IsHardwareAccelerated is true on x86 SSE2 and ARM64 NEON. The cross-platform
		// Vector128.{Equals,LoadUnsafe,Max,SubtractSaturate,ExtractMostSignificantBits} APIs
		// lower to PCMPEQD / UQSUB / etc. transparently, so this single implementation runs
		// fast on Windows x64, Linux x64, macOS x64, and macOS arm64.
		private static unsafe Point? FindMatchingPixel(
			byte* basePtr, int stride, int width, int height,
			uint target, byte variation, bool ltr, bool ttb)
		{
			var rowStart = ttb ? 0 : height - 1;
			var rowEnd = ttb ? height : -1;
			var rowStep = ttb ? 1 : -1;

			for (var row = rowStart; row != rowEnd; row += rowStep)
			{
				var rowPtr = (uint*)(basePtr + ((nint)row * stride));
				var col = ScanRow(rowPtr, width, target, variation, ltr);

				if (col >= 0)
					return new Point(col, row);
			}

			return null;
		}

		private static unsafe int ScanRow(uint* rowPtr, int width, uint target, byte variation, bool ltr)
		{
			if (variation == 0)
				return ScanRowExact(rowPtr, width, target, ltr);

			return ScanRowVariation(rowPtr, width, target, variation, ltr);
		}

		private static unsafe int ScanRowExact(uint* rowPtr, int width, uint target, bool ltr)
		{
			var col = 0;

			if (Vector128.IsHardwareAccelerated && width >= Vector128<uint>.Count)
			{
				var vTarget = Vector128.Create(target);
				// Bestmatch tracks the rightmost column when scanning RTL; -1 means no match yet.
				var bestRtl = -1;

				for (; col + Vector128<uint>.Count <= width; col += Vector128<uint>.Count)
				{
					var loaded = Vector128.LoadUnsafe(ref *(uint*)(rowPtr + col));
					var matched = Vector128.Equals(loaded, vTarget);
					var mask = matched.ExtractMostSignificantBits();

					if (mask == 0)
						continue;

					if (ltr)
						return col + BitOperations.TrailingZeroCount(mask);

					bestRtl = col + 31 - BitOperations.LeadingZeroCount(mask);
				}

				for (; col < width; col++)
				{
					if (rowPtr[col] != target)
						continue;

					if (ltr)
						return col;

					bestRtl = col;
				}

				return bestRtl;
			}

			// Scalar fallback for platforms where Vector128 isn't accelerated.
			if (ltr)
			{
				for (; col < width; col++)
					if (rowPtr[col] == target)
						return col;
			}
			else
			{
				for (var c = width - 1; c >= 0; c--)
					if (rowPtr[c] == target)
						return c;
			}

			return -1;
		}

		// Zeroes byte 3 of every pixel so the alpha-channel diff is forced to 0 (RGB-only match).
		private static readonly Vector128<byte> RgbOnlyMask = Vector128.Create(0x00FFFFFFu).AsByte();

		private static unsafe int ScanRowVariation(uint* rowPtr, int width, uint target, byte variation, bool ltr)
		{
			// |a - b| per byte = subs_epu8(a, b) | subs_epu8(b, a). With max_epu8(diff, v)
			// equal to v iff every channel diff is <= v. Force the alpha byte's diff to 0 so
			// it always passes — AHK's pixel variation match is RGB-only.
			var col = 0;

			if (Vector128.IsHardwareAccelerated && width >= Vector128<uint>.Count)
			{
				var vTargetBytes = Vector128.Create(target).AsByte();
				var vVariation = Vector128.Create(variation);
				var bestRtl = -1;

				for (; col + Vector128<uint>.Count <= width; col += Vector128<uint>.Count)
				{
					var loaded = Vector128.LoadUnsafe(ref *(byte*)(rowPtr + col));
					var diff = Vector128.SubtractSaturate(loaded, vTargetBytes)
						| Vector128.SubtractSaturate(vTargetBytes, loaded);
					diff &= RgbOnlyMask;  // alpha-channel diff → 0 (always passes)
					// per-byte: diff <= variation ↔ max(diff, variation) == variation
					var perByteOk = Vector128.Equals(Vector128.Max(diff, vVariation), vVariation);
					// Per-pixel verdict: all 4 channel bytes must pass ↔ uint lane is 0xFFFFFFFF.
					var perPixelOk = Vector128.Equals(perByteOk.AsUInt32(), Vector128<uint>.AllBitsSet);
					var mask = perPixelOk.ExtractMostSignificantBits();

					if (mask == 0)
						continue;

					if (ltr)
						return col + BitOperations.TrailingZeroCount(mask);

					bestRtl = col + 31 - BitOperations.LeadingZeroCount(mask);
				}

				for (; col < width; col++)
				{
					if (!ScalarPixelMatchesVariation(rowPtr[col], target, variation))
						continue;

					if (ltr)
						return col;

					bestRtl = col;
				}

				return bestRtl;
			}

			if (ltr)
			{
				for (; col < width; col++)
					if (ScalarPixelMatchesVariation(rowPtr[col], target, variation))
						return col;
			}
			else
			{
				for (var c = width - 1; c >= 0; c--)
					if (ScalarPixelMatchesVariation(rowPtr[c], target, variation))
						return c;
			}

			return -1;
		}

		private static bool ScalarPixelMatchesVariation(uint pixel, uint target, byte variation)
		{
			// Per-byte |pixel - target| <= variation, ignoring the alpha lane (byte 3).
			var diff0 = (int)((pixel >> 0) & 0xFF) - (int)((target >> 0) & 0xFF);
			var diff1 = (int)((pixel >> 8) & 0xFF) - (int)((target >> 8) & 0xFF);
			var diff2 = (int)((pixel >> 16) & 0xFF) - (int)((target >> 16) & 0xFF);
			var v = (int)variation;
			return diff0 >= -v && diff0 <= v
				&& diff1 >= -v && diff1 <= v
				&& diff2 >= -v && diff2 <= v;
		}

		private class PixelMask
		{
			public Color Color { get; set; }

			public bool Exact => Variation == 0;

			public bool Transparent => Color.A == 0;

			public byte Variation { get; set; }

			public PixelMask(Color color, byte variation)
			{
				Color = color;
				Variation = variation;
			}

#if WINDOWS
			public PixelMask() : this(Color.Black, 0)
#else
			public PixelMask() : this(Colors.Black, 0)
#endif
			{
			}

			public bool Equals(Color match)
			{
				if (Transparent)
					return true;

				if (Exact)
					return Color == match;

				var r = match.R >= Color.R - Variation && match.R <= Color.R + Variation;
				var g = match.G >= Color.G - Variation && match.G <= Color.G + Variation;
				var b = match.B >= Color.B - Variation && match.B <= Color.B + Variation;
				return r && g && b;
			}
		}
	}
}