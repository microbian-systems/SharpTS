# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

SharpTS is a TypeScript interpreter and compiler implemented in C# using .NET 10.0. It supports both tree-walking interpretation and ahead-of-time compilation to .NET IL.

## Build and Run Commands

```bash
dotnet build                              # Build the project
dotnet test                               # Run xUnit tests
dotnet test --filter "FullyQualifiedName~SomeTest"  # Run specific test
dotnet run                                # Start REPL mode
dotnet run -- script.ts                   # Interpret a TypeScript file
dotnet run -- --compile script.ts         # Compile to .NET assembly
dotnet run -- --compile script.ts -o out.dll  # Custom output path
dotnet run -- --compile script.ts --verify    # Verify emitted IL
```

## Architecture

### Pipeline Flow

```
Source → Lexer → Parser → TypeChecker → Interpreter (tree-walk)
                                     ↘ ILCompiler (AOT to .NET IL)
```

### Directory Structure

| Directory | Purpose |
|-----------|---------|
| `Parsing/` | Lexer, Parser (partial files), AST node definitions |
| `TypeSystem/` | Static type checking, type compatibility, generics |
| `Execution/` | Tree-walking interpreter |
| `Compilation/` | IL compilation (largest directory), async state machines, bundling |
| `Runtime/` | Runtime values, environment, built-ins |
| `Runtime/Types/` | TypeScript value types: arrays, classes, promises, etc. |
| `Runtime/BuiltIns/` | Built-in method implementations |
| `Runtime/Exceptions/` | Non-local unwinding exceptions (throw, yield) |
| `Modules/` | Module resolution, script detection |
| `Diagnostics/` | Error reporting, source locations |
| `Packaging/` | NuGet package generation |
| `Cli/` | Command-line argument parsing |
| `Declaration/` | TypeScript declaration generation from .NET types |
| `LspBridge/` | Language Server Protocol bridge for IDE integration |
| `SharpTS.Tests/` | xUnit test project |

### Critical Architecture Patterns

**Two-Environment System:**
- `TypeEnvironment` - Tracks types during static analysis (compile-time)
- `RuntimeEnvironment` - Tracks values during execution (runtime)
- Never mix these - they serve completely different phases

**Control Flow:**
- Return/break/continue are signaled via the `ExecutionResult` struct (`Execution/ExecutionResult.cs`), not exceptions
- `ThrowException` (guest `throw`) and `YieldException` (generator suspension) remain as intentional exception-based unwinding

**Type System:**
- Structural typing for interfaces (duck typing)
- Classes are compared **structurally** for assignment compatibility (matching `tsc`), **except** when the target class is branded by a private/protected member anywhere in its hierarchy — then it is nominal (source must be the same class or a subclass). Inheritance/subtyping is still nominal (name-based) for the hierarchy walk. See `TypeChecker.Compatibility.cs` (`Instance` vs `Instance`) and `HasNominalClassBrand`.
- `TypeInfo` records represent types statically; runtime objects are independent

### RuntimeValue Boxing Elimination (Active Optimization)

The codebase is migrating from `object?` to `RuntimeValue` struct to eliminate boxing:

**RuntimeValue** (`Runtime/RuntimeValue.cs`):
- 24-byte discriminated union storing primitives inline
- `ValueKind` enum: Undefined, Null, Boolean, Number, String, Object, Symbol, BigInt
- Factory methods: `FromNumber()`, `FromString()`, `FromBoolean()`, `FromObject()`, `FromBoxed()`
- Accessors: `AsNumber()`, `AsString()`, `AsBoolean()`, `AsObject<T>()`
- JavaScript semantics: `IsTruthy()`, `TypeofString()`

**ISharpTSCallable** (`Runtime/Types/SharpTSFunction.cs`) is the single callable interface:
- Boxed `Call(Interpreter, List<object?>)` (being retired) plus `CallV2(Interpreter, ReadOnlySpan<RuntimeValue>)`
- `CallV2` has a default implementation that bridges to `Call` — unmigrated implementors work unchanged; migrated ones override it to run boxing-free
- Call sites holding boxed args use `.CallBoxed(...)` from `Runtime/Types/CallableInterop.cs` (never invoke legacy `Call` directly in new code)
- Spans can't cross `await`: async call sites evaluate all args first, then call synchronously; async/generator implementors copy span → list before starting the state machine

**BuiltInMethod** (`Runtime/BuiltIns/BuiltInMethod.cs`):
- `CreateV2()` factory for RuntimeValue-based methods; `HasV2Implementation` gates the interpreter fast path
- Legacy delegate bodies (`Func<Interpreter, object?, List<object?>, object?>`) still work via an internal wrapper — converting them is incremental follow-up work
- Thread-local pooling in array built-ins to avoid allocations

