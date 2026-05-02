// Iterator-helper benchmarks (issue #90 hot path).
// `any[]` so the array is typed as List<object> in compiled mode — that's
// the path that goes through ArrayMap / ArrayFilter / etc., where my
// LoadArrayLikeElement indirection adds 1 Ldsfld + 1 Brfalse per element.
// Typed `number[]` (List<double>) takes a different ArrayEmitter path that
// is unaffected; it would't measure the regression I want to bound.

function arrMap(arr: any[]): any[] {
    return arr.map(x => x * 2);
}

function arrFilter(arr: any[]): any[] {
    return arr.filter(x => x > 10);
}

function arrReduce(arr: any[]): any {
    return arr.reduce((a, b) => a + b, 0);
}

function arrForEach(arr: any[]): any {
    let s: number = 0;
    arr.forEach(x => { s = s + x; });
    return s;
}

function arrEvery(arr: any[]): boolean {
    return arr.every(x => x >= 0);
}

function arrFind(arr: any[]): any {
    return arr.find(x => x > 9999) ?? -1;
}
