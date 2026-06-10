using SharpTS.Runtime.Types;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of the Node.js 'dgram' module (UDP sockets).
/// </summary>
public static class DgramModuleInterpreter
{
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            ["createSocket"] = new BuiltInMethod("createSocket", 1, 2, CreateSocket),
            ["Socket"] = SharpTSDatagramSocketConstructor.Instance
        };
    }

    private static object? CreateSocket(Interp interpreter, object? receiver, List<object?> args)
    {
        string type = "udp4";

        if (args.Count > 0)
        {
            if (args[0] is string t) type = t;
            else if (args[0] is SharpTSObject options)
            {
                if (options.GetProperty("type") is string ot) type = ot;
            }
        }

        var socket = new SharpTSDatagramSocket(type);

        // If callback provided, attach as 'message' listener
        if (args.Count > 1 && args[1] is ISharpTSCallable callback)
        {
            var onMethod = socket.GetMember("on") as BuiltInMethod;
            onMethod?.Bind(socket).CallBoxed(interpreter, new List<object?> { "message", callback });
        }

        return socket;
    }

    public sealed class SharpTSDatagramSocketConstructor : ISharpTSCallable
    {
        public static readonly SharpTSDatagramSocketConstructor Instance = new();
        private SharpTSDatagramSocketConstructor() { }

        public int Arity() => 1;

        public object? Call(Interp interpreter, List<object?> args)
        {
            return CreateSocket(interpreter, null, args);
        }

        public RuntimeValue CallV2(Interp interpreter, ReadOnlySpan<RuntimeValue> arguments)
            => RuntimeValue.FromBoxed(Call(interpreter, CallableInterop.ToBoxedList(arguments)));
    }
}
