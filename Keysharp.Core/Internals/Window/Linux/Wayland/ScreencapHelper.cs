#if LINUX
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Eto.Drawing;

namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Driver for the <c>keysharp-screencap</c> helper, covering both the KWin and GNOME Wayland capture
	/// paths plus the row-major SIMD converters for KWin's raw QImage formats.
	///
	/// <para>The KWin and GNOME paths are the same long-lived stdin/stdout subprocess protocol and differ
	/// only in two ways: the <c>--serve</c> mode argument, and the response frame format (KWin returns
	/// KSSC1-framed raw pixels because its ScreenShot2 D-Bus is restricted to desktop-file-whitelisted
	/// binaries; GNOME returns KSSG1-framed PNG from the Shell extension, callable only from the trusted
	/// root-owned setuid helper). Both therefore share a single <see cref="HelperSession"/> driver and
	/// differ only by the mode string and the frame reader passed to it.</para>
	/// </summary>
	internal static class ScreencapHelper
	{
		private const int FirstRequestTimeoutMs = 60_000;  // first request may trigger the trust prompt
		private const int RequestTimeoutMs = 30_000;
		private const int AuthorizationTimeoutMs = 65_000;
		private const int HelperStatusOk = 0;
		private const int HelperStatusError = 1;

		// One driver per compositor backend; only the one matching the running compositor is ever started.
		// They are identical except for the serve mode and the response frame format.
		private static readonly HelperSession kwin = new("kwin", ReadKsscFrame);
		private static readonly HelperSession gnome = new("gnome", ReadKssgFrame);

		internal static Bitmap Capture(int x, int y, int w, int h)
			=> kwin.Request($"area {Coord(x)} {Coord(y)} {Coord(w)} {Coord(h)}");

		internal static PermissionResult Authorize(string operation, bool forcePrompt = false)
			=> kwin.Authorize(forcePrompt);

		/// <summary>
		/// Captures a single window via KWin's <c>org.kde.KWin.ScreenShot2.CaptureWindow</c> — occlusion-
		/// independent, as KWin re-renders the window off-screen. <paramref name="uuid"/> is the window's KWin
		/// internalId in canonical <c>{…}</c> form. <paramref name="includeDecoration"/> selects the full window
		/// (title bar + borders) when true, or the client area only when false. Returns null when the window
		/// can't be captured (unknown id, permission denied) so the caller can fall back to a rectangle grab.
		/// </summary>
		internal static Bitmap CaptureKWinWindow(string uuid, bool includeDecoration)
			=> kwin.Request($"window {uuid} {(includeDecoration ? "1" : "0")}");

		/// <summary>Captures a screen region via the GNOME Shell extension through keysharp-screencap --serve gnome.</summary>
		internal static Bitmap CaptureGnome(int x, int y, int w, int h)
			=> gnome.Request($"area {Coord(x)} {Coord(y)} {Coord(w)} {Coord(h)}");

		/// <summary>
		/// Captures a single window's full contents via the GNOME Shell extension's CaptureWindow
		/// (meta_window_actor_get_image) — occlusion-independent, as it images the window actor's own
		/// buffer rather than the composited screen. <paramref name="handle"/> is the window's compositor
		/// stable-sequence. Returns null when the window can't be captured (unknown handle, minimized,
		/// permission denied) so the caller can fall back to a rectangle grab.
		/// </summary>
		internal static Bitmap CaptureGnomeWindow(ulong handle)
			=> gnome.Request($"window {handle.ToString(CultureInfo.InvariantCulture)}");

		/// <summary>Authorizes screen capture on GNOME by starting keysharp-screencap --serve gnome.</summary>
		internal static PermissionResult AuthorizeGnome(string operation, bool forcePrompt = false)
			=> gnome.Authorize(forcePrompt);

		private static string Coord(int v) => v.ToString(CultureInfo.InvariantCulture);

		/// <summary>
		/// One long-lived <c>keysharp-screencap --serve &lt;mode&gt;</c> subprocess. KWin's screenshot API
		/// is request/response over a single stdin+stdout pair, so concurrent Keysharp threads queue on
		/// <see cref="sync"/>; a typical capture holds it ~10–50ms. If multi-thread capture ever becomes a
		/// bottleneck the helper would need pipelined requests or multiple instances.
		/// </summary>
		private sealed class HelperSession
		{
			private readonly string mode;                     // "kwin" | "gnome"
			private readonly Func<Stream, Bitmap> readFrame;  // KSSC1 raw (KWin) | KSSG1 PNG (GNOME)
			private readonly object sync = new();
			private Process helper;
			private Stream stdin;
			private Stream stdout;
			private bool exitHookInstalled;
			private bool sessionDenied;
			private string sessionDeniedMessage = "Screen capture permission denied.";

			internal HelperSession(string mode, Func<Stream, Bitmap> readFrame)
			{
				this.mode = mode;
				this.readFrame = readFrame;
			}

			// Sends one request line and returns the captured frame, (re)starting the helper as needed. The
			// grammar is "area X Y W H", "window {uuid} {0|1}" on KWin (the trailing flag = include decoration),
			// or "window {seq}" on GNOME. Returns null when capture is impossible (denied, helper unavailable,
			// or a per-request error such as an unknown window) so callers fall back.
			internal Bitmap Request(string command)
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
								DebugLine($"keysharp-screencap --serve {mode} launch failed: {startError}");
								return null;
							}

							firstStart = true;
						}

						try
						{
							var bytes = Encoding.ASCII.GetBytes(command + "\n");
							stdin.Write(bytes, 0, bytes.Length);
							stdin.Flush();
							return ReadResponseLocked(firstStart ? FirstRequestTimeoutMs : RequestTimeoutMs);
						}
						catch (Exception ex)
						{
							DebugLine($"keysharp-screencap --serve {mode} request failed: {ex.Message}");
							CacheDeniedIfHelperExitedLocked();

							// A per-request error (e.g. window not found) leaves the helper alive and ready;
							// only tear it down if the process itself died, then retry once.
							if (helper == null || helper.HasExited)
								ResetLocked();
						}
					}

					return null;
				}
			}

			internal PermissionResult Authorize(bool forcePrompt)
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

			private bool StartLocked(out string error, out PermissionStatus status, bool forcePrompt = false)
			{
				error = null;
				status = PermissionStatus.Unsupported;
				var path = ResolveHelper();

				if (path == null)
				{
					error = "no keysharp-screencap binary found";
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
					p.StartInfo.ArgumentList.Add(mode);

					if (forcePrompt)
						p.StartInfo.ArgumentList.Add("--force-prompt");

					// Drain stderr asynchronously so the helper never blocks on a full stderr pipe.
					p.ErrorDataReceived += (_, e) =>
					{
						if (!string.IsNullOrEmpty(e.Data))
							DebugLine($"keysharp-screencap: {e.Data}");
					};

					if (!p.Start())
					{
						error = "Process.Start returned false";
						return false;
					}

					p.BeginErrorReadLine();
					helper = p;
					stdin = p.StandardInput.BaseStream;
					stdout = p.StandardOutput.BaseStream;
					ReadStartupStatus(stdout, AuthorizationTimeoutMs);
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

			private void InstallExitHook()
			{
				if (exitHookInstalled)
					return;

				exitHookInstalled = true;
				// Best-effort cleanup on managed-process exit: if the runtime triggers ProcessExit
				// mid-capture, the handler will block briefly on `sync` (the CLR allows ~2s for these
				// before aborting), then Kill is best-effort anyway. If the managed process is
				// SIGKILL'd, this handler never runs — instead the helper's read() returns EOF when
				// the kernel closes our stdin pipe, and the helper exits via its serve loop.
				AppDomain.CurrentDomain.ProcessExit += (_, _) =>
				{
					lock (sync)
						ResetLocked();
				};
			}

			private void ResetLocked()
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

			private void CacheDeniedIfHelperExitedLocked()
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

			private Bitmap ReadResponseLocked(int timeoutMs)
			{
				// All reads happen on a worker because Stream.Read on a redirected pipe doesn't
				// honour cancellation; we time out by waiting on the task.
				var task = Task.Run(() =>
				{
					var statusByte = stdout.ReadByte();

					if (statusByte < 0)
						throw new IOException("helper closed stdout before responding");

					if (statusByte != 0)
						throw new IOException("helper error: " + ReadErrorMessage(stdout));

					return readFrame(stdout);
				});

				if (!task.Wait(timeoutMs))
				{
					ResetLocked();
					throw new TimeoutException($"keysharp-screencap --serve {mode} response timed out after {timeoutMs}ms");
				}

				return task.GetAwaiter().GetResult();
			}
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
				throw new TimeoutException($"keysharp-screencap authorization timed out after {timeoutMs}ms");

			task.GetAwaiter().GetResult();
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

		// KWin frame: KSSC1 magic + width + height + stride + format + uint64 byteCount + raw pixels.
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

		// GNOME frame: KSSG1 magic + uint64 PNG byte count + PNG data.
		private static Bitmap ReadKssgFrame(Stream s)
		{
			var header = new byte[8 + 8];  // magic (8) + uint64 byteCount (8)
			ReadExact(s, header, 0, header.Length);

			if (header[0] != 'K' || header[1] != 'S' || header[2] != 'S' || header[3] != 'G' || header[4] != '1')
				throw new IOException("bad KSSG1 magic");

			var byteCount = BitConverter.ToUInt64(header, 8);

			if (byteCount == 0 || byteCount > int.MaxValue)
				throw new IOException($"invalid KSSG1 byte count {byteCount}");

			var bytes = new byte[(int)byteCount];
			ReadExact(s, bytes, 0, (int)byteCount);
			return new Bitmap(new MemoryStream(bytes));
		}

		private static string ResolveHelper()
		{
			var configured = Environment.GetEnvironmentVariable("KEYSHARP_SCREENCAP_HELPER");

			if (!string.IsNullOrEmpty(configured))
				return configured;

			var baseDir = AppContext.BaseDirectory;
			var candidates = new[]
			{
				Path.Combine(baseDir, "keysharp-screencap"),
				"/usr/local/lib/keysharp/keysharp-screencap",
				"/usr/lib/keysharp/keysharp-screencap",
				"/usr/local/bin/keysharp-screencap",
				"/usr/bin/keysharp-screencap",
			};

			foreach (var candidate in candidates)
				if (File.Exists(candidate))
					return candidate;

			return "keysharp-screencap";
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
