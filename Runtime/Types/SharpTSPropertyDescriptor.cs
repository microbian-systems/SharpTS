namespace SharpTS.Runtime.Types;

/// <summary>
/// Represents a JavaScript-style property descriptor for method/accessor decorators.
/// Used in Legacy (Stage 2) decorators for method and accessor decoration.
/// </summary>
public class SharpTSPropertyDescriptor
{
    /// <summary>Value for data properties (the method function)</summary>
    public object? Value { get; set; }

    /// <summary>Getter function for accessor properties</summary>
    public ISharpTSCallable? Get { get; set; }

    /// <summary>Setter function for accessor properties</summary>
    public ISharpTSCallable? Set { get; set; }

    /// <summary>Whether the property value can be changed.</summary>
    /// <remarks>
    /// Default is false to match ECMA-262 6.2.5.1 CompletePropertyDescriptor, which
    /// is the context used when parsing a user-supplied descriptor for
    /// Object.defineProperty / Reflect.defineProperty. Sites that want "data
    /// property" semantics (Writable=true, etc.) must pass the flag explicitly.
    /// </remarks>
    public bool Writable { get; set; } = false;

    /// <summary>Whether the property shows up in enumeration.</summary>
    public bool Enumerable { get; set; } = false;

    /// <summary>Whether the property can be deleted or changed.</summary>
    public bool Configurable { get; set; } = false;

    /// <summary>
    /// Presence flags distinguishing "field omitted" from "field present and
    /// null/undefined". ECMA-262 §6.2.5.6 treats an absent descriptor field
    /// differently from one explicitly set to undefined (e.g. an attribute-only
    /// redefine preserves the existing value, whereas <c>{ value: undefined }</c>
    /// overwrites it). The flattened Value/Get/Set/bool fields lose that
    /// distinction; these flags record whether each field was actually specified
    /// by the source descriptor object. See #801.
    /// </summary>
    public bool HasValue { get; set; }
    public bool HasGet { get; set; }
    public bool HasSet { get; set; }
    public bool HasWritable { get; set; }
    public bool HasEnumerable { get; set; }
    public bool HasConfigurable { get; set; }

    public SharpTSPropertyDescriptor() { }

    public SharpTSPropertyDescriptor(
        object? value = null,
        ISharpTSCallable? getter = null,
        ISharpTSCallable? setter = null,
        bool writable = false,
        bool enumerable = false,
        bool configurable = false)
    {
        Value = value;
        Get = getter;
        Set = setter;
        Writable = writable;
        Enumerable = enumerable;
        Configurable = configurable;
        // Explicitly-constructed descriptors (ForMethod/ForGetter/…) are full
        // descriptors: record which fields were supplied so the value-write gate in
        // SharpTSObject.DefineProperty treats them as specified rather than omitted (#801).
        HasValue = value != null;
        HasGet = getter != null;
        HasSet = setter != null;
        HasWritable = true;
        HasEnumerable = true;
        HasConfigurable = true;
    }

    /// <summary>
    /// Converts this descriptor to a SharpTS object for passing to decorators.
    /// </summary>
    public SharpTSObject ToObject()
    {
        var obj = new SharpTSObject(new Dictionary<string, object?>());

        if (Value != null)
        {
            obj.SetProperty("value", Value);
            obj.SetProperty("writable", Writable);
        }

        if (Get != null)
        {
            obj.SetProperty("get", Get);
        }

        if (Set != null)
        {
            obj.SetProperty("set", Set);
        }

        obj.SetProperty("enumerable", Enumerable);
        obj.SetProperty("configurable", Configurable);

        return obj;
    }

