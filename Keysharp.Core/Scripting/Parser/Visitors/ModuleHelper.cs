using System;
using System.Collections.Generic;
using System.Reflection;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Keysharp.Scripting.Parser;
using static MainParser;
using ExportKind = Keysharp.Scripting.Parser.Module.ExportKind;

namespace Keysharp.Scripting
{
	internal partial class VisitMain
	{
		internal void EmitModuleImports()
		{
			ResolveExportImports();
			foreach (var moduleName in parser.moduleParseOrder)
			{
				if (!parser.Modules.TryGetValue(moduleName, out var module) || module.ModuleClass == null)
					continue;
				if (module.ImportsEmitted || module.Imports.Count == 0)
					continue;

				EmitImportsForModule(module);
				module.ImportsEmitted = true;
			}
		}

		private void ResolveExportImports()
		{
			var guard = 0;
			bool changed;

			do
			{
				changed = false;
				foreach (var module in parser.Modules.Values)
				{
					if (module.Imports.Count == 0)
						continue;

					foreach (var import in module.Imports)
					{
						if (!import.IsExported)
							continue;

						if (!parser.Modules.TryGetValue(import.ModuleName, out var targetModule))
						{
							if (TryResolveExternalModuleType(import.ModuleName, out var externalType, out _))
								changed |= ResolveExportImport(
									module,
									import,
									name => ResolveExternalExportKind(externalType, name),
									EnumerateExternalExports(externalType),
									ExportKind.Variable
								);
							continue;
						}

						changed |= ResolveExportImport(
							module,
							import,
							name => ResolveScriptExport(targetModule, name),
							EnumerateScriptExports(targetModule),
							GetDefaultExportKind(targetModule)
						);
					}
				}
			}
			while (changed && guard++ < 32);
		}

		private static bool ShouldExportAll(Parser.Module.ImportEntry import, bool exportAll, List<string> exportNames)
		{
			if (import.Kind == Parser.Module.ImportKind.Wildcard && !exportAll && exportNames.Count == 0)
				return true;
			return exportAll;
		}

		private HashSet<string> GetExplicitAliases(Parser.Module.ImportEntry import)
		{
			var explicitAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			switch (import.Kind)
			{
				case Parser.Module.ImportKind.ModuleAlias:
					if (!string.IsNullOrWhiteSpace(import.Alias))
						explicitAliases.Add(parser.NormalizeIdentifier(import.Alias, eNameCase.Lower));
					break;
				case Parser.Module.ImportKind.Named:
					foreach (var spec in import.Specifiers)
						explicitAliases.Add(parser.NormalizeIdentifier(spec.Alias ?? spec.Name, eNameCase.Lower));
					break;
			}
			return explicitAliases;
		}

		private static ExportKind GetDefaultExportKind(Parser.Module targetModule)
		{
			return targetModule.DefaultExport switch
			{
				Parser.Module.DefaultExportKind.Function => ExportKind.Function,
				Parser.Module.DefaultExportKind.Type => ExportKind.Type,
				_ => ExportKind.Variable
			};
		}

		private static ExportKind GetExternalExportKind(ExternalMemberKind kind)
		{
			return kind switch
			{
				ExternalMemberKind.Method => ExportKind.Function,
				ExternalMemberKind.NestedType => ExportKind.Type,
				_ => ExportKind.Variable
			};
		}

		private ExportKind ResolveExternalExportKind(Type externalType, string name)
		{
			if (TryResolveExternalMember(externalType, name, out var member))
				return GetExternalExportKind(member.Kind);
			return ExportKind.None;
		}

		private static IEnumerable<(string Name, ExportKind Kind)> EnumerateScriptExports(Parser.Module targetModule)
		{
			foreach (var name in targetModule.ExportedFuncs)
				yield return (name, ExportKind.Function);
			foreach (var name in targetModule.ExportedTypes)
			{
				if (name.Contains('.', StringComparison.Ordinal))
					continue;
				yield return (name, ExportKind.Type);
			}
			foreach (var name in targetModule.ExportedVars)
				yield return (name, ExportKind.Variable);
		}

