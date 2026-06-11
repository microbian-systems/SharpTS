# Type-AST design: replacing the string-based type pipeline

**Status:** spike (this document + vertical slice in the same PR)
**Driver:** three independent conformance blockers and a recurring bug class, all rooted in
types passing through lossy string round-trips between the parser and the checker.

## The problem today

The parser tokenizes and *structurally* parses every type annotation — then throws the
structure away, re-rendering it to a `string` (`Parser.Types.cs`, ~1,100 lines). The checker
re-parses that string with a second, hand-rolled character-scanning parser
(`TypeChecker.TypeParsing.cs`, ~1,900 lines) to produce `TypeInfo` (the semantic type model).

Costs, all observed in practice during the 2026-06-10/11 conformance push:

1. **A recurring bug class.** The checker-side string scanner re-implements bracket matching,
   union splitting, member splitting, etc. We fixed four instances of the same
   `>`-of-`=>` mis-splitting family, a curried mis-parse of two-call-signature object types,
   and an alias-whitespace bug — each a new leak in the same dam.
2. **No declaration identity.** Two `class Base { foo: string }` declarations in different
   modules are different types to tsc but identical strings to us. This caps
   `assignmentCompatWithObjectMembers{4,Optionality2,StringNumericNames}` (cross-module
   diagnostic-code fidelity) and forced the `TypeInfo.CacheKey()` structural-fingerprint
   workaround for interfaces.
