using SharpTS.Execution;
using SharpTS.Runtime.BuiltIns;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime wrapper for JavaScript Proxy objects.
/// Supports handler traps: get, set, has, deleteProperty, apply, construct.
/// </summary>
public class SharpTSProxy : ISharpTSCallable
{
    private readonly object _target;
    private object? _handler;
    private bool _isRevoked;

    public object Target => _target;
    public bool IsRevoked => _isRevoked;

    public SharpTSProxy(object target, object handler)
    {
        ValidateObject(target, "target");
        ValidateObject(handler, "handler");
        _target = target;
        _handler = handler;
    }

    public void Revoke()
    {
        _isRevoked = true;
        _handler = null;
    }

    private void EnsureNotRevoked()
    {
        if (_isRevoked)
            throw new Exception("Runtime Error: Cannot perform operation on a revoked proxy.");
    }

    private static void ValidateObject(object? value, string argName)
    {
        if (value == null || value is SharpTSUndefined)
            throw new Exception($"Runtime Error: Cannot create proxy with a non-object as {argName}.");
        if (value is string or double or bool or int or long or float or decimal or SharpTSSymbol or SharpTSBigInt)
            throw new Exception($"Runtime Error: Cannot create proxy with a non-object as {argName}.");
    }

    private object? GetTrapCallable(string trapName)
    {
        EnsureNotRevoked();

        object? value = null;

        if (_handler is SharpTSObject obj)
        {
            value = obj.GetProperty(trapName);
        }
        else if (_handler is SharpTSInstance inst)
        {
            var token = new Parsing.Token(Parsing.TokenType.IDENTIFIER, trapName, null, 0);
            try { value = inst.Get(token); }
            catch { return null; }
        }
        else if (_handler is Dictionary<string, object?> dict)
        {
            dict.TryGetValue(trapName, out value);
        }

        if (value == null || value is SharpTSUndefined)
            return null;

        // ISharpTSCallable (interpreter mode)
        if (value is ISharpTSCallable)
            return value;

        // Check for compiled function types (TSFunction, Func<>, etc.) via Invoke method
        var invokeMethod = value.GetType().GetMethod("Invoke");
        if (invokeMethod != null)
            return value;

        // Func<object?[], object?> delegate
        if (value is Func<object?[], object?>)
            return value;

        throw new Exception($"Runtime Error: Proxy handler trap '{trapName}' is not a function.");
    }

    /// <summary>
    /// Invokes a trap function (either ISharpTSCallable, TSFunction, or Func delegate).
    /// </summary>
    private object? InvokeTrap(object trap, Interpreter? interp, List<object?> args)
    {
        if (trap is ISharpTSCallable callable)
            return callable.Call(interp!, args);

        // Func<object?[], object?> delegate
        if (trap is Func<object?[], object?> func)
            return func(args.ToArray());

        // Try Invoke(params object?[]) for TSFunction and similar compiled types
        var invokeMethod = trap.GetType().GetMethod("Invoke");
        if (invokeMethod != null)
        {
            // TSFunction.Invoke() takes params object?[] and handles argument conversion internally.
            // Object method shorthands in compiled mode have a __this parameter as the first arg.
            // We need to check the underlying method's parameter list to see if we need to prepend
            // a null __this arg so the trap args align correctly.
            var argsArray = args.ToArray();

            var methodField = trap.GetType().GetField("_method",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (methodField != null)
            {
                var mi = (System.Reflection.MethodInfo)methodField.GetValue(trap)!;
                var parameters = mi.GetParameters();
                // Check if the first parameter is __this (object method shorthand pattern)
                if (parameters.Length > 0 && parameters[0].Name == "__this")
                {
                    // Prepend null for __this
                    var extended = new object?[argsArray.Length + 1];
                    extended[0] = null;
                    Array.Copy(argsArray, 0, extended, 1, argsArray.Length);
                    argsArray = extended;
                }
            }

            return invokeMethod.Invoke(trap, [argsArray]);
        }

        throw new Exception("Runtime Error: Cannot invoke proxy trap.");
    }

    #region Trap Dispatch

    public object? TrapGet(string prop, Interpreter? interp)
    {
        var trap = GetTrapCallable("get");
        if (trap == null)
            return ForwardGet(prop, interp);

        // Pass target, prop, receiver (null for compiled mode compatibility)
        object? receiver = interp != null ? (object)this : null;
        return InvokeTrap(trap, interp, [_target, prop, receiver]);
    }

    public object? TrapSet(string prop, object? value, Interpreter? interp)
    {
        var trap = GetTrapCallable("set");
        if (trap == null)
            return ForwardSet(prop, value, interp);

        // Pass target, prop, value, receiver (null for compiled mode compatibility)
        object? receiver = interp != null ? (object)this : null;
        InvokeTrap(trap, interp, [_target, prop, value, receiver]);
        return value;
    }

    public bool TrapHas(string prop, Interpreter? interp)
    {
        var trap = GetTrapCallable("has");
        if (trap == null)
            return ForwardHas(prop, interp);

        var result = InvokeTrap(trap, interp, [_target, prop]);
        return ToBoolean(result);
    }

    public bool TrapDeleteProperty(string prop, Interpreter? interp)
    {
        var trap = GetTrapCallable("deleteProperty");
        if (trap == null)
            return ForwardDeleteProperty(prop);

        var result = InvokeTrap(trap, interp, [_target, prop]);
        return ToBoolean(result);
    }

