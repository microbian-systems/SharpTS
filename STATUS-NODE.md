# SharpTS Node.js Module Support Status

This document tracks Node.js module and API implementation status in SharpTS.

**Last Updated:** 2026-04-06 (CommonJS in both interpreter & compiled modes: require/module.exports, .cjs detection, ESM↔CJS interop, circular requires)

## Legend
- ✅ Implemented
- ⚠️ Partially Implemented
- ❌ Not Implemented

---

## 1. CORE NODE.JS MODULES

| Module | Status | Notes |
|--------|--------|-------|
| `fs` | ✅ | Sync, callback-based async, and `fs.promises` APIs |
| `path` | ✅ | Full API |
| `os` | ✅ | Full API |
| `process` | ✅ | Properties + methods, available as module and global |
| `crypto` | ✅ | Hash, HMAC, Cipher, PBKDF2, scrypt, HKDF (sync + async callback), RSA encrypt/decrypt, Sign/Verify, DH/ECDH, KeyPair (sync + async), KeyObject |
| `url` | ✅ | WHATWG URL + legacy parse/format/resolve |
| `querystring` | ✅ | parse, stringify, escape, unescape |
| `assert` | ✅ | Full testing utilities |
| `child_process` | ✅ | execSync, spawnSync, exec, spawn, execFileSync, execFile, fork (IPC via named pipes); ChildProcess EventEmitter with kill, send, disconnect, stdin |
| `util` | ✅ | format, inspect, isDeepStrictEqual, parseArgs, toUSVString, stripVTControlCharacters, getSystemErrorName, getSystemErrorMap, promisify, types helpers, deprecate, callbackify, inherits, TextEncoder/TextDecoder |
| `console` | ✅ | log, error, warn, info, debug, clear, time/timeEnd/timeLog, assert, count/countReset, table, dir, group/groupEnd, trace |
| `readline` | ✅ | questionSync, createInterface (extends EventEmitter), question, close, prompt, pause, resume, write, setPrompt, getPrompt |
| `events` | ✅ | EventEmitter with on/off/once/emit/removeListener |
| `stream` | ✅ | Readable, Writable, Duplex, Transform, PassThrough (sync mode) |
| `buffer` | ✅ | Full Buffer class with multi-byte LE/BE, float/double, BigInt, search, swap |
| `timers` | ✅ | setTimeout, setInterval, setImmediate + clear variants (module import) |
| `timers/promises` | ✅ | Promise-based setTimeout/setImmediate (with AbortSignal), AsyncIterable setInterval |
| `string_decoder` | ✅ | StringDecoder class for multi-byte character handling |
| `perf_hooks` | ✅ | performance.now(), timeOrigin, mark(), measure(), getEntries/ByName/ByType(), clearMarks/Measures(); PerformanceObserver |
| `http` / `https` | ✅ | createServer, request, get; IncomingMessage extends Readable; ServerResponse extends Writable; full event lifecycle |
| `net` | ✅ | createServer, createConnection/connect, Socket, Server; isIP, isIPv4, isIPv6 |
| `tls` | ✅ | createServer, connect, createSecureContext, TLSSocket, Server; DEFAULT_MIN_VERSION, DEFAULT_MAX_VERSION; ALPNProtocols, SNICallback, servername; secureConnect/secureConnection/tlsClientError events |
| `dns` | ✅ | lookup, lookupService, resolve, resolve4, resolve6, reverse, resolveMx, resolveTxt, resolveSrv, resolveCname, resolveNs, resolveSoa, resolvePtr, resolveCaa, resolveNaptr (callback + dns/promises) |
| `zlib` | ✅ | gzip, deflate, deflateRaw, brotli, zstd (sync + streaming + async callback APIs) |
| `worker_threads` | ⚠️ | Worker, MessageChannel, parentPort, workerData, isMainThread |
| `dgram` | ✅ | createSocket, Socket; bind, send, close, address, setBroadcast, setTTL, addMembership, dropMembership; connect, disconnect, remoteAddress, get/setRecvBufferSize, get/setSendBufferSize; message/listening/close/error/connect events |
| `cluster` | ✅ | isPrimary/isWorker/isMaster, fork, worker.send/disconnect/kill/isDead/isConnected, process.send (IPC), cluster events (fork/online/disconnect/exit/message), cluster.disconnect, setupPrimary, workers dict |
| `vm` | ✅ | runInNewContext, runInThisContext, createContext, isContext, compileFunction, Script class |
| `async_hooks` | ✅ | AsyncLocalStorage: run, getStore, enterWith, exit, disable; async context propagation via .NET AsyncLocal |

---

## 2. FILE SYSTEM (`fs`)

| Feature | Status | Notes |
|---------|--------|-------|
| **File Operations** | | |
| `existsSync` | ✅ | |
| `readFileSync` | ✅ | Supports encoding option |
| `writeFileSync` | ✅ | |
| `appendFileSync` | ✅ | |
| `copyFileSync` | ✅ | |
| `renameSync` | ✅ | |
| `unlinkSync` | ✅ | |
| `truncateSync` | ✅ | Truncate file to specified length |
| **Directory Operations** | | |
| `mkdirSync` | ✅ | Supports `recursive` option |
| `rmdirSync` | ✅ | |
| `readdirSync` | ✅ | Supports `recursive` and `withFileTypes` options |
| `mkdtempSync` | ✅ | Create unique temp directory |
| `opendirSync` | ✅ | Returns Dir object with readSync/closeSync |
| **File Info** | | |
| `statSync` | ✅ | Returns Stats object |
| `lstatSync` | ✅ | Stats without following symlinks |
| `accessSync` | ✅ | Check file access permissions |
| `realpathSync` | ✅ | Resolve canonical path |
| **File Descriptor APIs** | | |
| `openSync` | ✅ | Open file, returns fd (flags: r, w, a, r+, w+, a+, etc.) |
| `closeSync` | ✅ | Close file descriptor |
| `readSync` | ✅ | Read into Buffer at offset/position |
| `writeSync` | ✅ | Write Buffer or string to fd |
| `fstatSync` | ✅ | Stats for open file descriptor |
| `ftruncateSync` | ✅ | Truncate open file descriptor |
| **Links** | | |
| `linkSync` | ✅ | Create hard link (cross-platform) |
| `symlinkSync` | ✅ | Create symbolic link |
| `readlinkSync` | ✅ | Read symbolic link target |
| **Permissions** | | |
| `chmodSync` | ✅ | Change file mode/permissions |
| `chownSync` | ✅ | Change file owner (Unix only) |
| `lchownSync` | ✅ | Change symlink owner (Unix only) |
| `utimesSync` | ✅ | Update file access/modification times |
| **Async APIs (Callback)** | | |
| `readFile` | ✅ | Callback-based async |
| `writeFile` | ✅ | Callback-based async |
| `appendFile` | ✅ | Callback-based async |
| `stat` / `lstat` | ✅ | Callback-based async |
| `unlink` | ✅ | Callback-based async |
| `mkdir` / `rmdir` | ✅ | Callback-based async |
| `readdir` | ✅ | Callback-based async |
| `rename` / `copyFile` | ✅ | Callback-based async |
| `access` / `chmod` | ✅ | Callback-based async |
| `truncate` / `utimes` | ✅ | Callback-based async |
| `readlink` / `realpath` | ✅ | Callback-based async |
| `symlink` / `link` | ✅ | Callback-based async |
| `mkdtemp` | ✅ | Callback-based async |
| **Promise APIs (`fs/promises`)** | | |
| `fs/promises` | ✅ | Full promise-based API (also via `fs.promises`) |
| **Advanced** | | |
| `createReadStream` | ✅ | Returns Readable stream with `data`, `end`, `error` events |
| `createWriteStream` | ✅ | Returns Writable stream with `finish`, `error` events |
| `watch` | ✅ | Returns FSWatcher (EventEmitter) with `change`, `rename`, `error`, `close` events; supports `recursive` option |
| `watchFile` | ✅ | Polling-based file watching with `(current, previous)` Stats callback; supports `interval` option |
| `unwatchFile` | ✅ | Stop watching a file previously started with watchFile |
| **Error Codes** | ✅ | ENOENT, EACCES, EEXIST, EISDIR, ENOTDIR, ENOTEMPTY, EBADF, EXDEV, etc. |

