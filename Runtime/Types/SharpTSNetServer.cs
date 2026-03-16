using System.Net;
using System.Net.Sockets;
using SharpTS.Runtime.BuiltIns;
using SharpTS.TypeSystem;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of a Node.js net.Server.
/// Extends SharpTSEventEmitter for full event handling support.
/// Events: 'connection', 'listening', 'close', 'error'
/// </summary>
public class SharpTSNetServer : SharpTSEventEmitter, ITypeCategorized, IDisposable
{
    /// <inheritdoc />
    public TypeCategory RuntimeCategory => TypeCategory.Record;

    private TcpListener? _listener;
    private bool _isListening;
    private Interp? _interpreter;
    private CancellationTokenSource? _cts;
    private ISharpTSCallable? _connectionListener;
    private int _port;
    private string _host = "0.0.0.0";
    private readonly List<SharpTSSocket> _connections = [];
    private int _maxConnections = int.MaxValue;

    /// <summary>
    /// Creates a new TCP server with an optional connection listener.
    /// </summary>
    public SharpTSNetServer(ISharpTSCallable? connectionListener = null)
    {
        _connectionListener = connectionListener;
    }

    /// <summary>
    /// Whether the server is currently listening.
    /// </summary>
    public bool Listening => _isListening;

    /// <summary>
    /// Gets a member by name for interpreter dispatch.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            "listening" => Listening,
            "listen" => new BuiltInMethod("listen", 0, 4, Listen),
            "close" => new BuiltInMethod("close", 0, 1, Close),
            "address" => new BuiltInMethod("address", 0, GetAddress),
            "getConnections" => new BuiltInMethod("getConnections", 1, GetConnections),
            "ref" => new BuiltInMethod("ref", 0, Ref),
            "unref" => new BuiltInMethod("unref", 0, Unref),
            "maxConnections" => (double)_maxConnections,
            _ => base.GetMember(name)
        };
    }

    /// <summary>
    /// Sets a member by name.
    /// </summary>
    public void SetMember(string name, object? value)
    {
        if (name == "maxConnections" && value is double d)
            _maxConnections = (int)d;
    }

    /// <summary>
    /// Starts listening on the specified port.
    /// </summary>
    private object? Listen(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_isListening)
            throw new Exception("Runtime Error: Server is already listening");

        _interpreter = interpreter;

        // Parse arguments: listen(port?, host?, backlog?, callback?)
        // or listen(options, callback?)
        ISharpTSCallable? callback = null;

        if (args.Count > 0 && args[0] is SharpTSObject options)
        {
            if (options.GetProperty("port") is double p) _port = (int)p;
            if (options.GetProperty("host") is string h) _host = h;
            if (args.Count > 1 && args[1] is ISharpTSCallable cb) callback = cb;
        }
        else
        {
            int argIdx = 0;
            if (argIdx < args.Count && args[argIdx] is double portNum)
            {
                _port = (int)portNum;
                argIdx++;
            }
            if (argIdx < args.Count && args[argIdx] is string host)
            {
                _host = host;
                argIdx++;
            }
            // Skip backlog (number)
            if (argIdx < args.Count && args[argIdx] is double)
                argIdx++;
            if (argIdx < args.Count && args[argIdx] is ISharpTSCallable cb)
                callback = cb;
            // Also check: listen(port, callback) where callback is arg[1]
            if (callback == null)
            {
                for (int i = 0; i < args.Count; i++)
                {
                    if (args[i] is ISharpTSCallable c) { callback = c; break; }
                }
            }
        }

        var ipAddress = _host == "0.0.0.0" || _host == "::"
            ? IPAddress.Any
            : IPAddress.TryParse(_host, out var parsed) ? parsed : IPAddress.Loopback;

        _listener = new TcpListener(ipAddress, _port);

        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            EmitEvent(interpreter, "error", [new SharpTSError(ex.Message)]);
            return this;
        }

        // If port was 0, get the assigned port
        if (_port == 0)
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _isListening = true;
        _cts = new CancellationTokenSource();

        // Keep event loop alive
        interpreter.Ref();

        // Emit 'listening' event and call callback
        if (callback != null)
            callback.Call(interpreter, []);
        EmitEvent(interpreter, "listening", []);

        // Start accepting connections
        StartAccepting(interpreter);

        return this;
    }

    /// <summary>
    /// Starts accepting connections asynchronously.
    /// </summary>
    private void StartAccepting(Interp interpreter)
    {
        var token = _cts!.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync(token);

                    if (_connections.Count >= _maxConnections)
                    {
                        tcpClient.Close();
                        continue;
                    }

                    interpreter.ScheduleTimer(0, 0, () =>
                    {
                        var socket = new SharpTSSocket(tcpClient);
                        _connections.Add(socket);

                        // Call connection listener if set
                        _connectionListener?.Call(interpreter, [socket]);

                        // Emit 'connection' event
                        EmitEvent(interpreter, "connection", [socket]);

                        // Start reading on the socket
                        socket.StartReading(interpreter);
                    }, isInterval: false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
            }
        }, token);
    }

    /// <summary>
    /// Closes the server.
    /// </summary>
    private object? Close(Interp interpreter, object? receiver, List<object?> args)
    {
        if (!_isListening)
            return this;

        _cts?.Cancel();

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Ignore
        }

        _isListening = false;
        _interpreter?.Unref();

        ISharpTSCallable? callback = args.Count > 0 ? args[0] as ISharpTSCallable : null;
        callback?.Call(interpreter, []);

        EmitEvent(interpreter, "close", []);

        return this;
    }

    /// <summary>
    /// Gets the server address information.
    /// </summary>
    private object? GetAddress(Interp interpreter, object? receiver, List<object?> args)
    {
        if (!_isListening || _listener == null) return null;

        var ep = (IPEndPoint)_listener.LocalEndpoint;
        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["address"] = ep.Address.ToString(),
            ["family"] = ep.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
            ["port"] = (double)ep.Port
        });
    }

    /// <summary>
    /// Gets the number of concurrent connections.
    /// </summary>
    private object? GetConnections(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count > 0 && args[0] is ISharpTSCallable callback)
        {
            callback.Call(interpreter, [null, (double)_connections.Count]);
        }
        return this;
    }

    private object? Ref(Interp interpreter, object? receiver, List<object?> args)
    {
        _interpreter?.Ref();
        return this;
    }

    private object? Unref(Interp interpreter, object? receiver, List<object?> args)
    {
        _interpreter?.Unref();
        return this;
    }

    internal void EmitEvent(Interp interpreter, string eventName, List<object?> args)
    {
        var emit = base.GetMember("emit") as BuiltInMethod;
        if (emit != null)
        {
            var fullArgs = new List<object?> { eventName };
            fullArgs.AddRange(args);
            emit.Bind(this).Call(interpreter, fullArgs);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _cts?.Dispose();
        foreach (var conn in _connections)
        {
            try { conn.GetMember("destroy"); } catch { }
        }
        _connections.Clear();
    }

    public override string ToString() => $"Server {{ listening: {Listening} }}";
}
