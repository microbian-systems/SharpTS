using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of a Node.js-compatible Duplex stream.
/// Combines both Readable and Writable capabilities.
/// </summary>
/// <remarks>
/// Extends <see cref="SharpTSReadable"/> and adds Writable-side methods.
/// The read and write sides operate independently.
/// </remarks>
public class SharpTSDuplex : SharpTSReadable
{
    // Writable-side state
    private bool _writable = true;
    private bool _writableEnded;
    private bool _writableFinished;
    private bool _writableDestroyed;
    private bool _corked;
    private readonly List<object?> _corkBuffer = [];
    private ISharpTSCallable? _writeCallback;
    private ISharpTSCallable? _finalCallback;
    private ISharpTSCallable? _readCallback;
    private int _writableHighWaterMark = 16384;
    private int _pendingWrites;
    private int _writableLength;
    private bool _needDrain;
    private bool _writableObjectMode;

    /// <summary>
    /// Sets the custom write callback (from constructor options).
    /// </summary>
    public void SetWriteCallback(ISharpTSCallable callback) => _writeCallback = callback;

    /// <summary>
    /// Sets the custom final callback (from constructor options).
    /// </summary>
    public void SetFinalCallback(ISharpTSCallable callback) => _finalCallback = callback;

    /// <summary>
    /// Sets the custom read callback (from constructor options).
    /// </summary>
    public void SetReadCallback(ISharpTSCallable callback) => _readCallback = callback;

    /// <summary>
    /// Gets or sets the writable-side high water mark.
    /// </summary>
    public int WritableHighWaterMark { get => _writableHighWaterMark; set => _writableHighWaterMark = value; }

    /// <summary>
    /// Gets or sets whether the writable side operates in object mode.
    /// </summary>
    public bool WritableObjectMode { get => _writableObjectMode; set => _writableObjectMode = value; }

    /// <summary>
    /// Gets a member (method or property) by name for interpreter dispatch.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            // Writable-side methods
            "write" => BuiltInMethod.CreateV2("write", 1, 3, Write),
            "end" => BuiltInMethod.CreateV2("end", 0, 3, End),
            "cork" => BuiltInMethod.CreateV2("cork", 0, Cork),
            "uncork" => BuiltInMethod.CreateV2("uncork", 0, Uncork),

            // Writable-side properties
            "writable" => _writable && !_writableEnded && !_writableDestroyed,
            "writableEnded" => _writableEnded,
            "writableFinished" => _writableFinished,
            "writableLength" => (double)_writableLength,
            "writableCorked" => (double)(_corked ? 1 : 0),
            "writableHighWaterMark" => (double)_writableHighWaterMark,
            "writableObjectMode" => _writableObjectMode,

            // Override destroy to handle both sides
            "destroy" => BuiltInMethod.CreateV2("destroy", 0, 1, DestroyDuplex),

