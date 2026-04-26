using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits a private static method that populates the
    /// <c>Array.prototype</c> singleton dictionary (<see cref="EmittedRuntime.ArrayPrototypeField"/>)
    /// with <c>$TSFunction</c> wrappers around the <c>$Runtime.Array*</c>
    /// helpers. Called from the static cctor's tail (the cctor's <c>Ret</c>
    /// is patched to <c>Call</c> this method first).
    /// </summary>
    /// <remarks>
    /// Must be emitted AFTER all <c>EmitArray*</c> helpers so the wrapped
    /// MethodBuilders are non-null. Most Test262 tests don't directly invoke
    /// the wrappers — they probe via <c>typeof Array.prototype.X</c> /
    /// <c>isConstructor(Array.prototype.X)</c>. The pattern matcher in
    /// <c>ILEmitter.Calls.cs</c> still handles
    /// <c>Array.prototype.X.call(receiver, …)</c> syntactically.
    /// </remarks>
    private void EmitArrayPrototypePopulate(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "_ArrayPrototypePopulate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            Type.EmptyTypes);

        var il = method.GetILGenerator();
        var setItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item",
            _types.String, _types.Object);

        // Idempotent: if dict already has entries, return early. cctor calls
        // this once, but a future static-init reordering shouldn't double-fill.
        var doFillLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, runtime.ArrayPrototypeField);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, doFillLabel);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(doFillLabel);

        // For each named method: dict[jsName] = new $TSFunction(null, methodInfo)
        // The 2-arg ctor without name/length is fine — IsConstructor only needs
        // DeclaringType to detect "$Runtime", and typeof returns "function".
        // Method signatures don't match what TSFunction.Invoke expects (helpers
        // take a List receiver as first arg, not the user args), so direct
        // .call/.apply through these wrappers won't dispatch correctly. The
        // pattern matcher in ILEmitter.Calls.cs intercepts the syntactic
        // Array.prototype.X.call form and bypasses these wrappers.

        void Wire(string jsName, MethodBuilder? helper)
        {
            if (helper is null) return;
            il.Emit(OpCodes.Ldsfld, runtime.ArrayPrototypeField);
            il.Emit(OpCodes.Ldstr, jsName);
            // new $TSFunction(null, helper)
            il.Emit(OpCodes.Ldnull); // target
            il.Emit(OpCodes.Ldtoken, helper);
            il.Emit(OpCodes.Ldtoken, helper.DeclaringType!);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle",
                _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
            il.Emit(OpCodes.Castclass, _types.MethodInfo);
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
            il.Emit(OpCodes.Callvirt, setItem);
        }

        Wire("map",            runtime.ArrayMap);
        Wire("filter",         runtime.ArrayFilter);
        Wire("forEach",        runtime.ArrayForEach);
        Wire("find",           runtime.ArrayFind);
        Wire("findIndex",      runtime.ArrayFindIndex);
        Wire("findLast",       runtime.ArrayFindLast);
        Wire("findLastIndex",  runtime.ArrayFindLastIndex);
        Wire("some",           runtime.ArraySome);
        Wire("every",          runtime.ArrayEvery);
        Wire("reduce",         runtime.ArrayReduce);
        Wire("reduceRight",    runtime.ArrayReduceRight);
        Wire("includes",       runtime.ArrayIncludes);
        Wire("indexOf",        runtime.ArrayIndexOf);
        Wire("lastIndexOf",    runtime.ArrayLastIndexOf);
        Wire("join",           runtime.ArrayJoin);
        Wire("concat",         runtime.ArrayConcat);
        Wire("reverse",        runtime.ArrayReverse);
        Wire("flat",           runtime.ArrayFlat);
        Wire("flatMap",        runtime.ArrayFlatMap);
        Wire("sort",           runtime.ArraySort);
        Wire("toSorted",       runtime.ArrayToSorted);
        Wire("splice",         runtime.ArraySplice);
        Wire("toSpliced",      runtime.ArrayToSpliced);
        Wire("toReversed",     runtime.ArrayToReversed);
        Wire("with",           runtime.ArrayWith);
        Wire("at",             runtime.ArrayAt);
        Wire("fill",           runtime.ArrayFill);
        Wire("copyWithin",     runtime.ArrayCopyWithin);
        Wire("entries",        runtime.ArrayEntries);
        Wire("keys",           runtime.ArrayKeys);
        Wire("values",         runtime.ArrayValues);
        Wire("slice",          runtime.ArraySlice);
        Wire("push",           runtime.ArrayPush);
        Wire("pop",            runtime.ArrayPop);
        Wire("shift",          runtime.ArrayShift);
        Wire("unshift",        runtime.ArrayUnshift);

        il.Emit(OpCodes.Ret);

        runtime.ArrayPrototypePopulateMethod = method;
    }
}
