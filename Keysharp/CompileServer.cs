using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Keysharp.Parsing;
using Keysharp.Runtime;

namespace Keysharp.Main
{
	internal enum CompileDaemonStatus
	{
		/// <summary>The daemon compiled the script; assembly bytes are available.</summary>
		Compiled,
		/// <summary>The daemon was reached but the script failed to compile; an error message is available.</summary>
		CompileFailed,
		/// <summary>No compatible daemon could be reached (and, where applicable, none could be spawned).</summary>
		Unreachable
	}

	/// <summary>
	/// Identifies the exact Keysharp build, used to key the compile-server pipe. A client must only ever
	/// talk to a daemon running the SAME Keysharp.exe AND Keysharp.Core.dll, otherwise it could receive
	/// assembly bytes compiled against different references/runtime. We fingerprint both modules by their
	/// Module Version IDs (MVIDs): the compiler stamps a distinct MVID into every build of an assembly, so
	/// any change to either binary changes the fingerprint and a mismatched client simply spawns its own
	/// daemon instead of reusing an incompatible one.
	/// </summary>
	internal static class KeysharpFingerprint
	{
		internal static string Value { get; } = Compute();

		private static string Compute()
		{
			// typeof(KeysharpFingerprint) lives in Keysharp.dll/exe; typeof(Script) lives in Keysharp.Core.dll.
			var keysharpMvid = typeof(KeysharpFingerprint).Module.ModuleVersionId;
			var coreMvid = typeof(Script).Module.ModuleVersionId;

			Span<byte> buf = stackalloc byte[32];
			_ = keysharpMvid.TryWriteBytes(buf.Slice(0, 16));
			_ = coreMvid.TryWriteBytes(buf.Slice(16, 16));
			Span<byte> hash = stackalloc byte[32];
			_ = SHA256.HashData(buf, hash);
			return Convert.ToHexString(hash.Slice(0, 8)); // 16 hex chars is plenty to avoid collisions.
		}
	}

	/// <summary>
	/// Ensures at most one compile daemon runs per user: a starting daemon kills any live daemon of a
	/// DIFFERENT build and takes over, and defers (exits) only if an IDENTICAL build is already running.
	/// Build identity is the fingerprint-keyed pipe name (distinct MVIDs => distinct pipe), so "different
	/// build" is just "different pipe name" — no version ordering is implied; the most recently started
	/// daemon wins. Coordination is a tiny per-user lock file guarded by a named mutex so the
	/// compare-and-kill is atomic across processes. Stale entries (dead PID, or a reused PID whose process
	/// name no longer matches) are treated as no owner, so the scheme self-heals.
	/// </summary>
	internal static class DaemonCoordinator
	{
		private static readonly string LockFile = Path.Combine(Path.GetTempPath(), $"keysharp-compile-server-{Environment.UserName}.lock");
		private static readonly string MutexName = $@"Local\keysharp-compile-coord-{Environment.UserName}";

