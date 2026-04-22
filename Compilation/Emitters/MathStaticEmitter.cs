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
                // Emit first argument
                emitter.EmitExpressionAsDouble(arguments[0]);
                // Chain remaining arguments with min/max calls
                for (int i = 1; i < arguments.Count; i++)
                {
                    emitter.EmitExpressionAsDouble(arguments[i]);
                    il.Emit(OpCodes.Call, minMaxMethod);
                }
            }
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return true;
        }

        // Emit all arguments as doubles
        foreach (var arg in arguments)
        {
            emitter.EmitExpressionAsDouble(arg);
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
            _ => null
        };

        if (mathMethod != null)
        {
            il.Emit(OpCodes.Call, mathMethod);
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
            or "log" or "exp" or "pow" or "max" or "min" or "random";
}
