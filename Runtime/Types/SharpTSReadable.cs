using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of a Node.js-compatible Readable stream.
/// Provides sync push/pull mode for reading data with pipe() support.
/// </summary>
/// <remarks>
/// Extends <see cref="SharpTSEventEmitter"/> for event support (data, end, error, close).
/// Implements a simple pull-based read model suitable for sync operation.
/// </remarks>
public class SharpTSReadable : SharpTSEventEmitter
{
    private readonly Queue<object?> _readBuffer = new();
    private readonly List<object> _pipeDestinations = [];
    private bool _ended;
    private bool _destroyed;
    private string _encoding = "utf8";
    private bool _readable = true;
    private bool? _flowing; // null=initial, true=flowing, false=paused
    private bool _objectMode;
    private int _highWaterMark = 16384; // bytes for binary mode, 16 for object mode

    /// <summary>
    /// Gets or sets whether this stream operates in object mode.
    /// In object mode, chunks can be any JavaScript value (not just strings/Buffers).
    /// </summary>
    public bool ObjectMode
    {
        get => _objectMode;
        set
        {
            _objectMode = value;
            if (value && _highWaterMark == 16384)
                _highWaterMark = 16; // object mode default
        }
    }

    /// <summary>
    /// Gets or sets the high water mark for this stream.
    /// </summary>
    public int HighWaterMark { get => _highWaterMark; set => _highWaterMark = value; }

    /// <summary>
    /// Gets a member (method or property) by name for interpreter dispatch.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            // Readable-specific methods
            "read" => BuiltInMethod.CreateV2("read", 0, 1, Read),
            "push" => BuiltInMethod.CreateV2("push", 1, Push),
            "pipe" => BuiltInMethod.CreateV2("pipe", 1, 2, Pipe),
            "unpipe" => BuiltInMethod.CreateV2("unpipe", 0, 1, Unpipe),
            "setEncoding" => BuiltInMethod.CreateV2("setEncoding", 1, SetEncoding),
            "destroy" => BuiltInMethod.CreateV2("destroy", 0, 1, Destroy),
            "unshift" => BuiltInMethod.CreateV2("unshift", 1, Unshift),
            "pause" => BuiltInMethod.CreateV2("pause", 0, Pause),
            "resume" => BuiltInMethod.CreateV2("resume", 0, Resume),
            "isPaused" => BuiltInMethod.CreateV2("isPaused", 0, IsPaused),
            "toArray" => BuiltInMethod.CreateV2("toArray", 0, ToArray),
            "forEach" => BuiltInMethod.CreateV2("forEach", 1, ForEach),
            "map" => BuiltInMethod.CreateV2("map", 1, Map),
            "filter" => BuiltInMethod.CreateV2("filter", 1, Filter),

            // Wrap event methods to drain buffer after 'data' listener is added
            "on" or "addListener" => BuiltInMethod.CreateV2(name, 2, WrapOnForFlowing),
            "once" => BuiltInMethod.CreateV2("once", 2, WrapOnceForFlowing),

            // Properties
            "readable" => _readable && !_ended && !_destroyed,
            "readableEnded" => _ended,
            "readableLength" => (double)_readBuffer.Count,
            "readableHighWaterMark" => (double)_highWaterMark,
            "readableEncoding" => _encoding,
            "readableFlowing" => _flowing ?? (object)false,
            "readableObjectMode" => _objectMode,
            "destroyed" => _destroyed,

