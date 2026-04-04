namespace Keysharp.Builtins
{
	public class RegExMatchInfoCs : KeysharpObject, I__Enum, IEnumerable<(object, object)>
	{
		private Match match;
		public object Count => match.Groups.Count;
		public object Mark => match.Groups.Count > 0 ? match.Groups[ ^ 1].Name : "";
		public object Success => match.Success;

		public RegExMatchInfoCs(params object[] args) : base(args)
		{
			match = args[0] as Match;
		}

		public static implicit operator long(RegExMatchInfoCs r) => r.Pos();


		public object __Get(object name, object args) => name is string s && s.ParseLong(out long l) && l >= 0 && l <= match.Groups.Count ? this[l] : this[name];

		public IFuncObj __Enum(object count) => CreateEnumerator(count.Ai());

		public IEnumerator<(object, object)> GetEnumerator() => CreateEnumerator(2);

		public long Len(object obj)
		{
			var g = GetGroup(obj);
			return g != null && g.Success ? g.Length : 0;
		}

		public string Name(object obj)
		{
			var g = GetGroup(obj);
			return g != null && g.Success ? g.Name : "";
		}

		public long Pos(object obj = null)
		{
			var g = GetGroup(obj);
			return g != null && g.Success ? g.Index + 1 : 0;
		}

		public override string ToString() => Pos().ToString();

		IEnumerator IEnumerable.GetEnumerator() => CreateEnumerator(2);

		private Group GetGroup(object obj)
		{
			var o = obj;

			if (o == null)
				return match;
			else if (o is string s)
				return match.Groups[s];
			else
			{
				var index = Convert.ToInt32(o);

				if (index == 0)
					return match;
				else if (index > 0 && index <= match.Groups.Count)
					return match.Groups[index];
			}

			return null;
		}

		public string this[params object[] obj]
		{
			get
			{
				var g = GetGroup(obj.Length == 0 ? null : obj[0]);
				return g != null && g.Success ? g.Value : "";
			}
		}

		private Enumerator CreateEnumerator(int count)
		{
			var iter = ((IEnumerable<Group>)match.Groups).GetEnumerator();

			return new Enumerator(
					   this,
					   count,
					   () => iter.MoveNext(),
					   () => iter.Current.Value,
					   () => (iter.Current.Name, iter.Current.Value),
					   () => iter = ((IEnumerable<Group>)match.Groups).GetEnumerator());
		}
	}
}
