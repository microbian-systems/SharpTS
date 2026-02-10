using System.Text;

namespace SharpTS.Compilation.Bundling.Canonical;

/// <summary>
/// Represents a single file entry in the bundle manifest.
/// Serialization format matches Microsoft.NET.HostModel.Bundle.FileEntry.
/// </summary>
public sealed class CanonicalFileEntry
{
    /// <summary>
    /// Absolute offset of the file data within the bundle.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Uncompressed size of the file in bytes.
    /// </summary>
    public long Size { get; }

    /// <summary>
    /// Compressed size of the file in bytes. Zero means uncompressed.
    /// Present in bundle version 6+.
    /// </summary>
    public long CompressedSize { get; }

    /// <summary>
    /// Classification of the file.
    /// </summary>
    public CanonicalFileType Type { get; }

    /// <summary>
    /// Relative path of the file within the bundle (uses forward slashes).
    /// </summary>
    public string RelativePath { get; }

    public CanonicalFileEntry(string relativePath, long size, CanonicalFileType type, long compressedSize = 0)
    {
        RelativePath = relativePath.Replace('\\', '/');
        Size = size;
        Type = type;
        CompressedSize = compressedSize;
    }

    /// <summary>
    /// Writes this file entry to a BinaryWriter.
    /// Format: Offset(8) + Size(8) + CompressedSize(8) + Type(1) + Path(7-bit-length-prefixed UTF-8).
    /// </summary>
    public void Write(BinaryWriter writer)
    {
        writer.Write(Offset);
        writer.Write(Size);
        writer.Write(CompressedSize);
        writer.Write((byte)Type);
        writer.Write(RelativePath);
    }

    /// <summary>
    /// Reads a file entry from a BinaryReader.
    /// </summary>
    public static CanonicalFileEntry Read(BinaryReader reader)
    {
        var offset = reader.ReadInt64();
        var size = reader.ReadInt64();
        var compressedSize = reader.ReadInt64();
        var type = (CanonicalFileType)reader.ReadByte();
        var relativePath = reader.ReadString();

        return new CanonicalFileEntry(relativePath, size, type, compressedSize)
        {
            Offset = offset
        };
    }

    /// <summary>
    /// Calculates the serialized size of this entry in bytes.
    /// </summary>
    public int GetSerializedSize()
    {
        // Offset(8) + Size(8) + CompressedSize(8) + Type(1) + path length
        var pathBytes = Encoding.UTF8.GetByteCount(RelativePath);
        var lengthPrefixBytes = Get7BitEncodedIntSize(pathBytes);
        return 8 + 8 + 8 + 1 + lengthPrefixBytes + pathBytes;
    }

    private static int Get7BitEncodedIntSize(int value)
    {
        int size = 0;
        uint v = (uint)value;
        do
        {
            size++;
            v >>= 7;
        } while (v != 0);
        return size;
    }
}
