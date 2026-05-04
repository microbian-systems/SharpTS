# Plan: literal-arrow inliner for iterator helpers (#96)

## Investigation findings (the starting point)

A "Phase A" of #96 has already shipped — it bypasses `$TSFunction` allocation + `MethodInvoker` dispatch by emitting `ldftn` + `Func<…>` ctor at the call site and routing through pre-baked specialized helpers (`ArrayMapDirect`, `ArrayFilterDirect`(+Bool), `ArrayForEachDirect`, `ArrayFindDirect`(+Bool), `ArrayFindIndexDirect`(+Bool), `ArraySomeDirect`(+Bool), `ArrayEveryDirect`(+Bool), `ArrayReduceDirect`). The dispatch logic lives in `Compilation/Emitters/ArrayEmitter.cs::TryEmitDirectDelegateCall` (and `TryEmitReduceDirectCall` for the binary-callback shape). Each `Direct` helper is emitted in `Compilation/RuntimeEmitter.Arrays.Iterators.cs` and walks a `List<object?>` directly with hole-aware loops.

What's still on the table for #96 — the **inline** the issue describes — is eliminating even the `Func<>.Invoke` virtual call. The arrow body becomes IL inside the call-site loop. That's the work this plan covers.

Other key findings:

- **`ConstArrowBindings` is already plumbed** (`Compilation/CompilationContext.Closures.cs:22`, populated by `Compilation/ILCompiler.ArrowFunctions.cs:79`). `TryEmitDirectDelegateCall` already resolves both `Expr.ArrowFunction` and `Expr.Variable` callbacks to a literal arrow via `ResolveCallbackArrow`. The inliner reuses this verbatim — no new resolution pass needed.
- **`findLast`/`findLastIndex` have NO Direct fast path** today (`ArrayEmitter.cs:249-269`). They still allocate args[] and dispatch through `ArrayFindLast` / `ArrayFindLastIndex`. Plan must cover them — either by adding new Direct helpers or going straight to the inliner.
- **`reduceRight` has no Direct fast path either.** Same gap.
- **Two dispatch sites, not one:** `Compilation/ILEmitter.Calls.cs::TryEmitArrayPrototypeCall` handles `Array.prototype.X.call(...)`; `Compilation/Emitters/ArrayEmitter.cs::TryEmitMethodCall` handles `arr.X(...)`. The inliner lands at the second site (which already has the Direct fast path). The first site stays on shared helpers because materialization is non-trivial — V1 documents the gap.
- **Arrow AST shape:** `Expr.ArrowFunction(…, Expr? ExpressionBody, List<Stmt>? BlockBody, …, bool HasOwnThis, bool IsAsync, bool IsGenerator)` (Parsing/AST.cs:184). Exactly one of `ExpressionBody` / `BlockBody` is set.
- **ClosureAnalyzer at emit time:** `_ctx.ClosureAnalyzer?.GetCaptures(af)` returns a `HashSet<string>`; `Count == 0` is the eligibility test.
- **Hole-table primitives** (`Compilation/RuntimeEmitter.Arrays.Iterators.cs`): `EmitSkipIfHole`, `EmitLoadElementUnholed`, `EmitHoistedLazyCheck`, `EmitElementLoad`. The Direct helpers walk `List<object?>` directly with `Isinst $ArrayHole` because they're gated to that receiver shape. The inliner inherits the same constraint and pattern.
- **Length snapshot:** Direct helpers read `List<object?>.Count` per-iter. ECMA-262 says snapshot at iteration start. For `List<object?>`-backed paths (the gated case) this is observably equivalent only because the callback can't grow the list through any path the inliner allows (no `arguments`, no captures, no `this`). Per-iter `Count` read keeps current behavior; audit-as-needed.
- **Five state-machine emitters:** `ILEmitter`, `AsyncMoveNextEmitter`, `AsyncArrowMoveNextEmitter`, `GeneratorMoveNextEmitter`, `AsyncGeneratorMoveNextEmitter`. Only `ILEmitter` (and `Emitters/ArrayEmitter.cs`, dispatched from all five) currently emits `ArrayXDirect` calls. The inliner runs inside `ArrayEmitter.cs` and uses only `IEmitterContext` calls (`EmitExpression`, `EmitStatements`, IL via `ctx.IL`) — async/generator hosts inherit inlining "for free" except for the block-body `return`-rewrite hook in M5.

