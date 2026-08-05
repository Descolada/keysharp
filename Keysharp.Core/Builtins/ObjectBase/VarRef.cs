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

		/// <summary>
		/// True when this ref's <c>__Value</c> is the built-in property above, letting
		/// <c>GetPropertyValueOrNull</c>/<c>SetPropertyValue</c> read or write it directly instead of dispatching.
		/// They are the only two callers: everything else that touches a ref goes through them and inherits the
		/// shortcut, so the rule lives in one place.
		/// <para>
		/// Subclassing is the only way a script can put something else behind <c>__Value</c> -- <c>DefineProp</c> is
		/// not reachable on a ref, since VarRef derives from <see cref="Any"/> rather than Object and so has no such
		/// member -- which is why one type test answers it. A subclass declaring its own <c>__Value</c> is honoured;
		/// <see cref="Get"/>/<see cref="Set"/> are private to the C# constructors, so that is also the only shape
		/// that can work.
		/// </para>
		/// </summary>
		internal bool IsPlain => GetType() == typeof(VarRef);
	}
}
