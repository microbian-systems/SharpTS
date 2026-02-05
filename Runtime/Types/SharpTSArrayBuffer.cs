using SharpTS.TypeSystem;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of JavaScript's ArrayBuffer.
/// </summary>
/// <remarks>
/// Provides fixed-length raw binary data buffer for single-threaded use.
/// Unlike SharedArrayBuffer, ArrayBuffer is not shared across threads and
/// does not require pinned memory or IDisposable.
/// </remarks>
public class SharpTSArrayBuffer : ITypeCategorized
{
    private readonly byte[] _data;

    /// <inheritdoc />
    public TypeCategory RuntimeCategory => TypeCategory.Buffer;

    /// <summary>
    /// Gets the size of the buffer in bytes.
    /// </summary>
    public int ByteLength { get; }

    /// <summary>
    /// Creates a new ArrayBuffer with the specified byte length.
    /// </summary>
    /// <param name="byteLength">The size of the buffer in bytes.</param>
    public SharpTSArrayBuffer(int byteLength)
    {
        if (byteLength < 0)
        {
            throw new Exception("RangeError: Invalid ArrayBuffer length");
        }

        ByteLength = byteLength;
        _data = new byte[byteLength];
    }

    /// <summary>
    /// Gets a span over the entire buffer for direct memory access.
    /// </summary>
    public Span<byte> AsSpan() => _data.AsSpan();

    /// <summary>
    /// Gets a span over a portion of the buffer.
    /// </summary>
    /// <param name="start">The starting byte offset.</param>
    /// <param name="length">The number of bytes.</param>
    public Span<byte> AsSpan(int start, int length) => _data.AsSpan(start, length);

    /// <summary>
    /// Gets the underlying byte array for direct access.
    /// Use with caution - prefer AsSpan() for bounds-checked access.
    /// </summary>
    internal byte[] GetBackingArray() => _data;

    /// <summary>
    /// Creates a new ArrayBuffer containing a copy of a portion of this buffer.
    /// </summary>
    /// <param name="begin">The beginning index (inclusive). Negative values count from end.</param>
    /// <param name="end">The ending index (exclusive). Negative values count from end. Defaults to ByteLength.</param>
    /// <returns>A new ArrayBuffer containing the copied bytes.</returns>
    public SharpTSArrayBuffer Slice(int begin, int? end = null)
    {
        // Handle negative indices
        int actualBegin = begin < 0 ? Math.Max(ByteLength + begin, 0) : Math.Min(begin, ByteLength);
        int actualEnd = end.HasValue
            ? (end.Value < 0 ? Math.Max(ByteLength + end.Value, 0) : Math.Min(end.Value, ByteLength))
            : ByteLength;

        int length = Math.Max(actualEnd - actualBegin, 0);

        var result = new SharpTSArrayBuffer(length);
        if (length > 0)
        {
            Array.Copy(_data, actualBegin, result._data, 0, length);
        }

        return result;
    }

    /// <summary>
    /// Gets or sets a byte at the specified index.
    /// </summary>
    public byte this[int index]
    {
        get
        {
            if (index < 0 || index >= ByteLength)
            {
                throw new Exception("RangeError: Index out of bounds");
            }
            return _data[index];
        }
        set
        {
            if (index < 0 || index >= ByteLength)
            {
                throw new Exception("RangeError: Index out of bounds");
            }
            _data[index] = value;
        }
    }

    /// <summary>
    /// Checks if the argument is a TypedArray or DataView (a view over an ArrayBuffer).
    /// </summary>
    /// <param name="arg">The value to check.</param>
    /// <returns>True if arg is a TypedArray instance.</returns>
    public static bool IsView(object? arg)
    {
        // TypedArray instances are views
        if (arg is SharpTSTypedArray)
            return true;

        // DataView would also be a view, but it's not implemented yet
        // When DataView is added, check for it here

        return false;
    }

    public override string ToString() => $"ArrayBuffer {{ byteLength: {ByteLength} }}";
}
