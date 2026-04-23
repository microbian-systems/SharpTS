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
            ["gzipSync"] = new BuiltInMethod("gzipSync", 1, 2, GzipSync),
            ["gunzipSync"] = new BuiltInMethod("gunzipSync", 1, 2, GunzipSync),

            // Deflate (with zlib header)
            ["deflateSync"] = new BuiltInMethod("deflateSync", 1, 2, DeflateSync),
            ["inflateSync"] = new BuiltInMethod("inflateSync", 1, 2, InflateSync),

            // DeflateRaw (no header)
            ["deflateRawSync"] = new BuiltInMethod("deflateRawSync", 1, 2, DeflateRawSync),
            ["inflateRawSync"] = new BuiltInMethod("inflateRawSync", 1, 2, InflateRawSync),

            // Brotli
            ["brotliCompressSync"] = new BuiltInMethod("brotliCompressSync", 1, 2, BrotliCompressSync),
            ["brotliDecompressSync"] = new BuiltInMethod("brotliDecompressSync", 1, 2, BrotliDecompressSync),

            // Zstd
            ["zstdCompressSync"] = new BuiltInMethod("zstdCompressSync", 1, 2, ZstdCompressSync),
            ["zstdDecompressSync"] = new BuiltInMethod("zstdDecompressSync", 1, 2, ZstdDecompressSync),

            // Unzip (auto-detect gzip/deflate)
            ["unzipSync"] = new BuiltInMethod("unzipSync", 1, 2, UnzipSync),

            // Streaming APIs (Transform streams)
            ["createGzip"] = new BuiltInMethod("createGzip", 0, 1, CreateGzip),
            ["createGunzip"] = new BuiltInMethod("createGunzip", 0, 1, CreateGunzip),
            ["createDeflate"] = new BuiltInMethod("createDeflate", 0, 1, CreateDeflate),
            ["createInflate"] = new BuiltInMethod("createInflate", 0, 1, CreateInflate),
            ["createDeflateRaw"] = new BuiltInMethod("createDeflateRaw", 0, 1, CreateDeflateRaw),
            ["createInflateRaw"] = new BuiltInMethod("createInflateRaw", 0, 1, CreateInflateRaw),
            ["createBrotliCompress"] = new BuiltInMethod("createBrotliCompress", 0, 1, CreateBrotliCompress),
            ["createBrotliDecompress"] = new BuiltInMethod("createBrotliDecompress", 0, 1, CreateBrotliDecompress),
            ["createZstdCompress"] = new BuiltInMethod("createZstdCompress", 0, 1, CreateZstdCompress),
            ["createZstdDecompress"] = new BuiltInMethod("createZstdDecompress", 0, 1, CreateZstdDecompress),
            ["createUnzip"] = new BuiltInMethod("createUnzip", 0, 1, CreateUnzip),

            // Async callback APIs
            ["gzip"] = new BuiltInMethod("gzip", 2, 3, GzipAsync),
            ["gunzip"] = new BuiltInMethod("gunzip", 2, 3, GunzipAsync),
            ["deflate"] = new BuiltInMethod("deflate", 2, 3, DeflateAsync),
            ["inflate"] = new BuiltInMethod("inflate", 2, 3, InflateAsync),
            ["deflateRaw"] = new BuiltInMethod("deflateRaw", 2, 3, DeflateRawAsync),
            ["inflateRaw"] = new BuiltInMethod("inflateRaw", 2, 3, InflateRawAsync),
            ["brotliCompress"] = new BuiltInMethod("brotliCompress", 2, 3, BrotliCompressAsync),
            ["brotliDecompress"] = new BuiltInMethod("brotliDecompress", 2, 3, BrotliDecompressAsync),
            ["zstdCompress"] = new BuiltInMethod("zstdCompress", 2, 3, ZstdCompressAsync),
            ["zstdDecompress"] = new BuiltInMethod("zstdDecompress", 2, 3, ZstdDecompressAsync),
            ["unzip"] = new BuiltInMethod("unzip", 2, 3, UnzipAsync),

            // Constants
            ["constants"] = ZlibConstants.CreateConstantsObject()
        };
    }

    #region Gzip

    private static object? GzipSync(Interp interpreter, object? receiver, List<object?> args)
    {
        var input = GetInputBytes(args, 0, "gzipSync");
        var options = GetOptions(args, 1);

        var result = ZlibHelpers.GzipCompress(input, options);
        return new SharpTSBuffer(result);
    }

    private static object? GunzipSync(Interp interpreter, object? receiver, List<object?> args)
    {
        var input = GetInputBytes(args, 0, "gunzipSync");
        var options = GetOptions(args, 1);

        try
        {
            var result = ZlibHelpers.GzipDecompress(input, options);
            return new SharpTSBuffer(result);
        }
        catch (InvalidDataException)
        {
            throw new Exception("Error: incorrect header check");
        }
    }

    #endregion

    #region Deflate

    private static object? DeflateSync(Interp interpreter, object? receiver, List<object?> args)
    {
        var input = GetInputBytes(args, 0, "deflateSync");
        var options = GetOptions(args, 1);

        var result = ZlibHelpers.DeflateCompress(input, options);
        return new SharpTSBuffer(result);
    }

    private static object? InflateSync(Interp interpreter, object? receiver, List<object?> args)
    {
        var input = GetInputBytes(args, 0, "inflateSync");
        var options = GetOptions(args, 1);

        try
        {
            var result = ZlibHelpers.DeflateDecompress(input, options);
            return new SharpTSBuffer(result);
        }
        catch (InvalidDataException)
        {
            throw new Exception("Error: incorrect header check");
        }
    }

    #endregion

    #region DeflateRaw

    private static object? DeflateRawSync(Interp interpreter, object? receiver, List<object?> args)
    {
        var input = GetInputBytes(args, 0, "deflateRawSync");
        var options = GetOptions(args, 1);

        var result = ZlibHelpers.DeflateRawCompress(input, options);
        return new SharpTSBuffer(result);
    }

    private static object? InflateRawSync(Interp interpreter, object? receiver, List<object?> args)
    {
        var input = GetInputBytes(args, 0, "inflateRawSync");
        var options = GetOptions(args, 1);

        try
        {
            var result = ZlibHelpers.DeflateRawDecompress(input, options);
            return new SharpTSBuffer(result);
        }
        catch (InvalidDataException)
        {
            throw new Exception("Error: incorrect header check");
        }
    }

    #endregion

    #region Brotli

    private static object? BrotliCompressSync(Interp interpreter, object? receiver, List<object?> args)
    {
        var input = GetInputBytes(args, 0, "brotliCompressSync");
        var options = GetOptions(args, 1);

        var result = ZlibHelpers.BrotliCompress(input, options);
        return new SharpTSBuffer(result);
    }

    private static object? BrotliDecompressSync(Interp interpreter, object? receiver, List<object?> args)
    {
        var input = GetInputBytes(args, 0, "brotliDecompressSync");
        var options = GetOptions(args, 1);

        try
        {
            var result = ZlibHelpers.BrotliDecompress(input, options);
            return new SharpTSBuffer(result);
        }
        catch (InvalidDataException)
        {
            throw new Exception("Error: Decompression failed");
        }
    }

    #endregion

    #region Zstd

    private static object? ZstdCompressSync(Interp interpreter, object? receiver, List<object?> args)
    {
        var input = GetInputBytes(args, 0, "zstdCompressSync");
        var options = GetOptions(args, 1);

        var result = ZlibHelpers.ZstdCompress(input, options);
        return new SharpTSBuffer(result);
    }

    private static object? ZstdDecompressSync(Interp interpreter, object? receiver, List<object?> args)
    {
        var input = GetInputBytes(args, 0, "zstdDecompressSync");
        var options = GetOptions(args, 1);

        try
        {
            var result = ZlibHelpers.ZstdDecompress(input, options);
            return new SharpTSBuffer(result);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new Exception($"Error: Zstd decompression failed: {ex.Message}");
        }
    }

    #endregion

    #region Unzip (Auto-detect)

    private static object? UnzipSync(Interp interpreter, object? receiver, List<object?> args)
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
                return new SharpTSBuffer(result);
            }

            // Zlib header: first byte typically 0x78 (deflate)
            // 0x78 0x01 = no compression
            // 0x78 0x5e = fast compression
            // 0x78 0x9c = default compression
            // 0x78 0xda = best compression
            if (input[0] == 0x78)
            {
                var result = ZlibHelpers.DeflateDecompress(input, options);
                return new SharpTSBuffer(result);
            }
        }

        // Fallback: try raw deflate
        try
        {
            var result = ZlibHelpers.DeflateRawDecompress(input, options);
            return new SharpTSBuffer(result);
        }
        catch
        {
            throw new Exception("Error: unknown compression format");
        }
    }

    #endregion

    #region Streaming APIs

    private static object? CreateGzip(Interp interpreter, object? receiver, List<object?> args)
        => new SharpTSZlibTransform(ZlibTransformKind.Gzip, GetOptions(args, 0));

    private static object? CreateGunzip(Interp interpreter, object? receiver, List<object?> args)
        => new SharpTSZlibTransform(ZlibTransformKind.Gunzip, GetOptions(args, 0));

    private static object? CreateDeflate(Interp interpreter, object? receiver, List<object?> args)
        => new SharpTSZlibTransform(ZlibTransformKind.Deflate, GetOptions(args, 0));

    private static object? CreateInflate(Interp interpreter, object? receiver, List<object?> args)
        => new SharpTSZlibTransform(ZlibTransformKind.Inflate, GetOptions(args, 0));

    private static object? CreateDeflateRaw(Interp interpreter, object? receiver, List<object?> args)
        => new SharpTSZlibTransform(ZlibTransformKind.DeflateRaw, GetOptions(args, 0));

    private static object? CreateInflateRaw(Interp interpreter, object? receiver, List<object?> args)
        => new SharpTSZlibTransform(ZlibTransformKind.InflateRaw, GetOptions(args, 0));

    private static object? CreateBrotliCompress(Interp interpreter, object? receiver, List<object?> args)
        => new SharpTSZlibTransform(ZlibTransformKind.BrotliCompress, GetOptions(args, 0));

    private static object? CreateBrotliDecompress(Interp interpreter, object? receiver, List<object?> args)
        => new SharpTSZlibTransform(ZlibTransformKind.BrotliDecompress, GetOptions(args, 0));

    private static object? CreateZstdCompress(Interp interpreter, object? receiver, List<object?> args)
        => new SharpTSZlibTransform(ZlibTransformKind.ZstdCompress, GetOptions(args, 0));

    private static object? CreateZstdDecompress(Interp interpreter, object? receiver, List<object?> args)
        => new SharpTSZlibTransform(ZlibTransformKind.ZstdDecompress, GetOptions(args, 0));

    private static object? CreateUnzip(Interp interpreter, object? receiver, List<object?> args)
        => new SharpTSZlibTransform(ZlibTransformKind.Unzip, GetOptions(args, 0));

    #endregion

    #region Async Callback APIs

    private static object? GzipAsync(Interp interpreter, object? receiver, List<object?> args)
        => RunAsync(interpreter, args, "gzip", ZlibHelpers.GzipCompress);

    private static object? GunzipAsync(Interp interpreter, object? receiver, List<object?> args)
        => RunAsync(interpreter, args, "gunzip", ZlibHelpers.GzipDecompress);

    private static object? DeflateAsync(Interp interpreter, object? receiver, List<object?> args)
        => RunAsync(interpreter, args, "deflate", ZlibHelpers.DeflateCompress);

    private static object? InflateAsync(Interp interpreter, object? receiver, List<object?> args)
        => RunAsync(interpreter, args, "inflate", ZlibHelpers.DeflateDecompress);

    private static object? DeflateRawAsync(Interp interpreter, object? receiver, List<object?> args)
        => RunAsync(interpreter, args, "deflateRaw", ZlibHelpers.DeflateRawCompress);

    private static object? InflateRawAsync(Interp interpreter, object? receiver, List<object?> args)
        => RunAsync(interpreter, args, "inflateRaw", ZlibHelpers.DeflateRawDecompress);

    private static object? BrotliCompressAsync(Interp interpreter, object? receiver, List<object?> args)
        => RunAsync(interpreter, args, "brotliCompress", ZlibHelpers.BrotliCompress);

    private static object? BrotliDecompressAsync(Interp interpreter, object? receiver, List<object?> args)
        => RunAsync(interpreter, args, "brotliDecompress", ZlibHelpers.BrotliDecompress);

    private static object? ZstdCompressAsync(Interp interpreter, object? receiver, List<object?> args)
        => RunAsync(interpreter, args, "zstdCompress", ZlibHelpers.ZstdCompress);

    private static object? ZstdDecompressAsync(Interp interpreter, object? receiver, List<object?> args)
        => RunAsync(interpreter, args, "zstdDecompress", ZlibHelpers.ZstdDecompress);

    private static object? UnzipAsync(Interp interpreter, object? receiver, List<object?> args)
    {
        var input = GetInputBytes(args, 0, "unzip");
        var callback = ExtractCallback(args);
        var options = args.Count >= 3 ? GetOptions(args, 1) : new ZlibOptions();

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

        return null;
    }

    private static object? RunAsync(Interp interpreter, List<object?> args, string methodName,
        Func<byte[], ZlibOptions, byte[]> operation)
    {
        var input = GetInputBytes(args, 0, methodName);
        var callback = ExtractCallback(args);
        var options = args.Count >= 3 ? GetOptions(args, 1) : new ZlibOptions();

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

        return null;
    }

    private static ISharpTSCallable? ExtractCallback(List<object?> args)
    {
        // Callback is the last argument
        for (int i = args.Count - 1; i >= 0; i--)
        {
            if (args[i] is ISharpTSCallable cb)
                return cb;
        }
        return null;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Extracts input bytes from argument (Buffer or string).
    /// </summary>
    private static byte[] GetInputBytes(List<object?> args, int index, string methodName)
    {
        if (args.Count <= index || args[index] == null)
            throw new Exception($"{methodName} requires a Buffer or string argument");

        return args[index] switch
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
    private static ZlibOptions GetOptions(List<object?> args, int index)
    {
        if (args.Count <= index || args[index] == null)
            return new ZlibOptions();

        return ZlibOptions.FromValue(args[index]);
    }

    #endregion
}
