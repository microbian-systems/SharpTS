# SharpTS Implementation Status

This document tracks TypeScript language features and their implementation status in SharpTS.

**Last Updated:** 2026-02-16 (Added `Intl.NumberFormat` with locale-aware number/currency/percent formatting; Added missing STATUS entries for URL/URLSearchParams, TextEncoder/TextDecoder, console methods, Uint8ClampedArray, String.raw; Added Node.js built-in modules section)

## Legend
- ✅ Implemented
- ❌ Missing
- ⚠️ Partially Implemented

---

## 1. TYPE SYSTEM

| Feature | Status | Notes |
|---------|--------|-------|
| Primitive types (`string`, `number`, `boolean`, `null`) | ✅ | |
| `void` type | ✅ | |
| `any` type | ✅ | |
| Array types (`T[]`) | ✅ | |
| Object types | ✅ | Structural typing |
| Interfaces | ✅ | Structural typing |
| Classes | ✅ | Nominal typing |
| Generics (`<T>`) | ✅ | Full support with true .NET generics and constraints |
| Variance annotations (`in`, `out`, `in out`) | ✅ | Explicit variance control for generic type parameters (TS 4.7+) |
| Union Types (`string \| number`) | ✅ | With type narrowing support |
| Intersection Types (`A & B`) | ✅ | For combining types with full TypeScript semantics |
| Literal Types (`"success" \| "error"`) | ✅ | String, number, and boolean literals |
| Type Aliases (`type Name = ...`) | ✅ | Including function types |
| Tuple Types (`[string, number]`) | ✅ | Fixed-length typed arrays with optional, rest, and named elements |
| `unknown` type | ✅ | Safer alternative to `any` |
| `never` type | ✅ | For exhaustive checking |
| Type Assertions (`as`, `<Type>`) | ✅ | Both `as` and angle-bracket syntax |
| `as const` assertions | ✅ | Deep readonly inference for literals |
| Type Guards (`is`, `typeof` narrowing) | ✅ | `typeof` narrowing, user-defined type guards (`x is T`), assertion functions, property access narrowing |
| `readonly` modifier | ✅ | Compile-time enforcement |
| Optional Properties (`prop?:`) | ✅ | Partial object shapes |
| Index Signatures (`[key: string]: T`) | ✅ | String, number, and symbol key types |
| `object` type | ✅ | Non-primitive type (excludes string, number, boolean, bigint, symbol, null, undefined) |
| `unique symbol` type | ✅ | Nominally-typed symbols for const declarations |
| Type predicates (`is`, `asserts`) | ✅ | User-defined type guards (`x is T`), assertion functions (`asserts x is T`, `asserts x`) |
| `satisfies` operator | ✅ | Validates expression matches type without widening (TS 4.9+) |
| Variadic tuple types | ✅ | `[...T]` spread in tuples, Prepend/Append/Concat patterns |
| Definite assignment assertion | ✅ | `let x!: number` syntax for variables and class fields |

---

## 2. ENUMS

| Feature | Status | Notes |
|---------|--------|-------|
| Numeric Enums | ✅ | `enum Color { Red, Green }` with auto-increment |
| String Enums | ✅ | `enum Color { Red = "RED" }` |
| Const Enums | ✅ | Compile-time inlined enums with computed value support |
| Heterogeneous Enums | ✅ | Mixed string/number values |

---

## 3. CLASSES

| Feature | Status | Notes |
|---------|--------|-------|
| Basic classes | ✅ | Constructors, methods, fields |
| Inheritance (`extends`) | ✅ | Single inheritance |
| `super` calls | ✅ | |
| `this` keyword | ✅ | |
| Access modifiers (`public`/`private`/`protected`) | ✅ | Compile-time enforcement |
| `static` members | ✅ | Class-level properties/methods |
| `abstract` classes | ✅ | Cannot be instantiated |
| `abstract` methods | ✅ | Must be overridden, includes abstract accessors |
| Getters/Setters (`get`/`set`) | ✅ | Property accessors |
| Parameter properties | ✅ | `constructor(public x: number)` |
| `implements` keyword | ✅ | Class implementing interface |
| Method overloading | ✅ | Multiple signatures with implementation function |
| `override` keyword | ✅ | Explicit override marker for methods/accessors |
| Private fields (`#field`) | ✅ | ES2022 hard private fields with ConditionalWeakTable isolation; full interpreter and IL compiler support |
| Static blocks | ✅ | `static { }` for static initialization; executes in declaration order with static fields; `this` binds to class |
| `accessor` keyword | ✅ | Auto-accessor class fields (TS 4.9+); full interpreter and IL compiler support with deferred boxing optimization |
| `declare` field modifier | ✅ | Ambient field declarations for external initialization (decorators, DI); full interpreter and IL compiler support |

