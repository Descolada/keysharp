using System.Collections.Generic;
using Keysharp.Parsing.Lexing;

namespace Keysharp.Parsing.Syntax
{
	/// <summary>
	/// Hand-written recursive-descent / Pratt parser producing the strongly-typed AST.
	///
	/// Tracer-bullet scope: the full expression grammar (precedence taken from the authoritative
	/// AHK v2 source — bitwise binds tighter than comparison), calls, member/index
	/// access, fat-arrow lambdas, and the core statements (if/else, while, return, blocks,
	/// local/global/static decls, function definitions).
	///
	/// Deliberately deferred (later increments): implicit space-concatenation, dynamic vars
	/// (%...%), object/map literals, classes, switch/try/loop variants, hotkeys/directives,
	/// and no-parentheses command-style calls.
	/// </summary>
	internal sealed class Parser
	{
		private readonly List<Token> _t;
		private int _pos;
		private int _groupDepth;   // >0 inside ()/[] — newlines are insignificant
		private int _exprDepth;    // recursion guard for ParseExpression (defensive cap against malformed/unsupported input)
		private const int MaxExprDepth = 250;
		private bool _inDerefInner;   // true while parsing the inner expr of a %…% — a trailing '%' closes it, not name-building

		// Directives (e.g. #import) found in expression position, such as inside an object literal `{ #import … }`.
		// AHK processes directives per physical line regardless of nesting, so these are hoisted to program scope.
		private readonly List<Stmt> _hoistedStmts = new();
		private bool _inFlowCond;     // true while parsing a control-flow header — `(cond){…}` is the body, not an anon block fn
		// Preprocessor symbols for #if/#elif. WINDOWS is predefined (this front-end targets the Windows runtime); #define adds more.
		private readonly HashSet<string> _defines = new(System.StringComparer.OrdinalIgnoreCase)
		{
			"KEYSHARP"
#if WINDOWS
			, "WINDOWS"
#elif OSX
			, "OSX"
#elif LINUX
			, "LINUX"
#else
#error Unsupported platform symbol. Define exactly one of WINDOWS, LINUX, or OSX.
#endif
#if DEBUG
            , "DEBUG"
#endif
		};
		public readonly List<string> Diagnostics = new();

		private readonly string _includeDir;   // directory used to resolve relative #include paths (null => disabled)
		private readonly HashSet<string> _included = new(System.StringComparer.OrdinalIgnoreCase);   // #include dedup

		public Parser(List<Token> tokens, string includeDir = null) { _includeDir = includeDir; _t = Preprocess(tokens); }

		public static ProgramNode Parse(string source, string includeDir = null) => ParseWithDiagnostics(source, includeDir).program;

		// Tokenizes + parses, returning the AST together with all lex + parse diagnostics (line:col: message).
		public static (ProgramNode program, List<string> diagnostics) ParseWithDiagnostics(string source, string includeDir = null)
		{
			var diags = new List<string>();
			try
			{
				var lexer = new Lexer(source);
				var tokens = lexer.Tokenize();
				// A lex error (e.g. an unterminated string) terminates immediately — before parsing — with the first one.
				if (lexer.Diagnostics.Count > 0) throw new Keysharp.Builtins.ParseException(lexer.Diagnostics[0]);
				var parser = new Parser(tokens, includeDir);
				var prog = parser.ParseProgram();
				return (prog, parser.Diagnostics);
			}
			catch (Keysharp.Builtins.ParseException ex)
			{
				diags.Add(ex.Message);
				return (new ProgramNode(new List<Stmt>()), diags);
			}
		}

		// ---- token helpers ----
		private Token Current => _pos < _t.Count ? _t[_pos] : _t[^1];
		private Token Peek(int k) => _pos + k < _t.Count ? _t[_pos + k] : _t[^1];
		private Token Advance() { var t = Current; if (_pos < _t.Count - 1) _pos++; return t; }
		private bool At(TokenKind k) => Current.Kind == k;
		private bool AtKeyword(string w) => Current.IsKeyword(w);
		private bool Match(TokenKind k) { if (At(k)) { Advance(); return true; } return false; }
		private void SkipNewlines() { while (Current.Kind == TokenKind.Newline) Advance(); }

		private Token Expect(TokenKind k, string ctx)
		{
			if (At(k)) return Advance();
			Error($"expected {Friendly(k)} in {ctx} but found {Got()}");
			return Current;
		}

		private string ExpectIdentifier(string ctx)
		{
			if (At(TokenKind.Identifier)) return Advance().Text;
			Error($"expected an identifier in {ctx} but found {Got()}");
			return "«error»";
		}

		// Reader-friendly names for tokens, used in diagnostics (so messages read "expected '}'" not "expected RBrace").
		private static string Friendly(TokenKind k) => k switch
		{
			TokenKind.RBrace => "'}'", TokenKind.LBrace => "'{'",
			TokenKind.RParen => "')'", TokenKind.LParen => "'('",
			TokenKind.RBracket => "']'", TokenKind.LBracket => "'['",
			TokenKind.Comma => "','", TokenKind.Colon => "':'", TokenKind.FatArrow => "'=>'",
			TokenKind.Newline => "end of line", TokenKind.EOF => "end of file",
			TokenKind.Identifier => "an identifier", TokenKind.Percent => "'%'",
			_ => k.ToString()
		};

		// Describes the current (unexpected) token for diagnostics.
		private string Got() => Current.Kind switch
		{
			TokenKind.EOF => "end of file",
			TokenKind.Newline => "end of line",
			_ => $"'{Current.Text}'"
		};

		// A member name after `.` — an identifier or a numeric index (`match.0` accesses property "0").
		private string ExpectMemberName(string ctx)
		{
			if (At(TokenKind.Identifier) || At(TokenKind.Number)) return Advance().Text;
			Error($"expected a member name in {ctx} but found {Got()}");
			return "«error»";
		}

		// Member access after a consumed `.`/`?.`. The member name is a run of adjacent parts: identifiers/numbers
		// (literal text) and `%expr%` derefs. A run containing any deref is DYNAMIC (`obj.%x%`, `obj.a%x%b`) and the
		// name is the parts concatenated (`Concat("a", x, "b")`); a single plain identifier/number is a static name.
		private Expr MakeMember(Expr target, bool nullConditional)
		{
			Expr nameExpr = null;       // accumulated name (concatenation) — used when dynamic
			string firstStatic = null;  // the lone identifier/number when the run is a single static part
			bool dynamic = false, first = true;
			while (true)
			{
				if (!first && Current.LeadingWhitespace) break;   // only adjacent parts continue the name
				Expr part;
				if (At(TokenKind.Percent))
				{   // inside %…% a bare '%' is the closing delimiter, not modulo — guard with _inDerefInner.
					Advance();
					var savedDeref = _inDerefInner; _inDerefInner = true;
					part = ParseExpression(1);
					_inDerefInner = savedDeref;
					Expect(TokenKind.Percent, "dynamic member name"); dynamic = true;
				}
				else if (At(TokenKind.Identifier) || At(TokenKind.Number))
				{ var txt = Advance().Text; if (first) firstStatic = txt; part = new LiteralExpr(LiteralKind.String, "\"" + txt + "\""); }
				else break;
				nameExpr = nameExpr == null ? part : new BinaryExpr(".", nameExpr, part);
				first = false;
				if (Current.LeadingWhitespace || !(At(TokenKind.Identifier) || At(TokenKind.Number) || At(TokenKind.Percent))) break;
			}
			if (nameExpr == null) { Error($"expected a member name in member access but found {Got()}"); return new MemberExpr(target, "«error»", nullConditional); }
			return dynamic ? new DynMemberExpr(target, nameExpr, nullConditional) : new MemberExpr(target, firstStatic, nullConditional);
		}

		// Keywords that can start a statement — used to disambiguate a trailing `?` (maybe operator) from a ternary.
		private static readonly System.Collections.Generic.HashSet<string> statementKeywords =
			new(System.StringComparer.OrdinalIgnoreCase)
			// NOTE: `throw` and `class` are intentionally excluded — both can appear as a ternary then-branch
			// (`cond ? throw(x) : y`, and `class` is a legal variable name: `cond ? class : y`), so a `?` followed
			// by them is a ternary, not the maybe operator.
			{ "if", "else", "while", "loop", "for", "switch", "try", "catch", "finally",
			  "break", "continue", "goto", "return", "local", "global", "static", "until" };
		private bool IsStatementKeyword() => At(TokenKind.Identifier) && statementKeywords.Contains(Current.Text);

		// A lex/parse error terminates parsing immediately by throwing (caught in ParseWithDiagnostics and surfaced as a
		// single diagnostic), so error-recovery can't silently produce a wrong AST or cascade bogus follow-on errors.
		private void Error(string msg) => throw new Keysharp.Builtins.ParseException(
			$"{(Current.File != null ? Current.File + ":" : "")}{Current.Line}:{Current.Column}: {msg}");

		// ---- program / statements ----

