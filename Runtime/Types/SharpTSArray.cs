using System.Collections;
using SharpTS.Runtime;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime wrapper for TypeScript arrays.
/// </summary>
/// <remarks>
/// Wraps a <c>Deque&lt;object?&gt;</c> to represent TypeScript arrays at runtime.
/// Uses a circular buffer for O(1) shift/unshift operations.
/// Provides indexed Get/Set access with bounds checking.
/// Used by <see cref="Interpreter"/> when evaluating array literals and array operations
/// (e.g., indexing, push, pop, map, filter).
/// <para>
/// Access API (Stage A of issue #73): callers should use <see cref="Length"/>, the
/// indexer, <see cref="GetEnumerator"/>, and the mutation helpers on this type
/// rather than reaching into <see cref="Elements"/>. <c>Elements</c> remains public
/// during the migration so call sites can move incrementally; it will become
/// private when the dual-mode (dense + sparse dictionary) backing store lands.
/// </para>
/// </remarks>
/// <seealso cref="SharpTSObject"/>
public class SharpTSArray(Deque<object?> elements) : ITypeCategorized, IReadOnlyList<object?>
{
    /// <inheritdoc />
    public TypeCategory RuntimeCategory => TypeCategory.Array;

    public Deque<object?> Elements { get; } = elements;

    /// <summary>
    /// ECMA-262 array length. Today this is identical to <see cref="Elements"/>.<c>Count</c>;
    /// once sparse storage lands (issue #73) it becomes the authoritative length and
    /// may exceed the physical slot count.
    /// </summary>
    public int Length => Elements.Count;

    /// <summary>
    /// Collection count. Same as <see cref="Length"/> — present so the runtime can detect
    /// the array size cheaply (e.g. <c>new List&lt;object?&gt;(array)</c> preallocates capacity).
    /// </summary>
    public int Count => Length;

    /// <summary>
    /// Indexed access to an existing slot. Throws <see cref="ArgumentOutOfRangeException"/>
    /// for out-of-range reads and writes — use <see cref="Get(int)"/> / <see cref="Set(int, object?)"/>
    /// for the JS-semantic variants (undefined on OOB read, extend-with-undefined on OOB write).
    /// </summary>
    public object? this[int index]
    {
        get => Elements[index];
        set => Elements[index] = value;
    }

    /// <inheritdoc />
    public IEnumerator<object?> GetEnumerator() => Elements.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => Elements.GetEnumerator();

    /// <summary>Appends an element. Does not check frozen/sealed state — callers that care must check.</summary>
    public void Add(object? value) => Elements.Add(value);

    /// <summary>Appends many elements. Does not check frozen/sealed state.</summary>
    public void AddRange(IEnumerable<object?> values) => Elements.AddRange(values);

    /// <summary>Prepends an element (O(1) via Deque).</summary>
    public void AddFirst(object? value) => Elements.AddFirst(value);

    /// <summary>Inserts at an index, shifting later elements right.</summary>
    public void Insert(int index, object? value) => Elements.Insert(index, value);

    /// <summary>Inserts many elements at an index.</summary>
    public void InsertRange(int index, IEnumerable<object?> values) => Elements.InsertRange(index, values);

    /// <summary>Removes and returns the last element.</summary>
    public object? RemoveLast() => Elements.RemoveLast();

    /// <summary>Removes and returns the first element (O(1) via Deque).</summary>
    public object? RemoveFirst() => Elements.RemoveFirst();

    /// <summary>Removes the element at the given index.</summary>
    public void RemoveAt(int index) => Elements.RemoveAt(index);

    /// <summary>Removes a contiguous range of elements.</summary>
    public void RemoveRange(int index, int count) => Elements.RemoveRange(index, count);

    /// <summary>Clears all elements.</summary>
    public void Clear() => Elements.Clear();

    /// <summary>Reverses in place.</summary>
    public void ReverseInPlace() => Elements.Reverse();

    /// <summary>Returns a new <see cref="List{T}"/> containing the given slice.</summary>
    public List<object?> GetRange(int index, int count) => Elements.GetRange(index, count);

    /// <summary>Returns the last element without removing it.</summary>
    public object? PeekLast() => Elements.PeekLast();

    /// <summary>Returns the first element without removing it.</summary>
    public object? PeekFirst() => Elements.PeekFirst();

    /// <summary>Returns true if the element is present (reference/Equals match).</summary>
    public bool ContainsElement(object? item) => Elements.Contains(item);

