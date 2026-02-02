using System.Collections.Concurrent;

namespace Keysharp.Scripting
{
	public sealed class ModuleData
	{
		private static readonly ConcurrentDictionary<System.Type, ModuleData> cache = new ();

		internal System.Type ModuleType { get; }
		public ModuleVars Vars { get; }

		public ModuleData(System.Type moduleType)
		{
			ModuleType = moduleType ?? throw new System.ArgumentNullException(nameof(moduleType));
			Vars = new ModuleVars(ModuleType);
		}

		internal static ModuleData GetOrCreate(System.Type moduleType)
		{
			if (moduleType == null)
				return null;

			return cache.GetOrAdd(moduleType, static t => new ModuleData(t));
		}

		internal ModuleData Push(System.Type moduleType, out bool changed)
		{
			changed = false;
			var script = Script.TheScript;
			var prev = script.moduleData.Value;

			if (moduleType == null || prev.ModuleType == moduleType || !typeof(Keysharp.Core.Common.ObjectBase.Module).IsAssignableFrom(moduleType))
				return prev;

			var next = GetOrCreate(moduleType);
			if (!ReferenceEquals(prev, next))
			{
				script.moduleData.Value = next;
				changed = true;
			}

			return prev;
		}

		internal void Pop(ModuleData previous, bool changed)
		{
			if (changed)
				Script.TheScript.moduleData.Value = previous;
		}
	}

	public sealed class ModuleVars
	{
		private readonly System.Type moduleType;

		internal ModuleVars(System.Type moduleType)
		{
			this.moduleType = moduleType ?? throw new System.ArgumentNullException(nameof(moduleType));
		}

		public object this[object key]
		{
			get => TryGetPropertyValue(out object val, key, "__Value") ? val : Script.TheScript.Vars.GetVariable(moduleType, key.ToString()) ?? "";
			set => _ = (key is KeysharpObject kso && Functions.HasProp(kso, "__Value") == 1)
				? Script.SetPropertyValue(kso, "__Value", value)
				: Script.TheScript.Vars.SetVariable(moduleType, key.ToString(), value);
		}

		public bool HasVariable(object key) => Script.TheScript.Vars.HasVariable(moduleType, key.ToString());
		public object GetVariable(object key) => Script.TheScript.Vars.GetVariable(moduleType, key.ToString());
		public object SetVariable(object key, object value) => Script.TheScript.Vars.SetVariable(moduleType, key.ToString(), value);
	}
}
