function fibonacci(n: number): number {
    if (n <= 1) return n;
    return fibonacci(n - 1) + fibonacci(n - 2);
}

const params: number[] = [10, 20, 30, 35];
const warmup: number = 3;
const iterations: number = 5;

for (let p: number = 0; p < params.length; p++) {
    const n: number = params[p];
    for (let w: number = 0; w < warmup; w++) {
        fibonacci(n);
    }
    const start: number = Date.now();
    for (let i: number = 0; i < iterations; i++) {
        fibonacci(n);
    }
    const elapsed: number = Date.now() - start;
    console.log("BENCH:fibonacci:" + n + ":" + (elapsed / iterations));
}
