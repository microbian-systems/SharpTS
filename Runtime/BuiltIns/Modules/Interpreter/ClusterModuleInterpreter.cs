using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter implementation of the Node.js 'cluster' module.
/// Provides multi-process-like patterns using threads, following the worker_threads model.
/// </summary>
public static class ClusterModuleInterpreter
{
    /// <summary>
    /// Gets all exports for the cluster module.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        var singleton = ClusterSingleton.Instance;

        return new Dictionary<string, object?>
        {
            // Static properties — evaluated at import time, correct for this thread
            ["isPrimary"] = ClusterContext.IsPrimary,
            ["isWorker"] = ClusterContext.IsWorker,
            ["isMaster"] = ClusterContext.IsPrimary,

            // Dynamic properties — return fresh value each time via BuiltInMethod acting as getter
            // The module export SharpTSObject doesn't support live getters, so we snapshot here.
            // Tests that need live workers dict should access it via cluster.fork() return values.
            ["workers"] = singleton.GetWorkersObject(),

            // Current worker reference (non-null in worker context)
            ["worker"] = ClusterContext.CurrentWorker,

            // Settings
            ["settings"] = singleton.GetSettings(),

            // Methods
            ["fork"] = new BuiltInMethod("fork", 0, 1, Fork),
            ["disconnect"] = new BuiltInMethod("disconnect", 0, 1, Disconnect),
            ["setupPrimary"] = new BuiltInMethod("setupPrimary", 0, 1, SetupPrimary),
            ["setupMaster"] = new BuiltInMethod("setupMaster", 0, 1, SetupPrimary),

            // EventEmitter methods — delegate to singleton
            ["on"] = singleton.GetMember("on")!,
            ["once"] = singleton.GetMember("once")!,
            ["off"] = singleton.GetMember("off")!,
            ["addListener"] = singleton.GetMember("addListener")!,
            ["removeListener"] = singleton.GetMember("removeListener")!,
            ["emit"] = singleton.GetMember("emit")!,
            ["removeAllListeners"] = singleton.GetMember("removeAllListeners")!,
            ["listeners"] = singleton.GetMember("listeners")!,
            ["listenerCount"] = singleton.GetMember("listenerCount")!,
            ["eventNames"] = singleton.GetMember("eventNames")!,
        };
    }

    /// <summary>
    /// cluster.fork(env?) — spawns a new worker that re-executes the entry script.
    /// </summary>
    private static object? Fork(Execution.Interpreter interpreter, object? receiver, List<object?> args)
    {
        // Capture entry script from interpreter
        var entryPath = interpreter.EntryModulePath;
        if (entryPath == null)
            throw new Exception("Runtime Error: cluster.fork() cannot determine entry script path");

        ClusterSingleton.Instance.SetEntryScript(entryPath);

        Dictionary<string, object?>? env = null;
        if (args.Count > 0 && args[0] is SharpTSObject envObj)
        {
            env = new Dictionary<string, object?>();
            foreach (var key in envObj.PropertyNames)
            {
                env[key] = envObj.GetProperty(key);
            }
        }

        return ClusterSingleton.Instance.Fork(env, interpreter);
    }

    /// <summary>
    /// cluster.disconnect(callback?) — disconnects all workers.
    /// </summary>
    private static object? Disconnect(Execution.Interpreter interpreter, object? receiver, List<object?> args)
    {
        ClusterSingleton.Instance.DisconnectAll(args.Count > 0 ? args[0] : null);
        return null;
    }

    /// <summary>
    /// cluster.setupPrimary(settings?) — stores settings object.
    /// </summary>
    private static object? SetupPrimary(Execution.Interpreter interpreter, object? receiver, List<object?> args)
    {
        ClusterSingleton.Instance.SetupPrimary(args.Count > 0 ? args[0] as SharpTSObject : null);
        return null;
    }
}
