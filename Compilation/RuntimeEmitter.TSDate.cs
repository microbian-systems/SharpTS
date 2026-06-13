using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits the $TSDate class for standalone Date support.
/// NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSDate
/// </summary>
public partial class RuntimeEmitter
{
    private FieldBuilder _tsDateUtcDateTimeField = null!;
    private FieldBuilder _tsDateIsInvalidField = null!;
    private FieldBuilder _tsDateUnixEpochField = null!;
    private MethodBuilder _tsDateGetTimeMethod = null!;

    private void EmitTSDateClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        // Define class: public sealed class $TSDate
        var typeBuilder = moduleBuilder.DefineType(
            "$TSDate",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object
        );
        runtime.TSDateType = typeBuilder;

        // Fields
        _tsDateUtcDateTimeField = typeBuilder.DefineField("_utcDateTime", _types.DateTime, FieldAttributes.Private);
        _tsDateIsInvalidField = typeBuilder.DefineField("_isInvalid", _types.Boolean, FieldAttributes.Private);
        _tsDateUnixEpochField = typeBuilder.DefineField("UnixEpoch", _types.DateTime,
            FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);

        // Static constructor to initialize UnixEpoch
        EmitTSDateStaticConstructor(typeBuilder);

        // Constructors
        EmitTSDateCtorNoArgs(typeBuilder, runtime);
        EmitTSDateCtorMilliseconds(typeBuilder, runtime);
        EmitTSDateCtorString(typeBuilder, runtime);
        EmitTSDateCtorComponents(typeBuilder, runtime);

        // Static Now method
        EmitTSDateNowStatic(typeBuilder, runtime);

        // Instance getter methods
        EmitTSDateGetTime(typeBuilder, runtime);
        EmitTSDateGetFullYear(typeBuilder, runtime);
        EmitSimpleDateGetter(typeBuilder, runtime, "GetMonth", "Month", subtractAfter: 1);
        EmitTSDateGetDate(typeBuilder, runtime);
        EmitSimpleDateGetter(typeBuilder, runtime, "GetDay", "DayOfWeek");
        EmitTSDateGetHours(typeBuilder, runtime);
        EmitTSDateGetMinutes(typeBuilder, runtime);
        EmitTSDateGetSeconds(typeBuilder, runtime);
        EmitTSDateGetMilliseconds(typeBuilder, runtime);
        EmitTSDateGetTimezoneOffset(typeBuilder, runtime);

        // UTC getter methods (#516)
        EmitSimpleDateGetter(typeBuilder, runtime, "GetUTCFullYear", "Year", utc: true);
        EmitSimpleDateGetter(typeBuilder, runtime, "GetUTCMonth", "Month", utc: true, subtractAfter: 1);
        EmitSimpleDateGetter(typeBuilder, runtime, "GetUTCDate", "Day", utc: true);
        EmitSimpleDateGetter(typeBuilder, runtime, "GetUTCDay", "DayOfWeek", utc: true);
        EmitSimpleDateGetter(typeBuilder, runtime, "GetUTCHours", "Hour", utc: true);
        EmitSimpleDateGetter(typeBuilder, runtime, "GetUTCMinutes", "Minute", utc: true);
        EmitSimpleDateGetter(typeBuilder, runtime, "GetUTCSeconds", "Second", utc: true);
        EmitSimpleDateGetter(typeBuilder, runtime, "GetUTCMilliseconds", "Millisecond", utc: true);
        // Legacy getYear: local-time year minus 1900 (Annex B, #516)
        EmitSimpleDateGetter(typeBuilder, runtime, "GetYear", "Year", subtractAfter: 1900);

        // Instance setter methods (all route through the shared component setter)
        EmitTSDateSetTime(typeBuilder, runtime);
        EmitDateComponentSetter(typeBuilder, runtime, "SetFullYear", DateComponent.Year, utc: false);
        EmitDateComponentSetter(typeBuilder, runtime, "SetMonth", DateComponent.Month, utc: false);
        EmitDateComponentSetter(typeBuilder, runtime, "SetDate", DateComponent.Day, utc: false);
        EmitDateComponentSetter(typeBuilder, runtime, "SetHours", DateComponent.Hour, utc: false);
        EmitDateComponentSetter(typeBuilder, runtime, "SetMinutes", DateComponent.Minute, utc: false);
        EmitDateComponentSetter(typeBuilder, runtime, "SetSeconds", DateComponent.Second, utc: false);
        EmitDateComponentSetter(typeBuilder, runtime, "SetMilliseconds", DateComponent.Millisecond, utc: false);

