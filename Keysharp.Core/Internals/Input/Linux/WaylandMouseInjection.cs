#if LINUX
namespace Keysharp.Internals.Input.Linux
{
	/// <summary>
	/// Shared resolver for the active compositor's mouse-injection backend, used by both Linux keyboard/mouse
	/// senders (legacy-X11 <see cref="LinuxKeyboardMouseSender"/> and <see cref="InputdKeyboardMouseSender"/>) to
	/// route synthesized mouse events through the compositor on Wayland. Returns null off-Wayland or when the
	/// active backend can't inject mouse input, so the caller falls back to SharpHook/uinput.
	///
	/// NOTE: deliberately NOT <c>Platform.Mouse</c> — that path also performs an X11 <c>XWarpPointer</c> on a
	/// non-Wayland session, which would wrongly suppress these senders' SharpHook/uinput fallback. The senders
	/// specifically want "the compositor mouse backend, or nothing".
	/// </summary>
	internal static class WaylandMouseInjection
	{
		internal static Keysharp.Internals.Window.Linux.Wayland.IWaylandBackend Backend()
		{
			if (!Platform.Desktop.IsWaylandSession)
				return null;

			var b = Keysharp.Internals.Window.Linux.Wayland.WaylandBackend.Current;
			return b?.SupportsMouse == true ? b : null;
		}
	}
}
#endif
