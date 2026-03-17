using System.Net;
using System.Net.Sockets;
using DnsClient;
using DnsClient.Protocol;

namespace SharpTS.Runtime.BuiltIns.Modules;

/// <summary>
/// DNS record resolver using DnsClient for MX, TXT, SRV, CNAME, NS, SOA, PTR, CAA, NAPTR records.
/// Used by the interpreter (via direct calls) and compiled mode (via direct DnsClient IL emission).
/// </summary>
public static class DnsRecordResolver
{
    private static readonly Lazy<LookupClient> _client = new(() => new LookupClient());

    /// <summary>
    /// Resolves DNS records by type. Returns results as List&lt;object?&gt; suitable for
    /// creating SharpTS arrays/objects.
    /// </summary>
    /// <param name="hostname">The hostname to resolve.</param>
    /// <param name="rrtype">The record type (MX, TXT, SRV, CNAME, NS, SOA, PTR, CAA, NAPTR, A, AAAA).</param>
    /// <returns>
    /// For most types: List&lt;object?&gt; of results (each element is a Dictionary or string).
    /// For SOA: a single Dictionary&lt;string, object?&gt;.
    /// </returns>
    public static object Resolve(string hostname, string rrtype)
    {
        return rrtype.ToUpperInvariant() switch
        {
            "MX" => ResolveMx(hostname),
            "TXT" => ResolveTxt(hostname),
            "SRV" => ResolveSrv(hostname),
            "CNAME" => ResolveCname(hostname),
            "NS" => ResolveNs(hostname),
            "SOA" => ResolveSoa(hostname),
            "PTR" => ResolvePtr(hostname),
            "CAA" => ResolveCaa(hostname),
            "NAPTR" => ResolveNaptr(hostname),
            "A" => ResolveA(hostname),
            "AAAA" => ResolveAaaa(hostname),
            _ => throw new Exception($"Runtime Error: dns.resolve unknown rrtype: {rrtype}")
        };
    }

    /// <summary>
    /// Resolves a single record type by name. Used by individual resolveMx, resolveTxt, etc.
    /// </summary>
    public static object ResolveByType(string hostname, string rrtype)
    {
        return Resolve(hostname, rrtype);
    }

    public static List<object?> ResolveMx(string hostname)
    {
        var client = _client.Value;
        var result = client.Query(hostname, QueryType.MX);
        CheckDnsError(result, "resolveMx", hostname);

        var records = result.Answers.MxRecords().ToList();
        if (records.Count == 0)
            throw new Exception($"Runtime Error: dns.resolveMx ENODATA {hostname}");

        return records.Select(r => (object?)new Dictionary<string, object?>
        {
            ["exchange"] = r.Exchange.Value.TrimEnd('.'),
            ["priority"] = (double)r.Preference
        }).ToList();
    }

    public static List<object?> ResolveTxt(string hostname)
    {
        var client = _client.Value;
        var result = client.Query(hostname, QueryType.TXT);
        CheckDnsError(result, "resolveTxt", hostname);

        var records = result.Answers.TxtRecords().ToList();
        if (records.Count == 0)
            throw new Exception($"Runtime Error: dns.resolveTxt ENODATA {hostname}");

        // Node.js returns string[][] - each TXT record is an array of strings (chunks)
        return records.Select(r => (object?)r.Text.Select(t => (object?)t).ToList()).ToList();
    }

    public static List<object?> ResolveSrv(string hostname)
    {
        var client = _client.Value;
        var result = client.Query(hostname, QueryType.SRV);
        CheckDnsError(result, "resolveSrv", hostname);

        var records = result.Answers.SrvRecords().ToList();
        if (records.Count == 0)
            throw new Exception($"Runtime Error: dns.resolveSrv ENODATA {hostname}");

        return records.Select(r => (object?)new Dictionary<string, object?>
        {
            ["name"] = r.Target.Value.TrimEnd('.'),
            ["port"] = (double)r.Port,
            ["priority"] = (double)r.Priority,
            ["weight"] = (double)r.Weight
        }).ToList();
    }

    public static List<object?> ResolveCname(string hostname)
    {
        var client = _client.Value;
        var result = client.Query(hostname, QueryType.CNAME);
        CheckDnsError(result, "resolveCname", hostname);

        var records = result.Answers.CnameRecords().ToList();
        if (records.Count == 0)
            throw new Exception($"Runtime Error: dns.resolveCname ENODATA {hostname}");

        return records.Select(r => (object?)r.CanonicalName.Value.TrimEnd('.')).ToList();
    }

