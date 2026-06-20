using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncMoveNextEmitter
{
    protected override void EmitReturn(Stmt.Return r)
    {
        if (r.Value != null)
        {
            EmitExpression(r.Value);
            EnsureBoxed();
            if (_returnValueLocal != null)
                _il.Emit(OpCodes.Stloc, _returnValueLocal);
        }
        else if (_returnValueLocal != null)
        {
            // Bare `return;` resolves the promise with `undefined`, not null (#587) — mirrors
            // the generator GeneratorMoveNextEmitter.EmitReturn else. A genuine `return null;`
            // takes the branch above and still resolves with null.
            _il.Emit(OpCodes.Ldsfld, _ctx!.Runtime!.UndefinedInstance);
            _il.Emit(OpCodes.Stloc, _returnValueLocal);
        }

        // If a flag-based finally lies between this return and the function boundary, run it (and any
        // outer ones) before completing: stash the return value in a state-machine field (an await in
        // the finally would lose the IL local across the suspension), then route through the finally(s).
        // The return terminal restores the value into _returnValueLocal and leaves to SetResult (#774).
        var chain = ActiveFinallyFrames();
        if (chain.Count > 0)
        {
            if (_returnValueLocal != null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldloc, _returnValueLocal);
                _il.Emit(OpCodes.Stfld, GetPendingReturnValueField());
            }

            RegisterReturnTerminal();
            RouteThroughFinallys(chain, ExitCodeReturn, _ctx!.ExceptionBlockDepth > 0 ? OpCodes.Leave : OpCodes.Br);
            return;
        }

        // Jump to set result
        _il.Emit(OpCodes.Leave, _setResultLabel);
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
        if (_ctx!.ExceptionBlockDepth > 0)
            _il.Emit(OpCodes.Leave, target);
        else
            _il.Emit(OpCodes.Br, target);
    }

    // EmitIf: inherited from StatementEmitterBase (identical logic)
}
