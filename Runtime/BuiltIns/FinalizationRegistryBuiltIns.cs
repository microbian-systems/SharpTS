using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Built-in method implementations for FinalizationRegistry instances.
/// </summary>
public static class FinalizationRegistryBuiltIns
{
    public static object? GetMember(SharpTSFinalizationRegistry receiver, string name)
    {
        return name switch
        {
            "register" => new BuiltInMethod("register", 1, 3, (interpreter, recv, args) =>
            {
                var registry = (SharpTSFinalizationRegistry)recv!;
                var target = args.Count > 0 ? args[0] : null;
                var heldValue = args.Count > 1 ? args[1] : null;
                var token = args.Count > 2 ? args[2] : null;
                registry.Register(target!, heldValue, token);
                return null;
            }),

            "unregister" => new BuiltInMethod("unregister", 1, (interpreter, recv, args) =>
            {
                var registry = (SharpTSFinalizationRegistry)recv!;
                var token = args.Count > 0 ? args[0] : null;
                return registry.Unregister(token);
            }),

            _ => null
        };
    }
}
