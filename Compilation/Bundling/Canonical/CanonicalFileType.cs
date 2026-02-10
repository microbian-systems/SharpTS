namespace SharpTS.Compilation.Bundling.Canonical;

/// <summary>
/// Identifies the type of file embedded in a bundle.
/// Values match Microsoft.NET.HostModel.Bundle.FileType exactly.
/// </summary>
public enum CanonicalFileType : byte
{
    Unknown = 0,
    Assembly = 1,
    NativeBinary = 2,
    DepsJson = 3,
    RuntimeConfigJson = 4,
    Symbols = 5
}
