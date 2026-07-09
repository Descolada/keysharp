using System.Net.Sockets;
using System.Threading;
using Keysharp.Builtins;

#if LINUX
namespace Keysharp.Internals.Input.Linux
{
	internal static class KeysharpInputdManager
	{
		private static readonly Lock gate = new();
		private static readonly Lock queryGate = new();

		// client handles hook/control traffic; queryClient handles synthesis/state queries.
		// Volatile lets IsDaemonReachable answer without blocking behind a prompt.
		private static volatile KeysharpInputdClient client;
		private static KeysharpInputdClient queryClient;
		private const KeysharpInputdClient.Capabilities InputdGrantedCapabilities =
			KeysharpInputdClient.Capabilities.HookKeyboard
			| KeysharpInputdClient.Capabilities.HookMouse
			| KeysharpInputdClient.Capabilities.SynthKeyboard
			| KeysharpInputdClient.Capabilities.SynthMouse
			| KeysharpInputdClient.Capabilities.BlockInput;

		// Bounded daemon queue overflow is transient backpressure; retry briefly.
		private const int SynthesisBackpressureRetryMs = 4;
		private const long SynthesisBackpressureMaxWaitMs = 4000;

		internal static void SendInputViaSynthesisChannel(
			IReadOnlyList<KeysharpInputdClient.Input> inputs,
			KeysharpInputdClient.SynthFlags flags = KeysharpInputdClient.SynthFlags.None)
		{
			var backpressureDeadline = Environment.TickCount64 + SynthesisBackpressureMaxWaitMs;

			for (;;)
			{
				try
				{
					if (!TryUseQueryClient(qc =>
					{
						// Query connection inherits trust already established on the main client.
						if (!HasCapabilities(qc, KeysharpInputdClient.Capabilities.SynthKeyboard | KeysharpInputdClient.Capabilities.SynthMouse))
							qc.TryRequestCapabilities(
								KeysharpInputdClient.Capabilities.SynthKeyboard | KeysharpInputdClient.Capabilities.SynthMouse,
								out _);

						qc.SendInput(inputs, flags);
						return true;
					}))
						throw new InvalidOperationException("keysharp-inputd query channel is unavailable for synthesis.");

					return;
				}
				catch (KeysharpInputdClient.RequestFailedException ex)
					when (KeysharpInputdClient.IsSynthesisBackpressure(ex) && Environment.TickCount64 < backpressureDeadline)
				{
					Thread.Sleep(SynthesisBackpressureRetryMs);
				}
			}
		}

		/// <summary>Best-effort panic release; never throws.</summary>
		internal static void EmergencyReleaseInput()
		{
			try
			{
				_ = TryUseQueryClient(qc =>
				{
					qc.EmergencyPassthrough();
					return true;
				});
			}
			catch
			{
			}
		}

		internal static bool TryGetIndicatorState(out bool capsLock, out bool numLock, out bool scrollLock)
		{
			capsLock = false;
			numLock = false;
			scrollLock = false;

			if (!TryQuery(
					KeysharpInputdClient.Capabilities.HookKeyboard,
					qc => (true, qc.GetIndicatorState()),
					out (bool CapsLock, bool NumLock, bool ScrollLock) state,
					"indicator state query"))
				return false;

			capsLock = state.CapsLock;
			numLock = state.NumLock;
			scrollLock = state.ScrollLock;
			return true;
		}

		/// <summary>Queries inputd for current logical modifier and toggle-key state.</summary>
		internal static bool TryGetKeyState(out uint modifiersLR, out bool capsLock, out bool numLock, out bool scrollLock)
			=> TryGetKeyState(out modifiersLR, out capsLock, out numLock, out scrollLock, out _, out _);

		/// <summary>Queries logical keyboard state plus optional logical-key bitmap.</summary>
		internal static bool TryGetKeyState(out uint modifiersLR, out bool capsLock, out bool numLock, out bool scrollLock, out byte[] logicalKeys)
			=> TryGetKeyState(out modifiersLR, out capsLock, out numLock, out scrollLock, out logicalKeys, out _);

