using System.Collections.Concurrent;
using SharpTS.Diagnostics;
using SharpTS.Execution;
using SharpTS.Modules;
using SharpTS.Parsing;
using SharpTS.Runtime.BuiltIns;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Represents a Worker thread for parallel execution.
/// </summary>
/// <remarks>
/// Workers run TypeScript code in a separate thread with their own isolated interpreter.
/// Communication happens through message passing via postMessage/onmessage, using the
/// structured clone algorithm. SharedArrayBuffer is shared by reference, enabling
/// shared memory access with Atomics for synchronization.
///
/// Workers load script files from disk - inline functions are not supported.
/// This enforces a clean separation between threads and prevents closure issues.
/// </remarks>
public class SharpTSWorker : SharpTSEventEmitter, IDisposable
{
    private static int _nextThreadId = 1;

    private readonly Thread _thread;
    private readonly BlockingCollection<SharpTSMessagePort.ClonedMessage> _parentToWorkerQueue = new();
    private readonly BlockingCollection<SharpTSMessagePort.ClonedMessage> _workerToParentQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _scriptPath;
    private readonly object? _workerData;
    // Honored from either runtime: the interpreter SharpTSArray and a compiled
    // List<object?> both implement IEnumerable<object?>, so StructuredClone.Clone
    // receives the transfer list regardless of how the parent built it (#406).
    private readonly IEnumerable<object?>? _transferList;
    private volatile bool _isRunning;
    private volatile bool _isTerminated;
    private Exception? _workerError;
    private Interpreter? _parentInterpreter;

    // Event-loop keep-alive accounting against the parent interpreter. Node keeps
    // the parent process alive for a running Worker's lifetime by default;
    // worker.unref() opts out and worker.ref() opts back in. Without this, a
    // parent whose only pending work is a worker's 'message' listener can hit the
    // 250ms quiescence give-up and exit before the worker posts (#329 — same
    // native-I/O liveness class as #319/#320/#324). Guarded by _refLock so the
    // parent (ref/unref) and the worker thread (exit) can't race the count.
    private readonly object _refLock = new();
    private bool _refed;             // currently holding one parent-loop Ref for the running worker
    private bool _refReleased;       // worker exited/terminated — ref/unref are permanent no-ops

    // The keep-alive primitive, abstracted so the same accounting drives both
    // runtimes: in interpreter mode these are the parent Interpreter's Ref/Unref;
    // in compiled mode (no parent interpreter) they are the emitted $EventLoop
    // singleton's Ref/Unref, injected via CreateForCompiledLoop (#354). Null only
    // when there is neither — then ref/unref are no-ops.
    private readonly Action? _loopRef;
    private readonly Action? _loopUnref;

    // Compiled-mode callback marshal: the emitted $EventLoop's Schedule(Action),
    // which enqueues onto the loop and wakes it. Injected (rather than captured
    // from SynchronizationContext.Current, which is not reliably the installed
    // event-loop context at worker-construction time) so worker→parent event
    // delivery lands on the loop thread deterministically (#354).
    private readonly Action<Action>? _loopSchedule;

    // For compiled code support - enables Worker communication without interpreter
    private readonly SynchronizationContext? _syncContext;
    private readonly ConcurrentQueue<Action> _pendingCallbacks = new();

    /// <summary>
    /// Gets the unique thread ID for this worker.
    /// </summary>
    public double ThreadId { get; }

    /// <summary>
    /// Gets whether the worker thread is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <inheritdoc />
    public override TypeCategory RuntimeCategory => TypeCategory.Instance;

