using SharpTS.Compilation;
using SharpTS.Execution;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

public static class ObjectBuiltIns
{
    private static readonly BuiltInStaticMemberLookup _staticLookup =
        BuiltInStaticBuilder.Create()
            .Method("keys", 1, Keys)
            .Method("values", 1, Values)
            .Method("entries", 1, Entries)
            .Method("fromEntries", 1, FromEntries)
            .Method("hasOwn", 2, HasOwn)
            .Method("is", 2, Is)
            .Method("assign", 1, int.MaxValue, Assign)
            .Method("freeze", 1, Freeze)
            .Method("seal", 1, Seal)
            .Method("isFrozen", 1, IsFrozen)
            .Method("isSealed", 1, IsSealed)
            .Method("defineProperty", 3, DefineProperty)
            .Method("getOwnPropertyDescriptor", 2, GetOwnPropertyDescriptor)
            .Build();

    /// <summary>
    /// Get static methods on the Object namespace (e.g., Object.keys())
    /// </summary>
    public static object? GetStaticMethod(string name)
        => _staticLookup.GetMember(name);

    private static object? Keys(Interpreter _, List<object?> args)
    {
        if (args[0] is SharpTSObject obj)
        {
            var keys = obj.Fields.Keys.Select(k => (object?)k).ToList();
            return new SharpTSArray(keys);
        }
        if (args[0] is SharpTSInstance inst)
        {
            var keys = inst.GetFieldNames().Select(k => (object?)k).ToList();
            return new SharpTSArray(keys);
        }
        throw new Exception("Object.keys() requires an object argument");
    }

    private static object? Values(Interpreter _, List<object?> args)
    {
        if (args[0] is SharpTSObject obj)
        {
            var values = obj.Fields.Values.ToList();
            return new SharpTSArray(values);
        }
        if (args[0] is SharpTSInstance inst)
        {
            var values = inst.GetFieldNames().Select(n => inst.GetRawField(n)).ToList();
            return new SharpTSArray(values);
        }
        throw new Exception("Object.values() requires an object argument");
    }

    private static object? Entries(Interpreter _, List<object?> args)
    {
        if (args[0] is SharpTSObject obj)
        {
            var entries = obj.Fields.Select(kv =>
                (object?)new SharpTSArray([(object?)kv.Key, kv.Value])).ToList();
            return new SharpTSArray(entries);
        }
        if (args[0] is SharpTSInstance inst)
        {
            var entries = inst.GetFieldNames().Select(n =>
                (object?)new SharpTSArray([(object?)n, inst.GetRawField(n)])).ToList();
            return new SharpTSArray(entries);
        }
        throw new Exception("Object.entries() requires an object argument");
    }

    private static object? FromEntries(Interpreter interpreter, List<object?> args)
    {
        if (args[0] == null)
            throw new Exception("Runtime Error: Object.fromEntries() requires an iterable argument");

        var elements = interpreter.GetIterableElements(args[0]);
        Dictionary<string, object?> result = [];

        foreach (var element in elements)
        {
            if (element is SharpTSArray pair && pair.Elements.Count >= 2)
            {
                string key = pair.Get(0)?.ToString() ?? "";
                result[key] = pair.Get(1);
            }
            else if (element is List<object?> listPair && listPair.Count >= 2)
            {
                string key = listPair[0]?.ToString() ?? "";
                result[key] = listPair[1];
            }
            else
            {
                throw new Exception("Runtime Error: Object.fromEntries() requires [key, value] pairs");
            }
        }
        return new SharpTSObject(result);
    }

    private static object? HasOwn(Interpreter _, List<object?> args)
    {
        var key = args[1]?.ToString() ?? "";
        return args[0] switch
        {
            SharpTSObject obj => obj.Fields.ContainsKey(key),
            SharpTSInstance inst => inst.GetFieldNames().Contains(key),
            _ => false
        };
    }

    /// <summary>
    /// Object.is(value1, value2) - determines whether two values are the same value.
    /// Unlike === operator:
    /// - Object.is(NaN, NaN) returns true
    /// - Object.is(-0, +0) returns false
    /// </summary>
    private static object? Is(Interpreter _, List<object?> args)
    {
        var value1 = args[0];
        var value2 = args[1];

