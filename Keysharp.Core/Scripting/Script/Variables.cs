using System.Collections.Generic;
using System.Reflection;
using System.Xml.Linq;
using Keysharp.Core.Common.Cryptography;
using Keysharp.Core.Common.Invoke;

namespace Keysharp.Scripting
{
	public class Variables
	{
        public LazyDictionary<Type, Any> Prototypes = new();
		public LazyDictionary<Type, Any> Statics = new();
		private readonly Dictionary<string, Type> classTypesByName = new(StringComparer.OrdinalIgnoreCase);
        internal List<(string, bool)> preloadedDlls = [];
		internal DateTime startTime = DateTime.UtcNow;
		internal readonly Dictionary<string, MethodPropertyHolder> globalVars;
		private readonly OrderedDictionary<Type, Dictionary<string, MethodPropertyHolder>> moduleVars;
		private readonly List<Type> moduleTypes = new();
		private readonly Type defaultModuleType;
		internal Type DefaultModuleType => defaultModuleType;

		public Variables()
		{
			moduleVars = GatherModuleVariables(TheScript.ProgramType);
			if (moduleVars.Count == 0) 
				return;
			defaultModuleType = moduleVars.Keys.FirstOrDefault(t => t.Name.Equals(Keywords.MainModuleName, StringComparison.OrdinalIgnoreCase)) ?? moduleVars.First().Key;
			if (defaultModuleType != null && moduleVars.TryGetValue(defaultModuleType, out var defaultVars))
				globalVars = defaultVars;
			else
				globalVars = GatherTypeVariables(TheScript.ProgramType);
		}

		[Flags]
		internal enum VariableType
		{
			Field = 1,
			Property = 2,
			NormalName = 4,
			SpecialName = 8,
		}

		internal static Dictionary<string, MethodPropertyHolder> GatherTypeVariables(Type t, VariableType vartypes = VariableType.Field | VariableType.Property | VariableType.NormalName, string funcName = null)
		{
			var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
			PropertyInfo[] props = null;
			FieldInfo[] fields = null;

			if (vartypes.HasFlag(VariableType.Field))
				fields = t.GetFields(flags);
			if (vartypes.HasFlag(VariableType.Property))
				props = t.GetProperties(flags);

			var vars = new Dictionary<string, MethodPropertyHolder>((fields?.Length ?? 0) + (props?.Length ?? 0), StringComparer.OrdinalIgnoreCase);

			bool wantnormal = vartypes.HasFlag(VariableType.NormalName);
			bool wantspecial = vartypes.HasFlag(VariableType.SpecialName);

			if (vartypes.HasFlag(VariableType.Field))
			{
				foreach (var field in fields)
				{
					string name = field.Name;
					if (name.StartsWith("@", StringComparison.Ordinal)) name = name.Substring(1);
					if (name.StartsWith(Keywords.EscapePrefix)) name = name.Substring(Keywords.EscapePrefix.Length);
					if (name.StartsWith(Keywords.StaticLocalFieldPrefix))
					{
						if (wantnormal) continue;
						name = ExtractStaticLocalUserName(name, funcName);
						if (name == null) continue;
					}
					else if (IsSpecialName(name))
					{
						if (wantnormal) continue;
					}
					else if (wantspecial) continue;
					if (field.GetCustomAttribute<PublicHiddenFromUser>() != null) continue;
					vars[name] = MethodPropertyHolder.GetOrAdd(field);
				}
			}

			if (vartypes.HasFlag(VariableType.Property))
			{ 
				foreach (var prop in props)
				{
					var name = prop.Name;
					if (name.StartsWith("@", StringComparison.Ordinal)) name = name.Substring(1);
					bool isSpecial = IsSpecialName(name);
					if (wantspecial && !isSpecial) continue;
					if (wantnormal && isSpecial) continue;
					if (prop.GetCustomAttribute<PublicHiddenFromUser>() != null) continue;
					vars[name] = MethodPropertyHolder.GetOrAdd(prop);
				}
			}

			return vars;
		}

