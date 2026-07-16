namespace Keysharp.Builtins
{
	/// <summary>
	/// Public interface for environment-related functions.
	/// </summary>
	public static class Env
	{

		/// <summary>
		/// Waits until the clipboard contains data.
		/// </summary>
		/// <param name="timeout">If omitted, the function will wait indefinitely. Otherwise, it will wait no longer than this many seconds.<br/>
		/// To wait for a fraction of a second, specify a floating-point number, for example, 0.25 to wait for a maximum of 250 milliseconds.
		/// </param>
		/// <param name="waitFor">If omitted, it defaults to 0 (wait only for text or files).<br/>
		/// Otherwise, specify one of the following numbers to indicate what to wait for:<br/>
		/// 0: The function is more selective, waiting specifically for text or files to appear("text" includes anything that would produce text when you paste into Notepad).<br/>
		/// 1: The function waits for data of any kind to appear on the clipboard.
		/// </param>
		/// <returns>True if it did not time out, else false.</returns>
		public static bool ClipWait(object timeout = null, object waitFor = null)
		{
			var to = timeout.Ad(double.MinValue);
			var type = waitFor.Ab();
			var checktime = to != double.MinValue;
			long frequency = 100;
			var timeoutMs = checktime ? (long)(Math.Abs(to) * 1000) : long.MaxValue;
			var deadline = checktime ? Environment.TickCount64 + timeoutMs : long.MaxValue;

			while (true)
			{
				if (ClipboardMatchesWaitCondition(type))
					return true;

				if (checktime)
				{
					var remaining = deadline - Environment.TickCount64;

					if (remaining <= 0)
						return false;

					_ = Flow.Sleep(Math.Min(frequency, remaining));
				}
				else
					_ = Flow.Sleep(frequency);
			}
		}

		private static bool ClipboardMatchesWaitCondition(bool waitForAny)
		{
			var script = Script.TheScript;

			return Script.InvokeOnUIThread(() =>
			{
#if WINDOWS
				return !waitForAny
					? Clipboard.ContainsText() || Clipboard.ContainsFileDropList()
					: !Ks.IsClipboardEmpty();
#else
				var clip = Clipboard.Instance;

				if (clip == null)
					return false;

				if (!waitForAny)
				{
					// Match AHK's "text or files" behavior more closely on Eto/Gtk.
					// HTML is text-convertible, and URIs are how file copies surface on non-Windows.
					return clip.ContainsText
						|| clip.ContainsHtml
						|| clip.ContainsUris;
				}

				return !Ks.IsClipboardEmpty();
#endif
			});
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
			if ("source ~/.bashrc".Bash() != 0)
				Ks.OutputDebugLine("EnvUpdate: failed to source ~/.bashrc");
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
				var command = Runner.Parse(newArgs);

				if (command.RequiresLauncher)
					throw new Exception("This option is only available from the Keysharp launcher, not from a compiled script: --compile exe/exe-min, --daemon, --install, --uninstall.");

				Environment.ExitCode = Runner.Execute(command);
				throw new Flow.UserRequestedExitException();
			}

			A_Args.array.AddRange(args);
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
					var script = Script.TheScript;
					if (script.ClipFunctions.ModifyEventHandlers(fo, addRemove.Al(1)))
						script.UpdateClipboardMonitoring();

					return DefaultObject;
				}
				else
				return Errors.TypeErrorOccurred(callback, typeof(FuncObj), DefaultObject);
		}

		/// <summary>
		/// Retrieves dimensions of system objects, and other system properties.
		/// </summary>
		/// <param name="property">The variable to store the result.</param>
		/// <returns>This function returns the value of the specified system property.</returns>
		public static object SysGet(object property)
		{
#if !WINDOWS
			var sm = property is Keysharp.Internals.Os.SystemMetric en ? en : (SystemMetric)property.Ai();
			var (screenWidth, screenHeight) = Monitor.GetPrimaryScreenSize();
			var (workWidth, workHeight) = Monitor.GetPrimaryWorkAreaSize();

			switch (sm)
			{
				case SystemMetric.SM_CMONITORS:
					return Monitor.MonitorGetCount();

				case SystemMetric.SM_CXSCREEN:
					return screenWidth;

				case SystemMetric.SM_CYSCREEN:
					return screenHeight;

				case SystemMetric.SM_CXFULLSCREEN:
					return workWidth;

				case SystemMetric.SM_CYFULLSCREEN:
					return workHeight;

				case SystemMetric.SM_CXMAXIMIZED:
					return workWidth;

				case SystemMetric.SM_CYMAXIMIZED:
					return workHeight;

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
					_ = "last reboot".Bash(out var bootsOutput);
					var boots = bootsOutput.SplitLines().ToList();

					if (boots.Count > 0)
					{
						if (boots[0].Contains("recovery", StringComparison.OrdinalIgnoreCase))
							return NetworkUp() ? 2L : (object)1L;
					}

					return 0L;
				}

				case SystemMetric.SM_MOUSEWHEELPRESENT:
					return "xinput --list --long".Bash(out var wheelOut) == 0
						&& wheelOut.Contains("button wheel", StringComparison.OrdinalIgnoreCase) ? 1L : 0L;

				case SystemMetric.SM_REMOTESESSION:
					return "echo $SSH_TTY".Bash(out var sshOut1) == 0 && sshOut1 != "" ? 1L : 0L;

				case SystemMetric.SM_SHUTTINGDOWN:
					return "systemctl is-system-running".Bash(out var systemStateOut) == 0
						&& systemStateOut.Contains("stopping", StringComparison.OrdinalIgnoreCase) ? 1L : 0L;

				case SystemMetric.SM_REMOTECONTROL:
					return "echo $SSH_TTY".Bash(out var sshOut2) == 0 && sshOut2 != "" ? 1L : 0L;

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

		internal static byte[] ExtractClipboardAllBytes(object data, long size = long.MinValue)
		{
			if (data == null)
				return System.Array.Empty<byte>();

			if (data is byte[] ba)
				return CopyClipboardBytes(ba, size);

			if (data is Array arr)
				return CopyClipboardBytes(arr.ToByteArray().ToArray(), size);

			if (Reflections.TryGetPtrProperty(data, out var ptr))
			{
				var sourceLength = size;

				if (sourceLength == long.MinValue)
					_ = Reflections.TryGetSizeProperty(data, out sourceLength);//0/false when no Size; the > 0 guard below handles it.

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
			// May be queried before a Script exists (e.g. compiler-error reporting during early argument
			// parsing), in which case there are no Keysharp args to search.
			var args = Script.TheScript?.KeysharpArgs;

			if (args == null)
				return null;

			if (startsWith)
				return args.FirstOrDefault(x => (x.StartsWith('-')
						|| x.StartsWith('/')) && x.Trim(DashSlash).StartsWith(arg, StringComparison.OrdinalIgnoreCase));
			else
				return args.FirstOrDefault(x => (x.StartsWith('-')
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
			var args = Script.TheScript?.KeysharpArgs;

			if (args == null)
				return null;

			for (var i = 0; i < args.Length; i++)
			{
				if ((args[i].StartsWith('-') || args[i].StartsWith('/')) && (startsWith ? args[i].TrimStart(DashSlash).StartsWith(arg, StringComparison.OrdinalIgnoreCase) : args[i].Contains(arg, StringComparison.OrdinalIgnoreCase)))
					if (i < args.Length - 1)
						return args[i + 1];
			}

			return null;
		}

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
			if ("xinput list --long".Bash(out var inputStr) != 0)
				return 3L;

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
			if ("xinput list --name-only".Bash(out var deviceNamesOut) != 0
				|| "xinput list --id-only".Bash(out var deviceIdsOut) != 0)
				return false;

			var deviceNames = deviceNamesOut.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			//foreach (var name in deviceNames)
			//  Ks.OutputDebugLine($"{name}");
			var deviceIds = deviceIdsOut.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

			//foreach (var id in deviceIds)
			//  Ks.OutputDebugLine($"{id}");

			if (deviceNames.Length == deviceIds.Length)
			{
				for (var i = 0; i < deviceNames.Length && !swapped; i++)
				{
					if (!deviceNames[i].Contains("xtest", StringComparison.OrdinalIgnoreCase))
					{
						if ($"xinput get-button-map {deviceIds[i]}".Bash(out var buttonStr) != 0)
							continue;
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
			return "ip link show".Bash(out var linkStateOut) == 0
				&& linkStateOut.Contains("state up", StringComparison.OrdinalIgnoreCase);
#else
			_ = "ifconfig".Bash(out var output);
			return output.Contains("status: active", StringComparison.OrdinalIgnoreCase);
#endif
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

		public new static object staticCall(object @this, params object[] args) => @this is Class cls ? cls.Call(args) : Errors.TypeErrorOccurred(@this, typeof(Class));

		public override object __New(params object[] args)
		{
			byte[] bytes;

			if (args == null || args.Length == 0 || args[0] == null)
			{
				bytes = Platform.Clipboard.CaptureAll();
			}
			else
			{
				var size = args.Length > 1 && args[1] is not null ? args[1].ToLong() : long.MinValue;
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
		/// <param name="obj2">An optional name for the dynamically generated program; defaults to "*".</param>
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

			string name = obj2?.As();
			string result = null;
			byte[] compiledBytes;
			var ext = Path.GetExtension(script);

			// A precompiled assembly file (.cks/.dll) is run as-is; only source needs compiling. This lets
			// callers ship and launch a precompiled script (e.g. WindowSpy.cks) for faster startup.
			if (File.Exists(script) && (ext.Equals(".cks", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)))
			{
				compiledBytes = File.ReadAllBytes(script);
			}
			else
			{
				var ch = new CompilerHelper();
				(compiledBytes, result) = ch.CompileCodeToByteArray(script, name);

				if (compiledBytes == null)
					return Errors.ErrorOccurred(result);
			}

			// Relaunch a Keysharp host that understands "--script --assembly *" (it reads the assembly
			// bytes piped below). Environment.ProcessPath is only our app when published; under the dotnet
			// host (e.g. "dotnet Keysharp.dll" while debugging from an IDE) it is "dotnet", and handing
			// Keysharp's args to dotnet makes it exit without reading stdin -- so the pipe write below
			// would time out. Prefer the native apphost that sits beside the entry assembly; fall back to
			// "dotnet <entry.dll>", and finally to ProcessPath (single-file publish has no separate dll).
			string launcher = obj3?.As();
			var launcherArgs = "--script --assembly *";

			if (string.IsNullOrEmpty(launcher))
			{
#if WINDOWS
				const string appHostExtension = ".exe";
#else
				const string appHostExtension = "";
#endif
				var entryAsm = Assembly.GetEntryAssembly()?.Location;
				var entryDir = string.IsNullOrEmpty(entryAsm) ? null : Path.GetDirectoryName(entryAsm);
				var appHost = entryDir == null ? null
							  : Path.Combine(entryDir, Path.GetFileNameWithoutExtension(entryAsm) + appHostExtension);

				if (appHost != null && File.Exists(appHost))
					launcher = appHost;
				else if (entryDir != null && string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
					(launcher, launcherArgs) = (Environment.ProcessPath, $"\"{entryAsm}\" {launcherArgs}");
				else
					launcher = Environment.ProcessPath;
			}

			var scriptProcess = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = launcher,
					Arguments = launcherArgs,
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

			// Write the raw assembly bytes and close stdin; the child ("--assembly *") reads to EOF.
			// This must match the framing the launcher expects (Program.Main's "--assembly *" loader and
			// Keyview): a length prefix here gets loaded as part of the assembly and fails with "Bad IL format",
			// and leaving stdin open would hang the child's read-to-EOF.
			using (var stdin = scriptProcess.StandardInput.BaseStream)
			{
				stdin.Write(compiledBytes, 0, compiledBytes.Length);
				stdin.Flush();
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
			var (unit, errs) = ch.CreateCompilationUnitFromFile(code.As());

			if (errs.HasErrors || unit == null)
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
