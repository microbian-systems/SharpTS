using System.Text.Json;

namespace SharpTS.Packaging;

/// <summary>
/// Loads and parses package.json files for NuGet package metadata.
/// </summary>
public static class PackageJsonLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Attempts to find and load a package.json file in the specified directory or its parents.
    /// </summary>
    /// <param name="startDirectory">Directory to start searching from.</param>
    /// <param name="stopDirectory">Optional directory to stop searching at (exclusive). If specified, the search will not look in this directory or its parents.</param>
    /// <returns>Loaded PackageJson or null if not found.</returns>
    /// <remarks>
    /// The upward walk also stops (exclusive) at the system temp root and the
    /// user profile root: a package.json sitting in those directories is ambient
    /// noise from unrelated tooling, not the manifest of the project being
    /// packed. Each ceiling is still searched when it IS the start directory.
    /// </remarks>
    public static PackageJson? FindAndLoad(string startDirectory, string? stopDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory);
        var stopDirFullName = stopDirectory != null
            ? new DirectoryInfo(stopDirectory).FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : null;

        var ceilings = new[]
            {
                Path.GetTempPath(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            }
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToArray();

        bool isStartDirectory = true;
        while (dir != null)
        {
            // Stop if we've reached the stop directory
            var currentPath = dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (stopDirFullName != null &&
                string.Equals(currentPath, stopDirFullName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Stop when the walk ASCENDS into a ceiling directory; only search
            // a ceiling when the caller started there.
            if (!isStartDirectory &&
                ceilings.Any(c => string.Equals(currentPath, c, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var packageJsonPath = Path.Combine(dir.FullName, "package.json");
            if (File.Exists(packageJsonPath))
            {
                return Load(packageJsonPath);
            }
            isStartDirectory = false;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Loads a package.json file from the specified path.
    /// </summary>
    /// <param name="path">Path to the package.json file.</param>
    /// <returns>Loaded PackageJson.</returns>
    /// <exception cref="FileNotFoundException">If the file doesn't exist.</exception>
    /// <exception cref="JsonException">If the file contains invalid JSON.</exception>
    public static PackageJson Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"package.json not found at: {path}", path);

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<PackageJson>(stream, JsonOptions)
            ?? throw new JsonException($"Failed to parse package.json at: {path}");
    }

    /// <summary>
    /// Attempts to load a package.json file, returning null on any error.
    /// </summary>
    public static PackageJson? TryLoad(string path)
    {
        try
        {
            return Load(path);
        }
        catch
        {
            return null;
        }
    }
}