        // UTC setter methods (#516)
        EmitDateComponentSetter(typeBuilder, runtime, "SetUTCFullYear", DateComponent.Year, utc: true);
        EmitDateComponentSetter(typeBuilder, runtime, "SetUTCMonth", DateComponent.Month, utc: true);
        EmitDateComponentSetter(typeBuilder, runtime, "SetUTCDate", DateComponent.Day, utc: true);
        EmitDateComponentSetter(typeBuilder, runtime, "SetUTCHours", DateComponent.Hour, utc: true);
        EmitDateComponentSetter(typeBuilder, runtime, "SetUTCMinutes", DateComponent.Minute, utc: true);
        EmitDateComponentSetter(typeBuilder, runtime, "SetUTCSeconds", DateComponent.Second, utc: true);
        EmitDateComponentSetter(typeBuilder, runtime, "SetUTCMilliseconds", DateComponent.Millisecond, utc: true);
        // Legacy setYear (Annex B, #516) — emitted after SetFullYear, which it delegates to.
        EmitTSDateSetYear(typeBuilder, runtime);

        // Conversion methods
        EmitTSDateToString(typeBuilder, runtime);
        EmitTSDateToISOString(typeBuilder, runtime);
        EmitTSDateToDateString(typeBuilder, runtime);
        EmitTSDateToTimeString(typeBuilder, runtime);
        EmitTSDateToUTCString(typeBuilder, runtime);
        // toLocale* format in local time using the host's current culture (#516)
        EmitTSDateLocaleString(typeBuilder, runtime, "ToLocaleDateString", "d");
        EmitTSDateLocaleString(typeBuilder, runtime, "ToLocaleTimeString", "T");
        EmitTSDateLocaleString(typeBuilder, runtime, "ToLocaleString", "G");
        EmitTSDateValueOf(typeBuilder, runtime);

