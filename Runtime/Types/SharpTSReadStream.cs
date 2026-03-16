using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of fs.createReadStream() — a Readable that reads a file in chunks.
/// </summary>
public class SharpTSReadStream : SharpTSReadable
{
    private readonly string _filePath;
    private FileStream? _fileStream;
    private readonly bool _autoClose;
    private readonly long _start;
    private readonly long _end;
    private readonly int _chunkSize;
    private readonly string? _encodingOption;
    private long _bytesRead;
    private bool _started;

    public SharpTSReadStream(string filePath, Dictionary<string, object?>? options = null)
    {
        _filePath = filePath;
        _autoClose = true;
        _start = 0;
        _end = long.MaxValue;
        _chunkSize = 65536;
        _encodingOption = null;

        if (options != null)
        {
            if (options.TryGetValue("encoding", out var enc) && enc is string encStr)
                _encodingOption = encStr;
            if (options.TryGetValue("start", out var s) && s is double startD)
                _start = (long)startD;
            if (options.TryGetValue("end", out var e) && e is double endD)
                _end = (long)endD;
            if (options.TryGetValue("highWaterMark", out var hwm) && hwm is double hwmD)
                _chunkSize = (int)hwmD;
            if (options.TryGetValue("autoClose", out var ac) && ac is bool acBool)
                _autoClose = acBool;
        }
    }

    /// <summary>
    /// Start reading the file and pushing chunks into the stream.
    /// </summary>
    public void StartReading(Interp interpreter)
    {
        if (_started) return;
        _started = true;

        try
        {
            _fileStream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            EmitEvent(interpreter, "open", [(double)_fileStream.SafeFileHandle.DangerousGetHandle().ToInt64()]);

            if (_start > 0)
                _fileStream.Seek(_start, SeekOrigin.Begin);

            var buffer = new byte[_chunkSize];
            long remaining = _end == long.MaxValue ? long.MaxValue : _end - _start + 1;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int bytesRead = _fileStream.Read(buffer, 0, toRead);
                if (bytesRead == 0) break;

                _bytesRead += bytesRead;
                remaining -= bytesRead;

                var data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);

                object chunk;
                if (_encodingOption != null)
                {
                    chunk = System.Text.Encoding.UTF8.GetString(data);
                }
                else
                {
                    chunk = new SharpTSBuffer(data);
                }

                // Use base Push via the BuiltInMethod mechanism
                var pushMethod = GetMember("push") as BuiltInMethod;
                pushMethod?.Bind(this).Call(interpreter, [chunk]);
            }

            // Signal EOF
            var pushEof = GetMember("push") as BuiltInMethod;
            pushEof?.Bind(this).Call(interpreter, new List<object?> { null });

            CloseFileStream(interpreter);
        }
        catch (Exception ex)
        {
            EmitEvent(interpreter, "error", [ex.Message]);
            CloseFileStream(interpreter);
        }
    }

    private void CloseFileStream(Interp interpreter)
    {
        if (_fileStream != null && _autoClose)
        {
            _fileStream.Dispose();
            _fileStream = null;
            EmitEvent(interpreter, "close", []);
        }
    }

    public new object? GetMember(string name)
    {
        return name switch
        {
            "path" => _filePath,
            "bytesRead" => (double)_bytesRead,
            _ => base.GetMember(name)
        };
    }

    public override string ToString() => $"ReadStream {{ path: '{_filePath}' }}";
}
