using SharpTS.Execution;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Callback for Promise resolve function passed to the executor.
/// Implements ISharpTSCallable so it can be called from TypeScript code.
/// </summary>
public class PromiseResolveCallback : ISharpTSCallable
{
    private readonly Action<object?> _resolve;

    public PromiseResolveCallback(Action<object?> resolve)
    {
        _resolve = resolve;
    }

    public int Arity() => 0; // 0 minimum - resolve can be called with 0 or 1 argument

    public object? Call(Interpreter interpreter, List<object?> arguments)
        => CallV2(interpreter, CallableInterop.ToRuntimeValues(arguments)).ToObject();

    public RuntimeValue CallV2(Interpreter interpreter, ReadOnlySpan<RuntimeValue> arguments)
    {
        var value = arguments.Length > 0 ? arguments[0].ToObject() : null;
        _resolve(value);
        return RuntimeValue.Undefined;
    }
}

/// <summary>
/// Callback for Promise reject function passed to the executor.
/// Implements ISharpTSCallable so it can be called from TypeScript code.
/// </summary>
public class PromiseRejectCallback : ISharpTSCallable
{
    private readonly Action<object?> _reject;

    public PromiseRejectCallback(Action<object?> reject)
    {
        _reject = reject;
    }

    public int Arity() => 0; // 0 minimum - reject can be called with 0 or 1 argument

    public object? Call(Interpreter interpreter, List<object?> arguments)
        => CallV2(interpreter, CallableInterop.ToRuntimeValues(arguments)).ToObject();

    public RuntimeValue CallV2(Interpreter interpreter, ReadOnlySpan<RuntimeValue> arguments)
    {
        var reason = arguments.Length > 0 ? arguments[0].ToObject() : null;
        _reject(reason);
        return RuntimeValue.Undefined;
    }
}
