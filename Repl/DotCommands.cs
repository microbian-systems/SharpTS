using SharpTS.Diagnostics.Exceptions;
using SharpTS.Execution;
using SharpTS.Parsing;
using SharpTS.TypeSystem;

namespace SharpTS.Repl;

/// <summary>
/// Handles dot-commands in the REPL (e.g., .help, .clear, .exit).
/// </summary>
internal sealed class DotCommands
{
    private readonly Interpreter _interpreter;
    private readonly DecoratorMode _decoratorMode;
    private readonly List<string> _sessionHistory;

    /// <summary>
    /// Set to true when .exit is invoked to signal the REPL loop to terminate.
    /// </summary>
    public bool ExitRequested { get; private set; }

    /// <summary>
    /// Set to true when .reset is invoked to signal the REPL to recreate the interpreter.
    /// </summary>
    public bool ResetRequested { get; private set; }

    public DotCommands(Interpreter interpreter, DecoratorMode decoratorMode, List<string> sessionHistory)
    {
        _interpreter = interpreter;
        _decoratorMode = decoratorMode;
        _sessionHistory = sessionHistory;
    }

    /// <summary>
    /// Returns true if the input is a dot-command.
    /// </summary>
    public static bool IsDotCommand(string input)
    {
        return input.TrimStart().StartsWith('.');
    }

    /// <summary>
    /// Executes a dot-command. Returns true if the command was recognized.
    /// </summary>
    public bool Execute(string input)
    {
        var trimmed = input.Trim();
        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : null;

        switch (command)
        {
            case ".help":
                PrintHelp();
                return true;

            case ".clear":
                Console.Clear();
                return true;

            case ".exit":
                ExitRequested = true;
                return true;

            case ".reset":
                ResetRequested = true;
                return true;

            case ".type":
                ShowType(arg);
                return true;

            case ".save":
                SaveSession(arg);
                return true;

            case ".load":
                LoadFile(arg);
                return true;

            case ".editor":
                Console.WriteLine("Editor mode is not yet supported. Use Shift+Enter for multi-line input.");
                return true;

            default:
                Console.WriteLine($"Unknown command: {command}. Type .help for available commands.");
                return true;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            REPL Commands:
              .help              Show this help message
              .clear             Clear the console screen
              .exit              Exit the REPL (also: Ctrl+D)
              .reset             Reset interpreter state (fresh environment)
              .type <expr>       Show the TypeScript type of an expression
              .save <file>       Save session history to a TypeScript file
              .load <file>       Load and execute a TypeScript file
              .editor            Enter multi-line editing mode (not yet implemented)

            Keyboard Shortcuts:
              Enter              Submit input (or continue on incomplete input)
              Shift+Enter        Force new line
              Up/Down            Navigate command history
              Ctrl+C             Cancel current input
              Ctrl+L             Clear screen
            """);
    }

    private void ShowType(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            Console.WriteLine("Usage: .type <expression>");
            return;
        }

        try
        {
            var lexer = new Lexer(expression);
            var tokens = lexer.ScanTokens();
            var parser = new Parser(tokens, _decoratorMode);
            var parseResult = parser.Parse();

            if (!parseResult.IsSuccess)
            {
                Console.WriteLine("Parse error in expression.");
                return;
            }

            var checker = new TypeChecker();
            checker.SetDecoratorMode(_decoratorMode);
            var typeResult = checker.CheckWithRecovery(parseResult.Statements);

            if (!typeResult.IsSuccess)
            {
                foreach (var diag in typeResult.Diagnostics)
                    Console.WriteLine($"Type Error: {diag}");
                return;
            }

            // Find the type of the last expression statement
            var lastExpr = parseResult.Statements
                .OfType<Stmt.Expression>()
                .LastOrDefault();

            if (lastExpr != null && typeResult.TypeMap.TryGet(lastExpr.Expr, out var typeInfo) && typeInfo != null)
            {
                Console.WriteLine(typeInfo.ToString());
            }
            else
            {
                Console.WriteLine("Could not determine type.");
            }
        }
        catch (SharpTSException ex)
        {
            Console.WriteLine($"Error: {ex.Diagnostic}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private void SaveSession(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            Console.WriteLine("Usage: .save <filename>");
            return;
        }

        try
        {
            File.WriteAllLines(filePath, _sessionHistory);
            Console.WriteLine($"Session saved to {filePath} ({_sessionHistory.Count} lines)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving session: {ex.Message}");
        }
    }

    private void LoadFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            Console.WriteLine("Usage: .load <filename>");
            return;
        }

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"File not found: {filePath}");
            return;
        }

        try
        {
            var source = File.ReadAllText(filePath);
            Console.WriteLine($"Loading {filePath}...");

            var lexer = new Lexer(source);
            var tokens = lexer.ScanTokens();
            var parser = new Parser(tokens, _decoratorMode);
            var parseResult = parser.Parse();

            if (!parseResult.IsSuccess)
            {
                foreach (var diag in parseResult.Diagnostics)
                    Console.WriteLine($"Error: {diag}");
                return;
            }

            var checker = new TypeChecker();
            checker.SetDecoratorMode(_decoratorMode);
            var typeResult = checker.CheckWithRecovery(parseResult.Statements);

            if (!typeResult.IsSuccess)
            {
                foreach (var diag in typeResult.Diagnostics)
                    Console.WriteLine($"Error: {diag}");
                return;
            }

            var resolver = new Execution.VariableResolver(_interpreter);
            resolver.Resolve(parseResult.Statements);
            _interpreter.Interpret(parseResult.Statements, typeResult.TypeMap);

            Console.WriteLine($"Loaded {filePath}");
        }
        catch (SharpTSException ex)
        {
            Console.WriteLine($"Error: {ex.Diagnostic}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
