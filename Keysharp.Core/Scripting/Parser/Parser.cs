using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;
using Keysharp.Core.Scripting.Parser.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

namespace Keysharp.Scripting
{
    public enum eScope
    {
        Local,
        Global,
        Static
    }
    public enum eNameCase
    {
        Lower,
        Upper,
        Title
    };
    internal partial class Parser
	{
		internal const string scopeChar = "_";
		internal const string varsPropertyName = "Vars";

		/// <summary>
		/// The order of these is critically important. Items that start with the same character must go from longest to shortest.
		/// </summary>
		internal static FrozenSet<string> contExprOperators = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
		{
			",",
			//"%",
			".=",
			".",
			"??=",
			"??",
			"?",
			"+=",
			"+",
			"-=",
			"-",
			"**",
			"*=",
			"*",
			"!==",
			"!=",
			"!",
			"~",
			"&&",
			"&=",
			"&",
			"^=",
			"^",
			"||",
			"|=",
			"|",
			"//=",
			"//",
			"/=",
			"/",
			"<<=",
			"<<",
			">>>=",
			">>>",
			">>=",
			">>",
			"~=",
			"<=",
			"<",
			">=",
			">",
			":=",
			"==",
			"=>",
			"=",
			":",
			"["
		} .ToFrozenSet(StringComparer.InvariantCultureIgnoreCase);

		internal static FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> contExprOperatorsAlt = contExprOperators.GetAlternateLookup<ReadOnlySpan<char>>();

		internal static List<string> contExprOperatorsList = contExprOperators.ToList();

		/// <summary>
		/// The order of these is critically important. Items that start with the same character must go from longest to shortest.
		/// </summary>
		internal static FrozenSet<string> exprVerbalOperators = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
		{
			"is",
			"and",
			"not",
			"or"
		} .ToFrozenSet(StringComparer.InvariantCultureIgnoreCase);

		internal static FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> exprVerbalOperatorsAlt = exprVerbalOperators.GetAlternateLookup<ReadOnlySpan<char>>();

		internal static FrozenSet<string> flowOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			FlowBreak,
			FlowContinue,
			FlowCase,
			FlowClass,
			FlowDefault,
			FlowFor,
			FlowElse,
			FlowGosub,
			FlowGoto,
			FlowIf,
			FlowLoop,
			FlowReturn,
			FlowWhile,
			FunctionLocal,
			FunctionGlobal,
			//FunctionStatic,//Can't have this in here, else it throws off IsFlowOperator().
			FlowTry,
			FlowCatch,
			FlowFinally,
			FlowUntil,
			FlowSwitch,
			//FlowGet,
			//FlowSet,
			FlowThrow
		} .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

		internal static FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> flowOperatorsAlt = flowOperators.GetAlternateLookup<ReadOnlySpan<char>>();

		internal static List<string> nonContExprOperatorsList = ["++", "--"];
		internal static CodePrimitiveExpression nullPrimitive = new (null);
		internal static CodeTypeReference objTypeRef = new (typeof(object));
		internal static CodeTypeReference ctrpaa = new (typeof(ParamArrayAttribute));
		internal static CodeTypeReference ctrdva = new (typeof(DefaultValueAttribute));
		internal static CodePrimitiveExpression emptyStringPrimitive = new ("");

