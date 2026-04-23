namespace SharpTS.Test262;

/// <summary>
/// Locates the Test262 submodule checkout. The checkout lives at
/// <c>external/test262/</c> relative to the repo root, which is a few levels
/// above the test assembly's bin directory.
/// </summary>
public static class Test262Paths
{
    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> looking for
    /// <c>external/test262/harness/sta.js</c>. Returns null when the submodule
    /// hasn't been initialized — callers should skip rather than fail.
    /// </summary>
    public static string? TryFindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "external", "test262");
            if (File.Exists(Path.Combine(candidate, "harness", "sta.js")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public static string HarnessDir(string root) => Path.Combine(root, "harness");
    public static string TestDir(string root) => Path.Combine(root, "test");

    /// <summary>
    /// Locates the <c>SharpTS.Test262/</c> project source directory — the place
    /// where <c>config/</c> and <c>baselines/</c> live. Required so tests read
    /// the live files (and can rewrite baselines with <c>UPDATE_BASELINE=1</c>)
    /// instead of stale copies in <c>bin/</c>.
    /// </summary>
    public static string? TryFindProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "SharpTS.Test262");
            if (File.Exists(Path.Combine(candidate, "SharpTS.Test262.csproj")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
