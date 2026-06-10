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

        // Run the process asynchronously
        Task.Run(() =>
        {
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

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();

                if (timeout > 0)
                {
                    if (!process.WaitForExit((int)timeout))
                    {
                        process.Kill();
                        childProcess.SetExitCode(-1);
                        childProcess.EmitDirect("error", new SharpTSObject(new Dictionary<string, object?>
                        {
                            ["message"] = "Command timed out"
                        }));
                        callback?.Call(null!, [new SharpTSObject(new Dictionary<string, object?>
                        {
                            ["message"] = "Command timed out"
                        }), stdout, stderr]);
                        return;
                    }
                }
                else
                {
                    process.WaitForExit();
                }

                childProcess.SetExitCode(process.ExitCode);

                if (process.ExitCode != 0)
                {
                    var errorObj = new SharpTSObject(new Dictionary<string, object?>
                    {
                        ["message"] = $"Command failed with exit code {process.ExitCode}",
                        ["code"] = (double)process.ExitCode
                    });
                    callback?.Call(null!, [errorObj, stdout, stderr]);
                }
                else
                {
                    callback?.Call(null!, [null, stdout, stderr]);
                }

                childProcess.EmitDirect("close", (double)process.ExitCode);
                childProcess.EmitDirect("exit", (double)process.ExitCode);
            }
            catch (Exception ex)
            {
                var errorObj = new SharpTSObject(new Dictionary<string, object?>
                {
                    ["message"] = ex.Message
                });
                childProcess.EmitDirect("error", errorObj);
                callback?.Call(null!, [errorObj, "", ""]);
            }
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
        var useShell = GetBoolOption(options, "shell", false);

        // Create the ChildProcess event emitter with streams
        var childProcess = new SharpTSChildProcess();
        var stdoutStream = new SharpTSReadable();
        var stderrStream = new SharpTSReadable();
        var stdinStream = new SharpTSWritable();
        childProcess.SetStdoutStream(stdoutStream);
        childProcess.SetStderrStream(stderrStream);
        childProcess.SetStdinStream(stdinStream);

        // Run asynchronously
        Task.Run(() =>
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                };

                foreach (var arg in cmdArgs)
                {
                    startInfo.ArgumentList.Add(arg);
                }

                if (!string.IsNullOrEmpty(cwd))
                    startInfo.WorkingDirectory = cwd;

                ApplyEnvOptions(options, startInfo);

                var process = new Process { StartInfo = startInfo };
                process.Start();
                childProcess.SetPid(process.Id);
                childProcess.SetProcess(process);

                // Wire up stdin: when write() is called on the writable stream,
                // forward data to the process stdin
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
                    // Call the callback if provided (3rd arg in Node.js write(chunk, enc, cb))
                    if (wargs.Length > 2 && wargs[2].ToObject() is ISharpTSCallable cb)
                        cb.Call(null!, [null]);
                    else if (wargs.Length > 1 && wargs[1].ToObject() is ISharpTSCallable cb2)
                        cb2.Call(null!, [null]);
                    return RuntimeValue.True;
                }));

                // Read stdout and stderr asynchronously and push to streams
                var stdoutTask = Task.Run(() =>
                {
                    var buffer = new char[4096];
                    int read;
                    while ((read = process.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        stdoutStream.EmitDirect("data", new string(buffer, 0, read));
                    }
                    stdoutStream.EmitDirect("end");
                });

                var stderrTask = Task.Run(() =>
                {
                    var buffer = new char[4096];
                    int read;
                    while ((read = process.StandardError.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        stderrStream.EmitDirect("data", new string(buffer, 0, read));
                    }
                    stderrStream.EmitDirect("end");
                });

                process.WaitForExit();
                stdoutTask.Wait();
                stderrTask.Wait();

                childProcess.SetExitCode(process.ExitCode);
                childProcess.EmitDirect("close", (double)process.ExitCode);
                childProcess.EmitDirect("exit", (double)process.ExitCode);
            }
            catch (Exception ex)
            {
                childProcess.EmitDirect("error", new SharpTSObject(new Dictionary<string, object?>
                {
                    ["message"] = ex.Message
                }));
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

        Task.Run(() =>
        {
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

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();

                if (timeout > 0)
                {
                    if (!process.WaitForExit((int)timeout))
                    {
                        process.Kill();
                        childProcess.SetExitCode(-1);
                        var timeoutErr = new SharpTSObject(new Dictionary<string, object?>
                            { ["message"] = "Command timed out" });
                        childProcess.EmitDirect("error", timeoutErr);
                        callback?.Call(null!, [timeoutErr, stdout, stderr]);
                        return;
                    }
                }
                else
                {
                    process.WaitForExit();
                }

                childProcess.SetExitCode(process.ExitCode);

                if (process.ExitCode != 0)
                {
                    var errorObj = new SharpTSObject(new Dictionary<string, object?>
                    {
                        ["message"] = $"Command failed with exit code {process.ExitCode}",
                        ["code"] = (double)process.ExitCode
                    });
                    callback?.Call(null!, [errorObj, stdout, stderr]);
                }
                else
                {
                    callback?.Call(null!, [null, stdout, stderr]);
                }

                childProcess.EmitDirect("close", (double)process.ExitCode);
                childProcess.EmitDirect("exit", (double)process.ExitCode);
            }
            catch (Exception ex)
            {
                var errorObj = new SharpTSObject(new Dictionary<string, object?>
                    { ["message"] = ex.Message });
                childProcess.EmitDirect("error", errorObj);
                callback?.Call(null!, [errorObj, "", ""]);
            }
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

        // Resolve the module path relative to cwd
        var resolvedModule = modulePath;
        if (!Path.IsPathRooted(resolvedModule))
        {
            var basePath = cwd ?? Directory.GetCurrentDirectory();
            resolvedModule = Path.GetFullPath(Path.Combine(basePath, resolvedModule));
        }

        // Create a unique IPC pipe name
        var pipeName = $"sharpts-ipc-{Guid.NewGuid():N}";

        // Create the ChildProcess
        var childProcess = new SharpTSChildProcess();
        var cts = new CancellationTokenSource();

        // Create named pipe server for IPC
        var pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // Build the command to run SharpTS with the child module
        var entryAssembly = Assembly.GetEntryAssembly();
        var sharpTsPath = entryAssembly?.Location ?? "";

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Determine how to spawn: dotnet <dll> or direct executable
        var processPath = Environment.ProcessPath;
        if (processPath != null && Path.GetFileNameWithoutExtension(processPath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Running via `dotnet run` or `dotnet <dll>`
            startInfo.FileName = processPath;
            if (!string.IsNullOrEmpty(sharpTsPath) && sharpTsPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add("exec");
                startInfo.ArgumentList.Add(sharpTsPath);
            }
        }
        else if (!string.IsNullOrEmpty(processPath))
        {
            // Running as standalone executable
            startInfo.FileName = processPath;
        }
        else
        {
            throw new Exception("Cannot determine SharpTS executable path for fork()");
        }

        startInfo.ArgumentList.Add(resolvedModule);

        // Pass fork args
        foreach (var arg in forkArgs)
            startInfo.ArgumentList.Add(arg);

        // Set IPC pipe name via environment variable
        startInfo.Environment["SHARPTS_IPC_PIPE"] = pipeName;

        if (!string.IsNullOrEmpty(cwd))
            startInfo.WorkingDirectory = cwd;

        ApplyEnvOptions(options, startInfo);
        // Ensure IPC pipe name survives env override
        startInfo.Environment["SHARPTS_IPC_PIPE"] = pipeName;

        // Set up stdout/stderr streams on the child process
        var stdoutStream = new SharpTSReadable();
        var stderrStream = new SharpTSReadable();
        childProcess.SetStdoutStream(stdoutStream);
        childProcess.SetStderrStream(stderrStream);

        Task.Run(() =>
        {
            try
            {
                var process = new Process { StartInfo = startInfo };
                process.Start();
                childProcess.SetPid(process.Id);
                childProcess.SetProcess(process);

                // Wait for child to connect to IPC pipe
                var connectTask = pipeServer.WaitForConnectionAsync(cts.Token);
                if (!connectTask.Wait(10_000))
                {
                    pipeServer.Dispose();
                    throw new Exception("Child process failed to connect to IPC pipe");
                }

                var writer = new StreamWriter(pipeServer) { AutoFlush = true };
                childProcess.SetupIpc(pipeServer, writer, cts);

                // Start reading IPC messages from child
                var ipcReader = Task.Run(() =>
                {
                    try
                    {
                        using var reader = new StreamReader(pipeServer, leaveOpen: true);
                        while (!cts.Token.IsCancellationRequested)
                        {
                            var line = reader.ReadLine();
                            if (line == null) break; // pipe closed
                            var message = IpcSerializer.Deserialize(line);
                            childProcess.EmitDirect("message", message);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                }, cts.Token);

                // Read stdout/stderr
                var stdoutTask = Task.Run(() =>
                {
                    var buffer = new char[4096];
                    int read;
                    while ((read = process.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
                        stdoutStream.EmitDirect("data", new string(buffer, 0, read));
                    stdoutStream.EmitDirect("end");
                });

                var stderrTask = Task.Run(() =>
                {
                    var buffer = new char[4096];
                    int read;
                    while ((read = process.StandardError.Read(buffer, 0, buffer.Length)) > 0)
                        stderrStream.EmitDirect("data", new string(buffer, 0, read));
                    stderrStream.EmitDirect("end");
                });

                process.WaitForExit();
                cts.Cancel();
                stdoutTask.Wait();
                stderrTask.Wait();

                childProcess.SetExitCode(process.ExitCode);
                childProcess.EmitDirect("close", (double)process.ExitCode);
                childProcess.EmitDirect("exit", (double)process.ExitCode);
            }
            catch (Exception ex)
            {
                childProcess.EmitDirect("error", new SharpTSObject(new Dictionary<string, object?>
                {
                    ["message"] = ex.Message
                }));
            }
        });

        return RuntimeValue.FromObject(childProcess);
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
