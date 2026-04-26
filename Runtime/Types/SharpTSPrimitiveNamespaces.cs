using SharpTS.Execution;
using SharpTS.Runtime.BuiltIns;
using SharpTS.Runtime.Exceptions;

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

    /// <summary>
    /// Returns <c>String.prototype</c> so real-world patterns like
    /// <c>String.prototype.trim.call(x)</c> resolve correctly; built-in static
    /// methods (String.raw, String.fromCharCode, ...) fall through to the
    /// registry lookup.
    /// </summary>
    public object? GetMember(string name)
    {
        if (name == "prototype") return SharpTSStringPrototype.Instance;
        return StringBuiltIns.GetStaticMember(name);
    }

    public override string ToString() => "function String() { [native code] }";
}

/// <summary>
/// <c>String.prototype</c>. Exposes every registered String method as an
/// unbound <see cref="BuiltInMethod"/> via <see cref="StringBuiltIns"/>,
/// wrapped so <c>String.prototype.trim.call(value)</c> throws a proper
/// TypeError on null/undefined receivers and ToString-coerces every other
/// receiver per ECMA-262 before dispatch.
/// </summary>
public sealed class SharpTSStringPrototype
{
    public static readonly SharpTSStringPrototype Instance = new();
    private SharpTSStringPrototype() { }

    public object? GetMember(string name)
    {
        var method = StringBuiltIns.GetPrototypeMethod(name);
        if (method is null) return null;
        return new StringPrototypeMethodWrapper(name, method);
    }

    public override string ToString() => "[object String]";
}

/// <summary>
/// Adapter around a String <see cref="BuiltInMethod"/>. Throws TypeError for
/// null/undefined receivers and otherwise coerces the receiver to a string
/// (ToString — the abstract operation, not the method) before binding and
/// dispatching.
/// </summary>
internal sealed class StringPrototypeMethodWrapper : ISharpTSCallable
{
    private readonly string _name;
    private readonly BuiltInMethod _inner;
    private readonly object? _receiver;
    private readonly bool _hasReceiver;

    public StringPrototypeMethodWrapper(string name, BuiltInMethod inner)
    {
        _name = name;
        _inner = inner;
    }

    private StringPrototypeMethodWrapper(string name, BuiltInMethod inner, object? receiver)
    {
        _name = name;
        _inner = inner;
        _receiver = receiver;
        _hasReceiver = true;
    }

    public int Arity() => _inner.MinArity;

    public StringPrototypeMethodWrapper Bind(object? receiver)
        => new(_name, _inner, receiver);

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        if (!_hasReceiver || _receiver is null or SharpTSUndefined)
        {
            throw new ThrowException(new SharpTSTypeError(
                $"String.prototype.{_name} called on null or undefined"));
        }

        var coerced = CoerceToString(_receiver);
        return _inner.Bind(coerced).Call(interpreter, arguments);
    }

    /// <summary>
    /// ECMA-262 ToString abstract operation — mapped to the receiver types
    /// our interpreter actually hands to string methods. Symbols throw
    /// TypeError per spec (ToString(Symbol) is an abrupt completion). Wrapper
    /// objects (<c>new Number(1)</c>, <c>new Boolean(true)</c>) aren't yet
    /// wrappers here, so non-primitive/non-Symbol receivers fall through to
    /// <c>Object.prototype.toString</c> (lossy but adequate for Test262).
    /// </summary>
    private static string CoerceToString(object? receiver)
    {
        if (receiver is SharpTSSymbol)
            throw new ThrowException(new SharpTSTypeError(
                "Cannot convert a Symbol value to a string"));
        return receiver switch
        {
            string s => s,
            double d => SharpTS.Compilation.RuntimeTypes.Stringify(d),
            bool b => b ? "true" : "false",
            _ => receiver?.ToString() ?? "",
        };
    }

    public override string ToString() => $"function {_name}() {{ [native code] }}";
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

    /// <summary>
    /// Returns <c>Number.prototype</c> (so <c>Number.prototype.toString.call(x)</c>
    /// resolves), with built-in static members (Number.MAX_VALUE, isNaN, etc.)
    /// falling through to the registry.
    /// </summary>
    public object? GetMember(string name)
    {
        if (name == "prototype") return SharpTSNumberPrototype.Instance;
        return NumberBuiltIns.GetStaticMember(name);
    }

    public override string ToString() => "function Number() { [native code] }";
}

