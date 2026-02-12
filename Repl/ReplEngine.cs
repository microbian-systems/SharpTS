using PrettyPrompt;
using PrettyPrompt.Highlighting;
using SharpTS.Diagnostics;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Execution;
using SharpTS.Parsing;
using SharpTS.Runtime.Types;
using SharpTS.TypeSystem;

namespace SharpTS.Repl;

/// <summary>
/// Enhanced REPL engine using PrettyPrompt for multi-line editing,
/// syntax highlighting, persistent history, and auto-display of results.
/// </summary>
public sealed class ReplEngine
{
    private Interpreter _interpreter;
    private readonly DecoratorMode _decoratorMode;
    private readonly List<string> _sessionHistory = [];

    public ReplEngine(DecoratorMode decoratorMode)
    {
        _decoratorMode = decoratorMode;
        _interpreter = new Interpreter();
        _interpreter.SetDecoratorMode(decoratorMode);
    }

    public async Task RunAsync()
    {
        // Persistent history file at ~/.sharpts/repl_history
        var historyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".sharpts");
        Directory.CreateDirectory(historyDir);
        var historyPath = Path.Combine(historyDir, "repl_history");

        var callbacks = new ReplCallbacks();
        var configuration = new PromptConfiguration(
            prompt: new FormattedString("> ", new FormatSpan(0, 2, AnsiColor.Cyan)));

        await using var prompt = new Prompt(
            persistentHistoryFilepath: historyPath,
            callbacks: callbacks,
            configuration: configuration);

        while (true)
        {
            var response = await prompt.ReadLineAsync();

            if (!response.IsSuccess)
            {
                // Ctrl+C was pressed — reset current input, continue loop
                continue;
            }

            var input = response.Text;

            if (string.IsNullOrWhiteSpace(input))
                continue;

            _sessionHistory.Add(input);

            // Handle dot-commands
            if (DotCommands.IsDotCommand(input))
            {
                var commands = new DotCommands(_interpreter, _decoratorMode, _sessionHistory);
                commands.Execute(input);

                if (commands.ExitRequested)
                    break;

                if (commands.ResetRequested)
                {
                    _interpreter.Dispose();
                    _interpreter = new Interpreter();
                    _interpreter.SetDecoratorMode(_decoratorMode);
                    Console.WriteLine("REPL state has been reset.");
                }

                continue;
            }

            // Execute TypeScript input
            ExecuteInput(input);
        }
    }

    private static ParseDiagnosticResult TryParse(string source, DecoratorMode decoratorMode)
    {
        var lexer = new Lexer(source);
        var tokens = lexer.ScanTokens();
        var parser = new Parser(tokens, decoratorMode);
        return parser.Parse();
    }

    private void ExecuteInput(string source)
    {
        try
        {
            // Parse — if it fails, retry with an appended semicolon (REPL convenience:
            // the parser requires semicolons but REPL users often omit them)
            var parseResult = TryParse(source, _decoratorMode);

            if (!parseResult.IsSuccess)
            {
                var retryResult = TryParse(source + ";", _decoratorMode);
                if (retryResult.IsSuccess)
                {
                    parseResult = retryResult;
                }
                else
                {
                    // Show the original errors (not the retry errors)
                    foreach (var diagnostic in parseResult.Diagnostics)
                        Console.WriteLine($"Error: {diagnostic}");
                    if (parseResult.HitErrorLimit)
                        Console.WriteLine("Too many errors, stopping.");
                    return;
                }
            }

            // Skip type checking in REPL mode — the TypeChecker is stateless across lines
            // so it can't know about variables declared in previous inputs.
            // The interpreter handles runtime errors directly.

            // Variable resolution
            var resolver = new VariableResolver(_interpreter);
            resolver.Resolve(parseResult.Statements);

            // Execute and capture result
            var result = _interpreter.InterpretRepl(parseResult.Statements);

            // Auto-display expression results (skip undefined, like Node.js)
            if (result is not null and not SharpTSUndefined)
            {
                Console.WriteLine(ValueFormatter.Format(result));
            }
        }
        catch (SharpTSException ex)
        {
            Console.WriteLine($"Error: {ex.Diagnostic}");
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Runtime Error:"))
        {
            Console.WriteLine(ex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
