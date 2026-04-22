using Keysharp.Runtime;

namespace Keysharp.Builtins
{
	/// <summary>
	/// A virtual reference to a property (or indexed property) of an object,
	/// produced by the AHK v2.1 reference operator applied to a member access,
	/// e.g. <c>&amp;obj.prop</c> or <c>&amp;obj[i]</c>.
	///
	/// The Keysharp variant extends the AHK v2.1 spec by accepting variadic args
	/// so that <c>&amp;obj.prop[a, b]</c> and <c>&amp;obj[a, b]</c> can be represented.
	/// When args are provided for a property reference, the constructor applies the
	/// same fallback rule as <see cref="Script.GetPropertyValue(object, object, object[])"/>:
	/// if the property does not accept parameters, the reference is rebound to the
	/// current property's <c>__Item</c> target. The resolved target, property name and
	/// args are then replayed on every <c>__Value</c> get/set.
	/// </summary>
	public class PropRef : VarRef
	{
		public object Target { get; private set; }
		public object Name { get; private set; }
		public object[] Args { get; private set; } = null;

		public PropRef() : base() { }

		public override object __New(params object[] args)
		{
			if (args == null || args.Length < 2)
				throw new System.ArgumentException("PropRef requires at least (target, name).");

			var target = args[0];
			var name = args[1];
			var refArgs = args.Length > 2 ? args[2..] : [];
			var usesIndexAccess = TryResolveIndexTarget(ref target, name, refArgs);

			Target = target;
			Name = name;
			Args = refArgs;

			if (usesIndexAccess)
			{
				Get = () => Script.GetIndex(Target, Args);
				Set = v => _ = Script.SetObject(Target, [.. Args, v]);
			}
			else
			{
				Get = () => Script.GetPropertyValue(Target, Name, Args);
				Set = v => Script.SetPropertyValue(Target, Name, [.. Args, v]);
			}

			return DefaultObject;
		}

		public static object Call(object @this, params object[] args) => @this is Class cls ? cls.Call(args) : Errors.TypeErrorOccurred(@this, typeof(Class));

		private static bool TryResolveIndexTarget(ref object target, object name, object[] args)
		{
			if (args == null || args.Length == 0)
				return false;

			var nameStr = name.ToString();

			if (nameStr.Equals("__Item", StringComparison.OrdinalIgnoreCase))
				return true;

			if (target is not KeysharpObject kso)
				return false;

			if (!Script.TryGetOwnPropsMap(kso, nameStr, out var opm))
				return false;

			if (opm.Value != null)
			{
				target = opm.Value;
				return true;
			}

			if (opm.Get is FuncObj ifo && opm.NoParamGet)
			{
				target = ifo.Call(target);
				return true;
			}

			return false;
		}
	}
}
