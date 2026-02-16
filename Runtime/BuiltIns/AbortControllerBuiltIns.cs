using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Provides member dispatch for AbortController instances.
/// </summary>
public static class AbortControllerBuiltIns
{
    public static object? GetMember(SharpTSAbortController receiver, string name)
    {
        return name switch
        {
            "signal" => receiver.Signal,
            "abort" => new BuiltInMethod("abort", 0, 1, (interp, _, args) =>
            {
                var reason = args.Count > 0 ? args[0] : null;
                receiver.Abort(reason, interp);
                return SharpTSUndefined.Instance;
            }),
            _ => null
        };
    }
}
