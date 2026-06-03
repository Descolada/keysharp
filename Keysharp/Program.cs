using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
#if WINDOWS
using System.Windows.Forms;
#else
using Eto.Forms;
using MessageBoxIcon = Eto.Forms.MessageBoxType;
#endif
using Keysharp.Builtins;
using Keysharp.Parsing;
using Keysharp.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.NET.HostModel.AppHost;
using Microsoft.Win32;

namespace Keysharp.Main
{
	/// <summary>
	/// The main program which interprets command line arguments, reads and compiles the code, loads
	/// the resulting assembly and invokes the entry-point method.
	/// Similar but simplified logic is present in Keysharp.Runtime.Runner, so changes here should
	/// likely be done there as well.
	/// </summary>
	public static class Program
	{
		private static readonly CompilerHelper ch = new ();

		internal static Version Version => Assembly.GetExecutingAssembly().GetName().Version;

		[STAThread]
		public static int Main(string[] args)
		{
			// Compile-server operations live under the "--daemon" namespace, detected before the Script below
			// is constructed (the daemon owns the single warm, reused parse-context Script):
			//   --daemon                 start the daemon
			//   --daemon stop            stop the running daemon
			//   --daemon ping <script>   [diagnostic] compile <script> via a running daemon and report only;
			//                            does not spawn a daemon and does not run the script
			if (args.Length > 0 && TryGetSwitch(args[0], out var daemonOption) && daemonOption.Equals("daemon", StringComparison.OrdinalIgnoreCase))
			{
				var sub = args.Length > 1 && TryGetSwitch(args[1], out var daemonSubOption) ? daemonSubOption : args.Length > 1 ? args[1] : null;

				if (string.Equals(sub, "stop", StringComparison.OrdinalIgnoreCase))
				{
					DaemonCoordinator.StopOwner();
					return 0;
				}

				if (string.Equals(sub, "ping", StringComparison.OrdinalIgnoreCase) && args.Length > 2)
				{
					var st = CompileClient.TryCompile(args[2], out var b, out var err);
					Console.WriteLine(st switch
					{
						CompileDaemonStatus.Compiled => $"daemon ping: OK, {b.Length} bytes",
						CompileDaemonStatus.CompileFailed => $"daemon ping: COMPILE ERROR\n{err}",
						_ => "daemon ping: FAIL, no daemon reachable",
					});
					return st == CompileDaemonStatus.Compiled ? 0 : 1;
				}

				// Bare "--daemon" (or "--daemon" with no recognized subcommand/script): start the daemon.
				return CompileServer.Run();
			}

			// Default lean path: a plain "keysharp <script> [args]" run (no Keysharp flags) compiles via the
			// shared daemon - spawning one if needed - and runs the returned bytes in THIS process. Because
			// compilation happened in the daemon, this process never calls Roslyn's Emit, so
			// Microsoft.CodeAnalysis/ANTLR are never loaded here: the script keeps only the runtime footprint,
			// not the compiler's. KEYSHARP_DAEMON can force this on/off; if unset, release builds use it
			// and debug builds do not. If no daemon can be reached or spawned, we fall through anyway.
			if (ShouldUseDaemon()
					&& TryGetPlainScriptRun(args, out var fastPath, out var fastArgs)
					&& File.Exists(fastPath))
			{
				switch (CompileClient.CompileViaServer(fastPath, out var arr, out var derr))
				{
					case CompileDaemonStatus.Compiled:
						return RunCompiledBytes(arr, fastArgs);

					case CompileDaemonStatus.CompileFailed:
						return ReportDaemonCompileFailure(derr, fastPath);

						// Unreachable + unspawnable: fall through to the in-process compiler below.
				}
			}

			Task writeExeTask = null;
			Task writeCodeTask = null;
			MethodInfo runtimeEntryPoint = null;
			object[] runtimeEntryArgs = null;

			try
			{
				using var script = new Script();//One Script object will exist here, then another will be created when the script runs.
				var asm = Assembly.GetExecutingAssembly();
				var exePath = Path.GetFullPath(asm.Location);

				if (exePath.IsNullOrEmpty()) //Happens when the assembly is dynamically loaded from memory
					exePath = Environment.ProcessPath;

				var exeName = Path.GetFileNameWithoutExtension(exePath);
				var exeDir = Path.GetFullPath(Path.GetDirectoryName(exePath));
				var nsname = typeof(Program).Namespace;
				var transpile = false;
				var compileExe = false;
				var compileMinimalExe = false;
				var loadAsm = false;
				var asmType = $"{Keywords.MainNamespaceName}.{Keywords.MainClassName}";
				var asmMethod = "Main";
				var scriptName = string.Empty;
				var fromstdin = false;
				var validate = false;
				var compileAsm = false;
				var compileAsmPath = "";
				var compileDestPath = "";

				for (var i = 0; i < args.Length; i++)
				{
					if (!TryGetSwitch(args[i], out var option))
					{
						SetInput(args, i, script, ref scriptName);
						break;
					}

					var opt = option.ToLowerInvariant();

					if (opt.StartsWith("asm:", StringComparison.OrdinalIgnoreCase)
							|| opt.StartsWith("assembly:", StringComparison.OrdinalIgnoreCase))
					{
						loadAsm = true;
						var entryPoint = option.Substring(option.IndexOf(':') + 1);

						if (!TryParseAsmEntryPoint(entryPoint, out asmType, out asmMethod, out var entryError))
							return Message(entryError, true);

						continue;
					}

					switch (opt)
					{
						case "version":
						case "v":
							return Message($"{asm.GetName().Version}", false);

						case "validate":
							script.ValidateThenExit = validate = true;
							break;

						case "about":
							var license = exeDir + Path.DirectorySeparatorChar + "license.txt";
							return Message(new StreamReader(license).ReadToEnd(), false);

						case "asm":
						case "assembly":
							loadAsm = true;
							break;

						case "transpile":
							transpile = true;
							break;

						case "compile":
							if (i + 1 >= args.Length)
							{
								compileAsm = true;
								break;
							}

							switch (args[i + 1].ToLowerInvariant())
							{
								case "exe":
									i++;
									compileExe = true;
									break;

								case "exe-min":
									i++;
									compileMinimalExe = true;
									compileExe = true;
									break;

								case "asm":
								case "dll":
									i++;
									compileAsm = true;
									break;

								default:
									compileAsm = true;
									break;
							}

							break;

						case "dest":
							if (i + 1 >= args.Length || TryGetSwitch(args[i + 1], out _))
								return Message("--dest requires an output path.", true);

							compileDestPath = args[++i];
							break;

						case "include":
							i++;
							break;
#if WINDOWS

						case "install"://To be called by the installer during installation.
							InstallToPath(exeDir);
							return 0;

						case "uninstall"://To be called by the uninstaller during uninstallation.
							RemoveFromPath(exeDir);
							return 0;
#endif

						default:
							SetInput(args, i, script, ref scriptName);
							break;
					}

					if (!string.IsNullOrEmpty(scriptName))
						break;
				}

				//Message($"Operating off of script: {script} in current dir: {Environment.CurrentDirectory} for full path: {Path.GetFullPath(script)}", false);

				if (!string.IsNullOrEmpty(scriptName) && IsCompiledScriptInput(scriptName))
					loadAsm = true;

				if (string.IsNullOrEmpty(scriptName) && !loadAsm)
				{
					var dirs = new string[]
					{
						$"{Environment.CurrentDirectory}{Path.DirectorySeparatorChar}{exeName}.ahk",//Current executable dir.
						$"{Environment.CurrentDirectory}{Path.DirectorySeparatorChar}{exeName}.ks"
					};

					foreach (var dir in dirs)
					{
						if (File.Exists(dir))
						{
							scriptName = dir;
							break;
						}
					}
				}

				if (loadAsm && string.IsNullOrEmpty(scriptName))
					return Message("No assembly was specified.", true);

				if (loadAsm)
				{
					{
						byte[] asmBytes;

						if (scriptName == "*")
						{
							using var stdin = Console.OpenStandardInput();
							using var ms = new MemoryStream();
							stdin.CopyTo(ms);
							asmBytes = ms.ToArray();
						}
						else
							asmBytes = File.ReadAllBytes(scriptName);

						Assembly scriptAsm = Assembly.Load(asmBytes);
						Type type = scriptAsm.GetType(asmType);

						if (type == null)
							return Message($"Could not find assembly {asmType}", true);

						MethodInfo method = type.GetMethod(asmMethod);

						if (method == null)
							return Message($"Could not find method {asmMethod}", true);

						runtimeEntryPoint = method;
						runtimeEntryArgs = [script.ScriptArgs];
						script.Dispose();
						goto InvokeRuntimeEntryPoint;
					}
				}

				if (scriptName == "*")
				{
					fromstdin = true;
					string s;
					var sb = new StringBuilder(2048);

					while ((s = Console.ReadLine()) != null)
						sb.AppendLine(s);

					scriptName = sb.ToString();
				}

				if (string.IsNullOrEmpty(scriptName))
					return Message("No script was specified, no text was read from stdin, and no script named keysharp.ahk was found in the current folder or your documents folder.", true);

				if (!fromstdin && !File.Exists(scriptName))
					return Message($"Could not find the script file {scriptName}.", true);

				if (!compileAsm && !compileExe && compileDestPath.Length != 0)
					return Message("--dest is only valid with --compile.", true);

				string namenoext, path, scriptdir;

				if (!fromstdin)
				{
					namenoext = Path.GetFileNameWithoutExtension(scriptName);
					scriptdir = Path.GetDirectoryName(scriptName);
					path = $"{scriptdir}{Path.DirectorySeparatorChar}{namenoext}";
				}
				else
				{
					namenoext = "pipestdin";
					scriptdir = Environment.CurrentDirectory;
					path = $".{Path.DirectorySeparatorChar}{namenoext}";
				}

				if (compileAsm && compileAsmPath.Length == 0)
					compileAsmPath = ResolveCompileAsmOutput(compileDestPath, scriptdir, namenoext);

				if (compileExe && compileDestPath.Length != 0)
				{
					if (compileDestPath == "*")
						return Message("--dest * is only valid with --compile asm.", true);

					(path, scriptdir, namenoext) = ResolveCompileExeOutput(compileDestPath, scriptdir, namenoext);
				}

				byte[] arr = null;
				string result = null;
				(arr, result) = ch.CompileCodeToByteArray(scriptName, namenoext, exeDir, compileMinimalExe, transpile);

				//If they want to write out the code, place it in the same folder as the script, with the same name, and .cs extension.
				if (transpile)
				{
					writeCodeTask = Task.Run(() =>
					{
						var codePath = $"{path}.cs";

						try
						{
							using (var sourceWriter = new StreamWriter(codePath))
							{
								sourceWriter.WriteLine(result);
							}
						}
						catch (Exception writeex)
						{
							Message($"Writing code to {codePath} failed: {writeex.Message}", true);
						}
					});
				}

				if (arr == null)
					return Message(result, true);

				if (compileAsm)
				{
					try
					{
						using var outStream = compileAsmPath == "*" ? Console.OpenStandardOutput() : File.Create(compileAsmPath);
						outStream.Write(arr, 0, arr.Length);
					}
					catch (Exception writeex)
					{
						return Message($"Writing assembly to {compileAsmPath} failed: {writeex.Message}", true);
					}

					writeCodeTask?.Wait();//In case --transpile was also requested.
					return 0;//Assembly written; do not run (running would also corrupt stdout for "*").
				}

				if (compileExe)
				{
					writeExeTask = Task.Run(() =>
					{
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
							//Message($"About to write executable to {path}.exe/dll.\r\nappHostDestinationFilePath: {path}.exe\r\nappBinaryFilePath: {namenoext}.dll\r\nassemblyToCopyResorcesFrom: {path}.dll", false);
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
								var deps = compileMinimalExe ? ["Keysharp.Core.dll"]
										   : CompilerHelper.requiredManagedDependencies
										   .Concat(CompilerHelper.requiredNativeDependencies.Select(CompilerHelper.GetRidNativeDependencyPath));

								//For a minimal exe, every managed/native dependency except Keysharp.Core is embedded
								//into the script assembly (see CompilerHelper.CompileFromTree), so only Keysharp.Core.dll
								//must be copied alongside; it loads the embedded assemblies and native libs at runtime.
								//For a full exe, copy Keysharp.Core and the other dependencies from the install path to
								//the folder the script resides in. Without them, the compiled exe cannot be run in a standalone manner.
								//MessageBox.Show($"scriptdir = {scriptdir}");
								//MessageBox.Show($"About to copy from {ksCorePath} to {Path.Combine(scriptdir, "Keysharp.Builtins.dll")}");
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
							Message($"Writing executable to {finalPath} failed: {writeex.Message}", true);
						}
					});
				}

				if (transpile || compileExe)
				{
					writeExeTask?.Wait();
					writeCodeTask?.Wait();
					return 0;//Artifacts written; do not run unless a dedicated run action is requested later.
				}

				CompilerHelper.compiledasm = Assembly.Load(arr);

				if (validate)
				{
					writeExeTask?.Wait();
					writeCodeTask?.Wait();
					return 0;//Any other error condition returned 1 already.
				}

				if (CompilerHelper.compiledasm == null)
					throw new Exception("Compilation failed.");

				var program = CompilerHelper.compiledasm.GetType($"{Keywords.MainNamespaceName}.{Keywords.MainClassName}");
				var main = program.GetMethod("Main");
				runtimeEntryPoint = main;
				runtimeEntryArgs = [script.ScriptArgs];
				script.Dispose();

			InvokeRuntimeEntryPoint:
#if DEBUG
				Ks.OutputDebugLine("Running compiled code.");
#endif
				Environment.ExitCode = runtimeEntryPoint.Invoke(null, runtimeEntryArgs).Ai();
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
				var msg = error.ToString();
				var trace = $"{Accessors.A_AppData}/Keysharp/execution_errors.txt";

				try
				{
					//if (System.IO.File.Exists(trace))
					//  System.IO.File.Delete(trace);
					//System.IO.File.WriteAllText(trace, msg);
				}
				catch (Exception exx)
				{
					msg += $"\n\n{exx.Message}";
				}
				finally
				{
					writeExeTask?.Wait();
					writeCodeTask?.Wait();
				}

				Environment.ExitCode = Message(msg, true);
			}

