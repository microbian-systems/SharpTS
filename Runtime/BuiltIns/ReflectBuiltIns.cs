using SharpTS.Compilation;
using SharpTS.Execution;
using SharpTS.Runtime.Exceptions;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Provides implementations for the Reflect metadata API (reflect-metadata polyfill).
/// Used by decorators for storing and retrieving metadata on classes and class members.
/// </summary>
/// <remarks>
/// Implements the reflect-metadata API:
/// - Reflect.defineMetadata(key, value, target, propertyKey?)
/// - Reflect.getMetadata(key, target, propertyKey?)
/// - Reflect.hasMetadata(key, target, propertyKey?)
/// - Reflect.getMetadataKeys(target, propertyKey?)
/// - Reflect.metadata(key, value) - decorator factory
/// </remarks>
public static class ReflectBuiltIns
{
    /// <summary>
    /// Gets a static method from the Reflect namespace.
    /// </summary>
    public static ISharpTSCallable? GetStaticMethod(string name)
    {
        return name switch
        {
            "defineMetadata" => new BuiltInMethod("defineMetadata", 3, 4, (_, _, args) =>
            {
                var key = args[0]?.ToString() ?? "";
                var value = args[1];
                var target = args[2] ?? throw new Exception("Runtime Error: Reflect.defineMetadata requires a target.");
                var propertyKey = args.Count > 3 ? args[3]?.ToString() : null;

                ReflectMetadataStore.Instance.DefineMetadata(key, value, target, propertyKey);
                return null;
            }),

            "getMetadata" => new BuiltInMethod("getMetadata", 2, 3, (_, _, args) =>
            {
                var key = args[0]?.ToString() ?? "";
                var target = args[1] ?? throw new Exception("Runtime Error: Reflect.getMetadata requires a target.");
                var propertyKey = args.Count > 2 ? args[2]?.ToString() : null;

                return ReflectMetadataStore.Instance.GetMetadata(key, target, propertyKey);
            }),

            "getOwnMetadata" => new BuiltInMethod("getOwnMetadata", 2, 3, (_, _, args) =>
            {
                var key = args[0]?.ToString() ?? "";
                var target = args[1] ?? throw new Exception("Runtime Error: Reflect.getOwnMetadata requires a target.");
                var propertyKey = args.Count > 2 ? args[2]?.ToString() : null;

                return ReflectMetadataStore.Instance.GetOwnMetadata(key, target, propertyKey);
            }),

            "hasMetadata" => new BuiltInMethod("hasMetadata", 2, 3, (_, _, args) =>
            {
                var key = args[0]?.ToString() ?? "";
                var target = args[1] ?? throw new Exception("Runtime Error: Reflect.hasMetadata requires a target.");
                var propertyKey = args.Count > 2 ? args[2]?.ToString() : null;

                return ReflectMetadataStore.Instance.HasMetadata(key, target, propertyKey);
            }),

            "hasOwnMetadata" => new BuiltInMethod("hasOwnMetadata", 2, 3, (_, _, args) =>
            {
                var key = args[0]?.ToString() ?? "";
                var target = args[1] ?? throw new Exception("Runtime Error: Reflect.hasOwnMetadata requires a target.");
                var propertyKey = args.Count > 2 ? args[2]?.ToString() : null;

                return ReflectMetadataStore.Instance.HasOwnMetadata(key, target, propertyKey);
            }),

            "getMetadataKeys" => new BuiltInMethod("getMetadataKeys", 1, 2, (_, _, args) =>
            {
                var target = args[0] ?? throw new Exception("Runtime Error: Reflect.getMetadataKeys requires a target.");
                var propertyKey = args.Count > 1 ? args[1]?.ToString() : null;

                var keys = ReflectMetadataStore.Instance.GetMetadataKeys(target, propertyKey);
                return new SharpTSArray(keys.Cast<object?>().ToList());
            }),

            "getOwnMetadataKeys" => new BuiltInMethod("getOwnMetadataKeys", 1, 2, (_, _, args) =>
            {
                var target = args[0] ?? throw new Exception("Runtime Error: Reflect.getOwnMetadataKeys requires a target.");
                var propertyKey = args.Count > 1 ? args[1]?.ToString() : null;

                var keys = ReflectMetadataStore.Instance.GetOwnMetadataKeys(target, propertyKey);
                return new SharpTSArray(keys.Cast<object?>().ToList());
            }),

            "deleteMetadata" => new BuiltInMethod("deleteMetadata", 2, 3, (_, _, args) =>
            {
                var key = args[0]?.ToString() ?? "";
                var target = args[1] ?? throw new Exception("Runtime Error: Reflect.deleteMetadata requires a target.");
                var propertyKey = args.Count > 2 ? args[2]?.ToString() : null;

                return ReflectMetadataStore.Instance.DeleteMetadata(key, target, propertyKey);
            }),

            // Decorator factory: Reflect.metadata(key, value) returns a decorator
            "metadata" => new BuiltInMethod("metadata", 2, (interpreter, _, args) =>
            {
                var key = args[0]?.ToString() ?? "";
                var value = args[1];

                // Return a decorator function that defines the metadata
                return new MetadataDecorator(key, value);
            }),

            // --- Standard ES2015 Reflect API ---

            "has" => new BuiltInMethod("has", 2, (_, _, args) =>
            {
                var target = args[0] ?? throw new Exception("Runtime Error: Reflect.has requires a target object.");
                var propertyKey = args[1]?.ToString() ?? "";
                return target switch
                {
                    SharpTSObject obj => obj.HasProperty(propertyKey),
                    SharpTSInstance inst => inst.HasProperty(propertyKey),
                    SharpTSArray arr => propertyKey == "length"
                        || (int.TryParse(propertyKey, out var idx) && idx >= 0 && idx < arr.Length),
                    Dictionary<string, object?> dict => dict.ContainsKey(propertyKey),
                    _ => false
                };
            }),

            "deleteProperty" => new BuiltInMethod("deleteProperty", 2, (_, _, args) =>
            {
                var target = args[0] ?? throw new Exception("Runtime Error: Reflect.deleteProperty requires a target object.");
                var propertyKey = args[1]?.ToString() ?? "";
                switch (target)
                {
                    case SharpTSObject obj:
                        if ((obj.IsFrozen || obj.IsSealed) && obj.HasProperty(propertyKey))
                            return false;
                        obj.DeleteProperty(propertyKey);
                        return true;
                    case SharpTSInstance inst:
                        if ((inst.IsFrozen || inst.IsSealed) && inst.HasField(propertyKey))
                            return false;
                        inst.DeleteField(propertyKey);
                        return true;
                    case Dictionary<string, object?> dict:
                        if ((PropertyDescriptorStore.IsFrozen(dict) || PropertyDescriptorStore.IsSealed(dict))
                            && dict.ContainsKey(propertyKey))
                            return false;
                        dict.Remove(propertyKey);
                        return true;
                    default:
                        return true;
                }
            }),

            "get" => new BuiltInMethod("get", 2, 3, (_, _, args) =>
            {
                var target = args[0] ?? throw new Exception("Runtime Error: Reflect.get requires a target object.");
                var propertyKey = args[1]?.ToString() ?? "";
                return target switch
                {
                    SharpTSObject obj => obj.GetProperty(propertyKey),
                    SharpTSInstance inst => inst.GetRawField(propertyKey),
                    Dictionary<string, object?> dict => dict.TryGetValue(propertyKey, out var v) ? v : null,
                    _ => null
                };
            }),

            "set" => new BuiltInMethod("set", 3, 4, (_, _, args) =>
            {
                var target = args[0] ?? throw new Exception("Runtime Error: Reflect.set requires a target object.");
                var propertyKey = args[1]?.ToString() ?? "";
                var value = args[2];
                try
                {
                    switch (target)
                    {
                        case SharpTSObject obj:
                            if (obj.IsFrozen) return false;
                            obj.SetProperty(propertyKey, value);
                            return true;
                        case SharpTSInstance inst:
                            if (inst.IsFrozen) return false;
                            inst.SetRawField(propertyKey, value);
                            return true;
                        case Dictionary<string, object?> dict:
                            if (PropertyDescriptorStore.IsFrozen(dict)) return false;
                            dict[propertyKey] = value;
                            return true;
                        default:
                            return false;
                    }
                }
                catch
                {
                    return false;
                }
            }),

            "getPrototypeOf" => new BuiltInMethod("getPrototypeOf", 1, (_, _, args) =>
            {
                var target = args[0] ?? throw new Exception("Runtime Error: Reflect.getPrototypeOf requires a target object.");
                return target switch
                {
                    SharpTSObject obj => obj.Prototype,
                    SharpTSInstance inst => inst.Prototype,
                    Dictionary<string, object?> dict => PropertyDescriptorStore.GetPrototype(dict),
                    _ => null
                };
            }),

            "setPrototypeOf" => new BuiltInMethod("setPrototypeOf", 2, (_, _, args) =>
            {
                var target = args[0] ?? throw new Exception("Runtime Error: Reflect.setPrototypeOf requires a target object.");
                var proto = args[1];
                try
                {
                    switch (target)
                    {
                        case SharpTSObject obj:
                            if (!obj.IsExtensible) return false;
                            obj.Prototype = proto;
                            return true;
                        case Dictionary<string, object?> dict:
                            if (!PropertyDescriptorStore.IsExtensible(dict)) return false;
                            PropertyDescriptorStore.SetPrototype(dict, proto);
                            return true;
                        default:
                            return false;
                    }
                }
                catch
                {
                    return false;
                }
            }),

            "isExtensible" => new BuiltInMethod("isExtensible", 1, (_, _, args) =>
            {
                var target = args[0] ?? throw new Exception("Runtime Error: Reflect.isExtensible requires a target object.");
                return target switch
                {
                    SharpTSObject obj => obj.IsExtensible,
                    SharpTSInstance inst => inst.IsExtensible,
                    SharpTSArray arr => arr.IsExtensible,
                    Dictionary<string, object?> dict => PropertyDescriptorStore.IsExtensible(dict),
                    _ => false
                };
            }),

            "preventExtensions" => new BuiltInMethod("preventExtensions", 1, (_, _, args) =>
            {
                var target = args[0] ?? throw new Exception("Runtime Error: Reflect.preventExtensions requires a target object.");
                switch (target)
                {
                    case SharpTSObject obj:
                        obj.PreventExtensions();
                        break;
                    case SharpTSInstance inst:
                        inst.PreventExtensions();
                        break;
                    case SharpTSArray arr:
                        arr.PreventExtensions();
                        break;
                    case Dictionary<string, object?> dict:
                        PropertyDescriptorStore.PreventExtensions(dict);
                        break;
                }
                return true;
            }),

            "getOwnPropertyDescriptor" => new BuiltInMethod("getOwnPropertyDescriptor", 2, (_, _, args) =>
            {
                var target = args[0] ?? throw new Exception("Runtime Error: Reflect.getOwnPropertyDescriptor requires a target object.");
                var propertyKey = args[1]?.ToString() ?? "";
                // Delegate to ObjectBuiltIns which handles all target types
                return ObjectBuiltIns.RuntimeGetOwnPropertyDescriptor(target, propertyKey);
            }),

            "defineProperty" => new BuiltInMethod("defineProperty", 3, (_, _, args) =>
            {
                var target = args[0] ?? throw new Exception("Runtime Error: Reflect.defineProperty requires a target object.");
                var propertyKey = args[1]?.ToString() ?? "";
                var descriptorArg = args[2] ?? throw new Exception("TypeError: Property description must be an object");
                try
                {
                    SharpTSPropertyDescriptor descriptor = SharpTSPropertyDescriptor.FromAnyObject(descriptorArg);
                    switch (target)
                    {
                        case SharpTSObject obj:
                            return obj.DefineProperty(propertyKey, descriptor);
                        case SharpTSInstance inst:
                            return inst.DefineProperty(propertyKey, descriptor);
                        case SharpTSArray arr:
                            return arr.DefineProperty(propertyKey, descriptor);
                        default:
                            return false;
                    }
                }
                catch
                {
                    return false;
                }
            }),

            "ownKeys" => new BuiltInMethod("ownKeys", 1, (_, _, args) =>
            {
                var target = args[0] ?? throw new Exception("Runtime Error: Reflect.ownKeys requires a target object.");
                List<object?> keys = [];
                switch (target)
                {
                    case SharpTSObject obj:
                        keys.AddRange(obj.Fields.Keys.Select(k => (object?)k));
                        keys.AddRange(obj.GetSymbolPropertyNames().Select(s => (object?)s));
                        break;
                    case SharpTSInstance inst:
                        keys.AddRange(inst.GetFieldNames().Select(k => (object?)k));
                        keys.AddRange(inst.GetSymbolPropertyNames().Select(s => (object?)s));
                        break;
                    case Dictionary<string, object?> dict:
                        keys.AddRange(dict.Keys.Select(k => (object?)k));
                        keys.AddRange(PropertyDescriptorStore.GetSymbolKeys(dict).Select(s => (object?)s));
                        break;
                }
                return new SharpTSArray(keys);
            }),

            "apply" => new BuiltInMethod("apply", 3, (interpreter, _, args) =>
            {
                var target = args[0] as ISharpTSCallable
                    ?? throw new Exception("Runtime Error: Reflect.apply requires a function target.");
                var thisArg = args[1];
                var argsList = args[2];

                List<object?> callArgs;
                if (argsList == null)
                {
                    callArgs = [];
                }
                else if (argsList is SharpTSArray tsArray)
                {
                    callArgs = new List<object?>(tsArray);
                }
                else if (argsList is List<object?> list)
                {
                    callArgs = new List<object?>(list);
                }
                else
                {
                    throw new Exception("Runtime Error: Reflect.apply third argument must be an array-like object.");
                }

                // Invoke with this binding
                if (target is SharpTSFunction fn)
                {
                    var bound = new BoundFunction(fn, thisArg, []);
                    return bound.CallBoxed(interpreter, callArgs);
                }
                if (target is SharpTSArrowFunction arrow && arrow.HasOwnThis)
                {
                    var bound = arrow.Bind(thisArg!);
                    return bound.CallBoxed(interpreter, callArgs);
                }
                return target.CallBoxed(interpreter, callArgs);
            }),

            "construct" => new BuiltInMethod("construct", 2, 3, (interpreter, _, args) =>
            {
                var target = args[0] ?? throw new ThrowException(new SharpTSTypeError(
                    "Reflect.construct called on non-constructor"));
                var argsList = args[1];

                // ECMA-262 28.1.2: validate newTarget is a constructor when
                // explicitly supplied. Test262's `isConstructor(f)` harness
                // helper relies on this — it calls
                // `Reflect.construct(function(){}, [], f)` and checks for a
                // throw when f isn't constructable.
                if (args.Count > 2 && args[2] is not null)
                {
                    var newTarget = args[2];
                    if (IsNotConstructor(newTarget))
                        throw new ThrowException(new SharpTSTypeError(
                            "Reflect.construct newTarget is not a constructor"));
                }

                List<object?> callArgs;
                if (argsList == null)
                {
                    callArgs = [];
                }
                else if (argsList is SharpTSArray tsArray)
                {
                    callArgs = new List<object?>(tsArray);
                }
                else if (argsList is List<object?> list)
                {
                    callArgs = new List<object?>(list);
                }
                else
                {
                    throw new ThrowException(new SharpTSTypeError(
                        "Reflect.construct second argument must be an array-like object"));
                }

                if (IsNotConstructor(target))
                    throw new ThrowException(new SharpTSTypeError(
                        "Reflect.construct target is not a constructor"));

                if (target is SharpTSClass cls)
                {
                    return cls.CallBoxed(interpreter, callArgs);
                }
                if (target is ISharpTSCallable callable)
                {
                    return callable.CallBoxed(interpreter, callArgs);
                }
                throw new ThrowException(new SharpTSTypeError(
                    "Reflect.construct target is not a constructor"));
            }),

            _ => null
        };
    }

