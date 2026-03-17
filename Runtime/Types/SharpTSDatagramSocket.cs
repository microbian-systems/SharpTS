using System.Net;
using System.Net.Sockets;
using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of a Node.js dgram.Socket (UDP socket).
/// Extends SharpTSEventEmitter for event-driven patterns.
/// </summary>
public class SharpTSDatagramSocket : SharpTSEventEmitter
{
    private UdpClient? _client;
    private Interp? _interpreter;
    private readonly AddressFamily _family;
    private bool _bound;
    private bool _closed;
    private CancellationTokenSource? _receiveCts;

    public SharpTSDatagramSocket(string type = "udp4")
    {
        _family = type == "udp6" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
    }

    /// <summary>
    /// Gets a member by name for interpreter dispatch.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            "bind" => new BuiltInMethod("bind", 0, 3, Bind),
            "send" => new BuiltInMethod("send", 2, 6, Send),
            "close" => new BuiltInMethod("close", 0, 1, Close),
            "address" => new BuiltInMethod("address", 0, Address),
            "setBroadcast" => new BuiltInMethod("setBroadcast", 1, SetBroadcast),
            "setTTL" => new BuiltInMethod("setTTL", 1, SetTTL),
            "setMulticastTTL" => new BuiltInMethod("setMulticastTTL", 1, SetMulticastTTL),
            "addMembership" => new BuiltInMethod("addMembership", 1, 2, AddMembership),
            "dropMembership" => new BuiltInMethod("dropMembership", 1, 2, DropMembership),
            "ref" => new BuiltInMethod("ref", 0, Ref),
            "unref" => new BuiltInMethod("unref", 0, Unref),

