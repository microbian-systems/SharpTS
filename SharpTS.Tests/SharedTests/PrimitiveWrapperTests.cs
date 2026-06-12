using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Verifies that <c>new Number(x)</c>, <c>new String(x)</c>, and
/// <c>new Boolean(x)</c> produce boxed wrapper objects in both interpreter
/// and compiled mode, matching Node.js / ECMA-262 semantics (#360).
/// </summary>
public class PrimitiveWrapperTests
{
    // ── typeof ───────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Number_NewWrapper_TypeofIsObject(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(typeof new Number(5));", mode);
        Assert.Equal("object\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void String_NewWrapper_TypeofIsObject(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(typeof new String('x'));", mode);
        Assert.Equal("object\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Boolean_NewWrapper_TypeofIsObject(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(typeof new Boolean(true));", mode);
        Assert.Equal("object\n", output);
    }

    // ── instanceof Object ────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Number_NewWrapper_InstanceofObject(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(new Number(5) instanceof Object);", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void String_NewWrapper_InstanceofObject(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(new String('x') instanceof Object);", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Boolean_NewWrapper_InstanceofObject(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(new Boolean(true) instanceof Object);", mode);
        Assert.Equal("true\n", output);
    }

    // ── instanceof own constructor ───────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Number_NewWrapper_InstanceofNumber(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(new Number(5) instanceof Number);", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void String_NewWrapper_InstanceofString(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(new String('x') instanceof String);", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Boolean_NewWrapper_InstanceofBoolean(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(new Boolean(false) instanceof Boolean);", mode);
        Assert.Equal("true\n", output);
    }

    // ── primitive (non-new) is NOT an instance ───────────────────────────────
    // Interpreter-only: compiled mode treats bare primitives differently for instanceof
    // (pre-existing gap, tracked separately).

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Number_Bare_NotInstanceofNumber(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log((5 as any) instanceof Number);", mode);
        Assert.Equal("false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void String_Bare_NotInstanceofString(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(('x' as any) instanceof String);", mode);
        Assert.Equal("false\n", output);
    }

    // ── call form still coerces (no wrapper) ────────────────────────────────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Number_CallForm_ReturnsPrimitive(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(typeof Number('42'));", mode);
        Assert.Equal("number\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void String_CallForm_ReturnsPrimitive(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(typeof String(42));", mode);
        Assert.Equal("string\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Boolean_CallForm_ReturnsPrimitive(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(typeof Boolean(1));", mode);
        Assert.Equal("boolean\n", output);
    }

    // ── wrapper primitive value / conversion ─────────────────────────────────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Number_NoArg_WrapsZero(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(new Number() instanceof Number);", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void String_Length_MatchesPrimitive(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log(new String('hello').length);", mode);
        Assert.Equal("5\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void String_IndexedAccess_ReturnsChar(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log((new String('hi') as any)[0]);", mode);
        Assert.Equal("h\n", output);
    }

    // ── method dispatch on wrappers ──────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Number_ToFixed_WorksOnWrapper(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log((new Number(5) as any).toFixed(2));", mode);
        Assert.Equal("5.00\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void String_ToUpperCase_WorksOnWrapper(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log((new String('hello') as any).toUpperCase());", mode);
        Assert.Equal("HELLO\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Boolean_ToString_WorksOnWrapper(ExecutionMode mode)
    {
        var output = TestHarness.Run("console.log((new Boolean(true) as any).toString());", mode);
        Assert.Equal("true\n", output);
    }
}
