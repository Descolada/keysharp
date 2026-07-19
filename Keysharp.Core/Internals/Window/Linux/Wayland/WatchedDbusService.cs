#if LINUX
using Tmds.DBus;

namespace Keysharp.Internals.Window.Linux.Wayland
{
	internal sealed class DbusSession(Connection connection, string localName) : IDisposable
	{
		internal Connection Connection { get; } = connection;
		internal string LocalName { get; } = localName ?? string.Empty;
		public void Dispose() => Connection.Dispose();
	}

	/// <summary>Tracks a well-known name independently from the shared bus connection and binds calls to its owner.</summary>
	internal sealed class WatchedDbusService<TProxy> : IDisposable where TProxy : class, IDBusObject
	{
		private readonly object sync = new();
		private readonly RecoverableService<DbusSession> sessions;
		private readonly string name;
		private readonly ObjectPath path;
		private readonly int timeoutMs;
		private DbusSession session;
		private IDisposable watch;
		private string owner;
		private TProxy proxy;
		private long changes, generation;
		private bool watching, disposed;

		internal WatchedDbusService(RecoverableService<DbusSession> sessions, string name, ObjectPath path, int timeoutMs)
		{
			this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
			this.name = name ?? throw new ArgumentNullException(nameof(name));
			this.path = path;
			this.timeoutMs = Math.Max(1, timeoutMs);
		}

		internal event Action AvailabilityChanged;
		internal long Generation { get { lock (sync) return generation; } }

		internal bool HasOwner
		{
			get
			{
				using var lease = sessions.TryAcquire();
				if (lease == null || !EnsureWatch(lease.Value)) return false;
				lock (sync) return ReferenceEquals(session, lease.Value) && !string.IsNullOrEmpty(owner);
			}
		}

		internal bool TryUse<TResult>(Func<TProxy, TResult> action, out TResult result)
			=> TryUse((service, _) => action(service), out result);

		internal bool TryUse<TResult>(Func<TProxy, DbusSession, TResult> action, out TResult result)
		{
			result = default;
			if (action == null) return false;
			using var lease = sessions.TryAcquire();
			if (lease == null || !EnsureWatch(lease.Value)) return false;

			TProxy target;
			lock (sync)
			{
				if (disposed || !ReferenceEquals(session, lease.Value) || string.IsNullOrEmpty(owner)) return false;
				target = proxy ??= lease.Value.Connection.CreateProxy<TProxy>(owner, path);
			}
			result = action(target, lease.Value);
			return true;
		}

		internal void Invalidate(DbusSession failed, Exception error = null)
		{
			if (failed == null) return;
			lock (sync)
				if (ReferenceEquals(session, failed)) { owner = null; proxy = null; generation++; }
			sessions.Invalidate(failed, error);
		}

		private bool EnsureWatch(DbusSession candidate)
		{
			IDisposable old;
			lock (sync)
			{
				if (disposed) return false;
				if (ReferenceEquals(session, candidate) && watch != null) return true;
				if (watching) return false;
				watching = true;
				old = watch; watch = null; session = candidate; owner = null; proxy = null; changes = 0; generation++;
			}
			SafeDispose(old);

			IDisposable installed = null;
			Exception failure = null;
			try
			{
				var watchTask = candidate.Connection.ResolveServiceOwnerAsync(name,
					change => OwnerChanged(candidate, change.NewOwner), error => WatchFailed(candidate, error));
				if (!watchTask.WaitWithoutInterruption(timeoutMs)) throw Timeout("watching");
				installed = watchTask.GetAwaiter().GetResult();

				long before;
				lock (sync) before = changes;
				var query = candidate.Connection.ResolveServiceOwnerAsync(name);
				if (!query.WaitWithoutInterruption(timeoutMs)) throw Timeout("resolving");
				var queried = query.GetAwaiter().GetResult();
				bool notify = false;
				lock (sync)
				{
					if (disposed || !ReferenceEquals(session, candidate)) return false;
					watch = installed; installed = null;
					if (changes == before && owner != queried)
					{
						owner = queried; proxy = null; generation++; notify = true;
					}
				}
				if (notify) Notify();
				return true;
			}
			catch (Exception ex) { failure = ex; return false; }
			finally
			{
				SafeDispose(installed);
				lock (sync) watching = false;
				if (failure != null) sessions.Invalidate(candidate, failure);
			}
		}

		private TimeoutException Timeout(string operation)
			=> new($"{operation} D-Bus service '{name}' timed out after {timeoutMs} ms.");

		private void OwnerChanged(DbusSession source, string newOwner)
		{
			lock (sync)
			{
				if (disposed || !ReferenceEquals(session, source)) return;
				changes++; owner = newOwner; proxy = null; generation++;
			}
			Notify();
		}

		private void WatchFailed(DbusSession source, Exception error)
		{
			Invalidate(source, error);
			Notify();
		}

		private void Notify()
		{
			foreach (Action handler in AvailabilityChanged?.GetInvocationList() ?? [])
				try { handler(); } catch { }
		}

		private static void SafeDispose(IDisposable value) { try { value?.Dispose(); } catch { } }

		public void Dispose()
		{
			IDisposable retired;
			lock (sync)
			{
				if (disposed) return;
				disposed = true; retired = watch; watch = null; session = null; owner = null; proxy = null; generation++;
			}
			SafeDispose(retired);
		}
	}
}
#endif
