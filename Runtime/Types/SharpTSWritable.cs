using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of a Node.js-compatible Writable stream.
/// Provides sync write mode with optional custom write callback.
/// </summary>
/// <remarks>
/// Extends <see cref="SharpTSEventEmitter"/> for event support (drain, finish, error, close).
/// </remarks>
public class SharpTSWritable : SharpTSEventEmitter
{
    private bool _writable = true;
    private bool _ended;
    private bool _finished;
    private bool _destroyed;
    private bool _corked;
    private readonly List<object?> _corkBuffer = [];
    private ISharpTSCallable? _writeCallback;
    private ISharpTSCallable? _finalCallback;
    private ISharpTSCallable? _destroyCallback;
    private int _highWaterMark = 16384;
    private int _pendingWrites;
    private int _writableLength; // total bytes of in-flight writes (backpressure tracking)
    private bool _needDrain;
    private bool _objectMode;
    private bool _autoDestroy;
    private bool _errored;

    /// <summary>
    /// Whether this stream has errored — backs stream.isErrored (#1030).
    /// </summary>
    public bool Errored => _errored;

    /// <summary>
    /// Gets or sets whether this stream operates in object mode.
    /// </summary>
    public bool ObjectMode { get => _objectMode; set => _objectMode = value; }

    /// <summary>
    /// Gets or sets whether the stream auto-destroys after finishing.
    /// </summary>
    public bool AutoDestroy { get => _autoDestroy; set => _autoDestroy = value; }

    /// <summary>
    /// Gets or sets the high water mark for this stream.
    /// </summary>
    public int HighWaterMark { get => _highWaterMark; set => _highWaterMark = value; }

    /// <summary>
    /// Sets the custom write callback (from constructor options).
    /// </summary>
    public void SetWriteCallback(ISharpTSCallable callback) => _writeCallback = callback;

    /// <summary>
    /// Sets the custom final callback (from constructor options).
    /// </summary>
    public void SetFinalCallback(ISharpTSCallable callback) => _finalCallback = callback;

    /// <summary>
    /// Sets the custom destroy callback (from constructor options).
    /// </summary>
    public void SetDestroyCallback(ISharpTSCallable callback) => _destroyCallback = callback;

    /// <summary>
    /// Gets a member (method or property) by name for interpreter dispatch.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            // Writable-specific methods
            "write" => BuiltInMethod.CreateV2("write", 1, 3, Write),
            "end" => BuiltInMethod.CreateV2("end", 0, 3, End),
            "cork" => BuiltInMethod.CreateV2("cork", 0, Cork),
            "uncork" => BuiltInMethod.CreateV2("uncork", 0, Uncork),
            "destroy" => BuiltInMethod.CreateV2("destroy", 0, 1, Destroy),
            "setDefaultEncoding" => BuiltInMethod.CreateV2("setDefaultEncoding", 1, SetDefaultEncoding),

            // Properties
            "writable" => _writable && !_ended && !_destroyed,
            "writableEnded" => _ended,
            "writableFinished" => _finished,
            "writableLength" => (double)_writableLength,
            "writableCorked" => (double)(_corked ? 1 : 0),
            "writableHighWaterMark" => (double)_highWaterMark,
            "writableObjectMode" => _objectMode,
            "destroyed" => _destroyed,

