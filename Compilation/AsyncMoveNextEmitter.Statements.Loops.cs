using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncMoveNextEmitter
{
    // Per-method counter giving each for await…of loop unique iterator/protocol field names.
    private int _forAwaitCounter;

    // EmitWhile: inherited from StatementEmitterBase (identical logic)

    protected override void EmitForOf(Stmt.ForOf f)
    {
        if (f.IsAsync)
        {
            // for await...of: EmitForAwaitOf is overridden below to suspend (vs the blocking shared base).
            EmitForAwaitOf(f);
            return;
        }

        // Sync for...of: delegate to base (uses DeclareLoopVariable/EmitStoreLoopVariable
        // overrides in this class to handle hoisted state machine fields)
        base.EmitForOf(f);
    }

    /// <summary>
    /// Async-function override of the shared <see cref="StatementEmitterBase.EmitForAwaitOf"/> (#430/#645).
    /// The shared base drives the async iterator with a blocking GetResult; this override instead SUSPENDS
    /// the state machine on each next()/return() await, so a genuinely-async step (e.g. a setTimeout-backed
    /// await inside the iterator) doesn't deadlock the event-loop thread (#631). The analyzer reserved one
    /// suspension state for each await (AsyncStateAnalyzer.VisitForOf), consumed below in the same order.
    /// The base lowering still serves async arrows and async generators (the latter tracked by #697).
    /// </summary>
    protected override void EmitForAwaitOf(Stmt.ForOf f) => EmitForAwaitOf(f, labelName: null);

    /// <summary>
    /// Emits a <c>for await…of</c> loop, optionally carrying a statement <paramref name="labelName"/> so a
    /// labeled <c>break</c>/<c>continue &lt;label&gt;</c> targets it (the labeled path delegates here so it
    /// gets the same suspending async-iterator lowering as the unlabeled case, rather than enumerating the
    /// async iterator synchronously and leaving the reserved await-state labels unmarked — #728).
    /// </summary>
    private void EmitForAwaitOf(Stmt.ForOf f, string? labelName)
    {
        // for await…of drives an async iterator: resolve it (Symbol.asyncIterator, else assume the
        // value is itself an async iterator / $IAsyncGenerator), then each iteration await
        // iterator.next() and, on an early `break`, await iterator.return(). Both awaits SUSPEND the
        // enclosing async function via its state machine rather than blocking on GetResult — so a
        // genuinely-async step (e.g. a setTimeout-backed await inside the iterator) no longer deadlocks
        // the event-loop thread (#631). The analyzer reserved one suspension state for each await
        // (AsyncStateAnalyzer.VisitForOf), consumed below in the same order.

        string varName = f.Variable.Lexeme;
        var varField = _builder.GetVariableField(varName);

        var iterableLocal = _il.DeclareLocal(_types.Object);
        var asyncIterFnLocal = _il.DeclareLocal(_types.Object);

        // The iterator and its protocol kind must survive the per-iteration suspensions (a MoveNext
        // re-entry wipes IL locals), so store them in state-machine fields. Unique per loop so nested /
        // sequential for-await loops don't collide. The type is still open here (CreateType runs after
        // MoveNext), so defining fields now is valid.
        int loopId = _forAwaitCounter++;
        var iteratorField = _builder.StateMachineType.DefineField($"<>7__aiter{loopId}", _types.Object, FieldAttributes.Private);
        var isCustomField = _builder.StateMachineType.DefineField($"<>7__aiterCustom{loopId}", _types.Boolean, FieldAttributes.Private);

        // Synchronous prelude: evaluate the iterable and resolve the iterator + protocol kind:
        //   isCustom == true  → iterable[Symbol.asyncIterator]() ; step via InvokeIteratorNext / "return".
        //   isCustom == false → the value is itself the iterator   ; step via the $IAsyncGenerator interface.
        // Evaluating the iterable, or calling its [Symbol.asyncIterator], can throw synchronously (e.g. a
        // pre-aborted signal) — guarded below so that throw reaches an enclosing try's catch.
        void EmitPrelude()
        {
            EmitExpression(f.Iterable);
            EnsureBoxed();
            _il.Emit(OpCodes.Stloc, iterableLocal);

            _il.Emit(OpCodes.Ldloc, iterableLocal);
            _il.Emit(OpCodes.Ldsfld, _ctx!.Runtime!.SymbolAsyncIterator);
            _il.Emit(OpCodes.Call, _ctx.Runtime.GetIteratorFunction);
            _il.Emit(OpCodes.Stloc, asyncIterFnLocal);

            var customSetup = _il.DefineLabel();
            var afterSetup = _il.DefineLabel();
            _il.Emit(OpCodes.Ldloc, asyncIterFnLocal);
            _il.Emit(OpCodes.Brtrue, customSetup);

            // iterator = iterable; isCustom = false
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloc, iterableLocal);
            _il.Emit(OpCodes.Stfld, iteratorField);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Stfld, isCustomField);
            _il.Emit(OpCodes.Br, afterSetup);

            // iterator = iterable[Symbol.asyncIterator](); isCustom = true
            _il.MarkLabel(customSetup);
            var iteratorTemp = _il.DeclareLocal(_types.Object);
            _il.Emit(OpCodes.Ldloc, iterableLocal);
            _il.Emit(OpCodes.Ldloc, asyncIterFnLocal);
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Newarr, _types.Object);
            _il.Emit(OpCodes.Call, _ctx.Runtime.InvokeMethodValue);
            _il.Emit(OpCodes.Stloc, iteratorTemp);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloc, iteratorTemp);
            _il.Emit(OpCodes.Stfld, iteratorField);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4_1);
            _il.Emit(OpCodes.Stfld, isCustomField);

            _il.MarkLabel(afterSetup);
        }

        // The prelude is await-free unless the iterable expression itself awaits; only then can it not
        // sit in a real IL try (its resume label would be branched into) — in that case the iterable's
        // own await handling routes rejections, and the (rare) synchronous resolution throw is unguarded.
        if (ContainsAwaitInExpr(f.Iterable))
            EmitPrelude();
        else
            EmitGuardedSyncSegment(EmitPrelude);

        int nextState = _currentAwaitState++;   // iterator.next() await (reused each iteration)

        var startLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();
        var cleanupLabel = _il.DefineLabel();
        var continueLabel = _il.DefineLabel();

        // break → cleanup (await iterator.return()); natural done → endLabel (no return() per spec).
        // labelName (when set) lets `break`/`continue <label>` resolve to this loop.
        EnterLoop(cleanupLabel, continueLabel, labelName);

        _il.MarkLabel(startLabel);

        // result = await iterator.next()
        var stepLocal = _il.DeclareLocal(_types.Object);
        EmitAsyncStep(stepLocal, iteratorField, isCustomField, isReturn: false);
        _il.Emit(OpCodes.Ldloc, stepLocal);
        SetStackUnknown();
        EmitAwaitFromValueOnStack(nextState);
        var resultLocal = _il.DeclareLocal(_types.Object);
        _il.Emit(OpCodes.Stloc, resultLocal);

        // if (result.done) exit without calling return()
        _il.Emit(OpCodes.Ldloc, resultLocal);
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.GetIteratorDone);
        _il.Emit(OpCodes.Brtrue, endLabel);

        // loopVar = result.value
        _il.Emit(OpCodes.Ldloc, resultLocal);
        _il.Emit(OpCodes.Call, _ctx.Runtime.GetIteratorValue);
        if (varField != null)
        {
            var valueTemp = _il.DeclareLocal(_types.Object);
            _il.Emit(OpCodes.Stloc, valueTemp);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloc, valueTemp);
            _il.Emit(OpCodes.Stfld, varField);
        }
        else
        {
            var varLocal = _il.DeclareLocal(_types.Object);
            _ctx.Locals.RegisterLocal(varName, varLocal);
            _il.Emit(OpCodes.Stloc, varLocal);
        }

        EmitForAwaitBody(f.Body);

        _il.MarkLabel(continueLabel);
        _il.Emit(OpCodes.Br, startLabel);

        // Cleanup on break: await iterator.return() (runs the iterator's finally blocks), discard result.
        int returnState = _currentAwaitState++;   // allocated after the body, matching the analyzer
        _il.MarkLabel(cleanupLabel);
        var returnStepLocal = _il.DeclareLocal(_types.Object);
        EmitAsyncStep(returnStepLocal, iteratorField, isCustomField, isReturn: true);
        _il.Emit(OpCodes.Ldloc, returnStepLocal);
        SetStackUnknown();
        EmitAwaitFromValueOnStack(returnState);
        _il.Emit(OpCodes.Pop);

        _il.MarkLabel(endLabel);
        ExitLoop();
    }

    /// <summary>
    /// Emits the for-await loop body. When the loop sits inside a try-with-awaits (flag-based, so there is
    /// no real IL try around it), a synchronous <c>throw</c> in the body must still reach that try's catch.
    /// The body can't sit inside a real IL try when it contains awaits (their resume labels would be
    /// branched into) or top-level break/continue (which the async emitter branches to with <c>Br</c>, not
    /// <c>Leave</c>); for an await-free, jump-free body we wrap it in a real try that captures the throw
    /// into the try's exception local and exits the loop to the catch dispatch. Other bodies use the plain
    /// path — a rejected next()/return() is still caught (that path is suspension-based), only a *synchronous*
    /// throw inside such a body escapes (a narrow, pre-existing-style limitation). (#631)
    /// </summary>
    private void EmitForAwaitBody(Stmt body)
    {
        // An await-free, jump-free body can sit in a real IL try (see EmitGuardedSyncSegment); a body
        // with awaits (resume labels would be branched into) or break/continue (Br out of the try is
        // invalid) keeps the plain path — only a *synchronous* throw inside such a body escapes the
        // enclosing try, a narrow limitation.
        if (!ContainsAwaitInStmt(body) && !ContainsBreakOrContinue(body))
            EmitGuardedSyncSegment(() => EmitStatement(body));
        else
            EmitStatement(body);
    }

    /// <summary>
    /// Runs <paramref name="emit"/> (a synchronous, suspension-free IL span that leaves the eval stack
    /// balanced) under the enclosing try-with-awaits, if any: a throw from the span is captured into the
    /// try's exception local and control jumps to the catch dispatch. Outside such a try it just runs the
    /// span. Lets the for-await prelude / step / body route synchronous throws to a guest catch even though
    /// the loop as a whole can't sit in a real IL try (its awaits would be branched into). (#631)
    /// </summary>
    private void EmitGuardedSyncSegment(System.Action emit)
    {
        if (_currentTryCatchExceptionLocal == null || _currentTryCatchSkipLabel == null)
        {
            emit();
            return;
        }
        var exLocal = _currentTryCatchExceptionLocal;
        _il.BeginExceptionBlock();
        emit();
        _il.BeginCatchBlock(typeof(Exception));
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.WrapException);
        _il.Emit(OpCodes.Stloc, exLocal);
        _il.EndExceptionBlock();
        _il.Emit(OpCodes.Ldloc, exLocal);
        _il.Emit(OpCodes.Brtrue, _currentTryCatchSkipLabel.Value);
    }

    /// <summary>
    /// True if <paramref name="stmt"/> contains a <c>break</c>/<c>continue</c> anywhere (a conservative
    /// over-approximation — it does not exclude jumps that target a loop/switch nested in the body). Used
    /// to keep such bodies off the real-IL-try path in <see cref="EmitForAwaitBody"/>, where a <c>Br</c>
    /// out of the try would be invalid IL.
    /// </summary>
    private static bool ContainsBreakOrContinue(Stmt stmt) => stmt switch
    {
        Stmt.Break => true,
        Stmt.Continue => true,
        Stmt.Block b => b.Statements.Any(ContainsBreakOrContinue),
        Stmt.Sequence s => s.Statements.Any(ContainsBreakOrContinue),
        Stmt.If i => ContainsBreakOrContinue(i.ThenBranch) || (i.ElseBranch != null && ContainsBreakOrContinue(i.ElseBranch)),
        Stmt.While w => ContainsBreakOrContinue(w.Body),
        Stmt.DoWhile d => ContainsBreakOrContinue(d.Body),
        Stmt.For f => ContainsBreakOrContinue(f.Body),
        Stmt.ForOf fo => ContainsBreakOrContinue(fo.Body),
        Stmt.ForIn fi => ContainsBreakOrContinue(fi.Body),
        Stmt.LabeledStatement l => ContainsBreakOrContinue(l.Statement),
        Stmt.Switch sw => sw.Cases.Any(c => c.Body.Any(ContainsBreakOrContinue)) || (sw.DefaultBody?.Any(ContainsBreakOrContinue) ?? false),
        Stmt.TryCatch t => t.TryBlock.Any(ContainsBreakOrContinue)
            || (t.CatchBlock?.Any(ContainsBreakOrContinue) ?? false)
            || (t.FinallyBlock?.Any(ContainsBreakOrContinue) ?? false),
        _ => false
    };

    /// <summary>
    /// Produces one async-iterator protocol step into <paramref name="resultLocal"/>, guarding the call
    /// itself. The protocol call (iterator.next()/return()) can throw <em>synchronously</em> — e.g. a
    /// pre-aborted signal makes next() throw rather than reject — and that throw happens before the await,
    /// so when inside a try-with-awaits it must be captured into the try's exception local and routed to
    /// the catch (otherwise it escapes the state machine). Outside a try, it propagates normally. (#631)
    /// </summary>
    private void EmitAsyncStep(LocalBuilder resultLocal, FieldBuilder iteratorField, FieldBuilder isCustomField, bool isReturn)
        => EmitGuardedSyncSegment(() => EmitProduceAsyncStep(resultLocal, iteratorField, isCustomField, isReturn));

    /// <summary>
    /// Stores into <paramref name="resultLocal"/> the result of one async-iterator protocol call —
    /// <c>iterator.next()</c> when <paramref name="isReturn"/> is false, else <c>iterator.return()</c> —
    /// selecting the call by the protocol kind in <paramref name="isCustomField"/>. The value (a
    /// Task&lt;object&gt;, $Promise, or plain value) is later coerced and awaited by
    /// <see cref="EmitAwaitFromValueOnStack"/>. A custom iterator with no <c>return</c> method stores
    /// <c>undefined</c> (awaited as an already-resolved value), so the reserved return-await state is
    /// still consumed and its resume label stays marked.
    /// </summary>
    private void EmitProduceAsyncStep(LocalBuilder resultLocal, FieldBuilder iteratorField, FieldBuilder isCustomField, bool isReturn)
    {
        var stepLocal = resultLocal;
        var customLabel = _il.DefineLabel();
        var doneLabel = _il.DefineLabel();

        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, isCustomField);
        _il.Emit(OpCodes.Brtrue, customLabel);

        // ----- $IAsyncGenerator path: invoke the interface method (returns Task<object>) -----
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, iteratorField);
        _il.Emit(OpCodes.Castclass, _ctx!.Runtime!.AsyncGeneratorInterfaceType);
        if (isReturn)
        {
            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Callvirt, _ctx.Runtime.AsyncGeneratorReturnMethod);
        }
        else
        {
            _il.Emit(OpCodes.Callvirt, _ctx.Runtime.AsyncGeneratorNextMethod);
        }
        _il.Emit(OpCodes.Stloc, stepLocal);
        _il.Emit(OpCodes.Br, doneLabel);

        // ----- custom Symbol.asyncIterator path -----
        _il.MarkLabel(customLabel);
        if (isReturn)
        {
            // fn = GetProperty(iterator, "return"); absent/undefined → no-op close (push undefined).
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, iteratorField);
            _il.Emit(OpCodes.Ldstr, "return");
            _il.Emit(OpCodes.Call, _ctx.Runtime.GetProperty);
            var fnLocal = _il.DeclareLocal(_types.Object);
            _il.Emit(OpCodes.Stloc, fnLocal);

            var noFnLabel = _il.DefineLabel();
            var haveFnLabel = _il.DefineLabel();
            _il.Emit(OpCodes.Ldloc, fnLocal);
            _il.Emit(OpCodes.Brfalse, noFnLabel);
            _il.Emit(OpCodes.Ldloc, fnLocal);
            _il.Emit(OpCodes.Isinst, _ctx.Runtime.UndefinedType);
            _il.Emit(OpCodes.Brtrue, noFnLabel);

            // InvokeMethodValue(iterator, fn, [])
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, iteratorField);
            _il.Emit(OpCodes.Ldloc, fnLocal);
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Newarr, _types.Object);
            _il.Emit(OpCodes.Call, _ctx.Runtime.InvokeMethodValue);
            _il.Emit(OpCodes.Stloc, stepLocal);
            _il.Emit(OpCodes.Br, haveFnLabel);

            _il.MarkLabel(noFnLabel);
            _il.Emit(OpCodes.Ldsfld, _ctx.Runtime.UndefinedInstance);
            _il.Emit(OpCodes.Stloc, stepLocal);
            _il.MarkLabel(haveFnLabel);
        }
        else
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, iteratorField);
            _il.Emit(OpCodes.Call, _ctx.Runtime.InvokeIteratorNext);
            _il.Emit(OpCodes.Stloc, stepLocal);
        }

        _il.MarkLabel(doneLabel);
        // Result is left in resultLocal (== stepLocal); the caller loads and awaits it.
    }

    // EmitDoWhile: inherited from StatementEmitterBase (identical logic)
    // EmitForIn: inherited from StatementEmitterBase (uses DeclareLoopVariable/EmitStoreLoopVariable
    // overrides in this class to handle hoisted state machine fields)
}