		private static IEnumerable<(string Name, ExportKind Kind)> EnumerateExternalExports(Type externalType)
		{
			foreach (var member in EnumerateExternalMembers(externalType))
				yield return (member.Name, GetExternalExportKind(member.Kind));
		}

		private bool ResolveExportImport(
			Parser.Module module,
			Parser.Module.ImportEntry import,
			Func<string, ExportKind> resolveNamedKind,
			IEnumerable<(string Name, ExportKind Kind)> exportAllEntries,
			ExportKind moduleAliasKind)
		{
			var changed = false;
			var exportNames = import.ExportNames ?? new List<string>();
			var exportAll = ShouldExportAll(import, import.ExportAll, exportNames);
			var explicitAliases = GetExplicitAliases(import);

			if (import.Kind == Parser.Module.ImportKind.ModuleAlias && !string.IsNullOrWhiteSpace(import.Alias))
				changed |= MarkExported(module, import.Alias, moduleAliasKind);

			if (import.Kind == Parser.Module.ImportKind.Named)
			{
				foreach (var spec in import.Specifiers)
				{
					var alias = spec.Alias ?? spec.Name;
					var kind = resolveNamedKind(spec.Name);
					if (kind != ExportKind.None)
						changed |= MarkExported(module, alias, kind);
				}
			}

			if (exportAll)
			{
				foreach (var entry in exportAllEntries)
				{
					if (explicitAliases.Contains(parser.NormalizeIdentifier(entry.Name, eNameCase.Lower)))
						continue;
					changed |= MarkExported(module, entry.Name, entry.Kind);
				}
			}

			if (exportNames.Count > 0)
			{
				foreach (var name in exportNames)
				{
					if (explicitAliases.Contains(parser.NormalizeIdentifier(name, eNameCase.Lower)))
						continue;
					var kind = resolveNamedKind(name);
					if (kind != ExportKind.None)
						changed |= MarkExported(module, name, kind);
				}
			}

			return changed;
		}

		private void EmitImportsForModule(Parser.Module module)
		{
			var wildcardCollector = new WildcardImportCollector(this, module);

			foreach (var import in module.Imports)
			{
				var exportMember = import.IsExported;
				var exportNames = import.ExportNames ?? new List<string>();
				var exportAll = ShouldExportAll(import, import.ExportAll, exportNames);
				var explicitAliases = GetExplicitAliases(import);

				if (!parser.Modules.TryGetValue(import.ModuleName, out var targetModule))
				{
					if (TryResolveExternalModuleType(import.ModuleName, out var externalType, out var externalTypeName))
					{
						switch (import.Kind)
						{
							case Parser.Module.ImportKind.ModuleAlias:
								if (!string.IsNullOrWhiteSpace(import.Alias))
									AddModuleAliasImport(module, externalTypeName, import.Alias ?? import.ModuleName, exportMember);
								break;
							case Parser.Module.ImportKind.Named:
								foreach (var spec in import.Specifiers)
									AddExternalNamedImport(module, externalType, spec.Name, spec.Alias ?? spec.Name, exportMember);
								break;
							case Parser.Module.ImportKind.Wildcard:
								wildcardCollector.AddExternal(externalType, exportMember);
								break;
						}

						EmitExportedImports(
							module,
							exportMember,
							exportAll,
							exportNames,
							explicitAliases,
							() => wildcardCollector.AddExternal(externalType, exportMember: true),
							name => AddExternalNamedImport(module, externalType, name, name, exportMember: true)
						);
						continue;
					}
					throw new ParseException($"Unknown module '{import.ModuleName}'.");
				}

				switch (import.Kind)
				{
					case Parser.Module.ImportKind.ModuleAlias:
						if (!string.IsNullOrWhiteSpace(import.Alias))
							AddModuleAliasImport(module, targetModule, import.Alias ?? import.ModuleName, exportMember);
						break;
					case Parser.Module.ImportKind.Named:
						foreach (var spec in import.Specifiers)
							AddNamedImport(module, targetModule, spec.Name, spec.Alias ?? spec.Name, exportMember);
						break;
					case Parser.Module.ImportKind.Wildcard:
						wildcardCollector.AddScript(targetModule, exportMember);
						break;
				}

				EmitExportedImports(
					module,
					exportMember,
					exportAll,
					exportNames,
					explicitAliases,
					() => wildcardCollector.AddScript(targetModule, exportMember: true),
					name => AddNamedImport(module, targetModule, name, name, exportMember: true)
				);
			}

			wildcardCollector.Emit();
		}

