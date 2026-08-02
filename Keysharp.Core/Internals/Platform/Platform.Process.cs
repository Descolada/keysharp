namespace Keysharp.Internals
{
	internal static partial class Platform
	{
		/// <summary>Process/thread + icon-resource primitives. Compile-time per-OS.</summary>
		internal static partial class Process
		{
			/// <summary><paramref name="Started"/> separates "the executable isn't there" from "it ran and failed",
			/// which callers on optional helper tools need to tell apart.</summary>
			internal readonly record struct CommandResult(int ExitCode, string StandardOutput, string StandardError, bool Started = true)
			{
				internal bool Succeeded => Started && ExitCode == 0;

				internal string ErrorMessage => !string.IsNullOrWhiteSpace(StandardError)
					? StandardError.Trim()
					: !string.IsNullOrWhiteSpace(StandardOutput)
						? StandardOutput.Trim()
						: $"Process exited with code {ExitCode}.";
			}

			/// <summary>Runs an executable directly, without a command shell, and captures both output streams.</summary>
			internal static CommandResult RunCommand(string fileName, params string[] arguments)
			{
				using var process = new System.Diagnostics.Process
				{
					StartInfo = new ProcessStartInfo
					{
						FileName = fileName,
						RedirectStandardOutput = true,
						RedirectStandardError = true,
						UseShellExecute = false,
						CreateNoWindow = true
					}
				};

				foreach (var argument in arguments)
					process.StartInfo.ArgumentList.Add(argument);

				try
				{
					if (!process.Start())
						return new(-1, string.Empty, $"Failed to start {fileName}.", false);
				}
				catch (Exception ex)   // Win32Exception when the executable isn't installed, and friends.
				{
					return new(-1, string.Empty, ex.Message, false);
				}

				try
				{
					// Both streams are drained concurrently before waiting, or a child that fills one pipe's
					// buffer would block forever while we wait for it to exit.
					var outputTask = process.StandardOutput.ReadToEndAsync();
					var errorTask = process.StandardError.ReadToEndAsync();
					process.WaitForExit();
					return new(process.ExitCode, outputTask.GetAwaiter().GetResult(), errorTask.GetAwaiter().GetResult());
				}
				catch (Exception ex)
				{
					return new(-1, string.Empty, ex.Message);
				}
			}

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
