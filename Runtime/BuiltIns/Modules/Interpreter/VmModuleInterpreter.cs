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
            ["compileFunction"] = new BuiltInMethod("compileFunction", 1, 3, CompileFunction),
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
    /// vm.compileFunction(code, params?, options?) — compiles a function body with named parameters.
    /// Returns a callable function. Equivalent to new Function(params, code) with vm context control.
    /// </summary>
    private static object? CompileFunction(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count == 0 || args[0] is not string code)
            throw new Exception("vm.compileFunction requires a code string");

        // Extract params array (string[])
        var paramNames = new List<string>();
        if (args.Count > 1 && args[1] != null)
        {
            IEnumerable<object?> items;
            if (args[1] is List<object?> paramList)
                items = paramList;
            else if (args[1] is SharpTSArray paramArray)
                items = paramArray.Elements;
            else
                items = [];

            foreach (var p in items)
            {
                if (p is string name)
                    paramNames.Add(name);
                else
                    throw new Exception("SyntaxError: Invalid parameter name");
            }
        }

        // Validate parameter names are valid identifiers
        foreach (var name in paramNames)
        {
            if (string.IsNullOrEmpty(name) || !IsValidIdentifier(name))
                throw new Exception($"SyntaxError: Invalid parameter name '{name}'");
        }

        // Extract options
        object? parsingContext = null;
        List<object?>? contextExtensions = null;

        if (args.Count > 2 && args[2] != null)
        {
            var options = VmContext.ExtractProperties(args[2]);
            if (options.TryGetValue("parsingContext", out var ctx))
            {
                if (ctx != null && !VmContext.IsContext(ctx))
                    throw new Exception("TypeError: parsingContext must be a vm.createContext()-ed object");
                parsingContext = ctx;
            }
            if (options.TryGetValue("contextExtensions", out var ext))
            {
                if (ext is List<object?> extList)
                    contextExtensions = extList;
                else if (ext is SharpTSArray extArray)
                    contextExtensions = extArray.Elements.ToList();
            }
        }

        // Wrap code as function body and parse
        // Ensure code ends with semicolon since the parser requires them
        var trimmedCode = code.TrimEnd();
        if (trimmedCode.Length > 0 && !trimmedCode.EndsWith(';') && !trimmedCode.EndsWith('}'))
            trimmedCode += ";";
        var paramsStr = string.Join(", ", paramNames);
        var wrappedCode = $"function __vmfn__({paramsStr}) {{ {trimmedCode} }}";
        var funcDeclStatements = ParseCode(wrappedCode);

        // Return a callable that executes the function body in a fresh interpreter
        var compiledFn = new BuiltInMethod("compiledFunction", 0, paramNames.Count,
            (interp, recv, callArgs) =>
            {
                return ExecuteCompiledFunction(funcDeclStatements, paramNames, callArgs, parsingContext, contextExtensions);
            });

        return compiledFn;
    }

    /// <summary>
    /// Executes a compiled function by creating a fresh interpreter,
    /// seeding context/extensions/params, and running the function body.
    /// </summary>
    private static object? ExecuteCompiledFunction(
        List<Stmt> funcDeclStatements,
        List<string> paramNames,
        List<object?> callArgs,
        object? parsingContext,
        List<object?>? contextExtensions)
    {
        var subInterpreter = new Interp();
        var env = subInterpreter.Environment;

        // Seed parsingContext variables
        if (parsingContext != null)
        {
            var contextProps = VmContext.ExtractProperties(parsingContext);
            foreach (var (key, value) in contextProps)
                env.Define(key, value);
        }

        // Seed contextExtensions (layered, in order)
        if (contextExtensions != null)
        {
            foreach (var ext in contextExtensions)
            {
                if (ext != null)
                {
                    var extProps = VmContext.ExtractProperties(ext);
                    foreach (var (key, value) in extProps)
                        env.Define(key, value);
                }
            }
        }

        // Define temporary arg variables and build the call expression
        var argPlaceholders = new List<string>();
        for (int i = 0; i < paramNames.Count; i++)
        {
            var placeholder = $"__vmarg{i}__";
            argPlaceholders.Add(placeholder);
            var argVal = i < callArgs.Count ? callArgs[i] : null;
            env.Define(placeholder, argVal);
        }

        // Build code: define function then call it
        // The funcDeclStatements contain the function declaration; we combine it with a call
        var callCode = $"__vmfn__({string.Join(", ", argPlaceholders)});";
        var callStatements = ParseCode(callCode);

        var allStatements = new List<Stmt>(funcDeclStatements);
        allStatements.AddRange(callStatements);

        var resolver = new VariableResolver(subInterpreter);
        resolver.Resolve(allStatements);
        var result = subInterpreter.InterpretRepl(allStatements);

        subInterpreter.Dispose();
        return result;
    }

    /// <summary>
    /// Validates that a string is a valid JavaScript identifier.
    /// </summary>
    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_' && name[0] != '$') return false;
        for (int i = 1; i < name.Length; i++)
        {
            if (!char.IsLetterOrDigit(name[i]) && name[i] != '_' && name[i] != '$') return false;
        }
        return true;
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
