namespace Keysharp.Core.Common.Window
{
	internal sealed class MsgMonitorRegistration(IFuncObj callback, int maxInstances)
	{
		internal IFuncObj Callback { get; } = callback;
		internal bool IsActive { get; private set; } = true;
		internal int InstanceCount;
		internal int MaxInstances { get; } = maxInstances;
		internal string Name => Callback.Name;

		internal void Deactivate() => IsActive = false;
	}

	internal class MsgMonitor
	{
		private readonly object gate = new();
		private readonly List<MsgMonitorRegistration> registrations = [];
		internal bool isPrefiltered = false;

		internal bool IsEmpty
		{
			get
			{
				lock (gate)
				{
					return registrations.Count == 0;
				}
			}
		}

		internal MsgMonitorRegistration[] GetRegistrationsSnapshot()
		{
			lock (gate)
			{
				return [.. registrations.Where(static r => r.IsActive)];
			}
		}

		internal void ModifyRegistration(IFuncObj funcObj, long addRemove)
		{
			lock (gate)
			{
				if (addRemove == 0)
				{
					for (var i = registrations.Count - 1; i >= 0; i--)
					{
						if (registrations[i].Name == funcObj.Name)
						{
							registrations[i].Deactivate();
							registrations.RemoveAt(i);
						}
					}

					return;
				}

				var maxInstances = Math.Clamp((int)Math.Abs(addRemove), 1, Script.maxThreadsLimit);
				var registration = new MsgMonitorRegistration(funcObj, maxInstances);

				if (addRemove > 0)
					registrations.Add(registration);
				else
					registrations.Insert(0, registration);
			}
		}
	}
}
