using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits Promise executor constructor support: new Promise((resolve, reject) => { ... })
/// </summary>
public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits the $PromiseResolveCallback and $PromiseRejectCallback types.
    /// Must be called before EmitInvokeValue so the callback types are available for dispatch.
    /// </summary>
    private void EmitPromiseCallbackTypes(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        EmitPromiseResolveCallbackType(moduleBuilder, runtime);
        EmitPromiseRejectCallbackType(moduleBuilder, runtime);
    }

    /// <summary>
    /// Emits the PromiseFromExecutor method that creates promises from executor functions.
    /// Must be called after EmitInvokeValue since it depends on runtime.InvokeValue.
    /// </summary>
    private void EmitPromiseExecutorSupport(TypeBuilder runtimeType, EmittedRuntime runtime, ModuleBuilder moduleBuilder)
    {
        // Emit the PromiseFromExecutor method
        EmitPromiseFromExecutorMethod(runtimeType, runtime, runtime.PromiseResolveCallbackType, runtime.PromiseRejectCallbackType);

        // Promise-subclass support (#242): receiver unwrapping + derived-result wrapping
        EmitUnwrapPromiseReceiverMethod(runtimeType, runtime);
        EmitWrapDerivedPromiseResultMethod(runtimeType, runtime);
    }

    /// <summary>
    /// Emits NormalizePromiseList(object iterable) -> object: when the arg is
    /// a List&lt;object?&gt;, returns a copy with $Promise elements (incl. #242
    /// Promise subclasses) replaced by their wrapped Task — the combinator
    /// state machines only test elements for Task&lt;object?&gt;, so without
    /// this a subclass promise element would be treated as an already-resolved
    /// plain value. Non-list args pass through unchanged.
    /// </summary>
    internal void EmitNormalizePromiseList(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "NormalizePromiseList",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.NormalizePromiseListMethod = method;

        var il = method.GetILGenerator();
        var listType = _types.ListOfObject;
        var passThroughLabel = il.DefineLabel();

        var listLocal = il.DeclareLocal(listType);
        var resultLocal = il.DeclareLocal(listType);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var elementLocal = il.DeclareLocal(_types.Object);

        // if (iterable is not List<object?>) return iterable;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, listType);
        il.Emit(OpCodes.Stloc, listLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Brfalse, passThroughLabel);

        // var result = new List<object?>(); for each element: $Promise → .Task
        il.Emit(OpCodes.Newobj, _types.GetConstructor(listType, _types.EmptyTypes));
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var addRawLabel = il.DefineLabel();
        var nextLabel = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Item").GetGetMethod()!);
        il.Emit(OpCodes.Stloc, elementLocal);

        il.Emit(OpCodes.Ldloc, elementLocal);
        il.Emit(OpCodes.Isinst, runtime.TSPromiseType);
        il.Emit(OpCodes.Brfalse, addRawLabel);

        // result.Add(((​$Promise)element).Task)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, elementLocal);
        il.Emit(OpCodes.Castclass, runtime.TSPromiseType);
        il.Emit(OpCodes.Callvirt, runtime.TSPromiseTaskGetter);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(listType, "Add", _types.Object));
        il.Emit(OpCodes.Br, nextLabel);

        // result.Add(element)
        il.MarkLabel(addRawLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, elementLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(listType, "Add", _types.Object));

        il.MarkLabel(nextLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(passThroughLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits UnwrapPromiseReceiver(object receiver) -> Task&lt;object?&gt;:
    /// $Promise instances (including #242 Promise subclasses) yield their
    /// wrapped task; anything else is cast to Task&lt;object?&gt; (matching the
    /// previous inline Castclass that broke for $Promise receivers).
    /// </summary>
    private void EmitUnwrapPromiseReceiverMethod(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "UnwrapPromiseReceiver",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object]
        );
        runtime.UnwrapPromiseReceiverMethod = method;

        var il = method.GetILGenerator();
        var notPromiseObjLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSPromiseType);
        il.Emit(OpCodes.Brfalse, notPromiseObjLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSPromiseType);
        il.Emit(OpCodes.Callvirt, runtime.TSPromiseTaskGetter);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notPromiseObjLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.TaskOfObject);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits WrapDerivedPromiseResult(Task&lt;object?&gt; result, object receiver) -> object:
    /// when the receiver is a $Promise SUBCLASS instance (#242), constructs a
    /// receiver-typed promise around the result task by invoking the
    /// subclass's single-object (executor) constructor reflectively —
    /// PromiseFromExecutor adopts a raw task, so the new instance wraps
    /// `result`. Plain $Promise / Task receivers (and subclasses without a
    /// matching constructor) return the task unchanged. This is the
    /// species-lite step giving subclass-typed then/catch/finally results;
    /// full SpeciesConstructor semantics remain tracked by #221.
    /// </summary>
    private void EmitWrapDerivedPromiseResultMethod(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "WrapDerivedPromiseResult",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.TaskOfObject, _types.Object]
        );
        runtime.WrapDerivedPromiseResultMethod = method;

        var il = method.GetILGenerator();
        var returnResultLabel = il.DefineLabel();
        var typeLocal = il.DeclareLocal(_types.Type);
        var ctorLocal = il.DeclareLocal(typeof(ConstructorInfo));

        // if (receiver is not $Promise) return result;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSPromiseType);
        il.Emit(OpCodes.Brfalse, returnResultLabel);

        // if (receiver.GetType() == typeof($Promise)) return result;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldtoken, runtime.TSPromiseType);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Beq, returnResultLabel);

        // var ctor = receiverType.GetConstructor(new[] { typeof(object) });
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, _types.Object);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.Type.GetMethod("GetConstructor", [typeof(Type[])])!);
        il.Emit(OpCodes.Stloc, ctorLocal);

        // if (ctor == null) return result;
        il.Emit(OpCodes.Ldloc, ctorLocal);
        il.Emit(OpCodes.Brfalse, returnResultLabel);

        // return ctor.Invoke(new object[] { result });
        il.Emit(OpCodes.Ldloc, ctorLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, typeof(ConstructorInfo).GetMethod("Invoke", [typeof(object[])])!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnResultLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the $PromiseResolveCallback type with:
    /// - TaskCompletionSource field
    /// - SettledFlag field (object for locking + bool tracking)
    /// - Constructor(TaskCompletionSource, object settledLock, ref bool settledFlag)
    /// - Invoke(object?[] args) method
    /// </summary>
    private TypeBuilder EmitPromiseResolveCallbackType(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "$PromiseResolveCallback",
            TypeAttributes.Public | TypeAttributes.Sealed,
            _types.Object
        );

        // Fields
        var tcsField = typeBuilder.DefineField("_tcs", typeof(TaskCompletionSource<object?>), FieldAttributes.Private);
        var lockField = typeBuilder.DefineField("_lock", _types.Object, FieldAttributes.Private);
        var settledField = typeBuilder.DefineField("_settled", typeof(bool), FieldAttributes.Private);

        // Constructor: (TaskCompletionSource<object?> tcs, object lockObj)
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [typeof(TaskCompletionSource<object?>), _types.Object]
        );
        {
            var il = ctor.GetILGenerator();
            // Call base constructor
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, _types.Object.GetConstructor(Type.EmptyTypes)!);
            // this._tcs = tcs
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, tcsField);
            // this._lock = lockObj
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Stfld, lockField);
            // this._settled = false
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stfld, settledField);
            il.Emit(OpCodes.Ret);
        }

        // Invoke(object?[] args) method - compatible with TSFunction invocation
        var invokeMethod = typeBuilder.DefineMethod(
            "Invoke",
            MethodAttributes.Public,
            _types.Object,
            [typeof(object[])]
        );
        {
            var il = invokeMethod.GetILGenerator();
            var alreadySettledLabel = il.DefineLabel();
            var endLockLabel = il.DefineLabel();
            var notTaskLabel = il.DefineLabel();
            var endLabel = il.DefineLabel();

            var valueLocal = il.DeclareLocal(_types.Object);
            var tcsLocal = il.DeclareLocal(typeof(TaskCompletionSource<object?>));
            var innerTaskLocal = il.DeclareLocal(_types.TaskOfObject);

            // value = args.Length > 0 ? args[0] : null
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ble, notTaskLabel);  // if args.Length <= 0, jump (using notTaskLabel temporarily)
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldelem_Ref);
            il.Emit(OpCodes.Stloc, valueLocal);
            var afterValueLabel = il.DefineLabel();
            il.Emit(OpCodes.Br, afterValueLabel);
            il.MarkLabel(notTaskLabel);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Stloc, valueLocal);
            il.MarkLabel(afterValueLabel);

            // Load _tcs for later use
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, tcsField);
            il.Emit(OpCodes.Stloc, tcsLocal);

            // lock (_lock) { if (_settled) return; _settled = true; }
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, lockField);
            il.Emit(OpCodes.Call, _types.GetMethod(typeof(Monitor), "Enter", _types.Object));

            // try { if (_settled) goto alreadySettled; _settled = true; } finally { Monitor.Exit(_lock); }
            il.BeginExceptionBlock();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, settledField);
            il.Emit(OpCodes.Brtrue, alreadySettledLabel);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Stfld, settledField);
            il.Emit(OpCodes.Leave, endLockLabel);

            il.MarkLabel(alreadySettledLabel);
            il.Emit(OpCodes.Leave, endLabel);

            il.BeginFinallyBlock();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, lockField);
            il.Emit(OpCodes.Call, _types.GetMethod(typeof(Monitor), "Exit", _types.Object));
            il.EndExceptionBlock();

            il.MarkLabel(endLockLabel);

            // Just call TrySetResult(value) - no flattening for now (simplification)
            il.Emit(OpCodes.Ldloc, tcsLocal);
            il.Emit(OpCodes.Ldloc, valueLocal);
            var trySetResult = typeof(TaskCompletionSource<object?>).GetMethod("TrySetResult")!;
            il.Emit(OpCodes.Callvirt, trySetResult);
            il.Emit(OpCodes.Pop);

            il.MarkLabel(endLabel);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
        }

        typeBuilder.CreateType();
        runtime.PromiseResolveCallbackType = typeBuilder;
        runtime.PromiseResolveCallbackCtor = ctor;
        runtime.PromiseResolveCallbackInvoke = invokeMethod;
        return typeBuilder;
    }

    /// <summary>
    /// Emits the $PromiseRejectCallback type with similar structure to resolve callback.
    /// </summary>
    private TypeBuilder EmitPromiseRejectCallbackType(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "$PromiseRejectCallback",
            TypeAttributes.Public | TypeAttributes.Sealed,
            _types.Object
        );

        // Fields
        var tcsField = typeBuilder.DefineField("_tcs", typeof(TaskCompletionSource<object?>), FieldAttributes.Private);
        var lockField = typeBuilder.DefineField("_lock", _types.Object, FieldAttributes.Private);
        var settledField = typeBuilder.DefineField("_settled", typeof(bool), FieldAttributes.Private);

        // Constructor
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [typeof(TaskCompletionSource<object?>), _types.Object]
        );
        {
            var il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, _types.Object.GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, tcsField);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Stfld, lockField);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stfld, settledField);
            il.Emit(OpCodes.Ret);
        }

        // Invoke method
        var invokeMethod = typeBuilder.DefineMethod(
            "Invoke",
            MethodAttributes.Public,
            _types.Object,
            [typeof(object[])]
        );
        {
            var il = invokeMethod.GetILGenerator();
            var alreadySettledLabel = il.DefineLabel();
            var endLockLabel = il.DefineLabel();
            var endLabel = il.DefineLabel();

            var reasonLocal = il.DeclareLocal(_types.Object);
            var tcsLocal = il.DeclareLocal(typeof(TaskCompletionSource<object?>));

            // reason = args.Length > 0 ? args[0] : null
            var noReasonLabel = il.DefineLabel();
            var afterReasonLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ble, noReasonLabel);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldelem_Ref);
            il.Emit(OpCodes.Stloc, reasonLocal);
            il.Emit(OpCodes.Br, afterReasonLabel);
            il.MarkLabel(noReasonLabel);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Stloc, reasonLocal);
            il.MarkLabel(afterReasonLabel);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, tcsField);
            il.Emit(OpCodes.Stloc, tcsLocal);

            // lock (_lock) { if (_settled) return; _settled = true; }
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, lockField);
            il.Emit(OpCodes.Call, _types.GetMethod(typeof(Monitor), "Enter", _types.Object));

            il.BeginExceptionBlock();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, settledField);
            il.Emit(OpCodes.Brtrue, alreadySettledLabel);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Stfld, settledField);
            il.Emit(OpCodes.Leave, endLockLabel);

            il.MarkLabel(alreadySettledLabel);
            il.Emit(OpCodes.Leave, endLabel);

            il.BeginFinallyBlock();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, lockField);
            il.Emit(OpCodes.Call, _types.GetMethod(typeof(Monitor), "Exit", _types.Object));
            il.EndExceptionBlock();

            il.MarkLabel(endLockLabel);

            // tcs.TrySetException(new Exception(reason?.ToString() ?? "Promise rejected"))
            il.Emit(OpCodes.Ldloc, tcsLocal);
            il.Emit(OpCodes.Ldloc, reasonLocal);
            il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
            var exceptionCtor = _types.GetConstructor(_types.Exception, [_types.String]);
            il.Emit(OpCodes.Newobj, exceptionCtor);
            var trySetException = typeof(TaskCompletionSource<object?>).GetMethod("TrySetException", [typeof(Exception)])!;
            il.Emit(OpCodes.Callvirt, trySetException);
            il.Emit(OpCodes.Pop);

            il.MarkLabel(endLabel);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
        }

        typeBuilder.CreateType();
        runtime.PromiseRejectCallbackType = typeBuilder;
        runtime.PromiseRejectCallbackCtor = ctor;
        runtime.PromiseRejectCallbackInvoke = invokeMethod;
        return typeBuilder;
    }

    /// <summary>
    /// Emits the PromiseFromExecutor(object executor) -> Task<object?> method.
    /// </summary>
    private void EmitPromiseFromExecutorMethod(
        TypeBuilder runtimeType,
        EmittedRuntime runtime,
        TypeBuilder resolveCallbackType,
        TypeBuilder rejectCallbackType)
    {
        var method = runtimeType.DefineMethod(
            "PromiseFromExecutor",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object]
        );
        runtime.PromiseFromExecutor = method;

        var il = method.GetILGenerator();

        // Local variables
        var tcsLocal = il.DeclareLocal(typeof(TaskCompletionSource<object?>));
        var lockLocal = il.DeclareLocal(_types.Object);
        var resolveLocal = il.DeclareLocal(resolveCallbackType);
        var rejectLocal = il.DeclareLocal(rejectCallbackType);
        var argsLocal = il.DeclareLocal(typeof(object[]));

        // Task adoption (#242): a raw Task<object?> in place of an executor is
        // adopted as the promise's task directly. Promise-subclass constructors
        // chain through here (super(executor) → PromiseFromExecutor → base
        // $Promise ctor), so passing a task to that same constructor is the
        // derived-promise construction path used by inherited statics
        // (MyPromise.resolve) and subclass-typed then/catch/finally results.
        var notTaskLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.TaskOfObject);
        il.Emit(OpCodes.Brfalse, notTaskLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.TaskOfObject);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notTaskLabel);

        // TaskCompletionSource<object?> tcs = new TaskCompletionSource<object?>();
        var tcsCtor = typeof(TaskCompletionSource<object?>).GetConstructor([])!;
        il.Emit(OpCodes.Newobj, tcsCtor);
        il.Emit(OpCodes.Stloc, tcsLocal);

        // object lockObj = new object();
        il.Emit(OpCodes.Newobj, _types.Object.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, lockLocal);

        // var resolveCallback = new $PromiseResolveCallback(tcs, lockObj);
        il.Emit(OpCodes.Ldloc, tcsLocal);
        il.Emit(OpCodes.Ldloc, lockLocal);
        il.Emit(OpCodes.Newobj, runtime.PromiseResolveCallbackCtor);
        il.Emit(OpCodes.Stloc, resolveLocal);

        // var rejectCallback = new $PromiseRejectCallback(tcs, lockObj);
        il.Emit(OpCodes.Ldloc, tcsLocal);
        il.Emit(OpCodes.Ldloc, lockLocal);
        il.Emit(OpCodes.Newobj, runtime.PromiseRejectCallbackCtor);
        il.Emit(OpCodes.Stloc, rejectLocal);

        // Create args array [resolveCallback, rejectCallback]
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, resolveLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, rejectLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // try { InvokeValue(executor, args); }
        // catch (Exception ex) { tcs.TrySetException(ex); }
        var exLocal = il.DeclareLocal(_types.Exception);
        var endTryLabel = il.DefineLabel();

        il.BeginExceptionBlock();

        // Call the executor: InvokeValue(executor, args)
        // This invokes the executor function with (resolve, reject) arguments
        il.Emit(OpCodes.Ldarg_0);  // executor
        il.Emit(OpCodes.Ldloc, argsLocal);  // args
        il.Emit(OpCodes.Call, runtime.InvokeValue);
        il.Emit(OpCodes.Pop);  // Discard executor return value

        il.Emit(OpCodes.Leave, endTryLabel);

        // catch (Exception ex)
        il.BeginCatchBlock(_types.Exception);
        il.Emit(OpCodes.Stloc, exLocal);

        // tcs.TrySetException(ex)
        il.Emit(OpCodes.Ldloc, tcsLocal);
        il.Emit(OpCodes.Ldloc, exLocal);
        var trySetException = typeof(TaskCompletionSource<object?>).GetMethod("TrySetException", [typeof(Exception)])!;
        il.Emit(OpCodes.Callvirt, trySetException);
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Leave, endTryLabel);

        il.EndExceptionBlock();
        il.MarkLabel(endTryLabel);

        // return tcs.Task;
        il.Emit(OpCodes.Ldloc, tcsLocal);
        var taskProperty = typeof(TaskCompletionSource<object?>).GetProperty("Task")!.GetGetMethod()!;
        il.Emit(OpCodes.Callvirt, taskProperty);
        il.Emit(OpCodes.Ret);
    }
}
