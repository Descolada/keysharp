namespace Keysharp.Internals
{
	internal static partial class Platform
	{
		/// <summary>Native library loading + DLL search-path management. Compile-time per-OS.</summary>
		internal static class Library
		{
#if WINDOWS
			// The Win32 loader only accepts '\' path separators (a '/' yields ERROR_MOD_NOT_FOUND, including when
			// resolving a DLL's sibling dependencies), so normalize any '/' first. Allocates only when present.
			public static nint LoadLibrary(string path) => Os.Windows.WindowsAPI.LoadLibrary(path != null && path.IndexOf('/') >= 0 ? path.Replace('/', '\\') : path);

			public static bool SetDllDirectory(string path) => Os.Windows.WindowsAPI.SetDllDirectory(path);
#else
			public static nint LoadLibrary(string path) => NativeLibrary.TryLoad(path, out var module) ? module : 0;

			public static bool SetDllDirectory(string path)
			{
				var pathVariableName =
#if OSX
					"DYLD_LIBRARY_PATH";
#else
					"LD_LIBRARY_PATH";
#endif

				if (path == null)
				{
					Environment.SetEnvironmentVariable(pathVariableName, Script.TheScript.ldLibraryPath);
					return Environment.GetEnvironmentVariable(pathVariableName) == Script.TheScript.ldLibraryPath;
				}
				else
				{
					var append = path;
					var orig = Environment.GetEnvironmentVariable(pathVariableName) ?? "";
					var newPath = "";

					if (orig != "")
					{
						append = ":" + append;
						newPath = orig + append;
					}
					else
						newPath = append;

					Environment.SetEnvironmentVariable(pathVariableName, newPath);
					return Environment.GetEnvironmentVariable(pathVariableName).EndsWith(append);
				}
			}
#endif
		}
	}
}
