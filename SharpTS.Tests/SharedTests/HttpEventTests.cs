using System.Net;
using System.Net.Sockets;
using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for HTTP module event-driven features.
/// IncomingMessage (request) now extends Readable stream.
/// ServerResponse (response) now extends Writable stream.
/// </summary>
public class HttpEventTests
{
    /// <summary>
    /// Gets a random available TCP port.
    /// </summary>
    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void HttpServerRequestEvent(ExecutionMode mode)
    {
        var port = GetAvailablePort();
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = $$"""
                import * as http from 'http';
                const server = http.createServer((req, res) => {
                    console.log(typeof req.method);
                    console.log(typeof req.url);
                    console.log(typeof req.headers);
                    console.log(typeof req.on);
                    console.log(typeof res.writeHead);
                    console.log(typeof res.write);
                    console.log(typeof res.end);
                    console.log(typeof res.setHeader);
                    console.log(typeof res.on);
                    res.end();
                    server.close();
                });
                server.listen({{port}}, () => {
                    const req = http.get('http://localhost:{{port}}/');
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("string", output);
        Assert.Contains("function", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void HttpServerResponseWriteHead(ExecutionMode mode)
    {
        var port = GetAvailablePort();
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = $$"""
                import * as http from 'http';
                const server = http.createServer((req, res) => {
                    res.writeHead(200, { 'Content-Type': 'text/plain' });
                    res.end('OK');
                    server.close();
                });
                server.listen({{port}}, () => {
                    console.log('server started');
                    const req = http.get('http://localhost:{{port}}/');
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("server started", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void HttpServerSetHeader(ExecutionMode mode)
    {
        var port = GetAvailablePort();
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = $$"""
                import * as http from 'http';
                const server = http.createServer((req, res) => {
                    res.setHeader('X-Custom', 'test-value');
                    console.log(res.hasHeader('X-Custom'));
                    console.log(res.getHeader('X-Custom'));
                    const names = res.getHeaderNames();
                    console.log(names.length > 0);
                    res.removeHeader('X-Custom');
                    console.log(res.hasHeader('X-Custom'));
                    res.end('done');
                    server.close();
                });
                server.listen({{port}}, () => {
                    http.get('http://localhost:{{port}}/');
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("true", output);
        Assert.Contains("test-value", output);
        Assert.Contains("false", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void HttpServerResponseStatusCode(ExecutionMode mode)
    {
        var port = GetAvailablePort();
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = $$"""
                import * as http from 'http';
                const server = http.createServer((req, res) => {
                    res.statusCode = 201;
                    console.log(res.statusCode);
                    console.log(res.headersSent);
                    res.end('created');
                    console.log(res.finished);
                    server.close();
                });
                server.listen({{port}}, () => {
                    http.get('http://localhost:{{port}}/');
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("201", output);
        Assert.Contains("false", output);
        Assert.Contains("true", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void HttpRequestDataEvent(ExecutionMode mode)
    {
        var port = GetAvailablePort();
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = $$"""
                import * as http from 'http';
                const server = http.createServer((req, res) => {
                    console.log('method: ' + req.method);
                    console.log('url: ' + req.url);
                    req.on('end', () => {
                        console.log('request ended');
                        res.end('ok');
                        server.close();
                    });
                });
                server.listen({{port}}, () => {
                    http.get('http://localhost:{{port}}/test');
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("method: GET", output);
        Assert.Contains("url: /test", output);
        Assert.Contains("request ended", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void HttpRequestHeaders(ExecutionMode mode)
    {
        var port = GetAvailablePort();
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = $$"""
                import * as http from 'http';
                const server = http.createServer((req, res) => {
                    const headers = req.headers;
                    console.log(typeof headers === 'object');
                    console.log(typeof headers.host === 'string');
                    res.end('ok');
                    server.close();
                });
                server.listen({{port}}, () => {
                    http.get('http://localhost:{{port}}/');
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("true", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void HttpServerListeningAndCloseEvents(ExecutionMode mode)
    {
        var port = GetAvailablePort();
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = $$"""
                import * as http from 'http';
                const server = http.createServer();
                server.on('listening', () => {
                    console.log('listening event');
                });
                server.on('close', () => {
                    console.log('close event');
                });
                server.listen({{port}}, () => {
                    console.log('callback');
                    server.close();
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("callback", output);
        Assert.Contains("listening event", output);
        Assert.Contains("close event", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void HttpServerRequestEventEmitter(ExecutionMode mode)
    {
        var port = GetAvailablePort();
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = $$"""
                import * as http from 'http';
                const server = http.createServer();
                server.on('request', (req, res) => {
                    console.log('request event: ' + req.method + ' ' + req.url);
                    res.end('handled');
                    server.close();
                });
                server.listen({{port}}, () => {
                    http.get('http://localhost:{{port}}/test-path');
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("request event: GET /test-path", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void HttpResponseWriteMultiple(ExecutionMode mode)
    {
        var port = GetAvailablePort();
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = $$"""
                import * as http from 'http';
                const server = http.createServer((req, res) => {
                    res.writeHead(200, { 'Content-Type': 'text/plain' });
                    res.write('Hello ');
                    res.write('World');
                    res.end('!');
                    console.log('response sent');
                    server.close();
                });
                server.listen({{port}}, () => {
                    http.get('http://localhost:{{port}}/');
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("response sent", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void HttpRequestRawHeaders(ExecutionMode mode)
    {
        var port = GetAvailablePort();
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = $$"""
                import * as http from 'http';
                const server = http.createServer((req, res) => {
                    const raw = req.rawHeaders;
                    console.log(Array.isArray(raw));
                    console.log(raw.length > 0);
                    res.end();
                    server.close();
                });
                server.listen({{port}}, () => {
                    http.get('http://localhost:{{port}}/');
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("true", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void HttpResponseWriteHeadStatusMessage(ExecutionMode mode)
    {
        var port = GetAvailablePort();
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = $$"""
                import * as http from 'http';
                const server = http.createServer((req, res) => {
                    res.writeHead(404, 'Not Found', { 'Content-Type': 'text/plain' });
                    res.end('not found');
                    console.log('sent 404');
                    server.close();
                });
                server.listen({{port}}, () => {
                    http.get('http://localhost:{{port}}/missing');
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("sent 404", output);
    }
}
