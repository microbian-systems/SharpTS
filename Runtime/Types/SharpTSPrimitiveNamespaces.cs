using SharpTS.Execution;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Singleton representing the String namespace/constructor.
/// Callable as String(value) for type conversion, and provides static methods.
/// </summary>
public class SharpTSStringNamespace : ISharpTSCallable
{
    public static readonly SharpTSStringNamespace Instance = new();
    private SharpTSStringNamespace() { }

    public int Arity() => 0;

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        if (arguments.Count == 0) return "";
        var arg = arguments[0];
        if (arg is SharpTSUndefined) return "undefined";
        if (arg == null) return "null";
        if (arg is bool b) return b ? "true" : "false";
        if (arg is SharpTSArray arr) return arr.ToString();
        return arg.ToString() ?? "";
    }

    public override string ToString() => "function String() { [native code] }";
}

/// <summary>
/// Singleton representing the Number namespace/constructor.
/// Callable as Number(value) for type conversion, and provides static methods.
/// </summary>
public class SharpTSNumberNamespace : ISharpTSCallable
{
    public static readonly SharpTSNumberNamespace Instance = new();
    private SharpTSNumberNamespace() { }

    public int Arity() => 0;

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        if (arguments.Count == 0) return 0.0;
        var arg = arguments[0];
        if (arg is double d) return d;
        if (arg is SharpTSUndefined) return double.NaN;
        if (arg == null) return 0.0;
        if (arg is bool b) return b ? 1.0 : 0.0;
        if (arg is string s)
        {
            s = s.Trim();
            if (s.Length == 0) return 0.0;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double result))
                return result;
            return double.NaN;
        }
        return double.NaN;
    }

    public override string ToString() => "function Number() { [native code] }";
}

/// <summary>
/// Singleton representing the Boolean namespace/constructor.
/// Callable as Boolean(value) for type conversion.
/// </summary>
public class SharpTSBooleanNamespace : ISharpTSCallable
{
    public static readonly SharpTSBooleanNamespace Instance = new();
    private SharpTSBooleanNamespace() { }

    public int Arity() => 0;

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        if (arguments.Count == 0) return false;
        var arg = arguments[0];
        return SharpTS.Compilation.RuntimeTypes.IsTruthy(arg);
    }

    public override string ToString() => "function Boolean() { [native code] }";
}
