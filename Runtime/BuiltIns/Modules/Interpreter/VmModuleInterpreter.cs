using SharpTS.Diagnostics;
using SharpTS.Execution;
using SharpTS.Parsing;
using SharpTS.Runtime.Types;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of the Node.js 'vm' module.
/// Provides dynamic code compilation and execution within isolated contexts.
/// </summary>
public static class VmModuleInterpreter
{
    /// <summary>
    /// Gets all exported values for the vm module.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            ["runInNewContext"] = new BuiltInMethod("runInNewContext", 1, 3, RunInNewContext),
            ["runInThisContext"] = new BuiltInMethod("runInThisContext", 1, 2, RunInThisContext),
            ["createContext"] = new BuiltInMethod("createContext", 0, 1, CreateContext),
            ["isContext"] = new BuiltInMethod("isContext", 1, 1, IsContext),
            ["Script"] = new VmScriptConstructor()
        };
    }

    /// <summary>
    /// vm.runInNewContext(code, contextObject?, options?) — executes code in a fresh, isolated context.
    /// Context properties become variables; mutations are written back.
    /// </summary>
    private static object? RunInNewContext(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count == 0 || args[0] is not string code)
            throw new Exception("vm.runInNewContext requires a code string");

        var contextObject = args.Count > 1 ? args[1] : null;
        return ExecuteInNewContext(code, contextObject);
    }

    /// <summary>
    /// vm.runInThisContext(code, options?) — executes code in the caller's scope.
    /// </summary>
    private static object? RunInThisContext(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count == 0 || args[0] is not string code)
            throw new Exception("vm.runInThisContext requires a code string");

        return ExecuteInCurrentContext(code, interpreter);
    }

    /// <summary>
    /// vm.createContext(contextObject?) — tags an object as a vm context.
    /// </summary>
    private static object? CreateContext(Interp interpreter, object? receiver, List<object?> args)
    {
        var contextObject = args.Count > 0 ? args[0] : null;
        return VmContext.Create(contextObject);
    }

    /// <summary>
    /// vm.isContext(obj) — returns whether an object has been contextified.
    /// </summary>
    private static object? IsContext(Interp interpreter, object? receiver, List<object?> args)
    {
        var obj = args.Count > 0 ? args[0] : null;
        return VmContext.IsContext(obj);
    }

    /// <summary>
    /// Executes code in a fresh interpreter with context object properties seeded as variables.
    /// Mutations to context variables are written back to the context object.
    /// </summary>
    internal static object? ExecuteInNewContext(string code, object? contextObject)
    {
        // Parse the code
        var statements = ParseCode(code);

        // Extract context properties
        var contextProps = VmContext.ExtractProperties(contextObject);

        // Create fresh interpreter and seed environment
        var subInterpreter = new Interp();
        var env = subInterpreter.Environment;

        foreach (var (key, value) in contextProps)
            env.Define(key, value);

        // Resolve variables and execute
        var resolver = new VariableResolver(subInterpreter);
        resolver.Resolve(statements);

        var result = subInterpreter.InterpretRepl(statements);

        // Write mutations back to context object
        VmContext.WriteBack(contextObject, contextProps, subInterpreter.Environment);

        subInterpreter.Dispose();
        return result;
    }

    /// <summary>
    /// Executes code in the current interpreter's scope.
    /// </summary>
    internal static object? ExecuteInCurrentContext(string code, Interp interpreter)
    {
        var statements = ParseCode(code);

        var resolver = new VariableResolver(interpreter);
        resolver.Resolve(statements);

        return interpreter.InterpretRepl(statements);
    }

    /// <summary>
    /// Executes pre-parsed statements in a new context.
    /// Used by Script.runInNewContext and Script.runInContext.
    /// </summary>
    internal static object? ExecuteParsedInNewContext(List<Stmt> statements, object? contextObject)
    {
        var contextProps = VmContext.ExtractProperties(contextObject);

        var subInterpreter = new Interp();
        var env = subInterpreter.Environment;

        foreach (var (key, value) in contextProps)
            env.Define(key, value);

        var resolver = new VariableResolver(subInterpreter);
        resolver.Resolve(statements);

        var result = subInterpreter.InterpretRepl(statements);

        VmContext.WriteBack(contextObject, contextProps, subInterpreter.Environment);

        subInterpreter.Dispose();
        return result;
    }

    /// <summary>
    /// Executes pre-parsed statements in the current interpreter's scope.
    /// Used by Script.runInThisContext.
    /// </summary>
    internal static object? ExecuteParsedInCurrentContext(List<Stmt> statements, Interp interpreter)
    {
        var resolver = new VariableResolver(interpreter);
        resolver.Resolve(statements);
        return interpreter.InterpretRepl(statements);
    }

    /// <summary>
    /// Parses a code string into statements. Throws on parse errors.
    /// Follows the REPL pattern: retries with appended ";" on parse failure.
    /// </summary>
    internal static List<Stmt> ParseCode(string code)
    {
        var result = TryParse(code);

        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            // Retry with appended semicolon (same as REPL)
            var retryResult = TryParse(code + ";");
            var retryErrors = retryResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (retryErrors.Count == 0)
                return retryResult.Statements;

            throw new Exception($"SyntaxError: {errors[0].Message}");
        }

        return result.Statements;
    }

    private static ParseDiagnosticResult TryParse(string code)
    {
        var lexer = new Lexer(code);
        var tokens = lexer.ScanTokens();
        var parser = new Parser(tokens);
        return parser.Parse();
    }
}

/// <summary>
/// Constructor for vm.Script — pre-parses code for later execution.
/// </summary>
public sealed class VmScriptConstructor : ISharpTSCallable
{
    public int Arity() => 1;

    public object? Call(Interp interpreter, List<object?> args)
    {
        if (args.Count == 0 || args[0] is not string code)
            throw new Exception("vm.Script requires a code string");

        // Pre-parse the code (with semicolon retry)
        var statements = VmModuleInterpreter.ParseCode(code);

        // Create Script as a dictionary — works in both interpreter (via SharpTSObject wrapper)
        // and compiled mode (dictionary is native object representation).
        // Methods use BuiltInMethod (ISharpTSCallable for interpreter, has "Call" method for compiled mode).
        var runInNewCtx = new BuiltInMethod("runInNewContext", 0, 2,
            (interp, recv, methodArgs) =>
            {
                var contextObject = methodArgs.Count > 0 ? methodArgs[0] : null;
                return VmModuleInterpreter.ExecuteParsedInNewContext(statements, contextObject);
            });

        var runInThisCtx = new BuiltInMethod("runInThisContext", 0, 1,
            (interp, recv, methodArgs) =>
            {
                if (interp != null)
                    return VmModuleInterpreter.ExecuteParsedInCurrentContext(statements, interp);
                // Compiled mode: no interpreter available, fall back to new context
                return VmModuleInterpreter.ExecuteParsedInNewContext(statements, null);
            });

        var runInCtx = new BuiltInMethod("runInContext", 1, 2,
            (interp, recv, methodArgs) =>
            {
                var context = methodArgs.Count > 0 ? methodArgs[0] : null;
                return VmModuleInterpreter.ExecuteParsedInNewContext(statements, context);
            });

        // Return as SharpTSObject for interpreter (ISharpTSCallable dispatch)
        // and as Dictionary for compiled mode (GetFieldsProperty dispatch)
        var fields = new Dictionary<string, object?>
        {
            ["runInNewContext"] = runInNewCtx,
            ["runInThisContext"] = runInThisCtx,
            ["runInContext"] = runInCtx,
        };
        return new SharpTSObject(fields);
    }
}
