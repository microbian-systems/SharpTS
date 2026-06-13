using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #443: a compiled generator must produce the JavaScript <c>undefined</c>
/// sentinel — not CLR <c>null</c> or a stale value — for
/// <list type="bullet">
/// <item>the value a resumed <c>yield</c> expression evaluates to when no value is threaded back
/// in (every <c>for…of</c> / no-argument <c>.next()</c> resume), and</item>
/// <item>the completion value of a delegating <c>yield* expr</c>.</item>
/// </list>
/// Before the fix the compiler emitted <c>ldnull</c> at the resume point and for the <c>yield*</c>
/// completion, so e.g. <c>"x:" + (yield 1)</c> resumed to <c>"x:null"</c> instead of
/// <c>"x:undefined"</c>, and <c>yield* inner()</c> over a no-return generator produced the last
/// yielded value instead of <c>undefined</c>. The interpreter was already correct for these cases,
/// so each test below asserts identical output across both modes.
/// </summary>
public class GeneratorUndefinedValueTests
{
    // ---- Resumed `yield` value (the issue's primary case) ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ResumedYield_ConcatenatedAsString_IsUndefined(ExecutionMode mode)
    {
        // The repro from #443: for…of resumes with no sent value, so `yield 1` evaluates to undefined.
        var source = """
            function* g() { console.log("x:" + (yield 1)); }
            for (const v of g()) {}
            """;
        Assert.Equal("x:undefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ResumedYield_StoredInLocal_IsUndefined(ExecutionMode mode)
    {
        var source = """
            function* g() { const r = yield 1; console.log("r=" + r); }
            for (const v of g()) {}
            """;
        Assert.Equal("r=undefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ResumedYield_InArithmetic_IsNaN(ExecutionMode mode)
    {
        // undefined + 10 === NaN; exercises numeric coercion of the resumed sentinel, not just string.
        var source = """
            function* g() { console.log((yield 1) + 10); }
            for (const v of g()) {}
            """;
        Assert.Equal("NaN\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MultipleResumedYields_AreEachUndefined(ExecutionMode mode)
    {
        var source = """
            function* g() { console.log("t:" + (yield 1) + "|" + (yield 2)); }
            for (const v of g()) {}
            """;
        Assert.Equal("t:undefined|undefined\n", TestHarness.Run(source, mode));
    }

    // ---- `yield*` completion value ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void YieldStarOverArray_CompletionIsUndefined(ExecutionMode mode)
    {
        // A plain array has no return value, so the yield* completion value is undefined.
        var source = """
            function* g() { console.log("x:" + (yield* [1, 2])); }
            for (const v of g()) {}
            """;
        Assert.Equal("x:undefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void YieldStarOverString_CompletionIsUndefined(ExecutionMode mode)
    {
        var source = """
            function* g() { console.log("x:" + (yield* "ab")); }
            for (const v of g()) {}
            """;
        Assert.Equal("x:undefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void YieldStarOverGeneratorNoReturn_CompletionIsUndefined(ExecutionMode mode)
    {
        // inner runs off the end (no `return`), so the yield* completion value is undefined —
        // not the last yielded value (2), which the compiler previously produced.
        var source = """
            function* inner() { yield 1; yield 2; }
            function* g() { console.log("x:" + (yield* inner())); }
            for (const v of g()) {}
            """;
        Assert.Equal("x:undefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void YieldStarOverGeneratorBareReturn_CompletionIsUndefined(ExecutionMode mode)
    {
        var source = """
            function* inner() { yield 1; return; }
            function* g() { console.log("x:" + (yield* inner())); }
            for (const v of g()) {}
            """;
        Assert.Equal("x:undefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void YieldStarOverGeneratorWithReturn_CompletionIsReturnValue(ExecutionMode mode)
    {
        // The fix must not regress the case that already worked: an explicit `return <value>`
        // is the yield* completion value.
        var source = """
            function* inner() { yield 1; return "RET"; }
            function* g() { const r = yield* inner(); console.log("got:" + r); }
            for (const v of g()) {}
            """;
        Assert.Equal("got:RET\n", TestHarness.Run(source, mode));
    }

    // ---- Direct `.next().value` completion value ----
    // These assert spec-correct behavior in COMPILED mode. The interpreter still reports `null`
    // here (a separate, pre-existing gap tracked by #480), so they are compiled-only for now.

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void OffEndCompletionValue_IsUndefined_Compiled(ExecutionMode mode)
    {
        var source = """
            function* g() { yield 1; }
            const it = g();
            console.log("a:" + it.next().value);
            console.log("b:" + it.next().value);
            """;
        Assert.Equal("a:1\nb:undefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void BareReturnCompletionValue_IsUndefined_Compiled(ExecutionMode mode)
    {
        var source = """
            function* g() { yield 1; return; }
            const it = g();
            console.log("a:" + it.next().value);
            console.log("b:" + it.next().value);
            """;
        Assert.Equal("a:1\nb:undefined\n", TestHarness.Run(source, mode));
    }
}
