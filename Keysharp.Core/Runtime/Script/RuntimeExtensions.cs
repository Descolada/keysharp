namespace Keysharp.Runtime
{
	/// <summary>
	/// Extension methods required by runtime-compiled scripts.
	/// </summary>
	public static class RuntimeExtensions
	{
		/// <summary>
		/// Attempts to extract and return the object from a BoolResult if obj is a BoolResult.
		/// </summary>
		/// <param name="obj">The object to examine.</param>
		/// <returns>The .o field of the object if it was a BoolResult, else the object itself.</returns>
		public static object ParseObject(this object obj) => obj is BoolResult br ? br.o : obj;
	}
}
