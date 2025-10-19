#if WINDOWS
namespace Keysharp.Core.COM
{
	public unsafe class ComMethodPropertyHolder : MethodPropertyHolder
	{
		public new string Name { get; private set; }

		public ComMethodPropertyHolder(string name)
		{
			Name = name;
			_callFunc = (inst, obj) =>
			{
				var t = inst.GetType();

				object ret = null;
				if (inst is ComValue cv)
					ret = cv.RawInvokeMethod(Name, obj);
				else
					throw new Exception();

				return ret;
			};
		}
	}

	internal class ComMethodData
	{
		internal ConcurrentLfu<nint, Dictionary<string, ComMethodInfo>> comMethodCache = new (Caching.DefaultCacheCapacity);
	}

	internal class ComMethodInfo
	{
		internal Type[] expectedTypes;
		internal ParameterModifier[] modifiers;
		internal INVOKEKIND invokeKind;
	}
}

#endif