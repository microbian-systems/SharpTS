# Embedded Standard Library Plan

## Overview

This plan introduces a **baked-in TypeScript standard library** for SharpTS: Node built-in modules are implemented as TypeScript source files embedded in `SharpTS.dll` as resources, compiled into user output alongside user code. This collapses the current dual implementation (`Runtime/BuiltIns/Modules/Interpreter/*` + `Compilation/Emitters/Modules/*`) into a single TS source per module, eliminating "triple-sync risk" and shifting contribution from C#/IL to TypeScript.

The guiding principle is **compile-time resolution with embedded sources**. Stdlib TS files ship inside `SharpTS.dll` as `<EmbeddedResource>` entries. At user-compile time, SharpTS reads the TS via `Assembly.GetManifestResourceStream` and feeds it into the normal module graph. No runtime loading, no install-path discovery, no NuGet restore, no reflection from compiled output back to SharpTS. The resulting user DLL remains standalone.

Related community ask: [Discussion #13 — Dynamic Module Loading](https://github.com/nickna/SharpTS/discussions/13). The original proposal was for third-party "shim" packages distributed via NuGet. This plan responds with a counter-architecture: rather than accepting third-party C# shim assemblies, SharpTS owns its Node API surface, implemented in TypeScript, shipped in-box as a standard library. A third-party extensibility story may return in a future version once the in-box stdlib is stable; it is explicitly out of scope here.

**Terminology note.** The word "shim" appears in the originating discussion because jeremylcarter framed it as a compatibility layer supplied from outside. What this plan actually builds is SharpTS's **authoritative Node standard library implementation in TypeScript** — not a bridge or polyfill. Throughout this document, "stdlib" refers to these baked-in TS modules.

## Goals

1. A Node built-in module can be implemented as a single `.ts` file and work identically in interpreter and compiled modes.
2. A small, formal **primitive layer** exposes host services (I/O, process, os, net, crypto) that stdlib modules build on.
3. Stdlib sources are **embedded resources in `SharpTS.dll`**. Installing or upgrading SharpTS automatically updates all bundled modules.
4. Existing C#-implemented modules continue to work unchanged and remain the fallback. No flag-day migration.
5. User-compiled DLLs stay standalone; stdlib code is bundled, not referenced.
6. Stdlib authoring style keeps future optimizations (tree-shaking, precompilation, caching) additive — no rewrites needed to turn them on later.

## Non-Goals (v1)

- **Third-party module packages** (NuGet, project-local directories, `--shim` CLI flag) — all rejected for v1. SharpTS owns the Node surface.
- **Precompiled stdlib snapshots** — stdlib is compiled at every user compile. Measure first; optimize later if needed.
- **Tree-shaking of stdlib exports** — the current pipeline compiles every import in full. Stdlib modules will be *written in a tree-shakable style* (see Authoring Style), but the optimization itself is deferred.
- **Cross-run caching** of parsed/typed stdlib modules — future optimization.
- **C# shim assemblies** — rejected. Reflection tension with the standalone-DLL constraint and doubled contribution complexity.
- **Runtime stdlib loading** — all resolution at compile time.
- **User-selectable Node version target** — SharpTS picks one Node API level and documents it in `STATUS.md`.

---

## Design Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Stdlib implementation language | TypeScript | One source covers both interpreter and compiled modes; wider contributor pool; runs through existing pipeline. |
| Stdlib distribution | Embedded resources in `SharpTS.dll` | Single-DLL install; no install-path discovery; update-with-upgrade. |
| Resolution time | Compile-time only | Preserves standalone-DLL constraint; matches SharpTS's strict AOT stance. |
| Extension point | `IModuleProvider` with two implementations | Small abstraction for testability; only two sources for v1. |
| Provider precedence | `EmbeddedStdlibProvider` > `BuiltInCSharpProvider` | Stdlib wins if present; C# is the fallback for unmigrated modules. |
| Specifier → file mapping | Convention: `stdlib/node/<name>.ts`, nested paths for `fs/promises` etc. | No manifest file needed for v1; add if convention breaks down. |
| Primitive surface | `primitive:*` pseudo-modules, importable only from stdlib provider code | Gated by origin; user code can't bypass Node semantics. |
| Stdlib authoring style | Pure ESM named exports; no top-level side effects | Costs nothing now; keeps tree-shaking an additive future change. |
| Fallback policy | C# providers retained indefinitely | Migration is opportunistic; no module is orphaned. |
| Duplicate registration | Stdlib match suppresses C# emitter registration; conflict is a loud diagnostic | Prevents double-wiring during migration. |

---

## Architecture

### Provider chain (two providers)

```
┌──────────────────────────────────────────────────────────┐
│ ModuleResolver.ResolveModulePath(specifier)              │
└────────────────────────┬─────────────────────────────────┘
                         │
                         ▼
┌──────────────────────────────────────────────────────────┐
│ StdlibProviderChain.TryResolve(spec) → StdlibModule?     │
├──────────────────────────────────────────────────────────┤
│  1. EmbeddedStdlibProvider    (stdlib/node/*.ts as       │
│                                embedded resources)       │
│  2. BuiltInCSharpProvider     (current C# registries)    │
└────────────────────────┬─────────────────────────────────┘
                         │
                         ▼
                   first match wins
```

The interpreter consults the same chain — a single resolver powers both modes. This is the whole point of the effort: one source of truth per module.

### `IModuleProvider` interface (sketch)

```csharp
public interface IModuleProvider
{
    string Name { get; }
    bool TryResolve(string specifier, out StdlibModule module);
    IReadOnlyCollection<string> ProvidedModules { get; }
}

public record StdlibModule(
    string Specifier,
    StdlibSource Source,         // TypeScript or CSharpEmitter
    string Origin,               // "stdlib" or "builtin"
    string VirtualPath);         // e.g. "stdlib:node/querystring.ts"
```

### Embedded resource layout

In `SharpTS.csproj`:

```xml
<ItemGroup>
  <EmbeddedResource Include="stdlib/**/*.ts" />
</ItemGroup>
```

At user-compile time, `EmbeddedStdlibProvider.TryResolve("querystring")`:
1. Map specifier → resource name: `SharpTS.stdlib.node.querystring.ts`
2. Load via `Assembly.GetManifestResourceStream(resourceName)`
3. Return `StdlibModule` with `VirtualPath = "stdlib:node/querystring.ts"`
4. `ModuleResolver` feeds the source into the normal parse / type-check / emit pipeline

Nested specifiers (`fs/promises`) map to nested paths (`stdlib/node/fs/promises.ts`).

### Virtual source paths

Stdlib code reports its location as `stdlib:node/<name>.ts` in parse diagnostics, type errors, and runtime stack traces. This sits alongside the existing `builtin:<name>` sentinel used by the C# provider — the two prefixes stay **distinct on purpose** so "which provider answered this import" is visible at a glance in diagnostics:

- `stdlib:node/fs.ts:42` → TS stdlib source
- `builtin:fs` → C# built-in (unmigrated)

Requires that the parser's source-location records and the diagnostic printer accept non-filesystem paths cleanly. **Prerequisite check in Phase 1.**

### Primitive layer

Today's `Runtime/BuiltIns/Modules/Interop/` plus scattered BCL calls in each `*ModuleInterpreter.cs` *are* the primitive layer — just unformalized. This plan extracts them into named `primitive:*` pseudo-modules importable only by stdlib-provider code:

| Primitive | Covers | Host impl |
|---|---|---|
| `primitive:io` | open/read/write/stat/close, dirents | `Runtime/Primitives/IoPrimitive.cs` |
| `primitive:process` | argv, env, cwd, pid, exit, stdio FDs | `Runtime/Primitives/ProcessPrimitive.cs` |
| `primitive:os` | platform, arch, cpus, homedir, endianness | `Runtime/Primitives/OsPrimitive.cs` |
| `primitive:net` | socket/listen/connect primitives | `Runtime/Primitives/NetPrimitive.cs` (deferred) |
| `primitive:crypto` | randomBytes, hash, hmac primitives | `Runtime/Primitives/CryptoPrimitive.cs` (deferred) |
| `primitive:buffer` | byte-array allocation, slice, copy, encode/decode | `Runtime/Primitives/BufferPrimitive.cs` (Phase 4+) |

**API discipline:** narrow and boring. Dumb in, dumb out. Return `number` / `string` / `Uint8Array`, not rich objects. Every stdlib module migrated against a primitive becomes a regression test for that signature — changing a primitive shape forces touching every dependent module. Bias toward POSIX-like minimalism.

**Gating:** when the module resolver sees `primitive:*`, it checks that the *importing* module came from a stdlib provider. Non-stdlib imports are a compile error with a clear diagnostic.

### Standalone-DLL impact

None. Stdlib TS sources compile into the user's output DLL exactly like the user's own TS files. No new references, no new reflection, no new runtime dependency.

---

## Authoring Style (Stdlib Contract)

Stdlib modules live in-tree but are held to a light contract so future optimizations stay additive. To be documented in `stdlib/CONTRIBUTING.md` when Phase 2 lands:

1. **Pure ESM named exports.** No default exports for namespace-style modules; named exports enable future tree-shaking without rewrites.
2. **No top-level side effects.** No lookup tables built at module load, no registration calls, no conditional re-exports. If a module needs initialization, put it behind a function that the first caller invokes — don't do it at module top level.
3. **Primitive imports at top of file.** All `primitive:*` imports grouped and minimal. A module that imports from fewer primitives is healthier.
4. **No cross-stdlib imports from leaf modules.** Leaf modules (e.g., `querystring`, `path`) must not import other stdlib modules. Composite modules (e.g., `fs/promises` importing `fs`) are fine but must be migrated in dependency order.
5. **Node semantics are the spec.** Match Node's observable behavior, including error codes (`ENOENT`, `EACCES`, etc.). Diverge only with an explicit documented comment.

---

## Proposed file layout

### New files (v1)

```
Modules/Stdlib/
  IModuleProvider.cs
  StdlibProviderChain.cs
  StdlibModule.cs
  Providers/
    EmbeddedStdlibProvider.cs
    BuiltInCSharpProvider.cs

Runtime/Primitives/
  IoPrimitive.cs          # Phase 3
  ProcessPrimitive.cs     # Phase 3
  OsPrimitive.cs          # Phase 3

stdlib/
  CONTRIBUTING.md         # Authoring contract
  node/
    querystring.ts        # Phase 2 pathfinder
```

### Modified files

| File | Change |
|---|---|
| `SharpTS.csproj` | `<EmbeddedResource Include="stdlib/**/*.ts" />`. |
| `Modules/ModuleResolver.cs` | `ResolveModulePath` consults `StdlibProviderChain` before current `BuiltInModuleRegistry.IsBuiltIn` check. |
| `Execution/Interpreter.Statements.cs` | `ExecuteImport` / `ExecuteImportRequire` consult the same chain. |
| `Compilation/ILCompiler.cs` | `BuiltInModuleEmitterRegistry.Register` skipped for modules claimed by a stdlib entry. Diagnostic on conflict. |
| `Runtime/BuiltIns/Modules/BuiltInModuleRegistry.cs` | `IsBuiltIn` narrows to "known to any provider, stdlib or C#". |
| `Parsing/SourceLocation.cs` (or equivalent) | Accept `stdlib:` virtual path prefix in diagnostics and stack traces (prerequisite verification in Phase 1). |
| `STATUS.md` | Document Node API version target and acknowledged output-size cost before tree-shaking. |

---

## Migration Plan

Strictly additive. Three phases, each independently shippable.

### Phase 1 — Scaffolding (zero behavior change) ✅ COMPLETE

- Introduce `IModuleProvider`, `StdlibProviderChain`, `StdlibModule`. ✅
- Wrap existing registries in `BuiltInCSharpProvider`. ✅
- Add `EmbeddedStdlibProvider` with resource auto-discovery (empty — no stdlib files yet). ✅
- Refactor `ModuleResolver.ResolveModulePath` to consult the chain. ✅
- **Prerequisite verification:** `Diagnostics/SourceLocation.cs` uses a nullable `string FilePath` with no validation. `stdlib:` paths format cleanly; no code change needed. ✅

**Test gate:** every existing module and test behaves identically. Pure refactor. ✅ (9871 passing, 0 failures, 14 pre-existing skips)

**Phase 1 drift from plan — interpreter and ILCompiler dispatch code untouched.** The plan aspirationally said "interpreter import paths" would also consult the chain. In practice, the chain is the single chokepoint at `ModuleResolver.ResolveModulePath`: when `BuiltInCSharpProvider` claims a module it returns the exact same `builtin:<name>` sentinel as before, so downstream consumers (`Interpreter.cs:1169`, `Interpreter.CommonJs.cs:55`, `Interpreter.cs:1424`, `ILCompiler.cs` emitter registration) see identical inputs and don't need changes. The interpreter/compiler dispatch surgery only becomes necessary when the chain actually returns a `TypeScriptSource` — that's Phase 2.

### Phase 2 — Pathfinder migration: `querystring` ✅ COMPLETE

Phase 2 is the first *real* migration and is where the downstream dispatch changes Phase 1 deferred actually land. Work items, in order:

1. **`SharpTS.csproj`:** add `<EmbeddedResource Include="stdlib/**/*.ts" />`.
2. **Write `stdlib/node/querystring.ts`:** pure TS implementation matching Node 24.15.0 semantics for `parse`, `stringify`, `escape`, `unescape`, `decode`, `encode`. No primitive dependencies.
3. **Write `stdlib/CONTRIBUTING.md`:** document the authoring contract (pure named exports, no top-level side effects, primitive imports at top, no cross-stdlib imports from leaves, Node semantics as spec).
4. **Teach `ModuleResolver.LoadModule` the `stdlib:` prefix.** Currently it has a branch for `builtin:` that returns a placeholder `ParsedModule` populated from `BuiltInModuleTypes`. Stdlib paths take a different branch: load source via `StdlibChain`, lex/parse it, return a normal `ParsedModule` whose types flow from ordinary type inference.
5. **Remove `"querystring"` from `BuiltInModuleRegistry._builtInModules`.** This is how `BuiltInCSharpProvider` stops claiming the specifier and lets `EmbeddedStdlibProvider` answer first. Also enforces clean provider ownership (no dual claim).
6. **Delete C# implementations:** `Runtime/BuiltIns/Modules/Interpreter/QuerystringModuleInterpreter.cs`, `Compilation/Emitters/Modules/QuerystringModuleEmitter.cs`, any `RuntimeEmitter.Querystring*.cs` helpers, and the switch case in `BuiltInModuleValues.cs` dispatching to the interpreter. Remove the `_builtInModuleEmitterRegistry.Register(new QuerystringModuleEmitter())` line in `ILCompiler.cs`. Remove any `querystring` entry in `BuiltInModuleTypes` and `HasInterpreterSupport`.
7. **Add stdlib behavior test fixtures** that exercise `import "querystring"` through both interpreter and compiled modes. Use existing Node test vectors where possible.
8. **Verify standalone-DLL test still passes** — stdlib TS compiles into the user DLL like any other TS; there's no new reflection back to SharpTS.

**Why `querystring` first:** pure string parsing, no I/O, no primitive dependency, small existing code, covered by tests, removes real duplication on first merge. Zero host surface = cleanest proof of the pipeline.

**Test gate:** all `querystring` tests pass identically in both modes. Standalone-DLL test asserts no regressions.

**Conflict diagnostic (deferred).** The plan originally called for a loud diagnostic when both a stdlib entry and a C# emitter claim the same module. We're deferring that to when it becomes reachable — for now, removing a specifier from `BuiltInModuleRegistry` as part of migration makes dual claims impossible by construction. `StdlibProviderChain.FindAllClaimants` already exists to support the diagnostic later.

**Phase 2 drift from plan:**

- **Pre-requisite gap-fill landed as its own commit.** Writing `querystring.ts` needed `encodeURIComponent`/`decodeURIComponent`. These were declared in `BuiltInNames.cs` but had no runtime or compiler handler — a latent gap surfaced by the stdlib work. Gap-filled separately (commit `ee83fb2`) before the migration itself. This kind of "migrating a shim uncovers a missing JS primitive" outcome is expected (see Risks: *"stdlib authoring becomes a forcing function for compiler completeness"*) and will likely happen again on future migrations.

- **`ModuleResolver.GetCachedModule` normalized virtual paths.** The method ran `Path.GetFullPath` on everything not starting with `builtin:`, which mangled `stdlib:` paths into filesystem-rooted strings and caused cache misses during type checking. Fixed: both `builtin:` and `stdlib:` prefixes now bypass normalization. Surfaced by the first querystring test run.

- **Default parameters do not work through `$TSFunction.Invoke`.** Discovered during Phase 2 migration, documented below. Workaround applied in `querystring.ts`; proper fix deferred.

## Compiler gap: default parameters via `$TSFunction.Invoke` — FIXED for reference types

**Original issue.** Functions with default parameters compile into multiple overload methods (see `OverloadGenerator`): a full-arity method plus forwarding wrappers for each shorter arity. Direct compiled call sites select the correct overload by arg count. But the `$TSFunction.Invoke(object[])` runtime wrapper — used for every module import — pads args with null via `AdjustArgs` and always dispatches to the full-arity method. The full-arity method had no default-value handling, so callers through that path saw null for every missing defaulted argument.

**Fix.** The full-arity method now also emits the inline null-check pattern (`if (arg == null) arg = <default>`) at the top of its body. Direct callers still go through the overloads; module-imported callers now get the defaults applied correctly. Regression tests in `SharpTS.Tests/SharedTests/DefaultParameterTests.cs`.

**Limitation: value-type defaults.** The null-check pattern (`ldarg; brtrue`) doesn't work for value-type parameters (a `double` or `bool` can't be null). The fix's helper is type-aware and skips value-type params. Stdlib authors using numeric or boolean defaults in module-exported functions should use `param?: T` + `??` instead — documented in `stdlib/CONTRIBUTING.md`. Proper handling would require either (a) boxing all parameters, defeating the boxing-elimination optimization, or (b) changing `AdjustArgs` to dispatch by arity to the correct overload. Deferred.