		/// <summary>Queries logical and physical keyboard state.</summary>
		internal static bool TryGetKeyState(out uint modifiersLR, out bool capsLock, out bool numLock, out bool scrollLock, out byte[] logicalKeys, out byte[] physicalKeys)
		{
			modifiersLR = 0;
			capsLock = false;
			numLock = false;
			scrollLock = false;
			logicalKeys = [];
			physicalKeys = [];

			if (!TryQuery(
					KeysharpInputdClient.Capabilities.HookKeyboard,
					qc => (true, qc.QueryKeyState()),
					out KeysharpInputdClient.KeyStateSnapshot state,
					"key state query"))
				return false;

			modifiersLR = state.ModifiersLR;
			capsLock = state.CapsLock;
			numLock = state.NumLock;
			scrollLock = state.ScrollLock;
			logicalKeys = state.LogicalKeys ?? [];
			physicalKeys = state.PhysicalKeys ?? [];
			return true;
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

			if (!TryQuery(
					KeysharpInputdClient.Capabilities.HookMouse,
					qc => qc.TryGetPointerPosition(out var position) ? (true, position) : (false, default),
					out KeysharpInputdClient.PointerPosition position))
				return false;

			x = position.X;
			y = position.Y;
			xMin = position.XMin;
			xMax = position.XMax;
			yMin = position.YMin;
			yMax = position.YMax;
			return true;
		}

		/// <summary>Live logical state of one mouse button (Wayland path for GetKeyState(button)).</summary>
		internal static bool TryQueryButtonStateLogical(uint vk, out bool down)
			=> TryQueryButtonState(vk, physical: false, out down);

		/// <summary>Live physical state of one mouse button.</summary>
		internal static bool TryQueryButtonStatePhysical(uint vk, out bool down)
			=> TryQueryButtonState(vk, physical: true, out down);

		private static bool TryQueryButtonState(uint vk, bool physical, out bool down)
		{
			down = false;

			var bit = vk switch
			{
				0x01u => 1u << 0, // VK_LBUTTON
				0x02u => 1u << 1, // VK_RBUTTON
				0x04u => 1u << 2, // VK_MBUTTON
				0x05u => 1u << 3, // VK_XBUTTON1
				0x06u => 1u << 4, // VK_XBUTTON2
				_ => 0u
			};

			if (bit == 0)
				return false;

			if (!EnsureCapabilities(
					KeysharpInputdClient.Capabilities.HookMouse,
					"query mouse button state").IsGranted)
				return false;

			if (!TryQuery(
					KeysharpInputdClient.Capabilities.HookMouse,
					qc => qc.TryGetPointerButtons(out var buttons) ? (true, buttons) : (false, default),
					out KeysharpInputdClient.PointerButtons buttons))
				return false;

			var buttonsMask = physical ? buttons.PhysicalButtons : buttons.LogicalButtons;
			down = (buttonsMask & bit) != 0;
			return true;
		}

		private static bool TryQuery<T>(
			KeysharpInputdClient.Capabilities required,
			Func<KeysharpInputdClient, (bool Success, T Value)> query,
			out T value,
			string failureContext = null)
		{
			value = default;
			var captured = default(T);

			try
			{
				var success = TryUseQueryClient(qc =>
				{
					if (!EnsureQueryCapabilityNoPrompt(qc, required))
						return false;

					var result = query(qc);

					if (!result.Success)
						return false;

					captured = result.Value;
					return true;
				});

				if (!success)
					return false;

				value = captured;
				return true;
			}
			catch (Exception ex)
			{
				if (failureContext != null)
					Ks.OutputDebugLine($"keysharp-inputd: {failureContext} failed: {ex.Message}");

				return false;
			}
		}

