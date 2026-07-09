using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Keysharp.Builtins;

#if LINUX
namespace Keysharp.Internals.Input.Linux
{
	internal sealed class KeysharpInputdClient : IDisposable
	{
		internal const string SocketEnvironmentVariable = "KEYSHARP_INPUTD_SOCKET";
		internal const string DefaultSocketPathValue = "/run/keysharp-inputd/keysharp-inputd.sock";

		private const uint ProtocolMajor = 0;
		private const uint ProtocolMinor = 2;
		private const int HeaderSize = 24;
		private const int MaxMessageSize = 65536;
		private const int InputSize = 40;
		internal const int KeyStateBitmapBytes = 96;
		private const uint ClientHelloFlagForcePrompt = 0x00000001;
		// Bounds request round-trips so a hung daemon cannot block a script thread.
		internal const int DefaultRequestTimeoutMs = 5000;
		// CLIENT_HELLO may wait on the daemon's interactive trust prompt.
		private const int HelloResponseTimeoutMs = 75000;
		private const int LeaseHeartbeatPeriodMs = 5000;

		[Flags]
		internal enum Capabilities : uint
		{
			None = 0,
			HookKeyboard = 0x00000001,
			HookMouse = 0x00000002,
			SynthKeyboard = 0x00000004,
			SynthMouse = 0x00000008,
			BlockInput = 0x00000010,
			// Brokered in the combined inputd trust prompt for helper reuse.
			ScreenCapture = 0x00000020,
			AccessibilityAutomation = 0x00000040,
		}

		internal enum MessageType : uint
		{
			ClientHello = 1,
			Heartbeat = 3,
			SubscribeHook = 10,
			UnsubscribeHook = 11,
			HookEvent = 12,
			HookDecision = 13,
			SynthesizeInput = 20,
			SynthesisResult = 21,
			EmergencyPassthrough = 30,
			SetBlockInput = 31,
			GetIndicatorState    = 40,
			IndicatorStateResult = 41,
			GetPointerPosition   = 42,
			PointerPositionResult = 43,
			GetKeyState          = 44,
			KeyStateResult       = 45,
			GetPointerButtons    = 46,
			PointerButtonsResult = 47,
		}

		internal enum HookType : uint
		{
			KeyboardLowLevel = 13,
			MouseLowLevel = 14,
		}

		internal enum HookDecision : uint
		{
			Pass = 0,
			Block = 1,
			Modify = 2,
		}

		[Flags]
		internal enum BlockInputMask : uint
		{
			None = 0,
			Keyboard = 0x00000001,
			Mouse = 0x00000002,
		}

		internal enum InputType : uint
		{
			Mouse = 0,
			Keyboard = 1,
			Hardware = 2,
		}

		[Flags]
		internal enum KeyEventFlags : uint
		{
			ExtendedKey = 0x0001,
			KeyUp = 0x0002,
			Unicode = 0x0004,
			ScanCode = 0x0008,
		}

		[Flags]
		internal enum MouseEventFlags : uint
		{
			Move = 0x0001,
			LeftDown = 0x0002,
			LeftUp = 0x0004,
			RightDown = 0x0008,
			RightUp = 0x0010,
			MiddleDown = 0x0020,
			MiddleUp = 0x0040,
			XDown = 0x0080,
			XUp = 0x0100,
			Wheel = 0x0800,
			HWheel = 0x1000,
			MoveNoCoalesce = 0x2000,
			VirtualDesk = 0x4000,
			Absolute = 0x8000,
		}

		internal readonly record struct KeyboardInput(ushort Vk, ushort Scan, KeyEventFlags Flags, uint Time = 0, ulong ExtraInfo = 0);
		internal readonly record struct MouseInput(int Dx, int Dy, uint MouseData, MouseEventFlags Flags, uint Time = 0, ulong ExtraInfo = 0);

		internal readonly record struct Input(InputType Type, KeyboardInput Keyboard, MouseInput Mouse)
		{
			internal static Input Key(ushort vk, ushort scan = 0, KeyEventFlags flags = 0, uint time = 0, ulong extraInfo = 0)
				=> new(InputType.Keyboard, new KeyboardInput(vk, scan, flags, time, extraInfo), default);

			internal static Input MouseEvent(int dx, int dy, uint mouseData, MouseEventFlags flags, uint time = 0, ulong extraInfo = 0)
				=> new(InputType.Mouse, default, new MouseInput(dx, dy, mouseData, flags, time, extraInfo));
		}

