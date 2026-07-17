#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	internal static class WaylandRetryPolicy
	{
		internal static int DelayMilliseconds(int consecutiveFailures)
			=> Math.Min(5000, 100 << Math.Clamp(consecutiveFailures, 0, 8));
	}
}
#endif
