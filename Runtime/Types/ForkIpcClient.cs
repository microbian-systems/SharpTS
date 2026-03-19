using System.IO.Pipes;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Client-side IPC handler for child processes spawned via fork().
/// Connects to the parent's named pipe and provides send/receive messaging.
/// </summary>
public sealed class ForkIpcClient : IDisposable
{
    private static ForkIpcClient? _instance;
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly CancellationTokenSource _cts = new();
    private bool _connected;
    private bool _disposed;

    /// <summary>
    /// Gets the singleton instance if this process was forked with IPC.
    /// Returns null if this is not a forked child process.
    /// </summary>
    public static ForkIpcClient? Instance => _instance;

    /// <summary>
    /// Whether this process was started as a forked child with IPC.
    /// </summary>
    public static bool IsForkedChild => _instance != null;

    /// <summary>
    /// Whether the IPC channel is connected.
    /// </summary>
    public bool Connected => _connected && !_disposed;

    private ForkIpcClient(string pipeName)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        _pipe.Connect(10_000); // 10 second timeout
        _connected = true;

        _writer = new StreamWriter(_pipe) { AutoFlush = true };
        _reader = new StreamReader(_pipe);
    }

    /// <summary>
    /// Initializes the fork IPC client if SHARPTS_IPC_PIPE env var is set.
    /// Called during process startup.
    /// </summary>
    public static void TryInitialize()
    {
        var pipeName = Environment.GetEnvironmentVariable("SHARPTS_IPC_PIPE");
        if (string.IsNullOrEmpty(pipeName)) return;

        try
        {
            _instance = new ForkIpcClient(pipeName);
            _instance.StartReading();
        }
        catch
        {
            // Failed to connect - not a valid fork() child
            _instance = null;
        }
    }

    /// <summary>
    /// Sends a message to the parent process.
    /// </summary>
    public bool Send(object? message)
    {
        if (!_connected || _disposed) return false;

        try
        {
            var json = IpcSerializer.Serialize(message);
            _writer.WriteLine(json);
            return true;
        }
        catch
        {
            _connected = false;
            return false;
        }
    }

    /// <summary>
    /// Disconnects from the parent's IPC channel.
    /// </summary>
    public void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
        _cts.Cancel();

        try { _writer.Dispose(); } catch { }
        try { _pipe.Dispose(); } catch { }

        // Emit 'disconnect' on the process EventEmitter
        SharpTSProcess.Instance.EmitDirect("disconnect");
    }

    /// <summary>
    /// Starts a background task to read incoming IPC messages from the parent.
    /// Messages are emitted as 'message' events on the process EventEmitter.
    /// </summary>
    private void StartReading()
    {
        Task.Run(() =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested && _connected)
                {
                    var line = _reader.ReadLine();
                    if (line == null)
                    {
                        _connected = false;
                        SharpTSProcess.Instance.EmitDirect("disconnect");
                        break;
                    }

                    var message = IpcSerializer.Deserialize(line);
                    SharpTSProcess.Instance.EmitDirect("message", message);
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                _connected = false;
            }
        }, _cts.Token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;
        _cts.Cancel();
        try { _writer.Dispose(); } catch { }
        try { _reader.Dispose(); } catch { }
        try { _pipe.Dispose(); } catch { }
        _cts.Dispose();
    }
}
