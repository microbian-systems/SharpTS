using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// Tests for async DNS resolution methods: resolve, resolve4, resolve6, reverse.
/// Uses localhost/127.0.0.1 to avoid external DNS dependency.
/// </summary>
public class DnsAsyncTests
{
    [Theory]
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

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\n", output);
    }

    [Theory]
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

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
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

        var output = TestHarness.RunModules(files, "main.ts", mode);
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

    [Theory]
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

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void DnsPromises_Lookup(ExecutionMode mode)
    {
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

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
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

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }
}