### Phase 3a — Primitive infrastructure + origin-gating ✅ COMPLETE

Infrastructure-only commit. No user-visible module migrations; proves the
primitive layer resolves end-to-end and that user code cannot reach it.

- `Modules/Stdlib/PrimitiveRegistry.cs` — narrow, explicit set of primitive specifiers (`primitive:os` to start). Deliberately additive; growing it is a conscious architectural choice. ✅
- `Modules/Stdlib/Providers/PrimitiveProvider.cs` — claims `primitive:*` specifiers as `CSharpBuiltInSource` with a `primitive:<name>` virtual path. Added to the chain ahead of `EmbeddedStdlibProvider`. ✅
- `ModuleResolver.ResolveModulePath` — origin-gates `primitive:*` specifiers: imports from any non-`stdlib:` origin throw a clear diagnostic naming the specifier and explaining the namespace is reserved. ✅
- `ModuleResolver.LoadModule` — treats `primitive:` virtual paths analogously to `builtin:`: builds a placeholder `ParsedModule` with `IsBuiltIn = true` whose `ExportedTypes` come from `BuiltInModuleTypes.GetPrimitiveTypes`. ✅
- `Execution/Interpreter.cs` — dispatches `primitive:` module paths through a new `PrimitiveModuleValues.GetPrimitiveExports`, which currently delegates to the existing C# module implementations (reusing code without duplicating it). ✅
- `TypeSystem/BuiltInModuleTypes.cs:GetPrimitiveTypes` — types for `primitive:os` reuse `GetOsModuleTypes` for now (same shape as user-facing `os`). ✅
- `SharpTS.Tests/SharedTests/PrimitiveModuleGatingTests.cs` — registry unit tests, provider unit tests, and an integration test asserting that user code importing `primitive:os` fails with the gating diagnostic. ✅

