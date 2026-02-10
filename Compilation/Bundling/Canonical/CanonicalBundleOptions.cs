namespace SharpTS.Compilation.Bundling.Canonical;

/// <summary>
/// Configuration options for the canonical bundler.
/// </summary>
public sealed class CanonicalBundleOptions
{
    /// <summary>
    /// Name of the managed assembly (without extension).
    /// </summary>
    public required string AssemblyName { get; init; }

    /// <summary>
    /// Path to the managed DLL to embed.
    /// </summary>
    public required string DllPath { get; init; }

    /// <summary>
    /// Desired output path for the final executable.
    /// </summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// The .NET SDK/runtime version to target in runtimeconfig.json.
    /// If null, will be inferred from the apphost template version.
    /// </summary>
    public Version? TargetFrameworkVersion { get; init; }

    /// <summary>
    /// Maximum number of I/O retry attempts for transient failures (file locks, AV scanning).
    /// Default: 5.
    /// </summary>
    public int MaxRetries { get; init; } = 5;

    /// <summary>
    /// Base delay in milliseconds for exponential backoff on I/O retries.
    /// Default: 100ms.
    /// </summary>
    public int RetryBaseDelayMs { get; init; } = 100;

    /// <summary>
    /// If true, write to a temp file first and atomically replace the output.
    /// Default: true.
    /// </summary>
    public bool UseAtomicWrite { get; init; } = true;

    /// <summary>
    /// Optional explicit path to the apphost template.
    /// If null, the template resolver will search SDK packs.
    /// </summary>
    public string? AppHostTemplatePath { get; init; }

    /// <summary>
    /// Whether to include diagnostic output during bundling.
    /// </summary>
    public bool DiagnosticOutput { get; init; }

    /// <summary>
    /// Additional files to embed in the bundle beyond the main DLL and runtimeconfig.
    /// Each entry is a (sourcePath, bundleRelativePath) pair.
    /// </summary>
    public IReadOnlyList<(string SourcePath, string RelativePath)> AdditionalFiles { get; init; } = [];

    /// <summary>
    /// File name patterns to exclude from the bundle (glob-style, e.g. "*.pdb").
    /// Applied to relative paths of additional files.
    /// </summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];
}