		private void EmitExportedImports(
			Parser.Module module,
			bool exportMember,
			bool exportAll,
			List<string> exportNames,
			HashSet<string> explicitAliases,
			Action addWildcardExport,
			Action<string> addNamedExport)
		{
			if (!exportMember)
				return;

			if (exportAll)
				addWildcardExport();

			if (exportNames.Count > 0)
			{
				foreach (var name in exportNames)
				{
					var aliasName = parser.NormalizeIdentifier(name, eNameCase.Lower);
					if (explicitAliases.Contains(aliasName) || HasMemberName(module.ModuleClass, aliasName))
						continue;
					addNamedExport(name);
				}
			}
		}

		private static bool TryResolveExternalModuleType(string moduleName, out Type type, out string typeName)
		{
			typeName = null;
			type = null;
			if (!Script.TheScript.ReflectionsData.stringToTypes.TryGetValue(moduleName, out type))
				return false;
			if (!typeof(Keysharp.Core.Common.ObjectBase.Module).IsAssignableFrom(type))
				return false;

			typeName = (type.FullName ?? type.Name).Replace('+', '.');
			return true;
		}

		private void AddModuleAliasImport(Parser.Module module, Parser.Module targetModule, string alias, bool exportMember)
		{
			if (string.IsNullOrWhiteSpace(alias))
				return;

			var aliasName = parser.NormalizeIdentifier(alias, eNameCase.Lower);
			EnsureImportNameAvailable(module, aliasName, alias);

			ExpressionSyntax targetAccess;
			if (targetModule.DefaultExport == Parser.Module.DefaultExportKind.None)
			{
				targetAccess = GenerateItemAccess(
					VarsNameSyntax,
					SyntaxFactory.IdentifierName("Statics"),
					SyntaxFactory.TypeOfExpression(
						CreateQualifiedName($"{Keywords.MainClassName}.{targetModule.ModuleClassName}")
					)
				);
			}
			else
			{
				var memberName = parser.ToValidIdentifier(
					parser.NormalizeIdentifier(targetModule.DefaultExportName, eNameCase.Lower)
				);
				targetAccess = SyntaxFactory.MemberAccessExpression(
					SyntaxKind.SimpleMemberAccessExpression,
					SyntaxFactory.IdentifierName(targetModule.ModuleClassName),
					SyntaxFactory.IdentifierName(memberName)
				);
			}

			if (targetModule.DefaultExport == Parser.Module.DefaultExportKind.Function)
			{
				var field = CreateImportField(aliasName, targetAccess);
				if (exportMember)
				{
					MarkExported(module, alias, ExportKind.Function);
					field = Parser.WithExportAttribute(field);
				}
				module.ModuleClass.Body.Add(field);
				return;
			}

			var importProp = CreateImportProperty(aliasName, targetAccess, includeSetter: false);
			if (exportMember)
			{
				MarkExported(module, alias, targetModule.DefaultExport == Parser.Module.DefaultExportKind.Type ? ExportKind.Type : ExportKind.Variable);
				importProp = Parser.WithExportAttribute(importProp);
			}
			module.ModuleClass.Body.Add(importProp);
		}

