using SharpTS.Runtime.Types;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of the Node.js 'net' module.
/// </summary>
/// <remarks>
/// Provides TCP networking functionality:
/// - createServer(options?, connectionListener?) - create a TCP server
/// - createConnection(options, connectListener?) / connect() - create a TCP client
/// - Server and Socket constructors
/// - isIP(), isIPv4(), isIPv6() utility functions
/// </remarks>
public static class NetModuleInterpreter
{
    /// <summary>
    /// Gets all exported values for the net module.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            ["createServer"] = new BuiltInMethod("createServer", 0, 2, CreateServer),
            ["createConnection"] = new BuiltInMethod("createConnection", 1, 2, CreateConnection),
            ["connect"] = new BuiltInMethod("connect", 1, 2, CreateConnection),
            ["isIP"] = new BuiltInMethod("isIP", 1, IsIP),
            ["isIPv4"] = new BuiltInMethod("isIPv4", 1, IsIPv4),
            ["isIPv6"] = new BuiltInMethod("isIPv6", 1, IsIPv6),
            ["Server"] = new BuiltInMethod("Server", 0, 2, CreateServer),
            ["Socket"] = new BuiltInMethod("Socket", 0, 1, CreateSocket)
        };
    }

    /// <summary>
    /// Creates a new TCP server.
    /// </summary>
    private static object? CreateServer(Interp interpreter, object? receiver, List<object?> args)
    {
        ISharpTSCallable? connectionListener = null;

        if (args.Count > 0)
        {
            if (args[0] is ISharpTSCallable cb)
            {
                connectionListener = cb;
            }
            else if (args[0] is SharpTSObject && args.Count > 1 && args[1] is ISharpTSCallable cb2)
            {
                // First arg is options, second is callback
                connectionListener = cb2;
            }
        }

        return new SharpTSNetServer(connectionListener);
    }

    /// <summary>
    /// Creates a new TCP connection (client socket).
    /// </summary>
    private static object? CreateConnection(Interp interpreter, object? receiver, List<object?> args)
    {
        var socket = new SharpTSSocket();

        // Delegate to socket.connect()
        var connectMethod = socket.GetMember("connect") as BuiltInMethod;
        connectMethod?.Bind(socket).CallBoxed(interpreter, args);

        return socket;
    }

    /// <summary>
    /// Creates a new unconnected Socket.
    /// </summary>
    private static object? CreateSocket(Interp interpreter, object? receiver, List<object?> args)
    {
        return new SharpTSSocket();
    }

    /// <summary>
    /// Tests if input is an IP address. Returns 4 for IPv4, 6 for IPv6, 0 for invalid.
    /// </summary>
    private static object? IsIP(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count == 0 || args[0] is not string input)
            return 0.0;

        if (System.Net.IPAddress.TryParse(input, out var addr))
        {
            return addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 6.0 : 4.0;
        }
        return 0.0;
    }

    /// <summary>
    /// Tests if input is an IPv4 address.
    /// </summary>
    private static object? IsIPv4(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count == 0 || args[0] is not string input)
            return false;

        return System.Net.IPAddress.TryParse(input, out var addr)
            && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    /// <summary>
    /// Tests if input is an IPv6 address.
    /// </summary>
    private static object? IsIPv6(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count == 0 || args[0] is not string input)
            return false;

        return System.Net.IPAddress.TryParse(input, out var addr)
            && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
    }
}
