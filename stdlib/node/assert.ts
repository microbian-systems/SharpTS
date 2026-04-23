// Node.js 'assert' module — SharpTS embedded stdlib implementation.
// Target: Node.js 24.15.0. See https://nodejs.org/api/assert.html.
//
// Pure-logic leaf. No host dependencies: assertions are just value
// comparisons that throw AssertionError on failure. Node's assert API
// is wide but individually each method is small.

/**
 * Error class thrown by assert.* when an assertion fails. Mirrors Node's
 * AssertionError shape (actual/expected/operator/generatedMessage fields
 * and code='ERR_ASSERTION') so user code that inspects the error works
 * identically.
 */
export class AssertionError extends Error {
    actual: any;
    expected: any;
    operator: string;
    generatedMessage: boolean;
    code: string;

    constructor(options?: {
        message?: string;
        actual?: any;
        expected?: any;
        operator?: string;
        stackStartFn?: Function;
    }) {
        const opts = options || {};
        const generated = opts.message == null;
        const msg = opts.message != null ? opts.message : 'AssertionError';
        super(msg);
        this.name = 'AssertionError';
        this.actual = opts.actual;
        this.expected = opts.expected;
        this.operator = opts.operator != null ? opts.operator : '';
        this.generatedMessage = generated;
        this.code = 'ERR_ASSERTION';
    }
}

// ─── Value stringification (for generated error messages) ───────────

function stringify(value: any): string {
    if (value === null) return 'null';
    if (value === undefined) return 'undefined';
    const t = typeof value;
    if (t === 'string') return '"' + value + '"';
    if (t === 'number' || t === 'boolean' || t === 'bigint') return String(value);
    if (t === 'function') return '[Function]';
    if (t === 'symbol') return String(value);
    if (Array.isArray(value)) {
        const items: string[] = [];
        for (let i = 0; i < value.length; i++) items.push(stringify(value[i]));
        return '[' + items.join(', ') + ']';
    }
    // Plain object
    try {
        const keys = Object.keys(value);
        const parts: string[] = [];
        for (const k of keys) parts.push(k + ': ' + stringify(value[k]));
        return '{' + parts.join(', ') + '}';
    } catch {
        return String(value);
    }
}

// ─── Equality primitives ────────────────────────────────────────────

function sameValue(a: any, b: any): boolean {
    // Node's strict* family uses SameValue semantics (Object.is), which
    // treats NaN as equal to NaN and -0 as distinct from +0. Plain ===
    // diverges on NaN.
    if (a === b) {
        // +0 / -0 check: 1/+0 === Infinity, 1/-0 === -Infinity.
        if (a === 0 && b === 0) return 1 / (a as number) === 1 / (b as number);
        return true;
    }
    // NaN: only value that's not equal to itself.
    return a !== a && b !== b;
}

function looseEquals(a: any, b: any): boolean {
    // JS == semantics. TS catches the lint rule, but this is intentional here.
    // deno-lint-ignore eqeqeq
    return a == b;
}

function deepEquals(a: any, b: any, strict: boolean): boolean {
    // Primitive / null fast path. For any type where SameValue (strict) or
    // loose equality suffices, we don't need to recurse into structure.
    if (typeof a !== 'object' || a === null || typeof b !== 'object' || b === null) {
        return strict ? sameValue(a, b) : looseEquals(a, b);
    }

    // Reference identity (same object).
    if (a === b) return true;

    // Array branch: both must be arrays, same length, deep-equal elements.
    // Falling back to the object branch for arrays would also work in pure JS
    // (Object.keys on arrays returns the indices), but arrays get their own
    // branch for predictable element-by-index traversal.
    const aIsArray = Array.isArray(a);
    const bIsArray = Array.isArray(b);
    if (aIsArray !== bIsArray) return false;
    if (aIsArray) {
        const lenA = a.length;
        const lenB = b.length;
        if (lenA !== lenB) return false;
        for (let i = 0; i < lenA; i++) {
            if (!deepEquals(a[i], b[i], strict)) return false;
        }
        return true;
    }

    // Plain objects: same keys, deep-equal values.
    const keysA = Object.keys(a);
    const keysB = Object.keys(b);
    if (keysA.length !== keysB.length) return false;
    for (const key of keysA) {
        if (!(key in b)) return false;
        if (!deepEquals(a[key], b[key], strict)) return false;
    }
    return true;
}

// ─── Message helpers ────────────────────────────────────────────────

