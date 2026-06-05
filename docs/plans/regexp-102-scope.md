# Issue #102 — Compile-mode RegExp `Fail` bucket: scoping

Status: investigation complete; first fix landed. This document scopes the work
into clusters with effort/risk estimates.

## Survey: remaining work is past the clean-win frontier (2026-06-04)

After landing escape, the IsRegExp/constructor-identity family, and the generic
`flags` accessor (both modes), a full survey of the remaining ~84 #102-scope
Fail/RuntimeError shows no more clean wins — each remaining cluster needs core
infrastructure or hard engine work:

- **Descriptor introspection** (`flags/{length,name,prop-desc}`, per-flag
  `global/ignoreCase/multiline` `A8/A9/A10`): the **interp side is blocked by a
  foundational, non-RegExp gap** — interp plain objects do NOT inherit
  `Object.prototype` methods via the prototype chain (`({}).hasOwnProperty('x')`
  throws "undefined is not a function", though `Object.prototype.hasOwnProperty`
  itself exists; **compiled works**). `A8/A9` call `RegExp.prototype.hasOwnProperty`,
  so they RuntimeError. Also the interp `DefineGetter` path yields enumerable:true
  (defineProperty getters correctly yield enumerable:false), so the accessor would
  need non-enumerable exposure. Fixing `Object.prototype`-method inheritance is a
  large, high-blast-radius interpreter change well beyond #102. The compiled
  remainder (`length`/`name`/`prop-desc`, `A10`) needs per-attribute
  `verifyProperty` infrastructure (getter-fn `name`/`length` own-descriptors,
  instance `verifyNotWritable`).
- **Engine semantics** (`regexp-modifiers` 12, `CharacterClassEscapes` 8,
  `dotall`, `exec/u-*`): .NET-regex-vs-ECMAScript divergences (inline-modifier
  application to `\b`/`\w`/dotAll, `\d`/`\s`/`\w` membership, u-mode code points).
  Likely need a custom matcher/translation layer. High risk.
- **Char-class ranges** (`S15.10.2.x`): missing SyntaxErrors + class semantics;
  pattern-translation changes risk regressing currently-passing patterns.
- **Harness-blocked** (~15): `*/cross-realm.js` need `$262.createRealm()`;
  `source/value*` use `eval()` (unavailable in compiled).
- **Singletons** (`toString/called-as-function`, `S15.10.5.1_A2`,
  `test/y-fail-lastindex-no-write`): each needs its own descriptor /
  lastIndex-writability / global-`this` plumbing — low confidence, 1 test each.

Recommendation: track these as separate issues off #102 (foundational
`Object.prototype` inheritance, a RegExp engine-semantics matcher layer,
descriptor-attribute infrastructure). #102's clean-win phase is complete.

## Progress

- **Foundational: interp plain objects inherit `Object.prototype` methods — DONE.**
  The descriptor-cluster blocker from the survey above: interp ordinary objects
  didn't inherit `Object.prototype`'s methods via the prototype chain
  (`({}).hasOwnProperty('x')` threw "undefined is not a function"; compiled
  worked). Fixed by a FINAL fallback in `EvaluateGetOnRecord`/`RV` (after own
  props + the `__proto__` chain, so user overrides always win): resolve
  `SharpTSObjectPrototype.GetMember(name)` and return it bound to the receiver
  (`hasOwnProperty`/`propertyIsEnumerable`/`isPrototypeOf`/`toString`/`valueOf`).
  Added an `IsNullPrototype` flag to `SharpTSObject` (set by `Object.create(null)`
  and `Object.groupBy`) so genuine null-prototype objects correctly inherit
  nothing — without it, `Object/groupBy/null-prototype` (which asserts
  `obj.hasOwnProperty === undefined`) regressed. **+318 interp Test262 Pass, 0
  regressions** (315 in `built-ins/Object`, 3 `built-ins/Array`). Interp-only;
  `Stringify` uses `HasProperty` (own/`__proto__`), so the fallback doesn't
  affect object stringification. This unblocks the descriptor cluster's `A8/A9`
  reads (`RegExp.prototype.hasOwnProperty(...)`), though those still need the
  per-flag accessors exposed + non-enumerable + delete-of-getter to fully pass.

