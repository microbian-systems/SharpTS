using SharpTS.Runtime.Types;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of the Node.js 'stream' module.
/// </summary>
/// <remarks>
/// Provides stream classes for data processing:
/// - Readable: Read data from a source
/// - Writable: Write data to a destination
/// - Duplex: Read and write independently
/// - Transform: Transform data as it passes through
/// - PassThrough: Pass data through unchanged
/// - finished(): Callback when stream is no longer readable/writable
/// - pipeline(): Pipe streams together with error handling
/// </remarks>
public static class StreamModuleInterpreter
{
    /// <summary>
    /// Gets all exported values for the stream module.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            ["Readable"] = SharpTSReadableConstructor.Instance,
            ["Writable"] = SharpTSWritableConstructor.Instance,
            ["Duplex"] = SharpTSDuplexConstructor.Instance,
            ["Transform"] = SharpTSTransformConstructor.Instance,
            ["PassThrough"] = SharpTSPassThroughConstructor.Instance,
            ["finished"] = new BuiltInMethod("finished", 1, 3, Finished),
            ["pipeline"] = new BuiltInMethod("pipeline", 2, int.MaxValue, Pipeline),
            ["addAbortSignal"] = new BuiltInMethod("addAbortSignal", 2, AddAbortSignal),
            ["promises"] = StreamPromisesModuleInterpreter.CreatePromisesNamespace()
        };
    }

    /// <summary>
    /// stream.finished(stream, options?, callback) — calls callback when stream is done.
    /// Returns a cleanup function that removes listeners.
    /// </summary>
    internal static object? FinishedInternal(Interp interpreter, object? receiver, List<object?> args)
        => Finished(interpreter, receiver, args);

    private static object? Finished(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count < 1)
            throw new Exception("finished() requires at least a stream argument");

        var stream = args[0] as SharpTSEventEmitter
            ?? throw new Exception("finished() first argument must be a stream");

        // Parse options and callback
        SharpTSObject? options = null;
        ISharpTSCallable? callback = null;

        if (args.Count == 2)
        {
            if (args[1] is ISharpTSCallable cb)
                callback = cb;
            else if (args[1] is SharpTSObject opts)
                options = opts;
        }
        else if (args.Count >= 3)
        {
            options = args[1] as SharpTSObject;
            callback = args[2] as ISharpTSCallable;
        }

        if (callback == null)
            throw new Exception("finished() requires a callback function");

        bool watchReadable = true;
        bool watchWritable = true;

        if (options != null)
        {
            if (options.GetProperty("readable") is false)
                watchReadable = false;
            if (options.GetProperty("writable") is false)
                watchWritable = false;
        }

        bool called = false;
        var cbRef = callback;

        void CallOnce(Interp interp, object? error)
        {
            if (called) return;
            called = true;
            cbRef.CallBoxed(interp, [error]);
        }

        // Create listener callables
        var errorListener = new FinishedListener((interp, listenerArgs) =>
            CallOnce(interp, listenerArgs.Count > 0 ? listenerArgs[0] : null));
        var endListener = new FinishedListener((interp, _) =>
        {
            if (watchReadable) CallOnce(interp, null);
        });
        var finishListener = new FinishedListener((interp, _) =>
        {
            if (watchWritable) CallOnce(interp, null);
        });
        var closeListener = new FinishedListener((interp, _) => CallOnce(interp, null));

        stream.AddListenerDirect("error", errorListener, once: true);
        if (watchReadable)
            stream.AddListenerDirect("end", endListener, once: true);
        if (watchWritable)
            stream.AddListenerDirect("finish", finishListener, once: true);
        stream.AddListenerDirect("close", closeListener, once: true);

        // Return cleanup function
        return new BuiltInMethod("cleanup", 0, (Interp interp, object? recv, List<object?> a) =>
        {
            stream.RemoveListenerDirect("error", errorListener);
            stream.RemoveListenerDirect("end", endListener);
            stream.RemoveListenerDirect("finish", finishListener);
            stream.RemoveListenerDirect("close", closeListener);
            return null;
        });
    }

    /// <summary>
    /// stream.pipeline(source, ...transforms, dest, callback?) — pipes streams with error handling.
    /// Returns the destination stream.
    /// </summary>
    internal static object? PipelineInternal(Interp interpreter, object? receiver, List<object?> args)
        => Pipeline(interpreter, receiver, args);

    private static object? Pipeline(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count < 2)
            throw new Exception("pipeline() requires at least a source and destination");

        // Detect callback: if last arg is callable, it's the callback
        ISharpTSCallable? callback = null;
        var streams = new List<object?>(args);

        if (streams.Count > 0 && streams[^1] is ISharpTSCallable cb)
        {
            callback = cb;
            streams.RemoveAt(streams.Count - 1);
        }

        if (streams.Count < 2)
            throw new Exception("pipeline() requires at least a source and destination");

        // Pipe consecutive pairs
        for (int i = 0; i < streams.Count - 1; i++)
        {
            var source = streams[i];
            var dest = streams[i + 1];

            if (source is not SharpTSEventEmitter)
                throw new Exception("pipeline() arguments must be streams");

            // Call pipe on source
            var pipeMethod = GetStreamMethod(source, "pipe");
            pipeMethod?.CallBoxed(interpreter, [dest]);
        }

        // Attach error listeners on all streams for auto-destroy
        var finalCallback = callback;
        bool errorCalled = false;

        foreach (var s in streams)
        {
            if (s is SharpTSEventEmitter emitter)
            {
                emitter.AddListenerDirect("error", new FinishedListener((interp, errorArgs) =>
                {
                    if (errorCalled) return;
                    errorCalled = true;
                    var error = errorArgs.Count > 0 ? errorArgs[0] : null;

                    // Destroy all streams
                    foreach (var stream in streams)
                    {
                        var destroyMethod = GetStreamMethod(stream, "destroy");
                        destroyMethod?.CallBoxed(interp, []);
                    }

                    finalCallback?.CallBoxed(interp, [error]);
                }), once: true);
            }
        }

        // On success (last stream finishes), call callback(null)
        var lastStream = streams[^1];
        if (lastStream is SharpTSEventEmitter lastEmitter)
        {
            // Listen for finish (writable) or end (readable)
            var eventName = lastStream is SharpTSWritable or SharpTSDuplex ? "finish" : "end";
            lastEmitter.AddListenerDirect(eventName, new FinishedListener((interp, _) =>
            {
                if (!errorCalled)
                    finalCallback?.CallBoxed(interp, [null]);
            }), once: true);
        }

        return lastStream;
    }

    /// <summary>
    /// stream.addAbortSignal(signal, stream) — destroys stream when signal is aborted.
    /// </summary>
    private static object? AddAbortSignal(Interp interpreter, object? receiver, List<object?> args)
    {
        // Basic implementation: just return the stream
        // AbortSignal integration requires async runtime support
        if (args.Count < 2)
            throw new Exception("addAbortSignal() requires signal and stream arguments");

        return args[1]; // Return the stream
    }

    private static BuiltInMethod? GetStreamMethod(object? stream, string methodName)
    {
        return stream switch
        {
            SharpTSPassThrough pt => pt.GetMember(methodName) as BuiltInMethod,
            SharpTSTransform t => t.GetMember(methodName) as BuiltInMethod,
            SharpTSDuplex d => d.GetMember(methodName) as BuiltInMethod,
            SharpTSReadable r => r.GetMember(methodName) as BuiltInMethod,
            SharpTSWritable w => w.GetMember(methodName) as BuiltInMethod,
            _ => null
        };
    }

    /// <summary>
    /// Simple callable for finished/pipeline internal listeners.
    /// </summary>
    internal class FinishedListener : ISharpTSCallable
    {
        private readonly Action<Interp, List<object?>> _action;
        public FinishedListener(Action<Interp, List<object?>> action) => _action = action;
        public int Arity() => 0;
        public object? Call(Interp interpreter, List<object?> arguments)
        {
            _action(interpreter, arguments);
            return null;
        }

        public RuntimeValue CallV2(Interp interpreter, ReadOnlySpan<RuntimeValue> arguments)
            => RuntimeValue.FromBoxed(Call(interpreter, CallableInterop.ToBoxedList(arguments)));
    }
}