            // Inherit from EventEmitter
            _ => base.GetMember(name)
        };
    }

    private RuntimeValue WrapOnForFlowing(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        // Call base on
        var baseOn = base.GetMember("on") as BuiltInMethod;
        baseOn?.Bind(this).CallV2(interpreter, args);

        var eventName = args.Length > 0 ? args[0].ToObject()?.ToString() : null;

        // If a 'data' listener was added, drain the buffer
        if (eventName == "data" && _flowing == true && _readBuffer.Count > 0)
        {
            DrainBuffer(interpreter);
        }

        // If an 'end' listener was added on an already-ended stream, emit 'end' immediately
        if (eventName == "end" && _ended && _readBuffer.Count == 0)
        {
            EmitEvent(interpreter, "end", []);
        }

        return RuntimeValue.FromObject(this);
    }

    private RuntimeValue WrapOnceForFlowing(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        // Call base once
        var baseOnce = base.GetMember("once") as BuiltInMethod;
        baseOnce?.Bind(this).CallV2(interpreter, args);

        var eventName = args.Length > 0 ? args[0].ToObject()?.ToString() : null;

        // If a 'data' listener was added, drain the buffer
        if (eventName == "data" && _flowing == true && _readBuffer.Count > 0)
        {
            DrainBuffer(interpreter);
        }

        // If an 'end' listener was added on an already-ended stream, emit 'end' immediately
        if (eventName == "end" && _ended && _readBuffer.Count == 0)
        {
            EmitEvent(interpreter, "end", []);
        }

        return RuntimeValue.FromObject(this);
    }

    /// <summary>
    /// Reads data from the stream.
    /// </summary>
    private RuntimeValue Read(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (_destroyed || _readBuffer.Count == 0)
        {
            return RuntimeValue.Null;
        }

        // In object mode, always return one object at a time (size parameter is ignored)
        if (_objectMode)
        {
            return RuntimeValue.FromBoxed(_readBuffer.Dequeue());
        }

        int? size = null;
        if (args.Length > 0 && args[0].IsNumber)
        {
            size = (int)args[0].AsNumberUnsafe();
        }

        if (size == null || size <= 0)
        {
            // Read all available data
            if (_readBuffer.Count == 0)
            {
                return RuntimeValue.Null;
            }

            var chunks = new List<object?>();
            while (_readBuffer.Count > 0)
            {
                chunks.Add(_readBuffer.Dequeue());
            }

            // Concatenate all chunks
            return RuntimeValue.FromBoxed(ConcatenateChunks(chunks));
        }
        else
        {
            // Read specified amount
            return RuntimeValue.FromBoxed(ReadSize(size.Value));
        }
    }

    private object? ReadSize(int size)
    {
        if (_readBuffer.Count == 0)
        {
            return null;
        }

        var chunks = new List<object?>();
        int totalRead = 0;

        while (_readBuffer.Count > 0 && totalRead < size)
        {
            var chunk = _readBuffer.Peek();
            var chunkLength = GetChunkLength(chunk);

            if (totalRead + chunkLength <= size)
            {
                chunks.Add(_readBuffer.Dequeue());
                totalRead += chunkLength;
            }
            else
            {
                // Partial read from this chunk
                int needed = size - totalRead;
                var (taken, remaining) = SplitChunk(chunk, needed);
                chunks.Add(taken);
                _readBuffer.Dequeue();
                if (remaining != null)
                {
                    // Put the remaining back at the front
                    var temp = _readBuffer.ToList();
                    _readBuffer.Clear();
                    _readBuffer.Enqueue(remaining);
                    foreach (var item in temp)
                    {
                        _readBuffer.Enqueue(item);
                    }
                }
                totalRead = size;
            }
        }

        return ConcatenateChunks(chunks);
    }

    private static int GetChunkLength(object? chunk)
    {
        return chunk switch
        {
            string s => s.Length,
            SharpTSBuffer buf => buf.Length,
            _ => chunk?.ToString()?.Length ?? 0
        };
    }

    private static (object? taken, object? remaining) SplitChunk(object? chunk, int at)
    {
        if (chunk is string s)
        {
            return (s.Substring(0, Math.Min(at, s.Length)),
                    at < s.Length ? s.Substring(at) : null);
        }
        if (chunk is SharpTSBuffer buf)
        {
            var data = buf.Data;
            var taken = new byte[Math.Min(at, data.Length)];
            Array.Copy(data, taken, taken.Length);
            if (at < data.Length)
            {
                var remaining = new byte[data.Length - at];
                Array.Copy(data, at, remaining, 0, remaining.Length);
                return (new SharpTSBuffer(taken), new SharpTSBuffer(remaining));
            }
            return (new SharpTSBuffer(taken), null);
        }
        return (chunk, null);
    }

    private object? ConcatenateChunks(List<object?> chunks)
    {
        if (chunks.Count == 0)
        {
            return null;
        }
        if (chunks.Count == 1)
        {
            return chunks[0];
        }

        // Check if all chunks are strings
        if (chunks.All(c => c is string))
        {
            return string.Join("", chunks.Cast<string>());
        }

        // Convert all to buffers and concatenate
        var buffers = new List<byte[]>();
        foreach (var chunk in chunks)
        {
            if (chunk is SharpTSBuffer buf)
            {
                buffers.Add(buf.Data);
            }
            else if (chunk is string s)
            {
                buffers.Add(System.Text.Encoding.UTF8.GetBytes(s));
            }
        }

        var totalLength = buffers.Sum(b => b.Length);
        var result = new byte[totalLength];
        int offset = 0;
        foreach (var buffer in buffers)
        {
            Array.Copy(buffer, 0, result, offset, buffer.Length);
            offset += buffer.Length;
        }

        return _encoding == "utf8" || _encoding == "utf-8"
            ? System.Text.Encoding.UTF8.GetString(result)
            : (object)new SharpTSBuffer(result);
    }

    /// <summary>
    /// Pushes data into the stream buffer.
    /// </summary>
    private RuntimeValue Push(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (_destroyed)
        {
            return RuntimeValue.False;
        }

        var chunk = args.Length > 0 ? args[0].ToObject() : null;

        if (chunk == null)
        {
            // EOF signal
            _ended = true;
            _readable = false;
            if (_flowing == true)
            {
                EmitEvent(interpreter, "end", []);
            }
            else
            {
                EmitEndEvent(interpreter);
            }
            FlushPipes(interpreter);
            return RuntimeValue.False;
        }

        if (_flowing == true)
        {
            // Flowing mode: emit 'data' event directly, don't buffer
            EmitEvent(interpreter, "data", [chunk]);
            FlushToPipes(interpreter, chunk);
        }
        else
        {
            // Non-flowing mode: buffer as before
            _readBuffer.Enqueue(chunk);
            // In sync mode, immediately pipe to destinations
            FlushToPipes(interpreter, chunk);
        }

        // Return false when buffer exceeds highWaterMark (backpressure signal)
        return RuntimeValue.FromBoolean(GetBufferSize() < _highWaterMark);
    }

    /// <summary>
    /// Pushes data back to the front of the buffer.
    /// </summary>
    private RuntimeValue Unshift(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (_destroyed || _ended)
        {
            return RuntimeValue.Null;
        }

        var chunk = args.Length > 0 ? args[0].ToObject() : null;
        if (chunk == null)
        {
            return RuntimeValue.Null;
        }

        var temp = _readBuffer.ToList();
        _readBuffer.Clear();
        _readBuffer.Enqueue(chunk);
        foreach (var item in temp)
        {
            _readBuffer.Enqueue(item);
        }

        return RuntimeValue.FromObject(this);
    }

    /// <summary>
    /// Pipes this readable to a writable destination.
    /// </summary>
    private RuntimeValue Pipe(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length < 1)
        {
            throw new Exception("pipe() destination must be a Writable stream");
        }

        var destObj = args[0].ToObject();

        // Accept SharpTSWritable or any Duplex-derived type (which has Writable capabilities)
        if (destObj is not SharpTSWritable && destObj is not SharpTSDuplex)
        {
            throw new Exception("pipe() destination must be a Writable stream");
        }

        // Store the destination for piping
        if (destObj is SharpTSWritable or SharpTSDuplex)
        {
            _pipeDestinations.Add(destObj);
        }

        // Check options for end: false
        bool shouldEnd = true;
        if (args.Length > 1 && args[1].ToObject() is SharpTSObject options)
        {
            var endOption = options.GetProperty("end");
            if (endOption is bool endBool && !endBool)
            {
                shouldEnd = false;
            }
        }

        // Drain existing buffer to the destination
        while (_readBuffer.Count > 0)
        {
            var chunk = _readBuffer.Dequeue();
            var ok = WriteToDestination(interpreter, destObj, chunk, _encoding);
            if (!ok)
            {
                // Destination backpressure — stop draining, stay paused
                RegisterDrainListener(interpreter, destObj);
                return RuntimeValue.FromObject(destObj);
            }
        }

        // Enter flowing mode so future pushes emit 'data' and flow to pipes
        _flowing = true;

        // If already ended, end the destination
        if (shouldEnd && _ended)
        {
            EndDestination(interpreter, destObj);
        }

        return RuntimeValue.FromObject(destObj);
    }

    /// <summary>
    /// Writes a chunk to any writable-like destination. Returns false on backpressure.
    /// </summary>
    private static bool WriteToDestination(Interp interpreter, object destObj, object? chunk, string encoding)
    {
        if (destObj is SharpTSWritable writable)
        {
            return writable.WriteInternal(interpreter, chunk, encoding);
        }
        else if (destObj is SharpTSPassThrough passThrough)
        {
            var writeMethod = passThrough.GetMember("write") as BuiltInMethod;
            var result = writeMethod?.Bind(passThrough).Call(interpreter, [chunk, encoding]);
            return result is not false;
        }
        else if (destObj is SharpTSTransform transform)
        {
            var writeMethod = transform.GetMember("write") as BuiltInMethod;
            var result = writeMethod?.Bind(transform).Call(interpreter, [chunk, encoding]);
            return result is not false;
        }
        else if (destObj is SharpTSDuplex duplex)
        {
            var writeMethod = duplex.GetMember("write") as BuiltInMethod;
            var result = writeMethod?.Bind(duplex).Call(interpreter, [chunk, encoding]);
            return result is not false;
        }
        return true;
    }

    /// <summary>
    /// Ends any writable-like destination.
    /// </summary>
    private static void EndDestination(Interp interpreter, object destObj)
    {
        if (destObj is SharpTSWritable writable)
        {
            writable.EndInternal(interpreter, null, null);
        }
        else if (destObj is SharpTSPassThrough passThrough)
        {
            var endMethod = passThrough.GetMember("end") as BuiltInMethod;
            endMethod?.Bind(passThrough).Call(interpreter, []);
        }
        else if (destObj is SharpTSTransform transform)
        {
            var endMethod = transform.GetMember("end") as BuiltInMethod;
            endMethod?.Bind(transform).Call(interpreter, []);
        }
        else if (destObj is SharpTSDuplex duplex)
        {
            var endMethod = duplex.GetMember("end") as BuiltInMethod;
            endMethod?.Bind(duplex).Call(interpreter, []);
        }
    }

    /// <summary>
    /// Unpipes from a destination or all destinations.
    /// </summary>
    private RuntimeValue Unpipe(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length > 0 && args[0].ToObject() is { } dest)
        {
            _pipeDestinations.Remove(dest);
        }
        else
        {
            _pipeDestinations.Clear();
        }

        return RuntimeValue.FromObject(this);
    }

    /// <summary>
    /// Sets the encoding for string output.
    /// </summary>
    private RuntimeValue SetEncoding(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length > 0 && args[0].IsString)
        {
            _encoding = args[0].AsStringUnsafe().ToLowerInvariant();
        }
        return RuntimeValue.FromObject(this);
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
        _readable = false;
        _readBuffer.Clear();
        _pipeDestinations.Clear();

        if (args.Length > 0 && args[0].ToObject() is { } error)
        {
            // Emit error event
            EmitEvent(interpreter, "error", [error]);
        }

        EmitEvent(interpreter, "close", []);
        return RuntimeValue.FromObject(this);
    }

    private RuntimeValue Pause(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        _flowing = false;
        EmitEvent(interpreter, "pause", []);
        return RuntimeValue.FromObject(this);
    }

    private RuntimeValue Resume(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        _flowing = true;
        EmitEvent(interpreter, "resume", []);

        // Drain existing buffer by emitting 'data' for each queued chunk
        while (_readBuffer.Count > 0)
        {
            var chunk = _readBuffer.Dequeue();
            EmitEvent(interpreter, "data", [chunk]);
        }

        // If already ended, emit 'end'
        if (_ended)
        {
            EmitEvent(interpreter, "end", []);
        }

        return RuntimeValue.FromObject(this);
    }

    private RuntimeValue IsPaused(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        return RuntimeValue.FromBoolean(_flowing == false);
    }

    private void EmitEndEvent(Interp interpreter)
    {
        EmitEvent(interpreter, "end", []);
    }

    private void FlushToPipes(Interp interpreter, object? chunk)
    {
        foreach (var dest in _pipeDestinations.ToList())
        {
            var ok = WriteToDestination(interpreter, dest, chunk, _encoding);
            if (!ok && _flowing == true)
            {
                // Destination signaled backpressure — pause this readable
                _flowing = false;
                // Register a drain listener on the destination to resume
                RegisterDrainListener(interpreter, dest);
            }
        }
    }

    private void RegisterDrainListener(Interp interpreter, object dest)
    {
        var listener = new DrainResumeListener(this);
        if (dest is SharpTSWritable writable)
        {
            var onceMethod = ((SharpTSEventEmitter)writable).GetMember("once") as BuiltInMethod;
            onceMethod?.Bind(writable).Call(interpreter, ["drain", listener]);
        }
        else if (dest is SharpTSDuplex duplex)
        {
            var onceMethod = ((SharpTSEventEmitter)duplex).GetMember("once") as BuiltInMethod;
            onceMethod?.Bind(duplex).Call(interpreter, ["drain", listener]);
        }
    }

    /// <summary>
    /// Listener that resumes the readable when the writable drains.
    /// Implements ISharpTSCallable for interpreter mode compatibility.
    /// </summary>
    private class DrainResumeListener : ISharpTSCallable
    {
        private readonly SharpTSReadable _readable;

        public DrainResumeListener(SharpTSReadable readable)
        {
            _readable = readable;
        }

        public int Arity() => 0;

        public object? Call(Interp interpreter, List<object?> arguments)
        {
            _readable._flowing = true;
            // Drain any buffered data
            while (_readable._readBuffer.Count > 0)
            {
                var chunk = _readable._readBuffer.Dequeue();
                _readable.EmitEvent(interpreter, "data", [chunk]);
                _readable.FlushToPipes(interpreter, chunk);
            }
            return null;
        }
    }

    private void FlushPipes(Interp interpreter)
    {
        foreach (var dest in _pipeDestinations.ToList())
        {
            EndDestination(interpreter, dest);
        }
    }

    /// <summary>
    /// When a 'data' listener is added, enter flowing mode automatically.
    /// The actual buffer drain happens in the wrapped on/addListener methods
    /// which have access to the interpreter.
    /// </summary>
    protected override void OnListenerAdded(string eventName)
    {
        if (eventName == "data" && _flowing != true)
        {
            _flowing = true;
        }
    }

    /// <summary>
    /// Resets mutable state for singleton reuse (e.g., process.stdin between interpreter runs).
    /// </summary>
    internal void ResetReadableState()
    {
        _readBuffer.Clear();
        _pipeDestinations.Clear();
        _ended = false;
        _destroyed = false;
        _readable = true;
        _flowing = null;
        ClearAllListenersInternal();
    }

    /// <summary>
    /// Drains the existing read buffer by emitting 'data' events for each queued chunk.
    /// Called after a 'data' listener is added, when the interpreter is available.
    /// Does NOT emit 'end' — that's handled separately when the 'end' listener is added.
    /// </summary>
    private void DrainBuffer(Interp interpreter)
    {
        while (_readBuffer.Count > 0)
        {
            var chunk = _readBuffer.Dequeue();
            EmitEvent(interpreter, "data", [chunk]);
        }
    }

    /// <summary>
    /// Collects all chunks from the stream into an array.
    /// </summary>
    private RuntimeValue ToArray(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var elements = new SharpTS.Runtime.Deque<object?>();
        while (_readBuffer.Count > 0)
        {
            elements.AddLast(_readBuffer.Dequeue());
        }
        return RuntimeValue.FromObject(new SharpTSArray(elements));
    }

    /// <summary>
    /// Calls fn for each chunk in the stream.
    /// </summary>
    private RuntimeValue ForEach(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var fn = args.Length > 0 ? args[0].ToObject() as ISharpTSCallable : null;
        if (fn == null)
            throw new Exception("forEach() requires a function argument");

        while (_readBuffer.Count > 0)
        {
            var chunk = _readBuffer.Dequeue();
            fn.Call(interpreter, [chunk]);
        }
        return RuntimeValue.Null;
    }

    /// <summary>
    /// Creates a Transform that applies fn to each chunk.
    /// </summary>
    private RuntimeValue Map(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var fn = args.Length > 0 ? args[0].ToObject() as ISharpTSCallable : null;
        if (fn == null)
            throw new Exception("map() requires a function argument");

        var transform = new SharpTSTransform();
        transform.ObjectMode = _objectMode;
        transform.SetTransformCallback(new MapTransformCallable(fn));

        // Pipe self to the transform
        var pipeMethod = GetMember("pipe") as BuiltInMethod;
        pipeMethod?.Bind(this).Call(interpreter, [transform]);

        return RuntimeValue.FromObject(transform);
    }

    /// <summary>
    /// Creates a Transform that filters chunks by predicate.
    /// </summary>
    private RuntimeValue Filter(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var fn = args.Length > 0 ? args[0].ToObject() as ISharpTSCallable : null;
        if (fn == null)
            throw new Exception("filter() requires a function argument");

        var transform = new SharpTSTransform();
        transform.ObjectMode = _objectMode;
        transform.SetTransformCallback(new FilterTransformCallable(fn));

        var pipeMethod = GetMember("pipe") as BuiltInMethod;
        pipeMethod?.Bind(this).Call(interpreter, [transform]);

        return RuntimeValue.FromObject(transform);
    }

    /// <summary>
    /// Transform callable that applies a map function to each chunk.
    /// </summary>
    private class MapTransformCallable : ISharpTSCallable
    {
        private readonly ISharpTSCallable _fn;
        public MapTransformCallable(ISharpTSCallable fn) => _fn = fn;
        public int Arity() => 3;
        public object? Call(Interp interpreter, List<object?> arguments)
        {
            var chunk = arguments.Count > 0 ? arguments[0] : null;
            var callback = arguments.Count > 2 ? arguments[2] as ISharpTSCallable : null;
            var result = _fn.Call(interpreter, [chunk]);
            callback?.Call(interpreter, [null, result]);
            return null;
        }
    }

    /// <summary>
    /// Transform callable that filters chunks by predicate.
    /// </summary>
    private class FilterTransformCallable : ISharpTSCallable
    {
        private readonly ISharpTSCallable _fn;
        public FilterTransformCallable(ISharpTSCallable fn) => _fn = fn;
        public int Arity() => 3;
        public object? Call(Interp interpreter, List<object?> arguments)
        {
            var chunk = arguments.Count > 0 ? arguments[0] : null;
            var callback = arguments.Count > 2 ? arguments[2] as ISharpTSCallable : null;
            var result = _fn.Call(interpreter, [chunk]);
            if (result is true || (result is double d && d != 0))
                callback?.Call(interpreter, [null, chunk]);
            else
                callback?.Call(interpreter, [null]);
            return null;
        }
    }

    /// <summary>
    /// Gets the current buffer size in bytes (or count for object mode).
    /// </summary>
    private int GetBufferSize()
    {
        if (_objectMode)
            return _readBuffer.Count;

        int size = 0;
        foreach (var chunk in _readBuffer)
        {
            size += GetChunkLength(chunk);
        }
        return size;
    }

    public override string ToString() => "Readable {}";
}
