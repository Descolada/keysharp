using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Keysharp.Core.Windows;

namespace Keysharp.Scripting
{
	/// <summary>
	/// Base abstract class for script values (long, double, string, object), with per-instance lazy caches.
	/// </summary>
	[TypeConverter(typeof(ValTypeConverter))]
	public abstract class Val : IConvertible
	{
		/// <summary>Used as a secondary container for any accompanying objects such
		/// as an IPointable for LongVal or StringBuffer for StringVal.</summary>
		public object Payload;

		public static Val True = LongCache.Get(1);
		public static Val False = LongCache.Get(0);

		/// <summary>Factory methods for each subtype.</summary>
		public static Val From(long v) => LongCache.Get(v);
		public static Val From(double v) => new DoubleVal(v);
		public static Val From(string v) => new StringVal(v);
		public static Val From(bool v) => v ? True : False;
		public static Val From(Any v) => new AnyVal(v);
		public static Val From(object v) => v is Any a ? new AnyVal(a) : new ObjectVal(v);

		/// <summary>Implicit conversions</summary>
		public static implicit operator Val(long v) => From(v);
		public static implicit operator Val(double v) => From(v);
		public static implicit operator Val(string v) => From(v);
		public static implicit operator Val(bool v) => From(v);
		public static implicit operator Val(Any v) => From(v);
		public static implicit operator Val(Error v) => From(v);

		/// <summary>Try to get a long. Succeeds only if the value is a pure long
		/// or a string in pure long format.</summary>
		public abstract bool TryGetLong(out long value);
		/// <summary>Try to get a double. Returns false if conversion is not possible,
		/// meaning when it's not a pure long, double, or a string in long or double format.</summary>
		public abstract bool TryGetDouble(out double value);
		/// <summary>Try to get a string. Returns false if conversion is not possible.</summary>
		public abstract bool TryGetString(out string value);
		/// <summary>Always returns the underlying object or boxed primitive.</summary>
		public abstract object AsObject();
		/// <summary>Always returns whether the value evaluates to true.</summary>
		public abstract bool AsBool();
		/// <summary>Returns whether the value is a long, double, or string.</summary>
		public abstract bool IsPrimitive { get; }

		// Used as a temporary variable to throw errors.
		static Error _err;

		public static Val Add(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of addition", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of addition", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return new LongVal(la + lb, a.Payload ?? b.Payload);
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return da + db;
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Val)DefaultErrorObject;
		}

