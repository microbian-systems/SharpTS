using SharpTS.Execution;
using SharpTS.Runtime;
using SharpTS.Runtime.Types;
using Xunit;

namespace SharpTS.Tests.RuntimeTests;

/// <summary>
/// Unit tests for the unified ISharpTSCallable interface and the
/// CallableInterop boxed-call helpers.
/// </summary>
public class CallableAdapterTests
{
    #region Test Implementations

    private class AddCallable : ISharpTSCallable
    {
        public int Arity() => 2;

        public RuntimeValue Call(Interpreter interpreter, ReadOnlySpan<RuntimeValue> arguments)
        {
            return arguments[0].AsNumber() + arguments[1].AsNumber();
        }
    }

    private class EchoCallable : ISharpTSCallable
    {
        public int Arity() => 1;
        public RuntimeValue Call(Interpreter interpreter, ReadOnlySpan<RuntimeValue> arguments)
            => arguments[0];
    }

    private class ZeroArityCallable : ISharpTSCallable
    {
        public int Arity() => 0;
        public RuntimeValue Call(Interpreter interpreter, ReadOnlySpan<RuntimeValue> arguments) => 42.0;
    }

    #endregion

    #region Call Dispatch Tests

    [Fact]
    public void CallV2_ThroughInterface_DispatchesToImplementation()
    {
        ISharpTSCallable callable = new AddCallable();

        ReadOnlySpan<RuntimeValue> args = [3.0, 4.0];
        var result = callable.Call(null!, args);

        Assert.Equal(ValueKind.Number, result.Kind);
        Assert.Equal(7.0, result.AsNumber());
    }

    [Fact]
    public void CallV2_PreservesNull()
    {
        ISharpTSCallable callable = new EchoCallable();

        ReadOnlySpan<RuntimeValue> args = [RuntimeValue.Null];
        var result = callable.Call(null!, args);

        Assert.Equal(ValueKind.Null, result.Kind);
    }

    [Fact]
    public void CallV2_PreservesUndefined()
    {
        ISharpTSCallable callable = new EchoCallable();

        ReadOnlySpan<RuntimeValue> args = [RuntimeValue.Undefined];
        var result = callable.Call(null!, args);

        Assert.True(result.IsUndefined);
    }

    [Fact]
    public void CallV2_HandlesEmptyArguments()
    {
        ISharpTSCallable callable = new ZeroArityCallable();

        var result = callable.Call(null!, ReadOnlySpan<RuntimeValue>.Empty);

        Assert.Equal(42.0, result.AsNumber());
    }

    #endregion

    #region CallableInterop Tests

    [Fact]
    public void CallBoxed_ConvertsArgumentsAndResult()
    {
        var callable = new AddCallable();

        var result = callable.CallBoxed(null!, [5.0, 3.0]);

        Assert.Equal(8.0, (double)result!);
    }

    [Fact]
    public void CallBoxed_RoundTripsBoxedValues()
    {
        var callable = new EchoCallable();

        var marker = new object();
        var result = callable.CallBoxed(null!, [marker]);

        Assert.Same(marker, result);
    }

    [Fact]
    public void ToRuntimeValues_ConvertsBoxedList()
    {
        var values = CallableInterop.ToRuntimeValues([1.0, "x", null, true]);

        Assert.Equal(4, values.Length);
        Assert.Equal(1.0, values[0].AsNumber());
        Assert.Equal("x", values[1].AsString());
        Assert.Equal(ValueKind.Null, values[2].Kind);
        Assert.True(values[3].AsBoolean());
    }

    [Fact]
    public void ToBoxedList_ConvertsSpan()
    {
        ReadOnlySpan<RuntimeValue> span = [2.0, RuntimeValue.Undefined];
        var boxed = CallableInterop.ToBoxedList(span);

        Assert.Equal(2, boxed.Count);
        Assert.Equal(2.0, (double)boxed[0]!);
    }

    #endregion
}
