using SharpTS.Runtime;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of plain object literals.
/// </summary>
/// <remarks>
/// Represents <c>{ key: value }</c> object literals (not class instances).
/// Stores fields in a dictionary with dynamic Get/Set access. Used for structural
/// typing, object destructuring, and <c>Object.keys()</c> support.
/// Unlike <see cref="SharpTSInstance"/>, plain objects have no associated class or methods.
/// </remarks>
/// <seealso cref="SharpTSInstance"/>
/// <seealso cref="SharpTSArray"/>
public class SharpTSObject(Dictionary<string, object?> fields) : ISharpTSPropertyAccessor, ITypeCategorized
{
    private readonly Dictionary<string, object?> _fields = fields;
    private readonly Dictionary<SharpTSSymbol, object?> _symbolFields = new();
    private Dictionary<string, ISharpTSCallable>? _getters;
    private Dictionary<string, ISharpTSCallable>? _setters;
    private Dictionary<string, PropertyDescriptorFlags>? _descriptors;

    /// <inheritdoc />
    public virtual TypeCategory RuntimeCategory => TypeCategory.Record;

    /// <summary>
    /// Whether this object is frozen (no property additions, removals, or modifications).
    /// </summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Whether this object is sealed (no property additions or removals, but modifications allowed).
    /// </summary>
    public bool IsSealed { get; private set; }

    /// <summary>
    /// Whether this object is extensible (can have new properties added).
    /// </summary>
    public bool IsExtensible { get; private set; } = true;

    /// <summary>
    /// The prototype object (set via Object.create or Object.setPrototypeOf).
    /// </summary>
    public object? Prototype { get; set; }

    /// <summary>
    /// Freezes this object, preventing any property changes.
    /// </summary>
    public void Freeze()
    {
        IsFrozen = true;
        IsSealed = true; // Frozen implies sealed
        IsExtensible = false; // Frozen implies non-extensible
    }

    /// <summary>
    /// Seals this object, preventing property additions/removals but allowing modifications.
    /// </summary>
    public void Seal()
    {
        IsSealed = true;
        IsExtensible = false;
    }

    /// <summary>
    /// Prevents adding new properties to this object.
    /// </summary>
    public void PreventExtensions()
    {
        IsExtensible = false;
    }

    /// <summary>
    /// Gets all symbol-keyed property names.
    /// </summary>
    public IEnumerable<SharpTSSymbol> GetSymbolPropertyNames()
    {
        return _symbolFields.Keys;
    }

    /// <summary>
    /// Expose fields for Object.keys() and object rest patterns
    /// </summary>
    public IReadOnlyDictionary<string, object?> Fields => _fields;

    /// <inheritdoc />
    public IEnumerable<string> PropertyNames => _fields.Keys;

    /// <summary>
    /// Names of accessor (getter-defined) properties on this object. Disjoint from
    /// <see cref="Fields"/>.Keys because <see cref="DefineProperty"/> removes any
    /// data-property entry when installing an accessor. Callers that need the full
    /// own-property name set (re-exports from CJS modules whose named bindings are
    /// Babel-style getters) must union this with <see cref="Fields"/>.Keys.
    /// </summary>
    public IEnumerable<string> AccessorPropertyNames =>
        _getters?.Keys ?? Enumerable.Empty<string>();

    /// <inheritdoc />
    public object? GetProperty(string name)
    {
        if (_fields.TryGetValue(name, out object? value))
        {
            return value;
        }
        // Non-existent properties return undefined, not null (JavaScript semantics)
        return SharpTSUndefined.Instance;
    }

    public RuntimeValue GetPropertyRV(string name) => RuntimeValue.FromBoxed(GetProperty(name));

    /// <summary>
    /// Reflection-friendly field accessor used by the compiled-mode
    /// <c>GetFieldsProperty</c> helper. Its <c>GetMember(string)</c> reflection
    /// fallback (<c>Compilation/RuntimeEmitter.Objects.Properties.cs</c>) calls
    /// this method by name on any object whose fields it can't otherwise
    /// resolve. Exposing it lets compiled code read properties off an
    /// interpreter-constructed <see cref="SharpTSObject"/> (e.g., iterator
    /// result objects like <c>{value, done}</c>) without a type-specific
    /// dispatch branch.
    /// </summary>
    public object? GetMember(string name)
    {
        return _fields.TryGetValue(name, out var value) ? value : SharpTSUndefined.Instance;
    }

