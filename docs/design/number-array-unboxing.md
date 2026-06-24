# Design: closing the `number[]` boxing gap (Option A)

## The gap (measured)
Compiled `number[]` at params/fields boxes a `double` per element WRITE → ~73–80x slower than Node for write-heavy code; ~14x for read-heavy. Promoted-LOCAL `number[]` (unboxed `List<double>`) is only ~3.5x Node. The tax is the boxing at the boundary.

## Representation landscape (confirmed in code)
- **Interpreter:** `Runtime/Types/SharpTSArray.cs` — composition over `Deque<object?>` + sparse `Dictionary`, holes, length, freeze.
- **Compiled:** emitted `$Array` (`RuntimeEmitter.TSArray.cs`) — **inherits `List<object?>`** (the base list IS the dense store). ~48 IL emitter sites do `isinst List<object?>` to mean "is an array". Must stay in sync with `SharpTSArray`.
- **Promoted locals:** raw `List<double>`/`List<bool>` — a *separate* type, only for `number[]`/`boolean[]` locals the `ArrayLocalPromotionAnalyzer` proves never escape (not passed, returned, stored, aliased, captured, or used by any method beyond push/map/filter/reduce).

## The two candidate architectures

### A. `List<double>` end-to-end (extend promotion across boundaries + coerce at the `any` boundary)
Make `number[]` compile to `List<double>` for params/fields/returns too; insert `List<double>↔$Array` conversions wherever a `number[]` meets a non-`number[]` static type.
- **Reuses** existing unboxed machinery (`SetArrayElementDouble`, `ArrayMapDouble`, the descriptor).
- **FATAL FLAW — unsound for aliased arrays.** Arrays are mutable *reference* types. A boundary coercion `List<double> → $Array` **copies** (different CLR object), so:
  ```ts
  const a: number[] = []; const b: any = a; b.push(1); a.length // must be 1
  ```
  breaks — `b` becomes a *copied* `$Array`, mutations don't reflect in `a`. The existing promoted-LOCAL path is sound *only because* the analyzer forbids exactly these escapes; params/fields are inherently aliasable, so the forbiddance can't extend. **A cannot be made sound** without whole-program alias analysis (infeasible). ❌

### B. Elements-kind on `$Array` (V8 PACKED_DOUBLE_ELEMENTS), identity-preserving
Keep ONE `$Array` object (identity preserved — aliasing works); give it an internal **unboxed double storage mode** + a deopt-to-boxed transition on any non-double write. Compiler emits unboxed `GetDouble`/`SetDouble`/`PushDouble` at statically-`number[]` sites (local/param/field/return uniformly, since they're all `$Array`).
- **Sound** — object identity preserved, so aliasing across `number[]`/`any` is correct; deopt handles unsound casts (`any → number[]`).
- **This is the only sound general design.** ✅
- **BLOCKER — the inheritance.** `$Array : List<object?>` ⇒ the element store *is* a `List<object?>`; you cannot put unboxed doubles in it. Options:
  - **B1: shadow `List<double>` field**, base `List<object?>` unused in double-mode. Requires **every** element access to funnel through mode-aware `$Array` methods — but the inheritance was chosen *specifically so* the ~48 emitter sites + built-ins can use the base `List<object?>` API **directly**. Must audit + reroute all direct base-list access. Large.
  - **B2: break the inheritance** (compose instead) — then fix all ~48 `isinst List<object?>` sites + everything using the base API. Larger.

## Verdict
- The **sound** way to close the gap generally is **B** (identity-preserving elements-kind).
- B is a **foundational, multi-week, high-regression-risk** change to the single most-used runtime type, fighting the deliberate `$Array : List<object?>` inheritance, duplicated across interp + emitted-IL (+ standalone), with deopt-correctness obligations across the entire array-mutator + built-in surface, gated by 14k tests + Test262 at every step.
- **A is unsound** (breaks array aliasing) and must be rejected for the general case.

## Phasing for B (if pursued)
1. Audit: does every `$Array` element access (the ~48 sites + all built-ins) go through `$Array` methods, or hit the base `List<object?>` API directly? (Determines B1 feasibility / size.)
2. Add double-store + mode flag + central mode-aware Get/SetCore + deopt to emitted `$Array` (and `SharpTSArray` twin); ZERO behavior change (mode never entered yet). Gate.
3. `number[]`-typed array creation enters double-mode. Gate.
4. Emit `Get/SetDouble`/`PushDouble` at `number[]` sites. Re-benchmark. Gate.
5. Extend built-ins to operate on the double store directly; `boolean[]`.

## Phase-1 audit result (2026-06-23)
- Element access does NOT funnel through `$Array` methods: `SetArrayElement(List<object?>, i, v)` and ~62 `Arrays.*` built-in helpers operate on the base `List<object?>` API directly (`list[i]`, `.Add`, `.Count`). Base API is non-virtual → not interceptable.
- BUT access is centralized through typed helpers the compiler picks by static descriptor (`SetArrayElement` vs `SetArrayElementDouble`, `EmitSetArrayElementFor`). The compiler knows `number[]` statically at the emit site → the hook.
- Design = deopt-at-compiler-boundaries, not reroute-every-site:
  - Hot ops (index get/set, push, length): ~6–8 double-aware fast-path emit sites.
  - Cold built-ins + `number[]→any/object` widening: compiler emits one-time deopt (materialize `double[]`→boxed base list, flip mode). ~55 cold built-in bodies + ~48 runtime isinst sites UNCHANGED (only see boxed arrays).
- Only the EMITTED `$Array` needs changing; interp `SharpTSArray` parity is observable-only (stays boxed). Single-representation change.
- Backing store = raw `double[] + count`, geometric growth, direct/Unsafe access (per Nick's instinct; ceiling ~1.1x Node vs List<double> ~4x). Growth reallocs `$Array._store`; object identity preserved → aliasing sound.
- Core risk = deopt-completeness (every compiler boundary to base-list consumers must deopt; bounded by type-checker coercion points; miss one = silent corruption). Gated by Test262 + suite per phase.

## Sound contained alternative (not general, but real)
Extend `ArrayLocalPromotionAnalyzer` to promote MORE *local* `number[]` (it's sound for locals — no aliasing). Today it disqualifies on `pop`/`shift`/`forEach`/`for-of`/compound-assign/most methods. Broadening these captures more local hot loops with **low risk, no representation change** — but does NOT touch the param/field 73x case.
