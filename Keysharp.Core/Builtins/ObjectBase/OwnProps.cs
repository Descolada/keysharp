namespace Keysharp.Builtins
{
	internal static class OwnPropsEnumeration
	{
		internal static Enumerator CreateEnumerator(object obj, Dictionary<object, object> map, bool getVal)
		{
			var iter = map.GetEnumerator();

			return new Enumerator(
					   obj,
					   getVal ? 2 : 1,
					   () => iter.MoveNext(),
					   () => iter.Current.Key,
					   () => GetCurrent(obj, iter.Current),
					   () => iter = map.GetEnumerator());
		}

		private static (object, object) GetCurrent(object obj, KeyValuePair<object, object> kv)
		{
			if (kv.Value is OwnPropsDesc op)
			{
				if (op.Value != null)
					return (kv.Key, op.Value);
				else if (op.Get is FuncObj fo)
					return (kv.Key, fo.Call(obj));
				else if (op.Call != null)
					return (kv.Key, op.Call);
			}

			if (kv.Value is MethodPropertyHolder mph)
				return (kv.Key, mph.CallFunc(obj, null));
			else if (kv.Value is FuncObj fo)//ParamLength was verified when this was created in OwnProps().
				return (kv.Key, fo.Call(obj));
			else
				return (kv.Key, kv.Value);
		}
	}

	public class OwnPropsDesc
	{
		public Any Parent { get; private set; }
		public object Value;
		public object Get;
		public object Set;
		public object Call;

		internal OwnPropsMapType Type
		{
			get
			{
				var desc = OwnPropsMapType.None;
				if (Value != null) desc |= OwnPropsMapType.Value;
				if (Get != null) desc |= OwnPropsMapType.Get;
				if (Set != null) desc |= OwnPropsMapType.Set;
				if (Call != null) desc |= OwnPropsMapType.Call;
				return desc;
			}
		}

		public OwnPropsDesc()
		{
			Parent = null;
		}

		public OwnPropsDesc(Any kso, object set_Value = null, object set_Get = null, object set_Set = null, object set_Call = null)
		{
			Parent = kso;
			Value = set_Value;
			Get = set_Get;
			Set = set_Set;
			Call = set_Call;
		}


		public OwnPropsDesc(Any kso, Map map)
		{
			Parent = kso;
			Merge(map);
		}

		public bool IsEmpty
		{
			get => Value == null && Get == null && Set == null && Call == null;
		}

		internal void Merge(Dictionary<string, OwnPropsDesc> map)
		{
			foreach ((var key, var desc) in map)
			{
				switch (key.ToUpper())
				{
					case "VALUE":
						Value = desc.Value;
						Get = null;
						Set = null;
						Call = null;
						break;

					case "GET":
						Get = desc.Get;
						Value = null;
						break;

					case "SET":
						Set = desc.Set;
						Value = null;
						break;

					case "CALL":
						Call = desc.Call;
						Value = null;
						break;
				}
			}
		}

		internal void MergeOwnPropsValues(Dictionary<string, OwnPropsDesc> map)
		{
			foreach ((var name, var desc) in map)
			{
				switch (name.ToUpper())
				{
					case "VALUE":
						Value = desc.Get ?? desc.Value;
						Get = null;
						Set = null;
						Call = null;
						break;

					case "GET":
						Get = desc.Get ?? desc.Value;
						Value = null;
						break;

					case "SET":
						Set = desc.Get ?? desc.Value;
						Value = null;
						break;

					case "CALL":
						Call = desc.Get ?? desc.Value;
						Value = null;
						break;
				}
			}
		}

		internal void Merge(OwnPropsDesc opd)
		{
			if (opd.Value != null)
				Value = opd.Value;

			if (opd.Get != null)
				Get = opd.Get;

			if (opd.Set != null)
				Set = opd.Set;

			if (opd.Call != null)
				Call = opd.Call;
		}

		public void Merge(Map map)
		{
			foreach ((var key, var value) in map.map)
			{
				switch (key.ToString().ToUpper())
				{
					case "VALUE":
						Value = value;
						Get = null;
						Set = null;
						Call = null;
						break;

					case "GET":
						Get = value;
						Value = null;
						break;

					case "SET":
						Set = value;
						Value = null;
						break;

					case "CALL":
						Call = value;
						Value = null;
						break;
				}
			}
		}

		public KeysharpObject GetDesc()
		{
			var map = new KeysharpObject();
			map.EnsureOwnProps();

			if (Value != null)
				map.DefinePropInternal("value", new OwnPropsDesc(map, Value));

			if (Get != null)
				map.DefinePropInternal("get", new OwnPropsDesc(map, Get));

			if (Set != null)
				map.DefinePropInternal("set", new OwnPropsDesc(map, Set));

			if (Call != null)
				map.DefinePropInternal("call", new OwnPropsDesc(map, Call));

			return map;
		}

		public OwnPropsDesc Clone() => (OwnPropsDesc)MemberwiseClone();
	}
}