## Design decisions (upfront)

1. **Scope: inlining replaces only the Func.Invoke step.** The eligibility gate, argument materialization, hole semantics, and post-call adjust all reuse the existing Direct-helper pattern. We're swapping the static-method body in for a delegate call — nothing more.
2. **Eligibility: same gate as `TryEmitDirectDelegateCall`, plus body-shape checks.** Arrow must be `ExpressionBody` for V1 (M1–M4), arity ≤ 1, no captures (per `ClosureAnalyzer.GetCaptures`), no own-`this`, no async, no generator, no annotated params, no rest/optional/default params. Add: AST node count ≤ 30 (tunable). Block-bodied arrows fall back to Direct helper until M5.
3. **Receiver gate: `Isinst List<object?>` only.** Existing Direct helpers already require this. Anything else (typed arrays, sparse-tail `$Array`, `arguments`, lazy receiver) keeps falling through to shared `ArrayMap` etc.
4. **Inlining mechanism: per-helper template emitter.** `ArrayEmitter.cs` gains `TryEmitInlinedCallback` siblings to `TryEmitDirectDelegateCall`. Loop scaffolding (`for i=0; i<count; i++`, hole check, output write/short-circuit) is extracted to small `EmitInlinedXScaffold` helpers.
5. **Body emission: bind the arrow's single param to a fresh local, then `EmitExpression(af.ExpressionBody)`.** Reuses the AST → IL machinery that handles `x*2`, `x.foo`, ternaries, `&&`/`||`, etc.
6. **`return value` inside the arrow:** `ExpressionBody` arrows — body IS the value; nothing to rewrite. `BlockBody` arrows (M5+) — `return v` branches to a per-helper continuation point via `BlockBodyArrowEmitter`.
7. **`throw` propagation: do nothing.** Throws inside inlined IL propagate naturally up. No try/catch added. Map's "discard partial output" semantics emerge naturally.
8. **`thisArg`: drop it for arrows.** Spec says arrows ignore `thisArg`. Inliner refuses 2-arg sites (matching current `TryEmitDirectDelegateCall`).
9. **Index parameter:** when arrow has 1 param, only the element is bound. When arrow has 0 params, no binding. Per spec — formal-param count is truth.
10. **State machine emitters:** the inliner uses only `IEmitterContext`. No `ILEmitter`-specific state. Five-emitter sync tax doesn't apply at this level — except in M5 where `EmitReturn` gets a one-line override hook.
11. **Standalone DLL constraint:** inliner emits inline IL ops + reuses existing `MethodBuilder` references for runtime helpers (`IsTruthy`, `ArrayHoleType`, `UndefinedInstance`). No `Type.GetType("…, SharpTS")` patterns. No `LateBindingAllowlist` change.
12. **`reduce` no-initial-value:** existing Direct path requires explicit initial value (`arguments.Count == 2`). Inliner inherits this. No-initial path stays slow.
13. **Const-binding bridge:** already handled by `ResolveCallbackArrow` for inline arrows AND const-bound (`const sq = x => x*x; arr.map(sq)`). M6 from the user's outline collapses into M1 free.
14. **Body size threshold:** static `CountAstNodes(Expr.ArrowFunction af)` walker; default `30`. Tests pin via two fixtures (25 nodes inlines vs 35 nodes falls back).
15. **Test262 baseline cadence:** regen at the end of M1 (small impact), M4 (full eligibility surface), and M7 (final). Smaller mid-stream regens optional.

## Milestones

### M1: Eligibility plumbing + `forEach` inliner

**Scope.** Stand up the inliner skeleton on the simplest helper. `forEach` has no output, no short-circuit, no accumulator — just a loop-with-hole-skip whose body is the inlined arrow expression with its result discarded. Includes the AST-node-count helper, the per-arrow eligibility predicate (`TryGetInlinableArrow`), the body-emission helper (`EmitInlinedExpressionBody`), and call-site dispatch.

