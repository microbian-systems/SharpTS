namespace SharpTS.Runtime.Types;

/// <summary>
/// Minimal global <c>Function</c> constructor placeholder. Real-world CJS
/// packages (lodash) only use this indirectly for <c>Function.prototype</c>
/// and <c>funcProto.toString</c> introspection, so we expose a skeleton
/// sufficient for those lookups.
/// </summary>
public sealed class SharpTSFunctionGlobal : ISharpTSCallable
{
    public static readonly SharpTSFunctionGlobal Instance = new();
    private readonly SharpTSFunctionPrototype _prototype = new();
    private SharpTSFunctionGlobal() { }

    public int Arity() => 0;

    // Calling `new Function(body)` is not supported — lodash only dereferences
    // `.prototype`, never calls the constructor.
    public object? Call(Execution.Interpreter interpreter, List<object?> arguments)
        => throw new Exception("Runtime Error: Dynamic Function() construction is not supported.");

    public object? GetMember(string name)
    {
        if (name == "prototype") return _prototype;
        return null;
    }

    public override string ToString() => "function Function() { [native code] }";
}

/// <summary>
/// Minimal <c>Function.prototype</c> accessor. Exposes <c>toString</c> for
/// lodash's native-detection path (<c>funcToString.call(fn)</c>).
/// </summary>
public sealed class SharpTSFunctionPrototype
{
    public object? GetMember(string name)
    {
        if (name == "toString") return SharpTSFunctionProtoToString.Instance;
        return null;
    }

    public override string ToString() => "[object Function]";
}

/// <summary>
/// Unbound <c>Function.prototype.toString</c>. When invoked via
/// <c>.call(fn)</c> or <c>.apply(fn)</c>, returns a native-source-like string
/// for the bound function — enough to satisfy lodash's regex-based native
/// detection.
/// </summary>
public sealed class SharpTSFunctionProtoToString : ISharpTSCallable
{
    public static readonly SharpTSFunctionProtoToString Instance = new();
    private readonly object? _boundThis;
    private SharpTSFunctionProtoToString(object? boundThis = null) { _boundThis = boundThis; }

    public int Arity() => 0;

    public object? Call(Execution.Interpreter interpreter, List<object?> arguments)
    {
        var target = _boundThis ?? (arguments.Count > 0 ? arguments[0] : null);
        return target?.ToString() ?? "function () { [native code] }";
    }

    public SharpTSFunctionProtoToString BindTo(object? thisArg) => new(thisArg);

    public override string ToString() => "function toString() { [native code] }";
}
