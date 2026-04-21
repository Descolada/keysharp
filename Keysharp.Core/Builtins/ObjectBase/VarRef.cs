namespace Keysharp.Builtins
{
	public class VarRef : Any
	{
		protected Func<object> Get;
		protected Action<object> Set;

		public static VarRef Empty = new VarRef(() => null, x => x = null);

		protected VarRef() : base(null) { }

		public VarRef(object x) : base(null)
		{
			Get = () => x;
			Set = (value) => x = value;
		}

		public VarRef(Func<object> getter, Action<object> setter) : base()
		{
			Get = getter;
			Set = setter;
		}

		public object __Value
		{
			get => Get();
			set => Set(value);
		}
	}
}
