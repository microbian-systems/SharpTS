using System.Diagnostics;
using System.IO.Pipes;
using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of a Node.js ChildProcess object.
/// Extends EventEmitter and provides pid, exitCode, stdout, stderr, stdin,
/// and IPC methods (send/disconnect) for fork().
/// </summary>
public class SharpTSChildProcess : SharpTSEventEmitter
{
    private double _pid;
    private double? _exitCode;
    private SharpTSReadable? _stdout;
    private SharpTSReadable? _stderr;
    private SharpTSWritable? _stdin;
    private bool _killed;
    private Process? _process;
    private bool _connected;
    private NamedPipeServerStream? _ipcPipeServer;
    private StreamWriter? _ipcWriter;
    private CancellationTokenSource? _ipcCts;
    private string? _signalCode;

    /// <summary>
    /// Sets the underlying OS process for kill() support.
    /// </summary>
    public void SetProcess(Process process) => _process = process;

    /// <summary>
    /// Sets the PID of the spawned process.
    /// </summary>
    public void SetPid(int pid) => _pid = pid;

    /// <summary>
    /// Sets the exit code when the process completes.
    /// </summary>
    public void SetExitCode(int exitCode) => _exitCode = exitCode;

    /// <summary>
    /// Sets the stdout stream for spawn().
    /// </summary>
    public void SetStdoutStream(SharpTSReadable stream) => _stdout = stream;

    /// <summary>
    /// Sets the stderr stream for spawn().
    /// </summary>
    public void SetStderrStream(SharpTSReadable stream) => _stderr = stream;

    /// <summary>
    /// Sets the stdin writable stream for spawn().
    /// </summary>
    public void SetStdinStream(SharpTSWritable stream) => _stdin = stream;

    /// <summary>
    /// Sets up IPC for fork(). Stores the pipe server and writer for send().
    /// </summary>
    public void SetupIpc(NamedPipeServerStream pipeServer, StreamWriter writer, CancellationTokenSource cts)
    {
        _ipcPipeServer = pipeServer;
        _ipcWriter = writer;
        _ipcCts = cts;
        _connected = true;
    }

    /// <summary>
    /// Gets a member (method or property) by name for interpreter dispatch.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            // Properties
            "pid" => _pid,
            "exitCode" => _exitCode ?? (object)null!,
            "killed" => _killed,
            "stdout" => _stdout,
            "stderr" => _stderr,
            "stdin" => _stdin,
            "connected" => _connected,
            "signalCode" => _signalCode ?? (object)null!,

            // Methods
            "kill" => new BuiltInMethod("kill", 0, 1, Kill),
            "send" => new BuiltInMethod("send", 1, 4, Send),
            "disconnect" => new BuiltInMethod("disconnect", 0, Disconnect),
            "ref" => new BuiltInMethod("ref", 0, Ref),
            "unref" => new BuiltInMethod("unref", 0, Unref),

            // Inherit from EventEmitter
            _ => base.GetMember(name)
        };
    }

    private object? Kill(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_killed) return false;

        _killed = true;
        var signal = args.Count > 0 && args[0] is string s ? s : "SIGTERM";
        _signalCode = signal;

        try
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                return true;
            }
        }
        catch
        {
            // Process may have already exited
        }
        return true;
    }

    private object? Send(Interp interpreter, object? receiver, List<object?> args)
    {
        if (!_connected || _ipcWriter == null)
            throw new Exception("channel closed");

        var message = args.Count > 0 ? args[0] : null;
        try
        {
            var json = IpcSerializer.Serialize(message);
            _ipcWriter.WriteLine(json);
            _ipcWriter.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private object? Disconnect(Interp interpreter, object? receiver, List<object?> args)
    {
        if (!_connected) return null;
        _connected = false;

        try
        {
            _ipcCts?.Cancel();
            _ipcWriter?.Dispose();
            _ipcPipeServer?.Dispose();
        }
        catch { }

        EmitDirect("disconnect");
        return null;
    }

    private object? Ref(Interp interpreter, object? receiver, List<object?> args) => this;
    private object? Unref(Interp interpreter, object? receiver, List<object?> args) => this;

    public override string ToString() => "ChildProcess {}";
}
