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
public class SharpTSHttpResponse : SharpTSWritable
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
            "writeHead" => BuiltInMethod.CreateV2("writeHead", 1, 3, (interp, receiver, args) =>
            {
                if (receiver.ToObject() is SharpTSHttpResponse res) return res.WriteHead(interp, args);
                return receiver;
            }).Bind(this),
            "write" => BuiltInMethod.CreateV2("write", 1, 3, (interp, receiver, args) =>
            {
                if (receiver.ToObject() is SharpTSHttpResponse res) return res.WriteData(interp, args);
                return RuntimeValue.True;
            }).Bind(this),
            "end" => BuiltInMethod.CreateV2("end", 0, 3, (interp, receiver, args) =>
            {
                if (receiver.ToObject() is SharpTSHttpResponse res) return res.EndResponse(interp, args);
                return receiver;
            }).Bind(this),
            "setHeader" => BuiltInMethod.CreateV2("setHeader", 2, (_, receiver, args) =>
            {
                if (receiver.ToObject() is SharpTSHttpResponse res) return res.SetHeader(args);
                return receiver;
            }).Bind(this),
            "getHeader" => BuiltInMethod.CreateV2("getHeader", 1, (_, receiver, args) =>
            {
                if (receiver.ToObject() is SharpTSHttpResponse res) return res.GetHeader(args);
                return RuntimeValue.Undefined;
            }).Bind(this),
            "removeHeader" => BuiltInMethod.CreateV2("removeHeader", 1, (_, receiver, args) =>
            {
                if (receiver.ToObject() is SharpTSHttpResponse res) return res.RemoveHeader(args);
                return receiver;
            }).Bind(this),
            "getHeaderNames" => BuiltInMethod.CreateV2("getHeaderNames", 0, (_, _, _) =>
            {
                return RuntimeValue.FromObject(new SharpTSArray(_pendingHeaders.Keys.Select(k => (object?)k.ToLowerInvariant()).ToList()));
            }).Bind(this),
            "hasHeader" => BuiltInMethod.CreateV2("hasHeader", 1, (_, _, args) =>
            {
                var headerName = args.Length > 0 ? args[0].ToObject()?.ToString() : null;
                return RuntimeValue.FromBoolean(headerName != null && _pendingHeaders.ContainsKey(headerName));
            }).Bind(this),
            "flushHeaders" => BuiltInMethod.CreateV2("flushHeaders", 0, (_, _, _) =>
            {
                FlushHeaders();
                return RuntimeValue.Null;
            }).Bind(this),
            "writeContinue" => BuiltInMethod.CreateV2("writeContinue", 0, (_, _, _) => RuntimeValue.Null).Bind(this),

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
    private RuntimeValue WriteHead(Interpreter interpreter, ReadOnlySpan<RuntimeValue> args)
    {
        if (_headersSent)
            throw new Exception("Runtime Error: Cannot call writeHead after headers have been sent");

        if (args.Length == 0 || !args[0].IsNumber)
            throw new Exception("Runtime Error: writeHead requires a status code");

        _response.StatusCode = (int)args[0].AsNumberUnsafe();

        // Optional status message (string as second arg)
        int headersArgIdx = 1;
        if (args.Length > 1 && args[1].IsString)
        {
            _response.StatusDescription = args[1].AsStringUnsafe();
            headersArgIdx = 2;
        }

        // Optional headers
        if (args.Length > headersArgIdx && args[headersArgIdx].ToObject() is SharpTSObject headers)
        {
            foreach (var kv in headers.Fields)
            {
                SetResponseHeader(kv.Key, kv.Value?.ToString() ?? "");
            }
        }

        return RuntimeValue.FromObject(this);
    }

    /// <summary>
    /// Writes data to the response body.
    /// </summary>
    private RuntimeValue WriteData(Interpreter interpreter, ReadOnlySpan<RuntimeValue> args)
    {
        if (_finished)
            throw new Exception("Runtime Error: Cannot write after response has ended");

        if (args.Length == 0) return RuntimeValue.True;

        byte[] data;
        var encoding = args.Length > 1 && args[1].IsString ? args[1].AsStringUnsafe() : "utf8";

        switch (args[0].ToObject())
        {
            case string str:
                data = GetEncoding(encoding).GetBytes(str);
                break;
            case SharpTSBuffer buffer:
                data = buffer.Data;
                break;
            default:
                data = Encoding.UTF8.GetBytes(args[0].ToObject()?.ToString() ?? "");
                break;
        }

        _bodyBuffer.AddRange(data);

        // Call write callback if provided
        ISharpTSCallable? callback = null;
        if (args.Length > 1 && args[1].ToObject() is ISharpTSCallable cb1) callback = cb1;
        if (args.Length > 2 && args[2].ToObject() is ISharpTSCallable cb2) callback = cb2;
        callback?.Call(interpreter, []);

        return RuntimeValue.True;
    }

    /// <summary>
    /// Ends the response, optionally writing final data.
    /// </summary>
    private RuntimeValue EndResponse(Interpreter interpreter, ReadOnlySpan<RuntimeValue> args)
    {
        if (_finished) return RuntimeValue.FromObject(this);

        // Write any final data
        if (args.Length > 0 && args[0].ToObject() is { } first && first is not ISharpTSCallable)
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
            if (arg.ToObject() is ISharpTSCallable cb) { callback = cb; break; }
        }
        callback?.Call(interpreter, []);

        // Emit 'finish' event (Writable stream semantics)
        EmitEvent(interpreter, "finish", []);
        // Emit 'close' event
        EmitEvent(interpreter, "close", []);

        return RuntimeValue.FromObject(this);
    }

    /// <summary>
    /// Sets a single header value.
    /// </summary>
    private RuntimeValue SetHeader(ReadOnlySpan<RuntimeValue> args)
    {
        if (_headersSent)
            throw new Exception("Runtime Error: Cannot set header after headers have been sent");

        if (args.Length < 2)
            throw new Exception("Runtime Error: setHeader requires name and value");

        var name = args[0].ToObject()?.ToString() ?? throw new Exception("Runtime Error: header name must be a string");
        var value = args[1].ToObject()?.ToString() ?? "";

        _pendingHeaders[name] = value;
        SetResponseHeader(name, value);
        return RuntimeValue.FromObject(this);
    }

    /// <summary>
    /// Gets a header value.
    /// </summary>
    private RuntimeValue GetHeader(ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0) return RuntimeValue.Undefined;

        var name = args[0].ToObject()?.ToString();
        if (name == null) return RuntimeValue.Undefined;

        if (_pendingHeaders.TryGetValue(name, out var val))
            return RuntimeValue.FromString(val);

        var headerValue = _response.Headers[name];
        return string.IsNullOrEmpty(headerValue) ? RuntimeValue.Undefined : RuntimeValue.FromString(headerValue);
    }

    /// <summary>
    /// Removes a header.
    /// </summary>
    private RuntimeValue RemoveHeader(ReadOnlySpan<RuntimeValue> args)
    {
        if (_headersSent)
            throw new Exception("Runtime Error: Cannot remove header after headers have been sent");

        if (args.Length == 0) return RuntimeValue.FromObject(this);

        var name = args[0].ToObject()?.ToString();
        if (name != null)
        {
            _pendingHeaders.Remove(name);
            _response.Headers.Remove(name);
        }
        return RuntimeValue.FromObject(this);
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
