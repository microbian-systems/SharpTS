using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;
using SharpTS.TypeSystem;

namespace SharpTS.Compilation;

/// <summary>
/// Main call dispatch and function call emission methods for the IL emitter.
/// </summary>
public partial class ILEmitter
{
    protected override void EmitCall(Expr.Call c)
    {
        // External .NET type static methods (e.g., Console.WriteLine() via @DotNetType)
        // This is ILEmitter-only — requires TypeMapper.ExternalTypes + complex type conversion helpers
        if (c.Callee is Expr.Get externalStaticGet &&
            externalStaticGet.Object is Expr.Variable externalClassVar &&
            _ctx.TypeMapper?.ExternalTypes.TryGetValue(externalClassVar.Name.Lexeme, out var externalType) == true)
        {
            EmitExternalStaticMethodCall(externalType, externalStaticGet.Name.Lexeme, c.Arguments);
            return;
        }

        // For Expr.Get callees: let handler chain + base class handle known patterns,
        // then fall through to ILEmitter's EmitMethodCall for instance method dispatch.
        // EmitMethodCall has optimized dispatch with TypeMap that the base class lacks.
        if (c.Callee is Expr.Get methodGet)
        {
            // Handler chain handles: static types, Date.now, built-in modules, process streams,
            // globalThis chaining, imported/class-expr/this statics
            if (_callHandlers.TryHandle(this, c))
                return;

            // module.promises.methodName() (fs.promises, dns.promises, stream.promises)
            if (methodGet.Object is Expr.Get promisesGet &&
                promisesGet.Name.Lexeme == "promises" &&
                promisesGet.Object is Expr.Variable promisesModuleVar &&
                _ctx.BuiltInModuleNamespaces != null &&
                _ctx.BuiltInModuleNamespaces.TryGetValue(promisesModuleVar.Name.Lexeme, out var promisesModuleName) &&
                _ctx.BuiltInModuleEmitterRegistry?.GetEmitter(promisesModuleName + "/promises") is { } promisesEmitter)
            {
                if (promisesEmitter.TryEmitMethodCall(this, methodGet.Name.Lexeme, c.Arguments))
                {
                    SetStackUnknown();
                    return;
                }
            }

            // Class.staticMethod() with generic class support
            if (methodGet.Object is Expr.Variable classVar &&
                _ctx.Classes.TryGetValue(_ctx.ResolveClassName(classVar.Name.Lexeme), out var classBuilder))
            {
                string resolvedClassName = _ctx.ResolveClassName(classVar.Name.Lexeme);
                if (_ctx.ClassRegistry!.TryGetCallableStaticMethod(resolvedClassName, methodGet.Name.Lexeme, classBuilder, out var callableMethod))
                {
                    var staticMethodParams = callableMethod!.GetParameters();
                    for (int i = 0; i < c.Arguments.Count; i++)
                    {
                        EmitExpression(c.Arguments[i]);
                        if (i < staticMethodParams.Length)
                            EmitConversionForParameter(c.Arguments[i], staticMethodParams[i].ParameterType);
                        else
                            EmitBoxIfNeeded(c.Arguments[i]);
                    }
                    for (int i = c.Arguments.Count; i < staticMethodParams.Length; i++)
                        EmitDefaultForType(staticMethodParams[i].ParameterType);
                    IL.Emit(OpCodes.Call, callableMethod);
                    SetStackUnknown();
                    return;
                }
            }

            // Instance method dispatch (Array/String/Map/Promise/etc.)
            EmitMethodCall(methodGet, c.Arguments);
            return;
        }

        // All non-Get call patterns — delegate to base class
        base.EmitCall(c);
    }

    /// <summary>
    /// Resolves a type argument string to a .NET Type for generic instantiation.
    /// </summary>
    protected override Type ResolveTypeArg(string typeArg)
    {
        return typeArg switch
        {
            "number" => _ctx.Types.Double,
            "string" => _ctx.Types.String,
            "boolean" => _ctx.Types.Boolean,
            _ when _ctx.GenericTypeParameters.TryGetValue(typeArg, out var gp) => gp,
            _ when _ctx.Classes.TryGetValue(_ctx.ResolveClassName(typeArg), out var tb) => tb,
            _ => _ctx.Types.Object
        };
    }
}
