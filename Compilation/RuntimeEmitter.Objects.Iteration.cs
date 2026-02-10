using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    private void EmitObjectRest(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Accept object instead of Dictionary to support both object literals and class instances
        var method = typeBuilder.DefineMethod(
            "ObjectRest",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.DictionaryStringObject,
            [_types.Object, _types.ListOfObject]
        );
        runtime.ObjectRest = method;

        var il = method.GetILGenerator();

        var dictLabel = il.DefineLabel();
        var tsObjectLabel = il.DefineLabel();
        var emptyLabel = il.DefineLabel();
        var processLabel = il.DefineLabel();

        var sourceDictLocal = il.DeclareLocal(_types.DictionaryStringObject);

        // Check if arg0 is Dictionary<string, object>
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Check if arg0 is $IHasFields (covers $Object and class instances)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Brtrue, tsObjectLabel);

        // Fallback: null or unsupported source
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, emptyLabel);
        il.Emit(OpCodes.Ldstr, "ObjectRest requires dictionary or $IHasFields.");
        il.Emit(OpCodes.Newobj, _types.InvalidOperationExceptionCtorString);
        il.Emit(OpCodes.Throw);

        // Dictionary path: cast arg0 directly
        il.MarkLabel(dictLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, sourceDictLocal);
        il.Emit(OpCodes.Br, processLabel);

        // $IHasFields path: use Fields getter
        il.MarkLabel(tsObjectLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Callvirt, runtime.IHasFieldsFieldsGetter);
        il.Emit(OpCodes.Stloc, sourceDictLocal);
        il.Emit(OpCodes.Br, processLabel);

        // Empty result fallback
        il.MarkLabel(emptyLabel);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Ret);

        // Process the source dictionary (now in sourceDictLocal)
        il.MarkLabel(processLabel);

        // Create result dictionary
        var resultLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Stloc, resultLocal);

        // Create HashSet<string> from excludeKeys
        var excludeSetLocal = il.DeclareLocal(_types.HashSetOfString);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.HashSetOfString));
        il.Emit(OpCodes.Stloc, excludeSetLocal);

        // Add each exclude key to the set
        var excludeIndexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, excludeIndexLocal);

        var excludeLoopStart = il.DefineLabel();
        var excludeLoopEnd = il.DefineLabel();

        il.MarkLabel(excludeLoopStart);
        il.Emit(OpCodes.Ldloc, excludeIndexLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, excludeLoopEnd);

        // Get excludeKeys[i] and add to set if not null
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, excludeIndexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Item").GetGetMethod()!);
        var keyLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, keyLocal);

        var skipAdd = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Brfalse, skipAdd);

        il.Emit(OpCodes.Ldloc, excludeSetLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.HashSetOfString, "Add", _types.String));
        il.Emit(OpCodes.Pop); // discard bool return

        il.MarkLabel(skipAdd);
        il.Emit(OpCodes.Ldloc, excludeIndexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, excludeIndexLocal);
        il.Emit(OpCodes.Br, excludeLoopStart);

        il.MarkLabel(excludeLoopEnd);

        // Iterate over source dictionary keys using sourceDictLocal
        // We need the KeyCollection.Enumerator
        var keyCollectionType = typeof(Dictionary<string, object>.KeyCollection);
        var keysEnumType = typeof(Dictionary<string, object>.KeyCollection.Enumerator);

        var keysEnumLocal = il.DeclareLocal(keysEnumType);
        il.Emit(OpCodes.Ldloc, sourceDictLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Keys")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(keyCollectionType, "GetEnumerator"));
        il.Emit(OpCodes.Stloc, keysEnumLocal);

        var dictLoopStart = il.DefineLabel();
        var dictLoopEnd = il.DefineLabel();

        il.MarkLabel(dictLoopStart);
        // MoveNext
        il.Emit(OpCodes.Ldloca, keysEnumLocal);
        il.Emit(OpCodes.Call, _types.GetMethodNoParams(keysEnumType, "MoveNext"));
        il.Emit(OpCodes.Brfalse, dictLoopEnd);

        // Get Current key
        il.Emit(OpCodes.Ldloca, keysEnumLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(keysEnumType, "Current")!.GetGetMethod()!);
        var currentKeyLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Stloc, currentKeyLocal);

        // Check if key is in excludeSet
        il.Emit(OpCodes.Ldloc, excludeSetLocal);
        il.Emit(OpCodes.Ldloc, currentKeyLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.HashSetOfString, "Contains", _types.String));
        var skipKey = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, skipKey);

        // Add to result: result[key] = sourceDict[key]
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, currentKeyLocal);
        il.Emit(OpCodes.Ldloc, sourceDictLocal);
        il.Emit(OpCodes.Ldloc, currentKeyLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Item")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Item")!.GetSetMethod()!);

        il.MarkLabel(skipKey);
        il.Emit(OpCodes.Br, dictLoopStart);

        il.MarkLabel(dictLoopEnd);

        // Dispose enumerator
        il.Emit(OpCodes.Ldloca, keysEnumLocal);
        il.Emit(OpCodes.Constrained, keysEnumType);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IDisposable, "Dispose"));

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetValues(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetValues",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.Object]
        );
        runtime.GetValues = method;

        var il = method.GetILGenerator();
        var dictType = _types.DictionaryStringObject;
        var listType = _types.ListOfObject;
        var kvpType = _types.KeyValuePairStringObject;
        var enumeratorType = _types.MakeGenericType(typeof(Dictionary<,>.Enumerator).GetGenericTypeDefinition(), _types.String, _types.Object);

        var resultLocal = il.DeclareLocal(listType);
        var dictLocal = il.DeclareLocal(dictType);
        var fieldsDictLocal = il.DeclareLocal(dictType);
        var enumeratorLocal = il.DeclareLocal(enumeratorType);
        var currentLocal = il.DeclareLocal(kvpType);

        var reflectionLabel = il.DefineLabel();
        var returnEmptyLabel = il.DefineLabel();
        var returnResultLabel = il.DefineLabel();
        var fieldsLoopStart = il.DefineLabel();
        var fieldsLoopEnd = il.DefineLabel();

        // if (obj is Dictionary<string, object?> dict)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, dictType);
        il.Emit(OpCodes.Stloc, dictLocal);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, reflectionLabel);

        // Create result list and add all values from dictionary
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Callvirt, dictType.GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        var dictLoopStart = il.DefineLabel();
        var dictLoopEnd = il.DefineLabel();
        il.MarkLabel(dictLoopStart);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, dictLoopEnd);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, currentLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);
        il.Emit(OpCodes.Br, dictLoopStart);

        il.MarkLabel(dictLoopEnd);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("Dispose")!);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        // Emitted $Object path for class instances (standalone-safe)
        il.MarkLabel(reflectionLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnEmptyLabel);

        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);
        // $IHasFields supports class-instance key/value storage.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Brfalse, returnResultLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Callvirt, runtime.IHasFieldsFieldsGetter);
        il.Emit(OpCodes.Stloc, fieldsDictLocal);
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Brfalse, returnResultLabel);

        // Iterate _fields and add values
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Callvirt, dictType.GetMethod("GetEnumerator")!);
        var fieldsDictEnumeratorLocal = il.DeclareLocal(enumeratorType);
        il.Emit(OpCodes.Stloc, fieldsDictEnumeratorLocal);

        il.MarkLabel(fieldsLoopStart);
        il.Emit(OpCodes.Ldloca, fieldsDictEnumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, fieldsLoopEnd);

        il.Emit(OpCodes.Ldloca, fieldsDictEnumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, currentLocal);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);
        il.Emit(OpCodes.Br, fieldsLoopStart);

        il.MarkLabel(fieldsLoopEnd);
        il.Emit(OpCodes.Ldloca, fieldsDictEnumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("Dispose")!);

        il.MarkLabel(returnResultLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnEmptyLabel);
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetEntries(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetEntries",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.Object]
        );
        runtime.GetEntries = method;

        var il = method.GetILGenerator();
        var dictType = _types.DictionaryStringObject;
        var listType = _types.ListOfObject;
        var kvpType = _types.KeyValuePairStringObject;
        var enumeratorType = _types.MakeGenericType(typeof(Dictionary<,>.Enumerator).GetGenericTypeDefinition(), _types.String, _types.Object);

        var resultLocal = il.DeclareLocal(listType);
        var dictLocal = il.DeclareLocal(dictType);
        var fieldsDictLocal = il.DeclareLocal(dictType);
        var enumeratorLocal = il.DeclareLocal(enumeratorType);
        var currentLocal = il.DeclareLocal(kvpType);
        var entryLocal = il.DeclareLocal(listType);

        var reflectionLabel = il.DefineLabel();
        var returnEmptyLabel = il.DefineLabel();
        var returnResultLabel = il.DefineLabel();
        var fieldsLoopStart = il.DefineLabel();
        var fieldsLoopEnd = il.DefineLabel();

        // if (obj is Dictionary<string, object?> dict)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, dictType);
        il.Emit(OpCodes.Stloc, dictLocal);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, reflectionLabel);

        // Create result list and add [key, value] entries
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Callvirt, dictType.GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        var dictLoopStart = il.DefineLabel();
        var dictLoopEnd = il.DefineLabel();
        il.MarkLabel(dictLoopStart);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, dictLoopEnd);

        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, currentLocal);

        // var entry = new List<object?> { kvp.Key, kvp.Value };
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, entryLocal);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Key")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);
        il.Emit(OpCodes.Br, dictLoopStart);

        il.MarkLabel(dictLoopEnd);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("Dispose")!);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        // Emitted $Object path for class instances (standalone-safe)
        il.MarkLabel(reflectionLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnEmptyLabel);

        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Brfalse, returnResultLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Callvirt, runtime.IHasFieldsFieldsGetter);
        il.Emit(OpCodes.Stloc, fieldsDictLocal);
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Brfalse, returnResultLabel);

        var fieldsDictEnumeratorLocal = il.DeclareLocal(enumeratorType);
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Callvirt, dictType.GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, fieldsDictEnumeratorLocal);

        il.MarkLabel(fieldsLoopStart);
        il.Emit(OpCodes.Ldloca, fieldsDictEnumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, fieldsLoopEnd);

        il.Emit(OpCodes.Ldloca, fieldsDictEnumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, currentLocal);

        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, entryLocal);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Key")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);
        il.Emit(OpCodes.Br, fieldsLoopStart);

        il.MarkLabel(fieldsLoopEnd);
        il.Emit(OpCodes.Ldloca, fieldsDictEnumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("Dispose")!);

        il.MarkLabel(returnResultLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnEmptyLabel);
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitIsArray(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "IsArray",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object]
        );
        runtime.IsArray = method;

        var il = method.GetILGenerator();
        var trueLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // Check if IList<object?> (covers List<object?>, $Array, and any other array-like type)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.IListOfObject);
        il.Emit(OpCodes.Brtrue, trueLabel);

        // False
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Br, endLabel);

        // True
        il.MarkLabel(trueLabel);
        il.Emit(OpCodes.Ldc_I4_1);

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.fromEntries(entries) - converts iterable of [key, value] pairs to object.
    /// Signature: Dictionary&lt;string, object&gt; ObjectFromEntries(object entries, $TSSymbol iteratorSymbol, Type runtimeType)
    /// </summary>
    private void EmitObjectFromEntries(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectFromEntries",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.DictionaryStringObject,
            [_types.Object, runtime.TSSymbolType, _types.Type]
        );
        runtime.ObjectFromEntries = method;

        var il = method.GetILGenerator();
        var dictType = _types.DictionaryStringObject;
        var listType = _types.ListOfObject;

        // Locals
        var resultLocal = il.DeclareLocal(dictType);
        var iterableLocal = il.DeclareLocal(listType);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var entryLocal = il.DeclareLocal(_types.Object);
        var entryListLocal = il.DeclareLocal(listType);
        var keyLocal = il.DeclareLocal(_types.String);
        var valueLocal = il.DeclareLocal(_types.Object);

        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var notNullLabel = il.DefineLabel();
        var throwLabel = il.DefineLabel();

        // Check for null input
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, notNullLabel);

        // Input is null - throw exception
        il.Emit(OpCodes.Ldstr, "Runtime Error: Object.fromEntries() requires an iterable argument");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        il.MarkLabel(notNullLabel);

        // Convert input to list using IterateToList(entries, iteratorSymbol, runtimeType)
        il.Emit(OpCodes.Ldarg_0);  // entries
        il.Emit(OpCodes.Ldarg_1);  // iteratorSymbol
        il.Emit(OpCodes.Ldarg_2);  // runtimeType
        il.Emit(OpCodes.Call, runtime.IterateToList);
        il.Emit(OpCodes.Stloc, iterableLocal);

        // Create result dictionary
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(dictType));
        il.Emit(OpCodes.Stloc, resultLocal);

        // Initialize loop counter
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        // Loop start
        il.MarkLabel(loopStartLabel);

        // Check if index < iterable.Count
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, iterableLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEndLabel);

        // Get entry = iterable[index]
        il.Emit(OpCodes.Ldloc, iterableLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Item")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, entryLocal);

        // Cast entry to List<object>
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Isinst, listType);
        il.Emit(OpCodes.Stloc, entryListLocal);

        // If not a list, throw
        il.Emit(OpCodes.Ldloc, entryListLocal);
        il.Emit(OpCodes.Brfalse, throwLabel);

        // Check if list has at least 2 elements
        il.Emit(OpCodes.Ldloc, entryListLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Blt, throwLabel);

        // Get key = entryList[0]?.ToString() ?? ""
        il.Emit(OpCodes.Ldloc, entryListLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Item")!.GetGetMethod()!);
        var keyNullLabel = il.DefineLabel();
        var keyDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, keyNullLabel);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Br, keyDoneLabel);
        il.MarkLabel(keyNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(keyDoneLabel);
        il.Emit(OpCodes.Stloc, keyLocal);

        // Get value = entryList[1]
        il.Emit(OpCodes.Ldloc, entryListLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Item")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, valueLocal);

        // result[key] = value
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "set_Item", _types.String, _types.Object));

        // Increment index
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        // Throw error for invalid entry
        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "Runtime Error: Object.fromEntries() requires [key, value] pairs");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        // Return result
        il.MarkLabel(loopEndLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }
}
