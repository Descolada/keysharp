namespace Keysharp.Core.Common.Invoke
{
	public interface IPointable
	{
		public long Ptr { get; }
	}

	public interface IFuncObj
	{
		public object Inst { get; }
		public bool IsBuiltIn { get; }
		public bool IsValid { get; }
		public string Name { get; }

		public IFuncObj Bind(params object[] obj);

		public object Call(params object[] obj);

		public object CallWithRefs(params object[] obj);

		public bool IsByRef(object obj = null);

		public bool IsOptional(object obj = null);
	}

	internal class BoundFunc : FuncObj
	{
		internal object[] boundargs;

		public new (Type, object) super => (typeof(FuncObj), this);

		internal BoundFunc(MethodInfo m, object[] ba, object o = null)
			: base(m, o)
		{
			boundargs = ba;
			int argCount = ba.Length;
			// Find last non-null argument which determines the actual provided argument count
			for (; argCount > 0; argCount--)
			{
				if (ba[argCount - 1] != null)
					break;
			}
			if (argCount < ba.Length)
				System.Array.Resize(ref boundargs, argCount);


			if (argCount > MaxParams && !IsVariadic)
				throw new Error("Too many arguments bound to function");

			// Now calculate the new MinParams/MaxParams
			int minParams = (int)MinParams;
			int maxParams = (int)MaxParams;
			for (int i = 0; i < argCount && i < maxParams; i++)
			{
				// Empty slots do not change the counts
				if (ba[i] == null)
					continue;
				// If the index is greater than minimum param count then only MaxParams can be decreased.
				// We don't overflow into the variadic parameter because of the maxParams check above.
				if (i < minParams)
				{
					if (MinParams > 0)
						MinParams--;
				}
				if (MaxParams > 0)
					MaxParams--;
			}
		}

		public override IFuncObj Bind(params object[] args)
		{
			object[] newbound = new object[boundargs.Length + args.Length];
			System.Array.Copy(boundargs, newbound, boundargs.Length);
			int skipped = 0;
			for (int i = 0; i < boundargs.Length && skipped < args.Length; i++)
			{
				if (newbound[i] == null)
				{
					newbound[i] = args[skipped];
					skipped++;
				}
			}
			int leftCount = args.Length - skipped;
			if (leftCount > 0)
			{
				System.Array.Copy(args, args.Length - leftCount, newbound, boundargs.Length, leftCount);
			}
			return new BoundFunc(mi, newbound, Inst);
		}

		/// <summary>
		/// Add the bound arguments and call the target function.
		/// </summary>
		/// <param name="args">Forwarded on to <see cref="CallWithRefs(object[])"/> or bound
		/// the arguments and forwarded to <see cref="FuncObj.Call(object[])"/></param>
		/// <returns>The return value of the bound function.</returns>
		public override object Call(params object[] args) {
			if (AnyRef)
				return CallWithRefs(args);
			return base.Call(CreateArgs(args));
		}

		public override object CallWithRefs(params object[] args) => base.CallWithRefs(CreateArgs(args));

		private object[] CreateArgs(params object[] args)
		{
			var boundLen = boundargs.Length;
			var paramLen = boundLen + args.Length;
			// This may allocate more than needed, but MPH will take care of that
			var newArgs = new object[paramLen];

			int a = 0;

			// Fill fixed parameters in one pass
			for (int i = 0; i < paramLen; i++)
			{
				var b = i < boundLen ? boundargs[i] : null;

				if (b != null)
					newArgs[i] = b;
				else if (a < args.Length)
					newArgs[i] = args[a++];
				else
					newArgs[i] = null; // MPH will handle defaults
			}

			// MPH will handle variadic packing

			return newArgs;
		}
	}

	internal class FuncObj : KeysharpObject, IFuncObj
	{
		protected MethodInfo mi;
		protected MethodPropertyHolder mph;

		[PublicForTestOnly]
		public object Inst { get; internal set; }
		public bool IsBuiltIn => mi.DeclaringType.Module.Name.StartsWith("keysharp.core", StringComparison.OrdinalIgnoreCase);
		public bool IsValid => mi != null && mph != null && mph.IsCallFuncValid;
		public string Name => mi != null ? mi.Name : "";
		public new (Type, object) super => (typeof(KeysharpObject), this);
		public bool IsVariadic => mph.variadicParamIndex != -1;
		public long MaxParams { get; internal set; } = 0;
		public long MinParams { get; internal set; } = 0;
		internal int VariadicIndex => mph.variadicParamIndex;
		internal bool AnyRef => mph.byRefIndices != null;
		internal List<int> ByRefIndices => mph.byRefIndices;
		internal MethodPropertyHolder Mph => mph;

		internal FuncObj(string s, object o = null, object paramCount = null)
			: this(o != null ? Reflections.FindAndCacheMethod(o.GetType(), s, paramCount.Ai(-1)) : Reflections.FindMethod(s, paramCount.Ai(-1)), o)
		{
		}

		internal FuncObj(MethodPropertyHolder m, object o = null)
			: this(m?.mi, o)
		{
		}

		internal FuncObj(Delegate d, object o = null)
			: this(d.Method, o)
		{
		}

		internal FuncObj(MethodInfo m, object o = null)
		{
			mi = m;
			Inst = o;

			if (mi != null)
				Init();
		}

		public virtual IFuncObj Bind(params object[] args)
		=> new BoundFunc(mi, args, Inst);

		public virtual object Call(params object[] args) => mph.CallFunc(Inst, args);

		public virtual object CallWithRefs(params object[] args)
		{
			var refs = new List<RefHolder>(ByRefIndices.Count);

			foreach (int i in ByRefIndices)
			{
				var rh = args[i] as RefHolder;
				refs.Add(rh);
				if (rh != null)
					args[i] = rh.val;
			}

			var val = mph.CallFunc(Inst, args);

			int r = -1;
			foreach (int i in ByRefIndices)
			{
				if (refs[++r] != null)
					refs[r].reassign(args[i]);
			}

			return val;
		}

		public override bool Equals(object obj) => obj is FuncObj fo ? fo.mi == mi : false;

		public bool Equals(FuncObj value) => value.mi == mi;

		public override int GetHashCode() => mi.GetHashCode();

		public bool IsByRef(object obj = null)
		{
			var index = obj.Ai();
			var funcParams = mi.GetParameters();

			if (index > 0)
			{
				index--;

				if (index < funcParams.Length)
					return funcParams[index].ParameterType.IsByRef;
			}
			else
			{
				for (var i = 0; i < funcParams.Length; i++)
					if (funcParams[i].ParameterType.IsByRef)
						return true;
			}

			return false;
		}

		public bool IsOptional(object obj = null)
		{
			var index = obj.Ai();
			var funcParams = mi.GetParameters();

			if (index > 0)
			{
				index--;

				if (index < funcParams.Length)
					return funcParams[index].IsOptional;
			}
			else
			{
				for (var i = 0; i < funcParams.Length; i++)
					if (funcParams[i].IsOptional)
						return true;
			}

			return false;
		}

		private void Init()
		{
			mph = MethodPropertyHolder.GetOrAdd(mi);
			var parameters = mph.parameters;
			MinParams = mph.MinParams;
			MaxParams = mph.MaxParams;
		}
	}

	public delegate void SimpleDelegate();

	public delegate void VariadicAction(params object[] args);

	public delegate object VariadicFunction(params object[] args);
}