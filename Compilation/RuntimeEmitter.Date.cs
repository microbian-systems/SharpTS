using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Date-related runtime emission methods.
/// Uses the emitted $TSDate class for standalone support.
/// </summary>
public partial class RuntimeEmitter
{
    private void EmitDateMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitDateNow(typeBuilder, runtime);
        EmitCreateDateNoArgs(typeBuilder, runtime);
        EmitCreateDateFromValue(typeBuilder, runtime);
        EmitCreateDateFromComponents(typeBuilder, runtime);
        EmitDateToString(typeBuilder, runtime);
        EmitDateGetTime(typeBuilder, runtime);
        EmitDateGetFullYear(typeBuilder, runtime);
        EmitDateGetMonth(typeBuilder, runtime);
        EmitDateGetDate(typeBuilder, runtime);
        EmitDateGetDay(typeBuilder, runtime);
        EmitDateGetHours(typeBuilder, runtime);
        EmitDateGetMinutes(typeBuilder, runtime);
        EmitDateGetSeconds(typeBuilder, runtime);
        EmitDateGetMilliseconds(typeBuilder, runtime);
        EmitDateGetTimezoneOffset(typeBuilder, runtime);
        EmitDateSetTime(typeBuilder, runtime);
        EmitDateSetFullYear(typeBuilder, runtime);
        EmitDateSetMonth(typeBuilder, runtime);
        EmitDateSetDate(typeBuilder, runtime);
        EmitDateSetHours(typeBuilder, runtime);
        EmitDateSetMinutes(typeBuilder, runtime);
        EmitDateSetSeconds(typeBuilder, runtime);
        EmitDateSetMilliseconds(typeBuilder, runtime);
        EmitDateToISOString(typeBuilder, runtime);
        EmitDateToDateString(typeBuilder, runtime);
        EmitDateToTimeString(typeBuilder, runtime);
        EmitDateToJSON(typeBuilder, runtime);
        EmitDateValueOf(typeBuilder, runtime);

        // UTC getters + legacy getYear (#516): 0-arg, return double, NaN on non-Date.
        runtime.DateGetUTCFullYear = EmitDateDoubleGetter(typeBuilder, runtime, "DateGetUTCFullYear", "GetUTCFullYear");
        runtime.DateGetUTCMonth = EmitDateDoubleGetter(typeBuilder, runtime, "DateGetUTCMonth", "GetUTCMonth");
        runtime.DateGetUTCDate = EmitDateDoubleGetter(typeBuilder, runtime, "DateGetUTCDate", "GetUTCDate");
        runtime.DateGetUTCDay = EmitDateDoubleGetter(typeBuilder, runtime, "DateGetUTCDay", "GetUTCDay");
        runtime.DateGetUTCHours = EmitDateDoubleGetter(typeBuilder, runtime, "DateGetUTCHours", "GetUTCHours");
        runtime.DateGetUTCMinutes = EmitDateDoubleGetter(typeBuilder, runtime, "DateGetUTCMinutes", "GetUTCMinutes");
        runtime.DateGetUTCSeconds = EmitDateDoubleGetter(typeBuilder, runtime, "DateGetUTCSeconds", "GetUTCSeconds");
        runtime.DateGetUTCMilliseconds = EmitDateDoubleGetter(typeBuilder, runtime, "DateGetUTCMilliseconds", "GetUTCMilliseconds");
        runtime.DateGetYear = EmitDateDoubleGetter(typeBuilder, runtime, "DateGetYear", "GetYear");

        // UTC setters (#516). Multi-arg setters package args as object[] (primary arg read);
        // single-arg setters take a direct double. Legacy setYear takes a direct double.
        runtime.DateSetUTCFullYear = EmitDateArgsArraySetter(typeBuilder, runtime, "DateSetUTCFullYear", "SetUTCFullYear");
        runtime.DateSetUTCMonth = EmitDateArgsArraySetter(typeBuilder, runtime, "DateSetUTCMonth", "SetUTCMonth");
        runtime.DateSetUTCDate = EmitDateDoubleArgSetter(typeBuilder, runtime, "DateSetUTCDate", "SetUTCDate");
        runtime.DateSetUTCHours = EmitDateArgsArraySetter(typeBuilder, runtime, "DateSetUTCHours", "SetUTCHours");
        runtime.DateSetUTCMinutes = EmitDateArgsArraySetter(typeBuilder, runtime, "DateSetUTCMinutes", "SetUTCMinutes");
        runtime.DateSetUTCSeconds = EmitDateArgsArraySetter(typeBuilder, runtime, "DateSetUTCSeconds", "SetUTCSeconds");
        runtime.DateSetUTCMilliseconds = EmitDateDoubleArgSetter(typeBuilder, runtime, "DateSetUTCMilliseconds", "SetUTCMilliseconds");
        runtime.DateSetYear = EmitDateDoubleArgSetter(typeBuilder, runtime, "DateSetYear", "SetYear");

