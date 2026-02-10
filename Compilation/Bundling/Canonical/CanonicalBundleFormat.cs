using System.Runtime.InteropServices;

namespace SharpTS.Compilation.Bundling.Canonical;

/// <summary>
/// Constants and layout rules for the .NET single-file bundle format.
/// All values are aligned with the dotnet/runtime HostModel implementation.
/// </summary>
public static class CanonicalBundleFormat
{
    /// <summary>
    /// Bundle major version for .NET 6+ bundles.
    /// </summary>
    public const uint BundleMajorVersion = 6;

    /// <summary>
    /// Bundle minor version (always 0).
    /// </summary>
    public const uint BundleMinorVersion = 0;

    /// <summary>
    /// Length of the deterministic bundle ID (characters).
    /// </summary>
    public const int BundleIdLength = 12;

    /// <summary>
    /// 32-byte SHA-256 signature of ".net core bundle" used to locate the bundle header
    /// in the apphost binary. The full 40-byte placeholder is 8 zero bytes (header offset)
    /// followed by this signature.
    /// </summary>
    public static ReadOnlySpan<byte> BundleSignature =>
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    ];

    /// <summary>
    /// Full 40-byte bundle header placeholder: 8 zero bytes + 32-byte signature.
    /// Searched in the apphost to locate where to patch the manifest offset.
    /// </summary>
    public static ReadOnlySpan<byte> BundleHeaderPlaceholder =>
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    ];

    /// <summary>
    /// DLL path placeholder: SHA-256 of "foobar" (64 ASCII hex characters).
    /// The apphost template contains this as a marker for where to write the
    /// managed assembly DLL name. The placeholder area is 1024 bytes.
    /// </summary>
    public static ReadOnlySpan<byte> DllPathPlaceholder =>
        "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2"u8;

    /// <summary>
    /// Size of the DLL path placeholder area in bytes.
    /// </summary>
    public const int DllPathPlaceholderSize = 1024;

    /// <summary>
    /// Returns the assembly alignment in bytes for the given OS and architecture.
    /// Matches TargetInfo.DetermineAssemblyAlignment() in dotnet/runtime.
    /// </summary>
    public static int GetAssemblyAlignment(OSPlatform os, Architecture arch)
    {
        if (os == OSPlatform.Windows)
            return 4096;

        // Unix platforms
        return arch switch
        {
            Architecture.LoongArch64 => 16384,
            Architecture.Arm64 => 4096,
            _ => 64
        };
    }

    /// <summary>
    /// Returns the assembly alignment for the current platform.
    /// </summary>
    public static int GetCurrentAssemblyAlignment()
    {
        var os = GetCurrentOSPlatform();
        var arch = RuntimeInformation.OSArchitecture;
        return GetAssemblyAlignment(os, arch);
    }

    /// <summary>
    /// Returns the current OS as an OSPlatform value.
    /// </summary>
    public static OSPlatform GetCurrentOSPlatform()
    {
        if (OperatingSystem.IsWindows()) return OSPlatform.Windows;
        if (OperatingSystem.IsLinux()) return OSPlatform.Linux;
        if (OperatingSystem.IsMacOS()) return OSPlatform.OSX;
        return OSPlatform.Windows;
    }

    /// <summary>
    /// Returns the current runtime identifier string (e.g., "win-x64", "linux-arm64").
    /// </summary>
    public static string GetCurrentRuntimeIdentifier()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "x64"
        };

        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsLinux()) return $"linux-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        return $"win-{arch}";
    }

    /// <summary>
    /// Searches for a byte sequence in a larger byte array.
    /// Returns the index of the first match, or -1 if not found.
    /// </summary>
    public static int FindSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return -1;

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        }

        return -1;
    }
}
