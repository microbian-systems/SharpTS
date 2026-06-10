using System.Net;
using System.Net.Sockets;
using SharpTS.Runtime.BuiltIns.Modules;
using Xunit;

namespace SharpTS.Tests.RuntimeTests;

/// <summary>
/// Tests DnsWireProtocol retry behavior against a local fake DNS server.
/// SERVFAIL/REFUSED responses are transient resolver conditions and must be
/// retried like socket errors before surfacing an error.
/// </summary>
public class DnsWireProtocolRetryTests
{
    private const byte Servfail = 2;
    private const byte Refused = 5;

    [Theory]
    [InlineData(Servfail)]
    [InlineData(Refused)]
    public void Query_TransientErrorThenSuccess_Retries(byte rcode)
    {
        using var server = new FakeDnsServer(rcode, errorCount: 1);

        var result = DnsWireProtocol.Query("example.com", DnsWireProtocol.TypeTXT, server.Address);

        var records = Assert.IsType<List<object?>>(result);
        var record = Assert.IsType<List<object?>>(Assert.Single(records));
        Assert.Equal("hello", Assert.Single(record));
        Assert.Equal(2, server.QueryCount);
    }

    [Fact]
    public void Query_PersistentServfail_ThrowsEservfailAfterRetries()
    {
        using var server = new FakeDnsServer(Servfail, errorCount: int.MaxValue);

        var ex = Assert.Throws<Exception>(() =>
            DnsWireProtocol.Query("example.com", DnsWireProtocol.TypeTXT, server.Address));

        Assert.Contains("ESERVFAIL", ex.Message);
        Assert.Equal(3, server.QueryCount); // initial attempt + MaxRetries (2)
    }

    /// <summary>
    /// Minimal loopback DNS server: answers the first <c>errorCount</c> queries
    /// with the given error rcode, then with a one-record TXT answer ("hello").
    /// </summary>
    private sealed class FakeDnsServer : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly byte _errorRcode;
        private readonly int _errorCount;
        private int _queryCount;

        public FakeDnsServer(byte errorRcode, int errorCount)
        {
            _errorRcode = errorRcode;
            _errorCount = errorCount;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            Address = $"127.0.0.1:{port}";
            Task.Run(ServeLoop);
        }

        public string Address { get; }
        public int QueryCount => Volatile.Read(ref _queryCount);

        // Async receive only: on macOS, Dispose does not unblock a thread in a
        // synchronous Receive (see the SendReceive timeout comment in
        // DnsWireProtocol.cs) — a sync loop here deadlocks the testhost there.
        // Closing the socket does complete a pending async receive on all OSes.
        private async Task ServeLoop()
        {
            try
            {
                while (true)
                {
                    var query = await _udp.ReceiveAsync().ConfigureAwait(false);
                    var count = Interlocked.Increment(ref _queryCount);
                    var response = count <= _errorCount
                        ? BuildErrorResponse(query.Buffer, _errorRcode)
                        : BuildTxtResponse(query.Buffer);
                    await _udp.SendAsync(response, response.Length, query.RemoteEndPoint)
                        .ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private static byte[] BuildErrorResponse(byte[] request, byte rcode)
        {
            var response = (byte[])request.Clone(); // header + question echo
            response[2] = 0x81; // QR=1, RD=1
            response[3] = rcode;
            return response;
        }

        private static byte[] BuildTxtResponse(byte[] request)
        {
            var response = new List<byte>(request); // echo header + question
            response[2] = 0x81; // QR=1, RD=1
            response[3] = 0x80; // RA=1, rcode=0
            response[7] = 1;    // ANCOUNT = 1

            // Answer NAME: uncompressed copy of the question name
            var nameEnd = 12;
            while (request[nameEnd] != 0)
                nameEnd += request[nameEnd] + 1;
            for (var i = 12; i <= nameEnd; i++)
                response.Add(request[i]);

            response.AddRange(new byte[] { 0x00, 0x10 }); // TYPE = TXT
            response.AddRange(new byte[] { 0x00, 0x01 }); // CLASS = IN
            response.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x3C }); // TTL = 60
            response.AddRange(new byte[] { 0x00, 0x06 }); // RDLENGTH
            response.Add(0x05); // TXT string length
            response.AddRange("hello"u8.ToArray());
            return response.ToArray();
        }

        public void Dispose() => _udp.Dispose();
    }
}