			writeExeTask?.Wait();
			writeCodeTask?.Wait();

#if DEBUG
			Builtins.Debug.OutputDebug("Running compiled code.");
#endif

			return Environment.ExitCode;
		}

		private static bool TryGetSwitch(string value, out string option)
		{
			option = null;

			if (string.IsNullOrEmpty(value) || value.Length < 2)
				return false;

			if (value[0] != '-' && value[0] != '/')
				return false;

			option = value.TrimStart(Keywords.DashSlash);
			return option.Length > 0;
		}

		private static void SetInput(string[] args, int index, Script script, ref string scriptName)
		{
			scriptName = args[index] == "*" ? "*" : Path.GetFullPath(args[index]);
			script.ScriptArgs = [.. args.Skip(index + 1)];
			script.KeysharpArgs = [.. args.Take(index + 1)];
		}

		private static bool TryParseAsmEntryPoint(string value, out string typeName, out string methodName, out string error)
		{
			typeName = $"{Keywords.MainNamespaceName}.{Keywords.MainClassName}";
			methodName = "Main";
			error = null;

			if (string.IsNullOrWhiteSpace(value))
			{
				error = "Assembly entry point cannot be empty. Use --asm or --asm:Namespace.Type.Method.";
				return false;
			}

			var lastDot = value.LastIndexOf('.');

			if (lastDot <= 0 || lastDot == value.Length - 1)
			{
				error = $"Invalid assembly entry point '{value}'. Use --asm:Namespace.Type.Method.";
				return false;
			}

			typeName = value.Substring(0, lastDot);
			methodName = value.Substring(lastDot + 1);
			return true;
		}

