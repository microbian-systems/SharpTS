namespace SharpTS.Runtime.Types;

/// <summary>
/// Lightweight stand-in for <c>Class.prototype</c> on a built-in or user-
/// defined <see cref="SharpTSClass"/>. Member access goes through
/// <see cref="SharpTSClass.FindMethod"/> so that
/// <c>Error.prototype.toString</c>, <c>RangeError.prototype.toString</c>,
/// etc. resolve to the class's instance methods. Spec-aligned: in JS,
/// <c>Class.prototype</c> is a regular object (typeof "object") whose
/// properties are the instance methods plus a <c>constructor</c> back-
/// reference.
/// </summary>
/// <remarks>
/// This is read-only. Method writes (<c>Error.prototype.toString = ...</c>)
/// would mutate the class — kept out of scope for the same reasons
/// SharpTSMath.SetExtra is. Test262 tests that exercise this are rare and
/// can be addressed later if needed.
/// </remarks>
public sealed class SharpTSClassPrototype
{
    private readonly SharpTSClass _klass;

    public SharpTSClassPrototype(SharpTSClass klass)
    {
        _klass = klass;
    }

    public SharpTSClass Class => _klass;

    public object? GetMember(string name)
    {
        if (name == "constructor") return _klass;
        var method = _klass.FindMethod(name);
        if (method != null) return method;
        return SharpTSUndefined.Instance;
    }

    public override string ToString() => $"[object {_klass.Name}]";
}
