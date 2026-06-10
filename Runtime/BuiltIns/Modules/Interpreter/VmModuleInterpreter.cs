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
            ["runInNewContext"] = BuiltInMethod.CreateV2("runInNewContext", 1, 3, RunInNewContext),
            ["runInThisContext"] = BuiltInMethod.CreateV2("runInThisContext", 1, 2, RunInThisContext),
            ["createContext"] = BuiltInMethod.CreateV2("createContext", 0, 1, CreateContext),
            ["isContext"] = BuiltInMethod.CreateV2("isContext", 1, 1, IsContext),
            ["compileFunction"] = BuiltInMethod.CreateV2("compileFunction", 1, 3, CompileFunction),
            ["Script"] = new VmScriptConstructor()
        };
    }

    /// <summary>
    /// vm.runInNewContext(code, contextObject?, options?) — executes code in a fresh, isolated context.
    /// Context properties become variables; mutations are written back.
    /// </summary>
    private static RuntimeValue RunInNewContext(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || args[0].ToObject() is not string code)
            throw new Exception("vm.runInNewContext requires a code string");

        var contextObject = args.Length > 1 ? args[1].ToObject() : null;
        var timeout = GetTimeoutOption(args.Length > 2 ? args[2].ToObject() : null);
        return RuntimeValue.FromBoxed(ExecuteInNewContext(code, contextObject, interpreter, timeout));
    }

    /// <summary>
    /// vm.runInThisContext(code, options?) — executes code in the caller's scope.
    /// </summary>
    private static RuntimeValue RunInThisContext(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || args[0].ToObject() is not string code)
            throw new Exception("vm.runInThisContext requires a code string");

        var timeout = GetTimeoutOption(args.Length > 1 ? args[1].ToObject() : null);
        return RuntimeValue.FromBoxed(ExecuteInCurrentContext(code, interpreter, timeout));
    }

    /// <summary>
    /// vm.createContext(contextObject?) — tags an object as a vm context.
    /// </summary>
    private static RuntimeValue CreateContext(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var contextObject = args.Length > 0 ? args[0].ToObject() : null;
        return RuntimeValue.FromObject(VmContext.Create(contextObject));
    }

    /// <summary>
    /// vm.isContext(obj) — returns whether an object has been contextified.
    /// </summary>
    private static RuntimeValue IsContext(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var obj = args.Length > 0 ? args[0].ToObject() : null;
        return RuntimeValue.FromBoolean(VmContext.IsContext(obj));
    }

    /// <summary>
    /// Executes code in a fresh interpreter with context object properties seeded as variables.
    /// Mutations to context variables are written back to the context object.
    /// </summary>
    internal static object? ExecuteInNewContext(string code, object? contextObject, Interp? parentInterpreter = null, int timeout = -1)
    {
        // Parse the code
        var statements = ParseCode(code);

        // Extract context properties
        var contextProps = VmContext.ExtractProperties(contextObject);

        // Create fresh interpreter inheriting parent's output writers
        var subInterpreter = parentInterpreter != null
            ? new Interp(parentInterpreter.Out, parentInterpreter.Error)
            : new Interp();
        var env = subInterpreter.Environment;

        foreach (var (key, value) in contextProps)
            env.Define(key, value);

        // Apply timeout if specified
        CancellationTokenSource? cts = null;
        if (timeout > 0)
        {
            cts = new CancellationTokenSource(timeout);
            subInterpreter.SetVmTimeoutToken(cts.Token);
        }

        try
        {
            // Resolve variables and execute
            var resolver = new VariableResolver(subInterpreter);
            resolver.Resolve(statements);

            var result = subInterpreter.InterpretRepl(statements);

            // Write mutations back to context object
            VmContext.WriteBack(contextObject, contextProps, subInterpreter.Environment);

            return result;
        }
        finally
        {
            subInterpreter.Dispose();
            cts?.Dispose();
        }
    }

    /// <summary>
    /// Executes code in the current interpreter's scope.
    /// </summary>
    internal static object? ExecuteInCurrentContext(string code, Interp interpreter, int timeout = -1)
    {
        var statements = ParseCode(code);

        var resolver = new VariableResolver(interpreter);
        resolver.Resolve(statements);

        CancellationTokenSource? cts = null;
        if (timeout > 0)
        {
            cts = new CancellationTokenSource(timeout);
            interpreter.SetVmTimeoutToken(cts.Token);
        }

        try
        {
            return interpreter.InterpretRepl(statements);
        }
        finally
        {
            if (cts != null)
            {
                interpreter.SetVmTimeoutToken(default);
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Executes pre-parsed statements in a new context.
    /// Used by Script.runInNewContext and Script.runInContext.
    /// </summary>
    internal static object? ExecuteParsedInNewContext(List<Stmt> statements, object? contextObject, Interp? parentInterpreter = null, int timeout = -1)
    {
        var contextProps = VmContext.ExtractProperties(contextObject);

        var subInterpreter = parentInterpreter != null
            ? new Interp(parentInterpreter.Out, parentInterpreter.Error)
            : new Interp();
        var env = subInterpreter.Environment;

        foreach (var (key, value) in contextProps)
            env.Define(key, value);

        CancellationTokenSource? cts = null;
        if (timeout > 0)
        {
            cts = new CancellationTokenSource(timeout);
            subInterpreter.SetVmTimeoutToken(cts.Token);
        }

        try
        {
            var resolver = new VariableResolver(subInterpreter);
            resolver.Resolve(statements);

            var result = subInterpreter.InterpretRepl(statements);

            VmContext.WriteBack(contextObject, contextProps, subInterpreter.Environment);

            return result;
        }
        finally
        {
            subInterpreter.Dispose();
            cts?.Dispose();
        }
    }

    /// <summary>
    /// Executes pre-parsed statements in the current interpreter's scope.
    /// Used by Script.runInThisContext.
    /// </summary>
    internal static object? ExecuteParsedInCurrentContext(List<Stmt> statements, Interp interpreter, int timeout = -1)
    {
        var resolver = new VariableResolver(interpreter);
        resolver.Resolve(statements);

        CancellationTokenSource? cts = null;
        if (timeout > 0)
        {
            cts = new CancellationTokenSource(timeout);
            interpreter.SetVmTimeoutToken(cts.Token);
        }

        try
        {
            return interpreter.InterpretRepl(statements);
        }
        finally
        {
            if (cts != null)
            {
                interpreter.SetVmTimeoutToken(default);
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// vm.compileFunction(code, params?, options?) — compiles a function body with named parameters.
    /// Returns a callable function. Equivalent to new Function(params, code) with vm context control.
    /// </summary>
    private static RuntimeValue CompileFunction(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || args[0].ToObject() is not string code)
            throw new Exception("vm.compileFunction requires a code string");

        // Extract params array (string[])
        var paramNames = new List<string>();
        if (args.Length > 1 && !args[1].IsNull)
        {
            IEnumerable<object?> items;
            if (args[1].ToObject() is List<object?> paramList)
                items = paramList;
            else if (args[1].ToObject() is SharpTSArray paramArray)
                items = paramArray;
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

        if (args.Length > 2 && !args[2].IsNull)
        {
            var options = VmContext.ExtractProperties(args[2].ToObject());
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
                    contextExtensions = extArray.ToList();
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
        var compiledFn = BuiltInMethod.CreateV2("compiledFunction", 0, paramNames.Count,
            (interp, recv, callArgs) =>
            {
                var boxedCallArgs = new List<object?>(callArgs.Length);
                for (int i = 0; i < callArgs.Length; i++)
                    boxedCallArgs.Add(callArgs[i].ToObject());
                return RuntimeValue.FromBoxed(ExecuteCompiledFunction(funcDeclStatements, paramNames, boxedCallArgs, parsingContext, contextExtensions, interp));
            });

        return RuntimeValue.FromObject(compiledFn);
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
        List<object?>? contextExtensions,
        Interp? parentInterpreter = null)
    {
        var subInterpreter = parentInterpreter != null
            ? new Interp(parentInterpreter.Out, parentInterpreter.Error)
            : new Interp();
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
    /// Extracts the timeout option (in milliseconds) from an options object.
    /// Returns -1 if not specified.
    /// </summary>
    internal static int GetTimeoutOption(object? options)
    {
        if (options is SharpTSObject obj)
        {
            var val = obj.GetProperty("timeout");
            if (val is double d && d > 0)
                return (int)d;
        }
        else if (options is Dictionary<string, object?> dict)
        {
            if (dict.TryGetValue("timeout", out var val) && val is double d && d > 0)
                return (int)d;
        }
        return -1;
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
        var runInNewCtx = BuiltInMethod.CreateV2("runInNewContext", 0, 2,
            (interp, recv, methodArgs) =>
            {
                var contextObject = methodArgs.Length > 0 ? methodArgs[0].ToObject() : null;
                var timeout = VmModuleInterpreter.GetTimeoutOption(methodArgs.Length > 1 ? methodArgs[1].ToObject() : null);
                return RuntimeValue.FromBoxed(VmModuleInterpreter.ExecuteParsedInNewContext(statements, contextObject, interp, timeout));
            });

        var runInThisCtx = BuiltInMethod.CreateV2("runInThisContext", 0, 1,
            (interp, recv, methodArgs) =>
            {
                var timeout = VmModuleInterpreter.GetTimeoutOption(methodArgs.Length > 0 ? methodArgs[0].ToObject() : null);
                if (interp != null)
                    return RuntimeValue.FromBoxed(VmModuleInterpreter.ExecuteParsedInCurrentContext(statements, interp, timeout));
                // Compiled mode: no interpreter available, fall back to new context
                return RuntimeValue.FromBoxed(VmModuleInterpreter.ExecuteParsedInNewContext(statements, null, interp, timeout));
            });

        var runInCtx = BuiltInMethod.CreateV2("runInContext", 1, 2,
            (interp, recv, methodArgs) =>
            {
                var context = methodArgs.Length > 0 ? methodArgs[0].ToObject() : null;
                var timeout = VmModuleInterpreter.GetTimeoutOption(methodArgs.Length > 1 ? methodArgs[1].ToObject() : null);
                return RuntimeValue.FromBoxed(VmModuleInterpreter.ExecuteParsedInNewContext(statements, context, interp, timeout));
            });

        // Return as Dictionary<string, object?> — works in both modes:
        // Interpreter: EvaluateGetOnFallback handles IDictionary<string, object?>
        // Compiled: GetFieldsProperty handles Dictionary<string, object?>
        return new Dictionary<string, object?>
        {
            ["runInNewContext"] = runInNewCtx,
            ["runInThisContext"] = runInThisCtx,
            ["runInContext"] = runInCtx,
        };
    }
}
