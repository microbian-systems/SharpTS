using System.Net;
using SharpTS.Execution;
using SharpTS.Runtime.BuiltIns;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of an HTTP Server.
/// </summary>
/// <remarks>
/// Wraps HttpListener to provide the Node.js http.Server interface.
/// Extends SharpTSEventEmitter for full event handling support (on, once, off, emit, etc.).
/// Implements IAsyncHandle to keep the event loop alive while listening.
/// Methods: listen(port, callback?), close(callback?)
/// Events: 'listening', 'request', 'error', 'close'
/// </remarks>
public class SharpTSHttpServer : SharpTSEventEmitter, IDisposable
{
    /// <inheritdoc />
    public override TypeCategory RuntimeCategory => TypeCategory.Record;

    private HttpListener? _listener;
    private readonly ISharpTSCallable _requestHandler;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _isListening;
    private Interpreter? _interpreter;
    private bool _isClusterWorker;
    private int _port;

    // Drain-before-close state (issue #41): close() doesn't tear the listener
    // down while requests are in flight. It marks _closeRequested, then the
    // last HandleRequestAsync to finish invokes FinishClose. Mutations to
    // _closeRequested and _inFlightRequests are coordinated via _stateLock so
    // the accept loop can't dispatch a new request onto a listener that's
    // about to be torn down.
    private readonly object _stateLock = new();
    private bool _closeRequested;
    private int _inFlightRequests;
    private ISharpTSCallable? _pendingCloseCallback;

    /// <summary>
    /// Creates a new HTTP server with the given request handler.
    /// </summary>
    /// <param name="requestHandler">The callback function (req, res) => void.</param>
    public SharpTSHttpServer(ISharpTSCallable requestHandler)
    {
        _requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
    }

    /// <summary>
    /// Creates a new HTTP server from a compiled-mode callback (e.g. $TSFunction).
    /// </summary>
    public SharpTSHttpServer(object? requestHandler)
        : this(TSFunctionCallableAdapter.WrapCallback(requestHandler))
    { }

    /// <summary>
    /// Whether the server is currently listening.
    /// </summary>
    public bool Listening => _isListening;

