using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for JSON.stringify of Proxy objects (#92).
/// Compile-mode only — interpreter Proxy + JSON integration is a separate gap.
/// </summary>
public class JSONProxyTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void JSON_Stringify_ProxyWithGetTrap(ExecutionMode mode)
    {
        var source = """
            let target: any = { a: 1, b: 2 };
            let proxy: any = new Proxy(target, {
                get: function(t: any, p: string): any { return t[p] * 10; }
            });
            console.log(JSON.stringify(proxy));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("{\"a\":10,\"b\":20}\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void JSON_Stringify_ProxyWithOwnKeysTrap(ExecutionMode mode)
    {
        var source = """
            let target: any = { a: 1, b: 2, c: 3 };
            let proxy: any = new Proxy(target, {
                ownKeys: function(t: any): string[] { return ["a", "c"]; }
            });
            console.log(JSON.stringify(proxy));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("{\"a\":1,\"c\":3}\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void JSON_Stringify_RevokedProxy_Throws(ExecutionMode mode)
    {
        var source = """
            let target: any = { a: 1 };
            let r: any = Proxy.revocable(target, {});
            r.revoke();
            try {
                JSON.stringify(r.proxy);
                console.log("should not reach");
            } catch (e) {
                console.log("threw");
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("threw\n", output);
    }
}
