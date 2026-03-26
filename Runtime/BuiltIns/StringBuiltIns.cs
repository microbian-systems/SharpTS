using System.Text;
using SharpTS.Execution;
using SharpTS.Runtime;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

public static class StringBuiltIns
{
    private static readonly BuiltInTypeMemberLookup<string> _lookup =
        BuiltInTypeBuilder<string>.ForInstanceType()
            .Property("length", s => (double)s.Length)
            .MethodV2("charAt", 1, CharAtV2)
            .MethodV2("substring", 1, 2, SubstringV2)
            .MethodV2("indexOf", 1, IndexOfV2)
            .MethodV2("toUpperCase", 0, ToUpperCaseV2)
            .MethodV2("toLowerCase", 0, ToLowerCaseV2)
            .MethodV2("trim", 0, TrimV2)
            .Method("replace", 2, Replace)
            .Method("split", 1, 2, Split)
            .Method("match", 1, Match)
            .Method("matchAll", 1, MatchAll)
            .Method("search", 1, Search)
            .MethodV2("includes", 1, IncludesV2)
            .MethodV2("startsWith", 1, StartsWithV2)
            .MethodV2("endsWith", 1, EndsWithV2)
            .MethodV2("slice", 1, 2, SliceV2)
            .MethodV2("repeat", 1, RepeatV2)
            .Method("padStart", 1, 2, PadStart)
            .Method("padEnd", 1, 2, PadEnd)
            .MethodV2("charCodeAt", 1, CharCodeAtV2)
            .Method("codePointAt", 1, CodePointAt)
            .Method("concat", 0, int.MaxValue, Concat)
            .MethodV2("lastIndexOf", 1, LastIndexOfV2)
            .MethodV2("trimStart", 0, TrimStartV2)
            .MethodV2("trimEnd", 0, TrimEndV2)
            .Method("replaceAll", 2, ReplaceAll)
            .MethodV2("at", 1, AtV2)
            .Method("normalize", 0, 1, Normalize)
            .Method("localeCompare", 1, LocaleCompare)
            .Build();

    private static readonly BuiltInStaticMemberLookup _staticLookup =
        BuiltInStaticBuilder.Create()
            .Method("raw", 1, int.MaxValue, StringRaw)
            .Method("fromCharCode", 0, int.MaxValue, FromCharCode)
            .Method("fromCodePoint", 0, int.MaxValue, FromCodePoint)
            .Build();

    public static object? GetMember(string receiver, string name)
        => _lookup.GetMember(receiver, name);

    /// <summary>
    /// Gets a static member (method) from the String namespace.
    /// Currently only supports String.raw for tagged templates.
    /// </summary>
    public static object? GetStaticMember(string name)
        => _staticLookup.GetMember(name);

    private static object? CharAt(Interpreter _, string str, List<object?> args)
    {
        var index = (int)(double)args[0]!;
        if (index < 0 || index >= str.Length) return "";
        return str[index].ToString();
    }

    private static object? Substring(Interpreter _, string str, List<object?> args)
    {
        var start = Math.Max(0, (int)(double)args[0]!);
        var end = args.Count > 1 ? (int)(double)args[1]! : str.Length;
        if (start >= str.Length) return "";
        if (end > str.Length) end = str.Length;
        if (end <= start) return "";
        return str.Substring(start, end - start);
    }

    private static object? IndexOf(Interpreter _, string str, List<object?> args)
    {
        var search = (string)args[0]!;
        return (double)str.IndexOf(search);
    }

    private static object? ToUpperCase(Interpreter _, string str, List<object?> args)
        => str.ToUpper();

    private static object? ToLowerCase(Interpreter _, string str, List<object?> args)
        => str.ToLower();

    private static object? Trim(Interpreter _, string str, List<object?> args)
        => str.Trim();

    private static object? Replace(Interpreter _, string str, List<object?> args)
    {
        var replacement = args[1]?.ToString() ?? "";

        // Handle RegExp pattern
        if (args[0] is SharpTSRegExp regex)
        {
            return regex.Replace(str, replacement);
        }

