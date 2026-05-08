# Runtime tree-shaking — Phase 5: per-method shaking inside `$Runtime`

## Status

Phases 1–4e gated entire emitted *types* ($Buffer, $RegExp, $TextEncoder, …)
behind feature flags. Cumulative effect: DLL ~387KB → ~159.5KB (-59%),
71 → 69 emitted types.

`$Runtime` is the largest remaining contributor: a single always-emitted
type with **~1500 methods/fields** across helper families. Many of those
methods are dead code for any given test (a script that doesn't use
`JSON.parse` doesn't need `JsonParse` + `JsonStringify` + `JsonStringifyFull`,
which are large IL bodies).

## Goal

Skip emitting individual `$Runtime` methods when the program doesn't need
them. Reuse the existing feature flags (`UsesJSON`, `UsesRegExp`, `UsesDate`,
…) — no new flags unless a method has no existing home.

Target: another **20–40KB** of DLL reduction (rough estimate from JSON +
RegExp + String*RegExp method bodies alone).

## Design decisions (locked in)

| Decision | Choice |
|----------|--------|
| Strategy | **Group-based gates** — map existing UsesX flags → list of methods |
| Detector bias | **Conservative** (over-emit on uncertainty, false negatives = TypeLoadException) |
| Multi-feature methods | **Static method→features map** — `JsonStringify: {UsesJSON, UsesHttp, UsesUtilFormat}` |
| Cadence | **One commit per group, batched per PR** |
| Validation | **Test suite + per-method sync test** in `RuntimeTypeSyncTests` |
| First slice | **JSON family** (3 methods: `JsonParse`, `JsonParseWithReviver`, `JsonStringify`, `JsonStringifyFull`) |

## Implementation pattern

Two changes per group:

### 1. Gate the `Emit*` call site

Each method is emitted by a private `Emit{Name}` helper called from
`EmitRuntimeClass` (or a partial-class equivalent). Wrap the call:

```csharp
// BEFORE
EmitJsonParse(typeBuilder, runtime);
EmitJsonStringify(typeBuilder, runtime);
EmitJsonStringifyFull(typeBuilder, runtime);

// AFTER
if (_features.UsesJSON || _features.UsesHttp || _features.UsesUtilFormat)
{
    EmitJsonParse(typeBuilder, runtime);
    EmitJsonStringify(typeBuilder, runtime);
    EmitJsonStringifyFull(typeBuilder, runtime);
}
```

The condition is the **set of features that consume this method group**.
For methods belonging to one feature only, the check is simple
(`_features.UsesRegExp`).

### 2. Guard call sites that reference the now-conditional method

When `runtime.JsonParse` is null because we skipped it, any IL that does
`il.Emit(OpCodes.Call, runtime.JsonParse)` will NRE at compile time. Two
options per call site:

- **Skip the call site** when the consuming feature is off. Example:
  inside `EmitHttpModuleMethods`, the JSON serialization path is only
  emitted if `_features.UsesJSON || _features.UsesHttp` (which is already
  the case since `EmitHttpModuleMethods` itself is gated on `UsesHttp`).
- **Add a stub** that throws — only useful if the call site is reachable
  but the feature flag is meant to *gate* it (rare).

For the JSON case, all call sites are already inside type-level gates that
imply `UsesJSON` (HTTP gate already pulls in `UsesJSON` via the
implication chain), so **no call-site guards needed** for Phase 5a.

### 3. Dependency cascade (when needed)

If method A calls method B, and B is in a different group than A, gate A
on (A's group OR B's group) — emit B whenever A is emitted. Captured as a
**static method→features map** in a new file:

```csharp
// Compilation/RuntimeMethodDependencies.cs
internal static class RuntimeMethodDependencies
{
    // Methods that can be skipped when none of their consumers are present.
    public static readonly Dictionary<string, RuntimeFeatureSet.Predicate> MethodGates = new()
    {
        ["JsonParse"]          = f => f.UsesJSON || f.UsesHttp || f.UsesUtilFormat,
        ["JsonStringify"]      = f => f.UsesJSON || f.UsesHttp || f.UsesUtilFormat,
        ["JsonStringifyFull"]  = f => f.UsesJSON || f.UsesHttp || f.UsesUtilFormat,
        ["JsonParseWithReviver"] = f => f.UsesJSON,
        // ...
    };
}
```

