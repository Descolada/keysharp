using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Keysharp.Core.Windows;

namespace Keysharp.Scripting
{
	public partial class Script
	{
		public static readonly KsValue True = KsValueLongCache.Get(1L);
		public static readonly KsValue False = KsValueLongCache.Get(0L);
		public static readonly KsValue Unset = default;
	}
	public enum KsValueType : byte { Unset, Integer, Float, String, Any, External }

	[TypeConverter(typeof(KsValueTypeConverter))]
	[StructLayout(LayoutKind.Explicit)]
	[DebuggerDisplay("{DebuggerDisplay,nq}")]
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

		static Error _err;

		public KsValue(long v, object anchor = null) { _i64 = v; _ref = anchor; Type = KsValueType.Integer; }
		public KsValue(double v, object anchor = null) { _f64 = v; _ref = anchor; Type = KsValueType.Float; }
		public KsValue(string s) { _ref = s; Type = s == null ? KsValueType.Unset : KsValueType.String; }
		public KsValue(Any o) { _ref = o; Type = o == null ? KsValueType.Unset : KsValueType.Any; }
		public KsValue(object o)
		{
			if (o == null) { Type = KsValueType.Unset; }
			else if (o is Any a) { _ref = a; Type = KsValueType.Any; }
			else if (o is long l) { _i64 = l; Type = KsValueType.Integer; }
			else if (o is double d) { _f64 = d; Type = KsValueType.Float; }
			else if (o is string s) { _ref = s; Type = KsValueType.String; }
			else { _ref = o; Type = KsValueType.External; }
		}

