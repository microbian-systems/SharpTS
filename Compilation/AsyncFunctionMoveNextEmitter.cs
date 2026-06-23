using System;
using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// Shared base for the two async-FUNCTION MoveNext emitters (<see cref="AsyncMoveNextEmitter"/> and
/// <see cref="AsyncArrowMoveNextEmitter"/>). Holds the await-aware try/catch and the non-local-exit
/// (return / break / continue) finally-routing machinery (#774) in one place so the two emitters
/// cannot drift. The arrow previously carried its own structured-EH copy of try/catch that emitted
/// invalid IL (a runtime InvalidProgramException) on an <c>await</c> inside a <c>try</c>, and skipped
/// <c>finally</c> on a non-local exit out of such a try, because this machinery was never ported to it.
/// </summary>
/// <remarks>
/// Generators and async generators have additional suspension machinery (yield,
/// <c>generator.return()</c>, throw routing) and are intentionally NOT folded in here.
///
/// The machinery is emitter-agnostic except for three seams, exposed as hooks:
///   * <see cref="DefineStateMachineField"/> — defines a private field on the concrete state-machine
///     type (the routing needs fields that survive a MoveNext re-entry, not IL locals);
///   * <see cref="EmitCompleteWithReturnValueOnStack"/> — completes the state machine using the boxed
///     return value currently on the IL stack (async-fn stores it to its return-value local and leaves
///     to its SetResult label; the arrow's EmitSetResult consumes the stack value);
///   * <see cref="EmitAwaitGetResult"/> — wraps the awaiter's GetResult in the flag-based capture
///     try/catch when emitting inside a try-with-awaits; called by each emitter's await step.
/// </remarks>
public abstract partial class AsyncFunctionMoveNextEmitter : StatementEmitterBase
{
    protected AsyncFunctionMoveNextEmitter(StateMachineEmitHelpers helpers)
        : base(helpers)
    {
    }

    // For try/catch with awaits: where a caught exception is stored, and where to jump after capturing
    // a rejected await (the try-exit, so the remaining try body is abandoned). Both are set together by
    // EmitTryBodyWithAwaits and consulted by the await emission via EmitAwaitGetResult.
    protected LocalBuilder? _currentTryCatchExceptionLocal = null;
    protected Label? _currentTryCatchSkipLabel = null;

    // ---- Emitter-specific seams ----------------------------------------------------------------

    /// <summary>Defines a private field on the concrete state-machine type.</summary>
    protected abstract FieldBuilder DefineStateMachineField(string name, Type type);

    /// <summary>
    /// Completes the state machine using the boxed return value currently on top of the IL stack.
    /// </summary>
    protected abstract void EmitCompleteWithReturnValueOnStack();

    /// <summary>
    /// Emits <c>awaiter.GetResult()</c> (via <paramref name="emitRawGetResultCall"/>), leaving the
    /// result on the stack. Inside a flag-based try-with-awaits (<see cref="_currentTryCatchExceptionLocal"/>
    /// set) the call is wrapped in a try/catch that captures a rejection into the exception local and
    /// branches to the try-exit so the remaining try body is abandoned (rather than resuming it with a
    /// null result); otherwise the bare call is emitted. Shared by both emitters' await step (#774).
    /// </summary>
    protected void EmitAwaitGetResult(Action emitRawGetResultCall)
    {
        if (_currentTryCatchExceptionLocal != null)
        {
            var getResultDoneLabel = IL.DefineLabel();
            var exceptionCaughtLabel = IL.DefineLabel();
            IL.BeginExceptionBlock();
            emitRawGetResultCall();
            var resultTemp = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, resultTemp);
            IL.Emit(OpCodes.Leave, getResultDoneLabel);
            IL.BeginCatchBlock(typeof(Exception));
            IL.Emit(OpCodes.Call, Ctx.Runtime!.WrapException);
            IL.Emit(OpCodes.Stloc, _currentTryCatchExceptionLocal);
            IL.Emit(OpCodes.Ldnull);
            IL.Emit(OpCodes.Stloc, resultTemp);
            IL.Emit(OpCodes.Leave, exceptionCaughtLabel);
            IL.EndExceptionBlock();

            // A rejected awaitable must abandon the rest of the try body, not resume it with a null
            // result (which ran BOTH the success path and the catch). Jump straight to the try-exit so
            // the statement-level catch dispatch in EmitTryCatchWithAwaits sees the exception local.
            IL.MarkLabel(exceptionCaughtLabel);
            if (_currentTryCatchSkipLabel != null)
            {
                IL.Emit(OpCodes.Br, _currentTryCatchSkipLabel.Value);
            }
            // No skip target (shouldn't happen — the local and label are set together): fall through
            // with the null result as before.

            IL.MarkLabel(getResultDoneLabel);
            IL.Emit(OpCodes.Ldloc, resultTemp);
        }
        else
        {
            emitRawGetResultCall();
        }
    }

    // ---- Non-local return with finally routing (#774) ------------------------------------------

    protected override void EmitReturn(Stmt.Return r)
    {
        // Produce the completion value on the stack: the returned expression, or `undefined` for a
        // bare `return;` (resolves the promise with undefined, not null — #587).
        if (r.Value != null)
        {
            EmitExpression(r.Value);
            EnsureBoxed();
        }
        else
        {
            IL.Emit(OpCodes.Ldsfld, Ctx.Runtime!.UndefinedInstance);
        }

        // If a flag-based finally lies between this return and the function boundary, run it (and any
        // outer ones) before completing: stash the value in a state-machine field (an await in the
        // finally would lose an IL local across the suspension), then route through the finally(s). The
        // return terminal reloads the value and completes (#774).
        var chain = ActiveFinallyFrames();
        if (chain.Count > 0)
        {
            var tmp = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, tmp);
            IL.Emit(OpCodes.Ldarg_0);
            IL.Emit(OpCodes.Ldloc, tmp);
            IL.Emit(OpCodes.Stfld, GetPendingReturnValueField());

            RegisterReturnTerminal();
            RouteThroughFinallys(chain, ExitCodeReturn, Ctx.ExceptionBlockDepth > 0 ? OpCodes.Leave : OpCodes.Br);
            return;
        }

        EmitCompleteWithReturnValueOnStack();
    }

    /// <summary>
    /// Branch out to <paramref name="target"/> (a loop's break/continue label). Inside a real IL
    /// exception block a <c>br</c> out is illegal IL (BranchOutOfTry), so use <c>Leave</c> — which exits
    /// the block legally and runs its (no-await) finally. <c>ExceptionBlockDepth</c> counts only real
    /// blocks opened by <see cref="EmitSimpleTryCatch"/>, not the flag-based path's mini try/catch
    /// segments (EmitSegmentInTry), so an escaping break/continue in a try-with-awaits is pulled out as a
    /// segment-breaker (emitted at the top level, depth 0 → <c>Br</c>) while a break targeting a loop
    /// nested inside a segment stays a legal in-segment <c>Br</c>. Mirrors the generator emitters (#727).
    /// </summary>
    protected override void EmitBranchToLabel(Label target)
    {
        if (Ctx.ExceptionBlockDepth > 0)
            IL.Emit(OpCodes.Leave, target);
        else
            IL.Emit(OpCodes.Br, target);
    }
}
