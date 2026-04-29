using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters;

/// <summary>
/// Emitter strategy for string method calls and property access.
/// Handles all TypeScript string methods like charAt, substring, toUpperCase, etc.
/// </summary>
public sealed class StringEmitter : ITypeEmitterStrategy
{
    /// <summary>
    /// Attempts to emit IL for a method call on a string receiver.
    /// </summary>
    public bool TryEmitMethodCall(IEmitterContext emitter, Expr receiver, string methodName, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Emit the string object
        emitter.EmitExpression(receiver);
        emitter.EmitBoxIfNeeded(receiver);
        il.Emit(OpCodes.Castclass, ctx.Types.String);

        switch (methodName)
        {
            case "charAt":
                EmitCharAt(emitter, arguments);
                return true;

            case "substring":
                EmitSubstring(emitter, arguments);
                return true;

            case "substr":
                EmitSubstr(emitter, arguments);
                return true;

            case "indexOf":
                EmitIndexOf(emitter, arguments);
                return true;

            case "toUpperCase":
                il.Emit(OpCodes.Callvirt, ctx.Types.GetMethodNoParams(ctx.Types.String, "ToUpper"));
                return true;

            case "toLowerCase":
                il.Emit(OpCodes.Callvirt, ctx.Types.GetMethodNoParams(ctx.Types.String, "ToLower"));
                return true;

            case "trim":
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Call, ctx.Runtime!.JsTrimInline);
                return true;

            case "replace":
                EmitReplace(emitter, arguments);
                return true;

            case "split":
                EmitSplit(emitter, arguments);
                return true;

            case "match":
                EmitMatch(emitter, arguments);
                return true;

            case "matchAll":
                EmitMatchAll(emitter, arguments);
                return true;

            case "search":
                EmitSearch(emitter, arguments);
                return true;

            case "includes":
                EmitIncludes(emitter, arguments);
                return true;

            case "startsWith":
                EmitStartsWith(emitter, arguments);
                return true;

            case "endsWith":
                EmitEndsWith(emitter, arguments);
                return true;

            case "slice":
                EmitSlice(emitter, arguments);
                return true;

            case "repeat":
                EmitRepeat(emitter, arguments);
                return true;

            case "padStart":
                EmitPadStart(emitter, arguments);
                return true;

            case "padEnd":
                EmitPadEnd(emitter, arguments);
                return true;

            case "charCodeAt":
                EmitCharCodeAt(emitter, arguments);
                return true;

            case "codePointAt":
                EmitCodePointAt(emitter, arguments);
                return true;

            case "concat":
                EmitConcat(emitter, arguments);
                return true;

            case "lastIndexOf":
                EmitLastIndexOf(emitter, arguments);
                return true;

            case "trimStart":
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Call, ctx.Runtime!.JsTrimInline);
                return true;