- **`get RegExp.prototype.flags` generic accessor (interp) — DONE.** ECMA-262
  §22.2.5.3: `flags` is a GENERIC accessor — it requires only that `this` be an
  Object (not a RegExp) and builds the flag string by reading each flag via
  `Get`+ToBoolean. Interp previously didn't expose any accessor descriptor on
  `RegExp.prototype` at all, so `Object.getOwnPropertyDescriptor(RegExp.prototype,
  "flags").get` was undefined → the `flags/coercion-*` tests crashed (RuntimeError
  in both modes). Fix: generalized `BuildFlagsString` to `object?` receiver, added
  a generic `flags` getter (`BuiltInMethod`, requires-Object then `BuildFlagsString`),
  exposed it via `proto.DefineGetter("flags", …)` in `BuildPrototype`, and taught
  `BindAccessorToObject` to bind `BuiltInMethod` getters (so direct
  `RegExp.prototype.flags` access passes the right `this`); and broadened
  `RequireObject` (the shared "Type(R) is not Object" guard) to reject every
  primitive kind (number forms, string, boolean, Symbol, BigInt), not just
  null/undefined/bool/double/string. Instance `re.flags` unchanged (separate
  path). **10 interp Fail→Pass:** the 6 `flags/coercion-*`,
  `flags/this-val-regexp-prototype`, `flags/this-val-non-obj`, plus 2 bonus
  (`Symbol.search`/`Symbol.split` `this-val-non-obj`, from the broader
  `RequireObject`). No Pass regressions. The 3 `flags/{length,name,prop-desc}`
  near-misses shifted RuntimeError→Fail — they use `verifyProperty` mutation
  testing (delete/redefine) on the getter function's `length`/`name` and the
  prototype accessor's attributes, which needs the descriptor-attribute
  infrastructure below. **Compiled `flags` generic getter — DONE.** The compiled
  `flags` accessor previously shared `EmitProtoAccessorPrologue`, which throws for
  any non-RegExp `this` — correct for `global`/`ignoreCase`/… (§22.2.5.4+, which
  DO require a RegExp/prototype `this`) but wrong for the generic `flags` getter.
  Replaced it with a dedicated `EmitProtoFlagsAccessor` (pure BCL-only IL +
  standalone `runtime.GetProperty`/`runtime.IsTruthy` helpers): primitive `this`
  → TypeError; real `$RegExp` → cached-flags fast path; any other object → build
  the flag string via `Get`+ToBoolean (so `get.call(plainObj)` works). Flips the
  6 compiled `flags/coercion-*`. The broader descriptor cluster (`global`/`ignoreCase`/
  `multiline` `S15.10.7.x_A8/A9/A10`) additionally needs accessor exposure for the
  per-flag getters plus delete-of-getter / for-in / enumerable correctness on the
  prototype object (core `SharpTSObject` semantics) — a larger, riskier follow-up.

- **§22.2.4.1 IsRegExp brand check (interp) — DONE.** Brought the interp RegExp
  constructor to parity with the compiled reference (which already passed all 18
  brand-check-family tests). Added `RegExpBuiltIns.ConstructRegExp(interp, args,
  isCallForm)` implementing §22.2.4.1 with interpreter access: IsRegExp (§22.2.7.2,
  reads `Get(pattern, @@match)` and ToBooleans it; a real regex with
  `re[@@match]=false` is NOT regexp-like), the call-form same-constructor identity
  short-circuit (`RegExp(re)` with `re.constructor===RegExp` and no flags returns
  `re` unchanged — depends on the prototype-`constructor` wiring above), and
  regexp-like `source`/`flags` extraction via `Get` (honoring user getters,
  propagating throws, `source` before `flags`, flags-arg overrides the getter).
  Wired into both forms: call form via `SharpTSBuiltInConstructor.Call` (was
  ignoring its `interpreter` param), new form via `BuiltInConstructorFactory.
  TryCreate` (already had the interpreter). Supporting fixes: (1) symbol-keyed
  storage on `SharpTSRegExp` (`SetBySymbol`/`TryGetSymbolProperty`, internal so
  the runtime↔emitted parity test is unaffected) + regex+symbol get/set dispatch
  in the interpreter (`re[Symbol.match]=false` previously threw); (2)
  `Object.getPrototypeOf(regex)` now returns the per-realm RegExp.prototype
  (was null → `Object.getPrototypeOf(/x/) === RegExp.prototype` was false for
  *all* interp regexes). No compiled changes. Targets ~14 interp Fail→Pass across
  `S15.10.3.1_A1_T1..T5`/`A3_T2`, `call_with_*`, `from-regexp-like*`. Verified all
  scenarios in both modes; no unit regressions.

