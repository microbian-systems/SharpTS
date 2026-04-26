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
        runtime.StringToUpperCase = EmitStringStringStub(typeBuilder, "StringToUpperCase", "ToUpper");
        runtime.StringToLowerCase = EmitStringStringStub(typeBuilder, "StringToLowerCase", "ToLower");
        runtime.StringTrim = EmitStringStringStub(typeBuilder, "StringTrim", "Trim");
        runtime.StringTrimStart = EmitStringStringStub(typeBuilder, "StringTrimStart", "TrimStart");
        runtime.StringTrimEnd = EmitStringStringStub(typeBuilder, "StringTrimEnd", "TrimEnd");

        // Generic stub for methods without specific helpers — used only for
        // typeof + isConstructor probes via $TSFunction wrappers. Returns
        // empty string. Wired by name into Stage 4z10's populate.
        runtime.StringPrototypeGenericStub = EmitStringStringStub(typeBuilder, "_StringPrototypeStub", "ToString");
    }

    private MethodBuilder EmitStringStringStub(TypeBuilder typeBuilder, string runtimeName, string netName)
    {
        var method = typeBuilder.DefineMethod(
            runtimeName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]);

        var il = method.GetILGenerator();
        // return Convert.ToString(arg0).<netName>()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToString", _types.Object));
        var netMethod = _types.GetMethodNoParams(_types.String, netName);
        if (netMethod == null)
            throw new System.InvalidOperationException($"String.{netName} not found");
        il.Emit(OpCodes.Callvirt, netMethod);
        il.Emit(OpCodes.Ret);

        return method;
    }
}
