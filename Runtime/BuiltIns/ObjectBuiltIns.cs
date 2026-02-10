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
            .Method("getOwnPropertyNames", 1, GetOwnPropertyNames)
            .Method("create", 1, 2, Create)
            .Method("preventExtensions", 1, PreventExtensions)
            .Method("isExtensible", 1, IsExtensibleMethod)
            .Method("getOwnPropertySymbols", 1, GetOwnPropertySymbols)
            .Method("getPrototypeOf", 1, GetPrototypeOf)
            .Method("setPrototypeOf", 2, SetPrototypeOf)
            .Method("groupBy", 2, GroupBy)
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
    /// Object.getOwnPropertyNames(obj) - returns an array of all own property names (including non-enumerable).
    /// </summary>
    private static object? GetOwnPropertyNames(Interpreter _, List<object?> args)
    {
        var target = args[0];

        if (target == null)
        {
            throw new Exception("TypeError: Object.getOwnPropertyNames called on null or undefined");
        }

        List<object?> names = target switch
        {
            SharpTSObject obj => GetOwnPropertyNamesFromObject(obj),
            SharpTSInstance inst => inst.GetFieldNames().Select(k => (object?)k).ToList(),
            SharpTSArray arr => GetOwnPropertyNamesFromArray(arr),
            Dictionary<string, object?> dict => dict.Keys.Select(k => (object?)k).ToList(),
            _ => []
        };

        return new SharpTSArray(names);
    }

    /// <summary>
    /// Gets all own property names from a SharpTSObject (including accessor properties).
    /// </summary>
    private static List<object?> GetOwnPropertyNamesFromObject(SharpTSObject obj)
    {
        HashSet<string> names = new(obj.Fields.Keys);

        // Add accessor property names (getters define properties even without data)
        foreach (var key in obj.PropertyNames)
        {
            names.Add(key);
        }

        return names.Select(k => (object?)k).ToList();
    }

    /// <summary>
    /// Gets all own property names from a SharpTSArray (indices + length + any custom properties).
    /// </summary>
    private static List<object?> GetOwnPropertyNamesFromArray(SharpTSArray arr)
    {
        List<object?> names = [];

        // Add numeric indices
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            names.Add(i.ToString());
        }

        // Add "length"
        names.Add("length");

        return names;
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

    /// <summary>
    /// Object.create(proto, propertiesObject?) - creates a new object with the specified prototype.
    /// Since SharpTS doesn't have a true prototype chain, this copies properties from proto
    /// to simulate inheritance.
    /// </summary>
    private static object? Create(Interpreter _, List<object?> args)
    {
        var proto = args[0];
        var propertiesObject = args.Count > 1 ? args[1] : null;

        // Create a new empty object
        var result = new SharpTSObject([]);

        // Store the prototype reference
        result.Prototype = proto;

        // If proto is not null, copy its properties (simulating prototype inheritance)
        if (proto != null)
        {
            CopyPropertiesFrom(proto, result);
        }

        // If propertiesObject is provided, define properties using defineProperty semantics
        if (propertiesObject != null)
        {
            DefinePropertiesFromDescriptors(propertiesObject, result);
        }

        return result;
    }

    /// <summary>
    /// Copies properties from a source object to a target SharpTSObject.
    /// </summary>
    private static void CopyPropertiesFrom(object source, SharpTSObject target)
    {
        switch (source)
        {
            case SharpTSObject srcObj:
                foreach (var kv in srcObj.Fields)
                {
                    target.SetProperty(kv.Key, kv.Value);
                }
                // Copy getters and setters
                foreach (var propName in srcObj.PropertyNames)
                {
                    var getter = srcObj.GetGetter(propName);
                    var setter = srcObj.GetSetter(propName);
                    if (getter != null)
                        target.DefineGetter(propName, getter);
                    if (setter != null)
                        target.DefineSetter(propName, setter);
                }
                break;

            case SharpTSInstance srcInst:
                foreach (var key in srcInst.GetFieldNames())
                {
                    target.SetProperty(key, srcInst.GetRawField(key));
                }
                break;

            case Dictionary<string, object?> dict:
                foreach (var kv in dict)
                {
                    target.SetProperty(kv.Key, kv.Value);
                }
                break;
        }
    }

    /// <summary>
    /// Defines properties on target using property descriptors from propertiesObject.
    /// Each property in propertiesObject should be a descriptor object.
    /// </summary>
    private static void DefinePropertiesFromDescriptors(object propertiesObject, SharpTSObject target)
    {
        IEnumerable<KeyValuePair<string, object?>>? entries = propertiesObject switch
        {
            SharpTSObject obj => obj.Fields,
            Dictionary<string, object?> dict => dict,
            _ => null
        };

        if (entries == null) return;

        foreach (var kv in entries)
        {
            if (kv.Value == null) continue;

            var descriptor = SharpTSPropertyDescriptor.FromAnyObject(kv.Value);
            target.DefineProperty(kv.Key, descriptor);
        }
    }

    /// <summary>
    /// Runtime helper for Object.create called from compiled code.
    /// </summary>
    public static object? RuntimeCreate(object? proto, object? propertiesObject)
    {
        // Create a new object - for compiled mode, use Dictionary<string, object?>
        var result = new Dictionary<string, object?>();

        // Store the prototype reference
        PropertyDescriptorStore.SetPrototype(result, proto);

        // If proto is not null, copy its properties (simulating prototype inheritance)
        if (proto != null)
        {
            RuntimeCopyPropertiesFrom(proto, result);
        }

        // If propertiesObject is provided, define properties using defineProperty semantics
        if (propertiesObject != null)
        {
            RuntimeDefinePropertiesFromDescriptors(propertiesObject, result);
        }

        return result;
    }

    /// <summary>
    /// Copies properties from a source object to a target dictionary (compiled mode).
    /// </summary>
    private static void RuntimeCopyPropertiesFrom(object source, Dictionary<string, object?> target)
    {
        switch (source)
        {
            case SharpTSObject srcObj:
                foreach (var kv in srcObj.Fields)
                {
                    target[kv.Key] = kv.Value;
                }
                break;

            case SharpTSInstance srcInst:
                foreach (var key in srcInst.GetFieldNames())
                {
                    target[key] = srcInst.GetRawField(key);
                }
                break;

            case Dictionary<string, object?> dict:
                foreach (var kv in dict)
                {
                    target[kv.Key] = kv.Value;
                }
                break;

            case System.Collections.IDictionary idict:
                foreach (System.Collections.DictionaryEntry entry in idict)
                {
                    target[entry.Key?.ToString() ?? ""] = entry.Value;
                }
                break;

            default:
                // Try reflection for compiled class instances
                var type = source.GetType();

                // First, get typed backing fields (fields starting with __) for compiled class instances
                foreach (var backingField in type.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    if (backingField.Name.StartsWith("__"))
                    {
                        string pascalName = backingField.Name[2..]; // Remove __ prefix
                        // Convert PascalCase back to camelCase (how TypeScript originally named it)
                        string propName = ToCamelCase(pascalName);
                        target[propName] = backingField.GetValue(source);
                    }
                }

                // Also check for _fields dictionary (for dynamically added properties)
                var fieldsField = type.GetField("_fields", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fieldsField != null)
                {
                    var fieldsValue = fieldsField.GetValue(source);
                    if (fieldsValue is System.Collections.IDictionary fieldsDict)
                    {
                        foreach (System.Collections.DictionaryEntry entry in fieldsDict)
                        {
                            target[entry.Key?.ToString() ?? ""] = entry.Value;
                        }
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Defines properties on target using property descriptors (compiled mode).
    /// </summary>
    private static void RuntimeDefinePropertiesFromDescriptors(object propertiesObject, Dictionary<string, object?> target)
    {
        IEnumerable<KeyValuePair<string, object?>>? entries = null;

        if (propertiesObject is Dictionary<string, object?> dict)
        {
            entries = dict;
        }
        else if (propertiesObject is SharpTSObject obj)
        {
            entries = obj.Fields;
        }
        else if (propertiesObject is System.Collections.IDictionary idict)
        {
            var list = new List<KeyValuePair<string, object?>>();
            foreach (System.Collections.DictionaryEntry entry in idict)
            {
                list.Add(new KeyValuePair<string, object?>(entry.Key?.ToString() ?? "", entry.Value));
            }
            entries = list;
        }

        if (entries == null) return;

        foreach (var kv in entries)
        {
            if (kv.Value == null) continue;

            // Parse the descriptor and extract value or getter/setter
            var compiledDesc = CompiledPropertyDescriptor.FromAny(kv.Value);
            PropertyDescriptorStore.DefineProperty(target, kv.Key, compiledDesc);
        }
    }

    /// <summary>
    /// Converts a PascalCase property name to camelCase.
    /// </summary>
    private static string ToCamelCase(string pascalCase)
    {
        if (string.IsNullOrEmpty(pascalCase))
            return pascalCase;
        if (char.IsLower(pascalCase[0]))
            return pascalCase;
        return char.ToLowerInvariant(pascalCase[0]) + pascalCase[1..];
    }

    /// <summary>
    /// Object.preventExtensions(obj) - prevents new properties from being added to an object.
    /// Unlike freeze/seal, existing properties can still be modified and deleted.
    /// </summary>
    private static object? PreventExtensions(Interpreter _, List<object?> args)
    {
        switch (args[0])
        {
            case SharpTSObject obj:
                obj.PreventExtensions();
                return obj;
            case SharpTSInstance inst:
                inst.PreventExtensions();
                return inst;
            case SharpTSArray arr:
                arr.PreventExtensions();
                return arr;
            case Dictionary<string, object?> dict:
                PropertyDescriptorStore.PreventExtensions(dict);
                return dict;
            case System.Collections.IDictionary idict:
                PropertyDescriptorStore.PreventExtensions(idict);
                return idict;
            default:
                // Non-objects are returned unchanged (JavaScript behavior)
                return args[0];
        }
    }

    /// <summary>
    /// Object.isExtensible(obj) - returns whether new properties can be added to an object.
    /// </summary>
    private static object? IsExtensibleMethod(Interpreter _, List<object?> args)
    {
        return args[0] switch
        {
            SharpTSObject obj => obj.IsExtensible,
            SharpTSInstance inst => inst.IsExtensible,
            SharpTSArray arr => arr.IsExtensible,
            Dictionary<string, object?> dict => PropertyDescriptorStore.IsExtensible(dict),
            System.Collections.IDictionary idict => PropertyDescriptorStore.IsExtensible(idict),
            // Primitives are not extensible
            _ => false
        };
    }

    /// <summary>
    /// Object.getOwnPropertySymbols(obj) - returns an array of symbol-keyed properties.
    /// </summary>
    private static object? GetOwnPropertySymbols(Interpreter _, List<object?> args)
    {
        if (args[0] == null)
            throw new Exception("TypeError: Cannot convert null to object");

        List<object?> symbols = args[0] switch
        {
            SharpTSObject obj => obj.GetSymbolPropertyNames().Select(s => (object?)s).ToList(),
            SharpTSInstance inst => inst.GetSymbolPropertyNames().Select(s => (object?)s).ToList(),
            Dictionary<string, object?> dict => PropertyDescriptorStore.GetSymbolKeys(dict)
                                                  .Select(s => (object?)s).ToList(),
            _ => []
        };
        return new SharpTSArray(symbols);
    }

    /// <summary>
    /// Object.getPrototypeOf(obj) - returns the prototype of an object.
    /// </summary>
    private static object? GetPrototypeOf(Interpreter _, List<object?> args)
    {
        if (args[0] == null)
            throw new Exception("TypeError: Cannot convert null to object");

        return args[0] switch
        {
            SharpTSObject obj => obj.Prototype,
            SharpTSInstance inst => inst.Prototype,
            SharpTSArray => null, // Arrays have Array.prototype (simplified to null)
            Dictionary<string, object?> dict => PropertyDescriptorStore.GetPrototype(dict),
            _ => null // Primitives return null (simplified)
        };
    }

    /// <summary>
    /// Object.setPrototypeOf(obj, proto) - sets the prototype of an object.
    /// </summary>
    private static object? SetPrototypeOf(Interpreter _, List<object?> args)
    {
        var target = args[0];
        var proto = args.Count > 1 ? args[1] : null;

        if (target == null)
            throw new Exception("TypeError: Cannot convert null to object");

        switch (target)
        {
            case SharpTSObject obj:
                if (!obj.IsExtensible)
                    throw new Exception("TypeError: Object is not extensible");
                obj.Prototype = proto;
                // Copy properties from new prototype if non-null
                if (proto != null)
                    CopyPropertiesFrom(proto, obj);
                return obj;

            case SharpTSInstance:
                // Cannot change prototype of class instances
                throw new Exception("TypeError: Cannot set prototype of class instance");

            case Dictionary<string, object?> dict:
                if (!PropertyDescriptorStore.IsExtensible(dict))
                    throw new Exception("TypeError: Object is not extensible");
                PropertyDescriptorStore.SetPrototype(dict, proto);
                if (proto != null)
                    RuntimeCopyPropertiesFrom(proto, dict);
                return dict;

            default:
                // Non-objects return unchanged (JavaScript behavior)
                return target;
        }
    }

    /// <summary>
    /// Runtime helper for Object.preventExtensions called from compiled code.
    /// </summary>
    public static object? RuntimePreventExtensions(object? obj)
    {
        switch (obj)
        {
            case SharpTSObject tsObj:
                tsObj.PreventExtensions();
                return tsObj;
            case SharpTSInstance inst:
                inst.PreventExtensions();
                return inst;
            case SharpTSArray arr:
                arr.PreventExtensions();
                return arr;
            case Dictionary<string, object?> dict:
                PropertyDescriptorStore.PreventExtensions(dict);
                return dict;
            case List<object?> list:
                // Compiled arrays are List<object?>
                PropertyDescriptorStore.PreventExtensions(list);
                return list;
            case System.Collections.IDictionary idict:
                PropertyDescriptorStore.PreventExtensions(idict);
                return idict;
            case System.Collections.IList ilist:
                // Compiled arrays might also be IList
                PropertyDescriptorStore.PreventExtensions(ilist);
                return ilist;
            default:
                // For compiled class instances, use PropertyDescriptorStore
                if (obj != null && IsCompiledClassInstance(obj))
                {
                    PropertyDescriptorStore.PreventExtensions(obj);
                }
                return obj;
        }
    }

    /// <summary>
    /// Runtime helper for Object.isExtensible called from compiled code.
    /// </summary>
    public static bool RuntimeIsExtensible(object? obj)
    {
        return obj switch
        {
            SharpTSObject tsObj => tsObj.IsExtensible,
            SharpTSInstance inst => inst.IsExtensible,
            SharpTSArray arr => arr.IsExtensible,
            Dictionary<string, object?> dict => PropertyDescriptorStore.IsExtensible(dict),
            List<object?> list => PropertyDescriptorStore.IsExtensible(list),
            System.Collections.IDictionary idict => PropertyDescriptorStore.IsExtensible(idict),
            System.Collections.IList ilist => PropertyDescriptorStore.IsExtensible(ilist),
            null => false,
            _ when IsPrimitive(obj) => false,
            _ when IsCompiledClassInstance(obj) => PropertyDescriptorStore.IsExtensible(obj),
            _ => false
        };
    }

    /// <summary>
    /// Checks if an object is a primitive value (not extensible).
    /// </summary>
    private static bool IsPrimitive(object? obj)
    {
        return obj is double or int or float or decimal or string or bool or char;
    }

    /// <summary>
    /// Checks if an object is a compiled class instance (has _fields dictionary).
    /// </summary>
    private static bool IsCompiledClassInstance(object obj)
    {
        var type = obj.GetType();
        // Compiled class instances typically have a _fields field
        var fieldsField = type.GetField("_fields", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return fieldsField != null;
    }

    /// <summary>
    /// Runtime helper for Object.getOwnPropertySymbols called from compiled code.
    /// Returns a List<object?> for compiled code compatibility (not SharpTSArray).
    /// </summary>
    public static object? RuntimeGetOwnPropertySymbols(object? obj)
    {
        if (obj == null)
            throw new Exception("TypeError: Cannot convert null to object");

        List<object?> symbols = obj switch
        {
            SharpTSObject tsObj => tsObj.GetSymbolPropertyNames().Select(s => (object?)s).ToList(),
            SharpTSInstance inst => inst.GetSymbolPropertyNames().Select(s => (object?)s).ToList(),
            // For compiled objects (Dictionary or other), check RuntimeTypes symbol storage first,
            // then fall back to PropertyDescriptorStore for interpreted objects
            Dictionary<string, object?> dict => GetCompiledSymbolKeys(dict),
            _ => GetCompiledSymbolKeys(obj)
        };
        // Return List<object?> for compiled code (not SharpTSArray which is for interpreted mode)
        return symbols;
    }

    /// <summary>
    /// Gets symbol keys from an object, checking RuntimeTypes first (for compiled code)
    /// then PropertyDescriptorStore (for interpreted objects).
    /// </summary>
    private static List<object?> GetCompiledSymbolKeys(object obj)
    {
        // Try RuntimeTypes._symbolStorage first (used by compiled code)
        var compiledSymbols = SharpTS.Compilation.RuntimeTypes.GetSymbolKeys(obj).ToList();
        if (compiledSymbols.Count > 0)
        {
            return compiledSymbols.Select(s => (object?)s).ToList();
        }

        // Fall back to PropertyDescriptorStore (used by interpreted code)
        return PropertyDescriptorStore.GetSymbolKeys(obj).Select(s => (object?)s).ToList();
    }

    /// <summary>
    /// Runtime helper for Object.getPrototypeOf called from compiled code.
    /// </summary>
    public static object? RuntimeGetPrototypeOf(object? obj)
    {
        if (obj == null)
            throw new Exception("TypeError: Cannot convert null to object");

        return obj switch
        {
            SharpTSObject tsObj => tsObj.Prototype,
            SharpTSInstance inst => inst.Prototype,
            SharpTSArray => null,
            Dictionary<string, object?> dict => PropertyDescriptorStore.GetPrototype(dict),
            _ => null
        };
    }

    /// <summary>
    /// Runtime helper for Object.setPrototypeOf called from compiled code.
    /// </summary>
    public static object? RuntimeSetPrototypeOf(object? target, object? proto)
    {
        if (target == null)
            throw new Exception("TypeError: Cannot convert null to object");

        switch (target)
        {
            case SharpTSObject obj:
                if (!obj.IsExtensible)
                    throw new Exception("TypeError: Object is not extensible");
                obj.Prototype = proto;
                if (proto != null)
                    CopyPropertiesFrom(proto, obj);
                return obj;

            case SharpTSInstance:
                throw new Exception("TypeError: Cannot set prototype of class instance");

            case Dictionary<string, object?> dict:
                if (!PropertyDescriptorStore.IsExtensible(dict))
                    throw new Exception("TypeError: Object is not extensible");
                PropertyDescriptorStore.SetPrototype(dict, proto);
                if (proto != null)
                    RuntimeCopyPropertiesFrom(proto, dict);
                return dict;

            default:
                // Check for compiled class instances
                if (target != null && IsCompiledClassInstance(target))
                {
                    throw new Exception("TypeError: Cannot set prototype of class instance");
                }
                return target;
        }
    }

    private static object? GroupBy(Interpreter interp, List<object?> args)
    {
        var iterable = args[0] as SharpTSArray
            ?? throw new Exception("TypeError: Object.groupBy requires an iterable as first argument");
        var callback = args[1] as ISharpTSCallable
            ?? throw new Exception("TypeError: Object.groupBy requires a function as second argument");

        var groups = new Dictionary<string, object?>();
        var callbackArgs = new List<object?> { null, null };

        for (int i = 0; i < iterable.Elements.Count; i++)
        {
            var element = iterable.Elements[i];
            callbackArgs[0] = element;
            callbackArgs[1] = (double)i;
            var key = callback.Call(interp, callbackArgs);
            var keyStr = key?.ToString() ?? "undefined";

            if (!groups.TryGetValue(keyStr, out var existing))
            {
                existing = new SharpTSArray([]);
                groups[keyStr] = existing;
            }
            ((SharpTSArray)existing!).Elements.Add(element);
        }

        return new SharpTSObject(groups);
    }
}
