using System.Diagnostics;
using System.Linq;
using KL = Keysharp.Parsing.Lexing;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Keysharp.Tests
{
	public class LexerTests
	{
		private static System.Collections.Generic.List<KL.Token> Lex(string s) => KL.Lexer.Tokenize(s);

		private static void AssertKinds(string src, params KL.TokenKind[] expected)
		{
			var kinds = Lex(src).Select(t => t.Kind).ToArray();
			CollectionAssert.AreEqual(expected, kinds);
		}

		[Test, Category("Lexer")]
		public void Assignment()
		{
			AssertKinds("x := 1 + 2.5",
				KL.TokenKind.Identifier, KL.TokenKind.Assign, KL.TokenKind.Number,
				KL.TokenKind.Plus, KL.TokenKind.Number, KL.TokenKind.EOF);
		}

		[Test, Category("Lexer")]
		public void MaximalMunchOperators()
		{
			// >>>=  must win over >>>, >>, >
			AssertKinds("a >>>= b",
				KL.TokenKind.Identifier, KL.TokenKind.ShiftRightLogicalAssign, KL.TokenKind.Identifier, KL.TokenKind.EOF);
			AssertKinds("a ?? b ??= c",
				KL.TokenKind.Identifier, KL.TokenKind.NullCoalesce, KL.TokenKind.Identifier,
				KL.TokenKind.NullCoalesceAssign, KL.TokenKind.Identifier, KL.TokenKind.EOF);
			AssertKinds("!~= !== != !",
				KL.TokenKind.NotRegexMatch, KL.TokenKind.NotIdentity, KL.TokenKind.NotEqual, KL.TokenKind.Not, KL.TokenKind.EOF);
		}

		[Test, Category("Lexer")]
		public void Numbers()
		{
			AssertKinds("0xFF 0b1010 0o17 3.14e-2 100_000",
				KL.TokenKind.Number, KL.TokenKind.Number, KL.TokenKind.Number,
				KL.TokenKind.Number, KL.TokenKind.Number, KL.TokenKind.EOF);

			// member access on a number must NOT be eaten as a float
			AssertKinds("x.5",
				KL.TokenKind.Identifier, KL.TokenKind.Dot, KL.TokenKind.Number, KL.TokenKind.EOF);
		}

		[Test, Category("Lexer")]
		public void StringsWithEscapesAndSemicolons()
		{
			// Escaped closing quote must not end the string; literal ';' inside a string is not a comment.
			var toks = Lex("\"a`\"b ; not comment\"");
			Assert.AreEqual(KL.TokenKind.String, toks[0].Kind);
			Assert.AreEqual("\"a`\"b ; not comment\"", toks[0].Text);
			Assert.AreEqual(KL.TokenKind.EOF, toks[1].Kind);

			// single-quoted
			Assert.AreEqual(KL.TokenKind.String, Lex("'hello'")[0].Kind);
		}

		[Test, Category("Lexer")]
		public void CommentRules()
		{
			// '; comment' with leading whitespace is a comment; no tokens but the newline + EOF.
			AssertKinds("x ; trailing comment", KL.TokenKind.Identifier, KL.TokenKind.EOF);

			// ';' with no preceding whitespace is NOT a comment (becomes Unknown).
			var k = Lex("x;y").Select(t => t.Kind).ToArray();
			Assert.AreEqual(KL.TokenKind.Identifier, k[0]);
			Assert.AreEqual(KL.TokenKind.Unknown, k[1]);
			Assert.AreEqual(KL.TokenKind.Identifier, k[2]);
		}

		[Test, Category("Lexer")]
		public void NewlinesCollapse()
		{
			AssertKinds("a\n\n\nb",
				KL.TokenKind.Identifier, KL.TokenKind.Newline, KL.TokenKind.Identifier, KL.TokenKind.EOF);
		}

		[Test, Category("Lexer")]
		public void AdjacencyFlagForCallVsSpacedParen()
		{
			// Foo() — '(' is adjacent (no leading whitespace) => a call.
			var call = Lex("Foo()");
			Assert.IsFalse(call[1].LeadingWhitespace);

			// Foo () — '(' has leading whitespace.
			var spaced = Lex("Foo ()");
			Assert.IsTrue(spaced[1].LeadingWhitespace);
		}
	}
}
