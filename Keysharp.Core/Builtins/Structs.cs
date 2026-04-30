namespace Keysharp.Builtins
{
	internal enum StructPrimitiveKind
	{
		None,
		Int8,
		UInt8,
		Int16,
		UInt16,
		Int32,
		UInt32,
		Int64,
		Ptr,
		Float32,
		Float64
	}

	internal sealed class StructFieldInfo(string name, Type fieldType, long offset, long pack, bool hasExplicitOffset)
	{
		public string Name = name;
		public Type FieldType = fieldType;
		public long Offset = offset;
		public long Pack = pack;
		public bool HasExplicitOffset = hasExplicitOffset;
	}

	public class Struct(params object[] args) : Any(args), IDisposable, IPointable
	{
		private sealed class StructInfo
		{
			public readonly List<StructFieldInfo> Fields = new();
			public StructPrimitiveKind PrimitiveKind;
			public long Size;
			public long Alignment = 1;
			public bool SizeAligned;
			public bool FieldsLocked;

			public bool IsPrimitive => PrimitiveKind != StructPrimitiveKind.None;
		}

		private static readonly Lock infoLock = new();
		private static readonly Dictionary<Type, StructInfo> infos = new();
		private static readonly AssemblyBuilder dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Keysharp.DynamicStructs"), AssemblyBuilderAccess.Run);
		private static readonly ModuleBuilder dynamicModule = dynamicAssembly.DefineDynamicModule("Keysharp.DynamicStructs");
		private static readonly Dictionary<Type, Type> pointerTypes = new();
		private static readonly Dictionary<Type, Type> pointerTargets = new();
		private static readonly Dictionary<Type, Type> structBases = new();
		private static int dynamicTypeId;

		private NativeMemoryHandle ownedHandle;
		private long borrowedPtr;
		private bool disposed;
		private Dictionary<string, Struct> nestedViews;

		static Struct()
		{
			lock (infoLock)
			{
				_ = GetOrCreateInfo(typeof(Struct));
				foreach (var (type, kind) in new (Type, StructPrimitiveKind)[]
				{
					(typeof(StructInt8), StructPrimitiveKind.Int8),
					(typeof(StructUInt8), StructPrimitiveKind.UInt8),
					(typeof(StructInt16), StructPrimitiveKind.Int16),
					(typeof(StructUInt16), StructPrimitiveKind.UInt16),
					(typeof(StructInt32), StructPrimitiveKind.Int32),
					(typeof(StructUInt32), StructPrimitiveKind.UInt32),
					(typeof(StructInt64), StructPrimitiveKind.Int64),
					(typeof(StructPtr), StructPrimitiveKind.Ptr),
					(typeof(StructIntPtr), StructPrimitiveKind.Ptr),
					(typeof(StructFloat32), StructPrimitiveKind.Float32),
					(typeof(StructFloat64), StructPrimitiveKind.Float64)
				})
					InitPrimitiveInfo(type, kind);
			}

			pointerTargets[typeof(StructPtr)] = typeof(Struct);
			pointerTypes[typeof(Struct)] = typeof(StructPtr);
		}

		private Type StructType => type ?? GetType();

		public long Ptr => ownedHandle?.DangerousGetHandle().ToInt64() ?? borrowedPtr;

		public object Size => GetCurrentLayoutInfo().Size;

		[PublicHiddenFromUser]
		[UserDeclaredName("Ptr")]
		public class StructPtr(params object[] args) : Struct(args) { }

		public static object staticget_Ptr(object @this) => GetPointerClass(@this);

		public static object staticPtr(object @this, params object[] args) =>
			Script.Invoke(GetPointerClass(@this), "Call", args);

		public static object staticAt(object @this, object address)
		{
			if (@this is not Class cls)
				return Errors.TypeErrorOccurred(@this, typeof(Class));

			if (Script.GetPropertyValueOrNull(cls, "Prototype") is not Any proto)
				return Errors.PropertyErrorOccurred("Struct class does not have a prototype.");

			return CreatePointerView(proto.type, proto, address.Al());
		}

		internal static bool IsAutoPointerClass(Type type) => type != null && pointerTargets.ContainsKey(type);

		internal static bool IsStructType(Type type) =>
			type != null && (typeof(Struct).IsAssignableFrom(type) || infos.ContainsKey(type));

		internal static bool IsStructInstance(object value, Type expectedType = null)
		{
			if (value is not Struct st)
				return false;

			return expectedType == null
				|| IsSameOrDerivedStructType(st.StructType, expectedType)
				|| HasPrototypeType(st, expectedType);
		}

		private static object GetPointerClass(object @this)
		{
			if (@this is not Class cls)
				return Errors.TypeErrorOccurred(@this, typeof(Class));

			if (Script.GetPropertyValueOrNull(cls, "Prototype") is not Any proto)
				return Errors.PropertyErrorOccurred("Struct class does not have a prototype.");

			var structType = GetStructType(proto);

			if (structType == null)
				return Errors.TypeErrorOccurred(@this, typeof(Struct));

			var ptrType = GetPointerType(structType);
			EnsurePointerTypeInitialized(ptrType);
			return Script.TheScript.Vars.Statics[ptrType];
		}

		private static void EnsurePointerTypeInitialized(Type ptrType)
		{
			if (ptrType?.BaseType != null && pointerTargets.ContainsKey(ptrType.BaseType))
				EnsurePointerTypeInitialized(ptrType.BaseType);

			var vars = Script.TheScript.Vars;
			if (vars.Prototypes.ContainsKey(ptrType))
				_ = vars.Prototypes[ptrType];
			else
				Script.InitClass(ptrType);
		}

		internal static Type CreateDynamicStructType(string name, Type baseType)
		{
			if (!IsStructType(baseType))
				throw new InvalidOperationException($"{baseType?.FullName ?? "<null>"} is not a Struct.");

			var clrBaseType = typeof(Struct).IsAssignableFrom(baseType) ? baseType : typeof(Struct);
			var structType = CreateDynamicType(clrBaseType, name, "Struct", name);

			lock (infoLock)
				structBases[structType] = baseType;

			return structType;
		}

		private static Type GetPointerType(Type structType)
		{
			if (!IsStructType(structType))
				return null;

			lock (infoLock)
				if (pointerTypes.TryGetValue(structType, out var ptrType))
					return ptrType;

			var baseStructType = GetBaseStructType(structType) ?? typeof(Struct);
			var basePointerType = GetPointerType(baseStructType);
			var createdType = CreateDynamicType(basePointerType, structType.Name, "Ptr", "Ptr");

			lock (infoLock)
			{
				if (pointerTypes.TryGetValue(structType, out var ptrType))
					return ptrType;

				InitPrimitiveInfo(createdType, StructPrimitiveKind.Ptr);
				pointerTypes[structType] = createdType;
				pointerTargets[createdType] = structType;
				return createdType;
			}
		}

		private static Type CreateDynamicType(Type baseType, string typeName, string fallback, string userDeclaredName)
		{
			typeName = CleanDynamicTypeName(typeName, fallback);
			var uniqueName = $"Keysharp.DynamicStructs.{typeName}_{Interlocked.Increment(ref dynamicTypeId)}";
			var builder = dynamicModule.DefineType(uniqueName, TypeAttributes.Public | TypeAttributes.Class, baseType);

			if (!string.IsNullOrEmpty(userDeclaredName))
				builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(UserDeclaredNameAttribute).GetConstructor([typeof(string)]), [userDeclaredName]));

			var ctor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(object[])]);
			ctor.DefineParameter(1, ParameterAttributes.None, "args");
			var baseCtor = baseType.GetConstructor([typeof(object[])]);

			if (baseCtor == null)
				throw new InvalidOperationException($"{baseType.FullName} does not have the required object[] constructor.");

			var il = ctor.GetILGenerator();
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Call, baseCtor);
			il.Emit(OpCodes.Ret);

			return builder.CreateType();
		}

		private static string CleanDynamicTypeName(string name, string fallback)
		{
			if (string.IsNullOrWhiteSpace(name))
				return fallback;

			var clean = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
			return char.IsDigit(clean[0]) ? "_" + clean : clean;
		}

		private static StructFieldInfo RegisterField(Type structType, string fieldName, Type fieldType, long pack, object offsetSpec, bool ignoreExisting)
		{
			ArgumentNullException.ThrowIfNull(structType);
			ArgumentNullException.ThrowIfNull(fieldType);

			if (!IsStructType(structType))
				throw new InvalidOperationException($"{structType.FullName} is not a Struct.");

			if (!IsStructType(fieldType))
				throw new InvalidOperationException($"{fieldType.FullName} is not a supported struct field type.");

			if (pack is not (0 or 1 or 2 or 4 or 8))
				throw new InvalidOperationException("Struct field Pack must be 0, 1, 2, 4 or 8.");

			EnsureFieldTypeInitialized(fieldType);

			StructFieldInfo field;

			lock (infoLock)
			{
				var info = GetOrCreateInfo(structType);

				if (info.FieldsLocked)
					throw new InvalidOperationException($"Struct metadata for {structType.FullName} has already been finalized.");

				var existing = FindOwnField(structType, fieldName);

				if (existing != null)
				{
					if (ignoreExisting && existing.FieldType == fieldType)
						return existing;

					throw new InvalidOperationException($"Struct field {fieldName} is already registered on {structType.FullName}.");
				}

				if (FindField(structType, fieldName, false) != null)
					throw new InvalidOperationException($"Struct field {fieldName} is already registered on {structType.FullName}.");

				if (HasKnownDerivedStruct(structType))
					throw new InvalidOperationException("Cannot add typed property.");

				UpdateLayout(structType, info, false);

				var fieldInfo = GetLayoutInfo(fieldType, true);
				var alignment = ApplyPack(fieldInfo.Alignment, pack);
				var hasExplicitOffset = TryResolveOffset(structType, offsetSpec, out var offset);

				if (!hasExplicitOffset)
					offset = AlignUp(info.Size, alignment);

				field = new StructFieldInfo(fieldName, fieldType, offset, pack, hasExplicitOffset);

				info.Fields.Add(field);
				info.Size = Math.Max(info.Size, offset + fieldInfo.Size);
				info.Alignment = Math.Max(info.Alignment, alignment);
				info.PrimitiveKind = StructPrimitiveKind.None;
				info.SizeAligned = false;
				MarkDerivedLayoutsDirty(structType);
			}

			return field;
		}

		[PublicHiddenFromUser]
		public void InitializeStructStorage()
		{
			var info = GetCurrentLayoutInfo(true);

			if (Ptr != 0 || info.Size == 0)
				return;

			var handle = Marshal.AllocHGlobal((nint)info.Size);
			unsafe { Unsafe.InitBlockUnaligned((void*)handle, 0, (uint)info.Size); }

			ownedHandle = new NativeMemoryHandle(handle);
		}

		public object Dispose(bool disposing)
		{
			if (disposed)
				return DefaultObject;

			ReleaseOwnedStorage();
			borrowedPtr = 0;
			nestedViews?.Clear();
			disposed = true;
			return DefaultObject;
		}

		void IDisposable.Dispose()
		{
			Dispose(true);
			HasFinalizer = false;
		}

		public object get___Value() => GetValue();
		public object set___Value(object value) => SetPrimitiveValue(value);
		public object get___Item(params object[] indexArgs) => GetPrimitiveItem(indexArgs);
		public object set___Item(params object[] args) => SetPrimitiveItem(args);

		internal static bool TryDefineFieldOnPrototype(Any proto, string fieldName, object descriptor, out object result)
		{
			result = null;

			if (!TryGetDescriptorValue(descriptor, "type", out var typeValue))
				return false;

			if (GetStructType(proto) == null)
				return true;

			if (HasDescriptorValue(descriptor, "value", "get", "set", "call"))
			{
				result = Errors.ValueErrorOccurred("Type can't be defined along with value, get, set, or call.");
				return true;
			}

			var pack = TryGetDescriptorValue(descriptor, "pack", out var packValue) ? packValue.Al() : 0;
			var offsetSpec = TryGetDescriptorValue(descriptor, "offset", out var offsetValue) ? offsetValue : null;
			result = DefineFieldOnPrototype(proto, fieldName, ResolveFieldType(typeValue), pack, offsetSpec);
			return true;
		}

		internal static object DefineFieldOnPrototype(Any proto, string fieldName, Type fieldType, long pack = 0, object offsetSpec = null, bool ignoreExisting = false)
		{
			var structType = GetStructType(proto);

			if (structType == null)
				return null;

			if (fieldType == null)
				return Errors.ValueErrorOccurred("Struct field Type is not a valid struct type.");

			try
			{
				var field = RegisterField(structType, fieldName, fieldType, pack, offsetSpec, ignoreExisting);
				var desc = new OwnPropsDesc(proto);
				desc.SetStructProperty(field);
				var op = proto.EnsureOwnProps();

				if (op.TryGetValue(fieldName, out var existing))
					existing.Merge(desc);
				else
					op[fieldName] = desc;
			}
			catch (Exception ex)
			{
				return Errors.ErrorOccurred(ex.Message);
			}

			return proto;
		}

		internal object GetFieldValue(StructFieldInfo field)
		{
			var address = GetDataPointer() + field.Offset;

			if (pointerTargets.TryGetValue(field.FieldType, out var targetType))
			{
				var ptr = ReadPrimitive(StructPrimitiveKind.Ptr, address).Al();
				return CreatePointerValue(targetType, ptr);
			}

			var fieldInfo = GetLayoutInfo(field.FieldType, true);

			if (fieldInfo.IsPrimitive)
				return ReadPrimitive(fieldInfo.PrimitiveKind, address);

			if (nestedViews?.TryGetValue(field.Name, out var nested) == true && nested.Ptr == address)
				return nested;

			var proto = Script.TheScript.Vars.Prototypes[field.FieldType];
			nested = CreatePointerView(field.FieldType, proto, address);
			nestedViews ??= new(StringComparer.OrdinalIgnoreCase);
			nestedViews[field.Name] = nested;
			return nested;
		}

		internal object SetFieldValue(StructFieldInfo field, object value)
		{
			var address = GetDataPointer() + field.Offset;

			if (pointerTargets.TryGetValue(field.FieldType, out var targetType))
				return SetPointerValue(address, value, targetType, field.FieldType);

			var fieldInfo = GetLayoutInfo(field.FieldType, true);

			if (fieldInfo.IsPrimitive)
			{
				if (IsStructInstance(value) && ((Struct)value).GetCurrentLayoutInfo().IsPrimitive)
					value = ((Struct)value).GetPrimitiveValue();

				WritePrimitive(fieldInfo.PrimitiveKind, address, value);
				return value;
			}

			if (GetFieldValue(field) is Struct nested && HasCustomValueProperty(nested, Script.OwnPropsMapType.Set))
				return Script.SetPropertyValue(nested, "__Value", value);

			return Errors.ErrorOccurred("Assignment to struct is not supported.");
		}

		internal static Struct CreateInstance(Type structType)
		{
			var proto = Script.TheScript.Vars.Prototypes[structType];
			var instance = FastCtor.Call(structType, null) as Struct;
			instance.type = structType;
			instance.SetBaseInternal(proto);
			instance.InitializeStructStorage();
			return instance;
		}

		internal static bool TryResolveClass(object value, out Type structType)
		{
			structType = ResolveFieldType(value);
			return structType != null;
		}

		internal static bool TryResolvePointerClass(object value, out Type pointerType, out Type targetType)
		{
			pointerType = null;
			targetType = null;

			if (!TryResolveClass(value, out pointerType))
				return false;

			return pointerTargets.TryGetValue(pointerType, out targetType);
		}

		internal static long GetSize(Type structType) => GetLayoutInfo(structType, true).Size;

		internal static bool IsPrimitive(Type structType) => GetLayoutInfo(structType, true).IsPrimitive;

		internal static object ReadPrimitiveValue(Type structType, long address)
		{
			var info = GetLayoutInfo(structType, true);

			if (!info.IsPrimitive)
				return Errors.TypeErrorOccurred(structType, typeof(Struct));

			return ReadPrimitive(info.PrimitiveKind, address);
		}

		internal static unsafe long ReadArgumentSlot(Struct value)
		{
			var size = GetLayoutInfo(value.StructType, true).Size;

			if (size > sizeof(long))
				return value.Ptr;

			long slot = 0;
			CopyBytes(value.Ptr, (long)(nint)(&slot), size);
			return slot;
		}

		internal static unsafe void WriteArgumentSlot(Struct value, long slot)
		{
			var size = GetLayoutInfo(value.StructType, true).Size;

			if (size > sizeof(long))
				return;

			CopyBytes((long)(nint)(&slot), value.Ptr, size);
		}

		internal static object GetOutputValue(Struct value) =>
			HasCustomValueProperty(value, Script.OwnPropsMapType.Get | Script.OwnPropsMapType.Value)
				? Script.GetPropertyValue(value, "__Value")
				: IsPrimitive(value.StructType)
					? value.GetPrimitiveValue()
					: value;

		internal static void SetInputValue(Struct value, object input)
		{
			if (!HasCustomValueProperty(value, Script.OwnPropsMapType.Set) && !IsPrimitive(value.StructType))
				_ = Errors.TypeErrorOccurred(input, value.StructType);

			_ = Script.SetPropertyValue(value, "__Value", input);
		}

		private static bool HasCustomValueProperty(Struct value, Script.OwnPropsMapType requiredType)
		{
			for (var current = value._base; current != null; current = current._base)
			{
				if (current.op == null || !current.op.TryGetValue("__Value", out var desc))
					continue;

				if (current.type == typeof(Struct))
					return false;

				if ((desc.Type & requiredType) != 0)
					return true;
			}

			return false;
		}

		internal object GetValue() =>
			pointerTargets.TryGetValue(StructType, out var targetType)
				? CreatePointerValue(targetType, GetPrimitiveValue().Al())
				: GetPrimitiveValue();

		internal object GetPrimitiveValue()
		{
			var info = GetCurrentLayoutInfo();

			if (!info.IsPrimitive)
				return Errors.PropertyErrorOccurred("This struct does not define __Value.");

			return ReadPrimitive(info.PrimitiveKind, GetDataPointer());
		}

		internal object SetPrimitiveValue(object value)
		{
			if (pointerTargets.TryGetValue(StructType, out var targetType))
				return SetPointerValue(GetDataPointer(), value, targetType);

			var info = GetCurrentLayoutInfo();

			if (!info.IsPrimitive)
				return Errors.PropertyErrorOccurred("This struct does not define __Value.");

			WritePrimitive(info.PrimitiveKind, GetDataPointer(), value);
			return value;
		}

		protected object GetPrimitiveItem(params object[] indexArgs) =>
			indexArgs == null || indexArgs.Length == 0
				? GetPrimitiveValue()
				: Errors.PropertyErrorOccurred("This struct does not support indexed [] access.");

		protected object SetPrimitiveItem(params object[] args)
		{
			if (args == null || args.Length != 1)
				return Errors.PropertyErrorOccurred("This struct does not support indexed [] assignment.");

			return SetPrimitiveValue(args[0]);
		}

		private long GetDataPointer()
		{
			var ptr = Ptr;

			if (ptr == 0)
			{
				_ = Errors.ErrorOccurred("Struct storage has not been initialized.");
				throw new Error("Struct storage has not been initialized.");
			}

			return ptr;
		}

		private void BindToPointer(long ptr)
		{
			ReleaseOwnedStorage();
			borrowedPtr = ptr;
			nestedViews?.Clear();
		}

		private static StructInfo GetLayoutInfo(Type type, bool lockFields)
		{
			lock (infoLock)
			{
				var info = GetOrCreateInfo(type);
				UpdateLayout(type, info, lockFields);
				return info;
			}
		}

		private static StructInfo GetOrCreateInfo(Type type) =>
			infos.TryGetValue(type, out var info) ? info : infos[type] = new StructInfo();

		private static void InitPrimitiveInfo(Type type, StructPrimitiveKind kind)
		{
			var info = GetOrCreateInfo(type);
			info.PrimitiveKind = kind;
			info.Size = PrimitiveSize(kind);
			info.Alignment = info.Size == 0 ? 1 : info.Size;
			info.Fields.Clear();
			info.SizeAligned = true;
			info.FieldsLocked = true;
		}

		private static Type GetBaseStructType(Type type) =>
			type == null
				? null
				: structBases.TryGetValue(type, out var baseType)
					? baseType
					: IsStructType(type.BaseType) ? type.BaseType : null;

		private static StructFieldInfo FindField(Type type, string name, bool includeStart)
		{
			for (var current = includeStart ? type : GetBaseStructType(type); IsStructType(current); current = GetBaseStructType(current))
				if (FindOwnField(current, name) is StructFieldInfo field)
					return field;

			return null;
		}

		private static StructFieldInfo FindOwnField(Type type, string name) =>
			GetOrCreateInfo(type).Fields.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

		private static bool HasKnownDerivedStruct(Type type) =>
			infos.Keys.Any(knownType => knownType != type && IsDerivedStructType(knownType, type));

		private static bool IsDerivedStructType(Type type, Type baseType)
		{
			if (type.IsSubclassOf(baseType))
				return true;

			for (var current = GetBaseStructType(type); current != null; current = GetBaseStructType(current))
				if (current == baseType)
					return true;

			return false;
		}

		private static bool IsSameOrDerivedStructType(Type type, Type baseType) =>
			type == baseType || IsDerivedStructType(type, baseType);

		private static bool HasPrototypeType(Struct value, Type type)
		{
			for (var current = value._base; current != null; current = current._base)
				if (current.type == type)
					return true;

			return false;
		}

		private StructInfo GetCurrentLayoutInfo(bool lockFields = false) => GetLayoutInfo(StructType, lockFields);

		private static Struct CreatePointerView(Type viewType, Any proto, long address)
		{
			_ = GetLayoutInfo(viewType, true);
			var instance = FastCtor.Call(viewType, null) as Struct;
			instance.type = viewType;
			instance.SetBaseInternal(proto);
			instance.BindToPointer(address);
			return instance;
		}

		private static object CreatePointerValue(Type targetType, long ptr) =>
			ptr == 0 ? null : CreatePointerView(targetType, Script.TheScript.Vars.Prototypes[targetType], ptr);

		private static object SetPointerValue(long address, object value, Type targetType, Type pointerType = null)
		{
			if (value == null)
			{
				WritePrimitive(StructPrimitiveKind.Ptr, address, 0L);
				return null;
			}

			if (pointerType != null && IsStructInstance(value, pointerType))
				WritePrimitive(StructPrimitiveKind.Ptr, address, ((Struct)value).GetPrimitiveValue());
			else if (IsStructInstance(value, targetType))
				WritePrimitive(StructPrimitiveKind.Ptr, address, ((Struct)value).Ptr);
			else
				return Errors.TypeErrorOccurred(value, targetType);

			return value;
		}

		private void ReleaseOwnedStorage()
		{
			ownedHandle?.Dispose();
			ownedHandle = null;
		}

		private static void UpdateLayout(Type type, StructInfo info, bool lockFields)
		{
			if (!info.SizeAligned || (lockFields && !info.FieldsLocked))
			{
				var size = 0L;
				var alignment = 1L;
				var primitiveKind = StructPrimitiveKind.None;
				var baseType = GetBaseStructType(type);

				if (IsStructType(baseType))
				{
					var baseInfo = GetLayoutInfo(baseType, lockFields);
					size = baseInfo.Size;
					alignment = baseInfo.Alignment;
					primitiveKind = baseInfo.PrimitiveKind;
				}

				foreach (var field in info.Fields)
				{
					var fieldInfo = GetLayoutInfo(field.FieldType, true);
					var fieldAlignment = ApplyPack(fieldInfo.Alignment, field.Pack);

					if (!field.HasExplicitOffset)
					{
						size = AlignUp(size, fieldAlignment);
						field.Offset = size;
					}

					size = Math.Max(size, field.Offset + fieldInfo.Size);
					alignment = Math.Max(alignment, fieldAlignment);
				}

				info.Size = AlignUp(size, alignment);
				info.Alignment = alignment;
				info.PrimitiveKind = info.Fields.Count == 0 ? primitiveKind : StructPrimitiveKind.None;
			}

			info.SizeAligned = true;

			if (lockFields)
				info.FieldsLocked = true;
		}

		private static void MarkDerivedLayoutsDirty(Type baseType)
		{
			foreach (var (type, info) in infos)
				if (type != baseType && IsDerivedStructType(type, baseType))
					info.SizeAligned = false;
		}

		private static Type GetStructType(Any proto)
		{
			if (proto == null || !proto.isPrototype)
				return null;

			for (var current = proto; current != null; current = current._base)
				if (current.type != null && current.type != typeof(Prototype) && IsStructType(current.type))
					return current.type;

			return null;
		}

		private static Type ResolveFieldType(object value)
		{
			if (value is Type type && IsStructType(type))
				return type;

			if (value is Any any)
			{
				if (GetStructType(any) is Type structType)
					return structType;

				if (Script.GetPropertyValueOrNull(any, "Prototype") is Any proto)
					return GetStructType(proto);
			}

			return null;
		}

		private static bool HasDescriptorValue(object descriptor, params string[] names) =>
			names.Any(name => TryGetDescriptorValue(descriptor, name, out _));

		private static bool TryGetDescriptorValue(object descriptor, string name, out object value)
		{
			if (descriptor is Map map && map.map != null)
			{
				foreach (var (key, val) in map.map)
					if (key.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
					{
						value = val;
						return true;
					}
			}
			else if (descriptor is Any any && any.op != null)
			{
				foreach (var (key, desc) in any.op)
					if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
					{
						value = desc.Value ?? desc.Get ?? desc.Set ?? desc.Call;
						return true;
					}
			}

			value = null;
			return false;
		}

		private static bool TryResolveOffset(Type structType, object offsetSpec, out long offset)
		{
			offset = 0;

			if (offsetSpec == null)
				return false;

			if (offsetSpec is string name && !long.TryParse(name, out offset))
			{
				var existing = FindField(structType, name, true);
				if (existing == null)
					throw new InvalidOperationException($"Struct field offset reference {name} was not found.");

				offset = existing.Offset;
				return true;
			}

			offset = offsetSpec.Al();

			if (offset < 0)
				throw new InvalidOperationException("Struct field Offset must not be negative.");

			return true;
		}

		private static void EnsureFieldTypeInitialized(Type type)
		{
			if (type != typeof(Struct) && Script.TheScript?.Vars?.Statics.ContainsKey(type) == true)
				_ = Script.TheScript.Vars.Statics[type];
		}

		private static long PrimitiveSize(StructPrimitiveKind kind) => kind switch
		{
			StructPrimitiveKind.Int8 or StructPrimitiveKind.UInt8 => 1,
			StructPrimitiveKind.Int16 or StructPrimitiveKind.UInt16 => 2,
			StructPrimitiveKind.Int32 or StructPrimitiveKind.UInt32 or StructPrimitiveKind.Float32 => 4,
			StructPrimitiveKind.Int64 or StructPrimitiveKind.Float64 => 8,
			StructPrimitiveKind.Ptr => IntPtr.Size,
			_ => 0
		};

		private static long AlignUp(long value, long alignment) =>
			alignment <= 1 ? value : (value + alignment - 1) & ~(alignment - 1);

		private static long ApplyPack(long alignment, long pack) =>
			pack > 0 ? Math.Min(alignment, pack) : alignment;

		private static unsafe object ReadPrimitive(StructPrimitiveKind kind, long address)
		{
			var ptr = (void*)address;

			return kind switch
			{
				StructPrimitiveKind.Int8 => (long)Unsafe.ReadUnaligned<sbyte>(ptr),
				StructPrimitiveKind.UInt8 => (long)Unsafe.ReadUnaligned<byte>(ptr),
				StructPrimitiveKind.Int16 => (long)Unsafe.ReadUnaligned<short>(ptr),
				StructPrimitiveKind.UInt16 => (long)Unsafe.ReadUnaligned<ushort>(ptr),
				StructPrimitiveKind.Int32 => (long)Unsafe.ReadUnaligned<int>(ptr),
				StructPrimitiveKind.UInt32 => (long)Unsafe.ReadUnaligned<uint>(ptr),
				StructPrimitiveKind.Int64 => Unsafe.ReadUnaligned<long>(ptr),
				StructPrimitiveKind.Ptr => Unsafe.ReadUnaligned<nint>(ptr).ToInt64(),
				StructPrimitiveKind.Float32 => (double)Unsafe.ReadUnaligned<float>(ptr),
				StructPrimitiveKind.Float64 => Unsafe.ReadUnaligned<double>(ptr),
				_ => DefaultObject
			};
		}

		private static unsafe void WritePrimitive(StructPrimitiveKind kind, long address, object value)
		{
			var ptr = (void*)address;

			switch (kind)
			{
				case StructPrimitiveKind.Int8: Unsafe.WriteUnaligned(ptr, unchecked((sbyte)value.Al())); break;
				case StructPrimitiveKind.UInt8: Unsafe.WriteUnaligned(ptr, unchecked((byte)value.Al())); break;
				case StructPrimitiveKind.Int16: Unsafe.WriteUnaligned(ptr, unchecked((short)value.Al())); break;
				case StructPrimitiveKind.UInt16: Unsafe.WriteUnaligned(ptr, unchecked((ushort)value.Al())); break;
				case StructPrimitiveKind.Int32: Unsafe.WriteUnaligned(ptr, unchecked((int)value.Al())); break;
				case StructPrimitiveKind.UInt32: Unsafe.WriteUnaligned(ptr, unchecked((uint)value.Al())); break;
				case StructPrimitiveKind.Int64: Unsafe.WriteUnaligned(ptr, value.Al()); break;
				case StructPrimitiveKind.Ptr: Unsafe.WriteUnaligned(ptr, (nint)value.Al()); break;
				case StructPrimitiveKind.Float32: Unsafe.WriteUnaligned(ptr, value.Af()); break;
				case StructPrimitiveKind.Float64: Unsafe.WriteUnaligned(ptr, value.Ad()); break;
			}
		}

		private static unsafe void CopyBytes(long sourcePtr, long destPtr, long size)
		{
			System.Buffer.MemoryCopy((void*)sourcePtr, (void*)destPtr, size, size);
		}
	}

	[UserDeclaredName("Int8")] public class StructInt8(params object[] args) : Struct(args) { }
	[UserDeclaredName("UInt8")] public class StructUInt8(params object[] args) : Struct(args) { }
	[UserDeclaredName("Int16")] public class StructInt16(params object[] args) : Struct(args) { }
	[UserDeclaredName("UInt16")] public class StructUInt16(params object[] args) : Struct(args) { }
	[UserDeclaredName("Int32")] public class StructInt32(params object[] args) : Struct(args) { }
	[UserDeclaredName("UInt32")] public class StructUInt32(params object[] args) : Struct(args) { }
	[UserDeclaredName("Int64")] public class StructInt64(params object[] args) : Struct(args) { }
	[UserDeclaredName("IntPtr")] public class StructIntPtr(params object[] args) : Struct(args) { }
	[UserDeclaredName("Float32")] public class StructFloat32(params object[] args) : Struct(args) { }
	[UserDeclaredName("Float64")] public class StructFloat64(params object[] args) : Struct(args) { }
}
