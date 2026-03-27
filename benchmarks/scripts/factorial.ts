function factorial(n: number): number {
    let result: number = 1;
    for (let i: number = 2; i <= n; i++) {
        result = result * i;
    }
    return result;
}

const params: number[] = [100, 1000, 10000];
const warmup: number = 50;
const iterations: number = 500;

for (let p: number = 0; p < params.length; p++) {
    const n: number = params[p];
    for (let w: number = 0; w < warmup; w++) {
        factorial(n);
    }
    const start: number = Date.now();
    for (let i: number = 0; i < iterations; i++) {
        factorial(n);
    }
    const elapsed: number = Date.now() - start;
    console.log("BENCH:factorial:" + n + ":" + (elapsed / iterations));
}
