using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits Object.defineProperty(obj, prop, descriptor) - defines or modifies a property.
    /// Signature: object ObjectDefineProperty(object obj, object prop, object descriptor)
    /// Creates a $CompiledPropertyDescriptor and registers it in the emitted $PropertyDescriptorStore.
    /// </summary>
    private void EmitObjectDefineProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectDefineProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.ObjectDefineProperty = method;

        var il = method.GetILGenerator();

        // Emit standalone property descriptor creation and registration
        // This avoids any runtime dependency on SharpTS.dll

        var descriptorLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var propNameLocal = il.DeclareLocal(_types.String);
        var valueLocal = il.DeclareLocal(_types.Object);
        var notDictLabel = il.DefineLabel();
        var setDescriptorDoneLabel = il.DefineLabel();

        // Symbol-keyed path: Object.defineProperty(obj, symbol, {value:X}) must store X
        // in the object's symbol dict so `obj[symbol]` can retrieve it. Without this,
        // a later `obj[symbol]` read routes through EmitGetIndex's symbol-key handler
        // which only consults the symbol dict (not the string-keyed PDS), and the
        // value is silently invisible. yaml's Schema constructor depends on this for
        // schema[SCALAR] = strTag.
        var notSymbolLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.IsSymbolMethod);
        il.Emit(OpCodes.Brfalse, notSymbolLabel);

        // Extract value from descriptor dictionary (if it's a dict)
        var symbolValueLocal = il.DeclareLocal(_types.Object);
        var symbolDescDictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var symbolDictWriteLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Stloc, symbolValueLocal);

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, symbolDescDictLocal);
        il.Emit(OpCodes.Ldloc, symbolDescDictLocal);
        il.Emit(OpCodes.Brfalse, symbolDictWriteLabel);
        il.Emit(OpCodes.Ldloc, symbolDescDictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloca, symbolValueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "TryGetValue", _types.String, _types.Object.MakeByRefType()));
        il.Emit(OpCodes.Pop);

        il.MarkLabel(symbolDictWriteLabel);
        // GetSymbolDict(obj)[symbol] = value
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetSymbolDictMethod);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, symbolValueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryObjectObject, "set_Item", _types.Object, _types.Object));
        // Return the target object
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notSymbolLabel);

        // propName = prop.ToString()
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
        il.Emit(OpCodes.Stloc, propNameLocal);

        // Check if object is frozen - if so, throw TypeError
        var notFrozenLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSIsFrozen);
        il.Emit(OpCodes.Brfalse, notFrozenLabel);

        // Throw TypeError: Cannot define property on frozen object
        il.Emit(OpCodes.Ldstr, "Cannot define property: object is not extensible");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);  // Wrap in .NET exception
        il.Emit(OpCodes.Throw);

        il.MarkLabel(notFrozenLabel);

        // Check if object is sealed and property doesn't exist
        var notSealedLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSIsSealed);
        il.Emit(OpCodes.Brfalse, notSealedLabel);

        // Object is sealed - check if property already exists (can modify existing)
        var canAddLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Call, runtime.PDSCanAddProperty);
        il.Emit(OpCodes.Brtrue, canAddLabel);

        // Can't add - throw TypeError
        il.Emit(OpCodes.Ldstr, "Cannot define property: object is not extensible");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);  // Wrap in .NET exception
        il.Emit(OpCodes.Throw);

        il.MarkLabel(canAddLabel);
        il.MarkLabel(notSealedLabel);

        // Create new $CompiledPropertyDescriptor
        il.Emit(OpCodes.Newobj, runtime.CompiledPropertyDescriptorCtor);
        il.Emit(OpCodes.Stloc, descriptorLocal);

        // ECMA-262 6.2.5.1 CompletePropertyDescriptor: when Object.defineProperty receives
        // a partial descriptor, unspecified writable/enumerable/configurable default to FALSE.
        // The ctor sets them to true (used by CreateDataProperty for `obj.foo = X`);
        // we reset them here to match the spec for the defineProperty path.
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorWritable.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorEnumerable.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorConfigurable.GetSetMethod()!);

        // Check if descriptor is Dictionary<string, object?>
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, setDescriptorDoneLabel);

        // Extract properties from descriptor dictionary
        var dictTryGetValue = _types.GetMethod(_types.DictionaryStringObject, "TryGetValue", _types.String, _types.Object.MakeByRefType());

        // Try to get "value" property
        var noValueLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noValueLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetSetMethod()!);
        il.MarkLabel(noValueLabel);

        // Try to get "writable" property
        var noWritableLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noWritableLabel);
        // Convert to bool and set
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Call, runtime.IsTruthy);  // Convert to bool
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorWritable.GetSetMethod()!);
        il.MarkLabel(noWritableLabel);

        // Try to get "get" property (getter)
        var noGetterLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "get");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noGetterLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorGetter.GetSetMethod()!);
        il.MarkLabel(noGetterLabel);

        // Try to get "set" property (setter)
        var noSetterLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "set");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noSetterLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorSetter.GetSetMethod()!);
        il.MarkLabel(noSetterLabel);

        // Try to get "enumerable" property
        var noEnumerableLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "enumerable");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noEnumerableLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Call, runtime.IsTruthy);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorEnumerable.GetSetMethod()!);
        il.MarkLabel(noEnumerableLabel);

        // Try to get "configurable" property
        var noConfigurableLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "configurable");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noConfigurableLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Call, runtime.IsTruthy);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorConfigurable.GetSetMethod()!);
        il.MarkLabel(noConfigurableLabel);

        il.MarkLabel(setDescriptorDoneLabel);

        // Call $PropertyDescriptorStore.DefineProperty(obj, propName, descriptor)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Call, runtime.PDSDefineProperty);
        il.Emit(OpCodes.Pop);  // Discard bool result

        // Also set the value on the object if it's a data property (has value, not getter)
        // if (descriptor has "value" && obj is Dictionary)
        var skipValueSetLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // Check if descriptor has a value (not an accessor property)
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, skipValueSetLabel);

        // Check if getter is set (accessor property - don't set value directly)
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorGetter.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, skipValueSetLabel);

        // Set value on object if it's a dictionary
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, skipValueSetLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(skipValueSetLabel);

        il.MarkLabel(endLabel);
        // Return the object
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.getOwnPropertyDescriptor(obj, prop) - gets a property descriptor.
    /// Signature: object ObjectGetOwnPropertyDescriptor(object obj, object prop)
    /// Returns a JavaScript object with descriptor properties.
    /// </summary>
    private void EmitObjectGetOwnPropertyDescriptor(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectGetOwnPropertyDescriptor",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.ObjectGetOwnPropertyDescriptor = method;

        var il = method.GetILGenerator();

        var propNameLocal = il.DeclareLocal(_types.String);
        var descriptorLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
        var resultDictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var valueLocal = il.DeclareLocal(_types.Object);
        var returnNullLabel = il.DefineLabel();
        var checkObjPropertyLabel = il.DefineLabel();
        var hasDescriptorLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // propName = prop.ToString()
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
        il.Emit(OpCodes.Stloc, propNameLocal);

        // Try to get descriptor from $PropertyDescriptorStore
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Call, runtime.PDSGetPropertyDescriptor);
        il.Emit(OpCodes.Stloc, descriptorLocal);

        // If descriptor is not null, convert it to a JS object
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Brtrue, hasDescriptorLabel);

        // No descriptor - check if it's an array first
        var notListLabel = il.DefineLabel();
        var notTSArrayLabel = il.DefineLabel();
        var isListLabel = il.DefineLabel();
        var handleArrayLabel = il.DefineLabel();
        var listLocal = il.DeclareLocal(_types.ListOfObject);
        var indexLocal = il.DeclareLocal(_types.Int32);

        // Check for List<object?>
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Brtrue, isListLabel);

        // Check for $Array (SharpTSArray)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSArrayType);
        il.Emit(OpCodes.Brfalse, notTSArrayLabel);

        // It's $Array - get Elements list
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSArrayType);
        il.Emit(OpCodes.Callvirt, runtime.TSArrayElementsGetter);
        il.Emit(OpCodes.Stloc, listLocal);
        il.Emit(OpCodes.Br, handleArrayLabel);

        il.MarkLabel(isListLabel);
        // listLocal already has the list

        il.MarkLabel(handleArrayLabel);
        // Handle array property - check if propName is "length" or numeric index

        // Check for "length" property
        var notLengthLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notLengthLabel);

        // Return length descriptor: { value: length, writable: true, enumerable: false, configurable: false }
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultDictLocal);

        // value = list.Count
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // writable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // enumerable = false
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "enumerable");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // configurable = false
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "configurable");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(notLengthLabel);
        // Check if it's a numeric index
        var notNumericIndexLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Ldloca, indexLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Int32, "TryParse", _types.String, _types.Int32.MakeByRefType()));
        il.Emit(OpCodes.Brfalse, notNumericIndexLabel);

        // Check if index is in bounds
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, returnNullLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, returnNullLabel);

        // Return element descriptor: { value: element, writable: true, enumerable: true, configurable: true }
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultDictLocal);

        // value = list[index]
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", _types.Int32));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // writable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // enumerable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "enumerable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // configurable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "configurable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(notNumericIndexLabel);
        // Not length or numeric index on array - return null
        il.Emit(OpCodes.Br, returnNullLabel);

        il.MarkLabel(notTSArrayLabel);

        // No descriptor - check if property exists on the object directly (Dictionary case)
        var notDictLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, notDictLabel);

        // Check if dictionary contains the key
        var dictContainsKeyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "ContainsKey", _types.String));
        il.Emit(OpCodes.Brfalse, returnNullLabel);

        // Property exists on dict - create default data descriptor
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultDictLocal);

        // Get the value
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "get_Item", _types.String));
        il.Emit(OpCodes.Stloc, valueLocal);

        // Set value property
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // Set writable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // Set enumerable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "enumerable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // Set configurable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "configurable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Br, endLabel);

        // Not a dictionary - check if it implements $IHasFields (class instances)
        il.MarkLabel(notDictLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Brfalse, returnNullLabel);

        // Get the fields dictionary from the class instance
        var fieldsLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Callvirt, runtime.IHasFieldsFieldsGetter);
        il.Emit(OpCodes.Stloc, fieldsLocal);

        // Check if the fields dictionary contains the key
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Brfalse, returnNullLabel);
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "ContainsKey", _types.String));
        il.Emit(OpCodes.Brfalse, returnNullLabel);

        // Build data descriptor from the class field value
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultDictLocal);

        // Get the value from the fields dictionary
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "get_Item", _types.String));
        il.Emit(OpCodes.Stloc, valueLocal);

        // Set value
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // Set writable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // Set enumerable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "enumerable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // Set configurable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "configurable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Br, endLabel);

        // hasDescriptorLabel: Convert $CompiledPropertyDescriptor to JS object
        il.MarkLabel(hasDescriptorLabel);
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultDictLocal);

        // Check if it's an accessor property (has getter or setter)
        var isAccessorLabel = il.DefineLabel();
        var isDataLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorGetter.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, isAccessorLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorSetter.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, isAccessorLabel);
        il.Emit(OpCodes.Br, isDataLabel);

        // Accessor property - set get and set
        il.MarkLabel(isAccessorLabel);

        // Set get property if not null
        var noGetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorGetter.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, noGetLabel);
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "get");
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorGetter.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        il.MarkLabel(noGetLabel);

        // Set set property if not null
        var noSetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorSetter.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, noSetLabel);
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "set");
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorSetter.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        il.MarkLabel(noSetLabel);

        var afterAccessorLabel = il.DefineLabel();
        il.Emit(OpCodes.Br, afterAccessorLabel);

        // Data property - set value and writable
        il.MarkLabel(isDataLabel);

        // Set value
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // Set writable
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorWritable.GetGetMethod()!);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        il.MarkLabel(afterAccessorLabel);

        // Set enumerable
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "enumerable");
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorEnumerable.GetGetMethod()!);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // Set configurable
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "configurable");
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorConfigurable.GetGetMethod()!);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Br, endLabel);

        // returnNullLabel: return undefined
        il.MarkLabel(returnNullLabel);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.defineProperties(obj, props) - defines multiple properties.
    /// Signature: object ObjectDefineProperties(object obj, object props)
    /// Iterates over keys of props dictionary and calls ObjectDefineProperty for each.
    /// </summary>
    private void EmitObjectDefineProperties(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectDefineProperties",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.ObjectDefineProperties = method;

        var il = method.GetILGenerator();

        // Cast props to Dictionary<string, object?>
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var enumeratorLocal = il.DeclareLocal(typeof(Dictionary<string, object?>.Enumerator));
        var currentLocal = il.DeclareLocal(typeof(KeyValuePair<string, object?>));

        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();

        // dict = props as Dictionary<string, object?>
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        // If not a dictionary, just return obj
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, loopEndLabel);

        // Get enumerator
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "GetEnumerator"));
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        // Loop
        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        var moveNext = typeof(Dictionary<string, object?>.Enumerator).GetMethod("MoveNext")!;
        il.Emit(OpCodes.Call, moveNext);
        il.Emit(OpCodes.Brfalse, loopEndLabel);

        // Get current KVP
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        var currentProp = typeof(Dictionary<string, object?>.Enumerator).GetProperty("Current")!.GetGetMethod()!;
        il.Emit(OpCodes.Call, currentProp);
        il.Emit(OpCodes.Stloc, currentLocal);

        // Call ObjectDefineProperty(obj, key, descriptor)
        il.Emit(OpCodes.Ldarg_0);  // obj
        il.Emit(OpCodes.Ldloca, currentLocal);
        var keyGetter = typeof(KeyValuePair<string, object?>).GetProperty("Key")!.GetGetMethod()!;
        il.Emit(OpCodes.Call, keyGetter);
        il.Emit(OpCodes.Ldloca, currentLocal);
        var valueGetter = typeof(KeyValuePair<string, object?>).GetProperty("Value")!.GetGetMethod()!;
        il.Emit(OpCodes.Call, valueGetter);
        il.Emit(OpCodes.Call, runtime.ObjectDefineProperty);
        il.Emit(OpCodes.Pop);  // Discard return value from defineProperty

        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);
        // Return obj
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.getOwnPropertyDescriptors(obj) - gets all own property descriptors.
    /// Signature: object ObjectGetOwnPropertyDescriptors(object obj)
    /// Iterates over keys and calls ObjectGetOwnPropertyDescriptor for each, collecting into a new dict.
    /// </summary>
    private void EmitObjectGetOwnPropertyDescriptors(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectGetOwnPropertyDescriptors",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.ObjectGetOwnPropertyDescriptors = method;

        var il = method.GetILGenerator();

        var resultLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var keysLocal = il.DeclareLocal(_types.ListOfObject);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var keyLocal = il.DeclareLocal(_types.String);
        var descLocal = il.DeclareLocal(_types.Object);

        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var skipNullLabel = il.DefineLabel();

        // result = new Dictionary<string, object?>()
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultLocal);

        // Get keys using the existing GetKeys helper (returns List<object?>)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetKeys);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Stloc, keysLocal);

        // If keys is null, return empty result
        il.Emit(OpCodes.Ldloc, keysLocal);
        il.Emit(OpCodes.Brfalse, loopEndLabel);

        // index = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        // Loop
        il.MarkLabel(loopStartLabel);
        // if (index >= keys.Count) break
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, keysLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEndLabel);

        // key = keys[index].ToString()
        il.Emit(OpCodes.Ldloc, keysLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", _types.Int32));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
        il.Emit(OpCodes.Stloc, keyLocal);

        // desc = ObjectGetOwnPropertyDescriptor(obj, key)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Call, runtime.ObjectGetOwnPropertyDescriptor);
        il.Emit(OpCodes.Stloc, descLocal);

        // if (desc == null || desc is undefined) skip
        il.Emit(OpCodes.Ldloc, descLocal);
        il.Emit(OpCodes.Brfalse, skipNullLabel);
        il.Emit(OpCodes.Ldloc, descLocal);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Beq, skipNullLabel);

        // result[key] = desc
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Ldloc, descLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        il.MarkLabel(skipNullLabel);
        // index++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }
}
