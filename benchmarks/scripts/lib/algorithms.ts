// Shared benchmark algorithms — the SINGLE source of these workloads.
//
// Consumed two ways, so the two benchmark systems measure identical code:
//   * the cross-runtime shell harness (benchmarks/run-benchmarks.ps1) imports
//     these into its driver scripts and runs them under the SharpTS
//     interpreter, the SharpTS compiler, Node.js, and Bun;
//   * the BenchmarkDotNet suite (SharpTS.Benchmarks) embeds this file as a
//     resource, compiles it, and invokes the functions via reflection.
//
// Keep every export a plain `number -> number` function (no top-level
// execution) so the BDN harness can reflect them off the compiled $Program.

// ── Numeric / recursion / loops ──────────────────────────────────────────

// Recursion + function-call overhead.
export function fibonacci(n: number): number {
    if (n <= 1) return n;
    return fibonacci(n - 1) + fibonacci(n - 2);
}

// Tight arithmetic loop.
export function factorial(n: number): number {
    let result: number = 1;
    for (let i: number = 2; i <= n; i++) {
        result = result * i;
    }
    return result;
}

// Array allocation + nested loops (Sieve of Eratosthenes).
export function countPrimes(n: number): number {
    if (n <= 2) return 0;

    const isPrime: boolean[] = [];
    for (let i: number = 0; i < n; i++) {
        isPrime.push(true);
    }
    isPrime[0] = false;
    isPrime[1] = false;

    for (let i: number = 2; i * i < n; i++) {
        if (isPrime[i]) {
            for (let j: number = i * i; j < n; j = j + i) {
                isPrime[j] = false;
            }
        }
    }

    let count: number = 0;
    for (let i: number = 0; i < n; i++) {
        if (isPrime[i]) count = count + 1;
    }
    return count;
}

// ── Broadened workloads (interpreter vs compiler vs V8 diverge most here) ──

// String building + scanning: concatenation then a charCodeAt sweep.
export function stringWork(n: number): number {
    let s: string = "";
    for (let i: number = 0; i < n; i++) {
        s = s + "ab";
    }
    let sum: number = 0;
    for (let i: number = 0; i < s.length; i++) {
        sum = sum + s.charCodeAt(i);
    }
    return sum;
}

// Object allocation + property access: build small records, sum their fields.
export function objectWork(n: number): number {
    let sum: number = 0;
    for (let i: number = 0; i < n; i++) {
        const o = { x: i, y: i + 1 };
        sum = sum + o.x + o.y;
    }
    return sum;
}

// Closures: build an adder closure per iteration and invoke it.
export function closureWork(n: number): number {
    let sum: number = 0;
    for (let i: number = 0; i < n; i++) {
        const add = (a: number): number => a + i;
        sum = sum + add(i);
    }
    return sum;
}

// Array higher-order methods: map -> filter -> reduce pipeline.
export function arrayMethodWork(n: number): number {
    const arr: number[] = [];
    for (let i: number = 0; i < n; i++) {
        arr.push(i);
    }
    const doubled = arr.map((x: number): number => x * 2);
    const evens = doubled.filter((x: number): boolean => x % 4 === 0);
    return evens.reduce((acc: number, x: number): number => acc + x, 0);
}
