using Keysharp.Builtins;
namespace Keysharp.Internals.Strings
{
	internal class RegexHolder
	{
		internal PcreRegex regex;
		internal PcrePatternInfo info;
		internal string haystack;
		internal string needle;//Unmodified RegEx pattern.
		internal string pattern;//RegEx pattern with AHK settings removed.
		internal string tag = "";
		internal PcreRegexSettings opts;
		internal string[] groupNames;

		internal RegexHolder(string hs, string n)
		{
			haystack = hs;
			needle = n;
			PcreRegexSettings settings = null;
			var parenIndex = n.IndexOf(')');

			if (parenIndex != -1)
			{
				var leftParenIndex = n.IndexOf('(');

				if (leftParenIndex == -1 || (leftParenIndex > parenIndex))//Make sure it was just a ) for settings and not a ().
				{
					var span = n.AsSpan(0, parenIndex);
					var substr = n.Substring(parenIndex + 1);
					settings = Conversions.ToRegexOptions(span);

					if (span.Contains('A'))
						substr = "\\A" + substr;

					pattern = substr;
				}
			}

			settings ??= new PcreRegexSettings();
			settings.Options |= PcreOptions.Compiled;
			pattern ??= n;
			opts = settings;
			regex = new PcreRegex(pattern, opts);
			info = regex.PatternInfo;
			groupNames = new string[info.CaptureCount + 1];

			foreach (var name in info.GroupNames)
			{
				foreach (var i in info.GetGroupIndexesByName(name))
					groupNames[i] = name;
			}

			for (int i = 0; i < groupNames.Length; i++)
				groupNames[i] ??= "";
		}
	}
}
