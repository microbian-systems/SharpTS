using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    private void DefinePromisePrototypePopulateShell(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.PromisePrototypePopulateMethod = typeBuilder.DefineMethod(
            "_PromisePrototypePopulate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            Type.EmptyTypes);
    }

    /// <summary>
    /// Emits <c>PromiseThenHelper(object __this, params object[] args)</c>,
    /// <c>PromiseCatchHelper</c>, <c>PromiseFinallyHelper</c>. Each unwraps the
    /// receiver to <c>Task&lt;object&gt;</c> (handles raw Task and $Promise wrapper),
    /// then dispatches to the corresponding state-machine helper. Used as
    /// MethodInfo backing for the <c>$TSFunction</c> wrappers installed on
    /// <see cref="EmittedRuntime.PromisePrototypeField"/>.
    /// </summary>
    private void EmitPromisePrototypeHelpers(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // PromiseThenHelper(object __this, object onFulfilled, object onRejected) -> Task<object>
        {
            var m = typeBuilder.DefineMethod(
                "PromiseThenHelper",
                MethodAttributes.Public | MethodAttributes.Static,
                _types.Object,
                [_types.Object, _types.Object, _types.Object]);
            m.DefineParameter(1, ParameterAttributes.None, "__this");
            m.DefineParameter(2, ParameterAttributes.None, "onFulfilled");
            m.DefineParameter(3, ParameterAttributes.None, "onRejected");
            runtime.PromiseThenHelperMethod = m;

            var il = m.GetILGenerator();
            var taskLocal = il.DeclareLocal(_types.TaskOfObject);
            EmitUnwrapToTask(il, runtime, taskLocal);
            il.Emit(OpCodes.Ldloc, taskLocal);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, runtime.PromiseThen);
            il.Emit(OpCodes.Ret);
        }

        // PromiseCatchHelper(object __this, object onRejected) -> Task<object>
        {
            var m = typeBuilder.DefineMethod(
                "PromiseCatchHelper",
                MethodAttributes.Public | MethodAttributes.Static,
                _types.Object,
                [_types.Object, _types.Object]);
            m.DefineParameter(1, ParameterAttributes.None, "__this");
            m.DefineParameter(2, ParameterAttributes.None, "onRejected");
            runtime.PromiseCatchHelperMethod = m;

            var il = m.GetILGenerator();
            var taskLocal = il.DeclareLocal(_types.TaskOfObject);
            EmitUnwrapToTask(il, runtime, taskLocal);
            il.Emit(OpCodes.Ldloc, taskLocal);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, runtime.PromiseCatch);
            il.Emit(OpCodes.Ret);
        }

        // PromiseFinallyHelper(object __this, object onFinally) -> Task<object>
        {
            var m = typeBuilder.DefineMethod(
                "PromiseFinallyHelper",
                MethodAttributes.Public | MethodAttributes.Static,
                _types.Object,
                [_types.Object, _types.Object]);
            m.DefineParameter(1, ParameterAttributes.None, "__this");
            m.DefineParameter(2, ParameterAttributes.None, "onFinally");
            runtime.PromiseFinallyHelperMethod = m;

            var il = m.GetILGenerator();
            var taskLocal = il.DeclareLocal(_types.TaskOfObject);
            EmitUnwrapToTask(il, runtime, taskLocal);
            il.Emit(OpCodes.Ldloc, taskLocal);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, runtime.PromiseFinally);
            il.Emit(OpCodes.Ret);
        }
    }

    /// <summary>
    /// Emits IL that stores <c>Ldarg_0</c> (or its <c>$Promise.Task</c> projection
    /// when the arg is a $Promise wrapper) into <paramref name="taskLocal"/>.
    /// Falls through with the local set; on non-Promise/non-Task receivers
    /// leaves it null and lets the downstream helper handle ToString/await.
    /// </summary>
    private void EmitUnwrapToTask(ILGenerator il, EmittedRuntime runtime, LocalBuilder taskLocal)
    {
        var notTSPromiseLabel = il.DefineLabel();
        var doneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSPromiseType);
        il.Emit(OpCodes.Brfalse, notTSPromiseLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSPromiseType);
        il.Emit(OpCodes.Callvirt, runtime.TSPromiseTaskGetter);
        il.Emit(OpCodes.Stloc, taskLocal);
        il.Emit(OpCodes.Br, doneLabel);

        il.MarkLabel(notTSPromiseLabel);
        // Raw Task<object> or anything else — Castclass works for Task<object>;
        // null/non-Task falls to a null task local and the helper invocation
        // will throw NRE which the surrounding async/then machinery surfaces
        // as a rejection.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.TaskOfObject);
        il.Emit(OpCodes.Stloc, taskLocal);

        il.MarkLabel(doneLabel);
    }

    /// <summary>
    /// Populates <see cref="EmittedRuntime.PromisePrototypeField"/> with
    /// <c>$TSFunction</c> wrappers for then/catch/finally + a constructor
    /// pointer to <c>typeof(Task&lt;object&gt;)</c> + non-enumerable PDS
    /// descriptors (ECMA-262 §17 spec attrs for built-in methods).
    /// </summary>
    private void EmitPromisePrototypePopulate(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = runtime.PromisePrototypePopulateMethod;
        var il = method.GetILGenerator();
        var setItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item",
            _types.String, _types.Object);

        var doFillLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, runtime.PromisePrototypeField);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, doFillLabel);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(doFillLabel);

        // Promise.prototype.constructor === Promise (= typeof(Task<object>)).
        il.Emit(OpCodes.Ldsfld, runtime.PromisePrototypeField);
        il.Emit(OpCodes.Ldstr, "constructor");
        il.Emit(OpCodes.Ldtoken, _types.TaskOfObject);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Callvirt, setItem);

        var descLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
        void InstallNonEnumerableDescriptor(string jsName, System.Action emitValue)
        {
            il.Emit(OpCodes.Newobj, runtime.CompiledPropertyDescriptorCtor);
            il.Emit(OpCodes.Stloc, descLocal);
            il.Emit(OpCodes.Ldloc, descLocal);
            emitValue();
            il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetSetMethod()!);
            il.Emit(OpCodes.Ldloc, descLocal);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorEnumerable.GetSetMethod()!);
            il.Emit(OpCodes.Ldsfld, runtime.PromisePrototypeField);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldloc, descLocal);
            il.Emit(OpCodes.Call, runtime.PDSDefineProperty);
            il.Emit(OpCodes.Pop);
        }
        InstallNonEnumerableDescriptor("constructor", () =>
        {
            il.Emit(OpCodes.Ldtoken, _types.TaskOfObject);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        });

        // Wire then/catch/finally with their helper MethodInfos.
        void Wire(string jsName, MethodBuilder helper, int jsLength)
        {
            var fnLocal = il.DeclareLocal(_types.Object);
            il.Emit(OpCodes.Ldnull); // target — helpers take receiver as __this
            il.Emit(OpCodes.Ldtoken, helper);
            il.Emit(OpCodes.Ldtoken, helper.DeclaringType!);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle",
                _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
            il.Emit(OpCodes.Castclass, _types.MethodInfo);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldc_I4, jsLength);
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtorWithCache);
            il.Emit(OpCodes.Stloc, fnLocal);
            il.Emit(OpCodes.Ldsfld, runtime.PromisePrototypeField);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldloc, fnLocal);
            il.Emit(OpCodes.Callvirt, setItem);
            InstallNonEnumerableDescriptor(jsName, () => il.Emit(OpCodes.Ldloc, fnLocal));
        }

        // ECMA-262 §27.2.5: then/catch take 2 and 1 args respectively;
        // finally takes 1 (onFinally). Spec lengths matter for length.js tests.
        Wire("then",    runtime.PromiseThenHelperMethod,    2);
        Wire("catch",   runtime.PromiseCatchHelperMethod,   1);
        Wire("finally", runtime.PromiseFinallyHelperMethod, 1);

        il.Emit(OpCodes.Ret);
    }
}
