using Keysharp.Builtins;
using StringBuffer = Keysharp.Builtins.Ks.StringBuffer;

namespace Keysharp.Internals.Invoke
{
	internal class ComArgumentHelper : ArgumentHelper
	{
		internal ComArgumentHelper(object[] parameters) : base(parameters)
		{
			isCom = true;
		}
	}
	internal class ArgumentHelper : IDisposable
	{
		protected bool isCom = false;
		protected bool hresult = false;
		private List<GCHandle> _gcHandles;
		protected List<GCHandle> gcHandles => _gcHandles ??= [];
		protected bool hasReturn = false;
		protected Type returnType = typeof(int);
		private Func<long, object> structReturnConverter;
		//int is the index in the argument list, and bool specifies if it's a VarRef (false) or Ptr (true)
		protected Dictionary<int, (Type, bool)> outputVars;
		internal Dictionary<int, (Type, bool)> OutputVars => outputVars ??= [];
		internal long[] args;
		// contains bitwise info about the location of float and double type arguments, as well as the return type
		// bit i = 1 if argTypes[i] is float or double
		// bit n = 1 if returnType is float or double
		internal ulong floatingTypeMask = 0;

		// Storage for pinned BSTR pointers, to be released at disposal
		private readonly List<nint> _bstrs = [];
		private bool _isDisposed;

		internal ArgumentHelper(object[] parameters)
		{
			ConvertParameters(parameters);
		}

		public void Dispose()
		{
			if (!_isDisposed)
			{
				// free BSTRs
				for (int i = 0; i < _bstrs.Count; i++)
					Marshal.FreeBSTR(_bstrs[i]);

				// free GCHandles
				for (int i = 0; i < gcHandles?.Count; i++)
					_gcHandles[i].Free();

				_isDisposed = true;
			}
		}

