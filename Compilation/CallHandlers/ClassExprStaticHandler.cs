using System.Reflection.Emit;
using SharpTS.Compilation.Emitters;
using SharpTS.Parsing;

namespace SharpTS.Compilation.CallHandlers;

/// <summary>
/// Handles static method calls on class expressions (const Factory = class { static create() {} }; Factory.create()).
/// </summary>
public class ClassExprStaticHandler : ICallHandler
{
    public int Priority => 74;

    public bool TryHandle(IEmitterContext emitter, Expr.Call call)
    {
        if (call.Callee is not Expr.Get classExprGet ||
            classExprGet.Object is not Expr.Variable classExprVar)
            return false;

        var ctx = emitter.Context;
        if (ctx.VarToClassExpr == null ||
            !ctx.VarToClassExpr.TryGetValue(classExprVar.Name.Lexeme, out var classExpr) ||
            ctx.ClassExprStaticMethods == null ||
            !ctx.ClassExprStaticMethods.TryGetValue(classExpr, out var exprStaticMethods) ||
            !exprStaticMethods.TryGetValue(classExprGet.Name.Lexeme, out var exprStaticMethod))
            return false;

        var il = emitter.IL;
        var methodParams = exprStaticMethod.GetParameters();
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
            emitter.EmitDefaultForType(methodParams[i].ParameterType);

        il.Emit(OpCodes.Call, exprStaticMethod);
        emitter.SetStackUnknown();
        return true;
    }
}
