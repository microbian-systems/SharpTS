using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Populates <see cref="EmittedRuntime.BooleanPrototypeField"/> with
    /// <c>$TSFunction</c> wrappers for toString/valueOf. No dedicated
    /// $Runtime helpers exist for these (compiled mode handles
    /// <c>Boolean.toString()</c> via the inline String-conversion path),
    /// so the wrappers point at <see cref="EmittedRuntime.StringPrototypeGenericStub"/>
    /// — sufficient for typeof + IsConstructor probes from
    /// Test262 not-a-constructor.js tests.
    /// </summary>
    private void DefineBooleanPrototypePopulateShell(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.BooleanPrototypePopulateMethod = typeBuilder.DefineMethod(
            "_BooleanPrototypePopulate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            Type.EmptyTypes);
    }

    private void EmitBooleanPrototypePopulate(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = runtime.BooleanPrototypePopulateMethod;
        var il = method.GetILGenerator();
        var setItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item",
            _types.String, _types.Object);

        var doFillLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, runtime.BooleanPrototypeField);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, doFillLabel);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(doFillLabel);

        void Wire(string jsName, MethodBuilder? helper)
        {
            if (helper is null) return;
            il.Emit(OpCodes.Ldsfld, runtime.BooleanPrototypeField);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldtoken, helper);
            il.Emit(OpCodes.Ldtoken, helper.DeclaringType!);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle",
                _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
            il.Emit(OpCodes.Castclass, _types.MethodInfo);
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
            il.Emit(OpCodes.Callvirt, setItem);
        }

        Wire("toString", runtime.StringPrototypeGenericStub);
        Wire("valueOf",  runtime.StringPrototypeGenericStub);

        il.Emit(OpCodes.Ret);
    }
}
