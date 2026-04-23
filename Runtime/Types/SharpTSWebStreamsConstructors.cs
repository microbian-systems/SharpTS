using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Callable constructor wrappers for the Web Streams types. Used as the
/// module-exported values for <c>node:stream/web</c> so that
/// <c>new ReadableStream(...)</c> works after an ES or CJS import.
/// </summary>
public sealed class SharpTSReadableStreamConstructor : ISharpTSCallable
{
    public static readonly SharpTSReadableStreamConstructor Instance = new();
    private SharpTSReadableStreamConstructor() { }

    public int Arity() => 0;

    public object? Call(Interp interpreter, List<object?> arguments)
    {
        var src = arguments.Count > 0 ? arguments[0] : null;
        var strat = arguments.Count > 1 ? arguments[1] : null;
        return new SharpTSReadableStream(interpreter, src, strat);
    }

    public object? GetProperty(string name)
    {
        // ReadableStream.from(iterable) — build a stream from an iterable.
        if (name == "from")
        {
            return new BuiltInMethod("from", 1, (interp, _, args) =>
            {
                var iterable = args.Count > 0 ? args[0] : null;
                return BuildFromIterable(interp, iterable);
            });
        }
        return null;
    }

    private static SharpTSReadableStream BuildFromIterable(Interp interpreter, object? iterable)
    {
        // Drain the iterable eagerly into a list for v1 — async iterables use `pull`.
        var pulled = new Queue<object?>();
        switch (iterable)
        {
            case SharpTSArray arr:
                foreach (var e in arr) pulled.Enqueue(e);
                break;
            case string s:
                foreach (var ch in s) pulled.Enqueue(ch.ToString());
                break;
            case SharpTSSet set:
                foreach (var e in set) pulled.Enqueue(e);
                break;
        }

        var src = new SharpTSObject(new Dictionary<string, object?>
        {
            ["pull"] = new BuiltInMethod("pull", 1, (_, _, args) =>
            {
                var controller = args.Count > 0 ? args[0] : null;
                if (controller is not SharpTSReadableStreamDefaultController ctrl)
                    return SharpTSUndefined.Instance;
                if (pulled.Count > 0)
                {
                    var chunk = pulled.Dequeue();
                    var enq = ctrl.GetMember("enqueue") as BuiltInMethod;
                    enq?.Call(null!, [chunk]);
                }
                else
                {
                    var close = ctrl.GetMember("close") as BuiltInMethod;
                    close?.Call(null!, []);
                }
                return SharpTSUndefined.Instance;
            }),
        });
        return new SharpTSReadableStream(interpreter, src, null);
    }

    public override string ToString() => "[Function: ReadableStream]";
}

public sealed class SharpTSWritableStreamConstructor : ISharpTSCallable
{
    public static readonly SharpTSWritableStreamConstructor Instance = new();
    private SharpTSWritableStreamConstructor() { }

    public int Arity() => 0;

    public object? Call(Interp interpreter, List<object?> arguments)
    {
        var sink = arguments.Count > 0 ? arguments[0] : null;
        var strat = arguments.Count > 1 ? arguments[1] : null;
        return new SharpTSWritableStream(interpreter, sink, strat);
    }

    public override string ToString() => "[Function: WritableStream]";
}

public sealed class SharpTSTransformStreamConstructor : ISharpTSCallable
{
    public static readonly SharpTSTransformStreamConstructor Instance = new();
    private SharpTSTransformStreamConstructor() { }

    public int Arity() => 0;

    public object? Call(Interp interpreter, List<object?> arguments)
    {
        var transformer = arguments.Count > 0 ? arguments[0] : null;
        var ws = arguments.Count > 1 ? arguments[1] : null;
        var rs = arguments.Count > 2 ? arguments[2] : null;
        return new SharpTSTransformStream(interpreter, transformer, ws, rs);
    }

    public override string ToString() => "[Function: TransformStream]";
}

public sealed class SharpTSByteLengthQueuingStrategyConstructor : ISharpTSCallable
{
    public static readonly SharpTSByteLengthQueuingStrategyConstructor Instance = new();
    private SharpTSByteLengthQueuingStrategyConstructor() { }

    public int Arity() => 1;

    public object? Call(Interp interpreter, List<object?> arguments)
    {
        var hwm = 0.0;
        if (arguments.Count > 0 && arguments[0] != null &&
            StreamFields.TryGet(arguments[0], "highWaterMark", out var h))
        {
            hwm = h switch { double d => d, int i => i, long l => l, _ => 0.0 };
        }
        return new SharpTSByteLengthQueuingStrategy(hwm);
    }

    public override string ToString() => "[Function: ByteLengthQueuingStrategy]";
}

public sealed class SharpTSCountQueuingStrategyConstructor : ISharpTSCallable
{
    public static readonly SharpTSCountQueuingStrategyConstructor Instance = new();
    private SharpTSCountQueuingStrategyConstructor() { }

    public int Arity() => 1;

    public object? Call(Interp interpreter, List<object?> arguments)
    {
        var hwm = 0.0;
        if (arguments.Count > 0 && arguments[0] != null &&
            StreamFields.TryGet(arguments[0], "highWaterMark", out var h))
        {
            hwm = h switch { double d => d, int i => i, long l => l, _ => 0.0 };
        }
        return new SharpTSCountQueuingStrategy(hwm);
    }

    public override string ToString() => "[Function: CountQueuingStrategy]";
}