/// <summary>
/// <c>Number.prototype</c>. Exposes registered Number instance methods
/// (toFixed, toPrecision, toExponential, toString) as unbound callables
/// wrapped to coerce the receiver to a number per ECMA-262.
/// </summary>
public sealed class SharpTSNumberPrototype
{
    public static readonly SharpTSNumberPrototype Instance = new();
    private SharpTSNumberPrototype() { }

    public object? GetMember(string name)
    {
        var method = NumberBuiltIns.GetPrototypeMethod(name);
        if (method is null) return null;
        return new NumberPrototypeMethodWrapper(name, method);
    }

    public override string ToString() => "[object Number]";
}

/// <summary>
/// Adapter around a Number <see cref="BuiltInMethod"/>. Throws TypeError on
/// non-number receivers per ECMA-262 (Number.prototype.toString and friends
/// require <c>thisNumberValue</c>); plain numbers and bool/null aren't valid
/// either. Wrapper objects (<c>new Number(1)</c>) aren't real wrappers in
/// this interpreter, so this stays strict.
/// </summary>
internal sealed class NumberPrototypeMethodWrapper : ISharpTSCallable
{
    private readonly string _name;
    private readonly BuiltInMethod _inner;
    private readonly object? _receiver;
    private readonly bool _hasReceiver;

    public NumberPrototypeMethodWrapper(string name, BuiltInMethod inner)
    {
        _name = name;
        _inner = inner;
    }

    private NumberPrototypeMethodWrapper(string name, BuiltInMethod inner, object? receiver)
    {
        _name = name;
        _inner = inner;
        _receiver = receiver;
        _hasReceiver = true;
    }

    public int Arity() => _inner.MinArity;

    public NumberPrototypeMethodWrapper Bind(object? receiver)
        => new(_name, _inner, receiver);

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        if (!_hasReceiver || _receiver is not double d)
        {
            throw new ThrowException(new SharpTSTypeError(
                $"Number.prototype.{_name} requires that 'this' be a Number"));
        }

        return _inner.Bind(d).Call(interpreter, arguments);
    }

    public override string ToString() => $"function {_name}() {{ [native code] }}";
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

    /// <summary>
    /// Returns <c>Boolean.prototype</c>. Boolean has no static members worth
    /// exposing here, so an unknown name returns null (= undefined to user).
    /// </summary>
    public object? GetMember(string name)
    {
        if (name == "prototype") return SharpTSBooleanPrototype.Instance;
        return null;
    }

    public override string ToString() => "function Boolean() { [native code] }";
}

/// <summary>
/// <c>Boolean.prototype</c>. Exposes <c>toString</c> and <c>valueOf</c>
/// per ECMA-262 as wrapper callables that throw TypeError on non-boolean
/// receivers.
/// </summary>
public sealed class SharpTSBooleanPrototype
{
    public static readonly SharpTSBooleanPrototype Instance = new();
    private SharpTSBooleanPrototype() { }

    public object? GetMember(string name) => name switch
    {
        "toString" => BooleanPrototypeMethodWrapper.ToStringInstance,
        "valueOf" => BooleanPrototypeMethodWrapper.ValueOfInstance,
        _ => null,
    };

    public override string ToString() => "[object Boolean]";
}

/// <summary>
/// Adapter for Boolean.prototype.toString/valueOf. Throws TypeError on non-
/// boolean receivers per ECMA-262 (<c>thisBooleanValue</c>); returns the JS
/// string form ("true"/"false") or the primitive otherwise.
/// </summary>
internal sealed class BooleanPrototypeMethodWrapper : ISharpTSCallable
{
    public static readonly BooleanPrototypeMethodWrapper ToStringInstance = new("toString", isToString: true);
    public static readonly BooleanPrototypeMethodWrapper ValueOfInstance = new("valueOf", isToString: false);

    private readonly string _name;
    private readonly bool _isToString;
    private readonly object? _receiver;
    private readonly bool _hasReceiver;

    private BooleanPrototypeMethodWrapper(string name, bool isToString)
    {
        _name = name;
        _isToString = isToString;
    }

    private BooleanPrototypeMethodWrapper(string name, bool isToString, object? receiver)
    {
        _name = name;
        _isToString = isToString;
        _receiver = receiver;
        _hasReceiver = true;
    }

    public int Arity() => 0;

    public BooleanPrototypeMethodWrapper Bind(object? receiver)
        => new(_name, _isToString, receiver);

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        if (!_hasReceiver || _receiver is not bool b)
        {
            throw new ThrowException(new SharpTSTypeError(
                $"Boolean.prototype.{_name} requires that 'this' be a Boolean"));
        }
        return _isToString ? (b ? "true" : "false") : (object)b;
    }

    public override string ToString() => $"function {_name}() {{ [native code] }}";
}
