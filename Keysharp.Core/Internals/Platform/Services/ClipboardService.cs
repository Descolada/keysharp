#if WINDOWS
using static System.Windows.Forms.DataFormats;
#endif
#if LINUX
using Wl = Keysharp.Internals.Window.Linux.Wayland;
#endif

namespace Keysharp.Internals
{
	/// <summary>Runs an action once when disposed (used to unsubscribe an event handler). Thread-safe / idempotent.</summary>
	internal sealed class CallbackDisposable : IDisposable
	{
		private Action onDispose;

		internal CallbackDisposable(Action onDispose) => this.onDispose = onDispose;

		public void Dispose() => Interlocked.Exchange(ref onDispose, null)?.Invoke();
	}

#if LINUX || OSX
	/// <summary>
	/// The shared Eto (GTK/Cocoa) clipboard — the backend on macOS and the fallback on Linux (X11, or a Wayland
	/// session where Eto's data-control handler works, e.g. KWin). Every operation goes through
	/// <see cref="Eto.Forms.Clipboard.Instance"/>. <see cref="WaylandBackendClipboard"/> derives from this and
	/// overrides the pieces that must go through a compositor shell extension instead.
	/// </summary>
	internal class EtoClipboard : IClipboard
	{
		// "SKCB" blob framing for CaptureAll/RestoreAll (shared with the Wayland-backend path): an int magic, then
		// repeated {int typeLen, type utf8, int dataLen, data}, terminated by an int 0.
		protected const int ClipboardAllMagic = 0x42434B53; // "SKCB"
		private const string ImagePngKey = "__keysharp_image_png";
		private const string UrisKey = "__keysharp_uris";

		private static readonly string[] TextTypes =
		[
			DataFormats.Text, "TEXT", "STRING", "text/plain", "text/plain;charset=utf-8", "COMPOUND_TEXT"
		];

		private static bool IsTextType(string type) =>
			!string.IsNullOrEmpty(type) && TextTypes.Contains(type, StringComparer.OrdinalIgnoreCase);

		public virtual string GetText() => Clipboard.Instance?.Text ?? "";

		public virtual void SetText(string text)
		{
			var clip = Clipboard.Instance;

			if (clip == null)
				return;

			if (string.IsNullOrEmpty(text))
			{
				clip.Clear();
				clip.Text = "";
			}
			else
				clip.Text = text;
		}

		public virtual bool IsEmpty
		{
			get
			{
				var clip = Clipboard.Instance;

				if (clip == null)
					return true;

				// Eto's DataFormats field names don't match the underlying GTK/native clipboard target identifiers,
				// so reflecting over them never matches. Query Eto's typed accessors directly, then fall back to the
				// raw list of available types for anything else.
				return !clip.ContainsText
					&& !clip.ContainsHtml
					&& !clip.ContainsImage
					&& !clip.ContainsUris
					&& (clip.Types?.Length ?? 0) == 0;
			}
		}

		public virtual int ChangeType()
		{
			var clip = Clipboard.Instance;

			if (clip != null && clip.ContainsText)
				return 1;

			return IsEmpty ? 0 : 2;
		}

		public virtual Bitmap GetImage()
		{
			var clip = Clipboard.Instance;

			if (clip == null || !clip.ContainsImage || clip.Image is not Bitmap bmp)
				return null;

			return new Bitmap(bmp);   // detach a private copy from the clipboard object
		}

		public virtual void SetImage(Bitmap image)
		{
			var clip = Clipboard.Instance;

			if (clip != null)
				clip.Image = image;
		}

