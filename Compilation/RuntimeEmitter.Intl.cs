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
}
