using Keysharp.Builtins;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;

namespace Keysharp.Parsing
{
	internal partial class Parser
	{
		public class Class
		{
			public int Indent = 0;
			public string Name = null;
			public string FullName = null;
			public string UserDeclaredName = null;
			public string Base = "KeysharpObject";
			public bool IsStruct = false;
			public List<BaseTypeSyntax> BaseList = new();
			public NamedIndexedCollection<MemberDeclarationSyntax> Body = new(GetBodyMemberName);
			public List<AttributeSyntax> Attributes = new();
			public ClassDeclarationSyntax Declaration = null;
			public readonly List<StatementSyntax> StaticInitStatements = new();

			public bool isInitialization = false;
			public readonly List<(ExpressionSyntax BaseExpr, ExpressionSyntax TargetExpr, ExpressionSyntax Initializer)> deferredInitializations = new();
			public readonly List<(ExpressionSyntax BaseExpr, ExpressionSyntax TargetExpr, ExpressionSyntax Initializer)> deferredStaticInitializations = new();

			public Class(string name, string baseName = "KeysharpObject")
			{
				if (string.IsNullOrWhiteSpace(name))
					throw new ArgumentException("Name cannot be null or empty.", nameof(name));

				Name = name;
				Declaration = SyntaxFactory.ClassDeclaration(
					modifiers: SyntaxFactory.TokenList(PredefinedKeywords.PublicToken),
					identifier: SyntaxFactory.Identifier(Name),
					attributeLists: default,
					typeParameterList: null,
					baseList: null,
					constraintClauses: default,
					members: default
					);
				Base = baseName;
			}
			public AttributeListSyntax AssembleAttributes() => SyntaxFactory.AttributeList(
			SyntaxFactory.SeparatedList<AttributeSyntax>(
				Attributes));

			public ClassDeclarationSyntax Assemble()
			{
				if (UserDeclaredName != null && UserDeclaredName != Name)
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
					Declaration = Declaration.WithAttributeLists(attributeList);
				}
				if (!Base.IsNullOrEmpty())
					BaseList.Insert(0, SyntaxFactory.SimpleBaseType(CreateQualifiedName(Base)));
				return Declaration
					.WithBaseList(BaseList.Count > 0 ? SyntaxFactory.BaseList(SyntaxFactory.SeparatedList(BaseList)) : default)
					.AddMembers(Body.ToArray());
			}

			internal void AddBodyField(FieldDeclarationSyntax field) => Body.Add(field);

			internal void UpsertBodyField(FieldDeclarationSyntax field)
			{
				var name = field.Declaration.Variables.FirstOrDefault()?.Identifier.Text;
				Body.Upsert(name, field, caseSensitive: false,
					predicate: static m => m is FieldDeclarationSyntax);
			}

			internal bool TryGetBodyField(string name, out FieldDeclarationSyntax field)
			{
				if (Body.TryGetValue(name, out var member, caseSensitive: false,
					predicate: static m => m is FieldDeclarationSyntax))
				{
					field = (FieldDeclarationSyntax)member;
					return true;
				}
				field = null;
				return false;
			}

			private static string GetBodyMemberName(MemberDeclarationSyntax member) => member switch
			{
				FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
				PropertyDeclarationSyntax prop => prop.Identifier.Text,
				MethodDeclarationSyntax method => method.Identifier.Text,
				_ => null
			};

			public bool ContainsMethod(string methodName, bool searchStatic = false, bool caseSensitive = false)
			{
				if (Body == null) throw new ArgumentNullException(nameof(Body));
				if (string.IsNullOrEmpty(methodName)) throw new ArgumentException("Method name cannot be null or empty", nameof(methodName));

				// Adjust string comparison based on case-sensitivity
				var stringComparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

				// Search for methods
				foreach (var member in Body)
				{
					if (member is MethodDeclarationSyntax method)
					{
						var methName = method.Identifier.Text;
						bool isStatic = false;
						if (methName.StartsWith(Keywords.ClassStaticPrefix))
						{
							methName = methName.Substring(Keywords.ClassStaticPrefix.Length);
							isStatic = true;
						}
						// Check method name
						if (string.Equals(methName, methodName, stringComparison))
						{
							if (isStatic == searchStatic) return true;
						}
					}
				}

				return false;
			}
		}
		public void PushClass(string className, string baseName = "KeysharpObject")
		{
			ClassStack.Push(currentClass);
			classDepth++;
			var newClass = new Class(className, baseName);
			if (currentClass != null && currentClass != mainClass && !string.IsNullOrWhiteSpace(currentClass.FullName))
				newClass.FullName = currentClass.FullName + "." + newClass.Name;
			else
				newClass.FullName = newClass.Name;
			currentClass = newClass;
		}

		public void PopClass()
		{
			currentClass = ClassStack.Pop();
			classDepth--;
		}
	}
}
