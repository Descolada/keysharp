using System.Text.RegularExpressions;

namespace Keysharp.Builtins
{
	public class RegExMatchInfo : KeysharpObject, I__Enum, IEnumerable<(object, object)>
	{
		internal PcreMatch match;
		internal RegexHolder holder;
		public object Count => match.CaptureCount;
		public object Mark => match.Mark;
		public object Success => match.Success;
		public object pos => Pos(); //Lower-cased because of the naming conflict with the method

		public RegExMatchInfo(params object[] args) : base(args)
		{
			match = args[0] as PcreMatch;
			holder = args[1] as RegexHolder;
		}

		public static implicit operator long(RegExMatchInfo r) => r.Pos();

		public object __Get(object name, object args) => name is string s && s.ParseLong(out long l) && l >= 0 && l <= match.Groups.Count ? this[l] : this[name];

		public IFuncObj __Enum(object count) => CreateEnumerator(count.Ai());

		public IEnumerator<(object, object)> GetEnumerator() => CreateEnumerator(2);

		IEnumerator IEnumerable.GetEnumerator() => CreateEnumerator(2);

		public long get_Len(object obj = null) => Len(obj);

		public long Len(object obj)
		{
			var g = GetGroup(obj);
			return g != null && g.Success ? g.Length : 0;
		}

		public string Name(object obj)
		{
			var g = GetGroup(obj);
			return g != null && g.Success ? (obj is string o ? o : holder.groupNames[obj.Ai()]) : "";
		}

		public long get_Pos(object obj = null) => Pos(obj);

		public long Pos(object obj = null)
		{
			var g = GetGroup(obj);
			return g != null && g.Success ? g.Index + 1 : 0;
		}

		public override string ToString() => Pos().ToString();

		private PcreGroup GetGroup(object obj)
		{
			var o = obj;

			try
			{
				if (o == null)
					return match.Groups[0];
				else if (o is string s)
					return match.Groups[s];
				else
				{
					var index = Convert.ToInt32(o);

					if (index >= 0 && index <= match.Groups.Count)
						return match.Groups[index];
				}
			}
			catch (ArgumentOutOfRangeException)
			{
				return null;
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
			var iter = match.Groups.GetEnumerator();
			var index = -1;

			return new Enumerator(
					   this,
					   count,
					   () =>
			{
				index++;
				return iter.MoveNext();
			},
			() => iter.Current.Value,
			() => (holder.groupNames[index] == "" ? (long)index : holder.groupNames[index], iter.Current.Value),
			() =>
			{
				index = -1;
				iter = match.Groups.GetEnumerator();
			});
		}
	}
}
