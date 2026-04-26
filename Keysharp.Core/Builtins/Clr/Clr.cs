namespace Keysharp.Builtins
{
	public partial class Ks
	{
		public class Clr : KeysharpObject
		{
			public static object staticLoad(object @this, object asmOrPath)
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
			public static object staticType(object @this, object fullTypeName)
			{
				var name = fullTypeName.As();
				var t = TypeResolver.Resolve(name);
				if (t == null)
					return Errors.ErrorOccurred($"Type not found: {name}");
				return new ManagedType(t);
			}

			public static object staticGetNamespaceName(object @this, object @namespace)
			{
				var ns = @namespace as ManagedNamespace;
				if (ns == null)
					return Errors.ErrorOccurred("The provided argument was not a ManagedNamespace object.");
				return ns._ns;
			}

			public static object staticGetTypeName(object @this, object type)
			{
				var mt = type as ManagedType;
				if (mt == null)
					return Errors.ErrorOccurred("The provided argument was not a ManagedType object.");
				return mt._type.FullName;
			}

			public class ManagedObject : Any, IMetaObject
			{
				object IMetaObject.Get(string name, object[] args) => Get(name, args);
				internal virtual object Get(string name, object[] args) =>
					Errors.ErrorOccurred($"Get not implemented on type {GetType().FullName}.");

				void IMetaObject.Set(string name, object[] args, object value) => Set(name, args, value);
				internal virtual void Set(string name, object[] args, object value) =>
					Errors.ErrorOccurred($"Set not implemented on type {GetType().FullName}.");

				object IMetaObject.Call(string name, object[] args) => Call(name, args);
				internal virtual object Call(string name, object[] args) =>
					Errors.ErrorOccurred($"Call not implemented on type {GetType().FullName}.");

				object IMetaObject.get_Item(object[] indexArgs) => get_Item(indexArgs);
				internal virtual object get_Item(object[] indexArgs) =>
					Errors.ErrorOccurred($"get_Item not implemented on type {GetType().FullName}.");

				void IMetaObject.set_Item(object[] indexArgs, object value) => set_Item(indexArgs, value);
				internal virtual void set_Item(object[] indexArgs, object value) =>
					Errors.ErrorOccurred($"set_Item not implemented on type {GetType().FullName}.");
			}

			public sealed class ManagedAssembly : ManagedObject
			{
				internal readonly Assembly[] _assemblies;

				public ManagedAssembly(params Assembly[] assemblies) => _assemblies = assemblies;

				internal override object Get(string name, object[] args)
				{
					// Prefer unique simple-name in preferred assemblies; then global index.
					if (TypeResolver.TryResolveSimpleNameUnique(name, _assemblies, out var t, out var ambiguous))
					{
						return new Clr.ManagedType(t);
					}
					if (ambiguous)
					{
						return Errors.ErrorOccurred($"Type name '{name}' is ambiguous in these assemblies.");
					}

					// Not a type: start a namespace walk rooted at this assembly scope.
					return new Clr.ManagedNamespace(_assemblies, name);
				}

				internal override object Call(string name, object[] args)
				{
					// Try simple-name unique resolution again for constructor-ish call.
					if (!TypeResolver.TryResolveSimpleNameUnique(name, _assemblies, out var t, out var ambiguous))
					{
						if (ambiguous)
							return Errors.ErrorOccurred($"Type name '{name}' is ambiguous across loaded assemblies.");
						return Errors.ErrorOccurred($"Type not found: {name}");
					}

					// Delegate shapes consolidated via helper
					if (typeof(Delegate).IsAssignableFrom(t))
						return ClrDelegateFactory.BuildManagedDelegateNode(t, args);

					if (args.Length == 0)
					{
						var ci = t.GetConstructor(System.Type.EmptyTypes);
						if (ci != null) return new Clr.ManagedInstance(t, Activator.CreateInstance(t));
						if (t.IsValueType) return new Clr.ManagedInstance(t, Activator.CreateInstance(t));
						return new Clr.ManagedType(t);
					}

					var inArgs = ManagedInvoke.ConvertInArgs(t, args);
					var res = ActivatorUtil.CreateInstanceOrError(t, inArgs, inst => new Clr.ManagedInstance(t, inst));
					if (res == null) return DefaultObject;
					return (Clr.ManagedInstance)res;
				}
			}

			public sealed class ManagedNamespace : ManagedObject
			{
				internal readonly Assembly[] _assemblies; // preferred set for TypeResolver
				internal readonly string _ns;             // accumulated namespace root/prefix

				public ManagedNamespace(Assembly[] assemblies, string ns)
				{
					_assemblies = assemblies;
					_ns = ns ?? "";
				}

				internal override object Get(string name, object[] args)
				{
					var full = string.IsNullOrEmpty(_ns) ? name : _ns + "." + name;

					// Generic sugar: ns.List["int"] or ns.Dictionary["string","int"]
					if (args.Length > 0)
					{
						// e.g., "System.Collections.Generic.List`1"
						var genericName = full + "`" + args.Length;

						// Prefer the user-loaded assemblies first (TypeResolver uses caches internally)
						var genDef = TypeResolver.Resolve(genericName, _assemblies);
						if (genDef != null && genDef.IsGenericTypeDefinition)
						{
							var typeArgs = args.Select(TypeResolver.ResolveTypeArg).ToArray();
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

				internal override object Call(string name, object[] args)
				{
					if (name.Length == 0)
						return Errors.ErrorOccurred("Missing type name for constructor call.");

					var full = string.IsNullOrEmpty(_ns) ? name : _ns + "." + name;

					// Resolve the concrete type via the cached resolver, preferring our assemblies.
					var t = TypeResolver.Resolve(full, _assemblies);
					if (t is null)
						return Errors.ErrorOccurred($"Type not found: {full}");

					if (args.Length == 0)
					{
						// Prefer real parameterless constructor
						var ci = t.GetConstructor(System.Type.EmptyTypes);
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
						var inArgs = ManagedInvoke.ConvertInArgs(t, args);
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
						if (args.Length == 1)
						{
							var boxed = ManagedInvoke.CoerceToType(args[0], t);
							return new ManagedInstance(t, boxed);
						}
					}

					return Errors.ErrorOccurred($"Constructor on type '{t.FullName}' not found for {args.Length} argument(s).");
				}

				[PublicHiddenFromUser]
				public override string ToString() => _ns;
			}

			public sealed class ManagedType : ManagedObject
			{
				internal readonly Type _type;

				public ManagedType(Type type) => _type = type ?? throw new ArgumentNullException(nameof(type));

				internal override object Call(string name, object[] args)
				{
					// Treat "", null, or "Call" as a constructor call: listT()
					if (name == null || name.Length == 0 || name.Equals("Call", StringComparison.OrdinalIgnoreCase))
					{
						if (typeof(Delegate).IsAssignableFrom(_type))
						{
							var res = ClrDelegateFactory.BuildManagedDelegateNode(_type, args);
							if (res is ManagedInstance) return res;
							return res; // already an error node if not ManagedInstance
						}

						// 1) Try real ctors first
						try
						{
							if (args.Length == 0)
								return new ManagedInstance(_type, Activator.CreateInstance(_type));

							var inArgs = ManagedInvoke.ConvertInArgs(_type, args);
							var res = ActivatorUtil.CreateInstanceOrError(_type, inArgs, inst => new ManagedInstance(_type, inst));
							if (res == null) return res;
							return (ManagedInstance)res;
						}
						catch (MissingMethodException)
						{
							// 2) Value-type sugar: cast/box
							if (_type.IsValueType)
							{
								if (args.Length == 0)
									return new ManagedInstance(_type, Activator.CreateInstance(_type)); // default(T)

								if (args.Length == 1)
									return new ManagedInstance(_type, ManagedInvoke.CoerceToType(args[0], _type));
							}
							return Errors.ErrorOccurred($"Constructor on type '{_type.FullName}' not found for {args.Length} argument(s).");
						}
					}

					// Static method: TypeNode.Method(args...)
					return ManagedInvoke.InvokeStatic(_type, name, args);
				}

				// Static prop/field: get
				internal override object Get(string name, object[] args)
					=> ManagedInvoke.GetStatic(_type, name);

				// Static prop/field: set
				internal override void Set(string name, object[] args, object value)
					=> ManagedInvoke.SetStatic(_type, name, value);

				// Generics sugar: TypeNode[TArg1, TArg2, ...]
				internal override object get_Item(object[] typeArgs)
				{
					if (!_type.IsGenericTypeDefinition)
						return Errors.ErrorOccurred($"{_type.FullName} is not an open generic type.");

					var closed = _type.MakeGenericType(typeArgs.Select(TypeResolver.ResolveTypeArg).ToArray());
					return new ManagedType(closed);
				}

				[PublicHiddenFromUser]
				public override string ToString() => _type.ToString();
			}

			public sealed class ManagedInstance : ManagedObject
			{
				internal readonly Type _type;
				internal object _instance;

				public ManagedInstance(Type type, object instance) { _type = type; _instance = instance; }

				// Instance method call: obj.Method(args...)
				internal override object Call(string name, object[] args)
					=> ManagedInvoke.InvokeInstance(_instance, _type, name, args);

				// Instance prop/field: get/set
				internal override object Get(string name, object[] args)
					=> ManagedInvoke.GetInstance(_instance, _type, name, args);

				internal override void Set(string name, object[] args, object value)
					=> ManagedInvoke.SetInstance(_instance, _type, name, args, value);


				// Indexers: obj[args...]  (get & set variants)
				internal override object get_Item(object[] indexArgs)
					=> ManagedInvoke.GetIndexer(_instance, _type, indexArgs);

				internal override void set_Item(object[] indexArgs, object value)
					=> ManagedInvoke.SetIndexer(_instance, _type, value, indexArgs);

				/// <summary>
				/// AHK-style enumerator: returns a vararg thunk for for-in loops.
				/// argCount >= 1. For 1 var: value. For 2+ vars: decompose (key,value / tuple),
				/// else (index, value, null…).
				/// </summary>
				public IFuncObj __Enum(object argCount)
				{
					var c = argCount.Ai(1);
					if (c < 1) c = 1;

					return CreateEnumerator(this, c);
				}

				[PublicHiddenFromUser]
				public override string ToString() => _instance.ToString();
			}

			private static Enumerator CreateEnumerator(ManagedInstance instance, int count)
			{
				object source;
				MethodInfo moveNextMethod = null;
				PropertyInfo currentProperty = null;

				if (instance._instance is IEnumerable enumerable)
				{
					source = enumerable.GetEnumerator();
				}
				else if (instance._type.GetMethod(
							 "GetEnumerator",
							 BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
							 binder: null,
							 types: System.Type.EmptyTypes,
							 modifiers: null) is MethodInfo getEnumerator)
				{
					source = getEnumerator.Invoke(instance._instance, null);
				}
				else
				{
					moveNextMethod = instance._type.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
					currentProperty = instance._type.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
					source = instance._instance;
				}

				if (source == null)
				{
					_ = Errors.ErrorOccurred($"Object of type '{instance._type.FullName}' is not enumerable.");
					return null;
				}

				var enumType = source.GetType();
				moveNextMethod ??= enumType.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
				currentProperty ??= enumType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
				var resetMethod = enumType.GetMethod("Reset", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
				var index = -1L;

				if (moveNextMethod == null || currentProperty == null)
				{
					_ = Errors.ErrorOccurred($"Object of type '{instance._type.FullName}' is not enumerable.");
					return null;
				}

				return new Enumerator(
						   instance,
						   count,
						   () =>
				{
					var moved = (bool)moveNextMethod.Invoke(source, null);

					if (moved)
						index++;

					return moved;
				},
				() => currentProperty.GetValue(source),
				() =>
				{
					var parts = TryDecompose(currentProperty.GetValue(source));

					if (parts == null || parts.Length == 0)
						return (null, null);
					else if (parts.Length == 1)
						return (index + 1, parts[0]);
					else
						return (parts[0], parts[1]);
				},
				() =>
				{
					index = -1L;

					try
					{
						resetMethod?.Invoke(source, null);
					}
					catch
					{
					}
				},
				() =>
				{
					try
					{
						if (source is IDisposable disposable)
							disposable.Dispose();
					}
					catch
					{
					}
				});
			}

			private static object[] TryDecompose(object current)
			{
				if (current is null)
					return null;

				if (current is DictionaryEntry de)
				{
					return
					[
						ManagedInvoke.ConvertOut(de.Key),
						ManagedInvoke.ConvertOut(de.Value)
					];
				}

				var type = current.GetType();

				if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
				{
					var key = type.GetProperty("Key")?.GetValue(current);
					var value = type.GetProperty("Value")?.GetValue(current);
					return
					[
						ManagedInvoke.ConvertOut(key),
						ManagedInvoke.ConvertOut(value)
					];
				}

				if (current is ITuple tuple)
				{
					var parts = new object[tuple.Length];

					for (var i = 0; i < tuple.Length; i++)
						parts[i] = ManagedInvoke.ConvertOut(tuple[i]);

					return parts;
				}

				return [ManagedInvoke.ConvertOut(current)];
			}
		}
	}
}
