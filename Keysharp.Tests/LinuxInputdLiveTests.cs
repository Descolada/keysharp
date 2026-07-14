using Assert = NUnit.Framework.Legacy.ClassicAssert;

#if LINUX
using System.Collections.Concurrent;
using Keysharp.Internals.Input.Linux;
#endif

namespace Keysharp.Tests
{
	/// <summary>
	/// End-to-end tests for an installed keysharp-inputd. These tests inject real
	/// input and are deliberately excluded from ordinary test runs. Select this
	/// fixture explicitly to run it against the installed daemon.
	/// </summary>
	[Explicit("Injects real input through an installed keysharp-inputd instance.")]
	public class LinuxInputdLiveTests
	{
#if LINUX
		private const uint F13 = 0x7C;
		private const uint F14 = 0x7D;
		private const uint KeyUp = 0x80;
		private const uint Injected = 0x10;
		private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

		[Test, Category("External")]
		public void SendInsideHookIsRecursiveAndSynchronous()
		{
			using var fixture = new LiveHookFixture();
			var trace = new ConcurrentQueue<string>();
			var outerFinished = new ManualResetEventSlim();

			fixture.Callback.SetNestedHookEventHandler((rpc, hookEvent) =>
			{
				AssertSyntheticKey(hookEvent, F14);
				trace.Enqueue($"nested-{Direction(hookEvent)}");
				rpc.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Pass);
			});

			fixture.StartReader(hookEvent =>
			{
				AssertSyntheticKey(hookEvent, F13);
				trace.Enqueue($"outer-{Direction(hookEvent)}-enter");

				if ((hookEvent.Keyboard.Flags & KeyUp) == 0)
				{
					fixture.Callback.SendInput(KeyStroke(F14));
					trace.Enqueue("nested-send-returned");
				}

				fixture.Hook.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Pass);
				trace.Enqueue($"outer-{Direction(hookEvent)}-leave");

				if ((hookEvent.Keyboard.Flags & KeyUp) != 0)
					outerFinished.Set();
			});

			fixture.Sender.SendInput(KeyStroke(F13));

