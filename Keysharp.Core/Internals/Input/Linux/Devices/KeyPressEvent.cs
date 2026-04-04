using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Input.Linux.Devices
{
	public readonly struct KeyPressEvent
	{
		public KeyPressEvent(EventCode code, KeyState state)
		{
			Code = code;
			State = state;
		}

		public EventCode Code { get; }

		public KeyState State { get; }
	}
}
#endif