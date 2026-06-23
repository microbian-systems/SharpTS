# Plan: Hidden-class ("shape") representation for dynamic objects

Status: **design + de-risking spike complete; not yet started.** This is the
high-leverage representation project identified by the post-typed-array perf
session (see `MEMORY` / `project_compiled_perf_sort_json`).

## Goal

Give dynamic objects a V8-style **hidden class** ("shape" / "map") + **inline
cache** representation so that property access is a slot-index load behind a
cheap shape check instead of a `Dictionary<string,object?>` hash probe, and so
that objects sharing a structure share their layout. Targets the compiled mode
first (the competitive gap), interpreter second.

### Why (measured)

Every remaining big compiled-vs-Node gap bottoms out in the **same** cost — the
boxed `Dictionary`-per-object + boxed-`double`-per-number representation:

| Benchmark | compiled vs Node | dominated by |
|-----------|-----------------:|--------------|
| `json` parse | ~6–8× | building `Dictionary`/`List`/boxed graph |
| `json` stringify | ~5× | walking that graph |
| `binary-trees` | 6–26× | `node.left`/`node.right` dictionary lookups |
| property-heavy code | — | every `o.x` is a hash probe + box |

`#857`/`#858`/`#862` already promote **non-escaping** object literals to
compile-time value-type shape structs (`$Shape_N`), so `objectWork`
(`{x,y}; o.x+o.y`, never escapes) is already fast. This plan covers the
**escaping / dynamic** objects those optimizations cannot touch:
`JSON.parse` output, `binary-trees` nodes, objects returned from functions or
stored in collections.

## De-risking spike (results)

A standalone harness (`scratchpad/shapespike`) built 1,000,000 escaping objects
of shape `{id:number, name:string, value:number}`, stored them, then read the
numeric `value` field hot. `min` over repeats, Release, workstation GC:

### Property read — the inline-cache payoff

| representation | read 1M | vs Dictionary | vs class ceiling |
|----------------|--------:|--------------:|-----------------:|
| A `Dictionary<string,object?>` (today) | 11.8ms | 1.0× | 7.0× |
| B shape + boxed slots + monomorphic IC | 5.3ms | **2.2×** | 3.1× |
| C shape + **unboxed** numeric slots + IC | 4.5ms | **2.6×** | 2.7× |
| B-poly: 2 shapes, 1-entry IC thrashing | 7.9ms | **1.5×** | — |
| D idiomatic C# class field (ceiling) | 1.7ms | 7.0× | 1.0× |

### Construction — the JSON.parse payoff

| representation | build 1M | alloc | vs Dictionary |
|----------------|---------:|------:|--------------:|
| A `Dictionary` (today) | 170ms | 259MB | 1.0× |
| B shape, grow-per-slot (naive) | 239ms | 198MB | 0.7× (slower!) |
| **B2 shape + boxed, pre-sized** | 104ms | 130MB | **1.6×, ½ alloc** |
| **C2 shape + unboxed, pre-sized** | 80ms | 114MB | **2.1×, 44% alloc** |
| D idiomatic class (ceiling) | 27ms | 46MB | 6.3× |

### What the spike proved

1. **Inline caches deliver ~2.2× on reads** (2.6× with unboxed numeric slots).
   This validates Phase 2 — the IC is worth building.
2. **Pre-sizing the slot array is mandatory.** Growing one slot at a time (3
   `Array.Copy`s/object) makes shaped construction *slower* than `Dictionary`.
   With the shape known up front (compiled literal, or a parse that buffers a
   record then allocates the terminal shape once), construction is **1.6–2.1×
   faster and roughly halves allocation**.
3. **Shapes never regress below today's `Dictionary`.** Even a pathological
   polymorphic site where a 1-entry IC misses on *every* access is 1.5× faster
   than `Dictionary` (7.9 vs 11.8ms); megamorphic objects fall back to
   dictionary mode and merely *equal* today. **The risk is correctness, not a
   perf cliff.**
4. Shapes do **not** reach the idiomatic-class ceiling (still ~2.7–3.1× off) —
   the IC branch + array bounds check + indirection remain. They close most,
   not all, of the gap. Node doesn't reach it either; this is competitive.

## Current representation (what changes)