        // Handle null/undefined cases
        if (value1 is null && value2 is null)
            return true;
        if (value1 is null || value2 is null)
            return false;

        // Handle number cases (NaN and -0/+0)
        if (value1 is double d1 && value2 is double d2)
        {
            // NaN === NaN should be true for Object.is
            if (double.IsNaN(d1) && double.IsNaN(d2))
                return true;

            // +0 and -0 should be different for Object.is
            if (d1 == 0.0 && d2 == 0.0)
            {
                // Check if signs are the same using 1/x trick
                // 1/+0 = +Infinity, 1/-0 = -Infinity
                return 1.0 / d1 == 1.0 / d2;
            }

            return d1 == d2;
        }

        // Handle bigint cases
        if (value1 is System.Numerics.BigInteger bi1 && value2 is System.Numerics.BigInteger bi2)
            return bi1 == bi2;

        // For all other types, use reference equality for objects, value equality for primitives
        if (value1 is string s1 && value2 is string s2)
            return s1 == s2;

        if (value1 is bool b1 && value2 is bool b2)
            return b1 == b2;

        // Reference equality for objects
        return ReferenceEquals(value1, value2);
    }

    private static object? Assign(Interpreter _, List<object?> args)
    {
        // Object.assign(target, ...sources)
        if (args.Count == 0 || args[0] == null)
            throw new Exception("Runtime Error: Object.assign() requires a target object");

        // Handle SharpTSObject target
        if (args[0] is SharpTSObject targetObj)
        {
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i] == null) continue;