    /// <summary>
    /// Creates a PropertyDescriptor from a SharpTS object returned by a decorator.
    /// </summary>
    public static SharpTSPropertyDescriptor FromObject(SharpTSObject obj)
    {
        var descriptor = new SharpTSPropertyDescriptor();

        // Own-only fast path. Presence is detected via HasProperty/HasSetter so an
        // explicit `value: undefined` (or a setter-only `value` accessor) is recorded
        // as specified rather than dropped, distinguishing "omitted" from "undefined"
        // (#801). The interpreter-aware pass in ObjectBuiltIns re-derives these
        // prototype-aware (walking the chain + ToBoolean) and overrides as needed.
        if (obj.HasProperty("value") || obj.HasSetter("value"))
        {
            descriptor.Value = obj.GetProperty("value");
            descriptor.HasValue = true;
        }

        if (obj.HasProperty("get") || obj.HasSetter("get"))
        {
            descriptor.HasGet = true;
            if (obj.GetProperty("get") is ISharpTSCallable getterFn)
            {
                descriptor.Get = getterFn;
            }
        }

        if (obj.HasProperty("set") || obj.HasSetter("set"))
        {
            descriptor.HasSet = true;
            if (obj.GetProperty("set") is ISharpTSCallable setterFn)
            {
                descriptor.Set = setterFn;
            }
        }

        if (obj.HasProperty("writable"))
        {
            descriptor.HasWritable = true;
            if (obj.GetProperty("writable") is bool w) descriptor.Writable = w;
        }

        if (obj.HasProperty("enumerable"))
        {
            descriptor.HasEnumerable = true;
            if (obj.GetProperty("enumerable") is bool e) descriptor.Enumerable = e;
        }

        if (obj.HasProperty("configurable"))
        {
            descriptor.HasConfigurable = true;
            if (obj.GetProperty("configurable") is bool c) descriptor.Configurable = c;
        }

        return descriptor;
    }

    /// <summary>
    /// Creates a PropertyDescriptor from any object with GetProperty method.
    /// Used for compiled code where the object may be a $Object type.
    /// </summary>
    public static SharpTSPropertyDescriptor FromAnyObject(object obj)
    {
        if (obj is SharpTSObject tsObj)
        {
            return FromObject(tsObj);
        }

        // Handle Dictionary<string, object?> (compiled object literals)
        if (obj is Dictionary<string, object?> dict)
        {
            return FromDictionary(dict);
        }

        // Handle IDictionary (fallback)
        if (obj is System.Collections.IDictionary idict)
        {
            return FromIDictionary(idict);
        }

        // For compiled $Object type, use reflection to get properties
        var descriptor = new SharpTSPropertyDescriptor();
        var type = obj.GetType();

        // Try to get the GetProperty method
        var getPropertyMethod = type.GetMethod("GetProperty", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, [typeof(string)], null);

        if (getPropertyMethod != null)
        {
            object? GetProp(string name) => getPropertyMethod.Invoke(obj, [name]);

            // Probe presence via HasProperty(string) when the type exposes it, so an
            // explicit `value: undefined` is distinguished from an omitted value (#801).
            var hasPropertyMethod = type.GetMethod("HasProperty",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null, [typeof(string)], null);
            bool HasProp(string name) => hasPropertyMethod?.Invoke(obj, [name]) is bool b && b;

            if (HasProp("value"))
            {
                descriptor.Value = GetProp("value");
                descriptor.HasValue = true;
            }

            if (HasProp("get"))
            {
                descriptor.HasGet = true;
                if (GetProp("get") is ISharpTSCallable getterFn) descriptor.Get = getterFn;
            }

            if (HasProp("set"))
            {
                descriptor.HasSet = true;
                if (GetProp("set") is ISharpTSCallable setterFn) descriptor.Set = setterFn;
            }

            if (HasProp("writable"))
            {
                descriptor.HasWritable = true;
                if (GetProp("writable") is bool w) descriptor.Writable = w;
            }

            if (HasProp("enumerable"))
            {
                descriptor.HasEnumerable = true;
                if (GetProp("enumerable") is bool e) descriptor.Enumerable = e;
            }

            if (HasProp("configurable"))
            {
                descriptor.HasConfigurable = true;
                if (GetProp("configurable") is bool c) descriptor.Configurable = c;
            }
        }

        return descriptor;
    }