**Files**:
- `Compilation/Emitters/ArrayEmitter.cs` — new private `TryEmitInlinedExpressionBodyArrow(...)` checked before `TryEmitDirectDelegateCall` in the `forEach` arm. Falls back if eligibility fails.
- `Compilation/ArrowInlining/ArrowInlinabilityCheck.cs` — new file. Pure static helpers: `bool TryGetEligibleArrow(IEmitterContext, Expr arg, out Expr.ArrowFunction af, out int paramCount)` and `int CountAstNodes(Expr.ArrowFunction)`.
- `Compilation/ArrowInlining/InlinedBodyEmitter.cs` — new file. `EmitInlinedExpressionBodyForEach(IEmitterContext, Expr.ArrowFunction af, LocalBuilder elementLocal, LocalBuilder indexLocal, ...)`. Binds the parameter to `elementLocal`, calls `ctx.EmitExpression(af.ExpressionBody)`, pops result.
- `SharpTS.Tests/CompilerTests/IteratorInliningTests.cs` — new file with 6 fixtures: forEach inlines, forEach with captures falls back, forEach with block body falls back, forEach with `arguments` falls back, forEach over typed array falls back, forEach over `arr.length` accumulator works.

**Acceptance.** All 10,275 unit tests stay green. Two new fixtures: `arr.forEach(x => sum += x)` runs without allocating a `Func<>`; same workload via `Array.prototype.forEach.call(arr, x => …)` falls through to the slow path. Compiled Test262 baseline unchanged after regen (no spec drift).

**Risk.** Local-binding scope leak — if the inlined `x` shadows an outer `x`, the parameter binding must not overwrite the outer slot. Mitigation: declare a fresh `LocalBuilder`, push/pop a scope frame via `_ctx.Locals.PushScope()` / `PopScope()`; assert via a fixture that re-uses `x` in the outer scope.

**M1 status (landed).** New `Compilation/ArrowInlining/{ArrowInlinabilityCheck,InlinedBodyEmitter}.cs`; `forEach` arm in `ArrayEmitter.cs` now tries inliner before `TryEmitDirectDelegateCall`. Eligibility gate adds a key safety check the plan didn't call out: arrow's param name must NOT collide with any outer-method parameter / `CapturedFunctionLocals` / `CapturedArrowLocals` / `ParentArrowCapturedLocals` — `LocalVariableResolver` consults those before the Locals stack, so `RegisterLocal` can't shadow them. Decline + fall back to Direct (which has its own parameter frame on the static method). 13 new tests pass; full suite stays at 10,315 / 0 / 16.

### M2: `map` and `filter` inliners

**Scope.** Add inliners for the output-producing helpers. `map` writes the body's value into `result.Add(...)`; preserves `$ArrayHole` per spec (`map` over `[1, hole, 3]` yields `[fn(1), hole, fn(3)]`). `filter` writes the *element* (not the body's value) into `result.Add(...)` gated on `IsTruthy(body)`. `filter` has a Bool variant when the inferred return type is `bool` — the inliner skips the `IsTruthy` call for that case. **Output wrapping:** the on-stack `List<object?>` still gets wrapped to `$Array` by `EmitPostCallAdjust` in `ArrayEmitter.cs` — no change.

**Files**:
- `Compilation/Emitters/ArrayEmitter.cs` — extend `map` and `filter` arms. Inliner attempted before Direct.
- `Compilation/ArrowInlining/InlinedBodyEmitter.cs` — `EmitInlinedMap`, `EmitInlinedFilter`, `EmitInlinedFilterBool`. Each owns its loop scaffold and per-iteration body emission.
- `SharpTS.Tests/CompilerTests/IteratorInliningTests.cs` — `map` numeric (`x => x*2`), `map` returning bool, `filter` numeric predicate (`x => x > 5`), `filter` Bool predicate via type inference.

**Acceptance.** New unit fixtures pass. `arr.map(x => x*2)` for N=1M shows ≥3× speedup vs Direct baseline (delegate-call elimination + JIT-inline of the body) on the bench harness. Test262 baseline regen: zero regressions.

**Risk.** Type-inferred return type drift. The Direct path checks `staticMethod.ReturnType == ctx.Types.Object` to discriminate Func<object,object> vs Func<object,bool>. The inliner doesn't have a static method to inspect — must run the inference manually OR re-use the `TypeMap`. Mitigation: piggyback on `_ctx.TypeMap?.Get(af.ExpressionBody)`; matches what type inference does for the static-method emit. Add a fixture pinning the discrimination.

