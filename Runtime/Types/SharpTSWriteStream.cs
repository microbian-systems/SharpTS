using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of fs.createWriteStream() — a Writable that writes to a file.
/// </summary>
public class SharpTSWriteStream : SharpTSWritable
{
    private readonly string _filePath;
    private FileStream? _fileStream;
    private readonly bool _autoClose;
    private readonly string _flags;
    private long _bytesWritten;

    public SharpTSWriteStream(string filePath, Dictionary<string, object?>? options = null)
    {
        _filePath = filePath;
        _autoClose = true;
        _flags = "w";

        if (options != null)
        {
            if (options.TryGetValue("flags", out var f) && f is string flagsStr)
                _flags = flagsStr;
            if (options.TryGetValue("autoClose", out var ac) && ac is bool acBool)
                _autoClose = acBool;
        }

        // Open the file based on flags
        var mode = _flags switch
        {
            "a" => FileMode.Append,
            "ax" => FileMode.Append,
            "w" => FileMode.Create,
            "wx" => FileMode.CreateNew,
            _ => FileMode.Create
        };

        _fileStream = new FileStream(_filePath, mode, FileAccess.Write, FileShare.None);

        // Set internal write callback to write to file
        SetWriteCallback(new WriteStreamCallback(this));

        // Listen for 'finish' to close the file (works with both direct end() and piped end)
        AddListenerDirect("finish", new FinishListener(this));
    }

    /// <summary>
    /// Emits the 'open' event. Call after construction.
    /// </summary>
    public void EmitOpen(Interp interpreter)
    {
        if (_fileStream != null)
        {
            EmitEvent(interpreter, "open", [(double)_fileStream.SafeFileHandle.DangerousGetHandle().ToInt64()]);
        }
    }

    private class WriteStreamCallback : ISharpTSCallable
    {
        private readonly SharpTSWriteStream _stream;

        public WriteStreamCallback(SharpTSWriteStream stream) => _stream = stream;

        public int Arity() => 3;

        public object? Call(Interp interpreter, List<object?> arguments)
        {
            var chunk = arguments.Count > 0 ? arguments[0] : null;
            var callback = arguments.Count > 2 ? arguments[2] as ISharpTSCallable : null;

            if (chunk != null && _stream._fileStream != null)
            {
                byte[] data;
                if (chunk is SharpTSBuffer buf)
                {
                    data = buf.Data;
                }
                else
                {
                    data = System.Text.Encoding.UTF8.GetBytes(chunk.ToString() ?? "");
                }

                _stream._fileStream.Write(data, 0, data.Length);
                _stream._bytesWritten += data.Length;
            }

            // Call the done callback
            callback?.CallBoxed(interpreter, []);

            return null;
        }

        public RuntimeValue CallV2(Interp interpreter, ReadOnlySpan<RuntimeValue> arguments)
            => RuntimeValue.FromBoxed(Call(interpreter, CallableInterop.ToBoxedList(arguments)));
    }

    /// <summary>
    /// Listener for 'finish' event to close the file stream.
    /// </summary>
    private class FinishListener : ISharpTSCallable
    {
        private readonly SharpTSWriteStream _stream;

        public FinishListener(SharpTSWriteStream stream) => _stream = stream;

        public int Arity() => 0;

        public object? Call(Interp interpreter, List<object?> arguments)
        {
            _stream.CloseFileStream(interpreter);
            return null;
        }

        public RuntimeValue CallV2(Interp interpreter, ReadOnlySpan<RuntimeValue> arguments)
            => RuntimeValue.FromBoxed(Call(interpreter, CallableInterop.ToBoxedList(arguments)));
    }

    private void CloseFileStream(Interp interpreter)
    {
        if (_fileStream != null && _autoClose)
        {
            _fileStream.Flush();
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
            "bytesWritten" => (double)_bytesWritten,
            _ => base.GetMember(name)
        };
    }

    public override string ToString() => $"WriteStream {{ path: '{_filePath}' }}";
}