- **`RegExp.prototype.constructor` wiring (interp) — DONE.** ECMA-262 §22.2.6.1:
  `RegExp.prototype.constructor === RegExp`, and by inheritance
  `(/x/).constructor === RegExp`. The compiled side already held (the `$RegExp`
  Type token backs both the `RegExp` identifier and the GetProperty
  `constructor` arm); interp returned `undefined`. Fixed interp by caching the
  process-wide RegExp constructor singleton (`Interpreter.RegExpConstructorObject`,
  read once from the static globals table), returning it from a `constructor`
  arm in `EvaluateGetOnRegExp`, and setting it on the prototype object in
  `RegExpBuiltIns.BuildPrototype`. The instance arm yields to a user-set own
  `constructor` (`re.constructor = fn`) via a cheap `TryGetProperty` probe —
  caught a regression where the bare singleton shadowed the own property and
  broke `RegExp.prototype[@@split]`'s SpeciesConstructor path (3 Symbol.split
  tests). Flips 5 interp tests Fail→Pass (`S15.10.3.1_A3_T1`, `S15.10.7_A3_T1/T2`,
  `prototype/S15.10.6.1_A1_T1/T2`), no regressions. This is the prerequisite for
  the §22.2.4.1 IsRegExp brand check (`SameValue(newTarget, Get(O,"constructor"))`)
  that unblocks the ~18-test from-regexp-like / call-form-identity family — that
  brand-check short-circuit remains the next follow-up.

- **`unicode_restricted` u/v-mode early errors (safe subset) — DONE.** ECMA-262
  Annex B distinguishes `u`/`v` mode, where several forms .NET tolerates are
  SyntaxErrors. Implemented the false-positive-free subset shared by interp and
  compiled: (1) a lookaround assertion (`(?=…)`/`(?!…)`/`(?<=…)`/`(?<!…)`)
  immediately followed by a quantifier (`* + ? {`) → SyntaxError
  (`quantifiable_assertion`); (2) `\c` not followed by an ASCII letter →
  SyntaxError (`identity_escape_c`). Interp: `ValidateUnicodePattern` +
  `FindGroupClose` in `SharpTSRegExp.cs`, gated on `_flags` containing `u`/`v`,
  called after `ValidateModifiers`. Compiled: `EmitTSRegExpValidateUnicodePattern`
  + `EmitTSRegExpFindGroupClose` (pure BCL-only IL) in `RuntimeEmitter.TSRegExp.cs`,
  gated in the ctor on `flags.Contains('u'|'v')` after `ValidateFlags`. Verified
  no false positives against valid u-mode patterns (`(?:.)*`, `(abc)*`,
  `(?<n>a)*`, `\cA`, `\d+`, `\bfoo\b`, `[\cA]`). The other Annex B u-mode rules
  (identity-escape allowlist, octal/backreference, class ranges, incomplete
  quantifiers) need lookahead that risks rejecting valid patterns — deferred.

