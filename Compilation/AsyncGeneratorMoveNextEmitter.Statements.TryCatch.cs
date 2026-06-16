using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncGeneratorMoveNextEmitter
{
    // ---- Unified exit-scope stack (#559, the async analog of #500) -------------------------------
    //
    // A non-local exit (return / break / continue / a throw from a catch or finally) that crosses a
    // flag-based try/finally must run the enclosing finally(s) before transferring control. Because a
    // finally can itself yield or await, the routing state has to survive a MoveNextAsync re-entry, so
    // it lives in state-machine fields rather than IL locals.
    //
    // This mirrors GeneratorMoveNextEmitter.Statements.TryCatch.cs exactly; only two things differ for
    // the async generator: the terminal actions return `ValueTask<bool>` (via EmitReturnValueTaskBool)
    // rather than a bare `ret false`, and the routing coexists with the pre-existing external
    // `generator.return()` path (the `__returnRequested` flag checked at each yield resume and after
    // each finally — see EmitYield and EmitTryCatchWithSuspensions). The two never collide: the
    // external path uses `__returnRequested` and is consulted before the dispatch, while in-body exits
    // use `<>pendingExit` and are dispatched only when `__returnRequested` is clear.

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
    // This is independent of CompilationContext.ExceptionBlockDepth (which only counts simple try
    // blocks and drives the Leave-vs-Br choice in EmitBranchToLabel).
    private int _protectedRegionDepth;

    // True while emitting a catch or finally body. A `throw` there must run the enclosing finally(s);
    // a `throw` in a try body is instead captured by its sync-segment mini try/catch (and so must not
    // be routed). Saved/restored around each region so nesting is handled correctly.
    private bool _inHandlerBody;

    // `<>pendingExit` (int): the code of an in-flight non-local exit, or 0 when none. A finally that
    // yields/awaits suspends MoveNextAsync mid-routing, so this must be a field (a local would reset on
    // re-entry). Set once per exit and cleared by the terminal dispatch; defaults to 0 on a fresh
    // state machine.
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
    // after the finally has run (#597, async analog of #555). Held across any suspension in those finallys.
    private FieldBuilder? _pendingReturnValueField;

    private FieldBuilder GetPendingReturnValueField() =>
        _pendingReturnValueField ??= _builder.StateMachineType.DefineField(
            "<>pendingReturnValue", typeof(object), FieldAttributes.Private);

    // ---- Loop-scope methods (override the base stack to use `_exitScopes`) -----------------------

    protected override void EnterLoop(Label breakLabel, Label continueLabel, string? labelName = null)
        // Adopt a pending label (`outer: for (...)`) just like the sync generator emitter does, so a
        // labeled `break`/`continue` can resolve this loop as its target. Without the fallback the
        // LoopScope registers with LabelName == null and FindLabeledLoopScope never matches, making a
        // labeled break/continue a silent no-op — the loop keeps iterating (#586).
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
            // innermore); otherwise branch there directly (#597, async analog of #554).
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
    /// no enclosing flag-based try it runs the active finally(s) and propagates out of MoveNextAsync. A
    /// throw in a try body is captured by its sync-segment mini try/catch, not here.
    /// </summary>
    protected override void EmitThrow(Stmt.Throw t)
    {
        if (_inHandlerBody && _protectedRegionDepth == 0)
        {
            if (_currentTryExceptionLocal != null)
            {
                EmitThrowIntoEnclosingTry(() => { EmitExpression(t.Value); EnsureBoxed(); });
                return;
            }

            // No enclosing flag-based try (this handler belongs to the outermost try), but its own
            // finally may still be active and must run before the throw leaves MoveNextAsync.
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
    /// Propagates a guest exception escaping a handler body into the enclosing flag-based try (tracked
    /// by <see cref="_currentTryExceptionLocal"/> et al. while emitting a catch/finally body): stores
    /// the value into that try's capture local and sets its present flag, then branches to its cleanup
    /// entry so its catch runs. Any finally(s) strictly inside that try run first; because such a
    /// finally can yield/await, the value is held in <c>&lt;&gt;pendingException</c> across them and
    /// moved into the capture local by the routing terminal (#632, async analog).
    /// </summary>
    private void EmitThrowIntoEnclosingTry(Action loadValue)
    {
        var enclException = _currentTryExceptionLocal!;
        var enclPresent = _currentTryExceptionPresentLocal!;
        var enclCleanup = _currentTryCleanupLabel;
        var chain = FinallyFramesInside(_currentTryScopeDepth);
        if (chain.Count == 0)
        {
            // No intervening finally: store straight into the enclosing try and branch to its catch.
            loadValue();
            _il.Emit(OpCodes.Stloc, enclException);
            _il.Emit(OpCodes.Ldc_I4_1);
            _il.Emit(OpCodes.Stloc, enclPresent);
            _il.Emit(OpCodes.Br, enclCleanup);
            return;
        }

        // Intervening finally(s) may yield/await, so hold the value in a field across them; the routing
        // terminal moves it into the enclosing try's capture local and branches to its catch.
        _il.Emit(OpCodes.Ldarg_0);
        loadValue();
        _il.Emit(OpCodes.Stfld, GetPendingExceptionField());
        int code = _nextExitCode++;
        _exitTerminals[code] = () =>
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, GetPendingExceptionField());
            _il.Emit(OpCodes.Stloc, enclException);
            _il.Emit(OpCodes.Ldc_I4_1);
            _il.Emit(OpCodes.Stloc, enclPresent);
            _il.Emit(OpCodes.Br, enclCleanup);
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
    /// try's own finally (just below scopeDepth) and everything outside it — the finallys a throw
    /// escaping a nested handler must run before reaching that try's catch (#632).
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
    /// so every real finally is innermore than every flag one and this ordering is correct (#597/#554).
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
        // (#597, async analog of #555). With no yielding finally this is a no-op (Current is unchanged).
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, GetPendingReturnValueField());
        _il.Emit(OpCodes.Stfld, _builder.CurrentField);

        // The generator completes. State -2 is re-asserted here because a yielding finally between the
        // `return` and this point overwrote it with the finally's resume state.
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, -2);
        _il.Emit(OpCodes.Stfld, _builder.StateField);
        EmitReturnValueTaskBool(false);
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

    // ---- Try/catch emission ---------------------------------------------------------------------

    protected override void EmitTryCatch(Stmt.TryCatch t)
    {
        // Check if this try block contains any suspension points (yield or await)
        bool hasSuspensionsInTry = ContainsSuspension(t.TryBlock);
        bool hasSuspensionsInCatch = t.CatchBlock != null && ContainsSuspension(t.CatchBlock);
        bool hasSuspensionsInFinally = t.FinallyBlock != null && ContainsSuspension(t.FinallyBlock);

        if (hasSuspensionsInTry || hasSuspensionsInCatch || hasSuspensionsInFinally)
        {
            // Cannot use IL exception blocks when suspension points exist inside them
            // because: (1) state switch can't jump into protected regions,
            // (2) yield/return can't use 'ret' inside try blocks,
            // (3) 'leave' from try/finally would trigger finally prematurely on yield.
            // Use flag-based exception tracking instead.
            EmitTryCatchWithSuspensions(t);
        }
        else
        {
            // No suspension points - safe to use IL exception blocks
            EmitSimpleTryCatch(t);
        }
    }

    private void EmitSimpleTryCatch(Stmt.TryCatch t)
    {
        // A real IL protected region is open: a routed `br`/`ret` out of it would be illegal, so
        // exits emitted inside fall back to their per-path handling (see the exit overrides).
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
                // Stack has the .NET exception; wrap to the TS value and bind to the catch param,
                // honouring a hoisted field if the param is read across a suspension (#569).
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
    /// Binds the caught exception value (on the IL stack) to the catch parameter, honouring whether
    /// the parameter was hoisted to a state-machine field (because it is read across a yield/await in
    /// the catch body) or lives in an IL local. Storing to a fresh local unconditionally — the
    /// previous behaviour — lost the value whenever the catch parameter was hoisted, because reads
    /// resolve the field first (#569, async analog of GeneratorMoveNextEmitter.StoreCaughtExceptionToParam).
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
    /// Flag-based try/catch/finally for the case where a suspension (yield / yield* / await) lives
    /// inside the protected region. Synchronous segments of the try body are wrapped in mini IL
    /// try/catch blocks that capture any exception into a flag local; suspension points and non-local
    /// exits are emitted at the top level (outside any protected region) so their resume labels are
    /// reachable from the state-dispatch switch and their `ret`/`br` are legal.
    /// </summary>
    private void EmitTryCatchWithSuspensions(Stmt.TryCatch t)
    {
        var caughtExceptionLocal = _il.DeclareLocal(typeof(object));
        // Whether the try body raised an exception, tracked separately from caughtExceptionLocal's
        // nullness: a thrown/rejected null/undefined captures as a null CLR reference, which a
        // value-nullness gate misreads as "no exception" — skipping the catch and dropping the
        // post-finally rethrow (#628, the async analog of #619). This flag records presence regardless
        // of the captured value.
        var exceptionPresentLocal = _il.DeclareLocal(typeof(bool));
        var afterTryBodyLabel = _il.DefineLabel();
        var afterTryCatchLabel = _il.DefineLabel();

        // No exception captured yet.
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Stloc, caughtExceptionLocal);
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Stloc, exceptionPresentLocal);

        // Two finally-running mechanisms share `afterTryBodyLabel` as the cleanup entry:
        //   1. External `generator.return()`: a suspended yield observes `__returnRequested` on resume
        //      and jumps here to run the finally (see EmitYield). `_returnCleanupLabel` carries the
        //      label down to those yields and is only meaningful while emitting *this* try's body.
        //   2. In-body non-local exits (#559): break/continue/return/throw register a FinallyScope and
        //      route through it; on those paths the exception flag is null, so the catch is skipped and
        //      the finally runs.
        // Without a finally there is nothing to run, so neither is set up and exits go directly.
        var previousCleanupLabel = _returnCleanupLabel;
        FinallyScope? frame = null;
        if (t.FinallyBlock != null)
        {
            _returnCleanupLabel = afterTryBodyLabel;
            frame = new FinallyScope { CleanupLabel = afterTryBodyLabel };
            _exitScopes.Add(frame);
        }

        // Throws in the try body are captured by their sync segments, not routed.
        bool previousInHandler = _inHandlerBody;
        _inHandlerBody = false;
        // Carry this try's capture target down to the suspension points emitted at the top level, so a
        // rejected `await` inside the try routes its exception into the same flag + catch/finally
        // instead of escaping MoveNextAsync (#617), setting the present flag so a null rejection still
        // engages the catch (#628). After the body these revert to the enclosing try, which a throw
        // escaping this try's catch/finally must route to (#632). Saved/restored for correct nesting.
        var previousTryExceptionLocal = _currentTryExceptionLocal;
        var previousTryExceptionPresentLocal = _currentTryExceptionPresentLocal;
        var previousTryCleanupLabel = _currentTryCleanupLabel;
        var previousTryScopeDepth = _currentTryScopeDepth;
        _currentTryExceptionLocal = caughtExceptionLocal;
        _currentTryExceptionPresentLocal = exceptionPresentLocal;
        _currentTryCleanupLabel = afterTryBodyLabel;
        _currentTryScopeDepth = _exitScopes.Count;
        EmitTryBodyWithSuspensions(t.TryBlock, caughtExceptionLocal, exceptionPresentLocal, afterTryBodyLabel);
        _currentTryExceptionLocal = previousTryExceptionLocal;
        _currentTryExceptionPresentLocal = previousTryExceptionPresentLocal;
        _currentTryCleanupLabel = previousTryCleanupLabel;
        _currentTryScopeDepth = previousTryScopeDepth;
        _inHandlerBody = previousInHandler;

        // Restore the enclosing try's cleanup label (its yields must route to it, not ours).
        _returnCleanupLabel = previousCleanupLabel;

        _il.MarkLabel(afterTryBodyLabel);

        // Catch: runs only when the try body captured an exception. The finally scope is still open, so
        // a non-local exit (including a throw) from the catch body runs this finally too.
        if (t.CatchBlock != null)
        {
            // Gate on the present flag, not the value's nullness, so a caught null/undefined enters the
            // catch (#628). The value local stays authoritative for the binding below.
            var skipCatchLabel = _il.DefineLabel();
            _il.Emit(OpCodes.Ldloc, exceptionPresentLocal);
            _il.Emit(OpCodes.Brfalse, skipCatchLabel);

            // Bind the captured value to the catch param, honouring a hoisted field when the param is
            // read across a suspension in the catch body (#569). Storing to a fresh local
            // unconditionally lost the value whenever the param was hoisted (reads resolve the field).
            // This binding is also what lets a rejected await routed here (#617) surface its reason.
            if (t.CatchParam != null)
            {
                _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
                StoreCaughtExceptionToParam(t.CatchParam.Lexeme);
            }

            // Catch handles it; clear the present flag so the post-finally rethrow below is skipped —
            // and so a routed exit re-entering afterTryBody skips the catch rather than re-running it.
            // The flag (not the value) is the gate now, so clearing it is what matters (#628).
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
            _inHandlerBody = true;
            foreach (var stmt in t.FinallyBlock)
                EmitStatement(stmt);
            _inHandlerBody = previousInHandler;

            // External return() (mechanism 1): __returnRequested set → complete the generator now that
            // the finally has run. Checked before the in-body dispatch because the two use different
            // flags (__returnRequested vs <>pendingExit) and an external return never sets the latter.
            var noReturnRequestedLabel = _il.DefineLabel();
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, _builder.ReturnRequestedField);
            _il.Emit(OpCodes.Brfalse, noReturnRequestedLabel);

            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4, -2);
            _il.Emit(OpCodes.Stfld, _builder.StateField);
            EmitReturnValueTaskBool(false);

            _il.MarkLabel(noReturnRequestedLabel);

            // In-body non-local exit (mechanism 2): dispatch any pending exit that routed through here.
            EmitFinallyDispatch(frame!);

            // Propagate an uncaught exception once the finally has run (try/finally with no catch).
            if (t.CatchBlock == null)
            {
                // Gate on the present flag so a thrown/rejected null/undefined still propagates (#628).
                var noExceptionLabel = _il.DefineLabel();
                _il.Emit(OpCodes.Ldloc, exceptionPresentLocal);
                _il.Emit(OpCodes.Brfalse, noExceptionLabel);

                if (_currentTryExceptionLocal != null)
                {
                    // The finally has run; the still-uncaught exception now propagates to the enclosing
                    // flag-based try's catch (not out of MoveNextAsync), so an outer catch still handles
                    // it — the try/finally analog of the handler-body throw routing (#632).
                    EmitThrowIntoEnclosingTry(() => _il.Emit(OpCodes.Ldloc, caughtExceptionLocal));
                }
                else
                {
                    // No enclosing flag-based try: mark done and propagate out of MoveNextAsync.
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldc_I4, -2);
                    _il.Emit(OpCodes.Stfld, _builder.StateField);
                    _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
                    _il.Emit(OpCodes.Call, _ctx!.Runtime!.CreateException);
                    _il.Emit(OpCodes.Throw);
                }

                _il.MarkLabel(noExceptionLabel);
            }
        }

        _il.MarkLabel(afterTryCatchLabel);
    }

    /// <summary>
    /// Walks the try body, wrapping runs of plain statements in mini IL try/catch blocks while
    /// emitting suspension points and non-local exits (return/break/continue) at the top level.
    /// </summary>
    private void EmitTryBodyWithSuspensions(List<Stmt> tryBody, LocalBuilder caughtExceptionLocal, LocalBuilder exceptionPresentLocal, Label afterTryLabel)
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
                // Gate on the present flag so a thrown null/undefined still short-circuits here (#628).
                _il.Emit(OpCodes.Ldloc, exceptionPresentLocal);
                _il.Emit(OpCodes.Brtrue, afterTryLabel);

                // Emitted at the top level: a yield/await's `ret`/resume label and a return's `ret` or a
                // break/continue's `br` are only legal outside a protected region.
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
        // a prior thrown null/undefined still suppresses this segment (#628).
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
        // as "no exception" at the gates above (#628).
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
        ContainsSuspensionInStmt(stmt) || ContainsEscapingExit(stmt, insideLoop: false, insideSwitch: false);

    private static bool ContainsSuspension(List<Stmt> statements)
    {
        foreach (var stmt in statements)
        {
            if (ContainsSuspensionInStmt(stmt))
                return true;
        }
        return false;
    }

    private static bool ContainsSuspensionInStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case Stmt.Expression e:
                return ContainsSuspensionInExpr(e.Expr);
            case Stmt.Var v:
                return v.Initializer != null && ContainsSuspensionInExpr(v.Initializer);
            case Stmt.Const c:
                return ContainsSuspensionInExpr(c.Initializer);
            case Stmt.Return r:
                return r.Value != null && ContainsSuspensionInExpr(r.Value);
            case Stmt.If i:
                return ContainsSuspensionInExpr(i.Condition) ||
                       ContainsSuspensionInStmt(i.ThenBranch) ||
                       (i.ElseBranch != null && ContainsSuspensionInStmt(i.ElseBranch));
            case Stmt.While w:
                return ContainsSuspensionInExpr(w.Condition) || ContainsSuspensionInStmt(w.Body);
            case Stmt.DoWhile dw:
                return ContainsSuspensionInStmt(dw.Body) || ContainsSuspensionInExpr(dw.Condition);
            case Stmt.For f:
                return (f.Initializer != null && ContainsSuspensionInStmt(f.Initializer)) ||
                       (f.Condition != null && ContainsSuspensionInExpr(f.Condition)) ||
                       (f.Increment != null && ContainsSuspensionInExpr(f.Increment)) ||
                       ContainsSuspensionInStmt(f.Body);
            case Stmt.ForOf fo:
                // `for await…of` now suspends on its implicit next()/return() awaits (#697), so it
                // contains a suspension even when neither the iterable nor the body has an explicit
                // yield/await. Treating it otherwise would put a `for await` inside a try on the real-IL
                // try path, where its resume labels become illegal BranchIntoTry targets (the async-gen
                // analog of the #631 ContainsAwait pitfall).
                return fo.IsAsync || ContainsSuspensionInExpr(fo.Iterable) || ContainsSuspensionInStmt(fo.Body);
            case Stmt.ForIn fi:
                return ContainsSuspensionInExpr(fi.Object) || ContainsSuspensionInStmt(fi.Body);
            case Stmt.Block b:
                return b.Statements != null && ContainsSuspension(b.Statements);
            case Stmt.Sequence seq:
                return ContainsSuspension(seq.Statements);
            case Stmt.LabeledStatement ls:
                return ContainsSuspensionInStmt(ls.Statement);
            case Stmt.Switch s:
                foreach (var c in s.Cases)
                {
                    if (ContainsSuspensionInExpr(c.Value) || ContainsSuspension(c.Body))
                        return true;
                }
                return s.DefaultBody != null && ContainsSuspension(s.DefaultBody);
            case Stmt.TryCatch t:
                return ContainsSuspension(t.TryBlock) ||
                       (t.CatchBlock != null && ContainsSuspension(t.CatchBlock)) ||
                       (t.FinallyBlock != null && ContainsSuspension(t.FinallyBlock));
            case Stmt.Throw th:
                return ContainsSuspensionInExpr(th.Value);
            case Stmt.Print p:
                return ContainsSuspensionInExpr(p.Expr);
            default:
                return false;
        }
    }

    private static bool ContainsSuspensionInExpr(Expr expr)
    {
        switch (expr)
        {
            case Expr.Yield:
            case Expr.Await:
                return true;
            case Expr.Comma c:
                return ContainsSuspensionInExpr(c.Left) || ContainsSuspensionInExpr(c.Right);
            case Expr.Binary b:
                return ContainsSuspensionInExpr(b.Left) || ContainsSuspensionInExpr(b.Right);
            case Expr.Logical l:
                return ContainsSuspensionInExpr(l.Left) || ContainsSuspensionInExpr(l.Right);
            case Expr.Unary u:
                return ContainsSuspensionInExpr(u.Right);
            case Expr.Delete d:
                return ContainsSuspensionInExpr(d.Operand);
            case Expr.Grouping g:
                return ContainsSuspensionInExpr(g.Expression);
            case Expr.Call c:
                if (ContainsSuspensionInExpr(c.Callee)) return true;
                foreach (var arg in c.Arguments)
                    if (ContainsSuspensionInExpr(arg)) return true;
                return false;
            case Expr.Assign a:
                return ContainsSuspensionInExpr(a.Value);
            case Expr.CompoundAssign ca:
                return ContainsSuspensionInExpr(ca.Value);
            case Expr.Ternary t:
                return ContainsSuspensionInExpr(t.Condition) ||
                       ContainsSuspensionInExpr(t.ThenBranch) ||
                       ContainsSuspensionInExpr(t.ElseBranch);
            case Expr.Get g:
                return ContainsSuspensionInExpr(g.Object);
            case Expr.Set s:
                return ContainsSuspensionInExpr(s.Object) || ContainsSuspensionInExpr(s.Value);
            case Expr.GetIndex gi:
                return ContainsSuspensionInExpr(gi.Object) || ContainsSuspensionInExpr(gi.Index);
            case Expr.SetIndex si:
                return ContainsSuspensionInExpr(si.Object) || ContainsSuspensionInExpr(si.Index) || ContainsSuspensionInExpr(si.Value);
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
