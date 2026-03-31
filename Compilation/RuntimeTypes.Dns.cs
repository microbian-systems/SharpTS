using System.Net;
using System.Net.Sockets;
using SharpTS.Runtime.BuiltIns.Modules;

namespace SharpTS.Compilation;

/// <summary>
/// RuntimeTypes helpers for dns/promises compiled support.
/// Called from emitted code via late-binding reflection.
/// Each method returns Task&lt;object?&gt; for wrapping in $Promise.
/// </summary>
public static partial class RuntimeTypes
{
    public static Task<object?> DnsPromisesLookup(object? hostname, object? options)
    {
        var h = hostname?.ToString() ?? "";
        return Task.Run<object?>(() =>
        {
            var entry = Dns.GetHostEntry(h);
            AddressFamily? family = null;
            if (options is double d) family = (int)d == 6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;

            var addr = family != null
                ? entry.AddressList.FirstOrDefault(a => a.AddressFamily == family)
                : entry.AddressList.FirstOrDefault();

            if (addr == null) throw new SocketException((int)SocketError.HostNotFound);

            return (object?)new Dictionary<string, object?>
            {
                ["address"] = addr.ToString(),
                ["family"] = (double)(addr.AddressFamily == AddressFamily.InterNetworkV6 ? 6 : 4)
            };
        });
    }

    public static Task<object?> DnsPromisesResolve(object? hostname, object? rrtype)
    {
        var h = hostname?.ToString() ?? "";
        var rt = rrtype?.ToString() ?? "A";
        return Task.Run<object?>(() => DnsRecordResolver.Resolve(h, rt));
    }

    public static Task<object?> DnsPromisesResolve4(object? hostname)
    {
        var h = hostname?.ToString() ?? "";
        return Task.Run<object?>(() => (object?)DnsRecordResolver.ResolveA(h));
    }

    public static Task<object?> DnsPromisesResolve6(object? hostname)
    {
        var h = hostname?.ToString() ?? "";
        return Task.Run<object?>(() => (object?)DnsRecordResolver.ResolveAaaa(h));
    }

    public static Task<object?> DnsPromisesReverse(object? ip)
    {
        var ipStr = ip?.ToString() ?? "";
        return Task.Run<object?>(() =>
        {
            if (!IPAddress.TryParse(ipStr, out var addr))
                throw new Exception($"dns.reverse: invalid address {ipStr}");
            var entry = Dns.GetHostEntry(addr);
            return (object?)new List<object?> { entry.HostName };
        });
    }

    public static Task<object?> DnsPromisesResolveMx(object? hostname)
    {
        var h = hostname?.ToString() ?? "";
        return Task.Run<object?>(() => (object?)DnsRecordResolver.ResolveMx(h));
    }

    public static Task<object?> DnsPromisesResolveTxt(object? hostname)
    {
        var h = hostname?.ToString() ?? "";
        return Task.Run<object?>(() => (object?)DnsRecordResolver.ResolveTxt(h));
    }

    public static Task<object?> DnsPromisesResolveSrv(object? hostname)
    {
        var h = hostname?.ToString() ?? "";
        return Task.Run<object?>(() => (object?)DnsRecordResolver.ResolveSrv(h));
    }

    public static Task<object?> DnsPromisesResolveCname(object? hostname)
    {
        var h = hostname?.ToString() ?? "";
        return Task.Run<object?>(() => (object?)DnsRecordResolver.ResolveCname(h));
    }

    public static Task<object?> DnsPromisesResolveNs(object? hostname)
    {
        var h = hostname?.ToString() ?? "";
        return Task.Run<object?>(() => (object?)DnsRecordResolver.ResolveNs(h));
    }

    public static Task<object?> DnsPromisesResolveSoa(object? hostname)
    {
        var h = hostname?.ToString() ?? "";
        return Task.Run<object?>(() => (object?)DnsRecordResolver.ResolveSoa(h));
    }

    public static Task<object?> DnsPromisesResolvePtr(object? hostname)
    {
        var h = hostname?.ToString() ?? "";
        return Task.Run<object?>(() => (object?)DnsRecordResolver.ResolvePtr(h));
    }

    public static Task<object?> DnsPromisesResolveCaa(object? hostname)
    {
        var h = hostname?.ToString() ?? "";
        return Task.Run<object?>(() => (object?)DnsRecordResolver.ResolveCaa(h));
    }

    public static Task<object?> DnsPromisesResolveNaptr(object? hostname)
    {
        var h = hostname?.ToString() ?? "";
        return Task.Run<object?>(() => (object?)DnsRecordResolver.ResolveNaptr(h));
    }
}
