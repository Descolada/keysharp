namespace Keysharp.Core.Common.ObjectBase
{
	public class VarRef : KeysharpObject
	{
		private readonly Func<KsValue> Get;
		private readonly Action<KsValue> Set;

		public static VarRef Empty = new VarRef(() => default, x => x = default);

		public VarRef(KsValue x) : base(skipLogic: true)
		{
			Get = () => x;
			Set = (value) => x = value;
		}

		public VarRef(Func<KsValue> getter, Action<KsValue> setter) : base(skipLogic: true)
		{
			Get = getter;
			Set = setter;
		}

		// Do not rename this unless also modified in the parser
		public static KsValue ConstructVarRef(KsValue x) => new VarRef(() => x, (value) => x = value);
		public static KsValue ConstructVarRef(Func<KsValue> getter, Action<KsValue> setter) => new VarRef(getter, setter);

		public KsValue __Value
		{
			get => Get();
			set => Set(value);
		}
	}
}
