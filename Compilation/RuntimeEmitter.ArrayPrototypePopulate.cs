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

        // Wire with explicit JS-spec name + length per ECMA-262.
        // Length is the user-callable arg count (the receiver is implicit).
        void Wire(string jsName, MethodBuilder? helper, int jsLength)
        {
            if (helper is null) return;
            // Name first param "__this" so $TSFunction.InvokeWithThis prepends
            // the call-site receiver. Stage 4z35 added a List<object> coercion
            // branch in CoercePrimitiveArgs that materializes non-list receivers
            // via $Runtime.ArrayLikeMaterialize before the helper's Castclass —
            // unblocks borrowed Array.prototype.X patterns
            // (`obj.map = Array.prototype.map; obj.map(cb)`).
            try { helper.DefineParameter(1, System.Reflection.ParameterAttributes.None, "__this"); }
            catch { /* already named — ignore */ }
            il.Emit(OpCodes.Ldsfld, runtime.ArrayPrototypeField);
            il.Emit(OpCodes.Ldstr, jsName);
            // new $TSFunction(null, helper, jsName, jsLength)
            il.Emit(OpCodes.Ldnull); // target
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

        Wire("map",            runtime.ArrayMap,            1);
        Wire("filter",         runtime.ArrayFilter,         1);
        Wire("forEach",        runtime.ArrayForEach,        1);
        Wire("find",           runtime.ArrayFind,           1);
        Wire("findIndex",      runtime.ArrayFindIndex,      1);
        Wire("findLast",       runtime.ArrayFindLast,       1);
        Wire("findLastIndex",  runtime.ArrayFindLastIndex,  1);
        Wire("some",           runtime.ArraySome,           1);
        Wire("every",          runtime.ArrayEvery,          1);
        Wire("reduce",         runtime.ArrayReduce,         1);
        Wire("reduceRight",    runtime.ArrayReduceRight,    1);
        Wire("includes",       runtime.ArrayIncludes,       1);
        Wire("indexOf",        runtime.ArrayIndexOf,        1);
        Wire("lastIndexOf",    runtime.ArrayLastIndexOf,    1);
        Wire("join",           runtime.ArrayJoin,           1);
        Wire("concat",         runtime.ArrayConcat,         1);
        Wire("reverse",        runtime.ArrayReverse,        0);
        Wire("flat",           runtime.ArrayFlat,           0);
        Wire("flatMap",        runtime.ArrayFlatMap,        1);
        Wire("sort",           runtime.ArraySort,           1);
        Wire("toSorted",       runtime.ArrayToSorted,       1);
        Wire("splice",         runtime.ArraySplice,         2);
        Wire("toSpliced",      runtime.ArrayToSpliced,      2);
        Wire("toReversed",     runtime.ArrayToReversed,     0);
        Wire("with",           runtime.ArrayWith,           2);
        Wire("at",             runtime.ArrayAt,             1);
        Wire("fill",           runtime.ArrayFill,           1);
        Wire("copyWithin",     runtime.ArrayCopyWithin,     2);
        Wire("entries",        runtime.ArrayEntries,        0);
        Wire("keys",           runtime.ArrayKeys,           0);
        Wire("values",         runtime.ArrayValues,         0);
        Wire("slice",          runtime.ArraySlice,          2);
        Wire("push",           runtime.ArrayPush,           1);
        Wire("pop",            runtime.ArrayPop,            0);
        Wire("shift",          runtime.ArrayShift,          0);
        Wire("unshift",        runtime.ArrayUnshift,        1);

        // Methods without dedicated $Runtime helpers — wired to a generic
        // stub so typeof + isConstructor probes pass.
        Wire("toString",       runtime.StringPrototypeGenericStub, 0);
        Wire("toLocaleString", runtime.StringPrototypeGenericStub, 0);

        // Per ECMA-262 §23.1.3 Array.prototype's [[Prototype]] is %Object.prototype%.
        il.Emit(OpCodes.Ldsfld, runtime.ArrayPrototypeField);
        il.Emit(OpCodes.Ldsfld, runtime.ObjectPrototypeField);
        il.Emit(OpCodes.Call, runtime.PDSSetPrototype);

        il.Emit(OpCodes.Ret);

        runtime.ArrayPrototypePopulateMethod = method;
    }
}
