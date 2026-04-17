// Node.js 'querystring' module — SharpTS embedded stdlib implementation.
// Target: Node.js 24.15.0. See https://nodejs.org/api/querystring.html.
//
// Note: the 'querystring' module is deprecated in Node but still widely used.
// This file provides the observable subset exercised by real-world code.

// NOTE: default parameter syntax (sep = '&') cannot be used here. Module imports
// dispatch through $TSFunction.Invoke, which pads missing args with null and calls
// the full-arity method — bypassing the generated lower-arity overloads. So the
// default initializer never runs. Fall back to `??` at the top of each function.
// Tracked: docs/plans/embedded-stdlib.md (Risks).

/**
 * Parses a URL query string into an object. Repeated keys produce an array value.
 */
export function parse(str: string, sep?: string, eq?: string): any {
    const actualSep = sep ?? '&';
    const actualEq = eq ?? '=';
    const result: any = {};
    if (!str) return result;

    const pairs = str.split(actualSep);
    for (const pair of pairs) {
        if (!pair) continue;

        const eqIndex = pair.indexOf(actualEq);
        let key: string;
        let value: string;

        if (eqIndex >= 0) {
            key = decodeURIComponent(pair.substring(0, eqIndex).replaceAll('+', ' '));
            value = decodeURIComponent(pair.substring(eqIndex + actualEq.length).replaceAll('+', ' '));
        } else {
            key = decodeURIComponent(pair.replaceAll('+', ' '));
            value = '';
        }

        if (key in result) {
            const existing = result[key];
            if (Array.isArray(existing)) {
                existing.push(value);
            } else {
                result[key] = [existing, value];
            }
        } else {
            result[key] = value;
        }
    }

    return result;
}

/**
 * Serializes an object into a URL query string.
 */
export function stringify(obj: any, sep?: string, eq?: string): string {
    const actualSep = sep ?? '&';
    const actualEq = eq ?? '=';
    if (!obj) return '';

    const pairs: string[] = [];
    const keys = Object.keys(obj);
    for (const key of keys) {
        const encodedKey = encodeURIComponent(key);
        const value = obj[key];
        if (Array.isArray(value)) {
            for (const item of value) {
                pairs.push(encodedKey + actualEq + encodeURIComponent(String(item)));
            }
        } else {
            pairs.push(encodedKey + actualEq + encodeURIComponent(String(value)));
        }
    }
    return pairs.join(actualSep);
}

/** Percent-encodes a string — thin wrapper over encodeURIComponent. */
export function escape(str: string): string {
    return encodeURIComponent(str);
}

/** Decodes a percent-encoded string and translates '+' to space. */
export function unescape(str: string): string {
    return decodeURIComponent(str.replaceAll('+', ' '));
}

// Deprecated aliases retained for compatibility.
export const decode = parse;
export const encode = stringify;
