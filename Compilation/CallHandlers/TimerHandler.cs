using System.Reflection.Emit;
using SharpTS.Compilation.Emitters;
using SharpTS.Parsing;

namespace SharpTS.Compilation.CallHandlers;

/// <summary>
/// Handles timer functions: setTimeout, clearTimeout, setInterval, clearInterval, queueMicrotask.
/// </summary>
public class TimerHandler : ICallHandler
{
    public int Priority => 45;

    public bool TryHandle(IEmitterContext emitter, Expr.Call call)
    {
        if (call.Callee is not Expr.Variable v)
            return false;

        var il = emitter.IL;
        var ctx = emitter.Context;

        return v.Name.Lexeme switch
        {
            "setTimeout" => EmitTimer(emitter, il, ctx, call, ctx.Runtime!.SetTimeout),
            "clearTimeout" => EmitClearTimer(emitter, il, ctx, call, ctx.Runtime!.ClearTimeout),
            "setInterval" => EmitTimer(emitter, il, ctx, call, ctx.Runtime!.SetInterval),
            "clearInterval" => EmitClearTimer(emitter, il, ctx, call, ctx.Runtime!.ClearInterval),
            "queueMicrotask" => EmitQueueMicrotask(emitter, il, ctx, call),
            _ => false
        };
    }

    private static bool EmitTimer(IEmitterContext emitter, ILGenerator il, CompilationContext ctx, Expr.Call call, System.Reflection.MethodInfo method)
    {
        // Callback
        if (call.Arguments.Count > 0) { emitter.EmitExpression(call.Arguments[0]); emitter.EmitBoxIfNeeded(call.Arguments[0]); } else { il.Emit(OpCodes.Ldnull); }
        // Delay
        if (call.Arguments.Count > 1) { emitter.EmitExpression(call.Arguments[1]); if (call.Arguments[1] is not Expr.Literal { Value: double }) il.Emit(OpCodes.Unbox_Any, ctx.Types.Double); } else { il.Emit(OpCodes.Ldc_R8, 0.0); }
        // Args array
        if (call.Arguments.Count > 2)
        {
            il.Emit(OpCodes.Ldc_I4, call.Arguments.Count - 2);
            il.Emit(OpCodes.Newarr, ctx.Types.Object);
            for (int i = 2; i < call.Arguments.Count; i++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i - 2);
                emitter.EmitExpression(call.Arguments[i]);
                emitter.EmitBoxIfNeeded(call.Arguments[i]);
                il.Emit(OpCodes.Stelem_Ref);
            }
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Newarr, ctx.Types.Object);
        }
        il.Emit(OpCodes.Call, method);
        emitter.SetStackUnknown();
        return true;
    }

    private static bool EmitClearTimer(IEmitterContext emitter, ILGenerator il, CompilationContext ctx, Expr.Call call, System.Reflection.MethodInfo method)
    {
        if (call.Arguments.Count > 0) { emitter.EmitExpression(call.Arguments[0]); emitter.EmitBoxIfNeeded(call.Arguments[0]); } else { il.Emit(OpCodes.Ldnull); }
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ldnull);
        return true;
    }

    private static bool EmitQueueMicrotask(IEmitterContext emitter, ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        if (call.Arguments.Count > 0) { emitter.EmitExpression(call.Arguments[0]); emitter.EmitBoxIfNeeded(call.Arguments[0]); } else { il.Emit(OpCodes.Ldnull); }
        il.Emit(OpCodes.Call, ctx.Runtime!.QueueMicrotask);
        il.Emit(OpCodes.Ldsfld, ctx.Runtime!.UndefinedInstance);
        return true;
    }
}