		protected unsafe void ConvertParameters(object[] parameters)
		{
			Type type = null;
			int paramCount = parameters.Length;
			hasReturn = (paramCount & 1) != 0;
			int lastIdx = paramCount - 1;
			int argCount = paramCount / 2;

			args = new long[argCount];
			object p = null;
			int n = -1;
			void SetupPointerArg()
			{
				var gch = GCHandle.Alloc(p, GCHandleType.Pinned);
				gcHandles.Add(gch);
				args[n] = gch.AddrOfPinnedObject();
			}

			for (int paramIndex = 0; paramIndex < paramCount; paramIndex++)
			{
				bool isReturn = hasReturn && paramIndex == lastIdx;
				bool parseType = isReturn;
				bool isStructPointerArg = false;
				Type pointerType = null;
				Type targetType = null;
				// Read the tag and value
				object rawTag = parameters[paramIndex++];
				string tag = rawTag as string;

				if (tag == null)
				{
					if (isReturn && TrySetStructReturn(rawTag))
						continue;

					if (!isReturn)
					{
						if (Struct.TryResolvePointerClass(rawTag, out pointerType, out targetType))
						{
							tag = "ptr";
							isStructPointerArg = true;
						}
						else if (Struct.TryResolveClass(rawTag, out var structType))
						{
							n++;
							p = parameters[paramIndex];
							args[n] = ReadStructValueArg(p, structType);
							continue;
						}
					}

					tag ??= string.Empty;
				}

				// Trim whitespace around tag
				ReadOnlySpan<char> span = tag.AsSpan().Trim();
				int len = span.Length;

				if (len == 0)
					goto InvalidType;

				// Lowercase-first-char for fast case-insensitive dispatch
				char c0 = (char)(span[0] | 0x20);
				// Check for pointer suffix: '*' or 'P'/'p'
				char last = span[len - 1];

				if (isReturn)
				{
					if (c0 == 'c' && len >= 5
							&& ((span[1] | 0x20) == 'd')
							&& ((span[2] | 0x20) == 'e')
							&& ((span[3] | 0x20) == 'c')
							&& ((span[4] | 0x20) == 'l'))
					{
						span = span[5..].TrimStart();
						len = span.Length;

						if (len == 0)
						{
							hasReturn = false;
							break;
						}

						c0 = (char)(span[0] | 0x20);
						len = span.Length;
						last = span[len - 1];
					}
				}
				var hasPtrSuffix = last == '*' || (char)(last | 0x20) == 'p';
				var isPtr = IsPtrType(span);
				var isPtrByRef = hasPtrSuffix && IsPtrType(span[..^1]);

				if (!isReturn)
				{
					n++;
					p = parameters[paramIndex];

					if (isStructPointerArg)
						p = NormalizeStructPointerArg(parameters, p, paramIndex, pointerType, targetType);

					if (p is Any kso)
					{
						object kptr;

						if ((isPtr || isPtrByRef) && ((kso is IPointable ip && (kptr = ip.Ptr) != null)
							|| (kptr = Script.GetPropertyValueOrNull(kso, "ptr")) != null))
						{
							if (hasPtrSuffix)
								OutputVars[paramIndex] = (typeof(nint), true);

							p = kptr;
						}
					}
				}

				if (hasPtrSuffix)
				{
					// Remove the suffix
					span = span[..--len];

					if (p is Any kso
						&& !isPtrByRef
						&& !OutputVars.ContainsKey(paramIndex) //must not be a Ptr object
						&& Script.GetPropertyValueOrNull(kso, "__Value") is object kptr)
						p = kptr;

					// Pin the object and store its address
					object temp = 0L;

					if (p is long ll)
						temp = ll;
					else if (p is bool bl)
						temp = bl;
					else if (p is double d)
						temp = d;

					p = temp;
					SetupPointerArg();
					// Determine the type only
					parseType = true;
				}

				// BSTR
				if (c0 == 'b' && len == 4
						&& ((span[1] | 0x20) == 's')
						&& ((span[2] | 0x20) == 't')
						&& ((span[3] | 0x20) == 'r'))
				{
					if (parseType)
					{
						type = typeof(string);
						goto TypeDetermined;
					}

					if (p is string s)
					{
						nint bstr = Marshal.StringToBSTR(s);
						_bstrs.Add(bstr);
						args[n] = bstr;
					}
					else if (p is StringBuffer sb)
					{
						parameters[paramIndex] = sb;
						sb.UpdateBufferFromEntangledString();
						args[n] = sb.Ptr;
					}
					else
					{
						_ = Errors.TypeErrorOccurred(tag, typeof(string));
						return;
					}

					continue;
				}
				// WSTR or STR
				else if ((c0 == 'w' && len == 4 && ((span[1] | 0x20) == 's') && ((span[2] | 0x20) == 't') && ((span[3] | 0x20) == 'r'))
						 || (c0 == 's' && len == 3 && ((span[1] | 0x20) == 't') && ((span[2] | 0x20) == 'r')))
				{
					if (parseType)
					{
						type = isReturn ? typeof(string) : typeof(nint);
						goto TypeDetermined;
					}

					// Special case for strings passed by reference but not with "str*", since strings are always by reference
					if (p is Any kso2 && Script.GetPropertyValueOrNull(kso2, "__Value") is object kptr)
					{
						OutputVars[paramIndex] = (typeof(nint), false);
						p = kptr;
					}

					if (p is string s)
					{
						if (OutputVars.ContainsKey(paramIndex) && parameters[paramIndex] is Any kso)
						{
							var sb = new StringBuffer(s);
							gcHandles.Add(GCHandle.Alloc(sb, GCHandleType.Normal));
							sb.EntangledString = kso;
							parameters[paramIndex] = sb;
							args[n] = sb.Ptr;
						}
						else
							SetupPointerArg();
					}
					else if (p is StringBuffer sb)
					{
						parameters[paramIndex] = sb;
						sb.UpdateBufferFromEntangledString();
						args[n] = sb.Ptr;
					}
					else
					{
						_ = Errors.TypeErrorOccurred(tag, typeof(string));
						return;
					}

					continue;
				}

				// ASTR
				if (c0 == 'a' && len == 4
						&& ((span[1] | 0x20) == 's')
						&& ((span[2] | 0x20) == 't')
						&& ((span[3] | 0x20) == 'r'))
				{
					if (parseType)
					{
						type = isReturn ? typeof(char[]) : typeof(nint);
						goto TypeDetermined;
					}
					if (p is Any kso2 && Script.GetPropertyValueOrNull(kso2, "__Value") is object kptr)
					{
						OutputVars[paramIndex] = (typeof(nint), false);
						p = kptr;
					}

					if (p is string s)
					{
						if (OutputVars.ContainsKey(paramIndex) && parameters[paramIndex] is Any kso)
						{
							var sb = new StringBuffer(s, null, "ANSI");
							gcHandles.Add(GCHandle.Alloc(sb, GCHandleType.Normal));
							sb.EntangledString = kso;
							parameters[paramIndex] = sb;
							args[n] = sb.Ptr;
						}
						else
						{
							p = Encoding.ASCII.GetBytes(s);
							SetupPointerArg();
						}
					}
					else if (p is StringBuffer sb)
					{
						parameters[paramIndex] = sb;
						sb.UpdateBufferFromEntangledString();
						args[n] = sb.Ptr;
					}
					else
					{
						_ = Errors.TypeErrorOccurred(tag, typeof(string));
						return;
					}

					continue;
				}

				// Numeric and pointer types
				switch (c0)
				{
					case 'p':
						if (len == 3
								&& ((span[1] | 0x20) == 't')
								&& ((span[2] | 0x20) == 'r'))
						{
							if (parseType)
							{
								type = typeof(nint);
								goto TypeDetermined;
							}

							ConvertPtr();
							continue;
						}

						break;

					case 'i': // INT or INT64
						if (len == 5 && span[3] == '6' && span[4] == '4') // "int64"
						{
							if (parseType)
							{
								type = typeof(long);
								goto TypeDetermined;
							}

							args[n] = p.Al();
							continue;
						}
						else if (len == 3) // "int"
						{
							if (parseType)
							{
								type = typeof(int);
								goto TypeDetermined;
							}

							args[n] = p.Ai();
							continue;
						}

						break;

					case 'h': // HRESULT
						if (len == 7) // "hresult"
						{
							if (parseType)
							{
								hresult = true;

								if (isReturn)
									hasReturn = false; // needed for ComCall OSError

								type = typeof(int);
								goto TypeDetermined;
							}

							args[n] = p.Ai();
							continue;
						}

						break;

					case 'u': // UINT, USHORT, UCHAR, UPTR
						char c1u = (char)(span[1] | 0x20);

						if (c1u == 'i')
						{
							if (len == 6) // "uint64"
							{
								if (parseType)
								{
									type = typeof(ulong);
									goto TypeDetermined;
								}

								args[n] = p.Al();
								continue;
							}
							else if (len == 4) // "uint"
							{
								if (parseType)
								{
									type = typeof(uint);
									goto TypeDetermined;
								}

								args[n] = p.Aui();
								continue;
							}
						}
						else if (c1u == 's' && len == 6) // "ushort"
						{
							if (parseType)
							{
								type = typeof(ushort);
								goto TypeDetermined;
							}

							args[n] = (ushort)p.Al();
							continue;
						}
						else if (c1u == 'c' && len == 5) // "uchar"
						{
							if (parseType)
							{
								type = typeof(byte);
								goto TypeDetermined;
							}

							args[n] = (byte)p.Al();
							continue;
						}
						else if (c1u == 'p' && len == 4) // "uptr"
						{
							if (parseType)
							{
								type = typeof(nint);
								goto TypeDetermined;
							}

							ConvertPtr();
							continue;
						}

						break;

					case 's': // SHORT
						if (len == 5) // "short"
						{
							if (parseType)
							{
								type = typeof(short);
								goto TypeDetermined;
							}

							args[n] = (short)p.Al();
							continue;
						}

						break;

					case 'c': // CHAR
						if (len == 4) // "char"
						{
							if (parseType)
							{
								type = typeof(sbyte);
								goto TypeDetermined;
							}

							args[n] = (sbyte)p.Al();
							continue;
						}

						break;

					case 'f': // FLOAT
						if (len == 5) // "float"
						{
							if (parseType)
							{
								type = typeof(float);
								goto TypeDetermined;
							}

							floatingTypeMask |= 1UL << n;
							float f = p.Af();
							args[n] = *(int*)&f;
							continue;
						}

						break;

					case 'd': // DOUBLE
						if (len == 6) // "double"
						{
							if (parseType)
							{
								type = typeof(double);
								goto TypeDetermined;
							}

							floatingTypeMask |= 1UL << n;
							double d = p.Ad();
							args[n] = *(long*)&d;
							continue;
						}

						break;
				}

				InvalidType:
				// Invalid type tag
				_ = Errors.ValueErrorOccurred($"Arg or return type of {tag} is invalid.");
				TypeDetermined:

				if (isReturn)
				{
					returnType = type;

					if (type == typeof(float) || type == typeof(double))
						floatingTypeMask |= 1UL << (n + 1);
				}
				else
					OutputVars[paramIndex] = (type, outputVars.ContainsKey(paramIndex));
			}

			void ConvertPtr()
			{
				if (p is long lptr)
					args[n] = lptr;
				else if (p is string s)
					args[n] = s.Al();
				else if (p is IPointable ip)
					args[n] = ip.Ptr;
				else if (p is Keysharp.Builtins.Array arrPtr)
				{
					SetupPointerArg();
				}
#if WINDOWS
				else if (Marshal.IsComObject(p))
				{
					var pUnk = Marshal.GetIUnknownForObject(p);
					args[n] = pUnk;
					_ = Marshal.Release(pUnk);
				}
#endif
				else
				{
					SetupPointerArg();
				}
			}

		}

