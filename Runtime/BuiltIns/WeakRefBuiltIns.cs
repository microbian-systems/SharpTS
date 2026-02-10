using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Built-in method implementations for WeakRef instances.
/// </summary>
public static class WeakRefBuiltIns
{
    public static object? GetMember(SharpTSWeakRef receiver, string name)
    {
        return name switch
        {
            "deref" => new BuiltInMethod("deref", 0, (_, recv, _) =>
            {
                var weakRef = (SharpTSWeakRef)recv!;
                return weakRef.Deref();
            }),

            _ => null
        };
    }
}
