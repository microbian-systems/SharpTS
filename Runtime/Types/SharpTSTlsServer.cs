using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using SharpTS.Runtime.BuiltIns;
using SharpTS.TypeSystem;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of a Node.js tls.Server.
/// Extends SharpTSEventEmitter for full event handling support.
/// Events: 'secureConnection', 'tlsClientError', 'listening', 'close', 'error'
/// </summary>
public class SharpTSTlsServer : SharpTSEventEmitter, ITypeCategorized, IDisposable
{
    /// <inheritdoc />
    public override TypeCategory RuntimeCategory => TypeCategory.Record;

    private TcpListener? _listener;
    private bool _isListening;
    private Interp? _interpreter;
    private CancellationTokenSource? _cts;
    private ISharpTSCallable? _connectionListener;
    private int _port;
    private string _host = "0.0.0.0";
    private readonly List<SharpTSTlsSocket> _connections = [];
    private int _maxConnections = int.MaxValue;
    private X509Certificate2? _certificate;
    private bool _requestCert;
    private bool _rejectUnauthorized;
    private List<SslApplicationProtocol>? _alpnProtocols;
    private ISharpTSCallable? _sniCallback;

    /// <summary>
    /// Creates a new TLS server with certificate options and an optional connection listener.
    /// </summary>
    public SharpTSTlsServer(SharpTSObject? options = null, ISharpTSCallable? connectionListener = null)
    {
        _connectionListener = connectionListener;

        if (options != null)
        {
            // Parse certificate from PEM strings
            var certPem = options.GetProperty("cert") as string;
            var keyPem = options.GetProperty("key") as string;

            if (certPem != null && keyPem != null)
            {
                _certificate = X509Certificate2.CreateFromPem(certPem, keyPem);
                // On Windows, we need to export/reimport for SslStream compatibility
                _certificate = X509CertificateLoader.LoadPkcs12(_certificate.Export(X509ContentType.Pfx), null);
            }

            if (options.GetProperty("requestCert") is bool reqCert)
                _requestCert = reqCert;
            if (options.GetProperty("rejectUnauthorized") is bool reject)
                _rejectUnauthorized = reject;

            // Parse ALPNProtocols
            if (options.GetProperty("ALPNProtocols") is SharpTSArray alpnArray)
            {
                _alpnProtocols = alpnArray.Elements
                    .OfType<string>()
                    .Select(s => new SslApplicationProtocol(s))
                    .ToList();
            }

            // Parse SNICallback
            if (options.GetProperty("SNICallback") is ISharpTSCallable sniCb)
                _sniCallback = sniCb;
        }
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

    private object? Listen(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_isListening)
            throw new Exception("Runtime Error: Server is already listening");

        if (_certificate == null)
            throw new Exception("Runtime Error: TLS server requires key and cert options");

        _interpreter = interpreter;

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
            if (argIdx < args.Count && args[argIdx] is double)
                argIdx++;
            if (argIdx < args.Count && args[argIdx] is ISharpTSCallable cb)
                callback = cb;
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

        if (_port == 0)
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _isListening = true;
        _cts = new CancellationTokenSource();

        interpreter.Ref();

        if (callback != null)
            callback.Call(interpreter, []);
        EmitEvent(interpreter, "listening", []);

        StartAccepting(interpreter);

        return this;
    }

    private void StartAccepting(Interp interpreter)
    {
        var token = _cts!.Token;
        var cert = _certificate!;

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

                    // Perform TLS handshake in the background
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var sslStream = new SslStream(tcpClient.GetStream(), false);

                            var authOptions = new SslServerAuthenticationOptions
                            {
                                ServerCertificate = cert,
                                ClientCertificateRequired = _requestCert,
                                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            };

                            if (_alpnProtocols != null)
                                authOptions.ApplicationProtocols = _alpnProtocols;

                            if (_sniCallback != null)
                            {
                                var sniCb = _sniCallback;
                                var interp = interpreter;
                                authOptions.ServerCertificateSelectionCallback = (sender, hostName) =>
                                {
                                    try
                                    {
                                        var result = sniCb.Call(interp, [hostName]);
                                        if (result is SharpTSObject ctx)
                                        {
                                            var ctxCert = ctx.GetProperty("cert") as string;
                                            var ctxKey = ctx.GetProperty("key") as string;
                                            if (ctxCert != null && ctxKey != null)
                                            {
                                                var newCert = X509Certificate2.CreateFromPem(ctxCert, ctxKey);
                                                return X509CertificateLoader.LoadPkcs12(newCert.Export(X509ContentType.Pfx), null);
                                            }
                                        }
                                    }
                                    catch { }
                                    return cert;
                                };
                            }

                            await sslStream.AuthenticateAsServerAsync(authOptions);

                            interpreter.ScheduleTimer(0, 0, () =>
                            {
                                var tlsSocket = new SharpTSTlsSocket(tcpClient, sslStream);
                                _connections.Add(tlsSocket);

                                _connectionListener?.Call(interpreter, [tlsSocket]);
                                EmitEvent(interpreter, "secureConnection", [tlsSocket]);

                                tlsSocket.StartReading(interpreter);
                            }, isInterval: false);
                        }
                        catch (Exception ex)
                        {
                            interpreter.ScheduleTimer(0, 0, () =>
                            {
                                EmitEvent(interpreter, "tlsClientError", [new SharpTSError(ex.Message)]);
                            }, isInterval: false);

                            try { tcpClient.Close(); } catch { }
                        }
                    }, token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
            }
        }, token);
    }

    private object? Close(Interp interpreter, object? receiver, List<object?> args)
    {
        if (!_isListening)
            return this;

        _cts?.Cancel();

        try { _listener?.Stop(); } catch { }

        _isListening = false;
        _interpreter?.Unref();

        ISharpTSCallable? callback = args.Count > 0 ? args[0] as ISharpTSCallable : null;
        callback?.Call(interpreter, []);

        EmitEvent(interpreter, "close", []);

        return this;
    }

    private object? GetAddress(Interp interpreter, object? receiver, List<object?> args)
    {
        if (!_isListening || _listener == null) return null;

        var ep = (IPEndPoint)_listener.LocalEndpoint;
        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["address"] = ep.Address.ToString(),
            ["family"] = ep.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
            ["port"] = (double)ep.Port
        });
    }

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

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _cts?.Dispose();
        _connections.Clear();
        _certificate?.Dispose();
    }

    public override string ToString() => $"TLSServer {{ listening: {Listening} }}";
}
