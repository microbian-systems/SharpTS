using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    private MethodBuilder? _escapeJsonStringMethod;

    /// <summary>
    /// Emits a helper method that escapes a string for JSON output.
    /// This replaces dependency on System.Text.Json.JsonSerializer.
    /// </summary>
    private MethodBuilder EmitEscapeJsonStringHelper(TypeBuilder typeBuilder)
    {
        if (_escapeJsonStringMethod != null)
            return _escapeJsonStringMethod;

        var method = typeBuilder.DefineMethod(
            "EscapeJsonString",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.String,
            [_types.String]
        );

        var il = method.GetILGenerator();
        var sbLocal = il.DeclareLocal(_types.StringBuilder);
        var iLocal = il.DeclareLocal(_types.Int32);
        var cLocal = il.DeclareLocal(_types.Char);
        var lenLocal = il.DeclareLocal(_types.Int32);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var checkBackslash = il.DefineLabel();
        var checkNewline = il.DefineLabel();
        var checkReturn = il.DefineLabel();
        var checkTab = il.DefineLabel();
        var checkControl = il.DefineLabel();
        var appendNormal = il.DefineLabel();
        var nextChar = il.DefineLabel();

        // sb = new StringBuilder("\"");
        il.Emit(OpCodes.Ldstr, "\"");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.StringBuilder, [_types.String]));
        il.Emit(OpCodes.Stloc, sbLocal);

        // len = s.Length;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.String, "Length").GetGetMethod()!);
        il.Emit(OpCodes.Stloc, lenLocal);

        // i = 0;
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        // while (i < len)
        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, lenLocal);
        il.Emit(OpCodes.Bge, loopEnd);

        // c = s[i];
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "get_Chars", [_types.Int32]));
        il.Emit(OpCodes.Stloc, cLocal);

        // if (c == '"') sb.Append("\\\"");
        il.Emit(OpCodes.Ldloc, cLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'"');
        il.Emit(OpCodes.Bne_Un, checkBackslash);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, "\\\"");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, nextChar);

        // if (c == '\\') sb.Append("\\\\");
        il.MarkLabel(checkBackslash);
        il.Emit(OpCodes.Ldloc, cLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Bne_Un, checkNewline);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, "\\\\");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, nextChar);

        // if (c == '\n') sb.Append("\\n");
        il.MarkLabel(checkNewline);
        il.Emit(OpCodes.Ldloc, cLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'\n');
        il.Emit(OpCodes.Bne_Un, checkReturn);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, "\\n");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, nextChar);

        // if (c == '\r') sb.Append("\\r");
        il.MarkLabel(checkReturn);
        il.Emit(OpCodes.Ldloc, cLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'\r');
        il.Emit(OpCodes.Bne_Un, checkTab);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, "\\r");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, nextChar);

        // if (c == '\t') sb.Append("\\t");
        il.MarkLabel(checkTab);
        il.Emit(OpCodes.Ldloc, cLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'\t');
        il.Emit(OpCodes.Bne_Un, checkControl);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, "\\t");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, nextChar);

        // if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4"));
        il.MarkLabel(checkControl);
        il.Emit(OpCodes.Ldloc, cLocal);
        il.Emit(OpCodes.Ldc_I4, 32);
        il.Emit(OpCodes.Bge, appendNormal);
        // Control character - emit \uXXXX
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, "\\u");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldloc, sbLocal);
        // Convert char to int and format as 4-digit hex
        var charAsIntLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldloc, cLocal);
        il.Emit(OpCodes.Stloc, charAsIntLocal);
        il.Emit(OpCodes.Ldloca, charAsIntLocal);
        il.Emit(OpCodes.Ldstr, "x4");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Int32, "ToString", [_types.String]));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, nextChar);

        // Normal character - append as-is
        il.MarkLabel(appendNormal);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldloc, cLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.Char]));
        il.Emit(OpCodes.Pop);

        // i++;
        il.MarkLabel(nextChar);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStart);

        // sb.Append("\"");
        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, "\"");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        // return sb.ToString();
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Ret);

        _escapeJsonStringMethod = method;
        return method;
    }

    private void EmitJsonStringify(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // First emit the escape helper (needed by stringify)
        EmitEscapeJsonStringHelper(typeBuilder);

        // Then emit the main stringify helper
        var stringifyHelper = EmitJsonStringifyHelper(typeBuilder, runtime);

        var method = typeBuilder.DefineMethod(
            "JsonStringify",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.JsonStringify = method;

        var il = method.GetILGenerator();

        // Call our emitted StringifyValue helper
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0); // indent = 0
        il.Emit(OpCodes.Ldc_I4_0); // depth = 0
        il.Emit(OpCodes.Call, stringifyHelper);
        il.Emit(OpCodes.Ret);
    }

    private MethodBuilder EmitJsonStringifyHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "StringifyValue",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.String,
            [_types.Object, _types.Int32, _types.Int32] // value, indent, depth
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
        // unbounded and stack-overflow. ECMA-262 requires TypeError; the cap is
        // sized well above any legitimate nesting (512). Check is cheap; runs
        // at every entry so the throw fires before another frame is pushed.
        var depthOkLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_I4, 512);
        il.Emit(OpCodes.Blt, depthOkLabel);
        il.Emit(OpCodes.Ldstr, "Converting circular structure to JSON");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(depthOkLabel);

        // Store value in local (we may modify it via toJSON)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stloc, valueLocal);

        // if (value == null) return "null";
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Brfalse, nullLabel);

        // Check for BigInt - get type name and check
        EmitBigIntCheck(il, valueLocal, runtime);

        // Check for toJSON() method and call it if present
        EmitToJsonCheck(il, valueLocal, runtime);

        // if (value is bool)
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brtrue, boolLabel);

        // if (value is double)
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, doubleLabel);

        // if (value is string)
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, stringLabel);

        // if (value is List<object>)
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brtrue, listLabel);

        // if (value is Dictionary<string, object>)
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Check if it's an emitted $Object instance
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

        // List<object> - stringify array
        il.MarkLabel(listLabel);
        EmitStringifyArray(il, method, valueLocal, runtime);

        // Dictionary<string, object> - stringify object
        il.MarkLabel(dictLabel);
        EmitStringifyObject(il, method, valueLocal);

        // Class instance - stringify via $IHasFields fields dictionary
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
        EmitStringifyObject(il, method, valueLocal);

        il.MarkLabel(noClassFieldsLabel);
        il.Emit(OpCodes.Ldstr, "{}");
        il.Emit(OpCodes.Ret);

        return method;
    }

    private void EmitBigIntCheck(ILGenerator il, LocalBuilder valueLocal, EmittedRuntime runtime)
    {
        var notBigIntLabel = il.DefineLabel();
        var typeLocal = il.DeclareLocal(_types.Type);
        var nameLocal = il.DeclareLocal(_types.String);

        // var type = value.GetType();
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // var name = type.Name;
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.Type, "Name").GetGetMethod()!);
        il.Emit(OpCodes.Stloc, nameLocal);

        // if (name == "SharpTSBigInt" || name == "BigInteger")
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "SharpTSBigInt");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", [_types.String, _types.String]));
        var throwLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, throwLabel);

        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "BigInteger");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", [_types.String, _types.String]));
        il.Emit(OpCodes.Brfalse, notBigIntLabel);

        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "BigInt value can't be serialized in JSON");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(notBigIntLabel);
    }

    private void EmitToJsonCheck(ILGenerator il, LocalBuilder valueLocal, EmittedRuntime runtime)
    {
        var noToJsonLabel = il.DefineLabel();

        // First, check if value is a Dictionary<string, object?> (object literal).
        // If not, check for emitted $Object instance and read toJSON via TSObject.GetProperty.
        var notDictionaryLabel = il.DefineLabel();
        var notTsObjectLabel = il.DefineLabel();
        var toJsonFieldLocal = il.DeclareLocal(_types.Object);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // if (value is Dictionary<string, object?>)
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, notDictionaryLabel);

        // dict.TryGetValue("toJSON", out var fn)
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldstr, "toJSON");
        il.Emit(OpCodes.Ldloca, toJsonFieldLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "TryGetValue",
            _types.String, _types.Object.MakeByRefType()));
        il.Emit(OpCodes.Brfalse, notDictionaryLabel);

        // Check if field is a TSFunction
        var notTSFunctionLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, toJsonFieldLocal);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, notTSFunctionLabel);

        // Call TSFunction.Invoke with empty args
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Stloc, argsLocal);

        il.Emit(OpCodes.Ldloc, toJsonFieldLocal);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Stloc, valueLocal);
        il.Emit(OpCodes.Br, noToJsonLabel);

        il.MarkLabel(notTSFunctionLabel);
        // Check for BoundTSFunction
        var notBoundLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, toJsonFieldLocal);
        il.Emit(OpCodes.Isinst, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Brfalse, notBoundLabel);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Stloc, argsLocal);

        il.Emit(OpCodes.Ldloc, toJsonFieldLocal);
        il.Emit(OpCodes.Castclass, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, runtime.BoundTSFunctionInvoke);
        il.Emit(OpCodes.Stloc, valueLocal);
        il.Emit(OpCodes.Br, noToJsonLabel);

        il.MarkLabel(notBoundLabel);
        il.MarkLabel(notDictionaryLabel);

        // if (!(value is $IHasFields)) return;
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Brfalse, notTsObjectLabel);

        // toJsonField = (($IHasFields)value).GetProperty("toJSON");
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Castclass, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Ldstr, "toJSON");
        il.Emit(OpCodes.Callvirt, runtime.IHasFieldsGetProperty);
        il.Emit(OpCodes.Stloc, toJsonFieldLocal);
        il.Emit(OpCodes.Ldloc, toJsonFieldLocal);
        il.Emit(OpCodes.Brfalse, noToJsonLabel);

        // Reuse callable checks from dictionary branch.
        il.Emit(OpCodes.Ldloc, toJsonFieldLocal);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, notTsObjectLabel);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Stloc, argsLocal);
        il.Emit(OpCodes.Ldloc, toJsonFieldLocal);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Stloc, valueLocal);
        il.Emit(OpCodes.Br, noToJsonLabel);

        il.MarkLabel(notTsObjectLabel);
        il.MarkLabel(noToJsonLabel);
    }

    private void EmitIsClassInstanceCheck(ILGenerator il, LocalBuilder valueLocal, Label classInstanceLabel, EmittedRuntime runtime)
    {
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Brtrue, classInstanceLabel);
    }

    private void EmitFormatNumber(ILGenerator il, LocalBuilder valueLocal)
    {
        var local = il.DeclareLocal(_types.Double);
        var isIntLabel = il.DefineLabel();
        var isNanLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Stloc, local);

        // Check NaN/Infinity
        il.Emit(OpCodes.Ldloc, local);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Double, "IsNaN", [_types.Double]));
        il.Emit(OpCodes.Brtrue, isNanLabel);

        il.Emit(OpCodes.Ldloc, local);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Double, "IsInfinity", [_types.Double]));
        il.Emit(OpCodes.Brtrue, isNanLabel);

        // Check if integer
        il.Emit(OpCodes.Ldloc, local);
        il.Emit(OpCodes.Ldloc, local);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Math, "Floor", [_types.Double]));
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Brtrue, isIntLabel);

        // Float format
        il.Emit(OpCodes.Ldloca, local);
        il.Emit(OpCodes.Ldstr, "G15");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Double, "ToString", [_types.String]));
        il.Emit(OpCodes.Ret);

        // NaN/Infinity -> "null"
        il.MarkLabel(isNanLabel);
        il.Emit(OpCodes.Ldstr, "null");
        il.Emit(OpCodes.Ret);

        // Integer format
        il.MarkLabel(isIntLabel);
        il.Emit(OpCodes.Ldloc, local);
        il.Emit(OpCodes.Conv_I8);
        var longLocal = il.DeclareLocal(_types.Int64);
        il.Emit(OpCodes.Stloc, longLocal);
        il.Emit(OpCodes.Ldloca, longLocal);
        il.Emit(OpCodes.Call, _types.GetMethodNoParams(_types.Int64, "ToString"));
        il.Emit(OpCodes.Ret);
    }

    private void EmitStringifyArray(ILGenerator il, MethodBuilder stringifyMethod, LocalBuilder valueLocal, EmittedRuntime runtime)
    {
        var sbLocal = il.DeclareLocal(_types.StringBuilder);
        var arrLocal = il.DeclareLocal(_types.ListOfObject);
        var iLocal = il.DeclareLocal(_types.Int32);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

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

        // StringBuilder sb = new StringBuilder("[");
        il.Emit(OpCodes.Ldstr, "[");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.StringBuilder, [_types.String]));
        il.Emit(OpCodes.Stloc, sbLocal);

        // for (int i = 0; i < arr.Count; i++)
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

        // Stage E.2 M5: ECMA-262 25.5.2.4 SerializeJSONArray — a hole slot
        // serializes as "null" (SerializeJSONProperty returns undefined for
        // holes, which SerializeJSONArray substitutes with "null"). Without
        // this check the $ArrayHole sentinel would flow to StringifyValue
        // and render as "undefined" or similar.
        var notHoleLabel = il.DefineLabel();
        var appendedLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, arrLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", [_types.Int32]));
        il.Emit(OpCodes.Isinst, runtime.ArrayHoleType);
        il.Emit(OpCodes.Brfalse, notHoleLabel);
        // Hole: append "null" and skip.
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, "null");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, appendedLabel);

        il.MarkLabel(notHoleLabel);
        // sb.Append(StringifyValue(arr[i], indent, depth + 1));
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldloc, arrLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", [_types.Int32]));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Call, stringifyMethod);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        il.MarkLabel(appendedLabel);

        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        // sb.Append("]");
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, "]");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Ret);
    }

    private void EmitStringifyObject(ILGenerator il, MethodBuilder stringifyMethod, LocalBuilder valueLocal)
    {
        var sbLocal = il.DeclareLocal(_types.StringBuilder);
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var enumeratorLocal = il.DeclareLocal(_types.DictionaryStringObjectEnumerator);
        var currentLocal = il.DeclareLocal(_types.KeyValuePairStringObject);
        var firstLocal = il.DeclareLocal(_types.Boolean);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

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

        // sb.Append(EscapeJsonString(key));
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(_types.KeyValuePairStringObject, "Key").GetGetMethod()!);
        il.Emit(OpCodes.Call, _escapeJsonStringMethod!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        // sb.Append(":");
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldstr, ":");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        // sb.Append(StringifyValue(value, indent, depth + 1));
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(_types.KeyValuePairStringObject, "Value").GetGetMethod()!);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Call, stringifyMethod);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", [_types.String]));
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        // Dispose enumerator
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Constrained, _types.DictionaryStringObjectEnumerator);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IDisposable, "Dispose"));

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

