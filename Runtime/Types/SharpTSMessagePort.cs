using System.Collections.Concurrent;
using SharpTS.Runtime.BuiltIns;
using SharpTS.TypeSystem;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Represents a MessagePort for bidirectional communication between threads.
/// </summary>
/// <remarks>
/// MessagePort provides asynchronous message passing between workers and the main thread,
/// or between a pair of ports created via MessageChannel. Messages are cloned using the
/// structured clone algorithm, except SharedArrayBuffer which is shared by reference.
/// </remarks>
public class SharpTSMessagePort : SharpTSEventEmitter
{
    /// <summary>
    /// Internal queue for incoming messages.
    /// </summary>
    private readonly BlockingCollection<ClonedMessage> _queue = new();

    /// <summary>
    /// The paired port (for MessageChannel-created ports).
    /// </summary>
    private SharpTSMessagePort? _partner;

    /// <summary>
    /// Whether this port has been started (messages are delivered).
    /// </summary>
    private bool _started;

    /// <summary>
    /// Whether this port has been closed.
    /// </summary>
    private bool _closed;

    /// <summary>
    /// Whether this port has been neutered (transferred).
    /// </summary>
    private bool _neutered;

    /// <summary>
    /// The interpreter to use for event dispatch (set when added to a context).
    /// </summary>
    internal Interp? OwnerInterpreter { get; set; }

    // NOTE: RuntimeCategory deliberately NOT overridden to EventEmitter.
    // The base virtual returns Unknown for subclasses, which routes property
    // access through the per-type instance registration (BuiltInRegistry) and
    // reaches this class's GetMember. Forcing the EventEmitter category here
    // dispatched through a base-typed cast, so the port-specific members
    // (postMessage/start/close) resolved as undefined (#209).

    /// <summary>
    /// Represents a cloned message ready for delivery.
    /// </summary>
    internal record ClonedMessage(object? Data, SharpTSArray? Transfer);

    /// <summary>
    /// Sets the partner port for bidirectional communication.
    /// </summary>
    internal void SetPartner(SharpTSMessagePort partner)
    {
        _partner = partner;
    }

    /// <summary>
    /// Marks this port as neutered (after transfer).
    /// </summary>
    internal void Neuter()
    {
        _neutered = true;
    }

    /// <summary>
    /// Posts a message to the partner port or worker.
    /// </summary>
    public void PostMessage(object? message, SharpTSArray? transfer = null)
    {
        if (_neutered)
            throw new Exception("DataCloneError: Cannot post message on neutered port");

        if (_closed)
            return; // Silently ignore messages to closed ports

        // Clone the message
        var clonedMessage = StructuredClone.Clone(message, transfer);

        if (_partner != null && !_partner._closed)
        {
            // Direct delivery to partner port
            _partner.EnqueueMessage(new ClonedMessage(clonedMessage, transfer));
        }
        // If no partner, this might be a worker port - subclasses can override
    }

    /// <summary>
    /// Enqueues a message for delivery.
    /// </summary>
    internal void EnqueueMessage(ClonedMessage message)
    {
        if (_closed || _neutered)
            return;

        _queue.Add(message);

        // If started, trigger message delivery
        if (_started && OwnerInterpreter != null)
        {
            DeliverPendingMessages();
        }
    }

    /// <summary>
    /// Starts receiving messages (explicit start required for ports from MessageChannel).
    /// </summary>
    public void Start()
    {
        if (_started || _closed || _neutered)
            return;

        _started = true;

        // Deliver any queued messages
        if (OwnerInterpreter != null)
        {
            DeliverPendingMessages();
        }
    }

    /// <summary>
    /// Closes the port, preventing further message sending/receiving.
    /// </summary>
    public void Close()
    {
        if (_closed)
            return;

        _closed = true;
        _queue.CompleteAdding();

        // Emit close event
        if (OwnerInterpreter != null)
        {
            EmitEvent("close", []);
        }
    }

    /// <summary>
    /// Delivers pending messages to event listeners.
    /// </summary>
    internal void DeliverPendingMessages()
    {
        if (!_started || _closed || OwnerInterpreter == null)
            return;

        while (_queue.TryTake(out var message))
        {
            // Node worker_threads semantics: 'message' listeners receive the
            // cloned input value of postMessage() directly (not a browser-style
            // MessageEvent wrapper).
            EmitEvent("message", [message.Data]);
        }
    }

    /// <summary>
    /// Emits an event to listeners.
    /// </summary>
    private void EmitEvent(string eventName, List<object?> args)
    {
        if (OwnerInterpreter == null)
            return;
        base.EmitEvent(OwnerInterpreter, eventName, args);
    }

    /// <summary>
    /// Gets a member (method or property) by name.
    /// </summary>
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

            "start" => BuiltInMethod.CreateV2("start", 0, (_, _, _) =>
            {
                Start();
                return RuntimeValue.Null;
            }),

            "close" => BuiltInMethod.CreateV2("close", 0, (_, _, _) =>
            {
                Close();
                return RuntimeValue.Null;
            }),

            // Node semantics: attaching a 'message' listener implicitly starts
            // the port (https://nodejs.org/api/worker_threads.html#event-message).
            // MessageChannel-created ports also have no owner interpreter until
            // someone interacts with them, so capture it here — without it,
            // queued messages are never delivered.
            "on" or "addListener" or "once" => BuiltInMethod.CreateV2(name, 2, (interp, _, args) =>
            {
                var eventName = args[0].ToObject()?.ToString()
                    ?? throw new Exception("Event name must be a string");
                var listener = args[1].ToObject()
                    ?? throw new Exception("Listener must be a function");
                AddListenerDirect(eventName, listener, once: name == "once");
                if (eventName == "message")
                {
                    OwnerInterpreter ??= interp;
                    Start();
                }
                return RuntimeValue.FromObject(this);
            }),

            // Inherit EventEmitter methods
            _ => base.GetMember(name)
        };
    }

    /// <summary>
    /// Receives a message synchronously (blocking). Used for receiveMessageOnPort().
    /// </summary>
    internal object? ReceiveMessageSync(int timeoutMs = 0)
    {
        if (_neutered || _closed)
            return null;

        ClonedMessage? message;
        if (timeoutMs <= 0)
        {
            if (!_queue.TryTake(out message))
                return null;
        }
        else
        {
            if (!_queue.TryTake(out message, timeoutMs))
                return null;
        }

        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["message"] = message.Data
        });
    }

    public override string ToString() => _neutered ? "MessagePort { neutered }" :
                                         _closed ? "MessagePort { closed }" :
                                         "MessagePort {}";
}
