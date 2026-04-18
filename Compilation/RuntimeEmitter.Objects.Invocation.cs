using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    private void EmitGetArrayMethod(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // GetArrayMethod(object arr, string methodName) -> TSFunction or null
        // Maps TypeScript array method names to .NET List methods
        var method = typeBuilder.DefineMethod(
            "GetArrayMethod",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.String]
        );
        runtime.GetArrayMethod = method;

        var il = method.GetILGenerator();
        var nullLabel = il.DefineLabel();
        var notArrayLabel = il.DefineLabel();

        // Check if obj is List<object>
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, nullLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brfalse, notArrayLabel);

        // Map TypeScript method name to .NET method name
        // push -> Add, pop -> RemoveAt(Count-1), etc.
        var pushLabel = il.DefineLabel();
        var popLabel = il.DefineLabel();
        var shiftLabel = il.DefineLabel();

        // Check for "push"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "push");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, pushLabel);

        // Check for "pop"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "pop");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, popLabel);

        // Unknown array method - return null
        il.Emit(OpCodes.Br, nullLabel);

        // Handle push - wrap List.Add as TSFunction
        il.MarkLabel(pushLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));
        il.Emit(OpCodes.Ldtoken, _types.ListOfObject);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);

        // Handle pop - need special handling since pop returns removed element
        il.MarkLabel(popLabel);
        // For pop, we'll create a TSFunction that wraps a helper method
        // For now, return null and handle pop differently
        il.Emit(OpCodes.Br, nullLabel);

        il.MarkLabel(notArrayLabel);
        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    private void EmitInvokeValue(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "InvokeValue",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.ObjectArray]
        );
        runtime.InvokeValue = method;

        var il = method.GetILGenerator();
        var nullLabel = il.DefineLabel();
        var tsFunctionLabel = il.DefineLabel();
        var boundTsFunctionLabel = il.DefineLabel();
        var bindWrapperLabel = il.DefineLabel();
        var callWrapperLabel = il.DefineLabel();
        var applyWrapperLabel = il.DefineLabel();
        var deprecatedLabel = il.DefineLabel();
        var promisifiedLabel = il.DefineLabel();
        var textDecoderDecodeLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, nullLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, tsFunctionLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Brtrue, boundTsFunctionLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.FunctionBindWrapperType);
        il.Emit(OpCodes.Brtrue, bindWrapperLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.FunctionCallWrapperType);
        il.Emit(OpCodes.Brtrue, callWrapperLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.FunctionApplyWrapperType);
        il.Emit(OpCodes.Brtrue, applyWrapperLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSDeprecatedFunctionType);
        il.Emit(OpCodes.Brtrue, deprecatedLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSPromisifiedFunctionType);
        il.Emit(OpCodes.Brtrue, promisifiedLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSTextDecoderDecodeMethodType);
        il.Emit(OpCodes.Brtrue, textDecoderDecodeLabel);

        var callbackifiedLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSCallbackifiedFunctionType);
        il.Emit(OpCodes.Brtrue, callbackifiedLabel);

        var transformCbLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TransformDoneCallbackType);
        il.Emit(OpCodes.Brtrue, transformCbLabel);

        var writeCallbackWrapperLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.WriteCallbackWrapperType);
        il.Emit(OpCodes.Brtrue, writeCallbackWrapperLabel);

        var resolveCallbackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.PromiseResolveCallbackType);
        il.Emit(OpCodes.Brtrue, resolveCallbackLabel);

        var rejectCallbackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.PromiseRejectCallbackType);
        il.Emit(OpCodes.Brtrue, rejectCallbackLabel);

        var promisifyCallbackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSPromisifyCallbackType);
        il.Emit(OpCodes.Brtrue, promisifyCallbackLabel);

        var boundArrayMethodLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.BoundArrayMethodType);
        il.Emit(OpCodes.Brtrue, boundArrayMethodLabel);

        var boundMapMethodLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.BoundMapMethodType);
        il.Emit(OpCodes.Brtrue, boundMapMethodLabel);

        var boundSetMethodLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.BoundSetMethodType);
        il.Emit(OpCodes.Brtrue, boundSetMethodLabel);

        // Handle Func<object?[], object?> (from CreateBoundMethod in RuntimeTypes.Methods)
        var funcDelegateLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.FuncObjectArrayToObject);
        il.Emit(OpCodes.Brtrue, funcDelegateLabel);

        // Handle $MethodCallable
        var methodCallableLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.MethodCallableType);
        il.Emit(OpCodes.Brtrue, methodCallableLabel);

        // Proxy check: uses obj.GetType().FullName comparison (no SharpTS.dll dependency)
        var notProxyLabel = il.DefineLabel();
        EmitProxyInvokeCheck(il, () => il.Emit(OpCodes.Ldarg_0), () => il.Emit(OpCodes.Ldarg_1), notProxyLabel);

        il.MarkLabel(notProxyLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(tsFunctionLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(boundTsFunctionLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.BoundTSFunctionInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(bindWrapperLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.FunctionBindWrapperType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.FunctionBindWrapperInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(callWrapperLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.FunctionCallWrapperType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.FunctionCallWrapperInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(applyWrapperLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.FunctionApplyWrapperType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.FunctionApplyWrapperInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(deprecatedLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSDeprecatedFunctionType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSDeprecatedFunctionInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(promisifiedLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSPromisifiedFunctionType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSPromisifiedFunctionInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(textDecoderDecodeLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSTextDecoderDecodeMethodType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSTextDecoderDecodeMethodInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(callbackifiedLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSCallbackifiedFunctionType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSCallbackifiedFunctionInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(transformCbLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TransformDoneCallbackType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TransformDoneCallbackInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(writeCallbackWrapperLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.WriteCallbackWrapperType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.WriteCallbackWrapperInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(resolveCallbackLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.PromiseResolveCallbackType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.PromiseResolveCallbackInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(rejectCallbackLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.PromiseRejectCallbackType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.PromiseRejectCallbackInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(promisifyCallbackLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSPromisifyCallbackType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSPromisifyCallbackInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(boundArrayMethodLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.BoundArrayMethodType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.BoundArrayMethodInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(boundMapMethodLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.BoundMapMethodType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.BoundMapMethodInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(boundSetMethodLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.BoundSetMethodType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.BoundSetMethodInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(funcDelegateLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.FuncObjectArrayToObject);
        il.Emit(OpCodes.Ldarg_1);  // args
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.FuncObjectArrayToObject, "Invoke", _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(methodCallableLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.MethodCallableType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.MethodCallableInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    private void EmitInvokeMethodValue(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "InvokeMethodValue",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.ObjectArray]  // receiver, function, args
        );
        runtime.InvokeMethodValue = method;

        var il = method.GetILGenerator();
        // Check if value is $TSFunction and call InvokeWithThis
        // arg0 = receiver, arg1 = function, arg2 = args
        var nullLabel = il.DefineLabel();
        var notTSFunctionLabel = il.DefineLabel();

        // if (function == null) return null
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, nullLabel);

        // if (function is $TSFunction tsFunc)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, notTSFunctionLabel);

        // return tsFunc.InvokeWithThis(receiver, args)
        il.Emit(OpCodes.Ldarg_0);  // receiver
        il.Emit(OpCodes.Ldarg_2);  // args
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvokeWithThis);
        il.Emit(OpCodes.Ret);

        // Not a TSFunction - handle known callable wrappers without calling InvokeValue
        il.MarkLabel(notTSFunctionLabel);
        il.Emit(OpCodes.Pop);  // Pop the null from isinst
        var notBoundLabel = il.DefineLabel();
        var notBindWrapperLabel = il.DefineLabel();
        var notCallWrapperLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Brfalse, notBoundLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.BoundTSFunctionInvokeWithThis);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notBoundLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.FunctionBindWrapperType);
        il.Emit(OpCodes.Brfalse, notBindWrapperLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.FunctionBindWrapperType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.FunctionBindWrapperInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notBindWrapperLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.FunctionCallWrapperType);
        il.Emit(OpCodes.Brfalse, notCallWrapperLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.FunctionCallWrapperType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.FunctionCallWrapperInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notCallWrapperLabel);
        var notApplyWrapperLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.FunctionApplyWrapperType);
        il.Emit(OpCodes.Brfalse, notApplyWrapperLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.FunctionApplyWrapperType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.FunctionApplyWrapperInvoke);
        il.Emit(OpCodes.Ret);

        // Check $DeprecatedFunction
        il.MarkLabel(notApplyWrapperLabel);
        var notDeprecatedLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSDeprecatedFunctionType);
        il.Emit(OpCodes.Brfalse, notDeprecatedLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.TSDeprecatedFunctionType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.TSDeprecatedFunctionInvoke);
        il.Emit(OpCodes.Ret);

        // Check $PromisifiedFunction
        il.MarkLabel(notDeprecatedLabel);
        var notPromisifiedLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSPromisifiedFunctionType);
        il.Emit(OpCodes.Brfalse, notPromisifiedLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.TSPromisifiedFunctionType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.TSPromisifiedFunctionInvoke);
        il.Emit(OpCodes.Ret);

        // Check $CallbackifiedFunction
        il.MarkLabel(notPromisifiedLabel);
        var notCallbackifiedLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSCallbackifiedFunctionType);
        il.Emit(OpCodes.Brfalse, notCallbackifiedLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.TSCallbackifiedFunctionType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.TSCallbackifiedFunctionInvoke);
        il.Emit(OpCodes.Ret);

        // Check $TextDecoderDecodeMethod
        il.MarkLabel(notCallbackifiedLabel);
        var notTextDecoderDecodeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSTextDecoderDecodeMethodType);
        il.Emit(OpCodes.Brfalse, notTextDecoderDecodeLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.TSTextDecoderDecodeMethodType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.TSTextDecoderDecodeMethodInvoke);
        il.Emit(OpCodes.Ret);

        // Check $TransformDoneCallback
        il.MarkLabel(notTextDecoderDecodeLabel);
        var notTransformCbLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TransformDoneCallbackType);
        il.Emit(OpCodes.Brfalse, notTransformCbLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.TransformDoneCallbackType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.TransformDoneCallbackInvoke);
        il.Emit(OpCodes.Ret);

        // Check $WriteCallbackWrapper
        il.MarkLabel(notTransformCbLabel);
        var notWriteCallbackWrapperLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.WriteCallbackWrapperType);
        il.Emit(OpCodes.Brfalse, notWriteCallbackWrapperLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.WriteCallbackWrapperType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.WriteCallbackWrapperInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notWriteCallbackWrapperLabel);

        // Check $PromiseResolveCallback
        var notResolveCallbackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.PromiseResolveCallbackType);
        il.Emit(OpCodes.Brfalse, notResolveCallbackLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.PromiseResolveCallbackType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.PromiseResolveCallbackInvoke);
        il.Emit(OpCodes.Ret);

        // Check $PromiseRejectCallback
        il.MarkLabel(notResolveCallbackLabel);
        var notRejectCallbackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.PromiseRejectCallbackType);
        il.Emit(OpCodes.Brfalse, notRejectCallbackLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.PromiseRejectCallbackType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.PromiseRejectCallbackInvoke);
        il.Emit(OpCodes.Ret);

        // Check $PromisifyCallback
        il.MarkLabel(notRejectCallbackLabel);
        var notPromisifyCallbackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSPromisifyCallbackType);
        il.Emit(OpCodes.Brfalse, notPromisifyCallbackLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.TSPromisifyCallbackType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.TSPromisifyCallbackInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notPromisifyCallbackLabel);

        // Check $BoundArrayMethod
        var notBoundArrayMethodLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.BoundArrayMethodType);
        il.Emit(OpCodes.Brfalse, notBoundArrayMethodLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.BoundArrayMethodType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.BoundArrayMethodInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notBoundArrayMethodLabel);

        // Check $BoundMapMethod
        var notBoundMapMethodLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.BoundMapMethodType);
        il.Emit(OpCodes.Brfalse, notBoundMapMethodLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.BoundMapMethodType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.BoundMapMethodInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notBoundMapMethodLabel);

        // Check $BoundSetMethod
        var notBoundSetMethodLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.BoundSetMethodType);
        il.Emit(OpCodes.Brfalse, notBoundSetMethodLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.BoundSetMethodType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.BoundSetMethodInvoke);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notBoundSetMethodLabel);

        // Handle $MethodCallable (wraps BuiltInMethod from GetMember)
        il.MarkLabel(nullLabel);
        var notMethodCallableLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.MethodCallableType);
        il.Emit(OpCodes.Brfalse, notMethodCallableLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.MethodCallableType);
        il.Emit(OpCodes.Ldarg_2);  // args
        il.Emit(OpCodes.Callvirt, runtime.MethodCallableInvoke);
        il.Emit(OpCodes.Ret);

        // Handle Func<object?[], object?> (from CreateBoundMethod in RuntimeTypes.Methods)
        il.MarkLabel(notMethodCallableLabel);
        var notFuncLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.FuncObjectArrayToObject);
        il.Emit(OpCodes.Brfalse, notFuncLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.FuncObjectArrayToObject);
        il.Emit(OpCodes.Ldarg_2);  // args
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.FuncObjectArrayToObject, "Invoke", _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        // Proxy apply trap check: if function is a proxy, call TrapApply
        // TrapApply expects List<object?> so convert the object[] args
        il.MarkLabel(notFuncLabel);
        var notProxyLabel2 = il.DefineLabel();
        EmitProxyInvokeCheck(il, () => il.Emit(OpCodes.Ldarg_1), () =>
        {
            il.Emit(OpCodes.Ldarg_2); // object[] args
            il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor([typeof(IEnumerable<object>)])!);
        }, notProxyLabel2);

        il.MarkLabel(notProxyLabel2);

        // Fallback: reflection-based dispatch for SharpTS runtime callables (e.g., BuiltInMethod from vm.compileFunction).
        // Check if function has a "Call" method matching (Interpreter, List<object?>) signature.
        var noCallMethodLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, noCallMethodLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "Call");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        var callMiLocal = il.DeclareLocal(typeof(MethodInfo));
        il.Emit(OpCodes.Stloc, callMiLocal);
        il.Emit(OpCodes.Ldloc, callMiLocal);
        il.Emit(OpCodes.Brfalse, noCallMethodLabel);

        // Call(interpreter=null, args=new List<object?>(object[]))
        il.Emit(OpCodes.Ldloc, callMiLocal);
        il.Emit(OpCodes.Ldarg_1); // the callable object
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldnull); // interpreter = null
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        // Convert object[] to List<object?>
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableCtor_IEnumerable);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(noCallMethodLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetSuperMethod(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // GetSuperMethod(object instance, string methodName) -> object
        // Finds a method on the parent class using .NET type hierarchy (BaseType)
        // and wraps it in a $TSFunction for invocation.
        var method = typeBuilder.DefineMethod(
            "GetSuperMethod",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.String]
        );
        runtime.GetSuperMethod = method;

        var il = method.GetILGenerator();
        var baseTypeLocal = il.DeclareLocal(_types.Type);
        var methodInfoLocal = il.DeclareLocal(typeof(MethodInfo));
        var nullLabel = il.DefineLabel();
        var loopLabel = il.DefineLabel();
        var foundLabel = il.DefineLabel();

        // Check if instance is null
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, nullLabel);

        // Get instance.GetType().BaseType
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "GetType"));
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.Type, "BaseType").GetGetMethod()!);
        il.Emit(OpCodes.Stloc, baseTypeLocal);

        // Walk up the type hierarchy looking for the method
        il.MarkLabel(loopLabel);

        // If baseType is null or System.Object, return null
        il.Emit(OpCodes.Ldloc, baseTypeLocal);
        il.Emit(OpCodes.Brfalse, nullLabel);
        il.Emit(OpCodes.Ldloc, baseTypeLocal);
        il.Emit(OpCodes.Ldtoken, _types.Object);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Brtrue, nullLabel);

        // Try to get method: baseType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        il.Emit(OpCodes.Ldloc, baseTypeLocal);
        il.Emit(OpCodes.Ldarg_1); // methodName
        il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, typeof(BindingFlags)));
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // If method found, wrap in $TSFunction and return
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Brtrue, foundLabel);

        // Method not found at this level - try parent
        il.Emit(OpCodes.Ldloc, baseTypeLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.Type, "BaseType").GetGetMethod()!);
        il.Emit(OpCodes.Stloc, baseTypeLocal);
        il.Emit(OpCodes.Br, loopLabel);

        // Found the method - wrap in $TSFunction(instance, methodInfo)
        il.MarkLabel(foundLabel);
        il.Emit(OpCodes.Ldarg_0); // instance (target for the bound method)
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }
}

