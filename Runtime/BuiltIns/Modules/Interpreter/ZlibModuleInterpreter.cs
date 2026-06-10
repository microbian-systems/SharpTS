using SharpTS.Runtime.Types;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of the Node.js 'zlib' module.
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
public static class ZlibModuleInterpreter
{
    /// <summary>
    /// Gets all exported values for the zlib module.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            // Gzip
            ["gzipSync"] = BuiltInMethod.CreateV2("gzipSync", 1, 2, GzipSync),
            ["gunzipSync"] = BuiltInMethod.CreateV2("gunzipSync", 1, 2, GunzipSync),

            // Deflate (with zlib header)
            ["deflateSync"] = BuiltInMethod.CreateV2("deflateSync", 1, 2, DeflateSync),
            ["inflateSync"] = BuiltInMethod.CreateV2("inflateSync", 1, 2, InflateSync),

            // DeflateRaw (no header)
            ["deflateRawSync"] = BuiltInMethod.CreateV2("deflateRawSync", 1, 2, DeflateRawSync),
            ["inflateRawSync"] = BuiltInMethod.CreateV2("inflateRawSync", 1, 2, InflateRawSync),

            // Brotli
            ["brotliCompressSync"] = BuiltInMethod.CreateV2("brotliCompressSync", 1, 2, BrotliCompressSync),
            ["brotliDecompressSync"] = BuiltInMethod.CreateV2("brotliDecompressSync", 1, 2, BrotliDecompressSync),

            // Zstd
            ["zstdCompressSync"] = BuiltInMethod.CreateV2("zstdCompressSync", 1, 2, ZstdCompressSync),
            ["zstdDecompressSync"] = BuiltInMethod.CreateV2("zstdDecompressSync", 1, 2, ZstdDecompressSync),

            // Unzip (auto-detect gzip/deflate)
            ["unzipSync"] = BuiltInMethod.CreateV2("unzipSync", 1, 2, UnzipSync),

            // Streaming APIs (Transform streams)
            ["createGzip"] = BuiltInMethod.CreateV2("createGzip", 0, 1, CreateGzip),
            ["createGunzip"] = BuiltInMethod.CreateV2("createGunzip", 0, 1, CreateGunzip),
            ["createDeflate"] = BuiltInMethod.CreateV2("createDeflate", 0, 1, CreateDeflate),
            ["createInflate"] = BuiltInMethod.CreateV2("createInflate", 0, 1, CreateInflate),
            ["createDeflateRaw"] = BuiltInMethod.CreateV2("createDeflateRaw", 0, 1, CreateDeflateRaw),
            ["createInflateRaw"] = BuiltInMethod.CreateV2("createInflateRaw", 0, 1, CreateInflateRaw),
            ["createBrotliCompress"] = BuiltInMethod.CreateV2("createBrotliCompress", 0, 1, CreateBrotliCompress),
            ["createBrotliDecompress"] = BuiltInMethod.CreateV2("createBrotliDecompress", 0, 1, CreateBrotliDecompress),
            ["createZstdCompress"] = BuiltInMethod.CreateV2("createZstdCompress", 0, 1, CreateZstdCompress),
            ["createZstdDecompress"] = BuiltInMethod.CreateV2("createZstdDecompress", 0, 1, CreateZstdDecompress),
            ["createUnzip"] = BuiltInMethod.CreateV2("createUnzip", 0, 1, CreateUnzip),

            // Async callback APIs
            ["gzip"] = BuiltInMethod.CreateV2("gzip", 2, 3, GzipAsync),
            ["gunzip"] = BuiltInMethod.CreateV2("gunzip", 2, 3, GunzipAsync),
            ["deflate"] = BuiltInMethod.CreateV2("deflate", 2, 3, DeflateAsync),
            ["inflate"] = BuiltInMethod.CreateV2("inflate", 2, 3, InflateAsync),
            ["deflateRaw"] = BuiltInMethod.CreateV2("deflateRaw", 2, 3, DeflateRawAsync),
            ["inflateRaw"] = BuiltInMethod.CreateV2("inflateRaw", 2, 3, InflateRawAsync),
            ["brotliCompress"] = BuiltInMethod.CreateV2("brotliCompress", 2, 3, BrotliCompressAsync),
            ["brotliDecompress"] = BuiltInMethod.CreateV2("brotliDecompress", 2, 3, BrotliDecompressAsync),
            ["zstdCompress"] = BuiltInMethod.CreateV2("zstdCompress", 2, 3, ZstdCompressAsync),
            ["zstdDecompress"] = BuiltInMethod.CreateV2("zstdDecompress", 2, 3, ZstdDecompressAsync),
            ["unzip"] = BuiltInMethod.CreateV2("unzip", 2, 3, UnzipAsync),

