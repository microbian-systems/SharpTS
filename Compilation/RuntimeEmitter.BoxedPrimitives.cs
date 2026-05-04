using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits <c>$Runtime.NewBoxedPrimitive(string typeTag, object value) -&gt; $Object</c>:
    /// builds a fresh <c>$Object</c> wrapping a primitive (boolean/number/string)
    /// for the <c>new Boolean(x) / new Number(x) / new String(x)</c> ECMA-262
    /// boxed-primitive protocol. The wrapper has:
    /// <list type="bullet">
    /// <item>Marker field <c>__primitiveType</c> holding the JS type name.</item>
    /// <item>Marker field <c>__primitiveValue</c> holding the underlying primitive.</item>
    /// <item>PDS prototype link to the matching prototype singleton.</item>
    /// </list>
    /// Plus methods like <c>valueOf</c> are available via the prototype chain.
    /// Used by <c>TryEmitBuiltInConstructor</c> for Boolean/Number/String.
    /// </summary>
    private void EmitNewBoxedPrimitive(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "NewBoxedPrimitive",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.Object]);
        runtime.NewBoxedPrimitiveMethod = method;

        var il = method.GetILGenerator();
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var objLocal = il.DeclareLocal(runtime.TSObjectType);
        var typeTagLocal = il.DeclareLocal(_types.String);
        var setItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item",
            _types.String, _types.Object);

        // Save type tag
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stloc, typeTagLocal);

        // dict = new Dictionary<string,object>()
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Stloc, dictLocal);

        // dict["__primitiveType"] = typeTag
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "__primitiveType");
        il.Emit(OpCodes.Ldloc, typeTagLocal);
        il.Emit(OpCodes.Callvirt, setItem);

        // dict["__primitiveValue"] = value
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "__primitiveValue");
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, setItem);

        // String exotic object: per ECMA-262 §10.4.3, a String wrapper has a
        // [[Length]] internal slot mirroring the primitive's length, plus
        // own indexed properties for each character. We materialize them
        // into the dict so dynamic GetProperty surfaces them naturally —
        // `(new String("hi")).length === 2` and `s[0] === "h"`. Skipped for
        // Number/Boolean wrappers (no equivalent slots).
        var skipStringLayoutLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, typeTagLocal);
        il.Emit(OpCodes.Ldstr, "String");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, skipStringLayoutLabel);
        // Only string values get the layout — defensive against caller passing
        // a non-string for the "String" tag (shouldn't happen in practice).
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, skipStringLayoutLabel);

        var strLocal = il.DeclareLocal(_types.String);
        var lenLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Stloc, strLocal);
        il.Emit(OpCodes.Ldloc, strLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.String, "Length").GetGetMethod()!);
        il.Emit(OpCodes.Stloc, lenLocal);

        // dict["length"] = (double)len
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Ldloc, lenLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Callvirt, setItem);

        // for (i = 0; i < len; i++) dict[i.ToString()] = str[i].ToString()
        var idxLocal = il.DeclareLocal(_types.Int32);
        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, idxLocal);
        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldloc, lenLocal);
        il.Emit(OpCodes.Bge, loopEnd);
        il.Emit(OpCodes.Ldloc, dictLocal);
        // key = idx.ToString()
        il.Emit(OpCodes.Ldloca, idxLocal);
        il.Emit(OpCodes.Call, _types.Int32.GetMethod("ToString", Type.EmptyTypes)!);
        // value = str[idx].ToString() (single-char string)
        var charLocal = il.DeclareLocal(_types.Char);
        il.Emit(OpCodes.Ldloc, strLocal);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "get_Chars", _types.Int32));
        il.Emit(OpCodes.Stloc, charLocal);
        il.Emit(OpCodes.Ldloca, charLocal);
        il.Emit(OpCodes.Call, _types.GetMethodNoParams(_types.Char, "ToString"));
        il.Emit(OpCodes.Callvirt, setItem);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, idxLocal);
        il.Emit(OpCodes.Br, loopStart);
        il.MarkLabel(loopEnd);

        il.MarkLabel(skipStringLayoutLabel);

        // obj = new $Object(dict)
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);
        il.Emit(OpCodes.Stloc, objLocal);

        // Set prototype based on typeTag (Boolean/Number/String → matching singleton).
        // Populate is called to ensure the prototype singleton has the right
        // methods (e.g. toString stub) before we link.
        void LinkProto(string tag, FieldBuilder protoField, MethodBuilder populate)
        {
            var skip = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, typeTagLocal);
            il.Emit(OpCodes.Ldstr, tag);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
            il.Emit(OpCodes.Brfalse, skip);
            il.Emit(OpCodes.Call, populate);
            il.Emit(OpCodes.Ldloc, objLocal);
            il.Emit(OpCodes.Ldsfld, protoField);
            il.Emit(OpCodes.Call, runtime.PDSSetPrototype);
            il.MarkLabel(skip);
        }
        LinkProto("Boolean", runtime.BooleanPrototypeField, runtime.BooleanPrototypePopulateMethod);
        LinkProto("Number",  runtime.NumberPrototypeField,  runtime.NumberPrototypePopulateMethod);
        LinkProto("String",  runtime.StringPrototypeField,  runtime.StringPrototypePopulateMethod);

        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits <c>$Runtime.ToObject(object value) -&gt; object</c>: ECMA-262
    /// 7.1.18 ToObject coercion. <c>null</c>/<c>undefined</c> → empty
    /// <c>$Object</c>; <c>bool</c>/<c>double</c> → boxed wrapper via
    /// <c>NewBoxedPrimitive</c>; everything else (including <c>string</c>)
    /// passes through unchanged. Used by <c>new Object(v)</c> in compiled mode.
    /// String is intentionally not boxed — see Stage 4z19 carve-out.
    /// </summary>
    private void EmitToObject(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ToObject",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]);
        runtime.ToObjectMethod = method;

        var il = method.GetILGenerator();

        // null → empty $Object
        var notNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, notNullLabel);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notNullLabel);

        // undefined → empty $Object
        var notUndefLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brfalse, notUndefLabel);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notUndefLabel);

        // bool → NewBoxedPrimitive("Boolean", arg)
        var notBoolLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brfalse, notBoolLabel);
        il.Emit(OpCodes.Ldstr, "Boolean");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.NewBoxedPrimitiveMethod);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notBoolLabel);

        // double → NewBoxedPrimitive("Number", arg)
        var notNumLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, notNumLabel);
        il.Emit(OpCodes.Ldstr, "Number");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.NewBoxedPrimitiveMethod);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notNumLabel);

        // Otherwise (string, dict, array, $Object, etc.) — return as-is.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    private void DefineIsBoxedPrimitiveOfTypeShell(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.IsBoxedPrimitiveOfTypeMethod = typeBuilder.DefineMethod(
            "IsBoxedPrimitiveOfType",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.String]);
    }

    /// <summary>
    /// Emits <c>$Runtime.UnwrapStringReceiver(object) -&gt; string</c>: coerces
    /// a String-method receiver to its underlying string. Fast-paths actual
    /// strings; unwraps Stage-4z19 boxed primitives (<c>$Object</c> with
    /// <c>__primitiveType="String"</c>) to their <c>__primitiveValue</c>;
    /// otherwise falls back to <c>ToJsString</c> (which handles bool/double/etc.
    /// per the JS spec ToString protocol).
    /// </summary>
    private void EmitUnwrapStringReceiver(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "UnwrapStringReceiver",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]);
        runtime.UnwrapStringReceiverMethod = method;

        var il = method.GetILGenerator();

        // string fast path
        var notStringLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, notStringLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notStringLabel);

        // $Object wrapper unwrap: __primitiveValue (string only)
        var notWrapperLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brfalse, notWrapperLabel);
        var primValLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "__primitiveValue");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Stloc, primValLocal);
        il.Emit(OpCodes.Ldloc, primValLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, notWrapperLabel);
        il.Emit(OpCodes.Ldloc, primValLocal);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notWrapperLabel);

        // Fallback: spec ToString. Note ToJsString does NOT throw on
        // null/undefined (returns "null" / "undefined") — direct call sites
        // through StringEmitter only fire on receivers the type checker
        // proved non-nullish, so this matches expectations. Borrowed-method
        // dispatch flows through CoercePrimitiveArgs.RequireObjectCoercibleThis
        // which is the spec gate.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.ToJsString);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits <c>$Runtime.IsBoxedPrimitiveOfType(object obj, string typeTag) -&gt; bool</c>:
    /// returns true iff <paramref name="obj"/> is a <c>$Object</c> whose
    /// <c>__primitiveType</c> field equals <paramref name="typeTag"/>. Used by
    /// the <c>instanceof</c> emitter to recognize boxed Boolean/Number/String.
    /// </summary>
    private void EmitIsBoxedPrimitiveOfType(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = runtime.IsBoxedPrimitiveOfTypeMethod;
        var il = method.GetILGenerator();
        var falseLabel = il.DefineLabel();

        // null/undefined → false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, falseLabel);

        // Must be $Object
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brfalse, falseLabel);

        // Use HasOwnPropertyHelper-style lookup via TSObject.HasProperty +
        // GetProperty for "__primitiveType". Read via the public getter.
        var typeValueLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Ldstr, "__primitiveType");
        il.Emit(OpCodes.Callvirt, runtime.TSObjectGetProperty);
        il.Emit(OpCodes.Stloc, typeValueLocal);

        // Compare with typeTag string.
        il.Emit(OpCodes.Ldloc, typeValueLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, falseLabel);
        il.Emit(OpCodes.Ldloc, typeValueLocal);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(falseLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits <c>$Runtime.UnwrapIfBoxed(object obj) -&gt; object</c>: returns
    /// the underlying <c>__primitiveValue</c> when <paramref name="obj"/> is a
    /// <c>$Object</c> wrapper produced by <c>NewBoxedPrimitive</c> (i.e. has
    /// both <c>__primitiveType</c> and <c>__primitiveValue</c> fields), else
    /// returns the value unchanged. Used by abstract equality and string
    /// concatenation: ECMA-262 §7.2.14 step 11 and §13.10 require ToPrimitive
    /// on Object operands before comparison/concatenation, and the spec'd
    /// path lands at the wrapper's <c>__primitiveValue</c> via
    /// <c>OrdinaryToPrimitive(hint)</c> → <c>valueOf()</c>. This is the cheap
    /// shortcut the spec endorses for boxed primitives specifically.
    /// </summary>
    private void EmitUnwrapIfBoxed(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "UnwrapIfBoxed",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]);
        runtime.UnwrapIfBoxedMethod = method;

        var il = method.GetILGenerator();
        var passThruLabel = il.DefineLabel();

        // null / undefined / non-$Object → pass through
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, passThruLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brfalse, passThruLabel);

        // Must have __primitiveType marker (else it's a plain $Object).
        var typeMarkerLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Ldstr, "__primitiveType");
        il.Emit(OpCodes.Callvirt, runtime.TSObjectGetProperty);
        il.Emit(OpCodes.Stloc, typeMarkerLocal);
        il.Emit(OpCodes.Ldloc, typeMarkerLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, passThruLabel);

        // Read __primitiveValue and return it.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Ldstr, "__primitiveValue");
        il.Emit(OpCodes.Callvirt, runtime.TSObjectGetProperty);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(passThruLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }
}
