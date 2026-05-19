using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Keysharp.Internals.Platform;

#if LINUX
namespace Keysharp.Internals.Input.Linux
{
	internal static class KeysharpInputdManager
	{
		internal const string LegacyX11EnvironmentVariable = "KEYSHARP_X11_LEGACY";
		internal const string AutostartEnvironmentVariable = "KEYSHARP_INPUTD_AUTOSTART";
		internal const string DaemonExecutableEnvironmentVariable = "KEYSHARP_INPUTD_DAEMON";
		internal const string PermissionRepairEnvironmentVariable = "KEYSHARP_INPUTD_PERMISSION_REPAIR";

		// All capabilities requested at connect time; the daemon grants what it can.
		// Individual Ensure calls check only the subset they need.
		private static readonly KeysharpInputdClient.Capabilities AllCapabilities =
			KeysharpInputdClient.Capabilities.HookKeyboard
			| KeysharpInputdClient.Capabilities.HookMouse
			| KeysharpInputdClient.Capabilities.SynthKeyboard
			| KeysharpInputdClient.Capabilities.SynthMouse;

		private static readonly Lock gate = new();
		private static KeysharpInputdClient client;
		private static Process daemonProcess;
		private static bool attemptedPermissionRepair;

		internal static bool UseLegacyX11Input => IsTruthy(Environment.GetEnvironmentVariable(LegacyX11EnvironmentVariable));

		internal static KeysharpInputdClient Client
		{
			get
			{
				lock (gate)
					return client;
			}
		}

		/// <summary>Ensures the client is connected and has synthesis (Send) capability.</summary>
		internal static PermissionResult EnsureInputInjection(string operation = null)
			=> EnsureCapabilities(
				KeysharpInputdClient.Capabilities.SynthKeyboard | KeysharpInputdClient.Capabilities.SynthMouse,
				operation ?? "keyboard/mouse sending");

		/// <summary>Ensures the client is connected and has hook (monitoring) capability.</summary>
		internal static PermissionResult EnsureInputMonitoring(string operation = null)
			=> EnsureCapabilities(
				KeysharpInputdClient.Capabilities.HookKeyboard | KeysharpInputdClient.Capabilities.HookMouse,
				operation ?? "keyboard/mouse monitoring");

		internal static PermissionResult EnsureCapabilities(KeysharpInputdClient.Capabilities required, string operation = null)
		{
			if (UseLegacyX11Input)
				return new PermissionResult(PermissionStatus.NotApplicable);

			lock (gate)
			{
				if (!TryEnsureConnected(out var connectMessage))
					return new PermissionResult(PermissionStatus.Denied, connectMessage);

				if (HasCapabilities(client, required))
					return new PermissionResult(PermissionStatus.Granted);

				if (!attemptedPermissionRepair && TryRepairInputPermissions(out var repairMessage))
				{
					DisposeClient();
					StopStartedDaemon();

					if (TryEnsureConnected(out connectMessage) && HasCapabilities(client, required))
						return new PermissionResult(PermissionStatus.Granted);

					return new PermissionResult(PermissionStatus.Denied,
						$"keysharp-inputd permissions were repaired, but the daemon still did not grant '{operation}'. " +
						$"Restart any manually running keysharp-inputd process. Required: {required}, granted: {client?.GrantedCapabilities}. {connectMessage}");
				}

				return new PermissionResult(PermissionStatus.Denied,
					$"keysharp-inputd did not grant the required capabilities for '{operation}'. " +
					$"Required: {required}, granted: {client.GrantedCapabilities}.");
			}
		}

		private static bool TryRepairInputPermissions(out string message)
		{
			attemptedPermissionRepair = true;
			message = string.Empty;

			if (IsFalsey(Environment.GetEnvironmentVariable(PermissionRepairEnvironmentVariable)))
			{
				message = $"{PermissionRepairEnvironmentVariable} disabled inputd permission repair.";
				return false;
			}

			var executable = ResolveDaemonExecutable();

			if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
			{
				message = $"Cannot repair keysharp-inputd permissions because the daemon executable was not found.";
				return false;
			}

			var promptText =
				"Keysharp needs to repair keysharp-inputd device permissions.\n\n" +
				"This will request administrator access to install udev input rules, load uinput, " +
				$"and set group permissions on:\n{executable}\n\nContinue?";

			if (!AskUserForPermissionRepair(promptText))
			{
				message = "User declined keysharp-inputd permission repair.";
				return false;
			}

			if (!CommandExists("pkexec"))
			{
				message = "pkexec was not found. Install polkit or run keysharp-inputd --install-input-access and dev-permissions manually.";
				return false;
			}

			var script =
				"set -e\n" +
				$"{ShellQuote(executable)} --install-input-access\n" +
				$"chown root:input {ShellQuote(executable)}\n" +
				$"chmod g+s {ShellQuote(executable)}\n";

			try
			{
				var startInfo = new ProcessStartInfo
				{
					FileName = "pkexec",
					UseShellExecute = false,
				};

				startInfo.ArgumentList.Add("/bin/sh");
				startInfo.ArgumentList.Add("-c");
				startInfo.ArgumentList.Add(script);

				using var process = Process.Start(startInfo);

				if (process == null)
				{
					message = "pkexec failed to start.";
					return false;
				}

				process.WaitForExit();

				if (process.ExitCode != 0)
				{
					message = $"pkexec permission repair failed with exit code {process.ExitCode}.";
					return false;
				}

				message = string.Empty;
				return true;
			}
			catch (Exception ex)
			{
				message = ex.Message;
				return false;
			}
		}