            // Constants
            ["constants"] = ZlibConstants.CreateConstantsObject()
        };
    }

    #region Gzip

    private static RuntimeValue GzipSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var input = GetInputBytes(args, 0, "gzipSync");
        var options = GetOptions(args, 1);

        var result = ZlibHelpers.GzipCompress(input, options);
        return RuntimeValue.FromObject(new SharpTSBuffer(result));
    }

    private static RuntimeValue GunzipSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var input = GetInputBytes(args, 0, "gunzipSync");
        var options = GetOptions(args, 1);

        try
        {
            var result = ZlibHelpers.GzipDecompress(input, options);
            return RuntimeValue.FromObject(new SharpTSBuffer(result));
        }
        catch (InvalidDataException)
        {
            throw new Exception("Error: incorrect header check");
        }
    }

    #endregion

    #region Deflate

    private static RuntimeValue DeflateSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var input = GetInputBytes(args, 0, "deflateSync");
        var options = GetOptions(args, 1);

        var result = ZlibHelpers.DeflateCompress(input, options);
        return RuntimeValue.FromObject(new SharpTSBuffer(result));
    }

    private static RuntimeValue InflateSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var input = GetInputBytes(args, 0, "inflateSync");
        var options = GetOptions(args, 1);

        try
        {
            var result = ZlibHelpers.DeflateDecompress(input, options);
            return RuntimeValue.FromObject(new SharpTSBuffer(result));
        }
        catch (InvalidDataException)
        {
            throw new Exception("Error: incorrect header check");
        }
    }

    #endregion

    #region DeflateRaw

    private static RuntimeValue DeflateRawSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var input = GetInputBytes(args, 0, "deflateRawSync");
        var options = GetOptions(args, 1);

        var result = ZlibHelpers.DeflateRawCompress(input, options);
        return RuntimeValue.FromObject(new SharpTSBuffer(result));
    }

    private static RuntimeValue InflateRawSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var input = GetInputBytes(args, 0, "inflateRawSync");
        var options = GetOptions(args, 1);

        try
        {
            var result = ZlibHelpers.DeflateRawDecompress(input, options);
            return RuntimeValue.FromObject(new SharpTSBuffer(result));
        }
        catch (InvalidDataException)
        {
            throw new Exception("Error: incorrect header check");
        }
    }

    #endregion

    #region Brotli

    private static RuntimeValue BrotliCompressSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var input = GetInputBytes(args, 0, "brotliCompressSync");
        var options = GetOptions(args, 1);

        var result = ZlibHelpers.BrotliCompress(input, options);
        return RuntimeValue.FromObject(new SharpTSBuffer(result));
    }

    private static RuntimeValue BrotliDecompressSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var input = GetInputBytes(args, 0, "brotliDecompressSync");
        var options = GetOptions(args, 1);

        try
        {
            var result = ZlibHelpers.BrotliDecompress(input, options);
            return RuntimeValue.FromObject(new SharpTSBuffer(result));
        }
        catch (InvalidDataException)
        {
            throw new Exception("Error: Decompression failed");
        }
    }

    #endregion

    #region Zstd

    private static RuntimeValue ZstdCompressSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var input = GetInputBytes(args, 0, "zstdCompressSync");
        var options = GetOptions(args, 1);

        var result = ZlibHelpers.ZstdCompress(input, options);
        return RuntimeValue.FromObject(new SharpTSBuffer(result));
    }

    private static RuntimeValue ZstdDecompressSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var input = GetInputBytes(args, 0, "zstdDecompressSync");
        var options = GetOptions(args, 1);

        try
        {
            var result = ZlibHelpers.ZstdDecompress(input, options);
            return RuntimeValue.FromObject(new SharpTSBuffer(result));
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new Exception($"Error: Zstd decompression failed: {ex.Message}");
        }
    }

    #endregion

    #region Unzip (Auto-detect)

    private static RuntimeValue UnzipSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var input = GetInputBytes(args, 0, "unzipSync");
        var options = GetOptions(args, 1);

        // Try to auto-detect format from magic bytes
        if (input.Length >= 2)
        {
            // Gzip magic: 0x1f 0x8b
            if (input[0] == 0x1f && input[1] == 0x8b)
            {
                var result = ZlibHelpers.GzipDecompress(input, options);
                return RuntimeValue.FromObject(new SharpTSBuffer(result));
            }

            // Zlib header: first byte typically 0x78 (deflate)
            // 0x78 0x01 = no compression
            // 0x78 0x5e = fast compression
            // 0x78 0x9c = default compression
            // 0x78 0xda = best compression
            if (input[0] == 0x78)
            {
                var result = ZlibHelpers.DeflateDecompress(input, options);
                return RuntimeValue.FromObject(new SharpTSBuffer(result));
            }
        }

        // Fallback: try raw deflate
        try
        {
            var result = ZlibHelpers.DeflateRawDecompress(input, options);
            return RuntimeValue.FromObject(new SharpTSBuffer(result));
        }
        catch
        {
            throw new Exception("Error: unknown compression format");
        }
    }

    #endregion

    #region Streaming APIs

    private static RuntimeValue CreateGzip(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromObject(new SharpTSZlibTransform(ZlibTransformKind.Gzip, GetOptions(args, 0)));

    private static RuntimeValue CreateGunzip(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromObject(new SharpTSZlibTransform(ZlibTransformKind.Gunzip, GetOptions(args, 0)));

    private static RuntimeValue CreateDeflate(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromObject(new SharpTSZlibTransform(ZlibTransformKind.Deflate, GetOptions(args, 0)));

    private static RuntimeValue CreateInflate(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromObject(new SharpTSZlibTransform(ZlibTransformKind.Inflate, GetOptions(args, 0)));

    private static RuntimeValue CreateDeflateRaw(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromObject(new SharpTSZlibTransform(ZlibTransformKind.DeflateRaw, GetOptions(args, 0)));

    private static RuntimeValue CreateInflateRaw(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromObject(new SharpTSZlibTransform(ZlibTransformKind.InflateRaw, GetOptions(args, 0)));

    private static RuntimeValue CreateBrotliCompress(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromObject(new SharpTSZlibTransform(ZlibTransformKind.BrotliCompress, GetOptions(args, 0)));

    private static RuntimeValue CreateBrotliDecompress(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromObject(new SharpTSZlibTransform(ZlibTransformKind.BrotliDecompress, GetOptions(args, 0)));

    private static RuntimeValue CreateZstdCompress(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromObject(new SharpTSZlibTransform(ZlibTransformKind.ZstdCompress, GetOptions(args, 0)));

    private static RuntimeValue CreateZstdDecompress(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromObject(new SharpTSZlibTransform(ZlibTransformKind.ZstdDecompress, GetOptions(args, 0)));

    private static RuntimeValue CreateUnzip(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromObject(new SharpTSZlibTransform(ZlibTransformKind.Unzip, GetOptions(args, 0)));

    #endregion

    #region Async Callback APIs

    private static RuntimeValue GzipAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RunAsync(interpreter, args, "gzip", ZlibHelpers.GzipCompress);

    private static RuntimeValue GunzipAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RunAsync(interpreter, args, "gunzip", ZlibHelpers.GzipDecompress);

    private static RuntimeValue DeflateAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RunAsync(interpreter, args, "deflate", ZlibHelpers.DeflateCompress);

    private static RuntimeValue InflateAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RunAsync(interpreter, args, "inflate", ZlibHelpers.DeflateDecompress);

    private static RuntimeValue DeflateRawAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RunAsync(interpreter, args, "deflateRaw", ZlibHelpers.DeflateRawCompress);

    private static RuntimeValue InflateRawAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RunAsync(interpreter, args, "inflateRaw", ZlibHelpers.DeflateRawDecompress);

    private static RuntimeValue BrotliCompressAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RunAsync(interpreter, args, "brotliCompress", ZlibHelpers.BrotliCompress);

    private static RuntimeValue BrotliDecompressAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RunAsync(interpreter, args, "brotliDecompress", ZlibHelpers.BrotliDecompress);

    private static RuntimeValue ZstdCompressAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RunAsync(interpreter, args, "zstdCompress", ZlibHelpers.ZstdCompress);

    private static RuntimeValue ZstdDecompressAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
        => RunAsync(interpreter, args, "zstdDecompress", ZlibHelpers.ZstdDecompress);

    private static RuntimeValue UnzipAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var input = GetInputBytes(args, 0, "unzip");
        var callback = ExtractCallback(args);
        var options = args.Length >= 3 ? GetOptions(args, 1) : new ZlibOptions();

        interpreter.ScheduleTimer(0, 0, () =>
        {
            try
            {
                // Auto-detect format
                byte[] result;
                if (input.Length >= 2 && input[0] == 0x1f && input[1] == 0x8b)
                    result = ZlibHelpers.GzipDecompress(input, options);
                else if (input.Length >= 1 && input[0] == 0x78)
                    result = ZlibHelpers.DeflateDecompress(input, options);
                else
                    result = ZlibHelpers.DeflateRawDecompress(input, options);

                callback?.Call(interpreter, [null, new SharpTSBuffer(result)]);
            }
            catch (Exception ex)
            {
                callback?.Call(interpreter, [ex.Message]);
            }
        }, isInterval: false);

        return RuntimeValue.Null;
    }

    private static RuntimeValue RunAsync(Interp interpreter, ReadOnlySpan<RuntimeValue> args, string methodName,
        Func<byte[], ZlibOptions, byte[]> operation)
    {
        var input = GetInputBytes(args, 0, methodName);
        var callback = ExtractCallback(args);
        var options = args.Length >= 3 ? GetOptions(args, 1) : new ZlibOptions();

        interpreter.ScheduleTimer(0, 0, () =>
        {
            try
            {
                var result = operation(input, options);
                callback?.Call(interpreter, [null, new SharpTSBuffer(result)]);
            }
            catch (Exception ex)
            {
                callback?.Call(interpreter, [ex.Message]);
            }
        }, isInterval: false);

        return RuntimeValue.Null;
    }

    private static ISharpTSCallable? ExtractCallback(ReadOnlySpan<RuntimeValue> args)
    {
        // Callback is the last argument
        for (int i = args.Length - 1; i >= 0; i--)
        {
            if (args[i].ToObject() is ISharpTSCallable cb)
                return cb;
        }
        return null;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Extracts input bytes from argument (Buffer or string).
    /// </summary>
    private static byte[] GetInputBytes(ReadOnlySpan<RuntimeValue> args, int index, string methodName)
    {
        if (args.Length <= index || args[index].IsNull)
            throw new Exception($"{methodName} requires a Buffer or string argument");

        return args[index].ToObject() switch
        {
            SharpTSBuffer buffer => buffer.Data,
            string str => System.Text.Encoding.UTF8.GetBytes(str),
            SharpTSArray array => ArrayToBytes(array),
            _ => throw new Exception($"{methodName} requires a Buffer or string argument")
        };
    }

    /// <summary>
    /// Converts a SharpTSArray to byte array.
    /// </summary>
    private static byte[] ArrayToBytes(SharpTSArray array)
    {
        var bytes = new byte[array.Length];
        for (int i = 0; i < array.Length; i++)
        {
            bytes[i] = array[i] switch
            {
                double d => (byte)((int)d & 0xFF),
                int n => (byte)(n & 0xFF),
                _ => 0
            };
        }
        return bytes;
    }

    /// <summary>
    /// Extracts options object from arguments.
    /// </summary>
    private static ZlibOptions GetOptions(ReadOnlySpan<RuntimeValue> args, int index)
    {
        if (args.Length <= index || args[index].IsNull)
            return new ZlibOptions();

        return ZlibOptions.FromValue(args[index].ToObject());
    }

    #endregion
}
