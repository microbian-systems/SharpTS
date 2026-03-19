using System.Runtime.CompilerServices;

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
        return props;
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
    }

    private sealed class VmContextMarker { }
}
