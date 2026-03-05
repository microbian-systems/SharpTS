using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits the $Writable class for standalone stream support.
/// NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSWritable
/// </summary>
public partial class RuntimeEmitter
{
    // $WriteCallbackWrapper fields
    private TypeBuilder _tsWriteCallbackWrapperType = null!;
    private ConstructorBuilder _tsWriteCallbackWrapperCtor = null!;
    private FieldBuilder _tsWriteCallbackWrapperUserCallbackField = null!;

    // $Writable fields
    private FieldBuilder _tsWritableWritableField = null!;
    private FieldBuilder _tsWritableEndedField = null!;
    private FieldBuilder _tsWritableFinishedField = null!;
    private FieldBuilder _tsWritableDestroyedField = null!;
    private FieldBuilder _tsWritableCorkedField = null!;
    private FieldBuilder _tsWritableCorkBufferField = null!;
    private FieldBuilder _tsWritableWriteCallbackField = null!;
    private FieldBuilder _tsWritableFinalCallbackField = null!;

    /// <summary>
    /// Emits the $WriteCallbackWrapper helper class.
    /// This wraps the user-provided callback (or null) so that stream write handlers
    /// always receive a callable "done" callback as their third argument.
    /// Matches the interpreter's WriteCallbackWrapper behavior.
    /// </summary>
    private void EmitTSWriteCallbackWrapperClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "$WriteCallbackWrapper",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object
        );
        _tsWriteCallbackWrapperType = typeBuilder;
        runtime.WriteCallbackWrapperType = typeBuilder;

        // Field: _userCallback (object, may be null)
        _tsWriteCallbackWrapperUserCallbackField = typeBuilder.DefineField(
            "_userCallback", _types.Object, FieldAttributes.Private);

        // Constructor: public $WriteCallbackWrapper(object userCallback)
        var ctorBuilder = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.Object]
        );
        _tsWriteCallbackWrapperCtor = ctorBuilder;
        runtime.WriteCallbackWrapperCtor = ctorBuilder;

        var ctorIL = ctorBuilder.GetILGenerator();
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.Object));
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Stfld, _tsWriteCallbackWrapperUserCallbackField);
        ctorIL.Emit(OpCodes.Ret);

        // Invoke method: public object Invoke(object[] args)
        // Called when user code does callback() or callback(error).
        // Calls the original user callback with no args (matching Node.js behavior).
        var invokeBuilder = typeBuilder.DefineMethod(
            "Invoke",
            MethodAttributes.Public,
            _types.Object,
            [_types.ObjectArray]
        );
        runtime.WriteCallbackWrapperInvoke = invokeBuilder;

        var invokeIL = invokeBuilder.GetILGenerator();
        var noCallbackLabel = invokeIL.DefineLabel();

        // if (_userCallback != null && _userCallback is $TSFunction)
        invokeIL.Emit(OpCodes.Ldarg_0);
        invokeIL.Emit(OpCodes.Ldfld, _tsWriteCallbackWrapperUserCallbackField);
        invokeIL.Emit(OpCodes.Brfalse, noCallbackLabel);
        invokeIL.Emit(OpCodes.Ldarg_0);
        invokeIL.Emit(OpCodes.Ldfld, _tsWriteCallbackWrapperUserCallbackField);
        invokeIL.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        invokeIL.Emit(OpCodes.Brfalse, noCallbackLabel);

        // _userCallback.Invoke([])
        invokeIL.Emit(OpCodes.Ldarg_0);
        invokeIL.Emit(OpCodes.Ldfld, _tsWriteCallbackWrapperUserCallbackField);
        invokeIL.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        invokeIL.Emit(OpCodes.Ldc_I4_0);
        invokeIL.Emit(OpCodes.Newarr, _types.Object);
        invokeIL.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        invokeIL.Emit(OpCodes.Pop);

        invokeIL.MarkLabel(noCallbackLabel);
        invokeIL.Emit(OpCodes.Ldnull);
        invokeIL.Emit(OpCodes.Ret);

        typeBuilder.CreateType();
    }

    private void EmitTSWritableClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        // Emit the helper callback wrapper class first
        EmitTSWriteCallbackWrapperClass(moduleBuilder, runtime);

        // Define class: public class $Writable : $EventEmitter
        var typeBuilder = moduleBuilder.DefineType(
            "$Writable",
            TypeAttributes.Public | TypeAttributes.BeforeFieldInit,
            runtime.TSEventEmitterType  // Extends $EventEmitter
        );
        runtime.TSWritableType = typeBuilder;

        // Define fields
        _tsWritableWritableField = typeBuilder.DefineField("_writable", _types.Boolean, FieldAttributes.Private);
        _tsWritableEndedField = typeBuilder.DefineField("_ended", _types.Boolean, FieldAttributes.Private);
        _tsWritableFinishedField = typeBuilder.DefineField("_finished", _types.Boolean, FieldAttributes.Private);
        _tsWritableDestroyedField = typeBuilder.DefineField("_destroyed", _types.Boolean, FieldAttributes.Private);
        _tsWritableCorkedField = typeBuilder.DefineField("_corked", _types.Boolean, FieldAttributes.Private);

        var listType = _types.ListOfObject;
        _tsWritableCorkBufferField = typeBuilder.DefineField("_corkBuffer", listType, FieldAttributes.Private);
        _tsWritableWriteCallbackField = typeBuilder.DefineField("_writeCallback", _types.Object, FieldAttributes.Private);
        _tsWritableFinalCallbackField = typeBuilder.DefineField("_finalCallback", _types.Object, FieldAttributes.Private);

        // Constructor
        EmitTSWritableCtor(typeBuilder, runtime);

        // Methods (Cork/Uncork before End, since End calls Uncork)
        EmitTSWritableWrite(typeBuilder, runtime);
        EmitTSWritableCork(typeBuilder, runtime);
        EmitTSWritableUncork(typeBuilder, runtime);
        EmitTSWritableEnd(typeBuilder, runtime);
        EmitTSWritableDestroy(typeBuilder, runtime);
        EmitTSWritableSetDefaultEncoding(typeBuilder, runtime);

        // Setter methods for callbacks
        EmitTSWritableSetWriteCallback(typeBuilder, runtime);
        EmitTSWritableSetFinalCallback(typeBuilder, runtime);

        // Property getters
        EmitTSWritablePropertyGetters(typeBuilder, runtime);

        typeBuilder.CreateType();
    }

    private void EmitTSWritableCtor(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes
        );
        runtime.TSWritableCtor = ctor;

        var il = ctor.GetILGenerator();

        // Call base constructor ($EventEmitter)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.TSEventEmitterCtor);

        // _writable = true
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tsWritableWritableField);

        // _ended = false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsWritableEndedField);

        // _finished = false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsWritableFinishedField);

        // _destroyed = false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsWritableDestroyedField);

        // _corked = false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsWritableCorkedField);

        // _corkBuffer = new List<object?>()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stfld, _tsWritableCorkBufferField);

        il.Emit(OpCodes.Ret);
    }

    private void EmitTSWritableWrite(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public bool Write(object? chunk, object? encoding, object? callback)
        var method = typeBuilder.DefineMethod(
            "Write",
            MethodAttributes.Public,
            _types.Boolean,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.TSWritableWrite = method;

        var il = method.GetILGenerator();
        var returnFalseLabel = il.DefineLabel();
        var callCallbackLabel = il.DefineLabel();
        var notCorkedLabel = il.DefineLabel();

        // if (_destroyed || _ended) return false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableDestroyedField);
        il.Emit(OpCodes.Brtrue, returnFalseLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableEndedField);
        il.Emit(OpCodes.Brtrue, returnFalseLabel);

        // if (_corked) { _corkBuffer.Add(new object[] { chunk, encoding, callback }); return false; }
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableCorkedField);
        il.Emit(OpCodes.Brfalse, notCorkedLabel);

        // Buffer the write: _corkBuffer.Add(new object[] { chunk, encoding, callback })
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableCorkBufferField);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1); // chunk
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_2); // encoding
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldarg_3); // callback
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);
        il.Emit(OpCodes.Ldc_I4_0); // return false (matches interpreter)
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notCorkedLabel);

        // If _writeCallback is set, invoke it
        var noCallbackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableWriteCallbackField);
        il.Emit(OpCodes.Brfalse, noCallbackLabel);

        // Call _writeCallback with (chunk, encoding, done_callback)
        // For simplicity, we invoke via $TSFunction.Invoke if it's a function
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableWriteCallbackField);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, noCallbackLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableWriteCallbackField);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);

        // Load 'this' (the stream) for InvokeWithThis
        il.Emit(OpCodes.Ldarg_0);

        // Create args array: [chunk, encoding ?? "utf8", callback_wrapper]
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1); // chunk
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        // encoding ?? "utf8"
        var hasEncodingLabel = il.DefineLabel();
        var afterEncodingLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brtrue, hasEncodingLabel);
        il.Emit(OpCodes.Ldstr, "utf8");
        il.Emit(OpCodes.Br, afterEncodingLabel);
        il.MarkLabel(hasEncodingLabel);
        il.Emit(OpCodes.Ldarg_2);
        il.MarkLabel(afterEncodingLabel);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        // Wrap the callback in $WriteCallbackWrapper so the user's write handler
        // always receives a callable "done" function (matching Node.js behavior)
        il.Emit(OpCodes.Ldarg_3); // callback (may be null)
        il.Emit(OpCodes.Newobj, _tsWriteCallbackWrapperCtor);
        il.Emit(OpCodes.Stelem_Ref);

        // Call InvokeWithThis(this, args) instead of Invoke(args)
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvokeWithThis);
        il.Emit(OpCodes.Pop);

        // return true
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(noCallbackLabel);
        // Default: just accept the data, call user callback if provided
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Brfalse, callCallbackLabel);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, callCallbackLabel);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(callCallbackLabel);
        // return true
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnFalseLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSWritableEnd(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public $Writable End(object? chunk, object? encoding, object? callback)
        var method = typeBuilder.DefineMethod(
            "End",
            MethodAttributes.Public,
            typeBuilder,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.TSWritableEnd = method;

        var il = method.GetILGenerator();
        var alreadyEndedLabel = il.DefineLabel();
        var noChunkLabel = il.DefineLabel();
        var noFinalCallbackLabel = il.DefineLabel();

        // if (_ended) return this
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableEndedField);
        il.Emit(OpCodes.Brtrue, alreadyEndedLabel);

        // Write final chunk BEFORE setting _ended (Write() rejects when _ended is true)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, noChunkLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Callvirt, runtime.TSWritableWrite);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(noChunkLabel);

        // _ended = true; _writable = false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tsWritableEndedField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsWritableWritableField);

        // Flush cork buffer if corked
        var notCorkedLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableCorkedField);
        il.Emit(OpCodes.Brfalse, notCorkedLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, runtime.TSWritableUncork);
        il.MarkLabel(notCorkedLabel);

        // Invoke _finalCallback if set
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableFinalCallbackField);
        il.Emit(OpCodes.Brfalse, noFinalCallbackLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableFinalCallbackField);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, noFinalCallbackLabel);

        // _finalCallback.InvokeWithThis(this, [new $WriteCallbackWrapper(null)])
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableFinalCallbackField);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldarg_0); // this
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldnull); // no user callback
        il.Emit(OpCodes.Newobj, _tsWriteCallbackWrapperCtor);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvokeWithThis);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(noFinalCallbackLabel);

        // _finished = true
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tsWritableFinishedField);

        // emit 'finish' event
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "finish");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Call, runtime.TSEventEmitterEmit);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(alreadyEndedLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSWritableCork(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public void Cork()
        var method = typeBuilder.DefineMethod(
            "Cork",
            MethodAttributes.Public,
            _types.Void,
            Type.EmptyTypes
        );
        runtime.TSWritableCork = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tsWritableCorkedField);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSWritableUncork(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public void Uncork()
        var method = typeBuilder.DefineMethod(
            "Uncork",
            MethodAttributes.Public,
            _types.Void,
            Type.EmptyTypes
        );
        runtime.TSWritableUncork = method;

        var il = method.GetILGenerator();
        var notCorkedLabel = il.DefineLabel();

        // if (!_corked) return
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableCorkedField);
        il.Emit(OpCodes.Brfalse, notCorkedLabel);

        // _corked = false (must be set before flushing so Write() calls go through)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsWritableCorkedField);

        // Flush: for (int i = 0; i < _corkBuffer.Count; i++) {
        //   var entry = (object[])_corkBuffer[i];
        //   Write(entry[0], entry[1], entry[2]);
        // }
        var indexLocal = il.DeclareLocal(_types.Int32);
        var entryLocal = il.DeclareLocal(typeof(object[]));
        var loopStartLabel = il.DefineLabel();
        var loopCondLabel = il.DefineLabel();

        // i = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopCondLabel);

        // Loop body
        il.MarkLabel(loopStartLabel);

        // entry = (object[])_corkBuffer[i]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableCorkBufferField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetProperty("Item")!.GetGetMethod()!);
        il.Emit(OpCodes.Castclass, typeof(object[]));
        il.Emit(OpCodes.Stloc, entryLocal);

        // this.Write(entry[0], entry[1], entry[2])
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref); // chunk
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref); // encoding
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldelem_Ref); // callback
        il.Emit(OpCodes.Callvirt, runtime.TSWritableWrite);
        il.Emit(OpCodes.Pop); // discard bool return

        // i++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);

        // Loop condition: i < _corkBuffer.Count
        il.MarkLabel(loopCondLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableCorkBufferField);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Blt, loopStartLabel);

        // _corkBuffer.Clear()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableCorkBufferField);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Clear")!);

        il.MarkLabel(notCorkedLabel);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSWritableDestroy(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public $Writable Destroy(object? error)
        var method = typeBuilder.DefineMethod(
            "Destroy",
            MethodAttributes.Public,
            typeBuilder,
            [_types.Object]
        );
        runtime.TSWritableDestroy = method;

        var il = method.GetILGenerator();
        var alreadyDestroyedLabel = il.DefineLabel();
        var noErrorLabel = il.DefineLabel();

        // if (_destroyed) return this
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableDestroyedField);
        il.Emit(OpCodes.Brtrue, alreadyDestroyedLabel);

        // _destroyed = true; _writable = false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tsWritableDestroyedField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsWritableWritableField);

        // _corkBuffer.Clear()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableCorkBufferField);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Clear")!);

        // if (error != null) emit 'error'
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, noErrorLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "error");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Call, runtime.TSEventEmitterEmit);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(noErrorLabel);
        // emit 'close'
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "close");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Call, runtime.TSEventEmitterEmit);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(alreadyDestroyedLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSWritableSetDefaultEncoding(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public $Writable SetDefaultEncoding(string encoding)
        var method = typeBuilder.DefineMethod(
            "SetDefaultEncoding",
            MethodAttributes.Public,
            typeBuilder,
            [_types.String]
        );
        runtime.TSWritableSetDefaultEncoding = method;

        var il = method.GetILGenerator();
        // Just return this (no-op for compatibility)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSWritableSetWriteCallback(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public void SetWriteCallback(object callback)
        var method = typeBuilder.DefineMethod(
            "SetWriteCallback",
            MethodAttributes.Public,
            _types.Void,
            [_types.Object]
        );

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, _tsWritableWriteCallbackField);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSWritableSetFinalCallback(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public void SetFinalCallback(object callback)
        var method = typeBuilder.DefineMethod(
            "SetFinalCallback",
            MethodAttributes.Public,
            _types.Void,
            [_types.Object]
        );

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, _tsWritableFinalCallbackField);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSWritablePropertyGetters(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // writable property: _writable && !_ended && !_destroyed
        // Note: Use PascalCase getter names (get_Writable) for GetFieldsProperty lookup
        var writableProp = typeBuilder.DefineProperty("Writable", PropertyAttributes.None, _types.Boolean, null);
        var getWritable = typeBuilder.DefineMethod(
            "get_Writable",
            MethodAttributes.Public | MethodAttributes.SpecialName,
            _types.Boolean,
            Type.EmptyTypes
        );
        var il = getWritable.GetILGenerator();
        var falseLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableWritableField);
        il.Emit(OpCodes.Brfalse, falseLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableEndedField);
        il.Emit(OpCodes.Brtrue, falseLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableDestroyedField);
        il.Emit(OpCodes.Brtrue, falseLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(falseLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        writableProp.SetGetMethod(getWritable);

        // writableEnded property
        var writableEndedProp = typeBuilder.DefineProperty("WritableEnded", PropertyAttributes.None, _types.Boolean, null);
        var getWritableEnded = typeBuilder.DefineMethod(
            "get_WritableEnded",
            MethodAttributes.Public | MethodAttributes.SpecialName,
            _types.Boolean,
            Type.EmptyTypes
        );
        il = getWritableEnded.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableEndedField);
        il.Emit(OpCodes.Ret);
        writableEndedProp.SetGetMethod(getWritableEnded);

        // writableFinished property
        var writableFinishedProp = typeBuilder.DefineProperty("WritableFinished", PropertyAttributes.None, _types.Boolean, null);
        var getWritableFinished = typeBuilder.DefineMethod(
            "get_WritableFinished",
            MethodAttributes.Public | MethodAttributes.SpecialName,
            _types.Boolean,
            Type.EmptyTypes
        );
        il = getWritableFinished.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableFinishedField);
        il.Emit(OpCodes.Ret);
        writableFinishedProp.SetGetMethod(getWritableFinished);

        // writableLength property
        var writableLengthProp = typeBuilder.DefineProperty("WritableLength", PropertyAttributes.None, _types.Double, null);
        var getWritableLength = typeBuilder.DefineMethod(
            "get_WritableLength",
            MethodAttributes.Public | MethodAttributes.SpecialName,
            _types.Double,
            Type.EmptyTypes
        );
        il = getWritableLength.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableCorkBufferField);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ret);
        writableLengthProp.SetGetMethod(getWritableLength);

        // destroyed property
        var destroyedProp = typeBuilder.DefineProperty("Destroyed", PropertyAttributes.None, _types.Boolean, null);
        var getDestroyed = typeBuilder.DefineMethod(
            "get_Destroyed",
            MethodAttributes.Public | MethodAttributes.SpecialName,
            _types.Boolean,
            Type.EmptyTypes
        );
        il = getDestroyed.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsWritableDestroyedField);
        il.Emit(OpCodes.Ret);
        destroyedProp.SetGetMethod(getDestroyed);
    }
}
