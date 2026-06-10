using SharpTS.Execution;
using SharpTS.Runtime.BuiltIns;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Represents a Node.js-compatible readline Interface object.
/// Extends SharpTSEventEmitter for event-driven patterns (line, close, pause, resume events).
/// </summary>
public class SharpTSReadlineInterface : SharpTSEventEmitter
{
    private bool _closed;
    private bool _paused;
    private string _prompt = "> ";
    private TextReader? _input;
    private TextWriter? _output;

    /// <summary>
    /// Creates a new readline interface with optional input/output streams.
    /// </summary>
    public SharpTSReadlineInterface(SharpTSObject? options = null)
    {
        _closed = false;
        _paused = false;
        _input = Console.In;
        _output = Console.Out;

        if (options != null)
        {
            if (options.GetProperty("prompt") is string p)
                _prompt = p;
        }
    }

    /// <summary>
    /// Gets a member of this interface object.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            "question" => new BuiltInMethod("question", 2, Question),
            "close" => new BuiltInMethod("close", 0, CloseInterface),
            "prompt" => new BuiltInMethod("prompt", 0, 1, PromptMethod),
            "pause" => new BuiltInMethod("pause", 0, Pause),
            "resume" => new BuiltInMethod("resume", 0, Resume),
            "write" => new BuiltInMethod("write", 1, WriteMethod),
            "setPrompt" => new BuiltInMethod("setPrompt", 1, SetPrompt),
            "getPrompt" => new BuiltInMethod("getPrompt", 0, GetPrompt),
            // EventEmitter methods
            _ => base.GetMember(name)
        };
    }

    private object? Question(Interpreter interpreter, object? receiver, List<object?> args)
    {
        if (_closed || args.Count < 2)
            return null;

        var query = args[0]?.ToString() ?? "";
        var callback = args[1];

        _output?.Write(query);
        var answer = _input?.ReadLine() ?? "";

        if (callback is ISharpTSCallable callable)
        {
            callable.CallBoxed(interpreter, [answer]);
        }

        return null;
    }

    private object? CloseInterface(Interpreter interpreter, object? receiver, List<object?> args)
    {
        if (_closed) return this;
        _closed = true;

        EmitEvent(interpreter, "close", []);
        return this;
    }

    private object? PromptMethod(Interpreter interpreter, object? receiver, List<object?> args)
    {
        if (!_closed)
        {
            _output?.Write(_prompt);
        }
        return null;
    }

    private object? Pause(Interpreter interpreter, object? receiver, List<object?> args)
    {
        if (!_paused)
        {
            _paused = true;
            EmitEvent(interpreter, "pause", []);
        }
        return this;
    }

    private object? Resume(Interpreter interpreter, object? receiver, List<object?> args)
    {
        if (_paused)
        {
            _paused = false;
            EmitEvent(interpreter, "resume", []);
        }
        return this;
    }

    private object? WriteMethod(Interpreter interpreter, object? receiver, List<object?> args)
    {
        if (!_closed && args.Count > 0)
        {
            _output?.Write(args[0]?.ToString() ?? "");
        }
        return null;
    }

    private object? SetPrompt(Interpreter interpreter, object? receiver, List<object?> args)
    {
        if (args.Count > 0)
        {
            _prompt = args[0]?.ToString() ?? "";
        }
        return null;
    }

    private object? GetPrompt(Interpreter interpreter, object? receiver, List<object?> args)
    {
        return _prompt;
    }

    public override string ToString() => "Interface {}";
}
