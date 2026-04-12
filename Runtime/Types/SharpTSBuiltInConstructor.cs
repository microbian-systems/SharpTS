using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// A lightweight callable wrapper around built-in constructor factory functions.
/// Registered as a global variable so that <c>typeof Map</c>, <c>val instanceof Map</c>,
/// passing <c>Map</c> as a value, and <c>Map.groupBy()</c> all work correctly.
/// </summary>
public sealed class SharpTSBuiltInConstructor : ISharpTSCallable
{
    public string Name { get; }
    private readonly BuiltInConstructorFactory.ConstructorHandler _factory;

    public SharpTSBuiltInConstructor(string name, BuiltInConstructorFactory.ConstructorHandler factory)
    {
        Name = name;
        _factory = factory;
    }

    public int Arity() => 0;

    public object? Call(Interp interpreter, List<object?> arguments) => _factory(arguments);

    /// <summary>
    /// Resolves static methods like <c>Map.groupBy()</c> via the built-in registry.
    /// Called by the interpreter's GetFieldsProperty dispatch chain via reflection.
    /// </summary>
    public object? GetMember(string name)
        => BuiltInRegistry.Instance.GetStaticMethod(Name, name);

    public override string ToString() => $"function {Name}() {{ [native code] }}";
}