		public static Val Subtract(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of substraction", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of substraction", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return new LongVal(la - lb, a.Payload ?? b.Payload);
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return da - db;
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Val)DefaultErrorObject;
		}

		public static Val Multiply(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of multiplication", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of multiplication", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return la * lb;
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return da * db;
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Val)DefaultErrorObject;
		}

		public static Val Concat(Val a, Val b)
		{
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of concatenation", false);
			return string.Concat(a.As(), b.As());
		}

		public static Val Divide(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of divison", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of divison", false);
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
			{
				if (db == 0.0) return (Val)Errors.ZeroDivisionErrorOccurred("Right side operand of floating point division");
				return From(da / db);
			}
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
			{
				if (lb == 0L) return (Val)Errors.ZeroDivisionErrorOccurred("Right side operand of integer division");
				if (la % lb == 0) return From(la / lb);
				return From((double)la / lb);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Val)DefaultErrorObject;
		}

		public static Val FloorDivide(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of floor division", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of floor division", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
			{
				if (lb == 0L) return (Val)Errors.ZeroDivisionErrorOccurred("Right side operand of floor divide");
				return From(la / lb);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Val)DefaultErrorObject;
		}

		public static Val Modulus(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of modulus", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of modulus", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return From(la % lb);
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return From(da % db);
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Val)DefaultErrorObject;
		}

		public static Val Power(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of power", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of power", false);
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return From(Math.Pow(da, db));
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return From((long)Math.Pow(la, lb));
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Val)DefaultErrorObject;
		}

		public static Val BitShiftLeft(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of arithmetic left shift", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of arithmetic left shift", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
			{
				var r = (int)lb;
				if (r < 0 || r > 63) return (Val)Errors.ErrorOccurred($"Shift operand of {r} for arithmetic left shift was not in the range of [0-63].");
				return la << r;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not an integer.")) ? throw _err : (Val)DefaultErrorObject;
		}

		public static Val BitShiftRight(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of arithmetic right shift", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of arithmetic right shift", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
			{
				var r = (int)lb;
				if (r < 0 || r > 63) return (Val)Errors.ErrorOccurred($"Shift operand of {r} for arithmetic right shift was not in the range of [0-63].");
				return la >> r;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Val)DefaultErrorObject;
		}

		public static Val LogicalRightShift(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of logical right shift", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of logical right shift", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
			{
				var r = (int)lb;
				if (r < 0 || r > 63) return (Val)Errors.ErrorOccurred($"Shift operand of {r} for logical right shift was not in the range of [0-63].");
				return (long)((ulong)la >> r);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Val)DefaultErrorObject;
		}

		public static Val BitwiseAnd(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of bitwise and", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of bitwise and", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return la & lb;
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Val)DefaultErrorObject;
		}

		public static Val BitwiseOr(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of bitwise or", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of bitwise or", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return la | lb;
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Val)DefaultErrorObject;
		}

		public static Val BitwiseXor(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of bitwise xor", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of bitwise xor", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return la ^ lb;
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Val)DefaultErrorObject;
		}

		public static Val BooleanAnd(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of boolean and", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of boolean and", false);
			return a.AsBool() ? b : a;
		}

		public static Val BooleanOr(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of boolean or", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of boolean or", false);
			return a.AsBool() ? a : b;
		}

		public static Val RegEx(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of regex", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of regex", false);
			object matches = null;
			_ = Core.RegEx.RegExMatch(a.As(), b.As(), ref matches, 1);
			return From(matches);
		}

		public static Val IdentityEquality(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of identity equality", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of identity equality", false);

			if (a.IsPrimitive && b.IsPrimitive)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return la == lb ? True : False;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db)) return da == db ? True : False;
				if (a is StringVal sa && b is StringVal sb) return string.Equals(sa.Value, sa.Value, StringComparison.Ordinal) ? True : False;
				return false;
			}

			return a.AsObject() == b.AsObject() ? True : False;
		}

		public static Val IdentityInequality(Val a, Val b) => !IdentityEquality(a, b).AsBool();

		public static Val ValueEquality(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of value equality", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of value equality", false);
			if (a.IsPrimitive && b.IsPrimitive)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return la == lb ? True : False;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db)) return da == db ? True : False;
				if (a is StringVal sa && b is StringVal sb) return string.Equals(sa.Value, sa.Value, StringComparison.OrdinalIgnoreCase) ? True : False;
				return false;
			}
			return a.AsObject() == b.AsObject() ? True : False;
		}

		public static Val ValueInequality(Val a, Val b)
		{
			return From(!ValueEquality(a, b).AsBool());
		}

		public static Val LessThan(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of less than", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of less than", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return la < lb;
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return da < db;
			return From(string.Compare(a.As(), b.As(), StringComparison.OrdinalIgnoreCase) < 0);
		}

		public static Val LessThanOrEqual(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of less than or equal", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of less than or equal", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return la <= lb;
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return da <= db;
			return From(string.Compare(a.As(), b.As(), StringComparison.OrdinalIgnoreCase) <= 0);
		}

		public static Val GreaterThan(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of greater than", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of greater than or equal", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return la > lb;
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return da > db;
			return From(string.Compare(a.As(), b.As(), StringComparison.OrdinalIgnoreCase) > 0);
		}

		public static Val GreaterThanOrEqual(Val a, Val b)
		{
			if (a == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of greater than or equal", false);
			if (b == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of greater than or equal", false);
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return da >= db;
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return la >= lb;
			return From(string.Compare(a.As(), b.As(), StringComparison.OrdinalIgnoreCase) >= 0);
		}

		public static Val Is(Val a, Val b)
		{
			if (b == null)
				return a == null;

			if (!b.TryGetString(out string test))
				return false;
			test = test.ToLower();

			//Put common cases first.
			switch (test)
			{
				case "integer":
					return a is LongVal;

				case "float":
					return a is DoubleVal;

				case "number":
					return a.TryGetDouble(out _);

				case "string":
					return a is StringVal;

				case "unset":
				case "null":
					return a == null;
			}

			if (a == null) return (bool)Errors.UnsetErrorOccurred("Left side operand was unset", false);

			//Traverse class hierarchy to see if there is a match.
			if (a is ObjectVal o)
			{
				var type = o.Value.GetType();

				return Parser.IsTypeOrBase(type, test);
			}
				
			return (bool)Errors.TypeErrorOccurred(a, typeof(ObjectVal), false);
		}

		#region Arithmetic operators
		public static Val operator +(Val a, Val b) => Add(a, b);

		public static Val operator -(Val a, Val b) => Subtract(a, b);

		public static Val operator *(Val a, Val b) => Multiply(a, b);

		public static Val operator /(Val a, Val b) => Divide(a, b);

		public static Val operator !(Val a) => a.AsBool();

		public static Val operator <<(Val a, Val b) => BitShiftLeft(a, b);
		#endregion

		#region IConvertible implementations
		public TypeCode GetTypeCode() => TypeCode.Object;
		public bool ToBoolean(IFormatProvider provider) => AsBool();
		public char ToChar(IFormatProvider provider) => throw new InvalidCastException();
		public sbyte ToSByte(IFormatProvider provider) => Convert.ToSByte(ToInt64(provider));
		public byte ToByte(IFormatProvider provider) => Convert.ToByte(ToInt64(provider));
		public short ToInt16(IFormatProvider provider) => Convert.ToInt16(ToInt64(provider));
		public ushort ToUInt16(IFormatProvider provider) => Convert.ToUInt16(ToInt64(provider));
		public int ToInt32(IFormatProvider provider) => Convert.ToInt32(ToInt64(provider));
		public uint ToUInt32(IFormatProvider provider) => Convert.ToUInt32(ToInt64(provider));
		public long ToInt64(IFormatProvider provider)
		{
			if (TryGetLong(out var l)) return l;
			throw new InvalidCastException("Cannot convert to Int64");
		}
		public ulong ToUInt64(IFormatProvider provider) => Convert.ToUInt64(ToInt64(provider));
		public float ToSingle(IFormatProvider provider) => Convert.ToSingle(ToDouble(provider));
		public double ToDouble(IFormatProvider provider)
		{
			if (TryGetDouble(out var d)) return d;
			throw new InvalidCastException("Cannot convert to Double");
		}
		public decimal ToDecimal(IFormatProvider provider) => Convert.ToDecimal(ToDouble(provider));
		public DateTime ToDateTime(IFormatProvider provider) => throw new InvalidCastException();
		public string ToString(IFormatProvider provider)
		{
			if (TryGetString(out var s)) return s;
			var obj = AsObject();
			return obj?.ToString() ?? string.Empty;
		}
		public object ToType(Type conversionType, IFormatProvider provider)
		{
			if (conversionType == typeof(bool)) return ToBoolean(provider);
			if (conversionType == typeof(char)) return ToChar(provider);
			if (conversionType == typeof(sbyte)) return ToSByte(provider);
			if (conversionType == typeof(byte)) return ToByte(provider);
			if (conversionType == typeof(short)) return ToInt16(provider);
			if (conversionType == typeof(ushort)) return ToUInt16(provider);
			if (conversionType == typeof(int)) return ToInt32(provider);
			if (conversionType == typeof(uint)) return ToUInt32(provider);
			if (conversionType == typeof(long)) return ToInt64(provider);
			if (conversionType == typeof(ulong)) return ToUInt64(provider);
			if (conversionType == typeof(float)) return ToSingle(provider);
			if (conversionType == typeof(double)) return ToDouble(provider);
			if (conversionType == typeof(decimal)) return ToDecimal(provider);
			if (conversionType == typeof(string)) return ToString(provider);
			if (conversionType == typeof(DateTime)) return ToDateTime(provider);
			if (conversionType == typeof(object)) return AsObject();
			throw new InvalidCastException($"Cannot convert Val to {conversionType.Name}.");
		}
		#endregion
	}

	static class LongCache
	{
		public const long Min = -1;
		public const long Max = 256;

		private static readonly LongVal[] Cache = Build();

		private static LongVal[] Build()
		{
			var arr = new LongVal[Max - Min + 1];
			for (long v = Min; v <= Max; v++)
				arr[v - Min] = new LongVal(v);
			return arr;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static LongVal Get(long v, object o = null)
			=> (o == null && v >= Min && v <= Max) ? Cache[v - Min] : new LongVal(v, o);
	}

	public sealed class LongVal : Val
	{
		public readonly long Value;
		private string _cachedString;
		public LongVal(long v, object o = null) 
		{
			Value = v;
			if (o != null)
				Payload = o;
		}
		public override bool TryGetLong(out long value) { value = Value; return true; }
		public override bool TryGetDouble(out double value) { value = Value; return true; }
		public override bool TryGetString(out string value)
		{
			if (_cachedString == null)
				_cachedString = Value.ToString(CultureInfo.InvariantCulture);
			value = _cachedString;
			return true;
		}
		public override object AsObject() => Value;
		public override bool AsBool() => Value != 0L;
		public override bool IsPrimitive => true;
	}

	sealed class DoubleVal : Val
	{
		public readonly double Value;
		private string _cachedString;
		public DoubleVal(double v) => Value = v;
		public override bool TryGetLong(out long value) { value = default; return false; }
		public override bool TryGetDouble(out double value) { value = Value; return true; }
		public override bool TryGetString(out string value)
		{
			if (_cachedString == null)
				_cachedString = Value.ToString(CultureInfo.InvariantCulture);
			value = _cachedString;
			return true;
		}
		public override object AsObject() => Value;
		public override bool AsBool() => Value != 0.0;
		public override bool IsPrimitive => true;
	}

	public sealed class StringVal : Val
	{
		private string _value;
		public string Value {
			get
			{
				if (Payload != null)
				{
					if (Payload is StringBuilder sb)
						return sb.ToString();
					else if (Payload is StringBuffer sbuf)
						return sbuf.ToString();
					else
						throw new Exception("Unknown string type");
				}
				return _value;
			}
		}

		private enum CacheState : byte { Unknown = 0, Success = 1, Failure = 2 }

		//––– long parse cache/state –––
		private long _cachedLong;
		private CacheState _longState = CacheState.Unknown;

		//––– double parse cache/state –––
		private double _cachedDouble;
		private CacheState _doubleState = CacheState.Unknown;

		public StringVal(string v) => _value = v ?? throw new ArgumentNullException(nameof(v));
		public StringVal(StringBuilder v) => Payload = v ?? throw new ArgumentNullException(nameof(v));
		public StringVal(StringBuffer v) => Payload = v ?? throw new ArgumentNullException(nameof(v));

		public override bool TryGetLong(out long value)
		{
			if (_longState == CacheState.Unknown)
			{
				if (TryParseLong(Value.AsSpan(), out var l))
				{
					_cachedLong = l;
					_longState = CacheState.Success;
				}
				else
				{
					_longState = CacheState.Failure;
				}
			}

			if (_longState == CacheState.Success)
			{
				value = _cachedLong;
				return true;
			}

			value = default;
			return false;
		}

		public override bool TryGetDouble(out double value)
		{
			if (_doubleState == CacheState.Unknown)
			{
				if (double.TryParse(Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
				{
					_cachedDouble = d;
					_doubleState = CacheState.Success;
				}
				else
				{
					_doubleState = CacheState.Failure;
				}
			}

			if (_doubleState == CacheState.Success)
			{
				value = _cachedDouble;
				return true;
			}

			value = default;
			return false;
		}
		public override bool TryGetString(out string value) { value = Value; return true; }
		public override object AsObject() => Value;
		public override bool AsBool() => Value != "";
		public override bool IsPrimitive => true;

		private bool TryParseLong(ReadOnlySpan<char> s, out long ll)
		{
			s = s.Trim();
			var len = s.Length;
			if (len == 0)
			{
				ll = default;
				return false;
			}

			if (long.TryParse(s, out ll))
			{
				return true;
			}

			var neg = false;

			if (s[0] == '-')
			{
				if (len == 1)
				{
					ll = default;
					return false;
				}
				neg = true;
				s = s.Slice(1);
			}

			if (s[0] == '0' && s.Length > 1 &&
				((s[1] | 0x20) == 'x' && long.TryParse(s.Slice(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ll)
				|| ((s[1] | 0x20) == 'b' && long.TryParse(s.Slice(2), NumberStyles.AllowBinarySpecifier, CultureInfo.InvariantCulture, out ll))))
			{
				if (neg)
					ll = -ll;
				return true;
			}

			return false;
		}
	}

	public sealed class ObjectVal : Val
	{
		public readonly object Value;
		public ObjectVal(object v)
		{
			Value = v ?? throw new ArgumentNullException(nameof(v));
		}
		public override bool TryGetLong(out long value) { value = default; return false; }
		public override bool TryGetDouble(out double value) { value = default; return false; }
		public override bool TryGetString(out string value)
		{
			value = Value?.ToString() ?? string.Empty;
			return Value != null;
		}
		public override object AsObject() => Value;
		public override bool AsBool() => Value != null;
		public override bool IsPrimitive => false;
	}

	public sealed class AnyVal : Val
	{
		public readonly Any Value;
		public AnyVal(Any v)
		{
			Value = v ?? throw new ArgumentNullException(nameof(v)); ;
		}
		public override bool TryGetLong(out long value) { value = default; return false; }
		public override bool TryGetDouble(out double value) { value = default; return false; }
		public override bool TryGetString(out string value)
		{
			value = Value?.ToString() ?? string.Empty;
			return Value != null;
		}
		public override object AsObject() => Value;
		public override bool AsBool() => Value != null;
		public override bool IsPrimitive => false;
	}

	/// <summary>Extension methods for Val conversions.</summary>
	public static class ValExtensions
	{
		public static bool IsNumeric(this Val v)
			=> v != null && (v.TryGetLong(out _) || v.TryGetDouble(out _));

		public static bool Ab(this Val v)
		{
			if (v == null) return false;
			return v.AsBool();
		}
		public static bool Ab(this Val v, bool def)
			=> v == null ? def : v.Ab();

		public static long Al(this Val v)
		{
			if (v == null) throw new InvalidOperationException("Long conversion target is unset");
			if (v.TryGetLong(out var l))
				return l;
			if (v.TryGetDouble(out var d))
				return (long)d;
			throw new InvalidCastException($"Cannot convert '{v}' to long.");
		}
		public static long Al(this Val v, long def)
			=> v == null ? def : (v.TryGetLong(out var l) ? l : v.TryGetDouble(out var d) ? (long)d : def);

		public static double Ad(this Val v)
		{
			if (v == null) throw new InvalidOperationException("Double conversion target is unset");
			return v.TryGetDouble(out var d) ? d : throw new InvalidCastException();
		}
		public static double Ad(this Val v, double def)
			=> v == null ? def : v.TryGetDouble(out var d) ? d : def;

		public static string As(this Val v)
		{
			if (v == null) throw new InvalidOperationException("String conversion target is unset");
			return v.TryGetString(out var s) ? s : throw new InvalidCastException();
		}
		public static string As(this Val v, string def)
			=> v == null ? def : v.TryGetString(out var s) ? s : def;

		public static Any Aa(this Val v)
			=> (Any)v?.AsObject();

		public static object Ao(this Val v)
			=> v == null ? null : v.AsObject();
		public static object Ao(this Val v, object def)
			=> v == null ? def : v.AsObject();

		public static bool IsLong(this Val v) => v?.TryGetLong(out _) ?? false;
		public static bool IsLong(this Val v, out long value)
			=> v.TryGetLong(out value);

		public static bool IsDouble(this Val v) => v?.TryGetDouble(out _) ?? false;
		public static bool IsDouble(this Val v, out double value)
			=> v.TryGetDouble(out value);

		public static bool IsString(this Val v) => v?.TryGetString(out _) ?? false;
		public static bool IsString(this Val v, [NotNullWhen(true)] out string value)
			=> v.TryGetString(out value);

		public static bool IsObject(this Val v) => v != null;
		public static bool IsObject(this Val v, [NotNullWhen(true)] out object value)
		{
			value = v?.AsObject();
			return v != null;
		}

		public static Val AsVal(this object v)
		{
			if (v == null) return null;
			if (v is Val val) return val;
			if (v is IConvertible)
				return (Val)TypeDescriptor.GetConverter(typeof(Val)).ConvertFrom(v);
			return Val.From(v);
		}
	}

	/// <summary>TypeConverter for Val to/from primitives.</summary>
	public class ValTypeConverter : TypeConverter
	{
		static readonly HashSet<Type> Supported = new()
		{
			typeof(Val), typeof(bool), typeof(char), typeof(sbyte), typeof(byte),
			typeof(short), typeof(ushort), typeof(int), typeof(uint),
			typeof(long), typeof(ulong), typeof(float), typeof(double),
			typeof(decimal), typeof(string), typeof(DateTime), typeof(object)
		};

		public override bool CanConvertFrom(ITypeDescriptorContext ctx, Type src)
			=> Supported.Contains(src) || typeof(IConvertible).IsAssignableFrom(src);
		public override bool CanConvertTo(ITypeDescriptorContext ctx, Type dst)
			=> Supported.Contains(dst) || typeof(IConvertible).IsAssignableFrom(dst);

		public override object ConvertFrom(ITypeDescriptorContext ctx, CultureInfo culture, object value)
		{
			if (value == null) return null;
			if (value is Val v) return v;
			if (value is IConvertible ic)
			{
				switch (ic.GetTypeCode())
				{
					case TypeCode.Boolean: return Val.From(ic.ToBoolean(culture));
					case TypeCode.Char: return Val.From(ic.ToChar(culture).ToString());
					case TypeCode.SByte: return Val.From((long)ic.ToSByte(culture));
					case TypeCode.Byte: return Val.From((long)ic.ToByte(culture));
					case TypeCode.Int16: return Val.From((long)ic.ToInt16(culture));
					case TypeCode.UInt16: return Val.From((long)ic.ToUInt16(culture));
					case TypeCode.Int32: return Val.From((long)ic.ToInt32(culture));
					case TypeCode.UInt32: return Val.From((long)ic.ToUInt32(culture));
					case TypeCode.Int64: return Val.From(ic.ToInt64(culture));
					case TypeCode.UInt64: return Val.From((long)ic.ToUInt64(culture));
					case TypeCode.Single: return Val.From((double)ic.ToSingle(culture));
					case TypeCode.Double: return Val.From(ic.ToDouble(culture));
					case TypeCode.Decimal: return Val.From((double)ic.ToDecimal(culture));
					case TypeCode.String: return Val.From(ic.ToString(culture));
					case TypeCode.DateTime: return Val.From(ic.ToDateTime(culture).ToString());
				}
			}
			return base.ConvertFrom(ctx, culture, value);
		}

		public override object ConvertTo(ITypeDescriptorContext ctx, CultureInfo culture, object value, Type dest)
		{
			if (!(value is Val v)) return base.ConvertTo(ctx, culture, value, dest);
			if (v == null) return null;
			if (dest == typeof(Val)) return v;
			if (typeof(IConvertible).IsAssignableFrom(dest))
				return ((IConvertible)v).ToType(dest, culture);
			return base.ConvertTo(ctx, culture, value, dest);
		}
	}

	static class KsValueLongCache
	{
		public const long Min = -1;
		public const long Max = 256;

		private static readonly KsValue[] Cache = Build();

		private static KsValue[] Build()
		{
			var arr = new KsValue[Max - Min + 1];
			for (long v = Min; v <= Max; v++)
				arr[v - Min] = new KsValue(v);
			return arr;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static KsValue Get(long v, object o = null)
			=> (o == null && v >= Min && v <= Max) ? Cache[v - Min] : new KsValue(v, o);
	}

	public enum KsValueType : byte { Unset, Integer, Double, String, Any, Object }

	[TypeConverter(typeof(KsValueTypeConverter))]
	[StructLayout(LayoutKind.Explicit)]
	public readonly struct KsValue : IConvertible
	{
		// ---- numeric union (overlap) ----
		[FieldOffset(0)] private readonly long _i64;
		[FieldOffset(0)] private readonly double _f64;

		// ---- single reference slot ----
		// Holds: any object type (Any, Exception etc), string payload, object payload, or numeric anchor
		[FieldOffset(8)] private readonly object _ref;

		// ---- tag (separate; do not overlap with refs) ----
		[FieldOffset(16)] public readonly KsValueType Type;

		public static readonly KsValue True = KsValueLongCache.Get(1L);
		public static readonly KsValue False = KsValueLongCache.Get(0L);

		static Error _err;

		public KsValue(long v, object anchor = null) { _i64 = v; _f64 = default; _ref = anchor; Type = KsValueType.Integer; }
		public KsValue(double v, object anchor = null) { _i64 = default; _f64 = v; _ref = anchor; Type = KsValueType.Double; }
		public KsValue(string s) { _i64 = default; _f64 = default; _ref = s; Type = s == null ? KsValueType.Unset : KsValueType.String; }
		public KsValue(Any o) { _i64 = default; _f64 = default; _ref = o; Type = o == null ? KsValueType.Unset : KsValueType.Any; }
		public KsValue(object o) 
		{ 
			if (o == null) { _i64 = default; _f64 = default; _ref = null; Type = KsValueType.Unset; }
			else if (o is Any a) { _i64 = default; _f64 = default; _ref = a; Type = KsValueType.Any; }
			else if (o is long l) { _i64 = l; _f64 = default; _ref = null; Type = KsValueType.Integer; }
			else if (o is double d) { _i64 = default; _f64 = d; _ref = null; Type = KsValueType.Double; }
			else if (o is string s) { _i64 = default; _f64 = default; _ref = s; Type = KsValueType.String; } 
			else { _i64 = default; _f64 = default; _ref = o; Type = KsValueType.Object; }
		}

		private object Anchor => Type is KsValueType.Integer or KsValueType.Double or KsValueType.Object or KsValueType.Any ? _ref : null;
		public bool IsUnset => Type == KsValueType.Unset;
		public bool IsPrimitive => Type == KsValueType.Integer || Type == KsValueType.Double || Type == KsValueType.String;

		#region Basic conversions
		public readonly bool TryGetLong(out long value)
		{
			switch (Type)
			{
				case KsValueType.Integer: value = _i64; return true;
				case KsValueType.String: return TryParseInt64(out value);
				default: value = default; return false;
			}
		}

		public readonly bool TryGetDouble(out double value)
		{
			switch (Type)
			{
				case KsValueType.Double: value = _f64; return true;
				case KsValueType.Integer: value = (double)_i64; return true;
				case KsValueType.String:
					if (double.TryParse((string)_ref, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
						return true;
					if (TryParseInt64(out var i))
					{ value = i; return true; }
					value = default; return false;
				default:
					value = default; return false;
			}
		}

		public readonly bool TryGetString(out string value)
		{
			switch (Type)
			{
				case KsValueType.String: value = (string)_ref!; return true;
				case KsValueType.Integer: value = _i64.ToString(CultureInfo.InvariantCulture); return true;
				case KsValueType.Double: value = _f64.ToString(CultureInfo.InvariantCulture); return true;
				default:
					value = string.Empty; return false;
			}
		}

		public readonly bool TryGetAny(out Any value)
		{
			if (Type == KsValueType.Any)
			{
				value = (Any)_ref; return true;
			}
			value = null; return false;
		}

		public readonly object AsObject()
			=> Type switch
			{
				KsValueType.Integer => _i64,
				KsValueType.Double => _f64,
				KsValueType.String => _ref,
				KsValueType.Any => _ref,
				KsValueType.Object => _ref,
				_ => null
			};

		public readonly bool AsBool()
			=> Type switch
			{
				KsValueType.Integer => _i64 != 0L,
				KsValueType.Double => _f64 != 0.0,
				KsValueType.String => (string)_ref != string.Empty,
				_ => true
			};
		#endregion

		// ---------- Numeric parsing helpers ----------
		private readonly bool TryParseInt64(out long ll)
		{
			ReadOnlySpan<char> s = ((string)_ref).AsSpan().Trim();
			if (s.Length == 0) { ll = 0; return false; }

			bool neg = false;
			if (s[0] == '-' || s[0] == '+')
			{
				neg = s[0] == '-';
				s = s.Slice(1);
				if (s.Length == 0) { ll = 0; return false; }
			}

			// 0x / 0X hex or 0b / 0B binary
			if (s.Length > 2 && s[0] == '0')
			{
				char t = (char)(s[1] | 0x20);
				if (t == 'x')
				{
					if (long.TryParse(s.Slice(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
					{ ll = neg ? -hex : hex; return true; }
				}
				else if (t == 'b')
				{
#if NET8_0_OR_GREATER
					if (long.TryParse(s.Slice(2), NumberStyles.AllowBinarySpecifier, CultureInfo.InvariantCulture, out var bin))
					{ ll = neg ? -bin : bin; return true; }
#else
                    long acc = 0;
                    foreach (char ch in s.Slice(2))
                    {
                        int bit = ch - '0';
                        if ((uint)bit > 1) { ll = 0; return false; }
                        acc = (acc << 1) | bit;
                    }
                    ll = neg ? -acc : acc; return true;
#endif
				}
			}

			if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
			{ ll = neg ? -i : i; return true; }

			ll = 0; return false;
		}

		#region Operators
		// ---------- Arithmetic ----------
		public static KsValue Add(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of addition", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of addition", false);

			var anchor = a.Anchor ?? b.Anchor;

			if (a.Type == KsValueType.Integer)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(a._i64 + b._i64, anchor);
				else if (b.Type == KsValueType.Double) return new KsValue(a._i64 + b._f64, anchor);
			}
			else if (a.Type == KsValueType.Double)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(a._f64 + b._i64, anchor);
				else if (b.Type == KsValueType.Double) return new KsValue(a._f64 + b._f64, anchor);
			}

			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return new KsValue(la + lb, anchor);

			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return new KsValue(da + db, anchor);

			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric."))
				 ? throw _err
				 : DefaultErrorObject;
		}

		public static KsValue Subtract(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of subtraction", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of subtraction", false);

			var anchor = a.Anchor ?? b.Anchor;

			if (a.Type == KsValueType.Integer)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(a._i64 - b._i64, anchor);
				else if (b.Type == KsValueType.Double) return new KsValue(a._i64 - b._f64, anchor);
			}
			else if (a.Type == KsValueType.Double)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(a._f64 - b._i64, anchor);
				else if (b.Type == KsValueType.Double) return new KsValue(a._f64 - b._f64, anchor);
			}

			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return new KsValue(la - lb, anchor);

			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return new KsValue(da - db, anchor);

			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric."))
				 ? throw _err
				 : DefaultErrorObject;
		}

		public static KsValue Multiply(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of multiplication", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of multiplication", false);

			if (a.Type == KsValueType.Integer)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(a._i64 * b._i64);
				else if (b.Type == KsValueType.Double) return new KsValue(a._i64 * b._f64);
			}
			else if (a.Type == KsValueType.Double)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(a._f64 * b._i64);
				else if (b.Type == KsValueType.Double) return new KsValue(a._f64 * b._f64);
			}

			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return new KsValue(la * lb);

			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return new KsValue(da * db);

			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric."))
				 ? throw _err
				 : DefaultErrorObject;
		}

		public static KsValue Divide(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of divison", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of divison", false);

			if (a.Type == KsValueType.Integer)
			{
				if (b.Type == KsValueType.Integer) return (b._i64 == 0L) ? (KsValue)Errors.ZeroDivisionErrorOccurred("Right side operand of integer division") : new KsValue((double)a._i64 / b._i64);
				else if (b.Type == KsValueType.Double) return (b._f64 == 0.0) ? (KsValue)Errors.ZeroDivisionErrorOccurred("Right side operand of floating point division") : new KsValue(a._i64 / b._f64);
			}
			else if (a.Type == KsValueType.Double)
			{
				if (b.Type == KsValueType.Integer) return (b._i64 == 0L) ? (KsValue)Errors.ZeroDivisionErrorOccurred("Right side operand of floating point division") : new KsValue(a._f64 / b._i64);
				else if (b.Type == KsValueType.Double) return (b._f64 == 0.0) ? (KsValue)Errors.ZeroDivisionErrorOccurred("Right side operand of floating point division") : new KsValue(a._f64 / b._f64);
			}

			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
			{
				if (lb == 0L) return (KsValue)Errors.ZeroDivisionErrorOccurred("Right side operand of integer division");
				return new KsValue((double)la / lb);
			}
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
			{
				if (db == 0.0) return (KsValue)Errors.ZeroDivisionErrorOccurred("Right side operand of floating point division");
				return new KsValue(da / db);
			}

			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric."))
				 ? throw _err
				 : DefaultErrorObject;
		}

		public static KsValue IntegerDivide(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of divison", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of divison", false);

			if (a.Type == KsValueType.Integer)
			{
				if (b.Type == KsValueType.Integer) return (b._i64 == 0L) ? (KsValue)Errors.ZeroDivisionErrorOccurred("Right side operand of integer division") : new KsValue(a._i64 / b._i64);
			}

			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
			{
				if (lb == 0L) return (KsValue)Errors.ZeroDivisionErrorOccurred("Right side operand of integer division");
				return new KsValue((double)la / lb);
			}

			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetLong(out _) ? "Right" : "Left")} operand was not an integer."))
				 ? throw _err
				 : DefaultErrorObject;
		}

		public static KsValue Modulus(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of modulus", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of modulus", false);

			if (a.Type == KsValueType.Integer)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(a._i64 % b._i64);
				else if (b.Type == KsValueType.Double) return new KsValue(a._i64 % b._f64);
			}
			else if (a.Type == KsValueType.Double)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(a._f64 % b._i64);
				else if (b.Type == KsValueType.Double) return new KsValue(a._f64 % b._f64);
			}

			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return new KsValue(la % lb);
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return new KsValue(da % db);

			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric."))
				 ? throw _err
				 : DefaultErrorObject;
		}

		public static KsValue Power(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of power", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of power", false);

			if (a.Type == KsValueType.Integer)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(Math.Pow(a._f64, b._f64));
				else if (b.Type == KsValueType.Double) return new KsValue(Math.Pow(a._f64, b._i64));
			}
			else if (a.Type == KsValueType.Double)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(Math.Pow(a._i64, b._f64));
				else if (b.Type == KsValueType.Double) return new KsValue((long)Math.Pow(a._i64, b._i64));
			}

			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				return new KsValue(Math.Pow(da, db));
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				return new KsValue((long)Math.Pow(la, lb));

			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric."))
				 ? throw _err
				 : DefaultErrorObject;
		}

		public static KsValue Concat(in KsValue a, in KsValue b)
		{
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of concatenation", false);
			if (!a.IsPrimitive || !b.IsPrimitive)
			{
				return Errors.ErrorOccurred(_err = new TypeError($"{(a.IsPrimitive ? "Right" : "Left")} operand was an object."))
					? throw _err
					: DefaultErrorObject;
			}
			a.TryGetString(out var sa);
			b.TryGetString(out var sb);
			return new KsValue(string.Concat(sa, sb));
		}

		public static KsValue RegEx(KsValue a, KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred($"Left side operand of regex", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred($"Right side operand of regex", false);
			object matches = null;
			_ = Core.RegEx.RegExMatch(a.As(), b.As(), ref matches, 1);
			return new KsValue(matches);
		}

		// ---------- Bitwise / shifts ----------
		public static KsValue BitwiseAnd(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of bitwise and", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of bitwise and", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return new KsValue(la & lb);
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not an integer."))
				 ? throw _err
				 : DefaultErrorObject;
		}
		public static KsValue BitwiseOr(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of bitwise or", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of bitwise or", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return new KsValue(la | lb);
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not an integer."))
				 ? throw _err
				 : DefaultErrorObject;
		}
		public static KsValue BitwiseXor(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of bitwise xor", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of bitwise xor", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return new KsValue(la ^ lb);
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not an integer."))
				 ? throw _err
				 : DefaultErrorObject;
		}
		public static KsValue BitShiftLeft(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of arithmetic left shift", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of arithmetic left shift", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
			{
				var r = (int)lb;
				if (r < 0 || r > 63) return (KsValue)Errors.ErrorOccurred($"Shift operand of {r} for arithmetic left shift was not in the range of [0-63].");
				return new KsValue(la << r);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not an integer."))
				 ? throw _err
				 : DefaultErrorObject;
		}
		public static KsValue BitShiftRight(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of arithmetic right shift", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of arithmetic right shift", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
			{
				var r = (int)lb;
				if (r < 0 || r > 63) return (KsValue)Errors.ErrorOccurred($"Shift operand of {r} for arithmetic right shift was not in the range of [0-63].");
				return new KsValue(la >> r);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric."))
				 ? throw _err
				 : DefaultErrorObject;
		}
		public static KsValue LogicalRightShift(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of logical right shift", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of logical right shift", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
			{
				var r = (int)lb;
				if (r < 0 || r > 63) return (KsValue)Errors.ErrorOccurred($"Shift operand of {r} for logical right shift was not in the range of [0-63].");
				return new KsValue((long)((ulong)la >> r));
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(a.TryGetDouble(out _) ? "Right" : "Left")} operand was not numeric."))
				 ? throw _err
				 : DefaultErrorObject;
		}

		// ---------- Boolean ops ----------
		public static KsValue BooleanAnd(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of boolean and", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of boolean and", false);
			return a.AsBool() ? b : a;
		}
		public static KsValue BooleanOr(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of boolean or", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of boolean or", false);
			return a.AsBool() ? a : b;
		}

		// ---------- Equality / comparison ----------
		public static KsValue IdentityEquality(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred($"Left side operand of identity equality", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred($"Right side operand of identity equality", false);

			if (a.Type == KsValueType.Integer)
			{
				if (b.Type == KsValueType.Integer) return a._i64 == b._i64 ? True : False;
				if (b.Type == KsValueType.Double) return a._i64 == b._f64 ? True : False;
			}
			else if (a.Type == KsValueType.Double)
			{
				if (b.Type == KsValueType.Integer) return a._f64 == b._i64 ? True : False;
				if (b.Type == KsValueType.Double) return a._f64 == b._f64 ? True : False;
			}
			else if (a.Type == KsValueType.String && b.Type == KsValueType.String)
			{
				return string.Equals((string)a._ref, (string)b._ref, StringComparison.Ordinal) ? True : False;
			}
			else if (a.Type == KsValueType.Any && b.Type == KsValueType.Any)
			{
				return a._ref == b._ref ? True : False;
			}
			else if (a.Type == KsValueType.Object && b.Type == KsValueType.Object)
			{
				return a._ref == b._ref ? True : False;
			}
			else if (a.Type == KsValueType.Any || b.Type == KsValueType.Any ||
					 a.Type == KsValueType.Object || b.Type == KsValueType.Object)
			{
				return False;
			}

			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return la == lb ? True : False;
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db)) return da == db ? True : False;
			return False;
		}

		public static KsValue IdentityInequality(in KsValue a, in KsValue b) => !IdentityEquality(a, b).AsBool();
		public static KsValue ValueEquality(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of value equality", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of value equality", false);

			if (a.Type == KsValueType.Integer)
			{
				if (b.Type == KsValueType.Integer) return a._i64 == b._i64 ? True : False;
				if (b.Type == KsValueType.Double) return a._i64 == b._f64 ? True : False;
			}
			else if (a.Type == KsValueType.Double)
			{
				if (b.Type == KsValueType.Integer) return a._f64 == b._i64 ? True : False;
				if (b.Type == KsValueType.Double) return a._f64 == b._f64 ? True : False;
			}
			else if (a.Type == KsValueType.String && b.Type == KsValueType.String)
			{
				return string.Equals((string)a._ref, (string)b._ref, StringComparison.OrdinalIgnoreCase) ? True : False;
			}
			else if (a.Type == KsValueType.Any && b.Type == KsValueType.Any)
			{
				return a._ref == b._ref ? True : False;
			}
			else if (a.Type == KsValueType.Object && b.Type == KsValueType.Object)
			{
				return a._ref == b._ref ? True : False;
			}
			else if (a.Type == KsValueType.Any || b.Type == KsValueType.Any ||
					 a.Type == KsValueType.Object || b.Type == KsValueType.Object)
			{
				return False;
			}

			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return la == lb ? True : False;
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db)) return da == db ? True : False;

			return False;
		}
		public static KsValue ValueInequality(in KsValue a, in KsValue b)
			=> ValueEquality(a, b).AsBool() ? False : True;

		public static KsValue LessThan(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of less than", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of less than", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return la < lb ? True : False;
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db)) return da < db ? True : False;
			if (!a.IsPrimitive || !b.IsPrimitive)
			{
				return Errors.ErrorOccurred(_err = new TypeError($"{(a.IsPrimitive ? "Right" : "Left")} operand was not numeric."))
					? throw _err
					: DefaultErrorObject;
			}
			a.TryGetString(out var sa); b.TryGetString(out var sb);
			return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase) < 0 ? True : False;
		}
		public static KsValue LessThanOrEqual(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of less than or equal", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of less than or equal", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return la <= lb ? True : False;
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db)) return da <= db ? True : False;
			if (!a.IsPrimitive || !b.IsPrimitive)
			{
				return Errors.ErrorOccurred(_err = new TypeError($"{(a.IsPrimitive ? "Right" : "Left")} operand was not numeric."))
					? throw _err
					: DefaultErrorObject;
			}
			a.TryGetString(out var sa); b.TryGetString(out var sb);
			return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase) <= 0 ? True : False;
		}
		public static KsValue GreaterThan(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of greater than", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of greater than or equal", false);
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return la > lb ? True : False;
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db)) return da > db ? True : False;
			if (!a.IsPrimitive || !b.IsPrimitive)
			{
				return Errors.ErrorOccurred(_err = new TypeError($"{(a.IsPrimitive ? "Right" : "Left")} operand was not numeric."))
					? throw _err
					: DefaultErrorObject;
			}
			a.TryGetString(out var sa); b.TryGetString(out var sb);
			return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase) > 0 ? True : False;
		}
		public static KsValue GreaterThanOrEqual(in KsValue a, in KsValue b)
		{
			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand of greater than or equal", false);
			if (b.IsUnset) return (bool)Errors.UnsetErrorOccurred("Right side operand of greater than or equal", false);
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db)) return da >= db ? True : False;
			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return la >= lb ? True : False;
			if (!a.IsPrimitive || !b.IsPrimitive)
			{
				return Errors.ErrorOccurred(_err = new TypeError($"{(a.IsPrimitive ? "Right" : "Left")} operand was not numeric."))
					? throw _err
					: DefaultErrorObject;
			}
			a.TryGetString(out var sa); b.TryGetString(out var sb);
			return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase) >= 0 ? True : False;
		}
		#endregion

		#region IConvertible implementations
		public TypeCode GetTypeCode()
		{
			switch (Type)
			{
				case KsValueType.Integer: return TypeCode.Int64;
				case KsValueType.Double: return TypeCode.Double;
				case KsValueType.String: return TypeCode.String;
				default: return TypeCode.Object;
			}
		}
		public bool ToBoolean(IFormatProvider provider) => AsBool();
		public char ToChar(IFormatProvider provider) => throw new InvalidCastException();
		public sbyte ToSByte(IFormatProvider provider) => Convert.ToSByte(ToInt64(provider));
		public byte ToByte(IFormatProvider provider) => Convert.ToByte(ToInt64(provider));
		public short ToInt16(IFormatProvider provider) => Convert.ToInt16(ToInt64(provider));
		public ushort ToUInt16(IFormatProvider provider) => Convert.ToUInt16(ToInt64(provider));
		public int ToInt32(IFormatProvider provider) => Convert.ToInt32(ToInt64(provider));
		public uint ToUInt32(IFormatProvider provider) => Convert.ToUInt32(ToInt64(provider));
		public long ToInt64(IFormatProvider provider)
		{
			if (TryGetLong(out var l)) return l;
			throw new InvalidCastException("Cannot convert to Int64");
		}
		public ulong ToUInt64(IFormatProvider provider) => Convert.ToUInt64(ToInt64(provider));
		public float ToSingle(IFormatProvider provider) => Convert.ToSingle(ToDouble(provider));
		public double ToDouble(IFormatProvider provider)
		{
			if (TryGetDouble(out var d)) return d;
			throw new InvalidCastException("Cannot convert to Double");
		}
		public decimal ToDecimal(IFormatProvider provider) => Convert.ToDecimal(ToDouble(provider));
		public DateTime ToDateTime(IFormatProvider provider) => throw new InvalidCastException();
		public string ToString(IFormatProvider provider)
		{
			if (TryGetString(out var s)) return s;
			throw new InvalidCastException("Cannot convert to String");
		}
		public object ToType(Type conversionType, IFormatProvider provider)
		{
			if (conversionType == typeof(bool)) return ToBoolean(provider);
			if (conversionType == typeof(char)) return ToChar(provider);
			if (conversionType == typeof(sbyte)) return ToSByte(provider);
			if (conversionType == typeof(byte)) return ToByte(provider);
			if (conversionType == typeof(short)) return ToInt16(provider);
			if (conversionType == typeof(ushort)) return ToUInt16(provider);
			if (conversionType == typeof(int)) return ToInt32(provider);
			if (conversionType == typeof(uint)) return ToUInt32(provider);
			if (conversionType == typeof(long)) return ToInt64(provider);
			if (conversionType == typeof(ulong)) return ToUInt64(provider);
			if (conversionType == typeof(float)) return ToSingle(provider);
			if (conversionType == typeof(double)) return ToDouble(provider);
			if (conversionType == typeof(decimal)) return ToDecimal(provider);
			if (conversionType == typeof(string)) return ToString(provider);
			if (conversionType == typeof(DateTime)) return ToDateTime(provider);
			if (conversionType == typeof(object)) return AsObject();
			throw new InvalidCastException($"Cannot convert Val to {conversionType.Name}.");
		}
		#endregion

		#region Operators
		public static KsValue operator +(in KsValue a, in KsValue b) => Add(a, b);
		public static KsValue operator -(in KsValue a, in KsValue b) => Subtract(a, b);
		public static KsValue operator *(in KsValue a, in KsValue b) => Multiply(a, b);
		public static KsValue operator /(in KsValue a, in KsValue b) => Divide(a, b);
		public static KsValue operator %(in KsValue a, in KsValue b) => Modulus(a, b);
		public static KsValue operator <<(in KsValue a, in KsValue b) => BitShiftLeft(a, b);
		public static KsValue operator >>(in KsValue a, in KsValue b) => BitShiftRight(a, b);
		public static KsValue operator &(in KsValue a, in KsValue b) => BitwiseAnd(a, b);
		public static KsValue operator |(in KsValue a, in KsValue b) => BitwiseOr(a, b);
		public static KsValue operator ^(in KsValue a, in KsValue b) => BitwiseXor(a, b);

		public static KsValue operator -(in KsValue v)
			=> v.Type switch
			{
				KsValueType.Integer => new KsValue(-v._i64, v.Anchor),
				KsValueType.Double => new KsValue(-v._f64, v.Anchor),
				_ => new KsValue(-(v.TryGetDouble(out var d) ? d : 0.0), v.Anchor)
			};

		public static KsValue operator +(in KsValue v) => v;
		public static KsValue operator !(in KsValue v) => v.AsBool() ? False : True;
		#endregion

		#region Implicit conversions
		public static implicit operator KsValue(long v) => KsValueLongCache.Get(v);
		public static implicit operator KsValue(int v) => KsValueLongCache.Get((long)v);
		public static implicit operator KsValue(double v) => new KsValue(v);
		public static implicit operator KsValue(string v) => new KsValue(v);
		public static implicit operator KsValue(bool v) => v ? True : False;
		#endregion

		#region Explicit conversions
		public static explicit operator long(in KsValue v)
		{
			if (v.IsUnset) throw Errors.UnsetError("Long conversion target is unset");
			if (v.TryGetLong(out var l)) return l;
			if (v.TryGetDouble(out var d)) return (long)d;
			throw new InvalidCastException("KsValue is not numeric.");
		}
		public static explicit operator int(in KsValue v)
		{
			if (v.IsUnset) throw Errors.UnsetError("Int conversion target is unset");
			if (v.TryGetLong(out var l)) return (int)l;
			if (v.TryGetDouble(out var d)) return (int)d;
			throw new InvalidCastException("KsValue is not numeric.");
		}
		public static explicit operator string(in KsValue v)
		{
			if (v.IsUnset) throw Errors.UnsetError("String conversion target is unset");
			if (v.TryGetString(out var s)) return s;
			throw new InvalidCastException("KsValue is not a primitive value.");
		}
		public static explicit operator Any(in KsValue v)
		{
			if (v.IsUnset) throw Errors.UnsetError("Any conversion target is unset");
			if (v.Type == KsValueType.Any) return (Any)v._ref;
			throw new InvalidCastException("KsValue is not of type Any.");
		}
		#endregion
	}

	public static class KsValueExtensions
	{
		public static bool IsNumeric(this KsValue v)
			=> v.TryGetLong(out _) || v.TryGetDouble(out _);

		public static bool Ab(this KsValue v) => v.AsBool();
		public static bool Ab(this KsValue v, bool def) => v.IsUnset ? def : v.AsBool();

		public static long Al(this KsValue v)
		{
			if (v.IsUnset) throw Errors.UnsetError("Long conversion target is unset");
			if (v.TryGetLong(out var l)) return l;
			if (v.TryGetDouble(out var d)) return (long)d;
			throw new TypeError($"Cannot convert '{nameof(v.Type)}' to long."); 
		}
		public static long Al(this KsValue v, long def)
			=> v.IsUnset ? def : (v.TryGetLong(out var l) ? l : v.TryGetDouble(out var d) ? (long)d : def);

		public static double Ad(this KsValue v)
		{
			if (v.IsUnset) throw Errors.UnsetError("Double conversion target is unset");
			return v.TryGetDouble(out var d) ? d : throw new TypeError("Conversion to double failed");
		}
		public static double Ad(this KsValue v, double def)
			=> v.IsUnset ? def : (v.TryGetDouble(out var d) ? d : def);

		public static string As(this KsValue v)
		{
			if (v.IsUnset) throw Errors.UnsetError("String conversion target is unset");
			return v.TryGetString(out var s) ? s : throw new TypeError("Conversion to string failed");
		}
		public static string As(this KsValue v, string def)
			=> v.IsUnset ? def : (v.TryGetString(out var s) ? s : def);

		public static object Ao(this KsValue v) => v.AsObject();

		public static object Aunk(this KsValue v) => v.AsObject();
		public static object Aunk(this KsValue v, object def) => v.IsUnset ? def : v.AsObject();

		public static bool IsLong(this KsValue v) => v.TryGetLong(out _);
		public static bool IsLong(this KsValue v, out long value) => v.TryGetLong(out value);

		public static bool IsDouble(this KsValue v) => v.TryGetDouble(out _);
		public static bool IsDouble(this KsValue v, out double value) => v.TryGetDouble(out value);

		public static bool IsString(this KsValue v) => v.TryGetString(out _);
		public static bool IsString(this KsValue v, [NotNullWhen(true)] out string value) => v.TryGetString(out value);

		public static bool IsObject(this KsValue v) => v.Type != KsValueType.Unset;
		public static bool IsObject(this KsValue v, [NotNullWhen(true)] out object value)
		{
			value = v.AsObject()!;
			return v.Type != KsValueType.Unset;
		}

		// Helper to create KsValue from arbitrary objects
		public static KsValue AsKsValue(this object v)
		{
			if (v is null) return default;
			if (v is KsValue av) return av;

			if (v is bool b) return b ? KsValue.True : KsValue.False;
			if (v is string s) return new KsValue(s);

			switch (Type.GetTypeCode(v.GetType()))
			{
				case TypeCode.SByte: return new KsValue((long)(sbyte)v);
				case TypeCode.Byte: return new KsValue((long)(byte)v);
				case TypeCode.Int16: return new KsValue((long)(short)v);
				case TypeCode.UInt16: return new KsValue((long)(ushort)v);
				case TypeCode.Int32: return new KsValue((long)(int)v);
				case TypeCode.UInt32: return new KsValue((long)(uint)v);
				case TypeCode.Int64: return new KsValue((long)v);
				case TypeCode.UInt64: return new KsValue((long)(ulong)v);
				case TypeCode.Single: return new KsValue((double)(float)v);
				case TypeCode.Double: return new KsValue((double)v);
				case TypeCode.Decimal: return new KsValue((double)(decimal)v);
				case TypeCode.Char: return new KsValue(((char)v).ToString());
				case TypeCode.DateTime: return new KsValue(((DateTime)v).ToString(CultureInfo.InvariantCulture));
				default: return new KsValue(v);
			}
		}
	}

	/// <summary>TypeConverter for KsValue to/from primitives.</summary>
	public sealed class KsValueTypeConverter : TypeConverter
	{
		private static readonly HashSet<Type> Supported = new()
		{
			typeof(KsValue), typeof(bool), typeof(char), typeof(sbyte), typeof(byte),
			typeof(short), typeof(ushort), typeof(int), typeof(uint),
			typeof(long), typeof(ulong), typeof(float), typeof(double),
			typeof(decimal), typeof(string), typeof(DateTime), typeof(object)
		};

		public override bool CanConvertFrom(ITypeDescriptorContext ctx, Type src)
			=> Supported.Contains(src) || typeof(IConvertible).IsAssignableFrom(src);

		public override bool CanConvertTo(ITypeDescriptorContext ctx, Type dst)
			=> Supported.Contains(dst) || typeof(IConvertible).IsAssignableFrom(dst);

		public override object ConvertFrom(ITypeDescriptorContext ctx, CultureInfo culture, object value)
		{
			if (value is null) return null;
			if (value is KsValue av) return av;

			if (value is IConvertible ic)
			{
				culture ??= CultureInfo.InvariantCulture;

				switch (ic.GetTypeCode())
				{
					case TypeCode.Boolean: return ic.ToBoolean(culture) ? KsValue.True : KsValue.False;
					case TypeCode.Char: return new KsValue(ic.ToChar(culture).ToString());
					case TypeCode.SByte: return new KsValue((long)ic.ToSByte(culture));
					case TypeCode.Byte: return new KsValue((long)ic.ToByte(culture));
					case TypeCode.Int16: return new KsValue((long)ic.ToInt16(culture));
					case TypeCode.UInt16: return new KsValue((long)ic.ToUInt16(culture));
					case TypeCode.Int32: return new KsValue((long)ic.ToInt32(culture));
					case TypeCode.UInt32: return new KsValue((long)ic.ToUInt32(culture));
					case TypeCode.Int64: return new KsValue(ic.ToInt64(culture));
					case TypeCode.UInt64: return new KsValue((long)ic.ToUInt64(culture));
					case TypeCode.Single: return new KsValue((double)ic.ToSingle(culture));
					case TypeCode.Double: return new KsValue(ic.ToDouble(culture));
					case TypeCode.Decimal: return new KsValue((double)ic.ToDecimal(culture));
					case TypeCode.String: return new KsValue(ic.ToString(culture)!);
					case TypeCode.DateTime: return new KsValue(ic.ToDateTime(culture).ToString(culture));
				}
			}

			return base.ConvertFrom(ctx, culture, value);
		}

		public override object ConvertTo(ITypeDescriptorContext ctx, CultureInfo culture, object value, Type destinationType)
		{
			if (value is not KsValue v)
				return base.ConvertTo(ctx, culture, value, destinationType);

			culture ??= CultureInfo.InvariantCulture;

			if (destinationType == typeof(KsValue)) return v;

			if (destinationType == typeof(bool))
				return v.AsBool();

			if (destinationType == typeof(char))
			{
				if (v.TryGetString(out var s) && s.Length == 1) return s[0];
				throw new InvalidCastException("Cannot convert KsValue to Char.");
			}

			if (destinationType == typeof(string))
			{
				if (v.TryGetString(out var s)) return s;
				throw new InvalidCastException("Cannot convert KsValue to String.");
			}

			if (destinationType == typeof(long))
			{
				if (v.TryGetLong(out var l)) return l;
				if (v.TryGetDouble(out var d)) return (long)d;
				throw new InvalidCastException("Cannot convert AhkValue to Int64.");
			}
			if (destinationType == typeof(ulong))
			{
				if (v.TryGetLong(out var l)) return checked((ulong)l);
				if (v.TryGetDouble(out var d)) return checked((ulong)(long)d);
				throw new InvalidCastException("Cannot convert AhkValue to UInt64.");
			}

			if (destinationType == typeof(int)) return checked((int)(long)ConvertTo(ctx, culture, v, typeof(long))!);
			if (destinationType == typeof(uint)) return checked((uint)(long)ConvertTo(ctx, culture, v, typeof(long))!);
			if (destinationType == typeof(short)) return checked((short)(long)ConvertTo(ctx, culture, v, typeof(long))!);
			if (destinationType == typeof(ushort)) return checked((ushort)(long)ConvertTo(ctx, culture, v, typeof(long))!);
			if (destinationType == typeof(sbyte)) return checked((sbyte)(long)ConvertTo(ctx, culture, v, typeof(long))!);
			if (destinationType == typeof(byte)) return checked((byte)(long)ConvertTo(ctx, culture, v, typeof(long))!);

			if (destinationType == typeof(double))
			{
				if (v.TryGetDouble(out var d)) return d;
				if (v.TryGetLong(out var l)) return (double)l;
				throw new InvalidCastException("Cannot convert KsValue to Double.");
			}
			if (destinationType == typeof(float)) return (float)(double)ConvertTo(ctx, culture, v, typeof(double))!;
			if (destinationType == typeof(decimal)) return (decimal)(double)ConvertTo(ctx, culture, v, typeof(double))!;

			if (destinationType == typeof(DateTime))
			{
				if (v.TryGetString(out var s) && DateTime.TryParse(s, culture, DateTimeStyles.None, out var dt))
					return dt;
				throw new InvalidCastException("Cannot convert KsValue to DateTime.");
			}

			// Last-ditch: if they asked for object, hand back the native object view
			if (destinationType == typeof(object))
				return v.AsObject();

			return base.ConvertTo(ctx, culture, value, destinationType);
		}
	}
}
