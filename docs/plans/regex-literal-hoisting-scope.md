# Scope: compiled regex-literal hoisting

## Problem

In compiled mode, every evaluation of a regex literal `/pat/flags` emits a fresh
`new $RegExp(pattern, flags)` (`ILEmitter.Expressions.cs` `EmitRegexLiteral`).
In a hot loop — `for (const line of lines) if (/^\d+/.test(line)) …` — that is a
per-iteration object allocation plus the full ctor pipeline (validate, normalize,
two pattern scans, options, cached-`Regex` lookup). V8 pays ~zero here: it caches
the compiled regex per literal *site*.

This is the residual identified after the EnumerateMatches + `RegexOptions.Compiled`
work (commits `1f1114f5`, `3c341779`) took compiled regex from 3.20× → 1.61× Node.

## Evidence

Micro-workload: a literal re-evaluated every iteration vs. the same literal hoisted
out of the loop by hand (`re.test(short)`), compiled, N = 100 000:

| | compiled | Node |
|---|---:|---:|
| literal in loop (`/…/.test(x)` per iter) | **10.27 ms** | 2.60 ms |
| literal hoisted out of loop (`re.test(x)`) | **5.01 ms** | 2.34 ms |

The ~5.3 ms delta (≈53 ns/construction × 100 000) is pure per-evaluation wrapper
cost. The wrapper-construction *cache* (`3c341779`) only recovers ~1/3 of it for
short literals — object allocation + the cache lookup still run each evaluation.
Hoisting removes all of it: each evaluation becomes a single `ldsfld`.

(Note: even hoisted, 5.01 ms > Node 2.34 ms — a separate per-`.test()` dispatch +
short-string match gap remains. Hoisting closes the *construction* gap, not that one.)

## Transform

For a hoistable literal site, allocate **one** `$RegExp` per site (a static field,
lazily initialized) and load it instead of constructing per evaluation:

```
// before (per evaluation)
ldstr pattern ; ldstr flags ; call CreateRegExpWithFlags        // new $RegExp(...)

// after (per evaluation)
ldsfld  $hoistedRegex_N
dup ; brtrue done ; pop ; ldstr pattern ; ldstr flags ;
      call CreateRegExpWithFlags ; dup ; stsfld $hoistedRegex_N   // lazy init, once
done:
```

Lazy init (vs. a `.cctor`) sidesteps static-ctor ordering and keeps cold programs
from compiling unused literals; the per-eval cost is a load + null-check (~1 ns).

## Safety analysis (the heart of it)

ECMA-262 §13.2.7.3: evaluating a regex literal creates a **new** RegExp object each
time. Sharing one instance is observable only through:

1. **`lastIndex` statefulness** — only for `g`/`y` flags, and only through
   `RegExp.prototype.test`/`exec`, which advance `lastIndex` across calls. A shared
   `/a/g` used as `.test(x)` in a loop would advance `lastIndex` between iterations
   (true,true,true,false,…) instead of resetting each time (always true). **Unsafe.**
2. **Object mutation** — `re.foo = 1`, `Object.assign(re,…)`, `Object.defineProperty(re,…)`.
3. **Identity** — `a === b` for two evaluations of the *same* site (spec: distinct).

Key implementation fact that makes the gate clean: in `SharpTSRegExp`, the
`String.prototype` consumers — `MatchAll`, `Replace`, `Search`, `Split` — operate on
the underlying `_regex` and **scan from position 0**; they never touch the instance
`LastIndex`. Only `Test`/`Exec` honor `LastIndex`. (The emitted `$RegExp` mirrors this.)

So a literal is **safe to hoist** when it appears *directly* in one of these consuming
positions (so it cannot escape to be mutated or compared — hazards 2 & 3):

| Consumer | Safe to hoist? |
|---|---|
| `str.match(/lit/)`, `str.matchAll(/lit/)` | yes — stateless scan from 0 |
| `str.replace(/lit/, …)`, `str.replaceAll(/lit/, …)` | yes — stateless |
| `str.search(/lit/)`, `str.split(/lit/)` | yes — stateless |
| `(/lit/).test(x)`, `(/lit/).exec(x)` | **only if no `g` and no `y` flag** (hazard 1) |
| anything else (assigned, returned, passed to a user fn, `re.x = …`, `re === …`) | no — may escape/mutate/compare |

This is a **syntactic, local** check (parent context of the literal node) — no
whole-program escape analysis needed. It covers the dominant real-world pattern
(`/re/.test(line)` / `str.replace(/re/g, …)` in a loop) while staying conservative.

## Integration

