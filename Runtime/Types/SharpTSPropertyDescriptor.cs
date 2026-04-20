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

        var value = obj.GetProperty("value");
        if (value != null)
        {
            descriptor.Value = value;
        }

        var getter = obj.GetProperty("get");
        if (getter is ISharpTSCallable getterFn)
        {
            descriptor.Get = getterFn;
        }

        var setter = obj.GetProperty("set");
        if (setter is ISharpTSCallable setterFn)
        {
            descriptor.Set = setterFn;
        }

        var writable = obj.GetProperty("writable");
        if (writable is bool w)
        {
            descriptor.Writable = w;
        }

        var enumerable = obj.GetProperty("enumerable");
        if (enumerable is bool e)
        {
            descriptor.Enumerable = e;
        }

        var configurable = obj.GetProperty("configurable");
        if (configurable is bool c)
        {
            descriptor.Configurable = c;
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

            var value = GetProp("value");
            if (value != null)
            {
                descriptor.Value = value;
            }

            var getter = GetProp("get");
            if (getter is ISharpTSCallable getterFn)
            {
                descriptor.Get = getterFn;
            }

            var setter = GetProp("set");
            if (setter is ISharpTSCallable setterFn)
            {
                descriptor.Set = setterFn;
            }

            var writable = GetProp("writable");
            if (writable is bool w)
            {
                descriptor.Writable = w;
            }

            var enumerable = GetProp("enumerable");
            if (enumerable is bool e)
            {
                descriptor.Enumerable = e;
            }

            var configurable = GetProp("configurable");
            if (configurable is bool c)
            {
                descriptor.Configurable = c;
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
        }

        if (dict.TryGetValue("get", out var getter) && getter is ISharpTSCallable getterFn)
        {
            descriptor.Get = getterFn;
        }

        if (dict.TryGetValue("set", out var setter) && setter is ISharpTSCallable setterFn)
        {
            descriptor.Set = setterFn;
        }

        if (dict.TryGetValue("writable", out var writable) && writable is bool w)
        {
            descriptor.Writable = w;
        }

        if (dict.TryGetValue("enumerable", out var enumerable) && enumerable is bool e)
        {
            descriptor.Enumerable = e;
        }

        if (dict.TryGetValue("configurable", out var configurable) && configurable is bool c)
        {
            descriptor.Configurable = c;
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
        }

        if (dict.Contains("get") && dict["get"] is ISharpTSCallable getterFn)
        {
            descriptor.Get = getterFn;
        }

        if (dict.Contains("set") && dict["set"] is ISharpTSCallable setterFn)
        {
            descriptor.Set = setterFn;
        }

        if (dict.Contains("writable") && dict["writable"] is bool w)
        {
            descriptor.Writable = w;
        }

        if (dict.Contains("enumerable") && dict["enumerable"] is bool e)
        {
            descriptor.Enumerable = e;
        }

        if (dict.Contains("configurable") && dict["configurable"] is bool c)
        {
            descriptor.Configurable = c;
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
