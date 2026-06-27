// Node.js 'fs' module — SharpTS embedded stdlib implementation.
// Target: Node.js 24.15.0. See https://nodejs.org/api/fs.html.
//
// Raw filesystem I/O stays in C# behind `primitive:fs` (sync ops, streams,
// watchers, constants) and `primitive:fs/promises` (promise-based async I/O).
// This file is the Node-shape facade:
//   - sync APIs forward straight to `primitive:fs`;
//   - the callback APIs (`fs.readFile(p, cb)`, …) are derived here in TS from
//     the promise primitives, so BOTH execution modes (interpreter + compiled)
//     get identical callback behaviour from one implementation — the compiled
//     path previously had no callback fs at all;
//   - `fs.promises` is assembled from the same promise primitives.
// Divergence from Node semantics lives in the primitives, not here.

import {
    // Existence / read / write
    existsSync as __existsSync,
    readFileSync as __readFileSync,
    writeFileSync as __writeFileSync,
    appendFileSync as __appendFileSync,
    // Delete
    unlinkSync as __unlinkSync,
    // Directories
    mkdirSync as __mkdirSync,
    rmdirSync as __rmdirSync,
    readdirSync as __readdirSync,
    // Metadata
    statSync as __statSync,
    lstatSync as __lstatSync,
    // Move / copy
    renameSync as __renameSync,
    copyFileSync as __copyFileSync,
    // Access / permissions
    accessSync as __accessSync,
    chmodSync as __chmodSync,
    chownSync as __chownSync,
    lchownSync as __lchownSync,
    // Truncate / links / times
    truncateSync as __truncateSync,
    symlinkSync as __symlinkSync,
    readlinkSync as __readlinkSync,
    realpathSync as __realpathSync,
    utimesSync as __utimesSync,
    // File-descriptor APIs
    openSync as __openSync,
    closeSync as __closeSync,
    readSync as __readSync,
    writeSync as __writeSync,
    fstatSync as __fstatSync,
    ftruncateSync as __ftruncateSync,
    // Directory utilities / hard links
    mkdtempSync as __mkdtempSync,
    opendirSync as __opendirSync,
    linkSync as __linkSync,
    // Stream factories
    createReadStream as __createReadStream,
    createWriteStream as __createWriteStream,
    // Watch APIs
    watch as __watch,
    watchFile as __watchFile,
    unwatchFile as __unwatchFile,
    // Constants
    constants as __constants,
} from 'primitive:fs';

import {
    readFile as __pReadFile,
    writeFile as __pWriteFile,
    appendFile as __pAppendFile,
    stat as __pStat,
    lstat as __pLstat,
    unlink as __pUnlink,
    mkdir as __pMkdir,
    rmdir as __pRmdir,
    rm as __pRm,
    readdir as __pReaddir,
    rename as __pRename,
    copyFile as __pCopyFile,
    access as __pAccess,
    chmod as __pChmod,
    truncate as __pTruncate,
    utimes as __pUtimes,
    readlink as __pReadlink,
    realpath as __pRealpath,
    symlink as __pSymlink,
    link as __pLink,
    mkdtemp as __pMkdtemp,
} from 'primitive:fs/promises';

// ---------------------------------------------------------------------------
// Callback derivation helpers.
//
// Node's callback APIs are `(err, data?) => void`. We forward to the promise
// primitive and bridge its resolution/rejection back to the callback. The
// primitive returns a real Promise in both modes (the compiled emitter wraps
// its Task via WrapTaskAsPromise), so `.then` is a proven path here.
// ---------------------------------------------------------------------------

/** Bridge a value-producing promise to a Node `(err, data)` callback. */
function __cbData(p: Promise<any>, callback: any): void {
    p.then((value: any) => { callback(null, value); }, (err: any) => { callback(err); });
}

/** Bridge a void promise to a Node `(err)` callback. */
function __cbVoid(p: Promise<any>, callback: any): void {
    p.then(() => { callback(null); }, (err: any) => { callback(err); });
}

// ===========================================================================
// Synchronous APIs — thin forwarders to primitive:fs.
// ===========================================================================

/** Synchronously tests whether the path exists. Returns false on error (never throws). */
export function existsSync(path: string): boolean { return __existsSync(path); }

// Optional trailing arguments are dispatched by arity rather than forwarded as
// `undefined`: the primitives default by argument count, and forwarding an
// explicit `undefined` would be numeric-converted (crash) or mis-read. Spread
// can't be used — the compiled primitive emitter doesn't expand it (see process.ts).

