using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits zlib module helper methods.
    /// All compression logic is emitted inline using BCL compression streams.
    /// </summary>
    private void EmitZlibMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Emit helper methods FIRST (they are used by compression methods)
        EmitGetZlibInputBytes(typeBuilder, runtime);
        EmitGetZlibCompressionLevel(typeBuilder, runtime);
        EmitGetZlibRawLevel(typeBuilder, runtime);
        EmitGetZlibStrategy(typeBuilder, runtime);
        EmitGetBrotliQuality(typeBuilder, runtime);
        EmitGetBrotliWindow(typeBuilder, runtime);
        EmitGetZlibMaxOutputLength(typeBuilder, runtime);
        EmitCopyStreamWithLimit(typeBuilder, runtime);

        // Compression methods
        EmitZlibGzipSync(typeBuilder, runtime);
        EmitZlibGunzipSync(typeBuilder, runtime);
        EmitZlibDeflateSync(typeBuilder, runtime);
        EmitZlibInflateSync(typeBuilder, runtime);
        EmitZlibDeflateRawSync(typeBuilder, runtime);
        EmitZlibInflateRawSync(typeBuilder, runtime);
        EmitZlibBrotliCompressSync(typeBuilder, runtime);
        EmitZlibBrotliDecompressSync(typeBuilder, runtime);
        EmitZlibZstdCompressSync(typeBuilder, runtime);
        EmitZlibZstdDecompressSync(typeBuilder, runtime);
        EmitZlibUnzipSync(typeBuilder, runtime);
        EmitZlibGetConstants(typeBuilder, runtime);
        EmitZlibGetCodes(typeBuilder, runtime);
        EmitZlibCrc32(typeBuilder, runtime);

        // Emit streaming create* methods (pure-IL, no reflection)
        EmitZlibStreamingMethods(typeBuilder, runtime);

        // Emit per-method async wrappers
        EmitZlibAsyncMethods(typeBuilder, runtime);

        // Emit wrapper methods for named imports
        EmitZlibMethodWrappers(typeBuilder, runtime);
    }

    #region Input/Options Helpers

    private MethodBuilder? _getZlibInputBytes;
    private MethodBuilder? _getZlibCompressionLevel;

    /// <summary>
    /// Emits: public static byte[] GetZlibInputBytes(object input)
    /// Extracts bytes from Buffer or string.
    /// </summary>
    private void EmitGetZlibInputBytes(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetZlibInputBytes",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.MakeArrayType(_types.Byte),
            [_types.Object]);
        _getZlibInputBytes = method;

        var il = method.GetILGenerator();

        var isStringLabel = il.DefineLabel();
        var isBufferLabel = il.DefineLabel();
        var throwLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // Check for null
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, throwLabel);

        // Check if string
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, isStringLabel);

        // Check if $Buffer (using isinst with the buffer type)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSBufferType);
        il.Emit(OpCodes.Brtrue, isBufferLabel);

        // Unknown type - throw
        il.Emit(OpCodes.Br, throwLabel);

        // String path: UTF8.GetBytes(str)
        il.MarkLabel(isStringLabel);
        il.Emit(OpCodes.Call, _types.Encoding.GetProperty("UTF8")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, _types.Encoding.GetMethod("GetBytes", [_types.String])!);
        il.Emit(OpCodes.Br, endLabel);

        // Buffer path: call GetData() method
        il.MarkLabel(isBufferLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSBufferType);
        il.Emit(OpCodes.Callvirt, runtime.TSBufferGetData);
        il.Emit(OpCodes.Br, endLabel);

        // Throw error
        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "zlib requires a Buffer or string argument");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static int GetZlibCompressionLevel(object options)
    /// Extracts compression level from options object, returns CompressionLevel enum value.
    /// For now, returns Optimal (2) as the default. Options parsing can be enhanced later.
    /// </summary>
    private void EmitGetZlibCompressionLevel(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetZlibCompressionLevel",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Int32,
            [_types.Object]);
        _getZlibCompressionLevel = method;

        var il = method.GetILGenerator();

        var level0Label = il.DefineLabel();
        var level1Label = il.DefineLabel();
        var level9Label = il.DefineLabel();
        var defaultLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // Read "level" through the generic GetProperty so plain-Dictionary option
        // literals (the common case — CreateObject returns the dict as-is) are honored,
        // not just $Object instances. A null/undefined receiver returns null.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "level");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        var levelLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, levelLocal);

        // Check if level is null or undefined
        il.Emit(OpCodes.Ldloc, levelLocal);
        il.Emit(OpCodes.Brfalse, defaultLabel);

        // Check if it's undefined
        il.Emit(OpCodes.Ldloc, levelLocal);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, defaultLabel);

        // Unbox level as double and convert to int
        il.Emit(OpCodes.Ldloc, levelLocal);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        var levelIntLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Stloc, levelIntLocal);

        // Map zlib level to .NET CompressionLevel
        // .NET CompressionLevel enum values:
        //   Optimal = 0
        //   Fastest = 1
        //   NoCompression = 2
        //   SmallestSize = 3
        // zlib levels: 0=none, 1=fastest, 9=best, -1=default

        il.Emit(OpCodes.Ldloc, levelIntLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Beq, level0Label);

        il.Emit(OpCodes.Ldloc, levelIntLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Beq, level1Label);

        il.Emit(OpCodes.Ldloc, levelIntLocal);
        il.Emit(OpCodes.Ldc_I4, 9);
        il.Emit(OpCodes.Beq, level9Label);

        il.Emit(OpCodes.Br, defaultLabel);

        il.MarkLabel(level0Label);
        il.Emit(OpCodes.Ldc_I4_2); // zlib 0 -> NoCompression (enum value 2)
        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(level1Label);
        il.Emit(OpCodes.Ldc_I4_1); // zlib 1 -> Fastest (enum value 1)
        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(level9Label);
        il.Emit(OpCodes.Ldc_I4_3); // zlib 9 -> SmallestSize (enum value 3)
        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(defaultLabel);
        il.Emit(OpCodes.Ldc_I4_0); // Default -> Optimal (enum value 0)

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    private MethodBuilder? _getZlibRawLevel;
    private MethodBuilder? _getZlibStrategy;
    private MethodBuilder? _getBrotliQuality;
    private MethodBuilder? _getBrotliWindow;
    private MethodBuilder? _getZlibMaxOutputLength;
    private MethodBuilder? _copyStreamWithLimit;

    // Honors the exact Node `level` (-1..9), strategy (0..4) and Brotli params,
    // mirroring ZlibOptions.ToZLibCompressionOptions / GetBrotliQuality/Window so
    // interpreter and compiled emit byte-identical output for non-default options.
    private void EmitGetZlibRawLevel(TypeBuilder typeBuilder, EmittedRuntime runtime)
        => _getZlibRawLevel = EmitIntOptionGetter(typeBuilder, runtime, "GetZlibRawLevel", "level", null, -1, -1, 9);

    private void EmitGetZlibStrategy(TypeBuilder typeBuilder, EmittedRuntime runtime)
        => _getZlibStrategy = EmitIntOptionGetter(typeBuilder, runtime, "GetZlibStrategy", "strategy", null, 0, 0, 4);

    private void EmitGetBrotliQuality(TypeBuilder typeBuilder, EmittedRuntime runtime)
        => _getBrotliQuality = EmitIntOptionGetter(typeBuilder, runtime, "GetZlibBrotliQuality", "1", "params", 11, 0, 11);

    private void EmitGetBrotliWindow(TypeBuilder typeBuilder, EmittedRuntime runtime)
        => _getBrotliWindow = EmitIntOptionGetter(typeBuilder, runtime, "GetZlibBrotliWindow", "2", "params", 22, 10, 24);

    /// <summary>
    /// Emits <c>int Name(object options)</c> that reads a numeric option named
    /// <paramref name="key"/> (optionally nested inside the <c>options[nestedObjectKey]</c>
    /// object — used for Brotli <c>params</c>), clamping it to
    /// [<paramref name="clampLo"/>, <paramref name="clampHi"/>] and falling back to
    /// <paramref name="defaultValue"/> when absent/undefined.
    /// </summary>
    private MethodBuilder EmitIntOptionGetter(TypeBuilder typeBuilder, EmittedRuntime runtime,
        string methodName, string key, string? nestedObjectKey, int defaultValue, int clampLo, int clampHi)
    {
        var method = typeBuilder.DefineMethod(
            methodName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Int32,
            [_types.Object]);

        var il = method.GetILGenerator();

        var def = il.DefineLabel();
        var done = il.DefineLabel();
        var notBelow = il.DefineLabel();

        var resultLocal = il.DeclareLocal(_types.Int32);
        var valLocal = il.DeclareLocal(_types.Object);

        // Use the generic $Runtime.GetProperty(object, string) so this works whether
        // the options value is a plain Dictionary (literal — CreateObject returns the
        // dict as-is) or a $Object. A null/undefined receiver returns null here.
        if (nestedObjectKey != null)
        {
            // nested = GetProperty(options, nestedObjectKey); if null/undefined -> default
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, nestedObjectKey);
            il.Emit(OpCodes.Call, runtime.GetProperty);
            var nestedLocal = il.DeclareLocal(_types.Object);
            il.Emit(OpCodes.Stloc, nestedLocal);
            il.Emit(OpCodes.Ldloc, nestedLocal);
            il.Emit(OpCodes.Brfalse, def);
            il.Emit(OpCodes.Ldloc, nestedLocal);
            il.Emit(OpCodes.Isinst, runtime.UndefinedType);
            il.Emit(OpCodes.Brtrue, def);
            // val = GetProperty(nested, key)
            il.Emit(OpCodes.Ldloc, nestedLocal);
            il.Emit(OpCodes.Ldstr, key);
            il.Emit(OpCodes.Call, runtime.GetProperty);
            il.Emit(OpCodes.Stloc, valLocal);
        }
        else
        {
            // val = GetProperty(options, key)
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, key);
            il.Emit(OpCodes.Call, runtime.GetProperty);
            il.Emit(OpCodes.Stloc, valLocal);
        }

        // if (val == null || val is Undefined) -> default
        il.Emit(OpCodes.Ldloc, valLocal);
        il.Emit(OpCodes.Brfalse, def);
        il.Emit(OpCodes.Ldloc, valLocal);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, def);

        // n = (int)(double)val
        il.Emit(OpCodes.Ldloc, valLocal);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, resultLocal);

        // clamp [clampLo, clampHi]
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4, clampLo);
        il.Emit(OpCodes.Bge, notBelow);
        il.Emit(OpCodes.Ldc_I4, clampLo);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.MarkLabel(notBelow);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4, clampHi);
        il.Emit(OpCodes.Ble, done);
        il.Emit(OpCodes.Ldc_I4, clampHi);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Br, done);

        il.MarkLabel(def);
        il.Emit(OpCodes.Ldc_I4, defaultValue);
        il.Emit(OpCodes.Stloc, resultLocal);

        il.MarkLabel(done);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// Emits <c>long GetZlibMaxOutputLength(object options)</c> — the maxOutputLength
    /// option (0 = no limit), read through the generic GetProperty.
    /// </summary>
    private void EmitGetZlibMaxOutputLength(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetZlibMaxOutputLength",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Int64,
            [_types.Object]);
        _getZlibMaxOutputLength = method;

        var il = method.GetILGenerator();
        var def = il.DefineLabel();
        var valLocal = il.DeclareLocal(_types.Object);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "maxOutputLength");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Stloc, valLocal);

        il.Emit(OpCodes.Ldloc, valLocal);
        il.Emit(OpCodes.Brfalse, def);
        il.Emit(OpCodes.Ldloc, valLocal);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, def);

        // (long)(double)val
        il.Emit(OpCodes.Ldloc, valLocal);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(def);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits <c>void CopyStreamWithLimit(Stream source, MemoryStream dest, long max)</c>
    /// — drains <paramref name="source"/> into <paramref name="dest"/>, throwing when the
    /// running total exceeds <c>max</c> (when max &gt; 0). Mirrors ZlibHelpers.CopyWithLimit.
    /// </summary>
    private void EmitCopyStreamWithLimit(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CopyStreamWithLimit",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.Stream, typeof(MemoryStream), _types.Int64]);
        _copyStreamWithLimit = method;

        var il = method.GetILGenerator();
        var byteArr = _types.MakeArrayType(_types.Byte);
        var streamRead = _types.GetMethod(_types.Stream, "Read", byteArr, _types.Int32, _types.Int32);
        var streamWrite = _types.GetMethod(_types.Stream, "Write", byteArr, _types.Int32, _types.Int32);

        var bufLocal = il.DeclareLocal(byteArr);
        var totalLocal = il.DeclareLocal(_types.Int64);
        var readLocal = il.DeclareLocal(_types.Int32);

        il.Emit(OpCodes.Ldc_I4, 16384);
        il.Emit(OpCodes.Newarr, _types.Byte);
        il.Emit(OpCodes.Stloc, bufLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Stloc, totalLocal);

        var loop = il.DefineLabel();
        var done = il.DefineLabel();
        var noCheck = il.DefineLabel();

        il.MarkLabel(loop);
        // read = source.Read(buf, 0, 16384)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, bufLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4, 16384);
        il.Emit(OpCodes.Callvirt, streamRead);
        il.Emit(OpCodes.Stloc, readLocal);
        // if (read <= 0) done
        il.Emit(OpCodes.Ldloc, readLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, done);
        // if (max <= 0) skip limit check
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Ble, noCheck);
        // total += read; if (total > max) throw
        il.Emit(OpCodes.Ldloc, totalLocal);
        il.Emit(OpCodes.Ldloc, readLocal);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, totalLocal);
        il.Emit(OpCodes.Ldloc, totalLocal);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ble, noCheck);
        il.Emit(OpCodes.Ldstr, "Output length exceeds maxOutputLength");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);
        il.MarkLabel(noCheck);
        // dest.Write(buf, 0, read)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, bufLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, readLocal);
        il.Emit(OpCodes.Callvirt, streamWrite);
        il.Emit(OpCodes.Br, loop);

        il.MarkLabel(done);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the post-compression maxOutputLength guard: throws when
    /// <c>result.Length &gt; GetZlibMaxOutputLength(options)</c> (options = arg1).
    /// </summary>
    private void EmitMaxOutputCheck(ILGenerator il, LocalBuilder resultLocal)
    {
        var maxLocal = il.DeclareLocal(_types.Int64);
        var skip = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _getZlibMaxOutputLength!);
        il.Emit(OpCodes.Stloc, maxLocal);
        // if (max <= 0) skip
        il.Emit(OpCodes.Ldloc, maxLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Ble, skip);
        // if (result.Length <= max) skip
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Ldloc, maxLocal);
        il.Emit(OpCodes.Ble, skip);
        il.Emit(OpCodes.Ldstr, "Output length exceeds maxOutputLength");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);
        il.MarkLabel(skip);
    }

    #endregion

    #region Gzip

    /// <summary>
    /// Emits: public static object ZlibGzipSync(object input, object options)
    /// Uses GZipStream for compression.
    /// </summary>
    private void EmitZlibGzipSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibGzipSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.ZlibGzipSync = method;

        EmitDeflateFamilyCompress(method.GetILGenerator(), runtime, typeof(GZipStream));
    }

    /// <summary>
    /// Emits: public static object ZlibGunzipSync(object input, object options)
    /// Uses GZipStream for decompression.
    /// </summary>
    private void EmitZlibGunzipSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibGunzipSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.ZlibGunzipSync = method;

        EmitDecompressMethod(method.GetILGenerator(), runtime, typeof(GZipStream));
    }

    #endregion

    #region Deflate (with zlib header)

    /// <summary>
    /// Emits: public static object ZlibDeflateSync(object input, object options)
    /// Uses ZLibStream for compression (includes zlib header).
    /// </summary>
    private void EmitZlibDeflateSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibDeflateSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.ZlibDeflateSync = method;

        EmitDeflateFamilyCompress(method.GetILGenerator(), runtime, typeof(ZLibStream));
    }

    /// <summary>
    /// Emits: public static object ZlibInflateSync(object input, object options)
    /// Uses ZLibStream for decompression.
    /// </summary>
    private void EmitZlibInflateSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibInflateSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.ZlibInflateSync = method;

        EmitDecompressMethod(method.GetILGenerator(), runtime, typeof(ZLibStream));
    }

    #endregion

    #region DeflateRaw (no header)

    /// <summary>
    /// Emits: public static object ZlibDeflateRawSync(object input, object options)
    /// Uses DeflateStream for compression (no header).
    /// </summary>
    private void EmitZlibDeflateRawSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibDeflateRawSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.ZlibDeflateRawSync = method;

        EmitDeflateFamilyCompress(method.GetILGenerator(), runtime, typeof(DeflateStream));
    }

    /// <summary>
    /// Emits: public static object ZlibInflateRawSync(object input, object options)
    /// Uses DeflateStream for decompression.
    /// </summary>
    private void EmitZlibInflateRawSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibInflateRawSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.ZlibInflateRawSync = method;

        EmitDecompressMethod(method.GetILGenerator(), runtime, typeof(DeflateStream));
    }

    #endregion

    #region Brotli

    /// <summary>
    /// Emits: public static object ZlibBrotliCompressSync(object input, object options)
    /// Uses BrotliStream for compression.
    /// </summary>
    private void EmitZlibBrotliCompressSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibBrotliCompressSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.ZlibBrotliCompressSync = method;

        EmitBrotliCompress(method.GetILGenerator(), runtime);
    }

    /// <summary>
    /// Emits: public static object ZlibBrotliDecompressSync(object input, object options)
    /// Uses BrotliStream for decompression.
    /// </summary>
    private void EmitZlibBrotliDecompressSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibBrotliDecompressSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.ZlibBrotliDecompressSync = method;

        EmitDecompressMethod(method.GetILGenerator(), runtime, typeof(BrotliStream));
    }

    #endregion

    #region Zstd

    /// <summary>
    /// Emits: public static object ZlibZstdCompressSync(object input, object options)
    /// Uses ZstdSharp.CompressionStream for compression.
    /// </summary>
    private void EmitZlibZstdCompressSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibZstdCompressSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.ZlibZstdCompressSync = method;

        EmitZstdCompressMethod(method.GetILGenerator(), runtime);
    }

    /// <summary>
    /// Emits: public static object ZlibZstdDecompressSync(object input, object options)
    /// Uses ZstdSharp.DecompressionStream for decompression.
    /// </summary>
    private void EmitZlibZstdDecompressSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibZstdDecompressSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.ZlibZstdDecompressSync = method;

        EmitZstdDecompressMethod(method.GetILGenerator(), runtime);
    }

    #endregion

    #region Unzip (Auto-detect)

    /// <summary>
    /// Emits: public static object ZlibUnzipSync(object input, object options)
    /// Auto-detects gzip/deflate format based on magic bytes.
    /// </summary>
    private void EmitZlibUnzipSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibUnzipSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.ZlibUnzipSync = method;

        var il = method.GetILGenerator();

        // Get input bytes
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _getZlibInputBytes!);
        var inputLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));
        il.Emit(OpCodes.Stloc, inputLocal);

        var isGzipLabel = il.DefineLabel();
        var isZlibLabel = il.DefineLabel();
        var tryDeflateLabel = il.DefineLabel();
        var returnLabel = il.DefineLabel();

        // Check if length >= 2
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Blt, tryDeflateLabel);

        // Check for gzip magic: 0x1f 0x8b
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_U1);
        il.Emit(OpCodes.Ldc_I4, 0x1f);
        il.Emit(OpCodes.Bne_Un, isZlibLabel);

        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_U1);
        il.Emit(OpCodes.Ldc_I4, 0x8b);
        il.Emit(OpCodes.Beq, isGzipLabel);

        // Check for zlib header: first byte 0x78
        il.MarkLabel(isZlibLabel);
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_U1);
        il.Emit(OpCodes.Ldc_I4, 0x78);
        il.Emit(OpCodes.Bne_Un, tryDeflateLabel);

        // It's zlib - call ZlibInflateSync
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ZlibInflateSync);
        il.Emit(OpCodes.Ret);

        // It's gzip - call ZlibGunzipSync
        il.MarkLabel(isGzipLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ZlibGunzipSync);
        il.Emit(OpCodes.Ret);

        // Try raw deflate
        il.MarkLabel(tryDeflateLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ZlibInflateRawSync);
        il.Emit(OpCodes.Ret);
    }

    #endregion

    #region Constants

    /// <summary>
    /// Emits: public static object ZlibGetConstants()
    /// Creates a $Object with all zlib constants.
    /// </summary>
    private void EmitZlibGetConstants(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibGetConstants",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            Type.EmptyTypes);
        runtime.ZlibGetConstants = method;

        var il = method.GetILGenerator();

        // Create new $Object with empty dictionary: new $Object(new Dictionary<string, object?>())
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);
        var objLocal = il.DeclareLocal(runtime.TSObjectType);
        il.Emit(OpCodes.Stloc, objLocal);

        // Helper to add constant
        void AddConstant(string name, double value)
        {
            il.Emit(OpCodes.Ldloc, objLocal);
            il.Emit(OpCodes.Ldstr, name);
            il.Emit(OpCodes.Ldc_R8, value);
            il.Emit(OpCodes.Box, _types.Double);
            il.Emit(OpCodes.Callvirt, runtime.TSObjectSetProperty);
        }

        // Compression levels
        AddConstant("Z_NO_COMPRESSION", 0);
        AddConstant("Z_BEST_SPEED", 1);
        AddConstant("Z_BEST_COMPRESSION", 9);
        AddConstant("Z_DEFAULT_COMPRESSION", -1);

        // Strategies
        AddConstant("Z_FILTERED", 1);
        AddConstant("Z_HUFFMAN_ONLY", 2);
        AddConstant("Z_RLE", 3);
        AddConstant("Z_FIXED", 4);
        AddConstant("Z_DEFAULT_STRATEGY", 0);

        // Flush modes
        AddConstant("Z_NO_FLUSH", 0);
        AddConstant("Z_PARTIAL_FLUSH", 1);
        AddConstant("Z_SYNC_FLUSH", 2);
        AddConstant("Z_FULL_FLUSH", 3);
        AddConstant("Z_FINISH", 4);
        AddConstant("Z_BLOCK", 5);
        AddConstant("Z_TREES", 6);

        // Return codes
        AddConstant("Z_OK", 0);
        AddConstant("Z_STREAM_END", 1);
        AddConstant("Z_NEED_DICT", 2);
        AddConstant("Z_ERRNO", -1);
        AddConstant("Z_STREAM_ERROR", -2);
        AddConstant("Z_DATA_ERROR", -3);
        AddConstant("Z_MEM_ERROR", -4);
        AddConstant("Z_BUF_ERROR", -5);
        AddConstant("Z_VERSION_ERROR", -6);

        // Window/memory defaults
        AddConstant("Z_DEFAULT_WINDOWBITS", 15);
        AddConstant("Z_DEFAULT_MEMLEVEL", 8);
        AddConstant("Z_MIN_WINDOWBITS", 8);
        AddConstant("Z_MAX_WINDOWBITS", 15);
        AddConstant("Z_MIN_MEMLEVEL", 1);
        AddConstant("Z_MAX_MEMLEVEL", 9);
        AddConstant("Z_DEFAULT_CHUNK", 16384);
        AddConstant("Z_MIN_CHUNK", 64);
        AddConstant("Z_MAX_CHUNK", double.PositiveInfinity);
        AddConstant("Z_MIN_LEVEL", -1);
        AddConstant("Z_MAX_LEVEL", 9);
        AddConstant("Z_DEFAULT_LEVEL", 6);

        // Codec mode identifiers
        AddConstant("DEFLATE", 1);
        AddConstant("INFLATE", 2);
        AddConstant("GZIP", 3);
        AddConstant("GUNZIP", 4);
        AddConstant("DEFLATERAW", 5);
        AddConstant("INFLATERAW", 6);
        AddConstant("UNZIP", 7);
        AddConstant("BROTLI_DECODE", 8);
        AddConstant("BROTLI_ENCODE", 9);
        AddConstant("ZSTD_COMPRESS", 10);
        AddConstant("ZSTD_DECOMPRESS", 11);

        // Brotli constants
        AddConstant("BROTLI_OPERATION_PROCESS", 0);
        AddConstant("BROTLI_OPERATION_FLUSH", 1);
        AddConstant("BROTLI_OPERATION_FINISH", 2);
        AddConstant("BROTLI_OPERATION_EMIT_METADATA", 3);
        AddConstant("BROTLI_PARAM_MODE", 0);
        AddConstant("BROTLI_PARAM_QUALITY", 1);
        AddConstant("BROTLI_PARAM_LGWIN", 2);
        AddConstant("BROTLI_PARAM_LGBLOCK", 3);
        AddConstant("BROTLI_PARAM_DISABLE_LITERAL_CONTEXT_MODELING", 4);
        AddConstant("BROTLI_PARAM_SIZE_HINT", 5);
        AddConstant("BROTLI_PARAM_LARGE_WINDOW", 6);
        AddConstant("BROTLI_PARAM_NPOSTFIX", 7);
        AddConstant("BROTLI_PARAM_NDIRECT", 8);
        AddConstant("BROTLI_MODE_GENERIC", 0);
        AddConstant("BROTLI_MODE_TEXT", 1);
        AddConstant("BROTLI_MODE_FONT", 2);
        AddConstant("BROTLI_MIN_QUALITY", 0);
        AddConstant("BROTLI_MAX_QUALITY", 11);
        AddConstant("BROTLI_DEFAULT_QUALITY", 11);
        AddConstant("BROTLI_MIN_WINDOW_BITS", 10);
        AddConstant("BROTLI_MAX_WINDOW_BITS", 24);
        AddConstant("BROTLI_LARGE_MAX_WINDOW_BITS", 30);
        AddConstant("BROTLI_DEFAULT_WINDOW", 22);
        AddConstant("BROTLI_DECODER_PARAM_DISABLE_RING_BUFFER_REALLOCATION", 0);
        AddConstant("BROTLI_DECODER_PARAM_LARGE_WINDOW", 1);
        AddConstant("BROTLI_DECODER_RESULT_ERROR", 0);
        AddConstant("BROTLI_DECODER_RESULT_SUCCESS", 1);
        AddConstant("BROTLI_DECODER_RESULT_NEEDS_MORE_INPUT", 2);
        AddConstant("BROTLI_DECODER_RESULT_NEEDS_MORE_OUTPUT", 3);

        // Zstd constants
        AddConstant("ZSTD_c_compressionLevel", 100);
        AddConstant("ZSTD_c_windowLog", 101);
        AddConstant("ZSTD_c_hashLog", 102);
        AddConstant("ZSTD_c_chainLog", 103);
        AddConstant("ZSTD_c_searchLog", 104);
        AddConstant("ZSTD_c_minMatch", 105);
        AddConstant("ZSTD_c_targetLength", 106);
        AddConstant("ZSTD_c_strategy", 107);
        AddConstant("ZSTD_c_checksumFlag", 201);
        AddConstant("ZSTD_c_contentSizeFlag", 200);
        AddConstant("ZSTD_c_dictIDFlag", 202);
        AddConstant("ZSTD_c_nbWorkers", 400);
        AddConstant("ZSTD_c_jobSize", 401);
        AddConstant("ZSTD_c_overlapLog", 402);
        AddConstant("ZSTD_minCLevel", -131072);
        AddConstant("ZSTD_maxCLevel", 22);
        AddConstant("ZSTD_defaultCLevel", 3);
        AddConstant("ZSTD_e_continue", 0);
        AddConstant("ZSTD_e_flush", 1);
        AddConstant("ZSTD_e_end", 2);

        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object ZlibGetCodes()
    /// Creates the bidirectional zlib.codes object (name↔number) mirroring
    /// <c>ZlibConstants.CreateCodesObject</c>.
    /// </summary>
    private void EmitZlibGetCodes(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibGetCodes",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            Type.EmptyTypes);
        runtime.ZlibGetCodes = method;

        var il = method.GetILGenerator();

        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);
        var objLocal = il.DeclareLocal(runtime.TSObjectType);
        il.Emit(OpCodes.Stloc, objLocal);

        void AddNumber(string name, double value)
        {
            il.Emit(OpCodes.Ldloc, objLocal);
            il.Emit(OpCodes.Ldstr, name);
            il.Emit(OpCodes.Ldc_R8, value);
            il.Emit(OpCodes.Box, _types.Double);
            il.Emit(OpCodes.Callvirt, runtime.TSObjectSetProperty);
        }

        void AddString(string key, string value)
        {
            il.Emit(OpCodes.Ldloc, objLocal);
            il.Emit(OpCodes.Ldstr, key);
            il.Emit(OpCodes.Ldstr, value);
            il.Emit(OpCodes.Callvirt, runtime.TSObjectSetProperty);
        }

        foreach (var (name, value) in SharpTS.Runtime.BuiltIns.Modules.ZlibConstants.ReturnCodes)
        {
            AddNumber(name, value);                 // name -> number
            AddString(value.ToString(), name);      // numeric string -> name
        }

        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object ZlibCrc32(object data, object value)
    /// Computes CRC-32 (IEEE 802.3) with a hand-rolled bitwise loop — pure BCL,
    /// byte-for-byte identical to <c>ZlibHelpers.Crc32</c> (interpreter twin), so
    /// standalone output stays free of a System.IO.Hashing dependency.
    /// </summary>
    private void EmitZlibCrc32(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibCrc32",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.ZlibCrc32 = method;

        var il = method.GetILGenerator();

        // byte[] bytes = GetZlibInputBytes(data)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _getZlibInputBytes!);
        var bytesLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));
        il.Emit(OpCodes.Stloc, bytesLocal);

        // uint crc = ~initial   (initial = (value is double) ? (uint)value : 0)
        var initZero = il.DefineLabel();
        var haveInit = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, initZero);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, initZero);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Br, haveInit);
        il.MarkLabel(initZero);
        il.Emit(OpCodes.Ldc_I4_0);
        il.MarkLabel(haveInit);
        il.Emit(OpCodes.Ldc_I4_M1);  // 0xFFFFFFFF
        il.Emit(OpCodes.Xor);        // crc = ~initial
        var crcLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Stloc, crcLocal);

        // for (int i = 0; i < bytes.Length; i++)
        var iLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);
        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, bytesLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, loopEnd);

        // crc ^= bytes[i]
        il.Emit(OpCodes.Ldloc, crcLocal);
        il.Emit(OpCodes.Ldloc, bytesLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_U1);
        il.Emit(OpCodes.Xor);
        il.Emit(OpCodes.Stloc, crcLocal);

        // for (int k = 0; k < 8; k++)
        var kLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, kLocal);
        var innerStart = il.DefineLabel();
        var innerEnd = il.DefineLabel();
        il.MarkLabel(innerStart);
        il.Emit(OpCodes.Ldloc, kLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Bge, innerEnd);

        // crc = (crc >>> 1) ^ (0xEDB88320 & -(crc & 1))
        il.Emit(OpCodes.Ldloc, crcLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)0xEDB88320));
        il.Emit(OpCodes.Ldloc, crcLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Neg);        // mask: 0 or 0xFFFFFFFF
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Xor);
        il.Emit(OpCodes.Stloc, crcLocal);

        il.Emit(OpCodes.Ldloc, kLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, kLocal);
        il.Emit(OpCodes.Br, innerStart);
        il.MarkLabel(innerEnd);

        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStart);
        il.MarkLabel(loopEnd);

        // return (double)(uint)(~crc)
        il.Emit(OpCodes.Ldloc, crcLocal);
        il.Emit(OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Xor);
        il.Emit(OpCodes.Conv_R_Un);  // treat as unsigned -> float
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
    }

    #endregion

    #region Streaming APIs

    /// <summary>
    /// Emits all zlib streaming create* methods and the async callback helper.
    /// </summary>
    private void EmitZlibStreamingMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Create methods for each compression/decompression type
        EmitZlibCreateStreamMethod(typeBuilder, runtime, "ZlibCreateGzip", 0, mb => runtime.ZlibCreateGzip = mb);
        EmitZlibCreateStreamMethod(typeBuilder, runtime, "ZlibCreateGunzip", 1, mb => runtime.ZlibCreateGunzip = mb);
        EmitZlibCreateStreamMethod(typeBuilder, runtime, "ZlibCreateDeflate", 2, mb => runtime.ZlibCreateDeflate = mb);
        EmitZlibCreateStreamMethod(typeBuilder, runtime, "ZlibCreateInflate", 3, mb => runtime.ZlibCreateInflate = mb);
        EmitZlibCreateStreamMethod(typeBuilder, runtime, "ZlibCreateDeflateRaw", 4, mb => runtime.ZlibCreateDeflateRaw = mb);
        EmitZlibCreateStreamMethod(typeBuilder, runtime, "ZlibCreateInflateRaw", 5, mb => runtime.ZlibCreateInflateRaw = mb);
        EmitZlibCreateStreamMethod(typeBuilder, runtime, "ZlibCreateBrotliCompress", 6, mb => runtime.ZlibCreateBrotliCompress = mb);
        EmitZlibCreateStreamMethod(typeBuilder, runtime, "ZlibCreateBrotliDecompress", 7, mb => runtime.ZlibCreateBrotliDecompress = mb);
        EmitZlibCreateStreamMethod(typeBuilder, runtime, "ZlibCreateZstdCompress", 9, mb => runtime.ZlibCreateZstdCompress = mb);
        EmitZlibCreateStreamMethod(typeBuilder, runtime, "ZlibCreateZstdDecompress", 10, mb => runtime.ZlibCreateZstdDecompress = mb);
        EmitZlibCreateStreamMethod(typeBuilder, runtime, "ZlibCreateUnzip", 8, mb => runtime.ZlibCreateUnzip = mb);
        EmitZlibAsyncCallback(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits: public static object CreateXxx(object? options)
    /// Creates a $ZlibTransform directly via newobj (no reflection).
    /// </summary>
    private void EmitZlibCreateStreamMethod(TypeBuilder typeBuilder, EmittedRuntime runtime,
        string methodName, int kindValue, Action<MethodBuilder> setter)
    {
        var method = typeBuilder.DefineMethod(
            methodName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]);
        setter(method);

        var il = method.GetILGenerator();

        // Parse compression level from options (default: CompressionLevel.Optimal = 0)
        // For decompression kinds, level is ignored but we still pass it
        il.Emit(OpCodes.Ldc_I4, kindValue);   // kind
        il.Emit(OpCodes.Ldarg_0);              // options
        il.Emit(OpCodes.Call, _getZlibCompressionLevel!);  // -> int (CompressionLevel)
        il.Emit(OpCodes.Newobj, runtime.TSZlibTransformCtor);  // new $ZlibTransform(kind, level)
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Retained for backward compat — still referenced by EmittedRuntime.ZlibAsyncCallback.
    /// Now a no-op since individual async methods are emitted separately.
    /// </summary>
    private void EmitZlibAsyncCallback(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ZlibAsyncCallback",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]);
        runtime.ZlibAsyncCallback = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits per-method async wrappers: ZlibGzipAsync, ZlibDeflateAsync, etc.
    /// Each calls the corresponding sync method and invokes the callback with (null, result) or (error).
    /// In compiled mode, these run synchronously (acceptable tradeoff).
    /// </summary>
    private void EmitZlibAsyncMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitZlibAsyncMethod(typeBuilder, runtime, "ZlibGzipAsync", () => runtime.ZlibGzipSync, mb => runtime.ZlibGzipAsync = mb);
        EmitZlibAsyncMethod(typeBuilder, runtime, "ZlibGunzipAsync", () => runtime.ZlibGunzipSync, mb => runtime.ZlibGunzipAsync = mb);
        EmitZlibAsyncMethod(typeBuilder, runtime, "ZlibDeflateAsync", () => runtime.ZlibDeflateSync, mb => runtime.ZlibDeflateAsync = mb);
        EmitZlibAsyncMethod(typeBuilder, runtime, "ZlibInflateAsync", () => runtime.ZlibInflateSync, mb => runtime.ZlibInflateAsync = mb);
        EmitZlibAsyncMethod(typeBuilder, runtime, "ZlibDeflateRawAsync", () => runtime.ZlibDeflateRawSync, mb => runtime.ZlibDeflateRawAsync = mb);
        EmitZlibAsyncMethod(typeBuilder, runtime, "ZlibInflateRawAsync", () => runtime.ZlibInflateRawSync, mb => runtime.ZlibInflateRawAsync = mb);
        EmitZlibAsyncMethod(typeBuilder, runtime, "ZlibBrotliCompressAsync", () => runtime.ZlibBrotliCompressSync, mb => runtime.ZlibBrotliCompressAsync = mb);
        EmitZlibAsyncMethod(typeBuilder, runtime, "ZlibBrotliDecompressAsync", () => runtime.ZlibBrotliDecompressSync, mb => runtime.ZlibBrotliDecompressAsync = mb);
        EmitZlibAsyncMethod(typeBuilder, runtime, "ZlibZstdCompressAsync", () => runtime.ZlibZstdCompressSync, mb => runtime.ZlibZstdCompressAsync = mb);
        EmitZlibAsyncMethod(typeBuilder, runtime, "ZlibZstdDecompressAsync", () => runtime.ZlibZstdDecompressSync, mb => runtime.ZlibZstdDecompressAsync = mb);
        EmitZlibAsyncMethod(typeBuilder, runtime, "ZlibUnzipAsync", () => runtime.ZlibUnzipSync, mb => runtime.ZlibUnzipAsync = mb);
    }

    /// <summary>
    /// Emits: public static object ZlibXxxAsync(object input, object? options, object? callback)
    /// Calls syncMethod(input, options), then invokes callback(null, result).
    /// On exception, invokes callback(error.Message).
    /// </summary>
    private void EmitZlibAsyncMethod(TypeBuilder typeBuilder, EmittedRuntime runtime,
        string methodName, Func<MethodBuilder> getSyncMethod, Action<MethodBuilder> setter)
    {
        var method = typeBuilder.DefineMethod(
            methodName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]  // input, options, callback
        );
        setter(method);

        var il = method.GetILGenerator();

        var resultLocal = il.DeclareLocal(_types.Object);

        // try {
        il.BeginExceptionBlock();

        //   result = SyncMethod(input, options)
        il.Emit(OpCodes.Ldarg_0); // input
        il.Emit(OpCodes.Ldarg_1); // options
        il.Emit(OpCodes.Call, getSyncMethod());
        il.Emit(OpCodes.Stloc, resultLocal);

        //   if (callback != null && callback is $TSFunction)
        //     callback.Invoke([null, result])
        var noCallbackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brfalse, noCallbackLabel);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, noCallbackLabel);

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldnull); // error = null
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, resultLocal); // result
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(noCallbackLabel);

        // } catch (Exception ex) {
        il.BeginCatchBlock(typeof(Exception));
        var exLocal = il.DeclareLocal(typeof(Exception));
        il.Emit(OpCodes.Stloc, exLocal);

        //   if (callback != null) callback.Invoke([ex.Message])
        var noCatchCallbackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brfalse, noCatchCallbackLabel);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, noCatchCallbackLabel);

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, exLocal);
        il.Emit(OpCodes.Callvirt, typeof(Exception).GetProperty("Message")!.GetGetMethod()!);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(noCatchCallbackLabel);

        // }
        il.EndExceptionBlock();

        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    #endregion

    #region Method Wrappers for Named Imports

    /// <summary>
    /// Emits wrapper methods for zlib module functions to support named imports.
    /// </summary>
    private void EmitZlibMethodWrappers(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var methodNames = new[]
        {
            ("gzipSync", runtime.ZlibGzipSync),
            ("gunzipSync", runtime.ZlibGunzipSync),
            ("deflateSync", runtime.ZlibDeflateSync),
            ("inflateSync", runtime.ZlibInflateSync),
            ("deflateRawSync", runtime.ZlibDeflateRawSync),
            ("inflateRawSync", runtime.ZlibInflateRawSync),
            ("brotliCompressSync", runtime.ZlibBrotliCompressSync),
            ("brotliDecompressSync", runtime.ZlibBrotliDecompressSync),
            ("zstdCompressSync", runtime.ZlibZstdCompressSync),
            ("zstdDecompressSync", runtime.ZlibZstdDecompressSync),
            ("unzipSync", runtime.ZlibUnzipSync),
            ("crc32", runtime.ZlibCrc32)
        };

        foreach (var (name, targetMethod) in methodNames)
        {
            var wrapper = typeBuilder.DefineMethod(
                "ZlibWrapper_" + name,
                MethodAttributes.Public | MethodAttributes.Static,
                _types.Object,
                [_types.Object, _types.Object]);

            var il = wrapper.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, targetMethod);
            il.Emit(OpCodes.Ret);

            runtime.RegisterBuiltInModuleMethod("zlib", name, wrapper);
        }

        // Streaming create* methods (1 arg: options)
        var streamingMethods = new (string name, MethodBuilder target)[]
        {
            ("createGzip", runtime.ZlibCreateGzip),
            ("createGunzip", runtime.ZlibCreateGunzip),
            ("createDeflate", runtime.ZlibCreateDeflate),
            ("createInflate", runtime.ZlibCreateInflate),
            ("createDeflateRaw", runtime.ZlibCreateDeflateRaw),
            ("createInflateRaw", runtime.ZlibCreateInflateRaw),
            ("createBrotliCompress", runtime.ZlibCreateBrotliCompress),
            ("createBrotliDecompress", runtime.ZlibCreateBrotliDecompress),
            ("createZstdCompress", runtime.ZlibCreateZstdCompress),
            ("createZstdDecompress", runtime.ZlibCreateZstdDecompress),
            ("createUnzip", runtime.ZlibCreateUnzip)
        };

        foreach (var (name, targetMethod) in streamingMethods)
        {
            var wrapper = typeBuilder.DefineMethod(
                "ZlibWrapper_" + name,
                MethodAttributes.Public | MethodAttributes.Static,
                _types.Object,
                [_types.Object, _types.Object]);

            var il = wrapper.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); // options (first arg treated as options)
            il.Emit(OpCodes.Call, targetMethod);
            il.Emit(OpCodes.Ret);

            runtime.RegisterBuiltInModuleMethod("zlib", name, wrapper);
        }

        // Async callback methods (3 args: input, options, callback)
        var asyncMethods = new (string name, MethodBuilder target)[]
        {
            ("gzip", runtime.ZlibGzipAsync),
            ("gunzip", runtime.ZlibGunzipAsync),
            ("deflate", runtime.ZlibDeflateAsync),
            ("inflate", runtime.ZlibInflateAsync),
            ("deflateRaw", runtime.ZlibDeflateRawAsync),
            ("inflateRaw", runtime.ZlibInflateRawAsync),
            ("brotliCompress", runtime.ZlibBrotliCompressAsync),
            ("brotliDecompress", runtime.ZlibBrotliDecompressAsync),
            ("zstdCompress", runtime.ZlibZstdCompressAsync),
            ("zstdDecompress", runtime.ZlibZstdDecompressAsync),
            ("unzip", runtime.ZlibUnzipAsync)
        };

        foreach (var (name, targetMethod) in asyncMethods)
        {
            var wrapper = typeBuilder.DefineMethod(
                "ZlibWrapper_" + name,
                MethodAttributes.Public | MethodAttributes.Static,
                _types.Object,
                [_types.Object, _types.Object, _types.Object]);

            var il = wrapper.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); // input
            il.Emit(OpCodes.Ldarg_1); // options
            il.Emit(OpCodes.Ldarg_2); // callback
            il.Emit(OpCodes.Call, targetMethod);
            il.Emit(OpCodes.Ret);

            runtime.RegisterBuiltInModuleMethod("zlib", name, wrapper);
        }
    }

    #endregion

    #region Compression/Decompression Emission Helpers

    /// <summary>
    /// Emits deflate-family compression (gzip/deflate/deflateRaw) honoring the exact
    /// Node <c>level</c> (-1..9) and <c>strategy</c> via <see cref="ZLibCompressionOptions"/>,
    /// matching <c>ZlibHelpers</c> in the interpreter byte-for-byte. Pure BCL.
    /// </summary>
    private void EmitDeflateFamilyCompress(ILGenerator il, EmittedRuntime runtime, Type streamType)
    {
        var zoptsType = typeof(ZLibCompressionOptions);
        var zoptsCtor = zoptsType.GetConstructor(Type.EmptyTypes)!;
        var setLevel = zoptsType.GetProperty("CompressionLevel")!.GetSetMethod()!;
        var setStrategy = zoptsType.GetProperty("CompressionStrategy")!.GetSetMethod()!;
        var streamCtor = streamType.GetConstructor([typeof(Stream), zoptsType, typeof(bool)])!;

        // Get input bytes
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _getZlibInputBytes!);
        var inputLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));
        il.Emit(OpCodes.Stloc, inputLocal);

        // ZLibCompressionOptions opts = new() { CompressionLevel = level, CompressionStrategy = strategy }
        il.Emit(OpCodes.Newobj, zoptsCtor);
        var optsLocal = il.DeclareLocal(zoptsType);
        il.Emit(OpCodes.Stloc, optsLocal);
        il.Emit(OpCodes.Ldloc, optsLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _getZlibRawLevel!);
        il.Emit(OpCodes.Callvirt, setLevel);
        il.Emit(OpCodes.Ldloc, optsLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _getZlibStrategy!);
        il.Emit(OpCodes.Callvirt, setStrategy);

        // Create output MemoryStream
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(typeof(MemoryStream)));
        var outputLocal = il.DeclareLocal(typeof(MemoryStream));
        il.Emit(OpCodes.Stloc, outputLocal);

        // compress = new streamType(output, opts, leaveOpen: true)
        il.Emit(OpCodes.Ldloc, outputLocal);
        il.Emit(OpCodes.Ldloc, optsLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newobj, streamCtor);
        var compressLocal = il.DeclareLocal(streamType);
        il.Emit(OpCodes.Stloc, compressLocal);

        // Write input
        il.Emit(OpCodes.Ldloc, compressLocal);
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Stream, "Write", _types.MakeArrayType(_types.Byte), _types.Int32, _types.Int32));

        // Dispose compression stream (flushes final data)
        il.Emit(OpCodes.Ldloc, compressLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(IDisposable), "Dispose"));

        // result = output.ToArray()
        il.Emit(OpCodes.Ldloc, outputLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(MemoryStream), "ToArray"));
        var resultLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));
        il.Emit(OpCodes.Stloc, resultLocal);

        il.Emit(OpCodes.Ldloc, outputLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(IDisposable), "Dispose"));

        EmitMaxOutputCheck(il, resultLocal);

        // new $Buffer(result)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Brotli compression honoring the exact <c>BROTLI_PARAM_QUALITY</c> (0-11)
    /// and <c>BROTLI_PARAM_LGWIN</c> (10-24) via the one-shot <see cref="BrotliEncoder"/>,
    /// matching the interpreter byte-for-byte. Pure BCL; mode/size_hint are not applied
    /// (no public BCL knob).
    /// </summary>
    private void EmitBrotliCompress(ILGenerator il, EmittedRuntime runtime)
    {
        var byteArr = _types.MakeArrayType(_types.Byte);
        var roSpanByte = typeof(ReadOnlySpan<byte>);
        var spanByte = typeof(Span<byte>);
        var roSpanOp = roSpanByte.GetMethod("op_Implicit", [byteArr])!;
        var spanOp = spanByte.GetMethod("op_Implicit", [byteArr])!;
        var getMaxLen = typeof(BrotliEncoder).GetMethod("GetMaxCompressedLength", [_types.Int32])!;
        var tryCompress = typeof(BrotliEncoder).GetMethod("TryCompress",
            [roSpanByte, spanByte, _types.Int32.MakeByRefType(), _types.Int32, _types.Int32])!;
        var arrayCopy = typeof(Array).GetMethod("Copy", [typeof(Array), typeof(Array), _types.Int32])!;

        // input = GetZlibInputBytes(arg0)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _getZlibInputBytes!);
        var inputLocal = il.DeclareLocal(byteArr);
        il.Emit(OpCodes.Stloc, inputLocal);

        // quality, window from options
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _getBrotliQuality!);
        var qualityLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Stloc, qualityLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _getBrotliWindow!);
        var windowLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Stloc, windowLocal);

        // dest = new byte[GetMaxCompressedLength(input.Length)]
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Call, getMaxLen);
        il.Emit(OpCodes.Newarr, _types.Byte);
        var destLocal = il.DeclareLocal(byteArr);
        il.Emit(OpCodes.Stloc, destLocal);

        var writtenLocal = il.DeclareLocal(_types.Int32);

        // TryCompress((ReadOnlySpan<byte>)input, (Span<byte>)dest, out written, quality, window)
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Call, roSpanOp);
        il.Emit(OpCodes.Ldloc, destLocal);
        il.Emit(OpCodes.Call, spanOp);
        il.Emit(OpCodes.Ldloca, writtenLocal);
        il.Emit(OpCodes.Ldloc, qualityLocal);
        il.Emit(OpCodes.Ldloc, windowLocal);
        il.Emit(OpCodes.Call, tryCompress);
        il.Emit(OpCodes.Pop); // GetMaxCompressedLength guarantees the buffer fits

        // result = new byte[written]; Array.Copy(dest, result, written)
        il.Emit(OpCodes.Ldloc, writtenLocal);
        il.Emit(OpCodes.Newarr, _types.Byte);
        var resultLocal = il.DeclareLocal(byteArr);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Ldloc, destLocal);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, writtenLocal);
        il.Emit(OpCodes.Call, arrayCopy);

        EmitMaxOutputCheck(il, resultLocal);

        // new $Buffer(result)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits decompression using a BCL compression stream type.
    /// </summary>
    private void EmitDecompressMethod(ILGenerator il, EmittedRuntime runtime, Type streamType)
    {
        // Get input bytes
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _getZlibInputBytes!);
        var inputLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));
        il.Emit(OpCodes.Stloc, inputLocal);

        // Create input MemoryStream
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(typeof(MemoryStream), _types.MakeArrayType(_types.Byte)));
        var inputStreamLocal = il.DeclareLocal(typeof(MemoryStream));
        il.Emit(OpCodes.Stloc, inputStreamLocal);

        // Create decompression stream
        // Constructor: (Stream stream, CompressionMode mode)
        var streamCtor = streamType.GetConstructor([typeof(Stream), typeof(CompressionMode)])!;
        il.Emit(OpCodes.Ldloc, inputStreamLocal);
        il.Emit(OpCodes.Ldc_I4_0); // CompressionMode.Decompress = 0
        il.Emit(OpCodes.Newobj, streamCtor);
        var decompressLocal = il.DeclareLocal(streamType);
        il.Emit(OpCodes.Stloc, decompressLocal);

        // Create output MemoryStream
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(typeof(MemoryStream)));
        var outputLocal = il.DeclareLocal(typeof(MemoryStream));
        il.Emit(OpCodes.Stloc, outputLocal);

        // Copy from decompression stream to output, enforcing maxOutputLength
        il.Emit(OpCodes.Ldloc, decompressLocal);
        il.Emit(OpCodes.Ldloc, outputLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _getZlibMaxOutputLength!);
        il.Emit(OpCodes.Call, _copyStreamWithLimit!);

        // Dispose streams
        il.Emit(OpCodes.Ldloc, decompressLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(IDisposable), "Dispose"));

        il.Emit(OpCodes.Ldloc, inputStreamLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(IDisposable), "Dispose"));

        // Get result
        il.Emit(OpCodes.Ldloc, outputLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(MemoryStream), "ToArray"));
        var resultLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));
        il.Emit(OpCodes.Stloc, resultLocal);

        // Dispose output stream
        il.Emit(OpCodes.Ldloc, outputLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(IDisposable), "Dispose"));

        // Create $Buffer from result
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits zstd compression using ZstdSharp.CompressionStream.
    /// Constructor: CompressionStream(Stream stream, int level, int bufferSize, bool leaveOpen)
    /// </summary>
    private void EmitZstdCompressMethod(ILGenerator il, EmittedRuntime runtime)
    {
        // Get input bytes
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _getZlibInputBytes!);
        var inputLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));
        il.Emit(OpCodes.Stloc, inputLocal);

        // Get compression level (reuse helper, maps to int)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _getZlibCompressionLevel!);
        var levelLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Stloc, levelLocal);

        // Create output MemoryStream
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(typeof(MemoryStream)));
        var outputLocal = il.DeclareLocal(typeof(MemoryStream));
        il.Emit(OpCodes.Stloc, outputLocal);

        // Map CompressionLevel enum to zstd int level:
        // Optimal(0)->3, Fastest(1)->1, NoCompression(2)->0, SmallestSize(3)->19
        var zstdLevelLocal = il.DeclareLocal(_types.Int32);
        var lvl1Label = il.DefineLabel();
        var lvl2Label = il.DefineLabel();
        var lvl3Label = il.DefineLabel();
        var lvlDoneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, levelLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Beq, lvl1Label);
        il.Emit(OpCodes.Ldloc, levelLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Beq, lvl2Label);
        il.Emit(OpCodes.Ldloc, levelLocal);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Beq, lvl3Label);
        // Default (Optimal=0): zstd level 3
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Stloc, zstdLevelLocal);
        il.Emit(OpCodes.Br, lvlDoneLabel);

        il.MarkLabel(lvl1Label); // Fastest -> 1
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, zstdLevelLocal);
        il.Emit(OpCodes.Br, lvlDoneLabel);

        il.MarkLabel(lvl2Label); // NoCompression -> 0 (store uncompressed isn't really supported, use min)
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, zstdLevelLocal);
        il.Emit(OpCodes.Br, lvlDoneLabel);

        il.MarkLabel(lvl3Label); // SmallestSize -> 19
        il.Emit(OpCodes.Ldc_I4, 19);
        il.Emit(OpCodes.Stloc, zstdLevelLocal);

        il.MarkLabel(lvlDoneLabel);

        // Create CompressionStream(outputMs, zstdLevel, bufferSize=0, leaveOpen=true)
        var csType = typeof(ZstdSharp.CompressionStream);
        var csCtor = csType.GetConstructor([typeof(Stream), typeof(int), typeof(int), typeof(bool)])!;
        il.Emit(OpCodes.Ldloc, outputLocal);
        il.Emit(OpCodes.Ldloc, zstdLevelLocal);
        il.Emit(OpCodes.Ldc_I4_0);  // bufferSize = 0 (default)
        il.Emit(OpCodes.Ldc_I4_1);  // leaveOpen = true
        il.Emit(OpCodes.Newobj, csCtor);
        var compressLocal = il.DeclareLocal(csType);
        il.Emit(OpCodes.Stloc, compressLocal);

        // Write input to compression stream
        il.Emit(OpCodes.Ldloc, compressLocal);
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Stream, "Write", _types.MakeArrayType(_types.Byte), _types.Int32, _types.Int32));

        // Dispose compression stream (flushes final data)
        il.Emit(OpCodes.Ldloc, compressLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(IDisposable), "Dispose"));

        // Get result from output stream
        il.Emit(OpCodes.Ldloc, outputLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(MemoryStream), "ToArray"));
        var resultLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));
        il.Emit(OpCodes.Stloc, resultLocal);

        // Dispose output stream
        il.Emit(OpCodes.Ldloc, outputLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(IDisposable), "Dispose"));

        EmitMaxOutputCheck(il, resultLocal);

        // Create $Buffer from result
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits zstd decompression using ZstdSharp.DecompressionStream.
    /// Constructor: DecompressionStream(Stream stream, int bufferSize, bool checkEndOfStream, bool leaveOpen)
    /// </summary>
    private void EmitZstdDecompressMethod(ILGenerator il, EmittedRuntime runtime)
    {
        // Get input bytes
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _getZlibInputBytes!);
        var inputLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));
        il.Emit(OpCodes.Stloc, inputLocal);

        // Create input MemoryStream
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(typeof(MemoryStream), _types.MakeArrayType(_types.Byte)));
        var inputStreamLocal = il.DeclareLocal(typeof(MemoryStream));
        il.Emit(OpCodes.Stloc, inputStreamLocal);

        // Create DecompressionStream(inputMs, bufferSize=0, checkEndOfStream=false, leaveOpen=false)
        var dsType = typeof(ZstdSharp.DecompressionStream);
        var dsCtor = dsType.GetConstructor([typeof(Stream), typeof(int), typeof(bool), typeof(bool)])!;
        il.Emit(OpCodes.Ldloc, inputStreamLocal);
        il.Emit(OpCodes.Ldc_I4_0);  // bufferSize = 0 (default)
        il.Emit(OpCodes.Ldc_I4_0);  // checkEndOfStream = false
        il.Emit(OpCodes.Ldc_I4_0);  // leaveOpen = false
        il.Emit(OpCodes.Newobj, dsCtor);
        var decompressLocal = il.DeclareLocal(dsType);
        il.Emit(OpCodes.Stloc, decompressLocal);

        // Create output MemoryStream
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(typeof(MemoryStream)));
        var outputLocal = il.DeclareLocal(typeof(MemoryStream));
        il.Emit(OpCodes.Stloc, outputLocal);

        // Copy from decompression stream to output, enforcing maxOutputLength
        il.Emit(OpCodes.Ldloc, decompressLocal);
        il.Emit(OpCodes.Ldloc, outputLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _getZlibMaxOutputLength!);
        il.Emit(OpCodes.Call, _copyStreamWithLimit!);

        // Dispose streams
        il.Emit(OpCodes.Ldloc, decompressLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(IDisposable), "Dispose"));

        il.Emit(OpCodes.Ldloc, inputStreamLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(IDisposable), "Dispose"));

        // Get result
        il.Emit(OpCodes.Ldloc, outputLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(MemoryStream), "ToArray"));
        var resultLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));
        il.Emit(OpCodes.Stloc, resultLocal);

        // Dispose output stream
        il.Emit(OpCodes.Ldloc, outputLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(typeof(IDisposable), "Dispose"));

        // Create $Buffer from result
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);
    }

    #endregion
}
