using SharpTS.Compilation.Bundling.Canonical;
using Xunit;

namespace SharpTS.Tests.Compilation;

/// <summary>
/// Unit tests for canonical file type classification.
/// </summary>
public class CanonicalFileClassifierTests
{
    [Theory]
    [InlineData("app.dll", CanonicalFileType.Assembly)]
    [InlineData("MyLib.dll", CanonicalFileType.Assembly)]
    [InlineData("app.pdb", CanonicalFileType.Symbols)]
    [InlineData("app.deps.json", CanonicalFileType.DepsJson)]
    [InlineData("app.runtimeconfig.json", CanonicalFileType.RuntimeConfigJson)]
    [InlineData("libhostpolicy.so", CanonicalFileType.NativeBinary)]
    [InlineData("libhostfxr.dylib", CanonicalFileType.NativeBinary)]
    [InlineData("README.txt", CanonicalFileType.Unknown)]
    [InlineData("data.bin", CanonicalFileType.Unknown)]
    public void Classify_ReturnsCorrectType(string fileName, CanonicalFileType expectedType)
    {
        Assert.Equal(expectedType, CanonicalFileClassifier.Classify(fileName));
    }

    [Theory]
    [InlineData("subdir/app.dll", CanonicalFileType.Assembly)]
    [InlineData("config/app.runtimeconfig.json", CanonicalFileType.RuntimeConfigJson)]
    public void Classify_HandlesRelativePaths(string relativePath, CanonicalFileType expectedType)
    {
        Assert.Equal(expectedType, CanonicalFileClassifier.Classify(relativePath));
    }

    [Fact]
    public void RequiresAlignment_TrueForAssembly()
    {
        Assert.True(CanonicalFileClassifier.RequiresAlignment(CanonicalFileType.Assembly));
    }

    [Theory]
    [InlineData(CanonicalFileType.DepsJson)]
    [InlineData(CanonicalFileType.RuntimeConfigJson)]
    [InlineData(CanonicalFileType.NativeBinary)]
    [InlineData(CanonicalFileType.Symbols)]
    [InlineData(CanonicalFileType.Unknown)]
    public void RequiresAlignment_FalseForNonAssembly(CanonicalFileType type)
    {
        Assert.False(CanonicalFileClassifier.RequiresAlignment(type));
    }

    [Fact]
    public void IsCompressionExcluded_TrueForDepsJson()
    {
        Assert.True(CanonicalFileClassifier.IsCompressionExcluded(CanonicalFileType.DepsJson));
    }

    [Fact]
    public void IsCompressionExcluded_TrueForRuntimeConfig()
    {
        Assert.True(CanonicalFileClassifier.IsCompressionExcluded(CanonicalFileType.RuntimeConfigJson));
    }

    [Theory]
    [InlineData(CanonicalFileType.Assembly)]
    [InlineData(CanonicalFileType.NativeBinary)]
    [InlineData(CanonicalFileType.Symbols)]
    [InlineData(CanonicalFileType.Unknown)]
    public void IsCompressionExcluded_FalseForOtherTypes(CanonicalFileType type)
    {
        Assert.False(CanonicalFileClassifier.IsCompressionExcluded(type));
    }
}