            // EventEmitter methods
            _ => base.GetMember(name)
        };
    }

    /// <summary>
    /// Binds the socket to a local port and optional address.
    /// Signature: bind(port?, address?, callback?)
    ///            bind(options?, callback?)
    /// </summary>
    private object? Bind(Interp interpreter, object? receiver, List<object?> args)
    {
        _interpreter = interpreter;
        int port = 0;
        string address = _family == AddressFamily.InterNetworkV6 ? "::" : "0.0.0.0";
        ISharpTSCallable? callback = null;

        if (args.Count > 0)
        {
            if (args[0] is double p) port = (int)p;
            else if (args[0] is SharpTSObject options)
            {
                if (options.GetProperty("port") is double op) port = (int)op;
                if (options.GetProperty("address") is string oa) address = oa;
                if (args.Count > 1 && args[1] is ISharpTSCallable cb) callback = cb;
            }
            else if (args[0] is ISharpTSCallable cb0)
            {
                callback = cb0;
            }
        }
        if (args.Count > 1 && args[1] is string addr) address = addr;
        if (args.Count > 1 && args[1] is ISharpTSCallable cb1 && callback == null) callback = cb1;
        if (args.Count > 2 && args[2] is ISharpTSCallable cb2) callback = cb2;

        if (callback != null)
        {
            Once("listening", callback);
        }

        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(address), port);
            _client = new UdpClient(_family);
            _client.Client.Bind(ep);
            _bound = true;

            interpreter.Ref();

            // Start receive loop
            StartReceiving(interpreter);

            // Emit 'listening' event
            interpreter.ScheduleTimer(0, 0, () =>
            {
                EmitEvent(interpreter, "listening", []);
            }, isInterval: false);
        }
        catch (Exception ex)
        {
            interpreter.ScheduleTimer(0, 0, () =>
            {
                EmitEvent(interpreter, "error", [new SharpTSError(ex.Message)]);
            }, isInterval: false);
        }

        return this;
    }

    /// <summary>
    /// Sends a datagram.
    /// Signature: send(msg, port, address?, callback?)
    ///            send(msg, offset, length, port, address?, callback?)
    /// </summary>
    private object? Send(Interp interpreter, object? receiver, List<object?> args)
    {
        _interpreter = interpreter;

        if (_client == null)
        {
            // Auto-bind if not bound
            _client = new UdpClient(_family);
        }

        byte[] data;
        int port;
        string address = _family == AddressFamily.InterNetworkV6 ? "::1" : "127.0.0.1";
        ISharpTSCallable? callback = null;

        // Get message data
        if (args[0] is SharpTSBuffer buf)
        {
            data = buf.Data;
        }
        else if (args[0] is string str)
        {
            data = System.Text.Encoding.UTF8.GetBytes(str);
        }
        else
        {
            data = System.Text.Encoding.UTF8.GetBytes(args[0]?.ToString() ?? "");
        }

        // Parse remaining args - detect if offset/length form or direct port form
        if (args.Count >= 4 && args[1] is double && args[2] is double && args[3] is double)
        {
            // send(msg, offset, length, port, address?, callback?)
            int offset = (int)(double)args[1];
            int length = (int)(double)args[2];
            if (offset != 0 || length != data.Length)
            {
                var slice = new byte[length];
                Array.Copy(data, offset, slice, 0, length);
                data = slice;
            }
            port = (int)(double)args[3];
            if (args.Count > 4 && args[4] is string a) address = a;
            if (args.Count > 4 && args[4] is ISharpTSCallable c4) callback = c4;
            if (args.Count > 5 && args[5] is ISharpTSCallable c5) callback = c5;
        }
        else
        {
            // send(msg, port, address?, callback?)
            port = args.Count > 1 && args[1] is double p ? (int)p : 0;
            if (args.Count > 2 && args[2] is string a) address = a;
            if (args.Count > 2 && args[2] is ISharpTSCallable c2) callback = c2;
            if (args.Count > 3 && args[3] is ISharpTSCallable c3) callback = c3;
        }

        var sendData = data;
        var sendCallback = callback;
        var sendPort = port;
        var sendAddress = address;

        Task.Run(async () =>
        {
            try
            {
                var ep = new IPEndPoint(IPAddress.Parse(sendAddress), sendPort);
                await _client.SendAsync(sendData, sendData.Length, ep);
                if (sendCallback != null)
                {
                    interpreter.ScheduleTimer(0, 0, () =>
                    {
                        sendCallback.Call(interpreter, [null]);
                    }, isInterval: false);
                }
            }
            catch (Exception ex)
            {
                if (sendCallback != null)
                {
                    interpreter.ScheduleTimer(0, 0, () =>
                    {
                        sendCallback.Call(interpreter, [new SharpTSError(ex.Message)]);
                    }, isInterval: false);
                }
                else
                {
                    interpreter.ScheduleTimer(0, 0, () =>
                    {
                        EmitEvent(interpreter, "error", [new SharpTSError(ex.Message)]);
                    }, isInterval: false);
                }
            }
        });

        return null;
    }

    /// <summary>
    /// Closes the socket.
    /// </summary>
    private object? Close(Interp interpreter, object? receiver, List<object?> args)
    {
        ISharpTSCallable? callback = null;
        if (args.Count > 0 && args[0] is ISharpTSCallable cb) callback = cb;

        if (_closed) return null;
        _closed = true;

        _receiveCts?.Cancel();
        _client?.Close();
        _client?.Dispose();
        _client = null;

        if (_bound && _interpreter != null)
        {
            _interpreter.Unref();
        }

        var closeInterpreter = interpreter ?? _interpreter;
        if (closeInterpreter != null)
        {
            if (callback != null)
            {
                closeInterpreter.ScheduleTimer(0, 0, () =>
                {
                    callback.Call(closeInterpreter, []);
                }, isInterval: false);
            }

            closeInterpreter.ScheduleTimer(0, 0, () =>
            {
                EmitEvent(closeInterpreter, "close", []);
            }, isInterval: false);
        }

        return null;
    }

    /// <summary>
    /// Returns the address information for the socket.
    /// </summary>
    private object? Address(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_client?.Client?.LocalEndPoint is IPEndPoint ep)
        {
            return new SharpTSObject(new Dictionary<string, object?>
            {
                ["address"] = ep.Address.ToString(),
                ["family"] = ep.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
                ["port"] = (double)ep.Port
            });
        }
        return new SharpTSObject(new Dictionary<string, object?>());
    }

    private object? SetBroadcast(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_client != null && args.Count > 0)
        {
            _client.EnableBroadcast = args[0] is true || (args[0] is double d && d != 0);
        }
        return null;
    }

    private object? SetTTL(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_client != null && args.Count > 0 && args[0] is double ttl)
        {
            _client.Ttl = (short)ttl;
        }
        return null;
    }

    private object? SetMulticastTTL(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_client != null && args.Count > 0 && args[0] is double ttl)
        {
            _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, (int)ttl);
        }
        return null;
    }

    private object? AddMembership(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_client != null && args.Count > 0 && args[0] is string multicastAddress)
        {
            string? localAddress = args.Count > 1 ? args[1] as string : null;
            if (localAddress != null)
            {
                _client.JoinMulticastGroup(IPAddress.Parse(multicastAddress), IPAddress.Parse(localAddress));
            }
            else
            {
                _client.JoinMulticastGroup(IPAddress.Parse(multicastAddress));
            }
        }
        return null;
    }

    private object? DropMembership(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_client != null && args.Count > 0 && args[0] is string multicastAddress)
        {
            _client.DropMulticastGroup(IPAddress.Parse(multicastAddress));
        }
        return null;
    }

    private object? Ref(Interp interpreter, object? receiver, List<object?> args)
    {
        interpreter.Ref();
        return this;
    }

    private object? Unref(Interp interpreter, object? receiver, List<object?> args)
    {
        interpreter.Unref();
        return this;
    }

    /// <summary>
    /// Starts the async receive loop.
    /// </summary>
    private void StartReceiving(Interp interpreter)
    {
        if (_client == null) return;

        _receiveCts = new CancellationTokenSource();
        var token = _receiveCts.Token;
        var client = _client;

        Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested && client.Client != null)
                {
                    var result = await client.ReceiveAsync(token);
                    var msgBuffer = new SharpTSBuffer(result.Buffer);
                    var rinfo = new SharpTSObject(new Dictionary<string, object?>
                    {
                        ["address"] = result.RemoteEndPoint.Address.ToString(),
                        ["family"] = result.RemoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
                        ["port"] = (double)result.RemoteEndPoint.Port,
                        ["size"] = (double)result.Buffer.Length
                    });

                    interpreter.ScheduleTimer(0, 0, () =>
                    {
                        EmitEvent(interpreter, "message", [msgBuffer, rinfo]);
                    }, isInterval: false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (ObjectDisposedException)
            {
                // Socket was closed
            }
            catch (SocketException ex)
            {
                if (!_closed)
                {
                    interpreter.ScheduleTimer(0, 0, () =>
                    {
                        EmitEvent(interpreter, "error", [new SharpTSError(ex.Message)]);
                    }, isInterval: false);
                }
            }
        }, token);
    }

    protected internal void EmitEvent(Interp interpreter, string eventName, List<object?> args)
    {
        var emit = base.GetMember("emit") as BuiltInMethod;
        if (emit != null)
        {
            var fullArgs = new List<object?> { eventName };
            fullArgs.AddRange(args);
            emit.Bind(this).Call(interpreter, fullArgs);
        }
    }

    private void Once(string eventName, ISharpTSCallable callback)
    {
        var onceMethod = base.GetMember("once") as BuiltInMethod;
        onceMethod?.Bind(this).Call(null!, new List<object?> { eventName, callback });
    }
}
