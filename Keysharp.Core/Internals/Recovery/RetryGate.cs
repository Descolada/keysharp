namespace Keysharp.Internals
{
	/// <summary>
	/// Serializes a bounded burst of attempts for adapters that cannot expose their resource through
	/// <see cref="RecoverableService{T}"/>. A failed burst stays quiet until an authoritative signal calls
	/// <see cref="Rearm"/>; <see cref="Suspend"/> represents a known-stable absence.
	/// </summary>
	internal sealed class RetryGate
	{
		internal sealed class Attempt : IDisposable
		{
			private RetryGate owner;
			private readonly long version;
			private bool succeeded;
			private Exception error;

			internal Attempt(RetryGate owner, long version)
			{
				this.owner = owner;
				this.version = version;
			}

			internal void Succeed() => succeeded = true;

			internal void Fail(Exception exception = null)
			{
				error = exception;
				succeeded = false;
			}

			public void Dispose()
			{
				var gate = Interlocked.Exchange(ref owner, null);
				gate?.Complete(version, succeeded, error);
			}
		}

		private readonly object sync = new();
		private readonly TimeProvider timeProvider;
		private readonly int maximumAttempts;
		private readonly TimeSpan initialRetryDelay;
		private readonly TimeSpan maximumRetryDelay;
		private long version;
		private long lastFailureTimestamp;
		private int failures;
		private bool attempting;
		private bool suspended;

		internal RetryGate(TimeProvider timeProvider = null, int maximumAttempts = 3,
			TimeSpan? initialRetryDelay = null, TimeSpan? maximumRetryDelay = null)
		{
			this.timeProvider = timeProvider ?? TimeProvider.System;
			this.maximumAttempts = Math.Max(1, maximumAttempts);
			this.initialRetryDelay = initialRetryDelay ?? TimeSpan.FromMilliseconds(250);
			this.maximumRetryDelay = maximumRetryDelay ?? TimeSpan.FromSeconds(5);
		}

		internal int FailureCount
		{
			get { lock (sync) return failures; }
		}

		internal Attempt TryBegin()
		{
			lock (sync)
			{
				if (attempting || suspended || failures >= maximumAttempts || RetryDelayRemaining())
					return null;

				attempting = true;
				return new Attempt(this, version);
			}
		}

		internal void Rearm()
		{
			lock (sync)
			{
				version++;
				attempting = false;
				failures = 0;
				lastFailureTimestamp = 0;
				suspended = false;
			}
		}

		internal void Suspend()
		{
			lock (sync)
			{
				version++;
				attempting = false;
				suspended = true;
			}
		}

		private bool RetryDelayRemaining()
		{
			if (failures == 0 || lastFailureTimestamp == 0)
				return false;

			var shift = Math.Min(20, failures - 1);
			var delayMs = Math.Min(maximumRetryDelay.TotalMilliseconds,
				initialRetryDelay.TotalMilliseconds * (1L << shift));
			return timeProvider.GetElapsedTime(lastFailureTimestamp, timeProvider.GetTimestamp())
				< TimeSpan.FromMilliseconds(delayMs);
		}

		private void Complete(long attemptVersion, bool succeeded, Exception error)
		{
			lock (sync)
			{
				if (attemptVersion != version || !attempting)
					return;

				attempting = false;

				if (succeeded)
				{
					failures = 0;
					lastFailureTimestamp = 0;
				}
				else
				{
					failures = Math.Min(maximumAttempts, failures + 1);
					lastFailureTimestamp = timeProvider.GetTimestamp();
				}
			}
		}
	}
}
