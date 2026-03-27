function countPrimes(n: number): number {
    let isPrime: boolean[] = [];
    for (let i: number = 0; i < n; i++) {
        isPrime[i] = true;
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
        if (isPrime[i]) {
            count = count + 1;
        }
    }
    return count;
}

const params: number[] = [1000, 10000, 100000];
const warmup: number = 5;
const iterations: number = 10;

for (let p: number = 0; p < params.length; p++) {
    const n: number = params[p];
    for (let w: number = 0; w < warmup; w++) {
        countPrimes(n);
    }
    const start: number = Date.now();
    for (let i: number = 0; i < iterations; i++) {
        countPrimes(n);
    }
    const elapsed: number = Date.now() - start;
    console.log("BENCH:count-primes:" + n + ":" + (elapsed / iterations));
}
