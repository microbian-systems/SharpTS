using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    private void EmitCreateArray(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CreateArray",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.ObjectArray]
        );
        runtime.CreateArray = method;

        var il = method.GetILGenerator();
        // new List<object>(elements)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.IEnumerableOfObject));
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetLength(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetLength",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Int32,
            [_types.Object]
        );
        runtime.GetLength = method;

        var il = method.GetILGenerator();
        var listLabel = il.DefineLabel();
        var stringLabel = il.DefineLabel();
        var defaultLabel = il.DefineLabel();

        // List
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brtrue, listLabel);

        // String
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, stringLabel);

        // Default
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(listLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(stringLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.String, "Length").GetGetMethod()!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetElement(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetElement",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Int32]
        );
        runtime.GetElement = method;

        var il = method.GetILGenerator();
        var listLabel = il.DefineLabel();
        var stringLabel = il.DefineLabel();

        // List
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brtrue, listLabel);

        // String
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, stringLabel);

        // Default
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(listLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", _types.Int32));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(stringLabel);
        var charLocal = il.DeclareLocal(_types.Char);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "get_Chars", _types.Int32));
        il.Emit(OpCodes.Stloc, charLocal);
        il.Emit(OpCodes.Ldloca, charLocal);
        il.Emit(OpCodes.Call, _types.GetMethodNoParams(_types.Char, "ToString"));
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetKeys(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetKeys",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.Object]
        );
        runtime.GetKeys = method;

        var il = method.GetILGenerator();
        var dictType = _types.DictionaryStringObject;
        var listType = _types.ListOfObject;

        var resultLocal = il.DeclareLocal(listType);
        var dictLocal = il.DeclareLocal(dictType);
        var listLocal = il.DeclareLocal(listType);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var fieldsDictLocal = il.DeclareLocal(dictType);

        var checkListLabel = il.DefineLabel();
        var reflectionLabel = il.DefineLabel();
        var returnEmptyLabel = il.DefineLabel();
        var returnResultLabel = il.DefineLabel();

        // if (obj is Dictionary<string, object?> dict)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, dictType);
        il.Emit(OpCodes.Stloc, dictLocal);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, checkListLabel);

        // return dict.Keys.Select(k => (object?)k).ToList();
        // Simplified: iterate keys and add to list
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        // Use KeyCollection and iterate
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(dictType, "Keys").GetGetMethod()!);
        var keysType = _types.MakeGenericType(typeof(Dictionary<,>.KeyCollection).GetGenericTypeDefinition(), _types.String, _types.Object);
        var keysEnumeratorType = _types.MakeGenericType(typeof(Dictionary<,>.KeyCollection.Enumerator).GetGenericTypeDefinition(), _types.String, _types.Object);
        var keysEnumeratorLocal = il.DeclareLocal(keysEnumeratorType);
        il.Emit(OpCodes.Callvirt, keysType.GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, keysEnumeratorLocal);

        var keysLoopStart = il.DefineLabel();
        var keysLoopEnd = il.DefineLabel();
        il.MarkLabel(keysLoopStart);
        il.Emit(OpCodes.Ldloca, keysEnumeratorLocal);
        il.Emit(OpCodes.Call, keysEnumeratorType.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, keysLoopEnd);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloca, keysEnumeratorLocal);
        il.Emit(OpCodes.Call, keysEnumeratorType.GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);
        il.Emit(OpCodes.Br, keysLoopStart);

        il.MarkLabel(keysLoopEnd);
        il.Emit(OpCodes.Ldloca, keysEnumeratorLocal);
        il.Emit(OpCodes.Call, keysEnumeratorType.GetMethod("Dispose")!);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        // Check if obj is List<object?>
        il.MarkLabel(checkListLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, listType);
        il.Emit(OpCodes.Stloc, listLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Brfalse, reflectionLabel);

        // Return indices as strings: Enumerable.Range(0, list.Count).Select(i => (object?)i.ToString()).ToList()
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var listLoopStart = il.DefineLabel();
        var listLoopEnd = il.DefineLabel();
        il.MarkLabel(listLoopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, listLoopEnd);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloca, indexLocal);
        il.Emit(OpCodes.Call, _types.Int32.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);

        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, listLoopStart);

        il.MarkLabel(listLoopEnd);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        // Emitted $Object path for class instances (standalone-safe)
        il.MarkLabel(reflectionLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnEmptyLabel);

        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);
        // if (!(obj is $IHasFields)) return empty list
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Brfalse, returnResultLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Callvirt, runtime.IHasFieldsFieldsGetter);
        il.Emit(OpCodes.Stloc, fieldsDictLocal);
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Brfalse, returnResultLabel);

        // Add keys from _fields dictionary
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(dictType, "Keys").GetGetMethod()!);
        var keysEnumeratorLocal2 = il.DeclareLocal(keysEnumeratorType);
        il.Emit(OpCodes.Callvirt, keysType.GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, keysEnumeratorLocal2);

        var fieldsKeysLoopStart = il.DefineLabel();
        var fieldsKeysLoopEnd = il.DefineLabel();
        var keyLocal = il.DeclareLocal(_types.String);

        il.MarkLabel(fieldsKeysLoopStart);
        il.Emit(OpCodes.Ldloca, keysEnumeratorLocal2);
        il.Emit(OpCodes.Call, keysEnumeratorType.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, fieldsKeysLoopEnd);

        il.Emit(OpCodes.Ldloca, keysEnumeratorLocal2);
        il.Emit(OpCodes.Call, keysEnumeratorType.GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, keyLocal);

        // Only add if not already in result (skip duplicates)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Contains")!);
        var skipDuplicateLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, skipDuplicateLabel);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);

        il.MarkLabel(skipDuplicateLabel);
        il.Emit(OpCodes.Br, fieldsKeysLoopStart);

        il.MarkLabel(fieldsKeysLoopEnd);
        il.Emit(OpCodes.Ldloca, keysEnumeratorLocal2);
        il.Emit(OpCodes.Call, keysEnumeratorType.GetMethod("Dispose")!);

        il.MarkLabel(returnResultLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        // Return empty list
        il.MarkLabel(returnEmptyLabel);
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetOwnPropertyNames(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetOwnPropertyNames",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.Object]
        );
        runtime.GetOwnPropertyNames = method;

        var il = method.GetILGenerator();

        var dictLabel = il.DefineLabel();
        var listLabel = il.DefineLabel();
        var objectLabel = il.DefineLabel();
        var returnEmptyLabel = il.DefineLabel();

        // Local for result list
        var namesLocal = il.DeclareLocal(_types.ListOfObject);
        var iLocal = il.DeclareLocal(_types.Int32);

        // if (obj is Dictionary<string, object?> dict)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // if (obj is List<object?> list)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brtrue, listLabel);

        // if (obj != null)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, objectLabel);

        // return empty list
        il.MarkLabel(returnEmptyLabel);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, Type.EmptyTypes));
        il.Emit(OpCodes.Ret);

        // Dictionary case: return dict.Keys as list
        il.MarkLabel(dictLabel);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, Type.EmptyTypes));
        il.Emit(OpCodes.Stloc, namesLocal);

        // Get the Keys collection and iterate
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.DictionaryStringObject, "Keys"));
        // Get enumerator
        var keysEnumeratorLocal = il.DeclareLocal(_types.IEnumeratorOfString);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IEnumerableOfString, "GetEnumerator"));
        il.Emit(OpCodes.Stloc, keysEnumeratorLocal);

        var dictLoopStart = il.DefineLabel();
        var dictLoopEnd = il.DefineLabel();
        il.MarkLabel(dictLoopStart);
        // while (enumerator.MoveNext())
        il.Emit(OpCodes.Ldloc, keysEnumeratorLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IEnumerator, "MoveNext"));
        il.Emit(OpCodes.Brfalse, dictLoopEnd);
        // names.Add(enumerator.Current)
        il.Emit(OpCodes.Ldloc, namesLocal);
        il.Emit(OpCodes.Ldloc, keysEnumeratorLocal);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.IEnumeratorOfString, "Current"));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));
        il.Emit(OpCodes.Br, dictLoopStart);

        il.MarkLabel(dictLoopEnd);
        il.Emit(OpCodes.Ldloc, namesLocal);
        il.Emit(OpCodes.Ret);

        // List case: return ["0", "1", ..., "length"]
        il.MarkLabel(listLabel);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, Type.EmptyTypes));
        il.Emit(OpCodes.Stloc, namesLocal);

        // for (int i = 0; i < list.Count; i++)
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        var listLoopStart = il.DefineLabel();
        var listLoopEnd = il.DefineLabel();
        il.Emit(OpCodes.Br, listLoopEnd);

        il.MarkLabel(listLoopStart);
        // names.Add(i.ToString())
        il.Emit(OpCodes.Ldloc, namesLocal);
        il.Emit(OpCodes.Ldloca, iLocal);
        il.Emit(OpCodes.Call, _types.GetMethodNoParams(_types.Int32, "ToString"));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));
        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(listLoopEnd);
        // i < list.Count
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.ListOfObject, "Count"));
        il.Emit(OpCodes.Blt, listLoopStart);

        // names.Add("length")
        il.Emit(OpCodes.Ldloc, namesLocal);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));
        il.Emit(OpCodes.Ldloc, namesLocal);
        il.Emit(OpCodes.Ret);

        // Object case: use $IHasFields fields dictionary (standalone-safe)
        il.MarkLabel(objectLabel);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, Type.EmptyTypes));
        il.Emit(OpCodes.Stloc, namesLocal);
        var noFieldsDictLabel = il.DefineLabel();
        var fieldsDictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Brfalse, noFieldsDictLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Callvirt, runtime.IHasFieldsFieldsGetter);
        il.Emit(OpCodes.Stloc, fieldsDictLocal);
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Brfalse, noFieldsDictLabel);

        // Iterate fieldsDict.Keys
        var dictKeysEnumLocal = il.DeclareLocal(_types.IEnumeratorOfString);
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.DictionaryStringObject, "Keys"));
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IEnumerableOfString, "GetEnumerator"));
        il.Emit(OpCodes.Stloc, dictKeysEnumLocal);

        var dictKeysLoopStart = il.DefineLabel();
        var dictKeysLoopEnd = il.DefineLabel();
        il.MarkLabel(dictKeysLoopStart);
        il.Emit(OpCodes.Ldloc, dictKeysEnumLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IEnumerator, "MoveNext"));
        il.Emit(OpCodes.Brfalse, dictKeysLoopEnd);

        // var key = enumerator.Current
        var keyLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldloc, dictKeysEnumLocal);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.IEnumeratorOfString, "Current"));
        il.Emit(OpCodes.Stloc, keyLocal);

        // if (!names.Contains(key)) names.Add(key)
        var skipAddKeyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, namesLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Contains", _types.Object));
        il.Emit(OpCodes.Brtrue, skipAddKeyLabel);
        il.Emit(OpCodes.Ldloc, namesLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));
        il.MarkLabel(skipAddKeyLabel);
        il.Emit(OpCodes.Br, dictKeysLoopStart);

        il.MarkLabel(dictKeysLoopEnd);

        il.MarkLabel(noFieldsDictLabel);
        il.Emit(OpCodes.Ldloc, namesLocal);
        il.Emit(OpCodes.Ret);
    }

    private void EmitSpreadArray(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SpreadArray",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.Object]
        );
        runtime.SpreadArray = method;

        var il = method.GetILGenerator();
        var listLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brtrue, listLabel);

        // Not a list - return empty
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.EmptyTypes));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(listLabel);
        // Return new list with same elements
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.IEnumerableOfObject));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ConcatArrays: concatenates multiple iterables into a single List&lt;object&gt;.
    /// Supports arrays, strings, and custom iterables with Symbol.iterator.
    /// Signature: List&lt;object&gt; ConcatArrays(object[] arrays, $TSSymbol iteratorSymbol, Type runtimeType)
    /// </summary>
    private void EmitConcatArrays(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ConcatArrays",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.ObjectArray, runtime.TSSymbolType, _types.Type]  // Added iteratorSymbol and runtimeType
        );
        runtime.ConcatArrays = method;

        var il = method.GetILGenerator();
        // var result = new List<object>();
        // foreach (var element in arrays) result.AddRange(IterateToList(element, iteratorSymbol, runtimeType));
        // return result;
        var resultLocal = il.DeclareLocal(_types.ListOfObject);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var iteratedLocal = il.DeclareLocal(_types.ListOfObject);  // Result of IterateToList
        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.EmptyTypes));
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, loopEnd);

        // Call IterateToList(arrays[index], iteratorSymbol, runtimeType)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Ldarg_1);  // iteratorSymbol
        il.Emit(OpCodes.Ldarg_2);  // runtimeType
        il.Emit(OpCodes.Call, runtime.IterateToList);
        il.Emit(OpCodes.Stloc, iteratedLocal);

        // result.AddRange(iterated)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, iteratedLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "AddRange", _types.IEnumerableOfObject));

        // index++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ExpandCallArgs: expands function call arguments with spread support.
    /// Supports arrays, strings, and custom iterables with Symbol.iterator.
    /// Signature: object[] ExpandCallArgs(object[] args, bool[] isSpread, $TSSymbol iteratorSymbol, Type runtimeType)
    /// </summary>
    private void EmitExpandCallArgs(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ExpandCallArgs",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ObjectArray,
            [_types.ObjectArray, _types.BoolArray, runtime.TSSymbolType, _types.Type]  // Added iteratorSymbol and runtimeType
        );
        runtime.ExpandCallArgs = method;

        var il = method.GetILGenerator();
        // Create result list, iterate args, expand spreads using IterateToList
        var resultLocal = il.DeclareLocal(_types.ListOfObject);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var iteratedLocal = il.DeclareLocal(_types.ListOfObject);  // Result of IterateToList
        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.EmptyTypes));
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, loopEnd);

        // Check if this is a spread
        var notSpreadLabel = il.DefineLabel();
        var continueLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_I1);
        il.Emit(OpCodes.Brfalse, notSpreadLabel);

        // Is spread - use IterateToList to handle any iterable
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Ldarg_2);  // iteratorSymbol
        il.Emit(OpCodes.Ldarg_3);  // runtimeType
        il.Emit(OpCodes.Call, runtime.IterateToList);
        il.Emit(OpCodes.Stloc, iteratedLocal);

        // result.AddRange(iterated)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, iteratedLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "AddRange", _types.IEnumerableOfObject));
        il.Emit(OpCodes.Br, continueLabel);

        // Not spread - add single element
        il.MarkLabel(notSpreadLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));

        il.MarkLabel(continueLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "ToArray", _types.EmptyTypes));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Phase 1: Define $BoundArrayMethod type, fields, and constructor.
    /// Must be called before EmitRuntimeClass so GetListProperty can use the constructor.
    /// </summary>
    internal void EmitBoundArrayMethodTypeDefinition(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        // Define class: public sealed class $BoundArrayMethod
        var typeBuilder = moduleBuilder.DefineType(
            "$BoundArrayMethod",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object
        );
        runtime.BoundArrayMethodType = typeBuilder;

        // Fields
        var listField = typeBuilder.DefineField("_list", _types.ListOfObject, FieldAttributes.Private);
        var methodNameField = typeBuilder.DefineField("_methodName", _types.String, FieldAttributes.Private);
        runtime.BoundArrayMethodListField = listField;
        runtime.BoundArrayMethodNameField = methodNameField;

        // Constructor: public $BoundArrayMethod(List<object> list, string methodName)
        var ctorBuilder = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.ListOfObject, _types.String]
        );
        runtime.BoundArrayMethodCtor = ctorBuilder;

        var ctorIL = ctorBuilder.GetILGenerator();
        // Call base constructor
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.Object));
        // this._list = list
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Stfld, listField);
        // this._methodName = methodName
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldarg_2);
        ctorIL.Emit(OpCodes.Stfld, methodNameField);
        ctorIL.Emit(OpCodes.Ret);

        // Define Invoke method signature in Phase 1 so EmitInvokeValue can reference it.
        // The IL body is emitted in Phase 2 (EmitBoundArrayMethodFinalize).
        var invokeBuilder = typeBuilder.DefineMethod(
            "Invoke",
            MethodAttributes.Public,
            _types.Object,
            [_types.ObjectArray]
        );
        runtime.BoundArrayMethodInvoke = invokeBuilder;
    }

    /// <summary>
    /// Phase 2: Emit Invoke method body for $BoundArrayMethod and create the type.
    /// Must be called after EmitRuntimeClass so array methods are available.
    /// </summary>
    internal void EmitBoundArrayMethodFinalize(EmittedRuntime runtime)
    {
        var typeBuilder = runtime.BoundArrayMethodType;
        var listField = runtime.BoundArrayMethodListField;
        var methodNameField = runtime.BoundArrayMethodNameField;
        var invokeBuilder = runtime.BoundArrayMethodInvoke;

        var il = invokeBuilder.GetILGenerator();

        // Switch on _methodName to dispatch to appropriate runtime method
        var defaultLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // Helper to emit a method case
        void EmitMethodCase(string methodName, MethodBuilder runtimeMethod)
        {
            var skipLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, methodNameField);
            il.Emit(OpCodes.Ldstr, methodName);
            il.Emit(OpCodes.Call, _types.StringOpEquality);
            il.Emit(OpCodes.Brfalse, skipLabel);

            // Call runtime.Method(_list, args) or runtime.Method(_list) depending on method
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, listField);

            // Methods that take args
            if (methodName is "push" or "unshift" or "slice" or "indexOf" or "includes" or "concat" or "reduce")
            {
                il.Emit(OpCodes.Ldarg_1); // args
            }
            else if (methodName == "join")
            {
                // join takes a single separator (args[0]) not the whole args array
                // Handle: args.Length > 0 ? args[0] : null
                var noArgsLabel = il.DefineLabel();
                var afterArgLabel = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_1); // args
                il.Emit(OpCodes.Ldlen);
                il.Emit(OpCodes.Brfalse, noArgsLabel);
                il.Emit(OpCodes.Ldarg_1); // args
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldelem_Ref); // args[0]
                il.Emit(OpCodes.Br, afterArgLabel);
                il.MarkLabel(noArgsLabel);
                il.Emit(OpCodes.Ldnull);
                il.MarkLabel(afterArgLabel);
            }

            il.Emit(OpCodes.Call, runtimeMethod);

            // Box if needed (for methods returning double)
            if (runtimeMethod.ReturnType == _types.Double)
            {
                il.Emit(OpCodes.Box, _types.Double);
            }
            else if (runtimeMethod.ReturnType == _types.Boolean)
            {
                il.Emit(OpCodes.Box, _types.Boolean);
            }
            else if (runtimeMethod.ReturnType == _types.String)
            {
                // String is already object-compatible
            }

            il.Emit(OpCodes.Br, endLabel);
            il.MarkLabel(skipLabel);
        }

        // Helper to emit a callback method case (list, callback) where callback is args[0]
        void EmitCallbackMethodCase(string methodName, MethodBuilder runtimeMethod)
        {
            var skipLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, methodNameField);
            il.Emit(OpCodes.Ldstr, methodName);
            il.Emit(OpCodes.Call, _types.StringOpEquality);
            il.Emit(OpCodes.Brfalse, skipLabel);

            // Call runtime.Method(_list, args[0])
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, listField);
            il.Emit(OpCodes.Ldarg_1); // args
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldelem_Ref); // args[0]
            il.Emit(OpCodes.Call, runtimeMethod);

            // Handle return type
            if (runtimeMethod.ReturnType == _types.Void)
            {
                il.Emit(OpCodes.Ldnull); // void methods need to return null for object return
            }
            else if (runtimeMethod.ReturnType == _types.Boolean)
            {
                il.Emit(OpCodes.Box, _types.Boolean);
            }
            else if (runtimeMethod.ReturnType == _types.Double)
            {
                il.Emit(OpCodes.Box, _types.Double);
            }

            il.Emit(OpCodes.Br, endLabel);
            il.MarkLabel(skipLabel);
        }

        // Emit cases for each array method
        EmitMethodCase("join", runtime.ArrayJoin);
        EmitMethodCase("push", runtime.ArrayPush);
        EmitMethodCase("pop", runtime.ArrayPop);
        EmitMethodCase("shift", runtime.ArrayShift);
        EmitMethodCase("unshift", runtime.ArrayUnshift);
        EmitMethodCase("slice", runtime.ArraySlice);
        EmitMethodCase("indexOf", runtime.ArrayIndexOf);
        EmitMethodCase("includes", runtime.ArrayIncludes);
        EmitMethodCase("concat", runtime.ArrayConcat);
        EmitMethodCase("reverse", runtime.ArrayReverse);

        // Methods that take (list, args) for callback + options
        EmitMethodCase("reduce", runtime.ArrayReduce);

        // Callback-based methods (take list, callback)
        EmitCallbackMethodCase("map", runtime.ArrayMap);
        EmitCallbackMethodCase("filter", runtime.ArrayFilter);
        EmitCallbackMethodCase("forEach", runtime.ArrayForEach);
        EmitCallbackMethodCase("find", runtime.ArrayFind);
        EmitCallbackMethodCase("findIndex", runtime.ArrayFindIndex);
        EmitCallbackMethodCase("some", runtime.ArraySome);
        EmitCallbackMethodCase("every", runtime.ArrayEvery);

        // Default: return null
        il.MarkLabel(defaultLabel);
        il.Emit(OpCodes.Ldnull);

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);

        // Create the type
        typeBuilder.CreateType();
    }
}