		private void AddModuleAliasImport(Parser.Module module, string targetModuleTypeName, string alias, bool exportMember)
		{
			if (string.IsNullOrWhiteSpace(alias))
				return;

			var aliasName = parser.NormalizeIdentifier(alias, eNameCase.Lower);
			EnsureImportNameAvailable(module, aliasName, alias);

			var moduleStatic = GenerateItemAccess(
				VarsNameSyntax,
				SyntaxFactory.IdentifierName("Statics"),
				SyntaxFactory.TypeOfExpression(
					CreateQualifiedName(targetModuleTypeName)
				)
			);

			var field = CreateImportField(aliasName, moduleStatic, isReadOnly: true);
			if (exportMember)
			{
				MarkExported(module, alias, ExportKind.Variable);
				field = Parser.WithExportAttribute(field);
			}
			module.ModuleClass.Body.Add(field);
		}

		private void AddExternalNamedImport(Parser.Module module, Type targetType, string importName, string alias, bool exportMember)
		{
			if (!TryResolveExternalMember(targetType, importName, out var member))
				throw new ParseException($"Unknown import '{importName}' from module '{targetType.Name}'.");

			var aliasName = parser.NormalizeIdentifier(alias, eNameCase.Lower);
			EnsureImportNameAvailable(module, aliasName, alias);

			var kind = GetExternalExportKind(member.Kind);
			var targetAccess = BuildExternalMemberAccess(targetType, member);
			var includeSetter = kind == ExportKind.Variable && member.CanSet;
			AddImportMemberCore(module, importName, aliasName, alias, kind, targetAccess, includeSetter, exportMember);
		}

		private enum ExternalMemberKind
		{
			Method,
			NestedType,
			Property,
			Field
		}

		private sealed class ExternalMember
		{
			public string Name;
			public ExternalMemberKind Kind;
			public bool CanSet;
			public Type NestedType;
		}

		private static IEnumerable<ExternalMember> EnumerateExternalMembers(Type type)
		{
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var rd = Script.TheScript.ReflectionsData;

			Keysharp.Core.Common.Invoke.Reflections.FindAndCacheStaticMethod(type, "", -1);
			Keysharp.Core.Common.Invoke.Reflections.FindAndCacheProperty(type, "", -1);
			Keysharp.Core.Common.Invoke.Reflections.FindAndCacheField(type, "");

			if (rd.typeToStringStaticMethods.TryGetValue(type, out var methodsByName))
			{
				foreach (var kvp in methodsByName)
				{
					var name = kvp.Key;
					if (!seen.Add(name))
						continue;
					var holders = kvp.Value.Values;
					if (holders.Any(mph => mph?.mi?.GetCustomAttribute<PublicHiddenFromUser>() != null))
						continue;
					if (holders.All(mph => mph?.mi == null || mph.mi.IsSpecialName))
						continue;
					yield return new ExternalMember { Name = name, Kind = ExternalMemberKind.Method, CanSet = false };
				}
			}

			foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
			{
				if (nested.GetCustomAttribute<PublicHiddenFromUser>() != null)
					continue;
				if (!seen.Add(nested.Name))
					continue;

				yield return new ExternalMember { Name = nested.Name, Kind = ExternalMemberKind.NestedType, CanSet = false, NestedType = nested };
			}

			if (rd.typeToStringProperties.TryGetValue(type, out var propsByName))
			{
				foreach (var kvp in propsByName)
				{
					var name = kvp.Key;
					if (!seen.Add(name))
						continue;
					var mph = kvp.Value.Values.FirstOrDefault();
					if (mph?.pi?.GetCustomAttribute<PublicHiddenFromUser>() != null)
						continue;
					var canSet = mph?.pi?.CanWrite ?? false;
					yield return new ExternalMember { Name = name, Kind = ExternalMemberKind.Property, CanSet = canSet };
				}
			}

			if (rd.staticFields.TryGetValue(type, out var fieldsByName))
			{
				foreach (var kvp in fieldsByName)
				{
					var name = kvp.Key;
					if (!seen.Add(name))
						continue;
					var field = kvp.Value;
					if (field.GetCustomAttribute<PublicHiddenFromUser>() != null)
						continue;
					var canSet = !(field.IsInitOnly || field.IsLiteral);
					yield return new ExternalMember { Name = name, Kind = ExternalMemberKind.Field, CanSet = canSet };
				}
			}
		}