**Test gate:** 9896 tests pass (+5 new), 14 pre-existing skips. Zero regressions.

**Scope note.** The plan's Phase 3 called for migrating `path` alongside the infrastructure. That conflated two independent decisions — "do primitives work?" and "is the existing `path` implementation replaceable in TS?" — so we split them. 3a proves the primitive mechanism without touching any user-facing module; 3b (next) does the first real migration through primitives.

**Compiled-mode coverage.** No stdlib TS module currently imports from a primitive, so compiled-mode stdlib→primitive dispatch is not yet exercised. The existing compiled-mode `querystring` path is primitive-free, so 3a doesn't need new compiler work. The compiler changes for `primitive:*` imports land in 3b when the first consumer arrives.

### Phase 3b — First primitive consumer: `os` ✅ COMPLETE

Migrated `os` to `stdlib/node/os.ts` as the first module that imports from a `primitive:*` specifier. Proved the end-to-end stdlib→primitive dispatch in both interpreter and compiled modes.

- `stdlib/node/os.ts` — thin Node-shape facade (~80 lines) forwarding every function/constant to `primitive:os`. Existing OS-specific C# code (`OsModuleInterpreter`, `OsModuleEmitter`) stays — it's now reached through the primitive, not directly. ✅
- `Compilation/Emitters/Modules/OsModuleEmitter.cs` re-registered under `ModuleName = "primitive:os"`. The existing IL dispatch works unchanged; only the registry key moved. ✅
- `Compilation/ILCompiler.cs:PreScanBuiltInModuleImports` and `Compilation/ILEmitter.Modules.cs:EmitImport` recognize `primitive:*` specifiers and route them through the same built-in emitter dispatch used for the legacy C# modules. ✅
- Removed `"os"` from `BuiltInModuleRegistry`, `BuiltInModuleValues` dispatch, and `BuiltInModuleTypes.GetModuleTypes` — the stdlib provider now answers user-facing `import 'os'` through the EmbeddedStdlibProvider chain. ✅
- Parser gap-fills landed alongside (general compiler improvements, not os-specific): function declarations accept TypeScript contextual keywords (`function type()` et al.), export specifiers accept them after `as`, the `type` modifier in `{ type as X }` imports is now disambiguated via lookahead, and trailing commas in specifier lists are allowed. ✅

