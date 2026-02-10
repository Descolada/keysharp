using static Keysharp.Scripting.Parser;
using Antlr4.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Keysharp.Scripting
{
	internal class PreReader
	{
		private static readonly char[] libBrackets = ['<', '>'];
		private static int hotifcount;

		private readonly Dictionary<string, HashSet<string>> includesByModule = new(StringComparer.OrdinalIgnoreCase);
		private readonly Queue<string> pendingImports = new();
		private readonly HashSet<string> loadedImportModules = new(StringComparer.OrdinalIgnoreCase);
		private readonly Parser parser;
		private string includePath = "./";
		private string currentModuleName = Keywords.MainModuleName;
		internal int NextHotIfCount => ++hotifcount;
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
			currentModuleName = Keywords.MainModuleName;
			includesByModule.Clear();
			pendingImports.Clear();
			loadedImportModules.Clear();

            if (Env.FindCommandLineArgVal("include") is string cmdinc)
            {
                if (File.Exists(cmdinc))
                {
					var includeSet = GetIncludesForModule(currentModuleName);
                    if (!includeSet.Contains(cmdinc))
                    {
						includeSet.Add(cmdinc);
                        using var reader = new StreamReader(cmdinc);
                        ReadTokens(reader, cmdinc, result);
                    }
                }
                else
                    throw new ParseException($"Command line include file {cmdinc} specified with -/include not found.", 0, "");
            }

            ReadTokens(source, name, result);

			ResolvePendingImports(result);

			foreach (var tokens in result.TokensByModule.Values)
				TrimLeadingWhitespace(tokens);

			return result;
		}

		private void ReadTokens(TextReader source, string name, ModuleTokenResult result)
		{
			var replace = (string[,])accessorReplaceTemplate.Clone();
			replace[0, 1] = name; //Note that Name, with a capital N, is the initial script file, not any of the included files.
			replace[1, 1] = Path.GetDirectoryName(parser.name);
			replace[2, 1] = parser.name;

            includePath = File.Exists(name) ? Path.GetFullPath(name) : "./";

			String codeText = source.ReadToEnd() + "\n";
            var codeLines = Regex.Split(codeText, "\r\n|\r|\n");

            AntlrInputStream inputStream = new AntlrInputStream(codeText);
			inputStream.name = name;
            MainLexer preprocessorLexer = new MainLexer(inputStream);

            var codeTokens = GetTokensForModule(result, currentModuleName);
            List<IToken> commentTokens = new List<IToken>();

            var tokens = preprocessorLexer.GetAllTokens();
            var directiveTokens = new List<IToken>();
            var directiveTokenSource = new ListTokenSource(directiveTokens);
            var directiveTokenStream = new CommonTokenStream(directiveTokenSource, MainLexer.DIRECTIVE);
            PreprocessorParser preprocessorParser = new PreprocessorParser(directiveTokenStream);

            int index = 0;
            bool compiliedTokens = true;
			int braceDepth = 0;
			int parenDepth = 0;
			int bracketDepth = 0;
			int derefDepth = 0;
			//int maybeIsFunctionCallStatement = -2;
			int tokenCount = tokens.Count;

			while (index < tokenCount && tokens[index].Type == MainLexer.WS)
                index++;
            while (index < tokenCount)
            {
                var token = tokens[index];

                if (token.Channel == MainLexer.ERROR)
                {
                    if (token.Text == "\"" || token.Text == "'")
						throw new ParseException($"Unterminated string literal at {token.Line}:{token.Column}", token.Line, token.Text, token.TokenSource.SourceName);
					throw new ParseException($"Unexpected token at {token.Line}:{token.Column}: {token.Text}", token.Line, token.Text, token.TokenSource.SourceName);
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

                    directiveTokenSource = new ListTokenSource(directiveTokens);
                    directiveTokenStream = new CommonTokenStream(directiveTokenSource, MainLexer.DIRECTIVE);
                    preprocessorParser.TokenStream = directiveTokenStream;

                    //preprocessorParser.SetInputStream(directiveTokenStream);
                    preprocessorParser.Reset();

                    // Parse condition in preprocessor directive (based on CSharpPreprocessorParser.g4 grammar).
                    PreprocessorParser.Preprocessor_directiveContext directive = preprocessorParser.preprocessor_directive();
                    // if true than next code is valid and not ignored.
                    compiliedTokens = directive.value;

                    String directiveStr = directiveTokens[0].Text.Trim().ToUpper();
                    String conditionalSymbol = null;
                    var lineNumber = token.Line;
                    var code = codeLines[lineNumber - 1];
                    var includeOnce = false;

					index = directiveTokenIndex;
                    SkipWhitespaces(index);

					switch (directiveStr)
					{
                        case "IF":
                        case "ELIF":
                        case "ELSE":
                        case "REGION":
                        case "NULLABLE":
                            // Leave compiliedTokens untouched such that it affects whether the following
							// non-directive tokens will be interpreted or not.
                            break;
                        case "DEFINE":
                            // add to the conditional symbols 
							conditionalSymbol = directiveTokens[1].Text;
                            preprocessorParser.ConditionalSymbols.Add(conditionalSymbol);
                            compiliedTokens = true;
                            break;
                        case "UNDEF":
                            conditionalSymbol = directiveTokens[1].Text;
                            preprocessorParser.ConditionalSymbols.Remove(conditionalSymbol);
                            compiliedTokens = true;
                            break;
                        default:
							compiliedTokens = true;
							break;
                    }

					// Process some AHK directives which are easier to handle here rather than PreprocessorParserBase.cs
                    switch (directiveStr)
					{
                        case "MODULE":
                            {
								if (braceDepth > 0 || parenDepth > 0 || bracketDepth > 0 || derefDepth > 0)
									throw new ParseException("#Module cannot be used inside a block, parentheses, brackets, or deref.", token.Line, "#" + directiveTokens[0].Text, token.TokenSource.SourceName);
								if (directiveTokens.Count < 2)
									throw new ParseException("Module name missing.", token.Line, "#" + directiveTokens[0].Text, token.TokenSource.SourceName);

								var moduleName = directiveTokens[1].Text.Trim().Trim('"', '\'');
								if (string.IsNullOrWhiteSpace(moduleName))
									throw new ParseException("Module name missing.", token.Line, "#" + directiveTokens[0].Text, token.TokenSource.SourceName);

								currentModuleName = moduleName;
								codeTokens = GetTokensForModule(result, currentModuleName);
							}
                            break;
                        case "DLLLOAD":
                            {
                                var p1 = directiveTokens[1].Text;
                                var silent = false;
                                p1 = p1.Trim('"');//Quotes throw off the system file/path functions, so remove them.

                                if (p1.Length > 3 && p1.StartsWith("*i ", StringComparison.OrdinalIgnoreCase))
                                {
                                    p1 = p1.Substring(3);
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
                                    p1 = p1.Substring(3);
                                    silent = true;
                                }
                                else if (p1.StartsWith("*i", StringComparison.OrdinalIgnoreCase))
                                {
                                    p1 = p1.Substring(2);
                                    silent = true;
                                }

                                if (isLib)
                                {
                                    var paths = new List<string>(6);

                                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                                    {
                                        paths.Add($"{includePath}\\{p1}");//Folder relative to the script file, or as overridden.
                                        paths.Add($"{Accessors.A_MyDocuments}\\AutoHotkey\\{LibDir}\\{p1}");//User library.
                                        paths.Add($"{Accessors.A_KeysharpPath}\\{LibDir}\\{p1}");//Executable folder, standard library.
                                    }
                                    else if (Path.DirectorySeparatorChar == '/' && Environment.OSVersion.Platform == PlatformID.Unix)
                                    {
                                        paths.Add($"{includePath}/{p1}");
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

										var includeSet = GetIncludesForModule(currentModuleName);
                                        if (includeOnce && includeSet.Contains(dir))
                                            break;

										includeSet.Add(dir);
                                        using var dirReader = new StreamReader(dir);
                                        ReadTokens(dirReader, dir, result);
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

                                    if (!Path.IsPathRooted(path) && Directory.Exists(includePath))
                                        path = Path.Combine(includePath, path);
                                    else if (!Path.IsPathRooted(path))
                                        path = Path.Combine(Path.GetDirectoryName(name), path);

                                    path = Path.GetFullPath(path);

                                    if (Directory.Exists(path))
                                    {
                                        includePath = path;
                                    }
                                    else if (File.Exists(path))
                                    {
										var includeSet = GetIncludesForModule(currentModuleName);
                                        if (includeOnce && includeSet.Contains(path))
                                            break;

										includeSet.Add(path);
                                        using var pathReader = new StreamReader(path);
                                        ReadTokens(pathReader, path, result);
                                    }
                                    else
                                    {
                                        if (!silent)
                                            throw new ParseException($"Include file {p1} not found at location {path}", token.Line, tokens[index].Text + tokens[index + 1].Text + tokens[index + 2].Text);

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
                            switch ((directiveTokens.Count > 1 ? directiveTokens[1].Text : "FORCE").ToUpperInvariant())
                            {
                                case "FORCE":
                                    SingleInstance = eScriptInstance.Force;
                                    break;

                                case "IGNORE":
                                    SingleInstance = eScriptInstance.Ignore;
                                    break;

                                case "PROMPT":
                                    SingleInstance = eScriptInstance.Prompt;
                                    break;

                                case "OFF":
                                    SingleInstance = eScriptInstance.Off;
                                    break;

                                default:
                                    SingleInstance = eScriptInstance.Force;
                                    break;
                            }
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
                }
                else if (token.Channel == Lexer.DefaultTokenChannel && compiliedTokens)
                {
                    int i;
                    switch (token.Type)
                    {
						case MainLexer.OpenBracket:
							bracketDepth++;
                            SkipWhitespaces(index);
							break;
                        case MainLexer.DerefStart:
							derefDepth++;
                            SkipWhitespaces(index);
							break;
                        case MainLexer.CloseBracket:
							if (bracketDepth > 0)
								bracketDepth--;
                            PopWhitespaces(codeTokens.Count);
							break;
                        case MainLexer.DerefEnd:
							if (derefDepth > 0)
								derefDepth--;
                            PopWhitespaces(codeTokens.Count);
							break;
                        case MainLexer.OpenParen:
                            SkipWhitespaces(index);
							parenDepth++;
							break;
                        case MainLexer.Comma:
                            SkipWhitespaces(index);
                            if (derefDepth > 0 || braceDepth > 0 || parenDepth > 0)
                                PopWhitespaces(codeTokens.Count);
                            else
                            {
                                // Because function call argument list might start with comma, et
                                // MsgBox , "Test"
                                i = codeTokens.Count;
                                while (--i > 0 && codeTokens.Count > 0)
                                {
                                    if (codeTokens[i].Channel != Lexer.DefaultTokenChannel)
                                        continue;
                                    if (codeTokens[i].Type == MainLexer.WS)
                                        continue;
                                    else if (codeTokens[i].Type == MainLexer.EOL)
                                        codeTokens.RemoveAt(i);
                                    else
                                        break;
                                }
                            }
                            break;
                        case MainLexer.CloseParen:
							if (parenDepth > 0)
								parenDepth--;
							PopWhitespaces(codeTokens.Count);
                            break;
						//case MainLexer.BitAnd: // Can't be here because of VarRefs
						case MainLexer.Multiply:
							PopWhitespaces(codeTokens.Count);
							break;
                        case MainLexer.Not:
                        case MainLexer.BitNot:
					    case MainLexer.Plus: // Can't pop whitespaces because of function call statement
						case MainLexer.Minus:
							SkipWhitespaces(index);
                            break;
						case MainLexer.EOL:
                            /*
							if (maybeIsFunctionCallStatement > -1)
							{
								codeTokens.Insert(maybeIsFunctionCallStatement, new CommonToken(MainLexer.FunctionCallStatementMarker)
								{
									Line = token.Line,
									Column = token.Column,
									Text = "FunctionCallStatementMarker"
								});
							}
                            */
							PopWhitespaces(codeTokens.Count);
							SkipWhitespaces(index);
                            //if (enclosableDepth == 0)
							//    maybeIsFunctionCallStatement = -2;
							break;
						case MainLexer.Dot:
                            int previndex = index, prevCount = codeTokens.Count;
                            var popped = PopWhitespaces(codeTokens.Count);

							if ((SkipWhitespaces(index) - 1) != previndex && prevCount != codeTokens.Count)
                            { // Any skipped and popped
								var dottoken = new CommonToken(MainLexer.ConcatDot)
								{
									Line = token.Line,
									Column = token.Column
								};
                                codeTokens.Add(dottoken);
								goto SkipAdd;
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
							PopWhitespaces(codeTokens.Count, IsVerbalOperator(token.Type) ? (!(IsPrecededByEol() && IsFollowedByOpenParen(index))) : true);
                            SkipWhitespaces(index);
                            break;
                        case MainLexer.Loop:
							if (tokens[index + 1].Type == MainLexer.WS)
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
                            if (tokens[index + 1].Type == MainLexer.Comma)
                                index++;
							AddWhitespaces(index, token.Type == MainLexer.Not || token.Type == MainLexer.VerbalNot);
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
                        case MainLexer.Throw:
                        case MainLexer.Async:
                        case MainLexer.Static:
                        case MainLexer.VerbalNot:
                            codeTokens.Add(token);
                            AddWhitespaces(index, token.Type == MainLexer.Not || token.Type == MainLexer.VerbalNot);
                            goto SkipAdd;
						case MainLexer.CloseBrace:
							//if (enclosableDepth > 0)
							//	enclosableDepth--;
							if (braceDepth > 0)
								braceDepth--;
							i = PopWhitespaces(codeTokens.Count, false);
                            var eolToken = new CommonToken(MainLexer.EOL)
                            {
                                Line = token.Line,
                                Column = token.Column
                            };
                            if (i >= 0 && codeTokens[i].Type != MainLexer.EOL)
                            {
                                codeTokens.Add(eolToken);
                            }
                            codeTokens.Add(token);
                            i = index;
                            bool eolPresent = false;
                            while (++i < tokens.Count)
                            {
                                if (tokens[i].Channel == MainLexer.DIRECTIVE || tokens[i].Channel == MainLexer.ERROR)
                                    break;
                                if (tokens[i].Channel != Lexer.DefaultTokenChannel)
                                    continue;
                                if (tokens[i].Type == MainLexer.WS)
                                    index++;
                                else if (tokens[i].Type == MainLexer.EOL)
                                {
                                    eolPresent = true;
                                    break;
                                }
                                else
                                    break;
                            }
                            if (!eolPresent)
                                codeTokens.Add(eolToken);
							goto SkipAdd;
						case MainLexer.OpenBrace:
							braceDepth++;
							break;
						case MainLexer.Import:
							if (!TryParseImportStatement(index, out var importModuleName))
							{
								var identToken = new CommonToken(token)
								{
									Type = MainLexer.Identifier,
									Text = token.Text
								};
								codeTokens.Add(identToken);
								goto SkipAdd;
							}
							if (compiliedTokens)
								TryQueueImportModule(importModuleName);
							codeTokens.Add(token);
							AddWhitespaces(index, false);
							goto SkipAdd;
						case MainLexer.HotIf:
						case MainLexer.InputLevel:
						case MainLexer.UseHook:
						case MainLexer.SuspendExempt:
							i = index;
                            while (++i < tokens.Count)
                            {
                                if (tokens[i].Channel == MainLexer.DIRECTIVE || tokens[i].Channel == MainLexer.ERROR)
                                    break;
                                if (tokens[i].Channel != Lexer.DefaultTokenChannel)
                                    index++;
                                else if (tokens[i].Type == MainLexer.WS)
                                    index++;
                                else
                                    break;
                            }
							break;
                        case MainLexer.WS:
                            break;
                            /*
						case MainLexer.Identifier:
							// Here do some partial parsing to figure out whether this can be a function call
							// statement. 
							// 1. Can't be inside parenthesis, brackets, or object notation
							if (enclosableDepth > 0 || maybeIsFunctionCallStatement > -2)
							{
								break;
							}
							maybeIsFunctionCallStatement = codeTokens.Count;
							// Inspect previous tokens to figure out whether the context is suitable.
							i = codeTokens.Count;
							while (--i >= 0)
							{
								var t = codeTokens[i];
								switch (t.Type)
								{
									case MainLexer.WS: // Just skip these
										continue;
									case MainLexer.EOL: // Valid contexts
									case MainLexer.HotkeyTrigger:
									case MainLexer.HotstringTrigger:
									case MainLexer.Else:
									case MainLexer.Try:
										i = 0;
										break;
									case MainLexer.Colon: // Figure out whether this is after a label name (invalid), or switch-case/default (valid)
										if (codeTokens[--i].Type != MainLexer.Identifier && codeTokens[i].Type != MainLexer.DecimalLiteral) // Can't be a label, so leave maybeIsFunctionCallStatement as true
											break;

										// Try to determine whether this is a switch-case
										while (--i > 0)
										{
											var j = codeTokens[i];
											if (j.Type == MainLexer.WS)
												continue;
											else if (j.Type == MainLexer.EOL) // This was a label
											{
												maybeIsFunctionCallStatement = -1;
												break;
											}
											else
												break;
										}
										if (maybeIsFunctionCallStatement == -1)
											i = 0;
										break;
									default: // Invalid context for function call statement
										maybeIsFunctionCallStatement = -1;
										i = 0;
										break;
								}
							}
							if (maybeIsFunctionCallStatement == -1)
								break;

							i = index;
							int depth = 0;

							int nextToken;
							while (++i < tokenCount && maybeIsFunctionCallStatement != -1)
							{
								nextToken = tokens[i].Type;
								switch (nextToken)
								{
									case MainLexer.OpenBrace:
										maybeIsFunctionCallStatement = -1;
										break;
									case MainLexer.OpenParen:
										if (enclosableDepth == 0)
											maybeIsFunctionCallStatement = -1;
										depth++;
										break;
									case MainLexer.OpenBracket:
									case MainLexer.DerefStart:
										depth++;
										break;
									case MainLexer.CloseParen:
									case MainLexer.CloseBracket:
									case MainLexer.CloseBrace:
									case MainLexer.DerefEnd:
										depth--;
										if (depth == 0)
											continue;
										break;
								}
								if (depth != 0)
									continue;

								switch (nextToken)
								{
									case MainLexer.Identifier:
									case MainLexer.Dot:
										continue;
									case MainLexer.WS:
									case MainLexer.EOL:
									case MainLexer.Eof:
										i = tokenCount;
										break;
									case MainLexer.Comma:
										if (i == (index + 1))
										{
											IToken t = tokens[i];
											throw new InvalidOperationException($"Syntax error at line {t.Line}:{t.Column} - Function calls require a space or \"(\".  Use comma only between parameters.");
										}
										maybeIsFunctionCallStatement = -1;
										break;
									default:
										maybeIsFunctionCallStatement = -1;
										break;
								}
							}
							break;
                            */
						default:
                            break;
                    }
                AddToken:
                    codeTokens.Add(token); // Collect code tokens.
                }
            SkipAdd:
                index++;
            }

            int PopWhitespaces(int i, bool linebreaks = true)
            {
				while (--i >= 0)
                {
                    var ct = codeTokens[i];

					if (ct.Channel != Lexer.DefaultTokenChannel)
                        continue;
                    if (ct.Type == MainLexer.WS || (linebreaks && ct.Type == MainLexer.EOL))
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

            int SkipWhitespaces(int i, bool linebreaks = true)
            {
                while (++i < tokens.Count)
                {
                    if (tokens[i].Channel == MainLexer.DIRECTIVE || tokens[i].Channel == MainLexer.ERROR)
                        break;
                    if ((tokens[i].Channel != Lexer.DefaultTokenChannel) || tokens[i].Type == MainLexer.WS || (linebreaks && tokens[i].Type == MainLexer.EOL))
                        index++;
                    else
                        break;
                }
                return i;
            }

            int AddWhitespaces(int i, bool condition)
            {
				while (++i < tokens.Count)
				{
					if (tokens[i].Channel == MainLexer.DIRECTIVE || tokens[i].Channel == MainLexer.ERROR)
						break;
					if ((tokens[i].Channel != Lexer.DefaultTokenChannel) || (tokens[i].Type == MainLexer.EOL && condition))
						index++;
					else if (tokens[i].Type == MainLexer.WS)
					{
						codeTokens.Add(tokens[i]);
						index++;
					}
					else
						break;
				}
                return i;
			}

			int NextNonWhitespace(int i, bool allowEol)
			{
				while (++i < tokens.Count)
				{
					if (tokens[i].Channel == MainLexer.ERROR)
						return i;
					if (tokens[i].Channel == MainLexer.DIRECTIVE)
						continue;
					if (tokens[i].Channel != Lexer.DefaultTokenChannel)
						continue;
					if (tokens[i].Type == MainLexer.WS)
						continue;
					if (allowEol && tokens[i].Type == MainLexer.EOL)
						continue;
					return i;
				}
				return i;
			}

            bool IsStatementStart()
            {
                for (int i = codeTokens.Count - 1; i >= 0; i--)
                {
                    var t = codeTokens[i];
					if (t.Channel != Lexer.DefaultTokenChannel)
						continue;
					if (t.Type == MainLexer.WS)
						continue;
					if (t.Type == MainLexer.EOL)
						return true;
					return t.Type == MainLexer.CloseBrace
						|| t.Type == MainLexer.Export;
				}
                return true; // start of file
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
				return ++i < tokens.Count ? tokens[i].Type == MainLexer.OpenParen : false;
			}

			bool TryParseImportStatement(int startIndex, out string moduleName)
			{
				moduleName = null;
				if (braceDepth > 0 || parenDepth > 0 || bracketDepth > 0 || derefDepth > 0)
					return false;
				if (!IsStatementStart())
					return false;

				bool allowExportList = false;
				for (int scan = codeTokens.Count - 1; scan >= 0; scan--)
				{
					var t = codeTokens[scan];
					if (t.Channel != Lexer.DefaultTokenChannel)
						continue;
					if (t.Type == MainLexer.WS || t.Type == MainLexer.EOL)
						continue;
					allowExportList = t.Type == MainLexer.Export;
					break;
				}

				int scanIndex = startIndex;
				scanIndex = NextNonWhitespace(scanIndex, allowEol: false);
				if (scanIndex >= tokenCount)
					return false;

				switch (tokens[scanIndex].Type)
				{
					case MainLexer.OpenBrace:
						int localDepth = 1;
						while (++scanIndex < tokenCount)
						{
							var t = tokens[scanIndex];
							if (t.Channel == MainLexer.DIRECTIVE || t.Channel == MainLexer.ERROR)
								break;
							if (t.Channel != Lexer.DefaultTokenChannel)
								continue;
							if (t.Type == MainLexer.WS || t.Type == MainLexer.EOL)
								continue;
							if (t.Type == MainLexer.OpenBrace)
								localDepth++;
							else if (t.Type == MainLexer.CloseBrace)
							{
								localDepth--;
								if (localDepth == 0)
									break;
							}
						}
						if (localDepth != 0 || scanIndex >= tokenCount)
							return false;
						scanIndex = NextNonWhitespace(scanIndex, allowEol: false);
						if (scanIndex >= tokenCount || tokens[scanIndex].Type != MainLexer.From)
							return false;
						scanIndex = NextNonWhitespace(scanIndex, allowEol: false);
						if (scanIndex < tokenCount && (tokens[scanIndex].Type == MainLexer.Identifier || tokens[scanIndex].Type == MainLexer.StringLiteral))
						{
							moduleName = ExtractModuleName(tokens[scanIndex]);
							return !string.IsNullOrWhiteSpace(moduleName);
						}
						return false;
					case MainLexer.Multiply:
						scanIndex = NextNonWhitespace(scanIndex, allowEol: false);
						if (scanIndex >= tokenCount || tokens[scanIndex].Type != MainLexer.From)
							return false;
						scanIndex = NextNonWhitespace(scanIndex, allowEol: false);
						if (scanIndex < tokenCount && (tokens[scanIndex].Type == MainLexer.Identifier || tokens[scanIndex].Type == MainLexer.StringLiteral))
						{
							moduleName = ExtractModuleName(tokens[scanIndex]);
							return !string.IsNullOrWhiteSpace(moduleName);
						}
						return false;
					case MainLexer.Identifier:
					case MainLexer.StringLiteral:
						moduleName = ExtractModuleName(tokens[scanIndex]);
						if (string.IsNullOrWhiteSpace(moduleName))
							return false;

						bool sawAlias = false;
						int j = NextNonWhitespace(scanIndex, allowEol: false);
						if (j < tokenCount && tokens[j].Type == MainLexer.As)
						{
							j = NextNonWhitespace(j, allowEol: false);
							if (j >= tokenCount || tokens[j].Type != MainLexer.Identifier)
								return false;
							sawAlias = true;
							j = NextNonWhitespace(j, allowEol: false);
						}

						if (allowExportList && !sawAlias && j < tokenCount && tokens[j].Type == MainLexer.OpenBrace)
							return false;

						if (allowExportList && j < tokenCount && tokens[j].Type == MainLexer.OpenBrace)
						{
							int depth = 1;
							while (++j < tokenCount)
							{
								var t = tokens[j];
								if (t.Channel == MainLexer.DIRECTIVE || t.Channel == MainLexer.ERROR)
									break;
								if (t.Channel != Lexer.DefaultTokenChannel)
									continue;
								if (t.Type == MainLexer.WS || t.Type == MainLexer.EOL)
									continue;
								if (t.Type == MainLexer.OpenBrace)
									depth++;
								else if (t.Type == MainLexer.CloseBrace)
								{
									depth--;
									if (depth == 0)
										break;
								}
							}
							if (depth != 0 || j >= tokenCount)
								return false;
							j = NextNonWhitespace(j, allowEol: false);
						}

						if (j >= tokenCount)
							return true;
						return tokens[j].Type == MainLexer.EOL || tokens[j].Type == MainLexer.Eof;
					default:
						return false;
				}
			}

			void TryQueueImportModule(string moduleName)
			{
				if (string.IsNullOrWhiteSpace(moduleName))
					return;
				if (moduleName[0] == '*')
					return; // TODO: embedded resource imports
				pendingImports.Enqueue(moduleName);
			}

			string ExtractModuleName(IToken token)
			{
				if (token == null)
					return string.Empty;
				var text = token.Text ?? string.Empty;
				text = text.Trim();
				if (text.Length >= 2 && (text[0] == '"' || text[0] == '\''))
					text = text.Substring(1, text.Length - 2);
				return text;
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
				tokens = new List<IToken>();
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
			public List<string> ModuleOrder { get; } = new();
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

				var modulePath = ResolveImportPath(moduleName);
				if (modulePath == null)
					throw new ParseException($"Module '{moduleName}' not found.", 0, moduleName);

				var prevModule = currentModuleName;
				currentModuleName = moduleName;
				loadedImportModules.Add(moduleName);
				using (var reader = new StreamReader(modulePath))
					ReadTokens(reader, modulePath, result);
				currentModuleName = prevModule;
			}
		}

		private bool IsExternalModule(string moduleName)
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
