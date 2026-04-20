using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    private void EmitGetIndex(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetIndex",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.GetIndex = method;

        var il = method.GetILGenerator();
        var arrayLabel = il.DefineLabel();
        var stringLabel = il.DefineLabel();
        var dictLabel = il.DefineLabel();
        var dictStringKeyLabel = il.DefineLabel();
        var dictNumericKeyLabel = il.DefineLabel();
        var symbolKeyLabel = il.DefineLabel();
        var classInstanceLabel = il.DefineLabel();
        var typedArrayLabel = il.DefineLabel();
        var kvpLabel = il.DefineLabel();
        var nullLabel = il.DefineLabel();

        // null check on obj
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, nullLabel);

        // Proxy check: uses obj.GetType().FullName comparison (no SharpTS.dll dependency)
        var notProxyLabel = il.DefineLabel();
        EmitProxyGetIndexCheck(il, () => il.Emit(OpCodes.Ldarg_0), () => il.Emit(OpCodes.Ldarg_1), notProxyLabel);

        il.MarkLabel(notProxyLabel);

        // Check if index is a symbol first (symbols work on any object type)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.IsSymbolMethod);
        il.Emit(OpCodes.Brtrue, symbolKeyLabel);

        // TypedArray (check before List since TypedArray is more specific)
        // Use type name check to avoid hard dependency on SharpTS.dll
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.IsTypedArrayMethod);
        il.Emit(OpCodes.Brtrue, typedArrayLabel);

        // $Array (wrapper around List<object?>) - check before List
        var tsArrayLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSArrayType);
        il.Emit(OpCodes.Brtrue, tsArrayLabel);

        // Descriptor-driven: check each array backing type
        var listGetLabels = new List<(ArrayElementsDescriptor desc, Label label)>();
        foreach (var desc in ArrayElements.All)
        {
            var label = il.DefineLabel();
            listGetLabels.Add((desc, label));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Isinst, desc.GetListType(_types));
            il.Emit(OpCodes.Brtrue, label);
        }

        // Native .NET Array (e.g., string[] from command line args)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ArrayType);
        il.Emit(OpCodes.Brtrue, arrayLabel);

        // KeyValuePair<object, object> (Map entries when spread into array)
        var kvpType = _types.MakeGenericType(_types.KeyValuePairOpen, _types.Object, _types.Object);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, kvpType);
        il.Emit(OpCodes.Brtrue, kvpLabel);

        // String
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, stringLabel);

        // Dict with string key
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Class instance: check if index is string or numeric, then use GetFieldsProperty
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, classInstanceLabel);

        // Class instance with numeric key: convert to string first
        var classInstanceNumericLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, classInstanceNumericLabel);

        // Fallthrough: return null
        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        // Symbol key handler: use GetSymbolDict(obj).TryGetValue(index, out value)
        il.MarkLabel(symbolKeyLabel);
        var symbolDictLocal = il.DeclareLocal(_types.DictionaryObjectObject);
        var symbolValueLocal = il.DeclareLocal(_types.Object);
        // var symbolDict = GetSymbolDict(obj);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetSymbolDictMethod);
        il.Emit(OpCodes.Stloc, symbolDictLocal);
        // if (symbolDict.TryGetValue(index, out value)) return value; else return undefined;
        il.Emit(OpCodes.Ldloc, symbolDictLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, symbolValueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryObjectObject, "TryGetValue"));
        var symbolFoundLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, symbolFoundLabel);
        // Return undefined for missing symbol properties (JavaScript semantics)
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(symbolFoundLabel);
        il.Emit(OpCodes.Ldloc, symbolValueLocal);
        il.Emit(OpCodes.Ret);

        // TypedArray handler: use helper to get element (avoids hard dependency on SharpTS.dll)
        il.MarkLabel(typedArrayLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToInt32", _types.Object));
        il.Emit(OpCodes.Call, runtime.GetTypedArrayElementMethod);
        il.Emit(OpCodes.Ret);

        // Class instance handler: use GetFieldsProperty(obj, index as string)
        il.MarkLabel(classInstanceLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Call, runtime.GetFieldsProperty);
        il.Emit(OpCodes.Ret);

        // Class instance with numeric key: convert to string, then use GetFieldsProperty
        il.MarkLabel(classInstanceNumericLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Call, runtime.GetFieldsProperty);
        il.Emit(OpCodes.Ret);

        // $Array handler: unwrap to List<object?> elements, then index
        il.MarkLabel(tsArrayLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSArrayType);
        il.Emit(OpCodes.Callvirt, runtime.TSArrayElementsGetter);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToInt32", _types.Object));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", _types.Int32));
        il.Emit(OpCodes.Ret);

        // Descriptor-driven: emit get handler for each backing type.
        // Bounds-checks to match JS array-indexing semantics — out-of-range reads
        // (including negative indices) yield `undefined` rather than an IndexOutOfRangeException.
        // Surfaced by real-package testing: minimatch's `set[set.length - 1]` on an
        // empty array blew up with ArgumentOutOfRangeException before this guard.
        foreach (var (desc, label) in listGetLabels)
        {
            var listType = desc.GetListType(_types);
            il.MarkLabel(label);

            var listLocal = il.DeclareLocal(listType);
            var idxLocal = il.DeclareLocal(_types.Int32);
            var inRangeLabel = il.DefineLabel();
            var oobLabel = il.DefineLabel();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, listType);
            il.Emit(OpCodes.Stloc, listLocal);

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToInt32", _types.Object));
            il.Emit(OpCodes.Stloc, idxLocal);

            // if (idx < 0) goto oob;
            il.Emit(OpCodes.Ldloc, idxLocal);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Blt, oobLabel);
            // if (idx < list.Count) goto inRange;
            il.Emit(OpCodes.Ldloc, idxLocal);
            il.Emit(OpCodes.Ldloc, listLocal);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(listType, "get_Count"));
            il.Emit(OpCodes.Blt, inRangeLabel);

            il.MarkLabel(oobLabel);
            il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(inRangeLabel);
            il.Emit(OpCodes.Ldloc, listLocal);
            il.Emit(OpCodes.Ldloc, idxLocal);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(listType, "get_Item", _types.Int32));
            desc.EmitBoxElement(il, _types);
            il.Emit(OpCodes.Ret);
        }

        // Native .NET Array handler (e.g., string[] from command line args)
        il.MarkLabel(arrayLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ArrayType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToInt32", _types.Object));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ArrayType, "GetValue", _types.Int32));
        il.Emit(OpCodes.Ret);

        // KeyValuePair<object, object> handler (Map entries spread into array)
        // Treats the pair as [key, value] tuple: index 0 = Key, index 1 = Value
        il.MarkLabel(kvpLabel);
        var kvpLocal = il.DeclareLocal(kvpType);
        // Unbox the KeyValuePair
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox_Any, kvpType);
        il.Emit(OpCodes.Stloc, kvpLocal);
        // Convert index to int
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToInt32", _types.Object));
        // Check if index is 0: return Key
        var kvpIndex1Label = il.DefineLabel();
        var kvpReturnNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, kvpIndex1Label); // If not 0, check for 1
        // Index is 0: return Key
        il.Emit(OpCodes.Pop); // Remove the index
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(kvpType, "Key").GetGetMethod()!);
        il.Emit(OpCodes.Ret);
        // Check if index is 1: return Value
        il.MarkLabel(kvpIndex1Label);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Bne_Un, kvpReturnNullLabel); // If not 1, return null
        // Index is 1: return Value
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(kvpType, "Value").GetGetMethod()!);
        il.Emit(OpCodes.Ret);
        // Index is neither 0 nor 1: return null
        il.MarkLabel(kvpReturnNullLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(stringLabel);
        var charLocal = il.DeclareLocal(_types.Char);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToInt32", _types.Object));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "get_Chars", _types.Int32));
        il.Emit(OpCodes.Stloc, charLocal);
        il.Emit(OpCodes.Ldloca, charLocal);
        il.Emit(OpCodes.Call, _types.GetMethodNoParams(_types.Char, "ToString"));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictLabel);
        // Check if index is string
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, dictStringKeyLabel);
        // Check if index is double (numeric key - convert to string)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, dictNumericKeyLabel);
        // Otherwise return null (non-string, non-numeric, non-symbol keys not supported)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        var valueLocal = il.DeclareLocal(_types.Object);

        il.MarkLabel(dictStringKeyLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "TryGetValue"));
        var foundLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, foundLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(foundLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictNumericKeyLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "TryGetValue"));
        var foundNumLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, foundNumLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(foundNumLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Ret);
    }

    private void EmitSetIndex(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SetIndex",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.SetIndex = method;

        var il = method.GetILGenerator();
        var dictLabel = il.DefineLabel();
        var dictStringKeyLabel = il.DefineLabel();
        var dictNumericKeyLabel = il.DefineLabel();
        var symbolKeyLabel = il.DefineLabel();
        var classInstanceLabel = il.DefineLabel();
        var typedArraySetLabel = il.DefineLabel();
        var nullLabel = il.DefineLabel();

        // null check on obj
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, nullLabel);

        // Proxy check: uses obj.GetType().FullName comparison (no SharpTS.dll dependency)
        var notProxyLabel = il.DefineLabel();
        EmitProxySetIndexCheck(il, () => il.Emit(OpCodes.Ldarg_0), () => il.Emit(OpCodes.Ldarg_1), () => il.Emit(OpCodes.Ldarg_2), notProxyLabel);

        il.MarkLabel(notProxyLabel);

        // Check if index is a symbol first (symbols work on any object type)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.IsSymbolMethod);
        il.Emit(OpCodes.Brtrue, symbolKeyLabel);

        // TypedArray (check before List since TypedArray is more specific)
        // Use type name check to avoid hard dependency on SharpTS.dll
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.IsTypedArrayMethod);
        il.Emit(OpCodes.Brtrue, typedArraySetLabel);

        // $Array (wrapper around List<object?>) - check before List
        var tsArraySetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSArrayType);
        il.Emit(OpCodes.Brtrue, tsArraySetLabel);

        // Descriptor-driven: check each array backing type
        var listSetLabels = new List<(ArrayElementsDescriptor desc, Label label)>();
        foreach (var desc in ArrayElements.All)
        {
            var label = il.DefineLabel();
            listSetLabels.Add((desc, label));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Isinst, desc.GetListType(_types));
            il.Emit(OpCodes.Brtrue, label);
        }

        // Dict
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Class instance: check if index is string, then use SetFieldsProperty
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, classInstanceLabel);

        // Fallthrough: return (ignore)
        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ret);

        // Symbol key handler: GetSymbolDict(obj)[index] = value
        il.MarkLabel(symbolKeyLabel);
        // GetSymbolDict(obj)[index] = value
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetSymbolDictMethod);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryObjectObject, "set_Item"));
        il.Emit(OpCodes.Ret);

        // TypedArray handler: use helper to set element (avoids hard dependency on SharpTS.dll)
        il.MarkLabel(typedArraySetLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToInt32", _types.Object));
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, runtime.SetTypedArrayElementMethod);
        il.Emit(OpCodes.Ret);

        // Class instance handler: use SetFieldsProperty(obj, index as string, value)
        il.MarkLabel(classInstanceLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, runtime.SetFieldsProperty);
        il.Emit(OpCodes.Ret);

        // $Array handler: unwrap to List<object?> elements, then set
        il.MarkLabel(tsArraySetLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSArrayType);
        il.Emit(OpCodes.Callvirt, runtime.TSArrayElementsGetter);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToInt32", _types.Object));
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "set_Item", _types.Int32, _types.Object));
        il.Emit(OpCodes.Ret);

        // Descriptor-driven: emit set handler for each backing type
        foreach (var (desc, label) in listSetLabels)
        {
            var listType = desc.GetListType(_types);
            il.MarkLabel(label);

            if (desc.Kind == ArrayElementsKind.Object)
            {
                // Object list has frozen check before mutation
                var listFrozenCheckLocal = il.DeclareLocal(_types.Object);
                il.Emit(OpCodes.Ldsfld, runtime.FrozenObjectsField);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloca, listFrozenCheckLocal);
                il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
                il.Emit(OpCodes.Brtrue, nullLabel); // Frozen - silently return
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, listType);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToInt32", _types.Object));
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Callvirt, _types.GetMethod(listType, "set_Item", _types.Int32, _types.Object));
            }
            else
            {
                // Typed list: cast, convert index, convert value to element type, use SetArrayElement helper
                var convertMethod = desc.Kind == ArrayElementsKind.Double
                    ? _types.GetMethod(_types.Convert, "ToDouble", _types.Object)
                    : _types.GetMethod(_types.Convert, "ToBoolean", _types.Object);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, listType);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToInt32", _types.Object));
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, convertMethod);
                il.Emit(OpCodes.Call, desc.GetSetArrayElementMethod(runtime));
            }
            il.Emit(OpCodes.Ret);
        }

        il.MarkLabel(dictLabel);
        // Check if index is string
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, dictStringKeyLabel);
        // Check if index is double (numeric key - convert to string)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, dictNumericKeyLabel);
        // Otherwise ignore (non-string, non-numeric, non-symbol keys not supported)
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictStringKeyLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictNumericKeyLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits DeleteIndex(object obj, object key) -> bool
    /// Handles both symbol keys and string keys for delete operations.
    /// </summary>
    private void EmitDeleteIndex(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DeleteIndex",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.Object]
        );
        runtime.DeleteIndex = method;

        var il = method.GetILGenerator();
        var dictLabel = il.DefineLabel();
        var dictStringKeyLabel = il.DefineLabel();
        var dictNumericKeyLabel = il.DefineLabel();
        var symbolKeyLabel = il.DefineLabel();
        var trueLabel = il.DefineLabel();

        // null check on obj - return true (deleting from null is allowed in JS)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, trueLabel);

        // Check if index is a symbol first
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.IsSymbolMethod);
        il.Emit(OpCodes.Brtrue, symbolKeyLabel);

        // Dict<string, object>
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Other types (arrays, strings, etc.) - cannot delete, return true
        il.Emit(OpCodes.Br, trueLabel);

        // Symbol key handler: GetSymbolDict(obj).Remove(key)
        il.MarkLabel(symbolKeyLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetSymbolDictMethod);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryObjectObject, "Remove", _types.Object));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictLabel);
        // Check if frozen
        var valueLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldsfld, runtime.FrozenObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        var notFrozenLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notFrozenLabel);
        // Frozen - return false
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        // Check if sealed
        il.MarkLabel(notFrozenLabel);
        il.Emit(OpCodes.Ldsfld, runtime.SealedObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        var notSealedLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notSealedLabel);
        // Sealed - return false
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        // Check if index is string
        il.MarkLabel(notSealedLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, dictStringKeyLabel);
        // Check if index is double (numeric key - convert to string)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, dictNumericKeyLabel);
        // Other key types - return true
        il.Emit(OpCodes.Br, trueLabel);

        il.MarkLabel(dictStringKeyLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "Remove", _types.String));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictNumericKeyLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "Remove", _types.String));
        il.Emit(OpCodes.Ret);

        // Return true (default)
        il.MarkLabel(trueLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits DeleteIndexStrict(object obj, object key, bool strictMode) -> bool
    /// In strict mode, throws TypeError for frozen/sealed objects.
    /// Handles both symbol keys and string keys for delete operations.
    /// </summary>
    private void EmitDeleteIndexStrict(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DeleteIndexStrict",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.Object, _types.Boolean]
        );
        runtime.DeleteIndexStrict = method;

        var il = method.GetILGenerator();
        var dictLabel = il.DefineLabel();
        var dictStringKeyLabel = il.DefineLabel();
        var dictNumericKeyLabel = il.DefineLabel();
        var symbolKeyLabel = il.DefineLabel();
        var trueLabel = il.DefineLabel();

        // null check on obj - return true (deleting from null is allowed in JS)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, trueLabel);

        // Check if index is a symbol first
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.IsSymbolMethod);
        il.Emit(OpCodes.Brtrue, symbolKeyLabel);

        // Dict<string, object>
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Other types (arrays, strings, etc.) - cannot delete, return true
        il.Emit(OpCodes.Br, trueLabel);

        // Symbol key handler: GetSymbolDict(obj).Remove(key)
        il.MarkLabel(symbolKeyLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetSymbolDictMethod);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryObjectObject, "Remove", _types.Object));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictLabel);
        // Check if frozen
        var valueLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldsfld, runtime.FrozenObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        var notFrozenLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notFrozenLabel);

        // Frozen - check strict mode
        var frozenSloppyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brfalse, frozenSloppyLabel);

        // Frozen + strict - throw TypeError
        il.Emit(OpCodes.Ldstr, "TypeError: Cannot delete property of a frozen object");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        // Frozen + sloppy - return false
        il.MarkLabel(frozenSloppyLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        // Check if sealed
        il.MarkLabel(notFrozenLabel);
        il.Emit(OpCodes.Ldsfld, runtime.SealedObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        var notSealedLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notSealedLabel);

        // Sealed - check strict mode
        var sealedSloppyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brfalse, sealedSloppyLabel);

        // Sealed + strict - throw TypeError
        il.Emit(OpCodes.Ldstr, "TypeError: Cannot delete property of a sealed object");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        // Sealed + sloppy - return false
        il.MarkLabel(sealedSloppyLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        // Check if index is string
        il.MarkLabel(notSealedLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, dictStringKeyLabel);
        // Check if index is double (numeric key - convert to string)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, dictNumericKeyLabel);
        // Other key types - return true
        il.Emit(OpCodes.Br, trueLabel);

        il.MarkLabel(dictStringKeyLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "Remove", _types.String));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictNumericKeyLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "Remove", _types.String));
        il.Emit(OpCodes.Ret);

        // Return true (default)
        il.MarkLabel(trueLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }
}

