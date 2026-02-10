using System.IO.Compression;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace SharpTS.Compilation.Bundling.Canonical;

/// <summary>
/// Downloads the apphost template from NuGet when the .NET SDK packs directory
/// is not available. Templates are cached locally at ~/.sharpts/templates/ for
/// subsequent builds.
///
/// Package: Microsoft.NETCore.App.Host.{RID}
/// Entry:   runtimes/{rid}/native/apphost[.exe]
/// </summary>
public static class AppHostTemplateDownloader
{
    private const string NuGetSource = "https://api.nuget.org/v3/index.json";
    private const string PackageIdPrefix = "Microsoft.NETCore.App.Host.";

    /// <summary>
    /// Returns the root cache directory (~/.sharpts/templates/).
    /// </summary>
    public static string GetCacheDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".sharpts", "templates");

    /// <summary>
    /// Returns the expected cache path for a given RID and version.
    /// </summary>
    public static string GetCachedTemplatePath(string rid, string version)
    {
        var exeName = OperatingSystem.IsWindows() ? "apphost.exe" : "apphost";
        return Path.Combine(GetCacheDirectory(), rid, version, exeName);
    }

    /// <summary>
    /// Checks if a cached template exists for the given RID and version.
    /// Returns the path if found, null otherwise.
    /// </summary>
    public static string? FindCachedTemplate(string rid, string version)
    {
        var path = GetCachedTemplatePath(rid, version);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Detects the installed .NET runtime version as major.minor.patch.
    /// </summary>
    public static string DetectRuntimeVersion()
        => Environment.Version.ToString(3);

    /// <summary>
    /// Downloads the apphost template from NuGet, extracts it, and caches it locally.
    /// Returns the cached path and resolved version string.
    /// </summary>
    /// <param name="rid">Runtime identifier (e.g., "win-x64").</param>
    /// <param name="version">Target version string (major.minor.patch).</param>
    /// <param name="diagnosticOutput">Optional writer for progress messages.</param>
    /// <returns>The path to the cached apphost template and the resolved version.</returns>
    public static (string Path, string Version) DownloadTemplate(
        string rid, string version, TextWriter? diagnosticOutput = null)
    {
        // Run the async download synchronously to keep the bundler API synchronous
        return DownloadTemplateAsync(rid, version, diagnosticOutput, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Async implementation of the NuGet download + extract + cache pipeline.
    /// </summary>
    private static async Task<(string Path, string Version)> DownloadTemplateAsync(
        string rid, string version, TextWriter? diagnosticOutput, CancellationToken ct)
    {
        var packageId = PackageIdPrefix + rid;

        if (!Version.TryParse(version, out var targetVersion))
        {
            throw new InvalidOperationException(
                $"Could not parse version '{version}' for apphost template download.");
        }

        // Set up NuGet repository
        var packageSource = new PackageSource(NuGetSource);
        var repository = Repository.Factory.GetCoreV3(packageSource);
        var cache = new SourceCacheContext { NoCache = true };
        var logger = NullLogger.Instance;

        var resource = await repository.GetResourceAsync<FindPackageByIdResource>(ct);

        // Get all available versions and find best match
        var allVersions = await resource.GetAllVersionsAsync(packageId, cache, logger, ct);
        var versionList = allVersions?.ToList();

        if (versionList == null || versionList.Count == 0)
        {
            throw new InvalidOperationException(
                $"No versions found for NuGet package '{packageId}'.");
        }

        // Find the exact version or highest matching major.minor, or fall back to highest in same major
        var resolvedVersion = FindBestVersion(versionList, targetVersion);
        if (resolvedVersion == null)
        {
            throw new InvalidOperationException(
                $"No compatible version found for '{packageId}' targeting {version}. " +
                $"Available: {string.Join(", ", versionList.TakeLast(5))}");
        }

        var resolvedVersionStr = resolvedVersion.Version.ToString(3);

        // Check cache again with resolved version (in case we fell back to a different patch)
        var existingCached = FindCachedTemplate(rid, resolvedVersionStr);
        if (existingCached != null)
        {
            return (existingCached, resolvedVersionStr);
        }

        diagnosticOutput?.Write($"Downloading apphost template for {rid} v{resolvedVersionStr}... ");

        // Download the .nupkg to memory
        using var nupkgStream = new MemoryStream();
        var downloaded = await resource.CopyNupkgToStreamAsync(
            packageId, resolvedVersion, nupkgStream, cache, logger, ct);

        if (!downloaded)
        {
            throw new InvalidOperationException(
                $"Failed to download NuGet package '{packageId}' v{resolvedVersion}.");
        }

        // Extract the apphost from the zip
        nupkgStream.Position = 0;
        var apphostBytes = ExtractAppHostFromNupkg(nupkgStream, rid);

        // Cache with atomic write to prevent corrupt entries from concurrent builds
        var cachedPath = GetCachedTemplatePath(rid, resolvedVersionStr);
        AtomicCacheWrite(cachedPath, apphostBytes);

        diagnosticOutput?.WriteLine("done.");
        diagnosticOutput?.WriteLine($"Cached apphost template at {cachedPath}");

        return (cachedPath, resolvedVersionStr);
    }

    /// <summary>
    /// Finds the best matching NuGet version for the target.
    /// Priority: exact match > highest same major.minor > highest same major.
    /// </summary>
    private static NuGetVersion? FindBestVersion(List<NuGetVersion> versions, Version target)
    {
        // Only consider stable (non-prerelease) versions
        var stable = versions.Where(v => !v.IsPrerelease).ToList();
        if (stable.Count == 0)
            stable = versions; // Fall back to all versions if no stable ones

        // 1. Exact match
        var exact = stable.FirstOrDefault(v =>
            v.Major == target.Major && v.Minor == target.Minor && v.Patch == target.Build);
        if (exact != null) return exact;

        // 2. Highest patch in same major.minor
        var sameMajorMinor = stable
            .Where(v => v.Major == target.Major && v.Minor == target.Minor)
            .OrderByDescending(v => v.Patch)
            .FirstOrDefault();
        if (sameMajorMinor != null) return sameMajorMinor;

        // 3. Highest in same major
        var sameMajor = stable
            .Where(v => v.Major == target.Major)
            .OrderByDescending(v => v.Version)
            .FirstOrDefault();
        return sameMajor;
    }

    /// <summary>
    /// Extracts the apphost binary from a .nupkg zip stream.
    /// Looks for runtimes/{rid}/native/apphost[.exe].
    /// </summary>
    private static byte[] ExtractAppHostFromNupkg(MemoryStream nupkgStream, string rid)
    {
        var exeName = OperatingSystem.IsWindows() ? "apphost.exe" : "apphost";
        var entryPath = $"runtimes/{rid}/native/{exeName}";

        using var archive = new ZipArchive(nupkgStream, ZipArchiveMode.Read, leaveOpen: true);

        var entry = archive.Entries.FirstOrDefault(e =>
            e.FullName.Equals(entryPath, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            // List available entries for diagnostics
            var nativeEntries = archive.Entries
                .Where(e => e.FullName.Contains("native", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FullName)
                .ToList();

            throw new InvalidOperationException(
                $"Apphost template not found at '{entryPath}' in NuGet package. " +
                $"Available native entries: [{string.Join(", ", nativeEntries)}]");
        }

        using var entryStream = entry.Open();
        using var ms = new MemoryStream();
        entryStream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes the apphost bytes to the cache path atomically.
    /// Uses a temp file + rename to prevent partial writes from concurrent builds.
    /// </summary>
    private static void AtomicCacheWrite(string cachedPath, byte[] data)
    {
        var dir = Path.GetDirectoryName(cachedPath)!;
        Directory.CreateDirectory(dir);

        var tempPath = cachedPath + $".tmp.{Guid.NewGuid():N}";
        try
        {
            File.WriteAllBytes(tempPath, data);

            // Atomic replace
            if (File.Exists(cachedPath))
            {
                // Another concurrent build may have written it - that's fine
                BundlingIoPolicy.TryDelete(tempPath);
                return;
            }

            try
            {
                File.Move(tempPath, cachedPath);
            }
            catch (IOException)
            {
                // Race: another process wrote the file between our check and move.
                // The cached file exists now, so we're fine.
                BundlingIoPolicy.TryDelete(tempPath);
            }
        }
        catch
        {
            BundlingIoPolicy.TryDelete(tempPath);
            throw;
        }
    }
}
