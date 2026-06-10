using System.IO;
using System.IO.Pipes;
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
    protected internal TcpClient? _client;
    protected internal Stream? _stream;
    protected internal Interp? _interpreter;
    private bool _connecting;
    protected internal bool _destroyed;
    private bool _ended;
    private int _bytesRead;
    private int _bytesWritten;
    private CancellationTokenSource? _readCts;
    protected internal string _encoding = "utf8";
    protected internal bool _readingStarted;
    internal bool _isIpc;
    internal string? _pipePath;

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
    /// Creates a new Socket wrapping an IPC pipe stream (server-accepted IPC sockets).
    /// </summary>
    public SharpTSSocket(Stream pipeStream, string pipePath)
    {
        _stream = pipeStream ?? throw new ArgumentNullException(nameof(pipeStream));
        _isIpc = true;
        _pipePath = pipePath;
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
            "remoteAddress" => _isIpc ? (object?)null : (_client?.Client?.RemoteEndPoint is IPEndPoint rep ? rep.Address.ToString() : null),
            "remotePort" => _isIpc ? (object?)null : (_client?.Client?.RemoteEndPoint is IPEndPoint rep2 ? (double)rep2.Port : null),
            "remoteFamily" => _isIpc ? "pipe" : (_client?.Client?.RemoteEndPoint is IPEndPoint rep3 ? (rep3.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4") : null),
            "localAddress" => _isIpc ? (object?)null : (_client?.Client?.LocalEndPoint is IPEndPoint lep ? lep.Address.ToString() : null),
            "localPort" => _isIpc ? (object?)null : (_client?.Client?.LocalEndPoint is IPEndPoint lep2 ? (double)lep2.Port : null),
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
        if (_isIpc) return _stream != null ? "open" : "closed";
        if (_client?.Connected == true) return "open";
        return "closed";
    }

    /// <summary>
    /// Connects to a remote host or IPC pipe.
    /// </summary>
    private object? Connect(Interp interpreter, object? receiver, List<object?> args)
    {
        _interpreter = interpreter;

        // Detect IPC path: string first arg or options.path
        string? ipcPath = null;
        ISharpTSCallable? callback = null;

        if (args[0] is string pathArg)
        {
            ipcPath = pathArg;
            callback = args.Count > 1 ? WrapCallbackArg(args[1]) : null;
        }
        else if (args[0] is SharpTSObject options && options.GetProperty("path") is string optPath)
        {
            ipcPath = optPath;
            callback = args.Count > 1 ? WrapCallbackArg(args[1]) : null;
        }

        if (ipcPath != null)
        {
            return ConnectIpc(interpreter, ipcPath, callback);
        }

        int port;
        string host = "localhost";

        if (args[0] is SharpTSObject opts)
        {
            port = (int)(double)(opts.GetProperty("port") ?? throw new Exception("Runtime Error: port is required"));
            if (opts.GetProperty("host") is string h) host = h;
            callback = args.Count > 1 ? WrapCallbackArg(args[1]) : null;
        }
        else if (args[0] is double portNum)
        {
            port = (int)portNum;
            if (args.Count > 1 && args[1] is string h) host = h;
            if (args.Count > 1) callback = WrapCallbackArg(args[1]);
            if (args.Count > 2) callback = WrapCallbackArg(args[2]) ?? callback;
        }
        else
        {
            throw new Exception("Runtime Error: connect requires port number, path string, or options object");
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
                var error = CreateSocketError(ex, "connect", $"{capturedHost}:{capturedPort}");
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    EmitEvent(interpreter, "error", [error]);
                }, isInterval: false);
            }
        });

        return this;
    }

    /// <summary>
    /// Connects to an IPC pipe/Unix domain socket.
    /// </summary>
    private object? ConnectIpc(Interp interpreter, string path, ISharpTSCallable? callback)
    {
        if (callback != null)
        {
            AddListenerDirect("connect", callback);
        }

        _connecting = true;
        _isIpc = true;
        _pipePath = path;

        _ = Task.Run(async () =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var pipeName = ConvertToWindowsPipeName(path);
                    // Node raises ENOENT immediately for a missing pipe. NamedPipeClientStream's
                    // timed Connect cannot distinguish "missing" from "busy" — it retries
                    // CreateFile until the timeout expires — so pre-check existence and keep
                    // the 5s budget only for the exists-but-busy case it is actually for.
                    if (!WindowsPipeExists(pipeName))
                        throw new FileNotFoundException($"no such named pipe '{path}'");
                    var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipeClient.ConnectAsync(5000);
                    _stream = pipeClient;
                }
                else
                {
                    var unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await unixSocket.ConnectAsync(new UnixDomainSocketEndPoint(path));
                    _stream = new NetworkStream(unixSocket, ownsSocket: true);
                }

                _connecting = false;
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    // For IPC: start reading BEFORE emitting 'connect' so the
                    // server can write immediately (Windows InOut pipe requirement)
                    var readReady = new ManualResetEventSlim(false);
                    StartReading(interpreter, readReady);
                    readReady.Wait(5000);
                    EmitEvent(interpreter, "connect", []);
                }, isInterval: false);
            }
            catch (Exception ex)
            {
                _connecting = false;
                var error = CreateSocketError(ex, "connect", path);
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    EmitEvent(interpreter, "error", [error]);
                }, isInterval: false);
            }
        });

        return this;
    }

    /// <summary>
    /// Creates a SharpTSError with Node.js-compatible code and syscall properties.
    /// </summary>
    internal static SharpTSError CreateSocketError(Exception ex, string syscall, string? address = null)
    {
        var code = ex switch
        {
            FileNotFoundException => "ENOENT",
            DirectoryNotFoundException => "ENOENT",
            TimeoutException => "ENOENT", // Named pipe connect timeout = no server listening
            SocketException se when se.SocketErrorCode == SocketError.ConnectionRefused => "ECONNREFUSED",
            SocketException se when se.SocketErrorCode == SocketError.AddressAlreadyInUse => "EADDRINUSE",
            SocketException se when se.SocketErrorCode == SocketError.AddressNotAvailable => "EADDRNOTAVAIL",
            SocketException se when se.SocketErrorCode == SocketError.TimedOut => "ETIMEDOUT",
            IOException when ex.InnerException is SocketException inner
                && inner.SocketErrorCode == SocketError.ConnectionRefused => "ECONNREFUSED",
            _ => "ECONNREFUSED"
        };

        var msg = address != null
            ? $"{code}: {ex.Message}, {syscall} {address}"
            : $"{code}: {ex.Message}, {syscall}";

        return new SharpTSError(msg) { Code = code, Syscall = syscall };
    }

    /// <summary>
    /// Checks whether a Windows named pipe currently exists. Enumerating the pipe
    /// directory is the safe probe — CreateFile-based checks (File.Exists) can
    /// consume a pipe instance or interfere with WaitNamedPipe semantics.
    /// Returns true on enumeration failure so the connect timeout still governs.
    /// </summary>
    internal static bool WindowsPipeExists(string pipeName)
    {
        try
        {
            var fullPath = @"\\.\pipe\" + pipeName;
            foreach (var entry in Directory.EnumerateFiles(@"\\.\pipe\"))
            {
                if (string.Equals(entry, fullPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Converts a path to a Windows named pipe name.
    /// If already in \\.\pipe\name format, extracts the pipe name.
    /// Otherwise uses the filename portion of the path.
    /// </summary>
    internal static string ConvertToWindowsPipeName(string path)
    {
        if (path.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
            return path.Substring(@"\\.\pipe\".Length);
        // Use just the filename
        return Path.GetFileName(path);
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

        if (_isIpc)
        {
            // IPC pipes (Windows InOut) deadlock when sync Write blocks the event loop
            // because the reader may not have started yet. Write asynchronously.
            _ = Task.Run(() =>
            {
                try
                {
                    _stream!.Write(data, 0, data.Length);
                    _bytesWritten += data.Length;
                    if (callback != null)
                    {
                        interpreter.ScheduleTimer(0, 0, () => callback.Call(interpreter, []), false);
                    }
                }
                catch (Exception ex)
                {
                    interpreter.ScheduleTimer(0, 0, () =>
                    {
                        EmitEvent(interpreter, "error", [new SharpTSError(ex.Message)]);
                    }, false);
                }
            });
            return true;
        }

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

        if (_isIpc)
        {
            // Named pipes don't support half-close; close the stream entirely
            try
            {
                _stream?.Close();
                _stream = null;
            }
            catch
            {
                // May already be closed
            }
        }
        else
        {
            try
            {
                _client?.Client?.Shutdown(SocketShutdown.Send);
            }
            catch
            {
                // May already be disconnected
            }
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
        if (_isIpc) return this; // No-op for IPC sockets
        var noDelay = args.Count == 0 || (args[0] is bool b && b) || (args[0] is not bool);
        _client?.Client?.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, noDelay);
        return this;
    }

    private object? SetKeepAlive(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_isIpc) return this; // No-op for IPC sockets
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
    /// For IPC sockets, if readReady is provided, it is signaled right before
    /// the first ReadAsync call to indicate that a reader is pending (required
    /// because Windows InOut pipes block writes until a reader is active).
    /// </summary>
    internal virtual void StartReading(Interp interpreter, ManualResetEventSlim? readReady = null)
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
            // Signal that we're about to start reading (IPC needs this)
            readReady?.Set();
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

    protected internal static byte[] ChunkToBytes(object? chunk, string encoding)
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

    /// <summary>
    /// Wraps a callback argument that may be a compiled-mode $TSFunction into ISharpTSCallable.
    /// Returns null if the argument is not callable (e.g. a string or number).
    /// </summary>
    private static ISharpTSCallable? WrapCallbackArg(object? arg)
    {
        if (arg == null || arg is string || arg is double || arg is bool) return null;
        if (arg is ISharpTSCallable callable) return callable;
        return TSFunctionCallableAdapter.WrapCallback(arg);
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
