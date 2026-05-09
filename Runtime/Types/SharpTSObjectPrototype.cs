using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// <c>Object.prototype</c>. Exposes the classic object methods as unbound
/// callables: each expects to receive the target object via
/// <c>Function.prototype.apply/call</c>. Added so that real-world CJS packages
/// (lodash's <c>hasOwnProperty.call(obj, key)</c>, Intl <c>toString</c>
/// detection, etc.) can resolve these names.
/// </summary>
public sealed class SharpTSObjectPrototype
{
    public static readonly SharpTSObjectPrototype Instance = new();
    private SharpTSObjectPrototype() { }

    public object? GetMember(string name) => name switch
    {
        "hasOwnProperty" => SharpTSObjectUnboundMethod.HasOwnProperty,
        "toString" => SharpTSObjectUnboundMethod.ToString_,
        "valueOf" => SharpTSObjectUnboundMethod.ValueOf,
        "isPrototypeOf" => SharpTSObjectUnboundMethod.IsPrototypeOf,
        "propertyIsEnumerable" => SharpTSObjectUnboundMethod.PropertyIsEnumerable,
        _ => null,
    };

    public override string ToString() => "[object Object]";
}

/// <summary>
/// An unbound method on <c>Object.prototype</c>. Invoked via
/// <c>Function.prototype.call/apply</c> with the target object supplied as
/// <c>this</c>, or directly with the target as the first argument.
/// </summary>
public sealed class SharpTSObjectUnboundMethod : ISharpTSCallable
{
    public static readonly SharpTSObjectUnboundMethod HasOwnProperty = new("hasOwnProperty", HasOwnPropertyImpl);
    public static readonly SharpTSObjectUnboundMethod ToString_ = new("toString", ToStringImpl);
    public static readonly SharpTSObjectUnboundMethod ValueOf = new("valueOf", ValueOfImpl);
    public static readonly SharpTSObjectUnboundMethod IsPrototypeOf = new("isPrototypeOf", IsPrototypeOfImpl);
    public static readonly SharpTSObjectUnboundMethod PropertyIsEnumerable = new("propertyIsEnumerable", PropertyIsEnumerableImpl);

    private readonly string _name;
    private readonly Func<object?, List<object?>, object?> _impl;
    private readonly object? _boundThis;
    private readonly bool _hasBoundThis;

    private SharpTSObjectUnboundMethod(string name, Func<object?, List<object?>, object?> impl)
    {
        _name = name;
        _impl = impl;
        _boundThis = null;
        _hasBoundThis = false;
    }

    private SharpTSObjectUnboundMethod(string name, Func<object?, List<object?>, object?> impl, object? boundThis)
    {
        _name = name;
        _impl = impl;
        _boundThis = boundThis;
        _hasBoundThis = true;
    }

    public int Arity() => 0;

    public object? Call(Interp interpreter, List<object?> arguments)
    {
        object? target;
        List<object?> rest;
        if (_hasBoundThis)
        {
            target = _boundThis;
            rest = arguments;
        }
        else
        {
            if (arguments.Count == 0)
                throw new Exception($"Runtime Error: Object.prototype.{_name} requires a receiver.");
            target = arguments[0];
            rest = arguments.Count > 1 ? arguments.GetRange(1, arguments.Count - 1) : new List<object?>();
        }
        return _impl(target, rest);
    }

    public SharpTSObjectUnboundMethod BindTo(object? thisArg) => new(_name, _impl, thisArg);

    public override string ToString() => $"function {_name}() {{ [native code] }}";

    private static object? HasOwnPropertyImpl(object? target, List<object?> args)
    {
        if (target == null || args.Count == 0) return false;
        // ECMA-262 §19.1.3.2 ToPropertyKey: symbol args route through the
        // symbol-keyed dispatch instead of being stringified.
        if (args[0] is SharpTSSymbol sym)
        {
            return target switch
            {
                SharpTSObject obj => obj.HasSymbolProperty(sym),
                SharpTSInstance inst => inst.HasSymbolProperty(sym),
                _ => false,
            };
        }
        var key = args[0]?.ToString() ?? "";
        return target switch
        {
            SharpTSObject obj => obj.HasProperty(key),
            SharpTSInstance inst => inst.HasProperty(key),
            IDictionary<string, object?> dict => dict.ContainsKey(key),
            // Built-in functions expose `name` and `length` as own properties
            // per ECMA-262 §17. test262's verifyProperty calls
            // hasOwnProperty(fn, "name") before reading the descriptor — without
            // this branch the assertion fails before we ever see the descriptor.
            ISharpTSCallable when key is "name" or "length" => true,
            _ => false,
        };
    }

    private static object? ToStringImpl(object? target, List<object?> args)
    {
        // ECMA-262 20.1.3.6: toString uses the @@toStringTag tag of the
        // receiver. Kept conservative — extending this to every built-in tag
        // broke Lodash's typeof detection (it uses `Object.prototype.toString.call`
        // on functions and expected "[object Object]" back). Add new tags
        // only when a specific spec test needs them.
        if (target == null) return "[object Null]";
        if (target is SharpTSUndefined) return "[object Undefined]";
        if (target is string) return "[object String]";
        if (target is double or int) return "[object Number]";
        if (target is bool) return "[object Boolean]";
        if (target is SharpTSArray) return "[object Array]";
        if (target is SharpTSMath) return "[object Math]";
        return "[object Object]";
    }

    private static object? ValueOfImpl(object? target, List<object?> args) => target;

    private static object? IsPrototypeOfImpl(object? target, List<object?> args) => false;

    private static object? PropertyIsEnumerableImpl(object? target, List<object?> args)
    {
        if (target == null || args.Count == 0) return false;
        var key = args[0]?.ToString() ?? "";
        return target switch
        {
            SharpTSObject obj => obj.HasProperty(key),
            IDictionary<string, object?> dict => dict.ContainsKey(key),
            _ => false,
        };
    }
}