		private static bool TryEnsureConnected(out string message)
		{
			// Already connected — reuse the existing connection regardless of which
			// capabilities it has; individual Ensure calls do their own capability checks.
			if (client != null)
			{
				message = string.Empty;
				return true;
			}

			// If a daemon is already listening, connect immediately.
			if (TryConnect(out message))
				return true;

			if (!ShouldAutostart())
				return false;

			// Guard against starting a second daemon if one is already launching
			// (e.g. if a previous connect attempt failed mid-startup).
			if (daemonProcess != null && !daemonProcess.HasExited)
			{
				// A daemon we started is still alive — just wait for it.
			}
			else if (!TryStartDaemon(out var startMessage))
			{
				message = $"{message} Autostart failed: {startMessage}";
				return false;
			}

			// Give the daemon a short head-start before the first poll —
			// it scans input devices and sets up udev which takes a moment.
			Thread.Sleep(300);

			var socketPath = KeysharpInputdClient.DefaultSocketPath;
			var deadline = DateTime.UtcNow.AddSeconds(10);

			while (DateTime.UtcNow < deadline)
			{
				if (TryConnect(out message))
					return true;

				Thread.Sleep(200);
			}

			message = $"keysharp-inputd did not become ready at '{socketPath}' within 10 seconds.";
			return false;
		}

		private static bool TryConnect(out string message)
		{
			try
			{
				client = KeysharpInputdClient.Connect(AllCapabilities);
				message = string.Empty;
				return true;
			}
			catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException)
			{
				DisposeClient();
				message = $"keysharp-inputd is unavailable at '{KeysharpInputdClient.DefaultSocketPath}': {ex.Message}";
				return false;
			}
		}

		private static bool TryStartDaemon(out string message)
		{
			var executable = ResolveDaemonExecutable();

			if (string.IsNullOrEmpty(executable))
			{
				message = $"Set {DaemonExecutableEnvironmentVariable} to the keysharp-inputd executable path, or build native/keysharp-inputd.";
				return false;
			}

			try
			{
				// Log to XDG_STATE_HOME/keysharp/inputd.log (same directory as the trust store).
				var logPath = ResolveLogPath();

				var startInfo = new ProcessStartInfo
				{
					FileName = executable,
					UseShellExecute = false,
					// Redirect streams so the daemon is decoupled from Keysharp's terminal
					// and survives the terminal being closed (no SIGHUP from session leader exit).
					RedirectStandardInput = true,
					RedirectStandardOutput = logPath != null,
					RedirectStandardError = logPath != null,
					CreateNoWindow = true,
				};

				// Default socket path: daemon will compute the same XDG_RUNTIME_DIR path itself.
				// No --socket or --foreground flags needed; foreground is already the daemon default.

				daemonProcess = Process.Start(startInfo);

				if (daemonProcess == null)
				{
					message = "Process.Start returned null.";
					return false;
				}

				// Close stdin so the daemon doesn't block waiting for input.
				daemonProcess.StandardInput.Close();

				if (logPath != null)
				{
					// Forward output to the log file asynchronously without blocking startup.
					_ = daemonProcess.StandardOutput.BaseStream.CopyToAsync(OpenAppend(logPath));
					_ = daemonProcess.StandardError.BaseStream.CopyToAsync(OpenAppend(logPath));
				}

				message = string.Empty;
				return true;
			}
			catch (Exception ex)
			{
				message = ex.Message;
				return false;
			}
		}

		private static string ResolveLogPath()
		{
			try
			{
				var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
				var home = Environment.GetEnvironmentVariable("HOME");
				string dir;

				if (!string.IsNullOrWhiteSpace(stateHome))
					dir = Path.Combine(stateHome, "keysharp");
				else if (!string.IsNullOrWhiteSpace(home))
					dir = Path.Combine(home, ".local", "state", "keysharp");
				else
					return null;

				Directory.CreateDirectory(dir);
				return Path.Combine(dir, "inputd.log");
			}
			catch
			{
				return null;
			}
		}