        // Conversion methods (#516): return string, "Invalid Date" on non-Date.
        runtime.DateToUTCString = EmitDateStringMethod(typeBuilder, runtime, "DateToUTCString", "ToUTCString");
        runtime.DateToLocaleDateString = EmitDateStringMethod(typeBuilder, runtime, "DateToLocaleDateString", "ToLocaleDateString");
        runtime.DateToLocaleTimeString = EmitDateStringMethod(typeBuilder, runtime, "DateToLocaleTimeString", "ToLocaleTimeString");
        runtime.DateToLocaleString = EmitDateStringMethod(typeBuilder, runtime, "DateToLocaleString", "ToLocaleString");
    }

    /// <summary>
    /// Emits a static $Runtime helper for a 0-arg $TSDate getter returning a double
    /// (NaN when the receiver is not a $TSDate). Returns the emitted method.
    /// </summary>
    private MethodBuilder EmitDateDoubleGetter(TypeBuilder typeBuilder, EmittedRuntime runtime, string runtimeName, string instanceMethodName)
    {
        var method = typeBuilder.DefineMethod(
            runtimeName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );
        EmitDateInstanceMethodCall(typeBuilder, runtime, runtimeName, instanceMethodName, method);
        return method;
    }

    /// <summary>
    /// Emits a static $Runtime helper for a $TSDate setter taking a single double argument
    /// (NaN when the receiver is not a $TSDate). Returns the emitted method.
    /// </summary>
    private MethodBuilder EmitDateDoubleArgSetter(TypeBuilder typeBuilder, EmittedRuntime runtime, string runtimeName, string instanceMethodName)
    {
        var method = typeBuilder.DefineMethod(
            runtimeName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object, _types.Double]
        );

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods[instanceMethodName]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits a static $Runtime helper for a multi-arg $TSDate setter whose arguments are
    /// packaged as object[]; only the primary argument (index 0) is honored, matching the
    /// other compiled Date setters (#536) (NaN when the receiver is not a $TSDate). Returns the method.
    /// </summary>
    private MethodBuilder EmitDateArgsArraySetter(TypeBuilder typeBuilder, EmittedRuntime runtime, string runtimeName, string instanceMethodName)
    {
        var method = typeBuilder.DefineMethod(
            runtimeName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object, _types.ObjectArray]
        );

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods[instanceMethodName]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits a static $Runtime helper for a 0-arg $TSDate conversion method returning a string
    /// ("Invalid Date" when the receiver is not a $TSDate). Returns the emitted method.
    /// </summary>
    private MethodBuilder EmitDateStringMethod(TypeBuilder typeBuilder, EmittedRuntime runtime, string runtimeName, string instanceMethodName)
    {
        var method = typeBuilder.DefineMethod(
            runtimeName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]
        );

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods[instanceMethodName]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldstr, "Invalid Date");
        il.Emit(OpCodes.Ret);
        return method;
    }

    private void EmitDateNow(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateNow",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            _types.EmptyTypes
        );
        runtime.DateNow = method;

        var il = method.GetILGenerator();
        // Process any pending virtual timers before returning
        // This implements JavaScript-like single-threaded timer semantics
        il.Emit(OpCodes.Call, runtime.ProcessPendingTimers);
        il.Emit(OpCodes.Pop); // discard int return (next timer delay)
        // Call $TSDate.Now() static method
        il.Emit(OpCodes.Call, runtime.TSDateNowStatic);
        il.Emit(OpCodes.Ret);
    }

    private void EmitCreateDateNoArgs(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CreateDateNoArgs",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            _types.EmptyTypes
        );
        runtime.CreateDateNoArgs = method;

        var il = method.GetILGenerator();
        // new $TSDate()
        il.Emit(OpCodes.Newobj, runtime.TSDateCtorNoArgs);
        il.Emit(OpCodes.Ret);
    }

    private void EmitCreateDateFromValue(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CreateDateFromValue",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.CreateDateFromValue = method;

        var il = method.GetILGenerator();
        var stringLabel = il.DefineLabel();
        var defaultLabel = il.DefineLabel();

        // Check if value is double
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, stringLabel);

        // Double case: new $TSDate((double)value)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Newobj, runtime.TSDateCtorMilliseconds);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(stringLabel);
        // Check if value is string
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, defaultLabel);

        // String case: new $TSDate((string)value)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Newobj, runtime.TSDateCtorString);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(defaultLabel);
        // Default: new $TSDate()
        il.Emit(OpCodes.Newobj, runtime.TSDateCtorNoArgs);
        il.Emit(OpCodes.Ret);
    }

    private void EmitCreateDateFromComponents(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CreateDateFromComponents",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Double, _types.Double, _types.Double, _types.Double, _types.Double, _types.Double, _types.Double]
        );
        runtime.CreateDateFromComponents = method;

        var il = method.GetILGenerator();
        // new $TSDate((int)year, (int)month, (int)day, (int)hours, (int)minutes, (int)seconds, (int)ms)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldarg_S, (byte)5);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldarg_S, (byte)6);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Newobj, runtime.TSDateCtorComponents);
        il.Emit(OpCodes.Ret);
    }

    private void EmitDateToString(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateToString",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]
        );
        runtime.DateToString = method;

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        // Check if date is $TSDate
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        // Call date.ToString()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldstr, "Invalid Date");
        il.Emit(OpCodes.Ret);
    }

    private void EmitDateInstanceMethodCall(TypeBuilder typeBuilder, EmittedRuntime runtime,
        string helperName, string instanceMethodName, MethodBuilder targetMethod)
    {
        var il = targetMethod.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        // Check if date is $TSDate
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        // Call date.Method()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods[instanceMethodName]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);
    }

    private void EmitDateGetTime(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateGetTime",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );
        runtime.DateGetTime = method;
        EmitDateInstanceMethodCall(typeBuilder, runtime, "DateGetTime", "GetTime", method);
    }

    private void EmitDateGetFullYear(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateGetFullYear",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );
        runtime.DateGetFullYear = method;
        EmitDateInstanceMethodCall(typeBuilder, runtime, "DateGetFullYear", "GetFullYear", method);
    }

    private void EmitDateGetMonth(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateGetMonth",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );
        runtime.DateGetMonth = method;
        EmitDateInstanceMethodCall(typeBuilder, runtime, "DateGetMonth", "GetMonth", method);
    }

    private void EmitDateGetDate(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateGetDate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );
        runtime.DateGetDate = method;
        EmitDateInstanceMethodCall(typeBuilder, runtime, "DateGetDate", "GetDate", method);
    }

    private void EmitDateGetDay(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateGetDay",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );
        runtime.DateGetDay = method;
        EmitDateInstanceMethodCall(typeBuilder, runtime, "DateGetDay", "GetDay", method);
    }

    private void EmitDateGetHours(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateGetHours",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );
        runtime.DateGetHours = method;
        EmitDateInstanceMethodCall(typeBuilder, runtime, "DateGetHours", "GetHours", method);
    }

    private void EmitDateGetMinutes(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateGetMinutes",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );
        runtime.DateGetMinutes = method;
        EmitDateInstanceMethodCall(typeBuilder, runtime, "DateGetMinutes", "GetMinutes", method);
    }

    private void EmitDateGetSeconds(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateGetSeconds",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );
        runtime.DateGetSeconds = method;
        EmitDateInstanceMethodCall(typeBuilder, runtime, "DateGetSeconds", "GetSeconds", method);
    }

    private void EmitDateGetMilliseconds(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateGetMilliseconds",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );
        runtime.DateGetMilliseconds = method;
        EmitDateInstanceMethodCall(typeBuilder, runtime, "DateGetMilliseconds", "GetMilliseconds", method);
    }

    private void EmitDateGetTimezoneOffset(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateGetTimezoneOffset",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );
        runtime.DateGetTimezoneOffset = method;
        EmitDateInstanceMethodCall(typeBuilder, runtime, "DateGetTimezoneOffset", "GetTimezoneOffset", method);
    }

    private void EmitDateSetTime(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateSetTime",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object, _types.Double]
        );
        runtime.DateSetTime = method;

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods["SetTime"]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);
    }

    private void EmitDateSetFullYear(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateSetFullYear",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object, _types.ObjectArray]
        );
        runtime.DateSetFullYear = method;

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        // Call SetFullYear with args[0] as the year
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Ldarg_1);  // args array
        il.Emit(OpCodes.Ldc_I4_0);  // index 0
        il.Emit(OpCodes.Ldelem_Ref);  // args[0]
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods["SetFullYear"]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);
    }

    private void EmitDateSetMonth(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateSetMonth",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object, _types.ObjectArray]
        );
        runtime.DateSetMonth = method;

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        // Call SetMonth with args[0]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods["SetMonth"]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);
    }

    private void EmitDateSetDate(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateSetDate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object, _types.Double]
        );
        runtime.DateSetDate = method;

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        // Call SetDate with arg1 (direct double parameter)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods["SetDate"]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);
    }

    private void EmitDateSetHours(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateSetHours",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object, _types.ObjectArray]
        );
        runtime.DateSetHours = method;

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        // Call SetHours with args[0]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods["SetHours"]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);
    }

    private void EmitDateSetMinutes(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateSetMinutes",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object, _types.ObjectArray]
        );
        runtime.DateSetMinutes = method;

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        // Call SetMinutes with args[0]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods["SetMinutes"]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);
    }

    private void EmitDateSetSeconds(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateSetSeconds",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object, _types.ObjectArray]
        );
        runtime.DateSetSeconds = method;

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        // Call SetSeconds with args[0]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods["SetSeconds"]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);
    }

    private void EmitDateSetMilliseconds(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateSetMilliseconds",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object, _types.Double]
        );
        runtime.DateSetMilliseconds = method;

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        // Call SetMilliseconds with arg1 (direct double parameter)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods["SetMilliseconds"]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Ret);
    }

    private void EmitDateToISOString(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateToISOString",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]
        );
        runtime.DateToISOString = method;

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods["ToISOString"]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldstr, "Runtime Error: Invalid Date");
        il.Emit(OpCodes.Newobj, _types.Exception.GetConstructor([_types.String])!);
        il.Emit(OpCodes.Throw);
    }

    private void EmitDateToDateString(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateToDateString",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]
        );
        runtime.DateToDateString = method;

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods["ToDateString"]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldstr, "Invalid Date");
        il.Emit(OpCodes.Ret);
    }

    private void EmitDateToTimeString(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateToTimeString",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]
        );
        runtime.DateToTimeString = method;

        var il = method.GetILGenerator();
        var invalidLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, invalidLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods["ToTimeString"]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(invalidLabel);
        il.Emit(OpCodes.Ldstr, "Invalid Date");
        il.Emit(OpCodes.Ret);
    }

    // ECMA-262 §21.4.4.37: toJSON returns the ISO string, or null for a non-finite (Invalid)
    // date. Returns object (string | null), mirroring DateBuiltIns' interpreted implementation.
    private void EmitDateToJSON(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateToJSON",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.DateToJSON = method;

        var il = method.GetILGenerator();
        var nullLabel = il.DefineLabel();

        // Non-Date receiver → null (lenient; unreachable when the type checker is satisfied).
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDateType);
        il.Emit(OpCodes.Brfalse, nullLabel);

        // Invalid (NaN timestamp) → null, before ToISOString's RangeError throw can be reached.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods["GetTime"]);
        il.Emit(OpCodes.Call, _types.Double.GetMethod("IsNaN")!);
        il.Emit(OpCodes.Brtrue, nullLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDateType);
        il.Emit(OpCodes.Callvirt, runtime.TSDateMethods["ToISOString"]);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    private void EmitDateValueOf(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DateValueOf",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );
        runtime.DateValueOf = method;
        EmitDateInstanceMethodCall(typeBuilder, runtime, "DateValueOf", "ValueOf", method);
    }
}
