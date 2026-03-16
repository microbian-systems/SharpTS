using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for the net module (TCP sockets).
/// </summary>
public class NetModuleTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NetModuleImport(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = """
                import * as net from 'net';
                console.log(typeof net.createServer);
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("function\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NetModuleNodePrefix(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = """
                import * as net from 'node:net';
                console.log(typeof net.createServer);
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("function\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NetModuleExports(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = """
                import * as net from 'net';
                console.log(typeof net.createServer !== 'undefined');
                console.log(typeof net.createConnection !== 'undefined');
                console.log(typeof net.connect !== 'undefined');
                console.log(typeof net.isIP !== 'undefined');
                console.log(typeof net.isIPv4 !== 'undefined');
                console.log(typeof net.isIPv6 !== 'undefined');
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\ntrue\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NetIsIPv4(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = """
                import { isIP, isIPv4, isIPv6 } from 'net';
                console.log(isIP('127.0.0.1'));
                console.log(isIP('::1'));
                console.log(isIP('not-an-ip'));
                console.log(isIPv4('127.0.0.1'));
                console.log(isIPv4('::1'));
                console.log(isIPv6('::1'));
                console.log(isIPv6('127.0.0.1'));
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("4\n6\n0\ntrue\nfalse\ntrue\nfalse\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void NetCreateServerReturnsServer(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = """
                import * as net from 'net';
                const server = net.createServer();
                console.log(server.listening);
                console.log(typeof server.listen);
                console.log(typeof server.close);
                console.log(typeof server.address);
                console.log(typeof server.on);
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("false\nfunction\nfunction\nfunction\nfunction\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void NetServerListenAndClose(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = """
                import * as net from 'net';
                const server = net.createServer();
                server.listen(0, () => {
                    console.log('listening');
                    console.log(server.listening);
                    const addr = server.address();
                    console.log(typeof addr.port === 'number');
                    server.close(() => {
                        console.log('closed');
                        console.log(server.listening);
                    });
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("listening\ntrue\ntrue\nclosed\nfalse\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void NetServerListeningEvent(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = """
                import * as net from 'net';
                const server = net.createServer();
                server.on('listening', () => {
                    console.log('listening event fired');
                    server.close();
                });
                server.on('close', () => {
                    console.log('close event fired');
                });
                server.listen(0);
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("listening event fired", output);
        Assert.Contains("close event fired", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void NetServerConnectionEvent(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = """
                import * as net from 'net';
                const server = net.createServer((socket) => {
                    console.log('connection received');
                    console.log(typeof socket.remotePort === 'number');
                    socket.destroy();
                    server.close();
                });
                server.listen(0, () => {
                    const addr = server.address();
                    const client = net.createConnection({ port: addr.port, host: '127.0.0.1' });
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("connection received", output);
        Assert.Contains("true", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void NetSocketWriteAndReceive(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = """
                import * as net from 'net';
                const server = net.createServer((socket) => {
                    socket.write('hello');
                    socket.end();
                });
                server.listen(0, () => {
                    const addr = server.address();
                    const client = net.createConnection({ port: addr.port, host: '127.0.0.1' });
                    client.setEncoding('utf8');
                    let data = '';
                    client.on('data', (chunk: string) => {
                        data += chunk;
                    });
                    client.on('end', () => {
                        console.log('received: ' + data);
                        client.destroy();
                        server.close();
                    });
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("received: hello", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void NetSocketProperties(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = """
                import * as net from 'net';
                const server = net.createServer((socket) => {
                    console.log(typeof socket.remoteAddress);
                    console.log(typeof socket.remotePort);
                    console.log(typeof socket.localPort);
                    console.log(socket.destroyed);
                    socket.destroy();
                    server.close();
                });
                server.listen(0, () => {
                    const addr = server.address();
                    const client = net.createConnection({ port: addr.port, host: '127.0.0.1' });
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("string", output);
        Assert.Contains("number", output);
        Assert.Contains("false", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void NetServerGetConnections(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = """
                import * as net from 'net';
                const server = net.createServer((socket) => {
                    server.getConnections((err: any, count: number) => {
                        console.log('connections: ' + count);
                    });
                    socket.destroy();
                    server.close();
                });
                server.listen(0, () => {
                    const addr = server.address();
                    const client = net.createConnection({ port: addr.port, host: '127.0.0.1' });
                });
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Contains("connections: 1", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void HttpsModuleImport(ExecutionMode mode)
    {
        // Test that https module can be imported (delegates to http)
        var files = new Dictionary<string, string>
        {
            ["./main.ts"] = """
                import * as https from 'https';
                console.log(typeof https.createServer);
                console.log(typeof https.request);
                """
        };
        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("function\nfunction\n", output);
    }
}
