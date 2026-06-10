using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.Exceptions;

/// <summary>
/// Control flow exception thrown when a TypeScript throw statement is executed.
/// </summary>
/// <remarks>
/// Wraps user-thrown values from TypeScript code (e.g., <c>throw new Error("msg")</c>
/// or <c>throw "error"</c>). Thrown by <see cref="Interpreter"/> when executing a throw
/// statement, and caught by try/catch blocks to handle errors. The <see cref="Value"/>
/// property holds the thrown object, which can be any TypeScript value.
///
/// <see cref="Exception.Message"/> is derived from <see cref="Value"/> so that
/// C# callers (notably test harnesses using <c>Assert.Throws&lt;Exception&gt;</c>)
/// can inspect the thrown error's textual form without unwrapping Value.
/// </remarks>
public class ThrowException : Exception
{
    public object? Value { get; }

    public ThrowException(object? value) : base(ExtractMessage(value))
    {
        Value = value;
    }

    /// <summary>
    /// Builds the appropriate host exception for an <see cref="Execution.ExecutionResult"/>
    /// Throw value at a function boundary. Object values (<see cref="SharpTSError"/>,
    /// <see cref="SharpTSObject"/>, <see cref="SharpTSInstance"/>, ...) become a
    /// <see cref="ThrowException"/> so guest <c>try/catch</c> and constructor-identity
    /// checks see the original object. String values — which originate from
    /// <c>TranslateException</c> of a plain host <see cref="Exception"/>
    /// (strict-mode violations, most internal runtime errors) — stay as a plain
    /// <see cref="Exception"/> so pre-existing C# callers that rely on
    /// <c>catch(Exception)</c> with a stringified message (unit tests, CLI
    /// output) keep observing the old shape.
    /// </summary>
    public static Exception FromResult(object? value) => value is string s
        ? new Exception(s)
        : new ThrowException(value);

    /// <summary>
    /// Produces a textual form of <paramref name="value"/> suitable for
    /// <see cref="Exception.Message"/> — preferring spec-shaped "Name: message"
    /// formatting for Error-like objects, falling back to <c>ToString</c>.
    /// Lets C# callers (Test262 classifier, unit tests asserting
    /// <c>ex.Message</c>) distinguish error kinds without unwrapping Value.
    /// </summary>
    private static string ExtractMessage(object? value) => value switch
    {
        null => "null",
        SharpTSUndefined => "undefined",
        string s => s,
        SharpTSError err => err.ToString(),
        SharpTSInstance inst => ExtractFromInstance(inst),
        SharpTSObject obj => ExtractFromObject(obj),
        _ => value.ToString() ?? "",
    };

    private static string ExtractFromInstance(SharpTSInstance inst)
    {
        var name = inst.GetRawField("name")?.ToString();
        var message = inst.GetRawField("message")?.ToString();
        if (!string.IsNullOrEmpty(name))
            return string.IsNullOrEmpty(message) ? name : $"{name}: {message}";
        return inst.ToString() ?? "";
    }

    private static string ExtractFromObject(SharpTSObject obj)
    {
        string? name = obj.HasProperty("name") ? obj.GetProperty("name")?.ToString() : null;
        if (string.IsNullOrEmpty(name) && obj.HasProperty("constructor"))
        {
            // User-defined error types (e.g. Test262Error) don't set .name on
            // each instance — read it off the constructor function instead so
            // `throw new Test262Error(msg)` surfaces as "Test262Error: msg".
            name = ExtractFunctionName(obj.GetProperty("constructor"));
        }
        string? message = obj.HasProperty("message") ? obj.GetProperty("message")?.ToString() : null;
        if (!string.IsNullOrEmpty(name))
            return string.IsNullOrEmpty(message) ? name : $"{name}: {message}";
        if (!string.IsNullOrEmpty(message))
            return message;
        return "[object Object]";
    }

    private static string? ExtractFunctionName(object? fn) => fn switch
    {
        SharpTSClass cls => cls.Name,
        SharpTSFunction f => StripFnWrapper(f.ToString()),
        _ => null,
    };

    private static string? StripFnWrapper(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        const string prefix = "<fn ";
        if (s.StartsWith(prefix) && s.EndsWith(">"))
            return s.Substring(prefix.Length, s.Length - prefix.Length - 1);
        return s;
    }
}