		private static bool IsPtrType(ReadOnlySpan<char> span) =>
			span.Equals("ptr", StringComparison.OrdinalIgnoreCase)
			|| span.Equals("uptr", StringComparison.OrdinalIgnoreCase);

		private bool TrySetStructReturn(object rawReturnType)
		{
			if (Struct.TryResolvePointerClass(rawReturnType, out _, out var pointerTargetType))
			{
				returnType = typeof(nint);
				structReturnConverter = ptr => Struct.IsPrimitive(pointerTargetType)
					? Struct.ReadPrimitiveValue(pointerTargetType, ptr)
					: ptr == 0 ? null : Script.Invoke(Script.TheScript.Vars.Statics[pointerTargetType], "At", ptr);
				return true;
			}

			if (!Struct.TryResolveClass(rawReturnType, out var structType))
				return false;

			returnType = typeof(nint);

			if (Struct.GetSize(structType) > sizeof(long))
				_ = Errors.ValueErrorOccurred("Struct return values larger than 8 bytes are not supported yet.");

			structReturnConverter = slot =>
			{
				var result = Struct.CreateInstance(structType);
				Struct.WriteArgumentSlot(result, slot);
				return result;
			};
			return true;
		}

		private static long ReadStructValueArg(object input, Type structType)
		{
			var value = GetStructValue(structType, input);
			var size = Struct.GetSize(structType);

			if (size > sizeof(long))
				return (long)Errors.ValueErrorOccurred("Struct arguments larger than 8 bytes are not supported yet.", null, 0L);

			return Struct.ReadArgumentSlot(value);
		}

