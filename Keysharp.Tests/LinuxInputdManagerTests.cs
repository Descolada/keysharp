using Assert = NUnit.Framework.Legacy.ClassicAssert;

#if LINUX
using Keysharp.Internals.Input.Linux;
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
#endif
	}
}
