using System.Reflection.Emit;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters.Modules;

/// <summary>
/// Emits IL code for the Node.js 'zlib' module.
/// </summary>
/// <remarks>
/// Provides compression/decompression functionality:
/// - gzipSync/gunzipSync: Gzip format
/// - deflateSync/inflateSync: Deflate with zlib header
/// - deflateRawSync/inflateRawSync: Raw deflate (no header)
/// - brotliCompressSync/brotliDecompressSync: Brotli format
/// - zstdCompressSync/zstdDecompressSync: Zstandard format
/// - constants: Compression constants object
/// </remarks>
public sealed class ZlibModuleEmitter : IBuiltInModuleEmitter
{
    public string ModuleName => "zlib";

    private static readonly string[] _exportedMembers =
    [
        "gzipSync", "gunzipSync",
        "deflateSync", "inflateSync",
        "deflateRawSync", "inflateRawSync",
        "brotliCompressSync", "brotliDecompressSync",
        "zstdCompressSync", "zstdDecompressSync",
        "unzipSync",
        // Streaming APIs
        "createGzip", "createGunzip",
        "createDeflate", "createInflate",
        "createDeflateRaw", "createInflateRaw",
        "createBrotliCompress", "createBrotliDecompress",
        "createZstdCompress", "createZstdDecompress",
        "createUnzip",
        // Async callback APIs
        "gzip", "gunzip",
        "deflate", "inflate",
        "deflateRaw", "inflateRaw",
        "brotliCompress", "brotliDecompress",
        "zstdCompress", "zstdDecompress",
        "unzip",
        // Checksums
        "crc32",
        "constants", "codes"
    ];

    public IReadOnlyList<string> GetExportedMembers() => _exportedMembers;