			Assert.IsTrue(outerFinished.Wait(TestTimeout));
			Assert.That(trace.ToArray(), Is.EqualTo(new[]
			{
				"outer-down-enter",
				"nested-down",
				"nested-up",
				"nested-send-returned",
				"outer-down-leave",
				"outer-up-enter",
				"outer-up-leave",
			}));
			fixture.ThrowReaderFailure();
		}

		[Test, Category("External")]
		public void SendWaitsForEveryHookDecision()
		{
			using var fixture = new LiveHookFixture();
			using var callbackEntered = new AutoResetEvent(false);
			using var allowDecision = new SemaphoreSlim(0);
			var callbacks = 0;
			fixture.StartReader(hookEvent =>
			{
				Interlocked.Increment(ref callbacks);
				callbackEntered.Set();
				allowDecision.Wait();
				fixture.Hook.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Pass);
			});

			var send = Task.Run(() => fixture.Sender.SendInput(KeyStroke(F13)));
			Assert.IsTrue(callbackEntered.WaitOne(TestTimeout));
			Assert.IsFalse(send.IsCompleted, "SendInput returned before the first hook decision.");
			allowDecision.Release();
			Assert.IsTrue(callbackEntered.WaitOne(TestTimeout));
			Assert.IsFalse(send.IsCompleted, "SendInput returned before the second hook decision.");
			allowDecision.Release();

			Assert.IsTrue(send.Wait(TestTimeout));
			Assert.AreEqual(2, Volatile.Read(ref callbacks));
			fixture.ThrowReaderFailure();
		}

		[Test, Category("External")]
		public void TimedOutHookFailsOpenAndIsQuarantined()
		{
			using var fixture = new LiveHookFixture();
			var quarantineSeen = new ManualResetEventSlim();
			KeysharpInputdClient.HookQuarantine quarantine = default;
			var firstEvent = 0;
			fixture.Hook.SetHookQuarantineHandler(value =>
			{
				quarantine = value;
				quarantineSeen.Set();
			});
			fixture.StartReader(hookEvent =>
			{
				if (Interlocked.Exchange(ref firstEvent, 1) != 0)
					fixture.Hook.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Pass);
			});

			fixture.Sender.SendInput(KeyStroke(F13));

			Assert.IsTrue(quarantineSeen.Wait(TestTimeout));
			Assert.AreEqual(KeysharpInputdClient.HookType.KeyboardLowLevel, quarantine.HookType);
			Assert.AreEqual(1u, quarantine.Reason);
			Assert.AreEqual(1u, quarantine.StrikeCount);
			Assert.That(quarantine.RetryAfterMs, Is.EqualTo(1000u));
			fixture.ThrowReaderFailure();
		}

		[Test, Category("External")]
		public void QuarantinedHookCanBeRearmedAndReceivesLaterInput()
		{
			using var fixture = new LiveHookFixture();
			var quarantineSeen = new ManualResetEventSlim();
			var callbacksAfterRearm = new CountdownEvent(2);
			KeysharpInputdClient.HookQuarantine quarantine = default;
			var phase = 0;
			fixture.Hook.SetHookQuarantineHandler(value =>
			{
				quarantine = value;
				quarantineSeen.Set();
			});
			fixture.StartReader(hookEvent =>
			{
				if (Volatile.Read(ref phase) == 0)
					return;

				fixture.Hook.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Pass);

				if (Volatile.Read(ref phase) == 1)
					callbacksAfterRearm.Signal();
			});

			fixture.Sender.SendInput(KeyStroke(F13));
			Assert.IsTrue(quarantineSeen.Wait(TestTimeout));
			Thread.Sleep(checked((int)quarantine.RetryAfterMs + 100));
			fixture.Callback.RearmHook(quarantine.HookType, quarantine.Generation);
			Volatile.Write(ref phase, 1);
			fixture.Sender.SendInput(KeyStroke(F13));

			Assert.IsTrue(callbacksAfterRearm.Wait(TestTimeout));
			fixture.ThrowReaderFailure();
		}

		[Test, Category("External")]
		public void NestedCrossLaneSynthesisCompletesBeforeParentCallback()
		{
			using var fixture = new LiveHookFixture(subscribeMouse: true);
			var trace = new ConcurrentQueue<string>();
			var completed = new ManualResetEventSlim();
			fixture.Callback.SetNestedHookEventHandler((rpc, hookEvent) =>
			{
				Assert.AreEqual(KeysharpInputdClient.HookType.MouseLowLevel, hookEvent.HookType);
				Assert.AreEqual(0u, hookEvent.Mouse.DeviceId);
				trace.Enqueue("nested-mouse");
				rpc.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Pass);
			});
			fixture.StartReader(hookEvent =>
			{
				if (hookEvent.HookType != KeysharpInputdClient.HookType.KeyboardLowLevel)
				{
					fixture.Hook.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Pass);
					return;
				}

				trace.Enqueue("outer-enter");
				if ((hookEvent.Keyboard.Flags & KeyUp) == 0)
				{
					fixture.Callback.SendInput([
						KeysharpInputdClient.Input.MouseEvent(0, 0, 0, KeysharpInputdClient.MouseEventFlags.Move)
					]);
					trace.Enqueue("nested-returned");
				}

				fixture.Hook.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Pass);
				if ((hookEvent.Keyboard.Flags & KeyUp) != 0)
					completed.Set();
			});

			fixture.Sender.SendInput(KeyStroke(F13));
			Assert.IsTrue(completed.Wait(TestTimeout));
			Assert.That(trace.ToArray()[..3], Is.EqualTo(new[]
			{
				"outer-enter", "nested-mouse", "nested-returned"
			}));
			fixture.ThrowReaderFailure();
		}

		[Test, Category("External")]
		public void NewestSubscriberRunsFirstAndBlockStopsOlderSubscribers()
		{
			using var older = new LiveHookFixture();
			using var newer = new LiveHookFixture();
			var olderCalled = 0;
			var newerCallbacks = new CountdownEvent(2);
			older.StartReader(hookEvent =>
			{
				Interlocked.Increment(ref olderCalled);
				older.Hook.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Pass);
			});
			newer.StartReader(hookEvent =>
			{
				newer.Hook.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Block);
				newerCallbacks.Signal();
			});

			newer.Sender.SendInput(KeyStroke(F13));

			Assert.IsTrue(newerCallbacks.Wait(TestTimeout));
			Assert.AreEqual(0, Volatile.Read(ref olderCalled),
				"A BLOCK from the newest hook must finalize the event before older hooks run.");
			newer.ThrowReaderFailure();
			older.ThrowReaderFailure();
		}

		[Test, Category("External")]
		public void ModifyAcceptsReplacementAndStopsOlderSubscribers()
		{
			using var older = new LiveHookFixture();
			using var fixture = new LiveHookFixture();
			var olderCalled = 0;
			var callbacks = new CountdownEvent(2);
			older.StartReader(hookEvent =>
			{
				Interlocked.Increment(ref olderCalled);
				older.Hook.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Pass);
			});
			fixture.StartReader(hookEvent =>
			{
				if ((hookEvent.Keyboard.Flags & KeyUp) == 0)
					fixture.Hook.SendHookDecision(hookEvent.EventId,
						KeysharpInputdClient.HookDecision.Modify,
						KeyStroke(F14));
				else
					fixture.Hook.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Block);

				callbacks.Signal();
			});

			fixture.Sender.SendInput(KeyStroke(F13));
			Assert.IsTrue(callbacks.Wait(TestTimeout));
			Assert.AreEqual(0, Volatile.Read(ref olderCalled),
				"MODIFY must finalize the event instead of passing the original to older hooks.");

			fixture.ThrowReaderFailure();
			older.ThrowReaderFailure();
		}

		[Test, Category("External")]
		public void ConcurrentSendInputBatchesDoNotInterleaveHookCallbacks()
		{
			using var fixture = new LiveHookFixture();
			var trace = new ConcurrentQueue<uint>();
			var callbacks = new CountdownEvent(4);
			fixture.StartReader(hookEvent =>
			{
				trace.Enqueue(hookEvent.Keyboard.VkCode);
				fixture.Hook.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Pass);
				callbacks.Signal();
			});
			using var secondSender = KeysharpInputdClient.Connect(
				KeysharpInputdClient.Capabilities.HookKeyboard | KeysharpInputdClient.Capabilities.SynthKeyboard);
			using var start = new ManualResetEventSlim();
			var first = Task.Run(() => { start.Wait(); fixture.Sender.SendInput(KeyStroke(F13)); });
			var second = Task.Run(() => { start.Wait(); secondSender.SendInput(KeyStroke(F14)); });

			start.Set();
			Assert.IsTrue(Task.WaitAll([first, second], TimeSpan.FromSeconds(4)));
			Assert.IsTrue(callbacks.Wait(TestTimeout));
			var observed = trace.ToArray();
			Assert.That(observed, Is.EqualTo(new[] { F13, F13, F14, F14 })
				.Or.EqualTo(new[] { F14, F14, F13, F13 }),
				"Callbacks from separate SendInput batches must be contiguous.");
			fixture.ThrowReaderFailure();
		}

		private static IReadOnlyList<KeysharpInputdClient.Input> KeyStroke(uint vk) =>
		[
			KeysharpInputdClient.Input.Key((ushort)vk),
			KeysharpInputdClient.Input.Key((ushort)vk, flags: KeysharpInputdClient.KeyEventFlags.KeyUp),
		];

		private static string Direction(KeysharpInputdClient.HookEvent hookEvent)
			=> (hookEvent.Keyboard.Flags & KeyUp) == 0 ? "down" : "up";

		private static void AssertSyntheticKey(KeysharpInputdClient.HookEvent hookEvent, uint vk)
		{
			Assert.AreEqual(KeysharpInputdClient.HookType.KeyboardLowLevel, hookEvent.HookType);
			Assert.AreEqual(vk, hookEvent.Keyboard.VkCode);
			Assert.AreEqual(0u, hookEvent.Keyboard.DeviceId, "Synthetic input must not claim a physical source device.");
			Assert.AreEqual(Injected, hookEvent.Keyboard.Flags & Injected);
		}

		private sealed class LiveHookFixture : IDisposable
		{
			private readonly CancellationTokenSource cancellation = new();
			private Task reader;
			private Exception readerFailure;

			internal KeysharpInputdClient Hook { get; }
			internal KeysharpInputdClient Callback { get; }
			internal KeysharpInputdClient Sender { get; }

			internal LiveHookFixture(bool subscribeMouse = false)
			{
				var capabilities =
					KeysharpInputdClient.Capabilities.HookKeyboard |
					KeysharpInputdClient.Capabilities.SynthKeyboard;

				if (subscribeMouse)
					capabilities |= KeysharpInputdClient.Capabilities.HookMouse |
						KeysharpInputdClient.Capabilities.SynthMouse;
				Hook = KeysharpInputdClient.Connect(capabilities, role: KeysharpInputdClient.ConnectionRole.HookStream);
				Callback = KeysharpInputdClient.Connect(capabilities,
					role: KeysharpInputdClient.ConnectionRole.CallbackRpc,
					hookSessionToken: Hook.HookSessionToken);
				Sender = KeysharpInputdClient.Connect(capabilities);
				Hook.SubscribeHook(KeysharpInputdClient.HookType.KeyboardLowLevel);

				if (subscribeMouse)
					Hook.SubscribeHook(KeysharpInputdClient.HookType.MouseLowLevel);
			}

			internal void StartReader(Action<KeysharpInputdClient.HookEvent> handler)
			{
				reader = Task.Run(() =>
				{
					try
					{
						while (!cancellation.IsCancellationRequested)
							handler(Hook.ReadHookEvent());
					}
					catch (Exception ex) when (cancellation.IsCancellationRequested)
					{
						_ = ex;
					}
					catch (Exception ex)
					{
						readerFailure = ex;
					}
				});
			}

			internal void ThrowReaderFailure()
			{
				if (readerFailure != null)
					throw new AssertionException("Hook reader failed.", readerFailure);
			}

			public void Dispose()
			{
				cancellation.Cancel();
				Hook.Dispose();
				Callback.Dispose();
				Sender.Dispose();
				try { reader?.Wait(TestTimeout); } catch { }
			}
		}
#endif
	}
}
