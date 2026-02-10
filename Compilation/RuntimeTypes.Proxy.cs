using SharpTS.Runtime.Types;

namespace SharpTS.Compilation;

public static partial class RuntimeTypes
{
    #region Proxy Support

    /// <summary>
    /// Creates a Proxy wrapping the given target with the given handler.
    /// Used by both interpreter and compiled code paths.
    /// </summary>
    public static object CreateProxy(object? target, object? handler)
    {
        if (target == null)
            throw new Exception("Runtime Error: Cannot create proxy with a non-object as target.");
        if (handler == null)
            throw new Exception("Runtime Error: Cannot create proxy with a non-object as handler.");
        return new SharpTSProxy(target, handler);
    }

    /// <summary>
    /// Creates a revocable Proxy. Returns a Dictionary with "proxy" and "revoke" keys.
    /// </summary>
    public static object CreateRevocableProxy(object? target, object? handler)
    {
        if (target == null)
            throw new Exception("Runtime Error: Cannot create proxy with a non-object as target.");
        if (handler == null)
            throw new Exception("Runtime Error: Cannot create proxy with a non-object as handler.");

        var proxy = new SharpTSProxy(target, handler);
        var revoked = false;
        Func<object?[], object?> revoke = _ =>
        {
            if (!revoked)
            {
                revoked = true;
                proxy.Revoke();
            }
            return null;
        };

        return new Dictionary<string, object?>
        {
            ["proxy"] = proxy,
            ["revoke"] = revoke
        };
    }

    /// <summary>
    /// Gets a property from an object, checking for Proxy first.
    /// Used by compiled code when proxy support is needed.
    /// </summary>
    public static object? ProxyAwareGetProperty(object? obj, string name)
    {
        if (obj is SharpTSProxy proxy)
            return proxy.TrapGet(name, null);
        return GetProperty(obj, name);
    }

    /// <summary>
    /// Sets a property on an object, checking for Proxy first.
    /// </summary>
    public static void ProxyAwareSetProperty(object? obj, string name, object? value)
    {
        if (obj is SharpTSProxy proxy)
        {
            proxy.TrapSet(name, value, null);
            return;
        }
        SetProperty(obj, name, value);
    }

    /// <summary>
    /// Gets an indexed property, checking for Proxy first.
    /// </summary>
    public static object? ProxyAwareGetIndex(object? obj, object? index)
    {
        if (obj is SharpTSProxy proxy)
        {
            string key = index?.ToString() ?? "";
            return proxy.TrapGet(key, null);
        }
        return GetIndex(obj, index);
    }

    /// <summary>
    /// Sets an indexed property, checking for Proxy first.
    /// </summary>
    public static void ProxyAwareSetIndex(object? obj, object? index, object? value)
    {
        if (obj is SharpTSProxy proxy)
        {
            string key = index?.ToString() ?? "";
            proxy.TrapSet(key, value, null);
            return;
        }
        SetIndex(obj, index, value);
    }

    /// <summary>
    /// Checks if a property exists (for 'in' operator), checking for Proxy first.
    /// </summary>
    public static bool ProxyAwareHasIn(object? key, object? obj)
    {
        if (obj is SharpTSProxy proxy)
        {
            string keyStr = key?.ToString() ?? "";
            return proxy.TrapHas(keyStr, null);
        }
        // Fall through to regular HasIn logic
        return false; // Will be handled by the emitted HasIn method
    }

    /// <summary>
    /// Deletes a property, checking for Proxy first.
    /// </summary>
    public static bool ProxyAwareDeleteProperty(object? obj, string name)
    {
        if (obj is SharpTSProxy proxy)
            return proxy.TrapDeleteProperty(name, null);

        // Forward to regular delete
        if (obj is Dictionary<string, object?> dict)
            return dict.Remove(name);
        return true;
    }

    /// <summary>
    /// Checks if an object is a SharpTSProxy. Used by emitted code via reflection.
    /// </summary>
    public static bool IsSharpTSProxy(object? obj)
    {
        return obj is SharpTSProxy;
    }

    /// <summary>
    /// Calls TrapHas on a proxy. Used by emitted HasIn via reflection.
    /// Assumes obj is a SharpTSProxy.
    /// </summary>
    public static bool ProxyTrapHas(object? key, object? obj)
    {
        var proxy = (SharpTSProxy)obj!;
        string keyStr = key?.ToString() ?? "";
        return proxy.TrapHas(keyStr, null);
    }

    /// <summary>
    /// Calls TrapDeleteProperty on a proxy. Used by emitted DeleteProperty via reflection.
    /// Assumes obj is a SharpTSProxy.
    /// </summary>
    public static bool ProxyTrapDeleteProperty(object? obj, string name)
    {
        var proxy = (SharpTSProxy)obj!;
        return proxy.TrapDeleteProperty(name, null);
    }

    /// <summary>
    /// Calls TrapApply on a proxy. Used by emitted InvokeValue via reflection.
    /// Assumes callee is a SharpTSProxy.
    /// </summary>
    public static object? ProxyTrapApply(object? callee, object?[] args)
    {
        var proxy = (SharpTSProxy)callee!;
        var argsList = new List<object?>(args);
        return proxy.TrapApply(null, argsList, null);
    }

    /// <summary>
    /// Calls TrapConstruct on a proxy. Used by emitted new expression via reflection.
    /// Assumes callee is a SharpTSProxy.
    /// </summary>
    public static object? ProxyTrapConstruct(object? callee, object?[] args)
    {
        var proxy = (SharpTSProxy)callee!;
        var argsList = new List<object?>(args);
        return proxy.TrapConstruct(argsList, null);
    }

    #endregion
}
