using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters;

/// <summary>
/// Emitter strategy for String static method calls.
/// Handles String.fromCharCode(), String.raw().
/// </summary>
public sealed class StringStaticEmitter : IStaticTypeEmitterStrategy
{
    /// <summary>
    /// Attempts to emit IL for a String static method call.
    /// </summary>
    public bool TryEmitStaticCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        switch (methodName)
        {
            case "fromCharCode":
                // Create an object[] from all arguments (char codes)
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

                il.Emit(OpCodes.Call, ctx.Runtime!.StringFromCharCode);
                return true;

            case "fromCodePoint":
                // Create an object[] from all arguments (code points)
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

                il.Emit(OpCodes.Call, ctx.Runtime!.StringFromCodePoint);
                return true;

            case "raw":
                // String.raw is handled separately via tagged template literal emission
                // For direct calls: String.raw(templateStrings, ...substitutions)
                // This is a complex case that requires the template strings array
                // For now, delegate to the runtime method
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// String has no static properties.
    /// </summary>
    public bool TryEmitStaticPropertyGet(IEmitterContext emitter, string propertyName)
    {
        return false;
    }
}