                if (args[i] is SharpTSObject srcObj)
                {
                    foreach (var kv in srcObj.Fields)
                        targetObj.SetProperty(kv.Key, kv.Value);
                }
                else if (args[i] is SharpTSInstance srcInst)
                {
                    foreach (var key in srcInst.GetFieldNames())
                        targetObj.SetProperty(key, srcInst.GetRawField(key));
                }
            }
            return args[0];
        }

        // Handle SharpTSInstance target
        if (args[0] is SharpTSInstance targetInst)
        {
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i] == null) continue;

                if (args[i] is SharpTSObject srcObj)
                {
                    foreach (var kv in srcObj.Fields)
                        targetInst.SetRawField(kv.Key, kv.Value);
                }
                else if (args[i] is SharpTSInstance srcInst)
                {
                    foreach (var key in srcInst.GetFieldNames())
                        targetInst.SetRawField(key, srcInst.GetRawField(key));
                }
            }
            return args[0];
        }

        throw new Exception("Runtime Error: Object.assign() target must be an object");
    }

    private static object? Freeze(Interpreter _, List<object?> args)
    {
        // Object.freeze(obj) - freezes the object and returns it
        switch (args[0])
        {
            case SharpTSObject obj:
                obj.Freeze();
                return obj;
            case SharpTSInstance inst:
                inst.Freeze();
                return inst;
            case SharpTSArray arr:
                arr.Freeze();
                return arr;
            case Dictionary<string, object?> dict:
                // Use PropertyDescriptorStore for compiled dictionaries
                PropertyDescriptorStore.Freeze(dict);
                return dict;
            case System.Collections.IDictionary idict:
                PropertyDescriptorStore.Freeze(idict);
                return idict;
            default:
                // Non-objects are returned unchanged (JavaScript behavior)
                return args[0];
        }
    }

    private static object? Seal(Interpreter _, List<object?> args)
    {
        // Object.seal(obj) - seals the object and returns it
        switch (args[0])
        {
            case SharpTSObject obj:
                obj.Seal();
                return obj;
            case SharpTSInstance inst:
                inst.Seal();
                return inst;
            case SharpTSArray arr:
                arr.Seal();
                return arr;
            case Dictionary<string, object?> dict:
                // Use PropertyDescriptorStore for compiled dictionaries
                PropertyDescriptorStore.Seal(dict);
                return dict;
            case System.Collections.IDictionary idict:
                PropertyDescriptorStore.Seal(idict);
                return idict;
            default:
                // Non-objects are returned unchanged (JavaScript behavior)
                return args[0];
        }
    }

    private static object? IsFrozen(Interpreter _, List<object?> args)
    {
        // Object.isFrozen(obj) - returns true if the object is frozen
        return args[0] switch
        {
            SharpTSObject obj => obj.IsFrozen,
            SharpTSInstance inst => inst.IsFrozen,
            SharpTSArray arr => arr.IsFrozen,
            Dictionary<string, object?> dict => PropertyDescriptorStore.IsFrozen(dict),
            System.Collections.IDictionary idict => PropertyDescriptorStore.IsFrozen(idict),
            // Non-extensible primitives are considered frozen in JavaScript
            _ => true
        };
    }

    private static object? IsSealed(Interpreter _, List<object?> args)
    {
        // Object.isSealed(obj) - returns true if the object is sealed
        return args[0] switch
        {
            SharpTSObject obj => obj.IsSealed,
            SharpTSInstance inst => inst.IsSealed,
            SharpTSArray arr => arr.IsSealed,
            Dictionary<string, object?> dict => PropertyDescriptorStore.IsSealed(dict),
            System.Collections.IDictionary idict => PropertyDescriptorStore.IsSealed(idict),
            // Non-extensible primitives are considered sealed in JavaScript
            _ => true
        };
    }

    /// <summary>
    /// Object.defineProperty(obj, prop, descriptor) - defines a new property or modifies an existing one.
    /// </summary>
    private static object? DefineProperty(Interpreter _, List<object?> args)
    {
        var target = args[0];
        var propertyKey = args[1]?.ToString() ?? "";
        var descriptorArg = args[2];

        if (target == null)
        {
            throw new Exception("TypeError: Object.defineProperty called on null or undefined");
        }

        if (descriptorArg == null)
        {
            throw new Exception("TypeError: Property description must be an object");
        }

        // Parse descriptor from object - use FromAnyObject to handle any object type
        SharpTSPropertyDescriptor descriptor = SharpTSPropertyDescriptor.FromAnyObject(descriptorArg);

        bool success;
        switch (target)
        {
            case SharpTSObject obj:
                success = obj.DefineProperty(propertyKey, descriptor);
                break;
            case SharpTSInstance inst:
                success = inst.DefineProperty(propertyKey, descriptor);
                break;
            case SharpTSArray arr:
                // Arrays can have properties defined on them
                success = arr.DefineProperty(propertyKey, descriptor);
                break;
            case Dictionary<string, object?> dict:
                // Compiled mode: Dictionary<string, object?> for any-typed object literals
                var compiledDesc = CompiledPropertyDescriptor.FromAny(descriptorArg);
                success = PropertyDescriptorStore.DefineProperty(dict, propertyKey, compiledDesc);
                break;
            default:
                throw new Exception("TypeError: Object.defineProperty called on non-object");
        }

        if (!success)
        {
            throw new Exception($"TypeError: Cannot define property '{propertyKey}': object is not extensible or property is not configurable");
        }

        return target;
    }

    /// <summary>
    /// Object.getOwnPropertyDescriptor(obj, prop) - returns the property descriptor for an own property.
    /// </summary>
    private static object? GetOwnPropertyDescriptor(Interpreter _, List<object?> args)
    {
        var target = args[0];
        var propertyKey = args[1]?.ToString() ?? "";

        if (target == null)
        {
            throw new Exception("TypeError: Object.getOwnPropertyDescriptor called on null or undefined");
        }

        SharpTSPropertyDescriptor? descriptor = target switch
        {
            SharpTSObject obj => obj.GetOwnPropertyDescriptor(propertyKey),
            SharpTSInstance inst => inst.GetOwnPropertyDescriptor(propertyKey),
            SharpTSArray arr => arr.GetOwnPropertyDescriptor(propertyKey),
            Dictionary<string, object?> dict => GetDictionaryPropertyDescriptor(dict, propertyKey),
            _ => null
        };

        if (descriptor == null)
        {
            return null;
        }

        // Return as an object
        return descriptor.ToObject();
    }

    /// <summary>
    /// Gets property descriptor for a compiled Dictionary<string, object?>.
    /// </summary>
    private static SharpTSPropertyDescriptor? GetDictionaryPropertyDescriptor(Dictionary<string, object?> dict, string propertyKey)
    {
        // First check PropertyDescriptorStore for explicitly defined descriptors
        var compiledDesc = PropertyDescriptorStore.GetPropertyDescriptor(dict, propertyKey);
        if (compiledDesc != null)
        {
            return new SharpTSPropertyDescriptor
            {
                Value = compiledDesc.Value,
                Get = compiledDesc.Getter as ISharpTSCallable,
                Set = compiledDesc.Setter as ISharpTSCallable,
                Writable = compiledDesc.Writable,
                Enumerable = compiledDesc.Enumerable,
                Configurable = compiledDesc.Configurable
            };
        }

        // Fall back to checking if property exists in dictionary (default data descriptor)
        if (dict.TryGetValue(propertyKey, out var value))
        {
            return new SharpTSPropertyDescriptor
            {
                Value = value,
                Writable = true,
                Enumerable = true,
                Configurable = true
            };
        }

        return null;
    }

    /// <summary>
    /// Creates a new object with all properties from source except those in excludeKeys.
    /// Used for object rest patterns: const { x, ...rest } = obj;
    /// </summary>
    public static SharpTSObject ObjectRest(object? source, IEnumerable<object?> excludeKeys)
    {
        HashSet<string> excludeSet = new(excludeKeys.Where(k => k != null).Select(k => k!.ToString()!));
        Dictionary<string, object?> result = [];

        if (source is SharpTSObject obj)
        {
            foreach (var key in obj.Fields.Keys)
            {
                if (!excludeSet.Contains(key))
                    result[key] = obj.Fields[key];
            }
        }
        else if (source is SharpTSInstance inst)
        {
            foreach (var key in inst.GetFieldNames())
            {
                if (!excludeSet.Contains(key))
                    result[key] = inst.GetRawField(key);
            }
        }

        return new SharpTSObject(result);
    }

    /// <summary>
    /// Runtime helper for Object.defineProperty called from compiled code.
    /// </summary>
    public static object? RuntimeDefineProperty(object? target, object? propertyKey, object? descriptorArg)
    {
        var propKey = propertyKey?.ToString() ?? "";

        if (target == null)
        {
            throw new Exception("TypeError: Object.defineProperty called on null or undefined");
        }

        if (descriptorArg == null)
        {
            throw new Exception("TypeError: Property description must be an object");
        }

        // Parse descriptor from object - use FromAnyObject to handle both SharpTSObject and compiled $Object
        SharpTSPropertyDescriptor descriptor = SharpTSPropertyDescriptor.FromAnyObject(descriptorArg);

        bool success;
        switch (target)
        {
            case SharpTSObject obj:
                success = obj.DefineProperty(propKey, descriptor);
                break;
            case SharpTSInstance inst:
                success = inst.DefineProperty(propKey, descriptor);
                break;
            case SharpTSArray arr:
                success = arr.DefineProperty(propKey, descriptor);
                break;
            case Dictionary<string, object?> dict:
                // Handle compiled object literals (e.g., let obj: any = {})
                // Use PropertyDescriptorStore for full descriptor support
                // Parse directly from raw descriptor to preserve TSFunction getters/setters
                var compiledDesc = CompiledPropertyDescriptor.FromAny(descriptorArg);
                success = PropertyDescriptorStore.DefineProperty(target, propKey, compiledDesc);
                break;
            case System.Collections.IDictionary dict:
                // Handle other dictionary types
                // Parse directly from raw descriptor to preserve TSFunction getters/setters
                var compiledDesc2 = CompiledPropertyDescriptor.FromAny(descriptorArg);
                success = PropertyDescriptorStore.DefineProperty(target, propKey, compiledDesc2);
                break;
            case System.Collections.IList list:
                // Handle compiled arrays
                success = TryDefinePropertyOnList(list, propKey, descriptor);
                break;
            default:
                // Try to handle compiled $Object type using reflection
                success = TryDefinePropertyViaReflection(target, propKey, descriptor);
                break;
        }

        if (!success)
        {
            throw new Exception($"TypeError: Cannot define property '{propKey}': object is not extensible or property is not configurable");
        }

        return target;
    }

    /// <summary>
    /// Attempts to define a property on a compiled array (IList).
    /// </summary>
    private static bool TryDefinePropertyOnList(System.Collections.IList list, string propKey, SharpTSPropertyDescriptor descriptor)
    {
        // Only support numeric indices for arrays
        if (int.TryParse(propKey, out int index) && index >= 0)
        {
            // Expand list if needed
            while (list.Count <= index)
            {
                list.Add(null);
            }
            list[index] = descriptor.Value;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Attempts to define a property on a compiled $Object using reflection.
    /// </summary>
    private static bool TryDefinePropertyViaReflection(object target, string propKey, SharpTSPropertyDescriptor descriptor)
    {
        var type = target.GetType();

        // Check if this looks like a compiled $Object (has SetProperty method)
        var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        System.Reflection.MethodInfo? setPropertyMethod = null;

        foreach (var m in methods)
        {
            if (m.Name == "SetProperty")
            {
                var parms = m.GetParameters();
                if (parms.Length == 2 && parms[0].ParameterType == typeof(string))
                {
                    setPropertyMethod = m;
                    break;
                }
            }
        }

        if (setPropertyMethod != null)
        {
            // For compiled objects, we just set the value directly
            // Full descriptor support would require modifying the compiled type
            setPropertyMethod.Invoke(target, [propKey, descriptor.Value]);
            return true;
        }

        // Fallback: check if the type has a _fields dictionary (compiled $Object)
        var fieldsField = type.GetField("_fields", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (fieldsField != null)
        {
            var fieldsValue = fieldsField.GetValue(target);
            if (fieldsValue is System.Collections.IDictionary dict)
            {
                dict[propKey] = descriptor.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runtime helper for Object.getOwnPropertyDescriptor called from compiled code.
    /// </summary>
    public static object? RuntimeGetOwnPropertyDescriptor(object? target, object? propertyKey)
    {
        var propKey = propertyKey?.ToString() ?? "";

        if (target == null)
        {
            throw new Exception("TypeError: Object.getOwnPropertyDescriptor called on null or undefined");
        }

        // Special handling for Dictionary<string, object?> to preserve $TSFunction getters/setters
        // (which don't implement ISharpTSCallable)
        if (target is Dictionary<string, object?> dict)
        {
            // Check PropertyDescriptorStore for explicitly defined descriptor
            var storedDesc = PropertyDescriptorStore.GetPropertyDescriptor(dict, propKey);
            if (storedDesc != null)
            {
                // Use CompiledPropertyDescriptor.ToObject() directly to preserve getter/setter types
                return storedDesc.ToObject();
            }

            // Fall back to checking the dictionary directly
            if (dict.TryGetValue(propKey, out var value))
            {
                var desc = new SharpTSPropertyDescriptor
                {
                    Value = value,
                    Writable = true,
                    Enumerable = true,
                    Configurable = true
                };
                return desc.ToObject();
            }
            return null;
        }

        SharpTSPropertyDescriptor? descriptor = target switch
        {
            SharpTSObject obj => obj.GetOwnPropertyDescriptor(propKey),
            SharpTSInstance inst => inst.GetOwnPropertyDescriptor(propKey),
            SharpTSArray arr => arr.GetOwnPropertyDescriptor(propKey),
            System.Collections.IDictionary idict => GetDescriptorFromIDictionary(idict, propKey),
            System.Collections.IList list => GetDescriptorFromList(list, propKey),
            _ => TryGetPropertyDescriptorViaReflection(target, propKey)
        };

        if (descriptor == null)
        {
            return null;
        }

        // Return as an object
        return descriptor.ToObject();
    }

    /// <summary>
    /// Gets a property descriptor from a Dictionary<string, object?>.
    /// </summary>
    private static SharpTSPropertyDescriptor? GetDescriptorFromDictionary(Dictionary<string, object?> dict, string propKey)
    {
        // Check PropertyDescriptorStore for explicitly defined descriptor
        var storedDesc = PropertyDescriptorStore.GetPropertyDescriptor(dict, propKey);
        if (storedDesc != null)
        {
            return new SharpTSPropertyDescriptor
            {
                Value = storedDesc.Value,
                Get = storedDesc.Getter as ISharpTSCallable,
                Set = storedDesc.Setter as ISharpTSCallable,
                Writable = storedDesc.Writable,
                Enumerable = storedDesc.Enumerable,
                Configurable = storedDesc.Configurable
            };
        }

        // Fall back to checking the dictionary directly
        if (!dict.TryGetValue(propKey, out var value))
        {
            return null;
        }
        return new SharpTSPropertyDescriptor
        {
            Value = value,
            Writable = true,
            Enumerable = true,
            Configurable = true
        };
    }

    /// <summary>
    /// Gets a property descriptor from an IList (compiled arrays).
    /// </summary>
    private static SharpTSPropertyDescriptor? GetDescriptorFromList(System.Collections.IList list, string propKey)
    {
        // Handle "length" property
        if (propKey == "length")
        {
            return new SharpTSPropertyDescriptor
            {
                Value = (double)list.Count,
                Writable = true,
                Enumerable = false,
                Configurable = false
            };
        }

        // Handle numeric index
        if (int.TryParse(propKey, out int index) && index >= 0 && index < list.Count)
        {
            return new SharpTSPropertyDescriptor
            {
                Value = list[index],
                Writable = true,
                Enumerable = true,
                Configurable = true
            };
        }

        return null;
    }

    /// <summary>
    /// Gets a property descriptor from an IDictionary.
    /// </summary>
    private static SharpTSPropertyDescriptor? GetDescriptorFromIDictionary(System.Collections.IDictionary dict, string propKey)
    {
        // Check PropertyDescriptorStore for explicitly defined descriptor
        var storedDesc = PropertyDescriptorStore.GetPropertyDescriptor(dict, propKey);
        if (storedDesc != null)
        {
            return new SharpTSPropertyDescriptor
            {
                Value = storedDesc.Value,
                Get = storedDesc.Getter as ISharpTSCallable,
                Set = storedDesc.Setter as ISharpTSCallable,
                Writable = storedDesc.Writable,
                Enumerable = storedDesc.Enumerable,
                Configurable = storedDesc.Configurable
            };
        }

        // Fall back to checking the dictionary directly
        if (!dict.Contains(propKey))
        {
            return null;
        }
        return new SharpTSPropertyDescriptor
        {
            Value = dict[propKey],
            Writable = true,
            Enumerable = true,
            Configurable = true
        };
    }

    /// <summary>
    /// Attempts to get a property descriptor from a compiled $Object using reflection.
    /// </summary>
    private static SharpTSPropertyDescriptor? TryGetPropertyDescriptorViaReflection(object target, string propKey)
    {
        var type = target.GetType();
        var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        // Find HasProperty and GetProperty methods
        System.Reflection.MethodInfo? hasPropertyMethod = null;
        System.Reflection.MethodInfo? getPropertyMethod = null;

        foreach (var m in methods)
        {
            if (m.Name == "HasProperty")
            {
                var parms = m.GetParameters();
                if (parms.Length == 1 && parms[0].ParameterType == typeof(string))
                {
                    hasPropertyMethod = m;
                }
            }
            else if (m.Name == "GetProperty")
            {
                var parms = m.GetParameters();
                if (parms.Length == 1 && parms[0].ParameterType == typeof(string))
                {
                    getPropertyMethod = m;
                }
            }
        }

        if (hasPropertyMethod != null && getPropertyMethod != null)
        {
            var hasProperty = (bool?)hasPropertyMethod.Invoke(target, [propKey]);
            if (hasProperty != true)
            {
                return null;
            }

            var value = getPropertyMethod.Invoke(target, [propKey]);
            return new SharpTSPropertyDescriptor
            {
                Value = value,
                Writable = true,
                Enumerable = true,
                Configurable = true
            };
        }

        return null;
    }
}
