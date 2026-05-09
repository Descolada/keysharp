using Keysharp.Builtins;
using System.Xml.Linq;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Keysharp.Parsing.Parser;
using static Keysharp.Parsing.Antlr.MainParser;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Keysharp.Parsing
{
    internal partial class VisitMain : MainParserBaseVisitor<SyntaxNode>
    {
		// params object[] args
		public ParameterSyntax VariadicParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("args")) // Default name for spread argument
        .WithType(SyntaxFactory.ArrayType(
            SyntaxFactory.PredefinedType(Parser.PredefinedKeywords.Object), // object[]
            SyntaxFactory.SingletonList(SyntaxFactory.ArrayRankSpecifier(
                SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                    SyntaxFactory.OmittedArraySizeExpression()
                )
            ))
        ))
        .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ParamsKeyword)));

		public override SyntaxNode VisitClassDeclaration([NotNull] ClassDeclarationContext context)
        {
			if (parser.currentFunc != parser.autoExecFunc)
				throw new ParseException("Classes cannot be declared inside functions", context);

			string userDeclaredName = context.identifier().GetText();
            parser.PushClass(parser.NormalizeIdentifier(userDeclaredName, eNameCase.Title));
            parser.currentClass.UserDeclaredName = userDeclaredName;
			parser.currentClass.IsStruct = context.kind?.Type == MainParser.Struct;
			if (parser.currentClass.IsStruct)
				parser.currentClass.Base = "Struct";
			var isTopLevelClass = parser.ClassStack.Count == 1;
			var isTopLevelExported = isTopLevelClass
				&& parser.currentModule.ExportedTypes.Contains(parser.currentClass.Name);

			// Determine the base class (Extends clause)
            if (context.Extends() != null)
            {
                var extendsParts = context.classExtensionName().identifier();
				var baseClassName = parser.NormalizeIdentifier(extendsParts[0].GetText(), eNameCase.Title);
				for (int i = 1; i < extendsParts.Length; i++)
                {
                    baseClassName += "." + parser.NormalizeIdentifier(extendsParts[i].GetText(), eNameCase.Title);
                }
				if (extendsParts.Length == 1)
					UserTypeNameToKeysharp(ref baseClassName);

				var resolvedBase = parser.ResolveUserTypeName(baseClassName);
				var baseTypeKey = resolvedBase ?? baseClassName;

				if (parser.currentClass.IsStruct && !parser.IsStructDerivedType(baseTypeKey))
					throw new ParseException($"Struct base type {string.Join(".", extendsParts.Select(x => x.GetText()))} must derive from Struct", context);

				if (resolvedBase != null)
					baseClassName = parser.GetUserTypeCSharpName(resolvedBase);
                else if (extendsParts.Length == 1)
                {
                    var lookup = Script.TheScript.ReflectionsData.stringToTypes.GetAlternateLookup<ReadOnlySpan<char>>();
                    if (lookup.TryGetValue(baseClassName, out var builtInType))
                        baseClassName = (builtInType.FullName ?? builtInType.Name).Replace('+', '.');
                }
                parser.currentClass.Base = baseClassName;
            }
            else
            {
                // Default base class is KeysharpObject or Struct.
            }

			string fieldDeclarationName = parser.NormalizeIdentifier(parser.currentClass.Name);

            MemberDeclarationSyntax fieldDeclaration = null;
            SyntaxToken[] fieldDeclarationModifiers = [Parser.PredefinedKeywords.PublicToken, Parser.PredefinedKeywords.StaticToken];
            var fieldDeclarationArrowClause = SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.ElementAccessExpression(
                    CreateMemberAccess($"{MainScriptVariableName}.Vars", "Statics")
                )
                .WithArgumentList(
                    SyntaxFactory.BracketedArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(
                                SyntaxFactory.TypeOfExpression(
                                    CreateQualifiedName(
                                        string.Join(".",
                                            parser.ClassStack.Reverse()               // Outer-to-inner order.
                                            .Select(cls => cls.Name)
                                        ) + "." + parser.currentClass.Name
                                    )
                                )
                            )
                        )
                    )
                )
            );

			if (isTopLevelClass)
            {
                fieldDeclaration = SyntaxFactory.PropertyDeclaration(
                    Parser.PredefinedKeywords.ObjectType,
					SyntaxFactory.Identifier(fieldDeclarationName)
				)
                .AddModifiers(fieldDeclarationModifiers)
                .WithExpressionBody(fieldDeclarationArrowClause)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

				if (isTopLevelExported && fieldDeclaration is PropertyDeclarationSyntax prop)
					fieldDeclaration = Parser.WithExportAttribute(prop);

				parser.GlobalClass.Body.Insert(parser.currentModule.DeclaredTopLevelClassCount++, fieldDeclaration);
            }

            // Add the constructor
            parser.currentClass.Body.Add(CreateConstructor(parser.currentClass.Name));

            // Process class elements
            if (context.classTail().classElement() != null)
            {
                foreach (var element in context.classTail().classElement())
                {
                    var members = Visit(element);
                    if (members == null)
                        continue;
                    if (members is MemberDeclarationSyntax member) {
                        parser.currentClass.Body.Add(member);
                    }
                }
            }

            // Add static__Init and __Init after processing the elements for proper field assignments.
            AddInitMethods(parser.currentClass.Name);

            var newClass = parser.currentClass.Assemble();

            parser.PopClass();

			if (isTopLevelClass)
			{
				parser.GlobalClass.Body.Add(newClass);
				return SyntaxFactory.ExpressionStatement(
					((InvocationExpressionSyntax)InternalMethods.MultiStatement)
					.WithArgumentList(CreateArgumentList(SyntaxFactory.IdentifierName(fieldDeclarationName)))
				);
			}

            return newClass;
        }


		public override SyntaxNode VisitClassTail([NotNull] ClassTailContext context)
        {
            return base.VisitClassTail(context);
        }

		public override SyntaxNode VisitClassPositionalDirective([NotNull] ClassPositionalDirectiveContext context)
		{
			if (context.positionalDirective().positionalDirectiveBody() is not RequiresDirectiveContext)
				throw new ParseException("Only #Requires can be declared inside classes", context);

			return Visit(context.positionalDirective());
		}

        public override SyntaxNode VisitClassPropertyDeclaration([NotNull] ClassPropertyDeclarationContext context)
        {
            var propertyDefinition = context.propertyDefinition();
            var isStatic = context.Static() != null;

            // Determine if this is a regular property or an indexer
            var propertyNameSyntax = propertyDefinition.classPropertyName();

            string propertyName;
            List<ParameterSyntax> indexerParameters = null;

            propertyName = propertyNameSyntax.propertyName().GetText();

            // Getters and setters are created as normal methods with "static" +- "get_"/"set_" prefixes.
            // This is to allow arbitrary "this" parameters. When the script is ran then Script.InitClasses
            // will initialize the prototype and static instance with the prefixes trimmed.

            // Generate getter method
            MethodDeclarationSyntax getterMethod = null;
            if (propertyDefinition.propertyGetterDefinition().Length != 0 || propertyDefinition.singleExpression() != null)
            {
				var definitionContext = propertyDefinition.propertyGetterDefinition().Length != 0
					? (ParserRuleContext)propertyDefinition.propertyGetterDefinition(0)
					: propertyDefinition;
                PushFunction(propertyName, isStatic ? EmitKind.StaticGetter : EmitKind.Getter, definitionContext: definitionContext);

				if (propertyNameSyntax.formalParameterList() != null)
                {
                    indexerParameters = ((ParameterListSyntax)Visit(propertyNameSyntax.formalParameterList())).Parameters.ToList();
                    parser.currentFunc.Params.AddRange(indexerParameters);
                } else
					parser.currentFunc.Params.Add(Parser.PredefinedKeywords.ThisParam);

				if (propertyDefinition.propertyGetterDefinition().Length != 0)
                {
                    var getterBody = (BlockSyntax)Visit(propertyDefinition.propertyGetterDefinition(0));
                    parser.currentFunc.Body.AddRange(getterBody.Statements);
                }
                else if (propertyDefinition.singleExpression() != null)
                {
                    var getterBody = SyntaxFactory.Block(
                        SyntaxFactory.ReturnStatement(
                            PredefinedKeywords.ReturnToken,
                            (ExpressionSyntax)Visit(propertyDefinition.singleExpression()),
                            PredefinedKeywords.SemicolonToken
                        )
                    );
                    parser.currentFunc.Body.AddRange(getterBody.Statements);
                }

                getterMethod = parser.currentFunc.Assemble();
                PopFunction();
                parser.currentClass.Body.Add(getterMethod);
            }

            // Generate setter method
            MethodDeclarationSyntax setterMethod = null;
            if (propertyDefinition.propertySetterDefinition().Length != 0)
            {
                PushFunction(propertyName, isStatic ? EmitKind.StaticSetter : EmitKind.Setter, definitionContext: propertyDefinition.propertySetterDefinition(0));

				if (propertyNameSyntax.formalParameterList() != null)
                {
                    // Even the getter visited it, we need to visit again because some parameters
                    // add statements to the function body.
                    indexerParameters = ((ParameterListSyntax)Visit(propertyNameSyntax.formalParameterList())).Parameters.ToList();

                    var p = indexerParameters[^1];

                    // Check if it's a `params object[]` parameter
                    if (p.Type is ArrayTypeSyntax arrayType &&
                        arrayType.ElementType.IsKind(SyntaxKind.PredefinedType) &&
                        ((PredefinedTypeSyntax)arrayType.ElementType).Keyword.IsKind(SyntaxKind.ObjectKeyword) &&
                        p.Modifiers.Any(SyntaxKind.ParamsKeyword))
                    {
                        // Remove `params` modifier and replace with a normal `object[]`
                        indexerParameters[^1] = p.WithModifiers(SyntaxFactory.TokenList()); // Remove params
                    }
                    // Preserve indexer parameters
                    parser.currentFunc.Params.AddRange(indexerParameters);
                }
                else
                    parser.currentFunc.Params.Add(Parser.PredefinedKeywords.ThisParam);

                // Always add `object value` parameter for setters
                parser.currentFunc.Params.Add(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"))
                        .WithType(SyntaxFactory.PredefinedType(Parser.PredefinedKeywords.Object))
                );

                //parser.currentFunc.Void = true;
                var setterBody = (BlockSyntax)Visit(propertyDefinition.propertySetterDefinition(0));
                parser.currentFunc.Body.AddRange(setterBody.Statements);

                setterMethod = parser.currentFunc.Assemble();
                PopFunction();
                parser.currentClass.Body.Add(setterMethod);
            }
            return null;
        }

		public override SyntaxNode VisitClassFieldDeclaration([NotNull] ClassFieldDeclarationContext context)
		{
			parser.currentClass.isInitialization = true; // If the field initializer contains closures then they shouldn't get the "this" parameter added, this keeps track of that
            var isStatic = context.Static() != null;
			if (context.typedFieldDefinition().Length != 0)
			{
				if (!parser.currentClass.IsStruct)
					throw new ParseException("Typed fields are only supported inside structs", context);

				if (isStatic)
					throw new ParseException("Typed struct fields cannot be static", context);

				foreach (var fieldDefinition in context.typedFieldDefinition())
				{
					var fieldName = fieldDefinition.propertyName().GetText();
					var fieldTypeName = ResolveStructFieldTypeName(fieldDefinition.structFieldType());

					parser.currentClass.StaticInitStatements.Add(
						CreateDefinePropStatement(
							CreateClassStoreAccess("Prototypes", parser.currentClass.Name),
							CreateStringLiteral(fieldName),
							SyntaxFactory.TypeOfExpression(CreateQualifiedName(fieldTypeName))
						)
					);

					if (fieldDefinition.singleExpression() != null)
					{
						var initializerValue = (ExpressionSyntax)Visit(fieldDefinition.singleExpression());
						parser.currentClass.deferredInitializations.Add((PredefinedKeywords.This, CreateStringLiteral(fieldName), initializerValue));
					}
				}
			}
			else
			{
				if (parser.currentClass.IsStruct && !isStatic)
					throw new ParseException("Struct instance fields must use typed field syntax", context);

				foreach (var fieldDefinition in context.fieldDefinition())
				{
					if (fieldDefinition.singleExpression() != null)
					{
						var (baseExpression, targetExpression) = ResolveFieldTarget(fieldDefinition.propertyName());
						var initializerValue = (ExpressionSyntax)Visit(fieldDefinition.singleExpression());

						if (isStatic)
							parser.currentClass.deferredStaticInitializations.Add((baseExpression, targetExpression, initializerValue));
						else
							parser.currentClass.deferredInitializations.Add((baseExpression, targetExpression, initializerValue));
					}
				}
			}
            parser.currentClass.isInitialization = false;
			return null;
		}

		private (ExpressionSyntax Base, ExpressionSyntax Name) ResolveFieldTarget(PropertyNameContext[] path)
		{
			ExpressionSyntax target = PredefinedKeywords.This;

			for (int i = 0; i < path.Length - 1; i++)
				target = GenerateGetPropertyValue(target, CreateStringLiteral(path[i].GetText()));

			return (target, CreateStringLiteral(path[^1].GetText()));
		}


        public override SyntaxNode VisitPropertyGetterDefinition([Antlr4.Runtime.Misc.NotNull] PropertyGetterDefinitionContext context)
        {
			var scopeContext = (ParserRuleContext)context.functionBody()?.block() ?? context.functionBody()?.singleExpression();
			HandleScopeFunctions(scopeContext);
            return VisitFunctionBody(context.functionBody());
        }

        public override SyntaxNode VisitPropertySetterDefinition([NotNull] PropertySetterDefinitionContext context)
        {
			var scopeContext = (ParserRuleContext)context.functionBody()?.block() ?? context.functionBody()?.singleExpression();
			HandleScopeFunctions(scopeContext);
            return (BlockSyntax)VisitFunctionBody(context.functionBody());
        }

        public override SyntaxNode VisitClassMethodDeclaration([NotNull] ClassMethodDeclarationContext context)
        {
            var methodDefinition = context.functionDeclaration();
            var funcHead = methodDefinition.functionHead();
            PushFunction(funcHead, methodDefinition);
			VisitFunctionHeadPrefix(funcHead.functionHeadPrefix());
			var methodBodyContext = (ParserRuleContext)methodDefinition.functionBody().block() ?? methodDefinition.functionBody().singleExpression();
			HandleScopeFunctions(methodBodyContext);
			Visit(funcHead);

            // Visit method body
            BlockSyntax methodBody = (BlockSyntax)Visit(methodDefinition.functionBody());
            parser.currentFunc.Body.AddRange(methodBody.Statements);

            // Create method declaration
            parser.currentFunc.Method = SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(Parser.PredefinedKeywords.Object), // Return type is object
                    SyntaxFactory.Identifier(parser.currentFunc.ImplMethodName)
                );
            var methodDeclaration = parser.currentFunc.Assemble();

			PopFunction();

            return methodDeclaration;
        }

        private ConstructorDeclarationSyntax CreateConstructor(string className)
        {
			var ctor = SyntaxFactory.ConstructorDeclaration(className)
                .WithModifiers(SyntaxFactory.TokenList(Parser.PredefinedKeywords.PublicToken))
                .WithParameterList(
                    SyntaxFactory.ParameterList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Parameter(SyntaxFactory.Identifier("args"))
                                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ParamsKeyword)))
                                .WithType(
                                    SyntaxFactory.ArrayType(
                                        SyntaxFactory.PredefinedType(Parser.PredefinedKeywords.Object))
                                        .WithRankSpecifiers(
                                            SyntaxFactory.SingletonList(
                                                SyntaxFactory.ArrayRankSpecifier(
                                                    SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                                        SyntaxFactory.OmittedArraySizeExpression())
                                                )
                                            )
                                        )
                                )
                        )
                    )
                )
                .WithInitializer(
                    SyntaxFactory.ConstructorInitializer(
                        SyntaxKind.BaseConstructorInitializer,
						CreateArgumentList(
                            PredefinedKeywords.NullLiteral
                        )
                    )
                )
                .WithBody(SyntaxFactory.Block());

			return ctor;
        }

		private string ResolveStructFieldTypeName(StructFieldTypeContext context)
		{
			var rawTypeName = string.Join(".", context.identifier().Select(id => parser.NormalizeIdentifier(id.GetText(), eNameCase.Title)));
			if (rawTypeName.Length == 0)
				throw new ParseException("Struct field type is required", context);

			if (context.identifier().Length == 1)
				UserTypeNameToKeysharp(ref rawTypeName);

			var resolvedUserType = parser.ResolveUserTypeName(rawTypeName, Parser.UserTypeLookupMode.Scoped);
			if (resolvedUserType != null)
				return parser.GetUserTypeCSharpName(resolvedUserType);

			var lookup = Script.TheScript.ReflectionsData.stringToTypes.GetAlternateLookup<ReadOnlySpan<char>>();
			if (lookup.TryGetValue(rawTypeName, out var builtInType))
				return (builtInType.FullName ?? builtInType.Name).Replace('+', '.');

			throw new ParseException($"Unknown struct field type {rawTypeName}", context);
		}

        private void AddInitMethods(string className)
        {
            // Check if instance __Init method already exists
            var instanceInitName = "__Init";
			if (parser.currentClass.deferredInitializations.Count > 0
				&& parser.currentClass.ContainsMethod(instanceInitName, default, true))
				throw new ParseException(parser.currentClass.IsStruct
					? "Structs with field initializers cannot declare __Init."
					: "Classes with generated instance initialization cannot declare __Init.");

			if (parser.currentClass.deferredInitializations.Count > 0)
            {
                // Instance __Init method
                var instanceInitMethod = SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(Parser.PredefinedKeywords.Object),
                    instanceInitName
                )
                .WithModifiers(SyntaxFactory.TokenList(Parser.PredefinedKeywords.PublicToken, Parser.PredefinedKeywords.StaticToken))
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(Parser.PredefinedKeywords.ThisParam)))
                .WithBody(
                    SyntaxFactory.Block(
                        new StatementSyntax[] // Add the call to Invoke((object)super, "__Init") as the first statement
                        {
                    CreateSuperInitStatement(false)
                        }.Concat(
                            parser.currentClass.deferredInitializations.Select(deferred =>
                                CreateSetPropertyStatement(
									deferred.Item1,
									deferred.Item2,
									deferred.Item3
								)
                            )
                        )
                        .Append(parser.DefaultReturnStatement)
                    )
                );

                parser.currentClass.Body.Add(instanceInitMethod);
            }

			var staticInitStatements = new List<StatementSyntax>(parser.currentClass.StaticInitStatements);
			staticInitStatements.AddRange(
				parser.currentClass.deferredStaticInitializations.Select(deferred =>
					CreateSetPropertyStatement(
						deferred.Item1,
						deferred.Item2,
						deferred.Item3
					)
				)
			);

			if (staticInitStatements.Count == 0)
				return;

			const string staticInitName = Keywords.ClassStaticPrefix + "__Init";

			if (parser.currentClass.Body.ContainsName(staticInitName, true, static m => m is MethodDeclarationSyntax))
				throw new ParseException(parser.currentClass.IsStruct
					? "Structs with typed fields cannot declare static __Init."
					: "Classes with generated static initialization cannot declare static __Init.");

			// Static __Init method
			var staticInitMethod = SyntaxFactory.MethodDeclaration(
				SyntaxFactory.PredefinedType(Parser.PredefinedKeywords.Object),
				staticInitName
			)
			.WithModifiers(SyntaxFactory.TokenList(Parser.PredefinedKeywords.PublicToken, Parser.PredefinedKeywords.StaticToken))
			.WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(Parser.PredefinedKeywords.ThisParam)))
			.WithBody(
				SyntaxFactory.Block(
					staticInitStatements.Append(
						parser.DefaultReturnStatement
					)
				)
			);

			parser.currentClass.Body.Add(staticInitMethod);
        }

		private static StatementSyntax CreateDefinePropStatement(ExpressionSyntax target, ExpressionSyntax name, ExpressionSyntax descriptor) =>
			SyntaxFactory.ExpressionStatement(
				SyntaxFactory.InvocationExpression(
					CreateMemberAccess("Keysharp.Builtins.Objects", "ObjDefineProp"),
					CreateArgumentList(target, name, descriptor)
				)
			);

		private static StatementSyntax CreateSetPropertyStatement(ExpressionSyntax target, ExpressionSyntax name, ExpressionSyntax value) =>
			SyntaxFactory.ExpressionStatement(
				SyntaxFactory.InvocationExpression(
					CreateMemberAccess("Keysharp.Runtime.Script", "SetPropertyValue"),
					CreateArgumentList(target, name, value)
				)
			);

		private StatementSyntax CreateSuperInitStatement(bool isStatic)
		{
			var superTuple = SyntaxFactory.CastExpression(
				SyntaxFactory.PredefinedType(Parser.PredefinedKeywords.Object),
				SyntaxFactory.TupleExpression(
					SyntaxFactory.SeparatedList<ArgumentSyntax>(
						new ArgumentSyntax[]
						{
							SyntaxFactory.Argument(
								CreateClassStoreAccess(isStatic ? "Statics" : "Prototypes", parser.currentClass.Base)
							),
							SyntaxFactory.Argument(PredefinedKeywords.This)
						}
					)
				)
			);

			return SyntaxFactory.ExpressionStatement(
				((InvocationExpressionSyntax)InternalMethods.Invoke)
				.WithArgumentList(
					CreateArgumentList(
						superTuple,
						CreateStringLiteral("__Init")
					)
				)
			);
		}

		private static ExpressionSyntax CreateClassStoreAccess(string storeName, string typeName) =>
			GenerateItemAccess(
				VarsNameSyntax,
				SyntaxFactory.IdentifierName(storeName),
				SyntaxFactory.TypeOfExpression(CreateQualifiedName(typeName))
			);
    }
}
