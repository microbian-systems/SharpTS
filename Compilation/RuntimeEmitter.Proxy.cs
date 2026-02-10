using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    private const string ProxyTypeName = "SharpTS.Runtime.Types.SharpTSProxy";

    private void EmitProxyMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitCreateProxy(typeBuilder, runtime);
        EmitCreateRevocableProxy(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits a null-safe proxy type check: if (obj.GetType().FullName == "SharpTS.Runtime.Types.SharpTSProxy") goto proxyLabel;
    /// Assumes obj is already on the stack (does NOT consume it). Falls through to notProxyLabel if not a proxy.
    /// </summary>
    /// <param name="il">The IL generator.</param>
    /// <param name="loadObj">Action to emit loading the object onto the stack.</param>
    /// <param name="proxyLabel">Label to jump to if obj is a proxy.</param>
    /// <param name="notProxyLabel">Label to jump to if obj is not a proxy.</param>
    private void EmitProxyTypeCheck(ILGenerator il, Action emitLoadObj, Label proxyLabel, Label notProxyLabel)
    {
        // obj.GetType().FullName == "SharpTS.Runtime.Types.SharpTSProxy"
        emitLoadObj();
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "GetType"));
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.Type, "FullName").GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, ProxyTypeName);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, proxyLabel);
        il.Emit(OpCodes.Br, notProxyLabel);
    }

    /// <summary>
    /// Emits a call to a method on the proxy object via reflection on the object's own type:
    /// obj.GetType().GetMethod(methodName).Invoke(obj, args)
    /// This avoids any dependency on SharpTS.dll being loaded.
    /// </summary>
    private void EmitProxyMethodCall(ILGenerator il, Action emitLoadObj, string methodName, Action emitArgs)
    {
        // obj.GetType().GetMethod(methodName)
        emitLoadObj();
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, methodName);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));

        // .Invoke(obj, args)
        emitLoadObj();
        emitArgs();
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray));
    }

    /// <summary>
    /// Emits a proxy-aware property get: checks if obj is a proxy and calls TrapGet(name, null),
    /// otherwise falls through to notProxyLabel.
    /// Emitted IL equivalent:
    ///   if (obj.GetType().FullName == ProxyTypeName) return obj.TrapGet(name, null);
    /// </summary>
    internal void EmitProxyGetPropertyCheck(ILGenerator il, Action emitLoadObj, Action emitLoadName, Label notProxyLabel)
    {
        var proxyLabel = il.DefineLabel();
        EmitProxyTypeCheck(il, emitLoadObj, proxyLabel, notProxyLabel);

        il.MarkLabel(proxyLabel);
        // Call TrapGet(string prop, Interpreter? interp) via reflection on the proxy object
        EmitProxyMethodCall(il, emitLoadObj, "TrapGet", () =>
        {
            // new object[] { name, null }
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Newarr, _types.Object);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            emitLoadName();
            il.Emit(OpCodes.Stelem_Ref);
            // [1] = null (Interpreter) - already null from Newarr
        });
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a proxy-aware property set: checks if obj is a proxy and calls TrapSet(name, value, null),
    /// otherwise falls through to notProxyLabel.
    /// </summary>
    internal void EmitProxySetPropertyCheck(ILGenerator il, Action emitLoadObj, Action emitLoadName, Action emitLoadValue, Label notProxyLabel)
    {
        var proxyLabel = il.DefineLabel();
        EmitProxyTypeCheck(il, emitLoadObj, proxyLabel, notProxyLabel);

        il.MarkLabel(proxyLabel);
        // Call TrapSet(string prop, object? value, Interpreter? interp) via reflection
        EmitProxyMethodCall(il, emitLoadObj, "TrapSet", () =>
        {
            // new object[] { name, value, null }
            il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(OpCodes.Newarr, _types.Object);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            emitLoadName();
            il.Emit(OpCodes.Stelem_Ref);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_1);
            emitLoadValue();
            il.Emit(OpCodes.Stelem_Ref);
            // [2] = null (Interpreter) - already null from Newarr
        });
        il.Emit(OpCodes.Pop); // TrapSet returns the value, but SetProperty is void
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a proxy-aware index get: checks if obj is a proxy and calls TrapGet(key.ToString(), null).
    /// </summary>
    internal void EmitProxyGetIndexCheck(ILGenerator il, Action emitLoadObj, Action emitLoadIndex, Label notProxyLabel)
    {
        var proxyLabel = il.DefineLabel();
        EmitProxyTypeCheck(il, emitLoadObj, proxyLabel, notProxyLabel);

        il.MarkLabel(proxyLabel);
        // Convert index to string and call TrapGet
        EmitProxyMethodCall(il, emitLoadObj, "TrapGet", () =>
        {
            // new object[] { index?.ToString() ?? "", null }
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Newarr, _types.Object);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            emitLoadIndex();
            // Convert index to string via ToString
            var indexNullLabel = il.DefineLabel();
            var indexEndLabel = il.DefineLabel();
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brfalse, indexNullLabel);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
            il.Emit(OpCodes.Br, indexEndLabel);
            il.MarkLabel(indexNullLabel);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldstr, "");
            il.MarkLabel(indexEndLabel);
            il.Emit(OpCodes.Stelem_Ref);
            // [1] = null (Interpreter) - already null from Newarr
        });
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a proxy-aware index set: checks if obj is a proxy and calls TrapSet(key.ToString(), value, null).
    /// </summary>
    internal void EmitProxySetIndexCheck(ILGenerator il, Action emitLoadObj, Action emitLoadIndex, Action emitLoadValue, Label notProxyLabel)
    {
        var proxyLabel = il.DefineLabel();
        EmitProxyTypeCheck(il, emitLoadObj, proxyLabel, notProxyLabel);

        il.MarkLabel(proxyLabel);
        EmitProxyMethodCall(il, emitLoadObj, "TrapSet", () =>
        {
            // new object[] { index?.ToString() ?? "", value, null }
            il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(OpCodes.Newarr, _types.Object);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            emitLoadIndex();
            var indexNullLabel = il.DefineLabel();
            var indexEndLabel = il.DefineLabel();
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brfalse, indexNullLabel);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
            il.Emit(OpCodes.Br, indexEndLabel);
            il.MarkLabel(indexNullLabel);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldstr, "");
            il.MarkLabel(indexEndLabel);
            il.Emit(OpCodes.Stelem_Ref);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_1);
            emitLoadValue();
            il.Emit(OpCodes.Stelem_Ref);
            // [2] = null (Interpreter) - already null from Newarr
        });
        il.Emit(OpCodes.Pop); // TrapSet returns value, SetIndex is void
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a proxy-aware has check: checks if obj is a proxy and calls TrapHas(key, null).
    /// Returns bool result.
    /// </summary>
    internal void EmitProxyHasCheck(ILGenerator il, Action emitLoadObj, Action emitLoadKey, Label notProxyLabel)
    {
        var proxyLabel = il.DefineLabel();
        EmitProxyTypeCheck(il, emitLoadObj, proxyLabel, notProxyLabel);

        il.MarkLabel(proxyLabel);
        EmitProxyMethodCall(il, emitLoadObj, "TrapHas", () =>
        {
            // new object[] { key?.ToString() ?? "", null }
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Newarr, _types.Object);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            emitLoadKey();
            var keyNullLabel = il.DefineLabel();
            var keyEndLabel = il.DefineLabel();
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brfalse, keyNullLabel);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
            il.Emit(OpCodes.Br, keyEndLabel);
            il.MarkLabel(keyNullLabel);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldstr, "");
            il.MarkLabel(keyEndLabel);
            il.Emit(OpCodes.Stelem_Ref);
            // [1] = null (Interpreter) - already null from Newarr
        });
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a proxy-aware delete check: checks if obj is a proxy and calls TrapDeleteProperty(name, null).
    /// Returns bool result.
    /// </summary>
    internal void EmitProxyDeleteCheck(ILGenerator il, Action emitLoadObj, Action emitLoadName, Label notProxyLabel)
    {
        var proxyLabel = il.DefineLabel();
        EmitProxyTypeCheck(il, emitLoadObj, proxyLabel, notProxyLabel);

        il.MarkLabel(proxyLabel);
        EmitProxyMethodCall(il, emitLoadObj, "TrapDeleteProperty", () =>
        {
            // new object[] { name, null }
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Newarr, _types.Object);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            emitLoadName();
            il.Emit(OpCodes.Stelem_Ref);
            // [1] = null (Interpreter) - already null from Newarr
        });
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a proxy-aware invoke check: checks if callee is a proxy and calls TrapApply(null, argsList, null).
    /// </summary>
    internal void EmitProxyInvokeCheck(ILGenerator il, Action emitLoadCallee, Action emitLoadArgs, Label notProxyLabel)
    {
        var proxyLabel = il.DefineLabel();
        EmitProxyTypeCheck(il, emitLoadCallee, proxyLabel, notProxyLabel);

        il.MarkLabel(proxyLabel);
        EmitProxyMethodCall(il, emitLoadCallee, "TrapApply", () =>
        {
            // new object[] { null (thisArg), argsList, null (Interpreter) }
            il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(OpCodes.Newarr, _types.Object);
            // [0] = null (thisArg) - already null from Newarr
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_1);
            emitLoadArgs(); // Load the List<object?> args
            il.Emit(OpCodes.Stelem_Ref);
            // [2] = null (Interpreter) - already null from Newarr
        });
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits CreateProxy(object target, object handler) -> object (SharpTSProxy).
    /// Validates both args are non-null objects and creates a SharpTSProxy.
    /// Uses reflection to avoid compile-time dependency on SharpTS.dll.
    /// </summary>
    private void EmitCreateProxy(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CreateProxy",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.CreateProxy = method;

        var il = method.GetILGenerator();

        var targetNullLabel = il.DefineLabel();
        var handlerNullLabel = il.DefineLabel();

        // if (target == null) throw
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, targetNullLabel);

        // if (handler == null) throw
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, handlerNullLabel);

        // Use reflection to create SharpTSProxy: Type.GetType("SharpTS.Runtime.Types.SharpTSProxy, SharpTS")
        // and call Activator.CreateInstance(type, target, handler)

        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.Types.SharpTSProxy, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));

        // Create object[] { target, handler }
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stelem_Ref);

        // Call Activator.CreateInstance(type, args)
        var createInstanceMethod = _types.GetMethod(_types.Activator, "CreateInstance", _types.Type, _types.ObjectArray);
        il.Emit(OpCodes.Call, createInstanceMethod!);
        il.Emit(OpCodes.Ret);

        // target null - throw
        il.MarkLabel(targetNullLabel);
        il.Emit(OpCodes.Ldstr, "Runtime Error: Cannot create proxy with a non-object as target.");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        // handler null - throw
        il.MarkLabel(handlerNullLabel);
        il.Emit(OpCodes.Ldstr, "Runtime Error: Cannot create proxy with a non-object as handler.");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);
    }

    /// <summary>
    /// Emits CreateRevocableProxy(object target, object handler) -> object ({ proxy, revoke }).
    /// Calls RuntimeTypes.CreateRevocableProxy via reflection to avoid SharpTS.dll dependency.
    /// </summary>
    private void EmitCreateRevocableProxy(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CreateRevocableProxy",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.CreateRevocableProxy = method;

        var il = method.GetILGenerator();

        // Get the RuntimeTypes type via reflection
        // Type runtimeTypesType = Type.GetType("SharpTS.Compilation.RuntimeTypes, SharpTS")
        il.Emit(OpCodes.Ldstr, "SharpTS.Compilation.RuntimeTypes, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));

        // Get the CreateRevocableProxy method
        // MethodInfo mi = runtimeTypesType.GetMethod("CreateRevocableProxy")
        il.Emit(OpCodes.Ldstr, "CreateRevocableProxy");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));

        // Prepare args: new object[] { target, handler }
        il.Emit(OpCodes.Ldnull); // null target for static method invoke

        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0); // target
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1); // handler
        il.Emit(OpCodes.Stelem_Ref);

        // Call methodInfo.Invoke(null, args)
        var invokeMethod = _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray);
        il.Emit(OpCodes.Callvirt, invokeMethod!);

        il.Emit(OpCodes.Ret);
    }
}