---

## 3. PATH (`path`)

| Feature | Status | Notes |
|---------|--------|-------|
| `join` | ✅ | |
| `resolve` | ✅ | |
| `basename` | ✅ | |
| `dirname` | ✅ | |
| `extname` | ✅ | |
| `normalize` | ✅ | |
| `isAbsolute` | ✅ | |
| `relative` | ✅ | |
| `parse` | ✅ | Returns { root, dir, base, ext, name } |
| `format` | ✅ | |
| `sep` | ✅ | Platform path separator |
| `delimiter` | ✅ | Platform path list delimiter |
| `posix` | ✅ | POSIX-style path methods (always uses `/`) |
| `win32` | ✅ | Windows-style path methods (always uses `\`) |

---

## 4. OS (`os`)

| Feature | Status | Notes |
|---------|--------|-------|
| `platform` | ✅ | |
| `arch` | ✅ | |
| `hostname` | ✅ | |
| `homedir` | ✅ | |
| `tmpdir` | ✅ | |
| `type` | ✅ | |
| `release` | ✅ | |
| `cpus` | ✅ | Returns CPU info array |
| `totalmem` | ✅ | |
| `freemem` | ✅ | |
| `userInfo` | ✅ | |
| `EOL` | ✅ | Platform line ending |
| `networkInterfaces` | ✅ | Returns network interface information |
| `loadavg` | ✅ | Returns [0, 0, 0] on Windows (Node.js behavior) |

---

## 5. PROCESS

| Feature | Status | Notes |
|---------|--------|-------|
| **Properties** | | |
| `platform` | ✅ | |
| `arch` | ✅ | |
| `pid` | ✅ | |
| `version` | ✅ | |
| `env` | ✅ | Environment variables |
| `argv` | ✅ | Command-line arguments |
| `exitCode` | ✅ | |
| `stdin` | ✅ | Basic input support |
| `stdout` | ✅ | write() method |
| `stderr` | ✅ | write() method |
| **Methods** | | |
| `cwd` | ✅ | |
| `chdir` | ✅ | |
| `exit` | ✅ | |
| `hrtime` | ✅ | High-resolution time |
| `uptime` | ✅ | |
| `memoryUsage` | ✅ | |
| `nextTick` | ✅ | Schedules callback (implemented via timer) |
| **Events** | | |
| `on('exit')` | ✅ | Process extends EventEmitter; exit event emitted before process.exit() |
| `on('uncaughtException')` | ✅ | Process extends EventEmitter; uncaughtException event support |
| `on(event, listener)` | ✅ | Full EventEmitter API (on, once, off, emit, removeAllListeners, etc.) |

---

## 6. CRYPTO

| Feature | Status | Notes |
|---------|--------|-------|
| **Hashing** | | |
| `createHash` | ✅ | md5, sha1, sha256, sha384, sha512 |
| `createHmac` | ✅ | md5, sha1, sha256, sha384, sha512 with string/Buffer keys |
| **Random** | | |
| `randomBytes` | ✅ | |
| `randomFillSync` | ✅ | Fill buffer with random bytes in-place |
| `randomUUID` | ✅ | |
| `randomInt` | ✅ | |
| **Cipher** | | |
| `createCipheriv` | ✅ | AES-128/192/256-CBC and AES-128/192/256-GCM |
| `createDecipheriv` | ✅ | AES-128/192/256-CBC and AES-128/192/256-GCM |
| `createCipher` / `createDecipher` | ❌ | Deprecated in Node.js, use iv variants |
| **Key Derivation** | | |
| `pbkdf2Sync` | ✅ | sha1, sha256, sha384, sha512 (not md5) |
| `scryptSync` | ✅ | With N/cost, r/blockSize, p/parallelization options |
| `pbkdf2` / `scrypt` | ✅ | Async callback-based versions with event loop integration |
| **Comparison** | | |
| `timingSafeEqual` | ✅ | Constant-time buffer comparison (prevents timing attacks) |
| **Signing** | | |
| `createSign` / `createVerify` | ✅ | RSA and EC keys; SHA1/256/384/512; hex/base64/Buffer output |
| **Key Generation** | | |
| `generateKeyPairSync` | ✅ | RSA (2048/4096) and EC (P-256/P-384/P-521); PEM format |
| `generateKeyPair` | ✅ | Async callback-based version with (err, publicKey, privateKey) signature |
| **Diffie-Hellman** | | |
| `createDiffieHellman` | ✅ | With prime length or explicit prime/generator |
| `getDiffieHellman` | ✅ | Predefined groups: modp1, modp2, modp5, modp14-18 |
| `createECDH` | ✅ | P-256 (prime256v1), P-384 (secp384r1), P-521 (secp521r1) |
| **Discovery** | | |
| `getHashes` | ✅ | Returns array of supported hash algorithms |
| `getCiphers` | ✅ | Returns array of supported cipher algorithms |
| **RSA Encryption** | | |
| `publicEncrypt` | ✅ | RSA-OAEP encryption (SHA-1 default) |
| `privateDecrypt` | ✅ | RSA-OAEP decryption |
| `privateEncrypt` | ✅ | RSA PKCS#1 v1.5 signing primitive |
| `publicDecrypt` | ✅ | RSA PKCS#1 v1.5 verification primitive |
| **HKDF** | | |
| `hkdfSync` | ✅ | HKDF key derivation (RFC 5869); sha256, sha384, sha512 |
| `hkdf` | ✅ | Async callback-based version with event loop integration |
| **KeyObject** | | |
| `createSecretKey` | ✅ | Create symmetric KeyObject from Buffer |
| `createPublicKey` | ✅ | Create public KeyObject from PEM |
| `createPrivateKey` | ✅ | Create private KeyObject from PEM |
| `KeyObject.type` | ✅ | 'secret', 'public', or 'private' |
| `KeyObject.asymmetricKeyType` | ✅ | 'rsa' or 'ec' (undefined for secret) |
| `KeyObject.asymmetricKeyDetails` | ✅ | modulusLength/publicExponent for RSA, namedCurve for EC |
| `KeyObject.symmetricKeySize` | ✅ | Byte length (secret keys only) |
| `KeyObject.export()` | ✅ | Export to PEM string or Buffer |

---

## 7. URL

| Feature | Status | Notes |
|---------|--------|-------|
| **WHATWG URL API** | | |
| `URL` class | ✅ | Full property access |
| `URLSearchParams` | ✅ | get, set, has, append, delete, keys, values, size |
| **Legacy API** | | |
| `parse` | ✅ | |
| `format` | ✅ | |
| `resolve` | ✅ | |

---

## 8. CHILD PROCESS

| Feature | Status | Notes |
|---------|--------|-------|
| **Sync Methods** | | |
| `execSync` | ✅ | With cwd, timeout, env, shell options |
| `spawnSync` | ✅ | With cwd, timeout, env options |
| `execFileSync` | ✅ | Execute file directly (no shell); with cwd, timeout, env |
| **Async Methods** | | |
| `exec` | ✅ | Async with callback(error, stdout, stderr); returns ChildProcess EventEmitter |
| `spawn` | ✅ | Async; returns ChildProcess with stdout/stderr/stdin streams and events |
| `execFile` | ✅ | Execute file directly (no shell); async with callback; returns ChildProcess |
| `fork` | ✅ | Spawns new SharpTS process with IPC channel via named pipes; parent/child send/on('message') |
| **ChildProcess** | | |
| `pid` | ✅ | Process ID |
| `exitCode` | ✅ | Exit code (null until exit) |
| `killed` | ✅ | Whether process was killed |
| `stdout` / `stderr` | ✅ | Readable streams (spawn/fork) |
| `stdin` | ✅ | Writable stream (spawn) |
| `kill(signal?)` | ✅ | Actually kills the process (with entireProcessTree) |
| `send(message)` | ✅ | IPC messaging (fork only) |
| `disconnect()` | ✅ | Close IPC channel (fork only) |
| `connected` | ✅ | IPC connection status (fork only) |
| `on`/`once`/`off` | ✅ | EventEmitter: close, exit, error, message, disconnect events |
| `ref()` / `unref()` | ✅ | Event loop ref counting (basic) |

---

## 9. UTIL

| Feature | Status | Notes |
|---------|--------|-------|
| **Formatting** | | |
| `format()` | ✅ | Placeholders: %s, %d, %i, %f, %j, %o, %O, %% |
| `inspect()` | ✅ | Object stringification with depth option |
| `stripVTControlCharacters()` | ✅ | Remove ANSI escape sequences |
| **Comparison** | | |
| `isDeepStrictEqual()` | ✅ | Deep equality (NaN equals NaN) |
| **CLI Parsing** | | |
| `parseArgs()` | ✅ | Boolean/string options, short/long flags, negation |
| **String Utilities** | | |
| `toUSVString()` | ✅ | Replace lone surrogates with replacement char |
| **System Errors** | | |
| `getSystemErrorName()` | ✅ | POSIX errno to name (70+ codes) |
| `getSystemErrorMap()` | ✅ | Map of errno to [name, description] |
| **Function Utilities** | | |
| `deprecate()` | ✅ | Wrap function with deprecation warning |
| `callbackify()` | ✅ | Convert Promise function to callback style |
| `inherits()` | ✅ | Set up prototype chain (sets super_) |
| **TextEncoder** | | |
| `new TextEncoder()` | ✅ | UTF-8 encoder |
| `encode()` | ✅ | String to Uint8Array |
| `encodeInto()` | ✅ | Encode into existing buffer |
| **TextDecoder** | | |
| `new TextDecoder()` | ✅ | Supports utf-8, latin1, utf-16le |
| `decode()` | ✅ | Buffer to string |
| **util.types** | | |
| `types.isArray()` | ✅ | Array check |
| `types.isDate()` | ✅ | Date check |
| `types.isFunction()` | ✅ | Function check |
| `types.isNull()` | ✅ | Null check (not undefined) |
| `types.isUndefined()` | ✅ | Undefined check (not null) |
| `types.isPromise()` | ✅ | Promise check |
| `types.isRegExp()` | ✅ | RegExp check |
| `types.isMap()` | ✅ | Map check |
| `types.isSet()` | ✅ | Set check |
| `types.isTypedArray()` | ✅ | Buffer/TypedArray check |
| `types.isNativeError()` | ✅ | Error check |
| `types.isBoxedPrimitive()` | ✅ | Always false (no boxed primitives) |
| `types.isWeakMap()` | ✅ | WeakMap check |
| `types.isWeakSet()` | ✅ | WeakSet check |
| `types.isArrayBuffer()` | ✅ | ArrayBuffer check |
| **Async/Promisify** | | |
| `promisify()` | ✅ | Converts callback-style to Promise-returning |

---

## 10. MODULE SYSTEM

| Feature | Status | Notes |
|---------|--------|-------|
| **ES Modules** | | |
| `import { x } from './file'` | ✅ | Named imports |
| `import x from './file'` | ✅ | Default imports |
| `import * as x from './file'` | ✅ | Namespace imports |
| `export { x }` | ✅ | Named exports |
| `export default x` | ✅ | Default exports |
| `export * from './file'` | ✅ | Re-exports |
| `import type { T }` | ✅ | Type-only imports |
| `import('./file')` | ✅ | Dynamic imports |
| `import.meta.url` | ✅ | Module URL (file:// format) |
| `import.meta.dirname` | ✅ | Directory of current module |
| `import.meta.filename` | ✅ | Full path of current module |
| **CommonJS Interop** | | |
| `import x = require('path')` | ✅ | CommonJS import syntax |
| `export =` | ✅ | CommonJS export syntax |
| `require()` function | ✅ | Both interpreter and compiled modes. Loads .cjs/.js files, follows package.json `type` field, supports circular requires with partial exports, throws `MODULE_NOT_FOUND` on missing modules (catchable via try/catch). Compiled mode requires string-literal specifiers — non-literals are a `SHARPTS_CJS001` compile error. |
| `module.exports` | ✅ | Both modes. `module.exports = X` and `exports.foo = X` patterns supported. Reassignment semantics match Node. Compiled mode lowers `module.exports`/`exports` to direct static field accesses on the per-file `$Module_X` class. |
| `exports` shorthand | ✅ | Both modes. |
| ESM imports CJS | ✅ | `import x from './file.cjs'`, named imports, and namespace imports all work; CJS exports are typed as `any`. |
| `.cjs` extension | ✅ | Always treated as CommonJS regardless of nearest package.json. |
| `.mjs` extension | ✅ | Always treated as ES module. |
| package.json `type` field | ✅ | `"module"` → ESM, `"commonjs"` or absent → CJS for `.js` files. |
| **Resolution** | | |
| Relative paths | ✅ | `./foo`, `../bar` |
| Bare specifiers | ✅ | `node_modules` lookup |
| Directory index | ✅ | Looks for `index.ts` |
| Extension inference | ✅ | Adds `.ts` automatically |
| Circular detection | ✅ | With error reporting |
| `/// <reference>` | ✅ | Triple-slash references |
| `package.json` exports | ✅ | Subpath exports, wildcard patterns, `main`/`types`/`typings` fallback, .js→.ts extension mapping |
| Conditional exports | ✅ | Conditions: `types`, `import`, `default`; nested conditions; array fallbacks; null restrictions |
| Subpath imports (`#`) | ✅ | `"imports"` field in package.json with `#`-prefixed specifiers |
| Self-referencing | ✅ | Package imports itself by name through its own `exports` |
| Scoped packages | ✅ | `@scope/pkg` and `@scope/pkg/subpath` resolution |

---

## 11. GLOBALS

| Feature | Status | Notes |
|---------|--------|-------|
| `globalThis` | ✅ | ES2020 global reference |
| `process` | ✅ | Available globally |
| `console` | ✅ | `console.log` and variants |
| `setTimeout` / `clearTimeout` | ✅ | |
| `setInterval` / `clearInterval` | ✅ | |
| `__dirname` | ✅ | Directory of current module |
| `__filename` | ✅ | Full path of current module |
| `require` | ✅ | Available globally in both interpreter and compiled modes. Compiled mode requires string-literal specifiers (non-literals are a compile error). |
| `module` | ✅ | Per-module CJS binding (matches Node — not on `globalThis`). Interpreter binds in module scope; compiled mode lowers `module.exports` to the per-file `$Module_X.exports` static field. |
| `exports` | ✅ | Per-module CJS binding (matches Node — not on `globalThis`). Same lowering as `module.exports` in compiled mode. |
| `Buffer` | ✅ | Full Buffer class available globally |
| `global` | ⚠️ | Use `globalThis` |

---

## 12. STREAMS

| Feature | Status | Notes |
|---------|--------|-------|
| **Readable** | | |
| `new Readable()` | ✅ | Constructor with options |
| `push(chunk)` | ✅ | Add data to buffer; null signals EOF |
| `read()` | ✅ | Read from buffer |
| `pipe(dest)` | ✅ | Pipe to Writable/Duplex, returns destination |
| `unpipe(dest)` | ✅ | Remove pipe destination |
| `readable` | ✅ | Property: stream is readable |
| `readableEnded` | ✅ | Property: EOF reached |
| `readableLength` | ✅ | Property: buffer size |
| `'end'` event | ✅ | Emitted when EOF reached |
| **Writable** | | |
| `new Writable()` | ✅ | Constructor with write callback option |
| `write(chunk)` | ✅ | Write data, invokes write callback |
| `end()` | ✅ | Signal end of writes |
| `cork()` / `uncork()` | ✅ | Buffer writes |
| `writable` | ✅ | Property: stream is writable |
| `writableEnded` | ✅ | Property: end() called |
| `writableFinished` | ✅ | Property: all data flushed |
| `'finish'` event | ✅ | Emitted when all writes complete |
| **Duplex** | | |
| `new Duplex()` | ✅ | Readable + Writable combined |
| All Readable methods | ✅ | Inherited |
| All Writable methods | ✅ | Added |
| **Transform** | | |
| `new Transform()` | ✅ | Constructor with transform callback |
| `transform(chunk, enc, cb)` | ✅ | Transform callback; cb(null, data) pushes |
| All Duplex methods | ✅ | Inherited |
| **PassThrough** | | |
| `new PassThrough()` | ✅ | Transform that passes data unchanged |
| **Process Streams** | | |
| `process.stdout` | ✅ | Full Writable stream: write(), end(), cork(), uncork(), on/once/off events, writable/writableEnded/writableFinished properties, isTTY |
| `process.stderr` | ✅ | Full Writable stream: write(), end(), cork(), uncork(), on/once/off events, writable/writableEnded/writableFinished properties, isTTY |
| `process.stdin` | ✅ | Full Readable stream: on('data'), on('end'), read(), pause(), resume(), pipe(), setEncoding(), readable/readableEnded properties, isTTY; background Console.In reader thread |
| **Flowing Mode** | | |
| Auto-flowing on `data` listener | ✅ | Enters flowing mode when 'data' listener added |
| `pause()` / `resume()` | ✅ | Flow control with buffer draining on resume |
| `readableFlowing` property | ✅ | null/false/true states |
| Pipe backpressure | ✅ | Pauses source on writable backpressure, resumes on drain |
| **Object Mode** | | |
| Object mode | ✅ | `objectMode: true` option; streams accept any JS value (both interpreter and compiled modes) |
| `readableObjectMode` | ✅ | Property: whether readable side is in object mode |
| `writableObjectMode` | ✅ | Property: whether writable side is in object mode |
| **Not Implemented** | | |
| highWaterMark enforcement | ✅ | push() returns false at threshold; write() byte-based backpressure; pipe pauses/resumes on drain |

---

## 13. EVENTS

| Feature | Status | Notes |
|---------|--------|-------|
| `EventEmitter` class | ✅ | Full implementation |
| `on()` / `addListener()` | ✅ | |
| `once()` | ✅ | |
| `emit()` | ✅ | |
| `removeListener()` / `off()` | ✅ | |
| `removeAllListeners()` | ✅ | |
| `listenerCount()` | ✅ | |
| `listeners()` | ✅ | |
| `eventNames()` | ✅ | |
| `prependListener()` | ✅ | |
| `prependOnceListener()` | ✅ | |
| `defaultMaxListeners` | ✅ | Static property |

---

## 14. ZLIB (Compression)

| Feature | Status | Notes |
|---------|--------|-------|
| **Gzip** | | |
| `gzipSync` | ✅ | Compress using gzip |
| `gunzipSync` | ✅ | Decompress gzip data |
| **Deflate** | | |
| `deflateSync` | ✅ | Compress with zlib header |
| `inflateSync` | ✅ | Decompress zlib data |
| `deflateRawSync` | ✅ | Compress without header |
| `inflateRawSync` | ✅ | Decompress raw deflate |
| **Brotli** | | |
| `brotliCompressSync` | ✅ | Brotli compression |
| `brotliDecompressSync` | ✅ | Brotli decompression |
| **Zstd** | | |
| `zstdCompressSync` | ✅ | Zstandard compression |
| `zstdDecompressSync` | ✅ | Zstandard decompression |
| **Utilities** | | |
| `unzipSync` | ✅ | Auto-detect and decompress |
| `constants` | ✅ | Compression constants object |
| **Options** | | |
| `level` | ✅ | Compression level (0-9) |
| `chunkSize` | ✅ | Buffer size for streaming |
| `maxOutputLength` | ✅ | Maximum output size limit |
| `windowBits` | ⚠️ | Not directly supported in .NET |
| `memLevel` | ⚠️ | Not directly supported in .NET |
| `strategy` | ⚠️ | Not directly supported in .NET |
| **Async Callback APIs** | | |
| `gzip` / `gunzip` | ✅ | Callback-based async (interpreter mode) |
| `deflate` / `inflate` | ✅ | Callback-based async (interpreter mode) |
| `deflateRaw` / `inflateRaw` | ✅ | Callback-based async (interpreter mode) |
| `brotliCompress` / `brotliDecompress` | ✅ | Callback-based async (interpreter mode) |
| `unzip` | ✅ | Callback-based async with auto-detect (interpreter mode) |
| **Streaming APIs (Transform)** | | |
| `createGzip` / `createGunzip` | ✅ | Returns Transform stream; true streaming compression, accumulate-then-decompress |
| `createDeflate` / `createInflate` | ✅ | Returns Transform stream; zlib header format |
| `createDeflateRaw` / `createInflateRaw` | ✅ | Returns Transform stream; raw deflate format |
| `createBrotliCompress` / `createBrotliDecompress` | ✅ | Returns Transform stream; Brotli format |
| `createUnzip` | ✅ | Returns Transform stream; auto-detects gzip/deflate/raw |

---

## 15. TIMERS

| Feature | Status | Notes |
|---------|--------|-------|
| **Timeout** | | |
| `setTimeout()` | ✅ | Schedule callback after delay |
| `clearTimeout()` | ✅ | Cancel scheduled timeout |
| **Interval** | | |
| `setInterval()` | ✅ | Schedule repeating callback |
| `clearInterval()` | ✅ | Cancel interval |
| **Immediate** | | |
| `setImmediate()` | ✅ | Schedule callback for next tick |
| `clearImmediate()` | ✅ | Cancel immediate |
| **Module Import** | | |
| `import { setTimeout } from 'timers'` | ✅ | Named imports |
| `import * as timers from 'timers'` | ✅ | Namespace import |

---

## 16. TIMERS/PROMISES

| Feature | Status | Notes |
|---------|--------|-------|
| **Import** | | |
| `import { setTimeout } from 'timers/promises'` | ✅ | Named import |
| `import * as timers from 'timers/promises'` | ✅ | Namespace import |
| `import { setTimeout } from 'node:timers/promises'` | ✅ | Node prefix import |
| **Methods** | | |
| `setTimeout(delay?, value?, options?)` | ✅ | Returns `Promise<T>` that resolves with value after delay ms; supports `options.signal` |
| `setImmediate(value?, options?)` | ✅ | Returns `Promise<T>` that resolves with value immediately; supports `options.signal` |
| `setInterval(delay?, value?, options?)` | ✅ | Returns an async iterable for `for await...of`; supports `options.signal` (interpreter mode) |
| **AbortSignal Support** | | |
| `setTimeout` with `options.signal` | ✅ | Throws AbortError on pre-aborted signal or mid-delay abort (both modes) |
| `setImmediate` with `options.signal` | ✅ | Throws AbortError on pre-aborted signal (both modes) |
| `setInterval` with `options.signal` | ✅ | Throws AbortError on pre-abort; iterator ends cleanly on mid-iteration abort (both modes) |
| **Not Implemented** | | |
| `options.ref` | ❌ | Timer ref/unref control |
| `scheduler.wait()` / `scheduler.yield()` | ❌ | Scheduler API |

---

## 17. STRING_DECODER

| Feature | Status | Notes |
|---------|--------|-------|
| **Constructor** | | |
| `new StringDecoder()` | ✅ | Default encoding: utf8 |
| `new StringDecoder(encoding)` | ✅ | utf8, utf-8, utf16le, ucs2, latin1, ascii |
| **Methods** | | |
| `write(buffer)` | ✅ | Decode buffer, handle partial sequences |
| `end()` | ✅ | Flush remaining bytes |
| `end(buffer)` | ✅ | Write final buffer and flush |
| **Properties** | | |
| `encoding` | ✅ | Returns normalized encoding name |
| **Multi-byte Handling** | | |
| UTF-8 sequences | ✅ | Properly buffers incomplete sequences |
| UTF-16LE pairs | ✅ | Handles byte alignment |

---

## 17. PERF_HOOKS

| Feature | Status | Notes |
|---------|--------|-------|
| **performance** | | |
| `performance.now()` | ✅ | High-resolution monotonic timestamp |
| `performance.timeOrigin` | ✅ | Unix timestamp when process started |
| `performance.mark(name, options?)` | ✅ | Creates PerformanceMark entry; options.startTime supported |
| `performance.measure(name, start?, end?)` | ✅ | Creates PerformanceMeasure between marks |
| `performance.getEntries()` | ✅ | Returns all performance entries |
| `performance.getEntriesByName(name, type?)` | ✅ | Filter entries by name, optionally by type |
| `performance.getEntriesByType(type)` | ✅ | Filter entries by 'mark' or 'measure' |
| `performance.clearMarks(name?)` | ✅ | Clear all marks or by name |
| `performance.clearMeasures(name?)` | ✅ | Clear all measures or by name |
| **PerformanceObserver** | | |
| `new PerformanceObserver(callback)` | ✅ | Create observer with callback |
| `observer.observe({ entryTypes })` | ✅ | Start observing specified entry types |
| `observer.disconnect()` | ✅ | Stop receiving notifications |
| **Import** | | |
| `import { performance } from 'perf_hooks'` | ✅ | Named import |
| `import { PerformanceObserver } from 'perf_hooks'` | ✅ | Named import |
| `import * as perf from 'perf_hooks'` | ✅ | Namespace import |

---

## 18. BUFFER

| Feature | Status | Notes |
|---------|--------|-------|
| **Static Methods** | | |
| `Buffer.from()` | ✅ | From string, array, or Buffer |
| `Buffer.alloc()` | ✅ | Zero-filled allocation |
| `Buffer.allocUnsafe()` | ✅ | Uninitialized allocation |
| `Buffer.concat()` | ✅ | Concatenate multiple buffers |
| `Buffer.isBuffer()` | ✅ | Type check |
| `Buffer.byteLength()` | ✅ | String byte length |
| `Buffer.compare()` | ✅ | Static comparison |
| `Buffer.isEncoding()` | ✅ | Encoding validation |
| **Instance Properties** | | |
| `length` | ✅ | Buffer byte length |
| **Instance Methods** | | |
| `toString()` | ✅ | With encoding support |
| `slice()` | ✅ | Create view/copy |
| `copy()` | ✅ | Copy to target buffer |
| `compare()` | ✅ | Compare with other buffer |
| `equals()` | ✅ | Equality check |
| `fill()` | ✅ | Fill with value/string |
| `write()` | ✅ | Write string at offset |
| `readUInt8()` | ✅ | Read unsigned byte |
| `writeUInt8()` | ✅ | Write unsigned byte |
| `toJSON()` | ✅ | Serialize to {type, data} |
| **Multi-byte Reads** | | |
| `readUInt16LE/BE()` | ✅ | Unsigned 16-bit |
| `readUInt32LE/BE()` | ✅ | Unsigned 32-bit |
| `readInt8()` | ✅ | Signed 8-bit |
| `readInt16LE/BE()` | ✅ | Signed 16-bit |
| `readInt32LE/BE()` | ✅ | Signed 32-bit |
| `readBigInt64LE/BE()` | ✅ | Signed 64-bit BigInt |
| `readBigUInt64LE/BE()` | ✅ | Unsigned 64-bit BigInt |
| `readFloatLE/BE()` | ✅ | 32-bit float |
| `readDoubleLE/BE()` | ✅ | 64-bit double |
| **Multi-byte Writes** | | |
| `writeUInt16LE/BE()` | ✅ | Unsigned 16-bit |
| `writeUInt32LE/BE()` | ✅ | Unsigned 32-bit |
| `writeInt8()` | ✅ | Signed 8-bit |
| `writeInt16LE/BE()` | ✅ | Signed 16-bit |
| `writeInt32LE/BE()` | ✅ | Signed 32-bit |
| `writeBigInt64LE/BE()` | ✅ | Signed 64-bit BigInt |
| `writeBigUInt64LE/BE()` | ✅ | Unsigned 64-bit BigInt |
| `writeFloatLE/BE()` | ✅ | 32-bit float |
| `writeDoubleLE/BE()` | ✅ | 64-bit double |
| **Search & Swap** | | |
| `indexOf()` | ✅ | Find first occurrence |
| `includes()` | ✅ | Check if value exists |
| `swap16/32/64()` | ✅ | Byte order swapping |

---

## 19. WEB APIs

| Feature | Status | Notes |
|---------|--------|-------|
| **fetch()** | | |
| `fetch(url)` | ✅ | Basic GET request |
| `fetch(url, options)` | ✅ | With request configuration |
| **Request Options** | | |
| `method` | ✅ | GET, POST, PUT, DELETE, PATCH, etc. |
| `headers` | ✅ | Custom request headers as object |
| `body` | ✅ | String request body |
| **Response Object** | | |
| `status` | ✅ | HTTP status code |
| `statusText` | ✅ | HTTP status message |
| `ok` | ✅ | True if status 200-299 |
| `url` | ✅ | Final URL after redirects |
| `headers` | ✅ | Response headers as object |
| **Response Methods** | | |
| `json()` | ✅ | Parse body as JSON (returns Promise) |
| `text()` | ✅ | Get body as string (returns Promise) |
| `arrayBuffer()` | ✅ | Get body as ArrayBuffer (returns Promise) |
| **Async Support** | | |
| `await fetch(...)` | ✅ | Full async/await support |
| Promise chaining | ✅ | `.then()` style |
| `response.body` | ✅ | Readable stream (body eagerly loaded, streamed via Readable) |
| `Headers` class | ✅ | Constructable `new Headers(init?)` with get/set/has/delete/append/forEach/entries/keys/values |
| `AbortController` / `signal` option | ✅ | `new AbortController()`, `signal.aborted`, `abort(reason?)`, `throwIfAborted()`, `AbortSignal.abort()`/`timeout()`/`any()`; fetch `signal` option with pre-abort check and cancellation |
| **Not Implemented** | | |
| `Request` class | ✅ | `new Request(url, init?)` with method, headers, body, clone(); json(), text(), arrayBuffer() |
| `Response` class | ✅ | `new Response(body?, init?)` with status, statusText, ok, headers, clone(); json(), text(), arrayBuffer(); static json(), redirect(), error() |
| `credentials` option | ✅ | `'omit'`, `'same-origin'` (default, matches Node/undici), `'include'`. Backed by a process-wide cookie jar via `System.Net.CookieContainer` — handles RFC 6265 parsing, domain/path matching, expiry, Secure/HttpOnly. Each compiled DLL has its own jar. |
| `redirect` option | ✅ | `follow` (default), `manual` (return 3xx), `error` (throw on redirect) |
| **Cookie jar** | | |
| `fetch.cookieJar.getCookies(url)` | ✅ | Returns the `Cookie:` header that would be sent for `url`. SharpTS extension. |
| `fetch.cookieJar.setCookie(cookie, url)` | ✅ | Manually inject a cookie into the jar as if received from `url`. SharpTS extension. |
| `fetch.cookieJar.clear()` | ✅ | Removes all cookies from the jar. SharpTS extension. |
| `headers.getSetCookie()` | ✅ | Returns all `Set-Cookie` values as an array (WHATWG spec). |
| `headers.get('set-cookie')` | ✅ | Returns first cookie value (WHATWG spec). |
| Cookies persisted to disk | ❌ | Process-only; lost on exit. |
| `http.Agent` cookies option | ❌ | Cookies are jar-wide; per-Agent jars deferred. |

---

## 20. DNS

| Feature | Status | Notes |
|---------|--------|-------|
| **Methods** | | |
| `lookup` | ✅ | Resolve hostname to IP; supports family and all options |
| `lookupService` | ✅ | Reverse lookup (IP to hostname) |
| **Constants** | | |
| `ADDRCONFIG` | ✅ | Address configuration hint |
| `V4MAPPED` | ✅ | Map IPv4 to IPv6 hint |
| `ALL` | ✅ | Return all addresses hint |
| **Async Resolution** | | |
| `resolve` | ✅ | Async callback-based; supports rrtype parameter (A, AAAA, MX, TXT, SRV, CNAME, NS, SOA, PTR, CAA, NAPTR) |
| `resolve4` | ✅ | Async callback-based; resolves IPv4 addresses |
| `resolve6` | ✅ | Async callback-based; resolves IPv6 addresses |
| `reverse` | ✅ | Async callback-based; reverse DNS lookup |
| `resolveMx` | ✅ | MX records → `[{ exchange, priority }]` |
| `resolveTxt` | ✅ | TXT records → `string[][]` (chunks per record) |
| `resolveSrv` | ✅ | SRV records → `[{ name, port, priority, weight }]` |
| `resolveCname` | ✅ | CNAME records → `string[]` |
| `resolveNs` | ✅ | NS records → `string[]` |
| `resolveSoa` | ✅ | SOA record → `{ nsname, hostmaster, serial, refresh, retry, expire, minttl }` |
| `resolvePtr` | ✅ | PTR records → `string[]` |
| `resolveCaa` | ✅ | CAA records → `[{ critical, issue/issuewild/iodef }]` |
| `resolveNaptr` | ✅ | NAPTR records → `[{ flags, service, regexp, replacement, order, preference }]` |
| **Promise API** | | |
| `dns/promises` | ✅ | Promise-based: all callback methods available as promise variants |
| `dns.promises` | ✅ | Sub-module access to promise API |
| **Resolver** | | |
| `Resolver` class | ✅ | `new dns.Resolver()` with configurable servers |
| `resolver.setServers()` | ✅ | Set custom DNS servers (IP addresses with optional port) |
| `resolver.getServers()` | ✅ | Get configured DNS server list |
| `resolver.resolve()` | ✅ | All resolve methods use configured servers |
| `resolver.cancel()` | ⚠️ | No-op (no cancellation tracking) |

---

## 21. HTTP

| Feature | Status | Notes |
|---------|--------|-------|
| **Server** | | |
| `createServer` | ✅ | Create HTTP server with request handler |
| `server.listen()` | ✅ | Start listening on port |
| `server.close()` | ✅ | Stop server |
| **Client** | | |
| `request` | ✅ | Make HTTP request (delegates to fetch) |
| `get` | ✅ | Shorthand for GET requests |
| **Constants** | | |
| `METHODS` | ✅ | Array of supported HTTP methods |
| `STATUS_CODES` | ✅ | Map of status codes to messages |
| `globalAgent` | ✅ | Global HTTP agent (SharpTSAgent singleton with full Agent API) |
| **IncomingMessage** | | |
| Readable stream methods | ✅ | on, pipe, read, pause, resume, push (extends Readable) |
| `method`, `url`, `headers` | ✅ | Request properties |
| `httpVersion` | ✅ | Protocol version |
| `rawHeaders` | ✅ | Alternating [name, value] array |
| `complete` | ✅ | Whether body has been fully read |
| **ServerResponse** | | |
| Writable stream methods | ✅ | on, write, end, cork, uncork (extends Writable) |
| `writeHead()` | ✅ | Set status code, message, and headers |
| `setHeader()` / `getHeader()` | ✅ | Individual header management |
| `hasHeader()` / `removeHeader()` | ✅ | Header inspection and removal |
| `getHeaderNames()` | ✅ | List all set header names |
| `flushHeaders()` | ✅ | Send headers immediately |
| `statusCode` / `statusMessage` | ✅ | Readable/writable properties |
| `headersSent` / `finished` | ✅ | State properties |
| `finish` / `close` events | ✅ | Writable stream events on end |
| **Agent** | | |
| `Agent` class | ✅ | `new http.Agent(options?)` constructor with keepAlive, maxSockets, maxTotalSockets, maxFreeSockets, keepAliveMsecs, timeout, scheduling |
| `agent.destroy()` | ✅ | Marks agent as destroyed |
| `agent.getName(options?)` | ✅ | Returns pool key string (host:port:localAddress:family) |
| `agent.createConnection()` | ✅ | Stub (connection pooling handled by .NET HttpClient) |
| `agent.sockets/freeSockets/requests` | ✅ | Empty objects (pooling managed internally by .NET) |

---

## 22. NET (TCP)

| Feature | Status | Notes |
|---------|--------|-------|
| **Server** | | |
| `createServer(options?, listener?)` | ✅ | Create TCP server with optional connection listener |
| `server.listen(port, host?, callback?)` | ✅ | Start listening on port; supports port 0 for auto-assign |
| `server.close(callback?)` | ✅ | Stop accepting new connections |
| `server.address()` | ✅ | Returns { address, family, port } |
| `server.getConnections(callback)` | ✅ | Get number of concurrent connections |
| `server.listening` | ✅ | Whether server is listening |
| `server.maxConnections` | ✅ | Limit concurrent connections |
| Server events | ✅ | 'connection', 'listening', 'close', 'error' |
| **Socket** | | |
| `createConnection(options, listener?)` | ✅ | Create client socket and connect |
| `connect(options, listener?)` | ✅ | Alias for createConnection |
| `socket.write(data, encoding?, callback?)` | ✅ | Write data to socket |
| `socket.end(data?, encoding?, callback?)` | ✅ | Half-close the socket |
| `socket.destroy(error?)` | ✅ | Fully close and clean up |
| `socket.setEncoding(encoding)` | ✅ | Set string encoding for data events |
| `socket.setTimeout(timeout, callback?)` | ✅ | Set socket timeout |
| `socket.setNoDelay(noDelay?)` | ✅ | Disable Nagle's algorithm |
| `socket.setKeepAlive(enable?, delay?)` | ✅ | Enable/disable keep-alive |
| `socket.address()` | ✅ | Local address info |
| `socket.pause()` / `resume()` | ✅ | Flow control |
| `socket.pipe(dest)` | ✅ | Pipe to writable stream |
| Socket properties | ✅ | remoteAddress, remotePort, remoteFamily, localAddress, localPort, bytesRead, bytesWritten, connecting, destroyed, readyState |
| Socket events | ✅ | 'connect', 'data', 'end', 'close', 'error', 'drain', 'timeout' |
| **Utilities** | | |
| `isIP(input)` | ✅ | Returns 4 (IPv4), 6 (IPv6), or 0 (invalid) |
| `isIPv4(input)` | ✅ | True if valid IPv4 address |
| `isIPv6(input)` | ✅ | True if valid IPv6 address |
| **IPC** | | |
| IPC sockets | ✅ | Named pipes (Windows) / Unix domain sockets (Linux/macOS); `server.listen(path)`, `createConnection({path})`, `remoteFamily='pipe'`, error codes (ENOENT/ECONNREFUSED) |
| `socket.ref()` / `unref()` | ⚠️ | Basic support via event loop ref counting |

---

## 23. TLS (SSL)

| Feature | Status | Notes |
|---------|--------|-------|
| **Server** | | |
| `createServer(options?, listener?)` | ✅ | Create TLS server with cert/key options |
| `server.listen(port, host?, callback?)` | ✅ | Start listening; requires key+cert |
| `server.close(callback?)` | ✅ | Stop accepting new connections |
| `server.address()` | ✅ | Returns { address, family, port } |
| `server.getConnections(callback)` | ✅ | Get number of concurrent connections |
| `server.listening` | ✅ | Whether server is listening |
| Server events | ✅ | 'secureConnection', 'tlsClientError', 'listening', 'close', 'error' |
| **TLSSocket** | | |
| `connect(port, host?, options?, callback?)` | ✅ | Create TLS client connection |
| `connect(options, callback?)` | ✅ | Options-based connect variant |
| `socket.authorized` | ✅ | Whether peer certificate was verified |
| `socket.encrypted` | ✅ | Always true for TLS sockets |
| `socket.alpnProtocol` | ✅ | Negotiated ALPN protocol |
| `socket.getCipher()` | ✅ | Returns { name, standardName, version } |
| `socket.getPeerCertificate()` | ✅ | Returns { subject, issuer, valid_from, valid_to, serialNumber } |
| `socket.getProtocol()` | ✅ | Returns 'TLSv1.2' or 'TLSv1.3' |
| All net.Socket methods | ✅ | Inherits write, end, destroy, setEncoding, etc. |
| TLSSocket events | ✅ | 'secureConnect' + all net.Socket events |
| **Module Functions** | | |
| `createSecureContext(options?)` | ✅ | Create reusable secure context |
| `DEFAULT_MIN_VERSION` | ✅ | 'TLSv1.2' |
| `DEFAULT_MAX_VERSION` | ✅ | 'TLSv1.3' |
| **Advanced** | | |
| ALPN negotiation | ✅ | `ALPNProtocols` option on client and server; `socket.alpnProtocol` returns negotiated protocol |
| Client certificate auth | ⚠️ | requestCert option exists but limited |
| SNI callback | ✅ | `SNICallback` option on server; receives hostname, returns `{ cert, key }` for dynamic cert selection |

---

## 24. DGRAM (UDP)

| Feature | Status | Notes |
|---------|--------|-------|
| **Socket Creation** | | |
| `createSocket(type)` | ✅ | 'udp4' or 'udp6' |
| `createSocket(options, callback?)` | ✅ | Options with `type` field |
| **Socket Methods** | | |
| `socket.bind(port?, address?, callback?)` | ✅ | Bind to local address; port 0 for auto-assign |
| `socket.send(msg, port, address?, callback?)` | ✅ | Send datagram; string or Buffer |
| `socket.send(msg, offset, length, port, address?, callback?)` | ✅ | Send with offset/length |
| `socket.close(callback?)` | ✅ | Close socket |
| `socket.address()` | ✅ | Returns { address, family, port } |
| `socket.setBroadcast(flag)` | ✅ | Enable/disable broadcast |
| `socket.setTTL(ttl)` | ✅ | Set IP TTL |
| `socket.setMulticastTTL(ttl)` | ✅ | Set multicast TTL |
| `socket.addMembership(addr, iface?)` | ✅ | Join multicast group |
| `socket.dropMembership(addr)` | ✅ | Leave multicast group |
| `socket.ref()` / `unref()` | ✅ | Event loop ref counting |
| **Events** | | |
| `'message'` | ✅ | `(msg: Buffer, rinfo: { address, family, port, size })` |
| `'listening'` | ✅ | Emitted after bind completes |
| `'close'` | ✅ | Emitted after socket closed |
| `'error'` | ✅ | Emitted on error |
| **EventEmitter** | ✅ | on, once, off, emit, removeListener, etc. |
| **Connected Mode** | | |
| `socket.connect(port, address?)` | ✅ | Connected UDP mode; emits 'connect' event |
| `socket.disconnect()` | ✅ | Disconnect from remote address |
| `socket.remoteAddress()` | ✅ | Returns `{ address, family, port }` for connected socket |
| `socket.getRecvBufferSize()` / `setRecvBufferSize()` | ✅ | Receive buffer size control |
| `socket.getSendBufferSize()` / `setSendBufferSize()` | ✅ | Send buffer size control |

---

## 25. WORKER_THREADS

| Feature | Status | Notes |
|---------|--------|-------|
| **Worker Creation** | | |
| `Worker` constructor | ✅ | Create worker from script file |
| `workerData` | ✅ | Pass data to worker |
| **Thread Identity** | | |
| `isMainThread` | ✅ | Check if running on main thread |
| `threadId` | ✅ | Current thread identifier |
| **Messaging** | | |
| `parentPort` | ✅ | Port for worker-to-parent communication |
| `MessageChannel` | ✅ | Create connected port pairs |
| `receiveMessageOnPort` | ✅ | Sync message receive |
| `postMessage` | ✅ | Send messages between threads |
| **Environment** | | |
| `getEnvironmentData` | ✅ | Get shared environment data |
| `setEnvironmentData` | ✅ | Set shared environment data |
| `SHARE_ENV` | ⚠️ | Symbol exists, env sharing not supported |
| **Utilities** | | |
| `markAsUntransferable` | ⚠️ | No-op (no transferability tracking) |
| **Not Implemented** | | |
| `moveMessagePortToContext` | ❌ | Requires VM module |
| `resourceLimits` | ❌ | No resource limiting |
| `BroadcastChannel` | ❌ | |

---

## 26. VM

| Feature | Status | Notes |
|---------|--------|-------|
| **Static Methods** | | |
| `vm.runInNewContext(code, ctx?, opts?)` | ✅ | Executes code in fresh isolated context; context object properties seeded as variables; mutations written back |
| `vm.runInThisContext(code, opts?)` | ✅ | Executes code in caller's scope (interpreter mode) |
| `vm.createContext(obj?)` | ✅ | Tags object as vm context; creates empty context if no arg |
| `vm.isContext(obj)` | ✅ | Returns whether object was contextified |
| **Script Class** | | |
| `new vm.Script(code, opts?)` | ✅ | Pre-parses code for repeated execution (interpreter mode) |
| `script.runInNewContext(ctx?, opts?)` | ✅ | Runs pre-parsed script in fresh context (interpreter mode) |
| `script.runInThisContext(opts?)` | ✅ | Runs pre-parsed script in caller's scope (interpreter mode) |
| `script.runInContext(ctx, opts?)` | ✅ | Runs pre-parsed script in given context (interpreter mode) |
| `vm.compileFunction(code, params?, opts?)` | ✅ | Compiles function body with named params; parsingContext, contextExtensions options |
| **Not Implemented** | | |
| `vm.Module` / `vm.SourceTextModule` | ❌ | Experimental in Node.js |
| `timeout` option | ✅ | Execution timeout in ms; throws Error on expiry; checked per-statement and per-loop-iteration |
| `vm.measureMemory` | ❌ | V8-specific, not applicable |

---

## Summary

SharpTS provides comprehensive support for file system operations (sync, callback-based async, and promise-based via `fs/promises`), including file descriptor APIs, directory utilities, hard/symbolic links, permissions, file watching (`watch`, `watchFile`, `unwatchFile`), and streaming (`createReadStream`, `createWriteStream`). Also includes path manipulation, OS information, process management, crypto (hashing, encryption, key derivation, signing), URL parsing, binary data handling via Buffer, EventEmitter for event-driven patterns, timers (setTimeout/setInterval/setImmediate), string decoding for multi-byte characters, high-resolution performance timing, stream classes (Readable, Writable, Duplex, Transform, PassThrough) with flowing mode (auto-flowing on `data` listener, pause/resume, pipe backpressure), the Web Fetch API for HTTP client requests with AbortController support, HTTP/HTTPS servers via `http.createServer`/`https.createServer`, TLS/SSL via `tls` module, TCP via `net` module, UDP via `dgram` module (including connected mode), DNS resolution (full record type support), cluster module for multi-process scaling, and Worker Threads for parallel execution. The module system supports both ES modules and CommonJS import syntax.

**Key Gaps:**
- None — all major Node.js APIs are implemented

**Recommended Workarounds:**
- Use ES module syntax instead of `require()`
- Use `fetch()` for HTTP client requests (simpler than http.request)

---

## Recommended Next Steps

Priority features to implement for broader Node.js compatibility:

1. ~~**package.json exports**~~ ✅ Implemented: subpath exports, conditional exports, wildcard patterns, self-referencing, subpath imports
2. ~~**AsyncLocalStorage / async_hooks**~~ ✅ Implemented: AsyncLocalStorage with run, getStore, enterWith, exit, disable; async context propagation across await/Promise.then
3. ~~**IPC sockets**~~ ✅ Implemented: Named pipes (Windows) / Unix domain sockets (Linux/macOS) with error codes, server.address() returns pipe path
4. ~~**cluster HTTP port sharing**~~ ✅ Implemented: SharedTcpListener/SharedHttpListener with round-robin dispatch via atomic counter; server.listen() intercepted in worker mode
5. ~~**vm.timeout option**~~ ✅ Implemented: CancellationToken-based timeout checked per-statement and per-loop-iteration; throws Error on expiry
