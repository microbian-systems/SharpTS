using SharpTS.Runtime.Types;

namespace SharpTS.Compilation;

public static partial class RuntimeTypes
{
    #region AbortController / AbortSignal Support

    /// <summary>
    /// Creates a new AbortController.
    /// </summary>
    public static object CreateAbortController()
    {
        return new SharpTSAbortController();
    }

    /// <summary>
    /// Calls abort on an AbortController with an optional reason.
    /// Returns undefined (null).
    /// </summary>
    public static object? AbortControllerAbort(object? controller, object? reason)
    {
        if (controller is SharpTSAbortController ac)
        {
            ac.Abort(reason);
        }
        return null;
    }

    /// <summary>
    /// Gets the signal from an AbortController.
    /// </summary>
    public static object? AbortControllerGetSignal(object? controller)
    {
        if (controller is SharpTSAbortController ac)
        {
            return ac.Signal;
        }
        return null;
    }

    /// <summary>
    /// Gets the aborted property from an AbortSignal.
    /// </summary>
    public static bool AbortSignalGetAborted(object? signal)
    {
        if (signal is SharpTSAbortSignal s)
        {
            return s.Aborted;
        }
        return false;
    }

    /// <summary>
    /// Gets the reason property from an AbortSignal.
    /// </summary>
    public static object? AbortSignalGetReason(object? signal)
    {
        if (signal is SharpTSAbortSignal s)
        {
            return s.Reason;
        }
        return null;
    }

    /// <summary>
    /// Gets the onabort property from an AbortSignal.
    /// </summary>
    public static object? AbortSignalGetOnAbort(object? signal)
    {
        if (signal is SharpTSAbortSignal s)
        {
            return s.OnAbort;
        }
        return null;
    }

    /// <summary>
    /// Sets the onabort property on an AbortSignal.
    /// </summary>
    public static void AbortSignalSetOnAbort(object? signal, object? handler)
    {
        if (signal is SharpTSAbortSignal s)
        {
            s.OnAbort = handler;
        }
    }

    /// <summary>
    /// Calls throwIfAborted on an AbortSignal.
    /// </summary>
    public static void AbortSignalThrowIfAborted(object? signal)
    {
        if (signal is SharpTSAbortSignal s)
        {
            s.ThrowIfAborted();
        }
    }

    /// <summary>
    /// Adds an event listener to an AbortSignal. Returns undefined.
    /// </summary>
    public static object? AbortSignalAddEventListener(object? signal, string type, object? listener)
    {
        if (signal is SharpTSAbortSignal s && listener != null)
        {
            s.AddEventListener(type, listener);
        }
        return null;
    }

    /// <summary>
    /// Removes an event listener from an AbortSignal. Returns undefined.
    /// </summary>
    public static object? AbortSignalRemoveEventListener(object? signal, string type, object? listener)
    {
        if (signal is SharpTSAbortSignal s && listener != null)
        {
            s.RemoveEventListener(type, listener);
        }
        return null;
    }

    /// <summary>
    /// Static factory: AbortSignal.abort(reason?)
    /// </summary>
    public static object AbortSignalAbort(object? reason)
    {
        return SharpTSAbortSignal.Abort(reason);
    }

    /// <summary>
    /// Static factory: AbortSignal.timeout(ms)
    /// </summary>
    public static object AbortSignalTimeout(double ms)
    {
        return SharpTSAbortSignal.Timeout(ms);
    }

    /// <summary>
    /// Static factory: AbortSignal.any(signals)
    /// </summary>
    public static object AbortSignalAny(object? signals)
    {
        if (signals is SharpTSArray arr)
        {
            var abortSignals = arr.Elements
                .Where(e => e is SharpTSAbortSignal)
                .Cast<SharpTSAbortSignal>();
            return SharpTSAbortSignal.Any(abortSignals);
        }
        throw new Exception("Runtime Error: AbortSignal.any requires an array of AbortSignal instances");
    }

    #endregion
}
