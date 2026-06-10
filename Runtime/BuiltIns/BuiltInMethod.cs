using SharpTS.Runtime.Types;
using SharpTS.Execution;
using System.Runtime.CompilerServices;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Callable wrapper for native C# implementations of built-in methods.
/// </summary>
/// <remarks>
/// Implements <see cref="ISharpTSCallable"/> to provide a uniform calling convention for
/// built-in methods (array methods like push/pop/map, string methods, Math functions).
/// Supports variable arity via min/max argument counts. The <see cref="Bind"/> method
/// associates a receiver object (e.g., the array instance for array methods).
/// Used by <see cref="Interpreter"/> when resolving method calls on built-in types.
/// </remarks>
/// <seealso cref="ISharpTSCallable"/>
/// <seealso cref="MathBuiltIns"/>
public class BuiltInMethod : ISharpTSCallable
{
    private readonly string _name;
    private readonly int _minArity;
    private readonly int _maxArity;
    private readonly Func<Interpreter, object?, List<object?>, object?> _implementation;
    private readonly Func<Interpreter, RuntimeValue, ReadOnlySpan<RuntimeValue>, RuntimeValue>? _implementationV2;
    // Receiver is stored in both representations, computed once at bind time:
    // the boxed form feeds legacy delegate bodies, the RuntimeValue form feeds
    // V2 bodies without a per-call FromBoxed. Unbound = (null, RuntimeValue.Null).
    private readonly object? _receiverBoxed;
    private readonly RuntimeValue _receiver = RuntimeValue.Null;
    private readonly bool _isBound;
    // Spec-defined Function.prototype.length value visible to user code as
    // `f.length`. Distinct from MinArity / MaxArity (used internally for arg
    // padding/trimming): per ECMA-262, variadic methods like Array.prototype.push
    // have spec length 1 even though their MinArity is 0 in our registration.
    // -1 = unset (caller didn't specify a spec length); resolvers fall back to
    // MinArity for compatibility with the many call sites that already report
    // the correct value via MinArity.
    private int _specLength = -1;

    // Cache for bound methods - uses weak references to avoid memory leaks
    // Key: receiver object, Value: bound method instance
    private ConditionalWeakTable<object, BuiltInMethod>? _boundMethodCache;

    /// <summary>
    /// The minimum number of arguments this method accepts.
    /// </summary>
    public int MinArity => _minArity;

    /// <summary>
    /// The maximum number of arguments this method accepts.
    /// </summary>
    public int MaxArity => _maxArity;

    /// <summary>
    /// Gets the method name.
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// Returns true if this method has a receiver bound via Bind().
    /// Used by fast-path dispatch to avoid redundant double-binding.
    /// </summary>
    public bool IsBound => _isBound;

    /// <summary>
    /// True when this method wraps a constant value (e.g. Number.MAX_VALUE, Math.PI)
    /// rather than a real method. The interpreter invokes such methods on property
    /// access to materialize the underlying value. Distinguishing is necessary because
    /// some real methods also have <c>MinArity == MaxArity == 0</c> (Date.now, Array.isArray-as-value),
    /// and invoking THOSE on property access breaks function-reference aliasing
    /// (<c>var nativeNow = Date.now; nativeNow();</c> is a lodash/polyfill idiom).
    /// </summary>
    public bool IsConstant { get; }

    /// <summary>
    /// Returns true if this method has a native V2 (RuntimeValue) implementation,
    /// meaning Call can bypass the legacy wrapper for better performance.
    /// </summary>
    public bool HasNativeImplementation => _implementationV2 != null;

    public BuiltInMethod(string name, int arity, Func<Interpreter, object?, List<object?>, object?> implementation)
        : this(name, arity, arity, implementation) { }

    public BuiltInMethod(string name, int minArity, int maxArity, Func<Interpreter, object?, List<object?>, object?> implementation)
        : this(name, minArity, maxArity, implementation, isConstant: false) { }

    /// <summary>
    /// Internal constructor with IsConstant flag — use <see cref="CreateConstant"/> to wrap
    /// a property-style constant (Number.MAX_VALUE etc.).
    /// </summary>
    internal BuiltInMethod(string name, int minArity, int maxArity,
        Func<Interpreter, object?, List<object?>, object?> implementation, bool isConstant)
    {
        _name = name;
        _minArity = minArity;
        _maxArity = maxArity;
        _implementation = implementation;
        IsConstant = isConstant;
    }

