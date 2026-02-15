namespace Keysharp.Core.Common.Invoke
{
	public interface IPointable
	{
		public long Ptr { get; }
	}

	public interface IMetaObject
	{
		object Get(string name, object[] args);
		void Set(string name, object[] args, object value);
		object Call(string name, object[] args);
		object get_Item(object[] indexArgs);
		void set_Item(object[] indexArgs, object value);
	}

	public interface IFuncObj
    {
        public object Inst { get; set; }
		public bool IsBuiltIn { get; }
		public bool IsValid { get; }
		public string Name { get; }

		public IFuncObj Bind(params object[] obj);

        public object Call(params object[] obj);
		public object CallInst(object inst, params object[] obj);

        public bool IsByRef(object obj = null);

		public bool IsOptional(object obj = null);
	}

	public class BoundFunc : FuncObj
	{
		internal object[] boundargs;

		internal BoundFunc(MethodPropertyHolder m, object[] ba, object o = null)
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

		public override bool Equals(object obj) => ReferenceEquals(this, obj);

		[PublicHiddenFromUser]
		public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

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
			return new BoundFunc(mph, newbound, Inst);
		}

		/// <summary>
		/// Calls the target function along with any bound arguments.
		/// <returns>The return value of the bound function.</returns>
		public override object Call(params object[] args) => mi == null ? Script.Invoke(Inst, Name, CreateArgs(args).ToArray()) : base.Call(CreateArgs(args).ToArray());
		[PublicHiddenFromUser]
		public override object CallInst(object inst, params object[] args) => mi == null ? Script.Invoke(Inst, Name, CreateArgs(args.Prepend(inst)).ToArray()) : base.Call(CreateArgs(args.Prepend(inst)).ToArray());


		private List<object> CreateArgs(params object[] args)
		{
			int i = 0, argsused = 0;
			var argsList = new List<object>(mph.parameters?.Length ?? 4);

			for (; i < boundargs.Length; i++)
			{
				if (boundargs[i] != null)
				{
					argsList.Add(boundargs[i]);
				}
				else if (argsused < args.Length)
				{
					argsList.Add(args[argsused]);
					argsused++;
				}
				else
					argsList.Add(null);
			}

			for (; argsused < args.Length; argsused++)
				argsList.Add(args[argsused]);

			while (argsList.Count < (mph.parameters?.Length ?? 0))
			{
				var param = mph.parameters[argsList.Count];

				if (param.Attributes.HasFlag(ParameterAttributes.HasDefault))
					argsList.Add(param.DefaultValue);
				else
					break;
			}

			return argsList;
		}
	}

	public class Closure : FuncObj
	{
        internal Closure(Delegate m, object o = null) : base(m, o) { }
    }

	public class FuncObj : KeysharpObject, IFuncObj
	{
		protected MethodInfo mi;
		internal MethodPropertyHolder mph;
		private readonly Type moduleType;

		[PublicHiddenFromUser]
		public object Inst { get; set; }
		internal Type DeclaringType => mi?.DeclaringType;
		public bool IsClosure => Inst != null && mi.DeclaringType?.DeclaringType == Inst.GetType();
		public bool IsMethod => (mi != null && !mi.IsStatic) || (mph != null && mph.parameters?.First().Name == "@this");
		public bool IsBuiltIn => mi?.DeclaringType.Namespace != TheScript.ProgramType.Namespace;
		public bool IsValid => (mi != null && mph != null && mph.CallFunc != null) || (Inst is Any && mph.memberInfo == null);
		public string Name => mph.Name;
		public bool IsVariadic => mph.variadicParamIndex != -1;
		public long MaxParams { get; internal set; } = 0;
		public long MinParams { get; internal set; } = 0;
		internal int VariadicIndex => mph.variadicParamIndex;
		internal MethodPropertyHolder Mph => mph;

		internal FuncObj(string s, object o = null, object paramCount = null)
			: this(GetMethodInfo(s, o, paramCount), o)
		{
		}

		public FuncObj(params object[] args) : base(args) { }

		public static object Call(object @this, object funcName, object obj = null, object paramCount = null)
			=> Functions.GetFuncObj(funcName, obj, paramCount, obj != null);

        private static MethodInfo GetMethodInfo(string s, object o, object paramCount)
        {
            if (o != null)
            {
				if (o is not Type)
				{
					var mitup = Script.GetMethodOrProperty(o, s, paramCount.Ai(-1));
					if (mitup.Item2 is FuncObj fo)
						return fo.mph.mi;
					else if (mitup.Item2 is MethodPropertyHolder mph)
						return mph.mi;
				}
                // Try to find and cache the method
                var method = Reflections.FindAndCacheMethod((o as Type) ?? o.GetType(), s, paramCount.Ai(-1));
                if (method != null)
                    return method.mi;

				throw new TargetError("Unable to find a method object for the requested method " + s);
            }

            // Fallback to finding the method without an object
            return Reflections.FindMethod(s, paramCount.Ai(-1))?.mi;
        }

        internal FuncObj(string s, string t, object paramCount = null)
		: this(Reflections.FindAndCacheMethod(Script.TheScript.ReflectionsData.stringToTypes[t], s, paramCount.Ai(-1)))
        {
        }

        internal FuncObj(string s, Type t, object paramCount = null)
		: this(Reflections.FindAndCacheMethod(t, s, paramCount.Ai(-1)))
        {
        }

        internal FuncObj(MethodPropertyHolder m, object o = null) : base()
		{
			mph = m;
			mi = m?.mi;
			moduleType = ResolveModuleType(mi?.DeclaringType);
			Inst = o;

			if (mph != null)
			{
				MinParams = mph.MinParams;
				MaxParams = mph.MaxParams;
			}
		}

		internal FuncObj(Delegate m, object o = null)
		: this(m?.GetMethodInfo(), o)
        {
			this.Inst = o ?? m.Target;
        }

		internal FuncObj(MethodInfo m, object o = null) : this(m == null ? null : MethodPropertyHolder.GetOrAdd(m), o)
		{
		}

		public virtual IFuncObj Bind(params object[] args)
		=> new BoundFunc(mph, args, Inst);

		public virtual object Call(params object[] obj)
		{
			if (moduleType != null && Script.TheScript.ModuleData is ModuleData md && md != null)
			{
				var previous = md.Push(moduleType, out var changed);
				try
				{
					return mph.CallFunc(Inst, obj);
				}
				finally
				{
					md.Pop(previous, changed);
				}
			}

			return mph.CallFunc(Inst, obj);
		}
		[PublicHiddenFromUser]
		public virtual object CallInst(object inst, params object[] args)
		{
			if (moduleType != null && Script.TheScript.ModuleData is ModuleData md && md != null)
			{
				var previous = md.Push(moduleType, out var changed);
				try
				{
					if (Inst == null)
						return mph.CallFunc(inst, args);
					return mph.CallFunc(Inst, args.Prepend(inst));
				}
				finally
				{
					md.Pop(previous, changed);
				}
			}

			if (Inst == null)
				return mph.CallFunc(inst, args);

			return mph.CallFunc(Inst, args.Prepend(inst));
		}
		private static Type ResolveModuleType(Type type)
		{
			for (var t = type; t != null; t = t.DeclaringType)
			{
				if (typeof(Keysharp.Core.Common.ObjectBase.Module).IsAssignableFrom(t))
					return t;
			}

			return null;
		}

		public override bool Equals(object obj)
		{
			if (obj is BoundFunc)
				return false; // BoundFunc has its own Equals override and considers all instances unique
			return obj is FuncObj fo ? fo.mi == mi && fo.Inst == Inst : false;
		}

		[PublicHiddenFromUser]
		public override int GetHashCode()
		{
			unchecked
			{
				int h = mi?.GetHashCode() ?? 0;
				return (h * 31) ^ (Inst != null ? RuntimeHelpers.GetHashCode(Inst) : 0);
			}
		}

		public bool IsByRef(object obj = null)
		{
			var index = obj.Ai();
			var funcParams = mi.GetParameters();

			if (index > 0)
			{
				index--;

				if (index < funcParams.Length)
					return funcParams[index].ParameterType.IsByRef || funcParams[index].GetCustomAttribute(typeof(ByRefAttribute)) != null;
			}
			else
			{
				for (var i = 0; i < funcParams.Length; i++)
					if (funcParams[i].ParameterType.IsByRef || funcParams[index].GetCustomAttribute(typeof(ByRefAttribute)) != null)
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
	}

	public delegate void SimpleDelegate();

	public delegate void VariadicAction(params object[] args);

	public delegate object VariadicFunction(params object[] args);
}
