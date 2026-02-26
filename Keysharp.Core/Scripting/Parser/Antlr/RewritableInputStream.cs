using Antlr4.Runtime;
using Antlr4.Runtime.Misc;

internal interface IRewritableCharStream : ICharStream
{
	void ReplaceRange(int start, int endExclusive, string replacement);
}

internal class RewritableInputStream : AntlrInputStream, IRewritableCharStream
{
	internal RewritableInputStream(string input) : base(input)
	{
	}

	public void ReplaceRange(int start, int endExclusive, string replacement)
	{
		if (start < 0 || endExclusive < start || endExclusive > n)
			throw new ArgumentOutOfRangeException(nameof(start), $"Invalid replacement range [{start}, {endExclusive}) for stream length {n}.");

		replacement ??= string.Empty;

		var removedLength = endExclusive - start;
		var delta = replacement.Length - removedLength;
		var newData = new char[n + delta];

		if (start > 0)
			System.Array.Copy(data, 0, newData, 0, start);

		if (replacement.Length > 0)
			replacement.CopyTo(0, newData, start, replacement.Length);

		var suffixLength = n - endExclusive;
		if (suffixLength > 0)
			System.Array.Copy(data, endExclusive, newData, start + replacement.Length, suffixLength);

		data = newData;
		n = newData.Length;

		if (p >= endExclusive)
			p += delta;
		else if (p > start)
			p = start + replacement.Length;

		if (p < 0)
			p = 0;
		else if (p > n)
			p = n;
	}
}
