using System.Diagnostics.Metrics;
using System.Reflection.Metadata;
using static Keysharp.Core.Clr;

namespace Keysharp.Core
{

	public class Clr : KeysharpObject
	{
		public object staticLoad(object asmOrPath)
		{
			var s = asmOrPath.As();
			var asm = s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
					  ? Assembly.LoadFrom(s)
					  : Assembly.Load(s);

			// If user gave a simple assembly name that matches the loaded assembly,
			// return a namespace root anchored at that simple name ("System", etc.).
			// Otherwise return a ManagedAssembly for mixed scenarios.
			return s.Contains('.') || !asm.GetName().Name.Equals(s, StringComparison.OrdinalIgnoreCase) 
				? new ManagedAssembly(asm)
				: new ManagedNamespace([asm], s);
		}

		// Still keep these for direct access if someone prefers:
		public object staticType(object fullTypeName)
		{
			var name = fullTypeName.As();
			var t = TypeResolver.Resolve(name);
			if (t == null)
				return Errors.ErrorOccurred($"Type not found: {name}");
			return new ManagedType(t);
		}

		public object staticGetNamespaceName(object @namespace)
		{
			var ns = @namespace as ManagedNamespace;
			if (ns == null)
				return Errors.ErrorOccurred("The provided argument was not a ManagedNamespace object.");
			return ns._ns;
		}

		public object staticGetTypeName(object type)
		{
			var mt = type as ManagedType;
			if (mt == null)
				return Errors.ErrorOccurred("The provided argument was not a ManagedType object.");
			return mt._type.FullName;
		}

		public sealed class ManagedAssembly : Any
		{
			internal readonly Assembly[] _assemblies;

			public ManagedAssembly(params Assembly[] assemblies) => _assemblies = assemblies;

			public object __Get(object name, object args)
			{
				var seg = name.As();

				// Prefer unique simple-name in preferred assemblies; then global index.
				if (TypeResolver.TryResolveSimpleNameUnique(seg, _assemblies, out var t, out var ambiguous))
				{
					return new Clr.ManagedType(t);
				}
				if (ambiguous)
				{
					return Errors.ErrorOccurred($"Type name '{seg}' is ambiguous in these assemblies.");
				}

				// Not a type: start a namespace walk rooted at this assembly scope.
				return new Clr.ManagedNamespace(_assemblies, seg);
			}

			public object __Call(object name, object args)
			{
				var seg = name.As();

				// Try simple-name unique resolution again for constructor-ish call.
				if (!TypeResolver.TryResolveSimpleNameUnique(seg, _assemblies, out var t, out var ambiguous))
				{
					if (ambiguous)
						return Errors.ErrorOccurred($"Type name '{seg}' is ambiguous across loaded assemblies.");
					return Errors.ErrorOccurred($"Type not found: {seg}");
				}

				var argv = FlattenParam(args);

				// Delegate shapes consolidated via helper
				if (typeof(Delegate).IsAssignableFrom(t))
					return ClrDelegateFactory.BuildManagedDelegateNode(t, argv);

				if (argv.Length == 0)
				{
					var ci = t.GetConstructor(Type.EmptyTypes);
					if (ci != null) return new Clr.ManagedInstance(t, Activator.CreateInstance(t));
					if (t.IsValueType) return new Clr.ManagedInstance(t, Activator.CreateInstance(t));
					return new Clr.ManagedType(t);
				}

				var inArgs = ManagedInvoke.ConvertInArgs(t, argv);
				var res = ActivatorUtil.CreateInstanceOrError(t, inArgs, inst => new Clr.ManagedInstance(t, inst));
				if (res == null) return DefaultObject;
				return (Clr.ManagedInstance)res;
			}
		}

		public sealed class ManagedNamespace : Any
		{
			internal readonly Assembly[] _assemblies; // preferred set for TypeResolver
			internal readonly string _ns;             // accumulated namespace root/prefix

			public ManagedNamespace(Assembly[] assemblies, string ns)
			{
				_assemblies = assemblies;
				_ns = ns ?? "";
			}

			public object __Get(object name, object args)
			{
				var seg = name.As();
				var full = string.IsNullOrEmpty(_ns) ? seg : _ns + "." + seg;

