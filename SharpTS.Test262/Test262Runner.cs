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

    // Memory watchdog: if an individual test drives process memory growth
    // past this threshold we abort it. Catches runaway allocation paths we
    // haven't explicitly guarded (Array.prototype methods, large string
    // concat, etc.). Chosen to be well above any legitimate test's working
    // set but well below the point where the OS kills the host.
    private const long MemoryLimitBytes = 2L * 1024 * 1024 * 1024;

    // Serializes Console.Out/Error redirection across parallel test cases.
    private static readonly object ConsoleLock = new();

    private readonly string _test262Root;
    private readonly Test262HarnessAssembler _assembler;
    private readonly TimeSpan _timeout;
    private readonly HashSet<string> _skipFeatures;

    public Test262Runner(string test262Root, TimeSpan? timeout = null, HashSet<string>? skipFeatures = null)
    {
        _test262Root = test262Root;
        _assembler = new Test262HarnessAssembler(test262Root);
        _timeout = timeout ?? DefaultTimeout;
        _skipFeatures = skipFeatures ?? new HashSet<string>(StringComparer.Ordinal);
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

        // Feature-gated skip so known-unsupported categories don't pollute
        // Fail/RuntimeError buckets. Only one matching tag is reported; the
        // rest are irrelevant once the first has ruled the test out.
        if (_skipFeatures.Count > 0 && metadata.Features.Count > 0)
        {
            foreach (var feature in metadata.Features)
            {
                if (_skipFeatures.Contains(feature))
                    return new Test262Result(Test262Outcome.Skipped, null, $"skip-feature:{feature}");
            }
        }

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
        // Parse + type-check on the caller's thread — neither honors the VM
        // timeout token so we'd rather not burn a dedicated thread per test
        // on work that doesn't need cancellation.
        SharpTS.Diagnostics.ParseDiagnosticResult parseResult;
        try
        {
            var lexer = new Lexer(source);
            var tokens = lexer.ScanTokens();
            var parser = new Parser(tokens);
            parseResult = parser.Parse();
        }
        catch (Exception ex)
        {
            // Lexer throws raw Exception on syntax it won't tokenize
            // (legacy octals, bad escapes). Those are parse-phase failures.
            return new Test262Result(Test262Outcome.ParseError, ex.Message, null);
        }
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

        // Execute on a dedicated thread and signal the interpreter's VM
        // timeout token on deadline. The token is checked between statements
        // and loop iterations, so most runaway tests self-terminate promptly.
        // Pathological tight-loop tests can still run longer than the
        // timeout — we detect and log that leak but don't block.
        Test262Result? result = null;
        Exception? threadException = null;
        using var cts = new CancellationTokenSource();

        var thread = new Thread(() =>
        {
            var sw = new StringWriter();
            using var interpreter = new Interpreter(stdout: sw, stderr: TextWriter.Null);
            interpreter.SetVmTimeoutToken(cts.Token);
            try
            {
                interpreter.Interpret(parseResult.Statements, typeMap);
                result = new Test262Result(Test262Outcome.Pass, null, null);
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        })
        { IsBackground = true };

        thread.Start();
        // Poll for either completion, deadline, or runaway memory. Polling
        // at 100ms costs ~50 wakeups for a test that runs the full 5s; the
        // overhead is trivial compared to the interpreter work.
        var deadline = DateTime.UtcNow + _timeout;
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var baselineMemory = process.WorkingSet64;
        while (thread.IsAlive)
        {
            if (thread.Join(100)) break;
            if (DateTime.UtcNow > deadline)
            {
                cts.Cancel();
                thread.Join(TimeSpan.FromMilliseconds(500));
                return new Test262Result(Test262Outcome.Timeout, $"exceeded {_timeout.TotalSeconds}s", null);
            }
            process.Refresh();
            if (process.WorkingSet64 - baselineMemory > MemoryLimitBytes)
            {
                cts.Cancel();
                thread.Join(TimeSpan.FromMilliseconds(500));
                // Force a GC so we don't carry the bloat into the next test's
                // baseline measurement (and surface a moment of relief to the OS).
                GC.Collect();
                return new Test262Result(Test262Outcome.Timeout,
                    $"memory watchdog fired (grew by {(process.WorkingSet64 - baselineMemory) / 1024 / 1024}MB)", null);
            }
        }

        if (threadException is not null)
            return ClassifyExecutionException(threadException, harnessLength);
        return result ?? new Test262Result(Test262Outcome.RuntimeError, "runner produced no result", null);
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
            SharpTS.Diagnostics.ParseDiagnosticResult parseResult;
            try
            {
                var lexer = new Lexer(source);
                var tokens = lexer.ScanTokens();
                var parser = new Parser(tokens);
                parseResult = parser.Parse();
            }
            catch (Exception ex)
            {
                return new Test262Result(Test262Outcome.ParseError, ex.Message, null);
            }
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

            // Resolve the per-assembly $Runtime._cancelRequested field up-front
            // so InvokeCompiledMain can flip it on timeout. Each test compiles
            // its own assembly with its own $Runtime type — this keeps the
            // cancellation flag scoped to the running test. See issue #74.
            var runtimeType = assembly.GetType("$Runtime");
            var cancelField = runtimeType?.GetField("_cancelRequested",
                BindingFlags.Public | BindingFlags.Static);

            return InvokeCompiledMain(mainMethod, harnessLength, cancelField);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private Test262Result InvokeCompiledMain(MethodInfo mainMethod, int harnessLength, FieldInfo? cancelField)
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

        // Poll for timeout or memory runaway. Compiled-mode tests run through
        // the same process, so the watchdog threshold applies symmetrically.
        // When the deadline hits, we flip $Runtime._cancelRequested (the per-
        // assembly cooperative cancellation flag emitted per issue #74); the
        // next loop backedge in the compiled IL sees it and throws
        // OperationCanceledException, unwinding the task within ~100ms (the
        // event loop's Wait resolution) to a full iteration of the hot loop.
        var deadline = DateTime.UtcNow + _timeout;
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var baselineMemory = process.WorkingSet64;
        while (!task.IsCompleted)
        {
            if (task.Wait(100)) break;
            if (DateTime.UtcNow > deadline)
            {
                TripCancelAndJoin(cancelField, task);
                return new Test262Result(Test262Outcome.Timeout, $"exceeded {_timeout.TotalSeconds}s", null);
            }
            process.Refresh();
            if (process.WorkingSet64 - baselineMemory > MemoryLimitBytes)
            {
                TripCancelAndJoin(cancelField, task);
                GC.Collect();
                return new Test262Result(Test262Outcome.Timeout,
                    $"memory watchdog fired (grew by {(process.WorkingSet64 - baselineMemory) / 1024 / 1024}MB)", null);
            }
        }

        return result ?? new Test262Result(Test262Outcome.RuntimeError, "runner produced no result", null);
    }

    /// <summary>
    /// Flips <c>$Runtime._cancelRequested</c> on the timed-out test's assembly
    /// and gives the task a short grace period to unwind. If the emitted IL is
    /// at a loop backedge (the common case) or inside the event-loop drain, the
    /// next cancellation-check call will throw <see cref="OperationCanceledException"/>
    /// and the task completes cleanly. Without this, an orphan task keeps
    /// consuming CPU and holding assembly memory until the process exits —
    /// which is what caused the Promise rollout (#69) to hang in compile mode.
    /// </summary>
    private static void TripCancelAndJoin(FieldInfo? cancelField, Task task)
    {
        if (cancelField is null) return;
        try
        {
            cancelField.SetValue(null, true);
            task.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Best-effort; if Wait or SetValue throws we fall through to
            // returning Timeout anyway.
        }
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

        // Compiled mode: user-thrown JS values are preserved in Exception.Data["__tsValue"].
        // For `throw new Test262Error(msg)`, the value is a Dictionary or $Object whose
        // `name` field is "Test262Error". Check that before falling back to RuntimeError.
        if (ex.Data.Contains("__tsValue"))
        {
            var tsValue = ex.Data["__tsValue"];
            string? name = null;
            string? userMessage = null;
            if (tsValue is System.Collections.Generic.IDictionary<string, object?> d)
            {
                if (d.TryGetValue("name", out var n) && n is string ns) name = ns;
                if (d.TryGetValue("message", out var m) && m is string ms) userMessage = ms;
            }
            else if (tsValue != null)
            {
                // $Object / $Error subclass — read "name"/"message" via the emitted
                // $IHasFields interface (compiled $Object implements it). Fall back
                // to private `_fields` dictionary, then to ToString.
                try
                {
                    var t = tsValue.GetType();
                    var iface = t.GetInterface("$IHasFields");
                    System.Reflection.MethodInfo? getProp = iface?.GetMethod("GetProperty", [typeof(string)]);
                    if (getProp != null)
                    {
                        name = getProp.Invoke(tsValue, ["name"]) as string;
                        userMessage = getProp.Invoke(tsValue, ["message"]) as string;
                    }
                    if (name == null)
                    {
                        // Try private _fields dictionary directly.
                        var fieldsField = t.GetField("_fields",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        if (fieldsField?.GetValue(tsValue) is System.Collections.Generic.IDictionary<string, object?> fd)
                        {
                            if (fd.TryGetValue("name", out var fn) && fn is string fns) name = fns;
                            if (fd.TryGetValue("message", out var fm) && fm is string fms) userMessage = fms;
                        }
                    }
                }
                catch { }
                // Last-resort: stringify-like check
                if (name == null)
                {
                    var s = tsValue.ToString();
                    if (s != null && s.StartsWith("Test262Error", StringComparison.Ordinal))
                        name = "Test262Error";
                }
            }
            if (name == "Test262Error")
                return new Test262Result(Test262Outcome.Fail, userMessage ?? msg, null);
        }

        return new Test262Result(Test262Outcome.RuntimeError, msg, null);
    }
}
