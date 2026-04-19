using SharpTS.Runtime.Types;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of <c>primitive:readline</c>. Exposes
/// <c>questionSync(query)</c> and <c>createInterface(options)</c>; the TS
/// facade at <c>stdlib/node/readline.ts</c> wraps the returned Interface
/// instance and forwards method calls dynamically.
/// </summary>
public static class ReadlinePrimitiveInterpreter
{
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            ["questionSync"] = new BuiltInMethod("questionSync", 1, QuestionSync),
            ["createInterface"] = new BuiltInMethod("createInterface", 0, 1, CreateInterface)
        };
    }

    private static object? QuestionSync(Interp interpreter, object? receiver, List<object?> args)
    {
        var query = args.Count > 0 ? args[0]?.ToString() ?? "" : "";
        Console.Write(query);
        return Console.ReadLine() ?? "";
    }

    private static object? CreateInterface(Interp interpreter, object? receiver, List<object?> args)
    {
        var options = args.Count > 0 ? args[0] as SharpTSObject : null;
        return new SharpTSReadlineInterface(options);
    }
}