    /// <summary>
    /// Gets a member (property or method) by name.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            "listening" => Listening,
            "listen" => new BuiltInMethod("listen", 1, 3, (interp, receiver, args) =>
            {
                if (receiver is SharpTSHttpServer server)
                    return server.Listen(interp, args);
                return receiver;
            }).Bind(this),
            "close" => new BuiltInMethod("close", 0, 1, (interp, receiver, args) =>
            {
                if (receiver is SharpTSHttpServer server)
                    return server.Close(args);
                return receiver;
            }).Bind(this),
            "address" => GetAddress(),
            // Inherit EventEmitter methods for on, once, off, emit, removeAllListeners, etc.
            _ => base.GetMember(name)
        };
    }

    /// <summary>
    /// Starts listening on the specified port.
    /// </summary>
    private object? Listen(Interpreter interpreter, List<object?> args)
    {
        if (_isListening)
            throw new Exception("Runtime Error: Server is already listening");

        if (args.Count == 0 || args[0] is not double portNum)
            throw new Exception("Runtime Error: listen requires a port number");

        _port = (int)portNum;
        ISharpTSCallable? callback = null;

        // Second argument can be hostname (ignored for now) or callback
        if (args.Count > 1)
        {
            if (args[1] is ISharpTSCallable cb)
            {
                callback = cb;
            }
            else if (args.Count > 2 && args[2] is ISharpTSCallable cb2)
            {
                callback = cb2;
            }
        }

        _interpreter = interpreter;

        // Cluster worker mode: register with shared listener
        if (ClusterContext.IsWorker)
            return ListenAsClusterWorker(interpreter, callback);

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{_port}/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            // Try localhost only if wildcard fails (requires admin on Windows)
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
        }

        _isListening = true;
        _cts = new CancellationTokenSource();

        // Register with interpreter's event loop to keep process alive
        interpreter.Ref();

        // Start accepting requests
        _listenTask = AcceptRequestsAsync(_cts.Token);

        // Call the listening callback
        if (callback != null)
        {
            callback.CallBoxed(interpreter, new List<object?>());
        }

        // Emit 'listening' event
        EmitEvent("listening", new List<object?>());

        return this;
    }

    /// <summary>
    /// Listens as a cluster worker by registering with the shared HTTP listener.
    /// </summary>
    private object? ListenAsClusterWorker(Interpreter interpreter, ISharpTSCallable? callback)
    {
        _isClusterWorker = true;
        var registry = ClusterSingleton.Instance.SharedListeners;

        registry.RegisterHttpWorker(_port, ClusterContext.WorkerId, context =>
        {
            // Reserve in-flight slot so HandleRequestAsync's decrement pairs
            // cleanly and close() drain semantics work for cluster workers too.
            bool accepted;
            lock (_stateLock)
            {
                if (_closeRequested) { accepted = false; }
                else { _inFlightRequests++; accepted = true; }
            }
            if (!accepted)
            {
                try { context.Response.Abort(); } catch { }
                return;
            }
            _ = HandleRequestAsync(context);
        }, interpreter);

        _isListening = true;
        interpreter.Ref();

        callback?.CallBoxed(interpreter, new List<object?>());
        EmitEvent("listening", new List<object?>());

        return this;
    }

    /// <summary>
    /// Closes the server.
    /// </summary>
    /// <remarks>
    /// Node-compatible semantics: stops accepting new connections immediately, but
    /// defers the actual listener teardown until all in-flight requests complete.
    /// The 'close' event and the optional user callback fire on the main event loop
    /// thread AFTER the last request finishes — never synchronously from inside a
    /// request handler.
    /// </remarks>
    private object? Close(List<object?> args)
    {
        if (!_isListening)
            return this;

        ISharpTSCallable? userCloseCallback = null;
        if (args.Count > 0 && args[0] is ISharpTSCallable cb)
            userCloseCallback = cb;

        bool finishNow;
        lock (_stateLock)
        {
            if (_closeRequested) return this; // idempotent
            _closeRequested = true;
            _pendingCloseCallback = userCloseCallback;
            // If no requests are in flight RIGHT NOW, we own the teardown.
            // The accept loop checks _closeRequested under the same lock before
            // incrementing _inFlightRequests, so it can't slip a new request in.
            finishNow = _inFlightRequests == 0;
        }

        // Signal the accept loop to stop looping. Doesn't unblock the in-flight
        // GetContextAsync — that's FinishClose's job via Stop()/Close().
        _cts?.Cancel();

        if (_isClusterWorker)
        {
            ClusterSingleton.Instance.SharedListeners.UnregisterHttpWorker(_port, ClusterContext.WorkerId);
        }

        if (finishNow)
        {
            FinishClose();
        }

        return this;
    }

    /// <summary>
    /// Performs the actual listener teardown and dispatches the 'close' event +
    /// user callback. Called either synchronously from Close() (no in-flight
    /// requests) or from the last HandleRequestAsync's finally (drain complete).
    /// </summary>
    /// <remarks>
    /// .NET's HttpListener.Stop()/Close() can throw under load on Linux
    /// (HttpListenerException, ObjectDisposedException, InvalidOperationException).
    /// Pre-fix, those bubbled through ExecuteBlock's catch, got translated into a
    /// guest ThrowException, and surfaced as the generic "Exception of type
    /// ThrowException was thrown" flake in issue #41. Swallowed here because the
    /// listener is being torn down; nothing actionable remains.
    ///
    /// The 'close' event and user callback go through ScheduleTimer(0) so they
    /// always run on the main interpreter thread via the event loop — FinishClose
    /// itself may be invoked from a threadpool thread via HandleRequestAsync's
    /// finally, and user JS must not execute there.
    /// </remarks>
    private void FinishClose()
    {
        if (!_isClusterWorker && _listener != null)
        {
            try { _listener.Stop(); } catch { /* teardown path; see remarks */ }
            try { _listener.Close(); } catch { /* teardown path; see remarks */ }
            _listener = null;
        }

        _isListening = false;
        _interpreter?.Unref();

        var callback = _pendingCloseCallback;
        _pendingCloseCallback = null;

        var interp = _interpreter;
        if (interp != null)
        {
            interp.ScheduleTimer(0, 0, () =>
            {
                callback?.CallBoxed(interp, new List<object?>());
                EmitEvent("close", new List<object?>());
            }, isInterval: false);
        }
    }

    /// <summary>
    /// Emits an event using the stored interpreter.
    /// </summary>
    private void EmitEvent(string eventName, List<object?> eventArgs)
    {
        if (_interpreter == null) return;
        base.EmitEvent(_interpreter, eventName, eventArgs);
    }

    /// <summary>
    /// Gets the server address information.
    /// </summary>
    private object? GetAddress()
    {
        if (!_isListening)
            return null;

        if (_isClusterWorker)
        {
            return new SharpTSObject(new Dictionary<string, object?>
            {
                ["address"] = "0.0.0.0",
                ["family"] = "IPv4",
                ["port"] = (double)_port
            });
        }

        if (_listener == null) return null;

        var prefix = _listener.Prefixes.FirstOrDefault();
        if (prefix == null) return null;

        // Parse the prefix to extract port
        var uri = new Uri(prefix);
        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["address"] = "0.0.0.0",
            ["family"] = "IPv4",
            ["port"] = (double)uri.Port
        });
    }

    /// <summary>
    /// Accepts incoming HTTP requests asynchronously.
    /// </summary>
    private async Task AcceptRequestsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener != null && _isListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();

                // Reserve an in-flight slot under the same lock Close() uses.
                // If close was already requested, drop the connection — we're
                // draining, not accepting new work.
                bool accepted;
                lock (_stateLock)
                {
                    if (_closeRequested)
                    {
                        accepted = false;
                    }
                    else
                    {
                        _inFlightRequests++;
                        accepted = true;
                    }
                }

                if (!accepted)
                {
                    try { context.Response.Abort(); } catch { }
                    continue;
                }

                _ = HandleRequestAsync(context);
            }
            catch (HttpListenerException)
            {
                // Listener was closed
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Handles an individual HTTP request.
    /// </summary>
    /// <remarks>
    /// The in-flight counter is incremented by the accept loop BEFORE this
    /// method is invoked (to close the window with Close()). This method is
    /// responsible for decrementing it — and, if close was requested while
    /// it was running, for calling FinishClose from the decrement path.
    /// </remarks>
    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        if (_interpreter == null)
        {
            DecrementInFlight();
            return;
        }

        var req = new SharpTSHttpRequest(context.Request);
        var res = new SharpTSHttpResponse(context.Response);

        try
        {
            // Schedule the request handler to run on the virtual timer system
            // This ensures it runs on the main thread during the event loop
            var tcs = new TaskCompletionSource();

            _interpreter.ScheduleTimer(0, 0, async () =>
            {
                try
                {
                    // Emit 'request' event
                    EmitEvent("request", new List<object?> { req, res });

                    // Call the request handler
                    _requestHandler.CallBoxed(_interpreter!, new List<object?> { req, res });

                    // Read and emit body events
                    await req.EmitDataEventsAsync(_interpreter!);

                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    // Emit error event
                    EmitEvent("error", new List<object?> { new SharpTSError(ex.Message) });
                    tcs.TrySetException(ex);
                }
            }, isInterval: false);

            await tcs.Task;
        }
        catch (Exception ex)
        {
            // Send error response if not already sent
            if (!res.HeadersSent)
            {
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.StatusDescription = "Internal Server Error";
                    var errorBytes = System.Text.Encoding.UTF8.GetBytes($"Error: {ex.Message}");
                    context.Response.ContentLength64 = errorBytes.Length;
                    await context.Response.OutputStream.WriteAsync(errorBytes);
                }
                catch
                {
                    // Ignore errors when writing error response
                }
            }
        }
        finally
        {
            if (!res.Finished)
            {
                try
                {
                    context.Response.Close();
                }
                catch
                {
                    // Ignore close errors
                }
            }

            DecrementInFlight();
        }
    }

    /// <summary>
    /// Drops an in-flight reservation. If this is the last in-flight request
    /// and close() has been requested, completes the deferred teardown.
    /// </summary>
    private void DecrementInFlight()
    {
        bool finishNow;
        lock (_stateLock)
        {
            _inFlightRequests--;
            finishNow = _inFlightRequests == 0 && _closeRequested && _isListening;
        }
        if (finishNow) FinishClose();
    }

    /// <summary>
    /// Disposes the server and releases resources.
    /// </summary>
    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _cts?.Dispose();
        _listener = null;
        _isListening = false;
    }

    public override string ToString() => $"Server {{ listening: {Listening} }}";
}
