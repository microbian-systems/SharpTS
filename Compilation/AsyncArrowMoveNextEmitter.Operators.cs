using System.Reflection.Emit;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncArrowMoveNextEmitter
{
    // EmitCall and call helpers inherited from ExpressionEmitterBase.CallHelpers.cs

    protected override void EmitAwait(Expr.Await aw)
    {
        int stateNum = _currentState++;
        var continueLabel = _il.DefineLabel();

        if (!_builder.AwaiterFields.TryGetValue(stateNum, out var awaiterField))
        {
            throw new CompileException($"No awaiter field found for state {stateNum}");
        }

        // 1. Emit the awaited expression
        EmitExpression(aw.Expression);
        EnsureBoxed();

        // 2. Convert to Task<object>
        var taskLocal = _il.DeclareLocal(typeof(Task<object>));
        var isPromiseLabel = _il.DefineLabel();
        var isTaskLabel = _il.DefineLabel();
        var wrapValueLabel = _il.DefineLabel();
        var haveTaskLabel = _il.DefineLabel();

        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Isinst, _ctx!.Runtime!.TSPromiseType);
        _il.Emit(OpCodes.Brtrue, isPromiseLabel);

        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Isinst, typeof(Task<object>));
        _il.Emit(OpCodes.Brtrue, isTaskLabel);

        _il.MarkLabel(wrapValueLabel);
        _il.Emit(OpCodes.Call, typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(typeof(object)));
        _il.Emit(OpCodes.Stloc, taskLocal);
        _il.Emit(OpCodes.Br, haveTaskLabel);

        _il.MarkLabel(isTaskLabel);
        _il.Emit(OpCodes.Castclass, typeof(Task<object>));
        _il.Emit(OpCodes.Stloc, taskLocal);
        _il.Emit(OpCodes.Br, haveTaskLabel);

        _il.MarkLabel(isPromiseLabel);
        _il.Emit(OpCodes.Castclass, _ctx.Runtime.TSPromiseType);
        _il.Emit(OpCodes.Callvirt, _ctx.Runtime.TSPromiseTaskGetter);
        _il.Emit(OpCodes.Stloc, taskLocal);

        _il.MarkLabel(haveTaskLabel);
        _il.Emit(OpCodes.Ldloc, taskLocal);

        // 3. Get awaiter
        _il.Emit(OpCodes.Call, _builder.GetTaskGetAwaiterMethod());

        // 4. Store awaiter to field
        var awaiterLocal = _il.DeclareLocal(_builder.AwaiterType);
        _il.Emit(OpCodes.Stloc, awaiterLocal);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, awaiterLocal);
        _il.Emit(OpCodes.Stfld, awaiterField);

        // 5. Check IsCompleted
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldflda, awaiterField);
        _il.Emit(OpCodes.Call, _builder.GetAwaiterIsCompletedGetter());
        _il.Emit(OpCodes.Brtrue, continueLabel);

        // 6. Not completed - suspend
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, stateNum);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldflda, _builder.BuilderField);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldflda, awaiterField);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Call, _builder.GetBuilderAwaitUnsafeOnCompletedMethod());

        _il.Emit(OpCodes.Leave, _exitLabel);

        // 7. Resume point
        if (stateNum < _stateLabels.Count)
            _il.MarkLabel(_stateLabels[stateNum]);

        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4_M1);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // 8. Continue point
        _il.MarkLabel(continueLabel);

        // 9. Get result
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldflda, awaiterField);
        _il.Emit(OpCodes.Call, _builder.GetAwaiterGetResultMethod());

        SetStackUnknown();
    }
}
