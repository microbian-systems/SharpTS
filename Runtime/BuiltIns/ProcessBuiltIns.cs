using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpTS.Execution;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Provides implementations for Node.js process object members.
/// </summary>
/// <remarks>
/// Contains properties (platform, arch, pid, env, argv) and method implementations (cwd, exit)
/// that back the <c>process.x</c> syntax in TypeScript. Called by <see cref="Interpreter"/>
/// when resolving property access on <see cref="SharpTSProcess"/>. Methods are returned as
/// <see cref="BuiltInMethod"/> instances for uniform invocation.
/// </remarks>
/// <seealso cref="SharpTSProcess"/>
/// <seealso cref="BuiltInMethod"/>
public static class ProcessBuiltIns
{
    // Cache static methods to avoid allocation on every access
    private static readonly BuiltInMethod _cwd = new("cwd", 0, Cwd);
    private static readonly BuiltInMethod _chdir = new("chdir", 1, Chdir);
    private static readonly BuiltInMethod _exit = new("exit", 0, 1, Exit);
    private static readonly BuiltInMethod _hrtime = new("hrtime", 0, 1, Hrtime);
    private static readonly BuiltInMethod _uptime = new("uptime", 0, Uptime);
    private static readonly BuiltInMethod _memoryUsage = new("memoryUsage", 0, MemoryUsage);
    private static readonly BuiltInMethod _nextTick = new("nextTick", 1, int.MaxValue, NextTick);

    // Lazily create env and argv objects
    private static SharpTSObject? _envObject;
    private static SharpTSArray? _argvArray;

    // Script arguments set by the caller (for interpreted mode)
    private static string? _scriptPath;
    private static string[]? _scriptArgs;

    // Process start time for uptime calculation
    private static readonly DateTime _processStartTime = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    // Stopwatch frequency for hrtime calculations
    private static readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private static readonly double _tickFrequency = Stopwatch.Frequency;

    /// <summary>
    /// Gets a member of the process object by name.
    /// </summary>
    public static object? GetMember(string name)
    {
        return name switch
        {
            // Properties
            "platform" => GetPlatform(),
            "arch" => GetArch(),
            "pid" => (double)Environment.ProcessId,
            "version" => "v" + Environment.Version.ToString(),
            "env" => GetEnv(),
            "argv" => GetArgv(),
            "exitCode" => (double)Environment.ExitCode,

            // Stream objects
            "stdin" => SharpTSStdin.Instance,
            "stdout" => SharpTSStdout.Instance,
            "stderr" => SharpTSStderr.Instance,

            // Methods
            "cwd" => _cwd,
            "chdir" => _chdir,
            "exit" => _exit,
            "hrtime" => _hrtime,
            "uptime" => _uptime,
            "memoryUsage" => _memoryUsage,
            "nextTick" => _nextTick,

            // Cluster worker IPC methods
            "send" when ClusterContext.IsWorker => new BuiltInMethod("send", 1, (interp, recv, args) =>
            {
                if (args.Count == 0)
                    throw new Exception("process.send() requires at least one argument");
                ClusterContext.CurrentWorker?.PostMessageToPrimary(args[0]);
                return true;
            }),
            "disconnect" when ClusterContext.IsWorker => new BuiltInMethod("disconnect", 0, (interp, recv, args) =>
            {
                // Signal disconnect to primary
                try { ClusterContext.PrimaryToWorkerQueue?.CompleteAdding(); } catch { }
                return null;
            }),
            "connected" when ClusterContext.IsWorker => !ClusterContext.CancellationToken.IsCancellationRequested,

            // EventEmitter methods - delegate to the process singleton
            "on" or "addListener" or "once" or "off" or "removeListener"
                or "emit" or "removeAllListeners" or "listeners" or "rawListeners"
                or "listenerCount" or "eventNames" or "prependListener"
                or "prependOnceListener" or "setMaxListeners" or "getMaxListeners"
                => SharpTSProcess.Instance.GetMember(name),

            _ => null
        };
    }

