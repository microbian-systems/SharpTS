# Fetch credentials & cookie jar

## Goal

Implement the WHATWG fetch `credentials` option and a process-wide cookie jar
in both the interpreter and the compiled (IL) execution paths, without taking
any reflection dependency on `SharpTS.dll` from emitted code.

## Status

- [x] Plan written
- [x] Cookie jar singleton + `Set-Cookie` array fix
- [x] Interpreter: 4-client pool + credentials plumbing
- [x] Interpreter tests
- [x] Compiled mode: 4-client pool in `EmitCachedHttpClients`
- [x] Tests over `ExecutionModes.All`
- [x] `fetch.cookieJar` introspection API
- [x] STATUS-NODE.md update

**Shipped:** 2026-04-07. 26 cookie tests across both interpreter and compiled modes.

### Worker thread caveat

`SharpTSWorker.RunWorkerScript` always creates a fresh `Interpreter` to run the
worker script — even when the parent process is running compiled IL. The worker's
fetch jar is therefore `SharpTSCookieJar.GlobalContainer` (the interpreter
singleton in SharpTS.dll), while a compiled parent uses its own
`$Runtime._cookieContainer` static field on the emitted runtime type. **These are
two different jars in the same process.**

Concrete behaviors:

| Parent mode    | Worker (always interpreted) | Cookies shared? |
|----------------|-----------------------------|------------------|
| Interpreted    | Interpreted                 | ✅ Yes (same `SharpTSCookieJar.GlobalContainer`) |
| Compiled       | Interpreted                 | ❌ No (parent reads compiled jar; worker reads interpreter jar) |

If the compiled-vs-interpreter split becomes a real problem, the path is to make
the emitted `$Runtime._cookieContainer` resolve to `SharpTSCookieJar.GlobalContainer`
via reflection — but that violates the standalone-DLL constraint. The cleaner fix
is to have compiled-mode workers run the worker script through the IL compiler too,
not the interpreter.

### Compiled-mode `fetch.cookieJar` caveat

The compiled-mode introspection API uses a compile-time lowering: only the inline
form `fetch.cookieJar.METHOD(args)` is recognized. Aliasing first
(`const cj = fetch.cookieJar; cj.clear();`) does **not** work in compiled mode.
The interpreter mode supports both forms via `SharpTSFetchGlobal` /
`SharpTSCookieJar` implementing `ISharpTSPropertyAccessor`. If aliasing support
is needed in compiled mode, the path is to make `fetch` resolve to a `$Fetch`
runtime object with a `cookieJar` static property — about a day's IL work.

## Decisions

| Question | Answer |
|---|---|
| Default `credentials` | `'same-origin'` (matches Node/undici). Without a top-level realm, `CookieContainer`'s domain matching makes this behave like "send cookies only to hosts that set them" — which is the user-intuitive outcome. |
| Cookie persistence | Process-only. No disk persistence in v1. |
| Cookie jar API | Minimal: `fetch.cookieJar.getCookies(url)`, `setCookie(cookie, url)`, `clear()`. |
| `http.Agent` cookies option | Deferred to follow-up. |
| `Set-Cookie` storage | Array (multi-value preserved). `headers.get('set-cookie')` returns first per spec; `headers.getSetCookie()` returns the array. **Breaking** vs current comma-joined behavior, but current behavior is lossy. |
| Compiled-mode async dispatch | Unchanged — keep the existing `Task.Run` + `$FetchDisplayClass` model. |
| `document.cookie` global | Skipped (no DOM). |
| Worker visibility | **In practice not what was originally planned.** See "Worker thread caveat" below. |

## Architecture

### Cookie store

A single process-wide `System.Net.CookieContainer` exposed via a small wrapper
type `SharpTSCookieJar` (in `Runtime/Types/SharpTSCookieJar.cs`). The wrapper
provides the public API methods used by both modes and is the canonical type
that compiled IL refers to via `Type.GetType("System.Net.CookieContainer, ...")`
when needed (the container itself is BCL, so no SharpTS reference is created).

### HttpClient pool — both modes

We currently cache 2 clients (`follow` × `noRedirect`). Cookies multiply this
to 4:

|              | follow      | noRedirect   |
|--------------|-------------|--------------|
| **with-cookies**    | client A | client B |
| **without-cookies** | client C | client D |

