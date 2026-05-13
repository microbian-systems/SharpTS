using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    private void DefineObjectPrototypePopulateShell(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.ObjectPrototypePopulateMethod = typeBuilder.DefineMethod(
            "_ObjectPrototypePopulate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            Type.EmptyTypes);
    }

    /// <summary>
    /// Populates <see cref="EmittedRuntime.ObjectPrototypeField"/> with
    /// <c>$TSFunction</c> wrappers for hasOwnProperty/isPrototypeOf/toString/
    /// valueOf/etc. Required for Test262 tests that probe
    /// <c>Object.prototype.isPrototypeOf(SomeBuiltin.prototype)</c>.
    /// </summary>
    private void EmitObjectPrototypePopulate(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = runtime.ObjectPrototypePopulateMethod;
        var il = method.GetILGenerator();
        var setItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item",
            _types.String, _types.Object);

        var doFillLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, runtime.ObjectPrototypeField);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, doFillLabel);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(doFillLabel);

        // ECMA-262 19.1.3 Object.prototype.constructor === Object. Compiled
        // bare `Object` resolves to typeof(object) (per ObjectStaticEmitter).
        // Plant in dict + non-enumerable PDS descriptor (built-in §17 attrs).
        var protoDescLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
        void InstallNonEnumerableDescriptor(string jsName, System.Action emitValue)
        {
            il.Emit(OpCodes.Newobj, runtime.CompiledPropertyDescriptorCtor);
            il.Emit(OpCodes.Stloc, protoDescLocal);
            il.Emit(OpCodes.Ldloc, protoDescLocal);
            emitValue();
            il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetSetMethod()!);
            il.Emit(OpCodes.Ldloc, protoDescLocal);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorEnumerable.GetSetMethod()!);
            il.Emit(OpCodes.Ldsfld, runtime.ObjectPrototypeField);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldloc, protoDescLocal);
            il.Emit(OpCodes.Call, runtime.PDSDefineProperty);
            il.Emit(OpCodes.Pop);
        }

        il.Emit(OpCodes.Ldsfld, runtime.ObjectPrototypeField);
        il.Emit(OpCodes.Ldstr, "constructor");
        il.Emit(OpCodes.Ldtoken, _types.Object);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Callvirt, setItem);
        InstallNonEnumerableDescriptor("constructor", () =>
        {
            il.Emit(OpCodes.Ldtoken, _types.Object);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        });

        // Wire methods backed by $Runtime helpers. Each wrapper has the
        // helper as its MethodInfo and uses TSFunctionCtorWithCache for
        // proper .name + .length per ECMA-262.
        void Wire(string jsName, MethodBuilder helper, int jsLength)
        {
            var fnLocal = il.DeclareLocal(_types.Object);
            il.Emit(OpCodes.Ldnull); // target — methods take receiver as first arg
            il.Emit(OpCodes.Ldtoken, helper);
            il.Emit(OpCodes.Ldtoken, helper.DeclaringType!);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle",
                _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
            il.Emit(OpCodes.Castclass, _types.MethodInfo);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldc_I4, jsLength);
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtorWithCache);
            il.Emit(OpCodes.Stloc, fnLocal);
            // dict[jsName] = fn (covers fast-read path)
            il.Emit(OpCodes.Ldsfld, runtime.ObjectPrototypeField);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldloc, fnLocal);
            il.Emit(OpCodes.Callvirt, setItem);
            // Non-enumerable PDS descriptor for Object.keys / for-in / gOPD.
            InstallNonEnumerableDescriptor(jsName, () => il.Emit(OpCodes.Ldloc, fnLocal));
        }

        Wire("hasOwnProperty", runtime.HasOwnPropertyHelperMethod, 1);
        Wire("isPrototypeOf",  runtime.IsPrototypeOfHelperMethod,  1);
        // toString — ECMA-262 19.1.3.6 returns "[object X]" brand. Borrowed-
        // method patterns (`obj.getClass = Object.prototype.toString;
        // obj.getClass()`) need a real brand-tag function (the generic stub
        // returns Convert.ToString of receiver, which is wrong for arrays).
        Wire("toString",       runtime.ObjectProtoToStringHelper, 0);
        // ECMA-262 19.1.3.7 Object.prototype.valueOf returns ! ToObject(this).
        // For non-null/undefined values that means returning the receiver as
        // a JS object (we don't distinguish here — primitive receivers get the
        // primitive back, which the materializer's ToPrimitive treats as a
        // "valueOf returned non-primitive" signal so toString fires next).
        Wire("valueOf",        runtime.ObjectProtoValueOfHelper, 0);
        // toLocaleString = ToObject(this).toString — split helper enforces the
        // spec null/undef throw before delegating to ObjectProtoToString.
        Wire("toLocaleString", runtime.ObjectProtoToLocaleStringHelper, 0);
        Wire("propertyIsEnumerable", runtime.PropertyIsEnumerableHelperMethod, 1);

        il.Emit(OpCodes.Ret);
    }
}