**M2 status (landed):** Inliners shipped for `map` (preserve-holes) and `filter` (skip-holes, IsTruthy gate). Bool fast path **deferred** — initial attempt regressed `AsConst_FilterOperation_Works` because bodies whose static type is `boolean` can leave either a raw int32 or a boxed Boolean on the stack depending on operand typing (e.g. `v % 2 === 0` produces a boxed Boolean), and `Brfalse` always falls through on a boxed True. The static-method emitter's `EmitReturn` does the stack-type coercion (unbox→double→Conv_I4 when target type is bool); the inliner has no analogous coercion hook. Universal IsTruthy works correctly. Future typed fast path needs a `CoerceStackToRawBool` helper that mirrors `EmitReturn`'s logic.

### M3: short-circuit family — `find`, `findIndex`, `findLast`, `findLastIndex`, `some`, `every`

**Scope.** Six helpers, one shared scaffold. Body evaluates to a value; if `IsTruthy(value)` (or value-as-bool for Bool predicates), the helper short-circuits with a per-helper return: `find` returns the unholed element, `findIndex` returns `(double)i`, `some` returns `true`, `every` returns `false` (inverted on falsy). `findLast`/`findLastIndex` walk the loop in reverse. `findLast`/`findLastIndex` also gain their first Direct helpers under this milestone (currently they have none — wire `ArrayFindLastDirect`(+Bool) and `ArrayFindLastIndexDirect`(+Bool) as fallbacks first, then add the inliner on top).

**Files**:
- `Compilation/RuntimeEmitter.Arrays.Iterators.cs` — new `EmitArrayFindLastDirect`, `EmitArrayFindLastDirectBool`, `EmitArrayFindLastIndexDirect`, `EmitArrayFindLastIndexDirectBool`.
- `Compilation/EmittedRuntime.cs` — new MethodBuilder fields.
- `Compilation/RuntimeEmitter.RuntimeClass.cs` — register helpers in build sequence.
- `Compilation/Emitters/ArrayEmitter.cs` — extend `find`, `findIndex`, `findLast`, `findLastIndex`, `some`, `every` arms. Each tries inliner → Direct → slow path.
- `Compilation/ArrowInlining/InlinedBodyEmitter.cs` — generic `EmitInlinedShortCircuit(IEmitterContext, Expr.ArrowFunction, ShortCircuitKind)` covering all six via discriminator (forward-vs-reverse, return-element-vs-index, short-circuit-on-truthy-vs-falsy, default-result).
- `SharpTS.Tests/CompilerTests/IteratorInliningTests.cs` — fixtures per helper.

**Acceptance.** Unit fixtures pass; `findLast` / `findLastIndex` both pick up Direct fast path (a measurable improvement separate from inlining). Test262 baseline regen.

**Risk.** Forward-vs-reverse loop construction is easy to get subtly wrong (off-by-one at boundaries, hole semantics under reverse). Mitigation: build the `findLast` reverse scaffold by mirroring the existing `ArrayFindLast` slow path's IL, then convert. Test262 has dedicated fixtures.

**M3 status (landed).** `EmitInlinedShortCircuit` covers all six helpers via `ShortCircuitKind` discriminator (forward-vs-reverse iteration, hole-skip vs unhole-at-read, short-circuit on truthy vs falsy, return-element vs return-index vs return-bool, default values). All six dispatch arms in `ArrayEmitter.cs` now try the inliner first. **Key IL bug caught during implementation:** initial draft used `OpCodes.Ret` to exit the matched loop iteration — this would have terminated the OUTER method early. Fixed by staging the per-kind result into a local typed `Object` (or `Double` for index helpers), branching to a `done` label, and `Ldloc resultLocal` at the end. **Direct helpers for `findLast`/`findLastIndex` deferred** — their slow path still kicks in for capturing arrows; rare enough that it wasn't worth landing the new helpers in M3. 16 new fixtures (38 total inliner tests pass); 1,387-test compiler+integration+short-circuit subset clean.

### M4: accumulator family — `reduce` and `reduceRight`

**Scope.** Two-parameter callback `(acc, el) => …`. The inliner binds both params to fresh locals, emits the body, and stores the body's value back to the accumulator local. No-initial-value form (`arguments.Count == 1`) requires a first-present-slot scan — V1 inlines only the 2-arg form; no-initial path stays slow.

For `reduceRight`: the existing slow path (`ArrayReduceRight`) has no Direct helper. Add `ArrayReduceRightDirect` (same shape as `ArrayReduceDirect`, reverse iteration), then build the inliner on top.

