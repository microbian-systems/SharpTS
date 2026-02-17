using SharpTS.Runtime.Types;

namespace SharpTS.Compilation;

public static partial class RuntimeTypes
{
    #region Intl.NumberFormat Support

    /// <summary>
    /// Creates a new Intl.NumberFormat instance from locale and options arguments.
    /// Called from compiled code via reflection for standalone DLL compatibility.
    /// </summary>
    public static object CreateIntlNumberFormat(object? locale, object? options)
    {
        return new SharpTSIntlNumberFormat(locale, options);
    }

    /// <summary>
    /// Calls format() on an Intl.NumberFormat instance.
    /// </summary>
    public static object? IntlNumberFormatFormat(object? formatter, object? number)
    {
        if (formatter is SharpTSIntlNumberFormat nf)
        {
            double num = number switch
            {
                double d => d,
                int i => i,
                long l => l,
                float f => f,
                _ => 0.0
            };
            return nf.FormatNumber(num);
        }
        return null;
    }

    /// <summary>
    /// Calls resolvedOptions() on an Intl.NumberFormat instance.
    /// </summary>
    public static object? IntlNumberFormatResolvedOptions(object? formatter)
    {
        if (formatter is SharpTSIntlNumberFormat nf)
        {
            return new SharpTSObject(nf.GetResolvedOptions());
        }
        return null;
    }

    #endregion

    #region Intl.DateTimeFormat Support

    /// <summary>
    /// Creates a new Intl.DateTimeFormat instance from locale and options arguments.
    /// Called from compiled code via reflection for standalone DLL compatibility.
    /// </summary>
    public static object CreateIntlDateTimeFormat(object? locale, object? options)
    {
        return new SharpTSIntlDateTimeFormat(locale, options);
    }

    /// <summary>
    /// Calls format() on an Intl.DateTimeFormat instance.
    /// </summary>
    public static object? IntlDateTimeFormatFormat(object? formatter, object? date)
    {
        if (formatter is SharpTSIntlDateTimeFormat dtf)
        {
            return dtf.format(date);
        }
        return null;
    }

    /// <summary>
    /// Calls resolvedOptions() on an Intl.DateTimeFormat instance.
    /// </summary>
    public static object? IntlDateTimeFormatResolvedOptions(object? formatter)
    {
        if (formatter is SharpTSIntlDateTimeFormat dtf)
        {
            return new SharpTSObject(dtf.GetResolvedOptions());
        }
        return null;
    }

    #endregion
}
