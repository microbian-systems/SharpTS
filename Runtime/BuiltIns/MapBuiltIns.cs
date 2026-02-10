using SharpTS.Execution;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Built-in methods for the Map type.
/// </summary>
/// <remarks>
/// Implements the JavaScript Map API: get, set, has, delete, clear, keys, values, entries, forEach, size.
/// forEach callback receives (value, key, map) to match JavaScript semantics.
/// </remarks>
public static class MapBuiltIns
{
    private static readonly BuiltInTypeMemberLookup<SharpTSMap> _lookup =
        BuiltInTypeBuilder<SharpTSMap>.ForInstanceType()
            .Property("size", map => (double)map.Size)
            .Method("get", 1, Get)
            .Method("set", 2, Set)
            .Method("has", 1, Has)
            .Method("delete", 1, Delete)
            .Method("clear", 0, Clear)
            .Method("keys", 0, Keys)
            .Method("values", 0, Values)
            .Method("entries", 0, Entries)
            .Method("forEach", 1, ForEach)
            .Build();

    private static readonly BuiltInStaticMemberLookup _staticLookup =
        BuiltInStaticBuilder.Create()
            .Method("groupBy", 2, GroupBy)
            .Build();

    public static object? GetStaticMethod(string name)
        => _staticLookup.GetMember(name);

    public static object? GetMember(SharpTSMap receiver, string name)
        => _lookup.GetMember(receiver, name);

    private static object? Get(Interpreter _, SharpTSMap map, List<object?> args)
    {
        var key = args[0];
        if (key == null)
            throw new Exception("Runtime Error: Map key cannot be null.");
        return map.Get(key);
    }

    private static object? Set(Interpreter _, SharpTSMap map, List<object?> args)
    {
        var key = args[0];
        if (key == null)
            throw new Exception("Runtime Error: Map key cannot be null.");
        return map.Set(key, args[1]);
    }

    private static object? Has(Interpreter _, SharpTSMap map, List<object?> args)
    {
        var key = args[0];
        if (key == null)
            throw new Exception("Runtime Error: Map key cannot be null.");
        return map.Has(key);
    }

    private static object? Delete(Interpreter _, SharpTSMap map, List<object?> args)
    {
        var key = args[0];
        if (key == null)
            throw new Exception("Runtime Error: Map key cannot be null.");
        return map.Delete(key);
    }

    private static object? Clear(Interpreter _, SharpTSMap map, List<object?> args)
    {
        map.Clear();
        return null;
    }

    private static object? Keys(Interpreter _, SharpTSMap map, List<object?> args)
        => map.Keys();

    private static object? Values(Interpreter _, SharpTSMap map, List<object?> args)
        => map.Values();

    private static object? Entries(Interpreter _, SharpTSMap map, List<object?> args)
        => map.Entries();

    private static object? ForEach(Interpreter interp, SharpTSMap map, List<object?> args)
    {
        var callback = args[0] as ISharpTSCallable
            ?? throw new Exception("Runtime Error: forEach requires a function argument.");

        // JavaScript Map.forEach callback receives (value, key, map)
        var callbackArgs = new List<object?>(3) { null, null, map };
        foreach (var kvp in map.InternalEntries)
        {
            callbackArgs[0] = kvp.Value;
            callbackArgs[1] = kvp.Key;
            callback.Call(interp, callbackArgs);
        }
        return null;
    }

    private static object? GroupBy(Interpreter interp, List<object?> args)
    {
        var iterable = args[0] as SharpTSArray
            ?? throw new Exception("TypeError: Map.groupBy requires an iterable as first argument");
        var callback = args[1] as ISharpTSCallable
            ?? throw new Exception("TypeError: Map.groupBy requires a function as second argument");

        var result = new SharpTSMap();
        var callbackArgs = new List<object?> { null, null };

        for (int i = 0; i < iterable.Elements.Count; i++)
        {
            var element = iterable.Elements[i];
            callbackArgs[0] = element;
            callbackArgs[1] = (double)i;
            var key = callback.Call(interp, callbackArgs) ?? throw new Exception("Runtime Error: Map key cannot be null.");

            var existing = result.Get(key);
            if (existing == null)
            {
                existing = new SharpTSArray([]);
                result.Set(key, existing);
            }
            ((SharpTSArray)existing).Elements.Add(element);
        }

        return result;
    }
}