    public object? TrapApply(object? thisArg, List<object?> args, Interpreter? interp)
    {
        var trap = GetTrapCallable("apply");
        if (trap == null)
        {
            if (_target is ISharpTSCallable callable)
                return callable.Call(interp!, args);

            // Compiled mode: target is a TSFunction, not ISharpTSCallable
            if (interp == null)
            {
                var invokeMethod = _target.GetType().GetMethod("Invoke");
                if (invokeMethod != null)
                    return invokeMethod.Invoke(_target, [args.ToArray()]);
            }

            throw new Exception("Runtime Error: Proxy target is not callable.");
        }

        // Compiled mode uses List<object?> for arrays; interpreter uses SharpTSArray
        object argsArg = interp != null ? new SharpTSArray(args) : (object)args;
        return InvokeTrap(trap, interp, [_target, thisArg, argsArg]);
    }

    public object? TrapConstruct(List<object?> args, Interpreter? interp)
    {
        var trap = GetTrapCallable("construct");
        if (trap == null)
        {
            if (_target is SharpTSClass klass)
                return klass.Call(interp!, args);
            if (_target is ISharpTSCallable callable)
                return callable.Call(interp!, args);
            throw new Exception("Runtime Error: Proxy target is not constructable.");
        }

        var argsArray = new SharpTSArray(args);
        return InvokeTrap(trap, interp, [_target, argsArray, this]);
    }

    #endregion

    #region Default Forwarding

    private object? ForwardGet(string prop, Interpreter? interp)
    {
        if (_target is SharpTSObject obj)
            return obj.GetProperty(prop);
        if (_target is SharpTSInstance inst)
        {
            if (interp != null) inst.SetInterpreter(interp);
            var token = new Parsing.Token(Parsing.TokenType.IDENTIFIER, prop, null, 0);
            return inst.Get(token);
        }
        if (_target is SharpTSArray arr)
        {
            // Handle array properties
            if (prop == "length") return (double)arr.Elements.Count;
            var member = BuiltInRegistry.Instance.GetInstanceMember(arr, prop);
            if (member is BuiltInMethod m) return m.Bind(arr);
            if (member is BuiltInAsyncMethod am) return am.Bind(arr);
            return member;
        }
        if (_target is Dictionary<string, object?> dict)
        {
            dict.TryGetValue(prop, out var val);
            return val;
        }
        // Fall through to built-in member lookup
        if (_target != null)
        {
            var member = BuiltInRegistry.Instance.GetInstanceMember(_target, prop);
            if (member is BuiltInMethod m) return m.Bind(_target);
            if (member is BuiltInAsyncMethod am) return am.Bind(_target);
            return member;
        }
        return null;
    }

    private object? ForwardSet(string prop, object? value, Interpreter? interp)
    {
        if (_target is SharpTSObject obj)
        {
            obj.SetProperty(prop, value);
            return value;
        }
        if (_target is SharpTSInstance inst)
        {
            if (interp != null) inst.SetInterpreter(interp);
            var token = new Parsing.Token(Parsing.TokenType.IDENTIFIER, prop, null, 0);
            inst.Set(token, value);
            return value;
        }
        if (_target is Dictionary<string, object?> dict)
        {
            dict[prop] = value;
            return value;
        }
        return value;
    }

    private bool ForwardHas(string prop, Interpreter? interp)
    {
        if (_target is SharpTSObject obj)
            return obj.HasProperty(prop);
        if (_target is SharpTSInstance inst)
            return inst.HasProperty(prop);
        if (_target is SharpTSArray arr)
        {
            if (double.TryParse(prop, out double index))
            {
                int i = (int)index;
                return i >= 0 && i < arr.Elements.Count;
            }
            return false;
        }
        if (_target is Dictionary<string, object?> dict)
            return dict.ContainsKey(prop);
        return false;
    }

    private bool ForwardDeleteProperty(string prop)
    {
        if (_target is SharpTSObject obj)
            return obj.DeletePropertyStrict(prop, false);
        if (_target is SharpTSInstance inst)
            return inst.DeleteFieldStrict(prop, false);
        if (_target is Dictionary<string, object?> dict)
            return dict.Remove(prop);
        return true;
    }

    #endregion

    #region ISharpTSCallable (for apply trap)

    public int Arity() => _target is ISharpTSCallable callable ? callable.Arity() : 0;

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        return TrapApply(null, arguments, interpreter);
    }

    #endregion

    /// <summary>
    /// Returns whether the proxy target is callable (function-like).
    /// Checks ISharpTSCallable (interpreter mode), Delegate, and emitted compiled function types.
    /// </summary>
    public bool IsCallable => _target is ISharpTSCallable or Delegate
        || _target?.GetType().Name is "$TSFunction" or "$BoundTSFunction"
            or "$PromisifiedFunction" or "$DeprecatedFunction";

    #region RuntimeValue Overloads

    public RuntimeValue TrapGetRV(string property, Interpreter? interpreter)
        => RuntimeValue.FromBoxed(TrapGet(property, interpreter));

    public RuntimeValue TrapSetRV(string property, object? value, Interpreter? interpreter)
        => RuntimeValue.FromBoxed(TrapSet(property, value, interpreter));

    public RuntimeValue TrapConstructRV(List<object?> args, Interpreter? interpreter)
        => RuntimeValue.FromBoxed(TrapConstruct(args, interpreter));

    #endregion

    public override string ToString() => "Proxy {}";

    /// <summary>
    /// Converts a trap result to boolean using JavaScript truthiness rules.
    /// </summary>
    private static bool ToBoolean(object? value) => value switch
    {
        null => false,
        bool b => b,
        double d => d != 0 && !double.IsNaN(d),
        string s => s.Length > 0,
        _ => true
    };
}
