using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for Intl.DateTimeFormat API.
/// Tests run against both interpreter and compiler modes.
/// Uses en-US locale and a fixed date (Jan 15, 2024, 2:30:45 PM) for deterministic output.
/// </summary>
public class IntlDateTimeFormatTests
{
    // ========== Basic Date Formatting ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_DateStyleShort(ExecutionMode mode)
    {
        var source = @"
            const d = new Date(2024, 0, 15, 14, 30, 45);
            const dtf = new Intl.DateTimeFormat(""en-US"", {dateStyle: ""short""});
            console.log(dtf.format(d));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Contains("1/15/2024", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_DateStyleLong(ExecutionMode mode)
    {
        var source = @"
            const d = new Date(2024, 0, 15, 14, 30, 45);
            const dtf = new Intl.DateTimeFormat(""en-US"", {dateStyle: ""long""});
            console.log(dtf.format(d));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Contains("January", output);
        Assert.Contains("15", output);
        Assert.Contains("2024", output);
    }

    // ========== Time Formatting ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_TimeStyleShort(ExecutionMode mode)
    {
        var source = @"
            const d = new Date(2024, 0, 15, 14, 30, 45);
            const dtf = new Intl.DateTimeFormat(""en-US"", {timeStyle: ""short""});
            console.log(dtf.format(d));
        ";
        var output = TestHarness.Run(source, mode);
        // Should contain time portion (2:30 PM or 14:30)
        Assert.Contains("30", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_TimeStyleLong(ExecutionMode mode)
    {
        var source = @"
            const d = new Date(2024, 0, 15, 14, 30, 45);
            const dtf = new Intl.DateTimeFormat(""en-US"", {timeStyle: ""long""});
            console.log(dtf.format(d));
        ";
        var output = TestHarness.Run(source, mode);
        // Should contain seconds
        Assert.Contains("45", output);
    }

    // ========== Combined Date + Time ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_DateAndTimeStyle(ExecutionMode mode)
    {
        var source = @"
            const d = new Date(2024, 0, 15, 14, 30, 45);
            const dtf = new Intl.DateTimeFormat(""en-US"", {dateStyle: ""short"", timeStyle: ""short""});
            console.log(dtf.format(d));
        ";
        var output = TestHarness.Run(source, mode);
        // Should contain both date and time
        Assert.Contains("1/15/2024", output);
        Assert.Contains("30", output);
    }

    // ========== Individual Component Options ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_YearMonthDay(ExecutionMode mode)
    {
        var source = @"
            const d = new Date(2024, 0, 15, 14, 30, 45);
            const dtf = new Intl.DateTimeFormat(""en-US"", {year: ""numeric"", month: ""long"", day: ""numeric""});
            console.log(dtf.format(d));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Contains("2024", output);
        Assert.Contains("January", output);
        Assert.Contains("15", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_TwoDigitYear(ExecutionMode mode)
    {
        var source = @"
            const d = new Date(2024, 0, 15, 14, 30, 45);
            const dtf = new Intl.DateTimeFormat(""en-US"", {year: ""2-digit"", month: ""2-digit"", day: ""2-digit""});
            console.log(dtf.format(d));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Contains("24", output);
        Assert.Contains("01", output);
        Assert.Contains("15", output);
    }

    // ========== Weekday ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_WeekdayLong(ExecutionMode mode)
    {
        var source = @"
            const d = new Date(2024, 0, 15, 14, 30, 45);
            const dtf = new Intl.DateTimeFormat(""en-US"", {weekday: ""long""});
            console.log(dtf.format(d));
        ";
        var output = TestHarness.Run(source, mode);
        // Jan 15, 2024 is a Monday
        Assert.Contains("Monday", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_WeekdayShort(ExecutionMode mode)
    {
        var source = @"
            const d = new Date(2024, 0, 15, 14, 30, 45);
            const dtf = new Intl.DateTimeFormat(""en-US"", {weekday: ""short""});
            console.log(dtf.format(d));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Contains("Mon", output);
    }

    // ========== Hour12 ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_Hour12(ExecutionMode mode)
    {
        var source = @"
            const d = new Date(2024, 0, 15, 14, 30, 45);
            const dtf = new Intl.DateTimeFormat(""en-US"", {hour: ""numeric"", minute: ""2-digit"", hour12: true});
            console.log(dtf.format(d));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Contains("2", output);
        Assert.Contains("30", output);
        Assert.Contains("PM", output);
    }

    // ========== Resolved Options ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_ResolvedOptions(ExecutionMode mode)
    {
        var source = @"
            const dtf = new Intl.DateTimeFormat(""en-US"", {dateStyle: ""short""});
            const opts = dtf.resolvedOptions();
            console.log(opts.dateStyle);
            console.log(opts.calendar);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("short\ngregory\n", output);
    }

    // ========== Default (No Arguments) ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_DefaultNoArgs(ExecutionMode mode)
    {
        var source = @"
            const d = new Date(2024, 0, 15, 14, 30, 45);
            const dtf = new Intl.DateTimeFormat();
            const result = dtf.format(d);
            console.log(typeof result);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("string\n", output);
    }

    // ========== No-argument constructor ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_LocaleOnly(ExecutionMode mode)
    {
        var source = @"
            const d = new Date(2024, 0, 15, 14, 30, 45);
            const dtf = new Intl.DateTimeFormat(""en-US"");
            const result = dtf.format(d);
            console.log(typeof result);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("string\n", output);
    }

    // ========== Month Short ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IntlDateTimeFormat_MonthShort(ExecutionMode mode)
    {
        var source = @"
            const d = new Date(2024, 0, 15, 14, 30, 45);
            const dtf = new Intl.DateTimeFormat(""en-US"", {month: ""short"", day: ""numeric"", year: ""numeric""});
            console.log(dtf.format(d));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Contains("Jan", output);
        Assert.Contains("15", output);
        Assert.Contains("2024", output);
    }
}
