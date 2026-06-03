using Keysharp.Builtins;
using Microsoft.CodeAnalysis;

namespace Keysharp.Internals.Scripting
{
	/// <summary>
	/// This is a lightweight version of the Keysharp main program, which is used to parse and compile
	/// scripts dynamically specifically from compiled scripts. This version doesn't support emitting
	/// an executable(that would require the HostModel package dependency), and all errors/messages
	/// are output to StdOut. The compiled script used to run this must be shipped with CodeAnalysis
	/// and CodeDom dlls.
	/// </summary>
	internal class Runner
	{
		private static readonly CompilerHelper ch = new ();

		public static int Run(string[] args)
		{
			try
			{
				var script = new Script();
				var asm = Assembly.GetEntryAssembly();
				var exePath = Path.GetFullPath(asm.Location);

				if (exePath.IsNullOrEmpty()) //Happens when the assembly is dynamically loaded from memory
					exePath = Environment.ProcessPath;

				var exeName = Path.GetFileNameWithoutExtension(exePath);
				var exeDir = Path.GetFullPath(Path.GetDirectoryName(exePath));
				var transpile = false;
				var loadAsm = false;
				var asmType = $"{Keywords.MainNamespaceName}.{Keywords.MainClassName}";
				var asmMethod = "Main";
				var scriptName = string.Empty;
				var fromstdin = false;
				var validate = false;

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
						case "validate":
							script.ValidateThenExit = validate = true;
							break;

						case "asm":
						case "assembly":
							loadAsm = true;
							break;

						case "transpile":
							transpile = true;
							break;

						case "include":
							i++;
							break;

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
					byte[] asmBytes;

					if (scriptName == "*")
					{
						using var stdin = Console.OpenStandardInput();
						using var asmStream = new MemoryStream();
						stdin.CopyTo(asmStream);
						asmBytes = asmStream.ToArray();
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

					return method.Invoke(null, [script.ScriptArgs]).Ai();
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

				var (unit, errs) = ch.CreateCompilationUnitFromFile(scriptName);

				if (errs.HasErrors || unit == null)
					return HandleCompilerErrors(errs, scriptName, path, "Compiling script to DOM");

				//If they want to write out the code, place it in the same folder as the script, with the same name, and .cs extension.
				if (transpile)
				{
					var code = PrettyPrinter.Print(unit);
#if DEBUG
					var normalized = unit.NormalizeWhitespace("\t", Environment.NewLine).ToString();
					if (code != normalized)
						throw new Exception("Code formatting mismatch");
#endif
					Console.Write(code);
					return 0;
				}

				var (results, ms, compileexc) = ch.Compile(unit, namenoext, exeDir);

				try
				{
					if (results == null)
					{
						return Message($"Compiling C# code to executable: {(compileexc != null ? compileexc.Message : string.Empty)}", true);
					}
					else if (results.Success)
					{
						ms.Seek(0, SeekOrigin.Begin);
						var arr = ms.ToArray();
						CompilerHelper.compiledasm = Assembly.Load(arr);
					}
					else
					{
						return HandleCompilerErrors(results.Diagnostics, scriptName, path, "Compiling C# code to executable", compileexc != null ? compileexc.Message : string.Empty);
					}
				}
				finally
				{
					ms?.Dispose();
				}

				if (validate)
				{
					return 0;//Any other error condition returned 1 already.
				}

				if (CompilerHelper.compiledasm == null)
					throw new Exception("Compilation failed.");

				var program = CompilerHelper.compiledasm.GetType($"{Keywords.MainNamespaceName}.{Keywords.MainClassName}");
				var main = program.GetMethod("Main");
				return main.Invoke(null, [script.ScriptArgs]).Ai();
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
				return Message(msg, true);
			}
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

		private static bool IsCompiledScriptInput(string path)
		{
			var ext = Path.GetExtension(path);

			if (ext.Equals(".cks", StringComparison.OrdinalIgnoreCase)
					|| ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
				return true;

			return false;
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
				? Directory.GetDirectories(hostRoot).Select(Path.GetFileName).Where(x => x.StartsWith(dotNetMajorVersion)).OrderByDescending(x => new Version(x.Contains("-rc", StringComparison.OrdinalIgnoreCase) ? x.Substring(0, x.IndexOf("-rc", StringComparison.OrdinalIgnoreCase)) : x)).FirstOrDefault()
				: "";
#elif LINUX
			var dir = Directory.GetDirectories(@"/lib/dotnet/sdk/").Select(System.IO.Path.GetFileName).Where(x => x.StartsWith(dotNetMajorVersion)).OrderByDescending(x => new Version(x)).FirstOrDefault();
#elif WINDOWS
			var dir = Directory.GetDirectories(@"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Host.win-x64\").Select(Path.GetFileName).Where(x => x.StartsWith(dotNetMajorVersion)).OrderByDescending(x => new Version(x.Contains("-rc", StringComparison.OrdinalIgnoreCase) ? x.Substring(0, x.IndexOf("-rc", StringComparison.OrdinalIgnoreCase)) : x)).FirstOrDefault();
#else
			var dir = "";
#endif
			return dir;
		}

		private static int HandleCompilerErrors(ImmutableArray<Diagnostic> diagnostics, string filename, string path, string desc, string message = "")
		{
			var errstr = CompilerHelper.HandleCompilerErrors(diagnostics, filename, desc, message);

			if (errstr != "")
			{
				//System.IO.File.WriteAllText($"{Keysharp.Builtins.Accessors.A_AppData}/Keysharp/compiler_errors.txt", errstr);
				_ = Message(errstr, true);
				return 1;
			}

			return 0;
		}

		private static int HandleCompilerErrors(CompilerErrorCollection results, string filename, string path, string desc, string message = "")
		{
			var (errors, warnings) = CompilerHelper.GetCompilerErrors(results, filename);
			var failed = errors != "";

			if (failed)
			{
				var sb = new StringBuilder(1024);
				_ = sb.AppendLine($"{desc} failed.");

				if (!string.IsNullOrEmpty(errors))
					_ = sb.Append(errors);

				if (!string.IsNullOrEmpty(warnings))
					_ = sb.Append(warnings);

				if (!string.IsNullOrEmpty(message))
					_ = sb.Append(message);

				var errstr = sb.ToString();
				//System.IO.File.WriteAllText($"{Keysharp.Builtins.Accessors.A_AppData}/Keysharp/compiler_errors.txt", errstr);
				_ = Message(errstr, true);
			}
			else
			{
				try
				{
					//System.IO.File.Delete($"{Keysharp.Builtins.Accessors.A_AppData}/Keysharp/compiler_errors.txt");
				}
				catch { }
			}

			return failed ? 1 : 0;
		}

		private static int Message(string text, bool error)
		{
			if (error)
			{
				ch.ReportCompilerErrors(text);
			}
			else
			{
				Console.Write(text);
			}

			return error ? 1 : 0;
		}
	}
}
