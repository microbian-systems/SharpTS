namespace SharpTS.Runtime.BuiltIns.Modules;

/// <summary>
/// Registry of built-in Node.js-compatible modules.
/// These modules are intercepted during import resolution and handled specially
/// rather than being resolved from the filesystem.
/// </summary>
public static class BuiltInModuleRegistry
{
    private static readonly HashSet<string> _builtInModules =
    [
        "fs",
        "fs/promises",
        "path",
        "os",
        // "querystring" — migrated to stdlib/node/querystring.ts (embedded stdlib).
        // The EmbeddedStdlibProvider claims this specifier via the chain.
        "assert",
        "url",
        "process",
        "crypto",
        "util",
        "readline",
        "child_process",
        "buffer",
        "zlib",
        "events",
        "timers",
        "timers/promises",
        "string_decoder",
        "perf_hooks",
        "stream",
        "stream/promises",
        "stream/web",
        "http",
        "worker_threads",
        "dns",
        "dns/promises",
        "net",
        "https",
        "tls",
        "dgram",
        "cluster",
        "vm",
        "async_hooks",
        "tty"
    ];

    /// <summary>
    /// Checks if a module specifier refers to a built-in module.
    /// </summary>
    /// <param name="specifier">The import specifier (e.g., "fs", "path").</param>
    /// <returns>True if this is a built-in module.</returns>
    public static bool IsBuiltIn(string specifier) => _builtInModules.Contains(specifier);

    /// <summary>
    /// Gets all registered built-in module names.
    /// </summary>
    public static IReadOnlySet<string> GetBuiltInModules() => _builtInModules;

    /// <summary>
    /// The sentinel prefix used for built-in module paths.
    /// </summary>
    public const string BuiltInPrefix = "builtin:";

    /// <summary>
    /// Creates the sentinel path for a built-in module.
    /// </summary>
    public static string GetBuiltInPath(string moduleName) => $"{BuiltInPrefix}{moduleName}";

    /// <summary>
    /// Extracts the module name from a built-in sentinel path.
    /// </summary>
    public static string? GetModuleName(string path)
    {
        if (path.StartsWith(BuiltInPrefix))
            return path[BuiltInPrefix.Length..];
        return null;
    }
}
