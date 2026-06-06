namespace SharpTS.TypeScriptConformance;

/// <summary>
/// Locates the microsoft/TypeScript submodule checkout. The checkout lives at
/// <c>external/typescript/</c> relative to the repo root, which is a few levels
/// above the test assembly's bin directory.
/// </summary>
public static class TypeScriptConformancePaths
{
    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> looking for
    /// <c>external/typescript/src/compiler/checker.ts</c> (a TS-repo-unique
    /// sentinel). Returns null when the submodule hasn't been initialized —
    /// callers should skip rather than fail.
    /// </summary>
    public static string? TryFindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "external", "typescript");
            if (File.Exists(Path.Combine(candidate, "src", "compiler", "checker.ts")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public static string ConformanceDir(string root) => Path.Combine(root, "tests", "cases", "conformance");
    public static string BaselinesDir(string root) => Path.Combine(root, "tests", "baselines", "reference");
    public static string LibDir(string root) => Path.Combine(root, "src", "lib");

    /// <summary>
    /// Locates the <c>SharpTS.TypeScriptConformance/</c> project source
    /// directory — the place where <c>config/</c> and <c>baselines/</c> will
    /// live. Required so tests read the live files (and can rewrite baselines
    /// with <c>SHARPTS_TSCONFORMANCE_UPDATE_BASELINE=1</c>) instead of stale
    /// copies in <c>bin/</c>.
    /// </summary>
    public static string? TryFindProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "SharpTS.TypeScriptConformance");
            if (File.Exists(Path.Combine(candidate, "SharpTS.TypeScriptConformance.csproj")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
