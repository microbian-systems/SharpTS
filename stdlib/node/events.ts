// Node.js 'events' module — SharpTS embedded stdlib implementation.
// Target: Node.js 24.15.0. See https://nodejs.org/api/events.html.
//
// Self-contained TS EventEmitter class. Mirrors Node's observable semantics:
// listener-addition order, once-unwrap on emit, method chaining, prepend
// variants, per-instance and default max listeners.
//
// Note: SharpTS's internal runtime types (SharpTSProcess, SharpTSReadable,
// etc.) still inherit from a C#-side SharpTSEventEmitter. That's separate
// from this class. User code that imports EventEmitter from 'events' gets
// *this* class and builds on it; the runtime classes remain independent.

type Listener = Function;

interface ListenerWrapper {
    listener: Listener;
    once: boolean;
}

/** Node.js-compatible EventEmitter implementation. */
export class EventEmitter {
    /**
     * Default maximum listeners before a warning is emitted. Node defaults to 10.
     * Overridable globally; per-instance values take precedence via setMaxListeners.
     */
    static defaultMaxListeners: number = 10;

    private _events: { [eventName: string]: ListenerWrapper[] };
    private _maxListeners: number;

    constructor() {
        this._events = {};
        this._maxListeners = 0; // 0 → use defaultMaxListeners
    }

    private _getListeners(eventName: string, create: boolean): ListenerWrapper[] | null {
        let arr = this._events[eventName];
        if (arr == null) {
            if (!create) return null;
            arr = [];
            this._events[eventName] = arr;
        }
        return arr;
    }

    private _addListener(eventName: string, listener: Listener, once: boolean, prepend: boolean): EventEmitter {
        if (typeof listener !== 'function') {
            throw new TypeError('Listener must be a function');
        }
        const arr = this._getListeners(eventName, true)!;
        const wrapper: ListenerWrapper = { listener, once };
        if (prepend) arr.unshift(wrapper);
        else arr.push(wrapper);
        // Node emits a 'warning' event when the count exceeds the threshold; for now
        // we silently permit it — exceeds-threshold warnings are observability, not
        // correctness, and no current tests assert on them.
        return this;
    }

    /** Register a listener. Returns `this` for chaining. */
    on(eventName: string, listener: Listener): EventEmitter {
        return this._addListener(eventName, listener, false, false);
    }

    /** Alias for {@link on}. */
    addListener(eventName: string, listener: Listener): EventEmitter {
        return this._addListener(eventName, listener, false, false);
    }

    /** Register a one-shot listener that removes itself after firing once. */
    once(eventName: string, listener: Listener): EventEmitter {
        return this._addListener(eventName, listener, true, false);
    }

    /** Register a listener at the head of the chain. */
    prependListener(eventName: string, listener: Listener): EventEmitter {
        return this._addListener(eventName, listener, false, true);
    }

    /** Register a one-shot listener at the head of the chain. */
    prependOnceListener(eventName: string, listener: Listener): EventEmitter {
        return this._addListener(eventName, listener, true, true);
    }

    /**
     * Remove a single listener by reference.
     * Matches Node's semantics: only the first occurrence is removed even when
     * the same function is registered multiple times.
     */
    off(eventName: string, listener: Listener): EventEmitter {
        const arr = this._getListeners(eventName, false);
        if (arr == null) return this;
        for (let i = 0; i < arr.length; i++) {
            if (arr[i].listener === listener) {
                arr.splice(i, 1);
                if (arr.length === 0) delete this._events[eventName];
                break;
            }
        }
        return this;
    }

    /** Alias for {@link off}. */
    removeListener(eventName: string, listener: Listener): EventEmitter {
        return this.off(eventName, listener);
    }

    /** Remove every listener for an event, or every listener across all events when called without an argument. */
    removeAllListeners(eventName?: string): EventEmitter {
        if (eventName == null) {
            this._events = {};
        } else {
            delete this._events[eventName];
        }
        return this;
    }

    /**
     * Fire all registered listeners for `eventName` with the supplied arguments.
     * A snapshot of the listener array is taken before dispatch so listeners
     * added or removed during emission don't disturb the in-flight iteration.
     * Returns true if the event had listeners, false otherwise.
     */
    emit(eventName: string, ...args: any[]): boolean {
        const arr = this._getListeners(eventName, false);
        if (arr == null || arr.length === 0) return false;

        // Snapshot before dispatch — listeners may modify the array.
        const snapshot: ListenerWrapper[] = [];
        for (let i = 0; i < arr.length; i++) snapshot.push(arr[i]);

        // Pre-remove once wrappers from the live array before calling, so that
        // a listener that inspects listenerCount mid-emit sees the post-fire state.
        for (let i = 0; i < snapshot.length; i++) {
            const w = snapshot[i];
            if (w.once) {
                const live = this._events[eventName];
                if (live != null) {
                    for (let j = 0; j < live.length; j++) {
                        if (live[j] === w) {
                            live.splice(j, 1);
                            if (live.length === 0) delete this._events[eventName];
                            break;
                        }
                    }
                }
            }
        }

        // Dispatch.
        for (let i = 0; i < snapshot.length; i++) {
            snapshot[i].listener.apply(this, args);
        }
        return true;
    }

    /** Return the listener functions for `eventName`. Unwrapped — once wrappers' originals. */
    listeners(eventName: string): Listener[] {
        const arr = this._getListeners(eventName, false);
        if (arr == null) return [];
        const out: Listener[] = [];
        for (let i = 0; i < arr.length; i++) out.push(arr[i].listener);
        return out;
    }

    /** Same as {@link listeners} in this implementation; kept for API parity. */
    rawListeners(eventName: string): Listener[] {
        return this.listeners(eventName);
    }

    /** Number of listeners for `eventName`. */
    listenerCount(eventName: string): number {
        const arr = this._getListeners(eventName, false);
        return arr == null ? 0 : arr.length;
    }

    /** Names of events that currently have at least one listener. */
    eventNames(): string[] {
        const out: string[] = [];
        const keys = Object.keys(this._events);
        for (const k of keys) {
            if (this._events[k].length > 0) out.push(k);
        }
        return out;
    }

    /** Per-instance max listener override. Zero (default) falls back to the class default. */
    setMaxListeners(n: number): EventEmitter {
        if (typeof n !== 'number') throw new TypeError('setMaxListeners argument must be a number');
        this._maxListeners = n;
        return this;
    }

    /** Effective max listener count for this instance. */
    getMaxListeners(): number {
        return this._maxListeners > 0 ? this._maxListeners : EventEmitter.defaultMaxListeners;
    }
}

// Node's `events` default export is the EventEmitter class itself, not a
// namespace object: `const EE = require('events')` gives you the class.
export default EventEmitter;