    /// <summary>
    /// Creates a zero-arity BuiltInMethod whose <see cref="IsConstant"/> is true — the
    /// wrapped value materializes on every invocation, and the interpreter's property-access
    /// fast-path calls it on read instead of returning the method reference.
    /// </summary>
    public static BuiltInMethod CreateConstant(string name, object? value)
        => new(name, 0, 0, (_, _, _) => value, isConstant: true);

    /// <summary>
    /// Creates a BuiltInMethod with a RuntimeValue-based implementation.
    /// Use <see cref="CreateV2"/> factory method instead for clearer intent.
    /// </summary>
    private BuiltInMethod(int arity,
        Func<Interpreter, RuntimeValue, ReadOnlySpan<RuntimeValue>, RuntimeValue> implementation,
        string name)
    {
        _name = name;
        _minArity = arity;
        _maxArity = arity;
        _implementationV2 = implementation;
        // Create a legacy wrapper
        _implementation = WrapV2Implementation(implementation);
    }

    /// <summary>
    /// Creates a BuiltInMethod with a RuntimeValue-based implementation.
    /// </summary>
    /// <param name="name">The method name.</param>
    /// <param name="arity">The number of required arguments.</param>
    /// <param name="implementation">The V2 implementation using RuntimeValue.</param>
    /// <returns>A new BuiltInMethod instance.</returns>
    public static BuiltInMethod CreateV2(string name, int arity,
        Func<Interpreter, RuntimeValue, ReadOnlySpan<RuntimeValue>, RuntimeValue> implementation)
    {
        return new BuiltInMethod(arity, implementation, name);
    }

    /// <summary>
    /// Creates a BuiltInMethod with a RuntimeValue-based implementation and variable arity.
    /// </summary>
    /// <param name="name">The method name.</param>
    /// <param name="minArity">The minimum number of arguments.</param>
    /// <param name="maxArity">The maximum number of arguments.</param>
    /// <param name="implementation">The V2 implementation using RuntimeValue.</param>
    /// <returns>A new BuiltInMethod instance.</returns>
    public static BuiltInMethod CreateV2(string name, int minArity, int maxArity,
        Func<Interpreter, RuntimeValue, ReadOnlySpan<RuntimeValue>, RuntimeValue> implementation)
    {
        return new BuiltInMethod(name, minArity, maxArity, null!, implementation, (object?)null)
        {
            // Override the null implementation with the wrapper
        };
    }

    // Private constructor for creating bound instances (no cache needed on bound instances)
    private BuiltInMethod(string name, int minArity, int maxArity,
        Func<Interpreter, object?, List<object?>, object?>? implementation,
        Func<Interpreter, RuntimeValue, ReadOnlySpan<RuntimeValue>, RuntimeValue>? implementationV2,
        object? receiver)
    {
        _name = name;
        _minArity = minArity;
        _maxArity = maxArity;
        // If only V2 is provided, create the legacy wrapper
        if (implementation == null && implementationV2 != null)
        {
            _implementationV2 = implementationV2;
            _implementation = WrapV2Implementation(implementationV2);
        }
        else
        {
            _implementation = implementation ?? throw new ArgumentNullException(nameof(implementation));
            _implementationV2 = implementationV2;
        }
        _receiverBoxed = receiver;
        _receiver = RuntimeValue.FromBoxed(receiver);
        _isBound = receiver != null;
        // Bound instances don't have their own cache
    }

    // Private constructor for RuntimeValue-bound instances
    private BuiltInMethod(string name, int minArity, int maxArity,
        Func<Interpreter, object?, List<object?>, object?> implementation,
        Func<Interpreter, RuntimeValue, ReadOnlySpan<RuntimeValue>, RuntimeValue>? implementationV2,
        RuntimeValue receiverV2)
    {
        _name = name;
        _minArity = minArity;
        _maxArity = maxArity;
        _implementation = implementation;
        _implementationV2 = implementationV2;
        _receiver = receiverV2;
        _receiverBoxed = receiverV2.ToObject();
        _isBound = true;
    }

    public int Arity() => _minArity;

    /// <summary>
    /// ECMA-262 Function.prototype.length value. When unset (-1), falls back
    /// to <see cref="MinArity"/> — matches most methods, where the minimum
    /// argument count equals the spec length. Variadic methods that need an
    /// explicit value (push, slice, splice, …) set it via
    /// <see cref="WithSpecLength"/> at registration time.
    /// </summary>
    public int SpecLength => _specLength >= 0 ? _specLength : _minArity;

