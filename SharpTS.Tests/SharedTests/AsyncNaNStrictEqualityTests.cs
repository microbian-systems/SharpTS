using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #642: in the interpreter's async execution path, the number fast-path for
/// equality used <c>Double.Equals</c> (which treats <c>NaN.Equals(NaN)</c> as true) instead of
/// IEEE 754 <c>==</c>. So <c>NaN === NaN</c> inside an <c>async function</c> wrongly returned
/// <c>true</c> (and <c>NaN !== NaN</c> wrongly returned <c>false</c>), while top-level code, sync
/// functions, generators, and compiled mode were all correct (per ECMA-262 7.2.16 NaN is never
/// equal to anything, including itself).
///
/// <para>The fix mirrors the synchronous fast path (<c>EvaluateBinary</c> in
/// <c>Interpreter.Calls.cs</c>): <c>EvaluateBinaryAsync</c> now compares with <c>l == r</c>. These
/// tests assert the corrected NaN behavior in every async/generator state-machine context and pin
/// the already-correct contexts as cross-mode parity, plus guard that ordinary number equality is
/// unaffected by the change.</para>
/// </summary>
public class AsyncNaNStrictEqualityTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunction_NaNStrictEquality(ExecutionMode mode)
    {
        // The exact repro from #642.
        var source = """
            async function main() {
                console.log(NaN === NaN);
                const x = NaN;
                console.log(x === x);
                console.log(NaN !== NaN);
            }
            main();
            """;
        Assert.Equal("false\nfalse\ntrue\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunction_ComputedNaN_NotEqual(ExecutionMode mode)
    {
        // A NaN produced at runtime (not the literal) — exercises the same fast path.
        var source = """
            async function main() {
                const n = Math.sqrt(-1);
                console.log(n === n);
                console.log(n !== n);
            }
            main();
            """;
        Assert.Equal("false\ntrue\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunction_AfterAwait_NaNStillNotEqual(ExecutionMode mode)
    {
        // Equality evaluated after a suspension point still runs through the async fast path.
        var source = """
            async function main() {
                await Promise.resolve(0);
                console.log(NaN === NaN);
            }
            main();
            """;
        Assert.Equal("false\n", TestHarness.Run(source, mode));
    }

    // Interpreted-only: this issue (#642) is the interpreter async path. Compiled async ARROWS
    // still evaluate NaN strict equality with loose/Double.Equals semantics (NaN === NaN → true) —
    // a gap in #600's compiled coverage that does NOT affect compiled async *functions* or async
    // *generators* (both correct). Tracked by #648.
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void AsyncArrow_NaNStrictEquality_Interpreted(ExecutionMode mode)
    {
        var source = """
            const r = async () => {
                console.log(NaN === NaN);
                console.log(NaN !== NaN);
            };
            r();
            """;
        Assert.Equal("false\ntrue\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_NaNStrictEquality(ExecutionMode mode)
    {
        var source = """
            async function* g() {
                yield (NaN === NaN);
                yield (NaN !== NaN);
            }
            async function main() {
                for await (const x of g()) console.log(x);
            }
            main();
            """;
        Assert.Equal("false\ntrue\n", TestHarness.Run(source, mode));
    }

    // ---- Already-correct contexts: assert cross-mode parity so they don't regress ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TopLevel_NaNStrictEquality(ExecutionMode mode)
    {
        var source = """
            console.log(NaN === NaN);
            console.log(NaN !== NaN);
            """;
        Assert.Equal("false\ntrue\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_NaNStrictEquality(ExecutionMode mode)
    {
        var source = """
            function* g() {
                yield (NaN === NaN);
                yield (NaN !== NaN);
            }
            for (const x of g()) console.log(x);
            """;
        Assert.Equal("false\ntrue\n", TestHarness.Run(source, mode));
    }

    // ---- Guard: ordinary number equality is unaffected by the Double.Equals -> == change ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunction_OrdinaryNumberEquality_Unaffected(ExecutionMode mode)
    {
        var source = """
            async function main() {
                console.log(2 === 2);
                console.log(2 === 3);
                console.log(2 !== 3);
                console.log(0 === -0);
            }
            main();
            """;
        Assert.Equal("true\nfalse\ntrue\ntrue\n", TestHarness.Run(source, mode));
    }
}
