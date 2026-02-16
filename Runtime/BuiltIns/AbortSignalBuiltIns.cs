using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Provides member dispatch for AbortSignal instances and static methods.
/// </summary>
public static class AbortSignalBuiltIns
{
    /// <summary>
    /// Gets an instance member from an AbortSignal.
    /// </summary>
    public static object? GetMember(SharpTSAbortSignal receiver, string name)
    {
        return receiver.GetMember(name);
    }

    /// <summary>
    /// Gets a static method from the AbortSignal namespace.
    /// </summary>
    public static ISharpTSCallable? GetStaticMethod(string name)
    {
        return name switch
        {
            "abort" => new BuiltInMethod("abort", 0, 1, (_, _, args) =>
            {
                var reason = args.Count > 0 ? args[0] : null;
                return SharpTSAbortSignal.Abort(reason);
            }),
            "timeout" => new BuiltInMethod("timeout", 1, (_, _, args) =>
            {
                var ms = args[0] is double d ? d : throw new Exception("Runtime Error: AbortSignal.timeout requires a number argument");
                return SharpTSAbortSignal.Timeout(ms);
            }),
            "any" => new BuiltInMethod("any", 1, (_, _, args) =>
            {
                if (args[0] is not SharpTSArray signalArray)
                    throw new Exception("Runtime Error: AbortSignal.any requires an array of AbortSignal instances");

                var signals = signalArray.Elements
                    .Where(e => e is SharpTSAbortSignal)
                    .Cast<SharpTSAbortSignal>();
                return SharpTSAbortSignal.Any(signals);
            }),
            _ => null
        };
    }
}