/** Synchronously reads a file. Returns a Buffer, or a string when an encoding is given. */
export function readFileSync(path: string, options?: any): string | Buffer {
    if (options === undefined) return __readFileSync(path);
    return __readFileSync(path, options);
}

/** Synchronously writes data to a file, replacing the file if it already exists. */
export function writeFileSync(path: string, data: any, options?: any): void { __writeFileSync(path, data); }

/** Synchronously appends data to a file, creating the file if it does not yet exist. */
export function appendFileSync(path: string, data: any, options?: any): void { __appendFileSync(path, data); }

/** Synchronously removes a file or symbolic link. */
export function unlinkSync(path: string): void { __unlinkSync(path); }

/** Synchronously creates a directory. */
export function mkdirSync(path: string, options?: any): any {
    if (options === undefined) return __mkdirSync(path);
    return __mkdirSync(path, options);
}

/** Synchronously removes a directory. */
export function rmdirSync(path: string, options?: any): void {
    if (options === undefined) { __rmdirSync(path); return; }
    __rmdirSync(path, options);
}

/** Synchronously reads the contents of a directory. */
export function readdirSync(path: string, options?: any): string[] {
    if (options === undefined) return __readdirSync(path);
    return __readdirSync(path, options);
}

/** Synchronously retrieves the Stats for the path. */
export function statSync(path: string): any { return __statSync(path); }

/** Synchronously retrieves the Stats for the path without following symbolic links. */
export function lstatSync(path: string): any { return __lstatSync(path); }

/** Synchronously renames (moves) a file or directory. */
export function renameSync(oldPath: string, newPath: string): void { __renameSync(oldPath, newPath); }

/** Synchronously copies a file. */
export function copyFileSync(src: string, dest: string, mode?: number): void { __copyFileSync(src, dest); }

/** Synchronously tests a user's permissions for the path; throws if the check fails. */
export function accessSync(path: string, mode?: number): void {
    if (mode === undefined) { __accessSync(path); return; }
    __accessSync(path, mode);
}

/** Synchronously changes the permissions of a file. */
export function chmodSync(path: string, mode: number): void { __chmodSync(path, mode); }

/** Synchronously changes the owner and group of a file. */
export function chownSync(path: string, uid: number, gid: number): void { __chownSync(path, uid, gid); }

/** Synchronously changes the owner and group of a symbolic link. */
export function lchownSync(path: string, uid: number, gid: number): void { __lchownSync(path, uid, gid); }

/** Synchronously truncates a file to the given length. */
export function truncateSync(path: string, len?: number): void {
    if (len === undefined) { __truncateSync(path); return; }
    __truncateSync(path, len);
}

/** Synchronously creates a symbolic link. */
export function symlinkSync(target: string, path: string, type?: string): void {
    if (type === undefined) { __symlinkSync(target, path); return; }
    __symlinkSync(target, path, type);
}

/** Synchronously reads the value of a symbolic link. */
export function readlinkSync(path: string): string { return __readlinkSync(path); }

/** Synchronously computes the canonical pathname, resolving symbolic links. */
export function realpathSync(path: string): string { return __realpathSync(path); }

/** Synchronously changes the file-system timestamps of the path. */
export function utimesSync(path: string, atime: number, mtime: number): void { __utimesSync(path, atime, mtime); }

/** Synchronously opens a file and returns its file descriptor. */
export function openSync(path: string, flags: any, mode?: number): number {
    if (mode === undefined) return __openSync(path, flags);
    return __openSync(path, flags, mode);
}

/** Synchronously closes a file descriptor. */
export function closeSync(fd: number): void { __closeSync(fd); }

/** Synchronously reads from a file descriptor into a buffer; returns the number of bytes read. */
export function readSync(fd: number, buffer: Buffer, offset: number, length: number, position: any): number {
    return __readSync(fd, buffer, offset, length, position);
}

/** Synchronously writes a buffer (or string) to a file descriptor; returns the number of bytes written. */
export function writeSync(fd: number, buffer: any, offset?: number, length?: number, position?: any): number {
    if (offset === undefined) return __writeSync(fd, buffer);
    if (length === undefined) return __writeSync(fd, buffer, offset);
    if (position === undefined) return __writeSync(fd, buffer, offset, length);
    return __writeSync(fd, buffer, offset, length, position);
}

/** Synchronously retrieves the Stats for an open file descriptor. */
export function fstatSync(fd: number): any { return __fstatSync(fd); }

/** Synchronously truncates an open file descriptor to the given length. */
export function ftruncateSync(fd: number, len?: number): void {
    if (len === undefined) { __ftruncateSync(fd); return; }
    __ftruncateSync(fd, len);
}

