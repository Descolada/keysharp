namespace Keysharp.Internals
{
	internal static partial class Platform
	{
		/// <summary>Process/thread + icon-resource primitives. Compile-time per-OS.</summary>
		internal static partial class Process
		{
#if WINDOWS
			public static uint CurrentThreadId() => Os.Windows.WindowsAPI.GetCurrentThreadId();

			public static bool DestroyIcon(nint icon) => Os.Windows.WindowsAPI.DestroyIcon(icon);
#elif OSX
			[LibraryImport("libSystem.dylib")]
			private static partial int pthread_threadid_np(IntPtr thread, out ulong threadid);

			public static uint CurrentThreadId()
			{
				_ = pthread_threadid_np(IntPtr.Zero, out var tid);
				return (uint)tid;
			}

			public static bool DestroyIcon(nint icon) => true;
#else
			public static uint CurrentThreadId() => (uint)Keysharp.Internals.Window.Linux.X11.Xlib.gettid();

			public static bool DestroyIcon(nint icon) => Keysharp.Internals.Window.Linux.X11.Xlib.GdipDisposeImage(icon) == 0;
#endif
		}
	}
}
