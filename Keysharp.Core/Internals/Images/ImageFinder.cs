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

		internal byte Variation { get; set; }

		/// <summary>
		/// Creates a new Image Finder Instance
		/// </summary>
		/// <param name="source">Source Image where to search in</param>
		internal ImageFinder(Bitmap source) => sourceImage = source;

		internal Point? Find(Bitmap findImage, long trans = -1)
		{
			if (sourceImage == null || findImage == null)
				throw new InvalidOperationException();

			var fndBmp = findImage;

			try
			{
				int srcW = sourceImage.Width, srcH = sourceImage.Height;
				int fndW = fndBmp.Width, fndH = fndBmp.Height;

				if (fndW <= 0 || fndH <= 0 || fndW > srcW || fndH > srcH)
					return null;
				// Normalize both images to flat 0x00RRGGBB arrays up front. This converts each
				// pixel exactly once instead of once per candidate comparison (the old approach
				// of translating inside the innermost loop was the dominant cost: on Eto backends
				// TranslateDataToArgb is a virtual call doing premultiplied-alpha math). Stripping
				// the alpha byte here matches AutoHotkey, which masks pixels with 0x00FFFFFF and
				// only honors transparency via the explicit *TransN option.
				var src = ToRgbArray(sourceImage);
				var fnd = ToRgbArray(fndBmp);

				// 0 → trans pixel (matches any screen color), 0xFFFFFFFF → must compare.
				uint[] fndMask = null;

				if (trans != -1)
				{
					var transRgb = (uint)trans & 0x00FFFFFFu;
					fndMask = new uint[fnd.Length];

					for (var i = 0; i < fnd.Length; i++)
						fndMask[i] = fnd[i] == transRgb ? 0u : uint.MaxValue;
				}

				// Anchor pixel: the search scans rows for pixels matching the anchor and verifies
				// the full needle only at those columns, so most of the haystack is rejected by
				// the SIMD anchor scan alone. The first non-trans pixel serves as the anchor.
				var anchor = 0;

				if (fndMask != null)
				{
					while (anchor < fndMask.Length && fndMask[anchor] == 0)
						anchor++;

					if (anchor == fndMask.Length)//Every needle pixel is the trans color, so any position matches.
						return new Point(0, 0);
				}

				// Secondary probe: the non-trans pixel most different from the anchor. Checked
				// scalar before full verification, it rejects most false anchor hits cheaply —
				// critical at high variation levels where the anchor alone matches much of the
				// screen. -1 (uniform or fully trans needle) disables the check.
				var probe = -1;
				var probeDiff = 0;

				for (var i = 0; i < fnd.Length; i++)
				{
					if (i == anchor || (fndMask != null && fndMask[i] == 0))
						continue;

					var d = ChannelDiffSum(fnd[i], fnd[anchor]);

					if (d > probeDiff)
					{
						probeDiff = d;
						probe = i;
					}
				}

				// Verification row order: rows with the most horizontal color change first.
				// Uniform needle rows match large flat areas of the screen within the variation
				// tolerance, so checking edge-dense rows first fails mismatches in the first few
				// vector chunks instead of after whole uniform rows.
				var rowOrder = new int[fndH];
				var rowScore = new long[fndH];

				for (var ry = 0; ry < fndH; ry++)
				{
					long score = 0;
					var rb = ry * fndW;

					for (var rx = 1; rx < fndW; rx++)
					{
						if (fndMask != null && (fndMask[rb + rx] == 0 || fndMask[rb + rx - 1] == 0))
							continue;

						score += ChannelDiffSum(fnd[rb + rx], fnd[rb + rx - 1]);
					}

					rowOrder[ry] = ry;
					rowScore[ry] = -score;//Negated: Array.Sort is ascending, we want densest first.
				}

				System.Array.Sort(rowScore, rowOrder);

				unsafe
				{
					fixed (uint* srcPtr = src, fndPtr = fnd)
					fixed (uint* maskPtr = fndMask)//Yields null when fndMask is null.
					fixed (int* rowOrderPtr = rowOrder)
					{
						return SearchForNeedle(srcPtr, srcW, srcH, fndPtr, maskPtr, fndW, fndH,
											   anchor % fndW, anchor / fndW, probe, rowOrderPtr, Variation);
					}
				}
			}
			catch (Exception ex)
			{
				_ = Ks.OutputDebugLine(ex.Message);
				return null;
			}
			finally
			{
				if (!ReferenceEquals(fndBmp, findImage))
					fndBmp.Dispose();
			}
		}

		/// <summary>
		/// Copies a bitmap's pixels into a flat array of 0x00RRGGBB values (alpha stripped).
		/// </summary>
		private static uint[] ToRgbArray(Bitmap bmp)
		{
			int w = bmp.Width, h = bmp.Height;
			var pixels = new uint[w * h];
#if WINDOWS
			var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

			try
			{
				unsafe
				{
					var basePtr = (byte*)data.Scan0;

					for (var y = 0; y < h; y++)
					{
						// Format32bppArgb stores BGRA in memory, so a little-endian uint read
						// yields 0xAARRGGBB directly.
						var row = (uint*)(basePtr + (nint)y * data.Stride);
						var dst = y * w;

						for (var x = 0; x < w; x++)
							pixels[dst + x] = row[x] & 0x00FFFFFFu;
					}
				}
			}
			finally
			{
				bmp.UnlockBits(data);
			}

#else
			// Force 32bpp first so the 4-byte reads below are valid (Pixbuf is 3bpp for 24-bit
			// images) and so premultiplied backends see A=255, making the translate lossless.
			var bmp32 = ImageHelper.EnsureOpaque32Bpp(bmp);

			try
			{
				using var data = bmp32.Lock();

				unsafe
				{
					var basePtr = (byte*)data.Data;
					var stride = data.ScanWidth;
					var bpp = data.BytesPerPixel;

					for (var y = 0; y < h; y++)
					{
						var row = basePtr + (long)y * stride;
						var dst = y * w;

						// TranslateDataToArgb handles the backend's channel order
						// (Gtk RGBA vs Cocoa BGRA) and premultiplication.
						for (var x = 0; x < w; x++)
							pixels[dst + x] = (uint)data.TranslateDataToArgb(*(int*)(row + x * bpp)) & 0x00FFFFFFu;
					}
				}
			}
			finally
			{
				if (!ReferenceEquals(bmp32, bmp))
					bmp32.Dispose();
			}

#endif
			return pixels;
		}

		/// <summary>
		/// Scans the haystack for the needle and returns the top-left corner of the first match
		/// in row-major order (top to bottom, left to right), or null if not found.
		/// Rows are scanned with SIMD for pixels matching the anchor needle pixel within the
		/// given variation; the full needle is verified only at those candidate positions.
		/// The scan order must remain sequential so the first match returned is deterministic.
		/// </summary>
		private static unsafe Point? SearchForNeedle(
			uint* src, int srcW, int srcH,
			uint* fnd, uint* mask, int fndW, int fndH,
			int anchorX, int anchorY, int probe, int* rowOrder, byte variation)
		{
			var anchorRgb = fnd[anchorY * fndW + anchorX];
			var probeX = probe >= 0 ? probe % fndW : 0;
			var probeY = probe >= 0 ? probe / fndW : 0;
			var probeRgb = probe >= 0 ? fnd[probe] : 0u;
			var maxRow = srcH - fndH;
			// Candidate columns are anchor positions; the needle's top-left is (col - anchorX, row).
			var colEnd = srcW - fndW + anchorX;//Inclusive.

			for (var row = 0; row <= maxRow; row++)
			{
				var anchorRow = src + (long)(row + anchorY) * srcW;
				var col = anchorX;

				if (Vector128.IsHardwareAccelerated)
				{
					var vTarget = Vector128.Create(anchorRgb);
					var vTargetBytes = vTarget.AsByte();
					var vVariation = Vector128.Create(variation);

					for (; col + Vector128<uint>.Count - 1 <= colEnd; col += Vector128<uint>.Count)
					{
						var loaded = Vector128.LoadUnsafe(ref *(anchorRow + col));
						uint matchBits;

						if (variation == 0)
						{
							matchBits = Vector128.Equals(loaded, vTarget).ExtractMostSignificantBits();
						}
						else
						{
							// Per-byte |a - b| <= variation; the alpha lanes are zero in both
							// arrays so their diff is always 0 and never rejects.
							var diff = Vector128.SubtractSaturate(loaded.AsByte(), vTargetBytes)
									   | Vector128.SubtractSaturate(vTargetBytes, loaded.AsByte());
							var perByteOk = Vector128.Equals(Vector128.Max(diff, vVariation), vVariation);
							matchBits = Vector128.Equals(perByteOk.AsUInt32(), Vector128<uint>.AllBitsSet).ExtractMostSignificantBits();
						}

						while (matchBits != 0)
						{
							var candidate = col + BitOperations.TrailingZeroCount(matchBits);
							var c0 = candidate - anchorX;
							matchBits &= matchBits - 1;//Clear lowest set bit, try the next candidate.

							if (probe >= 0)
							{
								var probePixel = src[(long)(row + probeY) * srcW + c0 + probeX];

								if (variation == 0 ? probePixel != probeRgb : !ScalarPixelMatchesVariation(probePixel, probeRgb, variation))
									continue;
							}

							if (VerifyMatch(src, srcW, fnd, mask, fndW, fndH, c0, row, rowOrder, variation))
								return new Point(c0, row);
						}
					}
				}

				for (; col <= colEnd; col++)
				{
					var matches = variation == 0
								  ? anchorRow[col] == anchorRgb
								  : ScalarPixelMatchesVariation(anchorRow[col], anchorRgb, variation);

					if (!matches)
						continue;

					var c0 = col - anchorX;

					if (probe >= 0)
					{
						var probePixel = src[(long)(row + probeY) * srcW + c0 + probeX];

						if (variation == 0 ? probePixel != probeRgb : !ScalarPixelMatchesVariation(probePixel, probeRgb, variation))
							continue;
					}

					if (VerifyMatch(src, srcW, fnd, mask, fndW, fndH, c0, row, rowOrder, variation))
						return new Point(c0, row);
				}
			}

			return null;
		}

		/// <summary>
		/// Compares the full needle against the haystack with its top-left corner at (col, row).
		/// mask may be null; where it is 0, the needle pixel is the trans color and always matches.
		/// rowOrder lists needle rows with the most horizontal color change first: uniform rows
		/// match large flat screen areas within the variation tolerance, so edge-dense rows
		/// reject false candidates after far fewer chunk comparisons.
		/// </summary>
		private static unsafe bool VerifyMatch(
			uint* src, int srcW, uint* fnd, uint* mask, int fndW, int fndH,
			int col, int row, int* rowOrder, byte variation)
		{
			var vVariation = Vector128.Create(variation);

			for (var r = 0; r < fndH; r++)
			{
				var dr = rowOrder[r];
				var srcRow = src + (long)(row + dr) * srcW + col;
				var fndRow = fnd + (long)dr * fndW;
				var maskRow = mask == null ? null : mask + (long)dr * fndW;
				var dc = 0;

				if (Vector128.IsHardwareAccelerated)
				{
					for (; dc + Vector128<uint>.Count <= fndW; dc += Vector128<uint>.Count)
					{
						var s = Vector128.LoadUnsafe(ref *(srcRow + dc));
						var f = Vector128.LoadUnsafe(ref *(fndRow + dc));

						if (variation == 0)
						{
							var diff = s ^ f;

							if (maskRow != null)
								diff &= Vector128.LoadUnsafe(ref *(maskRow + dc));

							if (diff != Vector128<uint>.Zero)
								return false;
						}
						else
						{
							var diff = Vector128.SubtractSaturate(s.AsByte(), f.AsByte())
									   | Vector128.SubtractSaturate(f.AsByte(), s.AsByte());

							if (maskRow != null)
								diff &= Vector128.LoadUnsafe(ref *(maskRow + dc)).AsByte();

							if (Vector128.Equals(Vector128.Max(diff, vVariation), vVariation) != Vector128<byte>.AllBitsSet)
								return false;
						}
					}
				}

				for (; dc < fndW; dc++)
				{
					if (maskRow != null && maskRow[dc] == 0)
						continue;

					var matches = variation == 0
								  ? srcRow[dc] == fndRow[dc]
								  : ScalarPixelMatchesVariation(srcRow[dc], fndRow[dc], variation);

					if (!matches)
						return false;
				}
			}

			return true;
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

		/// <summary>
		/// Sum of absolute per-channel differences between two 0x00RRGGBB pixels.
		/// </summary>
		private static int ChannelDiffSum(uint a, uint b)
		{
			return Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF))
				   + Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF))
				   + Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
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

	}
}