		private static bool TryResolveExternalMember(Type type, string importName, out ExternalMember member)
		{
			foreach (var entry in EnumerateExternalMembers(type))
			{
				if (entry.Name.Equals(importName, StringComparison.OrdinalIgnoreCase))
				{
					member = entry;
					return true;
				}
			}

			member = null;
			return false;
		}

		private ExpressionSyntax BuildExternalMemberAccess(Type targetType, ExternalMember member)
		{
			var targetTypeName = (targetType.FullName ?? targetType.Name).Replace('+', '.');
			switch (member.Kind)
			{
				case ExternalMemberKind.Method:
					if (TheScript.ReflectionsData.stringToTypeBuiltInMethods.TryGetValue(member.Name, out var dict) &&
						dict.TryGetValue(targetType, out var mis) && mis.Count > 0 && mis.First().Value.mi is MethodInfo mi)
					{
						var methodAccess = SyntaxFactory.MemberAccessExpression(
							SyntaxKind.SimpleMemberAccessExpression,
							CreateQualifiedName((mi.DeclaringType?.FullName ?? mi.DeclaringType?.Name).Replace('+', '.')),
							SyntaxFactory.IdentifierName(mi.Name)
						);
						var delegateCast = SyntaxFactory.CastExpression(
							CreateQualifiedName("System.Delegate"),
							methodAccess
						);
						return SyntaxFactory.InvocationExpression(
							CreateMemberAccess("Keysharp.Core.Functions", "Func")
						).WithArgumentList(
							CreateArgumentList(delegateCast)
						);
					}
					return SyntaxFactory.InvocationExpression(
						CreateMemberAccess("Keysharp.Core.Functions", "Func")
					).WithArgumentList(
						CreateArgumentList(
							CreateStringLiteral(member.Name),
							SyntaxFactory.TypeOfExpression(CreateQualifiedName(targetTypeName))
						)
					);
				case ExternalMemberKind.NestedType:
					var nestedTypeName = ((member.NestedType?.FullName ?? member.Name).Replace('+', '.'));
					return GenerateItemAccess(
						VarsNameSyntax,
						SyntaxFactory.IdentifierName("Statics"),
						SyntaxFactory.TypeOfExpression(CreateQualifiedName(nestedTypeName))
					);
				case ExternalMemberKind.Property:
				case ExternalMemberKind.Field:
				default:
					return SyntaxFactory.MemberAccessExpression(
						SyntaxKind.SimpleMemberAccessExpression,
						CreateQualifiedName(targetTypeName),
						SyntaxFactory.IdentifierName(member.Name)
					);
			}
		}

		private static FieldDeclarationSyntax CreateImportField(string name, ExpressionSyntax valueExpression, bool isReadOnly = false)
		{
			var modifiers = isReadOnly
				? new[] { SyntaxKind.PublicKeyword, SyntaxKind.StaticKeyword, SyntaxKind.ReadOnlyKeyword }
				: new[] { SyntaxKind.PublicKeyword, SyntaxKind.StaticKeyword };
			return CreateFieldDeclaration(
				modifiers,
				PredefinedKeywords.ObjectType,
				name,
				valueExpression
			);
		}

		private void AddNamedImport(Parser.Module module, Parser.Module targetModule, string importName, string alias, bool exportMember)
		{
			var targetInfo = ResolveScriptExport(targetModule, importName);
			if (targetInfo == ExportKind.None)
				throw new ParseException($"Import '{importName}' is not exported by module '{targetModule.Name}'.");

			var aliasName = parser.NormalizeIdentifier(alias, eNameCase.Lower);
			EnsureImportNameAvailable(module, aliasName, alias);

			var targetName = parser.NormalizeIdentifier(importName, eNameCase.Lower);
			var targetAccess = SyntaxFactory.MemberAccessExpression(
				SyntaxKind.SimpleMemberAccessExpression,
				SyntaxFactory.IdentifierName(targetModule.ModuleClassName),
				SyntaxFactory.IdentifierName(targetName)
			);

			var includeSetter = targetInfo == ExportKind.Variable;
			AddImportMemberCore(module, importName, aliasName, alias, targetInfo, targetAccess, includeSetter, exportMember);
		}