		public virtual byte[] CaptureAll()
		{
			var clip = Clipboard.Instance;

			if (clip == null)
				return System.Array.Empty<byte>();

			using var ms = new MemoryStream();
			using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
			bw.Write(ClipboardAllMagic);
			var seen = new HashSet<string>(StringComparer.Ordinal);

			foreach (var type in clip.Types ?? System.Array.Empty<string>())
			{
				if (string.IsNullOrEmpty(type) || IsTextType(type))
					continue;

				var payload = clip.GetData(type);

				if (payload == null)
				{
					var str = clip.GetString(type);

					if (str != null)
						payload = Encoding.UTF8.GetBytes(str);
				}

				if (payload == null)
					continue;

				WriteEntry(bw, type, payload);
				_ = seen.Add(type);
			}

			var text = clip.Text;

			if (!string.IsNullOrEmpty(text) && !seen.Contains(DataFormats.Text))
				WriteEntry(bw, DataFormats.Text, Encoding.UTF8.GetBytes(text));

			var html = clip.Html;

			if (!string.IsNullOrEmpty(html) && !seen.Contains(DataFormats.Html))
				WriteEntry(bw, DataFormats.Html, Encoding.UTF8.GetBytes(html));

			if (clip.ContainsImage && clip.Image is Bitmap bmp)
			{
				var imageBytes = bmp.ToByteArray(ImageFormat.Png);

				if (imageBytes != null && imageBytes.Length > 0)
					WriteEntry(bw, ImagePngKey, imageBytes);
			}

			if (clip.ContainsUris && clip.Uris is Uri[] uris && uris.Length > 0)
				WriteEntry(bw, UrisKey, Encoding.UTF8.GetBytes(string.Join("\n", uris.Select(u => u.OriginalString))));

			bw.Write(0);
			return ms.ToArray();
		}