        // String pattern: JavaScript replace() only replaces the first occurrence
        var search = args[0]?.ToString() ?? "";
        var index = str.IndexOf(search);
        if (index < 0) return str;
        return str.Substring(0, index) + replacement + str.Substring(index + search.Length);
    }

    private static object? Split(Interpreter _, string str, List<object?> args)
    {
        int? limit = args.Count > 1 && args[1] is double d ? (int)d : null;

        // Handle RegExp separator
        if (args[0] is SharpTSRegExp regex)
        {
            string[] parts = regex.Split(str);
            // Apply limit if specified
            IEnumerable<string> resultParts = limit.HasValue && limit.Value >= 0
                ? parts.Take(limit.Value)
                : parts;
            return new SharpTSArray(resultParts.Select(p => (object?)p).ToList());
        }

        // String separator
        var separator = args[0]?.ToString() ?? "";
        string[] stringParts;
        if (separator == "")
        {
            // Empty separator splits into individual characters
            stringParts = str.Select(c => c.ToString()).ToArray();
        }
        else
        {
            stringParts = str.Split(separator);
        }

        // Apply limit if specified (JavaScript behavior: limit restricts number of results)
        if (limit.HasValue && limit.Value >= 0)
        {
            stringParts = stringParts.Take(limit.Value).ToArray();
        }

        var elements = stringParts.Select(p => (object?)p).ToList();
        return new SharpTSArray(elements);
    }

    private static object? Match(Interpreter _, string str, List<object?> args)
    {
        // Handle RegExp pattern
        if (args[0] is SharpTSRegExp regex)
        {
            if (regex.Global)
            {
                // Global match: return array of all matches
                var matches = regex.MatchAll(str);
                if (matches.Count == 0) return null;
                return new SharpTSArray(matches.Select(m => (object?)m).ToList());
            }
            else
            {
                // Non-global: same as exec()
                return regex.Exec(str);
            }
        }

        // String pattern: find first occurrence
        var search = args[0]?.ToString() ?? "";
        var index = str.IndexOf(search);
        if (index < 0) return null;
        return new SharpTSArray([(object?)search]);
    }

    private static object? MatchAll(Interpreter _, string str, List<object?> args)
    {
        if (args[0] is SharpTSRegExp regex)
        {
            if (!regex.Global)
                throw new Exception("TypeError: String.prototype.matchAll called with a non-global RegExp argument");
            var matchObjects = regex.MatchAllObjects(str);
            return new SharpTSArray(matchObjects.Select(m => (object?)m).ToList());
        }

        // String pattern: create a temporary global RegExp
        var pattern = args[0]?.ToString() ?? "";
        var tempRegex = new SharpTSRegExp(System.Text.RegularExpressions.Regex.Escape(pattern), "g");
        var results = tempRegex.MatchAllObjects(str);
        return new SharpTSArray(results.Select(m => (object?)m).ToList());
    }

    private static object? Search(Interpreter _, string str, List<object?> args)
    {
        // Handle RegExp pattern
        if (args[0] is SharpTSRegExp regex)
        {
            return (double)regex.Search(str);
        }

        // String pattern
        var search = args[0]?.ToString() ?? "";
        return (double)str.IndexOf(search);
    }

    private static object? Includes(Interpreter _, string str, List<object?> args)
    {
        var search = (string)args[0]!;
        return str.Contains(search);
    }

    private static object? StartsWith(Interpreter _, string str, List<object?> args)
    {
        var search = (string)args[0]!;
        return str.StartsWith(search);
    }

    private static object? EndsWith(Interpreter _, string str, List<object?> args)
    {
        var search = (string)args[0]!;
        return str.EndsWith(search);
    }

    private static object? Slice(Interpreter _, string str, List<object?> args)
    {
        var start = (int)(double)args[0]!;
        var end = args.Count > 1 ? (int)(double)args[1]! : str.Length;

        // Handle negative indices (from end of string)
        if (start < 0) start = Math.Max(0, str.Length + start);
        if (end < 0) end = Math.Max(0, str.Length + end);