		private void AddImportMemberCore(
			Parser.Module module,
			string importName,
			string aliasName,
			string aliasRaw,
			ExportKind kind,
			ExpressionSyntax targetAccess,
			bool includeSetter,
			bool exportMember)
		{
			if (kind == ExportKind.Function)
			{
				var field = CreateImportField(aliasName, targetAccess);
				if (exportMember)
				{
					MarkExported(module, aliasRaw, ExportKind.Function);
					field = Parser.WithExportAttribute(field);
				}
				module.ModuleClass.Body.Add(field);
				return;
			}

			var prop = CreateImportProperty(aliasName, targetAccess, includeSetter: includeSetter);
			if (exportMember)
			{
				var exportKind = kind == ExportKind.Type ? ExportKind.Type : ExportKind.Variable;
				MarkExported(module, aliasRaw, exportKind);
				prop = Parser.WithExportAttribute(prop);
			}
			module.ModuleClass.Body.Add(prop);
		}

		private sealed class WildcardImportEntry
		{
			public string AliasName;
			public string RawName;
			public ExportKind Kind;
			public bool ExportMember;
			public Parser.Module TargetModule;
			public Type ExternalType;
			public ExternalMember ExternalMember;
		}

		private sealed class WildcardImportCollector
		{
			private readonly VisitMain owner;
			private readonly Parser.Module module;
			private readonly List<WildcardImportEntry> entries = new();
			private readonly Dictionary<string, int> indices = new(StringComparer.OrdinalIgnoreCase);

			internal WildcardImportCollector(VisitMain owner, Parser.Module module)
			{
				this.owner = owner;
				this.module = module;
			}

			internal void AddScript(Parser.Module targetModule, bool exportMember)
			{
				foreach (var name in targetModule.ExportedFuncs)
					AddEntry(CreateScriptEntry(targetModule, name, ExportKind.Function, exportMember));

				foreach (var name in targetModule.ExportedTypes)
				{
					if (name.Contains('.', StringComparison.Ordinal))
						continue;
					AddEntry(CreateScriptEntry(targetModule, name, ExportKind.Type, exportMember));
				}

				foreach (var name in targetModule.ExportedVars)
					AddEntry(CreateScriptEntry(targetModule, name, ExportKind.Variable, exportMember));
			}

			internal void AddExternal(Type externalType, bool exportMember)
			{
				foreach (var member in EnumerateExternalMembers(externalType))
				{
					var kind = GetExternalExportKind(member.Kind);
					var aliasName = owner.parser.NormalizeIdentifier(member.Name, eNameCase.Lower);
					AddEntry(new WildcardImportEntry
					{
						AliasName = aliasName,
						RawName = member.Name,
						Kind = kind,
						ExportMember = exportMember,
						ExternalType = externalType,
						ExternalMember = member
					});
				}
			}

			internal void Emit()
			{
				foreach (var entry in entries)
					owner.EmitWildcardEntry(module, entry);
			}

			private WildcardImportEntry CreateScriptEntry(Parser.Module targetModule, string name, ExportKind kind, bool exportMember)
			{
				return new WildcardImportEntry
				{
					AliasName = owner.parser.NormalizeIdentifier(name, eNameCase.Lower),
					RawName = name,
					Kind = kind,
					ExportMember = exportMember,
					TargetModule = targetModule
				};
			}

			private void AddEntry(WildcardImportEntry entry)
			{
				if (owner.HasImportNameConflict(module, entry.AliasName, entry.RawName))
					return;

				if (indices.TryGetValue(entry.AliasName, out var idx))
					entries[idx] = entry;
				else
				{
					indices[entry.AliasName] = entries.Count;
					entries.Add(entry);
				}
			}
		}