		public virtual void RestoreAll(Keysharp.Builtins.ClipboardAll clip)
		{
			var sourceBytes = Keysharp.Builtins.Env.ExtractClipboardAllBytes(clip, (long)clip.Size);
			var clipboard = Clipboard.Instance;

			if (clipboard == null)
				return;

			if (sourceBytes.Length == 0)
			{
				clipboard.Clear();
				return;
			}

			using var ms = new MemoryStream(sourceBytes, writable: false);
			using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

			if (ms.Length < 4 || br.ReadInt32() != ClipboardAllMagic)
				return;

			clipboard.Clear();

			while (ms.Position < ms.Length)
			{
				var typeLen = br.ReadInt32();

				if (typeLen == 0)
					break;

				if (typeLen < 0 || typeLen > ms.Length - ms.Position)
					break;

				var type = Encoding.UTF8.GetString(br.ReadBytes(typeLen));
				var dataLen = br.ReadInt32();

				if (dataLen < 0 || dataLen > ms.Length - ms.Position)
					break;

				var payload = br.ReadBytes(dataLen);

				if (IsTextType(type))
				{
					clipboard.Text = Encoding.UTF8.GetString(payload);
				}
				else if (type == ImagePngKey)
				{
					using var imgStream = new MemoryStream(payload, writable: false);
					clipboard.Image = new Bitmap(imgStream);
				}
				else if (type == UrisKey)
				{
					var parsedUris = Encoding.UTF8.GetString(payload)
						.Split(['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
						.Select(s => Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null)
						.Where(u => u != null)
						.ToArray();

					if (parsedUris.Length > 0)
						clipboard.Uris = parsedUris;
				}
				else if (string.Equals(type, DataFormats.Html, StringComparison.OrdinalIgnoreCase))
				{
					clipboard.Html = Encoding.UTF8.GetString(payload);
				}
				else
				{
					clipboard.SetData(payload, type);
				}
			}
		}

		public virtual IDisposable Subscribe(Action onChanged)
		{
			var clip = Clipboard.Instance;

			if (clip == null)
				return null;

			EventHandler<EventArgs> handler = (_, _) => onChanged();
			clip.Changed += handler;
			return new CallbackDisposable(() =>
			{
				var c = Clipboard.Instance;

				if (c != null)
					c.Changed -= handler;
			});
		}

		private static void WriteEntry(BinaryWriter bw, string type, byte[] payload)
		{
			var typeBytes = Encoding.UTF8.GetBytes(type);
			bw.Write(typeBytes.Length);
			bw.Write(typeBytes);
			bw.Write(payload.Length);
			bw.Write(payload);
		}
	}
#endif

#if LINUX
	/// <summary>
	/// Resolves the one Linux <see cref="IClipboard"/> for this session — the only place the clipboard backend is
	/// chosen. After this, every clipboard seam is a plain call: the right implementation was already selected, so
	/// there is no <c>SupportsClipboard</c> / <c>is …Backend</c> test on the hot path.
	/// </summary>
	internal static class LinuxClipboards
	{
		internal static IClipboard Resolve()
		{
			// Route through a Wayland shell-extension backend only when the compositor exposes focus-independent
			// clipboard access (a responding extension) AND Eto has fallen back to its focus-gated GTK handler
			// (no data-control protocol). Otherwise — X11, or a Wayland session where Eto's data-control
			// WaylandClipboardHandler works (e.g. KWin) — the shared Eto clipboard is correct.
			if (Wl.WaylandBackend.Current?.SupportsClipboard == true
				&& Eto.Forms.Clipboard.Instance?.Handler is not Eto.GtkSharp.Forms.WaylandClipboardHandler)
				return new WaylandBackendClipboard();

			return new EtoClipboard();
		}
	}

	/// <summary>
	/// Clipboard access driven by the resolved <see cref="Wl.IWaylandBackend"/>'s shell extension (Cinnamon/Muffin
	/// today; any future compositor whose backend implements the clipboard members gets it for free). Muffin
	/// exposes no data-control protocol, so Eto's clipboard is the focus-gated GTK fallback that can't read/write/
	/// monitor from a background app; this routes every operation through the extension instead — as raw MIME
	/// &lt;-&gt; bytes, so every format (text, image, html, uri-list, …) round-trips — and keeps a cache warm from
	/// the change signal so text reads never block the UI thread on a D-Bus round-trip.
	/// </summary>
	internal sealed class WaylandBackendClipboard : EtoClipboard
	{
		internal const string TextMime = "text/plain;charset=utf-8";
		internal const string HtmlMime = "text/html";
		internal const string PngMime  = "image/png";
		internal const string UriMime  = "text/uri-list";

		private volatile string cachedText;          // last known UTF-8 text ("" = none), kept warm by the signal
		private volatile string[] cachedMimetypes;   // last known available MIME types (null = unknown)

		// True only while a change-signal Subscribe is active — MainWindow wires that up (Subscribe) only when the
		// script registers an OnClipboardChange handler. While monitoring, the signal keeps cachedText/cachedMimetypes
		// authoritative so reads stay off the D-Bus path. When NOT monitoring, a script that merely reads A_Clipboard
		// would otherwise be stuck on the FIRST cached value forever (Cinnamon/Muffin Wayland), so every query instead
		// goes live to the backend.
		private volatile bool monitoring;

		private static Wl.IWaylandBackend Backend => Wl.WaylandBackend.Current;

		private static bool IsTextMime(string m)
			=> m != null && (m.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)
							 || string.Equals(m, "UTF8_STRING", StringComparison.OrdinalIgnoreCase));

		private bool HasMime(string mime)
		{
			var m = cachedMimetypes;

			if (m == null)
				return false;

			foreach (var x in m)
				if (string.Equals(x, mime, StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}

		// Mimetypes to answer IsEmpty/ChangeType with: the signal-warmed cache while monitoring, else a live read so a
		// script without an OnClipboardChange handler doesn't see a stale (first-ever) snapshot.
		private string[] CurrentMimetypes => monitoring ? cachedMimetypes : Backend?.GetClipboardMimetypes();

		private bool HasTextIn(string[] mimes)
		{
			// Our optimistic/last-signalled text is only trustworthy while the signal is keeping it warm; otherwise
			// decide purely from the live mimes.
			if (monitoring && !string.IsNullOrEmpty(cachedText))
				return true;

			if (mimes != null)
				foreach (var x in mimes)
					if (IsTextMime(x))
						return true;

			return false;
		}

		public override bool IsEmpty
		{
			get { var m = CurrentMimetypes; return m == null ? string.IsNullOrEmpty(GetText()) : m.Length == 0; }
		}

		public override int ChangeType()
		{
			var m = CurrentMimetypes;

			// Mimetypes unknown: all we can tell is text-vs-empty (from a live text read); can't detect non-text.
			if (m == null)
				return string.IsNullOrEmpty(GetText()) ? 0 : 1;

			return HasTextIn(m) ? 1 : m.Length == 0 ? 0 : 2;
		}

		// ---- text (fast path) -------------------------------------------
		public override string GetText()
		{
			var t = cachedText;

			if (monitoring && t != null)         // signal keeps the cache warm: no D-Bus round-trip on the UI thread
				return t;

			t = Backend?.GetClipboardText() ?? "";
			cachedText = t;
			return t;
		}

		public override void SetText(string text)
		{
			text ??= "";
			Backend?.SetClipboardText(text);
			cachedText = text;                   // reflect our own write immediately (the signal confirms later)
			cachedMimetypes = text.Length == 0 ? System.Array.Empty<string>() : new[] { TextMime };
		}

		// ---- image ------------------------------------------------------
		public override Bitmap GetImage()
		{
			var png = GetContent(PngMime);

			if (png == null || png.Length == 0)
				return null;

			using var ms = new MemoryStream(png);
			return new Bitmap(ms);
		}

		public override void SetImage(Bitmap image)
		{
			if (image == null)
				return;

			SetContent(PngMime, ImageHelper.ToPngBytes(image));
		}

		// ---- generic MIME bytes -----------------------------------------
		private byte[] GetContent(string mimetype) => Backend?.GetClipboardContent(mimetype);

		private void SetContent(string mimetype, byte[] bytes)
		{
			Backend?.SetClipboardContent(mimetype, bytes ?? System.Array.Empty<byte>());
			cachedText = IsTextMime(mimetype) && bytes != null ? Encoding.UTF8.GetString(bytes) : "";
			cachedMimetypes = mimetype == null ? System.Array.Empty<string>() : new[] { mimetype };
		}

		private void Clear()
		{
			Backend?.SetClipboardText("");
			cachedText = "";
			cachedMimetypes = System.Array.Empty<string>();
		}

		// ---- ClipboardAll (every format) --------------------------------
		public override byte[] CaptureAll()
		{
			using var ms = new MemoryStream();
			using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
			bw.Write(ClipboardAllMagic);
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var mime in Backend?.GetClipboardMimetypes() ?? System.Array.Empty<string>())
			{
				if (string.IsNullOrEmpty(mime) || !seen.Add(mime))
					continue;

				var bytes = Backend?.GetClipboardContent(mime);

				if (bytes == null || bytes.Length == 0)
					continue;

				var typeBytes = Encoding.UTF8.GetBytes(mime);
				bw.Write(typeBytes.Length);
				bw.Write(typeBytes);
				bw.Write(bytes.Length);
				bw.Write(bytes);
			}

			bw.Write(0);
			return ms.ToArray();
		}

		public override void RestoreAll(Keysharp.Builtins.ClipboardAll clip)
		{
			var blob = Keysharp.Builtins.Env.ExtractClipboardAllBytes(clip, (long)clip.Size);

			if (blob == null || blob.Length < 4)
			{
				Clear();
				return;
			}

			var formats = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

			using (var ms = new MemoryStream(blob, writable: false))
			using (var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true))
			{
				if (ms.Length < 4 || br.ReadInt32() != ClipboardAllMagic)
				{
					Clear();
					return;
				}

				while (ms.Position < ms.Length)
				{
					var typeLen = br.ReadInt32();

					if (typeLen <= 0 || typeLen > ms.Length - ms.Position)
						break;

					var type = Encoding.UTF8.GetString(br.ReadBytes(typeLen));
					var dataLen = br.ReadInt32();

					if (dataLen < 0 || dataLen > ms.Length - ms.Position)
						break;

					formats[type] = br.ReadBytes(dataLen);
				}
			}

			if (formats.Count == 0)
			{
				Clear();
				return;
			}

			// Single-owner write: MetaSelectionSourceMemory can advertise only one MIME type, so we can't re-post
			// every captured format at once — choose the most useful single representation.
			string chosen = null;

			foreach (var m in formats.Keys)
				if (IsTextMime(m)) { chosen = m; break; }

			chosen ??= formats.ContainsKey(PngMime) ? PngMime
				: formats.ContainsKey(HtmlMime) ? HtmlMime
				: formats.ContainsKey(UriMime) ? UriMime
				: null;

			if (chosen == null)
				foreach (var m in formats.Keys) { chosen = m; break; }

			if (IsTextMime(chosen))
				SetText(Encoding.UTF8.GetString(formats[chosen]));
			else
				SetContent(chosen, formats[chosen]);
		}

		// ---- monitoring -------------------------------------------------
		public override IDisposable Subscribe(Action onChanged)
		{
			var inner = Backend?.SubscribeClipboardChanges((text, mimes) =>
			{
				cachedText = text ?? "";
				cachedMimetypes = mimes ?? System.Array.Empty<string>();
				onChanged?.Invoke();
			});

			if (inner == null)
				return null;                     // no live signal: stay in live-read mode (monitoring stays false)

			monitoring = true;                   // cache is now authoritative; reads may take the fast path

			return new CallbackDisposable(() =>
			{
				monitoring = false;              // back to live reads so a later A_Clipboard read isn't stale
				inner.Dispose();
			});
		}
	}
#endif

#if WINDOWS
	/// <summary>
	/// The Windows clipboard: raw Win32 (OpenClipboard/SetClipboardData) so A_ClipboardTimeout is honored under the
	/// single-owner lock and a text write fires WM_CLIPBOARDUPDATE exactly once (matching AutoHotkey), plus the
	/// typed WinForms Clipboard API for reads and images.
	/// </summary>
	internal sealed class WindowsClipboard : IClipboard
	{
		private static readonly IEnumerable<string> dataFormats =
			typeof(DataFormats).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name);

