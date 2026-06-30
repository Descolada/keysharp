namespace Keysharp.Internals
{
#if LINUX
	/// <summary>
	/// Linux input-injection transport. inputd is preferred; if its hooks are lost or its permission is denied,
	/// the transport demotes once — and stickily — to the legacy X11 (XTEST) path. The latch lives in
	/// <c>KeysharpInputdManager</c>; this is the platform-neutral read of which transport (and thus scancode
	/// space) is live.
	/// </summary>
	internal sealed class LinuxInput : IInput
	{
		public InputTransport ActiveTransport
			=> Keysharp.Internals.Input.Linux.KeysharpInputdManager.IsLegacyX11FallbackActive
				? InputTransport.LegacyX11
				: InputTransport.Inputd;

		public void Demote()
			=> Keysharp.Internals.Input.Linux.KeysharpInputdManager.ActivateLegacyX11Fallback();
	}
#elif WINDOWS
	internal sealed class WindowsInput : IInput
	{
		public InputTransport ActiveTransport => InputTransport.Windows;

		public void Demote() { }
	}
#elif OSX
	internal sealed class MacInput : IInput
	{
		public InputTransport ActiveTransport => InputTransport.Mac;

		public void Demote() { }
	}
#endif
}
