using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Represents the Readable stream constructor exported from the 'stream' module.
/// Supports instantiation via <c>new Readable(options?)</c>.
/// </summary>
public sealed class SharpTSReadableConstructor : ISharpTSCallable
{
    /// <summary>
    /// The singleton instance of the Readable constructor.
    /// </summary>
    public static readonly SharpTSReadableConstructor Instance = new();

    private SharpTSReadableConstructor() { }

    /// <summary>
    /// Gets the arity (number of required parameters) for the constructor.
    /// Readable constructor takes 0 required arguments.
    /// </summary>
    public int Arity() => 0;

    /// <summary>
    /// Creates a new <see cref="SharpTSReadable"/> instance.
    /// </summary>
    public object? Call(Interp interpreter, List<object?> arguments)
    {
        var stream = new SharpTSReadable();

        // Process options if provided
        if (arguments.Count > 0 && arguments[0] is SharpTSObject options)
        {
            // read callback: called when data is requested
            if (options.GetProperty("read") is ISharpTSCallable readCallback)
            {
                // Store for subclass implementations
                // Note: In the simple sync model, we don't use this callback
            }

            // encoding option
            if (options.GetProperty("encoding") is string encoding)
            {
                var setEncoding = stream.GetMember("setEncoding") as Runtime.BuiltIns.BuiltInMethod;
                setEncoding?.Bind(stream).CallBoxed(interpreter, [encoding]);
            }

            // objectMode option
            if (options.GetProperty("objectMode") is true)
            {
                stream.ObjectMode = true;
            }

            // highWaterMark option
            if (options.GetProperty("highWaterMark") is double hwm)
            {
                stream.HighWaterMark = (int)hwm;
            }
        }

        return stream;
    }

    public RuntimeValue Call(Interp interpreter, ReadOnlySpan<RuntimeValue> arguments)
        => RuntimeValue.FromBoxed(Call(interpreter, CallableInterop.ToBoxedList(arguments)));

    /// <summary>
    /// Gets a property from the Readable constructor (static properties/methods).
    /// </summary>
    public object? GetProperty(string name)
    {
        return name switch
        {
            "from" => new BuiltInMethod("from", 1, 2, ReadableFrom),
            "isReadable" => new BuiltInMethod("isReadable", 1, IsReadable),
            _ => null
        };
    }

    /// <summary>
    /// Readable.from(iterable, options?) — creates a Readable from an iterable in object mode.
    /// </summary>
    private static object? ReadableFrom(Interp interpreter, object? receiver, List<object?> args)
    {
        var iterable = args.Count > 0 ? args[0] : null;
        var stream = new SharpTSReadable();
        stream.ObjectMode = true;

        // Extract options
        if (args.Count > 1 && args[1] is SharpTSObject options)
        {
            if (options.GetProperty("objectMode") is false)
                stream.ObjectMode = false;
        }

        // Push items from iterable
        if (iterable is SharpTSArray arr)
        {
            foreach (var item in arr)
            {
                var pushMethod = stream.GetMember("push") as BuiltInMethod;
                pushMethod?.Bind(stream).CallBoxed(interpreter, [item]);
            }
        }
        else if (iterable is List<object?> list)
        {
            foreach (var item in list)
            {
                var pushMethod = stream.GetMember("push") as BuiltInMethod;
                pushMethod?.Bind(stream).CallBoxed(interpreter, [item]);
            }
        }

        // Push null to signal EOF
        var pushEnd = stream.GetMember("push") as BuiltInMethod;
        pushEnd?.Bind(stream).CallBoxed(interpreter, [null]);

        return stream;
    }

    /// <summary>
    /// Readable.isReadable(stream) — checks if stream is a readable stream.
    /// </summary>
    private static object? IsReadable(Interp interpreter, object? receiver, List<object?> args)
    {
        var obj = args.Count > 0 ? args[0] : null;
        return obj is SharpTSReadable;
    }

    /// <summary>
    /// Sets a property on the Readable constructor (static properties).
    /// </summary>
    public bool SetProperty(string name, object? value)
    {
        return false;
    }

    public override string ToString() => "[Function: Readable]";
}
