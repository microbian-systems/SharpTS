using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for the typed-array-local promotion optimization (#857/#860): a provably
/// non-escaping number[]/boolean[] local with an empty-array-literal initializer is
/// compiled to a concrete List&lt;double&gt;/List&lt;bool&gt; slot with unboxed element access.
///
/// These run against BOTH the interpreter and the compiler. The positive cases exercise
/// the promoted fast paths; the escape cases must NOT be promoted (they fall back to the
/// general $Array path) and must still produce correct results — i.e. interpreter/compiled
/// parity must hold even when the array is passed, returned, spread, iterated, compared,
/// or has holes. A wrong escape rule would surface here as a compiled-mode mismatch.
/// </summary>
public class ArrayLocalPromotionTests
{
    // ── Positive cases: promotable shapes ──────────────────────────────────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Promoted_BoolSieve_CountsPrimes(ExecutionMode mode)
    {
        // The count-primes shape: const boolean[] built by push, then index read/write.
        var source = """
            function countPrimes(n: number): number {
                if (n <= 2) return 0;
                const isPrime: boolean[] = [];
                for (let i: number = 0; i < n; i++) { isPrime.push(true); }
                isPrime[0] = false;
                isPrime[1] = false;
                for (let i: number = 2; i * i < n; i++) {
                    if (isPrime[i]) {
                        for (let j: number = i * i; j < n; j = j + i) { isPrime[j] = false; }
                    }
                }
                let count: number = 0;
                for (let i: number = 0; i < n; i++) { if (isPrime[i]) count = count + 1; }
                return count;
            }
            console.log(countPrimes(20));
            """;

        Assert.Equal("8\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Promoted_NumberArray_PushIndexLength(ExecutionMode mode)
    {
        var source = """
            function build(): number {
                const xs: number[] = [];
                for (let i: number = 0; i < 5; i++) { xs.push(i * 2); }
                xs[0] = 100;
                let sum: number = 0;
                for (let i: number = 0; i < xs.length; i++) { sum = sum + xs[i]; }
                return sum;
            }
            console.log(build());
            """;

        // 100 + 2 + 4 + 6 + 8 = 120
        Assert.Equal("120\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Promoted_IndexWrite_ReturnsAssignedValue(ExecutionMode mode)
    {
        // `arr[i] = v` is an expression whose value is the assigned RHS.
        var source = """
            function f(): number {
                const xs: number[] = [];
                xs.push(0);
                const v: number = (xs[0] = 42);
                return v + xs[0];
            }
            console.log(f());
            """;

        Assert.Equal("84\n", TestHarness.Run(source, mode));
    }

    // ── Escape cases: must fall back, must stay correct ────────────────────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Escape_PassedToFunctionThatPushes(ExecutionMode mode)
    {
        // Passing the array as an argument escapes — a bare List<T> can't be mutated
        // through the $Array-expecting callee, so this must NOT be promoted.
        var source = """
            function fill(a: number[]): void { a.push(1); a.push(2); a.push(3); }
            function go(): number {
                const xs: number[] = [];
                fill(xs);
                let sum: number = 0;
                for (let i: number = 0; i < xs.length; i++) { sum = sum + xs[i]; }
                return sum;
            }
            console.log(go());
            """;

        Assert.Equal("6\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Escape_Returned(ExecutionMode mode)
    {
        var source = """
            function make(): number[] {
                const xs: number[] = [];
                xs.push(7);
                xs.push(8);
                return xs;
            }
            const r: number[] = make();
            console.log(r.length);
            console.log(r[1]);
            """;

        Assert.Equal("2\n8\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Escape_Spread(ExecutionMode mode)
    {
        var source = """
            function f(): number {
                const xs: number[] = [];
                xs.push(1); xs.push(2); xs.push(3);
                const ys: number[] = [...xs, 4];
                let sum: number = 0;
                for (let i: number = 0; i < ys.length; i++) { sum = sum + ys[i]; }
                return sum;
            }
            console.log(f());
            """;

        Assert.Equal("10\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Escape_ForOf(ExecutionMode mode)
    {
        var source = """
            function f(): number {
                const xs: number[] = [];
                xs.push(10); xs.push(20); xs.push(30);
                let sum: number = 0;
                for (const x of xs) { sum = sum + x; }
                return sum;
            }
            console.log(f());
            """;

        Assert.Equal("60\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Escape_OtherMethod_Map(ExecutionMode mode)
    {
        // .map is not a permitted use → no promotion; result must still be correct.
        var source = """
            function f(): number {
                const xs: number[] = [];
                xs.push(1); xs.push(2); xs.push(3);
                const doubled: number[] = xs.map((x: number): number => x * 2);
                let sum: number = 0;
                for (let i: number = 0; i < doubled.length; i++) { sum = sum + doubled[i]; }
                return sum;
            }
            console.log(f());
            """;

        Assert.Equal("12\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Escape_OutOfRangeReadIsUndefined(ExecutionMode mode)
    {
        // A read past the end must yield undefined (JS semantics). Reading `xs[5]`
        // anywhere is fine for a promoted array only if it stays in range; here the
        // index expression escapes nothing, but an OOB read must match the interpreter.
        // Because the array is also logged (escape), it is not promoted — but this pins
        // the fallback semantics regardless.
        var source = """
            const xs: number[] = [];
            xs.push(1);
            console.log(xs);
            console.log(xs[5]);
            """;

        Assert.Equal("[1]\nundefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Escape_AnyTypedElementWrite_NotPromoted(ExecutionMode mode)
    {
        // An `any`-typed element write disqualifies promotion (the typed setter would
        // coerce a runtime undefined to NaN/false — the array analogue of the #367 taint
        // guard). We assert interpreter/compiled parity only on a NUMBER-typed any value
        // here, which both modes agree on; the guard's job is purely to keep codegen on
        // the pre-existing general path, which the count-primes IL inspection confirms.
        var source = """
            function f(): number {
                const xs: number[] = [];
                xs.push(1);
                const u: any = 41;
                xs[1] = u;
                return xs[0] + xs[1];
            }
            console.log(f());
            """;

        Assert.Equal("42\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Promoted_AutoExtendOnIndexWrite(ExecutionMode mode)
    {
        // Writing at index == length extends the array by one (auto-extend path).
        var source = """
            function f(): number {
                const xs: number[] = [];
                xs.push(1);
                xs[1] = 2;
                xs[2] = 3;
                let sum: number = 0;
                for (let i: number = 0; i < xs.length; i++) { sum = sum + xs[i]; }
                return sum;
            }
            console.log(f());
            """;

        Assert.Equal("6\n", TestHarness.Run(source, mode));
    }
}
