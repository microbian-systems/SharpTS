using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression for a compiler gap surfaced by the attempted <c>util</c> stdlib
/// migration: global class names (<c>Promise</c>, <c>Buffer</c>, <c>TextEncoder</c>,
/// <c>TextDecoder</c>, <c>FinalizationRegistry</c>, <c>Proxy</c>, <c>BroadcastChannel</c>)
/// were not resolvable as bare identifiers — only via constructor-expression
/// pattern matching (<c>new TextEncoder()</c>) or namespace-call pattern matching
/// (<c>Promise.resolve()</c>). This made <c>x instanceof Promise</c>,
/// <c>typeof Buffer === 'function'</c>, and any stdlib module that carries one
/// of these as a value throw <c>Undefined variable 'X'</c>.
/// </summary>
/// <remarks>
/// Fixed by extending the type checker's bare-identifier allowlist
/// (<see cref="SharpTS.TypeSystem.TypeChecker.CheckVariable"/>), registering
/// <c>TextEncoder</c>/<c>TextDecoder</c> in <c>BuiltInConstructorFactory</c>,
/// adding <c>Buffer</c> to the interpreter's singleton globals, and registering
/// a minimal <c>Promise</c> constructor sentinel. The compiled path's
/// <c>TryEmitBuiltInClassType</c> now also covers <c>Buffer</c> so bare
/// references emit a <c>Ldtoken</c> of the matching runtime type.
/// </remarks>
public class BareBuiltInClassRefTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Promise_BareValue_TypeofIsFunction(ExecutionMode mode)
    {
        var source = @"
            const ref: any = Promise;
            console.log(typeof ref);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("function\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Promise_InstanceOf_ResolvedPromise(ExecutionMode mode)
    {
        var source = @"
            const p = Promise.resolve(42);
            console.log(p instanceof Promise);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_BareValue_TypeofIsFunction(ExecutionMode mode)
    {
        var source = @"
            const ref: any = Buffer;
            console.log(typeof ref);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("function\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_InstanceOf_FromReturnsTrue(ExecutionMode mode)
    {
        var source = @"
            const b = Buffer.from([104, 105]);
            console.log(b instanceof Buffer);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TextEncoder_BareValue_TypeofIsFunction(ExecutionMode mode)
    {
        var source = @"
            const ref: any = TextEncoder;
            console.log(typeof ref);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("function\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TextEncoder_InstanceOf_NewInstance(ExecutionMode mode)
    {
        var source = @"
            const e = new TextEncoder();
            console.log(e instanceof TextEncoder);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TextDecoder_BareValue_TypeofIsFunction(ExecutionMode mode)
    {
        var source = @"
            const ref: any = TextDecoder;
            console.log(typeof ref);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("function\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TextDecoder_InstanceOf_NewInstance(ExecutionMode mode)
    {
        var source = @"
            const d = new TextDecoder();
            console.log(d instanceof TextDecoder);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BareBuiltIn_InstanceOf_NegativeCases(ExecutionMode mode)
    {
        // Negative checks — each bare-referencable class should reject the
        // wrong LHS without throwing.
        var source = @"
            console.log({} instanceof Promise);
            console.log({} instanceof Buffer);
            console.log({} instanceof TextEncoder);
            console.log({} instanceof TextDecoder);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\nfalse\nfalse\nfalse\n", output);
    }
}