**Test gate:** 9900 pass, 15 skipped (1 new documented limitation), zero regressions. All 48 `OsModuleTests` exercise the full migrated path in both modes.

**Prerequisite bug surfaced.** The migration work exposed a pre-existing compiler bug: cross-module top-level name collisions shared a single `_topLevelStaticVars` dict, so a `const foo` in one module would shadow an exported `function foo()` in another. The common import/export case (the one stdlib migrations routinely hit — `os.platform`, `querystring.parse`, etc.) is fixed in a prior commit with per-module static-field scoping; one rarer capture-collision case (two modules both declaring a same-named captured const) is documented as a known limitation via `[Theory(Skip=...)]` on `TwoModulesDeclaringSameConstName`.

**Compiled-mode dispatch proof.** `import * as os from 'os'` now resolves to stdlib `os.ts`, which imports named members from `primitive:os`. The compiler's PreScan recognizes the primitive specifier and routes calls through the same `BuiltInModuleEmitterRegistry` that used to handle `import 'os'`. End result: no new IL code paths, just re-routing — a minor commit-size testament that the primitive layer's design works with existing compiler infrastructure.

### Phase 3c — Path migration prerequisites (partial)

Attempted the `path` migration and discovered two pre-existing compiler bugs that block it. Shipping the prerequisite infrastructure now (primitives, parser gap-fills, regression tests for the blockers); the actual `path` → stdlib move returns after the compiler fixes.