    /// <summary>
    /// Creates a new Worker that will execute the specified script file.
    /// </summary>
    /// <param name="filename">Path to the TypeScript file to execute.</param>
    /// <param name="options">Optional worker options (workerData, transferList, etc.).</param>
    /// <param name="parentInterpreter">The parent interpreter for message delivery.</param>
    /// <param name="eventLoopRef">
    /// Compiled-mode keep-alive Ref (the emitted <c>$EventLoop</c>'s <c>Ref</c>). When
    /// supplied it takes precedence over <paramref name="parentInterpreter"/> for
    /// event-loop accounting; in interpreter mode it is left null and the parent
    /// interpreter's Ref/Unref are used instead (#354).
    /// </param>
    /// <param name="eventLoopUnref">Compiled-mode keep-alive Unref, paired with <paramref name="eventLoopRef"/>.</param>
    /// <param name="eventLoopSchedule">
    /// Compiled-mode marshal that runs an action on the event-loop thread (the
    /// emitted <c>$EventLoop.Schedule</c>). Used to deliver worker events to the
    /// parent without relying on an ambient <see cref="SynchronizationContext"/>.
    /// </param>
    public SharpTSWorker(string filename, object? options, Interpreter? parentInterpreter,
        Action? eventLoopRef = null, Action? eventLoopUnref = null, Action<Action>? eventLoopSchedule = null)
    {
        _loopSchedule = eventLoopSchedule;
        ThreadId = Interlocked.Increment(ref _nextThreadId);
        _scriptPath = filename;
        _parentInterpreter = parentInterpreter;

        // Resolve the keep-alive handle once: explicit delegates (compiled mode)
        // win over the parent interpreter (interpreter mode). Both arms feed the
        // identical Ref accounting below, so the #329 fix now covers both runtimes.
        if (eventLoopRef != null && eventLoopUnref != null)
        {
            _loopRef = eventLoopRef;
            _loopUnref = eventLoopUnref;
        }
        else if (parentInterpreter != null)
        {
            _loopRef = parentInterpreter.Ref;
            _loopUnref = parentInterpreter.Unref;
        }

        // Capture sync context for compiled code to marshal callbacks to main thread
        _syncContext = SynchronizationContext.Current;

        // Extract options. Only workerData and transferList are honored: the worker
        // runs an isolated interpreter on a dedicated thread in this same process, so
        // the worker's console output already shares the parent's stdout/stderr by
        // default. The Node stdio options (stdin/stdout/stderr) and resourceLimits are
        // intentionally NOT supported:
        //   - stdin/stdout/stderr=true would have to expose per-worker Readable/Writable
        //     streams and divert the worker off the shared (process-global) Console,
        //     which is not thread-safe in a single-process model.
        //   - resourceLimits maps to V8 heap/stack sizing (maxOldGenerationSizeMb, …)
        //     for which the .NET runtime exposes no per-thread equivalent.
        // They are not read here (rather than read-and-ignored) so the dead fields can't
        // masquerade as support. See issue #407.
        //
        // The bag is a SharpTSObject in interpreter mode and a Dictionary<string, object?>
        // (a compiled object literal) in compiled mode; ReadOption reads through both so
        // workerData/transferList are honored either way (#380). transferList is read as
        // IEnumerable<object?> so both the interpreter SharpTSArray and a compiled
        // List<object?> survive — a transferred MessagePort is then adopted by the worker
        // (cross-thread for interpreter ports, via CompiledMessagePortBridge for compiled
        // $MessagePort) inside StructuredClone.Clone (#406).
        if (options != null)
        {
            _workerData = ReadOption(options, "workerData");
            _transferList = ReadOption(options, "transferList") as IEnumerable<object?>;
        }

        // Clone workerData for transfer to worker
        if (_workerData != null)
        {
            try
            {
                _workerData = StructuredClone.Clone(_workerData, _transferList);
            }
            catch (StructuredClone.DataCloneError e)
            {
                throw new Exception($"Failed to clone workerData: {e.Message}");
            }
        }

        // Create and start the worker thread
        _thread = new Thread(WorkerThreadMain)
        {
            Name = $"SharpTS-Worker-{ThreadId}",
            IsBackground = true
        };

        _isRunning = true;

        // Default-on Node semantics: a running worker keeps the parent loop alive.
        // Establish the Ref before starting the thread so the worker can never exit
        // (and Unref) before we've counted it.
        lock (_refLock)
        {
            if (_loopRef != null)
            {
                _refed = true;
                _loopRef();
            }
        }

        _thread.Start();
    }