		internal static bool TryBecomeOwner(string pipeName)
		{
			using var mutex = new Mutex(false, MutexName);

			try { _ = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
			catch (AbandonedMutexException) { /* previous owner crashed holding it; we now hold it */ }

			try
			{
				var owner = Read();

				if (owner != null && owner.Pid != Environment.ProcessId && IsLiveDaemon(owner))
				{
					if (string.Equals(owner.Pipe, pipeName, StringComparison.Ordinal))
						return false; // An identical-build daemon already owns the slot.

					TryKill(owner.Pid); // Any different build: replace it so only one runs.
				}

				Write(pipeName);
				return true;
			}
			finally
			{
				try { mutex.ReleaseMutex(); } catch { }
			}
		}

		internal static void ReleaseOwnership()
		{
			using var mutex = new Mutex(false, MutexName);

			try { _ = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
			catch (AbandonedMutexException) { }
			catch { return; }

			try
			{
				var owner = Read();

				if (owner != null && owner.Pid == Environment.ProcessId && File.Exists(LockFile))
					File.Delete(LockFile);
			}
			catch { }
			finally
			{
				try { mutex.ReleaseMutex(); } catch { }
			}
		}

		internal static void StopOwner()
		{
			using var mutex = new Mutex(false, MutexName);

			try { _ = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
			catch (AbandonedMutexException) { }
			catch { return; }

			try
			{
				var owner = Read();

				if (owner != null && owner.Pid != Environment.ProcessId && IsLiveDaemon(owner))
					TryKill(owner.Pid);

				if (File.Exists(LockFile))
					File.Delete(LockFile);
			}
			catch { }
			finally
			{
				try { mutex.ReleaseMutex(); } catch { }
			}
		}

		private sealed class Owner
		{
			internal int Pid;
			internal string ProcName;
			internal string Pipe;
		}

		// Lock file is a single line: "pid|procName|pipeName".
		private static Owner Read()
		{
			try
			{
				if (!File.Exists(LockFile))
					return null;

				var parts = File.ReadAllText(LockFile).Split('|');

				if (parts.Length >= 3 && int.TryParse(parts[0], out var pid))
					return new Owner { Pid = pid, ProcName = parts[1], Pipe = parts[2] };
			}
			catch { }

			return null;
		}

		private static void Write(string pipeName)
		{
			var procName = Process.GetCurrentProcess().ProcessName;
			File.WriteAllText(LockFile, $"{Environment.ProcessId}|{procName}|{pipeName}");
		}

		// A recorded PID counts as a live daemon only if it is running AND its process name matches the
		// recorded one, guarding against killing an unrelated process that reused the PID.
		private static bool IsLiveDaemon(Owner owner)
		{
			try
			{
				using var p = Process.GetProcessById(owner.Pid);
				return !p.HasExited && string.Equals(p.ProcessName, owner.ProcName, StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		private static void TryKill(int pid)
		{
			try
			{
				using var p = Process.GetProcessById(pid);
				p.Kill();
				_ = p.WaitForExit(5000);
				CompileServer.Log($"killed older compile daemon (pid {pid}).");
			}
			catch (Exception ex)
			{
				CompileServer.Log($"could not kill older daemon (pid {pid}): {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Compile daemon ("--daemon" mode). Holds one warm <see cref="CompilerHelper"/> and one reused
	/// parse-context <see cref="Script"/>, accepts script paths over a per-build/per-user named pipe, and
	/// returns compiled assembly bytes so a thin launcher can run them in a lean process that never loads
	/// Roslyn/ANTLR (see <see cref="CompileClient"/> and Program.RunCompiledBytes).
	///
	/// Correctness constraints (see CompilerHelper.ResetScriptForParse):
	///   - <see cref="Script.TheScript"/> is process-global, so compiles MUST be serialized. The accept
	///     loop is single-threaded and runs on the (STA) thread that created the Script.
	///   - Parsing only reads the built-in-only ReflectionsData and writes scriptPath/scriptName + thread
	///     vars, so one Script is reused across parses via ResetScriptForParse.
	/// </summary>
	internal static class CompileServer
	{
		// Bump when the wire protocol changes. (The fingerprint already separates incompatible builds; this
		// guards against a protocol change within an otherwise-identical build during development.)
		internal const int ProtocolVersion = 1;

		// Idle shutdown so an abandoned daemon does not linger forever (mirrors VBCSCompiler behavior).
		private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(4);

		// All daemon-side diagnostics go to stderr with a common prefix.
		internal static void Log(string message) => Console.Error.WriteLine($"[keysharp --daemon] {message}");

		/// <summary>
		/// Pipe name keyed on protocol + build fingerprint + user, so a client never connects to a daemon
		/// built from different Keysharp/Keysharp.Core binaries, or to another user's daemon.
		/// </summary>
		internal static string PipeName => $"keysharp-compile-{ProtocolVersion}-{KeysharpFingerprint.Value}-{Environment.UserName}";

		internal static int Run()
		{
			// Only one daemon runs per user. A daemon of any different build already up is killed and we take
			// over; if an identical-build daemon already owns the slot we defer and exit. This keeps exactly
			// one daemon alive across builds, even though their fingerprint-keyed pipe names differ.
			if (!DaemonCoordinator.TryBecomeOwner(PipeName))
			{
				Log("an identical-build compile daemon already owns the slot; exiting.");
				return 0;
			}

			try
			{
				return Listen();
			}
			finally
			{
				DaemonCoordinator.ReleaseOwnership();
			}
		}

		private static int Listen()
		{
			// Establish the single warm parse-context Script on this (STA) thread. The server never
			// registers hotkeys/hotstrings, so no input hooks or message pumps are ever activated.
			using var script = new Script();
			script.SuppressErrorOccurredDialog = true;

			var ch = new CompilerHelper();
			var exeDir = Path.GetFullPath(Path.GetDirectoryName(Environment.ProcessPath));

			Warmup(ch, exeDir);

			Log($"listening on pipe '{PipeName}' (idle timeout {IdleTimeout.TotalHours:0} h).");

			while (true)
			{
				NamedPipeServerStream server;

				try
				{
					server = new NamedPipeServerStream(
						PipeName,
						PipeDirection.InOut,
						maxNumberOfServerInstances: 1,
						PipeTransmissionMode.Byte,
						PipeOptions.Asynchronous);
				}
				catch (IOException)
				{
					// The pipe name is already taken: another daemon with the same fingerprint owns it.
					// Two daemons would be redundant, so defer to the existing one and exit.
					Log("a compatible daemon is already running; exiting.");
					return 0;
				}

				using (server)
				{
					if (!WaitForConnection(server, IdleTimeout))
					{
						Log("idle timeout reached, exiting.");
						return 0;
					}

					try
					{
						HandleRequest(server, ch, exeDir);
					}
					catch (Exception ex)
					{
						// A single bad request must not take down the daemon.
						Log($"request failed: {ex.Message}");
					}
				}
			}
		}

		// Compile a trivial script once so the first real client request is already warm (Roslyn/ANTLR
		// JITted, reference metadata loaded). Failures here are non-fatal; the first request just pays cold.
		private static void Warmup(CompilerHelper ch, string exeDir)
		{
			try
			{
				var sw = Stopwatch.StartNew();
				_ = ch.CompileCodeToByteArray("x := 1", "warmup", exeDir);
				Log($"warmup compile took {sw.ElapsedMilliseconds} ms.");
			}
			catch (Exception ex)
			{
				Log($"warmup failed (ignored): {ex.Message}");
			}
		}

		private static bool WaitForConnection(NamedPipeServerStream server, TimeSpan timeout)
		{
			var task = server.WaitForConnectionAsync();

			if (task.Wait(timeout))
				return true;

			// Unblock the pending async accept so the stream can be disposed cleanly.
			try { server.Dispose(); } catch { }

			return false;
		}

		// Wire format (length-prefixed, BinaryReader/Writer):
		//   request : int32 protocolVersion, string scriptPath
		//   response: bool success,
		//             if success -> int32 byteLen, byte[] assemblyBytes
		//             else        -> string errorMessage
		private static void HandleRequest(NamedPipeServerStream server, CompilerHelper ch, string exeDir)
		{
			using var reader = new BinaryReader(server, Encoding.UTF8, leaveOpen: true);
			using var writer = new BinaryWriter(server, Encoding.UTF8, leaveOpen: true);

			var clientProtocol = reader.ReadInt32();

			if (clientProtocol != ProtocolVersion)
			{
				writer.Write(false);
				writer.Write($"Protocol mismatch: server={ProtocolVersion}, client={clientProtocol}.");
				return;
			}

			var scriptPath = reader.ReadString();

			var sw = Stopwatch.StartNew();
			var nameNoExt = scriptPath == "*" ? "pipestdin" : Path.GetFileNameWithoutExtension(scriptPath);

			byte[] bytes;
			string error;

			try
			{
				(bytes, error) = ch.CompileCodeToByteArray(scriptPath, nameNoExt, exeDir);
			}
			catch (Exception ex)
			{
				bytes = null;
				error = $"Compiling script failed.\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
			}

			sw.Stop();

			if (bytes != null)
			{
				Log($"compiled '{scriptPath}' ({bytes.Length} bytes) in {sw.ElapsedMilliseconds} ms.");
				writer.Write(true);
				writer.Write(bytes.Length);
				writer.Write(bytes);
			}
			else
			{
				Log($"compile error for '{scriptPath}' in {sw.ElapsedMilliseconds} ms.");
				writer.Write(false);
				writer.Write(error ?? "Unknown compile error.");
			}

			writer.Flush();
			server.WaitForPipeDrain();
		}
	}

	/// <summary>
	/// Thin client for the compile server. Connects to (and, on request, spawns) a daemon with a matching
	/// build fingerprint, and returns compiled assembly bytes so the caller can run them via the lean path.
	/// </summary>
	internal static class CompileClient
	{
		// How long to wait for a freshly spawned daemon to finish warmup and start listening.
		private static readonly TimeSpan SpawnWaitTimeout = TimeSpan.FromSeconds(30);

		/// <summary>
		/// Compiles <paramref name="scriptPath"/> via a running daemon, spawning one if none is reachable.
		/// Falls back to <see cref="CompileDaemonStatus.Unreachable"/> only if no daemon can be started.
		/// </summary>
		internal static CompileDaemonStatus CompileViaServer(string scriptPath, out byte[] bytes, out string error)
		{
			var status = TryCompile(scriptPath, out bytes, out error, connectTimeoutMs: 300);

			if (status != CompileDaemonStatus.Unreachable)
				return status;

			if (!TrySpawnServer(out error))
				return CompileDaemonStatus.Unreachable;

			// Poll until the spawned daemon finishes warmup and begins listening, or we give up.
			var sw = Stopwatch.StartNew();

			while (sw.Elapsed < SpawnWaitTimeout)
			{
				status = TryCompile(scriptPath, out bytes, out error, connectTimeoutMs: 500);

				if (status != CompileDaemonStatus.Unreachable)
					return status;

				Thread.Sleep(200);
			}

			error ??= "Compile server did not become ready in time.";
			return CompileDaemonStatus.Unreachable;
		}

		/// <summary>
		/// Attempts a single compile against an already-running daemon. Returns
		/// <see cref="CompileDaemonStatus.Unreachable"/> (no exception) when no daemon is listening.
		/// </summary>
		internal static CompileDaemonStatus TryCompile(string scriptPath, out byte[] bytes, out string error, int connectTimeoutMs = 1000)
		{
			bytes = null;
			error = null;

			using var client = new NamedPipeClientStream(".", CompileServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

			try
			{
				client.Connect(connectTimeoutMs);
			}
			catch (TimeoutException)
			{
				return CompileDaemonStatus.Unreachable;
			}
			catch (IOException)
			{
				return CompileDaemonStatus.Unreachable;
			}

			using var writer = new BinaryWriter(client, Encoding.UTF8, leaveOpen: true);
			using var reader = new BinaryReader(client, Encoding.UTF8, leaveOpen: true);

			try
			{
				writer.Write(CompileServer.ProtocolVersion);
				writer.Write(scriptPath == "*" ? "*" : Path.GetFullPath(scriptPath));
				writer.Flush();

				if (reader.ReadBoolean())
				{
					var len = reader.ReadInt32();
					bytes = reader.ReadBytes(len);
					return CompileDaemonStatus.Compiled;
				}

				error = reader.ReadString();
				return CompileDaemonStatus.CompileFailed;
			}
			catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
			{
				// The daemon went away mid-request (e.g. it was replaced by a different build, or it idled
				// out between connect and reply). Treat as unreachable so the caller can spawn/fall back.
				bytes = null;
				error = null;
				return CompileDaemonStatus.Unreachable;
			}
		}

		// Launches "<this host> --daemon" detached so it outlives the current process. The spawned daemon runs
		// DaemonCoordinator.TryBecomeOwner at startup, which kills any different-build daemon and defers to an
		// identical one, so racing spawns converge on a single owner.
		private static bool TrySpawnServer(out string error)
		{
			error = null;

			try
			{
				var processPath = Environment.ProcessPath;

				if (string.IsNullOrEmpty(processPath))
				{
					error = "Cannot determine process path to spawn compile server.";
					return false;
				}

				var psi = new ProcessStartInfo
				{
					FileName = processPath,
					UseShellExecute = false,
					CreateNoWindow = true,
				};

				// When launched as "dotnet Keysharp.dll", re-pass the managed dll so the child runs Keysharp.
				var entryDll = Assembly.GetEntryAssembly()?.Location;

				if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
						&& !string.IsNullOrEmpty(entryDll)
						&& entryDll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
					psi.ArgumentList.Add(entryDll);

				psi.ArgumentList.Add("--daemon");
				return Process.Start(psi) != null;
			}
			catch (Exception ex)
			{
				error = $"Failed to spawn compile server: {ex.Message}";
				return false;
			}
		}
	}
}
