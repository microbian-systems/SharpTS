using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpTS.Runtime.Types;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of the Node.js 'child_process' module.
/// </summary>
public static class ChildProcessModuleInterpreter
{
    /// <summary>
    /// Gets all exported values for the child_process module.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            ["execSync"] = BuiltInMethod.CreateV2("execSync", 1, 2, ExecSync),
            ["spawnSync"] = BuiltInMethod.CreateV2("spawnSync", 1, 3, SpawnSync),
            ["exec"] = BuiltInMethod.CreateV2("exec", 1, 3, Exec),
            ["spawn"] = BuiltInMethod.CreateV2("spawn", 1, 3, Spawn),
            ["execFileSync"] = BuiltInMethod.CreateV2("execFileSync", 1, 3, ExecFileSync),
            ["execFile"] = BuiltInMethod.CreateV2("execFile", 1, 4, ExecFile),
            ["fork"] = BuiltInMethod.CreateV2("fork", 1, 3, Fork)
        };
    }

    private static RuntimeValue ExecSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || args[0].ToObject() is not string command)
            throw new Exception("child_process.execSync requires a command string");

        var options = args.Length > 1 ? args[1].ToObject() as SharpTSObject : null;
        var cwd = GetStringOption(options, "cwd");
        var timeout = GetDoubleOption(options, "timeout", -1);

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Use shell to execute command
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/c {command}";
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
        }

        if (!string.IsNullOrEmpty(cwd))
            startInfo.WorkingDirectory = cwd;

        // Apply environment variables from options
        ApplyEnvOptions(options, startInfo);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdout = process.StandardOutput.ReadToEnd();

        if (timeout > 0)
        {
            if (!process.WaitForExit((int)timeout))
            {
                process.Kill();
                throw new Exception("Command timed out");
            }
        }
        else
        {
            process.WaitForExit();
        }

        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new Exception($"Command failed with exit code {process.ExitCode}: {stderr}");
        }

        return RuntimeValue.FromString(stdout);
    }

    private static RuntimeValue SpawnSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || args[0].ToObject() is not string command)
            throw new Exception("child_process.spawnSync requires a command");

        var cmdArgs = new List<string>();
        if (args.Length > 1 && args[1].ToObject() is SharpTSArray argsArray)
        {
            foreach (var arg in argsArray)
            {
                cmdArgs.Add(arg?.ToString() ?? "");
            }
        }

        var options = args.Length > 2 ? args[2].ToObject() as SharpTSObject : null;
        var cwd = GetStringOption(options, "cwd");
        var useShell = GetBoolOption(options, "shell", false);

        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in cmdArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrEmpty(cwd))
            startInfo.WorkingDirectory = cwd;

        // Apply environment variables from options
        ApplyEnvOptions(options, startInfo);

        string stdout, stderr;
        int exitCode;
        string? signal = null;

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            stdout = process.StandardOutput.ReadToEnd();
            stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            return RuntimeValue.FromObject(new SharpTSObject(new Dictionary<string, object?>
            {
                ["stdout"] = "",
                ["stderr"] = "",
                ["status"] = (double)-1,
                ["signal"] = null,
                ["error"] = ex.Message
            }));
        }

        return RuntimeValue.FromObject(new SharpTSObject(new Dictionary<string, object?>
        {
            ["stdout"] = stdout,
            ["stderr"] = stderr,
            ["status"] = (double)exitCode,
            ["signal"] = signal
        }));
    }

    /// <summary>
    /// exec(command, options?, callback?) - Executes a command asynchronously.
    /// Returns a ChildProcess object. Calls callback(error, stdout, stderr) when done.
    /// </summary>
    private static RuntimeValue Exec(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || args[0].ToObject() is not string command)
            throw new Exception("child_process.exec requires a command string");

        SharpTSObject? options = null;
        ISharpTSCallable? callback = null;

        // Parse arguments: exec(command, options?, callback?)
        if (args.Length > 1)
        {
            if (args[1].ToObject() is ISharpTSCallable cb1)
            {
                callback = cb1;
            }
            else
            {
                options = args[1].ToObject() as SharpTSObject;
                if (args.Length > 2 && args[2].ToObject() is ISharpTSCallable cb2)
                {
                    callback = cb2;
                }
            }
        }

        var cwd = GetStringOption(options, "cwd");
        var timeout = GetDoubleOption(options, "timeout", -1);

        // Create the ChildProcess event emitter
        var childProcess = new SharpTSChildProcess();

        // Keep the event loop alive while the child runs; the terminal callback /
        // lifecycle events are marshalled back onto the loop (EnqueueCallback) so they
        // run on the loop thread AFTER the synchronous script registers its listeners —
        // matching Node's "deferred to a future tick" guarantee.
        interpreter.Ref();
        Task.Run(() =>
        {
            string stdout = "", stderr = "";
            int code = 0, kind = 0; // kind: 0 normal, 1 timeout, 2 exception
            object? error = null;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    startInfo.FileName = "cmd.exe";
                    startInfo.Arguments = $"/c {command}";
                }
                else
                {
                    startInfo.FileName = "/bin/sh";
                    startInfo.Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
                }

                if (!string.IsNullOrEmpty(cwd))
                    startInfo.WorkingDirectory = cwd;

                ApplyEnvOptions(options, startInfo);

                using var process = new Process { StartInfo = startInfo };
                process.Start();
                childProcess.SetPid(process.Id);
                childProcess.SetProcess(process);

                stdout = process.StandardOutput.ReadToEnd();
                stderr = process.StandardError.ReadToEnd();

                if (timeout > 0)
                {
                    if (!process.WaitForExit((int)timeout))
                    {
                        process.Kill();
                        kind = 1;
                    }
                    else { code = process.ExitCode; }
                }
                else
                {
                    process.WaitForExit();
                    code = process.ExitCode;
                }

                if (kind == 0 && code != 0)
                    error = new SharpTSObject(new Dictionary<string, object?>
                    {
                        ["message"] = $"Command failed with exit code {code}",
                        ["code"] = (double)code
                    });
            }
            catch (Exception ex)
            {
                kind = 2;
                error = new SharpTSObject(new Dictionary<string, object?> { ["message"] = ex.Message });
            }

            interpreter.EnqueueCallback(() =>
            {
                try
                {
                    if (kind == 2)
                    {
                        childProcess.EmitWith(interpreter, "error", error);
                        callback?.CallBoxed(interpreter, [error, "", ""]);
                    }
                    else if (kind == 1)
                    {
                        childProcess.SetExitCode(-1);
                        var timeoutErr = new SharpTSObject(new Dictionary<string, object?> { ["message"] = "Command timed out" });
                        childProcess.EmitWith(interpreter, "error", timeoutErr);
                        callback?.CallBoxed(interpreter, [timeoutErr, stdout, stderr]);
                    }
                    else
                    {
                        childProcess.SetExitCode(code);
                        callback?.CallBoxed(interpreter, [error, stdout, stderr]);
                        childProcess.EmitWith(interpreter, "close", (double)code);
                        childProcess.EmitWith(interpreter, "exit", (double)code);
                    }
                }
                finally { interpreter.Unref(); }
            });
        });

        return RuntimeValue.FromObject(childProcess);
    }

    /// <summary>
    /// spawn(command, args?, options?) - Spawns a process asynchronously.
    /// Returns a ChildProcess object with stdout/stderr streams.
    /// </summary>
    private static RuntimeValue Spawn(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || args[0].ToObject() is not string command)
            throw new Exception("child_process.spawn requires a command");

        var cmdArgs = new List<string>();
        SharpTSObject? options = null;

        if (args.Length > 1 && args[1].ToObject() is SharpTSArray argsArray)
        {
            foreach (var arg in argsArray)
            {
                cmdArgs.Add(arg?.ToString() ?? "");
            }
            if (args.Length > 2)
                options = args[2].ToObject() as SharpTSObject;
        }
        else if (args.Length > 1)
        {
            options = args[1].ToObject() as SharpTSObject;
        }

        var cwd = GetStringOption(options, "cwd");
        var (useShell, shellPath) = GetShellOption(options);

        // Create the ChildProcess event emitter with streams
        var childProcess = new SharpTSChildProcess();
        var stdoutStream = new SharpTSReadable();
        var stderrStream = new SharpTSReadable();
        var stdinStream = new SharpTSWritable();
        childProcess.SetStdoutStream(stdoutStream);
        childProcess.SetStderrStream(stderrStream);
        childProcess.SetStdinStream(stdinStream);

        // Start synchronously so child.stdin.write()/.end() on the same tick reach a live
        // StandardInput; a spawn failure is surfaced as an async 'error' event.
        Process process;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };

            if (useShell)
            {
                // Run "command args" through a shell (cmd.exe / sh), matching exec().
                ApplyShellCommand(startInfo, command, cmdArgs, shellPath);
            }
            else
            {
                startInfo.FileName = command;
                foreach (var arg in cmdArgs)
                    startInfo.ArgumentList.Add(arg);
            }

            if (!string.IsNullOrEmpty(cwd))
                startInfo.WorkingDirectory = cwd;

            ApplyEnvOptions(options, startInfo);

            process = new Process { StartInfo = startInfo };
            process.Start();
            childProcess.SetPid(process.Id);
            childProcess.SetProcess(process);

            // Wire stdin: forwarding write() to the started process (write() runs on the loop thread).
            stdinStream.SetWriteCallback(BuiltInMethod.CreateV2("write", 1, 3, (interp, recv, wargs) =>
            {
                if (wargs.Length > 0 && !wargs[0].IsNull)
                {
                    try
                    {
                        process.StandardInput.Write(wargs[0].ToObject()!.ToString());
                        process.StandardInput.Flush();
                    }
                    catch { }
                }
                if (wargs.Length > 2 && wargs[2].ToObject() is ISharpTSCallable cb)
                    cb.Call(interpreter, [null]);
                else if (wargs.Length > 1 && wargs[1].ToObject() is ISharpTSCallable cb2)
                    cb2.Call(interpreter, [null]);
                return RuntimeValue.True;
            }));

            // stdin.end() closes the child's StandardInput so it sees EOF.
            stdinStream.SetFinalCallback(BuiltInMethod.CreateV2("final", 0, 1, (interp, recv, fargs) =>
            {
                try { process.StandardInput.Close(); }
                catch { }
                if (fargs.Length > 0 && fargs[0].ToObject() is ISharpTSCallable done)
                    done.Call(interp, [null]);
                return RuntimeValue.Undefined;
            }));
        }
        catch (Exception ex)
        {
            interpreter.Ref();
            interpreter.EnqueueCallback(() =>
            {
                try
                {
                    childProcess.EmitWith(interpreter, "error", new SharpTSObject(new Dictionary<string, object?>
                    {
                        ["message"] = ex.Message
                    }));
                }
                finally { interpreter.Unref(); }
            });
            return RuntimeValue.FromObject(childProcess);
        }

        // Keep the loop alive while the child runs; stream chunks and lifecycle events are
        // marshalled onto the loop thread (PushFromHost / EmitWith) so they run with a real
        // interpreter and after the synchronous script has attached its listeners.
        var startedProcess = process;
        interpreter.Ref();
        Task.Run(() =>
        {
            try
            {
                var stdoutTask = Task.Run(() =>
                {
                    var buffer = new char[4096];
                    int read;
                    while ((read = startedProcess.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        var chunk = new string(buffer, 0, read);
                        interpreter.EnqueueCallback(() => stdoutStream.PushFromHost(interpreter, chunk));
                    }
                    interpreter.EnqueueCallback(() => stdoutStream.PushFromHost(interpreter, null));
                });

                var stderrTask = Task.Run(() =>
                {
                    var buffer = new char[4096];
                    int read;
                    while ((read = startedProcess.StandardError.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        var chunk = new string(buffer, 0, read);
                        interpreter.EnqueueCallback(() => stderrStream.PushFromHost(interpreter, chunk));
                    }
                    interpreter.EnqueueCallback(() => stderrStream.PushFromHost(interpreter, null));
                });

                startedProcess.WaitForExit();
                stdoutTask.Wait();
                stderrTask.Wait();
                int code = startedProcess.ExitCode;

                interpreter.EnqueueCallback(() =>
                {
                    try
                    {
                        childProcess.SetExitCode(code);
                        childProcess.EmitWith(interpreter, "close", (double)code);
                        childProcess.EmitWith(interpreter, "exit", (double)code);
                    }
                    finally { interpreter.Unref(); }
                });
            }
            catch (Exception ex)
            {
                interpreter.EnqueueCallback(() =>
                {
                    try
                    {
                        childProcess.EmitWith(interpreter, "error", new SharpTSObject(new Dictionary<string, object?>
                        {
                            ["message"] = ex.Message
                        }));
                    }
                    finally { interpreter.Unref(); }
                });
            }
        });

        return RuntimeValue.FromObject(childProcess);
    }

    /// <summary>
    /// execFileSync(file, args?, options?) - Executes a file synchronously without a shell.
    /// Returns stdout as a string. Throws on non-zero exit code.
    /// </summary>
    private static RuntimeValue ExecFileSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || args[0].ToObject() is not string file)
            throw new Exception("child_process.execFileSync requires a file path");

        var cmdArgs = new List<string>();
        SharpTSObject? options = null;

        if (args.Length > 1 && args[1].ToObject() is SharpTSArray argsArray)
        {
            foreach (var arg in argsArray)
                cmdArgs.Add(arg?.ToString() ?? "");
            if (args.Length > 2)
                options = args[2].ToObject() as SharpTSObject;
        }
        else if (args.Length > 1)
        {
            options = args[1].ToObject() as SharpTSObject;
        }

        var cwd = GetStringOption(options, "cwd");
        var timeout = GetDoubleOption(options, "timeout", -1);

        var startInfo = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in cmdArgs)
            startInfo.ArgumentList.Add(arg);

        if (!string.IsNullOrEmpty(cwd))
            startInfo.WorkingDirectory = cwd;

        ApplyEnvOptions(options, startInfo);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdout = process.StandardOutput.ReadToEnd();

        if (timeout > 0)
        {
            if (!process.WaitForExit((int)timeout))
            {
                process.Kill();
                throw new Exception("Command timed out");
            }
        }
        else
        {
            process.WaitForExit();
        }

        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new Exception($"Command failed with exit code {process.ExitCode}: {stderr}");
        }

        return RuntimeValue.FromString(stdout);
    }

    /// <summary>
    /// execFile(file, args?, options?, callback?) - Executes a file asynchronously without a shell.
    /// Returns a ChildProcess object. Calls callback(error, stdout, stderr) when done.
    /// </summary>
    private static RuntimeValue ExecFile(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || args[0].ToObject() is not string file)
            throw new Exception("child_process.execFile requires a file path");

        var cmdArgs = new List<string>();
        SharpTSObject? options = null;
        ISharpTSCallable? callback = null;

        // Parse: execFile(file, args?, options?, callback?)
        var nextIdx = 1;
        if (args.Length > nextIdx && args[nextIdx].ToObject() is SharpTSArray argsArray)
        {
            foreach (var arg in argsArray)
                cmdArgs.Add(arg?.ToString() ?? "");
            nextIdx++;
        }

        if (args.Length > nextIdx)
        {
            if (args[nextIdx].ToObject() is ISharpTSCallable cb)
            {
                callback = cb;
            }
            else
            {
                options = args[nextIdx].ToObject() as SharpTSObject;
                nextIdx++;
                if (args.Length > nextIdx && args[nextIdx].ToObject() is ISharpTSCallable cb2)
                    callback = cb2;
            }
        }

        var cwd = GetStringOption(options, "cwd");
        var timeout = GetDoubleOption(options, "timeout", -1);

        var childProcess = new SharpTSChildProcess();

        interpreter.Ref();
        Task.Run(() =>
        {
            string stdout = "", stderr = "";
            int code = 0, kind = 0; // kind: 0 normal, 1 timeout, 2 exception
            object? error = null;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = file,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                foreach (var arg in cmdArgs)
                    startInfo.ArgumentList.Add(arg);

                if (!string.IsNullOrEmpty(cwd))
                    startInfo.WorkingDirectory = cwd;

                ApplyEnvOptions(options, startInfo);

                using var process = new Process { StartInfo = startInfo };
                process.Start();
                childProcess.SetPid(process.Id);
                childProcess.SetProcess(process);

                stdout = process.StandardOutput.ReadToEnd();
                stderr = process.StandardError.ReadToEnd();

                if (timeout > 0)
                {
                    if (!process.WaitForExit((int)timeout))
                    {
                        process.Kill();
                        kind = 1;
                    }
                    else { code = process.ExitCode; }
                }
                else
                {
                    process.WaitForExit();
                    code = process.ExitCode;
                }

                if (kind == 0 && code != 0)
                    error = new SharpTSObject(new Dictionary<string, object?>
                    {
                        ["message"] = $"Command failed with exit code {code}",
                        ["code"] = (double)code
                    });
            }
            catch (Exception ex)
            {
                kind = 2;
                error = new SharpTSObject(new Dictionary<string, object?> { ["message"] = ex.Message });
            }

            interpreter.EnqueueCallback(() =>
            {
                try
                {
                    if (kind == 2)
                    {
                        childProcess.EmitWith(interpreter, "error", error);
                        callback?.CallBoxed(interpreter, [error, "", ""]);
                    }
                    else if (kind == 1)
                    {
                        childProcess.SetExitCode(-1);
                        var timeoutErr = new SharpTSObject(new Dictionary<string, object?> { ["message"] = "Command timed out" });
                        childProcess.EmitWith(interpreter, "error", timeoutErr);
                        callback?.CallBoxed(interpreter, [timeoutErr, stdout, stderr]);
                    }
                    else
                    {
                        childProcess.SetExitCode(code);
                        callback?.CallBoxed(interpreter, [error, stdout, stderr]);
                        childProcess.EmitWith(interpreter, "close", (double)code);
                        childProcess.EmitWith(interpreter, "exit", (double)code);
                    }
                }
                finally { interpreter.Unref(); }
            });
        });

        return RuntimeValue.FromObject(childProcess);
    }

    /// <summary>
    /// fork(modulePath, args?, options?) - Spawns a new SharpTS process with IPC channel.
    /// Returns a ChildProcess with send()/on('message') support.
    /// </summary>
    private static RuntimeValue Fork(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || args[0].ToObject() is not string modulePath)
            throw new Exception("child_process.fork requires a module path");

        var forkArgs = new List<string>();
        SharpTSObject? options = null;

        if (args.Length > 1 && args[1].ToObject() is SharpTSArray argsArray)
        {
            foreach (var arg in argsArray)
                forkArgs.Add(arg?.ToString() ?? "");
            if (args.Length > 2)
                options = args[2].ToObject() as SharpTSObject;
        }
        else if (args.Length > 1)
        {
            options = args[1].ToObject() as SharpTSObject;
        }

        var cwd = GetStringOption(options, "cwd");
        var childProcess = RunFork(modulePath, forkArgs, options, cwd, interpreter,
            interpreter.Ref, interpreter.Unref, interpreter.EnqueueCallback,
            (cp, ev, arg) => cp.EmitWith(interpreter, ev, arg));
        return RuntimeValue.FromObject(childProcess);
    }

    /// <summary>
    /// Compiled-output bridge for fork (#1017). The emitted <c>$Runtime.ChildProcessFork</c>
    /// resolves this by reflection (keeping the standalone DLL free of a hard SharpTS.dll
    /// reference) and passes the compiled <c>$EventLoop</c>'s Ref/Unref/Schedule so IPC and
    /// lifecycle events marshal onto the compiled loop — mirroring SharpTSWorker.CreateForCompiledLoop.
    /// </summary>
    public static SharpTSChildProcess ForkForCompiledLoop(
        string modulePath, object? argsObj, object? optionsObj,
        Action eventLoopRef, Action eventLoopUnref, Action<Action> eventLoopSchedule)
    {
        var forkArgs = new List<string>();
        if (argsObj is SharpTSArray a)
            foreach (var x in a)
                forkArgs.Add(x?.ToString() ?? "");
        var options = optionsObj as SharpTSObject;
        var cwd = GetStringOption(options, "cwd");

        return RunFork(modulePath, forkArgs, options, cwd, interp: null,
            eventLoopRef, eventLoopUnref, eventLoopSchedule,
            (cp, ev, arg) => cp.EmitDirect(ev, arg));
    }

    /// <summary>
    /// Shared fork core: spawns the SharpTS runtime to run the child module over a named-pipe
    /// IPC channel, keeping the owning event loop alive (refLoop/unrefLoop) and marshalling
    /// IPC messages + stream chunks + lifecycle events onto it (post). <paramref name="interp"/>
    /// is the interpreter for interp-mode (null in compiled mode — stream pushes/events then
    /// dispatch compiled listeners directly).
    /// </summary>
    private static SharpTSChildProcess RunFork(
        string modulePath, List<string> forkArgs, SharpTSObject? options, string? cwd,
        Interp? interp, Action refLoop, Action unrefLoop, Action<Action> post,
        Action<SharpTSChildProcess, string, object?> emit)
    {
        var resolvedModule = modulePath;
        if (!Path.IsPathRooted(resolvedModule))
        {
            var basePath = cwd ?? Directory.GetCurrentDirectory();
            resolvedModule = Path.GetFullPath(Path.Combine(basePath, resolvedModule));
        }

        var pipeName = $"sharpts-ipc-{Guid.NewGuid():N}";
        var childProcess = new SharpTSChildProcess();
        var cts = new CancellationTokenSource();
        var pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // The child must run the SharpTS interpreter on the .ts module. SharpTS.dll is always
        // the assembly hosting this method — the CLI entry in interp mode, and the co-located
        // runtime in compiled mode (RequireSharpTSRuntime). (Using the *entry* assembly would
        // be wrong when SharpTS is embedded, e.g. the xUnit testhost.)
        var sharpTsPath = typeof(ChildProcessModuleInterpreter).Assembly.Location;
        var processPath = Environment.ProcessPath;
        var processName = processPath != null ? Path.GetFileNameWithoutExtension(processPath) : "";
        bool processIsDotnet = processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        bool processIsSharpTs = processName.Equals("sharpts", StringComparison.OrdinalIgnoreCase);

        if (processIsSharpTs && !string.IsNullOrEmpty(processPath))
        {
            // Running as a self-contained SharpTS executable — run it directly on the module.
            startInfo.FileName = processPath!;
        }
        else
        {
            // Everything else (interp via `dotnet SharpTS.dll`, compiled output, or SharpTS
            // embedded as a library e.g. the test host): run the SharpTS.dll interpreter via
            // the dotnet host — ProcessPath when it is dotnet, otherwise the muxer on PATH.
            startInfo.FileName = processIsDotnet ? processPath! : "dotnet";
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(sharpTsPath);
        }

        startInfo.ArgumentList.Add(resolvedModule);
        foreach (var arg in forkArgs)
            startInfo.ArgumentList.Add(arg);

        startInfo.Environment["SHARPTS_IPC_PIPE"] = pipeName;
        if (!string.IsNullOrEmpty(cwd))
            startInfo.WorkingDirectory = cwd;
        ApplyEnvOptions(options, startInfo);
        startInfo.Environment["SHARPTS_IPC_PIPE"] = pipeName; // survive an env override

        var stdoutStream = new SharpTSReadable();
        var stderrStream = new SharpTSReadable();
        childProcess.SetStdoutStream(stdoutStream);
        childProcess.SetStderrStream(stderrStream);

        refLoop();
        Task.Run(() =>
        {
            try
            {
                var process = new Process { StartInfo = startInfo };
                process.Start();
                childProcess.SetPid(process.Id);
                childProcess.SetProcess(process);

                var connectTask = pipeServer.WaitForConnectionAsync(cts.Token);
                if (!connectTask.Wait(10_000))
                {
                    pipeServer.Dispose();
                    throw new Exception("Child process failed to connect to IPC pipe");
                }

                var writer = new StreamWriter(pipeServer) { AutoFlush = true };
                childProcess.SetupIpc(pipeServer, writer, cts);

                // IPC message reader — marshal each message onto the owning loop.
                var ipcReader = Task.Run(() =>
                {
                    try
                    {
                        using var reader = new StreamReader(pipeServer, leaveOpen: true);
                        while (!cts.Token.IsCancellationRequested)
                        {
                            var line = reader.ReadLine();
                            if (line == null) break;
                            var message = IpcSerializer.Deserialize(line);
                            post(() => emit(childProcess, "message", message));
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                }, cts.Token);

                var stdoutTask = Task.Run(() =>
                {
                    var buffer = new char[4096];
                    int read;
                    while ((read = process.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        var chunk = new string(buffer, 0, read);
                        post(() => stdoutStream.PushFromHost(interp, chunk));
                    }
                    post(() => stdoutStream.PushFromHost(interp, null));
                });

                var stderrTask = Task.Run(() =>
                {
                    var buffer = new char[4096];
                    int read;
                    while ((read = process.StandardError.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        var chunk = new string(buffer, 0, read);
                        post(() => stderrStream.PushFromHost(interp, chunk));
                    }
                    post(() => stderrStream.PushFromHost(interp, null));
                });

                process.WaitForExit();
                cts.Cancel();
                stdoutTask.Wait();
                stderrTask.Wait();
                int code = process.ExitCode;

                post(() =>
                {
                    try
                    {
                        childProcess.SetExitCode(code);
                        emit(childProcess, "close", (double)code);
                        emit(childProcess, "exit", (double)code);
                    }
                    finally { unrefLoop(); }
                });
            }
            catch (Exception ex)
            {
                post(() =>
                {
                    try
                    {
                        emit(childProcess, "error", new SharpTSObject(new Dictionary<string, object?>
                        {
                            ["message"] = ex.Message
                        }));
                    }
                    finally { unrefLoop(); }
                });
            }
        });

        return childProcess;
    }

    private static string? GetStringOption(SharpTSObject? options, string name)
    {
        if (options == null)
            return null;
        var value = options.GetProperty(name);
        // Handle null and undefined as "not set"
        if (value == null || value is SharpTSUndefined)
            return null;
        return value.ToString();
    }

    private static double GetDoubleOption(SharpTSObject? options, string name, double defaultValue)
    {
        if (options == null)
            return defaultValue;
        var value = options.GetProperty(name);
        return value is double d ? d : defaultValue;
    }

    /// <summary>
    /// Reads the `shell` option: true → default platform shell; a string → that shell path;
    /// false/absent → no shell. Returns (useShell, shellPath?).
    /// </summary>
    private static (bool useShell, string? shellPath) GetShellOption(SharpTSObject? options)
    {
        if (options == null)
            return (false, null);
        var value = options.GetProperty("shell");
        if (value is bool b)
            return (b, null);
        if (value is string s && !string.IsNullOrEmpty(s))
            return (true, s);
        return (false, null);
    }

    /// <summary>
    /// Configures a ProcessStartInfo to run "command args" through a shell (cmd.exe /c on
    /// Windows, /bin/sh -c on Unix, or an explicit shell path), mirroring exec().
    /// </summary>
    private static void ApplyShellCommand(ProcessStartInfo startInfo, string command, List<string> cmdArgs, string? shellPath)
    {
        var fullCommand = cmdArgs.Count > 0 ? command + " " + string.Join(" ", cmdArgs) : command;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo.FileName = string.IsNullOrEmpty(shellPath) ? "cmd.exe" : shellPath;
            startInfo.Arguments = "/d /s /c " + fullCommand;
        }
        else
        {
            startInfo.FileName = string.IsNullOrEmpty(shellPath) ? "/bin/sh" : shellPath;
            startInfo.Arguments = "-c \"" + fullCommand.Replace("\"", "\\\"") + "\"";
        }
    }

    private static bool GetBoolOption(SharpTSObject? options, string name, bool defaultValue)
    {
        if (options == null)
            return defaultValue;
        var value = options.GetProperty(name);
        return value is bool b ? b : defaultValue;
    }

    private static void ApplyEnvOptions(SharpTSObject? options, ProcessStartInfo startInfo)
    {
        if (options == null)
            return;

        var env = options.GetProperty("env");
        if (env is SharpTSObject envObj)
        {
            foreach (var kv in envObj.Fields)
            {
                startInfo.Environment[kv.Key] = kv.Value?.ToString();
            }
        }
    }
}
