using SharpTS.Execution;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Provides implementations for JavaScript Math object members.
/// Uses RuntimeValue (V2) for zero-boxing primitive operations.
/// </summary>
public static class MathBuiltIns
{
    private static readonly Random _random = new();

    private static readonly BuiltInStaticMemberLookup _lookup =
        BuiltInStaticBuilder.Create()
            // Constants
            .Constant("PI", Math.PI)
            .Constant("E", Math.E)
            // Single argument methods (V2 — no boxing)
            .MethodV2("abs", 1, (_, _, args) =>
                RuntimeValue.FromNumber(Math.Abs(args[0].AsNumber())))
            .MethodV2("floor", 1, (_, _, args) =>
                RuntimeValue.FromNumber(Math.Floor(args[0].AsNumber())))
            .MethodV2("ceil", 1, (_, _, args) =>
                RuntimeValue.FromNumber(Math.Ceiling(args[0].AsNumber())))
            .MethodV2("round", 1, (_, _, args) =>
                RuntimeValue.FromNumber(Math.Floor(args[0].AsNumber() + 0.5)))
            .MethodV2("sqrt", 1, (_, _, args) =>
                RuntimeValue.FromNumber(Math.Sqrt(args[0].AsNumber())))
            .MethodV2("sin", 1, (_, _, args) =>
                RuntimeValue.FromNumber(Math.Sin(args[0].AsNumber())))
            .MethodV2("cos", 1, (_, _, args) =>
                RuntimeValue.FromNumber(Math.Cos(args[0].AsNumber())))
            .MethodV2("tan", 1, (_, _, args) =>
                RuntimeValue.FromNumber(Math.Tan(args[0].AsNumber())))
            .MethodV2("log", 1, (_, _, args) =>
                RuntimeValue.FromNumber(Math.Log(args[0].AsNumber())))
            .MethodV2("exp", 1, (_, _, args) =>
                RuntimeValue.FromNumber(Math.Exp(args[0].AsNumber())))
            .MethodV2("sign", 1, (_, _, args) =>
                RuntimeValue.FromNumber(Math.Sign(args[0].AsNumber())))
            .MethodV2("trunc", 1, (_, _, args) =>
                RuntimeValue.FromNumber(Math.Truncate(args[0].AsNumber())))
            // Two argument methods
            .MethodV2("pow", 2, (_, _, args) =>
                RuntimeValue.FromNumber(Math.Pow(args[0].AsNumber(), args[1].AsNumber())))
            // Variable arity methods
            .MethodV2("min", 2, int.MaxValue, Min)
            .MethodV2("max", 2, int.MaxValue, Max)
            // No argument methods
            .MethodV2("random", 0, (_, _, _) =>
                RuntimeValue.FromNumber(_random.NextDouble()))
            .Build();

    public static object? GetMember(string name)
        => _lookup.GetMember(name);

    private static RuntimeValue Min(Interpreter _, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0) return RuntimeValue.FromNumber(double.PositiveInfinity);

        double min = double.PositiveInfinity;
        for (int i = 0; i < args.Length; i++)
        {
            double val = args[i].AsNumber();
            if (val < min) min = val;
        }
        return RuntimeValue.FromNumber(min);
    }

    private static RuntimeValue Max(Interpreter _, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0) return RuntimeValue.FromNumber(double.NegativeInfinity);

        double max = double.NegativeInfinity;
        for (int i = 0; i < args.Length; i++)
        {
            double val = args[i].AsNumber();
            if (val > max) max = val;
        }
        return RuntimeValue.FromNumber(max);
    }
}
