using SharpTS.Parsing;
using SharpTS.Runtime;
using SharpTS.Execution;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime wrapper for generator function declarations (function*).
/// </summary>
/// <remarks>
/// When called, this does NOT execute the function body immediately.
/// Instead, it returns a <see cref="SharpTSGenerator"/> instance that
/// lazily executes the body as next() is called.
/// </remarks>
/// <seealso cref="SharpTSGenerator"/>
/// <seealso cref="SharpTSFunction"/>
public class SharpTSGeneratorFunction : ISharpTSCallable
{
    private readonly Stmt.Function _declaration;
    private readonly RuntimeEnvironment _closure;
    private readonly int _arity;

    public SharpTSGeneratorFunction(Stmt.Function declaration, RuntimeEnvironment closure)
    {
        _declaration = declaration;
        _closure = closure;
        _arity = declaration.Parameters.Count(p => p.DefaultValue == null && !p.IsRest && !p.IsOptional);
    }

    public int Arity() => _arity;

    /// <summary>
    /// RuntimeValue entry point. Parameter binding happens synchronously before this
    /// returns; only the bound environment outlives the call, so the span never escapes.
    /// </summary>
    public RuntimeValue Call(Interpreter interpreter, ReadOnlySpan<RuntimeValue> arguments)
    {
        RuntimeEnvironment environment = new(_closure);
        ParameterBinder.BindRV(_declaration.Parameters, arguments, environment, interpreter);
        return RuntimeValue.FromObject(new SharpTSGenerator(_declaration, environment, interpreter));
    }

    /// <summary>
    /// Creates a bound version with 'this' set for method calls.
    /// </summary>
    public SharpTSGeneratorFunction Bind(SharpTSInstance instance)
    {
        RuntimeEnvironment boundEnv = new(_closure);
        boundEnv.Define("this", instance);
        return new SharpTSGeneratorFunction(_declaration, boundEnv);
    }

    public SharpTSGeneratorFunction BindStatic(SharpTSClass klass)
    {
        RuntimeEnvironment boundEnv = new(_closure);
        boundEnv.Define("this", klass);
        if (klass.Superclass != null)
            boundEnv.Define("super", klass.Superclass);
        return new SharpTSGeneratorFunction(_declaration, boundEnv);
    }

    public override string ToString() => $"<generator fn {_declaration.Name.Lexeme}>";
}

/// <summary>
/// Runtime wrapper for generator function expressions (function*() { } as expression).
/// </summary>
/// <remarks>
/// Similar to <see cref="SharpTSGeneratorFunction"/> but wraps an <see cref="Expr.ArrowFunction"/>
/// with IsGenerator=true instead of a <see cref="Stmt.Function"/>.
/// </remarks>
public class SharpTSArrowGeneratorFunction : ISharpTSCallable
{
    private readonly Expr.ArrowFunction _declaration;
    private readonly RuntimeEnvironment _closure;
    private readonly int _arity;

    /// <summary>
    /// Indicates whether this function has its own 'this' binding (function expressions)
    /// versus capturing 'this' from enclosing scope (arrow functions).
    /// </summary>
    public bool HasOwnThis { get; }

    public SharpTSArrowGeneratorFunction(Expr.ArrowFunction declaration, RuntimeEnvironment closure, bool hasOwnThis = false)
    {
        _declaration = declaration;
        _closure = closure;
        HasOwnThis = hasOwnThis;
        _arity = declaration.Parameters.Count(p => p.DefaultValue == null && !p.IsRest && !p.IsOptional);
    }

    public int Arity() => _arity;

    /// <summary>
    /// RuntimeValue entry point — see <see cref="SharpTSGeneratorFunction.Call"/>.
    /// </summary>
    public RuntimeValue Call(Interpreter interpreter, ReadOnlySpan<RuntimeValue> arguments)
    {
        RuntimeEnvironment environment = new(_closure);
        if (_declaration.Name != null)
        {
            environment.Define(_declaration.Name.Lexeme, this);
        }
        ParameterBinder.BindRV(_declaration.Parameters, arguments, environment, interpreter);
        return RuntimeValue.FromObject(new SharpTSArrowGenerator(_declaration, environment, interpreter));
    }

    /// <summary>
    /// Binds 'this' to the given object. Only applicable for function expressions with HasOwnThis=true.
    /// </summary>
    public SharpTSArrowGeneratorFunction Bind(object thisObject)
    {
        RuntimeEnvironment boundEnv = new(_closure);
        boundEnv.Define("this", thisObject);
        return new SharpTSArrowGeneratorFunction(_declaration, boundEnv, hasOwnThis: true);
    }

    public override string ToString()
    {
        if (_declaration.Name != null)
            return $"<generator fn {_declaration.Name.Lexeme}>";
        return "<generator fn>";
    }
}
