using SharpTS.Parsing;
using SharpTS.Runtime;
using SharpTS.Runtime.Exceptions;
using SharpTS.Execution;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Interface for all callable objects in the SharpTS runtime.
/// </summary>
/// <remarks>
/// Implemented by <see cref="SharpTSFunction"/>, <see cref="SharpTSArrowFunction"/>,
/// and <see cref="SharpTSClass"/>. Enables uniform function invocation regardless
/// of whether the callee is a named function, arrow function, or class constructor.
/// </remarks>
public interface ISharpTSCallable
{
    int Arity();
    object? Call(Interpreter interpreter, List<object?> arguments);
}

/// <summary>
/// Runtime wrapper for named function declarations.
/// </summary>
/// <remarks>
/// Wraps a <see cref="Stmt.Function"/> AST node along with its closure environment.
/// Handles parameter binding (including default values and rest parameters),
/// executes the function body, and catches <see cref="ReturnException"/> to return values.
/// The <see cref="Bind"/> method creates a new function with <c>this</c> bound for method calls.
/// </remarks>
/// <seealso cref="SharpTSArrowFunction"/>
/// <seealso cref="RuntimeEnvironment"/>
public class SharpTSFunction : ISharpTSCallable, ISharpTSCallableV2, ITypeCategorized
{
    /// <inheritdoc />
    public TypeCategory RuntimeCategory => TypeCategory.Function;

    private readonly Stmt.Function _declaration;
    private readonly RuntimeEnvironment _closure;
    private readonly int _arity;
    // JS-spec: functions are objects and support arbitrary property
    // assignment (e.g. `fn.DNS = "..."`). Lazily allocated.
    private Dictionary<string, object?>? _properties;
    // Accessor properties defined via Object.defineProperty(fn, name, {get, set}).
    // When present, dispatch through the getter/setter instead of _properties.
    private Dictionary<string, (ISharpTSCallable? Get, ISharpTSCallable? Set)>? _accessors;

    public SharpTSFunction(Stmt.Function declaration, RuntimeEnvironment closure)
    {
        _declaration = declaration;
        _closure = closure;
        _arity = declaration.Parameters.Count(p => p.DefaultValue == null && !p.IsRest && !p.IsOptional);
    }

    /// <summary>JS function-as-object property access.</summary>
    public bool TryGetProperty(string name, out object? value)
    {
        if (_properties != null && _properties.TryGetValue(name, out value))
            return true;
        value = null;
        return false;
    }

    /// <summary>Sets a JS-object property on this function.</summary>
    public void SetProperty(string name, object? value)
    {
        _properties ??= [];
        _properties[name] = value;
    }

    /// <summary>Removes a JS-object property from this function.</summary>
    public bool DeleteProperty(string name)
    {
        bool removed = _properties?.Remove(name) ?? false;
        removed |= _accessors?.Remove(name) ?? false;
        return removed;
    }

    /// <summary>
    /// Defines a property with a getter and/or setter on this function.
    /// Supports <c>Object.defineProperty(fn, 'name', { get, set })</c>.
    /// </summary>
    public void DefineAccessor(string name, ISharpTSCallable? getter, ISharpTSCallable? setter)
    {
        _accessors ??= [];
        _accessors[name] = (getter, setter);
    }

    /// <summary>Returns the accessor pair for <paramref name="name"/> if defined.</summary>
    public bool TryGetAccessor(string name, out ISharpTSCallable? getter, out ISharpTSCallable? setter)
    {
        if (_accessors != null && _accessors.TryGetValue(name, out var pair))
        {
            getter = pair.Get;
            setter = pair.Set;
            return true;
        }
        getter = null;
        setter = null;
        return false;
    }

    public int Arity() => _arity;
    int ISharpTSCallableV2.Arity => _arity;

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        if (_declaration.Body == null)
        {
            throw new Exception($"Cannot invoke abstract method '{_declaration.Name.Lexeme}'.");
        }

        // Check for function-level "use strict" directive
        bool functionStrict = CheckForUseStrict(_declaration.Body);
        RuntimeEnvironment environment = functionStrict
            ? new RuntimeEnvironment(_closure, strictMode: true)
            : new RuntimeEnvironment(_closure);

