# SharpTS Node.js Module Support Status

This document tracks Node.js module and API implementation status in SharpTS.

**Last Updated:** 2026-02-04 (Updated to reflect actual implementation status)

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
| `crypto` | ⚠️ | Hash, HMAC, Cipher, PBKDF2, scrypt, HKDF, RSA encrypt/decrypt, Sign/Verify, DH/ECDH, KeyPair, KeyObject |
| `url` | ✅ | WHATWG URL + legacy parse/format/resolve |
| `querystring` | ✅ | parse, stringify, escape, unescape |
| `assert` | ✅ | Full testing utilities |
| `child_process` | ✅ | execSync, spawnSync, exec, spawn with ChildProcess EventEmitter |
| `util` | ✅ | format, inspect, isDeepStrictEqual, parseArgs, toUSVString, stripVTControlCharacters, getSystemErrorName, getSystemErrorMap, promisify, types helpers, deprecate, callbackify, inherits, TextEncoder/TextDecoder |
| `console` | ✅ | log, error, warn, info, debug, clear, time/timeEnd/timeLog, assert, count/countReset, table, dir, group/groupEnd, trace |
| `readline` | ⚠️ | questionSync, createInterface |
| `events` | ✅ | EventEmitter with on/off/once/emit/removeListener |
| `stream` | ✅ | Readable, Writable, Duplex, Transform, PassThrough (sync mode) |
| `buffer` | ✅ | Full Buffer class with multi-byte LE/BE, float/double, BigInt, search, swap |
| `timers` | ✅ | setTimeout, setInterval, setImmediate + clear variants (module import) |
| `string_decoder` | ✅ | StringDecoder class for multi-byte character handling |
| `perf_hooks` | ✅ | performance.now(), performance.timeOrigin |
| `http` / `https` | ⚠️ | createServer, request, get; STATUS_CODES, METHODS (https uses http) |
| `net` | ❌ | No TCP/IPC sockets |
| `dns` | ⚠️ | lookup, lookupService (sync only) |
| `zlib` | ✅ | gzip, deflate, deflateRaw, brotli, zstd (sync APIs) |
| `worker_threads` | ⚠️ | Worker, MessageChannel, parentPort, workerData, isMainThread |
| `cluster` | ❌ | No cluster support |

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
| `pbkdf2` / `scrypt` | ❌ | Async versions - use sync versions |
| **Comparison** | | |
| `timingSafeEqual` | ✅ | Constant-time buffer comparison (prevents timing attacks) |
| **Signing** | | |
| `createSign` / `createVerify` | ✅ | RSA and EC keys; SHA1/256/384/512; hex/base64/Buffer output |
| **Key Generation** | | |
| `generateKeyPairSync` | ✅ | RSA (2048/4096) and EC (P-256/P-384/P-521); PEM format |
| `generateKeyPair` | ❌ | Async version - use sync version |
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
| `hkdf` | ❌ | Async version - use sync version |
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
| `execSync` | ✅ | With cwd, timeout, env, shell options |
| `spawnSync` | ✅ | With cwd, timeout, env options |
| `exec` | ✅ | Async with callback(error, stdout, stderr); returns ChildProcess EventEmitter |
| `spawn` | ✅ | Async; returns ChildProcess with stdout/stderr streams and events |
| `fork` | ❌ | |
| ChildProcess events | ✅ | close, exit, error events via EventEmitter |

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
| `require()` function | ❌ | Not as global function |
| `module.exports` | ❌ | Not manipulable |
| `exports` shorthand | ❌ | |
| **Resolution** | | |
| Relative paths | ✅ | `./foo`, `../bar` |
| Bare specifiers | ✅ | `node_modules` lookup |
| Directory index | ✅ | Looks for `index.ts` |
| Extension inference | ✅ | Adds `.ts` automatically |
| Circular detection | ✅ | With error reporting |
| `/// <reference>` | ✅ | Triple-slash references |
| `package.json` exports | ❌ | |
| Conditional exports | ❌ | |

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
| `require` | ❌ | Use `import` syntax |
| `module` | ❌ | |
| `exports` | ❌ | |
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
| `process.stdout.write()` | ✅ | Basic only |
| `process.stderr.write()` | ✅ | Basic only |
| `process.stdin` events | ❌ | No event-based input |
| **Flowing Mode** | | |
| Auto-flowing on `data` listener | ✅ | Enters flowing mode when 'data' listener added |
| `pause()` / `resume()` | ✅ | Flow control with buffer draining on resume |
| `readableFlowing` property | ✅ | null/false/true states |
| Pipe backpressure | ✅ | Pauses source on writable backpressure, resumes on drain |
| **Object Mode** | | |
| Object mode | ✅ | `objectMode: true` option; streams accept any JS value (interpreter mode; compiled mode pending) |
| `readableObjectMode` | ✅ | Property: whether readable side is in object mode |
| `writableObjectMode` | ✅ | Property: whether writable side is in object mode |
| **Not Implemented** | | |
| highWaterMark enforcement | ❌ | No read-side backpressure (push always succeeds) |

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
| **Async APIs** | | |
| `gzip` / `gunzip` | ❌ | Use sync versions |
| `deflate` / `inflate` | ❌ | Use sync versions |
| `brotliCompress` / `brotliDecompress` | ❌ | Use sync versions |
| **Streaming APIs** | | |
| `createGzip` / `createGunzip` | ❌ | No stream support |
| `createDeflate` / `createInflate` | ❌ | No stream support |

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