1. **`RegexLiteralHoistAnalyzer`** (new AST pass, mirrors `ClosureAnalyzer` /
   `PerIterationCellAnalyzer`): walk expressions; for each `Expr.RegexLiteral`, inspect
   its immediate parent. If the parent is a recognized safe consumer (table above,
   honoring the `g`/`y` rule for test/exec), record the node in a
   `Dictionary<Expr.RegexLiteral, FieldBuilder>` (one static field per site). Threaded
   through `CompilationContext` like the other analyzers.
2. **Field emission**: declare the static `$RegExp` field per hoistable site on the
   owning type (`$Program` / runtime), alongside the existing symbol/static fields.
3. **`EmitRegexLiteral`**: if `re` is in the hoist map → emit the lazy `ldsfld`/init
   sequence above; else → current `new $RegExp` path (unchanged). Single, localized
   branch — every other emit site is untouched.

Interpreter: out of scope (tree-walking masks construction cost entirely — measured
literal-loop 42 ms ≈ hoisted 39 ms). Compiled-only optimization.

## Payoff / limits

- **~2× on literal-heavy compiled loops** (down to the hoisted floor), for the common
  `.test`/`.replace`/`.match`-in-a-loop shape.
- Does **not** close the residual per-`.test()` dispatch gap vs V8 (separate work,
  lower ceiling).
- No effect on bulk one-shot regex (one construction either way) — so `regex.ts`'s
  headline N=10000 row barely moves; this targets the *loop-over-literal* pattern the
  benchmark under-weights.

## Risks & test strategy

- **Highest risk: the `g`/`y` + test/exec exclusion.** Getting it wrong silently
  changes `lastIndex` semantics. Must gate on the full **Test262** regex suite
  (lastIndex, `@@match`/`@@replace`, identity, `from-regexp-like`), not just unit tests.
- Add unit tests asserting: (a) `g`/`y` literal in `.test` loop is NOT hoisted
  (behavior matches fresh-per-eval); (b) two evaluations of a hoisted site that *does*
  escape are NOT shared; (c) `str.replace(/g/, …)` in a loop is hoisted and correct.
- `--verify` IL on the lazy-init sequence (null-check + `stsfld`).
- Standalone-DLL: the static field + `new $RegExp` are BCL/emitted-runtime only — no
  SharpTS.dll reference introduced.

## Effort & phasing

- **Phase 1** — analyzer + field plumbing + `EmitRegexLiteral` branch for the two
  highest-value, unambiguous cases: `(/lit/).test/exec` with **no** `g`/`y`, and
  `str.replace(/lit/, …)`. ~1–1.5 days incl. Test262.
- **Phase 2** — extend the consumer table to match/matchAll/search/split and `g`-flagged
  string-method args. ~0.5 day.
- **Out of scope (later)** — dataflow-based hoisting for non-escaping literals assigned
  to a `const` used only in safe positions; the per-`.test()` dispatch gap.

## Implemented

Both phases landed in one pass. Files:
- `RegexLiteralHoistAnalyzer.cs` — the syntactic, reference-keyed analyzer (test/exec
  without g/y + the six stateless string consumers).
- `EmittedRuntime.RegexHoistFields` — the per-site field map, hung off the shared
  runtime so it reaches every emission context with no `CompilationContext` threading.
- `ILCompiler.DefineHoistedRegexFields` — wired into **both** the single-file
  (`Compile`) and module (`CompileModules`) phase orchestrations, after `$Program` is
  created. (The module path is separate; missing it the first time meant imported
  programs silently didn't hoist — caught by a perf no-op.)
- `ILEmitter.EmitRegexLiteral` — the lazy `ldsfld`/init branch.

Two gotchas worth recording:
- **Reference identity is load-bearing.** `Expr.RegexLiteral` is a record (value
  equality); the field map and hoistable set use `ReferenceEqualityComparer` so an
  escaping `const r = /a/` can't value-match a hoisted `/a/.test()` and get wrongly
  shared. Verified live: in a file with both, only the `.test` node hoisted.
- **Two compile entry points.** Hoisting must be wired into `Compile` *and*
  `CompileModules`; any program with an `import` uses the latter.

Measured (compiled, N=100 000, literal `/^\w+@\w+\.\w+$/.test(...)` per iteration):
literal-loop **10.19 ms → 4.86 ms (2.1×)**, matching the hand-hoisted floor (4.76 ms).
Residual vs Node (2.53 ms) is the per-`.test()` dispatch gap, as predicted. Correctness
verified interpreter-vs-compiled across the test-matrix (g/y exclusion, escaping
mutation, identity, all six string consumers); `--verify` clean; regex/IL-verify/
standalone/closure/module suites green.
