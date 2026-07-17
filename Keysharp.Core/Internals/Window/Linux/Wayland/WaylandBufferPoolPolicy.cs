#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	internal readonly record struct WaylandBufferState(int Width, int Height, bool Released);

	/// <summary>Pure selection policy for the overlay's bounded SHM pool.</summary>
	internal static class WaylandBufferPoolPolicy
	{
		internal const int Capacity = 3;

		internal static int FindReusable(ReadOnlySpan<WaylandBufferState> buffers, int width, int height)
		{
			for (var i = 0; i < buffers.Length; i++)
				if (buffers[i].Released && buffers[i].Width == width && buffers[i].Height == height)
					return i;

			return -1;
		}

		internal static bool CanAllocate(int count) => count < Capacity;
	}
}
#endif
