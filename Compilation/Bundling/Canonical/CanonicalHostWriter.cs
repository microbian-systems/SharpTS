using System.Text;
using SharpTS.Diagnostics.Exceptions;

namespace SharpTS.Compilation.Bundling.Canonical;

/// <summary>
/// Patches an apphost template binary with assembly name and bundle header offset.
/// Implements the same placeholder-search-and-replace logic as
/// Microsoft.NET.HostModel.AppHost.HostWriter.
/// </summary>
public static class CanonicalHostWriter
{
    /// <summary>
    /// Patches the DLL path placeholder in the apphost bytes with the actual assembly name.
    /// </summary>
    /// <param name="apphostBytes">The raw apphost template bytes (will be modified in-place).</param>
    /// <param name="assemblyName">Assembly name without extension (e.g., "MyApp").</param>
    /// <returns>The patched apphost bytes (same array, modified).</returns>
    public static byte[] PatchDllPath(byte[] apphostBytes, string assemblyName)
    {
        var dllName = $"{assemblyName}.dll";
        var dllNameBytes = Encoding.UTF8.GetBytes(dllName);

        if (dllNameBytes.Length > CanonicalBundleFormat.DllPathPlaceholderSize)
        {
            throw new CompileException(
                $"Assembly name '{assemblyName}' is too long for the apphost placeholder " +
                $"(max {CanonicalBundleFormat.DllPathPlaceholderSize} bytes).");
        }

        var index = CanonicalBundleFormat.FindSequence(apphostBytes, CanonicalBundleFormat.DllPathPlaceholder);
        if (index < 0)
        {
            throw new CompileException(
                "Could not find DLL path placeholder in apphost template. " +
                "The template may be corrupt or from an incompatible SDK version.");
        }

        // Clear the full placeholder area and write the DLL name
        Array.Clear(apphostBytes, index, CanonicalBundleFormat.DllPathPlaceholderSize);
        Array.Copy(dllNameBytes, 0, apphostBytes, index, dllNameBytes.Length);

        return apphostBytes;
    }

    /// <summary>
    /// Finds the bundle header placeholder offset in the apphost bytes.
    /// This is where the manifest offset will be patched after the bundle is assembled.
    /// </summary>
    /// <param name="apphostBytes">The apphost bytes to search.</param>
    /// <returns>The byte offset where the 8-byte header offset should be written.</returns>
    public static int FindBundleHeaderPlaceholder(ReadOnlySpan<byte> apphostBytes)
    {
        var index = CanonicalBundleFormat.FindSequence(apphostBytes, CanonicalBundleFormat.BundleHeaderPlaceholder);
        if (index < 0)
        {
            throw new CompileException(
                "Could not find bundle header placeholder in apphost template. " +
                "The template may be corrupt or from an incompatible SDK version.");
        }
        return index;
    }

    /// <summary>
    /// Patches the bundle header offset into the apphost bytes at the placeholder location.
    /// The offset points to the start of the manifest within the final bundle.
    /// </summary>
    /// <param name="bundleBytes">The complete bundle bytes (apphost + embedded files + manifest).</param>
    /// <param name="headerPlaceholderOffset">Offset of the header placeholder (from FindBundleHeaderPlaceholder).</param>
    /// <param name="manifestOffset">Absolute offset of the manifest within the bundle.</param>
    public static void PatchBundleHeaderOffset(byte[] bundleBytes, int headerPlaceholderOffset, long manifestOffset)
    {
        var offsetBytes = BitConverter.GetBytes(manifestOffset);
        Array.Copy(offsetBytes, 0, bundleBytes, headerPlaceholderOffset, 8);
    }

    /// <summary>
    /// Sets execute permissions on a file for Unix systems. No-op on Windows.
    /// </summary>
    public static void SetExecutePermission(string filePath)
    {
        if (OperatingSystem.IsWindows())
            return;

        var currentMode = File.GetUnixFileMode(filePath);
        var newMode = currentMode
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(filePath, newMode);
    }
}
