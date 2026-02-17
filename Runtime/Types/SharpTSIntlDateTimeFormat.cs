using System.Globalization;
using SharpTS.Runtime.BuiltIns;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of Intl.DateTimeFormat.
/// Provides locale-aware date/time formatting using .NET's CultureInfo/DateTimeFormatInfo.
/// </summary>
public class SharpTSIntlDateTimeFormat
{
    private string _locale;
    private string? _dateStyle;
    private string? _timeStyle;
    private string? _year;
    private string? _month;
    private string? _day;
    private string? _weekday;
    private string? _hour;
    private string? _minute;
    private string? _second;
    private bool? _hour12;
    private string? _timeZone;
    private string? _timeZoneName;
    private string? _era;
    private int? _fractionalSecondDigits;
    private readonly CultureInfo _culture;
    private readonly string _formatString;

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public SharpTSIntlDateTimeFormat(object? locale, object? options)
    {
        // Parse locale
        string localeStr = locale?.ToString() ?? "";
        _locale = localeStr;

        try
        {
            _culture = string.IsNullOrEmpty(localeStr)
                ? CultureInfo.CurrentCulture
                : CultureInfo.GetCultureInfo(localeStr.Replace('_', '-'));
        }
        catch
        {
            _culture = CultureInfo.InvariantCulture;
        }

        _locale = _culture.Name;
        if (string.IsNullOrEmpty(_locale))
            _locale = "en-US";

        // Parse options
        if (options is SharpTSObject obj)
        {
            ParseOptions(obj.Fields);
        }
        else if (options is IDictionary<string, object?> dict)
        {
            ParseOptions(dict);
        }

        // Build format string from options
        _formatString = BuildFormatString();
    }

    private void ParseOptions(IEnumerable<KeyValuePair<string, object?>> opts)
    {
        var dict = opts is IDictionary<string, object?> d
            ? d
            : opts.ToDictionary(kv => kv.Key, kv => kv.Value);

        if (dict.TryGetValue("dateStyle", out var ds) && ds is string dateStyle)
            _dateStyle = dateStyle;

        if (dict.TryGetValue("timeStyle", out var ts) && ts is string timeStyle)
            _timeStyle = timeStyle;

        if (dict.TryGetValue("year", out var y) && y is string year)
            _year = year;

        if (dict.TryGetValue("month", out var m) && m is string month)
            _month = month;

        if (dict.TryGetValue("day", out var dy) && dy is string day)
            _day = day;

        if (dict.TryGetValue("weekday", out var wd) && wd is string weekday)
            _weekday = weekday;

        if (dict.TryGetValue("hour", out var h) && h is string hour)
            _hour = hour;

        if (dict.TryGetValue("minute", out var mi) && mi is string minute)
            _minute = minute;

        if (dict.TryGetValue("second", out var s) && s is string second)
            _second = second;

        if (dict.TryGetValue("hour12", out var h12))
        {
            if (h12 is bool b)
                _hour12 = b;
            else if (h12 is string h12s)
                _hour12 = h12s != "false";
        }

        if (dict.TryGetValue("timeZone", out var tz) && tz is string timeZone)
            _timeZone = timeZone;

        if (dict.TryGetValue("timeZoneName", out var tzn) && tzn is string timeZoneName)
            _timeZoneName = timeZoneName;

        if (dict.TryGetValue("era", out var e) && e is string era)
            _era = era;

        if (dict.TryGetValue("fractionalSecondDigits", out var fsd))
        {
            _fractionalSecondDigits = fsd switch
            {
                double dv => (int)dv,
                int iv => iv,
                _ => null
            };
        }
    }

    private string BuildFormatString()
    {
        // If dateStyle/timeStyle are specified, use standard format strings
        if (_dateStyle != null || _timeStyle != null)
        {
            return BuildStyleFormat();
        }

        // If individual components are specified, build a custom format
        if (_year != null || _month != null || _day != null || _weekday != null ||
            _hour != null || _minute != null || _second != null)
        {
            return BuildComponentFormat();
        }

        // Default: date and time in medium style
        return "G";
    }