		public string GetText()
		{
			// OpenClipboard/CloseClipboard honors A_ClipboardTimeout under the Win32 single-owner lock.
			if (WindowsAPI.OpenClipboard(A_ClipboardTimeout.Al()))
			{
				// Whether plain text is present, captured while we still hold the clipboard open (stable — no other
				// process can be mid-update). Scopes the retry below so empty/non-text clipboards are never delayed.
				var hasText = WindowsAPI.IsClipboardFormatAvailable(WindowsAPI.CF_UNICODETEXT)
					|| WindowsAPI.IsClipboardFormatAvailable(WindowsAPI.CF_TEXT);
				_ = WindowsAPI.CloseClipboard();//Need to close it for it to work

				// Clipboard.TryGetData<string> (the .NET 9 typed API) intermittently reports a present text format as
				// empty for a couple of ticks right after an OLE-flushed SetDataObject when another process (e.g. a
				// clipboard-history service) touches the clipboard between our CloseClipboard and the read. The value
				// materializes on a re-read, so since we know text is present, retry the text read until it does.
				for (var attempt = 0; hasText; attempt++)
				{
					// The OS stores clipboard text with CRLF line endings; normalize to `n on the way out so
					// script-visible text uses the same line ending as everywhere else in Keysharp.
					if (Clipboard.TryGetData<string>(DataFormats.UnicodeText, out var uni) && !string.IsNullOrEmpty(uni))
						return Keysharp.Builtins.Ks.NormalizeEol(uni);

					if (Clipboard.TryGetData<string>(DataFormats.Text, out var text) && !string.IsNullOrEmpty(text))
						return Keysharp.Builtins.Ks.NormalizeEol(text);

					if (attempt >= 3)
						break;

					Flow.SleepWithoutInterruption(1);
				}

				if (Clipboard.TryGetData<string>(DataFormats.Html, out var html))
					return html;

				if (Clipboard.TryGetData<string>(DataFormats.Rtf, out var rtf))
					return rtf;

				if (Clipboard.TryGetData<string>(DataFormats.SymbolicLink, out var sym))
					return sym;

				if (Clipboard.TryGetData<string>(DataFormats.OemText, out var oem))
					return Keysharp.Builtins.Ks.NormalizeEol(oem);

				if (Clipboard.TryGetData<string>(DataFormats.CommaSeparatedValue, out var csv))
					return csv;

				if (Clipboard.TryGetData<string[]>(DataFormats.FileDrop, out var files))
					return string.Join(DefaultNewLine, files);
			}

			return "";
		}

