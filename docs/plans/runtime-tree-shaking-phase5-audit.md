# Phase 5 audit: ungated $Runtime method clusters

Inventory of method clusters in `RuntimeEmitter.RuntimeClass.cs` (the
master emit list) as of commit `767c36e` (Phase 5a complete). The goal
was to identify clusters that are still emitted unconditionally and rank
them by likely size win × detection difficulty.

## Already gated (no work needed)

| Flag | Cluster | Line in RuntimeClass.cs |
|------|---------|-------------------------|
| `UsesReflect` | Reflect.set/setPrototypeOf/defineProperty/ownKeys/apply/construct | 587 |
| `UsesRegExp` | EmitRegExpMethods | 724 |
| `UsesJSON` | JsonParse + JsonStringify family | 750 |
| `UsesBigInt` | CreateBigInt + BigIntArithmetic + BigIntComparison + BigIntBitwise | 763 |
| `UsesDate` | EmitDateMethods | 784 |
| `UsesFs` | FsModuleMethods + FsModuleMethodWrappers + FsWatchFactories | 818, 827, 867 |
| `UsesDns` | DnsModuleMethods + DnsPromisesMethods | 821, 886 |
| `UsesHttp` | EmitHttpModuleMethods | 837 |
| `UsesNet` | EmitNetModuleMethods | 840 |
| `UsesTls` | EmitTlsModuleMethods | 843 |
| `UsesDgram` | EmitDgramModuleMethods | 846 |
| `UsesCrypto` | EmitCryptoMethods | 855 |
| `UsesZlib` | EmitZlibMethods | 883 |
| `UsesIntl` | EmitIntlMethods | 894 |
| `UsesCluster` | EmitClusterHelpers | 904 |

## Easy wins — flag exists or obvious import

These have a clear, single-feature trigger and a complete cluster of
methods. Lowest implementation effort, smallest blast radius.

| Cluster | Detection | Approx file size | Notes |
|---------|-----------|------------------|-------|
| **EmitReadlineMethods** (line 861) | `UsesReadline` (already exists, not gated) | ~28KB | **Orphan flag** — flag is defined but call site doesn't check it. Pure missed gate. |
| **EmitOsModuleMethods** (line 820) | `import 'os'` / `require('os')` | ~38KB | Most callers do `os.platform()` or `os.cpus()`. Easy to detect at module-path level. |
| **EmitChildProcessMethods** (line 863) | `import 'child_process'` | 54KB *file* | Big — `spawn`, `exec`, `fork`, etc. Many test programs never touch it. |
| **EmitVmMethods** (line 908) | `import 'vm'` | small | Niche; `vm.runInNewContext` etc. |
| **EmitTtyPrimitiveMethods** (line 834) | `import 'tty'` / `process.stdout.isTTY` | small | Just `isatty(fd)`. |
| **EmitPerfPrimitiveMethods** (line 890) | `performance.now` / `performance.timeOrigin` | small | Most stuff is in stdlib `perf_hooks.ts`. |
| **EmitReflectMetadataMethods** (line 865) | `UsesReflectMetadata` (already exists, not gated) | small | Same situation as Readline. |
| **EmitTlsHandshakeHelpers** (line 898) | implication of `UsesTls` | small | Already-gated parent → child should follow. |

**Estimated combined win:** ~50–80KB if all eight land. (Tentative — needs
actual measurement; some method bodies may be small even when files are
large.)

## Medium-effort — partial usage, need careful detection

