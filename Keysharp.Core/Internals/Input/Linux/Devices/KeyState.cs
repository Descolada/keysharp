using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Input.Linux.Devices
{
	public enum KeyState
	{
		KeyUp,
		KeyDown,
		KeyHold
	}
}
#endif