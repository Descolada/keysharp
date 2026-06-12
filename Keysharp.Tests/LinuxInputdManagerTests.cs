using Assert = NUnit.Framework.Legacy.ClassicAssert;

#if LINUX
using Keysharp.Internals.Input.Linux;
using System.Diagnostics;
using System.Threading;
#endif

namespace Keysharp.Tests
{
	public class LinuxInputdManagerTests
	{
#if LINUX
		[Test, Category("Misc")]
		public void HookCapabilityRequestIncludesSynthesis()
		{
			var requested = KeysharpInputdClient.Capabilities.HookKeyboard;
			var expanded = KeysharpInputdManager.ExpandInputPermissionRequest(requested);

			Assert.IsTrue((expanded & KeysharpInputdClient.Capabilities.HookKeyboard) != 0);
			Assert.IsTrue((expanded & KeysharpInputdClient.Capabilities.HookMouse) != 0);
			Assert.IsTrue((expanded & KeysharpInputdClient.Capabilities.SynthKeyboard) != 0);
			Assert.IsTrue((expanded & KeysharpInputdClient.Capabilities.SynthMouse) != 0);
		}

		[Test, Category("Misc")]
		public void SynthesisCapabilityRequestDoesNotIncludeHooks()
		{
			var requested = KeysharpInputdClient.Capabilities.SynthKeyboard;
			var expanded = KeysharpInputdManager.ExpandInputPermissionRequest(requested);

			Assert.IsFalse((expanded & KeysharpInputdClient.Capabilities.HookKeyboard) != 0);
			Assert.IsFalse((expanded & KeysharpInputdClient.Capabilities.HookMouse) != 0);
			Assert.IsTrue((expanded & KeysharpInputdClient.Capabilities.SynthKeyboard) != 0);
			Assert.IsTrue((expanded & KeysharpInputdClient.Capabilities.SynthMouse) != 0);
		}

		[Test, Category("Misc")]
		public void CallbackGateFailsOpenInsteadOfWaitingBehindStalledLane()
		{
			var gate = new InputdCallbackGate();
			using var entered = new ManualResetEventSlim();
			using var release = new ManualResetEventSlim();
			var owner = Task.Run(() =>
			{
				Assert.IsTrue(gate.TryEnter(1000, out var lease));

				using (lease)
				{
					entered.Set();
					release.Wait();
				}
			});

			try
			{
				Assert.IsTrue(entered.Wait(1000), "Callback owner did not acquire the gate.");
				var stopwatch = Stopwatch.StartNew();
				Assert.IsFalse(gate.TryEnter(50, out _));
				stopwatch.Stop();
				Assert.Less(stopwatch.ElapsedMilliseconds, 500, "Competing callback waited too long.");
			}
			finally
			{
				release.Set();
				Assert.IsTrue(owner.Wait(1000), "Callback owner did not exit.");
			}
		}

		[Test, Category("Misc")]
		public void CallbackGateCanBeAcquiredAfterOwnerExits()
		{
			var gate = new InputdCallbackGate();
			Assert.IsTrue(gate.TryEnter(0, out var first));
			first.Dispose();
			Assert.IsTrue(gate.TryEnter(0, out var second));
			second.Dispose();
		}
#endif
	}
}
