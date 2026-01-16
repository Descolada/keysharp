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
		public static bool Ab(this object obj, bool def = default) => obj != null && obj.ParseBool(out var b) ? b : def;

		/// <summary>
		/// Converts an object to a double.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The object as a double if it was not null, else def.</returns>
		public static double Ad(this object obj, double def = default) => obj != null && obj.ParseDouble(out double d) ? d : def;

		/// <summary>
		/// Converts an object to a float.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The object as a float if it was not null, else def.</returns>
		public static float Af(this object obj, float def = default) => obj != null && obj.ParseDouble(out double d) ? unchecked((float)d) : def;

		/// <summary>
		/// Converts an object to an int.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The object as an int if it was not null, else def.</returns>
		public static int Ai(this object obj, int def = default) => obj != null && obj.ParseLong(out long l) ? unchecked((int)l) : def;

		/// <summary>
		/// Converts an object to a long.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The object as a long if it was not null, else def.</returns>
		public static long Al(this object obj, long def = default) => obj != null && obj.ParseLong(out long l) ? l : def;

		/// <summary>
		/// Converts an object to a string.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The object as a string if it was not null, else def.</returns>
		public static string As(this object obj, string def = "") => (obj is Any kso && Functions.HasMethod(kso, "ToString") != 0L ? Invoke(kso, "ToString")?.ToString() : obj?.ToString()) ?? def;

		/// <summary>
		/// Converts an object to an unsigned int.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="def">A default value to use if obj is null.</param>
		/// <returns>The object as an unsigned int if it was not null, else def.</returns>
		public static uint Aui(this object obj, uint def = default) => obj != null && obj.ParseLong(out long ll) ? unchecked((uint)ll) : def;

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
#if WINDOWS
			else if (obj is Keysharp.Core.Menu menu)
				return menu.GetMenu();
