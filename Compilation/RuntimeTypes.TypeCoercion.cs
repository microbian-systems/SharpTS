using System.Globalization;
using System.Runtime.CompilerServices;

namespace SharpTS.Compilation;

public static partial class RuntimeTypes
{
    #region Type Coercion

    public static string Stringify(object? value)
    {
        if (value == null) return "null";
        if (IsUndefined(value)) return "undefined";
        return value switch
        {
            bool b => b ? "true" : "false",
            double d => FormatNumber(d),
            System.Numerics.BigInteger bi => $"{bi}n",
            string s => s,
            object[] arr => "[" + string.Join(", ", arr.Select(Stringify)) + "]",
            List<object?> list => "[" + string.Join(", ", list.Select(Stringify)) + "]",
            System.Collections.IList list => "[" + string.Join(", ", list.Cast<object?>().Select(Stringify)) + "]",
            Dictionary<string, object?> dict => StringifyObject(dict),
            _ => value.ToString() ?? "null"
        };
    }

    internal static string FormatNumber(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        // ECMA-262 6.1.6.1.13: integers up to 10^21 - 1 format as plain digits.
        // For Math.Abs(d) >= 2^63 (Int64 overflow boundary), use F0 format.
        if (d == Math.Floor(d) && Math.Abs(d) < 1e21)
        {
            if (Math.Abs(d) < 9.2233720368547758e18)
                return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("F0", CultureInfo.InvariantCulture);
        }
        // ECMA-262 6.1.6.1.13: non-integer doubles use plain decimal when the
        // leading-digit exponent ∈ [-6, 20], exponential otherwise. .NET's G
        // format switches at < 1e-4 — wrong on the "0.000001" boundary.
        if (Math.Abs(d) >= 1e-6)
        {
            // Plain-decimal: variable-precision fixed-point without exponential
            // ever firing. Suppresses trailing zeros via `#` placeholder.
            return d.ToString("0.################", CultureInfo.InvariantCulture);
        }
        // |d| < 1e-6: exponential, JS-style (lowercase e + no leading zeros).
        var s = d.ToString("G15", CultureInfo.InvariantCulture).Replace("E", "e");
        return System.Text.RegularExpressions.Regex.Replace(s, @"e([+-])0+(?=\d)", "e$1");
    }

    private static string StringifyObject(Dictionary<string, object?> dict)
    {
        var props = dict.Select(kv => $"{kv.Key}: {Stringify(kv.Value)}");
        return "{ " + string.Join(", ", props) + " }";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToNumber(object? value)
    {
        if (value == null) return 0.0;
        if (IsUndefined(value)) return double.NaN;  // undefined coerces to NaN, not 0
        return value switch
        {
            double d => d,
            int i => i,
            long l => l,
            bool b => b ? 1.0 : 0.0,
            string s when double.TryParse(s, out var d) => d,
            _ => double.NaN
        };
    }

    /// <summary>
    /// Matches JS Number(value) semantics — empty/whitespace strings return 0 (not NaN).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ConvertToNumber(object? value)
    {
        if (value == null) return 0.0;
        if (IsUndefined(value)) return double.NaN;
        return value switch
        {
            double d => d,
            int i => i,
            long l => l,
            bool b => b ? 1.0 : 0.0,
            string s => ConvertStringToNumber(s),
            _ => double.NaN
        };
    }

    private static double ConvertStringToNumber(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0.0;
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double result))
            return result;
        return double.NaN;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTruthy(object? value)
    {
        if (value == null || IsUndefined(value)) return false;
        return value switch
        {
            bool b => b,
            double d => d != 0.0 && !double.IsNaN(d),
            string s => s.Length > 0,
            System.Numerics.BigInteger bi => bi != 0,
            _ => true
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string TypeOf(object? value)
    {
        if (value == null) return "object"; // typeof null === "object" in JS
        if (IsUndefined(value)) return "undefined";

        // Check for union types using marker interface
        if (value is IUnionType union)
            return TypeOf(union.Value);

        return value switch
        {
            bool => "boolean",
            double or int or long => "number",
            System.Numerics.BigInteger => "bigint",
            string => "string",
            TSFunction => "function",
            Delegate => "function",
            PromisifiedFunction => "function",
            DeprecatedFunction => "function",
            _ => "object"
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool InstanceOf(object? instance, object? classType)
    {
        if (instance == null || classType == null) return false;
        // For compiled code, we need to check if the instance's type matches or inherits from the class type
        var instanceType = instance.GetType();
        var targetType = classType as Type ?? classType.GetType();
        return targetType.IsAssignableFrom(instanceType);
    }

    /// <summary>
    /// Converts arguments to union types using implicit conversion operators if needed.
    /// Used by TSFunction.Invoke for reflection-based invocation.
    /// </summary>
    public static void ConvertArgsForUnionTypes(object?[] args, System.Reflection.ParameterInfo[] parameters)
        => UnionTypeHelper.ConvertArgsForUnionTypes(parameters, args);

    #endregion
}
