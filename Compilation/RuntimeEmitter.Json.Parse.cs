using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    private void EmitJsonParse(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "JsonParse",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.JsonParse = method;

        var il = method.GetILGenerator();
        var resultLocal = il.DeclareLocal(_types.Object);
        var endLabel = il.DefineLabel();

        // try { return JsonParseHelper(arg); }
        // catch (Exception ex) {
        //   if (ex.Data.Contains("__tsValue")) rethrow;
        //   throw $SyntaxError(ex.Message); // ECMA-262 24.5.1.1: parse failures throw SyntaxError
        // }
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, EmitJsonParseHelper(typeBuilder));
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Leave, endLabel);

        il.BeginCatchBlock(_types.Exception);
        var exLocal = il.DeclareLocal(_types.Exception);
        il.Emit(OpCodes.Stloc, exLocal);
        var rethrowLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, exLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.Exception, "Data").GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, "__tsValue");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.IDictionary, "Contains", _types.Object));
        il.Emit(OpCodes.Brtrue, rethrowLabel);

        il.Emit(OpCodes.Ldloc, exLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.Exception, "Message").GetGetMethod()!);
        il.Emit(OpCodes.Newobj, runtime.TSSyntaxErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(rethrowLabel);
        il.Emit(OpCodes.Rethrow);
        il.EndExceptionBlock();

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    private MethodBuilder EmitJsonParseHelper(TypeBuilder typeBuilder)
    {
        // Parse JSON using RuntimeTypes helper
        var method = typeBuilder.DefineMethod(
            "JsonParseHelper",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );

        var il = method.GetILGenerator();

        // Call RuntimeTypes.JsonParse directly - this method exists in the emitted assembly
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, EmitJsonParseStaticHelper(typeBuilder));
        il.Emit(OpCodes.Ret);

        return method;
    }

    private MethodBuilder EmitJsonParseStaticHelper(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod(
            "ParseJsonValue",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );

        var il = method.GetILGenerator();

        // Simple implementation: just call ToString and return
        // This won't properly parse, but at least we can test the infrastructure
        var strLocal = il.DeclareLocal(_types.String);
        var notNullLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse_S, notNullLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Stloc, strLocal);

        // Parse using JsonDocument - keep typeof() for System.Text.Json types
        var parseMethod = typeof(System.Text.Json.JsonDocument).GetMethod("Parse",
            [typeof(string), typeof(System.Text.Json.JsonDocumentOptions)])!;
        var optionsLocal = il.DeclareLocal(typeof(System.Text.Json.JsonDocumentOptions));
        il.Emit(OpCodes.Ldloc, strLocal);
        il.Emit(OpCodes.Ldloca, optionsLocal);
        il.Emit(OpCodes.Initobj, typeof(System.Text.Json.JsonDocumentOptions));
        il.Emit(OpCodes.Ldloc, optionsLocal);
        il.Emit(OpCodes.Call, parseMethod);

        // Get RootElement
        var docLocal = il.DeclareLocal(typeof(System.Text.Json.JsonDocument));
        il.Emit(OpCodes.Stloc, docLocal);
        il.Emit(OpCodes.Ldloc, docLocal);
        il.Emit(OpCodes.Callvirt, typeof(System.Text.Json.JsonDocument).GetProperty("RootElement")!.GetGetMethod()!);

        // Store element first before calling Clone (Clone is an instance method on struct)
        var elemLocal = il.DeclareLocal(typeof(System.Text.Json.JsonElement));
        il.Emit(OpCodes.Stloc, elemLocal);

        // Clone the element to avoid disposal issues (need address for struct instance method)
        il.Emit(OpCodes.Ldloca, elemLocal);
        il.Emit(OpCodes.Call, typeof(System.Text.Json.JsonElement).GetMethod("Clone")!);

        // Store cloned element and convert it
        var clonedLocal = il.DeclareLocal(typeof(System.Text.Json.JsonElement));
        il.Emit(OpCodes.Stloc, clonedLocal);
        il.Emit(OpCodes.Ldloca, clonedLocal);
        il.Emit(OpCodes.Call, EmitConvertJsonElementHelper(typeBuilder));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notNullLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        return method;
    }

    private MethodBuilder EmitConvertJsonElementHelper(TypeBuilder typeBuilder)
    {
        // Convert JsonElement to appropriate runtime type
        var method = typeBuilder.DefineMethod(
            "ConvertJsonElement",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.Object,
            [typeof(System.Text.Json.JsonElement).MakeByRefType()] // byref for struct
        );

        var il = method.GetILGenerator();

        var resultDictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var resultListLocal = il.DeclareLocal(_types.ListOfObject);
        var propEnumLocal = il.DeclareLocal(typeof(System.Text.Json.JsonElement.ObjectEnumerator));
        var arrEnumLocal = il.DeclareLocal(typeof(System.Text.Json.JsonElement.ArrayEnumerator));
        var propLocal = il.DeclareLocal(typeof(System.Text.Json.JsonProperty));
        var elemLocal = il.DeclareLocal(typeof(System.Text.Json.JsonElement));

        var undefinedLabel = il.DefineLabel();
        var objectLabel = il.DefineLabel();
        var arrayLabel = il.DefineLabel();
        var stringLabel = il.DefineLabel();
        var numberLabel = il.DefineLabel();
        var trueLiteralLabel = il.DefineLabel();
        var falseLiteralLabel = il.DefineLabel();
        var nullLabel = il.DefineLabel();
        var objLoopStart = il.DefineLabel();
        var objLoopEnd = il.DefineLabel();
        var arrLoopStart = il.DefineLabel();
        var arrLoopEnd = il.DefineLabel();

        // switch on element.ValueKind
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(System.Text.Json.JsonElement).GetProperty("ValueKind")!.GetGetMethod()!);

        // Check each value kind
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4, (int)System.Text.Json.JsonValueKind.Object);
        il.Emit(OpCodes.Beq, objectLabel);

        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4, (int)System.Text.Json.JsonValueKind.Array);
        il.Emit(OpCodes.Beq, arrayLabel);

        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4, (int)System.Text.Json.JsonValueKind.String);
        il.Emit(OpCodes.Beq, stringLabel);

        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4, (int)System.Text.Json.JsonValueKind.Number);
        il.Emit(OpCodes.Beq, numberLabel);

        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4, (int)System.Text.Json.JsonValueKind.True);
        il.Emit(OpCodes.Beq, trueLiteralLabel);

        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4, (int)System.Text.Json.JsonValueKind.False);
        il.Emit(OpCodes.Beq, falseLiteralLabel);

        il.Emit(OpCodes.Pop); // pop the valueKind we've been checking
        il.Emit(OpCodes.Br, nullLabel);

        // Object
        il.MarkLabel(objectLabel);
        il.Emit(OpCodes.Pop); // pop valueKind
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Stloc, resultDictLocal);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(System.Text.Json.JsonElement).GetMethod("EnumerateObject")!);
        il.Emit(OpCodes.Stloc, propEnumLocal);

        il.MarkLabel(objLoopStart);
        il.Emit(OpCodes.Ldloca, propEnumLocal);
        il.Emit(OpCodes.Call, typeof(System.Text.Json.JsonElement.ObjectEnumerator).GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, objLoopEnd);

        il.Emit(OpCodes.Ldloca, propEnumLocal);
        il.Emit(OpCodes.Call, typeof(System.Text.Json.JsonElement.ObjectEnumerator).GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, propLocal);

        // dict[name] = ConvertJsonElement(ref value)
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldloca, propLocal);
        il.Emit(OpCodes.Call, typeof(System.Text.Json.JsonProperty).GetProperty("Name")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloca, propLocal);
        il.Emit(OpCodes.Call, typeof(System.Text.Json.JsonProperty).GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, elemLocal);
        il.Emit(OpCodes.Ldloca, elemLocal);
        il.Emit(OpCodes.Call, method); // recursive
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", [_types.String, _types.Object]));
        il.Emit(OpCodes.Br, objLoopStart);

        il.MarkLabel(objLoopEnd);
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ret);

        // Array
        il.MarkLabel(arrayLabel);
        il.Emit(OpCodes.Pop); // pop valueKind
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.ListOfObject));
        il.Emit(OpCodes.Stloc, resultListLocal);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(System.Text.Json.JsonElement).GetMethod("EnumerateArray")!);
        il.Emit(OpCodes.Stloc, arrEnumLocal);

        il.MarkLabel(arrLoopStart);
        il.Emit(OpCodes.Ldloca, arrEnumLocal);
        il.Emit(OpCodes.Call, typeof(System.Text.Json.JsonElement.ArrayEnumerator).GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, arrLoopEnd);

        il.Emit(OpCodes.Ldloc, resultListLocal);
        il.Emit(OpCodes.Ldloca, arrEnumLocal);
        il.Emit(OpCodes.Call, typeof(System.Text.Json.JsonElement.ArrayEnumerator).GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, elemLocal);
        il.Emit(OpCodes.Ldloca, elemLocal);
        il.Emit(OpCodes.Call, method); // recursive
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", [_types.Object]));
        il.Emit(OpCodes.Br, arrLoopStart);

        il.MarkLabel(arrLoopEnd);
        il.Emit(OpCodes.Ldloc, resultListLocal);
        il.Emit(OpCodes.Ret);

        // String
        il.MarkLabel(stringLabel);
        il.Emit(OpCodes.Pop); // pop valueKind
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(System.Text.Json.JsonElement).GetMethod("GetString")!);
        il.Emit(OpCodes.Ret);

        // Number
        il.MarkLabel(numberLabel);
        il.Emit(OpCodes.Pop); // pop valueKind
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(System.Text.Json.JsonElement).GetMethod("GetDouble")!);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);

        // True
        il.MarkLabel(trueLiteralLabel);
        il.Emit(OpCodes.Pop); // pop valueKind
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);

        // False
        il.MarkLabel(falseLiteralLabel);
        il.Emit(OpCodes.Pop); // pop valueKind
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);

        // Null/Undefined
        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        return method;
    }
}

