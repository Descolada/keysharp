using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Keysharp.Runtime;
using Keysharp.Internals.Input.Keyboard;
using Keysharp.Internals.Input.Mouse;

namespace Keysharp.Parsing.Syntax
{
	/// <summary>
	/// Lowering pass: AST -> Roslyn CompilationUnit, built directly with SyntaxFactory (no text
	/// round-trips), matching the conventions the Keysharp runtime expects (Script.Invoke /
	/// Script.&lt;Operator&gt; / Script.IfTest, FN_ methods, Func-bound fields). Reuses shared
	/// infrastructure (the builtin registry, runtime operator methods, CompilerHelper.Compile).
	///
	/// Everything is emitted fully-qualified, so no `using` directives are produced and the compiler
	/// binds the structural tree directly.
	///
	/// Identifier model: in AutoHotkey a name is one case-insensitive slot, and a function name IS a
	/// variable holding the FuncObj — so every global reference lowers to a single static field named
	/// by the lowercased identifier. Locals (params + names assigned in a function) become hoisted
	/// method locals; names go through <see cref="NameMangler"/>.
	/// </summary>
	internal sealed class Lowerer
	{
		public readonly List<string> Diagnostics = new();

		// Global slot table: lowercased name -> emitted C# field identifier (keyword-escaped).
		private readonly Dictionary<string, string> _fields = new(System.StringComparer.Ordinal);
		// Names resolved INLINE to a fresh expression each reference (e.g. `#import __Main` self-import → `new __Main()`).
		private readonly Dictionary<string, System.Func<ExpressionSyntax>> _inlineAliases = new(System.StringComparer.Ordinal);
		private readonly List<MemberDeclarationSyntax> _fieldDecls = new();
		private readonly Dictionary<string, string> _userFuncByLower = new(System.StringComparer.Ordinal);
		private readonly Dictionary<string, string> _userClassByLower = new(System.StringComparer.Ordinal);
		private bool _inMethod;
		private string _currentClassBase;   // base type name of the class being lowered (for `super`)
		private string _structTypeName;     // C# type name of the struct being lowered (for typed-field DefineProp)
		private bool _currentMethodStatic;   // the method being lowered is static (super -> Statics vs Prototypes)
		private int _flowCounter;

		// Enclosing-loop stack for break/continue with a level/label. Each loop gets an id (its KS_e<id> base); the
		// optional source Label is the `name:` that preceded the loop. NeedsNext/NeedsEnd record whether a targeted
		// continue/break referenced this loop so we only emit the (otherwise-unused) `_next`/`_end` labels when needed.
		private sealed class LoopFrame { public int Id; public string Label; public bool NeedsNext; public bool NeedsEnd; }
		private readonly List<LoopFrame> _loopFrames = new();
		private string _pendingLoopLabel;   // a `name:` immediately preceding the next loop (its break/continue target)
		// Stack of label name-sets for the currently-open blocks (a real C# `switch` contributes one set shared by all
		// its cases). A `goto` emits a real C# goto only when its target is in some active scope (a valid C# jump).
		private readonly List<HashSet<string>> _labelScopes = new();
		private bool _inDerefFunc;   // inside a deref-using function: %name% routes through the local GetVar/SetVar functions
		private int _lambdaCounter;
		private readonly List<MemberDeclarationSyntax> _pendingLambdas = new();   // anonymous fat-arrow functions
		// C# local functions emitted for fat-arrows in the current callable scope — flushed into that scope's body so
		// they capture @this / enclosing locals via normal C# closure semantics (matches the canonical).
		private List<StatementSyntax> _pendingScopeFuncs = new();
		// Local FuncObj/Closure var declarations for NAMED nested functions, prepended to the scope body so the name is
		// bound before any (possibly forward-referenced) call — AHK hoists nested functions.
		private List<StatementSyntax> _pendingScopeClosureInits = new();
		// Names of bare (non-static) nested functions declared anywhere in the CURRENT scope's body. They become local
		// closure variables (hoisted), so even a forward-referenced call resolves to the local — NOT a module field.
		private HashSet<string> _scopeClosureNames = new(System.StringComparer.Ordinal);
		// SL_ static-local field name -> its index in _fieldDecls, so a constant-valued `static x := 1+2` can rewrite the
		// field's initializer to the folded literal (`SL_… = 3L`) instead of a runtime InitStaticVariable.
		private readonly Dictionary<string, int> _staticFieldDeclIdx = new(System.StringComparer.Ordinal);
		private readonly HashSet<string> _emittedFuncImpls = new();   // guards against duplicate hoisted nested functions
		private readonly List<Type> _wildcardModules = new();   // `#import "Mod" { * }` types — members resolved on demand
		private readonly List<string> _classFieldIds = new();   // class slot fields — referenced at auto-exec start to force static init
		private readonly List<(string Attr, string Value)> _asmAttributes = new();   // #Assembly* directives -> [assembly: …]

		// Compatibility mode (`#Requires AutoHotkey v2.0`/`v2.1`): lexically scoped per module/class/function. Controls
		// the default/implicit return value (v2.0 → "", v2.1 → unset) and the per-method [CompatibilityMode] attribute.
		private static readonly Semver.SemVersion[] CompatCandidates = { new(2, 0, 0), new(2, 1, 0) };
		private Semver.SemVersion _moduleCompat = Keysharp.Runtime.Script.DefaultCompatibilityVersion;   // current module's mode
		private Semver.SemVersion _currentCompat = Keysharp.Runtime.Script.DefaultCompatibilityVersion;  // current scope's mode
		private string _currentModuleClass = "__Main";   // the module class being lowered — used to qualify module-level
														 // field references from inside a (nested) class method (`Program.<Mod>.field`)
		// Hotkey/hotstring/remap support: extra __Main methods (the trigger callbacks) and the DHHR list
		// (Directives/Hotkeys/Hotstrings/Remaps registration calls) emitted into Program.AutoExecSection.
		private readonly List<MemberDeclarationSyntax> _hotMembers = new();
		private readonly List<StatementSyntax> _dhhr = new();
		private bool _persistent;            // a hotkey/hotstring makes the script persistent
		private string _singleInstanceMode;  // #SingleInstance mode (Force/Ignore/Prompt/Off), null = directive absent
		private string _hookMutexName;       // #HookMutexName argument (passed to the Script constructor), null = absent
		// #Warn config: per-type output mode ("MsgBox"/"StdOut"/"OutputDebug") or null when that warning is off.
		// AHK enables VarUnset+Unreachable by default; here warnings are OPT-IN (all off until a `#Warn` directive
		// turns them on), so existing scripts are unaffected by the compile-then-run model. `_warnDefaultMode` is the
		// program-wide default mode applied by `#Warn On` / a bare `#Warn <Mode>` (default MsgBox, like AHK).
		private string _warnVarUnset = null, _warnUnreachable = null, _warnLocalSameAsGlobal = null;
		private string _warnDefaultMode = "MsgBox";
		private readonly List<(string mode, int line, string desc)> _warnings = new();
		private int _hotCount;
		private readonly HashSet<string> _exportedNames = new(System.StringComparer.OrdinalIgnoreCase);   // names marked `export`

		private static string ExportName(Stmt decl) => decl switch
		{
			FunctionDecl f => f.Name,
			ClassDecl c => c.Name,
			ExpressionStmt { Expr: AssignExpr { Target: NameExpr n } } => n.Name,
			_ => ""
		};

		private HashSet<string> _locals;
		// Locals of ENCLOSING function scopes — a nested closure (fat-arrow/local fn) that references one captures it
		// by reference (it's the same C# local the C# local function closes over), rather than making its own.
		private HashSet<string> _enclosingLocals = new();
		// Enclosing-scope statics (name -> emitted module-level SL_ field), so a nested closure's deref `%name%` resolves
		// an enclosing function's static the same way it resolves its own.
		private Dictionary<string, string> _enclosingStatics = new(System.StringComparer.Ordinal);
		// Set by NameRef when it resolves a captured enclosing local or `this`; read (scoped) by LowerFatArrow to pick
		// Closure (capturing → `is Closure` true) vs Func (non-capturing) binding.
		private bool _capturedInScope;
		private HashSet<string> _forcedGlobals;
		private Dictionary<string, string> _statics;
		private HashSet<string> _byRefParams;   // &name params: reads/writes route through the VarRef's __Value
		private int _tempCounter;
		private List<string> _scopeTemps = new();   // object temps the current scope needs (declared at its top)

		private string NewTemp() { var n = "KS_temp" + (++_tempCounter); _scopeTemps.Add(n); return n; }

		// Null-conditional access `target?.<access>`: evaluate target once into a temp; if it is unset (null),
		// short-circuit the whole access (and any nested args/calls) to C# null — which reads back as unset —
		// otherwise apply `access` to the temp. Nested `?.` compose because each wrap yields null on short-circuit.
		private ExpressionSyntax NullCondWrap(Expr target, System.Func<ExpressionSyntax, ExpressionSyntax> access)
		{
			var t = NewTemp();
			return Op("MultiStatement",
				Assign(Id(t), LowerExpr(target)),
				SyntaxFactory.ParenthesizedExpression(SyntaxFactory.ConditionalExpression(
					SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, Id(t), Null),
					Null,
					access(Id(t)))));
		}

		// Case-insensitive so verbal operators carry any source casing (Is, AND, Or).
		private static readonly Dictionary<string, string> BinOps = new(System.StringComparer.OrdinalIgnoreCase)
		{
			["+"] = "Add", ["-"] = "Subtract", ["*"] = "Multiply", ["/"] = "Divide",
			["//"] = "FloorDivide", ["**"] = "Power", ["."] = "Concat",
			["&"] = "BitwiseAnd", ["|"] = "BitwiseOr", ["^"] = "BitwiseXor",
			["<<"] = "BitShiftLeft", [">>"] = "BitShiftRight", [">>>"] = "LogicalBitShiftRight",
			["<"] = "LessThan", ["<="] = "LessThanOrEqual", [">"] = "GreaterThan", [">="] = "GreaterThanOrEqual",
			["="] = "ValueEquality", ["=="] = "IdentityEquality", ["!="] = "ValueInequality", ["!=="] = "IdentityInequality",
			["~="] = "RegEx", ["!~="] = "NotRegEx",
			["&&"] = "BooleanAnd", ["||"] = "BooleanOr", ["and"] = "BooleanAnd", ["or"] = "BooleanOr",
			["is"] = "Is", ["??"] = "OrMaybe",
		};

		// The compiled script's full path ("*" for a from-string compile) and the caller-supplied startup name; used to
		// emit `MainScript.SetName(...)` so A_ScriptName/A_ScriptFullPath are correct.
		private string _scriptPath = "*";
		private string _startupName;
		private string _includeDir;   // directory for resolving file-based `#import "name"` module imports

		public CompilationUnitSyntax Build(ProgramNode prog, string name, string scriptPath = "*", string startupName = null, string includeDir = null)
		{
			_scriptPath = scriptPath ?? "*";
			_startupName = startupName;
			_includeDir = includeDir;
			// Use the dedicated multi-module path when the file defines/uses modules: a `#Module`, an `export`, or a
			// `#import "name"` that resolves to a separate <name>.ahk file. Otherwise the common single-module path is
			// completely unaffected (a builtin `#import "Ks"` stays here).
			bool IsFileImport(DirectiveStmt im)
			{
				if (_includeDir == null) return false;
				var (modName, _, _, _) = ParseImport(im.Args ?? "");
				return modName.Length > 0 && System.IO.File.Exists(System.IO.Path.Combine(_includeDir, modName + ".ahk"));
			}
			if (prog.Body.Any(s => s is DirectiveStmt d && d.Name.Equals("Module", System.StringComparison.OrdinalIgnoreCase))
				|| prog.Body.Any(s => s is ExportStmt)
				|| prog.Body.Any(s => s is DirectiveStmt im && im.Name.Equals("import", System.StringComparison.OrdinalIgnoreCase) && IsFileImport(im)))
				return BuildMultiModule(prog, name);

			// `export <decl>` outside a `#Module` file is just a normal top-level declaration.
			var body = prog.Body.Select(s => s is ExportStmt e ? e.Decl : s).ToList();
			_moduleCompat = ScanRequires(body) ?? Keysharp.Runtime.Script.DefaultCompatibilityVersion;
			_currentCompat = _moduleCompat;
			var (members, auto) = LowerProgramBody(body);
			if (Diagnostics.Count > 0) return null;
			return BuildUnit(name, members, auto);
		}

		// ---- multi-module (`#Module` / `export` / cross-module `#import`) ----

		private enum ExportK { Function, Variable, Type }
		private sealed class ModInfo
		{
			public string Name;
			public List<Stmt> Body = new();
			public List<DirectiveStmt> Imports = new();
			public Dictionary<string, ExportK> Exports = new(System.StringComparer.OrdinalIgnoreCase);
			public string DefaultName;          // user name of the `export default` member (or null)
			public bool HasExplicitExports;
		}

		// For each quoted `#import "name"` whose module isn't defined in this file (and isn't the built-in AHK module),
		// loads `<name>.ahk` from the include dir, parses it, and adds its content as a module named `name`.
		private void LoadFileModules(List<ModInfo> mods, Dictionary<string, ModInfo> byName)
		{
			if (_includeDir == null) return;
			var queue = new Queue<ModInfo>(mods);
			while (queue.Count > 0)
			{
				var m = queue.Dequeue();
				foreach (var im in m.Imports)
				{
					var (modName, _, _, _) = ParseImport(im.Args ?? "");
					if (modName.Length == 0 || byName.ContainsKey(modName) || modName.Equals("AHK", System.StringComparison.OrdinalIgnoreCase)) continue;
					var file = System.IO.Path.Combine(_includeDir, modName + ".ahk");
					if (!System.IO.File.Exists(file)) continue;
					ProgramNode fileProg;
					try { var (p, diags) = Keysharp.Parsing.Syntax.Parser.ParseWithDiagnostics(System.IO.File.ReadAllText(file), System.IO.Path.GetDirectoryName(file)); if (diags.Count > 0) continue; fileProg = p; }
					catch { continue; }
					// The file's own segments: its `__Main` (top-level) becomes the imported module `modName`; any inner
					// `#Module`s keep their own names.
					var fileMods = PartitionModules(fileProg.Body);
					foreach (var fm in fileMods)
					{
						if (fm.Name == "__Main") fm.Name = modName;
						if (byName.ContainsKey(fm.Name)) continue;
						ScanExports(fm);
						mods.Add(fm); byName[fm.Name] = fm; queue.Enqueue(fm);
					}
				}
			}
		}

		// Splits the program into module segments by `#Module Name` directives (segment 0 is `__Main`), pulling each
		// segment's `#import` directives into ModInfo.Imports.
		private static List<ModInfo> PartitionModules(List<Stmt> body)
		{
			var mods = new List<ModInfo>();
			var cur = new ModInfo { Name = "__Main" };
			mods.Add(cur);
			foreach (var s in body)
			{
				if (s is DirectiveStmt d && d.Name.Equals("Module", System.StringComparison.OrdinalIgnoreCase))
				{
					cur = new ModInfo { Name = (d.Args ?? "").Trim() };
					mods.Add(cur);
				}
				else if (s is DirectiveStmt im && im.Name.Equals("import", System.StringComparison.OrdinalIgnoreCase))
					cur.Imports.Add(im);
				else { cur.Body.Add(s); CollectNestedImports(s, cur.Imports); }
			}
			return mods;
		}

		// AHK processes `#import` directives at load time regardless of nesting, so an import inside a block / if / loop
		// (e.g. `if true { #import "M" { X as Y } }`) still binds at module scope. Recursively lift those out.
		private static void CollectNestedImports(Stmt s, List<DirectiveStmt> into)
		{
			switch (s)
			{
				case DirectiveStmt d when d.Name.Equals("import", System.StringComparison.OrdinalIgnoreCase): into.Add(d); break;
				case Block b: foreach (var c in b.Body) CollectNestedImports(c, into); break;
				case IfStmt i: CollectNestedImports(i.Then, into); if (i.Else != null) CollectNestedImports(i.Else, into); break;
				case WhileStmt w: CollectNestedImports(w.Body, into); if (w.Else != null) CollectNestedImports(w.Else, into); break;
				case LoopStmt l: CollectNestedImports(l.Body, into); if (l.Else != null) CollectNestedImports(l.Else, into); break;
				case SpecialLoopStmt sl: CollectNestedImports(sl.Body, into); if (sl.Else != null) CollectNestedImports(sl.Else, into); break;
				case ForStmt f: CollectNestedImports(f.Body, into); if (f.Else != null) CollectNestedImports(f.Else, into); break;
				case TryStmt t:
					CollectNestedImports(t.Body, into);
					foreach (var cb in t.Catches) CollectNestedImports(cb.Body, into);
					if (t.Else != null) CollectNestedImports(t.Else, into);
					if (t.Finally != null) CollectNestedImports(t.Finally, into);
					break;
			}
		}

		// Collects a module's exports. Explicit `export` declarations win; a module with none implicitly exports all of
		// its top-level functions/classes/variables (matching the AHK module semantics).
		private static void ScanExports(ModInfo m)
		{
			foreach (var s in m.Body)
			{
				if (s is ExportStmt e)
				{
					m.HasExplicitExports = true;
					var nm = ExportName(e.Decl);
					var kind = e.Decl is FunctionDecl ? ExportK.Function : e.Decl is ClassDecl ? ExportK.Type : ExportK.Variable;
					if (nm.Length > 0) m.Exports[nm] = kind;
					if (e.Default && nm.Length > 0) m.DefaultName = nm;
				}
			}
			if (m.HasExplicitExports) return;
			foreach (var s in m.Body)   // no explicit exports -> everything top-level is exportable
			{
				switch (s)
				{
					case FunctionDecl f: m.Exports[f.Name] = ExportK.Function; break;
					case ClassDecl c: m.Exports[c.Name] = ExportK.Type; break;
					case ExpressionStmt { Expr: AssignExpr { Target: NameExpr n } }: m.Exports[n.Name] = ExportK.Variable; break;
				}
			}
		}