		private void EmitWildcardEntry(Parser.Module module, WildcardImportEntry entry)
		{
			ExpressionSyntax targetAccess;
			bool includeSetter;

			if (entry.TargetModule != null)
			{
				var targetName = parser.NormalizeIdentifier(entry.RawName, eNameCase.Lower);
				targetAccess = SyntaxFactory.MemberAccessExpression(
					SyntaxKind.SimpleMemberAccessExpression,
					SyntaxFactory.IdentifierName(entry.TargetModule.ModuleClassName),
					SyntaxFactory.IdentifierName(targetName)
				);
				includeSetter = entry.Kind == ExportKind.Variable;
			}
			else
			{
				targetAccess = BuildExternalMemberAccess(entry.ExternalType, entry.ExternalMember);
				includeSetter = entry.Kind == ExportKind.Variable && entry.ExternalMember.CanSet;
			}

			AddImportMemberCore(module, entry.RawName, entry.AliasName, entry.RawName, entry.Kind, targetAccess, includeSetter, entry.ExportMember);
		}

		private ExportKind ResolveScriptExport(Parser.Module targetModule, string importName)
		{
			var info = parser.GetIdentifierInfo(importName);
			if (targetModule.ExportedFuncs.Contains(info.Trimmed))
				return ExportKind.Function;
			var typeName = parser.NormalizeIdentifier(info.Trimmed, eNameCase.Title);
			if (targetModule.ExportedTypes.Contains(typeName))
				return ExportKind.Type;
			if (targetModule.ExportedVars.Contains(info.BaseLower))
				return ExportKind.Variable;

			return ExportKind.None;
		}

		private bool MarkExported(Parser.Module module, string aliasRaw, ExportKind kind)
		{
			if (string.IsNullOrWhiteSpace(aliasRaw))
				return false;

			var info = parser.GetIdentifierInfo(aliasRaw);
			switch (kind)
			{
				case ExportKind.Function:
					return module.ExportedFuncs.Add(info.Trimmed);
				case ExportKind.Type:
					return module.ExportedTypes.Add(parser.NormalizeIdentifier(info.Trimmed, eNameCase.Title));
				case ExportKind.Variable:
					return module.ExportedVars.Add(info.BaseLower);
			}
			return false;
		}

		private bool HasImportNameConflict(Parser.Module module, string aliasName, string aliasRaw)
		{
			var raw = string.IsNullOrWhiteSpace(aliasRaw) ? aliasName : aliasRaw;
			var info = parser.GetIdentifierInfo(raw);
			var baseLower = info.BaseLower;

			return HasMemberName(module.ModuleClass, aliasName)
				|| module.GlobalVars.Contains(aliasName)
				|| module.GlobalVars.Contains(baseLower)
				|| module.UserFuncs.Contains(info.Trimmed)
				|| module.UserFuncs.Contains(baseLower)
				|| module.UserTypes.ContainsKey(parser.NormalizeIdentifier(info.Trimmed, eNameCase.Title))
				|| module.UserTypes.ContainsKey(info.Trimmed)
				|| module.UserTypes.ContainsKey(baseLower);
		}

		private void EnsureImportNameAvailable(Parser.Module module, string aliasName, string aliasRaw)
		{
			if (HasImportNameConflict(module, aliasName, aliasRaw))
			{
				var raw = string.IsNullOrWhiteSpace(aliasRaw) ? aliasName : aliasRaw;
				var info = parser.GetIdentifierInfo(raw);
				throw new ParseException($"Import name '{info.Trimmed}' conflicts with an existing declaration in module '{module.Name}'.");
			}
		}

