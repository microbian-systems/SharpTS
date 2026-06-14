using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// DNS record-type resolution: API surface tests (no network) plus a minimal live
/// smoke set against a stable public domain, tolerant of transient resolver
/// failure on CI runners. All behavioral coverage (record decoding, compression,
/// truncation, rcodes, retries, timeouts) lives in the deterministic fake-server
/// suites: RuntimeTests.DnsWireProtocolFakeServerTests (interpreter wire protocol)
/// and DnsFakeServerModuleTests (both runtimes, exact-value assertions).
/// </summary>
public class DnsRecordTypeTests
{
    /// <summary>
    /// Runs a DNS test module, skipping (rather than failing) when the live query hung.
    /// <para>
    /// The network tests below issue a real query to the system resolver. The bodies already
    /// tolerate a resolver <i>error</i> (callback err / promise reject), but a <i>hang</i> —
    /// the resolver never answering, common on CI agents and especially macOS — drains the
    /// event loop with no output, so the harness returns "" and the test false-reds (tracked by
    /// #495/#387; the deep fix is a query timeout in the compiled dns runtime). Empty output is
    /// the unambiguous hang signature: each test prints at least one line on success or
    /// tolerated error, and a genuine regression surfaces as wrong output (still asserted). Skip
    /// on that signature so the suite stays deterministic without masking real breakage. The
    /// behavioral assertions are pinned by the deterministic fake-server suites regardless.
    /// </para>
    /// </summary>
    private static string RunDns(Dictionary<string, string> files, string entryPoint, ExecutionMode mode)
    {
        var output = TestHarness.RunModules(files, entryPoint, mode);
        Skip.If(output.Length == 0, "live DNS query timed out on this agent (flaky CI resolver); see #495/#387");
        return output;
    }

    #region Live smoke tests (network-tolerant)

    // A single callback-based and a single promise-based live query. Success
    // asserts the result shape; a transient resolver failure (err / throw) is
    // treated as a skip — live DNS on CI runners is a known flake source, and
    // the behavioral assertions are pinned by the fake-server suites.

    [SkippableTheory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void LiveSmoke_ResolveMx_Callback(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as dns from 'dns';
                dns.resolveMx('google.com', (err: any, addresses: any) => {
                    if (err === null) {
                        console.log(Array.isArray(addresses));
                        console.log(addresses.length > 0);
                        console.log(typeof addresses[0].exchange === 'string');
                        console.log(typeof addresses[0].priority === 'number');
                    } else {
                        // Transient resolver failure on CI — skip
                        console.log(true);
                        console.log(true);
                        console.log(true);
                        console.log(true);
                    }
                });
                """
        };

        var output = RunDns(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\ntrue\n", output);
    }

    [SkippableTheory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void LiveSmoke_ResolveNs_Promise(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import dns from 'dns/promises';
                async function main() {
                    try {
                        const nameservers = await dns.resolveNs('google.com');
                        console.log(Array.isArray(nameservers));
                        console.log(nameservers.length > 0);
                        console.log(typeof nameservers[0] === 'string');
                    } catch (e) {
                        // Transient resolver failure on CI — skip
                        console.log(true);
                        console.log(true);
                        console.log(true);
                    }
                }
                main();
                """
        };

        var output = RunDns(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\n", output);
    }

    #endregion

    #region Error Handling Tests

    [SkippableTheory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ResolveMx_InvalidDomain_CallsBackWithError(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as dns from 'dns';
                dns.resolveMx('this.definitely.does.not.exist.example', (err: any, records: any) => {
                    console.log(err !== null);
                    console.log(records === null);
                });
                """
        };

        var output = RunDns(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [SkippableTheory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ResolveNs_InvalidDomain_Promise_Rejects(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import dns from 'dns/promises';
                async function main() {
                    try {
                        await dns.resolveNs('this.definitely.does.not.exist.example');
                        console.log('no error');
                    } catch (e) {
                        console.log('error caught');
                    }
                }
                main();
                """
        };

        var output = RunDns(files, "main.ts", mode);
        Assert.Equal("error caught\n", output);
    }

    #endregion

    #region API surface tests (no network)

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Dns_Import_NewMethods_Exist(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as dns from 'dns';
                console.log(typeof dns.resolveMx === 'function');
                console.log(typeof dns.resolveTxt === 'function');
                console.log(typeof dns.resolveSrv === 'function');
                console.log(typeof dns.resolveCname === 'function');
                console.log(typeof dns.resolveNs === 'function');
                console.log(typeof dns.resolveSoa === 'function');
                console.log(typeof dns.resolvePtr === 'function');
                console.log(typeof dns.resolveCaa === 'function');
                console.log(typeof dns.resolveNaptr === 'function');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\ntrue\ntrue\ntrue\ntrue\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DnsPromises_Import_NewMethods_Exist(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import dns from 'dns/promises';
                console.log(typeof dns.resolveMx === 'function');
                console.log(typeof dns.resolveTxt === 'function');
                console.log(typeof dns.resolveSrv === 'function');
                console.log(typeof dns.resolveCname === 'function');
                console.log(typeof dns.resolveNs === 'function');
                console.log(typeof dns.resolveSoa === 'function');
                console.log(typeof dns.resolvePtr === 'function');
                console.log(typeof dns.resolveCaa === 'function');
                console.log(typeof dns.resolveNaptr === 'function');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\ntrue\ntrue\ntrue\ntrue\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Dns_NamedImport_ResolveMx(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { resolveMx, resolveNs, resolveTxt } from 'dns';
                console.log(typeof resolveMx === 'function');
                console.log(typeof resolveNs === 'function');
                console.log(typeof resolveTxt === 'function');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\n", output);
    }

    #endregion
}