				// Generic sugar: ns.List["int"] or ns.Dictionary["string","int"]
				var argv = FlattenParam(args);
				if (argv.Length > 0)
				{
					// e.g., "System.Collections.Generic.List`1"
					var genericName = full + "`" + argv.Length;

					// Prefer the user-loaded assemblies first (TypeResolver uses caches internally)
					var genDef = TypeResolver.Resolve(genericName, _assemblies);
					if (genDef != null && genDef.IsGenericTypeDefinition)
					{
						var typeArgs = argv.Select(TypeResolver.ResolveTypeArg).ToArray();
						var closed = genDef.MakeGenericType(typeArgs);
						return new ManagedType(closed);
					}
					// If not a generic definition, fall through and try non-generic lookup below.
				}

				// Try exact, non-generic type by full name, preferring the assemblies user loaded.
				var t2 = TypeResolver.Resolve(full, _assemblies);
				if (t2 != null)
					return new ManagedType(t2);

				// Otherwise keep walking deeper into the namespace.
				return new ManagedNamespace(_assemblies, full);
			}

			public object __Call(object name, object args)
			{
				var seg = name.As();
				if (seg.Length == 0)
					return Errors.ErrorOccurred("Missing type name for constructor call.");

				var full = string.IsNullOrEmpty(_ns) ? seg : _ns + "." + seg;

				// Resolve the concrete type via the cached resolver, preferring our assemblies.
				var t = TypeResolver.Resolve(full, _assemblies);
				if (t is null)
					return Errors.ErrorOccurred($"Type not found: {full}");

				var argv = FlattenParam(args);

				if (argv.Length == 0)
				{
					// Prefer real parameterless constructor
					var ci = t.GetConstructor(Type.EmptyTypes);
					if (ci != null)
						return new ManagedInstance(t, Activator.CreateInstance(t));

					// Value-type sugar: default(T)
					if (t.IsValueType)
						return new ManagedInstance(t, Activator.CreateInstance(t));

					// No ctor: return the type node for static access
					return new ManagedType(t);
				}

				// First, try real constructors (DateTime, TimeSpan, Guid, etc.).
				try
				{
					var inArgs = ManagedInvoke.ConvertInArgs(t, argv);
					var res = ActivatorUtil.CreateInstanceOrError(t, inArgs, inst => new Clr.ManagedInstance(t, inst));
					if (res == null) return DefaultObject;
					return (Clr.ManagedInstance)res;
				}
				catch (MissingMethodException)
				{
					// fall through to value-type sugar
				}

				// Value-type sugar: single-arg cast/box
				if (t.IsValueType)
				{
					if (argv.Length == 1)
					{
						var boxed = ManagedInvoke.CoerceToType(argv[0], t);
						return new ManagedInstance(t, boxed);
					}
				}

				return Errors.ErrorOccurred($"Constructor on type '{t.FullName}' not found for {argv.Length} argument(s).");
			}
		}

		public sealed class ManagedType : Any
		{
			internal readonly Type _type;

			public ManagedType(Type type) => _type = type ?? throw new ArgumentNullException(nameof(type));

			public object __Call(object name, object args)
			{
				var method = name?.As() ?? "";
				var argv = FlattenParam(args);

				// Treat "", null, or "Call" as a constructor call: listT()
				if (method.Length == 0 || method.Equals("Call", StringComparison.OrdinalIgnoreCase))
				{
					if (typeof(Delegate).IsAssignableFrom(_type))
					{
						var res = ClrDelegateFactory.BuildManagedDelegateNode(_type, argv);
						if (res is ManagedInstance) return res;
						return res; // already an error node if not ManagedInstance
					}

					// 1) Try real ctors first
					try
					{
						if (argv.Length == 0)
							return new ManagedInstance(_type, Activator.CreateInstance(_type));

						var inArgs = ManagedInvoke.ConvertInArgs(_type, argv);
						var res = ActivatorUtil.CreateInstanceOrError(_type, inArgs, inst => new ManagedInstance(_type, inst));
						if (res == null) return res;
						return (ManagedInstance)res;
					}
					catch (MissingMethodException)
					{
						// 2) Value-type sugar: cast/box
						if (_type.IsValueType)
						{
							if (argv.Length == 0)
								return new ManagedInstance(_type, Activator.CreateInstance(_type)); // default(T)

							if (argv.Length == 1)
								return new ManagedInstance(_type, ManagedInvoke.CoerceToType(argv[0], _type));
						}
						return Errors.ErrorOccurred($"Constructor on type '{_type.FullName}' not found for {argv.Length} argument(s).");
					}
				}

