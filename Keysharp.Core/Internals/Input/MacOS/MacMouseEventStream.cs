#if OSX
using Keysharp.Internals.Input.Hooks;
using static Keysharp.Internals.Input.Keyboard.KeyboardMouseSender;

namespace Keysharp.Internals.Input.MacOS
{
	/// <summary>
	/// Maintains the state Quartz expects across a synthetic mouse stream. CGEventPost is asynchronous,
	/// so relative movement, drag type, click count and down/up event numbers cannot be reconstructed by
	/// querying WindowServer independently for each event.
	/// </summary>
	internal sealed class MacMouseEventStream
	{
		internal class Sink
		{
			internal static readonly Sink Native = new();
			internal virtual bool TryGetCursorPosition(out int x, out int y) => Platform.Mouse.TryGetCursorPos(out x, out y);
			internal virtual bool PostMove(int x, int y, long extraInfo, MouseButton draggingButton)
				=> MacNativeInput.PostMouseMove(x, y, extraInfo, draggingButton);
			internal virtual bool PostButton(MouseButton button, bool down, int x, int y, long extraInfo, int clickCount, long eventNumber)
				=> MacNativeInput.PostMouseButton(button, down, x, y, extraInfo, clickCount, eventNumber);
		}

		private static long nextEventNumber;
		private readonly Lock sendLock = new();
		private readonly Lock streamLock = new();
		private readonly long[] activeEventNumbers = new long[6];
		private readonly int[] activeClickCounts = new int[6];
		private readonly Sink sink;
		private readonly Func<long> timestamp;
		private readonly long clickIntervalTicks;
		private readonly int clickTolerance;
		private uint syntheticButtons;
		private uint observedButtons;
		// Sender revisions reject callbacks which began before a newer post; native revisions
		// preserve observations made while a post which ultimately fails was in flight.
		private long senderRevision;
		private long nativeRevision;
		private bool positionKnown;
		private int x;
		private int y;
		private MouseButton lastClickButton;
		private long lastClickAt;
		private int lastClickX;
		private int lastClickY;
		private int lastClickCount;

		internal MacMouseEventStream(Sink sink = null, TimeSpan? clickInterval = null,
			int clickTolerance = 4, Func<long> timestamp = null, long timestampFrequency = 0)
		{
			this.sink = sink ?? Sink.Native;
			this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
			var frequency = timestampFrequency > 0 ? timestampFrequency : Stopwatch.Frequency;
			clickIntervalTicks = (long)(GetClickInterval(clickInterval).TotalSeconds * frequency);
			this.clickTolerance = Math.Max(0, clickTolerance);
		}

		internal long SenderRevision { get { lock (streamLock) return senderRevision; } }

		internal void ResyncButtons(Func<uint, bool> query = null)
		{
			query ??= button => MacNativeInput.CGEventSourceButtonState(
				MacNativeInput.kCGEventSourceStateHIDSystemState, button);
			uint buttons = 0;
			for (uint button = 0; button < 5; button++)
				if (query(button)) buttons |= 1u << (int)button;
			lock (streamLock)
			{
				observedButtons = buttons;
				senderRevision++;
			}
		}

		internal void ResetObservedButtons()
		{
			lock (streamLock)
			{
				observedButtons = 0;
				senderRevision++;
			}
		}

		internal void MoveAbsolute(int destinationX, int destinationY, long extraInfo)
		{
			lock (sendLock)
			{
				MouseButton draggingButton;
				long nativeBefore;
				lock (streamLock)
				{
					draggingButton = FirstPressedButton(syntheticButtons | observedButtons);
					x = destinationX;
					y = destinationY;
					positionKnown = true;
					senderRevision++;
					nativeBefore = nativeRevision;
				}
				if (!sink.PostMove(destinationX, destinationY, extraInfo, draggingButton))
					InvalidateFailedPost(nativeBefore);
			}
		}

		internal bool MoveRelative(int deltaX, int deltaY, long extraInfo)
		{
			lock (sendLock)
			{
				if (!EnsurePosition())
					return false;

				int destinationX, destinationY;
				MouseButton draggingButton;
				long nativeBefore;
				lock (streamLock)
				{
					destinationX = (int)Math.Clamp((long)x + deltaX, int.MinValue, int.MaxValue);
					destinationY = (int)Math.Clamp((long)y + deltaY, int.MinValue, int.MaxValue);
					draggingButton = FirstPressedButton(syntheticButtons | observedButtons);
					x = destinationX;
					y = destinationY;
					senderRevision++;
					nativeBefore = nativeRevision;
				}
				var posted = sink.PostMove(destinationX, destinationY, extraInfo, draggingButton);
				if (!posted)
					InvalidateFailedPost(nativeBefore);
				return posted;
			}
		}

		private void InvalidateFailedPost(long nativeBefore)
		{
			lock (streamLock)
			{
				if (nativeRevision == nativeBefore)
					positionKnown = false;
			}
		}