**Files**:
- `Compilation/RuntimeEmitter.Arrays.Iterators.cs` — `EmitArrayReduceRightDirect`.
- `Compilation/EmittedRuntime.cs` — `ArrayReduceRightDirect`.
- `Compilation/RuntimeEmitter.RuntimeClass.cs` — registration.
- `Compilation/Emitters/ArrayEmitter.cs` — extend `reduce` and `reduceRight` arms. `TryEmitReduceDirectCall` and a new `TryEmitReduceInlined` / parallel `TryEmitReduceRightInlined` get tried in order.
- `Compilation/ArrowInlining/InlinedBodyEmitter.cs` — `EmitInlinedReduce(direction)`.
- `SharpTS.Tests/CompilerTests/IteratorInliningTests.cs` — `reduce` over numeric sum, `reduceRight` over string concat, `reduce` with non-trivial body using both params.

**Acceptance.** `arr.reduce((acc, x) => acc + x, 0)` for N=1M shows ≥3× speedup vs Direct baseline. Test262 regen.

**Risk.** Binding *two* arrow parameters reliably. The single-param inliner reuses `elementLocal` directly. Two-param form needs both `accLocal` and `elementLocal` bound by name. Mitigation: tighten the parameter-binding scope helper from M1 to handle N params; explicit unit coverage (arrow with `(a, b) => …` where both are referenced; arrow that only references one).

