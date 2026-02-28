using static Keysharp.Scripting.Parser;
using Antlr4.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Keysharp.Scripting
{
	internal class PreReader
	{
		private enum DelimiterKind
		{
			BraceBlock,
			BraceObjectLiteral,
			Paren,
			Bracket,
			Deref
		}

		private readonly struct DelimiterFrame
		{
			internal DelimiterFrame(DelimiterKind kind, IToken token)
			{
				Kind = kind;
				Token = token;
			}

			internal DelimiterKind Kind { get; }
			internal IToken Token { get; }
		}

		private static readonly char[] libBrackets = ['<', '>'];

		private readonly Dictionary<string, HashSet<string>> includesByModule = new(StringComparer.OrdinalIgnoreCase);
		private readonly Queue<string> pendingImports = new();
		private readonly HashSet<string> loadedImportModules = new(StringComparer.OrdinalIgnoreCase);
		private readonly Parser parser;
		private sealed class TokenScanState
		{
			internal string ModuleName;
			internal string IncludePath;
			internal IToken PrevToken;
			internal IToken PrevVisibleToken;
			internal Stack<DelimiterFrame> DelimiterStack;
			internal int[] DelimiterDepths;
			internal bool SkipWhitespace;
			internal bool SkipLinebreak;
			internal int PendingPrefixedStatementCount;
			internal int LineStartIndex;

			internal TokenScanState(string moduleName)
			{
				ModuleName = moduleName;
				IncludePath = "./";
				PrevToken = null;
				PrevVisibleToken = null;
				DelimiterStack = new Stack<DelimiterFrame>();
				DelimiterDepths = new int[5];
				SkipWhitespace = false;
				SkipLinebreak = false;
				PendingPrefixedStatementCount = 0;
				LineStartIndex = 0;
			}
		}

		internal List<(string, bool)> PreloadedDlls { get; } = [];
		
		internal eScriptInstance SingleInstance { get; private set; } = eScriptInstance.Prompt;

		internal PreReader(Parser p) => parser = p;

        internal string[,] accessorReplaceTemplate = new[,]//These will need to be done differently on linux.//LINUXTODO
{
				// The order of the first three must not be changed without altering the order in ReadTokens
                { "%A_LineFile%", "" },
                { "%A_ScriptDir%", "" },
				{ "%A_ScriptFullPath%", "" },
                { "%A_AhkPath%", Accessors.A_AhkPath },
                { "%A_AppData%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) },
                { "%A_AppDataCommon%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) },
                { "%A_ComputerName%", Accessors.A_ComputerName },
#if WINDOWS
				{ "%A_ComSpec%", Accessors.A_ComSpec },
#endif
				{ "%A_Desktop%", Accessors.A_Desktop },
                { "%A_DesktopCommon%", Accessors.A_DesktopCommon },
                { "%A_IsCompiled%", Accessors.A_IsCompiled.ToString() },
                { "%A_KeysharpPath%", Accessors.A_KeysharpPath },
                { "%A_MyDocuments%", Accessors.A_MyDocuments },
                { "%A_ProgramFiles%", Accessors.A_ProgramFiles },
                { "%A_Programs%", Accessors.A_Programs },
                { "%A_ProgramsCommon%", Accessors.A_ProgramsCommon },

                { "%A_ScriptName%", Accessors.A_ScriptName },
                { "%A_Space%", Accessors.A_Space },
                { "%A_StartMenu%", Accessors.A_StartMenu },
                { "%A_StartMenuCommon%", Accessors.A_StartMenuCommon },
                { "%A_Startup%", Accessors.A_Startup },
                { "%A_StartupCommon%", Accessors.A_StartupCommon },
                { "%A_Tab%", Accessors.A_Tab },
                { "%A_Temp%", Accessors.A_Temp },
                { "%A_UserName%", Accessors.A_UserName },
#if WINDOWS
				{ "%A_WinDir%", Accessors.A_WinDir },
#endif
			};

		internal ModuleTokenResult ReadScriptTokens(TextReader source, string name)
		{
			var result = new ModuleTokenResult();
			var state = CreateTokenScanState(Keywords.MainModuleName);
			includesByModule.Clear();
			pendingImports.Clear();
			loadedImportModules.Clear();

            if (Env.FindCommandLineArgVal("include") is string cmdinc)
            {
                if (File.Exists(cmdinc))
                {
					var includeSet = GetIncludesForModule(state.ModuleName);
					if (includeSet.Add(cmdinc))
                    {
                        using var reader = new StreamReader(cmdinc);
                        ReadTokens(reader, cmdinc, result, state);
                    }
                }
                else
                    throw new ParseException($"Command line include file {cmdinc} specified with -/include not found.", 0, "");
            }

            ReadTokens(source, name, result, state);
			ThrowIfUnclosedDelimiter(state);

			ResolvePendingImports(result);

			foreach (var tokens in result.TokensByModule.Values)
				TrimLeadingWhitespace(tokens);

			return result;
		}

		private void ReadTokens(TextReader source, string name, ModuleTokenResult result, TokenScanState state)
		{
			ArgumentNullException.ThrowIfNull(state);

			var replace = (string[,])accessorReplaceTemplate.Clone();
			replace[0, 1] = name; //Note that Name, with a capital N, is the initial script file, not any of the included files.
			replace[1, 1] = Path.GetDirectoryName(parser.name);
			replace[2, 1] = parser.name;

            state.IncludePath = ResolveInitialIncludePath(name, state.IncludePath);

			String codeText = source.ReadToEnd() + "\n";

            var inputStream = new RewritableInputStream(codeText)
            {
				name = name
            };
			MainLexer preprocessorLexer = new(inputStream);

            var codeTokens = GetTokensForModule(result, state.ModuleName);

            var tokens = preprocessorLexer.GetAllTokens();
            var directiveTokens = new List<IToken>();
            var directiveTokenSource = new ListTokenSource(directiveTokens);
            var directiveTokenStream = new CommonTokenStream(directiveTokenSource, MainLexer.DIRECTIVE);
			PreprocessorParser preprocessorParser = new(directiveTokenStream);

            int index = 0;
            bool compiledTokens = true;
			int pendingImportCodeTokenIndex = -1;
			int tokenCount = tokens.Count;

			while (index < tokenCount && tokens[index].Type == MainLexer.WS)
                index++;
            while (index < tokenCount)
            {
                var token = tokens[index];

                if (token.Channel == MainLexer.ERROR)
                {
                    if (token.Text == "\"" || token.Text == "'")
						throw new ParseException($"Unterminated string literal", token);
					throw new ParseException($"Unexpected token '{token.Text}'", token);
                }

                if (token.Type == MainLexer.Hashtag && (index + 1) < tokenCount && tokens[index + 1].Channel != Lexer.DefaultTokenChannel)
                {
                    directiveTokens.Clear();
                    int directiveTokenIndex = index + 1;
                    // Collect all preprocessor directive tokens.
                    while (directiveTokenIndex < tokenCount &&
                        tokens[directiveTokenIndex].Type != MainLexer.Eof &&
                        tokens[directiveTokenIndex].Type != MainLexer.DirectiveNewline &&
                        tokens[directiveTokenIndex].Type != MainLexer.Hashtag)
                    {
                        if (tokens[directiveTokenIndex].Channel != Lexer.Hidden)
                        {
                            directiveTokens.Add(tokens[directiveTokenIndex]);
                        }
                        directiveTokenIndex++;
                    }

					if (directiveTokens.Count == 0)
						throw new ParseException("Empty directive token", token);

                    directiveTokenSource = new ListTokenSource(directiveTokens);
                    directiveTokenStream = new CommonTokenStream(directiveTokenSource, MainLexer.DIRECTIVE);
                    preprocessorParser.TokenStream = directiveTokenStream;

                    //preprocessorParser.SetInputStream(directiveTokenStream);
                    preprocessorParser.Reset();

                    // Parse condition in preprocessor directive (based on CSharpPreprocessorParser.g4 grammar).
                    PreprocessorParser.Preprocessor_directiveContext directive = preprocessorParser.preprocessor_directive();
                    // if true than next code is valid and not ignored.
                    compiledTokens = directive.value;

                    String directiveStr = directiveTokens[0].Text.Trim().ToUpper();
                    var lineNumber = token.Line;

                    var includeOnce = false;

					index = directiveTokenIndex;

					switch (directiveStr)
					{
                        case "IF":
                        case "ELIF":
                        case "ELSE":
                        case "REGION":
                        case "NULLABLE":
                            // Leave compiledTokens untouched such that it affects whether the following
							// non-directive tokens will be interpreted or not.
                            break;
                        case "DEFINE":
                            // add to the conditional symbols 
                            preprocessorParser.ConditionalSymbols.Add(directiveTokens[1].Text);
                            compiledTokens = true;
                            break;
                        case "UNDEF":
                            preprocessorParser.ConditionalSymbols.Remove(directiveTokens[1].Text);
                            compiledTokens = true;
                            break;
                        default:
							compiledTokens = true;
							break;
                    }

					// Process some AHK directives which are easier to handle here rather than PreprocessorParserBase.cs
                    switch (directiveStr)
					{
						case "MODULE":
                            {
								if (state.DelimiterStack.Count > 0)
									throw new ParseException("#Module cannot be used inside a block, parentheses, brackets, or deref.", token.Line, "#" + directiveTokens[0].Text, token.TokenSource.SourceName);

								string moduleName = null;
								if (directiveTokens.Count > 1)
									moduleName = directiveTokens[1].Text.Trim().Trim('"', '\'');
								if (string.IsNullOrWhiteSpace(moduleName))
									moduleName = Keywords.MainModuleName;

								state.ModuleName = moduleName;
								codeTokens = GetTokensForModule(result, state.ModuleName);
							}
                            break;
                        case "DLLLOAD":
                            {
                                var p1 = directiveTokens[1].Text;
                                var silent = false;
                                p1 = p1.Trim('"');//Quotes throw off the system file/path functions, so remove them.

                                if (p1.Length > 3 && p1.StartsWith("*i ", StringComparison.OrdinalIgnoreCase))
                                {
									p1 = p1[3..];
                                    silent = true;
                                }

                                for (var ii = 0; ii < replace.Length / 2; ii++)
                                    p1 = p1.Replace(replace[ii, 0], replace[ii, 1]);

                                PreloadedDlls.Add((p1, silent));
                                //The generated code for this is handled in Parser.Parse() because it must come before the InitGlobalVars();
                            }
                            break;
                        case "INCLUDE":
                            includeOnce = true;

                            goto case "INCLUDEAGAIN";

                        case "INCLUDEAGAIN":
                            {
								var p1 = directiveTokens[1].Text;
                                var silent = false;
                                var isLib = false;
                                p1 = p1.RemoveAll("\"");//Quotes throw off the system file/path functions, so remove them.

                                if (p1.StartsWith('<') && p1.EndsWith('>'))
                                {
                                    p1 = p1.Trim(libBrackets).Split('_', StringSplitOptions.None)[0];

											if (!p1.EndsWith(".ahk", StringComparison.OrdinalIgnoreCase))
												p1 += ".ahk";

                                    isLib = true;
                                }

                                if (p1.StartsWith("*i ", StringComparison.OrdinalIgnoreCase))
                                {
									p1 = p1[3..];
                                    silent = true;
                                }
                                else if (p1.StartsWith("*i", StringComparison.OrdinalIgnoreCase))
                                {
									p1 = p1[2..];
                                    silent = true;
                                }

                                if (isLib)
                                {
                                    var paths = new List<string>(6);

                                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                                    {
                                        paths.Add($"{state.IncludePath}\\{p1}");//Folder relative to the script file, or as overridden.
                                        paths.Add($"{Accessors.A_MyDocuments}\\AutoHotkey\\{LibDir}\\{p1}");//User library.
                                        paths.Add($"{Accessors.A_KeysharpPath}\\{LibDir}\\{p1}");//Executable folder, standard library.
                                    }
                                    else if (Path.DirectorySeparatorChar == '/' && Environment.OSVersion.Platform == PlatformID.Unix)
                                    {
                                        paths.Add($"{state.IncludePath}/{p1}");
                                        paths.Add(Path.Combine(Path.Combine(Environment.GetEnvironmentVariable("HOME"), "/AutoHotkey"), p1));
                                        paths.Add($"{Accessors.A_KeysharpPath}/{LibDir}/{p1}");//Three ways to get the possible executable folder.
                                        paths.Add($"/usr/{LibDir}/AutoHotkey/{LibDir}/{p1}");
                                        paths.Add($"/usr/local/{LibDir}/AutoHotkey/{LibDir}/{p1}");
                                    }

                                    var found = false;

                                    foreach (var dir in paths)
                                    {
                                    if (File.Exists(dir))
                                    {
                                        found = true;

										var includeSet = GetIncludesForModule(state.ModuleName);
                                        if (includeOnce && includeSet.Contains(dir))
                                            break;

										includeSet.Add(dir);
                                        using var dirReader = new StreamReader(dir);
										var prevIncludePath = state.IncludePath;
                                        ReadTokens(dirReader, dir, result, state);
										state.IncludePath = prevIncludePath;
										codeTokens = GetTokensForModule(result, state.ModuleName);
                                        break;
                                    }
                                }

                                    if (!found && !silent)
                                        throw new ParseException($"Include file {p1} not found at any of the locations: {string.Join(Environment.NewLine, paths)}", token.Line, '#' + directiveTokens[0].Text + ' ' + directiveTokens[1].Text, token.TokenSource.SourceName);
                                }
                                else
                                {
                                    for (var ii = 0; ii < replace.Length / 2; ii++)
                                        p1 = p1.Replace(replace[ii, 0], replace[ii, 1]);

                                    var path = p1;

                                    if (!Path.IsPathRooted(path) && Directory.Exists(state.IncludePath))
                                        path = Path.Combine(state.IncludePath, path);
                                    else if (!Path.IsPathRooted(path))
                                        path = Path.Combine(Path.GetDirectoryName(name), path);

                                    path = Path.GetFullPath(path);

                                    if (Directory.Exists(path))
                                    {
                                        state.IncludePath = path;
                                    }
                                    else if (File.Exists(path))
                                    {
										var includeSet = GetIncludesForModule(state.ModuleName);
                                        if (includeOnce && includeSet.Contains(path))
                                            break;

										includeSet.Add(path);
                                        using var pathReader = new StreamReader(path);
										var prevIncludePath = state.IncludePath;
                                        ReadTokens(pathReader, path, result, state);
										state.IncludePath = prevIncludePath;
										codeTokens = GetTokensForModule(result, state.ModuleName);
                                    }
                                    else
                                    {
                                        if (!silent)
                                        {
                                            var messageTokenText = token.Text
                                                + ((index + 1) < tokenCount ? tokens[index + 1].Text : string.Empty)
                                                + ((index + 2) < tokenCount ? tokens[index + 2].Text : string.Empty);
                                            throw new ParseException($"Include file {p1} not found at location {path}", token.Line, messageTokenText);
                                        }

                                        break;
                                    }
                                }
                            }
                            break;
                        case "REQUIRES":
                        {
                            var p1 = directiveTokens[1].Text;
                            var reqAhk = p1.StartsWith("AutoHotkey", StringComparison.OrdinalIgnoreCase);

                            if (reqAhk || p1.StartsWith("Keysharp", StringComparison.OrdinalIgnoreCase))
                            {
                                var splits = p1.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                                if (splits.Length > 1)
                                {
                                    var ver = splits[1].Trim().TrimStart('v', 'V');
									
									Script.VerifyVersion(ver, reqAhk, lineNumber, name);

									//In addition to being checked here, it must be added to the code for when it runs as a compiled exe.
									parser.mainFuncInitial.Add(
                                        SyntaxFactory.ExpressionStatement(
		                                    SyntaxFactory.InvocationExpression(
                                                CreateMemberAccess("Keysharp.Scripting.Script", "VerifyVersion"),
		                                    // (ver, reqAhk, 0, name)
			                                    Parser.CreateArgumentList(
				                                    SyntaxFactory.LiteralExpression(
					                                    SyntaxKind.StringLiteralExpression,
					                                    SyntaxFactory.Literal(ver)
				                                    ),
													SyntaxFactory.LiteralExpression(reqAhk ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
				                                    SyntaxFactory.LiteralExpression(
					                                    SyntaxKind.NumericLiteralExpression,
					                                    SyntaxFactory.Literal(lineNumber)
				                                    ),
													SyntaxFactory.LiteralExpression(
														SyntaxKind.StringLiteralExpression,
														SyntaxFactory.Literal("")
													)
												)
		                                    )
                                        )
	                                );
                                    //Sub release designators such as "-alpha", "-beta" are not supported in C#. Only the assembly version is supported.
								}
                            }
							break;
                        }
						case "SINGLEINSTANCE":
							SingleInstance = (directiveTokens.Count > 1 ? directiveTokens[1].Text : "FORCE").ToUpperInvariant() switch
							{
								"FORCE" => eScriptInstance.Force,
								"IGNORE" => eScriptInstance.Ignore,
								"PROMPT" => eScriptInstance.Prompt,
								"OFF" => eScriptInstance.Off,
								_ => throw new ParseException("Unrecognized directive option", directiveTokens[1]),
							};
							break;
                        case "NODYNAMICVARS":
                            parser.DynamicVars = false;
                            break;

                        case "PERSISTENT":
							var nextTokenText = (directiveTokens.Count > 1 ? directiveTokens[1].Text : "1").ToLowerInvariant().Trim();
                            parser.persistent = !(nextTokenText == "false" || nextTokenText == "0");
                            break;

                        case "ERRORSTDOUT":
							parser.errorStdOut = true;
							break;
						case "HOOKMUTEXNAME":
							if (directiveTokens.Count < 2)
								throw new ParseException($"Directive #{directiveStr} requires a parameter.", token.Line, "#" + directiveTokens[0].Text, token.TokenSource.SourceName);
                            parser.hookMutexName = directiveTokens[1].Text.Trim();
                            break;
						case "CLIPBOARDTIMEOUT":
                        case "HOTIFTIMEOUT":
                        case "MAXTHREADS":
                        case "MAXTHREADSBUFFER":
                        case "MAXTHREADSPERHOTKEY":
                        case "ASSEMBLYTITLE":
                        case "ASSEMBLYDESCRIPTION":
                        case "ASSEMBLYCONFIGURATION":
                        case "ASSEMBLYCOMPANY":
                        case "ASSEMBLYPRODUCT":
                        case "ASSEMBLYCOPYRIGHT":
                        case "ASSEMBLYTRADEMARK":
						case "ASSEMBLYVERSION":
							if (directiveTokens.Count < 2)
								throw new ParseException($"Directive #{directiveStr} requires a parameter.", token.Line, "#" + directiveTokens[0].Text, token.TokenSource.SourceName);
							parser.generalDirectives[directiveStr] = directiveTokens[1].Text.Trim();
                            break;
                        case "WINACTIVATEFORCE":
                        case "NOTRAYICON":
							parser.generalDirectives[directiveStr] = "1";
							break;
                        case "WARN":
                            Console.WriteLine("Not implemented");
                            break;
                    }
					goto OnlyIncrementIndex;
                }
                else if (token.Channel == Lexer.DefaultTokenChannel && compiledTokens)
                {
					if (token.Type == MainLexer.WS && state.SkipWhitespace)
						goto OnlyIncrementIndex;
					else if (token.Type == MainLexer.EOL && state.SkipLinebreak)
					{
						// If the line started with "import" then maybe parse it as an import statement, otherwise converts to an identifier
						if (pendingImportCodeTokenIndex >= 0)
							FinalizePendingImportCandidate(token);
						goto OnlyIncrementIndex;
					}
					else
					{
						// If a question mark is followed by any of these tokens, then it's a maybe operator instead of a ternary operator.
						if (state.PrevVisibleToken?.Type == MainLexer.QuestionMark &&
							token.Type is MainLexer.CloseParen
							or MainLexer.CloseBrace
							or MainLexer.CloseBracket
							or MainLexer.Comma
							or MainLexer.Colon)
							(state.PrevVisibleToken as CommonToken)?.Type = MainLexer.Maybe;

						if (state.SkipWhitespace)
							state.SkipWhitespace = false;
						if (state.SkipLinebreak)
							state.SkipLinebreak = false;
					}

                    int i;
					var atStatementStart = IsStatementStart();

					// Consume one pending prefix (try/else/finally) once the next statement starts.
					if (state.PendingPrefixedStatementCount > 0 && atStatementStart)
						state.PendingPrefixedStatementCount--;

					switch (token.Type)
                    {
						case MainLexer.OpenBracket:
							PushDelimiter(DelimiterKind.Bracket, token);
							state.SkipWhitespace = state.SkipLinebreak = true;
							break;
						case MainLexer.CloseBracket:
							PopDelimiterOrThrow(token);
							PopWhitespaces(codeTokens.Count);
							break;
						case MainLexer.DerefStart:
							PushDelimiter(DelimiterKind.Deref, token);
							state.SkipWhitespace = state.SkipLinebreak = true;
							break;
						case MainLexer.DerefEnd:
							PopDelimiterOrThrow(token);
							PopWhitespaces(codeTokens.Count);
							break;
                        case MainLexer.OpenParen:
							state.SkipWhitespace = state.SkipLinebreak = true;
							PushDelimiter(DelimiterKind.Paren, token);
							break;
						case MainLexer.CloseParen:
							PopDelimiterOrThrow(token);
							PopWhitespaces(codeTokens.Count);
							break;
						case MainLexer.OpenBrace:
							if (state.PrevVisibleToken?.Type == MainLexer.CloseParen)
								PopWhitespaces(codeTokens.Count);
							if (IsOpenBraceObjectLiteral())
							{
								PushDelimiter(DelimiterKind.BraceObjectLiteral, token);
								(token as CommonToken)?.Type = MainLexer.ObjectLiteralStart;
								state.SkipWhitespace = state.SkipLinebreak = true;
							}
							else
								PushDelimiter(DelimiterKind.BraceBlock, token);
							break;
						case MainLexer.CloseBrace:
							var poppedDelimiterKind = PopDelimiterOrThrow(token);
							if (poppedDelimiterKind == DelimiterKind.BraceObjectLiteral)
								(token as CommonToken)?.Type = MainLexer.ObjectLiteralEnd;
							i = PopWhitespaces(codeTokens.Count, false);

							// Ensure that the CloseBrace is both directly preceded and followed by an EOL
							var eolToken = new CommonToken(MainLexer.EOL)
							{
								Line = token.Line,
								Column = token.Column
							};
							if (i >= 0 && codeTokens[i].Type != MainLexer.EOL)
								codeTokens.Add(eolToken);
							codeTokens.Add(token);
							i = index;
							bool eolPresent = false;
							while (++i < tokens.Count)
							{
								if (tokens[i].Channel != Lexer.DefaultTokenChannel)
									continue;
								else if (tokens[i].Type == MainLexer.WS)
									continue;
								else if (tokens[i].Type == MainLexer.EOL)
									eolPresent = true;
								break;
							}
							if (!eolPresent)
								codeTokens.Add(eolToken);
							state.SkipWhitespace = true;
							goto SkipAdd;
						case MainLexer.Class:
							// Do not allow "class" followed by an identifier separated by a whitespace.
							// This is because `class Identifier EOL {}` is ambiguous with `class Identifier` followed
							// by block statement and SLL may do large lookaheads.
							if (state.LineStartIndex == codeTokens.Count
								&& (index + 2) < tokenCount
								&& tokens[index + 1].Type == MainLexer.WS
								&& tokens[index + 2].Channel == Lexer.DefaultTokenChannel
								&& MainLexerBase.IsIdentifierToken(tokens[index + 2].Type))
								index++; // Skips next WS
							break;
						case MainLexer.Comma:
							state.SkipWhitespace = state.SkipLinebreak = true;
							if (GetDelimiterDepth(DelimiterKind.Deref) > 0 || GetDelimiterDepth(DelimiterKind.Bracket) > 0 || GetDelimiterDepth(DelimiterKind.Paren) > 0)
                                PopWhitespaces(codeTokens.Count);
                            else
                            {
								// Because function call argument list might start with comma, only pop EOLs. Example:
								// MsgBox , "Test"
								PopWhitespaces(codeTokens.Count, true, false);
                            }
                            break;
						//case MainLexer.BitAnd: // Can't be here because of VarRefs
						case MainLexer.Multiply:
							PopWhitespaces(codeTokens.Count);
							break;
                        case MainLexer.Not:
						case MainLexer.VerbalNot:
						case MainLexer.BitNot:
					    case MainLexer.Plus: // Can't pop whitespaces because of function call statement
						case MainLexer.Minus:
							state.SkipWhitespace = state.SkipLinebreak = true;
							break;
						case MainLexer.EOL:
							if (pendingImportCodeTokenIndex >= 0)
								FinalizePendingImportCandidate(token);
							PopWhitespaces(codeTokens.Count);
							state.SkipWhitespace = state.SkipLinebreak = true;
							break;
						case MainLexer.Dot:
                            int prevCount = codeTokens.Count;
                            PopWhitespaces(codeTokens.Count);
							state.SkipWhitespace = state.SkipLinebreak = true;

							if (IsNextTokenWhitespace(index) && prevCount != codeTokens.Count)
                            { // Any skipped and popped
								(token as CommonToken)?.Type = MainLexer.ConcatDot;
							}
							break;
						case MainLexer.Assign:
                        case MainLexer.Divide:
                        case MainLexer.IntegerDivide:
                        case MainLexer.Power:
                        case MainLexer.NullCoalesce:
                        case MainLexer.RightShiftArithmetic:
                        case MainLexer.LeftShiftArithmetic:
                        case MainLexer.RightShiftLogical:
                        case MainLexer.LessThan:
                        case MainLexer.MoreThan:
                        case MainLexer.LessThanEquals:
                        case MainLexer.GreaterThanEquals:
                        case MainLexer.Equals_:
                        case MainLexer.NotEquals:
                        case MainLexer.IdentityEquals:
                        case MainLexer.IdentityNotEquals:
                        case MainLexer.RegExMatch:
                        case MainLexer.BitXOr:
                        case MainLexer.BitOr:
                        case MainLexer.And:
                        case MainLexer.Or:
                        case MainLexer.VerbalAnd:
                        case MainLexer.VerbalOr:
                        case MainLexer.MultiplyAssign:
                        case MainLexer.DivideAssign:
                        case MainLexer.ModulusAssign:
                        case MainLexer.PlusAssign:
                        case MainLexer.MinusAssign:
                        case MainLexer.LeftShiftArithmeticAssign:
                        case MainLexer.RightShiftArithmeticAssign:
                        case MainLexer.RightShiftLogicalAssign:
                        case MainLexer.IntegerDivideAssign:
                        case MainLexer.ConcatenateAssign:
                        case MainLexer.BitAndAssign:
                        case MainLexer.BitXorAssign:
                        case MainLexer.BitOrAssign:
                        case MainLexer.PowerAssign:
                        case MainLexer.NullishCoalescingAssign:
                        case MainLexer.QuestionMark:
                        case MainLexer.QuestionMarkDot:
                        case MainLexer.Arrow:
							PopWhitespaces(codeTokens.Count, !IsVerbalOperator(token.Type) || (!(IsPrecededByEol() && IsFollowedByOpenParen(index))));
							state.SkipWhitespace = state.SkipLinebreak = true;
							break;
                        case MainLexer.Loop:
							if (atStatementStart)
							{
								if ((index + 2) < tokenCount && tokens[index + 1].Type == MainLexer.WS)
								{
									switch (tokens[index + 2].Type)
									{
										case MainLexer.Files:
										case MainLexer.Parse:
										case MainLexer.Read:
										case MainLexer.Reg:
											index += 2;
											codeTokens.Add(token);
											token = tokens[index];
											break;
									}
								}
								codeTokens.Add(token);
								// Allow a trailing comma after the loop statement
								if ((index + 1) < tokenCount && tokens[index + 1].Type == MainLexer.Comma)
									index++;
							}
                            goto SkipAdd;
						case MainLexer.Try:
                        case MainLexer.If:
                        case MainLexer.Catch:
                        case MainLexer.Finally:
                        case MainLexer.As:
                        case MainLexer.For:
                        case MainLexer.Switch:
                        case MainLexer.Case:
                        case MainLexer.Default:
                        case MainLexer.While:
                        case MainLexer.Until:
                        case MainLexer.Else:
                        case MainLexer.Goto:
                        case MainLexer.Async:
                        case MainLexer.Static:
							if (atStatementStart && (token.Type is MainLexer.Try or MainLexer.Else or MainLexer.Finally))
							{
								state.PendingPrefixedStatementCount++;
								// try/else/finally are followed by s* statement in grammar.
								state.SkipWhitespace = state.SkipLinebreak = true;
							}
                            codeTokens.Add(token);
                            goto SkipAdd;
						case MainLexer.Throw:
						case MainLexer.Delete:
						case MainLexer.Import:
						case MainLexer.Export:
						case MainLexer.Await:
						case MainLexer.Yield:
							if (state.LineStartIndex == codeTokens.Count) // Statement start?
							{
								if ((index + 1) < tokenCount && tokens[index + 1].Type == MainLexer.WS)
									index++;
								if (token.Type == MainLexer.Import && pendingImportCodeTokenIndex < 0)
									pendingImportCodeTokenIndex = codeTokens.Count;
							}
							break;
						case MainLexer.HotIf:
						case MainLexer.InputLevel:
						case MainLexer.UseHook:
						case MainLexer.SuspendExempt:
							state.SkipWhitespace = true;
							break;
                        case MainLexer.WS:
                            break;
						default:
                            break;
                    }
                    codeTokens.Add(token); // Collect code tokens.

				SkipAdd:
					if (codeTokens.Count > 0)
					{
						state.PrevToken = codeTokens[^1];
						state.PrevVisibleToken = state.PrevToken.Channel != Lexer.DefaultTokenChannel || state.PrevToken.Type is MainLexer.WS or MainLexer.EOL ? state.PrevVisibleToken : state.PrevToken;
					}
					else
					{
						state.PrevToken = null;
						state.PrevVisibleToken = null;
					}

					state.LineStartIndex = GetDelimiterDepth(DelimiterKind.BraceObjectLiteral) == 0
						&& GetDelimiterDepth(DelimiterKind.Deref) == 0
						&& GetDelimiterDepth(DelimiterKind.Bracket) == 0
						&& GetDelimiterDepth(DelimiterKind.Paren) == 0
						&& (state.PrevToken == null || state.PrevToken.Type == MainLexer.EOL) ? codeTokens.Count : -1;
				}

			OnlyIncrementIndex:
				index++;
			}

			if (pendingImportCodeTokenIndex >= 0)
			{
				var finalToken = tokens.Count > 0 ? tokens[^1] : null;
				FinalizePendingImportCandidate(finalToken);
			}

			bool IsOpenBraceObjectLiteral()
			{
				// If no token precedes it, then it's a block statement
				if (state.PrevVisibleToken == null)
					return false;

				// If the open brace is immediately preceded by a close parenthesis, then it's a block statement, not an object literal. 
				// If this limitation is not applied then we'd need to do complex lookaheads in lots of places.
				if (state.PrevVisibleToken.Type == MainLexer.CloseParen)
					return false;

				// If we are inside parentheses, brackets, or deref, then it's an object literal, not a block statement.
				// The exception is if preceded by a CloseParen, which is handled above.
				if (GetDelimiterDepth(DelimiterKind.Deref) > 0 || GetDelimiterDepth(DelimiterKind.Paren) > 0 || GetDelimiterDepth(DelimiterKind.Bracket) > 0 || codeTokens.Count == 0)
					return true;

				// If the open brace is preceded by a line continuation operator, then it's an object literal, not a block statement.
				// This is because line continuation operators can only be used in expression contexts, and an open brace following a line continuation operator must be starting an object literal.
				if (MainLexerBase.lineContinuationOperators.Contains(state.PrevVisibleToken.Type))
					return true;

				// Finally we need to check whether we are in certain contexts where an expression might follow and thus an open brace would be an object literal.
				// For example throw, return, while, and some other statements.

				bool isSpecialLoop = false;
				bool expectEOL = false;

				for (int i = codeTokens.Count - 1; i >= 0; i--)
				{
					var tk = codeTokens[i];
					var t = tk.Type;
					if (tk.Channel != Lexer.DefaultTokenChannel)
						continue;
					if (expectEOL) // Make sure we are at the start of a statement.
						return t == MainLexer.EOL || t == MainLexer.OpenBrace;
					if (t == MainLexer.WS)
						continue;
					if (t is MainLexer.Loop) // A normal Loop cannot be followed by an object literal, but a special loop such as "Loop Files" can be.
						return isSpecialLoop;
					else if (isSpecialLoop)
						return false;
					else if (t is MainLexer.Files or MainLexer.Read or MainLexer.Reg or MainLexer.Parse)
						isSpecialLoop = true;
					else if (t is MainLexer.Colon) // not in lineContinuationOperators
						return true; 
					else if (t is
						// May be followed by a singleExpression, which allow object literals
						MainLexer.Return or MainLexer.Throw or MainLexer.If or MainLexer.While or MainLexer.Yield or MainLexer.Await or MainLexer.Delete)
						expectEOL = true;
					else
						return false;
				}

				return expectEOL;
			}

			void PushDelimiter(DelimiterKind kind, IToken openToken)
			{
				state.DelimiterStack.Push(new DelimiterFrame(kind, openToken));
				state.DelimiterDepths[(int)kind]++;
			}

			int GetDelimiterDepth(DelimiterKind kind) => state.DelimiterDepths[(int)kind];

			DelimiterKind PopDelimiterOrThrow(IToken closeToken)
			{
				if (state.DelimiterStack.Count == 0)
					throw new ParseException($"Unmatched closing token '{closeToken.Text}':{closeToken.Column}", closeToken);

				var frame = state.DelimiterStack.Pop();
				state.DelimiterDepths[(int)frame.Kind]--;

				if (!IsMatchingDelimiter(frame.Kind, closeToken.Type))
				{
					throw new ParseException(
						$"Mismatched closing token '{closeToken.Text}' at {closeToken.Line}:{closeToken.Column}, expected {GetExpectedClosingText(frame.Kind)} for '{frame.Token.Text}' from {frame.Token.Line}:{frame.Token.Column}",
						closeToken.Line,
						closeToken.Text,
						closeToken.TokenSource.SourceName);
				}

				return frame.Kind;
			}

			bool IsMatchingDelimiter(DelimiterKind openKind, int closeType)
			{
				return openKind switch
				{
					DelimiterKind.BraceBlock => closeType == MainLexer.CloseBrace,
					DelimiterKind.BraceObjectLiteral => closeType == MainLexer.CloseBrace,
					DelimiterKind.Paren => closeType == MainLexer.CloseParen,
					DelimiterKind.Bracket => closeType == MainLexer.CloseBracket,
					DelimiterKind.Deref => closeType == MainLexer.DerefEnd,
					_ => false
				};
			}

			static string GetExpectedClosingText(DelimiterKind openKind)
			{
				return openKind switch
				{
					DelimiterKind.BraceBlock => "}",
					DelimiterKind.BraceObjectLiteral => "}",
					DelimiterKind.Paren => ")",
					DelimiterKind.Bracket => "]",
					DelimiterKind.Deref => "%",
					_ => "matching delimiter"
				};
			}

			int PopWhitespaces(int i, bool linebreaks = true, bool whitespaces = true)
            {
				while (--i >= 0)
                {
                    var ct = codeTokens[i];

					if (ct.Channel != Lexer.DefaultTokenChannel)
                        continue;
                    if ((whitespaces && ct.Type == MainLexer.WS) || (linebreaks && ct.Type == MainLexer.EOL))
                    {
                        codeTokens.RemoveAt(codeTokens.Count - 1);
                        //if (ct.Type == MainLexer.WS)
						//	maybeIsFunctionCallStatement = -1;
					}
                    else
                        break;
                }
                return i;
            }

            bool IsVerbalOperator(int type)
            {
                return type == MainLexer.VerbalAnd || type == MainLexer.VerbalNot || type == MainLexer.VerbalOr;
            }

			bool IsPrecededByEol()
			{
				for (int i = codeTokens.Count - 1; i >= 0; i--)
				{
					var t = codeTokens[i];
					if (t.Channel != Lexer.DefaultTokenChannel)
						continue;
					if (t.Type == MainLexer.WS)
						continue;
					return t.Type == MainLexer.EOL;
				}
				return false;
			}

			bool IsFollowedByOpenParen(int i)
			{
				return ++i < tokens.Count && tokens[i].Type == MainLexer.OpenParen;
			}

			bool IsStatementStart()
			{
				return state.LineStartIndex == codeTokens.Count || state.PendingPrefixedStatementCount > 0;
			}

			bool IsNextTokenWhitespace(int i)
			{
				return ++i < tokens.Count && (tokens[i].Type == MainLexer.WS || tokens[i].Type == MainLexer.EOL);
			}

			void FinalizePendingImportCandidate(IToken lineEndToken)
			{
				try
				{
					if (pendingImportCodeTokenIndex < 0 || pendingImportCodeTokenIndex >= codeTokens.Count)
						return;

					var pendingImportToken = codeTokens[pendingImportCodeTokenIndex];

					if (pendingImportToken.Type != MainLexer.Import)
						return;

					if (TryProbeImportStatementFromCodeTokens(pendingImportCodeTokenIndex, lineEndToken, out var moduleName)
						&& !string.IsNullOrWhiteSpace(moduleName))
					{
						if (compiledTokens)
							TryQueueImportModule(moduleName);
					}
					else
					{
						(pendingImportToken as CommonToken)?.Type = MainLexer.Identifier;
					}
				}
				finally
				{
					pendingImportCodeTokenIndex = -1;
				}
			}

			bool TryProbeImportStatementFromCodeTokens(int startCodeTokenIndex, IToken lineEndToken, out string moduleName)
			{
				moduleName = null;
				var probeTokens = codeTokens[startCodeTokenIndex..^0];

				if (probeTokens.Count == 0)
					return false;

				probeTokens.Add(lineEndToken);

				var probeTokenSource = new ListTokenSource(probeTokens);
				var probeTokenStream = new CommonTokenStream(probeTokenSource);
				var probeParser = new MainParser(probeTokenStream);
				probeParser.RemoveErrorListeners();
				probeParser.ErrorHandler = new BailErrorStrategy();

				try
				{
					var sourceElement = probeParser.sourceElement();
					var importStatement = sourceElement?.importStatement();
					if (importStatement == null)
						return false;

					moduleName = ExtractImportModuleName(importStatement);
					return !string.IsNullOrWhiteSpace(moduleName);
				}
				catch
				{
					return false;
				}
			}

			static string ExtractImportModuleName(MainParser.ImportStatementContext importStatement)
			{
				if (importStatement == null)
					return null;

				MainParser.ModuleNameContext moduleContext = null;
				var clause = importStatement.importClause();

				if (clause?.importNamedFrom() != null)
					moduleContext = clause.importNamedFrom().moduleName();
				else if (clause?.importWildcardFrom() != null)
					moduleContext = clause.importWildcardFrom().moduleName();
				else if (importStatement.importModule() != null)
					moduleContext = importStatement.importModule().moduleName();

				if (moduleContext == null)
					return null;

				if (moduleContext.identifierName() != null)
					return moduleContext.identifierName().GetText();

				var text = moduleContext.StringLiteral()?.GetText() ?? string.Empty;
				if (text.Length >= 2 && (text[0] == '"' || text[0] == '\''))
					text = text[1..^1];

				return text;
			}

			void TryQueueImportModule(string moduleName)
			{
				if (string.IsNullOrWhiteSpace(moduleName))
					return;
				if (moduleName[0] == '*')
					return; // TODO: embedded resource imports
				pendingImports.Enqueue(moduleName);
			}

			/*
            foreach (var token in codeTokens)
            {
                System.Diagnostics.Debug.WriteLine($"Token: {preprocessorLexer.Vocabulary.GetSymbolicName(token.Type)}, Text: '{token.Text}'" + (token.Channel == MainLexer.Hidden ? " (hidden)" : ""));
            }
			*/

			preprocessorLexer.RemoveErrorListeners();

        }

		private List<IToken> GetTokensForModule(ModuleTokenResult result, string moduleName)
		{
			if (!result.TokensByModule.TryGetValue(moduleName, out var tokens))
			{
				tokens = [];
				result.TokensByModule[moduleName] = tokens;
				result.ModuleOrder.Add(moduleName);
			}

			_ = GetIncludesForModule(moduleName);
			return tokens;
		}

		private HashSet<string> GetIncludesForModule(string moduleName)
		{
			if (!includesByModule.TryGetValue(moduleName, out var includeSet))
			{
				includeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				includesByModule[moduleName] = includeSet;
			}

			return includeSet;
		}

		private static void TrimLeadingWhitespace(List<IToken> tokens)
		{
			for (int i = 0; i < tokens.Count; i++)
			{
				if (tokens[i].Type == MainLexer.WS || tokens[i].Type == MainLexer.EOL)
				{
					tokens.RemoveAt(i);
					i--;
				}
				else
					break;
			}
		}

		internal sealed class ModuleTokenResult
		{
			public Dictionary<string, List<IToken>> TokensByModule { get; } = new(StringComparer.OrdinalIgnoreCase);
			public List<string> ModuleOrder { get; } = [];
		}

		private void ResolvePendingImports(ModuleTokenResult result)
		{
			while (pendingImports.Count > 0)
			{
				var moduleName = pendingImports.Dequeue();
				if (string.IsNullOrWhiteSpace(moduleName))
					continue;
				if (result.TokensByModule.ContainsKey(moduleName))
					continue;
				if (loadedImportModules.Contains(moduleName))
					continue;
				if (IsExternalModule(moduleName))
				{
					loadedImportModules.Add(moduleName);
					continue;
				}

				var modulePath = ResolveImportPath(moduleName) ?? throw new ParseException($"Module '{moduleName}' not found.", 0, moduleName);
				loadedImportModules.Add(moduleName);
				var moduleState = CreateTokenScanState(moduleName);
				using (var reader = new StreamReader(modulePath))
					ReadTokens(reader, modulePath, result, moduleState);
				ThrowIfUnclosedDelimiter(moduleState);
			}
		}

		private static TokenScanState CreateTokenScanState(string moduleName) => new(moduleName);

		private static string ResolveInitialIncludePath(string sourceName, string fallbackIncludePath)
		{
			if (!string.IsNullOrWhiteSpace(sourceName))
			{
				if (File.Exists(sourceName))
					return Path.GetDirectoryName(Path.GetFullPath(sourceName)) ?? ".";

				if (Directory.Exists(sourceName))
					return Path.GetFullPath(sourceName);

				if (Path.IsPathRooted(sourceName))
					return Path.GetDirectoryName(sourceName) ?? ".";
			}

			return !string.IsNullOrWhiteSpace(fallbackIncludePath) ? fallbackIncludePath : ".";
		}

		private static void ThrowIfUnclosedDelimiter(TokenScanState state)
		{
			if (state.DelimiterStack.Count > 0)
			{
				var unclosed = state.DelimiterStack.Peek();
				throw new ParseException($"Unclosed delimiter '{unclosed.Token.Text}'", unclosed.Token);
			}
		}

		private static bool IsExternalModule(string moduleName)
		{
			if (string.IsNullOrWhiteSpace(moduleName))
				return false;
			if (Script.TheScript.ReflectionsData.stringToTypes.TryGetValue(moduleName, out var type))
				return typeof(Keysharp.Core.Common.ObjectBase.Module).IsAssignableFrom(type);
			return false;
		}

		private string ResolveImportPath(string moduleName)
		{
			var scriptDir = Path.GetDirectoryName(parser.name);
			var searchPath = Environment.GetEnvironmentVariable("AhkImportPath");
			if (string.IsNullOrWhiteSpace(searchPath))
				searchPath = ".;%A_MyDocuments%\\AutoHotkey;%A_AhkPath%\\..";
			searchPath = ExpandAccessorVariables(searchPath, scriptDir, parser.name);

			foreach (var rawDir in searchPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				var dir = rawDir.Trim();
				if (string.IsNullOrWhiteSpace(dir))
					continue;
				if (!Path.IsPathRooted(dir))
					dir = Path.GetFullPath(Path.Combine(scriptDir ?? string.Empty, dir));

				if (TryResolveModulePath(dir, moduleName, out var path))
					return path;
			}

			return null;
		}

		private static bool TryResolveModulePath(string baseDir, string moduleName, out string path)
		{
			path = null;
			if (string.IsNullOrWhiteSpace(moduleName))
				return false;

			var candidateBase = Path.IsPathRooted(moduleName)
				? moduleName
				: Path.Combine(baseDir, moduleName);

			if (File.Exists(candidateBase))
			{
				path = candidateBase;
				return true;
			}

			if (Directory.Exists(candidateBase))
			{
				var initPath = Path.Combine(candidateBase, "__Init.ahk");
				if (File.Exists(initPath))
				{
					path = initPath;
					return true;
				}
			}

			var withExt = candidateBase + ".ahk";
			if (File.Exists(withExt))
			{
				path = withExt;
				return true;
			}

			return false;
		}

		private string ExpandAccessorVariables(string value, string scriptDir, string scriptPath)
		{
			if (string.IsNullOrEmpty(value))
				return value;

			var replace = (string[,])accessorReplaceTemplate.Clone();
			replace[0, 1] = scriptPath ?? string.Empty;
			replace[1, 1] = scriptDir ?? string.Empty;
			replace[2, 1] = scriptPath ?? string.Empty;

			var expanded = value;
			for (var ii = 0; ii < replace.Length / 2; ii++)
				expanded = expanded.Replace(replace[ii, 0], replace[ii, 1], StringComparison.OrdinalIgnoreCase);

			return expanded;
		}
	}
}