    /// <summary>
    /// Reads a named property from a worker options bag regardless of how the bag is
    /// represented: a <see cref="SharpTSObject"/> in interpreter mode, or a
    /// <see cref="Dictionary{TKey,TValue}"/> (a compiled object literal) in compiled
    /// mode (#380). Returns null when the property is absent or the bag is an
    /// unsupported shape (e.g. a compiled options literal that uses accessors).
    /// </summary>
    private static object? ReadOption(object? options, string name) => options switch
    {
        SharpTSObject obj => obj.GetProperty(name),
        IDictionary<string, object?> dict => dict.TryGetValue(name, out var v) ? v : null,
        _ => null,
    };

    /// <summary>
    /// Factory used by compiled output to construct a worker whose running-lifetime
    /// keep-alive is accounted against the emitted <c>$EventLoop</c> singleton rather
    /// than an <see cref="Interpreter"/> (which does not exist at runtime in compiled
    /// programs). The emitted <c>$Runtime.CreateWorker</c> resolves this method by
    /// reflection — keeping the standalone DLL free of a hard SharpTS.dll reference —
    /// and passes the loop's <c>Ref</c>/<c>Unref</c> as delegates (#354).
    /// </summary>
    /// <param name="filename">Path to the TypeScript file to execute.</param>
    /// <param name="options">
    /// Worker options bag — a compiled object literal (<c>Dictionary&lt;string, object?&gt;</c>).
    /// Passed through to the constructor, which reads <c>workerData</c>/<c>transferList</c>
    /// via <see cref="ReadOption"/> regardless of representation (#380).
    /// </param>
    /// <param name="eventLoopRef">The emitted <c>$EventLoop</c> instance's <c>Ref</c>.</param>
    /// <param name="eventLoopUnref">The emitted <c>$EventLoop</c> instance's <c>Unref</c>.</param>
    public static SharpTSWorker CreateForCompiledLoop(
        string filename, object? options, Action eventLoopRef, Action eventLoopUnref,
        Action<Action> eventLoopSchedule)
    {
        return new SharpTSWorker(filename, options, parentInterpreter: null,
            eventLoopRef, eventLoopUnref, eventLoopSchedule);
    }

    /// <summary>
    /// Drops the running-worker keep-alive Ref against the parent loop and marks it
    /// permanently released, so a later <see cref="Ref"/>/<see cref="Unref"/> from
    /// guest code becomes a no-op. Idempotent and safe to call from either the parent
    /// thread (terminate) or the worker thread (exit).
    /// </summary>
    private void ReleaseRunningRef()
    {
        lock (_refLock)
        {
            if (_refReleased)
                return;
            _refReleased = true;
            if (_refed)
            {
                _refed = false;
                _loopUnref?.Invoke();
            }
        }
    }

    /// <summary>
    /// The main entry point for the worker thread.
    /// </summary>
    private void WorkerThreadMain()
    {
        try
        {
            RunWorkerScript();
        }
        catch (Exception ex)
        {
            _workerError = ex;
            // Emit error on parent thread
            EnqueueErrorToParent(ex);
        }
        finally
        {
            _isRunning = false;
            _parentToWorkerQueue.CompleteAdding();
            _workerToParentQueue.CompleteAdding();

            // Worker is done — stop keeping the parent loop alive (Node unrefs an
            // exited worker). Released before the exit event is enqueued so the
            // parent's only remaining work is that event's delivery timer, which
            // Refs the loop itself for its in-flight window.
            ReleaseRunningRef();

            // Notify parent that worker has exited
            EnqueueExitToParent(0);
        }
    }

