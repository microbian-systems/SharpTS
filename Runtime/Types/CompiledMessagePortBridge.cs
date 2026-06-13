using System.Collections.Concurrent;
using System.Reflection;
using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Worker-side adapter that lets an interpreter worker drive a MessagePort that a
/// COMPILED parent created and transferred via <c>workerData</c>/<c>transferList</c> (#406).
/// </summary>
/// <remarks>
/// A compiled parent's <c>MessageChannel</c> produces two emitted <c>$MessagePort</c>
/// objects (see <c>RuntimeEmitter.MessageChannel.cs</c>) that communicate in-process:
/// <c>postMessage</c> enqueues a structured clone to the partner's internal
/// <c>_pending</c> queue and, if the partner has started, schedules the partner's
/// <c>Drain</c> on the process-global <c>$EventLoop</c>. A worker always runs the
/// interpreter on its own thread, so it cannot call methods on the raw emitted type
/// and cannot safely be driven by the parent's <c>$EventLoop</c> (a different thread).
///
/// This bridge wraps the transferred compiled port and presents the interpreter
/// MessagePort surface (<c>postMessage</c>/<c>on</c>/<c>once</c>/<c>start</c>/
/// <c>close</c> plus the inherited EventEmitter members):
/// <list type="bullet">
/// <item><b>send</b> — <c>postMessage(v)</c> structured-clones <c>v</c> (copy
/// semantics) then reflectively invokes the compiled port's <c>PostMessage</c>,
/// reusing its enqueue-to-partner + schedule-drain-on-<c>$EventLoop</c> logic so the
/// parent's compiled listeners run on the parent loop thread.</item>
/// <item><b>receive</b> — the compiled partner's posts are always enqueued to the
/// transferred port's <c>_pending</c> queue (the drain is only <i>scheduled</i> when
/// the port has started, which this transferred port never does on the compiled
/// side). A recurring timer on the WORKER interpreter's loop drains that queue and
/// emits <c>'message'</c> to the worker's listeners on the worker thread. The
/// interval timer also keeps the worker loop alive while the port is open, matching
/// Node's "a listening port is ref'd" semantics (#406, same liveness class as #329).</item>
/// </list>
///
/// All access to the emitted type is via reflection cached at adoption time, since
/// this class lives in SharpTS.dll while the compiled port lives in the output
/// assembly. The field/method names mirrored here (<c>PostMessage</c>, <c>_pending</c>)
/// MUST stay in sync with <c>RuntimeEmitter.MessageChannel.cs</c>.
/// </remarks>
public sealed class CompiledMessagePortBridge : SharpTSEventEmitter
{
    // The poll cadence for draining the compiled port's incoming queue onto the
    // worker loop. Matches WorkerMessageHandler's 10ms parent→worker poll so the
    // two worker-bound delivery paths feel identical.
    private const int PollIntervalMs = 10;

    private readonly object _compiledPort;               // transferred emitted $MessagePort
    private readonly MethodInfo _postMessageMethod;      // $MessagePort.PostMessage(object)
    private readonly ConcurrentQueue<object> _incoming;  // $MessagePort._pending

    // The worker interpreter that owns delivery. Captured the first time the worker
    // attaches a 'message' listener / starts the port; null until then.
    private Interp? _owner;
    private Interp.VirtualTimer? _pollTimer;
    private bool _started;
    private bool _closed;
    private bool _loopRefed;

    // RuntimeCategory deliberately not overridden — see SharpTSMessagePort. The base
    // returns Unknown for subclasses, routing member access through the per-type
    // registration in BuiltInRegistry, which reaches this class's GetMember.

    private CompiledMessagePortBridge(object compiledPort, MethodInfo postMessageMethod, ConcurrentQueue<object> incoming)
    {
        _compiledPort = compiledPort;
        _postMessageMethod = postMessageMethod;
        _incoming = incoming;
    }

    /// <summary>
    /// True when <paramref name="value"/> is a compiled-mode emitted <c>$MessagePort</c>.
    /// Detected by type name (the type cannot be referenced from SharpTS.dll), the same
    /// approach <see cref="StructuredClone"/> uses for <c>$ArrayBuffer</c>/<c>$SharedArrayBuffer</c>.
    /// </summary>
    public static bool IsEmittedMessagePort(object? value) => value?.GetType().Name == "$MessagePort";

    /// <summary>
    /// Adopts a transferred compiled <c>$MessagePort</c> into a worker-usable bridge.
    /// Caches the reflection handles for the port's <c>PostMessage</c> method and
    /// <c>_pending</c> queue. Throws <see cref="StructuredClone.DataCloneError"/> if the
    /// emitted shape doesn't match what <c>RuntimeEmitter.MessageChannel.cs</c> emits.
    /// </summary>
    public static CompiledMessagePortBridge Adopt(object compiledPort)
    {
        var type = compiledPort.GetType();

        var postMessage = type.GetMethod("PostMessage", [typeof(object)])
            ?? throw new StructuredClone.DataCloneError(
                "Cannot transfer $MessagePort: no PostMessage(object) method (emitted shape changed?)");

        var pendingField = type.GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new StructuredClone.DataCloneError(
                "Cannot transfer $MessagePort: no _pending field (emitted shape changed?)");

        if (pendingField.GetValue(compiledPort) is not ConcurrentQueue<object> incoming)
            throw new StructuredClone.DataCloneError(
                "Cannot transfer $MessagePort: _pending is not a ConcurrentQueue<object>");

        return new CompiledMessagePortBridge(compiledPort, postMessage, incoming);
    }