    /// <summary>
    /// Returns the operating system platform (win32, linux, darwin).
    /// </summary>
    public static string GetPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "win32";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "darwin";
        return "unknown";
    }

    /// <summary>
    /// Returns the CPU architecture (x64, arm64, ia32, arm).
    /// </summary>
    public static string GetArch()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "ia32",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Sets the script path and arguments for process.argv.
    /// Call this before interpreting a script to populate process.argv correctly.
    /// </summary>
    /// <param name="scriptPath">The absolute path to the script being run.</param>
    /// <param name="args">The user-provided arguments to pass to the script.</param>
    public static void SetScriptArguments(string scriptPath, string[] args)
    {
        _scriptPath = scriptPath;
        _scriptArgs = args;
        _argvArray = null; // Clear cache to rebuild with new arguments
    }

    /// <summary>
    /// Clears the script arguments. Call this for REPL mode or tests to reset state.
    /// </summary>
    public static void ClearScriptArguments()
    {
        _scriptPath = null;
        _scriptArgs = null;
        _argvArray = null;
    }

    /// <summary>
    /// Returns the process.env object containing environment variables.
    /// </summary>
    public static SharpTSObject GetEnv()
    {
        if (_envObject != null)
            return _envObject;

        var fields = new Dictionary<string, object?>();
        var envVars = Environment.GetEnvironmentVariables();
        foreach (System.Collections.DictionaryEntry entry in envVars)
        {
            fields[entry.Key?.ToString() ?? ""] = entry.Value?.ToString();
        }
        _envObject = new SharpTSObject(fields);
        return _envObject;
    }

    /// <summary>
    /// Returns the process.argv array containing command line arguments.
    /// Mimics Node.js argv format: [runtime_path, script_path, ...args]
    /// </summary>
    public static SharpTSArray GetArgv()
    {
        if (_argvArray != null)
            return _argvArray;

        var elements = new List<object?>();

        // Node.js format: [node_path, script_path, ...user_args]
        // argv[0] = executable path (runtime)
        // argv[1] = script path
        // argv[2+] = user arguments

        // Always use ProcessPath for argv[0] (the runtime)
        var cmdArgs = Environment.GetCommandLineArgs();
        elements.Add(Environment.ProcessPath ?? cmdArgs[0]);

        // If script arguments were explicitly set (interpreted mode), use them
        if (_scriptPath != null)
        {
            elements.Add(_scriptPath);
            if (_scriptArgs != null)
            {
                foreach (var arg in _scriptArgs)
                {
                    elements.Add(arg);
                }
            }
        }
        else
        {
            // Fall back to command line args (for compiled mode or when not explicitly set)
            // Skip args[0] (the DLL path) since it's not user-relevant
            for (int i = 1; i < cmdArgs.Length; i++)
            {
                elements.Add(cmdArgs[i]);
            }
        }

        _argvArray = new SharpTSArray(elements);
        return _argvArray;
    }

    private static object? Cwd(Interpreter i, object? r, List<object?> args)
    {
        return Directory.GetCurrentDirectory();
    }

    private static object? Chdir(Interpreter i, object? r, List<object?> args)
    {
        if (args.Count > 0 && args[0] is string dir)
        {
            Directory.SetCurrentDirectory(dir);
        }
        return null;
    }

    private static object? Exit(Interpreter i, object? r, List<object?> args)
    {
        int exitCode = 0;
        if (args.Count > 0 && args[0] is double d)
        {
            exitCode = (int)d;
        }

        // Emit 'exit' event synchronously before exiting, matching Node.js behavior
        EmitExitEvent(i, exitCode);

        Environment.Exit(exitCode);
        return null; // Never reached
    }

    /// <summary>
    /// Emits the 'exit' event on the process object.
    /// Called before process.exit() and at natural program end.
    /// In Node.js, the exit event callback receives the exit code as argument.
    /// </summary>
    public static void EmitExitEvent(Interpreter? interpreter, int exitCode)
    {
        try
        {
            var process = SharpTSProcess.Instance;
            if (interpreter != null)
            {
                // Use interpreter-based emit for interpreted mode
                var emit = process.GetMember("emit") as BuiltInMethod;
                emit?.Bind(process).Call(interpreter, ["exit", (double)exitCode]);
            }
            else
            {
                // Use direct emit for compiled mode (no interpreter available)
                process.EmitDirect("exit", (double)exitCode);
            }
        }
        catch
        {
            // Node.js ignores errors in exit handlers
        }
    }

    /// <summary>
    /// Dispatches an EventEmitter method call on the process singleton.
    /// Used by compiled code to call process.on(), process.emit(), etc.
    /// </summary>
    /// <param name="methodName">The EventEmitter method name (on, once, emit, etc.).</param>
    /// <param name="args">The arguments to pass to the method.</param>
    /// <returns>The result of the method call.</returns>
    public static object? EventEmitterCall(string methodName, object?[] args)
    {
        var process = SharpTSProcess.Instance;

        // For "on", "once", "addListener": register listener directly
        switch (methodName)
        {
            case "on":
            case "addListener":
                if (args.Length >= 2 && args[0] is string onEvent)
                {
                    process.AddListenerDirect(onEvent, args[1]!, once: false);
                    return process;
                }
                return process;

            case "once":
                if (args.Length >= 2 && args[0] is string onceEvent)
                {
                    process.AddListenerDirect(onceEvent, args[1]!, once: true);
                    return process;
                }
                return process;

            case "emit":
                if (args.Length >= 1 && args[0] is string emitEvent)
                {
                    var emitArgs = args.Length > 1 ? args[1..] : [];
                    return process.EmitDirect(emitEvent, emitArgs);
                }
                return false;

            case "off":
            case "removeListener":
                // Delegate to GetMember for complex operations
                var offMethod = process.GetMember(methodName) as BuiltInMethod;
                if (offMethod != null)
                {
                    return offMethod.Bind(process).Call(null!, args.ToList<object?>());
                }
                return process;

            case "removeAllListeners":
                var removeMethod = process.GetMember(methodName) as BuiltInMethod;
                if (removeMethod != null)
                {
                    return removeMethod.Bind(process).Call(null!, args.ToList<object?>());
                }
                return process;

            case "listenerCount":
                var lcMethod = process.GetMember(methodName) as BuiltInMethod;
                if (lcMethod != null)
                {
                    return lcMethod.Bind(process).Call(null!, args.ToList<object?>());
                }
                return 0.0;

            case "listeners":
            case "eventNames":
            case "setMaxListeners":
            case "getMaxListeners":
            case "prependListener":
            case "prependOnceListener":
                var method = process.GetMember(methodName) as BuiltInMethod;
                if (method != null)
                {
                    return method.Bind(process).Call(null!, args.ToList<object?>());
                }
                return process;

            default:
                return null;
        }
    }

    /// <summary>
    /// Handles uncaught exceptions by emitting 'uncaughtException' on process.
    /// Returns true if the exception was handled (a listener was registered).
    /// </summary>
    public static bool EmitUncaughtException(Interpreter? interpreter, Exception exception)
    {
        try
        {
            var process = SharpTSProcess.Instance;
            // Create an Error-like object for the exception
            var errorObj = new SharpTSObject(new Dictionary<string, object?>
            {
                ["message"] = exception.Message,
                ["stack"] = exception.StackTrace ?? "",
                ["name"] = exception.GetType().Name
            });

            if (interpreter != null)
            {
                var emit = process.GetMember("emit") as BuiltInMethod;
                var result = emit?.Bind(process).Call(interpreter, ["uncaughtException", errorObj]);
                return result is true;
            }
            else
            {
                return process.EmitDirect("uncaughtException", errorObj);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the current high-resolution real time in a [seconds, nanoseconds] tuple.
    /// If a previous hrtime result is passed, returns the difference.
    /// </summary>
    private static object? Hrtime(Interpreter i, object? r, List<object?> args)
    {
        long currentTicks = Stopwatch.GetTimestamp() - _startTimestamp;
        double totalNanoseconds = currentTicks * 1_000_000_000.0 / _tickFrequency;

        // If a previous time is provided, calculate the difference
        if (args.Count > 0 && args[0] is SharpTSArray prev && prev.Elements.Count >= 2)
        {
            var prevSeconds = Convert.ToDouble(prev.Elements[0]);
            var prevNanos = Convert.ToDouble(prev.Elements[1]);
            double prevTotalNanos = prevSeconds * 1_000_000_000.0 + prevNanos;
            totalNanoseconds -= prevTotalNanos;
        }

        double seconds = Math.Floor(totalNanoseconds / 1_000_000_000.0);
        double nanos = totalNanoseconds % 1_000_000_000.0;

        // Ensure non-negative values
        if (seconds < 0) seconds = 0;
        if (nanos < 0) nanos = 0;

        return new SharpTSArray([seconds, nanos]);
    }

    /// <summary>
    /// Returns the number of seconds the process has been running.
    /// </summary>
    private static object? Uptime(Interpreter i, object? r, List<object?> args)
    {
        return (DateTime.UtcNow - _processStartTime).TotalSeconds;
    }

    /// <summary>
    /// Returns an object describing the memory usage of the process.
    /// </summary>
    private static object? MemoryUsage(Interpreter i, object? r, List<object?> args)
    {
        var process = Process.GetCurrentProcess();
        var heap = (double)GC.GetTotalMemory(false);

        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["rss"] = (double)process.WorkingSet64,
            ["heapTotal"] = heap,
            ["heapUsed"] = heap,
            ["external"] = 0.0,
            ["arrayBuffers"] = 0.0
        });
    }

    /// <summary>
    /// Sets a member of the process object by name.
    /// Currently only supports setting exitCode.
    /// </summary>
    public static bool SetMember(string name, object? value)
    {
        return name switch
        {
            "exitCode" when value is double d => SetExitCode((int)d),
            _ => false
        };
    }

    private static bool SetExitCode(int code)
    {
        Environment.ExitCode = code;
        return true;
    }

    /// <summary>
    /// process.nextTick(callback, ...args) - schedules callback to run after the current operation.
    /// </summary>
    /// <remarks>
    /// In Node.js, nextTick callbacks run before any I/O events. Since SharpTS uses a simplified
    /// event loop, we implement this as a timer with 0 delay (similar to setImmediate).
    /// </remarks>
    private static object? NextTick(Interpreter interpreter, object? receiver, List<object?> args)
    {
        if (args.Count == 0)
            throw new Exception("Runtime Error: process.nextTick requires at least 1 argument");

        var callback = args[0] as ISharpTSCallable
            ?? throw new Exception("Runtime Error: process.nextTick callback must be a function");

        var callbackArgs = args.Count > 1
            ? args.Skip(1).ToList()
            : new List<object?>();

        // Schedule as a timer with 0 delay (runs as soon as possible)
        TimerBuiltIns.SetTimeout(interpreter, callback, 0, callbackArgs);

        // nextTick returns undefined (void)
        return null;
    }
}
