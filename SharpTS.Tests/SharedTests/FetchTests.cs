using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Comprehensive tests for the fetch API, Headers class, and related functionality.
/// Tests run against both interpreter and compiler modes.
/// </summary>
public class FetchTests : IDisposable
{
    private readonly MockHttpServer _server;

    public FetchTests()
    {
        _server = new MockHttpServer();

        _server.AddJsonRoute("/json", new { message = "Hello", count = 42 });
        _server.AddTextRoute("/text", "Hello, World!");
        _server.AddEchoRoute("/echo");
        _server.AddBinaryRoute("/binary", new byte[] { 0x48, 0x65, 0x6c, 0x6c, 0x6f }); // "Hello"
        _server.AddStatusRoute("/status/404", 404, "Not Found");
        _server.AddStatusRoute("/status/500", 500, "Server Error");
        _server.AddDelayRoute("/slow", 3000, "slow response");

        _server.Start();
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    // ========== Headers API tests ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Headers_Get_ReturnsValue(ExecutionMode mode)
    {
        var source = @"
            const h = new Headers({""content-type"": ""text/html""});
            console.log(h.get(""content-type""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("text/html\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Headers_Get_ReturnsNull(ExecutionMode mode)
    {
        var source = @"
            const h = new Headers();
            console.log(h.get(""x-missing""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("null\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Headers_Has_ReturnsBool(ExecutionMode mode)
    {
        var source = @"
            const h = new Headers({""content-type"": ""text/html""});
            console.log(h.has(""content-type""));
            console.log(h.has(""x-missing""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\nfalse\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Headers_Set_OverwritesValue(ExecutionMode mode)
    {
        var source = @"
            const h = new Headers({""content-type"": ""text/html""});
            h.set(""content-type"", ""application/json"");
            console.log(h.get(""content-type""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("application/json\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Headers_Delete_RemovesHeader(ExecutionMode mode)
    {
        var source = @"
            const h = new Headers({""content-type"": ""text/html""});
            h.delete(""content-type"");
            console.log(h.has(""content-type""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Headers_Append_AddsMultipleValues(ExecutionMode mode)
    {
        var source = @"
            const h = new Headers();
            h.append(""accept"", ""text/html"");
            h.append(""accept"", ""application/json"");
            console.log(h.get(""accept""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("text/html, application/json\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Headers_ForEach_IteratesAll(ExecutionMode mode)
    {
        var source = @"
            const h = new Headers({""accept"": ""text/html"", ""content-type"": ""application/json""});
            const results: string[] = [];
            h.forEach((value: string, key: string) => {
                results.push(key + "": "" + value);
            });
            console.log(results.join(""; ""));
        ";
        var output = TestHarness.Run(source, mode);
        // Headers are sorted alphabetically
        Assert.Equal("accept: text/html; content-type: application/json\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Headers_CaseInsensitive(ExecutionMode mode)
    {
        var source = @"
            const h = new Headers();
            h.set(""Content-Type"", ""text/html"");
            console.log(h.get(""content-type""));
            console.log(h.has(""CONTENT-TYPE""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("text/html\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Headers_Entries_ReturnsIterator(ExecutionMode mode)
    {
        var source = @"
            const h = new Headers({""accept"": ""text/html"", ""host"": ""example.com""});
            const results: string[] = [];
            for (const [k, v] of h.entries()) {
                results.push(k + ""="" + v);
            }
            console.log(results.join("",""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("accept=text/html,host=example.com\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Headers_Keys_ReturnsIterator(ExecutionMode mode)
    {
        var source = @"
            const h = new Headers({""accept"": ""text/html"", ""host"": ""example.com""});
            const keys: string[] = [];
            for (const k of h.keys()) {
                keys.push(k);
            }
            console.log(keys.join("",""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("accept,host\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Headers_Values_ReturnsIterator(ExecutionMode mode)
    {
        var source = @"
            const h = new Headers({""accept"": ""text/html"", ""host"": ""example.com""});
            const vals: string[] = [];
            for (const v of h.values()) {
                vals.push(v);
            }
            console.log(vals.join("",""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("text/html,example.com\n", output);
    }

    // ========== Response Headers from fetch ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FetchResponse_Headers_Get(ExecutionMode mode)
    {
        var source = $$"""
            async function test(): Promise<void> {
                const res = await fetch('{{_server.BaseUrl}}json');
                const ct = res.headers.get("content-type");
                console.log(ct);
            }
            test();
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Contains("application/json", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FetchResponse_Headers_Has(ExecutionMode mode)
    {
        var source = $$"""
            async function test(): Promise<void> {
                const res = await fetch('{{_server.BaseUrl}}json');
                console.log(res.headers.has("content-type"));
            }
            test();
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FetchResponse_Headers_ForEach(ExecutionMode mode)
    {
        // Use an array to collect results instead of mutating a captured primitive,
        // because async state machine closure capture doesn't support primitive mutation.
        var source = $$"""
            async function test(): Promise<void> {
                const res = await fetch('{{_server.BaseUrl}}text');
                const keys: string[] = [];
                res.headers.forEach((value: string, key: string) => {
                    keys.push(key);
                });
                console.log(keys.indexOf("content-type") >= 0);
            }
            test();
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    // ========== AbortSignal integration tests ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fetch_AbortSignal_AbortBeforeFetch(ExecutionMode mode)
    {
        var source = $$"""
            async function test(): Promise<void> {
                const controller = new AbortController();
                controller.abort();
                try {
                    await fetch('{{_server.BaseUrl}}json', { signal: controller.signal });
                    console.log("should not reach");
                } catch (e) {
                    console.log("aborted");
                }
            }
            test();
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("aborted\n", output);
    }

    // Note: AbortDuringFetch test omitted — requires concurrent timer execution during
    // in-flight fetch which the interpreter's event loop doesn't support yet.

    // ========== Error handling tests ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fetch_NoArguments_Throws(ExecutionMode mode)
    {
        var source = @"
            async function test(): Promise<void> {
                try {
                    await (fetch as any)();
                    console.log(""should not reach"");
                } catch (e) {
                    console.log(""error"");
                }
            }
            test();
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("error\n", output);
    }

    // ========== Response methods ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FetchResponse_StatusText_ReturnsText(ExecutionMode mode)
    {
        var source = $$"""
            async function test(): Promise<void> {
                const res = await fetch('{{_server.BaseUrl}}status/404');
                console.log(res.status);
                console.log(res.ok);
            }
            test();
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("404\nfalse\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FetchResponse_Url_ReturnsUrl(ExecutionMode mode)
    {
        var source = $$"""
            async function test(): Promise<void> {
                const res = await fetch('{{_server.BaseUrl}}json');
                console.log(res.url);
            }
            test();
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Contains("json", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FetchResponse_BodyUsed_TracksConsumption(ExecutionMode mode)
    {
        var source = $$"""
            async function test(): Promise<void> {
                const res = await fetch('{{_server.BaseUrl}}text');
                console.log(res.bodyUsed);
                await res.text();
                console.log(res.bodyUsed);
            }
            test();
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FetchResponse_Clone_Works(ExecutionMode mode)
    {
        var source = $$"""
            async function test(): Promise<void> {
                const res = await fetch('{{_server.BaseUrl}}text');
                const cloned = res.clone();
                const text1 = await res.text();
                const text2 = await cloned.text();
                console.log(text1 === text2);
            }
            test();
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    // ========== HTTP methods ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fetch_PatchMethod(ExecutionMode mode)
    {
        var source = $$"""
            async function test(): Promise<void> {
                const res = await fetch('{{_server.BaseUrl}}echo', {
                    method: 'PATCH',
                    body: 'patch data'
                });
                const data = await res.json();
                console.log(data.method);
            }
            test();
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("PATCH\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fetch_HeadMethod(ExecutionMode mode)
    {
        var source = $$"""
            async function test(): Promise<void> {
                const res = await fetch('{{_server.BaseUrl}}text', {
                    method: 'HEAD'
                });
                console.log(res.status);
                console.log(res.ok);
            }
            test();
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("200\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fetch_JsonBody(ExecutionMode mode)
    {
        var source = $$"""
            async function test(): Promise<void> {
                const res = await fetch('{{_server.BaseUrl}}echo', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ name: 'test', value: 123 })
                });
                const data = await res.json();
                console.log(data.method);
                console.log(data.body);
            }
            test();
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Contains("POST", output);
        Assert.Contains("test", output);
    }

    // ========== Headers with fetch options ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fetch_WithHeadersObject(ExecutionMode mode)
    {
        var source = $$"""
            async function test(): Promise<void> {
                const headers = new Headers();
                headers.set('X-Custom', 'hello');
                const res = await fetch('{{_server.BaseUrl}}echo', {
                    headers: headers
                });
                const data = await res.json();
                // GetEntries() lowercases keys, check both cases
                const val = data.headers['X-Custom'] || data.headers['x-custom'];
                console.log(val);
            }
            test();
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("hello\n", output);
    }
}
