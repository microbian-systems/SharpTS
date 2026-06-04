# Issue #102 — Compile-mode RegExp `Fail` bucket: scoping

Status: investigation complete; first fix landed. This document scopes the work
into clusters with effort/risk estimates.

## Progress

- **`RegExp.escape` (ES2025) — DONE.** Implemented in both runtimes (interp:
  `RegExpBuiltIns.EscapeString` + a `RegExp` static namespace; compiled: a
  standalone BCL-only `$RegExp.Escape` IL method + `RegExpStaticEmitter`
  exposing it as both a call and a first-class value). Result: compiled RegExp
  Fail **307 → 289** (+18 Pass); interp Pass **+15**. Only `escape/cross-realm.js`
  remains (needs unsupported `$262` realm infra). The 4 introspection tests
  (name/length/prop-desc/not-a-constructor) pass in compiled; in interp they hit
  a separate generic gap (built-in statics aren't introspectable own properties —
  `getOwnPropertyDescriptor(Object,"keys")` is also null).
- **`regexp-modifiers` feature-skip — REJECTED (see cluster 1).** Data showed it
  would discard far more passing tests than it cleans up.

## What changed during investigation

1. **Interp Test262 signal fix (applied).** `Execution/Interpreter.cs` `Interpret()`
   swallowed top-level guest `throw`s (printed `"Runtime Error: …"` and returned),
   so the Test262 interp runner — which bucketed on a *propagated* exception — scored
   every thrown assertion (`Test262Error`) and runtime `TypeError` as **Pass**. Interp
   structurally could not report `Fail`. Fixed by capturing the swallowed throw
   (`Interpreter.LastUncaughtError`) and routing it through the existing
   `ClassifyExecutionException` in `Test262Runner`. The committed `interpreted.txt`
   was regenerated; across the subset **4,237** falsely-Pass tests moved to Fail/RuntimeError
   (Pass 7405→3168, Fail 177→3820, RuntimeError 2781→3375).

2. **#102 premise overturned.** Authoritative HEAD numbers, RegExp folder (1879 tests):

   | Bucket | Interp (post-fix) | Compiled |
   |---|---:|---:|
   | Pass | 498 | 660 |
   | Fail | 370 | 307 |
   | RuntimeError | 152 | 53 |
   | Skipped | 859 | 859 |

   Interp now fails *more* RegExp tests than compiled. "Make compile match interp" is
   not the right bar — the ECMAScript spec is, with the **compiled baseline as the more
   complete reference**. The committed `compiled.txt` is also stale vs HEAD and should be
   regenerated (separate ~20 min batched run).

## Cluster breakdown of the 307 compiled `Fail`s (non-Symbol = #102 scope)

Symbol.* clusters (≈62: Symbol.replace 21, Symbol.split 16, Symbol.matchAll 15,
Symbol.match 5, Symbol.search 1, Symbol.species 4) belong to **#101** — excluded below.

