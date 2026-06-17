# Keysharp — Claude Code Guide

## Project overview

Keysharp is a cross-platform C# implementation of AutoHotkey v2. AHK scripts (`.ahk`/`.ks`) are parsed by a hand-written lexer + recursive-descent parser into an AST, lowered to C# (Roslyn `SyntaxNode`s), compiled in-memory with Roslyn, and executed as a .NET assembly. The goal is full AHK v2 compatibility on Windows, with partial Linux and eventual macOS support.

## Solution structure

```
Keysharp.sln
├── Keysharp/               # Entry-point executable (thin launcher)
├── Keysharp.Core/          # All runtime, parsing, and built-in logic
│   ├── Builtins/           # AHK built-in functions (Keyboard, GUI, Files, COM, …)
│   ├── Internals/          # Platform services: hooks, threading, window, input
│   │   ├── Input/Hooks/    # HookThread (4 k lines) — OS keyboard/mouse callbacks
│   │   ├── Input/Keyboard/ # HotkeyDefinition, KeyboardMouseSender
│   │   └── Threading/      # Threads, ScriptTimerManager, SlimStack, ThreadVariables
│   ├── Parsing/            # Hand-written lexer/parser/lowerer + Roslyn compile
│   │   ├── Lexing/         # Lexer, Token, TokenKind
│   │   ├── Syntax/         # Parser, Ast, Lowerer (AST → C#), NameMangler
│   │   └── CompilerHelper.cs  # Wraps the lowered unit in a CSharpCompilation
│   └── Runtime/Script/     # Script singleton, Call helpers, event scheduler
├── Keysharp.Tests/         # NUnit test suite; test scripts in Code/
├── Keysharp.Benchmark/     # BenchmarkDotNet perf suite
├── Keysharp.OutputTest/    # Compiled-exe smoke tests (Program.cs is generated output)
└── Keyview/                # Script viewer tool (Scintilla-based in Windows, Eto.Forms-based in other platforms)
```

## Build

Target framework is **net10.0** (net10.0-windows on Windows). Platform is **x64** on Windows, AnyCPU elsewhere.

```bash
# Windows — from repo root
dotnet build Keysharp.sln -c Debug

# Linux — requires sibling Eto fork cloned at ../Eto
# git clone -b Keysharp https://github.com/Descolada/Eto.git ../Eto
dotnet build Keysharp.sln -c Debug
```

Build output lands in `bin/Debug/net10.0-windows/` (or the appropriate TFM subfolder) due to `Directory.Build.props`.

## Run a script

```bash
# Windows
./bin/Debug/net10.0-windows/Keysharp.exe myscript.ahk

# Useful flags
--transpile          # emit generated C# alongside the script and exit (great for debugging transpiler)
--validate           # compile-check only, do not run
--compile exe        # emit a standalone .exe and exit
--compile exe-min    # like exe, with dependencies embedded
--compile dll <path> # emit the raw assembly to <path> (or * for stdout) and exit
```

## Tests

The test suite uses **NUnit 4** and is serialized (`LevelOfParallelism(1)`) because tests share `Script.TheScript` global state. Do not add `[Parallelizable]` attributes.

```bash
# Run the curated subset (safe, no user-input required — matches CI)
dotnet test Keysharp.Tests/Keysharp.Tests.csproj -c Debug --nologo \
  --filter "Category=Assign|Category=BuiltInVars|Category=Class|Category=Collections|Category=Directives|Category=Flow|Category=Function|Category=Hotstring|Category=Math|Category=Misc|Category=Module|Category=Operator|Category=String|Category=Types|Category=FileAndDir|Category=Network|FullyQualifiedName~SchedulerTests|FullyQualifiedName~MessageFilterTests"

# Run a specific category
dotnet test Keysharp.Tests/Keysharp.Tests.csproj --filter "Category=Math"

# Run a single test
dotnet test Keysharp.Tests/Keysharp.Tests.csproj --filter "FullyQualifiedName~MathTests.Abs"
```

> **Important**: The full `dotnet test` run (without `--filter`) includes tests that require interactive GUI or elevated permissions and will block waiting for user input. Always use the curated filter above (or a narrower category filter) when running tests locally. The same filter is used in CI (`.github/workflows/curated-tests.yml`).

> **Important**: Do not run commands to build parts of the project and run tests at the same time.

Test `.ahk` scripts live in `Keysharp.Tests/Code/`. Each test typically:
1. Calls a C# built-in directly and checks the return value.
2. Calls `TestScript("script-name", true/false)` which compiles and runs the matching `.ahk` file and checks for `PASS` in output.

`TestRunner.SetupBeforeEachTest` resets global state before each test. `CleanupAfterEachTest` disposes the `Script` object.

## Architecture: how a script runs

1. `CompilerHelper.CreateCompilationUnitFromFile()` (in `Keysharp.Core/Parsing/CompilerHelper.cs`) drives the front end.
2. `Lexing.Lexer` tokenizes the `.ahk` source; `Syntax.Parser.ParseWithDiagnostics()` builds the strongly-typed AST (`ProgramNode`).
3. `Syntax.Lowerer.Build()` walks the AST and emits a Roslyn `CompilationUnitSyntax` (the generated C#).
4. `CompilerHelper.Compile()` wraps the syntax tree in a `CSharpCompilation` and emits a `MemoryStream`.
5. The compiled assembly is loaded and its entry point is invoked.
6. At runtime, AHK built-ins dispatch to the static methods in `Keysharp.Core/Builtins/`.

Use `--transpile` to see the exact C# that step 3 produces — this is the fastest way to debug lowering issues.

## Platform-specific code

Use `#if WINDOWS`, `#if LINUX`, `#if OSX` preprocessor constants. These are set in each `.csproj`. Corresponding source exclusions (`<Compile Remove="...">`) are in `Keysharp.Core.csproj`. Do not add a Windows-only API without wrapping it in `#if WINDOWS`.

## Conventions

- Built-in AHK functions are `public static` methods in `Keysharp.Core/Builtins/` classes.
- AHK `long`/`double`/`string` map to C# `object` at the scripting boundary; use the `.Al()`, `.Ad()`, `.As()` extension methods to convert.
- The `Script.TheScript` singleton is the single source of truth for all runtime state. It is set once at startup (`Script.cs:369`) and is never null during execution.
- Test scripts signal pass/fail by outputting a line containing `PASS` or `FAIL`.
- Suppress warnings 1701, 1702, 8981, 0164, 8974 project-wide (already in `.csproj`). Do not add new `#pragma warning disable` without a comment explaining why.

## Useful entry points for common tasks

| Task | Start here |
|------|-----------|
| Add/fix a built-in function | `Keysharp.Core/Builtins/<Category>.cs` |
| Fix a parse/syntax error | `Keysharp.Core/Parsing/Lexing/Lexer.cs`, `Parsing/Syntax/Parser.cs` |
| Fix code generation (lowering) | `Keysharp.Core/Parsing/Syntax/Lowerer.cs` |
| Fix hotkey/hook behavior | `Internals/Input/Hooks/HookThread.cs`, `Internals/Input/Keyboard/HotkeyDefinition.cs` |
| Fix timer behavior | `Internals/Threading/ScriptTimerManager.cs` |
| Fix GUI | `Builtins/Gui/Gui.cs`, platform-specific `Windows/` or `Unix/` subfolder |
| Add a test | Prefer `Keysharp.Tests/Code/<script-name>.ahk`, use `Keysharp.Tests/<Category>Tests.cs` if really needed |
