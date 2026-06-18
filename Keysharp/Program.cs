using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using Keysharp.Builtins;
using Keysharp.Internals.ExtensionMethods;
using Keysharp.Internals.Scripting;
using Keysharp.Internals.Strings;
using Keysharp.Parsing;
using Keysharp.Runtime;
using Microsoft.NET.HostModel.AppHost;
#if WINDOWS
using Microsoft.Win32;
using System.Windows.Forms;
#elif OSX
using Eto.Forms;
using System.Threading;
#endif

namespace Keysharp.Main
{
	/// <summary>
	/// The Keysharp launcher. Command-line parsing lives in <see cref="Runner.Parse"/> (in Keysharp.Core, so
	/// it is shared with a compiled script's "/script" path). This launcher only adds the
	/// two things that must stay out of Keysharp.Core: the compile daemon, and building an executable
	/// (which needs the Microsoft.NET.HostModel package). Runner returns those as deferred results for us to
	/// carry out here.
	/// </summary>
	public static class Program
	{
		private static readonly CompilerHelper ch = new ();

		internal static Version Version => Assembly.GetExecutingAssembly().GetName().Version;

		[STAThread]
		public static int Main(string[] args)
		{
			// Run Script's static constructor eagerly so any error messageboxes render correctly even before a
			// Script instance exists (e.g. a daemon compile failure reported below).
			System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Script).TypeHandle);

#if OSX
			// On macOS, double-clicking a .ks/.ahk file sends an Apple Event rather than a command-line
			// argument. Receive it via Eto's AppDelegate before the normal arg-parsing pipeline.
			if (args.Length == 0)
				args = WaitForMacOsDocumentOpen();
#elif WINDOWS
			if (TryEditWithKeyview(args, out var editResult))
				return editResult;
#endif

			var command = Runner.Parse(args);

			// Daemon fast path: a plain source run can offload compilation to the shared daemon and run the
			// returned bytes here, so this lean launcher never loads the parser/Roslyn. KEYSHARP_DAEMON forces it
			// on/off; if unset, release builds use it and debug builds do not.
			if (command.Kind == CliCommandKind.RunSource
					&& !command.FromStdin
					&& !command.Validate
					&& !command.Transpile
					&& command.KeysharpArgs.Length == 0
					&& ShouldUseDaemon())
			{
				switch (CompileClient.CompileViaServer(command.ScriptName, out var daemonBytes, out var daemonErr))
				{
					case CompileDaemonStatus.Compiled:
						return RunCompiledBytes(daemonBytes, command.ScriptArgs);

					case CompileDaemonStatus.CompileFailed:
						return ReportDaemonCompileFailure(daemonErr, command.ScriptName);

						// Unreachable + unspawnable: fall through to the in-process runner below.
				}
			}