    /// <summary>
    /// Runs the worker script in an isolated interpreter.
    /// </summary>
    private void RunWorkerScript()
    {
        // Resolve the script path
        string absolutePath = Path.GetFullPath(_scriptPath);
        if (!File.Exists(absolutePath))
        {
            throw new Exception($"Worker script not found: {absolutePath}");
        }

        string source = File.ReadAllText(absolutePath);

        // Create isolated interpreter for this worker
        using var interpreter = new Interpreter();

        // Set up worker globals
        SetupWorkerGlobals(interpreter);

        // Set up message handling loop
        var messageHandler = new WorkerMessageHandler(this, interpreter);
        messageHandler.Start();

        try
        {
            // A worker whose script uses import/export must run through the same
            // module pipeline the parent uses — the bare single-file path rejects any
            // import at type-check ("Import statements require module mode"), which
            // includes the canonical `import { workerData } from "worker_threads"` (#410).
            if (UsesModuleSyntax(source, absolutePath))
            {
                RunWorkerModule(interpreter, absolutePath);
            }
            else
            {
                RunWorkerSingleFile(interpreter, source);
            }
        }
        finally
        {
            messageHandler.Stop();
        }
    }

    /// <summary>
    /// Decides whether the worker script must be run in module mode. Mirrors the
    /// trigger the CLI uses for the parent (<c>Program.RunFile</c>): an
    /// <c>import</c>/<c>export</c> statement, a triple-slash path reference, or a
    /// CommonJS file using <c>require</c>/<c>module.exports</c>/<c>exports.</c>.
    /// </summary>
    private static bool UsesModuleSyntax(string source, string absolutePath)
    {
        var lexer = new Lexer(source);
        lexer.ScanTokens();
        bool hasPathReferences =
            lexer.TripleSlashDirectives.Any(d => d.Type == TripleSlashReferenceType.Path);

        bool isCjsFile = CommonJsDetector.Detect(absolutePath) == CommonJsDetector.ModuleKind.CommonJs
            && (source.Contains("require(") || source.Contains("module.exports") || source.Contains("exports."));

        return hasPathReferences || source.Contains("import ") || source.Contains("export ") || isCjsFile;
    }

    /// <summary>
    /// Runs a script-mode worker (no import/export) on a single-file pipeline.
    /// </summary>
    private static void RunWorkerSingleFile(Interpreter interpreter, string source)
    {
        var lexer = new Lexer(source);
        var tokens = lexer.ScanTokens();
        var parser = new Parser(tokens);
        var parseResult = parser.Parse();

        if (!parseResult.IsSuccess)
        {
            throw new Exception($"Worker script parse error: {parseResult.Diagnostics.FirstOrDefault()?.Message ?? "Unknown error"}");
        }

        // Type check. AsWorkerContext lets the worker-scoped globals (parentPort,
        // postMessage, workerData, threadId, isMainThread) resolve instead of
        // failing as undefined — they're bound by SetupWorkerGlobals.
        var typeChecker = new TypeChecker().AsWorkerContext();
        var typeMap = typeChecker.Check(parseResult.Statements);

        interpreter.Interpret(parseResult.Statements, typeMap);
    }

    /// <summary>
    /// Runs a module-mode worker through the same resolver/type-check/interpret
    /// pipeline the parent uses, so <c>import</c>/<c>export</c> (including
    /// <c>import { workerData, parentPort } from "worker_threads"</c>) resolve. The
    /// worker-context module bindings injected by <see cref="SetupWorkerGlobals"/>
    /// make the imported worker_threads identity exports carry this worker's live
    /// values (#410).
    /// </summary>
    private static void RunWorkerModule(Interpreter interpreter, string absolutePath)
    {
        var resolver = new ModuleResolver(absolutePath);
        var entryModule = resolver.LoadModule(absolutePath);
        var allModules = resolver.GetModulesInOrder(entryModule);

        // AsWorkerContext keeps the bare worker-scoped globals resolving as `any` in
        // every module, matching the single-file path.
        var typeChecker = new TypeChecker().AsWorkerContext();
        var typeMap = typeChecker.CheckModules(allModules, resolver);

        var firstError = typeChecker.GetDiagnostics()
            .FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (firstError != null)
        {
            throw new Exception($"Worker script type error: {firstError}");
        }

        // Variable resolution for O(1) lookups (built-in modules have no user statements).
        var varResolver = new VariableResolver(interpreter);
        foreach (var module in allModules)
        {
            if (!module.IsBuiltIn)
                varResolver.Resolve(module.Statements);
        }

        interpreter.InterpretModules(allModules, resolver, typeMap);
    }