| Cluster | Detection | Approx file size | Notes |
|---------|-----------|------------------|-------|
| **EmitConsoleExtensions** (line 853) | `console.X` for X in {error, warn, dir, time, timeEnd, group, groupEnd, clear, table, count} | ~? | `console.log` is universal; extensions are common but not universal. Hard to gate as one cluster — finer-grained per-method gates would help. |
| **EmitProcessMethods** (line 881) | usage of `process.X` | ~? | Most programs touch `process.argv`/`env`/`cwd`. Probably keep unconditional. |
| **EmitFinalizationRegistry / EmitWeakRef** (804-805) | `FinalizationRegistry` / `WeakRef` identifier | ~? | Niche; flag may already exist (`UsesFinalizationRegistry`). |
| **EmitProxyMethods** (line 807) | `Proxy` identifier | ~? | Common in modern JS. Worth gating. |
| **EmitAbortControllerMethods** (line 810) | `AbortController` / `AbortSignal` identifier | ~? | Common in fetch + timer code. |
| **EmitDynamicImportMethods** (line 812) | `import(...)` syntax (parser-level) | ~? | Detect via AST `Stmt.DynamicImport`. |
| **EmitAsyncGeneratorAwaitContinue** (line 814) | `async function*` / `yield` in async fn | ~? | Detect via parser flag in function declarations. |
| **EmitNumberMethods + EmitNumberPrototypePopulate** (772, 775) | Number is a primitive — always-needed for `1.toString()` etc. | ~50KB+ | Hard. Number prototype is fundamental. |
| **EmitMapMethods / EmitSetMethods / EmitWeakMapMethods / EmitWeakSetMethods** (794-801) | `new Map()` / `new Set()` / `new WeakMap()` / `new WeakSet()` | ~? | Detect via `new X()` or bare identifier. |

## Hard — coupled to runtime dispatch

These can't be cleanly gated because their methods are referenced via
populate-dictionary patterns (e.g. `ArrayPrototypePopulate` stores
`$TSFunction` wrappers around every `ArrayPop`/`ArrayMap`/etc. method,
all of which must exist if anyone reads `arr.map`).

| Cluster | Approx file size | Why it's hard |
|---------|-----------------|---------------|
| Array prototype helpers (ArrayPop/Map/Filter/etc.) | ~290KB across Mutators+Iterators+Search | All wired into `_arrayPrototype` dictionary via `ArrayPrototypePopulate`. To gate, need to either (a) emit only the populate entries that are referenced, or (b) skip populate entirely when no arr.X access exists. Both are invasive. |
| String prototype helpers (StringCharAt/etc.) | ~72KB | Same pattern via `StringPrototypePopulate`. |
| Number prototype helpers | ~103KB | Same. |
| Object freeze/seal/define/descriptors | ~? | Some are referenced by descriptor-based property lookups internally. Detection-fragile. |
| Format specifier helpers (HasFormatSpecifiers/FormatSingleArg/FormatAsInteger/Float/Json/ConsoleArgs) | small | Already upstream of ConsoleLog; needed even for plain `console.log(x)` because the format detector runs first. Probably keep. |

## Recommended sequencing for Phase 5b–5h

| Phase | Cluster | Rationale | Risk |
|-------|---------|-----------|------|
| **5b** | EmitReadlineMethods (orphan-flag fix) | Trivial — flag exists, just add the `if`. Ships pattern with zero new detection logic. | Very low. |
| **5c** | EmitReflectMetadataMethods (orphan-flag fix) | Same — `UsesReflectMetadata` already detected. Verify and gate. | Very low. |
| **5d** | EmitTlsHandshakeHelpers under `UsesTls` | Trivial implication — TLS helpers only meaningful with TLS. | Very low. |
| **5e** | New `UsesOs` flag + gate `EmitOsModuleMethods` | Big size win (~38KB), easy detection (`os` module path). | Low. |
| **5f** | New `UsesChildProcess` + gate `EmitChildProcessMethods` | Biggest single-file win (54KB). | Low–medium (some test patterns spawn). |
| **5g** | New `UsesVm` + gate `EmitVmMethods` | Small but trivial. | Very low. |
| **5h** | New `UsesProxy` flag + gate `EmitProxyMethods` | Niche but isolated. | Low (Proxy detection already exists for runtime checks?). |
| **Audit** | Re-measure DLL size, decide on Tier-2 (Map/Set/AbortController/etc.) and Tier-3 (prototype population). | Data-driven next step. | — |

Phases 5b–5d are essentially **free** — they fix orphan flags or trivially
imply from existing gates. Phases 5e–5h add new flags but follow the
established pattern. After Phase 5h, the next round would be the harder
cases (Array/String prototype shaking) which need a different approach
(populate-table pruning).
