using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits <c>$Runtime</c> adapter methods that expose JS <c>Math</c> static
/// methods as values (for <c>var f = Math.floor; f(x)</c>-style patterns used
/// by lodash and similar libraries). Adapter signatures are
/// <c>object(object)</c> / <c>object(object, object)</c> / <c>object(object[])</c>
/// so they're directly wrappable in <c>$TSFunction</c>, and each adapter routes
/// its arg(s) through <c>ToNumber</c> first to preserve ECMAScript coercion
/// (e.g. <c>Math.floor("2.5") === 2</c>, <c>Math.floor(null) === 0</c>).
///
/// See issue #60 for the motivating lodash breakage and the wider set of
/// built-in static methods that need analogous treatment.
/// </summary>
public partial class RuntimeEmitter
{
    private void EmitMathAdapters(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.MathFloorAdapter = EmitUnaryMathAdapter(typeBuilder, runtime, "MathFloorAdapter", "Floor");
        runtime.MathCeilAdapter = EmitUnaryMathAdapter(typeBuilder, runtime, "MathCeilAdapter", "Ceiling");
        runtime.MathAbsAdapter = EmitUnaryMathAdapter(typeBuilder, runtime, "MathAbsAdapter", "Abs");
        runtime.MathSqrtAdapter = EmitUnaryMathAdapter(typeBuilder, runtime, "MathSqrtAdapter", "Sqrt");
        runtime.MathTruncAdapter = EmitUnaryMathAdapter(typeBuilder, runtime, "MathTruncAdapter", "Truncate");
        runtime.MathSinAdapter = EmitUnaryMathAdapter(typeBuilder, runtime, "MathSinAdapter", "Sin");
        runtime.MathCosAdapter = EmitUnaryMathAdapter(typeBuilder, runtime, "MathCosAdapter", "Cos");
        runtime.MathTanAdapter = EmitUnaryMathAdapter(typeBuilder, runtime, "MathTanAdapter", "Tan");
        runtime.MathLogAdapter = EmitUnaryMathAdapter(typeBuilder, runtime, "MathLogAdapter", "Log");
        runtime.MathExpAdapter = EmitUnaryMathAdapter(typeBuilder, runtime, "MathExpAdapter", "Exp");

        runtime.MathRoundAdapter = EmitMathRoundAdapter(typeBuilder, runtime);
        runtime.MathSignAdapter = EmitMathSignAdapter(typeBuilder, runtime);
        runtime.MathPowAdapter = EmitMathPowAdapter(typeBuilder, runtime);
        runtime.MathMaxAdapter = EmitMathMinMaxAdapter(typeBuilder, runtime, "MathMaxAdapter", isMax: true);
        runtime.MathMinAdapter = EmitMathMinMaxAdapter(typeBuilder, runtime, "MathMinAdapter", isMax: false);
    }

    /// <summary>
    /// Emits <c>public static object {name}(object arg) =&gt; Math.{systemMethod}(ToNumber(arg))</c>.
    /// </summary>
    private MethodBuilder EmitUnaryMathAdapter(TypeBuilder typeBuilder, EmittedRuntime runtime, string name, string systemMathMethod)
    {
        var method = typeBuilder.DefineMethod(
            name,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.ToNumber);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Math, systemMathMethod, _types.Double));
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits Math.round adapter. JS rounds half-values toward +∞
    /// (<c>Math.round(-0.5) === 0</c>, <c>Math.round(0.5) === 1</c>), which
    /// matches <c>Math.Floor(x + 0.5)</c>.
    /// </summary>
    private MethodBuilder EmitMathRoundAdapter(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "MathRoundAdapter",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.ToNumber);
        il.Emit(OpCodes.Ldc_R8, 0.5);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Math, "Floor", _types.Double));
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits Math.sign adapter. System.Math.Sign returns int and throws on NaN;
    /// JS returns NaN for NaN input. Handle NaN explicitly, convert int result
    /// to double for boxing consistency.
    /// </summary>
    private MethodBuilder EmitMathSignAdapter(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "MathSignAdapter",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );

        var il = method.GetILGenerator();
        var vLocal = il.DeclareLocal(_types.Double);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.ToNumber);
        il.Emit(OpCodes.Stloc, vLocal);

        // if (double.IsNaN(v)) return NaN (boxed)
        var notNaN = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, vLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Double, "IsNaN", _types.Double));
        il.Emit(OpCodes.Brfalse, notNaN);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notNaN);
        il.Emit(OpCodes.Ldloc, vLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Math, "Sign", _types.Double));
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits <c>MathPowAdapter(object base, object exponent)</c>.
    /// </summary>
    private MethodBuilder EmitMathPowAdapter(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "MathPowAdapter",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.ToNumber);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ToNumber);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Math, "Pow", _types.Double, _types.Double));
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits variadic Math.max / Math.min adapter. Signature
    /// <c>object(object[])</c> so that <c>$TSFunction.AdjustArgs</c> packs all
    /// JS args into the <c>object[]</c> slot (its existing rest-parameter
    /// handling). Spec behavior: empty args returns ±∞; any NaN in the input
    /// short-circuits to NaN.
    /// </summary>
    private MethodBuilder EmitMathMinMaxAdapter(TypeBuilder typeBuilder, EmittedRuntime runtime, string name, bool isMax)
    {
        var method = typeBuilder.DefineMethod(
            name,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ObjectArray]
        );

        var il = method.GetILGenerator();
        var resultLocal = il.DeclareLocal(_types.Double);
        var iLocal = il.DeclareLocal(_types.Int32);
        var nextLocal = il.DeclareLocal(_types.Double);

        // if (args == null || args.Length == 0) return isMax ? -Infinity : +Infinity;
        var notEmpty = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, notEmpty);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Brtrue, notEmpty);
        il.Emit(OpCodes.Ldc_R8, isMax ? double.NegativeInfinity : double.PositiveInfinity);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notEmpty);

        // result = ToNumber(args[0])
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Call, runtime.ToNumber);
        il.Emit(OpCodes.Stloc, resultLocal);

        // for (i = 1; i < args.Length; i++)
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, iLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, loopEnd);

        // next = ToNumber(args[i])
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Call, runtime.ToNumber);
        il.Emit(OpCodes.Stloc, nextLocal);

        // if (double.IsNaN(next) || double.IsNaN(result)) return NaN;
        var nanReturn = il.DefineLabel();
        var notNaN = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, nextLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Double, "IsNaN", _types.Double));
        il.Emit(OpCodes.Brtrue, nanReturn);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Double, "IsNaN", _types.Double));
        il.Emit(OpCodes.Brfalse, notNaN);

        il.MarkLabel(nanReturn);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notNaN);

        // if (isMax ? next > result : next < result) result = next;
        var skipUpdate = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, nextLocal);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(isMax ? OpCodes.Ble : OpCodes.Bge, skipUpdate);
        il.Emit(OpCodes.Ldloc, nextLocal);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.MarkLabel(skipUpdate);

        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        // return result
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);

        return method;
    }
}
