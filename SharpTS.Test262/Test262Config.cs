using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpTS.Test262;

/// <summary>
/// Subset configuration loaded from a JSON file. Describes which folders
/// under <c>test/</c> to enumerate, the per-test timeout, and a path to
/// the skip-features list.
/// </summary>
public sealed record Test262Config(
    [property: JsonPropertyName("folders")] IReadOnlyList<string> Folders,
    [property: JsonPropertyName("timeoutSeconds")] int TimeoutSeconds,
    [property: JsonPropertyName("skipFeaturesFile")] string? SkipFeaturesFile)
{
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Test262Config Load(string path)
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<Test262Config>(json, Options)
            ?? throw new InvalidDataException($"Failed to deserialize {path}");
        return config;
    }

    /// <summary>
    /// Reads the skip-features file (one tag per line, '#' comments allowed).
    /// Returns empty set if the file isn't present or not configured.
    /// </summary>
    public HashSet<string> LoadSkipFeatures(string configDir)
    {
        if (string.IsNullOrEmpty(SkipFeaturesFile))
            return new HashSet<string>(StringComparer.Ordinal);

        var path = Path.IsPathRooted(SkipFeaturesFile)
            ? SkipFeaturesFile
            : Path.Combine(configDir, SkipFeaturesFile);

        if (!File.Exists(path))
            return new HashSet<string>(StringComparer.Ordinal);

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length > 0) set.Add(line);
        }
        return set;
    }
}
