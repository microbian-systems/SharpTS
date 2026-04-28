using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits stub <c>$Runtime.StringTo*</c> / <c>StringTrim*</c> helpers used
    /// only for <c>$TSFunction</c> wrapping in <see cref="EmitStringPrototypePopulate"/>.
    /// The hot path for <c>"abc".toUpperCase()</c> is inline-emitted by
    /// <c>StringEmitter</c>, so these stubs aren't called from compiled
    /// user code — they exist purely so <c>String.prototype.toUpperCase</c>
    /// can be referenced as a value (typeof === "function",
    /// isConstructor === false) without polluting the inline dispatch path.
    /// Each stub takes <c>object</c> (the string this-arg) and returns the
    /// corresponding .NET String operation result.
    /// </summary>
    private void EmitStringPrototypeStubs(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Concrete trim/toUpperCase/toLowerCase helpers: enforce ECMA-262
        // RequireObjectCoercible (throw TypeError on undefined/null) and
        // coerce via JS-spec ToJsString (so .call(false) → "false" not
        // .NET "False").
        runtime.StringToUpperCase = EmitStringStringStub(typeBuilder, runtime, "StringToUpperCase", "ToUpper", strictReceiver: true);
        runtime.StringToLowerCase = EmitStringStringStub(typeBuilder, runtime, "StringToLowerCase", "ToLower", strictReceiver: true);
        runtime.StringTrim = EmitStringStringStub(typeBuilder, runtime, "StringTrim", "Trim", strictReceiver: true);
        runtime.StringTrimStart = EmitStringStringStub(typeBuilder, runtime, "StringTrimStart", "TrimStart", strictReceiver: true);
        runtime.StringTrimEnd = EmitStringStringStub(typeBuilder, runtime, "StringTrimEnd", "TrimEnd", strictReceiver: true);

        // Generic stub for methods without specific helpers — used only for
        // typeof + isConstructor probes via $TSFunction wrappers, AND wired
        // into Object.prototype.toString / Array.prototype.toString. Stays
        // tolerant of null/undefined receivers (returns empty string) since
        // those wirings legitimately call with non-string receivers.
        runtime.StringPrototypeGenericStub = EmitStringStringStub(typeBuilder, runtime, "_StringPrototypeStub", "ToString", strictReceiver: false);

        // ECMA-262 19.1.3.6 Object.prototype.toString — returns "[object X]"
        // brand based on receiver type. Wired into the Object.prototype slot
        // for borrowed-method patterns. Mirrors the syntactic
        // `Object.prototype.toString.call(...)` pattern matcher in
        // ILEmitter.Calls.cs.
        runtime.ObjectProtoToStringHelper = EmitObjectProtoToStringHelper(typeBuilder, runtime);
    }

    private MethodBuilder EmitObjectProtoToStringHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectProtoToString",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]
        );
        // Name first parameter "__this" so $TSFunction.InvokeWithThis (called
        // when the helper is borrowed via `obj.toString = Object.prototype.toString`)
        // prepends the receiver as arg0 instead of treating the user-supplied
        // arg list as the actual JS arguments.
        method.DefineParameter(1, ParameterAttributes.None, "__this");

        var il = method.GetILGenerator();
        var endLabel = il.DefineLabel();

        void EmitTag(string tag)
        {
            il.Emit(OpCodes.Ldstr, tag);
            il.Emit(OpCodes.Br, endLabel);
        }

        // null
        var notNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, notNullLabel);
        EmitTag("[object Null]");
        il.MarkLabel(notNullLabel);

        // undefined
        var notUndefLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brfalse, notUndefLabel);
        EmitTag("[object Undefined]");
        il.MarkLabel(notUndefLabel);

        // Math singleton
        var notMathLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, runtime.MathSingletonField);
        il.Emit(OpCodes.Bne_Un, notMathLabel);
        EmitTag("[object Math]");
        il.MarkLabel(notMathLabel);

        // JSON singleton
        var notJsonLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, runtime.JsonSingletonField);
        il.Emit(OpCodes.Bne_Un, notJsonLabel);
        EmitTag("[object JSON]");
        il.MarkLabel(notJsonLabel);

        // Number/String/Boolean/Array prototype singletons
        var notNumberProtoLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, runtime.NumberPrototypeField);
        il.Emit(OpCodes.Bne_Un, notNumberProtoLabel);
        EmitTag("[object Number]");
        il.MarkLabel(notNumberProtoLabel);

        var notStringProtoLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, runtime.StringPrototypeField);
        il.Emit(OpCodes.Bne_Un, notStringProtoLabel);
        EmitTag("[object String]");
        il.MarkLabel(notStringProtoLabel);

        var notBoolProtoLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, runtime.BooleanPrototypeField);
        il.Emit(OpCodes.Bne_Un, notBoolProtoLabel);
        EmitTag("[object Boolean]");
        il.MarkLabel(notBoolProtoLabel);

        var notArrayProtoLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, runtime.ArrayPrototypeField);
        il.Emit(OpCodes.Bne_Un, notArrayProtoLabel);
        EmitTag("[object Array]");
        il.MarkLabel(notArrayProtoLabel);

        // object[]
        var notArgsLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ObjectArray);
        il.Emit(OpCodes.Brfalse, notArgsLabel);
        EmitTag("[object Arguments]");
        il.MarkLabel(notArgsLabel);

        // List<object>
        var notListLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brfalse, notListLabel);
        EmitTag("[object Array]");
        il.MarkLabel(notListLabel);

        // $Array
        var notTSArrayLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSArrayType);
        il.Emit(OpCodes.Brfalse, notTSArrayLabel);
        EmitTag("[object Array]");
        il.MarkLabel(notTSArrayLabel);

        // string
        var notStringLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, notStringLabel);
        EmitTag("[object String]");
        il.MarkLabel(notStringLabel);

        // bool
        var notBoolLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brfalse, notBoolLabel);
        EmitTag("[object Boolean]");
        il.MarkLabel(notBoolLabel);

        // double
        var notDoubleLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, notDoubleLabel);
        EmitTag("[object Number]");
        il.MarkLabel(notDoubleLabel);

        // $TSFunction
        var notTSFunctionLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, notTSFunctionLabel);
        EmitTag("[object Function]");
        il.MarkLabel(notTSFunctionLabel);

        // Default
        il.Emit(OpCodes.Ldstr, "[object Object]");

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);

        return method;
    }

    private MethodBuilder EmitStringStringStub(TypeBuilder typeBuilder, EmittedRuntime runtime, string runtimeName, string netName, bool strictReceiver)
    {
        var method = typeBuilder.DefineMethod(
            runtimeName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]);

        var il = method.GetILGenerator();

        if (strictReceiver)
        {
            // ECMA-262 RequireObjectCoercible: throws TypeError on undefined/null.
            // Apply here so `String.prototype.trim.call(undefined)` and similar
            // borrowed-method patterns surface the spec-defined TypeError.
            var notNullLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Brtrue, notNullLabel);
            il.Emit(OpCodes.Ldstr, "Cannot convert undefined or null to object");
            il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
            il.Emit(OpCodes.Call, runtime.CreateException);
            il.Emit(OpCodes.Throw);
            il.MarkLabel(notNullLabel);

            var notUndefLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Isinst, runtime.UndefinedType);
            il.Emit(OpCodes.Brfalse, notUndefLabel);
            il.Emit(OpCodes.Ldstr, "Cannot convert undefined or null to object");
            il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
            il.Emit(OpCodes.Call, runtime.CreateException);
            il.Emit(OpCodes.Throw);
            il.MarkLabel(notUndefLabel);

            // Coerce via $Runtime.ToJsString — JS-spec ToString protocol so
            // booleans surface as "true"/"false" (not .NET "True"/"False"),
            // numbers via JS formatting, objects via valueOf/toString.
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, runtime.ToJsString);
        }
        else
        {
            // Generic-stub path used as the fallback wiring for various
            // prototype.toString slots. Stay tolerant of null/undefined and
            // avoid ToJsString — the latter walks the prototype chain looking
            // for "toString" which itself is wired to this stub, causing
            // infinite recursion. Convert.ToString gives the receiver's .NET
            // ToString without dispatching back through the prototype.
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToString", _types.Object));
        }

        var netMethod = _types.GetMethodNoParams(_types.String, netName);
        if (netMethod == null)
            throw new System.InvalidOperationException($"String.{netName} not found");
        il.Emit(OpCodes.Callvirt, netMethod);
        il.Emit(OpCodes.Ret);

        return method;
    }
}
