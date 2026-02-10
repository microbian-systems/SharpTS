using System.Runtime.InteropServices;
using System.Text;
using SharpTS.Compilation.Bundling.Canonical;
using Xunit;

namespace SharpTS.Tests.Compilation;

/// <summary>
/// Unit tests for canonical bundle format constants and utilities.
/// </summary>
public class CanonicalBundleFormatTests
{
    [Fact]
    public void BundleSignature_Is32Bytes()
    {
        Assert.Equal(32, CanonicalBundleFormat.BundleSignature.Length);
    }

    [Fact]
    public void BundleHeaderPlaceholder_Is40Bytes()
    {
        Assert.Equal(40, CanonicalBundleFormat.BundleHeaderPlaceholder.Length);
    }

    [Fact]
    public void BundleHeaderPlaceholder_StartsWithZeroOffset()
    {
        var placeholder = CanonicalBundleFormat.BundleHeaderPlaceholder;
        // First 8 bytes should be zeros (the offset slot)
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(0, placeholder[i]);
        }
    }

    [Fact]
    public void BundleHeaderPlaceholder_EndsWithSignature()
    {
        var placeholder = CanonicalBundleFormat.BundleHeaderPlaceholder;
        var signature = CanonicalBundleFormat.BundleSignature;
        Assert.True(placeholder[8..].SequenceEqual(signature));
    }

    [Fact]
    public void DllPathPlaceholder_Is64Bytes()
    {
        Assert.Equal(64, CanonicalBundleFormat.DllPathPlaceholder.Length);
    }

    [Fact]
    public void DllPathPlaceholder_IsKnownSha256()
    {
        var expected = Encoding.UTF8.GetBytes("c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2");
        Assert.True(CanonicalBundleFormat.DllPathPlaceholder.SequenceEqual(expected));
    }

    [Fact]
    public void FindSequence_FindsExactMatch()
    {
        byte[] haystack = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        byte[] needle = [4, 5, 6];
        Assert.Equal(3, CanonicalBundleFormat.FindSequence(haystack, needle));
    }

    [Fact]
    public void FindSequence_ReturnsMinusOne_WhenNotFound()
    {
        byte[] haystack = [1, 2, 3, 4, 5];
        byte[] needle = [6, 7];
        Assert.Equal(-1, CanonicalBundleFormat.FindSequence(haystack, needle));
    }

    [Fact]
    public void FindSequence_FindsAtStart()
    {
        byte[] haystack = [1, 2, 3, 4, 5];
        byte[] needle = [1, 2];
        Assert.Equal(0, CanonicalBundleFormat.FindSequence(haystack, needle));
    }

    [Fact]
    public void FindSequence_FindsAtEnd()
    {
        byte[] haystack = [1, 2, 3, 4, 5];
        byte[] needle = [4, 5];
        Assert.Equal(3, CanonicalBundleFormat.FindSequence(haystack, needle));
    }

    [Fact]
    public void FindSequence_EmptyNeedle_ReturnsMinusOne()
    {
        byte[] haystack = [1, 2, 3];
        Assert.Equal(-1, CanonicalBundleFormat.FindSequence(haystack, []));
    }

    [Fact]
    public void GetAssemblyAlignment_Windows_Returns4096()
    {
        Assert.Equal(4096, CanonicalBundleFormat.GetAssemblyAlignment(OSPlatform.Windows, Architecture.X64));
        Assert.Equal(4096, CanonicalBundleFormat.GetAssemblyAlignment(OSPlatform.Windows, Architecture.Arm64));
        Assert.Equal(4096, CanonicalBundleFormat.GetAssemblyAlignment(OSPlatform.Windows, Architecture.X86));
    }

    [Fact]
    public void GetAssemblyAlignment_LinuxArm64_Returns4096()
    {
        Assert.Equal(4096, CanonicalBundleFormat.GetAssemblyAlignment(OSPlatform.Linux, Architecture.Arm64));
    }

    [Fact]
    public void GetAssemblyAlignment_LinuxX64_Returns64()
    {
        Assert.Equal(64, CanonicalBundleFormat.GetAssemblyAlignment(OSPlatform.Linux, Architecture.X64));
    }

    [Fact]
    public void GetCurrentRuntimeIdentifier_IsNotEmpty()
    {
        var rid = CanonicalBundleFormat.GetCurrentRuntimeIdentifier();
        Assert.False(string.IsNullOrEmpty(rid));
        Assert.Contains("-", rid); // Should be like "win-x64"
    }

    [Fact]
    public void BundleMajorVersion_Is6()
    {
        Assert.Equal(6u, CanonicalBundleFormat.BundleMajorVersion);
    }

    [Fact]
    public void BundleMinorVersion_Is0()
    {
        Assert.Equal(0u, CanonicalBundleFormat.BundleMinorVersion);
    }
}
