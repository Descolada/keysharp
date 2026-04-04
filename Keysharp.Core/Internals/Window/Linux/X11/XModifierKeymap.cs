using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Window.Linux.X11
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct XModifierKeymap
	{
		internal int max_keypermod;
		internal nint modifiermap;
	}
}
#endif