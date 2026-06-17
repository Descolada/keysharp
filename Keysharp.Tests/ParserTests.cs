using System.Collections.Generic;
using KL = Keysharp.Parsing.Lexing;
using KP = Keysharp.Parsing.Syntax;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	public class ParserTests
	{
		private static (string ast, List<string> diags) Parse(string s)
		{
			var p = new KP.Parser(KL.Lexer.Tokenize(s));
			var prog = p.ParseProgram();
			return (KP.AstPrinter.Print(prog), p.Diagnostics);
		}

		private static string Ast(string s)
		{
			var (ast, diags) = Parse(s);
			Assert.IsTrue(diags.Count == 0, "unexpected diagnostics: " + string.Join("; ", diags));
			return ast;
		}

		[Test, Category("Parser")]
		public void ArithmeticPrecedence()
		{
			Assert.AreEqual("(+ 2 (* 3 4))", Ast("2 + 3 * 4"));
			Assert.AreEqual("(- (+ 2 3) 4)", Ast("2 + 3 - 4"));   // additive is left-assoc
		}

		[Test, Category("Parser")]
		public void PowerIsRightAssociativeAndBindsTighterThanUnaryMinus()
		{
			Assert.AreEqual("(** 2 (** 3 2))", Ast("2 ** 3 ** 2"));
			Assert.AreEqual("(- (** 2 2))", Ast("-2 ** 2"));      // -(2**2), not (-2)**2
		}

		[Test, Category("Parser")]
		public void BitwiseBindsTighterThanComparison_AhkSemantics()
		{
			// This is the case AHK2AST gets wrong; the AHK v2 source puts & above =.
			Assert.AreEqual("(= (& a b) c)", Ast("a & b = c"));
			Assert.AreEqual("(| (^ a b) c)", Ast("a ^ b | c"));  // & > ^ > |
		}

		[Test, Category("Parser")]
		public void AssignmentIsRightAssociative()
		{
			Assert.AreEqual("(:= a (:= b c))", Ast("a := b := c"));
			Assert.AreEqual("(+= x (* y 2))", Ast("x += y * 2"));
		}

		[Test, Category("Parser")]
		public void VerbalOperators()
		{
			Assert.AreEqual("(and (not a) b)", Ast("not a and b"));   // 'not' is low-precedence
			Assert.AreEqual("(or (and a b) c)", Ast("a and b or c")); // and > or
		}

		[Test, Category("Parser")]
		public void TernaryAndMaybe()
		{
			Assert.AreEqual("(?: a b c)", Ast("a ? b : c"));
			Assert.AreEqual("(?: a b (?: c d e))", Ast("a ? b : c ? d : e")); // right-assoc
			Assert.AreEqual("(call foo (a?))", Ast("foo(a?)"));               // maybe-operator
		}

		[Test, Category("Parser")]
		public void MemberCallIndexChains()
		{
			Assert.AreEqual("(call (. (. a b) c))", Ast("a.b.c()"));
			Assert.AreEqual("(index a 1)", Ast("a[1]"));
			Assert.AreEqual("(call (. obj method) x (y?))", Ast("obj.method(x, y?)"));
			Assert.AreEqual("(call foo _ b)", Ast("foo(, b)"));   // omitted argument
		}

		[Test, Category("Parser")]
		public void NoParenCommandSyntax()
		{
			Assert.AreEqual("(call FileAppend \"pass\" \"*\")", Ast("FileAppend \"pass\", \"*\""));
			Assert.AreEqual("(call MsgBox x)", Ast("MsgBox x"));
			Assert.AreEqual("(call Send _ a)", Ast("Send , a"));     // leading-comma omit
			Assert.AreEqual("(:= x 5)", Ast("x := 5"));              // NOT a command
			Assert.AreEqual("(call foo a b)", Ast("foo(a, b)"));     // paren call still works
		}

		[Test, Category("Parser")]
		public void CommaSequences()
		{
			Assert.AreEqual("(seq (:= x 1) (:= y 2))", Ast("x := 1, y := 2"));
			Assert.AreEqual("(:= r (seq 1 2))", Ast("r := (1, 2)"));
		}

		[Test, Category("Parser")]
		public void Literals()
		{
			Assert.AreEqual("(:= x (object a:1 b:2))", Ast("x := {a: 1, b: 2}"));
			Assert.AreEqual("(:= x (array 1 2 3))", Ast("x := [1, 2, 3]"));
		}

		[Test, Category("Parser")]
		public void ControlFlow()
		{
			Assert.AreEqual("(loop 3 { (+= s \"x\") })", Ast("loop 3\n{\n\ts += \"x\"\n}"));
			Assert.AreEqual("(for v in arr { (call use v) })", Ast("for v in arr\n{\n\tuse(v)\n}"));
			Assert.AreEqual("(switch x (case 1 -> (:= r \"a\")) (default -> (:= r \"b\")))",
				Ast("switch x {\ncase 1: r := \"a\"\ndefault: r := \"b\"\n}"));
			Assert.AreEqual("(try { (throw \"x\") } catch e { (:= r \"c\") })",
				Ast("try {\n\tthrow \"x\"\n} catch as e {\n\tr := \"c\"\n}"));
		}

		[Test, Category("Parser")]
		public void ClassDeclaration()
		{
			Assert.AreEqual("(class Animal field name:=\"dog\" method Speak ())",
				Ast("class Animal {\n\tname := \"dog\"\n\tSpeak() {\n\t\treturn this.name\n\t}\n}"));
		}

		[Test, Category("Parser")]
		public void FatArrowLambdas()
		{
			Assert.AreEqual("(=> (x) (* x 2))", Ast("x => x * 2"));
			Assert.AreEqual("(=> (a b) (+ a b))", Ast("(a, b) => a + b"));
		}

		[Test, Category("Parser")]
		public void RemapSplitsSourceAndTargetInLexer()
		{
			// The lexer splits a remap `source::target` into a RemapSourceKey + RemapTargetKey pair.
			Assert.AreEqual("(remap a::b)", Ast("a::b"));
			Assert.AreEqual("(remap ^x::^c)", Ast("^x::^c"));
			Assert.AreEqual("(remap Esc::CapsLock)", Ast("Esc::CapsLock"));
		}

		[Test, Category("Parser")]
		public void Statements()
		{
			Assert.AreEqual("(if x (:= y 1) else (:= y 2))",
				Ast("if x\n\ty := 1\nelse\n\ty := 2"));
			Assert.AreEqual("(while (< i 10) (+= i 1))",
				Ast("while i < 10\n\ti += 1"));
			Assert.AreEqual("(func Add (a b) { (return (+ a b)) })",
				Ast("Add(a, b) {\n\treturn a + b\n}"));
			Assert.AreEqual("(func Sq (n) (* n n))", Ast("Sq(n) => n * n"));
		}

		[Test, Category("Parser")]
		public void LineContinuationAfterOperator()
		{
			// a trailing binary operator continues onto the next line
			Assert.AreEqual("(+ 1 2)", Ast("1 +\n2"));
		}

		// ---- error handling: malformed input must yield an understandable diagnostic (and never throw) ----

		private static void AssertDiagnostic(string src, string expectedSubstring)
		{
			var (_, diags) = KP.Parser.ParseWithDiagnostics(src);
			Assert.IsTrue(diags.Count > 0, $"expected a diagnostic for: {src}");
			Assert.IsTrue(diags.Exists(d => d.Contains(expectedSubstring)),
				$"expected a diagnostic containing '{expectedSubstring}' for: {src}\n got: {string.Join(" | ", diags)}");
		}

		[Test, Category("Parser")]
		public void UnterminatedStringReported() =>
			AssertDiagnostic("x := \"hello\n", "unterminated string");

		[Test, Category("Parser")]
		public void UnterminatedStringMidExpressionReported() =>
			AssertDiagnostic("MsgBox(\"oops)\n", "unterminated string");

		[Test, Category("Parser")]
		public void MissingClosingBraceReported() =>
			AssertDiagnostic("if (x) {\n\ty := 1\n", "expected '}'");

		[Test, Category("Parser")]
		public void MissingClosingParenReported() =>
			AssertDiagnostic("x := (1 + 2\n", "expected ')'");

		[Test, Category("Parser")]
		public void MissingClosingBracketReported() =>
			AssertDiagnostic("x := [1, 2\n", "expected ']'");

		[Test, Category("Parser")]
		public void DiagnosticsCarryLineAndColumn()
		{
			// Every diagnostic should start with a "line:col:" location so editors can point at it.
			var (_, diags) = KP.Parser.ParseWithDiagnostics("x := (1 + 2\n");
			Assert.IsTrue(diags.Count > 0);
			Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(diags[0], @"^\d+:\d+: "), "diagnostic lacks line:col — " + diags[0]);
		}

		[Test, Category("Parser")]
		public void MalformedInputNeverThrows()
		{
			// A pile of broken constructs must still terminate with diagnostics rather than throwing.
			foreach (var bad in new[] { "\"", "((((", "}}}", "class", "if (", "for x", "switch {", "x := [" })
				Assert.DoesNotThrow(() => KP.Parser.ParseWithDiagnostics(bad), "threw on: " + bad);
		}
	}
}
