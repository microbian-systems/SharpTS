using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public abstract partial class AsyncFunctionMoveNextEmitter
{
    // ---- Unified exit-scope stack (#774, the async-function analog of #500/#559) ----------------
    //
    // A non-local exit (return / break / continue) that crosses a flag-based try/finally must run the
    // enclosing finally(s) before transferring control. Because a finally can itself await, the routing
    // state has to survive a MoveNext re-entry, so it lives in state-machine fields rather than IL
    // locals.
    //
    // This mirrors the async generator's routing (AsyncGeneratorMoveNextEmitter.Statements.TryCatch.cs,
    // itself the async analog of GeneratorMoveNextEmitter), pared down for the plain async function:
    //   * there are no yields and no external generator.return(), so there is no <>pendingReturnValue
    //     vs Current juggling and no __returnRequested mechanism;
    //   * a `throw` in a try body is already captured by the existing flag-based exception path
    //     (caughtExceptionLocal, which runs the finally before rethrowing), so throw routing is NOT
    //     ported — only return/break/continue need the finally machinery here.
    //
    // The Leave-vs-Br choice keys off CompilationContext.ExceptionBlockDepth (the count of real IL
    // exception blocks opened by EmitSimpleTryCatch around the current point), exactly as the existing
    // EmitBranchToLabel does — escaping break/continue/return are pulled out as segment breakers and
    // emitted at the top level (depth 0 → Br), while one nested inside a no-await simple try leaves it
    // via Leave, running that real finally on the way to the innermost flag cleanup.

    private interface IExitScope;

    /// <summary>A loop (or switch / labeled statement) that <c>break</c>/<c>continue</c> can target.</summary>
    private sealed class LoopScope : IExitScope
    {
        public required Label BreakLabel;
        public required Label ContinueLabel;
        public IReadOnlyList<string> LabelNames = CompilationContext.NoLabels;

        // Lazily assigned the first time a break/continue to this loop must run an intervening
        // finally; identifies that exit in the `<>pendingExit` field. Unset while the loop's
        // break/continue need no finally routing (the common case → a direct branch).
        public int? BreakCode;
        public int? ContinueCode;
    }

    /// <summary>A flag-based <c>try</c> that has a <c>finally</c>; its finally runs on any exit that crosses it.</summary>
    private sealed class FinallyScope : IExitScope
    {
        public required Label CleanupLabel;

        // code → where control goes after this finally for that pending exit: a chain to the next
        // outer finally's cleanup label, or null meaning "this finally is terminal for the exit".
        public readonly Dictionary<int, Label?> Dispatch = [];
    }

    // Innermost-last stack of loop and finally scopes currently open at the emission point. Replaces
    // the base class's `_loopLabels` (the loop-scope methods below are overridden to use this) so loops
    // and finallys share one ordering — needed to know which finallys lie between a break and its loop.
    private readonly List<IExitScope> _exitScopes = [];

    private const int ExitCodeReturn = 1;
    private int _nextExitCode = 2; // break/continue codes are allocated from here (no throw routing here)

    // code → emits the terminal action for that code (invoked when a finally is terminal for it).
    private readonly Dictionary<int, System.Action> _exitTerminals = [];

    // `<>pendingExit` (int): the code of an in-flight non-local exit, or 0 when none. A finally that
    // awaits suspends MoveNext mid-routing, so this must be a field (a local would reset on re-entry).
    // Set once per exit and cleared by the terminal dispatch; defaults to 0 on a fresh state machine.
    private FieldBuilder? _pendingExitField;

    private FieldBuilder GetPendingExitField() =>
        _pendingExitField ??= DefineStateMachineField("<>pendingExit", typeof(int));

    // `<>pendingReturnValue` (object): the completion value of a `return` being routed through
    // finally(s). The value is stashed here at the `return` and restored by the return terminal, after
    // the finally has run — held across any suspension in those finallys (an IL local would not survive
    // the MoveNext re-entry).
    private FieldBuilder? _pendingReturnValueField;

    private FieldBuilder GetPendingReturnValueField() =>
        _pendingReturnValueField ??= DefineStateMachineField("<>pendingReturnValue", typeof(object));

    // ---- Loop-scope methods (override the base stack to use `_exitScopes`) -----------------------

    protected override void EnterLoop(Label breakLabel, Label continueLabel, string? labelName = null)
        // Adopt any pending labels (`outer: for (...)`, or a chain `a: b: for`) just like the base
        // EnterLoop does, so a labeled `break`/`continue` can resolve this loop as its target.
        => _exitScopes.Add(new LoopScope { BreakLabel = breakLabel, ContinueLabel = continueLabel, LabelNames = labelName != null ? new[] { labelName } : Ctx.TakePendingLoopLabels() });

    protected override void ExitLoop()
    {
        // Well-nested: any try opened inside the loop body has already been closed, so the top of the
        // stack is this loop's LoopScope.
        _exitScopes.RemoveAt(_exitScopes.Count - 1);
    }

    protected override (Label BreakLabel, Label ContinueLabel, IReadOnlyList<string> LabelNames)? CurrentLoop
    {
        get
        {
            var loop = CurrentLoopScope();
            return loop == null ? null : (loop.BreakLabel, loop.ContinueLabel, loop.LabelNames);
        }
    }

    protected override (Label BreakLabel, Label ContinueLabel, IReadOnlyList<string> LabelNames)? FindLabeledLoop(string labelName)
    {
        var loop = FindLabeledLoopScope(labelName);
        return loop == null ? null : (loop.BreakLabel, loop.ContinueLabel, loop.LabelNames);
    }

    private LoopScope? CurrentLoopScope()
    {
        for (int i = _exitScopes.Count - 1; i >= 0; i--)
            if (_exitScopes[i] is LoopScope ls)
                return ls;
        return null;
    }

    private LoopScope? FindLabeledLoopScope(string labelName)
    {
        for (int i = _exitScopes.Count - 1; i >= 0; i--)
            if (_exitScopes[i] is LoopScope ls && ls.LabelNames.Contains(labelName))
                return ls;
        return null;
    }

    // ---- Non-local exit overrides ---------------------------------------------------------------

    /// <summary>
    /// A <c>break</c> must run any finally between it and the target loop before jumping out.
    /// </summary>
    protected override void EmitBreak(Stmt.Break b)
    {
        var loop = b.Label != null ? FindLabeledLoopScope(b.Label.Lexeme) : CurrentLoopScope();
        if (loop == null) return; // matches the base: a break with no resolvable target is a no-op

        var chain = FinallyFramesAbove(loop);
        if (chain.Count > 0)
        {
            // Flag-based finally(s) lie between the break and its loop; run them before jumping out.
            int code = loop.BreakCode ??= _nextExitCode++;
            _exitTerminals.TryAdd(code, () => IL.Emit(OpCodes.Br, loop.BreakLabel));
            RouteThroughFinallys(chain, code, Ctx.ExceptionBlockDepth > 0 ? OpCodes.Leave : OpCodes.Br);
            return;
        }

        EmitBranchToLabel(loop.BreakLabel);
    }

    /// <summary>
    /// A <c>continue</c> must run any finally between it and the target loop before jumping to the
    /// loop's continue point.
    /// </summary>
    protected override void EmitContinue(Stmt.Continue c)
    {
        var loop = c.Label != null ? FindLabeledLoopScope(c.Label.Lexeme) : CurrentLoopScope();
        if (loop == null) return;

        var chain = FinallyFramesAbove(loop);
        if (chain.Count > 0)
        {
            int code = loop.ContinueCode ??= _nextExitCode++;
            _exitTerminals.TryAdd(code, () => IL.Emit(OpCodes.Br, loop.ContinueLabel));
            RouteThroughFinallys(chain, code, Ctx.ExceptionBlockDepth > 0 ? OpCodes.Leave : OpCodes.Br);
            return;
        }

        EmitBranchToLabel(loop.ContinueLabel);
    }

    // ---- Routing helpers ------------------------------------------------------------------------

    /// <summary>All open finally scopes, innermost first.</summary>
    private List<FinallyScope> ActiveFinallyFrames()
    {
        var result = new List<FinallyScope>();
        for (int i = _exitScopes.Count - 1; i >= 0; i--)
            if (_exitScopes[i] is FinallyScope fs)
                result.Add(fs);
        return result;
    }

    /// <summary>The finally scopes between the current point and <paramref name="target"/>, innermost first.</summary>
    private List<FinallyScope> FinallyFramesAbove(LoopScope target)
    {
        var result = new List<FinallyScope>();
        for (int i = _exitScopes.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_exitScopes[i], target)) break;
            if (_exitScopes[i] is FinallyScope fs)
                result.Add(fs);
        }
        return result;
    }

    /// <summary>
    /// Records <paramref name="code"/>'s per-frame routing across <paramref name="chain"/> (each frame
    /// chains to the next, the outermost is terminal), then sets the pending-exit field and branches to
    /// the innermost finally. The caller must have prepared any value the terminal needs (e.g. the
    /// return value) beforehand.
    /// </summary>
    /// <param name="branch">
    /// <c>Br</c> when the exit is at the top level, or <c>Leave</c> when it is emitted inside a real IL
    /// exception block (EmitSimpleTryCatch) nested in the flag-based finally(s): the <c>Leave</c> exits
    /// that block — running its no-await finally — straight to the innermost flag cleanup label, then
    /// the flag machinery runs the remaining (outer) finally(s). A real try never encloses a flag try,
    /// so every real finally is innermore than every flag one and this ordering is correct.
    /// </param>
    private void RouteThroughFinallys(List<FinallyScope> chain, int code, OpCode branch)
    {
        for (int i = 0; i < chain.Count; i++)
        {
            // The last frame in the chain is terminal for this code (null); the rest chain outward.
            Label? next = i < chain.Count - 1 ? chain[i + 1].CleanupLabel : null;
            chain[i].Dispatch[code] = next; // idempotent: the same code always routes a frame the same way
        }

        IL.Emit(OpCodes.Ldarg_0);
        IL.Emit(OpCodes.Ldc_I4, code);
        IL.Emit(OpCodes.Stfld, GetPendingExitField());
        IL.Emit(branch, chain[0].CleanupLabel);
    }

    /// <summary>
    /// Emitted after a finally body: for each pending-exit code that routes through this frame, either
    /// chain to the next outer finally or, if terminal here, clear the pending field and run the
    /// terminal action. A pending value of 0 (none) and codes not handled here fall through unchanged.
    /// </summary>
    private void EmitFinallyDispatch(FinallyScope frame)
    {
        foreach (var (code, next) in frame.Dispatch)
        {
            var skip = IL.DefineLabel();
            IL.Emit(OpCodes.Ldarg_0);
            IL.Emit(OpCodes.Ldfld, GetPendingExitField());
            IL.Emit(OpCodes.Ldc_I4, code);
            IL.Emit(OpCodes.Bne_Un, skip);

            if (next.HasValue)
            {
                // Not terminal here — keep the pending code and run the next outer finally.
                IL.Emit(OpCodes.Br, next.Value);
            }
            else
            {
                // Terminal: the exit has run every finally it needed to. Clear the flag first so a
                // break/continue resumes with clean state.
                IL.Emit(OpCodes.Ldarg_0);
                IL.Emit(OpCodes.Ldc_I4_0);
                IL.Emit(OpCodes.Stfld, GetPendingExitField());
                _exitTerminals[code]();
            }

            IL.MarkLabel(skip);
        }
    }

    /// <summary>
    /// The terminal for a routed <c>return</c>: a finally that awaited between the <c>return</c> and
    /// here ran on a separate MoveNext invocation, so the return value (stashed in
    /// <c>&lt;&gt;pendingReturnValue</c> at the <c>return</c>) is reloaded onto the stack and used to
    /// complete the state machine via the emitter's completion hook.
    /// </summary>
    private void RegisterReturnTerminal() => _exitTerminals.TryAdd(ExitCodeReturn, () =>
    {
        IL.Emit(OpCodes.Ldarg_0);
        IL.Emit(OpCodes.Ldfld, GetPendingReturnValueField());
        EmitCompleteWithReturnValueOnStack();
    });
}
