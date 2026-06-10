using SharpTS.Execution;
using SharpTS.Runtime.BuiltIns;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of an FSWatcher returned by fs.watch().
/// Wraps .NET's FileSystemWatcher and emits 'change', 'rename', 'error', 'close' events.
/// </summary>
public class SharpTSFSWatcher : SharpTSEventEmitter, IDisposable
{
    private FileSystemWatcher? _watcher;
    private Interpreter? _interpreter;
    private bool _closed;
    private readonly string _filename;

    public SharpTSFSWatcher(string filename, Dictionary<string, object?>? options, Interpreter interpreter)
    {
        _filename = filename;
        _interpreter = interpreter;

        var recursive = false;
        if (options != null)
        {
            if (options.TryGetValue("recursive", out var rec) && rec is bool recBool)
                recursive = recBool;
        }

        try
        {
            var fullPath = Path.GetFullPath(filename);
            string dir;
            string filter;

            if (Directory.Exists(fullPath))
            {
                dir = fullPath;
                filter = "*";
            }
            else if (File.Exists(fullPath))
            {
                dir = Path.GetDirectoryName(fullPath)!;
                filter = Path.GetFileName(fullPath);
            }
            else
            {
                throw new FileNotFoundException($"ENOENT: no such file or directory, watch '{filename}'");
            }

            _watcher = new FileSystemWatcher(dir, filter)
            {
                IncludeSubdirectories = recursive,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName
                              | NotifyFilters.DirectoryName
                              | NotifyFilters.LastWrite
                              | NotifyFilters.Size
                              | NotifyFilters.CreationTime
            };

            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;

            // Keep process alive while watching
            interpreter.Ref();
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new Exception($"Error watching '{filename}': {ex.Message}");
        }
    }

    private void OnChanged(object? sender, FileSystemEventArgs e)
    {
        var interpreter = _interpreter;
        if (interpreter == null || _closed) return;

        var relativeName = GetRelativeName(e.FullPath);
        interpreter.ScheduleTimer(0, 0, () =>
        {
            EmitWatchEvent("change", relativeName);
        }, isInterval: false);
    }

    private void OnRenamed(object? sender, RenamedEventArgs e)
    {
        var interpreter = _interpreter;
        if (interpreter == null || _closed) return;

        var relativeName = GetRelativeName(e.FullPath);
        interpreter.ScheduleTimer(0, 0, () =>
        {
            EmitWatchEvent("rename", relativeName);
        }, isInterval: false);
    }

    private void OnError(object? sender, ErrorEventArgs e)
    {
        var interpreter = _interpreter;
        if (interpreter == null || _closed) return;

        var error = e.GetException();
        interpreter.ScheduleTimer(0, 0, () =>
        {
            EmitWatchEvent("error", error.Message);
        }, isInterval: false);
    }

    private string? GetRelativeName(string fullPath)
    {
        var watchedPath = Path.GetFullPath(_filename);
        if (Directory.Exists(watchedPath))
        {
            return Path.GetRelativePath(watchedPath, fullPath).Replace('\\', '/');
        }
        return Path.GetFileName(fullPath);
    }

    private void EmitWatchEvent(string eventType, object? filename)
    {
        if (_interpreter == null || _closed) return;

        var emitMethod = base.GetMember("emit") as BuiltInMethod;
        if (emitMethod != null)
        {
            emitMethod.CallBoxed(_interpreter, [eventType, eventType, filename]);
        }
    }

    /// <summary>
    /// Gets a member (method or property) by name for interpreter dispatch.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            "close" => new BuiltInMethod("close", 0, 0, Close),
            "ref" => new BuiltInMethod("ref", 0, 0, Ref),
            "unref" => new BuiltInMethod("unref", 0, 0, Unref),
            _ => base.GetMember(name)
        };
    }

    private object? Close(Interpreter interpreter, object? receiver, List<object?> args)
    {
        CloseInternal();
        return null;
    }

    private object? Ref(Interpreter interpreter, object? receiver, List<object?> args)
    {
        _interpreter?.Ref();
        return this;
    }

    private object? Unref(Interpreter interpreter, object? receiver, List<object?> args)
    {
        _interpreter?.Unref();
        return this;
    }

    private void CloseInternal()
    {
        if (_closed) return;
        _closed = true;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnChanged;
            _watcher.Created -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }

        _interpreter?.Unref();

        // Emit close event
        if (_interpreter != null)
        {
            var emitMethod = base.GetMember("emit") as BuiltInMethod;
            emitMethod?.CallBoxed(_interpreter, ["close"]);
        }

        _interpreter = null;
    }

    public void Dispose()
    {
        CloseInternal();
        GC.SuppressFinalize(this);
    }

    public override string ToString() => $"FSWatcher {{ path: '{_filename}' }}";
}