    /// <summary>
    /// Sets up worker-specific global variables.
    /// </summary>
    private void SetupWorkerGlobals(Interpreter interpreter)
    {
        var env = interpreter.Environment;

        // isMainThread - always false in worker
        env.Define("isMainThread", false);

        // threadId - this worker's ID
        env.Define("threadId", ThreadId);

        // workerData - data passed from parent
        env.Define("workerData", _workerData);

        // parentPort - MessagePort for communicating with parent
        var parentPort = new WorkerParentPort(this);
        env.Define("parentPort", parentPort);

        // postMessage - convenience function (same as parentPort.postMessage)
        env.Define("postMessage", BuiltInMethod.CreateV2("postMessage", 1, 2, (_, _, args) =>
        {
            if (args.Length == 0)
                throw new Exception("postMessage requires at least one argument");
            var transfer = args.Length > 1 ? args[1].ToObject() as SharpTSArray : null;
            PostMessageToParent(args[0].ToObject(), transfer);
            return RuntimeValue.Null;
        }));

        // Mirror the same live values into the worker_threads module exports so a
        // script that uses the canonical import form — `import { workerData,
        // parentPort } from "worker_threads"` — sees this worker's inputs (#410).
        // The very same parentPort instance is reused so a listener attached via the
        // import receives the messages WorkerMessageHandler delivers to the bare global.
        interpreter.WorkerThreadsContext =
            new Interpreter.WorkerThreadsBindings(_workerData, parentPort, ThreadId);
    }

    /// <summary>
    /// Posts a message from the worker to the parent.
    /// </summary>
    internal void PostMessageToParent(object? message, SharpTSArray? transfer = null)
    {
        if (_isTerminated)
            return;

        try
        {
            var cloned = StructuredClone.Clone(message, transfer);
            _workerToParentQueue.Add(new SharpTSMessagePort.ClonedMessage(cloned, transfer));

            // Schedule delivery on the parent thread. Routed through
            // ScheduleOnMainThread so compiled mode (no parent interpreter) marshals
            // the delivery onto the $EventLoop via the captured sync context — a bare
            // _parentInterpreter?.ScheduleTimer here would silently drop every worker
            // message in compiled programs (#354).
            ScheduleOnMainThread(DeliverMessagesToParent);
        }
        catch (StructuredClone.DataCloneError e)
        {
            throw new Exception($"Failed to clone message: {e.Message}");
        }
        catch (InvalidOperationException)
        {
            // Queue was completed - worker is terminating
        }
    }

    /// <summary>
    /// Delivers pending messages from worker to parent event listeners.
    /// Called from the main thread to process messages queued by the worker.
    /// </summary>
    internal void DeliverMessagesToParent()
    {
        while (_workerToParentQueue.TryTake(out var message))
        {
            var eventData = new SharpTSObject(new Dictionary<string, object?>
            {
                ["data"] = message.Data
            });

            EmitEventOnMainThread("message", eventData);
        }
    }

    /// <summary>
    /// Enqueues an error event to be delivered to the parent.
    /// </summary>
    private void EnqueueErrorToParent(Exception ex)
    {
        var errorObj = new SharpTSError(ex.Message)
        {
            Stack = ex.StackTrace ?? ""
        };
        ScheduleOnMainThread(() => EmitEventOnMainThread("error", errorObj));
    }

    /// <summary>
    /// Enqueues an exit event to be delivered to the parent.
    /// </summary>
    private void EnqueueExitToParent(int exitCode)
    {
        ScheduleOnMainThread(() => EmitEventOnMainThread("exit", (double)exitCode));
    }

