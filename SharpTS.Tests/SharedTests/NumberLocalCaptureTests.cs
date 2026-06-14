using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #431: a value-type (number) local captured by a closure must be boxed
/// before being stored into the closure's object-typed display-class field. The captured-local
/// fallback in <c>ILEmitter.Calls.Closures.cs</c> (<c>EmitDisplayInstanceFieldPopulation</c>)
/// loaded the unboxed <c>double</c> straight into the field, leaving a <c>float64</c> where the
/// verifier expects a reference — invalid IL (<c>StackUnexpected</c>) that segfaulted / threw
/// <c>AccessViolationException</c> at runtime. The captured-parameter path already boxed; the local
/// path needed the same guard. Strings/objects (reference types) were unaffected, which is why the
/// bug only surfaced for number-typed captures.
/// </summary>
public class NumberLocalCaptureTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NumberLoopLocal_CapturedByArrow(ExecutionMode mode)
    {
        // The #431 repro: a per-iteration number const captured by an arrow.
        var source = """
            const fns: any[] = [];
            for (let i = 0; i < 3; i++) { const v = i; fns.push(() => v); }
            console.log(fns.map((f: any) => f()).join(","));
            """;
        Assert.Equal("0,1,2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NumberBlockConst_CapturedByArrow(ExecutionMode mode)
    {
        var source = """
            { const base = 5; const add = (n: number): number => n + base; console.log(add(1)); }
            """;
        Assert.Equal("6\n", TestHarness.Run(source, mode));
    }

    [Fact]
    public void NumberLoopLocal_CapturedByArrow_CompiledIsConformant()
    {
        // Compiled mode snapshots the loop variable per iteration, so capturing the `let` binding
        // itself (not a fresh per-iteration const) is also conformant: 0,1,2.
        var source = """
            const fns: any[] = [];
            for (let k = 0; k < 3; k++) { fns.push(() => k); }
            console.log(fns.map((f: any) => f()).join(","));
            """;
        Assert.Equal("0,1,2\n", TestHarness.RunCompiled(source));
    }
}
