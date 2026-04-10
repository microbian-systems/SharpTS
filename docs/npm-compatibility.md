# npm Package Compatibility

Tracks SharpTS compatibility with real-world npm packages. Tests live in `SharpTS.Tests/IntegrationTests/RealPackageSmokeTests.cs`.

Run: `dotnet test --filter "Category=npm"`

## Results

| Package | Version | Interpreter | Compiled | Blocker |
|---------|---------|:-----------:|:--------:|---------|
| ms | 2.1.3 | **Pass** | Fail | `String()` not callable in emitted IL |
| uuid | 9.0.1 | Fail | Fail | Type checker rejects forward `var` refs in `Object.defineProperty` getters |
| debug | 4.3.4 | Fail | Fail | Missing built-in module: `tty` |
| semver | 7.6.0 | Fail | Fail | Needs ASI (automatic semicolon insertion) |
| minimatch | 9.0.4 | Fail | Fail | Needs ASI |
| yaml | 2.4.1 | Fail | Fail | `Error` class not available as global variable (needed for `extends Error`) |
| lodash | 4.17.21 | Fail | Fail | Needs ASI |

## Gaps Discovered

### Critical: Automatic Semicolon Insertion (ASI)

**Affects:** semver, minimatch, lodash (and most npm packages)

Most npm packages omit semicolons, relying on JavaScript's ASI rules. SharpTS's parser currently requires explicit semicolons. This is the single largest blocker for npm ecosystem compatibility.

### `Error` as a Global Class Variable

**Affects:** yaml (and any package that uses `class X extends Error`)

`new Error("msg")` works (via `BuiltInConstructorFactory`), but `Error` is not available as a runtime variable. Packages that do `class MyError extends Error` fail because `extends` resolves `Error` as a variable expression.

### `String()` / `Number()` / `Boolean()` Not Callable in Compiled Mode

**Affects:** ms compiled mode (and any package using these conversion functions)

These are now callable in interpreter mode (fixed in this PR), but the IL compiler doesn't yet emit code that can call them as functions.

### Type Checker Strictness with Forward `var` References

**Affects:** uuid

The uuid package uses `Object.defineProperty(exports, "NIL", { get: function() { return _nil.default; } })` where `_nil` is defined later via `var`. The type checker rejects this forward reference even though the getter is lazy.

### Missing Built-in Module: `tty`

**Affects:** debug

The `debug` package requires `tty` to detect terminal color support. The `tty` module is not implemented.

## Fixes Applied in This PR

1. **Contextual keywords as identifiers** — `type`, `module`, `undefined`, etc. can now be used as variable/parameter names (was blocking `ms`)
2. **Lenient parameter binding** — missing function arguments default to `undefined` instead of throwing (JavaScript semantics)
3. **`String()`, `Number()`, `Boolean()` as callable globals** — interpreter mode now supports these conversion functions
4. **Package exports condition ordering** — changed from `["types", "import", "default"]` to `["node", "require", "import", "default"]` to match Node.js runtime behavior (was resolving `.d.ts` files instead of `.js`)
5. **Lenient property access on built-in types** — accessing non-existent properties returns `undefined` instead of throwing (JavaScript semantics)