		internal static bool IsSpecialName(string name) => name.Length > 3 && char.IsAsciiLetterUpper(name[0]) && char.IsAsciiLetterUpper(name[1]) && name[2] == '_';

		private static bool IsModuleType(Type type) =>
			typeof(Module).IsAssignableFrom(type);

		internal Type ResolveModuleType(Type type)
		{
			for (var cur = type; cur != null; cur = cur.DeclaringType)
			{
				if (IsModuleType(cur))
					return cur;
			}

			return defaultModuleType;
		}

		private static OrderedDictionary<Type, Dictionary<string, MethodPropertyHolder>> GatherModuleVariables(Type programType)
		{
			var map = new OrderedDictionary<Type, Dictionary<string, MethodPropertyHolder>>();
			if (programType == null)
				return map;

			var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
			foreach (var nested in programType.GetNestedTypes(flags).Where(IsModuleType))
				map[nested] = GatherTypeVariables(nested);

			return map;
		}

		internal Dictionary<string, MethodPropertyHolder> GetModuleVars(Type moduleType)
		{
			if (moduleType == null)
				return globalVars;

			if (moduleVars.TryGetValue(moduleType, out var vars))
				return vars;

			vars = GatherTypeVariables(moduleType);
			moduleVars[moduleType] = vars;
			return vars;
		}

		internal static string ExtractStaticLocalUserName(string staticFieldName, string funcName = null)
		{
			var s = staticFieldName.TrimStart('@');

			// Expect: SL_<lenF>_<func>_<var>
			// Example: SL_7_my__func___a

			var start = s.IndexOf(Keywords.StaticLocalFieldPrefix, StringComparison.Ordinal);

			if (start == -1)
				return null;

			int i = start + Keywords.StaticLocalFieldPrefix.Length;

			// read lenF
			int lenF = 0;
			while (i < s.Length && char.IsDigit(s[i]))
			{
				lenF = (lenF * 10) + (s[i] - '0');
				i++;
			}
			if (i >= s.Length || s[i] != '_') return null;
			i++; // skip '_'

			if (i + lenF > s.Length) return null;
			if (funcName != null)
			{
				var funcInField = s.Substring(i, lenF);
				if (!string.Equals(funcInField, funcName, StringComparison.OrdinalIgnoreCase))
					return null; // function name doesn't match
			}
			i += lenF;
			if (i >= s.Length || s[i] != '_') return null;
			i++; // skip '_'

			if (i >= s.Length) return null;

			return s.Substring(i);
		}