### Visitor-Style Traversal Pattern

All major phases use switch pattern matching on AST node types:
- `TypeChecker.Check()` / `CheckExpr()` - static analysis
- `Interpreter.Execute()` / `Evaluate()` - runtime
- `ILEmitter.EmitStatement()` / `EmitExpression()` - IL compilation

### AST Nodes

All AST nodes are C# records in `Parsing/AST.cs`:
- Expression nodes inherit from `Expr`
- Statement nodes inherit from `Stmt`
- Use pattern matching to traverse

### IL Compilation Phases

ILCompiler runs in multiple phases:
1. Emit runtime types
2. Analyze closures
3. Define classes/functions
4. Collect arrow functions
5. Emit arrow bodies
6. Emit class methods
7. Emit entry point
8. Finalize types

Arrow functions use display classes for captured variables; non-capturing arrows compile to static methods.

### CRITICAL: Standalone DLL Constraint

**Compiled TypeScript DLLs must NOT reference SharpTS.dll.** The output DLL must be fully standalone.

**NEVER do this in Compilation/ files:**
```csharp
// BAD - embeds SharpTS.dll reference in output
var method = typeof(RuntimeTypes).GetMethod("SomeMethod");
il.Emit(OpCodes.Call, method);
```

**Instead, use reflection-based IL that resolves at runtime:**
```csharp
// GOOD - uses RuntimeEmitter helper methods
EmitReflectionCall(il, "SharpTS.Compilation.RuntimeTypes, SharpTS", "SomeMethod", argCount);
// or for void methods:
EmitReflectionCallVoid(il, "SharpTS.Compilation.RuntimeTypes, SharpTS", "SomeMethod", argCount);
```

The same applies to `PropertyDescriptorStore`, `ObjectBuiltIns`, and any other SharpTS types.

**Why:** When emitting IL with `typeof(X).GetMethod(...)`, the method token references the SharpTS assembly directly. This creates a hard dependency. The reflection pattern emits IL that does `Type.GetType("..., SharpTS")` at runtime, allowing graceful degradation if SharpTS isn't present.

**Soft-dependency signal + auto-copy:** Some features (eval, Proxy, Intl, vm, dns, `@DotNetType` dynamic events) emit a `Type.GetType("…, SharpTS")` path whose *normal* execution needs SharpTS.dll present. When such a path is emitted, the emit site records a reason via `EmittedRuntime.RequireSharpTSRuntime(reason)`, surfaced through `ILCompiler.RequiredSharpTSRuntimeReasons`. After `Save`, the CLI (`Program.cs` `CopySharpTSRuntimeIfNeeded`) co-locates SharpTS.dll with the output **only when** that set is non-empty — programs using none of these stay fully standalone. `--compile … --standalone` suppresses the copy (those features then throw a clear "not supported" error at runtime). Do NOT record reasons for pure-BCL features (zlib, child_process, JSON) or unconditional graceful-fallback plumbing (`process`) — only paths a normal program actually reaches.

### Error Handling Conventions

- Type errors: "Type Error:" prefix
- Runtime errors: "Runtime Error:" prefix
- Compile errors: "Compile Error:" prefix

## Important Implementation Details

- **For Loop Desugaring:** Parser converts `for` loops into `while` loops
- **console.log:** Hardcoded special case in type checker, interpreter, and compiler
- **Inner function declarations:** Supported in IL compiler with hoisting, closure capture, and recursion
- **Method Lookup:** Searches up inheritance chain (see `TypeChecker.cs` CheckGet, `Interpreter.Properties.cs` EvaluateGet)

## Conformance Suites

Two standalone test projects pin SharpTS against external corpora. Neither is in `SharpTS.sln`; invoke explicitly. Both use a committed-baseline + diff harness — runs hard-fail on regression or new-pass.

- **`SharpTS.Test262/`** — TC39 ECMA-262 (interpreter + compiled). Update baseline: `SHARPTS_TEST262_UPDATE_BASELINE=1`.
- **`SharpTS.TypeScriptConformance/`** — `microsoft/TypeScript` conformance (type-checker only). Update baseline: `SHARPTS_TSCONFORMANCE_UPDATE_BASELINE=1`. Bucket model: Pass/Fail/ParseError/TypeCheckError/Skipped/HarnessError. Match strategy: `(line, tsCode)` tuples — `tsCode` is the canonical `TSnnnn` code carried on every type-checker `Diagnostic` (see `Diagnostics/Diagnostic.cs`). See `SharpTS.TypeScriptConformance/README.md`.

## See Also

- `STATUS.md` - Feature implementation status and known bugs
- `README.md` - User documentation and examples