        // JS calling convention: if `this` isn't bound by a receiver
        // (bare call `foo()`), it defaults to the global object in
        // sloppy mode and to `undefined` in strict mode. Only default
        // if the closure doesn't already supply `this` (e.g. from Bind*
        // or a method call via `obj.foo()`).
        if (!_closure.TryGet("this", out _))
        {
            environment.Define("this",
                functionStrict ? SharpTSUndefined.Instance : (object?)SharpTSGlobalThis.Instance);
        }

        ParameterBinder.Bind(_declaration.Parameters, arguments, environment, interpreter);
        // Bind the JS-spec `arguments` array-like to the current call's args.
        // Arrow functions do NOT bind `arguments` — they inherit from the
        // enclosing non-arrow function (handled by SharpTSArrowFunction).
        environment.Define("arguments", new SharpTSArray(arguments));

        var result = interpreter.ExecuteBlock(_declaration.Body, environment);
        if (result.Type == ExecutionResult.ResultType.Return)
        {
            return result.Value.ToObject();
        }
        if (result.Type == ExecutionResult.ResultType.Throw)
        {
            // Preserve the original thrown value so the outer catch receives the actual
            // Error/object. Wrapping in `new Exception(Stringify(...))` flattens it to a
            // string, which breaks `e.message`/`e.name` at any .NET-interop boundary
            // (delegate callbacks, reflected calls) where the error can't round-trip
            // through ExecutionResult.
            throw new ThrowException(result.Value.ToObject());
        }

        return null;
    }

    /// <summary>
    /// Checks if the statements begin with a "use strict" directive.
    /// </summary>
    private static bool CheckForUseStrict(List<Stmt> statements)
    {
        foreach (var stmt in statements)
        {
            if (stmt is Stmt.Directive directive)
            {
                if (directive.Value == "use strict")
                {
                    return true;
                }
            }
            else
            {
                break;
            }
        }
        return false;
    }

    public SharpTSFunction Bind(SharpTSInstance instance)
    {
        RuntimeEnvironment environment = new(_closure);
        environment.Define("this", instance);

        // Propagate 'super' from closure if present (needed for methods in derived classes)
        // Use TryGet to avoid exceptions - 'super' may not be in scope for non-derived classes
        if (_closure.TryGet("super", out var superclass) && superclass != null)
        {
            environment.Define("super", superclass);
        }

        return new SharpTSFunction(_declaration, environment);
    }

    /// <summary>
    /// Binds this function to a class receiver (for static methods and static
    /// accessors, where `this` should refer to the class itself).
    /// </summary>
    public SharpTSFunction BindStatic(SharpTSClass klass)
    {
        RuntimeEnvironment environment = new(_closure);
        environment.Define("this", klass);
        if (klass.Superclass != null)
        {
            environment.Define("super", klass.Superclass);
        }
        return new SharpTSFunction(_declaration, environment);
    }

    /// <summary>
    /// Binds this function to an arbitrary `this` value. Used by `new Func()`
    /// to bind the fresh instance, and by `Function.prototype.call/apply`.
    /// </summary>
    public SharpTSFunction BindThis(object? thisValue)
    {
        RuntimeEnvironment environment = new(_closure);
        environment.Define("this", thisValue);
        return new SharpTSFunction(_declaration, environment);
    }

    /// <summary>
    /// V2 call path — avoids boxing at parameter and return boundaries.
    /// </summary>
    public RuntimeValue CallV2(Interpreter interpreter, ReadOnlySpan<RuntimeValue> arguments)
    {
        if (_declaration.Body == null)
        {
            throw new Exception($"Cannot invoke abstract method '{_declaration.Name.Lexeme}'.");
        }

        bool functionStrict = CheckForUseStrict(_declaration.Body);
        RuntimeEnvironment environment = functionStrict
            ? new RuntimeEnvironment(_closure, strictMode: true)
            : new RuntimeEnvironment(_closure);

        // See Call() for rationale: default `this` to globalThis in sloppy
        // mode / undefined in strict mode for bare calls.
        if (!_closure.TryGet("this", out _))
        {
            environment.Define("this",
                functionStrict ? SharpTSUndefined.Instance : (object?)SharpTSGlobalThis.Instance);
        }

        ParameterBinder.BindRV(_declaration.Parameters, arguments, environment, interpreter);
        // See Call for the JS-spec rationale; materialize the args span into a
        // SharpTSArray so `arguments[i]` and `arguments.length` work.
        var argsList = new List<object?>(arguments.Length);
        for (int i = 0; i < arguments.Length; i++) argsList.Add(arguments[i].ToObject());
        environment.Define("arguments", new SharpTSArray(argsList));

        var result = interpreter.ExecuteBlock(_declaration.Body, environment);
        if (result.Type == ExecutionResult.ResultType.Return)
        {
            return result.Value;
        }
        if (result.Type == ExecutionResult.ResultType.Throw)
        {
            throw new Exception(interpreter.Stringify(result.Value.ToObject()));
        }

        return RuntimeValue.Undefined;
    }

    public override string ToString() => $"<fn {_declaration.Name.Lexeme}>";
}

