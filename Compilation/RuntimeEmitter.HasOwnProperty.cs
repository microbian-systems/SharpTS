using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits <c>$Runtime.HasOwnPropertyHelper(object obj, object name) -&gt; bool</c>.
    /// Used to back <c>obj.hasOwnProperty(name)</c> for $TSFunction wrappers
    /// (Test262 frequently probes <c>String.prototype.X.hasOwnProperty("length")</c>),
    /// $Object instances, Dictionary literals, and arrays. Wired up in
    /// GetFunctionMethod / GetProperty / GetFieldsProperty for "hasOwnProperty".
    /// </summary>
    /// <remarks>
    /// Per ECMA-262, hasOwnProperty checks "own" properties — those directly
    /// on the receiver, not inherited. For each backing type:
    /// - $TSFunction: name is "name"/"length" (always cached) or PDS has a
    ///   user-defined descriptor for it.
    /// - $Object: name is in _fields or _getters.
    /// - Dictionary: ContainsKey(name).
    /// - List/$Array: name is "length" or a numeric index in range.
    /// - String: numeric index in [0,len) or "length".
    /// - Otherwise: false.
    /// Symbols / non-string names are coerced via ToString.
    /// </remarks>
    private void EmitHasOwnPropertyHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "HasOwnPropertyHelper",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.Object]);
        runtime.HasOwnPropertyHelperMethod = method;

        // Name parameter 0 as "__this" so the wrapping $TSFunction sets
        // _expectsThis. That routes `.call(receiver, name)` through
        // InvokeWithThis's expectsThis branch (which prepends the explicit
        // thisArg as args[0]) instead of relying on a target-bound shortcut
        // that double-prepends and trims the wrong argument.
        method.DefineParameter(1, ParameterAttributes.None, "__this");
        method.DefineParameter(2, ParameterAttributes.None, "name");

        var il = method.GetILGenerator();
        var nameLocal = il.DeclareLocal(_types.String);

        var falseLabel = il.DefineLabel();
        var trueLabel = il.DefineLabel();

        // ECMA-262 §20.1.3.2 step 2: Let O be ? ToObject(this value). ToObject
        // throws TypeError for null/undefined. Test262
        // `Object.prototype.hasOwnProperty.call(undefined, ...)` requires the
        // throw. Pre-fix `Brfalse falseLabel` silently returned false.
        var receiverOkLabel = il.DefineLabel();
        var receiverThrowLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, receiverThrowLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, receiverThrowLabel);
        il.Emit(OpCodes.Br, receiverOkLabel);

        il.MarkLabel(receiverThrowLabel);
        il.Emit(OpCodes.Ldstr, "Cannot convert undefined or null to object");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(receiverOkLabel);

        // ECMA-262 §20.1.3.2 step 1: Let P be ? ToPropertyKey(V). For Symbol,
        // ToPropertyKey returns the symbol itself — NOT a string. Symbol keys
        // are stored in the per-object symbol dict (GetSymbolDict), so resolve
        // them on that side path before falling through to the string-key
        // helpers below.
        var notSymbolKeyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.IsSymbolMethod);
        il.Emit(OpCodes.Brfalse, notSymbolKeyLabel);
        // GetSymbolDict(obj).ContainsKey(symbol)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetSymbolDictMethod);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryObjectObject, "ContainsKey", _types.Object));
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notSymbolKeyLabel);

        // Coerce name to string
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, falseLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Stloc, nameLocal);

        // $TSFunction branch
        var notTSFunction = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, notTSFunction);
        // If `name` or `length` was deleted on this instance, the property is
        // no longer own — report false. Per ECMA-262 §17, both are configurable.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Call, runtime.IsBuiltinDeletedMethod);
        il.Emit(OpCodes.Brtrue, falseLabel);
        // True for "name" or "length"
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, trueLabel);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, trueLabel);
        // Otherwise check PDS for own descriptor
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Call, runtime.PDSGetPropertyDescriptor);
        il.Emit(OpCodes.Brtrue, trueLabel);
        il.Emit(OpCodes.Br, falseLabel);
        il.MarkLabel(notTSFunction);

        // $Object branch
        var notTSObject = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brfalse, notTSObject);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectHasProperty);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notTSObject);

        // Dictionary<string,object> — own property is either a direct key in
        // the backing dict (\"foo\" = 1 syntax) OR a PDS-stored descriptor
        // (Object.defineProperty path, which doesn't write to _fields). Both
        // count as \"own\" per ECMA-262 §7.3.13. Pre-fix the PDS branch was
        // missing, so non-enumerable defineProperty results returned false
        // here even though they were truly own — that's the residual #103
        // tail (~3500 Fail entries that read like `assert(obj.hasOwnProperty(...))`
        // after a defineProperty with no enumerable: true).
        var notDict = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, notDict);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "ContainsKey", _types.String));
        il.Emit(OpCodes.Brtrue, trueLabel);
        // Fall through to PDS check on dict-keyed property descriptors.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Call, runtime.PDSGetPropertyDescriptor);
        il.Emit(OpCodes.Brtrue, trueLabel);
        il.Emit(OpCodes.Br, falseLabel);
        il.MarkLabel(notDict);

        // String — numeric index in [0,len) or "length"
        var notString = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, notString);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, trueLabel);
        // Numeric index — try int.TryParse
        var strIdxLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldloca, strIdxLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Int32, "TryParse", _types.String, _types.Int32.MakeByRefType()));
        il.Emit(OpCodes.Brfalse, falseLabel);
        // 0 <= idx < str.Length
        il.Emit(OpCodes.Ldloc, strIdxLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, falseLabel);
        il.Emit(OpCodes.Ldloc, strIdxLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.String, "Length").GetGetMethod()!);
        il.Emit(OpCodes.Bge, falseLabel);
        il.Emit(OpCodes.Br, trueLabel);
        il.MarkLabel(notString);

        // List<object> / $Array — "length" or numeric index in range
        var notList = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brfalse, notList);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, trueLabel);
        var listIdxLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldloca, listIdxLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Int32, "TryParse", _types.String, _types.Int32.MakeByRefType()));
        il.Emit(OpCodes.Brfalse, falseLabel);
        il.Emit(OpCodes.Ldloc, listIdxLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, falseLabel);
        il.Emit(OpCodes.Ldloc, listIdxLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, falseLabel);
        il.Emit(OpCodes.Br, trueLabel);
        il.MarkLabel(notList);

        // System.Type — check known own properties for built-in JS constructors
        // (Boolean/Number/String have "prototype"; all have "name" and "length").
        // Also check known JS-spec static names per Type, then static reflection.
        var notTypeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Type);
        il.Emit(OpCodes.Brfalse, notTypeLabel);
        // "prototype" / "name" / "length" → true for any Type
        void NameEq(string n)
        {
            il.Emit(OpCodes.Ldloc, nameLocal);
            il.Emit(OpCodes.Ldstr, n);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
            il.Emit(OpCodes.Brtrue, trueLabel);
        }
        NameEq("prototype");
        NameEq("name");
        NameEq("length");

        // Number static names — JS-spec own properties of the Number constructor.
        // System.Double's own static members don't match, so we check by Type ==
        // typeof(double).
        var notDoubleLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, _types.Double);
        il.Emit(OpCodes.Call, _types.Type.GetMethod("GetTypeFromHandle")!);
        il.Emit(OpCodes.Bne_Un, notDoubleLabel);
        NameEq("MAX_VALUE"); NameEq("MIN_VALUE");
        NameEq("NaN"); NameEq("POSITIVE_INFINITY"); NameEq("NEGATIVE_INFINITY");
        NameEq("MAX_SAFE_INTEGER"); NameEq("MIN_SAFE_INTEGER"); NameEq("EPSILON");
        NameEq("parseInt"); NameEq("parseFloat");
        NameEq("isNaN"); NameEq("isFinite"); NameEq("isInteger"); NameEq("isSafeInteger");
        il.MarkLabel(notDoubleLabel);

        // String static names.
        var notStringTypeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, _types.String);
        il.Emit(OpCodes.Call, _types.Type.GetMethod("GetTypeFromHandle")!);
        il.Emit(OpCodes.Bne_Un, notStringTypeLabel);
        NameEq("fromCharCode"); NameEq("fromCodePoint"); NameEq("raw");
        il.MarkLabel(notStringTypeLabel);
        // Reflection: type.GetField(name, Public|Static) ?? type.GetMethod(name, Public|Static)
        const System.Reflection.BindingFlags staticPub = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
        var typeLocal2 = il.DeclareLocal(_types.Type);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.Type);
        il.Emit(OpCodes.Stloc, typeLocal2);
        // GetField
        il.Emit(OpCodes.Ldloc, typeLocal2);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldc_I4, (int)staticPub);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetField", _types.String, typeof(System.Reflection.BindingFlags)));
        il.Emit(OpCodes.Brtrue, trueLabel);
        // GetProperty(name) — covers static .NET property accessors.
        il.Emit(OpCodes.Ldloc, typeLocal2);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldc_I4, (int)staticPub);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String, typeof(System.Reflection.BindingFlags)));
        il.Emit(OpCodes.Brtrue, trueLabel);
        il.Emit(OpCodes.Br, falseLabel);
        il.MarkLabel(notTypeLabel);

        // Default: PDS check (might find user-set descriptor)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Call, runtime.PDSGetPropertyDescriptor);
        il.Emit(OpCodes.Brtrue, trueLabel);

        il.MarkLabel(falseLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(trueLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits <c>$Runtime.PropertyIsEnumerableHelper(object obj, object name) -&gt; bool</c>.
    /// ECMA-262 §19.1.3.4: returns the property's [[Enumerable]] descriptor bit
    /// when the property is own, false otherwise. Spec defaults for built-in
    /// data properties are enumerable=false (§17), so PDS-installed descriptors
    /// for built-in accessors override the dict's default-true.
    /// </summary>
    /// <remarks>
    /// Pre-fix this slot pointed at <c>StringPrototypeGenericStub</c> which
    /// returned <c>Convert.ToString(receiver)</c> — i.e. a Dictionary's
    /// type-name string instead of a bool. verifyProperty's enumerable
    /// assertion (in propertyHelper.js) flunked every RegExp.prototype
    /// accessor's prop-desc.js as a result.
    /// </remarks>
    private void EmitPropertyIsEnumerableHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PropertyIsEnumerableHelper",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.Object]);
        runtime.PropertyIsEnumerableHelperMethod = method;
        method.DefineParameter(1, ParameterAttributes.None, "__this");
        method.DefineParameter(2, ParameterAttributes.None, "name");

        var il = method.GetILGenerator();
        var nameLocal = il.DeclareLocal(_types.String);
        var descLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
        var falseLabel = il.DefineLabel();

        // ECMA-262 §20.1.3.4 step 2: Let O be ? ToObject(this value). ToObject
        // throws TypeError for null/undefined. Tests S15.2.4.7_A12 + A13
        // verify each. Pre-fix silently returned false.
        var pieReceiverOkLabel = il.DefineLabel();
        var pieReceiverThrowLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, pieReceiverThrowLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, pieReceiverThrowLabel);
        il.Emit(OpCodes.Br, pieReceiverOkLabel);

        il.MarkLabel(pieReceiverThrowLabel);
        il.Emit(OpCodes.Ldstr, "Cannot convert undefined or null to object");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(pieReceiverOkLabel);

        // Symbol-keyed lookup: same routing as HasOwnPropertyHelper.
        var pieNotSymbolLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.IsSymbolMethod);
        il.Emit(OpCodes.Brfalse, pieNotSymbolLabel);
        // For Symbol keys: lookup descriptor in PDS (Object.defineProperty-
        // installed descriptors hold the Enumerable bit). If missing, fall
        // back to whether the symbol exists in GetSymbolDict (then default
        // enumerable=true for plain `obj[sym] = v` assignments).
        var pieSymPdsLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
        var pieSymNoPdsLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.PDSGetPropertyDescriptor);
        il.Emit(OpCodes.Stloc, pieSymPdsLocal);
        il.Emit(OpCodes.Ldloc, pieSymPdsLocal);
        il.Emit(OpCodes.Brfalse, pieSymNoPdsLabel);
        il.Emit(OpCodes.Ldloc, pieSymPdsLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorEnumerable.GetGetMethod()!);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(pieSymNoPdsLabel);
        // No PDS: check symbol dict. Present → enumerable by default; absent → false.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetSymbolDictMethod);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryObjectObject, "ContainsKey", _types.Object));
        il.Emit(OpCodes.Ret);
        il.MarkLabel(pieNotSymbolLabel);

        // Coerce name to string. (Object.ToString — same convention as
        // HasOwnPropertyHelper.)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, falseLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Stloc, nameLocal);

        // ECMA-262 §17: built-in function .name / .length are
        // { writable:false, enumerable:false, configurable:true } — return
        // false explicitly when the receiver is a $TSFunction and the name
        // is one of these synthetic slots. HasOwnPropertyHelper would
        // otherwise report true and the PropertyIsEnumerable fallback below
        // would mistakenly inherit that as enumerable=true.
        var notFunctionBuiltinLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, notFunctionBuiltinLabel);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, falseLabel);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, falseLabel);
        il.MarkLabel(notFunctionBuiltinLabel);

        // PDS descriptor lookup. When defineProperty installed a descriptor
        // for this name we already have the spec-correct Enumerable bit.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Call, runtime.PDSGetPropertyDescriptor);
        il.Emit(OpCodes.Stloc, descLocal);
        var noPdsLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, descLocal);
        il.Emit(OpCodes.Brfalse, noPdsLabel);
        il.Emit(OpCodes.Ldloc, descLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorEnumerable.GetGetMethod()!);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(noPdsLabel);

        // No PDS descriptor — fall back to HasOwnPropertyHelper. ECMA-262
        // says only "own" properties qualify; plain dict entries with no
        // explicit descriptor are spec-default enumerable=true, which is
        // exactly what HasOwn returns when found.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.HasOwnPropertyHelperMethod);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(falseLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }
}
