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
		private static readonly Lock gate = new();
		private static readonly Lock queryGate = new();

		/// <summary>
		/// Sends input events to the daemon via the dedicated synthesis socket. The daemon
		/// acknowledges that the request was validated and queued for the input thread; it
		/// does not wait for the queued events to be emitted to uinput.
		///
		/// Safe to call from any thread, including the hook-event reader thread.
		/// </summary>
		internal static void SendInputViaSynthesisChannel(
			IReadOnlyList<KeysharpInputdClient.Input> inputs,
			KeysharpInputdClient.SynthFlags flags = KeysharpInputdClient.SynthFlags.None)
		{
			var qc = GetOrCreateQueryClient();
			var required = GetRequiredSynthesisCapabilities(inputs);

			if (qc != null)
			{
				lock (queryGate)
				{
					if (!qc.TryRequestCapabilities(required, out _))
						throw new IOException($"keysharp-inputd did not grant synthesis capabilities. Required: {required}, granted: {qc.GrantedCapabilities}.");

					qc.SendInput(inputs, flags);
				}
				return;
			}

			// Fallback: query client not yet available (startup). Use the hook client.
			// SendInput on the hook socket is safe here because the hook reader is
			// not yet running (we're still in EnsureCapabilities at startup).
			lock (gate)
			{
				if (client == null || !client.TryRequestCapabilities(required, out _))
					throw new IOException($"keysharp-inputd did not grant synthesis capabilities. Required: {required}, granted: {client?.GrantedCapabilities}.");

				client.SendInput(inputs, flags);
			}
		}
		private static KeysharpInputdClient client;
		private static KeysharpInputdClient queryClient;

		internal static bool TryGetIndicatorState(out bool capsLock, out bool numLock, out bool scrollLock)
		{
			capsLock = false;
			numLock = false;
			scrollLock = false;

			var qc = GetOrCreateQueryClient();

			if (qc == null)
				return false;

			try
			{
				lock (queryGate)
				{
					if (!qc.TryRequestCapabilities(KeysharpInputdClient.Capabilities.HookKeyboard, out _))
						return false;

					(capsLock, numLock, scrollLock) = qc.GetIndicatorState();
				}

				return true;
			}
			catch
			{
				return false;
			}
		}

		private static KeysharpInputdClient GetOrCreateQueryClient()
		{
			lock (queryGate)
			{
				if (queryClient != null)
					return queryClient;

				// Only create the query connection if the main connection already exists
				// (i.e. the daemon is reachable). This avoids a redundant connect attempt
				// during startup before the daemon has been discovered.
				lock (gate)
				{
					if (client == null)
						return null;
				}

				try
				{
					queryClient = KeysharpInputdClient.Connect();
				}
				catch (Exception ex) when (ex is IOException or SocketException
					or ObjectDisposedException or InvalidDataException)
				{
					// Non-fatal: callers can fall back to the main request/response connection.
				}

				return queryClient;
			}
		}

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

				if (HasCapabilities(client, required) || client.TryRequestCapabilities(required, out _))
					return new PermissionResult(PermissionStatus.Granted);

				return new PermissionResult(PermissionStatus.Denied,
					$"keysharp-inputd did not grant the required capabilities for '{operation}'. " +
					$"Required: {required}, granted: {client.GrantedCapabilities}.");
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

			// The installed systemd socket starts the privileged service on connect.
			return TryConnect(out message);
		}

		private static bool TryConnect(out string message)
		{
			try
			{
				client = KeysharpInputdClient.Connect();
				message = string.Empty;

				// Pre-warm the QueryClient (synthesis channel) in the background so the
				// first SendInput call does not pay connection setup cost mid-action.
				ThreadPool.QueueUserWorkItem(
					_ => { try { GetOrCreateQueryClient(); } catch { } });

				return true;
			}
			catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException)
			{
				DisposeClient();
				message = $"keysharp-inputd is unavailable at '{KeysharpInputdClient.DefaultSocketPath}': {ex.Message}";
				return false;
			}
		}

		private static bool HasCapabilities(KeysharpInputdClient connectedClient, KeysharpInputdClient.Capabilities required)
			=> (connectedClient.GrantedCapabilities & required) == required;

		private static KeysharpInputdClient.Capabilities GetRequiredSynthesisCapabilities(IReadOnlyList<KeysharpInputdClient.Input> inputs)
		{
			var required = KeysharpInputdClient.Capabilities.None;

			for (var i = 0; i < inputs.Count; i++)
			{
				if (inputs[i].Type == KeysharpInputdClient.InputType.Keyboard)
					required |= KeysharpInputdClient.Capabilities.SynthKeyboard;
				else if (inputs[i].Type == KeysharpInputdClient.InputType.Mouse)
					required |= KeysharpInputdClient.Capabilities.SynthMouse;
			}

			return required;
		}

		private static bool IsTruthy(string value)
			=> !string.IsNullOrEmpty(value)
				&& (value.Equals("1", StringComparison.OrdinalIgnoreCase)
					|| value.Equals("true", StringComparison.OrdinalIgnoreCase)
					|| value.Equals("yes", StringComparison.OrdinalIgnoreCase)
					|| value.Equals("on", StringComparison.OrdinalIgnoreCase));

		private static void DisposeClient()
		{
			try { client?.Dispose(); } catch { }
			client = null;
			DisposeQueryClient();
		}

		private static void DisposeQueryClient()
		{
			lock (queryGate)
			{
				try { queryClient?.Dispose(); } catch { }
				queryClient = null;
			}
		}

	}
}
#endif