/// <summary>
/// Runtime wrapper for arrow function expressions.
/// </summary>
/// <remarks>
/// Wraps an <see cref="Expr.ArrowFunction"/> AST node along with its closure environment.
/// Supports both expression bodies (<c>x =&gt; x + 1</c>) and block bodies (<c>x =&gt; { return x + 1; }</c>).
/// For arrow functions (<c>HasOwnThis=false</c>), <c>this</c> is captured from the enclosing scope via the closure.
/// For function expressions and object method shorthand (<c>HasOwnThis=true</c>), <c>this</c> is bound at call time.
/// </remarks>
/// <seealso cref="SharpTSFunction"/>
public class SharpTSArrowFunction : ISharpTSCallable, ISharpTSCallableV2, ITypeCategorized
{
    /// <inheritdoc />
    public TypeCategory RuntimeCategory => TypeCategory.Function;

    private readonly Expr.ArrowFunction _declaration;
    private readonly RuntimeEnvironment _closure;
    private readonly int _arity;
    // JS: arrow/function expressions are objects; support property assignment
    // (e.g. minimatch's `exports.minimatch.sep = "/"`).
    private Dictionary<string, object?>? _properties;
    private Dictionary<string, (ISharpTSCallable? Get, ISharpTSCallable? Set)>? _accessors;

    /// <summary>
    /// Indicates whether this function has its own 'this' binding (function expressions)
    /// versus capturing 'this' from enclosing scope (arrow functions).
    /// </summary>
    public bool HasOwnThis { get; }

    public SharpTSArrowFunction(Expr.ArrowFunction declaration, RuntimeEnvironment closure, bool hasOwnThis = false)
    {
        _declaration = declaration;
        _closure = closure;
        HasOwnThis = hasOwnThis;
        _arity = declaration.Parameters.Count(p => p.DefaultValue == null && !p.IsRest && !p.IsOptional);
    }

    /// <summary>JS function-as-object property access.</summary>
    public bool TryGetProperty(string name, out object? value)
    {
        if (_properties != null && _properties.TryGetValue(name, out value))
            return true;
        value = null;
        return false;
    }

    /// <summary>Sets a JS-object property on this arrow function.</summary>
    public void SetProperty(string name, object? value)
    {
        _properties ??= [];
        _properties[name] = value;
    }

    /// <summary>Removes a JS-object property from this arrow function.</summary>
    public bool DeleteProperty(string name)
    {
        bool removed = _properties?.Remove(name) ?? false;
        removed |= _accessors?.Remove(name) ?? false;
        return removed;
    }

    /// <summary>Defines a getter/setter pair via Object.defineProperty.</summary>
    public void DefineAccessor(string name, ISharpTSCallable? getter, ISharpTSCallable? setter)
    {
        _accessors ??= [];
        _accessors[name] = (getter, setter);
    }

    /// <summary>Returns the accessor pair for <paramref name="name"/> if defined.</summary>
    public bool TryGetAccessor(string name, out ISharpTSCallable? getter, out ISharpTSCallable? setter)
    {
        if (_accessors != null && _accessors.TryGetValue(name, out var pair))
        {
            getter = pair.Get;
            setter = pair.Set;
            return true;
        }
        getter = null;
        setter = null;
        return false;
    }

    public int Arity() => _arity;
    int ISharpTSCallableV2.Arity => _arity;

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        // Check for function-level "use strict" directive in block body
        bool functionStrict = _declaration.BlockBody != null && CheckForUseStrict(_declaration.BlockBody);
        RuntimeEnvironment environment = functionStrict
            ? new RuntimeEnvironment(_closure, strictMode: true)
            : new RuntimeEnvironment(_closure);