		private static Stream OpenAppend(string path)
		{
			try { return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read); }
			catch { return Stream.Null; }
		}

		private static string ResolveDaemonExecutable()
		{
			var configured = Environment.GetEnvironmentVariable(DaemonExecutableEnvironmentVariable);

			if (!string.IsNullOrWhiteSpace(configured))
				return File.Exists(configured) ? configured : null;

			// Fixed install location alongside the app binary.
			var nextToApp = Path.Combine(AppContext.BaseDirectory, "keysharp-inputd");

			if (File.Exists(nextToApp))
				return nextToApp;

			// Development: walk up from AppContext.BaseDirectory looking for the cmake build tree.
			const string devRelative = "native/keysharp-inputd/build/keysharp-inputd";

			var dir = new DirectoryInfo(AppContext.BaseDirectory);

			while (dir != null)
			{
				var candidate = Path.Combine(dir.FullName, devRelative);

				if (File.Exists(candidate))
					return candidate;

				dir = dir.Parent;
			}

			// Also try from the working directory (e.g. when launched from the repo root).
			var fromCwd = Path.Combine(Environment.CurrentDirectory, devRelative);

			if (File.Exists(fromCwd))
				return fromCwd;

			return null; // not found — caller will report a helpful error
		}

		private static bool HasCapabilities(KeysharpInputdClient connectedClient, KeysharpInputdClient.Capabilities required)
			=> (connectedClient.GrantedCapabilities & required) == required;

		private static bool ShouldAutostart()
		{
			var value = Environment.GetEnvironmentVariable(AutostartEnvironmentVariable);
			return string.IsNullOrEmpty(value) || IsTruthy(value);
		}

		private static bool IsTruthy(string value)
			=> !string.IsNullOrEmpty(value)
				&& (value.Equals("1", StringComparison.OrdinalIgnoreCase)
					|| value.Equals("true", StringComparison.OrdinalIgnoreCase)
					|| value.Equals("yes", StringComparison.OrdinalIgnoreCase)
					|| value.Equals("on", StringComparison.OrdinalIgnoreCase));

		private static bool IsFalsey(string value)
			=> !string.IsNullOrEmpty(value)
				&& (value.Equals("0", StringComparison.OrdinalIgnoreCase)
					|| value.Equals("false", StringComparison.OrdinalIgnoreCase)
					|| value.Equals("no", StringComparison.OrdinalIgnoreCase)
					|| value.Equals("off", StringComparison.OrdinalIgnoreCase));

		private static bool AskUserForPermissionRepair(string text)
		{
			if (!Script.IsHeadless && IsVisualPromptAvailable())
			{
				if (CommandExists("zenity"))
					return RunPromptCommand("zenity", ["--question", "--title=Keysharp input permissions", $"--text={text}", "--ok-label=Repair", "--cancel-label=Cancel"]);

				if (CommandExists("kdialog"))
					return RunPromptCommand("kdialog", ["--title", "Keysharp input permissions", "--yesno", text]);
			}

			try
			{
				using var tty = new FileStream("/dev/tty", FileMode.Open, FileAccess.ReadWrite);
				using var reader = new StreamReader(tty, leaveOpen: true);
				using var writer = new StreamWriter(tty) { AutoFlush = true };

				writer.WriteLine();
				writer.WriteLine(text);
				writer.Write("Repair now? [y/N]: ");
				var answer = reader.ReadLine();
				return !string.IsNullOrEmpty(answer) && answer.StartsWith("y", StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		private static bool IsVisualPromptAvailable()
			=> !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
				|| !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

		private static bool RunPromptCommand(string fileName, IReadOnlyList<string> arguments)
		{
			try
			{
				var startInfo = new ProcessStartInfo
				{
					FileName = fileName,
					UseShellExecute = false,
				};

				foreach (var argument in arguments)
					startInfo.ArgumentList.Add(argument);

				using var process = Process.Start(startInfo);

				if (process == null)
					return false;

				process.WaitForExit();
				return process.ExitCode == 0;
			}
			catch
			{
				return false;
			}
		}

		private static bool CommandExists(string command)
		{
			try
			{
				var startInfo = new ProcessStartInfo
				{
					FileName = "/bin/sh",
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
				};

				startInfo.ArgumentList.Add("-c");
				startInfo.ArgumentList.Add($"command -v {ShellQuote(command)} >/dev/null 2>&1");

				using var process = Process.Start(startInfo);

				if (process == null)
					return false;

				process.WaitForExit();
				return process.ExitCode == 0;
			}
			catch
			{
				return false;
			}
		}

		private static string ShellQuote(string value)
			=> "'" + (value ?? string.Empty).Replace("'", "'\\''", StringComparison.Ordinal) + "'";

		private static void DisposeClient()
		{
			try { client?.Dispose(); } catch { }
			client = null;
		}

		private static void StopStartedDaemon()
		{
			try
			{
				if (daemonProcess != null && !daemonProcess.HasExited)
				{
					daemonProcess.Kill();
					daemonProcess.WaitForExit(2000);
				}
			}
			catch
			{
			}
			finally
			{
				daemonProcess = null;
			}
		}
	}
}
#endif