/** Synchronously creates a unique temporary directory and returns its path. */
export function mkdtempSync(prefix: string): string { return __mkdtempSync(prefix); }

/** Synchronously opens a directory and returns a Dir handle. */
export function opendirSync(path: string): any { return __opendirSync(path); }

/** Synchronously creates a hard link. */
export function linkSync(existingPath: string, newPath: string): void { __linkSync(existingPath, newPath); }

/** Returns a new ReadStream for the given path. */
export function createReadStream(path: string, options?: any): any { return __createReadStream(path, options); }

/** Returns a new WriteStream for the given path. */
export function createWriteStream(path: string, options?: any): any { return __createWriteStream(path, options); }

/** Watches for changes on a file or directory; returns an FSWatcher. */
export function watch(filename: string, options?: any, listener?: any): any { return __watch(filename, options, listener); }

/** Watches for changes on a file by polling its stats. */
export function watchFile(filename: string, options: any, listener?: any): any { return __watchFile(filename, options, listener); }

/** Stops watching a file previously registered with watchFile. */
export function unwatchFile(filename: string, listener?: any): void { __unwatchFile(filename, listener); }

/** File-system constants (access modes, open flags, copy flags, file-type bits). */
export const constants: any = __constants;

// ===========================================================================
// Callback-based async APIs — derived from the promise primitives.
// The trailing argument is the callback; an `options` argument may be omitted.
// ===========================================================================

/** Asynchronously reads the entire contents of a file. */
export function readFile(path: string, options?: any, callback?: any): void {
    if (typeof options === 'function') { callback = options; options = undefined; }
    __cbData(__pReadFile(path, options), callback);
}

/** Asynchronously writes data to a file, replacing the file if it already exists. */
export function writeFile(path: string, data: any, options?: any, callback?: any): void {
    if (typeof options === 'function') { callback = options; options = undefined; }
    __cbVoid(__pWriteFile(path, data, options), callback);
}

/** Asynchronously appends data to a file, creating the file if it does not yet exist. */
export function appendFile(path: string, data: any, options?: any, callback?: any): void {
    if (typeof options === 'function') { callback = options; options = undefined; }
    __cbVoid(__pAppendFile(path, data, options), callback);
}

/** Asynchronously retrieves the Stats for the path. */
export function stat(path: string, options?: any, callback?: any): void {
    if (typeof options === 'function') { callback = options; options = undefined; }
    __cbData(__pStat(path), callback);
}

/** Asynchronously retrieves the Stats for the path without following symbolic links. */
export function lstat(path: string, options?: any, callback?: any): void {
    if (typeof options === 'function') { callback = options; options = undefined; }
    __cbData(__pLstat(path), callback);
}

/** Asynchronously removes a file or symbolic link. */
export function unlink(path: string, callback: any): void {
    __cbVoid(__pUnlink(path), callback);
}

/** Asynchronously creates a directory. */
export function mkdir(path: string, options?: any, callback?: any): void {
    if (typeof options === 'function') { callback = options; options = undefined; }
    __cbData(__pMkdir(path, options), callback);
}

/** Asynchronously removes a directory. */
export function rmdir(path: string, options?: any, callback?: any): void {
    if (typeof options === 'function') { callback = options; options = undefined; }
    __cbVoid(__pRmdir(path, options), callback);
}

/** Asynchronously reads the contents of a directory. */
export function readdir(path: string, options?: any, callback?: any): void {
    if (typeof options === 'function') { callback = options; options = undefined; }
    __cbData(__pReaddir(path, options), callback);
}

/** Asynchronously renames (moves) a file or directory. */
export function rename(oldPath: string, newPath: string, callback: any): void {
    __cbVoid(__pRename(oldPath, newPath), callback);
}

/** Asynchronously copies a file. */
export function copyFile(src: string, dest: string, mode?: any, callback?: any): void {
    if (typeof mode === 'function') { callback = mode; mode = undefined; }
    __cbVoid(__pCopyFile(src, dest, mode), callback);
}

/** Asynchronously tests a user's permissions for the path. */
export function access(path: string, mode?: any, callback?: any): void {
    if (typeof mode === 'function') { callback = mode; mode = undefined; }
    __cbVoid(__pAccess(path, mode), callback);
}

/** Asynchronously changes the permissions of a file. */
export function chmod(path: string, mode: number, callback: any): void {
    __cbVoid(__pChmod(path, mode), callback);
}

/** Asynchronously truncates a file to the given length. */
export function truncate(path: string, len?: any, callback?: any): void {
    if (typeof len === 'function') { callback = len; len = undefined; }
    __cbVoid(__pTruncate(path, len), callback);
}