This file becomes the **single source of truth** for which features need
which methods. Reviewers can audit one file instead of grepping
emit-site-by-emit-site.

## Phase 5a: JSON family (first slice)

**Methods to gate** (4 total):
- `JsonParse` — `JSON.parse(s)`
- `JsonParseWithReviver` — `JSON.parse(s, reviver)`
- `JsonStringify` — `JSON.stringify(v)` (1-arg)
- `JsonStringifyFull` — `JSON.stringify(v, replacer, indent)` (3-arg)

**Files affected:**
- `Compilation/RuntimeEmitter.Json.Parse.cs`
- `Compilation/RuntimeEmitter.Json.ParseReviver.cs`
- `Compilation/RuntimeEmitter.Json.Stringify.cs`
- `Compilation/RuntimeEmitter.Json.StringifyFull.cs`
- `Compilation/RuntimeEmitter.RuntimeClass.cs` — gate the four `Emit*` calls
- `Compilation/RuntimeMethodDependencies.cs` — **new file**, holds method→features map
- `SharpTS.Tests/Compilation/RuntimeTypeSyncTests.cs` — add per-method assertion

**Consumers of JSON methods:**
- Direct: `JSON.parse` / `JSON.stringify` calls — gated by `UsesJSON`
- HTTP: request/response body serialization — gated by `UsesHttp`
- util.format: `%j` formatter — gated by `UsesUtilFormat`

`UsesHttp ⇒ UsesJSON` is **already** in the detector implication chain,
so the gate could be `_features.UsesJSON` alone. Keeping the `|| UsesHttp ||
UsesUtilFormat` form makes the intent explicit and survives future
implication-chain edits.

**Expected size win:** JSON parse/stringify are some of the largest IL
bodies in `$Runtime` (cyclic recursive helpers + escape handling). Rough
estimate: **5–10KB** off DLL when JSON is unused.

**Steps:**
1. Add `RuntimeMethodDependencies.cs` with the 4 JSON gates.
2. Wrap the `EmitJson*` calls in `RuntimeEmitter.RuntimeClass.cs` with the
   feature predicate lookup.
3. Run perf-probe with a JSON-using script and a non-JSON script to confirm
   the size delta only appears in the no-JSON case.
4. Run full xUnit suite — must stay at 10310/10320 (same baseline).
5. Add `RuntimeTypeSyncTests` assertion: when `EmitEverything()` is set,
   `$Runtime.JsonParse` exists. (Catches accidental dropping during
   refactor.)
6. Commit.

## Roadmap (Phase 5b–5e)

Priority order based on (1) IL body size, (2) detection clarity, (3)
isolation from other groups:

### Phase 5b: RegExp family (~10 methods)
**Methods:** `RegExpExec`, `RegExpTest`, `RegExpCoerceArg`, `RegExpGetFlags`,
`RegExpGetGlobal`, `RegExpGetIgnoreCase`, `RegExpGetLastIndex`,
`RegExpGetMultiline`, `RegExpGetSource`, `RegExpSetLastIndex`,
`RegExpToString`.

**Gate:** `_features.UsesRegExp`.

**Note:** Phase 4e already added the String-method → UsesRegExp implication
(`.split/.replace/.match/...` flips UsesRegExp). So `String*RegExp`
helpers (gated below) work even on string-only programs that use
`'foo'.split('-')`.

**Risk:** Some `RegExp*` methods are called by `String*RegExp` methods
(StringSplitRegExp internally checks if the separator is a RegExp). Need
to verify the dependency direction.

### Phase 5c: String*RegExp helpers (~5 methods)
**Methods:** `StringSplitRegExp`, `StringReplaceRegExp`, `StringMatchRegExp`,
`StringMatchAllRegExp`, `StringSearchRegExp`.

**Gate:** `_features.UsesRegExp`.

**Note:** These wrap `String.prototype.split/replace/match/matchAll/search`.
Already de facto gated since the only callers are inside `StringEmitter`,
which is itself unconditionally emitted. So this gate is the actual
shaker — saves the IL bodies when the program never calls those methods.

