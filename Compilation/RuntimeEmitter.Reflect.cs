using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits ReflectSet: (object target, object key, object? value) → bool
    /// Tries to set the property; returns false if frozen/sealed.
    /// </summary>
    private void EmitReflectSet(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ReflectSet",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.ReflectSet = method;

        var il = method.GetILGenerator();
        var resultLocal = il.DeclareLocal(_types.Boolean);

        // Check if target is null
        var notNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, notNullLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notNullLabel);

        // Check if target is frozen
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSIsFrozen);
        var notFrozenLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notFrozenLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notFrozenLabel);

        // try { SetProperty(target, key.ToString(), value); return true; }
        // catch { return false; }
        il.BeginExceptionBlock();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, runtime.SetProperty);

        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, resultLocal);

        il.BeginCatchBlock(_types.Exception);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.EndExceptionBlock();

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ReflectSetPrototypeOf: (object target, object? proto) → bool
    /// Tries to set prototype; returns false if not extensible.
    /// </summary>
    private void EmitReflectSetPrototypeOf(TypeBuilder typeBuilder, EmittedRuntime runtime,
        FieldBuilder prototypeStoreField, FieldBuilder nonExtensibleObjectsField)
    {
        var method = typeBuilder.DefineMethod(
            "ReflectSetPrototypeOf",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.Object]
        );
        runtime.ReflectSetPrototypeOf = method;

        var il = method.GetILGenerator();
        var resultLocal = il.DeclareLocal(_types.Boolean);

        // Check if target is null
        var notNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, notNullLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notNullLabel);

        // Check if not extensible → return false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.ObjectIsExtensible);
        var isExtensibleLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, isExtensibleLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(isExtensibleLabel);

        // try { ObjectSetPrototypeOf(target, proto); return true; }
        // catch { return false; }
        il.BeginExceptionBlock();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ObjectSetPrototypeOf);
        il.Emit(OpCodes.Pop); // ObjectSetPrototypeOf returns the object; discard

        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, resultLocal);

        il.BeginCatchBlock(_types.Exception);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.EndExceptionBlock();

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ReflectDefineProperty: (object target, object key, object descriptor) → bool
    /// Tries to define a property; returns false on failure.
    /// </summary>
    private void EmitReflectDefineProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ReflectDefineProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.ReflectDefineProperty = method;

        var il = method.GetILGenerator();
        var resultLocal = il.DeclareLocal(_types.Boolean);

        // try { ObjectDefineProperty(target, key, descriptor); return true; }
        // catch { return false; }
        il.BeginExceptionBlock();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, runtime.ObjectDefineProperty);
        il.Emit(OpCodes.Pop); // ObjectDefineProperty returns the object; discard

        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, resultLocal);

        il.BeginCatchBlock(_types.Exception);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.EndExceptionBlock();

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ReflectOwnKeys: (object target) → object (List of keys including symbol keys)
    /// </summary>
    private void EmitReflectOwnKeys(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ReflectOwnKeys",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.ReflectOwnKeys = method;

        var il = method.GetILGenerator();
        var resultLocal = il.DeclareLocal(_types.ListOfObject);
        var notNullLabel = il.DefineLabel();

        // Create result list
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.EmptyTypes));
        il.Emit(OpCodes.Stloc, resultLocal);

        // Check if target is null
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, notNullLabel);
        // Return empty list
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notNullLabel);

        // Check if target is Dictionary<string, object?>
        var isDictLabel = il.DefineLabel();
        var notDictLabel = il.DefineLabel();
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brtrue, isDictLabel);
        il.Emit(OpCodes.Br, notDictLabel);

        // Dictionary path: iterate keys
        il.MarkLabel(isDictLabel);
        {
            var enumeratorType = typeof(Dictionary<string, object?>.Enumerator);
            var keyValuePairType = _types.KeyValuePairStringObject;
            var enumeratorLocal = il.DeclareLocal(enumeratorType);
            var loopStart = il.DefineLabel();
            var loopEnd = il.DefineLabel();

            il.Emit(OpCodes.Ldloc, dictLocal);
            il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.DictionaryStringObject, "GetEnumerator"));
            il.Emit(OpCodes.Stloc, enumeratorLocal);

            il.MarkLabel(loopStart);
            il.Emit(OpCodes.Ldloca, enumeratorLocal);
            il.Emit(OpCodes.Call, _types.GetMethodNoParams(enumeratorType, "MoveNext"));
            il.Emit(OpCodes.Brfalse, loopEnd);

            // Get current key and add to result
            il.Emit(OpCodes.Ldloc, resultLocal);
            il.Emit(OpCodes.Ldloca, enumeratorLocal);
            il.Emit(OpCodes.Call, _types.GetProperty(enumeratorType, "Current")!.GetGetMethod()!);
            var kvpLocal = il.DeclareLocal(keyValuePairType);
            il.Emit(OpCodes.Stloc, kvpLocal);
            il.Emit(OpCodes.Ldloca, kvpLocal);
            il.Emit(OpCodes.Call, _types.GetProperty(keyValuePairType, "Key")!.GetGetMethod()!);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));
            il.Emit(OpCodes.Br, loopStart);

            il.MarkLabel(loopEnd);
        }

        // Also get symbol keys via GetOwnPropertySymbols
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetOwnPropertySymbols);
        // GetOwnPropertySymbols returns a List<object?>; add all to result
        var symbolList = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Stloc, symbolList);
        var noSymbolsLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, symbolList);
        il.Emit(OpCodes.Brfalse, noSymbolsLabel);

        // AddRange
        var addRangeMethod = _types.ListOfObject.GetMethod("AddRange");
        if (addRangeMethod != null)
        {
            il.Emit(OpCodes.Ldloc, resultLocal);
            il.Emit(OpCodes.Ldloc, symbolList);
            il.Emit(OpCodes.Callvirt, addRangeMethod);
        }
        il.MarkLabel(noSymbolsLabel);

        // Return result wrapped in list
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        // Non-dict path: use GetKeys for string keys
        il.MarkLabel(notDictLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetKeys);
        // GetKeys returns a List<object?> (array of keys); add all to result
        var keysResult = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Stloc, keysResult);
        var noKeysLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, keysResult);
        il.Emit(OpCodes.Brfalse, noKeysLabel);

        if (addRangeMethod != null)
        {
            il.Emit(OpCodes.Ldloc, resultLocal);
            il.Emit(OpCodes.Ldloc, keysResult);
            il.Emit(OpCodes.Callvirt, addRangeMethod);
        }
        il.MarkLabel(noKeysLabel);

        // Also get symbol keys for non-dict path
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetOwnPropertySymbols);
        var symbolList2 = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Stloc, symbolList2);
        var noSymbols2Label = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, symbolList2);
        il.Emit(OpCodes.Brfalse, noSymbols2Label);

        if (addRangeMethod != null)
        {
            il.Emit(OpCodes.Ldloc, resultLocal);
            il.Emit(OpCodes.Ldloc, symbolList2);
            il.Emit(OpCodes.Callvirt, addRangeMethod);
        }
        il.MarkLabel(noSymbols2Label);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ReflectApply: (object target, object? thisArg, object argsList) → object?
    /// Converts argsList to object[] and invokes target with thisArg.
    /// </summary>
    private void EmitReflectApply(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ReflectApply",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.ReflectApply = method;

        var il = method.GetILGenerator();
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Convert argsList (arg2) to object[]
        // Check if it's a List<object?>
        var isListLabel = il.DefineLabel();
        var gotArgsLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        var listLocal = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Brtrue, isListLabel);

        // Not a list - use empty args
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Stloc, argsLocal);
        il.Emit(OpCodes.Br, gotArgsLabel);

        // Is a list - call ToArray()
        il.MarkLabel(isListLabel);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("ToArray")!);
        il.Emit(OpCodes.Stloc, argsLocal);

        il.MarkLabel(gotArgsLabel);

        // Check if target is TSFunction
        var isTsFunctionLabel = il.DefineLabel();
        var notTsFunctionLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, isTsFunctionLabel);

        // Not a TSFunction - try InvokeValue
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Call, runtime.InvokeValue);
        il.Emit(OpCodes.Ret);

        // Is a TSFunction - use InvokeWithThis
        il.MarkLabel(isTsFunctionLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldarg_1); // thisArg
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvokeWithThis);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ReflectConstruct: (object target, object argsList) → object?
    /// Converts argsList to object[] and invokes target as a constructor.
    /// </summary>
    private void EmitReflectConstruct(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ReflectConstruct",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.ReflectConstruct = method;

        var il = method.GetILGenerator();
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Convert argsList (arg1) to object[]
        var isListLabel = il.DefineLabel();
        var gotArgsLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        var listLocal = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Brtrue, isListLabel);

        // Not a list - use empty args
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Stloc, argsLocal);
        il.Emit(OpCodes.Br, gotArgsLabel);

        // Is a list - call ToArray()
        il.MarkLabel(isListLabel);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("ToArray")!);
        il.Emit(OpCodes.Stloc, argsLocal);

        il.MarkLabel(gotArgsLabel);

        // Just use InvokeValue which handles TSFunction, BoundTSFunction, etc.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Call, runtime.InvokeValue);
        il.Emit(OpCodes.Ret);
    }
}
