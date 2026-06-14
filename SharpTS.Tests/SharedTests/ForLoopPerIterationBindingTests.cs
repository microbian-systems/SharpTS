using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #633: each iteration of a <c>for (let/const …)</c> loop must get its own
/// binding for the loop variable(s) (ECMA-262 13.7.4 CreatePerIterationEnvironment), so closures
/// (and generators) created in different iterations capture distinct values.
///
/// <para>Before the fix the tree-walking interpreter reused a single environment slot for the loop
/// variable across iterations, so a closure capturing the loop variable read its <i>final</i> value
/// (e.g. <c>3,3,3</c> instead of <c>0,1,2</c>). Compiled mode (after the #431 box fix) already
/// snapshots per iteration, so this was an interpreter-vs-compiler divergence as well as a
/// conformance bug. The fix copies the loop variables into a fresh sibling environment per
/// iteration in <c>VisitFor</c> / <c>ExecuteForAsyncVT</c>.</para>
///
/// <para><c>var</c> declarations and bare-expression initializers share a single binding (no
/// per-iteration copy) — the negative cases below assert closures over those still read the final
/// value, matching JS.</para>
/// </summary>
public class ForLoopPerIterationBindingTests
{
    // ---- Headline repro: arrow capturing the loop `let` ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrowCapturesLetLoopVar_PerIteration(ExecutionMode mode)
    {
        var source = """
            const fns: any[] = [];
            for (let k = 0; k < 3; k++) { fns.push(() => k); }
            console.log(fns.map((f: any) => f()).join(","));
            """;
        Assert.Equal("0,1,2\n", TestHarness.Run(source, mode));
    }

    // The #622 headline repro: a generator created per iteration capturing the loop variable.
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GeneratorCapturesLetLoopVar_PerIteration(ExecutionMode mode)
    {
        var source = """
            const gens: any[] = [];
            for (let k = 0; k < 3; k++) { function* g() { yield k; } gens.push(g()); }
            console.log(gens.map((it: any) => it.next().value).join(","));
            """;
        Assert.Equal("0,1,2\n", TestHarness.Run(source, mode));
    }

    // ---- Multi-declarator: every declared name is per-iteration ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MultiDeclarator_AllNamesPerIteration(ExecutionMode mode)
    {
        var source = """
            const a: any[] = [];
            for (let i = 0, j = 10; i < 3; i++, j++) { a.push(() => i + ":" + j); }
            console.log(a.map((f: any) => f()).join(","));
            """;
        Assert.Equal("0:10,1:11,2:12\n", TestHarness.Run(source, mode));
    }

    // ---- continue keeps per-iteration semantics ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ContinuePath_StillPerIteration(ExecutionMode mode)
    {
        var source = """
            const c: any[] = [];
            for (let i = 0; i < 4; i++) { if (i === 2) continue; c.push(() => i); }
            console.log(c.map((f: any) => f()).join(","));
            """;
        Assert.Equal("0,1,3\n", TestHarness.Run(source, mode));
    }

    // ---- Nested loops: inner closure captures both per-iteration bindings ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NestedLoops_BothBindingsPerIteration(ExecutionMode mode)
    {
        var source = """
            const d: any[] = [];
            for (let i = 0; i < 2; i++) { for (let j = 0; j < 2; j++) { d.push(() => "" + i + j); } }
            console.log(d.map((f: any) => f()).join(","));
            """;
        Assert.Equal("00,01,10,11\n", TestHarness.Run(source, mode));
    }

    // ---- Single-statement body (no block braces) ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NoBraceBody_PerIteration(ExecutionMode mode)
    {
        var source = """
            const fns: any[] = [];
            for (let i = 0; i < 3; i++) fns.push(() => i);
            console.log(fns.map((f: any) => f()).join(","));
            """;
        Assert.Equal("0,1,2\n", TestHarness.Run(source, mode));
    }

    // ---- Mutating the loop var inside the body is reflected in that iteration's capture ----
    // i becomes i+10 at capture time, then i-10 before the iteration ends, so the per-iteration
    // binding settles back to the loop value; the per-iteration copy then carries that forward.
    // Interpreted-only: compiled mode snapshots the loop var at a different point and yields
    // 10,11,12 here (a pre-existing per-iteration-snapshot timing gap, tracked by #650). The
    // interpreter reads the live binding slot, matching node (0,1,2).
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void BodyMutatesLoopVar_CapturesLiveSlot(ExecutionMode mode)
    {
        var source = """
            const g: any[] = [];
            for (let i = 0; i < 3; i++) { i = i + 10; g.push(() => i); i = i - 10; }
            console.log(g.map((f: any) => f()).join(","));
            """;
        Assert.Equal("0,1,2\n", TestHarness.Run(source, mode));
    }

    // ---- Negative: `var` is function-scoped — one shared binding, closures read the final value ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void VarLoopVar_SharedBinding_ReadsFinal(ExecutionMode mode)
    {
        var source = """
            const fns: any[] = [];
            for (var k = 0; k < 3; k++) { fns.push(() => k); }
            console.log(fns.map((f: any) => f()).join(","));
            """;
        Assert.Equal("3,3,3\n", TestHarness.Run(source, mode));
    }

    // ---- Negative: a bare-expression initializer assigns an outer var — one shared binding ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExpressionInitializer_OuterVar_ReadsFinal(ExecutionMode mode)
    {
        var source = """
            let m = 0;
            const fns: any[] = [];
            for (m = 0; m < 3; m++) { fns.push(() => m); }
            console.log(fns.map((f: any) => f()).join(","));
            """;
        Assert.Equal("3,3,3\n", TestHarness.Run(source, mode));
    }

    // ---- A fresh per-iteration const is unaffected (already correct in both modes) ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerConstSnapshot_StillWorks(ExecutionMode mode)
    {
        var source = """
            const fns: any[] = [];
            for (let i = 0; i < 3; i++) { const v = i * 2; fns.push(() => v); }
            console.log(fns.map((f: any) => f()).join(","));
            """;
        Assert.Equal("0,2,4\n", TestHarness.Run(source, mode));
    }

    // ---- Async function body exercises the ExecuteForAsyncVT per-iteration path ----
    // Interpreted-only: compiled async-function state machines don't create per-iteration display
    // classes for a `for (let …)`, so the capture reads the final value (3,3,3) — a pre-existing
    // compiled gap (the #431 per-iteration box fix doesn't reach async bodies), tracked by #649.
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void AsyncFunctionBody_PerIteration(ExecutionMode mode)
    {
        var source = """
            async function main() {
                const fns: any[] = [];
                for (let k = 0; k < 3; k++) { fns.push(() => k); }
                console.log(fns.map((f: any) => f()).join(","));
            }
            main();
            """;
        Assert.Equal("0,1,2\n", TestHarness.Run(source, mode));
    }
}