### Phase 5d: Date helpers (~varies)
**Methods:** Date getters, formatters, comparison helpers.
**Gate:** `_features.UsesDate`.

### Phase 5e: util.format / inspect family (~10+ methods)
**Methods:** `UtilFormat`, `UtilInspect`, `UtilInspectValue`, `UtilInspectArray`,
`UtilInspectObject`, `UtilDeepEqualImpl`, `UtilIsDeepStrictEqual`,
`UtilParseArgs` + helpers.
**Gate:** `_features.UsesUtilFormat`.

**Note:** Some of these are referenced by `console.log`/`console.error`
formatting paths. Need to verify console paths don't depend on UtilInspect.

### Phase 5f: util.types helpers (~10 methods)
**Methods:** `UtilTypesIsTypedArray`, `UtilTypesIsArrayBuffer`, `UtilTypesIs*`.
**Gate:** Mostly `_features.UsesUtilFormat` (they live in the util module),
but several are referenced by general dispatch (e.g. structured clone).
Audit needed.

### Phase 5g+: Long tail
Per-method gates beyond these will hit diminishing returns fast. Audit
after Phase 5e to decide whether to continue or call it done.

## Validation strategy

### Per-method sync test (new)

Extend `RuntimeTypeSyncTests` so that, with `EmitEverything()`, the
emitted `$Runtime` type contains every method that any feature flag
needs. The test reads `RuntimeMethodDependencies.MethodGates`, asserts
each named method is present:

```csharp
[Fact]
public void EmittedRuntime_HasAllGatedMethods()
{
    var runtime = _fixture.CompiledAssembly.GetType("$Runtime");
    var actualMethods = runtime.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Select(m => m.Name).ToHashSet();
    var expectedMethods = RuntimeMethodDependencies.MethodGates.Keys;
    var missing = expectedMethods.Where(m => !actualMethods.Contains(m)).ToList();
    Assert.Empty(missing);
}
```

This catches the failure mode where a refactor accidentally removes a
method from emission even in the everything-emit path.

### Test suite

Full xUnit suite must remain at the same pass count (10310/10320) before
and after each phase. Any net-negative regression blocks the phase.

### Perf-probe

Bench `/tmp/bench-perf-probe` records DLL size + per-test time. Run
before and after each phase, document the delta in the commit message.

## Risks / known unknowns

1. **Reflection-emitted IL** — `runtime.JsonParse` etc. are referenced as
   `MethodBuilder` tokens. If a method is conditionally emitted but a
   call site references it unconditionally, JIT-time NRE. **Mitigation:**
   The conservative-bias rule keeps gates at the predicate level — any
   plausible consumer keeps the method alive.

2. **Cross-method IL references** — `JsonStringifyFull` calls
   `JsonStringify` internally? If so, we can't gate `JsonStringify`
   independently. **Action:** before each phase, audit cross-method calls
   inside the targeted family.

3. **Indirect dispatch via `GetFieldsProperty`** — properties like
   `JSON.parse` route through `$Runtime.GetFieldsProperty` reflection in
   some paths. If `JsonParse` doesn't exist on the type when we're
   computing reflection signatures, the property lookup might crash.
   **Action:** Phase 5a verifies that `JSON.X` access still works in
   programs that *do* use JSON (positive case), and that no NRE happens
   in programs that don't (negative case).

4. **Standalone DLL constraint** — none of the gates can touch the
   late-binding allowlist or the SharpTS-dll-free invariant. (Gates only
   change which methods are emitted, not how they're called.)

5. **Test coverage gaps** — if no test exercises `JSON.parse` with a
   reviver, gating `JsonParseWithReviver` could regress production
   without test failure. **Mitigation:** add a one-line test per method
   group when adding the gate, asserting the feature works in a
   no-JSON-elsewhere program.

## Open questions to revisit

- Is there value in also gating **fields** (like `JsonSingletonField`) or
  is the win all in method bodies?
- Should `Phase 3g (UsesIteratorHelpers)` — deferred as low-ROI for type
  shaking — be picked up at the per-method level here?
- After Phase 5e, do we have enough data to call the original plan done,
  or does the audit suggest a Phase 6 (e.g., per-method shaking inside
  $TSArray/$TSObject prototype-population helpers)?
