using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace SharpTS.Compilation;

/// <summary>
/// Emits $FsReadStream and $FsWriteStream types and their factory methods
/// for compiled fs.createReadStream/createWriteStream support.
/// </summary>
public partial class RuntimeEmitter
{
    // Stream type builders (module-level emitted types)
    private TypeBuilder _fsReadStreamType = null!;
    private TypeBuilder _fsWriteStreamType = null!;

    // $FsReadStream fields
    private FieldBuilder _rsPathField = null!;
    private FieldBuilder _rsDataField = null!;
    private FieldBuilder _rsBytesReadField = null!;

    // $FsWriteStream fields
    private FieldBuilder _wsPathField = null!;
    private FieldBuilder _wsStreamField = null!;
    private FieldBuilder _wsBytesWrittenField = null!;

    // Method builders for cross-type references
    private MethodBuilder _wsWriteMethod = null!;
    private MethodBuilder _wsEndMethod = null!;

    /// <summary>
    /// Phase 1: Define $FsReadStream/$FsWriteStream types, fields, methods, and create them.
    /// Must be called before EmitRuntimeClass and after TSFunction is defined.
    /// </summary>
    private void EmitFsStreamTypeDefinitions(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        DefineFsReadStreamType(moduleBuilder, runtime);
        DefineFsWriteStreamType(moduleBuilder);
        EmitFsWriteStreamMethods(runtime);  // Must come before ReadStream methods (Pipe needs _wsWriteMethod)
        EmitFsReadStreamMethods(runtime);
        _fsReadStreamType.CreateType();
        _fsWriteStreamType.CreateType();
    }

    /// <summary>
    /// Phase 2: Emit static factory methods on the runtime type.
    /// Must be called during/after EmitRuntimeClass.
    /// </summary>
    private void EmitFsStreamFactories(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        EmitFsCreateReadStreamFactory(runtimeType, runtime);
        EmitFsCreateWriteStreamFactory(runtimeType, runtime);
    }

    #region $FsReadStream

