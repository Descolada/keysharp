using System.Xml.Linq;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Keysharp.Scripting.Parser;
using static MainParser;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Keysharp.Scripting
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
			var isTopLevelExported = parser.ClassStack.Count == 1
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
				if (resolvedBase != null)
					baseClassName = parser.GetUserTypeCSharpName(resolvedBase);
                else if (extendsParts.Length == 1 && Script.TheScript.ReflectionsData.stringToTypes.ContainsKey(baseClassName))
                {
                    baseClassName = Script.TheScript.ReflectionsData.stringToTypes.First(pair => pair.Key.Equals(baseClassName, StringComparison.InvariantCultureIgnoreCase)).Key;
                }
                parser.currentClass.Base = baseClassName;
            }
            else
            {
                // Default base class is KeysharpObject
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

			if (parser.ClassStack.Count == 1)
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

				parser.declaredTopLevelClasses.Add(fieldDeclaration);
                parser.GlobalClass.cachedFieldNames.Add(fieldDeclarationName);
            }

            if (parser.ClassStack.Count == 1)
            {
                var discard = SyntaxFactory.ExpressionStatement(((InvocationExpressionSyntax)InternalMethods.MultiStatement)
                    .WithArgumentList(CreateArgumentList(SyntaxFactory.IdentifierName(fieldDeclarationName))));
				parser.autoExecFunc.Body.Add(discard);
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

            // Add static__Init, __Init, and static constructor method (must be after processing the elements for proper field assignments)
            AddInitMethods(parser.currentClass.Name);
            parser.currentClass.Body.Add(CreateStaticConstructor(parser.currentClass.Name));

            var newClass = parser.currentClass.Assemble();

            parser.PopClass();
            return newClass;
        }


		public override SyntaxNode VisitClassTail([NotNull] ClassTailContext context)
        {
            return base.VisitClassTail(context);
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
                PushFunction(propertyName, isStatic ? EmitKind.StaticGetter : EmitKind.Getter);

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
                PushFunction(propertyName, isStatic ? EmitKind.StaticSetter : EmitKind.Setter);

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
            var fieldDeclarations = new List<PropertyDeclarationSyntax>();

            var isStatic = context.Static() != null;

            EqualsValueClauseSyntax initializer = null;
            ExpressionSyntax initializerValue = null;

            foreach (var fieldDefinition in context.fieldDefinition())
            {
				ExpressionSyntax baseExpression = PredefinedKeywords.This;
                ExpressionSyntax targetExpression = null;

				if (fieldDefinition.propertyName().Length == 1)
					targetExpression = CreateStringLiteral(fieldDefinition.propertyName(0).GetText());
                else
                {
                    for (int i = 0; i < (fieldDefinition.propertyName().Length - 1); i++)
                    {
                        baseExpression = GenerateGetPropertyValue(baseExpression, CreateStringLiteral(fieldDefinition.propertyName(i).GetText()));
					}
					targetExpression = CreateStringLiteral(fieldDefinition.propertyName(fieldDefinition.propertyName().Length - 1).GetText());
				}

                if (fieldDefinition.singleExpression() != null)
                {
                    initializerValue = (ExpressionSyntax)Visit(fieldDefinition.singleExpression());

                    if (IsLiteralOrConstant(initializerValue))
                    {
                        initializer = SyntaxFactory.EqualsValueClause(PredefinedKeywords.EqualsToken, initializerValue);
                    }

                    if (isStatic)
                        parser.currentClass.deferredStaticInitializations.Add((baseExpression, targetExpression, initializerValue));
                    else
                        parser.currentClass.deferredInitializations.Add((baseExpression, targetExpression, initializerValue));
                }
            }
            parser.currentClass.isInitialization = false;
            return null;
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
            PushFunction(funcHead);
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
            return SyntaxFactory.ConstructorDeclaration(className)
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
                            SyntaxFactory.IdentifierName("args")
                        )
                    )
                )
                .WithBody(SyntaxFactory.Block());
        }

        private ConstructorDeclarationSyntax CreateStaticConstructor(string className)
        {
            return SyntaxFactory.ConstructorDeclaration(className)
                .WithModifiers(SyntaxFactory.TokenList(Parser.PredefinedKeywords.StaticToken))
                .WithBody(SyntaxFactory.Block());
        }

        private void AddInitMethods(string className)
        {
            // Check if instance __Init method already exists
            var instanceInitName = "__Init";
            if (!parser.currentClass.ContainsMethod(instanceInitName, default, true))
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
                    (StatementSyntax)SyntaxFactory.ExpressionStatement(
                        ((InvocationExpressionSyntax)InternalMethods.InvokeOrNull)
                        .WithArgumentList(
							CreateArgumentList(
                                parser.CreateSuperTuple(),
                                CreateStringLiteral("__Init")
                            )
                        )
                    )
                        }.Concat(
                            parser.currentClass.deferredInitializations.Select(deferred =>
                                SyntaxFactory.ExpressionStatement(
                                ((InvocationExpressionSyntax)InternalMethods.SetPropertyValue)
                                    .WithArgumentList(
										CreateArgumentList(
                                            deferred.Item1,
                                            deferred.Item2,
                                            deferred.Item3
                                        )
                                    )
                                )
                            )
                        )
                        .Append(PredefinedKeywords.DefaultReturnStatement)
                    )
                );

                parser.currentClass.Body.Add(instanceInitMethod);
            }

            // Check if static __Init method already exists
            if (!parser.currentClass.ContainsMethod("__Init", true, true))
            {
                // Static __Init method
                var staticInitMethod = SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(Parser.PredefinedKeywords.Object),
                    Keywords.ClassStaticPrefix + "__Init"
                )
                .WithModifiers(SyntaxFactory.TokenList(Parser.PredefinedKeywords.PublicToken, Parser.PredefinedKeywords.StaticToken))
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(Parser.PredefinedKeywords.ThisParam)))
                .WithBody(
                    SyntaxFactory.Block(
					        parser.currentClass.deferredStaticInitializations.Select(deferred =>
                                (StatementSyntax)SyntaxFactory.ExpressionStatement(
                                    ((InvocationExpressionSyntax)InternalMethods.SetPropertyValue)
                                    .WithArgumentList(
								        CreateArgumentList(
                                            deferred.Item1,
                                            deferred.Item2,
                                            deferred.Item3
                                        )
                                    )
                                )
                            )
                        .Append(
                            PredefinedKeywords.DefaultReturnStatement
                        )
                    )
                );

                parser.currentClass.Body.Add(staticInitMethod);
            }
        }
    }
}
