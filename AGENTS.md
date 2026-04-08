# Keysharp — Claude Code Guide

## Project overview

Keysharp is a cross-platform C# implementation of AutoHotkey v2. AHK scripts (`.ahk`/`.ks`) are parsed by an ANTLR4 grammar, transpiled to C#, compiled in-memory with Roslyn, and executed as a .NET assembly. The goal is full AHK v2 compatibility on Windows, with partial Linux and eventual macOS support.

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
│   ├── Parsing/            # ANTLR4 grammar + Roslyn visitor/transpiler
│   │   ├── Antlr/          # *.g4 grammar files + generated lexer/parser
│   │   └── Visitors/       # VisitMain, VisitExpression, VisitFlow, VisitorHelper
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
--codeout        # dump generated C# alongside the script (great for debugging transpiler)
--validate       # compile-check only, do not run
--exeout         # also emit a standalone .exe
```

## Tests

The test suite uses **NUnit 4** and is serialized (`LevelOfParallelism(1)`) because tests share `Script.TheScript` global state. Do not add `[Parallelizable]` attributes.

```bash
# Run all tests (Windows)
dotnet test Keysharp.Tests/Keysharp.Tests.csproj -c Debug

# Run a specific category
dotnet test Keysharp.Tests/Keysharp.Tests.csproj --filter "Category=Math"

# Run a single test
dotnet test Keysharp.Tests/Keysharp.Tests.csproj --filter "FullyQualifiedName~MathTests.Abs"
```

Test `.ahk` scripts live in `Keysharp.Tests/Code/`. Each test typically:
1. Calls a C# built-in directly and checks the return value.
2. Calls `TestScript("script-name", true/false)` which compiles and runs the matching `.ahk` file and checks for `PASS` in output.

`TestRunner.SetupBeforeEachTest` resets global state before each test. `CleanupAfterEachTest` disposes the `Script` object.

## Regenerating the ANTLR grammar

The generated files (`MainLexer.cs`, `MainParser.cs`, etc.) are committed. To regenerate after editing a `.g4` file:

```bash
# Requires the ANTLR4 tool jar (antlr-4.x-complete.jar)
java -jar antlr-4.x-complete.jar -Dlanguage=CSharp -visitor -no-listener \
     -o Keysharp.Core/Parsing/Antlr/ \
     Keysharp.Core/Parsing/Antlr/MainLexer.g4 \
     Keysharp.Core/Parsing/Antlr/MainParser.g4
```

Do not hand-edit `MainLexer.cs` or `MainParser.cs` — they will be overwritten.

## Architecture: how a script runs

1. `Keysharp/Program.cs` → `Parser.ParseFile()` (in `Keysharp.Core/Parsing/Parser.cs`)
2. ANTLR4 lexer + parser build a parse tree from the `.ahk` source.
3. `VisitMain` (and sibling visitors) walk the parse tree and emit Roslyn `SyntaxNode`s.
4. `CompilerHelper.Compile()` wraps the syntax tree in a `CSharpCompilation` and emits a `MemoryStream`.
5. The compiled assembly is loaded and its entry point is invoked.
6. At runtime, AHK built-ins dispatch to the static methods in `Keysharp.Core/Builtins/`.

Use `--codeout` to see the exact C# that step 3 produces — this is the fastest way to debug transpiler issues.

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
| Fix a parse/syntax error | `Keysharp.Core/Parsing/Antlr/MainParser.g4` + `Parsing/Visitors/` |
| Fix code generation (transpiler) | `Parsing/Visitors/VisitMain.cs`, `VisitExpression.cs`, `VisitFlow.cs` |
| Fix hotkey/hook behavior | `Internals/Input/Hooks/HookThread.cs`, `Internals/Input/Keyboard/HotkeyDefinition.cs` |
| Fix timer behavior | `Internals/Threading/ScriptTimerManager.cs` |
| Fix GUI | `Builtins/Gui/Gui.cs`, platform-specific `Windows/` or `Unix/` subfolder |
| Add a test | Prefer `Keysharp.Tests/Code/<script-name>.ahk`, use `Keysharp.Tests/<Category>Tests.cs` if really needed |