    private string BuildStyleFormat()
    {
        string? datePart = _dateStyle switch
        {
            "full" => "D",
            "long" => "D",
            "medium" => "d",
            "short" => "d",
            _ => null
        };

        string? timePart = _timeStyle switch
        {
            "full" => "T",
            "long" => "T",
            "medium" => "T",
            "short" => "t",
            _ => null
        };

        if (datePart != null && timePart != null)
        {
            // Use culture's full date/time pattern built from separate parts
            var dtfi = _culture.DateTimeFormat;
            string datePattern = datePart == "D" ? dtfi.LongDatePattern : dtfi.ShortDatePattern;
            string timePattern = timePart == "T" ? dtfi.LongTimePattern : dtfi.ShortTimePattern;
            return datePattern + " " + timePattern;
        }

        if (datePart != null) return datePart;
        if (timePart != null) return timePart;

        return "G";
    }

    private string BuildComponentFormat()
    {
        var parts = new List<string>();

        // Weekday
        if (_weekday != null)
        {
            parts.Add(_weekday switch
            {
                "long" => "dddd",
                "short" => "ddd",
                "narrow" => "ddd".Substring(0, 1), // First letter approximation
                _ => "ddd"
            });
        }

        // Era (approximate)
        if (_era != null)
        {
            parts.Add("g");
        }

        // Year
        if (_year != null)
        {
            parts.Add(_year switch
            {
                "2-digit" => "yy",
                _ => "yyyy" // "numeric"
            });
        }

        // Month
        if (_month != null)
        {
            parts.Add(_month switch
            {
                "2-digit" => "MM",
                "long" => "MMMM",
                "short" => "MMM",
                "narrow" => "MMM".Substring(0, 1),
                _ => "M" // "numeric"
            });
        }

        // Day
        if (_day != null)
        {
            parts.Add(_day switch
            {
                "2-digit" => "dd",
                _ => "d" // "numeric"
            });
        }

        // Build date portion
        string datePortion = "";
        if (parts.Count > 0)
        {
            datePortion = string.Join(" ", parts);
        }

        // Time portion
        var timeParts = new List<string>();

        if (_hour != null)
        {
            bool use12 = _hour12 ?? false;
            timeParts.Add(_hour switch
            {
                "2-digit" when use12 => "hh",
                "2-digit" => "HH",
                _ when use12 => "h",
                _ => "H" // "numeric"
            });
        }

        if (_minute != null)
        {
            timeParts.Add(_minute switch
            {
                "2-digit" => "mm",
                _ => "m" // "numeric"
            });
        }

        if (_second != null)
        {
            string secFmt = _second switch
            {
                "2-digit" => "ss",
                _ => "s" // "numeric"
            };

            if (_fractionalSecondDigits.HasValue && _fractionalSecondDigits.Value > 0)
            {
                secFmt += "." + new string('f', Math.Min(_fractionalSecondDigits.Value, 3));
            }

            timeParts.Add(secFmt);
        }

        string timePortion = "";
        if (timeParts.Count > 0)
        {
            timePortion = string.Join(":", timeParts);
            if (_hour12 == true)
            {
                timePortion += " tt";
            }
        }

        // Combine
        if (!string.IsNullOrEmpty(datePortion) && !string.IsNullOrEmpty(timePortion))
            return datePortion + " " + timePortion;
        if (!string.IsNullOrEmpty(datePortion))
            return datePortion;
        if (!string.IsNullOrEmpty(timePortion))
            return timePortion;

        return "G";
    }

    /// <summary>
    /// Formats a DateTime according to the locale and options.
    /// </summary>
    public string FormatDate(DateTime dateTime)
    {
        // Apply timeZone if specified
        if (_timeZone != null)
        {
            try
            {
                if (_timeZone.Equals("UTC", StringComparison.OrdinalIgnoreCase))
                {
                    dateTime = dateTime.Kind == DateTimeKind.Utc
                        ? dateTime
                        : dateTime.ToUniversalTime();
                }
                else
                {
                    var tz = TimeZoneInfo.FindSystemTimeZoneById(_timeZone);
                    dateTime = TimeZoneInfo.ConvertTime(dateTime, tz);
                }
            }
            catch
            {
                // Invalid timezone, use as-is
            }
        }

        string result = dateTime.ToString(_formatString, _culture);

        // Append timezone name if requested
        if (_timeZoneName != null)
        {
            string tzAbbr = GetTimeZoneAbbreviation(dateTime);
            result += " " + tzAbbr;
        }

        return result;
    }