    /// <summary>Returns the first index of the element, or -1 if not found.</summary>
    public int IndexOfElement(object? item) => Elements.IndexOf(item);

    /// <summary>
    /// Creates a SharpTSArray from any enumerable collection.
    /// </summary>
    public SharpTSArray(IEnumerable<object?> elements) : this(new Deque<object?>(elements)) { }

    /// <summary>
    /// Creates an empty SharpTSArray.
    /// </summary>
    public SharpTSArray() : this(new Deque<object?>()) { }

    /// <summary>
    /// Whether this array is frozen (no element additions, removals, or modifications).
    /// </summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Whether this array is sealed (no element additions or removals, but modifications allowed).
    /// </summary>
    public bool IsSealed { get; private set; }

    /// <summary>
    /// Whether this array is extensible (can have new elements/properties added).
    /// </summary>
    public bool IsExtensible { get; private set; } = true;

    /// <summary>
    /// Upper bound on the number of <c>undefined</c> slots <see cref="Set(int, object?)"/>
    /// will pad when assigning beyond <see cref="Elements"/>.Count. ECMA-262 requires
    /// assignments like <c>a[2**31] = 1</c> to be O(1) on a sparse backing store;
    /// SharpTS currently uses a dense <see cref="Deque{T}"/>, so unbounded padding
    /// OOMs the process (billions of object references). Until the sparse-array
    /// refactor lands (issue #73) we throw <c>RangeError</c> past this threshold.
    /// </summary>
    private const int SparseGrowthLimit = 1_000_000;

    /// <summary>
    /// Freezes this array, preventing any element changes.
    /// </summary>
    public void Freeze()
    {
        IsFrozen = true;
        IsSealed = true; // Frozen implies sealed
        IsExtensible = false; // Frozen implies non-extensible
    }

    /// <summary>
    /// Seals this array, preventing element additions/removals but allowing modifications.
    /// </summary>
    public void Seal()
    {
        IsSealed = true;
        IsExtensible = false;
    }

    /// <summary>
    /// Prevents adding new elements/properties to this array.
    /// </summary>
    public void PreventExtensions()
    {
        IsExtensible = false;
    }

    public object? Get(int index)
    {
        // JS semantics: out-of-range index access returns `undefined`
        // rather than throwing.
        if (index < 0 || index >= Elements.Count)
        {
            return SharpTSUndefined.Instance;
        }
        return Elements[index];
    }

    public RuntimeValue GetRV(int index) => RuntimeValue.FromBoxed(Get(index));

    public void Set(int index, object? value)
    {
        if (IsFrozen)
        {
            // Frozen arrays silently ignore element modifications (JavaScript behavior)
            return;
        }

        if (index < 0)
        {
            throw new Exception("RangeError: Index out of bounds.");
        }

        // JS semantics: assigning to an index >= length extends the array,
        // padding intermediate slots with undefined.
        if (index >= Elements.Count)
        {
            if (!IsExtensible && index >= Elements.Count)
            {
                return;
            }
            GuardSparseGrowth(index);
            while (Elements.Count <= index)
            {
                Elements.Add(SharpTSUndefined.Instance);
            }
        }
        Elements[index] = value;
    }

    /// <summary>
    /// Sets an element at the given index with strict mode behavior.
    /// In strict mode, throws TypeError for modifications to frozen arrays.
    /// </summary>
    public void SetStrict(int index, object? value, bool strictMode)
    {
        if (IsFrozen)
        {
            if (strictMode)
            {
                throw new Exception($"TypeError: Cannot assign to read only property '{index}' of array");
            }
            return;
        }

        if (index < 0)
        {
            throw new Exception("RangeError: Index out of bounds.");
        }

        if (index >= Elements.Count)
        {
            if (!IsExtensible)
            {
                if (strictMode)
                    throw new Exception($"TypeError: Cannot add property {index}, object is not extensible");
                return;
            }
            GuardSparseGrowth(index);
            while (Elements.Count <= index)
            {
                Elements.Add(SharpTSUndefined.Instance);
            }
        }
        Elements[index] = value;
    }

    private void GuardSparseGrowth(int index)
    {
        var growth = (long)index + 1 - Elements.Count;
        if (growth > SparseGrowthLimit)
        {
            throw new Exception(
                $"RangeError: Array index {index} would require extending the backing store by " +
                $"{growth} slots (limit {SparseGrowthLimit}). SharpTS does not yet implement " +
                $"sparse-array storage (see issue #73).");
        }
    }

