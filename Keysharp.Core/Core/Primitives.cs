namespace Keysharp.Core
{
	public class Primitive : Any
	{
		internal static bool IsNative(object item) => item is string || item is long || item is double;
		internal static Type MapPrimitiveToNativeType(object item)
		{
			if (item is string)
				return typeof(Keysharp.Core.@String);
			else if (item is long)
				return typeof(Keysharp.Core.Integer);
			else
				return typeof(Keysharp.Core.Float);
		}
	}

	public class String : Primitive
	{
		/// <summary>
		/// Converts a value to a string.
		/// </summary>
		/// <param name="value">The value to convert.</param>
		/// <returns>The result of converting value to a string, or value itself if it was a string.</returns>
		public static string staticCall(object @this, object value) => value.As();

		/// <summary>
		/// Determines if a string starts with a given string, using the current culture.
		/// </summary>
		/// <param name="str">The string to examine the start of.</param>
		/// <param name="str2">The string to search for.</param>
		/// <param name="ignoreCase">True to ignore case, else case sensitive. Default: case sensitive.</param>
		/// <returns>1 if str started with str2, else 0.</returns>
		public static long StartsWith(object str, object str2, object ignoreCase = null) => str.As().StartsWith(str2.As(), ignoreCase.Ab() ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase) ? 1L : 0L;

		/// <summary>
		/// Determines if a string ends with a given string, using the current culture.
		/// </summary>
		/// <param name="str">The string to examine the end of.</param>
		/// <param name="str2">The string to search for.</param>
		/// <param name="ignoreCase">True to ignore case, else case sensitive. Default: case sensitive.</param>
		/// <returns>1 if str ended with str2, else 0.</returns>
		public static long EndsWith(object str, object str2, object ignoreCase = null) => str.As().EndsWith(str2.As(), ignoreCase.Ab() ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase) ? 1L : 0L;
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

				if (s.Contains('.'))
				{
					var val = s.ParseDouble();

					if (val.HasValue)
						return val.Value;
				}
				else
				{
					var val = s.ParseLong();

					if (val.HasValue)
						return val.Value;
				}

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
		public static object staticCall(object @this, object value)
		{
			if (value.ParseLong(out long l))
				return l;
			else if (value.ParseDouble(out double d, true))
				return (long)d;
			return Errors.TypeErrorOccurred(value, typeof(double));
		}
	}

	public class Float : Number
	{
		/// <summary>
		/// Converts a numeric string or integer value to a floating-point number.
		/// </summary>
		/// <param name="value">The object to be converted</param>
		/// <returns>The converted value as a double.</returns>
		/// <exception cref="TypeError">A <see cref="TypeError"/> exception is thrown if the conversion failed.</exception>
		public static object staticCall(object @this, object value)
		{
			if (value.ParseDouble() is double d)
				return d;
			return Errors.TypeErrorOccurred(value, typeof(double));
		}
	}
}
