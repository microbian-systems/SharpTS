using SharpTS.Compilation.Emitters;
using SharpTS.Parsing;

namespace SharpTS.Compilation.CallHandlers;

/// <summary>
/// Handles global built-in functions: parseInt, parseFloat, isNaN, isFinite, String, Number, Boolean,
/// encodeURIComponent, decodeURIComponent.
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
            "eval" => EmitEval(emitter, il, ctx, call),
            "parseInt" => EmitParseInt(emitter, il, ctx, call),
            "parseFloat" => EmitParseFloat(emitter, il, ctx, call),
            "isNaN" => EmitIsNaN(emitter, il, ctx, call),
            "isFinite" => EmitIsFinite(emitter, il, ctx, call),
            "String" => EmitStringConversion(emitter, il, ctx, call),
            "Number" => EmitNumberConversion(emitter, il, ctx, call),
            "Boolean" => EmitBooleanConversion(emitter, il, ctx, call),
            "encodeURIComponent" => EmitEncodeURIComponent(emitter, il, ctx, call),
            "decodeURIComponent" => EmitDecodeURIComponent(emitter, il, ctx, call),
            _ => false
        };
    }

    /// <summary>
    /// Emits a compiled <c>eval(arg)</c>. Compiled output has no live interpreter/scope, so this
    /// reflectively invokes <c>SharpTS.Execution.EvalBridge.Eval(object)</c> (indirect, global-scope
    /// eval) only when the SharpTS runtime is present, degrading to a deterministic throw otherwise.
    /// The reflection pattern keeps the output DLL free of a hard SharpTS.dll reference.
    /// </summary>
    private static bool EmitEval(IEmitterContext emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        // eval always routes through EvalBridge in the SharpTS runtime — record the soft
        // dependency so the build co-locates SharpTS.dll with the output.
        ctx.Runtime?.RequireSharpTSRuntime("eval()");

        // object arg = <arg0 boxed> (or null when called with no arguments)
        var argLocal = il.DeclareLocal(ctx.Types.Object);
        if (call.Arguments.Count > 0) { emitter.EmitExpression(call.Arguments[0]); emitter.EmitBoxIfNeeded(call.Arguments[0]); }
        else { il.Emit(System.Reflection.Emit.OpCodes.Ldnull); }
        il.Emit(System.Reflection.Emit.OpCodes.Stloc, argLocal);

        // Type t = Type.GetType("SharpTS.Execution.EvalBridge, SharpTS");
        il.Emit(System.Reflection.Emit.OpCodes.Ldstr, "SharpTS.Execution.EvalBridge, SharpTS");
        il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Types.GetMethod(ctx.Types.Type, "GetType", ctx.Types.String));

        // Graceful degradation: if the SharpTS runtime isn't present, t is null — throw a clear error
        // instead of letting the subsequent virtual calls NRE.
        var present = il.DefineLabel();
        il.Emit(System.Reflection.Emit.OpCodes.Dup);
        il.Emit(System.Reflection.Emit.OpCodes.Brtrue, present);
        il.Emit(System.Reflection.Emit.OpCodes.Pop);
        il.Emit(System.Reflection.Emit.OpCodes.Ldstr, "eval is not supported in standalone compiled output (SharpTS runtime not present).");
        il.Emit(System.Reflection.Emit.OpCodes.Newobj, ctx.Types.ExceptionCtorString);
        il.Emit(System.Reflection.Emit.OpCodes.Throw);
        il.MarkLabel(present);

        // MethodInfo m = t.GetMethod("Eval");
        il.Emit(System.Reflection.Emit.OpCodes.Ldstr, "Eval");
        il.Emit(System.Reflection.Emit.OpCodes.Callvirt, ctx.Types.GetMethod(ctx.Types.Type, "GetMethod", ctx.Types.String));

        // return (object) m.Invoke(null, new object[] { arg });
        il.Emit(System.Reflection.Emit.OpCodes.Ldnull);
        il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
        il.Emit(System.Reflection.Emit.OpCodes.Newarr, ctx.Types.Object);
        il.Emit(System.Reflection.Emit.OpCodes.Dup);
        il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
        il.Emit(System.Reflection.Emit.OpCodes.Ldloc, argLocal);
        il.Emit(System.Reflection.Emit.OpCodes.Stelem_Ref);
        il.Emit(System.Reflection.Emit.OpCodes.Callvirt, ctx.Types.GetMethod(
            ctx.Types.MethodInfo, "Invoke", ctx.Types.Object, ctx.Types.ObjectArray));

        // Result is an arbitrary JS value (boxed object) of statically unknown type.
        emitter.SetStackUnknown();
        return true;
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
            // StringFromValue (not Stringify): runs the spec ToString chain —
            // user toString/@@toPrimitive on objects — with the §22.1.1.1
            // Symbol exemption. Keeps this path consistent with the sync
            // emitter's String(x) handling in ILEmitter.Calls.cs.
            emitter.EmitExpression(call.Arguments[0]);
            emitter.EmitBoxIfNeeded(call.Arguments[0]);
            il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.StringFromValueMethod);
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

    private static bool EmitEncodeURIComponent(IEmitterContext emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        // JS: encodeURIComponent() throws; encodeURIComponent(undefined) returns "undefined".
        // We match the "undefined" coercion and let the runtime throw if truly missing.
        if (call.Arguments.Count == 0)
        {
            il.Emit(System.Reflection.Emit.OpCodes.Ldstr, "undefined");
        }
        else
        {
            emitter.EmitExpression(call.Arguments[0]);
            emitter.EmitBoxIfNeeded(call.Arguments[0]);
            il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.Stringify);
        }
        il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Types.UriEscapeDataString);
        emitter.SetStackType(StackType.String);
        return true;
    }

    private static bool EmitDecodeURIComponent(IEmitterContext emitter, System.Reflection.Emit.ILGenerator il, CompilationContext ctx, Expr.Call call)
    {
        if (call.Arguments.Count == 0)
        {
            il.Emit(System.Reflection.Emit.OpCodes.Ldstr, "undefined");
        }
        else
        {
            emitter.EmitExpression(call.Arguments[0]);
            emitter.EmitBoxIfNeeded(call.Arguments[0]);
            il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.Stringify);
        }
        il.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Types.UriUnescapeDataString);
        emitter.SetStackType(StackType.String);
        return true;
    }
}
