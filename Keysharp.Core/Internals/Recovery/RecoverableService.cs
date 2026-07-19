namespace Keysharp.Internals
{
	/// <summary>Owns one replaceable service. Creation is bounded and retired values outlive active borrowers.</summary>
	internal sealed class RecoverableService<T> : IDisposable where T : class
	{
		internal sealed class Entry(T value)
		{
			internal readonly T Value = value;
			internal int Borrowers;
			internal bool Retired;
		}

		internal sealed class Lease(RecoverableService<T> owner, Entry entry) : IDisposable
		{
			private RecoverableService<T> owner = owner;
			private Entry entry = entry;
			internal T Value => entry?.Value;

			public void Dispose()
			{
				var service = Interlocked.Exchange(ref owner, null);
				service?.Release(Interlocked.Exchange(ref entry, null));
			}
		}

		private readonly object sync = new();
		private readonly Func<T> factory;
		private readonly Action<T> disposer;
		private readonly TimeProvider clock;
		private readonly int maximumAttempts;
		private readonly TimeSpan initialDelay, maximumDelay;
		private Entry current;
		private Exception lastError;
		private long version, lastFailure;
		private int failures;
		private bool creating, suspended, disposed;

		internal RecoverableService(Func<T> factory, Action<T> disposer = null, TimeProvider timeProvider = null,
			int maximumAttempts = 3, TimeSpan? initialRetryDelay = null, TimeSpan? maximumRetryDelay = null)
		{
			this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
			this.disposer = disposer ?? (value => (value as IDisposable)?.Dispose());
			clock = timeProvider ?? TimeProvider.System;
			this.maximumAttempts = Math.Max(1, maximumAttempts);
			initialDelay = initialRetryDelay ?? TimeSpan.FromMilliseconds(250);
			maximumDelay = maximumRetryDelay ?? TimeSpan.FromSeconds(5);
		}

		internal Exception LastError { get { lock (sync) return lastError; } }
		internal int FailureCount { get { lock (sync) return failures; } }

		internal Lease TryAcquire()
		{
			long attemptVersion;
			lock (sync)
			{
				if (disposed || suspended || creating || failures >= maximumAttempts || Delayed()) return null;
				if (current != null) return Borrow(current);
				creating = true;
				attemptVersion = version;
			}

			T created = null;
			Exception error = null;
			try { created = factory(); } catch (Exception ex) { error = ex; }

			T discard = null;
			Lease lease = null;
			lock (sync)
			{
				creating = false;
				if (disposed || version != attemptVersion || current != null) discard = created;
				else if (created == null) Failed(error);
				else
				{
					current = new Entry(created);
					lease = Borrow(current);
					failures = 0; lastFailure = 0; lastError = null;
				}
			}
			Dispose(discard);
			return lease;
		}

		internal void Invalidate(T value, Exception error = null)
		{
			T retire = null;
			lock (sync)
			{
				if (disposed || current == null || !ReferenceEquals(current.Value, value)) return;
				retire = Retire();
				version++;
				Failed(error);
			}
			Dispose(retire);
		}

		internal void Rearm()
		{
			lock (sync)
			{
				if (disposed) return;
				version++; failures = 0; lastFailure = 0; lastError = null; suspended = false;
			}
		}

		internal void Suspend()
		{
			T retire;
			lock (sync)
			{
				if (disposed) return;
				version++; suspended = true; retire = Retire();
			}
			Dispose(retire);
		}

		private Lease Borrow(Entry entry) { entry.Borrowers++; return new Lease(this, entry); }

		private bool Delayed()
		{
			if (failures == 0 || lastFailure == 0) return false;
			var factor = 1L << Math.Min(20, failures - 1);
			var delay = TimeSpan.FromMilliseconds(Math.Min(maximumDelay.TotalMilliseconds,
				initialDelay.TotalMilliseconds * factor));
			return clock.GetElapsedTime(lastFailure, clock.GetTimestamp()) < delay;
		}

		private void Failed(Exception error)
		{
			failures = Math.Min(maximumAttempts, failures + 1);
			lastFailure = clock.GetTimestamp();
			lastError = error;
		}

		private T Retire()
		{
			var entry = current;
			current = null;
			if (entry == null) return null;
			entry.Retired = true;
			return entry.Borrowers == 0 ? entry.Value : null;
		}

		private void Release(Entry entry)
		{
			T retire = null;
			lock (sync)
				if (entry != null && --entry.Borrowers == 0 && entry.Retired) retire = entry.Value;
			Dispose(retire);
		}

		private void Dispose(T value) { try { if (value != null) disposer(value); } catch { } }

		public void Dispose()
		{
			T retire;
			lock (sync)
			{
				if (disposed) return;
				disposed = true; version++; retire = Retire();
			}
			Dispose(retire);
		}
	}
}
