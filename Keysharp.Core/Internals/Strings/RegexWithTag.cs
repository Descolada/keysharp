using Keysharp.Builtins;
namespace Keysharp.Internals.Strings
{
	internal class RegexWithTag : Regex
	{
		internal string tag;

		internal RegexWithTag(string s)
			: base(s)
		{
		}

		internal RegexWithTag(string s, RegexOptions options)
			: base(s, options)
		{
		}
	}
}
