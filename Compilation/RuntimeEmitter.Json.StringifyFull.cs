using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits JsonStringifyFull as pure IL for standalone support.
    /// Signature: JsonStringifyFull(object? value, object? replacer, object? space) -> object?
    /// </summary>
    private void EmitJsonStringifyFull(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // First emit the escape helper (needed by stringify)
        EmitEscapeJsonStringHelper(typeBuilder);

        // Then emit the helper method for recursive stringification
        var stringifyFullHelper = EmitStringifyValueFullHelper(typeBuilder, runtime);

        var method = typeBuilder.DefineMethod(
            "JsonStringifyFull",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]  // value, replacer, space
        );
        runtime.JsonStringifyFull = method;

        var il = method.GetILGenerator();

        // Locals
        var indentStrLocal = il.DeclareLocal(_types.String);     // string indentStr
        var replacerFuncLocal = il.DeclareLocal(_types.Object);  // $TSFunction or null
        var allowedKeysLocal = il.DeclareLocal(_types.HashSetOfString);  // HashSet<string> or null
        var spaceDoubleLocal = il.DeclareLocal(_types.Double);
        var countLocal = il.DeclareLocal(_types.Int32);

        // Labels
        var spaceIsStringLabel = il.DefineLabel();
        var spaceIsNullLabel = il.DefineLabel();
        var spaceDoneLabel = il.DefineLabel();
        var replacerIsListLabel = il.DefineLabel();
        var replacerDoneLabel = il.DefineLabel();

        // ============ Parse space parameter ============
        // indentStr = ""
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, indentStrLocal);

        // if (space == null) goto spaceDoneLabel
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brfalse, spaceDoneLabel);

        // if (space is double)
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, spaceIsStringLabel);

        // space is double - convert to int spaces
        // count = (int)Math.Min(Math.Max((double)space, 0), 10)
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Stloc, spaceDoubleLocal);

        // Math.Max(space, 0)
        il.Emit(OpCodes.Ldloc, spaceDoubleLocal);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Math, "Max", [_types.Double, _types.Double]));

        // Math.Min(result, 10)
        il.Emit(OpCodes.Ldc_R8, 10.0);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Math, "Min", [_types.Double, _types.Double]));

        // Convert to int
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, countLocal);

        // indentStr = new string(' ', count)
        il.Emit(OpCodes.Ldc_I4, (int)' ');
        il.Emit(OpCodes.Ldloc, countLocal);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.String, [_types.Char, _types.Int32]));
        il.Emit(OpCodes.Stloc, indentStrLocal);
        il.Emit(OpCodes.Br, spaceDoneLabel);

        // space is string
        il.MarkLabel(spaceIsStringLabel);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, spaceDoneLabel);

        // indentStr = space.Length > 10 ? space.Substring(0, 10) : space
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.String, "Length").GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4, 10);
        var noTruncateLabel = il.DefineLabel();
        il.Emit(OpCodes.Ble, noTruncateLabel);

        // Truncate to 10 chars
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4, 10);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "Substring", [_types.Int32, _types.Int32]));
        il.Emit(OpCodes.Stloc, indentStrLocal);
        il.Emit(OpCodes.Br, spaceDoneLabel);

        il.MarkLabel(noTruncateLabel);
        il.Emit(OpCodes.Stloc, indentStrLocal);

        il.MarkLabel(spaceDoneLabel);

        // ============ Parse replacer parameter ============
        // replacerFunc = null, allowedKeys = null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, replacerFuncLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, allowedKeysLocal);

        // if (replacer == null) goto replacerDoneLabel
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, replacerDoneLabel);

        // if (replacer is List<object?>)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brtrue, replacerIsListLabel);

        // replacer is function - store it (assuming it's $TSFunction compatible)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stloc, replacerFuncLocal);
        il.Emit(OpCodes.Br, replacerDoneLabel);

        // replacer is List - convert to HashSet<string>
        il.MarkLabel(replacerIsListLabel);
        EmitConvertListToHashSet(il, allowedKeysLocal);

        il.MarkLabel(replacerDoneLabel);

        // ============ Call helper method ============
        // return StringifyValueFull(value, replacerFunc, allowedKeys, indentStr, 0)
        il.Emit(OpCodes.Ldarg_0);           // value
        il.Emit(OpCodes.Ldloc, replacerFuncLocal);   // replacer
        il.Emit(OpCodes.Ldloc, allowedKeysLocal);    // allowedKeys
        il.Emit(OpCodes.Ldloc, indentStrLocal);      // indentStr
        il.Emit(OpCodes.Ldc_I4_0);                   // depth = 0
        il.Emit(OpCodes.Call, stringifyFullHelper);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits code to convert List&lt;object?&gt; to HashSet&lt;string&gt; (filtering for strings only).
    /// </summary>
    private void EmitConvertListToHashSet(ILGenerator il, LocalBuilder allowedKeysLocal)
    {
        var listLocal = il.DeclareLocal(_types.ListOfObject);
        var iLocal = il.DeclareLocal(_types.Int32);
        var elemLocal = il.DeclareLocal(_types.Object);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var skipLabel = il.DefineLabel();

        // Cast replacer to List<object?>
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);

        // allowedKeys = new HashSet<string>()
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.HashSetOfString, Type.EmptyTypes));
        il.Emit(OpCodes.Stloc, allowedKeysLocal);

        // for (int i = 0; i < list.Count; i++)
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // elem = list[i]
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", [_types.Int32]));
        il.Emit(OpCodes.Stloc, elemLocal);

        // if (elem is string) allowedKeys.Add((string)elem)
        il.Emit(OpCodes.Ldloc, elemLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, skipLabel);

        il.Emit(OpCodes.Ldloc, allowedKeysLocal);
        il.Emit(OpCodes.Ldloc, elemLocal);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.HashSetOfString, "Add", [_types.String]));
        il.Emit(OpCodes.Pop);  // discard bool

        il.MarkLabel(skipLabel);

        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
    }

    /// <summary>
    /// Emits the StringifyValueFull helper method for recursive JSON stringification with full options.
    /// Signature: StringifyValueFull(object? value, object? replacer, HashSet&lt;string&gt;? allowedKeys, string indentStr, int depth) -> string?
    /// </summary>
    private MethodBuilder EmitStringifyValueFullHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "StringifyValueFull",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.String,
            [_types.Object, _types.Object, _types.HashSetOfString, _types.String, _types.Int32]
            // value, replacer, allowedKeys, indentStr, depth
        );

        var il = method.GetILGenerator();
        var valueLocal = il.DeclareLocal(_types.Object);

        var nullLabel = il.DefineLabel();
        var boolLabel = il.DefineLabel();
        var doubleLabel = il.DefineLabel();
        var stringLabel = il.DefineLabel();
        var listLabel = il.DefineLabel();
        var dictLabel = il.DefineLabel();
        var classInstanceLabel = il.DefineLabel();

        // Depth cap — recursive cycles (`a.self = a`) would otherwise recurse
        // unbounded and stack-overflow. ECMA-262 requires TypeError; the cap
        // is sized well above any legitimate nesting (512). Arg 4 = depth.
        var depthOkLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg, 4);
        il.Emit(OpCodes.Ldc_I4, 512);
        il.Emit(OpCodes.Blt, depthOkLabel);
        il.Emit(OpCodes.Ldstr, "TypeError: Converting circular structure to JSON");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(typeof(Exception), _types.String));
        il.Emit(OpCodes.Throw);
        il.MarkLabel(depthOkLabel);

        // Store value in local
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stloc, valueLocal);

        // if (value == null) return "null";
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Brfalse, nullLabel);

        // Check for BigInt
        EmitBigIntCheck(il, valueLocal);

        // Check for toJSON() method
        EmitToJsonCheck(il, valueLocal, runtime);

        // Type checks
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brtrue, boolLabel);

        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, doubleLabel);

        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, stringLabel);

        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brtrue, listLabel);

        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Check for emitted $Object instance
        EmitIsClassInstanceCheck(il, valueLocal, classInstanceLabel, runtime);

        // Default: return "null"
        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ldstr, "null");
        il.Emit(OpCodes.Ret);

        // bool
        il.MarkLabel(boolLabel);
        var trueLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Brtrue, trueLabel);
        il.Emit(OpCodes.Ldstr, "false");
        il.Emit(OpCodes.Ret);
        il.MarkLabel(trueLabel);
        il.Emit(OpCodes.Ldstr, "true");
        il.Emit(OpCodes.Ret);

        // double
        il.MarkLabel(doubleLabel);
        EmitFormatNumber(il, valueLocal);

        // string - escape for JSON
        il.MarkLabel(stringLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Call, _escapeJsonStringMethod!);
        il.Emit(OpCodes.Ret);

        // List<object?> - stringify array with full options
        il.MarkLabel(listLabel);
        EmitStringifyArrayFull(il, method, valueLocal, runtime);

        // Dictionary<string, object?> - stringify object with full options
        il.MarkLabel(dictLabel);
        EmitStringifyObjectFull(il, method, valueLocal, runtime);

        // Class instance
        il.MarkLabel(classInstanceLabel);
        var classFieldsLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var noClassFieldsLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Castclass, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Callvirt, runtime.IHasFieldsFieldsGetter);
        il.Emit(OpCodes.Stloc, classFieldsLocal);
        il.Emit(OpCodes.Ldloc, classFieldsLocal);
        il.Emit(OpCodes.Brfalse, noClassFieldsLabel);
        il.Emit(OpCodes.Ldloc, classFieldsLocal);
        il.Emit(OpCodes.Stloc, valueLocal);
        EmitStringifyObjectFull(il, method, valueLocal, runtime);
        il.MarkLabel(noClassFieldsLabel);
        il.Emit(OpCodes.Ldstr, "{}");
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// Emits array stringification with full options (replacer, indentation).
    /// </summary>
    private void EmitStringifyArrayFull(ILGenerator il, MethodBuilder stringifyMethod, LocalBuilder valueLocal, EmittedRuntime runtime)
    {
        var sbLocal = il.DeclareLocal(_types.StringBuilder);
        var arrLocal = il.DeclareLocal(_types.ListOfObject);
        var iLocal = il.DeclareLocal(_types.Int32);
        var newlineLocal = il.DeclareLocal(_types.String);
        var closeLocal = il.DeclareLocal(_types.String);
        var elemLocal = il.DeclareLocal(_types.Object);
        var strResultLocal = il.DeclareLocal(_types.String);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var hasIndentLabel = il.DefineLabel();
        var noIndentLabel = il.DefineLabel();
        var indentDoneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Stloc, arrLocal);

        // if (arr.Count == 0) return "[]";
        il.Emit(OpCodes.Ldloc, arrLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        var notEmpty = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, notEmpty);
        il.Emit(OpCodes.Ldstr, "[]");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notEmpty);

        // Check if indent is needed (indentStr.Length > 0)
        il.Emit(OpCodes.Ldarg_3);  // indentStr
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.String, "Length").GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, hasIndentLabel);

        // No indent
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, newlineLocal);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, closeLocal);
        il.Emit(OpCodes.Br, indentDoneLabel);

        // Has indent - compute newline and close strings
        il.MarkLabel(hasIndentLabel);
        // newline = "\n" + RepeatString(indentStr, depth + 1)
        EmitComputeNewline(il, newlineLocal, closeLocal);

        il.MarkLabel(indentDoneLabel);

        // StringBuilder sb = new StringBuilder("[");
        il.Emit(OpCodes.Ldstr, "[");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.StringBuilder, [_types.String]));
        il.Emit(OpCodes.Stloc, sbLocal);

        // for loop
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, arrLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // if (i > 0) sb.Append(",");
        il.Emit(OpCodes.Ldloc, iLocal);
        var skipComma = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, skipComma);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, ",");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);
        il.MarkLabel(skipComma);

        // sb.Append(newline);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldloc, newlineLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        // elem = arr[i]
        il.Emit(OpCodes.Ldloc, arrLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", [_types.Int32]));
        il.Emit(OpCodes.Stloc, elemLocal);

        // Stage E.2 M5: ECMA-262 25.5.2.4 SerializeJSONArray — a hole slot
        // serializes as "null". The replacer is NOT invoked for holes
        // (SerializeJSONProperty short-circuits on missing properties).
        var holeAppendedLabel = il.DefineLabel();
        var notHoleLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, elemLocal);
        il.Emit(OpCodes.Isinst, runtime.ArrayHoleType);
        il.Emit(OpCodes.Brfalse, notHoleLabel);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, "null");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, holeAppendedLabel);

        il.MarkLabel(notHoleLabel);

        // If replacer function exists, call it: elem = InvokeCallback(replacer, i, elem)
        EmitCallReplacerIfNeeded(il, elemLocal, iLocal, runtime);

        // strResult = StringifyValueFull(elem, replacer, allowedKeys, indentStr, depth + 1)
        il.Emit(OpCodes.Ldloc, elemLocal);
        il.Emit(OpCodes.Ldarg_1);  // replacer
        il.Emit(OpCodes.Ldarg_2);  // allowedKeys
        il.Emit(OpCodes.Ldarg_3);  // indentStr
        il.Emit(OpCodes.Ldarg, 4); // depth
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Call, stringifyMethod);
        il.Emit(OpCodes.Stloc, strResultLocal);

        // sb.Append(strResult ?? "null")
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldloc, strResultLocal);
        il.Emit(OpCodes.Dup);
        var notNullResult = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, notNullResult);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "null");
        il.MarkLabel(notNullResult);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        il.MarkLabel(holeAppendedLabel);

        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        // sb.Append(close);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldloc, closeLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        // sb.Append("]");
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, "]");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits code to compute newline and close strings for indentation.
    /// </summary>
    private void EmitComputeNewline(ILGenerator il, LocalBuilder newlineLocal, LocalBuilder closeLocal)
    {
        // newline = "\n" + RepeatString(indentStr, depth + 1)
        // We need String.Concat and a loop or use string constructor

        // For simplicity, use StringBuilder to build the indent
        var sbTemp = il.DeclareLocal(_types.StringBuilder);
        var jLocal = il.DeclareLocal(_types.Int32);
        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        // Build newline: "\n" + (indentStr * (depth + 1))
        il.Emit(OpCodes.Ldstr, "\n");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.StringBuilder, [_types.String]));
        il.Emit(OpCodes.Stloc, sbTemp);

        // for (int j = 0; j <= depth; j++) sb.Append(indentStr);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, jLocal);

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldarg, 4);  // depth
        il.Emit(OpCodes.Bgt, loopEnd);

        il.Emit(OpCodes.Ldloc, sbTemp);
        il.Emit(OpCodes.Ldarg_3);  // indentStr
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, jLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        il.Emit(OpCodes.Ldloc, sbTemp);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Stloc, newlineLocal);

        // Build close: "\n" + (indentStr * depth)
        var sbTemp2 = il.DeclareLocal(_types.StringBuilder);
        var loopStart2 = il.DefineLabel();
        var loopEnd2 = il.DefineLabel();

        il.Emit(OpCodes.Ldstr, "\n");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.StringBuilder, [_types.String]));
        il.Emit(OpCodes.Stloc, sbTemp2);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, jLocal);

        il.MarkLabel(loopStart2);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldarg, 4);  // depth
        il.Emit(OpCodes.Bge, loopEnd2);

        il.Emit(OpCodes.Ldloc, sbTemp2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, jLocal);
        il.Emit(OpCodes.Br, loopStart2);

        il.MarkLabel(loopEnd2);

        il.Emit(OpCodes.Ldloc, sbTemp2);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Stloc, closeLocal);
    }

    /// <summary>
    /// Emits code to call the replacer function if it exists.
    /// For arrays: replacer(index, elem) -> elem
    /// Handles both $TSFunction and $BoundTSFunction.
    /// </summary>
    private void EmitCallReplacerIfNeeded(ILGenerator il, LocalBuilder elemLocal, LocalBuilder indexLocal, EmittedRuntime runtime)
    {
        var skipLabel = il.DefineLabel();
        var isTSFunctionLabel = il.DefineLabel();
        var isBoundLabel = il.DefineLabel();
        var callDoneLabel = il.DefineLabel();
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // if (replacer == null) skip
        il.Emit(OpCodes.Ldarg_1);  // replacer
        il.Emit(OpCodes.Brfalse, skipLabel);

        // Create object[] { (double)index, elem } and store in local
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, elemLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Check if replacer is $TSFunction
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, isTSFunctionLabel);

        // Check if replacer is $BoundTSFunction
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Brtrue, isBoundLabel);

        // Unknown type - use InvokeValue fallback
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Call, runtime.InvokeValue);
        il.Emit(OpCodes.Stloc, elemLocal);
        il.Emit(OpCodes.Br, callDoneLabel);

        // isTSFunctionLabel: call $TSFunction.Invoke
        il.MarkLabel(isTSFunctionLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Stloc, elemLocal);
        il.Emit(OpCodes.Br, callDoneLabel);

        // isBoundLabel: call $BoundTSFunction.Invoke
        il.MarkLabel(isBoundLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, runtime.BoundTSFunctionInvoke);
        il.Emit(OpCodes.Stloc, elemLocal);

        il.MarkLabel(callDoneLabel);
        il.MarkLabel(skipLabel);
    }

    /// <summary>
    /// Emits code to call the replacer function with a string key.
    /// Handles both $TSFunction and $BoundTSFunction.
    /// </summary>
    private void EmitCallReplacerWithKey(ILGenerator il, LocalBuilder valueLocal, LocalBuilder keyLocal, EmittedRuntime runtime)
    {
        var skipLabel = il.DefineLabel();
        var isTSFunctionLabel = il.DefineLabel();
        var isBoundLabel = il.DefineLabel();
        var callDoneLabel = il.DefineLabel();
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        il.Emit(OpCodes.Ldarg_1);  // replacer
        il.Emit(OpCodes.Brfalse, skipLabel);

        // Create object[] { key, value } and store in local
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Check if replacer is $TSFunction
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, isTSFunctionLabel);

        // Check if replacer is $BoundTSFunction
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Brtrue, isBoundLabel);

        // Unknown type - use InvokeValue fallback
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Call, runtime.InvokeValue);
        il.Emit(OpCodes.Stloc, valueLocal);
        il.Emit(OpCodes.Br, callDoneLabel);

        // isTSFunctionLabel: call $TSFunction.Invoke
        il.MarkLabel(isTSFunctionLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Stloc, valueLocal);
        il.Emit(OpCodes.Br, callDoneLabel);

        // isBoundLabel: call $BoundTSFunction.Invoke
        il.MarkLabel(isBoundLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, runtime.BoundTSFunctionInvoke);
        il.Emit(OpCodes.Stloc, valueLocal);

        il.MarkLabel(callDoneLabel);
        il.MarkLabel(skipLabel);
    }

    /// <summary>
    /// Emits object stringification with full options (replacer, allowedKeys, indentation).
    /// </summary>
    private void EmitStringifyObjectFull(ILGenerator il, MethodBuilder stringifyMethod, LocalBuilder valueLocal, EmittedRuntime runtime)
    {
        var sbLocal = il.DeclareLocal(_types.StringBuilder);
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var enumeratorLocal = il.DeclareLocal(_types.DictionaryStringObjectEnumerator);
        var currentLocal = il.DeclareLocal(_types.KeyValuePairStringObject);
        var firstLocal = il.DeclareLocal(_types.Boolean);
        var newlineLocal = il.DeclareLocal(_types.String);
        var closeLocal = il.DeclareLocal(_types.String);
        var keyLocal = il.DeclareLocal(_types.String);
        var valLocal = il.DeclareLocal(_types.Object);
        var strResultLocal = il.DeclareLocal(_types.String);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var hasIndentLabel = il.DefineLabel();
        var indentDoneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        // if (dict.Count == 0) return "{}";
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Count").GetGetMethod()!);
        var notEmpty = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, notEmpty);
        il.Emit(OpCodes.Ldstr, "{}");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notEmpty);

        // Check indent
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.String, "Length").GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, hasIndentLabel);

        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, newlineLocal);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, closeLocal);
        il.Emit(OpCodes.Br, indentDoneLabel);

        il.MarkLabel(hasIndentLabel);
        EmitComputeNewline(il, newlineLocal, closeLocal);

        il.MarkLabel(indentDoneLabel);

        // StringBuilder sb = new StringBuilder("{");
        il.Emit(OpCodes.Ldstr, "{");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.StringBuilder, [_types.String]));
        il.Emit(OpCodes.Stloc, sbLocal);

        // bool first = true;
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, firstLocal);

        // Get enumerator
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, _types.DictionaryStringObjectEnumerator.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, loopEnd);

        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, _types.DictionaryStringObjectEnumerator.GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, currentLocal);

        // key = current.Key
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(_types.KeyValuePairStringObject, "Key").GetGetMethod()!);
        il.Emit(OpCodes.Stloc, keyLocal);

        // if (allowedKeys != null && !allowedKeys.Contains(key)) continue;
        var keyAllowedLabel = il.DefineLabel();
        var skipKeyCheckLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_2);  // allowedKeys
        il.Emit(OpCodes.Brfalse, skipKeyCheckLabel);

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.HashSetOfString, "Contains", [_types.String]));
        il.Emit(OpCodes.Brtrue, keyAllowedLabel);
        il.Emit(OpCodes.Br, loopStart);  // skip this key

        il.MarkLabel(skipKeyCheckLabel);
        il.MarkLabel(keyAllowedLabel);

        // val = current.Value
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(_types.KeyValuePairStringObject, "Value").GetGetMethod()!);
        il.Emit(OpCodes.Stloc, valLocal);

        // Call replacer if needed
        EmitCallReplacerWithKey(il, valLocal, keyLocal, runtime);

        // strResult = StringifyValueFull(val, replacer, allowedKeys, indentStr, depth + 1)
        il.Emit(OpCodes.Ldloc, valLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldarg, 4);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Call, stringifyMethod);
        il.Emit(OpCodes.Stloc, strResultLocal);

        // if (strResult == null) continue;
        il.Emit(OpCodes.Ldloc, strResultLocal);
        il.Emit(OpCodes.Brfalse, loopStart);

        // if (!first) sb.Append(",");
        il.Emit(OpCodes.Ldloc, firstLocal);
        var skipComma = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, skipComma);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, ",");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);
        il.MarkLabel(skipComma);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, firstLocal);

        // sb.Append(newline);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldloc, newlineLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        // sb.Append(EscapeJsonString(key));
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Call, _escapeJsonStringMethod!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        // sb.Append(indentStr.Length > 0 ? ": " : ":");
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.String, "Length").GetGetMethod()!);
        var colonNoSpace = il.DefineLabel();
        var colonDone = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, colonNoSpace);
        il.Emit(OpCodes.Ldstr, ": ");
        il.Emit(OpCodes.Br, colonDone);
        il.MarkLabel(colonNoSpace);
        il.Emit(OpCodes.Ldstr, ":");
        il.MarkLabel(colonDone);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        // sb.Append(strResult);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldloc, strResultLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        // Dispose enumerator
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Constrained, _types.DictionaryStringObjectEnumerator);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IDisposable, "Dispose"));

        // sb.Append(close);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldloc, closeLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        // sb.Append("}");
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, "}");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Ret);
    }

}
