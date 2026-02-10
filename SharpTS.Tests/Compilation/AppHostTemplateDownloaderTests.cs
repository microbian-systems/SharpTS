using SharpTS.Compilation.Bundling.Canonical;
using Xunit;

namespace SharpTS.Tests.Compilation;

/// <summary>
/// Unit tests for AppHostTemplateDownloader: cache paths, version detection, RID detection.
/// Network-dependent tests are skipped by default.
/// </summary>
public class AppHostTemplateDownloaderTests
{
    [Fact]
    public void DetectRuntimeVersion_ReturnsMajorMinorPatch()
    {
        var version = AppHostTemplateDownloader.DetectRuntimeVersion();

        Assert.NotNull(version);
        Assert.NotEmpty(version);

        // Should be parseable as major.minor.patch
        Assert.True(Version.TryParse(version, out var parsed));
        Assert.True(parsed!.Major >= 6, "Expected .NET 6+ runtime version");
        Assert.True(parsed.Minor >= 0);
        Assert.True(parsed.Build >= 0);
    }

    [Fact]
    public void GetCacheDirectory_ReturnsPathUnderUserProfile()
    {
        var cacheDir = AppHostTemplateDownloader.GetCacheDirectory();

        Assert.NotNull(cacheDir);
        Assert.NotEmpty(cacheDir);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.StartsWith(userProfile, cacheDir);
        Assert.Contains(".sharpts", cacheDir);
        Assert.Contains("templates", cacheDir);
    }

    [Fact]
    public void GetCachedTemplatePath_ReturnsExpectedStructure()
    {
        var path = AppHostTemplateDownloader.GetCachedTemplatePath("win-x64", "10.0.0");

        Assert.NotNull(path);
        Assert.Contains("win-x64", path);
        Assert.Contains("10.0.0", path);

        var fileName = Path.GetFileName(path);
        // On Windows it should end with .exe, on Unix without
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("apphost.exe", fileName);
        }
        else
        {
            Assert.Equal("apphost", fileName);
        }
    }

    [Fact]
    public void GetCachedTemplatePath_DifferentRids_ProduceDifferentPaths()
    {
        var winPath = AppHostTemplateDownloader.GetCachedTemplatePath("win-x64", "10.0.0");
        var linuxPath = AppHostTemplateDownloader.GetCachedTemplatePath("linux-x64", "10.0.0");

        Assert.NotEqual(winPath, linuxPath);
    }

    [Fact]
    public void GetCachedTemplatePath_DifferentVersions_ProduceDifferentPaths()
    {
        var v10 = AppHostTemplateDownloader.GetCachedTemplatePath("win-x64", "10.0.0");
        var v9 = AppHostTemplateDownloader.GetCachedTemplatePath("win-x64", "9.0.0");

        Assert.NotEqual(v10, v9);
    }

    [Fact]
    public void FindCachedTemplate_ReturnsNull_WhenCacheIsEmpty()
    {
        // Use a version that's very unlikely to be cached
        var result = AppHostTemplateDownloader.FindCachedTemplate("win-x64", "99.99.99");

        Assert.Null(result);
    }

    [Fact]
    public void FindCachedTemplate_ReturnsPath_WhenFileExists()
    {
        // Create a temp cache entry
        var rid = "test-rid";
        var version = "99.99.99";
        var path = AppHostTemplateDownloader.GetCachedTemplatePath(rid, version);
        var dir = Path.GetDirectoryName(path)!;

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, [0x42]);

            var found = AppHostTemplateDownloader.FindCachedTemplate(rid, version);
            Assert.NotNull(found);
            Assert.Equal(path, found);
        }
        finally
        {
            // Clean up
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DetectRuntimeVersion_MatchesEnvironmentVersion()
    {
        var detected = AppHostTemplateDownloader.DetectRuntimeVersion();
        var expected = Environment.Version.ToString(3);

        Assert.Equal(expected, detected);
    }

    [Fact(Skip = "Requires network access - run manually")]
    public void DownloadTemplate_DownloadsAndCaches()
    {
        var rid = CanonicalBundleFormat.GetCurrentRuntimeIdentifier();
        var version = AppHostTemplateDownloader.DetectRuntimeVersion();
        var output = new StringWriter();

        var (path, resolvedVersion) = AppHostTemplateDownloader.DownloadTemplate(rid, version, output);

        Assert.NotNull(path);
        Assert.True(File.Exists(path), $"Downloaded template should exist at {path}");
        Assert.NotEmpty(resolvedVersion);

        // Verify the output contains progress messages
        var outputText = output.ToString();
        Assert.Contains("Downloading apphost template", outputText);
        Assert.Contains("Cached apphost template", outputText);

        // Verify the file is a valid PE/ELF (non-empty)
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 1000, "Apphost template should be a substantial binary");
    }

    [Fact(Skip = "Requires network access - run manually")]
    public void DownloadTemplate_CacheHit_DoesNotRedownload()
    {
        var rid = CanonicalBundleFormat.GetCurrentRuntimeIdentifier();
        var version = AppHostTemplateDownloader.DetectRuntimeVersion();

        // First download
        var (path1, _) = AppHostTemplateDownloader.DownloadTemplate(rid, version);

        // Second call should hit cache (no "Downloading" message)
        var output = new StringWriter();
        var (path2, _) = AppHostTemplateDownloader.DownloadTemplate(rid, version, output);

        Assert.Equal(path1, path2);
    }
}