		public ProgramNode ParseProgram()
		{
			var body = new List<Stmt>();
			SkipNewlines();
			while (!At(TokenKind.EOF))
			{
				var p = _pos;
				var s = ParseStatement();
				if (s != null) body.Add(s);
				// Drain directives lifted out of an object literal in this statement at THIS position, so they stay in
				// the correct #Module segment (appending at the end would misplace them into the last module).
				if (_hoistedStmts.Count > 0) { body.AddRange(_hoistedStmts); _hoistedStmts.Clear(); }
				if (_pos == p) Advance();  // guarantee progress on error
				SkipNewlines();
			}
			return new ProgramNode(body);
		}

		private Stmt ParseStatement()
		{
			SkipNewlines();
			if (At(TokenKind.RemapSourceKey))
			{
				var src = Advance().Text;
				return new RemapDef(src, Expect(TokenKind.RemapTargetKey, "remap target").Text);
			}
			if (At(TokenKind.HotkeyTrigger)) return ParseHotkey();
			if (At(TokenKind.HotstringTrigger)) return ParseHotstring();
			if (At(TokenKind.Hash)) return ParseDirective();
			if (At(TokenKind.LBrace)) return ParseBlock();
			if (AtKeyword("if")) return ParseIf();
			if (AtKeyword("while")) return ParseWhile();
			if (AtKeyword("loop")) return ParseLoop();
			if (AtKeyword("for")) return ParseFor();
			if (AtKeyword("switch")) return ParseSwitch();
			if (AtKeyword("try")) return ParseTry();
			if (AtKeyword("throw")) return ParseThrow();
			if (AtKeyword("break")) { Advance(); return new BreakStmt(ParseLoopJumpTarget()); }
			if (AtKeyword("continue")) { Advance(); return new ContinueStmt(ParseLoopJumpTarget()); }
			if (AtKeyword("goto")) return ParseGoto();
			if (AtKeyword("return")) return ParseReturn();
			if (AtKeyword("export") && Peek(1).Kind == TokenKind.Identifier) return ParseExport();
			if (AtKeyword("local") || AtKeyword("global") || AtKeyword("static")) return ParseDecl();
			if (AtKeyword("class") && Peek(1).Kind == TokenKind.Identifier) return ParseClass();
			if (AtKeyword("struct") && Peek(1).Kind == TokenKind.Identifier) return ParseClass(isStruct: true);
			// A label: `name:` alone on its own line. The colon must be ADJACENT to the name (no left whitespace) and
			// end the line — this distinguishes it from a ternary's `: arm` continued on its own line (`false :`).
			if (At(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Colon && !Peek(1).LeadingWhitespace
				&& (Peek(2).Kind == TokenKind.Newline || Peek(2).Kind == TokenKind.EOF))
			{ var lbl = Advance().Text; Advance(); return new LabelStmt(lbl); }
			if (IsFunctionDefinition()) return ParseFunctionDecl();
			if (IsCommandStatement()) return ParseCommandCall();

			var e = ParseExpression(1);
			// A comma continues the statement as a sequence (`x := 1, y := 2`). The comma may also start the NEXT
			// line — a leading comma is a line continuation — so look past newlines before each comma.
			var commaSave = _pos;
			SkipNewlines();
			if (At(TokenKind.Comma))   // comma statement: `x := 1, y := 2` (possibly split across lines)
			{
				var items = new List<Expr> { e };
				while (true)
				{
					if (!Match(TokenKind.Comma)) break;
					SkipNewlines();
					items.Add(ParseExpression(1));
					var s2 = _pos;
					SkipNewlines();
					if (!At(TokenKind.Comma)) { _pos = s2; break; }
				}
				return new ExpressionStmt(new SequenceExpr(items));
			}
			_pos = commaSave;
			return new ExpressionStmt(e);
		}

		private Block ParseBlock()
		{
			Expect(TokenKind.LBrace, "block");
			var body = new List<Stmt>();
			SkipNewlines();
			while (!At(TokenKind.RBrace) && !At(TokenKind.EOF))
			{
				var p = _pos;
				body.Add(ParseStatement());
				if (_pos == p) Advance();
				SkipNewlines();
			}
			Expect(TokenKind.RBrace, "block");
			return new Block(body);
		}

		private Stmt ParseBodyStatement()
		{
			SkipNewlines();
			return At(TokenKind.LBrace) ? ParseBlock() : ParseStatement();
		}

		// Parses a control-flow header expression: a trailing `(…){` is the flow body, not an anon block fn.
		private Expr ParseCondExpr()
		{
			var saved = _inFlowCond; _inFlowCond = true;
			var e = ParseExpression(1);
			_inFlowCond = saved;
			return e;
		}

		private Stmt ParseIf()
		{
			Advance(); // if
			var cond = ParseCondExpr();
			var then = ParseBodyStatement();
			Stmt els = null;
			var save = _pos;
			SkipNewlines();
			if (AtKeyword("else")) { Advance(); els = ParseBodyStatement(); }
			else _pos = save;
			return new IfStmt(cond, then, els);
		}

		private Stmt ParseWhile()
		{
			Advance();
			var cond = ParseCondExpr();
			var body = ParseBodyStatement();
			Expr until = null;
			var save = _pos;
			SkipNewlines();
			if (AtKeyword("until")) { Advance(); until = ParseCondExpr(); }
			else _pos = save;
			return new WhileStmt(cond, body, until) { Else = ParseOptionalLoopElse() };
		}

		// A loop may be followed by `else { … }` (or `else <stmt>`), which runs iff the loop body never executed.
		// Returns the else body or null (restoring position when no else is present).
		private Stmt ParseOptionalLoopElse()
		{
			var save = _pos;
			SkipNewlines();
			if (AtKeyword("else")) { Advance(); return ParseBodyStatement(); }
			_pos = save;
			return null;
		}

		private static readonly System.Collections.Generic.HashSet<string> loopSubKinds =
			new(System.StringComparer.OrdinalIgnoreCase) { "parse", "files", "read", "reg" };

		private Stmt ParseLoop()
		{
			Advance(); // loop
			// Specialized loops: `Loop Parse/Files/Read/Reg <arg>, <arg>, …`. The sub-keyword is an identifier
			// directly after Loop and not itself the loop count (it must be followed by an argument, '{' or EOL —
			// not by an operator like `:=`/`.`/`+`, which would mean it's a variable used as the count expression).
			if (At(TokenKind.Identifier) && loopSubKinds.Contains(Current.Text) && IsSpecialLoopFollow(Peek(1)))
			{
				var kind = Advance().Text.ToLowerInvariant();
				Match(TokenKind.Comma);   // the comma separating the sub-keyword from the first arg (`Loop Parse, X`)
				var args = new System.Collections.Generic.List<Expr>();
				while (!At(TokenKind.LBrace) && !At(TokenKind.Newline) && !At(TokenKind.EOF))
				{
					if (At(TokenKind.Comma)) { args.Add(null); Advance(); continue; }   // omitted arg
					args.Add(ParseExpression(1));
					if (!Match(TokenKind.Comma)) break;
				}
				var sbody = ParseBodyStatement();
				var ssave = _pos;
				SkipNewlines();
				var sl = new SpecialLoopStmt(kind, args, sbody);
				if (AtKeyword("until")) { Advance(); sl.Until = ParseCondExpr(); }
				else _pos = ssave;
				sl.Else = ParseOptionalLoopElse();
				return sl;
			}
			Expr count = (At(TokenKind.LBrace) || At(TokenKind.Newline) || At(TokenKind.EOF)) ? null : ParseCondExpr();
			var body = ParseBodyStatement();
			// Optional trailing `Until cond` (on its own line) turns the loop into a do-until.
			Expr lpUntil = null;
			var save = _pos;
			SkipNewlines();
			if (AtKeyword("until")) { Advance(); lpUntil = ParseCondExpr(); }
			else _pos = save;
			return new LoopStmt(count, body, lpUntil) { Else = ParseOptionalLoopElse() };
		}

		// True when the token after a Loop sub-keyword starts an argument list (or ends the header), i.e. the
		// sub-keyword is a real specialized-loop keyword rather than a variable used as the loop count.
		private static bool IsSpecialLoopFollow(Token t) => t.Kind switch
		{
			TokenKind.String or TokenKind.Number or TokenKind.Identifier or TokenKind.Percent
				or TokenKind.LParen or TokenKind.Comma or TokenKind.LBrace or TokenKind.Newline or TokenKind.EOF => true,
			_ => false,
		};

		private Stmt ParseFor()
		{
			Advance(); // for
			bool paren = Match(TokenKind.LParen);   // optional parens around the header: for (i, v in arr)
			var vars = new List<string> { ParseForVar() };
			while (Match(TokenKind.Comma)) vars.Add(ParseForVar());
			if (!AtKeyword("in")) Error($"expected 'in' in for loop but found {Got()}");
			else Advance();
			var enumerable = ParseCondExpr();
			if (paren) Expect(TokenKind.RParen, "for header");
			var forStmt = new ForStmt(vars, enumerable, ParseBodyStatement());
			var fsave = _pos;
			SkipNewlines();
			if (AtKeyword("until")) { Advance(); forStmt.Until = ParseCondExpr(); }
			else _pos = fsave;
			forStmt.Else = ParseOptionalLoopElse();
			return forStmt;
		}

		// A for-loop variable, or null if omitted (e.g. `for (, v in arr)` discards the first value).
		private string ParseForVar() => (At(TokenKind.Comma) || AtKeyword("in")) ? null : ExpectIdentifier("for variable");

		private Stmt ParseSwitch()
		{
			Advance(); // switch
			// The switch value is on the same line; a newline or '{' right after `switch` means a value-less switch.
			Expr value = null, caseSense = null;
			if (!At(TokenKind.Newline) && !At(TokenKind.LBrace))
			{
				value = ParseCondExpr();
				if (Match(TokenKind.Comma)) caseSense = ParseExpression(1);   // `switch v, caseSense`
			}
			SkipNewlines();
			Expect(TokenKind.LBrace, "switch body");
			var cases = new List<SwitchCase>();
			List<Stmt> defaultBody = null;
			SkipNewlines();
			while (!At(TokenKind.RBrace) && !At(TokenKind.EOF))
			{
				if (AtKeyword("case"))
				{
					Advance();
					var values = new List<Expr> { ParseExpression(1) };
					while (Match(TokenKind.Comma)) values.Add(ParseExpression(1));
					Expect(TokenKind.Colon, "case");
					cases.Add(new SwitchCase(values, ParseCaseBody()));
				}
				else if (AtKeyword("default"))
				{
					Advance();
					Expect(TokenKind.Colon, "default");
					defaultBody = ParseCaseBody();
				}
				else { Error($"expected 'case' or 'default' in switch but found {Got()}"); Advance(); }
				SkipNewlines();
			}
			Expect(TokenKind.RBrace, "switch body");
			return new SwitchStmt(value, caseSense, cases, defaultBody);
		}

		// Statements up to the next case/default or closing brace.
		private List<Stmt> ParseCaseBody()
		{
			var body = new List<Stmt>();
			SkipNewlines();
			while (!At(TokenKind.RBrace) && !At(TokenKind.EOF) && !AtKeyword("case") && !AtKeyword("default"))
			{
				var p = _pos;
				body.Add(ParseStatement());
				if (_pos == p) Advance();
				SkipNewlines();
			}
			return body;
		}

		private Stmt ParseTry()
		{
			Advance(); // try
			var body = ParseBodyStatement();
			var catches = new List<CatchBlock>();
			Stmt elseBody = null, finallyBody = null;

			while (true)
			{
				var save = _pos;
				SkipNewlines();
				if (!AtKeyword("catch")) { _pos = save; break; }
				Advance(); // catch
				catches.Add(ParseCatchClause());
			}
			var save2 = _pos;
			SkipNewlines();
			if (AtKeyword("else")) { Advance(); elseBody = ParseBodyStatement(); save2 = _pos; SkipNewlines(); }
			if (AtKeyword("finally")) { Advance(); finallyBody = ParseBodyStatement(); }
			else _pos = save2;

			return new TryStmt(body, catches, elseBody, finallyBody);
		}

		// A catch clause after the `catch` keyword: optional type list (parenthesized or not) and optional `as`/trailing var.
		private CatchBlock ParseCatchClause()
		{
			var types = new List<string>();
			string var = null;
			if (Match(TokenKind.LParen))   // catch (Type1, Type2 as var) | catch (Type) | catch ()
			{
				while (!At(TokenKind.RParen) && !At(TokenKind.EOF))
				{
					if (AtKeyword("as")) { Advance(); var = ExpectIdentifier("catch variable"); break; }
					types.Add(ExpectIdentifier("catch type"));
					if (AtKeyword("as")) { Advance(); var = ExpectIdentifier("catch variable"); break; }
					if (!Match(TokenKind.Comma)) break;
				}
				Expect(TokenKind.RParen, "catch type list");
			}
			else if (At(TokenKind.Identifier) && !AtKeyword("as"))   // catch Type [, Type]* [as var | var]
			{
				types.Add(ExpectIdentifier("catch type"));
				while (Match(TokenKind.Comma)) { if (AtKeyword("as")) break; types.Add(ExpectIdentifier("catch type")); }
			}
			if (var == null)
			{
				if (AtKeyword("as")) { Advance(); var = ExpectIdentifier("catch variable"); }
				else if (At(TokenKind.Identifier)) var = ExpectIdentifier("catch variable");   // catch Type var (no 'as')
			}
			return new CatchBlock(types, var, ParseBodyStatement());
		}

		// #DirectiveName trailing-args  — the trailing text is consumed raw (directives use unquoted args).
		private Stmt ParseDirective()
		{
			Advance(); // #
			var name = At(TokenKind.Identifier) ? Advance().Text : "";
			var sb = new System.Text.StringBuilder();
			int brace = 0;   // a `{ … }` import list (e.g. #import "M" { a as b, … }) may span several lines
			while ((brace > 0 || !At(TokenKind.Newline)) && !At(TokenKind.EOF))
			{
				if (At(TokenKind.Newline)) { Advance(); continue; }
				if (At(TokenKind.LBrace)) brace++;
				else if (At(TokenKind.RBrace) && brace > 0) brace--;
				if (Current.LeadingWhitespace && sb.Length > 0) sb.Append(' ');
				sb.Append(Advance().Text);
			}
			return new DirectiveStmt(name, sb.ToString());
		}

		// export [default] <function | class | variable-assignment> — marks a module export.
		private Stmt ParseExport()
		{
			Advance();   // 'export'
			bool isDefault = AtKeyword("default");
			if (isDefault) Advance();
			return new ExportStmt(isDefault, ParseStatement());
		}

		// hotkey : HotkeyTrigger (EOL HotkeyTrigger)* s* (functionDeclaration | statement)
		// Stacked trigger-only lines share the single following body (block, statement, or named function).
		private Stmt ParseHotkey()
		{
			var triggers = new List<string>();
			while (true)
			{
				triggers.Add(Advance().Text);          // HotkeyTrigger
				var save = _pos;
				SkipNewlines();
				if (At(TokenKind.HotkeyTrigger)) continue;
				_pos = save;
				break;
			}
			SkipNewlines();
			if (IsFunctionDefinition()) return new HotkeyDef(triggers, null, (FunctionDecl)ParseFunctionDecl());
			if (At(TokenKind.EOF) || At(TokenKind.HotkeyTrigger) || At(TokenKind.HotstringTrigger) || At(TokenKind.RemapSourceKey))
				return new HotkeyDef(triggers, new Block(new List<Stmt>()), null);
			return new HotkeyDef(triggers, ParseBodyStatement(), null);
		}

		// hotstring : HotstringTrigger (EOL HotstringTrigger)* WS* (Expansion | EOL? functionDeclaration | EOL? statement)
		private Stmt ParseHotstring()
		{
			var triggers = new List<string>();
			while (true)
			{
				triggers.Add(Advance().Text);          // HotstringTrigger
				if (At(TokenKind.HotstringExpansion))  // inline / continuation-section expansion
					return new HotstringDef(triggers, Advance().Text, null, null);
				var save = _pos;
				SkipNewlines();
				if (At(TokenKind.HotstringTrigger)) continue;
				_pos = save;
				break;
			}
			SkipNewlines();
			if (IsFunctionDefinition()) return new HotstringDef(triggers, null, null, (FunctionDecl)ParseFunctionDecl());
			if (At(TokenKind.EOF) || At(TokenKind.HotkeyTrigger) || At(TokenKind.HotstringTrigger) || At(TokenKind.RemapSourceKey))
				return new HotstringDef(triggers, "", null, null);
			return new HotstringDef(triggers, null, ParseBodyStatement(), null);
		}

		// ---- conditional-compilation preprocessor ----
		// Resolves #if/#elif/#else/#endif and #define/#undef on the token stream BEFORE parsing, so a directive can
		// split a single statement (e.g. an `if (#if WINDOWS cond1 #else cond2 #endif)` condition). Only tokens in
		// the active branch survive; the directive lines themselves are removed.
		private List<Token> Preprocess(List<Token> src) => Preprocess(src, _includeDir);

		// `includeDir` is the directory relative includes in THIS token stream resolve against (the main script dir, or
		// an #included file's own dir for its nested includes).
		private List<Token> Preprocess(List<Token> src, string includeDir)
		{
			var outp = new List<Token>(src.Count);
			var stack = new Stack<(bool parentActive, bool taken, bool active)>();
			bool Emit() { foreach (var f in stack) if (!f.active) return false; return true; }

			int i = 0;
			while (i < src.Count)
			{
				var t = src[i];
				// #include / #includeagain <file>: read the file, lex it, and splice its tokens in (recursing into the
				// outer loop handles nested includes). Plain #include dedups already-included files; #includeagain doesn't.
				if (_includeDir != null && t.Kind == TokenKind.Hash && i + 1 < src.Count && src[i + 1].Kind == TokenKind.Identifier
					&& (src[i + 1].Text.Equals("include", System.StringComparison.OrdinalIgnoreCase)
						|| src[i + 1].Text.Equals("includeagain", System.StringComparison.OrdinalIgnoreCase)) && Emit())
				{
					bool again = src[i + 1].Text.Equals("includeagain", System.StringComparison.OrdinalIgnoreCase);
					int j = i + 2;
					var fileToks = new List<Token>();
					while (j < src.Count && src[j].Kind != TokenKind.Newline && src[j].Kind != TokenKind.EOF) fileToks.Add(src[j++]);
					if (fileToks.Count == 0)   // continuation form: `#include` <nl> `(` file `)`
					{
						while (j < src.Count && src[j].Kind == TokenKind.Newline) j++;
						if (j < src.Count && src[j].Kind == TokenKind.LParen)
						{
							j++;
							while (j < src.Count && src[j].Kind != TokenKind.RParen && src[j].Kind != TokenKind.EOF)
							{ if (src[j].Kind != TokenKind.Newline) fileToks.Add(src[j]); j++; }
							if (j < src.Count && src[j].Kind == TokenKind.RParen) j++;
						}
					}
					var included = ResolveAndLexInclude(fileToks, again, includeDir, out var includedDir);
					// Recursively preprocess the included tokens against THEIR directory (so nested relative includes
					// resolve correctly), then emit the result.
					if (included != null && Emit()) outp.AddRange(Preprocess(included, includedDir));
					i = j;
					continue;
				}
				if (t.Kind == TokenKind.Hash && i + 1 < src.Count && src[i + 1].Kind == TokenKind.Identifier && IsCondDirective(src[i + 1].Text))
				{
					var name = src[i + 1].Text;
					int j = i + 2;
					var cond = new List<Token>();
					while (j < src.Count && src[j].Kind != TokenKind.Newline && src[j].Kind != TokenKind.EOF) cond.Add(src[j++]);
					bool emit = Emit();
					if (name.Equals("if", System.StringComparison.OrdinalIgnoreCase))
					{ bool v = emit && EvalCond(cond); stack.Push((emit, v, v)); }
					else if (name.Equals("elif", System.StringComparison.OrdinalIgnoreCase) && stack.Count > 0)
					{ var f = stack.Pop(); bool v = f.parentActive && !f.taken && EvalCond(cond); stack.Push((f.parentActive, f.taken || v, v)); }
					else if (name.Equals("else", System.StringComparison.OrdinalIgnoreCase) && stack.Count > 0)
					{ var f = stack.Pop(); bool v = f.parentActive && !f.taken; stack.Push((f.parentActive, true, v)); }
					else if (name.Equals("endif", System.StringComparison.OrdinalIgnoreCase) && stack.Count > 0)
						stack.Pop();
					else if (name.Equals("define", System.StringComparison.OrdinalIgnoreCase))
					{ if (emit && cond.Count > 0 && cond[0].Kind == TokenKind.Identifier) _defines.Add(cond[0].Text); }
					else if (name.Equals("undef", System.StringComparison.OrdinalIgnoreCase))
					{ if (emit && cond.Count > 0 && cond[0].Kind == TokenKind.Identifier) _defines.Remove(cond[0].Text); }
					i = j;   // leave the trailing newline to be emitted normally, preserving line separation
					continue;
				}
				if (Emit()) outp.Add(t);
				i++;
			}
			return outp.Count > 0 ? outp : src;   // never hand the parser an empty stream
		}

		// Reconstructs the include filename from its tokens, resolves it against the include dir, and returns the
		// included file's tokens (minus EOF). Returns null on dedup/missing-file.
		private List<Token> ResolveAndLexInclude(List<Token> fileToks, bool again, string baseDir, out string includedDir)
		{
			includedDir = baseDir;
			if (fileToks.Count == 0) return null;
			string file;
			if (fileToks.Count == 1 && fileToks[0].Kind == TokenKind.String && fileToks[0].Text.Length >= 2)
				file = fileToks[0].Text.Substring(1, fileToks[0].Text.Length - 2);   // strip quotes
			else
			{
				var sb = new System.Text.StringBuilder();
				for (int k = 0; k < fileToks.Count; k++) { if (k > 0 && fileToks[k].LeadingWhitespace) sb.Append(' '); sb.Append(fileToks[k].Text); }
				file = sb.ToString().Trim();
			}
			// %A_ScriptDir% is always the MAIN script dir; *i (ignore-if-missing) prefix; relative paths resolve against
			// the INCLUDING file's directory (baseDir) so nested includes work.
			file = System.Text.RegularExpressions.Regex.Replace(file, "%A_ScriptDir%", _includeDir, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			file = file.TrimStart('*', 'i', 'I', ' ').Trim();
			string path;
			try { path = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(file) ? file : System.IO.Path.Combine(baseDir, file)); }
			catch { return null; }
			if (!again && !_included.Add(path)) return null;   // already included (plain #include dedups)
			else if (again) _included.Add(path);
			if (!System.IO.File.Exists(path)) return null;
			includedDir = System.IO.Path.GetDirectoryName(path);   // nested includes in this file resolve against its dir
			var fileName = System.IO.Path.GetFileName(path);
			// The lexer stamps each token with this file name (per-file line numbers + diagnostics), no post-pass needed.
			var lexer = new Lexing.Lexer(System.IO.File.ReadAllText(path), fileName);
			var toks = lexer.Tokenize();
			// A lex error in the included file terminates immediately, reported against that file.
			if (lexer.Diagnostics.Count > 0) throw new Keysharp.Builtins.ParseException($"{fileName}:{lexer.Diagnostics[0]}");
			if (toks.Count > 0 && toks[^1].Kind == TokenKind.EOF) toks.RemoveAt(toks.Count - 1);   // drop the included EOF
			toks.Insert(0, new Token(TokenKind.Newline, "\n", 0, 0, 0, 0, true, fileName));   // keep line separation from the host
			return toks;
		}

		private static bool IsCondDirective(string name) =>
			name.Equals("if", System.StringComparison.OrdinalIgnoreCase) || name.Equals("elif", System.StringComparison.OrdinalIgnoreCase)
			|| name.Equals("else", System.StringComparison.OrdinalIgnoreCase) || name.Equals("endif", System.StringComparison.OrdinalIgnoreCase)
			|| name.Equals("define", System.StringComparison.OrdinalIgnoreCase) || name.Equals("undef", System.StringComparison.OrdinalIgnoreCase);

		// Evaluates an #if/#elif condition: defined symbols, integer literals, !, &&/and, ||/or, and parentheses.
		private bool EvalCond(List<Token> toks) { int p = 0; return EvalCondOr(toks, ref p); }

		private bool EvalCondOr(List<Token> t, ref int p)
		{
			var v = EvalCondAnd(t, ref p);
			while (p < t.Count && (t[p].Kind == TokenKind.LogicalOr || t[p].IsKeyword("or"))) { p++; v = EvalCondAnd(t, ref p) || v; }
			return v;
		}

		private bool EvalCondAnd(List<Token> t, ref int p)
		{
			var v = EvalCondUnary(t, ref p);
			while (p < t.Count && (t[p].Kind == TokenKind.LogicalAnd || t[p].IsKeyword("and"))) { p++; v = EvalCondUnary(t, ref p) && v; }
			return v;
		}

		private bool EvalCondUnary(List<Token> t, ref int p)
		{
			if (p < t.Count && (t[p].Kind == TokenKind.Not || t[p].IsKeyword("not"))) { p++; return !EvalCondUnary(t, ref p); }
			if (p < t.Count && t[p].Kind == TokenKind.LParen)
			{
				p++;
				var v = EvalCondOr(t, ref p);
				if (p < t.Count && t[p].Kind == TokenKind.RParen) p++;
				return v;
			}
			if (p < t.Count && t[p].Kind == TokenKind.Number) { var r = t[p++].Text; return r != "0" && r != "0.0"; }
			if (p < t.Count && t[p].Kind == TokenKind.Identifier) return _defines.Contains(t[p++].Text);
			if (p < t.Count) p++;
			return false;
		}

		private Stmt ParseThrow()
		{
			Advance(); // throw
			if (At(TokenKind.Newline) || At(TokenKind.EOF) || At(TokenKind.RBrace)) return new ThrowStmt(null);
			return new ThrowStmt(ParseExpression(1));
		}

		private Stmt ParseReturn()
		{
			Advance();
			if (At(TokenKind.Newline) || At(TokenKind.EOF) || At(TokenKind.RBrace))
				return new ReturnStmt(null);
			return new ReturnStmt(ParseExpression(1));
		}

		// Optional `break`/`continue` target on the same line: a loop level (number) or a source label name.
		private string ParseLoopJumpTarget()
		{
			if (At(TokenKind.Number) || At(TokenKind.Identifier)) return Advance().Text;
			return null;
		}

		// `Goto Label` / `Goto(Label)` / `Goto, Label` — the destination label name.
		private Stmt ParseGoto()
		{
			Advance(); // goto
			Match(TokenKind.Comma);
			bool paren = Match(TokenKind.LParen);
			var target = ExpectIdentifier("goto target");
			if (paren) Expect(TokenKind.RParen, "goto target");
			return new GotoStmt(target);
		}

		private Stmt ParseDecl()
		{
			var kw = Advance().Text.ToLowerInvariant();
			// `static name(...) {…}` / `static name(...) => expr` is a (static, non-capturing) nested function definition.
			if (IsFunctionDefinition()) return ParseFunctionDecl(isStatic: kw == "static");
			var items = new List<Expr>();
			// A decl list continues across lines on a comma — trailing (`X := 1,` ⏎ `Y`) OR leading (`X := 1` ⏎ `, Y`).
			// `global`/`local` alone (assume-global/local) has no items and must not eat the next statement.
			while (!At(TokenKind.Newline) && !At(TokenKind.EOF) && !At(TokenKind.RBrace))
			{
				items.Add(ParseExpression(1));
				var save = _pos;
				SkipNewlines();
				if (At(TokenKind.Comma)) { Advance(); SkipNewlines(); continue; }
				_pos = save;
				break;
			}
			return new DeclStmt(kw, items);
		}

		private Stmt ParseClass(bool isStruct = false)
		{
			Advance(); // class / struct
			var name = ExpectIdentifier(isStruct ? "struct name" : "class name");
			string baseName = null;
			if (AtKeyword("extends"))
			{
				Advance();
				baseName = ExpectIdentifier("base class");
				while (At(TokenKind.Dot)) { Advance(); baseName += "." + ExpectIdentifier("base class"); }  // dotted base e.g. Gui.Control
			}
			SkipNewlines();   // `class Name` and `{` are often on separate lines
			Expect(TokenKind.LBrace, "class body");

			var fields = new List<ClassField>();
			var methods = new List<ClassMethod>();
			var properties = new List<ClassProperty>();
			var nested = new List<ClassDecl>();
			var staticInits = new List<Stmt>();     // `static x.y := z` member/index-target static initializers
			var instanceInits = new List<Stmt>();   // `x.y := z` member/index-target instance initializers
			string classRequires = null;
			long structPack = 0;   // #StructPack alignment in effect for subsequent typed fields (0 = default)
			SkipNewlines();
			while (!At(TokenKind.RBrace) && !At(TokenKind.EOF))
			{
				var p = _pos;
				// A directive in the class body (e.g. `#Requires AutoHotkey v2.1` sets the class's compatibility mode).
				if (At(TokenKind.Hash))
				{
					var dir = (DirectiveStmt)ParseDirective();
					if (dir.Name.Equals("Requires", System.StringComparison.OrdinalIgnoreCase)) classRequires = dir.Args;
					// #StructPack [1|2|4|8] sets the max alignment for subsequent typed fields (0/omitted resets to default).
					else if (dir.Name.Equals("StructPack", System.StringComparison.OrdinalIgnoreCase))
						structPack = long.TryParse((dir.Args ?? "").Trim(), out var sp) ? sp : 0;
					SkipNewlines();
					continue;
				}
				// A nested class/struct declaration (`class Inner { … }` / `struct Inner { … }`).
				if ((AtKeyword("class") || AtKeyword("struct")) && Peek(1).Kind == TokenKind.Identifier)
				{
					if (ParseClass(isStruct: AtKeyword("struct")) is ClassDecl ncd) nested.Add(ncd);
					SkipNewlines();
					continue;
				}
				var isStatic = AtKeyword("static");
				if (isStatic) Advance();

				if (IsFunctionDefinition())
				{
					var mname = Advance().Text;
					var ps = ParseParamList();
					SkipNewlines();
					if (Match(TokenKind.FatArrow))
						methods.Add(new ClassMethod(mname, ps, null, ParseExpression(1), isStatic));
					else
						methods.Add(new ClassMethod(mname, ps, ParseBlock(), null, isStatic));
				}
				else if (At(TokenKind.Identifier))
				{
					var fname = Advance().Text;
					var indexParams = At(TokenKind.LBracket) ? ParseParamList(TokenKind.LBracket, TokenKind.RBracket) : new List<Param>();
					var save = _pos;
					SkipNewlines();
					if (At(TokenKind.LBrace))   // property body, possibly with the brace on the next line
						properties.Add(ParsePropertyBody(fname, indexParams, isStatic));
					else
					{
						_pos = save;   // field initializer / shorthand getter are on the same line
						if (At(TokenKind.Dot) && indexParams.Count == 0)   // member-target init: `static Template.Framework := X`
						{
							var target = ParsePostfix(new MemberExpr(new NameExpr("this"), fname, false));
							if (!Match(TokenKind.Assign)) Error($"expected ':=' in class member initializer but found {Got()}");
							var stmt = new ExpressionStmt(new AssignExpr(":=", target, ParseExpression(1)));
							(isStatic ? staticInits : instanceInits).Add(stmt);
						}
						else if (Match(TokenKind.FatArrow))
							properties.Add(new ClassProperty(fname, indexParams, null, ParseExpression(1), null, null, isStatic));
						else if (At(TokenKind.Colon))   // typed struct field `name : Type [:= init]`
						{
							Advance();
							var tn = ExpectIdentifier("struct field type");
							while (At(TokenKind.Dot)) { Advance(); tn += "." + ExpectIdentifier("struct field type"); }
							// Structured-array element type `ElementType[N]` (a fixed-size array of N elements).
							if (At(TokenKind.LBracket))
							{
								Advance();
								var len = Expect(TokenKind.Number, "struct array length").Text;
								Expect(TokenKind.RBracket, "']' after struct array length");
								tn += "[" + len + "]";
							}
							Expr tinit = Match(TokenKind.Assign) ? ParseExpression(1) : null;
							fields.Add(new ClassField(fname, tinit, isStatic, tn, structPack));
						}
						else
						{
							Expr init = Match(TokenKind.Assign) ? ParseExpression(1) : null;
							fields.Add(new ClassField(fname, init, isStatic));
						}
					}
				}
				else
				{
					Error($"unexpected token in class body: '{Current.Text}'");
				}

				if (_pos == p) Advance();
				SkipNewlines();
			}
			Expect(TokenKind.RBrace, isStruct ? "struct body" : "class body");
			return new ClassDecl(name, baseName, fields, methods, properties, nested, isStruct)
			{ Requires = classRequires, StaticInit = staticInits, InstanceInit = instanceInits };
		}

		// Property body: { get [=> expr | { ... }]  set [=> expr | { ... }] }
		private ClassProperty ParsePropertyBody(string name, List<Param> indexParams, bool isStatic)
		{
			Expect(TokenKind.LBrace, "property");
			Block getBody = null, setBody = null;
			Expr getArrow = null, setArrow = null;
			SkipNewlines();
			while (!At(TokenKind.RBrace) && !At(TokenKind.EOF))
			{
				var p = _pos;
				if (AtKeyword("get")) { Advance(); if (Match(TokenKind.FatArrow)) getArrow = ParseExpression(1); else { SkipNewlines(); getBody = ParseBlock(); } }
				else if (AtKeyword("set")) { Advance(); if (Match(TokenKind.FatArrow)) setArrow = ParseExpression(1); else { SkipNewlines(); setBody = ParseBlock(); } }
				else Error($"expected 'get' or 'set' in property but found {Got()}");
				if (_pos == p) Advance();
				SkipNewlines();
			}
			Expect(TokenKind.RBrace, "property");
			return new ClassProperty(name, indexParams, getBody, getArrow, setBody, setArrow, isStatic);
		}

		// No-parentheses command call:  Name arg1, arg2   /   obj.Method arg   /   Name , arg (omit first).
		// Detected by lookahead: the callee primary (name/member/index) is followed by whitespace + an
		// argument start, or a comma — but not by an adjacent '(' (a call expression) or an operator.
		private static readonly System.Collections.Generic.HashSet<string> verbalOps =
			new(System.StringComparer.OrdinalIgnoreCase) { "not", "and", "or", "is", "in", "contains" };

		private bool IsCommandStatement()
		{
			if (!At(TokenKind.Identifier)) return false;
			if (verbalOps.Contains(Current.Text)) return false;   // `not x`, `a and b` are operators, not commands
			var i = _pos + 1;   // past the leading identifier
			bool sawDot = false;
			while (i < _t.Count)
			{
				var t = _t[i];
				if (t.Kind == TokenKind.Dot && !t.LeadingWhitespace)
				{ sawDot = true; i++; if (i < _t.Count && _t[i].Kind == TokenKind.Identifier) i++; continue; }     // .member
				if (t.Kind == TokenKind.LBracket && !t.LeadingWhitespace)
				{ var d = 1; i++; while (i < _t.Count && d > 0) { if (_t[i].Kind == TokenKind.LBracket) d++; else if (_t[i].Kind == TokenKind.RBracket) d--; i++; } continue; }  // [index]
				break;
			}
			// A primary-expression chain (`name`, `obj.method`) ending the line is a zero-arg function-call statement
			// (`ExitApp`, `obj.Method`). An index-access end (`arr[i]`) is an expression statement, not a call (mirrors
			// the canonical isFunctionCallStatement, which rejects a trailing CloseBracket).
			if (i >= _t.Count || _t[i].Kind == TokenKind.Newline || _t[i].Kind == TokenKind.EOF || _t[i].Kind == TokenKind.RBrace)
				return _t[i - 1].Kind != TokenKind.RBracket && (sawDot || _t[i - 1].Kind == TokenKind.Identifier);
			var next = _t[i];
			if (next.Kind == TokenKind.LParen && !next.LeadingWhitespace) return false;   // Name(...) call expression
			// A command-call statement (`MsgBox "x", "y"`) requires the name/chain to be followed by whitespace (then an
			// argument or a leading-comma omitted first argument), a comment, or end-of-line. Anything ADJACENT — `,`
			// `:=` `.` `[` … — makes it an expression statement instead, so `MsgBox, "x"` and `a[i], b := 1` are
			// comma-sequences, while `MsgBox ,"x"` (space before the comma) is a call with the first arg omitted.
			if (!next.LeadingWhitespace) return false;                                     // adjacent operator/comma/postfix => expression
			switch (next.Kind)
			{
				case TokenKind.Comma:
					return _t[i - 1].Kind != TokenKind.RBracket;   // `Func ,arg` => command with an omitted first argument
				case TokenKind.Identifier:
					return !verbalOps.Contains(next.Text);   // `a and b` is a binary expression, not `a(and …)`
				case TokenKind.String: case TokenKind.Number:
				case TokenKind.Percent: case TokenKind.LBrace:
				case TokenKind.LParen:   // a SPACE before '(' is a command-style call: `OnExit (*) => f`, `MsgBox (1+1)`
					return true;
				case TokenKind.Minus: case TokenKind.Plus: case TokenKind.Not: case TokenKind.BitNot:
				case TokenKind.Star: case TokenKind.BitAnd: case TokenKind.LBracket:
					return i + 1 < _t.Count && !_t[i + 1].LeadingWhitespace;   // unary/ref (adjacent operand) => arg; spaced => binary
				default: return false;                                          // := / + / = etc. => expression statement
			}
		}

		private Stmt ParseCommandCall()
		{
			var callee = ParsePostfix(ParsePrimary());   // name / member / index — no adjacent '(' (ensured by detection)
			return new ExpressionStmt(new CallExpr(callee, ParseCommandArgs()));
		}

		// Comma-separated command arguments, ending at end of line / '}'. Supports omitted args.
		private List<Argument> ParseCommandArgs()
		{
			var args = new List<Argument>();
			while (!At(TokenKind.Newline) && !At(TokenKind.EOF) && !At(TokenKind.RBrace))
			{
				if (At(TokenKind.Comma)) { args.Add(new Argument(null, false)); Advance(); continue; }
				var ex = ParseExpression(1);
				var spread = Match(TokenKind.Star);
				args.Add(new Argument(ex, spread));
				if (!Match(TokenKind.Comma)) break;
			}
			return args;
		}

		private bool IsFunctionDefinition()
		{
			if (!At(TokenKind.Identifier)) return false;
			var lp = Peek(1);
			if (lp.Kind != TokenKind.LParen || lp.LeadingWhitespace) return false; // name( must be adjacent

			var i = _pos + 1;
			var depth = 0;
			for (; i < _t.Count; i++)
			{
				if (_t[i].Kind == TokenKind.LParen) depth++;
				else if (_t[i].Kind == TokenKind.RParen) { depth--; if (depth == 0) { i++; break; } }
			}
			while (i < _t.Count && _t[i].Kind == TokenKind.Newline) i++;
			if (i >= _t.Count) return false;
			return _t[i].Kind == TokenKind.LBrace || _t[i].Kind == TokenKind.FatArrow;
		}

		private Stmt ParseFunctionDecl(bool isStatic = false)
		{
			var name = Advance().Text;
			var ps = ParseParamList();
			SkipNewlines();
			if (Match(TokenKind.FatArrow))
				return new FunctionDecl(name, ps, null, ParseExpression(1), isStatic);
			return new FunctionDecl(name, ps, ParseBlock(), null, isStatic);
		}

		private List<Param> ParseParamList() => ParseParamList(TokenKind.LParen, TokenKind.RParen);

		private List<Param> ParseParamList(TokenKind open, TokenKind close)
		{
			Expect(open, "parameter list");
			_groupDepth++;
			var ps = new List<Param>();
			SkipNewlines();
			while (!At(close) && !At(TokenKind.EOF))
			{
				var byRef = Match(TokenKind.BitAnd);
				if (!byRef && At(TokenKind.Star))   // anonymous variadic `*` — AHK binds it to the implicit local `args`
				{
					Advance();
					ps.Add(new Param("args", null, false, true, false));
					SkipNewlines();
					if (!Match(TokenKind.Comma)) break;
					SkipNewlines();
					continue;
				}
				var name = ExpectIdentifier("parameter");
				var variadic = false; var optional = false; Expr def = null;
				if (Match(TokenKind.Star)) variadic = true;
				else if (Match(TokenKind.Question)) optional = true;
				else if (Match(TokenKind.Assign)) def = ParseExpression(1);
				ps.Add(new Param(name, def, byRef, variadic, optional));
				SkipNewlines();
				if (!Match(TokenKind.Comma)) break;
				SkipNewlines();
			}
			Expect(close, "parameter list");
			_groupDepth--;
			return ps;
		}

		// ---- expressions (Pratt) ----

		// Binding power: higher = binds tighter. Taken from the AHK v2 operator-precedence order.
		private const int PrecPower = 17;

		private readonly struct Infix
		{
			public readonly int Prec; public readonly bool Right; public readonly bool Assign; public readonly bool Concat;
			public Infix(int prec, bool right, bool assign, bool concat) { Prec = prec; Right = right; Assign = assign; Concat = concat; }
		}

		private Infix GetInfix(Token t)
		{
			switch (t.Kind)
			{
				case TokenKind.Assign:
				case TokenKind.PlusAssign: case TokenKind.MinusAssign:
				case TokenKind.StarAssign: case TokenKind.SlashAssign: case TokenKind.IntDivAssign:
				case TokenKind.PowerAssign: case TokenKind.PercentAssign: case TokenKind.DotAssign:
				case TokenKind.BitAndAssign: case TokenKind.BitOrAssign: case TokenKind.BitXorAssign:
				case TokenKind.ShiftLeftAssign: case TokenKind.ShiftRightAssign: case TokenKind.ShiftRightLogicalAssign:
				case TokenKind.NullCoalesceAssign:
					return new Infix(1, true, true, false);

				case TokenKind.NullCoalesce: return new Infix(3, true, false, false);
				case TokenKind.LogicalOr: return new Infix(4, false, false, false);
				case TokenKind.LogicalAnd: return new Infix(5, false, false, false);

				case TokenKind.Equal: case TokenKind.Identity:
				case TokenKind.NotEqual: case TokenKind.NotIdentity:
					return new Infix(7, false, false, false);

				case TokenKind.Less: case TokenKind.Greater:
				case TokenKind.LessEqual: case TokenKind.GreaterEqual:
					return new Infix(8, false, false, false);

				case TokenKind.RegexMatch: case TokenKind.NotRegexMatch:
					return new Infix(9, false, false, false);

				case TokenKind.Dot:   // any Dot reaching the binary loop is a spaced concat (adjacent dots
					return new Infix(10, false, false, true);  // are consumed as member access in ParsePostfix)

				case TokenKind.BitOr: return new Infix(11, false, false, false);
				case TokenKind.BitXor: return new Infix(12, false, false, false);
				case TokenKind.BitAnd: return new Infix(13, false, false, false);

				case TokenKind.ShiftLeft: case TokenKind.ShiftRight: case TokenKind.ShiftRightLogical:
					return new Infix(14, false, false, false);

				case TokenKind.Plus: case TokenKind.Minus: return new Infix(15, false, false, false);
				case TokenKind.Star: case TokenKind.Slash: case TokenKind.IntDiv: return new Infix(16, false, false, false);
				case TokenKind.Power: return new Infix(PrecPower, true, false, false);

				case TokenKind.Identifier:
					if (t.IsKeyword("or")) return new Infix(4, false, false, false);
					if (t.IsKeyword("and")) return new Infix(5, false, false, false);
					if (t.IsKeyword("is") || t.IsKeyword("in") || t.IsKeyword("contains"))
						return new Infix(6, false, false, false);
					return default;

				default: return default;
			}
		}

		private Expr ParseExpression(int minPrec)
		{
			// Guard against unbounded recursion from unsupported/malformed continuations (e.g. non-string `(…)`
			// continuation sections) so a bad parse fails cleanly instead of overflowing the stack.
			if (_exprDepth >= MaxExprDepth)
			{
				Error("expression nested too deeply (unterminated grouping or unsupported continuation?)");
				while (!At(TokenKind.Newline) && !At(TokenKind.EOF) && !At(TokenKind.RParen) && !At(TokenKind.RBracket) && !At(TokenKind.RBrace)) Advance();
				return new NameExpr("«error»");
			}
			_exprDepth++;
			try { return ParseExpressionCore(minPrec); }
			finally { _exprDepth--; }
		}

		private Expr ParseExpressionCore(int minPrec)
		{
			var left = ParseUnary();

			while (true)
			{
				if (_groupDepth > 0) SkipNewlines();
				var t = Current;

				// Leading-operator line continuation: `expr`<newline>`<binary-op> ...` joins the lines.
				if (t.Kind == TokenKind.Newline)
				{
					var save = _pos;
					SkipNewlines();
					if (GetInfix(Current).Prec > 0 || Current.Kind == TokenKind.Question) t = Current;   // ?: may continue a line
					else _pos = save;
				}

				// Ternary `? :` (prec 2, right-assoc) and the maybe-operator `x?`.
				if (t.Kind == TokenKind.Question)
				{
					if (2 < minPrec) break;
					Advance();   // consume '?'
					// `?` is the maybe (unset-permissive) operator when the next SIGNIFICANT token (skipping newlines;
					// whitespace/comments are already stripped) is one of ) ] } , : ? . or a statement-starting
					// keyword; otherwise it's the ternary conditional. (So `x := 1?\n2:\n3` is the ternary 1?2:3.)
					var qSave = _pos;
					SkipNewlines();
					if (At(TokenKind.RParen) || At(TokenKind.RBracket) || At(TokenKind.RBrace) || At(TokenKind.Comma)
						|| At(TokenKind.Colon) || At(TokenKind.Question) || At(TokenKind.Dot) || At(TokenKind.EOF)
						|| IsStatementKeyword())
					{
						_pos = qSave;   // leave any trailing newline in place — the maybe value ends here
						left = new UnaryExpr("?", left, true);
						continue;
					}
					var then = ParseExpression(1);
					SkipNewlines();   // the ':' arm may be on a continuation line
					Expect(TokenKind.Colon, "ternary");
					SkipNewlines();
					var els = ParseExpression(1);
					left = new TernaryExpr(left, then, els);
					continue;
				}

				// `expr*` spread (argument/array): a trailing '*' before a terminator is not multiplication.
				if (t.Kind == TokenKind.Star && IsArgTerminator(Peek(1))) break;

				var inf = GetInfix(t);
				if (inf.Prec == 0 || inf.Prec < minPrec)
				{
					// Implicit concatenation: `a b` => `a . b` (auto-concat of space-separated operands).
					if (minPrec <= 10 && t.LeadingWhitespace && IsImplicitConcatStart(t))
					{
						left = new BinaryExpr(".", left, ParseExpression(11));
						continue;
					}
					break;
				}

				Advance();
				SkipNewlines();   // a dangling operator continues onto the next line
				var rhs = ParseExpression(inf.Right ? inf.Prec : inf.Prec + 1);
				left = inf.Assign ? AttachAssign(t.Text, left, rhs)
					 : inf.Concat ? new BinaryExpr(".", left, rhs)
					 : new BinaryExpr(t.Text, left, rhs);
			}
			return left;
		}

		// AHK raises assignment precedence to avoid a syntax error / give intuitive behavior: the assignment binds to the
		// variable immediately on its left, not to a whole boolean/relational/unary expression. So `x==y && z:=1` is
		// `x==y && (z:=1)`, `not x:=y` is `not (x:=y)`, and `++Var:=X` is `++(Var:=X)`. (Ternary arms already parse this
		// way since each arm is a full expression.) Push the assignment into the apparent target's rightmost operand.
		private static Expr AttachAssign(string op, Expr target, Expr rhs) =>
			target is BinaryExpr be ? new BinaryExpr(be.Op, be.Left, AttachAssign(op, be.Right, rhs))
			: target is UnaryExpr { Postfix: false } ue ? new UnaryExpr(ue.Op, AttachAssign(op, ue.Operand, rhs), false)
			: new AssignExpr(op, target, rhs);

		// Whether a token can start the right side of an auto-concatenation (`a b`).
		private bool IsImplicitConcatStart(Token t) => t.Kind switch
		{
			TokenKind.String or TokenKind.Number or TokenKind.LParen or TokenKind.Percent => true,
			TokenKind.Identifier => !verbalOps.Contains(t.Text),
			_ => false
		};

		// A token that terminates an argument/array element, so a preceding * is a spread marker not multiplication.
		private static bool IsArgTerminator(Token t) => t.Kind switch
		{
			TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace or TokenKind.Comma
			or TokenKind.Newline or TokenKind.EOF => true,
			_ => false
		};

		private Expr ParseUnary()
		{
			SkipNewlines();
			if (IsFatArrowHead()) return ParseFatArrow();

			var t = Current;
			switch (t.Kind)
			{
				case TokenKind.Minus: case TokenKind.Plus: case TokenKind.Not: case TokenKind.BitNot:
					Advance();
					return new UnaryExpr(t.Text, ParseExpression(PrecPower), false); // power binds into the operand
				case TokenKind.PlusPlus: case TokenKind.MinusMinus:
					Advance();
					return new UnaryExpr(t.Text, ParseUnary(), false);
				case TokenKind.BitAnd:   // reference / address-of
					Advance();
					return new UnaryExpr("&", ParsePostfix(ParsePrimary()), false);
			}
			if (t.IsKeyword("not"))   // low-precedence verbal not
			{
				Advance();
				return new UnaryExpr("not", ParseExpression(6), false);
			}
			return ParsePostfix(ParsePrimary());
		}

		private Expr ParsePostfix(Expr e)
		{
			while (true)
			{
				var t = Current;
				// Leading-dot member-access continuation: `expr` ⏎ `.member` (a '.' on the next line immediately followed
				// by the member name). This is method/member access, NOT concat (`expr` ⏎ `. member`, with a space after
				// the dot, stays a concat handled by the infix loop).
				if (t.Kind == TokenKind.Newline)
				{
					var save = _pos;
					SkipNewlines();
					if (At(TokenKind.Dot) && !Peek(1).LeadingWhitespace
						&& Peek(1).Kind is TokenKind.Identifier or TokenKind.Number or TokenKind.Percent)
					{ Advance(); e = MakeMember(e, false); continue; }
					_pos = save;
					break;
				}
				if (t.Kind == TokenKind.Dot && !t.LeadingWhitespace)
				{ Advance(); e = MakeMember(e, false); continue; }
				if (t.Kind == TokenKind.QuestionDot)
				{
					Advance();
					// `obj?.[i]` optional element access and `obj?.()` optional call — the `?.` guards the access
					// that follows, rather than a member name.
					if (At(TokenKind.LBracket))
					{ e = new IndexExpr(e, ParseArgs(TokenKind.LBracket, TokenKind.RBracket), nullConditional: true); continue; }
					if (At(TokenKind.LParen))
					{ e = new CallExpr(e, ParseArgs(TokenKind.LParen, TokenKind.RParen), nullConditional: true); continue; }
					e = MakeMember(e, true); continue;
				}
				if (t.Kind == TokenKind.LParen && !t.LeadingWhitespace)
				{ e = new CallExpr(e, ParseArgs(TokenKind.LParen, TokenKind.RParen)); continue; }
				if (t.Kind == TokenKind.LBracket && !t.LeadingWhitespace)
				{ e = new IndexExpr(e, ParseArgs(TokenKind.LBracket, TokenKind.RBracket)); continue; }
				if ((t.Kind == TokenKind.PlusPlus || t.Kind == TokenKind.MinusMinus) && !t.LeadingWhitespace)
				{ Advance(); e = new UnaryExpr(t.Text, e, true); continue; }
				break;
			}
			return e;
		}

		private Expr ParsePrimary()
		{
			SkipNewlines();
			var t = Current;
			switch (t.Kind)
			{
				case TokenKind.Number: Advance(); return new LiteralExpr(LiteralKind.Number, t.Text);
				case TokenKind.String: Advance(); return new LiteralExpr(LiteralKind.String, t.Text);
				case TokenKind.Identifier:
					Advance();
					// `pre%mid%…` with no intervening whitespace is dynamic name-building, not a plain variable —
					// but a '%' that closes the enclosing %…% is a delimiter, not the start of a new name.
					if (!_inDerefInner && !Current.LeadingWhitespace && At(TokenKind.Percent))
						return ParseNameBuild(new LiteralExpr(LiteralKind.String, "\"" + t.Text + "\""));
					return new NameExpr(t.Text, t.Line);
				case TokenKind.LParen:
					Advance(); _groupDepth++;
					var inner = ParseExpression(1);
					if (At(TokenKind.Comma))   // parenthesized sequence `(a, b)`
					{
						var items = new List<Expr> { inner };
						while (Match(TokenKind.Comma)) { SkipNewlines(); items.Add(ParseExpression(1)); }
						inner = new SequenceExpr(items);
					}
					_groupDepth--;
					Expect(TokenKind.RParen, "parenthesized expression");
					return inner is SequenceExpr ? inner : new GroupExpr(inner);
				case TokenKind.LBracket:
					return ParseArrayOrMap();
				case TokenKind.LBrace:
					return ParseObjectLiteral();
				case TokenKind.Percent:   // %expr% dynamic dereference (or start of name-building)
					Advance();
					var savedDeref = _inDerefInner; _inDerefInner = true;
					var nameExpr = ParseExpression(1);
					_inDerefInner = savedDeref;
					Expect(TokenKind.Percent, "dereference");
					if (!Current.LeadingWhitespace && (At(TokenKind.Identifier) || At(TokenKind.Number) || At(TokenKind.Percent)))
						return ParseNameBuild(nameExpr);
					return new DerefExpr(nameExpr);
				default:
					Error($"unexpected {Got()}");
					// Resync to an expression/statement boundary so one bad token can't cascade via auto-concat.
					Advance();
					while (!At(TokenKind.Newline) && !At(TokenKind.EOF) && !At(TokenKind.RParen)
						&& !At(TokenKind.RBracket) && !At(TokenKind.RBrace) && !At(TokenKind.Comma)) Advance();
					return new NameExpr("«error»");
			}
		}

		// Dynamic name-building: a run of adjacent identifier/number/%expr% parts forms a variable name at
		// runtime (`z%y%` -> deref of "z" . y). Lowered as a DerefExpr over the concatenation of the parts.
		private Expr ParseNameBuild(Expr firstPart)
		{
			var parts = new List<Expr> { firstPart };
			while (!Current.LeadingWhitespace)
			{
				if (At(TokenKind.Identifier) || At(TokenKind.Number))
					parts.Add(new LiteralExpr(LiteralKind.String, "\"" + Advance().Text + "\""));
				else if (At(TokenKind.Percent))
				{
					Advance();
					var savedDeref = _inDerefInner; _inDerefInner = true;
					var e = ParseExpression(1);
					_inDerefInner = savedDeref;
					Expect(TokenKind.Percent, "dereference");
					parts.Add(e);
				}
				else break;
			}
			Expr name = parts[0];
			for (int i = 1; i < parts.Count; i++) name = new BinaryExpr(".", name, parts[i]);
			return new DerefExpr(name);
		}

		private List<Argument> ParseArgs(TokenKind open, TokenKind close)
		{
			Expect(open, "argument list");
			_groupDepth++;
			var args = new List<Argument>();
			SkipNewlines();
			// Comma-separated slots: each slot is an expression or omitted (empty). A TRAILING comma is ignored (it does
			// not add a slot), so `f(a,)`/`f(x,&y,)` keep their arg count and `f()` is zero, while `f(,a)`/`f(a,,b)` keep
			// the leading/interior omitted slots and `f(,)` is a single omitted arg.
			if (!At(close) && !At(TokenKind.EOF))
			{
				while (true)
				{
					if (At(TokenKind.Comma) || At(close) || At(TokenKind.EOF))
						args.Add(new Argument(null, false));   // omitted slot
					else
					{
						var ex = ParseExpression(1);
						args.Add(new Argument(ex, Match(TokenKind.Star)));
					}
					SkipNewlines();
					if (!At(TokenKind.Comma)) break;
					Advance(); SkipNewlines();
					if (At(close)) break;   // trailing comma: no extra slot
				}
			}
			Expect(close, "argument list");
			_groupDepth--;
			return args;
		}

		// `[ … ]` is an array literal, or a map-creation literal `[k1: v1, k2: v2]` when a top-level `:` (not a ternary
		// colon) appears before the first top-level `,`/`]`.
		private Expr ParseArrayOrMap() => IsMapLiteral() ? ParseMap() : new ArrayExpr(ParseArgs(TokenKind.LBracket, TokenKind.RBracket));

		// Scans the bracketed group (from the '[') for a top-level ':' that is not consumed by a ternary '?'.
		private bool IsMapLiteral()
		{
			int i = _pos + 1, depth = 0, ternary = 0;
			while (i < _t.Count)
			{
				switch (_t[i].Kind)
				{
					case TokenKind.LParen: case TokenKind.LBracket: case TokenKind.LBrace: depth++; break;
					case TokenKind.RParen: case TokenKind.RBrace: depth--; break;
					case TokenKind.RBracket: if (depth == 0) return false; depth--; break;
					case TokenKind.Question: if (depth == 0) ternary++; break;
					case TokenKind.Colon:
						if (depth == 0) { if (ternary > 0) ternary--; else return true; }
						break;
					case TokenKind.Comma: if (depth == 0) return false; break;
				}
				i++;
			}
			return false;
		}

		private Expr ParseMap()
		{
			Expect(TokenKind.LBracket, "map literal");
			_groupDepth++;
			var entries = new List<(Expr, Expr)>();
			SkipNewlines();
			while (!At(TokenKind.RBracket) && !At(TokenKind.EOF))
			{
				if (At(TokenKind.Comma)) { Advance(); SkipNewlines(); continue; }
				var key = ParseExpression(1);
				SkipNewlines();
				Expect(TokenKind.Colon, "map literal");
				SkipNewlines();
				entries.Add((key, ParseExpression(1)));
				SkipNewlines();
				if (!Match(TokenKind.Comma)) break;
				SkipNewlines();
			}
			Expect(TokenKind.RBracket, "map literal");
			_groupDepth--;
			return new MapExpr(entries);
		}

		// Object literal { name: value, "str": value, ... }. Only reached in expression position
		// (statement-position '{' is parsed as a block), so no lexer disambiguation is needed.
		private Expr ParseObjectLiteral()
		{
			Expect(TokenKind.LBrace, "object literal");
			_groupDepth++;
			var entries = new List<ObjectEntry>();
			SkipNewlines();
			while (!At(TokenKind.RBrace) && !At(TokenKind.EOF))
			{
				if (At(TokenKind.Hash))   // a directive (e.g. #import) on its own line inside the literal — hoist it out
				{
					_hoistedStmts.Add(ParseDirective());
					SkipNewlines();
					Match(TokenKind.Comma);
					SkipNewlines();
					continue;
				}
				Expr key = At(TokenKind.Identifier) ? new NameExpr(Advance().Text)
					: At(TokenKind.String) ? new LiteralExpr(LiteralKind.String, Advance().Text)
					: ParsePrimary();
				SkipNewlines();   // inside `{ }` the key, ':' and value may each be on their own line
				Expect(TokenKind.Colon, "object literal");
				SkipNewlines();
				entries.Add(new ObjectEntry(key, ParseExpression(1)));
				SkipNewlines();
				if (!Match(TokenKind.Comma)) break;
				SkipNewlines();
			}
			Expect(TokenKind.RBrace, "object literal");
			_groupDepth--;
			return new ObjectExpr(entries);
		}

		// ---- fat-arrow lambdas ----

		private bool IsFatArrowHead()
		{
			if (At(TokenKind.Identifier) && Peek(1).Kind == TokenKind.FatArrow) return true;   // a => …
			// named arrow/block fn `name(params) => …` or `name(params) { … }` (name adjacent to its '(')
			if (At(TokenKind.Identifier) && Peek(1).Kind == TokenKind.LParen && !Peek(1).LeadingWhitespace)
				return ParenMatchFollowedBy(TokenKind.FatArrow, _pos + 1) || (!_inFlowCond && ParenMatchFollowedBy(TokenKind.LBrace, _pos + 1));
			// anonymous `(params) => …`, or `(params) { … }` (the block form is ambiguous with a control-flow body)
			if (At(TokenKind.LParen))
				return ParenMatchFollowedBy(TokenKind.FatArrow, _pos) || (!_inFlowCond && ParenMatchFollowedBy(TokenKind.LBrace, _pos));
			return false;
		}

		private bool ParenMatchFollowedBy(TokenKind kind, int i)
		{
			// i is at '('
			var depth = 0;
			for (; i < _t.Count; i++)
			{
				var k = _t[i].Kind;
				if (k == TokenKind.LParen) depth++;
				else if (k == TokenKind.RParen) { depth--; if (depth == 0) { i++; break; } }
				else if (k == TokenKind.EOF) return false;
			}
			while (i < _t.Count && _t[i].Kind == TokenKind.Newline) i++;
			return i < _t.Count && _t[i].Kind == kind;
		}

		private Expr ParseFatArrow()
		{
			// Optional name for a named fn `name(params) => …` / `name(params) { … }`: captured so the body can recurse
			// by that name (resolved to the lambda itself in the lowerer).
			string faName = null;
			if (At(TokenKind.Identifier) && Peek(1).Kind == TokenKind.LParen && !Peek(1).LeadingWhitespace)
				faName = Advance().Text;
			List<Param> ps;
			if (At(TokenKind.Identifier))
				ps = new List<Param> { new Param(Advance().Text, null, false, false, false) };
			else
				ps = ParseParamList();
			if (At(TokenKind.LBrace))   // anonymous block-bodied function `(params) { … }`
				return new FatArrowExpr(ps, ParseBlock()) { Name = faName };
			Expect(TokenKind.FatArrow, "fat-arrow function");
			return new FatArrowExpr(ps, ParseExpression(1)) { Name = faName };
		}
	}
}
