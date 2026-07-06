#if !WINDOWS
using System;

namespace Keysharp.Internals.Input.Unix
{
	// Key-simulation backend abstractions for Unix senders. These describe how synthesized
	// keystrokes are emitted ("Event" mode = immediate, "Input" mode = batched); they are
	// unrelated to key-code conversion (see KeyCodes).
	internal interface IKeySimulationBackend
	{
		// Event mode: send immediately.
		void KeyDown(uint vk);
		void KeyUp(uint vk);
		void KeyStroke(uint vk);

		// Input mode: batching via a platform sequence.
		IKeySimulationSequence BeginSequence();
	}

	internal interface IKeySimulationSequence : IDisposable
	{
		void AddKeyDown(uint vk);
		void AddKeyUp(uint vk);
		void AddKeyStroke(uint vk);
		void Commit(long extraInfo);
	}
}
#endif
