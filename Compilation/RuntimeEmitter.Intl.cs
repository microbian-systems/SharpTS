using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits Intl.NumberFormat support methods into the $Runtime class.
    /// Uses reflection to call RuntimeTypes methods for standalone DLL compatibility.
    /// </summary>
    private void EmitIntlMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitCreateIntlNumberFormat(typeBuilder, runtime);
        EmitIntlNumberFormatFormat(typeBuilder, runtime);
        EmitIntlNumberFormatResolvedOptions(typeBuilder, runtime);

        EmitCreateIntlDateTimeFormat(typeBuilder, runtime);
        EmitIntlDateTimeFormatFormat(typeBuilder, runtime);
        EmitIntlDateTimeFormatResolvedOptions(typeBuilder, runtime);
        EmitIntlDateTimeFormatFormatToParts(typeBuilder, runtime);
        EmitIntlDateTimeFormatFormatRange(typeBuilder, runtime);
        EmitIntlDateTimeFormatFormatRangeToParts(typeBuilder, runtime);

        EmitCreateIntlCollator(typeBuilder, runtime);
        EmitIntlCollatorCompare(typeBuilder, runtime);
        EmitIntlCollatorResolvedOptions(typeBuilder, runtime);

        EmitCreateIntlPluralRules(typeBuilder, runtime);
        EmitIntlPluralRulesSelect(typeBuilder, runtime);
        EmitIntlPluralRulesResolvedOptions(typeBuilder, runtime);

        EmitCreateIntlRelativeTimeFormat(typeBuilder, runtime);
        EmitIntlRelativeTimeFormatFormat(typeBuilder, runtime);
        EmitIntlRelativeTimeFormatResolvedOptions(typeBuilder, runtime);

        EmitCreateIntlListFormat(typeBuilder, runtime);
        EmitIntlListFormatFormat(typeBuilder, runtime);
        EmitIntlListFormatFormatToParts(typeBuilder, runtime);
        EmitIntlListFormatResolvedOptions(typeBuilder, runtime);
    }

    /// <summary>
    /// CreateIntlNumberFormat(object? locale, object? options) → object
    /// Creates a new Intl.NumberFormat via reflection to RuntimeTypes.
    /// </summary>
    private void EmitCreateIntlNumberFormat(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CreateIntlNumberFormat",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.CreateIntlNumberFormat = method;

        var il = method.GetILGenerator();

        // Get the RuntimeTypes type via reflection
        // Type runtimeTypesType = Type.GetType("SharpTS.Compilation.RuntimeTypes, SharpTS")
        il.Emit(OpCodes.Ldstr, "SharpTS.Compilation.RuntimeTypes, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));

        // Get the CreateIntlNumberFormat method
        il.Emit(OpCodes.Ldstr, "CreateIntlNumberFormat");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));

        // Prepare args: new object[] { locale, options }
        il.Emit(OpCodes.Ldnull); // null target for static method invoke

        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0); // locale
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1); // options
        il.Emit(OpCodes.Stelem_Ref);

        // Call methodInfo.Invoke(null, args)
        var invokeMethod = _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray);
        il.Emit(OpCodes.Callvirt, invokeMethod!);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// IntlNumberFormatFormat(object? formatter, object? number) → object?
    /// Calls format() via reflection to RuntimeTypes.
    /// </summary>
    private void EmitIntlNumberFormatFormat(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "IntlNumberFormatFormat",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.IntlNumberFormatFormat = method;

        var il = method.GetILGenerator();

        // Get the RuntimeTypes type via reflection
        il.Emit(OpCodes.Ldstr, "SharpTS.Compilation.RuntimeTypes, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));

        // Get the IntlNumberFormatFormat method
        il.Emit(OpCodes.Ldstr, "IntlNumberFormatFormat");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));

        // Prepare args: new object[] { formatter, number }
        il.Emit(OpCodes.Ldnull); // null target for static method

        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0); // formatter
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1); // number
        il.Emit(OpCodes.Stelem_Ref);

        var invokeMethod = _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray);
        il.Emit(OpCodes.Callvirt, invokeMethod!);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// IntlNumberFormatResolvedOptions(object? formatter) → object?
    /// Calls resolvedOptions() via reflection to RuntimeTypes.
    /// </summary>
    private void EmitIntlNumberFormatResolvedOptions(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "IntlNumberFormatResolvedOptions",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.IntlNumberFormatResolvedOptions = method;

        var il = method.GetILGenerator();

        // Get the RuntimeTypes type via reflection
        il.Emit(OpCodes.Ldstr, "SharpTS.Compilation.RuntimeTypes, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));

        // Get the IntlNumberFormatResolvedOptions method
        il.Emit(OpCodes.Ldstr, "IntlNumberFormatResolvedOptions");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));

        // Prepare args: new object[] { formatter }
        il.Emit(OpCodes.Ldnull); // null target for static method

        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0); // formatter
        il.Emit(OpCodes.Stelem_Ref);

        var invokeMethod = _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray);
        il.Emit(OpCodes.Callvirt, invokeMethod!);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// CreateIntlDateTimeFormat(object? locale, object? options) → object
    /// Creates a new Intl.DateTimeFormat via reflection to RuntimeTypes.
    /// </summary>
    private void EmitCreateIntlDateTimeFormat(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CreateIntlDateTimeFormat",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.CreateIntlDateTimeFormat = method;

        var il = method.GetILGenerator();

        il.Emit(OpCodes.Ldstr, "SharpTS.Compilation.RuntimeTypes, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));

        il.Emit(OpCodes.Ldstr, "CreateIntlDateTimeFormat");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));

        il.Emit(OpCodes.Ldnull);

        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0); // locale
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1); // options
        il.Emit(OpCodes.Stelem_Ref);

        var invokeMethod = _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray);
        il.Emit(OpCodes.Callvirt, invokeMethod!);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// IntlDateTimeFormatFormat(object? formatter, object? date) → object?
    /// Calls format() via reflection to RuntimeTypes.
    /// </summary>
    private void EmitIntlDateTimeFormatFormat(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "IntlDateTimeFormatFormat",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.IntlDateTimeFormatFormat = method;

        var il = method.GetILGenerator();

        il.Emit(OpCodes.Ldstr, "SharpTS.Compilation.RuntimeTypes, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));

        il.Emit(OpCodes.Ldstr, "IntlDateTimeFormatFormat");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));

        il.Emit(OpCodes.Ldnull);

        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0); // formatter
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1); // date
        il.Emit(OpCodes.Stelem_Ref);

        var invokeMethod = _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray);
        il.Emit(OpCodes.Callvirt, invokeMethod!);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// IntlDateTimeFormatResolvedOptions(object? formatter) → object?
    /// Calls resolvedOptions() via reflection to RuntimeTypes.
    /// </summary>
    private void EmitIntlDateTimeFormatResolvedOptions(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "IntlDateTimeFormatResolvedOptions",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.IntlDateTimeFormatResolvedOptions = method;

        var il = method.GetILGenerator();

        il.Emit(OpCodes.Ldstr, "SharpTS.Compilation.RuntimeTypes, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));

        il.Emit(OpCodes.Ldstr, "IntlDateTimeFormatResolvedOptions");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));

        il.Emit(OpCodes.Ldnull);

        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0); // formatter
        il.Emit(OpCodes.Stelem_Ref);

        var invokeMethod = _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray);
        il.Emit(OpCodes.Callvirt, invokeMethod!);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// IntlDateTimeFormatFormatToParts(object? formatter, object? date) → object?
    /// Calls formatToParts() via reflection to RuntimeTypes.
    /// </summary>
    private void EmitIntlDateTimeFormatFormatToParts(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "IntlDateTimeFormatFormatToParts",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.IntlDateTimeFormatFormatToParts = method;

        var il = method.GetILGenerator();

        il.Emit(OpCodes.Ldstr, "SharpTS.Compilation.RuntimeTypes, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));

        il.Emit(OpCodes.Ldstr, "IntlDateTimeFormatFormatToParts");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));

        il.Emit(OpCodes.Ldnull);

        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0); // formatter
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1); // date
        il.Emit(OpCodes.Stelem_Ref);

        var invokeMethod2 = _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray);
        il.Emit(OpCodes.Callvirt, invokeMethod2!);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// IntlDateTimeFormatFormatRange(object? formatter, object? start, object? end) → object?
    /// Calls formatRange() via reflection to RuntimeTypes.
    /// </summary>
    private void EmitIntlDateTimeFormatFormatRange(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "IntlDateTimeFormatFormatRange",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.IntlDateTimeFormatFormatRange = method;

        var il = method.GetILGenerator();

        il.Emit(OpCodes.Ldstr, "SharpTS.Compilation.RuntimeTypes, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));

        il.Emit(OpCodes.Ldstr, "IntlDateTimeFormatFormatRange");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));

        il.Emit(OpCodes.Ldnull);

        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0); // formatter
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1); // start
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldarg_2); // end
        il.Emit(OpCodes.Stelem_Ref);

        var invokeMethod2 = _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray);
        il.Emit(OpCodes.Callvirt, invokeMethod2!);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// IntlDateTimeFormatFormatRangeToParts(object? formatter, object? start, object? end) → object?
    /// Calls formatRangeToParts() via reflection to RuntimeTypes.
    /// </summary>
    private void EmitIntlDateTimeFormatFormatRangeToParts(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "IntlDateTimeFormatFormatRangeToParts",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.IntlDateTimeFormatFormatRangeToParts = method;

        var il = method.GetILGenerator();

        il.Emit(OpCodes.Ldstr, "SharpTS.Compilation.RuntimeTypes, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));

        il.Emit(OpCodes.Ldstr, "IntlDateTimeFormatFormatRangeToParts");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));

        il.Emit(OpCodes.Ldnull);

        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0); // formatter
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1); // start
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldarg_2); // end
        il.Emit(OpCodes.Stelem_Ref);

        var invokeMethod2 = _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray);
        il.Emit(OpCodes.Callvirt, invokeMethod2!);

        il.Emit(OpCodes.Ret);
    }

    // ========== Intl.Collator ==========

    private void EmitCreateIntlCollator(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.CreateIntlCollator = EmitReflectionHelper(typeBuilder, "CreateIntlCollator", 2);
    }

    private void EmitIntlCollatorCompare(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.IntlCollatorCompare = EmitReflectionHelper(typeBuilder, "IntlCollatorCompare", 3);
    }

    private void EmitIntlCollatorResolvedOptions(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.IntlCollatorResolvedOptions = EmitReflectionHelper(typeBuilder, "IntlCollatorResolvedOptions", 1);
    }

    // ========== Intl.PluralRules ==========

    private void EmitCreateIntlPluralRules(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.CreateIntlPluralRules = EmitReflectionHelper(typeBuilder, "CreateIntlPluralRules", 2);
    }

    private void EmitIntlPluralRulesSelect(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.IntlPluralRulesSelect = EmitReflectionHelper(typeBuilder, "IntlPluralRulesSelect", 2);
    }

    private void EmitIntlPluralRulesResolvedOptions(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.IntlPluralRulesResolvedOptions = EmitReflectionHelper(typeBuilder, "IntlPluralRulesResolvedOptions", 1);
    }

    // ========== Intl.RelativeTimeFormat ==========

    private void EmitCreateIntlRelativeTimeFormat(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.CreateIntlRelativeTimeFormat = EmitReflectionHelper(typeBuilder, "CreateIntlRelativeTimeFormat", 2);
    }

    private void EmitIntlRelativeTimeFormatFormat(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.IntlRelativeTimeFormatFormat = EmitReflectionHelper(typeBuilder, "IntlRelativeTimeFormatFormat", 3);
    }

    private void EmitIntlRelativeTimeFormatResolvedOptions(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.IntlRelativeTimeFormatResolvedOptions = EmitReflectionHelper(typeBuilder, "IntlRelativeTimeFormatResolvedOptions", 1);
    }

    // ========== Intl.ListFormat ==========

    private void EmitCreateIntlListFormat(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.CreateIntlListFormat = EmitReflectionHelper(typeBuilder, "CreateIntlListFormat", 2);
    }

    private void EmitIntlListFormatFormat(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.IntlListFormatFormat = EmitReflectionHelper(typeBuilder, "IntlListFormatFormat", 2);
    }

    private void EmitIntlListFormatFormatToParts(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.IntlListFormatFormatToParts = EmitReflectionHelper(typeBuilder, "IntlListFormatFormatToParts", 2);
    }

    private void EmitIntlListFormatResolvedOptions(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.IntlListFormatResolvedOptions = EmitReflectionHelper(typeBuilder, "IntlListFormatResolvedOptions", 1);
    }

    // ========== Helper ==========

    /// <summary>
    /// Emits a reflection-based helper method that calls RuntimeTypes.{methodName} via reflection.
    /// All arguments are object? and passed as an object[] to MethodInfo.Invoke.
    /// </summary>
    private MethodBuilder EmitReflectionHelper(TypeBuilder typeBuilder, string methodName, int argCount)
    {
        var paramTypes = new Type[argCount];
        Array.Fill(paramTypes, _types.Object);

        var method = typeBuilder.DefineMethod(
            methodName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            paramTypes
        );

        var il = method.GetILGenerator();

        // Type.GetType("SharpTS.Compilation.RuntimeTypes, SharpTS")
        il.Emit(OpCodes.Ldstr, "SharpTS.Compilation.RuntimeTypes, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));

        // .GetMethod("methodName")
        il.Emit(OpCodes.Ldstr, methodName);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));

        // null target for static method
        il.Emit(OpCodes.Ldnull);

        // new object[argCount] { arg0, arg1, ... }
        il.Emit(OpCodes.Ldc_I4, argCount);
        il.Emit(OpCodes.Newarr, _types.Object);

        for (int i = 0; i < argCount; i++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ldarg, i);
            il.Emit(OpCodes.Stelem_Ref);
        }

        // methodInfo.Invoke(null, args)
        var invokeMethod = _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray);
        il.Emit(OpCodes.Callvirt, invokeMethod!);

        il.Emit(OpCodes.Ret);

        return method;
    }
}
