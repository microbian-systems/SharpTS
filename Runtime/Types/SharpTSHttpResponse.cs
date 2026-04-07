using System.Net;
using System.Text;
using SharpTS.Execution;
using SharpTS.Runtime.BuiltIns;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of an HTTP ServerResponse.
/// Extends SharpTSWritable for full Writable stream semantics (write, end, drain, finish events).
/// </summary>
/// <remarks>
/// Wraps an HttpListenerResponse to provide the Node.js ServerResponse interface.
/// Properties: statusCode, statusMessage, headersSent, finished
/// Methods: writeHead, write, end, setHeader, getHeader, removeHeader, getHeaderNames, hasHeader
/// Inherits stream methods: on, once, cork, uncork, destroy, etc.
/// </remarks>
public class SharpTSHttpResponse : SharpTSWritable, ITypeCategorized
{
    /// <inheritdoc />
    public override TypeCategory RuntimeCategory => TypeCategory.Record;

    private readonly HttpListenerResponse _response;
    private bool _headersSent;
    private bool _finished;
    private readonly List<byte> _bodyBuffer = new();
    private readonly Dictionary<string, string> _pendingHeaders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new ServerResponse wrapping an HttpListenerResponse.
    /// </summary>
    public SharpTSHttpResponse(HttpListenerResponse response)
    {
        _response = response ?? throw new ArgumentNullException(nameof(response));
    }

    /// <summary>
    /// Gets or sets the HTTP status code.
    /// </summary>
    public double StatusCode
    {
        get => _response.StatusCode;
        set => _response.StatusCode = (int)value;
    }

    /// <summary>
    /// Gets or sets the status message.
    /// </summary>
    public string StatusMessage
    {
        get => _response.StatusDescription;
        set => _response.StatusDescription = value;
    }

    /// <summary>
    /// Whether headers have been sent.
    /// </summary>
    public bool HeadersSent => _headersSent;

    /// <summary>
    /// Whether the response has finished.
    /// </summary>
    public bool Finished => _finished;

    /// <summary>
    /// Gets a member (property or method) by name.
    /// Overrides Writable.GetMember to add HTTP-specific methods.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            // HTTP-specific properties
            "statusCode" => StatusCode,
            "statusMessage" => StatusMessage,
            "headersSent" => HeadersSent,
            "finished" => Finished,
            "sendDate" => true,
            "connection" => (object?)null,
            "socket" => (object?)null,

            // HTTP-specific methods
            "writeHead" => new BuiltInMethod("writeHead", 1, 3, (interp, receiver, args) =>
            {
                if (receiver is SharpTSHttpResponse res) return res.WriteHead(interp, args);
                return receiver;
            }).Bind(this),
            "write" => new BuiltInMethod("write", 1, 3, (interp, receiver, args) =>
            {
                if (receiver is SharpTSHttpResponse res) return res.WriteData(interp, args);
                return true;
            }).Bind(this),
            "end" => new BuiltInMethod("end", 0, 3, (interp, receiver, args) =>
            {
                if (receiver is SharpTSHttpResponse res) return res.EndResponse(interp, args);
                return receiver;
            }).Bind(this),
            "setHeader" => new BuiltInMethod("setHeader", 2, (interp, receiver, args) =>
            {
                if (receiver is SharpTSHttpResponse res) return res.SetHeader(args);
                return receiver;
            }).Bind(this),
            "getHeader" => new BuiltInMethod("getHeader", 1, (interp, receiver, args) =>
            {
                if (receiver is SharpTSHttpResponse res) return res.GetHeader(args);
                return SharpTSUndefined.Instance;
            }).Bind(this),
            "removeHeader" => new BuiltInMethod("removeHeader", 1, (interp, receiver, args) =>
            {
                if (receiver is SharpTSHttpResponse res) return res.RemoveHeader(args);
                return receiver;
            }).Bind(this),
            "getHeaderNames" => new BuiltInMethod("getHeaderNames", 0, (interp, receiver, args) =>
            {
                return new SharpTSArray(_pendingHeaders.Keys.Select(k => (object?)k.ToLowerInvariant()).ToList());
            }).Bind(this),
            "hasHeader" => new BuiltInMethod("hasHeader", 1, (interp, receiver, args) =>
            {
                var headerName = args.Count > 0 ? args[0]?.ToString() : null;
                return headerName != null && _pendingHeaders.ContainsKey(headerName);
            }).Bind(this),
            "flushHeaders" => new BuiltInMethod("flushHeaders", 0, (interp, receiver, args) =>
            {
                FlushHeaders();
                return null;
            }).Bind(this),
            "writeContinue" => new BuiltInMethod("writeContinue", 0, (interp, receiver, args) => null).Bind(this),