    /// <summary>
    /// Returns this same instance with the JS-spec length set. Mutates in
    /// place — safe because BuiltInMethod is set up once at static-init time.
    /// </summary>
    public BuiltInMethod WithSpecLength(int specLength)
    {
        _specLength = specLength;
        return this;
    }

    /// <summary>
    /// Binds the method to a receiver using RuntimeValue.
    /// </summary>
    public BuiltInMethod Bind(RuntimeValue receiver)
    {
        var bound = new BuiltInMethod(_name, _minArity, _maxArity, _implementation, _implementationV2, receiver);
        bound._specLength = _specLength;
        return bound;
    }

    public BuiltInMethod Bind(object? receiver)
    {
        // Null receivers don't need caching
        if (receiver == null)
        {
            var unboundCopy = new BuiltInMethod(_name, _minArity, _maxArity, _implementation, _implementationV2, (object?)null);
            unboundCopy._specLength = _specLength;
            return unboundCopy;
        }

        // Value types (like double for numbers) can't be cached in ConditionalWeakTable
        // because they're boxed each time, creating new object instances
        if (receiver.GetType().IsValueType)
        {
            var valBound = new BuiltInMethod(_name, _minArity, _maxArity, _implementation, _implementationV2, receiver);
            valBound._specLength = _specLength;
            return valBound;
        }

        // Initialize cache lazily
        _boundMethodCache ??= new ConditionalWeakTable<object, BuiltInMethod>();

        // Try to get cached bound method
        if (_boundMethodCache.TryGetValue(receiver, out var cached))
        {
            return cached;
        }

        // Create new bound method and cache it
        var bound = new BuiltInMethod(_name, _minArity, _maxArity, _implementation, _implementationV2, receiver);
        bound._specLength = _specLength;
        _boundMethodCache.AddOrUpdate(receiver, bound);
        return bound;
    }

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        if (arguments.Count < _minArity || arguments.Count > _maxArity)
        {
            throw new Exception($"Runtime Error: '{_name}' expects {_minArity}-{_maxArity} arguments but got {arguments.Count}.");
        }
        return _implementation(interpreter, _receiverBoxed, arguments);
    }

    /// <summary>
    /// Reflection-stable boxed entry point. Compiled DLLs invoke this by name
    /// (<c>GetMethod("CallBoxed")</c>) from the vm/eval bridge paths emitted in
    /// RuntimeEmitter — a null interpreter is part of that contract. Do not
    /// rename or change the signature without updating the emitter's
    /// <c>Ldstr "CallBoxed"</c> sites.
    /// </summary>
    public object? CallBoxed(Interpreter? interpreter, List<object?> arguments)
        => Call(interpreter!, arguments);

    /// <summary>
    /// Calls the method with RuntimeValue arguments.
    /// </summary>
    public RuntimeValue Call(Interpreter interpreter, ReadOnlySpan<RuntimeValue> arguments)
    {
        if (arguments.Length < _minArity || arguments.Length > _maxArity)
        {
            throw new Exception($"Runtime Error: '{_name}' expects {_minArity}-{_maxArity} arguments but got {arguments.Length}.");
        }

        // Fast path: if we have a V2 implementation, use it directly
        if (_implementationV2 != null)
        {
            return _implementationV2(interpreter, _receiver, arguments);
        }

        // Slow path: convert to legacy call
        var result = _implementation(interpreter, _receiverBoxed, CallableInterop.ToBoxedList(arguments));
        return RuntimeValue.FromBoxed(result);
    }

    /// <summary>
    /// Creates a legacy wrapper for a V2 implementation.
    /// </summary>
    private static Func<Interpreter, object?, List<object?>, object?> WrapV2Implementation(
        Func<Interpreter, RuntimeValue, ReadOnlySpan<RuntimeValue>, RuntimeValue> v2Impl)
    {
        return (interpreter, receiver, arguments) =>
        {
            var rvArgs = new RuntimeValue[arguments.Count];
            for (int i = 0; i < arguments.Count; i++)
            {
                rvArgs[i] = RuntimeValue.FromBoxed(arguments[i]);
            }

            var result = v2Impl(interpreter, RuntimeValue.FromBoxed(receiver), rvArgs);
            return result.ToObject();
        };
    }

    public override string ToString() => $"<built-in {_name}>";
}
