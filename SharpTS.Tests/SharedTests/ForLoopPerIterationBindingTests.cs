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
    // Interpreted-only: compiled mode SNAPSHOTS the loop var's value at closure-creation time
    // (i+10 == 10/11/12) rather than reference-capturing the per-iteration binding (whose end-of-
    // body value is 0/1/2). This snapshot-timing gap is uniform across compiled contexts (top
    // level, sync/async function bodies) and is tracked by #650; a full fix needs a per-iteration
    // binding cell that closures reference-capture. The interpreter reads the live binding slot,
    // matching node (0,1,2).
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

    // ---- Closures over a loop `let` inside a FUNCTION body are per-iteration in both modes (#649) ----
    // Before #649 the compiler promoted a captured `for (let …)` loop variable to the function's
    // single shared display class, so every closure read the loop's final value (3,3,3) inside any
    // function body — sync OR async. (Top level already stayed correct because the loop variable
    // remained a local that each closure snapshots.) The fix keeps these per-iteration loop bindings
    // out of the function display class, so they are snapshotted per iteration like the top-level case.

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SyncFunctionBody_PerIteration(ExecutionMode mode)
    {
        var source = """
            function main(): string {
                const fns: any[] = [];
                for (let k = 0; k < 3; k++) { fns.push(() => k); }
                return fns.map((f: any) => f()).join(",");
            }
            console.log(main());
            """;
        Assert.Equal("0,1,2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
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

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassMethodBody_PerIteration(ExecutionMode mode)
    {
        var source = """
            class C {
                run(): string {
                    const fns: any[] = [];
                    for (let k = 0; k < 3; k++) { fns.push(() => k); }
                    return fns.map((f: any) => f()).join(",");
                }
            }
            console.log(new C().run());
            """;
        Assert.Equal("0,1,2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrowBody_PerIteration(ExecutionMode mode)
    {
        var source = """
            const outer = () => {
                const fns: any[] = [];
                for (let k = 0; k < 3; k++) { fns.push(() => k); }
                return fns.map((f: any) => f()).join(",");
            };
            console.log(outer());
            """;
        Assert.Equal("0,1,2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunctionBody_PerIteration(ExecutionMode mode)
    {
        var source = """
            function host(): string {
                function inner(): string {
                    const fns: any[] = [];
                    for (let k = 0; k < 3; k++) { fns.push(() => k); }
                    return fns.map((f: any) => f()).join(",");
                }
                return inner();
            }
            console.log(host());
            """;
        Assert.Equal("0,1,2\n", TestHarness.Run(source, mode));
    }

    // A function-scoped binding declared OUTSIDE the loop stays SHARED (one binding) even when a
    // loop-body closure also captures the per-iteration loop variable: the per-iteration exclusion
    // must apply only to the loop binding, not to ordinary captured locals. `outer` ends at 3 for
    // every closure; `k` is per-iteration.
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FunctionBody_OuterCapturedVarStaysShared(ExecutionMode mode)
    {
        var source = """
            function f(): string {
                let outer = 0;
                const fns: any[] = [];
                for (let k = 0; k < 3; k++) { fns.push(() => outer + ":" + k); outer++; }
                return fns.map((g: any) => g()).join(",");
            }
            console.log(f());
            """;
        Assert.Equal("3:0,3:1,3:2\n", TestHarness.Run(source, mode));
    }
}
