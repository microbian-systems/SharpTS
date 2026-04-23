using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Global <c>Array</c> identifier. Behaves as both a namespace (<c>Array.from</c>,
/// <c>Array.isArray</c>) and a constructor (<c>new Array(...)</c>), and exposes
/// <c>Array.prototype</c> with the common mutating methods rebound via
/// <c>Function.prototype.apply/call</c>.
/// </summary>
/// <remarks>
/// Prior to this type, a bare <c>Array</c> reference threw "Undefined variable"
/// because <c>Array</c> was registered as a non-singleton namespace. Real-world
/// code (yaml, lodash internals) frequently uses patterns like
/// <c>Array.prototype.push.apply(target, items)</c>, which requires <c>Array</c>
/// to be reifiable as a value and <c>Array.prototype</c> to carry its classic
/// methods.
/// </remarks>
public sealed class SharpTSArrayGlobal : ISharpTSCallable
{
    public static readonly SharpTSArrayGlobal Instance = new();
    private readonly SharpTSArrayPrototype _prototype = new();
    private SharpTSArrayGlobal() { }

    public int Arity() => 0;

    /// <summary>
    /// <c>new Array(...)</c> / <c>Array(...)</c>.
    /// If called with a single numeric argument, creates an array of that length;
    /// otherwise treats all arguments as elements.
    /// </summary>
    public object? Call(Interp interpreter, List<object?> arguments)
    {
        if (arguments.Count == 1 && arguments[0] is double d)
        {
            if (d < 0 || d > uint.MaxValue || Math.Floor(d) != d)
                throw new Exception("RangeError: Invalid array length.");
            int len = (int)d;
            // Matches the guard in SharpTSArray.Set: until sparse storage lands
            // we refuse to pre-allocate huge undefined-filled backing stores.
            if (len > 1_000_000)
                throw new Exception(
                    $"RangeError: Array({len}) would require eagerly allocating {len} slots. " +
                    "SharpTS does not yet implement sparse-array storage (see issue #73).");
            var list = new List<object?>(len);
            for (int i = 0; i < len; i++) list.Add(SharpTSUndefined.Instance);
            return new SharpTSArray(list);
        }
        return new SharpTSArray(new List<object?>(arguments));
    }

    public object? GetMember(string name)
    {
        if (name == "prototype") return _prototype;
        return BuiltInRegistry.Instance.GetStaticMethod("Array", name);
    }

    public override string ToString() => "function Array() { [native code] }";
}

/// <summary>
/// <c>Array.prototype</c>. Exposes the classic array methods as unbound
/// callables: each method expects to receive its target array via
/// <c>Function.prototype.apply/call</c> (or via <c>fn(target, ...args)</c> as
/// a pragmatic fallback), then performs the mutation/query on it.
/// </summary>
public sealed class SharpTSArrayPrototype
{
    public object? GetMember(string name) => name switch
    {
        "push" => SharpTSArrayUnboundMethod.Push,
        "pop" => SharpTSArrayUnboundMethod.Pop,
        "shift" => SharpTSArrayUnboundMethod.Shift,
        "unshift" => SharpTSArrayUnboundMethod.Unshift,
        "slice" => SharpTSArrayUnboundMethod.Slice,
        "concat" => SharpTSArrayUnboundMethod.Concat,
        "indexOf" => SharpTSArrayUnboundMethod.IndexOf,
        _ => null,
    };

    public override string ToString() => "[object Array]";
}

/// <summary>
/// An unbound method living on <c>Array.prototype</c>. When called directly as
/// <c>fn(target, ...args)</c>, the first argument is treated as the receiver.
/// When used via <c>Function.prototype.apply</c>/<c>call</c>, the bound
/// <c>this</c> becomes the receiver.
/// </summary>
public sealed class SharpTSArrayUnboundMethod : ISharpTSCallable
{
    public static readonly SharpTSArrayUnboundMethod Push = new("push", PushImpl);
    public static readonly SharpTSArrayUnboundMethod Pop = new("pop", PopImpl);
    public static readonly SharpTSArrayUnboundMethod Shift = new("shift", ShiftImpl);
    public static readonly SharpTSArrayUnboundMethod Unshift = new("unshift", UnshiftImpl);
    public static readonly SharpTSArrayUnboundMethod Slice = new("slice", SliceImpl);
    public static readonly SharpTSArrayUnboundMethod Concat = new("concat", ConcatImpl);
    public static readonly SharpTSArrayUnboundMethod IndexOf = new("indexOf", IndexOfImpl);

    private readonly string _name;
    private readonly Func<SharpTSArray, List<object?>, object?> _impl;
    private readonly object? _boundThis;

    private SharpTSArrayUnboundMethod(string name, Func<SharpTSArray, List<object?>, object?> impl, object? boundThis = null)
    {
        _name = name;
        _impl = impl;
        _boundThis = boundThis;
    }

    public int Arity() => 0;

    public object? Call(Interp interpreter, List<object?> arguments)
    {
        // Receiver: explicit bind (from .bind/.apply/.call) takes precedence,
        // otherwise treat the first argument as the receiver (pragmatic form).
        SharpTSArray? target = _boundThis as SharpTSArray;
        List<object?> rest;
        if (target != null)
        {
            rest = arguments;
        }
        else
        {
            if (arguments.Count == 0 || arguments[0] is not SharpTSArray first)
                throw new Exception($"Runtime Error: Array.prototype.{_name} requires an array receiver.");
            target = first;
            rest = arguments.Count > 1 ? arguments.GetRange(1, arguments.Count - 1) : new List<object?>();
        }
        return _impl(target, rest);
    }

    /// <summary>
    /// Produces a bound variant — used by <c>Function.prototype.apply/call</c>
    /// to pre-attach <c>thisArg</c> before invocation.
    /// </summary>
    public SharpTSArrayUnboundMethod BindTo(object? thisArg) => new(_name, _impl, thisArg);

    public override string ToString() => $"function {_name}() {{ [native code] }}";

    private static object? PushImpl(SharpTSArray arr, List<object?> args)
    {
        foreach (var item in args) arr.Add(item);
        return (double)arr.Length;
    }

    private static object? PopImpl(SharpTSArray arr, List<object?> args)
    {
        if (arr.Length == 0) return SharpTSUndefined.Instance;
        return arr.RemoveLast();
    }

    private static object? ShiftImpl(SharpTSArray arr, List<object?> args)
    {
        if (arr.Length == 0) return SharpTSUndefined.Instance;
        return arr.RemoveFirst();
    }

    private static object? UnshiftImpl(SharpTSArray arr, List<object?> args)
    {
        for (int i = 0; i < args.Count; i++) arr.Insert(i, args[i]);
        return (double)arr.Length;
    }

    private static object? SliceImpl(SharpTSArray arr, List<object?> args)
    {
        int start = args.Count > 0 && args[0] is double s ? (int)s : 0;
        int end = args.Count > 1 && args[1] is double e ? (int)e : arr.Length;
        if (start < 0) start = Math.Max(0, arr.Length + start);
        if (end < 0) end = Math.Max(0, arr.Length + end);
        start = Math.Min(start, arr.Length);
        end = Math.Min(end, arr.Length);
        if (end <= start) return new SharpTSArray(new List<object?>());
        var result = new List<object?>(end - start);
        for (int i = start; i < end; i++) result.Add(arr[i]);
        return new SharpTSArray(result);
    }

    private static object? ConcatImpl(SharpTSArray arr, List<object?> args)
    {
        var result = new List<object?>(arr);
        foreach (var a in args)
        {
            if (a is SharpTSArray inner) result.AddRange(inner);
            else result.Add(a);
        }
        return new SharpTSArray(result);
    }

    private static object? IndexOfImpl(SharpTSArray arr, List<object?> args)
    {
        if (args.Count == 0) return -1.0;
        var target = args[0];
        for (int i = 0; i < arr.Length; i++)
        {
            if (Equals(arr[i], target)) return (double)i;
        }
        return -1.0;
    }
}
