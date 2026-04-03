using System.Reflection.Emit;
using SharpTS.Compilation.Emitters;
using SharpTS.Parsing;

namespace SharpTS.Compilation.CallHandlers;

/// <summary>
/// Handles super() and super.constructor() calls in derived class constructors.
/// Resolves the parent constructor from class declarations or class expressions.
/// </summary>
public class SuperConstructorHandler : ICallHandler
{
    public int Priority => 10; // Highest priority — must run before other handlers

    public bool TryHandle(IEmitterContext emitter, Expr.Call call)
    {
        // Must be super() or super.constructor()
        if (call.Callee is not Expr.Super superExpr)
            return false;
        if (superExpr.Method != null && superExpr.Method.Lexeme != "constructor")
            return false;

        var ctx = emitter.Context;

        // Try class declaration constructors first
        var parentCtor = ctx.CurrentSuperclassName != null
            ? ctx.ClassRegistry?.GetConstructor(ctx.CurrentSuperclassName)
            : null;
        if (parentCtor != null)
        {
            EmitSuperCtorCall(emitter, parentCtor, call.Arguments);
            return true;
        }

        // Try class expression constructors
        if (ctx.CurrentClassExpr != null &&
            ctx.ClassExprSuperclass?.TryGetValue(ctx.CurrentClassExpr, out var superclassName) == true &&
            superclassName != null)
        {
            ConstructorBuilder? parentExprCtor = null;

            // Check class expression constructors using VarToClassExpr mapping
            if (ctx.VarToClassExpr != null &&
                ctx.VarToClassExpr.TryGetValue(superclassName, out var parentClassExpr) &&
                ctx.ClassExprConstructors != null &&
                ctx.ClassExprConstructors.TryGetValue(parentClassExpr, out var exprCtor))
            {
                parentExprCtor = exprCtor;
            }

            // If not found in class expressions, try class declarations
            parentExprCtor ??= ctx.ClassRegistry?.GetConstructorByQualifiedName(superclassName);

            if (parentExprCtor != null)
            {
                EmitSuperCtorCall(emitter, parentExprCtor, call.Arguments);
                return true;
            }
        }

        return false;
    }

    private static void EmitSuperCtorCall(IEmitterContext emitter, ConstructorBuilder parentCtor, List<Expr> arguments)
    {
        var il = emitter.IL;
        var ctx = emitter.Context;

        il.Emit(OpCodes.Ldarg_0);

        var ctorParams = parentCtor.GetParameters();
        for (int i = 0; i < arguments.Count; i++)
        {
            emitter.EmitExpression(arguments[i]);
            if (i < ctorParams.Length)
                emitter.EmitConversionForParameter(arguments[i], ctorParams[i].ParameterType);
            else
                emitter.EmitBoxIfNeeded(arguments[i]);
        }

        for (int i = arguments.Count; i < ctorParams.Length; i++)
            emitter.EmitDefaultForType(ctorParams[i].ParameterType);

        System.Reflection.ConstructorInfo ctorToCall = parentCtor;
        Type? baseType = ctx.CurrentClassBuilder?.BaseType;
        if (baseType != null && baseType.IsGenericType && baseType.IsConstructedGenericType)
            ctorToCall = TypeBuilder.GetConstructor(baseType, parentCtor);

        il.Emit(OpCodes.Call, ctorToCall);
        il.Emit(OpCodes.Ldnull);
        emitter.SetStackUnknown();
    }
}