    private void DefineFsReadStreamType(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        _fsReadStreamType = moduleBuilder.DefineType(
            "$FsReadStream",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            runtime.TSReadableType  // Extends $Readable instead of object
        );

        // Fields (path and bytesRead only — data goes into $Readable's buffer via Push)
        _rsPathField = _fsReadStreamType.DefineField("_path", _types.String, FieldAttributes.Private);
        _rsDataField = _fsReadStreamType.DefineField("_data", _types.Object, FieldAttributes.Private);
        _rsBytesReadField = _fsReadStreamType.DefineField("_bytesRead", _types.Double, FieldAttributes.Private);

        // Constructor: $FsReadStream(string path, object data, double bytesRead)
        // Calls base $Readable ctor, then Push(data) and Push(null) to load buffer and signal EOF
        var ctor = _fsReadStreamType.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.String, _types.Object, _types.Double]
        );
        var il = ctor.GetILGenerator();

        // Call base $Readable constructor (no args)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.TSReadableCtor);

        // Store fields
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, _rsPathField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Stfld, _rsDataField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Stfld, _rsBytesReadField);

        // Push data into the Readable buffer (if non-null)
        var skipPushLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_2); // data
        il.Emit(OpCodes.Brfalse, skipPushLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.TSReadablePush);
        il.Emit(OpCodes.Pop); // Push returns bool
        il.MarkLabel(skipPushLabel);

        // Push null to signal EOF
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Callvirt, runtime.TSReadablePush);
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ret);

        // Property: Path (string)
        var pathProp = _fsReadStreamType.DefineProperty("Path", PropertyAttributes.None, _types.String, null);
        var pathGetter = _fsReadStreamType.DefineMethod("get_Path",
            MethodAttributes.Public | MethodAttributes.SpecialName, _types.String, Type.EmptyTypes);
        var pil = pathGetter.GetILGenerator();
        pil.Emit(OpCodes.Ldarg_0);
        pil.Emit(OpCodes.Ldfld, _rsPathField);
        pil.Emit(OpCodes.Ret);
        pathProp.SetGetMethod(pathGetter);

        // Property: BytesRead (object - returns boxed double)
        var brProp = _fsReadStreamType.DefineProperty("BytesRead", PropertyAttributes.None, _types.Object, null);
        var brGetter = _fsReadStreamType.DefineMethod("get_BytesRead",
            MethodAttributes.Public | MethodAttributes.SpecialName, _types.Object, Type.EmptyTypes);
        var bril = brGetter.GetILGenerator();
        bril.Emit(OpCodes.Ldarg_0);
        bril.Emit(OpCodes.Ldfld, _rsBytesReadField);
        bril.Emit(OpCodes.Box, _types.Double);
        bril.Emit(OpCodes.Ret);
        brProp.SetGetMethod(brGetter);
    }

    private void EmitFsReadStreamMethods(EmittedRuntime runtime)
    {
        // Override Pipe to handle $FsWriteStream (which doesn't extend $Writable)
        EmitFsReadStreamPipeOverride(runtime);
    }

    /// <summary>
    /// Override Pipe on $FsReadStream: first tries the base $Readable.Pipe behavior (for $Writable/$Duplex),
    /// then falls back to calling Write/End directly on $FsWriteStream.
    /// </summary>
    private void EmitFsReadStreamPipeOverride(EmittedRuntime runtime)
    {
        var method = _fsReadStreamType.DefineMethod("Pipe",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            _types.Object, [_types.Object, _types.Object]);

        var il = method.GetILGenerator();
        var basePathLabel = il.DefineLabel();

        // Check if dest is $FsWriteStream
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _fsWriteStreamType);
        il.Emit(OpCodes.Brfalse, basePathLabel);

        // $FsWriteStream path: write _data directly, then end
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _fsWriteStreamType);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _rsDataField);
        il.Emit(OpCodes.Callvirt, _wsWriteMethod);
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _fsWriteStreamType);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Callvirt, _wsEndMethod);
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ret);

        // For all other destinations: delegate to base $Readable.Pipe
        il.MarkLabel(basePathLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, runtime.TSReadablePipe);
        il.Emit(OpCodes.Ret);
    }

    #endregion

    #region $FsWriteStream

    private void DefineFsWriteStreamType(ModuleBuilder moduleBuilder)
    {
        _fsWriteStreamType = moduleBuilder.DefineType(
            "$FsWriteStream",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object
        );

        // Fields
        _wsPathField = _fsWriteStreamType.DefineField("_path", _types.String, FieldAttributes.Private);
        _wsStreamField = _fsWriteStreamType.DefineField("_stream", typeof(FileStream), FieldAttributes.Private);
        _wsBytesWrittenField = _fsWriteStreamType.DefineField("_bytesWritten", _types.Double, FieldAttributes.Private);

        // Constructor: $FsWriteStream(string path)
        var ctor = _fsWriteStreamType.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.String]
        );
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, _wsPathField);
        // Open FileStream: new FileStream(path, FileMode.Create, FileAccess.Write)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4, (int)FileMode.Create);
        il.Emit(OpCodes.Ldc_I4, (int)FileAccess.Write);
        il.Emit(OpCodes.Newobj, typeof(FileStream).GetConstructor([typeof(string), typeof(FileMode), typeof(FileAccess)])!);
        il.Emit(OpCodes.Stfld, _wsStreamField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Stfld, _wsBytesWrittenField);
        il.Emit(OpCodes.Ret);

        // Property: Path (string)
        var pathProp = _fsWriteStreamType.DefineProperty("Path", PropertyAttributes.None, _types.String, null);
        var pathGetter = _fsWriteStreamType.DefineMethod("get_Path",
            MethodAttributes.Public | MethodAttributes.SpecialName, _types.String, Type.EmptyTypes);
        var pil = pathGetter.GetILGenerator();
        pil.Emit(OpCodes.Ldarg_0);
        pil.Emit(OpCodes.Ldfld, _wsPathField);
        pil.Emit(OpCodes.Ret);
        pathProp.SetGetMethod(pathGetter);

        // Property: BytesWritten (object - returns boxed double)
        var bwProp = _fsWriteStreamType.DefineProperty("BytesWritten", PropertyAttributes.None, _types.Object, null);
        var bwGetter = _fsWriteStreamType.DefineMethod("get_BytesWritten",
            MethodAttributes.Public | MethodAttributes.SpecialName, _types.Object, Type.EmptyTypes);
        var bwil = bwGetter.GetILGenerator();
        bwil.Emit(OpCodes.Ldarg_0);
        bwil.Emit(OpCodes.Ldfld, _wsBytesWrittenField);
        bwil.Emit(OpCodes.Box, _types.Double);
        bwil.Emit(OpCodes.Ret);
        bwProp.SetGetMethod(bwGetter);

        // Define method stubs (bodies emitted later for cross-type references)
        _wsWriteMethod = _fsWriteStreamType.DefineMethod("Write",
            MethodAttributes.Public, _types.Object, [_types.Object]);
        _wsEndMethod = _fsWriteStreamType.DefineMethod("End",
            MethodAttributes.Public, _types.Object, [_types.Object]);
    }

    private void EmitFsWriteStreamMethods(EmittedRuntime runtime)
    {
        EmitFsWriteStreamWriteBody();
        EmitFsWriteStreamEndBody();
        EmitFsWriteStreamOn();
    }

    private void EmitFsWriteStreamWriteBody()
    {
        // public object Write(object data) - writes data to FileStream
        var il = _wsWriteMethod.GetILGenerator();

        // Handle both byte[] and string data
        var bytesLocal = il.DeclareLocal(typeof(byte[]));
        var afterConvertLabel = il.DefineLabel();

        // if (data is byte[]) bytes = (byte[])data
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, typeof(byte[]));
        var notBytesLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notBytesLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, typeof(byte[]));
        il.Emit(OpCodes.Stloc, bytesLocal);
        il.Emit(OpCodes.Br, afterConvertLabel);

        // else: bytes = Encoding.UTF8.GetBytes(data.ToString())
        il.MarkLabel(notBytesLabel);
        il.Emit(OpCodes.Call, typeof(Encoding).GetProperty("UTF8")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
        il.Emit(OpCodes.Callvirt, typeof(Encoding).GetMethod("GetBytes", [typeof(string)])!);
        il.Emit(OpCodes.Stloc, bytesLocal);

        il.MarkLabel(afterConvertLabel);

        // _stream.Write(bytes, 0, bytes.Length)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _wsStreamField);
        il.Emit(OpCodes.Ldloc, bytesLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, bytesLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Callvirt, typeof(Stream).GetMethod("Write", [typeof(byte[]), typeof(int), typeof(int)])!);

        // _bytesWritten += bytes.Length
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _wsBytesWrittenField);
        il.Emit(OpCodes.Ldloc, bytesLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stfld, _wsBytesWrittenField);

        // return true
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);
    }

    private void EmitFsWriteStreamEndBody()
    {
        // public object End(object? data) - writes optional final data and closes
        var il = _wsEndMethod.GetILGenerator();
        var skipWriteLabel = il.DefineLabel();

        // if (data != null) Write(data)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, skipWriteLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _wsWriteMethod);
        il.Emit(OpCodes.Pop);
        il.MarkLabel(skipWriteLabel);

        // _stream.Flush(); _stream.Close()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _wsStreamField);
        il.Emit(OpCodes.Callvirt, typeof(Stream).GetMethod("Flush", Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _wsStreamField);
        il.Emit(OpCodes.Callvirt, typeof(Stream).GetMethod("Close", Type.EmptyTypes)!);

        // return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    private void EmitFsWriteStreamOn()
    {
        // public object On(object eventName, object callback) - stub, returns this
        var method = _fsWriteStreamType.DefineMethod("On",
            MethodAttributes.Public, _types.Object, [_types.Object, _types.Object]);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    #endregion

    #region Factory Methods

    private void EmitFsCreateReadStreamFactory(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        // public static object FsCreateReadStream(object path, object? options)
        var method = runtimeType.DefineMethod("FsCreateReadStream",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object, [_types.Object, _types.Object]);
        runtime.FsCreateReadStream = method;

        var il = method.GetILGenerator();

        // string pathStr = path?.ToString() ?? ""
        var pathStrLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
        il.Emit(OpCodes.Stloc, pathStrLocal);

        // Check for encoding option
        var encodingLocal = il.DeclareLocal(_types.Object);
        var noOptionsLabel = il.DefineLabel();
        var afterEncodingLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, noOptionsLabel);

        // encoding = GetProperty(options, "encoding")
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "encoding");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Stloc, encodingLocal);
        il.Emit(OpCodes.Br, afterEncodingLabel);

        il.MarkLabel(noOptionsLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, encodingLocal);

        il.MarkLabel(afterEncodingLabel);

        // Read file data
        var dataLocal = il.DeclareLocal(_types.Object);
        var bytesReadLocal = il.DeclareLocal(_types.Double);
        var noEncodingLabel = il.DefineLabel();
        var afterReadLabel = il.DefineLabel();

        // if (encoding != null)
        il.Emit(OpCodes.Ldloc, encodingLocal);
        il.Emit(OpCodes.Brfalse, noEncodingLabel);

        // Text mode: data = File.ReadAllText(pathStr)
        il.Emit(OpCodes.Ldloc, pathStrLocal);
        il.Emit(OpCodes.Call, typeof(File).GetMethod("ReadAllText", [typeof(string)])!);
        il.Emit(OpCodes.Stloc, dataLocal);
        // bytesRead = new FileInfo(pathStr).Length
        il.Emit(OpCodes.Ldloc, pathStrLocal);
        il.Emit(OpCodes.Newobj, typeof(FileInfo).GetConstructor([typeof(string)])!);
        il.Emit(OpCodes.Callvirt, typeof(FileInfo).GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Stloc, bytesReadLocal);
        il.Emit(OpCodes.Br, afterReadLabel);

        // Binary mode: data = File.ReadAllBytes(pathStr)
        il.MarkLabel(noEncodingLabel);
        il.Emit(OpCodes.Ldloc, pathStrLocal);
        il.Emit(OpCodes.Call, typeof(File).GetMethod("ReadAllBytes", [typeof(string)])!);
        il.Emit(OpCodes.Stloc, dataLocal);
        // bytesRead = bytes.Length
        il.Emit(OpCodes.Ldloc, dataLocal);
        il.Emit(OpCodes.Castclass, typeof(byte[]));
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Stloc, bytesReadLocal);

        il.MarkLabel(afterReadLabel);

        // return new $FsReadStream(pathStr, data, bytesRead)
        var rsCtor = _fsReadStreamType.GetConstructors()[0];
        il.Emit(OpCodes.Ldloc, pathStrLocal);
        il.Emit(OpCodes.Ldloc, dataLocal);
        il.Emit(OpCodes.Ldloc, bytesReadLocal);
        il.Emit(OpCodes.Newobj, rsCtor);
        il.Emit(OpCodes.Ret);
    }

    private void EmitFsCreateWriteStreamFactory(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        // public static object FsCreateWriteStream(object path, object? options)
        var method = runtimeType.DefineMethod("FsCreateWriteStream",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object, [_types.Object, _types.Object]);
        runtime.FsCreateWriteStream = method;

        var il = method.GetILGenerator();

        // string pathStr = path?.ToString() ?? ""
        var pathStrLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
        il.Emit(OpCodes.Stloc, pathStrLocal);

        // return new $FsWriteStream(pathStr)
        var wsCtor = _fsWriteStreamType.GetConstructors()[0];
        il.Emit(OpCodes.Ldloc, pathStrLocal);
        il.Emit(OpCodes.Newobj, wsCtor);
        il.Emit(OpCodes.Ret);
    }

    #endregion
}
