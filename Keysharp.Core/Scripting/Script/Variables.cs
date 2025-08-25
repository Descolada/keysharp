namespace Keysharp.Scripting
{
	public class Variables
	{
		internal DateTime startTime = DateTime.UtcNow;
		private readonly Dictionary<string, MemberInfo> globalVars = new (StringComparer.OrdinalIgnoreCase);

		public Variables()
		{
			var stack = new StackTrace(false).GetFrames();

			for (var i = stack.Length - 1; i >= 0; i--)
			{
				var type = stack[i].GetMethod().DeclaringType;

				if (type != null && type.FullName.StartsWith("Keysharp.CompiledMain", StringComparison.OrdinalIgnoreCase))
				{
					var fields = type.GetFields(BindingFlags.Static |
												BindingFlags.NonPublic |
												BindingFlags.Public);
					var props = type.GetProperties(BindingFlags.Static |
												   BindingFlags.NonPublic |
												   BindingFlags.Public);
					_ = globalVars.EnsureCapacity(fields.Length + props.Length);

					foreach (var field in fields)
						globalVars[field.Name] = field;

					foreach (var prop in props)
						globalVars[prop.Name] = prop;

					break;
				}
			}
		}

		public void ClearAllObjectVariables()
		{
			if (globalVars.Count == 0)
				return;

			var mainType = globalVars.First().Value.DeclaringType;

			foreach (var (name, member) in globalVars)
			{
				if (name.StartsWith("_ks_"))
					continue;
				if (member is FieldInfo fi && fi.GetValue(null) is Any)
					fi.SetValue(null, null);
				else if (member is PropertyInfo pi && pi.GetValue(null) is Any)
					pi.SetValue(null, null);
			}

			foreach (var nested in Reflections.GetNestedTypes([mainType]))
			{
				if (nested == mainType)
					continue;

				var fields = nested.GetFields(BindingFlags.Static |
					BindingFlags.NonPublic |
					BindingFlags.Public);
				var props = nested.GetProperties(BindingFlags.Static |
					BindingFlags.NonPublic |
					BindingFlags.Public);

				foreach (var field in fields)
				{
					if (field.Name.StartsWith("_ks_"))
						continue;
					if (field.GetValue(null) is Any)
						field.SetValue(null, null);
				}

				foreach (var prop in props)
				{
					if (prop.Name.StartsWith("_ks_"))
						continue;
					if (prop.GetValue(null) is Any)
						prop.SetValue(null, null);
				}
			}
		}

		public object GetVariable(string key)
		{
			if (globalVars.TryGetValue(key, out var field))
			{
				if (field is PropertyInfo pi)
					return pi.GetValue(null);
				else if (field is FieldInfo fi)
					return fi.GetValue(null);
			}

			return GetReservedVariable(key);//Last, try reserved variable.
		}

		public object SetVariable(string key, object value)
		{
			if (globalVars.TryGetValue(key, out var field))
			{
				if (field is PropertyInfo pi)
					pi.SetValue(null, value);
				else if (field is FieldInfo fi)
					fi.SetValue(null, value);
			}
			else
				_ = SetReservedVariable(key, value);

			return value;
		}

		private PropertyInfo FindReservedVariable(string name)
		{
			_ = Script.TheScript.ReflectionsData.flatPublicStaticProperties.TryGetValue(name, out var prop);
			return prop;
		}

		private object GetReservedVariable(string name)
		{
			var prop = FindReservedVariable(name);
			return prop == null || !prop.CanRead ? null : prop.GetValue(null);
		}

		private bool SetReservedVariable(string name, object value)
		{
			var prop = FindReservedVariable(name);
			var set = prop != null && prop.CanWrite;

			if (set)
			{
				value = Script.ForceType(prop.PropertyType, value);
				prop.SetValue(null, value);
			}

			return set;
		}

		public object this[object key]
		{
			get => GetVariable(key.ToString()) ?? "";
			set => _ = SetVariable(key.ToString(), value);
		}
	}
}