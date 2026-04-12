using SharpTS.Compilation.Emitters;
using SharpTS.Parsing;

namespace SharpTS.Compilation.CallHandlers;

/// <summary>
/// Handles global built-in functions: parseInt, parseFloat, isNaN, isFinite, String, Number, Boolean.
/// </summary>
public class GlobalFunctionHandler : ICallHandler
{
    public int Priority => 50;

    public bool TryHandle(IEmitterContext emitter, Expr.Call call)
    {
        if (call.Callee is not Expr.Variable v)
            return false;

        var il = emitter.IL;
        var ctx = emitter.Context;

        return v.Name.Lexeme switch
        {
            "parseInt" => EmitParseInt(emitter, il, ctx, call),
            "parseFloat" => EmitParseFloat(emitter, il, ctx, call),
            "isNaN" => EmitIsNaN(emitter, il, ctx, call),
            "isFinite" => EmitIsFinite(emitter, il, ctx, call),
            "String" => EmitStringConversion(emitter, il, ctx, call),
            "Number" => EmitNumberConversion(emitter, il, ctx, call),
            "Boolean" => EmitBooleanConversion(emitter, il, ctx, call),
            _ => false
        };
    }

    private static bool EmitParseInt(IEmitterContext emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        if (call.Arguments.Count > 0) { emitter.EmitExpression(call.Arguments[0]); emitter.EmitBoxIfNeeded(call.Arguments[0]); } else { il.Emit(System.Reflection.Emit.OpCodes.Ldnull); }
        if (call.Arguments.Count > 1) { emitter.EmitExpression(call.Arguments[1]); emitter.EmitBoxIfNeeded(call.Arguments[1]); } else { il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, 10); il.Emit(System.Reflection.Emit.OpCodes.Box, ctx.Types.Int32); }
        il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.NumberParseInt);
        emitter.SetStackType(StackType.Double);
        return true;
    }

    private static bool EmitParseFloat(IEmitterContext emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        if (call.Arguments.Count > 0) { emitter.EmitExpression(call.Arguments[0]); emitter.EmitBoxIfNeeded(call.Arguments[0]); } else { il.Emit(System.Reflection.Emit.OpCodes.Ldnull); }
        il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.NumberParseFloat);
        emitter.SetStackType(StackType.Double);
        return true;
    }

    private static bool EmitIsNaN(IEmitterContext emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        if (call.Arguments.Count > 0) { emitter.EmitExpression(call.Arguments[0]); emitter.EmitBoxIfNeeded(call.Arguments[0]); } else { il.Emit(System.Reflection.Emit.OpCodes.Ldnull); }
        il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.GlobalIsNaN);
        emitter.SetStackType(StackType.Boolean);
        return true;
    }

    private static bool EmitIsFinite(IEmitterContext emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        if (call.Arguments.Count > 0) { emitter.EmitExpression(call.Arguments[0]); emitter.EmitBoxIfNeeded(call.Arguments[0]); } else { il.Emit(System.Reflection.Emit.OpCodes.Ldnull); }
        il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.GlobalIsFinite);
        emitter.SetStackType(StackType.Boolean);
        return true;
    }

    private static bool EmitStringConversion(IEmitterContext emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        if (call.Arguments.Count == 0)
        {
            // String() with no args returns ""
            il.Emit(System.Reflection.Emit.OpCodes.Ldstr, "");
        }
        else
        {
            emitter.EmitExpression(call.Arguments[0]);
            emitter.EmitBoxIfNeeded(call.Arguments[0]);
            il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.Stringify);
        }
        emitter.SetStackType(StackType.String);
        return true;
    }

    private static bool EmitNumberConversion(IEmitterContext emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        if (call.Arguments.Count == 0)
        {
            // Number() with no args returns 0
            il.Emit(System.Reflection.Emit.OpCodes.Ldc_R8, 0.0);
        }
        else
        {
            emitter.EmitExpression(call.Arguments[0]);
            emitter.EmitBoxIfNeeded(call.Arguments[0]);
            il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.ConvertToNumber);
        }
        emitter.SetStackType(StackType.Double);
        return true;
    }

    private static bool EmitBooleanConversion(IEmitterContext emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        if (call.Arguments.Count == 0)
        {
            // Boolean() with no args returns false
            il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
        }
        else
        {
            emitter.EmitExpression(call.Arguments[0]);
            emitter.EmitBoxIfNeeded(call.Arguments[0]);
            il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.IsTruthy);
        }
        emitter.SetStackType(StackType.Boolean);
        return true;
    }
}
