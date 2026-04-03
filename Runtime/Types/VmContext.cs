using System.Reflection;
using System.Runtime.CompilerServices;
using SharpTS.Execution;
using SharpTS.Runtime.BuiltIns;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Tracks which objects have been "contextified" via vm.createContext().
/// Uses a ConditionalWeakTable so objects can be garbage collected normally.
/// </summary>
public static class VmContext
{
    private static readonly ConditionalWeakTable<object, VmContextMarker> _contexts = new();

    /// <summary>
    /// Tags an object as a vm context. If contextObject is null, creates a new SharpTSObject.
    /// Returns the contextified object.
    /// </summary>
    public static object Create(object? contextObject)
    {
        var obj = contextObject ?? new SharpTSObject(new Dictionary<string, object?>());
        _contexts.GetOrCreateValue(obj);
        return obj;
    }

    /// <summary>
    /// Returns whether the object has been contextified via createContext().
    /// </summary>
    public static bool IsContext(object? obj)
    {
        if (obj == null) return false;
        return _contexts.TryGetValue(obj, out _);
    }

    /// <summary>
    /// Extracts properties from a context object as a dictionary for seeding a RuntimeEnvironment.
    /// </summary>
    public static Dictionary<string, object?> ExtractProperties(object? contextObject)
    {
        var props = new Dictionary<string, object?>();
        if (contextObject is SharpTSObject tsObj)
        {
            foreach (var kv in tsObj.Fields)
                props[kv.Key] = kv.Value;
        }
        else if (contextObject is Dictionary<string, object?> dict)
        {
            foreach (var kv in dict)
                props[kv.Key] = kv.Value;
        }
        else if (contextObject != null)
        {
            // Fallback: handle emitted $Object and other types with a Fields property
            var fieldsProp = contextObject.GetType().GetProperty("Fields");
            if (fieldsProp?.GetValue(contextObject) is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                foreach (var kv in fields)
                    props[kv.Key] = kv.Value;
            }
        }
        // Wrap any compiled callables (e.g. $TSFunction) so the sub-interpreter can call them
        WrapCompiledCallables(props);

        return props;
    }

    /// <summary>
    /// Wraps values that look like compiled callables ($TSFunction) in ISharpTSCallable adapters
    /// so the sub-interpreter can invoke them. Compiled functions have an Invoke(object, object[])
    /// method but don't implement ISharpTSCallable.
    /// </summary>
    private static void WrapCompiledCallables(Dictionary<string, object?> props)
    {
        foreach (var key in props.Keys.ToList())
        {
            var value = props[key];
            if (value == null || value is ISharpTSCallable || value is BuiltInMethod)
                continue;

            // Check for Invoke(object[]) method — signature of emitted $TSFunction
            var invokeMethod = value.GetType().GetMethod("Invoke", [typeof(object[])]);
            if (invokeMethod != null)
            {
                props[key] = new CompiledCallableAdapter(value, invokeMethod);
            }
        }
    }

    /// <summary>
    /// Writes mutations back from a RuntimeEnvironment to the context object.
    /// </summary>
    public static void WriteBack(object? contextObject, Dictionary<string, object?> originalProperties, RuntimeEnvironment env)
    {
        if (contextObject is SharpTSObject tsObj)
        {
            foreach (var name in originalProperties.Keys)
            {
                if (env.TryGet(name, out var value))
                    tsObj.SetProperty(name, value);
            }
        }
        else if (contextObject is Dictionary<string, object?> dict)
        {
            foreach (var name in originalProperties.Keys)
            {
                if (env.TryGet(name, out var value))
                    dict[name] = value;
            }
        }
        else if (contextObject != null)
        {
            // Fallback: handle emitted $Object via reflection on SetProperty method
            var setMethod = contextObject.GetType().GetMethod("SetProperty",
                [typeof(string), typeof(object)]);
            if (setMethod != null)
            {
                foreach (var name in originalProperties.Keys)
                {
                    if (env.TryGet(name, out var value))
                        setMethod.Invoke(contextObject, [name, value]);
                }
            }
        }
    }

    private sealed class VmContextMarker { }
}

/// <summary>
/// Wraps a compiled callable ($TSFunction or similar) as ISharpTSCallable so the
/// interpreter can invoke it. Used at the vm module boundary when compiled functions
/// are passed in context objects.
/// </summary>
internal sealed class CompiledCallableAdapter(object target, MethodInfo invokeMethod) : ISharpTSCallable
{
    public int Arity() => 0; // Unknown arity for compiled functions

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        var args = arguments.ToArray();
        return invokeMethod.Invoke(target, [args]);
    }

    public override string ToString() => target.ToString() ?? "<compiled function>";
}
