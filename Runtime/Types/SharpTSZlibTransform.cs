using System.IO.Compression;
using SharpTS.Runtime.BuiltIns;
using SharpTS.Runtime.BuiltIns.Modules;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Zlib compression/decompression transform type.
/// </summary>
public enum ZlibTransformKind
{
    Gzip,
    Gunzip,
    Deflate,
    Inflate,
    DeflateRaw,
    InflateRaw,
    BrotliCompress,
    BrotliDecompress,
    Unzip,
    ZstdCompress,
    ZstdDecompress
}

/// <summary>
/// Runtime representation of a zlib streaming Transform (e.g., createGzip(), createGunzip()).
/// Extends SharpTSTransform to provide compression/decompression as a Transform stream.
/// </summary>
/// <remarks>
/// Accumulates input chunks, then compresses/decompresses on flush.
/// For compression, true streaming is used (output produced per chunk).
/// </remarks>
public class SharpTSZlibTransform : SharpTSTransform
{
    private readonly ZlibTransformKind _kind;
    private readonly ZlibOptions _options;

    // For compression: streaming output
    private MemoryStream? _outputBuffer;
    private Stream? _compressionStream;

    // For decompression: accumulate then decompress
    private MemoryStream? _inputBuffer;

    public SharpTSZlibTransform(ZlibTransformKind kind, ZlibOptions options)
    {
        _kind = kind;
        _options = options;

        if (IsCompression)
        {
            _outputBuffer = new MemoryStream();
            _compressionStream = CreateCompressionStream(_outputBuffer);
        }
        else
        {
            _inputBuffer = new MemoryStream();
        }

        // Set our internal transform and flush callbacks
        SetTransformCallback(new ZlibTransformCallback(this));
        SetFlushCallback(new ZlibFlushCallback(this));
    }

    private bool IsCompression => _kind is ZlibTransformKind.Gzip
        or ZlibTransformKind.Deflate
        or ZlibTransformKind.DeflateRaw
        or ZlibTransformKind.BrotliCompress
        or ZlibTransformKind.ZstdCompress;

    private Stream CreateCompressionStream(MemoryStream output)
    {
        var level = _options.ToCompressionLevel();
        return _kind switch
        {
            ZlibTransformKind.Gzip => new GZipStream(output, level, leaveOpen: true),
            ZlibTransformKind.Deflate => new ZLibStream(output, level, leaveOpen: true),
            ZlibTransformKind.DeflateRaw => new DeflateStream(output, level, leaveOpen: true),
            ZlibTransformKind.BrotliCompress => new BrotliStream(output, level, leaveOpen: true),
            ZlibTransformKind.ZstdCompress => new ZstdSharp.CompressionStream(output, _options.GetZstdLevel(), 0, leaveOpen: true),
            _ => throw new InvalidOperationException($"Not a compression kind: {_kind}")
        };
    }

    private void TransformChunk(Interp interpreter, object? chunk)
    {
        var bytes = ChunkToBytes(chunk);
        if (bytes.Length == 0) return;

        if (IsCompression)
        {
            // Write to compression stream, flush, and push whatever comes out
            _compressionStream!.Write(bytes, 0, bytes.Length);
            _compressionStream.Flush();
            PushOutputBuffer(interpreter);
        }
        else
        {
            // Accumulate for decompression
            _inputBuffer!.Write(bytes, 0, bytes.Length);
        }
    }

    private void Flush(Interp interpreter)
    {
        if (IsCompression)
        {
            // Dispose compression stream to write final data
            _compressionStream!.Dispose();
            _compressionStream = null;
            PushOutputBuffer(interpreter);
        }
        else
        {
            // Decompress accumulated data
            var inputBytes = _inputBuffer!.ToArray();
            _inputBuffer.Dispose();
            _inputBuffer = null;

            if (inputBytes.Length > 0)
            {
                var result = Decompress(inputBytes);
                PushToReadableSide(interpreter, new SharpTSBuffer(result));
            }
        }
    }

    private void PushOutputBuffer(Interp interpreter)
    {
        if (_outputBuffer == null || _outputBuffer.Position == 0) return;

        var data = _outputBuffer.ToArray();
        // Reset the buffer for next chunk
        _outputBuffer.SetLength(0);

        if (data.Length > 0)
        {
            PushToReadableSide(interpreter, new SharpTSBuffer(data));
        }
    }

