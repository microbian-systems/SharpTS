using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for the guest-error identity fix at the interpreter's catch binding.
///
/// Two failure modes are pinned:
/// <list type="bullet">
/// <item><b>Over-typing</b>: a genuine guest <c>throw "RangeError: ..."</c> — a bare string that
/// merely looks like a runtime error message — must be caught verbatim as a string, never
/// re-typed into an <c>Error</c>. Coercion now applies only to translated <em>host</em>
/// exceptions, not to guest <c>throw</c> values.</item>
/// <item><b>Under-typing</b>: an internal runtime error (surfaced as a <c>"Runtime Error: ..."</c>
/// host message) caught by guest code must present as an <c>Error</c> instance so
/// <c>instanceof</c>/<c>.message</c> hold, matching JS and compiled mode — the prefix table
/// previously omitted the <c>"Runtime Error: "</c> wrapper.</item>
/// </list>
/// </summary>
public class CaughtErrorIdentityTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GuestThrownErrorShapedString_IsCaughtVerbatim_NotReTyped(ExecutionMode mode)
    {
        // A guest throw of an error-prefixed string is a plain string value in JS — it must NOT
        // be reconstructed into a typed Error by the catch binding's host-error recovery.
        var source = """
            try { throw "RangeError: hand-rolled, not a real error"; }
            catch (e: any) {
              console.log(typeof e);
              console.log(e);
              console.log(e instanceof Error);
            }
            """;

        Assert.Equal("string\nRangeError: hand-rolled, not a real error\nfalse\n",
            TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InternalRuntimeError_CaughtByGuest_IsErrorInstance(ExecutionMode mode)
    {
        // `1n ** -1n` is a runtime error in both modes; caught by guest code it must be an Error
        // instance (not a raw string), at parity between interpreter and compiled output.
        var source = """
            try { const r = 1n ** -1n; console.log("unreachable", r); }
            catch (e: any) { console.log(e instanceof Error); }
            """;

        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void InternalRuntimeError_CaughtByGuest_RecoversTypedError(ExecutionMode mode)
    {
        // Interpreter-specific: the "Runtime Error: " wrapper with no inner JS error name is
        // recovered as a generic Error carrying the unwrapped message. (Compiled mode surfaces a
        // different underlying .NET message, so the exact text is asserted for the interpreter only.)
        var source = """
            try { const r = 1n ** -1n; console.log("unreachable", r); }
            catch (e: any) {
              console.log(e.name);
              console.log(e.message);
            }
            """;

        Assert.Equal("Error\nBigInt exponent must be non-negative.\n", TestHarness.Run(source, mode));
    }
}