**Landed:**

- `primitive:process` added to `PrimitiveRegistry`, `PrimitiveProvider`, `PrimitiveModuleValues` (interpreter), `BuiltInModuleTypes.GetPrimitiveTypes`. Delegates to the existing `ProcessModuleInterpreter` for runtime values.
- `BuiltInModuleEmitterRegistry.RegisterAlias` method and `ProcessModuleEmitter` registered under `primitive:process` (in addition to `process`) so stdlib modules can dispatch process calls through the primitive specifier without disrupting user-level `import { cwd } from 'process'`.
- Arrow-function parameter parser accepts TypeScript contextual keywords (`from`, `type`, etc.) — general compiler improvement that surfaces any time stdlib code writes `(from: string, to: string) => ...` style signatures.
- `SharpTS.Tests/SharedTests/CrossModuleRestParamTests.cs` — two `[Theory(Skip=…)]` regression fixtures that pin the compiler bugs below. Removing `Skip` once each bug is fixed will prove the gate naturally.

**Compiler bugs exposed (blocking path migration):**

1. **Rest-parameter cross-module dispatch drops args.** A function with `export function fn(...parts: string[])` called from another module receives `parts = null` — same-module calls work fine. `path.join`, `path.resolve`, and any stdlib function with rest params hit this immediately. Root cause is probably in `$TSFunction.Invoke` / `AdjustArgs` not packaging the tail of args into a rest-array before dispatching to the method.
2. **Object-literal methods lose their invocation target across imports.** `export const posix = { join(...p) {...} }` with `ns.join(...)` called via import throws `NullReferenceException` in `$Runtime.InvokeMethodValue`. This blocks Node's `path.posix.*` / `path.win32.*` sub-object APIs — the natural shape for platform-flavored namespaces.

