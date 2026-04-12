namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Provides runtime values for the Node.js 'tty' built-in module (interpreter mode).
/// </summary>
public static class TtyModuleInterpreter
{
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            ["isatty"] = new BuiltInMethod("isatty", 1, (interp, recv, args) =>
            {
                var fd = Convert.ToInt32(args[0]);
                return fd switch
                {
                    0 => !Console.IsInputRedirected,
                    1 => !Console.IsOutputRedirected,
                    2 => !Console.IsErrorRedirected,
                    _ => false
                };
            })
        };
    }
}