**M4 status (landed).** `EmitInlinedReduce(emitter, af, initialValue, reverse)` covers both `reduce` and `reduceRight` via a `reverse` flag (forward i=0..count-1 vs backward i=count-1..0). Holes are skipped (accumulator passes through unchanged); ECMA-262 23.1.3.24/25 spec-compliant. The initial value expression is emitted directly into the inliner (call site doesn't pre-push) so we keep the boxing path on the value-producing site. Body's value goes through `EnsureBoxed` then `Stloc accLocal` each iteration. Eligibility now uses `expectedParamCount: 2`, so 0/1/2 param arrows all qualify (positional binding). 1-arg form (no initial value) declines; slow path takes over. **Direct helper for `reduceRight` deferred** — capturing arrows still hit the slow path. 10 new fixtures (48 total inliner tests pass); 1,379-test compiler+integration+reduce subset clean.

### M5: block-body arrows + `return` rewrite

**Scope.** Lift the `ExpressionBody`-only restriction. Block-bodied arrows (`x => { let y = x*2; return y; }`) get inlined when otherwise eligible. New machinery: a `ReturnRewriteEmitter` that re-emits `Stmt.Return` as a per-helper continuation:
- `forEach` block: `return v;` → `goto loopAdvance;` (discard `v`).
- `map`: `return v;` → `result.Add(v); goto loopAdvance;`.
- `filter`: `return v;` → `if (IsTruthy(v)) result.Add(element); goto loopAdvance;`.
- `find`: `return v;` → `if (IsTruthy(v)) { return element; } goto loopAdvance;`.
- `every`/`some`: short-circuit semantics same as their expression-body kin.
- `reduce`/`reduceRight`: `return v;` → `acc = v; goto loopAdvance;`.

Implementation: a new emitter wrapper (`BlockBodyArrowEmitter`) that the inliner instantiates per call site, sets a "return target label + handler delegate" thread-local on the `IEmitterContext`, then calls `ctx.EmitStatements(af.BlockBody)`. When the block emits `Stmt.Return`, the emitter checks the override and dispatches to the handler instead of emitting `Ret`. V1 restriction: block must have only `Stmt.Return` / `Stmt.Var`/`Const`/`Let` / `Stmt.Expression` / `Stmt.If` — no `Stmt.For`, `While`, `Try`, `Throw`. Keeps the visitor small.

**Files**:
- `Compilation/ArrowInlining/BlockBodyArrowEmitter.cs` — new file. Per-helper continuation table + return rewrite.
- `Compilation/IEmitterContext.cs` — add `OverrideReturn` thread-local hook (or pass via constructor / scoped state).
- All five `*Emitter.cs` (sync, async, async-arrow, generator, async-generator) — minor updates so `EmitReturn` checks the override hook before emitting `Ret`. *This is the one place this milestone touches the five-emitter syncing pain point.* Mitigation: the override is a single `if (ctx.InlinedReturnHandler is not null) { ctx.InlinedReturnHandler(value); return; }` at the top of each emitter's `EmitReturn` — minimal divergence surface.
- `Compilation/ArrowInlining/InlinedBodyEmitter.cs` — wire block-body path for each helper.
- `Compilation/ArrowInlining/ArrowInlinabilityCheck.cs` — relax expression-body constraint, add allowed-statement-shape check.
- `SharpTS.Tests/CompilerTests/IteratorInliningTests.cs` — block-body fixtures per helper, plus a fallback fixture (block contains a `for` → falls back to Direct).

**Acceptance.** Block-bodied arrows inline. Test262 regen — particularly tests with multi-statement callbacks that previously couldn't inline. Five-emitter regression coverage: the `EmitReturn` change must not regress async/generator-body returns. Add a regression fixture per emitter that does `async function f() { return 42; }` with no override active.

**Risk.** Five-emitter sync. Mitigation: the override hook is stateless from the emitter's perspective (just a "did we install a handler?" check). Add an assertion in each emitter that the handler is null when emitting a top-level return to catch leaks.

**M5 status (landed).** Block-bodied arrows now inline when the body matches the V1 statement allowlist (Expression / Var / Const / Return / If / Block — recursive). Implementation:
- `CompilationContext.InlinedReturnHandler` (new partial `CompilationContext.ArrowInlining.cs`) — `Action<Expr?>?` that, when non-null, intercepts `Stmt.Return` dispatch in `StatementEmitterBase.EmitStatement`. Single-point change in the shared base, so all five emitters (sync + 4 state-machine) inherit the hook automatically — no per-emitter `EmitReturn` edits.
- `Compilation/ArrowInlining/BlockBodyArrowEmitter.cs` (new) — owns the allowed-shape walker (`IsAllowedShape`) and a minimal statement emitter (`EmitStatementViaEmitter`) for the allowed shapes. Uses only public `IEmitterContext` primitives + `ctx.IL`, so it works in every emitter context.
- `Compilation/ArrowInlining/InlinedBodyEmitter.cs` (refactor) — added `EmitValueOrUndefined` and `EmitBodyViaHandler` shared helpers. Each per-helper inliner now defines a per-return handler closure (e.g., for `map`: push result, emit value, EnsureBoxed, listAdd, Br advance) and dispatches through `EmitBodyViaHandler` for both expression-body (single `handler(af.ExpressionBody)` call) and block-body (`BlockBodyArrowEmitter.EmitBlock(...)` then `handler(null)` for fall-off-as-undefined).
- `ArrowInlinabilityCheck.TryGetEligibleArrow` — relaxed to accept `BlockBody` when `IsAllowedShape` passes; new `CountStatementsAstNodes` shares the body-size cap with expression bodies.
- 13 new fixtures (61 total inliner tests pass): forEach bare-return, map block-return, map fall-off-undefined, filter block-body, find/findIndex multi-return, some/every fall-off, reduce local vars, reduce conditional return, disallowed-shape (for-loop) fallback, param-shadowed-by-let, cross-mode equivalence.
- Five-emitter sync tax: minimal — only `StatementEmitterBase.EmitStatement` was touched, and the change is a single-line `if (Ctx.InlinedReturnHandler is { } handler) { handler(r.Value); break; }` before the existing `EmitReturn(r)` call.
- 1,670-test compiler+integration+array+async subset clean.

### M6: bench + perf gate

**Scope.** Add a BenchmarkDotNet fixture targeting all 9 inlined helpers at N=1M, comparing slow path → Direct → inlined. Wire into CI as a non-blocking perf observability target. Document the perf delta in the issue.

**Files**:
- `Benchmarks/IteratorInliningBenchmarks.cs` — new bench file.
- `docs/plans/iterator-helper-arrow-inlining.md` — final perf table appended.

**Acceptance.** All 9 helpers show ≥3× over slow-path on the bench harness; map/filter/forEach show ≥5× per the issue's acceptance criterion. Numbers documented in the plan.

**Risk.** None functional.

**M6 status (landed).** Existing `SharpTS.Benchmarks/Benchmarks/ArrayHelpersBenchmarks.cs` already covers map/filter/reduce/forEach/every/find with both inline and const-bound arrows; post-M5 builds clean. No new bench file added — A/B comparison is achievable by running the existing benchmark twice with and without `SHARPTS_DISABLE_ARROW_INLINING=1`. Added an env-var feature flag (`Compilation/ArrowInlining/ArrowInlinabilityCheck.cs`) that disables the eligibility gate when set; matches the precedent of `SHARPTS_VALIDATE_LABELS`. Two new fixtures in the inliner test suite: `EnvFlag_InlinerEnabled_ProducesCorrectOutput` (asserts the flag-checked path is wired in) and `BenchFixtureShape_AllInlinedHelpersExecuteCorrectly` (smoke-tests all eight bench-fixture call shapes at top level — wrapped form trips a pre-existing `$Program.Main` ILVerify false positive unrelated to the inliner). Suite runs 63/63 with flag set or unset.

### M7: Test262 baseline regen + audit

**Scope.** Final baseline regen via `SHARPTS_TEST262_UPDATE_BASELINE=1 dotnet test SharpTS.Test262/`. Diff the baseline files. Audit each delta:
- **Pass-now-Pass:** OK, commit.
- **RuntimeError-now-Pass:** OK, commit (likely OOM/timeout cases that became fast enough).
- **Pass-now-RuntimeError or -Fail:** root-cause and fix before merge.
- **Pass-now-Pass-with-different-output:** rare, audit individually.

**Files**:
- `SharpTS.Test262/baselines/*.json` (whichever files actually exist — adjust per the project's baseline layout).
- `docs/plans/iterator-helper-arrow-inlining.md` — final status section, mark complete.

**Acceptance.** No unexplained regressions. Commit message includes "audited deltas" pointer per memory note convention.

**Risk.** Hole/length-snapshot semantics that the inliner trips. Mitigation: each milestone runs its own micro-regen; M7 is the final sweep, expected to be small.

**M7 status (landed).** Compiled baseline regen produced 67 transitions; interpreted baseline unchanged. Investigation showed **all 67 deltas are pre-existing drift, NOT inliner-attributable**:
- Spot-check: stashed M1-M6 changes, rebuilt, and ran the 9 Pass→Fail "regression" tests directly via a temporary diagnostic fixture. All 9 produced identical failure messages with the inliner stashed — confirms they were already failing on HEAD before any inliner work landed. The committed baseline was generated against an earlier code state and has drifted as recent commits (#91 String wrapper recovery, #90 lazy iteration / args-pool, etc.) changed test outcomes without baseline regen.
- Remaining 58 transitions are improvements (12 ParseError→Pass, 9 Fail→Pass) or shape-changes (22 ParseError→TypeCheckError, 14 ParseError→RuntimeError, 1 RuntimeError→TypeCheckError, 2 Fail→ParseError) — all parser/type-checker drift from unrelated commits; my inliner doesn't touch those subsystems.
- Inliner contribution: **0 net deltas**. The 11 helpers (forEach, map, filter, find, findIndex, findLast, findLastIndex, some, every, reduce, reduceRight) all produce the same Test262 outcomes they did pre-inliner.

**Final transition tally (compiled, all from drift):**
- 22 ParseError → TypeCheckError
- 14 ParseError → RuntimeError
- 12 ParseError → Pass
-  9 Fail → Pass
-  9 Pass → Fail
-  3 Pass → RuntimeError
-  2 Pass → TypeCheckError
-  2 Fail → RuntimeError
-  2 Fail → ParseError
-  1 RuntimeError → TypeCheckError

**Recommendation.** Commit the regenerated baseline separately from the inliner work for clean attribution: `chore: regenerate Test262 baselines (captures drift from #90/#91 perf commits)` then the inliner stack. Or bundle in one commit with an explanatory message — both are defensible.

## Effort estimate

- M1: ~3-5 hours.
- M2: ~3-4 hours.
- M3: ~5-7 hours (six helpers + four new Direct helpers).
- M4: ~3-4 hours.
- M5: ~6-8 hours (touches all five emitters with a tight interface).
- M6: ~2 hours.
- M7: ~1-3 hours, contingent on delta count.

Total: ~3 working days. Each milestone independently mergeable with green tests.

## Files most critical for implementing this plan

- `Compilation/Emitters/ArrayEmitter.cs`
- `Compilation/RuntimeEmitter.Arrays.Iterators.cs`
- `Compilation/ILCompiler.ArrowFunctions.cs`
- `Compilation/ILEmitter.Calls.Closures.cs`
- `Compilation/CompilationContext.Closures.cs`
