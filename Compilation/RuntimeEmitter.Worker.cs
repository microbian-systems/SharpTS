using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits Worker Threads support into the compiled assembly.
/// Provides helper methods for SharedArrayBuffer, TypedArrays, Atomics,
/// MessagePort, MessageChannel, and Worker constructors.
/// </summary>
public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits all Worker-related helper methods into the $Runtime class.
    /// </summary>
    private void EmitWorkerHelpers(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        // SharedArrayBuffer constructor helper
        EmitSharedArrayBufferHelper(runtimeType, runtime);

        // ArrayBuffer constructor helper
        EmitArrayBufferHelper(runtimeType, runtime);

        // DataView constructor helper
        EmitDataViewHelper(runtimeType, runtime);

        // TypedArray constructor helpers
        EmitTypedArrayHelpers(runtimeType, runtime);

        // Atomics static methods
        EmitAtomicsHelpers(runtimeType, runtime);

        // MessageChannel constructor helper
        EmitMessageChannelHelper(runtimeType, runtime);

        // Worker constructor helper
        EmitWorkerHelper(runtimeType, runtime);

        // StructuredClone helper
        EmitStructuredCloneHelper(runtimeType, runtime);

        // worker_threads module helpers
        EmitWorkerThreadsModuleHelpers(runtimeType, runtime);
    }

    /// <summary>
    /// Emits helper for creating SharedArrayBuffer.
    /// public static object CreateSharedArrayBuffer(double byteLength)
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitSharedArrayBufferHelper(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "CreateSharedArrayBuffer",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Double]
        );

        var il = method.GetILGenerator();

        // Use reflection to get the type and create instance at runtime
        // var type = Type.GetType("SharpTS.Runtime.Types.SharpTSSharedArrayBuffer, SharpTS");
        // var ctor = type.GetConstructor(new[] { typeof(int) });
        // return ctor.Invoke(new object[] { (int)byteLength });

        var typeLocal = il.DeclareLocal(_types.Type);
        var ctorLocal = il.DeclareLocal(_types.ConstructorInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Get the type by name
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.Types.SharpTSSharedArrayBuffer, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Get constructor that takes int
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, _types.Int32);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetConstructor", _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, ctorLocal);

        // Create args array: new object[] { (int)byteLength }
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);  // byteLength (double)
        il.Emit(OpCodes.Conv_I4);  // convert to int
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the constructor
        il.Emit(OpCodes.Ldloc, ctorLocal);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConstructorInfo, "Invoke", _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        runtime.TSSharedArrayBufferCtor = method;

        // Also emit slice and byteLength helpers
        EmitSharedArrayBufferSlice(runtimeType, runtime);
        EmitSharedArrayBufferByteLength(runtimeType, runtime);
    }

    /// <summary>
    /// Emits SharedArrayBuffer.slice(begin?, end?) helper.
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitSharedArrayBufferSlice(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "SharedArrayBufferSlice",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Int32, _types.Int32]
        );

        var il = method.GetILGenerator();

        // Use reflection to call Slice method at runtime
        var typeLocal = il.DeclareLocal(_types.Type);
        var methodInfoLocal = il.DeclareLocal(_types.MethodInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Get the type from the object itself
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Get the Slice method: type.GetMethod("Slice", new[] { typeof(int), typeof(int?) })
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "Slice");
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, _types.Int32);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldtoken, typeof(int?));
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // Build args array with nullable end handling
        // new object[] { begin, (end == int.MaxValue) ? null : (int?)end }
        var endLabel = il.DefineLabel();
        var buildArgsLabel = il.DefineLabel();
        var localEndValue = il.DeclareLocal(_types.Object);

        // Check if end == int.MaxValue
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_I4, int.MaxValue);
        il.Emit(OpCodes.Beq, endLabel);

        // end is a real value - box as int?
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Newobj, _types.NullableInt32Ctor);
        il.Emit(OpCodes.Box, typeof(int?));
        il.Emit(OpCodes.Stloc, localEndValue);
        il.Emit(OpCodes.Br, buildArgsLabel);

        // end is MaxValue - use null
        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, localEndValue);

        il.MarkLabel(buildArgsLabel);

        // Create args array: new object[] { begin, endValue }
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, localEndValue);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the method
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        runtime.TSSharedArrayBufferSlice = method;
    }

    /// <summary>
    /// Emits SharedArrayBuffer.byteLength getter helper.
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitSharedArrayBufferByteLength(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "SharedArrayBufferByteLength",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection: obj.GetType().GetProperty("ByteLength").GetValue(obj)
        var typeLocal = il.DeclareLocal(_types.Type);
        var propInfoLocal = il.DeclareLocal(_types.PropertyInfo);

        // var type = obj.GetType();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // var propInfo = type.GetProperty("ByteLength");
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "ByteLength");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Stloc, propInfoLocal);

        // return (double)(int)propInfo.GetValue(obj);
        il.Emit(OpCodes.Ldloc, propInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object));
        il.Emit(OpCodes.Unbox_Any, _types.Int32);
        il.Emit(OpCodes.Conv_R8);  // Convert int to double
        il.Emit(OpCodes.Ret);

        runtime.TSSharedArrayBufferByteLengthGetter = method;
    }

    /// <summary>
    /// Emits helper for creating ArrayBuffer.
    /// public static object CreateArrayBuffer(double byteLength)
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitArrayBufferHelper(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "CreateArrayBuffer",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Double]
        );

        var il = method.GetILGenerator();

        // Use reflection to get the type and create instance at runtime
        var typeLocal = il.DeclareLocal(_types.Type);
        var ctorLocal = il.DeclareLocal(_types.ConstructorInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Get the type by name
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.Types.SharpTSArrayBuffer, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Get constructor that takes int
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, _types.Int32);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetConstructor", _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, ctorLocal);

        // Create args array: new object[] { (int)byteLength }
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);  // byteLength (double)
        il.Emit(OpCodes.Conv_I4);  // convert to int
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the constructor
        il.Emit(OpCodes.Ldloc, ctorLocal);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConstructorInfo, "Invoke", _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        runtime.TSArrayBufferCtor = method;

        // Also emit slice, byteLength, and isView helpers
        EmitArrayBufferSlice(runtimeType, runtime);
        EmitArrayBufferByteLength(runtimeType, runtime);
        EmitArrayBufferIsView(runtimeType, runtime);
    }

    /// <summary>
    /// Emits ArrayBuffer.slice(begin?, end?) helper.
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitArrayBufferSlice(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "ArrayBufferSlice",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Int32, _types.Int32]
        );

        var il = method.GetILGenerator();

        // Use reflection to call Slice method at runtime
        var typeLocal = il.DeclareLocal(_types.Type);
        var methodInfoLocal = il.DeclareLocal(_types.MethodInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Get the type from the object itself
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Get the Slice method: type.GetMethod("Slice", new[] { typeof(int), typeof(int?) })
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "Slice");
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, _types.Int32);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldtoken, typeof(int?));
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // Build args array with nullable end handling
        var endLabel = il.DefineLabel();
        var buildArgsLabel = il.DefineLabel();
        var localEndValue = il.DeclareLocal(_types.Object);

        // Check if end == int.MaxValue
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_I4, int.MaxValue);
        il.Emit(OpCodes.Beq, endLabel);

        // end is a real value - box as int?
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Newobj, _types.NullableInt32Ctor);
        il.Emit(OpCodes.Box, typeof(int?));
        il.Emit(OpCodes.Stloc, localEndValue);
        il.Emit(OpCodes.Br, buildArgsLabel);

        // end is MaxValue - use null
        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, localEndValue);

        il.MarkLabel(buildArgsLabel);

        // Create args array: new object[] { begin, endValue }
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, localEndValue);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the method
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        runtime.TSArrayBufferSlice = method;
    }

    /// <summary>
    /// Emits ArrayBuffer.byteLength getter helper.
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitArrayBufferByteLength(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "ArrayBufferByteLength",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection: obj.GetType().GetProperty("ByteLength").GetValue(obj)
        var typeLocal = il.DeclareLocal(_types.Type);
        var propInfoLocal = il.DeclareLocal(_types.PropertyInfo);

        // var type = obj.GetType();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // var propInfo = type.GetProperty("ByteLength");
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "ByteLength");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Stloc, propInfoLocal);

        // return (double)(int)propInfo.GetValue(obj);
        il.Emit(OpCodes.Ldloc, propInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object));
        il.Emit(OpCodes.Unbox_Any, _types.Int32);
        il.Emit(OpCodes.Conv_R8);  // Convert int to double
        il.Emit(OpCodes.Ret);

        runtime.TSArrayBufferByteLengthGetter = method;
    }

    /// <summary>
    /// Emits ArrayBuffer.isView static method helper.
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitArrayBufferIsView(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "ArrayBufferIsView",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection to call static IsView method at runtime
        var typeLocal = il.DeclareLocal(_types.Type);
        var methodInfoLocal = il.DeclareLocal(_types.MethodInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Get the ArrayBuffer type by name
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.Types.SharpTSArrayBuffer, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Get the static IsView method
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "IsView");
        il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Public | BindingFlags.Static));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, typeof(BindingFlags)));
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // Create args array: new object[] { arg }
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the static method: methodInfo.Invoke(null, args)
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Ldnull);  // null for static method
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Ret);

        runtime.TSArrayBufferIsView = method;
    }

    /// <summary>
    /// Emits helper for creating DataView.
    /// public static object CreateDataView(object buffer, double byteOffset, object byteLength)
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitDataViewHelper(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "CreateDataView",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Double, _types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection to get types and create instance at runtime
        // We need to determine if buffer is ArrayBuffer or SharedArrayBuffer by checking type name
        var dvTypeLocal = il.DeclareLocal(_types.Type);
        var bufferTypeLocal = il.DeclareLocal(_types.Type);
        var ctorLocal = il.DeclareLocal(_types.ConstructorInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);
        var byteLengthLocal = il.DeclareLocal(_types.Object);
        var typeArrayLocal = il.DeclareLocal(_types.MakeArrayType(_types.Type));

        // Get the DataView type
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.Types.SharpTSDataView, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, dvTypeLocal);

        // Get the buffer's type
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, bufferTypeLocal);

        // Handle nullable byteLength - convert from object to int? boxed
        var hasLengthLabel = il.DefineLabel();
        var afterLengthLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brfalse, hasLengthLabel);

        // Has byteLength - unbox double and convert to int?
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Newobj, _types.NullableInt32Ctor);
        il.Emit(OpCodes.Box, typeof(int?));
        il.Emit(OpCodes.Stloc, byteLengthLocal);
        il.Emit(OpCodes.Br, afterLengthLabel);

        // No byteLength - use null
        il.MarkLabel(hasLengthLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, byteLengthLocal);

        il.MarkLabel(afterLengthLabel);

        // Build Type[] for constructor lookup: new[] { bufferType, typeof(int), typeof(int?) }
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, bufferTypeLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldtoken, _types.Int32);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldtoken, typeof(int?));
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, typeArrayLocal);

        // Get constructor: dvType.GetConstructor(typeArray)
        il.Emit(OpCodes.Ldloc, dvTypeLocal);
        il.Emit(OpCodes.Ldloc, typeArrayLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetConstructor", _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, ctorLocal);

        // Build args array: new object[] { buffer, (int)byteOffset, byteLength }
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);  // buffer
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1);  // byteOffset (double)
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldloc, byteLengthLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke constructor
        il.Emit(OpCodes.Ldloc, ctorLocal);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConstructorInfo, "Invoke", _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        runtime.TSDataViewCtor = method;

        // Emit property getters
        EmitDataViewByteLength(runtimeType, runtime);
        EmitDataViewByteOffset(runtimeType, runtime);
        EmitDataViewBuffer(runtimeType, runtime);

        // Emit getter methods
        EmitDataViewGetter(runtimeType, runtime, "GetInt8", "getInt8", false);
        EmitDataViewGetter(runtimeType, runtime, "GetUint8", "getUint8", false);
        EmitDataViewGetter(runtimeType, runtime, "GetInt16", "getInt16", true);
        EmitDataViewGetter(runtimeType, runtime, "GetUint16", "getUint16", true);
        EmitDataViewGetter(runtimeType, runtime, "GetInt32", "getInt32", true);
        EmitDataViewGetter(runtimeType, runtime, "GetUint32", "getUint32", true);
        EmitDataViewGetter(runtimeType, runtime, "GetFloat32", "getFloat32", true);
        EmitDataViewGetter(runtimeType, runtime, "GetFloat64", "getFloat64", true);
        EmitDataViewBigIntGetter(runtimeType, runtime, "GetBigInt64", "getBigInt64");
        EmitDataViewBigIntGetter(runtimeType, runtime, "GetBigUint64", "getBigUint64");

        // Emit setter methods
        EmitDataViewSetter(runtimeType, runtime, "SetInt8", "setInt8", false);
        EmitDataViewSetter(runtimeType, runtime, "SetUint8", "setUint8", false);
        EmitDataViewSetter(runtimeType, runtime, "SetInt16", "setInt16", true);
        EmitDataViewSetter(runtimeType, runtime, "SetUint16", "setUint16", true);
        EmitDataViewSetter(runtimeType, runtime, "SetInt32", "setInt32", true);
        EmitDataViewSetter(runtimeType, runtime, "SetUint32", "setUint32", true);
        EmitDataViewSetter(runtimeType, runtime, "SetFloat32", "setFloat32", true);
        EmitDataViewSetter(runtimeType, runtime, "SetFloat64", "setFloat64", true);
        EmitDataViewSetter(runtimeType, runtime, "SetBigInt64", "setBigInt64", true);
        EmitDataViewSetter(runtimeType, runtime, "SetBigUint64", "setBigUint64", true);
    }

    private void EmitNullableIntFromObject(ILGenerator il, int argIndex)
    {
        var hasValueLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg, argIndex);
        il.Emit(OpCodes.Brfalse, hasValueLabel);

        // Has value - unbox and wrap in nullable
        il.Emit(OpCodes.Ldarg, argIndex);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Newobj, _types.NullableInt32Ctor);
        il.Emit(OpCodes.Br, endLabel);

        // Null
        il.MarkLabel(hasValueLabel);
        var localNullableInt = il.DeclareLocal(typeof(int?));
        il.Emit(OpCodes.Ldloca, localNullableInt);
        il.Emit(OpCodes.Initobj, typeof(int?));
        il.Emit(OpCodes.Ldloc, localNullableInt);

        il.MarkLabel(endLabel);
    }

    private void EmitDataViewByteLength(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "DataViewByteLength",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection: obj.GetType().GetProperty("ByteLength").GetValue(obj)
        var typeLocal = il.DeclareLocal(_types.Type);
        var propInfoLocal = il.DeclareLocal(_types.PropertyInfo);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "ByteLength");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Stloc, propInfoLocal);

        il.Emit(OpCodes.Ldloc, propInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object));
        il.Emit(OpCodes.Unbox_Any, _types.Int32);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ret);

        runtime.TSDataViewByteLengthGetter = method;
    }

    private void EmitDataViewByteOffset(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "DataViewByteOffset",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection: obj.GetType().GetProperty("ByteOffset").GetValue(obj)
        var typeLocal = il.DeclareLocal(_types.Type);
        var propInfoLocal = il.DeclareLocal(_types.PropertyInfo);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "ByteOffset");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Stloc, propInfoLocal);

        il.Emit(OpCodes.Ldloc, propInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object));
        il.Emit(OpCodes.Unbox_Any, _types.Int32);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ret);

        runtime.TSDataViewByteOffsetGetter = method;
    }

    private void EmitDataViewBuffer(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "DataViewBuffer",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection: obj.GetType().GetProperty("Buffer").GetValue(obj)
        var typeLocal = il.DeclareLocal(_types.Type);
        var propInfoLocal = il.DeclareLocal(_types.PropertyInfo);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "Buffer");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Stloc, propInfoLocal);

        il.Emit(OpCodes.Ldloc, propInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object));
        il.Emit(OpCodes.Ret);

        runtime.TSDataViewBufferGetter = method;
    }

    private void EmitDataViewGetter(TypeBuilder runtimeType, EmittedRuntime runtime, string runtimeMethodName, string jsMethodName, bool hasEndianness)
    {
        var paramTypes = hasEndianness
            ? new[] { _types.Object, _types.Int32, _types.Boolean }
            : new[] { _types.Object, _types.Int32 };

        var method = runtimeType.DefineMethod(
            $"DataView{runtimeMethodName}",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            paramTypes
        );

        var il = method.GetILGenerator();

        // Use reflection to call the method at runtime
        var typeLocal = il.DeclareLocal(_types.Type);
        var methodInfoLocal = il.DeclareLocal(_types.MethodInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Get the object's type
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Get the method: type.GetMethod(methodName, types)
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, runtimeMethodName);
        if (hasEndianness)
        {
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Newarr, _types.Type);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldtoken, _types.Int32);
            il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
            il.Emit(OpCodes.Stelem_Ref);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldtoken, _types.Boolean);
            il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
            il.Emit(OpCodes.Stelem_Ref);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Newarr, _types.Type);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldtoken, _types.Int32);
            il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
            il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // Build args array
        if (hasEndianness)
        {
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Newarr, _types.Object);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Box, _types.Int32);
            il.Emit(OpCodes.Stelem_Ref);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Box, _types.Boolean);
            il.Emit(OpCodes.Stelem_Ref);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Newarr, _types.Object);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Box, _types.Int32);
            il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the method and convert result to double
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToDouble", _types.Object));
        il.Emit(OpCodes.Ret);

        // Store in runtime
        switch (jsMethodName)
        {
            case "getInt8": runtime.TSDataViewGetInt8 = method; break;
            case "getUint8": runtime.TSDataViewGetUint8 = method; break;
            case "getInt16": runtime.TSDataViewGetInt16 = method; break;
            case "getUint16": runtime.TSDataViewGetUint16 = method; break;
            case "getInt32": runtime.TSDataViewGetInt32 = method; break;
            case "getUint32": runtime.TSDataViewGetUint32 = method; break;
            case "getFloat32": runtime.TSDataViewGetFloat32 = method; break;
            case "getFloat64": runtime.TSDataViewGetFloat64 = method; break;
        }
    }

    private void EmitDataViewBigIntGetter(TypeBuilder runtimeType, EmittedRuntime runtime, string runtimeMethodName, string jsMethodName)
    {
        var method = runtimeType.DefineMethod(
            $"DataView{runtimeMethodName}",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.BigInteger,
            [_types.Object, _types.Int32, _types.Boolean]
        );

        var il = method.GetILGenerator();

        // Use reflection to call the method at runtime
        var typeLocal = il.DeclareLocal(_types.Type);
        var methodInfoLocal = il.DeclareLocal(_types.MethodInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Get the object's type
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Get the method: type.GetMethod(methodName, new[] { typeof(int), typeof(bool) })
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, runtimeMethodName);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, _types.Int32);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldtoken, _types.Boolean);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // Build args array: new object[] { index, littleEndian }
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the method and unbox result as BigInteger
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Unbox_Any, _types.BigInteger);
        il.Emit(OpCodes.Ret);

        switch (jsMethodName)
        {
            case "getBigInt64": runtime.TSDataViewGetBigInt64 = method; break;
            case "getBigUint64": runtime.TSDataViewGetBigUint64 = method; break;
        }
    }

    private void EmitDataViewSetter(TypeBuilder runtimeType, EmittedRuntime runtime, string runtimeMethodName, string jsMethodName, bool hasEndianness)
    {
        var paramTypes = hasEndianness
            ? new[] { _types.Object, _types.Int32, _types.Object, _types.Boolean }
            : new[] { _types.Object, _types.Int32, _types.Object };

        var method = runtimeType.DefineMethod(
            $"DataView{runtimeMethodName}",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            paramTypes
        );

        var il = method.GetILGenerator();

        // Use reflection to call the method at runtime
        var typeLocal = il.DeclareLocal(_types.Type);
        var methodInfoLocal = il.DeclareLocal(_types.MethodInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Get the object's type
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Get the method: type.GetMethod(methodName, types)
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, runtimeMethodName);
        if (hasEndianness)
        {
            il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(OpCodes.Newarr, _types.Type);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldtoken, _types.Int32);
            il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
            il.Emit(OpCodes.Stelem_Ref);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldtoken, _types.Object);
            il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
            il.Emit(OpCodes.Stelem_Ref);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Ldtoken, _types.Boolean);
            il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
            il.Emit(OpCodes.Stelem_Ref);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Newarr, _types.Type);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldtoken, _types.Int32);
            il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
            il.Emit(OpCodes.Stelem_Ref);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldtoken, _types.Object);
            il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
            il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // Build args array
        if (hasEndianness)
        {
            il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(OpCodes.Newarr, _types.Object);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Box, _types.Int32);
            il.Emit(OpCodes.Stelem_Ref);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Stelem_Ref);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Box, _types.Boolean);
            il.Emit(OpCodes.Stelem_Ref);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Newarr, _types.Object);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Box, _types.Int32);
            il.Emit(OpCodes.Stelem_Ref);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the method (discard result)
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Pop);  // Discard return value
        il.Emit(OpCodes.Ret);

        // Store in runtime
        switch (jsMethodName)
        {
            case "setInt8": runtime.TSDataViewSetInt8 = method; break;
            case "setUint8": runtime.TSDataViewSetUint8 = method; break;
            case "setInt16": runtime.TSDataViewSetInt16 = method; break;
            case "setUint16": runtime.TSDataViewSetUint16 = method; break;
            case "setInt32": runtime.TSDataViewSetInt32 = method; break;
            case "setUint32": runtime.TSDataViewSetUint32 = method; break;
            case "setFloat32": runtime.TSDataViewSetFloat32 = method; break;
            case "setFloat64": runtime.TSDataViewSetFloat64 = method; break;
            case "setBigInt64": runtime.TSDataViewSetBigInt64 = method; break;
            case "setBigUint64": runtime.TSDataViewSetBigUint64 = method; break;
        }
    }

    /// <summary>
    /// Emits helpers for creating TypedArrays.
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitTypedArrayHelpers(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        // Helper method for each TypedArray type - use type names instead of typeof()
        EmitTypedArrayHelper(runtimeType, runtime, "Int8Array", "SharpTS.Runtime.Types.SharpTSInt8Array, SharpTS");
        EmitTypedArrayHelper(runtimeType, runtime, "Uint8Array", "SharpTS.Runtime.Types.SharpTSUint8Array, SharpTS");
        EmitTypedArrayHelper(runtimeType, runtime, "Uint8ClampedArray", "SharpTS.Runtime.Types.SharpTSUint8ClampedArray, SharpTS");
        EmitTypedArrayHelper(runtimeType, runtime, "Int16Array", "SharpTS.Runtime.Types.SharpTSInt16Array, SharpTS");
        EmitTypedArrayHelper(runtimeType, runtime, "Uint16Array", "SharpTS.Runtime.Types.SharpTSUint16Array, SharpTS");
        EmitTypedArrayHelper(runtimeType, runtime, "Int32Array", "SharpTS.Runtime.Types.SharpTSInt32Array, SharpTS");
        EmitTypedArrayHelper(runtimeType, runtime, "Uint32Array", "SharpTS.Runtime.Types.SharpTSUint32Array, SharpTS");
        EmitTypedArrayHelper(runtimeType, runtime, "Float32Array", "SharpTS.Runtime.Types.SharpTSFloat32Array, SharpTS");
        EmitTypedArrayHelper(runtimeType, runtime, "Float64Array", "SharpTS.Runtime.Types.SharpTSFloat64Array, SharpTS");
        EmitTypedArrayHelper(runtimeType, runtime, "BigInt64Array", "SharpTS.Runtime.Types.SharpTSBigInt64Array, SharpTS");
        EmitTypedArrayHelper(runtimeType, runtime, "BigUint64Array", "SharpTS.Runtime.Types.SharpTSBigUint64Array, SharpTS");

        // Get typed array element helper
        EmitTypedArrayGetHelper(runtimeType, runtime);
        EmitTypedArraySetHelper(runtimeType, runtime);

        // General-purpose TypedArray creation from object
        EmitTypedArrayFromObjectHelpers(runtimeType, runtime);

    }

    /// <summary>
    /// Emits TypedArray detection and access helpers that don't depend on SharpTS.dll.
    /// These are called early in the emission order, before GetIndex/SetIndex.
    /// </summary>
    public void EmitTypedArrayDetectionHelpers(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        EmitIsTypedArrayHelper(runtimeType, runtime);
        EmitGetTypedArrayElementHelper(runtimeType, runtime);
        EmitSetTypedArrayElementHelper(runtimeType, runtime);
        EmitGetTypedArrayMemberHelper(runtimeType, runtime);
    }

    /// <summary>
    /// Emits a helper that checks if an object is a TypedArray by examining its type name.
    /// This avoids a hard dependency on SharpTS.dll for standalone compilation.
    /// </summary>
    private void EmitIsTypedArrayHelper(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "IsTypedArray",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object]
        );
        runtime.IsTypedArrayMethod = method;

        var il = method.GetILGenerator();
        var falseNullObjLabel = il.DefineLabel();
        var falseNullBaseTypeLabel = il.DefineLabel();
        var falseNullFullNameLabel = il.DefineLabel();
        var trueLabel = il.DefineLabel();
        var returnFalseLabel = il.DefineLabel();

        // if (obj == null) return false;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, falseNullObjLabel);

        // Check if type name contains "SharpTSTypedArray" (base class name)
        // obj.GetType().BaseType?.FullName?.Contains("SharpTSTypedArray") == true
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Type, "get_BaseType"));
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse_S, falseNullBaseTypeLabel);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Type, "get_FullName"));
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse_S, falseNullFullNameLabel);
        il.Emit(OpCodes.Ldstr, "SharpTSTypedArray");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "Contains", _types.String));
        il.Emit(OpCodes.Brtrue_S, trueLabel);
        // Contains returned false, go to return false
        il.Emit(OpCodes.Br_S, returnFalseLabel);

        // Handle null obj case (stack is empty)
        il.MarkLabel(falseNullObjLabel);
        il.Emit(OpCodes.Br_S, returnFalseLabel);

        // Handle null BaseType case (pop the null BaseType from stack)
        il.MarkLabel(falseNullBaseTypeLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br_S, returnFalseLabel);

        // Handle null FullName case (pop the null FullName from stack)
        il.MarkLabel(falseNullFullNameLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br_S, returnFalseLabel);

        il.MarkLabel(returnFalseLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(trueLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a helper that gets an element from a TypedArray using reflection.
    /// This avoids a hard dependency on SharpTS.dll for standalone compilation.
    /// </summary>
    private void EmitGetTypedArrayElementHelper(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "GetTypedArrayElement",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Int32]
        );
        runtime.GetTypedArrayElementMethod = method;

        var il = method.GetILGenerator();

        // Use reflection: obj.GetType().GetProperty("Item").GetValue(obj, new object[] { index })
        var typeLocal = il.DeclareLocal(_types.Type);
        var propInfoLocal = il.DeclareLocal(_types.PropertyInfo);
        var indexArrayLocal = il.DeclareLocal(_types.ObjectArray);

        // var type = obj.GetType();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // var propInfo = type.GetProperty("Item");
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "Item");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Stloc, propInfoLocal);

        // var indexArray = new object[] { index };
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, indexArrayLocal);

        // return propInfo.GetValue(obj, indexArray);
        il.Emit(OpCodes.Ldloc, propInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexArrayLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a helper that sets an element in a TypedArray using reflection.
    /// This avoids a hard dependency on SharpTS.dll for standalone compilation.
    /// </summary>
    private void EmitSetTypedArrayElementHelper(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "SetTypedArrayElement",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.Object, _types.Int32, _types.Object]
        );
        runtime.SetTypedArrayElementMethod = method;

        var il = method.GetILGenerator();

        // Use reflection: obj.GetType().GetProperty("Item").SetValue(obj, value, new object[] { index })
        var typeLocal = il.DeclareLocal(_types.Type);
        var propInfoLocal = il.DeclareLocal(_types.PropertyInfo);
        var indexArrayLocal = il.DeclareLocal(_types.ObjectArray);

        // var type = obj.GetType();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // var propInfo = type.GetProperty("Item");
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "Item");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Stloc, propInfoLocal);

        // var indexArray = new object[] { index };
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, indexArrayLocal);

        // propInfo.SetValue(obj, value, indexArray);
        il.Emit(OpCodes.Ldloc, propInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldloc, indexArrayLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "SetValue", _types.Object, _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a helper that gets a member from a TypedArray using reflection.
    /// This avoids a hard dependency on SharpTS.dll for standalone compilation.
    /// Calls the GetMember(string) method on the TypedArray.
    /// </summary>
    private void EmitGetTypedArrayMemberHelper(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "GetTypedArrayMember",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.String]
        );
        runtime.GetTypedArrayMemberMethod = method;

        var il = method.GetILGenerator();

        // Use reflection: obj.GetType().GetMethod("GetMember").Invoke(obj, new object[] { name })
        var typeLocal = il.DeclareLocal(_types.Type);
        var methodInfoLocal = il.DeclareLocal(_types.MethodInfo);
        var argsArrayLocal = il.DeclareLocal(_types.ObjectArray);

        // var type = obj.GetType();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // var methodInfo = type.GetMethod("GetMember", new[] { typeof(string) });
        // First get the Type[] for the parameter types
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "GetMember");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, _types.String);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // var argsArray = new object[] { name };
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1);  // name string
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsArrayLocal);

        // return methodInfo.Invoke(obj, argsArray);
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, argsArrayLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits helpers for creating TypedArrays from an object argument (number or SharedArrayBuffer).
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitTypedArrayFromObjectHelpers(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        EmitTypedArrayFromObjectHelper(runtimeType, runtime, "Int8Array", "SharpTS.Runtime.Types.SharpTSInt8Array, SharpTS");
        EmitTypedArrayFromObjectHelper(runtimeType, runtime, "Uint8Array", "SharpTS.Runtime.Types.SharpTSUint8Array, SharpTS");
        EmitTypedArrayFromObjectHelper(runtimeType, runtime, "Uint8ClampedArray", "SharpTS.Runtime.Types.SharpTSUint8ClampedArray, SharpTS");
        EmitTypedArrayFromObjectHelper(runtimeType, runtime, "Int16Array", "SharpTS.Runtime.Types.SharpTSInt16Array, SharpTS");
        EmitTypedArrayFromObjectHelper(runtimeType, runtime, "Uint16Array", "SharpTS.Runtime.Types.SharpTSUint16Array, SharpTS");
        EmitTypedArrayFromObjectHelper(runtimeType, runtime, "Int32Array", "SharpTS.Runtime.Types.SharpTSInt32Array, SharpTS");
        EmitTypedArrayFromObjectHelper(runtimeType, runtime, "Uint32Array", "SharpTS.Runtime.Types.SharpTSUint32Array, SharpTS");
        EmitTypedArrayFromObjectHelper(runtimeType, runtime, "Float32Array", "SharpTS.Runtime.Types.SharpTSFloat32Array, SharpTS");
        EmitTypedArrayFromObjectHelper(runtimeType, runtime, "Float64Array", "SharpTS.Runtime.Types.SharpTSFloat64Array, SharpTS");
        EmitTypedArrayFromObjectHelper(runtimeType, runtime, "BigInt64Array", "SharpTS.Runtime.Types.SharpTSBigInt64Array, SharpTS");
        EmitTypedArrayFromObjectHelper(runtimeType, runtime, "BigUint64Array", "SharpTS.Runtime.Types.SharpTSBigUint64Array, SharpTS");
    }

    /// <summary>
    /// Emits a helper that creates a TypedArray from an object (either a number for length, SharedArrayBuffer, or ArrayBuffer).
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitTypedArrayFromObjectHelper(TypeBuilder runtimeType, EmittedRuntime runtime, string name, string arrayTypeName)
    {
        // Create{name}FromObject(object arg) - handles number, SharedArrayBuffer, or ArrayBuffer
        var method = runtimeType.DefineMethod(
            $"Create{name}FromObject",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection to create the TypedArray at runtime
        var arrayTypeLocal = il.DeclareLocal(_types.Type);
        var ctorLocal = il.DeclareLocal(_types.ConstructorInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);
        var argTypeLocal = il.DeclareLocal(_types.Type);
        var argTypeNameLocal = il.DeclareLocal(_types.String);

        // Get the TypedArray type by name
        il.Emit(OpCodes.Ldstr, arrayTypeName);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, arrayTypeLocal);

        // Check if arg is null - if so, treat as length 0
        var argNotNullLabel = il.DefineLabel();
        var isBufferLabel = il.DefineLabel();
        var createFromLengthLabel = il.DefineLabel();
        var createFromBufferLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, argNotNullLabel);

        // Arg is null - create with length 0
        il.Emit(OpCodes.Ldloc, arrayTypeLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, _types.Int32);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetConstructor", _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, ctorLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);
        il.Emit(OpCodes.Ldloc, ctorLocal);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConstructorInfo, "Invoke", _types.ObjectArray));
        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(argNotNullLabel);

        // Get the arg's type name to check if it's a buffer
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, argTypeLocal);
        il.Emit(OpCodes.Ldloc, argTypeLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Type, "get_FullName"));
        il.Emit(OpCodes.Stloc, argTypeNameLocal);

        // Check if it contains "SharedArrayBuffer" or "ArrayBuffer"
        il.Emit(OpCodes.Ldloc, argTypeNameLocal);
        il.Emit(OpCodes.Ldstr, "ArrayBuffer");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "Contains", _types.String));
        il.Emit(OpCodes.Brtrue, isBufferLabel);

        // Not a buffer - treat as length
        il.MarkLabel(createFromLengthLabel);

        // Get constructor(int)
        il.Emit(OpCodes.Ldloc, arrayTypeLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, _types.Int32);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetConstructor", _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, ctorLocal);

        // Create args array: new object[] { (int)Convert.ToDouble(arg) }
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToDouble", _types.Object));
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        il.Emit(OpCodes.Ldloc, ctorLocal);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConstructorInfo, "Invoke", _types.ObjectArray));
        il.Emit(OpCodes.Br, endLabel);

        // Is a buffer - get constructor(bufferType, int, int?)
        il.MarkLabel(isBufferLabel);

        // Build Type[] for constructor: new[] { argType, typeof(int), typeof(int?) }
        il.Emit(OpCodes.Ldloc, arrayTypeLocal);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, argTypeLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldtoken, _types.Int32);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldtoken, typeof(int?));
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetConstructor", _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, ctorLocal);

        // Create args array: new object[] { arg, 0, null }
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);  // buffer
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldc_I4_0);  // byteOffset = 0
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldnull);  // length = null
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        il.Emit(OpCodes.Ldloc, ctorLocal);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConstructorInfo, "Invoke", _types.ObjectArray));

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);

        // Store the helper for use by ILEmitter
        runtime.TypedArrayFromObjectHelpers[name] = method;
    }

    private void EmitTypedArrayHelper(TypeBuilder runtimeType, EmittedRuntime runtime, string name, string arrayTypeName)
    {
        // Create from length: CreateInt8Array(double length)
        // Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
        var methodFromLength = runtimeType.DefineMethod(
            $"Create{name}",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Double]
        );

        var il = methodFromLength.GetILGenerator();
        var typeLocal = il.DeclareLocal(_types.Type);
        var ctorLocal = il.DeclareLocal(_types.ConstructorInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Get the type by name
        il.Emit(OpCodes.Ldstr, arrayTypeName);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Get constructor(int)
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, _types.Int32);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetConstructor", _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, ctorLocal);

        // Create args array: new object[] { (int)length }
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);  // length (double)
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke constructor
        il.Emit(OpCodes.Ldloc, ctorLocal);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConstructorInfo, "Invoke", _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        // Create from SharedArrayBuffer: CreateInt8ArrayFromSAB(object sab, double byteOffset, object length)
        // Uses reflection-based late-binding
        var methodFromSAB = runtimeType.DefineMethod(
            $"Create{name}FromSAB",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Double, _types.Object]
        );

        var ilSAB = methodFromSAB.GetILGenerator();
        var typeLocalSAB = ilSAB.DeclareLocal(_types.Type);
        var sabTypeLocal = ilSAB.DeclareLocal(_types.Type);
        var ctorLocalSAB = ilSAB.DeclareLocal(_types.ConstructorInfo);
        var argsLocalSAB = ilSAB.DeclareLocal(_types.ObjectArray);
        var lengthLocal = ilSAB.DeclareLocal(_types.Object);

        // Get the TypedArray type by name
        ilSAB.Emit(OpCodes.Ldstr, arrayTypeName);
        ilSAB.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        ilSAB.Emit(OpCodes.Stloc, typeLocalSAB);

        // Get the sab's actual type (could be SharedArrayBuffer or ArrayBuffer)
        ilSAB.Emit(OpCodes.Ldarg_0);
        ilSAB.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        ilSAB.Emit(OpCodes.Stloc, sabTypeLocal);

        // Handle nullable length parameter
        var lblHasLength = ilSAB.DefineLabel();
        var lblEndLength = ilSAB.DefineLabel();

        ilSAB.Emit(OpCodes.Ldarg_2);
        ilSAB.Emit(OpCodes.Brfalse, lblHasLength);

        // length is not null - convert and box as int?
        ilSAB.Emit(OpCodes.Ldarg_2);
        ilSAB.Emit(OpCodes.Unbox_Any, _types.Double);
        ilSAB.Emit(OpCodes.Conv_I4);
        ilSAB.Emit(OpCodes.Newobj, _types.NullableInt32Ctor);
        ilSAB.Emit(OpCodes.Box, typeof(int?));
        ilSAB.Emit(OpCodes.Stloc, lengthLocal);
        ilSAB.Emit(OpCodes.Br, lblEndLength);

        ilSAB.MarkLabel(lblHasLength);
        // length is null
        ilSAB.Emit(OpCodes.Ldnull);
        ilSAB.Emit(OpCodes.Stloc, lengthLocal);

        ilSAB.MarkLabel(lblEndLength);

        // Build Type[] for constructor: new[] { sabType, typeof(int), typeof(int?) }
        ilSAB.Emit(OpCodes.Ldloc, typeLocalSAB);
        ilSAB.Emit(OpCodes.Ldc_I4_3);
        ilSAB.Emit(OpCodes.Newarr, _types.Type);
        ilSAB.Emit(OpCodes.Dup);
        ilSAB.Emit(OpCodes.Ldc_I4_0);
        ilSAB.Emit(OpCodes.Ldloc, sabTypeLocal);
        ilSAB.Emit(OpCodes.Stelem_Ref);
        ilSAB.Emit(OpCodes.Dup);
        ilSAB.Emit(OpCodes.Ldc_I4_1);
        ilSAB.Emit(OpCodes.Ldtoken, _types.Int32);
        ilSAB.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        ilSAB.Emit(OpCodes.Stelem_Ref);
        ilSAB.Emit(OpCodes.Dup);
        ilSAB.Emit(OpCodes.Ldc_I4_2);
        ilSAB.Emit(OpCodes.Ldtoken, typeof(int?));
        ilSAB.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        ilSAB.Emit(OpCodes.Stelem_Ref);
        ilSAB.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetConstructor", _types.MakeArrayType(_types.Type)));
        ilSAB.Emit(OpCodes.Stloc, ctorLocalSAB);

        // Create args array: new object[] { sab, (int)byteOffset, length }
        ilSAB.Emit(OpCodes.Ldc_I4_3);
        ilSAB.Emit(OpCodes.Newarr, _types.Object);
        ilSAB.Emit(OpCodes.Dup);
        ilSAB.Emit(OpCodes.Ldc_I4_0);
        ilSAB.Emit(OpCodes.Ldarg_0);  // sab
        ilSAB.Emit(OpCodes.Stelem_Ref);
        ilSAB.Emit(OpCodes.Dup);
        ilSAB.Emit(OpCodes.Ldc_I4_1);
        ilSAB.Emit(OpCodes.Ldarg_1);  // byteOffset (double)
        ilSAB.Emit(OpCodes.Conv_I4);
        ilSAB.Emit(OpCodes.Box, _types.Int32);
        ilSAB.Emit(OpCodes.Stelem_Ref);
        ilSAB.Emit(OpCodes.Dup);
        ilSAB.Emit(OpCodes.Ldc_I4_2);
        ilSAB.Emit(OpCodes.Ldloc, lengthLocal);
        ilSAB.Emit(OpCodes.Stelem_Ref);
        ilSAB.Emit(OpCodes.Stloc, argsLocalSAB);

        // Invoke constructor
        ilSAB.Emit(OpCodes.Ldloc, ctorLocalSAB);
        ilSAB.Emit(OpCodes.Ldloc, argsLocalSAB);
        ilSAB.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConstructorInfo, "Invoke", _types.ObjectArray));
        ilSAB.Emit(OpCodes.Ret);

        // Store the helper for use by ILEmitter
        runtime.TypedArrayFromBufferHelpers[name] = methodFromSAB;
    }

    private void EmitTypedArrayGetHelper(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        // public static object TypedArrayGet(object typedArray, double index)
        // Uses reflection to avoid hard dependency on SharpTS.dll
        var method = runtimeType.DefineMethod(
            "TypedArrayGet",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Double]
        );

        var il = method.GetILGenerator();

        // Use reflection: obj.GetType().GetProperty("Item").GetValue(obj, new object[] { (int)index })
        var typeLocal = il.DeclareLocal(_types.Type);
        var propInfoLocal = il.DeclareLocal(_types.PropertyInfo);
        var indexArrayLocal = il.DeclareLocal(_types.ObjectArray);

        // var type = obj.GetType();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // var propInfo = type.GetProperty("Item");
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "Item");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Stloc, propInfoLocal);

        // var indexArray = new object[] { (int)index };
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, indexArrayLocal);

        // return propInfo.GetValue(obj, indexArray);
        il.Emit(OpCodes.Ldloc, propInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexArrayLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        runtime.TSTypedArrayGet = method;
    }

    private void EmitTypedArraySetHelper(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        // public static void TypedArraySet(object typedArray, double index, object value)
        // Uses reflection to avoid hard dependency on SharpTS.dll
        var method = runtimeType.DefineMethod(
            "TypedArraySet",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [_types.Object, _types.Double, _types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection: obj.GetType().GetProperty("Item").SetValue(obj, value, new object[] { (int)index })
        var typeLocal = il.DeclareLocal(_types.Type);
        var propInfoLocal = il.DeclareLocal(_types.PropertyInfo);
        var indexArrayLocal = il.DeclareLocal(_types.ObjectArray);

        // var type = obj.GetType();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // var propInfo = type.GetProperty("Item");
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "Item");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Stloc, propInfoLocal);

        // var indexArray = new object[] { (int)index };
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, indexArrayLocal);

        // propInfo.SetValue(obj, value, indexArray);
        il.Emit(OpCodes.Ldloc, propInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldloc, indexArrayLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "SetValue", _types.Object, _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        runtime.TSTypedArraySet = method;
    }

    /// <summary>
    /// Emits Atomics static method helpers.
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitAtomicsHelpers(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        const string atomicsTypeName = "SharpTS.Runtime.Types.SharpTSAtomics, SharpTS";

        // Atomics.load(typedArray, index)
        runtime.AtomicsLoad = EmitAtomicsMethod(runtimeType, "AtomicsLoad", atomicsTypeName, "Load",
            [_types.Object, _types.Double], _types.Object, hasValue: false);

        // Atomics.store(typedArray, index, value)
        runtime.AtomicsStore = EmitAtomicsMethod(runtimeType, "AtomicsStore", atomicsTypeName, "Store",
            [_types.Object, _types.Double, _types.Object], _types.Object, hasValue: true);

        // Atomics.add(typedArray, index, value)
        runtime.AtomicsAdd = EmitAtomicsMethod(runtimeType, "AtomicsAdd", atomicsTypeName, "Add",
            [_types.Object, _types.Double, _types.Object], _types.Object, hasValue: true);

        // Atomics.sub(typedArray, index, value)
        runtime.AtomicsSub = EmitAtomicsMethod(runtimeType, "AtomicsSub", atomicsTypeName, "Sub",
            [_types.Object, _types.Double, _types.Object], _types.Object, hasValue: true);

        // Atomics.and(typedArray, index, value)
        runtime.AtomicsAnd = EmitAtomicsMethod(runtimeType, "AtomicsAnd", atomicsTypeName, "And",
            [_types.Object, _types.Double, _types.Object], _types.Object, hasValue: true);

        // Atomics.or(typedArray, index, value)
        runtime.AtomicsOr = EmitAtomicsMethod(runtimeType, "AtomicsOr", atomicsTypeName, "Or",
            [_types.Object, _types.Double, _types.Object], _types.Object, hasValue: true);

        // Atomics.xor(typedArray, index, value)
        runtime.AtomicsXor = EmitAtomicsMethod(runtimeType, "AtomicsXor", atomicsTypeName, "Xor",
            [_types.Object, _types.Double, _types.Object], _types.Object, hasValue: true);

        // Atomics.exchange(typedArray, index, value)
        runtime.AtomicsExchange = EmitAtomicsMethod(runtimeType, "AtomicsExchange", atomicsTypeName, "Exchange",
            [_types.Object, _types.Double, _types.Object], _types.Object, hasValue: true);

        // Atomics.compareExchange(typedArray, index, expectedValue, replacementValue)
        runtime.AtomicsCompareExchange = EmitAtomicsCompareExchangeMethod(runtimeType, atomicsTypeName);

        // Atomics.wait(typedArray, index, value, timeout?)
        runtime.AtomicsWait = EmitAtomicsWaitMethod(runtimeType, atomicsTypeName);

        // Atomics.notify(typedArray, index, count?)
        runtime.AtomicsNotify = EmitAtomicsNotifyMethod(runtimeType, atomicsTypeName);

        // Atomics.isLockFree(size)
        runtime.AtomicsIsLockFree = EmitAtomicsIsLockFreeMethod(runtimeType, atomicsTypeName);
    }

    private MethodBuilder EmitAtomicsMethod(TypeBuilder runtimeType, string methodName, string atomicsTypeName,
        string runtimeMethodName, Type[] paramTypes, Type returnType, bool hasValue)
    {
        var method = runtimeType.DefineMethod(
            methodName,
            MethodAttributes.Public | MethodAttributes.Static,
            returnType,
            paramTypes
        );

        var il = method.GetILGenerator();

        // Use reflection to call the static method at runtime
        var atomicsTypeLocal = il.DeclareLocal(_types.Type);
        var methodInfoLocal = il.DeclareLocal(_types.MethodInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Get the Atomics type by name
        il.Emit(OpCodes.Ldstr, atomicsTypeName);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, atomicsTypeLocal);

        // Get the method by name with BindingFlags
        il.Emit(OpCodes.Ldloc, atomicsTypeLocal);
        il.Emit(OpCodes.Ldstr, runtimeMethodName);
        il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Public | BindingFlags.Static));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, typeof(BindingFlags)));
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // Build args array
        if (hasValue)
        {
            // new object[] { typedArray, (int)index, value }
            il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(OpCodes.Newarr, _types.Object);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stelem_Ref);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Box, _types.Int32);
            il.Emit(OpCodes.Stelem_Ref);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Stelem_Ref);
        }
        else
        {
            // new object[] { typedArray, (int)index }
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Newarr, _types.Object);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stelem_Ref);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Box, _types.Int32);
            il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the static method
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Ldnull);  // null instance for static method
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        return method;
    }

    private MethodBuilder EmitAtomicsCompareExchangeMethod(TypeBuilder runtimeType, string atomicsTypeName)
    {
        var method = runtimeType.DefineMethod(
            "AtomicsCompareExchange",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Double, _types.Object, _types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection to call the static method at runtime
        var atomicsTypeLocal = il.DeclareLocal(_types.Type);
        var methodInfoLocal = il.DeclareLocal(_types.MethodInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Get the Atomics type by name
        il.Emit(OpCodes.Ldstr, atomicsTypeName);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, atomicsTypeLocal);

        // Get the CompareExchange method
        il.Emit(OpCodes.Ldloc, atomicsTypeLocal);
        il.Emit(OpCodes.Ldstr, "CompareExchange");
        il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Public | BindingFlags.Static));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, typeof(BindingFlags)));
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // Build args array: new object[] { typedArray, (int)index, expected, replacement }
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the static method
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        return method;
    }

    private MethodBuilder EmitAtomicsWaitMethod(TypeBuilder runtimeType, string atomicsTypeName)
    {
        var method = runtimeType.DefineMethod(
            "AtomicsWait",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object, _types.Double, _types.Object, _types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection to call the static method at runtime
        var atomicsTypeLocal = il.DeclareLocal(_types.Type);
        var methodInfoLocal = il.DeclareLocal(_types.MethodInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);
        var timeoutLocal = il.DeclareLocal(_types.Object);

        // Get the Atomics type by name
        il.Emit(OpCodes.Ldstr, atomicsTypeName);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, atomicsTypeLocal);

        // Get the Wait method
        il.Emit(OpCodes.Ldloc, atomicsTypeLocal);
        il.Emit(OpCodes.Ldstr, "Wait");
        il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Public | BindingFlags.Static));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, typeof(BindingFlags)));
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // Handle nullable timeout
        var lblHasTimeout = il.DefineLabel();
        var lblEndTimeout = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Brfalse, lblHasTimeout);

        // Has timeout - unbox and wrap in nullable
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Newobj, typeof(double?).GetConstructor([typeof(double)])!);
        il.Emit(OpCodes.Box, typeof(double?));
        il.Emit(OpCodes.Stloc, timeoutLocal);
        il.Emit(OpCodes.Br, lblEndTimeout);

        il.MarkLabel(lblHasTimeout);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, timeoutLocal);

        il.MarkLabel(lblEndTimeout);

        // Build args array: new object[] { typedArray, (int)index, value, timeout }
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Ldloc, timeoutLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the static method
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ret);

        return method;
    }

    private MethodBuilder EmitAtomicsNotifyMethod(TypeBuilder runtimeType, string atomicsTypeName)
    {
        var method = runtimeType.DefineMethod(
            "AtomicsNotify",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object, _types.Double, _types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection to call the static method at runtime
        var atomicsTypeLocal = il.DeclareLocal(_types.Type);
        var methodInfoLocal = il.DeclareLocal(_types.MethodInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);
        var countLocal = il.DeclareLocal(_types.Object);

        // Get the Atomics type by name
        il.Emit(OpCodes.Ldstr, atomicsTypeName);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, atomicsTypeLocal);

        // Get the Notify method
        il.Emit(OpCodes.Ldloc, atomicsTypeLocal);
        il.Emit(OpCodes.Ldstr, "Notify");
        il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Public | BindingFlags.Static));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, typeof(BindingFlags)));
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // Handle nullable count
        var lblHasCount = il.DefineLabel();
        var lblEndCount = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brfalse, lblHasCount);

        // Has count - unbox and wrap in nullable
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Newobj, _types.NullableInt32Ctor);
        il.Emit(OpCodes.Box, typeof(int?));
        il.Emit(OpCodes.Stloc, countLocal);
        il.Emit(OpCodes.Br, lblEndCount);

        il.MarkLabel(lblHasCount);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, countLocal);

        il.MarkLabel(lblEndCount);

        // Build args array: new object[] { typedArray, (int)index, count }
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldloc, countLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the static method and convert result to double
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToDouble", _types.Object));
        il.Emit(OpCodes.Ret);

        return method;
    }

    private MethodBuilder EmitAtomicsIsLockFreeMethod(TypeBuilder runtimeType, string atomicsTypeName)
    {
        var method = runtimeType.DefineMethod(
            "AtomicsIsLockFree",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Double]
        );

        var il = method.GetILGenerator();

        // Use reflection to call the static method at runtime
        var atomicsTypeLocal = il.DeclareLocal(_types.Type);
        var methodInfoLocal = il.DeclareLocal(_types.MethodInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // Get the Atomics type by name
        il.Emit(OpCodes.Ldstr, atomicsTypeName);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, atomicsTypeLocal);

        // Get the IsLockFree method
        il.Emit(OpCodes.Ldloc, atomicsTypeLocal);
        il.Emit(OpCodes.Ldstr, "IsLockFree");
        il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Public | BindingFlags.Static));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, typeof(BindingFlags)));
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // Build args array: new object[] { (int)size }
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the static method
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// Emits MessageChannel constructor helper.
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitMessageChannelHelper(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "CreateMessageChannel",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            Type.EmptyTypes
        );

        var il = method.GetILGenerator();

        // Use reflection to create MessageChannel at runtime
        var typeLocal = il.DeclareLocal(_types.Type);
        var ctorLocal = il.DeclareLocal(_types.ConstructorInfo);

        // Get the type by name
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.Types.SharpTSMessageChannel, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Get the parameterless constructor
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetConstructor", _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, ctorLocal);

        // Invoke the constructor with empty args
        il.Emit(OpCodes.Ldloc, ctorLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConstructorInfo, "Invoke", _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        runtime.TSMessageChannelCtor = method;
    }

    /// <summary>
    /// Emits Worker constructor helper.
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitWorkerHelper(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        // CreateWorker(string filename, object? options, Interpreter? parentInterpreter)
        var method = runtimeType.DefineMethod(
            "CreateWorker",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.Object, _types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection to create Worker at runtime
        var typeLocal = il.DeclareLocal(_types.Type);
        var ctorLocal = il.DeclareLocal(_types.ConstructorInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);
        var optionsTypeLocal = il.DeclareLocal(_types.Type);
        var interpreterTypeLocal = il.DeclareLocal(_types.Type);

        // Get the Worker type by name
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.Types.SharpTSWorker, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Get the SharpTSObject type for options parameter
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.Types.SharpTSObject, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, optionsTypeLocal);

        // Get the Interpreter type for parentInterpreter parameter
        il.Emit(OpCodes.Ldstr, "SharpTS.Execution.Interpreter, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, interpreterTypeLocal);

        // Build Type[] for constructor: new[] { typeof(string), SharpTSObjectType, InterpreterType }
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, _types.String);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, optionsTypeLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldloc, interpreterTypeLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetConstructor", _types.MakeArrayType(_types.Type)));
        il.Emit(OpCodes.Stloc, ctorLocal);

        // Build args array: new object[] { filename, options, parentInterpreter }
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);  // filename
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1);  // options
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldarg_2);  // parentInterpreter
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the constructor
        il.Emit(OpCodes.Ldloc, ctorLocal);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConstructorInfo, "Invoke", _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        runtime.TSWorkerCtor = method;
    }

    /// <summary>
    /// Emits StructuredClone helper.
    /// Accepts either null, a SharpTSArray (transfer list), or a SharpTSObject with { transfer: [...] }.
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitStructuredCloneHelper(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "StructuredClone",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );

        var il = method.GetILGenerator();

        // Use reflection to call StructuredClone.Clone at runtime
        var structuredCloneTypeLocal = il.DeclareLocal(_types.Type);
        var cloneMethodLocal = il.DeclareLocal(_types.MethodInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);
        var transferLocal = il.DeclareLocal(_types.Object);
        var argTypeNameLocal = il.DeclareLocal(_types.String);
        var valueLocal = il.DeclareLocal(_types.Object);

        // Get the StructuredClone type by name
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.Types.StructuredClone, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, structuredCloneTypeLocal);

        // Get the Clone method
        il.Emit(OpCodes.Ldloc, structuredCloneTypeLocal);
        il.Emit(OpCodes.Ldstr, "Clone");
        il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Public | BindingFlags.Static));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, typeof(BindingFlags)));
        il.Emit(OpCodes.Stloc, cloneMethodLocal);

        // Initialize transfer to null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, transferLocal);

        var callCloneLabel = il.DefineLabel();
        var checkObjectLabel = il.DefineLabel();
        var checkArrayLabel = il.DefineLabel();
        var extractTransferLabel = il.DefineLabel();

        // Check if arg1 is null
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, callCloneLabel);

        // Get the arg's type name to check what type it is
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Type, "get_FullName"));
        il.Emit(OpCodes.Stloc, argTypeNameLocal);

        // Check if it's a SharpTSObject (options)
        il.Emit(OpCodes.Ldloc, argTypeNameLocal);
        il.Emit(OpCodes.Ldstr, "SharpTSObject");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "Contains", _types.String));
        il.Emit(OpCodes.Brtrue, extractTransferLabel);

        // Check if it's a SharpTSArray (transfer directly)
        il.Emit(OpCodes.Ldloc, argTypeNameLocal);
        il.Emit(OpCodes.Ldstr, "SharpTSArray");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "Contains", _types.String));
        il.Emit(OpCodes.Brfalse, callCloneLabel);

        // It's an array - use it directly as transfer
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stloc, transferLocal);
        il.Emit(OpCodes.Br, callCloneLabel);

        // Extract transfer from object's Fields dictionary
        il.MarkLabel(extractTransferLabel);

        // Get "transfer" field via reflection: obj.GetType().GetProperty("Fields").GetValue(obj)
        var fieldsLocal = il.DeclareLocal(_types.Object);
        var propInfoLocal = il.DeclareLocal(_types.PropertyInfo);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "Fields");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Stloc, propInfoLocal);

        // Check if propInfo is null
        il.Emit(OpCodes.Ldloc, propInfoLocal);
        il.Emit(OpCodes.Brfalse, callCloneLabel);

        // Get the Fields dictionary
        il.Emit(OpCodes.Ldloc, propInfoLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object));
        il.Emit(OpCodes.Stloc, fieldsLocal);

        // Try to get "transfer" from the dictionary using indexer via reflection
        // For simplicity, we'll use the Item property (indexer)
        var dictTypeLocal = il.DeclareLocal(_types.Type);
        var itemPropLocal = il.DeclareLocal(_types.PropertyInfo);
        var indexArgsLocal = il.DeclareLocal(_types.ObjectArray);

        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Brfalse, callCloneLabel);

        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, dictTypeLocal);

        // Try to get ContainsKey method and check if "transfer" exists
        var containsKeyMethodLocal = il.DeclareLocal(_types.MethodInfo);
        il.Emit(OpCodes.Ldloc, dictTypeLocal);
        il.Emit(OpCodes.Ldstr, "ContainsKey");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        il.Emit(OpCodes.Stloc, containsKeyMethodLocal);

        // Call ContainsKey("transfer")
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldstr, "transfer");
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, indexArgsLocal);

        il.Emit(OpCodes.Ldloc, containsKeyMethodLocal);
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldloc, indexArgsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Brfalse, callCloneLabel);

        // Get the "transfer" value using Item indexer
        il.Emit(OpCodes.Ldloc, dictTypeLocal);
        il.Emit(OpCodes.Ldstr, "Item");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Stloc, itemPropLocal);

        il.Emit(OpCodes.Ldloc, itemPropLocal);
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldloc, indexArgsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Stloc, transferLocal);

        // Call Clone(value, transfer)
        il.MarkLabel(callCloneLabel);

        // Build args: new object[] { value, transfer }
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, transferLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke Clone
        il.Emit(OpCodes.Ldloc, cloneMethodLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        runtime.StructuredCloneClone = method;
    }

    /// <summary>
    /// Emits worker_threads module helper methods.
    /// Uses reflection-based late-binding to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitWorkerThreadsModuleHelpers(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        const string workerThreadsTypeName = "SharpTS.Runtime.Types.WorkerThreads, SharpTS";

        // isMainThread getter
        var isMainThreadMethod = runtimeType.DefineMethod(
            "WorkerThreadsIsMainThread",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            Type.EmptyTypes
        );

        var il = isMainThreadMethod.GetILGenerator();
        var typeLocal = il.DeclareLocal(_types.Type);
        var propInfoLocal = il.DeclareLocal(_types.PropertyInfo);

        // Get the WorkerThreads type
        il.Emit(OpCodes.Ldstr, workerThreadsTypeName);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Get the IsMainThread property
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "IsMainThread");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Stloc, propInfoLocal);

        // Get the property value (static property, so pass null)
        il.Emit(OpCodes.Ldloc, propInfoLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object));
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Ret);
        runtime.WorkerThreadsIsMainThread = isMainThreadMethod;

        // threadId getter
        var threadIdMethod = runtimeType.DefineMethod(
            "WorkerThreadsThreadId",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            Type.EmptyTypes
        );

        var il2 = threadIdMethod.GetILGenerator();
        var typeLocal2 = il2.DeclareLocal(_types.Type);
        var propInfoLocal2 = il2.DeclareLocal(_types.PropertyInfo);

        // Get the WorkerThreads type
        il2.Emit(OpCodes.Ldstr, workerThreadsTypeName);
        il2.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il2.Emit(OpCodes.Stloc, typeLocal2);

        // Get the ThreadId property
        il2.Emit(OpCodes.Ldloc, typeLocal2);
        il2.Emit(OpCodes.Ldstr, "ThreadId");
        il2.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il2.Emit(OpCodes.Stloc, propInfoLocal2);

        // Get the property value and convert to double
        il2.Emit(OpCodes.Ldloc, propInfoLocal2);
        il2.Emit(OpCodes.Ldnull);
        il2.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object));
        il2.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToDouble", _types.Object));
        il2.Emit(OpCodes.Ret);
        runtime.WorkerThreadsThreadId = threadIdMethod;

        // receiveMessageOnPort
        var receiveMethod = runtimeType.DefineMethod(
            "WorkerThreadsReceiveMessageOnPort",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );

        var il3 = receiveMethod.GetILGenerator();
        var typeLocal3 = il3.DeclareLocal(_types.Type);
        var methodInfoLocal = il3.DeclareLocal(_types.MethodInfo);
        var argsLocal = il3.DeclareLocal(_types.ObjectArray);

        // Get the WorkerThreads type
        il3.Emit(OpCodes.Ldstr, workerThreadsTypeName);
        il3.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il3.Emit(OpCodes.Stloc, typeLocal3);

        // Get the ReceiveMessageOnPort method
        il3.Emit(OpCodes.Ldloc, typeLocal3);
        il3.Emit(OpCodes.Ldstr, "ReceiveMessageOnPort");
        il3.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Public | BindingFlags.Static));
        il3.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, typeof(BindingFlags)));
        il3.Emit(OpCodes.Stloc, methodInfoLocal);

        // Build args array: new object[] { port }
        il3.Emit(OpCodes.Ldc_I4_1);
        il3.Emit(OpCodes.Newarr, _types.Object);
        il3.Emit(OpCodes.Dup);
        il3.Emit(OpCodes.Ldc_I4_0);
        il3.Emit(OpCodes.Ldarg_0);
        il3.Emit(OpCodes.Stelem_Ref);
        il3.Emit(OpCodes.Stloc, argsLocal);

        // Invoke the static method
        il3.Emit(OpCodes.Ldloc, methodInfoLocal);
        il3.Emit(OpCodes.Ldnull);
        il3.Emit(OpCodes.Ldloc, argsLocal);
        il3.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il3.Emit(OpCodes.Ret);
        runtime.WorkerThreadsReceiveMessageOnPort = receiveMethod;
    }
}
