using System.Text.Json;

namespace SharpTS.Modules;

/// <summary>
/// Lightweight model for resolution-relevant package.json fields.
/// </summary>
internal sealed class ModulePackageJson
{
    public string? Name { get; init; }
    public string? Main { get; init; }
    public string? Types { get; init; }
    public string? Typings { get; init; }
    public string? Module { get; init; }
    public JsonElement? Exports { get; init; }
    public JsonElement? Imports { get; init; }

    /// <summary>
    /// The owning JsonDocument — must be kept alive while Exports/Imports JsonElements are used.
    /// </summary>
    private JsonDocument? _document;

    /// <summary>
    /// Tries to load and parse a package.json file. Returns null on missing or malformed file.
    /// </summary>
    public static ModulePackageJson? TryLoad(string packageJsonPath)
    {
        if (!File.Exists(packageJsonPath))
            return null;

        try
        {
            var json = File.ReadAllText(packageJsonPath);
            var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var root = doc.RootElement;

            return new ModulePackageJson
            {
                _document = doc,
                Name = GetString(root, "name"),
                Main = GetString(root, "main"),
                Types = GetString(root, "types"),
                Typings = GetString(root, "typings"),
                Module = GetString(root, "module"),
                Exports = GetElement(root, "exports"),
                Imports = GetElement(root, "imports"),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    private static JsonElement? GetElement(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var el) && el.ValueKind != JsonValueKind.Undefined)
            return el;
        return null;
    }
}
