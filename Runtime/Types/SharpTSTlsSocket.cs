using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of a Node.js tls.TLSSocket.
/// Extends SharpTSSocket, wrapping the underlying stream with SslStream.
/// </summary>
public class SharpTSTlsSocket : SharpTSSocket
{
    private SslStream? _sslStream;
    private bool _authorized;
    private string? _alpnProtocol;
    private X509Certificate2? _peerCertificate;
    private SslProtocols _negotiatedProtocol;
    private string? _servername;
    private bool _rejectUnauthorized;

    /// <summary>
    /// Creates a new unconnected TLS socket (client-side).
    /// </summary>
    public SharpTSTlsSocket()
    {
    }

    /// <summary>
    /// Creates a TLS socket wrapping an existing TCP client with an already-negotiated SslStream (server-side).
    /// </summary>
    public SharpTSTlsSocket(System.Net.Sockets.TcpClient client, SslStream sslStream)
        : base(client)
    {
        _sslStream = sslStream;
        _stream = sslStream; // Replace the NetworkStream with SslStream
        _authorized = sslStream.IsAuthenticated && sslStream.IsMutuallyAuthenticated || sslStream.IsAuthenticated;
        _negotiatedProtocol = sslStream.SslProtocol;
        _peerCertificate = sslStream.RemoteCertificate as X509Certificate2;
        _alpnProtocol = sslStream.NegotiatedApplicationProtocol.ToString();
        if (string.IsNullOrEmpty(_alpnProtocol)) _alpnProtocol = null;
    }

    /// <summary>
    /// Gets a member by name for interpreter dispatch.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            // TLS-specific properties
            "authorized" => _authorized,
            "encrypted" => _sslStream != null,
            "alpnProtocol" => _alpnProtocol,

            // TLS-specific methods
            "getCipher" => new BuiltInMethod("getCipher", 0, GetCipher),
            "getPeerCertificate" => new BuiltInMethod("getPeerCertificate", 0, 1, GetPeerCertificate),
            "getProtocol" => new BuiltInMethod("getProtocol", 0, GetProtocol),
            "renegotiate" => new BuiltInMethod("renegotiate", 0, 2, Renegotiate),

            // Fall through to base Socket members
            _ => base.GetMember(name)
        };
    }

    /// <summary>
    /// Connects to a remote host with TLS.
    /// </summary>
    internal void ConnectTls(Interp interpreter, int port, string host, SharpTSObject? options, ISharpTSCallable? callback)
    {
        _interpreter = interpreter;
        _servername = options?.GetProperty("servername") as string ?? host;

        if (options?.GetProperty("rejectUnauthorized") is bool reject)
            _rejectUnauthorized = reject;
        else
            _rejectUnauthorized = true; // Default: reject unauthorized

        if (callback != null)
            AddListenerDirect("secureConnect", callback);

        _client = new System.Net.Sockets.TcpClient();

        // Keep event loop alive during async TLS handshake
        interpreter.Ref();

        var capturedHost = host;
        var capturedPort = port;
        var capturedServername = _servername;
        var capturedReject = _rejectUnauthorized;

        _ = Task.Run(async () =>
        {
            try
            {
                await _client.ConnectAsync(capturedHost, capturedPort);
                var networkStream = _client.GetStream();

                _sslStream = new SslStream(networkStream, false,
                    capturedReject ? null : (_, _, _, _) => true);

                var sslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = capturedServername,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                };

                await _sslStream.AuthenticateAsClientAsync(sslOptions);

                _stream = _sslStream;
                _authorized = _sslStream.IsAuthenticated;
                _negotiatedProtocol = _sslStream.SslProtocol;
                _peerCertificate = _sslStream.RemoteCertificate as X509Certificate2;
                _alpnProtocol = _sslStream.NegotiatedApplicationProtocol.ToString();
                if (string.IsNullOrEmpty(_alpnProtocol)) _alpnProtocol = null;

                interpreter.ScheduleTimer(0, 0, () =>
                {
                    // Unref the handshake ref; StartReading will add its own
                    interpreter.Unref();
                    EmitEvent(interpreter, "secureConnect", []);
                    StartReading(interpreter);
                }, isInterval: false);
            }
            catch (AuthenticationException ex)
            {
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    interpreter.Unref();
                    EmitEvent(interpreter, "error", [new SharpTSError(ex.Message)]);
                }, isInterval: false);
            }
            catch (Exception ex)
            {
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    interpreter.Unref();
                    EmitEvent(interpreter, "error", [new SharpTSError(ex.Message)]);
                }, isInterval: false);
            }
        });
    }

    private object? GetCipher(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_sslStream == null)
            return null;

        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["name"] = _sslStream.NegotiatedCipherSuite.ToString(),
            ["standardName"] = _sslStream.NegotiatedCipherSuite.ToString(),
            ["version"] = GetProtocolString(_sslStream.SslProtocol)
        });
    }

    private object? GetPeerCertificate(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_peerCertificate == null)
            return new SharpTSObject(new Dictionary<string, object?>());

        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["subject"] = _peerCertificate.Subject,
            ["issuer"] = _peerCertificate.Issuer,
            ["valid_from"] = _peerCertificate.NotBefore.ToString("R"),
            ["valid_to"] = _peerCertificate.NotAfter.ToString("R"),
            ["serialNumber"] = _peerCertificate.SerialNumber
        });
    }

    private object? GetProtocol(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_sslStream == null) return null;
        return GetProtocolString(_sslStream.SslProtocol);
    }

    private object? Renegotiate(Interp interpreter, object? receiver, List<object?> args)
    {
        // Node.js renegotiate() - not widely used, return this for chaining
        return this;
    }

    private static string? GetProtocolString(SslProtocols protocol)
    {
        return protocol switch
        {
            SslProtocols.Tls12 => "TLSv1.2",
            SslProtocols.Tls13 => "TLSv1.3",
#pragma warning disable SYSLIB0039 // Obsolete TLS versions - needed for protocol string mapping
            SslProtocols.Tls11 => "TLSv1.1",
            SslProtocols.Tls => "TLSv1",
#pragma warning restore SYSLIB0039
            _ => protocol.ToString()
        };
    }

    public override string ToString() => $"TLSSocket {{ encrypted: {_sslStream != null}, authorized: {_authorized} }}";
}
