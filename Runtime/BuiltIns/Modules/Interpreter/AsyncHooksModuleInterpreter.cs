using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of the Node.js 'async_hooks' module.
/// </summary>
public static class AsyncHooksModuleInterpreter
{
    /// <summary>
    /// Gets all exported values for the async_hooks module.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            ["AsyncLocalStorage"] = SharpTSAsyncLocalStorageConstructor.Instance
        };
    }
}
