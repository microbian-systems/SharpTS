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
}