    private byte[] Decompress(byte[] input)
    {
        var actualKind = _kind;
        if (actualKind == ZlibTransformKind.Unzip)
        {
            actualKind = DetectFormat(input);
        }

        return actualKind switch
        {
            ZlibTransformKind.Gunzip => ZlibHelpers.GzipDecompress(input, _options),
            ZlibTransformKind.Inflate => ZlibHelpers.DeflateDecompress(input, _options),
            ZlibTransformKind.InflateRaw => ZlibHelpers.DeflateRawDecompress(input, _options),
            ZlibTransformKind.BrotliDecompress => ZlibHelpers.BrotliDecompress(input, _options),
            ZlibTransformKind.ZstdDecompress => ZlibHelpers.ZstdDecompress(input, _options),
            ZlibTransformKind.Unzip => ZlibHelpers.DeflateRawDecompress(input, _options),
            _ => throw new InvalidOperationException($"Not a decompression kind: {_kind}")
        };
    }

    private static ZlibTransformKind DetectFormat(byte[] input)
    {
        if (input.Length >= 2)
        {
            if (input[0] == 0x1f && input[1] == 0x8b)
                return ZlibTransformKind.Gunzip;
            if (input[0] == 0x78)
                return ZlibTransformKind.Inflate;
        }
        return ZlibTransformKind.InflateRaw;
    }

    private static byte[] ChunkToBytes(object? chunk)
    {
        return chunk switch
        {
            SharpTSBuffer buffer => buffer.Data,
            string str => System.Text.Encoding.UTF8.GetBytes(str),
            _ => []
        };
    }

    public override string ToString() => _kind switch
    {
        ZlibTransformKind.Gzip => "Gzip {}",
        ZlibTransformKind.Gunzip => "Gunzip {}",
        ZlibTransformKind.Deflate => "Deflate {}",
        ZlibTransformKind.Inflate => "Inflate {}",
        ZlibTransformKind.DeflateRaw => "DeflateRaw {}",
        ZlibTransformKind.InflateRaw => "InflateRaw {}",
        ZlibTransformKind.BrotliCompress => "BrotliCompress {}",
        ZlibTransformKind.BrotliDecompress => "BrotliDecompress {}",
        ZlibTransformKind.Unzip => "Unzip {}",
        ZlibTransformKind.ZstdCompress => "ZstdCompress {}",
        ZlibTransformKind.ZstdDecompress => "ZstdDecompress {}",
        _ => "ZlibTransform {}"
    };

    private class ZlibTransformCallback : ISharpTSCallable
    {
        private readonly SharpTSZlibTransform _stream;
        public ZlibTransformCallback(SharpTSZlibTransform stream) => _stream = stream;
        public int Arity() => 3;

        public object? Call(Interp interpreter, List<object?> arguments)
        {
            var chunk = arguments.Count > 0 ? arguments[0] : null;
            var callback = arguments.Count > 2 ? arguments[2] as ISharpTSCallable : null;

            try
            {
                _stream.TransformChunk(interpreter, chunk);
                callback?.CallBoxed(interpreter, [null]);
            }
            catch (Exception ex)
            {
                callback?.CallBoxed(interpreter, [ex.Message]);
            }

            return null;
        }

        public RuntimeValue Call(Interp interpreter, ReadOnlySpan<RuntimeValue> arguments)
            => RuntimeValue.FromBoxed(Call(interpreter, CallableInterop.ToBoxedList(arguments)));
    }

    private class ZlibFlushCallback : ISharpTSCallable
    {
        private readonly SharpTSZlibTransform _stream;
        public ZlibFlushCallback(SharpTSZlibTransform stream) => _stream = stream;
        public int Arity() => 1;

        public object? Call(Interp interpreter, List<object?> arguments)
        {
            var callback = arguments.Count > 0 ? arguments[0] as ISharpTSCallable : null;

            try
            {
                _stream.Flush(interpreter);
                callback?.CallBoxed(interpreter, [null]);
            }
            catch (Exception ex)
            {
                callback?.CallBoxed(interpreter, [ex.Message]);
            }

            return null;
        }

        public RuntimeValue Call(Interp interpreter, ReadOnlySpan<RuntimeValue> arguments)
            => RuntimeValue.FromBoxed(Call(interpreter, CallableInterop.ToBoxedList(arguments)));
    }
}
