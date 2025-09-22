using System;
using System.Reflection;
using System.Xml.Linq;
using Antlr4.Runtime.Misc;
using Keysharp.Core;

namespace Keysharp.Scripting
{
	public partial class Script
	{
		public static void InitClass(Type t, Type alias = null)
		{
			var script = Script.TheScript;
			var store = script.Vars;
			Type actual = alias ?? t;

			if (store.Prototypes.IsInitialized(t))
				return;

			var proto = (Any)RuntimeHelpers.GetUninitializedObject(actual);
			Any staticInst = (Any)RuntimeHelpers.GetUninitializedObject(actual);

			store.Statics.AddLazy(t, () =>
			{
				// triggers the Prototypes’ factory (if not yet run),
				// which in turn set store.Statics[t] to the real staticInst.
				var dummy = store.Prototypes[t];

				return staticInst;
			});

			store.Prototypes.AddLazy(t, () =>
			{
				store.Prototypes[t] = proto;
				store.Statics[t] = staticInst;

				var isBuiltin = script.ProgramType.Namespace != t.Namespace;

				proto.op = new Dictionary<string, OwnPropsDesc>(StringComparer.OrdinalIgnoreCase);
				staticInst.op = new Dictionary<string, OwnPropsDesc>(StringComparer.OrdinalIgnoreCase);

				// Get all static and instance methods
				MethodInfo[] methods;

				if (isBuiltin && (script.ReflectionsData.typeToStringMethods.ContainsKey(t) || script.ReflectionsData.typeToStringStaticMethods.ContainsKey(t)))
				{
					var builtinInstMeths = script.ReflectionsData.typeToStringMethods[t]
						.Values // Get Dictionary<string, Dictionary<int, MethodPropertyHolder>>
						.SelectMany(m => m.Values) // Flatten to IEnumerable<Dictionary<int, MethodPropertyHolder>>
						.Select(mph => mph.mi); // Flatten to IEnumerable<MethodPropertyHolder>

					var builtinStaticMeths = script.ReflectionsData.typeToStringStaticMethods[t]
						.Values // Get Dictionary<string, Dictionary<int, MethodPropertyHolder>>
						.SelectMany(m => m.Values) // Flatten to IEnumerable<Dictionary<int, MethodPropertyHolder>>
						.Select(mph => mph.mi); // Flatten to IEnumerable<MethodPropertyHolder>

					methods = builtinInstMeths
						.Concat(builtinStaticMeths)
						.ToArray();
				}
				else
					methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);

				foreach (var method in methods)
				{
					var methodName = method.Name;

					bool isStatic = isBuiltin && method.IsStatic;
					if (methodName.StartsWith(Keywords.ClassStaticPrefix))
					{
						isStatic = true;
						methodName = methodName.Substring(Keywords.ClassStaticPrefix.Length);
					}

					if (methodName.StartsWith("get_") || methodName.StartsWith("set_"))
					{
						var propName = methodName.Substring(4);

						if (propName == "Item")
							propName = "__Item";

						OwnPropsDesc propertyMap = new OwnPropsDesc();
						OwnPropsDesc propDesc;

						if (isStatic)
						{
							if (staticInst.op.TryGetValue(propName, out propDesc))
								propertyMap = propDesc;

						}
						else
						{
							if (proto.op.TryGetValue(propName, out propDesc))
								propertyMap = propDesc;
						}

						if (methodName.StartsWith("get_"))
							propertyMap.Get = new FuncObj(method);
						else
							propertyMap.Set = new FuncObj(method);

						if (isStatic)
							staticInst.op[propName] = propertyMap;
						else
							proto.op[propName] = propertyMap;

						continue;
					}

					var nameAttrs = method.GetCustomAttributes(typeof(UserDeclaredNameAttribute));
					if (nameAttrs.Any())
					{
						methodName = ((UserDeclaredNameAttribute)nameAttrs.First()).Name;
					}

					if (isStatic)
					{
						DefineProp(staticInst, methodName, new OwnPropsDesc(staticInst, null, null, null, new FuncObj(method)));
						continue;
					}

					// Wrap method in FuncObj
					DefineProp(proto, methodName, new OwnPropsDesc(proto, null, null, null, new FuncObj(method)));
				}

				// Get all instance and static properties

				PropertyInfo[] properties;

				if (isBuiltin && script.ReflectionsData.typeToStringProperties.ContainsKey(t))
					properties = script.ReflectionsData.typeToStringProperties[t]
						.Values
						.SelectMany(m => m.Values)
						.Select(mph => mph.pi)
						.ToArray();
				else
					properties = t.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);

				foreach (var prop in properties)
				{
					var propertyName = prop.Name;
					OwnPropsDesc propertyMap = null;
					if ((prop.GetMethod?.IsStatic ?? false) || (prop.SetMethod?.IsStatic ?? false) || (propertyName.StartsWith(Keywords.ClassStaticPrefix)))
					{
						if (propertyName.StartsWith(Keywords.ClassStaticPrefix))
							propertyName = propertyName.Substring(Keywords.ClassStaticPrefix.Length);

						if (propertyName.StartsWith("get_") || propertyName.StartsWith("set_"))
							propertyName = propertyName.Substring(4);

						propertyMap = staticInst.op != null && staticInst.op.TryGetValue(propertyName, out OwnPropsDesc staticPropDesc) ? staticPropDesc : new OwnPropsDesc();

						if (prop.GetMethod != null)
						{
							propertyMap.Get = new FuncObj(prop.GetMethod);
						}

						if (prop.SetMethod != null)
						{
							propertyMap.Set = new FuncObj(prop.SetMethod);
						}

						if (!propertyMap.IsEmpty)
							staticInst.op[propertyName] = propertyMap;

						continue;
					}

					propertyMap = proto.op.TryGetValue(propertyName, out OwnPropsDesc propDesc) ? propDesc : new OwnPropsDesc();

					if (prop.GetMethod != null)
					{
						propertyMap.Get = new FuncObj(prop.GetMethod);
					}

					if (prop.SetMethod != null)
					{
						propertyMap.Set = new FuncObj(prop.SetMethod);
					}

					if (!propertyMap.IsEmpty)
						proto.op[propertyName] = propertyMap;
				}

				if (t != typeof(FuncObj) && t != typeof(Any))
					proto._base = script.Vars.Prototypes[t.BaseType];

				if (isBuiltin)
				{
					string name = t.Name;
					if (Keywords.TypeNameAliases.ContainsKey(name))
						name = Keywords.TypeNameAliases[name];
					proto.op["__Class"] = new OwnPropsDesc(proto, name);
				}

				staticInst.op["prototype"] = new OwnPropsDesc(staticInst, proto);

				if (t != typeof(FuncObj) && t != typeof(Any))
					staticInst._base = script.Vars.Statics[t.BaseType];

				if (!isBuiltin)
				{
					if (staticInst.op.TryGetValue("__Static", out var __static) && __static.Set != null)
					{
						if (__static.Set is IFuncObj ifo)
							ifo.Call((object)staticInst);
					}
					var nestedTypes = t.GetNestedTypes(BindingFlags.Public);
					foreach (var nestedType in nestedTypes)
					{
						RuntimeHelpers.RunClassConstructor(nestedType.TypeHandle);
						DefineProp(staticInst, nestedType.Name,
							new OwnPropsDesc(staticInst, null,
								new FuncObj((params object[] args) => script.Vars.Statics[nestedType]),
								null,
								new FuncObj((object @this, params object[] args) => Script.Invoke(script.Vars.Statics[nestedType], "Call", args))
							)
						);
					}

					_ = Script.InvokeMeta(staticInst, "__Init");
					_ = Script.InvokeMeta(staticInst, "__New");
				}
				return proto;
			});
        }

		internal static void DefineProp(Any kso, string name, OwnPropsDesc desc)
		{
			if (kso.op == null)
				kso.op = new Dictionary<string, OwnPropsDesc>(StringComparer.OrdinalIgnoreCase);

			if (kso.op.TryGetValue(name, out var existing))
				existing.Merge(desc);
			else
				kso.op[name] = desc;
		}

		public static object Index(object item, params object[] index) => item == null ? null : IndexAt(item, index);

		public static object SetObject(object item, params object[] args)
		{
			object key = null;
			Type typetouse = null;

			if (args == null) args = [null];
			if (args.Length == 0) return Errors.ErrorOccurred($"Attempting to set value on object {item} failed because no value was provided");

			object value = args[^1];

			try
			{
				if (item is ITuple otup && otup.Length > 1)
				{
					if (otup[0] is Type t && otup[1] is object o)
					{
						typetouse = t;
						item = o;
					} else if (otup[0] is Any kso && otup[1] is object ob)
					{
                        item = ob; typetouse = kso.GetType();
                    }
				}
				else
					typetouse = item.GetType();

				if (args.Length == 2)
				{
					key = args[0];

					//This excludes types derived from Array so that super can be used.
					if (typetouse == typeof(Keysharp.Core.Array))
					{
						((Keysharp.Core.Array)item)[key] = value;
						return value;
					}
					else if (typetouse == typeof(Keysharp.Core.Map))
					{
						((Keysharp.Core.Map)item)[key] = value;
						return value;
					}

					var position = (int)ForceLong(key);

					if (item is object[] objarr)
					{
						var actualindex = position < 0 ? objarr.Length + position : position - 1;
						objarr[actualindex] = value;
						return value;
					}
					else if (item is System.Array array)
					{
						var actualindex = position < 0 ? array.Length + position : position - 1;
						array.SetValue(value, actualindex);
						return value;
					}
					else if (item == null)
					{
						return DefaultErrorObject;
					}
				}

				if (item is Any kso2)
				{
					if (TryGetOwnPropsMap(kso2, "__Item", out var opm))
					{
						if (opm.Set != null && opm.Set is IFuncObj ifo)
						{
							_ = ifo.CallInst(kso2, args);
							return value;
						}
						else if (opm.Call != null && opm.Call is IFuncObj ifo2)
						{
							_ = ifo2.CallInst(kso2, args);
							return value;
						}
					}
					else if (TryGetOwnPropsMap(kso2, "__Set", out var opm2) && opm2.Call != null && opm2.Call is IFuncObj ifo2)
					{
						_ = ifo2.Call(kso2, new Keysharp.Core.Array(GetIndices()), value);
						return value;
					}
                }
				else if (Core.Primitive.IsNative(item))
				{
					SetObject((TheScript.Vars.Prototypes[Core.Primitive.MapPrimitiveToNativeType(item)], item), args);
					return value;
				}

#if WINDOWS

				if (item is ComObjArray coa)
				{

					coa[GetIndices()] = value;
					return value;
				}
				else if (item is ComValue co)
				{
					if (args.Length == 1 && (co.vt & VarEnum.VT_BYREF) != 0)
						ComValue.WriteVariant(co.Ptr.Al(), co.vt, value);
					else
						co.Ptr.GetType().InvokeMember("Item", BindingFlags.SetProperty, null, co.Ptr, args);
					return value;
				}
				else if (Marshal.IsComObject(item))
				{
					_ = item.GetType().InvokeMember("Item", BindingFlags.SetProperty, null, item, args); ;
					return value;
				}

#endif
				var il1 = args.Length;

				if (Reflections.FindAndCacheInstanceMethod(typetouse, "set_Item", il1) is MethodPropertyHolder mph2)
				{
					if (il1 == mph2.ParamLength || mph2.IsVariadic)
					{
						_ = mph2.CallFunc(item, args);
						return value;
					}
					else
						return Errors.ValueErrorOccurred($"{il1} arguments were passed to a set indexer which only accepts {mph2.ParamLength}.");
				}
			}
			catch (Exception e)
			{
				if (e.InnerException is KeysharpException ke)
					throw ke;
				else
					throw;
			}

			return Errors.ErrorOccurred($"Attempting to set index {key} of object {item} to value {value} failed.");

			object[] GetIndices()
			{
				object[] indices = new object[args.Length - 1];
				System.Array.Copy(args, indices, indices.Length);
				return indices;
			}
		}

		private static object IndexAt(object item, params object[] index)
		{
			int len;
			object key = null;
			if (index == null) index = [null];

			try
			{
				if (index.Length > 0)
				{
					len = index.Length;
					key = index[0];
				}
				else
					len = 1;

				Any type = item as Any;

                if (item is ITuple otup && otup.Length > 1 && otup[0] is Any t)
				{
					type = t; item = otup[1];
				}

				if (type != null)
				{
					if (TryGetOwnPropsMap(type, "__Item", out var opm, true, OwnPropsMapType.Get) && opm.Get is IFuncObj ifo)
						return ifo.CallInst(item, index);
					else if (TryGetOwnPropsMap(type, "__Get", out var opm2, true, OwnPropsMapType.Call) && opm2.Call is IFuncObj ifo2)
						return ifo2.Call(item, new Keysharp.Core.Array(index));
				} 
				else if (Core.Primitive.IsNative(item))
				{
					return IndexAt((TheScript.Vars.Prototypes[Core.Primitive.MapPrimitiveToNativeType(item)], item), index);
				}

				if (len == 1)
				{
					var position = (int)ForceLong(key);

					//The most common is going to be a string, array, map or buffer.
					if (item is string s)
					{
						var actualindex = position < 0 ? s.Length + position : position - 1;
						return s[actualindex];
					}
					else if (item is object[] objarr)//Used for indexing into variadic function params.
					{
						var actualindex = position < 0 ? objarr.Length + position : position - 1;
						return objarr[actualindex];
					}
					else if (item is System.Array array)
					{
						var actualindex = position < 0 ? array.Length + position : position - 1;
						return array.GetValue(actualindex);
					}
				}

#if WINDOWS

				if (item is ComObjArray coa)
				{
					return coa[index];
				}
				else if (item is ComValue co)
				{
					//Could be an indexer, but MethodPropertyHolder currently doesn't support those
					if (index.Length == 0 && (co.vt & VarEnum.VT_BYREF) != 0)
						return ComValue.ReadVariant(co.Ptr.Al(), co.vt);

					return Invoke((co.Ptr, new ComMethodPropertyHolder("Item")), index);
				}
				else if (Marshal.IsComObject(item))
					return Invoke((item, new ComMethodPropertyHolder("Item")), index);

#endif
			}
			catch (Exception e)
			{
				if (e.InnerException is KeysharpException ke)
					throw ke;
				else
					throw;
			}

			return Errors.ErrorOccurred($"Attempting to get index of {key} on item {item} failed.");
		}
	}
}