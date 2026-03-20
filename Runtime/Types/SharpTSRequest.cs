using System.Text;
using System.Text.Json;
using SharpTS.Execution;
using SharpTS.Runtime.BuiltIns;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of a standalone Request object.
/// </summary>
/// <remarks>
/// Provides the Web API Request constructor for creating HTTP requests.
/// Unlike SharpTSFetchResponse which wraps HttpResponseMessage, this wraps in-memory data.
/// Properties: method, url, headers, body, bodyUsed
/// Methods: json(), text(), arrayBuffer(), clone()
/// </remarks>
public class SharpTSRequest : ITypeCategorized
{
    /// <inheritdoc />
    public TypeCategory RuntimeCategory => TypeCategory.Record;

    private readonly string _method;
    private readonly string _url;
    private readonly SharpTSHeaders _headers;
    private readonly object? _body;
    private byte[]? _bodyBytes;
    private bool _bodyConsumed;

    /// <summary>
    /// Creates a new Request with the given URL and optional init object.
    /// </summary>
    public SharpTSRequest(string url, SharpTSObject? init)
    {
        _url = url ?? "";
        _method = "GET";
        _headers = new SharpTSHeaders();
        _body = null;

        if (init == null) return;

        if (init.Fields.TryGetValue("method", out var methodObj) && methodObj is string m)
            _method = m.ToUpperInvariant();

        if (init.Fields.TryGetValue("headers", out var headersObj))
        {
            _headers = headersObj switch
            {
                SharpTSHeaders h => h,
                SharpTSObject obj => new SharpTSHeaders(obj),
                _ => new SharpTSHeaders()
            };
        }

        if (init.Fields.TryGetValue("body", out var bodyObj) && bodyObj != null)
            _body = bodyObj;
    }

    /// <summary>
    /// Internal constructor for clone.
    /// </summary>
    private SharpTSRequest(string url, string method, SharpTSHeaders headers, object? body, byte[]? bodyBytes)
    {
        _url = url;
        _method = method;
        _headers = headers;
        _body = body;
        _bodyBytes = bodyBytes;
        _bodyConsumed = false;
    }

    public string Method => _method;
    public string Url => _url;
    public SharpTSHeaders Headers => _headers;
    public object? Body => _body;
    public bool BodyUsed => _bodyConsumed;

    /// <summary>
    /// Gets a member (property or method) by name.
    /// </summary>
    public object? GetMember(string name)
    {
        return name switch
        {
            "method" => Method,
            "url" => Url,
            "headers" => Headers,
            "body" => Body,
            "bodyUsed" => BodyUsed,
            "json" => new BuiltInAsyncMethod("json", 0, JsonImpl).Bind(this),
            "text" => new BuiltInAsyncMethod("text", 0, TextImpl).Bind(this),
            "arrayBuffer" => new BuiltInAsyncMethod("arrayBuffer", 0, ArrayBufferImpl).Bind(this),
            "clone" => new BuiltInMethod("clone", 0, (_, receiver, _) =>
            {
                if (receiver is SharpTSRequest req)
                    return req.Clone();
                throw new Exception("Runtime Error: clone requires a Request object");
            }).Bind(this),
            _ => SharpTSUndefined.Instance
        };
    }

    private async Task<object?> JsonImpl(Interpreter interpreter, object? receiver, List<object?> args)
    {
        var text = ReadBodyAsString();
        try
        {
            return ParseJson(text);
        }
        catch (JsonException ex)
        {
            throw new SharpTSPromiseRejectedException(
                new SharpTSSyntaxError($"Unexpected token in JSON: {ex.Message}"));
        }
    }

    private async Task<object?> TextImpl(Interpreter interpreter, object? receiver, List<object?> args)
    {
        return ReadBodyAsString();
    }

    private async Task<object?> ArrayBufferImpl(Interpreter interpreter, object? receiver, List<object?> args)
    {
        return new SharpTSBuffer(ReadBodyAsBytes());
    }

    private byte[] ReadBodyAsBytes()
    {
        if (_bodyConsumed && _bodyBytes != null)
            return _bodyBytes;

        if (_bodyConsumed)
            throw new Exception("Runtime Error: body stream already read");

        _bodyBytes = _body switch
        {
            string s => Encoding.UTF8.GetBytes(s),
            SharpTSBuffer buf => buf.Data,
            _ => []
        };
        _bodyConsumed = true;
        return _bodyBytes;
    }

    private string ReadBodyAsString()
    {
        var bytes = ReadBodyAsBytes();
        return Encoding.UTF8.GetString(bytes);
    }

    private SharpTSRequest Clone()
    {
        if (_bodyConsumed)
            throw new Exception("Runtime Error: cannot clone Request after body has been consumed");

        return new SharpTSRequest(_url, _method, _headers, _body, _bodyBytes);
    }

    private static object? ParseJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return ConvertJsonElement(doc.RootElement);
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Array => new SharpTSArray(
                element.EnumerateArray().Select(ConvertJsonElement).ToList()),
            JsonValueKind.Object => new SharpTSObject(
                element.EnumerateObject().ToDictionary(
                    p => p.Name,
                    p => ConvertJsonElement(p.Value))),
            _ => null
        };
    }

    public override string ToString() => $"Request {{ method: {Method}, url: {Url} }}";
}
