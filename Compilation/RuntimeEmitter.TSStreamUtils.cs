using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits the $StreamUtils class with static helper methods for stream utilities:
/// Finished, Pipeline, ReadableFrom, PromisePipeline, PromiseFinished.
/// These are emitted into the output assembly for standalone DLL support.
/// </summary>
public partial class RuntimeEmitter
{
    private void EmitTSStreamUtilsClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "$StreamUtils",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit
        );

        EmitStreamFinished(typeBuilder, runtime);
        EmitStreamPipeline(typeBuilder, runtime);
        EmitStreamReadableFrom(typeBuilder, runtime);
        EmitStreamPromisePipeline(typeBuilder, runtime);
        EmitStreamPromiseFinished(typeBuilder, runtime);

        typeBuilder.CreateType();
    }

    /// <summary>
    /// Emits: public static object Finished(object[] args)
    /// Attaches end/finish/error/close listeners, calls callback on first trigger.
    /// </summary>
    private void EmitStreamFinished(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Finished",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.MakeArrayType(_types.Object)]
        );
        runtime.StreamFinished = method;

        var il = method.GetILGenerator();

        // Extract stream from args[0]
        var streamLocal = il.DeclareLocal(runtime.TSEventEmitterType); // local 0: stream (cast to $EventEmitter)
        var callbackLocal = il.DeclareLocal(_types.Object); // local 1: callback

        // Cast args[0] to $EventEmitter
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Castclass, runtime.TSEventEmitterType);
        il.Emit(OpCodes.Stloc, streamLocal);

        // Determine callback position
        var threeArgs = il.DefineLabel();
        var afterCallback = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Bge, threeArgs);

        // 2-arg case: callback = args[1]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, callbackLocal);
        il.Emit(OpCodes.Br, afterCallback);

        // 3-arg case: callback = args[2]
        il.MarkLabel(threeArgs);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, callbackLocal);

        il.MarkLabel(afterCallback);

        // Attach once listeners for each event
        // stream.Once("end", callback)
        EmitOnceCall(il, runtime, streamLocal, callbackLocal, "end");
        EmitOnceCall(il, runtime, streamLocal, callbackLocal, "finish");
        EmitOnceCall(il, runtime, streamLocal, callbackLocal, "error");
        EmitOnceCall(il, runtime, streamLocal, callbackLocal, "close");

        // Return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitOnceCall(ILGenerator il, EmittedRuntime runtime, LocalBuilder stream, LocalBuilder callback, string eventName)
    {
        // stream.Once(eventName, callback) returns the emitter - pop it
        il.Emit(OpCodes.Ldloc, stream);
        il.Emit(OpCodes.Ldstr, eventName);
        il.Emit(OpCodes.Ldloc, callback);
        il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterOnce);
        il.Emit(OpCodes.Pop); // Once returns the emitter, discard it
    }

    /// <summary>
    /// Emits: public static object Pipeline(object[] args)
    /// Pipes streams together. Returns the last stream.
    /// </summary>
    private void EmitStreamPipeline(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Pipeline",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.MakeArrayType(_types.Object)]
        );
        runtime.StreamPipeline = method;

        var il = method.GetILGenerator();

        var argsLocal = il.DeclareLocal(_types.MakeArrayType(_types.Object)); // local 0
        var lengthLocal = il.DeclareLocal(_types.Int32); // local 1
        var indexLocal = il.DeclareLocal(_types.Int32); // local 2

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stloc, argsLocal);

        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, lengthLocal);

        // Loop: for (i = 0; i < length - 1; i++)
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, lengthLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Bge, loopEnd);

        // Cast args[i] to $Readable and call Pipe(args[i+1], null)
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Castclass, runtime.TSReadableType);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Ldnull); // options = null
        il.Emit(OpCodes.Callvirt, runtime.TSReadablePipe);
        il.Emit(OpCodes.Pop); // Pipe returns destination

        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        // Return last stream: args[length - 1]
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Ldloc, lengthLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object ReadableFrom(object[] args)
    /// Creates a Readable from args[0] (array/list), pushes items, pushes null.
    /// </summary>
    private void EmitStreamReadableFrom(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ReadableFrom",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.MakeArrayType(_types.Object)]
        );
        runtime.StreamReadableFrom = method;

        var il = method.GetILGenerator();

        // Create new $Readable instance
        var streamLocal = il.DeclareLocal(runtime.TSReadableType); // local 0
        il.Emit(OpCodes.Newobj, runtime.TSReadableCtor);
        il.Emit(OpCodes.Stloc, streamLocal);

        // Set object mode
        il.Emit(OpCodes.Ldloc, streamLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Call, runtime.TSReadableSetObjectMode);

        // Get iterable from args[0]
        var iterableLocal = il.DeclareLocal(_types.Object); // local 1
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, iterableLocal);

        // If iterable is List<object?>, iterate and push
        var notListLabel = il.DefineLabel();
        var doneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, iterableLocal);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brfalse, notListLabel);

        // Cast to List<object?> and iterate
        var listLocal = il.DeclareLocal(_types.ListOfObject); // local 2
        il.Emit(OpCodes.Ldloc, iterableLocal);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);

        var countLocal = il.DeclareLocal(_types.Int32); // local 3
        var idxLocal = il.DeclareLocal(_types.Int32); // local 4
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, countLocal);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, idxLocal);

        var pushLoop = il.DefineLabel();
        var pushEnd = il.DefineLabel();

        il.MarkLabel(pushLoop);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldloc, countLocal);
        il.Emit(OpCodes.Bge, pushEnd);

        // Push item: stream.Push(list[idx])
        il.Emit(OpCodes.Ldloc, streamLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("get_Item")!);
        il.Emit(OpCodes.Callvirt, runtime.TSReadablePush);
        il.Emit(OpCodes.Pop); // Push returns bool

        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, idxLocal);
        il.Emit(OpCodes.Br, pushLoop);

        il.MarkLabel(pushEnd);
        il.Emit(OpCodes.Br, doneLabel);

        il.MarkLabel(notListLabel);
        // For non-list types, skip

        il.MarkLabel(doneLabel);

        // Push null for EOF
        il.Emit(OpCodes.Ldloc, streamLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Callvirt, runtime.TSReadablePush);
        il.Emit(OpCodes.Pop);

        // Return the stream
        il.Emit(OpCodes.Ldloc, streamLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object PromisePipeline(object[] args)
    /// </summary>
    private void EmitStreamPromisePipeline(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PromisePipeline",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.MakeArrayType(_types.Object)]
        );
        runtime.StreamPromisePipeline = method;

        var il = method.GetILGenerator();

        // Call Pipeline and wrap result
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.StreamPipeline);
        // Pipeline returns an object (the last stream), wrap in Task
        il.Emit(OpCodes.Call, typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(_types.Object));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object PromiseFinished(object[] args)
    /// </summary>
    private void EmitStreamPromiseFinished(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PromiseFinished",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.MakeArrayType(_types.Object)]
        );
        runtime.StreamPromiseFinished = method;

        var il = method.GetILGenerator();

        // Call Finished to set up listeners
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.StreamFinished);
        il.Emit(OpCodes.Pop); // Finished returns null, discard

        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(_types.Object));
        il.Emit(OpCodes.Ret);
    }
}
