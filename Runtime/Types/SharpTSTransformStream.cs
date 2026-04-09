using SharpTS.Runtime.BuiltIns;
using SharpTS.TypeSystem;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of the WHATWG Streams <c>TransformStream</c>.
/// </summary>
/// <remarks>
/// Owns one readable/writable pair. Chunks written to <see cref="Writable"/>
/// are piped through the user's <c>transform</c> callback which may call
/// <c>controller.enqueue(...)</c> to push into <see cref="Readable"/>. When the
/// writable side closes, the user's optional <c>flush</c> callback runs before
/// the readable side closes.
/// </remarks>
public class SharpTSTransformStream : ITypeCategorized
{
    public TypeCategory RuntimeCategory => TypeCategory.Unknown;

    public SharpTSReadableStream Readable { get; }
    public SharpTSWritableStream Writable { get; }

    private readonly SharpTSTransformStreamDefaultController _controller;
    private readonly Interp? _interp;

    private object? _transformFn;
    private object? _flushFn;

    public SharpTSTransformStream(Interp? interp, object? transformer, object? writableStrategy, object? readableStrategy)
    {
        _interp = interp;

        if (transformer != null)
        {
            _transformFn = StreamFields.GetCallback(transformer, "transform");
            _flushFn = StreamFields.GetCallback(transformer, "flush");
        }

        // Build the readable side first (with a pull algorithm that's a no-op — data is pushed).
        Readable = new SharpTSReadableStream(interp, underlyingSource: null, strategy: readableStrategy);
        _controller = new SharpTSTransformStreamDefaultController(Readable);

        // Build a writable sink that runs the user's transform callback.
        var sinkFields = new Dictionary<string, object?>
        {
            ["write"] = new BuiltInMethod("write", 2, (i, _, args) =>
            {
                var chunk = args.Count > 0 ? args[0] : SharpTSUndefined.Instance;
                return RunTransformAsync(i, chunk);
            }),
            ["close"] = new BuiltInMethod("close", 0, (i, _, _) =>
            {
                return RunFlushAsync(i);
            }),
            ["abort"] = new BuiltInMethod("abort", 1, (_, _, args) =>
            {
                var reason = args.Count > 0 ? args[0] : SharpTSUndefined.Instance;
                Readable.ErrorInternal(reason);
                return SharpTSUndefined.Instance;
            }),
        };
        var sink = new SharpTSObject(sinkFields);

        Writable = new SharpTSWritableStream(interp, sink, writableStrategy);

        // Fire transformer.start(controller) if present.
        var startFn = StreamFields.GetCallback(transformer, "start");
        if (startFn != null)
        {
            try
            {
                RuntimeCallableDispatcher.Invoke(interp, startFn, _controller);
            }
            catch (Exception ex)
            {
                Readable.ErrorInternal(ex is SharpTSPromiseRejectedException pre ? pre.Reason : ex);
            }
        }
    }

    private object? RunTransformAsync(Interp? interp, object? chunk)
    {
        if (_transformFn == null)
        {
            // Default: pass through.
            try
            {
                Readable.EnqueueInternal(chunk);
            }
            catch (Exception ex)
            {
                Readable.ErrorInternal(ex is SharpTSPromiseRejectedException pre ? pre.Reason : ex);
            }
            return SharpTSUndefined.Instance;
        }

        try
        {
            var result = RuntimeCallableDispatcher.Invoke(interp, _transformFn, chunk, _controller);
            if (result is SharpTSPromise p) return p;
            return SharpTSUndefined.Instance;
        }
        catch (Exception ex)
        {
            var err = ex is SharpTSPromiseRejectedException pre ? pre.Reason : ex;
            Readable.ErrorInternal(err);
            return SharpTSPromise.Reject(err);
        }
    }

    private object? RunFlushAsync(Interp? interp)
    {
        Task<object?> finishAsync()
        {
            Readable.CloseInternal();
            return Task.FromResult<object?>(SharpTSUndefined.Instance);
        }

        if (_flushFn == null)
        {
            return new SharpTSPromise(finishAsync());
        }

        try
        {
            var result = RuntimeCallableDispatcher.Invoke(interp, _flushFn, _controller);
            if (result is SharpTSPromise p)
            {
                async Task<object?> awaitThenClose()
                {
                    await p.GetValueAsync();
                    Readable.CloseInternal();
                    return SharpTSUndefined.Instance;
                }
                return new SharpTSPromise(awaitThenClose());
            }
            Readable.CloseInternal();
            return SharpTSUndefined.Instance;
        }
        catch (Exception ex)
        {
            var err = ex is SharpTSPromiseRejectedException pre ? pre.Reason : ex;
            Readable.ErrorInternal(err);
            return SharpTSPromise.Reject(err);
        }
    }

    public object? GetMember(string name)
    {
        return name switch
        {
            "readable" => Readable,
            "writable" => Writable,
            _ => null,
        };
    }

    public override string ToString() => "TransformStream {}";
}

/// <summary>
/// Controller handed to a <see cref="SharpTSTransformStream"/>'s
/// <c>transform</c>/<c>flush</c> callbacks.
/// </summary>
public class SharpTSTransformStreamDefaultController : ITypeCategorized
{
    public TypeCategory RuntimeCategory => TypeCategory.Unknown;

    private readonly SharpTSReadableStream _readable;

    internal SharpTSTransformStreamDefaultController(SharpTSReadableStream readable)
    {
        _readable = readable;
    }

    public object? GetMember(string name)
    {
        return name switch
        {
            "desiredSize" => _readable.DesiredSize is { } d ? (object)d : null,
            "enqueue" => new BuiltInMethod("enqueue", 1, (_, _, args) =>
            {
                _readable.EnqueueInternal(args.Count > 0 ? args[0] : SharpTSUndefined.Instance);
                return SharpTSUndefined.Instance;
            }),
            "terminate" => new BuiltInMethod("terminate", 0, (_, _, _) =>
            {
                // Per WHATWG spec: terminate() closes the readable side
                // (not errors it). Subsequent reader.read() returns
                // { value: undefined, done: true } as with a normal close.
                _readable.CloseInternal();
                return SharpTSUndefined.Instance;
            }),
            "error" => new BuiltInMethod("error", 1, (_, _, args) =>
            {
                _readable.ErrorInternal(args.Count > 0 ? args[0] : SharpTSUndefined.Instance);
                return SharpTSUndefined.Instance;
            }),
            _ => null,
        };
    }

    public override string ToString() => "TransformStreamDefaultController {}";
}
