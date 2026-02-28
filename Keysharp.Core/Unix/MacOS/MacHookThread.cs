#if OSX
namespace Keysharp.Core.MacOS
{
	// macOS reuses the non-Windows SharpHook pipeline from UnixHookThread.
	// X11-specific behavior remains disabled via PlatformManager.IsX11Available.
	internal sealed class MacHookThread : Keysharp.Core.Unix.UnixHookThread
	{
	}
}
#endif