**Why defer rather than push through:** both bugs are generic compiler fixes touching cross-module call emission; bundling them with a 400-line TS migration would mix concerns and make review harder. Same pattern as the scoping fix that landed before Phase 3b — isolate the compiler change, then the migration becomes small and auditable.

### Phase 3d — Path migration ✅ COMPLETE

Migrated `path` to a pure-TS implementation in `stdlib/node/path.ts` (~840 lines). Unlike the `os` facade pattern (where a thin TS shim forwards to `primitive:os`), path gets real logic in TypeScript — string manipulation is portable, and keeping it in TS means Node-compatible semantics live with the rest of the stdlib rather than scattered across C# emitters.

- `stdlib/node/path.ts` — full POSIX + Win32 implementation covering `join`, `resolve`, `basename`, `dirname`, `extname`, `normalize`, `isAbsolute`, `relative`, `parse`, `format`, plus `sep`/`delimiter` constants and `posix`/`win32` sub-objects. Imports `cwd` and `platform` from `primitive:process` — the only host dependency. ✅
- Removed `"path"` from `BuiltInModuleRegistry`, `BuiltInModuleValues` (dispatch + `HasInterpreterSupport`), and `BuiltInModuleTypes.GetModuleTypes`. Deleted `GetPathModuleTypes`/`GetPathObjectType`/`BuildPathMembers`. ✅
- Deleted `Runtime/BuiltIns/Modules/Interpreter/PathModuleInterpreter.cs`, `Compilation/Emitters/Modules/PathModuleEmitter.cs`, `Compilation/PathHelpers.cs`, `Compilation/RuntimeEmitter.PathModule.cs`, `Compilation/RuntimeEmitter.PathHelpers.cs`. Removed `PathFormat`/`Posix*`/`Win32*`/`ComputeRelative` runtime slots from `EmittedRuntime.cs` and their emission from `RuntimeEmitter.RuntimeClass.cs`. ✅

