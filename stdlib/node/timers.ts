// Node.js 'timers' module — SharpTS embedded stdlib implementation.
// Target: Node.js 24.15.0. See https://nodejs.org/api/timers.html.
//
// Re-exports the callback-based timer API from `primitive:timers`, which
// dispatches to the same $Runtime methods used by the global setTimeout etc.
// The user-facing timers module and the globals share the same runtime
// infrastructure; importing here adds no semantic difference.
//
// Rest-arg note: the setTimeout / setInterval / setImmediate primitives pack
// trailing callback args into an object[]. The TS facade below arity-dispatches
// rather than spread-forwarding — a `__setTimeout(cb, delay, ...args)` call
// into a primitive emitter hits the same compiler gap process.nextTick
// documented (Expr.Spread not expanded by built-in module emitters). Flat
// positional dispatch sidesteps it. The 8-arg ceiling matches Node's
// practical usage; >8 callback payload args are vanishingly rare.

import {
    setTimeout as __setTimeout,
    clearTimeout as __clearTimeout,
    setInterval as __setInterval,
    clearInterval as __clearInterval,
    setImmediate as __setImmediate,
    clearImmediate as __clearImmediate,
} from 'primitive:timers';

/** Schedules `callback` to run after `delay` ms with optional trailing args. */
export function setTimeout(callback: any, delay?: number, ...args: any[]): any {
    const d = delay ?? 0;
    const n = args.length;
    if (n === 0) return __setTimeout(callback, d);
    if (n === 1) return __setTimeout(callback, d, args[0]);
    if (n === 2) return __setTimeout(callback, d, args[0], args[1]);
    if (n === 3) return __setTimeout(callback, d, args[0], args[1], args[2]);
    if (n === 4) return __setTimeout(callback, d, args[0], args[1], args[2], args[3]);
    if (n === 5) return __setTimeout(callback, d, args[0], args[1], args[2], args[3], args[4]);
    if (n === 6) return __setTimeout(callback, d, args[0], args[1], args[2], args[3], args[4], args[5]);
    if (n === 7) return __setTimeout(callback, d, args[0], args[1], args[2], args[3], args[4], args[5], args[6]);
    return __setTimeout(callback, d, args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]);
}

/** Cancels a pending timeout created by setTimeout. */
export function clearTimeout(handle?: any): void {
    __clearTimeout(handle);
}

/** Schedules `callback` to repeat every `delay` ms with optional trailing args. */
export function setInterval(callback: any, delay?: number, ...args: any[]): any {
    const d = delay ?? 0;
    const n = args.length;
    if (n === 0) return __setInterval(callback, d);
    if (n === 1) return __setInterval(callback, d, args[0]);
    if (n === 2) return __setInterval(callback, d, args[0], args[1]);
    if (n === 3) return __setInterval(callback, d, args[0], args[1], args[2]);
    if (n === 4) return __setInterval(callback, d, args[0], args[1], args[2], args[3]);
    if (n === 5) return __setInterval(callback, d, args[0], args[1], args[2], args[3], args[4]);
    if (n === 6) return __setInterval(callback, d, args[0], args[1], args[2], args[3], args[4], args[5]);
    if (n === 7) return __setInterval(callback, d, args[0], args[1], args[2], args[3], args[4], args[5], args[6]);
    return __setInterval(callback, d, args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]);
}

/** Cancels a pending interval created by setInterval. */
export function clearInterval(handle?: any): void {
    __clearInterval(handle);
}

/** Schedules `callback` to run on the next event-loop iteration. */
export function setImmediate(callback: any, ...args: any[]): any {
    const n = args.length;
    if (n === 0) return __setImmediate(callback);
    if (n === 1) return __setImmediate(callback, args[0]);
    if (n === 2) return __setImmediate(callback, args[0], args[1]);
    if (n === 3) return __setImmediate(callback, args[0], args[1], args[2]);
    if (n === 4) return __setImmediate(callback, args[0], args[1], args[2], args[3]);
    if (n === 5) return __setImmediate(callback, args[0], args[1], args[2], args[3], args[4]);
    if (n === 6) return __setImmediate(callback, args[0], args[1], args[2], args[3], args[4], args[5]);
    if (n === 7) return __setImmediate(callback, args[0], args[1], args[2], args[3], args[4], args[5], args[6]);
    return __setImmediate(callback, args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]);
}

/** Cancels a pending immediate created by setImmediate. */
export function clearImmediate(handle?: any): void {
    __clearImmediate(handle);
}

export default {
    setTimeout, clearTimeout,
    setInterval, clearInterval,
    setImmediate, clearImmediate,
};
