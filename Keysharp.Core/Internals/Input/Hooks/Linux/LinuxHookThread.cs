#if LINUX
using Keysharp.Builtins;
using Keysharp.Internals.Input.Linux;

namespace Keysharp.Internals.Input.Hooks.Linux
{
	// Linux hook extension points backed by keysharp-inputd.
	internal sealed class LinuxHookThread : UnixHookThread
	{
		protected override bool UsePlatformHotstringArming => true;

		protected override void AddPlatformHotstringArmEnds(HotstringManager hm, ReadOnlySpan<char> hsBuf, HashSet<char> ends)
		{
			inputState.AddPredictedHotstringCompletionEnds(hm, hsBuf, ends, IsHotstringWordChar);
		}

		protected override bool TryQueryModifierLRStatePlatform(out uint mods, byte[] keymapBuffer = null)
		{
			if (KeysharpInputdManager.TryGetKeyState(out mods, out var capsOn, out var numOn, out var scrollOn))
			{
				SetIndicatorSnapshot(capsOn, numOn, scrollOn);
				return true;
			}

			mods = 0u;
			return false;
		}

		protected override bool TryQueryKeyStatePlatform(uint vk, out bool isDown)
		{
			isDown = false;
			return false;
		}

		internal override bool TryGetIndicatorStates(out bool capsOn, out bool numOn, out bool scrollOn)
		{
			if (KeysharpInputdManager.TryGetKeyState(out _, out capsOn, out numOn, out scrollOn))
				return true;

			// Last resort: the snapshot (may be stale, but better than nothing).
			return base.TryGetIndicatorStates(out capsOn, out numOn, out scrollOn);
		}
	}
}
#endif
