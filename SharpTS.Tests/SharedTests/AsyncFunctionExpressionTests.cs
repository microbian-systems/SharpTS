using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #635: an <c>async function</c> expression in expression position
/// (e.g. <c>const f = async function () {}</c>) must parse. Previously the parser only
/// accepted an async <em>arrow</em> after <c>async</c> and demanded a <c>(</c>, so any
/// <c>async function …</c> expression produced a parse error. Mirrors the existing
/// <c>function</c> / <c>function*</c> expression handling. Runs against both modes.
/// </summary>
public class AsyncFunctionExpressionTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunctionExpression_Anonymous_ReturnsPromise(ExecutionMode mode)
    {
        // The exact repro from #635.
        var source = """
            const af = async function () { return 9; };
            af().then((v: number) => console.log(v));
            """;
        Assert.Equal("9\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunctionExpression_Named_Works(ExecutionMode mode)
    {
        var source = """
            const af = async function compute() { return 42; };
            af().then((v: number) => console.log(v));
            """;
        Assert.Equal("42\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunctionExpression_WithAwaitBody_Works(ExecutionMode mode)
    {
        var source = """
            const af = async function () {
                const a = await Promise.resolve(10);
                const b = await Promise.resolve(20);
                return a + b;
            };
            af().then((v: number) => console.log(v));
            """;
        Assert.Equal("30\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunctionExpression_ImmediatelyInvoked_Works(ExecutionMode mode)
    {
        var source = """
            (async function () {
                console.log("start");
                const v = await Promise.resolve(5);
                console.log(v);
            })();
            """;
        Assert.Equal("start\n5\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunctionExpression_WithExplicitParams_Works(ExecutionMode mode)
    {
        var source = """
            const af = async function (x: number, y: number) { return x + y; };
            af(4, 3).then((v: number) => console.log(v));
            """;
        Assert.Equal("7\n", TestHarness.Run(source, mode));
    }

    [Fact]
    public void AsyncFunctionExpression_DefaultParameter_Applied_Interpreted()
    {
        // The async function expression honors a parameter default. Asserted interpreted-only
        // because compiled function expressions / arrows currently drop omitted-arg defaults
        // (pre-existing, not specific to async function expressions — tracked by #646).
        var source = """
            const af = async function (x: number, y: number = 3) { return x + y; };
            af(4).then((v: number) => console.log(v));
            """;
        Assert.Equal("7\n", TestHarness.RunInterpreted(source));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncArrow_StillParses_NoRegression(ExecutionMode mode)
    {
        // The async-arrow path the original code already handled must keep working.
        var source = """
            const af = async () => 9;
            af().then((v: number) => console.log(v));
            """;
        Assert.Equal("9\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGeneratorFunctionExpression_Works(ExecutionMode mode)
    {
        // `async function* () {}` is also enabled by the fix (FunctionExpression handles the
        // trailing `*`). Consumed by an async function declaration so it exercises the
        // GeneratorArrowLifter path in compiled mode. (for-await inside an async ARROW is a
        // separate, pre-existing compiled bug — see #645 — so it is deliberately not used here.)
        var source = """
            const ag = async function* () { yield 1; yield 2; yield 3; };
            async function run() {
                let sum = 0;
                for await (const x of ag()) sum += x;
                console.log(sum);
            }
            run();
            """;
        Assert.Equal("6\n", TestHarness.Run(source, mode));
    }
}