		public void InitClasses()
		{
			var anyType = typeof(Any);
			var script = Script.TheScript;
			var types = script.ReflectionsData.stringToTypes.Values
				.Where(type => type.IsClass && !type.IsAbstract && anyType.IsAssignableFrom(type));

			if (script.ProgramType != null)
			{
				var nested = Reflections.GetNestedTypes(script.ProgramType.GetNestedTypes()).Where(type => type.IsClass && anyType.IsAssignableFrom(type));
				types = types.Concat(nested);
			}
			CacheClassTypeNames(types);

			/*
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => type.IsClass && !type.IsAbstract && anyType.IsAssignableFrom(type));
			*/

			// Initiate necessary base types in specific order
			InitClass(typeof(FuncObj));
			// Need to do this so that FuncObj methods contain themselves in the prototype,
			// meaning a circular reference. This shouldn't prevent garbage collection, but
			// I haven't verified that.
			var fop = Prototypes[typeof(FuncObj)];
			foreach (var op in fop.op)
			{
				var opm = op.Value;
				if (opm.Value is FuncObj fov && fov != null)
				{
					fov.SetBaseInternal(fop);
				}
				if (opm.Get is FuncObj fog && fog != null)
				{
					fog.SetBaseInternal(fop);
				}
				if (opm.Set is FuncObj fos && fos != null)
				{
					fos.SetBaseInternal(fop);
				}
				if (opm.Call is FuncObj foc && foc != null)
				{
					foc.SetBaseInternal(fop);
				}
			}
			InitClass(typeof(Any));
			InitClass(typeof(KeysharpObject));
			InitClass(typeof(Class));

			// Class.Base == Object
			Statics[typeof(Class)].SetBaseInternal(Statics[typeof(KeysharpObject)]);
			// Any.Base == Class.Prototype
			Statics[typeof(Any)].SetBaseInternal(Prototypes[typeof(Class)]);

			// Remove __New because it's only for internal overrides
			Prototypes[typeof(Any)].op.Remove("__New");

			// Manually define Object static instance prototype property to be the Object prototype
			var ksoStatic = Statics[typeof(KeysharpObject)];
			ksoStatic.DefinePropInternal("Prototype", new OwnPropsDesc(ksoStatic, Prototypes[typeof(KeysharpObject)]));
			// Object.Base == Any
			ksoStatic.SetBaseInternal(Statics[typeof(Any)]);

			//FuncObj was initialized when Object wasn't, so define the bases
			Prototypes[typeof(FuncObj)].SetBaseInternal(Prototypes[typeof(KeysharpObject)]);
			Statics[typeof(FuncObj)].SetBaseInternal(Statics[typeof(KeysharpObject)]);

			// Do not initialize the core types again
			var typesToRemoveSet = new HashSet<Type>(new[] { typeof(Any), typeof(FuncObj), typeof(KeysharpObject), typeof(Class) });
			var orderedTypes = types.Where(type => !typesToRemoveSet.Contains(type)).OrderBy(Reflections.GetInheritanceDepth);

			// Lazy-initialize all other classes
			foreach (var t in orderedTypes)
			{
				Script.InitClass(t);
			}
		}

		private void CacheClassTypeNames(IEnumerable<Type> types)
		{
			foreach (var type in types)
			{
				var name = Script.GetUserDeclaredName(type) ?? type.Name;
				if (!string.IsNullOrEmpty(name))
					classTypesByName[name] = type;

				if (Keywords.TypeNameAliases.TryGetValue(type.Name, out var alias))
					classTypesByName[alias] = type;
			}
		}

		private bool TryGetClassValue(string key, out object value)
		{
			value = null;
			if (!classTypesByName.TryGetValue(key, out var type))
				return false;

			if (Statics.TryGetValue(type, out var staticObj))
			{
				value = staticObj;
				return true;
			}

			return false;
		}

		public bool HasVariable(string key) =>
			globalVars.ContainsKey(key)
			|| Script.TheScript.ReflectionsData.flatPublicStaticProperties.ContainsKey(key)
			|| Script.TheScript.ReflectionsData.flatPublicStaticMethods.ContainsKey(key);

		public bool HasVariable(Type moduleType, string key)
		{
			var vars = GetModuleVars(moduleType);
			return vars.ContainsKey(key)
				|| Script.TheScript.ReflectionsData.flatPublicStaticProperties.ContainsKey(key)
				|| Script.TheScript.ReflectionsData.flatPublicStaticMethods.ContainsKey(key);
		}

		public object GetVariable(string key)
		{
			if (globalVars.TryGetValue(key, out var mph))
				return mph.CallFunc(null, null);

			var rv = GetReservedVariable(key); // Try reserved variable first, to take precedence over IFuncObj
			if (rv != null)
				return rv;

			if (TryGetClassValue(key, out var classValue))
				return classValue;

			return Functions.GetFuncObj(key, null);

        }

		public object GetVariable(Type moduleType, string key, bool exportsOnly = false)
		{
			var vars = GetModuleVars(moduleType);
			if (vars.TryGetValue(key, out var mph) && (!exportsOnly || mph.IsExported))
				return mph.CallFunc(null, null);

			var rv = GetReservedVariable(key);
			if (rv != null)
				return rv;

			if (TryGetClassValue(key, out var classValue))
				return classValue;

			return Functions.Func(key, moduleType);
		}

