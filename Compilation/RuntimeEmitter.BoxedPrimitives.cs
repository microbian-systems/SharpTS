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
}
