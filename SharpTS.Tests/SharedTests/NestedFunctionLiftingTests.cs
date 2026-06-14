using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for nested function-like lowering on the compile path (#470, #501, #583).
///
/// The compiler relocates non-capturing nested generator/async/state-machine-nested function
/// declarations to the module top level (<c>Compilation/NestedFunctionLifter.cs</c>) so the mature
/// top-level state-machine pipeline can lower them. These tests assert interpreter/compiler parity
/// for the supported (non-capturing) shapes and pin the documented limitations (#583 §1/§2).
/// </summary>
public class NestedFunctionLiftingTests
{
    // ── #470: a plain function declared inside a state-machine body ──────────────────────────────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PlainFunction_NestedInGeneratorBody_IsCallable(ExecutionMode mode)
    {
        var source = """
            function* outer(): Generator<number> {
                function helper(): number { return 7; }
                yield helper();
            }
            for (const v of outer()) console.log(v);
            """;
        Assert.Equal("7\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PlainFunction_NestedInAsyncBody_IsCallable(ExecutionMode mode)
    {
        var source = """
            async function outer(): Promise<number> {
                function helper(): number { return 9; }
                return helper();
            }
            outer().then(r => console.log(r));
            """;
        Assert.Equal("9\n", TestHarness.Run(source, mode));
    }

    // ── #501: a nested generator/async function declaration ──────────────────────────────────────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_NestedInPlainFunction_IsCallable(ExecutionMode mode)
    {
        var source = """
            function outer(): number[] {
                function* g(): Generator<number> { yield 42; }
                return [...g()];
            }
            console.log(JSON.stringify(outer()));
            """;
        Assert.Equal("[42]\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_NestedInGeneratorBody_NonCapturing_IsCallable(ExecutionMode mode)
    {
        var source = """
            function* outer(): Generator<number> {
                function* inner(): Generator<number> { yield 1; yield 2; }
                yield* inner();
                yield 3;
            }
            console.log(JSON.stringify([...outer()]));
            """;
        Assert.Equal("[1,2,3]\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunction_NestedInPlainFunction_IsCallable(ExecutionMode mode)
    {
        var source = """
            function outer(): Promise<number> {
                async function inner(): Promise<number> { return 5; }
                return inner();
            }
            outer().then(r => console.log(r));
            """;
        Assert.Equal("5\n", TestHarness.Run(source, mode));
    }

    // ── Recursion: self-reference must resolve to the relocated declaration ───────────────────────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void RecursiveNestedFunction_InGeneratorBody(ExecutionMode mode)
    {
        var source = """
            function* outer(): Generator<number> {
                function fact(n: number): number { return n <= 1 ? 1 : n * fact(n - 1); }
                yield fact(5);
            }
            for (const v of outer()) console.log(v);
            """;
        Assert.Equal("120\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void RecursiveNestedGenerator_InPlainFunction(ExecutionMode mode)
    {
        var source = """
            function driver(): number[] {
                function* count(n: number): Generator<number> {
                    if (n > 0) { yield n; yield* count(n - 1); }
                }
                return [...count(3)];
            }
            console.log(JSON.stringify(driver()));
            """;
        Assert.Equal("[3,2,1]\n", TestHarness.Run(source, mode));
    }

    // ── Multiple siblings and name independence across scopes ────────────────────────────────────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MultipleIndependentNestedHelpers(ExecutionMode mode)
    {
        var source = """
            function* outer(): Generator<number> {
                function a(): number { return 1; }
                function b(): number { return 2; }
                yield a();
                yield b();
            }
            console.log(JSON.stringify([...outer()]));
            """;
        Assert.Equal("[1,2]\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SameNamedNestedHelpers_InDifferentFunctions_StayIndependent(ExecutionMode mode)
    {
        // Each relocated declaration gets a fresh unique top-level name, so two generators that both
        // declare a nested `h` do not collide.
        var source = """
            function* p(): Generator<number> { function h(): number { return 1; } yield h(); }
            function* q(): Generator<number> { function h(): number { return 2; } yield h(); }
            console.log(JSON.stringify([...p()]), JSON.stringify([...q()]));
            """;
        Assert.Equal("[1] [2]\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void LiftedHelper_DoesNotHijack_SameNamedBindingElsewhere(ExecutionMode mode)
    {
        // `a`'s nested `h` is relocated; `b`'s own (unrelated) nested `h` must keep returning 20.
        // Guards against a relocated top-level name shadowing a same-named binding in another scope.
        var source = """
            function* a(): Generator<number> { function h(): number { return 10; } yield h(); }
            function b(): number { function h(): number { return 20; } return h(); }
            console.log(JSON.stringify([...a()]), b());
            """;
        Assert.Equal("[10] 20\n", TestHarness.Run(source, mode));
    }

    // ── Deep nesting ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DeeplyNestedGenerators(ExecutionMode mode)
    {
        var source = """
            function outer(): number[] {
                function* g(): Generator<number> {
                    function* h(): Generator<number> { yield 99; }
                    yield* h();
                    yield 1;
                }
                return [...g()];
            }
            console.log(JSON.stringify(outer()));
            """;
        Assert.Equal("[99,1]\n", TestHarness.Run(source, mode));
    }

    // ── Generator value-references to a top-level function (the lift's alias relies on this) ──────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TopLevelFunction_ReferencedAsValueInsideGenerator(ExecutionMode mode)
    {
        // A top-level function used as a value (not a direct call) inside a generator body must
        // resolve to a callable function, not null.
        var source = """
            function helper(): number { return 7; }
            function* outer(): Generator<string> {
                const h = helper;
                yield typeof h;
                yield String(h());
            }
            for (const v of outer()) console.log(v);
            """;
        Assert.Equal("function\n7\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NestedHelper_YieldedAsValue_IsCallable(ExecutionMode mode)
    {
        var source = """
            function* makeGen(): Generator<any> {
                function helper(): number { return 1; }
                yield helper;
            }
            const fn = makeGen().next().value;
            console.log(typeof fn, fn());
            """;
        Assert.Equal("function 1\n", TestHarness.Run(source, mode));
    }

    // ── Documented limitation #583 §1: capturing nested function-likes are NOT lifted ────────────
    // The interpreter handles them via real closures; the compiler still cannot lower a nested
    // state-machine function that captures an enclosing local (it stays nested and fails to compile,
    // a clean failure — never a miscompile). This pins the interpreter behaviour.

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void CapturingNestedGenerator_Interpreted(ExecutionMode mode)
    {
        var source = """
            function* outer(): Generator<number> {
                const x = 10;
                function* inner(): Generator<number> { yield x; }
                yield* inner();
            }
            for (const v of outer()) console.log(v);
            """;
        Assert.Equal("10\n", TestHarness.Run(source, mode));
    }

    // A generator declared inside a module-level loop that captures the loop variable must NOT be
    // relocated: lifting it out of the loop would silently drop the capture. The compiler may decline
    // to lower it (a clean failure), but it must never produce the miscompiled empty values.
    [Fact]
    public void GeneratorCapturingModuleLevelLoopVar_IsNotMiscompiled()
    {
        var source = """
            const gens: any[] = [];
            for (let k = 0; k < 3; k++) { function* g() { yield k; } gens.push(g()); }
            console.log(gens.map((it: any) => it.next().value).join(","));
            """;
        string compiled;
        try { compiled = TestHarness.RunCompiled(source); }
        catch { compiled = "<compile-or-runtime-error>"; }
        // The bug produced ",," (three lost captures). Anything but that wrong value is acceptable
        // here — either a clean failure, or the correct interpreter result if lowering improves.
        Assert.NotEqual(",,\n", compiled);
    }
}
