namespace Keysharp.Builtins
{
	public class Primitive : Any
	{
		internal static bool IsNative(object item) => item is string || item is long || item is double;
		internal static Type MapPrimitiveToNativeType(object item)
		{
			if (item is string)
				return typeof(Keysharp.Builtins.@String);
			else if (item is long)
				return typeof(Keysharp.Builtins.Integer);
			else
				return typeof(Keysharp.Builtins.Float);
		}
	}

	public class String : Primitive
	{
		/// <summary>
		/// Converts a value to a string.
		/// </summary>
		/// <param name="value">The value to convert.</param>
		/// <returns>The result of converting value to a string, or value itself if it was a string.<br/>
		/// If value's ToString() returns no value, so does this. [v2.1-alpha.30+]
		/// </returns>
		public static object staticCall(object @this, object value) => value.As(DefaultObject);

		/// <summary>
		/// Determines whether a string starts with a given string.
		/// </summary>
		/// <param name="this">The string to examine the start of.</param>
		/// <param name="token">The string to search for.</param>
		/// <param name="caseSense">If omitted, it defaults to Off (case-insensitive). Otherwise, one of:<br/>
		///     On/1/True: case-sensitive, culture-invariant.<br/>
		///     Off/0/False: case-insensitive, culture-invariant.<br/>
		///     Locale: case-sensitive, compared according to the current user's locale.
		/// </param>
		/// <returns>1 if the string started with <paramref name="token"/>, else 0.</returns>
		public static long StartsWith(object @this, object token, object caseSense = null) =>
			@this.As().StartsWith(token.As(), CaseSenseComparison(caseSense)) ? 1L : 0L;

		/// <summary>
		/// Determines whether a string ends with a given string.
		/// </summary>
		/// <param name="this">The string to examine the end of.</param>
		/// <param name="token">The string to search for.</param>
		/// <param name="caseSense">See <see cref="StartsWith"/>.</param>
		/// <returns>1 if the string ended with <paramref name="token"/>, else 0.</returns>
		public static long EndsWith(object @this, object token, object caseSense = null) =>
			@this.As().EndsWith(token.As(), CaseSenseComparison(caseSense)) ? 1L : 0L;

		/// <summary>
		/// The comparison mode for a <c>CaseSense</c> argument, routed through the same helper InStr and StrCompare
		/// use rather than inventing a second convention: omitted or Off is case-INSENSITIVE, On/1/True is
		/// case-sensitive, and both are Ordinal (culture-invariant); only the explicit <c>Locale</c> option
		/// consults the current culture, and it compares case-sensitively.
		/// </summary>
		private static StringComparison CaseSenseComparison(object caseSense)
		{
			var opt = caseSense.As();
			return opt.Length != 0 ? Conversions.ParseComparisonOption(opt) : StringComparison.OrdinalIgnoreCase;
		}
	}

	public class Number : Primitive
	{
		/// <summary>
		/// Converts a numeric string to a pure integer or floating-point number.
		/// </summary>
		/// <param name="value">The value to convert.</param>
		/// <returns>The result of converting Value to a pure integer or floating-point number, or value itself if it is<br/>
		/// already an Integer or Float value.
		/// </returns>
		/// <exception cref="TypeError">A <see cref="TypeError"/> exception is thrown if the value cannot be converted.</exception>
		public static object staticCall(object @this, object value)
		{
			if (value is long l)
				return l;
			else if (value is double d)
				return d;
			else
			{
				var s = value.As();

				if (!s.Contains('.') && s.TryParseLong(out long ll))
					return ll;

				if (s.TryParseDouble(out double dd))//Also handles scientific notation without a dot, such as "1e5".
					return dd;

				return Errors.TypeErrorOccurred(s, typeof(double));
			}
		}
	}

	public class Integer : Number
	{
		/// <summary>
		/// Converts a numeric string or floating-point value to an integer.
		/// </summary>
		/// <param name="value">The object to be converted</param>
		/// <returns>The converted value as a long.</returns>
		/// <exception cref="TypeError">A <see cref="TypeError"/> exception is thrown if the conversion failed.</exception>
		public new static object staticCall(object @this, object value) => value.ToLong();
	}

	public class Float : Number
	{
		/// <summary>
		/// Converts a numeric string or integer value to a floating-point number.
		/// </summary>
		/// <param name="value">The object to be converted</param>
		/// <returns>The converted value as a double.</returns>
		/// <exception cref="TypeError">A <see cref="TypeError"/> exception is thrown if the conversion failed.</exception>
		public new static object staticCall(object @this, object value) => value.ToDouble();
	}
}
