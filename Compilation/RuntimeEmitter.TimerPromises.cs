using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits timers/promises support methods into the $Runtime class.
/// Uses Task.Delay for non-blocking promise-based timers with event loop ref counting.
/// </summary>
public partial class RuntimeEmitter
{
    private TypeBuilder _timerPromiseClosureType = null!;
    private FieldBuilder _timerPromiseClosureValue = null!;
    private ConstructorBuilder _timerPromiseClosureCtor = null!;
    private MethodBuilder _timerPromiseClosureOnComplete = null!;

    private void EmitTimerPromisesMethods(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var moduleBuilder = runtimeType.Module as ModuleBuilder ?? throw new Exception("need ModuleBuilder");
        EmitTimerPromiseClosure(moduleBuilder, runtime);
        EmitSetTimeoutPromise(runtimeType, runtime);
        EmitSetImmediatePromise(runtimeType, runtime);
    }

    /// <summary>
    /// Emits $TimerPromiseClosure display class.
    /// Captures the resolved value and handles EventLoop.Unref on completion.
    /// </summary>
    private void EmitTimerPromiseClosure(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        _timerPromiseClosureType = moduleBuilder.DefineType(
            "$TimerPromiseClosure",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit);

        _timerPromiseClosureValue = _timerPromiseClosureType.DefineField(
            "Value", _types.Object, FieldAttributes.Public);

        _timerPromiseClosureCtor = _timerPromiseClosureType.DefineConstructor(
            MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        {
            var il = _timerPromiseClosureCtor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Ret);
        }

        // OnComplete(Task t) → EventLoop.Unref(); return Value;
        _timerPromiseClosureOnComplete = _timerPromiseClosureType.DefineMethod(
            "OnComplete",
            MethodAttributes.Public,
            _types.Object,
            [_types.Task]);
        {
            var il = _timerPromiseClosureOnComplete.GetILGenerator();

            // EventLoop.GetInstance().Unref();
            il.Emit(OpCodes.Call, runtime.EventLoopGetInstance);
            il.Emit(OpCodes.Call, runtime.EventLoopUnref);

            // return this.Value;
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, _timerPromiseClosureValue);
            il.Emit(OpCodes.Ret);
        }

