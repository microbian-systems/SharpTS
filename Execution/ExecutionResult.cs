using SharpTS.Runtime;

namespace SharpTS.Execution;

/// <summary>
/// Represents the result of executing a statement, including flow control information.
/// </summary>
/// <remarks>
/// Replaces exception-based flow control (ReturnException, BreakException, etc.)
/// with a lightweight struct to avoid the high cost of stack unwinding.
/// </remarks>
public readonly struct ExecutionResult
{
    public enum ResultType
    {
        None,       // Normal completion, proceed to next statement
        Return,     // return keyword encountered
        Break,      // break keyword encountered
        Continue,   // continue keyword encountered
        Throw       // throw keyword encountered or runtime error
    }

    public readonly ResultType Type;
    public readonly RuntimeValue Value;
    public readonly string? TargetLabel;

    private ExecutionResult(ResultType type, RuntimeValue value = default, string? targetLabel = null)
    {
        Type = type;
        Value = value;
        TargetLabel = targetLabel;
    }

    public static ExecutionResult Success() => new(ResultType.None);
    public static ExecutionResult Return(RuntimeValue value) => new(ResultType.Return, value);
    public static ExecutionResult Return(object? value) => new(ResultType.Return, RuntimeValue.FromBoxed(value));
    public static ExecutionResult Break(string? label = null) => new(ResultType.Break, default, label);
    public static ExecutionResult Continue(string? label = null) => new(ResultType.Continue, default, label);
    public static ExecutionResult Throw(RuntimeValue value) => new(ResultType.Throw, value);
    public static ExecutionResult Throw(object? value) => new(ResultType.Throw, RuntimeValue.FromBoxed(value));

    public bool IsNormal => Type == ResultType.None;
    public bool IsAbrupt => Type != ResultType.None;
}
