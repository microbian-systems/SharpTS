using SharpTS.Runtime.Types;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of the Node.js 'tls' module.
/// </summary>
/// <remarks>
/// Provides TLS/SSL networking functionality:
/// - createServer(options?, callback?) - create a TLS server
/// - connect(port, host?, options?, callback?) - create a TLS client connection
/// - createSecureContext(options?) - create a secure context object
/// - DEFAULT_MIN_VERSION, DEFAULT_MAX_VERSION - protocol version constants
/// </remarks>
public static class TlsModuleInterpreter
{
    /// <summary>
    /// Gets all exported values for the tls module.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            ["createServer"] = new BuiltInMethod("createServer", 0, 2, CreateServer),
            ["connect"] = new BuiltInMethod("connect", 1, 4, Connect),
            ["createSecureContext"] = new BuiltInMethod("createSecureContext", 0, 1, CreateSecureContext),
            ["Server"] = new BuiltInMethod("Server", 0, 2, CreateServer),
            ["TLSSocket"] = new BuiltInMethod("TLSSocket", 0, 1, CreateTlsSocket),
            ["DEFAULT_MIN_VERSION"] = "TLSv1.2",
            ["DEFAULT_MAX_VERSION"] = "TLSv1.3"
        };
    }

    /// <summary>
    /// Creates a new TLS server.
    /// tls.createServer(options?, connectionListener?)
    /// </summary>
    private static object? CreateServer(Interp interpreter, object? receiver, List<object?> args)
    {
        SharpTSObject? options = null;
        ISharpTSCallable? connectionListener = null;

        if (args.Count > 0)
        {
            if (args[0] is SharpTSObject opts)
            {
                options = opts;
                if (args.Count > 1 && args[1] is ISharpTSCallable cb)
                    connectionListener = cb;
            }
            else if (args[0] is ISharpTSCallable cb)
            {
                connectionListener = cb;
            }
        }

        return new SharpTSTlsServer(options, connectionListener);
    }

    /// <summary>
    /// Creates a TLS connection to a remote server.
    /// tls.connect(port, host?, options?, callback?)
    /// tls.connect(options, callback?)
    /// </summary>
    private static object? Connect(Interp interpreter, object? receiver, List<object?> args)
    {
        var socket = new SharpTSTlsSocket();
        int port;
        string host = "localhost";
        SharpTSObject? options = null;
        ISharpTSCallable? callback = null;

        if (args.Count > 0 && args[0] is SharpTSObject opts)
        {
            // tls.connect(options, callback?)
            options = opts;
            port = (int)(double)(opts.GetProperty("port") ?? throw new Exception("Runtime Error: port is required"));
            if (opts.GetProperty("host") is string h) host = h;
            if (args.Count > 1 && args[1] is ISharpTSCallable cb) callback = cb;
        }
        else if (args.Count > 0 && args[0] is double portNum)
        {
            // tls.connect(port, host?, options?, callback?)
            port = (int)portNum;
            int idx = 1;
            if (idx < args.Count && args[idx] is string h) { host = h; idx++; }
            if (idx < args.Count && args[idx] is SharpTSObject o) { options = o; idx++; }
            if (idx < args.Count && args[idx] is ISharpTSCallable cb) callback = cb;
            // Also check: connect(port, callback)
            if (callback == null)
            {
                for (int i = 1; i < args.Count; i++)
                {
                    if (args[i] is ISharpTSCallable c) { callback = c; break; }
                }
            }
        }
        else
        {
            throw new Exception("Runtime Error: tls.connect requires port number or options object");
        }

        socket.ConnectTls(interpreter, port, host, options, callback);
        return socket;
    }

    /// <summary>
    /// Creates a secure context object.
    /// tls.createSecureContext(options?)
    /// </summary>
    private static object? CreateSecureContext(Interp interpreter, object? receiver, List<object?> args)
    {
        var context = new Dictionary<string, object?>();

        if (args.Count > 0 && args[0] is SharpTSObject options)
        {
            if (options.GetProperty("cert") is string cert) context["cert"] = cert;
            if (options.GetProperty("key") is string key) context["key"] = key;
            if (options.GetProperty("ca") is string ca) context["ca"] = ca;
            if (options.GetProperty("minVersion") is string minVer) context["minVersion"] = minVer;
            if (options.GetProperty("maxVersion") is string maxVer) context["maxVersion"] = maxVer;
        }

        return new SharpTSObject(context);
    }

    /// <summary>
    /// Creates a new unconnected TLS socket.
    /// </summary>
    private static object? CreateTlsSocket(Interp interpreter, object? receiver, List<object?> args)
    {
        return new SharpTSTlsSocket();
    }
}