		public object SetVariable(string key, object value)
		{
			if (globalVars.TryGetValue(key, out var mph))
				SetMemberValue(mph, value);
			else
				_ = SetReservedVariable(key, value);

			return value;
		}

		public object SetVariable(Type moduleType, string key, object value)
		{
			var vars = GetModuleVars(moduleType);
			if (vars.TryGetValue(key, out var mph))
				SetMemberValue(mph, value);
			else
				_ = SetReservedVariable(key, value);

			return value;
		}

		private static void SetMemberValue(MethodPropertyHolder mph, object value)
		{
			if (mph.SetProp != null)
			{
				mph.SetProp(null, value);
				return;
			}

			if (mph.pi != null)
			{
				mph.pi.SetValue(null, value);
				return;
			}

			if (mph.fi != null)
			{
				mph.fi.SetValue(null, value);
			}
		}

		private PropertyInfo FindReservedVariable(string name)
		{
			_ = Script.TheScript.ReflectionsData.flatPublicStaticProperties.TryGetValue(name, out var prop);
			return prop;
		}

		private object GetReservedVariable(string name)
		{
			var prop = FindReservedVariable(name);
			return prop == null || !prop.CanRead ? null : prop.GetValue(null);
		}

		private bool SetReservedVariable(string name, object value)
		{
			var prop = FindReservedVariable(name);
			var set = prop != null && prop.CanWrite;

			if (set)
			{
				value = Script.ForceType(prop.PropertyType, value);
				prop.SetValue(null, value);
			}

			return set;
		}



		public object this[object key]
        {
			get => TryGetPropertyValue(out object val, key, "__Value") ? val : GetVariable(key.ToString()) ?? "";
			set => _ = (key is KeysharpObject kso && Functions.HasProp(kso, "__Value") == 1) ? Script.SetPropertyValue(kso, "__Value", value) : SetVariable(key.ToString(), value);
		}

			public class Dereference
			{
            private readonly Dictionary<string, object> locals = new(StringComparer.OrdinalIgnoreCase);
			private eScope scope = eScope.Local;
			private HashSet<string> globals;
			private Dictionary<string, MethodPropertyHolder> statics = null;
			private readonly Type moduleType;
            public Dereference(eScope funcScope, string funcName, Type parentType, string[] funcGlobals, params object[] args)
			{
				scope = funcScope;
				globals = funcGlobals == null ? null : new HashSet<string>(funcGlobals, StringComparer.OrdinalIgnoreCase);
				statics = GatherTypeVariables(parentType, VariableType.Field | VariableType.SpecialName, funcName);
				moduleType = Script.TheScript.Vars.ResolveModuleType(parentType);

				for (int i = 0; i < args.Length; i += 2)
				{
					if (args[i] is string varName)
					{
						locals[varName] = args[i + 1];
					}
				}
			}

            public object this[object key]
			{
				get
				{
					if (key is KeysharpObject)
						return GetPropertyValue(key, "__Value");
					if (locals.TryGetValue(key.ToString(), out var val))
						return GetPropertyValue(val, "__Value");
					else if (statics.TryGetValue(key.ToString(), out var mph))
						return mph.CallFunc(null, null);
					return Script.TheScript.Vars.GetVariable(moduleType, key.ToString());
				}
				set
				{
					if (key is KeysharpObject)
					{
						SetPropertyValue(key, "__Value", value);
						return;
					}

					var s = key.ToString();
					if (locals.TryGetValue(s, out var val))
					{
						SetPropertyValue(val, "__Value", value);
						return;
					}
					else if (statics.TryGetValue(s, out var mph))
					{
						SetMemberValue(mph, value);
						return;
					}
					if ((scope == eScope.Global || (globals?.Contains(s) ?? false)) && Script.TheScript.Vars.HasVariable(moduleType, s))
					{
						Script.TheScript.Vars.SetVariable(moduleType, s, value);
						return;
					}

					locals[s] = new VarRef(null);
                }
			}
        }
		}
}
