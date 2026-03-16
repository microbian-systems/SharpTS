using System.Net;
using System.Net.Sockets;
using System.Text;
using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of a Node.js net.Socket.
/// Extends SharpTSDuplex for full duplex stream semantics (Readable + Writable).
/// </summary>
public class SharpTSSocket : SharpTSEventEmitter
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Interp? _interpreter;
    private bool _connecting;
    private bool _destroyed;
    private bool _ended;
    private int _bytesRead;
    private int _bytesWritten;
    private CancellationTokenSource? _readCts;
    private string _encoding = "utf8";
    private bool _readingStarted;

    /// <summary>
    /// Creates a new Socket wrapping an existing TcpClient (server-side).
    /// </summary>
    public SharpTSSocket(TcpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _stream = client.GetStream();
    }

    /// <summary>
    /// Creates a new unconnected Socket (client-side).
    /// </summary>
    public SharpTSSocket()
    {
    }

    /// <summary>
    /// Gets a member by name for interpreter dispatch.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            // Methods
            "connect" => new BuiltInMethod("connect", 1, 3, Connect),
            "write" => new BuiltInMethod("write", 1, 3, Write),
            "end" => new BuiltInMethod("end", 0, 3, End),
            "destroy" => new BuiltInMethod("destroy", 0, 1, Destroy),
            "setEncoding" => new BuiltInMethod("setEncoding", 1, SetEncoding),
            "setTimeout" => new BuiltInMethod("setTimeout", 1, 2, SetTimeout),
            "setNoDelay" => new BuiltInMethod("setNoDelay", 0, 1, SetNoDelay),
            "setKeepAlive" => new BuiltInMethod("setKeepAlive", 0, 2, SetKeepAlive),
            "address" => new BuiltInMethod("address", 0, Address),
            "ref" => new BuiltInMethod("ref", 0, Ref),
            "unref" => new BuiltInMethod("unref", 0, Unref),
            "pause" => new BuiltInMethod("pause", 0, Pause),
            "resume" => new BuiltInMethod("resume", 0, Resume),
            "pipe" => new BuiltInMethod("pipe", 1, 2, Pipe),

            // Properties
            "remoteAddress" => _client?.Client?.RemoteEndPoint is IPEndPoint rep ? rep.Address.ToString() : (object?)null,
            "remotePort" => _client?.Client?.RemoteEndPoint is IPEndPoint rep2 ? (double)rep2.Port : (object?)null,
            "remoteFamily" => _client?.Client?.RemoteEndPoint is IPEndPoint rep3 ? (rep3.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4") : (object?)null,
            "localAddress" => _client?.Client?.LocalEndPoint is IPEndPoint lep ? lep.Address.ToString() : (object?)null,
            "localPort" => _client?.Client?.LocalEndPoint is IPEndPoint lep2 ? (double)lep2.Port : (object?)null,
            "bytesRead" => (double)_bytesRead,
            "bytesWritten" => (double)_bytesWritten,
            "connecting" => _connecting,
            "destroyed" => _destroyed,
            "readyState" => GetReadyState(),

            // EventEmitter methods
            _ => base.GetMember(name)
        };
    }

    private string GetReadyState()
    {
        if (_connecting) return "opening";
        if (_destroyed) return "closed";
        if (_client?.Connected == true) return "open";
        return "closed";
    }

    /// <summary>
    /// Connects to a remote host.
    /// </summary>
    private object? Connect(Interp interpreter, object? receiver, List<object?> args)
    {
        _interpreter = interpreter;

        int port;
        string host = "localhost";
        ISharpTSCallable? callback = null;

        if (args[0] is SharpTSObject options)
        {
            port = (int)(double)(options.GetProperty("port") ?? throw new Exception("Runtime Error: port is required"));
            if (options.GetProperty("host") is string h) host = h;
            if (args.Count > 1 && args[1] is ISharpTSCallable cb) callback = cb;
        }
        else if (args[0] is double portNum)
        {
            port = (int)portNum;
            if (args.Count > 1 && args[1] is string h) host = h;
            if (args.Count > 1 && args[1] is ISharpTSCallable cb1) callback = cb1;
            if (args.Count > 2 && args[2] is ISharpTSCallable cb2) callback = cb2;
        }
        else
        {
            throw new Exception("Runtime Error: connect requires port number or options object");
        }

        if (callback != null)
        {
            AddListenerDirect("connect", callback);
        }

        _connecting = true;
        _client = new TcpClient();

        // Start async connect via Task.Run
        var capturedHost = host;
        var capturedPort = port;
        _ = Task.Run(async () =>
        {
            try
            {
                await _client.ConnectAsync(capturedHost, capturedPort);
                _stream = _client.GetStream();
                _connecting = false;
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    EmitEvent(interpreter, "connect", []);
                    StartReading(interpreter);
                }, isInterval: false);
            }
            catch (Exception ex)
            {
                _connecting = false;
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    EmitEvent(interpreter, "error", [new SharpTSError(ex.Message)]);
                }, isInterval: false);
            }
        });

        return this;
    }

    /// <summary>
    /// Writes data to the socket.
    /// </summary>
    private object? Write(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_destroyed || _stream == null)
        {
            EmitEvent(interpreter, "error", [new SharpTSError("This socket has been ended by the other party")]);
            return false;
        }

        var chunk = args[0];
        string? encoding = null;
        ISharpTSCallable? callback = null;

        if (args.Count > 1)
        {
            if (args[1] is string enc) encoding = enc;
            else if (args[1] is ISharpTSCallable cb) callback = cb;
        }
        if (args.Count > 2 && args[2] is ISharpTSCallable cb2) callback = cb2;

        byte[] data = ChunkToBytes(chunk, encoding ?? _encoding);
        _interpreter = interpreter;

        try
        {
            _stream.Write(data, 0, data.Length);
            _bytesWritten += data.Length;
            callback?.Call(interpreter, []);
            return true;
        }
        catch (Exception ex)
        {
            EmitEvent(interpreter, "error", [new SharpTSError(ex.Message)]);
            return false;
        }
    }

    /// <summary>
    /// Ends the writable side of the socket.
    /// </summary>
    private object? End(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_ended) return this;

        // Write final chunk if provided
        if (args.Count > 0 && args[0] != null && args[0] is not ISharpTSCallable)
        {
            Write(interpreter, receiver, args);
        }

        _ended = true;

        try
        {
            _client?.Client?.Shutdown(SocketShutdown.Send);
        }
        catch
        {
            // May already be disconnected
        }

        ISharpTSCallable? callback = null;
        foreach (var arg in args)
        {
            if (arg is ISharpTSCallable cb) { callback = cb; break; }
        }
        callback?.Call(interpreter, []);

        return this;
    }

    /// <summary>
    /// Destroys the socket.
    /// </summary>
    private object? Destroy(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_destroyed) return this;

        _destroyed = true;
        _readCts?.Cancel();

        try
        {
            _stream?.Close();
            _client?.Close();
        }
        catch
        {
            // Ignore close errors
        }

        if (args.Count > 0 && args[0] != null)
        {
            EmitEvent(interpreter, "error", [args[0]]);
        }

        EmitEvent(interpreter, "close", [args.Count > 0 && args[0] != null]);
        // Only unref if reading was started (StartReading does Ref)
        if (_readingStarted)
        {
            _readingStarted = false;
            _interpreter?.Unref();
        }
        return this;
    }

    private object? SetEncoding(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count > 0 && args[0] is string enc)
            _encoding = enc.ToLowerInvariant();
        return this;
    }

    private object? SetTimeout(Interp interpreter, object? receiver, List<object?> args)
    {
        var timeout = args.Count > 0 && args[0] is double t ? (int)t : 0;
        if (_client?.Client != null)
        {
            _client.Client.ReceiveTimeout = timeout;
            _client.Client.SendTimeout = timeout;
        }
        if (args.Count > 1 && args[1] is ISharpTSCallable cb)
        {
            AddListenerDirect("timeout", cb);
        }
        return this;
    }

    private object? SetNoDelay(Interp interpreter, object? receiver, List<object?> args)
    {
        var noDelay = args.Count == 0 || (args[0] is bool b && b) || (args[0] is not bool);
        _client?.Client?.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, noDelay);
        return this;
    }

    private object? SetKeepAlive(Interp interpreter, object? receiver, List<object?> args)
    {
        var enable = args.Count > 0 && args[0] is bool b && b;
        _client?.Client?.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, enable);
        return this;
    }

    private object? Address(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_client?.Client?.LocalEndPoint is not IPEndPoint ep) return new SharpTSObject(new Dictionary<string, object?>());
        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["port"] = (double)ep.Port,
            ["family"] = ep.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
            ["address"] = ep.Address.ToString()
        });
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

    private object? Pause(Interp interpreter, object? receiver, List<object?> args)
    {
        _readCts?.Cancel();
        return this;
    }

    private object? Resume(Interp interpreter, object? receiver, List<object?> args)
    {
        StartReading(interpreter);
        return this;
    }

    private object? Pipe(Interp interpreter, object? receiver, List<object?> args)
    {
        // Minimal pipe: forward data events to writable
        if (args.Count < 1) throw new Exception("pipe() requires a destination");
        var dest = args[0];
        AddListenerDirect("data", new PipeDataListener(dest, interpreter));
        return dest;
    }

    /// <summary>
    /// Starts reading from the socket asynchronously.
    /// Called after connection is established.
    /// </summary>
    internal void StartReading(Interp interpreter)
    {
        if (_destroyed || _stream == null) return;

        _interpreter = interpreter;
        _readCts?.Cancel();
        _readCts = new CancellationTokenSource();
        var token = _readCts.Token;

        _readingStarted = true;
        interpreter.Ref();

        _ = Task.Run(async () =>
        {
            var buffer = new byte[65536];
            try
            {
                while (!token.IsCancellationRequested && _stream != null)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        // Remote end closed
                        interpreter.ScheduleTimer(0, 0, () =>
                        {
                            if (!_destroyed)
                            {
                                EmitEvent(interpreter, "end", []);
                                EmitEvent(interpreter, "close", [false]);
                            }
                            if (_readingStarted)
                            {
                                _readingStarted = false;
                                interpreter.Unref();
                            }
                        }, isInterval: false);
                        break;
                    }

                    _bytesRead += bytesRead;
                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);

                    interpreter.ScheduleTimer(0, 0, () =>
                    {
                        object chunk = _encoding is "utf8" or "utf-8"
                            ? Encoding.UTF8.GetString(data)
                            : (object)new SharpTSBuffer(data);
                        EmitEvent(interpreter, "data", [chunk]);
                    }, isInterval: false);
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && !_destroyed)
                {
                    interpreter.ScheduleTimer(0, 0, () =>
                    {
                        EmitEvent(interpreter, "error", [new SharpTSError(ex.Message)]);
                        EmitEvent(interpreter, "close", [true]);
                        if (_readingStarted)
                        {
                            _readingStarted = false;
                            interpreter.Unref();
                        }
                    }, isInterval: false);
                }
            }
        }, token);
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

    private static byte[] ChunkToBytes(object? chunk, string encoding)
    {
        return chunk switch
        {
            string s => GetEncoding(encoding).GetBytes(s),
            SharpTSBuffer buf => buf.Data,
            _ => Encoding.UTF8.GetBytes(chunk?.ToString() ?? "")
        };
    }

    private static Encoding GetEncoding(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "utf8" or "utf-8" => Encoding.UTF8,
            "ascii" => Encoding.ASCII,
            "latin1" or "binary" => Encoding.Latin1,
            "utf16le" or "ucs2" => Encoding.Unicode,
            _ => Encoding.UTF8
        };
    }

    public override string ToString() => $"Socket {{ connecting: {_connecting}, destroyed: {_destroyed} }}";

    private class PipeDataListener : ISharpTSCallable
    {
        private readonly object? _dest;
        private readonly Interp _interpreter;

        public PipeDataListener(object? dest, Interp interpreter)
        {
            _dest = dest;
            _interpreter = interpreter;
        }

        public int Arity() => 1;

        public object? Call(Interp interpreter, List<object?> arguments)
        {
            if (_dest is SharpTSWritable writable)
            {
                writable.WriteInternal(_interpreter, arguments.Count > 0 ? arguments[0] : null, "utf8");
            }
            else if (_dest is SharpTSSocket socket)
            {
                socket.Write(_interpreter, socket, arguments);
            }
            return null;
        }
    }
}