---

## 4. FUNCTIONS

| Feature | Status | Notes |
|---------|--------|-------|
| Function declarations | ✅ | |
| Arrow functions | ✅ | |
| Closures | ✅ | Variable capture works |
| Default parameters | ✅ | `(x = 5)` |
| Type annotations | ✅ | Parameters and return types |
| Rest parameters (`...args`) | ✅ | Variable arguments |
| Spread in calls (`fn(...arr)`) | ✅ | Array expansion |
| Overloads | ✅ | Multiple signatures with implementation function |
| `this` parameter typing | ✅ | Explicit `this` type in function declarations |
| Generic functions | ✅ | `function identity<T>(x: T)` with type inference |
| Named function expressions | ✅ | `const f = function myFunc() {}` with self-reference for recursion |
| Constructor signatures | ✅ | `new (params): T` in interfaces, `new` on expressions |
| Call signatures | ✅ | `(params): T` in interfaces, callable interface types |

---

## 5. ASYNC/PROMISES

| Feature | Status | Notes |
|---------|--------|-------|
| Promises | ✅ | `Promise<T>` type with await support, `new Promise((resolve, reject) => { })` executor constructor |
| Promise instance methods | ✅ | `.then()`, `.catch()`, `.finally()` with chaining support |
| `async` functions | ✅ | Full state machine compilation |
| `await` keyword | ✅ | Pause and resume via .NET Task infrastructure |
| Async arrow functions | ✅ | Including nested async arrows |
| Async class methods | ✅ | Full `this` capture support |
| Try/catch in async | ✅ | Await inside try/catch/finally blocks |
| Nested await in args | ✅ | `await fn(await getValue())` |
| `Promise.all/race/any/allSettled` | ✅ | Full interpreter support; IL compiler: all/race/allSettled as pure IL state machines, any delegates to runtime |
| `Promise.resolve/reject` | ✅ | Static factory methods with Promise flattening |
| `Promise.withResolvers` | ✅ | Returns `{promise, resolve, reject}` for external promise resolution (ES2024) |

---

## 6. MODULES

| Feature | Status | Notes |
|---------|--------|-------|
| `import` statements | ✅ | `import { x } from './file'` |
| `export` statements | ✅ | `export function/class/const` |
| Default exports | ✅ | `export default` |
| Namespace imports | ✅ | `import * as X from './file'` |
| Re-exports | ✅ | `export { x } from './file'`, `export * from './file'` |
| TypeScript namespaces | ✅ | `namespace X { }` with declaration merging, dotted syntax, functions, variables, enums, nested namespaces, classes with `new Namespace.Class()` instantiation |
| Namespace import alias | ✅ | `import X = Namespace.Member`, `export import X = Namespace.Member` |
| Dynamic imports | ✅ | `await import('./file')` with module registry for compiled mode, `typeof import()` typing for literal paths |
| `import type` | ✅ | Statement-level (`import type { T }`) and inline (`import { type T }`) type-only imports |
| `import.meta` | ✅ | `import.meta.url` for module metadata |
| `export =` / `import =` | ✅ | CommonJS interop: `export = value`, `import x = require('path')`, `export import x = require()` (class exports have known limitation) |
| Ambient module declarations | ✅ | `declare module 'x' { }` - type-only declarations for external packages |
| Module augmentation | ✅ | `declare module './path' { }` extends existing modules, `declare global { }` extends global types |
| Triple-slash references | ✅ | `/// <reference path="...">` for script-style file merging |

---

## 7. OPERATORS

