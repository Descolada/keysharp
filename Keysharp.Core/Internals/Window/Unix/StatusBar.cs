using Keysharp.Builtins;
#if !WINDOWS
namespace Keysharp.Internals.Window.Unix
{
	/// <summary>
	/// Concrete implementation of StatusBar for the linux platfrom.
	/// </summary>
	internal class StatusBar : Keysharp.Internals.Window.StatusBarBase
	{
		internal StatusBar(nint hWnd)
			: base(hWnd)
		{
		}

		// Cross-process status bar inspection requires AT-SPI on Linux/macOS, which is not
		// currently wired up. These stubs report an empty bar so callers can detect absence
		// gracefully instead of crashing with NotImplementedException.
		protected override string GetCaption(uint index) => DefaultObject;

		protected override uint GetOwningPid() => 0;

		protected override int GetPanelCount() => 0;
	}
}

#endif

