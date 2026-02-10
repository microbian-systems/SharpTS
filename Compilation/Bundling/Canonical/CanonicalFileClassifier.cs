namespace SharpTS.Compilation.Bundling.Canonical;

/// <summary>
/// Classifies files by extension and name into bundle file types.
/// Matches the classification logic in dotnet/runtime HostModel.
/// </summary>
public static class CanonicalFileClassifier
{
    /// <summary>
    /// Infers the <see cref="CanonicalFileType"/> from a file name or relative path.
    /// </summary>
    public static CanonicalFileType Classify(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);

        if (fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
            return CanonicalFileType.DepsJson;

        if (fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            return CanonicalFileType.RuntimeConfigJson;

        var ext = Path.GetExtension(fileName);

        return ext.ToLowerInvariant() switch
        {
            ".dll" => CanonicalFileType.Assembly,
            ".pdb" => CanonicalFileType.Symbols,
            ".ni.dll" => CanonicalFileType.Assembly,
            ".so" or ".dylib" => CanonicalFileType.NativeBinary,
            _ => CanonicalFileType.Unknown
        };
    }

    /// <summary>
    /// Returns true if the file type should be aligned to the assembly alignment boundary.
    /// Only Assembly-type files need alignment for memory-mapped access.
    /// </summary>
    public static bool RequiresAlignment(CanonicalFileType type) =>
        type == CanonicalFileType.Assembly;

    /// <summary>
    /// Returns true if this file type should NOT be compressed even when compression is enabled.
    /// deps.json and runtimeconfig.json are never compressed per the HostModel spec.
    /// </summary>
    public static bool IsCompressionExcluded(CanonicalFileType type) =>
        type is CanonicalFileType.DepsJson or CanonicalFileType.RuntimeConfigJson;
}