            case "trimEnd":
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Call, ctx.Runtime!.JsTrimInline);
                return true;

            case "replaceAll":
                EmitReplaceAll(emitter, arguments);
                return true;

            case "at":
                EmitAt(emitter, arguments);
                return true;

            case "normalize":
                EmitNormalize(emitter, arguments);
                return true;

            case "localeCompare":
                EmitLocaleCompare(emitter, arguments);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Attempts to emit IL for a property get on a string receiver.
    /// </summary>
    public bool TryEmitPropertyGet(IEmitterContext emitter, Expr receiver, string propertyName)
    {
        if (propertyName != "length")
            return false;

        var ctx = emitter.Context;
        var il = ctx.IL;

        // Emit the string object
        emitter.EmitExpression(receiver);
        emitter.EmitBoxIfNeeded(receiver);
        il.Emit(OpCodes.Castclass, ctx.Types.String);

        // Get length and convert to double (TypeScript number)
        il.Emit(OpCodes.Call, ctx.Types.GetProperty(ctx.Types.String, "Length").GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, ctx.Types.Double);

        return true;
    }

    /// <summary>
    /// Attempts to emit IL for a property set on a string receiver.
    /// Strings are immutable in TypeScript/JavaScript.
    /// </summary>
    public bool TryEmitPropertySet(IEmitterContext emitter, Expr receiver, string propertyName, Expr value)
    {
        return false;
    }

    #region String Method Implementations

    private static void EmitCharAt(IEmitterContext emitter, List<Expr> arguments)
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
        il.Emit(OpCodes.Call, ctx.Runtime!.StringCharAt);
    }

    private static void EmitSubstring(IEmitterContext emitter, List<Expr> arguments)
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
        il.Emit(OpCodes.Call, ctx.Runtime!.StringSubstring);
    }

    private static void EmitSubstr(IEmitterContext emitter, List<Expr> arguments)
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
        il.Emit(OpCodes.Call, ctx.Runtime!.StringSubstr);
    }

    private static void EmitIndexOf(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
            // ECMA-262: argument coerced via ToString protocol — handles objects
            // with custom toString/valueOf and avoids InvalidCastException for
            // non-string args (e.g. `"abc".indexOf({toString: () => "b"})`).
            il.Emit(OpCodes.Call, ctx.Runtime!.ToJsString);
        }
        else
        {
            il.Emit(OpCodes.Ldstr, "");
        }

        // JS: str.indexOf(search, fromIndex). With fromIndex, use the from-variant.
        if (arguments.Count >= 2)
        {
            emitter.EmitExpression(arguments[1]);
            emitter.EmitBoxIfNeeded(arguments[1]);
            // ECMA-262: fromIndex coerced via ToInteger which routes ToNumber
            // first — invokes valueOf/toString on object args. Going through
            // Convert.ToDouble here threw InvalidCastException for Dictionary
            // and skipped the toString throw the spec requires. ToNumber
            // returns an unboxed `double`, so no Unbox_Any.
            il.Emit(OpCodes.Call, ctx.Runtime!.ToNumber);
            il.Emit(OpCodes.Call, ctx.Runtime!.StringIndexOfFrom);
        }
        else
        {
            il.Emit(OpCodes.Call, ctx.Runtime!.StringIndexOf);
        }
        il.Emit(OpCodes.Box, ctx.Types.Double);
    }

    private static void EmitReplace(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count >= 2)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
            emitter.EmitExpression(arguments[1]);
            emitter.EmitBoxIfNeeded(arguments[1]);
            // ECMA-262 ToString protocol — handles objects with custom toString
            // (a throwing toString on the replacement now propagates instead of
            // being silently debug-dumped by the legacy `Stringify` path).
            //
            // Functional replacement (callable replaceValue per ECMA-262
            // 22.1.3.18 step 3) is NOT yet wired through — partial impl
            // attempted broke real-world callers (debug npm formatter relies
            // on capture-group args). Skip until proper capture-group
            // forwarding lands.
            il.Emit(OpCodes.Call, ctx.Runtime!.ToJsString);
        }
        else
        {
            il.Emit(OpCodes.Ldstr, "");
            il.Emit(OpCodes.Ldstr, "");
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringReplaceRegExp);
    }

    private static void EmitSplit(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
            // Don't cast - let runtime handle string or RegExp
        }
        else
        {
            il.Emit(OpCodes.Ldstr, "");
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringSplitRegExp);

        // ECMA-262 22.1.3.21 step 6: optional `limit` argument truncates the
        // result list. Post-process: if list.Count > limit, GetRange(0, limit).
        // Pre-fix the limit was silently ignored.
        if (arguments.Count >= 2)
        {
            var listLocal = il.DeclareLocal(ctx.Types.ListOfObject);
            il.Emit(OpCodes.Stloc, listLocal);

            // limitInt = (int)$Runtime.ToNumber(limitArg)
            var limitLocal = il.DeclareLocal(ctx.Types.Int32);
            emitter.EmitExpression(arguments[1]);
            emitter.EmitBoxIfNeeded(arguments[1]);
            il.Emit(OpCodes.Call, ctx.Runtime!.ToNumber);
            // Clamp NaN → 0, Infinity → MaxValue. Conv_I4 of NaN is undefined
            // behavior, so clamp via IsFinite + sign check. For typical
            // 0 ≤ limit ≤ str.Length cases this just truncates a finite int.
            var limitDouble = il.DeclareLocal(ctx.Types.Double);
            il.Emit(OpCodes.Stloc, limitDouble);
            // if (!IsFinite(limit)) → use MaxValue (effectively no clamp)
            var clampDoneLabel = il.DefineLabel();
            var notInfLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, limitDouble);
            il.Emit(OpCodes.Call, ctx.Types.Double.GetMethod("IsFinite", [ctx.Types.Double])!);
            il.Emit(OpCodes.Brtrue, notInfLabel);
            il.Emit(OpCodes.Ldc_I4, int.MaxValue);
            il.Emit(OpCodes.Stloc, limitLocal);
            il.Emit(OpCodes.Br, clampDoneLabel);
            il.MarkLabel(notInfLabel);
            il.Emit(OpCodes.Ldloc, limitDouble);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Stloc, limitLocal);
            il.MarkLabel(clampDoneLabel);

            // if (limit < list.Count) result = list.GetRange(0, limit); else result = list;
            var skipTrimLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, limitLocal);
            il.Emit(OpCodes.Ldloc, listLocal);
            il.Emit(OpCodes.Callvirt, ctx.Types.GetProperty(ctx.Types.ListOfObject, "Count").GetGetMethod()!);
            il.Emit(OpCodes.Bge, skipTrimLabel);
            // limit < count → trim
            il.Emit(OpCodes.Ldloc, listLocal);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldloc, limitLocal);
            il.Emit(OpCodes.Callvirt, ctx.Types.ListOfObject.GetMethod("GetRange", [ctx.Types.Int32, ctx.Types.Int32])!);
            il.Emit(OpCodes.Stloc, listLocal);
            il.MarkLabel(skipTrimLabel);
            il.Emit(OpCodes.Ldloc, listLocal);
        }
    }

    private static void EmitMatch(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldstr, "");
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringMatchRegExp);
    }

    private static void EmitMatchAll(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldstr, "");
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringMatchAllRegExp);
    }

    private static void EmitSearch(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldstr, "");
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringSearchRegExp);
        il.Emit(OpCodes.Box, ctx.Types.Double);
    }

    private static void EmitIncludes(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
            // ECMA-262: argument coerced via ToString protocol — handles objects
            // with custom toString/valueOf and avoids InvalidCastException for
            // non-string args (e.g. `"abc".indexOf({toString: () => "b"})`).
            il.Emit(OpCodes.Call, ctx.Runtime!.ToJsString);
        }
        else
        {
            il.Emit(OpCodes.Ldstr, "");
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringIncludes);
        il.Emit(OpCodes.Box, ctx.Types.Boolean);
    }

    private static void EmitStartsWith(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
            // ECMA-262: argument coerced via ToString protocol — handles objects
            // with custom toString/valueOf and avoids InvalidCastException for
            // non-string args (e.g. `"abc".indexOf({toString: () => "b"})`).
            il.Emit(OpCodes.Call, ctx.Runtime!.ToJsString);
        }
        else
        {
            il.Emit(OpCodes.Ldstr, "");
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringStartsWith);
        il.Emit(OpCodes.Box, ctx.Types.Boolean);
    }

    private static void EmitEndsWith(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
            // ECMA-262: argument coerced via ToString protocol — handles objects
            // with custom toString/valueOf and avoids InvalidCastException for
            // non-string args (e.g. `"abc".indexOf({toString: () => "b"})`).
            il.Emit(OpCodes.Call, ctx.Runtime!.ToJsString);
        }
        else
        {
            il.Emit(OpCodes.Ldstr, "");
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringEndsWith);
        il.Emit(OpCodes.Box, ctx.Types.Boolean);
    }

    private static void EmitSlice(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // StringSlice now takes (string, object[]) — argCount derived from
        // args.Length internally so the helper is borrowable via \$TSFunction.
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
        il.Emit(OpCodes.Call, ctx.Runtime!.StringSlice);
    }

    private static void EmitRepeat(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
            il.Emit(OpCodes.Unbox_Any, ctx.Types.Double);
        }
        else
        {
            il.Emit(OpCodes.Ldc_R8, 0.0);
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringRepeat);
    }

    private static void EmitPadStart(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // StringPadStart now takes (string, object[]) — argCount derived from args.Length.
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
        il.Emit(OpCodes.Call, ctx.Runtime!.StringPadStart);
    }

    private static void EmitPadEnd(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // StringPadEnd now takes (string, object[]) — argCount derived from args.Length.
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
        il.Emit(OpCodes.Call, ctx.Runtime!.StringPadEnd);
    }

    private static void EmitCharCodeAt(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
            // Use ToNumber instead of raw Unbox_Any Double — non-double args
            // (bool, string, object) per ECMA-262 ToInteger.
            il.Emit(OpCodes.Call, ctx.Runtime!.ToNumber);
        }
        else
        {
            il.Emit(OpCodes.Ldc_R8, 0.0);
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringCharCodeAt);
        il.Emit(OpCodes.Box, ctx.Types.Double);
    }

    private static void EmitCodePointAt(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
            il.Emit(OpCodes.Unbox_Any, ctx.Types.Double);
        }
        else
        {
            il.Emit(OpCodes.Ldc_R8, 0.0);
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringCodePointAt);
        // Result is already boxed (returns object: double or null)
    }

    private static void EmitConcat(IEmitterContext emitter, List<Expr> arguments)
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
        il.Emit(OpCodes.Call, ctx.Runtime!.StringConcat);
    }

    private static void EmitLastIndexOf(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
            // ECMA-262: argument coerced via ToString protocol — handles objects
            // with custom toString/valueOf and avoids InvalidCastException for
            // non-string args (e.g. `"abc".indexOf({toString: () => "b"})`).
            il.Emit(OpCodes.Call, ctx.Runtime!.ToJsString);
        }
        else
        {
            il.Emit(OpCodes.Ldstr, "");
        }

        // ECMA-262 22.1.3.10 step 5: ToIntegerOrInfinity(position) is performed
        // before the actual lastIndexOf scan, so a throwing toString/valueOf
        // on the position argument must propagate. Evaluate via $Runtime.ToNumber
        // (handles object/Symbol/etc.) and discard — current StringLastIndexOf
        // helper doesn't yet support an explicit fromIndex.
        if (arguments.Count >= 2)
        {
            emitter.EmitExpression(arguments[1]);
            emitter.EmitBoxIfNeeded(arguments[1]);
            il.Emit(OpCodes.Call, ctx.Runtime!.ToNumber);
            il.Emit(OpCodes.Pop);
        }

        il.Emit(OpCodes.Call, ctx.Runtime!.StringLastIndexOf);
        il.Emit(OpCodes.Box, ctx.Types.Double);
    }

    private static void EmitReplaceAll(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count >= 2)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
            // Don't cast — the regex-aware helper accepts object and branches.
            emitter.EmitExpression(arguments[1]);
            emitter.EmitBoxIfNeeded(arguments[1]);
            il.Emit(OpCodes.Call, ctx.Runtime!.Stringify); // replacement is always string
        }
        else
        {
            il.Emit(OpCodes.Ldstr, "");
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldstr, "");
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringReplaceAllRegExp);
    }

    private static void EmitAt(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
            il.Emit(OpCodes.Unbox_Any, ctx.Types.Double);
        }
        else
        {
            il.Emit(OpCodes.Ldc_R8, 0.0);
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringAt);
    }

    private static void EmitNormalize(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        il.Emit(OpCodes.Ldc_I4, arguments.Count);
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
        il.Emit(OpCodes.Call, ctx.Runtime!.StringNormalize);
    }

    private static void EmitLocaleCompare(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
            // ECMA-262: argument coerced via ToString protocol — handles objects
            // with custom toString/valueOf and avoids InvalidCastException for
            // non-string args (e.g. `"abc".indexOf({toString: () => "b"})`).
            il.Emit(OpCodes.Call, ctx.Runtime!.ToJsString);
        }
        else
        {
            il.Emit(OpCodes.Ldstr, "");
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.StringLocaleCompare);
        il.Emit(OpCodes.Box, ctx.Types.Double);
    }

    #endregion
}