		internal readonly record struct KeyboardHookEvent(
			uint Message,
			uint VkCode,
			uint ScanCode,
			uint Flags,
			ulong TimeMs,
			ulong ExtraInfo,
			uint DeviceId);

		internal readonly record struct MouseHookEvent(
			uint Message,
			int X,
			int Y,
			uint MouseData,
			uint Flags,
			ulong TimeMs,
			ulong ExtraInfo,
			uint DeviceId);

		internal readonly record struct HookEvent(
			ulong EventId,
			HookType HookType,
			KeyboardHookEvent Keyboard,
			MouseHookEvent Mouse);

		internal readonly record struct PointerPosition(
			int X,
			int Y,
			int XMin,
			int XMax,
			int YMin,
			int YMax);

		internal readonly record struct KeyStateSnapshot(
			uint ModifiersLR,
			bool CapsLock,
			bool NumLock,
			bool ScrollLock,
			byte[] LogicalKeys,
			byte[] PhysicalKeys);

		private readonly Socket socket;
		/// <summary>
		/// Hook events received on this socket while a thread was waiting for a
		/// command response. Buffered by ReadResponseFrame and drained by
		/// ReadHookEvent.
		/// </summary>
		private readonly Queue<HookEvent> pendingHookEvents = new();
		private readonly Lock pendingHookEventsLock = new();
		private readonly Lock sendLock = new();
		private Timer leaseHeartbeatTimer;
		// Null renews unconditionally; false stops renewing the daemon grab lease.
		private volatile Func<bool> leaseLivenessProbe;
		private ulong nextCorrelationId = 1;
		private bool disposed;
		private readonly int requestTimeoutMs;

		private KeysharpInputdClient(Socket socket, int requestTimeoutMs)
		{
			this.socket = socket;
			this.requestTimeoutMs = requestTimeoutMs;

			if (requestTimeoutMs > 0)
			{
				socket.ReceiveTimeout = requestTimeoutMs;
				socket.SendTimeout = requestTimeoutMs;
			}
		}

		internal sealed class RequestFailedException : IOException
		{
			internal MessageType RequestType { get; }
			internal int Status { get; }
			internal uint Detail { get; }

			internal RequestFailedException(MessageType requestType, int status, uint detail)
				: base($"keysharp-inputd request {requestType} failed with status {status}, detail {detail}.")
			{
				RequestType = requestType;
				Status = status;
				Detail = detail;
			}
		}

		internal static bool IsStaleHookDecisionFailure(Exception exception)
			=> exception is RequestFailedException
			{
				RequestType: MessageType.HookDecision,
				Detail: 2u
			};

		// SYNTHESIS_RESULT detail 12: transient bounded-queue backpressure.
		internal const uint SynthesisBackpressureDetail = 12u;

		internal static bool IsSynthesisBackpressure(Exception exception)
			=> exception is RequestFailedException
			{
				RequestType: MessageType.SynthesisResult,
				Detail: SynthesisBackpressureDetail
			};

		internal Capabilities GrantedCapabilities { get; private set; }

		/// <summary>
		/// Cheap, non-blocking liveness probe -- a single Poll() syscall, no
		/// actual request. False once the socket is disposed or the daemon has
		/// already closed/reset the connection (e.g. it crashed or restarted).
		/// A live, idle connection is never reported readable, so this cannot
		/// false-negative on a healthy connection; it CAN still false-positive
		/// right up until the daemon actually dies (this is a snapshot, not a
		/// guarantee), so callers must still handle a transport exception on
		/// the next real request either way -- this only catches the common
		/// case of a cached connection that is already known-dead before that
		/// request is even attempted, letting the caller reconnect
		/// transparently instead of surfacing an avoidable exception.
		/// </summary>
		internal bool IsConnected
		{
			get
			{
				if (disposed)
					return false;

				try
				{
					return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
				}
				catch (ObjectDisposedException)
				{
					return false;
				}
				catch (SocketException)
				{
					return false;
				}
			}
		}

