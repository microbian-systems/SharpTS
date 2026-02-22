using System.Globalization;
using SharpTS.Runtime.BuiltIns;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of Intl.Segmenter.
/// Provides locale-aware text segmentation by grapheme, word, or sentence boundaries.
/// </summary>
public class SharpTSIntlSegmenter
{
    private readonly string _locale;
    private string _granularity;

    public SharpTSIntlSegmenter(object? locale, object? options)
    {
        string localeStr = locale?.ToString() ?? "";

        CultureInfo culture;
        try
        {
            culture = string.IsNullOrEmpty(localeStr)
                ? CultureInfo.CurrentCulture
                : CultureInfo.GetCultureInfo(localeStr.Replace('_', '-'));
        }
        catch
        {
            culture = CultureInfo.InvariantCulture;
        }

        _locale = culture.Name;
        if (string.IsNullOrEmpty(_locale))
            _locale = "en-US";

        // Default granularity
        _granularity = "grapheme";

        if (options is SharpTSObject obj)
        {
            ParseOptions(obj.Fields);
        }
        else if (options is IDictionary<string, object?> dict)
        {
            ParseOptions(dict);
        }
    }

    private void ParseOptions(IEnumerable<KeyValuePair<string, object?>> opts)
    {
        var dict = opts is IDictionary<string, object?> d
            ? d
            : opts.ToDictionary(kv => kv.Key, kv => kv.Value);

        if (dict.TryGetValue("granularity", out var granVal) && granVal is string g)
            _granularity = g;
    }

    /// <summary>
    /// Segments the input string according to the granularity setting.
    /// Returns a SharpTSIntlSegments (which is a List&lt;object?&gt;) for iteration compatibility.
    /// </summary>
    public SharpTSIntlSegments SegmentText(string input)
    {
        return new SharpTSIntlSegments(input, _granularity);
    }

    public Dictionary<string, object?> GetResolvedOptions()
    {
        return new Dictionary<string, object?>
        {
            ["locale"] = _locale,
            ["granularity"] = _granularity,
        };
    }

    /// <summary>
    /// JS-facing segment() method for compiled mode reflection dispatch.
    /// </summary>
    public object? segment(object? input)
    {
        return SegmentText(input?.ToString() ?? "");
    }

    /// <summary>
    /// JS-facing resolvedOptions() method for compiled mode reflection dispatch.
    /// </summary>
    public object? resolvedOptions()
    {
        return GetResolvedOptions();
    }

    /// <summary>
    /// Gets a member (method) by name for interpreter dispatch.
    /// </summary>
    public object? GetMember(string name)
    {
        return name switch
        {
            "segment" => new BuiltInMethod("segment", 1, (_, _, args) =>
            {
                string input = (args.Count > 0 ? args[0] : null)?.ToString() ?? "";
                return SegmentText(input);
            }),
            "resolvedOptions" => new BuiltInMethod("resolvedOptions", 0, (_, _, _) =>
            {
                return new SharpTSObject(GetResolvedOptions());
            }),
            _ => null
        };
    }

    public override string ToString() => "[object Intl.Segmenter]";
}
