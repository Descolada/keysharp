using System.Net.Sockets;
using System.Threading;
using Keysharp.Builtins;

#if LINUX
namespace Keysharp.Internals.Input.Linux
{
	internal static class KeysharpInputdManager
	{
		internal const string LegacyX11EnvironmentVariable = "KEYSHARP_X11_LEGACY";
		private static readonly Lock gate = new();
		private static readonly Lock queryGate = new();
		private static volatile bool legacyX11FallbackActive;

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
			var required = ExpandInputPermissionRequest(GetRequiredSynthesisCapabilities(inputs));

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
					if (!qc.TryRequestCapabilities(ExpandInputPermissionRequest(KeysharpInputdClient.Capabilities.HookKeyboard), out _))
						return false;

					(capsLock, numLock, scrollLock) = qc.GetIndicatorState();
				}

				return true;
			}
			catch (Exception ex)
			{
				Ks.OutputDebugLine($"keysharp-inputd: indicator state query failed: {ex.Message}");
				return false;
			}
		}

		internal static bool TryGetPointerPosition(
			out int x,
			out int y,
			out int xMin,
			out int xMax,
			out int yMin,
			out int yMax)
		{
			x = y = xMin = xMax = yMin = yMax = 0;

			if (!EnsureCapabilities(
					KeysharpInputdClient.Capabilities.HookMouse,
					"query pointer position").IsGranted)
				return false;

			var qc = GetOrCreateQueryClient();

			if (qc == null)
				return false;

			try
			{
				lock (queryGate)
				{
					if (!qc.TryRequestCapabilities(ExpandInputPermissionRequest(KeysharpInputdClient.Capabilities.HookMouse), out _)
						|| !qc.TryGetPointerPosition(out var position))
						return false;

					x = position.X;
					y = position.Y;
					xMin = position.XMin;
					xMax = position.XMax;
					yMin = position.YMin;
					yMax = position.YMax;
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

		internal static bool IsLegacyX11FallbackActive => UseLegacyX11Input || legacyX11FallbackActive;

		internal static void ActivateLegacyX11Fallback(string reason = null)
		{
			if (UseLegacyX11Input)
				return;

			if (!legacyX11FallbackActive && !string.IsNullOrEmpty(reason))
				Ks.OutputDebugLine($"keysharp-inputd unavailable; using X11/SharpHook fallback. {reason}");

			legacyX11FallbackActive = true;
		}

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

		internal static bool TrySetBlockInput(KeysharpInputdClient.BlockInputMask mask, out string message)
		{
			if (IsLegacyX11FallbackActive)
			{
				message = string.Empty;
				return false;
			}

			lock (gate)
			{
				if (!TryEnsureConnected("block input", out _, out message))
					return false;

				if (!HasCapabilities(client, KeysharpInputdClient.Capabilities.BlockInput)
					&& !client.TryRequestCapabilities(KeysharpInputdClient.Capabilities.BlockInput, out _))
				{
					message = $"keysharp-inputd did not grant block-input capability. Granted: {client.GrantedCapabilities}.";
					return false;
				}

				try
				{
					var granted = client.SetBlockInput(mask);

					if (granted != mask)
						Ks.OutputDebugLine($"BlockInput: daemon granted {granted}, requested {mask}.");

					message = string.Empty;
					return true;
				}
				catch (Exception ex) when (ex is IOException or SocketException
					or ObjectDisposedException or InvalidDataException)
				{
					message = ex.Message;
					return false;
				}
			}
		}

		internal static PermissionResult EnsureCapabilities(KeysharpInputdClient.Capabilities required, string operation = null, bool forcePrompt = false)
		{
			if (IsLegacyX11FallbackActive)
				return new PermissionResult(PermissionStatus.NotApplicable);

			operation ??= "input automation";

			lock (gate)
			{
				required = ExpandInputPermissionRequest(required);

				if (!TryEnsureConnected(operation, out var connectStatus, out var connectMessage))
					return new PermissionResult(connectStatus, connectMessage);

				if ((!forcePrompt && HasCapabilities(client, required)) || client.TryRequestCapabilities(required, out _, forcePrompt))
					return new PermissionResult(PermissionStatus.Granted);

				return new PermissionResult(PermissionStatus.Denied,
					$"keysharp-inputd did not grant the required capabilities for '{operation}'. " +
					$"Required: {required}, granted: {client.GrantedCapabilities}.");
			}
		}

		private static bool TryEnsureConnected(string operation, out PermissionStatus status, out string message)
		{
			// Already connected — reuse the existing connection regardless of which
			// capabilities it has; individual Ensure calls do their own capability checks.
			if (client != null)
			{
				status = PermissionStatus.Granted;
				message = string.Empty;
				return true;
			}

			// The installed systemd socket starts the privileged service on connect.
			return TryConnect(operation, out status, out message);
		}

		/// <summary>
		/// Probes whether keysharp-inputd is reachable without requesting any
		/// capabilities (so no permission prompt is shown). Used to decide which
		/// keyboard/mouse sender to construct before any hook/send is actually
		/// requested — defer the real capability prompt to first use.
		/// </summary>
		internal static bool IsDaemonReachable()
		{
			if (IsLegacyX11FallbackActive)
				return false;

			lock (gate)
				return TryEnsureConnected("probe keysharp-inputd", out _, out _);
		}

		private static bool TryConnect(string operation, out PermissionStatus status, out string message)
		{
			try
			{
				client = KeysharpInputdClient.Connect();
				status = PermissionStatus.Granted;
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
				status = PermissionStatus.Unsupported;
				message = $"keysharp-inputd is not installed or not available at '{KeysharpInputdClient.DefaultSocketPath}'. " +
					$"Install Keysharp's optional Linux input helper to use '{operation ?? "input automation"}'. Details: {ex.Message}";
				return false;
			}
		}

		private static bool HasCapabilities(KeysharpInputdClient connectedClient, KeysharpInputdClient.Capabilities required)
			=> (connectedClient.GrantedCapabilities & required) == required;

		internal static KeysharpInputdClient.Capabilities ExpandInputPermissionRequest(KeysharpInputdClient.Capabilities requested)
		{
			const KeysharpInputdClient.Capabilities hook =
				KeysharpInputdClient.Capabilities.HookKeyboard | KeysharpInputdClient.Capabilities.HookMouse;
			const KeysharpInputdClient.Capabilities synth =
				KeysharpInputdClient.Capabilities.SynthKeyboard | KeysharpInputdClient.Capabilities.SynthMouse;

			// Hook access is the more invasive grant (it can observe and replay
			// every keystroke), so when the user already authorizes hooks we
			// fold in synthesis too — asking again for Send would be redundant.
			// Synthesis on its own does not imply hooks: a script that only
			// sends keys should not be granted observation rights.
			if ((requested & hook) != 0)
				requested |= hook | synth;
			else if ((requested & synth) != 0)
				requested |= synth;

			return requested;
		}

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
