namespace Keysharp.Builtins
{
	public partial class Ks
	{
		/// <summary>
		/// Converts between JSON text and script values. Scripts reach it through the KS module:
		/// <c>#Import "Ks" { Json }</c>, then <c>Json.Encode(value)</c> and <c>Json.Decode(text)</c>.
		/// </summary>
		public class Json : KeysharpObject
		{
			/// <summary>
			/// The nesting limit applied when encoding. A container which (indirectly) contains itself would
			/// otherwise recurse until the stack overflows; cycles are detected by reference so that two
			/// distinct but equal containers remain legal.
			/// </summary>
			private const int MaxDepth = 128;

			/// <summary>
			/// Returns the JSON text for a script value.
			/// </summary>
			/// <param name="this">The class object, supplied by the script-static call.</param>
			/// <param name="value">
			/// The value to encode. A <see cref="Map"/> or an object's own value properties become a JSON
			/// object, an <see cref="Array"/> becomes a JSON array, a string becomes a string, a number
			/// becomes a number, and an unset value becomes null.
			/// </param>
			/// <returns>JSON text.</returns>
			/// <exception cref="ValueError">Thrown if value contains a reference cycle or nests too deeply.</exception>
			[Static]
			public static object Encode(object @this, object value)
			{
				using var stream = new MemoryStream();

				// The default encoder is HTML-safe, which would escape quotes and every non-ASCII character
				// as \uXXXX. Relaxed escaping emits the JSON a script author expects: \" for a quote and
				// literal text for anything outside ASCII.
				var options = new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

				using (var writer = new Utf8JsonWriter(stream, options))
					WriteValue(writer, value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);

				return Encoding.UTF8.GetString(stream.ToArray());
			}

			/// <summary>
			/// Returns the script value for JSON text.
			/// </summary>
			/// <param name="this">The class object, supplied by the script-static call.</param>
			/// <param name="jsonText">The JSON text to decode.</param>
			/// <returns>
			/// A <see cref="Map"/> for a JSON object, an <see cref="Array"/> for a JSON array, a string, a
			/// number, 1 or 0 for true or false, and an empty string for null.
			/// </returns>
			/// <exception cref="ValueError">Thrown if jsonText is not well-formed JSON.</exception>
			[Static]
			public static object Decode(object @this, object jsonText)
			{
				try
				{
					// Trailing commas and comments are accepted because hand-written configuration files
					// commonly carry them; everything else follows the JSON grammar.
					using var doc = JsonDocument.Parse(jsonText.As(), new JsonDocumentOptions
					{
						AllowTrailingCommas = true,
						CommentHandling = JsonCommentHandling.Skip
					});
					return ReadValue(doc.RootElement);
				}
				catch (JsonException ex)
				{
					return Errors.ValueErrorOccurred(ex.Message);
				}
			}

			/// <summary>
			/// Writes one script value to the JSON writer, recursing into containers.
			/// </summary>
			/// <param name="writer">The writer to append to.</param>
			/// <param name="value">The value to write.</param>
			/// <param name="open">The containers currently being written, used to detect a cycle.</param>
			/// <param name="depth">The current nesting depth.</param>
			private static void WriteValue(Utf8JsonWriter writer, object value, HashSet<object> open, int depth)
			{
				switch (value)
				{
					case null: writer.WriteNullValue(); return;

					case string s: writer.WriteStringValue(s); return;

					case bool b: writer.WriteBooleanValue(b); return;

					case long l: writer.WriteNumberValue(l); return;

					case int i: writer.WriteNumberValue(i); return;

					case double d: writer.WriteNumberValue(d); return;

					case decimal m: writer.WriteNumberValue(m); return;
				}

				if (depth >= MaxDepth)
				{
					_ = Errors.ValueErrorOccurred($"JSON nesting exceeds the limit of {MaxDepth}.");
					return;
				}

				if (!open.Add(value))
				{
					_ = Errors.ValueErrorOccurred("A value cannot be encoded because it contains itself.");
					return;
				}

				try
				{
					switch (value)
					{
						case Map map:
							writer.WriteStartObject();

							foreach (var (key, val) in (IEnumerable<(object, object)>)map)
							{
								writer.WritePropertyName(key?.ToString() ?? "");
								WriteValue(writer, val, open, depth + 1);
							}

							writer.WriteEndObject();
							break;

						case Array arr:
							writer.WriteStartArray();

							foreach (var item in (IEnumerable<object>)arr)
								WriteValue(writer, item, open, depth + 1);

							writer.WriteEndArray();
							break;

						case KeysharpObject kso:
							writer.WriteStartObject();

							// Only own value properties: a dynamic property would have to be invoked to produce
							// a value, which encoding a value must not do.
							if (kso.op != null)
							{
								foreach (var (name, desc) in kso.op)
								{
									if (desc.Value == null)
										continue;

									writer.WritePropertyName(name);
									WriteValue(writer, desc.Value, open, depth + 1);
								}
							}

							writer.WriteEndObject();
							break;

						default:
							writer.WriteStringValue(value.ToString());
							break;
					}
				}
				finally { _ = open.Remove(value); }
			}

			/// <summary>
			/// Converts one parsed JSON element to its script value, recursing into containers.
			/// </summary>
			/// <param name="element">The element to convert.</param>
			/// <returns>The script value.</returns>
			private static object ReadValue(JsonElement element)
			{
				switch (element.ValueKind)
				{
					case JsonValueKind.Object:
						var map = new Map();

						foreach (var prop in element.EnumerateObject())
							map[prop.Name] = ReadValue(prop.Value);

						return map;

					case JsonValueKind.Array:
						var arr = new Array();

						foreach (var item in element.EnumerateArray())
							_ = arr.Push(ReadValue(item));

						return arr;

					case JsonValueKind.String:
						return element.GetString();

					// An integral value stays an Integer so that it round-trips and indexes; anything else,
					// including a value too large for Int64, becomes a Float.
					// The (object) cast matters: without it the conditional's type unifies to double and
					// every integral value would decode as a Float.
					case JsonValueKind.Number:
						return element.TryGetInt64(out var l) ? l : (object)element.GetDouble();

					// A script has no distinct boolean type, so true/false decode to 1/0 as they do elsewhere.
					case JsonValueKind.True:
						return 1L;

					case JsonValueKind.False:
						return 0L;

					// Null has no script equivalent which can be stored in a Map, so it becomes an empty string.
					default:
						return "";
				}
			}
		}
	}
}