    /// <summary>
    /// Adds an element to the end of the array. Respects frozen/sealed state.
    /// </summary>
    /// <returns>True if the element was added, false if blocked by frozen/sealed state.</returns>
    public bool TryAdd(object? value)
    {
        if (IsFrozen || IsSealed)
        {
            return false;
        }
        Elements.Add(value);
        return true;
    }

    /// <summary>
    /// Adds an element to the end of the array with strict mode behavior.
    /// In strict mode, throws TypeError for additions to frozen/sealed arrays.
    /// </summary>
    public bool TryAddStrict(object? value, bool strictMode)
    {
        if (IsFrozen || IsSealed)
        {
            if (strictMode)
            {
                throw new Exception($"TypeError: Cannot add elements to a frozen or sealed array");
            }
            return false;
        }
        Elements.Add(value);
        return true;
    }

    /// <summary>
    /// Removes the last element. Respects frozen/sealed state.
    /// </summary>
    /// <returns>The removed element, or null if blocked or empty.</returns>
    public object? TryPop()
    {
        if (IsFrozen || IsSealed || Elements.Count == 0)
        {
            return null;
        }
        var last = Elements[^1];
        Elements.RemoveAt(Elements.Count - 1);
        return last;
    }

    /// <summary>
    /// Removes the last element with strict mode behavior.
    /// In strict mode, throws TypeError for removals from frozen/sealed arrays.
    /// </summary>
    public object? TryPopStrict(bool strictMode)
    {
        if (IsFrozen || IsSealed)
        {
            if (strictMode && Elements.Count > 0)
            {
                throw new Exception($"TypeError: Cannot remove elements from a frozen or sealed array");
            }
            return null;
        }
        if (Elements.Count == 0)
        {
            return null;
        }
        var last = Elements[^1];
        Elements.RemoveAt(Elements.Count - 1);
        return last;
    }

    /// <summary>
    /// Removes the first element. Respects frozen/sealed state. O(1) with Deque.
    /// </summary>
    /// <returns>The removed element, or null if blocked or empty.</returns>
    public object? TryShift()
    {
        if (IsFrozen || IsSealed || Elements.Count == 0)
        {
            return null;
        }
        return Elements.RemoveFirst();
    }

    /// <summary>
    /// Removes the first element with strict mode behavior. O(1) with Deque.
    /// In strict mode, throws TypeError for removals from frozen/sealed arrays.
    /// </summary>
    public object? TryShiftStrict(bool strictMode)
    {
        if (IsFrozen || IsSealed)
        {
            if (strictMode && Elements.Count > 0)
            {
                throw new Exception($"TypeError: Cannot remove elements from a frozen or sealed array");
            }
            return null;
        }
        if (Elements.Count == 0)
        {
            return null;
        }
        return Elements.RemoveFirst();
    }

    /// <summary>
    /// Adds an element to the beginning. Respects frozen/sealed state. O(1) with Deque.
    /// </summary>
    /// <returns>True if the element was added, false if blocked.</returns>
    public bool TryUnshift(object? value)
    {
        if (IsFrozen || IsSealed)
        {
            return false;
        }
        Elements.AddFirst(value);
        return true;
    }

    /// <summary>
    /// Adds an element to the beginning with strict mode behavior. O(1) with Deque.
    /// In strict mode, throws TypeError for additions to frozen/sealed arrays.
    /// </summary>
    public bool TryUnshiftStrict(object? value, bool strictMode)
    {
        if (IsFrozen || IsSealed)
        {
            if (strictMode)
            {
                throw new Exception($"TypeError: Cannot add elements to a frozen or sealed array");
            }
            return false;
        }
        Elements.AddFirst(value);
        return true;
    }

    /// <summary>
    /// Reverses the array in place. Respects frozen state (sealed allows in-place modification).
    /// </summary>
    /// <returns>True if reversed, false if frozen.</returns>
    public bool TryReverse()
    {
        if (IsFrozen)
        {
            return false;
        }
        Elements.Reverse();
        return true;
    }

    /// <summary>
    /// Reverses the array in place with strict mode behavior.
    /// In strict mode, throws TypeError for modifications to frozen arrays.
    /// </summary>
    public bool TryReverseStrict(bool strictMode)
    {
        if (IsFrozen)
        {
            if (strictMode)
            {
                throw new Exception($"TypeError: Cannot modify a frozen array");
            }
            return false;
        }
        Elements.Reverse();
        return true;
    }