		internal static string DefaultSocketPath
		{
			get
			{
				var configured = Environment.GetEnvironmentVariable(SocketEnvironmentVariable);

				if (!string.IsNullOrWhiteSpace(configured))
					return configured;

				return DefaultSocketPathValue;
			}
		}

		internal static KeysharpInputdClient Connect(
			Capabilities requested = Capabilities.None,
			string socketPath = null,
			int requestTimeoutMs = DefaultRequestTimeoutMs)
		{
			var endpoint = new UnixDomainSocketEndPoint(socketPath ?? DefaultSocketPath);
			var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

			try
			{
				if (requestTimeoutMs > 0)
				{
					using var cancellation = new CancellationTokenSource(requestTimeoutMs);

					try
					{
						socket.ConnectAsync(endpoint, cancellation.Token).AsTask().GetAwaiter().GetResult();
					}
					catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
					{
						throw new SocketException((int)SocketError.TimedOut);
					}
				}
				else
					socket.Connect(endpoint);

				var client = new KeysharpInputdClient(socket, requestTimeoutMs);
				client.RequestCapabilities(requested);
				return client;
			}
			catch
			{
				socket.Dispose();
				throw;
			}
		}

		internal void SendHeartbeat()
		{
			SendStatusRequest(MessageType.Heartbeat);
		}

		internal void EmergencyPassthrough()
		{
			SendStatusRequest(MessageType.EmergencyPassthrough);
		}

