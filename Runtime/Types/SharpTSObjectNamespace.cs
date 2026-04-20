using SharpTS.Execution;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Singleton representing the Object namespace.
/// Provides static methods like Object.keys, Object.values, etc.
/// Implements ISharpTSCallable so `Object(value)` coerces per ECMA-262 §19.1.1 —
/// lodash uses this idiom heavily (`Object(object)` to guarantee object-ness before
/// key iteration).
/// </summary>
public class SharpTSObjectNamespace : ISharpTSCallable
{
    public static readonly SharpTSObjectNamespace Instance = new();
    private SharpTSObjectNamespace() { }

    public int Arity() => 0;

    /// <summary>
    /// ECMA-262 §19.1.1 Object(value): if value is null/undefined, return a new empty object;
    /// otherwise return ToObject(value). For already-object values this is a pass-through.
    /// </summary>
    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        if (arguments.Count == 0) return new SharpTSObject(new Dictionary<string, object?>());
        var value = arguments[0];
        if (value == null || value is SharpTSUndefined)
            return new SharpTSObject(new Dictionary<string, object?>());
        // Primitives (string/number/bool) — wrap in a plain object holding the primitive.
        // Good enough for lodash's use case where the wrapper is iterated over, not read.
        // A fuller implementation would materialize String/Number/Boolean wrapper objects.
        if (value is string or double or int or long or bool)
            return new SharpTSObject(new Dictionary<string, object?> { ["valueOf"] = value });
        return value;
    }

    public override string ToString() => "function Object() { [native code] }";
}