		internal static FrozenSet<string> propKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			FlowGet,
			FlowSet
		} .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

		internal static FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> propKeywordsAlt = propKeywords.GetAlternateLookup<ReadOnlySpan<char>>();

		internal bool errorStdOut;
		internal CodeStatementCollection initial = [];//These are placed at the very beginning of Main().
		internal List<StatementSyntax> mainFuncInitial = new();
		internal string name = string.Empty;
        internal string hookMutexName = string.Empty;

		internal bool persistent;
		private const string args = "args";
		private const string initParams = "initparams";
		private const string mainScope = "";
		private static readonly char[] directiveDelims = Spaces.Concat([Multicast]);

		private static readonly FrozenSet<string> persistentTerms = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
		{
			"settimer",
			"menu",
			"hotkey",
			"hotstring",
			"onmessage",
			"onclipboardchange",
			"gui",
			"persistent",
			"showdebug"
		} .ToFrozenSet(StringComparer.InvariantCultureIgnoreCase);

		private readonly Stack<List<string>> globalFuncVars = new ();
		private readonly Stack<List<string>> localFuncVars = new ();

        private readonly CompilerHelper Ch;
        public string fileName;

        private readonly char[] ops = [Equal, Not, Greater, Less];
		public List<IToken> codeTokens = [];
		private List<CodeLine> codeLines = [];

        public CompilationUnitSyntax compilationUnit;
        public NamespaceDeclarationSyntax namespaceDeclaration;
        public Class mainClass;
		public Function mainEntryFunc;
        public SeparatedSyntaxList<AttributeSyntax> assemblies = new();
		public Dictionary<string, Module> Modules = new(StringComparer.OrdinalIgnoreCase);
		public Module currentModule;
		internal bool isPrepass;
		internal bool isFirstModulePass;
		internal bool isFinalModulePass;
		internal bool isMultiModuleParse;
		internal bool isAssignmentTarget;
		internal List<List<StatementSyntax>> moduleAutoExecBodies = new();
		internal List<MemberDeclarationSyntax> allDeclaredTopLevelClasses = new();
		internal List<string> moduleParseOrder = new();

        internal PreReader reader;

		public List<MemberDeclarationSyntax> declaredTopLevelClasses
		{
			get => currentModule.DeclaredTopLevelClasses;
			set => currentModule.DeclaredTopLevelClasses = value;
		}

		public Function autoExecFunc
		{
			get => currentModule.AutoExecFunc;
			set => currentModule.AutoExecFunc = value;
		}

		public Function currentFunc
		{
			get => currentModule.CurrentFunc;
			set => currentModule.CurrentFunc = value;
		}

		public List<StatementSyntax> DHHR
		{
			get => currentModule.DHHR;
			set => currentModule.DHHR = value;
		}

		internal Class GlobalClass => currentModule?.ModuleClass ?? mainClass;
		internal bool IsTopLevelContainerClass(Class cls) =>
			cls == mainClass || cls == currentModule?.ModuleClass;

		public uint hotIfCount
		{
			get => currentModule.HotIfCount;
			set => currentModule.HotIfCount = value;
		}

		public uint hotkeyCount
		{
			get => currentModule.HotkeyCount;
			set => currentModule.HotkeyCount = value;
		}

		public uint hotstringCount
		{
			get => currentModule.HotstringCount;
			set => currentModule.HotstringCount = value;
		}

		public bool isHotkeyDefinition
		{
			get => currentModule.IsHotkeyDefinition;
			set => currentModule.IsHotkeyDefinition = value;
		}

		public Stack<(Function, HashSet<string>)> FunctionStack
		{
			get => currentModule.FunctionStack;
			set => currentModule.FunctionStack = value;
		}

		public Stack<Class> ClassStack
		{
			get => currentModule.ClassStack;
			set => currentModule.ClassStack = value;
		}

		public Stack<Loop> LoopStack
		{
			get => currentModule.LoopStack;
			set => currentModule.LoopStack = value;
		}

		public Class currentClass
		{
			get => currentModule.CurrentClass;
			set => currentModule.CurrentClass = value;
		}

		public HashSet<string> globalVars
		{
			get => currentModule.GlobalVars;
			set => currentModule.GlobalVars = value;
		}

		public HashSet<string> accessibleVars
		{
			get => currentModule.AccessibleVars;
			set => currentModule.AccessibleVars = value;
		}

        public static Dictionary<string, Type> _builtinTypes = null;
		public static Dictionary<string, Type> BuiltinTopLevelTypes
        {
            get
            {
                if (_builtinTypes != null)
                    return _builtinTypes;
                _builtinTypes = new (StringComparer.OrdinalIgnoreCase);
				var anyType = typeof(Any);
				foreach (var type in Script.TheScript.ReflectionsData.stringToTypes.Values
						.Where(type => type.IsClass 
                            && !type.IsAbstract
							&& !type.IsNested
							&& anyType.IsAssignableFrom(type)))
					_builtinTypes[type.Name] = type;
				return _builtinTypes;
			}
        }
        public Dictionary<string, string> AllTypes
		{
			get => currentModule.AllTypes;
			set => currentModule.AllTypes = value;
		}

		public Dictionary<string, string> UserTypes
		{
			get => currentModule.UserTypes;
			set => currentModule.UserTypes = value;
		}

		public HashSet<string> UserFuncs
		{
			get => currentModule.UserFuncs;
			set => currentModule.UserFuncs = value;
		}

		public uint loopDepth
		{
			get => currentModule.LoopDepth;
			set => currentModule.LoopDepth = value;
		}

		public uint functionDepth
		{
			get => currentModule.FunctionDepth;
			set => currentModule.FunctionDepth = value;
		}

		public uint classDepth
		{
			get => currentModule.ClassDepth;
			set => currentModule.ClassDepth = value;
		}

		public uint tryDepth
		{
			get => currentModule.TryDepth;
			set => currentModule.TryDepth = value;
		}

		public uint tempVarCount
		{
			get => currentModule.TempVarCount;
			set => currentModule.TempVarCount = value;
		}

		public uint lambdaCount
		{
			get => currentModule.LambdaCount;
			set => currentModule.LambdaCount = value;
		}

        public const string LoopEnumeratorBaseName = InternalPrefix + "e";

		public List<StatementSyntax> generalDirectiveStatements = new();
		public Dictionary<string, string> generalDirectives = new(StringComparer.InvariantCultureIgnoreCase) 
		{
			{ "ClipboardTimeout", null },
            { "ErrorStdOut", null },
            { "HotIfTimeout", null },
            { "MaxThreads", null },
            { "MaxThreadsBuffer", null },
            { "MaxThreadsPerHotkey", null },
            { "NoTrayIcon", null },
            { "WinActivateForce", null },
            { "AssemblyTitle", null },
            { "AssemblyDescription", null },
            { "AssemblyConfiguration", null },
            { "AssemblyCompany", null },
            { "AssemblyProduct", null },
            { "AssemblyCopyright", null },
            { "AssemblyTrademark", null },
			{ "AssemblyVersion", null }
        };

		public static class PredefinedKeywords
        {
			public static LiteralExpressionSyntax NullLiteral = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);

			public static SyntaxToken Object = SyntaxFactory.Token(SyntaxKind.ObjectKeyword);

			public static TypeSyntax ObjectType = SyntaxFactory.PredefinedType(Object);

            public static TypeSyntax ObjectArrayType = SyntaxFactory.ArrayType(
                    SyntaxFactory.PredefinedType(Object))
                .WithRankSpecifiers(
                    SyntaxFactory.SingletonList(
                        SyntaxFactory.ArrayRankSpecifier(
                            SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                SyntaxFactory.OmittedArraySizeExpression()))));

            public static TypeSyntax StringArrayType = SyntaxFactory.ArrayType(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)))
                .WithRankSpecifiers(
                    SyntaxFactory.SingletonList(
                        SyntaxFactory.ArrayRankSpecifier(
                            SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                SyntaxFactory.OmittedArraySizeExpression()))));

            public static IdentifierNameSyntax This = SyntaxFactory.IdentifierName("@this");

			public static ParameterSyntax ThisParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("@this"))
						.WithType(ObjectType);


			public static SyntaxToken PublicToken = SyntaxFactory.Token(SyntaxKind.PublicKeyword);

			public static SyntaxToken StaticToken = SyntaxFactory.Token(SyntaxKind.StaticKeyword);

			public static SyntaxToken AsyncToken = SyntaxFactory.Token(SyntaxKind.StaticKeyword);

			public static SyntaxToken NewToken = SyntaxFactory.Token(SyntaxKind.NewKeyword);

			public static SyntaxToken EqualsToken = SyntaxFactory.Token(SyntaxKind.EqualsToken);

            public static SyntaxToken IsToken = SyntaxFactory.Token(SyntaxKind.IsKeyword);

			public static SyntaxToken ReturnToken = SyntaxFactory.Token(SyntaxKind.ReturnKeyword);

			public static SyntaxToken GotoToken = SyntaxFactory.Token(SyntaxKind.GotoKeyword);

			public static SyntaxToken SemicolonToken = SyntaxFactory.Token(SyntaxKind.SemicolonToken);

			public static StatementSyntax DefaultReturnStatement = SyntaxFactory.ReturnStatement(
                PredefinedKeywords.ReturnToken,
                SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal("")),
                PredefinedKeywords.SemicolonToken
            );
		}
		public class Function
        {
			public Class Class = null;
			public Function Parent = null;
			public MethodDeclarationSyntax Method = null;
            public string Name = null;
			public string UserDeclaredName = null; // Original name as declared by the user (before any mangling)
			public string ImplMethodName = null; // Name of the generated method that implements this function
			public List<StatementSyntax> Body = new();
            public List<ParameterSyntax> Params = new();
			public List<AttributeSyntax> Attributes = new();
			public Dictionary<string, StatementSyntax> Locals = new();
            public HashSet<string> Globals = new HashSet<string>();
            public HashSet<string> Statics = new HashSet<string>();
            public HashSet<string> VarRefs = new HashSet<string>();
            public eScope Scope = eScope.Local;
            public ParserRuleContext RootContext;

			public bool Void = false;
            public bool Async = false;
			public bool Public = true;
			public bool Static = true;
            public bool UserStatic = false;

            public bool HasDerefs = false;

			public Function(string normalizedNameLower, string userDeclaredName, string emittedMethodName, TypeSyntax returnType = null)
			{
				if (string.IsNullOrWhiteSpace(normalizedNameLower))
					throw new ArgumentException(nameof(normalizedNameLower));
				if (string.IsNullOrWhiteSpace(emittedMethodName))
					throw new ArgumentException(nameof(emittedMethodName));

				Name = normalizedNameLower;          // semantic name (for lookups)
				UserDeclaredName = userDeclaredName; // original spelling
				ImplMethodName = emittedMethodName;  // emitted method name
				Method = SyntaxFactory.MethodDeclaration(returnType ?? SyntaxFactory.PredefinedType(Parser.PredefinedKeywords.Object),
														emittedMethodName);
			}

			public BlockSyntax AssembleBody()
            {
                var statements = Locals.Values.ToList();

                if (HasDerefs && Name != Keywords.AutoExecSectionName)
                {
                    var arguments = new List<ArgumentSyntax>();

                    arguments.Add(
                        SyntaxFactory.Argument(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                CreateQualifiedName("Keysharp.Scripting.eScope"),
                                SyntaxFactory.IdentifierName(Scope.ToString())
                            )
                        )
                    );
					arguments.Add(
						SyntaxFactory.Argument(
							SyntaxFactory.LiteralExpression(
								SyntaxKind.StringLiteralExpression,
								SyntaxFactory.Literal(ImplMethodName)
							)
						)
					);
					arguments.Add(
						SyntaxFactory.Argument(
							SyntaxFactory.TypeOfExpression(
								SyntaxFactory.IdentifierName(Class?.Name ?? Keywords.MainClassName)
							)
						)
					);

					if (Globals.Count == 0)
                        arguments.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
                    else
                    {
                        var literalList = new List<ExpressionSyntax>();
                        foreach (var s in Globals)
                            literalList.Add(CreateStringLiteral(s));

                        // Create an array initializer: { "item1", "item2", ... }
                        var arrayInitializer = SyntaxFactory.InitializerExpression(
                            SyntaxKind.ArrayInitializerExpression,
                            SyntaxFactory.SeparatedList(literalList)
                        );

                        // Create an array creation expression: new string[] { "item1", "item2", ... }
                        var arrayCreation = SyntaxFactory.ArrayCreationExpression(
                            SyntaxFactory.ArrayType(
                                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                                SyntaxFactory.SingletonList(
                                    SyntaxFactory.ArrayRankSpecifier(
                                        SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                            SyntaxFactory.OmittedArraySizeExpression()
                                        )
                                    )
                                )
                            )
                        ).WithInitializer(arrayInitializer);

                        arguments.Add(SyntaxFactory.Argument(arrayCreation));
                    }

                    foreach (string localName in Locals.Keys) {
						if (Variables.IsSpecialName(localName)) continue;
                        arguments.Add(
                            SyntaxFactory.Argument(
                                SyntaxFactory.LiteralExpression(
                                    SyntaxKind.StringLiteralExpression, 
                                    SyntaxFactory.Literal(localName)
                                )
                            )
                        );

                        arguments.Add(
                            SyntaxFactory.Argument(
                                Parser.ConstructVarRefFromIdentifier(localName)
                            )
                        );
                    }

                    // Create the object creation expression:
                    //   new Keysharp.Scripting.Script.Variables.Dereference()
                    ObjectCreationExpressionSyntax newExpr = SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName("Keysharp.Scripting.Variables.Dereference"))
                        .WithArgumentList(CreateArgumentList(arguments));

                    // Create the variable declarator for _ks_Derefs with its initializer.
                    VariableDeclaratorSyntax varDeclarator = SyntaxFactory.VariableDeclarator(
                            SyntaxFactory.Identifier(InternalPrefix + "Derefs"))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(PredefinedKeywords.EqualsToken, newExpr));

                    // Create the variable declaration: "Dereference _ks_Derefs = new Keysharp.Scripting.Variables.Dereference();"
                    VariableDeclarationSyntax varDeclaration = SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("Keysharp.Scripting.Variables.Dereference"))
                        .WithVariables(SyntaxFactory.SingletonSeparatedList(varDeclarator));

                    statements.Add(SyntaxFactory.LocalDeclarationStatement(varDeclaration));
                }

                statements.AddRange(Body);

                if (!Void)
                {
                    bool hasReturn = statements.OfType<ReturnStatementSyntax>().Any();

                    if (!hasReturn)
                    {
                        statements.Add(PredefinedKeywords.DefaultReturnStatement);
                    }
                }

				return SyntaxFactory.Block(statements);
            }

            public ParameterListSyntax AssembleParams() => SyntaxFactory.ParameterList(
                SyntaxFactory.SeparatedList<ParameterSyntax>(Params));

			public AttributeListSyntax AssembleAttributes() => SyntaxFactory.AttributeList(
	            SyntaxFactory.SeparatedList<AttributeSyntax>(
		            Attributes));

			public MethodDeclarationSyntax Assemble()
            {
                var modifiers = new List<SyntaxToken>();
				if (Async)
                    modifiers.Add(Parser.PredefinedKeywords.AsyncToken);
                if (Public)
                    modifiers.Add(Parser.PredefinedKeywords.PublicToken);
				if (Static)
                    modifiers.Add(Parser.PredefinedKeywords.StaticToken);

                var body = AssembleBody();

                Method = Method.WithModifiers(modifiers.Count == 0 ? default : SyntaxFactory.TokenList(modifiers));

                if (UserDeclaredName != null && UserDeclaredName != ImplMethodName)
                {
                    var value = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(UserDeclaredName));
                    var nameAttr = SyntaxFactory.Attribute(
                        SyntaxFactory.IdentifierName("UserDeclaredName"),
                        SyntaxFactory.AttributeArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.AttributeArgument(value)
                            )
                        )
                    );
                    Attributes.Add(nameAttr);
				}

				if (Attributes.Count > 0)
                {
                    var attributeList = new SyntaxList<AttributeListSyntax>(AssembleAttributes());
					Method = Method.WithAttributeLists(attributeList);
                }

                return Method
                    .WithParameterList(AssembleParams())
                    .WithBody(body);
            }
        }

        public class Loop
        {
            public string Label = null;
            public bool IsInitialized = true; //Used to determine if a labelledStatement pushed the loop or not
        }
        public Parser(CompilerHelper ch)
		{
			Ch = ch;
			currentModule = GetOrCreateModule(Keywords.MainModuleName);
		}

		internal Module GetOrCreateModule(string moduleName)
		{
			if (string.IsNullOrWhiteSpace(moduleName))
				throw new ArgumentException("Module name cannot be null or whitespace.", nameof(moduleName));

			if (!Modules.TryGetValue(moduleName, out var module))
			{
				module = new Module(moduleName);
				Modules[moduleName] = module;
			}

			EnsureModuleClass(module);

			return module;
		}

		private void EnsureModuleClass(Module module)
		{
			if (module.ModuleClass != null)
				return;

			var moduleClassName = NormalizeIdentifier(module.Name, eNameCase.Title).TrimStart('@');
			module.ModuleClassName = moduleClassName;

			var moduleClass = new Class(moduleClassName, "Module");
			var modifiers = new List<SyntaxKind> { SyntaxKind.PublicKeyword, SyntaxKind.StaticKeyword };
			module.ModuleClass = moduleClass;
		}

		internal void SwitchModule(string moduleName)
		{
			currentModule = GetOrCreateModule(moduleName);
		}

        public static bool IsTypeOrBase(Type t1, string t2)
        {
            while (t1 != null)
            {
                var nameToUse = t1.FullName;

                if (!string.IsNullOrEmpty(t1.Namespace))
                    nameToUse = nameToUse.TrimStartOf($"{t1.Namespace}.").Replace('+', '.').TrimStartOf(Keywords.MainClassName + ".");

                if (string.Compare(nameToUse, t2, true) == 0)
                    return true;

                t1 = t1.BaseType;
            }

            return false;
        }

        public T Parse<T>(TextReader codeStream) => Parse<T>(codeStream, string.Empty);

		public T Parse<T>(TextReader codeStream, string nm)
		{
			name = nm;
            reader = new PreReader(this);

			fileName = name;
            var moduleTokens = reader.ReadScriptTokens(codeStream, name);
			codeTokens.Clear();
			moduleAutoExecBodies.Clear();
			allDeclaredTopLevelClasses.Clear();
			moduleParseOrder = moduleTokens.ModuleOrder.ToList();
			isMultiModuleParse = moduleParseOrder.Count > 1;

			codeStream.Close();

			VisitMain visitor = new VisitMain(this);
			SyntaxNode compilationUnit = null;
			var parsedPrograms = new Dictionary<string, MainParser.ProgramContext>(StringComparer.OrdinalIgnoreCase);

			isPrepass = true;
			for (var i = 0; i < moduleParseOrder.Count; i++)
			{
				var moduleName = moduleParseOrder[i];
				SwitchModule(moduleName);

				if (!moduleTokens.TokensByModule.TryGetValue(moduleName, out var moduleCodeTokens))
					continue;

				codeTokens = moduleCodeTokens;

				/*
				foreach (var token in codeTokens)
				{
					Debug.WriteLine($"Token: {mainLexer.Vocabulary.GetSymbolicName(token.Type)}, Text: '{token.Text}'");
				}
				*/

				var codeTokenSource = new ListTokenSource(codeTokens);
				var codeTokenStream = new CommonTokenStream(codeTokenSource);
				MainParser mainParser = new MainParser(codeTokenStream);

				//mainParser.EnableProfiling();

				//mainParser.Trace = true;
				//var listener = new TraceListener();
				//mainParser.AddParseListener(listener);

				//mainParser.ErrorHandler = new BailErrorStrategy();
				//mainParser.AddErrorListener(new DiagnosticErrorListener());
				//mainParser.Interpreter.PredictionMode = PredictionMode.LL_EXACT_AMBIG_DETECTION;

				//var profilingATNSimulator = new ProfilingATNSimulator(mainParser);

				MainParser.ProgramContext programContext = mainParser.program();

				//ProfileParser(mainParser);
				//Console.WriteLine("End");
				//var decisionInfo = profilingATNSimulator.getDecisionInfo();

				parsedPrograms[moduleName] = programContext;
				visitor.PrepassCollect(programContext);

				//mainParser.RemoveErrorListeners();
			}
			isPrepass = false;

			visitor.EmitModuleImports();

			for (var i = 0; i < moduleParseOrder.Count; i++)
			{
				var moduleName = moduleParseOrder[i];
				SwitchModule(moduleName);

				isFirstModulePass = i == 0;
				isFinalModulePass = i == moduleParseOrder.Count - 1;

				if (parsedPrograms.TryGetValue(moduleName, out var programContext))
				{
					compilationUnit = visitor.Visit(programContext);
				}
			}

			return (T)(object)compilationUnit;
		}

        static void ProfileParser(MainParser parser)
        {
            var profilingATN = parser.GetProfiler();
            var parseInfo = new ParseInfo(profilingATN);

            Console.WriteLine(string.Format("{0,-35}{1,-15}{2,-15}{3,-15}{4,-15}{5,-15}{6,-15}",
                "Rule", "Time", "Invocations", "Lookahead", "Lookahead(Max)", "Ambiguities", "Errors"));

            var ruleNames = parser.RuleNames;
            System.Diagnostics.Debug.WriteLine(string.Format("{0,-35}{1,-15}{2,-15}{3,-15}{4,-15}{5,-15}{6,-15}",
				"Rule",
				"TimeInPrediction",
				"Invocations",
				"SLL_TotalLook",
				"SLL_MaxLook",
				"Abiguities count",
				"Errors"));

            foreach (var decisionInfo in parseInfo.getDecisionInfo())
            {
                var decisionState = parser.Atn.GetDecisionState(decisionInfo.decision);
                var ruleName = ruleNames[decisionState.ruleIndex];

                if (decisionInfo.timeInPrediction > 0)
                {
                    System.Diagnostics.Debug.WriteLine(string.Format("{0,-35}{1,-15}{2,-15}{3,-15}{4,-15}{5,-15}{6,-15}",
                        ruleName,
                        decisionInfo.timeInPrediction,
                        decisionInfo.invocations,
                        decisionInfo.SLL_TotalLook,
                        decisionInfo.SLL_MaxLook,
                        decisionInfo.ambiguities.Count,
                        decisionInfo.errors));
                }
            }
        }

        public static string EscapeHotkeyTrigger(ReadOnlySpan<char> s)
        {
            var escaped = false;
            var sb = new StringBuilder(s.Length);
            char ch = (char)0;

            for (var i = 0; i < s.Length; ++i)
            {
                ch = s[i];
                escaped = i == 0 && ch == '`';

                if (!escaped)
                    sb.Append(ch);
            }

            if (escaped)
                sb.Append(ch);

            return sb.ToString();
        }
    }
}