			return command.Kind switch
			{
				CliCommandKind.CompileExe => CompileToExe(command),
				CliCommandKind.Daemon => HandleDaemon(command.DaemonArgs),
#if WINDOWS
				CliCommandKind.Install => InstallToPath(command.ExeDir),
				CliCommandKind.Uninstall => RemoveFromPath(command.ExeDir),
				CliCommandKind.CloseInstances => CloseRunningInstances(command.ExeDir, command.ScriptArgs),
#endif
				_ => Runner.Execute(command),
			};
		}

		// Compile-server control, deferred to us by Runner because CompileServer lives in this launcher.
		// daemonArgs[0] is the "--daemon" switch itself: "--daemon" starts it; "--daemon stop" stops the
		// running one; "--daemon ping <script>" compiles via a running daemon and reports only (no spawn/run).
		private static int HandleDaemon(string[] daemonArgs)
		{
			var sub = daemonArgs.Length > 1 ? (Runner.TryGetSwitch(daemonArgs[1], out var daemonSub) ? daemonSub : daemonArgs[1]) : null;

			if (string.Equals(sub, "stop", StringComparison.OrdinalIgnoreCase))
			{
				DaemonCoordinator.StopOwner();
				return 0;
			}

			if (string.Equals(sub, "ping", StringComparison.OrdinalIgnoreCase) && daemonArgs.Length > 2)
			{
				var st = CompileClient.TryCompile(daemonArgs[2], out var b, out var err);
				Console.WriteLine(st switch
				{
					CompileDaemonStatus.Compiled => $"daemon ping: OK, {b.Length} bytes",
					CompileDaemonStatus.CompileFailed => $"daemon ping: COMPILE ERROR\n{err}",
					_ => "daemon ping: FAIL, no daemon reachable",
				});
				return st == CompileDaemonStatus.Compiled ? 0 : 1;
			}

			return CompileServer.Run();
		}

		// Builds an executable from a script. Deferred to us by Runner because HostWriter.CreateAppHost
		// requires the Microsoft.NET.HostModel package, which Keysharp.Core deliberately does not reference.
		private static int CompileToExe(CliCommand r)
		{
			var asm = Assembly.GetExecutingAssembly();
			var exePath = Path.GetFullPath(asm.Location);

			if (exePath.IsNullOrEmpty())
				exePath = Environment.ProcessPath;

			var exeDir = Path.GetFullPath(Path.GetDirectoryName(exePath));
			var namenoext = r.NameNoExt;
			var scriptdir = r.ScriptDir;
			var path = r.OutPath;

			if (r.DestPath.Length != 0)
			{
				if (r.DestPath == "*")
					return Runner.Message("--dest * is only valid with --compile asm.", true);

				(path, scriptdir, namenoext) = ResolveCompileExeOutput(r.DestPath, scriptdir, namenoext);
			}

			// The parser resolves built-ins through Script.TheScript, so a parse-context Script is needed for
			// the compile; dispose it once the assembly bytes are produced.
			byte[] arr;
			string compileResult;

			using (var script = new Script())
				(arr, compileResult) = ch.CompileCodeToByteArray(r.ScriptName, namenoext, exeDir, r.MinimalExe, false);

			if (arr == null)
				return Runner.Message(compileResult, true);

			var finalPath = "";

			try
			{
				var ver = GetLatestDotNetVersion();
				var outputRuntimeConfigPath = Path.ChangeExtension(path, "runtimeconfig.json");
				var currentRuntimeConfigPath = Path.ChangeExtension(exePath, "runtimeconfig.json");
				var outputDllPath = path + ".dll";
				File.WriteAllBytes(outputDllPath, arr);
				File.Copy(currentRuntimeConfigPath, outputRuntimeConfigPath, true);
				var outputDepsConfigPath = Path.ChangeExtension(path, "deps.json");
				var currentDepsConfigPath = Path.ChangeExtension(exePath, "deps.json");
				File.Copy(currentDepsConfigPath, outputDepsConfigPath, true);
#if LINUX
				finalPath = path;
				HostWriter.CreateAppHost(
					appHostSourceFilePath: @$"/lib/dotnet/sdk/{ver}/AppHostTemplate/apphost",
					appHostDestinationFilePath: finalPath,
					appBinaryFilePath: $"{namenoext}.dll",
					windowsGraphicalUserInterface: false,
					assemblyToCopyResorcesFrom: outputDllPath);
#elif OSX
				finalPath = path;
				var rid = RuntimeInformation.RuntimeIdentifier.Contains("osx-arm64", StringComparison.OrdinalIgnoreCase) ? "osx-arm64" : "osx-x64";
				var appHostCandidates = new[]
				{
					$"/usr/local/share/dotnet/packs/Microsoft.NETCore.App.Host.{rid}/{ver}/runtimes/{rid}/native/apphost",
					$"/usr/share/dotnet/packs/Microsoft.NETCore.App.Host.{rid}/{ver}/runtimes/{rid}/native/apphost"
				};
				var appHostPath = appHostCandidates.FirstOrDefault(File.Exists) ?? appHostCandidates[0];
				HostWriter.CreateAppHost(
					appHostSourceFilePath: appHostPath,
					appHostDestinationFilePath: finalPath,
					appBinaryFilePath: $"{namenoext}.dll",
					windowsGraphicalUserInterface: false,
					assemblyToCopyResorcesFrom: outputDllPath);
#elif WINDOWS
				finalPath = $"{path}.exe";
				HostWriter.CreateAppHost(
					appHostSourceFilePath: @$"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Host.win-x64\{ver}\runtimes\win-x64\native\apphost.exe",
					appHostDestinationFilePath: finalPath,
					appBinaryFilePath: $"{namenoext}.dll",
					windowsGraphicalUserInterface: true,
					assemblyToCopyResorcesFrom: outputDllPath);
#endif

				if (string.Compare(exeDir, scriptdir, true) != 0)
				{
					var deps = r.MinimalExe ? ["Keysharp.Core.dll"]
							   : CompilerHelper.requiredManagedDependencies
							   .Concat(CompilerHelper.requiredNativeDependencies.Select(CompilerHelper.GetRidNativeDependencyPath));

					//For a minimal exe, every managed/native dependency except Keysharp.Core is embedded into the
					//script assembly (see CompilerHelper.CompileFromTree), so only Keysharp.Core.dll must be copied
					//alongside; it loads the embedded assemblies and native libs at runtime. For a full exe, copy
					//Keysharp.Core and the other dependencies from the install path to the script's folder. Without
					//them, the compiled exe cannot be run in a standalone manner.
					foreach (var dep in deps)
					{
						var depPath = CompilerHelper.requiredNativeDependencies.Contains(Path.GetFileName(dep), StringComparer.OrdinalIgnoreCase)
									  ? CompilerHelper.ResolveAppNativeDependencyPath(exeDir, Path.GetFileName(dep))
									  : Path.Combine(exeDir, dep);

						if (File.Exists(depPath))
						{
							var destPath = Path.Combine(scriptdir, dep);
							var destDir = Path.GetDirectoryName(destPath);

							if (!string.IsNullOrEmpty(destDir))
								_ = Directory.CreateDirectory(destDir);

							File.Copy(depPath, destPath, true);
						}
					}
				}
			}
			catch (Exception writeex)
			{
				return Runner.Message($"Writing executable to {finalPath} failed: {writeex.Message}", true);
			}

			return 0;
		}

		private static bool ShouldUseDaemon()
		{
			// KEYSHARP_DAEMON forces the daemon on/off; if unset (or unrecognized), default on for release
			// builds and off for debug builds.
			var value = Environment.GetEnvironmentVariable("KEYSHARP_DAEMON")?.Trim();
			return Conversions.ParseBoolish(value)
#if DEBUG
				   ?? false;
#else
				   ?? true;
#endif
		}

		// Loads a precompiled script assembly (bytes returned by the compile server) and invokes its entry
		// point in this process. No compile-context Script is created here: the compiled assembly's own Main
		// creates its runtime Script.
		private static int RunCompiledBytes(byte[] arr, string[] scriptArgs)
		{
			try
			{
				CompilerHelper.compiledasm = Assembly.Load(arr);
				var program = CompilerHelper.compiledasm.GetType($"{Keywords.MainNamespaceName}.{Keywords.MainClassName}");
				var main = program.GetMethod("Main");
#if DEBUG
				Ks.OutputDebugLine("Running compiled code (daemon).");
#endif
				Environment.ExitCode = main.Invoke(null, [scriptArgs]).Ai();
			}
			catch (Exception ex)
			{
				if (ex is TargetInvocationException)
					ex = ex.InnerException;

				var error = new StringBuilder();
				_ = error.AppendLine("Execution error:\n");
				_ = error.AppendLine($"{ex.GetType().Name}: {ex.Message}");
				_ = error.AppendLine();
				_ = error.AppendLine(ex.StackTrace);
				Environment.ExitCode = Runner.Message(error.ToString(), true);
			}

			return Environment.ExitCode;
		}

		private static int ReportDaemonCompileFailure(string error, string scriptPath)
		{
			using var script = new Script();
			script.scriptPath = Path.GetFullPath(scriptPath);
			script.scriptName = Path.GetFileName(script.scriptPath);
			return Runner.Message(error, true);
		}

		internal static string GetLatestDotNetVersion()
		{
#if OSX
			var rid = RuntimeInformation.RuntimeIdentifier.Contains("osx-arm64", StringComparison.OrdinalIgnoreCase) ? "osx-arm64" : "osx-x64";
			var hostRoots = new[]
			{
				$"/usr/local/share/dotnet/packs/Microsoft.NETCore.App.Host.{rid}/",
				$"/usr/share/dotnet/packs/Microsoft.NETCore.App.Host.{rid}/"
			};
			var hostRoot = hostRoots.FirstOrDefault(Directory.Exists);
			var dir = hostRoot != null
				? Directory.GetDirectories(hostRoot).Select(Path.GetFileName).Where(x => x.StartsWith(Script.dotNetMajorVersion)).OrderByDescending(x => new Version(x.Contains("-rc", StringComparison.OrdinalIgnoreCase) ? x.Substring(0, x.IndexOf("-rc", StringComparison.OrdinalIgnoreCase)) : x)).FirstOrDefault()
				: "";
#elif LINUX
			var dir = Directory.GetDirectories(@"/lib/dotnet/sdk/").Select(System.IO.Path.GetFileName).Where(x => x.StartsWith(Script.dotNetMajorVersion)).OrderByDescending(x => new Version(x)).FirstOrDefault();
#elif WINDOWS
			var dir = Directory.GetDirectories(@"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Host.win-x64\").Select(Path.GetFileName).Where(x => x.StartsWith(Script.dotNetMajorVersion)).OrderByDescending(x => new Version(x.Contains("-rc", StringComparison.OrdinalIgnoreCase) ? x.Substring(0, x.IndexOf("-rc", StringComparison.OrdinalIgnoreCase)) : x)).FirstOrDefault();
#else
			var dir = "";
#endif
			return dir;
		}

		private static (string PathNoExtension, string OutputDir, string NameNoExt) ResolveCompileExeOutput(string outputPath, string scriptDir, string scriptNameNoExt)
		{
			var fullPath = Path.GetFullPath(outputPath);
			var isDirectory = Directory.Exists(fullPath) || outputPath.EndsWith(Path.DirectorySeparatorChar) || outputPath.EndsWith(Path.AltDirectorySeparatorChar);

			if (isDirectory)
			{
				var outputDir = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				_ = Directory.CreateDirectory(outputDir);
				return (Path.Combine(outputDir, scriptNameNoExt), outputDir, scriptNameNoExt);
			}

			var outputDirForFile = Path.GetDirectoryName(fullPath);

			if (outputDirForFile.IsNullOrEmpty())
				outputDirForFile = Environment.CurrentDirectory;
			else
				_ = Directory.CreateDirectory(outputDirForFile);

			var nameNoExt = Path.GetFileNameWithoutExtension(fullPath);

			if (nameNoExt.IsNullOrEmpty())
				nameNoExt = scriptNameNoExt;

			return (Path.Combine(outputDirForFile, nameNoExt), outputDirForFile, nameNoExt);
		}

#if OSX

		// Start a minimal Eto Application, wait up to 1 s for macOS to deliver the "open file" Apple
		// Event (via AppDelegate.OpenFile / OpenFiles), then stop the loop and return the first path.
		// If no event arrives the method returns an empty array and the normal "no script" error follows.
		// The Application instance is deliberately NOT disposed: EnsureEtoApplication() reuses it for
		// GUI scripts that call RunMainWindow → app.Run() afterwards.
		private static string[] WaitForMacOsDocumentOpen()
		{
			string openedPath = null;
			var app = Application.Instance ?? new Application();

			void OnFileOpened(string path)
			{
				Volatile.Write(ref openedPath, path);
				Eto.Mac.AppDelegate.FileOpened -= OnFileOpened;
				app.AsyncInvoke(app.Quit);
			}

			Eto.Mac.AppDelegate.FileOpened += OnFileOpened;

			var timeoutThread = new Thread(() =>
			{
				Thread.Sleep(1000);

				if (Volatile.Read(ref openedPath) == null)
					app.AsyncInvoke(app.Quit);
			}) { IsBackground = true };
			timeoutThread.Start();

			app.Run();

			Eto.Mac.AppDelegate.FileOpened -= OnFileOpened;
			var path = Volatile.Read(ref openedPath);
			return path != null ? [path] : [];
		}

#endif

#if WINDOWS

		private static bool TryEditWithKeyview(string[] args, out int result)
		{
			result = 0;

			if (args.Length < 2 || !args[0].TrimStart(Keywords.DashSlash).Equals("edit", StringComparison.OrdinalIgnoreCase))
				return false;

			var keyviewExe = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath), "Keyview.exe");

			if (!File.Exists(keyviewExe))
			{
				Console.Error.WriteLine($"Keyview executable not found: {keyviewExe}");
				result = 1;
				return true;
			}

			_ = Process.Start(new ProcessStartInfo
			{
				FileName = keyviewExe,
				ArgumentList = { args[1] },
				UseShellExecute = false
			});
			return true;
		}

		private static int InstallToPath(string path)
		{
			var keyName = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
			var oldPath = (string)Registry.LocalMachine.CreateSubKey(keyName).GetValue("PATH", "", RegistryValueOptions.DoNotExpandEnvironmentNames);//Get non-expanded PATH environment variable.

			if (!oldPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(s => string.Compare(s, path, true) == 0))
				Registry.LocalMachine.CreateSubKey(keyName).SetValue("PATH", oldPath + (oldPath.EndsWith(';') ? path : $";{path}"), RegistryValueKind.ExpandString);//Set the path as an an expandable string with the passed in value included.

			RegisterShellIntegration(path);
			return 0;
		}

		private static int RemoveFromPath(string path)
		{
			DaemonCoordinator.StopOwner();
			var keyName = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
			var oldPath = (string)Registry.LocalMachine.CreateSubKey(keyName).GetValue("PATH", "", RegistryValueOptions.DoNotExpandEnvironmentNames);//Get non-expanded PATH environment variable.
			var newPath = string.Join(';', oldPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => string.Compare(s, path, true) != 0));
			Registry.LocalMachine.CreateSubKey(keyName).SetValue("PATH", newPath, RegistryValueKind.ExpandString);//Restore the old path to what it was without the passed in value included.
			UnregisterShellIntegration();
			return 0;
		}

		// Closes every process belonging to THIS install — scripts launched through Keysharp.exe, the compile
		// daemon, and the Keyview editor — so a locked Keysharp.exe / Keysharp.Core.dll can be replaced or
		// deleted. Windows refuses to overwrite a running image or a loaded DLL, so an upgrade or uninstall
		// performed while Keysharp is running otherwise fails or defers files to a reboot, which can leave a
		// stale-version compile daemon serving against the new binaries. This is a manual command; the MSI does
		// its own version-independent close before InstallValidate (see Keysharp.Install/package-windows.ps1).
		// Best-effort: a kill failure never blocks; only an explicit "No" at the optional prompt returns nonzero.
		private static int CloseRunningInstances(string exeDir, string[] args)
		{
			// Stop the compile daemon through its coordinator first: it may run as a different user, and
			// StopOwner kills by the recorded PID regardless. The scan below mops up anything still holding files.
			try { DaemonCoordinator.StopOwner(); } catch { }

			var dir = Path.GetFullPath(exeDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var selfId = Environment.ProcessId;

			var targets = Process.GetProcessesByName("Keysharp")
						  .Concat(Process.GetProcessesByName("Keyview"))
						  .Where(p =>
			{
				if (p.Id == selfId)
					return false;

				try
				{
					var moduleDir = Path.GetDirectoryName(Path.GetFullPath(p.MainModule.FileName));
					return string.Equals(moduleDir?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), dir, StringComparison.OrdinalIgnoreCase);
				}
				catch
				{
					return false; // Exited, or a process we cannot inspect (different elevation/user).
				}
			}).ToArray();

			if (targets.Length == 0)
				return 0;

			// Confirm before closing when a UILevel >= 5 is passed. Uses MessageBox.Show, matching how the rest
			// of Keysharp reports to the user (see Runner.Message).
			if (args.Length > 0 && int.TryParse(args[0], out var uiLevel) && uiLevel >= 5)
			{
				var prompt = $"Keysharp is currently running ({targets.Length} process(es)).\n\n"
							 + "It must be closed to continue. Close it now?\n\n"
							 + "Choose No to cancel.";

				if (MessageBox.Show(prompt, "Keysharp", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
					return 1; // Caller asked to confirm and the user declined.
			}

			foreach (var p in targets)
			{
				try
				{
					// Ask GUI scripts / Keyview to close gracefully (runs OnExit, lets them save), then force-kill
					// anything that ignores it or has no window (background scripts, the console daemon).
					if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow() && p.WaitForExit(3000))
						continue;

					if (!p.HasExited)
					{
						p.Kill();
						_ = p.WaitForExit(5000);
					}
				}
				catch { /* already gone, or cannot be killed; best-effort */ }
				finally { p.Dispose(); }
			}

			return 0;
		}

		private static void RegisterShellIntegration(string path)
		{
			var exe = Path.Combine(path, "Keysharp.exe");
			var keyviewExe = Path.Combine(path, "Keyview.exe");
			var command = $"\"{exe}\" \"%1\"";
			var compileCommand = $"\"{exe}\" --compile \"%1\"";
			var editCommand = $"\"{keyviewExe}\" \"%1\"";

			using (var ext = Registry.LocalMachine.CreateSubKey(@"Software\Classes\.ks"))
				ext.SetValue("", "Keysharp");

			using (var type = Registry.LocalMachine.CreateSubKey(@"Software\Classes\Keysharp"))
				type.SetValue("", "Keysharp script");

			using (var icon = Registry.LocalMachine.CreateSubKey(@"Software\Classes\Keysharp\DefaultIcon"))
				icon.SetValue("", $"\"{exe}\",0");

			using (var open = Registry.LocalMachine.CreateSubKey(@"Software\Classes\Keysharp\shell\open\command"))
				open.SetValue("", command);

			using (var ext = Registry.LocalMachine.CreateSubKey(@"Software\Classes\.cks"))
				ext.SetValue("", "Keysharp.CompiledScript");

			using (var type = Registry.LocalMachine.CreateSubKey(@"Software\Classes\Keysharp.CompiledScript"))
				type.SetValue("", "Compiled Keysharp script");

			using (var icon = Registry.LocalMachine.CreateSubKey(@"Software\Classes\Keysharp.CompiledScript\DefaultIcon"))
				icon.SetValue("", $"\"{exe}\",0");

			using (var open = Registry.LocalMachine.CreateSubKey(@"Software\Classes\Keysharp.CompiledScript\shell\open\command"))
				open.SetValue("", command);

			// Older installers registered this iconless verb under the .ks ProgID. Remove it so only the
			// SystemFileAssociations verb below remains.
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\Keysharp\shell\compile", false);
			RegisterCompileVerb(".ahk", compileCommand, exe);
			RegisterCompileVerb(".ks", compileCommand, exe);

			if (File.Exists(keyviewExe))
			{
				RegisterEditVerb(".ahk", editCommand, keyviewExe);
				RegisterEditVerb(".ks", editCommand, keyviewExe);
			}
		}

		private static void RegisterCompileVerb(string extension, string command, string exe)
		{
			using var shell = Registry.LocalMachine.CreateSubKey($@"Software\Classes\SystemFileAssociations\{extension}\shell\KeysharpCompile");
			shell.SetValue("", "Compile");
			shell.SetValue("Icon", $"\"{exe}\",0");

			using var commandKey = shell.CreateSubKey("command");
			commandKey.SetValue("", command);
		}

		private static void RegisterEditVerb(string extension, string command, string exe)
		{
			using var shell = Registry.LocalMachine.CreateSubKey($@"Software\Classes\SystemFileAssociations\{extension}\shell\KeyviewEdit");
			shell.SetValue("", "Edit with Keyview");
			shell.SetValue("Icon", $"\"{exe}\",0");

			using var commandKey = shell.CreateSubKey("command");
			commandKey.SetValue("", command);
		}

		private static void UnregisterShellIntegration()
		{
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\.cks", false);
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\.ks", false);
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\Keysharp", false);
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\Keysharp.CompiledScript", false);
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.ahk\shell\KeysharpCompile", false);
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.ks\shell\KeysharpCompile", false);
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.ahk\shell\KeyviewEdit", false);
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.ks\shell\KeyviewEdit", false);
		}

#endif
	}
}
