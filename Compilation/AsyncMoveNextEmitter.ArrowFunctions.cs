using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncMoveNextEmitter
{
    protected override void EmitArrowFunction(Expr.ArrowFunction af)
    {
        if (af.IsAsync)
        {
            EmitAsyncArrowFunction(af);
            return;
        }

        if (_ctx!.ArrowMethods == null || !_ctx.ArrowMethods.TryGetValue(af, out var method))
        {
            _il.Emit(OpCodes.Ldnull);
            SetStackUnknown();
            return;
        }

        if (_ctx.DisplayClasses != null && _ctx.DisplayClasses.TryGetValue(af, out var displayClass))
        {
            EmitCapturingArrowFunction(af, method, displayClass);
        }
        else
        {
            EmitNonCapturingArrowFunction(af, method);
        }
    }

    private void EmitAsyncArrowFunction(Expr.ArrowFunction af)
    {
        if (_ctx?.AsyncArrowBuilders == null ||
            !_ctx.AsyncArrowBuilders.TryGetValue(af, out var arrowBuilder))
        {
            throw new CompileException(
                "Async arrow function not registered with state machine builder.");
        }

        if (_builder.SelfBoxedField != null)
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, _builder.SelfBoxedField);
        }
        else
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldobj, _builder.StateMachineType);
            _il.Emit(OpCodes.Box, _builder.StateMachineType);
        }

        _il.Emit(OpCodes.Ldtoken, arrowBuilder.StubMethod);
        _il.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
        _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
        _il.Emit(OpCodes.Newobj, _ctx!.Runtime!.TSFunctionCtor);
        SetStackUnknown();
    }

    private void EmitCapturingArrowFunction(Expr.ArrowFunction af, MethodBuilder method, TypeBuilder displayClass)
    {
        if (_ctx!.DisplayClassConstructors == null || !_ctx.DisplayClassConstructors.TryGetValue(af, out var displayCtor))
        {
            _il.Emit(OpCodes.Ldnull);
            SetStackUnknown();
            return;
        }

        _il.Emit(OpCodes.Newobj, displayCtor);

        if (_ctx.ArrowEntryPointDCFields?.TryGetValue(af, out var entryPointDCField) == true &&
            _ctx.EntryPointDisplayClassStaticField != null)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldsfld, _ctx.EntryPointDisplayClassStaticField);
            _il.Emit(OpCodes.Stfld, entryPointDCField);
        }

        if (_ctx.DisplayClassFields == null || !_ctx.DisplayClassFields.TryGetValue(af, out var fieldMap))
        {
            _il.Emit(OpCodes.Ldtoken, method);
            _il.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
            _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
            _il.Emit(OpCodes.Newobj, _ctx.Runtime!.TSFunctionCtor);
            SetStackUnknown();
            return;
        }

        foreach (var (capturedVar, field) in fieldMap)
        {
            _il.Emit(OpCodes.Dup);

            var hoistedField = _builder.GetVariableField(capturedVar);
            if (hoistedField != null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, hoistedField);
            }
            else if (capturedVar == "this" && _builder.ThisField != null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, _builder.ThisField);
            }
            else if (_ctx.Locals.TryGetLocal(capturedVar, out var local))
            {
                _il.Emit(OpCodes.Ldloc, local);
            }
            else
            {
                _il.Emit(OpCodes.Ldnull);
            }

            _il.Emit(OpCodes.Stfld, field);
        }

        _il.Emit(OpCodes.Ldtoken, method);
        _il.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
        _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
        _il.Emit(OpCodes.Newobj, _ctx.Runtime!.TSFunctionCtor);
        SetStackUnknown();
    }

    private void EmitNonCapturingArrowFunction(Expr.ArrowFunction af, MethodBuilder method)
    {
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Ldtoken, method);
        _il.Emit(OpCodes.Call, typeof(MethodBase).GetMethod(
            "GetMethodFromHandle",
            [typeof(RuntimeMethodHandle)])!);
        _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
        _il.Emit(OpCodes.Newobj, _ctx!.Runtime!.TSFunctionCtor);
        SetStackUnknown();
    }
}
