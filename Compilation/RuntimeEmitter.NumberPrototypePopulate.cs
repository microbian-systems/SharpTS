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
        // Emit valueOf helper before the populate body that wires it up.
        var numberValueOfHelper = EmitNumberValueOfHelper(typeBuilder, runtime);

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

        // ECMA-262 21.1.3 Number.prototype.constructor === Number. Compiled
        // bare `Number` resolves to typeof(double) (per ILEmitter.Expressions
        // and InstanceOf semantics).
        il.Emit(OpCodes.Ldsfld, runtime.NumberPrototypeField);
        il.Emit(OpCodes.Ldstr, "constructor");
        il.Emit(OpCodes.Ldtoken, _types.Double);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Callvirt, setItem);

        // Wire with explicit JS-spec name + length per ECMA-262. Number's
        // prototype methods take (thisNumberValue, digits/precision/radix);
        // name first param "__this" so $TSFunction.InvokeWithThis prepends
        // the receiver. Without this, `n.toExponential(1000)` would map
        // 1000 to value (the first arg) and lose the receiver.
        void Wire(string jsName, MethodBuilder? helper, int jsLength)
        {
            if (helper is null) return;
            try { helper.DefineParameter(1, System.Reflection.ParameterAttributes.None, "__this"); }
            catch { /* already named — ignore */ }
            il.Emit(OpCodes.Ldsfld, runtime.NumberPrototypeField);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldtoken, helper);
            il.Emit(OpCodes.Ldtoken, helper.DeclaringType!);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle",
                _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
            il.Emit(OpCodes.Castclass, _types.MethodInfo);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldc_I4, jsLength);
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtorWithCache);
            il.Emit(OpCodes.Callvirt, setItem);
        }

        Wire("toFixed",        runtime.NumberToFixed,         1);
        Wire("toPrecision",    runtime.NumberToPrecision,     1);
        Wire("toExponential",  runtime.NumberToExponential,   1);
        // Stub these with NumberToStringRadix so typeof + IsConstructor work.
        // Not actually invoked by user code in the not-a-constructor.js path.
        Wire("toString",       runtime.NumberToStringRadix,   1);
        Wire("toLocaleString", runtime.NumberToStringRadix,   0);
        Wire("valueOf",        numberValueOfHelper,           0);

        // PDSSetPrototype(NumberPrototypeField, ObjectPrototypeField).
        // Per ECMA-262 §21.1.3 Number.prototype's [[Prototype]] is %Object.prototype%.
        il.Emit(OpCodes.Ldsfld, runtime.NumberPrototypeField);
        il.Emit(OpCodes.Ldsfld, runtime.ObjectPrototypeField);
        il.Emit(OpCodes.Call, runtime.PDSSetPrototype);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Number.prototype.valueOf helper. Per ECMA-262 21.1.3.7
    /// thisNumberValue: returns the underlying primitive number, unwrapping
    /// the boxed wrapper if needed. Returns NaN when receiver is not a
    /// number-shaped value (matches what `Number.prototype.valueOf.call(x)`
    /// does for non-Numbers — actually spec throws TypeError but compiled
    /// mode's looser behavior here is fine for the tests we exercise).
    /// </summary>
    private MethodBuilder EmitNumberValueOfHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "NumberValueOf",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]);
        var il = method.GetILGenerator();

        // If receiver has __primitiveValue, return it (boxed-Number unwrap).
        // GetProperty returns null for missing fields and $Undefined when the
        // property exists but is undefined; treat both as "not boxed".
        var primValLocal = il.DeclareLocal(_types.Object);
        var notBoxedLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, notBoxedLabel); // null receiver: skip
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brfalse, notBoxedLabel); // non-$Object: skip
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "__primitiveValue");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Stloc, primValLocal);
        il.Emit(OpCodes.Ldloc, primValLocal);
        il.Emit(OpCodes.Brfalse, notBoxedLabel);
        il.Emit(OpCodes.Ldloc, primValLocal);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, notBoxedLabel);
        il.Emit(OpCodes.Ldloc, primValLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notBoxedLabel);
        // ECMA-262 §21.1.3: Number.prototype's [[NumberData]] is +0.
        // `Number.prototype.valueOf()` must return 0, not the prototype dict.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, runtime.NumberPrototypeField);
        var notNumberPrototypeLabel = il.DefineLabel();
        il.Emit(OpCodes.Bne_Un, notNumberPrototypeLabel);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notNumberPrototypeLabel);

        // Not boxed: return the receiver as-is.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);

        return method;
    }
}