            // Inherit Readable methods and properties
            _ => base.GetMember(name)
        };
    }

    private RuntimeValue Write(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (_writableDestroyed || _writableEnded)
        {
            EmitEvent(interpreter, "error", ["write after end"]);
            return RuntimeValue.False;
        }

        var chunk = args.Length > 0 ? args[0].ToObject() : null;
        string? encoding = null;
        ISharpTSCallable? callback = null;

        if (args.Length > 1)
        {
            if (args[1].IsString)
            {
                encoding = args[1].AsStringUnsafe();
                if (args.Length > 2 && args[2].ToObject() is ISharpTSCallable cb)
                {
                    callback = cb;
                }
            }
            else if (args[1].ToObject() is ISharpTSCallable cb)
            {
                callback = cb;
            }
        }

        if (_corked)
        {
            _corkBuffer.Add(new WriteChunk(chunk, encoding, callback));
            return RuntimeValue.False;
        }

        return RuntimeValue.FromBoxed(DoWrite(interpreter, chunk, encoding, callback));
    }

    private record WriteChunk(object? Chunk, string? Encoding, ISharpTSCallable? Callback);

    private object? DoWrite(Interp interpreter, object? chunk, string? encoding, ISharpTSCallable? callback)
    {
        _pendingWrites++;
        var chunkSize = SharpTSWritable.GetChunkSize(chunk, _writableObjectMode);
        _writableLength += chunkSize;

        if (_writeCallback != null)
        {
            var cbWrapper = new WriteCallbackWrapper(callback, interpreter, this, chunkSize);
            var writeArgs = new List<object?> { chunk, encoding ?? "utf8", cbWrapper };
            try
            {
                _writeCallback.Call(interpreter, writeArgs);
            }
            catch (Exception ex)
            {
                EmitEvent(interpreter, "error", [ex.Message]);
                return false;
            }
        }
        else
        {
            // Sync completion
            _pendingWrites--;
            _writableLength -= chunkSize;
            callback?.Call(interpreter, []);
            CheckDrain(interpreter);
        }

        // Return false when buffered data exceeds highWaterMark (backpressure)
        if (_writableLength >= _writableHighWaterMark)
        {
            _needDrain = true;
            return false;
        }

        return true;
    }

    private void CheckDrain(Interp interpreter)
    {
        if (_needDrain && _writableLength < _writableHighWaterMark)
        {
            _needDrain = false;
            EmitEvent(interpreter, "drain", []);
        }
    }

    private RuntimeValue End(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (_writableEnded)
        {
            return RuntimeValue.FromObject(this);
        }

        object? chunk = null;
        string? encoding = null;
        ISharpTSCallable? callback = null;

        if (args.Length > 0)
        {
            if (args[0].ToObject() is ISharpTSCallable cb0)
            {
                callback = cb0;
            }
            else
            {
                chunk = args[0].ToObject();
                if (args.Length > 1)
                {
                    if (args[1].IsString)
                    {
                        encoding = args[1].AsStringUnsafe();
                        if (args.Length > 2 && args[2].ToObject() is ISharpTSCallable cb)
                        {
                            callback = cb;
                        }
                    }
                    else if (args[1].ToObject() is ISharpTSCallable cb)
                    {
                        callback = cb;
                    }
                }
            }
        }

        _writableEnded = true;
        _writable = false;

        if (chunk != null)
        {
            DoWrite(interpreter, chunk, encoding, null);
        }

        if (_corked)
        {
            Uncork(interpreter, RuntimeValue.Null, []);
        }

        if (_finalCallback != null)
        {
            var finalCbWrapper = new WriteCallbackWrapper(null, interpreter, this, 0);
            try
            {
                _finalCallback.Call(interpreter, [finalCbWrapper]);
            }
            catch (Exception ex)
            {
                EmitEvent(interpreter, "error", [ex.Message]);
            }
        }

        _writableFinished = true;
        callback?.Call(interpreter, []);
        EmitEvent(interpreter, "finish", []);

        return RuntimeValue.FromObject(this);
    }

    private RuntimeValue Cork(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        _corked = true;
        return RuntimeValue.Null;
    }

    private RuntimeValue Uncork(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (!_corked)
        {
            return RuntimeValue.Null;
        }

        _corked = false;

        foreach (var item in _corkBuffer.Cast<WriteChunk>())
        {
            DoWrite(interpreter, item.Chunk, item.Encoding, item.Callback);
        }
        _corkBuffer.Clear();

        return RuntimeValue.Null;
    }

    private RuntimeValue DestroyDuplex(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        _writableDestroyed = true;
        _writable = false;
        _corkBuffer.Clear();

        // Destroy the readable side too via the base Destroy method
        var baseDestroy = base.GetMember("destroy") as BuiltInMethod;
        baseDestroy?.Bind(this).CallV2(interpreter, args);

        return RuntimeValue.FromObject(this);
    }

    public override string ToString() => "Duplex {}";

    private class WriteCallbackWrapper : ISharpTSCallable
    {
        private readonly ISharpTSCallable? _callback;
        private readonly Interp _interpreter;
        private readonly SharpTSDuplex _stream;
        private readonly int _chunkSize;

        public WriteCallbackWrapper(ISharpTSCallable? callback, Interp interpreter, SharpTSDuplex stream, int chunkSize)
        {
            _callback = callback;
            _interpreter = interpreter;
            _stream = stream;
            _chunkSize = chunkSize;
        }

        public int Arity() => 1;

        public object? Call(Interp interpreter, List<object?> arguments)
        {
            _stream._pendingWrites--;
            _stream._writableLength -= _chunkSize;
            _callback?.Call(_interpreter, []);
            _stream.CheckDrain(_interpreter);
            return null;
        }
    }
}
