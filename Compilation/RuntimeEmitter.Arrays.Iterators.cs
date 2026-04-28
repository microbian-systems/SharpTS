using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits: <c>if (callback == null || callback is $Undefined) throw new TypeError(...);</c>.
    /// ECMA-262 23.1.3.* require iterator-helper callbacks to be callable.
    /// Without this, our ArrayMap/ArrayEvery/etc. silently invoke null and
    /// throw NullReferenceException instead of TypeError. Caller passes the
    /// argument-index of the callback (most are arg1).
    /// </summary>
    private void EmitThrowIfCallbackNotCallable(ILGenerator il, EmittedRuntime runtime, int argIndex, string methodName)
    {
        var okLabel = il.DefineLabel();
        var throwLabel = il.DefineLabel();
        OpCode loadOp = argIndex switch
        {
            0 => OpCodes.Ldarg_0,
            1 => OpCodes.Ldarg_1,
            2 => OpCodes.Ldarg_2,
            3 => OpCodes.Ldarg_3,
            _ => throw new InvalidOperationException("EmitThrowIfCallbackNotCallable: unsupported argIndex")
        };
        il.Emit(loadOp);
        il.Emit(OpCodes.Brfalse, throwLabel);
        il.Emit(loadOp);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, throwLabel);

        // Positive callable check: $TSFunction / $BoundTSFunction / Function*
        // wrappers pass; anything else (bool, double, string, dict, list, …)
        // throws TypeError. ECMA-262 IsCallable returns true only for
        // function-like values; tests like `arr.every(true)` rely on the throw.
        il.Emit(loadOp);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, okLabel);
        il.Emit(loadOp);
        il.Emit(OpCodes.Isinst, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Brtrue, okLabel);
        il.Emit(loadOp);
        il.Emit(OpCodes.Isinst, runtime.FunctionBindWrapperType);
        il.Emit(OpCodes.Brtrue, okLabel);
        il.Emit(loadOp);
        il.Emit(OpCodes.Isinst, runtime.FunctionCallWrapperType);
        il.Emit(OpCodes.Brtrue, okLabel);
        il.Emit(loadOp);
        il.Emit(OpCodes.Isinst, runtime.FunctionApplyWrapperType);
        il.Emit(OpCodes.Brtrue, okLabel);
        // Default: not a recognized function-like → throw.
        il.Emit(OpCodes.Br, throwLabel);

        il.MarkLabel(throwLabel);
        // Build a real $TypeError instance + wrap in .NET Exception via CreateException
        // (matches the pattern used elsewhere — Object.defineProperty etc.). This is
        // important because test262 harness's `assert.throws(TypeError, fn)` checks
        // `e instanceof TypeError`, and only $TypeError instances satisfy that.
        il.Emit(OpCodes.Ldstr, methodName + " callback is not callable");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(okLabel);
    }

    /// <summary>
    /// Emits: <c>if (list[indexLocal] is $ArrayHole) goto skipLabel;</c>.
    /// Used by Stage E.2 M4 to skip holes in forEach/filter/reduce/every/
    /// some/flat/flatMap/indexOf/lastIndexOf per ECMA-262. Expects
    /// <c>arg0 = list</c> (the List&lt;object?&gt; we're iterating).
    /// </summary>
    private void EmitSkipIfHole(ILGenerator il, LocalBuilder indexLocal, Label skipLabel, EmittedRuntime runtime)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Item").GetGetMethod()!);
        il.Emit(OpCodes.Isinst, runtime.ArrayHoleType);
        il.Emit(OpCodes.Brtrue, skipLabel);
    }

    /// <summary>
    /// Emits: load <c>list[indexLocal]</c>, and if it's the <c>$ArrayHole</c>
    /// sentinel substitute <c>$Undefined.Instance</c>. Leaves the boundary-
    /// adjusted value on the stack. Used by find/findIndex/findLast/
    /// findLastIndex where the callback IS invoked on holes but with
    /// <c>undefined</c> as the element (matches interpreter's <c>arr[i]</c>
    /// unhole semantics).
    /// </summary>
    private void EmitLoadElementUnholed(ILGenerator il, LocalBuilder indexLocal, EmittedRuntime runtime)
    {
        var notHoleLabel = il.DefineLabel();
        var doneLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Item").GetGetMethod()!);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Isinst, runtime.ArrayHoleType);
        il.Emit(OpCodes.Brfalse, notHoleLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Br, doneLabel);
        il.MarkLabel(notHoleLabel);
        il.MarkLabel(doneLabel);
    }

    /// <summary>
    /// Emits IL to create callback args array [list[i], (double)i, list],
    /// invoke the callback via InvokeMethodValue with undefined thisArg,
    /// and leave the result on the stack.
    /// Expects: arg0 = list (List&lt;object&gt;), arg1 = callback (object).
    /// </summary>
    /// <remarks>
    /// Must dispatch through <see cref="EmittedRuntime.InvokeMethodValue"/> (not
    /// <see cref="EmittedRuntime.InvokeValue"/>) so that callbacks compiled from
    /// <c>function(…){…}</c> expressions (which carry a synthetic <c>__this</c>
    /// first parameter) have the <c>__this</c> slot filled by the runtime rather
    /// than absorbing the first real argument (list[i]) into it. ES spec: when
    /// no <c>thisArg</c> is supplied to forEach/map/filter/etc., the callback's
    /// <c>this</c> is undefined in strict mode, which we represent as null.
    ///
    /// Stage E.2 M4: args[0] is unholed so callbacks never see the $ArrayHole
    /// sentinel. Callers that want to skip holes entirely should call
    /// <see cref="EmitSkipIfHole"/> BEFORE this helper — the unhole here is
    /// for find/etc. which must still invoke the callback on holes (with
    /// undefined as the element).
    /// </remarks>
    private void EmitCallbackArgsAndInvoke(ILGenerator il, LocalBuilder indexLocal, EmittedRuntime runtime)
    {
        // var args = new object[] { list[i]-unholed, (double)i, list }
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, _types.Object);

        // args[0] = unholed list[i]
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        EmitLoadElementUnholed(il, indexLocal, runtime);
        il.Emit(OpCodes.Stelem_Ref);

        // args[1] = (double)i
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Stelem_Ref);

        // args[2] = $Runtime._currentArrayLikeReceiver ?? list
        // Per ECMA-262, the callback's 3rd arg (O in the spec) is ToObject(this).
        // When the pattern matcher rewrites `Array.prototype.X.call(receiver, ...)`
        // into a helper call, it sets the thread-static field to the original
        // receiver — we pick that up here. Direct `arr.forEach(cb)` leaves the
        // field null; we fall back to the List (legacy behavior).
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldsfld, runtime.CurrentArrayLikeReceiverField);
        var useOriginalLabel = il.DefineLabel();
        var afterReceiverLabel = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, useOriginalLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Br, afterReceiverLabel);
        il.MarkLabel(useOriginalLabel);
        // original is on the stack
        il.MarkLabel(afterReceiverLabel);
        il.Emit(OpCodes.Stelem_Ref);

        // InvokeMethodValue(thisArg, callback, args). thisArg comes from the
        // _currentCallbackThisArg thread-static (set by ArrayEmitter when the
        // user passes a 2nd arg to forEach/map/etc, e.g. `arr.forEach(cb, ctx)`).
        var argsLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Stloc, argsLocal);
        il.Emit(OpCodes.Ldsfld, runtime.CurrentCallbackThisArgField);
        il.Emit(OpCodes.Ldarg_1); // callback
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Call, runtime.InvokeMethodValue);
    }

    private void EmitArrayMap(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayMap",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.ListOfObject, _types.Object]
        );
        runtime.ArrayMap = method;

        var il = method.GetILGenerator();
        EmitThrowIfCallbackNotCallable(il, runtime, 1, "Array.prototype.map");

        // var result = new List<object>()
        var resultLocal = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.EmptyTypes));
        il.Emit(OpCodes.Stloc, resultLocal);

        // var i = 0
        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var holeLabel = il.DefineLabel();
        var addedLabel = il.DefineLabel();

        // Loop: for (int i = 0; i < list.Count; i++)
        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // ECMA-262 23.1.3.19: map SKIPS the callback on holes but PRESERVES
        // the hole in the output at the same position. So a `map` over
        // `[1, hole, 3]` yields `[fn(1), hole, fn(3)]`, not `[fn(1), fn(3)]`.
        EmitSkipIfHole(il, indexLocal, holeLabel, runtime);

        EmitCallbackArgsAndInvoke(il, indexLocal, runtime);

        // Store the call result
        var callResultLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, callResultLocal);

        // result.Add(callResult)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, callResultLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));
        il.Emit(OpCodes.Br, addedLabel);

        il.MarkLabel(holeLabel);
        // Hole-preserving output: push $ArrayHole.Instance to the result list.
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldsfld, runtime.ArrayHoleInstance);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));

        il.MarkLabel(addedLabel);
        // i++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    private void EmitArrayFilter(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayFilter",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.ListOfObject, _types.Object]
        );
        runtime.ArrayFilter = method;

        var il = method.GetILGenerator();
        EmitThrowIfCallbackNotCallable(il, runtime, 1, "Array.prototype.filter");

        // var result = new List<object>()
        var resultLocal = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.EmptyTypes));
        il.Emit(OpCodes.Stloc, resultLocal);

        // var i = 0
        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var skipAdd = il.DefineLabel();

        // Loop: for (int i = 0; i < list.Count; i++)
        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // ECMA-262 23.1.3.8: filter skips holes (callback not invoked, element
        // not copied into output — filter densifies).
        EmitSkipIfHole(il, indexLocal, skipAdd, runtime);

        EmitCallbackArgsAndInvoke(il, indexLocal, runtime);

        // Call IsTruthy
        il.Emit(OpCodes.Call, runtime.IsTruthy);

        // if (!truthy) skip add
        il.Emit(OpCodes.Brfalse, skipAdd);

        // result.Add(list[i])
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Item").GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));

        il.MarkLabel(skipAdd);

        // i++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    private void EmitArrayForEach(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayForEach",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.ListOfObject, _types.Object]
        );
        runtime.ArrayForEach = method;

        var il = method.GetILGenerator();
        EmitThrowIfCallbackNotCallable(il, runtime, 1, "Array.prototype.forEach");

        // var i = 0
        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var advance = il.DefineLabel();

        // Loop: for (int i = 0; i < list.Count; i++)
        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // ECMA-262 23.1.3.15: forEach skips holes.
        EmitSkipIfHole(il, indexLocal, advance, runtime);

        EmitCallbackArgsAndInvoke(il, indexLocal, runtime);
        il.Emit(OpCodes.Pop); // Discard result

        il.MarkLabel(advance);
        // i++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ret);
    }

    private void EmitArrayFind(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayFind",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ListOfObject, _types.Object]
        );
        runtime.ArrayFind = method;

        var il = method.GetILGenerator();

        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        EmitCallbackArgsAndInvoke(il, indexLocal, runtime);

        // if (IsTruthy(result)) return list[i] (unholed — spec: find returns
        // `undefined` when the matched slot is a hole, not the sentinel).
        il.Emit(OpCodes.Call, runtime.IsTruthy);
        var notFound = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notFound);
        EmitLoadElementUnholed(il, indexLocal, runtime);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notFound);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        // ECMA-262 23.1.3.10 Array.prototype.find: return undefined when no element matches.
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Ret);
    }

    private void EmitArrayFindIndex(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayFindIndex",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.ListOfObject, _types.Object]
        );
        runtime.ArrayFindIndex = method;

        var il = method.GetILGenerator();

        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        EmitCallbackArgsAndInvoke(il, indexLocal, runtime);
        il.Emit(OpCodes.Call, runtime.IsTruthy);

        var notFound = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notFound);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notFound);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldc_R8, -1.0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitArraySome(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArraySome",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,  // Return boxed bool to match ILEmitter expectations
            [_types.ListOfObject, _types.Object]
        );
        runtime.ArraySome = method;

        var il = method.GetILGenerator();
        EmitThrowIfCallbackNotCallable(il, runtime, 1, "Array.prototype.some");

        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var advance = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // ECMA-262 23.1.3.29: some skips holes.
        EmitSkipIfHole(il, indexLocal, advance, runtime);

        EmitCallbackArgsAndInvoke(il, indexLocal, runtime);
        il.Emit(OpCodes.Call, runtime.IsTruthy);

        il.Emit(OpCodes.Brfalse, advance);
        il.Emit(OpCodes.Ldc_I4_1); // return true
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(advance);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldc_I4_0); // return false
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);
    }

    private void EmitArrayEvery(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayEvery",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,  // Return boxed bool to match ILEmitter expectations
            [_types.ListOfObject, _types.Object]
        );
        runtime.ArrayEvery = method;

        var il = method.GetILGenerator();
        EmitThrowIfCallbackNotCallable(il, runtime, 1, "Array.prototype.every");

        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var continueLoop = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // ECMA-262 23.1.3.6: every skips holes (callback not invoked; doesn't
        // affect "all match" result).
        EmitSkipIfHole(il, indexLocal, continueLoop, runtime);

        EmitCallbackArgsAndInvoke(il, indexLocal, runtime);
        il.Emit(OpCodes.Call, runtime.IsTruthy);

        il.Emit(OpCodes.Brtrue, continueLoop);
        il.Emit(OpCodes.Ldc_I4_0); // return false
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(continueLoop);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldc_I4_1); // return true
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);
    }

    private void EmitArrayFindLast(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayFindLast",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ListOfObject, _types.Object]
        );
        runtime.ArrayFindLast = method;

        var il = method.GetILGenerator();

        // int i = list.Count - 1
        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        // Loop: for (int i = list.Count - 1; i >= 0; i--)
        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, loopEnd);

        EmitCallbackArgsAndInvoke(il, indexLocal, runtime);

        // if (IsTruthy(result)) return list[i] (unholed).
        il.Emit(OpCodes.Call, runtime.IsTruthy);
        var notFound = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notFound);
        EmitLoadElementUnholed(il, indexLocal, runtime);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notFound);
        // i--
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        // ECMA-262 23.1.3.11 Array.prototype.findLast: return undefined when no element matches.
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Ret);
    }

    private void EmitArrayFindLastIndex(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayFindLastIndex",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.ListOfObject, _types.Object]
        );
        runtime.ArrayFindLastIndex = method;

        var il = method.GetILGenerator();

        // int i = list.Count - 1
        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        // Loop: for (int i = list.Count - 1; i >= 0; i--)
        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, loopEnd);

        EmitCallbackArgsAndInvoke(il, indexLocal, runtime);
        il.Emit(OpCodes.Call, runtime.IsTruthy);

        var notFound = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notFound);
        // Return index as double
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notFound);
        // i--
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        // Return -1
        il.Emit(OpCodes.Ldc_R8, -1.0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitArrayReduce(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayReduce",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ListOfObject, _types.ObjectArray]
        );
        runtime.ArrayReduce = method;

        var il = method.GetILGenerator();

        // args[0] = callback, args[1] = initial value (optional)
        var accLocal = il.DeclareLocal(_types.Object);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var callbackLocal = il.DeclareLocal(_types.Object);

        // ECMA-262 23.1.3.21: throw TypeError if callback is missing or not callable.
        // Check args.Length > 0 FIRST — otherwise Ldelem_Ref throws IndexOutOfRange
        // for `Array.prototype.reduce.call(obj)` with zero args, which we want to
        // surface as a TypeError matching spec (and what the caller's assert.throws
        // expects).
        var reduceCallableOk = il.DefineLabel();
        var reduceCallableThrow = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Brfalse, reduceCallableThrow);

        // callback = args[0]
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, callbackLocal);

        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Brfalse, reduceCallableThrow);
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, reduceCallableThrow);
        // Positive callable check — bool/number/string/dict/list/etc. all
        // throw TypeError per ECMA-262 IsCallable. Mirrors
        // EmitThrowIfCallbackNotCallable used by every/filter/map.
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, reduceCallableOk);
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Isinst, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Brtrue, reduceCallableOk);
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Isinst, runtime.FunctionBindWrapperType);
        il.Emit(OpCodes.Brtrue, reduceCallableOk);
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Isinst, runtime.FunctionCallWrapperType);
        il.Emit(OpCodes.Brtrue, reduceCallableOk);
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Isinst, runtime.FunctionApplyWrapperType);
        il.Emit(OpCodes.Brtrue, reduceCallableOk);
        il.Emit(OpCodes.Br, reduceCallableThrow);
        il.MarkLabel(reduceCallableThrow);
        il.Emit(OpCodes.Ldstr, "Array.prototype.reduce callback is not callable");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(reduceCallableOk);

        // Check if initial value provided (args.Length > 1)
        var hasInitial = il.DefineLabel();
        var noInitial = il.DefineLabel();
        var startLoop = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Bgt, hasInitial);

        // No initial value: scan forward for first PRESENT (non-hole) element.
        // ECMA-262 23.1.3.24: "If no initial value was provided, the first
        // present element is used as the initial acc and kPresent tracking
        // starts from there; TypeError if the array has no present elements."
        var scanLocal = il.DeclareLocal(_types.Int32);
        var scanStart = il.DefineLabel();
        var scanEnd = il.DefineLabel();
        var scanFound = il.DefineLabel();
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, scanLocal);
        il.MarkLabel(scanStart);
        il.Emit(OpCodes.Ldloc, scanLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, scanEnd);
        // if (!(list[scan] is ArrayHole)) goto found
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, scanLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Item").GetGetMethod()!);
        il.Emit(OpCodes.Isinst, runtime.ArrayHoleType);
        il.Emit(OpCodes.Brfalse, scanFound);
        il.Emit(OpCodes.Ldloc, scanLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, scanLocal);
        il.Emit(OpCodes.Br, scanStart);

        il.MarkLabel(scanEnd);
        // No present element — TypeError per spec. Build a real $TypeError
        // instance (not a raw .NET Exception) so test262 patterns like
        // `e instanceof TypeError` and `e.constructor.name === 'TypeError'` hold.
        il.Emit(OpCodes.Ldstr, "Reduce of empty array with no initial value");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(scanFound);
        // acc = list[scan]; i = scan + 1;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, scanLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Item").GetGetMethod()!);
        il.Emit(OpCodes.Stloc, accLocal);
        il.Emit(OpCodes.Ldloc, scanLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, startLoop);

        il.MarkLabel(hasInitial);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, accLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        il.MarkLabel(startLoop);
        var loopEnd = il.DefineLabel();
        var reduceAdvance = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // Skip holes per spec.
        EmitSkipIfHole(il, indexLocal, reduceAdvance, runtime);

        // Create args: [acc, list[i], i, list]
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Newarr, _types.Object);

        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, accLocal);
        il.Emit(OpCodes.Stelem_Ref);

        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Item").GetGetMethod()!);
        il.Emit(OpCodes.Stelem_Ref);

        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Stelem_Ref);

        // args[3] = $Runtime._currentArrayLikeReceiver ?? list (mirror the
        // EmitCallbackArgsAndInvoke logic — when the pattern matcher invoked
        // reduce via Array.prototype.reduce.call(receiver, ...), we want the
        // callback's 4th arg to be the original receiver per ECMA-262).
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Ldsfld, runtime.CurrentArrayLikeReceiverField);
        var redUseOriginal = il.DefineLabel();
        var redAfterReceiver = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, redUseOriginal);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Br, redAfterReceiver);
        il.MarkLabel(redUseOriginal);
        il.MarkLabel(redAfterReceiver);
        il.Emit(OpCodes.Stelem_Ref);

        var argsLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Stloc, argsLocal);

        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Call, runtime.InvokeValue);
        il.Emit(OpCodes.Stloc, accLocal);

        il.MarkLabel(reduceAdvance);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, startLoop);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldloc, accLocal);
        il.Emit(OpCodes.Ret);
    }

    private void EmitArrayReduceRight(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayReduceRight",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ListOfObject, _types.ObjectArray]
        );
        runtime.ArrayReduceRight = method;

        var il = method.GetILGenerator();

        // args[0] = callback, args[1] = initial value (optional)
        var accLocal = il.DeclareLocal(_types.Object);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var callbackLocal = il.DeclareLocal(_types.Object);

        // ECMA-262 23.1.3.22: throw TypeError if callback is missing or not callable.
        var reduceRCallableOk = il.DefineLabel();
        var reduceRCallableThrow = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Brfalse, reduceRCallableThrow);

        // callback = args[0]
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, callbackLocal);

        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Brfalse, reduceRCallableThrow);
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, reduceRCallableThrow);
        // Positive callable check (mirrors reduce / EmitThrowIfCallbackNotCallable).
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, reduceRCallableOk);
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Isinst, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Brtrue, reduceRCallableOk);
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Isinst, runtime.FunctionBindWrapperType);
        il.Emit(OpCodes.Brtrue, reduceRCallableOk);
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Isinst, runtime.FunctionCallWrapperType);
        il.Emit(OpCodes.Brtrue, reduceRCallableOk);
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Isinst, runtime.FunctionApplyWrapperType);
        il.Emit(OpCodes.Brtrue, reduceRCallableOk);
        il.Emit(OpCodes.Br, reduceRCallableThrow);
        il.MarkLabel(reduceRCallableThrow);
        il.Emit(OpCodes.Ldstr, "Array.prototype.reduceRight callback is not callable");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(reduceRCallableOk);

        // Check if initial value provided (args.Length > 1)
        var hasInitial = il.DefineLabel();
        var noInitial = il.DefineLabel();
        var startLoop = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Bgt, hasInitial);

        // No initial value: scan BACKWARD for last PRESENT (non-hole) element.
        // ECMA-262 23.1.3.25 (symmetric to reduce).
        var scanLocal = il.DeclareLocal(_types.Int32);
        var scanStart = il.DefineLabel();
        var scanEnd = il.DefineLabel();
        var scanFound = il.DefineLabel();
        // scan = list.Count - 1
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, scanLocal);
        il.MarkLabel(scanStart);
        il.Emit(OpCodes.Ldloc, scanLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, scanEnd);
        // if (!(list[scan] is ArrayHole)) goto found
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, scanLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Item").GetGetMethod()!);
        il.Emit(OpCodes.Isinst, runtime.ArrayHoleType);
        il.Emit(OpCodes.Brfalse, scanFound);
        il.Emit(OpCodes.Ldloc, scanLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, scanLocal);
        il.Emit(OpCodes.Br, scanStart);

        il.MarkLabel(scanEnd);
        // No present element — TypeError per spec. Throw a real $TypeError
        // instance (parity with EmitArrayReduce).
        il.Emit(OpCodes.Ldstr, "Reduce of empty array with no initial value");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(scanFound);
        // acc = list[scan]; i = scan - 1;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, scanLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Item").GetGetMethod()!);
        il.Emit(OpCodes.Stloc, accLocal);
        il.Emit(OpCodes.Ldloc, scanLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, startLoop);

        il.MarkLabel(hasInitial);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, accLocal);
        // startIndex = list.Count - 1
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, indexLocal);

        il.MarkLabel(startLoop);
        var loopEnd = il.DefineLabel();
        var reduceRightAdvance = il.DefineLabel();

        // Loop: for (int i = startIndex; i >= 0; i--)
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, loopEnd);

        // Skip holes per spec (symmetric to reduce).
        EmitSkipIfHole(il, indexLocal, reduceRightAdvance, runtime);

        // Create args: [acc, list[i], i, list]
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Newarr, _types.Object);

        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, accLocal);
        il.Emit(OpCodes.Stelem_Ref);

        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Item").GetGetMethod()!);
        il.Emit(OpCodes.Stelem_Ref);

        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Stelem_Ref);

        // args[3] = $Runtime._currentArrayLikeReceiver ?? list (per ECMA-262
        // the callback's 4th arg is the original receiver, not the materialized
        // temp list; mirrors the EmitCallbackArgsAndInvoke + ArrayReduce fix).
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Ldsfld, runtime.CurrentArrayLikeReceiverField);
        var rrUseOriginal = il.DefineLabel();
        var rrAfterReceiver = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, rrUseOriginal);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Br, rrAfterReceiver);
        il.MarkLabel(rrUseOriginal);
        il.MarkLabel(rrAfterReceiver);
        il.Emit(OpCodes.Stelem_Ref);

        var argsLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Stloc, argsLocal);

        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Call, runtime.InvokeValue);
        il.Emit(OpCodes.Stloc, accLocal);

        il.MarkLabel(reduceRightAdvance);
        // i--
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, startLoop);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldloc, accLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ArrayEntries: returns an iterator yielding [index, value] pairs.
    /// </summary>
    private void EmitArrayEntries(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayEntries",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ListOfObject]
        );
        runtime.ArrayEntries = method;

        var il = method.GetILGenerator();

        // Create result list
        var resultLocal = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.EmptyTypes));
        il.Emit(OpCodes.Stloc, resultLocal);

        // Loop index
        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        // Loop: for (int i = 0; i < list.Count; i++)
        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // Create pair array: [index, value]
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);

        // pair[0] = (double)index
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Stelem_Ref);

        // pair[1] = list[index]
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Item").GetGetMethod()!);
        il.Emit(OpCodes.Stelem_Ref);

        // Create List<object> from array and add to result
        var pairArrayLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Stloc, pairArrayLocal);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, pairArrayLocal);
        // new List<object>(array)
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, typeof(IEnumerable<object>)));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));

        // i++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ArrayKeys: returns an iterator yielding array indices.
    /// </summary>
    private void EmitArrayKeys(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayKeys",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ListOfObject]
        );
        runtime.ArrayKeys = method;

        var il = method.GetILGenerator();

        // Create result list
        var resultLocal = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.EmptyTypes));
        il.Emit(OpCodes.Stloc, resultLocal);

        // Loop index
        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        // Loop: for (int i = 0; i < list.Count; i++)
        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // result.Add((double)i)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));

        // i++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ArrayValues: returns an iterator yielding array elements.
    /// </summary>
    private void EmitArrayValues(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayValues",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ListOfObject]
        );
        runtime.ArrayValues = method;

        var il = method.GetILGenerator();

        // Return NormalizeToEnumerator(list) — returns a stateful IEnumerator<object>
        // that supports both for...of iteration and iterator protocol (.next())
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.NormalizeToEnumerator);
        il.Emit(OpCodes.Ret);
    }
}

