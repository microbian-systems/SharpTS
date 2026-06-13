using SharpTS.Execution;
using SharpTS.Runtime;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Provides implementations for JavaScript Date object members.
/// Includes static methods (Date.now()) and instance methods (date.getFullYear(), date.setMonth()).
/// </summary>
public static class DateBuiltIns
{
    // Static method lookup for Date namespace
    private static readonly BuiltInStaticMemberLookup _staticLookup =
        BuiltInStaticBuilder.Create()
            .MethodV2("now", 0, (_, _, _) => RuntimeValue.FromNumber(SharpTSDate.Now()))
            .Build();

    // Instance method lookup for Date instances
    private static readonly BuiltInTypeMemberLookup<SharpTSDate> _instanceLookup =
        BuiltInTypeBuilder<SharpTSDate>.ForInstanceType()
            // Getter Methods
            .MethodV2("getTime", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetTime()))
            .MethodV2("getFullYear", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetFullYear()))
            .MethodV2("getMonth", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetMonth()))
            .MethodV2("getDate", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetDate()))
            .MethodV2("getDay", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetDay()))
            .MethodV2("getHours", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetHours()))
            .MethodV2("getMinutes", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetMinutes()))
            .MethodV2("getSeconds", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetSeconds()))
            .MethodV2("getMilliseconds", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetMilliseconds()))
            .MethodV2("getTimezoneOffset", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetTimezoneOffset()))
            // UTC Getter Methods
            .MethodV2("getUTCFullYear", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetUTCFullYear()))
            .MethodV2("getUTCMonth", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetUTCMonth()))
            .MethodV2("getUTCDate", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetUTCDate()))
            .MethodV2("getUTCDay", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetUTCDay()))
            .MethodV2("getUTCHours", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetUTCHours()))
            .MethodV2("getUTCMinutes", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetUTCMinutes()))
            .MethodV2("getUTCSeconds", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetUTCSeconds()))
            .MethodV2("getUTCMilliseconds", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetUTCMilliseconds()))
            // Setter Methods
            .MethodV2("setTime", 1, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetTime(args[0].AsNumber())))
            .MethodV2("setFullYear", 1, 3, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetFullYear(
                    args[0].AsNumber(),
                    args.Length > 1 && args[1].Kind != ValueKind.Undefined ? (double?)args[1].AsNumber() : null,
                    args.Length > 2 && args[2].Kind != ValueKind.Undefined ? (double?)args[2].AsNumber() : null)))
            .MethodV2("setMonth", 1, 2, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetMonth(
                    args[0].AsNumber(),
                    args.Length > 1 && args[1].Kind != ValueKind.Undefined ? (double?)args[1].AsNumber() : null)))
            .MethodV2("setDate", 1, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetDate(args[0].AsNumber())))
            .MethodV2("setHours", 1, 4, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetHours(
                    args[0].AsNumber(),
                    args.Length > 1 && args[1].Kind != ValueKind.Undefined ? (double?)args[1].AsNumber() : null,
                    args.Length > 2 && args[2].Kind != ValueKind.Undefined ? (double?)args[2].AsNumber() : null,
                    args.Length > 3 && args[3].Kind != ValueKind.Undefined ? (double?)args[3].AsNumber() : null)))
            .MethodV2("setMinutes", 1, 3, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetMinutes(
                    args[0].AsNumber(),
                    args.Length > 1 && args[1].Kind != ValueKind.Undefined ? (double?)args[1].AsNumber() : null,
                    args.Length > 2 && args[2].Kind != ValueKind.Undefined ? (double?)args[2].AsNumber() : null)))
            .MethodV2("setSeconds", 1, 2, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetSeconds(
                    args[0].AsNumber(),
                    args.Length > 1 && args[1].Kind != ValueKind.Undefined ? (double?)args[1].AsNumber() : null)))
            .MethodV2("setMilliseconds", 1, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetMilliseconds(args[0].AsNumber())))
            // UTC Setter Methods
            .MethodV2("setUTCFullYear", 1, 3, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetUTCFullYear(
                    args[0].AsNumber(),
                    args.Length > 1 && args[1].Kind != ValueKind.Undefined ? (double?)args[1].AsNumber() : null,
                    args.Length > 2 && args[2].Kind != ValueKind.Undefined ? (double?)args[2].AsNumber() : null)))
            .MethodV2("setUTCMonth", 1, 2, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetUTCMonth(
                    args[0].AsNumber(),
                    args.Length > 1 && args[1].Kind != ValueKind.Undefined ? (double?)args[1].AsNumber() : null)))
            .MethodV2("setUTCDate", 1, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetUTCDate(args[0].AsNumber())))
            .MethodV2("setUTCHours", 1, 4, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetUTCHours(
                    args[0].AsNumber(),
                    args.Length > 1 && args[1].Kind != ValueKind.Undefined ? (double?)args[1].AsNumber() : null,
                    args.Length > 2 && args[2].Kind != ValueKind.Undefined ? (double?)args[2].AsNumber() : null,
                    args.Length > 3 && args[3].Kind != ValueKind.Undefined ? (double?)args[3].AsNumber() : null)))
            .MethodV2("setUTCMinutes", 1, 3, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetUTCMinutes(
                    args[0].AsNumber(),
                    args.Length > 1 && args[1].Kind != ValueKind.Undefined ? (double?)args[1].AsNumber() : null,
                    args.Length > 2 && args[2].Kind != ValueKind.Undefined ? (double?)args[2].AsNumber() : null)))
            .MethodV2("setUTCSeconds", 1, 2, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetUTCSeconds(
                    args[0].AsNumber(),
                    args.Length > 1 && args[1].Kind != ValueKind.Undefined ? (double?)args[1].AsNumber() : null)))
            .MethodV2("setUTCMilliseconds", 1, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetUTCMilliseconds(args[0].AsNumber())))
            // Conversion Methods
            .MethodV2("toString", 0, (_, date, _) => RuntimeValue.FromString(date.ToString()!))
            .MethodV2("toISOString", 0, (_, date, _) => RuntimeValue.FromString(date.ToISOString()))
            .MethodV2("toDateString", 0, (_, date, _) => RuntimeValue.FromString(date.ToDateString()))
            .MethodV2("toTimeString", 0, (_, date, _) => RuntimeValue.FromString(date.ToTimeString()))
            .MethodV2("toUTCString", 0, (_, date, _) => RuntimeValue.FromString(date.ToUTCString()))
            // toLocale* accept optional (locales, options) per lib.es2020.date; the runtime
            // ignores them (formats with the host culture) — see SharpTSDate for rationale.
            .MethodV2("toLocaleDateString", 0, 2, (_, date, _) => RuntimeValue.FromString(date.ToLocaleDateString()))
            .MethodV2("toLocaleTimeString", 0, 2, (_, date, _) => RuntimeValue.FromString(date.ToLocaleTimeString()))
            .MethodV2("toLocaleString", 0, 2, (_, date, _) => RuntimeValue.FromString(date.ToLocaleString()))
            // ECMA-262 §21.4.4.37: toJSON returns the ISO string, or null for a non-finite
            // (Invalid) date — guard so we never reach ToISOString's RangeError throw.
            .MethodV2("toJSON", 0, (_, date, _) => double.IsNaN(date.GetTime())
                ? RuntimeValue.Null
                : RuntimeValue.FromString(date.ToISOString()))
            .MethodV2("valueOf", 0, (_, date, _) => RuntimeValue.FromNumber(date.ValueOf()))
            // Legacy methods (ECMA-262 Annex B)
            .MethodV2("getYear", 0, (_, date, _) => RuntimeValue.FromNumber(date.GetYear()))
            .MethodV2("setYear", 1, (_, date, args) =>
                RuntimeValue.FromNumber(date.SetYear(args[0].AsNumber())))
            .Build();

    /// <summary>
    /// Gets a static member (method) from the Date namespace.
    /// </summary>
    public static BuiltInMethod? GetStaticMethod(string name)
        => _staticLookup.GetMember(name) as BuiltInMethod;

    /// <summary>
    /// Gets an instance member (method) for a Date object.
    /// </summary>
    public static object? GetMember(SharpTSDate receiver, string name)
        => _instanceLookup.GetMember(receiver, name);
}
