# CommonJS Support Plan

## Overview

This plan adds CommonJS (`require`, `module.exports`, `exports`) support to SharpTS so users can consume the npm ecosystem. Both interpreter and AOT-compiled modes are covered, but interpreter ships first.

The guiding principle is **strict AOT**: in compiled mode, CommonJS is resolved entirely at compile time. Anything that cannot be resolved statically (dynamic specifiers, `eval`, `new Function`) is a compile error. This commits SharpTS's compiled mode to a "no runtime interpreter" architecture, matching `deno compile`, NativeAOT, and Go.

## Goals

1. ESM TypeScript code can `import` from CommonJS npm packages.
2. CommonJS files (`.cjs`, or `.js` resolved as CJS) can `require` other CommonJS files.
3. Pure-JS utility packages work end-to-end: lodash, debug, chalk, ms, uuid, semver, minimatch, yaml.
4. TypeScript type checking continues to work via existing `.d.ts` resolution.
5. Compiled output remains a standalone `.dll` with no SharpTS runtime dependency.

## Non-Goals (v1)

- `require()` of ES modules from CommonJS (Node only allows this via dynamic `import()` returning a Promise — defer).
- Dynamic specifiers: `require(variable)`, `require(`./prefix/${x}`)`, etc. → **compile error**.
- `eval`, `new Function` of dynamic source → **compile error in compiled mode**.
- `require.cache`, `require.extensions` → not implemented.
- Native addons (`.node` binaries via N-API) → out of scope.
- Frameworks with large dep trees (express, koa, commander) → v2 milestone.

---

## Design Decisions

| Decision | Choice | Rationale |
|---|---|---|
| CJS detection | All signals: `.cjs` extension, `package.json` `"type":"commonjs"` (or absent), or heuristic on `.js` files containing `require`/`module.exports` without ESM syntax | Maximum npm compatibility; matches Node's resolution rules |
| Interop direction | ESM imports CJS only (v1) | Dominant real-world case; CJS-requires-ESM is rare for TS-first projects |
| Optional dep pattern | Allow `require('literal')` even if missing; emit IL that throws `MODULE_NOT_FOUND` at runtime so `try/catch` fires | Required for chalk/debug/express which probe for optional native deps |
| Sequencing | Interpreter first as standalone milestone, then compiler | Smaller change unblocks REPL/script users; validates semantics before committing to IL |
| Dynamic specifiers | Reject all non-literal `require` at parse/check time | "Fail fast" stance; predictable; deferrable to context-modules in v2 |
| `require` API surface | `require.resolve('id')` only | Used by packages locating sibling fixtures; computable as a compile-time string constant. `require.main`, `require.cache`, `require.extensions` deferred |
| Compatibility target | Pure-JS utility packages | Realistic v1, exercises all CJS edge cases without native-addon detour |
| node_modules resolution | Full nested traversal, reusing existing ESM resolver | Handles transitive deps with version conflicts; avoids code-path drift |
| Type info source | Reuse existing `.d.ts` pipeline (`package.json` `types`/`typings`) | Already implemented per STATUS-NODE.md; no new type machinery |
| Output shape | One .NET class per CJS source file | Mirrors ESM emission; readable stack traces; clean lazy init; correct circular semantics |
| Runtime patterns | `__dirname`, `__filename`, `global`/`globalThis`, `process`, `Buffer` bound in CJS scope; circular requires with partial exports per Node spec | Required for the v1 target packages |

---

## Architecture

### CJS Module Wrapper

Every CJS file is conceptually wrapped in:

```js
(function(exports, require, module, __filename, __dirname) {
  // ...file body...
});
```

The wrapper is synthetic — the parser/loader injects these as locals into the module's top-level scope. The file body sees them as ordinary identifiers.

### Module Detection (`Modules/CommonJsDetector.cs` — new)

Resolution order for a candidate file:

1. Extension check: `.cjs` → CJS, `.mjs` → ESM, `.ts`/`.tsx` → ESM (always — TS is ESM-first).
2. `.js` files: walk up to nearest `package.json`.
   - `"type": "module"` → ESM
   - `"type": "commonjs"` → CJS
   - No `type` field → CJS (Node default)
3. Fallback heuristic for ambiguous cases: AST scan for `require(`/`module.exports`/`exports.X` without `import`/`export` → CJS. Only used when there is no `package.json`.

### Module Graph (Compile Time)

Today, `Modules/` builds a graph of ESM files. Extend it so each node carries a `ModuleKind` (`Esm` | `CommonJs`). The resolver walks `require()` edges the same way it walks `import` edges, using the existing nested `node_modules` traversal.

For each `require()` call:
1. Specifier must be a literal string (else compile error: `SHARPTS_CJS001: require() specifier must be a string literal`).
2. Resolve via the existing module resolver.
3. If found: add edge to graph.
4. If not found AND inside a `try` block: still legal — emit code that throws `MODULE_NOT_FOUND` at runtime. Track as a "soft miss" so we don't error at compile time.
5. If not found and not inside a try: compile error (`SHARPTS_CJS002: cannot resolve module 'X'`).

