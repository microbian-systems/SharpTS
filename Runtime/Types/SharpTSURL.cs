using SharpTS.Runtime.BuiltIns;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of the WHATWG URL API.
/// Wraps System.Uri and provides Node.js/browser-compatible URL properties.
/// </summary>
public class SharpTSURL : ITypeCategorized
{
    /// <inheritdoc />
    public TypeCategory RuntimeCategory => TypeCategory.Record;

    private readonly Uri _uri;
    private SharpTSURLSearchParams? _searchParams;

    public SharpTSURL(string url)
    {
        _uri = new Uri(url, UriKind.Absolute);
    }

    public SharpTSURL(string url, string baseUrl)
    {
        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        _uri = new Uri(baseUri, url);
    }

    /// <summary>
    /// Gets the full URL string.
    /// </summary>
    public string Href => _uri.AbsoluteUri;

    /// <summary>
    /// Gets the protocol (scheme) with trailing colon, e.g. "https:".
    /// </summary>
    public string Protocol => _uri.Scheme + ":";

    /// <summary>
    /// Gets the host (hostname + port if non-default).
    /// </summary>
    public string Host => _uri.IsDefaultPort ? _uri.Host : $"{_uri.Host}:{_uri.Port}";

    /// <summary>
    /// Gets the hostname without port.
    /// </summary>
    public string Hostname => _uri.Host;

    /// <summary>
    /// Gets the port as a string, or empty string if default.
    /// </summary>
    public string Port => _uri.IsDefaultPort ? "" : _uri.Port.ToString();

    /// <summary>
    /// Gets the pathname (path portion).
    /// </summary>
    public string Pathname => _uri.AbsolutePath;

    /// <summary>
    /// Gets the search string including leading '?', or empty if none.
    /// </summary>
    public string Search => string.IsNullOrEmpty(_uri.Query) ? "" : _uri.Query;

    /// <summary>
    /// Gets the URLSearchParams object for this URL.
    /// </summary>
    public SharpTSURLSearchParams SearchParams
    {
        get
        {
            _searchParams ??= new SharpTSURLSearchParams(Search.TrimStart('?'));
            return _searchParams;
        }
    }

    /// <summary>
    /// Gets the hash (fragment) including leading '#', or empty if none.
    /// </summary>
    public string Hash => string.IsNullOrEmpty(_uri.Fragment) ? "" : _uri.Fragment;

    /// <summary>
    /// Gets the origin (protocol + host).
    /// </summary>
    public string Origin => $"{Protocol}//{Host}";

    /// <summary>
    /// Gets the username portion of the URL.
    /// </summary>
    public string Username => Uri.UnescapeDataString(_uri.UserInfo.Split(':')[0]);

    /// <summary>
    /// Gets the password portion of the URL.
    /// </summary>
    public string Password
    {
        get
        {
            var parts = _uri.UserInfo.Split(':');
            return parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
    }

    /// <summary>
    /// Returns the URL as a string.
    /// </summary>
    public override string ToString() => Href;

    /// <summary>
    /// Returns the URL as JSON (same as href).
    /// </summary>
    public string ToJSON() => Href;

    /// <summary>
    /// Gets a member (method or property) by name for interpreter dispatch.
    /// </summary>
    public object? GetMember(string name)
    {
        return name switch
        {
            "href" => Href,
            "protocol" => Protocol,
            "host" => Host,
            "hostname" => Hostname,
            "port" => Port,
            "pathname" => Pathname,
            "search" => Search,
            "hash" => Hash,
            "origin" => Origin,
            "username" => Username,
            "password" => Password,
            "searchParams" => SearchParams,
            "toString" => new BuiltInMethod("toString", 0, (_, _, _) => (object)Href),
            "toJSON" => new BuiltInMethod("toJSON", 0, (_, _, _) => (object)Href),
            _ => null
        };
    }
}

/// <summary>
/// Runtime representation of URLSearchParams API.
/// Provides methods to work with the query string of a URL.
/// </summary>
public class SharpTSURLSearchParams : ITypeCategorized
{
    /// <inheritdoc />
    public TypeCategory RuntimeCategory => TypeCategory.Record;

    private readonly List<(string Key, string Value)> _params = [];

    public SharpTSURLSearchParams()
    {
    }

    public SharpTSURLSearchParams(string init)
    {
        if (string.IsNullOrEmpty(init))
            return;

        foreach (var pair in init.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex >= 0)
            {
                var key = Uri.UnescapeDataString(pair[..eqIndex].Replace('+', ' '));
                var value = Uri.UnescapeDataString(pair[(eqIndex + 1)..].Replace('+', ' '));
                _params.Add((key, value));
            }
            else
            {
                var key = Uri.UnescapeDataString(pair.Replace('+', ' '));
                _params.Add((key, ""));
            }
        }
    }