		internal void Button(MouseButton button, bool down, int eventX, int eventY, long extraInfo)
		{
			lock (sendLock)
			{
				if (button == MouseButton.NoButton)
					return;

				ResolvePosition(ref eventX, ref eventY);
				var index = (int)button;
				long eventNumber;
				int clickCount;
				long nativeBefore;
				uint syntheticBefore;
				long activeNumberBefore;
				int activeCountBefore;
				(MouseButton button, long at, int x, int y, int count) clickBefore;
				lock (streamLock)
				{
					syntheticBefore = syntheticButtons;
					activeNumberBefore = activeEventNumbers[index];
					activeCountBefore = activeClickCounts[index];
					clickBefore = (lastClickButton, lastClickAt, lastClickX, lastClickY, lastClickCount);
					nativeBefore = nativeRevision;
					if (down)
					{
						eventNumber = Interlocked.Increment(ref nextEventNumber);
						clickCount = NextClickCount(button, eventX, eventY);
						activeEventNumbers[index] = eventNumber;
						activeClickCounts[index] = clickCount;
						syntheticButtons |= ButtonMask(button);
					}
					else
					{
						eventNumber = activeEventNumbers[index];
						if (eventNumber == 0)
							eventNumber = Interlocked.Increment(ref nextEventNumber);
						clickCount = Math.Max(1, activeClickCounts[index]);
						syntheticButtons &= ~ButtonMask(button);
						activeEventNumbers[index] = 0;
						activeClickCounts[index] = 0;
						if (activeNumberBefore != 0)
							CompleteClick(button, eventX, eventY, clickCount);
					}
					x = eventX;
					y = eventY;
					positionKnown = true;
					senderRevision++;
				}

				if (sink.PostButton(button, down, eventX, eventY, extraInfo, clickCount, eventNumber))
					return;
				lock (streamLock)
				{
					syntheticButtons = syntheticBefore;
					activeEventNumbers[index] = activeNumberBefore;
					activeClickCounts[index] = activeCountBefore;
					if (nativeRevision == nativeBefore)
					{
						(lastClickButton, lastClickAt, lastClickX, lastClickY, lastClickCount) = clickBefore;
						positionKnown = false;
					}
				}
			}
		}

		internal void ObserveButton(MouseButton button, bool down, int eventX, int eventY, int clickCount,
			long expectedSenderRevision)
		{
			if (button == MouseButton.NoButton)
				return;

			lock (streamLock)
			{
				if (down) observedButtons |= ButtonMask(button);
				else observedButtons &= ~ButtonMask(button);
				if (senderRevision != expectedSenderRevision)
					return;
				x = eventX;
				y = eventY;
				positionKnown = true;
				if (!down)
					CompleteClick(button, eventX, eventY, clickCount);
				nativeRevision++;
			}
		}

		private int NextClickCount(MouseButton button, int eventX, int eventY)
		{
			var elapsed = timestamp() - lastClickAt;
			var continues = lastClickCount > 0 && button == lastClickButton
				&& elapsed >= 0 && elapsed <= clickIntervalTicks
				&& Math.Abs((long)eventX - lastClickX) <= clickTolerance
				&& Math.Abs((long)eventY - lastClickY) <= clickTolerance;
			return continues ? (int)Math.Min(lastClickCount + 1L, int.MaxValue) : 1;
		}

		private void CompleteClick(MouseButton button, int eventX, int eventY, int count)
		{
			lastClickButton = button;
			lastClickX = eventX;
			lastClickY = eventY;
			lastClickCount = Math.Max(1, count);
			lastClickAt = timestamp();
		}

		private static TimeSpan GetClickInterval(TimeSpan? requested)
		{
			if (requested is { } interval)
				return interval < TimeSpan.Zero ? TimeSpan.Zero : interval;
			try
			{
				var seconds = MonoMac.AppKit.NSEvent.DoubleClickInterval;
				return TimeSpan.FromSeconds(seconds > 0 && double.IsFinite(seconds) ? seconds : 0.5);
			}
			catch { return TimeSpan.FromSeconds(0.5); }
		}

		internal void ObserveMove(int positionX, int positionY, long expectedSenderRevision)
		{
			lock (streamLock)
			{
				if (senderRevision != expectedSenderRevision)
					return;
				x = positionX;
				y = positionY;
				positionKnown = true;
				nativeRevision++;
			}
		}

		internal void InvalidatePosition()
		{
			lock (streamLock)
			{
				positionKnown = false;
				senderRevision++;
			}
		}

		private bool EnsurePosition()
		{
			lock (streamLock)
			{
				if (positionKnown)
					return true;
			}

			if (!sink.TryGetCursorPosition(out var queriedX, out var queriedY))
				return false;

			lock (streamLock)
			{
				if (!positionKnown)
				{
					x = queriedX;
					y = queriedY;
					positionKnown = true;
				}
			}
			return true;
		}

		private void ResolvePosition(ref int eventX, ref int eventY)
		{
			if ((eventX == CoordUnspecified || eventY == CoordUnspecified) && EnsurePosition())
			{
				lock (streamLock)
				{
					if (eventX == CoordUnspecified) eventX = x;
					if (eventY == CoordUnspecified) eventY = y;
				}
			}

			if (eventX == CoordUnspecified) eventX = 0;
			if (eventY == CoordUnspecified) eventY = 0;
		}

		private static uint ButtonMask(MouseButton button) => 1u << ((int)button - 1);

		private static MouseButton FirstPressedButton(uint buttons)
		{
			// Quartz has dedicated drag types for primary and secondary buttons. Preserve those
			// first if multiple buttons are held; remaining buttons share otherMouseDragged.
			if ((buttons & ButtonMask(MouseButton.Button1)) != 0) return MouseButton.Button1;
			if ((buttons & ButtonMask(MouseButton.Button2)) != 0) return MouseButton.Button2;
			if ((buttons & ButtonMask(MouseButton.Button3)) != 0) return MouseButton.Button3;
			if ((buttons & ButtonMask(MouseButton.Button4)) != 0) return MouseButton.Button4;
			if ((buttons & ButtonMask(MouseButton.Button5)) != 0) return MouseButton.Button5;
			return MouseButton.NoButton;
		}
	}
}
#endif