    // Property descriptor storage for named properties (arrays can have non-numeric properties too)
    private Dictionary<string, object?>? _namedProperties;
    private Dictionary<string, PropertyDescriptorFlags>? _descriptors;

    /// <summary>
    /// Gets a named property value from the array.
    /// </summary>
    public object? GetNamedProperty(string name)
    {
        if (_namedProperties?.TryGetValue(name, out var value) == true)
        {
            return value;
        }
        return null;
    }

    /// <summary>
    /// Checks if a named property exists on the array.
    /// </summary>
    public bool HasNamedProperty(string name)
    {
        return _namedProperties?.ContainsKey(name) ?? false;
    }

    /// <summary>
    /// Sets a named property value on the array.
    /// </summary>
    public void SetNamedProperty(string name, object? value)
    {
        _namedProperties ??= new Dictionary<string, object?>();
        _namedProperties[name] = value;
    }

    /// <summary>
    /// Defines or modifies a property with the given descriptor.
    /// For arrays, this supports both numeric indices and named properties.
    /// </summary>
    public bool DefineProperty(string name, SharpTSPropertyDescriptor descriptor)
    {
        if (IsFrozen)
        {
            return false;
        }

        // Check if it's a numeric index
        if (int.TryParse(name, out int index) && index >= 0)
        {
            // For numeric indices, we only support data properties
            if (descriptor.Get != null || descriptor.Set != null)
            {
                // Arrays don't support accessor properties on indices
                return false;
            }

            // Expand array if needed (but not if sealed and index is beyond current length)
            if (index >= Elements.Count)
            {
                if (IsSealed)
                {
                    return false;
                }
                // Expand array with nulls
                while (Elements.Count <= index)
                {
                    Elements.Add(null);
                }
            }

            Elements[index] = descriptor.Value;

            // Store descriptor flags
            _descriptors ??= new Dictionary<string, PropertyDescriptorFlags>();
            _descriptors[name] = PropertyDescriptorFlags.ForDefineProperty(
                descriptor.Writable,
                descriptor.Enumerable,
                descriptor.Configurable
            );
            return true;
        }

        // Named property (non-numeric)
        if (IsSealed && (_namedProperties == null || !_namedProperties.ContainsKey(name)))
        {
            return false;
        }

        _namedProperties ??= new Dictionary<string, object?>();
        _namedProperties[name] = descriptor.Value;

        _descriptors ??= new Dictionary<string, PropertyDescriptorFlags>();
        _descriptors[name] = PropertyDescriptorFlags.ForDefineProperty(
            descriptor.Writable,
            descriptor.Enumerable,
            descriptor.Configurable
        );
        return true;
    }

    /// <summary>
    /// Gets the property descriptor for the given property name.
    /// Returns null if the property doesn't exist.
    /// </summary>
    public SharpTSPropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        // Special case for 'length'
        if (name == "length")
        {
            return new SharpTSPropertyDescriptor
            {
                Value = (double)Elements.Count,
                Writable = true,
                Enumerable = false,
                Configurable = false
            };
        }

        // Check if it's a numeric index
        if (int.TryParse(name, out int index) && index >= 0 && index < Elements.Count)
        {
            PropertyDescriptorFlags flags = default;
            if (_descriptors?.TryGetValue(name, out flags) != true)
            {
                flags = PropertyDescriptorFlags.Default;
            }

            return new SharpTSPropertyDescriptor
            {
                Value = Elements[index],
                Writable = flags.Writable,
                Enumerable = flags.Enumerable,
                Configurable = flags.Configurable
            };
        }

        // Check named properties
        if (_namedProperties?.TryGetValue(name, out var value) == true)
        {
            PropertyDescriptorFlags flags = default;
            if (_descriptors?.TryGetValue(name, out flags) != true)
            {
                flags = PropertyDescriptorFlags.Default;
            }

            return new SharpTSPropertyDescriptor
            {
                Value = value,
                Writable = flags.Writable,
                Enumerable = flags.Enumerable,
                Configurable = flags.Configurable
            };
        }

        return null;
    }

    public override string ToString() => $"[{string.Join(", ", Elements.Select(e => e?.ToString() ?? "null"))}]";
}