		public static KsValue FromObject(object v)
		{
			if (v is null) return default;
			else if (v is KsValue av) return av;
			else if (v is bool b) return b ? True : False;
			else if (v is string s) return new KsValue(s);

			switch (System.Type.GetTypeCode(v.GetType()))
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

		private object Anchor => Type is KsValueType.Integer or KsValueType.Float or KsValueType.External or KsValueType.Any ? _ref : null;
		public bool IsUnset => Type == KsValueType.Unset;
		public bool IsSet => Type != KsValueType.Unset;
		public bool IsInteger => Type == KsValueType.Integer;
		public bool IsFloat => Type == KsValueType.Float;
		public bool IsString => Type == KsValueType.String;
		public bool IsAny => Type == KsValueType.Any;
		public bool IsExternal => Type == KsValueType.External;
		public bool IsObject => Type == KsValueType.Any || Type == KsValueType.External;
		public bool IsPrimitive => Type == KsValueType.Integer || Type == KsValueType.Float || Type == KsValueType.String;

		public readonly KsValue Default(in KsValue value) => IsUnset ? value : this;
		public readonly Type GetInternalType()
		{
			switch (Type)
			{
				case KsValueType.Integer: return typeof(long);
				case KsValueType.Float: return typeof(double);
				default:
					return _ref.GetType();
			}
		}

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
				case KsValueType.Float: value = _f64; return true;
				case KsValueType.Integer: value = (double)_i64; return true;
				case KsValueType.String:
					if (double.TryParse(GetSpan(), NumberStyles.Any, CultureInfo.InvariantCulture, out value))
						return true;
					if (TryParseInt64(out var i))
					{ value = i; return true; }
					value = default; return false;
				default:
					value = default; return false;
			}
		}

		private readonly string GetString() => _ref is string s ? s : _ref is StringBuffer sb ? sb.ToString() : throw new Exception("Unknown string type");
		private readonly ReadOnlySpan<char> GetSpan() => _ref is string s ? s.AsSpan() : _ref is StringBuffer sb ? sb.AsSpan() : throw new Exception("Unknown string type");

		public readonly bool TryGetString(out string value)
		{
			switch (Type)
			{
				case KsValueType.String: value = GetString(); return true;
				case KsValueType.Integer: value = _i64.ToString(CultureInfo.InvariantCulture); return true;
				case KsValueType.Float: value = _f64.ToString(CultureInfo.InvariantCulture); return true;
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

		public readonly bool TryGetExternal(out object value)
		{
			if (Type == KsValueType.External)
			{
				value = _ref; return true;
			}
			value = null; return false;
		}

		public readonly object AsObject()
			=> Type switch
			{
				KsValueType.Integer => _i64,
				KsValueType.Float => _f64,
				KsValueType.String => _ref,
				KsValueType.Any => _ref,
				KsValueType.External => _ref,
				_ => null
			};

		public readonly bool AsBool()
		{ 
			switch (Type)
			{
				case KsValueType.Integer: return _i64 != 0L;
				case KsValueType.Float: return _f64 != 0.0;
				case KsValueType.String:
					if (TryGetDouble(out double v))
						return v != 0.0;
					return GetString() != string.Empty;
				case KsValueType.Unset:
					throw new UnsetError();
				default:
					return true;
			}
		}

		public override string ToString()
		{
			if (TryGetString(out string value))
				return value;
			return _ref?.ToString();
		}
		#endregion

		// ---------- Numeric parsing helpers ----------
		private readonly bool TryParseInt64(out long ll)
		{
			ReadOnlySpan<char> s = GetSpan().Trim();
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
				else if (b.Type == KsValueType.Float) return new KsValue(a._i64 + b._f64, anchor);
			}
			else if (a.Type == KsValueType.Float)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(a._f64 + b._i64, anchor);
				else if (b.Type == KsValueType.Float) return new KsValue(a._f64 + b._f64, anchor);
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
				else if (b.Type == KsValueType.Float) return new KsValue(a._i64 - b._f64, anchor);
			}
			else if (a.Type == KsValueType.Float)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(a._f64 - b._i64, anchor);
				else if (b.Type == KsValueType.Float) return new KsValue(a._f64 - b._f64, anchor);
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
				else if (b.Type == KsValueType.Float) return new KsValue(a._i64 * b._f64);
			}
			else if (a.Type == KsValueType.Float)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(a._f64 * b._i64);
				else if (b.Type == KsValueType.Float) return new KsValue(a._f64 * b._f64);
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
				else if (b.Type == KsValueType.Float) return (b._f64 == 0.0) ? (KsValue)Errors.ZeroDivisionErrorOccurred("Right side operand of floating point division") : new KsValue(a._i64 / b._f64);
			}
			else if (a.Type == KsValueType.Float)
			{
				if (b.Type == KsValueType.Integer) return (b._i64 == 0L) ? (KsValue)Errors.ZeroDivisionErrorOccurred("Right side operand of floating point division") : new KsValue(a._f64 / b._i64);
				else if (b.Type == KsValueType.Float) return (b._f64 == 0.0) ? (KsValue)Errors.ZeroDivisionErrorOccurred("Right side operand of floating point division") : new KsValue(a._f64 / b._f64);
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

		public static KsValue FloorDivide(in KsValue a, in KsValue b)
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
				return new KsValue(la / lb);
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
				else if (b.Type == KsValueType.Float) return new KsValue(a._i64 % b._f64);
			}
			else if (a.Type == KsValueType.Float)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(a._f64 % b._i64);
				else if (b.Type == KsValueType.Float) return new KsValue(a._f64 % b._f64);
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
				if (b.Type == KsValueType.Integer) return new KsValue((long)Math.Pow(a._i64, b._i64));
				else if (b.Type == KsValueType.Float) return new KsValue(Math.Pow(a._i64, b._f64));
			}
			else if (a.Type == KsValueType.Float)
			{
				if (b.Type == KsValueType.Integer) return new KsValue(Math.Pow(a._f64, b._i64));
				else if (b.Type == KsValueType.Float) return new KsValue(Math.Pow(a._f64, b._f64));
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
			KsValue matches = default;
			_ = Core.RegEx.RegExMatch(a.As(), b.As(), new VarRef(() => matches, value => matches = value), 1);
			return matches;
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
		public static KsValue LogicalBitShiftRight(in KsValue a, in KsValue b)
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
				if (b.Type == KsValueType.Float) return a._i64 == b._f64 ? True : False;
			}
			else if (a.Type == KsValueType.Float)
			{
				if (b.Type == KsValueType.Integer) return a._f64 == b._i64 ? True : False;
				if (b.Type == KsValueType.Float) return a._f64 == b._f64 ? True : False;
			}
			else if (a.Type == KsValueType.String && b.Type == KsValueType.String)
			{
				return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal) ? True : False;
			}
			else if (a.Type == KsValueType.Any && b.Type == KsValueType.Any)
			{
				return a._ref == b._ref ? True : False;
			}
			else if (a.Type == KsValueType.External && b.Type == KsValueType.External)
			{
				return a._ref == b._ref ? True : False;
			}
			else if (a.Type == KsValueType.Any || b.Type == KsValueType.Any ||
					 a.Type == KsValueType.External || b.Type == KsValueType.External)
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
				if (b.Type == KsValueType.Float) return a._i64 == b._f64 ? True : False;
			}
			else if (a.Type == KsValueType.Float)
			{
				if (b.Type == KsValueType.Integer) return a._f64 == b._i64 ? True : False;
				if (b.Type == KsValueType.Float) return a._f64 == b._f64 ? True : False;
			}
			else if (a.Type == KsValueType.String && b.Type == KsValueType.String)
			{
				return string.Equals(a.GetString(), b.GetString(), StringComparison.OrdinalIgnoreCase) ? True : False;
			}
			else if (a.Type == KsValueType.Any && b.Type == KsValueType.Any)
			{
				if (a._ref is Core.Array al1 && b._ref is Core.Array al2)
				{
					var len1 = (long)al1.Length;
					var len2 = (long)al2.Length;

					if (len1 != len2)
						return False;

					for (var i = 1; i <= len1; i++)
					{
						if (IdentityInequality(al1[i], al2[i]))
							return False;
					}

					return True;
				}
				else if (a._ref is Core.Buffer buf1 && b._ref is Core.Buffer buf2)
				{
					var len1 = (long)buf1.Size;
					var len2 = (long)buf2.Size;

					if (len1 != len2)
						return False;

					for (var i = 1; i <= len1; i++)
					{
						if (buf1[i] != buf2[i])
							return False;
					}

					return True;
				}
				return a._ref == b._ref ? True : False;
			}
			else if (a.Type == KsValueType.External && b.Type == KsValueType.External)
			{
				return a._ref == b._ref ? True : False;
			}
			else if (a.Type == KsValueType.Any || b.Type == KsValueType.Any ||
					 a.Type == KsValueType.External || b.Type == KsValueType.External)
			{
				return False;
			}

			if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return la == lb ? True : False;
			if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db)) return da == db ? True : False;

			return False;
		}
		public static KsValue ValueInequality(in KsValue a, in KsValue b)
			=> ValueEquality(a, b).AsBool() ? False : True;

		public static KsValue Is(KsValue a, KsValue b)
		{
			if (b.IsUnset)
				return a.IsUnset;

			if (!b.TryGetString(out string test))
				return false;
			test = test.ToLower();

			//Put common cases first.
			switch (test)
			{
				case "integer":
					return a.IsInteger;

				case "float":
					return a.IsFloat;

				case "number":
					return a.IsInteger || a.IsFloat;

				case "string":
					return a.IsString;

				case "unset":
				case "null":
					return a.IsUnset;
			}

			if (a.IsUnset) return (bool)Errors.UnsetErrorOccurred("Left side operand was unset", false);

			var alias = Keywords.TypeNameAliases.FirstOrDefault(kvp => kvp.Value.Equals(test, StringComparison.OrdinalIgnoreCase)).Key;
			if (alias != null)
				test = alias;

			//Traverse class hierarchy to see if there is a match.
			object o = a.AsObject();
			var type = o.GetType();

			return Parser.IsTypeOrBase(type, test);
		}

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

		public static KsValue Plus(in KsValue v)
		{
			if (v.IsUnset)
				return (bool)Errors.UnsetErrorOccurred("Plus operand target was unset", false);
			if (v.TryGetLong(out long l))
				return l;
			if (v.TryGetDouble(out double d))
				return d;
			return Errors.ErrorOccurred(_err = new TypeError($"Plus operand was not numeric."))
					? throw _err
					: DefaultErrorObject;
		}
		public static KsValue Minus(long v) => -v;
		public static KsValue Minus(double v) => -v;
		public static KsValue Minus(in KsValue v) {
			if (v.IsUnset)
				return (bool)Errors.UnsetErrorOccurred("Minus target was unset", false);
			if (v.TryGetLong(out long l))
				return -l;
			if (v.TryGetDouble(out double d))
				return -d;
			return Errors.ErrorOccurred(_err = new TypeError($"Minus operand was not numeric."))
					? throw _err
					: DefaultErrorObject;
		}
		public static KsValue BitwiseNot(long v) => ~v;
		public static KsValue BitwiseNot(in KsValue v) {
			if (v.IsUnset)
				return (bool)Errors.UnsetErrorOccurred("Bitwise-not target was unset", false);
			if (v.TryGetLong(out long l))
				return ~l;
			return (long)Errors.TypeErrorOccurred(v, typeof(long), 0L);
		}
		public static KsValue Not(string v) => v == "" ? True : False;
		public static KsValue Not(long v) => v == 0L ? True : False;
		public static KsValue Not(in KsValue v) => v.AsBool() ? False : True;
		#endregion

		#region IConvertible implementations
		public TypeCode GetTypeCode()
		{
			switch (Type)
			{
				case KsValueType.Integer: return TypeCode.Int64;
				case KsValueType.Float: return TypeCode.Double;
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
		public static KsValue operator ^(in KsValue a, in KsValue b) => BitwiseXor(a, b);
		public static KsValue operator -(in KsValue v) => Minus(v);
		public static KsValue operator &(in KsValue a, in KsValue b) => BooleanAnd(a, b);
		public static KsValue operator |(in KsValue a, in KsValue b) => BooleanOr(a, b);
		public static KsValue operator +(in KsValue v) => Plus(v);
		public static KsValue operator ~(in KsValue v) => BitwiseNot(v);
		public static KsValue operator !(in KsValue v) => Not(v);

		public static bool operator true(in KsValue v) => v.IsUnset ? (bool)Errors.UnsetErrorOccurred("Boolean conversion target was unset", false) : v.AsBool();
		public static bool operator false(in KsValue v) => v.IsUnset ? (bool)Errors.UnsetErrorOccurred("Boolean conversion target was unset", true) : !v.AsBool();
		#endregion

		#region Implicit conversions

		public static implicit operator KsValue(long v) => KsValueLongCache.Get(v);
		public static implicit operator KsValue(int v) => KsValueLongCache.Get((long)v);
		public static implicit operator KsValue(double v) => new KsValue(v);
		public static implicit operator KsValue(string v) => new KsValue(v);
		public static implicit operator KsValue(bool v) => v ? True : False;
		public static implicit operator KsValue(Any v) => new KsValue(v);
		public static implicit operator KsValue((Any, KsValue) v) => new KsValue(v);
		#endregion

		#region Explicit conversions
		public static implicit operator double(in KsValue v)
		{
			if (v.IsUnset) throw Errors.UnsetError("Double conversion target is unset");
			if (v.TryGetDouble(out var d)) return d;
			throw new InvalidCastException("KsValue is not numeric.");
		}
		public static implicit operator long(in KsValue v)
		{
			if (v.IsUnset) throw Errors.UnsetError("Long conversion target is unset");
			if (v.TryGetLong(out var l)) return l;
			if (v.TryGetDouble(out var d)) return (long)d;
			throw new InvalidCastException("KsValue is not numeric.");
		}
		public static implicit operator int(in KsValue v)
		{
			if (v.IsUnset) throw Errors.UnsetError("Int conversion target is unset");
			if (v.TryGetLong(out var l)) return (int)l;
			if (v.TryGetDouble(out var d)) return (int)d;
			throw new InvalidCastException("KsValue is not numeric.");
		}
		public static implicit operator string(in KsValue v)
		{
			if (v.IsUnset) throw Errors.UnsetError("String conversion target is unset");
			if (v.TryGetString(out var s)) return s;
			throw new InvalidCastException("KsValue is not a primitive value.");
		}
		public static implicit operator Any(in KsValue v)
		{
			if (v.IsUnset) throw Errors.UnsetError("Any conversion target is unset");
			if (v.Type == KsValueType.Any) return (Any)v._ref;
			throw new InvalidCastException("KsValue is not of type Any.");
		}
		#endregion

		private string DebuggerDisplay
		{
			get
			{
				// Keep this side-effect free and resilient — debugger may evaluate often.
				try
				{
					switch (Type)
					{
						case KsValueType.Integer:
							return _ref is null
								? $"Integer {_i64}"
								: $"Integer {_i64} (anchor={_ref.GetType().Name})";

						case KsValueType.Float:
							return _ref is null
								? $"Double {_f64.ToString("G", CultureInfo.InvariantCulture)}"
								: $"Double {_f64.ToString("G", CultureInfo.InvariantCulture)} (anchor={_ref.GetType().Name})";

						case KsValueType.String:
							var s = _ref as string ?? "";
							var preview = s.Length <= 64 ? s : s.Substring(0, 61) + "…";
							return $"String (len={s.Length}) \"{preview}\"";

						case KsValueType.External:
							return $"External {_ref?.GetType().Name ?? "null"}";

						case KsValueType.Any:
							return $"Any {_ref?.GetType().Name ?? "null"}";

						default:
							return "Empty";
					}
				}
				catch
				{
					// Never throw in debugger display
					return "<display error>";
				}
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void EnsureDefault(ref KsValue v, long d) { if (v.IsUnset) v = d; }
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void EnsureDefault(ref KsValue v, double d) { if (v.IsUnset) v = d; }
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void EnsureDefault(ref KsValue v, string d) { if (v.IsUnset) v = d; }
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void EnsureDefault(ref KsValue v, in KsValue d) { if (v.IsUnset) v = d; }

		public bool IsNumeric() => TryGetLong(out _) || TryGetDouble(out _);

		public bool Ab() => AsBool();
		public bool Ab(bool def) => IsUnset ? def : AsBool();

		public int Ai() => unchecked((int)Al());
		public int Ai(int def) => IsUnset ? def : unchecked((int)Al());
		public long Al()
		{
			if (IsUnset) throw Errors.UnsetError("Long conversion target is unset");
			if (TryGetLong(out var l)) return l;
			if (TryGetDouble(out var d)) return (long)d;
			throw new TypeError($"Cannot convert '{nameof(Type)}' to long.");
		}
		public long Al(long def) => IsUnset ? def : (TryGetLong(out var l) ? l : TryGetDouble(out var d) ? (long)d : def);

		public double Ad()
		{
			if (IsUnset) throw Errors.UnsetError("Double conversion target is unset");
			return TryGetDouble(out var d) ? d : throw new TypeError("Conversion to double failed");
		}
		public double Ad(double def)
			=> IsUnset ? def : (TryGetDouble(out var d) ? d : def);

		public string As()
		{
			if (IsUnset) throw Errors.UnsetError("String conversion target is unset");
			return TryGetString(out var d) ? d : throw new TypeError("Conversion to string failed");
		}

		public string As(string def)
		{
			if (IsUnset) return def;
			return TryGetString(out var d) ? d : throw new TypeError("Conversion to string failed");
		}

		public object Ao() => AsObject();
		public object Ao(object def) => IsUnset ? def : AsObject();

		public bool IsLong() => TryGetLong(out _);
		public bool IsDouble() => TryGetDouble(out _);

		public static IEnumerable<KsValue> CastEnumerable(IEnumerable source)
		{
			foreach (var o in source)
				yield return new KsValue(o);
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
					case TypeCode.Boolean: return ic.ToBoolean(culture) ? True : False;
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
				throw new InvalidCastException("Cannot convert KsValue to Int64.");
			}
			if (destinationType == typeof(ulong))
			{
				if (v.TryGetLong(out var l)) return checked((ulong)l);
				if (v.TryGetDouble(out var d)) return checked((ulong)(long)d);
				throw new InvalidCastException("Cannot convert KsValue to UInt64.");
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
}