| Feature | Status | Notes |
|---------|--------|-------|
| Arithmetic (`+`, `-`, `*`, `/`, `%`) | ✅ | |
| Comparison (`==`, `!=`, `<`, `>`, `<=`, `>=`) | ✅ | |
| Logical (`&&`, `\|\|`, `!`) | ✅ | Short-circuit evaluation |
| Nullish coalescing (`??`) | ✅ | |
| Optional chaining (`?.`) | ✅ | |
| Ternary (`? :`) | ✅ | |
| `typeof` | ✅ | Returns `"undefined"` for undeclared variables (no ReferenceError) |
| Assignment (`=`, `+=`, `-=`, `*=`, `/=`, `%=`) | ✅ | |
| Increment/Decrement (`++`, `--`) | ✅ | Pre and post |
| Bitwise (`&`, `\|`, `^`, `~`, `<<`, `>>`, `>>>`) | ✅ | Including compound assignments |
| Strict equality (`===`, `!==`) | ✅ | Same behavior as `==`/`!=` |
| `instanceof` | ✅ | With inheritance chain support |
| `in` operator | ✅ | Property existence check |
| Exponentiation (`**`) | ✅ | Right-associative |
| Spread operator (`...`) | ✅ | In arrays/objects/calls |
| Non-null assertion (`x!`) | ✅ | Postfix operator to assert non-null |
| Logical assignment (`&&=`, `\|\|=`, `??=`) | ✅ | Compound logical assignment operators with short-circuit evaluation |
| `keyof` operator | ✅ | Extract keys as union type |
| `typeof` in type position | ✅ | Extract type from value |
| Comma operator (`,`) | ✅ | Sequence expression: evaluates left-to-right, returns last value |

---

## 8. DESTRUCTURING

| Feature | Status | Notes |
|---------|--------|-------|
| Array destructuring | ✅ | `const [a, b] = arr` |
| Object destructuring | ✅ | `const { x, y } = obj` |
| Nested destructuring | ✅ | Deep pattern matching |
| Default values in destructuring | ✅ | `const { x = 5 } = obj` (via nullish coalescing) |
| Array rest pattern | ✅ | `const [first, ...rest] = arr` |
| Object rest pattern | ✅ | `const { x, ...rest } = obj` |
| Array holes | ✅ | `const [a, , c] = arr` |
| Object rename | ✅ | `const { x: newName } = obj` |
| Parameter destructuring | ✅ | `function f({ x, y })` and `([a, b]) => ...` |

---

## 9. CONTROL FLOW

| Feature | Status | Notes |
|---------|--------|-------|
| `if`/`else` | ✅ | |
| `while` loops | ✅ | |
| `for` loops | ✅ | Desugared to while |
| `for...of` loops | ✅ | Array iteration |
| `switch`/`case` | ✅ | With fall-through |
| `break` | ✅ | |
| `continue` | ✅ | |
| `return` | ✅ | |
| `try`/`catch`/`finally` | ✅ | |
| `throw` | ✅ | |
| `for...in` loops | ✅ | Object key iteration |
| `do...while` loops | ✅ | Post-condition loop |
| Label statements | ✅ | `label: for (...)` with break/continue support |
| Optional catch binding | ✅ | `catch { }` without parameter (ES2019) |

---

## 10. BUILT-IN APIS

