#if WINDOWS
using static System.Windows.Forms.DataFormats;
#else
using static Eto.Forms.DataFormats;
#endif

namespace Keysharp.Core
{
	/// <summary>
	/// Public interface for environment-related functions.
	/// </summary>
	public static class Env
	{
		internal static byte[] CaptureClipboardAllBytes()
		{
#if !WINDOWS
			return CaptureClipboardAllEto();
#else
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
#endif
		}

		/// <summary>
		/// Waits until the clipboard contains data.
		/// </summary>
		/// <param name="tiemout">If omitted, the function will wait indefinitely. Otherwise, it will wait no longer than this many seconds.<br/>
		/// To wait for a fraction of a second, specify a floating-point number, for example, 0.25 to wait for a maximum of 250 milliseconds.
		/// </param>
		/// <param name="waitFor">If omitted, it defaults to 0 (wait only for text or files).<br/>
		/// Otherwise, specify one of the following numbers to indicate what to wait for:<br/>
		/// 0: The function is more selective, waiting specifically for text or files to appear("text" includes anything that would produce text when you paste into Notepad).<br/>
		/// 1: The function waits for data of any kind to appear on the clipboard.
		/// </param>
		/// <returns>True if it did not time out, else false.</returns>
		public static bool ClipWait(object tiemout = null, object waitFor = null)
		{
			//Need to see if this should be put on the main thread like A_Clipboard.//TODO
			var to = tiemout.Ad(double.MinValue);
			var type = waitFor.Ab();
			var checktime = to != double.MinValue;
			var frequency = 100;
			var time = checktime ? (long)(Math.Abs(to) * 1000) : long.MaxValue;

			for (var i = 0L; !checktime || i < time; i += frequency)
			{
#if WINDOWS
				if (!type ? Clipboard.ContainsText()
						|| Clipboard.ContainsFileDropList()
#else
				if (!type ? Clipboard.Instance.ContainsText
#endif
						: !Ks.IsClipboardEmpty())
					return true;

				Flow.Sleep(frequency);
			}

			return false;
		}

		/// <summary>
		/// Retrieves the value of the specified environment variable.
		/// </summary>
		/// <param name="name">The name of the environment variable to retrieve.</param>
		/// <returns>The value of the specified environment variable if it exists, else empty string.</returns>
		public static string EnvGet(object name) => Environment.GetEnvironmentVariable(name.As()) ?? string.Empty;

		/// <summary>
		/// Writes a value to the specified environment variable.
		/// </summary>
		/// <param name="name">The name of the environment variable.</param>
		/// <param name="value">If omitted, the environment variable will be deleted. Otherwise, specify the value to write.</param>
		/// <exception cref="OSError">An <see cref="OSError"/> exception is thrown if any failure is detected.</exception>
		public static object EnvSet(object name, object value = null)
		{
			try
			{
				Environment.SetEnvironmentVariable(name.As(), value as string);
				return DefaultObject;
			}
			catch (Exception ex)
			{
				return Errors.OSErrorOccurred(ex, $@"Error setting environment variable {name} to value ""{value}"".");
			}
		}

		/// <summary>
		/// Notifies the operating system and all running applications that environment variables have changed.
		/// </summary>
		/// <exception cref="OSError">An <see cref="OSError"/> exception is thrown on Windows if any failure is detected.</exception>
		public static object EnvUpdate()
		{
#if LINUX
			"source ~/.bashrc".Bash();
#elif WINDOWS

			//SendMessage() freezes when running in a unit test. PostMessage seems to work. Use SendMessageTimeout().
			try { _ = WindowsAPI.SendMessageTimeout(new nint(WindowsAPI.HWND_BROADCAST), WindowsAPI.WM_SETTINGCHANGE, 0, 0, SendMessageTimeoutFlags.SMTO_ABORTIFHUNG, 1000, out var result); }
			catch (Exception ex)
			{
				return Errors.OSErrorOccurred(ex, "Error updating environment variables.");
			}

#endif
			return DefaultObject;
		}

		/// <summary>
		/// Assign command line arguments to <see cref="A_Args"/>.
		/// This should never be called directly by a Script.TheScript.<br/>
		/// Instead, it's used by the parser in the generated C# code.
		/// </summary>
		/// <param name="args">The command line arguments to process.</param>
		[PublicHiddenFromUser]
		public static object HandleCommandLineParams(string[] args)
		{
			if (args.Length > 0 && args[0].TrimStart(Keywords.DashSlash).ToUpper() == "SCRIPT")
			{
				string[] newArgs = new string[args.Length - 1];
				System.Array.Copy(args, 1, newArgs, 0, args.Length - 1);
				Environment.ExitCode = Runner.Run(args);
				throw new Flow.UserRequestedExitException();
			}

			_ = A_Args.AddRange(args);
			return DefaultObject;
		}

		/// <summary>
		/// Registers a function to be called automatically whenever the clipboard's content changes.
		/// </summary>
		/// <param name="callback">The callback to call, which has a single parameter specifying which data type the clipboard contains.<br/>
		/// 0: Clipboard is now empty.<br/>
		/// 1: Clipboard contains something that can be expressed as text(this includes files copied from an Explorer window).<br/>
		/// 2: Clipboard contains something entirely non-text such as a picture.<br/>
		/// </param>
		/// <param name="addRemove">If omitted, it defaults to 1. Otherwise, specify one of the following numbers:<br/>
		///  1: Call the callback after any previously registered callbacks.<br/>
		/// -1: Call the callback before any previously registered callbacks.<br/>
		///  0: Do not call the callback.
		/// </param>
		/// <exception cref="TypeError">A <see cref="TypeError"/> exception is thrown if callback is not of type <see cref="FuncObj"/>.</exception>
		public static object OnClipboardChange(object callback, object addRemove = null)
		{
			if (callback is IFuncObj fo)
			{
				Script.TheScript.ClipFunctions.ModifyEventHandlers(fo, addRemove.Al(1));
				return DefaultObject;
			}
			else
				return Errors.TypeErrorOccurred(callback, typeof(FuncObj), DefaultErrorObject);
		}

		/// <summary>
		/// Retrieves dimensions of system objects, and other system properties.
		/// </summary>
		/// <param name="property">The variable to store the result.</param>
		/// <returns>This function returns the value of the specified system property.</returns>
		public static object SysGet(object property)
		{
#if !WINDOWS
			var sm = property is Keysharp.Core.Common.Platform.SystemMetric en ? en : (SystemMetric)property.Ai();
			Eto.Forms.Screen screen = null;
			Eto.Forms.Screen[] screens = [];

			try
			{
				screen = Eto.Forms.Screen.PrimaryScreen;
				screens = Eto.Forms.Screen.Screens?.ToArray() ?? [];
			}
			catch
			{
			}

			RectangleF bounds;
			RectangleF working;

			try
			{
				bounds = screen?.Bounds ?? new RectangleF(0, 0, 0, 0);
			}
			catch
			{
				bounds = new RectangleF(0, 0, 0, 0);
			}

			try
			{
				working = screen?.WorkingArea ?? bounds;
			}
			catch
			{
				working = bounds;
			}

			switch (sm)
			{
				case SystemMetric.SM_CMONITORS:
					return (long)screens.Length;

				case SystemMetric.SM_CXSCREEN:
					return (long)bounds.Width;

				case SystemMetric.SM_CYSCREEN:
					return (long)bounds.Height;

				case SystemMetric.SM_CXFULLSCREEN:
					return (long)working.Width;

				case SystemMetric.SM_CYFULLSCREEN:
					return (long)working.Height;

				case SystemMetric.SM_CXMAXIMIZED:
					return (long)working.Width;

				case SystemMetric.SM_CYMAXIMIZED:
					return (long)working.Height;

				case SystemMetric.SM_MOUSEPRESENT:
					return 1L;

				case SystemMetric.SM_SWAPBUTTON:
					return MouseButtonsSwapped() ? 1L : 0L;

				case SystemMetric.SM_CMOUSEBUTTONS:
					return MouseButtonCount();

				case SystemMetric.SM_NETWORK:
					return NetworkUp() ? 1L : 0L;

				case SystemMetric.SM_CLEANBOOT:
				{
					var boots = "last reboot".Bash().SplitLines().ToList();

					if (boots.Count > 0)
					{
						if (boots[0].Contains("recovery", StringComparison.OrdinalIgnoreCase))
							return NetworkUp() ? 2L : (object)1L;
					}

					return 0L;
				}

				case SystemMetric.SM_MOUSEWHEELPRESENT:
					return "xinput --list --long".Bash().Contains("button wheel", StringComparison.OrdinalIgnoreCase) ? 1L : 0L;

				case SystemMetric.SM_REMOTESESSION:
					return "echo $SSH_TTY".Bash() != "" ? 1L : 0L;

				case SystemMetric.SM_SHUTTINGDOWN:
					return "systemctl is-system-running".Bash().Contains("stopping", StringComparison.OrdinalIgnoreCase) ? 1L : 0L;

				case SystemMetric.SM_REMOTECONTROL:
					return "echo $SSH_TTY".Bash() != "" ? 1L : 0L;

				default:
					throw new NotImplementedException($"SysGet({sm}) has no Linux/Eto equivalent.");
			}
#elif WINDOWS

			if (property is SystemMetric en)
				return (long)WindowsAPI.GetSystemMetrics(en);

			return (long)WindowsAPI.GetSystemMetrics((SystemMetric)property.Ai());
#else
			return 0L;
#endif
		}

		/// <summary>
		/// Internal helper to convert a clipboard integer format to a string.
		/// </summary>
		/// <param name="fmt">The format to convert.</param>
		/// <returns>The string representation of the specified clipboard format.</returns>
#if WINDOWS
		internal static int ClipFormatStringToInt(string fmt) => GetFormat(fmt) is Format d ? d.Id : 0;
#else
		internal static int ClipFormatStringToInt(string fmt) => 0;
#endif

		internal static byte[] ExtractClipboardAllBytes(object data, long size = long.MinValue)
		{
			if (data == null)
				return System.Array.Empty<byte>();

			if (data is byte[] ba)
				return CopyClipboardBytes(ba, size);

			if (data is Array arr)
				return CopyClipboardBytes(arr.ToByteArray().ToArray(), size);

			var ptr = Reflections.GetPtrProperty(data);

			if (ptr != 0)
			{
				var sourceLength = size;

				if (sourceLength == long.MinValue && Script.GetPropertyValueOrNull(data, "Size") is object sz)
					sourceLength = sz.Al(long.MinValue);

				if (sourceLength > 0)
					return CopyClipboardBytes((nint)ptr, sourceLength, size);
			}

			return System.Array.Empty<byte>();
		}

		private static byte[] CopyClipboardBytes(byte[] source, long requestedSize = long.MinValue)
		{
			if (source == null || source.Length == 0)
				return System.Array.Empty<byte>();

			var length = requestedSize == long.MinValue ? source.Length : (int)Math.Max(0, Math.Min(requestedSize, source.Length));
			var result = new byte[length];
			System.Array.Copy(source, result, length);
			return result;
		}

		private static byte[] CopyClipboardBytes(nint sourcePtr, long sourceLength, long requestedSize = long.MinValue)
		{
			if (sourcePtr == 0 || sourceLength <= 0)
				return System.Array.Empty<byte>();

			var length = requestedSize == long.MinValue ? sourceLength : Math.Max(0, Math.Min(sourceLength, requestedSize));

			if (length <= 0 || length > int.MaxValue)
				return System.Array.Empty<byte>();

			var result = new byte[length];
			Marshal.Copy(sourcePtr, result, 0, (int)length);
			return result;
		}

		/// <summary>
		/// Internal helper to search the command line arguments for a specified string.
		/// </summary>
		/// <param name="arg">The argument to search for.</param>
		/// <param name="startsWith">True to require the argument to start with arg, else it must contain arg.</param>
		/// <returns>The matched argument if found, else null.</returns>
		internal static string FindCommandLineArg(string arg, bool startsWith = true)
		{
			var script = Script.TheScript;

			if (startsWith)
				return script.ScriptArgs.FirstOrDefault(x => (x.StartsWith('-')
						|| x.StartsWith('/')) && x.Trim(DashSlash).StartsWith(arg, StringComparison.OrdinalIgnoreCase));
			else
				return script.ScriptArgs.FirstOrDefault(x => (x.StartsWith('-')
						|| x.StartsWith('/')) && x.Trim(DashSlash).Contains(arg, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Internal helper to search the command line argument values for a specified string.
		/// </summary>
		/// <param name="arg">The argument to search for.</param>
		/// <param name="startsWith">True to require the argument to start with arg, else it must contain arg.</param>
		/// <returns>The matched argument if found, else null.</returns>
		/// <returns>The matched value if found, else null.</returns>
		internal static string FindCommandLineArgVal(string arg, bool startsWith = true)
		{
			var script = Script.TheScript;
			var args = script.ScriptArgs;

			for (var i = 0; i < args.Length; i++)
			{
				if ((args[i].StartsWith('-') || args[i].StartsWith('/')) && (startsWith ? args[i].TrimStart(DashSlash).StartsWith(arg, StringComparison.OrdinalIgnoreCase) : args[i].Contains(arg, StringComparison.OrdinalIgnoreCase)))
					if (i < args.Length - 1)
						return args[i + 1];
			}

			return null;
		}

#if WINDOWS

		/// <summary>
		/// Internal helper to get the data on the clipboard in the specified format.
		/// Gotten from: http://pinvoke.net/default.aspx/user32/GetClipboardData.html
		/// </summary>
		/// <param name="format">The numerical format of the data to retireve.</param>
		/// <param name="nullData">True if null data is ok, else false.</param>
		/// <returns>The retrieved data as a byte array, else null on error.</returns>
		internal static byte[] GetClipboardData(int format, ref bool nullData)
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

#endif

#if !WINDOWS
		/// <summary>
		/// Get the number of buttons on the mouse.
		/// This tries to find the device with the least number of buttons and assumes that is the mouse.
		/// XTEST devices are considered but it shouldn't matter because they won't have less buttons than
		/// the actual mouse.
		/// </summary>
		/// <returns>The number of mouse buttons detected</returns>
		internal static long MouseButtonCount()
		{
#if LINUX
			var count = long.MaxValue;
			var inputStr = "xinput list --long".Bash();

			foreach (Range r in inputStr.AsSpan().SplitAny(CrLf))
			{
				var split = inputStr.AsSpan(r).Trim();

				if (split.Contains("Buttons supported:", StringComparison.OrdinalIgnoreCase))
				{
					var splitct = 0;

					foreach (Range r2 in split.Split(':'))
					{
						var btnSplit = split[r2].Trim();

						if (btnSplit.Length > 0)
						{
							if (splitct > 0)
							{
								if (long.TryParse(btnSplit, out var btnCount))
								{
									count = Math.Min(count, btnCount);
									break;
								}
							}

							splitct++;
						}
					}
				}
			}

			return count == long.MaxValue ? 3L : count;
#else
			return 3L;
#endif
		}

		/// <summary>
		/// Detect whether the buttons on any mouse are swapped. This function is needed because Mono's Winforms
		/// hard codes SystemInformation.MouseButtonsSwapped to false for linux.
		/// This will break on the first device with buttons swapped, which may or may not be the actual mouse.
		/// Unsure how to find the "actual" mouse device though. Should be good enough for our purposes.
		/// This also excludes any device with "XTEST" in the name.
		/// </summary>
		/// <returns>Whether any mouse was found to have any buttons swapped</returns>
		internal static bool MouseButtonsSwapped()
		{
#if LINUX
			var swapped = false;
			var deviceNames = "xinput list --name-only".Bash().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			//foreach (var name in deviceNames)
			//  Ks.OutputDebugLine($"{name}");
			var deviceIds = "xinput list --id-only".Bash().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

			//foreach (var id in deviceIds)
			//  Ks.OutputDebugLine($"{id}");

			if (deviceNames.Length == deviceIds.Length)
			{
				for (var i = 0; i < deviceNames.Length && !swapped; i++)
				{
					if (!deviceNames[i].Contains("xtest", StringComparison.OrdinalIgnoreCase))
					{
						var buttonStr = $"xinput get-button-map {deviceIds[i]}".Bash();
						var buttonStrSplits = buttonStr.Split(SpaceTab, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

						if (buttonStrSplits.All(sp => int.TryParse(sp, out var _)))
						{
							//Ks.OutputDebugLine($"Device {deviceIds[i]}: {deviceNames[i]} with buttons {buttonStr} getting examined.");
							for (var j = 0; j < 3 && j < buttonStrSplits.Length; j++)
							{
								if (int.TryParse(buttonStrSplits[j], out var btn))
								{
									if (btn != j + 1)
									{
										swapped = true;
										//Ks.OutputDebugLine($"\tWas swapped.");
										break;
									}
								}
								else
									break;
							}
						}
					}
				}
			}

			return swapped;
#else
			return false;
#endif
		}

		internal static bool NetworkUp()
		{
#if LINUX
			return "ip link show".Bash().Contains("state up", StringComparison.OrdinalIgnoreCase);
#else
			var output = "ifconfig".Bash();
			return output.Contains("status: active", StringComparison.OrdinalIgnoreCase);
#endif
		}

#endif
		/// <summary>
		/// Internal helper to restore data contained in a <see cref="ClipboardAll"/> object to the clipboard.
		/// </summary>
		/// <param name="clip">The object whose data will be restored.</param>
		/// <param name="length">The length of the data to restore.</param>
		internal static void RestoreClipboardAll(ClipboardAll clip, long length)
		{
			unsafe
			{
#if !WINDOWS
				RestoreClipboardAllEto(clip, length);
#elif WINDOWS
				var wasOpened = false;

				try
				{
					if (WindowsAPI.OpenClipboard(A_ClipboardTimeout.Al()))//Need to leave it open for it to work when using the Windows API.
					{
						wasOpened = true;
						_ = WindowsAPI.EmptyClipboard();
						var ptr = (nint)clip.Ptr;
						length = Math.Min(Math.Max(0U, length), (long)clip.Size);

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

#endif
			}
		}

#if !WINDOWS
		private const int ClipboardAllEtoMagic = 0x42434B53; // "SKCB"
		private const string ClipboardAllEtoImagePng = "__keysharp_image_png";
		private const string ClipboardAllEtoUris = "__keysharp_uris";

		private static byte[] CaptureClipboardAllEto()
		{
			var clip = Clipboard.Instance;

			if (clip == null)
				return System.Array.Empty<byte>();

			using var ms = new MemoryStream();
			using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
			bw.Write(ClipboardAllEtoMagic);
			var seen = new HashSet<string>(StringComparer.Ordinal);

			foreach (var type in clip.Types ?? System.Array.Empty<string>())
			{
				if (string.IsNullOrEmpty(type))
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

				WriteEtoClipboardEntry(bw, type, payload);
				_ = seen.Add(type);
			}

			if (clip.ContainsText && !seen.Contains(DataFormats.Text))
			{
				var text = clip.Text;

				if (!string.IsNullOrEmpty(text))
					WriteEtoClipboardEntry(bw, DataFormats.Text, Encoding.UTF8.GetBytes(text));
			}

			if (clip.ContainsHtml && !seen.Contains(DataFormats.Html))
			{
				var html = clip.Html;

				if (!string.IsNullOrEmpty(html))
					WriteEtoClipboardEntry(bw, DataFormats.Html, Encoding.UTF8.GetBytes(html));
			}

			if (clip.ContainsImage && clip.Image is Bitmap bmp)
			{
				var imageBytes = bmp.ToByteArray(ImageFormat.Png);

				if (imageBytes != null && imageBytes.Length > 0)
					WriteEtoClipboardEntry(bw, ClipboardAllEtoImagePng, imageBytes);
			}

			if (clip.ContainsUris && clip.Uris is Uri[] uris && uris.Length > 0)
				WriteEtoClipboardEntry(bw, ClipboardAllEtoUris, Encoding.UTF8.GetBytes(string.Join("\n", uris.Select(u => u.OriginalString))));

			bw.Write(0);
			return ms.ToArray();
		}

		private static void RestoreClipboardAllEto(ClipboardAll clip, long length)
		{
			var sourceBytes = ExtractClipboardAllBytes(clip, length > 0 ? length : (long)clip.Size);
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

			if (ms.Length < 4 || br.ReadInt32() != ClipboardAllEtoMagic)
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

				if (type == ClipboardAllEtoImagePng)
				{
					using var imgStream = new MemoryStream(payload, writable: false);
					clipboard.Image = new Bitmap(imgStream);
				}
				else if (type == ClipboardAllEtoUris)
				{
					var parsedUris = Encoding.UTF8.GetString(payload)
						.Split(['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
						.Select(s => Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null)
						.Where(u => u != null)
						.ToArray();

					if (parsedUris.Length > 0)
						clipboard.Uris = parsedUris;
				}
				else
				{
					clipboard.SetData(payload, type);
				}
			}
		}

		private static void WriteEtoClipboardEntry(BinaryWriter bw, string type, byte[] payload)
		{
			var typeBytes = Encoding.UTF8.GetBytes(type);
			bw.Write(typeBytes.Length);
			bw.Write(typeBytes);
			bw.Write(payload.Length);
			bw.Write(payload);
		}
#endif
	}
	/// <summary>
	/// A class that represents clipboard data.
	/// This is just a thin derivation of <see cref="Buffer"/>.
	/// </summary>
	public class ClipboardAll : Buffer
	{
		/// <summary>
		/// Constructor that just passes the data to the base.
		/// </summary>
		/// <param name="obj">The data to pass to the base.</param>
		//public ClipboardAll(byte[] obj)
		//  : base(obj)
		//{
		//}

		public new static object Call(object @this, params object[] args) => @this is Class cls ? cls.Call(args) : Errors.TypeErrorOccurred(@this, typeof(Class));

		public override object __New(params object[] args)
		{
			byte[] bytes;

			if (args == null || args.Length == 0 || args[0] == null)
			{
				bytes = Env.CaptureClipboardAllBytes();
			}
			else
			{
				var size = args.Length > 1 ? args[1].Al(long.MinValue) : long.MinValue;
				bytes = Env.ExtractClipboardAllBytes(args[0], size);
			}

			return base.__New([bytes]);
		}

		public ClipboardAll(params object[] args) : base(args) { }
	}

	public partial class Ks
	{
		/// <summary>
		/// Compiles and executes a C# script dynamically in a separate process.
		/// </summary>
		/// <param name="obj0">The script source result (as any object with a valid string representation).</param>
		/// <param name="obj1">Whether to run the process as async (provide non-unset non-zero value) or not.
		/// <param name="obj2">An optional name for the dynamically generated program; defaults to "DynamicScript".</param>
		/// <param name="obj3">Optional executable path used to run the generated assembly; defaults to the currently running process.</param>
		/// If provided a callback function then it's considered async and the function <c>Call</c> method will be
		/// invoked when the process exits with the ProcessInfo as the only argument.</param>
		/// <returns>
		/// Returns a <see cref="ProcessInfo"/> wrapper around the spawned process.
		/// If compilation fails without a flagged error, returns <c>null</c>.
		/// </returns>
		/// <exception cref="Error">Throws any compilation as <see cref="Error"/>.</exception>
		public static object RunScript(object obj0, object obj1 = null, object obj2 = null, object obj3 = null)
		{
			string script = obj0.As();
			IFuncObj cb = null;

			if (obj1 != null)
				cb = Functions.Func(obj1);

			string name = obj2.As("DynamicScript");
			string result = null;
			byte[] compiledBytes = null;
			var ch = new CompilerHelper();
			(compiledBytes, result) = ch.CompileCodeToByteArray([script], name);

			if (compiledBytes == null)
				return Errors.ErrorOccurred(result);

			var scriptProcess = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = obj3 == null ? Path.GetFileName(Environment.ProcessPath) : obj3.As(),
					Arguments = "--script --assembly *",
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					UseShellExecute = false,
					CreateNoWindow = true
				}
			};

			var info = new ProcessInfo(scriptProcess);
			scriptProcess.EnableRaisingEvents = true;
			scriptProcess.Exited += (object sender, EventArgs e) => cb?.Call(info);
			_ = scriptProcess.Start();

			using (var writer = new BinaryWriter(scriptProcess.StandardInput.BaseStream, Encoding.UTF8, leaveOpen: true))
			{
				writer.Write(compiledBytes.Length);
				writer.Write(compiledBytes);
				writer.Flush();
			}

			if (!ForceBool(obj1 ?? false))
				scriptProcess.WaitForExit();

			return info;
		}

		///
		/// <summary>
		/// Parses the provided script source or filename and validates it by invoking the parser.
		/// On success this method returns <c>""</c>. On failure it returns a string containing
		/// the formatted compiler errors.
		/// </summary>
		/// <param name="code">The script source or filename to parse.</param>
		/// <returns>
		/// Returns <see cref=""/> when parsing completes with no compiler errors and a valid compilation unit.
		/// If the compiler reports errors or the first compilation unit is null, a string containing compiler error messages
		/// (and warnings if present) is returned.
		/// </returns>
		/// <exception cref="Exception">
		/// Any unexpected exception thrown by the underlying <see cref="CompilerHelper"/> APIs will propagate to the caller.
		/// </exception>
		public static object ParseScript(object code)
		{
			var ch = new CompilerHelper();
			var (units, errs) = ch.CreateCompilationUnitFromFile([code.As()]);

			if (errs.HasErrors || units[0] == null)
			{
				var (errors, warnings) = CompilerHelper.GetCompilerErrors(errs);

				var sb = new StringBuilder(1024);

				if (!string.IsNullOrEmpty(errors))
					_ = sb.Append(errors);

				return sb.ToString();
			}
			return DefaultObject;
		}
	}
}
