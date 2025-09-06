namespace Keysharp.Core.Common.ObjectBase
{
	/// <summary>
	/// Base abstract class for primitive script values (long, double, string), with per-instance lazy caches.
	/// </summary>
	[TypeConverter(typeof(PrimitiveTypeConverter))]
	public abstract class Primitive : Any, IConvertible
	{
		/// <summary>Used as a secondary container for any accompanying objects such
		/// as an IPointable for LongPrimitive or StringBuffer for StringPrimitive.</summary>
		public object Payload;

		public override Any Base 
		{
			get
			{
				if (_base == null)
				{
					var dict = TheScript.Vars.Statics;
					var t = GetType();
					TheScript.Vars.Statics.TryGetValue(GetType(), out Any value);
					_base = (Any)value.op["prototype"].Value;
				}
				return _base;
			}
		}

		public Primitive() : base(skipLogic: true) { }

		public static LongPrimitive True = LongCache.Get(1);
		public static LongPrimitive False = LongCache.Get(0);

		/// <summary>Factory methods for each subtype.</summary>
		public static LongPrimitive From(long v) => LongCache.Get(v);
		public static DoublePrimitive From(double v) => new DoublePrimitive(v);
		public static StringPrimitive From(string v) => new StringPrimitive(v);

		public static LongPrimitive From(bool v) => v ? True : False;
		public static Primitive From(object v) {
			if (v == null) throw Errors.UnsetError("Provided argument was unset");
			return TryCoercePrimitive(v, out Primitive p) ? p : throw Errors.ValueError("Provided argument was not a primitive value");
		}
		public static Primitive NumericFrom(object v)
		{
			if (v == null) throw Errors.UnsetError("Provided argument was unset");
			return TryCoerceNumericPrimitive(v, out Primitive p) ? p : throw Errors.ValueError("Provided argument was not a primitive numeric value");
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static Primitive Require(object value, [CallerArgumentExpression("value")] string param = null)
		{
			if (value is Primitive p) return p;
			return Errors.ErrorOccurred(_err = new TypeError($"{param ?? "Value"} is not a primitive.")) ? throw _err : DefaultErrorObject;
		}

		/// <summary>Implicit conversions</summary>
		public static implicit operator Primitive(long v) => From(v);
		public static implicit operator Primitive(double v) => From(v);
		public static implicit operator Primitive(string v) => From(v);
		public static implicit operator Primitive(bool v) => From(v);

		public static implicit operator double(Primitive v)
		{
			if (v == null) throw Errors.UnsetError("Double conversion target is unset");
			if (v.TryGetDouble(out var d)) return d;
			throw Errors.ValueError("Value is not numeric.");
		}
		public static implicit operator long(Primitive v)
		{
			if (v == null) throw Errors.UnsetError("Long conversion target is unset");
			if (v.TryGetLong(out var l)) return l;
			if (v.TryGetDouble(out var d)) return (long)d;
			throw Errors.ValueError("Value is not numeric.");
		}
		public static implicit operator nint(Primitive v)
		{
			if (v == null) throw Errors.UnsetError("Pointer conversion target is unset");
			if (v.TryGetLong(out var l)) return (nint)l;
			throw Errors.ValueError("Value is not an integer.");
		}
		public static explicit operator int(Primitive v)
		{
			if (v == null) throw Errors.UnsetError("Int conversion target is unset");
			if (v.TryGetLong(out var l)) return (int)l;
			if (v.TryGetDouble(out var d)) return (int)d;
			throw Errors.ValueError("Value is not numeric.");
		}
		public static implicit operator bool(Primitive v)
		{
			if (v == null) throw Errors.UnsetError("Bool conversion target is unset");
			return v.IsTrue;
		}
		public static implicit operator string(Primitive v)
		{
			if (v == null) throw Errors.UnsetError("String conversion target is unset");
			return v.ToString();
		}

		/// <summary>Try to get a long. Succeeds only if the value is a pure long
		/// or a string in pure long format.</summary>
		public abstract bool TryGetLong(out long value);
		/// <summary>Try to get a double. Returns false if conversion is not possible,
		/// meaning when it's not a pure long, double, or a string in long or double format.</summary>
		public abstract bool TryGetDouble(out double value);
		/// <summary>Always returns the underlying object or boxed primitive.</summary>
		public abstract object AsObject();
		/// <summary>Always returns whether the value evaluates to true.</summary>
		public abstract bool IsTrue { get; }
		/// <summary>Returns whether the value is a long, double, or numeric string.</summary>
		public abstract bool IsNumeric { get; }
		public abstract bool IsLong { get; }
		public abstract bool IsDouble { get; }
		public abstract bool IsString { get; }

		// Used as a temporary variable to throw errors.
		static Error _err;

		public static Primitive ForceNumeric(Primitive p)
		{
			if (p is StringPrimitive)
			{
				if (p.TryGetLong(out long ll))
					return ll;
				else if (p.TryGetDouble(out double dd))
					return dd;
				return Errors.ErrorOccurred(_err = new TypeError($"Value {p} was not numeric.")) ? throw _err : 0L;
			}
			return p;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool TryCoerceNumericPrimitive(object value, out Primitive p)
		{
			switch (value)
			{
				case LongPrimitive pp: p = pp; return true;
				case DoublePrimitive dd: p = dd; return true;
				case StringPrimitive sp:
					if (sp.TryGetLong(out long ll))
					{
						p = ll; return true;
					} 
					else if (sp.TryGetDouble(out double dd))
					{
						p = dd; return true;
					}
					p = null!; return false;
				case long l: p = LongCache.Get(l); return true;
				case int i: p = LongCache.Get(i); return true;
				case bool b: p = b ? True : False; return true;
				case double d: p = new DoublePrimitive(d); return true;
				case float f: p = new DoublePrimitive(f); return true;
				case string s:
					StringPrimitive ss = s;
					if (ss.TryGetLong(out long sll))
					{
						p = sll; return true;
					}
					else if (ss.TryGetDouble(out double dd))
					{
						p = dd; return true;
					}
					p = null!; return false;
				default:
					try
					{
						p = Convert.ToInt64(value); return true;
					} 
					catch
					{
					}
					try
					{
						p = Convert.ToDouble(value); return true;
					}
					catch
					{
					}
					p = null!; return false;
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool TryCoercePrimitive(object value, out Primitive p)
		{
			switch (value)
			{
				case Primitive pp: p = pp; return true;
				case long l: p = LongCache.Get(l); return true;
				case int i: p = LongCache.Get(i); return true;
				case bool b: p = b ? True : False; return true;
				case double d: p = new DoublePrimitive(d); return true;
				case float f: p = new DoublePrimitive(f); return true;
				case string s: p = From(s); return true;
				default: p = null!; return false;
			}
		}

		public static Primitive Add(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of addition", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of addition", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return LongCache.Get(la + lb, a.Payload ?? b.Payload);
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da + db;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive Subtract(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of substraction", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of substraction", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return LongCache.Get(la - lb, a.Payload ?? b.Payload);
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da - db;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive Multiply(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of multiplication", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of multiplication", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la * lb;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da * db;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive Divide(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of divison", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of divison", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				{
					if (lb == 0L) return Errors.ZeroDivisionErrorOccurred("Right side operand of integer division");
					//if (la % lb == 0) return la / lb;
					return (double)la / lb;
				}
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				{
					if (db == 0.0) return Errors.ZeroDivisionErrorOccurred("Right side operand of floating point division");
					return da / db;
				}
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive FloorDivide(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of floor division", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of floor division", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				{
					if (lb == 0L) return Errors.ZeroDivisionErrorOccurred("Right side operand of floor divide");
					return la / lb;
				}
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive Modulus(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of modulus", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of modulus", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la % lb;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da % db;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive Power(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of power", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of power", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return Math.Pow(da, db);
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return (long)Math.Pow(la, lb);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive BitShiftLeft(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of arithmetic left shift", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of arithmetic left shift", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				{
					var r = (int)lb;
					if (r < 0 || r > 63) return Errors.ErrorOccurred($"Shift operand of {r} for arithmetic left shift was not in the range of [0-63].");
					return la << r;
				}
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsLong ? "Right" : "Left")} operand was not an integer.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive BitShiftRight(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of arithmetic right shift", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of arithmetic right shift", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				{
					var r = (int)lb;
					if (r < 0 || r > 63) return Errors.ErrorOccurred($"Shift operand of {r} for arithmetic right shift was not in the range of [0-63].");
					return la >> r;
				}
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsLong ? "Right" : "Left")} operand was not an integer.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive LogicalBitShiftRight(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of logical right shift", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of logical right shift", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				{
					var r = (int)lb;
					if (r < 0 || r > 63) return Errors.ErrorOccurred($"Shift operand of {r} for logical right shift was not in the range of [0-63].");
					return (long)((ulong)la >> r);
				}
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not an integer.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive BitwiseAnd(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of bitwise and", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of bitwise and", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la & lb;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsLong ? "Right" : "Left")} operand was not an integer.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive BitwiseOr(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of bitwise or", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of bitwise or", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la | lb;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive BitwiseXor(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of bitwise xor", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of bitwise xor", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la ^ lb;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsLong ? "Right" : "Left")} operand was not an integer.")) ? throw _err : DefaultErrorObject;
		}

		public static object BooleanAnd(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of boolean and", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of boolean and", false);
			if (left is Primitive a)
				return a.IsTrue ? right : left;
			return right;
		}

		public static object BooleanOr(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of boolean or", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of boolean or", false);
			if (left is Primitive a)
				return a.IsTrue ? left : right;
			return left;
		}

		public static Primitive Concat(object left, object right)
		{
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of concatenation", false);
			if (left == null && right is Primitive pb) return pb.ToString();
			if (left is Primitive a && right is Primitive b)
				return string.Concat(a.As(), b.As());
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive ? "Right" : "Left")} operand was not a primitive value.")) ? throw _err : DefaultErrorObject;
		}

		public static object RegEx(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of regex", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of regex", false);
			VarRef matches = new(null);
			if (left is Primitive a && right is Primitive b)
			{
				return Core.RegEx.RegExMatch(a.As(), b.As(), matches, 1);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive ? "Right" : "Left")} operand was not a primitive value.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive IdentityEquality(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of identity equality", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of identity equality", false);

			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return la == lb ? True : False;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db)) return da == db ? True : False;
				return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal) ? True : False;
			}

			return left.Equals(right);
		}

		public static Primitive IdentityInequality(object left, object right) => !IdentityEquality(left, right);

		public static Primitive ValueEquality(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of value equality", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of value equality", false);

			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return la == lb ? True : False;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db)) return da == db ? True : False;
				return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase) ? True : False;
			}
			else if (left is Array al1 && right is Array al2)
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
			else if (left is Buffer buf1 && right is Buffer buf2)
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

			return left.Equals(right) ? True : False;
		}

		public static Primitive ValueInequality(object left, object right)
		{
			return !ValueEquality(left, right);
		}

		public static Primitive LessThan(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of less than", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of less than", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la < lb;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da < db;
				return From(string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase) < 0);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive ? "Right" : "Left")} operand was not a primitive value.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive LessThanOrEqual(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of less than or equal", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of less than or equal", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la <= lb;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da <= db;
				return From(string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase) <= 0);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive ? "Right" : "Left")} operand was not a primitive value.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive GreaterThan(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of greater than", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of greater than or equal", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la > lb;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da > db;
				return From(string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase) > 0);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive ? "Right" : "Left")} operand was not a primitive value.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive GreaterThanOrEqual(object left, object right)
		{
			if (left == null) return Errors.UnsetErrorOccurred($"Left side operand of greater than or equal", false);
			if (right == null) return Errors.UnsetErrorOccurred($"Right side operand of greater than or equal", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da >= db;
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la >= lb;
				return From(string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase) >= 0);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive ? "Right" : "Left")} operand was not a primitive value.")) ? throw _err : DefaultErrorObject;
		}

		public static Primitive Is(object left, object right)
		{
			if (right == null)
				return left == null;

			string test;
			if (right is not Primitive b || (test = b.ToString()) == "")
				return false;
			test = test.ToLower();

			//Put common cases first.
			switch (test)
			{
				case "integer":
					return left is LongPrimitive;

				case "float":
					return left is DoublePrimitive;

				case "number":
					return left is Primitive lp && lp.IsNumeric;

				case "string":
					return left is StringPrimitive;

				case "unset":
				case "null":
					return left == null;
			}

			if (left == null) return Errors.UnsetErrorOccurred("Left side operand was unset", false);
			if (left is Primitive) return false;

			var alias = TypeNameAliases.FirstOrDefault(kvp => kvp.Value.Equals(test, StringComparison.OrdinalIgnoreCase)).Key;
			if (alias != null)
				test = alias;

			//Traverse class hierarchy to see if there is a match.
			var type = left.GetType();
			return Parser.IsTypeOrBase(type, test);
		}

		public static Primitive Plus(object op)
		{
			if (op == null) return Errors.UnsetErrorOccurred("Plus operand target was unset", false);
			if (op is Primitive v) { 
				if (v.TryGetLong(out long l))
					return l;
				if (v.TryGetDouble(out double d))
					return d;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"Plus operand was not numeric."))
					? throw _err
					: DefaultErrorObject;
		}
		public static Primitive Minus(LongPrimitive p)
		{
			if (p == null) return Errors.UnsetErrorOccurred("Minus operand target was unset", DefaultErrorObject);
			return -p.Value;
		}
		public static Primitive Minus(DoublePrimitive p)
		{
			
			return -p.Value;
		}
		public static Primitive Minus(StringPrimitive p)
		{
			if (p == null) return Errors.UnsetErrorOccurred("Minus operand target was unset", DefaultErrorObject);
			if (p.TryGetLong(out long ll))
				return -ll;
			else if (p.TryGetDouble(out double dd))
				return -dd;
			return Errors.ErrorOccurred(_err = new TypeError($"Minus operand was not numeric."))
				? throw _err
				: DefaultErrorObject;
		}
		public static Primitive Minus(Primitive p)
		{
			if (p == null) return Errors.UnsetErrorOccurred("Minus operand target was unset", DefaultErrorObject);
			if (p is LongPrimitive lp)
				return -lp.Value;
			else if (p is DoublePrimitive dp)
				return -dp.Value;
			else if (p is StringPrimitive sp)
			{
				if (sp.TryGetLong(out long ll))
					return -ll;
				else if (sp.TryGetDouble(out double dd))
					return -dd;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"Minus operand was not numeric."))
				? throw _err
				: DefaultErrorObject;
		}
		public static Primitive Minus(long v) => -v;
		public static Primitive Minus(double v) => -v;
		public static Primitive Minus(object op)
		{
			if (op == null) return Errors.UnsetErrorOccurred("Minus target was unset", false);
			if (op is Primitive v)
			{
				if (v.TryGetLong(out long l))
					return -l;
				if (v.TryGetDouble(out double d))
					return -d;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"Minus operand was not numeric."))
					? throw _err
					: DefaultErrorObject;
		}
		public static Primitive BitwiseNot(long v) => ~v;
		public static Primitive BitwiseNot(object op)
		{
			if (op == null)	return Errors.UnsetErrorOccurred("Bitwise-not target was unset", false);
			if (op is Primitive v)
			{
				if (v.TryGetLong(out long l))
					return ~l;
			}
			return (long)Errors.TypeErrorOccurred(op, typeof(long), 0L);
		}
		public static Primitive Not(bool v) => v ? False : True;
		public static Primitive Not(string v) => v == "" ? True : False;
		public static Primitive Not(long v) => v == 0L ? True : False;
		public static Primitive Not(StringPrimitive v)
		{
			if (v == null) return Errors.UnsetErrorOccurred("Boolean conversion target was unset", false);
			return v.Value == "" ? True : False;
		}
		public static Primitive Not(DoublePrimitive v)
		{
			if (v == null) return Errors.UnsetErrorOccurred("Boolean conversion target was unset", false);
			return v.Value == 0.0 ? True : False;
		}
		public static Primitive Not(LongPrimitive v)
		{
			if (v == null) return Errors.UnsetErrorOccurred("Boolean conversion target was unset", false);
			return v.Value == 0L ? True : False;
		}
		public static Primitive Not(Primitive v) {
			if (v == null) return Errors.UnsetErrorOccurred("Boolean conversion target was unset", false);
			return v.IsTrue? False : True;
		}
		public static Primitive Not(object op)
		{
			if (op == null) return Errors.UnsetErrorOccurred("Boolean conversion target was unset", false);
			return op is Primitive v ? !v.IsTrue : False;
		}

		#region Arithmetic operators
		public static Primitive operator +(Primitive a, Primitive b) => Add(a, b);
		public static Primitive operator -(Primitive a, Primitive b) => Subtract(a, b);
		public static Primitive operator *(Primitive a, Primitive b) => Multiply(a, b);
		public static Primitive operator /(Primitive a, Primitive b) => Divide(a, b);
		public static Primitive operator %(Primitive a, Primitive b) => Modulus(a, b);
		public static Primitive operator <<(Primitive a, Primitive b) => BitShiftLeft(a, b);
		public static Primitive operator >>(Primitive a, Primitive b) => BitShiftRight(a, b);
		public static Primitive operator >(Primitive a, Primitive b) => GreaterThan(a, b);
		public static Primitive operator >=(Primitive a, Primitive b) => GreaterThanOrEqual(a, b);
		public static Primitive operator <(Primitive a, Primitive b) => LessThan(a, b);
		public static Primitive operator <=(Primitive a, Primitive b) => LessThanOrEqual(a, b);
		public static Primitive operator ^(Primitive a, Primitive b) => BitwiseXor(a, b);
		public static Primitive operator -(Primitive v) => Minus(v);
		public static Primitive operator &(Primitive a, Primitive b) => (Primitive)BooleanAnd(a, b);
		public static Primitive operator |(Primitive a, Primitive b) => (Primitive)BooleanOr(a, b);
		public static Primitive operator +(Primitive v) => Plus(v);
		public static Primitive operator ~(Primitive v) => BitwiseNot(v);
		public static Primitive operator !(Primitive v) => Not(v);

		public static bool operator true(Primitive v) => (v == null ? Errors.UnsetErrorOccurred("Boolean conversion target was unset", false) : v).IsTrue;
		public static bool operator false(Primitive v) => (v == null ? Errors.UnsetErrorOccurred("Boolean conversion target was unset", true) : !v).IsTrue;
		#endregion

		#region IConvertible implementations
		public TypeCode GetTypeCode() => TypeCode.Object;
		public bool ToBoolean(IFormatProvider provider) => IsTrue;
		public char ToChar(IFormatProvider provider) => throw Errors.ValueError();
		public sbyte ToSByte(IFormatProvider provider) => Convert.ToSByte(ToInt64(provider));
		public byte ToByte(IFormatProvider provider) => Convert.ToByte(ToInt64(provider));
		public short ToInt16(IFormatProvider provider) => Convert.ToInt16(ToInt64(provider));
		public ushort ToUInt16(IFormatProvider provider) => Convert.ToUInt16(ToInt64(provider));
		public int ToInt32(IFormatProvider provider) => Convert.ToInt32(ToInt64(provider));
		public uint ToUInt32(IFormatProvider provider) => Convert.ToUInt32(ToInt64(provider));
		public long ToInt64(IFormatProvider provider)
		{
			if (TryGetLong(out var l)) return l;
			throw Errors.ValueError("Cannot convert to Int64");
		}
		public ulong ToUInt64(IFormatProvider provider) => Convert.ToUInt64(ToInt64(provider));
		public float ToSingle(IFormatProvider provider) => Convert.ToSingle(ToDouble(provider));
		public double ToDouble(IFormatProvider provider)
		{
			if (TryGetDouble(out var d)) return d;
			throw Errors.ValueError("Cannot convert to Double");
		}
		public decimal ToDecimal(IFormatProvider provider) => Convert.ToDecimal(ToDouble(provider));
		public DateTime ToDateTime(IFormatProvider provider) => throw Errors.ValueError();
		public abstract string ToString(IFormatProvider provider);
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
			throw Errors.ValueError($"Cannot convert Primitive to {conversionType.Name}.");
		}
		#endregion
	}

	internal static class LongCache
	{
		public const long Min = -256;
		public const long Max = 1024;

		private static readonly LongPrimitive[] Cache = Build();

		private static LongPrimitive[] Build()
		{
			var arr = new LongPrimitive[Max - Min + 1];
			for (long v = Min; v <= Max; v++)
				arr[v - Min] = new LongPrimitive(v);
			return arr;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static LongPrimitive Get(long v, object o = null)
			=> o == null && v >= Min && v <= Max ? Cache[v - Min] : new LongPrimitive(v, o);
	}

	public sealed class LongPrimitive : Primitive
	{
		public readonly long Value;
		private string _cachedString;
		public LongPrimitive(long v, object o = null)
		{
			Value = v;
			if (o != null)
				Payload = o;
		}

		public static new LongPrimitive From(object v)
		{
			if (v == null) throw Errors.UnsetError("Provided argument was unset");
			return TryCoercePrimitive(v, out LongPrimitive p) ? p : throw Errors.ValueError("Provided argument was not an integer");
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool TryCoercePrimitive(object value, out LongPrimitive p)
		{
			switch (value)
			{
				case LongPrimitive lp: p = lp; return true;
				case Primitive pp:
					if (pp.TryGetLong(out long ll))
					{
						p = ll; return true;
					} 
					else if (pp.TryGetDouble(out double d))
					{
						p = (long)d; return true;
					}
					p = null!; return false;
				case long l: p = LongCache.Get(l); return true;
				case int i: p = LongCache.Get(i); return true;
				case bool b: p = b ? True : False; return true;
				default:
					try
					{
						p = Convert.ToInt64(value); return true;
					}
					catch
					{
					}
					p = null!; return false;
			}
		}

		public static implicit operator nint(LongPrimitive v) => (nint)v.Value;
		public static implicit operator long(LongPrimitive v) => v.Value;
		public static implicit operator int(LongPrimitive v) => unchecked((int)v.Value);
		public static implicit operator bool(LongPrimitive v) => v.Value != 0L;
		public static implicit operator LongPrimitive(long v) => LongCache.Get(v);
		public static implicit operator LongPrimitive(int v) => LongCache.Get(v);
		public static implicit operator LongPrimitive(bool v) => v ? True : False;


		public override int GetHashCode() => Value.GetHashCode();
		public override bool Equals(object comp) => Value.Equals(comp is LongPrimitive p ? p.Value : comp);
		public override bool TryGetLong(out long value) { value = Value; return true; }
		public override bool TryGetDouble(out double value) { value = Value; return true; }
		public override string ToString(IFormatProvider format) => Value.ToString(format);
		public override string ToString()
		{
			if (_cachedString == null)
				_cachedString = Value.ToString(CultureInfo.InvariantCulture);
			return _cachedString;
		}
		public string ToString(string format) => Value.ToString(format);
		public override object AsObject() => Value;
		public override bool IsTrue => Value != 0L;
		public override bool IsNumeric => true;
		public override bool IsLong => true;
		public override bool IsDouble => false;
		public override bool IsString => false;
	}

	public sealed class DoublePrimitive : Primitive
	{
		public readonly double Value;
		private string _cachedString;
		public DoublePrimitive(double v) => Value = v;
		public static implicit operator double(DoublePrimitive v) => v.Value;
		public static implicit operator DoublePrimitive(double v) => new (v);
		public static explicit operator long(DoublePrimitive v) => (long)v.Value;

		public static new DoublePrimitive From(object v)
		{
			if (v == null) throw Errors.UnsetError("Provided argument was unset");
			return TryCoercePrimitive(v, out DoublePrimitive p) ? p : throw Errors.ValueError("Provided argument was not a floating-point number");
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool TryCoercePrimitive(object value, out DoublePrimitive p)
		{
			switch (value)
			{
				case DoublePrimitive dp: p = dp; return true;
				case LongPrimitive lp: p = lp.Value; return true;
				case Primitive pp:
					if (pp.TryGetLong(out long ll))
					{
						p = ll; return true;
					}
					else if (pp.TryGetDouble(out double d))
					{
						p = (long)d; return true;
					}
					p = null!; return false;
				case double d: p = d; return true;
				case long l: p = l; return true;
				case int i: p = i; return true;
				case bool b: p = b ? 1L : 0L; return true;
				default:
					try
					{
						p = Convert.ToDouble(value); return true;
					}
					catch
					{
					}
					p = null!; return false;
			}
		}

		public override int GetHashCode() => Value.GetHashCode();
		public override bool Equals(object comp) => Value.Equals(comp is DoublePrimitive p ? p.Value : comp);
		public override bool TryGetLong(out long value) { value = default; return false; }
		public override bool TryGetDouble(out double value) { value = Value; return true; }
		public override string ToString(IFormatProvider format) => Value.ToString(format);
		public override string ToString()
		{
			if (_cachedString == null)
				_cachedString = Value.ToString(CultureInfo.InvariantCulture);
			return _cachedString;
		}
		public override object AsObject() => Value;
		public override bool IsTrue => Value != 0.0;
		public override bool IsNumeric => true;
		public override bool IsLong => false;
		public override bool IsDouble => true;
		public override bool IsString => false;
	}

	public sealed class StringPrimitive : Primitive
	{
		private string _value;
		public string Value
		{
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

		public LongPrimitive Length => SpanLength;
		internal int SpanLength => AsSpan().Length;

		internal ReadOnlySpan<char> AsSpan()
		{
			if (Payload != null)
			{
				if (Payload is StringBuilder sb)
					return sb.ToString().AsSpan();
				else if (Payload is StringBuffer sbuf)
					return sbuf.AsSpan();
				else
					throw new Exception("Unknown string type");
			}
			return _value.AsSpan();
		}
		internal ReadOnlySpan<char> AsSpan(Range r) => AsSpan()[r];

		private enum CacheState : byte { Unknown = 0, Success = 1, Failure = 2 }

		//––– long parse cache/state –––
		private long _cachedLong;
		private CacheState _longState = CacheState.Unknown;

		//––– double parse cache/state –––
		private double _cachedDouble;
		private CacheState _doubleState = CacheState.Unknown;

		public StringPrimitive(params object[] args)
		{
			if (args != null && args.Length > 0)
			{
				object val = args[^1];
				if (val is Primitive sp)
					_value = sp.ToString();
				else if (val is string s)
					_value = s;
				else
					throw new TypeError();
			} else
				throw new ArgumentError();
		}
		public StringPrimitive(string v) => _value = v ?? throw new ArgumentNullException(nameof(v));
		public StringPrimitive(char v) => _value = v.ToString();
		public StringPrimitive(StringBuilder v) => Payload = v ?? throw new ArgumentNullException(nameof(v));
		public StringPrimitive(StringBuffer v) => Payload = v ?? throw new ArgumentNullException(nameof(v));

		public static implicit operator StringPrimitive(string v) => v == null ? null : From(v);
		public static implicit operator StringPrimitive(char v) => new StringPrimitive(v);
		public static implicit operator string(StringPrimitive v) => v == null ? null : v.ToString();

		public static StringPrimitive operator +(string a, StringPrimitive b) => string.Concat(a, b);
		public static StringPrimitive operator +(StringPrimitive a, string b) => string.Concat(a, b);

		public override int GetHashCode() => Value.GetHashCode();
		public override bool Equals(object comp) => Value.Equals(comp is StringPrimitive p ? p.Value : comp);
		public override bool TryGetLong(out long value)
		{
			if (_longState == CacheState.Unknown)
			{
				if (TryParseLong(AsSpan(), out var l))
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
				if (double.TryParse(AsSpan(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
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

			if (TryGetLong(out long ll))
			{
				value = (double)ll;
				return true;
			}

			value = default;
			return false;
		}
		public override string ToString(IFormatProvider format) => Value.ToString(format);
		public override string ToString() => Value;
		public override object AsObject() => Payload ?? Value;
		public override bool IsTrue => SpanLength != 0 && (TryGetDouble(out double dd) ? dd != 0.0 : true);
		public override bool IsNumeric => TryGetLong(out _) || TryGetDouble(out _);
		public override bool IsLong => TryGetLong(out _);
		public override bool IsDouble => TryGetDouble(out _) && !TryGetLong(out _);
		public override bool IsString => true;
		public bool IsEmpty => SpanLength == 0;

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
				|| (s[1] | 0x20) == 'b' && long.TryParse(s.Slice(2), NumberStyles.AllowBinarySpecifier, CultureInfo.InvariantCulture, out ll)))
			{
				if (neg)
					ll = -ll;
				return true;
			}

			return false;
		}
	}

	/// <summary>Extension methods for Primitive conversions.</summary>
	public static class PrimitiveExtensions
	{
		public static long Al(this Primitive v)
		{
			if (v == null) Errors.UnsetError("Long conversion target is unset");
			if (v.TryGetLong(out var l))
				return l;
			if (v.TryGetDouble(out var d))
				return (long)d;
			throw Errors.ValueError($"Cannot convert '{v}' to long.");
		}
		public static long Al(this Primitive v, long def)
			=> v == null ? def : v.TryGetLong(out var l) ? l : v.TryGetDouble(out var d) ? (long)d : def;

		public static string As(this Primitive v)
		{
			if (v == null) Errors.UnsetError("String conversion target is unset");
			return v.ToString();
		}
		public static string As(this Primitive v, string def)
			=> v == null ? def : v.ToString();

		public static Primitive AsPrimitive(this object v)
		{
			if (v == null) return null;
			if (v is Primitive val) return val;
			if (v is IConvertible)
				return (Primitive)TypeDescriptor.GetConverter(typeof(Primitive)).ConvertFrom(v);
			return Primitive.From(v);
		}
	}

	/// <summary>TypeConverter for Primitive to/from primitives.</summary>
	public class PrimitiveTypeConverter : TypeConverter
	{
		static readonly HashSet<Type> Supported = new()
		{
			typeof(Primitive), typeof(bool), typeof(char), typeof(sbyte), typeof(byte),
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
			if (value is Primitive v) return v;
			if (value is IConvertible ic)
			{
				switch (ic.GetTypeCode())
				{
					case TypeCode.Boolean: return (Primitive)ic.ToBoolean(culture);
					case TypeCode.Char: return (Primitive)ic.ToChar(culture).ToString();
					case TypeCode.SByte: return (Primitive)ic.ToSByte(culture);
					case TypeCode.Byte: return (Primitive)ic.ToByte(culture);
					case TypeCode.Int16: return (Primitive)ic.ToInt16(culture);
					case TypeCode.UInt16: return (Primitive)ic.ToUInt16(culture);
					case TypeCode.Int32: return (Primitive)ic.ToInt32(culture);
					case TypeCode.UInt32: return (Primitive)ic.ToUInt32(culture);
					case TypeCode.Int64: return (Primitive)ic.ToInt64(culture);
					case TypeCode.UInt64: return (Primitive)(long)ic.ToUInt64(culture);
					case TypeCode.Single: return (Primitive)(double)ic.ToSingle(culture);
					case TypeCode.Double: return (Primitive)ic.ToDouble(culture);
					case TypeCode.Decimal: return (Primitive)(double)ic.ToDecimal(culture);
					case TypeCode.String: return (Primitive)ic.ToString(culture);
					case TypeCode.DateTime: return (Primitive)ic.ToDateTime(culture).ToString();
				}
			}
			return base.ConvertFrom(ctx, culture, value);
		}

		public override object ConvertTo(ITypeDescriptorContext ctx, CultureInfo culture, object value, Type dest)
		{
			if (!(value is Primitive v)) return base.ConvertTo(ctx, culture, value, dest);
			if (v == null) return null;
			if (dest == typeof(Primitive)) return v;
			if (typeof(IConvertible).IsAssignableFrom(dest))
				return ((IConvertible)v).ToType(dest, culture);
			return base.ConvertTo(ctx, culture, value, dest);
		}
	}
}