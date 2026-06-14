using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters;

/// <summary>
/// Emitter strategy for Date method calls and property access.
/// Handles all TypeScript Date methods like getTime, getFullYear, setDate, toISOString, etc.
/// </summary>
public sealed class DateEmitter : ITypeEmitterStrategy
{
    /// <summary>
    /// Attempts to emit IL for a method call on a Date receiver.
    /// </summary>
    public bool TryEmitMethodCall(IEmitterContext emitter, Expr receiver, string methodName, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Emit the Date object
        emitter.EmitExpression(receiver);
        emitter.EmitBoxIfNeeded(receiver);

        switch (methodName)
        {
            // Getters (no arguments, return double)
            case "getTime":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetTime);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getFullYear":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetFullYear);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getMonth":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetMonth);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getDate":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetDate);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getDay":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetDay);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getHours":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetHours);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getMinutes":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetMinutes);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getSeconds":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetSeconds);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getMilliseconds":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetMilliseconds);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getTimezoneOffset":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetTimezoneOffset);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            // UTC getters + legacy getYear (no arguments, return double)
            case "getUTCFullYear":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetUTCFullYear);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getUTCMonth":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetUTCMonth);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getUTCDate":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetUTCDate);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getUTCDay":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetUTCDay);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getUTCHours":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetUTCHours);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getUTCMinutes":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetUTCMinutes);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getUTCSeconds":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetUTCSeconds);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getUTCMilliseconds":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetUTCMilliseconds);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "getYear":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateGetYear);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            // Simple setters (single argument, return double)
            case "setTime":
                EmitSingleDoubleArgOrNaN(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetTime);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "setDate":
                EmitSingleDoubleArgOrNaN(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetDate);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "setMilliseconds":
                EmitSingleDoubleArgOrNaN(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetMilliseconds);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            // UTC simple setters + legacy setYear (single argument, return double)
            case "setUTCDate":
                EmitSingleDoubleArgOrNaN(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetUTCDate);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "setUTCMilliseconds":
                EmitSingleDoubleArgOrNaN(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetUTCMilliseconds);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "setYear":
                EmitSingleDoubleArgOrNaN(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetYear);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            // Multi-argument setters (variadic, packaged as object[])
            case "setFullYear":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetFullYear);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "setMonth":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetMonth);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "setHours":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetHours);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "setMinutes":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetMinutes);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "setSeconds":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetSeconds);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            // UTC multi-argument setters (variadic, packaged as object[])
            case "setUTCFullYear":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetUTCFullYear);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "setUTCMonth":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetUTCMonth);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "setUTCHours":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetUTCHours);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "setUTCMinutes":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetUTCMinutes);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "setUTCSeconds":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.DateSetUTCSeconds);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            // Conversion methods (no arguments, return string)
            case "toISOString":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateToISOString);
                return true;

            case "toDateString":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateToDateString);
                return true;

            case "toTimeString":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateToTimeString);
                return true;

            case "toUTCString":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateToUTCString);
                return true;

            // toLocale* (locale/options args accepted by the type checker but ignored at runtime)
            case "toLocaleDateString":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateToLocaleDateString);
                return true;

            case "toLocaleTimeString":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateToLocaleTimeString);
                return true;

            case "toLocaleString":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateToLocaleString);
                return true;

            // toJSON (no arguments, returns string | null as object)
            case "toJSON":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateToJSON);
                return true;

            // valueOf (no arguments, returns double)
            case "valueOf":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateValueOf);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            // toString (no arguments, returns string)
            case "toString":
                il.Emit(OpCodes.Call, ctx.Runtime!.DateToString);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Attempts to emit IL for a property get on a Date receiver.
    /// Date objects don't have accessible properties in TypeScript.
    /// </summary>
    public bool TryEmitPropertyGet(IEmitterContext emitter, Expr receiver, string propertyName)
    {
        // Date doesn't expose properties directly - all access is via methods
        return false;
    }

    /// <summary>
    /// Attempts to emit IL for a property set on a Date receiver.
    /// Date properties are not directly settable.
    /// </summary>
    public bool TryEmitPropertySet(IEmitterContext emitter, Expr receiver, string propertyName, Expr value)
    {
        return false;
    }

    #region Helper Methods

    /// <summary>
    /// Emits a single argument as double, or NaN if no arguments.
    /// </summary>
    private static void EmitSingleDoubleArgOrNaN(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpressionAsDouble(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldc_R8, double.NaN);
        }
    }

    /// <summary>
    /// Emits all arguments as an object array.
    /// </summary>
    private static void EmitArgsArray(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

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
    }

    #endregion
}
