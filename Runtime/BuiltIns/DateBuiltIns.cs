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
            // Conversion Methods
            .MethodV2("toString", 0, (_, date, _) => RuntimeValue.FromString(date.ToString()!))
            .MethodV2("toISOString", 0, (_, date, _) => RuntimeValue.FromString(date.ToISOString()))
            .MethodV2("toDateString", 0, (_, date, _) => RuntimeValue.FromString(date.ToDateString()))
            .MethodV2("toTimeString", 0, (_, date, _) => RuntimeValue.FromString(date.ToTimeString()))
            .MethodV2("valueOf", 0, (_, date, _) => RuntimeValue.FromNumber(date.ValueOf()))
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
