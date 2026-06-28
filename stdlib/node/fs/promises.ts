// Node.js 'fs/promises' module — SharpTS embedded stdlib implementation.
// Target: Node.js 24.15.0. See https://nodejs.org/api/fs.html#promises-api.
//
// Thin Node-shape facade over `primitive:fs/promises`. Each export forwards to
// its matching promise primitive at a call-site (the compiled emitter dispatches
// method calls, not method values). Divergence from Node lives in the primitive.

import {
    readFile as __readFile,
    writeFile as __writeFile,
    appendFile as __appendFile,
    stat as __stat,
    lstat as __lstat,
    unlink as __unlink,
    mkdir as __mkdir,
    rmdir as __rmdir,
    rm as __rm,
    readdir as __readdir,
    rename as __rename,
    copyFile as __copyFile,
    access as __access,
    chmod as __chmod,
    truncate as __truncate,
    utimes as __utimes,
    readlink as __readlink,
    realpath as __realpath,
    symlink as __symlink,
    link as __link,
    mkdtemp as __mkdtemp,
    constants as __constants,
} from 'primitive:fs/promises';

/** Asynchronously reads the entire contents of a file. */
export function readFile(path: string, options?: any): Promise<any> { return __readFile(path, options); }

/** Asynchronously writes data to a file, replacing the file if it already exists. */
export function writeFile(path: string, data: any, options?: any): Promise<void> { return __writeFile(path, data, options); }

/** Asynchronously appends data to a file, creating the file if it does not yet exist. */
export function appendFile(path: string, data: any, options?: any): Promise<void> { return __appendFile(path, data, options); }

/** Asynchronously retrieves the Stats for the path. */
export function stat(path: string): Promise<any> { return __stat(path); }

/** Asynchronously retrieves the Stats for the path without following symbolic links. */
export function lstat(path: string): Promise<any> { return __lstat(path); }

/** Asynchronously removes a file or symbolic link. */
export function unlink(path: string): Promise<void> { return __unlink(path); }

/** Asynchronously creates a directory. */
export function mkdir(path: string, options?: any): Promise<any> { return __mkdir(path, options); }

/** Asynchronously removes a directory. */
export function rmdir(path: string, options?: any): Promise<void> { return __rmdir(path, options); }

/** Asynchronously removes files and directories (recursively when requested). */
export function rm(path: string, options?: any): Promise<void> { return __rm(path, options); }

/** Asynchronously reads the contents of a directory. */
export function readdir(path: string, options?: any): Promise<any> { return __readdir(path, options); }

/** Asynchronously renames (moves) a file or directory. */
export function rename(oldPath: string, newPath: string): Promise<void> { return __rename(oldPath, newPath); }

/** Asynchronously copies a file. */
export function copyFile(src: string, dest: string, mode?: any): Promise<void> { return __copyFile(src, dest, mode); }

/** Asynchronously tests a user's permissions for the path. */
export function access(path: string, mode?: any): Promise<void> { return __access(path, mode); }

/** Asynchronously changes the permissions of a file. */
export function chmod(path: string, mode: number): Promise<void> { return __chmod(path, mode); }

/** Asynchronously truncates a file to the given length. */
export function truncate(path: string, len?: any): Promise<void> { return __truncate(path, len); }

/** Asynchronously changes the file-system timestamps of the path. */
export function utimes(path: string, atime: number, mtime: number): Promise<void> { return __utimes(path, atime, mtime); }

/** Asynchronously reads the value of a symbolic link. */
export function readlink(path: string): Promise<any> { return __readlink(path); }

/** Asynchronously computes the canonical pathname, resolving symbolic links. */
export function realpath(path: string): Promise<any> { return __realpath(path); }

/** Asynchronously creates a symbolic link. */
export function symlink(target: string, path: string, type?: any): Promise<void> { return __symlink(target, path, type); }

/** Asynchronously creates a hard link. */
export function link(existingPath: string, newPath: string): Promise<void> { return __link(existingPath, newPath); }

/** Asynchronously creates a unique temporary directory. */
export function mkdtemp(prefix: string): Promise<any> { return __mkdtemp(prefix); }

/** File-system constants (access modes, open flags, copy flags, file-type bits). */
export const constants: any = __constants;

export default {
    readFile, writeFile, appendFile, stat, lstat, unlink, mkdir, rmdir, rm,
    readdir, rename, copyFile, access, chmod, truncate, utimes,
    readlink, realpath, symlink, link, mkdtemp, constants,
};
