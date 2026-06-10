using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter implementation of the worker_threads module.
/// </summary>
/// <remarks>
/// Provides Worker Threads API for parallel execution:
/// - Worker: Execute scripts in separate threads
/// - isMainThread: Check if running on main thread
/// - parentPort: MessagePort for worker-to-parent communication
/// - workerData: Data passed from parent to worker
/// - MessageChannel: Create connected message ports
/// - receiveMessageOnPort: Synchronously receive messages
/// </remarks>
public static class WorkerThreadsModuleInterpreter
{
    /// <summary>
    /// Gets all exports for the worker_threads module.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            // Worker constructor
            ["Worker"] = new WorkerConstructor(),

            // Check if current context is main thread
            ["isMainThread"] = WorkerThreads.IsMainThread,

            // Thread ID of current context
            ["threadId"] = WorkerThreads.ThreadId,

            // In main thread, these are null; in worker context, they're set up by the worker
            ["parentPort"] = null,
            ["workerData"] = null,

            // MessageChannel constructor
            ["MessageChannel"] = new MessageChannelConstructor(),

            // BroadcastChannel constructor
            ["BroadcastChannel"] = new BroadcastChannelConstructor(),

            // Synchronous message receive
            ["receiveMessageOnPort"] = BuiltInMethod.CreateV2("receiveMessageOnPort", 1, (interp, recv, args) =>
            {
                if (args.Length == 0 || args[0].ToObject() is not SharpTSMessagePort port)
                    throw new Exception("receiveMessageOnPort requires a MessagePort argument");
                return RuntimeValue.FromBoxed(port.ReceiveMessageSync());
            }),

            // SHARE_ENV constant (placeholder - we don't support env sharing)
            ["SHARE_ENV"] = new SharpTSSymbol("SHARE_ENV"),

            // resourceLimits (not fully implemented)
            ["resourceLimits"] = new SharpTSObject(new Dictionary<string, object?>()),

            // markAsUntransferable (no-op in our implementation)
            ["markAsUntransferable"] = BuiltInMethod.CreateV2("markAsUntransferable", 1, (interp, recv, args) =>
            {
                // No-op - we don't track transferability at runtime
                return RuntimeValue.Null;
            }),

            // moveMessagePortToContext (not fully implemented - requires VM)
            ["moveMessagePortToContext"] = BuiltInMethod.CreateV2("moveMessagePortToContext", 2, (interp, recv, args) =>
            {
                throw new Exception("moveMessagePortToContext is not supported in SharpTS");
            }),

            // getEnvironmentData / setEnvironmentData (environment data sharing)
            ["getEnvironmentData"] = BuiltInMethod.CreateV2("getEnvironmentData", 1, (interp, recv, args) =>
            {
                // Simple implementation - return from process.env
                if (args.Length > 0 && args[0].IsString)
                {
                    return RuntimeValue.FromBoxed(Environment.GetEnvironmentVariable(args[0].AsStringUnsafe()));
                }
                return RuntimeValue.Null;
            }),

            ["setEnvironmentData"] = BuiltInMethod.CreateV2("setEnvironmentData", 2, (interp, recv, args) =>
            {
                if (args.Length >= 2 && args[0].IsString)
                {
                    var key = args[0].AsStringUnsafe();
                    string? value = args[1].ToObject()?.ToString();
                    if (value != null)
                    {
                        Environment.SetEnvironmentVariable(key, value);
                    }
                    else
                    {
                        Environment.SetEnvironmentVariable(key, null);
                    }
                }
                return RuntimeValue.Null;
            }),
        };
    }
}