		public void SetText(string text)
		{
			if (WindowsAPI.OpenClipboard(A_ClipboardTimeout.Al()))
			{
				// A single raw Win32 transaction (EmptyClipboard + SetClipboardData) fires WM_CLIPBOARDUPDATE exactly
				// once, matching AutoHotkey. Clipboard.SetDataObject(copy:true) would instead do OleSetClipboard then
				// OleFlushClipboard, firing the clipboard-change notification twice per assignment.
				_ = WindowsAPI.EmptyClipboard();

				if (!string.IsNullOrEmpty(text))
				{
					// Store with native CRLF line endings (like Gui control text is written) so the text pastes
					// correctly into other Windows apps. GetText normalizes back to `n on read.
					var hglobal = Marshal.StringToHGlobalUni(Keysharp.Builtins.Ks.NormalizeEol(text, Environment.NewLine));

					if (WindowsAPI.SetClipboardData(WindowsAPI.CF_UNICODETEXT, hglobal) == 0)
						Marshal.FreeHGlobal(hglobal);//SetClipboardData failed, so ownership stays with us.
					//On success the system takes ownership of hglobal and frees it; do not free it here.
				}

				_ = WindowsAPI.CloseClipboard();
			}
		}

		public bool IsEmpty => !dataFormats.Any(Clipboard.ContainsData);