				// Static method: TypeNode.Method(args...)
				return ManagedInvoke.InvokeStatic(_type, method, argv);
			}

			// Static prop/field: get
			public object __Get(object name, object args)
				=> ManagedInvoke.GetStatic(_type, name.As());

			// Static prop/field: set
			public object __Set(object name, object args, object value)
			{
				ManagedInvoke.SetStatic(_type, name.As(), value);
				return value;
			}

			// Generics sugar: TypeNode[TArg1, TArg2, ...]
			public object get___Item(params object[] typeArgs)
			{
				if (!_type.IsGenericTypeDefinition)
					return Errors.ErrorOccurred($"{_type.FullName} is not an open generic type.");

				var closed = _type.MakeGenericType(typeArgs.Select(TypeResolver.ResolveTypeArg).ToArray());
				return new ManagedType(closed);
			}
		}

		public sealed class ManagedInstance : Any
		{
			internal readonly Type _type;
			internal object _instance;

			public ManagedInstance(Type type, object instance) { _type = type; _instance = instance; }

			// Instance method call: obj.Method(args...)
			public object __Call(object name, object args)
				=> ManagedInvoke.InvokeInstance(_instance, _type, name.As(), FlattenParam(args));

			// Instance prop/field: get/set
			public object __Get(object name, object args)
				=> ManagedInvoke.GetInstance(_instance, _type, name.As(), FlattenParam(args));

			public object __Set(object name, object args, object value)
			{
				ManagedInvoke.SetInstance(_instance, _type, name.As(), FlattenParam(args), value);
				return value;
			}

			// Indexers: obj[args...]  (get & set variants)
			public object get___Item(params object[] indexArgs)
				=> ManagedInvoke.GetIndexer(_instance, _type, indexArgs);

			public object set___Item(object[] indexArgs, object value)
			{
				ManagedInvoke.SetIndexer(_instance, _type, value, indexArgs);
				return value;
			}

			/// <summary>
			/// AHK-style enumerator: returns a vararg thunk for for-in loops.
			/// argCount >= 1. For 1 var: value. For 2+ vars: decompose (key,value / tuple),
			/// else (index, value, null…).
			/// </summary>
			public IFuncObj __Enum(object argCount)
			{
				var c = argCount.Ai(1);
				if (c < 1) c = 1;

				var it = new ClrIterator(this, c);
				if (it.Enumerator == null) return null; // error already reported

				var p = Script.TheScript.ManagedDotNetIteratorData.p1;
				it.fo = (FuncObj)p.Clone();
				it.fo.Inst = it;

				return it.fo;
			}
		}
	}

	internal class ClrIteratorData : BaseIteratorData<ClrIterator>
	{
	}

	/// <summary>
	/// Varargs Keysharp enumerator over .NET enumerables.
	/// Count = requested variable count from __Enum(n).
	/// </summary>
	internal sealed class ClrIterator : KeysharpEnumerator, IDisposable, IEnumerator<(object, object)>
	{
		public object Enumerator;          // IEnumerator or custom struct enumerator
		public Type EnumType;
		public MethodInfo MoveNextMI;
		public PropertyInfo CurrentPI;
		public MethodInfo ResetMI;
		public bool IsIDisposable;
		public long Index = -1;          // 0-based

		private Func<bool> _moveNextFast;
		private Func<object> _currentFast;

