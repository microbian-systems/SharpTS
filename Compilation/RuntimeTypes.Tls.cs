using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SharpTS.Compilation;

public static partial class RuntimeTypes
{
    /// <summary>
    /// Performs a blocking TLS server accept + handshake.
    /// Called from emitted $TlsServer._TlsAcceptWorker via late-binding.
    /// Returns object[] { authorized(bool), encrypted(bool), alpnProtocol(string) } on success,
    /// or null on failure.
    /// </summary>
    public static object?[]? TlsAcceptAndHandshake(TcpListener listener, string certPem, string keyPem, string[]? alpnProtocols)
    {
        var tcpClient = listener.AcceptTcpClient();

        try
        {
            var sslStream = new SslStream(tcpClient.GetStream(), false);

            var cert = X509Certificate2.CreateFromPem(certPem, keyPem);
            cert = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);

            var authOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            };

            if (alpnProtocols != null)
            {
                authOptions.ApplicationProtocols = alpnProtocols
                    .Select(s => new SslApplicationProtocol(s))
                    .ToList();
            }

            sslStream.AuthenticateAsServer(authOptions);

            var alpnResult = sslStream.NegotiatedApplicationProtocol.ToString();
            if (string.IsNullOrEmpty(alpnResult)) alpnResult = null;

            return [
                sslStream.IsAuthenticated,  // authorized
                true,                        // encrypted
                alpnResult                   // alpnProtocol
            ];
        }
        catch
        {
            try { tcpClient.Close(); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Performs a blocking TLS client connect + handshake.
    /// Called from emitted $TlsConnectClosure.Connect via late-binding.
    /// Returns object[] { authorized(bool), encrypted(bool), alpnProtocol(string) } on success,
    /// or null on failure.
    /// </summary>
    public static object?[]? TlsConnectAndHandshake(int port, string host, bool rejectUnauthorized, string[]? alpnProtocols)
    {
        try
        {
            var tcpClient = new TcpClient();
            tcpClient.Connect(host, port);

            var sslStream = rejectUnauthorized
                ? new SslStream(tcpClient.GetStream(), false)
                : new SslStream(tcpClient.GetStream(), false, (_, _, _, _) => true);

            var authOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            };

            if (alpnProtocols != null)
            {
                authOptions.ApplicationProtocols = alpnProtocols
                    .Select(s => new SslApplicationProtocol(s))
                    .ToList();
            }

            sslStream.AuthenticateAsClient(authOptions);

            var alpnResult = sslStream.NegotiatedApplicationProtocol.ToString();
            if (string.IsNullOrEmpty(alpnResult)) alpnResult = null;

            return [
                sslStream.IsAuthenticated,  // authorized
                true,                        // encrypted
                alpnResult                   // alpnProtocol
            ];
        }
        catch
        {
            return null;
        }
    }
}