		private static KeysharpInputdClient GetOrCreateQueryClient()
		{
			// Lock order: gate, then queryGate.
			lock (gate)
			{
				if (client == null)
					return null;

				lock (queryGate)
				{
					if (queryClient != null)
						return queryClient;

					try
					{
						queryClient = KeysharpInputdClient.Connect();
					}
					catch (Exception ex) when (IsConnectException(ex))
					{
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
				if (!ReferenceEquals(qc, queryClient))
					return false;

				try
				{
					return action(qc);
				}
				catch (KeysharpInputdClient.RequestFailedException)
				{
					throw;
				}
				catch (Exception ex) when (IsTransportException(ex))
				{
					Ks.OutputDebugLine($"keysharp-inputd query channel lost: {ex.Message}");
					try { queryClient?.Dispose(); } catch { }
					queryClient = null;
					return false;
				}
			}
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
				catch (Exception ex) when (IsTransportException(ex))
				{
					DisposeClient();
					message = ex.Message;
					return false;
				}
			}
		}

		internal static PermissionResult EnsureCapabilities(KeysharpInputdClient.Capabilities required, string operation = null, bool forcePrompt = false)
		{
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
				catch (Exception ex) when (IsTransportException(ex))
				{
					DisposeClient();
					return new PermissionResult(PermissionStatus.Unsupported,
						$"keysharp-inputd connection lost while preparing '{operation}': {ex.Message}");
				}
			}
		}

		private static bool TryEnsureConnected(string operation, out PermissionStatus status, out string message)
		{
			if (client != null)
			{
				if (client.IsConnected)
				{
					status = PermissionStatus.Granted;
					message = string.Empty;
					return true;
				}

				// Cached client is stale -- the daemon already closed this
				// connection (e.g. it crashed or restarted) since we last used
				// it. Previously this branch was trusted purely because it was
				// non-null, so the FIRST real request after a silent daemon
				// death threw an avoidable transport exception instead of
				// transparently reconnecting here, same as every other
				// caller-visible transport failure already does.
				DisposeClient();
			}

			return TryConnect(operation, out status, out message);
		}

		/// <summary>Probes daemon reachability without requesting capabilities.</summary>
		internal static bool IsDaemonReachable()
		{
			if (!gate.TryEnter(TimeSpan.FromMilliseconds(50)))
				return client != null;

			try
			{
				return TryEnsureConnected("probe keysharp-inputd", out _, out _);
			}
			finally
			{
				gate.Exit();
			}
		}

		private static bool TryConnect(string operation, out PermissionStatus status, out string message)
		{
			try
			{
				client = KeysharpInputdClient.Connect();
				status = PermissionStatus.Granted;
				message = string.Empty;

				ThreadPool.QueueUserWorkItem(
					_ => { try { GetOrCreateQueryClient(); } catch { } });

				return true;
			}
			catch (Exception ex) when (IsConnectException(ex))
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

		private static bool IsConnectException(Exception ex)
			=> ex is IOException or SocketException or ObjectDisposedException or InvalidDataException;

		private static bool IsTransportException(Exception ex)
			=> IsConnectException(ex) || ex is EndOfStreamException;

		private static bool MainClientHasCapability(KeysharpInputdClient.Capabilities cap)
		{
			var c = client;
			return c != null && (c.GrantedCapabilities & cap) == cap;
		}

		// Query reads must never prompt; inherit only from an existing main grant.
		private static bool EnsureQueryCapabilityNoPrompt(KeysharpInputdClient qc, KeysharpInputdClient.Capabilities required)
		{
			if (HasCapabilities(qc, required))
				return true;

			return MainClientHasCapability(required)
				&& qc.TryRequestCapabilities(ExpandInputPermissionRequest(required), out _);
		}

		internal static KeysharpInputdClient.Capabilities ExpandInputPermissionRequest(KeysharpInputdClient.Capabilities requested)
		{
			const KeysharpInputdClient.Capabilities hook =
				KeysharpInputdClient.Capabilities.HookKeyboard | KeysharpInputdClient.Capabilities.HookMouse;
			const KeysharpInputdClient.Capabilities synth =
				KeysharpInputdClient.Capabilities.SynthKeyboard | KeysharpInputdClient.Capabilities.SynthMouse;

			// Hooks can synthesize later (remaps), so hook grants include synthesis.
			if ((requested & hook) != 0)
				requested |= hook | synth;
			else if ((requested & synth) != 0)
				requested |= synth;

			return requested;
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