### Interpreter Mode (Phase 1)

1. **Loader** (`Modules/CommonJsLoader.cs` — new): Reads a CJS file, wraps its AST in the synthetic function, and returns a `CommonJsModule` runtime object with `exports` field, `_initialized` flag, and a closure over the file's scope.
2. **`require` builtin** (`Runtime/BuiltIns/RequireBuiltIn.cs` — new): Implements `require(id)` in interpreter mode. Resolves the id via the module resolver, finds or creates the `CommonJsModule`, calls its init if not already running, returns its `exports`.
3. **Circular handling**: If `require(B)` happens while `B` is mid-init, return `B`'s current (possibly empty) `exports` object. Match Node's "live binding" semantics — the object reference is shared, not copied.
4. **ESM ↔ CJS bridging**: When an ESM file does `import x from 'cjs-pkg'`, the existing import machinery delegates to `require('cjs-pkg')` and uses `module.exports` as the default export. Named imports map to properties on `module.exports` (matching Node's default interop).
5. **`__dirname`, `__filename`, `process`, `Buffer`, `global`**: Bound as locals in the CJS scope. `global` is an alias for `globalThis`.
6. **`require.resolve(id)`**: Implemented as a method on the `require` callable; returns the absolute resolved path (or throws `MODULE_NOT_FOUND`).

### Compiled Mode (Phase 2)

1. **Module class emission** (`Compilation/CommonJs/CjsModuleEmitter.cs` — new):
   Each CJS source file becomes a generated .NET class:
   ```csharp
   internal static class $CjsModule_lodash_index {
       internal static object? Exports;
       internal static bool _initStarted;
       internal static bool _initialized;
       internal static object? GetExports() {
           if (_initialized) return Exports;
           if (_initStarted) return Exports; // circular: return current state
           _initStarted = true;
           Exports = new Dictionary<string, object?>();
           Init();
           _initialized = true;
           return Exports;
       }
       internal static void Init() { /* compiled file body */ }
   }
   ```
2. **Init body**: The file's statements are compiled by the existing IL emitter, with the synthetic locals (`exports`, `require`, `module`, `__filename`, `__dirname`) materialized as local IL slots seeded at the top of `Init()`. `module` is a small struct/dict with an `exports` property that aliases the static field.
3. **`require('literal')` lowering**: Replaced at compile time with a direct call to `$CjsModule_X.GetExports()`. No runtime resolution, no dictionary lookup.
4. **`require('missing-but-in-try')` lowering**: Replaced with `throw new Error("Cannot find module 'X'") { code = "MODULE_NOT_FOUND" }`. The surrounding `try` catches it.
5. **`require.resolve('literal')` lowering**: Replaced with the resolved path as a string constant. No runtime work.
6. **ESM importing CJS**: An `import x from 'lodash'` becomes a load of `$CjsModule_lodash_index.Exports` (after calling `GetExports()`). Named imports emit member loads on the exports dictionary.
7. **Standalone DLL constraint**: All emission goes through `RuntimeEmitter` helpers. No `typeof(SharpTS.X).GetMethod(...)` references in the generated code. CJS-specific helpers added to `RuntimeEmitter.CommonJs.cs` and registered in `StandaloneDllTests.LateBindingAllowlist`.
8. **Async/generator state machine emitters**: CjsModule init is plain synchronous IL — no async path needed for the wrapper itself. But `require()` calls inside async functions must work. Since `require` lowers to a static method call, all four state-machine emitters (AsyncMoveNextEmitter, AsyncArrowMoveNextEmitter, GeneratorMoveNextEmitter, AsyncGeneratorMoveNextEmitter) need their `EmitCall` to recognize the rewritten `require` form and emit the static call. Likely a tiny addition since the rewrite happens before emission.

### Type Checking

No new machinery. The flow is:

1. `import _ from 'lodash'` → resolver finds `node_modules/lodash/package.json`, reads `types` field → loads `node_modules/@types/lodash/index.d.ts` (or bundled `.d.ts`).
2. Type checker treats the default import as having the type of the `.d.ts` `export =` declaration (or the namespace if the `.d.ts` uses `declare module`).
3. At runtime, the value comes from `module.exports`. Node's interop convention is that the default import == `module.exports`, so types and runtime line up by construction.

Existing `export = ` / `import = require()` plumbing already handles this for the TS-syntax form; we just need to make it work for the runtime `require()` call form too.

### Error Codes

- `SHARPTS_CJS001`: `require()` specifier must be a string literal
- `SHARPTS_CJS002`: Cannot resolve module 'X'
- `SHARPTS_CJS003`: `eval` / `new Function` not supported in compiled mode
- `SHARPTS_CJS004`: Mixing `import`/`export` and `require`/`module.exports` in the same file
- `SHARPTS_CJS005`: `require.cache` / `require.extensions` not supported

---

## Phasing

### Phase 1 — Interpreter CJS (standalone milestone)

1. `CommonJsDetector` — extension + package.json + heuristic logic, with unit tests over fixture trees.
2. `CommonJsLoader` — wraps a parsed file's AST in the synthetic function scope; binds `exports`, `require`, `module`, `__filename`, `__dirname`, `global` as locals.
3. `RequireBuiltIn` — interpreter-mode `require(id)` and `require.resolve(id)`. Hooks into existing module resolver. Implements circular handling via `_initStarted` flag and shared exports object.
4. ESM → CJS interop in the existing import handler: when an `import` resolves to a CJS file, delegate to the CJS loader and unwrap `module.exports`.
5. Tests:
   - Unit: detector, loader, require builtin, circular requires (A→B→A), missing optional dep with try/catch.
   - Integration: `require('./local-cjs.js')` from a CJS file; `import _ from 'lodash'` from a TS file (real lodash from `node_modules`).
   - Smoke: lodash, debug, ms, semver, minimatch, uuid, yaml — each gets a tiny script that calls a typical API.
6. Update STATUS-NODE.md: mark `require()` and `module.exports` as ✅ in interpreter mode.

**Exit criteria**: All 7 target packages run in interpreter mode. Type checking works via `@types/*`. CI green.

### Phase 2 — Compiled CJS

1. `CjsModuleEmitter` — generates the per-file .NET class with `Exports`, `_initStarted`, `_initialized`, `GetExports()`, `Init()`.
2. `RequireRewriter` (compile pass) — walks the AST, replaces literal `require('X')` with a synthetic `CjsModuleRef` AST node carrying the target module class. Errors on non-literal specifiers (`SHARPTS_CJS001`).
3. IL emission for `CjsModuleRef`: direct static call to `$CjsModule_X.GetExports()`.
4. Synthetic locals seeded in each `Init()` body: `__dirname`, `__filename` as string constants; `module` as a small object with `exports` property aliasing the static field; `exports` as a local pointing at the same dict; `global` as `globalThis`.
5. Optional-dep lowering: `require('missing')` inside a `try` block compiles to a runtime `throw` with `code = "MODULE_NOT_FOUND"`.
6. `require.resolve('literal')` → string constant.
7. ESM → CJS import lowering: `import x from 'lodash'` becomes a load of `$CjsModule_lodash_index.GetExports()`.
8. Async state machine integration: verify `require()` works inside async functions/generators across all 4 state machine emitters. Add tests covering each.
9. Standalone DLL audit: add `RuntimeEmitter.CommonJs.cs` to `LateBindingAllowlist`. Run `StandaloneDllTests` to verify no SharpTS.dll references leak.
10. Tests: re-run all Phase 1 smoke tests in compiled mode. Verify each compiles to a standalone .dll and runs without SharpTS.dll present.
11. Update STATUS-NODE.md: mark `require()` and `module.exports` as ✅ in both modes.

**Exit criteria**: All Phase 1 target packages compile to standalone DLLs and run correctly. `StandaloneDllTests` passes. `eval`/`new Function` produces clear `SHARPTS_CJS003` errors with file:line.

### Phase 3 — Polish (post-merge)

- Better error messages: when rejecting a non-literal `require`, suggest the literal alternative or point at docs.
- Documentation: `docs/node-modules-api.md` gets a CommonJS section.
- README example: "Using npm packages from SharpTS".

---

## Open Questions / Future Work

1. **Context modules** (webpack-style `require(`./locales/${x}`)`) — deferred to v2 if real packages demand it.
2. **CJS requires ESM** — deferred. Node only allows this via dynamic `import()` returning a Promise.
3. **`require.main === module`** detection — deferred; trivial to add later as a per-module compile-time constant.
4. **`require.cache` mutation** — likely never; fundamentally incompatible with AOT class-per-module layout.
5. **Native addons (`.node` files)** — separate initiative; would require N-API shim layer.
6. **Optional fallback to bundled interpreter for `eval`** — explicitly deferred per architectural discussion. If revisited, would be a separate plan because it changes what "compiled mode" means.

---

## Risks

| Risk | Mitigation |
|---|---|
| Real packages use patterns we didn't anticipate | The Phase 1 smoke tests are the canary. Each target package surfaces its own edge cases before we commit to IL. |
| Circular requires diverge from Node semantics | Test fixtures for A→B→A, A→B→C→A, and self-referential modules. Compare output against `node` directly. |
| `.d.ts` types and runtime `module.exports` shape disagree | This is a Node ecosystem hazard, not a SharpTS bug. Surface clearly when it happens; document. |
| State machine emitters drift again | Add CJS-in-async tests to all 4 state machine emitter test suites at the same time as the change, per existing tech-debt audit guidance. |
| Heuristic CJS detection misclassifies a `.js` file | Detection prefers explicit signals (extension, package.json) over heuristic. Heuristic only fires when no `package.json` is reachable, which is rare in npm packages. |
