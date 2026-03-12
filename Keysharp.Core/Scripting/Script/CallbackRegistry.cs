using System.Runtime.CompilerServices;

using Keysharp.Core.Common.Invoke;

namespace Keysharp.Scripting
{
	internal sealed class CallbackRegistry<TRegistration> where TRegistration : CallbackRegistration
	{
		private readonly Lock gate = new();
		private readonly List<TRegistration> ordered = [];
		private readonly Dictionary<CallbackRegistrationKey, List<TRegistration>> byCallbackAndScheduler = [];
		private readonly Dictionary<ScriptEventScheduler, List<TRegistration>> byScheduler = new(ReferenceEqualityComparer<ScriptEventScheduler>.Instance);
		private TRegistration[] snapshot = [];
		private bool snapshotDirty = true;

		internal int Count
		{
			get
			{
				lock (gate)
					return ordered.Count;
			}
		}

		internal bool IsEmpty
		{
			get
			{
				lock (gate)
					return ordered.Count == 0;
			}
		}

		internal void Add(TRegistration registration, bool addFirst = false)
		{
			if (registration == null)
				return;

			lock (gate)
			{
				if (addFirst)
					ordered.Insert(0, registration);
				else
					ordered.Add(registration);

				IndexAdd(registration);
				snapshotDirty = true;
			}
		}

		internal TRegistration[] GetSnapshot()
		{
			lock (gate)
			{
				EnsureSnapshotLocked();
				return snapshot;
			}
		}

		internal TRegistration Find(IFuncObj callback, ScriptEventScheduler scheduler)
		{
			lock (gate)
				return byCallbackAndScheduler.TryGetValue(new CallbackRegistrationKey(callback, scheduler), out var registrations) && registrations.Count > 0
					? registrations[^1]
					: null;
		}

		internal bool Remove(IFuncObj callback, ScriptEventScheduler scheduler, bool matchScheduler = true)
		{
			lock (gate)
			{
				if (matchScheduler)
				{
					if (!byCallbackAndScheduler.TryGetValue(new CallbackRegistrationKey(callback, scheduler), out var registrations) || registrations.Count == 0)
						return false;

					return RemoveRegistrationsLocked([.. registrations]);
				}

				List<TRegistration> removals = null;

				for (var i = ordered.Count - 1; i >= 0; i--)
				{
					var registration = ordered[i];

					if (!Equals(registration.Callback, callback))
						continue;

					(removals ??= []).Add(registration);
				}

				return removals != null && RemoveRegistrationsLocked(removals);
			}
		}

		internal bool RemoveOwned(ScriptEventScheduler scheduler)
		{
			if (scheduler == null)
				return false;

			lock (gate)
			{
				if (!byScheduler.TryGetValue(scheduler, out var registrations) || registrations.Count == 0)
					return false;

				return RemoveRegistrationsLocked([.. registrations]);
			}
		}

		internal bool Remove(Predicate<TRegistration> shouldRemove)
		{
			lock (gate)
			{
				if (ordered.Count == 0)
					return false;

				var removals = new List<TRegistration>();

				for (var i = ordered.Count - 1; i >= 0; i--)
				{
					if (shouldRemove(ordered[i]))
						removals.Add(ordered[i]);
				}

				return removals.Count != 0 && RemoveRegistrationsLocked(removals);
			}
		}

		internal void Clear()
		{
			lock (gate)
			{
				foreach (var registration in ordered)
					registration.SetActive(false);

				ordered.Clear();
				byCallbackAndScheduler.Clear();
				byScheduler.Clear();
				snapshot = [];
				snapshotDirty = false;
			}
		}

		internal bool ModifyEventHandlers(IFuncObj callback, long addRemove, Func<IFuncObj, long, TRegistration> createRegistration = null, bool matchCurrentSchedulerOnRemove = true)
		{
			if (callback == null)
				return false;

			if (createRegistration == null)
			{
				if (typeof(TRegistration) != typeof(CallbackRegistration))
					throw new InvalidOperationException($"A registration factory is required for {typeof(TRegistration).Name}.");

				createRegistration = (Func<IFuncObj, long, TRegistration>)(object)CallbackRegistration.CreateCurrent;
			}

			if (addRemove > 0)
			{
				Add(createRegistration(callback, addRemove));
				return true;
			}

			if (addRemove < 0)
			{
				Add(createRegistration(callback, addRemove), true);
				return true;
			}

			return Remove(callback, matchCurrentSchedulerOnRemove ? Script.TheScript?.EventScheduler : null, matchCurrentSchedulerOnRemove);
		}

