using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters;

/// <summary>
/// Emitter strategy for Math static method calls and property access.
/// Handles Math.random(), Math.min(), Math.max(), Math.round(), etc. and Math.PI, Math.E.
/// </summary>
public sealed class MathStaticEmitter : IStaticTypeEmitterStrategy
{
    /// <summary>
    /// Attempts to emit IL for a Math static method call.
    /// </summary>
    public bool TryEmitStaticCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (methodName == "random")
        {
            il.Emit(OpCodes.Call, ctx.Runtime!.Random);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        // Handle variadic min/max (JavaScript allows any number of arguments)
        if (methodName is "min" or "max")
        {
            var minMaxMethod = methodName == "min"
                ? ctx.Types.GetMethod(ctx.Types.Math, "Min", ctx.Types.Double, ctx.Types.Double)
                : ctx.Types.GetMethod(ctx.Types.Math, "Max", ctx.Types.Double, ctx.Types.Double);

            if (arguments.Count == 0)
            {
                // No args: min() returns Infinity, max() returns -Infinity
                il.Emit(OpCodes.Ldc_R8, methodName == "min" ? double.PositiveInfinity : double.NegativeInfinity);
            }
            else
            {
                // Same ToNumber routing as the unary-Math loop below — handles
                // \`Math.max(undefined, 1)\` returning NaN (spec) instead of crashing.
                emitter.EmitExpression(arguments[0]);
                emitter.EnsureBoxed();
                il.Emit(OpCodes.Call, ctx.Runtime!.ToNumber);
                for (int i = 1; i < arguments.Count; i++)
                {
                    emitter.EmitExpression(arguments[i]);
                    emitter.EnsureBoxed();
                    il.Emit(OpCodes.Call, ctx.Runtime!.ToNumber);
                    il.Emit(OpCodes.Call, minMaxMethod);
                }
            }
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        // Math.sumPrecise(iterable) — ECMA-262 21.3.2.31. Iterates the input,
        // throws TypeError on non-Number elements (incl. BigInt), and returns
        // the precise sum. Unlike other Math.* methods, the arg is an iterable
        // not a number, so it bypasses the ToNumber-coercion loop below.
        if (methodName == "sumPrecise" && arguments.Count == 1)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EnsureBoxed();
            il.Emit(OpCodes.Call, ctx.Runtime!.MathSumPrecise);
            return true;
        }

        // Emit all arguments as doubles. Per ECMA-262, Math.* methods coerce
        // each arg via ToNumber — undefined → NaN, null → +0, "abc" → NaN, etc.
        // Pre-fix EmitExpressionAsDouble used Convert.ToDouble(object) which
        // threw InvalidCastException on $Undefined.Instance. Routing through
        // $Runtime.ToNumber gives spec semantics for all primitives.
        foreach (var arg in arguments)
        {
            emitter.EmitExpression(arg);
            emitter.EnsureBoxed();
            il.Emit(OpCodes.Call, ctx.Runtime!.ToNumber);
        }

        if (methodName == "round")
        {
            // JavaScript rounds half-values toward +infinity: Math.Floor(x + 0.5).
            // ECMA-262 21.3.2.28 zero-sign preservation: Math.round(-0) = -0,
            // but naive Math.Floor(-0 + 0.5) = +0 loses the sign. Stash arg;
            // special-case using double.IsNegative which distinguishes -0/+0
            // by sign bit (regular IEEE comparisons treat them as equal).
            // Branch:
            //   if x is in (-0.5, 0]: return -0 if IsNegative(x), else +0
            //   else: Floor(x + 0.5)
            var argLocal = il.DeclareLocal(ctx.Types.Double);
            il.Emit(OpCodes.Stloc, argLocal);
            var endLabel = il.DefineLabel();
            var doFloorLabel = il.DefineLabel();
            // if (x <= -0.5) → Floor path
            il.Emit(OpCodes.Ldloc, argLocal);
            il.Emit(OpCodes.Ldc_R8, -0.5);
            il.Emit(OpCodes.Ble, doFloorLabel);
            // if (x > 0) → Floor path
            il.Emit(OpCodes.Ldloc, argLocal);
            il.Emit(OpCodes.Ldc_R8, 0.0);
            il.Emit(OpCodes.Bgt, doFloorLabel);
            // x in (-0.5, 0]: return x as-is (preserves -0 sign).
            il.Emit(OpCodes.Ldloc, argLocal);
            il.Emit(OpCodes.Br, endLabel);

            il.MarkLabel(doFloorLabel);
            il.Emit(OpCodes.Ldloc, argLocal);
            il.Emit(OpCodes.Ldc_R8, 0.5);
            il.Emit(OpCodes.Add);
            var floorMethod = ctx.Types.GetMethod(ctx.Types.Math, "Floor", ctx.Types.Double);
            il.Emit(OpCodes.Call, floorMethod);

            il.MarkLabel(endLabel);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        if (methodName == "sign")
        {
            // System.Math.Sign throws ArithmeticException on NaN; spec says
            // Math.sign(NaN) === NaN. Math.sign(±0) must preserve sign per
            // ECMA-262 21.3.2.30: Math.sign(-0) = -0, Math.sign(+0) = +0.
            // Math.Sign(-0.0) returns 0 (loses sign), so short-circuit on zero.
            var argLocal = il.DeclareLocal(ctx.Types.Double);
            il.Emit(OpCodes.Stloc, argLocal);
            var notNaN = il.DefineLabel();
            var notZero = il.DefineLabel();
            var done = il.DefineLabel();
            // NaN check
            il.Emit(OpCodes.Ldloc, argLocal);
            il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.Double, "IsNaN", ctx.Types.Double));
            il.Emit(OpCodes.Brfalse, notNaN);
            il.Emit(OpCodes.Ldc_R8, double.NaN);
            il.Emit(OpCodes.Br, done);
            il.MarkLabel(notNaN);
            // Zero check (preserves -0/+0 sign): if (x == 0) return x.
            il.Emit(OpCodes.Ldloc, argLocal);
            il.Emit(OpCodes.Ldc_R8, 0.0);
            il.Emit(OpCodes.Bne_Un, notZero);
            il.Emit(OpCodes.Ldloc, argLocal);
            il.Emit(OpCodes.Br, done);
            il.MarkLabel(notZero);
            il.Emit(OpCodes.Ldloc, argLocal);
            il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.Math, "Sign", ctx.Types.Double));
            il.Emit(OpCodes.Conv_R8);
            il.MarkLabel(done);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        MethodInfo? mathMethod = methodName switch
        {
            "abs" => ctx.Types.GetMethod(ctx.Types.Math, "Abs", ctx.Types.Double),
            "floor" => ctx.Types.GetMethod(ctx.Types.Math, "Floor", ctx.Types.Double),
            "ceil" => ctx.Types.GetMethod(ctx.Types.Math, "Ceiling", ctx.Types.Double),
            "sqrt" => ctx.Types.GetMethod(ctx.Types.Math, "Sqrt", ctx.Types.Double),
            "sin" => ctx.Types.GetMethod(ctx.Types.Math, "Sin", ctx.Types.Double),
            "cos" => ctx.Types.GetMethod(ctx.Types.Math, "Cos", ctx.Types.Double),
            "tan" => ctx.Types.GetMethod(ctx.Types.Math, "Tan", ctx.Types.Double),
            "log" => ctx.Types.GetMethod(ctx.Types.Math, "Log", ctx.Types.Double),
            "exp" => ctx.Types.GetMethod(ctx.Types.Math, "Exp", ctx.Types.Double),
            "trunc" => ctx.Types.GetMethod(ctx.Types.Math, "Truncate", ctx.Types.Double),
            "pow" => ctx.Types.GetMethod(ctx.Types.Math, "Pow", ctx.Types.Double, ctx.Types.Double),
            // Inverse trig + hyperbolic — direct .NET Math equivalents.
            "asin" => ctx.Types.GetMethod(ctx.Types.Math, "Asin", ctx.Types.Double),
            "acos" => ctx.Types.GetMethod(ctx.Types.Math, "Acos", ctx.Types.Double),
            "atan" => ctx.Types.GetMethod(ctx.Types.Math, "Atan", ctx.Types.Double),
            "atan2" => ctx.Types.GetMethod(ctx.Types.Math, "Atan2", ctx.Types.Double, ctx.Types.Double),
            "sinh" => ctx.Types.GetMethod(ctx.Types.Math, "Sinh", ctx.Types.Double),
            "cosh" => ctx.Types.GetMethod(ctx.Types.Math, "Cosh", ctx.Types.Double),
            "tanh" => ctx.Types.GetMethod(ctx.Types.Math, "Tanh", ctx.Types.Double),
            "asinh" => ctx.Types.GetMethod(ctx.Types.Math, "Asinh", ctx.Types.Double),
            "acosh" => ctx.Types.GetMethod(ctx.Types.Math, "Acosh", ctx.Types.Double),
            "atanh" => ctx.Types.GetMethod(ctx.Types.Math, "Atanh", ctx.Types.Double),
            "cbrt" => ctx.Types.GetMethod(ctx.Types.Math, "Cbrt", ctx.Types.Double),
            "log10" => ctx.Types.GetMethod(ctx.Types.Math, "Log10", ctx.Types.Double),
            "log2" => ctx.Types.GetMethod(ctx.Types.Math, "Log2", ctx.Types.Double),
            "log1p" => ctx.Types.GetMethod(typeof(System.Math), "Log", ctx.Types.Double), // see Log1p special case below
            "expm1" => ctx.Types.GetMethod(typeof(System.Math), "Exp", ctx.Types.Double), // see Expm1 special case below
            _ => null
        };

        if (mathMethod != null)
        {
            // log1p/expm1 need pre/post adjustments + zero-sign preservation:
            //   log1p(x) = log(x + 1); spec: log1p(-0) = -0.
            //   expm1(x) = exp(x) - 1; spec: expm1(-0) = -0.
            // The naive log(x+1)/exp(x)-1 implementation loses the sign of zero.
            // Stash arg, short-circuit if x == 0 returning x as-is.
            if (methodName == "log1p" || methodName == "expm1")
            {
                var argLocal = il.DeclareLocal(ctx.Types.Double);
                il.Emit(OpCodes.Stloc, argLocal);
                var endLabel = il.DefineLabel();
                var notZeroLabel = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, argLocal);
                il.Emit(OpCodes.Ldc_R8, 0.0);
                il.Emit(OpCodes.Bne_Un, notZeroLabel);
                il.Emit(OpCodes.Ldloc, argLocal);
                il.Emit(OpCodes.Br, endLabel);
                il.MarkLabel(notZeroLabel);
                il.Emit(OpCodes.Ldloc, argLocal);
                if (methodName == "log1p")
                {
                    il.Emit(OpCodes.Ldc_R8, 1.0);
                    il.Emit(OpCodes.Add);
                }
                il.Emit(OpCodes.Call, mathMethod);
                if (methodName == "expm1")
                {
                    il.Emit(OpCodes.Ldc_R8, 1.0);
                    il.Emit(OpCodes.Sub);
                }
                il.MarkLabel(endLabel);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;
            }
            // Math.pow(base, exp): two ECMA-262 spec quirks vs .NET Math.Pow:
            //   1) If exp is NaN, return NaN (.NET returns 1 for Pow(1, NaN)).
            //   2) If abs(base) == 1 and abs(exp) == +∞, return NaN (.NET
            //      follows IEEE-754 fma which returns 1).
            // Stack on entry: [base, exp].
            if (methodName == "pow")
            {
                var expLocal = il.DeclareLocal(ctx.Types.Double);
                var baseLocal = il.DeclareLocal(ctx.Types.Double);
                il.Emit(OpCodes.Stloc, expLocal);
                il.Emit(OpCodes.Stloc, baseLocal);
                var notNaNExpLabel = il.DefineLabel();
                var endLabel = il.DefineLabel();
                // Quirk 1: NaN exponent → NaN.
                il.Emit(OpCodes.Ldloc, expLocal);
                il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.Double, "IsNaN", ctx.Types.Double));
                il.Emit(OpCodes.Brfalse, notNaNExpLabel);
                il.Emit(OpCodes.Ldc_R8, double.NaN);
                il.Emit(OpCodes.Br, endLabel);
                il.MarkLabel(notNaNExpLabel);
                // Quirk 2: |base| == 1 && IsInfinity(exp) → NaN.
                var notUnitInfLabel = il.DefineLabel();
                var absBaseLocal = il.DeclareLocal(ctx.Types.Double);
                il.Emit(OpCodes.Ldloc, baseLocal);
                il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.Math, "Abs", ctx.Types.Double));
                il.Emit(OpCodes.Stloc, absBaseLocal);
                il.Emit(OpCodes.Ldloc, absBaseLocal);
                il.Emit(OpCodes.Ldc_R8, 1.0);
                il.Emit(OpCodes.Bne_Un, notUnitInfLabel);
                il.Emit(OpCodes.Ldloc, expLocal);
                il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.Double, "IsInfinity", ctx.Types.Double));
                il.Emit(OpCodes.Brfalse, notUnitInfLabel);
                il.Emit(OpCodes.Ldc_R8, double.NaN);
                il.Emit(OpCodes.Br, endLabel);
                il.MarkLabel(notUnitInfLabel);
                il.Emit(OpCodes.Ldloc, baseLocal);
                il.Emit(OpCodes.Ldloc, expLocal);
                il.Emit(OpCodes.Call, mathMethod);
                il.MarkLabel(endLabel);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;
            }
            il.Emit(OpCodes.Call, mathMethod);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        // Math.hypot(...args) — sqrt(sum(a_i^2)) per spec.
        // ECMA-262 21.3.2.16: Infinity check fires BEFORE NaN check, so
        // Math.hypot(NaN, Infinity) returns Infinity (not NaN). Stack on
        // entry: [arg0, arg1, ..., argN-1] (each a double).
        if (methodName == "hypot")
        {
            if (arguments.Count == 0)
            {
                il.Emit(OpCodes.Ldc_R8, 0.0);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;
            }
            // Stash all args into locals; deepest arg on bottom of stack → pop reversed.
            var argLocals = new LocalBuilder[arguments.Count];
            for (int i = arguments.Count - 1; i >= 0; i--)
            {
                argLocals[i] = il.DeclareLocal(ctx.Types.Double);
                il.Emit(OpCodes.Stloc, argLocals[i]);
            }
            // First pass: any infinity → return +Infinity.
            // Use Math.Abs(x) > 0 to detect both +∞ and -∞ via comparison with double.MaxValue.
            // Simpler: check IsInfinity directly per arg.
            var endLabel = il.DefineLabel();
            var notInfLabel = il.DefineLabel();
            for (int i = 0; i < arguments.Count; i++)
            {
                il.Emit(OpCodes.Ldloc, argLocals[i]);
                il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.Double, "IsInfinity", ctx.Types.Double));
                var notThisInfLabel = il.DefineLabel();
                il.Emit(OpCodes.Brfalse, notThisInfLabel);
                il.Emit(OpCodes.Ldc_R8, double.PositiveInfinity);
                il.Emit(OpCodes.Br, endLabel);
                il.MarkLabel(notThisInfLabel);
            }
            // No arg was Infinity. Compute sum-of-squares: Math.Sqrt(NaN) = NaN
            // automatically propagates if any arg is NaN.
            il.Emit(OpCodes.Ldloc, argLocals[0]);
            il.Emit(OpCodes.Ldloc, argLocals[0]);
            il.Emit(OpCodes.Mul);
            for (int i = 1; i < arguments.Count; i++)
            {
                il.Emit(OpCodes.Ldloc, argLocals[i]);
                il.Emit(OpCodes.Ldloc, argLocals[i]);
                il.Emit(OpCodes.Mul);
                il.Emit(OpCodes.Add);
            }
            il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.Math, "Sqrt", ctx.Types.Double));
            il.MarkLabel(endLabel);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        // Math.fround(x) — round to nearest float32 then back to double.
        if (methodName == "fround" && arguments.Count == 1)
        {
            il.Emit(OpCodes.Conv_R4);
            il.Emit(OpCodes.Conv_R8);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        // Math.f16round(x) — round to nearest binary16 (Float16) then back to
        // double. Per Float16Array proposal in ECMA-262. Uses System.Half's
        // double→Half (op_Explicit) + Half→double (op_Explicit returning double)
        // conversions. Both directions are op_Explicit on Half; need to filter
        // by signature to disambiguate.
        if (methodName == "f16round" && arguments.Count == 1)
        {
            var halfFromDouble = typeof(System.Half).GetMethods()
                .First(m => m.Name == "op_Explicit"
                            && m.ReturnType == typeof(System.Half)
                            && m.GetParameters().Length == 1
                            && m.GetParameters()[0].ParameterType == typeof(double));
            var doubleFromHalf = typeof(System.Half).GetMethods()
                .First(m => m.Name == "op_Explicit"
                            && m.ReturnType == typeof(double)
                            && m.GetParameters().Length == 1
                            && m.GetParameters()[0].ParameterType == typeof(System.Half));
            il.Emit(OpCodes.Call, halfFromDouble);
            il.Emit(OpCodes.Call, doubleFromHalf);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        // Math.clz32(x) — count leading zeros of ToUint32(x).
        // ECMA-262 21.3.2.7: ToUint32 modular-reduces the truncated value to
        // [0, 2^32). Conv_U4 from double is undefined for values outside
        // [0, 2^32); JsToInt32 handles NaN/Infinity → 0 + reduction. Cast
        // its int32 result to uint32 by reinterpretation (same bit pattern).
        if (methodName == "clz32" && arguments.Count == 1)
        {
            // Stack: [double]. Box, JsToInt32 → int32, then uint32 view.
            il.Emit(OpCodes.Box, ctx.Types.Double);
            il.Emit(OpCodes.Call, ctx.Runtime!.JsToInt32);
            // int32 → uint32 reinterpret (no-op at IL level)
            il.Emit(OpCodes.Call, typeof(System.Numerics.BitOperations).GetMethod("LeadingZeroCount", [typeof(uint)])!);
            il.Emit(OpCodes.Conv_R8);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        // Math.imul(a, b) — multiply two 32-bit ints, return as int32.
        // ECMA-262 21.3.2.18: a = ToInt32(x), b = ToInt32(y); result = (a*b) mod 2^32
        // returned as int32. Conv_I4 from double is undefined for values outside
        // [-2^31, 2^31); JsToInt32 handles NaN/Infinity → 0 and modular reduction
        // properly. Box/JsToInt32 round-trip is acceptable for spec-correctness.
        if (methodName == "imul" && arguments.Count == 2)
        {
            // Stack: [a_double, b_double]. Stash b → local; box a, call JsToInt32 → int32 a.
            var bDoubleLocal = il.DeclareLocal(ctx.Types.Double);
            il.Emit(OpCodes.Stloc, bDoubleLocal);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            il.Emit(OpCodes.Call, ctx.Runtime!.JsToInt32);
            // Stack: [a_int32]. Box b, call JsToInt32 → int32 b.
            il.Emit(OpCodes.Ldloc, bDoubleLocal);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            il.Emit(OpCodes.Call, ctx.Runtime!.JsToInt32);
            // Stack: [a_int32, b_int32]. Multiply (Mul wraps mod 2^32).
            il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Conv_R8);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to emit IL for bare access to a Math static member without
    /// a call — data constants (<c>PI</c>, <c>E</c>) and method references
    /// (<c>var f = Math.floor</c>). Method references emit a
    /// <c>$TSFunction</c> wrapping the matching <c>$Runtime</c> adapter so
    /// subsequent invocations dispatch correctly. See issue #60 for the
    /// motivating lodash pattern (<c>var nativeMax = Math.max, …</c> at IIFE
    /// init).
    /// </summary>
    public bool TryEmitStaticPropertyGet(IEmitterContext emitter, string propertyName)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // ECMA-262 21.3.1: numeric constants on the Math object.
        switch (propertyName)
        {
            case "PI":
                il.Emit(OpCodes.Ldc_R8, Math.PI);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;
            case "E":
                il.Emit(OpCodes.Ldc_R8, Math.E);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;
            case "LN10":
                il.Emit(OpCodes.Ldc_R8, Math.Log(10.0));
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;
            case "LN2":
                il.Emit(OpCodes.Ldc_R8, Math.Log(2.0));
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;
            case "LOG10E":
                il.Emit(OpCodes.Ldc_R8, 1.0 / Math.Log(10.0));
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;
            case "LOG2E":
                il.Emit(OpCodes.Ldc_R8, 1.0 / Math.Log(2.0));
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;
            case "SQRT2":
                il.Emit(OpCodes.Ldc_R8, Math.Sqrt(2.0));
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;
            case "SQRT1_2":
                il.Emit(OpCodes.Ldc_R8, Math.Sqrt(0.5));
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;
        }

        var runtime = ctx.Runtime!;
        // Stage 4z5: tuple of (adapter, jsName, jsLength) so .name reports
        // the JS-spec name (lowercase) instead of the .NET adapter method
        // name (e.g. "MathFloorAdapter") and .length reports the spec length.
        // Spec lengths from ECMA-262 21.3.2:
        //   pow/atan2/imul/max/min ... → 2
        //   hypot → 2 (per spec); others → 1; random → 0.
        (MethodInfo? adapter, int len) info = propertyName switch
        {
            "floor"  => (runtime.MathFloorAdapter, 1),
            "ceil"   => (runtime.MathCeilAdapter, 1),
            "abs"    => (runtime.MathAbsAdapter, 1),
            "sqrt"   => (runtime.MathSqrtAdapter, 1),
            "round"  => (runtime.MathRoundAdapter, 1),
            "trunc"  => (runtime.MathTruncAdapter, 1),
            "sign"   => (runtime.MathSignAdapter, 1),
            "sin"    => (runtime.MathSinAdapter, 1),
            "cos"    => (runtime.MathCosAdapter, 1),
            "tan"    => (runtime.MathTanAdapter, 1),
            "log"    => (runtime.MathLogAdapter, 1),
            "exp"    => (runtime.MathExpAdapter, 1),
            "pow"    => (runtime.MathPowAdapter, 2),
            "max"    => (runtime.MathMaxAdapter, 2),
            "min"    => (runtime.MathMinAdapter, 2),
            "random" => (runtime.Random, 0),
            "asin"   => (runtime.MathAsinAdapter, 1),
            "acos"   => (runtime.MathAcosAdapter, 1),
            "atan"   => (runtime.MathAtanAdapter, 1),
            "atan2"  => (runtime.MathAtan2Adapter, 2),
            "sinh"   => (runtime.MathSinhAdapter, 1),
            "cosh"   => (runtime.MathCoshAdapter, 1),
            "tanh"   => (runtime.MathTanhAdapter, 1),
            "asinh"  => (runtime.MathAsinhAdapter, 1),
            "acosh"  => (runtime.MathAcoshAdapter, 1),
            "atanh"  => (runtime.MathAtanhAdapter, 1),
            "cbrt"   => (runtime.MathCbrtAdapter, 1),
            "log10"  => (runtime.MathLog10Adapter, 1),
            "log2"   => (runtime.MathLog2Adapter, 1),
            "log1p"  => (runtime.MathLog1pAdapter, 1),
            "expm1"  => (runtime.MathExpm1Adapter, 1),
            "fround" => (runtime.MathFroundAdapter, 1),
            "clz32"  => (runtime.MathClz32Adapter, 1),
            "imul"   => (runtime.MathImulAdapter, 2),
            "hypot"  => (runtime.MathHypotAdapter, 2),
            _ => (null, 0)
        };
        if (info.adapter == null) return false;

        // new $TSFunction(null, MethodInfo_of(adapter), name, length) — uses
        // the WithCache ctor so .name reports the JS spec name.
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldtoken, info.adapter);
        il.Emit(OpCodes.Ldtoken, info.adapter.DeclaringType!);
        il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.MethodBase, "GetMethodFromHandle",
            ctx.Types.RuntimeMethodHandle, ctx.Types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, ctx.Types.MethodInfo);
        il.Emit(OpCodes.Ldstr, propertyName);
        il.Emit(OpCodes.Ldc_I4, info.len);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtorWithCache);
        return true;
    }

    public bool HasStaticProperty(string memberName) =>
        memberName is "PI" or "E" or "LN10" or "LN2" or "LOG10E" or "LOG2E"
            or "SQRT2" or "SQRT1_2"
            or "floor" or "ceil" or "abs" or "sqrt"
            or "round" or "trunc" or "sign" or "sin" or "cos" or "tan"
            or "log" or "exp" or "pow" or "max" or "min" or "random"
            or "asin" or "acos" or "atan" or "atan2" or "sinh" or "cosh" or "tanh"
            or "asinh" or "acosh" or "atanh" or "cbrt" or "log10" or "log2"
            or "log1p" or "expm1" or "fround" or "clz32" or "imul" or "hypot"
            or "f16round";
}