        // Clamp to valid range
        start = Math.Min(start, str.Length);
        end = Math.Min(end, str.Length);

        if (end <= start) return "";
        return str.Substring(start, end - start);
    }

    private static object? Repeat(Interpreter _, string str, List<object?> args)
    {
        var count = (int)(double)args[0]!;
        if (count < 0) throw new Exception("Runtime Error: Invalid count value for repeat()");
        if (count == 0 || str.Length == 0) return "";
        if (count == 1) return str;

        // Use string.Create with Span for efficient repetition
        return string.Create(str.Length * count, (str, count), static (span, state) =>
        {
            var (s, c) = state;
            var srcSpan = s.AsSpan();
            for (int i = 0; i < c; i++)
            {
                srcSpan.CopyTo(span.Slice(i * s.Length, s.Length));
            }
        });
    }

    private static object? PadStart(Interpreter _, string str, List<object?> args)
    {
        var targetLength = (int)(double)args[0]!;
        var padString = args.Count > 1 ? (string)args[1]! : " ";

        if (targetLength <= str.Length || padString.Length == 0) return str;

        var padLength = targetLength - str.Length;
        // Use string.Create with Span to build result efficiently
        return string.Create(targetLength, (str, padString, padLength), static (span, state) =>
        {
            var (s, pad, pLen) = state;
            var padSpan = pad.AsSpan();
            int pos = 0;
            // Fill padding
            while (pos < pLen)
            {
                int copyLen = Math.Min(pad.Length, pLen - pos);
                padSpan.Slice(0, copyLen).CopyTo(span.Slice(pos, copyLen));
                pos += copyLen;
            }
            // Copy original string
            s.AsSpan().CopyTo(span.Slice(pLen));
        });
    }

    private static object? PadEnd(Interpreter _, string str, List<object?> args)
    {
        var targetLength = (int)(double)args[0]!;
        var padString = args.Count > 1 ? (string)args[1]! : " ";

        if (targetLength <= str.Length || padString.Length == 0) return str;

        var padLength = targetLength - str.Length;
        // Use string.Create with Span to build result efficiently
        return string.Create(targetLength, (str, padString, padLength), static (span, state) =>
        {
            var (s, pad, pLen) = state;
            // Copy original string first
            s.AsSpan().CopyTo(span);
            // Fill padding after
            var padSpan = pad.AsSpan();
            int pos = s.Length;
            while (pos < span.Length)
            {
                int copyLen = Math.Min(pad.Length, span.Length - pos);
                padSpan.Slice(0, copyLen).CopyTo(span.Slice(pos, copyLen));
                pos += copyLen;
            }
        });
    }

    private static object? CharCodeAt(Interpreter _, string str, List<object?> args)
    {
        var index = (int)(double)args[0]!;
        if (index < 0 || index >= str.Length) return double.NaN;
        return (double)str[index];
    }

    private static object? CodePointAt(Interpreter _, string str, List<object?> args)
    {
        var index = (int)(double)args[0]!;
        if (index < 0 || index >= str.Length) return null; // undefined
        // Check for surrogate pair
        if (char.IsHighSurrogate(str[index]) && index + 1 < str.Length && char.IsLowSurrogate(str[index + 1]))
            return (double)char.ConvertToUtf32(str[index], str[index + 1]);
        return (double)str[index];
    }

    private static object? Concat(Interpreter _, string str, List<object?> args)
    {
        if (args.Count == 0) return str;
        if (args.Count == 1) return string.Concat(str, args[0]?.ToString() ?? "");

        // Use StringBuilder for multiple concatenations to avoid intermediate allocations
        var sb = new StringBuilder(str);
        foreach (var arg in args)
        {
            sb.Append(arg?.ToString() ?? "");
        }
        return sb.ToString();
    }

    private static object? LastIndexOf(Interpreter _, string str, List<object?> args)
    {
        var search = (string)args[0]!;
        return (double)str.LastIndexOf(search);
    }

    private static object? TrimStart(Interpreter _, string str, List<object?> args)
        => str.TrimStart();