		internal bool ModifyGlobalEventHandlers(IFuncObj callback, long addRemove)
		{
			if (typeof(TRegistration) != typeof(CallbackRegistration))
				throw new InvalidOperationException($"Global callback registration is only supported for {typeof(CallbackRegistration).Name}.");

			return ModifyEventHandlers(callback, addRemove, (Func<IFuncObj, long, TRegistration>)(object)CallbackRegistration.CreateGlobal, false);
		}

		internal object InvokeEventHandlers(params object[] args)
			=> GetSnapshot().InvokeEventHandlers(args);

		internal static bool RemoveOwned<TKey>(ConcurrentDictionary<TKey, CallbackRegistry<TRegistration>> hubs, ScriptEventScheduler scheduler)
		{
			if (hubs == null || scheduler == null)
				return false;

			var removedAny = false;

			foreach (var kv in hubs.ToArray())
			{
				if (!kv.Value.RemoveOwned(scheduler))
					continue;

				removedAny = true;

				if (kv.Value.IsEmpty)
					_ = hubs.TryRemove(kv.Key, out _);
			}

			return removedAny;
		}

		private bool RemoveRegistrationsLocked(IReadOnlyCollection<TRegistration> removals)
		{
			if (removals == null || removals.Count == 0)
				return false;

			foreach (var registration in removals)
			{
				registration.SetActive(false);
				IndexRemove(registration);
			}

			if (removals.Count == 1)
			{
				foreach (var registration in removals)
					_ = ordered.Remove(registration);
			}
			else
			{
				var removalSet = new HashSet<TRegistration>(removals);
				_ = ordered.RemoveAll(removalSet.Contains);
			}

			snapshotDirty = true;
			return true;
		}

		private void EnsureSnapshotLocked()
		{
			if (!snapshotDirty)
				return;

			snapshot = ordered.Count != 0 ? [.. ordered] : [];
			snapshotDirty = false;
		}

		private void IndexAdd(TRegistration registration)
		{
			if (registration.Callback != null)
			{
				byCallbackAndScheduler.GetOrAdd(new CallbackRegistrationKey(registration.Callback, registration.OwnerScheduler), static () => []).Add(registration);
			}

			if (registration.OwnerScheduler != null)
				byScheduler.GetOrAdd(registration.OwnerScheduler, static () => []).Add(registration);
		}

		private void IndexRemove(TRegistration registration)
		{
			if (registration.Callback != null)
			{
				RemoveFromIndex(byCallbackAndScheduler, new CallbackRegistrationKey(registration.Callback, registration.OwnerScheduler), registration);
			}

			if (registration.OwnerScheduler != null)
				RemoveFromIndex(byScheduler, registration.OwnerScheduler, registration);
		}

		private static void RemoveFromIndex<TKey>(Dictionary<TKey, List<TRegistration>> index, TKey key, TRegistration registration) where TKey : notnull
		{
			if (!index.TryGetValue(key, out var registrations))
				return;

			_ = registrations.Remove(registration);

			if (registrations.Count == 0)
				_ = index.Remove(key);
		}
	}

	internal readonly record struct CallbackRegistrationKey(IFuncObj Callback, ScriptEventScheduler Scheduler)
	{
		public bool Equals(CallbackRegistrationKey other)
			=> Equals(Callback, other.Callback) && ReferenceEquals(Scheduler, other.Scheduler);

		public override int GetHashCode()
		{
			unchecked
			{
				return ((Callback?.GetHashCode() ?? 0) * 397) ^ (Scheduler != null ? RuntimeHelpers.GetHashCode(Scheduler) : 0);
			}
		}
	}

	internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
	{
		internal static readonly ReferenceEqualityComparer<T> Instance = new();

		public bool Equals(T x, T y) => ReferenceEquals(x, y);
		public int GetHashCode(T obj) => obj != null ? RuntimeHelpers.GetHashCode(obj) : 0;
	}
}