| # | Cluster | Compiled Fails | Nature | Effort | Risk | Hot-path? |
|---|---|---:|---|---|---|---|
| 1 | `regexp-modifiers` (ES2025 inline `(?ims-ims:…)`) | 87 of 147 tagged | **Compiled-mode bug, NOT a missing feature.** Interp passes **145/147** (native .NET `Regex` handles inline modifiers); compiled fails 87 (Fail) / passes 58. The compiled `$RegExp` construction/dispatch diverges from interp for modifier patterns. **Do NOT feature-skip** — skipping discards 145 passing interp + 58 passing compiled tests. Fix compiled to match interp instead. | MEDIUM (find the compiled divergence) | MEDIUM (touches $RegExp construction) | No |
| 2 | `escape/` — `RegExp.escape` (ES2025 static) | 19 (+1 cross-realm RuntimeError) | Unimplemented in both modes; compiled dispatch returns `null`. 20 tagged. Cannot delegate to .NET `Regex.Escape` (ES `EncodeForRegExpEscape` escapes a different, leading-char-aware set). | LOW–MEDIUM | LOW (new static) | No |
| 3 | `prototype/exec/` lastIndex | 12 | `lastIndex` stored as typed `int` → loses object identity (`r.lastIndex = obj`) and skips ToLength-on-read `valueOf` side effects; plus `u`-flag surrogate advancement. Shared interp+compile design. | MEDIUM–HIGH | MEDIUM (exec hot path — must keep typed-int fast path; add boxed slot only when a non-number is assigned) | **Yes** |
| 4 | Root Sputnik `S15.10.2.x` (NonemptyClassRanges) | 44 | Character-class range validation/semantics (`/[\d-a]/` etc.). Mostly missing SyntaxError + .NET-vs-ES class semantics. | HIGH | MEDIUM–HIGH (pattern translation changes can regress passing patterns) | No (construction) |
| 5 | Root Sputnik `S15.10.1/3/4/5/7.x` | ~46 | Pattern syntax (16), quantifiers/atoms (9), constructor (15+2), `toString`/`source` (2), property attrs (4). Heterogeneous — needs per-test triage. | HIGH (heterogeneous) | MEDIUM | Mixed |
| 6 | `unicode_restricted_*` + `unicode_full_case_folding` | 12 | Annex B `u`-mode restricted-syntax SyntaxErrors (identity escapes, quantifier-without-atom, restricted brackets) that .NET doesn't enforce. | MEDIUM (u-mode validation pass) | LOW–MEDIUM | No |
| 7 | `dotall/`, `CharacterClassEscapes/`, `character-class-escape-non-whitespace` | 6 | Engine semantics: `\s/\S/\d/\D` membership + dotAll×unicode differ between .NET and ECMAScript. | MEDIUM (custom class-escape translation) | MEDIUM | Match path |
| 8 | `from*`, `call_with_non_regexp_same_constructor`, `nullable-quantifier`, `lastIndex.js` | ~9 | Misc constructor/coercion edge cases. | LOW–MEDIUM per test (triage) | LOW | No |

## Recommended sequence (payoff vs risk)

1. **`RegExp.escape` (cluster 2) — DONE.** +18 compiled Pass, +15 interp Pass; no hot-path risk.

2. **Compiled regexp-modifiers bug (cluster 1)** — highest remaining payoff: ~87 compiled Fails
   where interp already passes 145/147. Diff the compiled `$RegExp` construction/dispatch against
   interp's native-`Regex` path for `(?i:…)`-style patterns. Do NOT feature-skip.

3. **lastIndex semantics** (cluster 3) — ~12 tests; the only hot-path-sensitive change. Preserve
   the typed-`int` fast path; only allocate a boxed `lastIndex` slot when a non-number is assigned,
   and apply ToLength coercion on read. Benefits both runtimes.

4. **`u`-mode + class-range validation** (clusters 4 & 6) — add ECMAScript pattern validation that
   emits SyntaxError for restricted forms. Start with the homogeneous `S15.10.2.15` range cluster.

5. **Engine-semantics** (cluster 7) and **misc triage** (clusters 5, 8) — lowest priority; per-test.

6. **Defer Symbol.* (clusters under #101).** Both committed baselines were regenerated at HEAD with
   the signal fix + escape, so they are consistent.

## Lesson: don't feature-skip a category the interp already passes

Feature-skip is honest only when the feature is genuinely unimplemented in *both* modes. For
`regexp-modifiers`, interp passes 145/147 via native `Regex`, so the failures are a compiled-mode
divergence. Always check the per-tag Pass/Fail split in *both* modes before adding a skip — a skip
that converts Pass→Skipped is a coverage regression, not a cleanup.

## Performance note (original concern)

None of the above threatens the existing regex perf work: both runtimes already delegate matching to
native `System.Text.RegularExpressions.Regex` and share a process-lifetime compile cache keyed by
`(pattern, options)`. The one remaining native-Regex opportunity is `RegexOptions.Compiled` on the
cached engines (one-time JIT cost amortized by the cache; A/B with `SharpTS.Benchmarks/RegexBenchmarks`).
Only cluster 3 touches a hot path, and its fast-path-preserving design keeps the common
`r.exec(s)` / `r.test(s)` literal call site on typed fields.
