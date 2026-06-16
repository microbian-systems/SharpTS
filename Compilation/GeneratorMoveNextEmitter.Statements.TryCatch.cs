using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class GeneratorMoveNextEmitter
{
    // ---- Unified exit-scope stack (#500) --------------------------------------------------------
    //
    // A non-local exit (return / break / continue / a throw from a catch or finally) that crosses a
    // flag-based try/finally must run the enclosing finally(s) before transferring control. Because a
    // finally can itself yield, the routing state has to survive a MoveNext re-entry, so it lives in
    // state-machine fields rather than IL locals.
    //
    // The scheme mirrors how Roslyn lowers iterator try/finally: each exit is assigned an integer
    // "pending action" code, stored in `<>pendingExit`, and branches to the innermost enclosing
    // finally's cleanup label. After each finally body runs, a dispatch (EmitFinallyDispatch) inspects
    // the code and either chains to the next outer finally or, if this finally is the last one the exit
    // must traverse, performs the terminal action (complete / rethrow / jump to a loop label). The set
    // of finallys an exit traverses — and which one is terminal — is known at exit-emission time (the
    // scopes are on a stack), so each frame's per-code routing is recorded then.

    private interface IExitScope;

    /// <summary>A loop (or switch / labeled statement) that <c>break</c>/<c>continue</c> can target.</summary>
    private sealed class LoopScope : IExitScope
    {
        public required Label BreakLabel;
        public required Label ContinueLabel;
        public string? LabelName;

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
    private const int ExitCodeThrow = 2;
    private int _nextExitCode = 3; // break/continue codes are allocated from here

    // code → emits the terminal action for that code (invoked when a finally is terminal for it).
    private readonly Dictionary<int, Action> _exitTerminals = [];

    // Depth of real IL exception blocks (EmitSimpleTryCatch / EmitSyncSegmentInTry) open around the
    // current emission point. While > 0, a `br`/`ret` out of the region would be illegal, so exits are
    // left to the existing per-path handling instead of being routed through the finally machinery.
    private int _protectedRegionDepth;

    // True while emitting a catch or finally body. A `throw` there must run the enclosing finally(s);
    // a `throw` in a try body is instead captured by its sync-segment mini try/catch (and so must not
    // be routed). Saved/restored around each region so nesting is handled correctly.
    private bool _inHandlerBody;

    // The innermost flag-based try whose *body* is currently being emitted: its catch/finally entry
    // (afterTryBodyLabel), the local capturing a try-body exception, the boolean flag recording whether
    // one was captured, and `_exitScopes.Count` at the start of its body (ScopeDepth — finally scopes at
    // indices >= it are strictly inside this try). An external throw() injected at a yield in this body
    // behaves as if the body threw there — it stores the error into the local, sets the flag, and
    // branches to the cleanup so the catch/finally run (#526). The flag (not the value's nullness) gates
    // the catch so an injected throw(null)/throw(undefined) still engages it (#619). Saved/restored
    // around the try-body emission, so while emitting a catch/finally body it instead identifies the
    // *enclosing* flag-based try (or is null) — the one whose catch must handle a throw escaping that
    // handler, after the finally(s) inside it have run (#632).
    private (Label AfterTryBody, LocalBuilder CaughtException, LocalBuilder ExceptionPresent, int ScopeDepth)? _tryBodyContext;

    // `<>pendingExit` (int): the code of an in-flight non-local exit, or 0 when none. A finally that
    // yields suspends MoveNext mid-routing, so this must be a field (a local would reset on re-entry).
    // Set once per exit and cleared by the terminal dispatch; defaults to 0 on a fresh state machine.
    private FieldBuilder? _pendingExitField;

    private FieldBuilder GetPendingExitField() =>
        _pendingExitField ??= _builder.StateMachineType.DefineField(
            "<>pendingExit", typeof(int), FieldAttributes.Private);

    // `<>pendingException` (object): the value of a `throw` being routed through finally(s), held
    // across any suspension in those finallys until the terminal dispatch rethrows it.
    private FieldBuilder? _pendingExceptionField;

    private FieldBuilder GetPendingExceptionField() =>
        _pendingExceptionField ??= _builder.StateMachineType.DefineField(
            "<>pendingException", typeof(object), FieldAttributes.Private);

    // `<>pendingReturnValue` (object): the completion value of a `return` being routed through
    // finally(s). A finally that yields overwrites `<>2__current` with its yielded value, so the
    // return value is stashed here at the `return` and restored to Current by the return terminal,
    // after the finally has run (#555). Held across any suspension in those finallys.
    private FieldBuilder? _pendingReturnValueField;

    private FieldBuilder GetPendingReturnValueField() =>
        _pendingReturnValueField ??= _builder.StateMachineType.DefineField(
            "<>pendingReturnValue", typeof(object), FieldAttributes.Private);

    // Per-construct fields holding a try-body exception across a *yielding* finally in a try/finally
    // with no catch (#599). The exception is captured into an IL local during the try body, but that
    // local resets when the yielding finally suspends MoveNext, so the post-finally rethrow would see
    // null and silently drop it. Persisting to a field before the finally keeps it alive. Each
    // qualifying construct gets its own field rather than sharing one: a nested persisting construct
    // inside the finally body would otherwise clobber the outer's captured exception.
    private int _caughtExceptionFieldCounter;

    private FieldBuilder DefineCaughtExceptionField() =>
        _builder.StateMachineType.DefineField(
            $"<>caughtException{_caughtExceptionFieldCounter++}", typeof(object), FieldAttributes.Private);

    // Companion to `<>caughtException{n}`: the exception-present flag (#619) that must likewise survive
    // a *yielding* finally in a catch-less try/finally. Gating the catch/rethrow on this boolean rather
    // than the captured value's nullness is what lets a thrown null/undefined be caught (a null CLR ref
    // would otherwise read as "no exception"). Own counter so each construct gets a distinct field.
    private int _exceptionPresentFieldCounter;

    private FieldBuilder DefineExceptionPresentField() =>
        _builder.StateMachineType.DefineField(
            $"<>exceptionPresent{_exceptionPresentFieldCounter++}", typeof(bool), FieldAttributes.Private);

    // ---- Loop-scope methods (override the base stack to use `_exitScopes`) -----------------------

    protected override void EnterLoop(Label breakLabel, Label continueLabel, string? labelName = null)
        => _exitScopes.Add(new LoopScope { BreakLabel = breakLabel, ContinueLabel = continueLabel, LabelName = labelName ?? Ctx.TakePendingLoopLabel() });

    protected override void ExitLoop()
    {
        // Well-nested: any try opened inside the loop body has already been closed, so the top of the
        // stack is this loop's LoopScope.
        _exitScopes.RemoveAt(_exitScopes.Count - 1);
    }

    protected override (Label BreakLabel, Label ContinueLabel, string? LabelName)? CurrentLoop
    {
        get
        {
            var loop = CurrentLoopScope();
            return loop == null ? null : (loop.BreakLabel, loop.ContinueLabel, loop.LabelName);
        }
    }

    protected override (Label BreakLabel, Label ContinueLabel, string? LabelName)? FindLabeledLoop(string labelName)
    {
        var loop = FindLabeledLoopScope(labelName);
        return loop == null ? null : (loop.BreakLabel, loop.ContinueLabel, loop.LabelName);
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
            if (_exitScopes[i] is LoopScope ls && ls.LabelName == labelName)
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
            // From inside a real IL block, `Leave` to the innermost flag cleanup (running the real,
            // no-yield finally(s) on the way — a real try never encloses a flag try, so they are all
            // innermore); otherwise branch there directly.
            int code = loop.BreakCode ??= _nextExitCode++;
            _exitTerminals.TryAdd(code, () => _il.Emit(OpCodes.Br, loop.BreakLabel));
            RouteThroughFinallys(chain, code, _protectedRegionDepth > 0 ? OpCodes.Leave : OpCodes.Br);
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
            // As in EmitBreak: run the intervening flag-based finally(s) first, `Leave`-ing out of any
            // real IL block we are inside so its no-yield finally runs and the IL stays legal.
            int code = loop.ContinueCode ??= _nextExitCode++;
            _exitTerminals.TryAdd(code, () => _il.Emit(OpCodes.Br, loop.ContinueLabel));
            RouteThroughFinallys(chain, code, _protectedRegionDepth > 0 ? OpCodes.Leave : OpCodes.Br);
            return;
        }

        EmitBranchToLabel(loop.ContinueLabel);
    }

    /// <summary>
    /// A <c>throw</c> in a catch or finally body propagates to the enclosing flag-based try (the one
    /// whose body lexically contains this handler): it runs the finally(s) inside that try, then lands
    /// in its catch — rather than a real IL <c>throw</c> that bypasses the flag-based catch (#632). With
    /// no enclosing flag-based try it runs the active finally(s) and propagates out of MoveNext. A throw
    /// in a try body is captured by its sync-segment mini try/catch (handled by the catch arm), not here.
    /// </summary>
    protected override void EmitThrow(Stmt.Throw t)
    {
        if (_inHandlerBody && _protectedRegionDepth == 0)
        {
            if (_tryBodyContext is { } encl)
            {
                EmitThrowIntoEnclosingTry(encl, () => { EmitExpression(t.Value); EnsureBoxed(); });
                return;
            }

            // No enclosing flag-based try (this handler belongs to the outermost try), but its own
            // finally may still be active and must run before the throw leaves MoveNext.
            var chain = ActiveFinallyFrames();
            if (chain.Count > 0)
            {
                EmitExpression(t.Value);
                EnsureBoxed();
                var thrown = _il.DeclareLocal(typeof(object));
                _il.Emit(OpCodes.Stloc, thrown);
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldloc, thrown);
                _il.Emit(OpCodes.Stfld, GetPendingExceptionField());

                RegisterThrowTerminal();
                RouteThroughFinallys(chain, ExitCodeThrow, OpCodes.Br);
                return;
            }
        }

        base.EmitThrow(t);
    }

    /// <summary>
    /// Propagates a guest exception escaping a handler body into the enclosing flag-based try
    /// <paramref name="encl"/>: stores the value into that try's capture local and sets its present
    /// flag, then branches to its cleanup entry so its catch runs (or, catch-less, its finally then its
    /// own propagation). Any finally(s) strictly inside that try run first; because such a finally can
    /// yield, the value is held in <c>&lt;&gt;pendingException</c> across them and moved into the capture
    /// local by the routing terminal. This is the catch-side analog of the finally routing already used
    /// for a routed return/throw (#632). <paramref name="loadValue"/> pushes the boxed guest value.
    /// </summary>
    private void EmitThrowIntoEnclosingTry((Label AfterTryBody, LocalBuilder CaughtException, LocalBuilder ExceptionPresent, int ScopeDepth) encl, Action loadValue)
    {
        var chain = FinallyFramesInside(encl.ScopeDepth);
        if (chain.Count == 0)
        {
            // No intervening finally: store straight into the enclosing try and branch to its catch.
            loadValue();
            _il.Emit(OpCodes.Stloc, encl.CaughtException);
            _il.Emit(OpCodes.Ldc_I4_1);
            _il.Emit(OpCodes.Stloc, encl.ExceptionPresent);
            _il.Emit(OpCodes.Br, encl.AfterTryBody);
            return;
        }

        // Intervening finally(s) may yield, so hold the value in a field across them; the routing
        // terminal moves it into the enclosing try's capture local and branches to its catch.
        _il.Emit(OpCodes.Ldarg_0);
        loadValue();
        _il.Emit(OpCodes.Stfld, GetPendingExceptionField());
        int code = _nextExitCode++;
        _exitTerminals[code] = () =>
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, GetPendingExceptionField());
            _il.Emit(OpCodes.Stloc, encl.CaughtException);
            _il.Emit(OpCodes.Ldc_I4_1);
            _il.Emit(OpCodes.Stloc, encl.ExceptionPresent);
            _il.Emit(OpCodes.Br, encl.AfterTryBody);
        };
        RouteThroughFinallys(chain, code, OpCodes.Br);
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
    /// The finally scopes strictly inside the flag-based try whose body began at <paramref
    /// name="scopeDepth"/> (= <c>_exitScopes.Count</c> at that point), innermost first. Excludes the
    /// try's own finally (which lives just below scopeDepth) and everything outside it. These are the
    /// finallys a throw escaping a nested handler must run before reaching that try's catch (#632).
    /// </summary>
    private List<FinallyScope> FinallyFramesInside(int scopeDepth)
    {
        var result = new List<FinallyScope>();
        for (int i = _exitScopes.Count - 1; i >= scopeDepth; i--)
            if (_exitScopes[i] is FinallyScope fs)
                result.Add(fs);
        return result;
    }

    /// <summary>
    /// Records <paramref name="code"/>'s per-frame routing across <paramref name="chain"/> (each frame
    /// chains to the next, the outermost is terminal), then sets the pending-exit field and branches to
    /// the innermost finally. The caller must have prepared any value the terminal needs (e.g. the
    /// thrown value, or the Current/return state) beforehand.
    /// </summary>
    /// <param name="branch">
    /// <c>Br</c> when the exit is at the top level, or <c>Leave</c> when it is emitted inside a real IL
    /// exception block (EmitSimpleTryCatch) nested in the flag-based finally(s): the <c>Leave</c> exits
    /// that block — running its no-yield finally — straight to the innermost flag cleanup label, then
    /// the flag machinery runs the remaining (outer) finally(s). A real try never encloses a flag try,
    /// so every real finally is innermore than every flag one and this ordering is correct (#554).
    /// </param>
    private void RouteThroughFinallys(List<FinallyScope> chain, int code, OpCode branch)
    {
        for (int i = 0; i < chain.Count; i++)
        {
            // The last frame in the chain is terminal for this code (null); the rest chain outward.
            Label? next = i < chain.Count - 1 ? chain[i + 1].CleanupLabel : null;
            chain[i].Dispatch[code] = next; // idempotent: the same code always routes a frame the same way
        }

        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, code);
        _il.Emit(OpCodes.Stfld, GetPendingExitField());
        _il.Emit(branch, chain[0].CleanupLabel);
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
            var skip = _il.DefineLabel();
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, GetPendingExitField());
            _il.Emit(OpCodes.Ldc_I4, code);
            _il.Emit(OpCodes.Bne_Un, skip);

            if (next.HasValue)
            {
                // Not terminal here — keep the pending code and run the next outer finally.
                _il.Emit(OpCodes.Br, next.Value);
            }
            else
            {
                // Terminal: the exit has run every finally it needed to. Clear the flag first so a
                // break/continue resumes the generator with clean state.
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Stfld, GetPendingExitField());
                _exitTerminals[code]();
            }

            _il.MarkLabel(skip);
        }
    }

    private void RegisterReturnTerminal() => _exitTerminals.TryAdd(ExitCodeReturn, () =>
    {
        // Restore the completion value into Current: a yielding finally between the `return` and this
        // point overwrote Current with its yielded value, so re-load the value stashed at the `return`
        // (#555). With no yielding finally this is a no-op (Current still holds the same value).
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, GetPendingReturnValueField());
        _il.Emit(OpCodes.Stfld, _builder.CurrentField);

        // The generator completes. State -2 is re-asserted here because a yielding finally between the
        // `return` and this point overwrote it with the finally's resume state.
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, -2);
        _il.Emit(OpCodes.Stfld, _builder.StateField);
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Ret);
    });

    private void RegisterThrowTerminal() => _exitTerminals.TryAdd(ExitCodeThrow, () =>
    {
        // The routed exception has run every enclosing finally; propagate it now. The generator is
        // completing (throwing), so mark it done first.
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, -2);
        _il.Emit(OpCodes.Stfld, _builder.StateField);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, GetPendingExceptionField());
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.CreateException);
        _il.Emit(OpCodes.Throw);
    });

    // ---- External return()/throw() injection at a suspended yield (#526) -------------------------

    /// <summary>
    /// At a yield resume point, consult the injection fields a suspended generator's
    /// return()/throw() set (#526) and, if one is pending, perform that abrupt completion here —
    /// running active try/finally(/catch) — instead of resuming normally. Emits nothing that
    /// transfers control when no injection is pending, so the caller's normal-resume code runs. A
    /// no-op when the $IGenerator methods (hence the injection fields) were not emitted.
    /// </summary>
    private void EmitResumeInjectionCheck()
    {
        var kindField = _builder.InjectedKindField;
        var valueField = _builder.InjectedValueField;
        if (kindField == null || valueField == null) return;

        void LoadInjectedValue()
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, valueField);
        }

        // return(v): behaves as `return v` at this point (consume the kind first so a yielding
        // finally that re-enters MoveNext does not re-inject).
        var afterReturn = _il.DefineLabel();
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, kindField);
        _il.Emit(OpCodes.Ldc_I4, GeneratorStateMachineBuilder.InjectKindReturn);
        _il.Emit(OpCodes.Bne_Un, afterReturn);
        ClearInjectedKind();
        EmitRoutedReturn(LoadInjectedValue);
        _il.MarkLabel(afterReturn);

        // throw(e): behaves as `throw e` at this point.
        var afterThrow = _il.DefineLabel();
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, kindField);
        _il.Emit(OpCodes.Ldc_I4, GeneratorStateMachineBuilder.InjectKindThrow);
        _il.Emit(OpCodes.Bne_Un, afterThrow);
        ClearInjectedKind();
        EmitRoutedThrow(LoadInjectedValue);
        _il.MarkLabel(afterThrow);
    }

    private void ClearInjectedKind()
    {
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, GeneratorStateMachineBuilder.InjectKindNone);
        _il.Emit(OpCodes.Stfld, _builder.InjectedKindField!);
    }

    /// <summary>
    /// Emits an abrupt <c>return &lt;value&gt;</c> at a top-level resume point: store the value into
    /// Current, mark the generator done, and route through any enclosing flag-based finally(s) so
    /// they run before completion. <paramref name="loadValue"/> pushes the boxed completion value.
    /// Mirrors <see cref="EmitReturn"/>'s chain logic, but the value is supplied (not evaluated from
    /// an expression) and the resume point is always at the top level, so the route uses <c>Br</c>.
    /// </summary>
    private void EmitRoutedReturn(Action loadValue)
    {
        _il.Emit(OpCodes.Ldarg_0);
        loadValue();
        _il.Emit(OpCodes.Stfld, _builder.CurrentField);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, -2);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        var chain = ActiveFinallyFrames();
        if (chain.Count > 0)
        {
            // Stash the completion value: a yielding finally overwrites Current; the return terminal
            // restores it after the finally has run (#555).
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, _builder.CurrentField);
            _il.Emit(OpCodes.Stfld, GetPendingReturnValueField());

            RegisterReturnTerminal();
            RouteThroughFinallys(chain, ExitCodeReturn, OpCodes.Br);
            return;
        }

        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a <c>throw &lt;value&gt;</c> at a top-level resume point (an external <c>throw()</c>
    /// injected at a suspended yield). Inside a try body it behaves as if the body threw there (store
    /// into the try's caught-exception local and branch to its cleanup, so the catch/finally run);
    /// inside a catch/finally body it propagates to the enclosing flag-based try's catch the same way a
    /// lexical handler-body throw does (#632); with no enclosing try it runs the active finally(s) and
    /// propagates out of MoveNext. <paramref name="loadValue"/> pushes the boxed guest error value.
    /// </summary>
    private void EmitRoutedThrow(Action loadValue)
    {
        if (_tryBodyContext is { } ctx)
        {
            if (!_inHandlerBody)
            {
                // In a try body: capture exactly like a try-body exception so the catch/finally at
                // afterTryBodyLabel handle it. A catch-less yielding finally persists this local to a
                // field before suspending, so it survives (#599). Set the present flag (not the value's
                // nullness) so an injected throw(null)/throw(undefined) still engages the catch (#619).
                loadValue();
                _il.Emit(OpCodes.Stloc, ctx.CaughtException);
                _il.Emit(OpCodes.Ldc_I4_1);
                _il.Emit(OpCodes.Stloc, ctx.ExceptionPresent);
                _il.Emit(OpCodes.Br, ctx.AfterTryBody);
                return;
            }

            // In a catch/finally body: run the finally(s) inside the enclosing try, then land in its
            // catch — the injection-path analog of the lexical handler-body throw fix (#632).
            EmitThrowIntoEnclosingTry(ctx, loadValue);
            return;
        }

        var chain = ActiveFinallyFrames();
        if (chain.Count > 0)
        {
            // Outermost try's catch/finally body: run its own finally(s), then rethrow at the terminal.
            _il.Emit(OpCodes.Ldarg_0);
            loadValue();
            _il.Emit(OpCodes.Stfld, GetPendingExceptionField());
            RegisterThrowTerminal();
            RouteThroughFinallys(chain, ExitCodeThrow, OpCodes.Br);
            return;
        }

        // No enclosing try: mark done and propagate out of MoveNext.
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, -2);
        _il.Emit(OpCodes.Stfld, _builder.StateField);
        loadValue();
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.CreateException);
        _il.Emit(OpCodes.Throw);
    }

    /// <summary>
    /// Emits try/catch/finally. When a yield crosses the protected region, real IL exception
    /// blocks cannot be used (the state-dispatch switch can't branch into a protected region,
    /// and `yield`'s `ret` is illegal inside one), so a flag-based scheme is emitted instead.
    /// </summary>
    protected override void EmitTryCatch(Stmt.TryCatch t)
    {
        bool hasYields = ContainsYield(t.TryBlock)
            || (t.CatchBlock != null && ContainsYield(t.CatchBlock))
            || (t.FinallyBlock != null && ContainsYield(t.FinallyBlock));

        // A return/break/continue lexically inside the finally body can never be lowered with the
        // real-IL path: none of `ret`/`br`/`Leave` may exit a .NET `finally` region, so it would emit
        // invalid IL (LeaveOutOfFinally). Route the whole construct through the flag-based scheme even
        // with no yield, so the finally is emitted as top-level statements and the exit is dispatched
        // legally (#598, the finally-side analog of #554, which handles exits in the try/catch body).
        // An exit targeting a loop *inside* the finally stays local and does not count as escaping.
        bool finallyHasEscapingExit = t.FinallyBlock != null
            && ContainsEscapingExit2(t.FinallyBlock, insideLoop: false, insideSwitch: false);

        if (hasYields || finallyHasEscapingExit)
            EmitTryCatchWithYields(t);
        else
            EmitSimpleTryCatch(t);
    }

    /// <summary>
    /// No yield crosses the protected region — real IL exception blocks are correct and cheapest.
    /// This is the original generator try/catch emission, unchanged.
    /// </summary>
    private void EmitSimpleTryCatch(Stmt.TryCatch t)
    {
        // A real IL protected region is open. A `br`/`ret` directly out of it is illegal, so a
        // non-local exit crossing it must use `Leave` instead — which also runs this (no-yield)
        // finally. _protectedRegionDepth tells the exit overrides a real block is open (so they pick
        // `Leave` and, when also inside flag-based finally(s), route out via the innermost flag
        // cleanup); ExceptionBlockDepth drives the Leave-vs-Br choice in EmitBranchToLabel. The latter
        // is incremented only here (not in the flag path's sync segments) so internal branches inside
        // a sync segment stay `Br` and do not illegally leave the mini try/catch.
        _protectedRegionDepth++;
        _ctx!.ExceptionBlockDepth++;
        _il.BeginExceptionBlock();

        foreach (var stmt in t.TryBlock)
            EmitStatement(stmt);

        if (t.CatchBlock != null)
        {
            _il.BeginCatchBlock(typeof(Exception));

            if (t.CatchParam != null)
            {
                // Stack has the .NET exception; wrap to the TS value and bind to the catch param.
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.WrapException);
                StoreCaughtExceptionToParam(t.CatchParam.Lexeme);
            }
            else
            {
                _il.Emit(OpCodes.Pop);
            }

            foreach (var stmt in t.CatchBlock)
                EmitStatement(stmt);
        }

        if (t.FinallyBlock != null)
        {
            _il.BeginFinallyBlock();
            foreach (var stmt in t.FinallyBlock)
                EmitStatement(stmt);
        }

        _il.EndExceptionBlock();
        _ctx!.ExceptionBlockDepth--;
        _protectedRegionDepth--;
    }

    /// <summary>
    /// Binds the caught exception value (on the IL stack) to the catch parameter, honouring
    /// whether the parameter was hoisted to a state-machine field (used across a yield) or lives
    /// in an IL local. Storing to a fresh local unconditionally — the previous behaviour — lost
    /// the value whenever the catch parameter was hoisted, because reads resolve the field first.
    /// </summary>
    private void StoreCaughtExceptionToParam(string name)
    {
        if (GetHoistedVariableField(name) == null)
        {
            // Not hoisted: register a local so the catch body's reads resolve to it.
            var exLocal = _il.DeclareLocal(typeof(object));
            _ctx!.Locals.RegisterLocal(name, exLocal);
        }

        // Resolver stores to the hoisted field if present, otherwise the registered local.
        Resolver.TryStoreVariable(name);
    }

    /// <summary>
    /// Flag-based try/catch/finally for the case where a yield (or yield*) lives inside the
    /// protected region. Synchronous segments of the try body are wrapped in mini IL try/catch
    /// blocks that capture any exception into a flag local; suspension points and non-local exits
    /// are emitted at the top level (outside any protected region) so their resume labels are
    /// reachable from the state-dispatch switch and their `ret`/`br` are legal.
    /// </summary>
    private void EmitTryCatchWithYields(Stmt.TryCatch t)
    {
        var caughtExceptionLocal = _il.DeclareLocal(typeof(object));
        // Whether the try body raised an exception, tracked separately from caughtExceptionLocal's
        // nullness: a thrown null/undefined captures as a null CLR reference, which a value-nullness
        // gate misreads as "no exception" — skipping the catch and dropping the post-finally rethrow
        // (#619). This flag records presence regardless of the captured value.
        var exceptionPresentLocal = _il.DeclareLocal(typeof(bool));
        var afterTryBodyLabel = _il.DefineLabel();

        // #599: in a try/finally with no catch whose finally can yield, the captured try-body
        // exception must survive the finally's suspension. The IL local resets on MoveNext re-entry,
        // so persist it to a dedicated field before the finally and read that field in the
        // post-finally rethrow. Allocated only for that shape; null means "use the local". The
        // present flag needs the same persistence (read by the rethrow gate after the finally, #619).
        bool persistAcrossYieldingFinally =
            t.CatchBlock == null && t.FinallyBlock != null && ContainsYield(t.FinallyBlock);
        FieldBuilder? caughtExceptionField = persistAcrossYieldingFinally ? DefineCaughtExceptionField() : null;
        FieldBuilder? exceptionPresentField = persistAcrossYieldingFinally ? DefineExceptionPresentField() : null;

        void EmitLoadCaughtException()
        {
            if (caughtExceptionField != null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, caughtExceptionField);
            }
            else
            {
                _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
            }
        }

        void EmitLoadExceptionPresent()
        {
            if (exceptionPresentField != null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, exceptionPresentField);
            }
            else
            {
                _il.Emit(OpCodes.Ldloc, exceptionPresentLocal);
            }
        }

        // No exception captured yet.
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Stloc, caughtExceptionLocal);
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Stloc, exceptionPresentLocal);

        // A non-local exit inside this try (or its catch) must run the finally before transferring
        // control, so register a finally scope whose cleanup is the catch/finally entry. On those exit
        // paths the exception flag is null, so the catch is skipped and the finally runs. Without a
        // finally there is nothing to route through, so no scope is pushed and exits go directly.
        FinallyScope? frame = null;
        if (t.FinallyBlock != null)
        {
            frame = new FinallyScope { CleanupLabel = afterTryBodyLabel };
            _exitScopes.Add(frame);
        }

        // Throws in the try body are captured by their sync segments, not routed. While emitting the
        // body, expose this try as the injected-throw target so an external throw() at a yield here
        // engages this try's catch/finally (#526).
        bool previousInHandler = _inHandlerBody;
        var previousTryBody = _tryBodyContext;
        _inHandlerBody = false;
        _tryBodyContext = (afterTryBodyLabel, caughtExceptionLocal, exceptionPresentLocal, _exitScopes.Count);
        EmitTryBodyWithYields(t.TryBlock, caughtExceptionLocal, exceptionPresentLocal, afterTryBodyLabel);
        _tryBodyContext = previousTryBody;
        _inHandlerBody = previousInHandler;

        _il.MarkLabel(afterTryBodyLabel);

        // Catch: runs only when the try body captured an exception. The finally scope is still open, so
        // a non-local exit (including a throw) from the catch body runs this finally too.
        if (t.CatchBlock != null)
        {
            // Gate on the present flag, not the value's nullness, so a caught null/undefined enters the
            // catch (#619). In the with-catch shape no field is allocated, so the local is authoritative.
            var skipCatchLabel = _il.DefineLabel();
            _il.Emit(OpCodes.Ldloc, exceptionPresentLocal);
            _il.Emit(OpCodes.Brfalse, skipCatchLabel);

            if (t.CatchParam != null)
            {
                _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
                StoreCaughtExceptionToParam(t.CatchParam.Lexeme);
            }

            // Catch handles it; clear the present flag so the post-finally rethrow below is skipped —
            // and so a routed exit re-entering afterTryBodyLabel skips the catch rather than re-running
            // it. The flag (not the value) is the gate now, so clearing it is what matters (#619).
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Stloc, exceptionPresentLocal);

            _inHandlerBody = true;
            foreach (var stmt in t.CatchBlock)
                EmitStatement(stmt);
            _inHandlerBody = previousInHandler;

            _il.MarkLabel(skipCatchLabel);
        }

        // The finally itself is outside its own scope: an exit within it runs the *enclosing* finallys.
        if (frame != null)
            _exitScopes.RemoveAt(_exitScopes.Count - 1);

        // Finally: always runs — on normal completion, after a caught exception, or on a routed exit.
        if (t.FinallyBlock != null)
        {
            // #599: persist the captured exception (null on the normal/routed-exit paths) before the
            // finally so a suspension inside it does not wipe the IL local out from under the rethrow.
            if (caughtExceptionField != null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
                _il.Emit(OpCodes.Stfld, caughtExceptionField);

                // Persist the present flag alongside the value so the post-finally rethrow gate reads a
                // live flag after a yielding finally (#619, same survival reason as the value, #599).
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldloc, exceptionPresentLocal);
                _il.Emit(OpCodes.Stfld, exceptionPresentField!);
            }

            _inHandlerBody = true;
            foreach (var stmt in t.FinallyBlock)
                EmitStatement(stmt);
            _inHandlerBody = previousInHandler;

            // After the finally, dispatch any pending non-local exit that routed through here.
            EmitFinallyDispatch(frame!);
        }

        // Propagate an uncaught exception once the finally has run (try/finally with no catch).
        if (t.CatchBlock == null)
        {
            var noExceptionLabel = _il.DefineLabel();
            EmitLoadExceptionPresent();
            _il.Emit(OpCodes.Brfalse, noExceptionLabel);

            if (_tryBodyContext is { } encl)
            {
                // The finally has run; the still-uncaught exception now propagates to the enclosing
                // flag-based try's catch (not out of MoveNext), so an outer catch still handles it — the
                // try/finally analog of the handler-body throw routing (#632).
                EmitThrowIntoEnclosingTry(encl, EmitLoadCaughtException);
            }
            else
            {
                // No enclosing flag-based try: mark the generator done and propagate out of MoveNext.
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldc_I4, -2);
                _il.Emit(OpCodes.Stfld, _builder.StateField);
                EmitLoadCaughtException();
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.CreateException);
                _il.Emit(OpCodes.Throw);
            }

            _il.MarkLabel(noExceptionLabel);
        }
    }

    /// <summary>
    /// Walks the try body, wrapping runs of plain statements in mini IL try/catch blocks while
    /// emitting suspension points and non-local exits (return/break/continue) at the top level.
    /// </summary>
    private void EmitTryBodyWithYields(List<Stmt> tryBody, LocalBuilder caughtExceptionLocal, LocalBuilder exceptionPresentLocal, Label afterTryLabel)
    {
        List<Stmt> syncSegment = [];

        foreach (var stmt in tryBody)
        {
            if (IsSegmentBreaker(stmt))
            {
                // Flush the accumulated plain statements first.
                if (syncSegment.Count > 0)
                {
                    EmitSyncSegmentInTry(syncSegment, caughtExceptionLocal, exceptionPresentLocal);
                    syncSegment.Clear();
                }

                // If an earlier segment threw, skip the suspension/exit and head to catch/finally.
                // Gate on the present flag so a thrown null/undefined still short-circuits here (#619).
                _il.Emit(OpCodes.Ldloc, exceptionPresentLocal);
                _il.Emit(OpCodes.Brtrue, afterTryLabel);

                // Emitted at the top level: a yield's `ret`/resume label and a return's `br` are
                // only legal outside a protected region.
                EmitStatement(stmt);
            }
            else
            {
                syncSegment.Add(stmt);
            }
        }

        if (syncSegment.Count > 0)
            EmitSyncSegmentInTry(syncSegment, caughtExceptionLocal, exceptionPresentLocal);
    }

    /// <summary>
    /// Emits a run of plain (non-suspending, non-exiting) statements inside a real IL try/catch
    /// that records any thrown exception into <paramref name="caughtExceptionLocal"/>.
    /// </summary>
    private void EmitSyncSegmentInTry(List<Stmt> statements, LocalBuilder caughtExceptionLocal, LocalBuilder exceptionPresentLocal)
    {
        // An earlier segment may already have thrown — don't run this one. Gate on the present flag so
        // a prior thrown null/undefined still suppresses this segment (#619).
        var skipLabel = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, exceptionPresentLocal);
        _il.Emit(OpCodes.Brtrue, skipLabel);

        // A real IL protected region is open across the segment body (see _protectedRegionDepth).
        _protectedRegionDepth++;
        _il.BeginExceptionBlock();
        foreach (var stmt in statements)
            EmitStatement(stmt);

        _il.BeginCatchBlock(typeof(Exception));
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.WrapException);
        _il.Emit(OpCodes.Stloc, caughtExceptionLocal);
        // Record presence with the flag, not the value: a caught null/undefined would otherwise read
        // as "no exception" at the gates above (#619).
        _il.Emit(OpCodes.Ldc_I4_1);
        _il.Emit(OpCodes.Stloc, exceptionPresentLocal);
        _il.EndExceptionBlock();
        _protectedRegionDepth--;

        _il.MarkLabel(skipLabel);
    }

    #region Suspension / control-exit detection

    /// <summary>
    /// A statement must be emitted at the top level (rather than inside a mini try/catch segment)
    /// if it contains a suspension point or a control-flow exit that leaves the try region. Both
    /// would otherwise produce illegal IL inside the segment's protected region.
    /// </summary>
    private static bool IsSegmentBreaker(Stmt stmt) =>
        ContainsYieldInStmt(stmt) || ContainsEscapingExit(stmt, insideLoop: false, insideSwitch: false);

    private static bool ContainsYield(List<Stmt> statements)
    {
        foreach (var stmt in statements)
            if (ContainsYieldInStmt(stmt))
                return true;
        return false;
    }

    private static bool ContainsYieldInStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case Stmt.Expression e:
                return ContainsYieldInExpr(e.Expr);
            case Stmt.Var v:
                return v.Initializer != null && ContainsYieldInExpr(v.Initializer);
            case Stmt.Const c:
                return ContainsYieldInExpr(c.Initializer);
            case Stmt.Return r:
                return r.Value != null && ContainsYieldInExpr(r.Value);
            case Stmt.If i:
                return ContainsYieldInExpr(i.Condition)
                    || ContainsYieldInStmt(i.ThenBranch)
                    || (i.ElseBranch != null && ContainsYieldInStmt(i.ElseBranch));
            case Stmt.While w:
                return ContainsYieldInExpr(w.Condition) || ContainsYieldInStmt(w.Body);
            case Stmt.DoWhile dw:
                return ContainsYieldInStmt(dw.Body) || ContainsYieldInExpr(dw.Condition);
            case Stmt.For f:
                return (f.Initializer != null && ContainsYieldInStmt(f.Initializer))
                    || (f.Condition != null && ContainsYieldInExpr(f.Condition))
                    || (f.Increment != null && ContainsYieldInExpr(f.Increment))
                    || ContainsYieldInStmt(f.Body);
            case Stmt.ForOf fo:
                return ContainsYieldInExpr(fo.Iterable) || ContainsYieldInStmt(fo.Body);
            case Stmt.ForIn fi:
                return ContainsYieldInExpr(fi.Object) || ContainsYieldInStmt(fi.Body);
            case Stmt.Block b:
                return b.Statements != null && ContainsYield(b.Statements);
            case Stmt.Sequence seq:
                return ContainsYield(seq.Statements);
            case Stmt.LabeledStatement ls:
                return ContainsYieldInStmt(ls.Statement);
            case Stmt.Switch s:
                foreach (var c in s.Cases)
                {
                    if (ContainsYieldInExpr(c.Value) || ContainsYield(c.Body))
                        return true;
                }
                return s.DefaultBody != null && ContainsYield(s.DefaultBody);
            case Stmt.TryCatch t:
                return ContainsYield(t.TryBlock)
                    || (t.CatchBlock != null && ContainsYield(t.CatchBlock))
                    || (t.FinallyBlock != null && ContainsYield(t.FinallyBlock));
            case Stmt.Throw th:
                return ContainsYieldInExpr(th.Value);
            case Stmt.Print p:
                return ContainsYieldInExpr(p.Expr);
            default:
                return false;
        }
    }

    private static bool ContainsYieldInExpr(Expr expr)
    {
        switch (expr)
        {
            case Expr.Yield:
                return true;
            case Expr.Comma c:
                return ContainsYieldInExpr(c.Left) || ContainsYieldInExpr(c.Right);
            case Expr.Binary b:
                return ContainsYieldInExpr(b.Left) || ContainsYieldInExpr(b.Right);
            case Expr.Logical l:
                return ContainsYieldInExpr(l.Left) || ContainsYieldInExpr(l.Right);
            case Expr.Unary u:
                return ContainsYieldInExpr(u.Right);
            case Expr.Delete d:
                return ContainsYieldInExpr(d.Operand);
            case Expr.Grouping g:
                return ContainsYieldInExpr(g.Expression);
            case Expr.Call c:
                if (ContainsYieldInExpr(c.Callee)) return true;
                foreach (var arg in c.Arguments)
                    if (ContainsYieldInExpr(arg)) return true;
                return false;
            case Expr.Assign a:
                return ContainsYieldInExpr(a.Value);
            case Expr.CompoundAssign ca:
                return ContainsYieldInExpr(ca.Value);
            case Expr.Ternary t:
                return ContainsYieldInExpr(t.Condition)
                    || ContainsYieldInExpr(t.ThenBranch)
                    || ContainsYieldInExpr(t.ElseBranch);
            case Expr.Get g:
                return ContainsYieldInExpr(g.Object);
            case Expr.Set s:
                return ContainsYieldInExpr(s.Object) || ContainsYieldInExpr(s.Value);
            case Expr.GetIndex gi:
                return ContainsYieldInExpr(gi.Object) || ContainsYieldInExpr(gi.Index);
            case Expr.SetIndex si:
                return ContainsYieldInExpr(si.Object) || ContainsYieldInExpr(si.Index) || ContainsYieldInExpr(si.Value);
            default:
                return false;
        }
    }

    /// <summary>
    /// Detects return/break/continue that would transfer control out of the surrounding try
    /// region. Over-approximates conservatively (labeled break/continue are always treated as
    /// escaping): a false positive only costs a statement some mini-segment exception coverage,
    /// whereas a false negative would emit a `br`/`ret` inside a protected region (illegal IL).
    /// Nested function/arrow bodies are not traversed (their returns are their own).
    /// </summary>
    private static bool ContainsEscapingExit(Stmt stmt, bool insideLoop, bool insideSwitch)
    {
        switch (stmt)
        {
            case Stmt.Return:
                return true;
            case Stmt.Break b:
                return b.Label != null || !(insideLoop || insideSwitch);
            case Stmt.Continue c:
                return c.Label != null || !insideLoop;
            case Stmt.If i:
                return ContainsEscapingExit(i.ThenBranch, insideLoop, insideSwitch)
                    || (i.ElseBranch != null && ContainsEscapingExit(i.ElseBranch, insideLoop, insideSwitch));
            case Stmt.Block b:
                if (b.Statements == null) return false;
                foreach (var s in b.Statements)
                    if (ContainsEscapingExit(s, insideLoop, insideSwitch)) return true;
                return false;
            case Stmt.Sequence seq:
                foreach (var s in seq.Statements)
                    if (ContainsEscapingExit(s, insideLoop, insideSwitch)) return true;
                return false;
            case Stmt.While w:
                return ContainsEscapingExit(w.Body, insideLoop: true, insideSwitch);
            case Stmt.DoWhile dw:
                return ContainsEscapingExit(dw.Body, insideLoop: true, insideSwitch);
            case Stmt.For f:
                return ContainsEscapingExit(f.Body, insideLoop: true, insideSwitch);
            case Stmt.ForOf fo:
                return ContainsEscapingExit(fo.Body, insideLoop: true, insideSwitch);
            case Stmt.ForIn fi:
                return ContainsEscapingExit(fi.Body, insideLoop: true, insideSwitch);
            case Stmt.Switch s:
                foreach (var c in s.Cases)
                    foreach (var cs in c.Body)
                        if (ContainsEscapingExit(cs, insideLoop, insideSwitch: true)) return true;
                if (s.DefaultBody != null)
                    foreach (var ds in s.DefaultBody)
                        if (ContainsEscapingExit(ds, insideLoop, insideSwitch: true)) return true;
                return false;
            case Stmt.LabeledStatement ls:
                return ContainsEscapingExit(ls.Statement, insideLoop, insideSwitch);
            case Stmt.TryCatch t:
                if (ContainsEscapingExit2(t.TryBlock, insideLoop, insideSwitch)) return true;
                if (t.CatchBlock != null && ContainsEscapingExit2(t.CatchBlock, insideLoop, insideSwitch)) return true;
                if (t.FinallyBlock != null && ContainsEscapingExit2(t.FinallyBlock, insideLoop, insideSwitch)) return true;
                return false;
            default:
                return false;
        }
    }

    private static bool ContainsEscapingExit2(List<Stmt> statements, bool insideLoop, bool insideSwitch)
    {
        foreach (var s in statements)
            if (ContainsEscapingExit(s, insideLoop, insideSwitch)) return true;
        return false;
    }

    #endregion
}
