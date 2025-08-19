namespace Keysharp.Scripting
{
	/// <summary>
	/// Base abstract class for primitive script values (long, double, string), with per-instance lazy caches.
	/// </summary>
	[TypeConverter(typeof(PrimitiveTypeConverter))]
	public abstract class Primitive : IConvertible
	{
		/// <summary>Used as a secondary container for any accompanying objects such
		/// as an IPointable for LongPrimitive or StringBuffer for StringPrimitive.</summary>
		public object Payload;

		public static Primitive True = LongCache.Get(1);
		public static Primitive False = LongCache.Get(0);

		/// <summary>Factory methods for each subtype.</summary>
		public static Primitive From(long v) => LongCache.Get(v);
		public static Primitive From(double v) => new DoublePrimitive(v);
		public static Primitive From(string v) => new StringPrimitive(v);
		public static Primitive From(bool v) => v ? True : False;
		public static Primitive From(object v) {
			if (v == null)
				throw new ArgumentNullException(nameof(v));
			else if (v is long l)
				return LongCache.Get(l);
			else if (v is bool b)
				return b ? True : False;
			else if (v is double d)
				return new DoublePrimitive(d);
			else if (v is string s)
				return new StringPrimitive(s);
			throw new Exception();
		}

		/// <summary>Implicit conversions</summary>
		public static implicit operator Primitive(long v) => From(v);
		public static implicit operator Primitive(double v) => From(v);
		public static implicit operator Primitive(string v) => From(v);
		public static implicit operator Primitive(bool v) => From(v);

		#region Explicit conversions
		public static implicit operator double(Primitive v)
		{
			if (v == null) throw Errors.UnsetError("Double conversion target is unset");
			if (v.TryGetDouble(out var d)) return d;
			throw new InvalidCastException("KsValue is not numeric.");
		}
		public static implicit operator long(Primitive v)
		{
			if (v == null) throw Errors.UnsetError("Long conversion target is unset");
			if (v.TryGetLong(out var l)) return l;
			if (v.TryGetDouble(out var d)) return (long)d;
			throw new InvalidCastException("KsValue is not numeric.");
		}
		public static implicit operator int(Primitive v)
		{
			if (v == null) throw Errors.UnsetError("Int conversion target is unset");
			if (v.TryGetLong(out var l)) return (int)l;
			if (v.TryGetDouble(out var d)) return (int)d;
			throw new InvalidCastException("KsValue is not numeric.");
		}
		public static implicit operator string(Primitive v)
		{
			if (v == null) throw Errors.UnsetError("String conversion target is unset");
			return v.AsString();
		}
		#endregion

		/// <summary>Try to get a long. Succeeds only if the value is a pure long
		/// or a string in pure long format.</summary>
		public abstract bool TryGetLong(out long value);
		/// <summary>Try to get a double. Returns false if conversion is not possible,
		/// meaning when it's not a pure long, double, or a string in long or double format.</summary>
		public abstract bool TryGetDouble(out double value);
		/// <summary>Try to get a string. Returns false if conversion is not possible.</summary>
		public abstract string AsString();
		/// <summary>Always returns the underlying object or boxed primitive.</summary>
		public abstract object AsObject();
		/// <summary>Always returns whether the value evaluates to true.</summary>
		public abstract bool AsBool();
		/// <summary>Returns whether the value is a long, double, or numeric string.</summary>
		public abstract bool IsNumeric { get; }
		public abstract bool IsLong { get; }
		public abstract bool IsDouble { get; }
		public abstract bool IsString { get; }

		// Used as a temporary variable to throw errors.
		static Error _err;

		public static Primitive Add(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of addition", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of addition", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return LongCache.Get(la + lb, a.Payload ?? b.Payload);
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da + db;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive Subtract(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of substraction", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of substraction", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return LongCache.Get(la - lb, a.Payload ?? b.Payload);
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da - db;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive Multiply(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of multiplication", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of multiplication", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la * lb;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da * db;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive Divide(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of divison", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of divison", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				{
					if (lb == 0L) return (Primitive)Errors.ZeroDivisionErrorOccurred("Right side operand of integer division");
					if (la % lb == 0) return la / lb;
					return (double)la / lb;
				}
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
				{
					if (db == 0.0) return (Primitive)Errors.ZeroDivisionErrorOccurred("Right side operand of floating point division");
					return da / db;
				}
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive FloorDivide(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of floor division", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of floor division", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				{
					if (lb == 0L) return (Primitive)Errors.ZeroDivisionErrorOccurred("Right side operand of floor divide");
					return la / lb;
				}
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive Modulus(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of modulus", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of modulus", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la % lb;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da % db;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive Power(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of power", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of power", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return Math.Pow(da, db);
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return (long)Math.Pow(la, lb);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive BitShiftLeft(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of arithmetic left shift", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of arithmetic left shift", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				{
					var r = (int)lb;
					if (r < 0 || r > 63) return (Primitive)Errors.ErrorOccurred($"Shift operand of {r} for arithmetic left shift was not in the range of [0-63].");
					return la << r;
				}
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsLong ? "Right" : "Left")} operand was not an integer.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive BitShiftRight(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of arithmetic right shift", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of arithmetic right shift", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				{
					var r = (int)lb;
					if (r < 0 || r > 63) return (Primitive)Errors.ErrorOccurred($"Shift operand of {r} for arithmetic right shift was not in the range of [0-63].");
					return la >> r;
				}
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsLong ? "Right" : "Left")} operand was not an integer.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive LogicalBitShiftRight(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of logical right shift", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of logical right shift", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
				{
					var r = (int)lb;
					if (r < 0 || r > 63) return (Primitive)Errors.ErrorOccurred($"Shift operand of {r} for logical right shift was not in the range of [0-63].");
					return (long)((ulong)la >> r);
				}
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not an integer.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive BitwiseAnd(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of bitwise and", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of bitwise and", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la & lb;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsLong ? "Right" : "Left")} operand was not an integer.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive BitwiseOr(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of bitwise or", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of bitwise or", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la | lb;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsNumeric ? "Right" : "Left")} operand was not numeric.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive BitwiseXor(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of bitwise xor", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of bitwise xor", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la ^ lb;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive lp && lp.IsLong ? "Right" : "Left")} operand was not an integer.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static object BooleanAnd(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of boolean and", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of boolean and", false);
			if (left is Primitive a)
				return a.AsBool() ? right : left;
			return right;
		}

		public static object BooleanOr(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of boolean or", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of boolean or", false);
			if (left is Primitive a)
				return a.AsBool() ? left : right;
			return left;
		}

		public static Primitive Concat(object left, object right)
		{
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of concatenation", false);
			if (left is Primitive a && right is Primitive b)
				return string.Concat(a.As(), b.As());
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive ? "Right" : "Left")} operand was not a primitive value.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static object RegEx(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of regex", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of regex", false);
			VarRef matches = new(null);
			if (left is Primitive a && right is Primitive b)
			{
				_ = Core.RegEx.RegExMatch(a.As(), b.As(), matches, 1);
				return matches;
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive ? "Right" : "Left")} operand was not a primitive value.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive IdentityEquality(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of identity equality", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of identity equality", false);

			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return la == lb ? True : False;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db)) return da == db ? True : False;
				return string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal) ? True : False;
			}

			return left.Equals(right);
		}

		public static Primitive IdentityInequality(object left, object right) => !IdentityEquality(left, right);

		public static Primitive ValueEquality(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of value equality", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of value equality", false);

			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb)) return la == lb ? True : False;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db)) return da == db ? True : False;
				return string.Equals(a.AsString(), b.AsString(), StringComparison.OrdinalIgnoreCase) ? True : False;
			}
			else if (left is Core.Array al1 && right is Core.Array al2)
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
			else if (left is Core.Buffer buf1 && right is Core.Buffer buf2)
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
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of less than", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of less than", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la < lb;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da < db;
				return From(string.Compare(a.AsString(), b.AsString(), StringComparison.OrdinalIgnoreCase) < 0);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive ? "Right" : "Left")} operand was not a primitive value.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive LessThanOrEqual(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of less than or equal", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of less than or equal", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la <= lb;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da <= db;
				return From(string.Compare(a.AsString(), b.AsString(), StringComparison.OrdinalIgnoreCase) <= 0);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive ? "Right" : "Left")} operand was not a primitive value.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive GreaterThan(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of greater than", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of greater than or equal", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la > lb;
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da > db;
				return From(string.Compare(a.AsString(), b.AsString(), StringComparison.OrdinalIgnoreCase) > 0);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive ? "Right" : "Left")} operand was not a primitive value.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive GreaterThanOrEqual(object left, object right)
		{
			if (left == null) return (bool)Errors.UnsetErrorOccurred($"Left side operand of greater than or equal", false);
			if (right == null) return (bool)Errors.UnsetErrorOccurred($"Right side operand of greater than or equal", false);
			if (left is Primitive a && right is Primitive b)
			{
				if (a.TryGetDouble(out var da) && b.TryGetDouble(out var db))
					return da >= db;
				if (a.TryGetLong(out var la) && b.TryGetLong(out var lb))
					return la >= lb;
				return From(string.Compare(a.AsString(), b.AsString(), StringComparison.OrdinalIgnoreCase) >= 0);
			}
			return Errors.ErrorOccurred(_err = new TypeError($"{(left is Primitive ? "Right" : "Left")} operand was not a primitive value.")) ? throw _err : (Primitive)DefaultErrorObject;
		}

		public static Primitive Is(object left, object right)
		{
			if (right == null)
				return left == null;

			string test;
			if (right is not Primitive b || (test = b.AsString()) == "")
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

			if (left == null) return (bool)Errors.UnsetErrorOccurred("Left side operand was unset", false);
			if (left is Primitive) return false;

			var alias = Keywords.TypeNameAliases.FirstOrDefault(kvp => kvp.Value.Equals(test, StringComparison.OrdinalIgnoreCase)).Key;
			if (alias != null)
				test = alias;

			//Traverse class hierarchy to see if there is a match.
			var type = left.GetType();
			return Parser.IsTypeOrBase(type, test);
		}

		public static Primitive Plus(object op)
		{
			if (op == null) return (bool)Errors.UnsetErrorOccurred("Plus operand target was unset", false);
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
		public static Primitive Minus(long v) => -v;
		public static Primitive Minus(double v) => -v;
		public static Primitive Minus(object op)
		{
			if (op == null) return (bool)Errors.UnsetErrorOccurred("Minus target was unset", false);
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
			if (op == null)	return (bool)Errors.UnsetErrorOccurred("Bitwise-not target was unset", false);
			if (op is Primitive v)
			{
				if (v.TryGetLong(out long l))
					return ~l;
			}
			return (long)Errors.TypeErrorOccurred(op, typeof(long), 0L);
		}
		public static Primitive Not(string v) => v == "" ? True : False;
		public static Primitive Not(long v) => v == 0L ? True : False;
		public static Primitive Not(Primitive v) => !v.AsBool();
		public static Primitive Not(object op) => op is Primitive v ? !v.AsBool() : False;

		#region Arithmetic operators
		public static Primitive operator +(Primitive a, Primitive b) => Add(a, b);
		public static Primitive operator -(Primitive a, Primitive b) => Subtract(a, b);
		public static Primitive operator *(Primitive a, Primitive b) => Multiply(a, b);
		public static Primitive operator /(Primitive a, Primitive b) => Divide(a, b);
		public static Primitive operator %(Primitive a, Primitive b) => Modulus(a, b);
		public static Primitive operator <<(Primitive a, Primitive b) => BitShiftLeft(a, b);
		public static Primitive operator >>(Primitive a, Primitive b) => BitShiftRight(a, b);
		public static Primitive operator ^(Primitive a, Primitive b) => BitwiseXor(a, b);
		public static Primitive operator -(Primitive v) => Minus(v);
		public static object operator &(Primitive a, Primitive b) => BooleanAnd(a, b);
		public static object operator |(Primitive a, Primitive b) => BooleanOr(a, b);
		public static Primitive operator +(Primitive v) => Plus(v);
		public static Primitive operator ~(Primitive v) => BitwiseNot(v);
		public static Primitive operator !(Primitive v) => Not(v);

		public static bool operator true(Primitive v) => v == null ? (bool)Errors.UnsetErrorOccurred("Boolean conversion target was unset", false) : v.AsBool();
		public static bool operator false(Primitive v) => v == null ? (bool)Errors.UnsetErrorOccurred("Boolean conversion target was unset", true) : !v.AsBool();
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
		public string ToString(IFormatProvider provider) => AsString();
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
			throw new InvalidCastException($"Cannot convert Primitive to {conversionType.Name}.");
		}
		#endregion
	}

	static class LongCache
	{
		public const long Min = -1;
		public const long Max = 256;

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
			=> (o == null && v >= Min && v <= Max) ? Cache[v - Min] : new LongPrimitive(v, o);
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
		public override bool TryGetLong(out long value) { value = Value; return true; }
		public override bool TryGetDouble(out double value) { value = Value; return true; }
		public override string AsString()
		{
			if (_cachedString == null)
				_cachedString = Value.ToString(CultureInfo.InvariantCulture);
			return _cachedString;
		}
		public override object AsObject() => Value;
		public override bool AsBool() => Value != 0L;
		public override bool IsNumeric => true;
		public override bool IsLong => true;
		public override bool IsDouble => false;
		public override bool IsString => false;
	}

	sealed class DoublePrimitive : Primitive
	{
		public readonly double Value;
		private string _cachedString;
		public DoublePrimitive(double v) => Value = v;
		public override bool TryGetLong(out long value) { value = default; return false; }
		public override bool TryGetDouble(out double value) { value = Value; return true; }
		public override string AsString()
		{
			if (_cachedString == null)
				_cachedString = Value.ToString(CultureInfo.InvariantCulture);
			return _cachedString;
		}
		public override object AsObject() => Value;
		public override bool AsBool() => Value != 0.0;
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

		public ReadOnlySpan<char> AsSpan()
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

		private enum CacheState : byte { Unknown = 0, Success = 1, Failure = 2 }

		//––– long parse cache/state –––
		private long _cachedLong;
		private CacheState _longState = CacheState.Unknown;

		//––– double parse cache/state –––
		private double _cachedDouble;
		private CacheState _doubleState = CacheState.Unknown;

		public StringPrimitive(string v) => _value = v ?? throw new ArgumentNullException(nameof(v));
		public StringPrimitive(StringBuilder v) => Payload = v ?? throw new ArgumentNullException(nameof(v));
		public StringPrimitive(StringBuffer v) => Payload = v ?? throw new ArgumentNullException(nameof(v));

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

			value = default;
			return false;
		}
		public override string AsString() => Value;
		public override object AsObject() => Payload ?? Value;
		public override bool AsBool() => AsSpan().Length != 0;
		public override bool IsNumeric => TryGetLong(out _) || TryGetDouble(out _);
		public override bool IsLong => false;
		public override bool IsDouble => false;
		public override bool IsString => true;

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

	/// <summary>Extension methods for Primitive conversions.</summary>
	public static class PrimitiveExtensions
	{
		public static bool Ab(this Primitive v)
		{
			if (v == null) return false;
			return v.AsBool();
		}
		public static bool Ab(this Primitive v, bool def)
			=> v == null ? def : v.Ab();

		public static long Al(this Primitive v)
		{
			if (v == null) throw new InvalidOperationException("Long conversion target is unset");
			if (v.TryGetLong(out var l))
				return l;
			if (v.TryGetDouble(out var d))
				return (long)d;
			throw new InvalidCastException($"Cannot convert '{v}' to long.");
		}
		public static long Al(this Primitive v, long def)
			=> v == null ? def : (v.TryGetLong(out var l) ? l : v.TryGetDouble(out var d) ? (long)d : def);

		public static double Ad(this Primitive v)
		{
			if (v == null) throw new InvalidOperationException("Double conversion target is unset");
			return v.TryGetDouble(out var d) ? d : throw new InvalidCastException();
		}
		public static double Ad(this Primitive v, double def)
			=> v == null ? def : v.TryGetDouble(out var d) ? d : def;

		public static string As(this Primitive v)
		{
			if (v == null) throw new InvalidOperationException("String conversion target is unset");
			return v.AsString();
		}
		public static string As(this Primitive v, string def)
			=> v == null ? def : v.AsString();

		public static Any Aa(this Primitive v)
			=> (Any)v?.AsObject();

		public static object Ao(this Primitive v)
			=> v == null ? null : v.AsObject();
		public static object Ao(this Primitive v, object def)
			=> v == null ? def : v.AsObject();

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
					case TypeCode.Boolean: return Primitive.From(ic.ToBoolean(culture));
					case TypeCode.Char: return Primitive.From(ic.ToChar(culture).ToString());
					case TypeCode.SByte: return Primitive.From((long)ic.ToSByte(culture));
					case TypeCode.Byte: return Primitive.From((long)ic.ToByte(culture));
					case TypeCode.Int16: return Primitive.From((long)ic.ToInt16(culture));
					case TypeCode.UInt16: return Primitive.From((long)ic.ToUInt16(culture));
					case TypeCode.Int32: return Primitive.From((long)ic.ToInt32(culture));
					case TypeCode.UInt32: return Primitive.From((long)ic.ToUInt32(culture));
					case TypeCode.Int64: return Primitive.From(ic.ToInt64(culture));
					case TypeCode.UInt64: return Primitive.From((long)ic.ToUInt64(culture));
					case TypeCode.Single: return Primitive.From((double)ic.ToSingle(culture));
					case TypeCode.Double: return Primitive.From(ic.ToDouble(culture));
					case TypeCode.Decimal: return Primitive.From((double)ic.ToDecimal(culture));
					case TypeCode.String: return Primitive.From(ic.ToString(culture));
					case TypeCode.DateTime: return Primitive.From(ic.ToDateTime(culture).ToString());
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