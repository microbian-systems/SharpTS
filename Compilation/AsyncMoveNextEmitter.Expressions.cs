using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncMoveNextEmitter
{
    // EmitExpression dispatch is inherited from ExpressionEmitterBase
    // EmitCall and call helpers are inherited from ExpressionEmitterBase.CallHelpers.cs

    protected override void EmitAwait(Expr.Await a)
    {
        // 1. Emit the awaited expression (should produce Task<object> or $Promise)
        EmitExpression(a.Expression);
        EnsureBoxed();

        // 2+. Coerce to Task<object>, suspend/resume, and leave the awaited result on the stack.
        EmitAwaitFromValueOnStack(_currentAwaitState++);
    }

    /// <summary>
    /// Emits the await of a value already on the evaluation stack (boxed): coerces it to
    /// <c>Task&lt;object&gt;</c> (unwrapping $Promise / adopting thenables / wrapping plain values),
    /// suspends the state machine until it settles, and leaves the awaited result on the stack.
    /// Shared by <see cref="EmitAwait"/> and the <c>for await…of</c> loop's implicit next()/return()
    /// awaits (#631); <paramref name="stateNumber"/> is the reserved suspension state for this await.
    /// </summary>
    internal void EmitAwaitFromValueOnStack(int stateNumber)
    {
        var resumeLabel = _stateLabels[stateNumber];
        var continueLabel = _il.DefineLabel();
        var awaiterField = _builder.AwaiterFields[stateNumber];

        // 2. Convert to Task<object> - handle $Promise, Task<object>, or non-Task values
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
        // Adopt an ordinary thenable (e.g. a general non-Promise then/catch/finally
        // species result, #349); non-thenables become Task.FromResult(value).
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.CoerceAwaitableToTaskMethod);
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

        // 3. Get awaiter: task.GetAwaiter()
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
        _il.Emit(OpCodes.Ldc_I4, stateNumber);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // Mirror live spill temps to fields before AwaitUnsafeOnCompleted boxes the state
        // machine: IL locals do not survive the MoveNext re-entry, and writes after the box
        // would not reach the continuation's snapshot (#400). Suspending path only.
        _helpers.PersistLiveSpillsBeforeSuspend();

        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldflda, _builder.BuilderField);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldflda, awaiterField);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Call, _builder.GetBuilderAwaitUnsafeOnCompletedMethod());

        _il.Emit(OpCodes.Leave, _endLabel);

        // 7. Resume point
        _il.MarkLabel(resumeLabel);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4_M1);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // Restore spill temps from their fields — only on the resumed path; the
        // synchronously-completed path (below) never persisted and keeps its locals.
        _helpers.RehydrateLiveSpillsAfterResume();

        // 8. Continue point
        _il.MarkLabel(continueLabel);

        // 9. Get result
        if (_currentTryCatchExceptionLocal != null)
        {
            var getResultDoneLabel = _il.DefineLabel();
            var exceptionCaughtLabel = _il.DefineLabel();
            _il.BeginExceptionBlock();
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldflda, awaiterField);
            _il.Emit(OpCodes.Call, _builder.GetAwaiterGetResultMethod());
            var resultTemp = _il.DeclareLocal(typeof(object));
            _il.Emit(OpCodes.Stloc, resultTemp);
            _il.Emit(OpCodes.Leave, getResultDoneLabel);
            _il.BeginCatchBlock(typeof(Exception));
            _il.Emit(OpCodes.Call, _ctx!.Runtime!.WrapException);
            _il.Emit(OpCodes.Stloc, _currentTryCatchExceptionLocal);
            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Stloc, resultTemp);
            _il.Emit(OpCodes.Leave, exceptionCaughtLabel);
            _il.EndExceptionBlock();

            // A rejected awaitable must abandon the rest of the try body, not
            // resume it with a null result (which ran BOTH the success path and
            // the catch). Jump straight to the try-exit so the statement-level
            // catch dispatch in EmitTryCatchWithAwaits sees the exception local.
            _il.MarkLabel(exceptionCaughtLabel);
            if (_currentTryCatchSkipLabel != null)
            {
                _il.Emit(OpCodes.Br, _currentTryCatchSkipLabel.Value);
            }
            // No skip target (shouldn't happen — the local and label are set
            // together): fall through with the null result as before.

            _il.MarkLabel(getResultDoneLabel);
            _il.Emit(OpCodes.Ldloc, resultTemp);
        }
        else
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldflda, awaiterField);
            _il.Emit(OpCodes.Call, _builder.GetAwaiterGetResultMethod());
        }

        SetStackUnknown();
    }

}
