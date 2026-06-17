using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #634: <c>GeneratorArrowLifter.RewriteStmt</c> previously only recursed
/// into a subset of statement kinds, so a generator function expression declared inside a
/// <c>for</c> / <c>for-of</c> / <c>for-in</c> / <c>do-while</c> / <c>try</c> / <c>switch</c> /
/// labeled statement was never lifted to a top-level declaration. Without the lift the type
/// checker rejects its <c>yield</c> with "'yield' is only valid inside a generator function"
/// (the generator-expression context is only established via the lift). These exercise each
/// newly-traversed statement kind in both interpreted and compiled modes.
/// </summary>
public class GeneratorExpressionStatementContextTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GeneratorExpression_InForLoop_IsLifted(ExecutionMode mode)
    {
        // The exact repro from #634.
        var source = """
            for (let k = 0; k < 1; k++) {
                const g = function* () { yield 99; };
                console.log(g().next().value);
            }
            """;
        Assert.Equal("99\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GeneratorExpression_InForOf_IsLifted(ExecutionMode mode)
    {
        var source = """
            for (const _ of [0]) {
                const g = function* () { yield 1; };
                console.log(g().next().value);
            }
            """;
        Assert.Equal("1\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GeneratorExpression_InForIn_IsLifted(ExecutionMode mode)
    {
        var source = """
            for (const _k in { a: 1 }) {
                const g = function* () { yield 2; };
                console.log(g().next().value);
            }
            """;
        Assert.Equal("2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GeneratorExpression_InDoWhile_IsLifted(ExecutionMode mode)
    {
        var source = """
            let once = true;
            do {
                const g = function* () { yield 3; };
                console.log(g().next().value);
                once = false;
            } while (once);
            """;
        Assert.Equal("3\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GeneratorExpression_InTryCatchFinally_IsLifted(ExecutionMode mode)
    {
        var source = """
            try {
                const g = function* () { yield 4; };
                console.log(g().next().value);
            } finally {
                const g = function* () { yield 5; };
                console.log(g().next().value);
            }
            """;
        Assert.Equal("4\n5\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GeneratorExpression_InSwitchCase_IsLifted(ExecutionMode mode)
    {
        var source = """
            switch (1) {
                case 1: {
                    const g = function* () { yield 6; };
                    console.log(g().next().value);
                    break;
                }
            }
            """;
        Assert.Equal("6\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GeneratorExpression_InLabeledBlock_IsLifted(ExecutionMode mode)
    {
        var source = """
            outer: {
                const g = function* () { yield 7; };
                console.log(g().next().value);
            }
            """;
        Assert.Equal("7\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GeneratorExpression_NestedInForInsideTry_IsLifted(ExecutionMode mode)
    {
        // Nested traversal: try → for → block. Each new arm must recurse so the inner
        // generator expression still reaches the lifter. The body yields a constant
        // (it does not close over the loop variable, which the lift would move out of
        // scope — see the capture limitation tracked by #534).
        var source = """
            try {
                for (let i = 0; i < 2; i++) {
                    const g = function* () { yield 8; };
                    console.log(g().next().value);
                }
            } catch (e) {
                console.log("unreachable");
            }
            """;
        Assert.Equal("8\n8\n", TestHarness.Run(source, mode));
    }
}
