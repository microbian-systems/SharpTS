using System.Globalization;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime;

/// <summary>
/// ECMA-262 §7.1.19 ToPropertyKey: coerces an arbitrary value to a property
/// key suitable for <c>Object.defineProperty</c>, <c>Object.getOwnPropertyDescriptor</c>,
/// <c>Object.defineProperties</c>, <c>Object.create</c>, <c>Object.hasOwn</c>,
/// and bracket-style index access.
/// </summary>
/// <remarks>
/// JavaScript property keys are either strings or Symbols. The spec coercion
/// for non-Symbol values is "ToPrimitive(arg, hint=String) then ToString" —
/// notably:
/// <list type="bullet">
///   <item><c>undefined</c> → <c>"undefined"</c> (not <c>""</c>)</item>
///   <item><c>null</c> → <c>"null"</c></item>
///   <item><c>true</c>/<c>false</c> → <c>"true"</c>/<c>"false"</c> (lowercase, not .NET <c>"True"</c>)</item>
///   <item><c>-0</c> → <c>"0"</c> (sign stripped)</item>
///   <item><c>1.5</c> → <c>"1.5"</c></item>
///   <item><c>NaN</c> → <c>"NaN"</c></item>
/// </list>
/// The pre-existing fallback (<c>value?.ToString() ?? ""</c>) gets several of
/// these wrong — boolean casing, the empty-string-for-null, and (for some
/// .NET versions) negative-zero formatting.
/// </remarks>
public static class PropertyKeyConverter
{
    /// <summary>
    /// Coerces <paramref name="value"/> to a property key. Returns either a
    /// <see cref="string"/> (for non-Symbol values) or a <see cref="SharpTSSymbol"/>
    /// passthrough — callers must pattern-match on the result.
    /// </summary>
    public static object ToPropertyKey(object? value)
    {
        if (value is SharpTSSymbol sym) return sym;
        return ToPropertyKeyString(value);
    }

    /// <summary>
    /// Stringifies <paramref name="value"/> per ECMA-262 ToString semantics for
    /// the property-key path. Use when the caller has already separated the
    /// Symbol case.
    /// </summary>
    public static string ToPropertyKeyString(object? value)
    {
        return value switch
        {
            null => "null",
            SharpTSUndefined => "undefined",
            bool b => b ? "true" : "false",
            string s => s,
            // Numeric formatting via the same path RuntimeTypes.FormatNumber uses
            // for compiled mode — keeps interpreted/compiled property keys identical.
            double d => FormatNumber(d),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            System.Numerics.BigInteger bi => bi.ToString(CultureInfo.InvariantCulture),
            // Symbol shouldn't reach here (caller separates them) but if it does,
            // stringify by description so we don't leak ToString boilerplate.
            SharpTSSymbol sym => sym.ToString(),
            // Object/array: ECMA spec says ToPrimitive(hint=String) then recurse.
            // We don't have a full ToPrimitive yet — fall back to ToString, which
            // is what the legacy code did for these cases anyway.
            _ => value.ToString() ?? "",
        };
    }

    /// <summary>
    /// ECMA-262 6.1.6.1.13 Number::toString. Mirrors the logic in
    /// <c>SharpTS.Compilation.RuntimeTypes.FormatNumber</c> so interpreted and
    /// compiled property keys produce identical strings (e.g. <c>-0</c> → <c>"0"</c>,
    /// <c>1e21</c> → <c>"1e+21"</c>).
    /// </summary>
    private static string FormatNumber(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e21)
        {
            // Integer in safe range — no decimal point. Math.Abs(-0.0) is 0,
            // (long)(-0.0) is 0, so -0 round-trips to "0" without a sign.
            if (Math.Abs(d) < 9.2233720368547758e18)
                return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("F0", CultureInfo.InvariantCulture);
        }
        if (Math.Abs(d) >= 1e-6 && Math.Abs(d) < 1e21)
            return d.ToString("0.################", CultureInfo.InvariantCulture);
        var s = d.ToString("G15", CultureInfo.InvariantCulture).Replace("E", "e");
        return System.Text.RegularExpressions.Regex.Replace(s, @"e([+-])0+(?=\d)", "e$1");
    }
}
