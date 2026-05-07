using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace SharpTS.Compilation;

/// <summary>
/// Emits util.format helper methods (UtilFormat, format specifier handling).
/// </summary>
public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits UtilFormat: handles format specifiers %s, %d, %i, %f, %j, %o, %O, %%.
    /// </summary>
    private void EmitUtilFormatBody(EmittedRuntime runtime)
    {
        var il = runtime.UtilFormat.GetILGenerator();

        // Locals
        var resultLocal = il.DeclareLocal(typeof(StringBuilder));
        var formatLocal = il.DeclareLocal(_types.String);
        var argIndexLocal = il.DeclareLocal(_types.Int32);
        var iLocal = il.DeclareLocal(_types.Int32);
        var lengthLocal = il.DeclareLocal(_types.Int32);
        var argsLengthLocal = il.DeclareLocal(_types.Int32);

        var emptyLabel = il.DefineLabel();
        var singleArgLabel = il.DefineLabel();
        var loopStartLabel = il.DefineLabel();
        var loopConditionLabel = il.DefineLabel();
        var appendRemaining = il.DefineLabel();
        var appendRemainingLoop = il.DefineLabel();
        var appendRemainingCondition = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // if (args.Length == 0) return ""
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Brfalse, emptyLabel);

        // format = args[0]?.ToString() ?? ""
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Dup);
        var notNullFormatLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, notNullFormatLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Br, singleArgLabel);
        il.MarkLabel(notNullFormatLabel);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Dup);
        var formatNotNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, formatNotNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(formatNotNullLabel);

        il.MarkLabel(singleArgLabel);
        il.Emit(OpCodes.Stloc, formatLocal);

        // argsLength = args.Length
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, argsLengthLocal);

        // Note: We can't early-return here even with 1 arg because we need to process %% escapes

        // result = new StringBuilder()
        il.Emit(OpCodes.Newobj, _types.StringBuilderDefaultCtor);
        il.Emit(OpCodes.Stloc, resultLocal);

        // argIndex = 1
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, argIndexLocal);

        // i = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        // length = format.Length
        il.Emit(OpCodes.Ldloc, formatLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, lengthLocal);

        il.Emit(OpCodes.Br, loopConditionLabel);

        // Main loop
        il.MarkLabel(loopStartLabel);

        // Check for '%'
        il.Emit(OpCodes.Ldloc, formatLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [typeof(int)])!);
        il.Emit(OpCodes.Ldc_I4, '%');
        var notPercentLabel = il.DefineLabel();
        il.Emit(OpCodes.Bne_Un, notPercentLabel);

        // Check if i + 1 < length
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldloc, lengthLocal);
        il.Emit(OpCodes.Bge, notPercentLabel);

        // Get specifier
        var specifierLocal = il.DeclareLocal(_types.Char);
        il.Emit(OpCodes.Ldloc, formatLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [typeof(int)])!);
        il.Emit(OpCodes.Stloc, specifierLocal);

        // Handle specifiers with simple fallthrough pattern
        EmitFormatSpecifierHandling(il, runtime, resultLocal, argIndexLocal, iLocal, argsLengthLocal, specifierLocal, loopConditionLabel);

        // Not a format specifier - append character normally
        il.MarkLabel(notPercentLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, formatLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [typeof(int)])!);
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendChar);
        il.Emit(OpCodes.Pop);

        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);

        // Loop condition
        il.MarkLabel(loopConditionLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, lengthLocal);
        il.Emit(OpCodes.Blt, loopStartLabel);

        // Append remaining arguments
        il.MarkLabel(appendRemaining);
        il.Emit(OpCodes.Br, appendRemainingCondition);

        il.MarkLabel(appendRemainingLoop);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4, ' ');
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendChar);
        il.Emit(OpCodes.Pop);

        // Append args[argIndex]?.ToString() ?? "undefined"
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Dup);
        var argNotNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, argNotNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "undefined");
        var argAppendLabel = il.DefineLabel();
        il.Emit(OpCodes.Br, argAppendLabel);
        il.MarkLabel(argNotNullLabel);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Dup);
        var argStrNotNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, argStrNotNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "undefined");
        il.MarkLabel(argStrNotNullLabel);
        il.MarkLabel(argAppendLabel);
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendString);
        il.Emit(OpCodes.Pop);

        // argIndex++
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, argIndexLocal);

        il.MarkLabel(appendRemainingCondition);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldloc, argsLengthLocal);
        il.Emit(OpCodes.Blt, appendRemainingLoop);

        // Return result.ToString()
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Callvirt, _types.StringBuilderToString);
        il.Emit(OpCodes.Ret);

        // Empty case
        il.MarkLabel(emptyLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Ret);
    }

    private void EmitFormatSpecifierHandling(
        ILGenerator il,
        EmittedRuntime runtime,
        LocalBuilder resultLocal,
        LocalBuilder argIndexLocal,
        LocalBuilder iLocal,
        LocalBuilder argsLengthLocal,
        LocalBuilder specifierLocal,
        Label loopConditionLabel)
    {
        var specifierSLabel = il.DefineLabel();
        var specifierDLabel = il.DefineLabel();
        var specifierFLabel = il.DefineLabel();
        var specifierJLabel = il.DefineLabel();
        var specifierOLabel = il.DefineLabel();
        var specifierPercentLabel = il.DefineLabel();
        var unknownSpecifierLabel = il.DefineLabel();
        var continueLabel = il.DefineLabel();

        // Switch on specifier
        il.Emit(OpCodes.Ldloc, specifierLocal);
        il.Emit(OpCodes.Ldc_I4, 's');
        il.Emit(OpCodes.Beq, specifierSLabel);

        il.Emit(OpCodes.Ldloc, specifierLocal);
        il.Emit(OpCodes.Ldc_I4, 'd');
        il.Emit(OpCodes.Beq, specifierDLabel);

        il.Emit(OpCodes.Ldloc, specifierLocal);
        il.Emit(OpCodes.Ldc_I4, 'i');
        il.Emit(OpCodes.Beq, specifierDLabel);

        il.Emit(OpCodes.Ldloc, specifierLocal);
        il.Emit(OpCodes.Ldc_I4, 'f');
        il.Emit(OpCodes.Beq, specifierFLabel);

        il.Emit(OpCodes.Ldloc, specifierLocal);
        il.Emit(OpCodes.Ldc_I4, 'j');
        il.Emit(OpCodes.Beq, specifierJLabel);

        il.Emit(OpCodes.Ldloc, specifierLocal);
        il.Emit(OpCodes.Ldc_I4, 'o');
        il.Emit(OpCodes.Beq, specifierOLabel);

        il.Emit(OpCodes.Ldloc, specifierLocal);
        il.Emit(OpCodes.Ldc_I4, 'O');
        il.Emit(OpCodes.Beq, specifierOLabel);

        il.Emit(OpCodes.Ldloc, specifierLocal);
        il.Emit(OpCodes.Ldc_I4, '%');
        il.Emit(OpCodes.Beq, specifierPercentLabel);

        il.Emit(OpCodes.Br, unknownSpecifierLabel);

        // %s - string
        il.MarkLabel(specifierSLabel);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldloc, argsLengthLocal);
        var noArgSLabel = il.DefineLabel();
        il.Emit(OpCodes.Bge, noArgSLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        EmitToStringOrUndefined(il);
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendString);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, argIndexLocal);
        il.Emit(OpCodes.Br, continueLabel);
        il.MarkLabel(noArgSLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "%s");
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendString);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, continueLabel);

        // %d/%i - integer
        il.MarkLabel(specifierDLabel);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldloc, argsLengthLocal);
        var noArgDLabel = il.DefineLabel();
        il.Emit(OpCodes.Bge, noArgDLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, typeof(double));
        var notDoubleDLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notDoubleDLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Unbox_Any, typeof(double));
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendInt);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, argIndexLocal);
        il.Emit(OpCodes.Br, continueLabel);
        il.MarkLabel(notDoubleDLabel);
        il.MarkLabel(noArgDLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4, '%');
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendChar);
        il.Emit(OpCodes.Ldloc, specifierLocal);
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendChar);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, continueLabel);

        // %f - float
        il.MarkLabel(specifierFLabel);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldloc, argsLengthLocal);
        var noArgFLabel = il.DefineLabel();
        il.Emit(OpCodes.Bge, noArgFLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, typeof(double));
        var notDoubleFLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notDoubleFLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Unbox_Any, typeof(double));
        var fLocal = il.DeclareLocal(typeof(double));
        il.Emit(OpCodes.Stloc, fLocal);
        il.Emit(OpCodes.Ldloca, fLocal);
        il.Emit(OpCodes.Call, typeof(System.Globalization.CultureInfo).GetProperty("InvariantCulture")!.GetGetMethod()!);
        il.Emit(OpCodes.Call, _types.DoubleToStringWithFormat);
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendString);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, argIndexLocal);
        il.Emit(OpCodes.Br, continueLabel);
        il.MarkLabel(notDoubleFLabel);
        il.MarkLabel(noArgFLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "%f");
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendString);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, continueLabel);

        // %j - JSON. The whole "consume an arg + JsonStringify it" branch needs
        // runtime.JsonStringify, which only exists when UsesJSON is true. When
        // gated off, fall through to the literal-append branch (`%j` stays as
        // text in the output) so util.format works without JSON support.
        il.MarkLabel(specifierJLabel);
        var noArgJLabel = il.DefineLabel();
        if (_features.UsesJSON)
        {
            il.Emit(OpCodes.Ldloc, argIndexLocal);
            il.Emit(OpCodes.Ldloc, argsLengthLocal);
            il.Emit(OpCodes.Bge, noArgJLabel);
            il.Emit(OpCodes.Ldloc, resultLocal);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, argIndexLocal);
            il.Emit(OpCodes.Ldelem_Ref);
            // Call JsonStringify which returns object? (always a string for valid input)
            il.Emit(OpCodes.Call, runtime.JsonStringify);
            // Convert result to string (it's already a string, but cast to be safe)
            il.Emit(OpCodes.Castclass, _types.String);
            il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendString);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldloc, argIndexLocal);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, argIndexLocal);
            il.Emit(OpCodes.Br, continueLabel);
        }
        il.MarkLabel(noArgJLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "%j");
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendString);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, continueLabel);

        // %o/%O - object (inspect)
        il.MarkLabel(specifierOLabel);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldloc, argsLengthLocal);
        var noArgOLabel = il.DefineLabel();
        il.Emit(OpCodes.Bge, noArgOLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Ldc_I4_2); // depth
        il.Emit(OpCodes.Ldc_I4_0); // currentDepth
        il.Emit(OpCodes.Call, runtime.UtilInspectValue);
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendString);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldloc, argIndexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, argIndexLocal);
        il.Emit(OpCodes.Br, continueLabel);
        il.MarkLabel(noArgOLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4, '%');
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendChar);
        il.Emit(OpCodes.Ldloc, specifierLocal);
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendChar);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, continueLabel);

        // %% - literal percent
        il.MarkLabel(specifierPercentLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4, '%');
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendChar);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, continueLabel);

        // Unknown specifier - just append the character
        il.MarkLabel(unknownSpecifierLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4, '%');
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendChar);
        il.Emit(OpCodes.Pop);
        // Don't skip the specifier character, let normal processing handle it
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopConditionLabel);

        // Continue: i += 2 and loop
        il.MarkLabel(continueLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopConditionLabel);
    }

    private void EmitToStringOrUndefined(ILGenerator il)
    {
        // Stack: value
        // Returns: string
        il.Emit(OpCodes.Dup);
        var notNullLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, notNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "undefined");
        il.Emit(OpCodes.Br, endLabel);
        il.MarkLabel(notNullLabel);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Dup);
        var strNotNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, strNotNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "undefined");
        il.MarkLabel(strNotNullLabel);
        il.MarkLabel(endLabel);
    }
}
