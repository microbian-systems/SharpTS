using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters;

/// <summary>
/// Emitter strategy for Array static method calls.
/// Handles Array.isArray().
/// </summary>
public sealed class ArrayStaticEmitter : IStaticTypeEmitterStrategy
{
    /// <summary>
    /// Attempts to emit IL for an Array static method call.
    /// </summary>
    public bool TryEmitStaticCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        switch (methodName)
        {
            case "isArray":
                if (arguments.Count > 0)
                {
                    emitter.EmitExpression(arguments[0]);
                    emitter.EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Call, ctx.Runtime!.IsArray);
                il.Emit(OpCodes.Box, ctx.Types.Boolean);
                return true;
            case "from":
                // Emit iterable argument
                if (arguments.Count > 0)
                {
                    emitter.EmitExpression(arguments[0]);
                    emitter.EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                }

                // Emit mapFn (or null)
                if (arguments.Count > 1)
                {
                    emitter.EmitExpression(arguments[1]);
                    emitter.EmitBoxIfNeeded(arguments[1]);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                }

                // Load Symbol.iterator and runtime type for IterateToList
                il.Emit(OpCodes.Ldsfld, ctx.Runtime!.SymbolIterator);
                il.Emit(OpCodes.Ldtoken, ctx.Runtime!.RuntimeType);
                il.Emit(OpCodes.Call, ctx.Types.TypeGetTypeFromHandle);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFrom);
                return true;
            case "of":
                // Create an object[] from all arguments
                il.Emit(OpCodes.Ldc_I4, arguments.Count);
                il.Emit(OpCodes.Newarr, ctx.Types.Object);

                for (int i = 0; i < arguments.Count; i++)
                {
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, i);
                    emitter.EmitExpression(arguments[i]);
                    emitter.EmitBoxIfNeeded(arguments[i]);
                    il.Emit(OpCodes.Stelem_Ref);
                }

                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayOf);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Emits IL for bare property access on `Array`, without a call — e.g.
    /// `var f = Array.isArray`. Returns a <c>$TSFunction</c> that wraps the
    /// corresponding runtime helper so downstream <c>f(x)</c> invocations
    /// route to the right builtin.
    /// </summary>
    /// <remarks>
    /// Without this, bare property access fell through to the generic
    /// <c>$Runtime.GetProperty</c> path, which does
    /// <c>P_0.GetType().GetProperty(name, IgnoreCase|Instance|Public)</c>
    /// on the emitted <c>System.Type</c> token for <c>Array</c>. That
    /// leaked <c>System.Type.IsArray</c> (a .NET reflection property) as
    /// the JS value of <c>Array.isArray</c> — returning the boolean
    /// <c>false</c> instead of a callable. lodash caches
    /// <c>var isArray = Array.isArray;</c> at IIFE init, so every
    /// subsequent <c>isArray(x)</c> invoked <c>false(x)</c> and silently
    /// yielded <c>null</c>, which rippled through to <c>_.chunk</c>
    /// returning <c>[]</c> and <c>_.flatten</c> returning its input.
    /// </remarks>
    public bool TryEmitStaticPropertyGet(IEmitterContext emitter, string propertyName)
    {
        var ctx = emitter.Context;
        var runtime = ctx.Runtime!;

        // Only isArray wraps cleanly today — its signature `bool IsArray(object)`
        // matches what TSFunction.Invoke can dispatch to via MethodInfo.Invoke
        // (AdjustArgs pads/trims to the method's parameter count, and bool
        // return values auto-box). Array.from / Array.of take variadic or
        // runtime-specific trailing args and need adapter methods before they
        // can be exposed as values the same way.
        var method = propertyName switch
        {
            "isArray" => runtime.IsArray,
            // Stage 4y: Array.from / Array.of as values. Array.from goes
            // through an adapter (ArrayFrom has a 4-arg internal signature
            // that includes spec-fixed Symbol.iterator + runtime type;
            // ArrayFromAdapter wraps with a 2-arg public surface). Array.of
            // is already (object[])->$Array which TSFunction can dispatch.
            "from"    => runtime.ArrayFromAdapter,
            "of"      => runtime.ArrayOf,
            _ => null
        };
        if (method == null) return false;

        var il = ctx.IL;

        // new $TSFunction(target: null, method: MethodInfo_of(method)).
        // Materialize the MethodInfo via GetMethodFromHandle(methodHandle, declaringTypeHandle)
        // — the two-arg overload is needed because the runtime method lives on
        // the emitted $Runtime TypeBuilder and token resolution in persisted
        // assemblies requires the declaring type.
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldtoken, method);
        il.Emit(OpCodes.Ldtoken, method.DeclaringType!);
        il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.MethodBase, "GetMethodFromHandle",
            ctx.Types.RuntimeMethodHandle, ctx.Types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, ctx.Types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        return true;
    }
}