function resolveMessage(message: any, fallback: string): string {
    if (message == null) return fallback;
    if (typeof message === 'string') return message;
    // Node also accepts an Error instance directly, but we narrow to string here —
    // the full Error-passthrough variant is deferred until a test demands it.
    return fallback;
}

function fail_(message: string, actual: any, expected: any, op: string): never {
    throw new AssertionError({
        message,
        actual,
        expected,
        operator: op,
    });
}

// ─── Public API ─────────────────────────────────────────────────────

/** Throws if `value` is falsy. */
export function ok(value: any, message?: string | Error): void {
    if (!value) {
        fail_(
            resolveMessage(message, 'The expression evaluated to a falsy value'),
            value,
            true,
            'ok'
        );
    }
}

/** Throws if `actual !== expected` (SameValue). */
export function strictEqual(actual: any, expected: any, message?: string | Error): void {
    if (!sameValue(actual, expected)) {
        fail_(
            resolveMessage(message,
                'Expected values to be strictly equal:\n' + stringify(actual) +
                '\nshould equal\n' + stringify(expected)),
            actual,
            expected,
            'strictEqual'
        );
    }
}

/** Throws if `actual === expected` (SameValue). */
export function notStrictEqual(actual: any, expected: any, message?: string | Error): void {
    if (sameValue(actual, expected)) {
        fail_(
            resolveMessage(message,
                'Expected values to be strictly unequal: ' + stringify(actual)),
            actual,
            expected,
            'notStrictEqual'
        );
    }
}

/** Deep comparison with strict (SameValue) equality at leaves. */
export function deepStrictEqual(actual: any, expected: any, message?: string | Error): void {
    if (!deepEquals(actual, expected, true)) {
        fail_(
            resolveMessage(message,
                'Expected values to be deeply equal:\n' + stringify(actual) +
                '\nshould equal\n' + stringify(expected)),
            actual,
            expected,
            'deepStrictEqual'
        );
    }
}

/** Throws if actual and expected are deeply strictly equal. */
export function notDeepStrictEqual(actual: any, expected: any, message?: string | Error): void {
    if (deepEquals(actual, expected, true)) {
        fail_(
            resolveMessage(message,
                'Expected values not to be deeply equal: ' + stringify(actual)),
            actual,
            expected,
            'notDeepStrictEqual'
        );
    }
}

/** Loose (`==`) equality. */
export function equal(actual: any, expected: any, message?: string | Error): void {
    if (!looseEquals(actual, expected)) {
        fail_(
            resolveMessage(message,
                'Expected values to be loosely equal:\n' + stringify(actual) +
                '\nshould equal\n' + stringify(expected)),
            actual,
            expected,
            'equal'
        );
    }
}

/** Loose (`!=`) inequality. */
export function notEqual(actual: any, expected: any, message?: string | Error): void {
    if (looseEquals(actual, expected)) {
        fail_(
            resolveMessage(message,
                'Expected values not to be loosely equal: ' + stringify(actual)),
            actual,
            expected,
            'notEqual'
        );
    }
}

/** Always throws; convenience for unreachable branches. */
export function fail(message?: string | Error): never {
    fail_(resolveMessage(message, 'Failed'), undefined, undefined, 'fail');
    // Unreachable — fail_ returns never — but TS requires an explicit return
    // on some code paths. The throw above handles it.
    throw new AssertionError({ message: 'unreachable' });
}

/** Throws if `fn` does NOT throw when invoked. */
export function throws(fn: Function, message?: string | Error): void {
    if (typeof fn !== 'function') {
        fail_('First argument must be a function', fn, undefined, 'throws');
    }
    let threw = false;
    try {
        fn();
    } catch {
        threw = true;
    }
    if (!threw) {
        fail_(
            resolveMessage(message, 'Missing expected exception'),
            undefined,
            undefined,
            'throws'
        );
    }
}

/** Throws if `fn` DOES throw when invoked. */
export function doesNotThrow(fn: Function, message?: string | Error): void {
    if (typeof fn !== 'function') {
        fail_('First argument must be a function', fn, undefined, 'doesNotThrow');
    }
    try {
        fn();
    } catch (e) {
        const base = 'Got unwanted exception';
        const detail = (e as any) && (e as any).message ? ': ' + (e as any).message : '';
        fail_(
            resolveMessage(message, base + detail),
            e,
            undefined,
            'doesNotThrow'
        );
    }
}

export default {
    AssertionError,
    ok, strictEqual, notStrictEqual, deepStrictEqual, notDeepStrictEqual,
    equal, notEqual, fail, throws, doesNotThrow,
};
