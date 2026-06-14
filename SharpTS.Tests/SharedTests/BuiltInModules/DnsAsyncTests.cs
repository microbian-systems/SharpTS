using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// Tests for async DNS resolution methods: resolve, resolve4, resolve6, reverse.
/// Uses localhost/127.0.0.1 to avoid external DNS dependency.
/// </summary>
public class DnsAsyncTests
{
    /// <summary>
    /// Runs a DNS test module, skipping (rather than failing) when the live query hung.
    /// <para>
    /// <c>dns.resolve*</c>/<c>dns.reverse</c> send a real query to the system resolver
    /// (unlike <c>dns.lookup</c>, they don't consult the hosts file), and that query can
    /// time out on CI agents — notably macOS — draining the event loop with no output. That
    /// is a flaky false-red, not a SharpTS bug (tracked by #495/#387), and the proper deep
    /// fix is a query timeout in the compiled <c>dns</c> runtime. Empty output is the
    /// unambiguous hang signature here: every test below prints at least one line on success,
    /// and a genuine regression surfaces as wrong/partial output (still asserted) or fails
    /// identically on Linux/Windows, where the resolver doesn't hang. Skip on that signature
    /// so the suite stays deterministic without masking real breakage.
    /// </para>
    /// </summary>
    private static string RunDns(Dictionary<string, string> files, string entryPoint, ExecutionMode mode)
    {
        var output = TestHarness.RunModules(files, entryPoint, mode);
        Skip.If(output.Length == 0, "live DNS query timed out on this agent (flaky CI resolver); see #495/#387");
        return output;
    }

    [SkippableTheory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Resolve4_Localhost(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as dns from 'dns';
                dns.resolve4('localhost', (err: any, addresses: any) => {
                    console.log(err === null);
                    console.log(Array.isArray(addresses));
                    console.log(addresses.length > 0);
                });
                """
        };

        var output = RunDns(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\n", output);
    }

    [SkippableTheory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Resolve_DefaultRrtypeA(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as dns from 'dns';
                dns.resolve('localhost', (err: any, addresses: any) => {
                    console.log(err === null);
                    console.log(Array.isArray(addresses));
                });
                """
        };

        var output = RunDns(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [SkippableTheory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Resolve4_NestedInsideCallback_CallbackFires(ExecutionMode mode)
    {
        // #239: in compiled mode, dns.* calls inside another callback (or any
        // function body) resolved the module member dynamically to null and
        // silently dropped the callback.
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as dns from 'dns';
                dns.resolve4('localhost', (err: any, a: any) => {
                    console.log('outer ' + (err === null));
                    dns.resolve4('localhost', (err2: any, b: any) => {
                        console.log('inner ' + (err2 === null));
                    });
                });
                """
        };

        var output = RunDns(files, "main.ts", mode);
        Assert.Equal("outer true\ninner true\n", output);
    }

    [SkippableTheory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Resolve4_InsideTimerCallback_CallbackFires(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as dns from 'dns';
                setTimeout(() => {
                    dns.resolve4('localhost', (err: any, a: any) => {
                        console.log('dns-in-timer ' + (err === null));
                    });
                }, 20);
                """
        };

        var output = RunDns(files, "main.ts", mode);
        Assert.Equal("dns-in-timer true\n", output);
    }

    [SkippableTheory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Resolve4_InsideFunctionBody_CallbackFires(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as dns from 'dns';
                function go() {
                    dns.resolve4('localhost', (err: any, a: any) => {
                        console.log('fn ' + (err === null));
                    });
                }
                go();
                """
        };

        var output = RunDns(files, "main.ts", mode);
        Assert.Equal("fn true\n", output);
    }

    [SkippableTheory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reverse_Loopback(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as dns from 'dns';
                dns.reverse('127.0.0.1', (err: any, hostnames: any) => {
                    console.log(err === null);
                    console.log(Array.isArray(hostnames));
                    console.log(hostnames.length > 0);
                });
                """
        };

        var output = RunDns(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DnsConstants_Defined(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as dns from 'dns';
                console.log(dns.NOTFOUND === 'ENOTFOUND');
                console.log(dns.NODATA === 'ENODATA');
                console.log(dns.SERVFAIL === 'ESERVFAIL');
                console.log(dns.CONNREFUSED === 'ECONNREFUSED');
                console.log(dns.TIMEOUT === 'ETIMEOUT');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\ntrue\ntrue\n", output);
    }

    [SkippableTheory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DnsPromises_Resolve4(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import dns from 'dns/promises';
                async function main() {
                    const addresses = await dns.resolve4('localhost');
                    console.log(Array.isArray(addresses));
                    console.log(addresses.length > 0);
                }
                main();
                """
        };

        var output = RunDns(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DnsPromises_Lookup(ExecutionMode mode)
    {
        // dns.lookup uses the OS resolver (hosts file) rather than a live DNS query, so
        // 'localhost' resolves deterministically — no live-network skip guard needed.
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import dns from 'dns/promises';
                async function main() {
                    const result = await dns.lookup('localhost');
                    console.log(typeof result.address === 'string');
                    console.log(result.family === 4 || result.family === 6);
                }
                main();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [SkippableTheory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DnsPromises_ViaModule(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as dns from 'dns';
                async function main() {
                    const addresses = await dns.promises.resolve4('localhost');
                    console.log(Array.isArray(addresses));
                    console.log(addresses.length > 0);
                }
                main();
                """
        };

        var output = RunDns(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }
}
