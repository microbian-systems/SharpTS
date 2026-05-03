using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of JavaScript/TypeScript RegExp objects.
/// </summary>
/// <remarks>
/// Wraps .NET System.Text.RegularExpressions.Regex with JavaScript semantics.
/// Supports global (g), ignoreCase (i), and multiline (m) flags.
/// Maintains lastIndex for global matching.
/// </remarks>
public class SharpTSRegExp : ITypeCategorized
{
    /// <inheritdoc />
    public TypeCategory RuntimeCategory => TypeCategory.RegExp;

    /// <summary>
    /// Compile cache keyed by (pattern, options). A regex literal in TS
    /// source like <c>/foo/g</c> evaluates to <c>new SharpTSRegExp("foo", "g")</c>
    /// every time the expression runs — in a tight loop that's per-iter
    /// <c>new Regex(...)</c> compilation, the dominant cost (~250 MB of
    /// regex-engine state allocated for N=100K calls in baseline). Sharing
    /// the compiled .NET <c>Regex</c> across calls collapses that to a
    /// single compile per (pattern, options) per process.
    ///
    /// Why a tuple key works: ECMAScript's regex semantics here are fully
    /// captured by (source, flags) — no instance-specific state lives on
    /// the .NET <c>Regex</c> object. Per-instance <c>LastIndex</c> stays
    /// on <c>SharpTSRegExp</c>, not on <c>_regex</c>, so cache sharing is
    /// safe. Cache size is bounded by the set of distinct regex literals
    /// in the program, which is finite.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex> _compileCache = new();

    private readonly Regex _regex;
    private readonly string _source;
    private readonly string _flags;
    private readonly bool _global;
    private readonly bool _ignoreCase;
    private readonly bool _multiline;
    // JS: RegExp instances are objects and support arbitrary property assignment
    // (e.g. minimatch's `Object.assign(new RegExp(...), { _src, _glob })`).
    // internal: emitted $RegExp has its own storage; runtime↔emitted parity
    // tests track public methods only (see RuntimeTypeSyncTests).
    private Dictionary<string, object?>? _properties;

    internal bool TryGetProperty(string name, out object? value)
    {
        if (_properties != null && _properties.TryGetValue(name, out value))
            return true;
        value = null;
        return false;
    }

    internal void SetProperty(string name, object? value)
    {
        _properties ??= [];
        _properties[name] = value;
    }

    /// <summary>
    /// The index at which to start the next match (used with global flag).
    /// </summary>
    public int LastIndex { get; set; }

    /// <summary>
    /// The pattern string of the regular expression.
    /// </summary>
    public string Source => _source;

    /// <summary>
    /// The flags string of the regular expression.
    /// </summary>
    public string Flags => _flags;

    /// <summary>
    /// Whether the global (g) flag is set.
    /// </summary>
    public bool Global => _global;

    /// <summary>
    /// Whether the ignoreCase (i) flag is set.
    /// </summary>
    public bool IgnoreCase => _ignoreCase;

    /// <summary>
    /// Whether the multiline (m) flag is set.
    /// </summary>
    public bool Multiline => _multiline;

    /// <summary>
    /// Creates a RegExp with the specified pattern and optional flags.
    /// </summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="flags">The flags string (g, i, m).</param>
    public SharpTSRegExp(string pattern, string flags = "")
    {
        _source = pattern;
        _flags = NormalizeFlags(flags);
        _global = _flags.Contains('g');
        _ignoreCase = _flags.Contains('i');
        _multiline = _flags.Contains('m');
        LastIndex = 0;

        // Named groups ((?<name>...)) are not supported in ECMAScript mode in .NET.
        // Detect them and fall back to non-ECMAScript mode.
        // .NET also rejects combining ECMAScript with Singleline, so drop ECMAScript
        // whenever the 's' (dotAll) flag is set.
        bool useEcmaScript = !HasNamedGroups(pattern) && !_flags.Contains('s');
        var options = useEcmaScript ? RegexOptions.ECMAScript : RegexOptions.None;
        if (_ignoreCase) options |= RegexOptions.IgnoreCase;
        if (_multiline) options |= RegexOptions.Multiline;
        if (_flags.Contains('s')) options |= RegexOptions.Singleline;

        try
        {
            _regex = _compileCache.GetOrAdd((pattern, options),
                static key => new Regex(key.Pattern, key.Options));
        }
        catch (ArgumentException ex)
        {
            // .NET wraps the underlying RegexParseException in
            // ArgumentException; ConcurrentDictionary.GetOrAdd will surface
            // it as-is on the first failed compile (and won't cache the
            // failure — subsequent retries can re-attempt).
            throw new Exception($"Invalid regular expression: {ex.Message}");
        }
    }

    /// <summary>
    /// Normalize and deduplicate flags, preserving only valid flags.
    /// </summary>
    private static string NormalizeFlags(string flags)
    {
        var sb = new StringBuilder();
        if (flags.Contains('g')) sb.Append('g');
        if (flags.Contains('i')) sb.Append('i');
        if (flags.Contains('m')) sb.Append('m');
        if (flags.Contains('s')) sb.Append('s');
        if (flags.Contains('d')) sb.Append('d');
        return sb.ToString();
    }

