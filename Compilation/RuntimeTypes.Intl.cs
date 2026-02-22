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

    /// <summary>
    /// Calls formatToParts() on an Intl.DateTimeFormat instance.
    /// </summary>
    public static object? IntlDateTimeFormatFormatToParts(object? formatter, object? date)
    {
        if (formatter is SharpTSIntlDateTimeFormat dtf)
        {
            return dtf.formatToParts(date);
        }
        return null;
    }

    /// <summary>
    /// Calls formatRange() on an Intl.DateTimeFormat instance.
    /// </summary>
    public static object? IntlDateTimeFormatFormatRange(object? formatter, object? start, object? end)
    {
        if (formatter is SharpTSIntlDateTimeFormat dtf)
        {
            return dtf.formatRange(start, end);
        }
        return null;
    }

    /// <summary>
    /// Calls formatRangeToParts() on an Intl.DateTimeFormat instance.
    /// </summary>
    public static object? IntlDateTimeFormatFormatRangeToParts(object? formatter, object? start, object? end)
    {
        if (formatter is SharpTSIntlDateTimeFormat dtf)
        {
            return dtf.formatRangeToParts(start, end);
        }
        return null;
    }

    #endregion

    #region Intl.Collator Support

    public static object CreateIntlCollator(object? locale, object? options)
    {
        return new SharpTSIntlCollator(locale, options);
    }

    public static object? IntlCollatorCompare(object? collator, object? x, object? y)
    {
        if (collator is SharpTSIntlCollator c)
        {
            return c.CompareStrings(x?.ToString() ?? "", y?.ToString() ?? "");
        }
        return 0.0;
    }

    public static object? IntlCollatorResolvedOptions(object? collator)
    {
        if (collator is SharpTSIntlCollator c)
        {
            return new SharpTSObject(c.GetResolvedOptions());
        }
        return null;
    }

    #endregion

    #region Intl.PluralRules Support

    public static object CreateIntlPluralRules(object? locale, object? options)
    {
        return new SharpTSIntlPluralRules(locale, options);
    }

    public static object? IntlPluralRulesSelect(object? rules, object? number)
    {
        if (rules is SharpTSIntlPluralRules pr)
        {
            double num = number switch
            {
                double d => d,
                int i => i,
                long l => l,
                float f => f,
                _ => 0.0
            };
            return pr.SelectCategory(num);
        }
        return "other";
    }

    public static object? IntlPluralRulesResolvedOptions(object? rules)
    {
        if (rules is SharpTSIntlPluralRules pr)
        {
            return new SharpTSObject(pr.GetResolvedOptions());
        }
        return null;
    }

    #endregion

    #region Intl.RelativeTimeFormat Support

    public static object CreateIntlRelativeTimeFormat(object? locale, object? options)
    {
        return new SharpTSIntlRelativeTimeFormat(locale, options);
    }

    public static object? IntlRelativeTimeFormatFormat(object? formatter, object? value, object? unit)
    {
        if (formatter is SharpTSIntlRelativeTimeFormat rtf)
        {
            return rtf.format(value, unit);
        }
        return null;
    }

    public static object? IntlRelativeTimeFormatResolvedOptions(object? formatter)
    {
        if (formatter is SharpTSIntlRelativeTimeFormat rtf)
        {
            return new SharpTSObject(rtf.GetResolvedOptions());
        }
        return null;
    }

    #endregion

    #region Intl.ListFormat Support

    public static object CreateIntlListFormat(object? locale, object? options)
    {
        return new SharpTSIntlListFormat(locale, options);
    }

    public static object? IntlListFormatFormat(object? formatter, object? items)
    {
        if (formatter is SharpTSIntlListFormat lf)
        {
            return lf.format(items);
        }
        return null;
    }

    public static object? IntlListFormatFormatToParts(object? formatter, object? items)
    {
        if (formatter is SharpTSIntlListFormat lf)
        {
            return lf.formatToParts(items);
        }
        return null;
    }

    public static object? IntlListFormatResolvedOptions(object? formatter)
    {
        if (formatter is SharpTSIntlListFormat lf)
        {
            return new SharpTSObject(lf.GetResolvedOptions());
        }
        return null;
    }

    #endregion
}
