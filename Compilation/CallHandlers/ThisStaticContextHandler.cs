using System.Reflection.Emit;
using SharpTS.Compilation.Emitters;
using SharpTS.Parsing;

namespace SharpTS.Compilation.CallHandlers;

/// <summary>
/// Handles this.method() in static context (static blocks, static methods).
/// In static context, 'this' refers to the class constructor, so this.method() calls static methods.
/// </summary>
public class ThisStaticContextHandler : ICallHandler
{
    public int Priority => 76;

    public bool TryHandle(IEmitterContext emitter, Expr.Call call)
    {
        if (call.Callee is not Expr.Get thisGet ||
            thisGet.Object is not Expr.This)
            return false;

        var ctx = emitter.Context;

        // Only applies in static context
        if (ctx.IsInstanceMethod || ctx.CurrentClassBuilder == null)
            return false;

        string? currentClassName = ctx.CurrentClassName;
        if (currentClassName == null)
            return false;

        if (!ctx.ClassRegistry!.TryGetCallableStaticMethod(currentClassName, thisGet.Name.Lexeme, ctx.CurrentClassBuilder, out var thisStaticMethod))
            return false;

        var il = emitter.IL;
        var methodParams = thisStaticMethod!.GetParameters();
        var paramCount = methodParams.Length;

        for (int i = 0; i < call.Arguments.Count; i++)
        {
            emitter.EmitExpression(call.Arguments[i]);
            if (i < methodParams.Length)
                emitter.EmitConversionForParameter(call.Arguments[i], methodParams[i].ParameterType);
            else
                emitter.EmitBoxIfNeeded(call.Arguments[i]);
        }

        for (int i = call.Arguments.Count; i < paramCount; i++)
            emitter.EmitOmittedArgument(methodParams[i].ParameterType);

        il.Emit(OpCodes.Call, thisStaticMethod);
        emitter.SetStackUnknown();
        return true;
    }
}