    /// <summary>
    /// Schedules an action to run on the main thread.
    /// Uses interpreter timers if available, SynchronizationContext if available,
    /// or queues for manual processing via ProcessPendingCallbacks().
    /// </summary>
    private void ScheduleOnMainThread(Action action)
    {
        if (_parentInterpreter != null)
        {
            // Interpreter path - use timer for callback delivery
            _parentInterpreter.ScheduleTimer(0, 0, action, false);
        }
        else if (_loopSchedule != null)
        {
            // Compiled path - marshal onto the emitted $EventLoop deterministically.
            _loopSchedule(action);
        }
        else if (_syncContext != null)
        {
            // Compiled path with sync context (WinForms, WPF, etc.)
            _syncContext.Post(_ => action(), null);
        }
        else
        {
            // Compiled path without sync context (console app)
            // Queue for manual processing
            _pendingCallbacks.Enqueue(action);
        }
    }

    /// <summary>
    /// Emits an event using the appropriate mechanism.
    /// Works for both interpreted and compiled code.
    /// </summary>
    private void EmitEventOnMainThread(string eventName, object? data)
    {
        if (_parentInterpreter != null)
        {
            // Interpreter path - use BuiltInMethod
            var emitMethod = GetMember("emit") as BuiltInMethod;
            emitMethod?.Call(_parentInterpreter, [eventName, data]);
        }
        else
        {
            // Compiled path - use direct emit
            EmitDirect(eventName, data);
        }
    }

    /// <summary>
    /// Processes any pending callbacks queued for the main thread.
    /// Call this periodically from the main thread in console applications
    /// to receive Worker messages and events.
    /// </summary>
    /// <remarks>
    /// In GUI applications with a SynchronizationContext (WinForms, WPF),
    /// callbacks are automatically marshaled to the UI thread.
    /// In console applications, you must call this method to process events.
    /// </remarks>
    /// <example>
    /// <code>
    /// var worker = new Worker('./worker.ts');
    /// worker.on('message', (data) => console.log(data));
    ///
    /// // In console app main loop:
    /// while (worker.IsRunning) {
    ///     worker.ProcessPendingCallbacks();
    ///     Thread.Sleep(10); // Small delay to avoid busy-wait
    /// }
    /// </code>
    /// </example>
    public void ProcessPendingCallbacks()
    {
        // Process all queued messages first
        DeliverMessagesToParent();

        // Then process any pending callbacks
        while (_pendingCallbacks.TryDequeue(out var callback))
        {
            callback();
        }
    }

    /// <summary>
    /// Posts a message from the parent to the worker.
    /// </summary>
    public void PostMessage(object? message, SharpTSArray? transfer = null)
    {
        if (_isTerminated)
            return;

        try
        {
            var cloned = StructuredClone.Clone(message, transfer);
            _parentToWorkerQueue.Add(new SharpTSMessagePort.ClonedMessage(cloned, transfer));
        }
        catch (StructuredClone.DataCloneError e)
        {
            throw new Exception($"Failed to clone message: {e.Message}");
        }
        catch (InvalidOperationException)
        {
            // Queue was completed - worker is terminating
        }
    }

    /// <summary>
    /// Terminates the worker.
    /// </summary>
    public SharpTSPromise Terminate()
    {
        if (_isTerminated)
        {
            return SharpTSPromise.Resolve((double)0);
        }

        _isTerminated = true;
        _cts.Cancel();
        _parentToWorkerQueue.CompleteAdding();

        // The worker is being torn down — drop its running keep-alive Ref now
        // rather than waiting for the worker thread's finally, so the only loop
        // keep-alive from here is the bounded join Ref below. Idempotent with the
        // worker-thread finally's ReleaseRunningRef.
        ReleaseRunningRef();

        // The thread join is real, always-completing work bounded at 5s. Ref the
        // owning event loop for its duration so the quiescence heuristic does not
        // abandon a program whose only remaining work is `await worker.terminate()`
        // when the worker takes >250ms to wind down (the #319/#320 native-I/O
        // liveness class; #324). Unref once the join settles. Accounted against the
        // parent interpreter (interpreter mode) or the emitted $EventLoop (compiled
        // mode, #354); a no-op when neither is present.
        var loopUnref = _loopUnref;
        _loopRef?.Invoke();
        var task = Task.Run<object?>(() =>
        {
            _thread.Join(5000); // Wait up to 5 seconds
            return (double)0;
        });
        if (_loopRef != null && loopUnref != null)
        {
            task.ContinueWith(_ => loopUnref(), TaskScheduler.Default);
        }
        return new SharpTSPromise(task);
    }

