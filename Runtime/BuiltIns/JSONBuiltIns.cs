using System.Text;
using System.Text.Json;
using SharpTS.Execution;
using SharpTS.Runtime.Exceptions;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Static methods on the JSON namespace (JSON.parse, JSON.stringify)
/// </summary>
public static class JSONBuiltIns
{
    public static object? GetStaticMethod(string name)
    {
        return name switch
        {
            "parse" => new BuiltInMethod("parse", 1, 2, ParseJson),
            "stringify" => new BuiltInMethod("stringify", 1, 3, StringifyJson),
            _ => null
        };
    }

    private static object? ParseJson(Interpreter interp, object? _, List<object?> args)
    {
        var text = args[0]?.ToString() ?? "null";
        var reviver = args.Count > 1 ? args[1] as ISharpTSCallable : null;

        object? parsed;
        try
        {
            using var doc = JsonDocument.Parse(text);
            parsed = ConvertJsonElement(doc.RootElement);
        }
        catch (JsonException)
        {
            throw new Exception("Unexpected token in JSON");
        }

        if (reviver != null)
        {
            parsed = ApplyReviver(interp, parsed, "", reviver);
        }

        return parsed;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Array => new SharpTSArray(
                element.EnumerateArray().Select(ConvertJsonElement).ToList()),
            JsonValueKind.Object => new SharpTSObject(
                element.EnumerateObject().ToDictionary(
                    p => p.Name,
                    p => ConvertJsonElement(p.Value))),
            _ => null
        };
    }

    private static object? ApplyReviver(Interpreter interp, object? value, object? key, ISharpTSCallable reviver)
    {
        // First, recursively transform children (bottom-up)
        if (value is SharpTSObject obj)
        {
            Dictionary<string, object?> newFields = [];
            foreach (var kv in obj.Fields)
            {
                // ApplyReviver already calls the reviver for each child
                var result = ApplyReviver(interp, kv.Value, kv.Key, reviver);
                if (result != null) // undefined removes the property
                    newFields[kv.Key] = result;
            }
            value = new SharpTSObject(newFields);
        }
        else if (value is SharpTSArray arr)
        {
            List<object?> newElements = [];
            for (int i = 0; i < arr.Length; i++)
            {
                // ApplyReviver already calls the reviver for each element
                var result = ApplyReviver(interp, arr[i], (double)i, reviver);
                newElements.Add(result);
            }
            value = new SharpTSArray(newElements);
        }

        // Then call reviver for this node (after children are transformed)
        return reviver.Call(interp, [key, value]);
    }

    private static object? StringifyJson(Interpreter interp, object? _, List<object?> args)
    {
        var value = args[0];
        var replacer = args.Count > 1 ? args[1] : null;
        var space = args.Count > 2 ? args[2] : null;

        // Handle space parameter: number = spaces, string = literal indent string
        string indentStr = "";
        switch (space)
        {
            case double d:
                var count = (int)Math.Min(Math.Max(d, 0), 10);
                indentStr = new string(' ', count);
                break;
            case string s:
                indentStr = s.Length > 10 ? s[..10] : s;
                break;
        }

        var replacerFunc = replacer as ISharpTSCallable;
        var replacerArray = replacer as SharpTSArray;
        HashSet<string>? allowedKeys = null;

        if (replacerArray != null)
        {
            allowedKeys = replacerArray
                .OfType<string>()
                .ToHashSet();
        }

        var sb = new StringBuilder();
        // ECMA-262 25.5.2.3: SerializeJSONProperty maintains a stack of currently-
        // serializing objects/arrays. A cycle throws TypeError. Reference equality
        // (not .Equals) is the spec's notion of identity.
        var seen = new HashSet<object>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        if (StringifyValue(interp, value, "", replacerFunc, allowedKeys, indentStr, 0, sb, seen))
        {
            return sb.ToString();
        }

        return null;
    }

    private static bool StringifyValue(Interpreter interp, object? value, object? key,
        ISharpTSCallable? replacer, HashSet<string>? allowedKeys, string indentStr, int depth, StringBuilder sb, HashSet<object> seen)
    {
        if (replacer != null)
        {
            value = replacer.Call(interp, [key, value]);
        }

        // Check for toJSON() method before serializing
        value = CallToJsonIfExists(interp, value);

        switch (value)
        {
            case null:
                sb.Append("null");
                return true;
            case bool b:
                sb.Append(b ? "true" : "false");
                return true;
            case double d:
                var numStr = FormatJsonNumber(d);
                if (numStr == "null") sb.Append("null");
                else sb.Append(numStr);
                return true;
            case string s:
                sb.Append(JsonSerializer.Serialize(s));
                return true;
            case SharpTSBigInt:
                throw new ThrowException("TypeError: BigInt value can't be serialized in JSON");
            case SharpTSArray arr:
                StringifyArray(interp, arr, replacer, allowedKeys, indentStr, depth, sb, seen);
                return true;
            case SharpTSObject obj:
                StringifyObject(interp, obj, replacer, allowedKeys, indentStr, depth, sb, seen);
                return true;
            case SharpTSInstance inst:
                StringifyInstance(interp, inst, replacer, allowedKeys, indentStr, depth, sb, seen);
                return true;
            // Plain Dictionary<string, object?> — used by runtime helpers like
            // Web Streams iterator results that produce JS-object-shaped data.
            // Compiled mode already serializes dicts as JS objects in its
            // emitted JSON.stringify path; this branch keeps the interpreter
            // at parity. SharpTSMap uses object keys (Dictionary<object, object?>)
            // and is handled separately by SharpTSMap-specific paths, so this
            // branch is unambiguous.
            case IReadOnlyDictionary<string, object?> dict:
                StringifyDictionary(interp, dict, replacer, allowedKeys, indentStr, depth, sb, seen);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Checks if the value has a toJSON() method and calls it if present.
    /// </summary>
    private static object? CallToJsonIfExists(Interpreter interp, object? value)
    {
        if (value is SharpTSInstance inst)
        {
            var toJson = inst.GetClass().FindMethod("toJSON");
            if (toJson != null)
                return SharpTSClass.BindMethod(toJson, inst).Call(interp, []);
        }
        else if (value is SharpTSObject obj && obj.Fields.TryGetValue("toJSON", out var fn))
        {
            if (fn is ISharpTSCallable callable)
                return callable.Call(interp, []);
        }
        return value;
    }

    private static string FormatJsonNumber(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
            return ((long)d).ToString();
        return d.ToString("G15");
    }

    private static void StringifyArray(Interpreter interp, SharpTSArray arr,
        ISharpTSCallable? replacer, HashSet<string>? allowedKeys, string indentStr, int depth, StringBuilder sb, HashSet<object> seen)
    {
        // ECMA-262 25.5.2.5 SerializeJSONArray — throw if we're re-entering
        // the same array mid-serialization (cycle).
        if (!seen.Add(arr))
            throw new ThrowException("TypeError: Converting circular structure to JSON");
        try
        {
            if (arr.Length == 0)
            {
                sb.Append("[]");
                return;
            }

            sb.Append('[');

            bool pretty = indentStr.Length > 0;
            string stepIndent = pretty ? "\n" + GetIndent(indentStr, depth + 1) : "";
            string separator = pretty ? "," + stepIndent : ",";

            if (pretty) sb.Append(stepIndent);

            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(separator);

                if (!StringifyValue(interp, arr[i], (double)i, replacer, allowedKeys, indentStr, depth + 1, sb, seen))
                {
                    sb.Append("null");
                }
            }

            if (pretty)
            {
                sb.Append('\n');
                sb.Append(GetIndent(indentStr, depth));
            }
            sb.Append(']');
        }
        finally
        {
            seen.Remove(arr);
        }
    }

    /// <summary>
    /// Serializes a plain <see cref="IReadOnlyDictionary{TKey, TValue}"/> as a
    /// JSON object. Identical body to <see cref="StringifyObject"/> but for
    /// the dict shape; kept as a sibling helper rather than a refactor to
    /// minimize churn on the SharpTSObject path that the rest of the
    /// interpreter relies on.
    /// </summary>
    private static void StringifyDictionary(Interpreter interp, IReadOnlyDictionary<string, object?> dict,
        ISharpTSCallable? replacer, HashSet<string>? allowedKeys, string indentStr, int depth, StringBuilder sb, HashSet<object> seen)
    {
        if (!seen.Add(dict))
            throw new ThrowException("TypeError: Converting circular structure to JSON");
        try
        {
            IEnumerable<KeyValuePair<string, object?>> fields = dict;
            if (allowedKeys != null)
            {
                fields = fields.Where(kv => allowedKeys.Contains(kv.Key));
            }

            var fieldList = fields.ToList();
            if (fieldList.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append('{');

            bool pretty = indentStr.Length > 0;
            string stepIndent = pretty ? "\n" + GetIndent(indentStr, depth + 1) : "";

            if (pretty) sb.Append(stepIndent);

            bool first = true;
            foreach (var kv in fieldList)
            {
                int mark = sb.Length;

                if (!first)
                {
                    sb.Append(',');
                    if (pretty) sb.Append(stepIndent);
                }

                sb.Append(JsonSerializer.Serialize(kv.Key));
                sb.Append(':');
                if (pretty) sb.Append(' ');

                if (StringifyValue(interp, kv.Value, kv.Key, replacer, allowedKeys, indentStr, depth + 1, sb, seen))
                {
                    first = false;
                }
                else
                {
                    sb.Length = mark;
                }
            }

            if (pretty)
            {
                sb.Append('\n');
                sb.Append(GetIndent(indentStr, depth));
            }
            sb.Append('}');
        }
        finally
        {
            seen.Remove(dict);
        }
    }

    private static void StringifyObject(Interpreter interp, SharpTSObject obj,
        ISharpTSCallable? replacer, HashSet<string>? allowedKeys, string indentStr, int depth, StringBuilder sb, HashSet<object> seen)
    {
        if (!seen.Add(obj))
            throw new ThrowException("TypeError: Converting circular structure to JSON");
        try
        {
            var fields = obj.Fields;
            if (allowedKeys != null)
            {
                fields = fields.Where(kv => allowedKeys.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            if (fields.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append('{');

            bool pretty = indentStr.Length > 0;
            string stepIndent = pretty ? "\n" + GetIndent(indentStr, depth + 1) : "";

            if (pretty) sb.Append(stepIndent);

            bool first = true;
            foreach (var kv in fields)
            {
                int mark = sb.Length;

                if (!first)
                {
                    sb.Append(',');
                    if (pretty) sb.Append(stepIndent);
                }

                sb.Append(JsonSerializer.Serialize(kv.Key));
                sb.Append(':');
                if (pretty) sb.Append(' ');

                if (StringifyValue(interp, kv.Value, kv.Key, replacer, allowedKeys, indentStr, depth + 1, sb, seen))
                {
                    first = false;
                }
                else
                {
                    // Value is undefined — rewind this entry (including the comma
                    // added above, since `mark` was captured before it).
                    sb.Length = mark;
                }
            }

            if (pretty)
            {
                sb.Append('\n');
                sb.Append(GetIndent(indentStr, depth));
            }
            sb.Append('}');
        }
        finally
        {
            seen.Remove(obj);
        }
    }

    private static void StringifyInstance(Interpreter interp, SharpTSInstance inst,
        ISharpTSCallable? replacer, HashSet<string>? allowedKeys, string indentStr, int depth, StringBuilder sb, HashSet<object> seen)
    {
        if (!seen.Add(inst))
            throw new ThrowException("TypeError: Converting circular structure to JSON");
        try
        {
            IEnumerable<string> fieldNames = inst.GetFieldNames();
            if (allowedKeys != null)
            {
                fieldNames = fieldNames.Where(k => allowedKeys.Contains(k));
            }

            var namesList = fieldNames.ToList();
            if (namesList.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append('{');

            bool pretty = indentStr.Length > 0;
            string stepIndent = pretty ? "\n" + GetIndent(indentStr, depth + 1) : "";

            if (pretty) sb.Append(stepIndent);

            bool first = true;
            foreach (var name in namesList)
            {
                int mark = sb.Length;

                if (!first)
                {
                    sb.Append(',');
                    if (pretty) sb.Append(stepIndent);
                }

                sb.Append(JsonSerializer.Serialize(name));
                sb.Append(':');
                if (pretty) sb.Append(' ');

                var fieldValue = inst.GetRawField(name);
                if (StringifyValue(interp, fieldValue, name, replacer, allowedKeys, indentStr, depth + 1, sb, seen))
                {
                    first = false;
                }
                else
                {
                    sb.Length = mark;
                }
            }

            if (pretty)
            {
                sb.Append('\n');
                sb.Append(GetIndent(indentStr, depth));
            }
            sb.Append('}');
        }
        finally
        {
            seen.Remove(inst);
        }
    }

    private static string GetIndent(string indentStr, int depth)
    {
        // Optimization: Cache small indent strings? 
        // For now, this is acceptable as it's only for pretty printing.
        return string.Concat(Enumerable.Repeat(indentStr, depth));
    }
}