| Feature | Status | Notes |
|---------|--------|-------|
| `console.log` | ✅ | Multiple arguments, printf-style format specifiers (%s, %d, %i, %f, %o, %O, %j, %%) |
| `Math` object | ✅ | PI, E, abs, floor, ceil, round, sqrt, sin, cos, tan, log, exp, sign, trunc, pow, min, max, random |
| String methods | ✅ | length, charAt, substring, indexOf, toUpperCase, toLowerCase, trim, replace, split, includes, startsWith, endsWith, slice, repeat, padStart, padEnd, charCodeAt, codePointAt, concat, lastIndexOf, trimStart, trimEnd, replaceAll, at, matchAll |
| Array methods | ✅ | push, pop, shift, unshift, reverse, slice, concat, map, filter, forEach, find, findIndex, findLast, findLastIndex, some, every, reduce, reduceRight, includes, indexOf, join, sort, toSorted, toReversed, with, flat, flatMap, splice, toSpliced, at, fill, copyWithin, entries, keys, values |
| `JSON.parse`/`stringify` | ✅ | With reviver, replacer, indentation, class instances, toJSON(), BigInt TypeError |
| `Object.keys`/`values`/`entries`/`fromEntries`/`hasOwn` | ✅ | Full support for object literals and class instances |
| `Array.isArray` | ✅ | Type guard for array detection |
| `Number` methods | ✅ | parseInt, parseFloat, isNaN, isFinite, isInteger, isSafeInteger, toFixed, toPrecision, toExponential, toString(radix); constants: MAX_VALUE, MIN_VALUE, NaN, POSITIVE_INFINITY, NEGATIVE_INFINITY, MAX_SAFE_INTEGER, MIN_SAFE_INTEGER, EPSILON |
| `Date` object | ✅ | Full local timezone support with constructors, getters, setters, conversion methods |
| `Map`/`Set` | ✅ | Full API (get, set, has, delete, clear, size, keys, values, entries, forEach); for...of iteration; reference equality for object keys; `Map.groupBy()` (ES2024); ES2025 Set operations (union, intersection, difference, symmetricDifference, isSubsetOf, isSupersetOf, isDisjointFrom) |
| `WeakMap`/`WeakSet` | ✅ | Full API (get, set, has, delete for WeakMap; add, has, delete for WeakSet); object-only keys/values; no iteration or size |
| `RegExp` | ✅ | Full API (test, exec, source, flags, global, ignoreCase, multiline, lastIndex); `/pattern/flags` literal and `new RegExp()` constructor; string methods (match, replace, search, split, matchAll) with regex support; named capture groups (`(?<name>...)`) with `.groups` property on match results |
| `Array.from()` | ✅ | Create array from iterable with optional map function |
| `Array.of()` | ✅ | Create array from arguments |
| `Object.assign()` | ✅ | Merge objects - copies properties from one or more source objects to a target object, returns the target |
| `Object.fromEntries()` | ✅ | Inverse of `Object.entries()` - converts iterable of [key, value] pairs to object |
| `Object.hasOwn()` | ✅ | Safer `hasOwnProperty` check - returns true for own properties, false for methods |
| `Object.freeze()`/`seal()`/`isFrozen()`/`isSealed()` | ✅ | Object immutability - freeze prevents all changes, seal allows modification but prevents adding/removing properties; shallow freeze/seal (nested objects unaffected); works on objects, arrays, class instances |
| `Error` class | ✅ | Error, TypeError, RangeError, ReferenceError, SyntaxError, URIError, EvalError, AggregateError with name, message, stack, cause (ES2022) properties |
| Strict mode (`"use strict"`) | ✅ | File-level and function-level strict mode; frozen/sealed object mutations throw TypeError in strict mode |
| `setTimeout`/`clearTimeout` | ✅ | Timer functions with Timeout handle, ref/unref support |
| `setInterval`/`clearInterval` | ✅ | Repeating timer functions with Timeout handle, no overlap between executions |
| `setImmediate`/`clearImmediate` | ✅ | Immediate execution timer (runs after current event loop iteration) |
| `globalThis` | ✅ | ES2020 global object reference with property access and method calls |
| `structuredClone` | ✅ | Deep clone of values (objects, arrays, Map, Set, etc.) |
| `AbortController`/`AbortSignal` | ✅ | `new AbortController()`, `signal.aborted`, `abort(reason?)`, `addEventListener`/`removeEventListener`, `throwIfAborted`, `onabort`, `AbortSignal.abort()`/`timeout()`/`any()`, fetch `signal` option |
| `fetch()` API | ✅ | `fetch(url, options?)` returns `Promise<Response>`; Response: `status`, `statusText`, `ok`, `url`, `headers`, `bodyUsed`, `json()`, `text()`, `arrayBuffer()`, `clone()`; Headers: `get()`, `set()`, `has()`, `delete()`, `append()`, `forEach()`, `entries()`, `keys()`, `values()`; options: `method`, `headers`, `body`, `signal` |
| `URL`/`URLSearchParams` | ✅ | Full WHATWG URL API: constructor, `href`, `protocol`, `host`, `hostname`, `port`, `pathname`, `search`, `hash`, `origin`, `username`, `password`, `searchParams`, `toString()`; URLSearchParams: `get()`, `set()`, `has()`, `delete()`, `append()`, `entries()`, `keys()`, `values()`, `forEach()`, `toString()`, `sort()` |
| `TextEncoder`/`TextDecoder` | ✅ | `TextEncoder.encode(string)` → `Uint8Array`; `TextDecoder.decode(buffer)` → `string`; UTF-8 encoding/decoding |
| `console` methods | ✅ | `log`, `error`, `warn`, `info`, `debug`, `clear`, `time`/`timeEnd`/`timeLog`, `assert`, `count`/`countReset`, `table`, `dir`, `group`/`groupCollapsed`/`groupEnd`, `trace` |
| `Intl.NumberFormat` | ✅ | Locale-aware number/currency/percent formatting; `format()`, `resolvedOptions()`; options: style, currency, minimumFractionDigits, maximumFractionDigits, minimumIntegerDigits, useGrouping |
| `Intl.DateTimeFormat` | ✅ | Locale-aware date/time formatting; `format()`, `resolvedOptions()`; options: dateStyle, timeStyle, year, month, day, weekday, hour, minute, second, hour12, timeZone, timeZoneName, era, fractionalSecondDigits |