#endif
			else if (obj is Control control)//Final check in the event it's some kind of native control or form.
				return control;

			return null;
		}

		/// <summary>
		/// Returns whether a callback result non-empty.
		/// </summary>
		/// <param name="result">The callback result to examine.</param>
		/// <returns>True if non-empty, else false.</returns>
		public static bool IsCallbackResultNonEmpty(this object result)
		{
			if (result == null) return false;
			else if (result is long ll) return ll != 0;
			else if (result is double dbl) return dbl != 0.0;
			else if (result is bool b) return b;
			else if (result is string str)
			{
				if (str.AsSpan().Trim().Length == 0) return false;
				if (str.ParseLong(out long l))
					return l != 0;
				if (str.ParseDouble(out double dl))
					return dl != 0.0;
				return true;
			}
			else if (result is BoolResult br) return br;
			return true;
		}

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
		public static bool? ParseBool(this object obj, bool parseBoolKeywords = false) => obj.ParseBool(out bool b, parseBoolKeywords) ? b : null;

		public static bool ParseBool(this object obj, out bool outvar, bool parseBoolKeywords = false)
		{
			if (obj is bool b)
			{
				outvar = b;
				return true;
			}

			if (obj is long l && (l == 0 || l == 1))
			{
				outvar = l != 0;
				return true;
			}

			if (obj is BoolResult br)
			{
				return br.o.ParseBool(out outvar);
			}

			if (parseBoolKeywords)
			{
				var onoff = Options.OnOff(obj);
				if (onoff != null)
				{
					outvar = onoff.Value;
					return true;
				}
			}

			outvar = false;
			return false;
		}

		/// <summary>
		/// Attempts various methods for converting an object to a double value.<br/>
		/// This will first attempt direct casting because it's the most efficient and the most likely scenario.<br/>
		/// String parsing will be attempted after that.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="requiredot">Whether to require a . character in the string when parsing after other attempts have failed.</param>
		/// <returns>The converted value as a nullable double.</returns>
		public static double? ParseDouble(this object obj, bool requiredot = false) => obj.ParseDouble(out double d, requiredot) ? d : null;
		public static bool ParseDouble(this object obj, out double outvar, bool requiredot = false)
		{
			if (obj is double d)
			{
				outvar = d;
				return true;
			}

			if (obj is long l)
			{
				if (requiredot) { outvar = default; return false; }
				outvar = l;
				return true;
			}

			if (obj is BoolResult br)
			{
				return br.o.ParseDouble(out outvar, requiredot);
			}

			if (obj is int i)//int is seldom used in Keysharp, so check last.
			{
				if (requiredot) { outvar = default; return false; }
				outvar = i;
				return true;
			}

			if (obj is Any)
			{
				outvar = default;
				return false;
			}

			var s = (obj as string ?? obj.ToString()).AsSpan().Trim();

			if (s.Length == 0)
			{
				outvar = 0.0D;
				return false;
			}

			if (requiredot && !s.Contains('.'))
			{
				outvar = 0.0D;
				return false;
			}

			if (double.TryParse(s, out outvar))
				return true;

			if (!char.IsNumber(s[s.Length - 1]))//Handle a string specifying a double like "123.0D".
				if (double.TryParse(s.Slice(0, s.Length - 1), out outvar))
					return true;

			return false;
		}

		/// <summary>
		/// Attempts various methods for converting an object to a float value.<br/>
		/// This will first attempt direct casting because it's the most efficient and the most likely scenario.<br/>
		/// String parsing will be attempted after that.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="requiredot">Whether to require a . character in the string when parsing after other attempts have failed.</param>
		/// <returns>The converted value as a nullable float.</returns>
		public static float? ParseFloat(this object obj, bool requiredot = false) => (float?)obj.ParseDouble(requiredot);

		/// <summary>
		/// Attempts various methods for converting an object to a int value.<br/>
		/// This will first attempt direct casting because it's the most efficient and the most likely scenario.<br/>
		/// String parsing will be attempted after that.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="donoprefixhex">Whether to treat a hexadecimal string without an 0x prefix as valid.</param>
		/// <returns>The converted value as a nullable int.</returns>
		public static int? ParseInt(this object obj, bool donoprefixhex = true) => (int?)obj.ParseLong(donoprefixhex);

		/// <summary>
		/// Attempts various methods for converting an object to a long value.<br/>
		/// This will first attempt direct casting because it's the most efficient and the most likely scenario.<br/>
		/// String parsing will be attempted after that.
		/// </summary>
		/// <param name="obj">The object to convert.</param>
		/// <param name="donoprefixhex">Whether to treat a hexadecimal string without an 0x prefix as valid.</param>
		/// <returns>The converted value as a nullable long.</returns>
		public static long? ParseLong(this object obj, bool donoprefixhex = true) => obj.ParseLong(out long l, donoprefixhex) ? l : null;

		public static bool ParseLong(this object obj, out long outvar, bool donoprefixhex = false)
		{
			if (obj is long l)
			{
				outvar = l;
				return true;
			}

			if (obj is BoolResult br)
				return br.o.ParseLong(out outvar);

			if (obj is Any)
			{
				outvar = default;
				return false;
			}

			ReadOnlySpan<char> s = (obj as string ?? obj.ToString()).AsSpan().Trim();

			if (s.Length == 0)
			{
				outvar = 0L;
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

			if (s[0] == Keywords.Minus)
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

			outvar = 0L;
			return false;
		}

		/// <summary>
		/// Attempts to extract and return the object from a BoolResult if obj is a BoolResult.
		/// </summary>
		/// <param name="obj">The object to examine.</param>
		/// <returns>The .o field of the object if it was a BoolResult, else the object itself.</returns>
		public static object ParseObject(this object obj) => obj is BoolResult br ? br.o : obj;

		/// <summary>
		/// Returns the string representation of an object.
		/// </summary>
		/// <param name="obj">The object to examine.</param>
		/// <returns>If obj is not null, the result of calling obj.ToString(), else empty string.</returns>
		public static string Str(this object obj) => obj != null ? obj.ToString() : "";
	}
}
