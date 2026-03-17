using System.Net;
using System.Net.Sockets;
using SharpTS.Compilation;
using SharpTS.Runtime.Types;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of the Node.js 'dns' module.
/// </summary>
/// <remarks>
/// Provides synchronous DNS resolution methods.
/// In Node.js, dns.lookup uses the OS resolver and is typically callback-based.
/// SharpTS implements synchronous versions that return results directly.
/// </remarks>
public static class DnsModuleInterpreter
{
    /// <summary>
    /// Gets all exported values for the dns module.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            // Sync/callback methods
            ["lookup"] = new BuiltInMethod("lookup", 1, 3, Lookup),
            ["lookupService"] = new BuiltInMethod("lookupService", 2, 3, LookupService),

            // Async (callback-based) DNS resolution
            ["resolve"] = new BuiltInMethod("resolve", 2, 3, ResolveAsync),
            ["resolve4"] = new BuiltInMethod("resolve4", 2, Resolve4Async),
            ["resolve6"] = new BuiltInMethod("resolve6", 2, Resolve6Async),
            ["reverse"] = new BuiltInMethod("reverse", 2, ReverseAsync),

            // Promises API
            ["promises"] = new SharpTSObject(GetPromisesExports()),

            // Constants
            ["ADDRCONFIG"] = (double)1,
            ["V4MAPPED"] = (double)2,
            ["ALL"] = (double)4,
            ["NODATA"] = "ENODATA",
            ["FORMERR"] = "EFORMERR",
            ["SERVFAIL"] = "ESERVFAIL",
            ["NOTFOUND"] = "ENOTFOUND",
            ["NOTIMP"] = "ENOTIMP",
            ["REFUSED"] = "EREFUSED",
            ["BADQUERY"] = "EBADQUERY",
            ["BADNAME"] = "EBADNAME",
            ["BADFAMILY"] = "EBADFAMILY",
            ["BADRESP"] = "EBADRESP",
            ["CONNREFUSED"] = "ECONNREFUSED",
            ["TIMEOUT"] = "ETIMEOUT",
            ["EOF"] = "EEOF",
            ["FILE"] = "EFILE",
            ["NOMEM"] = "ENOMEM",
            ["DESTRUCTION"] = "EDESTRUCTION",
            ["BADSTR"] = "EBADSTR",
            ["BADFLAGS"] = "EBADFLAGS",
            ["NONAME"] = "ENONAME",
            ["BADHINTS"] = "EBADHINTS",
            ["NOTINITIALIZED"] = "ENOTINITIALIZED",
            ["LOADIPHLPAPI"] = "ELOADIPHLPAPI",
            ["ADDRGETNETWORKPARAMS"] = "EADDRGETNETWORKPARAMS",
            ["CANCELLED"] = "ECANCELLED"
        };
    }

    /// <summary>
    /// Gets exported values for dns.promises module.
    /// </summary>
    public static Dictionary<string, object?> GetPromisesExports()
    {
        return new Dictionary<string, object?>
        {
            ["lookup"] = new BuiltInAsyncMethod("lookup", 1, 2, LookupPromise),
            ["resolve"] = new BuiltInAsyncMethod("resolve", 1, 2, ResolvePromise),
            ["resolve4"] = new BuiltInAsyncMethod("resolve4", 1, 1, Resolve4Promise),
            ["resolve6"] = new BuiltInAsyncMethod("resolve6", 1, 1, Resolve6Promise),
            ["reverse"] = new BuiltInAsyncMethod("reverse", 1, 1, ReversePromise)
        };
    }

    /// <summary>
    /// dns.lookup(hostname[, options][, callback]) - Resolves a hostname to an IP address.
    /// </summary>
    /// <remarks>
    /// Options can be:
    /// - A number specifying the address family (4 for IPv4, 6 for IPv6, 0 for both)
    /// - An object with { family?: number, hints?: number, all?: boolean }
    ///
    /// Returns:
    /// - If all is false (default): { address: string, family: number }
    /// - If all is true: Array of { address: string, family: number }
    ///
    /// In Node.js this is async with callback, but SharpTS uses synchronous pattern.
    /// </remarks>
    private static object? Lookup(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count == 0 || args[0] is not string hostname)
            throw new Exception("Runtime Error: dns.lookup requires a hostname string");

        // Parse options
        int family = 0; // 0 = any, 4 = IPv4 only, 6 = IPv6 only
        bool all = false;

        if (args.Count > 1 && args[1] != null)
        {
            if (args[1] is double familyNum)
            {
                family = (int)familyNum;
            }
            else if (args[1] is SharpTSObject options)
            {
                if (options.Fields.TryGetValue("family", out var familyVal) && familyVal is double f)
                    family = (int)f;
                if (options.Fields.TryGetValue("all", out var allVal))
                    all = IsTruthy(allVal);
            }
        }

        try
        {
            var hostEntry = Dns.GetHostEntry(hostname);
            var addresses = hostEntry.AddressList;

            // Filter by family if specified
            if (family == 4)
                addresses = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            else if (family == 6)
                addresses = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToArray();

            if (addresses.Length == 0)
            {
                throw new Exception($"Runtime Error: dns.lookup ENOTFOUND {hostname}");
            }

            if (all)
            {
                // Return array of all addresses
                var results = new List<object?>();
                foreach (var addr in addresses)
                {
                    var fields = new Dictionary<string, object?>
                    {
                        ["address"] = addr.ToString(),
                        ["family"] = addr.AddressFamily == AddressFamily.InterNetwork ? 4.0 : 6.0
                    };
                    results.Add(new SharpTSObject(fields));
                }
                return new SharpTSArray(results);
            }
            else
            {
                // Return first matching address
                var addr = addresses[0];
                var fields = new Dictionary<string, object?>
                {
                    ["address"] = addr.ToString(),
                    ["family"] = addr.AddressFamily == AddressFamily.InterNetwork ? 4.0 : 6.0
                };
                return new SharpTSObject(fields);
            }
        }
        catch (SocketException ex)
        {
            throw new Exception($"Runtime Error: dns.lookup {GetErrorCode(ex)} {hostname}");
        }
    }

    /// <summary>
    /// dns.lookupService(address, port[, callback]) - Resolves address and port to hostname and service.
    /// </summary>
    private static object? LookupService(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count < 2)
            throw new Exception("Runtime Error: dns.lookupService requires address and port");

        if (args[0] is not string address)
            throw new Exception("Runtime Error: dns.lookupService address must be a string");

        if (args[1] is not double portNum)
            throw new Exception("Runtime Error: dns.lookupService port must be a number");

        int port = (int)portNum;

        try
        {
            // Parse the IP address
            if (!IPAddress.TryParse(address, out var ipAddress))
                throw new Exception($"Runtime Error: dns.lookupService invalid address {address}");

            // Reverse DNS lookup
            var hostEntry = Dns.GetHostEntry(ipAddress);

            var fields = new Dictionary<string, object?>
            {
                ["hostname"] = hostEntry.HostName,
                // Note: .NET doesn't have built-in service name lookup, so we just return the port
                ["service"] = port.ToString()
            };
            return new SharpTSObject(fields);
        }
        catch (SocketException ex)
        {
            throw new Exception($"Runtime Error: dns.lookupService {GetErrorCode(ex)} {address}");
        }
    }

    private static bool IsTruthy(object? value) => RuntimeTypes.IsTruthy(value);

    private static string GetErrorCode(SocketException ex)
    {
        return ex.SocketErrorCode switch
        {
            SocketError.HostNotFound => "ENOTFOUND",
            SocketError.NoData => "ENODATA",
            SocketError.TryAgain => "EAGAIN",
            SocketError.NoRecovery => "ESERVFAIL",
            SocketError.TimedOut => "ETIMEDOUT",
            SocketError.ConnectionRefused => "ECONNREFUSED",
            _ => "EAI_FAIL"
        };
    }

    private static SharpTSObject CreateDnsError(string code, string hostname)
    {
        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["code"] = code,
            ["hostname"] = hostname,
            ["message"] = $"queryA {code} {hostname}"
        });
    }

    private static SharpTSArray ResolveAddresses(string hostname, AddressFamily? family)
    {
        var hostEntry = Dns.GetHostEntry(hostname);
        var addresses = hostEntry.AddressList;
        if (family != null)
            addresses = addresses.Where(a => a.AddressFamily == family).ToArray();
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);
        return new SharpTSArray(addresses.Select(a => (object?)a.ToString()).ToList());
    }

    #region Async Callback-based DNS Resolution

    /// <summary>
    /// dns.resolve(hostname[, rrtype], callback) - Resolve hostname to array of addresses.
    /// </summary>
    private static object? ResolveAsync(Interp interpreter, object? receiver, List<object?> args)
    {
        var hostname = args[0]?.ToString() ?? "";
        var callback = args[^1] as ISharpTSCallable
            ?? throw new Exception("Runtime Error: dns.resolve callback is required");

        // Optional rrtype (default 'A')
        string rrtype = "A";
        if (args.Count > 2 && args[1] is string rt)
            rrtype = rt;

        interpreter.Ref();
        _ = Task.Run(() =>
        {
            try
            {
                AddressFamily? family = rrtype.ToUpperInvariant() switch
                {
                    "A" => AddressFamily.InterNetwork,
                    "AAAA" => AddressFamily.InterNetworkV6,
                    _ => null // For unrecognized, return all
                };
                var result = ResolveAddresses(hostname, family);
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    callback.Call(interpreter, [null, result]);
                    interpreter.Unref();
                }, isInterval: false);
            }
            catch (SocketException ex)
            {
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    callback.Call(interpreter, [CreateDnsError(GetErrorCode(ex), hostname), null]);
                    interpreter.Unref();
                }, isInterval: false);
            }
        });

        return SharpTSUndefined.Instance;
    }

    /// <summary>
    /// dns.resolve4(hostname, callback) - Resolve hostname to IPv4 addresses.
    /// </summary>
    private static object? Resolve4Async(Interp interpreter, object? receiver, List<object?> args)
    {
        var hostname = args[0]?.ToString() ?? "";
        var callback = args[^1] as ISharpTSCallable
            ?? throw new Exception("Runtime Error: dns.resolve4 callback is required");

        interpreter.Ref();
        _ = Task.Run(() =>
        {
            try
            {
                var result = ResolveAddresses(hostname, AddressFamily.InterNetwork);
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    callback.Call(interpreter, [null, result]);
                    interpreter.Unref();
                }, isInterval: false);
            }
            catch (SocketException ex)
            {
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    callback.Call(interpreter, [CreateDnsError(GetErrorCode(ex), hostname), null]);
                    interpreter.Unref();
                }, isInterval: false);
            }
        });

        return SharpTSUndefined.Instance;
    }

    /// <summary>
    /// dns.resolve6(hostname, callback) - Resolve hostname to IPv6 addresses.
    /// </summary>
    private static object? Resolve6Async(Interp interpreter, object? receiver, List<object?> args)
    {
        var hostname = args[0]?.ToString() ?? "";
        var callback = args[^1] as ISharpTSCallable
            ?? throw new Exception("Runtime Error: dns.resolve6 callback is required");

        interpreter.Ref();
        _ = Task.Run(() =>
        {
            try
            {
                var result = ResolveAddresses(hostname, AddressFamily.InterNetworkV6);
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    callback.Call(interpreter, [null, result]);
                    interpreter.Unref();
                }, isInterval: false);
            }
            catch (SocketException ex)
            {
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    callback.Call(interpreter, [CreateDnsError(GetErrorCode(ex), hostname), null]);
                    interpreter.Unref();
                }, isInterval: false);
            }
        });

        return SharpTSUndefined.Instance;
    }

    /// <summary>
    /// dns.reverse(ip, callback) - Reverse DNS lookup.
    /// </summary>
    private static object? ReverseAsync(Interp interpreter, object? receiver, List<object?> args)
    {
        var ip = args[0]?.ToString() ?? "";
        var callback = args[^1] as ISharpTSCallable
            ?? throw new Exception("Runtime Error: dns.reverse callback is required");

        interpreter.Ref();
        _ = Task.Run(() =>
        {
            try
            {
                if (!IPAddress.TryParse(ip, out var ipAddress))
                    throw new Exception($"invalid address {ip}");

                var hostEntry = Dns.GetHostEntry(ipAddress);
                var hostnames = new SharpTSArray(new List<object?> { hostEntry.HostName });
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    callback.Call(interpreter, [null, hostnames]);
                    interpreter.Unref();
                }, isInterval: false);
            }
            catch (SocketException ex)
            {
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    callback.Call(interpreter, [CreateDnsError(GetErrorCode(ex), ip), null]);
                    interpreter.Unref();
                }, isInterval: false);
            }
            catch (Exception)
            {
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    callback.Call(interpreter, [CreateDnsError("EAI_FAIL", ip), null]);
                    interpreter.Unref();
                }, isInterval: false);
            }
        });

        return SharpTSUndefined.Instance;
    }

    #endregion

    #region Promise-based DNS Resolution

    private static async Task<object?> LookupPromise(Interp interpreter, object? receiver, List<object?> args)
    {
        var hostname = args[0]?.ToString() ?? "";
        int family = 0;
        if (args.Count > 1 && args[1] is double f) family = (int)f;
        else if (args.Count > 1 && args[1] is SharpTSObject opts)
        {
            if (opts.Fields.TryGetValue("family", out var fv) && fv is double fd) family = (int)fd;
        }

        return await Task.Run<object?>(() =>
        {
            var hostEntry = Dns.GetHostEntry(hostname);
            var addresses = hostEntry.AddressList;
            if (family == 4) addresses = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            else if (family == 6) addresses = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToArray();

            if (addresses.Length == 0)
                throw new Exception($"dns.lookup ENOTFOUND {hostname}");

            var addr = addresses[0];
            return new SharpTSObject(new Dictionary<string, object?>
            {
                ["address"] = addr.ToString(),
                ["family"] = addr.AddressFamily == AddressFamily.InterNetwork ? 4.0 : 6.0
            });
        });
    }

    private static async Task<object?> ResolvePromise(Interp interpreter, object? receiver, List<object?> args)
    {
        var hostname = args[0]?.ToString() ?? "";
        string rrtype = "A";
        if (args.Count > 1 && args[1] is string rt) rrtype = rt;

        return await Task.Run<object?>(() =>
        {
            AddressFamily? family = rrtype.ToUpperInvariant() switch
            {
                "A" => AddressFamily.InterNetwork,
                "AAAA" => AddressFamily.InterNetworkV6,
                _ => null
            };
            return ResolveAddresses(hostname, family);
        });
    }

    private static async Task<object?> Resolve4Promise(Interp interpreter, object? receiver, List<object?> args)
    {
        var hostname = args[0]?.ToString() ?? "";
        return await Task.Run<object?>(() => ResolveAddresses(hostname, AddressFamily.InterNetwork));
    }

    private static async Task<object?> Resolve6Promise(Interp interpreter, object? receiver, List<object?> args)
    {
        var hostname = args[0]?.ToString() ?? "";
        return await Task.Run<object?>(() => ResolveAddresses(hostname, AddressFamily.InterNetworkV6));
    }

    private static async Task<object?> ReversePromise(Interp interpreter, object? receiver, List<object?> args)
    {
        var ip = args[0]?.ToString() ?? "";
        return await Task.Run<object?>(() =>
        {
            if (!IPAddress.TryParse(ip, out var ipAddress))
                throw new Exception($"dns.reverse: invalid address {ip}");
            var hostEntry = Dns.GetHostEntry(ipAddress);
            return new SharpTSArray(new List<object?> { hostEntry.HostName });
        });
    }

    #endregion
}
