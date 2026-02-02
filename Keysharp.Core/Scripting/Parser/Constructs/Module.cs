using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Keysharp.Scripting
{
	internal partial class Parser
	{
		public sealed class Module
		{
			public enum DefaultExportKind
			{
				None,
				Function,
				Type
			}
			public enum ExportKind
			{
				None,
				Function,
				Type,
				Variable
			}
			public enum ImportKind
			{
				ModuleAlias,
				Named,
				Wildcard
			}

			public sealed class ImportSpecifier
			{
				public string Name;
				public string Alias;
			}

			public sealed class ImportEntry
			{
				public ImportKind Kind;
				public string ModuleName;
				public string Alias;
				public bool IsQuoted;
				public bool IsExported;
				public bool ExportAll;
				public List<string> ExportNames = new();
				public List<ImportSpecifier> Specifiers = new();
			}

			public string Name { get; }
			public string ModuleClassName;
			public Class ModuleClass;

			public List<ImportEntry> Imports = new();
			public bool ImportsEmitted = false;

			public List<MemberDeclarationSyntax> DeclaredTopLevelClasses = new();
			public Function AutoExecFunc;
			public Function CurrentFunc;
			public List<StatementSyntax> DHHR = new();
			public uint HotIfCount = 0;
			public bool HotIfActive = false;
			public uint HotkeyCount = 0;
			public uint HotstringCount = 0;
			public bool IsHotkeyDefinition = false;

			public HashSet<string> GlobalVars = [];
			public HashSet<string> AccessibleVars = [];

			public HashSet<string> ExportedVars = new(StringComparer.OrdinalIgnoreCase);
			public HashSet<string> ExportedFuncs = new(StringComparer.OrdinalIgnoreCase);
			public HashSet<string> ExportedTypes = new(StringComparer.OrdinalIgnoreCase);
			public string DefaultExportName;
			public DefaultExportKind DefaultExport = DefaultExportKind.None;

			public Dictionary<string, string> AllTypes = new(StringComparer.OrdinalIgnoreCase);
			public Dictionary<string, string> UserTypes = new(StringComparer.OrdinalIgnoreCase);
			public HashSet<string> UserFuncs = new(StringComparer.OrdinalIgnoreCase);

			public uint LoopDepth = 0;
			public uint FunctionDepth = 0;
			public uint ClassDepth = 0;
			public uint TryDepth = 0;
			public uint TempVarCount = 0;
			public uint LambdaCount = 0;

			public Stack<(Function, HashSet<string>)> FunctionStack = new();
			public Stack<Class> ClassStack = new();
			public Stack<Loop> LoopStack = new();

			public Class CurrentClass;

			public Module(string name)
			{
				Name = name;
			}
		}
	}
}