    /// <inheritdoc />
    public void SetProperty(string name, object? value)
    {
        if (IsFrozen)
        {
            // Frozen objects silently ignore property modifications (JavaScript behavior in non-strict mode)
            SloppyModeWarnings.Warn("write to frozen", $"Assignment to frozen object property '{name}' ignored");
            return;
        }

        // Check for getter-only properties (has getter but no setter)
        if (HasGetter(name) && !HasSetter(name))
        {
            SloppyModeWarnings.Warn("write to getter-only", $"Assignment to getter-only property '{name}' ignored");
            return;
        }

        bool exists = _fields.ContainsKey(name) || HasGetter(name);
        if (!IsExtensible && !exists)
        {
            // Non-extensible objects silently ignore new property additions
            SloppyModeWarnings.Warn("add to non-extensible", $"Property addition to non-extensible object '{name}' ignored");
            return;
        }

        // Check writable flag for properties defined via defineProperty
        if (exists && _descriptors?.TryGetValue(name, out var flags) == true && flags.HasExplicitDescriptor && !flags.Writable)
        {
            SloppyModeWarnings.Warn("write to non-writable", $"Assignment to non-writable property '{name}' ignored");
            return;
        }

        _fields[name] = value;
    }

    /// <summary>
    /// Sets a property value with strict mode behavior.
    /// In strict mode, throws TypeError for modifications to frozen objects, new properties on sealed objects,
    /// or assignments to getter-only properties.
    /// </summary>
    /// <param name="name">The property name to set.</param>
    /// <param name="value">The value to set.</param>
    /// <param name="strictMode">Whether strict mode is enabled.</param>
    public void SetPropertyStrict(string name, object? value, bool strictMode)
    {
        if (IsFrozen)
        {
            if (strictMode)
            {
                throw StrictModeErrors.TypeError($"Cannot assign to read only property '{name}' of object");
            }
            SloppyModeWarnings.Warn("write to frozen", $"Assignment to frozen object property '{name}' ignored");
            return;
        }

        // Check for getter-only properties (has getter but no setter)
        if (HasGetter(name) && !HasSetter(name))
        {
            if (strictMode)
            {
                throw StrictModeErrors.TypeError($"Cannot set property '{name}' which has only a getter");
            }
            SloppyModeWarnings.Warn("write to getter-only", $"Assignment to getter-only property '{name}' ignored");
            return;
        }

        bool exists = _fields.ContainsKey(name) || HasGetter(name);
        if (!IsExtensible && !exists)
        {
            if (strictMode)
            {
                throw StrictModeErrors.TypeError($"Cannot add property '{name}' to a non-extensible object");
            }
            SloppyModeWarnings.Warn("add to non-extensible", $"Property addition to non-extensible object '{name}' ignored");
            return;
        }

        // Check writable flag for properties defined via defineProperty
        if (exists && _descriptors?.TryGetValue(name, out var flags) == true && flags.HasExplicitDescriptor && !flags.Writable)
        {
            if (strictMode)
            {
                throw StrictModeErrors.TypeError($"Cannot assign to read only property '{name}'");
            }
            SloppyModeWarnings.Warn("write to non-writable", $"Assignment to non-writable property '{name}' ignored");
            return;
        }

        _fields[name] = value;
    }

    /// <summary>
    /// Removes a property by name. Respects frozen/sealed state.
    /// </summary>
    public bool DeleteProperty(string name)
    {
        if (IsFrozen || IsSealed)
        {
            // Frozen and sealed objects silently ignore property deletions
            SloppyModeWarnings.Warn("delete from frozen/sealed", $"Delete from frozen/sealed object property '{name}' returns false");
            return false;
        }
        return _fields.Remove(name);
    }

    /// <summary>
    /// Removes a property by name with strict mode behavior.
    /// In strict mode, throws TypeError for deletions on frozen/sealed objects.
    /// </summary>
    /// <param name="name">The property name to delete.</param>
    /// <param name="strictMode">Whether strict mode is enabled.</param>
    /// <returns>True if the property was deleted, false otherwise.</returns>
    public bool DeletePropertyStrict(string name, bool strictMode)
    {
        if (IsFrozen || IsSealed)
        {
            if (strictMode)
            {
                throw StrictModeErrors.TypeError($"Cannot delete property '{name}' of a frozen or sealed object");
            }
            SloppyModeWarnings.Warn("delete from frozen/sealed", $"Delete from frozen/sealed object property '{name}' returns false");
            return false;
        }
        return _fields.Remove(name);
    }

