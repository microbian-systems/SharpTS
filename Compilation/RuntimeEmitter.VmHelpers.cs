using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits vm module helper methods that delegate to VmModuleInterpreter via reflection.
    /// The vm module inherently requires the interpreter at runtime since it compiles
    /// and executes arbitrary strings of code.
    /// </summary>
    private void EmitVmMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitVmRunInNewContext(typeBuilder, runtime);
        EmitVmRunInThisContext(typeBuilder, runtime);
        EmitVmRunInContext(typeBuilder, runtime);
        EmitVmCreateContext(typeBuilder, runtime);
        EmitVmIsContext(typeBuilder, runtime);
        EmitVmCompileFunction(typeBuilder, runtime);
        EmitVmMeasureMemory(typeBuilder, runtime);
        EmitVmGetConstants(typeBuilder, runtime);
        EmitVmGetScriptConstructor(typeBuilder, runtime);
        EmitVmNewScript(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits: public static object VmMeasureMemory(object options)
    /// Fetches the raw measureMemory result dictionary from VmModuleInterpreter via
    /// reflection and wraps it in a native $Promise (a cross-boundary SharpTSPromise is
    /// not unwrapped by compiled await, so we can't return the interpreter's promise).
    /// </summary>
    private void EmitVmMeasureMemory(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "VmMeasureMemory",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]);
        runtime.VmMeasureMemory = method;
        runtime.RegisterBuiltInModuleMethod("vm", "measureMemory", method);

        var il = method.GetILGenerator();

        // Type moduleType = Type.GetType("...VmModuleInterpreter, SharpTS");
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.BuiltIns.Modules.Interpreter.VmModuleInterpreter, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        var typeLocal = il.DeclareLocal(_types.Type);
        il.Emit(OpCodes.Stloc, typeLocal);

        // Standalone (SharpTS absent): return a resolved promise of null.
        var typeOk = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Brtrue, typeOk);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, runtime.TSPromiseResolve);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(typeOk);

        // object dict = moduleType.GetMethod("MeasureMemoryResultObject").Invoke(null, Array.Empty<object>());
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "MeasureMemoryResultObject");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));

        // return $Runtime.Resolve(dict)  →  native $Promise
        il.Emit(OpCodes.Call, runtime.TSPromiseResolve);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object VmRunInContext(object code, object contextifiedObject, object options)
    /// Delegates to VmModuleInterpreter.GetExports()["runInContext"] via reflection.
    /// </summary>
    private void EmitVmRunInContext(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "VmRunInContext",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]);
        runtime.VmRunInContext = method;
        runtime.RegisterBuiltInModuleMethod("vm", "runInContext", method);

        var il = method.GetILGenerator();
        EmitVmReflectionCall(il, "runInContext", 3);
    }

    /// <summary>
    /// Emits: public static object VmGetConstants()
    /// Returns VmModuleInterpreter.GetExports()["constants"] via reflection.
    /// </summary>
    private void EmitVmGetConstants(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "VmGetConstants",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            Type.EmptyTypes);
        runtime.VmGetConstants = method;

        var il = method.GetILGenerator();
        EmitVmGetExportValue(il, "constants");
    }

    /// <summary>
    /// Emits: public static object VmRunInNewContext(object code, object contextObject, object options)
    /// Delegates to VmModuleInterpreter.GetExports()["runInNewContext"] via reflection.
    /// </summary>
    private void EmitVmRunInNewContext(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "VmRunInNewContext",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]);
        runtime.VmRunInNewContext = method;
        runtime.RegisterBuiltInModuleMethod("vm", "runInNewContext", method);

        var il = method.GetILGenerator();
        EmitVmReflectionCall(il, "runInNewContext", 3);
    }

    /// <summary>
    /// Emits: public static object VmRunInThisContext(object code, object options)
    /// Delegates to VmModuleInterpreter.GetExports()["runInThisContext"] via reflection.
    /// </summary>
    private void EmitVmRunInThisContext(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "VmRunInThisContext",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.VmRunInThisContext = method;
        runtime.RegisterBuiltInModuleMethod("vm", "runInThisContext", method);

        var il = method.GetILGenerator();
        EmitVmReflectionCall(il, "runInThisContext", 2);
    }

    /// <summary>
    /// Emits: public static object VmCreateContext(object contextObject)
    /// Delegates to VmModuleInterpreter.GetExports()["createContext"] via reflection.
    /// </summary>
    private void EmitVmCreateContext(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "VmCreateContext",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.VmCreateContext = method;
        runtime.RegisterBuiltInModuleMethod("vm", "createContext", method);

        var il = method.GetILGenerator();
        EmitVmReflectionCall(il, "createContext", 2);
    }

    /// <summary>
    /// Emits: public static object VmIsContext(object obj)
    /// Delegates to VmModuleInterpreter.GetExports()["isContext"] via reflection.
    /// </summary>
    private void EmitVmIsContext(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "VmIsContext",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]);
        runtime.VmIsContext = method;
        runtime.RegisterBuiltInModuleMethod("vm", "isContext", method);

        var il = method.GetILGenerator();
        EmitVmReflectionCall(il, "isContext", 1);
    }

    /// <summary>
    /// Emits: public static object VmCompileFunction(object code, object params, object options)
    /// Delegates to VmModuleInterpreter.GetExports()["compileFunction"] via reflection.
    /// Returns a BuiltInMethod that InvokeMethodValue dispatches via reflection fallback.
    /// </summary>
    private void EmitVmCompileFunction(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "VmCompileFunction",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]);
        runtime.VmCompileFunction = method;
        runtime.RegisterBuiltInModuleMethod("vm", "compileFunction", method);

        var il = method.GetILGenerator();
        EmitVmReflectionCall(il, "compileFunction", 3);
    }

    /// <summary>
    /// Emits: public static object VmGetScriptConstructor()
    /// Delegates to VmModuleInterpreter.GetExports()["Script"] via reflection.
    /// </summary>
    private void EmitVmGetScriptConstructor(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "VmGetScriptConstructor",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            Type.EmptyTypes);
        runtime.VmGetScriptConstructor = method;
        runtime.RegisterBuiltInModuleMethod("vm", "Script", method);

        var il = method.GetILGenerator();
        EmitVmGetExportValue(il, "Script");
    }

    /// <summary>
    /// Emits IL that returns VmModuleInterpreter.GetExports()[exportName] (the raw
    /// exported value — a dict, constructor, or other object — not the result of a Call).
    /// Returns null when SharpTS isn't present at runtime (standalone graceful degradation).
    /// </summary>
    private void EmitVmGetExportValue(ILGenerator il, string exportName)
    {
        // Type moduleType = Type.GetType("SharpTS.Runtime.BuiltIns.Modules.Interpreter.VmModuleInterpreter, SharpTS");
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.BuiltIns.Modules.Interpreter.VmModuleInterpreter, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        var typeLocal = il.DeclareLocal(_types.Type);
        il.Emit(OpCodes.Stloc, typeLocal);

        // If null, return null
        var typeOk = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Brtrue, typeOk);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(typeOk);

        // MethodInfo getExports = moduleType.GetMethod("GetExports");
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "GetExports");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        // object exports = getExports.Invoke(null, Array.Empty<object>());
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));

        // Get exports[exportName] via the dictionary indexer
        var exportsLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, exportsLocal);

        il.Emit(OpCodes.Ldloc, exportsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "Item");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Ldloc, exportsLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldstr, exportName);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object VmNewScript(object code, object options)
    /// Creates a vm.Script object via reflection to VmScriptConstructor.
    /// </summary>
    private void EmitVmNewScript(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "VmNewScript",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.VmNewScript = method;

        var il = method.GetILGenerator();

        // Get the Script constructor: VmModuleInterpreter.GetExports()["Script"]
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.BuiltIns.Modules.Interpreter.VmModuleInterpreter, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        var typeLocal = il.DeclareLocal(_types.Type);
        il.Emit(OpCodes.Stloc, typeLocal);

        var typeOk = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Brtrue, typeOk);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(typeOk);

        // Get exports
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "GetExports");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        var exportsLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, exportsLocal);

        // Get "Script" from exports
        il.Emit(OpCodes.Ldloc, exportsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "Item");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Ldloc, exportsLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldstr, "Script");
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object, _types.ObjectArray));
        var ctorLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, ctorLocal);

        // Build args: new List<object?> { code, options }
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableDefaultCtor);
        var argsLocal = il.DeclareLocal(_types.ListOfObjectNullable);
        il.Emit(OpCodes.Stloc, argsLocal);

        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Ldarg_0); // code
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObjectNullable, "Add", _types.Object));

        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Ldarg_1); // options
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObjectNullable, "Add", _types.Object));

        // Call ctor.GetType().GetMethod("Call").Invoke(ctor, new object[] { null, argsList })
        il.Emit(OpCodes.Ldloc, ctorLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "Call");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        il.Emit(OpCodes.Ldloc, ctorLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldnull); // interpreter = null
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits IL that calls VmModuleInterpreter.GetExports()[methodName] via reflection.
    /// Same pattern as EmitChildProcessReflectionCall.
    /// </summary>
    private void EmitVmReflectionCall(ILGenerator il, string methodName, int argCount)
    {
        // Type moduleType = Type.GetType("SharpTS.Runtime.BuiltIns.Modules.Interpreter.VmModuleInterpreter, SharpTS");
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.BuiltIns.Modules.Interpreter.VmModuleInterpreter, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        var typeLocal = il.DeclareLocal(_types.Type);
        il.Emit(OpCodes.Stloc, typeLocal);

        // If null, return null
        var typeOk = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Brtrue, typeOk);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(typeOk);

        // MethodInfo getExports = moduleType.GetMethod("GetExports");
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "GetExports");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        // object exports = getExports.Invoke(null, Array.Empty<object>());
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        var exportsLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, exportsLocal);

        // Get the method by name: dict[methodName]
        il.Emit(OpCodes.Ldloc, exportsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "Item");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Ldloc, exportsLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldstr, methodName);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object, _types.ObjectArray));
        var builtInLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, builtInLocal);

        // Build args list: new List<object?> { arg0, arg1, ... }
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableDefaultCtor);
        var argsLocal = il.DeclareLocal(_types.ListOfObjectNullable);
        il.Emit(OpCodes.Stloc, argsLocal);

        for (int i = 0; i < argCount; i++)
        {
            il.Emit(OpCodes.Ldloc, argsLocal);
            il.Emit(OpCodes.Ldarg, i);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObjectNullable, "Add", _types.Object));
        }

        // Call builtIn.GetType().GetMethod("Call").Invoke(builtIn, new object[] { null, argsList })
        il.Emit(OpCodes.Ldloc, builtInLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "Call");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        il.Emit(OpCodes.Ldloc, builtInLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldnull); // interpreter = null
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);
    }
}
