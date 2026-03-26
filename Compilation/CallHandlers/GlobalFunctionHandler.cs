using SharpTS.Parsing;

namespace SharpTS.Compilation.CallHandlers;

/// <summary>
/// Handles global built-in functions: parseInt, parseFloat, isNaN, isFinite.
/// Note: These are now also handled by ExpressionEmitterBase.EmitCall directly,
/// so this handler serves as an additional dispatch path for ILEmitter only.
/// The base class dispatch catches these before they reach the handler chain
/// in non-ILEmitter emitters.
/// </summary>
public class GlobalFunctionHandler : ICallHandler
{
    public int Priority => 50;

    public bool TryHandle(ILEmitter emitter, Expr.Call call)
    {
        if (call.Callee is not Expr.Variable v)
            return false;

        var il = emitter.ILGen;
        var ctx = emitter.Context;

        return v.Name.Lexeme switch
        {
            "parseInt" => EmitParseInt(emitter, il, ctx, call),
            "parseFloat" => EmitParseFloat(emitter, il, ctx, call),
            "isNaN" => EmitIsNaN(emitter, il, ctx, call),
            "isFinite" => EmitIsFinite(emitter, il, ctx, call),
            _ => false
        };
    }

    private static bool EmitParseInt(ILEmitter emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        if (call.Arguments.Count > 0) { emitter.EmitExpression(call.Arguments[0]); emitter.EmitBoxIfNeeded(call.Arguments[0]); } else { il.Emit(System.Reflection.Emit.OpCodes.Ldnull); }
        if (call.Arguments.Count > 1) { emitter.EmitExpression(call.Arguments[1]); emitter.EmitBoxIfNeeded(call.Arguments[1]); } else { il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, 10); il.Emit(System.Reflection.Emit.OpCodes.Box, ctx.Types.Int32); }
        il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.NumberParseInt);
        il.Emit(System.Reflection.Emit.OpCodes.Box, ctx.Types.Double);
        return true;
    }

    private static bool EmitParseFloat(ILEmitter emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        if (call.Arguments.Count > 0) { emitter.EmitExpression(call.Arguments[0]); emitter.EmitBoxIfNeeded(call.Arguments[0]); } else { il.Emit(System.Reflection.Emit.OpCodes.Ldnull); }
        il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.NumberParseFloat);
        il.Emit(System.Reflection.Emit.OpCodes.Box, ctx.Types.Double);
        return true;
    }

    private static bool EmitIsNaN(ILEmitter emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        if (call.Arguments.Count > 0) { emitter.EmitExpression(call.Arguments[0]); emitter.EmitBoxIfNeeded(call.Arguments[0]); } else { il.Emit(System.Reflection.Emit.OpCodes.Ldnull); }
        il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.GlobalIsNaN);
        il.Emit(System.Reflection.Emit.OpCodes.Box, ctx.Types.Boolean);
        return true;
    }

    private static bool EmitIsFinite(ILEmitter emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        if (call.Arguments.Count > 0) { emitter.EmitExpression(call.Arguments[0]); emitter.EmitBoxIfNeeded(call.Arguments[0]); } else { il.Emit(System.Reflection.Emit.OpCodes.Ldnull); }
        il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.GlobalIsFinite);
        il.Emit(System.Reflection.Emit.OpCodes.Box, ctx.Types.Boolean);
        return true;
    }
}
