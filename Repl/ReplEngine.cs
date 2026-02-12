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
    private VariableResolver _resolver;
    private TypeChecker _typeChecker;
    private readonly DecoratorMode _decoratorMode;
    private readonly List<string> _sessionHistory = [];
    private readonly List<Stmt> _accumulatedStatements = [];

    public ReplEngine(DecoratorMode decoratorMode)
    {
        _decoratorMode = decoratorMode;
        _interpreter = new Interpreter();
        _interpreter.SetDecoratorMode(decoratorMode);
        _resolver = new VariableResolver(_interpreter);
        _typeChecker = CreateTypeChecker();
    }

    private TypeChecker CreateTypeChecker()
    {
        var checker = new TypeChecker();
        checker.SetDecoratorMode(_decoratorMode);
        return checker;
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
            // Tick the event loop between inputs so timers/microtasks fire
            _interpreter.TickEventLoop();

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
                var commands = new DotCommands(_interpreter, _resolver, _typeChecker,
                    _decoratorMode, _sessionHistory, _accumulatedStatements);
                commands.Execute(input);

                if (commands.ExitRequested)
                    break;

                if (commands.ResetRequested)
                {
                    _interpreter.Dispose();
                    _interpreter = new Interpreter();
                    _interpreter.SetDecoratorMode(_decoratorMode);
                    _resolver = new VariableResolver(_interpreter);
                    _typeChecker = CreateTypeChecker();
                    _accumulatedStatements.Clear();
                    Console.WriteLine("REPL state has been reset.");
                }

                continue;
            }

            // Execute TypeScript input with Ctrl+C interruption support
            ExecuteWithInterrupt(input);
        }
    }

    private void ExecuteWithInterrupt(string source)
    {
        var executionThread = Thread.CurrentThread;
        var cancelled = false;

        void handler(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true; // Prevent process exit
            cancelled = true;
            executionThread.Interrupt();
        }

        Console.CancelKeyPress += handler;
        try
        {
            ExecuteInput(source);
        }
        catch (ThreadInterruptedException)
        {
            Console.WriteLine();
            Console.WriteLine("Execution interrupted.");
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            if (cancelled)
            {
                // Clear any residual interrupt flag. If the interrupt was already
                // consumed by the catch above, Sleep(0) returns immediately.
                // If a second interrupt arrived between catch and here, Sleep(0)
                // throws — we catch and discard it so the REPL loop survives.
                try { Thread.Sleep(0); }
                catch (ThreadInterruptedException) { /* consumed */ }
            }
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

            // Variable resolution — reuse the persistent resolver.
            // If resolution throws (e.g. malformed closure leaves scope stack dirty),
            // replace with a fresh resolver. The important accumulated state lives in
            // the interpreter's _locals dictionary, not in the resolver itself.
            try
            {
                _resolver.Resolve(parseResult.Statements);
            }
            catch
            {
                _resolver = new VariableResolver(_interpreter);
            }

            // Execute and capture result
            var result = _interpreter.InterpretRepl(parseResult.Statements);

            // Accumulate statements so .type can resolve types from previous inputs.
            // Type checking is deferred — only runs when .type is actually invoked.
            _accumulatedStatements.AddRange(parseResult.Statements);

            // Tick event loop after execution to process async work
            _interpreter.TickEventLoop();

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
        catch (ThreadInterruptedException)
        {
            throw; // Re-throw for the outer handler
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