Both with-cookies clients share the same `CookieContainer` instance, so cookies
set by any request flow into the jar that the next request reads from.

### Interpreter (`Runtime/BuiltIns/FetchBuiltIns.cs`)

- Add two more cached `HttpClient` fields wired to the shared
  `SharpTSCookieJar.GlobalContainer`
- `ExecuteFetch` reads `options.credentials`, picks the matching client
- `SharpTSRequest` gains a `Credentials` string property defaulting to
  `"same-origin"`

### Compiled (`Compilation/RuntimeEmitter.Http.cs`)

- Extend `EmitCachedHttpClients` to emit **four** static fields and a 2-arg
  `GetOrCreateHttpClient(redirectMode, useCookies)` selector — pure IL
- Add a single static `_cookieContainer` field on the emitted runtime type,
  initialized lazily via `Newobj CookieContainer.ctor`
- The two with-cookies handlers' `CookieContainer` setter is called with the
  shared field
- `EmitFetchHelper` reads `credentials` from the options dict (loop already
  scans the dict) and passes it through to the selector
- `$Request` gains a `_credentials` field paralleling the interpreter

### `Set-Cookie` header preservation

`SharpTSHeaders` already stores values internally as `Dictionary<string,
List<string>>` — but the `HttpResponseMessage` ingest constructor flattens
each header to a single `string.Join(", ", values)` element, destroying
multi-value `Set-Cookie`. Fix:

1. `SharpTSHeaders(HttpResponseMessage)` constructor: store the raw value list,
   not the joined string
2. Add `GetSetCookie()` method returning `IList<string>` and a `getSetCookie`
   member binding that returns a `SharpTSArray`
3. Compiled `$Headers`: same change to `EmitHeadersClass` ingest path; expose
   `getSetCookie` method

### Compiled `FetchHelper` headers extraction

The current `FetchHelper` builds the headers dict by iterating
`HttpResponseMessage.Headers` and writing
`headers[name.ToLowerInvariant()] = string.Join(", ", values)`. We change this
so that **`Set-Cookie` is stored as `string[]`** (not joined). The
`$FetchResponse` constructor's headers wrapper (`new $Headers(dict)`) accepts
either a string or array value per key.

## Public API

```ts
// Web standard
fetch(url, { credentials: 'omit' | 'same-origin' | 'include' })

// Web standard
response.headers.get('set-cookie')        // first cookie string per spec
response.headers.getSetCookie()           // string[] of all Set-Cookie values

// SharpTS extension
fetch.cookieJar.getCookies(url: string): string  // value of "Cookie:" header
fetch.cookieJar.setCookie(cookie: string, url: string): void
fetch.cookieJar.clear(): void
```

## Test plan

`SharpTS.Tests/SharedTests/FetchCookieTests.cs` running `[Theory]` over
`ExecutionModes.All`. New `MockHttpServer` extensions:

- `AddSetCookieRoute(path, cookieHeader)` — emits `Set-Cookie: <header>` then 200
- `AddCookieEchoRoute(path)` — echoes incoming `Cookie:` header into response body
- Multi-value `Set-Cookie` route (two cookies in one response)

Cases:

1. `Set-Cookie` parsed and stored; subsequent same-host fetch sends `Cookie:`
2. `credentials: 'omit'` does not send stored cookies
3. `credentials: 'omit'` does not store `Set-Cookie` from the response
4. Cross-host: cookies from host A are not sent to host B
5. Expired cookie (`Max-Age=0`) is removed
6. `HttpOnly` cookie is sent in subsequent requests (server flag — no JS distinction needed)
7. Multiple `Set-Cookie` headers preserved as array via `getSetCookie()`
8. `headers.get('set-cookie')` returns first cookie string
9. Cookies survive across a redirect chain (both follow and manual modes)
10. `fetch.cookieJar.clear()` removes all cookies
11. `fetch.cookieJar.getCookies(url)` returns the right `Cookie:` header for a host
12. Concurrent fetches to the same host don't race the jar (50 parallel)

Plus a `StandaloneDllTests` allowlist update for any new
`Compilation/RuntimeEmitter.*.cs` file paths.

## Out of scope (deferred)

- `http.Agent` cookies option
- Disk persistence of cookies
- `tough-cookie`-like Cookie class
- Full `document.cookie` API