		public ClrIterator(ManagedInstance instance, int count)
			: base(null, Math.Max(1, count))
		{
			object en = null;
			MethodInfo move = null;
			PropertyInfo cur = null;
			// 1) IEnumerable fast path
			if (instance._instance is IEnumerable ien)
				en = ien.GetEnumerator();
			// 2) Generic IEnumerable<T> → GetEnumerator()
			else if (instance._type.GetMethod("GetEnumerator",
					BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
					binder: null, types: Type.EmptyTypes, modifiers: null) is MethodInfo getEnum && getEnum != null)
			{

				en = getEnum.Invoke(instance._instance, null);
			}

			if (en == null) {
				// 3) Instance itself is an enumerator: MoveNext + Current
				move = instance._type.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
				cur = instance._type.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
				en = instance._instance;
			}

			if (en == null)
			{
				_ = Errors.ErrorOccurred($"Object of type '{instance._type.FullName}' is not enumerable.");
				return;
			}

			var et = en.GetType();
			MoveNextMI = move ?? et.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
			_moveNextFast = (Func<bool>)MoveNextMI.CreateDelegate(typeof(Func<bool>), en);
			CurrentPI = cur ?? et.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
			_currentFast = (Func<object>)CurrentPI.GetMethod.CreateDelegate(typeof(Func<object>), en);
			var reset = et.GetMethod("Reset", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
			if (MoveNextMI == null || CurrentPI == null) return;

			var disposable = typeof(IDisposable).IsAssignableFrom(et) || et.GetInterface(nameof(IDisposable)) != null;

			Enumerator = en;
			EnumType = et;
			ResetMI = reset;
			IsIDisposable = disposable;
		}

		public bool MoveNext()
		{
			var ok = (bool)_moveNextFast();
			if (ok) Index++;
			return ok;
		}

		public void Reset()
		{
			Index = -1;
			try { ResetMI?.Invoke(Enumerator, null); } catch { /* many don't support Reset; ignore */ }
		}

		public void Dispose()
		{
			try { if (IsIDisposable && Enumerator is IDisposable d) d.Dispose(); } catch { }
		}

		// Try to split "Current" into parts:
		// - DictionaryEntry → (key,value)
		// - KeyValuePair<,> → (key,value)
		// - ITuple (ValueTuple / Tuple) → all elements
		// Else: return null to indicate "not decomposable".
		public static object[] TryDecompose(object current)
		{
			if (current is null) return null;

			if (current is DictionaryEntry de)
				return new[]
				{
					ManagedInvoke.ConvertOut(de.Key),
					ManagedInvoke.ConvertOut(de.Value)
				};

			var t = current.GetType();

			if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
			{
				var pk = t.GetProperty("Key");
				var pv = t.GetProperty("Value");
				var k = pk?.GetValue(current);
				var v = pv?.GetValue(current);
				return new[]
				{
					ManagedInvoke.ConvertOut(k),
					ManagedInvoke.ConvertOut(v)
				};
			}

			if (current is ITuple tup)
			{
				var n = tup.Length;
				var arr = new object[n];
				for (int i = 0; i < n; i++)
					arr[i] = ManagedInvoke.ConvertOut(tup[i]);
				return arr;
			}

			return new[] { current };
		}

		/// <summary>
		/// The implementation for <see cref="IEnumerator.Current"/> which gets the index,value tuple at the current iterator position.
		/// </summary>
		public (object, object) Current
		{
			get
			{
				var cur = ((IEnumerator)this).Current;
				var parts = TryDecompose(cur);
				if (parts == null || parts.Length == 0) return (null, null);
				else if (parts.Length == 1) return (parts[0], null);
				else return (parts[0], parts[1]);
			}
		}

		/// <summary>
		/// The <see cref="IEnumerator.Current"/> implementation that just returns <see cref="Current"/>.
		/// </summary>
		object IEnumerator.Current => _currentFast();

		/// <summary>
		/// Engine invokes this with exactly Count ByRef boxes.
		/// </summary>
		public override object Call(params object[] args)
		{
			// Strict arg-count validation (should never mismatch if engine is correct)
			if (args == null || args.Length != Count)
				throw new InvalidOperationException($"Enumerator expected {Count} ByRef vars, received {(args?.Length ?? 0)}.");

			if (!MoveNext())
			{
				Dispose();
				return false;
			}

			var cur = ((IEnumerator)this).Current;
			var parts = TryDecompose(cur);
			var needed = Count;

			if (parts == null)
			{
				if (needed == 1)
				{
					Script.SetPropertyValue(args[0], "__Value", null);
					return true;
				}
				return false;
			}

			if (Count >= 2 && parts.Length == 1)
				parts = new object[] { Index + 1, parts[0] }; // 1-based index for AHK

			if (needed > parts.Length) throw new Error("Too many enumerator arguments provided");

			// Fill from decomposed parts; pad with null if fewer than Count
			for (int i = 0; i < needed; i++)
			{
				var v = i < parts.Length ? parts[i] : null;
				Script.SetPropertyValue(args[i], "__Value", ManagedInvoke.ConvertOut(v));
			}
			return true;
		}
	}
}