    public bool HasProperty(string name)
    {
        return _fields.ContainsKey(name) || (_getters?.ContainsKey(name) ?? false);
    }

    /// <summary>
    /// Defines a getter for a property.
    /// </summary>
    public void DefineGetter(string name, ISharpTSCallable getter)
    {
        _getters ??= new Dictionary<string, ISharpTSCallable>();
        _getters[name] = getter;
    }

    /// <summary>
    /// Defines a setter for a property.
    /// </summary>
    public void DefineSetter(string name, ISharpTSCallable setter)
    {
        _setters ??= new Dictionary<string, ISharpTSCallable>();
        _setters[name] = setter;
    }

    /// <summary>
    /// Checks if a property has a getter.
    /// </summary>
    public bool HasGetter(string name)
    {
        return _getters?.ContainsKey(name) ?? false;
    }

    /// <summary>
    /// Checks if a property has a setter.
    /// </summary>
    public bool HasSetter(string name)
    {
        return _setters?.ContainsKey(name) ?? false;
    }

    /// <summary>
    /// Gets the getter function for a property, or null if none.
    /// </summary>
    public ISharpTSCallable? GetGetter(string name)
    {
        return _getters?.GetValueOrDefault(name);
    }

    /// <summary>
    /// Gets the setter function for a property, or null if none.
    /// </summary>
    public ISharpTSCallable? GetSetter(string name)
    {
        return _setters?.GetValueOrDefault(name);
    }

    /// <summary>
    /// Gets a value by symbol key.
    /// </summary>
    public object? GetBySymbol(SharpTSSymbol symbol)
    {
        return _symbolFields.TryGetValue(symbol, out var value) ? value : null;
    }

    /// <summary>
    /// Sets a value by symbol key.
    /// </summary>
    public void SetBySymbol(SharpTSSymbol symbol, object? value)
    {
        if (IsFrozen)
        {
            return;
        }

        bool exists = _symbolFields.ContainsKey(symbol);
        if (!IsExtensible && !exists)
        {
            return;
        }

        _symbolFields[symbol] = value;
    }

    /// <summary>
    /// Sets a value by symbol key with strict mode behavior.
    /// </summary>
    public void SetBySymbolStrict(SharpTSSymbol symbol, object? value, bool strictMode)
    {
        if (IsFrozen)
        {
            if (strictMode)
            {
                throw new Exception($"TypeError: Cannot assign to read only symbol property of object");
            }
            return;
        }

        bool exists = _symbolFields.ContainsKey(symbol);
        if (!IsExtensible && !exists)
        {
            if (strictMode)
            {
                throw new Exception($"TypeError: Cannot add symbol property to a non-extensible object");
            }
            return;
        }

        _symbolFields[symbol] = value;
    }

    /// <summary>
    /// Checks if the object has a property with the given symbol key.
    /// </summary>
    public bool HasSymbolProperty(SharpTSSymbol symbol)
    {
        return _symbolFields.ContainsKey(symbol);
    }

    /// <summary>
    /// Removes a property by symbol key. Respects frozen/sealed state.
    /// </summary>
    public bool DeleteBySymbol(SharpTSSymbol symbol)
    {
        if (IsFrozen || IsSealed)
        {
            // Frozen and sealed objects silently ignore property deletions
            return false;
        }
        return _symbolFields.Remove(symbol);
    }

    /// <summary>
    /// Removes a property by symbol key with strict mode behavior.
    /// In strict mode, throws TypeError for deletions on frozen/sealed objects.
    /// </summary>
    public bool DeleteBySymbolStrict(SharpTSSymbol symbol, bool strictMode)
    {
        if (IsFrozen || IsSealed)
        {
            if (strictMode)
            {
                throw new Exception($"TypeError: Cannot delete symbol property of a frozen or sealed object");
            }
            return false;
        }
        return _symbolFields.Remove(symbol);
    }

