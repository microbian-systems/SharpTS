using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Helper: emits IL to set a boolean descriptor field
    /// (writable/enumerable/configurable) on the result dict to a constant
    /// value. Reduces 6 lines of boilerplate to one call at each site.
    /// </summary>
    private void EmitDescriptorBoolField(ILGenerator il, LocalBuilder resultDictLocal, string fieldName, bool value)
    {
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, fieldName);
        il.Emit(value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
    }

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

        // ECMA-262 §20.1.2.4 step 1: If Type(O) is not Object, throw TypeError.
        // Covers null/undefined/primitives. test262 15.2.3.6-{1-*}.js verify.
        var primitiveThrowLabel = il.DefineLabel();
        var skipTypeThrowLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, primitiveThrowLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, primitiveThrowLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, primitiveThrowLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Int32);
        il.Emit(OpCodes.Brtrue, primitiveThrowLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brtrue, primitiveThrowLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, primitiveThrowLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSSymbolType);
        il.Emit(OpCodes.Brtrue, primitiveThrowLabel);
        il.Emit(OpCodes.Br, skipTypeThrowLabel);

        il.MarkLabel(primitiveThrowLabel);
        il.Emit(OpCodes.Ldstr, "Object.defineProperty called on non-object");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(skipTypeThrowLabel);

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

        // propName = $Runtime.ToJsString(prop) — ECMA-262 §7.1.19 ToPropertyKey
        // string path via the spec-shaped ToString. Avoids the prop.ToString()
        // Callvirt-on-null NRE for `Object.defineProperty(obj, null, ...)`,
        // and unlike runtime.Stringify (which produces debug "[1, 2]" form for
        // arrays) honors `Array.prototype.toString` join semantics so
        // `defineProperty(obj, [1], ...)` lands at key "1" (matches V8/SM).
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ToJsString);
        il.Emit(OpCodes.Stloc, propNameLocal);

        // ECMA-262 10.4.2.4 ArraySetLength: validate that ToUint32(newLen) ===
        // ToNumber(newLen) — i.e. an integer in [0, 2^32). Without this,
        // \`Object.defineProperty([], 'length', {value: -1})\` silently stores
        // -1 in the underlying dict instead of throwing RangeError.
        // Only fires for List<object> receivers (compiled-mode arrays) with
        // propName == "length" and a value-typed descriptor.
        var skipArrayLenCheck = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brfalse, skipArrayLenCheck);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, skipArrayLenCheck);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, skipArrayLenCheck);
        var lenValLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloca, lenValLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "TryGetValue", _types.String, _types.Object.MakeByRefType()));
        il.Emit(OpCodes.Brfalse, skipArrayLenCheck);
        // Coerce value via ToNumber — captures NaN, Infinity, non-numeric.
        var lenNumLocal = il.DeclareLocal(_types.Double);
        il.Emit(OpCodes.Ldloc, lenValLocal);
        il.Emit(OpCodes.Call, runtime.ToNumber);
        il.Emit(OpCodes.Stloc, lenNumLocal);
        // RangeError if NaN, Infinity, negative, non-integer, or >= 2^32.
        il.Emit(OpCodes.Ldloc, lenNumLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Double, "IsNaN", _types.Double));
        var rangeErrLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, rangeErrLabel);
        il.Emit(OpCodes.Ldloc, lenNumLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Double, "IsInfinity", _types.Double));
        il.Emit(OpCodes.Brtrue, rangeErrLabel);
        il.Emit(OpCodes.Ldloc, lenNumLocal);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Blt, rangeErrLabel);
        il.Emit(OpCodes.Ldloc, lenNumLocal);
        il.Emit(OpCodes.Ldc_R8, 4294967296.0);
        il.Emit(OpCodes.Bge, rangeErrLabel);
        // Non-integer: floor(x) != x.
        il.Emit(OpCodes.Ldloc, lenNumLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Math, "Floor", _types.Double));
        il.Emit(OpCodes.Ldloc, lenNumLocal);
        il.Emit(OpCodes.Bne_Un, rangeErrLabel);
        il.Emit(OpCodes.Br, skipArrayLenCheck);
        il.MarkLabel(rangeErrLabel);
        il.Emit(OpCodes.Ldstr, "Invalid array length");
        il.Emit(OpCodes.Newobj, runtime.TSRangeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(skipArrayLenCheck);

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

        // ECMA-262 §10.1.6.3 [[DefineOwnProperty]]: throw TypeError when
        // adding a new property to a non-extensible object. \`PDSCanAddProperty\`
        // returns true when the object IS extensible OR the property already
        // exists (modify-in-place is always allowed). Sealed/frozen objects
        // are also non-extensible, so this single gate covers all three.
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

        // Try to get "get" property (getter). ECMA-262 §6.2.5.5 step 7:
        // if "get" is present and not callable and not undefined → throw TypeError.
        // For undefined, we store $Undefined.Instance in the slot so the
        // descriptor classifier (slot non-null = accessor) still treats this
        // as an accessor descriptor (verifyProperty expects `desc.get === undefined`).
        var noGetterLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "get");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noGetterLabel);
        var getterStoreLabel = il.DefineLabel();
        var getterIsUndefLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Brfalse, getterIsUndefLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, getterIsUndefLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, getterStoreLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, runtime.BoundAnyFunctionType);
        il.Emit(OpCodes.Brtrue, getterStoreLabel);
        il.Emit(OpCodes.Ldstr, "Property descriptor 'get' is not callable");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(getterIsUndefLabel);
        // Store $Undefined.Instance so the descriptor remains classified as
        // accessor (slot non-null).
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorGetter.GetSetMethod()!);
        il.Emit(OpCodes.Br, noGetterLabel);
        il.MarkLabel(getterStoreLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorGetter.GetSetMethod()!);
        il.MarkLabel(noGetterLabel);

        // Try to get "set" property (setter). Same callable check as "get".
        var noSetterLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "set");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noSetterLabel);
        var setterStoreLabel = il.DefineLabel();
        var setterIsUndefLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Brfalse, setterIsUndefLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, setterIsUndefLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, setterStoreLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, runtime.BoundAnyFunctionType);
        il.Emit(OpCodes.Brtrue, setterStoreLabel);
        il.Emit(OpCodes.Ldstr, "Property descriptor 'set' is not callable");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(setterIsUndefLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorSetter.GetSetMethod()!);
        il.Emit(OpCodes.Br, noSetterLabel);
        il.MarkLabel(setterStoreLabel);
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

        // ECMA-262 §10.1.6.3 ValidateAndApplyPropertyDescriptor: when an
        // existing non-configurable descriptor is being redefined, reject
        // incompatible changes. Covers Object/defineProperty/15.2.3.6-4-*
        // family (~50 tests) plus most defineProperties spec-validation tests.
        var existingDescLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Call, runtime.PDSGetPropertyDescriptor);
        il.Emit(OpCodes.Stloc, existingDescLocal);

        // Classify new descriptor type ahead of both validation and merge:
        // accessor if dict has "get"/"set", data if it has "value"/"writable".
        var newIsAccessorOuter = il.DeclareLocal(_types.Boolean);
        var newIsDataOuter = il.DeclareLocal(_types.Boolean);
        var tmpClassifyVal = il.DeclareLocal(_types.Object);
        var skipClassifyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, skipClassifyLabel);

        var setNewAccessorOuter = il.DefineLabel();
        var afterNewAccessorOuter = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "get");
        il.Emit(OpCodes.Ldloca, tmpClassifyVal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brtrue, setNewAccessorOuter);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "set");
        il.Emit(OpCodes.Ldloca, tmpClassifyVal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, afterNewAccessorOuter);
        il.MarkLabel(setNewAccessorOuter);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, newIsAccessorOuter);
        il.MarkLabel(afterNewAccessorOuter);

        var setNewDataOuter = il.DefineLabel();
        var afterNewDataOuter = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloca, tmpClassifyVal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brtrue, setNewDataOuter);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldloca, tmpClassifyVal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, afterNewDataOuter);
        il.MarkLabel(setNewDataOuter);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, newIsDataOuter);
        il.MarkLabel(afterNewDataOuter);

        il.MarkLabel(skipClassifyLabel);

        // ECMA-262 §6.2.5.5 ToPropertyDescriptor step 10: an attempt to
        // combine accessor (get/set) and data (value/writable) attributes in
        // a single descriptor throws TypeError. test262 15.2.3.6-3-1 et al.
        var noMixLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, newIsAccessorOuter);
        il.Emit(OpCodes.Brfalse, noMixLabel);
        il.Emit(OpCodes.Ldloc, newIsDataOuter);
        il.Emit(OpCodes.Brfalse, noMixLabel);
        il.Emit(OpCodes.Ldstr, "Invalid property descriptor. Cannot both specify accessors and a value or writable attribute");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(noMixLabel);

        var validationEndLabel = il.DefineLabel();
        // No existing descriptor → skip validation (new property add).
        il.Emit(OpCodes.Ldloc, existingDescLocal);
        il.Emit(OpCodes.Brfalse, validationEndLabel);
        // Existing is configurable → all changes allowed.
        il.Emit(OpCodes.Ldloc, existingDescLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorConfigurable.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, validationEndLabel);

        // Existing is non-configurable. Examine new descriptor for forbidden
        // changes. Re-consult the input dict for "was field X specified"
        // (the parsed descriptor already has all fields normalized).
        var throwRedefineLabel = il.DefineLabel();

        // We only run this block when the input was a dict (dictLocal non-null).
        // For non-dict descriptor sources we fall through to the apply step.
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, validationEndLabel);

        // Rule (a): if new specifies configurable=true → throw.
        var configKeyLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "configurable");
        il.Emit(OpCodes.Ldloca, configKeyLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        var checkEnumerableLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, checkEnumerableLabel);
        il.Emit(OpCodes.Ldloc, configKeyLocal);
        il.Emit(OpCodes.Call, runtime.IsTruthy);
        il.Emit(OpCodes.Brtrue, throwRedefineLabel);
        il.MarkLabel(checkEnumerableLabel);

        // Rule (b): if new specifies enumerable AND it differs from existing → throw.
        var enumKeyLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "enumerable");
        il.Emit(OpCodes.Ldloca, enumKeyLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        var checkTypeLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, checkTypeLabel);
        il.Emit(OpCodes.Ldloc, enumKeyLocal);
        il.Emit(OpCodes.Call, runtime.IsTruthy);
        il.Emit(OpCodes.Ldloc, existingDescLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorEnumerable.GetGetMethod()!);
        il.Emit(OpCodes.Bne_Un, throwRedefineLabel);
        il.MarkLabel(checkTypeLabel);

        // Rule (c): accessor↔data type swap. Existing is accessor if Getter
        // OR Setter is non-null. New is accessor if it specifies "get" or "set".
        var existingIsAccessor = il.DeclareLocal(_types.Boolean);
        var notExistingAccessor = il.DefineLabel();
        var setExistingAccessor = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, existingDescLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorGetter.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, setExistingAccessor);
        il.Emit(OpCodes.Ldloc, existingDescLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorSetter.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, notExistingAccessor);
        il.MarkLabel(setExistingAccessor);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, existingIsAccessor);
        var afterExistingAccessor = il.DefineLabel();
        il.Emit(OpCodes.Br, afterExistingAccessor);
        il.MarkLabel(notExistingAccessor);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, existingIsAccessor);
        il.MarkLabel(afterExistingAccessor);

        var newIsAccessor = il.DeclareLocal(_types.Boolean);
        var newIsData = il.DeclareLocal(_types.Boolean);
        var setNewAccessor = il.DefineLabel();
        var afterNewAccessor = il.DefineLabel();
        var tmpVal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "get");
        il.Emit(OpCodes.Ldloca, tmpVal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brtrue, setNewAccessor);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "set");
        il.Emit(OpCodes.Ldloca, tmpVal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, afterNewAccessor);
        il.MarkLabel(setNewAccessor);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, newIsAccessor);
        il.MarkLabel(afterNewAccessor);

        var setNewData = il.DefineLabel();
        var afterNewData = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloca, tmpVal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brtrue, setNewData);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldloca, tmpVal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, afterNewData);
        il.MarkLabel(setNewData);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, newIsData);
        il.MarkLabel(afterNewData);

        // Type swap: existing accessor + new data → throw. Existing data + new
        // accessor → throw. (Same descriptor type required when configurable=false.)
        var typeSwapDoneLabel = il.DefineLabel();
        var existingIsDataLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, existingIsAccessor);
        il.Emit(OpCodes.Brfalse, existingIsDataLabel);
        // existing accessor: new data forbids it.
        il.Emit(OpCodes.Ldloc, newIsData);
        il.Emit(OpCodes.Brtrue, throwRedefineLabel);
        il.Emit(OpCodes.Br, typeSwapDoneLabel);
        il.MarkLabel(existingIsDataLabel);
        // existing data: new accessor forbids it.
        il.Emit(OpCodes.Ldloc, newIsAccessor);
        il.Emit(OpCodes.Brtrue, throwRedefineLabel);
        il.MarkLabel(typeSwapDoneLabel);

        // Rule (d): data with existing.writable=false: cannot set writable=true.
        // (writable: false → true is forbidden when configurable=false.)
        // Existing is data when existingIsAccessor=false.
        var skipWritableCheck = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, existingIsAccessor);
        il.Emit(OpCodes.Brtrue, skipWritableCheck);
        // existing data. Check writable.
        il.Emit(OpCodes.Ldloc, existingDescLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorWritable.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, skipWritableCheck); // existing.writable=true → all OK
        // existing.writable=false. New specifies writable=true → throw.
        var writableKeyLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldloca, writableKeyLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        var checkValueChange = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, checkValueChange);
        il.Emit(OpCodes.Ldloc, writableKeyLocal);
        il.Emit(OpCodes.Call, runtime.IsTruthy);
        il.Emit(OpCodes.Brtrue, throwRedefineLabel);
        il.MarkLabel(checkValueChange);
        // New specifies value != existing.value → throw (data with writable=false).
        // Skip the equality check when existing.value is null: the prior PDS
        // descriptor was installed without an explicit value (\`defineProperty\`
        // with {writable:false} alone, before any value was captured). The
        // actual current value lives on the underlying object (array length
        // / dict entry), not in the PDS slot — comparing a non-null new value
        // against a null existing slot would falsely report a change.
        var valueKeyLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloca, valueKeyLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, skipWritableCheck);
        var existingValueForCompare = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloc, existingDescLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, existingValueForCompare);
        il.Emit(OpCodes.Ldloc, existingValueForCompare);
        il.Emit(OpCodes.Brfalse, skipWritableCheck);  // null existing → skip
        il.Emit(OpCodes.Ldloc, valueKeyLocal);
        il.Emit(OpCodes.Ldloc, existingValueForCompare);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Object, "Equals", _types.Object, _types.Object));
        il.Emit(OpCodes.Brfalse, throwRedefineLabel);
        il.MarkLabel(skipWritableCheck);

        // Validation passed.
        il.Emit(OpCodes.Br, validationEndLabel);

        il.MarkLabel(throwRedefineLabel);
        il.Emit(OpCodes.Ldstr, "Cannot redefine property");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(validationEndLabel);

        // ECMA-262 §10.1.6.3 step 6: when modifying an existing descriptor,
        // unspecified fields keep their existing values (don't overwrite to
        // defaults). Merge existing's values into descriptorLocal for any
        // field NOT specified in the new dict.
        var skipMergeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, existingDescLocal);
        il.Emit(OpCodes.Brfalse, skipMergeLabel);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, skipMergeLabel);

        void MergeIfMissing(string fieldName, PropertyInfo prop, LocalBuilder? skipWhenLocal = null)
        {
            var skipLabel = il.DefineLabel();
            // Skip if cross-type redefine: don't carry data fields into a new
            // accessor descriptor (or vice versa). \`skipWhenLocal\` is the
            // boolean that, when true, indicates an incompatible new-desc type.
            if (skipWhenLocal != null)
            {
                il.Emit(OpCodes.Ldloc, skipWhenLocal);
                il.Emit(OpCodes.Brtrue, skipLabel);
            }
            var tmpKey = il.DeclareLocal(_types.Object);
            il.Emit(OpCodes.Ldloc, dictLocal);
            il.Emit(OpCodes.Ldstr, fieldName);
            il.Emit(OpCodes.Ldloca, tmpKey);
            il.Emit(OpCodes.Callvirt, dictTryGetValue);
            il.Emit(OpCodes.Brtrue, skipLabel);   // already specified — skip merge
            // Copy from existing
            il.Emit(OpCodes.Ldloc, descriptorLocal);
            il.Emit(OpCodes.Ldloc, existingDescLocal);
            il.Emit(OpCodes.Callvirt, prop.GetGetMethod()!);
            il.Emit(OpCodes.Callvirt, prop.GetSetMethod()!);
            il.MarkLabel(skipLabel);
        }
        // Cross-type merge guards: new is data → don't carry get/set from
        // existing accessor; new is accessor → don't carry value/writable
        // from existing data. Use the OUTER (always-computed) classifiers
        // so the guard fires regardless of whether validation ran.
        MergeIfMissing("value", runtime.CompiledPropertyDescriptorValue, newIsAccessorOuter);
        MergeIfMissing("writable", runtime.CompiledPropertyDescriptorWritable, newIsAccessorOuter);
        MergeIfMissing("get", runtime.CompiledPropertyDescriptorGetter, newIsDataOuter);
        MergeIfMissing("set", runtime.CompiledPropertyDescriptorSetter, newIsDataOuter);
        MergeIfMissing("enumerable", runtime.CompiledPropertyDescriptorEnumerable);
        MergeIfMissing("configurable", runtime.CompiledPropertyDescriptorConfigurable);

        il.MarkLabel(skipMergeLabel);

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

        // ECMA-262 §7.3.5 + §19.1.2.4: when the property key is a Symbol,
        // look it up in the per-object symbol dict (same one that handles
        // `obj[Symbol.x]` index access). Required for prop-desc.js tests
        // that probe Symbol.match/matchAll/replace/search/split on
        // RegExp.prototype. Without this the ToJsString below throws
        // TypeError on every Symbol-keyed gOPD call.
        var notSymbolKeyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSSymbolType);
        il.Emit(OpCodes.Brfalse, notSymbolKeyLabel);
        EmitSymbolKeyDescriptorLookup(il, runtime);
        il.MarkLabel(notSymbolKeyLabel);

        // propName = $Runtime.ToJsString(prop) — spec ECMA-262 ToString. Honors
        // Array.prototype.toString (so `gOPD(obj, [1])` looks up "1", not "[1]"),
        // and avoids the prop.ToString() NRE for null.
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ToJsString);
        il.Emit(OpCodes.Stloc, propNameLocal);

        // Try to get descriptor from $PropertyDescriptorStore
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Call, runtime.PDSGetPropertyDescriptor);
        il.Emit(OpCodes.Stloc, descriptorLocal);

        // If descriptor is not null, convert it to a JS object
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Brtrue, hasDescriptorLabel);

        // ECMA-262 §17 — built-in functions expose `name` and `length` as
        // { writable: false, enumerable: false, configurable: true } own data
        // properties. Synthesize those descriptors when the receiver is a
        // $TSFunction (covers RegExp.prototype[Symbol.match], etc. that
        // verifyProperty inspects). Other callable wrappers fall through to
        // the existing paths (PDS / dict / class instance). After
        // `delete fn.name`/`length`, IsBuiltinDeleted hides the synthetic
        // descriptor — descriptor lookup returns null, matching the post-
        // delete state expected by verifyProperty's isConfigurable check.
        var notTSFunctionForDescLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, notTSFunctionForDescLabel);

        // name / length only — anything else on a function returns null.
        var notFnNameLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notFnNameLabel);
        // Hide if this instance had `name` deleted.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Call, runtime.IsBuiltinDeletedMethod);
        il.Emit(OpCodes.Brtrue, returnNullLabel);
        // value = TSFunction.GetMember(fn, "name") — or just inline it via the
        // GetProperty path which handles function name lookup.
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultDictLocal);
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        EmitDescriptorBoolField(il, resultDictLocal, "writable", false);
        EmitDescriptorBoolField(il, resultDictLocal, "enumerable", false);
        EmitDescriptorBoolField(il, resultDictLocal, "configurable", true);
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Br, endLabel);
        il.MarkLabel(notFnNameLabel);

        var notFnLengthLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notFnLengthLabel);
        // Hide if this instance had `length` deleted.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, runtime.IsBuiltinDeletedMethod);
        il.Emit(OpCodes.Brtrue, returnNullLabel);
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultDictLocal);
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        EmitDescriptorBoolField(il, resultDictLocal, "writable", false);
        EmitDescriptorBoolField(il, resultDictLocal, "enumerable", false);
        EmitDescriptorBoolField(il, resultDictLocal, "configurable", true);
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Br, endLabel);
        il.MarkLabel(notFnLengthLabel);

        // Other keys on a function: not own → null.
        il.Emit(OpCodes.Br, returnNullLabel);
        il.MarkLabel(notTSFunctionForDescLabel);

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
    /// Stack-effect: pops 0, returns from the enclosing method when the
    /// arg1 prop key is a Symbol. Reads <c>GetSymbolDict(arg0)</c> and, if
    /// the symbol resolves, builds a JS descriptor dict with
    /// <c>{value, writable:true, enumerable:false, configurable:true}</c>
    /// (the ECMA-262 §17 default for built-in data slots — matches
    /// RegExp.prototype's well-known-symbol-keyed methods). Returns
    /// undefined if the symbol isn't present in the dict — same semantics
    /// as the string-keyed PDS miss path below the call site.
    /// </summary>
    private void EmitSymbolKeyDescriptorLookup(ILGenerator il, EmittedRuntime runtime)
    {
        var symDictLocal = il.DeclareLocal(_types.DictionaryObjectObject);
        var valueLocal = il.DeclareLocal(_types.Object);
        var resultDictLocal = il.DeclareLocal(_types.DictionaryStringObject);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetSymbolDictMethod);
        il.Emit(OpCodes.Stloc, symDictLocal);

        var foundLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, symDictLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryObjectObject, "TryGetValue"));
        il.Emit(OpCodes.Brtrue, foundLabel);

        // Not in user symbol-dict — return undefined.
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(foundLabel);
        // Build descriptor dict: { value, writable:true, enumerable:false, configurable:true }
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultDictLocal);
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        EmitDescriptorBoolField(il, resultDictLocal, "writable", true);
        EmitDescriptorBoolField(il, resultDictLocal, "enumerable", false);
        EmitDescriptorBoolField(il, resultDictLocal, "configurable", true);
        il.Emit(OpCodes.Ldloc, resultDictLocal);
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
