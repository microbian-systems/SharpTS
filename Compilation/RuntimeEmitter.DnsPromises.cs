using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits dns/promises support methods into the $Runtime class.
/// Uses reflection to call RuntimeTypes.DnsPromises* methods for standalone DLL compatibility.
/// </summary>
public partial class RuntimeEmitter
{
    private void EmitDnsPromisesMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.DnsPromisesWrapperMethods = new Dictionary<string, MethodBuilder>();

        // Single-arg methods: (object hostname) → Task<object?>
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesResolveMx", 1);
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesResolveTxt", 1);
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesResolveSrv", 1);
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesResolveCname", 1);
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesResolveNs", 1);
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesResolveSoa", 1);
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesResolvePtr", 1);
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesResolveCaa", 1);
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesResolveNaptr", 1);
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesResolve4", 1);
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesResolve6", 1);
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesReverse", 1);

        // Two-arg methods
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesLookup", 2);
        EmitDnsPromisesReflectionWrapper(typeBuilder, runtime, "DnsPromisesResolve", 2);

        // Namespace getter
        EmitDnsGetPromisesNamespace(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits a $Runtime wrapper method that calls RuntimeTypes.{methodName} via late-binding reflection.
    /// Returns Task&lt;object?&gt; (the caller wraps it in a Promise).
    /// </summary>
    private void EmitDnsPromisesReflectionWrapper(
        TypeBuilder typeBuilder, EmittedRuntime runtime,
        string methodName, int argCount)
    {
        var paramTypes = new Type[argCount];
        for (int i = 0; i < argCount; i++)
            paramTypes[i] = _types.Object;

        var wrapper = typeBuilder.DefineMethod(
            methodName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,  // Returns $Promise (via WrapTaskAsPromise)
            paramTypes);

        var il = wrapper.GetILGenerator();

        // Type runtimeTypes = Type.GetType("SharpTS.Compilation.RuntimeTypes, SharpTS")
        il.Emit(OpCodes.Ldstr, "SharpTS.Compilation.RuntimeTypes, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));

        // MethodInfo mi = runtimeTypes.GetMethod("DnsPromisesResolveMx")
        il.Emit(OpCodes.Ldstr, methodName);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));

        // object result = mi.Invoke(null, new object[] { arg0, ... })
        il.Emit(OpCodes.Ldnull); // static method target

        il.Emit(OpCodes.Ldc_I4, argCount);
        il.Emit(OpCodes.Newarr, _types.Object);
        for (int i = 0; i < argCount; i++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ldarg, i);
            il.Emit(OpCodes.Stelem_Ref);
        }

        var invokeMethod = _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray);
        il.Emit(OpCodes.Callvirt, invokeMethod!);

        // Cast object → Task<object?>, then wrap as Promise
        il.Emit(OpCodes.Castclass, _types.TaskOfObject);
        il.Emit(OpCodes.Call, runtime.WrapTaskAsPromise);

        il.Emit(OpCodes.Ret);

        runtime.DnsPromisesWrapperMethods[methodName] = wrapper;
    }

    /// <summary>
    /// Emits DnsGetPromisesNamespace: creates a Dictionary&lt;string, object?&gt; namespace
    /// with TSFunction entries for each dns/promises method.
    /// </summary>
    private void EmitDnsGetPromisesNamespace(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DnsGetPromisesNamespace",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            Type.EmptyTypes);
        runtime.DnsGetPromisesNamespace = method;

        var il = method.GetILGenerator();

        var dictCtor = _types.DictionaryStringObject.GetConstructor(Type.EmptyTypes)!;
        var addMethod = _types.DictionaryStringObject.GetMethod("Add", [typeof(string), typeof(object)])!;

        il.Emit(OpCodes.Newobj, dictCtor);
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        // Map JS method names → $Runtime wrapper methods
        var methodMap = new (string JsName, string WrapperKey)[]
        {
            ("lookup", "DnsPromisesLookup"),
            ("resolve", "DnsPromisesResolve"),
            ("resolve4", "DnsPromisesResolve4"),
            ("resolve6", "DnsPromisesResolve6"),
            ("reverse", "DnsPromisesReverse"),
            ("resolveMx", "DnsPromisesResolveMx"),
            ("resolveTxt", "DnsPromisesResolveTxt"),
            ("resolveSrv", "DnsPromisesResolveSrv"),
            ("resolveCname", "DnsPromisesResolveCname"),
            ("resolveNs", "DnsPromisesResolveNs"),
            ("resolveSoa", "DnsPromisesResolveSoa"),
            ("resolvePtr", "DnsPromisesResolvePtr"),
            ("resolveCaa", "DnsPromisesResolveCaa"),
            ("resolveNaptr", "DnsPromisesResolveNaptr"),
        };

        foreach (var (jsName, wrapperKey) in methodMap)
        {
            il.Emit(OpCodes.Ldloc, dictLocal);
            il.Emit(OpCodes.Ldstr, jsName);

            // new $TSFunction(null, wrapperMethod)
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldtoken, runtime.DnsPromisesWrapperMethods[wrapperKey]);
            il.Emit(OpCodes.Call, _types.GetMethod(typeof(MethodBase), "GetMethodFromHandle", typeof(RuntimeMethodHandle)));
            il.Emit(OpCodes.Castclass, typeof(MethodInfo));
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);

            il.Emit(OpCodes.Call, addMethod);
        }

        // Wrap in TSObject for proper member access via GetFieldsProperty dispatch
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);

        il.Emit(OpCodes.Ret);
    }
}
