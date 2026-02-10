using System.Text;
using SharpTS.Compilation.Bundling.Canonical;
using Xunit;

namespace SharpTS.Tests.Compilation;

/// <summary>
/// Unit tests for canonical manifest serialization and bundle ID generation.
/// </summary>
public class CanonicalManifestTests
{
    [Fact]
    public void Manifest_RoundTrip_PreservesData()
    {
        var manifest = new CanonicalManifest("TestBundleId1", majorVersion: 6, minorVersion: 0);
        manifest.AddEntry(new CanonicalFileEntry("test.dll", 1024, CanonicalFileType.Assembly) { Offset = 4096 });
        manifest.AddEntry(new CanonicalFileEntry("test.runtimeconfig.json", 200, CanonicalFileType.RuntimeConfigJson) { Offset = 5120 });

        // Serialize
        using var writeStream = new MemoryStream();
        using (var writer = new BinaryWriter(writeStream, Encoding.UTF8, leaveOpen: true))
        {
            manifest.Write(writer);
        }

        // Deserialize
        writeStream.Position = 0;
        using var reader = new BinaryReader(writeStream, Encoding.UTF8);
        var restored = CanonicalManifest.Read(reader);

        Assert.Equal(6u, restored.MajorVersion);
        Assert.Equal(0u, restored.MinorVersion);
        Assert.Equal("TestBundleId1", restored.BundleId);
        Assert.Equal(2, restored.Entries.Count);

        var dllEntry = restored.Entries[0];
        Assert.Equal("test.dll", dllEntry.RelativePath);
        Assert.Equal(1024, dllEntry.Size);
        Assert.Equal(CanonicalFileType.Assembly, dllEntry.Type);
        Assert.Equal(4096, dllEntry.Offset);

        var configEntry = restored.Entries[1];
        Assert.Equal("test.runtimeconfig.json", configEntry.RelativePath);
        Assert.Equal(200, configEntry.Size);
        Assert.Equal(CanonicalFileType.RuntimeConfigJson, configEntry.Type);
        Assert.Equal(5120, configEntry.Offset);
    }

    [Fact]
    public void Manifest_FindDepsJson_ReturnsCorrectEntry()
    {
        var manifest = new CanonicalManifest("test123bundl");
        manifest.AddEntry(new CanonicalFileEntry("app.dll", 100, CanonicalFileType.Assembly));
        manifest.AddEntry(new CanonicalFileEntry("app.deps.json", 50, CanonicalFileType.DepsJson));

        var deps = manifest.FindDepsJson();
        Assert.NotNull(deps);
        Assert.Equal("app.deps.json", deps.RelativePath);
    }

    [Fact]
    public void Manifest_FindRuntimeConfig_ReturnsCorrectEntry()
    {
        var manifest = new CanonicalManifest("test123bundl");
        manifest.AddEntry(new CanonicalFileEntry("app.dll", 100, CanonicalFileType.Assembly));
        manifest.AddEntry(new CanonicalFileEntry("app.runtimeconfig.json", 50, CanonicalFileType.RuntimeConfigJson));

        var config = manifest.FindRuntimeConfig();
        Assert.NotNull(config);
        Assert.Equal("app.runtimeconfig.json", config.RelativePath);
    }

    [Fact]
    public void Manifest_FindDepsJson_ReturnsNull_WhenNotPresent()
    {
        var manifest = new CanonicalManifest("test123bundl");
        manifest.AddEntry(new CanonicalFileEntry("app.dll", 100, CanonicalFileType.Assembly));

        Assert.Null(manifest.FindDepsJson());
    }

    [Fact]
    public void GenerateBundleId_Is12Characters()
    {
        var id = CanonicalManifest.GenerateBundleId([1, 2, 3], [4, 5, 6]);
        Assert.Equal(12, id.Length);
    }

    [Fact]
    public void GenerateBundleId_IsDeterministic()
    {
        var data1 = Encoding.UTF8.GetBytes("hello world");
        var data2 = Encoding.UTF8.GetBytes("runtime config");

        var id1 = CanonicalManifest.GenerateBundleId(data1, data2);
        var id2 = CanonicalManifest.GenerateBundleId(data1, data2);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void GenerateBundleId_DifferentInput_DifferentId()
    {
        var id1 = CanonicalManifest.GenerateBundleId([1, 2, 3], [4, 5, 6]);
        var id2 = CanonicalManifest.GenerateBundleId([7, 8, 9], [10, 11, 12]);

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void GenerateBundleId_IsUrlSafe()
    {
        var id = CanonicalManifest.GenerateBundleId(
            Encoding.UTF8.GetBytes("test assembly content"),
            Encoding.UTF8.GetBytes("test config content"));

        // Should not contain URL-unsafe characters
        Assert.DoesNotContain("+", id);
        Assert.DoesNotContain("/", id);
        Assert.DoesNotContain("=", id);
    }

    [Fact]
    public void Manifest_Flags_DefaultsToZero()
    {
        var manifest = new CanonicalManifest("test123bundl");
        Assert.Equal(0UL, manifest.Flags);
    }

    [Fact]
    public void Manifest_SerializedHeaderOffsets_MatchEntryData()
    {
        var manifest = new CanonicalManifest("test123bundl");
        var configEntry = new CanonicalFileEntry("app.runtimeconfig.json", 200, CanonicalFileType.RuntimeConfigJson)
        {
            Offset = 8192
        };
        manifest.AddEntry(configEntry);

        // Serialize
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        manifest.Write(writer);

        // Read back the header fields manually
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        var majorVersion = reader.ReadUInt32();
        var minorVersion = reader.ReadUInt32();
        var fileCount = reader.ReadInt32();
        var bundleId = reader.ReadString();

        // deps.json offset/size (should be 0/0 since no deps entry)
        var depsOffset = reader.ReadInt64();
        var depsSize = reader.ReadInt64();
        Assert.Equal(0, depsOffset);
        Assert.Equal(0, depsSize);

        // runtimeconfig.json offset/size (should match entry)
        var configOffset = reader.ReadInt64();
        var configSize = reader.ReadInt64();
        Assert.Equal(8192, configOffset);
        Assert.Equal(200, configSize);
    }
}