    public static List<object?> ResolveNs(string hostname)
    {
        var client = _client.Value;
        var result = client.Query(hostname, QueryType.NS);
        CheckDnsError(result, "resolveNs", hostname);

        var records = result.Answers.NsRecords().ToList();
        if (records.Count == 0)
            throw new Exception($"Runtime Error: dns.resolveNs ENODATA {hostname}");

        return records.Select(r => (object?)r.NSDName.Value.TrimEnd('.')).ToList();
    }

    public static Dictionary<string, object?> ResolveSoa(string hostname)
    {
        var client = _client.Value;
        var result = client.Query(hostname, QueryType.SOA);
        CheckDnsError(result, "resolveSoa", hostname);

        var record = result.Answers.SoaRecords().FirstOrDefault()
            ?? throw new Exception($"Runtime Error: dns.resolveSoa ENODATA {hostname}");

        return new Dictionary<string, object?>
        {
            ["nsname"] = record.MName.Value.TrimEnd('.'),
            ["hostmaster"] = record.RName.Value.TrimEnd('.'),
            ["serial"] = (double)record.Serial,
            ["refresh"] = (double)record.Refresh,
            ["retry"] = (double)record.Retry,
            ["expire"] = (double)record.Expire,
            ["minttl"] = (double)record.Minimum
        };
    }

    public static List<object?> ResolvePtr(string hostname)
    {
        var client = _client.Value;
        var result = client.Query(hostname, QueryType.PTR);
        CheckDnsError(result, "resolvePtr", hostname);

        var records = result.Answers.PtrRecords().ToList();
        if (records.Count == 0)
            throw new Exception($"Runtime Error: dns.resolvePtr ENODATA {hostname}");

        return records.Select(r => (object?)r.PtrDomainName.Value.TrimEnd('.')).ToList();
    }

    public static List<object?> ResolveCaa(string hostname)
    {
        var client = _client.Value;
        var result = client.Query(hostname, QueryType.CAA);
        CheckDnsError(result, "resolveCaa", hostname);

        var records = result.Answers.CaaRecords().ToList();
        if (records.Count == 0)
            throw new Exception($"Runtime Error: dns.resolveCaa ENODATA {hostname}");

        return records.Select(r => (object?)new Dictionary<string, object?>
        {
            ["critical"] = (double)(r.Flags & 0x80),
            [r.Tag] = r.Value
        }).ToList();
    }

    public static List<object?> ResolveNaptr(string hostname)
    {
        var client = _client.Value;
        var result = client.Query(hostname, QueryType.NAPTR);
        CheckDnsError(result, "resolveNaptr", hostname);

        var records = result.Answers.OfType<NAPtrRecord>().ToList();
        if (records.Count == 0)
            throw new Exception($"Runtime Error: dns.resolveNaptr ENODATA {hostname}");

        return records.Select(r => (object?)new Dictionary<string, object?>
        {
            ["flags"] = r.Flags,
            ["service"] = r.Services,
            ["regexp"] = r.RegularExpression,
            ["replacement"] = r.Replacement.Value.TrimEnd('.'),
            ["order"] = (double)r.Order,
            ["preference"] = (double)r.Preference
        }).ToList();
    }

    public static List<object?> ResolveA(string hostname)
    {
        var hostEntry = Dns.GetHostEntry(hostname);
        var addresses = hostEntry.AddressList
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);
        return addresses.Select(a => (object?)a.ToString()).ToList();
    }

    public static List<object?> ResolveAaaa(string hostname)
    {
        var hostEntry = Dns.GetHostEntry(hostname);
        var addresses = hostEntry.AddressList
            .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6)
            .ToArray();
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);
        return addresses.Select(a => (object?)a.ToString()).ToList();
    }

    private static void CheckDnsError(IDnsQueryResponse result, string methodName, string hostname)
    {
        if (result.HasError)
        {
            var code = result.Header.ResponseCode switch
            {
                DnsHeaderResponseCode.NotExistentDomain => "ENOTFOUND",
                DnsHeaderResponseCode.ServerFailure => "ESERVFAIL",
                DnsHeaderResponseCode.Refused => "EREFUSED",
                DnsHeaderResponseCode.FormatError => "EFORMERR",
                DnsHeaderResponseCode.NotImplemented => "ENOTIMP",
                _ => "EAI_FAIL"
            };
            throw new Exception($"Runtime Error: dns.{methodName} {code} {hostname}");
        }
    }
}
