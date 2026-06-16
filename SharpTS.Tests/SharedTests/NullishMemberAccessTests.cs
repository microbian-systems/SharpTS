using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// ECMA-262 RequireObjectCoercible (§13.3.2 / §13.3.3): a non-optional member
/// access on <c>undefined</c> or <c>null</c> must throw a guest <c>TypeError</c>
/// ("Cannot read properties of &lt;undefined|null&gt; (reading '&lt;key&gt;')"),
/// not silently yield <c>undefined</c> or surface a raw host "Runtime Error" string.
///
/// <para>#676 (interpreter): the property-access fallback threw a host
/// <c>InterpreterException</c> that a guest <c>catch</c> bound as a plain string.</para>
/// <para>#701 (compiled): the emitted property/index read silently evaluated to
/// <c>undefined</c> instead of throwing.</para>
///
/// <para><b>Interpreter vs compiled on a genuine <c>null</c> receiver:</b> the
/// interpreter represents JS <c>null</c> as CLR <c>null</c> (sloppy-mode <c>this</c>
/// is a distinct globalThis object), so it can throw on both. Compiled mode
/// represents sloppy-mode <c>this</c> as CLR <c>null</c> too, so — like the
/// established method-call-callee guard — it only throws on the unambiguous
/// <c>$Undefined</c> singleton and leaves CLR <c>null</c> coercible. The
/// <c>null</c>-receiver cases below are therefore interpreter-only.</para>
/// </summary>
public class NullishMemberAccessTests
{
    // The guest harness prints the caught value's shape so we assert the guest
    // sees a real TypeError (object + instanceof + name), plus the message.
    private const string ProbeFull =
        "catch (e: any) { console.log(typeof e, e instanceof TypeError, e.name, \"|\" + e.message); }";
    private const string ProbeIdentity =
        "catch (e: any) { console.log(typeof e, e instanceof TypeError, e.name); }";

    // ----- undefined receiver: dot read (#676 / #701 core) -----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void UndefinedDotRead_ThrowsTypeError(ExecutionMode mode)
    {
        var source = $$"""
            const o: any = undefined;
            try { const y = o.foo; console.log("NOTHROW " + y); }
            {{ProbeFull}}
            """;
        Assert.Equal(
            "object true TypeError |Cannot read properties of undefined (reading 'foo')\n",
            TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void UndefinedBracketRead_StringKey_ThrowsTypeError(ExecutionMode mode)
    {
        var source = $$"""
            const o: any = undefined;
            try { const y = o["bar"]; console.log("NOTHROW " + y); }
            {{ProbeFull}}
            """;
        Assert.Equal(
            "object true TypeError |Cannot read properties of undefined (reading 'bar')\n",
            TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void UndefinedBracketRead_NumericKey_ThrowsTypeError(ExecutionMode mode)
    {
        var source = $$"""
            const o: any = undefined;
            try { const y = o[0]; console.log("NOTHROW " + y); }
            {{ProbeFull}}
            """;
        Assert.Equal(
            "object true TypeError |Cannot read properties of undefined (reading '0')\n",
            TestHarness.Run(source, mode));
    }

    // ----- undefined receiver: method call (the #676 repro shape) -----
    // Identity-only: the interpreter routes the callee through the property read
    // ("Cannot read properties of …"), while compiled mode reaches its method-call
    // guard — both raise a real TypeError, which is what #676 was about.

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void UndefinedMethodCall_ThrowsTypeError(ExecutionMode mode)
    {
        var source = $$"""
            const o: any = undefined;
            try { o.foo(); console.log("NOTHROW"); }
            {{ProbeIdentity}}
            """;
        Assert.Equal("object true TypeError\n", TestHarness.Run(source, mode));
    }

    // ----- null receiver (interpreter-only; see class remarks) -----

    [Fact]
    public void NullDotRead_ThrowsTypeError_Interpreted()
    {
        var source = $$"""
            const o: any = null;
            try { const y = o.foo; console.log("NOTHROW " + y); }
            {{ProbeFull}}
            """;
        Assert.Equal(
            "object true TypeError |Cannot read properties of null (reading 'foo')\n",
            TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void NullBracketRead_ThrowsTypeError_Interpreted()
    {
        var source = $$"""
            const o: any = null;
            try { const y = o["bar"]; console.log("NOTHROW " + y); }
            {{ProbeFull}}
            """;
        Assert.Equal(
            "object true TypeError |Cannot read properties of null (reading 'bar')\n",
            TestHarness.RunInterpreted(source));
    }

    // ----- async context exercises the base (state-machine) emitter for #701 -----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void UndefinedDotRead_InsideAsync_ThrowsTypeError(ExecutionMode mode)
    {
        var source = $$"""
            async function run(): Promise<void> {
                const o: any = undefined;
                try { const y = o.foo; console.log("NOTHROW " + y); }
                {{ProbeFull}}
            }
            run();
            """;
        Assert.Equal(
            "object true TypeError |Cannot read properties of undefined (reading 'foo')\n",
            TestHarness.Run(source, mode));
    }

    // ----- regression: optional chaining still short-circuits (does NOT throw) -----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void OptionalDotChain_OnUndefined_ReturnsUndefined(ExecutionMode mode)
    {
        var source = """
            const o: any = undefined;
            const y = o?.foo;
            console.log(y === undefined ? "undefined-ok" : "WRONG:" + y);
            """;
        Assert.Equal("undefined-ok\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void OptionalBracketChain_OnUndefined_ReturnsUndefined(ExecutionMode mode)
    {
        var source = """
            const o: any = undefined;
            const y = o?.["bar"];
            console.log(y === undefined ? "undefined-ok" : "WRONG:" + y);
            """;
        Assert.Equal("undefined-ok\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void OptionalDotChain_OnUndefined_InsideAsync_ReturnsUndefined(ExecutionMode mode)
    {
        // Exercises the base EmitGet optional short-circuit added for the
        // async/generator state-machine emitters alongside the #701 guard.
        var source = """
            async function run(): Promise<void> {
                const o: any = undefined;
                const y = o?.foo;
                console.log(y === undefined ? "undefined-ok" : "WRONG:" + y);
            }
            run();
            """;
        Assert.Equal("undefined-ok\n", TestHarness.Run(source, mode));
    }

    // ----- regression: real reads still work after the guard -----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DefinedReceiver_DotAndBracketRead_StillWork(ExecutionMode mode)
    {
        var source = """
            const o: any = { foo: 42, "1": "one" };
            console.log(o.foo, o["foo"], o[1]);
            """;
        Assert.Equal("42 42 one\n", TestHarness.Run(source, mode));
    }
}