		// Execution order: a module runs after the modules it imports (topological); ties/cycles fall back to declaration
		// order. Importing `__Main` is what positions `__Main` (so a module that imports __Main runs after it).
		private static List<string> ComputeExecOrder(List<ModInfo> mods, Dictionary<string, ModInfo> byName)
		{
			var order = new List<string>();
			var state = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);   // 0=unseen,1=visiting,2=done
			void Visit(ModInfo m)
			{
				if (state.ContainsKey(m.Name)) return;   // done or already on stack (cycle) -> skip
				state[m.Name] = 1;
				foreach (var dep in ImportTargets(m))
					if (byName.TryGetValue(dep, out var dm)) Visit(dm);
				state[m.Name] = 2;
				order.Add(m.Name);
			}
			// Drive in REVERSE declaration order (matches the canonical GetModuleExecutionOrder): combined with the
			// cycle skip, a module that imports __Main runs after __Main even though __Main also imports it.
			for (int i = mods.Count - 1; i >= 0; i--) Visit(mods[i]);
			return order;
		}

		private static IEnumerable<string> ImportTargets(ModInfo m)
		{
			foreach (var im in m.Imports)
			{
				var (mod, _, _, _) = ParseImport(im.Args ?? "");
				if (mod.Length > 0) yield return mod;
			}
		}

		// Parses a `#import` argument: `(moduleName, alias, namedList, quoted)`. namedList is null if no `{ … }` is
		// present, otherwise the comma-separated names inside the braces (a single `*` for wildcard).
		private static (string mod, string alias, string named, bool quoted) ParseImport(string args)
		{
			args = args.Trim();
			bool quoted = false;
			string mod;
			int i = 0;
			if (i < args.Length && (args[i] == '"' || args[i] == '\''))
			{
				quoted = true;
				var q = args[i]; i++;
				int s = i;
				while (i < args.Length && args[i] != q) i++;
				mod = args.Substring(s, i - s);
				if (i < args.Length) i++;
			}
			else
			{
				int s = i;
				while (i < args.Length && (char.IsLetterOrDigit(args[i]) || args[i] == '_')) i++;
				mod = args.Substring(s, i - s);
			}
			var rest = args.Substring(i).Trim();
			string alias = null, named = null;
			var brace = rest.IndexOf('{');
			if (brace >= 0)
			{
				var end = rest.IndexOf('}', brace);
				named = rest.Substring(brace + 1, (end < 0 ? rest.Length : end) - brace - 1).Trim();
				rest = rest.Substring(0, brace).Trim();
			}
			if (rest.StartsWith("as ", System.StringComparison.OrdinalIgnoreCase))
				alias = rest.Substring(3).Trim();
			return (mod, alias, named, quoted);
		}

		private void ClearPerModuleState()
		{
			_fields.Clear(); _fieldDecls.Clear(); _staticFieldDeclIdx.Clear(); _userFuncByLower.Clear(); _userClassByLower.Clear();
			_inlineAliases.Clear(); _wildcardModules.Clear(); _classFieldIds.Clear(); _emittedFuncImpls.Clear();
			_exportedNames.Clear(); _pendingLambdas.Clear(); _scopeTemps.Clear(); _tempCounter = 0;
		}

		private CompilationUnitSyntax BuildMultiModule(ProgramNode prog, string name)
		{
			var mods = PartitionModules(prog.Body);
			foreach (var m in mods) ScanExports(m);
			var byName = mods.ToDictionary(m => m.Name, m => m, System.StringComparer.OrdinalIgnoreCase);
			LoadFileModules(mods, byName);   // pull in `#import "name"` modules defined in a separate <name>.ahk
			var execOrder = ComputeExecOrder(mods, byName);

			// A top-level `#Requires` (in __Main, before any #Module) sets the script-wide default that other modules
			// inherit; each module may override it with its own `#Requires`.
			var globalCompat = ScanRequires(mods[0].Body) ?? Keysharp.Runtime.Script.DefaultCompatibilityVersion;

			var moduleClasses = new List<MemberDeclarationSyntax>();
			foreach (var m in mods)
			{
				ClearPerModuleState();
				_currentModuleClass = NameMangler.ModuleClass(m.Name);
				_moduleCompat = ScanRequires(m.Body) ?? globalCompat;
				_currentCompat = _moduleCompat;
				var importMembers = EmitImports(m, byName);
				var body = m.Body.Select(s => s is ExportStmt e ? e.Decl : s).ToList();
				var (members, auto) = LowerProgramBody(body);
				if (Diagnostics.Count > 0) return null;
				MarkExports(m);
				moduleClasses.Add(BuildModuleClass(m.Name, _fieldDecls, members, _pendingLambdas,
					m.Name == "__Main" ? _hotMembers : null, auto, importMembers, _moduleCompat));
			}
			return AssembleProgram(name, moduleClasses, execOrder);
		}

		// References an exported member field of another module: `Program.<Mod>.<escapedName>`.
		private static ExpressionSyntax ModuleMemberField(string modName, string memberName) =>
			Member(Member(Id("Program"), modName), NameMangler.Global(memberName));

		// Emits the import binding members for a module (and registers the bound names in `_fields`). Returns property
		// members (imported variables) to add to the class body; field bindings go straight into `_fieldDecls`.
		// Names declared locally in a module (functions/classes/hotkey-or-hotstring funcs). A local declaration takes
		// precedence over any imported name (even a later import), so these are excluded from import binding.
		private static HashSet<string> CollectLocalDeclNames(List<Stmt> body)
		{
			var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
			foreach (var s in body)
			{
				var st = s is ExportStmt e ? e.Decl : s;
				switch (st)
				{
					case FunctionDecl fd: set.Add(fd.Name); break;
					case ClassDecl cd: set.Add(cd.Name); break;
					case HotkeyDef { Func: { } hkf }: set.Add(hkf.Name); break;
					case HotstringDef { Func: { } hsf }: set.Add(hsf.Name); break;
				}
			}
			return set;
		}

		private List<MemberDeclarationSyntax> EmitImports(ModInfo m, Dictionary<string, ModInfo> byName)
		{
			var props = new List<MemberDeclarationSyntax>();
			var localNames = CollectLocalDeclNames(m.Body);
			// Wildcard `{ * }` exports are deferred and resolved with LAST-import-wins (a later `#import "B" { * }`
			// overrides an earlier `#import "A" { * }` for a shared name); explicit imports and locals take priority.
			var wild = new Dictionary<string, (string mod, string key, ExportK kind)>(System.StringComparer.OrdinalIgnoreCase);
			foreach (var im in m.Imports)
			{
				var (modName, alias, named, quoted) = ParseImport(im.Args ?? "");
				if (modName.Length == 0) continue;
				bool isAhk = modName.Equals("AHK", System.StringComparison.OrdinalIgnoreCase);
				bool isScript = byName.ContainsKey(modName);

				// The module object: `new Program.<Mod>()` (or `new Keysharp.Runtime.Ahk()` for the built-in AHK module).
				ExpressionSyntax ModuleObject() => isAhk
					? SyntaxFactory.ObjectCreationExpression(Ty("Keysharp.Runtime.Ahk")).WithArgumentList(SyntaxFactory.ArgumentList())
					: SyntaxFactory.ObjectCreationExpression(Ty("Program." + NameMangler.ModuleClass(modName))).WithArgumentList(SyntaxFactory.ArgumentList());

				// `as Alias`: bind the module's default export if it has one, else the module object.
				if (alias != null && !localNames.Contains(alias))
				{
					ExpressionSyntax val = isScript && byName[modName].DefaultName != null
						? ModuleMemberField(modName, byName[modName].DefaultName)
						: ModuleObject();
					RegisterImportField(alias, val);
				}
				// Named imports `{ a, b as c, * }`.
				if (named != null)
				{
					foreach (var raw in named.Split(','))
					{
						var part = raw.Trim();
						if (part.Length == 0) continue;
						if (part == "*")   // wildcard: record every export (resolved last-wins after the loop)
						{
							if (isScript)
								foreach (var ex in byName[modName].Exports)
									if (!localNames.Contains(ex.Key)) wild[ex.Key] = (modName, ex.Key, ex.Value);
							continue;
						}
						string impName = part, impAlias = part;
						var asIdx = part.IndexOf(" as ", System.StringComparison.OrdinalIgnoreCase);
						if (asIdx >= 0) { impName = part.Substring(0, asIdx).Trim(); impAlias = part.Substring(asIdx + 4).Trim(); }
						if (localNames.Contains(impAlias)) continue;   // a local declaration overrides this import
						wild.Remove(impAlias);                         // an explicit import overrides a wildcard one
						if (isScript && byName[modName].Exports.TryGetValue(impName, out var k))
							BindNamedImport(modName, impName, impAlias, k, props);
						else
							RegisterImportField(impAlias, ModuleMemberField(modName, impName));   // best-effort
					}
				}
				// A bare `#import Mod` (no alias, no braces) adds the module NAME as the module object — but a quoted
				// `#import "Mod"` does NOT introduce the name unless aliased.
				if (alias == null && named == null && !quoted && !localNames.Contains(modName))
					RegisterImportField(modName, ModuleObject());
			}
			// Emit the surviving wildcard bindings (those not shadowed by an explicit import or local declaration).
			foreach (var (lower, w) in wild)
				if (!_fields.ContainsKey(lower.ToLowerInvariant()))
					BindNamedImport(w.mod, w.key, w.key, w.kind, props);
			return props;
		}

		// A method export binds as a cached field (`alias = Mod.name`); a variable/type export binds as a get/set
		// property delegating to the source module (so writes propagate) — matching the canonical AddImportMemberCore.
		private void BindNamedImport(string modName, string name, string alias, ExportK kind, List<MemberDeclarationSyntax> props)
		{
			if (kind == ExportK.Function)
				RegisterImportField(alias, ModuleMemberField(modName, name));
			else
			{
				var lower = alias.ToLowerInvariant();
				if (_fields.ContainsKey(lower)) return;
				_fields[lower] = NameMangler.Escape(lower);
				var src = ModuleMemberField(modName, name);
				props.Add(SyntaxFactory.PropertyDeclaration(ObjType, NameMangler.Escape(lower))
					.AddModifiers(PublicTok, StaticTok)
					.AddAccessorListAccessors(
						SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(src)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
						SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(Assign(src, Id("value")))).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))));
			}
		}

		private void RegisterImportField(string name, ExpressionSyntax value)
		{
			var lower = name.ToLowerInvariant();
			if (_fields.ContainsKey(lower)) return;
			_fields[lower] = NameMangler.Escape(lower);
			_fieldDecls.Add(ObjField(NameMangler.Escape(lower), value));
		}

		// Adds `[Keysharp.Runtime.Export]` to the backing fields of a module's exported names.
		private void MarkExports(ModInfo m)
		{
			for (int i = 0; i < _fieldDecls.Count; i++)
			{
				if (_fieldDecls[i] is not FieldDeclarationSyntax fd) continue;
				var varName = fd.Declaration.Variables[0].Identifier.Text.TrimStart('@');
				if (m.Exports.ContainsKey(varName))
					_fieldDecls[i] = fd.AddAttributeLists(Attr("Keysharp.Runtime.Export"));
			}
		}

		// Pre-pass (register functions/classes/imports) + the top-level lowering loop for one module body, returning
		// the module-class members and its auto-execute statements. Shared by the single- and multi-module paths.
		private (List<MemberDeclarationSyntax> members, List<StatementSyntax> auto) LowerProgramBody(List<Stmt> body)
		{
			foreach (var s in body)
			{
				// Only TOP-LEVEL functions are module-global. Nested functions (static or not) are scoped to their
				// enclosing function — bound as local FuncObj/Closure vars when that scope is lowered — so same-named
				// nested functions in different scopes don't collide.
				if (s is FunctionDecl fd) { _userFuncByLower[fd.Name.ToLowerInvariant()] = fd.Name; }
				else if (s is HotkeyDef { Func: { } hkf }) { _userFuncByLower[hkf.Name.ToLowerInvariant()] = hkf.Name; }
				else if (s is HotstringDef { Func: { } hsf }) { _userFuncByLower[hsf.Name.ToLowerInvariant()] = hsf.Name; }
				else if (s is ClassDecl cd) { RegisterClass(cd); }
				else if (s is DirectiveStmt dir && dir.Name.Equals("import", System.StringComparison.OrdinalIgnoreCase)) RegisterImport(dir);
			}
			foreach (var lower in _userFuncByLower.Keys.ToList())
				EnsureGlobalField(lower);

			// #Warn: apply directive config (location-independent) then run the warning analysis over the whole module,
			// now that user funcs/classes are registered. Collected warnings are emitted at load time (see BuildOuterAuto).
			PrescanWarnDirectives(body);
			AnalyzeWarnings(body);

			var members = new List<MemberDeclarationSyntax>();
			var auto = new List<StatementSyntax>();
			// The top-level (auto-exec) scope's fat-arrow local functions/closures must start empty per module, or one
			// module's top-level lambdas leak into the next module's AutoExecSection (referencing fields it lacks).
			_pendingScopeFuncs = new();
			_pendingScopeClosureInits = new();
			_labelScopes.Add(CollectDirectLabels(body));   // top-level labels (goto targets from auto-exec loops)
			for (int i = 0; i < body.Count; i++)
			{
				var s = body[i];
				if (s is FunctionDecl fd) { _emittedFuncImpls.Add(NameMangler.FunctionMethod(fd.Name)); members.Add(LowerFunction(fd)); }
				else if (s is ClassDecl cd)
				{
					members.Add(LowerClass(cd));
					// AHK initializes a class when execution reaches its declaration — model that by referencing the
					// class slot at that point in the auto-exec, which triggers the lazy Statics[typeof(Class)] init.
					auto.Add(ExprStmt(Op("MultiStatement", Id(_fields[cd.Name.ToLowerInvariant()]))));
				}
				else if (s is HotkeyDef hk) LowerHotkey(hk);
				else if (s is HotstringDef hs) LowerHotstring(hs);
				else if (s is RemapDef rm) LowerRemap(rm);
				else if (s is DirectiveStmt hd && hd.Name.Equals("Hotstring", System.StringComparison.OrdinalIgnoreCase)) LowerHotstringDirective(hd);
				// `#HotIf <expr>` sets the context for the hotkeys/hotstrings that follow it; a bare `#HotIf` clears it.
				// Emitted into the DHHR in SOURCE ORDER so it brackets the AddHotkey calls (matches the canonical).
				else if (s is DirectiveStmt hi && hi.Name.Equals("HotIf", System.StringComparison.OrdinalIgnoreCase)) LowerHotIf(hi);
				else
				{
					// A `name:` label immediately before a top-level loop becomes that loop's break/continue target.
					if (s is LabelStmt ll && i + 1 < body.Count && IsLoopStmt(body[i + 1])) _pendingLoopLabel = ll.Name;
					var st = LowerStmt(s); if (st != null) auto.Add(st);
				}
			}
			_labelScopes.RemoveAt(_labelScopes.Count - 1);
			// Declare temps introduced by postfix ++/-- in the top-level (auto-execute) scope.
			for (int i = _scopeTemps.Count - 1; i >= 0; i--) auto.Insert(0, DeclLocal(ObjType, _scopeTemps[i], Null));
			auto.AddRange(_pendingScopeFuncs);   // top-level fat-arrow local functions (auto-exec scope)
			return (members, auto);
		}

		// ---- symbol resolution ----

		// Collects the names of nested functions (static or not) declared in a scope body (recursing into control-flow
		// blocks but NOT into nested function bodies, which are separate scopes). Used so a forward-referenced call to a
		// nested function resolves to its local variable rather than (mis)resolving to a module field.
		private static void CollectBareNestedFuncNames(Stmt s, HashSet<string> into)
		{
			switch (s)
			{
				case FunctionDecl fd: into.Add(fd.Name.ToLowerInvariant()); break;
				case Block bl: foreach (var x in bl.Body) CollectBareNestedFuncNames(x, into); break;
				case IfStmt iff: CollectBareNestedFuncNames(iff.Then, into); if (iff.Else != null) CollectBareNestedFuncNames(iff.Else, into); break;
				case WhileStmt w: CollectBareNestedFuncNames(w.Body, into); break;
				case LoopStmt lp: CollectBareNestedFuncNames(lp.Body, into); break;
				case SpecialLoopStmt slp: CollectBareNestedFuncNames(slp.Body, into); break;
				case ForStmt fr: CollectBareNestedFuncNames(fr.Body, into); break;
				case TryStmt tr:
					CollectBareNestedFuncNames(tr.Body, into);
					foreach (var cb in tr.Catches) CollectBareNestedFuncNames(cb.Body, into);
					if (tr.Else != null) CollectBareNestedFuncNames(tr.Else, into);
					if (tr.Finally != null) CollectBareNestedFuncNames(tr.Finally, into);
					break;
				case SwitchStmt sw:
					foreach (var c in sw.Cases) foreach (var x in c.Body) CollectBareNestedFuncNames(x, into);
					if (sw.Default != null) foreach (var x in sw.Default) CollectBareNestedFuncNames(x, into);
					break;
			}
		}

		// Minimal `#import "Module" { name1, name2 }`: binds each named export of an external/builtin module
		// (a Keysharp.Runtime.Module subtype, e.g. "Ks") to a global slot — methods as Func, types as the
		// type singleton, properties as a direct member access. Script modules / wildcard / aliases are deferred.
		// Maps a `#Requires` argument (e.g. "AutoHotkey v2.1-alpha") to its compatibility line (2.0.0 or 2.1.0), or null
		// if the requirement isn't an AutoHotkey/Keysharp version requirement. Mirrors GetCompatibilityModeFromRequirement.
		private static Semver.SemVersion MapRequires(string args)
		{
			var parts = (args ?? "").Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2) return null;
			if (!parts[0].StartsWith("AutoHotkey", System.StringComparison.OrdinalIgnoreCase) &&
				!parts[0].StartsWith("Keysharp", System.StringComparison.OrdinalIgnoreCase)) return null;
			var ver = parts[1].TrimStart('v', 'V');
			foreach (var c in CompatCandidates)
				if (Keysharp.Runtime.CompatibilityVersions.RequirementAllowsCompatibilityLine(ver, c)) return c;
			return CompatCandidates[^1];
		}

		// The last `#Requires` directive among a statement list (its mode), or null if none — used to resolve a
		// module/function body's compatibility mode (a later directive wins).
		private static Semver.SemVersion ScanRequires(IEnumerable<Stmt> body)
		{
			Semver.SemVersion result = null;
			if (body != null)
				foreach (var s in body)
					if (s is DirectiveStmt d && d.Name.Equals("Requires", System.StringComparison.OrdinalIgnoreCase) && MapRequires(d.Args) is { } m)
						result = m;
			return result;
		}

		// The default/implicit return value for the current scope: unset (null) under v2.1+, "" under v2.0.
		private ExpressionSyntax DefaultReturnExpr() =>
			Keysharp.Runtime.Script.ReturnsUnsetByDefault(_currentCompat) ? Null : Str("");

		// A [CompatibilityMode] attribute list, emitted on a member only when its mode differs from the module's.
		private AttributeListSyntax[] CompatAttr() =>
			_currentCompat.CompareSortOrderTo(_moduleCompat) != 0
				? new[] { Attr("Keysharp.Runtime.CompatibilityMode", Str(_currentCompat.ToString())) }
				: System.Array.Empty<AttributeListSyntax>();

		private void RegisterImport(DirectiveStmt d)
		{
			// The module name may be quoted or unquoted (`#import "Ks" {…}` and `#import Ks {…}` are both valid), and
			// the brace list may be `{ * }` or named — ParseImport normalizes all of these.
			var (modName, _, named, _) = ParseImport(d.Args ?? "");
			if (modName.Length == 0) return;
			if (modName.Equals("__Main", System.StringComparison.OrdinalIgnoreCase) && named == null)
			{
				// `#import __Main` — self-import of the current module: the name resolves to a fresh Module instance
				// whose IMetaObject Get/Set reflect to the module's static fields (matches the canonical).
				if (!_inlineAliases.ContainsKey("__main"))
					_inlineAliases["__main"] = () => SyntaxFactory.ObjectCreationExpression(Ty("__Main")).WithArgumentList(SyntaxFactory.ArgumentList());
				return;
			}
			if (!Script.TheScript.ReflectionsData.stringToTypes.TryGetValue(modName, out var modType)) return;
			if (!typeof(Keysharp.Runtime.Module).IsAssignableFrom(modType)) return;

			var names = named ?? "*";
			if (names == "*" || names.Length == 0)   // `#import "Mod"` / `#import "Mod" { * }`: resolve members on demand
			{
				_wildcardModules.Add(modType);
				return;
			}
			foreach (var raw in names.Split(','))
			{
				var name = raw.Trim();
				if (name.Length == 0) continue;
				var lower = name.ToLowerInvariant();
				if (_fields.ContainsKey(lower)) continue;
				var init = BindModuleMember(modType, name);
				if (init == null) continue;
				_fields[lower] = NameMangler.Escape(lower);
				_fieldDecls.Add(ObjField(NameMangler.Escape(lower), init));
			}
		}

		// Resolves one exported name on a module type to its binding: method->Func, nested type->singleton, property->access.
		private ExpressionSyntax BindModuleMember(Type modType, string name)
		{
			const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy;
			var method = modType.GetMethods(flags).FirstOrDefault(mi => !mi.IsSpecialName && mi.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
			if (method != null) return FuncBind(method.DeclaringType.FullName.Replace('+', '.') + "." + method.Name);
			var nested = modType.GetNestedTypes(System.Reflection.BindingFlags.Public).FirstOrDefault(t => t.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
			if (nested != null) return TypeSingleton(nested.FullName.Replace('+', '.'));
			var prop = modType.GetProperties(flags).FirstOrDefault(p => p.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
			if (prop != null) return Access(prop.DeclaringType.FullName.Replace('+', '.') + "." + prop.Name);
			return null;
		}

		// A type from `stringToTypes` is an AHK-visible class only if it derives from `Keysharp.Builtins.Any`
		// (Array, Map, Gui, …). Static method-containers (Dir, Files, Maths — their methods are global functions)
		// are NOT classes; resolving a same-named variable (e.g. `dir`) to `Statics[typeof(Dir)]` would crash at init.
		private static bool IsAhkClass(System.Type type) => typeof(Keysharp.Builtins.Any).IsAssignableFrom(type);

		private string EnsureGlobalField(string lower)
		{
			if (_fields.TryGetValue(lower, out var existing)) return existing;

			var id = NameMangler.Escape(lower);
			_fields[lower] = id;

			ExpressionSyntax init;
			if (_userFuncByLower.TryGetValue(lower, out var orig))
				init = FuncBind(NameMangler.FunctionMethod(orig));
			else if (Script.TheScript.ReflectionsData.flatPublicStaticMethods.TryGetValue(lower, out var mi))
				init = FuncBind($"{mi.DeclaringType.FullName.Replace('+', '.')}.{mi.Name}");
			else if (Script.TheScript.ReflectionsData.stringToTypes.TryGetValue(lower, out var type) && IsAhkClass(type))
				init = TypeSingleton(type.FullName.Replace('+', '.'));
			// (AHK class-name aliases Object/Func/File are resolved INLINE in NameRef, not as a cached field.)
			else if (_wildcardModules.Select(t => BindModuleMember(t, lower)).FirstOrDefault(b => b != null) is { } wild)
				init = wild;   // resolved through a `#import "Mod" { * }` wildcard
			else
				init = Null;

			_fieldDecls.Add(ObjField(id, init));
			return id;
		}

		private string EnsureTypeField(string typeFullName, string fieldLower)
		{
			if (_fields.TryGetValue(fieldLower, out var id)) return id;
			id = NameMangler.Escape(fieldLower);
			_fields[fieldLower] = id;
			_fieldDecls.Add(ObjField(id, TypeSingleton(typeFullName)));
			return id;
		}

		// Whether a name is a declared local/static/param/forced-global (so A_LineNumber/A_LineFile shadowing works).
		private bool IsDeclaredLocal(string lower) =>
			(_locals != null && _locals.Contains(lower)) || (_statics != null && _statics.ContainsKey(lower))
			|| (_forcedGlobals != null && _forcedGlobals.Contains(lower)) || _enclosingLocals.Contains(lower);

		// The script's full path (for A_LineFile), matching A_ScriptFullPath; falls back to the raw path for `*`.
		private string ScriptFullPath() =>
			!string.IsNullOrEmpty(_scriptPath) && System.IO.File.Exists(_scriptPath)
				? System.IO.Path.GetFullPath(_scriptPath) : _scriptPath ?? "";

		private ExpressionSyntax NameRef(string name)
		{
			var lower = name.ToLowerInvariant();
			if (_inMethod && lower == "this") { _capturedInScope = true; return Id("@this"); }
			if (_byRefParams != null && _byRefParams.Contains(lower))   // &param read: through the VarRef
				return Op("GetPropertyValue", Id(NameMangler.Escape(lower)), Str("__Value"));
			if (_statics != null && _statics.TryGetValue(lower, out var sf)) return Id(sf);
			if (_forcedGlobals != null && _forcedGlobals.Contains(lower)) return Id(EnsureGlobalField(lower));
			if (_locals != null && _locals.Contains(lower)) return Id(NameMangler.Escape(lower));
			// A bare nested function in this scope is a local closure var (hoisted) — resolve even forward references to
			// the local, never to a (qualified) module field.
			if (_scopeClosureNames.Contains(lower)) return Id(NameMangler.Escape(lower));
			// A captured enclosing-scope local: the same C# local the surrounding scope declared (C# closes over it).
			if (_enclosingLocals.Contains(lower)) { _capturedInScope = true; return Id(NameMangler.Escape(lower)); }
			// An enclosing-scope static is a module-level SL_ field reachable directly from a nested closure.
			if (_enclosingStatics.TryGetValue(lower, out var esf)) { _capturedInScope = true; return Id(esf); }
			if (_inlineAliases.TryGetValue(lower, out var alias)) return alias();
			// Built-in variables (A_Index, A_Clipboard, A_TickCount, …) resolve to their accessor property.
			if (Script.TheScript.ReflectionsData.flatPublicStaticProperties.TryGetValue(lower, out var prop))
				return Access(prop.DeclaringType.FullName.Replace('+', '.') + "." + prop.Name);
			// Built-in class names exposed under an AHK alias (Object->KeysharpObject, Func->FuncObj, File->KeysharpFile)
			// resolve to the class singleton INLINE (lazy — never a cached static field, which for FuncObj would
			// perturb its init order), so `Object.Prototype`, `x is Func`, `Func("name")` etc. work.
			if (!_userFuncByLower.ContainsKey(lower)
				&& Keysharp.Parsing.Keywords.TypeNameAliases.FirstOrDefault(kv => kv.Value.Equals(lower, System.StringComparison.OrdinalIgnoreCase)).Key is { } aliasKey
				&& Script.TheScript.ReflectionsData.stringToTypes.TryGetValue(aliasKey, out var aliasType))
				return TypeSingleton(aliasType.FullName.Replace('+', '.'));
			// A wildcard-imported PROPERTY reflects live runtime state (e.g. A_DefaultHotstring*), so read it through
			// a direct member access each reference rather than caching it in a static field initialised once.
			if (WildcardLiveProperty(lower) is { } liveProp) return liveProp;
			var gf = EnsureGlobalField(lower);
			// Inside a (nested) class method the module's static field isn't in scope unqualified — qualify it as
			// `Program.<Module>.<field>` (e.g. a class property `Prop => ModuleFunc()`).
			return _inMethod ? Member(Member(Id("Program"), _currentModuleClass), gf) : Id(gf);
		}

		// If `lower` names a (non-method) property of a `#import "Mod" { * }` wildcard module, returns a live member
		// access to it; otherwise null. Methods/types are fine cached as fields, but properties must be re-read.
		private ExpressionSyntax WildcardLiveProperty(string lower)
		{
			const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy;
			foreach (var modType in _wildcardModules)
			{
				if (modType.GetMethods(flags).Any(mi => !mi.IsSpecialName && mi.Name.Equals(lower, System.StringComparison.OrdinalIgnoreCase))) continue;
				var prop = modType.GetProperties(flags).FirstOrDefault(p => p.Name.Equals(lower, System.StringComparison.OrdinalIgnoreCase));
				if (prop != null) return Access(prop.DeclaringType.FullName.Replace('+', '.') + "." + prop.Name);
			}
			return null;
		}

		// ---- statements ----

		private StatementSyntax LowerStmt(Stmt s)
		{
			switch (s)
			{
				case ExpressionStmt es:
					if (es.Expr is CallExpr call) return ExprStmt(LowerCall(call, statement: true));
					var le = LowerExpr(es.Expr);
					// C# only allows call/assignment/new as a statement; wrap anything else (bare value, ternary, …).
					return ExprStmt(le is InvocationExpressionSyntax or AssignmentExpressionSyntax ? le : Op("MultiStatement", le));
				case ReturnStmt r:
					return SyntaxFactory.ReturnStatement(r.Value != null ? LowerExpr(r.Value) : DefaultReturnExpr());
				case Block b:
					return SyntaxFactory.Block(LowerStmtList(b.Body));
				case IfStmt iff:
					var elseClause = iff.Else != null ? SyntaxFactory.ElseClause(AsBlock(LowerStmt(iff.Else))) : null;
					return SyntaxFactory.IfStatement(IfTest(LowerExpr(iff.Cond)), AsBlock(LowerStmt(iff.Then)), elseClause);
				case WhileStmt w: return LowerWhile(w);
				case DeclStmt d: return LowerDecl(d);
				case LoopStmt lp: return LowerLoop(lp);
				case SpecialLoopStmt sl: return LowerSpecialLoop(sl);
				case ForStmt fr: return LowerFor(fr);
				case SwitchStmt sw: return LowerSwitch(sw);
				case TryStmt tr: return LowerTry(tr);
				case ThrowStmt th: return LowerThrow(th);
				case BreakStmt bs: return LowerBreakContinue(bs.Target, isBreak: true);
				case ContinueStmt cs: return LowerBreakContinue(cs.Target, isBreak: false);
				case GotoStmt g:
					// Emit a real C# goto only to a label in an enclosing scope / the same C# switch (a valid C# jump);
					// an out-of-scope target (which AHK forbids anyway) degrades to a no-op rather than failing to compile.
					return _labelScopes.Any(sc => sc.Contains(g.Target))
						? SyntaxFactory.GotoStatement(SyntaxKind.GotoStatement, Id(UserLabelId(g.Target)))
						: SyntaxFactory.EmptyStatement();
				case LabelStmt l: return SyntaxFactory.LabeledStatement(UserLabelId(l.Name), SyntaxFactory.EmptyStatement());
				case FunctionDecl fd:
					// A function nested inside another function (_locals != null) is scoped to it: emitted as a uniquely
					// named local function bound to a local var, so same-named nested functions in sibling scopes don't
					// collide and siblings/the parent can reference it by name. (A `static` nested function differs only in
					// not capturing the parent's non-static locals — handled leniently.) Top-level functions
					// (_locals == null) are module-level methods.
					if (_locals != null) return LowerNestedClosure(fd);
					if (_emittedFuncImpls.Add(NameMangler.FunctionMethod(fd.Name))) _pendingLambdas.Add(LowerFunction(fd));
					return null;
				case DirectiveStmt dir: return LowerDirective(dir);   // value-setting directives; rest are no-ops here
				default: Diag($"statement not yet lowerable: {s.GetType().Name}"); return null;
			}
		}

		// Value-setting directives lower to an assignment of the matching runtime accessor/field at their position in
		// the auto-exec (matches the canonical, which assigns the same A_* / Script members). Unknown or purely
		// load-time directives (#Requires, #ErrorStdOut, #MaxThreads, #DllLoad, #Include, #import handled elsewhere…)
		// are no-ops here.
		private StatementSyntax LowerDirective(DirectiveStmt d)
		{
			var args = (d.Args ?? "").Trim();
			// A numeric/true/false directive argument as a numeric literal (default when omitted).
			ExpressionSyntax NumArg(long deflt) =>
				args.Length == 0 ? Num(deflt.ToString())
				: args.Equals("true", System.StringComparison.OrdinalIgnoreCase) ? Num("1")
				: args.Equals("false", System.StringComparison.OrdinalIgnoreCase) ? Num("0")
				: Num(args);
			StatementSyntax Set(string target, ExpressionSyntax val) => ExprStmt(Assign(Access(target), val));
			var True = SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression);

			switch (d.Name.ToUpperInvariant())
			{
				case "CLIPBOARDTIMEOUT": return Set("Keysharp.Builtins.Accessors.A_ClipboardTimeout", NumArg(1000));
				case "HOTIFTIMEOUT": return Set("Keysharp.Builtins.Accessors.A_HotIfTimeout", NumArg(1000));
				case "INPUTLEVEL": return Set("Keysharp.Builtins.Accessors.A_InputLevel", NumArg(0));   // setter clamps 0..100
				case "SUSPENDEXEMPT": return Set("Keysharp.Builtins.Ks.A_SuspendExempt", NumArg(1));   // on Ks; setter ForceBools
				case "MAXTHREADSBUFFER":
					return Set("Keysharp.Builtins.Accessors.A_MaxThreadsBuffer",
						Num(args.Equals("false", System.StringComparison.OrdinalIgnoreCase) || args == "0" ? "0" : "1"));
				case "MAXTHREADSPERHOTKEY":
					long.TryParse(args, out var mtph);
					return Set("Keysharp.Builtins.Accessors.A_MaxThreadsPerHotkey", Num(System.Math.Clamp(mtph, 1, 255).ToString()));
				case "USEHOOK": return Set("MainScript.ForceKeybdHook", Op("ForceBool", NumArg(0)));
				case "NOTRAYICON": return Set("MainScript.NoTrayIcon", True);
				case "NOMAINWINDOW": return Set("MainScript.NoMainWindow", True);
				case "WINACTIVATEFORCE": return Set("MainScript.WinActivateForce", True);
				// #MaxThreads N: global concurrent-thread cap (AHK clamps 1..255). MaxThreadsTotal is a uint field.
				case "MAXTHREADS":
					long.TryParse(args, out var mt);
					return Set("MainScript.MaxThreadsTotal", SyntaxFactory.CastExpression(
						SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.UIntKeyword)),
						Num(System.Math.Clamp(mt, 1, 255).ToString())));
				// #MenuMaskKey <key>: the key A_MenuMaskKey sends to mask Win/Alt; settable accessor takes the raw key text.
				case "MENUMASKKEY": return Set("Keysharp.Builtins.Accessors.A_MenuMaskKey", Str(args.Trim('"', '\'')));
				// #DllLoad [*i] file: load a DLL at startup so later DllCalls resolve it. The `*i` prefix ignores a
				// missing file (LoadDll's throwOnFailure=false). Runtime method pins the module (see Script.LoadDll).
				case "DLLLOAD":
				{
					var lib = args.Trim('"', '\'');
					bool ignoreMissing = false;
					if (lib.StartsWith("*i", System.StringComparison.OrdinalIgnoreCase)) { ignoreMissing = true; lib = lib.Substring(2).Trim().Trim('"', '\''); }
					return ExprStmt(Inv(Access("MainScript.LoadDll"), Str(lib), ignoreMissing ? False : True));
				}
				// #Persistent [false|0]: keep the script running with no hotkeys/timers. Sets the same flag the DHHR uses
				// to emit Flow.Persistent() (see BuildOuterAuto); a `false`/`0` argument leaves it off.
				case "PERSISTENT":
					if (!(args.Equals("false", System.StringComparison.OrdinalIgnoreCase) || args == "0")) _persistent = true;
					return null;
				// #SingleInstance [Force|Ignore|Prompt|Off]: recorded here and emitted as a HandleSingleInstance guard at
				// the top of Main (see BuildMain). Default option is Force (matches AHK v2 and the canonical).
				case "SINGLEINSTANCE":
					_singleInstanceMode = args.Length == 0 ? "Force" : args.ToUpperInvariant() switch
					{
						"FORCE" => "Force", "IGNORE" => "Ignore", "PROMPT" => "Prompt", "OFF" => "Off",
						_ => null
					};
					if (_singleInstanceMode == null) Diag($"#SingleInstance: unrecognized option '{args}'");
					return null;
				// #HookMutexName <name>: names the keyboard/mouse hook mutex (so multiple scripts can share a hook). Passed
				// to the Script constructor (see AssembleProgram), which sets HookThread.MutexName.
				case "HOOKMUTEXNAME":
					if (args.Length > 0) _hookMutexName = args.Trim('"', '\'');
					return null;
				// #ErrorStdOut: send uncaught errors to stderr instead of a dialog (runtime honors MainScript.ErrorStdOut).
				case "ERRORSTDOUT": return Set("MainScript.ErrorStdOut", True);
				// #Warn config is applied in a prescan (PrescanWarnDirectives) so it is location-independent (per the
				// docs); here it is a no-op. #Nullable / #NoDynamicVars are recognized compile-time hints with no effect.
				case "WARN":
				case "NULLABLE":
				case "NODYNAMICVARS":
					return null;
				// #Assembly* metadata -> assembly attributes on the generated unit (A_Asm* read them at runtime).
				case "ASSEMBLYTITLE": _asmAttributes.Add(("System.Reflection.AssemblyTitleAttribute", args)); return null;
				case "ASSEMBLYDESCRIPTION": _asmAttributes.Add(("System.Reflection.AssemblyDescriptionAttribute", args)); return null;
				case "ASSEMBLYCONFIGURATION": _asmAttributes.Add(("System.Reflection.AssemblyConfigurationAttribute", args)); return null;
				case "ASSEMBLYCOMPANY": _asmAttributes.Add(("System.Reflection.AssemblyCompanyAttribute", args)); return null;
				case "ASSEMBLYPRODUCT": _asmAttributes.Add(("System.Reflection.AssemblyProductAttribute", args)); return null;
				case "ASSEMBLYCOPYRIGHT": _asmAttributes.Add(("System.Reflection.AssemblyCopyrightAttribute", args)); return null;
				case "ASSEMBLYTRADEMARK": _asmAttributes.Add(("System.Reflection.AssemblyTrademarkAttribute", args)); return null;
				case "ASSEMBLYVERSION":
					_asmAttributes.Add(("System.Reflection.AssemblyVersionAttribute", args));
					_asmAttributes.Add(("System.Reflection.AssemblyFileVersionAttribute", args));   // A_AsmVersion reads FileVersion
					return null;
				default: return null;
			}
		}

		// ---- #Warn (compile-time warning analysis) ----

		private static readonly HashSet<string> WarnModeNames = new(System.StringComparer.OrdinalIgnoreCase)
		{ "MsgBox", "StdOut", "OutputDebug", "Off", "On" };
		private static string CanonWarnMode(string m) => m.ToLowerInvariant() switch
		{ "stdout" => "StdOut", "outputdebug" => "OutputDebug", _ => "MsgBox" };

		// Applies one `#Warn [Type], [Mode]` directive to the per-type mode config (see the field declarations).
		private void ApplyWarnDirective(string args)
		{
			args = (args ?? "").Trim();
			if (args.Length == 0)   // bare `#Warn`: VarUnset + Unreachable on (default mode), LocalSameAsGlobal off.
			{ _warnVarUnset = _warnUnreachable = _warnDefaultMode; _warnLocalSameAsGlobal = null; return; }
			int comma = args.IndexOf(',');
			var typeStr = (comma < 0 ? args : args.Substring(0, comma)).Trim();
			var modeStr = comma < 0 ? "" : args.Substring(comma + 1).Trim();
			// A lone mode (`#Warn StdOut` / `#Warn Off` / `#Warn On`) sets the program-wide default + the on warnings.
			if (comma < 0 && WarnModeNames.Contains(typeStr))
			{
				if (typeStr.Equals("On", System.StringComparison.OrdinalIgnoreCase))
				{ _warnVarUnset = _warnUnreachable = _warnDefaultMode; return; }
				_warnDefaultMode = typeStr.Equals("Off", System.StringComparison.OrdinalIgnoreCase) ? null : CanonWarnMode(typeStr);
				if (_warnVarUnset != null) _warnVarUnset = _warnDefaultMode;
				if (_warnUnreachable != null) _warnUnreachable = _warnDefaultMode;
				if (_warnLocalSameAsGlobal != null) _warnLocalSameAsGlobal = _warnDefaultMode;
				return;
			}
			var mode = modeStr.Length == 0 || modeStr.Equals("On", System.StringComparison.OrdinalIgnoreCase) ? _warnDefaultMode
					 : modeStr.Equals("Off", System.StringComparison.OrdinalIgnoreCase) ? null : CanonWarnMode(modeStr);
			switch (typeStr.ToLowerInvariant())
			{
				case "": case "all": _warnVarUnset = _warnUnreachable = _warnLocalSameAsGlobal = mode; break;
				case "varunset": _warnVarUnset = mode; break;
				case "unreachable": _warnUnreachable = mode; break;
				case "localsameasglobal": _warnLocalSameAsGlobal = mode; break;
				default: Diag($"#Warn: unrecognized warning type '{typeStr}'"); break;
			}
		}

		// Scans the module body for `#Warn` directives up-front so the config is location-independent (per the docs).
		private void PrescanWarnDirectives(List<Stmt> body)
		{
			foreach (var s in body)
				if (s is DirectiveStmt d && d.Name.Equals("Warn", System.StringComparison.OrdinalIgnoreCase))
					ApplyWarnDirective(d.Args);
		}

		private void Warn(string mode, int line, string desc) { if (mode != null) _warnings.Add((mode, line, desc)); }

		// Runs the enabled warning checks over a module body (the top-level scope) and all nested function/method/
		// property/lambda scopes. Must run AFTER the user-func/class prescan (so names resolve) and BEFORE lowering.
		private void AnalyzeWarnings(List<Stmt> body)
		{
			if (_warnVarUnset == null && _warnUnreachable == null && _warnLocalSameAsGlobal == null) return;
			var globals = _warnLocalSameAsGlobal != null ? new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) : null;
			if (globals != null) { var acc = new List<string>(); var seen = new HashSet<string>(); foreach (var s in body) CollectAssignedStmt(s, acc, seen); globals.UnionWith(acc); }
			AnalyzeScope(body, null, System.Array.Empty<string>(), globals, topLevel: true);
		}

		// Analyzes one scope (a statement list and/or an arrow expression) plus recurses into the scopes nested in it.
		private void AnalyzeScope(IReadOnlyList<Stmt> body, Expr arrow, System.Collections.Generic.IEnumerable<string> paramNames, HashSet<string> globals, bool topLevel)
		{
			if (_warnUnreachable != null && body != null) CheckUnreachable(body);
			// VarUnset / LocalSameAsGlobal both need the set of names "provided" in this scope.
			if (_warnVarUnset != null || (_warnLocalSameAsGlobal != null && !topLevel))
			{
				var provided = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
				foreach (var p in paramNames) provided.Add(p.ToLowerInvariant());
				var declaredGlobal = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
				if (body != null) foreach (var s in body) CollectProvided(s, provided, declaredGlobal);
				if (arrow != null) CollectProvidedExpr(arrow, provided, declaredGlobal);
				if (_warnVarUnset != null)
				{
					var warned = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
					if (body != null) foreach (var s in body) CheckReadsStmt(s, provided, warned);
					if (arrow != null) CheckReadsExpr(arrow, provided, warned);
				}
				if (_warnLocalSameAsGlobal != null && !topLevel && globals != null)
					foreach (var n in provided) if (!declaredGlobal.Contains(n) && globals.Contains(n)) Warn(_warnLocalSameAsGlobal, 0, $"This local variable has the same name as a global variable: {n}.");
			}
			// Recurse into nested scopes (their bodies are separate scopes).
			if (body != null) foreach (var s in body) RecurseScopes(s, globals);
			if (arrow != null) RecurseScopesExpr(arrow, globals);
		}

		private static bool IsFlowTerminator(Stmt s) => s is ReturnStmt or BreakStmt or ContinueStmt or ThrowStmt or GotoStmt;

		// Unreachable: a statement immediately following a Return/Break/Continue/Throw/Goto at the same level that is not
		// a label target. Applies within every statement list (recurses into nested blocks/bodies).
		private void CheckUnreachable(IReadOnlyList<Stmt> list)
		{
			for (int i = 0; i < list.Count; i++)
			{
				if (i + 1 < list.Count && IsFlowTerminator(list[i]) && list[i + 1] is not LabelStmt)
					Warn(_warnUnreachable, StmtLine(list[i + 1]), "This line will never be executed.");
			}
		}

		// Collects names "provided" (so a read of them is not unset) in THIS scope only (not descending into nested
		// function/lambda bodies): params, direct `:=` targets, `&var`, `IsSet(var)`, for/catch vars, and declarations.
		private void CollectProvided(Stmt s, HashSet<string> provided, HashSet<string> declaredGlobal)
		{
			switch (s)
			{
				case DeclStmt d:
					foreach (var item in d.Items) { var nm = DeclItemName(item); if (nm != null) { provided.Add(nm.ToLowerInvariant()); if (d.Keyword == "global") declaredGlobal.Add(nm.ToLowerInvariant()); } CollectProvidedExpr(item, provided, declaredGlobal); }
					break;
				case ExpressionStmt es: CollectProvidedExpr(es.Expr, provided, declaredGlobal); break;
				case ReturnStmt r: if (r.Value != null) CollectProvidedExpr(r.Value, provided, declaredGlobal); break;
				case ThrowStmt t: if (t.Value != null) CollectProvidedExpr(t.Value, provided, declaredGlobal); break;
				case IfStmt iff: CollectProvidedExpr(iff.Cond, provided, declaredGlobal); CollectProvided(iff.Then, provided, declaredGlobal); if (iff.Else != null) CollectProvided(iff.Else, provided, declaredGlobal); break;
				case Block b: foreach (var x in b.Body) CollectProvided(x, provided, declaredGlobal); break;
				case WhileStmt w: CollectProvidedExpr(w.Cond, provided, declaredGlobal); CollectProvided(w.Body, provided, declaredGlobal); break;
				case LoopStmt lp: if (lp.Count != null) CollectProvidedExpr(lp.Count, provided, declaredGlobal); CollectProvided(lp.Body, provided, declaredGlobal); break;
				case SpecialLoopStmt slp: if (slp.Args != null) foreach (var a in slp.Args) if (a != null) CollectProvidedExpr(a, provided, declaredGlobal); CollectProvided(slp.Body, provided, declaredGlobal); break;
				case ForStmt fr: foreach (var v in fr.Vars) if (v != null) provided.Add(v.ToLowerInvariant()); CollectProvidedExpr(fr.Enumerable, provided, declaredGlobal); CollectProvided(fr.Body, provided, declaredGlobal); break;
				case SwitchStmt sw:
					if (sw.Value != null) CollectProvidedExpr(sw.Value, provided, declaredGlobal);
					foreach (var c in sw.Cases) { foreach (var v in c.Values) CollectProvidedExpr(v, provided, declaredGlobal); foreach (var st in c.Body) CollectProvided(st, provided, declaredGlobal); }
					if (sw.Default != null) foreach (var st in sw.Default) CollectProvided(st, provided, declaredGlobal);
					break;
				case TryStmt tr:
					CollectProvided(tr.Body, provided, declaredGlobal);
					foreach (var cb in tr.Catches) { if (cb.Var != null) provided.Add(cb.Var.ToLowerInvariant()); CollectProvided(cb.Body, provided, declaredGlobal); }
					if (tr.Else != null) CollectProvided(tr.Else, provided, declaredGlobal);
					if (tr.Finally != null) CollectProvided(tr.Finally, provided, declaredGlobal);
					break;
			}
		}

		private void CollectProvidedExpr(Expr e, HashSet<string> provided, HashSet<string> declaredGlobal)
		{
			switch (e)
			{
				case AssignExpr a:
					if (a.Op == ":=" && a.Target is NameExpr tn) provided.Add(tn.Name.ToLowerInvariant());   // direct assignment
					CollectProvidedExpr(a.Target, provided, declaredGlobal); CollectProvidedExpr(a.Value, provided, declaredGlobal);
					break;
				case UnaryExpr u:
					if (u.Op == "&" && u.Operand is NameExpr rn) provided.Add(rn.Name.ToLowerInvariant());      // &var
					CollectProvidedExpr(u.Operand, provided, declaredGlobal);
					break;
				case CallExpr c:
					if (c.Callee is NameExpr ne && ne.Name.Equals("IsSet", System.StringComparison.OrdinalIgnoreCase)
						&& c.Args.Count == 1 && c.Args[0].Value is NameExpr iv) provided.Add(iv.Name.ToLowerInvariant());   // IsSet(var)
					CollectProvidedExpr(c.Callee, provided, declaredGlobal); foreach (var ar in c.Args) if (ar.Value != null) CollectProvidedExpr(ar.Value, provided, declaredGlobal);
					break;
				case BinaryExpr b: CollectProvidedExpr(b.Left, provided, declaredGlobal); CollectProvidedExpr(b.Right, provided, declaredGlobal); break;
				case TernaryExpr t: CollectProvidedExpr(t.Cond, provided, declaredGlobal); CollectProvidedExpr(t.Then, provided, declaredGlobal); CollectProvidedExpr(t.Else, provided, declaredGlobal); break;
				case GroupExpr g: CollectProvidedExpr(g.Inner, provided, declaredGlobal); break;
				case SequenceExpr sq: foreach (var it in sq.Items) CollectProvidedExpr(it, provided, declaredGlobal); break;
				case MemberExpr m: CollectProvidedExpr(m.Target, provided, declaredGlobal); break;
				case DynMemberExpr dm: CollectProvidedExpr(dm.Target, provided, declaredGlobal); CollectProvidedExpr(dm.NameExpr, provided, declaredGlobal); break;
				case IndexExpr ix: CollectProvidedExpr(ix.Target, provided, declaredGlobal); foreach (var ar in ix.Args) if (ar.Value != null) CollectProvidedExpr(ar.Value, provided, declaredGlobal); break;
				case ObjectExpr oe: foreach (var en in oe.Entries) { CollectProvidedExpr(en.Key, provided, declaredGlobal); CollectProvidedExpr(en.Value, provided, declaredGlobal); } break;
				case ArrayExpr ar2: foreach (var el in ar2.Elements) if (el.Value != null) CollectProvidedExpr(el.Value, provided, declaredGlobal); break;
				case MapExpr mp: foreach (var (k, v) in mp.Entries) { CollectProvidedExpr(k, provided, declaredGlobal); CollectProvidedExpr(v, provided, declaredGlobal); } break;
				case DerefExpr dr: CollectProvidedExpr(dr.Name, provided, declaredGlobal); break;
			}
		}

		// Walks reads; warns at the first read of a name that is not provided/param/builtin/user-func/class/keyword.
		// Does NOT descend into nested function/lambda bodies (separate scopes) or into the OK positions themselves.
		private void CheckReadsStmt(Stmt s, HashSet<string> provided, HashSet<string> warned)
		{
			switch (s)
			{
				case DeclStmt d: foreach (var item in d.Items) CheckReadsExpr(item, provided, warned); break;
				case ExpressionStmt es: CheckReadsExpr(es.Expr, provided, warned); break;
				case ReturnStmt r: if (r.Value != null) CheckReadsExpr(r.Value, provided, warned); break;
				case ThrowStmt t: if (t.Value != null) CheckReadsExpr(t.Value, provided, warned); break;
				case IfStmt iff: CheckReadsExpr(iff.Cond, provided, warned); CheckReadsStmt(iff.Then, provided, warned); if (iff.Else != null) CheckReadsStmt(iff.Else, provided, warned); break;
				case Block b: foreach (var x in b.Body) CheckReadsStmt(x, provided, warned); break;
				case WhileStmt w: CheckReadsExpr(w.Cond, provided, warned); CheckReadsStmt(w.Body, provided, warned); break;
				case LoopStmt lp: if (lp.Count != null) CheckReadsExpr(lp.Count, provided, warned); CheckReadsStmt(lp.Body, provided, warned); break;
				case SpecialLoopStmt slp: if (slp.Args != null) foreach (var a in slp.Args) if (a != null) CheckReadsExpr(a, provided, warned); CheckReadsStmt(slp.Body, provided, warned); break;
				case ForStmt fr: CheckReadsExpr(fr.Enumerable, provided, warned); CheckReadsStmt(fr.Body, provided, warned); break;
				case SwitchStmt sw:
					if (sw.Value != null) CheckReadsExpr(sw.Value, provided, warned);
					foreach (var c in sw.Cases) { foreach (var v in c.Values) CheckReadsExpr(v, provided, warned); foreach (var st in c.Body) CheckReadsStmt(st, provided, warned); }
					if (sw.Default != null) foreach (var st in sw.Default) CheckReadsStmt(st, provided, warned);
					break;
				case TryStmt tr:
					CheckReadsStmt(tr.Body, provided, warned);
					foreach (var cb in tr.Catches) CheckReadsStmt(cb.Body, provided, warned);
					if (tr.Else != null) CheckReadsStmt(tr.Else, provided, warned);
					if (tr.Finally != null) CheckReadsStmt(tr.Finally, provided, warned);
					break;
			}
		}

		private void CheckReadsExpr(Expr e, HashSet<string> provided, HashSet<string> warned)
		{
			switch (e)
			{
				case NameExpr n:
					var lo = n.Name.ToLowerInvariant();
					if (!provided.Contains(lo) && !warned.Contains(lo) && IsUnsetCandidate(lo))
					{ warned.Add(lo); Warn(_warnVarUnset, n.Line, $"This variable appears to never be assigned a value: {n.Name}."); }
					break;
				case AssignExpr a:   // a direct `:=` target is NOT a read; a compound/member/index target IS evaluated.
					if (!(a.Op == ":=" && a.Target is NameExpr)) CheckReadsExpr(a.Target, provided, warned);
					CheckReadsExpr(a.Value, provided, warned);
					break;
				case UnaryExpr u: if (u.Op != "&") CheckReadsExpr(u.Operand, provided, warned); break;   // &var is a provide, not a read
				case CallExpr c:
					var isIsSet = c.Callee is NameExpr cn && cn.Name.Equals("IsSet", System.StringComparison.OrdinalIgnoreCase) && c.Args.Count == 1 && c.Args[0].Value is NameExpr;
					CheckReadsExpr(c.Callee, provided, warned);
					if (!isIsSet) foreach (var ar in c.Args) if (ar.Value != null) CheckReadsExpr(ar.Value, provided, warned);
					break;
				case BinaryExpr b: CheckReadsExpr(b.Left, provided, warned); CheckReadsExpr(b.Right, provided, warned); break;
				case TernaryExpr t: CheckReadsExpr(t.Cond, provided, warned); CheckReadsExpr(t.Then, provided, warned); CheckReadsExpr(t.Else, provided, warned); break;
				case GroupExpr g: CheckReadsExpr(g.Inner, provided, warned); break;
				case SequenceExpr sq: foreach (var it in sq.Items) CheckReadsExpr(it, provided, warned); break;
				case MemberExpr m: CheckReadsExpr(m.Target, provided, warned); break;
				case DynMemberExpr dm: CheckReadsExpr(dm.Target, provided, warned); CheckReadsExpr(dm.NameExpr, provided, warned); break;
				case IndexExpr ix: CheckReadsExpr(ix.Target, provided, warned); foreach (var ar in ix.Args) if (ar.Value != null) CheckReadsExpr(ar.Value, provided, warned); break;
				case ObjectExpr oe: foreach (var en in oe.Entries) { if (en.Key is not NameExpr) CheckReadsExpr(en.Key, provided, warned); CheckReadsExpr(en.Value, provided, warned); } break;
				case ArrayExpr ar2: foreach (var el in ar2.Elements) if (el.Value != null) CheckReadsExpr(el.Value, provided, warned); break;
				case MapExpr mp: foreach (var (k, v) in mp.Entries) { CheckReadsExpr(k, provided, warned); CheckReadsExpr(v, provided, warned); } break;
				case DerefExpr dr: CheckReadsExpr(dr.Name, provided, warned); break;
				// FatArrowExpr: a separate scope, analyzed via RecurseScopes — do NOT check its reads here.
			}
		}

		// True when a bare name has no binding (so a read of it is statically unset): not a builtin var/func/type, a
		// user func/class, a value keyword, or a wildcard/inline alias.
		private bool IsUnsetCandidate(string lower)
		{
			if (lower is "this" or "true" or "false" or "unset" or "super") return false;
			if (lower.StartsWith("a_", System.StringComparison.Ordinal)) return false;   // built-in vars (A_*) — be conservative
			if (_userFuncByLower.ContainsKey(lower) || _userClassByLower.ContainsKey(lower)) return false;
			if (_inlineAliases.ContainsKey(lower)) return false;
			var rd = Script.TheScript.ReflectionsData;
			if (rd.flatPublicStaticProperties.ContainsKey(lower) || rd.flatPublicStaticMethods.ContainsKey(lower) || rd.stringToTypes.ContainsKey(lower)) return false;
			if (Keysharp.Parsing.Keywords.TypeNameAliases.Any(kv => kv.Value.Equals(lower, System.StringComparison.OrdinalIgnoreCase))) return false;
			return true;
		}

		// Recurses into the scopes nested in a statement (function/method/property/lambda bodies are separate scopes).
		private void RecurseScopes(Stmt s, HashSet<string> globals)
		{
			switch (s)
			{
				case FunctionDecl fd: AnalyzeScope(fd.Body?.Body, fd.ArrowBody, fd.Params.Select(p => p.Name), globals, topLevel: false); break;
				case ClassDecl cd:
					foreach (var m in cd.Methods) AnalyzeScope(m.Body?.Body, m.ArrowBody, m.Params.Select(p => p.Name), globals, topLevel: false);
					foreach (var pr in cd.Properties)
					{
						if (pr.HasGet) AnalyzeScope(pr.GetBody?.Body, pr.GetArrow, pr.Params.Select(p => p.Name), globals, topLevel: false);
						if (pr.HasSet) AnalyzeScope(pr.SetBody?.Body, pr.SetArrow, pr.Params.Select(p => p.Name).Append("value"), globals, topLevel: false);
					}
					foreach (var nc in cd.Nested) RecurseScopes(nc, globals);
					break;
				case Block b: foreach (var x in b.Body) RecurseScopes(x, globals); break;
				case IfStmt iff: RecurseScopes(iff.Then, globals); if (iff.Else != null) RecurseScopes(iff.Else, globals); RecurseScopesExpr(iff.Cond, globals); break;
				case WhileStmt w: RecurseScopesExpr(w.Cond, globals); RecurseScopes(w.Body, globals); break;
				case LoopStmt lp: RecurseScopes(lp.Body, globals); break;
				case SpecialLoopStmt slp: RecurseScopes(slp.Body, globals); break;
				case ForStmt fr: RecurseScopesExpr(fr.Enumerable, globals); RecurseScopes(fr.Body, globals); break;
				case SwitchStmt sw:
					foreach (var c in sw.Cases) foreach (var st in c.Body) RecurseScopes(st, globals);
					if (sw.Default != null) foreach (var st in sw.Default) RecurseScopes(st, globals);
					break;
				case TryStmt tr:
					RecurseScopes(tr.Body, globals);
					foreach (var cb in tr.Catches) RecurseScopes(cb.Body, globals);
					if (tr.Else != null) RecurseScopes(tr.Else, globals);
					if (tr.Finally != null) RecurseScopes(tr.Finally, globals);
					break;
				case ExpressionStmt es: RecurseScopesExpr(es.Expr, globals); break;
				case ReturnStmt r: if (r.Value != null) RecurseScopesExpr(r.Value, globals); break;
				case DeclStmt d: foreach (var item in d.Items) RecurseScopesExpr(item, globals); break;
			}
		}

		private void RecurseScopesExpr(Expr e, HashSet<string> globals)
		{
			switch (e)
			{
				case FatArrowExpr fa: AnalyzeScope(fa.BlockBody?.Body, fa.Body, fa.Params.Select(p => p.Name), globals, topLevel: false); break;
				case AssignExpr a: RecurseScopesExpr(a.Target, globals); RecurseScopesExpr(a.Value, globals); break;
				case BinaryExpr b: RecurseScopesExpr(b.Left, globals); RecurseScopesExpr(b.Right, globals); break;
				case UnaryExpr u: RecurseScopesExpr(u.Operand, globals); break;
				case TernaryExpr t: RecurseScopesExpr(t.Cond, globals); RecurseScopesExpr(t.Then, globals); RecurseScopesExpr(t.Else, globals); break;
				case GroupExpr g: RecurseScopesExpr(g.Inner, globals); break;
				case SequenceExpr sq: foreach (var it in sq.Items) RecurseScopesExpr(it, globals); break;
				case CallExpr c: RecurseScopesExpr(c.Callee, globals); foreach (var ar in c.Args) if (ar.Value != null) RecurseScopesExpr(ar.Value, globals); break;
				case MemberExpr m: RecurseScopesExpr(m.Target, globals); break;
				case DynMemberExpr dm: RecurseScopesExpr(dm.Target, globals); RecurseScopesExpr(dm.NameExpr, globals); break;
				case IndexExpr ix: RecurseScopesExpr(ix.Target, globals); foreach (var ar in ix.Args) if (ar.Value != null) RecurseScopesExpr(ar.Value, globals); break;
				case ObjectExpr oe: foreach (var en in oe.Entries) { RecurseScopesExpr(en.Key, globals); RecurseScopesExpr(en.Value, globals); } break;
				case ArrayExpr ar2: foreach (var el in ar2.Elements) if (el.Value != null) RecurseScopesExpr(el.Value, globals); break;
				case MapExpr mp: foreach (var (k, v) in mp.Entries) { RecurseScopesExpr(k, globals); RecurseScopesExpr(v, globals); } break;
			}
		}

		// Best-effort source line for a statement-level warning (statements carry no line; dig for a NameExpr).
		private static int StmtLine(Stmt s) => s switch
		{
			ExpressionStmt es => ExprLine(es.Expr),
			ReturnStmt r => r.Value != null ? ExprLine(r.Value) : 0,
			ThrowStmt t => t.Value != null ? ExprLine(t.Value) : 0,
			IfStmt iff => ExprLine(iff.Cond),
			WhileStmt w => ExprLine(w.Cond),
			_ => 0
		};
		private static int ExprLine(Expr e) => e switch
		{
			NameExpr n => n.Line,
			BinaryExpr b => NzOr(ExprLine(b.Left), ExprLine(b.Right)),
			UnaryExpr u => ExprLine(u.Operand),
			AssignExpr a => NzOr(ExprLine(a.Target), ExprLine(a.Value)),
			CallExpr c => NzOr(ExprLine(c.Callee), c.Args.Count > 0 && c.Args[0].Value != null ? ExprLine(c.Args[0].Value) : 0),
			MemberExpr m => ExprLine(m.Target),
			IndexExpr ix => ExprLine(ix.Target),
			GroupExpr g => ExprLine(g.Inner),
			_ => 0
		};
		private static int NzOr(int a, int b) => a != 0 ? a : b;

		// Generates the load-time warning output (one call per warning, per its mode), prepended to the outer auto-exec.
		private List<StatementSyntax> EmitWarnings()
		{
			var stmts = new List<StatementSyntax>();
			foreach (var (mode, line, desc) in _warnings)
			{
				var text = (line > 0 ? $"({line}) : ==> " : "") + desc;
				stmts.Add(mode switch
				{
					"StdOut" => CallStmt("Keysharp.Builtins.Files.FileAppend", Str(text + "\n"), Str("*")),
					"OutputDebug" => CallStmt("Keysharp.Builtins.Debug.OutputDebug", Str(text)),
					_ => CallStmt("Keysharp.Builtins.Dialogs.MsgBox", Str(text)),
				});
			}
			return stmts;
		}

		private static BlockSyntax AsBlock(StatementSyntax s) =>
			s as BlockSyntax ?? SyntaxFactory.Block(s ?? SyntaxFactory.EmptyStatement());

		private BlockSyntax LowerBlock(IEnumerable<Stmt> stmts) =>
			SyntaxFactory.Block(LowerStmtList(stmts as System.Collections.Generic.IReadOnlyList<Stmt> ?? stmts.ToList()));

		// ---- expressions ----

		private ExpressionSyntax LowerExpr(Expr e)
		{
			switch (e)
			{
				case LiteralExpr l: return l.Kind == LiteralKind.Number ? Num(l.Raw) : Str(DecodeString(l.Raw));
				case NameExpr n:
					// AHK reserves true/false/unset as value keywords (booleans 1/0, and the no-value sentinel null).
					switch (n.Name.ToLowerInvariant())
					{
						case "true": return Num("1");
						case "false": return Num("0");
						case "unset": return Null;
						case "super": return SuperTuple();
						// A_LineNumber/A_LineFile are folded to compile-time literals (the source line / script path) —
						// they have no runtime accessor and the canonical substitutes them the same way.
						case "a_linenumber" when !IsDeclaredLocal("a_linenumber"): return Num(n.Line.ToString());
						case "a_linefile" when !IsDeclaredLocal("a_linefile"): return Str(ScriptFullPath());
					}
					return NameRef(n.Name);
				case GroupExpr g: return SyntaxFactory.ParenthesizedExpression(LowerExpr(g.Inner));
				case SequenceExpr seq: return Op("MultiStatement", seq.Items.Select(LowerExpr).ToArray());
				case AssignExpr a: return LowerAssign(a);
				case BinaryExpr b:
					// && / and and || / or short-circuit and yield an operand (not a bool), so they can't be plain calls.
					if (b.Op == "&&" || b.Op.Equals("and", System.StringComparison.OrdinalIgnoreCase)) return LowerShortCircuit(b, andOp: true);
					if (b.Op == "||" || b.Op.Equals("or", System.StringComparison.OrdinalIgnoreCase)) return LowerShortCircuit(b, andOp: false);
					// `a ?? b` (maybe): the left's unset must yield null (not throw) so the default applies — rewrite its
					// outer GetPropertyValue/GetIndex/Invoke to the *OrNull form (as for IsSet).
					if (b.Op == "??") return Op("OrMaybe", RewriteToOrNull(LowerExpr(b.Left)), LowerExpr(b.Right));
					// `X is unset` / `X is null` test whether X is unset, so X itself must NOT raise when unset — make its
					// outer GetIndex/GetPropertyValue/Invoke lenient (`*OrNull`); `Is(null, null)` is then true.
					if (b.Op.Equals("is", System.StringComparison.OrdinalIgnoreCase)
						&& b.Right is NameExpr { Name: "unset" or "null" })
						return Op("Is", RewriteToOrNull(LowerExpr(b.Left)), Null);
					if (BinOps.TryGetValue(b.Op, out var m))
					{
						var le = LowerExpr(b.Left); var re = LowerExpr(b.Right);
						return TryFoldBinary(b.Op, le, re) ?? Op(m, le, re);   // constant-fold literal operands (1+2 -> 3L)
					}
					Diag($"operator not yet lowerable: '{b.Op}'"); return Str("");
				case UnaryExpr u: return LowerUnary(u);
				case TernaryExpr t:
					return SyntaxFactory.ParenthesizedExpression(
						SyntaxFactory.ConditionalExpression(IfTest(LowerExpr(t.Cond)), LowerExpr(t.Then), LowerExpr(t.Else)));
				// `?.` chains are unset-lenient: the base is guarded non-null by NullCondWrap, and the access itself uses
				// the *OrNull form so an unset-VALUED member yields null (which short-circuits the next `?.`) rather than
				// raising (e.g. `optObj?.prop?.inner` when `prop` is unset).
				case MemberExpr mem:
					return mem.NullConditional
						? NullCondWrap(mem.Target, tt => Op("GetPropertyValueOrNull", tt, Str(mem.Name)))
						: Op("GetPropertyValue", LowerExpr(mem.Target), Str(mem.Name));
				case DynMemberExpr dmem:
					return dmem.NullConditional
						? NullCondWrap(dmem.Target, tt => Op("GetPropertyValueOrNull", tt, LowerExpr(dmem.NameExpr)))
						: Op("GetPropertyValue", LowerExpr(dmem.Target), LowerExpr(dmem.NameExpr));
				case IndexExpr ix:
					// `obj.member[args]` folds the index into the member access (`GetPropertyValue(obj, "member", args)`)
					// so the runtime routes the args to an index-property getter (`Prop[i] { get }`) — or indexes the
					// member's value when it is a plain field. A bare `name[args]` / `expr[args]` stays a GetIndex.
					if (!ix.NullConditional && ix.Target is MemberExpr { NullConditional: false } im)
						return Op("GetPropertyValue", new[] { LowerExpr(im.Target), Str(im.Name) }.Concat(LowerArgs(ix.Args)).ToArray());
					if (!ix.NullConditional && ix.Target is DynMemberExpr { NullConditional: false } idm)
						return Op("GetPropertyValue", new[] { LowerExpr(idm.Target), LowerExpr(idm.NameExpr) }.Concat(LowerArgs(ix.Args)).ToArray());
					return ix.NullConditional
						? NullCondWrap(ix.Target, tt => Op("GetIndexOrNull", Cons(tt, LowerArgs(ix.Args))))
						: Op("GetIndex", Cons(LowerExpr(ix.Target), LowerArgs(ix.Args)));
				case ArrayExpr ar:
					var arArgs = ar.Elements.Any(el => el.Spread)
						? SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(Arg(SpreadParams(ar.Elements))))
						: SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(LowerArgs(ar.Elements).Select(Arg)));
					return SyntaxFactory.ObjectCreationExpression(Ty("Keysharp.Builtins.Array")).WithArgumentList(arArgs);
				case MapExpr mp:   // [k1: v1, k2: v2] -> new Map(k1, v1, k2, v2)
					var mapArgs = new List<ExpressionSyntax>();
					foreach (var (k, v) in mp.Entries) { mapArgs.Add(LowerExpr(k)); mapArgs.Add(LowerExpr(v)); }
					return SyntaxFactory.ObjectCreationExpression(Ty("Keysharp.Builtins.Map"))
						.WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(mapArgs.Select(Arg))));
				case ObjectExpr o: return LowerObject(o);
				case FatArrowExpr fa: return LowerFatArrow(fa);
				case DerefExpr dr:   // %name% read -> GetVar(name) (in a deref fn) or ModuleData.Vars[name] (global)
					return _inDerefFunc ? Inv(Id("GetVar"), LowerExpr(dr.Name)) : VarsIndex(LowerExpr(dr.Name));
				case CallExpr c: return LowerCall(c, statement: false);
				default: Diag($"expression not yet lowerable: {e.GetType().Name}"); return Str("");
			}
		}

		private ExpressionSyntax LowerAssign(AssignExpr a)
		{
			if (a.Target is UnaryExpr amp && amp.Op == "&")   // &x := v  ->  assign, then pass a ref to x
				return MakeRefFor(new AssignExpr(a.Op, amp.Operand, a.Value));
			if (a.Target is DerefExpr dt)   // %name% := v -> SetVar(name,v) (in a deref fn) or SetObject(Vars,name,v) (global)
			{
				ExpressionSyntax DerefRead(ExpressionSyntax nm) => _inDerefFunc ? Inv(Id("GetVar"), nm) : VarsIndex(nm);
				ExpressionSyntax DerefWrite(ExpressionSyntax nm, ExpressionSyntax v) => _inDerefFunc ? Inv(Id("SetVar"), nm, v) : VarsWrite(nm, v);
				if (a.Op == ":=")
					return DerefWrite(LowerExpr(dt.Name), LowerExpr(a.Value));
				// Compound `%name% op= v`: capture the (possibly side-effecting) name once, then write op(read, v) back.
				if (!BinOps.TryGetValue(a.Op[..^1], out var dop)) { Diag($"compound assignment to a dereference ('{a.Op}') not yet lowerable"); return Str(""); }
				var nt = NewTemp();
				return Op("MultiStatement", Assign(Id(nt), LowerExpr(dt.Name)),
					DerefWrite(Id(nt), Op(dop, DerefRead(Id(nt)), LowerExpr(a.Value))));
			}
			if (a.Target is MemberExpr me)
			{
				if (a.Op == ":=")   // target evaluated once — no temp needed
					return Op("SetPropertyValue", LowerExpr(me.Target), Str(me.Name), LowerExpr(a.Value));
				if (!BinOps.TryGetValue(a.Op[..^1], out var mm)) { Diag($"compound assignment to a member ('{a.Op}') not yet lowerable"); return Str(""); }
				// Compound: capture the target once so a side-effecting target expression runs a single time.
				var t = NewTemp();
				return Op("MultiStatement", Assign(Id(t), LowerExpr(me.Target)),
					Op("SetPropertyValue", Id(t), Str(me.Name), Op(mm, Op("GetPropertyValue", Id(t), Str(me.Name)), LowerExpr(a.Value))));
			}
			if (a.Target is DynMemberExpr dme)   // obj.%x% := v / obj.%x% op= v — capture target and the computed name once
			{
				if (a.Op == ":=")
					return Op("SetPropertyValue", LowerExpr(dme.Target), LowerExpr(dme.NameExpr), LowerExpr(a.Value));
				if (!BinOps.TryGetValue(a.Op[..^1], out var dmm)) { Diag($"compound assignment to a member ('{a.Op}') not yet lowerable"); return Str(""); }
				var dmt = NewTemp(); var dmn = NewTemp();
				return Op("MultiStatement", Assign(Id(dmt), LowerExpr(dme.Target)), Assign(Id(dmn), LowerExpr(dme.NameExpr)),
					Op("SetPropertyValue", Id(dmt), Id(dmn), Op(dmm, Op("GetPropertyValue", Id(dmt), Id(dmn)), LowerExpr(a.Value))));
			}
			if (a.Target is IndexExpr ie)
			{
				if (a.Op == ":=")
				{
					var argv = new List<ExpressionSyntax> { LowerExpr(ie.Target) };
					argv.AddRange(LowerArgs(ie.Args));
					argv.Add(LowerExpr(a.Value));
					return Op("SetObject", argv.ToArray());
				}
				if (!BinOps.TryGetValue(a.Op[..^1], out var im)) { Diag($"compound assignment to an index ('{a.Op}') not yet lowerable"); return Str(""); }
				// Compound: capture the target and each index once.
				var tt = NewTemp();
				var loweredArgs = LowerArgs(ie.Args);
				var argTemps = loweredArgs.Select(_ => NewTemp()).ToList();
				var ops = new List<ExpressionSyntax> { Assign(Id(tt), LowerExpr(ie.Target)) };
				for (int k = 0; k < argTemps.Count; k++) ops.Add(Assign(Id(argTemps[k]), loweredArgs[k]));
				List<ExpressionSyntax> IdxIds() => argTemps.Select(n => (ExpressionSyntax)Id(n)).ToList();
				var newVal = Op(im, Op("GetIndex", Cons(Id(tt), IdxIds())), LowerExpr(a.Value));
				var setArgs = new List<ExpressionSyntax> { Id(tt) };
				setArgs.AddRange(IdxIds());
				setArgs.Add(newVal);
				ops.Add(Op("SetObject", setArgs.ToArray()));
				return Op("MultiStatement", ops.ToArray());
			}
			if (a.Target is not NameExpr tn)
			{
				Diag($"unsupported assignment target at {a.Line}:{a.Column}: {a.Target.GetType().Name}" + (a.Target is BinaryExpr be ? $" op={be.Op}" : a.Target is UnaryExpr ue ? $" op={ue.Op}" : ""));
				return Str("");
			}
			// &param write: through the VarRef's __Value (rather than reassigning the local VarRef).
			if (_byRefParams != null && _byRefParams.Contains(tn.Name.ToLowerInvariant()))
			{
				var refId = Id(NameMangler.Escape(tn.Name.ToLowerInvariant()));
				var rv = a.Op == ":=" ? LowerExpr(a.Value)
					: BinOps.TryGetValue(a.Op[..^1], out var bm) ? Op(bm, Op("GetPropertyValue", refId, Str("__Value")), LowerExpr(a.Value))
					: null;
				if (rv == null) { Diag($"compound assignment '{a.Op}' not yet lowerable"); return Str(""); }
				return Op("SetPropertyValue", refId, Str("__Value"), rv);
			}
			var target = NameRef(tn.Name);

			ExpressionSyntax value;
			if (a.Op == ":=") value = LowerExpr(a.Value);
			else
			{
				var baseOp = a.Op[..^1];
				if (BinOps.TryGetValue(baseOp, out var m)) value = Op(m, target, LowerExpr(a.Value));
				else { Diag($"compound assignment '{a.Op}' not yet lowerable"); return Str(""); }
			}
			return Assign(target, value);
		}

		private StatementSyntax LowerDecl(DeclStmt d)
		{
			var stmts = new List<StatementSyntax>();
			foreach (var item in d.Items)
			{
				var name = DeclItemName(item);
				if (name == null) { Diag($"unsupported declaration item: {item.GetType().Name}"); continue; }
				var lower = name.ToLowerInvariant();
				var keyword = _locals != null ? d.Keyword : "global";

				if (keyword == "static")
				{
					if (_statics == null || !_statics.TryGetValue(lower, out var field)) { Diag($"static '{name}' not registered"); continue; }
					var value = item is AssignExpr sa && sa.Op == ":=" ? sa.Value : null;
					// A static with no initializer stays unset (the backing field defaults to null) — only emit the
					// one-time InitStaticVariable when there's an initializer, so `static z` ⇒ `z is unset` holds.
					if (value != null)
					{
						var lowered = LowerExpr(value);
						// A constant initializer (e.g. `static a := 1 + 2` → 3L) has no side effects, so initialize the
						// backing field directly (`SL_… = 3L`) instead of a lazy InitStaticVariable.
						if (lowered is LiteralExpressionSyntax && _staticFieldDeclIdx.TryGetValue(field, out var fi))
							_fieldDecls[fi] = ObjField(field, lowered);
						else
							stmts.Add(ExprStmt(InitStatic(field, lowered)));
					}
				}
				// A declaration item may also be an executable operation (`global x := 1`, `global x += 1`,
				// `global log .= "x"`, `global x++`): the name is declared and the operation runs.
				else if (item is AssignExpr a)
					stmts.Add(ExprStmt(LowerAssign(a)));
				else if (item is UnaryExpr)
					stmts.Add(LowerStmt(new ExpressionStmt(item)));
			}
			if (stmts.Count == 0) return null;
			return stmts.Count == 1 ? stmts[0] : SyntaxFactory.Block(stmts);
		}

		// The variable name a declaration item refers to (`x`, `x := v`, `x += v`, `x++`).
		private static string DeclItemName(Expr item) => item switch
		{
			NameExpr n => n.Name,
			AssignExpr a when a.Target is NameExpr tn => tn.Name,
			UnaryExpr u when (u.Op == "++" || u.Op == "--") && u.Operand is NameExpr un => un.Name,
			_ => null
		};


		// a && b -> (KS_temp = a, IfTest(KS_temp) ? b : KS_temp);  a || b -> (KS_temp = a, IfTest(KS_temp) ? KS_temp : b).
		// The temp captures the left operand once; the right is only evaluated when needed (true short-circuit),
		// and the result is the operand value (matching Script.BooleanAnd/Or) rather than a bool.
		private ExpressionSyntax LowerShortCircuit(BinaryExpr b, bool andOp)
		{
			var tmp = NewTemp();
			var assign = Assign(Id(tmp), LowerExpr(b.Left));
			var cond = IfTest(Id(tmp));
			var ternary = SyntaxFactory.ParenthesizedExpression(andOp
				? SyntaxFactory.ConditionalExpression(cond, LowerExpr(b.Right), Id(tmp))
				: SyntaxFactory.ConditionalExpression(cond, Id(tmp), LowerExpr(b.Right)));
			return Op("MultiStatement", assign, ternary);
		}

		private ExpressionSyntax LowerUnary(UnaryExpr u)
		{
			if (u.Op == "++" || u.Op == "--") return LowerIncDec(u);
			if (u.Postfix)
			{
				// Maybe / unset-permissive `expr?`: the inner call/member/index must not raise on an unset result, so
				// rewrite it to its *OrNull variant (`f()?` -> InvokeOrNull, `obj.p?` -> GetPropertyValueOrNull).
				if (u.Op == "?") return RewriteToOrNull(LowerExpr(u.Operand));
				Diag($"postfix '{u.Op}' not yet lowerable"); return Str("");
			}
			switch (u.Op)
			{
				case "!": case "not": return Op("LogicalNot", LowerExpr(u.Operand));
				case "-": return Op("Subtract", Num("0"), LowerExpr(u.Operand));
				case "+": return LowerExpr(u.Operand);
				case "&": return MakeRefFor(u.Operand);
				case "~": return Op("BitwiseNot", LowerExpr(u.Operand));
				default: Diag($"unary '{u.Op}' not yet lowerable"); return Str("");
			}
		}

		// ++/--. The write-back expression yields the NEW value. For the postfix form we capture the old value
		// into a temp first: `x++` -> MultiStatement(KS_temp = x, x = Add(x,1), KS_temp), so `y := x++` sees the
		// pre-increment value without re-deriving it (avoids a redundant op and any side effects).
		private ExpressionSyntax LowerIncDec(UnaryExpr u)
		{
			var op = u.Op == "++" ? "Add" : "Subtract";
			var setup = new List<ExpressionSyntax>();   // temp captures so a member/index target runs exactly once
			ExpressionSyntax read, write;
			switch (u.Operand)
			{
				// A by-ref param's value lives in its VarRef's __Value, so the write must go through SetPropertyValue
				// (the same as a compound assignment) — `GetPropertyValue(p,"__Value") = …` is not a C# lvalue.
				case NameExpr bn when _byRefParams != null && _byRefParams.Contains(bn.Name.ToLowerInvariant()):
					var brefId = Id(NameMangler.Escape(bn.Name.ToLowerInvariant()));
					read = Op("GetPropertyValue", brefId, Str("__Value"));
					write = Op("SetPropertyValue", brefId, Str("__Value"), Op(op, Op("GetPropertyValue", brefId, Str("__Value")), Num("1")));
					break;
				case NameExpr:
					read = LowerExpr(u.Operand);
					write = SyntaxFactory.ParenthesizedExpression(Assign(LowerExpr(u.Operand), Op(op, LowerExpr(u.Operand), Num("1"))));
					break;
				case MemberExpr me:
					var mt = NewTemp();
					setup.Add(Assign(Id(mt), LowerExpr(me.Target)));
					read = Op("GetPropertyValue", Id(mt), Str(me.Name));
					write = Op("SetPropertyValue", Id(mt), Str(me.Name), Op(op, Op("GetPropertyValue", Id(mt), Str(me.Name)), Num("1")));
					break;
				case IndexExpr ie:
					var it = NewTemp();
					setup.Add(Assign(Id(it), LowerExpr(ie.Target)));
					var idx = LowerArgs(ie.Args);
					var argTemps = idx.Select(_ => NewTemp()).ToList();
					for (int k = 0; k < argTemps.Count; k++) setup.Add(Assign(Id(argTemps[k]), idx[k]));
					List<ExpressionSyntax> IdxIds() => argTemps.Select(n => (ExpressionSyntax)Id(n)).ToList();
					read = Op("GetIndex", Cons(Id(it), IdxIds()));
					var setArgs = new List<ExpressionSyntax> { Id(it) };
					setArgs.AddRange(IdxIds());
					setArgs.Add(Op(op, Op("GetIndex", Cons(Id(it), IdxIds())), Num("1")));
					write = Op("SetObject", setArgs.ToArray());
					break;
				case DerefExpr dr:   // %n%++ : write back through the deref machinery
					read = LowerExpr(dr);
					var inc = Op(op, LowerExpr(dr), Num("1"));
					write = _inDerefFunc ? Inv(Id("SetVar"), LowerExpr(dr.Name), inc) : VarsWrite(LowerExpr(dr.Name), inc);
					break;
				default:
					Diag($"'{u.Op}' on {u.Operand.GetType().Name} not yet lowerable"); return Str("");
			}
			if (u.Postfix)   // capture targets, read the OLD value into a temp, write, then yield the old value
			{
				var old = NewTemp();
				var seq = new List<ExpressionSyntax>(setup) { Assign(Id(old), read), write, Id(old) };
				return Op("MultiStatement", seq.ToArray());
			}
			if (setup.Count == 0) return write;   // prefix on a plain name: the write expression yields the new value
			var pre = new List<ExpressionSyntax>(setup) { write };
			return Op("MultiStatement", pre.ToArray());
		}

		// IsSet(expr) must not throw when expr is an unset property/index/call: rewrite the argument's
		// GetPropertyValue/GetIndex/Invoke to its *OrNull variant (which yields null instead of erroring),
		// then IsSet's plain null-check works. Mirrors the canonical RewriteIsSetArgumentList.
		private static readonly Dictionary<string, string> IsSetOrNull = new(System.StringComparer.Ordinal)
		{ { "GetPropertyValue", "GetPropertyValueOrNull" }, { "GetIndex", "GetIndexOrNull" }, { "Invoke", "InvokeOrNull" } };

		private static ExpressionSyntax RewriteToOrNull(ExpressionSyntax e)
		{
			if (e is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma
				&& IsSetOrNull.TryGetValue(ma.Name.Identifier.Text, out var orNull))
				return inv.WithExpression(ma.WithName(SyntaxFactory.IdentifierName(orNull)));
			return e;
		}

		private ExpressionSyntax LowerCall(CallExpr c, bool statement)
		{
			// IsSet(member/index/call) must not throw on an unset target — rewrite the arg to its *OrNull form.
			bool isSet = c.Callee is NameExpr ne && ne.Name.Equals("IsSet", System.StringComparison.OrdinalIgnoreCase)
				&& c.Args.Count == 1 && c.Args[0].Value != null && !c.Args[0].Spread;

			// The call arguments (shared by both the normal and the null-conditional path).
			List<ExpressionSyntax> CallArgs() =>
				c.Args.Any(a => a.Spread) ? new() { SpreadParams(c.Args) }
				: isSet ? new() { RewriteToOrNull(LowerExpr(c.Args[0].Value)) }
				: LowerArgs(c.Args);

			// `obj?.M(args)` (null-conditional method) / `obj?.()` (null-conditional call): evaluate the target once;
			// if unset, short-circuit to null without evaluating the call (or its args). Matches the canonical chain.
			if (c.Callee is MemberExpr mc && mc.NullConditional)
				return NullCondWrap(mc.Target, tt => Op("Invoke", Cons2(tt, Str(mc.Name), CallArgs())));
			if (c.Callee is DynMemberExpr dmc && dmc.NullConditional)
				return NullCondWrap(dmc.Target, tt => Op("Invoke", Cons2(tt, LowerExpr(dmc.NameExpr), CallArgs())));
			if (c.NullConditional)
				return NullCondWrap(c.Callee, tt => Op("Invoke", Cons2(tt, Str("Call"), CallArgs())));

			List<ExpressionSyntax> args;
			if (c.Callee is MemberExpr m) args = new() { LowerExpr(m.Target), Str(m.Name) };
			else if (c.Callee is DynMemberExpr dm) args = new() { LowerExpr(dm.Target), LowerExpr(dm.NameExpr) };
			else args = new() { LowerExpr(c.Callee), Str("Call") };
			args.AddRange(CallArgs());
			return Op(statement ? "InvokeOrNull" : "Invoke", args.ToArray());
		}

		private ExpressionSyntax LowerObject(ObjectExpr o)
		{
			var args = new List<ExpressionSyntax> { Id(EnsureTypeField("Keysharp.Builtins.KeysharpObject", "object")), Str("Call") };
			foreach (var en in o.Entries) { args.Add(KeyToString(en.Key)); args.Add(LowerExpr(en.Value)); }
			return Op("Invoke", args.ToArray());
		}

		// An object-literal key. A bare identifier and a string literal are literal property names; everything else
		// (`%x%` deref, `(expr)`, numbers) is a DYNAMIC key — evaluated and forced to a string at runtime.
		private ExpressionSyntax KeyToString(Expr key) => key switch
		{
			NameExpr n => Str(n.Name),
			LiteralExpr l when l.Kind == LiteralKind.String => Str(DecodeString(l.Raw)),
			// `{ %x% : v }`: the key is the VALUE of x (a dynamic property name), not a variable deref of x's value.
			DerefExpr d => Op("ForceString", LowerExpr(d.Name)),
			_ => Op("ForceString", LowerExpr(key))
		};

		// Builds a collection expression for an argument list containing a spread, e.g. `[a, ..FlattenParam(arr), b]`.
		// Targets a params object[]: normal args are expression elements, `arr*` becomes `..FlattenParam(arr)`.
		private ExpressionSyntax SpreadParams(List<Argument> args)
		{
			var elems = args.Select(a =>
				a.Spread
					? (CollectionElementSyntax)SyntaxFactory.SpreadElement(Op("FlattenParam", a.Value == null ? Null : LowerExpr(a.Value)))
					: SyntaxFactory.ExpressionElement(a.Value == null ? Null : LowerExpr(a.Value)));
			return SyntaxFactory.CollectionExpression(SyntaxFactory.SeparatedList(elems));
		}

		private List<ExpressionSyntax> LowerArgs(List<Argument> args)
		{
			var list = new List<ExpressionSyntax>();
			foreach (var a in args)
			{
				if (a.Value == null) { list.Add(Null); continue; }   // omitted argument -> null (runtime treats as unset)
				list.Add(LowerExpr(a.Value));
			}
			return list;
		}

		// ---- control flow ----

		// while cond { body } -> Push; try { while(IfTest(cond)) { Inc(); body [; if(until) break] } } finally { Pop }
		// (Push/Inc/Pop give the loop a working A_Index, matching AHK.)
		private StatementSyntax LowerWhile(WhileStmt w)
		{
			var id = ++_flowCounter;
			var frame = PushLoop(id);
			var body = AsBlock(LowerStmt(w.Body));
			body = body.WithStatements(body.Statements.Insert(0, CallStmt("Keysharp.Runtime.Loops.Inc")));
			body = WrapLoopBody(body, w.Until, frame);
			PopLoop();
			var loop = SyntaxFactory.WhileStatement(IfTest(LowerExpr(w.Cond)), body);
			var block = new List<StatementSyntax> {
				CallStmt("Keysharp.Runtime.Loops.Push", Access("Keysharp.Runtime.LoopType.Normal")),
				TryFinally(loop, LoopFinally(w.Else)) };
			if (frame.NeedsEnd) block.Add(EndLabel(id));
			return SyntaxFactory.Block(block);
		}

		private StatementSyntax LowerLoop(LoopStmt lp)
		{
			// Both finite and infinite loops use Loops.Loop(count) (count -1 == infinite); its enumerator advances
			// A_Index on MoveNext and IsTrueAndRunning runs the per-iteration message check.
			var id = ++_flowCounter;
			var ev = "KS_e" + id;
			var countExpr = lp.Count == null ? Num("-1") : LowerExpr(lp.Count);
			var enumInit = Inv(Member(Inv(Access("Keysharp.Runtime.Loops.Loop"), countExpr), "GetEnumerator"));
			var cond = Inv(Access("Keysharp.Runtime.Flow.IsTrueAndRunning"), Inv(Member(Id(ev), "MoveNext")));
			var frame = PushLoop(id);
			var body = WrapLoopBody(AsBlock(LowerStmt(lp.Body)), lp.Until, frame);
			PopLoop();
			var loop = SyntaxFactory.WhileStatement(cond, body);
			var block = new List<StatementSyntax> {
				CallStmt("Keysharp.Runtime.Loops.Push", Access("Keysharp.Runtime.LoopType.Normal")),
				LocalDecl(Ty("System.Collections.IEnumerator"), ev, enumInit),
				TryFinally(loop, LoopFinally(lp.Else)) };
			if (frame.NeedsEnd) block.Add(EndLabel(id));
			return SyntaxFactory.Block(block);
		}

		// Maps a specialized-loop sub-keyword to its (enumerator method, LoopType) pair.
		private static readonly Dictionary<string, (string Method, string LoopType)> SpecialLoops = new(System.StringComparer.Ordinal)
		{
			{ "parse", ("LoopParse", "Parse") },
			{ "files", ("LoopFile", "Directory") },
			{ "read",  ("LoopRead", "File") },
			{ "reg",   ("LoopRegistry", "Registry") },
		};

		// Loop Parse/Files/Read/Reg <args>: same Push/while(MoveNext)/Pop shape as LowerLoop, but the enumerator is
		// the matching Loops.LoopXxx(args) and the pushed LoopType drives A_LoopField/A_LoopFile* accessors.
		private StatementSyntax LowerSpecialLoop(SpecialLoopStmt sl)
		{
			var (method, loopType) = SpecialLoops[sl.Kind];
			var id = ++_flowCounter;
			var ev = "KS_e" + id;
			var argExprs = sl.Args.Select(a => a == null ? Null : LowerExpr(a)).ToArray();
			var enumInit = Inv(Member(Inv(Access("Keysharp.Runtime.Loops." + method), argExprs), "GetEnumerator"));
			var cond = Inv(Access("Keysharp.Runtime.Flow.IsTrueAndRunning"), Inv(Member(Id(ev), "MoveNext")));
			var frame = PushLoop(id);
			var body = WrapLoopBody(AsBlock(LowerStmt(sl.Body)), sl.Until, frame);
			PopLoop();
			var loop = SyntaxFactory.WhileStatement(cond, body);
			var block = new List<StatementSyntax> {
				CallStmt("Keysharp.Runtime.Loops.Push", Access("Keysharp.Runtime.LoopType." + loopType)),
				LocalDecl(Ty("System.Collections.IEnumerator"), ev, enumInit),
				TryFinally(loop, LoopFinally(sl.Else)) };
			if (frame.NeedsEnd) block.Add(EndLabel(id));
			return SyntaxFactory.Block(block);
		}

		private static bool IsLoopStmt(Stmt s) => s is WhileStmt or LoopStmt or SpecialLoopStmt or ForStmt;

		// Pushes a loop frame (consuming any `name:` label that preceded this loop) and returns it.
		private LoopFrame PushLoop(int id)
		{
			var f = new LoopFrame { Id = id, Label = _pendingLoopLabel };
			_pendingLoopLabel = null;
			_loopFrames.Add(f);
			return f;
		}
		private void PopLoop() => _loopFrames.RemoveAt(_loopFrames.Count - 1);

		private static StatementSyntax NextLabel(int id) => SyntaxFactory.LabeledStatement("KS_e" + id + "_next", SyntaxFactory.EmptyStatement());
		private static StatementSyntax EndLabel(int id) => SyntaxFactory.LabeledStatement("KS_e" + id + "_end", SyntaxFactory.EmptyStatement());
		// A user label `name:` / goto target -> a mangled, collision-free C# label identifier.
		private static string UserLabelId(string name) => "KS_lbl_" + NameMangler.Escape(name.ToLowerInvariant());

		// Appends the trailing `Until` break and, when a level/label continue targeted this loop, the `_next:` label.
		private BlockSyntax WrapLoopBody(BlockSyntax body, Expr until, LoopFrame frame)
		{
			if (until != null)
				body = body.WithStatements(body.Statements.Add(SyntaxFactory.IfStatement(IfTest(LowerExpr(until)), SyntaxFactory.BreakStatement())));
			if (frame.NeedsNext)
				body = body.WithStatements(body.Statements.Add(NextLabel(frame.Id)));
			return body;
		}

		// break/continue always `goto`s the target loop's `_end`/`_next` label (never native C# break/continue): AHK
		// break/continue refer to the enclosing LOOP, and a switch is lowered as a real C# switch, so a native `break`
		// in a case would wrongly break the switch. Resolves an optional level-N or source-label target. Matches canonical.
		private StatementSyntax LowerBreakContinue(string target, bool isBreak)
		{
			if (_loopFrames.Count == 0)   // no enclosing loop (malformed) — emit a harmless native break/continue
				return isBreak ? SyntaxFactory.BreakStatement() : (StatementSyntax)SyntaxFactory.ContinueStatement();
			LoopFrame frame;
			if (target == null)
				frame = _loopFrames[^1];
			else if (int.TryParse(target, out var n) && n >= 1)
			{
				var idx = _loopFrames.Count - n;
				frame = _loopFrames[idx < 0 ? 0 : idx];
			}
			else
				frame = _loopFrames.LastOrDefault(f => f.Label != null && f.Label.Equals(target, System.StringComparison.OrdinalIgnoreCase))
					?? _loopFrames[^1];
			if (isBreak) { frame.NeedsEnd = true; return SyntaxFactory.GotoStatement(SyntaxKind.GotoStatement, Id("KS_e" + frame.Id + "_end")); }
			frame.NeedsNext = true; return SyntaxFactory.GotoStatement(SyntaxKind.GotoStatement, Id("KS_e" + frame.Id + "_next"));
		}

		private static HashSet<string> CollectDirectLabels(System.Collections.Generic.IEnumerable<Stmt> stmts)
		{
			var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
			foreach (var s in stmts) if (s is LabelStmt l) set.Add(l.Name);
			return set;
		}

		// Lowers a statement sequence in its own label scope, associating a `name:` label that immediately precedes a
		// loop with that loop (so `break name`/`continue name` can target it). Labels also emit a plain C# label.
		private List<StatementSyntax> LowerStmtList(System.Collections.Generic.IReadOnlyList<Stmt> stmts)
		{
			_labelScopes.Add(CollectDirectLabels(stmts));
			var result = new List<StatementSyntax>();
			for (int i = 0; i < stmts.Count; i++)
			{
				if (stmts[i] is LabelStmt ll && i + 1 < stmts.Count && IsLoopStmt(stmts[i + 1])) _pendingLoopLabel = ll.Name;
				var ls = LowerStmt(stmts[i]);
				if (ls != null) result.Add(ls);
			}
			_labelScopes.RemoveAt(_labelScopes.Count - 1);
			return result;
		}

		// The loop's `finally` body. Always pops the loop frame; when an `else` block is present it runs iff the
		// loop body never executed (`Loops.Pop().index == 0`), mirroring AHK loop-else and the canonical visitor.
		private StatementSyntax LoopFinally(Stmt elseStmt)
		{
			var pop = Inv(Access("Keysharp.Runtime.Loops.Pop"));
			if (elseStmt == null) return ExprStmt(pop);
			return SyntaxFactory.IfStatement(
				SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, Member(pop, "index"), Num("0")),
				AsBlock(LowerStmt(elseStmt)));
		}

		private StatementSyntax LowerFor(ForStmt fr)
		{
			var id = ++_flowCounter;
			var ev = "KS_e" + id;
			var meArgs = new List<ExpressionSyntax> { LowerExpr(fr.Enumerable) };
			// An omitted loop variable (`for (, v in arr)`) still consumes a slot: pass a discarding VarRef.
			meArgs.AddRange(fr.Vars.Select(v => v == null ? DiscardVarRef() : MakeRefFor(new NameExpr(v))));
			var enumInit = Inv(Member(Inv(Access("Keysharp.Runtime.Loops.MakeEnumerable"), meArgs.ToArray()), "GetEnumerator"));

			var frame = PushLoop(id);
			var body = AsBlock(LowerStmt(fr.Body));
			PopLoop();
			body = body.WithStatements(body.Statements.Insert(0, CallStmt("Keysharp.Runtime.Loops.Inc")));
			var cond = Inv(Access("Keysharp.Runtime.Flow.IsTrueAndRunning"), Inv(Member(Id(ev), "MoveNext")));
			var loop = SyntaxFactory.WhileStatement(cond, WrapLoopBody(body, fr.Until, frame));

			// AHK scopes for-loop variables: they're restored to their pre-loop values after the loop. Back each one
			// up into a temp before the loop and restore it in the finally (matches the canonical backup/restore).
			var loopVars = fr.Vars.Where(v => v != null).ToList();
			var backups = loopVars.Select(_ => NewTemp()).ToList();

			var finallyStmts = new List<StatementSyntax>();
			for (int i = 0; i < loopVars.Count; i++) finallyStmts.Add(ExprStmt(Assign(NameRef(loopVars[i]), Id(backups[i]))));
			finallyStmts.Add(LoopFinally(fr.Else));

			var block = new List<StatementSyntax> { CallStmt("Keysharp.Runtime.Loops.Push") };
			for (int i = 0; i < loopVars.Count; i++) block.Add(ExprStmt(Assign(Id(backups[i]), NameRef(loopVars[i]))));
			block.Add(LocalDeclVar(ev, enumInit));
			block.Add(TryFinally(loop, finallyStmts.Count == 1 ? finallyStmts[0] : SyntaxFactory.Block(finallyStmts)));
			if (frame.NeedsEnd) block.Add(EndLabel(id));
			return SyntaxFactory.Block(block);
		}

		// Lowered as a real C# `switch` (not an if-chain) so that all case bodies share one label scope — a `goto`
		// in one case can target a `name:` label in another (mirrors the canonical visitor). A value switch governs on
		// `ForceString(value)` with `case string KS_swN when KS_swN.Equals(caseValue)`; a value-less switch governs on
		// `true` with `case true when IfTest(cond)`. Each section ends with a (switch-)break; AHK user break/continue
		// inside a case go to the enclosing loop via goto (see LowerBreakContinue).
		private StatementSyntax LowerSwitch(SwitchStmt s)
		{
			bool valueless = s.Value == null;
			// Optional CaseSense arg: a literal 0/"off"/false makes string comparison case-insensitive (default: sensitive).
			bool insensitive = s.CaseSense is LiteralExpr cl &&
				(cl.Kind == LiteralKind.Number ? cl.Raw is "0" or "0.0"
				 : DecodeString(cl.Raw) is var cs && (cs.Equals("off", System.StringComparison.OrdinalIgnoreCase) || cs == "0" || cs.Equals("false", System.StringComparison.OrdinalIgnoreCase)));

			// All labels directly inside any case/default body share the switch's (single) C# label scope.
			var switchLabels = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
			foreach (var c in s.Cases) switchLabels.UnionWith(CollectDirectLabels(c.Body));
			if (s.Default != null) switchLabels.UnionWith(CollectDirectLabels(s.Default));
			_labelScopes.Add(switchLabels);

			SwitchLabelSyntax CaseLabel(Expr v)
			{
				if (valueless)
					return SyntaxFactory.CasePatternSwitchLabel(
						SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)),
						SyntaxFactory.WhenClause(IfTest(LowerExpr(v))), SyntaxFactory.Token(SyntaxKind.ColonToken));
				var pv = "KS_sw" + (++_flowCounter);
				var pattern = SyntaxFactory.DeclarationPattern(
					SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
					SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(pv)));
				var guard = insensitive
					? Inv(Member(Id(pv), "Equals"), CaseValueString(v), Access("System.StringComparison.OrdinalIgnoreCase"))
					: Inv(Member(Id(pv), "Equals"), CaseValueString(v));
				return SyntaxFactory.CasePatternSwitchLabel(pattern, SyntaxFactory.WhenClause(guard), SyntaxFactory.Token(SyntaxKind.ColonToken));
			}

			SwitchSectionSyntax Section(IEnumerable<SwitchLabelSyntax> labels, List<StatementSyntax> body)
			{
				body.Add(SyntaxFactory.BreakStatement());   // AHK cases don't fall through; terminate the C# section
				return SyntaxFactory.SwitchSection(SyntaxFactory.List(labels), SyntaxFactory.List(body));
			}

			var sections = new List<SwitchSectionSyntax>();
			foreach (var c in s.Cases)
				sections.Add(Section(c.Values.Select(CaseLabel), LowerStmtList(c.Body)));
			if (s.Default != null)
				sections.Add(Section(new SwitchLabelSyntax[] { SyntaxFactory.DefaultSwitchLabel() }, LowerStmtList(s.Default)));

			_labelScopes.RemoveAt(_labelScopes.Count - 1);

			var govern = valueless
				? (ExpressionSyntax)SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)
				: Op("ForceString", LowerExpr(s.Value));
			return SyntaxFactory.SwitchStatement(govern, SyntaxFactory.List(sections));
		}

		// The string a `case` value compares against. String/integer literals are folded to a constant at
		// lower-time (no runtime ForceString); everything else is forced to a string at runtime.
		private ExpressionSyntax CaseValueString(Expr v)
		{
			if (v is LiteralExpr l)
			{
				if (l.Kind == LiteralKind.String) return Str(DecodeString(l.Raw));
				if (l.Kind == LiteralKind.Number && TryFoldIntLiteral(l.Raw, out var folded)) return Str(folded);
			}
			return Op("ForceString", LowerExpr(v));
		}

		// Folds an integer literal (decimal/hex/octal/binary, optional sign/underscores) to its decimal string.
		// Floats and anything unparseable return false so the caller falls back to a runtime ForceString.
		private static bool TryFoldIntLiteral(string raw, out string result)
		{
			result = null;
			var t = raw.Replace("_", "");
			if (t.Length == 0 || t.Contains('.')) return false;
			bool neg = t.StartsWith("-"); if (neg || t.StartsWith("+")) t = t.Substring(1);
			bool isHex = t.StartsWith("0x") || t.StartsWith("0X");
			if (!isHex && (t.Contains('e') || t.Contains('E'))) return false;   // exponent => float in AHK
			try
			{
				long val =
					isHex ? System.Convert.ToInt64(t.Substring(2), 16) :
					(t.StartsWith("0b") || t.StartsWith("0B")) ? System.Convert.ToInt64(t.Substring(2), 2) :
					(t.StartsWith("0o") || t.StartsWith("0O")) ? System.Convert.ToInt64(t.Substring(2), 8) :
					long.Parse(t);
				result = (neg ? -val : val).ToString(System.Globalization.CultureInfo.InvariantCulture);
				return true;
			}
			catch { return false; }
		}

		private StatementSyntax LowerTry(TryStmt tr)
		{
			var body = AsBlock(LowerStmt(tr.Body));
			var finallyBlock = tr.Finally != null ? AsBlock(LowerStmt(tr.Finally)) : null;

			// try/catch/else: the else body runs only if the try completed without an exception — guard with a flag.
			if (tr.Else != null)
			{
				var ok = "KS_ok" + (++_flowCounter);
				body = body.WithStatements(body.Statements.Add(ExprStmt(Assign(Id(ok), SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)))));
				var inner = BuildTry(body, tr.Catches, null);
				var elseBlock = SyntaxFactory.Block(
					LocalDecl(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), ok, SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)),
					inner, SyntaxFactory.IfStatement(Id(ok), AsBlock(LowerStmt(tr.Else))));
				if (finallyBlock == null) return elseBlock;
				return SyntaxFactory.TryStatement().WithBlock(elseBlock).WithFinally(SyntaxFactory.FinallyClause(finallyBlock));
			}

			return BuildTry(body, tr.Catches, finallyBlock);
		}

		private StatementSyntax BuildTry(BlockSyntax body, List<CatchBlock> catchBlocks, BlockSyntax finallyBlock)
		{
			var catches = new List<CatchClauseSyntax>();
			foreach (var cb in catchBlocks)
				catches.Add(LowerCatch(cb));
			// Bare `try` (no catch, no finally) still swallows Keysharp exceptions to mirror AHK.
			if (catches.Count == 0 && finallyBlock == null)
				catches.Add(SyntaxFactory.CatchClause()
					.WithDeclaration(SyntaxFactory.CatchDeclaration(Ty("Keysharp.Builtins.KeysharpException")))
					.WithBlock(SyntaxFactory.Block()));

			// When the try actually catches (has catch clauses), register the caught types on the runtime try-stack for
			// the duration of the body, so a runtime-RAISED error (a ComCall HRESULT, a type/property error via
			// Errors.*Occurred) knows it is inside a catching `try` and throws to be caught here rather than surfacing
			// the unhandled-error dialog / exiting the thread. A `try`/`finally` with no catch does NOT catch, so it
			// gets no PushTry. PopTry runs via an inner finally so it is balanced even when the body throws.
			if (catches.Count > 0)
			{
				var caughtTypes = new List<string>();
				foreach (var cb in catchBlocks)
					if (cb.Types.Count == 0) caughtTypes.Add("Keysharp.Builtins.Error");
					else foreach (var t in cb.Types) caughtTypes.Add(ResolveErrorType(t));
				if (catchBlocks.Count == 0) caughtTypes.Add("Keysharp.Builtins.Error");   // synthetic bare-`try` catch
				var pushArgs = caughtTypes.Select(tn => (ExpressionSyntax)SyntaxFactory.TypeOfExpression(Ty(tn))).ToArray();
				var innerTry = SyntaxFactory.TryStatement().WithBlock(body)
					.WithFinally(SyntaxFactory.FinallyClause(SyntaxFactory.Block(CallStmt("Keysharp.Runtime.Loops.PopTry"))));
				body = SyntaxFactory.Block(CallStmt("Keysharp.Runtime.Loops.PushTry", pushArgs), innerTry);
			}

			var stmt = SyntaxFactory.TryStatement().WithBlock(body).WithCatches(SyntaxFactory.List(catches));
			if (finallyBlock != null) stmt = stmt.WithFinally(SyntaxFactory.FinallyClause(finallyBlock));
			return stmt;
		}

		// catch (KeysharpException ex) when (ex.UserError is Type1 || ...) { [var := ex.UserError;] body }
		private CatchClauseSyntax LowerCatch(CatchBlock cb)
		{
			var ex = "KS_ex" + (++_flowCounter);
			var userErr = Member(Id(ex), "UserError");
			var block = AsBlock(LowerStmt(cb.Body));
			if (cb.Var != null)
				block = block.WithStatements(block.Statements.Insert(0, ExprStmt(Assign(NameRef(cb.Var), userErr))));

			ExpressionSyntax cond;
			if (cb.Types.Count == 0)
				cond = SyntaxFactory.IsPatternExpression(userErr, SyntaxFactory.TypePattern(Ty("Keysharp.Builtins.Error")));
			else
			{
				cond = null;
				foreach (var t in cb.Types)
				{
					var test = SyntaxFactory.IsPatternExpression(userErr, SyntaxFactory.TypePattern(Ty(ResolveErrorType(t))));
					cond = cond == null ? test : SyntaxFactory.BinaryExpression(SyntaxKind.LogicalOrExpression, cond, test);
				}
			}
			return SyntaxFactory.CatchClause()
				.WithDeclaration(SyntaxFactory.CatchDeclaration(Ty("Keysharp.Builtins.KeysharpException"), SyntaxFactory.Identifier(ex)))
				.WithFilter(SyntaxFactory.CatchFilterClause(cond))
				.WithBlock(block);
		}

		// Resolve a catch type name to a C# type: a user class, a known builtin, else Keysharp.Builtins.<name>.
		private string ResolveErrorType(string name)
		{
			var lower = name.ToLowerInvariant();
			if (_userClassByLower.TryGetValue(lower, out var ut)) return ut;
			if (Script.TheScript.ReflectionsData.stringToTypes.TryGetValue(lower, out var type)) return type.FullName.Replace('+', '.');
			return "Keysharp.Builtins." + name;
		}

		private StatementSyntax LowerThrow(ThrowStmt th)
		{
			var errType = Ty("Keysharp.Builtins.Error");
			if (th.Value == null)
				return SyntaxFactory.ThrowStatement(SyntaxFactory.ObjectCreationExpression(errType).WithArgumentList(SyntaxFactory.ArgumentList()));
			var v = LowerExpr(th.Value);
			if (v is LiteralExpressionSyntax)
				return SyntaxFactory.ThrowStatement(SyntaxFactory.ObjectCreationExpression(errType)
					.WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(Arg(v)))));
			return SyntaxFactory.ThrowStatement(SyntaxFactory.CastExpression(errType, SyntaxFactory.ParenthesizedExpression(v)));
		}

		// ---- classes ----

		// Resolves a dotted builtin base type like `Gui.Custom` (root via stringToTypes / a type-name alias, then nested
		// types case-insensitively) to its C# full name, or null if not a builtin.
		private static string ResolveDottedBuiltinType(string dotted)
		{
			if (dotted == null || !dotted.Contains('.')) return null;
			var parts = dotted.Split('.');
			var rootLower = parts[0].ToLowerInvariant();
			if (!Script.TheScript.ReflectionsData.stringToTypes.TryGetValue(rootLower, out var t))
			{
				var aliasKey = Keysharp.Parsing.Keywords.TypeNameAliases.FirstOrDefault(kv => kv.Value.Equals(rootLower, System.StringComparison.OrdinalIgnoreCase)).Key;
				if (aliasKey == null || !Script.TheScript.ReflectionsData.stringToTypes.TryGetValue(aliasKey, out t)) return null;
			}
			for (int i = 1; i < parts.Length && t != null; i++)
				t = t.GetNestedTypes(System.Reflection.BindingFlags.Public).FirstOrDefault(n => n.Name.Equals(parts[i], System.StringComparison.OrdinalIgnoreCase));
			return t?.FullName.Replace('+', '.');
		}

		private void RegisterClass(ClassDecl c, string outerLower = null, string outerType = null)
		{
			var lower = outerLower == null ? c.Name.ToLowerInvariant() : outerLower + "." + c.Name.ToLowerInvariant();
			var typeName = outerType == null ? NameMangler.ClassType(c.Name) : outerType + "." + NameMangler.ClassType(c.Name);
			_userClassByLower[lower] = typeName;
			if (outerLower == null)   // only top-level classes get a global slot field; nested ones are static props of the parent
			{
				var id = NameMangler.Escape(lower);
				_fields[lower] = id;
				_classFieldIds.Add(id);
				_fieldDecls.Add(ObjArrowProp(id, TypeSingleton(typeName)));
			}
			// Register nested classes under their dotted path (`Outer.Inner` -> `OuterType.InnerType`) so a sibling can
			// extend them by qualified name (e.g. `class B extends Outer.A`, common in UIA.ahk).
			foreach (var nc in c.Nested) RegisterClass(nc, lower, typeName);
		}

		private MemberDeclarationSyntax LowerClass(ClassDecl c)
		{
			var baseType = c.IsStruct ? "Keysharp.Builtins.Struct" : "Keysharp.Builtins.KeysharpObject";
			if (c.Base != null)
			{
				var bl = c.Base.ToLowerInvariant();
				if (_userClassByLower.TryGetValue(bl, out var bn)) baseType = bn;
				else if (Script.TheScript.ReflectionsData.stringToTypes.TryGetValue(bl, out var bt)) baseType = bt.FullName.Replace('+', '.');
				else if (ResolveDottedBuiltinType(c.Base) is { } dotted) baseType = dotted;   // e.g. `Gui.Custom`
				else Diag($"extending unknown class '{c.Base}' is not yet lowerable");
			}

			var typeName = NameMangler.ClassType(c.Name);
			var savedBase = _currentClassBase; _currentClassBase = baseType;
			var savedCompat = _currentCompat;
			_currentCompat = (c.Requires != null ? MapRequires(c.Requires) : null) ?? _currentCompat;   // class-level `#Requires`
			var members = new List<MemberDeclarationSyntax> { ClassCtor(typeName) };
			foreach (var m in c.Methods) members.Add(LowerMethod(m, typeName));
			foreach (var pr in c.Properties) members.AddRange(LowerProperty(pr));
			// Nested classes become nested C# types; the runtime registers them as static properties on the parent.
			foreach (var nc in c.Nested) members.Add(LowerClass(nc));

			var instFields = c.Fields.Where(f => !f.Static && f.Init != null).ToList();
			if (instFields.Count > 0 || c.InstanceInit.Count > 0)
				members.Add(InitMethod(NameMangler.InstanceInit, baseType, instFields, null, c.InstanceInit, staticCtx: false));
			var statFields = c.Fields.Where(f => f.Static && f.Init != null).ToList();
			// A struct's typed fields are registered on the prototype (ObjDefineProp with the field's .NET type) in the
			// static initializer, so the runtime knows the struct layout.
			var typedFields = c.Fields.Where(f => f.TypeName != null).ToList();
			_structTypeName = typeName;
			var staticPre = typedFields.Select(StructFieldDefineProp).Where(s => s != null).ToList();
			if (statFields.Count > 0 || staticPre.Count > 0 || c.StaticInit.Count > 0)
				members.Add(InitMethod(NameMangler.StaticInit(), null, statFields, staticPre, c.StaticInit, staticCtx: true));

			var decl = SyntaxFactory.ClassDeclaration(typeName)
				.AddModifiers(PublicTok)
				.WithBaseList(BaseList(baseType))
				.WithMembers(SyntaxFactory.List(members));
			// Preserve the user's original spelling so the runtime can resolve the class by name (and display it).
			if (!string.Equals(typeName, c.Name, System.StringComparison.Ordinal))
				decl = decl.AddAttributeLists(Attr("Keysharp.Runtime.UserDeclaredName", Str(c.Name)));
			_currentClassBase = savedBase;
			_currentCompat = savedCompat;
			return decl;
		}

		// Registers a typed struct field on the prototype: `Objects.ObjDefineProp(Prototypes[typeof(Struct)], "x", typeof(FieldType))`.
		private StatementSyntax StructFieldDefineProp(ClassField f)
		{
			var fieldType = ResolveStructFieldType(f.TypeName);
			if (fieldType == null) { Diag($"unknown struct field type '{f.TypeName}'"); return null; }
			var proto = SyntaxFactory.ElementAccessExpression(Access("MainScript.Vars.Prototypes"))
				.WithArgumentList(SyntaxFactory.BracketedArgumentList(SyntaxFactory.SingletonSeparatedList(Arg(SyntaxFactory.TypeOfExpression(Ty(_structTypeName))))));
			return ExprStmt(Inv(Access("Keysharp.Builtins.Objects.ObjDefineProp"), proto, Str(f.Name), SyntaxFactory.TypeOfExpression(Ty(fieldType))));
		}

		// Resolves a struct field/base type name to its C# type: a user struct/class, or a builtin (Int32 -> StructInt32).
		private string ResolveStructFieldType(string raw)
		{
			var lower = raw.ToLowerInvariant();
			if (_userClassByLower.TryGetValue(lower, out var un)) return un;
			if (Script.TheScript.ReflectionsData.stringToTypes.TryGetValue(lower, out var bt)) return bt.FullName.Replace('+', '.');
			return null;
		}

		// __Init / static__Init: optionally chain the base prototype's __Init, then run extra prologue statements
		// (typed struct-field registrations) and set each field.
		private MemberDeclarationSyntax InitMethod(string name, string baseProtoType, List<ClassField> fields,
			List<StatementSyntax> prologue = null, List<Stmt> extra = null, bool staticCtx = false)
		{
			var savedScopeFuncs = _pendingScopeFuncs; _pendingScopeFuncs = new();   // field-init fat-arrow local funcs
			var stmts = new List<StatementSyntax>();
			if (prologue != null) stmts.AddRange(prologue);
			if (baseProtoType != null)
			{
				var proto = SyntaxFactory.ElementAccessExpression(Access("MainScript.Vars.Prototypes"))
					.WithArgumentList(SyntaxFactory.BracketedArgumentList(SyntaxFactory.SingletonSeparatedList(Arg(SyntaxFactory.TypeOfExpression(Ty(baseProtoType))))));
				var tuple = SyntaxFactory.CastExpression(ObjType,
					SyntaxFactory.TupleExpression(SyntaxFactory.SeparatedList(new[] { Arg(proto), Arg(Id("@this")) })));
				stmts.Add(ExprStmt(Op("Invoke", tuple, Str("__Init"))));
			}
			foreach (var f in fields) stmts.Add(FieldSet(f));
			// Member/index-target initializers (`static x.y := z`) run after the plain field sets, with `this` bound to
			// the class object (static) or instance.
			if (extra != null && extra.Count > 0)
			{
				var savedM = _inMethod; var savedS = _currentMethodStatic; _inMethod = true; _currentMethodStatic = staticCtx;
				foreach (var st in extra) { var ls = LowerStmt(st); if (ls != null) stmts.Add(ls); }
				_inMethod = savedM; _currentMethodStatic = savedS;
			}
			stmts.AddRange(_pendingScopeFuncs);   // fat-arrow field values become local funcs that capture @this
			stmts.Add(SyntaxFactory.ReturnStatement(Str("")));
			_pendingScopeFuncs = savedScopeFuncs;
			return ObjMethod(name, ParamThis(), SyntaxFactory.Block(stmts));
		}

		// `super` lowers to the tuple (object)(MainScript.Vars.{Prototypes|Statics}[typeof(Base)], @this); the runtime
		// resolves a member/method against the base's prototype while keeping `this` bound to the current instance.
		private ExpressionSyntax SuperTuple()
		{
			var baseType = _currentClassBase ?? "Keysharp.Builtins.KeysharpObject";
			var table = _currentMethodStatic ? "MainScript.Vars.Statics" : "MainScript.Vars.Prototypes";
			var proto = SyntaxFactory.ElementAccessExpression(Access(table))
				.WithArgumentList(SyntaxFactory.BracketedArgumentList(SyntaxFactory.SingletonSeparatedList(Arg(SyntaxFactory.TypeOfExpression(Ty(baseType))))));
			return SyntaxFactory.CastExpression(ObjType,
				SyntaxFactory.TupleExpression(SyntaxFactory.SeparatedList(new[] { Arg(proto), Arg(Id("@this")) })));
		}

		private StatementSyntax FieldSet(ClassField f)
		{
			var saved = _inMethod; _inMethod = true;
			var v = LowerExpr(f.Init);
			_inMethod = saved;
			return ExprStmt(Op("SetPropertyValue", Id("@this"), Str(f.Name), v));
		}

		private MemberDeclarationSyntax LowerMethod(ClassMethod m, string classType)
		{
			var paramLowers = m.Params.Select(p => p.Name.ToLowerInvariant()).ToHashSet();
			var implName = m.Static ? NameMangler.StaticMethod(m.Name) : NameMangler.Method(m.Name);
			// A method whose impl name collides with the enclosing type (or its constructor) must be renamed;
			// the runtime still resolves it via the UserDeclaredName attribute.
			bool renamed = !m.Static && implName == classType;
			if (renamed) implName += "_KSm";
			var saved = _inMethod; _inMethod = true;
			var savedStatic = _currentMethodStatic; _currentMethodStatic = m.Static;
			var savedCompat = _currentCompat;
			_currentCompat = ScanRequires(m.Body?.Body) ?? _currentCompat;   // a `#Requires` in the method body sets its mode
			var body = LowerCallableBody(paramLowers, m.Body, m.ArrowBody, implName, ByRefSet(m.Params), m.Params);
			_inMethod = saved; _currentMethodStatic = savedStatic;
			var attrs = new List<AttributeListSyntax>();
			// Preserve the exact source-case name whenever the emitted C# identifier differs from it (the mangler
			// TitleCases, prefixes statics, suffixes renamed collisions). OwnProps() enumeration and case-sensitive
			// (`==`/`!==`) user comparisons need the original casing; the runtime resolves via this attribute.
			if (!string.Equals(implName, m.Name, StringComparison.Ordinal))
				attrs.Add(Attr("Keysharp.Runtime.UserDeclaredName", Str(m.Name)));
			attrs.AddRange(CompatAttr());
			_currentCompat = savedCompat;
			return ObjMethod(implName, ParamDecls(m.Params, includeThis: true, wrapVariadics: true), body, attrs.ToArray());
		}

		private List<MemberDeclarationSyntax> LowerProperty(ClassProperty pr)
		{
			var result = new List<MemberDeclarationSyntax>();
			var idxLowers = pr.Params.Select(p => p.Name.ToLowerInvariant()).ToList();
			// Index params with full variadic (`a*` -> params object[]) / optional handling, like a normal param list.
			// wrapVariadics so the body sees an Array (`__Item[a*]` uses `a.Length`); LowerCallableBody adds the wrap.
			List<ParameterSyntax> IdxParams() => ParamDecls(pr.Params, includeThis: false, wrapVariadics: true).Parameters.ToList();
			// A static property's accessors get the ClassStaticPrefix (registered on the class's static object); `this`
			// inside them is the static class object. Otherwise identical to instance-property accessors.
			var getterName = pr.Static ? NameMangler.StaticGetter(pr.Name) : NameMangler.Getter(pr.Name);
			var setterName = pr.Static ? NameMangler.StaticSetter(pr.Name) : NameMangler.Setter(pr.Name);

			if (pr.HasGet)
			{
				var savedM = _inMethod; var savedS = _currentMethodStatic; _inMethod = true; _currentMethodStatic = pr.Static;
				var body = LowerCallableBody(new HashSet<string>(idxLowers), pr.GetBody, pr.GetArrow, getterName, ByRefSet(pr.Params), pr.Params);
				_inMethod = savedM; _currentMethodStatic = savedS;
				var ps = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(Prepend(ThisParam(), IdxParams().ToArray())));
				result.Add(ObjMethod(getterName, ps, body, Attr("Keysharp.Runtime.UserDeclaredName", Str(pr.Name))));
			}
			if (pr.HasSet)
			{
				var setParams = new HashSet<string>(idxLowers) { "value" };
				var savedM = _inMethod; var savedS = _currentMethodStatic; _inMethod = true; _currentMethodStatic = pr.Static;
				var body = LowerCallableBody(setParams, pr.SetBody, pr.SetArrow, setterName, ByRefSet(pr.Params), pr.Params);
				_inMethod = savedM; _currentMethodStatic = savedS;
				var idx = IdxParams();
				// `value` is always the LAST setter param, so a trailing `params object[]` index param drops `params`
				// (C# requires params to be last) — it becomes a plain object[] the runtime fills with the index args.
				if (idx.Count > 0 && idx[^1].Modifiers.Any(SyntaxKind.ParamsKeyword))
					idx[^1] = idx[^1].WithModifiers(SyntaxFactory.TokenList());
				var all = new List<ParameterSyntax> { ThisParam() };
				all.AddRange(idx);
				all.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(NameMangler.Escape("value"))).WithType(ObjType));
				result.Add(ObjMethod(setterName, SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(all)),
					body, Attr("Keysharp.Runtime.UserDeclaredName", Str(pr.Name))));
			}
			return result;
		}

		// ---- functions ----

		// A fat-arrow used as a value: emit it as a C# LOCAL FUNCTION in the current callable scope and bind it as a
		// FuncObj via its method group (`Func((Delegate)FN)`). Because it's a local function it captures `@this` and
		// the enclosing locals through normal C# closure semantics (matches the canonical — no Functions.Closure /
		// top-level method needed). The local functions are flushed into the scope body by LowerCallableBody /
		// InitMethod / the auto-exec assembly (C# hoists local functions, so forward references are fine).
		private ExpressionSyntax LowerFatArrow(FatArrowExpr fa)
		{
			var n = ++_lambdaCounter;
			var implName = "FN_KS_AnonLambda_" + n;
			var paramLowers = fa.Params.Select(p => p.Name.ToLowerInvariant()).ToHashSet();
			var savedCaptured = _capturedInScope; _capturedInScope = false;
			// A named fn-expression `name(params) => …` can call itself: resolve `name` inside the body to a Func over
			// this lambda's impl (the local function is hoisted, so a forward self-reference is fine). Save/restore any
			// outer alias of the same name. Skip when the name is a real local/param (it shadows the self-reference).
			string selfName = fa.Name?.ToLowerInvariant();
			bool aliasInBody = selfName != null && !paramLowers.Contains(selfName)
				&& !(_locals != null && _locals.Contains(selfName));
			System.Func<ExpressionSyntax> savedAlias = null;
			bool hadAlias = aliasInBody && _inlineAliases.TryGetValue(selfName, out savedAlias);
			if (aliasInBody) _inlineAliases[selfName] = () => FuncBind(implName);
			var body = LowerCallableBody(paramLowers, fa.BlockBody, fa.BlockBody == null ? fa.Body : null, implName, ByRefSet(fa.Params), fa.Params, capturing: true);
			if (aliasInBody) { if (hadAlias) _inlineAliases[selfName] = savedAlias; else _inlineAliases.Remove(selfName); }
			bool captured = _capturedInScope; _capturedInScope = savedCaptured;
			_pendingScopeFuncs.Add(SyntaxFactory.LocalFunctionStatement(ObjType, SyntaxFactory.Identifier(implName))
				.WithParameterList(ParamDecls(fa.Params, includeThis: false, wrapVariadics: true))
				.WithBody(body));
			// A closure that captured `this`/an enclosing local binds as a Closure (so `x is Closure`); otherwise a Func.
			return captured ? ClosureBind(implName) : FuncBind(implName);
		}

		// A bare nested function: emit a capturing C# local function (like a fat-arrow) and bind its name to a local
		// FuncObj/Closure var (declared at this position) so `name(...)` calls it. Captures `@this`/enclosing locals.
		private StatementSyntax LowerNestedClosure(FunctionDecl fd)
		{
			var nameLower = fd.Name.ToLowerInvariant();
			var implName = "FN_" + NameMangler.Escape(nameLower) + "_" + (++_lambdaCounter);
			var paramLowers = fd.Params.Select(p => p.Name.ToLowerInvariant()).ToHashSet();
			// The name is already in _scopeClosureNames (pre-collected by the enclosing scope), so a recursive reference
			// in the body resolves to this local closure var rather than a module field.
			var savedCaptured = _capturedInScope; _capturedInScope = false;
			var savedCompat = _currentCompat;
			_currentCompat = ScanRequires(fd.Body?.Body) ?? _currentCompat;   // nested-function `#Requires` (restored after)
			var body = LowerCallableBody(paramLowers, fd.Body, fd.ArrowBody, implName, ByRefSet(fd.Params), fd.Params, capturing: true, staticNested: fd.Static);
			_currentCompat = savedCompat;
			bool captured = _capturedInScope; _capturedInScope = savedCaptured;
			_pendingScopeFuncs.Add(SyntaxFactory.LocalFunctionStatement(ObjType, SyntaxFactory.Identifier(implName))
				.WithParameterList(ParamDecls(fd.Params, includeThis: false, wrapVariadics: true))
				.WithBody(body));
			// Assign (not declare — the var is hoisted) at the scope TOP so forward-referenced calls work and a
			// self-recursive binding doesn't trip definite assignment; the delegate's captures read lazily.
			_pendingScopeClosureInits.Add(ExprStmt(Assign(Id(NameMangler.Escape(nameLower)), captured ? ClosureBind(implName) : FuncBind(implName))));
			return null;
		}

		// ---- hotkeys / hotstrings / remaps ----

		private static readonly ExpressionSyntax UintZero = SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0u));

		// Strips the trailing `::` from a raw trigger, restores a dangling backtick, and resolves AHK escapes
		// (matches the canonical VisitHotkey/VisitHotstring trigger handling).
		private static string ProcessTriggerText(string rawWithColons)
		{
			var t = rawWithColons.Substring(0, rawWithColons.Length - 2);   // strip trailing `::`
			if (t.Length > 0 && t[^1] == '`' && (t.Length < 2 || t[^2] != '`')) t += '`';
			return Keysharp.Parsing.Parser.EscapedString(t, true);
		}

		// Emits a callback method (`public static object <name>(object thishotkey) { … }`) onto __Main and returns its name.
		private string EmitHotCallback(Block body, string name)
		{
			var ps = new List<Param> { new Param("thishotkey", null, false, false, false) };
			var lowered = LowerCallableBody(new HashSet<string> { "thishotkey" }, body, null, name);
			_hotMembers.Add(ObjMethod(name, ParamDecls(ps, includeThis: false), lowered));
			return name;
		}

		private void LowerHotkey(HotkeyDef hk)
		{
			_persistent = true;
			string fnName;
			if (hk.Func != null)
			{
				if (_emittedFuncImpls.Add(NameMangler.FunctionMethod(hk.Func.Name))) _hotMembers.Add(LowerFunction(hk.Func));
				fnName = NameMangler.FunctionMethod(hk.Func.Name);
			}
			else
			{
				var block = hk.Body as Block ?? new Block(new List<Stmt> { hk.Body });
				fnName = EmitHotCallback(block, "__Hotkey_" + (++_hotCount));
			}
			foreach (var trig in hk.Triggers)
			{
				var text = ProcessTriggerText(trig);
				_dhhr.Add(ExprStmt(Inv(Access("Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey"),
					FuncBind("__Main." + fnName), UintZero, Str(text))));
			}
		}

		private void LowerHotstring(HotstringDef hs)
		{
			_persistent = true;
			bool hasExpansion = hs.Expansion != null;
			string expansionText = hasExpansion ? Keysharp.Parsing.Parser.EscapedString(hs.Expansion, true) : "";
			ExpressionSyntax funcArg = Null;
			if (!hasExpansion)
			{
				string fnName;
				if (hs.Func != null)
				{
					if (_emittedFuncImpls.Add(NameMangler.FunctionMethod(hs.Func.Name))) _hotMembers.Add(LowerFunction(hs.Func));
					fnName = NameMangler.FunctionMethod(hs.Func.Name);
				}
				else
				{
					var block = hs.Body as Block ?? new Block(new List<Stmt> { hs.Body ?? new Block(new List<Stmt>()) });
					fnName = EmitHotCallback(block, "__Hotstring_" + (++_hotCount));
				}
				funcArg = FuncBind("__Main." + fnName);
			}
			foreach (var trig in hs.Triggers)
			{
				var name = ProcessTriggerText(trig);   // `:opts:key` (escapes resolved)
				var colon = name.IndexOf(':', 1);
				var options = colon > 0 ? name.Substring(1, colon - 1) : "";
				var key = colon > 0 ? name.Substring(colon + 1) : name;
				_dhhr.Add(ExprStmt(Inv(Access("Keysharp.Runtime.Keyboard.HotstringManager.AddHotstring"),
					Str(name), funcArg, Str($"{options}:{key}"), Str(key), Str(expansionText), False)));
			}
		}

		private static StatementSyntax SetDelay(bool isMouse) =>
			CallStmt("Keysharp.Builtins." + (isMouse ? "Mouse.SetMouseDelay" : "Keyboard.SetKeyDelay"),
				SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(-1L)));
		private static StatementSyntax SendStmt(string text) => CallStmt("Keysharp.Builtins.Keyboard.Send", Str(text));

		private MemberDeclarationSyntax RemapCallback(string name, List<StatementSyntax> body) =>
			SyntaxFactory.MethodDeclaration(ObjType, SyntaxFactory.Identifier(name)).AddModifiers(PublicTok, StaticTok)
				.WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
					SyntaxFactory.Parameter(SyntaxFactory.Identifier("args")).WithType(ObjArrayType).AddModifiers(SyntaxFactory.Token(SyntaxKind.ParamsKeyword)))))
				.WithBody(SyntaxFactory.Block(body));

		// Port of VisitGeneral.VisitRemap: turns `source::target` into a down hotkey and an up hotkey that Send the
		// target key, computing the Send strings at compile time from the runtime key tables.
		private void LowerRemap(RemapDef rm)
		{
			_persistent = true;
			// The lexer already split the remap into its source/target key text; just decode the backtick escapes.
			var sourceKey = Keysharp.Parsing.Parser.EscapedString(rm.Source, true);
			var targetKey = Keysharp.Parsing.Parser.EscapedString(rm.Target, true);

			uint remapDestVk = 0u, remapDestSc = 0u; uint? modLR = null, modifiersLR = null;
			var remapName = targetKey; var hotName = sourceKey;
			var ht = Script.TheScript.HookThread;
			var kbLayout = GetKeyboardLayout(0);

			remapName = HotkeyDefinition.TextToModifiers(remapName, null);
			var remapDestSource = KeySource.None;
			ht.TextToVKandSC(remapName, ref remapDestVk, ref remapDestSc, ref remapDestSource, ref modLR, kbLayout);

			var tempcp1 = HotkeyDefinition.TextToModifiers(hotName, null);
			var remapSourceVk = ht.TextToVK(tempcp1, ref modifiersLR, kbLayout);
			var remapSourceIsCombo = tempcp1.Contains(HotkeyDefinition.COMPOSITE_DELIMITER);
			var remapSourceIsMouse = MouseUtils.IsMouseVK(remapSourceVk);
			var remapDestIsMouse = MouseUtils.IsMouseVK(remapDestVk);
			var remapKeybdToMouse = !remapSourceIsMouse && remapDestIsMouse;
			var remapWheel = MouseUtils.IsWheelVK(remapSourceVk) || MouseUtils.IsWheelVK(remapDestVk);
			var remapSource = (remapSourceIsCombo ? "" : "*") + (tempcp1.Length == 1 && char.IsUpper(tempcp1[0]) ? "+" : "") + hotName;

			var remapDest = remapName[0] == '"' ? "\"" : remapName;
			var remapDestModifiers = targetKey.Substring(0, targetKey.IndexOf(remapName));
			var remapDestKey = (Keys)remapDestVk;

			if (remapDestKey == Keys.Pause && remapDestModifiers.Length == 0 && string.Compare(remapDest, "Pause", true) == 0)
				return;   // a hotkey to pause the script, not a remap to the Pause key

			var altTabAction = HotkeyDefinition.ConvertAltTab(targetKey, false);
			if (altTabAction != 0)
			{
				_dhhr.Add(ExprStmt(Inv(Access("Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey"),
					Null, SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(altTabAction)), Str(hotName))));
				return;
			}

			var blindMods = "";
			var temphk = new HotkeyDefinition(999, null, (uint)HotkeyTypeEnum.Normal, hotName, 0);
			for (var i = 0; i < 8; ++i)
				if ((temphk.modifiersConsolidatedLR & (1 << i)) != 0 && !remapDestModifiers.Contains(KeyboardMouseSender.ModLRString[i * 2 + 1]))
				{ blindMods += KeyboardMouseSender.ModLRString[i * 2]; blindMods += KeyboardMouseSender.ModLRString[i * 2 + 1]; }

			var p = $"{{Blind{blindMods}}}{remapDestModifiers}{{{remapDest}{(remapWheel ? "" : " DownR")}}}";

			var downName = "__Remap_" + (++_hotCount);
			var upName = "__Remap_" + (++_hotCount);
			var downStatements = new List<StatementSyntax> { SetDelay(remapDestIsMouse) };
			if (remapKeybdToMouse && !remapWheel)
				downStatements.Add(SyntaxFactory.IfStatement(
					SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
						IfTest(Inv(Access("Keysharp.Builtins.Keyboard.GetKeyState"), Str(remapDest))), False),
					SyntaxFactory.Block(SendStmt(p))));
			else
				downStatements.Add(SendStmt(p));
			downStatements.Add(SyntaxFactory.ReturnStatement(Str("")));
			_hotMembers.Add(RemapCallback(downName, downStatements));
			_hotMembers.Add(RemapCallback(upName, new List<StatementSyntax>
				{ SetDelay(remapDestIsMouse), SendStmt($"{{Blind}}{{{remapDest} Up}}"), SyntaxFactory.ReturnStatement(Str("")) }));

			_dhhr.Add(ExprStmt(Inv(Access("Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey"),
				FuncBind("__Main." + downName), UintZero, Str(remapSource))));
			_dhhr.Add(ExprStmt(Inv(Access("Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey"),
				FuncBind("__Main." + upName), UintZero, Str(remapSource + " up"))));
		}

		// `#Hotstring` directive: sets default options (`#Hotstring X`), end chars, or mouse-reset for subsequent
		// hotstrings (mirrors VisitHotstringDirective). Emitted into the DHHR so it precedes later registrations.
		private void LowerHotstringDirective(DirectiveStmt d)
		{
			var args = (d.Args ?? "").Trim();
			if (args.StartsWith("NoMouse", System.StringComparison.OrdinalIgnoreCase))
				_dhhr.Insert(0, CallStmt("Keysharp.Builtins.Keyboard.Hotstring", Str("MouseReset"), False));
			else if (args.StartsWith("EndChars", System.StringComparison.OrdinalIgnoreCase))
				_dhhr.Insert(0, CallStmt("Keysharp.Builtins.Keyboard.Hotstring", Str("EndChars"),
					Str(Keysharp.Parsing.Parser.EscapedString(args.Substring("EndChars".Length).Trim().Trim('"', '\''), false))));
			else
				_dhhr.Add(CallStmt("Keysharp.Builtins.Keyboard.HotstringOptions", Str(args.Trim('"', '\''))));
		}

		// `#HotIf <expr>` registers a hot-criterion: the condition becomes a callback (called with the hotkey name,
		// like a hotkey body) whose return value gates the following hotkeys/hotstrings; a bare `#HotIf` clears it
		// with `HotIf("")`. Emitted into the DHHR at this source position so it brackets the AddHotkey calls.
		private void LowerHotIf(DirectiveStmt d)
		{
			var args = (d.Args ?? "").Trim();
			if (args.Length == 0)
			{
				_dhhr.Add(ExprStmt(Inv(Access("Keysharp.Builtins.Keyboard.HotIf"), Str(""))));
				return;
			}
			var cond = ParseExprFragment(args);
			if (cond == null) { Diag($"#HotIf condition is not a valid expression: '{args}'"); return; }
			var fnName = EmitHotCallback(new Block(new List<Stmt> { new ReturnStmt(cond) }), "__HotIf_" + (++_hotCount));
			_dhhr.Add(ExprStmt(Inv(Access("Keysharp.Builtins.Keyboard.HotIf"), FuncBind("__Main." + fnName))));
		}

		// Re-parses a directive's reconstructed expression argument (e.g. a `#HotIf` condition) back into an Expr.
		// Returns null if it doesn't parse cleanly to a single expression.
		private static Expr ParseExprFragment(string text)
		{
			if (string.IsNullOrWhiteSpace(text)) return null;
			var (prog, diags) = Keysharp.Parsing.Syntax.Parser.ParseWithDiagnostics(text);
			return diags.Count == 0 && prog.Body.Count > 0 && prog.Body[0] is ExpressionStmt es ? es.Expr : null;
		}

		private MemberDeclarationSyntax LowerFunction(FunctionDecl f)
		{
			var savedCompat = _currentCompat;
			_currentCompat = ScanRequires(f.Body?.Body) ?? _currentCompat;   // a `#Requires` in the body sets this function's mode
			var paramLowers = f.Params.Select(p => p.Name.ToLowerInvariant()).ToHashSet();
			var implName = NameMangler.FunctionMethod(f.Name);
			var body = LowerCallableBody(paramLowers, f.Body, f.ArrowBody, implName, ByRefSet(f.Params), f.Params);
			var attrs = new List<AttributeListSyntax> { Attr("Keysharp.Runtime.UserDeclaredName", Str(f.Name)) };
			attrs.AddRange(CompatAttr());
			var method = ObjMethod(implName, ParamDecls(f.Params, includeThis: false, wrapVariadics: true), body, attrs.ToArray());
			_currentCompat = savedCompat;
			return method;
		}

		// Emits a C# parameter list. name:=literal -> [Optional, DefaultParameterValue]; name? -> [Optional];
		// name* -> params object[]. By-ref '&name' params get the [ByRef] attribute (a VarRef is passed).
		// The variadic param is exposed to the body as an AHK Array; the raw `params object[]` is the `KS_`-prefixed
		// signature param, and LowerCallableBody prepends `<name> = new Array(KS_<name>)` (see VariadicWrap).
		// The `KS_` prefix already guarantees a non-keyword identifier, so do NOT @-escape (a variadic param named
		// `params` must become `KS_params`, never the invalid `KS_@params`).
		private static string VariadicRawName(string name) => "KS_" + name.ToLowerInvariant();

		private ParameterListSyntax ParamDecls(List<Param> ps, bool includeThis, bool wrapVariadics = false)
		{
			var list = new List<ParameterSyntax>();
			if (includeThis) list.Add(ThisParam());
			foreach (var p in ps)
			{
				var ident = SyntaxFactory.Identifier(NameMangler.Escape(p.Name.ToLowerInvariant()));
				ParameterSyntax param;
				if (p.Variadic)
					param = SyntaxFactory.Parameter(wrapVariadics ? SyntaxFactory.Identifier(VariadicRawName(p.Name)) : ident)
						.WithType(ObjArrayType).AddModifiers(SyntaxFactory.Token(SyntaxKind.ParamsKeyword));
				else
				{
					param = SyntaxFactory.Parameter(ident).WithType(ObjType);
					if (p.ByRef) param = param.AddAttributeLists(Attr("Keysharp.Runtime.ByRef"));   // &name: receives a VarRef
					// Optionality is independent of by-ref: `&name?` / `&name:=v` are optional by-ref params, so the
					// [Optional]/[DefaultParameterValue] attributes must still be applied (else the runtime counts them
					// as required and rejects calls that omit them).
					if (p.Default != null)
					{
						var d = LowerExpr(p.Default);
						// A constant default goes straight into the attribute; a non-constant one defaults to null and gets a
						// `param ??= <expr>` prologue. A by-ref default is ALWAYS declared null so an omitted `&p` arrives null
						// (its default is substituted into the VarRef in LowerCallableBody, never via the attribute).
						param = param.AddAttributeLists(Attr("System.Runtime.InteropServices.Optional"),
													   Attr("System.Runtime.InteropServices.DefaultParameterValue", d is LiteralExpressionSyntax && !p.ByRef ? d : Null));
					}
					else if (p.Optional) param = param.AddAttributeLists(Attr("System.Runtime.InteropServices.Optional"));
				}
				list.Add(param);
			}
			return SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(list));
		}

		private BlockSyntax LowerCallableBody(HashSet<string> paramLowers, Block bodyBlock, Expr arrowBody, string funcName, HashSet<string> byRefParams = null, List<Param> paramDefaults = null, bool capturing = false, bool staticNested = false)
		{
			// A `static` nested function resolves sibling nested-function names (so `static f2() => f1()` works) but does
			// NOT capture the enclosing function's variable locals/statics (AHK semantics) — split those two facets here.
			bool varCapture = capturing && !staticNested;
			var savedByRef = _byRefParams;
			_byRefParams = byRefParams;
			var savedTemps = _scopeTemps; _scopeTemps = new();
			var savedScopeFuncs = _pendingScopeFuncs; _pendingScopeFuncs = new();   // fat-arrow local funcs for THIS scope
			var savedClosureInits = _pendingScopeClosureInits; _pendingScopeClosureInits = new();
			var forcedGlobals = new HashSet<string>();
			var explicitLocals = new HashSet<string>();
			var statics = new Dictionary<string, string>();
			if (bodyBlock != null) foreach (var st in bodyBlock.Body) PrescanDecls(st, forcedGlobals, explicitLocals, statics, funcName);

			var assignedOrdered = new List<string>();
			var seen = new HashSet<string>();
			if (arrowBody != null) CollectAssignedExpr(arrowBody, assignedOrdered, seen);
			else if (bodyBlock != null) foreach (var st in bodyBlock.Body) CollectAssignedStmt(st, assignedOrdered, seen);

			var savedLocals = _locals; var savedForced = _forcedGlobals; var savedStatics = _statics;
			// Only a CLOSURE (fat-arrow / bare nested function) captures the enclosing scope's locals by reference; a
			// module-level function/method does NOT (it's not lexically a C# local function), so reset to empty there.
			var savedEnclosing = _enclosingLocals;
			_enclosingLocals = !varCapture ? new HashSet<string>()
				: savedLocals == null ? savedEnclosing
				: new HashSet<string>(savedEnclosing.Concat(savedLocals));
			// A capturing closure also sees the enclosing scope's statics (module-level SL_ fields) for derefs.
			var savedEnclosingStatics = _enclosingStatics;
			_enclosingStatics = !varCapture ? new Dictionary<string, string>(System.StringComparer.Ordinal)
				: new Dictionary<string, string>(savedEnclosingStatics.Concat(savedStatics ?? Enumerable.Empty<KeyValuePair<string, string>>())
					.GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value), System.StringComparer.Ordinal);
			// A bare `global` declaration switches the function to assume-global: assigned variables default
			// to global storage unless explicitly declared local/static.
			bool assumeGlobal = bodyBlock != null && bodyBlock.Body.Any(st => st is DeclStmt d && d.Keyword == "global" && d.Items.Count == 0);
			_forcedGlobals = forcedGlobals;
			_statics = statics;
			var savedScopeClosureNames = _scopeClosureNames;
			// Bare nested-function names declared in THIS scope's body (become local closure vars, hoisted below).
			var ownClosureNames = new HashSet<string>(System.StringComparer.Ordinal);
			if (bodyBlock != null) foreach (var st in bodyBlock.Body) CollectBareNestedFuncNames(st, ownClosureNames);
			// A capturing scope (fat-arrow / bare nested function) ALSO sees the enclosing scope's nested-closure names, so
			// a reference to a sibling/enclosing closure resolves to the captured local rather than a module field.
			_scopeClosureNames = capturing ? new HashSet<string>(savedScopeClosureNames, System.StringComparer.Ordinal)
											: new HashSet<string>(System.StringComparer.Ordinal);
			_scopeClosureNames.UnionWith(ownClosureNames);
			// A bare `static` declaration switches the function to assume-static: undeclared assigned variables become
			// per-function STATIC locals (persisting across calls) rather than fresh locals — they need backing SL_ fields
			// like explicit statics. `global` wins if both are present. Params, explicit locals, and nested-closure names
			// are excluded (the latter so `_statics` doesn't shadow the closure var in NameRef).
			bool assumeStatic = !assumeGlobal && bodyBlock != null
				&& bodyBlock.Body.Any(st => st is DeclStmt d && d.Keyword == "static" && d.Items.Count == 0);
			if (assumeStatic)
				foreach (var n in assignedOrdered)
					if (!forcedGlobals.Contains(n) && !statics.ContainsKey(n) && !explicitLocals.Contains(n)
						&& !paramLowers.Contains(n) && !_enclosingLocals.Contains(n) && !ownClosureNames.Contains(n))
					{
						var field = NameMangler.StaticLocalField(funcName, n);
						statics[n] = field;
						_staticFieldDeclIdx[field] = _fieldDecls.Count;
						_fieldDecls.Add(ObjField(field, Null));
					}
			_locals = new HashSet<string>(paramLowers);
			if (!assumeGlobal)
				foreach (var n in assignedOrdered)
					if (!forcedGlobals.Contains(n) && !statics.ContainsKey(n) && !_enclosingLocals.Contains(n)) _locals.Add(n);
			foreach (var n in explicitLocals) _locals.Add(n);
			// In a method (or a closure nested in one), `this` is always the special @this (the receiver), never a local —
			// even when assigned (`this := 0`), which reassigns the captured receiver. Don't shadow it with a null local.
			if (_inMethod) _locals.Remove("this");

			var body = new List<StatementSyntax>();
			var hoisted = new HashSet<string>(paramLowers);
			void Hoist(string n) { if (_locals.Contains(n) && hoisted.Add(n)) body.Add(DeclLocal(ObjType, NameMangler.Escape(n), Null)); }
			foreach (var n in assignedOrdered) Hoist(n);
			foreach (var n in explicitLocals) Hoist(n);
			// Declare this scope's nested-closure vars up front (`object f = null;`) so a self-/mutually-recursive binding
			// `f = Closure(FN_f)` (where FN_f captures f) satisfies C# definite assignment.
			foreach (var n in ownClosureNames) if (hoisted.Add(n)) body.Add(DeclLocal(ObjType, NameMangler.Escape(n), Null));

			// Expose a variadic param to the body as an AHK Array: `object <name> = new Array(KS_<name>)` (matches the
			// canonical). The raw `params object[]` keeps the `KS_`-prefixed signature name (see ParamDecls). Declared
			// BEFORE the deref GetVar/SetVar functions, which may reference it by name.
			if (paramDefaults != null)
				foreach (var p in paramDefaults)
					if (p.Variadic)
						body.Add(LocalDecl(ObjType, NameMangler.Escape(p.Name.ToLowerInvariant()),
							SyntaxFactory.ObjectCreationExpression(Ty("Keysharp.Builtins.Array"))
								.WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(Arg(Id(VariadicRawName(p.Name))))))));

			// If the body dereferences (%name%), expose this scope through local GetVar/SetVar functions
			// (zero-alloc: they're only called, never converted to delegates). Globals/statics fall back
			// to MainScript.ModuleData.Vars; the switch maps locals + statics by name directly.
			var savedDeref = _inDerefFunc;
			_inDerefFunc = (bodyBlock != null && bodyBlock.Body.Any(HasDerefStmt)) || (arrowBody != null && HasDerefExpr(arrowBody));
			if (_inDerefFunc) { body.Add(BuildGetVar()); body.Add(BuildSetVar()); }

			// A by-ref param is read/written through its VarRef's __Value. Only when the caller OMITS an optional `&p`
			// does it arrive null (by-ref defaults are declared as null, see ParamDecls) — substitute a VarRef holding
			// the declared default (or "") so __Value access behaves like a local. A passed argument is left untouched:
			// it may be a real VarRef OR a "virtual reference" (an object exposing `.Value`/`__Value` but not derived
			// from VarRef) — re-wrapping the latter in a new VarRef would sever it from its backing store.
			if (paramDefaults != null)
				foreach (var p in paramDefaults)
					if (p.ByRef && !p.Variadic)
					{
						var rid = Id(NameMangler.Escape(p.Name.ToLowerInvariant()));
						var deflt = p.Default != null ? LowerExpr(p.Default) : Str("");
						body.Add(ExprStmt(SyntaxFactory.AssignmentExpression(SyntaxKind.CoalesceAssignmentExpression, rid,
							SyntaxFactory.ObjectCreationExpression(Ty("Keysharp.Builtins.VarRef"))
								.WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(Arg(deflt)))))));
					}

			// Non-literal parameter defaults: the param is declared [Optional, DefaultParameterValue(null)],
			// so assign the real default when the caller omitted it (`param ??= <expr>`).
			if (paramDefaults != null)
				foreach (var p in paramDefaults)
					if (!p.ByRef && !p.Variadic && p.Default != null && LowerExpr(p.Default) is not LiteralExpressionSyntax)
						body.Add(ExprStmt(SyntaxFactory.AssignmentExpression(SyntaxKind.CoalesceAssignmentExpression,
							Id(NameMangler.Escape(p.Name.ToLowerInvariant())), LowerExpr(p.Default))));

			// A fat-arrow `=> expr` propagates an unset result rather than raising (unlike an explicit `return f()`),
			// so its outermost call/member/index uses the non-raising *OrNull form.
			var setupEnd = body.Count;   // boundary after local hoists + param setup, before the executable body
			if (arrowBody != null) body.Add(SyntaxFactory.ReturnStatement(RewriteToOrNull(LowerExpr(arrowBody))));
			else if (bodyBlock != null) body.AddRange(LowerStmtList(bodyBlock.Body));

			if (body.Count == 0 || body[^1] is not ReturnStatementSyntax)
				body.Add(SyntaxFactory.ReturnStatement(DefaultReturnExpr()));

			// Declare any temps introduced by postfix ++/-- at the top of the scope.
			for (int i = _scopeTemps.Count - 1; i >= 0; i--) body.Insert(0, DeclLocal(ObjType, _scopeTemps[i], Null));
			// Emit fat-arrow local functions for this scope (C# hoists them, so position doesn't matter).
			body.AddRange(_pendingScopeFuncs);
			// Bind named nested functions just below the local hoists (so captured locals are definitely-assigned before
			// a closure is converted to a delegate — C# CS0165) but above the body (so forward-referenced calls work).
			body.InsertRange(setupEnd + _scopeTemps.Count, _pendingScopeClosureInits);

			_inDerefFunc = savedDeref;
			_locals = savedLocals; _forcedGlobals = savedForced; _statics = savedStatics; _byRefParams = savedByRef;
			_scopeClosureNames = savedScopeClosureNames;
			_scopeTemps = savedTemps; _pendingScopeFuncs = savedScopeFuncs; _enclosingLocals = savedEnclosing;
			_pendingScopeClosureInits = savedClosureInits; _enclosingStatics = savedEnclosingStatics;
			return SyntaxFactory.Block(body);
		}

		// object GetVar(object KS_name) { var KS_key = ForceString(KS_name).ToLowerInvariant();
		//   if (KS_key == "<local>") return <local>; ... return MainScript.ModuleData.Vars[KS_name]; }
		private StatementSyntax BuildGetVar()
		{
			var stmts = new List<StatementSyntax> { LocalDeclVar("KS_key", LowerKey()) };
			foreach (var n in _locals) stmts.Add(SyntaxFactory.IfStatement(Eq(Id("KS_key"), Str(n)), SyntaxFactory.ReturnStatement(Id(NameMangler.Escape(n)))));
			foreach (var kv in _statics) stmts.Add(SyntaxFactory.IfStatement(Eq(Id("KS_key"), Str(kv.Key)), SyntaxFactory.ReturnStatement(Id(kv.Value))));
			// Enclosing-scope locals (captured C# locals) and statics (module-level SL_ fields) — after this scope's own,
			// so a nested closure's `%name%` reaches the enclosing function's variables, not just globals.
			foreach (var n in _enclosingLocals) if (!_locals.Contains(n)) stmts.Add(SyntaxFactory.IfStatement(Eq(Id("KS_key"), Str(n)), SyntaxFactory.ReturnStatement(Id(NameMangler.Escape(n)))));
			foreach (var kv in _enclosingStatics) if (!_statics.ContainsKey(kv.Key)) stmts.Add(SyntaxFactory.IfStatement(Eq(Id("KS_key"), Str(kv.Key)), SyntaxFactory.ReturnStatement(Id(kv.Value))));
			stmts.Add(SyntaxFactory.ReturnStatement(VarsIndex(Id("KS_name"))));
			return LocalFunc("GetVar", SyntaxFactory.Block(stmts), ("KS_name", ObjType));
		}

		// MainScript.ModuleData.Vars[name] — the global dynamic-variable store. Read and write go through the same
		// C# indexer so a deref write (`%n% := v`, `y%x%++`) and a later read see the same value (the field slot).
		private static ExpressionSyntax VarsIndex(ExpressionSyntax name) =>
			SyntaxFactory.ElementAccessExpression(Access("MainScript.ModuleData.Vars"))
				.WithArgumentList(SyntaxFactory.BracketedArgumentList(SyntaxFactory.SingletonSeparatedList(Arg(name))));

		// A bare assignment (not parenthesized) so it's valid both as a statement-expression / lambda body and
		// as a sub-expression (e.g. inside MultiStatement(...) or `z := y%x%++`).
		private static ExpressionSyntax VarsWrite(ExpressionSyntax name, ExpressionSyntax value) => Assign(VarsIndex(name), value);

		// object SetVar(object KS_name, object KS_val) { var KS_key = ...;
		//   if (KS_key == "<local>") { <local> = KS_val; return KS_val; } ... return SetObject(Vars, KS_name, KS_val); }
		private StatementSyntax BuildSetVar()
		{
			var stmts = new List<StatementSyntax> { LocalDeclVar("KS_key", LowerKey()) };
			foreach (var n in _locals) stmts.Add(SetVarArm(Str(n), Id(NameMangler.Escape(n))));
			foreach (var kv in _statics) stmts.Add(SetVarArm(Str(kv.Key), Id(kv.Value)));
			foreach (var n in _enclosingLocals) if (!_locals.Contains(n)) stmts.Add(SetVarArm(Str(n), Id(NameMangler.Escape(n))));
			foreach (var kv in _enclosingStatics) if (!_statics.ContainsKey(kv.Key)) stmts.Add(SetVarArm(Str(kv.Key), Id(kv.Value)));
			stmts.Add(SyntaxFactory.ReturnStatement(VarsWrite(Id("KS_name"), Id("KS_val"))));
			return LocalFunc("SetVar", SyntaxFactory.Block(stmts), ("KS_name", ObjType), ("KS_val", ObjType));
		}

		private static ExpressionSyntax LowerKey() => Inv(Member(Op("ForceString", Id("KS_name")), "ToLowerInvariant"));
		private static StatementSyntax SetVarArm(ExpressionSyntax key, ExpressionSyntax target) =>
			SyntaxFactory.IfStatement(Eq(Id("KS_key"), key), SyntaxFactory.Block(ExprStmt(Assign(target, Id("KS_val"))), SyntaxFactory.ReturnStatement(Id("KS_val"))));
		private static ExpressionSyntax Eq(ExpressionSyntax a, ExpressionSyntax b) => SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, a, b);

		private static StatementSyntax LocalFunc(string name, BlockSyntax body, params (string name, TypeSyntax type)[] ps) =>
			SyntaxFactory.LocalFunctionStatement(ObjType, SyntaxFactory.Identifier(name))
				.WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
					ps.Select(p => SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.name)).WithType(p.type)))))
				.WithBody(body);

		private static bool HasDerefStmt(Stmt s) => s switch
		{
			ExpressionStmt es => HasDerefExpr(es.Expr),
			ReturnStmt r => r.Value != null && HasDerefExpr(r.Value),
			Block b => b.Body.Any(HasDerefStmt),
			IfStmt iff => HasDerefExpr(iff.Cond) || HasDerefStmt(iff.Then) || (iff.Else != null && HasDerefStmt(iff.Else)),
			WhileStmt w => HasDerefExpr(w.Cond) || HasDerefStmt(w.Body),
			LoopStmt lp => (lp.Count != null && HasDerefExpr(lp.Count)) || HasDerefStmt(lp.Body),
			ForStmt fr => HasDerefExpr(fr.Enumerable) || HasDerefStmt(fr.Body),
			SwitchStmt sw => HasDerefExpr(sw.Value) || sw.Cases.Any(c => c.Values.Any(HasDerefExpr) || c.Body.Any(HasDerefStmt)) || (sw.Default != null && sw.Default.Any(HasDerefStmt)),
			TryStmt tr => HasDerefStmt(tr.Body) || tr.Catches.Any(cb => HasDerefStmt(cb.Body)) || (tr.Else != null && HasDerefStmt(tr.Else)) || (tr.Finally != null && HasDerefStmt(tr.Finally)),
			ThrowStmt th => th.Value != null && HasDerefExpr(th.Value),
			DeclStmt d => d.Items.Any(HasDerefExpr),
			_ => false
		};

		private static bool HasDerefExpr(Expr e) => e switch
		{
			DerefExpr => true,
			BinaryExpr b => HasDerefExpr(b.Left) || HasDerefExpr(b.Right),
			UnaryExpr u => HasDerefExpr(u.Operand),
			AssignExpr a => HasDerefExpr(a.Target) || HasDerefExpr(a.Value),
			TernaryExpr t => HasDerefExpr(t.Cond) || HasDerefExpr(t.Then) || HasDerefExpr(t.Else),
			GroupExpr g => HasDerefExpr(g.Inner),
			SequenceExpr seq => seq.Items.Any(HasDerefExpr),
			CallExpr c => HasDerefExpr(c.Callee) || c.Args.Any(ar => ar.Value != null && HasDerefExpr(ar.Value)),
			MemberExpr m => HasDerefExpr(m.Target),
			DynMemberExpr dm => HasDerefExpr(dm.Target) || HasDerefExpr(dm.NameExpr),
			IndexExpr ix => HasDerefExpr(ix.Target) || ix.Args.Any(ar => ar.Value != null && HasDerefExpr(ar.Value)),
			ArrayExpr ar => ar.Elements.Any(el => el.Value != null && HasDerefExpr(el.Value)),
			ObjectExpr o => o.Entries.Any(en => HasDerefExpr(en.Key) || HasDerefExpr(en.Value)),
			_ => false
		};

		private void PrescanDecls(Stmt s, HashSet<string> fg, HashSet<string> el, Dictionary<string, string> st, string funcName)
		{
			switch (s)
			{
				case DeclStmt d:
					foreach (var item in d.Items)
					{
						var name = DeclItemName(item);
						if (name == null) continue;
						var lower = name.ToLowerInvariant();
						switch (d.Keyword)
						{
							case "global": fg.Add(lower); break;
							case "local": el.Add(lower); break;
							case "static":
								if (!st.ContainsKey(lower))
								{
									var field = NameMangler.StaticLocalField(funcName, lower);
									st[lower] = field;
									_staticFieldDeclIdx[field] = _fieldDecls.Count;
									_fieldDecls.Add(ObjField(field, Null));
								}
								break;
						}
					}
					break;
				case Block b: foreach (var x in b.Body) PrescanDecls(x, fg, el, st, funcName); break;
				case IfStmt iff: PrescanDecls(iff.Then, fg, el, st, funcName); if (iff.Else != null) PrescanDecls(iff.Else, fg, el, st, funcName); break;
				case WhileStmt w: PrescanDecls(w.Body, fg, el, st, funcName); if (w.Else != null) PrescanDecls(w.Else, fg, el, st, funcName); break;
				case LoopStmt lp: PrescanDecls(lp.Body, fg, el, st, funcName); if (lp.Else != null) PrescanDecls(lp.Else, fg, el, st, funcName); break;
				case SpecialLoopStmt slp: PrescanDecls(slp.Body, fg, el, st, funcName); if (slp.Else != null) PrescanDecls(slp.Else, fg, el, st, funcName); break;
				case ForStmt fr: PrescanDecls(fr.Body, fg, el, st, funcName); if (fr.Else != null) PrescanDecls(fr.Else, fg, el, st, funcName); break;
				case SwitchStmt sw:
					foreach (var c in sw.Cases) foreach (var s2 in c.Body) PrescanDecls(s2, fg, el, st, funcName);
					if (sw.Default != null) foreach (var s2 in sw.Default) PrescanDecls(s2, fg, el, st, funcName);
					break;
				case TryStmt tr:
					PrescanDecls(tr.Body, fg, el, st, funcName);
					foreach (var cb in tr.Catches) PrescanDecls(cb.Body, fg, el, st, funcName);
					if (tr.Else != null) PrescanDecls(tr.Else, fg, el, st, funcName);
					if (tr.Finally != null) PrescanDecls(tr.Finally, fg, el, st, funcName);
					break;
			}
		}

		// ---- collect assigned (local) names ----

		private static void CollectAssignedStmt(Stmt s, List<string> acc, HashSet<string> seen)
		{
			switch (s)
			{
				case ExpressionStmt es: CollectAssignedExpr(es.Expr, acc, seen); break;
				// A declaration's initializers may contain nested assignments / `&`-refs (`local a := (b := 5)`); those
				// targets are function-locals too. The declared names themselves are registered by PrescanDecls.
				case DeclStmt d: foreach (var item in d.Items) CollectAssignedExpr(item, acc, seen); break;
				case ReturnStmt r: if (r.Value != null) CollectAssignedExpr(r.Value, acc, seen); break;
				case Block b: foreach (var x in b.Body) CollectAssignedStmt(x, acc, seen); break;
				case IfStmt iff:
					CollectAssignedExpr(iff.Cond, acc, seen);
					CollectAssignedStmt(iff.Then, acc, seen);
					if (iff.Else != null) CollectAssignedStmt(iff.Else, acc, seen);
					break;
				case WhileStmt w: CollectAssignedExpr(w.Cond, acc, seen); CollectAssignedStmt(w.Body, acc, seen); if (w.Else != null) CollectAssignedStmt(w.Else, acc, seen); break;
				case LoopStmt lp: if (lp.Count != null) CollectAssignedExpr(lp.Count, acc, seen); CollectAssignedStmt(lp.Body, acc, seen); if (lp.Else != null) CollectAssignedStmt(lp.Else, acc, seen); break;
				case SpecialLoopStmt slp:
					if (slp.Args != null) foreach (var a in slp.Args) if (a != null) CollectAssignedExpr(a, acc, seen);
					CollectAssignedStmt(slp.Body, acc, seen); if (slp.Else != null) CollectAssignedStmt(slp.Else, acc, seen);
					break;
				case ForStmt fr:
					foreach (var v in fr.Vars) { if (v == null) continue; var lo = v.ToLowerInvariant(); if (seen.Add(lo)) acc.Add(lo); }
					CollectAssignedExpr(fr.Enumerable, acc, seen); CollectAssignedStmt(fr.Body, acc, seen); if (fr.Else != null) CollectAssignedStmt(fr.Else, acc, seen);
					break;
				case SwitchStmt sw:
					CollectAssignedExpr(sw.Value, acc, seen);
					foreach (var c in sw.Cases) { foreach (var v in c.Values) CollectAssignedExpr(v, acc, seen); foreach (var st in c.Body) CollectAssignedStmt(st, acc, seen); }
					if (sw.Default != null) foreach (var st in sw.Default) CollectAssignedStmt(st, acc, seen);
					break;
				case TryStmt tr:
					CollectAssignedStmt(tr.Body, acc, seen);
					foreach (var cb in tr.Catches)
					{
						if (cb.Var != null) { var lo = cb.Var.ToLowerInvariant(); if (seen.Add(lo)) acc.Add(lo); }
						CollectAssignedStmt(cb.Body, acc, seen);
					}
					if (tr.Else != null) CollectAssignedStmt(tr.Else, acc, seen);
					if (tr.Finally != null) CollectAssignedStmt(tr.Finally, acc, seen);
					break;
				case ThrowStmt th: if (th.Value != null) CollectAssignedExpr(th.Value, acc, seen); break;
			}
		}

		private static void CollectAssignedExpr(Expr e, List<string> acc, HashSet<string> seen)
		{
			switch (e)
			{
				case AssignExpr a:
					// A builtin var (A_Clipboard, A_SendLevel, …) is never a local even when assigned inside a function —
					// it resolves to its accessor (whose setter validates/throws), so don't shadow it with a local slot.
					if (a.Target is NameExpr n && !Script.TheScript.ReflectionsData.flatPublicStaticProperties.ContainsKey(n.Name.ToLowerInvariant()))
					{ var lo = n.Name.ToLowerInvariant(); if (seen.Add(lo)) acc.Add(lo); }
					CollectAssignedExpr(a.Target, acc, seen);   // a nested `x:=` inside a member/index target (e.g. obj[x:=v]:=w)
					CollectAssignedExpr(a.Value, acc, seen);
					break;
				case BinaryExpr b: CollectAssignedExpr(b.Left, acc, seen); CollectAssignedExpr(b.Right, acc, seen); break;
				case UnaryExpr u:
					// `&var` (a reference, typically an output param like `SplitPath(p,,,, &name)`) makes that variable a
					// local — it may be written through the ref. (`&obj.prop` is a PropRef, not a local; handled by recursion.)
					if (u.Op == "&" && u.Operand is NameExpr rn && !Script.TheScript.ReflectionsData.flatPublicStaticProperties.ContainsKey(rn.Name.ToLowerInvariant()))
					{ var lo = rn.Name.ToLowerInvariant(); if (seen.Add(lo)) acc.Add(lo); }
					CollectAssignedExpr(u.Operand, acc, seen);
					break;
				case TernaryExpr t: CollectAssignedExpr(t.Cond, acc, seen); CollectAssignedExpr(t.Then, acc, seen); CollectAssignedExpr(t.Else, acc, seen); break;
				case GroupExpr g: CollectAssignedExpr(g.Inner, acc, seen); break;
				case SequenceExpr seq: foreach (var it in seq.Items) CollectAssignedExpr(it, acc, seen); break;
				case DerefExpr dr: CollectAssignedExpr(dr.Name, acc, seen); break;
				case MemberExpr m: CollectAssignedExpr(m.Target, acc, seen); break;
				case DynMemberExpr dm: CollectAssignedExpr(dm.Target, acc, seen); CollectAssignedExpr(dm.NameExpr, acc, seen); break;
				case IndexExpr ix: CollectAssignedExpr(ix.Target, acc, seen); foreach (var ar in ix.Args) if (ar.Value != null) CollectAssignedExpr(ar.Value, acc, seen); break;
				case ObjectExpr oe: foreach (var en in oe.Entries) { CollectAssignedExpr(en.Key, acc, seen); CollectAssignedExpr(en.Value, acc, seen); } break;
				case CallExpr c: CollectAssignedExpr(c.Callee, acc, seen); foreach (var ar in c.Args) if (ar.Value != null) CollectAssignedExpr(ar.Value, acc, seen); break;
			}
		}

		// ---- scaffold ----

		private CompilationUnitSyntax BuildUnit(string name, List<MemberDeclarationSyntax> members, List<StatementSyntax> autoStmts)
		{
			var moduleClass = BuildModuleClass("__Main", _fieldDecls, members, _pendingLambdas, _hotMembers, autoStmts, null, _moduleCompat);
			return AssembleProgram(name, new[] { (MemberDeclarationSyntax)moduleClass }, new[] { "__Main" });
		}

		// Builds a `className : Keysharp.Runtime.Module` class: import bindings, fields, member functions, lambdas,
		// hotkey callbacks, then its AutoExecSection and constructor.
		private MemberDeclarationSyntax BuildModuleClass(string moduleName, IEnumerable<MemberDeclarationSyntax> fieldDecls,
			IEnumerable<MemberDeclarationSyntax> members, IEnumerable<MemberDeclarationSyntax> lambdas,
			IEnumerable<MemberDeclarationSyntax> hotMembers, List<StatementSyntax> autoStmts, IEnumerable<MemberDeclarationSyntax> importMembers,
			Semver.SemVersion compat = null)
		{
			// The C# class name is the module name, disambiguated if it shadows a framework/structural root; the runtime
			// resolves the original AHK module name back through [UserDeclaredName] (Reflections keys stringToTypes by it).
			var csName = NameMangler.ModuleClass(moduleName);
			var autoBody = new List<StatementSyntax>(autoStmts) { SyntaxFactory.ReturnStatement(Str("")) };
			var mm = new List<MemberDeclarationSyntax>();
			if (importMembers != null) mm.AddRange(importMembers);
			mm.AddRange(fieldDecls);
			mm.AddRange(members);
			if (lambdas != null) mm.AddRange(lambdas);
			if (hotMembers != null) mm.AddRange(hotMembers);
			mm.Add(ObjMethod("AutoExecSection", EmptyParams(), SyntaxFactory.Block(autoBody)));
			mm.Add(ClassCtor(csName));
			var decl = SyntaxFactory.ClassDeclaration(csName)
				.AddModifiers(PublicTok).WithBaseList(BaseList("Keysharp.Runtime.Module"))
				.AddAttributeLists(Attr("Keysharp.Runtime.CompatibilityMode", Str((compat ?? Keysharp.Runtime.Script.DefaultCompatibilityVersion).ToString())))
				.WithMembers(SyntaxFactory.List(mm));
			if (csName != moduleName) decl = decl.AddAttributeLists(Attr("Keysharp.Runtime.UserDeclaredName", Str(moduleName)));
			return decl;
		}

		// Wraps the module classes in the Program class (with Main, MainScript and the outer AutoExecSection that drives
		// DHHR + per-module auto-exec in execution order) and the compilation unit (+ #Assembly* attributes).
		private CompilationUnitSyntax AssembleProgram(string name, IEnumerable<MemberDeclarationSyntax> moduleClasses, IReadOnlyList<string> execOrder)
		{
			var mainScriptField = SyntaxFactory.FieldDeclaration(
				SyntaxFactory.VariableDeclaration(Ty("Keysharp.Runtime.Script")).AddVariables(
					SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier("MainScript")).WithInitializer(SyntaxFactory.EqualsValueClause(
						SyntaxFactory.ObjectCreationExpression(Ty("Keysharp.Runtime.Script"))
							.WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
								_hookMutexName == null
									? new[] { Arg(SyntaxFactory.TypeOfExpression(Id("Program"))) }
									: new[] { Arg(SyntaxFactory.TypeOfExpression(Id("Program"))), Arg(Str(_hookMutexName)) })))))))
				.AddModifiers(PrivateTok, StaticTok);

			var programMembers = new List<MemberDeclarationSyntax> { BuildMain(name), mainScriptField };
			programMembers.AddRange(moduleClasses);
			programMembers.Add(BuildOuterAuto(execOrder));

			var programClass = SyntaxFactory.ClassDeclaration("Program").AddModifiers(PublicTok).WithMembers(SyntaxFactory.List(programMembers));
			var ns = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.QualifiedName(Id("Keysharp"), Id("CompiledMain"))).AddMembers(programClass);
			// No using directives: generated code uses clean, fully-qualified framework names (Keysharp.Runtime.Script.*,
			// System.*). A user class/module that would shadow such a root is given a non-colliding C# type name by the
			// mangler (NameMangler.ClassType / ModuleClass) + [UserDeclaredName], so no aliases/global:: are ever needed.
			var unit = SyntaxFactory.CompilationUnit().AddMembers(ns);
			// #Assembly* directives -> `[assembly: System.Reflection.Assembly*Attribute("value")]` on the compiled unit.
			foreach (var (attr, value) in _asmAttributes)
				unit = unit.AddAttributeLists(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
						SyntaxFactory.Attribute(QName(attr)).WithArgumentList(SyntaxFactory.AttributeArgumentList(
							SyntaxFactory.SingletonSeparatedList(SyntaxFactory.AttributeArgument(Str(value)))))))
					.WithTarget(SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.AssemblyKeyword))));
			return unit;
		}

		private MemberDeclarationSyntax BuildMain(string name)
		{
			// SetName(<full path or "*">[, <startup name>]); the 2nd arg only when a distinct name was given (matches the
			// canonical), so A_ScriptName is the file name for a file and the given name for a from-string compile.
			var setNameArgs = new List<ExpressionSyntax> { Str(_scriptPath) };
			var fileName = _scriptPath == "*" ? null : System.IO.Path.GetFileName(_scriptPath);
			// For a real file, derive A_ScriptName from the path (don't override with the build name); for a from-string
			// compile, the explicit startup name (or the build name) becomes A_ScriptName.
			var givenName = _startupName ?? (_scriptPath == "*" ? name : null);
			if (givenName != null && givenName != fileName) setNameArgs.Add(Str(givenName));
			var tryStmts = new List<StatementSyntax>
			{
				ExprStmt(Inv(Member(Id("MainScript"), "SetName"), setNameArgs.ToArray())),
			};
			// #SingleInstance: bail out (return 0) when another instance already handles this one — placed right after
			// SetName so A_ScriptName is set, before any window/auto-exec runs (matches the canonical Main).
			if (_singleInstanceMode != null)
				tryStmts.Add(SyntaxFactory.IfStatement(
					Inv(Access("Keysharp.Runtime.Script.HandleSingleInstance"),
						Access("Keysharp.Builtins.Accessors.A_ScriptName"), Access("Keysharp.Runtime.eScriptInstance." + _singleInstanceMode)),
					SyntaxFactory.ReturnStatement(IntLit(0))));
			tryStmts.Add(CallStmt("Keysharp.Builtins.Env.HandleCommandLineParams", Id("args")));
			tryStmts.Add(ExprStmt(Inv(Member(Id("MainScript"), "RunMainWindow"), Access("Keysharp.Builtins.Accessors.A_ScriptName"), Id("AutoExecSection"), False)));
			var catchStmts = new List<StatementSyntax>
			{
				LocalDeclVar("ex", Inv(Access("Keysharp.Runtime.Flow.UnwrapException"), Id("mainex"))),
				SyntaxFactory.IfStatement(
					SyntaxFactory.BinaryExpression(SyntaxKind.IsExpression, Id("ex"), Ty("Keysharp.Builtins.Flow.UserRequestedExitException")),
					SyntaxFactory.ReturnStatement(Access("System.Environment.ExitCode"))),
				SyntaxFactory.IfStatement(
					SyntaxFactory.IsPatternExpression(Id("ex"), SyntaxFactory.DeclarationPattern(Ty("Keysharp.Builtins.KeysharpException"),
						SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("kserr")))),
					ExprStmt(Op("TryProcessKeysharpException", Id("MainScript"), Id("kserr"))),
					SyntaxFactory.ElseClause(ExprStmt(Op("TryProcessUnhandledException", Id("MainScript"), Id("ex"))))),
				ExprStmt(Op("SafeExit", IntLit(1))),
			};
			var catchClause = SyntaxFactory.CatchClause()
				.WithDeclaration(SyntaxFactory.CatchDeclaration(Ty("System.Exception"), SyntaxFactory.Identifier("mainex")))
				.WithBlock(SyntaxFactory.Block(catchStmts));
			var body = SyntaxFactory.Block(
				SyntaxFactory.TryStatement().WithBlock(SyntaxFactory.Block(tryStmts)).WithCatches(SyntaxFactory.SingletonList(catchClause)),
				SyntaxFactory.ReturnStatement(Access("System.Environment.ExitCode")));

			return SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)), "Main")
				.AddModifiers(PublicTok, StaticTok)
				.AddAttributeLists(Attr("System.STAThread"))
				.WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
					SyntaxFactory.Parameter(SyntaxFactory.Identifier("args")).WithType(
						ArrayOf(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)))))))
				.WithBody(body);
		}

		private MemberDeclarationSyntax BuildOuterAuto(IReadOnlyList<string> execOrder)
		{
			var stmts = new List<StatementSyntax>();
			// #Warn output runs first (at load time, before any script logic), per the configured mode.
			stmts.AddRange(EmitWarnings());
			// DHHR: hotkey/hotstring/remap registration runs before the manifest, then Persistent() if any were defined.
			stmts.AddRange(_dhhr);
			if (_persistent) stmts.Add(CallStmt("Keysharp.Builtins.Flow.Persistent"));
			stmts.Add(CallStmt("Keysharp.Runtime.Keyboard.HotkeyDefinition.ManifestAllHotkeysHotstringsHooks"));
			// Run each module's auto-exec in dependency order, scoping CurrentModuleType to that module.
			foreach (var mod in execOrder)
			{
				var modClass = NameMangler.ModuleClass(mod);
				stmts.Add(ExprStmt(Assign(Member(Id("MainScript"), "CurrentModuleType"), SyntaxFactory.TypeOfExpression(Ty("Program." + modClass)))));
				stmts.Add(CallStmt("Program." + modClass + ".AutoExecSection"));
			}
			stmts.Add(ExprStmt(Assign(Member(Id("MainScript"), "CurrentModuleType"), Null)));
			stmts.Add(SyntaxFactory.ReturnStatement(Str("")));
			return ObjMethod("AutoExecSection", EmptyParams(), SyntaxFactory.Block(stmts));
		}

		// ---- SyntaxFactory helpers ----

		private static readonly SyntaxToken PublicTok = SyntaxFactory.Token(SyntaxKind.PublicKeyword);
		private static readonly SyntaxToken StaticTok = SyntaxFactory.Token(SyntaxKind.StaticKeyword);
		private static readonly SyntaxToken PrivateTok = SyntaxFactory.Token(SyntaxKind.PrivateKeyword);
		private static readonly TypeSyntax ObjType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
		private static ArrayTypeSyntax ArrayOf(TypeSyntax elem) => SyntaxFactory.ArrayType(elem,
			SyntaxFactory.SingletonList(SyntaxFactory.ArrayRankSpecifier(SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(SyntaxFactory.OmittedArraySizeExpression()))));
		private static readonly TypeSyntax ObjArrayType = ArrayOf(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)));
		private static ExpressionSyntax Null => SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
		private static ExpressionSyntax False => SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression);

		private static IdentifierNameSyntax Id(string n) => SyntaxFactory.IdentifierName(n);

		// Generated code uses clean, fully-qualified framework names. A user class/module that would shadow a framework
		// namespace (System/Keysharp) or structural root (Program/…) is instead given a non-colliding C# type name by
		// the mangler (NameMangler.ClassType / ModuleClass), so these helpers never need alias/global:: qualification.
		private static ExpressionSyntax Access(string dotted)
		{
			var parts = dotted.Split('.');
			ExpressionSyntax e = Id(parts[0]);
			for (var i = 1; i < parts.Length; i++)
				e = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, e, Id(parts[i]));
			return e;
		}

		private static NameSyntax QName(string dotted)
		{
			var parts = dotted.Split('.');
			NameSyntax n = Id(parts[0]);
			for (var i = 1; i < parts.Length; i++) n = SyntaxFactory.QualifiedName(n, Id(parts[i]));
			return n;
		}

		private static TypeSyntax Ty(string dotted) => QName(dotted);
		private static ExpressionSyntax Member(ExpressionSyntax e, string name) =>
			SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, e, Id(name));
		private static ArgumentSyntax Arg(ExpressionSyntax e) => SyntaxFactory.Argument(e);

		private static InvocationExpressionSyntax Inv(ExpressionSyntax callee, params ExpressionSyntax[] args) =>
			SyntaxFactory.InvocationExpression(callee, SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args.Select(Arg))));

		private static ExpressionSyntax Op(string method, params ExpressionSyntax[] args) =>
			Inv(Access("Keysharp.Runtime.Script." + method), args);

		private static ExpressionSyntax IfTest(ExpressionSyntax cond) => Op("IfTest", cond);
		private static ExpressionSyntax Assign(ExpressionSyntax target, ExpressionSyntax value) =>
			SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, target, value);

		private static StatementSyntax ExprStmt(ExpressionSyntax e) => SyntaxFactory.ExpressionStatement(e);
		private static StatementSyntax CallStmt(string dotted, params ExpressionSyntax[] args) => ExprStmt(Inv(Access(dotted), args));

		private static StatementSyntax TryFinally(StatementSyntax tryBody, StatementSyntax finallyStmt) =>
			SyntaxFactory.TryStatement().WithBlock(SyntaxFactory.Block(tryBody)).WithFinally(SyntaxFactory.FinallyClause(SyntaxFactory.Block(finallyStmt)));

		private static StatementSyntax LocalDecl(TypeSyntax type, string name, ExpressionSyntax init) =>
			SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(type).AddVariables(
				SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(name)).WithInitializer(SyntaxFactory.EqualsValueClause(init))));
		private static StatementSyntax LocalDeclVar(string name, ExpressionSyntax init) => LocalDecl(SyntaxFactory.IdentifierName("var"), name, init);
		private static StatementSyntax DeclLocal(TypeSyntax type, string name, ExpressionSyntax init) => LocalDecl(type, name, init);

		// Keysharp.Builtins.Functions.Func((System.Delegate)<methodPath>)
		private static ExpressionSyntax FuncBind(string methodPath) =>
			Inv(Access("Keysharp.Builtins.Functions.Func"), SyntaxFactory.CastExpression(Ty("System.Delegate"), Access(methodPath)));

		// Functions.Closure((System.Delegate)<localFn>) — wraps a capturing C# local function as a Closure-typed FuncObj
		// (the captures live in the delegate; the runtime type is Closure so `x is Closure` holds).
		private static ExpressionSyntax ClosureBind(string methodPath) =>
			Inv(Access("Keysharp.Builtins.Functions.Closure"), SyntaxFactory.CastExpression(Ty("System.Delegate"), Access(methodPath)));

		// MainScript.Vars.Statics[typeof(T)]
		private static ExpressionSyntax TypeSingleton(string typeFullName) =>
			SyntaxFactory.ElementAccessExpression(Access("MainScript.Vars.Statics"))
				.WithArgumentList(SyntaxFactory.BracketedArgumentList(SyntaxFactory.SingletonSeparatedList(Arg(SyntaxFactory.TypeOfExpression(Ty(typeFullName))))));

		// Keysharp.Builtins.Misc.MakeVarRef(() => getter, (KS_value) => setter)
		private static ExpressionSyntax MakeVarRefGS(ExpressionSyntax getter, ExpressionSyntax setter) =>
			Inv(Access("Keysharp.Builtins.Misc.MakeVarRef"),
				SyntaxFactory.ParenthesizedLambdaExpression().WithExpressionBody(getter),
				SyntaxFactory.ParenthesizedLambdaExpression()
					.WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Parameter(SyntaxFactory.Identifier("KS_value")))))
					.WithExpressionBody(setter));

		// A VarRef that ignores writes — used for omitted for-loop variables (`for (, v in arr)`).
		private static ExpressionSyntax DiscardVarRef() =>
			Inv(Access("Keysharp.Builtins.Misc.MakeVarRef"),
				SyntaxFactory.ParenthesizedLambdaExpression().WithExpressionBody(Str("")),
				SyntaxFactory.ParenthesizedLambdaExpression()
					.WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Parameter(SyntaxFactory.Identifier("KS_value")))))
					.WithBlock(SyntaxFactory.Block()));

		// &lvalue : a VarRef whose getter reads and whose setter writes the lvalue (variable, member or index).
		private ExpressionSyntax MakeRefFor(Expr lvalue)
		{
			switch (lvalue)
			{
				case NameExpr n:
					var lown = n.Name.ToLowerInvariant();
					if (_byRefParams != null && _byRefParams.Contains(lown))   // &param: read/write the underlying VarRef's __Value
					{
						var refId = Id(NameMangler.Escape(lown));
						return MakeVarRefGS(Op("GetPropertyValue", refId, Str("__Value")),
							Op("SetPropertyValue", refId, Str("__Value"), Id("KS_value")));
					}
					var nr = NameRef(n.Name);
					return MakeVarRefGS(nr, Assign(nr, Id("KS_value")));
				// `&obj.prop` / `&obj[i]` produce a v2.1 PropRef bound to the property slot, via obj.__Ref(name[, args]).
				case MemberExpr me:
					return Op("Invoke", LowerExpr(me.Target), Str("__Ref"), Str(me.Name));
				case DynMemberExpr dme:
					return Op("Invoke", LowerExpr(dme.Target), Str("__Ref"), LowerExpr(dme.NameExpr));
				// `&obj.prop[i,j]` binds to the parameterized PROPERTY slot (obj.__Ref("prop", i, j)), re-reading the
				// property each access — not to whatever array `obj.prop` currently resolves to.
				case IndexExpr ie when ie.Target is MemberExpr ipm:
					var pmArgs = new List<ExpressionSyntax> { LowerExpr(ipm.Target), Str("__Ref"), Str(ipm.Name) };
					pmArgs.AddRange(LowerArgs(ie.Args));
					return Op("Invoke", pmArgs.ToArray());
				case IndexExpr ie when ie.Target is DynMemberExpr ipd:
					var pdArgs = new List<ExpressionSyntax> { LowerExpr(ipd.Target), Str("__Ref"), LowerExpr(ipd.NameExpr) };
					pdArgs.AddRange(LowerArgs(ie.Args));
					return Op("Invoke", pdArgs.ToArray());
				case IndexExpr ie:   // &obj[i] (obj a plain value/var) -> obj.__Ref("__Item", i)
					var refArgs = new List<ExpressionSyntax> { LowerExpr(ie.Target), Str("__Ref"), Str("__Item") };
					refArgs.AddRange(LowerArgs(ie.Args));
					return Op("Invoke", refArgs.ToArray());
				case DerefExpr dr:   // &%name% : ref to a dynamically-named variable, through GetVar/SetVar or ModuleData.Vars
					if (_inDerefFunc)
						return MakeVarRefGS(Inv(Id("GetVar"), LowerExpr(dr.Name)), Inv(Id("SetVar"), LowerExpr(dr.Name), Id("KS_value")));
					return MakeVarRefGS(VarsIndex(LowerExpr(dr.Name)), VarsWrite(LowerExpr(dr.Name), Id("KS_value")));
				case GroupExpr g:
					return MakeRefFor(g.Inner);
				case AssignExpr a:   // &(x := v): perform the assignment, then yield a ref to its target.
					return Op("MultiStatement", LowerAssign(a), MakeRefFor(a.Target));
				default:
					Diag($"'&' on {lvalue.GetType().Name} is not yet lowerable"); return Str("");
			}
		}

		// Keysharp.Runtime.Script.InitStaticVariable(ref <field>, "__Main_<field>", () => <init>)
		private static ExpressionSyntax InitStatic(string field, ExpressionSyntax init) =>
			SyntaxFactory.InvocationExpression(Access("Keysharp.Runtime.Script.InitStaticVariable"),
				SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
				{
					SyntaxFactory.Argument(Id(field)).WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
					Arg(Str("__Main_" + field)),
					Arg(SyntaxFactory.ParenthesizedLambdaExpression().WithExpressionBody(init)),
				})));

		private static FieldDeclarationSyntax ObjField(string name, ExpressionSyntax init) =>
			SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(ObjType).AddVariables(
				SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(name)).WithInitializer(SyntaxFactory.EqualsValueClause(init))))
			.AddModifiers(PublicTok, StaticTok);

		private static PropertyDeclarationSyntax ObjArrowProp(string name, ExpressionSyntax expr) =>
			SyntaxFactory.PropertyDeclaration(ObjType, SyntaxFactory.Identifier(name)).AddModifiers(PublicTok, StaticTok)
				.WithExpressionBody(SyntaxFactory.ArrowExpressionClause(expr)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

		private static MethodDeclarationSyntax ObjMethod(string name, ParameterListSyntax pl, BlockSyntax body, params AttributeListSyntax[] attrs)
		{
			var m = SyntaxFactory.MethodDeclaration(ObjType, SyntaxFactory.Identifier(name)).AddModifiers(PublicTok, StaticTok).WithParameterList(pl).WithBody(body);
			return attrs.Length > 0 ? m.AddAttributeLists(attrs) : m;
		}

		private static AttributeListSyntax Attr(string dottedName, params ExpressionSyntax[] args)
		{
			var attr = SyntaxFactory.Attribute(QName(dottedName));
			if (args.Length > 0)
				attr = attr.WithArgumentList(SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(args.Select(a => SyntaxFactory.AttributeArgument(a)))));
			return SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attr));
		}

		private static BaseListSyntax BaseList(string typeName) =>
			SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(SyntaxFactory.SimpleBaseType(Ty(typeName))));

		private static ParameterSyntax ThisParam() => SyntaxFactory.Parameter(SyntaxFactory.Identifier("@this")).WithType(ObjType);
		private static ParameterListSyntax ParamThis() => SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(ThisParam()));
		private static ParameterListSyntax EmptyParams() => SyntaxFactory.ParameterList();

		private static MemberDeclarationSyntax ClassCtor(string className) =>
			SyntaxFactory.ConstructorDeclaration(className).AddModifiers(PublicTok)
				.WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
					SyntaxFactory.Parameter(SyntaxFactory.Identifier("args")).WithType(ObjArrayType).AddModifiers(SyntaxFactory.Token(SyntaxKind.ParamsKeyword)))))
				.WithInitializer(SyntaxFactory.ConstructorInitializer(SyntaxKind.BaseConstructorInitializer,
					SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(Arg(Null)))))
				.WithBody(SyntaxFactory.Block());

		private static ExpressionSyntax IntLit(int n) => SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(n));
		private static ExpressionSyntax Str(string value) => SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value));

		private static ExpressionSyntax NumLit(long v) => SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(v));
		private static ExpressionSyntax NumLit(double v) => SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(v));

		// True if the lowered expression is a numeric (or true/false) C# literal, returning its value.
		private static bool TryGetNumLit(ExpressionSyntax e, out bool isDouble, out double dbl, out long lng)
		{
			isDouble = false; dbl = 0; lng = 0;
			if (e is not LiteralExpressionSyntax lit) return false;
			if (lit.IsKind(SyntaxKind.TrueLiteralExpression)) { lng = 1; return true; }
			if (lit.IsKind(SyntaxKind.FalseLiteralExpression)) { lng = 0; return true; }
			if (!lit.IsKind(SyntaxKind.NumericLiteralExpression)) return false;
			switch (lit.Token.Value) { case long l: lng = l; return true; case double d: isDouble = true; dbl = d; return true; }
			return false;
		}

		// True if the lowered expression is a literal that can be concatenated (string/number/true/false/null).
		private static bool TryGetConcatLit(ExpressionSyntax e, out object value)
		{
			value = null;
			if (e is not LiteralExpressionSyntax lit) return false;
			if (lit.IsKind(SyntaxKind.StringLiteralExpression)) { value = lit.Token.Value as string ?? ""; return true; }
			if (lit.IsKind(SyntaxKind.NumericLiteralExpression)) { value = lit.Token.Value; return true; }
			if (lit.IsKind(SyntaxKind.TrueLiteralExpression)) { value = true; return true; }
			if (lit.IsKind(SyntaxKind.FalseLiteralExpression)) { value = false; return true; }
			if (lit.IsKind(SyntaxKind.NullLiteralExpression)) { value = null; return true; }
			return false;
		}

		// Constant-folds a binary op over already-lowered LITERAL operands (matches the canonical TryFoldBinaryExpression):
		// `1 + 2` -> `3L`, `1 . "a"` -> `"1a"`. Returns null when not foldable (non-literal operand, div-by-zero, etc.).
		private static ExpressionSyntax TryFoldBinary(string op, ExpressionSyntax le, ExpressionSyntax re)
		{
			if (op == ".")
			{
				if (TryGetConcatLit(le, out var lv) && TryGetConcatLit(re, out var rv) && rv != null)   // Concat() raises on null right
					return Str(Keysharp.Runtime.Script.ForceString(lv) + Keysharp.Runtime.Script.ForceString(rv));
				return null;
			}
			if (!TryGetNumLit(le, out var lD, out var ld, out var ll) || !TryGetNumLit(re, out var rD, out var rd, out var rl))
				return null;
			bool useD = lD || rD;
			double L() => lD ? ld : ll;
			double R() => rD ? rd : rl;
			switch (op)
			{
				case "+": return useD ? NumLit(L() + R()) : NumLit(ll + rl);
				case "-": return useD ? NumLit(L() - R()) : NumLit(ll - rl);
				case "*": return useD ? NumLit(L() * R()) : NumLit(ll * rl);
				case "/": return R() == 0 ? null : NumLit(L() / R());
				case "//": return useD || rl == 0 ? null : NumLit(ll / rl);
				case "**": return useD ? NumLit(System.Math.Pow(L(), R())) : NumLit((long)System.Math.Pow(ll, rl));
				case "<<": return useD || rl < 0 || rl > 63 ? null : NumLit(ll << (int)rl);
				case ">>": return useD || rl < 0 || rl > 63 ? null : NumLit(ll >> (int)rl);
				case ">>>": return useD || rl < 0 || rl > 63 ? null : NumLit((long)((ulong)ll >> (int)rl));
				case "&": return useD ? null : NumLit(ll & rl);
				case "|": return useD ? null : NumLit(ll | rl);
				case "^": return useD ? null : NumLit(ll ^ rl);
				default: return null;
			}
		}

		private static ExpressionSyntax Num(string raw)
		{
			var t = raw.Replace("_", "");
			if (t.EndsWith("n") || t.EndsWith("N")) t = t[..^1];
			if (TryParseAhkInt(t, out var v)) return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(v));
			if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
				return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(d));
			return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0L));
		}

		private static bool TryParseAhkInt(string t, out long v)
		{
			v = 0;
			if (t.Length == 0) return false;
			try
			{
				if (t.StartsWith("0x") || t.StartsWith("0X")) { v = System.Convert.ToInt64(t[2..], 16); return true; }
				if (t.StartsWith("0b") || t.StartsWith("0B")) { v = System.Convert.ToInt64(t[2..], 2); return true; }
				if (t.StartsWith("0o") || t.StartsWith("0O")) { v = System.Convert.ToInt64(t[2..], 8); return true; }
				if (t.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0) return false;
				return long.TryParse(t, out v);
			}
			catch { return false; }
		}

		private static string DecodeString(string raw)
		{
			if (raw.Length < 2) return "";
			var inner = raw.Substring(1, raw.Length - 2);
			var sb = new System.Text.StringBuilder(inner.Length);
			for (var i = 0; i < inner.Length; i++)
			{
				var ch = inner[i];
				if (ch == '`' && i + 1 < inner.Length)
				{
					var n = inner[++i];
					sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f', 'v' => '\v', 's' => ' ', 'a' => '\a', _ => n });
				}
				else sb.Append(ch);
			}
			return sb.ToString();
		}

		private static ExpressionSyntax[] Cons(ExpressionSyntax head, List<ExpressionSyntax> tail)
		{
			var arr = new ExpressionSyntax[tail.Count + 1];
			arr[0] = head;
			tail.CopyTo(arr, 1);
			return arr;
		}

		private static ExpressionSyntax[] Cons2(ExpressionSyntax a, ExpressionSyntax b, List<ExpressionSyntax> tail)
		{
			var arr = new ExpressionSyntax[tail.Count + 2];
			arr[0] = a;
			arr[1] = b;
			tail.CopyTo(arr, 2);
			return arr;
		}

		private static ParameterSyntax[] Prepend(ParameterSyntax head, ParameterSyntax[] tail)
		{
			var arr = new ParameterSyntax[tail.Length + 1];
			arr[0] = head;
			tail.CopyTo(arr, 1);
			return arr;
		}

		private static HashSet<string> ByRefSet(List<Param> ps)
		{
			HashSet<string> set = null;
			foreach (var p in ps) if (p.ByRef) (set ??= new()).Add(p.Name.ToLowerInvariant());
			return set;
		}

		private void Diag(string msg) => Diagnostics.Add(msg);
	}
}
