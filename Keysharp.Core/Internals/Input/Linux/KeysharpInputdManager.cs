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

		// Two-socket model:
		//   client      – hook events + decisions (hook reader thread).
		//   queryClient – synthesis + state queries (any thread, queryGate for safety).
		//
		// Both synthesis (SYNTHESIS_RESULT returned after enqueue, not after uinput
		// delivery) and queries (GET_KEY_STATE, GET_INDICATOR_STATE, etc.) complete in
		// one short IPC round-trip, so queryGate is never held for long.
		// volatile so the best-effort lock-free read in IsDaemonReachable (when a capability prompt is
		// holding 'gate') observes a consistent connect/disconnect state.
		private static volatile KeysharpInputdClient client;
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
		// The daemon rejects (rather than blocks) a SYNTHESIZE_INPUT batch that would
		// overflow its bounded output queue while the sequencer is still draining prior
		// events. That reject is transient backpressure, so a long Send resends the same
		// batch after a short pause instead of failing — which also queues physical input
		// behind the whole Send, matching Windows SendInput. Bounded so a genuinely wedged
		// sequencer still surfaces an error rather than hanging the script thread forever.
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

					return;
				}
				catch (KeysharpInputdClient.RequestFailedException ex)
					when (KeysharpInputdClient.IsSynthesisBackpressure(ex) && Environment.TickCount64 < backpressureDeadline)
				{
					// Output queue full; let the sequencer drain (queryGate is released here),
					// then resend the same batch.
					Thread.Sleep(SynthesisBackpressureRetryMs);
				}
			}
		}

		/// <summary>
		/// Best-effort panic release: asks keysharp-inputd to drop ALL grabs and block-input
		/// (emergency passthrough). Sent on the query channel so it works even while the hook lane and
		/// the main script thread are busy/hung. Never throws — if even the query channel is gone there
		/// is nothing more to do here (the daemon also auto-releases when this client disconnects).
		/// </summary>
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
			var queryCapsLock = false;
			var queryNumLock = false;
			var queryScrollLock = false;

			try
			{
				var success = TryUseQueryClient(qc =>
				{
					if (!EnsureQueryCapabilityNoPrompt(qc, KeysharpInputdClient.Capabilities.HookKeyboard))
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
		/// Queries inputd for the current logical modifier and toggle-key state.
		/// Returns false if the daemon is unavailable or the capability is not granted.
		/// </summary>
		internal static bool TryGetKeyState(out uint modifiersLR, out bool capsLock, out bool numLock, out bool scrollLock)
			=> TryGetKeyState(out modifiersLR, out capsLock, out numLock, out scrollLock, out _);

		/// <summary>
		/// Queries inputd for the current logical keyboard state. logicalKeys is an evdev KEY_* bitmap
		/// when the daemon supports it; older daemons return an empty array.
		/// </summary>
		internal static bool TryGetKeyState(out uint modifiersLR, out bool capsLock, out bool numLock, out bool scrollLock, out byte[] logicalKeys)
			=> TryGetKeyState(out modifiersLR, out capsLock, out numLock, out scrollLock, out logicalKeys, out _);

		/// <summary>
		/// Queries inputd for the current logical and physical keyboard state. physicalKeys is available when
		/// the daemon supports the extended payload; older daemons return an empty array.
		/// </summary>
		internal static bool TryGetKeyState(out uint modifiersLR, out bool capsLock, out bool numLock, out bool scrollLock, out byte[] logicalKeys, out byte[] physicalKeys)
		{
			modifiersLR = 0;
			capsLock = false;
			numLock = false;
			scrollLock = false;
			logicalKeys = [];
			physicalKeys = [];
			var queryModifiersLR = 0u;
			var queryCapsLock = false;
			var queryNumLock = false;
			var queryScrollLock = false;
			byte[] queryLogicalKeys = [];
			byte[] queryPhysicalKeys = [];

			try
			{
				var success = TryUseQueryClient(qc =>
				{
					if (!EnsureQueryCapabilityNoPrompt(qc, KeysharpInputdClient.Capabilities.HookKeyboard))
						return false;

					var state = qc.QueryKeyState();
					queryModifiersLR = state.ModifiersLR;
					queryCapsLock = state.CapsLock;
					queryNumLock = state.NumLock;
					queryScrollLock = state.ScrollLock;
					queryLogicalKeys = state.LogicalKeys ?? [];
					queryPhysicalKeys = state.PhysicalKeys ?? [];
					return true;
				});

				if (!success)
					return false;

				modifiersLR = queryModifiersLR;
				capsLock = queryCapsLock;
				numLock = queryNumLock;
				scrollLock = queryScrollLock;
				logicalKeys = queryLogicalKeys;
				physicalKeys = queryPhysicalKeys;
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
					if (!EnsureQueryCapabilityNoPrompt(qc, KeysharpInputdClient.Capabilities.HookMouse)
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

		/// <summary>Live logical state of one mouse button (Wayland path for GetKeyState(button)).</summary>
		internal static bool TryQueryButtonStateLogical(uint vk, out bool down)
			=> TryQueryButtonState(vk, physical: false, out down);

		/// <summary>
		/// Live physical state of one mouse button (Wayland path for GetKeyState(.., "P")). The daemon snapshots
		/// evdev button state via EVIOCGKEY, so this works with no mouse grab/hook. Returns false — leaving the
		/// caller to fall back to hook-tracked state — if the daemon is unavailable, lacks the query (older
		/// daemon), or has no readable pointer device.
		/// </summary>
		internal static bool TryQueryButtonStatePhysical(uint vk, out bool down)
			=> TryQueryButtonState(vk, physical: true, out down);

		private static bool TryQueryButtonState(uint vk, bool physical, out bool down)
		{
			down = false;

			// Bit layout mirrors KeysharpInputdClient.TryGetPointerButtons: 0=left,1=right,2=middle,3=X1,4=X2.
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

			KeysharpInputdClient.PointerButtons buttons = default;

			try
			{
				var success = TryUseQueryClient(qc =>
				{
					if (!EnsureQueryCapabilityNoPrompt(qc, KeysharpInputdClient.Capabilities.HookMouse)
						|| !qc.TryGetPointerButtons(out buttons))
						return false;

					return true;
				});

				if (!success)
					return false;

				var buttonsMask = physical ? buttons.PhysicalButtons : buttons.LogicalButtons;
				down = (buttonsMask & bit) != 0;
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
				catch (KeysharpInputdClient.RequestFailedException)
				{
					// A protocol-level rejection (e.g. capability denied, or an output-queue
					// backpressure reject) is NOT a dead transport — the connection is healthy.
					// Let it propagate so the caller can react (retry backpressure, fall back on
					// a 403) instead of tearing down a working channel and reconnecting.
					throw;
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
			// A capability/trust prompt in EnsureCapabilities can hold 'gate' for as long as the user
			// takes to decide (tens of seconds). This probe drives sender selection and must stay
			// responsive, so don't block behind that prompt: if the lock isn't promptly available,
			// answer from the current (volatile) connection state instead of stalling.
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

		// True when the main (hook/request) connection already holds a capability. If it
		// does, the same process identity's grant is in the trust store, so requesting the
		// capability on a sibling query connection is served silently — no prompt. Used to
		// keep read-only state queries from ever raising an interactive permission prompt
		// (e.g. the key-state reconcile after a Send in a synthesis-only script).
		private static bool MainClientHasCapability(KeysharpInputdClient.Capabilities cap)
		{
			var c = client;
			return c != null && (c.GrantedCapabilities & cap) == cap;
		}

		// Ensures a query connection carries a read capability without ever prompting:
		// reuse an existing grant if present, otherwise inherit it from the main
		// connection's trust-store grant. Returns false (caller falls back) rather than
		// prompting when the capability was never granted to this process.
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