        // Named function expression: bind the self-reference in the same scope
        // as parameters so recursion works (`function f(n) { return f(n-1); }`)
        // while keeping outer-variable distances consistent with the resolver.
        if (_declaration.Name != null)
        {
            environment.Define(_declaration.Name.Lexeme, this);
        }

        ParameterBinder.Bind(_declaration.Parameters, arguments, environment, interpreter);

        if (_declaration.ExpressionBody != null)
        {
            // Expression body - evaluate and return directly
            RuntimeEnvironment previous = interpreter.Environment;
            try
            {
                interpreter.SetEnvironment(environment);
                return interpreter.Evaluate(_declaration.ExpressionBody);
            }
            finally
            {
                interpreter.SetEnvironment(previous);
            }
        }
        else if (_declaration.BlockBody != null)
        {
            // Block body - execute statements, catch return
            var result = interpreter.ExecuteBlock(_declaration.BlockBody, environment);
            if (result.Type == ExecutionResult.ResultType.Return)
            {
                return result.Value.ToObject();
            }
            if (result.Type == ExecutionResult.ResultType.Throw)
            {
                // See SharpTSFunction.Call — preserve original thrown value.
                throw new ThrowException(result.Value.ToObject());
            }
        }

        return null;
    }

    /// <summary>
    /// V2 call path — avoids boxing at parameter and return boundaries.
    /// </summary>
    public RuntimeValue CallV2(Interpreter interpreter, ReadOnlySpan<RuntimeValue> arguments)
    {
        bool functionStrict = _declaration.BlockBody != null && CheckForUseStrict(_declaration.BlockBody);
        RuntimeEnvironment environment = functionStrict
            ? new RuntimeEnvironment(_closure, strictMode: true)
            : new RuntimeEnvironment(_closure);

        // Named function expression: bind the self-reference in the same scope as
        // parameters so recursion works (`function f(n) { return f(n-1); }`) while
        // keeping outer-variable distances consistent with the resolver.
        if (_declaration.Name != null)
        {
            environment.Define(_declaration.Name.Lexeme, this);
        }

        ParameterBinder.BindRV(_declaration.Parameters, arguments, environment, interpreter);

        if (_declaration.ExpressionBody != null)
        {
            RuntimeEnvironment previous = interpreter.Environment;
            try
            {
                interpreter.SetEnvironment(environment);
                return interpreter.EvaluateRV(_declaration.ExpressionBody);
            }
            finally
            {
                interpreter.SetEnvironment(previous);
            }
        }
        else if (_declaration.BlockBody != null)
        {
            var result = interpreter.ExecuteBlock(_declaration.BlockBody, environment);
            if (result.Type == ExecutionResult.ResultType.Return)
            {
                return result.Value;
            }
            if (result.Type == ExecutionResult.ResultType.Throw)
            {
                throw new Exception(interpreter.Stringify(result.Value.ToObject()));
            }
        }

        return RuntimeValue.Undefined;
    }

    /// <summary>
    /// Checks if the statements begin with a "use strict" directive.
    /// </summary>
    private static bool CheckForUseStrict(List<Stmt> statements)
    {
        foreach (var stmt in statements)
        {
            if (stmt is Stmt.Directive directive)
            {
                if (directive.Value == "use strict")
                {
                    return true;
                }
            }
            else
            {
                break;
            }
        }
        return false;
    }

    /// <summary>
    /// Binds 'this' to the given object. Only applicable for function expressions with HasOwnThis=true.
    /// </summary>
    /// <param name="thisObject">The object to bind as 'this'.</param>
    /// <returns>A new SharpTSArrowFunction with 'this' bound in its closure.</returns>
    public SharpTSArrowFunction Bind(object thisObject)
    {
        RuntimeEnvironment environment = new(_closure);
        environment.Define("this", thisObject);
        return new SharpTSArrowFunction(_declaration, environment, hasOwnThis: true);
    }

    public override string ToString()
    {
        if (_declaration.Name != null)
            return $"<fn {_declaration.Name.Lexeme}>";
        return "<arrow fn>";
    }
}