		internal BlockInputMask SetBlockInput(BlockInputMask mask)
		{
			Span<byte> payload = stackalloc byte[8];
			BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)mask);
			BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], 0);

			var applied = (BlockInputMask)SendStatusRequest(MessageType.SetBlockInput, payload).Detail;

			if (applied != BlockInputMask.None)
				EnsureLeaseHeartbeat();

			return applied;
		}

		internal uint SubscribeHook(HookType hookType)
		{
			Span<byte> payload = stackalloc byte[8];
			BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)hookType);
			BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], 0);

			var subscriptions = SendStatusRequest(MessageType.SubscribeHook, payload).Detail;

			if (subscriptions != 0)
				EnsureLeaseHeartbeat();

			return subscriptions;
		}

		internal uint UnsubscribeHook(HookType hookType)
		{
			Span<byte> payload = stackalloc byte[8];
			BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)hookType);
			BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], 0);

			return SendStatusRequest(MessageType.UnsubscribeHook, payload).Detail;
		}

		[Flags]
		internal enum SynthFlags : uint
		{
			None = 0,
			BypassHook = 0x00000001,  // mirrors KSI_SYNTH_FLAG_BYPASS_HOOK
		}

		internal void SendInput(IReadOnlyList<Input> inputs, SynthFlags flags = SynthFlags.None)
		{
			if (inputs == null)
				throw new ArgumentNullException(nameof(inputs));

			var payload = new byte[8 + (inputs.Count * InputSize)];
			BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)inputs.Count);
			BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)flags);

			for (var i = 0; i < inputs.Count; i++)
				WriteInput(payload.AsSpan(8 + (i * InputSize), InputSize), inputs[i]);

			SendStatusRequest(MessageType.SynthesizeInput, MessageType.SynthesisResult, payload);
		}

		/// <summary>Queries the daemon for current lock-key indicator state.</summary>
		internal (bool CapsLock, bool NumLock, bool ScrollLock) GetIndicatorState()
		{
			var response = SendRequest(MessageType.GetIndicatorState, MessageType.IndicatorStateResult);

			if (response.Payload.Length < 3)
				return (false, false, false);

			return (response.Payload[0] != 0, response.Payload[1] != 0, response.Payload[2] != 0);
		}

		/// <summary>Queries logical/toggle keyboard state plus optional key bitmaps.</summary>
		internal KeyStateSnapshot QueryKeyState()
		{
			var response = SendRequest(MessageType.GetKeyState, MessageType.KeyStateResult);

			if (response.Payload.Length < 8)
				return new(0u, false, false, false, [], []);

			var mods = BinaryPrimitives.ReadUInt32LittleEndian(response.Payload.AsSpan(0));
			var logicalKeys = System.Array.Empty<byte>();
			var physicalKeys = System.Array.Empty<byte>();

			if (response.Payload.Length >= 8 + KeyStateBitmapBytes)
			{
				logicalKeys = new byte[KeyStateBitmapBytes];
				response.Payload.AsSpan(8, KeyStateBitmapBytes).CopyTo(logicalKeys);
			}

			if (response.Payload.Length >= 8 + (KeyStateBitmapBytes * 2))
			{
				physicalKeys = new byte[KeyStateBitmapBytes];
				response.Payload.AsSpan(8 + KeyStateBitmapBytes, KeyStateBitmapBytes).CopyTo(physicalKeys);
			}

			return new(mods, response.Payload[4] != 0, response.Payload[5] != 0, response.Payload[6] != 0, logicalKeys, physicalKeys);
		}

		/// <summary>Queries the last raw evdev ABS_X/ABS_Y pointer sample.</summary>
		internal bool TryGetPointerPosition(out PointerPosition position)
		{
			position = default;

			var response = SendRequest(MessageType.GetPointerPosition, MessageType.PointerPositionResult);

			if (response.Payload.Length != 28 || response.Payload[0] == 0)
				return false;

			position = new PointerPosition(
				BinaryPrimitives.ReadInt32LittleEndian(response.Payload.AsSpan(4)),
				BinaryPrimitives.ReadInt32LittleEndian(response.Payload.AsSpan(8)),
				BinaryPrimitives.ReadInt32LittleEndian(response.Payload.AsSpan(12)),
				BinaryPrimitives.ReadInt32LittleEndian(response.Payload.AsSpan(16)),
				BinaryPrimitives.ReadInt32LittleEndian(response.Payload.AsSpan(20)),
				BinaryPrimitives.ReadInt32LittleEndian(response.Payload.AsSpan(24)));
			return true;
		}

		internal readonly record struct PointerButtons(uint LogicalButtons, uint PhysicalButtons);

		/// <summary>Queries logical and physical mouse-button masks.</summary>
		internal bool TryGetPointerButtons(out PointerButtons buttons)
		{
			buttons = default;

			var response = SendRequest(MessageType.GetPointerButtons, MessageType.PointerButtonsResult);

			if (response.Payload.Length < 8 || response.Payload[0] == 0)
				return false;

			var compatPhysicalButtons = BinaryPrimitives.ReadUInt32LittleEndian(response.Payload.AsSpan(4));

			if (response.Payload.Length >= 16)
			{
				buttons = new(
					BinaryPrimitives.ReadUInt32LittleEndian(response.Payload.AsSpan(8)),
					BinaryPrimitives.ReadUInt32LittleEndian(response.Payload.AsSpan(12)));
			}
			else
			{
				buttons = new(compatPhysicalButtons, compatPhysicalButtons);
			}

			return true;
		}

		internal HookEvent ReadHookEvent()
		{
			lock (pendingHookEventsLock)
			{
				if (pendingHookEvents.Count != 0)
					return pendingHookEvents.Dequeue();
			}

			var frame = ReadFrame(idleRetry: true);

			if (frame.Type != MessageType.HookEvent)
				throw new InvalidDataException($"Expected hook event, got {frame.Type}.");

			return ParseHookEvent(frame);
		}

		private static HookEvent ParseHookEvent(Frame frame)
		{
			if (frame.Payload.Length < 16)
				throw new InvalidDataException("Hook event payload is too small.");

			var eventId = BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload);
			var hookType = (HookType)BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload[8..]);

			if (hookType == HookType.KeyboardLowLevel)
			{
				if (frame.Payload.Length != 56)
					throw new InvalidDataException($"Keyboard hook event has invalid size {frame.Payload.Length}.");

				var keyboard = ReadKeyboardHookEvent(frame.Payload[16..]);
				return new HookEvent(eventId, hookType, keyboard, default);
			}

			if (hookType == HookType.MouseLowLevel)
			{
				if (frame.Payload.Length != 64)
					throw new InvalidDataException($"Mouse hook event has invalid size {frame.Payload.Length}.");

				var mouse = ReadMouseHookEvent(frame.Payload[16..]);
				return new HookEvent(eventId, hookType, default, mouse);
			}

			throw new InvalidDataException($"Unknown hook type {hookType}.");
		}

		internal void SendHookDecision(ulong eventId, HookDecision decision, IReadOnlyList<Input> replacementInputs = null)
		{
			var inputCount = replacementInputs?.Count ?? 0;
			var payload = new byte[16 + (inputCount * InputSize)];
			BinaryPrimitives.WriteUInt64LittleEndian(payload, eventId);
			BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)decision);
			BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), (uint)inputCount);

			for (var i = 0; i < inputCount; i++)
				WriteInput(payload.AsSpan(16 + (i * InputSize), InputSize), replacementInputs[i]);

			SendStatusRequest(MessageType.HookDecision, MessageType.HookDecision, payload, eventId);
		}

		public void Dispose()
		{
			if (disposed)
				return;

			disposed = true;
			leaseHeartbeatTimer?.Dispose();
			leaseHeartbeatTimer = null;
			socket.Dispose();
		}

		internal bool TryRequestCapabilities(Capabilities requested, out int status, bool forcePrompt = false)
			=> TryRequestCapabilities(requested, requested, out status, forcePrompt);

		internal bool TryRequestCapabilities(Capabilities requested, Capabilities requiredFromInputd, out int status, bool forcePrompt = false)
		{
			var hello = ExchangeHello(requested, forcePrompt);
			status = hello.Status;
			GrantedCapabilities |= hello.Granted;
			return status == 0 && (GrantedCapabilities & requiredFromInputd) == requiredFromInputd;
		}

		internal void RequestCapabilities(Capabilities requested, bool forcePrompt = false)
		{
			if (!TryRequestCapabilities(requested, out var status, forcePrompt))
				throw new IOException(
					$"keysharp-inputd hello failed with status {status}. Requested: {requested}. Granted: {GrantedCapabilities}.");
		}

		private (int Status, Capabilities Granted) ExchangeHello(Capabilities requested, bool forcePrompt)
		{
			Span<byte> payload = stackalloc byte[8];
			BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)requested);
			BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], forcePrompt ? ClientHelloFlagForcePrompt : 0);

			var correlationId = NextCorrelationId();
			SendFrame(MessageType.ClientHello, correlationId, payload);

			Frame response;
			var previousReceiveTimeout = socket.ReceiveTimeout;

			if (requestTimeoutMs > 0)
				socket.ReceiveTimeout = HelloResponseTimeoutMs;

			try
			{
				response = ReadResponseFrame(MessageType.ClientHello, correlationId);
			}
			finally
			{
				if (requestTimeoutMs > 0)
					socket.ReceiveTimeout = previousReceiveTimeout;
			}

			if (response.Payload.Length != 8)
				throw new InvalidDataException($"Unexpected hello response size {response.Payload.Length}.");

			// The daemon stamps its own compiled protocol version into every response
			// header, so a successful hello already tells us which daemon we reached.
			// Surface it (and flag a mismatch) to make a stale daemon binary visible.
			LogDaemonVersion(response.Major, response.Minor);

			var status = BinaryPrimitives.ReadInt32LittleEndian(response.Payload);
			var granted = (Capabilities)BinaryPrimitives.ReadUInt32LittleEndian(response.Payload[4..]);
			return (status, granted);
		}

		// Guards the one-shot daemon-version log below so reconnects and the several
		// client connections a script opens do not spam the debug output.
		private static int daemonVersionLogged;

		/// <summary>
		/// Logs the daemon's negotiated protocol version once per process, warning
		/// when it differs from the client's compiled version. A daemon whose minor
		/// is &lt;= the client's still negotiates on the wire (see <see cref="ReadFrame"/>),
		/// so without this a stale daemon would silently lack newer behavior.
		/// </summary>
		private static void LogDaemonVersion(ushort daemonMajor, ushort daemonMinor)
		{
			if (Interlocked.Exchange(ref daemonVersionLogged, 1) != 0)
				return;

			if (daemonMajor == ProtocolMajor && daemonMinor == ProtocolMinor)
			{
				Ks.OutputDebugLine($"keysharp-inputd: connected to daemon protocol {daemonMajor}.{daemonMinor}.");
			}
			else
			{
				Ks.OutputDebugLine(
					$"keysharp-inputd: WARNING - daemon protocol {daemonMajor}.{daemonMinor} differs from client " +
					$"{ProtocolMajor}.{ProtocolMinor}; the keysharp-inputd binary may be stale. Reinstall/restart it " +
					"if input behaves unexpectedly.");
			}
		}

		private ulong NextCorrelationId() => nextCorrelationId++;

		private StatusPayload SendStatusRequest(MessageType type)
			=> SendStatusRequest(type, type, ReadOnlySpan<byte>.Empty);

		private StatusPayload SendStatusRequest(MessageType type, ReadOnlySpan<byte> payload)
			=> SendStatusRequest(type, type, payload);

		private StatusPayload SendStatusRequest(
			MessageType requestType,
			MessageType responseType,
			ReadOnlySpan<byte> payload,
			ulong? correlationId = null)
		{
			var id = correlationId ?? NextCorrelationId();
			SendFrame(requestType, id, payload);
			return EnsureStatus(ReadResponseFrame(responseType, id), responseType, id);
		}

		private Frame SendRequest(MessageType requestType, MessageType responseType)
			=> SendRequest(requestType, responseType, ReadOnlySpan<byte>.Empty);

		private Frame SendRequest(MessageType requestType, MessageType responseType, ReadOnlySpan<byte> payload)
		{
			var correlationId = NextCorrelationId();
			SendFrame(requestType, correlationId, payload);
			return ReadResponseFrame(responseType, correlationId);
		}

		private void SendFrame(MessageType type, ulong correlationId, ReadOnlySpan<byte> payload)
		{
			lock (sendLock)
			{
				ThrowIfDisposed();

				if (payload.Length > MaxMessageSize - HeaderSize)
					throw new ArgumentOutOfRangeException(nameof(payload));

				Span<byte> header = stackalloc byte[HeaderSize];
				BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(HeaderSize + payload.Length));
				BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)ProtocolMajor);
				BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)ProtocolMinor);
				BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)type);
				BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0);
				BinaryPrimitives.WriteUInt64LittleEndian(header[16..], correlationId);

				WriteAll(header);
				WriteAll(payload);
			}
		}

		/// <summary>Installs a probe controlling whether periodic grab-lease heartbeats renew.</summary>
		internal void SetLeaseLivenessProbe(Func<bool> probe) => leaseLivenessProbe = probe;

		private void EnsureLeaseHeartbeat()
		{
			if (leaseHeartbeatTimer != null)
				return;

			leaseHeartbeatTimer = new Timer(
				_ =>
				{
					try
					{
						var probe = leaseLivenessProbe;

						if (probe != null && !probe())
							return;

						// Correlation zero is a one-way lease renewal.
						SendFrame(MessageType.Heartbeat, 0, ReadOnlySpan<byte>.Empty);
					}
					catch
					{
						// Normal request/hook paths own recovery and disposal.
					}
				},
				null,
				LeaseHeartbeatPeriodMs,
				LeaseHeartbeatPeriodMs);
		}

		private Frame ReadFrame(bool idleRetry = false)
		{
			ThrowIfDisposed();

			var header = new byte[HeaderSize];
			ReadAll(header, idleRetry);

			var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
			var major = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
			var minor = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));

			if (size < HeaderSize || size > MaxMessageSize)
				throw new InvalidDataException($"Invalid inputd frame size {size}.");

			if (major != ProtocolMajor || minor > ProtocolMinor)
				throw new InvalidDataException($"Unsupported inputd protocol version {major}.{minor}.");

			var payload = new byte[size - HeaderSize];
			ReadAll(payload);

			return new Frame(
				(MessageType)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)),
				BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)),
				BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16)),
				payload,
				major,
				minor);
		}

		private void ReadAll(Span<byte> buffer, bool idleRetry = false)
		{
			var offset = 0;

			while (offset < buffer.Length)
			{
				if (!socket.Poll(ReceiveTimeoutMicroseconds(), SelectMode.SelectRead))
				{
					if (idleRetry && offset == 0)
						continue;

					throw new SocketException((int)SocketError.TimedOut);
				}

				var read = socket.Receive(buffer[offset..], SocketFlags.None);

				if (read == 0)
					throw new EndOfStreamException("keysharp-inputd disconnected.");

				offset += read;
			}
		}

		private int ReceiveTimeoutMicroseconds()
		{
			var timeoutMs = socket.ReceiveTimeout;

			if (timeoutMs <= 0)
				return -1;

			return timeoutMs >= int.MaxValue / 1000 ? int.MaxValue : timeoutMs * 1000;
		}

		private Frame ReadResponseFrame(MessageType expectedType, ulong expectedCorrelationId)
		{
			for (;;)
			{
				var frame = ReadFrame();

				if (frame.Type == expectedType && frame.CorrelationId == expectedCorrelationId)
					return frame;

				if (frame.Type == MessageType.HookEvent)
				{
					var hookEvent = ParseHookEvent(frame);

					lock (pendingHookEventsLock)
						pendingHookEvents.Enqueue(hookEvent);

					continue;
				}

				throw new InvalidDataException(
					$"Unexpected response type={frame.Type} correlation={frame.CorrelationId}; expected type={expectedType} correlation={expectedCorrelationId}.");
			}
		}

		private void WriteAll(ReadOnlySpan<byte> buffer)
		{
			var offset = 0;

			while (offset < buffer.Length)
			{
				var written = socket.Send(buffer[offset..], SocketFlags.None);

				if (written == 0)
					throw new IOException("Failed to write to keysharp-inputd.");

				offset += written;
			}
		}

		private static StatusPayload EnsureStatus(Frame frame, MessageType expectedType, ulong expectedCorrelationId)
		{
			if (frame.Type != expectedType || frame.CorrelationId != expectedCorrelationId)
				throw new InvalidDataException($"Unexpected status response type={frame.Type} correlation={frame.CorrelationId}.");

			if (frame.Payload.Length != 8)
				throw new InvalidDataException($"Unexpected status payload size {frame.Payload.Length}.");

			var status = BinaryPrimitives.ReadInt32LittleEndian(frame.Payload);
			var detail = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload[4..]);

			if (status != 0)
				throw new RequestFailedException(expectedType, status, detail);

			return new StatusPayload(status, detail);
		}

		private static void WriteInput(Span<byte> destination, Input input)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)input.Type);
			BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], 0);

			if (input.Type == InputType.Keyboard)
			{
				BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], input.Keyboard.Vk);
				BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], input.Keyboard.Scan);
				BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)input.Keyboard.Flags);
				BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], input.Keyboard.Time);
				BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], input.Keyboard.ExtraInfo);
			}
			else if (input.Type == InputType.Mouse)
			{
				BinaryPrimitives.WriteInt32LittleEndian(destination[8..], input.Mouse.Dx);
				BinaryPrimitives.WriteInt32LittleEndian(destination[12..], input.Mouse.Dy);
				BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], input.Mouse.MouseData);
				BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], (uint)input.Mouse.Flags);
				BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], input.Mouse.Time);
				BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], input.Mouse.ExtraInfo);
			}
			else
			{
				throw new NotSupportedException($"Input type {input.Type} is not supported.");
			}
		}

		private static KeyboardHookEvent ReadKeyboardHookEvent(ReadOnlySpan<byte> payload)
			=> new(
				BinaryPrimitives.ReadUInt32LittleEndian(payload),
				BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
				BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]),
				BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]),
				BinaryPrimitives.ReadUInt64LittleEndian(payload[16..]),
				BinaryPrimitives.ReadUInt64LittleEndian(payload[24..]),
				BinaryPrimitives.ReadUInt32LittleEndian(payload[32..]));

		private static MouseHookEvent ReadMouseHookEvent(ReadOnlySpan<byte> payload)
			=> new(
				BinaryPrimitives.ReadUInt32LittleEndian(payload),
				BinaryPrimitives.ReadInt32LittleEndian(payload[4..]),
				BinaryPrimitives.ReadInt32LittleEndian(payload[8..]),
				BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]),
				BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]),
				BinaryPrimitives.ReadUInt64LittleEndian(payload[24..]),
				BinaryPrimitives.ReadUInt64LittleEndian(payload[32..]),
				BinaryPrimitives.ReadUInt32LittleEndian(payload[40..]));

		private void ThrowIfDisposed()
		{
			if (disposed)
				throw new ObjectDisposedException(nameof(KeysharpInputdClient));
		}

		private readonly record struct Frame(MessageType Type, uint ClientId, ulong CorrelationId, byte[] Payload, ushort Major, ushort Minor);
		private readonly record struct StatusPayload(int Status, uint Detail);
	}
}
#endif
