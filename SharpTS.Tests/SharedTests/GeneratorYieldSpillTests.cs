using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #414: a value spilled before a <c>yield</c> (or <c>yield*</c>) and
/// used after it must survive the generator's MoveNext re-entry. Before the fix the spilled
/// operand lived in an IL local that the re-entry wiped, so the compiler replaced the prefix
/// with a garbage value (e.g. <c>"a" + (yield "b")</c> resumed to <c>"0"</c> instead of
/// <c>"a…"</c>). The fix mirrors live spill locals to state-machine fields at the yield and
/// restores them on resume.
///
/// These assert the spilled prefix is <em>present</em> rather than the exact line, because the
/// value a resumed <c>yield</c> expression evaluates to differs between modes (interpreter:
/// <c>undefined</c>; compiler: <c>null</c>) — a separate, pre-existing gap (compiled generators
/// don't thread <c>.next(arg)</c> / the JS <c>undefined</c> sentinel back into the yield
/// expression). The prefix-survival is what #414 fixes and is identical across modes.
/// </summary>
public class GeneratorYieldSpillTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BinaryConcat_PrefixSurvivesYield(ExecutionMode mode)
    {
        var source = """
            function* g() { console.log("PFX:" + (yield 1)); }
            for (const x of g()) {}
            """;
        // Pre-fix compiled output was "0" (prefix lost). The prefix must now be present.
        Assert.StartsWith("PFX:", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MultipleYields_PrefixSurvives(ExecutionMode mode)
    {
        var source = """
            function* g() { console.log("TAG:" + (yield 1) + "|" + (yield 2)); }
            for (const x of g()) {}
            """;
        Assert.Contains("TAG:", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TemplateLiteral_PrefixSurvivesYield(ExecutionMode mode)
    {
        var source = """
            function* g() { console.log(`[${"head"}-${yield 1}]`); }
            for (const x of g()) {}
            """;
        Assert.Contains("[head-", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void YieldStar_PrefixSurvivesDelegation(ExecutionMode mode)
    {
        var source = """
            function* inner() { yield 1; yield 2; }
            function* g() { console.log("PFX:" + (yield* inner())); }
            for (const x of g()) {}
            """;
        Assert.StartsWith("PFX:", TestHarness.Run(source, mode));
    }
}