---

## 11. SYNTAX

| Feature | Status | Notes |
|---------|--------|-------|
| Line comments (`//`) | ✅ | |
| Double-quoted strings | ✅ | |
| Template literals | ✅ | With interpolation |
| Object literals | ✅ | |
| Array literals | ✅ | |
| Block comments (`/* */`) | ✅ | |
| Single-quoted strings | ✅ | |
| Object method shorthand | ✅ | `{ fn() {} }` |
| Object literal accessors | ✅ | `{ get x() {}, set x(v) {} }` with proper `this` binding |
| Computed property names | ✅ | `{ [expr]: value }`, `{ "key": v }`, `{ 123: v }` |
| Class expressions | ✅ | `const C = class { }` - interpreter and IL compiler full support |
| Shorthand properties | ✅ | `{ x }` instead of `{ x: x }` |
| Tagged template literals | ✅ | `` tag`template` `` syntax with TemplateStringsArray and raw property |
| Numeric separators | ✅ | `1_000_000` for readability |

---

## 12. ADVANCED FEATURES

| Feature | Status | Notes |
|---------|--------|-------|
| Decorators (`@decorator`) | ✅ | Legacy & TC39 Stage 3, class/method/property/parameter decorators, Reflect API, `@Namespace` for .NET namespaces |
| Generators (`function*`) | ✅ | `yield`, `yield*`, `.next()`, `.return()`, `.throw()`; for...of integration |
| Async Generators (`async function*`) | ✅ | `yield`, `yield*`, `.next()`, `.return()`, `.throw()`; `for await...of`; full IL compiler support |
| Well-known Symbols | ✅ | `Symbol.iterator`, `Symbol.asyncIterator`, `Symbol.toStringTag`, `Symbol.hasInstance`, `Symbol.isConcatSpreadable`, `Symbol.toPrimitive`, `Symbol.species`, `Symbol.unscopables`, `Symbol.dispose`, `Symbol.asyncDispose` |
| Iterator Protocol | ✅ | Custom iterables via `[Symbol.iterator]()` method (interpreter and compiler) |
| Iterator Helpers (ES2025) | ✅ | `.map()`, `.filter()`, `.take()`, `.drop()`, `.flatMap()`, `.reduce()`, `.toArray()`, `.forEach()`, `.some()`, `.every()`, `.find()`, `.next()` protocol; lazy evaluation; chaining; works on arrays, generators, Map/Set iterators |
| Async Iterator Protocol | ✅ | Custom async iterables via `[Symbol.asyncIterator]()` method |
| `for await...of` | ✅ | Async iteration over async iterators and generators |
| `Symbol.for`/`Symbol.keyFor` | ✅ | Global symbol registry |
| Symbols | ✅ | Unique identifiers via `Symbol()` constructor |
| `bigint` type | ✅ | Arbitrary precision integers with full operation support |
| Mapped types | ✅ | `{ [K in keyof T]: ... }`, `keyof`, indexed access `T[K]`, modifiers (+/-readonly, +/-?) |
| Conditional types | ✅ | `T extends U ? X : Y`, `infer` keyword, distribution over unions |
| Template literal types | ✅ | `` `prefix${string}` ``, union expansion, pattern matching, `infer` support |
| Utility types | ✅ | `Partial<T>`, `Required<T>`, `Readonly<T>`, `Record<K, V>`, `Pick<T, K>`, `Omit<T, K>`, `Uppercase<S>`, `Lowercase<S>`, `Capitalize<S>`, `Uncapitalize<S>` |
| Additional utility types | ✅ | `ReturnType<T>`, `Parameters<T>`, `ConstructorParameters<T>`, `InstanceType<T>`, `ThisType<T>`, `Awaited<T>`, `NonNullable<T>`, `Extract<T, U>`, `Exclude<T, U>` |
| `using`/`await using` | ✅ | Explicit resource management (TS 5.2+); `Symbol.dispose`/`Symbol.asyncDispose`; automatic disposal at scope exit; SuppressedError for disposal errors |
| Const type parameters | ✅ | `<const T>` syntax (TS 5.0+) for preserving literal types during inference; readonly semantics for objects/arrays |
| Variance annotations | ✅ | `in`/`out`/`in out` modifiers on type parameters (TS 4.7+) |
| Recursive type aliases | ✅ | Self-referential type definitions like `type Node = { next: Node | null }` and generic `type Tree<T> = { children: Tree<T>[] }` |

