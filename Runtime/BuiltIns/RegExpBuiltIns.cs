using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Provides implementations for JavaScript RegExp object members.
/// Includes instance properties (source, flags, global, etc.) and methods (test, exec, toString).
/// </summary>
public static class RegExpBuiltIns
{
    /// <summary>
    /// Gets an instance member (property or method) for a RegExp object.
    /// </summary>
    public static object? GetMember(SharpTSRegExp receiver, string name)
    {
        // User-set properties shadow nothing built-in but are returned when no
        // builtin matches (JS: RegExp instances are ordinary objects).
        var builtIn = name switch
        {
            // ========== Properties ==========
            "source" => (object?)receiver.Source,
            "flags" => receiver.Flags,
            "global" => receiver.Global,
            "ignoreCase" => receiver.IgnoreCase,
            "multiline" => receiver.Multiline,
            "lastIndex" => (double)receiver.LastIndex,

            // ========== Methods ==========
            "test" => new BuiltInMethod("test", 1, (_, recv, args) =>
            {
                var regex = (SharpTSRegExp)recv!;
                var str = args[0]?.ToString() ?? "";
                return regex.Test(str);
            }),

            "exec" => new BuiltInMethod("exec", 1, (_, recv, args) =>
            {
                var regex = (SharpTSRegExp)recv!;
                var str = args[0]?.ToString() ?? "";
                return regex.Exec(str);
            }),

            "toString" => new BuiltInMethod("toString", 0, (_, recv, _) =>
                ((SharpTSRegExp)recv!).ToString()),

            _ => null
        };
        if (builtIn != null) return builtIn;
        return receiver.TryGetProperty(name, out var userVal) ? userVal : null;
    }

    /// <summary>
    /// Sets an instance member (property) for a RegExp object.
    /// Only lastIndex is writable.
    /// </summary>
    /// <returns>True if the property was set, false if the property is not writable.</returns>
    public static bool SetMember(SharpTSRegExp receiver, string name, object? value)
    {
        if (name == "lastIndex")
        {
            // ECMA-262 ToLength: assignments like `re.lastIndex = undefined`,
            // `= "1.9"`, `= {valueOf: ...}` are legal — they coerce via
            // ToNumber → ToInteger → bounded to [0, 2^53-1]. A hard
            // (int)(double)value cast throws InvalidCastException on the
            // primitives that aren't already double, so route via JS coercion.
            receiver.LastIndex = ToLengthAsInt32(value);
            return true;
        }
        return false;
    }

    /// <summary>
    /// ECMA-262 §7.1.20 ToLength via §7.1.5 ToInteger via §7.1.4 ToNumber,
    /// clamped to int32 (lastIndex storage).
    /// </summary>
    private static int ToLengthAsInt32(object? value)
    {
        double d = value switch
        {
            null => 0,
            SharpTSUndefined => double.NaN,
            double dv => dv,
            int iv => iv,
            long lv => lv,
            bool bv => bv ? 1 : 0,
            string sv => double.TryParse(sv, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN,
            _ => double.NaN
        };
        if (double.IsNaN(d)) return 0;
        if (d <= 0) return 0;
        if (d > int.MaxValue) return int.MaxValue;
        return (int)d;
    }

    /// <summary>
    /// Resolves a well-known symbol on the RegExp prototype to the corresponding
    /// callable per ECMA-262 §22.2.5 (@@match, @@matchAll, @@replace, @@search, @@split).
    /// Returns null for symbols not present on the prototype.
    /// </summary>
    public static object? GetSymbolMember(SharpTSRegExp receiver, SharpTSSymbol symbol)
    {
        if (ReferenceEquals(symbol, SharpTSSymbol.Match))
        {
            return new BuiltInMethod("[Symbol.match]", 1, (_, recv, args) =>
            {
                var regex = (SharpTSRegExp)recv!;
                var s = args.Count > 0 ? args[0]?.ToString() ?? "undefined" : "undefined";
                if (regex.Global)
                {
                    var matches = regex.MatchAll(s);
                    if (matches.Count == 0) return null;
                    return new SharpTSArray(matches.Select(m => (object?)m).ToList());
                }
                return regex.Exec(s);
            });
        }
        if (ReferenceEquals(symbol, SharpTSSymbol.MatchAll))
        {
            return new BuiltInMethod("[Symbol.matchAll]", 1, (_, recv, args) =>
            {
                var regex = (SharpTSRegExp)recv!;
                var s = args.Count > 0 ? args[0]?.ToString() ?? "undefined" : "undefined";
                var results = regex.MatchAllObjects(s);
                return new SharpTSArray(results.Select(m => (object?)m).ToList());
            });
        }
        if (ReferenceEquals(symbol, SharpTSSymbol.Replace))
        {
            return new BuiltInMethod("[Symbol.replace]", 2, (_, recv, args) =>
            {
                var regex = (SharpTSRegExp)recv!;
                var s = args.Count > 0 ? args[0]?.ToString() ?? "undefined" : "undefined";
                var replacement = args.Count > 1 ? args[1]?.ToString() ?? "undefined" : "undefined";
                return regex.Replace(s, replacement);
            });
        }
        if (ReferenceEquals(symbol, SharpTSSymbol.Search))
        {
            return new BuiltInMethod("[Symbol.search]", 1, (_, recv, args) =>
            {
                var regex = (SharpTSRegExp)recv!;
                var s = args.Count > 0 ? args[0]?.ToString() ?? "undefined" : "undefined";
                return (double)regex.Search(s);
            });
        }
        if (ReferenceEquals(symbol, SharpTSSymbol.Split))
        {
            return new BuiltInMethod("[Symbol.split]", 1, 2, (_, recv, args) =>
            {
                var regex = (SharpTSRegExp)recv!;
                var s = args.Count > 0 ? args[0]?.ToString() ?? "undefined" : "undefined";
                int? limit = null;
                if (args.Count > 1 && args[1] is double ld) limit = (int)ld;
                var parts = regex.Split(s);
                IEnumerable<string> resultParts = limit.HasValue && limit.Value >= 0
                    ? parts.Take(limit.Value)
                    : parts;
                return new SharpTSArray(resultParts.Select(p => (object?)p).ToList());
            });
        }
        return null;
    }
}