## 16. STRING_DECODER

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
| **Import** | | |
| `import { performance } from 'perf_hooks'` | ✅ | Named import |
| `import * as perf from 'perf_hooks'` | ✅ | Namespace import |
| **Not Implemented** | | |
| `PerformanceObserver` | ❌ | |
| `performance.mark()` | ❌ | |
| `performance.measure()` | ❌ | |
| `performance.getEntries()` | ❌ | |

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
| **Not Implemented** | | |
| `Request` class | ❌ | Use options object |
| `Response` class | ❌ | Only from fetch() return |
| `Headers` class | ❌ | Use plain objects |
| Streaming body | ❌ | Body fully loaded |
| `AbortController` | ❌ | No request cancellation |
| `credentials` option | ❌ | No cookie handling |
| `redirect` option | ❌ | Auto-follows redirects |

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
| **Not Implemented** | | |
| `resolve` | ❌ | Use lookup instead |
| `resolve4` / `resolve6` | ❌ | Use lookup with family option |
| `resolveMx` / `resolveTxt` | ❌ | MX/TXT record lookup |
| `reverse` | ❌ | Use lookupService instead |
| `Resolver` class | ❌ | Use module methods |
| Async callbacks | ❌ | Sync-only, returns directly |

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
| `globalAgent` | ✅ | Global HTTP agent object |
| **Not Implemented** | | |
| `Agent` class | ❌ | Connection pooling agent |
| `ClientRequest` events | ❌ | No event-based request lifecycle |
| `IncomingMessage` events | ❌ | No event-based response streaming |

---

## 22. WORKER_THREADS

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

## Summary

SharpTS provides comprehensive support for file system operations (sync, callback-based async, and promise-based via `fs/promises`), including file descriptor APIs, directory utilities, hard/symbolic links, permissions, file watching (`watch`, `watchFile`, `unwatchFile`), and streaming (`createReadStream`, `createWriteStream`). Also includes path manipulation, OS information, process management, crypto (hashing, encryption, key derivation, signing), URL parsing, binary data handling via Buffer, EventEmitter for event-driven patterns, timers (setTimeout/setInterval/setImmediate), string decoding for multi-byte characters, high-resolution performance timing, stream classes (Readable, Writable, Duplex, Transform, PassThrough) with flowing mode (auto-flowing on `data` listener, pause/resume, pipe backpressure), the Web Fetch API for HTTP client requests, basic HTTP server via `http.createServer`, DNS resolution (lookup/lookupService), and Worker Threads for parallel execution. The module system supports both ES modules and CommonJS import syntax.

**Key Gaps:**
- No net (TCP/IPC) sockets
- No cluster support
- HTTP server is basic (no full event lifecycle)
- Object mode streams: interpreter only (compiled mode pending)
- No highWaterMark enforcement on read-side backpressure

**Recommended Workarounds:**
- Use ES module syntax instead of `require()`
- Use `fetch()` for HTTP client requests (simpler than http.request)

---

## Recommended Next Steps

Priority features to implement for broader Node.js compatibility:

1. **net module** - TCP/IPC socket support (higher effort)
2. **Full HTTP server events** - Complete request/response lifecycle events (medium effort)
3. **cluster module** - Multi-process support (higher effort)
4. **Object mode streams** - Non-buffer chunk types for stream pipelines (medium effort)