---

## 13. BINARY DATA & THREADING

| Feature | Status | Notes |
|---------|--------|-------|
| **TypedArrays** | | |
| `Int8Array` | ✅ | Signed 8-bit integer array |
| `Uint8Array` | ✅ | Unsigned 8-bit integer array |
| `Int16Array` | ✅ | Signed 16-bit integer array |
| `Uint16Array` | ✅ | Unsigned 16-bit integer array |
| `Int32Array` | ✅ | Signed 32-bit integer array |
| `Uint32Array` | ✅ | Unsigned 32-bit integer array |
| `Float32Array` | ✅ | 32-bit float array |
| `Float64Array` | ✅ | 64-bit float array |
| `BigInt64Array` | ✅ | Signed 64-bit BigInt array |
| `BigUint64Array` | ✅ | Unsigned 64-bit BigInt array |
| `Uint8ClampedArray` | ✅ | Clamped unsigned 8-bit integer array |
| **Shared Memory** | | |
| `SharedArrayBuffer` | ✅ | Shared memory for worker threads |
| `Atomics` | ✅ | load, store, add, sub, and, or, xor, exchange, compareExchange, wait, notify |
| `ArrayBuffer` | ✅ | Non-shared binary buffer: constructor, byteLength, slice(), isView() |
| **Not Implemented** | | |
| `DataView` | ✅ | Full API: constructor, properties (buffer, byteLength, byteOffset), getter/setter methods with endianness support |

---

## 14. NOT IMPLEMENTED

This section documents JavaScript/TypeScript features that are **not currently implemented**.

### Objects & Types

| Feature | Status | Notes |
|---------|--------|-------|
| `Proxy` | ✅ | `new Proxy(target, handler)`, `Proxy.revocable()`, traps: get/set/has/deleteProperty/apply/construct |
| `WeakRef` | ✅ | `new WeakRef(target)`, `.deref()` |
| `FinalizationRegistry` | ❌ | No GC finalization callbacks |
| `Intl.NumberFormat` | ✅ | See Section 10 |
| `Intl.DateTimeFormat` | ✅ | See Section 10 |

### Global Functions

| Feature | Status | Notes |
|---------|--------|-------|
| `eval()` | ❌ | No dynamic code evaluation |
| `Function` constructor | ❌ | Cannot create functions from strings |
| `queueMicrotask()` | ✅ | Schedules microtask for execution |

### Object Static Methods

| Feature | Status | Notes |
|---------|--------|-------|
| `Object.create()` | ✅ | Creates new object with prototype; supports propertiesObject argument for defining properties via descriptors |
| `Object.is()` | ✅ | Same-value comparison; handles NaN and +0/-0 edge cases |
| `Object.getOwnPropertyDescriptor()` | ✅ | Full support in both interpreter and compiled mode |
| `Object.defineProperty()` | ✅ | Full support including accessor properties (get/set) and descriptor flags in both modes |
| `Object.getPrototypeOf()` | ✅ | Returns prototype of an object |
| `Object.setPrototypeOf()` | ✅ | Sets prototype of an object |
| `Object.getOwnPropertyNames()` | ✅ | Returns all own property names including non-enumerable |
| `Object.getOwnPropertySymbols()` | ✅ | Returns array of symbol-keyed properties |
| `Object.preventExtensions()` | ✅ | Prevents adding new properties to an object |
| `Object.isExtensible()` | ✅ | Returns whether object allows new properties |
| `Object.groupBy()` | ✅ | Groups iterable elements by callback return value (ES2024); returns plain object with string keys |
| `Object.defineProperties()` | ✅ | Batch version of defineProperty; defines multiple properties from a descriptors object |
| `Object.getOwnPropertyDescriptors()` | ✅ | Returns all own property descriptors as an object; works with defineProperties for proper object cloning |

### Array Methods

| Feature | Status | Notes |
|---------|--------|-------|
| `fill()` | ✅ | |
| `reduceRight()` | ✅ | Iterates right-to-left |
| `entries()` | ✅ | Returns iterator of [index, value] pairs |
| `keys()` | ✅ | Returns iterator of indices |
| `values()` | ✅ | Returns iterator of values |
| `copyWithin()` | ✅ | Copies array elements within the array |

### String Methods & Static