        _timerPromiseClosureType.CreateType();
    }

    /// <summary>
    /// Emits: public static object SetTimeoutPromise(double delay, object? value)
    /// Creates a promise that resolves with value after delay ms.
    /// </summary>
    private void EmitSetTimeoutPromise(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "SetTimeoutPromise",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Double, _types.Object]);
        runtime.SetTimeoutPromise = method;

        var il = method.GetILGenerator();

        // int delayMs = Math.Max(0, (int)delay);
        var delayMsLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0); // delay
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Math, "Max", _types.Int32, _types.Int32));
        il.Emit(OpCodes.Stloc, delayMsLocal);

        // EventLoop.Ref() — keep event loop alive during delay
        il.Emit(OpCodes.Call, runtime.EventLoopGetInstance);
        il.Emit(OpCodes.Call, runtime.EventLoopRef);

        // var closure = new $TimerPromiseClosure();
        var closureLocal = il.DeclareLocal(_timerPromiseClosureType);
        il.Emit(OpCodes.Newobj, _timerPromiseClosureCtor);
        il.Emit(OpCodes.Stloc, closureLocal);

        // closure.Value = value;
        il.Emit(OpCodes.Ldloc, closureLocal);
        il.Emit(OpCodes.Ldarg_1); // value
        il.Emit(OpCodes.Stfld, _timerPromiseClosureValue);

        // Task.Delay(delayMs)
        il.Emit(OpCodes.Ldloc, delayMsLocal);
        il.Emit(OpCodes.Call, typeof(Task).GetMethod("Delay", [typeof(int)])!);

        // .ContinueWith<object?>(closure.OnComplete)
        il.Emit(OpCodes.Ldloc, closureLocal);
        il.Emit(OpCodes.Ldftn, _timerPromiseClosureOnComplete);
        il.Emit(OpCodes.Newobj, typeof(Func<Task, object?>).GetConstructors()[0]);
        il.Emit(OpCodes.Call,
            typeof(Task).GetMethod("ContinueWith", 1,
                [typeof(Func<,>).MakeGenericType(typeof(Task), Type.MakeGenericMethodParameter(0))])!
            .MakeGenericMethod(typeof(object)));

        // WrapTaskAsPromise(task)
        il.Emit(OpCodes.Call, runtime.WrapTaskAsPromise);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object SetImmediatePromise(object? value)
    /// Creates a promise that resolves with value on the next tick.
    /// </summary>
    private void EmitSetImmediatePromise(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        var method = runtimeType.DefineMethod(
            "SetImmediatePromise",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]);
        runtime.SetImmediatePromise = method;

        var il = method.GetILGenerator();

        // SetTimeoutPromise(0.0, value)
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Ldarg_0); // value
        il.Emit(OpCodes.Call, runtime.SetTimeoutPromise);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits module method wrappers for timers/promises (used for named/namespace imports).
    /// Each wrapper takes object[] args and dispatches to the actual runtime method.
    /// </summary>
    private void EmitTimerPromisesModuleWrappers(TypeBuilder runtimeType, EmittedRuntime runtime)
    {
        // setTimeout wrapper: object SetTimeoutPromiseWrapper(object[] args)
        {
            var method = runtimeType.DefineMethod(
                "SetTimeoutPromiseWrapper",
                MethodAttributes.Public | MethodAttributes.Static,
                _types.Object,
                [_types.ObjectArray]);

            var il = method.GetILGenerator();

            // delay = args.Length > 0 ? (double)args[0] : 0.0
            var hasDelay = il.DefineLabel();
            var afterDelay = il.DefineLabel();
            var delayLocal = il.DeclareLocal(_types.Double);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Bgt_S, hasDelay);
            il.Emit(OpCodes.Ldc_R8, 0.0);
            il.Emit(OpCodes.Stloc, delayLocal);
            il.Emit(OpCodes.Br_S, afterDelay);

            il.MarkLabel(hasDelay);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldelem_Ref);
            il.Emit(OpCodes.Unbox_Any, _types.Double);
            il.Emit(OpCodes.Stloc, delayLocal);

            il.MarkLabel(afterDelay);

            // value = args.Length > 1 ? args[1] : null
            var hasValue = il.DefineLabel();
            var afterValue = il.DefineLabel();
            var valueLocal = il.DeclareLocal(_types.Object);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Bgt_S, hasValue);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Stloc, valueLocal);
            il.Emit(OpCodes.Br_S, afterValue);

            il.MarkLabel(hasValue);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldelem_Ref);
            il.Emit(OpCodes.Stloc, valueLocal);

            il.MarkLabel(afterValue);

            // Call SetTimeoutPromise(delay, value)
            il.Emit(OpCodes.Ldloc, delayLocal);
            il.Emit(OpCodes.Ldloc, valueLocal);
            il.Emit(OpCodes.Call, runtime.SetTimeoutPromise);
            il.Emit(OpCodes.Ret);

            runtime.RegisterBuiltInModuleMethod("timers/promises", "setTimeout", method);
        }

        // setImmediate wrapper: object SetImmediatePromiseWrapper(object[] args)
        {
            var method = runtimeType.DefineMethod(
                "SetImmediatePromiseWrapper",
                MethodAttributes.Public | MethodAttributes.Static,
                _types.Object,
                [_types.ObjectArray]);

            var il = method.GetILGenerator();

            // value = args.Length > 0 ? args[0] : null
            var hasValue = il.DefineLabel();
            var afterValue = il.DefineLabel();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Bgt_S, hasValue);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Br_S, afterValue);

            il.MarkLabel(hasValue);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldelem_Ref);

            il.MarkLabel(afterValue);

            // Call SetImmediatePromise(value)
            il.Emit(OpCodes.Call, runtime.SetImmediatePromise);
            il.Emit(OpCodes.Ret);

            runtime.RegisterBuiltInModuleMethod("timers/promises", "setImmediate", method);
        }

        // setInterval wrapper: reuse setTimeout wrapper (simplified)
        {
            var method = runtimeType.DefineMethod(
                "SetIntervalPromiseWrapper",
                MethodAttributes.Public | MethodAttributes.Static,
                _types.Object,
                [_types.ObjectArray]);

            var il = method.GetILGenerator();

            // Delegate to SetTimeoutPromiseWrapper
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, runtime.GetBuiltInModuleMethod("timers/promises", "setTimeout")!);
            il.Emit(OpCodes.Ret);

            runtime.RegisterBuiltInModuleMethod("timers/promises", "setInterval", method);
        }
    }
}
