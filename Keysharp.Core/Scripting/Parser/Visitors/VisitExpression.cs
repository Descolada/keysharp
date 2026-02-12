using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using static MainParser;
using static Keysharp.Scripting.Parser;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.AccessControl;
using System.Configuration;
using System.Xml.Linq;

namespace Keysharp.Scripting
{
    internal partial class VisitMain : MainParserBaseVisitor<SyntaxNode>
    {
		private bool _coalesceOrNullAccess;

        // Converts identifiers such as a%b% to
        // Keysharp.Scripting.Script.Vars[string.Concat<object>(new object[] {"a", b)]
        public SyntaxNode CreateDynamicVariableString(ParserRuleContext context)
        {
            // Collect the parts of the dereference expression
            var parts = new List<ExpressionSyntax>();

            foreach (var child in context.children)
            {
                if (child is PropertyNameContext)
                    parts.Add(CreateStringLiteral(child.GetText().Trim()));
                else
                    parts.Add((ExpressionSyntax)Visit(child));
            }

            // Determine if there is only a single part (no string concatenation needed)
            ExpressionSyntax combinedExpression;
            if (parts.Count == 1)
                combinedExpression = parts[0];
            else
            {
                // Create string.Concat<object>(new object[] { ... }) for multiple parts
                combinedExpression = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        CreateQualifiedName("System.String"), // Roslyn doesn't like lowercase "string"
                        SyntaxFactory.IdentifierName("Concat")
                    ),
					CreateArgumentList(
						SyntaxFactory.ArrayCreationExpression(
                            SyntaxFactory.ArrayType(
                                SyntaxFactory.PredefinedType(
                                    Parser.PredefinedKeywords.Object
                                ),
                                SyntaxFactory.SingletonList(
                                    SyntaxFactory.ArrayRankSpecifier(
                                        SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                            SyntaxFactory.OmittedArraySizeExpression()
                                        )
                                    )
                                )
                            ),
                            SyntaxFactory.InitializerExpression(
                                SyntaxKind.ArrayInitializerExpression,
                                SyntaxFactory.SeparatedList(parts)
                            )
                        )
                    )
                );
            }

            // Wrap the combined expression in Keysharp.Scripting.Script.Vars[...]
            return combinedExpression;
        }

        public override SyntaxNode VisitDereference([NotNull] DereferenceContext context)
        {
            return Visit(context.singleExpression());
        }

        public override SyntaxNode VisitMemberIdentifier([NotNull] MemberIdentifierContext context)
        {
            if (context.dynamicIdentifier() == null)
            {
                var text = context.GetText();
                return SyntaxFactory.IdentifierName(text);
            }
            return base.VisitMemberIdentifier(context);
        }

        public override SyntaxNode VisitIdentifierExpression([NotNull] IdentifierExpressionContext context)
        {
            return Visit(context.identifier());
        }

        public override SyntaxNode VisitPropertyName([NotNull] PropertyNameContext context)
        {
            return CreateStringLiteral(context.GetText().Trim('"', ' '));
        }

		public override SyntaxNode VisitAccessExpression([NotNull] AccessExpressionContext context)
		{
			var useOrNullAccess = _coalesceOrNullAccess;
			if (useOrNullAccess)
				_coalesceOrNullAccess = false;

			var suffixes = new List<AccessSuffixContext>();
			PrimaryExpressionContext baseContext = context;
			while (baseContext is AccessExpressionContext accessContext)
			{
				suffixes.Add(accessContext.accessSuffix());
				baseContext = accessContext.primaryExpression();
			}
			suffixes.Reverse();

			var baseExpression = (ExpressionSyntax)Visit(baseContext);
			return BuildAccessChain(baseExpression, suffixes, useOrNullAccess, suppressFirstOptional: false);
		}

		private ExpressionSyntax BuildAccessChain(
			ExpressionSyntax baseExpression,
			IReadOnlyList<AccessSuffixContext> suffixes,
			bool useOrNull,
			bool suppressFirstOptional)
		{
			var optionalIndex = -1;
			for (var i = 0; i < suffixes.Count; i++)
			{
				if (suppressFirstOptional && i == 0)
					continue;
				if (suffixes[i].modifier != null && suffixes[i].modifier.Type == MainLexer.QuestionMarkDot)
				{
					optionalIndex = i;
					break;
				}
			}

			if (optionalIndex >= 0)
			{
				var prefix = baseExpression;
				for (var i = 0; i < optionalIndex; i++)
				{
					var allowNullResult = i == optionalIndex - 1;
					prefix = ApplyAccessSuffix(prefix, suffixes[i], useOrNull: allowNullResult);
				}

				var tempVar = parser.PushTempVar();
				var guardedExpr = BuildAccessChain(tempVar, suffixes.Skip(optionalIndex).ToList(), useOrNull, suppressFirstOptional: true);
				var assigned = SyntaxFactory.AssignmentExpression(
					SyntaxKind.SimpleAssignmentExpression,
					tempVar,
					PredefinedKeywords.EqualsToken,
					prefix
				);
				var condition = SyntaxFactory.BinaryExpression(
					SyntaxKind.EqualsExpression,
					tempVar,
					PredefinedKeywords.NullLiteral
				);
				var conditional = SyntaxFactory.ConditionalExpression(
					condition,
					PredefinedKeywords.NullLiteral,
					guardedExpr
				);
				var result = ((InvocationExpressionSyntax)InternalMethods.MultiStatement)
					.WithArgumentList(CreateArgumentList(assigned, conditional));
				parser.PopTempVar();
				return result;
			}

			var current = baseExpression;
			for (var i = 0; i < suffixes.Count; i++)
			{
				var isFinal = i == suffixes.Count - 1;
				current = ApplyAccessSuffix(current, suffixes[i], useOrNull && isFinal);
			}
			return current;
		}

		private ExpressionSyntax ApplyAccessSuffix(ExpressionSyntax baseExpression, AccessSuffixContext suffix, bool useOrNull)
		{
			if (suffix.memberIdentifier() != null)
				return GenerateMemberDotAccess(baseExpression, suffix.memberIdentifier(), suffix.memberIndexArguments(), useOrNull);

			if (suffix.memberIndexArguments() != null)
				return GenerateMemberIndexAccess(baseExpression, suffix.memberIndexArguments(), useOrNull);

			if (suffix.modifier != null && suffix.modifier.Type == MainLexer.QuestionMark)
				return baseExpression;

			ArgumentListSyntax argumentList = suffix.arguments() != null
				? (ArgumentListSyntax)VisitArguments(suffix.arguments())
				: SyntaxFactory.ArgumentList();

			var methodName = ExtractMethodName(baseExpression) ?? string.Empty;
			if (methodName.Equals("IsSet", StringComparison.InvariantCultureIgnoreCase)
				&& baseExpression is IdentifierNameSyntax)
			{
				var arg = argumentList.Arguments.First().Expression;
				if (arg is IdentifierNameSyntax identifierName)
				{
					var addedName = parser.MaybeAddVariableDeclaration(identifierName.Identifier.Text);
					if (addedName != null && addedName != identifierName.Identifier.Text)
					{
						identifierName = SyntaxFactory.IdentifierName(addedName);
						argumentList = CreateArgumentList(identifierName);
					}
				}
				argumentList = RewriteIsSetArgumentList(argumentList);
			}
			else if (methodName.Equals("StrPtr", StringComparison.InvariantCultureIgnoreCase)
				&& argumentList.Arguments.First().Expression is ExpressionSyntax strVar
				&& strVar is not LiteralExpressionSyntax)
			{
				argumentList = CreateArgumentList(parser.ConstructVarRef(strVar, false));
			}

			return parser.GenerateFunctionInvocation(baseExpression, argumentList, methodName, useOrNull);
		}

        private ExpressionSyntax GenerateMemberIndexAccess(ExpressionSyntax targetExpression, MemberIndexArgumentsContext memberIndexArguments, bool useOrNull = false)
        {
            // Visit the expressionSequence to generate an ArgumentListSyntax
            var exprArgSeqContext = memberIndexArguments.arguments();
            var argumentList = exprArgSeqContext == null 
                ? SyntaxFactory.ArgumentList()
                : (ArgumentListSyntax)Visit(exprArgSeqContext);

            // Prepend the targetExpression as the first argument
            var fullArgumentList = argumentList.WithArguments(
                argumentList.Arguments.Insert(0, SyntaxFactory.Argument(targetExpression))
            );

            // Generate the invocation: Keysharp.Scripting.Script.GetIndex(target, index)
            var indexInvocation = (useOrNull ? (InvocationExpressionSyntax)InternalMethods.GetIndexOrNull : (InvocationExpressionSyntax)InternalMethods.GetIndex)
                .WithArgumentList(fullArgumentList);

            return indexInvocation;
        }

        public override SyntaxNode VisitExpressionStatement([Antlr4.Runtime.Misc.NotNull] ExpressionStatementContext context)
        {
			var sequence = context.expressionSequence();
            if (sequence.ChildCount == 1)
            {
                var exprContext = sequence.singleExpression(0);
                if (parser.currentFunc.Name == Keywords.AutoExecSectionName && (exprContext is FunctionExpressionContext || exprContext is FatArrowExpressionContext))
                {
                    throw new Exception("Invalid parse: function expression detected instead of function declaration (bug)");
                }
			}

			var sequenceArgList = (ArgumentListSyntax)Visit(sequence);
			ArgumentListSyntax argumentList = CreateArgumentList(sequenceArgList.Arguments.ToArray());

            ExpressionSyntax singleExpression = null;
            if (argumentList.Arguments.Count == 0)
                throw new Error("Expression count can't be 0");

            singleExpression = argumentList.Arguments[0].Expression;

            if (argumentList.Arguments.Count == 1)
            {
                // Validate and convert the expression if necessary
                singleExpression = EnsureValidStatementExpression(singleExpression);

                return SyntaxFactory.ExpressionStatement(singleExpression);
            }
            else
            {
                 // Wrap each expression if needed and create a block
                return SyntaxFactory.Block(argumentList.Arguments
                    .Select(arg =>
                    {
                        var expression = EnsureValidStatementExpression(arg.Expression);
                        return SyntaxFactory.ExpressionStatement(expression);
                    })
                    .ToList());
            }
        }

        private ExpressionSyntax EnsureValidStatementExpression(ExpressionSyntax expression)
        {
            // Check if the expression is valid as a statement
            if (expression is InvocationExpressionSyntax ||
                expression is AssignmentExpressionSyntax ||
                expression is PostfixUnaryExpressionSyntax ||
                expression is PrefixUnaryExpressionSyntax ||
                expression is AwaitExpressionSyntax ||
                expression is ObjectCreationExpressionSyntax)
            {
                return expression; // It's valid, return as-is
            }

            // If not valid, wrap it in a dummy call
            
            return ((InvocationExpressionSyntax)InternalMethods.MultiStatement)
                .WithArgumentList(
				    CreateArgumentList(expression)
                );
        }

        public override SyntaxNode VisitParenthesizedExpression([NotNull] ParenthesizedExpressionContext context)
        {
            ArgumentListSyntax argumentList = (ArgumentListSyntax)Visit(context.expressionSequence());
            if (argumentList.Arguments.Count == 1)
                return SyntaxFactory.ParenthesizedExpression(argumentList.Arguments[0].Expression);

            return ((InvocationExpressionSyntax)InternalMethods.MultiStatement)
                .WithArgumentList(argumentList);
        }

        public override SyntaxNode VisitExpressionSequence(ExpressionSequenceContext context)
        {
            var arguments = new List<ExpressionSyntax>();

            bool isComma;
            bool lastWasComma = true;
            for (var i = 0; i < context.ChildCount; i++)
            {
                var child = context.GetChild(i);
                if (child is ITerminalNode node)
                {
                    if (node.Symbol.Type == EOL || node.Symbol.Type == WS)
                        continue;
                    isComma = node.Symbol.Type == MainLexer.Comma;
                }
                else
                    isComma = false;

                if (isComma)
                {
                    if (lastWasComma)
                        arguments.Add(PredefinedKeywords.NullLiteral);

                    goto ShouldVisitNextChild;
                }
                SyntaxNode expr = Visit((SingleExpressionContext)child);
				if (expr is MethodDeclarationSyntax)
					return expr;
				arguments.Add((ExpressionSyntax)expr);

                ShouldVisitNextChild:
                lastWasComma = isComma;
            }

            return CreateArgumentList(arguments);
        }

        /*
        public override SyntaxNode VisitConcatenateExpression([NotNull] ConcatenateExpressionContext context)
        {
            return ConcatenateExpressions(
                new List<ExpressionSyntax> {
                    (ExpressionSyntax)Visit(context.singleExpression()),
                    (ExpressionSyntax)Visit(context.singleExpressionConcatenation())
            });
        }
        */

		public ExpressionSyntax HandleConcatenateExpressionVisit([NotNull] ParserRuleContext left, ParserRuleContext right)
        {
			var leftExpr = (ExpressionSyntax)Visit(left);
			var rightExpr = (ExpressionSyntax)Visit(right);

            if (TryFoldBinaryExpression(leftExpr, rightExpr, MainParser.Dot, out var folded))
				return folded;

            return CreateBinaryOperatorExpression(MainParser.Dot, leftExpr, rightExpr);
        }

        public override SyntaxNode VisitPreIncrementDecrementExpression([NotNull] PreIncrementDecrementExpressionContext context)
		{
			var prevAssignmentTarget = parser.isAssignmentTarget;
			parser.isAssignmentTarget = true;
			var expression = (ExpressionSyntax)Visit(context.singleExpression());
			parser.isAssignmentTarget = prevAssignmentTarget;
			return HandleCompoundAssignment(expression, CreateNumericLiteral(1L), context.op.Type == MainLexer.PlusPlus ? "+=" : "-=");
		}

		public override SyntaxNode VisitPostIncrementDecrementExpression([NotNull] PostIncrementDecrementExpressionContext context)
		{
			var prevAssignmentTarget = parser.isAssignmentTarget;
			parser.isAssignmentTarget = true;
			var expression = (ExpressionSyntax)Visit(context.singleExpression());
			parser.isAssignmentTarget = prevAssignmentTarget;
			return HandleCompoundAssignment(expression, CreateNumericLiteral(1L), context.op.Type == MainLexer.PlusPlus ? "+=" : "-=", isPostFix: true);
		}

		public override SyntaxNode VisitPowerExpression([NotNull] PowerExpressionContext context)
        {
            return HandleBinaryExpressionVisit(context.left, context.right, context.op.Type);
        }

		public override SyntaxNode VisitUnaryExpression([NotNull] UnaryExpressionContext context)
		{
			return HandleUnaryExpressionVisit(context, context.op.Type);
		}

		public SyntaxNode HandleBinaryExpressionVisit([NotNull] ParserRuleContext left, ParserRuleContext right, int op)
        {
			var leftExpr = (ExpressionSyntax)Visit(left);
			var rightExpr = (ExpressionSyntax)Visit(right);

			if (TryFoldBinaryExpression(leftExpr, rightExpr, op, out var folded))
				return folded;

            return CreateBinaryOperatorExpression(op, leftExpr, rightExpr);
		}

		private static bool TryGetConcatLiteral(ExpressionSyntax expression, out object value)
		{
			value = null;

			if (expression is not LiteralExpressionSyntax literal)
				return false;

			if (literal.IsKind(SyntaxKind.StringLiteralExpression))
			{
				value = literal.Token.Value as string ?? "";
				return true;
			}

			if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
			{
				value = true;
				return true;
			}

			if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
			{
				value = false;
				return true;
			}

			if (literal.IsKind(SyntaxKind.NullLiteralExpression))
			{
				value = null;
				return true;
			}

			if (literal.IsKind(SyntaxKind.NumericLiteralExpression))
			{
				value = literal.Token.Value;
				return true;
			}

			return false;
		}

		private static bool TryGetNumericLiteral(ExpressionSyntax expression, out bool isDouble, out double dbl, out long lng)
		{
			isDouble = false;
			dbl = 0.0;
			lng = 0L;

			if (expression is not LiteralExpressionSyntax literal)
				return false;

			if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
			{
				lng = 1L;
				return true;
			}

			if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
			{
				lng = 0L;
				return true;
			}

			if (!literal.IsKind(SyntaxKind.NumericLiteralExpression))
				return false;

			switch (literal.Token.Value)
			{
				case long l:
					lng = l;
					return true;
				case double d:
					isDouble = true;
					dbl = d;
					return true;
			}

			return false;
		}

		private static bool TryFoldBinaryExpression(ExpressionSyntax leftExpr, ExpressionSyntax rightExpr, int op, out ExpressionSyntax folded)
		{
			folded = null;

			if (op == MainParser.Dot)
			{
				if (TryGetConcatLiteral(leftExpr, out var leftVal) &&
					TryGetConcatLiteral(rightExpr, out var rightVal))
				{
					// Concat() errors if right is null; preserve runtime behavior.
					if (rightVal == null)
						return false;

					var result = string.Concat(Script.ForceString(leftVal), Script.ForceString(rightVal));
					folded = CreateStringLiteral(result);
					return true;
				}
				return false;
			}

			if (!TryGetNumericLiteral(leftExpr, out var leftIsDouble, out var leftD, out var leftL) ||
				!TryGetNumericLiteral(rightExpr, out var rightIsDouble, out var rightD, out var rightL))
				return false;

			var useDouble = leftIsDouble || rightIsDouble;

			switch (op)
			{
				case MainParser.Plus:
					folded = useDouble ? CreateNumericLiteral((leftIsDouble ? leftD : leftL) + (rightIsDouble ? rightD : rightL))
						: CreateNumericLiteral(leftL + rightL);
					return true;
				case MainParser.Minus:
					folded = useDouble ? CreateNumericLiteral((leftIsDouble ? leftD : leftL) - (rightIsDouble ? rightD : rightL))
						: CreateNumericLiteral(leftL - rightL);
					return true;
				case MainParser.Multiply:
					folded = useDouble ? CreateNumericLiteral((leftIsDouble ? leftD : leftL) * (rightIsDouble ? rightD : rightL))
						: CreateNumericLiteral(leftL * rightL);
					return true;
				case MainParser.Divide:
					if ((rightIsDouble ? rightD : rightL) == 0)
						return false;
					folded = CreateNumericLiteral((leftIsDouble ? leftD : leftL) / (rightIsDouble ? rightD : rightL));
					return true;
				case MainParser.IntegerDivide:
					if (useDouble || rightL == 0)
						return false;
					folded = CreateNumericLiteral(leftL / rightL);
					return true;
				case MainParser.Modulus:
					if ((rightIsDouble ? rightD : rightL) == 0)
						return false;
					folded = useDouble ? CreateNumericLiteral((leftIsDouble ? leftD : leftL) % (rightIsDouble ? rightD : rightL))
						: CreateNumericLiteral(leftL % rightL);
					return true;
				case MainParser.Power:
					folded = useDouble
						? CreateNumericLiteral(Math.Pow(leftIsDouble ? leftD : leftL, rightIsDouble ? rightD : rightL))
						: CreateNumericLiteral((long)Math.Pow(leftL, rightL));
					return true;
				case MainParser.LeftShiftArithmetic:
					if (useDouble)
						return false;
					if (rightL < 0 || rightL > 63)
						return false;
					folded = CreateNumericLiteral(leftL << (int)rightL);
					return true;
				case MainParser.RightShiftArithmetic:
					if (useDouble)
						return false;
					if (rightL < 0 || rightL > 63)
						return false;
					folded = CreateNumericLiteral(leftL >> (int)rightL);
					return true;
				case MainParser.RightShiftLogical:
					if (useDouble)
						return false;
					if (rightL < 0 || rightL > 63)
						return false;
					folded = CreateNumericLiteral((long)((ulong)leftL >> (int)rightL));
					return true;
				case MainParser.BitAnd:
					if (useDouble)
						return false;
					folded = CreateNumericLiteral(leftL & rightL);
					return true;
				case MainParser.BitOr:
					if (useDouble)
						return false;
					folded = CreateNumericLiteral(leftL | rightL);
					return true;
				case MainParser.BitXOr:
					if (useDouble)
						return false;
					folded = CreateNumericLiteral(leftL ^ rightL);
					return true;
			}

			return false;
		}

        public SyntaxNode HandlePureBinaryExpressionVisit([NotNull] ParserRuleContext context)
        {
            if (context.GetChild(1) is ITerminalNode node && node != null)
                return SyntaxFactory.BinaryExpression(
                    pureBinaryOperators[node.Symbol.Type],
                    (ExpressionSyntax)Visit(context.GetChild(0)),
                    (ExpressionSyntax)Visit(context.GetChild(2)));
            throw new ValueError("Invalid operand: " + context.GetChild(1).GetText());
        }

        public override SyntaxNode VisitMultiplicativeExpression([NotNull] MultiplicativeExpressionContext context)
        {
            return HandleBinaryExpressionVisit(context.left, context.right, context.op.Type);
        }

		public override SyntaxNode VisitAdditiveExpression([NotNull] AdditiveExpressionContext context)
        {
            return HandleBinaryExpressionVisit(context.left, context.right, context.op.Type);
        }

		public override SyntaxNode VisitBitShiftExpression([NotNull] BitShiftExpressionContext context)
        {
            return HandleBinaryExpressionVisit(context.left, context.right, context.op.Type);
        }

		public override SyntaxNode VisitBitAndExpression([NotNull] BitAndExpressionContext context)
        {
            return HandleBinaryExpressionVisit(context.left, context.right, context.op.Type);
        }

		public override SyntaxNode VisitBitXOrExpression([NotNull] BitXOrExpressionContext context)
        {
            return HandleBinaryExpressionVisit(context.left, context.right, context.op.Type);
        }

		public override SyntaxNode VisitBitOrExpression([NotNull] BitOrExpressionContext context)
        {
            return HandleBinaryExpressionVisit(context.left, context.right, context.op.Type);
        }

		public override SyntaxNode VisitConcatenateExpression([NotNull] ConcatenateExpressionContext context)
        {
            return HandleConcatenateExpressionVisit(context.left, context.right);
        }

		public override SyntaxNode VisitRegExMatchExpression([NotNull] RegExMatchExpressionContext context)
        {
            return HandleBinaryExpressionVisit(context.left, context.right, context.op.Type);
        }

		public override SyntaxNode VisitRelationalExpression([NotNull] RelationalExpressionContext context)
        {
            return HandleBinaryExpressionVisit(context.left, context.right, context.op.Type);
        }

		public override SyntaxNode VisitEqualityExpression([NotNull] EqualityExpressionContext context)
        {
            return HandleBinaryExpressionVisit(context.left, context.right, context.op.Type);
        }

		public override SyntaxNode VisitContainExpression([NotNull] ContainExpressionContext context)
        {
            return HandleContainExpressionVisit(context.left, context.right, context.op.Type);
        }

		private SyntaxNode HandleContainExpressionVisit(ParserRuleContext left, ParserRuleContext right, int type)
        {
			switch (type)
			{
				case MainLexer.Is:
					var leftExpression = (ExpressionSyntax)Visit(left);
                    var rightExpression = (ExpressionSyntax)Visit(right);
					if (IsNullLiteral(rightExpression))
						leftExpression = RewriteAccessInvocationToOrNull(leftExpression);
					return CreateBinaryOperatorExpression(
						MainLexer.Is,
						leftExpression,
						rightExpression);
			}
			throw new NotImplementedException();
		}

		private ArgumentListSyntax RewriteIsSetArgumentList(ArgumentListSyntax argumentList)
		{
			if (argumentList == null || argumentList.Arguments.Count == 0)
				return argumentList;

			var firstArg = argumentList.Arguments[0].Expression;
			if (firstArg is not InvocationExpressionSyntax invocation)
				return argumentList;

			var replacement = RewriteAccessInvocationToOrNull(invocation);
			if (ReferenceEquals(replacement, invocation))
				return argumentList;

			return CreateArgumentList(replacement, argumentList.Arguments.Skip(1).ToList());
		}

		private ExpressionSyntax RewriteAccessInvocationToOrNull(ExpressionSyntax expression)
		{
			if (expression is not InvocationExpressionSyntax invocation)
				return expression;

			if (CheckInvocationExpressionName(invocation, "GetPropertyValue"))
				return ((InvocationExpressionSyntax)InternalMethods.GetPropertyValueOrNull).WithArgumentList(invocation.ArgumentList);
			if (CheckInvocationExpressionName(invocation, "GetIndex"))
				return ((InvocationExpressionSyntax)InternalMethods.GetIndexOrNull).WithArgumentList(invocation.ArgumentList);
			if (CheckInvocationExpressionName(invocation, "Invoke"))
				return ((InvocationExpressionSyntax)InternalMethods.InvokeOrNull).WithArgumentList(invocation.ArgumentList);

			return expression;
		}

		private static bool IsNullLiteral(ExpressionSyntax expression) =>
			expression is LiteralExpressionSyntax literal
			&& literal.IsKind(SyntaxKind.NullLiteralExpression);

        private SyntaxNode HandleLogicalAndExpression(ExpressionSyntax left, ExpressionSyntax right)
        {
            var leftIf = ((InvocationExpressionSyntax)InternalMethods.IfTest)
                .WithArgumentList(CreateArgumentList(left));

            var rightIf = ((InvocationExpressionSyntax)InternalMethods.IfTest)
                .WithArgumentList(CreateArgumentList(right));

            // Create If(left) && If(right)
            var andExpression = SyntaxFactory.BinaryExpression(
                SyntaxKind.LogicalAndExpression,
                leftIf,
                rightIf
            );

            // Call .ParseObject()
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParenthesizedExpression(andExpression),
                    SyntaxFactory.IdentifierName("ParseObject")
                )
            );

			/*
            return SyntaxFactory.ConditionalExpression(
                SyntaxFactory.InvocationExpression(
                    CreateMemberAccess("Keysharp.Scripting.Script", "ForceBool"),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(left))
                    )
                ),
                right,  // If left is truthy, return right
                left    // If left is falsy, return left
            );
            */
		}
		private SyntaxNode HandleLogicalOrExpression(ExpressionSyntax left, ExpressionSyntax right)
        {
            var leftIf = ((InvocationExpressionSyntax)InternalMethods.IfTest)
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(left))));

            var rightIf = ((InvocationExpressionSyntax)InternalMethods.IfTest)
                .WithArgumentList(CreateArgumentList(right));

            // Create If(left) && If(right)
            var orExpression = SyntaxFactory.BinaryExpression(
                SyntaxKind.LogicalOrExpression,
                leftIf,
                rightIf
            );

            // Call .ParseObject()
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParenthesizedExpression(orExpression),
                    SyntaxFactory.IdentifierName("ParseObject")
                )
            );
			/*
            return SyntaxFactory.ConditionalExpression(
                SyntaxFactory.InvocationExpression(
                    CreateMemberAccess("Keysharp.Scripting.Script", "ForceBool"),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(left))
                    )
                ),
                left,  // If left is truthy, return left
                right    // If left is falsy, return right
            );
            */
		}
		public override SyntaxNode VisitLogicalAndExpression([NotNull] LogicalAndExpressionContext context)
        {
            return HandleLogicalAndExpression((ExpressionSyntax)Visit(context.left), (ExpressionSyntax)Visit(context.right));
        }

        public override SyntaxNode VisitLogicalOrExpression([NotNull] LogicalOrExpressionContext context)
        {
            return HandleLogicalOrExpression((ExpressionSyntax)Visit(context.left), (ExpressionSyntax)Visit(context.right));
        }

        public SyntaxNode HandleCoalesceExpression(ExpressionSyntax left, ExpressionSyntax right)
        {
            if (right is AssignmentExpressionSyntax)
                right = SyntaxFactory.ParenthesizedExpression(right);

			return SyntaxFactory.ParenthesizedExpression(SyntaxFactory.BinaryExpression(
				SyntaxKind.CoalesceExpression,
				left,
				right
			));
		}

        public override SyntaxNode VisitCoalesceExpression([NotNull] CoalesceExpressionContext context)
        {
			var prevCoalesceOrNullAccess = _coalesceOrNullAccess;
			_coalesceOrNullAccess = true;
			var left = (ExpressionSyntax)Visit(context.left);
			_coalesceOrNullAccess = prevCoalesceOrNullAccess;
			var right = (ExpressionSyntax)Visit(context.right);
            return HandleCoalesceExpression(left, right);
        }

        public SyntaxNode HandleUnaryExpressionVisit([NotNull] ParserRuleContext context, int type)
        {
            var arguments = new List<ExpressionSyntax>() {
                (ExpressionSyntax)Visit(context.GetChild(context.ChildCount - 1))
            };
            var argumentList = CreateArgumentList(arguments);
            return SyntaxFactory.InvocationExpression(CreateQualifiedName($"Keysharp.Scripting.Script.{unaryOperators[type]}"), argumentList);
        }

		public override SyntaxNode VisitVerbalNotExpression([NotNull] VerbalNotExpressionContext context)
        {
            return HandleUnaryExpressionVisit(context, context.op.Type);
        }

		public override SyntaxNode VisitArrayLiteralExpression([NotNull] ArrayLiteralExpressionContext context)
        {
            return base.VisitArrayLiteralExpression(context);
        }

        private SyntaxNode HandleAssignmentExpression(IParseTree left, IParseTree right, string assignmentOperator)
        {
			var prevAssignmentTarget = parser.isAssignmentTarget;
			parser.isAssignmentTarget = true;
            var leftExpression = (ExpressionSyntax)Visit(left);
			parser.isAssignmentTarget = prevAssignmentTarget;
            var rightExpression = (ExpressionSyntax)Visit(right);

            return HandleAssignment(leftExpression, rightExpression, assignmentOperator);
        }
        public override SyntaxNode VisitAssignmentExpression([NotNull] AssignmentExpressionContext context)
        {
            return HandleAssignmentExpression(context.left, context.right, context.assignmentOperator().GetText());
        }

        private ExpressionSyntax HandleAssignment(ExpressionSyntax leftExpression, ExpressionSyntax rightExpression, string assignmentOperator)
        {
            // Handle ElementAccessExpression for array or indexed assignments
            if (leftExpression is ElementAccessExpressionSyntax elementAccess)
            {
                return HandleElementAccessAssignment(elementAccess, rightExpression, assignmentOperator);
            }

            // Handle MemberAccessExpression for property or field assignments
            else if (leftExpression is MemberAccessExpressionSyntax memberAccess)
            {
                return HandleMemberAccessAssignment(memberAccess, rightExpression, assignmentOperator);
            }

            // Handle other cases (e.g., property assignments, index access)
            if (assignmentOperator == ":=" || assignmentOperator == "??=")
            {
                var assignmentKind = (assignmentOperator == ":=") ? SyntaxKind.SimpleAssignmentExpression : SyntaxKind.CoalesceAssignmentExpression;
				if ((leftExpression is ObjectCreationExpressionSyntax objectExpression)
                && objectExpression.Type is IdentifierNameSyntax objectName
                && objectName.Identifier.Text == "VarRef")
                {
                    var varRefExpression = leftExpression;
                    leftExpression = parser.PushTempVar();
                    var result = ((InvocationExpressionSyntax)InternalMethods.MultiStatement)
                        .WithArgumentList(
							CreateArgumentList(
								SyntaxFactory.AssignmentExpression(
                                    assignmentKind,
                                    leftExpression,
									varRefExpression
                                ),
                                ((InvocationExpressionSyntax)InternalMethods.SetPropertyValue)
                                    .WithArgumentList(
                                        CreateArgumentList(
                                            leftExpression,
                                            CreateStringLiteral("__Value"),
                                            rightExpression
                                        )
                                    )
                                ,
								leftExpression
							)
                        );
                    parser.PopTempVar();
                    return result;
                }
                if (leftExpression is InvocationExpressionSyntax getPropertyInvocation &&
                    CheckInvocationExpressionName(getPropertyInvocation, "GetPropertyValue"))
                {
                    return HandlePropertyAssignment(getPropertyInvocation, rightExpression);
                }
                else if (leftExpression is InvocationExpressionSyntax indexAccessInvocation &&
					CheckInvocationExpressionName(indexAccessInvocation, "GetIndex"))
                {
                    return HandleIndexAssignment(indexAccessInvocation, rightExpression);
                }
                else if (leftExpression is IdentifierNameSyntax identifierNameSyntax)
                {
                    var addedName = parser.MaybeAddVariableDeclaration(identifierNameSyntax.Identifier.Text);
                    if (addedName != null && addedName != identifierNameSyntax.Identifier.Text)
                        identifierNameSyntax = SyntaxFactory.IdentifierName(addedName);
                    leftExpression = identifierNameSyntax;
                    return SyntaxFactory.AssignmentExpression(
                        assignmentKind,
                        leftExpression,
						rightExpression
                    );
                }
                else
                {
                    throw new Error("Unknown left expression type");
                }
            }

            // Handle compound assignments
            return HandleCompoundAssignment(leftExpression, rightExpression, assignmentOperator);
        }

        private ExpressionSyntax HandleElementAccessAssignment(
            ElementAccessExpressionSyntax elementAccess,
            ExpressionSyntax rightExpression,
            string assignmentOperator)
        {
            if (assignmentOperator == ":=")
            {
                return SyntaxFactory.InvocationExpression(
					CreateMemberAccess("Keysharp.Scripting.Script", "SetObject"),
					CreateArgumentList(
                        elementAccess.Expression,
                        elementAccess.ArgumentList.Arguments,
						rightExpression
					)
                );
            }

            if (assignmentOperator == "??=")
            {
                var getIndexValue = CreateElementAccessGetterInvocation(elementAccess);
                var setIndexValue = (ExpressionSyntax)HandleElementAccessAssignment(elementAccess, rightExpression, ":=");

                // Return: left ?? (SetObject(base, index, right))
                return SyntaxFactory.BinaryExpression(
                    SyntaxKind.CoalesceExpression,
                    getIndexValue,
                    setIndexValue
                );
            }

            // Handle compound assignments
            string binaryOperator = MapAssignmentOperatorToBinaryOperator(assignmentOperator);
            var binaryOperation = CreateBinaryOperatorExpression(
                GetOperatorToken(binaryOperator),
                CreateElementAccessGetterInvocation(elementAccess),
                rightExpression
            );

            return HandleElementAccessAssignment(elementAccess, binaryOperation, ":=");
        }

        private ExpressionSyntax HandleMemberAccessAssignment(
            MemberAccessExpressionSyntax memberAccess,
            ExpressionSyntax rightExpression,
            string assignmentOperator)
        {
            if (assignmentOperator == ":=")
            {
                return SyntaxFactory.InvocationExpression(
                    CreateMemberAccess("Keysharp.Scripting.Script", "SetPropertyValue"),
					CreateArgumentList(
                        memberAccess.Expression,
                        CreateStringLiteral(memberAccess.Name.Identifier.Text),
                        rightExpression
                    )
                );
            }

            if (assignmentOperator == "??=")
            {
                var getPropertyValue = SyntaxFactory.InvocationExpression(
					CreateMemberAccess("Keysharp.Scripting.Script", "GetPropertyValue"),
					CreateArgumentList(
                        memberAccess.Expression,
                        CreateStringLiteral(memberAccess.Name.Identifier.Text)
                    )
                );

                var setPropertyValue = (ExpressionSyntax)HandleMemberAccessAssignment(memberAccess, rightExpression, ":=");

                // Return: left ?? (SetPropertyValue(base, member, right))
                return SyntaxFactory.BinaryExpression(
                    SyntaxKind.CoalesceExpression,
                    getPropertyValue,
                    setPropertyValue
                );
            }

            // Handle compound assignments
            string binaryOperator = MapAssignmentOperatorToBinaryOperator(assignmentOperator);
            var binaryOperation = CreateBinaryOperatorExpression(
                GetOperatorToken(binaryOperator),
                SyntaxFactory.InvocationExpression(
					CreateMemberAccess("Keysharp.Scripting.Script", "GetPropertyValue"),
					CreateArgumentList(
                        memberAccess.Expression,
                        CreateStringLiteral(memberAccess.Name.Identifier.Text)
                    )
                ),
                rightExpression
            );

            return HandleMemberAccessAssignment(memberAccess, binaryOperation, ":=");
        }

        private ExpressionSyntax CreateElementAccessGetterInvocation(ElementAccessExpressionSyntax elementAccess)
        {
            var baseExpression = elementAccess.Expression;
            var indexExpression = elementAccess.ArgumentList.Arguments.First().Expression;

            return SyntaxFactory.InvocationExpression(
				CreateMemberAccess("Keysharp.Scripting.Script", "GetIndex"),
				CreateArgumentList(
                    baseExpression,
                    indexExpression
                )
            );
        }

        private ExpressionSyntax HandlePropertyAssignment(InvocationExpressionSyntax getPropertyInvocation, ExpressionSyntax rightExpression)
        {
            var args = getPropertyInvocation.ArgumentList.Arguments;
            if (args[^1].Expression is CollectionExpressionSyntax ces)
            {
				var collectionElements = new List<CollectionElementSyntax>();
				foreach (var node in ces.Elements)
					collectionElements.Add(node);
                collectionElements.Add(SyntaxFactory.ExpressionElement(rightExpression));
				var collectionExpression = SyntaxFactory.CollectionExpression(SyntaxFactory.SeparatedList(collectionElements));

				return SyntaxFactory.InvocationExpression(
	                CreateMemberAccess("Keysharp.Scripting.Script", "SetPropertyValue"),
	                CreateArgumentList(
		                args.SkipLast(1),
						collectionExpression
					)
                );
			}

			return SyntaxFactory.InvocationExpression(
				CreateMemberAccess("Keysharp.Scripting.Script", "SetPropertyValue"),
				CreateArgumentList(
					args,
                    rightExpression
                )
            );
        }

        private ExpressionSyntax HandleIndexAssignment(InvocationExpressionSyntax indexAccessInvocation, ExpressionSyntax rightExpression)
        {
            return SyntaxFactory.InvocationExpression(
				CreateMemberAccess("Keysharp.Scripting.Script", "SetObject"),
				CreateArgumentList(
					indexAccessInvocation.ArgumentList.Arguments,
					rightExpression
				)
            );
        }

        private ExpressionSyntax HandleCompoundAssignment(ExpressionSyntax leftExpression, ExpressionSyntax rightExpression, string assignmentOperator, bool isPostFix = false)
        {
            string binaryOperator = MapAssignmentOperatorToBinaryOperator(assignmentOperator);
			var operatorToken = GetOperatorToken(binaryOperator);

			InvocationExpressionSyntax binaryOperation;
            InvocationExpressionSyntax result = null;

            leftExpression = RemoveExcessParenthesis(leftExpression);

			// If the left side is itself a simple assignment like "a = expr",
			// rewrite (a = expr) <op>= rhs  into  a = (expr <op> rhs)
			if (leftExpression is AssignmentExpressionSyntax simpleAssign && simpleAssign.Kind() == SyntaxKind.SimpleAssignmentExpression)
			{
				// decompose "a = expr"
				var finalTarget = simpleAssign.Left;        // "a"
				var initialValue = simpleAssign.Right;      // "expr" (1 in your example)

				// Build "expr <op> rightExpression"
				// i.e. CreateBinaryOperatorExpression(operatorToken, initialValue, rightExpression)
				var combinedOperation = CreateBinaryOperatorExpression(
					operatorToken,
					initialValue,
					rightExpression
				);

				if (isPostFix)
				{
					// Postfix: (a = expr) <op>= rhs
					//
					// Semantics should match:
					// temp = (a = expr)
					// a = (temp <op> rhs)
					// return temp

					var tempVar = parser.PushTempVar();

					// temp = expr
					var assignTemp = SyntaxFactory.AssignmentExpression(
						SyntaxKind.SimpleAssignmentExpression,
						tempVar,
						PredefinedKeywords.EqualsToken,
						initialValue
					);

					// a = temp <op> rhs
					var tempOp = CreateBinaryOperatorExpression(
						operatorToken,
						tempVar,
						rightExpression
					);
					var assignBack = SyntaxFactory.AssignmentExpression(
						SyntaxKind.SimpleAssignmentExpression,
						finalTarget,
						PredefinedKeywords.EqualsToken,
						tempOp
					);

					// MultiStatement(temp = expr, a = temp <op> rhs, temp)
					var multi = ((InvocationExpressionSyntax)InternalMethods.MultiStatement)
						.WithArgumentList(
							CreateArgumentList(
								assignTemp,
								assignBack,
								tempVar
							)
						);

					parser.PopTempVar();
					return multi;
				}

				// Non-postfix:
				// a = (initialValue <op> rightExpression)
				return SyntaxFactory.AssignmentExpression(
					SyntaxKind.SimpleAssignmentExpression,
					finalTarget,
					PredefinedKeywords.EqualsToken,
					combinedOperation
				);
			}
			// In the case of member or index access, buffer the base and member and then get+set to avoid multiple evaluations
			if (!(leftExpression is IdentifierNameSyntax))
            {
                var baseTemp = parser.PushTempVar();
                var memberTemp = parser.PushTempVar();
                IdentifierNameSyntax resultTemp = null;
                IdentifierNameSyntax varRefTemp = null;
                ExpressionSyntax assignmentExpression = null;
                ExpressionSyntax baseExpression = null;
                ExpressionSyntax memberExpression = null;
                ExpressionSyntax varRefExpression = null;

                if ((leftExpression is ObjectCreationExpressionSyntax objectExpression)
                    && objectExpression.Type is IdentifierNameSyntax objectName
                    && objectName.Identifier.Text == "VarRef")
                {
                    varRefTemp = parser.PushTempVar();
                    varRefExpression = leftExpression;
                    leftExpression = SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.ParenthesizedExpression(
                                SyntaxFactory.CastExpression(
                                    SyntaxFactory.IdentifierName("VarRef"),
                                    varRefTemp
                                )
                            ),
                            SyntaxFactory.IdentifierName("__Value")
                        );
                }
                if (leftExpression is InvocationExpressionSyntax getPropertyInvocation 
                    && CheckInvocationExpressionName(getPropertyInvocation, "GetPropertyValue"))
                {
                    baseExpression = getPropertyInvocation.ArgumentList.Arguments[0].Expression;
                    memberExpression = getPropertyInvocation.ArgumentList.Arguments[1].Expression;

                    var propValue = ((InvocationExpressionSyntax)InternalMethods.GetPropertyValue)
                        .WithArgumentList(
							CreateArgumentList(
                                baseTemp,
                                memberTemp
                            )
                        );

                    if (isPostFix)
                    {
                        resultTemp = parser.PushTempVar();
                        binaryOperation = CreateBinaryOperatorExpression(
							operatorToken,
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                resultTemp,
                                PredefinedKeywords.EqualsToken,
                                propValue
                            ),
                            rightExpression
                        );
                        parser.PopTempVar();
                    }
                    else
                    {
                        binaryOperation = CreateBinaryOperatorExpression(
							operatorToken,
                            propValue,
                            rightExpression
                        );
                    }

                    assignmentExpression = ((InvocationExpressionSyntax)InternalMethods.SetPropertyValue)
                        .WithArgumentList(
							CreateArgumentList(
                                baseTemp,
                                memberTemp,
                                binaryOperation
                            )
                    );
                }
                else if (leftExpression is InvocationExpressionSyntax indexAccessInvocation 
                    && CheckInvocationExpressionName(indexAccessInvocation, "GetIndex"))
                {
                    baseExpression = indexAccessInvocation.ArgumentList.Arguments[0].Expression;
                    memberExpression = indexAccessInvocation.ArgumentList.Arguments[1].Expression;

                    var propValue = ((InvocationExpressionSyntax)InternalMethods.GetIndex)
                        .WithArgumentList(
							CreateArgumentList(
								baseTemp,
                                memberTemp
                            )
                        );

                    if (isPostFix)
                    {
                        resultTemp = parser.PushTempVar();
                        binaryOperation = CreateBinaryOperatorExpression(
							operatorToken,
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                resultTemp,
                                PredefinedKeywords.EqualsToken,
                                propValue),
                            rightExpression
                        );
                        parser.PopTempVar();
                    } else
                    {
                        binaryOperation = CreateBinaryOperatorExpression(
                            operatorToken,
                            propValue,
                            rightExpression
                        );
                    }

                    assignmentExpression = ((InvocationExpressionSyntax)InternalMethods.SetObject)
                        .WithArgumentList(
							CreateArgumentList(
                                baseTemp,
                                memberTemp,
								binaryOperation
							)
                        );
                }
                else if (leftExpression is ElementAccessExpressionSyntax elementAccess)
                {
                    baseExpression = elementAccess.Expression;
                    var indices = elementAccess.ArgumentList.Arguments;

                    resultTemp = parser.PushTempVar();

                    // Assign each index to a temporary variable to avoid repeated evaluation
                    var indexTemps = new List<IdentifierNameSyntax>();
                    foreach (var _ in indices)
                        indexTemps.Add(parser.PushTempVar());
                    // Create assignment expressions for each index and tempIndex
                    var indexTempAssigns = indices.Zip(indexTemps, (indexArg, tempIndex) =>
                        SyntaxFactory.Argument(
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                tempIndex, // The temporary variable
                                PredefinedKeywords.EqualsToken,
                                indexArg.Expression // The original index expression
                            )
                        )
                    ).ToList();

                    // Creates an access in the form Keysharp.Scripting.Script.Vars[_ks_temp2 = x]
                    // This will get assigned to baseTemp: _ks_temp1 = Keysharp.Scripting.Script.Vars[_ks_temp2 = x]
                    baseExpression = elementAccess
                        .WithArgumentList(SyntaxFactory.BracketedArgumentList(SyntaxFactory.SeparatedList(indexTempAssigns)));

                    // Keysharp.Scripting.Script.Vars[_ks_temp2]
                    memberExpression = elementAccess
                        .WithArgumentList(SyntaxFactory.BracketedArgumentList(SyntaxFactory.SeparatedList(indexTemps.Select(index => SyntaxFactory.Argument(index)).ToList())));

					// Keysharp.Scripting.Script.Add(_ks_temp1, rightExpression)
					binaryOperation = CreateBinaryOperatorExpression(
						operatorToken,
                        resultTemp,
                        rightExpression
                    );

                    assignmentExpression = SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        memberExpression,
                        PredefinedKeywords.EqualsToken,
                        binaryOperation
                    );

                    var argumentList = new List<ArgumentSyntax> {
                        SyntaxFactory.Argument(
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                resultTemp,
                                PredefinedKeywords.EqualsToken,
                                baseExpression
                            )
                        ),
                        SyntaxFactory.Argument(
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                memberExpression,
                                PredefinedKeywords.EqualsToken,
                                binaryOperation
                            )
                        )
                    };

                    result = ((InvocationExpressionSyntax)InternalMethods.MultiStatement)
                    .WithArgumentList(
						CreateArgumentList(argumentList)
                    );

                    // Clean up pushed temporaries for indices
                    foreach (var temp in indexTemps)
                    {
                        parser.PopTempVar();
                    }

                    parser.PopTempVar(); // For the resultTemp variable
                }
                else
                    throw new Error("Unknown compound assignment left operand");

                parser.PopTempVar();
                parser.PopTempVar();

                if (result == null)
                result = ((InvocationExpressionSyntax)InternalMethods.MultiStatement)
                    .WithArgumentList(
						CreateArgumentList(
							SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                baseTemp,
                                PredefinedKeywords.EqualsToken,
                                baseExpression
                            ),
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                memberTemp,
                                PredefinedKeywords.EqualsToken,
                                memberExpression
                            ),
                            assignmentExpression
                        )
                    );

                if (varRefTemp != null)
                {
                    result = result.WithArgumentList(
						CreateArgumentList(
							SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                varRefTemp, // The temporary variable
                                varRefExpression // The VarRef expression
                            ),
                            result.ArgumentList.Arguments,
                            varRefTemp
                        )
                    );
                    parser.PopTempVar();
                }

                if (isPostFix && resultTemp != null)
                {
                    return result.WithArgumentList(
						CreateArgumentList(
                            result.ArgumentList.Arguments,
                            resultTemp
                        )
                    );
                }

                return result;
            }

            // If operator is .= then make sure the left-side operand is declared
            if (operatorToken == MainParser.Dot && leftExpression is IdentifierNameSyntax identifier)
            {
                parser.MaybeAddVariableDeclaration(identifier.Identifier.Text);
            }

			binaryOperation = CreateBinaryOperatorExpression(
				operatorToken,
                leftExpression,
                rightExpression
            );

            if (isPostFix)
            {
                var tempVar = parser.PushTempVar(); // Create a temporary variable

                // Create a MultiStatement:
                // temp = x, x = x + 1, temp
                result = ((InvocationExpressionSyntax)InternalMethods.MultiStatement)
                    .WithArgumentList(
						CreateArgumentList(
							SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                tempVar,
                                PredefinedKeywords.EqualsToken,
                                leftExpression
                            ),
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                leftExpression,
                                PredefinedKeywords.EqualsToken,
                                binaryOperation
                            ),
                            tempVar
                        )
                    );
                parser.PopTempVar();
                return result;
            }

            return SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                leftExpression,
                PredefinedKeywords.EqualsToken,
                binaryOperation
            );
        }

        // Helper function to map assignment operators to binary operators
        private string MapAssignmentOperatorToBinaryOperator(string assignmentOperator)
        {
            return assignmentOperator switch
            {
                "+=" => "Add",
                "-=" => "Subtract",
                "*=" => "Multiply",
                "/=" => "Divide",
                "%=" => "Modulus",
                "//=" => "FloorDivide",
                ".=" => "Concat",
                "|=" => "BitwiseOr",
                "&=" => "BitwiseAnd",
                "^=" => "BitwiseXor",
                "<<=" => "BitShiftLeft",
                ">>=" => "BitShiftRight",
                ">>>=" => "LogicalBitShiftRight",
                "**=" => "Power",
                "??=" => "Coalesce",
                _ => throw new InvalidOperationException($"Unsupported assignment operator: {assignmentOperator}")
            };
        }

        // Helper function to map binary operator names to tokens
        private int GetOperatorToken(string binaryOperator)
        {
            return binaryOperators.FirstOrDefault(kvp => kvp.Value == binaryOperator).Key;
        }

        private InvocationExpressionSyntax GenerateMemberDotAccess(ExpressionSyntax baseExpression, MemberIdentifierContext memberIdentifier, MemberIndexArgumentsContext propertyIndexArguments, bool useOrNull = false)
        {
            // Determine the property or method being accessed
            ExpressionSyntax memberExpression = (ExpressionSyntax)Visit(memberIdentifier);

            // Simple identifier should be converted to string literal
            if (memberExpression is IdentifierNameSyntax memberIdentifierName)
            {
                memberExpression = CreateStringLiteral(memberIdentifierName.Identifier.Text);
            }
            // Keysharp.Scripting.Script.Vars[expression] should extract expression
            else if (memberExpression is ElementAccessExpressionSyntax memberElementAccess)
            {
                memberExpression = memberElementAccess.ArgumentList.Arguments.FirstOrDefault().Expression;
            }
            else
                throw new Error("Invalid member dot access expression member");

            var propGetter = useOrNull
                ? (InvocationExpressionSyntax)InternalMethods.GetPropertyValueOrNull
                : (InvocationExpressionSyntax)InternalMethods.GetPropertyValue;

            ArgumentListSyntax memberIndexArgList = SyntaxFactory.ArgumentList();

			if (propertyIndexArguments != null)
            {
				// Visit the expressionSequence to generate an ArgumentListSyntax
                if (propertyIndexArguments.arguments() != null)
				    memberIndexArgList = (ArgumentListSyntax)Visit(propertyIndexArguments.arguments());
                else
                {
					return propGetter
                    .WithArgumentList(
						CreateArgumentList(
							propGetter
						    .WithArgumentList(
								CreateArgumentList(
								    baseExpression,
								    memberExpression
							    )
						    ),
						    CreateStringLiteral("__Item")
						)
					);
				}
			}

            // Generate the call to Keysharp.Scripting.Script.GetPropertyValue(base, member)
            return propGetter
                .WithArgumentList(
                CreateArgumentList(
                    baseExpression,
                    memberExpression,
                    memberIndexArgList.Arguments
                )
            );
        }

        public override SyntaxNode VisitObjectLiteralExpression([NotNull] ObjectLiteralExpressionContext context)
        {
            return base.VisitObjectLiteralExpression(context);
        }
    }
}
