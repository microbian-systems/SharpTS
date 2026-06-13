using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Return-value semantics for plain (non-generator) functions: a function that completes
/// without an explicit <c>return &lt;expr&gt;</c> — a bare <c>return;</c> or falling off the end —
/// has return value <c>undefined</c>, distinct from <c>return null;</c> (which is null).
///
/// The interpreter is spec-correct in both cases: a bare <c>return;</c> produces the
/// <c>undefined</c> sentinel at its source (<c>VisitReturn</c>) rather than C# null — the #480
/// root-cause fix (which also corrected the generator completion value). Compiled mode still
/// yields <c>null</c> for these implicit-completion cases (tracked by #563), so those two
/// assertions are interpreter-only; the explicit <c>return null;</c> case agrees across modes.
/// </summary>
public class FunctionReturnValueTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void BareReturn_IsUndefined(ExecutionMode mode)
    {
        // Bare `return;` is equivalent to `return undefined` (compiled half: #563).
        var source = """
            function f() { return; }
            console.log(typeof f());
            console.log(f() === undefined);
            console.log(f() === null);
            """;
        Assert.Equal("undefined\ntrue\nfalse\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void OffEndCompletion_IsUndefined(ExecutionMode mode)
    {
        // Falling off the end of the body completes with undefined (compiled half: #563).
        var source = """
            function f() {}
            console.log(typeof f());
            console.log(f() === undefined);
            """;
        Assert.Equal("undefined\ntrue\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReturnNull_IsNull(ExecutionMode mode)
    {
        // `return null;` is distinct from a bare `return;`: the value is JS null, not undefined.
        var source = """
            function f() { return null; }
            console.log(typeof f());
            console.log(f() === null);
            """;
        Assert.Equal("object\ntrue\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void AsyncFunction_BareReturn_ResolvesUndefined(ExecutionMode mode)
    {
        // The async return path (ExecuteReturnAsyncVT) got the same fix as the sync VisitReturn:
        // a bare `return;` resolves the promise with undefined, not null (compiled half: #563).
        // Async generators are a separate path and remain tracked by #540.
        var source = """
            async function f() { return; }
            f().then(v => console.log(typeof v + ":" + (v === undefined)));
            """;
        Assert.Equal("undefined:true\n", TestHarness.Run(source, mode));
    }
}
