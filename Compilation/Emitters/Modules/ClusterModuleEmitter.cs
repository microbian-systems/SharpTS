using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters.Modules;

/// <summary>
/// Emits IL code for the Node.js 'cluster' module.
/// Uses emitted $ClusterContext, $ClusterWorker, $ClusterManager types — no reflection.
/// </summary>
public sealed class ClusterModuleEmitter : IBuiltInModuleEmitter
{
    public string ModuleName => "cluster";

    private static readonly string[] _exportedMembers =
    [
        "isPrimary", "isWorker", "isMaster",
        "fork", "disconnect", "setupPrimary", "setupMaster",
        "workers", "worker", "settings",
        "on", "once", "off", "emit", "removeAllListeners",
        "addListener", "removeListener",
        "listeners", "listenerCount", "eventNames"
    ];

    public IReadOnlyList<string> GetExportedMembers() => _exportedMembers;

    public bool TryEmitMethodCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        return methodName switch
        {
            "fork" => EmitFork(emitter, arguments),
            "disconnect" => EmitDisconnect(emitter, arguments),
            "setupPrimary" or "setupMaster" => EmitSetupPrimary(emitter, arguments),
            "on" or "addListener" => EmitEventMethod(emitter, "On", arguments),
            "once" => EmitEventMethod(emitter, "Once", arguments),
            "off" or "removeListener" => EmitEventMethod(emitter, "Off", arguments),
            "emit" => EmitEventMethod(emitter, "Emit", arguments),
            "removeAllListeners" => EmitRemoveAllListeners(emitter, arguments),
            "listeners" => EmitSingleArgEventMethod(emitter, "Listeners", arguments),
            "listenerCount" => EmitSingleArgEventMethod(emitter, "ListenerCount", arguments),
            "eventNames" => EmitEventNames(emitter),
            _ => false
        };
    }

    public bool TryEmitPropertyGet(IEmitterContext emitter, string propertyName)
    {
        return propertyName switch
        {
            "isPrimary" or "isMaster" => EmitIsPrimary(emitter),
            "isWorker" => EmitIsWorker(emitter),
            "workers" => EmitGetWorkers(emitter),
            "worker" => EmitGetWorker(emitter),
            "settings" => EmitGetSettings(emitter),
            // Methods emitted as null for the namespace dict — actual calls go through TryEmitMethodCall
            "fork" or "disconnect" or "setupPrimary" or "setupMaster"
                or "on" or "once" or "off" or "emit" or "removeAllListeners"
                or "addListener" or "removeListener"
                or "listeners" or "listenerCount" or "eventNames" => EmitNull(emitter),
            _ => false
        };
    }

    private static bool EmitNull(IEmitterContext emitter)
    {
        emitter.Context.IL.Emit(OpCodes.Ldnull);
        return true;
    }

    // --- Properties: read directly from emitted types ---

    private static bool EmitIsPrimary(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        il.Emit(OpCodes.Call, ctx.Runtime!.ClusterIsPrimary);
        il.Emit(OpCodes.Box, ctx.Types.Boolean);
        return true;
    }

    private static bool EmitIsWorker(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        il.Emit(OpCodes.Call, ctx.Runtime!.ClusterIsWorker);
        il.Emit(OpCodes.Box, ctx.Types.Boolean);
        return true;
    }

    private static bool EmitGetWorkers(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        // $ClusterManager.Instance.GetWorkersObject()
        il.Emit(OpCodes.Ldsfld, ctx.Runtime!.ClusterManagerInstanceField);
        il.Emit(OpCodes.Callvirt, ctx.Runtime!.ClusterManagerGetWorkersObject);
        return true;
    }

    private static bool EmitGetWorker(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        // $ClusterContext.CurrentWorker
        il.Emit(OpCodes.Ldsfld, ctx.Runtime!.ClusterContextCurrentWorkerField);
        return true;
    }

    private static bool EmitGetSettings(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        // $ClusterManager.Instance.GetSettings()
        il.Emit(OpCodes.Ldsfld, ctx.Runtime!.ClusterManagerInstanceField);
        il.Emit(OpCodes.Callvirt, ctx.Runtime!.ClusterManagerGetSettings);
        return true;
    }

    // --- Methods: call on $ClusterManager.Instance ---

    private static bool EmitFork(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // $ClusterManager.Instance.Fork(env)
        il.Emit(OpCodes.Ldsfld, ctx.Runtime!.ClusterManagerInstanceField);
        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }
        il.Emit(OpCodes.Callvirt, ctx.Runtime!.ClusterManagerFork);
        return true;
    }

    private static bool EmitDisconnect(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        il.Emit(OpCodes.Ldsfld, ctx.Runtime!.ClusterManagerInstanceField);
        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }
        il.Emit(OpCodes.Callvirt, ctx.Runtime!.ClusterManagerDisconnectAll);

        // DisconnectAll returns void, push null for expression result
        il.Emit(OpCodes.Ldnull);
        return true;
    }

    private static bool EmitSetupPrimary(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        il.Emit(OpCodes.Ldsfld, ctx.Runtime!.ClusterManagerInstanceField);
        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }
        il.Emit(OpCodes.Callvirt, ctx.Runtime!.ClusterManagerSetupPrimary);

        il.Emit(OpCodes.Ldnull);
        return true;
    }

    // --- Event methods: call inherited $EventEmitter methods on $ClusterManager.Instance ---

    private static bool EmitEventMethod(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        if (arguments.Count < 2) return false;

        var ctx = emitter.Context;
        var il = ctx.IL;

        // $ClusterManager.Instance.On/Once/Off(eventName, listener)
        il.Emit(OpCodes.Ldsfld, ctx.Runtime!.ClusterManagerInstanceField);
        emitter.EmitExpression(arguments[0]);
        emitter.EmitBoxIfNeeded(arguments[0]);
        emitter.EmitExpression(arguments[1]);
        emitter.EmitBoxIfNeeded(arguments[1]);

        var method = methodName switch
        {
            "On" => ctx.Runtime!.TSEventEmitterOn,
            "Once" => ctx.Runtime!.TSEventEmitterOnce,
            "Off" => ctx.Runtime!.TSEventEmitterOff,
            "Emit" => ctx.Runtime!.TSEventEmitterEmit,
            _ => throw new Exception($"Unknown event method: {methodName}")
        };
        il.Emit(OpCodes.Callvirt, method);
        return true;
    }

    private static bool EmitRemoveAllListeners(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        il.Emit(OpCodes.Ldsfld, ctx.Runtime!.ClusterManagerInstanceField);
        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }
        il.Emit(OpCodes.Callvirt, ctx.Runtime!.TSEventEmitterRemoveAllListeners);
        return true;
    }

    private static bool EmitSingleArgEventMethod(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        if (arguments.Count < 1) return false;

        var ctx = emitter.Context;
        var il = ctx.IL;

        il.Emit(OpCodes.Ldsfld, ctx.Runtime!.ClusterManagerInstanceField);
        emitter.EmitExpression(arguments[0]);
        emitter.EmitBoxIfNeeded(arguments[0]);

        var method = methodName switch
        {
            "Listeners" => ctx.Runtime!.TSEventEmitterListeners,
            "ListenerCount" => ctx.Runtime!.TSEventEmitterListenerCount,
            _ => throw new Exception($"Unknown event method: {methodName}")
        };
        il.Emit(OpCodes.Callvirt, method);
        return true;
    }

    private static bool EmitEventNames(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        il.Emit(OpCodes.Ldsfld, ctx.Runtime!.ClusterManagerInstanceField);
        il.Emit(OpCodes.Callvirt, ctx.Runtime!.TSEventEmitterEventNames);
        return true;
    }
}
