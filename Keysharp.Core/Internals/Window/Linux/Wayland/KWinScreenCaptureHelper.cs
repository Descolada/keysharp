using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Eto.Drawing;

#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Long-lived subprocess driver for <c>keysharp-kwin-screencap</c>, plus the row-major SIMD
	/// converters that turn KWin's raw QImage pixel formats into a 32-bit RGBA Eto bitmap.
	///
	/// Why this exists: KWin's ScreenShot2 D-Bus interface only accepts calls from binaries
	/// whitelisted via a privileged-location <c>.desktop</c> file (<c>X-KDE-DBUS-Restricted-
	/// Interfaces</c>). The Keysharp managed process can't be whitelisted, so screen capture
	/// is delegated to the C helper. We start one helper per Keysharp process (lazily, on
	/// first capture) and keep it alive — every subsequent capture is just one line of stdin
	/// and one framed response on stdout, no fork/exec/permission-check overhead.
	/// </summary>
	internal static class KWinScreenCaptureHelper
	{
		private const int FirstRequestTimeoutMs = 60_000;  // first request may trigger the trust prompt
		private const int RequestTimeoutMs = 30_000;
		private const int AuthorizationTimeoutMs = 65_000;
		private const int HelperStatusOk = 0;
		private const int HelperStatusError = 1;

			// Single-process serialization: KWin's screenshot API is request/response over one
			// stdin+stdout pair, so concurrent Keysharp threads asking to capture must queue.
			// A typical capture is ~10–50ms held; if multi-thread screen capture ever becomes a
			// bottleneck the helper would need to support pipelined requests or multiple instances.
			private static readonly object sync = new();
			private static Process helper;
			private static Stream stdin;
			private static Stream stdout;
			private static bool exitHookInstalled;
			private static bool sessionDenied;
			private static string sessionDeniedMessage = "Screen capture permission denied.";

			internal static Bitmap Capture(int x, int y, int w, int h)
			{
				lock (sync)
				{
					if (sessionDenied)
						return null;

					var attempt = 0;

					while (attempt < 2)
					{
						attempt++;
						var firstStart = false;

						if (helper == null || helper.HasExited)
						{
							ResetLocked();

							if (!StartLocked(out var startError, out _))
							{
								DebugLine($"keysharp-kwin-screencap launch failed: {startError}");
								return null;
							}

							firstStart = true;
						}

						try
						{
							SendRequestLocked($"area {x.ToString(CultureInfo.InvariantCulture)} {y.ToString(CultureInfo.InvariantCulture)} {w.ToString(CultureInfo.InvariantCulture)} {h.ToString(CultureInfo.InvariantCulture)}\n");
							return ReadResponseLocked(firstStart ? FirstRequestTimeoutMs : RequestTimeoutMs);
						}
						catch (Exception ex)
						{
							DebugLine($"keysharp-kwin-screencap request failed: {ex.Message}");
							CacheDeniedIfHelperExitedLocked();
							ResetLocked();
							// Retry once on protocol/IO failure — covers the case where the helper
							// died between captures (e.g. session bus went away).
						}
					}

					return null;
				}
			}

			internal static PermissionResult Authorize(string operation, bool forcePrompt = false)
			{
				lock (sync)
				{
					if (forcePrompt)
					{
						sessionDenied = false;
						sessionDeniedMessage = string.Empty;
						ResetLocked();
					}
					else if (sessionDenied)
						return new PermissionResult(PermissionStatus.Denied, sessionDeniedMessage);

					if (helper != null && !helper.HasExited)
						return new PermissionResult(PermissionStatus.Granted);

					if (StartLocked(out var error, out var status, forcePrompt))
						return new PermissionResult(PermissionStatus.Granted);

					return new PermissionResult(status, error);
				}
			}

		private static bool StartLocked(out string error, out PermissionStatus status, bool forcePrompt = false)
		{
			error = null;
			status = PermissionStatus.Unsupported;
			var path = ResolveKWinHelper();

			if (path == null)
			{
				error = "no keysharp-kwin-screencap binary found";
				return false;
			}

			try
			{
				var p = new Process
				{
					StartInfo = new ProcessStartInfo
					{
						FileName = path,
						UseShellExecute = false,
						RedirectStandardInput = true,
						RedirectStandardOutput = true,
						RedirectStandardError = true,
					}
				};
				p.StartInfo.ArgumentList.Add("--serve");

				if (forcePrompt)
					p.StartInfo.ArgumentList.Add("--force-prompt");

				// Drain stderr asynchronously so the helper never blocks on a full stderr pipe.
				p.ErrorDataReceived += (_, e) =>
				{
					if (!string.IsNullOrEmpty(e.Data))
						DebugLine($"keysharp-kwin-screencap: {e.Data}");
				};

				if (!p.Start())
				{
					error = "Process.Start returned false";
					return false;
				}

				p.BeginErrorReadLine();
				var nextStdin = p.StandardInput.BaseStream;
				var nextStdout = p.StandardOutput.BaseStream;
				helper = p;
				stdin = nextStdin;
				stdout = nextStdout;
				ReadStartupStatus(nextStdout, AuthorizationTimeoutMs);
				InstallExitHook();
				return true;
			}
			catch (HelperStartException ex)
			{
				error = ex.Message;
				status = ex.Status;

				if (status == PermissionStatus.Denied)
				{
					sessionDenied = true;
					sessionDeniedMessage = error;
				}

				ResetLocked();
				return false;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				status = PermissionStatus.Unsupported;
				ResetLocked();
				return false;
			}
		}

		private static void InstallExitHook()
		{
			if (exitHookInstalled)
				return;

			exitHookInstalled = true;
			// Best-effort cleanup on managed-process exit: if the runtime triggers ProcessExit
			// mid-capture, the handler will block briefly on `sync` (the CLR allows ~2s for these
			// before aborting), then Kill is best-effort anyway. If the managed process is
			// SIGKILL'd, this handler never runs — instead the helper's read() returns EOF when
			// the kernel closes our stdin pipe, and the helper exits via serve_loop.
			AppDomain.CurrentDomain.ProcessExit += (_, _) =>
			{
				lock (sync)
					ResetLocked();
			};
		}

			private static void ResetLocked()
			{
				try { stdin?.Dispose(); } catch { }
				try { stdout?.Dispose(); } catch { }

				if (helper != null && !helper.HasExited)
				{
					try { helper.Kill(entireProcessTree: true); } catch { }
				}

				try { helper?.Dispose(); } catch { }
				helper = null;
				stdin = null;
				stdout = null;
			}

			private static void CacheDeniedIfHelperExitedLocked()
			{
				try
				{
					if (helper is { HasExited: true, ExitCode: 3 })
					{
						sessionDenied = true;
						sessionDeniedMessage = "Screen capture permission denied.";
					}
				}
				catch
				{
				}
			}

		private static void SendRequestLocked(string request)
		{
			var bytes = Encoding.ASCII.GetBytes(request);
			stdin.Write(bytes, 0, bytes.Length);
			stdin.Flush();
		}

		private static void ReadStartupStatus(Stream s, int timeoutMs)
		{
			var task = Task.Run(() =>
			{
				var status = s.ReadByte();

				if (status < 0)
					throw new EndOfStreamException("helper closed stdout before startup status");

				if (status == HelperStatusOk)
					return;

				if (status != HelperStatusError)
					throw new IOException($"unexpected helper startup status {status}");

				var message = ReadErrorMessage(s);
				var permissionStatus = message.Contains("denied", StringComparison.OrdinalIgnoreCase)
					? PermissionStatus.Denied
					: PermissionStatus.Unsupported;
				throw new HelperStartException(permissionStatus, message);
			});

			if (!task.Wait(timeoutMs))
			{
				ResetLocked();
				throw new TimeoutException($"keysharp-kwin-screencap authorization timed out after {timeoutMs}ms");
			}

			task.GetAwaiter().GetResult();
		}

		private static Bitmap ReadResponseLocked(int timeoutMs)
		{
			// All reads happen on a worker because Stream.Read on a redirected pipe doesn't
			// honour cancellation; we time out by waiting on the task.
			var task = Task.Run(() =>
			{
				var status = stdout.ReadByte();

				if (status < 0)
					throw new IOException("helper closed stdout before responding");

				if (status != 0)
				{
					throw new IOException("helper error: " + ReadErrorMessage(stdout));
				}

				return ReadKsscFrame(stdout);
			});

			if (!task.Wait(timeoutMs))
			{
				ResetLocked();
				throw new TimeoutException($"keysharp-kwin-screencap response timed out after {timeoutMs}ms");
			}

			return task.GetAwaiter().GetResult();
		}

		private static string ReadErrorMessage(Stream s)
		{
			var lenBuf = new byte[4];
			ReadExact(s, lenBuf, 0, 4);
			var length = (int)BitConverter.ToUInt32(lenBuf, 0);
			var msg = length > 0 ? new byte[length] : Array.Empty<byte>();

			if (length > 0)
				ReadExact(s, msg, 0, length);

			return Encoding.UTF8.GetString(msg);
		}

		private static void ReadExact(Stream s, byte[] buf, int offset, int count)
		{
			while (count > 0)
			{
				var read = s.Read(buf, offset, count);

				if (read <= 0)
					throw new EndOfStreamException();

				offset += read;
				count -= read;
			}
		}

		private static Bitmap ReadKsscFrame(Stream s)
		{
			var header = new byte[8 + 4 + 4 + 4 + 4 + 8];  // magic + w + h + stride + format + byteCount
			ReadExact(s, header, 0, header.Length);

			if (header[0] != 'K' || header[1] != 'S' || header[2] != 'S' || header[3] != 'C' || header[4] != '1')
				throw new IOException("bad KSSC1 magic");

			var width = BitConverter.ToUInt32(header, 8);
			var height = BitConverter.ToUInt32(header, 12);
			var stride = BitConverter.ToUInt32(header, 16);
			var format = BitConverter.ToUInt32(header, 20);
			var byteCount = BitConverter.ToUInt64(header, 24);
			var bytesPerPixel = (uint)KWinBytesPerPixel(format);

			if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue
				|| bytesPerPixel == 0 || stride < width * bytesPerPixel
				|| byteCount != (ulong)stride * height || byteCount > int.MaxValue)
				throw new IOException($"invalid KSSC1 metadata w={width} h={height} stride={stride} format={format} bytes={byteCount}");

			// 1080p RGBA = ~8MB per frame; pooled buffers avoid GC pressure in capture loops.
			var byteCountInt = (int)byteCount;
			var bytes = ArrayPool<byte>.Shared.Rent(byteCountInt);

			try
			{
				ReadExact(s, bytes, 0, byteCountInt);
				return BuildBitmapFromKWinPixels(bytes, byteCountInt, (int)width, (int)height, (int)stride, format);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(bytes);
			}
		}

		private static string ResolveKWinHelper()
		{
			var configured = Environment.GetEnvironmentVariable("KEYSHARP_KWIN_SCREENCAP_HELPER");

			if (!string.IsNullOrEmpty(configured))
				return configured;

			var baseDir = AppContext.BaseDirectory;
			var candidates = new[]
			{
				Path.Combine(baseDir, "keysharp-kwin-screencap"),
				"/usr/local/lib/keysharp/keysharp-kwin-screencap",
				"/usr/lib/keysharp/keysharp-kwin-screencap",
				"/usr/local/bin/keysharp-kwin-screencap",
				"/usr/bin/keysharp-kwin-screencap",
			};

			foreach (var candidate in candidates)
				if (File.Exists(candidate))
					return candidate;

			return "keysharp-kwin-screencap";
		}

		private static void DebugLine(string message)
			=> Keysharp.Builtins.Ks.OutputDebugLine(message);

		// QImage format codes that KWin reports back in the KSSC1 header.
		private const uint QImageFormatRgb32 = 4;                    // BGRX in memory (alpha unused)
		private const uint QImageFormatArgb32 = 5;                   // BGRA in memory
		private const uint QImageFormatArgb32Premultiplied = 6;      // BGRA in memory (premul)
		private const uint QImageFormatRgb888 = 13;                  // RGB packed 24-bit
		private const uint QImageFormatRgbx8888 = 16;                // RGBX in memory (alpha unused)
		private const uint QImageFormatRgba8888 = 17;                // RGBA in memory
		private const uint QImageFormatRgba8888Premultiplied = 18;   // RGBA in memory (premul)
		private const uint QImageFormatBgr888 = 29;                  // BGR packed 24-bit

		// pshufb mask: per-pixel byte indices [2,1,0,3] swap B↔R in each BGRA pixel.
		private static readonly Vector128<byte> BgraToRgbaShuffleMask = Vector128.Create(
			(byte)2, 1, 0, 3,
			6, 5, 4, 7,
			10, 9, 8, 11,
			14, 13, 12, 15);

		// OR'ing this into a 32bpp row forces alpha=0xFF on the X-channel formats.
		private static readonly Vector128<byte> AlphaOpaqueMask = Vector128.Create(
			(byte)0, 0, 0, 0xFF,
			0, 0, 0, 0xFF,
			0, 0, 0, 0xFF,
			0, 0, 0, 0xFF);

		// `bytes` is a pooled buffer that may be larger than the actual payload; respect `byteCount`.
		private static unsafe Bitmap BuildBitmapFromKWinPixels(byte[] bytes, int byteCount, int width, int height, int stride, uint format)
		{
			_ = byteCount;  // length is implied by stride*height; carried through for caller intent
			var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgba);

			try
			{
				using var data = bitmap.Lock();

				fixed (byte* srcBase = bytes)
				{
					for (var row = 0; row < height; row++)
					{
						var srcRow = srcBase + ((long)row * stride);
						var dstRow = (byte*)data.Data + ((long)row * data.ScanWidth);

						switch (format)
						{
							case QImageFormatRgb32:                       // BGRX → RGBA, force α=0xFF
							case QImageFormatArgb32:                      // BGRA → RGBA
							case QImageFormatArgb32Premultiplied:
								ConvertBgraRowToRgba(srcRow, dstRow, width, forceAlphaOpaque: format == QImageFormatRgb32);
								break;

							case QImageFormatRgbx8888:                    // RGBX → RGBA, force α=0xFF
								CopyRgbaRow(srcRow, dstRow, width, forceAlphaOpaque: true);
								break;

							case QImageFormatRgba8888:                    // RGBA → RGBA (identity)
							case QImageFormatRgba8888Premultiplied:
								CopyRgbaRow(srcRow, dstRow, width, forceAlphaOpaque: false);
								break;

							case QImageFormatRgb888:                      // 24-bit RGB → RGBA
								ExpandRgb24RowToRgba(srcRow, dstRow, width, swapRb: false);
								break;

							case QImageFormatBgr888:                      // 24-bit BGR → RGBA
								ExpandRgb24RowToRgba(srcRow, dstRow, width, swapRb: true);
								break;

							default:
								bitmap.Dispose();
								return null;
						}
					}
				}

				return bitmap;
			}
			catch
			{
				bitmap.Dispose();
				throw;
			}
		}

		// BGRA → RGBA: swap byte 0 and byte 2 within each 4-byte pixel.
		// SSSE3 pshufb does 4 pixels per 128-bit op; scalar fallback otherwise.
		private static unsafe void ConvertBgraRowToRgba(byte* src, byte* dst, int width, bool forceAlphaOpaque)
		{
			var i = 0;

			if (Ssse3.IsSupported)
			{
				for (; i + 4 <= width; i += 4)
				{
					var v = Sse2.LoadVector128(src + (i * 4));
					v = Ssse3.Shuffle(v, BgraToRgbaShuffleMask);

					if (forceAlphaOpaque)
						v = Sse2.Or(v, AlphaOpaqueMask);

					Sse2.Store(dst + (i * 4), v);
				}
			}

			for (; i < width; i++)
			{
				var p = src + (i * 4);
				var q = dst + (i * 4);
				q[0] = p[2];
				q[1] = p[1];
				q[2] = p[0];
				q[3] = forceAlphaOpaque ? (byte)0xFF : p[3];
			}
		}

		// RGBA → RGBA: identity copy. With forceAlphaOpaque we OR α=0xFF into every pixel.
		private static unsafe void CopyRgbaRow(byte* src, byte* dst, int width, bool forceAlphaOpaque)
		{
			var rowBytes = width * 4;

			if (!forceAlphaOpaque)
			{
				Buffer.MemoryCopy(src, dst, rowBytes, rowBytes);
				return;
			}

			var i = 0;

			if (Sse2.IsSupported)
			{
				for (; i + 4 <= width; i += 4)
				{
					var v = Sse2.LoadVector128(src + (i * 4));
					v = Sse2.Or(v, AlphaOpaqueMask);
					Sse2.Store(dst + (i * 4), v);
				}
			}

			for (; i < width; i++)
			{
				var p = src + (i * 4);
				var q = dst + (i * 4);
				q[0] = p[0];
				q[1] = p[1];
				q[2] = p[2];
				q[3] = 0xFF;
			}
		}

		// 24-bit RGB/BGR → 32-bit RGBA expand with α=0xFF. Used rarely (KWin almost always
		// reports a 32-bit format); kept scalar for simplicity.
		private static unsafe void ExpandRgb24RowToRgba(byte* src, byte* dst, int width, bool swapRb)
		{
			for (var i = 0; i < width; i++)
			{
				var p = src + (i * 3);
				var q = dst + (i * 4);

				if (swapRb)
				{
					q[0] = p[2]; q[1] = p[1]; q[2] = p[0];
				}
				else
				{
					q[0] = p[0]; q[1] = p[1]; q[2] = p[2];
				}

				q[3] = 0xFF;
			}
		}

		private static int KWinBytesPerPixel(uint format)
			=> format switch
			{
				4 or 5 or 6 or 16 or 17 or 18 => 4,
				13 or 29 => 3,
				_ => 0,
			};

		private sealed class HelperStartException(PermissionStatus status, string message) : Exception(message)
		{
			internal PermissionStatus Status { get; } = status;
		}
	}
}
#endif