            // Inherit from EventEmitter
            _ => base.GetMember(name)
        };
    }

    /// <summary>
    /// Writes data to the stream.
    /// </summary>
    private RuntimeValue Write(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (_destroyed || _ended)
        {
            EmitError(interpreter, "write after end");
            return RuntimeValue.False;
        }

        var chunk = args.Length > 0 ? args[0].ToObject() : null;
        string? encoding = null;
        ISharpTSCallable? callback = null;

        // Parse arguments: (chunk, encoding?, callback?)
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
        var chunkSize = GetChunkSize(chunk, _objectMode);
        _writableLength += chunkSize;

        if (_writeCallback != null)
        {
            // Custom write callback: (chunk, encoding, callback)
            var cbWrapper = new WriteCallbackWrapper(callback, interpreter, this, chunkSize);
            var writeArgs = new List<object?> { chunk, encoding ?? "utf8", cbWrapper };
            try
            {
                _writeCallback.Call(interpreter, writeArgs);
            }
            catch (Exception ex)
            {
                EmitError(interpreter, ex.Message);
                return false;
            }
        }
        else
        {
            // Default behavior: just accept the data (sync completion)
            _pendingWrites--;
            _writableLength -= chunkSize;
            callback?.Call(interpreter, []);
            CheckDrain(interpreter);
        }

        // Return false when buffered data exceeds highWaterMark (backpressure)
        if (_writableLength >= _highWaterMark)
        {
            _needDrain = true;
            return false;
        }

        return true;
    }

    private void CheckDrain(Interp interpreter)
    {
        if (_needDrain && _writableLength < _highWaterMark)
        {
            _needDrain = false;
            EmitEvent(interpreter, "drain", []);
        }
    }

    /// <summary>
    /// Gets the byte size of a chunk (or 1 for object mode).
    /// </summary>
    internal static int GetChunkSize(object? chunk, bool objectMode)
    {
        if (objectMode) return 1;
        return chunk switch
        {
            string s => s.Length,
            SharpTSBuffer buf => buf.Length,
            _ => 0
        };
    }

    /// <summary>
    /// Internal write method for piped data. Returns false on backpressure.
    /// </summary>
    internal bool WriteInternal(Interp interpreter, object? chunk, string? encoding)
    {
        if (_destroyed || _ended)
        {
            return false;
        }

        return (bool)(DoWrite(interpreter, chunk, encoding, null) ?? false);
    }

    /// <summary>
    /// Ends the stream, optionally writing final data.
    /// </summary>
    private RuntimeValue End(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (_ended)
        {
            return RuntimeValue.FromObject(this);
        }

        object? chunk = null;
        string? encoding = null;
        ISharpTSCallable? callback = null;

        // Parse arguments: (chunk?, encoding?, callback?)
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

        _ended = true;
        _writable = false;

        // Write final chunk if provided
        if (chunk != null)
        {
            DoWrite(interpreter, chunk, encoding, null);
        }

        // Flush cork buffer
        if (_corked)
        {
            Uncork(interpreter, RuntimeValue.Null, []);
        }

        // Call final callback
        if (_finalCallback != null)
        {
            var finalCbWrapper = new WriteCallbackWrapper(null, interpreter, this, 0);
            try
            {
                _finalCallback.Call(interpreter, [finalCbWrapper]);
            }
            catch (Exception ex)
            {
                EmitError(interpreter, ex.Message);
            }
        }

        _finished = true;
        callback?.Call(interpreter, []);
        EmitFinish(interpreter);

        return RuntimeValue.FromObject(this);
    }

    /// <summary>
    /// Internal end method for piped streams.
    /// </summary>
    internal void EndInternal(Interp interpreter, object? chunk, string? encoding)
    {
        End(interpreter, RuntimeValue.FromObject(this),
            chunk != null
                ? [RuntimeValue.FromBoxed(chunk), RuntimeValue.FromBoxed(encoding)]
                : []);
    }

    /// <summary>
    /// Corks the stream, buffering all writes.
    /// </summary>
    private RuntimeValue Cork(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        _corked = true;
        return RuntimeValue.Null;
    }

    /// <summary>
    /// Uncorks the stream, flushing the buffer.
    /// </summary>
    private RuntimeValue Uncork(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (!_corked)
        {
            return RuntimeValue.Null;
        }

        _corked = false;

        // Flush the cork buffer
        foreach (var item in _corkBuffer.Cast<WriteChunk>())
        {
            DoWrite(interpreter, item.Chunk, item.Encoding, item.Callback);
        }
        _corkBuffer.Clear();

        return RuntimeValue.Null;
    }

    /// <summary>
    /// Destroys the stream.
    /// </summary>
    private RuntimeValue Destroy(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (_destroyed)
        {
            return RuntimeValue.FromObject(this);
        }

        _destroyed = true;
        _writable = false;
        _corkBuffer.Clear();

        if (_destroyCallback != null)
        {
            try
            {
                var err = args.Length > 0 ? args[0].ToObject() : null;
                _destroyCallback.Call(interpreter, [err, new DestroyCallbackWrapper(interpreter, this)]);
            }
            catch (Exception ex)
            {
                EmitError(interpreter, ex.Message);
            }
        }
        else
        {
            if (args.Length > 0 && args[0].ToObject() is { } error)
            {
                EmitError(interpreter, error);
            }
            EmitClose(interpreter);
        }

        return RuntimeValue.FromObject(this);
    }

    private RuntimeValue SetDefaultEncoding(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        // Just accept it for compatibility
        return RuntimeValue.FromObject(this);
    }

    private void EmitError(Interp interpreter, object? error)
    {
        _errored = true;
        EmitEvent(interpreter, "error", [error]);
    }

    private void EmitFinish(Interp interpreter)
    {
        EmitEvent(interpreter, "prefinish", []);
        EmitEvent(interpreter, "finish", []);
        if (_autoDestroy)
        {
            Destroy(interpreter, RuntimeValue.FromObject(this), []);
        }
    }

    private void EmitClose(Interp interpreter)
    {
        EmitEvent(interpreter, "close", []);
    }

    /// <summary>
    /// Resets mutable state for singleton reuse (e.g., process.stdout between interpreter runs).
    /// </summary>
    internal void ResetWritableState()
    {
        _writable = true;
        _ended = false;
        _finished = false;
        _destroyed = false;
        _corked = false;
        _corkBuffer.Clear();
        _pendingWrites = 0;
        _writableLength = 0;
        _needDrain = false;
        ClearAllListenersInternal();
    }

    public override string ToString() => "Writable {}";

    /// <summary>
    /// Wrapper for write callbacks to match Node.js callback(error?) signature.
    /// </summary>
    private class WriteCallbackWrapper : ISharpTSCallable
    {
        private readonly ISharpTSCallable? _callback;
        private readonly Interp _interpreter;
        private readonly SharpTSWritable _stream;
        private readonly int _chunkSize;

        public WriteCallbackWrapper(ISharpTSCallable? callback, Interp interpreter, SharpTSWritable stream, int chunkSize)
        {
            _callback = callback;
            _interpreter = interpreter;
            _stream = stream;
            _chunkSize = chunkSize;
        }

        public int Arity() => 0;

        public object? Call(Interp interpreter, List<object?> arguments)
        {
            // error argument
            var error = arguments.Count > 0 ? arguments[0] : null;
            // Decrement pending writes and buffered length
            _stream._pendingWrites--;
            _stream._writableLength -= _chunkSize;
            // Call the original callback
            _callback?.Call(_interpreter, []);
            // Check if we need to emit drain
            _stream.CheckDrain(_interpreter);
            return null;
        }
    }

    /// <summary>
    /// Wrapper for destroy callback to emit close event.
    /// </summary>
    private class DestroyCallbackWrapper : ISharpTSCallable
    {
        private readonly Interp _interpreter;
        private readonly SharpTSWritable _stream;

        public DestroyCallbackWrapper(Interp interpreter, SharpTSWritable stream)
        {
            _interpreter = interpreter;
            _stream = stream;
        }

        public int Arity() => 0;

        public object? Call(Interp interpreter, List<object?> arguments)
        {
            var error = arguments.Count > 0 ? arguments[0] : null;
            if (error != null)
            {
                _stream.EmitError(_interpreter, error);
            }
            _stream.EmitClose(_interpreter);
            return null;
        }
    }
}
