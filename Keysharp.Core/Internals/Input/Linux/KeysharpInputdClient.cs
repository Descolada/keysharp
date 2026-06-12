using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;

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
		private const uint ClientHelloFlagForcePrompt = 0x00000001;
		// Bounds a request round-trip so a hung daemon cannot block a script thread
		// indefinitely (callers hold a manager lock across these calls).
		internal const int DefaultRequestTimeoutMs = 5000;
		// CLIENT_HELLO can trigger an interactive trust prompt; the daemon enforces a
		// 60s prompt window (KSI_PROMPT_TIMEOUT_SECONDS), so allow margin beyond that
		// for the hello response specifically.
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
			// inputd does not physically provide screen capture, but including this bit
			// in the CLIENT_HELLO causes the combined prompt to cover it and writes the
			// PID session grant so that screencap can skip its own prompt.
			ScreenCapture = 0x00000020,
			// AT-SPI access is not provided by inputd either, but it is a privileged
			// automation capability from the user's perspective, so inputd brokers the
			// trust prompt and records the user's decision.
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

			private readonly Socket socket;
			/// <summary>
			/// Hook events received on this socket while a thread was waiting for a
			/// command response. Buffered by ReadResponseFrame and drained by
			/// ReadHookEvent. Access serialized through pendingHookEventsLock so a
			/// reader-thread + control-thread workload is safe even if both end up
			/// touching this client concurrently.
			/// </summary>
		private readonly Queue<HookEvent> pendingHookEvents = new();
		private readonly Lock pendingHookEventsLock = new();
		private readonly Lock sendLock = new();
		private Timer leaseHeartbeatTimer;
			private ulong nextCorrelationId = 1;
			private bool disposed;
			private readonly int requestTimeoutMs;

		private KeysharpInputdClient(Socket socket, int requestTimeoutMs)
		{
			this.socket = socket;
			this.requestTimeoutMs = requestTimeoutMs;

			// A finite timeout means a stalled daemon surfaces as a SocketException the
			// caller can recover from, rather than an unbounded block. The hook-event
			// read path tolerates idle timeouts explicitly (see ReadAll's idleRetry).
			if (requestTimeoutMs > 0)
			{
				socket.ReceiveTimeout = requestTimeoutMs;
				socket.SendTimeout = requestTimeoutMs;
			}
		}

		internal Capabilities GrantedCapabilities { get; private set; }

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
				var correlationId = NextCorrelationId();
				SendFrame(MessageType.Heartbeat, correlationId, ReadOnlySpan<byte>.Empty);
				var response = ReadResponseFrame(MessageType.Heartbeat, correlationId);
				EnsureStatus(response, MessageType.Heartbeat, correlationId);
			}

		internal void EmergencyPassthrough()
		{
				var correlationId = NextCorrelationId();
				SendFrame(MessageType.EmergencyPassthrough, correlationId, ReadOnlySpan<byte>.Empty);
				var response = ReadResponseFrame(MessageType.EmergencyPassthrough, correlationId);
				EnsureStatus(response, MessageType.EmergencyPassthrough, correlationId);
			}

		internal BlockInputMask SetBlockInput(BlockInputMask mask)
		{
			Span<byte> payload = stackalloc byte[8];
			BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)mask);
			BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], 0);

				var correlationId = NextCorrelationId();
				SendFrame(MessageType.SetBlockInput, correlationId, payload);
				var response = ReadResponseFrame(MessageType.SetBlockInput, correlationId);
				var applied = (BlockInputMask)EnsureStatus(response, MessageType.SetBlockInput, correlationId).Detail;

				if (applied != BlockInputMask.None)
					EnsureLeaseHeartbeat();

				return applied;
			}

		internal uint SubscribeHook(HookType hookType)
		{
			Span<byte> payload = stackalloc byte[8];
			BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)hookType);
			BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], 0);

				var correlationId = NextCorrelationId();
				SendFrame(MessageType.SubscribeHook, correlationId, payload);
				var response = ReadResponseFrame(MessageType.SubscribeHook, correlationId);
				var subscriptions = EnsureStatus(response, MessageType.SubscribeHook, correlationId).Detail;

				if (subscriptions != 0)
					EnsureLeaseHeartbeat();

				return subscriptions;
			}

		internal uint UnsubscribeHook(HookType hookType)
		{
			Span<byte> payload = stackalloc byte[8];
			BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)hookType);
			BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], 0);

				var correlationId = NextCorrelationId();
				SendFrame(MessageType.UnsubscribeHook, correlationId, payload);
				var response = ReadResponseFrame(MessageType.UnsubscribeHook, correlationId);
				return EnsureStatus(response, MessageType.UnsubscribeHook, correlationId).Detail;
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

			var correlationId = NextCorrelationId();
			SendFrame(MessageType.SynthesizeInput, correlationId, payload);
			var response = ReadResponseFrame(MessageType.SynthesisResult, correlationId);
			EnsureStatus(response, MessageType.SynthesisResult, correlationId);
		}

		/// <summary>
		/// Queries the daemon for the current keyboard indicator (lock key) state.
		/// Must be called on a dedicated query connection, not on the hook-event socket.
		/// </summary>
		internal (bool CapsLock, bool NumLock, bool ScrollLock) GetIndicatorState()
		{
			var correlationId = NextCorrelationId();
			SendFrame(MessageType.GetIndicatorState, correlationId, ReadOnlySpan<byte>.Empty);
			var response = ReadResponseFrame(MessageType.IndicatorStateResult, correlationId);

			if (response.Type != MessageType.IndicatorStateResult || response.CorrelationId != correlationId)
				throw new InvalidDataException(
					$"Unexpected response to GetIndicatorState: type={response.Type} corr={response.CorrelationId} expected={correlationId}");

			if (response.Payload.Length < 3)
				return (false, false, false);

			return (response.Payload[0] != 0, response.Payload[1] != 0, response.Payload[2] != 0);
		}

		/// <summary>
		/// Queries the daemon for the current physical modifier and toggle-key state.
		/// Returns (modifiersLR, capsLock, numLock, scrollLock).
		/// modifiersLR uses the same bit layout as Keysharp's internal MOD_* constants:
		///   0=LCONTROL 1=RCONTROL 2=LALT 3=RALT 4=LSHIFT 5=RSHIFT 6=LWIN 7=RWIN.
		/// Must be called on a dedicated query connection, not on the hook-event socket.
		/// </summary>
		internal (uint ModifiersLR, bool CapsLock, bool NumLock, bool ScrollLock) QueryKeyState()
		{
			var correlationId = NextCorrelationId();
			SendFrame(MessageType.GetKeyState, correlationId, ReadOnlySpan<byte>.Empty);
			var response = ReadResponseFrame(MessageType.KeyStateResult, correlationId);

			if (response.Type != MessageType.KeyStateResult || response.CorrelationId != correlationId)
				throw new InvalidDataException(
					$"Unexpected response to GetKeyState: type={response.Type} corr={response.CorrelationId} expected={correlationId}");

			if (response.Payload.Length < 8)
				return (0u, false, false, false);

			var mods = BinaryPrimitives.ReadUInt32LittleEndian(response.Payload.AsSpan(0));
			return (mods, response.Payload[4] != 0, response.Payload[5] != 0, response.Payload[6] != 0);
		}

		/// <summary>
		/// Queries the daemon for the last raw evdev ABS_X/ABS_Y pointer sample.
		/// Screen-space mapping stays in Keysharp because the daemon has no display model.
		/// </summary>
		internal bool TryGetPointerPosition(out PointerPosition position)
		{
			position = default;

			var correlationId = NextCorrelationId();
			SendFrame(MessageType.GetPointerPosition, correlationId, ReadOnlySpan<byte>.Empty);
			var response = ReadResponseFrame(MessageType.PointerPositionResult, correlationId);

			if (response.Type != MessageType.PointerPositionResult || response.CorrelationId != correlationId)
				throw new InvalidDataException(
					$"Unexpected response to GetPointerPosition: type={response.Type} corr={response.CorrelationId} expected={correlationId}");

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

		internal HookEvent ReadHookEvent()
		{
			lock (pendingHookEventsLock)
			{
				if (pendingHookEvents.Count != 0)
					return pendingHookEvents.Dequeue();
			}

			// The hook connection waits an unbounded time for the next input event, so
			// an idle receive timeout here is not a failure — keep waiting.
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

			var correlationId = eventId;
			SendFrame(MessageType.HookDecision, correlationId, payload);
			var response = ReadResponseFrame(MessageType.HookDecision, correlationId);
			EnsureStatus(response, MessageType.HookDecision, correlationId);
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

			// The hello may block on an interactive trust prompt; widen the receive
			// timeout to cover the daemon's prompt window, then restore it.
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

			if (response.Type != MessageType.ClientHello || response.CorrelationId != correlationId)
				throw new InvalidDataException($"Unexpected hello response type={response.Type} correlation={response.CorrelationId}.");

			if (response.Payload.Length != 8)
				throw new InvalidDataException($"Unexpected hello response size {response.Payload.Length}.");

			var status = BinaryPrimitives.ReadInt32LittleEndian(response.Payload);
			var granted = (Capabilities)BinaryPrimitives.ReadUInt32LittleEndian(response.Payload[4..]);
			return (status, granted);
		}

		private ulong NextCorrelationId() => nextCorrelationId++;

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

		private void EnsureLeaseHeartbeat()
		{
			if (leaseHeartbeatTimer != null)
				return;

			leaseHeartbeatTimer = new Timer(
				_ =>
				{
					try
					{
						// Correlation zero is a protocol 0.2 one-way lease renewal,
						// so it cannot interfere with the hook reader's receive stream.
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
				payload);
		}

		private void ReadAll(Span<byte> buffer, bool idleRetry = false)
		{
			var offset = 0;

			while (offset < buffer.Length)
			{
				int read;

				try
				{
					read = socket.Receive(buffer[offset..], SocketFlags.None);
				}
				catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut && idleRetry && offset == 0)
				{
					// No frame has started arriving yet: this is an idle hook connection
					// waiting for the next event, not a stalled daemon. Keep waiting.
					continue;
				}

				if (read == 0)
					throw new EndOfStreamException("keysharp-inputd disconnected.");

				offset += read;
			}
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
				throw new IOException($"keysharp-inputd request {expectedType} failed with status {status}, detail {detail}.");

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

		private readonly record struct Frame(MessageType Type, uint ClientId, ulong CorrelationId, byte[] Payload);
		private readonly record struct StatusPayload(int Status, uint Detail);
	}
}
#endif
