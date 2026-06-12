using System;
using System.Threading;

namespace Keysharp.Internals.Input.Linux
{
	/// <summary>
	/// Serializes the shared HookThread state transition without allowing one
	/// inputd lane to wait indefinitely behind the other.
	/// </summary>
	internal sealed class InputdCallbackGate
	{
		private readonly object sync = new();

		internal bool TryEnter(int millisecondsTimeout, out Lease lease)
		{
			if (Monitor.TryEnter(sync, millisecondsTimeout))
			{
				lease = new Lease(sync);
				return true;
			}

			lease = default;
			return false;
		}

		internal readonly struct Lease : IDisposable
		{
			private readonly object sync;

			internal Lease(object sync) => this.sync = sync;

			public void Dispose()
			{
				if (sync != null)
					Monitor.Exit(sync);
			}
		}
	}
}
