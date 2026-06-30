// Node.js 'process' module — SharpTS embedded stdlib implementation.
// Target: Node.js 24.15.0. See https://nodejs.org/api/process.html.
//
// Heavy lifting (platform detection, argv construction, stdio singletons,
// nextTick dispatch) stays in C# behind `primitive:process`. This file is
// a thin, Node-shape facade: every export forwards to its matching primitive.
// Divergence from Node semantics lives in the primitive, not here.
//
// The global `process` object (accessed as bare `process.cwd()` without an
// import) is a separate binding registered as a built-in namespace; this
// module only covers the `import ... from 'process'` pathway.

import {
    // Properties (data)
    platform as __platform,
    arch as __arch,
    pid as __pid,
    version as __version,
    env as __env,
    argv as __argv,
    exitCode as __exitCode,
    stdin as __stdin,
    stdout as __stdout,
    stderr as __stderr,
    // Methods
    cwd as __cwd,
    chdir as __chdir,
    exit as __exit,
    hrtime as __hrtime,
    uptime as __uptime,
    memoryUsage as __memoryUsage,
    nextTick as __nextTick,
} from 'primitive:process';

/** The operating system platform (e.g. 'win32', 'linux', 'darwin'). */
export const platform: string = __platform;

/** The CPU architecture (e.g. 'x64', 'arm64'). */
export const arch: string = __arch;

/** The PID of the process. */
export const pid: number = __pid;

/** The runtime version string (e.g. 'v10.0.0'). */
export const version: string = __version;

/** Environment variables as a string-keyed object. */
export const env: any = __env;

/** Command-line arguments: [runtime_path, script_path, ...userArgs]. */
export const argv: string[] = __argv;

/** The current process exit code (0 by default). */
export const exitCode: number = __exitCode;

/** Readable stream connected to standard input. */
export const stdin: any = __stdin;

/** Writable stream connected to standard output. */
export const stdout: any = __stdout;

/** Writable stream connected to standard error. */
export const stderr: any = __stderr;

/** Returns the current working directory. */
export function cwd(): string { return __cwd(); }

/** Changes the current working directory. */
export function chdir(directory: string): void { __chdir(directory); }

/** Terminates the process with the given exit code (defaults to 0). */
export function exit(code?: number): void { __exit(code as any); }

/** Returns a high-resolution [seconds, nanoseconds] tuple; if `time` is passed, returns the delta since that tuple. */
export function hrtime(time?: number[]): number[] { return __hrtime(time as any); }

/** Returns the number of seconds the current process has been running. */
export function uptime(): number { return __uptime(); }

/** Returns an object describing the memory usage of the process in bytes. */
export function memoryUsage(): { rss: number; heapTotal: number; heapUsed: number; external: number; arrayBuffers: number } {
    return __memoryUsage();
}

// `nextTick` forwards its trailing `...args` straight to the primitive; the
// built-in module emitters now expand a trailing `Expr.Spread` at runtime (see
// ProcessModuleEmitter.EmitArgsArray), so there is no arity ceiling.
export function nextTick(callback: any, ...args: any[]): void {
    __nextTick(callback, ...args);
}

// Node's `process` module exposes its surface as both named exports and a
// default export (the process object itself). Supports `import process from
// 'process'; process.stdout.write(...)`. The default export is a plain
// object literal captured at module-init time; stdio singletons and data
// properties are the same live references used by the named exports.
export default {
    platform, arch, pid, version, env, argv, exitCode,
    stdin, stdout, stderr,
    cwd, chdir, exit, hrtime, uptime, memoryUsage, nextTick,
};