3. **No substitution origin.** tsc's `isInstantiatedGenericParameter` — "did this parameter
   type come from substituting a type parameter?" — gates the always-on callback comparison
   rule. The information cannot survive a string round-trip. Blocks `covariantCallbacks`
   (#202) and alias-instantiation variance.
4. **No source spans.** Diagnostics inside type annotations attach to the statement line, not
   the offending type position.
5. **Double parsing** of every annotation, plus `ToString()`-keyed caches that make rendering
   a correctness concern (three cache-collision incidents).

## The design

Mirror tsc's Node/Type split. Two layers, with an explicit boundary:

```
source ──Lexer──> tokens ──Parser──> TypeNode  (SYNTAX: what was written, where)
                                        │
                              TypeResolver (checker)
                                        │
                                        v
                                    TypeInfo   (SEMANTICS: what it means)
```

- **`TypeNode`** (new, `Parsing/TypeNodes.cs`): immutable records describing type *syntax* —
  `NamedTypeNode("Map", [k, v])`, `UnionTypeNode([...])`, `ArrayTypeNode(elem)`,
  `LiteralTypeNode(...)`, etc. Each carries its source line. Built by the parser in the same
  pass that today builds the string — the structure already exists; we stop discarding it.
- **`TypeInfo`** (existing, unchanged role): the checker's semantic model. Resolution
  (`ToTypeInfo(TypeNode)`) happens in the checker where scopes/generics live, exactly like
  today's `ToTypeInfo(string)` — minus the string re-parse.
- **Identity and origin live on the semantic layer, fed by the syntactic one.** Once
  resolution starts from nodes, a `NamedTypeNode` resolves through the environment to a
  *declaration*, so `TypeInfo` for classes/interfaces can carry a declaration handle
  (fixing problem 2), and `Substitute` can mark results with their origin (fixing problem 3).
  These are follow-ups enabled by the node layer, not part of the first slices.

### Why two layers (and not one)?

Considered: making `TypeInfo` itself carry syntax (one layer). Rejected — `TypeInfo` is
compared, substituted, and cached by *meaning*; syntax (spans, written form) would poison
structural equality and the caches. The two-layer split is also what tsc itself does
(`TypeNode` vs `Type`), so conformance reasoning maps 1:1.

### Migration strategy: dual-path, node-first with string fallback

The same playbook as the RuntimeValue/V2 migration (incremental, finished cleanly):

1. Parser's existing type-parsing functions return `(string, TypeNode?)` — the string exactly
   as today (zero behavior change), the node when every sub-component produced one. An
   unsupported construct anywhere inside yields a null node; the consumer falls back to the
   string path. **The string is authoritative until the end of the migration.**
2. AST statement/expression records grow optional `…Node` fields next to their existing
   string annotation fields, populated when available.
3. The checker prefers `ToTypeInfo(TypeNode)` when a node is present, falling back to
   `ToTypeInfo(string)` otherwise. Node conversion reuses the existing resolution helpers
   (`ResolveGenericType`, environment lookups), so semantics are shared, not duplicated.
4. Expand node coverage construct-by-construct (each step measured against both suites),
   then site-by-site flip consumers off the string fields, then delete the string scanner.

**Safety net:** the conformance suite (63 pinned Passes, (line, TSnnnn)-exact) and the
10,650+ unit suite. Every increment must keep both identical — the slice in this PR includes
an equivalence test that converts both ways and asserts identical `TypeInfo` renderings.

### Slice shipped with this document

- `TypeNode` records: named (with type arguments), primitives/keywords, string/number/boolean
  literals, array suffix, union, parenthesized.
- Parser: `ParsePrimaryType` / union / annotation entry points build nodes opportunistically.
- `Stmt.Var.TypeAnnotationNode` populated; `VisitVar` resolves node-first.
- Instrumentation: `TypeNodeStats` counters (node-hit vs string-fallback), to size the
  remaining migration. **Measured over the 79-test conformance corpus: 37% of variable
  annotations already resolve through the node path with just this slice** (198 node / 337
  fallback); function and object types dominate the fallback set, confirming slices 1–2 as
  the high-value next steps.

### Order of subsequent slices (each independently shippable)

1. ✅ **Shipped.** Function/constructor type nodes (`(a: T) => R`, `new (...) => R`), plus
   `const` annotations wired into the node path. Coverage over the conformance corpus:
   37% → 46.4% (254 node / 293 fallback). Generic function/constructor types
   (`<T>(…) => R`) and `this`-parameter constructor types still fall back (slice 3).
   Shipping this slice immediately caught the **fifth** `>`-of-`=>` instance: the node
   path parsed `(x: (a: B) => D, y: (a2: B) => D) => R` correctly while the string path's
   `SplitFunctionParams` fused both parameters into one — previously masked because both
   sides of the comparison were equally mangled. The string path got the same arrow guard
   `SplitObjectMembers` already had.
2. ✅ **Shipped (object + tuple).** `ObjectTypeNode` (properties with optional/method
   markers, computed names in their `@@` spelling, index signatures, call/construct
   signatures) and `TupleTypeNode` (named/optional/spread elements, trailing-rest rule,
   TS1257 ordering). `ParseMethodSignature` became a node producer alongside
   `ParseFunctionTypeBody`. Coverage: 46.4% → **65.3%** (357 node / 190 fallback).
   Conditional and mapped types remain string-path — they tie into generic/alias
   resolution, so they ride with slice 3.
3. Type aliases store nodes (kills alias string substitution; enables alias-instantiation
   identity for #202's variance cases).
4. Declaration handles on `TypeInfo.Interface/Class` resolved via nodes (kills the
   `CacheKey()` workaround; fixes the cross-module diagnostic tests).
5. Substitution-origin marking (unblocks #202's callback rule → `covariantCallbacks`).
6. Delete `TypeChecker.TypeParsing.cs` string scanning; `ToTypeInfo(string)` survives only
   for the REPL/embedding API surface, implemented as parse-to-node + convert.

### Non-goals

- No change to `TypeInfo`'s public shape in this phase (compat caches, IL compiler, and the
  declaration generator all consume it).
- No parser grammar changes — nodes capture exactly what the string parser already accepts.
- The interpreter/IL compiler are untouched until slice 6 (they never see type strings today;
  types are erased before execution).