    private static object? TrimEnd(Interpreter _, string str, List<object?> args)
        => str.TrimEnd();

    private static object? ReplaceAll(Interpreter _, string str, List<object?> args)
    {
        var search = (string)args[0]!;
        var replacement = (string)args[1]!;
        if (search.Length == 0) return str;
        return str.Replace(search, replacement);
    }

    private static object? At(Interpreter _, string str, List<object?> args)
    {
        var index = (int)(double)args[0]!;
        // Handle negative indices
        if (index < 0) index = str.Length + index;
        if (index < 0 || index >= str.Length) return null;
        return str[index].ToString();
    }

    private static object? Normalize(Interpreter _, string str, List<object?> args)
    {
        var form = args.Count > 0 && args[0] is string f ? f : "NFC";
        var normForm = form switch
        {
            "NFC" => System.Text.NormalizationForm.FormC,
            "NFD" => System.Text.NormalizationForm.FormD,
            "NFKC" => System.Text.NormalizationForm.FormKC,
            "NFKD" => System.Text.NormalizationForm.FormKD,
            _ => throw new Exception($"RangeError: The normalization form should be one of NFC, NFD, NFKC, NFKD.")
        };
        return str.Normalize(normForm);
    }

    private static object? LocaleCompare(Interpreter _, string str, List<object?> args)
    {
        var that = args[0]?.ToString() ?? "";
        var result = string.Compare(str, that, StringComparison.CurrentCulture);
        return (double)(result < 0 ? -1 : result > 0 ? 1 : 0);
    }

    /// <summary>
    /// String.raw tag function implementation.
    /// Returns raw strings from template literals with substitutions.
    /// </summary>
    private static object? StringRaw(Interpreter _, List<object?> args)
    {
        if (args.Count == 0)
            throw new Exception("TypeError: String.raw requires at least 1 argument.");

        // First argument should have a 'raw' property
        object? stringsArg = args[0];
        IList<object?>? rawStrings = null;

        if (stringsArg is SharpTSTemplateStringsArray tsa)
        {
            rawStrings = tsa.Raw.Elements;
        }
        else if (stringsArg is SharpTSObject obj)
        {
            var rawProp = obj.GetProperty("raw");
            if (rawProp is SharpTSArray rawArr)
                rawStrings = rawArr.Elements;
        }
        else if (stringsArg is SharpTSArray arr)
        {
            // Check if array has a 'raw' property (via SharpTSTemplateStringsArray)
            if (stringsArg is ISharpTSPropertyAccessor accessor)
            {
                var rawProp = accessor.GetProperty("raw");
                if (rawProp is SharpTSArray rawArr)
                    rawStrings = rawArr.Elements;
            }
            if (rawStrings == null)
            {
                // Use the array elements directly as raw strings
                rawStrings = arr.Elements;
            }
        }

        if (rawStrings == null || rawStrings.Count == 0)
            return "";