| Feature | Status | Notes |
|---------|--------|-------|
| `normalize()` | ✅ | Unicode normalization (NFC, NFD, NFKC, NFKD) |
| `localeCompare()` | ✅ | Locale-aware comparison via string.Compare with CurrentCulture |
| `codePointAt()` | ✅ | Full Unicode code point at position; handles surrogate pairs for supplementary characters |
| `String.fromCharCode()` | ✅ | Creates string from UTF-16 code units |
| `String.fromCodePoint()` | ✅ | Creates string from Unicode code points; handles supplementary characters (> U+FFFF) via surrogate pairs |
| `String.raw` | ✅ | Tagged template for raw string access without escape processing |

### Reflect API (Standard)

| Feature | Status | Notes |
|---------|--------|-------|
| `Reflect.get()` | ✅ | Property access on target object |
| `Reflect.set()` | ✅ | Returns `bool`; `false` for frozen objects |
| `Reflect.has()` | ✅ | Equivalent to `in` operator |
| `Reflect.deleteProperty()` | ✅ | Returns `bool`; `false` for frozen objects |
| `Reflect.apply()` | ✅ | Calls function with thisArg and args array |
| `Reflect.construct()` | ✅ | Creates instance; interpreter mode only for classes |
| `Reflect.ownKeys()` | ✅ | Returns string keys + symbol keys |
| `Reflect.getPrototypeOf()` | ✅ | Returns object prototype |
| `Reflect.setPrototypeOf()` | ✅ | Returns `bool`; `false` for non-extensible objects |
| `Reflect.isExtensible()` | ✅ | Returns `bool` |
| `Reflect.preventExtensions()` | ✅ | Returns `true` |
| `Reflect.getOwnPropertyDescriptor()` | ✅ | Returns property descriptor or undefined |
| `Reflect.defineProperty()` | ✅ | Returns `bool`; `false` on failure (unlike `Object.defineProperty` which throws) |

---

## 15. NODE.JS BUILT-IN MODULES

SharpTS implements 20+ Node.js built-in modules accessible via `import ... from "node:..."` or bare specifiers.

| Module | Status | Notes |
|--------|--------|-------|
| `fs` / `fs/promises` | ✅ | readFileSync, writeFileSync, existsSync, mkdirSync, readdirSync, statSync, unlinkSync, renameSync, copyFileSync, appendFileSync, readFile, writeFile, mkdir, readdir, stat, unlink, rename, rm, access, lstat, realpath, createReadStream, createWriteStream |
| `path` | ✅ | join, resolve, dirname, basename, extname, normalize, isAbsolute, relative, parse, format, sep, delimiter, posix, win32 |
| `os` | ✅ | platform, arch, cpus, hostname, homedir, tmpdir, type, release, uptime, totalmem, freemem, EOL, networkInterfaces, loadavg, userInfo |
| `process` | ✅ | argv, env, cwd(), exit(), pid, platform, arch, version, stdout, stderr, stdin, hrtime, nextTick, memoryUsage, exitCode |
| `crypto` | ✅ | createHash, createHmac, randomBytes, randomUUID, randomInt, randomFillSync, createCipheriv/Decipheriv, pbkdf2/Sync, scrypt/Sync, timingSafeEqual, generateKeyPair/Sync, createSign/Verify, createDiffieHellman, createECDH, hkdf/Sync, getHashes, getCiphers, getCurves, constants |
| `events` | ✅ | EventEmitter: on, once, emit, removeListener, removeAllListeners, listenerCount, listeners, prependListener, prependOnceListener, off, setMaxListeners, getMaxListeners, eventNames |
| `stream` | ✅ | Readable, Writable, Duplex, Transform, PassThrough, pipeline |
| `buffer` | ✅ | Buffer.from, Buffer.alloc, Buffer.allocUnsafe, Buffer.concat, Buffer.isBuffer, Buffer.byteLength; instance methods: toString, slice, copy, write, fill, includes, indexOf, compare, equals, readUInt/Int, writeUInt/Int, toJSON |
| `http` | ✅ | createServer, request, get; Server: listen, close; IncomingMessage/ServerResponse; request/response streaming |
| `child_process` | ✅ | execSync, exec, spawnSync |
| `url` | ✅ | URL, URLSearchParams, fileURLToPath, pathToFileURL, format, parse |
| `util` | ✅ | promisify, deprecate, types (isDate, isRegExp, isMap, isSet, etc.), format, inspect, TextEncoder, TextDecoder |
| `querystring` | ✅ | parse, stringify, escape, unescape |
| `zlib` | ✅ | gzipSync, gunzipSync, deflateSync, inflateSync, brotliCompressSync, brotliDecompressSync |
| `dns` | ✅ | lookup, resolve, resolve4, resolve6, promises API |
| `assert` | ✅ | ok, equal, notEqual, deepEqual, notDeepEqual, strictEqual, notStrictEqual, deepStrictEqual, throws, doesNotThrow, rejects, doesNotReject, fail, match, doesNotMatch, assert.strict |
| `readline` | ✅ | createInterface, question, close |
| `string_decoder` | ✅ | StringDecoder: write, end, encoding |
| `timers` | ✅ | setTimeout, clearTimeout, setInterval, clearInterval, setImmediate, clearImmediate |
| `perf_hooks` | ✅ | performance.now(), performance.mark(), performance.measure(), performance.getEntries(), PerformanceObserver |
| `worker_threads` | ✅ | Worker, isMainThread, parentPort, workerData, MessageChannel, MessagePort |

