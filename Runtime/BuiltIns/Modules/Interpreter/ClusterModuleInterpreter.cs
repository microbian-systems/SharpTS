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
            ["fork"] = BuiltInMethod.CreateV2("fork", 0, 1, Fork),
            ["disconnect"] = BuiltInMethod.CreateV2("disconnect", 0, 1, Disconnect),
            ["setupPrimary"] = BuiltInMethod.CreateV2("setupPrimary", 0, 1, SetupPrimary),
            ["setupMaster"] = BuiltInMethod.CreateV2("setupMaster", 0, 1, SetupPrimary),

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
    private static RuntimeValue Fork(Execution.Interpreter interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        // Capture entry script from interpreter
        var entryPath = interpreter.EntryModulePath;
        if (entryPath == null)
            throw new Exception("Runtime Error: cluster.fork() cannot determine entry script path");

        ClusterSingleton.Instance.SetEntryScript(entryPath);

        Dictionary<string, object?>? env = null;
        if (args.Length > 0 && args[0].ToObject() is SharpTSObject envObj)
        {
            env = new Dictionary<string, object?>();
            foreach (var key in envObj.PropertyNames)
            {
                env[key] = envObj.GetProperty(key);
            }
        }

        return RuntimeValue.FromBoxed(ClusterSingleton.Instance.Fork(env, interpreter));
    }

    /// <summary>
    /// cluster.disconnect(callback?) — disconnects all workers.
    /// </summary>
    private static RuntimeValue Disconnect(Execution.Interpreter interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        ClusterSingleton.Instance.DisconnectAll(args.Length > 0 ? args[0].ToObject() : null);
        return RuntimeValue.Null;
    }

    /// <summary>
    /// cluster.setupPrimary(settings?) — stores settings object.
    /// </summary>
    private static RuntimeValue SetupPrimary(Execution.Interpreter interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        ClusterSingleton.Instance.SetupPrimary(args.Length > 0 ? args[0].ToObject() as SharpTSObject : null);
        return RuntimeValue.Null;
    }
}
