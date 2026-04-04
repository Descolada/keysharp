using Keysharp.Builtins;
using Keysharp.Internals.Invoke;

namespace Keysharp.Internals.Scripting
{
	internal class SchedulerRegistration
	{
		private bool persistenceHeld;

		internal SchedulerRegistration(ScriptEventScheduler ownerScheduler = null, bool active = false)
			=> Set(ownerScheduler, active);

		internal ScriptEventScheduler OwnerScheduler { get; private set; }
		internal bool IsActive { get; private set; }

		internal void SetActive(bool active)
			=> Set(OwnerScheduler, active);

		internal void Set(ScriptEventScheduler ownerScheduler, bool active)
		{
			if (ReferenceEquals(OwnerScheduler, ownerScheduler) && IsActive == active)
				return;

			UpdatePersistence(false);
			OwnerScheduler = ownerScheduler;
			IsActive = active;
			UpdatePersistence(active && ownerScheduler != null);
		}

		internal void Clear()
		{
			UpdatePersistence(false);
			OwnerScheduler = null;
			IsActive = false;
		}

		private void UpdatePersistence(bool shouldHold)
		{
			if (persistenceHeld == shouldHold)
				return;

			OwnerScheduler?.AdjustPersistenceRoot(shouldHold ? 1 : -1);
			persistenceHeld = shouldHold;
		}
	}

	internal class CallbackRegistration : SchedulerRegistration
	{
		private IFuncObj callback;

		internal CallbackRegistration(IFuncObj callback = null, ScriptEventScheduler ownerScheduler = null, bool active = false)
			: base(ownerScheduler, active)
		{
			this.callback = callback;
		}

		internal static CallbackRegistration CreateCurrent(IFuncObj callback, long _)
			=> new(callback, Script.TheScript?.EventScheduler, true);

		internal static CallbackRegistration CreateGlobal(IFuncObj callback, long _)
			=> new(callback, null, true);

		internal IFuncObj Callback => callback;

		internal void Set(IFuncObj callback, ScriptEventScheduler ownerScheduler, bool active)
		{
			this.callback = callback;
			base.Set(ownerScheduler, active);
		}
	}
}