		private bool TryResolveImplicitImport(Parser.IdentifierInfo info, out ExpressionSyntax expression)
		{
			expression = null;
			var imports = parser.currentModule?.Imports;
			if (imports == null || imports.Count == 0)
				return false;

			var importName = info.Trimmed;

			for (int i = imports.Count - 1; i >= 0; i--)
			{
				var import = imports[i];
				if (import.Kind != Parser.Module.ImportKind.Wildcard)
					continue;

				if (!parser.Modules.TryGetValue(import.ModuleName, out var targetModule))
				{
					if (TryResolveExternalModuleType(import.ModuleName, out var externalType, out _)
						&& TryResolveExternalMember(externalType, importName, out var member))
					{
						if (member.Kind == ExternalMemberKind.Method)
						{
							var aliasName = parser.NormalizeIdentifier(importName, eNameCase.Lower);
							if (!HasMemberName(parser.currentModule.ModuleClass, aliasName))
							{
								var field = CreateImportField(aliasName, BuildExternalMemberAccess(externalType, member));
								parser.currentModule.ModuleClass.Body.Add(field);
							}
							expression = SyntaxFactory.IdentifierName(aliasName);
							return true;
						}

						expression = BuildExternalMemberAccess(externalType, member);
						return true;
					}
					continue;
				}

				var exportKind = ResolveScriptExport(targetModule, importName);
				if (exportKind == ExportKind.None)
					continue;

				var targetName = parser.NormalizeIdentifier(importName, eNameCase.Lower);
				var targetAccess = SyntaxFactory.MemberAccessExpression(
					SyntaxKind.SimpleMemberAccessExpression,
					SyntaxFactory.IdentifierName(targetModule.ModuleClassName),
					SyntaxFactory.IdentifierName(targetName)
				);

				if (exportKind == ExportKind.Function || exportKind == ExportKind.Type)
				{
					var aliasName = targetName;
					if (!HasMemberName(parser.currentModule.ModuleClass, aliasName))
					{
						var field = CreateImportField(aliasName, targetAccess);
						parser.currentModule.ModuleClass.Body.Add(field);
					}
					expression = SyntaxFactory.IdentifierName(aliasName);
					return true;
				}

				expression = targetAccess;
				return true;
			}

			return false;
		}

		private static bool HasMemberName(Parser.Class cls, string name)
		{
			foreach (var member in cls.Body)
			{
				if (member is FieldDeclarationSyntax field)
				{
					foreach (var variable in field.Declaration.Variables)
					{
						if (variable.Identifier.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
							return true;
					}
				}
				else if (member is PropertyDeclarationSyntax prop)
				{
					if (prop.Identifier.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
						return true;
				}
			}
			return false;
		}

		private void SetDefaultExport(string name, Parser.Module.DefaultExportKind kind, ParserRuleContext context)
		{
			if (parser.currentModule.DefaultExport != Parser.Module.DefaultExportKind.None)
			{
				if (parser.currentModule.DefaultExport == kind
					&& string.Equals(parser.currentModule.DefaultExportName, name, StringComparison.OrdinalIgnoreCase))
					return;
				throw new ParseException("Only one default export is allowed per module.", context);
			}
			parser.currentModule.DefaultExport = kind;
			parser.currentModule.DefaultExportName = name;
		}

		private static PropertyDeclarationSyntax CreateImportProperty(string name, ExpressionSyntax valueExpression, bool includeSetter)
		{
			var prop = SyntaxFactory.PropertyDeclaration(PredefinedKeywords.ObjectType, SyntaxFactory.Identifier(name))
				.AddModifiers(PredefinedKeywords.PublicToken, PredefinedKeywords.StaticToken);

			if (!includeSetter)
			{
				return prop
					.WithExpressionBody(SyntaxFactory.ArrowExpressionClause(valueExpression))
					.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
			}

			var getAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
				.WithExpressionBody(SyntaxFactory.ArrowExpressionClause(valueExpression))
				.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

			var setAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
				.WithExpressionBody(
					SyntaxFactory.ArrowExpressionClause(
						SyntaxFactory.AssignmentExpression(
							SyntaxKind.SimpleAssignmentExpression,
							valueExpression,
							SyntaxFactory.IdentifierName("value")
						)
					)
				)
				.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

			return prop.WithAccessorList(
				SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { getAccessor, setAccessor }))
			);
		}
	}
}