/** Asynchronously changes the file-system timestamps of the path. */
export function utimes(path: string, atime: number, mtime: number, callback: any): void {
    __cbVoid(__pUtimes(path, atime, mtime), callback);
}

/** Asynchronously reads the value of a symbolic link. */
export function readlink(path: string, options?: any, callback?: any): void {
    if (typeof options === 'function') { callback = options; options = undefined; }
    __cbData(__pReadlink(path), callback);
}

/** Asynchronously computes the canonical pathname, resolving symbolic links. */
export function realpath(path: string, options?: any, callback?: any): void {
    if (typeof options === 'function') { callback = options; options = undefined; }
    __cbData(__pRealpath(path), callback);
}

/** Asynchronously creates a symbolic link. */
export function symlink(target: string, path: string, type?: any, callback?: any): void {
    if (typeof type === 'function') { callback = type; type = undefined; }
    __cbVoid(__pSymlink(target, path, type), callback);
}

/** Asynchronously creates a hard link. */
export function link(existingPath: string, newPath: string, callback: any): void {
    __cbVoid(__pLink(existingPath, newPath), callback);
}

/** Asynchronously creates a unique temporary directory. */
export function mkdtemp(prefix: string, options?: any, callback?: any): void {
    if (typeof options === 'function') { callback = options; options = undefined; }
    __cbData(__pMkdtemp(prefix), callback);
}

// ===========================================================================
// fs.promises namespace — assembled from the promise primitives.
// Each method is wrapped in an arrow so the call reaches the primitive at a
// call-site (the emitter dispatches method calls, not method values).
// ===========================================================================

/** Promise-based file-system API (equivalent to `require('fs/promises')`). */
export const promises = {
    readFile: (path: string, options?: any): Promise<any> => __pReadFile(path, options),
    writeFile: (path: string, data: any, options?: any): Promise<void> => __pWriteFile(path, data, options),
    appendFile: (path: string, data: any, options?: any): Promise<void> => __pAppendFile(path, data, options),
    stat: (path: string): Promise<any> => __pStat(path),
    lstat: (path: string): Promise<any> => __pLstat(path),
    unlink: (path: string): Promise<void> => __pUnlink(path),
    mkdir: (path: string, options?: any): Promise<any> => __pMkdir(path, options),
    rmdir: (path: string, options?: any): Promise<void> => __pRmdir(path, options),
    rm: (path: string, options?: any): Promise<void> => __pRm(path, options),
    readdir: (path: string, options?: any): Promise<any> => __pReaddir(path, options),
    rename: (oldPath: string, newPath: string): Promise<void> => __pRename(oldPath, newPath),
    copyFile: (src: string, dest: string, mode?: any): Promise<void> => __pCopyFile(src, dest, mode),
    access: (path: string, mode?: any): Promise<void> => __pAccess(path, mode),
    chmod: (path: string, mode: number): Promise<void> => __pChmod(path, mode),
    truncate: (path: string, len?: any): Promise<void> => __pTruncate(path, len),
    utimes: (path: string, atime: number, mtime: number): Promise<void> => __pUtimes(path, atime, mtime),
    readlink: (path: string): Promise<any> => __pReadlink(path),
    realpath: (path: string): Promise<any> => __pRealpath(path),
    symlink: (target: string, path: string, type?: any): Promise<void> => __pSymlink(target, path, type),
    link: (existingPath: string, newPath: string): Promise<void> => __pLink(existingPath, newPath),
    mkdtemp: (prefix: string): Promise<any> => __pMkdtemp(prefix),
    constants: __constants,
};

// Node's `fs` module exposes its surface as both named exports and a default
// export (the module object). Supports `import fs from 'fs'; fs.readFileSync(...)`.
export default {
    existsSync, readFileSync, writeFileSync, appendFileSync,
    unlinkSync, mkdirSync, rmdirSync, readdirSync,
    statSync, lstatSync, renameSync, copyFileSync, accessSync,
    chmodSync, chownSync, lchownSync, truncateSync,
    symlinkSync, readlinkSync, realpathSync, utimesSync,
    openSync, closeSync, readSync, writeSync, fstatSync, ftruncateSync,
    mkdtempSync, opendirSync, linkSync,
    createReadStream, createWriteStream,
    watch, watchFile, unwatchFile,
    constants,
    readFile, writeFile, appendFile, stat, lstat, unlink, mkdir, rmdir,
    readdir, rename, copyFile, access, chmod, truncate, utimes,
    readlink, realpath, symlink, link, mkdtemp,
    promises,
};
