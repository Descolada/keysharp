namespace System
{
	/// <summary>
	/// Extension methods for the System.Object class.
	/// </summary>
	public static class ObjectExtensions
	{
		/// <summary>
		/// Converts an object to a bool.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null or the conversion fails.</param>
		/// <returns>The object as a bool if conversion succeeded, else def.</returns>
		public static bool Ab(this object obj)
		{
			if (obj == null) return Errors.UnsetErrorOccurred("Bool conversion target is unset", false);
			return obj.ParseBool(out bool b) ? b : Errors.ValueErrorOccurred($"Cannot convert '{obj}' to boolean.", false);
		}
		public static bool Ab(this object obj, bool def) => obj != null && obj.ParseBool(out bool b) ? b : def;

		/// <summary>
		/// Converts an object to a double.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The object as a double if it was not null, else def.</returns>
		public static double Ad(this object obj)
		{
			if (obj == null) return Errors.UnsetErrorOccurred("Double conversion target is unset", 0.0D);
			return obj.ParseDouble() ?? Errors.ValueErrorOccurred($"Cannot convert '{obj}' to double.", 0.0);
		}
		public static double Ad(this object obj, double def) => obj?.ParseDouble() ?? def;

		/// <summary>
		/// Converts an object to a float.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The object as a float if it was not null, else def.</returns>
		public static float Af(this object obj)
		{
			if (obj == null) return Errors.UnsetErrorOccurred("Float conversion target is unset", 0.0);
			return obj.ParseFloat() ?? Errors.ValueErrorOccurred($"Cannot convert '{obj}' to float.", 0.0);
		}
		public static float Af(this object obj, float def) => obj?.ParseFloat() ?? def;

		/// <summary>
		/// Converts an object to an int.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The object as an int if it was not null, else def.</returns>
		public static int Ai(this object obj)
		{
			if (obj == null) return (int)Errors.UnsetErrorOccurred("Int conversion target is unset", 0);
			return obj.ParseInt() ?? (int)Errors.ValueErrorOccurred($"Cannot convert '{obj}' to int.", 0);
		}
		public static int Ai(this object obj, int def) => obj?.ParseInt() ?? def;

		/// <summary>
		/// Converts an object to a long.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The object as a long if it was not null, else def.</returns>
		public static long Al(this object obj)
		{
			if (obj == null) return (long)Errors.UnsetErrorOccurred("Long conversion target is unset", 0);
			return obj.ParseLong(out long l) ? l : Errors.ValueErrorOccurred($"Cannot convert '{obj}' to long.", 0);
		}
		public static long Al(this object obj, long def) => obj != null && obj.ParseLong(out long l) ? l : def;

		/// <summary>
		/// Converts an object to a string.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The object as a string if it was not null, else def.</returns>
		public static string As(this object obj, string def = "") => obj?.ToString() ?? def;

		/// <summary>
		/// Converts an object to an unsigned int.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The object as an unsigned int if it was not null, else def.</returns>
		public static uint Aui(this object obj)
		{
			if (obj == null) return (uint)Errors.UnsetErrorOccurred("UInt conversion target is unset", 0);
			return obj.ParseUInt() ?? (uint)Errors.ValueErrorOccurred($"Cannot convert '{obj}' to uint.", 0);
		}
		public static uint Aui(this object obj, uint def) => obj?.ParseUInt() ?? def;

		/// <summary>
		/// Converts an object to a Primitive.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The object as a Primitive if it was not null, else def.</returns>
		public static Primitive Ap(this object obj, Primitive def = default) => Primitive.TryCoercePrimitive(obj, out Primitive p) ? p : def;

		/// <summary>
		/// Extracts the inner value of a Primitive if necessary, otherwise returns the object.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The inner object of the Primitive or the object.</returns>
		public static object Ao(this object obj, object def = default) => obj is Primitive p ? p.AsObject() : obj;

		/// <summary>
		/// Wrapper around casting an object to a type <typeparamref name="T"/>.
		/// </summary>
		/// <typeparam name="T">The type to cast the object to.</typeparam>
		/// <param name="obj">The object to cast.</param>
		/// <returns>The object casted to <typeparamref name="T"/>.</returns>
		public static T CastTo<T>(this object obj) => (T)obj;

		/// <summary>
		/// Casts an object to a type using reflection.
		/// </summary>
		/// <param name="obj">The object to cast.</param>
		/// <param name="type">The type to cast obj to.</param>
		/// <returns>obj casted to type.</returns>
		public static object CastToReflected(this object obj, Type type)
		{
			var methodInfo = typeof(ObjectExtensions).GetMethod(nameof(CastTo), BindingFlags.Static | BindingFlags.Public);
			var genericArguments = new[] { type };
			var genericMethodInfo = methodInfo?.MakeGenericMethod(genericArguments);
			return genericMethodInfo?.Invoke(null, [obj]);
		}

		/// <summary>
		/// Attempts to convert an object to a <see cref="Control"/>.
		/// </summary>
		/// <param name="obj">The object to examine.</param>
		/// <returns>A <see cref="Control"/> if the conversion succeeded, else null.</returns>
		public static Control GetControl(this object obj)
		{
			if (obj is Gui gui)
				return gui.form;
			else if (obj is Gui.Control ctrl)
				return ctrl.Ctrl;
			else if (obj is Keysharp.Core.Menu menu)
				return menu.GetMenu();
			else if (obj is Control control)//Final check in the event it's some kind of native control or form.
				return control;

			return null;
		}

		/// <summary>
		/// Returns whether a callback result non-empty.
		/// </summary>
		/// <param name="result">The callback result to examine.</param>
		/// <returns>True if non-empty, else false.</returns>
		public static bool IsCallbackResultNonEmpty(this object result) => result != null && ((result.ParseLong(false) is long l && l != 0) || (result.ParseDouble(false) is double dl && dl != 0.0) || result.ParseBool().IsTrue() || (result is string s && s != ""));

		/// <summary>
		/// Returns whether an object is a <see cref="Gui"/>, <see cref="GuiControl"/> or <see cref="Menu"/>.
		/// </summary>
		/// <param name="obj">The object to examine.</param>
		/// <returns>True if obj was a <see cref="Gui"/>, <see cref="GuiControl"/> or <see cref="Menu"/>, else false.</returns>
		public static bool IsKeysharpGui(this object obj) => obj is Gui || obj is Gui.Control || obj is Keysharp.Core.Menu;

		/// <summary>
		/// Returns whether an object is a string that is not empty.
		/// </summary>
		/// <param name="obj">The obj to examine.</param>
		/// <returns>True if obj was a string that was not empty, else false.</returns>
		public static bool IsNotNullOrEmpty(this object obj) => obj != null&& !(obj is string s&& s?.Length == 0);

		/// <summary>
		/// Returns whether an object is null or an empty string.
		/// </summary>
		/// <param name="obj">The obj to examine.</param>
		/// <returns>True if obj was null or an empty string, else false.</returns>
		public static bool IsNullOrEmpty(this object obj) => obj == null ? true : obj is string s ? s?.Length == 0 : false;

		/// <summary>
		/// Attempt to convert an object to a bool.
		/// This treats 0, false, and optionally "off" and "false" string literals as false.
		/// and 1, true and optionally "on" and "true" string literals as true.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <returns>The nullable bool resulting from the conversion.</returns>
		public static bool? ParseBool(this object obj, bool parseBoolKeywords = false) => obj.ParseBool(out bool b) ? b : null;

		public static bool ParseBool(this object obj, out bool outvar, bool parseBoolKeywords = false)
		{
			if (obj is Primitive p)
			{
				outvar = p.IsTrue;
				return true;
			}
			else if (obj is BoolResult br)
			{
				return br.o.ParseBool(out outvar);
			}
			else if (obj is bool b)
			{
				outvar = b;
				return true;
			}
			else if (obj is long l)
			{
				outvar = l != 0L;
				return true;
			}
			else if (obj is double dl)
			{

				outvar = dl != 0.0;
				return true;
			}
			else if (obj is string s)
			{
				outvar = s != "";
				return true;
			}
			else if (obj != null && !parseBoolKeywords)
			{
				outvar = true;
				return true;
			}

			if (parseBoolKeywords) {
				var v = Options.OnOff(Primitive.From(obj));
				if (v.HasValue)
				{
					outvar = v.Value;
					return true;
				}
			}
			outvar = false;
			return false;
		}

		/// <summary>
		/// Attempts various methods for converting an object to a decimal value.<br/>
		/// This will first attempt direct casting because it's the most efficient and the most likely scenario.<br/>
		/// String parsing will be attempted after that, then using the <see cref="Convert"/> class as a final attempt.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="doconvert">Whether to attempt using the <see cref="Convert"/> class if all other attempts fail.</param>
		/// <param name="requiredot">Whether to require a . character in the string when parsing after other attempts have failed.</param>
		/// <returns>The converted value as a nullable decimal.</returns>
		public static decimal? ParseDecimal(this object obj, bool doconvert = true, bool requiredot = false)
		{
			if (obj is decimal m)
				return m;

			if (obj is long l)
				return l;

			if (obj is BoolResult br)
				return br.o.ParseDecimal(doconvert, requiredot);

			if (obj is int i)//int is seldom used in Keysharp, so check last.
				return i;

			var s = obj.ToString().AsSpan().Trim();

			if (s.Length == 0)
				return new decimal? ();

			if (requiredot && !s.Contains('.'))
				return new decimal? ();

			if (decimal.TryParse(s, out m))
				return m;

			if (!char.IsNumber(s[s.Length - 1]))//Handle a string specifying a double like "123.0D".
				if (decimal.TryParse(s.Slice(0, s.Length - 1), out m))
					return m;

			try
			{
				return doconvert ? Convert.ToDecimal(obj) : new decimal? ();
			}
			catch
			{
				_ = Errors.TypeErrorOccurred(obj, typeof(decimal));
				return default;
			}
		}

		/// <summary>
		/// Attempts various methods for converting an object to a double value.<br/>
		/// This will first attempt direct casting because it's the most efficient and the most likely scenario.<br/>
		/// String parsing will be attempted after that, then using the <see cref="Convert"/> class as a final attempt.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="doconvert">Whether to attempt using the <see cref="Convert"/> class if all other attempts fail.</param>
		/// <param name="requiredot">Whether to require a . character in the string when parsing after other attempts have failed.</param>
		/// <returns>The converted value as a nullable double.</returns>
		public static double? ParseDouble(this object obj, bool doconvert = true, bool requiredot = false)
		{
			if (obj is Primitive p)
			{
				if (p.TryGetDouble(out double dl))
					return dl;
				if (doconvert && p.IsTrue)
					_ = Errors.TypeErrorOccurred(obj, typeof(double));
				return default;
			}

			if (obj.ParseDouble(out double d, doconvert, requiredot))
				return d;
			else
				return null;
		}

		public static bool ParseDouble(this object obj, out double outvar, bool doconvert = true, bool requiredot = false)
		{
			if (obj is Primitive p)
			{
				if (p.TryGetDouble(out double dl))
				{
					outvar = dl;
					return true;
				}
				if (doconvert && p.IsTrue)
					_ = Errors.TypeErrorOccurred(obj, typeof(double));
				outvar = default;
				return false;
			}

			if (obj is double d)
			{
				outvar = d;
				return true;
			}

			if (obj is long l)
			{
				outvar = l;
				return true;
			}

			if (obj is BoolResult br)
			{
				return br.o.ParseDouble(out outvar, doconvert, requiredot);
			}

			if (obj is int i)//int is seldom used in Keysharp, so check last.
			{
				outvar = i;
				return true;
			}

			var s = obj.ToString().AsSpan().Trim();

			if (s.Length == 0)
			{
				outvar = 0.0;
				return false;
			}

			if (requiredot && !s.Contains('.'))
			{
				outvar = 0.0;
				return false;
			}

			if (double.TryParse(s, out outvar))
				return true;

			if (!char.IsNumber(s[s.Length - 1]))//Handle a string specifying a double like "123.0D".
				if (double.TryParse(s.Slice(0, s.Length - 1), out outvar))
					return true;

			try
			{
				if (doconvert)
				{
					outvar = Convert.ToDouble(obj);
					return true;
				}
			}
			catch
			{
				return Errors.TypeErrorOccurred(obj, typeof(double), false);
			}

			return false;
		}

		/// <summary>
		/// Attempts various methods for converting an object to a float value.<br/>
		/// This will first attempt direct casting because it's the most efficient and the most likely scenario.<br/>
		/// String parsing will be attempted after that, then using the <see cref="Convert"/> class as a final attempt.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="doconvert">Whether to attempt using the <see cref="Convert"/> class if all other attempts fail.</param>
		/// <param name="requiredot">Whether to require a . character in the string when parsing after other attempts have failed.</param>
		/// <returns>The converted value as a nullable float.</returns>
		public static float? ParseFloat(this object obj, bool doconvert = true, bool requiredot = false)
		{
			if (obj is Primitive p)
			{
				if (p.TryGetDouble(out double dl))
					return unchecked((float)dl);
				if (doconvert && p.IsTrue)
					_ = Errors.TypeErrorOccurred(obj, typeof(float));
				return default;
			}

			if (obj is float d)
				return d;

			if (obj is double dd)//Check for double here, but not the reverse in ParseDouble() because most decimal numbers will be double.
				return (float)dd;

			if (obj is long l)
				return l;

			if (obj is BoolResult br)
				return br.o.ParseFloat(doconvert, requiredot);

			if (obj is int i)//int is seldom used in Keysharp, so check last.
				return i;

			var s = obj.ToString().AsSpan().Trim();

			if (s.Length == 0)
				return new float? ();

			if (requiredot && !s.Contains('.'))
				return new float? ();

			if (float.TryParse(s, out d))
				return d;

			if (!char.IsNumber(s[s.Length - 1]))//Handle a string specifying a double like "123.0D".
				if (float.TryParse(s.Slice(0, s.Length - 1), out d))
					return d;

			try
			{
				return doconvert ? (float)Convert.ToDouble(obj) : new float? ();
			}
			catch
			{
				_ = Errors.TypeErrorOccurred(obj, typeof(float));
				return default;
			}
		}

		/// <summary>
		/// Attempts various methods for converting an object to a int value.<br/>
		/// This will first attempt direct casting because it's the most efficient and the most likely scenario.<br/>
		/// String parsing will be attempted after that, then using the <see cref="Convert"/> class as a final attempt.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="doconvert">Whether to attempt using the <see cref="Convert"/> class if all other attempts fail.</param>
		/// <param name="donoprefixhex">Whether to treat a hexadecimal string without an 0x prefix as valid.</param>
		/// <returns>The converted value as a nullable int.</returns>
		public static int? ParseInt(this object obj, bool doconvert = true, bool donoprefixhex = true)
		{
			if (obj is Primitive p)
			{
				if (p.TryGetLong(out long pl))
					return unchecked((int)pl);
				if (doconvert && p.IsTrue)
					_ = Errors.TypeErrorOccurred(obj, typeof(int));
				return default;
			}
				

			if (obj is int i)
				return i;

			if (obj is long l)
				return (int)l;

			if (obj is BoolResult br)
				return br.o.ParseInt(doconvert);

			var s = obj.ToString().AsSpan().Trim();

			if (s.Length == 0)
				return new int? ();

			if (int.TryParse(s, out i))
				return i;

			if (!char.IsNumber(s[s.Length - 1]))//Handle a string specifying a int like "123I".
				if (int.TryParse(s.Slice(0, s.Length - 1), out i))
					return i;

			var neg = false;

			if (s[0] == Minus)
			{
				neg = true;
				s = s.Slice(1);
			}

			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
					int.TryParse(s.Slice(2), NumberStyles.HexNumber, Parser.inv, out var ii))
				return neg ? -ii : ii;

			if (donoprefixhex)
				if (int.TryParse(s, NumberStyles.HexNumber, Parser.inv, out ii))
					return neg ? -ii : ii;

			try
			{
				return doconvert ? Convert.ToInt32(obj) : new int? ();
			}
			catch
			{
				_ = Errors.TypeErrorOccurred(obj, typeof(int));
				return default;
			}
		}

		/// <summary>
		/// Attempts various methods for converting an object to a long value.<br/>
		/// This will first attempt direct casting because it's the most efficient and the most likely scenario.<br/>
		/// String parsing will be attempted after that, then using the <see cref="Convert"/> class as a final attempt.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="doconvert">Whether to attempt using the <see cref="Convert"/> class if all other attempts fail.</param>
		/// <param name="donoprefixhex">Whether to treat a hexadecimal string without an 0x prefix as valid.</param>
		/// <returns>The converted value as a nullable long.</returns>
		public static long? ParseLong(this object obj, bool doconvert = true, bool donoprefixhex = true)
		{
			if (obj is Primitive p)
			{
				if (p.TryGetLong(out long pl))
					return pl;
				if (doconvert && p.IsTrue)
					_ = Errors.TypeErrorOccurred(obj, typeof(long));
				return default;
			}

			long l = 0;

			if (obj.ParseLong(out l, doconvert, donoprefixhex))
				return l;
			else
				return new long? ();
		}

		public static bool ParseLong(this object obj, out long outvar, bool doconvert = true, bool donoprefixhex = true)
		{
			if (obj is Primitive p)
			{
				if (p.TryGetLong(out long pl))
				{
					outvar = pl;
					return true;
				}
				if (doconvert && p.IsTrue)
					_ = Errors.TypeErrorOccurred(obj, typeof(long));
				outvar = default;
				return default;
			}

			if (obj is long l)
			{
				outvar = l;
				return true;
			}

			if (obj is BoolResult br)
				return br.o.ParseLong(out outvar, doconvert);

            ReadOnlySpan<char> s = (obj as string ?? obj.ToString()).AsSpan().Trim();

			if (s.Length == 0)
			{
				outvar = default;
				return false;
			}

			if (long.TryParse(s, out l))
			{
				outvar = l;
				return true;
			}

			if (!char.IsNumber(s[s.Length - 1]))//Handle a string specifying a long like "123L".
				if (long.TryParse(s.Slice(0, s.Length - 1), out outvar))
					return true;

			var neg = false;

			if (s[0] == Minus)
			{
				neg = true;
				s = s.Slice(1);
			}

			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
					long.TryParse(s.Slice(2), NumberStyles.HexNumber, Parser.inv, out outvar))
			{
				if (neg)
					outvar = -outvar;

				return true;
			}

			if (donoprefixhex &&
					long.TryParse(s, NumberStyles.HexNumber, Parser.inv, out outvar))
			{
				if (neg)
					outvar = -outvar;

				return true;
			}

			try
			{
				if (doconvert)
				{
					outvar = Convert.ToInt64(obj);
					return true;
				}
			}
			catch
			{
				_ = Errors.TypeErrorOccurred(obj, typeof(long));
				outvar = default;
				return default;
			}

			outvar = default;
			return false;
		}

		/// <summary>
		/// Attempts to extract and return the object from a BoolResult if obj is a BoolResult.
		/// </summary>
		/// <param name="obj">The object to examine.</param>
		/// <returns>The .o field of the object if it was a BoolResult, else the object itself.</returns>
		public static object ParseObject(this object obj) => obj is BoolResult br ? br.o : obj;

		/// <summary>
		/// Attempts various methods for converting an object to a uint value.<br/>
		/// This will first attempt direct casting because it's the most efficient and the most likely scenario.<br/>
		/// String parsing will be attempted after that, then using the <see cref="Convert"/> class as a final attempt.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="doconvert">Whether to attempt using the <see cref="Convert"/> class if all other attempts fail.</param>
		/// <param name="donoprefixhex">Whether to treat a hexadecimal string without an 0x prefix as valid.</param>
		/// <returns>The converted value as a nullable uint.</returns>
		public static uint? ParseUInt(this object obj, bool doconvert = true, bool donoprefixhex = true)
		{
			if (obj is Primitive p)
			{
				if (p.TryGetLong(out long pl))
					return unchecked((uint)pl);
				if (doconvert && p.IsTrue)
					_ = Errors.TypeErrorOccurred(obj, typeof(uint));
				return default;
			}

			if (obj is uint i)
				return i;

			if (obj is BoolResult br)
				return br.o.ParseUInt(doconvert);

			if (obj is long l)
				return unchecked((uint)l);

			var s = obj.ToString().AsSpan().Trim();

			if (s.Length == 0)
				return new uint? ();

			if (long.TryParse(s, out var ll))
				return unchecked((uint)ll);

			if (uint.TryParse(s, out i))
				return i;

			if (!char.IsNumber(s[s.Length - 1]))//Handle a string specifying a uint like "123U".
				if (uint.TryParse(s.Slice(0, s.Length - 1), out i))
					return i;

			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
					uint.TryParse(s.Slice(2), NumberStyles.HexNumber, Parser.inv, out var ii))
				return ii;

			if (donoprefixhex)
				if (uint.TryParse(s, NumberStyles.HexNumber, Parser.inv, out ii))
					return ii;

			try
			{
				return doconvert ? Convert.ToUInt32(obj) : new uint? ();
			}
			catch
			{
				_ = Errors.TypeErrorOccurred(obj, typeof(uint));
				return default;
			}
		}

		/// <summary>
		/// Returns the string representation of an object.
		/// </summary>
		/// <param name="obj">The object to examine.</param>
		/// <returns>If obj is not null, the result of calling obj.ToString(), else empty string.</returns>
		public static string Str(this object obj) => obj != null ? obj.ToString() : "";

		public static bool IsString(this object obj, out string s)
		{
			if (obj is string ss)
			{
				s = ss; return true;
			} else if (obj is StringPrimitive sp)
			{
				s = sp.ToString(); return true;
			}
			s = default; return false;
		}

		public static bool IsString(this object obj, out StringPrimitive s)
		{
			if (obj is string ss)
			{
				s = ss; return true;
			}
			else if (obj is StringPrimitive sp)
			{
				s = sp; return true;
			}
			s = default; return false;
		}
		public static bool IsString(this object obj) => obj is string || obj is StringPrimitive;
	}
}