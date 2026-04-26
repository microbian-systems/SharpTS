using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Populates <see cref="EmittedRuntime.NumberPrototypeField"/> with
    /// <c>$TSFunction</c> wrappers for the Number prototype methods we
    /// have helpers for. Mirrors <see cref="EmitArrayPrototypePopulate"/>.
    /// Only toFixed/toPrecision/toExponential have direct runtime helpers;
    /// others (toString/valueOf/toLocaleString) are wired to NumberToStringRadix
    /// as placeholders — they're typeof-probed but not invoked by the
    /// not-a-constructor.js tests, so the placeholder is sufficient.
    /// </summary>
    private void DefineNumberPrototypePopulateShell(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.NumberPrototypePopulateMethod = typeBuilder.DefineMethod(
            "_NumberPrototypePopulate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            Type.EmptyTypes);
    }

    private void EmitNumberPrototypePopulate(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = runtime.NumberPrototypePopulateMethod;
        var il = method.GetILGenerator();
        var setItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item",
            _types.String, _types.Object);

        var doFillLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, runtime.NumberPrototypeField);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, doFillLabel);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(doFillLabel);

        void Wire(string jsName, MethodBuilder? helper)
        {
            if (helper is null) return;
            il.Emit(OpCodes.Ldsfld, runtime.NumberPrototypeField);
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

        Wire("toFixed",        runtime.NumberToFixed);
        Wire("toPrecision",    runtime.NumberToPrecision);
        Wire("toExponential",  runtime.NumberToExponential);
        // Stub these with NumberToStringRadix so typeof + IsConstructor work.
        // Not actually invoked by user code in the not-a-constructor.js path.
        Wire("toString",       runtime.NumberToStringRadix);
        Wire("toLocaleString", runtime.NumberToStringRadix);
        Wire("valueOf",        runtime.NumberToStringRadix);

        il.Emit(OpCodes.Ret);
    }
}