		// Recognizes the common "keysharp <script> [scriptargs...]" invocation with no Keysharp options, which
		// is eligible for the compile-server fast path. Any leading flag (e.g. --compile, --validate, --asm,
		// /switch) disqualifies it so those modes keep using the full in-process flow below. Stdin ("*") is
		// likewise left to the normal flow.
		private static bool TryGetPlainScriptRun(string[] args, out string scriptPath, out string[] scriptArgs)
		{
			scriptPath = null;
			scriptArgs = [];

			if (args.Length == 0)
				return false;

			// The first token decides: if it is a Keysharp flag the full in-process flow handles it; otherwise
			// it is the script path and everything after it are the script's own arguments.
			if (TryGetSwitch(args[0], out _))
				return false;

			scriptPath = args[0];
			scriptArgs = [.. args.Skip(1)];
			return scriptPath != "*" && !IsCompiledScriptInput(scriptPath);
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

		private static string ResolveCompileAsmOutput(string outputPath, string scriptDir, string scriptNameNoExt)
		{
			if (outputPath.Length == 0)
				return Path.Combine(scriptDir, $"{scriptNameNoExt}.cks");

			if (outputPath == "*")
				return "*";

			var fullPath = Path.GetFullPath(outputPath);
			var isDirectory = Directory.Exists(fullPath) || outputPath.EndsWith(Path.DirectorySeparatorChar) || outputPath.EndsWith(Path.AltDirectorySeparatorChar);

			if (isDirectory)
			{
				var outputDir = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				_ = Directory.CreateDirectory(outputDir);
				return Path.Combine(outputDir, $"{scriptNameNoExt}.cks");
			}

			var outputDirForFile = Path.GetDirectoryName(fullPath);

			if (!outputDirForFile.IsNullOrEmpty())
				_ = Directory.CreateDirectory(outputDirForFile);

			return fullPath;
		}

		private static bool ShouldUseDaemon()
		{
			var value = Environment.GetEnvironmentVariable("KEYSHARP_DAEMON");

			if (!string.IsNullOrWhiteSpace(value))
			{
				value = value.Trim();

				if (value.Equals("1", StringComparison.OrdinalIgnoreCase)
						|| value.Equals("true", StringComparison.OrdinalIgnoreCase)
						|| value.Equals("yes", StringComparison.OrdinalIgnoreCase)
						|| value.Equals("on", StringComparison.OrdinalIgnoreCase))
					return true;

				if (value.Equals("0", StringComparison.OrdinalIgnoreCase)
						|| value.Equals("false", StringComparison.OrdinalIgnoreCase)
						|| value.Equals("no", StringComparison.OrdinalIgnoreCase)
						|| value.Equals("off", StringComparison.OrdinalIgnoreCase))
					return false;
			}

#if DEBUG
			return false;
#else
			return true;
#endif
		}

		private static bool IsCompiledScriptInput(string path)
		{
			var ext = Path.GetExtension(path);

			if (ext.Equals(".cks", StringComparison.OrdinalIgnoreCase)
					|| ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
				return true;

			return false;
		}

		// Loads a precompiled script assembly (produced by the compile server) and invokes its entry point in
		// this process, mirroring the tail of Main's normal flow. No compile-context Script is created here:
		// compilation already happened in the daemon, and the compiled Main creates its own runtime Script.
		private static int RunCompiledBytes(byte[] arr, string[] scriptArgs)
		{
			try
			{
				CompilerHelper.compiledasm = Assembly.Load(arr);
				var program = CompilerHelper.compiledasm.GetType($"{Keywords.MainNamespaceName}.{Keywords.MainClassName}");
				var main = program.GetMethod("Main");
#if DEBUG
				Ks.OutputDebugLine("Running compiled code (served).");
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
				Environment.ExitCode = Message(error.ToString(), true);
			}

			return Environment.ExitCode;
		}

		private static int ReportDaemonCompileFailure(string error, string scriptPath)
		{
			using var script = new Script();
			script.scriptPath = Path.GetFullPath(scriptPath);
			script.scriptName = Path.GetFileName(script.scriptPath);
			return Message(error, true);
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

#if WINDOWS

		internal static void InstallToPath(string path)
		{
			var keyName = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
			var oldPath = (string)Registry.LocalMachine.CreateSubKey(keyName).GetValue("PATH", "", RegistryValueOptions.DoNotExpandEnvironmentNames);//Get non-expanded PATH environment variable.

			if (!oldPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(s => string.Compare(s, path, true) == 0))
				Registry.LocalMachine.CreateSubKey(keyName).SetValue("PATH", oldPath + (oldPath.EndsWith(';') ? path : $";{path}"), RegistryValueKind.ExpandString);//Set the path as an an expandable string with the passed in value included.

			RegisterShellIntegration(path);
		}

		internal static void RemoveFromPath(string path)
		{
			DaemonCoordinator.StopOwner();
			var keyName = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
			var oldPath = (string)Registry.LocalMachine.CreateSubKey(keyName).GetValue("PATH", "", RegistryValueOptions.DoNotExpandEnvironmentNames);//Get non-expanded PATH environment variable.
			var newPath = string.Join(';', oldPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => string.Compare(s, path, true) != 0));
			Registry.LocalMachine.CreateSubKey(keyName).SetValue("PATH", newPath, RegistryValueKind.ExpandString);//Restore the old path to what it was without the passed in value included.
			UnregisterShellIntegration();
		}

		private static void RegisterShellIntegration(string path)
		{
			var exe = Path.Combine(path, "Keysharp.exe");
			var command = $"\"{exe}\" \"%1\"";
			var compileCommand = $"\"{exe}\" --compile \"%1\"";

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
		}

		private static void RegisterCompileVerb(string extension, string command, string exe)
		{
			using var shell = Registry.LocalMachine.CreateSubKey($@"Software\Classes\SystemFileAssociations\{extension}\shell\KeysharpCompile");
			shell.SetValue("", "Compile");
			shell.SetValue("Icon", $"\"{exe}\",0");

			using var commandKey = shell.CreateSubKey("command");
			commandKey.SetValue("", command);
		}

		private static void UnregisterShellIntegration()
		{
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\.cks", false);
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\Keysharp.CompiledScript", false);
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\Keysharp\shell\compile", false);
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.ahk\shell\KeysharpCompile", false);
			Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.ks\shell\KeysharpCompile", false);
		}

#endif

		private static int Message(string text, bool error)
		{
			const string marker = "\nusing static ";
			int idx = text.IndexOf(marker, StringComparison.Ordinal);

			if (idx >= 0)
				text = text.Substring(0, idx);

			if (error)
			{
				try
				{
					if (ch.ReportCompilerErrors(text))
						return RestartCurrentProcess();
				}
				catch (Exception ex)
				{
					var fallback = $"{text}\n\nError reporting failed: {ex.GetType().Name}: {ex.Message}";

					if (TryShowInfoMessageBox(fallback))
						_ = MessageBox.Show(fallback, "Keysharp", MessageBoxButtons.OK, MessageBoxIcon.Error);
					else
						Console.Error.WriteLine(fallback);
				}
			}
			else
			{
				if (TryShowInfoMessageBox(text))
					_ = MessageBox.Show(text, "Keysharp", MessageBoxButtons.OK, MessageBoxIcon.Information);
				else
					Console.WriteLine(text);

				_ = Ks.OutputDebugLine(text);
			}

			return error ? 1 : 0;
		}

		// Relaunches this process with the same arguments. Used when the user clicks "Reload"
		// on a *compile-time* syntax-error dialog, which happens before the script ever runs.
		// Flow.Reload() can't be used here: it posts the restart to the UI thread and waits for
		// the running app to exit, but at this point the only message loop was the modal error
		// dialog (now closed), so there is no pump to process the post and no script to exit.
		// This also re-passes the managed dll path when running as "dotnet Keysharp.dll <script>".
		private static int RestartCurrentProcess()
		{
			var processPath = Environment.ProcessPath;

			if (processPath.IsNullOrEmpty())
				return 1;

			try
			{
				var args = Environment.GetCommandLineArgs();
				var start = new ProcessStartInfo
				{
					FileName = processPath,
					UseShellExecute = false,
				};
				var processName = Path.GetFileNameWithoutExtension(processPath);
				var includeArg0 = args.Length > 0
					&& processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
					&& args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
				var firstArg = includeArg0 ? 0 : 1;

				for (var i = firstArg; i < args.Length; i++)
					start.ArgumentList.Add(args[i]);

				_ = Process.Start(start);
				return 0;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"Reload failed: {ex.Message}");
				return 1;
			}
		}

		private static bool TryShowInfoMessageBox(string text)
		{
#if WINDOWS
			return true;
#else
			if (Script.IsHeadless || Script.IsTestHost)
				return false;

			try
			{
				if (Application.Instance == null)
					_ = new Application();

				return true;
			}
			catch (Exception ex)
			{
				_ = Ks.OutputDebugLine($"Unable to initialize UI for message box: {ex.Message}");
				return false;
			}
#endif
		}
	}
}
