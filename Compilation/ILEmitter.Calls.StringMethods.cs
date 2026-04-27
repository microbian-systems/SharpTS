using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// String-only method dispatch with runtime type checking for the IL emitter.
/// </summary>
public partial class ILEmitter
{
    /// <summary>
    /// Emits a string-only method call with runtime type checking for any/unknown types.
    /// Checks if the receiver is a string at runtime and dispatches accordingly.
    /// For padEnd, padStart, trim, replace, split, etc.
    /// </summary>
    private void EmitStringOnlyMethodCall(Expr obj, string methodName, List<Expr> arguments)
    {
        // Emit the object and store in local
        EmitExpression(obj);
        EmitBoxIfNeeded(obj);

        var objLocal = IL.DeclareLocal(_ctx.Types.Object);
        IL.Emit(OpCodes.Stloc, objLocal);

        var builder = _ctx.ILBuilder;
        var isStringLabel = builder.DefineLabel("string_method_string");
        var fallbackLabel = builder.DefineLabel("string_method_fallback");
        var doneLabel = builder.DefineLabel("string_method_done");

        // Check if it's a string
        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Isinst, _ctx.Types.String);
        builder.Emit_Brtrue(isStringLabel);

        // Fall through to dynamic dispatch
        builder.Emit_Br(fallbackLabel);

        // String path - call the appropriate string method
        builder.MarkLabel(isStringLabel);
        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Castclass, _ctx.Types.String);

        switch (methodName)
        {
            case "padEnd":
                IL.Emit(OpCodes.Ldc_I4, arguments.Count);
                IL.Emit(OpCodes.Newarr, _ctx.Types.Object);
                for (int i = 0; i < arguments.Count; i++)
                {
                    IL.Emit(OpCodes.Dup);
                    IL.Emit(OpCodes.Ldc_I4, i);
                    EmitExpression(arguments[i]);
                    EmitBoxIfNeeded(arguments[i]);
                    IL.Emit(OpCodes.Stelem_Ref);
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StringPadEnd);
                break;

            case "padStart":
                IL.Emit(OpCodes.Ldc_I4, arguments.Count);
                IL.Emit(OpCodes.Newarr, _ctx.Types.Object);
                for (int i = 0; i < arguments.Count; i++)
                {
                    IL.Emit(OpCodes.Dup);
                    IL.Emit(OpCodes.Ldc_I4, i);
                    EmitExpression(arguments[i]);
                    EmitBoxIfNeeded(arguments[i]);
                    IL.Emit(OpCodes.Stelem_Ref);
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StringPadStart);
                break;

            case "trim":
                IL.Emit(OpCodes.Callvirt, _ctx.Types.GetMethodNoParams(_ctx.Types.String, "Trim"));
                break;

            case "trimStart":
                IL.Emit(OpCodes.Callvirt, _ctx.Types.GetMethodNoParams(_ctx.Types.String, "TrimStart"));
                break;

            case "trimEnd":
                IL.Emit(OpCodes.Callvirt, _ctx.Types.GetMethodNoParams(_ctx.Types.String, "TrimEnd"));
                break;

            case "toUpperCase":
                IL.Emit(OpCodes.Callvirt, _ctx.Types.GetMethodNoParams(_ctx.Types.String, "ToUpper"));
                break;

            case "toLowerCase":
                IL.Emit(OpCodes.Callvirt, _ctx.Types.GetMethodNoParams(_ctx.Types.String, "ToLower"));
                break;

            case "replace":
                if (arguments.Count >= 2)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                    EmitExpression(arguments[1]);
                    EmitBoxIfNeeded(arguments[1]);
                    IL.Emit(OpCodes.Call, _ctx.Runtime!.Stringify);
                }
                else
                {
                    IL.Emit(OpCodes.Ldstr, "");
                    IL.Emit(OpCodes.Ldstr, "");
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StringReplaceRegExp);
                break;

            case "replaceAll":
                if (arguments.Count >= 2)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                    // Don't cast — the regex-aware helper accepts object.
                    EmitExpression(arguments[1]);
                    EmitBoxIfNeeded(arguments[1]);
                    IL.Emit(OpCodes.Call, _ctx.Runtime!.Stringify);
                }
                else
                {
                    IL.Emit(OpCodes.Ldstr, "");
                    IL.Emit(OpCodes.Ldnull);
                    IL.Emit(OpCodes.Ldstr, "");
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StringReplaceAllRegExp);
                break;

            case "split":
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldstr, "");
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StringSplitRegExp);
                break;

            case "match":
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldstr, "");
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StringMatchRegExp);
                break;

            case "search":
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldstr, "");
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StringSearchRegExp);
                IL.Emit(OpCodes.Box, _ctx.Types.Double);
                break;

            case "repeat":
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                    IL.Emit(OpCodes.Unbox_Any, _ctx.Types.Double);
                }
                else
                {
                    IL.Emit(OpCodes.Ldc_R8, 0.0);
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StringRepeat);
                break;

            case "charCodeAt":
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                    IL.Emit(OpCodes.Unbox_Any, _ctx.Types.Double);
                }
                else
                {
                    IL.Emit(OpCodes.Ldc_R8, 0.0);
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StringCharCodeAt);
                IL.Emit(OpCodes.Box, _ctx.Types.Double);
                break;

            case "at":
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                    IL.Emit(OpCodes.Unbox_Any, _ctx.Types.Double);
                }
                else
                {
                    IL.Emit(OpCodes.Ldc_R8, 0.0);
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StringAt);
                break;

            case "lastIndexOf":
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                    IL.Emit(OpCodes.Call, _ctx.Runtime!.ToJsString);
                }
                else
                {
                    IL.Emit(OpCodes.Ldstr, "");
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StringLastIndexOf);
                IL.Emit(OpCodes.Box, _ctx.Types.Double);
                break;

            case "normalize":
                IL.Emit(OpCodes.Ldc_I4, arguments.Count);
                IL.Emit(OpCodes.Ldc_I4, arguments.Count);
                IL.Emit(OpCodes.Newarr, _ctx.Types.Object);
                for (int i = 0; i < arguments.Count; i++)
                {
                    IL.Emit(OpCodes.Dup);
                    IL.Emit(OpCodes.Ldc_I4, i);
                    EmitExpression(arguments[i]);
                    EmitBoxIfNeeded(arguments[i]);
                    IL.Emit(OpCodes.Stelem_Ref);
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StringNormalize);
                break;

            case "localeCompare":
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                    IL.Emit(OpCodes.Call, _ctx.Runtime!.ToJsString);
                }
                else
                {
                    IL.Emit(OpCodes.Ldstr, "");
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StringLocaleCompare);
                IL.Emit(OpCodes.Box, _ctx.Types.Double);
                break;
        }
        builder.Emit_Br(doneLabel);

        // Fallback path - use dynamic dispatch via GetProperty/InvokeMethodValue
        builder.MarkLabel(fallbackLabel);
        IL.Emit(OpCodes.Ldloc, objLocal);  // receiver
        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Ldstr, methodName);
        IL.Emit(OpCodes.Call, _ctx.Runtime!.GetProperty);

        // Create args array
        IL.Emit(OpCodes.Ldc_I4, arguments.Count);
        IL.Emit(OpCodes.Newarr, _ctx.Types.Object);
        for (int i = 0; i < arguments.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            EmitExpression(arguments[i]);
            EmitBoxIfNeeded(arguments[i]);
            IL.Emit(OpCodes.Stelem_Ref);
        }

        IL.Emit(OpCodes.Call, _ctx.Runtime!.InvokeMethodValue);

        builder.MarkLabel(doneLabel);
    }
}