    /// <summary>
    /// Posts a message to the compiled partner port. Structured-clones for copy
    /// semantics, then hands off to the compiled port's own delivery (enqueue +
    /// schedule the partner's drain on the parent <c>$EventLoop</c>).
    /// </summary>
    private void PostMessage(object? message)
    {
        if (_closed)
            return;

        // Copy semantics: clone on the worker side before handing to the compiled
        // port. The compiled PostMessage re-clones via its own (standalone) routine,
        // but that is a pass-through no-op for interpreter-shaped values, so the net
        // effect is exactly one structured copy and the worker never shares a mutable
        // graph with the parent.
        var clone = StructuredClone.Clone(message);
        _postMessageMethod.Invoke(_compiledPort, [clone]);
    }

    /// <summary>
    /// Begins delivery: schedules a recurring drain of the compiled port's incoming
    /// queue onto the worker loop. Idempotent. No-op until an owner interpreter has
    /// been captured (via the first <c>on('message')</c>/<c>start()</c>).
    /// </summary>
    private void Start()
    {
        if (_started || _closed || _owner == null)
            return;

        _started = true;

        // Keep the worker loop alive while the port is open. RunEventLoop's exit
        // check counts active handles and queued callbacks but NOT scheduled timers,
        // so the poll interval alone would not hold the loop open — without this Ref
        // the worker can quiesce and exit before the parent's message is ever polled
        // (Node: a listening port is ref'd; #406, same liveness class as #329).
        if (!_loopRefed)
        {
            _loopRefed = true;
            _owner.Ref();
        }

        // The interval timer drains _incoming on the worker loop thread. Delay 0 so
        // anything the parent already posted before the worker started is delivered
        // on the next tick; the partner's posts land in _incoming asynchronously, so
        // the loop must poll (the compiled enqueue cannot wake this interpreter).
        // An event-driven alternative is tracked as a follow-up (#465).
        _pollTimer = _owner.ScheduleTimer(0, PollIntervalMs, Pump, isInterval: true);
    }

    /// <summary>
    /// Drains the compiled port's incoming queue, emitting each message to the
    /// worker's 'message' listeners. Runs on the worker loop thread.
    /// </summary>
    private void Pump()
    {
        if (_closed || _owner == null)
            return;

        // The compiled sender already structured-cloned each payload before enqueuing,
        // so values here are independent copies — emit them directly (Node semantics:
        // the listener receives the value, not a {data} wrapper).
        while (_incoming.TryDequeue(out var message))
        {
            EmitEvent(_owner, "message", [message]);
        }
    }

    /// <summary>
    /// Stops worker-side delivery and cancels the poll timer so the worker loop can
    /// quiesce and the worker can exit. Emits 'close' to the worker's listeners.
    /// </summary>
    private void Close()
    {
        if (_closed)
            return;

        _closed = true;

        if (_pollTimer != null)
        {
            _pollTimer.IsCancelled = true;
            _pollTimer = null;
        }

        // Release the keep-alive Ref so the worker loop can quiesce and the worker
        // thread can exit.
        if (_loopRefed && _owner != null)
        {
            _loopRefed = false;
            _owner.Unref();
        }

        if (_owner != null)
            EmitEvent(_owner, "close", []);
    }

    /// <summary>
    /// Interpreter member dispatch. Adds the MessagePort surface on top of the
    /// inherited EventEmitter members; attaching a 'message' listener implicitly
    /// starts the port (Node worker_threads semantics, mirroring SharpTSMessagePort).
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            "postMessage" => BuiltInMethod.CreateV2("postMessage", 1, 2, (_, _, args) =>
            {
                if (args.Length == 0)
                    throw new Exception("postMessage requires at least one argument");
                // A transferList on a re-post from the worker is not supported across
                // this bridge (the worker re-transferring a compiled port); the second
                // argument is ignored rather than rejected.
                PostMessage(args[0].ToObject());
                return RuntimeValue.Null;
            }),

            "start" => BuiltInMethod.CreateV2("start", 0, (interp, _, _) =>
            {
                _owner ??= interp;
                Start();
                return RuntimeValue.Null;
            }),

            "close" => BuiltInMethod.CreateV2("close", 0, (interp, _, _) =>
            {
                _owner ??= interp;
                Close();
                return RuntimeValue.Null;
            }),

            "on" or "addListener" or "once" => BuiltInMethod.CreateV2(name, 2, (interp, _, args) =>
            {
                var eventName = args[0].ToObject()?.ToString()
                    ?? throw new Exception("Event name must be a string");
                var listener = args[1].ToObject()
                    ?? throw new Exception("Listener must be a function");
                AddListenerDirect(eventName, listener, once: name == "once");
                if (eventName == "message")
                {
                    _owner ??= interp;
                    Start();
                }
                return RuntimeValue.FromObject(this);
            }),

            // Inherit EventEmitter methods (off/emit/listeners/...).
            _ => base.GetMember(name)
        };
    }

    public override string ToString() => _closed ? "MessagePort { closed }" : "MessagePort {}";
}
