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

		//May need to add wait functionality here the way AHK does in StatusBarUtil().
		protected override string GetCaption(uint index)
		{
			throw new NotImplementedException();
		}

		protected override uint GetOwningPid()
		{
			throw new NotImplementedException();
		}

		protected override int GetPanelCount()
		{
			throw new NotImplementedException();
		}
	}
}

#endif

