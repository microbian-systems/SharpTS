# SharpTS TypeScript Conformance Runner

Runs SharpTS's type checker against the canonical [microsoft/TypeScript](https://github.com/microsoft/TypeScript) conformance corpus and diffs our diagnostics against `tsc`'s `*.errors.txt` baselines. Mirrors the shape of `SharpTS.Test262/`.

Tracking epic: [#80](https://github.com/nickna/SharpTS/issues/80). This project is currently scaffolding only — runner and baseline harness land in [#84](https://github.com/nickna/SharpTS/issues/84) and [#85](https://github.com/nickna/SharpTS/issues/85).

## Pinned TypeScript version

The corpus is vendored as a git submodule at `external/typescript/`, pinned to **`v5.5.4`**. TypeScript rewords its diagnostic messages between versions, so the pin is load-bearing for baseline stability. Bumping it is intentional, not incidental.

## Initial setup

```bash
git submodule update --init external/typescript
```

The TypeScript repo contains paths that exceed Windows' default 260-character `MAX_PATH`. On Windows you'll need long-path support in git:

```bash
git config --global core.longpaths true
```

## Running locally

This project is **not** included in `SharpTS.sln`. Solution-level `dotnet build` and `dotnet test` (what CI runs) won't pick it up. Invoke explicitly:

```bash
dotnet test SharpTS.TypeScriptConformance/SharpTS.TypeScriptConformance.csproj
```

At this stage the project compiles and runs but has zero tests. The runner lands in [#84](https://github.com/nickna/SharpTS/issues/84).

## Updating the baseline

Once the runner lands, regenerate the committed baseline by setting:

```bash
SHARPTS_TSCONFORMANCE_UPDATE_BASELINE=1 dotnet test SharpTS.TypeScriptConformance/SharpTS.TypeScriptConformance.csproj
```

Same shape as `SHARPTS_TEST262_UPDATE_BASELINE=1`.

## Layout

| Path | Purpose |
|---|---|
| `external/typescript/` | Vendored TS repo (submodule, pinned to a tag) |
| `external/typescript/tests/cases/conformance/` | The conformance corpus (~10–15k `.ts` files) |
| `external/typescript/tests/baselines/reference/` | `tsc`'s `*.errors.txt` / `*.js` / `*.types` baselines |
| `external/typescript/src/lib/` | `lib.es*.d.ts`, `lib.dom.d.ts`, etc. |
| `SharpTS.TypeScriptConformance/` | This runner project |

## See also

- `SharpTS.Test262/` — the equivalent project for the ECMA-262 / JavaScript spec; this one mirrors its harness shape.
