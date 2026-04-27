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
