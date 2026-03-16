using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of a Node.js ChildProcess object.
/// Extends EventEmitter and provides pid, exitCode, stdout, stderr, and stdin properties.
/// </summary>
public class SharpTSChildProcess : SharpTSEventEmitter
{
    private double _pid;
    private double? _exitCode;
    private SharpTSReadable? _stdout;
    private SharpTSReadable? _stderr;
    private bool _killed;

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

            // Methods
            "kill" => new BuiltInMethod("kill", 0, 1, Kill),

            // Inherit from EventEmitter
            _ => base.GetMember(name)
        };
    }

    private object? Kill(Interp interpreter, object? receiver, List<object?> args)
    {
        _killed = true;
        // In a real implementation, we'd send a signal to the process.
        // For now, just mark as killed.
        return true;
    }

    public override string ToString() => "ChildProcess {}";
}