		private object NormalizeStructPointerArg(object[] parameters, object value, int paramIndex, Type pointerType, Type targetType)
		{
			var targetRef = value as VarRef;
			var input = targetRef != null ? targetRef.__Value : value;
			var hasInput = input != null;

			if (!hasInput && targetRef == null)
				return 0L;

			if (hasInput && Struct.IsStructInstance(input, pointerType))
			{
				var pointerValue = (Struct)input;
				return pointerValue.GetPrimitiveValue();
			}

			var structValue = GetStructValue(targetType, input);

			if (targetRef != null)
			{
				parameters[paramIndex] = new VarRef(() => structValue, value => targetRef.__Value = value);
				OutputVars[paramIndex] = (typeof(Struct), false);
			}
			else if (!ReferenceEquals(structValue, input))
				parameters[paramIndex] = structValue;

			return structValue;
		}

		private static Struct GetStructValue(Type structType, object input)
		{
			if (Struct.IsStructInstance(input, structType))
				return (Struct)input;

			var value = Struct.CreateInstance(structType);

			if (input != null)
				Struct.SetInputValue(value, input);

			return value;
		}

		internal unsafe object ConvertReturnValue(object value)
		{
			if (structReturnConverter != null)
				return structReturnConverter(((long)value).Al());

			// If the return type was omitted then it should be treated as HRESULT
			// and if that is a negative value then throw an OSError
			if (hresult || (isCom && !hasReturn))
			{
				long hrLong = (long)value;                // unbox the raw long
				int hr32 = unchecked((int)hrLong);   // keep only the low 32 bits
				return Errors.OSErrorOccurredForHR(hr32);
			}
			//Special conversion for the return value.
			else if (returnType == typeof(int))
			{
				long l = (long)value;
				int ii = *(int*)&l;
				value = (long)ii;
			}
			else if (returnType == typeof(float))
			{
				if (value is not double) return _ = Errors.TypeErrorOccurred(value, typeof(double));

				double d = (double)value;
				float f = *(float*)&d;
				return (double)f;
			}
			else if (returnType == typeof(char[]))
			{
				nint ptr = (nint)(long)value;
				var str = Marshal.PtrToStringAnsi(ptr);
				Marshal.FreeHGlobal(ptr);
				return str;
			}
			else if (returnType == typeof(string))
			{
				var str = Marshal.PtrToStringUni((nint)(long)value);
				_ = Objects.ObjFree(value);//If this string came from us, it will be freed, else no action.
				return str;
			}

			return value;
		}
	}
}