| Concern | Interpreter | Compiled |
|---------|-------------|----------|
| Object backing | `SharpTSObject._fields : Dictionary<string,object?>` (`Runtime/Types/SharpTSObject.cs:19`) | emitted `$Object` over a `Dictionary` (`Compilation/RuntimeEmitter.TSObject.cs`) |
| Read / write | `GetProperty`/`SetProperty` (`SharpTSObject.cs:111,147`) | `EmitGet`/`EmitSet` (`Compilation/ILEmitter.Properties.cs`) |
| Getters/setters/descriptors | side `_getters`/`_setters`/`_descriptors` dicts (`SharpTSObject.cs:21-23`) | `PropertyDescriptorStore` (PDS) |
| Symbols, prototype, frozen/sealed | `_symbolFields`, `Prototype`, `IsFrozen`/`IsSealed`/`IsExtensible` | mirrored on `$Object` |

Numbers are stored boxed (`object?` slot / `RuntimeValue`→boxed at the boundary).

## Design

### Shape (immutable, interned)

```
sealed class Shape {
    string[]                 Names;          // ordered own property names (insertion order)
    Dictionary<string,int>   SlotOf;         // name -> slot index
    Dictionary<string,Shape> Transitions;    // "add name" -> resulting shape (shared)
    // Phase 4: per-slot kind (Num|Ref) + sub-index into typed backing arrays
    static readonly Shape Root;              // empty
}
```

A transition tree means every `{id,name,value}` literal/parse, after the first,
walks cached `Add` edges and lands on the **same terminal `Shape`** — so the
shape is allocated once per distinct structure, not once per object, and an IC
keyed on shape identity (`ReferenceEquals`) is monomorphic across all of them.

### ShapedObject

```
sealed class ShapedObject {
    Shape    Shape;
    object?[] Slots;                 // Phase 1-2
    // Phase 4: double[] Nums; object?[] Refs;  (segregated typed storage)
    Dictionary<string,object?>? Overflow;  // dictionary-mode fallback (null in fast path)
}
```

Insertion-order, descriptors, getters/setters, symbols, prototype, frozen state:
property **order** comes from `Shape.Names`; the rare side concerns
(descriptors, accessors, symbols) stay in lazily-allocated side tables exactly
as today — the shape only governs the plain data slots.

### Inline caches (compiled `o.x` sites) — where the speed is

Each property site gets a per-site cache (static fields, like the regex-literal
hoisting pattern in `regex-literal-hoisting-scope.md`):

```
// o.x  -->
if (o.Shape == site.CachedShape) value = o.Slots[site.CachedSlot];   // fast
else                             value = SlowGet(o, "x", ref site);  // fill cache / deopt
```

- **Monomorphic** (1 shape): the common case; ~2.2× the spike measured.
- **Polymorphic** (cache 2–4 shapes, linear check): still beats Dictionary.
- **Megamorphic** (>N shapes): stop caching, dictionary-mode lookup ≈ today.

### Deopt to dictionary mode

`delete`, non-string/integer-index keys, runaway property counts, `Object.assign`
of disjoint shapes, etc. flip a `ShapedObject` to `Overflow` dictionary mode
(shape becomes a sentinel; ICs miss and route to the slow path). This is the
correctness safety net that guarantees "never worse than today."

## Phasing (each independently shippable, gated by test262 + TS-conformance)

| Phase | Scope | Expected | Risk |
|-------|-------|---------:|------|
| **1** | `Shape` + `ShapedObject` as the dynamic-object backing. Interpreter first (easier to validate every `Object.*` path), then compiled `$Object`. Slot-index access, **no ICs yet**, **pre-sized construction for literals**. Dictionary-mode deopt in place. | Construction 1.6× + ½ alloc; reads ~flat without ICs | **High** — central type, every property path |
| **2** | Inline caches at compiled `o.x` / `o.x=` sites (mono→poly→mega). | **Reads ~2.2×** (binary-trees, property-heavy) | Med-high |
| **3** | `JSON.parse` builds shaped objects directly (buffer record → terminal shape → pre-sized slots); object literals emit shaped objects; stringify walks slots. | json parse ~1.6×, stringify, binary-trees | Med |
| **4** *(optional)* | Segregated **unboxed numeric slots** (`double[] Nums` + `object?[] Refs`). | +read 1.18×, +construction 1.3×, less alloc | **High** (typed slot bookkeeping; deopt on type change) |

## Integration points (the reason this is multi-week, not a weekend)

Every path that reads/writes/enumerates object properties must understand shapes
**or** route through a shape-aware accessor, in **both** modes:

- `EvaluateGet`/`EvaluateSet` (interp), `EmitGet`/`EmitSet` (compiled)
- `PropertyDescriptorStore`, `Object.defineProperty`/`getOwnPropertyDescriptor`
- `Object.keys`/`values`/`entries`/`assign`/`freeze`/`seal`/`getOwnPropertyNames`
- `for-in`, spread `{...o}`, object destructuring, `delete`, `in`
- getters/setters, `Symbol`-keyed properties, the prototype chain walk
- Proxies (must NOT be shaped — they dispatch through traps)
- Reviver walk (`RuntimeEmitter.Json.ParseReviver.cs`) — already graph-based, fine

