using System.Net;
using System.Text;
using SharpTS.Execution;
using SharpTS.Runtime.BuiltIns;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of an HTTP IncomingMessage (request).
/// Extends SharpTSReadable for full Readable stream semantics (data, end, error events).
/// </summary>
/// <remarks>
/// Wraps an HttpListenerRequest to provide the Node.js IncomingMessage interface.
/// Properties: method, url, headers, httpVersion, statusCode, statusMessage
/// Inherits stream methods: on, once, pipe, read, pause, resume, etc.
/// </remarks>
public class SharpTSHttpRequest : SharpTSReadable
{
    /// <inheritdoc />
    public override TypeCategory RuntimeCategory => TypeCategory.Record;

    private readonly HttpListenerRequest _request;
    private bool _bodyRead;
    private bool _endEmitted;

    /// <summary>
    /// Creates a new IncomingMessage wrapping an HttpListenerRequest.
    /// </summary>
    public SharpTSHttpRequest(HttpListenerRequest request)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
    }

    /// <summary>
    /// HTTP method (GET, POST, etc.).
    /// </summary>
    public string Method => _request.HttpMethod;

    /// <summary>
    /// Request URL path (e.g., "/api/users").
    /// </summary>
    public string Url => _request.RawUrl ?? "/";

    /// <summary>
    /// HTTP version string (e.g., "1.1").
    /// </summary>
    public string HttpVersion => $"{_request.ProtocolVersion.Major}.{_request.ProtocolVersion.Minor}";

    /// <summary>
    /// Gets a member (property or method) by name.
    /// Overrides Readable.GetMember to add HTTP-specific properties.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            // HTTP-specific properties
            "method" => Method,
            "url" => Url,
            "httpVersion" => HttpVersion,
            "headers" => GetHeadersObject(),
            "rawHeaders" => GetRawHeaders(),
            "socket" => CreateSocketObject(),
            "complete" => _bodyRead && _endEmitted,
            "statusCode" => (double)_request.ProtocolVersion.Major, // For response messages
            "statusMessage" => (object?)null,
            "trailers" => new SharpTSObject(new Dictionary<string, object?>()),
            "aborted" => false,

            // Inherit Readable stream methods (on, once, pipe, read, pause, resume, etc.)
            _ => base.GetMember(name)
        };
    }

    /// <summary>
    /// Emits data and end events by reading the request body.
    /// Called internally when the request is processed.
    /// </summary>
    internal async Task EmitDataEventsAsync(Interpreter interpreter)
    {
        if (_bodyRead) return;

        try
        {
            using var ms = new MemoryStream();
            await _request.InputStream.CopyToAsync(ms);
            var body = ms.ToArray();
            _bodyRead = true;

            // Push data into the Readable stream (triggers 'data' events if flowing)
            if (body.Length > 0)
            {
                var pushMethod = base.GetMember("push") as BuiltInMethod;
                pushMethod?.Bind(this).Call(interpreter, [new SharpTSBuffer(body)]);
            }

            // Signal EOF
            var pushNull = base.GetMember("push") as BuiltInMethod;
            pushNull?.Bind(this).Call(interpreter, new List<object?> { null });
            _endEmitted = true;
        }
        catch (Exception ex)
        {
            // Emit 'error' event through the Readable stream
            EmitEvent(interpreter, "error", [new SharpTSError(ex.Message)]);
        }
    }

    /// <summary>
    /// Gets the headers as a SharpTSObject.
    /// </summary>
    private SharpTSObject GetHeadersObject()
    {
        var headers = new Dictionary<string, object?>();
        foreach (string? key in _request.Headers.AllKeys)
        {
            if (key != null)
            {
                headers[key.ToLowerInvariant()] = _request.Headers[key];
            }
        }
        return new SharpTSObject(headers);
    }

    /// <summary>
    /// Gets raw headers as alternating [name, value] array.
    /// </summary>
    private SharpTSArray GetRawHeaders()
    {
        var raw = new List<object?>();
        foreach (string? key in _request.Headers.AllKeys)
        {
            if (key != null)
            {
                raw.Add(key);
                raw.Add(_request.Headers[key]);
            }
        }
        return new SharpTSArray(raw);
    }

    /// <summary>
    /// Creates a minimal socket object for compatibility.
    /// </summary>
    private SharpTSObject CreateSocketObject()
    {
        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["remoteAddress"] = _request.RemoteEndPoint?.Address?.ToString(),
            ["remotePort"] = (double?)_request.RemoteEndPoint?.Port,
            ["localAddress"] = _request.LocalEndPoint?.Address?.ToString(),
            ["localPort"] = (double?)_request.LocalEndPoint?.Port
        });
    }

    public override string ToString() => $"IncomingMessage {{ method: '{Method}', url: '{Url}' }}";
}
