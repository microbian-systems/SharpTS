using SharpTS.Execution;
using SharpTS.Parsing;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime object representing an async generator function declaration.
/// </summary>
/// <remarks>
/// Created for declarations using both 'async' and 'function*' syntax.
/// When called, instantiates a <see cref="SharpTSAsyncGenerator"/>.
/// Async generators yield Promises and can use 'await' internally.
/// </remarks>
/// <seealso cref="SharpTSAsyncGenerator"/>
/// <seealso cref="SharpTSGeneratorFunction"/>
public class SharpTSAsyncGeneratorFunction : ISharpTSCallable
{
    private readonly Stmt.Function _declaration;
    private readonly RuntimeEnvironment _closure;
    private readonly int _arity;

    public SharpTSAsyncGeneratorFunction(Stmt.Function declaration, RuntimeEnvironment closure)
    {
        _declaration = declaration;
        _closure = closure;
        _arity = declaration.Parameters?.Count ?? 0;
    }

    public int Arity() => _arity;

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        // Create a new environment for this generator invocation
        RuntimeEnvironment environment = new(_closure);

        // Bind parameters to arguments
        ParameterBinder.Bind(_declaration.Parameters ?? [], arguments, environment, interpreter);

        // Return the async generator object (not yet started)
        return new SharpTSAsyncGenerator(_declaration, environment, interpreter);
    }

    /// <summary>
    /// RuntimeValue entry point. Binding is synchronous — the generator body runs
    /// later from the bound environment, so the span never escapes this frame.
    /// </summary>
    public RuntimeValue CallV2(Interpreter interpreter, ReadOnlySpan<RuntimeValue> arguments)
    {
        RuntimeEnvironment environment = new(_closure);
        ParameterBinder.BindRV(_declaration.Parameters ?? [], arguments, environment, interpreter);
        return RuntimeValue.FromObject(new SharpTSAsyncGenerator(_declaration, environment, interpreter));
    }

    /// <summary>
    /// Creates a bound version with 'this' set for method calls.
    /// </summary>
    public SharpTSAsyncGeneratorFunction Bind(SharpTSInstance instance)
    {
        RuntimeEnvironment boundEnv = new(_closure);
        boundEnv.Define("this", instance);
        return new SharpTSAsyncGeneratorFunction(_declaration, boundEnv);
    }

    public SharpTSAsyncGeneratorFunction BindStatic(SharpTSClass klass)
    {
        RuntimeEnvironment boundEnv = new(_closure);
        boundEnv.Define("this", klass);
        if (klass.Superclass != null)
            boundEnv.Define("super", klass.Superclass);
        return new SharpTSAsyncGeneratorFunction(_declaration, boundEnv);
    }

    public override string ToString() => $"[async function* {_declaration.Name?.Lexeme ?? "anonymous"}]";
}