**Invariant to preserve at every step:** ECMA-262 own-property **insertion
order** (integer-index keys ascending first, then string keys in insertion
order). `Shape.Names` must encode exactly this.

## Risks & mitigations

- **Conformance regressions** — property order, descriptor flags, accessor
  semantics, freeze/seal, `in`/`hasOwnProperty`. *Mitigation:* phase behind both
  conformance suites; keep dictionary-mode as a behavior-identical fallback; land
  interpreter first (cheaper to validate, no IL) and diff against it.
- **Standalone-DLL constraint** — shapes/IC sites are emitted IL + BCL types
  only; per-site caches are static fields (proven pattern). No new SharpTS.dll
  refs.
- **Perf cliff from polymorphism** — spike shows even thrashing mono-IC beats
  Dictionary; poly-IC (2–4 entries) recovers; megamorphic equals today.
- **Phase 4 type instability** — a slot that changes number↔ref must transition
  shape (or deopt). Keep Phase 4 optional and last.

## Open questions

1. IC storage in compiled IL: per-site static `(Shape, int)` fields vs a small
   per-site struct. (Lean: two static fields, mirroring regex-literal hoisting.)
2. Poly-IC width before megamorphic (V8 uses 4). Start at 4.
3. Should the interpreter get ICs too, or only the denser representation? (Spike
   suggests interp benefits mostly from the representation; ICs are a compiled
   win. Likely: representation both modes, ICs compiled-only.)
4. JSON.parse shape-building: buffer a whole record before allocating (clean
   pre-size) vs transition-walk with end-of-object resize. (Lean: buffer.)

## Prototype 1 (interpreter) — results

A first interpreter prototype (`Runtime/Types/ShapedFieldStore.cs`) replaced the
`Dictionary<string,object?>` backing of `SharpTSObject` with an interned-shape +
slot-array store (`IReadOnlyDictionary` drop-in; delete → dictionary-mode deopt).
What it established:

- **Integration is viable.** After one fix it passes the object-semantics suite
  (1,363 object/destructure/spread/freeze/for-in tests + 84 worker tests).
- **The one real integration cost found:** `SharpTSObject(Dictionary)`'s *aliasing
  contract*. `StructuredClone.CloneObject` constructed the object with an empty
  dict and then **mutated that dict after construction**, relying on the object
  aliasing it. A shaped store copies at construction, so those writes were lost
  (clone came back empty → `cloned.nested` undefined → "Cannot set properties of
  undefined"). Fixed by writing through `SetProperty`. Only **1 of 159**
  `new SharpTSObject(...)` sites relied on this — the friction is small but real,
  and Phase 1 must audit for it.
- **Shapes are perf-NEUTRAL in the interpreter** (paired object-read bench: ~1100ms
  both). Two reasons: the tree-walk dominates per-access cost, and a shaped read
  *still* does a name→slot dictionary lookup (`ObjectShape.SlotOf`) + an array
  index — no cheaper than a direct `Dictionary` probe **without an inline cache**.

**Conclusion: the 2.2× payoff is entirely inline-cache-bound**, and ICs are a
*compiled* call-site mechanism (cache `(shape, slot)` in per-site static fields,
skip `SlotOf` on the hot path). The interpreter, which resolves by name every
access, cannot realize the win from the representation alone. So:

- The interpreter prototype **de-risked integration/correctness** (cheap, no IL).
- The standalone spike **de-risked the IC payoff** (2.2×, in C#).
- **Still unproven end-to-end: the *compiled* shaped-`$Object` + emitted IC** —
  that this hits 2.2× in real emitted IL, integrates with stack-typing, and stays
  standalone. That is the next de-risk.

## Recommendation

Phase 1 is the expensive, risky foundation; Phase 2 (ICs) is where the measured
2.2× lands — and Prototype 1 shows the representation alone does nothing without
them. **Next: prototype the *compiled* IC on a single path** (e.g. a
statically-typed escaping object's `.field`, binary-trees-style) end to end —
emit a shaped `$Object`, cache `(shape, slot)` per site, and confirm the 2.2×
holds in emitted IL before committing to the full both-modes rollout. The
interpreter `ShapedFieldStore` is perf-neutral on its own, so it should land
*together with* the compiled ICs (as one net win), not before.