    public bool TryEmitMethodCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        return methodName switch
        {
            "gzipSync" => EmitCompressionMethod(emitter, arguments, "ZlibGzipSync"),
            "gunzipSync" => EmitCompressionMethod(emitter, arguments, "ZlibGunzipSync"),
            "deflateSync" => EmitCompressionMethod(emitter, arguments, "ZlibDeflateSync"),
            "inflateSync" => EmitCompressionMethod(emitter, arguments, "ZlibInflateSync"),
            "deflateRawSync" => EmitCompressionMethod(emitter, arguments, "ZlibDeflateRawSync"),
            "inflateRawSync" => EmitCompressionMethod(emitter, arguments, "ZlibInflateRawSync"),
            "brotliCompressSync" => EmitCompressionMethod(emitter, arguments, "ZlibBrotliCompressSync"),
            "brotliDecompressSync" => EmitCompressionMethod(emitter, arguments, "ZlibBrotliDecompressSync"),
            "zstdCompressSync" => EmitCompressionMethod(emitter, arguments, "ZlibZstdCompressSync"),
            "zstdDecompressSync" => EmitCompressionMethod(emitter, arguments, "ZlibZstdDecompressSync"),
            "unzipSync" => EmitCompressionMethod(emitter, arguments, "ZlibUnzipSync"),
            // Streaming APIs
            "createGzip" => EmitCreateStream(emitter, arguments, "ZlibCreateGzip"),
            "createGunzip" => EmitCreateStream(emitter, arguments, "ZlibCreateGunzip"),
            "createDeflate" => EmitCreateStream(emitter, arguments, "ZlibCreateDeflate"),
            "createInflate" => EmitCreateStream(emitter, arguments, "ZlibCreateInflate"),
            "createDeflateRaw" => EmitCreateStream(emitter, arguments, "ZlibCreateDeflateRaw"),
            "createInflateRaw" => EmitCreateStream(emitter, arguments, "ZlibCreateInflateRaw"),
            "createBrotliCompress" => EmitCreateStream(emitter, arguments, "ZlibCreateBrotliCompress"),
            "createBrotliDecompress" => EmitCreateStream(emitter, arguments, "ZlibCreateBrotliDecompress"),
            "createZstdCompress" => EmitCreateStream(emitter, arguments, "ZlibCreateZstdCompress"),
            "createZstdDecompress" => EmitCreateStream(emitter, arguments, "ZlibCreateZstdDecompress"),
            "createUnzip" => EmitCreateStream(emitter, arguments, "ZlibCreateUnzip"),
            // Async callback APIs
            "gzip" => EmitAsyncMethod(emitter, arguments, "ZlibGzipAsync"),
            "gunzip" => EmitAsyncMethod(emitter, arguments, "ZlibGunzipAsync"),
            "deflate" => EmitAsyncMethod(emitter, arguments, "ZlibDeflateAsync"),
            "inflate" => EmitAsyncMethod(emitter, arguments, "ZlibInflateAsync"),
            "deflateRaw" => EmitAsyncMethod(emitter, arguments, "ZlibDeflateRawAsync"),
            "inflateRaw" => EmitAsyncMethod(emitter, arguments, "ZlibInflateRawAsync"),
            "brotliCompress" => EmitAsyncMethod(emitter, arguments, "ZlibBrotliCompressAsync"),
            "brotliDecompress" => EmitAsyncMethod(emitter, arguments, "ZlibBrotliDecompressAsync"),
            "zstdCompress" => EmitAsyncMethod(emitter, arguments, "ZlibZstdCompressAsync"),
            "zstdDecompress" => EmitAsyncMethod(emitter, arguments, "ZlibZstdDecompressAsync"),
            "unzip" => EmitAsyncMethod(emitter, arguments, "ZlibUnzipAsync"),
            // Checksums (Node 22+): crc32(data[, value])
            "crc32" => EmitCrc32(emitter, arguments),
            _ => false
        };
    }

    /// <summary>
    /// Emits crc32(data[, value]) -> number. Routes to $Runtime.ZlibCrc32(object, object).
    /// </summary>
    private static bool EmitCrc32(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // data argument
        if (arguments.Count == 0)
        {
            il.Emit(OpCodes.Ldnull);
        }
        else
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }

        // optional running value
        if (arguments.Count >= 2)
        {
            emitter.EmitExpression(arguments[1]);
            emitter.EmitBoxIfNeeded(arguments[1]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        il.Emit(OpCodes.Call, ctx.Runtime!.ZlibCrc32);
        return true;
    }

    public bool TryEmitPropertyGet(IEmitterContext emitter, string propertyName)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        switch (propertyName)
        {
            case "constants":
                il.Emit(OpCodes.Call, ctx.Runtime!.ZlibGetConstants);
                return true;
            case "codes":
                il.Emit(OpCodes.Call, ctx.Runtime!.ZlibGetCodes);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Emits a createXxx() streaming method call.
    /// Pattern: CreateMethod(object? options) -> object (ZlibTransform)
    /// </summary>
    private static bool EmitCreateStream(IEmitterContext emitter, List<Expr> arguments, string runtimeMethodName)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Emit options argument (null if not provided)
        if (arguments.Count >= 1)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        // Get the runtime method
        var method = runtimeMethodName switch
        {
            "ZlibCreateGzip" => ctx.Runtime!.ZlibCreateGzip,
            "ZlibCreateGunzip" => ctx.Runtime!.ZlibCreateGunzip,
            "ZlibCreateDeflate" => ctx.Runtime!.ZlibCreateDeflate,
            "ZlibCreateInflate" => ctx.Runtime!.ZlibCreateInflate,
            "ZlibCreateDeflateRaw" => ctx.Runtime!.ZlibCreateDeflateRaw,
            "ZlibCreateInflateRaw" => ctx.Runtime!.ZlibCreateInflateRaw,
            "ZlibCreateBrotliCompress" => ctx.Runtime!.ZlibCreateBrotliCompress,
            "ZlibCreateBrotliDecompress" => ctx.Runtime!.ZlibCreateBrotliDecompress,
            "ZlibCreateZstdCompress" => ctx.Runtime!.ZlibCreateZstdCompress,
            "ZlibCreateZstdDecompress" => ctx.Runtime!.ZlibCreateZstdDecompress,
            "ZlibCreateUnzip" => ctx.Runtime!.ZlibCreateUnzip,
            _ => throw new CompileException($"Unknown zlib streaming method: {runtimeMethodName}")
        };

        il.Emit(OpCodes.Call, method);
        return true;
    }

    /// <summary>
    /// Emits an async callback zlib method call.
    /// Pattern: Method(input, [options,] callback) -> null
    /// Calls the per-method async wrapper which calls sync + invokes callback.
    /// </summary>
    private static bool EmitAsyncMethod(IEmitterContext emitter, List<Expr> arguments, string asyncMethodName)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Emit input argument
        if (arguments.Count == 0)
        {
            il.Emit(OpCodes.Ldnull);
        }
        else
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }

        // Emit options argument (null if callback is arg[1])
        if (arguments.Count >= 3)
        {
            emitter.EmitExpression(arguments[1]);
            emitter.EmitBoxIfNeeded(arguments[1]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        // Emit callback argument (last arg)
        if (arguments.Count >= 2)
        {
            var cbIndex = arguments.Count >= 3 ? 2 : 1;
            emitter.EmitExpression(arguments[cbIndex]);
            emitter.EmitBoxIfNeeded(arguments[cbIndex]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        // Call the per-method async wrapper: XxxAsync(input, options, callback)
        var asyncMethod = asyncMethodName switch
        {
            "ZlibGzipAsync" => ctx.Runtime!.ZlibGzipAsync,
            "ZlibGunzipAsync" => ctx.Runtime!.ZlibGunzipAsync,
            "ZlibDeflateAsync" => ctx.Runtime!.ZlibDeflateAsync,
            "ZlibInflateAsync" => ctx.Runtime!.ZlibInflateAsync,
            "ZlibDeflateRawAsync" => ctx.Runtime!.ZlibDeflateRawAsync,
            "ZlibInflateRawAsync" => ctx.Runtime!.ZlibInflateRawAsync,
            "ZlibBrotliCompressAsync" => ctx.Runtime!.ZlibBrotliCompressAsync,
            "ZlibBrotliDecompressAsync" => ctx.Runtime!.ZlibBrotliDecompressAsync,
            "ZlibZstdCompressAsync" => ctx.Runtime!.ZlibZstdCompressAsync,
            "ZlibZstdDecompressAsync" => ctx.Runtime!.ZlibZstdDecompressAsync,
            "ZlibUnzipAsync" => ctx.Runtime!.ZlibUnzipAsync,
            _ => throw new CompileException($"Unknown zlib async method: {asyncMethodName}")
        };

        il.Emit(OpCodes.Call, asyncMethod);
        return true;
    }

    /// <summary>
    /// Emits a compression/decompression method call.
    /// All methods follow the pattern: MethodName(object input, object? options) -> object (Buffer)
    /// </summary>
    private static bool EmitCompressionMethod(IEmitterContext emitter, List<Expr> arguments, string runtimeMethodName)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Emit input argument
        if (arguments.Count == 0)
        {
            il.Emit(OpCodes.Ldnull);
        }
        else
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }

        // Emit options argument (null if not provided)
        if (arguments.Count >= 2)
        {
            emitter.EmitExpression(arguments[1]);
            emitter.EmitBoxIfNeeded(arguments[1]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        // Get the appropriate runtime method
        var method = runtimeMethodName switch
        {
            "ZlibGzipSync" => ctx.Runtime!.ZlibGzipSync,
            "ZlibGunzipSync" => ctx.Runtime!.ZlibGunzipSync,
            "ZlibDeflateSync" => ctx.Runtime!.ZlibDeflateSync,
            "ZlibInflateSync" => ctx.Runtime!.ZlibInflateSync,
            "ZlibDeflateRawSync" => ctx.Runtime!.ZlibDeflateRawSync,
            "ZlibInflateRawSync" => ctx.Runtime!.ZlibInflateRawSync,
            "ZlibBrotliCompressSync" => ctx.Runtime!.ZlibBrotliCompressSync,
            "ZlibBrotliDecompressSync" => ctx.Runtime!.ZlibBrotliDecompressSync,
            "ZlibZstdCompressSync" => ctx.Runtime!.ZlibZstdCompressSync,
            "ZlibZstdDecompressSync" => ctx.Runtime!.ZlibZstdDecompressSync,
            "ZlibUnzipSync" => ctx.Runtime!.ZlibUnzipSync,
            _ => throw new CompileException($"Unknown zlib method: {runtimeMethodName}")
        };

        il.Emit(OpCodes.Call, method);
        return true;
    }
}
