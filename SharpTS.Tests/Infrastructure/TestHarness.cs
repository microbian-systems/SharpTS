using System.Diagnostics;
using System.Reflection;
using SharpTS.Compilation;
using SharpTS.Execution;
using SharpTS.Modules;
using SharpTS.Parsing;
using SharpTS.Runtime.BuiltIns;
using SharpTS.TypeSystem;

namespace SharpTS.Tests.Infrastructure;

/// <summary>
/// Test harness utilities for running TypeScript source through the interpreter
/// and compiler, capturing console output for assertions.
/// </summary>
public static class TestHarness
{
    /// <summary>
    /// Default timeout for test execution (30 seconds).
    /// Async iterator bugs can cause infinite loops - this ensures tests fail fast.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Legacy lock for tests that still call <see cref="Console.SetOut"/> directly
    /// (e.g., LSP bridge tests, diagnostic reporter tests). New compiled-mode runners
    /// use <see cref="AsyncLocalConsoleRedirector"/> instead, which doesn't need a lock.
    /// </summary>
    internal static readonly object ConsoleLock = new();

    /// <summary>
    /// Runs TypeScript source using the specified execution mode and captures console output.
    /// This is the primary entry point for parameterized tests that should run against
    /// both the interpreter and compiler.
    /// </summary>
    /// <param name="source">TypeScript source code</param>
    /// <param name="mode">Execution mode (Interpreted or Compiled)</param>
    /// <returns>Captured console output</returns>
    public static string Run(string source, ExecutionMode mode)
    {
        return mode switch
        {
            ExecutionMode.Interpreted => RunInterpreted(source),
            ExecutionMode.Compiled => RunCompiled(source),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    /// <summary>
    /// Runs TypeScript source using the specified execution mode with decorator support.
    /// </summary>
    /// <param name="source">TypeScript source code</param>
    /// <param name="mode">Execution mode (Interpreted or Compiled)</param>
    /// <param name="decoratorMode">Decorator mode (None, Legacy, Stage3)</param>
    /// <returns>Captured console output</returns>
    public static string Run(string source, ExecutionMode mode, DecoratorMode decoratorMode)
    {
        return mode switch
        {
            ExecutionMode.Interpreted => RunInterpreted(source, decoratorMode),
            ExecutionMode.Compiled => RunCompiled(source, decoratorMode),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    /// <summary>
    /// Runs TypeScript modules using the specified execution mode.
    /// This is the primary entry point for parameterized module tests.
    /// </summary>
    /// <param name="files">Dictionary of file paths to file contents</param>
    /// <param name="entryPoint">Path to the entry point module</param>
    /// <param name="mode">Execution mode (Interpreted or Compiled)</param>
    /// <returns>Captured console output</returns>
    public static string RunModules(Dictionary<string, string> files, string entryPoint, ExecutionMode mode)
    {
        return mode switch
        {
            ExecutionMode.Interpreted => RunModulesInterpreted(files, entryPoint),
            ExecutionMode.Compiled => RunModulesCompiled(files, entryPoint),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    /// <summary>
    /// Parses TypeScript source code and returns the list of statements.
    /// Useful for testing AST structure without execution.
    /// </summary>
    /// <param name="source">TypeScript source code</param>
    /// <returns>List of parsed statements</returns>
    public static List<Stmt> Parse(string source)
    {
        var lexer = new Lexer(source);
        var tokens = lexer.ScanTokens();
        var parser = new Parser(tokens);
        var result = parser.Parse();
        return result.Statements;
    }

    /// <summary>
    /// Runs TypeScript source through the interpreter and captures console output.
    /// Uses default timeout to prevent infinite loops from hanging tests.
    /// </summary>
    /// <param name="source">TypeScript source code</param>
    /// <returns>Captured console output</returns>
    public static string RunInterpreted(string source)
    {
        return RunInterpreted(source, DecoratorMode.None, DefaultTimeout);
    }

    /// <summary>
    /// Runs TypeScript source through the interpreter with a timeout.
    /// </summary>
    /// <param name="source">TypeScript source code</param>
    /// <param name="timeout">Maximum execution time before throwing TimeoutException</param>
    /// <returns>Captured console output</returns>
    public static string RunInterpreted(string source, TimeSpan timeout)
    {
        return RunInterpreted(source, DecoratorMode.None, timeout);
    }

    /// <summary>
    /// Runs TypeScript source through the interpreter with decorator support and captures console output.
    /// Uses default timeout.
    /// </summary>
    public static string RunInterpreted(string source, DecoratorMode decoratorMode)
    {
        return RunInterpreted(source, decoratorMode, DefaultTimeout);
    }

    /// <summary>
    /// Runs TypeScript source through the interpreter with decorator support and timeout.
    /// </summary>
    /// <param name="source">TypeScript source code</param>
    /// <param name="decoratorMode">Decorator mode (None, Legacy, Stage3)</param>
    /// <param name="timeout">Maximum execution time before throwing TimeoutException</param>
    /// <returns>Captured console output</returns>
    /// <exception cref="TimeoutException">Thrown if execution exceeds the timeout (likely an infinite loop bug)</exception>
    public static string RunInterpreted(string source, DecoratorMode decoratorMode, TimeSpan timeout)
    {
        // Run interpretation in a task so we can enforce a timeout.
        // This catches infinite loop bugs (e.g., Promise double-wrapping in async iterators).
        var task = Task.Run(() =>
        {
            var sw = new StringWriter();

            var lexer = new Lexer(source);
            var tokens = lexer.ScanTokens();
            var parser = new Parser(tokens, decoratorMode);
            var statements = parser.ParseOrThrow();

            var checker = new TypeChecker();
            checker.SetDecoratorMode(decoratorMode);
            var typeMap = checker.Check(statements);

            using var interpreter = new Interpreter(stdout: sw, stderr: TextWriter.Null);
            interpreter.SetDecoratorMode(decoratorMode);
            interpreter.Interpret(statements, typeMap);

            // Normalize line endings for cross-platform test consistency
            return sw.ToString().Replace("\r\n", "\n");
        });

        try
        {
            if (task.Wait(timeout))
            {
                return task.Result;
            }

            throw new TimeoutException(
                $"Interpreter execution exceeded {timeout.TotalSeconds}s timeout. " +
                "This likely indicates an infinite loop bug (e.g., Promise double-wrapping in async iterators).");
        }
        catch (AggregateException ex)
        {
            // Unwrap AggregateException to preserve original exception type for tests
            // that use Assert.Throws<SpecificExceptionType>
            if (ex.InnerExceptions.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
            }
            throw;
        }
    }

    /// <summary>
    /// Compiles TypeScript source to a .NET DLL, executes it, and captures output.
    /// Uses default timeout to prevent infinite loops from hanging tests.
    /// </summary>
    /// <param name="source">TypeScript source code</param>
    /// <returns>Captured console output from the compiled executable</returns>
    public static string RunCompiled(string source)
    {
        return RunCompiled(source, DecoratorMode.None, DefaultTimeout);
    }

    /// <summary>
    /// Compiles TypeScript source to a .NET DLL with a timeout, executes it, and captures output.
    /// </summary>
    /// <param name="source">TypeScript source code</param>
    /// <param name="timeout">Maximum execution time before throwing TimeoutException</param>
    /// <returns>Captured console output from the compiled executable</returns>
    public static string RunCompiled(string source, TimeSpan timeout)
    {
        return RunCompiled(source, DecoratorMode.None, timeout);
    }

    /// <summary>
    /// Compiles TypeScript source to a .NET DLL with decorator support, executes it, and captures output.
    /// Uses default timeout.
    /// </summary>
    public static string RunCompiled(string source, DecoratorMode decoratorMode)
    {
        return RunCompiled(source, decoratorMode, DefaultTimeout);
    }

    /// <summary>
    /// Compiles and runs with <c>SharpTS.Tests.dll</c> copied alongside the compiled
    /// output so <c>@DotNetType</c> can resolve test fixture types at runtime (e.g.,
    /// <c>SharpTS.Tests.Infrastructure.CallbackFixture</c>).
    /// </summary>
    public static string RunCompiledWithTestFixtures(string source, DecoratorMode decoratorMode = DecoratorMode.Legacy)
    {
        return RunCompiled(source, decoratorMode, DefaultTimeout, scriptArgs: null, includeTestsAssembly: true);
    }

    /// <summary>
    /// Like <see cref="RunCompiled(string, DecoratorMode)"/> but does NOT copy SharpTS.dll
    /// alongside the output. Simulates the real-world flow of shipping a user's compiled
    /// DLL without the SharpTS runtime — exposes bugs where emitted IL reflects into
    /// SharpTS via <c>Type.GetType("..., SharpTS")</c> and then NREs when that returns null.
    /// </summary>
    public static string RunCompiledStandalone(string source, DecoratorMode decoratorMode = DecoratorMode.Legacy)
    {
        return RunCompiled(source, decoratorMode, DefaultTimeout, scriptArgs: null, includeTestsAssembly: false, copySharpTsRuntime: false);
    }

    /// <summary>
    /// Compiles TypeScript source to a .NET DLL with decorator support and timeout, executes it, and captures output.
    /// </summary>
    /// <param name="source">TypeScript source code</param>
    /// <param name="decoratorMode">Decorator mode (None, Legacy, Stage3)</param>
    /// <param name="timeout">Maximum execution time before throwing TimeoutException</param>
    /// <returns>Captured console output from the compiled executable</returns>
    /// <exception cref="TimeoutException">Thrown if execution exceeds the timeout (likely an infinite loop bug)</exception>
    public static string RunCompiled(string source, DecoratorMode decoratorMode, TimeSpan timeout, string[]? scriptArgs = null, bool includeTestsAssembly = false, bool copySharpTsRuntime = true)
    {
        // The default fast path is in-process Assembly.LoadFrom — it skips the ~4s
        // dotnet-startup-plus-SharpTS-JIT cost per test by reusing the testhost's
        // already-warmed runtime. Two cases still need a real subprocess:
        //   1. scriptArgs != null — `process.argv` in compiled mode reads
        //      Environment.GetCommandLineArgs() directly (see RuntimeEmitter.ProcessHelpers),
        //      which would surface the testhost's argv in-process, not the test's.
        //   2. copySharpTsRuntime == false — RunCompiledStandalone simulates a
        //      shipped DLL with no SharpTS.dll alongside; that only repros via spawn.
        if (scriptArgs is not null || !copySharpTsRuntime)
            return RunCompiledViaSubprocess(source, decoratorMode, timeout, scriptArgs, includeTestsAssembly, copySharpTsRuntime);

        return RunCompiledInProcess(source, decoratorMode, timeout);
    }

    /// <summary>
    /// In-process compile + load + invoke. Skips the ~4s subprocess startup by reusing
    /// the testhost's already-warm SharpTS.dll. Uses a unique GUID-suffixed assembly
    /// name so concurrent <see cref="Assembly.Load(byte[])"/> calls don't collide on
    /// simple-name identity (see feedback_test_perf_changes.md). Output is captured
    /// per logical-execution-context via <see cref="AsyncLocalConsoleRedirector"/>, so
    /// parallel tests don't fight over <see cref="Console.Out"/>.
    /// </summary>
    private static string RunCompiledInProcess(string source, DecoratorMode decoratorMode, TimeSpan timeout)
    {
        var assemblyName = $"test_{Guid.NewGuid():N}";

        var lexer = new Lexer(source);
        var tokens = lexer.ScanTokens();
        var parser = new Parser(tokens, decoratorMode);
        var statements = parser.ParseOrThrow();

        var checker = new TypeChecker();
        checker.SetDecoratorMode(decoratorMode);
        var typeMap = checker.Check(statements);

        var deadCodeAnalyzer = new DeadCodeAnalyzer(typeMap);
        var deadCodeInfo = deadCodeAnalyzer.Analyze(statements);

        var compiler = new ILCompiler(assemblyName);
        compiler.SetDecoratorMode(decoratorMode);
        compiler.Compile(statements, typeMap, deadCodeInfo);

        // Skip the disk roundtrip — load the assembly straight from the in-memory PE bytes.
        var bytes = compiler.SaveToBytes();
        var assembly = Assembly.Load(bytes);
        var programType = assembly.GetType("$Program")
            ?? throw new InvalidOperationException("Compiled assembly has no $Program type");
        var mainMethod = programType.GetMethod("Main", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("$Program has no public static Main method");

        // Run on a worker so we can enforce a timeout. AsyncLocal flows into Task.Run,
        // so the redirector's capture buffer follows the test through the task boundary.
        var task = Task.Run(() =>
        {
            using var capture = AsyncLocalConsoleRedirector.Capture();
            try
            {
                mainMethod.Invoke(null, null);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            }
            return capture.GetOutput().Replace("\r\n", "\n");
        });

        try
        {
            if (task.Wait(timeout))
                return task.Result;

            // Cooperatively cancel: the compiled $Runtime polls _cancelRequested at every
            // loop backedge (issue #74). Tripping it lets a runaway loop unwind itself
            // without orphan-threading the testhost. Best-effort wait for the unwind so
            // the worker thread doesn't stay pinned, but always surface a TimeoutException
            // to callers — that's the contract the subprocess path established and tests
            // assert against.
            try
            {
                var cancelField = assembly.GetType("$Runtime")?.GetField("_cancelRequested",
                    BindingFlags.Public | BindingFlags.Static);
                cancelField?.SetValue(null, true);
            }
            catch { /* best-effort */ }

            try { task.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore — we're throwing TimeoutException anyway */ }

            throw new TimeoutException(
                $"Compiled program execution exceeded {timeout.TotalSeconds}s timeout. " +
                "This likely indicates an infinite loop bug (e.g., Promise double-wrapping in async iterators).");
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            // Unwrap so Assert.Throws<SpecificException> still works.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
            throw; // unreachable
        }
    }

    /// <summary>
    /// Subprocess fallback for the cases the in-process path can't serve: tests passing
    /// <c>scriptArgs</c> (need a real <c>Environment.GetCommandLineArgs()</c>) and
    /// standalone-DLL tests that explicitly omit SharpTS.dll from the output dir.
    /// </summary>
    private static string RunCompiledViaSubprocess(string source, DecoratorMode decoratorMode, TimeSpan timeout, string[]? scriptArgs, bool includeTestsAssembly, bool copySharpTsRuntime)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sharpts_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dllPath = Path.Combine(tempDir, "test.dll");

            // Compile
            var lexer = new Lexer(source);
            var tokens = lexer.ScanTokens();
            var parser = new Parser(tokens, decoratorMode);
            var statements = parser.ParseOrThrow();

            var checker = new TypeChecker();
            checker.SetDecoratorMode(decoratorMode);
            var typeMap = checker.Check(statements);

            var deadCodeAnalyzer = new DeadCodeAnalyzer(typeMap);
            var deadCodeInfo = deadCodeAnalyzer.Analyze(statements);

            var compiler = new ILCompiler("test");
            compiler.SetDecoratorMode(decoratorMode);
            compiler.Compile(statements, typeMap, deadCodeInfo);
            compiler.Save(dllPath);

            // Copy SharpTS.dll and its dependencies for runtime dependency (needed for Promise.all/race/allSettled/any)
            // Tests that intentionally simulate a standalone deployment pass copySharpTsRuntime: false.
            if (copySharpTsRuntime)
            {
                var sharpTsDll = typeof(RuntimeTypes).Assembly.Location;
                if (!string.IsNullOrEmpty(sharpTsDll) && File.Exists(sharpTsDll))
                {
                    File.Copy(sharpTsDll, Path.Combine(tempDir, "SharpTS.dll"), overwrite: true);

                    // Copy ZstdSharp.dll (required for zstd compression in compiled mode)
                    var zstdDll = Path.Combine(Path.GetDirectoryName(sharpTsDll)!, "ZstdSharp.dll");
                    if (File.Exists(zstdDll))
                    {
                        File.Copy(zstdDll, Path.Combine(tempDir, "ZstdSharp.dll"), overwrite: true);
                    }
                }
            }

            // Optionally copy SharpTS.Tests.dll so `@DotNetType` fixture types (e.g.,
            // CallbackFixture) are loadable by the compiled-out-of-process executable.
            if (includeTestsAssembly)
            {
                var testsDll = typeof(TestHarness).Assembly.Location;
                if (!string.IsNullOrEmpty(testsDll) && File.Exists(testsDll))
                {
                    File.Copy(testsDll, Path.Combine(tempDir, Path.GetFileName(testsDll)), overwrite: true);
                }
            }

            // Write runtimeconfig.json
            var configPath = Path.Combine(tempDir, "test.runtimeconfig.json");
            File.WriteAllText(configPath, """
                {
                  "runtimeOptions": {
                    "tfm": "net10.0",
                    "framework": {
                      "name": "Microsoft.NETCore.App",
                      "version": "10.0.0"
                    }
                  }
                }
                """);

            // Execute and capture output
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = tempDir
            };
            psi.ArgumentList.Add(dllPath);
            if (scriptArgs != null)
            {
                foreach (var arg in scriptArgs)
                    psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)!;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            // Use timeout to catch infinite loop bugs
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                process.Kill();
                throw new TimeoutException(
                    $"Compiled program execution exceeded {timeout.TotalSeconds}s timeout. " +
                    "This likely indicates an infinite loop bug (e.g., Promise double-wrapping in async iterators).");
            }

            var output = outputTask.Result;
            var error = errorTask.Result;

            if (process.ExitCode != 0)
            {
                throw new Exception($"Compiled program exited with code {process.ExitCode}. Stderr: {error}");
            }

            // Normalize line endings for cross-platform test consistency
            return output.Replace("\r\n", "\n");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Runs TypeScript source with command-line arguments available via process.argv.slice(2).
    /// In interpreted mode, uses ProcessBuiltIns.SetScriptArguments.
    /// In compiled mode, passes arguments to the spawned dotnet process.
    /// </summary>
    public static string RunWithArgs(string source, ExecutionMode mode, string[] scriptArgs)
    {
        return mode switch
        {
            ExecutionMode.Interpreted => RunInterpretedWithArgs(source, scriptArgs),
            ExecutionMode.Compiled => RunCompiled(source, DecoratorMode.None, DefaultTimeout, scriptArgs),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static string RunInterpretedWithArgs(string source, string[] scriptArgs)
    {
        ProcessBuiltIns.SetScriptArguments("script.ts", scriptArgs);
        try
        {
            return RunInterpreted(source);
        }
        finally
        {
            ProcessBuiltIns.ClearScriptArguments();
        }
    }

    /// <summary>
    /// Compiles TypeScript source, loads the assembly in-process, executes it, and returns both the assembly and output.
    /// This allows tests to perform reflection on the compiled types.
    /// </summary>
    /// <param name="source">TypeScript source code</param>
    /// <param name="decoratorMode">Decorator mode (None, Legacy, Stage3)</param>
    /// <returns>Tuple of (Assembly, console output)</returns>
    public static (Assembly assembly, string output) CompileAndRun(string source, DecoratorMode decoratorMode)
    {
        // Use unique assembly name to avoid conflicts when loading multiple assemblies
        var assemblyName = $"test_{Guid.NewGuid():N}";
        var tempDir = Path.Combine(Path.GetTempPath(), $"sharpts_{assemblyName}");
        Directory.CreateDirectory(tempDir);

        var dllPath = Path.Combine(tempDir, $"{assemblyName}.dll");

        // Compile
        var lexer = new Lexer(source);
        var tokens = lexer.ScanTokens();
        var parser = new Parser(tokens, decoratorMode);
        var statements = parser.ParseOrThrow();

        var checker = new TypeChecker();
        checker.SetDecoratorMode(decoratorMode);
        var typeMap = checker.Check(statements);

        var deadCodeAnalyzer = new DeadCodeAnalyzer(typeMap);
        var deadCodeInfo = deadCodeAnalyzer.Analyze(statements);

        var compiler = new ILCompiler(assemblyName);
        compiler.SetDecoratorMode(decoratorMode);
        compiler.Compile(statements, typeMap, deadCodeInfo);
        compiler.Save(dllPath);

        // Load assembly in-process for reflection
        var assembly = Assembly.LoadFrom(dllPath);

        // Capture output via AsyncLocal so concurrent tests don't fight over Console.Out.
        using var capture = AsyncLocalConsoleRedirector.Capture();
        var programType = assembly.GetType("$Program");
        var mainMethod = programType?.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
        mainMethod?.Invoke(null, null);

        return (assembly, capture.GetOutput().Replace("\r\n", "\n"));
    }

    /// <summary>
    /// Compiles TypeScript source to a .NET DLL with reference assembly mode enabled.
    /// Returns the path to the compiled DLL (in a temp directory that caller should clean up).
    /// </summary>
    /// <param name="source">TypeScript source code</param>
    /// <returns>Tuple of (tempDir, dllPath) - caller must clean up tempDir</returns>
    public static (string tempDir, string dllPath) CompileWithRefAsm(string source)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sharpts_refasm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        var dllPath = Path.Combine(tempDir, "test.dll");

        // Compile
        var lexer = new Lexer(source);
        var tokens = lexer.ScanTokens();
        var parser = new Parser(tokens);
        var statements = parser.ParseOrThrow();

        var checker = new TypeChecker();
        var typeMap = checker.Check(statements);

        var deadCodeAnalyzer = new DeadCodeAnalyzer(typeMap);
        var deadCodeInfo = deadCodeAnalyzer.Analyze(statements);

        // Use reference assembly mode
        var compiler = new ILCompiler("test", preserveConstEnums: false, useReferenceAssemblies: true, sdkPath: null);
        compiler.Compile(statements, typeMap, deadCodeInfo);
        compiler.Save(dllPath);

        // Copy SharpTS.dll and its dependencies for runtime dependency
        var sharpTsDll = typeof(RuntimeTypes).Assembly.Location;
        if (!string.IsNullOrEmpty(sharpTsDll) && File.Exists(sharpTsDll))
        {
            File.Copy(sharpTsDll, Path.Combine(tempDir, "SharpTS.dll"), overwrite: true);

        }

        // Write runtimeconfig.json
        var configPath = Path.Combine(tempDir, "test.runtimeconfig.json");
        File.WriteAllText(configPath, """
            {
              "runtimeOptions": {
                "tfm": "net10.0",
                "framework": {
                  "name": "Microsoft.NETCore.App",
                  "version": "10.0.0"
                }
              }
            }
            """);

        return (tempDir, dllPath);
    }

    /// <summary>
    /// Executes a compiled DLL and returns its console output.
    /// </summary>
    /// <param name="dllPath">Path to the DLL</param>
    /// <returns>Console output</returns>
    public static string ExecuteCompiledDll(string dllPath)
    {
        var workingDir = Path.GetDirectoryName(dllPath)!;
        var psi = new ProcessStartInfo("dotnet", dllPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir
        };

        using var process = Process.Start(psi)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)DefaultTimeout.TotalMilliseconds))
        {
            process.Kill();
            throw new TimeoutException(
                $"Compiled DLL execution exceeded {DefaultTimeout.TotalSeconds}s timeout.");
        }

        var output = outputTask.Result;
        var error = errorTask.Result;

        if (process.ExitCode != 0)
        {
            throw new Exception($"Compiled program exited with code {process.ExitCode}. Stderr: {error}");
        }

        return output.Replace("\r\n", "\n");
    }

    /// <summary>
    /// Runs multiple TypeScript modules through the interpreter and captures console output.
    /// </summary>
    /// <param name="files">Dictionary mapping file paths to source code</param>
    /// <param name="entryPoint">The entry point file path (e.g., "./main.ts")</param>
    /// <returns>Captured console output</returns>
    public static string RunModulesInterpreted(Dictionary<string, string> files, string entryPoint)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sharpts_module_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Write all files to temp directory
            foreach (var (path, content) in files)
            {
                var fullPath = Path.Combine(tempDir, path.TrimStart('.', '/', '\\'));
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(fullPath, content);
            }

            string entryPath = Path.Combine(tempDir, entryPoint.TrimStart('.', '/', '\\'));

            // Run in a task with timeout to prevent infinite hangs (e.g., DNS on macOS)
            var task = Task.Run(() =>
            {
                var sw = new StringWriter();

                var resolver = new ModuleResolver(entryPath);
                var entryModule = resolver.LoadModule(entryPath);
                var allModules = resolver.GetModulesInOrder(entryModule);

                var checker = new TypeChecker();
                var typeMap = checker.CheckModules(allModules, resolver);

                using var interpreter = new Interpreter(stdout: sw, stderr: TextWriter.Null);
                interpreter.InterpretModules(allModules, resolver, typeMap);

                return sw.ToString().Replace("\r\n", "\n");
            });

            try
            {
                if (task.Wait(DefaultTimeout))
                {
                    return task.Result;
                }

                throw new TimeoutException(
                    $"Interpreted module execution exceeded {DefaultTimeout.TotalSeconds}s timeout.");
            }
            catch (AggregateException ex)
            {
                if (ex.InnerExceptions.Count == 1)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                }
                throw;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Compiles multiple TypeScript modules to a .NET DLL, executes it, and captures output.
    /// </summary>
    /// <param name="files">Dictionary mapping file paths to source code</param>
    /// <param name="entryPoint">The entry point file path (e.g., "./main.ts")</param>
    /// <returns>Captured console output from the compiled executable</returns>
    public static string RunModulesCompiled(Dictionary<string, string> files, string entryPoint)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sharpts_module_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Write all files to temp directory
            foreach (var (path, content) in files)
            {
                var fullPath = Path.Combine(tempDir, path.TrimStart('.', '/', '\\'));
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(fullPath, content);
            }

            string entryPath = Path.Combine(tempDir, entryPoint.TrimStart('.', '/', '\\'));
            var dllPath = Path.Combine(tempDir, "test.dll");

            // Load and resolve modules
            var resolver = new ModuleResolver(entryPath);
            var entryModule = resolver.LoadModule(entryPath);
            var allModules = resolver.GetModulesInOrder(entryModule);

            // Type check
            var checker = new TypeChecker();
            var typeMap = checker.CheckModules(allModules, resolver);

            // Dead code analysis across all modules
            var allStatements = allModules.SelectMany(m => m.Statements).ToList();
            var deadCodeAnalyzer = new DeadCodeAnalyzer(typeMap);
            var deadCodeInfo = deadCodeAnalyzer.Analyze(allStatements);

            // Compile
            var compiler = new ILCompiler("test");
            compiler.CompileModules(allModules, resolver, typeMap, deadCodeInfo);
            compiler.Save(dllPath);

            // Copy SharpTS.dll and its dependencies for runtime dependency
            var sharpTsDll = typeof(RuntimeTypes).Assembly.Location;
            if (!string.IsNullOrEmpty(sharpTsDll) && File.Exists(sharpTsDll))
            {
                File.Copy(sharpTsDll, Path.Combine(tempDir, "SharpTS.dll"), overwrite: true);

                // Copy ZstdSharp.dll (required for zstd compression in compiled mode)
                var zstdDll = Path.Combine(Path.GetDirectoryName(sharpTsDll)!, "ZstdSharp.dll");
                if (File.Exists(zstdDll))
                {
                    File.Copy(zstdDll, Path.Combine(tempDir, "ZstdSharp.dll"), overwrite: true);
                }
            }

            // Write runtimeconfig.json
            var configPath = Path.Combine(tempDir, "test.runtimeconfig.json");
            File.WriteAllText(configPath, """
                {
                  "runtimeOptions": {
                    "tfm": "net10.0",
                    "framework": {
                      "name": "Microsoft.NETCore.App",
                      "version": "10.0.0"
                    }
                  }
                }
                """);

            // Execute and capture output
            var psi = new ProcessStartInfo("dotnet", dllPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = tempDir
            };

            using var process = Process.Start(psi)!;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)DefaultTimeout.TotalMilliseconds))
            {
                var partialOut = outputTask.IsCompleted ? outputTask.Result : "(reading)";
                var partialErr = errorTask.IsCompleted ? errorTask.Result : "(reading)";
                process.Kill();
                throw new TimeoutException(
                    $"Compiled module execution exceeded {DefaultTimeout.TotalSeconds}s timeout. " +
                    $"Stdout: [{partialOut}] Stderr: [{partialErr}]");
            }

            var output = outputTask.Result;
            var error = errorTask.Result;

            if (process.ExitCode != 0)
            {
                throw new Exception($"Compiled program exited with code {process.ExitCode}. Stderr: {error}");
            }

            // Normalize line endings for cross-platform test consistency
            return output.Replace("\r\n", "\n");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Verifies the IL in a compiled assembly.
    /// </summary>
    /// <param name="dllPath">Path to the compiled DLL</param>
    /// <returns>List of verification error messages (empty if valid)</returns>
    public static List<string> VerifyIL(string dllPath)
    {
        var sdkPath = SdkResolver.FindReferenceAssembliesPath();
        if (sdkPath == null)
            throw new InvalidOperationException("Could not find .NET SDK reference assemblies for IL verification");

        using var verifier = new ILVerifier(sdkPath);
        using var stream = File.OpenRead(dllPath);
        return verifier.Verify(stream);
    }

    /// <summary>
    /// Compiles TypeScript source and verifies the generated IL without running.
    /// </summary>
    /// <param name="source">TypeScript source code</param>
    /// <param name="decoratorMode">Decorator mode</param>
    /// <returns>List of verification errors</returns>
    public static List<string> CompileAndVerifyOnly(string source, DecoratorMode decoratorMode = DecoratorMode.None)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sharpts_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dllPath = Path.Combine(tempDir, "test.dll");

            // Compile
            var lexer = new Lexer(source);
            var tokens = lexer.ScanTokens();
            var parser = new Parser(tokens, decoratorMode);
            var statements = parser.ParseOrThrow();

            var checker = new TypeChecker();
            checker.SetDecoratorMode(decoratorMode);
            var typeMap = checker.Check(statements);

            var deadCodeAnalyzer = new DeadCodeAnalyzer(typeMap);
            var deadCodeInfo = deadCodeAnalyzer.Analyze(statements);

            // Use reference assemblies for IL verification compatibility
            var sdkPath = SdkResolver.FindReferenceAssembliesPath();
            var compiler = new ILCompiler("test", preserveConstEnums: false, useReferenceAssemblies: true, sdkPath: sdkPath);
            compiler.SetDecoratorMode(decoratorMode);
            compiler.Compile(statements, typeMap, deadCodeInfo);
            compiler.Save(dllPath);

            // Verify IL - filter out expected "Failed to load assembly 'SharpTS'" errors
            var allErrors = VerifyIL(dllPath);
            return allErrors.Where(e => !e.Contains("Failed to load assembly")).ToList();
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Compiles TypeScript source and verifies the generated IL.
    /// </summary>
    /// <param name="source">TypeScript source code</param>
    /// <param name="decoratorMode">Decorator mode</param>
    /// <returns>Tuple of (verification errors, console output)</returns>
    public static (List<string> errors, string output) CompileVerifyAndRun(string source, DecoratorMode decoratorMode = DecoratorMode.None)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sharpts_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dllPath = Path.Combine(tempDir, "test.dll");

            // Compile
            var lexer = new Lexer(source);
            var tokens = lexer.ScanTokens();
            var parser = new Parser(tokens, decoratorMode);
            var statements = parser.ParseOrThrow();

            var checker = new TypeChecker();
            checker.SetDecoratorMode(decoratorMode);
            var typeMap = checker.Check(statements);

            var deadCodeAnalyzer = new DeadCodeAnalyzer(typeMap);
            var deadCodeInfo = deadCodeAnalyzer.Analyze(statements);

            // Use reference assemblies for IL verification compatibility
            var sdkPath = SdkResolver.FindReferenceAssembliesPath();
            var compiler = new ILCompiler("test", preserveConstEnums: false, useReferenceAssemblies: true, sdkPath: sdkPath);
            compiler.SetDecoratorMode(decoratorMode);
            compiler.Compile(statements, typeMap, deadCodeInfo);
            compiler.Save(dllPath);

            // Verify IL - filter out expected "Failed to load assembly 'SharpTS'" errors
            var allErrors = VerifyIL(dllPath);
            var errors = allErrors.Where(e => !e.Contains("Failed to load assembly")).ToList();

            // Copy SharpTS.dll and its dependencies for runtime dependency
            var sharpTsDll = typeof(RuntimeTypes).Assembly.Location;
            if (!string.IsNullOrEmpty(sharpTsDll) && File.Exists(sharpTsDll))
            {
                File.Copy(sharpTsDll, Path.Combine(tempDir, "SharpTS.dll"), overwrite: true);

                // Copy ZstdSharp.dll (required for zstd compression in compiled mode)
                var zstdDll = Path.Combine(Path.GetDirectoryName(sharpTsDll)!, "ZstdSharp.dll");
                if (File.Exists(zstdDll))
                {
                    File.Copy(zstdDll, Path.Combine(tempDir, "ZstdSharp.dll"), overwrite: true);
                }
            }

            // Write runtimeconfig.json
            var configPath = Path.Combine(tempDir, "test.runtimeconfig.json");
            File.WriteAllText(configPath, """
                {
                  "runtimeOptions": {
                    "tfm": "net10.0",
                    "framework": {
                      "name": "Microsoft.NETCore.App",
                      "version": "10.0.0"
                    }
                  }
                }
                """);

            // Execute and capture output
            var psi = new ProcessStartInfo("dotnet", dllPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = tempDir
            };

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception($"Compiled program exited with code {process.ExitCode}. Stderr: {error}");
            }

            return (errors, output.Replace("\r\n", "\n"));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
