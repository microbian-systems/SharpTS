using System.Reflection;
using SharpTS.Compilation;
using SharpTS.Execution;
using SharpTS.Parsing;
using SharpTS.TypeSystem;
using SharpTS.TypeSystem.Exceptions;

namespace SharpTS.Test262;

/// <summary>Execution mode for a Test262 run.</summary>
public enum Test262ExecutionMode
{
    Interpreted,
    Compiled,
}

/// <summary>
/// Runs a single Test262 file end-to-end and classifies the outcome.
///
/// Assembly.LoadFrom is used for compiled mode — subprocess-per-test has
/// ~500ms–1s startup overhead that is prohibitive at spec-suite scale.
/// Trade-off: loaded assemblies are not unloadable in the default AppDomain.
/// Acceptable for the spike; revisit with AssemblyLoadContext if the process
/// footprint becomes painful.
/// </summary>
public sealed class Test262Runner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // Serializes Console.Out/Error redirection across parallel test cases.
    private static readonly object ConsoleLock = new();

    private readonly string _test262Root;
    private readonly Test262HarnessAssembler _assembler;
    private readonly TimeSpan _timeout;

    public Test262Runner(string test262Root, TimeSpan? timeout = null)
    {
        _test262Root = test262Root;
        _assembler = new Test262HarnessAssembler(test262Root);
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Runs one Test262 file and returns the classified outcome. Does not
    /// throw — all failure modes map to <see cref="Test262Outcome"/> buckets.
    /// </summary>
    public Test262Result RunOne(string testFilePath, Test262ExecutionMode mode)
    {
        string body;
        try
        {
            body = File.ReadAllText(testFilePath);
        }
        catch (Exception ex)
        {
            return new Test262Result(Test262Outcome.HarnessError, $"Failed to read test file: {ex.Message}", null);
        }

        var metadata = Test262MetadataParser.Parse(body);

        // Milestone 1 defers negative tests wholesale — their outcome protocol
        // (parse-phase vs runtime-phase expected errors) is a follow-up.
        if (metadata.IsNegative)
            return new Test262Result(Test262Outcome.Skipped, null, "negative-test-deferred");

        if (metadata.IsModule)
            return new Test262Result(Test262Outcome.Skipped, null, "module-test-deferred");

        if (metadata.IsAsync)
            return new Test262Result(Test262Outcome.Skipped, null, "async-done-deferred");

        string assembled;
        int harnessLength;
        try
        {
            (assembled, harnessLength) = _assembler.Assemble(body, metadata);
        }
        catch (FileNotFoundException ex)
        {
            return new Test262Result(Test262Outcome.HarnessError, ex.Message, null);
        }

        return mode switch
        {
            Test262ExecutionMode.Interpreted => RunInterpreted(assembled, harnessLength),
            Test262ExecutionMode.Compiled => RunCompiled(assembled, harnessLength),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private Test262Result RunInterpreted(string source, int harnessLength)
    {
        var task = Task.Run(() =>
        {
            var lexer = new Lexer(source);
            var tokens = lexer.ScanTokens();
            var parser = new Parser(tokens);
            var parseResult = parser.Parse();
            if (!parseResult.IsSuccess)
                return new Test262Result(Test262Outcome.ParseError, parseResult.Diagnostics.First().ToString(), null);

            TypeMap typeMap;
            try
            {
                var checker = new TypeChecker();
                typeMap = checker.Check(parseResult.Statements);
            }
            catch (TypeCheckException ex)
            {
                return new Test262Result(Test262Outcome.TypeCheckError, ex.Message, null);
            }

            var sw = new StringWriter();
            using var interpreter = new Interpreter(stdout: sw, stderr: TextWriter.Null);
            try
            {
                interpreter.Interpret(parseResult.Statements, typeMap);
            }
            catch (Exception ex)
            {
                return ClassifyExecutionException(ex, harnessLength);
            }
            return new Test262Result(Test262Outcome.Pass, null, null);
        });

        try
        {
            if (task.Wait(_timeout))
                return task.Result;
            return new Test262Result(Test262Outcome.Timeout, $"exceeded {_timeout.TotalSeconds}s", null);
        }
        catch (AggregateException ex)
        {
            return ClassifyExecutionException(ex.InnerException ?? ex, harnessLength);
        }
    }

    private Test262Result RunCompiled(string source, int harnessLength)
    {
        // Compile phase may throw parse/type errors before any IL is emitted.
        // Execution phase runs under a timeout because infinite loops in the
        // emitted program would hang the test assembly.
        var assemblyName = $"test262_{Guid.NewGuid():N}";
        var tempDir = Path.Combine(Path.GetTempPath(), $"sharpts_{assemblyName}");
        Directory.CreateDirectory(tempDir);
        var dllPath = Path.Combine(tempDir, $"{assemblyName}.dll");

        try
        {
            var lexer = new Lexer(source);
            var tokens = lexer.ScanTokens();
            var parser = new Parser(tokens);
            var parseResult = parser.Parse();
            if (!parseResult.IsSuccess)
                return new Test262Result(Test262Outcome.ParseError, parseResult.Diagnostics.First().ToString(), null);

            TypeMap typeMap;
            DeadCodeInfo deadCodeInfo;
            try
            {
                var checker = new TypeChecker();
                typeMap = checker.Check(parseResult.Statements);
                var deadCodeAnalyzer = new DeadCodeAnalyzer(typeMap);
                deadCodeInfo = deadCodeAnalyzer.Analyze(parseResult.Statements);
            }
            catch (TypeCheckException ex)
            {
                return new Test262Result(Test262Outcome.TypeCheckError, ex.Message, null);
            }

            try
            {
                var compiler = new ILCompiler(assemblyName);
                compiler.Compile(parseResult.Statements, typeMap, deadCodeInfo);
                compiler.Save(dllPath);
            }
            catch (Exception ex)
            {
                return new Test262Result(Test262Outcome.HarnessError, $"IL compile failed: {ex.Message}", null);
            }

            var assembly = Assembly.LoadFrom(dllPath);
            var programType = assembly.GetType("$Program");
            var mainMethod = programType?.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
            if (mainMethod is null)
                return new Test262Result(Test262Outcome.HarnessError, "$Program.Main not found in compiled assembly", null);

            return InvokeCompiledMain(mainMethod, harnessLength);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private Test262Result InvokeCompiledMain(MethodInfo mainMethod, int harnessLength)
    {
        // Serialize Console redirection; multiple compiled tests can run in
        // parallel xUnit cases but must not cross-contaminate stdout.
        Test262Result? result = null;
        var task = Task.Run(() =>
        {
            lock (ConsoleLock)
            {
                var originalOut = Console.Out;
                var originalError = Console.Error;
                var sw = new StringWriter();
                Console.SetOut(sw);
                Console.SetError(TextWriter.Null);
                try
                {
                    try
                    {
                        mainMethod.Invoke(null, null);
                    }
                    catch (TargetInvocationException tie)
                    {
                        result = ClassifyExecutionException(tie.InnerException ?? tie, harnessLength);
                        return;
                    }
                    catch (Exception ex)
                    {
                        result = ClassifyExecutionException(ex, harnessLength);
                        return;
                    }
                    result = new Test262Result(Test262Outcome.Pass, null, null);
                }
                finally
                {
                    Console.SetOut(originalOut);
                    Console.SetError(originalError);
                }
            }
        });

        if (!task.Wait(_timeout))
            return new Test262Result(Test262Outcome.Timeout, $"exceeded {_timeout.TotalSeconds}s", null);

        return result ?? new Test262Result(Test262Outcome.RuntimeError, "runner produced no result", null);
    }

    /// <summary>
    /// Maps an exception thrown from inside the running test to an outcome
    /// bucket. <c>Test262Error</c> (thrown by <c>harness/assert.js</c> on
    /// assertion failure) is the sentinel that distinguishes <see cref="Test262Outcome.Fail"/>
    /// from <see cref="Test262Outcome.RuntimeError"/>.
    /// </summary>
    private static Test262Result ClassifyExecutionException(Exception ex, int harnessLength)
    {
        // Unwrap one layer of TargetInvocationException / AggregateException
        while (ex is TargetInvocationException or AggregateException && ex.InnerException is not null)
            ex = ex.InnerException;

        if (ex is TypeCheckException tce)
            return new Test262Result(Test262Outcome.TypeCheckError, tce.Message, null);

        var msg = ex.Message ?? "";
        // Interpreter/compiler surface user-thrown JS errors with their `name`
        // in the message. `assert.sameValue` etc. throw `new Test262Error(...)`.
        if (msg.Contains("Test262Error", StringComparison.Ordinal))
            return new Test262Result(Test262Outcome.Fail, msg, null);

        return new Test262Result(Test262Outcome.RuntimeError, msg, null);
    }
}
