using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters;

/// <summary>
/// Emitter strategy for Promise static method calls.
/// Handles Promise.resolve(), Promise.reject(), Promise.all(), Promise.race(), Promise.allSettled(), Promise.any().
/// </summary>
public sealed class PromiseStaticEmitter : IStaticTypeEmitterStrategy
{
    /// <summary>
    /// Attempts to emit IL for a Promise static method call.
    /// Returns Task&lt;object?&gt; on the stack - does NOT synchronously await.
    /// </summary>
    public bool TryEmitStaticCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        switch (methodName)
        {
            case "resolve":
                // Promise.resolve(value?) - returns Task<object?> directly
                if (arguments.Count > 0)
                {
                    emitter.EmitExpression(arguments[0]);
                    emitter.EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Call, ctx.Runtime!.PromiseResolve);
                return true;

            case "reject":
                // Promise.reject(reason) - returns Task<object?> directly
                if (arguments.Count > 0)
                {
                    emitter.EmitExpression(arguments[0]);
                    emitter.EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Call, ctx.Runtime!.PromiseReject);
                return true;

            case "all":
                // Promise.all(iterable) - returns Task<object?> directly
                if (arguments.Count > 0)
                {
                    emitter.EmitExpression(arguments[0]);
                    emitter.EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Call, ctx.Runtime!.PromiseAll);
                return true;

            case "race":
                // Promise.race(iterable) - returns Task<object?> directly
                if (arguments.Count > 0)
                {
                    emitter.EmitExpression(arguments[0]);
                    emitter.EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Call, ctx.Runtime!.PromiseRace);
                return true;

            case "allSettled":
                // Promise.allSettled(iterable) - returns Task<object?> directly
                if (arguments.Count > 0)
                {
                    emitter.EmitExpression(arguments[0]);
                    emitter.EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Call, ctx.Runtime!.PromiseAllSettled);
                return true;

            case "any":
                // Promise.any(iterable) - returns Task<object?> directly
                if (arguments.Count > 0)
                {
                    emitter.EmitExpression(arguments[0]);
                    emitter.EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Call, ctx.Runtime!.PromiseAny);
                return true;

            case "withResolvers":
                // Promise.withResolvers() - returns Task<object?> wrapping {promise, resolve, reject}
                il.Emit(OpCodes.Call, ctx.Runtime!.PromiseWithResolvers);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Promise has no static properties.
    /// </summary>
    public bool TryEmitStaticPropertyGet(IEmitterContext emitter, string propertyName)
    {
        var ctx = emitter.Context;
        var runtime = ctx.Runtime!;
        // Stage 4y: Promise.* statics as values for `let r = Promise.resolve;
        // r(42).then(...)` patterns + test262 isConstructor harness.
        MethodInfo? method = propertyName switch
        {
            "resolve"        => runtime.PromiseResolve,
            "reject"         => runtime.PromiseReject,
            "all"            => runtime.PromiseAll,
            "race"           => runtime.PromiseRace,
            "allSettled"     => runtime.PromiseAllSettled,
            "any"            => runtime.PromiseAny,
            "withResolvers"  => runtime.PromiseWithResolvers,
            _ => null
        };
        if (method == null) return false;

        // ECMA-262 §17 built-in `name` + spec `length`. resolve/reject/all/race/
        // allSettled/any take one arg; withResolvers takes none.
        int specLength = propertyName == "withResolvers" ? 0 : 1;
        var il = ctx.IL;
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldtoken, method);
        il.Emit(OpCodes.Ldtoken, method.DeclaringType!);
        il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.MethodBase, "GetMethodFromHandle",
            ctx.Types.RuntimeMethodHandle, ctx.Types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, ctx.Types.MethodInfo);
        il.Emit(OpCodes.Ldstr, propertyName);
        il.Emit(OpCodes.Ldc_I4, specLength);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtorWithCache);
        return true;
    }

    public bool HasStaticProperty(string memberName) =>
        memberName is "resolve" or "reject" or "all" or "race"
            or "allSettled" or "any" or "withResolvers";
}
