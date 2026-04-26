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
            // JavaScript rounds half-values toward +infinity: Math.Floor(x + 0.5)
            il.Emit(OpCodes.Ldc_R8, 0.5);
            il.Emit(OpCodes.Add);
            var floorMethod = ctx.Types.GetMethod(ctx.Types.Math, "Floor", ctx.Types.Double);
            il.Emit(OpCodes.Call, floorMethod);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        if (methodName == "sign")
        {
            // Math.Sign returns int, need to convert to double
            var signMethod = ctx.Types.GetMethod(ctx.Types.Math, "Sign", ctx.Types.Double);
            il.Emit(OpCodes.Call, signMethod);
            il.Emit(OpCodes.Conv_R8); // Convert int to double
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
            // log1p/expm1 need pre/post adjustments (log(x+1) and exp(x)-1).
            // We hijack log/exp's MethodInfo above and patch around the call here.
            if (methodName == "log1p")
            {
                // log1p(x) = log(x + 1)
                il.Emit(OpCodes.Ldc_R8, 1.0);
                il.Emit(OpCodes.Add);
            }
            il.Emit(OpCodes.Call, mathMethod);
            if (methodName == "expm1")
            {
                // expm1(x) = exp(x) - 1
                il.Emit(OpCodes.Ldc_R8, 1.0);
                il.Emit(OpCodes.Sub);
            }
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        // Math.hypot(...args) — sqrt(sum(a_i^2)) per spec, with special-cases for
        // Infinity/NaN. Stack on entry: [arg0, arg1, ..., argN-1] (each a double).
        // Plan: pop each into a local, square it, sum them, sqrt the sum. Locals
        // avoid the IL-stack-shuffle gymnastics that broke an earlier in-place
        // version (squaring "the arg on top" misread the loop's stack shape and
        // produced sum-of-cross-products instead of sum-of-squares).
        if (methodName == "hypot")
        {
            if (arguments.Count == 0)
            {
                il.Emit(OpCodes.Ldc_R8, 0.0);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;
            }
            // Stash all args into locals; deepest arg is on the bottom of stack so
            // pop order is reverse (argN-1 first).
            var argLocals = new LocalBuilder[arguments.Count];
            for (int i = arguments.Count - 1; i >= 0; i--)
            {
                argLocals[i] = il.DeclareLocal(ctx.Types.Double);
                il.Emit(OpCodes.Stloc, argLocals[i]);
            }
            // sum = arg0 * arg0
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

        // Math.clz32(x) — count leading zeros of (x >>> 0) as 32-bit unsigned.
        // ECMA-262: ToUint32(x), then count leading zero bits; 32 if x === 0.
        if (methodName == "clz32" && arguments.Count == 1)
        {
            // Stack: [double]. Convert to uint via Conv_U4 (truncates fractional + handles NaN→0).
            il.Emit(OpCodes.Conv_U4);
            il.Emit(OpCodes.Call, typeof(System.Numerics.BitOperations).GetMethod("LeadingZeroCount", [typeof(uint)])!);
            il.Emit(OpCodes.Conv_R8);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        // Math.imul(a, b) — multiply two 32-bit ints, return as int32.
        if (methodName == "imul" && arguments.Count == 2)
        {
            // Stack: [a_double, b_double]
            // Convert both to int32, multiply, convert to double.
            il.Emit(OpCodes.Conv_I4);
            // Now: [a_double, b_int32]. Need a_int32. Use a helper local.
            var bLocal = il.DeclareLocal(ctx.Types.Int32);
            il.Emit(OpCodes.Stloc, bLocal);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Ldloc, bLocal);
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
        }

        var runtime = ctx.Runtime!;
        MethodInfo? adapter = propertyName switch
        {
            "floor"  => runtime.MathFloorAdapter,
            "ceil"   => runtime.MathCeilAdapter,
            "abs"    => runtime.MathAbsAdapter,
            "sqrt"   => runtime.MathSqrtAdapter,
            "round"  => runtime.MathRoundAdapter,
            "trunc"  => runtime.MathTruncAdapter,
            "sign"   => runtime.MathSignAdapter,
            "sin"    => runtime.MathSinAdapter,
            "cos"    => runtime.MathCosAdapter,
            "tan"    => runtime.MathTanAdapter,
            "log"    => runtime.MathLogAdapter,
            "exp"    => runtime.MathExpAdapter,
            "pow"    => runtime.MathPowAdapter,
            "max"    => runtime.MathMaxAdapter,
            "min"    => runtime.MathMinAdapter,
            "random" => runtime.Random,
            // Stage 4y: ES2015+ Math methods exposed as values.
            "asin"   => runtime.MathAsinAdapter,
            "acos"   => runtime.MathAcosAdapter,
            "atan"   => runtime.MathAtanAdapter,
            "atan2"  => runtime.MathAtan2Adapter,
            "sinh"   => runtime.MathSinhAdapter,
            "cosh"   => runtime.MathCoshAdapter,
            "tanh"   => runtime.MathTanhAdapter,
            "asinh"  => runtime.MathAsinhAdapter,
            "acosh"  => runtime.MathAcoshAdapter,
            "atanh"  => runtime.MathAtanhAdapter,
            "cbrt"   => runtime.MathCbrtAdapter,
            "log10"  => runtime.MathLog10Adapter,
            "log2"   => runtime.MathLog2Adapter,
            "log1p"  => runtime.MathLog1pAdapter,
            "expm1"  => runtime.MathExpm1Adapter,
            "fround" => runtime.MathFroundAdapter,
            "clz32"  => runtime.MathClz32Adapter,
            "imul"   => runtime.MathImulAdapter,
            "hypot"  => runtime.MathHypotAdapter,
            _ => null
        };
        if (adapter == null) return false;

        // new $TSFunction(null, MethodInfo_of(adapter)) — same shape as
        // ArrayStaticEmitter.TryEmitStaticPropertyGet. The two-arg
        // GetMethodFromHandle is required so the method token resolves
        // against the emitted $Runtime TypeBuilder in persisted DLLs.
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldtoken, adapter);
        il.Emit(OpCodes.Ldtoken, adapter.DeclaringType!);
        il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.MethodBase, "GetMethodFromHandle",
            ctx.Types.RuntimeMethodHandle, ctx.Types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, ctx.Types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        return true;
    }

    public bool HasStaticProperty(string memberName) =>
        memberName is "PI" or "E" or "floor" or "ceil" or "abs" or "sqrt"
            or "round" or "trunc" or "sign" or "sin" or "cos" or "tan"
            or "log" or "exp" or "pow" or "max" or "min" or "random"
            or "asin" or "acos" or "atan" or "atan2" or "sinh" or "cosh" or "tanh"
            or "asinh" or "acosh" or "atanh" or "cbrt" or "log10" or "log2"
            or "log1p" or "expm1" or "fround" or "clz32" or "imul" or "hypot";
}