    /// <summary>
    /// Gets the first value associated with a given key.
    /// </summary>
    public string? Get(string name)
    {
        foreach (var (key, value) in _params)
        {
            if (key == name)
                return value;
        }
        return null;
    }

    /// <summary>
    /// Gets all values associated with a given key.
    /// </summary>
    public List<string> GetAll(string name)
    {
        var result = new List<string>();
        foreach (var (key, value) in _params)
        {
            if (key == name)
                result.Add(value);
        }
        return result;
    }

    /// <summary>
    /// Returns true if a parameter with the specified key exists.
    /// </summary>
    public bool Has(string name)
    {
        foreach (var (key, _) in _params)
        {
            if (key == name)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Sets the value associated with a given key. Removes other values.
    /// </summary>
    public void Set(string name, string value)
    {
        // Remove all existing entries with this key
        _params.RemoveAll(p => p.Key == name);
        // Add new entry
        _params.Add((name, value));
    }

    /// <summary>
    /// Appends a specified key/value pair.
    /// </summary>
    public void Append(string name, string value)
    {
        _params.Add((name, value));
    }

    /// <summary>
    /// Deletes all occurrences of a given key.
    /// </summary>
    public void Delete(string name)
    {
        _params.RemoveAll(p => p.Key == name);
    }

    /// <summary>
    /// Sorts all key/value pairs by key name, alphabetically.
    /// </summary>
    public void Sort()
    {
        _params.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns all keys (includes duplicates, preserving iteration order).
    /// </summary>
    public List<string> Keys()
    {
        return _params.Select(p => p.Key).ToList();
    }

    /// <summary>
    /// Returns all values.
    /// </summary>
    public List<string> Values()
    {
        return _params.Select(p => p.Value).ToList();
    }

    /// <summary>
    /// Returns the query string.
    /// </summary>
    public override string ToString()
    {
        var pairs = _params.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}");
        return string.Join("&", pairs);
    }

    /// <summary>
    /// Gets the number of parameters.
    /// </summary>
    public int Size => _params.Count;

    /// <summary>
    /// Gets a member (method or property) by name for interpreter dispatch.
    /// </summary>
    public object? GetMember(string name)
    {
        return name switch
        {
            "get" => new BuiltInMethod("get", 1, (_, _, args) =>
            {
                var key = args[0]?.ToString() ?? "";
                return (object?)Get(key);
            }),
            "getAll" => new BuiltInMethod("getAll", 1, (_, _, args) =>
            {
                var key = args[0]?.ToString() ?? "";
                return new SharpTSArray(GetAll(key).Select(v => (object?)v).ToList());
            }),
            "has" => new BuiltInMethod("has", 1, (_, _, args) =>
            {
                var key = args[0]?.ToString() ?? "";
                return (object)Has(key);
            }),
            "set" => new BuiltInMethod("set", 2, (_, _, args) =>
            {
                var key = args[0]?.ToString() ?? "";
                var value = args.Count > 1 ? args[1]?.ToString() ?? "" : "";
                Set(key, value);
                return SharpTSUndefined.Instance;
            }),
            "append" => new BuiltInMethod("append", 2, (_, _, args) =>
            {
                var key = args[0]?.ToString() ?? "";
                var value = args.Count > 1 ? args[1]?.ToString() ?? "" : "";
                Append(key, value);
                return SharpTSUndefined.Instance;
            }),
            "delete" => new BuiltInMethod("delete", 1, (_, _, args) =>
            {
                var key = args[0]?.ToString() ?? "";
                Delete(key);
                return SharpTSUndefined.Instance;
            }),
            "sort" => new BuiltInMethod("sort", 0, (_, _, _) =>
            {
                Sort();
                return SharpTSUndefined.Instance;
            }),
            "toString" => new BuiltInMethod("toString", 0, (_, _, _) => (object)ToString()),
            "forEach" => new BuiltInMethod("forEach", 1, (interp, _, args) =>
            {
                var callback = args[0];
                foreach (var (key, value) in _params)
                {
                    if (callback is ISharpTSCallable callable)
                    {
                        callable.Call(interp!, [value, key]);
                    }
                }
                return SharpTSUndefined.Instance;
            }),
            "entries" => new BuiltInMethod("entries", 0, (_, _, _) =>
            {
                var entries = _params
                    .Select(p => (object?)new SharpTSArray([(object?)p.Key, p.Value]))
                    .ToList();
                return new SharpTSIterator(entries);
            }),
            "keys" => new BuiltInMethod("keys", 0, (_, _, _) =>
            {
                var keys = _params
                    .Select(p => (object?)p.Key)
                    .ToList();
                return new SharpTSIterator(keys);
            }),
            "values" => new BuiltInMethod("values", 0, (_, _, _) =>
            {
                var values = _params
                    .Select(p => (object?)p.Value)
                    .ToList();
                return new SharpTSIterator(values);
            }),
            "size" => (double)Size,
            _ => null
        };
    }
}