		public int ChangeType()
		{
			if (Clipboard.ContainsText() || Clipboard.ContainsFileDropList())
				return 1;

			return !IsEmpty ? 2 : 0;
		}

		public Bitmap GetImage()
		{
			if (System.Windows.Forms.Clipboard.GetImage() is not System.Drawing.Image img)
				return null;

			var bmp = new Bitmap(img);   // detach a private copy from the clipboard object
			img.Dispose();
			return bmp;
		}

		public void SetImage(Bitmap image) => System.Windows.Forms.Clipboard.SetImage(image);

		public byte[] CaptureAll()
		{
			using (var ms = new MemoryStream())
			{
				var dibToOmit = 0;
				var bw = new BinaryWriter(ms);
				var dataObject = Clipboard.GetDataObject();

				if (dataObject != null)
				{
					foreach (var format in dataObject.GetFormats())
					{
						var fi = ClipFormatStringToInt(format);

						switch (fi)
						{
							case WindowsAPI.CF_BITMAP:
							case WindowsAPI.CF_ENHMETAFILE:
							case WindowsAPI.CF_DSPENHMETAFILE:
								continue;//These formats appear to be specific handle types, not always safe to call GlobalSize() for.
						}

						if (fi == WindowsAPI.CF_TEXT || fi == WindowsAPI.CF_OEMTEXT || fi == dibToOmit)
							continue;

						if (dibToOmit == 0)
						{
							if (fi == WindowsAPI.CF_DIB)
								dibToOmit = WindowsAPI.CF_DIBV5;
							else if (fi == WindowsAPI.CF_DIBV5)
								dibToOmit = WindowsAPI.CF_DIB;
						}
					}

					foreach (var format in dataObject.GetFormats())
					{
						var fi = ClipFormatStringToInt(format);
						var nulldata = false;

						switch (fi)
						{
							case WindowsAPI.CF_BITMAP:
							case WindowsAPI.CF_ENHMETAFILE:
							case WindowsAPI.CF_DSPENHMETAFILE:
								// These formats appear to be specific handle types, not always safe to call GlobalSize() for.
								continue;
						}

						if (fi == WindowsAPI.CF_TEXT || fi == WindowsAPI.CF_OEMTEXT || fi == dibToOmit)
							continue;

						var buf = GetClipboardData(fi, ref nulldata);

						if (buf != null)
						{
						}
						else if (nulldata)
							buf = [];//This format usually has null data.
						else
							continue;//GetClipboardData() failed: skip this format.

						bw.Write(fi);
						bw.Write(buf.Length);
						bw.Write(buf);
					}

					if (ms.Position > 0)
					{
						bw.Write(0);
						return ms.ToArray();
					}
				}
			}

			return System.Array.Empty<byte>();
		}