    /// <summary>
    /// Detects whether a regex pattern contains named capture groups (?&lt;name&gt;...).
    /// Excludes lookbehind assertions (?&lt;= and (?&lt;!.
    /// </summary>
    internal static bool HasNamedGroups(string pattern)
    {
        int i = 0;
        while (i < pattern.Length - 2)
        {
            if (pattern[i] == '\\')
            {
                i += 2; // skip escaped char
                continue;
            }
            if (pattern[i] == '(' && i + 2 < pattern.Length && pattern[i + 1] == '?' && pattern[i + 2] == '<')
            {
                // Check it's not lookbehind: (?<= or (?<!
                if (i + 3 < pattern.Length && (pattern[i + 3] == '=' || pattern[i + 3] == '!'))
                {
                    i += 4;
                    continue;
                }
                return true;
            }
            i++;
        }
        return false;
    }

    /// <summary>
    /// Builds a groups object from named capture groups in a match result.
    /// Returns null if there are no named groups.
    /// </summary>
    private SharpTSObject? BuildGroupsObject(Match match)
    {
        Dictionary<string, object?>? groups = null;
        foreach (Group group in match.Groups)
        {
            // Skip numeric group names (unnamed groups)
            if (int.TryParse(group.Name, out _))
                continue;

            groups ??= new Dictionary<string, object?>();
            groups[group.Name] = group.Success ? group.Value : null;
        }
        return groups != null ? new SharpTSObject(groups) : null;
    }

    /// <summary>
    /// Tests if the pattern matches the string.
    /// For global regexes, starts from lastIndex and updates it.
    /// </summary>
    /// <param name="input">The string to test against.</param>
    /// <returns>True if the pattern matches, false otherwise.</returns>
    public bool Test(string input)
    {
        if (_global)
        {
            if (LastIndex > input.Length)
            {
                LastIndex = 0;
                return false;
            }

            var match = _regex.Match(input, Math.Min(LastIndex, input.Length));
            if (match.Success)
            {
                LastIndex = match.Index + match.Length;
                return true;
            }
            else
            {
                LastIndex = 0;
                return false;
            }
        }

        return _regex.IsMatch(input);
    }

    /// <summary>
    /// Executes the regex on the string and returns match info.
    /// For global regexes, maintains state via lastIndex.
    /// </summary>
    /// <param name="input">The string to match against.</param>
    /// <returns>An array with the match and capture groups, or null if no match.</returns>
    public SharpTSObject? Exec(string input)
    {
        Match match;

        if (_global)
        {
            if (LastIndex > input.Length)
            {
                LastIndex = 0;
                return null;
            }
            match = _regex.Match(input, Math.Min(LastIndex, input.Length));
        }
        else
        {
            match = _regex.Match(input);
        }

        if (!match.Success)
        {
            if (_global) LastIndex = 0;
            return null;
        }

        if (_global)
        {
            LastIndex = match.Index + match.Length;
        }

        // Build result object with indexed properties: "0", "1", ..., plus index, input, groups
        var fields = new Dictionary<string, object?>
        {
            ["0"] = match.Value,
            ["index"] = (double)match.Index,
            ["input"] = input,
            ["groups"] = BuildGroupsObject(match)
        };

        // Add capture groups (skip group 0 which is the full match)
        for (int i = 1; i < match.Groups.Count; i++)
        {
            var group = match.Groups[i];
            fields[i.ToString()] = group.Success ? group.Value : null;
        }

        return new SharpTSObject(fields);
    }

    /// <summary>
    /// Returns the string representation of the regex: /pattern/flags
    /// </summary>
    public override string ToString() => $"/{_source}/{_flags}";

    /// <summary>
    /// Internal regex accessor for string methods.
    /// </summary>
    internal Regex InternalRegex => _regex;

    /// <summary>
    /// Match all occurrences in the string (used by String.match with global flag).
    /// </summary>
    internal List<string> MatchAll(string input)
    {
        var matches = _regex.Matches(input);
        List<string> result = [];
        foreach (Match m in matches)
        {
            result.Add(m.Value);
        }
        return result;
    }

    /// <summary>
    /// Match all occurrences returning detailed match objects (used by String.matchAll).
    /// Each object has "0" (full match), "1".."n" (groups), "index", "input", "groups".
    /// </summary>
    internal List<SharpTSObject> MatchAllObjects(string input)
    {
        var matches = _regex.Matches(input);
        List<SharpTSObject> result = [];
        foreach (Match m in matches)
        {
            var fields = new Dictionary<string, object?>
            {
                ["0"] = m.Value,
                ["index"] = (double)m.Index,
                ["input"] = input,
                ["groups"] = BuildGroupsObject(m)
            };
            for (int i = 1; i < m.Groups.Count; i++)
            {
                var group = m.Groups[i];
                fields[i.ToString()] = group.Success ? group.Value : null;
            }
            result.Add(new SharpTSObject(fields));
        }
        return result;
    }

    /// <summary>
    /// Replace occurrences in the string.
    /// </summary>
    /// <param name="input">The input string.</param>
    /// <param name="replacement">The replacement string.</param>
    /// <returns>The string with replacements made.</returns>
    internal string Replace(string input, string replacement)
    {
        if (_global)
        {
            return _regex.Replace(input, replacement);
        }
        else
        {
            // Non-global: replace first match only
            return _regex.Replace(input, replacement, 1);
        }
    }

    /// <summary>
    /// Search for the first match in the string.
    /// </summary>
    /// <param name="input">The input string.</param>
    /// <returns>The index of the first match, or -1 if not found.</returns>
    internal int Search(string input)
    {
        var match = _regex.Match(input);
        return match.Success ? match.Index : -1;
    }

    /// <summary>
    /// Split the string by the regex pattern.
    /// </summary>
    /// <param name="input">The input string.</param>
    /// <returns>An array of substrings.</returns>
    internal string[] Split(string input)
    {
        return _regex.Split(input);
    }
}