    private string GetTimeZoneAbbreviation(DateTime dateTime)
    {
        if (_timeZone?.Equals("UTC", StringComparison.OrdinalIgnoreCase) == true)
            return _timeZoneName == "long" ? "Coordinated Universal Time" : "UTC";

        var tz = _timeZone != null
            ? TimeZoneInfo.FindSystemTimeZoneById(_timeZone)
            : TimeZoneInfo.Local;

        return _timeZoneName == "long"
            ? (tz.IsDaylightSavingTime(dateTime) ? tz.DaylightName : tz.StandardName)
            : GetShortTimeZoneName(tz, dateTime);
    }

    private static string GetShortTimeZoneName(TimeZoneInfo tz, DateTime dateTime)
    {
        // Extract abbreviation from display name or use offset
        var offset = tz.GetUtcOffset(dateTime);
        return $"GMT{(offset >= TimeSpan.Zero ? "+" : "")}{offset.Hours:D2}:{offset.Minutes:D2}";
    }

    /// <summary>
    /// Returns an object describing the computed options of the formatter.
    /// </summary>
    public Dictionary<string, object?> GetResolvedOptions()
    {
        var result = new Dictionary<string, object?>
        {
            ["locale"] = _locale,
            ["numberingSystem"] = "latn",
            ["calendar"] = "gregory",
        };

        if (_dateStyle != null) result["dateStyle"] = _dateStyle;
        if (_timeStyle != null) result["timeStyle"] = _timeStyle;
        if (_year != null) result["year"] = _year;
        if (_month != null) result["month"] = _month;
        if (_day != null) result["day"] = _day;
        if (_weekday != null) result["weekday"] = _weekday;
        if (_hour != null) result["hour"] = _hour;
        if (_minute != null) result["minute"] = _minute;
        if (_second != null) result["second"] = _second;
        if (_hour12 != null) result["hour12"] = _hour12.Value;
        if (_timeZone != null) result["timeZone"] = _timeZone;
        if (_timeZoneName != null) result["timeZoneName"] = _timeZoneName;
        if (_era != null) result["era"] = _era;
        if (_fractionalSecondDigits != null) result["fractionalSecondDigits"] = (double)_fractionalSecondDigits.Value;

        return result;
    }

    /// <summary>
    /// Converts a JS date value to DateTime.
    /// Handles DateTime, double (epoch ms), string, SharpTSDate, and emitted $TSDate (via reflection).
    /// </summary>
    private static DateTime ToDateTime(object? value)
    {
        if (value == null) return DateTime.Now;
        if (value is DateTime dt) return dt;
        if (value is SharpTSDate d) return UnixEpoch.AddMilliseconds(d.GetTime()).ToLocalTime();
        if (value is double ms) return UnixEpoch.AddMilliseconds(ms).ToLocalTime();
        if (value is int i) return UnixEpoch.AddMilliseconds(i).ToLocalTime();
        if (value is long l) return UnixEpoch.AddMilliseconds(l).ToLocalTime();
        if (value is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        // Handle emitted $TSDate type (compiled mode) via reflection
        var getTimeMethod = value.GetType().GetMethod("GetTime");
        if (getTimeMethod != null)
        {
            var result = getTimeMethod.Invoke(value, null);
            if (result is double epochMs)
                return UnixEpoch.AddMilliseconds(epochMs).ToLocalTime();
        }

        return DateTime.Now;
    }

    /// <summary>
    /// JS-facing format method for compiled mode reflection dispatch.
    /// </summary>
    public object? format(object? date)
    {
        DateTime dt = ToDateTime(date);
        return FormatDate(dt);
    }

    /// <summary>
    /// JS-facing resolvedOptions method for compiled mode reflection dispatch.
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
            "format" => new BuiltInMethod("format", 1, (_, _, args) =>
            {
                DateTime dt = ToDateTime(args.Count > 0 ? args[0] : null);
                return FormatDate(dt);
            }),
            "resolvedOptions" => new BuiltInMethod("resolvedOptions", 0, (_, _, _) =>
            {
                return new SharpTSObject(GetResolvedOptions());
            }),
            _ => null
        };
    }

    public override string ToString() => "[object Intl.DateTimeFormat]";
}