		public unsafe void RestoreAll(Keysharp.Builtins.ClipboardAll clip)
		{
			var wasOpened = false;

			try
			{
				if (WindowsAPI.OpenClipboard(A_ClipboardTimeout.Al()))//Need to leave it open for it to work when using the Windows API.
				{
					wasOpened = true;
					_ = WindowsAPI.EmptyClipboard();
					var ptr = (nint)clip.Ptr;
					var length = (long)clip.Size;

					for (var index = 0; index < length;)
					{
						var cliptype = Unsafe.Read<uint>((void*)nint.Add(ptr, index));

						if (cliptype == 0)
							break;

						index += 4;
						var size = Unsafe.Read<int>((void*)nint.Add(ptr, index));
						index += 4;

						if (size > 0 && index + size <= length)
						{
							var hglobal = Marshal.AllocHGlobal(size);
							System.Buffer.MemoryCopy((void*)nint.Add(ptr, index), hglobal.ToPointer(), size, size);
							_ = WindowsAPI.SetClipboardData(cliptype, hglobal);
							//Do not free hglobal here.
							index += size;
						}
					}
				}
			}
			finally
			{
				if (wasOpened)
					_ = WindowsAPI.CloseClipboard();
			}
		}

		// Windows clipboard monitoring is a native WM_CLIPBOARDUPDATE listener owned by the Windows MainWindow, not
		// a subscription — so nothing calls this on Windows.
		public IDisposable Subscribe(Action onChanged) => null;

		private static int ClipFormatStringToInt(string fmt) => GetFormat(fmt) is Format d ? d.Id : 0;

		// Get the clipboard data in the given integer format. Gotten from:
		// http://pinvoke.net/default.aspx/user32/GetClipboardData.html
		private static byte[] GetClipboardData(int format, ref bool nullData)
		{
			if (format != 0)
			{
				if (WindowsAPI.OpenClipboard(A_ClipboardTimeout.Al()))
				{
					byte[] buf;
					nint gLock = 0;

					try
					{
						var clipdata = WindowsAPI.GetClipboardData(format, ref nullData);//Get pointer to clipboard data in the selected format.
						var length = (int)WindowsAPI.GlobalSize(clipdata);
						gLock = WindowsAPI.GlobalLock(clipdata);
						buf = new byte[length];

						if (length != 0)
							Marshal.Copy(gLock, buf, 0, length);
					}
					finally
					{
						_ = WindowsAPI.GlobalUnlock(gLock);
						_ = WindowsAPI.CloseClipboard();
					}

					return buf;
				}
			}

			return null;
		}
	}
#endif
}