**ESM→CJS interop gap surfaced.** `require('path')` worked when `path` was a C# builtin (direct dispatch in `BuiltInModuleRegistry`). Once path became a TS ESM module, both modes broke because CJS require had no path for ESM-in-the-assembly: interpreter returned `DefaultExport` (null for named-exports-only modules), compiled mode only checked `CommonJsGetExportsMethods` (CJS-classified files). Fixed both:

- Interpreter: `GetCurrentExports` now falls back to `ExportsAsObject()` when `DefaultExport` is null — mirrors Node's ESM→CJS convention of returning a namespace object of named exports.
- Compiled: `TryEmitCjsRequireCall` now handles ESM modules present in `ModuleExportFields` by calling the module's `$Initialize` and materializing a namespace `Dictionary → SharpTSObject` from the export static fields.

This is the pre-existing general ESM-from-CJS gap finally getting paid down — would have bitten any future stdlib migration with CJS consumers. `os` migration didn't expose it because no `require('os')` test existed.

**Path.ts authoring notes.**

- **`ext != null` instead of `ext !== undefined` for optional parameters.** Compiled-mode passes unset optional reference-type args as C# null. `null !== undefined` evaluates true in JS semantics, so the narrow check falls through; `.length` on null then NREs. Loose equality with null catches both cases. Documented as a stdlib authoring rule going forward.
- **`win32.isAbsolute('/foo')` returns false** (matches pre-existing C# behavior). Node returns true because Node treats any leading separator as absolute on win32; SharpTS historically required a drive letter OR double-separator UNC prefix. Tests pin the SharpTS behavior, so the TS port preserves it with an explicit comment.

**Test gate:** 9904 pass, 15 skipped (pre-existing), 0 regressions. Standalone-DLL test passes — no new reflection back to SharpTS.

### Beyond v1 (not scheduled here)

Migrate modules opportunistically, leaves first. Dependency-ordered candidates:

1. **Leaves** (no stdlib-to-stdlib imports): ~~`url`~~ ✅ (done as Phase 3h — full WHATWG Living Standard port; see below), ~~`events`~~ ✅ (done as Phase 3f — self-contained TS EventEmitter, C# SharpTSEventEmitter retained for runtime inheritance), ~~`assert`~~ ✅ (done as Phase 3e — pure-logic leaf, ~1700 deletions → 290-line TS), ~~`string_decoder`~~ ✅ (done as Phase 3g — TS class over Buffer JS API, ~800 deletions → 105-line TS)

### Phase 3h — URL migration ✅ COMPLETE

Full WHATWG URL Living Standard port: parser state machine (all 20 states), IPv4/IPv6/opaque host parsing, percent-encode sets, WHATWG-compliant serialization, URL class with property getters/setters, URLSearchParams, legacy parse/format/resolve, fileURLToPath/pathToFileURL. ~1640 lines of TS, replaces ~2400 lines of C# (SharpTSURL, UrlModuleInterpreter, UrlModuleEmitter, RuntimeEmitter.Url) plus pattern-matched compile-time interception.

**Architectural change**: ripped out the global-URL escape hatch. Previously `new URL(...)` without an import pattern-matched to a compile-time-emitted `$URL` class backed by System.Uri, so a user's global URL saw different semantics from `import { URL } from 'url'`. The built-in `$URL` / `$URLSearchParams` emitters are now removed. The TS stdlib class is the single source of URL behavior; user code must `import { URL, URLSearchParams } from 'url'` to use them (matches ESM-strict stance; no global URL polyfill).

**Compiler bugs surfaced and fixed as part of this phase:**

1. Class constructors with `return;` emitted invalid IL (the return-type default fell through `EmitReturn`'s object branch because `CurrentMethodReturnType` wasn't set for ctors). Fix: set `CurrentMethodReturnType = typeof(void)` in the ctor context.
2. Indexing `typeof x === 'object'`-narrowed values by string threw "Index type 'string' is not valid for indexing 'object'", aborting the enclosing method's type check mid-body. That aborted body triggered the push-wrap pattern from Phase 3h's earlier three-bug commit. Fix: accept `TypeInfo.Object` with string index, return `Any`.

**Workarounds living in `stdlib/node/url.ts` (to remove when the compiler catches up):**

- URLSearchParams stores keys and values as two parallel `string[]` arrays, not a `string[][]` of pairs — nested-array push on class fields still has a codegen gap where the inner array gets wrapped one level deep.
- URLSearchParams.sort uses insertion sort, not `Array.sort` with a comparator, because `this` captures inside arrow comparators fail to resolve.
- `get`/`set`/`delete` are assigned as per-instance function properties in the constructor because the parser treats `get`/`set` as accessor keywords and `delete` as a reserved word when used as class method names.
- String comparison via `localeCompare` instead of `>` because compiled-mode relational operators on strings coerce to numbers today.

**Scope for follow-up:** the workarounds are load-bearing but narrow; each has a tracked pattern. None affect correctness of URL behavior. Fixing them collapses `url.ts` back toward idiomatic TS.

2. **Composite after leaves**: `util`, `stream`, `fs`, `fs/promises`, `readline`, `http`, `https`, `net`, `tls`
3. **Hybrid: thin TS module over `primitive:buffer`** — `Buffer` gets a TS stdlib module for API symmetry, but heavy lifting (byte-array alloc, slice, copy, encode/decode) stays native in `primitive:buffer`. This keeps every module on equal footing (every module has a `.ts` file) without a perf cliff on hot paths. The primitive surface is stable — Node's Buffer API is locked down, so `primitive:buffer` can be designed once and held.

Each migration is its own PR with the same test gate: identical behavior, no standalone-DLL regressions, module written to the authoring contract.

---

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| User compile time grows as stdlib count grows | Measure during Phase 2. If painful, add Option C (precompile stdlib at SharpTS build time, embed IL) — infrastructure is compatible. |
| Output DLL size bloats for small scripts that import big modules | Acknowledged cost before tree-shaking lands. Document in `STATUS.md`. Tree-shaking reclaims most of it. |
| Tree-shaking later requires rewriting modules | Authoring Style contract prevents this: pure named exports, no top-level side effects. |
| TS stdlib performance is worse than hand-tuned IL for hot-path modules | Keep hot-path modules in C#. Migration is opportunistic — nothing forces it. |
| Primitive API churn forces mass stdlib rewrites | Primitive API discipline: narrow, boring, stable. Treat as internal contract. |
| Stdlib code exercises TS features SharpTS hasn't implemented | *This is a feature.* Stdlib authoring becomes a forcing function for compiler completeness. Budget for it. |
| Debugging unclear: is failure in stdlib, compiler, or primitive? | Separate test categories (stdlib behavior tests vs primitive unit tests) introduced in Phase 3. |
| Migration-order bugs (composite stdlib module depends on unmigrated leaf) | Enforce leaf-first migration order. Composite modules can import unmigrated ones via the C# provider transparently (chain handles it), so ordering is a quality concern, not a correctness one. |
| Virtual source paths break existing diagnostic/stack-trace code | Prerequisite verification in Phase 1. Fix before any stdlib module lands. |
| Loss of third-party plugin story disappoints contributors | Acknowledge in the Discussion #13 response; leave the door open for v2 NuGet plugins if demand builds. |

---

## Community Response (Discussion #13)

Draft reply to jeremylcarter:

> Thanks — this discussion pushed the right architectural question, even though we're going to land somewhere different from what you proposed. After walking through it, here's the direction:
>
> Rather than third-party C# shim assemblies (which would require reflection from compiled output back into the shim, conflicting with SharpTS's standalone-DLL constraint), we're going to own the Node surface in-box as a **TypeScript standard library embedded in `SharpTS.dll`**. Installing/upgrading SharpTS automatically updates every bundled Node module. Contributors who want to add or fix a module write TypeScript, not C#/IL.
>
> This closes the door on *third-party* plugins for v1 — everything lives in the main repo. That's deliberate: we get quality control, unified testing, and a single Node compatibility target. A NuGet/plugin story may return in a future version once the in-box stdlib is stable.
>
> The contribution opportunity is real: there are ~30 Node modules in the repo today, each implemented twice (interpreter + compiler). A migration to TS collapses both into one source. If you're interested, the first pathfinder is `querystring` — small, no I/O, covered by tests. Design doc: [link]. Want to take the first one?

## Resolved Decisions

1. **Node API version target:** Node.js 24.15.0 (current LTS as of 2026-04). Stdlib behavior matches this version's observable semantics; divergences require explicit documented comments.
2. **Buffer strategy:** hybrid — thin TS stdlib module over `primitive:buffer`. Symmetry without the perf cliff of a pure-TS Buffer.
3. **Virtual path convention:** `stdlib:node/<name>.ts` for TS stdlib modules, `builtin:<name>` retained for C#-provided modules. Distinct prefixes so the provider is visible in diagnostics.
4. **Naming:** the system is SharpTS's **embedded standard library**, not a "shim system." "Shim" is retained only when referring to jeremylcarter's original proposal framing.

## See Also

- `docs/plans/commonjs-support.md` — strict AOT philosophy this plan inherits
- `CLAUDE.md` — Standalone DLL constraint and emission rules
- `STATUS.md` — current module coverage and Node API target
- [Discussion #13](https://github.com/nickna/SharpTS/discussions/13) — origin of this plan
