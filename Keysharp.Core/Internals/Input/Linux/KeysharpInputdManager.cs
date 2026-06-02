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

		// Two-socket model:
		//   client      – hook events + decisions (hook reader thread).
		//   queryClient – synthesis + state queries (any thread, queryGate for safety).
		//
		// Both synthesis (SYNTHESIS_RESULT returned after enqueue, not after uinput
		// delivery) and queries (GET_KEY_STATE, GET_INDICATOR_STATE, etc.) complete in
		// one short IPC round-trip, so queryGate is never held for long.
		private static KeysharpInputdClient client;
		private static KeysharpInputdClient queryClient;
		private const KeysharpInputdClient.Capabilities InputdGrantedCapabilities =
			KeysharpInputdClient.Capabilities.HookKeyboard
			| KeysharpInputdClient.Capabilities.HookMouse
			| KeysharpInputdClient.Capabilities.SynthKeyboard
			| KeysharpInputdClient.Capabilities.SynthMouse
			| KeysharpInputdClient.Capabilities.BlockInput;

		/// <summary>
		/// Sends input events to the daemon via the query/synthesis socket.
		/// Capabilities must already be granted (via EnsureInputInjection) before this is called.
		/// inputd returns SYNTHESIS_RESULT as soon as events are enqueued for the
		/// output sequencer, so this call is fast and queryGate is held briefly.
		/// </summary>
		internal static void SendInputViaSynthesisChannel(
			IReadOnlyList<KeysharpInputdClient.Input> inputs,
			KeysharpInputdClient.SynthFlags flags = KeysharpInputdClient.SynthFlags.None)
		{
			if (!TryUseQueryClient(qc =>
			{
				// Request synth caps lazily on first use. By the time Send is called,
				// EnsureInputInjection has already established trust on the main client,
				// so the daemon serves this from the trust store without a prompt.
				if (!HasCapabilities(qc, KeysharpInputdClient.Capabilities.SynthKeyboard | KeysharpInputdClient.Capabilities.SynthMouse))
					qc.TryRequestCapabilities(
						KeysharpInputdClient.Capabilities.SynthKeyboard | KeysharpInputdClient.Capabilities.SynthMouse,
						out _);

				qc.SendInput(inputs, flags);
				return true;
			}))
				throw new InvalidOperationException("keysharp-inputd query channel is unavailable for synthesis.");
		}

		internal static bool TryGetIndicatorState(out bool capsLock, out bool numLock, out bool scrollLock)
		{
			capsLock = false;
			numLock = false;
			scrollLock = false;
			var queryCapsLock = false;
			var queryNumLock = false;
			var queryScrollLock = false;

			try
			{
				var success = TryUseQueryClient(qc =>
				{
					if (!qc.TryRequestCapabilities(ExpandInputPermissionRequest(KeysharpInputdClient.Capabilities.HookKeyboard), out _))
						return false;

					(queryCapsLock, queryNumLock, queryScrollLock) = qc.GetIndicatorState();
					return true;
				});

				if (!success)
					return false;

				capsLock = queryCapsLock;
				numLock = queryNumLock;
				scrollLock = queryScrollLock;
				return true;
			}
			catch (Exception ex)
			{
				Ks.OutputDebugLine($"keysharp-inputd: indicator state query failed: {ex.Message}");
				return false;
			}
		}

		/// <summary>
		/// Queries inputd for the current physical modifier and toggle-key state.
		/// Returns false if the daemon is unavailable or the capability is not granted.
		/// </summary>
		internal static bool TryGetKeyState(out uint modifiersLR, out bool capsLock, out bool numLock, out bool scrollLock)
		{
			modifiersLR = 0;
			capsLock = false;
			numLock = false;
			scrollLock = false;
			var queryModifiersLR = 0u;
			var queryCapsLock = false;
			var queryNumLock = false;
			var queryScrollLock = false;

			try
			{
				var success = TryUseQueryClient(qc =>
				{
					if (!qc.TryRequestCapabilities(ExpandInputPermissionRequest(KeysharpInputdClient.Capabilities.HookKeyboard), out _))
						return false;

					(queryModifiersLR, queryCapsLock, queryNumLock, queryScrollLock) = qc.QueryKeyState();
					return true;
				});

				if (!success)
					return false;

				modifiersLR = queryModifiersLR;
				capsLock = queryCapsLock;
				numLock = queryNumLock;
				scrollLock = queryScrollLock;
				return true;
			}
			catch (Exception ex)
			{
				Ks.OutputDebugLine($"keysharp-inputd: key state query failed: {ex.Message}");
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
			var queryX = 0;
			var queryY = 0;
			var queryXMin = 0;
			var queryXMax = 0;
			var queryYMin = 0;
			var queryYMax = 0;

			if (!EnsureCapabilities(
					KeysharpInputdClient.Capabilities.HookMouse,
					"query pointer position").IsGranted)
				return false;

			try
			{
				var success = TryUseQueryClient(qc =>
				{
					if (!qc.TryRequestCapabilities(ExpandInputPermissionRequest(KeysharpInputdClient.Capabilities.HookMouse), out _)
						|| !qc.TryGetPointerPosition(out var position))
						return false;

					queryX = position.X;
					queryY = position.Y;
					queryXMin = position.XMin;
					queryXMax = position.XMax;
					queryYMin = position.YMin;
					queryYMax = position.YMax;
					return true;
				});

				if (!success)
					return false;

				x = queryX;
				y = queryY;
				xMin = queryXMin;
				xMax = queryXMax;
				yMin = queryYMin;
				yMax = queryYMax;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static KeysharpInputdClient GetOrCreateQueryClient()
		{
			// Keep lock order consistent with DisconnectClients()/DisposeClient():
			// gate first, then queryGate. Reversing this can deadlock shutdown while
			// a background query-client prewarm is checking the main connection.
			lock (gate)
			{
				// Only create the query connection if the main connection already exists
				// (i.e. the daemon is reachable). This avoids a redundant connect attempt
				// during startup before the daemon has been discovered.
				if (client == null)
					return null;

				lock (queryGate)
				{
					if (queryClient != null)
						return queryClient;

					try
					{
						// Connect with no capabilities; synthesis is requested lazily in
						// SendInputViaSynthesisChannel after EnsureInputInjection has
						// established trust on the main client.
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
		}

		private static bool TryUseQueryClient(Func<KeysharpInputdClient, bool> action)
		{
			var qc = GetOrCreateQueryClient();

			if (qc == null)
				return false;

			lock (queryGate)
			{
				// GetOrCreateQueryClient releases queryGate before callers use the
				// returned client. DisconnectClients() can dispose and clear it in that
				// gap, so only use the object if it is still the cached query client.
				if (!ReferenceEquals(qc, queryClient))
					return false;

				try
				{
					return action(qc);
				}
				catch (Exception ex) when (ex is IOException or SocketException
					or ObjectDisposedException or InvalidDataException or EndOfStreamException)
				{
					// Drop the dead query channel (inline: we already hold queryGate) so the
					// next call recreates it against a freshly socket-activated daemon.
					Ks.OutputDebugLine($"keysharp-inputd query channel lost: {ex.Message}");
					try { queryClient?.Dispose(); } catch { }
					queryClient = null;
					return false;
				}
			}
		}

		internal static readonly bool UseLegacyX11Input = ShouldUseLegacyX11Input();

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
					or ObjectDisposedException or InvalidDataException or EndOfStreamException)
				{
					// Drop the dead connection so a later call reconnects.
					DisposeClient();
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
				var requested = ExpandInputPermissionRequest(required);
				var requiredFromInputd = requested & InputdGrantedCapabilities;

				if (!TryEnsureConnected(operation, out var connectStatus, out var connectMessage))
					return new PermissionResult(connectStatus, connectMessage);

				try
				{
					if ((!forcePrompt && HasCapabilities(client, requiredFromInputd))
						|| client.TryRequestCapabilities(requested, requiredFromInputd, out _, forcePrompt))
						return new PermissionResult(PermissionStatus.Granted);

					return new PermissionResult(PermissionStatus.Denied,
						$"keysharp-inputd did not grant the required capabilities for '{operation}'. " +
						$"Required from inputd: {requiredFromInputd}, requested: {requested}, granted: {client.GrantedCapabilities}.");
				}
				catch (Exception ex) when (ex is IOException or SocketException
					or ObjectDisposedException or InvalidDataException or EndOfStreamException)
				{
					// The cached connection died (daemon crash/restart). Drop it so the
					// next call reconnects — the systemd socket respawns the daemon.
					DisposeClient();
					return new PermissionResult(PermissionStatus.Unsupported,
						$"keysharp-inputd connection lost while preparing '{operation}': {ex.Message}");
				}
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

			// Hook-backed features can synthesize events later from the hook path
			// (for example, remaps), so a hook grant includes synthesis to avoid
			// a second prompt on first activation. Synthesis alone still does not
			// imply hook access.
			if ((requested & hook) != 0)
				requested |= hook | synth;
			else if ((requested & synth) != 0)
				requested |= synth;

			return requested;
		}

		private static bool IsTruthy(string value)
			=> !string.IsNullOrEmpty(value)
				&& (value.Equals("1", StringComparison.OrdinalIgnoreCase)
					|| value.Equals("true", StringComparison.OrdinalIgnoreCase)
					|| value.Equals("yes", StringComparison.OrdinalIgnoreCase)
					|| value.Equals("on", StringComparison.OrdinalIgnoreCase));

		private static bool ShouldUseLegacyX11Input()
		{
			if (IsTruthy(Environment.GetEnvironmentVariable(LegacyX11EnvironmentVariable)))
				return true;

			// Test hosts must not trigger inputd permission prompts. If an X display is
			// available, use the in-process X11/SharpHook path so hook tests can run
			// unattended.
			return AppDomain.CurrentDomain.FriendlyName == "testhost"
				&& !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
		}

		private static void DisposeClient()
		{
			try { client?.Dispose(); } catch { }
			client = null;
			DisposeQueryClient();
		}

		internal static void DisconnectClients()
		{
			lock (gate)
				DisposeClient();
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