---

## Known Bugs

### IL Compiler Limitations

- ~~**Inner function declarations**~~ ✅ Inner function declarations (`function inner() {}` inside another function) are now fully supported with hoisting, closure capture, and recursion.

### Type Checker Limitations

- Type alias declarations are lazily validated - errors in type alias definitions (e.g., `type R = ReturnType<string, number>;` with wrong arg count) are only caught when the alias is used, not at declaration time. TypeScript catches these at declaration.

### Recently Fixed Bugs (2026-02-10)
- ~~`typeof` on undeclared variables throws~~ - Fixed: `typeof undeclaredVar` now returns `"undefined"` instead of throwing, matching JavaScript spec. Works in type checker, interpreter, and all compiler emitters (including async/generator state machines).

### Recently Fixed Bugs (2026-02-05)
- ~~Property descriptors in compiled mode~~ - Fixed: `Object.defineProperty()` and `Object.getOwnPropertyDescriptor()` now fully support accessor properties (get/set) and descriptor flags (writable, enumerable, configurable) in compiled mode via `PropertyDescriptorStore`.

### Recently Fixed Bugs (2026-02-04)
- ~~Spreading iterators~~ - Fixed: Type checker now allows spreading any iterable type (`[...arr.entries()]`, `[...mySet]`, `[...myMap]`, `[..."hello"]`, `[...generator()]`).
- ~~Destructuring in for...of declarations~~ - Fixed: Parser now supports `for (const [i, val] of arr.entries())` and `for (const {x, y} of items)`. Desugars to temp variable with body destructuring.
- ~~Method chaining on `new` expressions~~ - Fixed: Parser now correctly allows method chaining directly after `new` expressions (e.g., `new Date().toISOString()`)
- ~~String concatenation optimizer incorrect stringification~~ - Fixed: IL compiler's string concat optimizer now calls `Stringify()` instead of relying on .NET's `ToString()`, ensuring JavaScript-style output (`null` → "null", `true` → "true" not "True")

### Recently Fixed Bugs (2026-01-21)
- ~~Object literal accessor `this` type inference~~ - Fixed: `this` in getter/setter bodies now correctly infers the object's type instead of defaulting to `any`; uses two-pass type checking with literal type widening
- ~~`any + any` returning `bigint`~~ - Fixed: Binary operators now return `any` when either operand is `any`, not `bigint`
- ~~Spread properties ignored in compiled object literals with accessors~~ - Fixed: `MergeIntoTSObject` runtime method properly merges spread properties into `$Object` instances

### Recently Fixed Bugs (2026-01-13)
- ~~`yield await expr` NullReferenceException~~ - Fixed: State analyzer now assigns yield state before visiting nested await, matching emitter execution order
- ~~Generator variable capture for module-level variables~~ - Fixed: Generators correctly capture and use module-level variables including with `yield*`
- ~~Class expression constructors with default parameters~~ - Fixed: IL compiler now uses direct `newobj` with constructor builder instead of `Activator.CreateInstance`

### Recently Fixed Bugs (2026-01-12)
- ~~Generic types with array suffix~~ - Fixed: `ParseGenericTypeReference()` now properly finds matching `>` and handles array suffixes (`Partial<T>[]`, `Promise<number>[][]`, etc.)

### Recently Fixed Bugs (2026-01-06)
- ~~Math.round() JS parity~~ - Fixed: Now uses `Math.Floor(x + 0.5)` for JavaScript-compatible rounding (half-values toward +∞)
- ~~Object method `this` binding~~ - Fixed: `{ fn() { return this.x; } }` now correctly binds `this` in compiled code via `__this` parameter

---