    /// <summary>
    /// Opts the worker back into keeping the parent event loop alive (Node's
    /// <c>worker.ref()</c>). No-op once the worker has exited or been terminated,
    /// or when no event loop owns the worker.
    /// </summary>
    public SharpTSWorker Ref()
    {
        lock (_refLock)
        {
            if (!_refReleased && !_refed && _loopRef != null)
            {
                _refed = true;
                _loopRef();
            }
        }
        return this;
    }

    /// <summary>
    /// Opts the worker out of keeping the parent event loop alive (Node's
    /// <c>worker.unref()</c>). The worker keeps running; the parent is just free to
    /// exit if the worker is its only remaining work. No-op once the worker has
    /// exited/terminated, or when no event loop owns the worker.
    /// </summary>
    public void Unref()
    {
        lock (_refLock)
        {
            if (_refed)
            {
                _refed = false;
                _loopUnref?.Invoke();
            }
        }
    }

    /// <summary>
    /// Gets a member (method or property) by name.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            "threadId" => ThreadId,

            "postMessage" => BuiltInMethod.CreateV2("postMessage", 1, 2, (_, _, args) =>
            {
                if (args.Length == 0)
                    throw new Exception("postMessage requires at least one argument");
                var transfer = args.Length > 1 ? args[1].ToObject() as SharpTSArray : null;
                PostMessage(args[0].ToObject(), transfer);
                return RuntimeValue.Null;
            }),

            "terminate" => BuiltInMethod.CreateV2("terminate", 0, (_, _, _) => RuntimeValue.FromObject(Terminate())),

            "ref" => BuiltInMethod.CreateV2("ref", 0, (_, _, _) => RuntimeValue.FromObject(Ref())),

            "unref" => BuiltInMethod.CreateV2("unref", 0, (_, _, _) =>
            {
                Unref();
                return RuntimeValue.FromObject(this);
            }),

            // Inherit EventEmitter methods for on/once/emit
            _ => base.GetMember(name)
        };
    }

    /// <summary>
    /// Checks for pending messages from parent (called by worker thread).
    /// </summary>
    internal bool TryReceiveMessage(out SharpTSMessagePort.ClonedMessage? message, int timeoutMs = 0)
    {
        if (timeoutMs <= 0)
        {
            return _parentToWorkerQueue.TryTake(out message);
        }
        return _parentToWorkerQueue.TryTake(out message, timeoutMs);
    }

    /// <summary>
    /// Gets whether the worker is terminated.
    /// </summary>
    internal bool IsTerminated => _isTerminated;

    /// <summary>
    /// Gets the cancellation token for the worker.
    /// </summary>
    internal CancellationToken CancellationToken => _cts.Token;

    public void Dispose()
    {
        Terminate().Task.Wait(1000);
        _cts.Dispose();
        _parentToWorkerQueue.Dispose();
        _workerToParentQueue.Dispose();
        GC.SuppressFinalize(this);
    }

    public override string ToString() => $"Worker {{ threadId: {ThreadId} }}";
}

/// <summary>
/// MessagePort-like object for worker to communicate with parent.
/// </summary>
internal class WorkerParentPort : SharpTSEventEmitter
{
    private readonly SharpTSWorker _worker;

    // RuntimeCategory deliberately not overridden — see SharpTSMessagePort.
    // The EventEmitter category dispatched through a base-typed cast that
    // hid this class's postMessage (#209); the per-type registration in
    // BuiltInRegistry reaches the derived GetMember instead.

    public WorkerParentPort(SharpTSWorker worker)
    {
        _worker = worker;
    }

    public void PostMessage(object? message, SharpTSArray? transfer = null)
    {
        _worker.PostMessageToParent(message, transfer);
    }

