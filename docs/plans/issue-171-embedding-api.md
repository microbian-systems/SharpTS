# Plan: Public embedding API — programmatic compile-and-run (issue #171)

## Goal

A public, library-consumable facade that compiles a TypeScript source string to an
in-memory assembly and executes it in-process, with structured diagnostics and zero
Console writes / `Environment.Exit` on the library path. Consumer: the sharpts-www
playground worker (process-per-request, stdout is a JSON protocol channel).

## What already exists (no new capability needed)

| Need | Existing piece |
|------|----------------|
| In-memory PE bytes | `ILCompiler.SaveToBytes()` (`Compilation/ILCompiler.cs:1202`) |
| In-process execute | `Assembly.Load(bytes)` → `$Program.Main` invoke — proven in `TestHarness.RunCompiledInProcess` and `Test262Runner.cs:485` |
| Structured parse errors | `Parser.Parse()` → `ParseResult` (`Diagnostics`, `IsSuccess`, `HitErrorLimit`) |
| Structured type errors | `TypeChecker.CheckWithRecovery()` → `TypeCheckDiagnosticResult` |
| Diagnostic shape | `Diagnostics/Diagnostic.cs` — severity, code, message, `SourceLocation`, `TsCode` |
| `@ts-ignore` handling | `TypeCheckPolicy.ApplyLineDirectives(diags, lexer.Pragmas)` (mirrors CLI) |

The work is purely assembling these into a facade — the same pipeline the CLI's
`CompileSingleFile` (`Program.cs:526`) runs, minus the Console/Exit/file-system parts.

## New files

### 1. `Compilation/CompilationService.cs` (+ result records, same file or sibling)

```csharp
namespace SharpTS.Compilation;

public sealed record CompileOptions(
    DecoratorMode DecoratorMode = DecoratorMode.None,
    string? AssemblyName = null,     // default: "ts_" + Guid — avoids simple-name
                                     // collisions on repeated Assembly loads in one host
    string FileName = "input.ts");   // used for diagnostic FilePath only

public sealed record CompileResult(
    bool Success,
    IReadOnlyList<Diagnostic> Diagnostics,
    byte[]? AssemblyBytes,
    long CompileTimeMs,
    IReadOnlyCollection<string> RequiredSharpTSRuntimeReasons);

public sealed record RunResult(
    bool Success,
    string? Error,        // unhandled guest exception message, null on success
    long ExecuteTimeMs);

public static class CompilationService
{
    public static CompileResult Compile(string source, CompileOptions? options = null);
    public static RunResult Execute(byte[] assemblyBytes, TextWriter output,
                                    CancellationToken cancellationToken = default);
}
```

**`Compile` pipeline** (clone of `TestHarness.RunCompiledInProcess` lines 271–290, with
recovery-mode checking from `Program.cs:537–552`):

1. `Stopwatch.StartNew()`.
2. **Lex** — `new Lexer(source).ScanTokens()`. The Lexer throws raw `Exception`
   with "… at line N" embedded (`Parsing/Lexer.cs`); catch it and convert to a
   `Diagnostic.ParseError` (regex the trailing `at line (\d+)` into a
   `SourceLocation` when present; message-only otherwise). Do **not** refactor the
   Lexer to diagnostics in this PR — out of scope.
3. **Parse** — `new Parser(tokens, options.DecoratorMode).Parse()`. On
   `!IsSuccess`, return failure with `parseResult.Diagnostics`.
4. **Type check** — `new TypeChecker().WithFilePath(options.FileName)` +
   `SetDecoratorMode`, then `CheckWithRecovery(statements)`. Apply
   `TypeCheckPolicy.ApplyLineDirectives(result.Diagnostics, lexer.Pragmas)` so
   `// @ts-ignore` works like the CLI. Any `Error`-severity diagnostic → failure
   (return all diagnostics, including warnings).
