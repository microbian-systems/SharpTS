using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of Node.js AsyncLocalStorage.
/// Uses .NET's AsyncLocal&lt;T&gt; to automatically propagate context across
/// await boundaries, Promise chains, and timer callbacks.
/// </summary>
public class SharpTSAsyncLocalStorage
{
    /// <summary>Sentinel value to distinguish "not set" from "explicitly set to null".</summary>
    private static readonly object NotSet = new();

    private readonly AsyncLocal<object?> _store = new();
    private bool _enabled = true;

    public SharpTSAsyncLocalStorage()
    {
        _store.Value = NotSet;
    }

    /// <summary>
    /// Gets a member (method) by name for interpreter dispatch.
    /// </summary>
    public object? GetMember(string name)
    {
        return name switch
        {
            "run" => new BuiltInMethod("run", 2, int.MaxValue, Run),
            "getStore" => new BuiltInMethod("getStore", 0, GetStore),
            "enterWith" => new BuiltInMethod("enterWith", 1, EnterWith),
            "exit" => new BuiltInMethod("exit", 1, int.MaxValue, Exit),
            "disable" => new BuiltInMethod("disable", 0, Disable),
            _ => null
        };
    }

    /// <summary>
    /// Runs a callback with the given store value. The store is visible via getStore()
    /// inside the callback and any async operations it spawns. The previous store
    /// is restored after the callback completes.
    /// </summary>
    private object? Run(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count < 2)
            throw new Exception("run() requires store and callback arguments");

        var store = args[0];
        var callback = args[1];

        // Collect extra args for the callback
        var callbackArgs = args.Count > 2 ? args.GetRange(2, args.Count - 2) : [];

        var oldValue = _store.Value;
        _store.Value = store;
        try
        {
            return InvokeCallback(interpreter, callback, callbackArgs);
        }
        finally
        {
            _store.Value = oldValue;
        }
    }

    /// <summary>
    /// Returns the current store value, or null (undefined) if not inside a run() call
    /// or if the storage has been disabled.
    /// </summary>
    private object? GetStore(Interp interpreter, object? receiver, List<object?> args)
    {
        if (!_enabled) return null;
        var val = _store.Value;
        return ReferenceEquals(val, NotSet) ? null : val;
    }

    /// <summary>
    /// Sets the store for the current async context without running a callback.
    /// The store will be visible in subsequent code in this async flow.
    /// </summary>
    private object? EnterWith(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count < 1)
            throw new Exception("enterWith() requires a store argument");

        _store.Value = args[0];
        return null;
    }

    /// <summary>
    /// Runs a callback with the store cleared (set to null/undefined).
    /// The previous store is restored after the callback completes.
    /// </summary>
    private object? Exit(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count < 1)
            throw new Exception("exit() requires a callback argument");

        var callback = args[0];
        var callbackArgs = args.Count > 1 ? args.GetRange(1, args.Count - 1) : [];

        var oldValue = _store.Value;
        _store.Value = NotSet;
        try
        {
            return InvokeCallback(interpreter, callback, callbackArgs);
        }
        finally
        {
            _store.Value = oldValue;
        }
    }

    /// <summary>
    /// Disables the AsyncLocalStorage instance. After calling disable(),
    /// getStore() will always return undefined.
    /// </summary>
    private object? Disable(Interp interpreter, object? receiver, List<object?> args)
    {
        _enabled = false;
        _store.Value = null;
        return null;
    }

    /// <summary>
    /// Invokes a callback supporting multiple callable types.
    /// In interpreter mode, callbacks are ISharpTSCallable.
    /// In compiled mode, callbacks have an Invoke(object[]) method (e.g. $TSFunction).
    /// </summary>
    private static object? InvokeCallback(Interp? interpreter, object? callback, List<object?> args)
    {
        if (callback is ISharpTSCallable callable)
            return callable.CallBoxed(interpreter!, args);

        // Compiled mode: use reflection to find Invoke(object[]) on emitted types
        var invokeMethod = callback?.GetType().GetMethod("Invoke", [typeof(object?[])]);
        if (invokeMethod != null)
            return invokeMethod.Invoke(callback, [args.ToArray()]);

        throw new Exception("Callback must be a function");
    }

    public override string ToString() => "AsyncLocalStorage {}";
}