            // Inherit Writable stream methods (on, once, cork, uncork, etc.)
            _ => base.GetMember(name)
        };
    }

    /// <summary>
    /// Sets a member (property) by name.
    /// </summary>
    /// <summary>
    /// Sets a member (property) by name.
    /// </summary>
    public void SetMember(string name, object? value)
    {
        switch (name)
        {
            case "statusCode":
                if (value is double d)
                    StatusCode = d;
                break;
            case "statusMessage":
                if (value is string s)
                    StatusMessage = s;
                break;
        }
    }

    /// <summary>
    /// Writes the status code and headers.
    /// </summary>
    private object? WriteHead(Interpreter interpreter, List<object?> args)
    {
        if (_headersSent)
            throw new Exception("Runtime Error: Cannot call writeHead after headers have been sent");

        if (args.Count == 0 || args[0] is not double statusCode)
            throw new Exception("Runtime Error: writeHead requires a status code");

        _response.StatusCode = (int)statusCode;

        // Optional status message (string as second arg)
        int headersArgIdx = 1;
        if (args.Count > 1 && args[1] is string statusMsg)
        {
            _response.StatusDescription = statusMsg;
            headersArgIdx = 2;
        }

        // Optional headers
        if (args.Count > headersArgIdx && args[headersArgIdx] is SharpTSObject headers)
        {
            foreach (var kv in headers.Fields)
            {
                SetResponseHeader(kv.Key, kv.Value?.ToString() ?? "");
            }
        }

        return this;
    }

    /// <summary>
    /// Writes data to the response body.
    /// </summary>
    private object? WriteData(Interpreter interpreter, List<object?> args)
    {
        if (_finished)
            throw new Exception("Runtime Error: Cannot write after response has ended");

        if (args.Count == 0) return true;

        byte[] data;
        var encoding = args.Count > 1 && args[1] is string enc ? enc : "utf8";

        switch (args[0])
        {
            case string str:
                data = GetEncoding(encoding).GetBytes(str);
                break;
            case SharpTSBuffer buffer:
                data = buffer.Data;
                break;
            default:
                data = Encoding.UTF8.GetBytes(args[0]?.ToString() ?? "");
                break;
        }

        _bodyBuffer.AddRange(data);

        // Call write callback if provided
        ISharpTSCallable? callback = null;
        if (args.Count > 1 && args[1] is ISharpTSCallable cb1) callback = cb1;
        if (args.Count > 2 && args[2] is ISharpTSCallable cb2) callback = cb2;
        callback?.Call(interpreter, []);

        return true;
    }

    /// <summary>
    /// Ends the response, optionally writing final data.
    /// </summary>
    private object? EndResponse(Interpreter interpreter, List<object?> args)
    {
        if (_finished) return this;

        // Write any final data
        if (args.Count > 0 && args[0] != null && args[0] is not ISharpTSCallable)
        {
            WriteData(interpreter, args);
        }

        // Flush pending headers
        FlushHeaders();

        // Actually send the response
        try
        {
            _headersSent = true;
            _response.ContentLength64 = _bodyBuffer.Count;
            if (_bodyBuffer.Count > 0)
            {
                _response.OutputStream.Write(_bodyBuffer.ToArray(), 0, _bodyBuffer.Count);
            }
            _response.OutputStream.Close();
        }
        catch
        {
            // Client may have disconnected
        }

        _finished = true;

        // Call callback
        ISharpTSCallable? callback = null;
        foreach (var arg in args)
        {
            if (arg is ISharpTSCallable cb) { callback = cb; break; }
        }
        callback?.Call(interpreter, []);

        // Emit 'finish' event (Writable stream semantics)
        EmitEvent(interpreter, "finish", []);
        // Emit 'close' event
        EmitEvent(interpreter, "close", []);

        return this;
    }

    /// <summary>
    /// Sets a single header value.
    /// </summary>
    private object? SetHeader(List<object?> args)
    {
        if (_headersSent)
            throw new Exception("Runtime Error: Cannot set header after headers have been sent");

        if (args.Count < 2)
            throw new Exception("Runtime Error: setHeader requires name and value");

        var name = args[0]?.ToString() ?? throw new Exception("Runtime Error: header name must be a string");
        var value = args[1]?.ToString() ?? "";

        _pendingHeaders[name] = value;
        SetResponseHeader(name, value);
        return this;
    }

    /// <summary>
    /// Gets a header value.
    /// </summary>
    private object? GetHeader(List<object?> args)
    {
        if (args.Count == 0) return SharpTSUndefined.Instance;

        var name = args[0]?.ToString();
        if (name == null) return SharpTSUndefined.Instance;

        if (_pendingHeaders.TryGetValue(name, out var val))
            return val;

        var headerValue = _response.Headers[name];
        return string.IsNullOrEmpty(headerValue) ? SharpTSUndefined.Instance : (object)headerValue;
    }

    /// <summary>
    /// Removes a header.
    /// </summary>
    private object? RemoveHeader(List<object?> args)
    {
        if (_headersSent)
            throw new Exception("Runtime Error: Cannot remove header after headers have been sent");

        if (args.Count == 0) return this;

        var name = args[0]?.ToString();
        if (name != null)
        {
            _pendingHeaders.Remove(name);
            _response.Headers.Remove(name);
        }
        return this;
    }

    /// <summary>
    /// Flushes pending headers to the response.
    /// </summary>
    private void FlushHeaders()
    {
        foreach (var (name, value) in _pendingHeaders)
        {
            SetResponseHeader(name, value);
        }
    }

    /// <summary>
    /// Sets a response header, handling special cases.
    /// </summary>
    private void SetResponseHeader(string name, string value)
    {
        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            _response.ContentType = value;
        }
        else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(value, out var length))
                _response.ContentLength64 = length;
        }
        else
        {
            _response.Headers[name] = value;
        }
    }

    /// <summary>
    /// Gets the encoding by name.
    /// </summary>
    private static Encoding GetEncoding(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "utf8" or "utf-8" => Encoding.UTF8,
            "ascii" => Encoding.ASCII,
            "latin1" or "binary" => Encoding.Latin1,
            "utf16le" or "ucs2" or "ucs-2" => Encoding.Unicode,
            _ => Encoding.UTF8
        };
    }

    public override string ToString() => $"ServerResponse {{ statusCode: {StatusCode} }}";
}