    /// <summary>
    /// Defines or modifies a property with the given descriptor.
    /// Returns true on success, false if the operation is not allowed.
    /// </summary>
    public bool DefineProperty(string name, SharpTSPropertyDescriptor descriptor)
    {
        // Get existing descriptor flags if any
        bool hasExisting = _fields.ContainsKey(name) || HasGetter(name) || HasSetter(name);
        PropertyDescriptorFlags existingFlags = default;

        if (hasExisting && _descriptors?.TryGetValue(name, out existingFlags) != true)
        {
            // Existing property without explicit descriptor - use defaults
            existingFlags = PropertyDescriptorFlags.Default;
        }

        // Check if we can modify the property
        if (hasExisting && existingFlags.HasExplicitDescriptor && !existingFlags.Configurable)
        {
            // Non-configurable property - limited modifications allowed
            // Can only change value if writable, cannot change other attributes
            if (descriptor.Get != null || descriptor.Set != null)
            {
                // Cannot change accessor on non-configurable property
                return false;
            }
            if (descriptor.Writable != existingFlags.Writable ||
                descriptor.Enumerable != existingFlags.Enumerable ||
                descriptor.Configurable != existingFlags.Configurable)
            {
                // Cannot change attributes on non-configurable property
                return false;
            }
            if (!existingFlags.Writable && descriptor.Value != null)
            {
                // Cannot change value of non-writable, non-configurable property
                var currentValue = _fields.TryGetValue(name, out var v) ? v : null;
                if (!ReferenceEquals(currentValue, descriptor.Value) &&
                    (currentValue == null || !currentValue.Equals(descriptor.Value)))
                {
                    return false;
                }
            }
        }

        // Check sealed/frozen/extensible state
        if (IsFrozen)
        {
            return false;
        }
        if (!IsExtensible && !hasExisting)
        {
            return false;
        }

        // Store the descriptor flags
        _descriptors ??= new Dictionary<string, PropertyDescriptorFlags>();
        _descriptors[name] = PropertyDescriptorFlags.ForDefineProperty(
            descriptor.Writable,
            descriptor.Enumerable,
            descriptor.Configurable
        );

        // Apply the descriptor
        if (descriptor.Get != null || descriptor.Set != null)
        {
            // Accessor property - remove any data property value
            _fields.Remove(name);

            if (descriptor.Get != null)
            {
                DefineGetter(name, descriptor.Get);
            }
            else
            {
                _getters?.Remove(name);
            }

            if (descriptor.Set != null)
            {
                DefineSetter(name, descriptor.Set);
            }
            else
            {
                _setters?.Remove(name);
            }
        }
        else
        {
            // Data property - remove any accessor
            _getters?.Remove(name);
            _setters?.Remove(name);

            // Only set value if provided in descriptor
            if (descriptor.Value != null || !hasExisting)
            {
                _fields[name] = descriptor.Value;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the property descriptor for the given property name.
    /// Returns null if the property doesn't exist.
    /// </summary>
    public SharpTSPropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        bool hasDataProperty = _fields.TryGetValue(name, out var fieldValue);
        bool hasGetter = HasGetter(name);
        bool hasSetter = HasSetter(name);

        if (!hasDataProperty && !hasGetter && !hasSetter)
        {
            return null;
        }

        // Get descriptor flags (defaults if not explicitly set)
        PropertyDescriptorFlags flags = default;
        if (_descriptors?.TryGetValue(name, out flags) != true)
        {
            flags = PropertyDescriptorFlags.Default;
        }

        if (hasGetter || hasSetter)
        {
            // Accessor property
            return new SharpTSPropertyDescriptor
            {
                Get = GetGetter(name),
                Set = GetSetter(name),
                Enumerable = flags.Enumerable,
                Configurable = flags.Configurable
            };
        }
        else
        {
            // Data property
            return new SharpTSPropertyDescriptor
            {
                Value = fieldValue,
                Writable = flags.Writable,
                Enumerable = flags.Enumerable,
                Configurable = flags.Configurable
            };
        }
    }

    /// <summary>
    /// Gets the descriptor flags for a property, or default flags if not explicitly set.
    /// </summary>
    public PropertyDescriptorFlags GetPropertyFlags(string name)
    {
        if (_descriptors?.TryGetValue(name, out var flags) == true)
        {
            return flags;
        }
        return PropertyDescriptorFlags.Default;
    }

    public override string ToString() => $"{{ {string.Join(", ", _fields.Select(f => $"{f.Key}: {f.Value}"))} }}";
}