        typeBuilder.CreateType();
    }

    private void EmitTSDateStaticConstructor(TypeBuilder typeBuilder)
    {
        var cctor = typeBuilder.DefineConstructor(
            MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            Type.EmptyTypes
        );
        var il = cctor.GetILGenerator();

        // UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        il.Emit(OpCodes.Ldc_I4, 1970);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_1); // DateTimeKind.Utc
        il.Emit(OpCodes.Newobj, _types.DateTime.GetConstructor([
            _types.Int32, _types.Int32, _types.Int32, _types.Int32, _types.Int32, _types.Int32, _types.DateTimeKind
        ])!);
        il.Emit(OpCodes.Stsfld, _tsDateUnixEpochField);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateCtorNoArgs(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public $TSDate() { _utcDateTime = DateTime.UtcNow; _isInvalid = false; }
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes
        );
        runtime.TSDateCtorNoArgs = ctor;

        var il = ctor.GetILGenerator();

        // Call base constructor
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.Object.GetConstructor(Type.EmptyTypes)!);

        // _utcDateTime = DateTime.UtcNow
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.DateTime.GetProperty("UtcNow")!.GetGetMethod()!);
        il.Emit(OpCodes.Stfld, _tsDateUtcDateTimeField);

        // _isInvalid = false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsDateIsInvalidField);

        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateCtorMilliseconds(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public $TSDate(double milliseconds)
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.Double]
        );
        runtime.TSDateCtorMilliseconds = ctor;

        var il = ctor.GetILGenerator();
        var invalidLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // Call base constructor
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.Object.GetConstructor(Type.EmptyTypes)!);

        // Check for NaN or Infinity
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.Double.GetMethod("IsNaN")!);
        il.Emit(OpCodes.Brtrue, invalidLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.Double.GetMethod("IsInfinity")!);
        il.Emit(OpCodes.Brtrue, invalidLabel);

        // Check range: -8640000000000000 to 8640000000000000
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_R8, -8640000000000000.0);
        il.Emit(OpCodes.Blt, invalidLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_R8, 8640000000000000.0);
        il.Emit(OpCodes.Bgt, invalidLabel);

        // _utcDateTime = UnixEpoch.AddMilliseconds(milliseconds)
        // For value type instance methods, we need the address of the struct
        var epochLocal = il.DeclareLocal(_types.DateTime);
        il.Emit(OpCodes.Ldsfld, _tsDateUnixEpochField);
        il.Emit(OpCodes.Stloc, epochLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, epochLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddMilliseconds")!);
        il.Emit(OpCodes.Stfld, _tsDateUtcDateTimeField);

        // _isInvalid = false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Br, endLabel);

        // invalidLabel: _isInvalid = true
        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tsDateIsInvalidField);

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateCtorString(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public $TSDate(string isoString) - simplified: try parse, if fail set invalid
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.String]
        );
        runtime.TSDateCtorString = ctor;

        var il = ctor.GetILGenerator();
        var invalidLabel = il.DefineLabel();
        var validLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();
        var resultLocal = il.DeclareLocal(_types.DateTime);

        // Call base constructor
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.Object.GetConstructor(Type.EmptyTypes)!);

        // if (string.IsNullOrWhiteSpace(isoString)) goto invalid
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrWhiteSpace")!);
        il.Emit(OpCodes.Brtrue, invalidLabel);

        // Try DateTime.TryParse with RoundtripKind
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, typeof(CultureInfo).GetProperty("InvariantCulture")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4, (int)(DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces));
        il.Emit(OpCodes.Ldloca, resultLocal);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("TryParse", [_types.String, typeof(IFormatProvider), typeof(DateTimeStyles), _types.DateTime.MakeByRefType()])!);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        // Convert to UTC if needed: result.Kind == Utc ? result : result.ToUniversalTime()
        var notUtcLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloca, resultLocal);
        il.Emit(OpCodes.Call, _types.DateTime.GetProperty("Kind")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_1); // DateTimeKind.Utc
        il.Emit(OpCodes.Bne_Un, notUtcLabel);

        // Already UTC - store directly
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Stfld, _tsDateUtcDateTimeField);
        il.Emit(OpCodes.Br, validLabel);

        // Not UTC - convert to UTC
        il.MarkLabel(notUtcLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, resultLocal);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToUniversalTime")!);
        il.Emit(OpCodes.Stfld, _tsDateUtcDateTimeField);

        // _isInvalid = false
        il.MarkLabel(validLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Br, endLabel);

        // invalidLabel: _isInvalid = true
        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tsDateIsInvalidField);

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateCtorComponents(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public $TSDate(int year, int month, int day, int hours, int minutes, int seconds, int milliseconds)
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.Int32, _types.Int32, _types.Int32, _types.Int32, _types.Int32, _types.Int32, _types.Int32]
        );
        runtime.TSDateCtorComponents = ctor;

        var il = ctor.GetILGenerator();
        var endLabel = il.DefineLabel();
        var yearLocal = il.DeclareLocal(_types.Int32);
        var baseDateLocal = il.DeclareLocal(_types.DateTime);
        var localDateTimeLocal = il.DeclareLocal(_types.DateTime);

        // Call base constructor
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.Object.GetConstructor(Type.EmptyTypes)!);

        // Begin try block
        il.BeginExceptionBlock();

        // JavaScript quirk: 2-digit years (0-99) map to 1900-1999
        // if (year >= 0 && year <= 99) year += 1900;
        var skipYearAdjustLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stloc, yearLocal);

        il.Emit(OpCodes.Ldloc, yearLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, skipYearAdjustLabel); // Skip if < 0
        il.Emit(OpCodes.Ldloc, yearLocal);
        il.Emit(OpCodes.Ldc_I4_S, (sbyte)99);
        il.Emit(OpCodes.Bgt, skipYearAdjustLabel); // Skip if > 99

        // Within 0-99 range, add 1900
        il.Emit(OpCodes.Ldloc, yearLocal);
        il.Emit(OpCodes.Ldc_I4, 1900);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, yearLocal);

        il.MarkLabel(skipYearAdjustLabel);

        // baseDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Local)
        il.Emit(OpCodes.Ldloc, yearLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_2); // DateTimeKind.Local
        il.Emit(OpCodes.Newobj, _types.DateTime.GetConstructor([
            _types.Int32, _types.Int32, _types.Int32, _types.Int32, _types.Int32, _types.Int32, _types.DateTimeKind
        ])!);
        il.Emit(OpCodes.Stloc, baseDateLocal);

        // localDateTime = baseDate.AddMonths(month).AddDays(day-1).AddHours(hours).AddMinutes(minutes).AddSeconds(seconds).AddMilliseconds(milliseconds)
        il.Emit(OpCodes.Ldloca, baseDateLocal);
        il.Emit(OpCodes.Ldarg_2); // month
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddMonths")!);
        il.Emit(OpCodes.Stloc, localDateTimeLocal);

        il.Emit(OpCodes.Ldloca, localDateTimeLocal);
        il.Emit(OpCodes.Ldarg_3); // day
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddDays")!);
        il.Emit(OpCodes.Stloc, localDateTimeLocal);

        il.Emit(OpCodes.Ldloca, localDateTimeLocal);
        il.Emit(OpCodes.Ldarg_S, (byte)4); // hours
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddHours")!);
        il.Emit(OpCodes.Stloc, localDateTimeLocal);

        il.Emit(OpCodes.Ldloca, localDateTimeLocal);
        il.Emit(OpCodes.Ldarg_S, (byte)5); // minutes
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddMinutes")!);
        il.Emit(OpCodes.Stloc, localDateTimeLocal);

        il.Emit(OpCodes.Ldloca, localDateTimeLocal);
        il.Emit(OpCodes.Ldarg_S, (byte)6); // seconds
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddSeconds")!);
        il.Emit(OpCodes.Stloc, localDateTimeLocal);

        il.Emit(OpCodes.Ldloca, localDateTimeLocal);
        il.Emit(OpCodes.Ldarg_S, (byte)7); // milliseconds
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddMilliseconds")!);
        il.Emit(OpCodes.Stloc, localDateTimeLocal);

        // _utcDateTime = localDateTime.ToUniversalTime()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, localDateTimeLocal);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToUniversalTime")!);
        il.Emit(OpCodes.Stfld, _tsDateUtcDateTimeField);

        // _isInvalid = false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Leave, endLabel);

        // Catch block
        il.BeginCatchBlock(_types.Exception);
        il.Emit(OpCodes.Pop); // Discard exception
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Leave, endLabel);

        il.EndExceptionBlock();

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateNowStatic(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public static double Now() => (DateTime.UtcNow - UnixEpoch).TotalMilliseconds
        var method = typeBuilder.DefineMethod(
            "Now",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            Type.EmptyTypes
        );
        runtime.TSDateNowStatic = method;

        var il = method.GetILGenerator();
        var utcNowLocal = il.DeclareLocal(_types.DateTime);
        var unixEpochLocal = il.DeclareLocal(_types.DateTime);

        // DateTime.UtcNow
        il.Emit(OpCodes.Call, _types.DateTime.GetProperty("UtcNow")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, utcNowLocal);

        // UnixEpoch
        il.Emit(OpCodes.Ldsfld, _tsDateUnixEpochField);
        il.Emit(OpCodes.Stloc, unixEpochLocal);

        // Subtract: utcNow - unixEpoch
        il.Emit(OpCodes.Ldloc, utcNowLocal);
        il.Emit(OpCodes.Ldloc, unixEpochLocal);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("op_Subtraction", [_types.DateTime, _types.DateTime])!);

        // .TotalMilliseconds
        var timeSpanLocal = il.DeclareLocal(_types.TimeSpan);
        il.Emit(OpCodes.Stloc, timeSpanLocal);
        il.Emit(OpCodes.Ldloca, timeSpanLocal);
        il.Emit(OpCodes.Call, _types.TimeSpan.GetProperty("TotalMilliseconds")!.GetGetMethod()!);

        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateGetTime(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public double GetTime() { if (_isInvalid) return NaN; return (_utcDateTime - UnixEpoch).TotalMilliseconds; }
        var method = typeBuilder.DefineMethod(
            "GetTime",
            MethodAttributes.Public,
            _types.Double,
            Type.EmptyTypes
        );
        _tsDateGetTimeMethod = method; // Save for later use
        runtime.TSDateMethods["GetTime"] = method;

        var il = method.GetILGenerator();
        var validLabel = il.DefineLabel();

        // if (_isInvalid) return NaN
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Brfalse, validLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(validLabel);
        // (_utcDateTime - UnixEpoch).TotalMilliseconds
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsDateUtcDateTimeField);
        il.Emit(OpCodes.Ldsfld, _tsDateUnixEpochField);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("op_Subtraction", [_types.DateTime, _types.DateTime])!);
        var tsLocal = il.DeclareLocal(_types.TimeSpan);
        il.Emit(OpCodes.Stloc, tsLocal);
        il.Emit(OpCodes.Ldloca, tsLocal);
        il.Emit(OpCodes.Call, _types.TimeSpan.GetProperty("TotalMilliseconds")!.GetGetMethod()!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateGetFullYear(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitSimpleDateGetter(typeBuilder, runtime, "GetFullYear", "Year");
    }

    private void EmitTSDateGetDate(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitSimpleDateGetter(typeBuilder, runtime, "GetDate", "Day");
    }

    private void EmitTSDateGetHours(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitSimpleDateGetter(typeBuilder, runtime, "GetHours", "Hour");
    }

    private void EmitTSDateGetMinutes(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitSimpleDateGetter(typeBuilder, runtime, "GetMinutes", "Minute");
    }

    private void EmitTSDateGetSeconds(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitSimpleDateGetter(typeBuilder, runtime, "GetSeconds", "Second");
    }

    private void EmitTSDateGetMilliseconds(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitSimpleDateGetter(typeBuilder, runtime, "GetMilliseconds", "Millisecond");
    }

    /// <summary>
    /// Emits a zero-argument $TSDate getter returning a DateTime component as a double.
    /// When <paramref name="utc"/> is true the stored UTC instant is read directly; otherwise
    /// it is converted to local time first. <paramref name="subtractAfter"/> offsets the result
    /// (e.g. 1 for the 0-indexed month, 1900 for the Annex B getYear).
    /// </summary>
    private void EmitSimpleDateGetter(TypeBuilder typeBuilder, EmittedRuntime runtime, string methodName, string propertyName, bool utc = false, int subtractAfter = 0)
    {
        var method = typeBuilder.DefineMethod(
            methodName,
            MethodAttributes.Public,
            _types.Double,
            Type.EmptyTypes
        );
        runtime.TSDateMethods[methodName] = method;

        var il = method.GetILGenerator();
        var validLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Brfalse, validLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(validLabel);
        if (utc)
        {
            // Read the UTC instant directly; the property getter is called on the field address.
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldflda, _tsDateUtcDateTimeField);
        }
        else
        {
            var localTimeLocal = il.DeclareLocal(_types.DateTime);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldflda, _tsDateUtcDateTimeField);
            il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToLocalTime")!);
            il.Emit(OpCodes.Stloc, localTimeLocal);
            il.Emit(OpCodes.Ldloca, localTimeLocal);
        }
        il.Emit(OpCodes.Call, _types.DateTime.GetProperty(propertyName)!.GetGetMethod()!);
        if (subtractAfter != 0)
        {
            il.Emit(OpCodes.Ldc_I4, subtractAfter);
            il.Emit(OpCodes.Sub);
        }
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateGetTimezoneOffset(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetTimezoneOffset",
            MethodAttributes.Public,
            _types.Double,
            Type.EmptyTypes
        );
        runtime.TSDateMethods["GetTimezoneOffset"] = method;

        var il = method.GetILGenerator();
        var validLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Brfalse, validLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(validLabel);
        // -TimeZoneInfo.Local.GetUtcOffset(_utcDateTime).TotalMinutes
        il.Emit(OpCodes.Call, typeof(TimeZoneInfo).GetProperty("Local")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsDateUtcDateTimeField);
        il.Emit(OpCodes.Callvirt, typeof(TimeZoneInfo).GetMethod("GetUtcOffset", [_types.DateTime])!);
        var tsLocal = il.DeclareLocal(_types.TimeSpan);
        il.Emit(OpCodes.Stloc, tsLocal);
        il.Emit(OpCodes.Ldloca, tsLocal);
        il.Emit(OpCodes.Call, _types.TimeSpan.GetProperty("TotalMinutes")!.GetGetMethod()!);
        il.Emit(OpCodes.Neg);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateSetTime(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public double SetTime(double time) - sets from epoch ms, returns new timestamp
        var method = typeBuilder.DefineMethod(
            "SetTime",
            MethodAttributes.Public,
            _types.Double,
            [_types.Double]
        );
        runtime.TSDateMethods["SetTime"] = method;

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();
        var validLabel = il.DefineLabel();

        // Check for NaN/Infinity/out of range
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.Double.GetMethod("IsNaN")!);
        il.Emit(OpCodes.Brtrue, invalidLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.Double.GetMethod("IsInfinity")!);
        il.Emit(OpCodes.Brtrue, invalidLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_R8, -8640000000000000.0);
        il.Emit(OpCodes.Blt, invalidLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_R8, 8640000000000000.0);
        il.Emit(OpCodes.Bgt, invalidLabel);

        // Valid - set time
        // For value type instance methods, we need the address of the struct
        var epochLocal = il.DeclareLocal(_types.DateTime);
        il.Emit(OpCodes.Ldsfld, _tsDateUnixEpochField);
        il.Emit(OpCodes.Stloc, epochLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, epochLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddMilliseconds")!);
        il.Emit(OpCodes.Stfld, _tsDateUtcDateTimeField);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Br, validLabel);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tsDateIsInvalidField);

        il.MarkLabel(validLabel);
        // Call GetTime() to return result
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _tsDateGetTimeMethod);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>Identifies which component a <see cref="EmitDateComponentSetter"/> replaces.</summary>
    private enum DateComponent { Year, Month, Day, Hour, Minute, Second, Millisecond }

    /// <summary>
    /// Emits a $TSDate setter that replaces a single date component (keeping the others) and
    /// returns the new timestamp. The instant is rebuilt with DateTime.Add* from a normalized
    /// base so overflowing components roll over per JavaScript semantics (e.g. setMonth(13)
    /// advances the year); an out-of-range result (e.g. a year beyond DateTime's domain) is
    /// caught and marks the date Invalid, mirroring <see cref="Runtime.Types.SharpTSDate"/>.
    /// When <paramref name="utc"/> is true the instant is read and written directly in UTC;
    /// otherwise it round-trips through local time. Only the primary argument is honored —
    /// optional trailing arguments are dropped in compiled mode (tracked in #536).
    /// </summary>
    private void EmitDateComponentSetter(TypeBuilder typeBuilder, EmittedRuntime runtime, string methodName, DateComponent component, bool utc)
    {
        var method = typeBuilder.DefineMethod(methodName, MethodAttributes.Public, _types.Double, [_types.Double]);
        runtime.TSDateMethods[methodName] = method;

        var il = method.GetILGenerator();
        var computeLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();
        var dtLocal = il.DeclareLocal(_types.DateTime);
        var cur = il.DeclareLocal(_types.DateTime);
        int kind = utc ? 1 : 2; // DateTimeKind.Utc = 1, Local = 2

        // if (_isInvalid) return GetTime(); // stays NaN
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Brfalse, computeLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _tsDateGetTimeMethod);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(computeLabel);
        // dt = utc ? _utcDateTime : _utcDateTime.ToLocalTime();
        if (utc)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, _tsDateUtcDateTimeField);
            il.Emit(OpCodes.Stloc, dtLocal);
        }
        else
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldflda, _tsDateUtcDateTimeField);
            il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToLocalTime")!);
            il.Emit(OpCodes.Stloc, dtLocal);
        }

        il.BeginExceptionBlock();

        // cur = new DateTime(year, 1, 1, 0, 0, 0, kind)
        PushDateComponentValue(il, dtLocal, component, DateComponent.Year, "Year", 0);
        il.Emit(OpCodes.Ldc_I4_1); // month
        il.Emit(OpCodes.Ldc_I4_1); // day
        il.Emit(OpCodes.Ldc_I4_0); // hour
        il.Emit(OpCodes.Ldc_I4_0); // minute
        il.Emit(OpCodes.Ldc_I4_0); // second
        il.Emit(OpCodes.Ldc_I4, kind);
        il.Emit(OpCodes.Newobj, _types.DateTime.GetConstructor([
            _types.Int32, _types.Int32, _types.Int32, _types.Int32, _types.Int32, _types.Int32, _types.DateTimeKind
        ])!);
        il.Emit(OpCodes.Stloc, cur);

        // cur = cur.AddMonths(month0)   (month0 = 0-indexed month to add)
        il.Emit(OpCodes.Ldloca, cur);
        PushDateComponentValue(il, dtLocal, component, DateComponent.Month, "Month", 1);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddMonths")!);
        il.Emit(OpCodes.Stloc, cur);

        // cur = cur.AddDays(day - 1)
        il.Emit(OpCodes.Ldloca, cur);
        PushDateComponentValue(il, dtLocal, component, DateComponent.Day, "Day", 0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddDays")!);
        il.Emit(OpCodes.Stloc, cur);

        // cur = cur.AddHours(hour)
        il.Emit(OpCodes.Ldloca, cur);
        PushDateComponentValue(il, dtLocal, component, DateComponent.Hour, "Hour", 0);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddHours")!);
        il.Emit(OpCodes.Stloc, cur);

        // cur = cur.AddMinutes(minute)
        il.Emit(OpCodes.Ldloca, cur);
        PushDateComponentValue(il, dtLocal, component, DateComponent.Minute, "Minute", 0);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddMinutes")!);
        il.Emit(OpCodes.Stloc, cur);

        // cur = cur.AddSeconds(second)
        il.Emit(OpCodes.Ldloca, cur);
        PushDateComponentValue(il, dtLocal, component, DateComponent.Second, "Second", 0);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddSeconds")!);
        il.Emit(OpCodes.Stloc, cur);

        // cur = cur.AddMilliseconds(millisecond)
        il.Emit(OpCodes.Ldloca, cur);
        PushDateComponentValue(il, dtLocal, component, DateComponent.Millisecond, "Millisecond", 0);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("AddMilliseconds")!);
        il.Emit(OpCodes.Stloc, cur);

        // _utcDateTime = utc ? cur : cur.ToUniversalTime();
        il.Emit(OpCodes.Ldarg_0);
        if (utc)
        {
            il.Emit(OpCodes.Ldloc, cur);
        }
        else
        {
            il.Emit(OpCodes.Ldloca, cur);
            il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToUniversalTime")!);
        }
        il.Emit(OpCodes.Stfld, _tsDateUtcDateTimeField);
        il.Emit(OpCodes.Leave, endLabel);

        // catch { _isInvalid = true; }
        il.BeginCatchBlock(_types.Exception);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Leave, endLabel);
        il.EndExceptionBlock();

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _tsDateGetTimeMethod);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Pushes the int value for one slot of the DateTime rebuilt by <see cref="EmitDateComponentSetter"/>:
    /// the setter argument (truncated toward zero, matching <c>(int)value</c> in SharpTSDate) when this
    /// slot is the one being set, otherwise the current component read from <paramref name="dtLocal"/>
    /// (minus <paramref name="subtract"/>, e.g. 1 to convert .NET's 1-indexed month to 0-indexed).
    /// </summary>
    private void PushDateComponentValue(ILGenerator il, LocalBuilder dtLocal, DateComponent target, DateComponent slot, string propertyName, int subtract)
    {
        if (target == slot)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Conv_I4);
        }
        else
        {
            il.Emit(OpCodes.Ldloca, dtLocal);
            il.Emit(OpCodes.Call, _types.DateTime.GetProperty(propertyName)!.GetGetMethod()!);
            if (subtract != 0)
            {
                il.Emit(OpCodes.Ldc_I4, subtract);
                il.Emit(OpCodes.Sub);
            }
        }
    }

    // ECMA-262 Annex B B.2.4.2: setYear maps 0-99 to 1900-1999, then delegates to SetFullYear.
    private void EmitTSDateSetYear(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("SetYear", MethodAttributes.Public, _types.Double, [_types.Double]);
        runtime.TSDateMethods["SetYear"] = method;

        var il = method.GetILGenerator();
        var computeLabel = il.DefineLabel();
        var skipMapLabel = il.DefineLabel();
        var yLocal = il.DeclareLocal(_types.Int32);

        // if (_isInvalid) return GetTime();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Brfalse, computeLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _tsDateGetTimeMethod);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(computeLabel);
        // int y = (int)year;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, yLocal);
        // if (y < 0 || y > 99) goto skipMap;
        il.Emit(OpCodes.Ldloc, yLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, skipMapLabel);
        il.Emit(OpCodes.Ldloc, yLocal);
        il.Emit(OpCodes.Ldc_I4, 99);
        il.Emit(OpCodes.Bgt, skipMapLabel);
        // y += 1900;
        il.Emit(OpCodes.Ldloc, yLocal);
        il.Emit(OpCodes.Ldc_I4, 1900);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, yLocal);

        il.MarkLabel(skipMapLabel);
        // return SetFullYear((double)y);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, yLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Call, runtime.TSDateMethods["SetFullYear"]);
        il.Emit(OpCodes.Ret);
    }

    // RFC 7231 UTC string, e.g. "Thu, 01 Jan 1970 00:00:00 GMT".
    private void EmitTSDateToUTCString(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("ToUTCString", MethodAttributes.Public, _types.String, Type.EmptyTypes);
        runtime.TSDateMethods["ToUTCString"] = method;

        var il = method.GetILGenerator();
        var validLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Brfalse, validLabel);
        il.Emit(OpCodes.Ldstr, "Invalid Date");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(validLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, _tsDateUtcDateTimeField);
        il.Emit(OpCodes.Ldstr, "ddd, dd MMM yyyy HH:mm:ss 'GMT'");
        il.Emit(OpCodes.Call, typeof(CultureInfo).GetProperty("InvariantCulture")!.GetGetMethod()!);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToString", [_types.String, typeof(IFormatProvider)])!);
        il.Emit(OpCodes.Ret);
    }

    // toLocale* family: format in local time using the host's current culture (#516).
    private void EmitTSDateLocaleString(TypeBuilder typeBuilder, EmittedRuntime runtime, string methodName, string format)
    {
        var method = typeBuilder.DefineMethod(methodName, MethodAttributes.Public, _types.String, Type.EmptyTypes);
        runtime.TSDateMethods[methodName] = method;

        var il = method.GetILGenerator();
        var validLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Brfalse, validLabel);
        il.Emit(OpCodes.Ldstr, "Invalid Date");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(validLabel);
        var localTimeLocal = il.DeclareLocal(_types.DateTime);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, _tsDateUtcDateTimeField);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToLocalTime")!);
        il.Emit(OpCodes.Stloc, localTimeLocal);
        il.Emit(OpCodes.Ldloca, localTimeLocal);
        il.Emit(OpCodes.Ldstr, format);
        il.Emit(OpCodes.Call, typeof(CultureInfo).GetProperty("CurrentCulture")!.GetGetMethod()!);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToString", [_types.String, typeof(IFormatProvider)])!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateToString(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ToString",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            _types.String,
            Type.EmptyTypes
        );
        runtime.TSDateMethods["ToString"] = method;

        var il = method.GetILGenerator();
        var validLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Brfalse, validLabel);
        il.Emit(OpCodes.Ldstr, "Invalid Date");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(validLabel);
        // Simple format: return local.ToString("ddd MMM dd yyyy HH:mm:ss") + offset
        var localTimeLocal = il.DeclareLocal(_types.DateTime);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, _tsDateUtcDateTimeField);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToLocalTime")!);
        il.Emit(OpCodes.Stloc, localTimeLocal);

        il.Emit(OpCodes.Ldloca, localTimeLocal);
        il.Emit(OpCodes.Ldstr, "ddd MMM dd yyyy HH:mm:ss");
        il.Emit(OpCodes.Call, typeof(CultureInfo).GetProperty("InvariantCulture")!.GetGetMethod()!);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToString", [_types.String, typeof(IFormatProvider)])!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateToISOString(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ToISOString",
            MethodAttributes.Public,
            _types.String,
            Type.EmptyTypes
        );
        runtime.TSDateMethods["ToISOString"] = method;

        var il = method.GetILGenerator();
        var validLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Brfalse, validLabel);
        il.Emit(OpCodes.Ldstr, "Runtime Error: Invalid Date");
        il.Emit(OpCodes.Newobj, _types.Exception.GetConstructor([_types.String])!);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(validLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, _tsDateUtcDateTimeField);
        il.Emit(OpCodes.Ldstr, "yyyy-MM-ddTHH:mm:ss.fffZ");
        il.Emit(OpCodes.Call, typeof(CultureInfo).GetProperty("InvariantCulture")!.GetGetMethod()!);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToString", [_types.String, typeof(IFormatProvider)])!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateToDateString(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ToDateString",
            MethodAttributes.Public,
            _types.String,
            Type.EmptyTypes
        );
        runtime.TSDateMethods["ToDateString"] = method;

        var il = method.GetILGenerator();
        var validLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Brfalse, validLabel);
        il.Emit(OpCodes.Ldstr, "Invalid Date");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(validLabel);
        var localTimeLocal = il.DeclareLocal(_types.DateTime);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, _tsDateUtcDateTimeField);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToLocalTime")!);
        il.Emit(OpCodes.Stloc, localTimeLocal);

        il.Emit(OpCodes.Ldloca, localTimeLocal);
        il.Emit(OpCodes.Ldstr, "ddd MMM dd yyyy");
        il.Emit(OpCodes.Call, typeof(CultureInfo).GetProperty("InvariantCulture")!.GetGetMethod()!);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToString", [_types.String, typeof(IFormatProvider)])!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateToTimeString(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ToTimeString",
            MethodAttributes.Public,
            _types.String,
            Type.EmptyTypes
        );
        runtime.TSDateMethods["ToTimeString"] = method;

        var il = method.GetILGenerator();
        var validLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsDateIsInvalidField);
        il.Emit(OpCodes.Brfalse, validLabel);
        il.Emit(OpCodes.Ldstr, "Invalid Date");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(validLabel);
        var localTimeLocal = il.DeclareLocal(_types.DateTime);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, _tsDateUtcDateTimeField);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToLocalTime")!);
        il.Emit(OpCodes.Stloc, localTimeLocal);

        il.Emit(OpCodes.Ldloca, localTimeLocal);
        il.Emit(OpCodes.Ldstr, "HH:mm:ss");
        il.Emit(OpCodes.Call, typeof(CultureInfo).GetProperty("InvariantCulture")!.GetGetMethod()!);
        il.Emit(OpCodes.Call, _types.DateTime.GetMethod("ToString", [_types.String, typeof(IFormatProvider)])!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSDateValueOf(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ValueOf",
            MethodAttributes.Public,
            _types.Double,
            Type.EmptyTypes
        );
        runtime.TSDateMethods["ValueOf"] = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _tsDateGetTimeMethod);
        il.Emit(OpCodes.Ret);
    }
}
