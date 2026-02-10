using System.Security.Cryptography;
using System.Text;

namespace SharpTS.Compilation.Bundling.Canonical;

/// <summary>
/// Represents the bundle manifest (header + file entries).
/// Serialization format matches Microsoft.NET.HostModel.Bundle.Manifest.
///
/// Binary layout:
///   uint   MajorVersion
///   uint   MinorVersion
///   int    FileCount
///   string BundleID          (7-bit-length-prefixed UTF-8)
///   long   DepsJsonOffset    (v2+)
///   long   DepsJsonSize      (v2+)
///   long   RuntimeConfigOffset (v2+)
///   long   RuntimeConfigSize   (v2+)
///   ulong  Flags             (v2+)
///   FileEntry[FileCount]     (sequential)
/// </summary>
public sealed class CanonicalManifest
{
    public uint MajorVersion { get; }
    public uint MinorVersion { get; }
    public string BundleId { get; }
    public ulong Flags { get; set; }

    private readonly List<CanonicalFileEntry> _entries = [];

    /// <summary>
    /// The file entries embedded in this manifest.
    /// </summary>
    public IReadOnlyList<CanonicalFileEntry> Entries => _entries;

    public CanonicalManifest(string? bundleId = null, uint majorVersion = CanonicalBundleFormat.BundleMajorVersion, uint minorVersion = CanonicalBundleFormat.BundleMinorVersion)
    {
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        BundleId = bundleId ?? GenerateBundleId();
    }

    /// <summary>
    /// Adds a file entry to the manifest.
    /// </summary>
    public void AddEntry(CanonicalFileEntry entry) => _entries.Add(entry);

    /// <summary>
    /// Finds the entry for deps.json, if any.
    /// </summary>
    public CanonicalFileEntry? FindDepsJson() =>
        _entries.FirstOrDefault(e => e.Type == CanonicalFileType.DepsJson);

    /// <summary>
    /// Finds the entry for runtimeconfig.json, if any.
    /// </summary>
    public CanonicalFileEntry? FindRuntimeConfig() =>
        _entries.FirstOrDefault(e => e.Type == CanonicalFileType.RuntimeConfigJson);

    /// <summary>
    /// Writes the manifest to a BinaryWriter.
    /// </summary>
    public void Write(BinaryWriter writer)
    {
        writer.Write(MajorVersion);
        writer.Write(MinorVersion);
        writer.Write(_entries.Count);
        writer.Write(BundleId);

        // Version 2+ fields
        var depsEntry = FindDepsJson();
        writer.Write(depsEntry?.Offset ?? 0L);
        writer.Write(depsEntry?.Size ?? 0L);

        var configEntry = FindRuntimeConfig();
        writer.Write(configEntry?.Offset ?? 0L);
        writer.Write(configEntry?.Size ?? 0L);

        writer.Write(Flags);

        // File entries
        foreach (var entry in _entries)
        {
            entry.Write(writer);
        }
    }

    /// <summary>
    /// Reads a manifest from a BinaryReader.
    /// </summary>
    public static CanonicalManifest Read(BinaryReader reader)
    {
        var majorVersion = reader.ReadUInt32();
        var minorVersion = reader.ReadUInt32();
        var fileCount = reader.ReadInt32();
        var bundleId = reader.ReadString();

        var manifest = new CanonicalManifest(bundleId, majorVersion, minorVersion);

        if (majorVersion >= 2)
        {
            // deps.json offset/size (informational - actual data in entries)
            reader.ReadInt64(); // depsJsonOffset
            reader.ReadInt64(); // depsJsonSize

            // runtimeconfig.json offset/size
            reader.ReadInt64(); // runtimeConfigOffset
            reader.ReadInt64(); // runtimeConfigSize

            manifest.Flags = reader.ReadUInt64();
        }

        for (int i = 0; i < fileCount; i++)
        {
            manifest.AddEntry(CanonicalFileEntry.Read(reader));
        }

        return manifest;
    }

    /// <summary>
    /// Generates a deterministic bundle ID from the file entries.
    /// Uses SHA-256 hashing of all file content hashes concatenated.
    /// Produces a 12-character Base64Url string compatible with path names.
    /// </summary>
    public static string GenerateBundleId(params ReadOnlySpan<byte[]> fileContents)
    {
        using var sha = SHA256.Create();

        foreach (var content in fileContents)
        {
            var hash = SHA256.HashData(content);
            sha.TransformBlock(hash, 0, hash.Length, hash, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        var base64 = Convert.ToBase64String(sha.Hash!);
        var urlSafe = base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return urlSafe[..CanonicalBundleFormat.BundleIdLength];
    }

    /// <summary>
    /// Generates a random bundle ID (used when no file contents are available yet).
    /// </summary>
    private static string GenerateBundleId()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        var base64 = Convert.ToBase64String(bytes);
        var urlSafe = base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return urlSafe[..CanonicalBundleFormat.BundleIdLength];
    }
}
