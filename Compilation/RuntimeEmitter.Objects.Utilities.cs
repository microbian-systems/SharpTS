using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    private void EmitCreateObject(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CreateObject",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.DictionaryStringObject,
            [_types.DictionaryStringObject]
        );
        runtime.CreateObject = method;

        var il = method.GetILGenerator();
        // Just return the dictionary as-is (it's already created)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitMergeIntoObject(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "MergeIntoObject",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.DictionaryStringObject, _types.Object]
        );
        runtime.MergeIntoObject = method;

        var il = method.GetILGenerator();
        var dictLabel = il.DefineLabel();

        // Check if source is dict
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Not a dict - do nothing
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictLabel);
        // Iterate and copy
        // We need the Enumerator type for Dictionary<string, object>
        // Since TypeProvider might not expose nested types directly, we resolve it from the Dictionary type
        var dictType = _types.DictionaryStringObject;
        var enumeratorType = typeof(Dictionary<string, object>.Enumerator);
        var keyValuePairType = _types.KeyValuePairStringObject;

        var enumeratorLocal = il.DeclareLocal(enumeratorType);
        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, dictType);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(dictType, "GetEnumerator"));
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, _types.GetMethodNoParams(enumeratorType, "MoveNext"));
        il.Emit(OpCodes.Brfalse, loopEnd);

        // Get current and add to target
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(enumeratorType, "Current")!.GetGetMethod()!);
        var kvpLocal = il.DeclareLocal(keyValuePairType);
        il.Emit(OpCodes.Stloc, kvpLocal);
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(keyValuePairType, "Key")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(keyValuePairType, "Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "set_Item", _types.String, _types.Object));

        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ret);
    }

    private void EmitMergeIntoTSObject(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public static void MergeIntoTSObject($Object target, object? source)
        // Merges properties from source (Dictionary or $Object) into target $Object
        var method = typeBuilder.DefineMethod(
            "MergeIntoTSObject",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [runtime.TSObjectType, _types.Object]
        );
        runtime.MergeIntoTSObject = method;

        var il = method.GetILGenerator();
        var dictLabel = il.DefineLabel();
        var tsObjectLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // Check if source is Dictionary<string, object?>
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Check if source is $IHasFields (covers $Object and class instances)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Brtrue, tsObjectLabel);

        // Not a dict or $IHasFields - do nothing
        il.Emit(OpCodes.Ret);

        // Handle Dictionary source
        il.MarkLabel(dictLabel);
        {
            var dictType = _types.DictionaryStringObject;
            var enumeratorType = typeof(Dictionary<string, object>.Enumerator);
            var keyValuePairType = _types.KeyValuePairStringObject;

            var enumeratorLocal = il.DeclareLocal(enumeratorType);
            var kvpLocal = il.DeclareLocal(keyValuePairType);
            var loopStart = il.DefineLabel();
            var loopEnd = il.DefineLabel();

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, dictType);
            il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(dictType, "GetEnumerator"));
            il.Emit(OpCodes.Stloc, enumeratorLocal);

            il.MarkLabel(loopStart);
            il.Emit(OpCodes.Ldloca, enumeratorLocal);
            il.Emit(OpCodes.Call, _types.GetMethodNoParams(enumeratorType, "MoveNext"));
            il.Emit(OpCodes.Brfalse, loopEnd);

            // target.SetProperty(key, value)
            il.Emit(OpCodes.Ldarg_0); // target
            il.Emit(OpCodes.Ldloca, enumeratorLocal);
            il.Emit(OpCodes.Call, _types.GetProperty(enumeratorType, "Current")!.GetGetMethod()!);
            il.Emit(OpCodes.Stloc, kvpLocal);
            il.Emit(OpCodes.Ldloca, kvpLocal);
            il.Emit(OpCodes.Call, _types.GetProperty(keyValuePairType, "Key")!.GetGetMethod()!);
            il.Emit(OpCodes.Ldloca, kvpLocal);
            il.Emit(OpCodes.Call, _types.GetProperty(keyValuePairType, "Value")!.GetGetMethod()!);
            il.Emit(OpCodes.Callvirt, runtime.TSObjectSetProperty);

            il.Emit(OpCodes.Br, loopStart);

            il.MarkLabel(loopEnd);
            il.Emit(OpCodes.Br, endLabel);
        }

        // Handle $IHasFields source - iterate Fields dictionary
        il.MarkLabel(tsObjectLabel);
        {
            var fieldsDictLocal = il.DeclareLocal(_types.DictionaryStringObject);
            var dictEnumType = typeof(Dictionary<string, object>.Enumerator);
            var kvpType = _types.KeyValuePairStringObject;
            var enumLocal = il.DeclareLocal(dictEnumType);
            var kvpLocal = il.DeclareLocal(kvpType);
            var loopStart = il.DefineLabel();
            var loopEnd = il.DefineLabel();

            // Get Fields dictionary from source
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, runtime.IHasFieldsInterface);
            il.Emit(OpCodes.Callvirt, runtime.IHasFieldsFieldsGetter);
            il.Emit(OpCodes.Stloc, fieldsDictLocal);

            // If null, skip
            il.Emit(OpCodes.Ldloc, fieldsDictLocal);
            il.Emit(OpCodes.Brfalse, endLabel);

            // Iterate dictionary
            il.Emit(OpCodes.Ldloc, fieldsDictLocal);
            il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.DictionaryStringObject, "GetEnumerator"));
            il.Emit(OpCodes.Stloc, enumLocal);

            il.MarkLabel(loopStart);
            il.Emit(OpCodes.Ldloca, enumLocal);
            il.Emit(OpCodes.Call, dictEnumType.GetMethod("MoveNext")!);
            il.Emit(OpCodes.Brfalse, loopEnd);

            il.Emit(OpCodes.Ldloca, enumLocal);
            il.Emit(OpCodes.Call, dictEnumType.GetProperty("Current")!.GetGetMethod()!);
            il.Emit(OpCodes.Stloc, kvpLocal);

            // target.SetProperty(key, value)
            il.Emit(OpCodes.Ldarg_0); // target
            il.Emit(OpCodes.Ldloca, kvpLocal);
            il.Emit(OpCodes.Call, kvpType.GetProperty("Key")!.GetGetMethod()!);
            il.Emit(OpCodes.Ldloca, kvpLocal);
            il.Emit(OpCodes.Call, kvpType.GetProperty("Value")!.GetGetMethod()!);
            il.Emit(OpCodes.Callvirt, runtime.TSObjectSetProperty);

            il.Emit(OpCodes.Br, loopStart);

            il.MarkLabel(loopEnd);
        }

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    private void EmitRandom(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder randomField)
    {
        var method = typeBuilder.DefineMethod(
            "Random",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            _types.EmptyTypes
        );
        runtime.Random = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, randomField);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Random, "NextDouble"));
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetEnumMemberName(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetEnumMemberName",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String, _types.Double, _types.DoubleArray, _types.StringArray]
        );
        runtime.GetEnumMemberName = method;

        var il = method.GetILGenerator();
        // Simple linear search through keys to find matching value
        var indexLocal = il.DeclareLocal(_types.Int32);
        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, loopEnd);

        // Check if keys[i] == value
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_R8);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ceq);
        var notMatchLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notMatchLabel);

        // Found - return values[i]
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notMatchLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        // Not found - throw
        il.Emit(OpCodes.Ldstr, "Value not found in enum");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);
    }
}