    /// <summary>
    /// True when <paramref name="value"/> isn't a constructor — used by
    /// Reflect.construct to validate target/newTarget per ECMA-262, and by
    /// Test262's <c>isConstructor(f)</c> harness helper which calls
    /// <c>Reflect.construct(function(){}, [], f)</c> and treats a throw as
    /// "not a constructor".
    /// </summary>
    private static bool IsNotConstructor(object? value)
    {
        // Direct rejections — methods/wrappers that are clearly callable but
        // aren't constructors per spec.
        if (value is SharpTS.Runtime.Types.ArrayPrototypeMethodWrapper
            or SharpTS.Runtime.Types.StringPrototypeMethodWrapper
            or SharpTS.Runtime.Types.NumberPrototypeMethodWrapper
            or SharpTS.Runtime.Types.BooleanPrototypeMethodWrapper
            or SharpTS.Runtime.Types.SharpTSObjectUnboundMethod
            or BoundFunction)
            return true;
        // Anything else that's not callable can't be a constructor.
        return value is not ISharpTSCallable;
    }

    /// <summary>
    /// A decorator that defines metadata on the target.
    /// Used with Reflect.metadata(key, value) factory.
    /// </summary>
    private class MetadataDecorator(string key, object? value) : ISharpTSCallable
    {
        public int Arity() => 2; // For Stage 3: (value, context), or Legacy: (target, propertyKey?, descriptor?)

        public object? Call(Interpreter interpreter, List<object?> arguments)
        {
            // This decorator just defines metadata on the target
            // Works for both Legacy and Stage 3 decorators
            if (arguments.Count >= 1 && arguments[0] != null)
            {
                var target = arguments[0]!;
                string? propertyKey = null;

                // For method/property decorators, second arg is property key (Legacy)
                // or context object (Stage 3)
                if (arguments.Count >= 2 && arguments[1] is string propKey)
                {
                    propertyKey = propKey;
                }
                else if (arguments.Count >= 2 && arguments[1] is SharpTSObject context)
                {
                    // Stage 3 context object has 'name' property
                    var name = context.GetProperty("name");
                    if (name is string contextName)
                    {
                        propertyKey = contextName;
                    }
                }

                ReflectMetadataStore.Instance.DefineMetadata(key, value, target, propertyKey);
            }

            // Return undefined (decorators that don't modify return void/undefined)
            return null;
        }

        public RuntimeValue Call(Interpreter interpreter, ReadOnlySpan<RuntimeValue> arguments)
            => RuntimeValue.FromBoxed(Call(interpreter, CallableInterop.ToBoxedList(arguments)));
    }
}