5. **Dead code** — `new DeadCodeAnalyzer(typeMap).Analyze(statements)`.
6. **Compile** — `new ILCompiler(assemblyName)` + `SetDecoratorMode` + `Compile(...)`
   + `SaveToBytes()`. Catch `SharpTSException` → its `.Diagnostic`; catch other
   `Exception` → `Diagnostic.CompileError(ex.Message)`. (This is the no-Console,
   no-Exit replacement for the CLI's catch blocks at `Program.cs:397–415`.)
7. Populate `RequiredSharpTSRuntimeReasons` from
   `ILCompiler.RequiredSharpTSRuntimeReasons` (`ILCompiler.cs:95`) — irrelevant for
   in-process execution (SharpTS.dll is by definition loaded), but lets a host that
   ships DLLs know when to co-locate the runtime.
8. Success → `CompileResult(true, warnings, bytes, elapsed, reasons)`.

Entire body wrapped so **no exception escapes for source-input problems**; only
genuine internal bugs (e.g. `InvalidOperationException` from a compiler defect) may
propagate — debatable, but swallowing those into a CompileError diagnostic (step 6's
catch-all) is friendlier for a web host, so do that.

**`Execute`**:

1. Load via a **collectible `AssemblyLoadContext`** (`isCollectible: true`,
   `LoadFromStream(new MemoryStream(bytes))`), not `Assembly.Load` — issue asks for
   long-lived-host friendliness. `Type.GetType("…, SharpTS")` in emitted IL still
   resolves through the default ALC where SharpTS.dll lives. Call `Unload()` after
   (best-effort; document that pinned statics can delay collection).
2. Resolve `$Program` / public static `Main` (same error messages as TestHarness).
3. **Console contract**: emitted IL writes to `Console.Out`/`Console.Error`
   directly. `Execute` swaps `Console.SetOut(output)` (and `SetError(output)`) in a
   `try/finally` restore. Document explicitly: this is **process-global** — correct
   for a process-per-request worker; not safe for concurrent in-process tenants.
   (The tests' `AsyncLocalConsoleRedirector` solves that with private-field
   reflection; promoting it into the library is a documented follow-up if anyone
   needs concurrent hosting.) This also confirms the issue's question: a caller
   wrapping `Console.SetOut` themselves works — `Execute` just formalizes it.
4. **Cancellation**: register `cancellationToken` → set the emitted
   `$Runtime._cancelRequested` static field (the issue-#74 cooperative-cancel hook
   TestHarness already uses at line 326). Loops then unwind without killing the host.
5. Invoke; unwrap `TargetInvocationException`; an unhandled guest exception →
   `RunResult(false, message, elapsed)` rather than a thrown exception. Time with
   `Stopwatch`.
6. **Documented limitations** (XML docs on `Execute`):
   - guest `process.exit(n)` compiles to `Environment.Exit` → terminates the host
     process (the playground's sandbox model already tolerates this);
   - no wall-clock timeout inside `Execute` — host owns that (use the
     CancellationToken or kill the process);
   - output goes through `Console` redirection, so a caller-supplied capping writer
     sees everything including `Console.Error`.

### 2. `SharpTS.Tests/CompilationServiceTests.cs`

Must live in a **non-parallel xUnit collection** (`[Collection]` with
`DisableParallelization = true`): `Execute`'s `Console.SetOut` swap would otherwise
fight the test suite's `AsyncLocalConsoleRedirector` proxies and sibling tests'
output would leak into the captured writer.

Test cases:
- Acceptance round-trip exactly as written in the issue: compile `console.log` source
  with `DecoratorMode.None`, execute into a `StringWriter`, assert output.
- Type error → `Success == false`, `AssemblyBytes == null`, diagnostic has location
  + message; multiple errors surface (recovery mode, not first-error-throw).
- Parse error and lexer error (e.g. legacy octal) → ParseError diagnostics, no throw.
- `// @ts-ignore` suppresses the flagged line's error (line-directive parity with CLI).
- Guest `throw` → `RunResult.Success == false` with the error message; host survives.
- No Console pollution: wrap `Compile` of a failing source in a console capture,
  assert nothing was written.
- `CompileTimeMs`/`ExecuteTimeMs` populated (>= 0).
- Cancellation: infinite-loop source + pre/quickly-cancelled token → `Execute`
  returns (cooperative cancel) instead of hanging.
- Repeated `Compile`+`Execute` in one process (10×) — unique assembly names, no
  simple-name collision, ALC unload doesn't break subsequent runs.

## Explicitly out of scope (state in PR description)

- **Modules / multi-file** — single source string only, matching the issue.
- **CLI refactor** — `Program.cs` keeps its own flow; runtimeconfig generation,
  SharpTS.dll copy, bundling, NuGet pack are CLI-only concerns. A later cleanup can
  rebase `CompileSingleFile` onto the service, but mixing it in here risks behavior
  drift in `--compile`.
- **TestHarness refactor** — its in-process path predates the service and has
  test-specific needs (AsyncLocal capture, timeout contract). Don't touch.
- **IL-text / decompiled output tab** — the issue's nice-to-have; defer. (Note for
  the follow-up: `ILVerifier` + `System.Reflection.Metadata` reader over
  `SaveToBytes()` output is the likely shape.)
- **Lexer diagnostics refactor** — facade converts thrown messages; making the Lexer
  recovery-based is independent work.

## Perf note (cold-start, requirement 6)

`SaveToBytes` → `Assembly.Load` does a PE serialize/deserialize round-trip
(~15 ms per docs/plans/runtime-tree-shaking.md measurements — fine for the request
path). If that ever matters, `ILCompiler` already has an `inMemoryOnly` mode
(`GetEmittedAssembly()`, ILCompiler.cs:251–264) that skips PE emit entirely — a
`CompileAndExecute(source, options, output)` convenience could use it later, but the
issue explicitly wants the bytes handoff for the timing-comparison UI, so the
two-call API stays primary.

## Suggested commit sequence

1. `feat: add CompilationService.Compile — structured-diagnostic compile-to-bytes facade` (+ Compile-side tests)
2. `feat: add CompilationService.Execute — in-process run via collectible ALC` (+ Execute-side tests, acceptance round-trip)
3. (optional) `docs: README/STATUS blurb for the embedding API`

## Verification

- `dotnet build` clean; `dotnet test --filter "FullyQualifiedName~CompilationService"`.
- Full `dotnet test` once at the end (pipe to file per repo convention) — the
  Console.SetOut swap in a serial collection must not destabilize parallel
  compiled-mode tests.
- Smoke: tiny console host project referencing SharpTS.csproj running the issue's
  acceptance snippet (manual, not committed).