- **`prototype/exec` lastIndex object-identity (compiled) — DONE.** ECMA-262
  §22.2.5.2.2: lastIndex is an ordinary writable data property; ToLength runs at
  exec read time (one `valueOf`), not at assignment. Compiled coerced at write
  (and didn't call `valueOf` for objects), losing identity. Added a
  `$RegExp._lastIndexBoxed` slot, `ResolveLastIndex()` (ToLength via `ToNumber`
  at Exec/Test start; non-global doesn't write back, so identity survives), and
  box-clearing on numeric/strict write-back. Only objects are boxed —
  `null`/number/string/bool fold to the typed-int fast path (so `lastIndex=null`
  ToLengths to 0, not aliased to "no box"). Numeric fast path unchanged. Fixed
  the 4 lastindex-access tests + `S15.10.6.2_A4_T10/T11/T12` + 2 `Symbol.match`
  coercion tests (+9). Remaining exec: u-mode code-point matching (3) + `A1_T6`
  captures + `A1_T17` exec(null). Compiled-only (interp Exec lacks an interpreter
  for `valueOf`).

- **`from-regexp-like` new-path (compiled) — DONE.** ECMA-262 §22.2.4.1: a
  non-RegExp object with truthy `[Symbol.match]` (IsRegExp) supplies `source`/`flags`
  via `Get` instead of ToString→"[object Object]". Added a regexp-like branch to
  the compiled `RegExpFromArgs` (reads `pattern[Symbol.match]` via `GetIndex`;
  `source` before `flags`; honors a supplied flags arg). 4 of 6 pass
  (`from-regexp-like`, `-flag-override`, `-get-source-err`, `-get-flags-err`); the
  other 2 (`-short-circuit`, `-get-ctor-err`) need the deferred call-form identity.
  Compiled-only (interp's static `CreateRegExp` lacks interpreter access for `Get`).

- **RegExp call-form identity + `constructor` wiring (compiled) — DONE.**
  Two parts: (1) `$RegExp` instance reads of `constructor` now resolve to the
  RegExp constructor (the `$RegExp` Type token, == `RegExp` as a value) — a new
  `constructor` branch in the compiled `$RegExp` GetProperty arm, after the PDS
  check so `re.constructor = x` still wins. Fixes `(/x/).constructor === RegExp`
  (was false). (2) The proper §22.2.4.1 step-1 call-form short-circuit in
  `BuiltInConstructorHandler.EmitRegExp`: `RegExp(pattern)` returns the SAME
  object iff flags is undefined, `IsRegExp(pattern)` (`pattern[Symbol.match]`
  truthy, via `GetIndex`), and `pattern.constructor === %RegExp%`. Fixes
  `S15.10.3.1`, `from-regexp-like-short-circuit`/`-get-ctor-err`, while keeping
  `call_with_regexp_{not_same_constructor,match_falsy}` copying (the earlier
  simple-short-circuit regression is resolved by the brand checks). Compiled-only
  (interp's `RegExp.prototype.constructor` is also unwired — separate follow-up).

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
call-form identity, and boxed-constructor cases. The `Symbol.match` half is now
available (`GetIndex`, used by the `from-regexp-like` new-path). The blocker is
the `constructor` half: **`(/x/).constructor === RegExp` is currently false in
both modes** (RegExp.prototype.constructor isn't wired to the RegExp constructor),
so the spec-mandatory `SameValue(newTarget, patternConstructor)` check can't pass
for real regexes — fixing that prototype-constructor wiring is the prerequisite,
and is its own sub-task (likely helps other prototype/constructor tests too).

## Precise next steps (investigated, deferred)

- **`new RegExp(null)` → source `"null"`; `new RegExp(".", null)` → SyntaxError
  (`S15.10.4.1_A4_T1/T4/A5_T6`, ~3 tests).** Spec: only `undefined` coerces to `""`;
  `null` → `ToString(null)` = `"null"`. Blocked by an absent-vs-null conflation:
  `EmitNewRegExpConstructor` case 1 and `EmitRegExp` push `Ldnull` for *absent*
  flags, indistinguishable from an explicit JS `null`. Fix = route absent
  pattern/flags as `$Undefined` (not null) at every construction site, then change
  `RegExpCoerceArg` to `null → ToJsString` (keeping `$Undefined → ""`). Touches
  ubiquitous single-arg `new RegExp("a")`, so must be done carefully + full-suite
  verified — risk disproportionate to ~3 tests for now.
- **`RegExp.prototype.<arbitrary>` inheritance (`S15.10.4.1_A7_T1`).** `re.indicator`
  should walk to `RegExp.prototype.indicator`; the `$RegExp` GetProperty arm
  doesn't fall through to the prototype for arbitrary names (only the wired
  accessors + the new `constructor` branch). Needs a general prototype-walk
  fallthrough.
- **interp `RegExp.prototype.constructor`** is still unwired (the compiled fix was
  IL-side); wiring it would let interp's call-form identity work too.

## Practical ceiling (this session)

Compiled RegExp Fail **307 → 128 (~58%)**, regression-free, across these landed
fixes (see Progress above). Every remaining cluster has now been *probed* and
found to require one of: a prerequisite fix (prototype-`constructor` wiring for
call-form identity), hard `.NET`-vs-ECMAScript **engine semantics** (Unicode
case-folding, `\s`/`\d` membership, dotAll×unicode), a **risky hot-path** rework
(`prototype/exec` lastIndex storage), interp-factory plumbing (interp `Get`-based
coercion), or many finicky per-rule validations (`unicode_restricted`). These are
feature-sized follow-ups, not clean wins — recommend tracking each as its own
issue off #102.

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