        var result = new StringBuilder();
        for (int i = 0; i < rawStrings.Count; i++)
        {
            result.Append(rawStrings[i]?.ToString() ?? "");
            if (i < args.Count - 1 && i < rawStrings.Count - 1)
            {
                result.Append(args[i + 1]?.ToString() ?? "");
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// String.fromCharCode() implementation.
    /// Creates a string from the specified sequence of UTF-16 code units.
    /// </summary>
    private static object? FromCharCode(Interpreter _, List<object?> args)
    {
        if (args.Count == 0) return "";

        // Fast path for single character
        if (args.Count == 1)
        {
            var code = args[0] is double d ? (int)d : 0;
            return ((char)(code & 0xFFFF)).ToString();
        }

        // Multiple characters - use Span-based string creation
        return string.Create(args.Count, args, static (span, argList) =>
        {
            for (int i = 0; i < argList.Count; i++)
            {
                var code = argList[i] is double d ? (int)d : 0;
                span[i] = (char)(code & 0xFFFF);  // Truncate to 16-bit as per JavaScript spec
            }
        });
    }

    /// <summary>
    /// String.fromCodePoint() implementation.
    /// Creates a string from the specified sequence of Unicode code points.
    /// Unlike fromCharCode, handles supplementary characters (> U+FFFF) via surrogate pairs.
    /// </summary>
    private static object? FromCodePoint(Interpreter _, List<object?> args)
    {
        if (args.Count == 0) return "";

        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            var codePoint = arg is double d ? (int)d : 0;
            if (codePoint < 0 || codePoint > 0x10FFFF)
                throw new Exception($"RangeError: Invalid code point {codePoint}");
            sb.Append(char.ConvertFromUtf32(codePoint));
        }
        return sb.ToString();
    }

    #region V2 Implementations (RuntimeValue — no boxing)

    private static RuntimeValue CharAtV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
    {
        var index = (int)args[0].AsNumber();
        if (index < 0 || index >= str.Length) return RuntimeValue.EmptyString;
        return RuntimeValue.FromString(str[index].ToString());
    }

    private static RuntimeValue SubstringV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
    {
        var start = Math.Max(0, (int)args[0].AsNumber());
        var end = args.Length > 1 ? (int)args[1].AsNumber() : str.Length;
        if (start >= str.Length) return RuntimeValue.EmptyString;
        if (end > str.Length) end = str.Length;
        if (end <= start) return RuntimeValue.EmptyString;
        return RuntimeValue.FromString(str.Substring(start, end - start));
    }

    private static RuntimeValue IndexOfV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
    {
        var search = args[0].AsString();
        return RuntimeValue.FromNumber(str.IndexOf(search));
    }

    private static RuntimeValue ToUpperCaseV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromString(str.ToUpper());

    private static RuntimeValue ToLowerCaseV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromString(str.ToLower());

    private static RuntimeValue TrimV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromString(str.Trim());

    private static RuntimeValue IncludesV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromBoolean(str.Contains(args[0].AsString()));

    private static RuntimeValue StartsWithV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromBoolean(str.StartsWith(args[0].AsString()));

    private static RuntimeValue EndsWithV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromBoolean(str.EndsWith(args[0].AsString()));

    private static RuntimeValue SliceV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
    {
        var start = (int)args[0].AsNumber();
        var end = args.Length > 1 ? (int)args[1].AsNumber() : str.Length;
        if (start < 0) start = Math.Max(0, str.Length + start);
        if (end < 0) end = Math.Max(0, str.Length + end);
        start = Math.Min(start, str.Length);
        end = Math.Min(end, str.Length);
        if (end <= start) return RuntimeValue.EmptyString;
        return RuntimeValue.FromString(str.Substring(start, end - start));
    }

    private static RuntimeValue RepeatV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
    {
        var count = (int)args[0].AsNumber();
        if (count < 0) throw new Exception("Runtime Error: Invalid count value for repeat()");
        if (count == 0 || str.Length == 0) return RuntimeValue.EmptyString;
        if (count == 1) return RuntimeValue.FromString(str);
        return RuntimeValue.FromString(string.Create(str.Length * count, (str, count), static (span, state) =>
        {
            var (s, c) = state;
            var srcSpan = s.AsSpan();
            for (int i = 0; i < c; i++)
                srcSpan.CopyTo(span.Slice(i * s.Length, s.Length));
        }));
    }

    private static RuntimeValue CharCodeAtV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
    {
        var index = (int)args[0].AsNumber();
        if (index < 0 || index >= str.Length) return RuntimeValue.NaN;
        return RuntimeValue.FromNumber(str[index]);
    }

    private static RuntimeValue LastIndexOfV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromNumber(str.LastIndexOf(args[0].AsString()));

    private static RuntimeValue TrimStartV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromString(str.TrimStart());

    private static RuntimeValue TrimEndV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
        => RuntimeValue.FromString(str.TrimEnd());

    private static RuntimeValue AtV2(Interpreter _, string str, ReadOnlySpan<RuntimeValue> args)
    {
        var index = (int)args[0].AsNumber();
        if (index < 0) index = str.Length + index;
        if (index < 0 || index >= str.Length) return RuntimeValue.Null;
        return RuntimeValue.FromString(str[index].ToString());
    }

    #endregion
}
