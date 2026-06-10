using SharpTS.Parsing;
using SharpTS.Runtime.BuiltIns;
using SharpTS.TypeSystem;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Callable wrapper around a <see cref="GlobalFunctionRegistry"/> handler.
/// Lets global functions (e.g. <c>parseFloat</c>, <c>parseInt</c>,
/// <c>isNaN</c>, <c>setTimeout</c>) be referenced as first-class values —
/// e.g. <c>var pf = parseFloat; typeof parseFloat === 'function';
/// freeParseFloat("1.5")</c> — not just called by name.
/// </summary>
public sealed class SharpTSGlobalFunction : ISharpTSCallable, ITypeCategorized
{
    public TypeCategory RuntimeCategory => TypeCategory.Function;

    public string Name { get; }

    public SharpTSGlobalFunction(string name)
    {
        Name = name;
    }

    public int Arity() => 0;

    public object? Call(Interp interpreter, List<object?> arguments)
        => CallV2(interpreter, CallableInterop.ToRuntimeValues(arguments)).ToObject();

    public RuntimeValue CallV2(Interp interpreter, ReadOnlySpan<RuntimeValue> arguments)
    {
        // Build ephemeral literal Expr args wrapping the already-evaluated
        // argument values, then invoke the registered handler. The handler
        // protocol takes Exprs, so values are boxed into literals either way.
        var argExprs = new List<Expr>(arguments.Length);
        foreach (var a in arguments)
        {
            argExprs.Add(new Expr.Literal(a.ToObject()));
        }

        if (GlobalFunctionRegistry.Instance.TryGetHandlerV2(Name, out var handlerV2) && handlerV2 != null)
        {
            var task = handlerV2(
                expr => ValueTask.FromResult(interpreter.EvaluateRV(expr)),
                argExprs,
                interpreter);
            return task.GetAwaiter().GetResult();
        }

        if (GlobalFunctionRegistry.Instance.TryGetHandler(Name, out var handler) && handler != null)
        {
            var task = handler(
                expr => ValueTask.FromResult(interpreter.Evaluate(expr)),
                argExprs,
                interpreter);
            return RuntimeValue.FromBoxed(task.GetAwaiter().GetResult());
        }

        throw new Exception($"Runtime Error: Global function '{Name}' is not registered.");
    }

    public override string ToString() => $"function {Name}() {{ [native code] }}";
}