    /// <summary>
    /// Creates a PropertyDescriptor from a Dictionary.
    /// </summary>
    private static SharpTSPropertyDescriptor FromDictionary(Dictionary<string, object?> dict)
    {
        var descriptor = new SharpTSPropertyDescriptor();

        if (dict.TryGetValue("value", out var value))
        {
            descriptor.Value = value;
            descriptor.HasValue = true;
        }

        if (dict.TryGetValue("get", out var getter))
        {
            descriptor.HasGet = true;
            if (getter is ISharpTSCallable getterFn) descriptor.Get = getterFn;
        }

        if (dict.TryGetValue("set", out var setter))
        {
            descriptor.HasSet = true;
            if (setter is ISharpTSCallable setterFn) descriptor.Set = setterFn;
        }

        if (dict.TryGetValue("writable", out var writable))
        {
            descriptor.HasWritable = true;
            if (writable is bool w) descriptor.Writable = w;
        }

        if (dict.TryGetValue("enumerable", out var enumerable))
        {
            descriptor.HasEnumerable = true;
            if (enumerable is bool e) descriptor.Enumerable = e;
        }

        if (dict.TryGetValue("configurable", out var configurable))
        {
            descriptor.HasConfigurable = true;
            if (configurable is bool c) descriptor.Configurable = c;
        }

        return descriptor;
    }

    /// <summary>
    /// Creates a PropertyDescriptor from an IDictionary.
    /// </summary>
    private static SharpTSPropertyDescriptor FromIDictionary(System.Collections.IDictionary dict)
    {
        var descriptor = new SharpTSPropertyDescriptor();

        if (dict.Contains("value"))
        {
            descriptor.Value = dict["value"];
            descriptor.HasValue = true;
        }

        if (dict.Contains("get"))
        {
            descriptor.HasGet = true;
            if (dict["get"] is ISharpTSCallable getterFn) descriptor.Get = getterFn;
        }

        if (dict.Contains("set"))
        {
            descriptor.HasSet = true;
            if (dict["set"] is ISharpTSCallable setterFn) descriptor.Set = setterFn;
        }

        if (dict.Contains("writable"))
        {
            descriptor.HasWritable = true;
            if (dict["writable"] is bool w) descriptor.Writable = w;
        }

        if (dict.Contains("enumerable"))
        {
            descriptor.HasEnumerable = true;
            if (dict["enumerable"] is bool e) descriptor.Enumerable = e;
        }

        if (dict.Contains("configurable"))
        {
            descriptor.HasConfigurable = true;
            if (dict["configurable"] is bool c) descriptor.Configurable = c;
        }

        return descriptor;
    }

    /// <summary>
    /// Creates a descriptor for a method.
    /// </summary>
    public static SharpTSPropertyDescriptor ForMethod(ISharpTSCallable method)
    {
        return new SharpTSPropertyDescriptor(
            value: method,
            writable: true,
            enumerable: false,
            configurable: true
        );
    }

    /// <summary>
    /// Creates a descriptor for a getter.
    /// </summary>
    public static SharpTSPropertyDescriptor ForGetter(ISharpTSCallable getter)
    {
        return new SharpTSPropertyDescriptor(
            getter: getter,
            enumerable: false,
            configurable: true
        );
    }

    /// <summary>
    /// Creates a descriptor for a setter.
    /// </summary>
    public static SharpTSPropertyDescriptor ForSetter(ISharpTSCallable setter)
    {
        return new SharpTSPropertyDescriptor(
            setter: setter,
            enumerable: false,
            configurable: true
        );
    }

    /// <summary>
    /// Creates a descriptor for an accessor pair.
    /// </summary>
    public static SharpTSPropertyDescriptor ForAccessor(ISharpTSCallable? getter, ISharpTSCallable? setter)
    {
        return new SharpTSPropertyDescriptor(
            getter: getter,
            setter: setter,
            enumerable: false,
            configurable: true
        );
    }
}
