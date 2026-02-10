using System.Runtime.InteropServices;

namespace SharpTS.Compilation.Bundling.Canonical;

/// <summary>
/// Resolves the apphost template binary for bundling.
/// Consolidates SDK pack discovery logic from ManualBundler and SdkBundlerDetector
/// into a single, testable resolver.
///
/// Search order:
///   1. Explicit path (if provided)
///   2. .NET SDK packs directory (Microsoft.NETCore.App.Host.{RID}/{version})
///   3. Local cache (~/.sharpts/templates/{rid}/{version}/apphost)
///   4. NuGet download (Microsoft.NETCore.App.Host.{RID})
/// </summary>
public static class AppHostTemplateResolver
{
    /// <summary>
    /// Finds the apphost template and returns both the path and SDK version.
    /// Searches SDK packs, then cache, then downloads from NuGet.
    /// </summary>
    public static (string? Path, Version? Version) FindAppHostTemplateWithVersion(
        TextWriter? diagnosticOutput = null)
    {
        var rid = CanonicalBundleFormat.GetCurrentRuntimeIdentifier();
        return FindAppHostTemplateWithVersion(rid, diagnosticOutput);
    }

    /// <summary>
    /// Finds the apphost template for a specific RID.
    /// Searches SDK packs, then cache, then downloads from NuGet.
    /// </summary>
    public static (string? Path, Version? Version) FindAppHostTemplateWithVersion(
        string rid, TextWriter? diagnosticOutput = null)
    {
        // 1. SDK packs directory
        var dotnetRoot = GetDotNetRoot();
        if (dotnetRoot != null)
        {
            var (packPath, packVersion) = FindAppHostInPacks(dotnetRoot, rid);
            if (packPath != null)
                return (packPath, packVersion);
        }

        // 2. Local cache
        var runtimeVersion = AppHostTemplateDownloader.DetectRuntimeVersion();
        var cachedPath = AppHostTemplateDownloader.FindCachedTemplate(rid, runtimeVersion);
        if (cachedPath != null)
        {
            return (cachedPath, Version.Parse(runtimeVersion));
        }

        // 3. NuGet download
        try
        {
            var (downloadedPath, resolvedVersion) =
                AppHostTemplateDownloader.DownloadTemplate(rid, runtimeVersion, diagnosticOutput);
            return (downloadedPath, Version.Parse(resolvedVersion));
        }
        catch (Exception ex)
        {
            diagnosticOutput?.WriteLine($"Warning: Could not download apphost template: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Returns the path to the apphost template, or null if not found.
    /// </summary>
    public static string? FindAppHostTemplate()
    {
        var (path, _) = FindAppHostTemplateWithVersion();
        return path;
    }

    /// <summary>
    /// Returns the .NET root directory, or null if not found.
    /// Checks DOTNET_ROOT env var first, then platform-specific default locations,
    /// then derives from the runtime location.
    /// </summary>
    public static string? GetDotNetRoot()
    {
        // 1. DOTNET_ROOT environment variable
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot) && Directory.Exists(dotnetRoot))
            return dotnetRoot;

        // 2. Platform-specific default locations
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var path = Path.Combine(programFiles, "dotnet");
            if (Directory.Exists(path)) return path;
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var path in new[] { "/usr/local/share/dotnet", "/opt/homebrew/opt/dotnet/libexec" })
            {
                if (Directory.Exists(path)) return path;
            }
        }
        else
        {
            // Linux and other Unix
            var homeDotnet = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
            foreach (var path in new[] { "/usr/share/dotnet", "/usr/lib/dotnet", "/opt/dotnet", homeDotnet })
            {
                if (Directory.Exists(path)) return path;
            }
        }

        // 3. Derive from runtime location
        try
        {
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            if (!string.IsNullOrEmpty(runtimeDir))
            {
                // Navigate up: shared/Microsoft.NETCore.App/version -> dotnet root
                var root = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
                if (Directory.Exists(root))
                    return root;
            }
        }
        catch
        {
            // Ignore derivation errors
        }

        return null;
    }

    /// <summary>
    /// Searches the packs directory for the apphost template with the highest version.
    /// </summary>
    private static (string? Path, Version? Version) FindAppHostInPacks(string dotnetRoot, string rid)
    {
        var packsDir = Path.Combine(dotnetRoot, "packs");
        if (!Directory.Exists(packsDir)) return (null, null);

        var hostPackPattern = $"Microsoft.NETCore.App.Host.{rid}";
        var hostPackDirs = Directory.GetDirectories(packsDir)
            .Where(d => Path.GetFileName(d)
                .StartsWith(hostPackPattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hostPackDirs.Count == 0) return (null, null);

        string? bestPath = null;
        Version? bestVersion = null;

        foreach (var packDir in hostPackDirs)
        {
            foreach (var versionDir in Directory.GetDirectories(packDir))
            {
                var versionStr = Path.GetFileName(versionDir);
                var dashIndex = versionStr.IndexOf('-');
                var cleanVersion = dashIndex > 0 ? versionStr[..dashIndex] : versionStr;

                if (Version.TryParse(cleanVersion, out var version) &&
                    (bestVersion == null || version > bestVersion))
                {
                    var exeName = OperatingSystem.IsWindows() ? "apphost.exe" : "apphost";
                    var apphostPath = Path.Combine(versionDir, "runtimes", rid, "native", exeName);
                    if (File.Exists(apphostPath))
                    {
                        bestVersion = version;
                        bestPath = apphostPath;
                    }
                }
            }
        }

        return (bestPath, bestVersion);
    }
}
