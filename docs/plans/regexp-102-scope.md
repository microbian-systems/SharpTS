# Issue #102 — Compile-mode RegExp `Fail` bucket: scoping

Status: investigation complete; first fix landed. This document scopes the work
into clusters with effort/risk estimates.

## Progress

- **`from-regexp-like` new-path (compiled) — DONE.** ECMA-262 §22.2.4.1: a
  non-RegExp object with truthy `[Symbol.match]` (IsRegExp) supplies `source`/`flags`
  via `Get` instead of ToString→"[object Object]". Added a regexp-like branch to
  the compiled `RegExpFromArgs` (reads `pattern[Symbol.match]` via `GetIndex`;
  `source` before `flags`; honors a supplied flags arg). 4 of 6 pass
  (`from-regexp-like`, `-flag-override`, `-get-source-err`, `-get-flags-err`); the
  other 2 (`-short-circuit`, `-get-ctor-err`) need the deferred call-form identity.
  Compiled-only (interp's static `CreateRegExp` lacks interpreter access for `Get`).

- **RegExp call-form identity (S15.10.3.1) — ATTEMPTED, REVERTED (deferred).**
  `RegExp(re)` (call form, undefined flags) should return the *same* object. A
  simple `pattern is $RegExp && flags undefined` short-circuit gave +5 compiled
  but regressed 2 previously-passing tests (`call_with_regexp_not_same_constructor`,
  `call_with_regexp_match_falsy`): §22.2.4.1 only short-circuits when
  `IsRegExp(pattern)` (i.e. `pattern[Symbol.match]` is truthy) AND
  `pattern.constructor === RegExp`. Doing that correctly needs symbol-property +
  constructor reads on `$RegExp`/`SharpTSRegExp` (uncertain support; both modes
  incl. IL) for a modest +5 — not worth the risk now, so reverted to keep the
  baseline regression-free. Revisit once `$RegExp` has a proper IsRegExp brand check.

- **RegExp flags validation (both modes) — DONE.** ECMA-262 §22.2.3.3: each flag
  must be one of d/g/i/m/s/u/v/y, no duplicates, not both u and v. `NormalizeFlags`
  silently dropped invalid flags; now `ValidateFlags` (interp C# + compiled IL)
  runs first and throws SyntaxError. Catches `new RegExp("a","ii")` (dup),
  `new RegExp("","migr")` (unknown `r`), `new RegExp(/x/, {})` (object→invalid
  flags), etc. Compiled RegExp Fail **143 → 134 (+9 Pass)**.

- **`regexp-modifiers` early-error validation (both modes) — DONE.** Added an
  ES2025 modifier-group validator (`ValidateModifiers`) to both ctors:
  `SharpTSRegExp` (interp C#) and `$RegExp` (compiled IL, standalone — mirrors the
  C# single-pass logic). It scans `(?addFlags-removeFlags:…)` groups (skipping
  escapes, char classes, and the non-modifier `(?:`/`(?=`/`(?!`/`(?<…` forms) and
  throws SyntaxError on a non-i/m/s flag, a duplicate within a set, a flag in
  both sets, a second dash, or `(?-:)`. Compiled RegExp Fail **172 → 143 (+29
  Pass)**. The ~10 `u`-flag Unicode case-folding modifier tests remain (separate
  engine-semantics issue). Verified: 19 invalid → SyntaxError, 16 valid (incl.
  `(?i-:…)`, `[(?x:]`, nested) pass with no false positives, in both modes.

- **Invalid-regex → guest `SyntaxError` (both modes) — DONE.** The biggest single
  lever. `SharpTSRegExp` (interp) and the `$RegExp` ctor (compiled) caught the
  .NET `ArgumentException` and threw a generic host `Exception`, so
  `e instanceof SyntaxError` was false and `assert.throws(SyntaxError, …)` failed.
  Now both throw a guest `SharpTSSyntaxError` / `$SyntaxError`. Measured RegExp-folder
  impact: **compiled Fail 286 → 172 (+113 Pass), interp Fail 368 → 254 (+114 Pass)**.
  This only covers patterns .NET already rejects; ECMAScript-specific invalid
  forms .NET accepts (modifier early errors, some ranges, `unicode_restricted`)
  still need explicit validation (next sub-phase).

- **`RegExp.escape` (ES2025) — DONE.** Implemented in both runtimes (interp:
  `RegExpBuiltIns.EscapeString` + a `RegExp` static namespace; compiled: a
  standalone BCL-only `$RegExp.Escape` IL method + `RegExpStaticEmitter`
  exposing it as both a call and a first-class value). Result: compiled RegExp
  Fail **307 → 289** (+18 Pass); interp Pass **+15**. Only `escape/cross-realm.js`
  remains (needs unsupported `$262` realm infra). The 4 introspection tests
  (name/length/prop-desc/not-a-constructor) pass in compiled; in interp they hit
  a separate generic gap (built-in statics aren't introspectable own properties —
  `getOwnPropertyDescriptor(Object,"keys")` is also null).
- **`regexp-modifiers` feature-skip — REJECTED, but it IS a genuine both-mode
  feature gap (see cluster 1).** A first analysis used the swallow-inflated
  pre-signal-fix interp baseline and wrongly concluded interp passed 145/147.
  Re-measured against the corrected post-fix baselines, `regexp-modifiers` fails
  ~equally in both modes (interp 90 Fail / 55 Pass; compiled 87 Fail / 58 Pass)
  — an unimplemented feature (modifier early-error validation), not a
  compiled-only bug. Skip still rejected because it would convert the 55–58
  genuinely passing tests (native `(?i:…)`) to Skipped; the right fix is to
  implement the validation, which helps both modes.

- **2 compiled-only #102 bugs (`S15.10.7_A1_T1/T2`) — DONE.** (a) Calling a
  RegExp (`/x/()`, `r()`) returned null instead of throwing TypeError — added a
  targeted `$RegExp`-callee check in `InvokeValue`/`InvokeMethodValue`. (b)
  `RegExp(p, f)` without `new` returned null instead of a RegExp — routed the
  call form through `RegExpFromArgs` in `BuiltInConstructorHandler`. Compiled
  RegExp Fail **289 → 286**. Cumulative with escape: **307 → 286** (Pass +21),
  no regressions.

- **Corrected #102 framing.** Of the (pre-fix) 289 compiled RegExp Fails, **273 also fail
  in interp** — genuine spec/feature gaps shared by both modes, not compiled
  divergences. Only **16 are compiled-only** (compiled Fail, interp Pass), and
  **14 of those are Symbol.replace/Symbol.split → #101**. So #102 has just **2**
  genuine compiled-only bugs (`S15.10.7_A1_T1/T2`: calling a RegExp as a function
  must throw TypeError). The headline lesson: measure against the post-fix
  baseline — "compiled diverges from interp" is mostly an artifact of the old
  swallow bug.

## Remaining #102 work (current: compiled RegExp Fail **134**)

Compiled RegExp Fail **307 → 134** (~56%). Of the 134, **63 are `Symbol.*` (→ #101)**,
leaving **~71 in #102 scope**. The clean validation/coercion wins are done; what
remains is hard-tier, grouped by the work it actually needs:

| Cluster | Compiled Fails | Nature / blocker |
|---|---:|---|
| `prototype/exec` lastIndex | 12 | Typed-`int` lastIndex storage rework on the match **hot path** — loses object identity + ToLength-on-read `valueOf`; `u`-flag surrogate advance. Risky (perf + correctness). |
| `regexp-modifiers` u-fold (10), `dotall` (3), `CharacterClassEscapes`/class-escape (3) | ~16 | **Engine semantics** — `u`-flag Unicode case-folding and `\s`/`\d` class membership differ between .NET and ECMAScript. Likely needs a custom matcher layer. |
| `from-regexp-like` (6) + call-form identity `S15.10.3.1` (7) + `S15.10.4.1` boxed (5) | ~18 | **Need a proper `IsRegExp` brand check** on `$RegExp` (`Symbol.match`-aware) + `Get`-based `source`/`flags` coercion + `constructor===RegExp`. A cohesive §22.2.4.1 feature shared by all three. **Blockers:** symbol-keyed get on `$RegExp` (compiled) and interp's static `CreateRegExp` factory has no interpreter access for getter invocation. A simple short-circuit was tried and reverted (regressed `not_same_constructor`/`match_falsy` — the brand check is mandatory). |
| `unicode_restricted` | 8 | 8 distinct finicky Annex B `u`-mode rules (octal/identity escape, quantified assertion, …); each `.NET`-behavior-dependent. |
| `prototype/{global,ignoreCase,multiline}` A10, `test/y-fail`, misc | ~8 | Descriptor introspection on `RegExp.prototype` (`hasOwnProperty`/`verifyNotWritable`) + misc. |

**Highest-leverage next investment:** a real `IsRegExp` brand check on `$RegExp`
(Symbol.match + constructor) — unblocks ~18 tests across `from-regexp-like`,
call-form identity, and boxed-constructor cases. It's a genuine feature (symbol
property support on `$RegExp` + interp-aware factory), not a quick win.

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
| 1 | `regexp-modifiers` (ES2025 inline `(?ims-ims:…)`) | 87 compiled / 90 interp of 147 tagged | **Genuine both-mode feature gap** (missing modifier early-error validation), NOT a compiled-only bug. Positive `(?i:…)` patterns already pass (~55–58/mode via native `Regex`); the failures are `syntax-err-*`/`early-err-*` tests expecting a **SyntaxError** for invalid modifier forms (`(?i-i:)` add+remove same flag, `(?ii:)` duplicate, non-`ims` flags) which neither mode throws. Fix = implement the validation in the shared regex-construction path and raise a SyntaxError-typed error. Do NOT feature-skip (would drop the 55–58 passing tests). | MEDIUM–HIGH (spec-detailed validator + SyntaxError typing, both modes) | MEDIUM (construction path) | No |
| 2 | `escape/` — `RegExp.escape` (ES2025 static) | 19 (+1 cross-realm RuntimeError) | Unimplemented in both modes; compiled dispatch returns `null`. 20 tagged. Cannot delegate to .NET `Regex.Escape` (ES `EncodeForRegExpEscape` escapes a different, leading-char-aware set). | LOW–MEDIUM | LOW (new static) | No |
| 3 | `prototype/exec/` lastIndex | 12 | `lastIndex` stored as typed `int` → loses object identity (`r.lastIndex = obj`) and skips ToLength-on-read `valueOf` side effects; plus `u`-flag surrogate advancement. Shared interp+compile design. | MEDIUM–HIGH | MEDIUM (exec hot path — must keep typed-int fast path; add boxed slot only when a non-number is assigned) | **Yes** |
| 4 | Root Sputnik `S15.10.2.x` (NonemptyClassRanges) | 44 | Character-class range validation/semantics (`/[\d-a]/` etc.). Mostly missing SyntaxError + .NET-vs-ES class semantics. | HIGH | MEDIUM–HIGH (pattern translation changes can regress passing patterns) | No (construction) |
| 5 | Root Sputnik `S15.10.1/3/4/5/7.x` | ~46 | Pattern syntax (16), quantifiers/atoms (9), constructor (15+2), `toString`/`source` (2), property attrs (4). Heterogeneous — needs per-test triage. | HIGH (heterogeneous) | MEDIUM | Mixed |
| 6 | `unicode_restricted_*` + `unicode_full_case_folding` | 12 | Annex B `u`-mode restricted-syntax SyntaxErrors (identity escapes, quantifier-without-atom, restricted brackets) that .NET doesn't enforce. | MEDIUM (u-mode validation pass) | LOW–MEDIUM | No |
| 7 | `dotall/`, `CharacterClassEscapes/`, `character-class-escape-non-whitespace` | 6 | Engine semantics: `\s/\S/\d/\D` membership + dotAll×unicode differ between .NET and ECMAScript. | MEDIUM (custom class-escape translation) | MEDIUM | Match path |
| 8 | `from*`, `call_with_non_regexp_same_constructor`, `nullable-quantifier`, `lastIndex.js` | ~9 | Misc constructor/coercion edge cases. | LOW–MEDIUM per test (triage) | LOW | No |

## Recommended sequence (payoff vs risk)

1. **`RegExp.escape` (cluster 2) — DONE.** +18 compiled Pass, +15 interp Pass; no hot-path risk.

2. **`S15.10.7_A1_T1/T2` (the 2 genuine compiled-only #102 bugs) — DONE.** RegExp-not-callable
   → TypeError, plus `RegExp(…)` call-form → real RegExp.

3. **regexp-modifiers early-error validation (cluster 1)** — largest single cluster (~87 compiled /
   90 interp). Implement the ECMAScript modifier early errors (SyntaxError for `(?i-i:)`, `(?ii:)`,
   non-`ims` flags) in the shared regex-construction path. Benefits both modes; preserves the 55–58
   already-passing positive tests.

4. **`u`-mode + class-range validation** (clusters 4 & 6) — ECMAScript pattern validation emitting
   SyntaxError for restricted forms. Both modes. Start with the homogeneous `S15.10.2.15` cluster.

5. **lastIndex semantics** (cluster 3) — ~12 tests; the only hot-path-sensitive change. Preserve
   the typed-`int` fast path; allocate a boxed `lastIndex` slot only when a non-number is assigned,
   and apply ToLength coercion on read. Both modes.

6. **Engine-semantics** (cluster 7) and **misc triage** (clusters 5, 8) — lowest priority; per-test.

7. **Symbol.* clusters → #101** (incl. 14 of the 16 compiled-only bugs).

## Lesson: measure feature gaps against the post-signal-fix baseline

Before the signal fix, interp could not report Fail, so any "interp passes / compiled fails"
comparison was an artifact. The first `regexp-modifiers` read ("interp passes 145/147 → compiled
bug") came from that broken baseline; the corrected data shows it fails ~equally in both modes.
Always cross-reference compiled Fails against the *post-fix* interp baseline: 273 of 289 are shared
gaps (real features to implement), not compiled divergences.

## Performance note (original concern)

None of the above threatens the existing regex perf work: both runtimes already delegate matching to
native `System.Text.RegularExpressions.Regex` and share a process-lifetime compile cache keyed by
`(pattern, options)`. The one remaining native-Regex opportunity is `RegexOptions.Compiled` on the
cached engines (one-time JIT cost amortized by the cache; A/B with `SharpTS.Benchmarks/RegexBenchmarks`).
Only cluster 3 touches a hot path, and its fast-path-preserving design keeps the common
`r.exec(s)` / `r.test(s)` literal call site on typed fields.