    public new object? GetMember(string name)
    {
        return name switch
        {
            "postMessage" => BuiltInMethod.CreateV2("postMessage", 1, 2, (_, _, args) =>
            {
                if (args.Length == 0)
                    throw new Exception("postMessage requires at least one argument");
                var transfer = args.Length > 1 ? args[1].ToObject() as SharpTSArray : null;
                PostMessage(args[0].ToObject(), transfer);
                return RuntimeValue.Null;
            }),

            // Inherit EventEmitter methods
            _ => base.GetMember(name)
        };
    }

    public override string ToString() => "MessagePort { parentPort }";
}

/// <summary>
/// Handles message delivery on the worker thread.
/// </summary>
internal class WorkerMessageHandler
{
    private readonly SharpTSWorker _worker;
    private readonly Interpreter _interpreter;
    private Timer? _pollTimer;

    public WorkerMessageHandler(SharpTSWorker worker, Interpreter interpreter)
    {
        _worker = worker;
        _interpreter = interpreter;
    }

    public void Start()
    {
        // Poll for messages periodically
        _pollTimer = new Timer(PollMessages, null, 10, 10);
    }

    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private void PollMessages(object? state)
    {
        if (_worker.IsTerminated)
            return;

        while (_worker.TryReceiveMessage(out var message))
        {
            if (message == null)
                continue;

            try
            {
                // Get the parentPort and emit message event
                if (_interpreter.Environment.TryGet("parentPort", out var portRV) &&
                    portRV.ToObject() is WorkerParentPort parentPort)
                {
                    var eventData = new SharpTSObject(new Dictionary<string, object?>
                    {
                        ["data"] = message.Data
                    });

                    // Emit on parentPort
                    var emitMethod = parentPort.GetMember("emit") as BuiltInMethod;
                    emitMethod?.Call(_interpreter, ["message", eventData]);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Worker message handler error: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Static helper for worker_threads module functionality.
/// </summary>
public static class WorkerThreads
{
    /// <summary>
    /// Gets whether the current thread is the main thread.
    /// </summary>
    public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == 1 ||
                                       !Thread.CurrentThread.Name?.StartsWith("SharpTS-Worker-") == true;

    /// <summary>
    /// Gets the current thread's ID.
    /// </summary>
    public static double ThreadId => Thread.CurrentThread.ManagedThreadId;

    /// <summary>
    /// Synchronously receives a message from a MessagePort.
    /// </summary>
    public static object? ReceiveMessageOnPort(SharpTSMessagePort port)
    {
        return port.ReceiveMessageSync();
    }

    /// <summary>
    /// Creates a worker_threads module exports object.
    /// </summary>
    public static SharpTSObject CreateModuleExports()
    {
        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["Worker"] = new WorkerConstructor(),
            ["isMainThread"] = IsMainThread,
            ["threadId"] = ThreadId,
            ["workerData"] = null, // Set in worker context
            ["parentPort"] = null, // Set in worker context
            ["MessageChannel"] = new MessageChannelConstructor(),
            ["MessagePort"] = null, // Can't construct directly
            ["receiveMessageOnPort"] = BuiltInMethod.CreateV2("receiveMessageOnPort", 1, static (_, _, args) =>
            {
                object? result = (args.Length == 0 ? null : args[0].ToObject()) switch
                {
                    SharpTSMessagePort port => ReceiveMessageOnPort(port),
                    CompiledMessagePortBridge bridge => bridge.ReceiveMessageSync(),
                    _ => throw new Exception("receiveMessageOnPort requires a MessagePort argument"),
                };
                return RuntimeValue.FromBoxed(result);
            }),
        });
    }
}

/// <summary>
/// Constructor for Worker class.
/// </summary>
internal class WorkerConstructor : ISharpTSCallable
{
    public int Arity() => 1;

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        if (arguments.Count == 0 || arguments[0] is not string filename)
            throw new Exception("Worker constructor requires a filename argument");

        var options = arguments.Count > 1 ? arguments[1] as SharpTSObject : null;
        return new SharpTSWorker(filename, options, interpreter);
    }
}

/// <summary>
/// Constructor for MessageChannel class.
/// </summary>
internal class MessageChannelConstructor : ISharpTSCallable
{
    public int Arity() => 0;

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        return new SharpTSMessageChannel();
    }
}
