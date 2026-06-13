using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class GeneratorMoveNextEmitter
{
    // When emitting inside a flag-based try that has a finally, a `return` statement cannot
    // `ret` directly — the finally must run first. It jumps here instead (after recording the
    // pending return). Saved/restored around each try so nested finallys chain.
    private Label? _returnCleanupLabel;

    // Set by a `return` inside a flag-based try/finally so that, once the finally(s) have run,
    // MoveNext completes (returns false). Declared lazily; a finally that itself yields suspends
    // MoveNext between the return and the completion check, so this must be a state-machine field
    // (not an IL local, which the re-entry would reset) — mirrors the async generator's
    // ReturnRequestedField. Defaults to false on the freshly-allocated state machine and is set
    // at most once (the generator is completing), so it never needs resetting.
    private FieldBuilder? _pendingReturnField;

    private FieldBuilder GetPendingReturnField() =>
        _pendingReturnField ??= _builder.StateMachineType.DefineField(
            "<>pendingReturn", typeof(bool), FieldAttributes.Private);

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

        if (hasYields)
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
        var afterTryBodyLabel = _il.DefineLabel();

        // No exception captured yet.
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Stloc, caughtExceptionLocal);

        // A `return` inside this try must run the finally before completing, so point its cleanup
        // at the catch/finally entry. On the return path the exception flag is null, so the catch
        // is skipped and the finally runs. Only meaningful when a finally is present; without one,
        // a `return` completes directly (it is emitted at the top level, so `ret` is legal).
        var previousCleanupLabel = _returnCleanupLabel;
        if (t.FinallyBlock != null)
            _returnCleanupLabel = afterTryBodyLabel;

        EmitTryBodyWithYields(t.TryBlock, caughtExceptionLocal, afterTryBodyLabel);

        _returnCleanupLabel = previousCleanupLabel;

        _il.MarkLabel(afterTryBodyLabel);

        // Catch: runs only when the try body captured an exception.
        if (t.CatchBlock != null)
        {
            var skipCatchLabel = _il.DefineLabel();
            _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
            _il.Emit(OpCodes.Brfalse, skipCatchLabel);

            if (t.CatchParam != null)
            {
                _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
                StoreCaughtExceptionToParam(t.CatchParam.Lexeme);
            }

            // Catch handles it; clear the flag so the post-finally rethrow below is skipped.
            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Stloc, caughtExceptionLocal);

            foreach (var stmt in t.CatchBlock)
                EmitStatement(stmt);

            _il.MarkLabel(skipCatchLabel);
        }

        // Finally: always runs — on normal completion, after a caught exception, or on a pending
        // return that routed here.
        if (t.FinallyBlock != null)
        {
            foreach (var stmt in t.FinallyBlock)
                EmitStatement(stmt);

            // After the finally, propagate a pending `return` — to the next enclosing finally if
            // there is one, otherwise complete MoveNext.
            if (_pendingReturnField != null)
            {
                var noPendingReturnLabel = _il.DefineLabel();
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, _pendingReturnField);
                _il.Emit(OpCodes.Brfalse, noPendingReturnLabel);

                if (previousCleanupLabel != null)
                    _il.Emit(OpCodes.Br, previousCleanupLabel.Value);
                else
                {
                    _il.Emit(OpCodes.Ldc_I4_0);
                    _il.Emit(OpCodes.Ret);
                }

                _il.MarkLabel(noPendingReturnLabel);
            }
        }

        // Rethrow an uncaught exception once the finally has run (try/finally with no catch).
        if (t.CatchBlock == null)
        {
            var noExceptionLabel = _il.DefineLabel();
            _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
            _il.Emit(OpCodes.Brfalse, noExceptionLabel);

            _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
            _il.Emit(OpCodes.Call, _ctx!.Runtime!.CreateException);
            _il.Emit(OpCodes.Throw);

            _il.MarkLabel(noExceptionLabel);
        }
    }

    /// <summary>
    /// Walks the try body, wrapping runs of plain statements in mini IL try/catch blocks while
    /// emitting suspension points and non-local exits (return/break/continue) at the top level.
    /// </summary>
    private void EmitTryBodyWithYields(List<Stmt> tryBody, LocalBuilder caughtExceptionLocal, Label afterTryLabel)
    {
        List<Stmt> syncSegment = [];

        foreach (var stmt in tryBody)
        {
            if (IsSegmentBreaker(stmt))
            {
                // Flush the accumulated plain statements first.
                if (syncSegment.Count > 0)
                {
                    EmitSyncSegmentInTry(syncSegment, caughtExceptionLocal);
                    syncSegment.Clear();
                }

                // If an earlier segment threw, skip the suspension/exit and head to catch/finally.
                _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
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
            EmitSyncSegmentInTry(syncSegment, caughtExceptionLocal);
    }

    /// <summary>
    /// Emits a run of plain (non-suspending, non-exiting) statements inside a real IL try/catch
    /// that records any thrown exception into <paramref name="caughtExceptionLocal"/>.
    /// </summary>
    private void EmitSyncSegmentInTry(List<Stmt> statements, LocalBuilder caughtExceptionLocal)
    {
        // An earlier segment may already have thrown — don't run this one.
        var skipLabel = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
        _il.Emit(OpCodes.Brtrue, skipLabel);

        _il.BeginExceptionBlock();
        foreach (var stmt in statements)
            EmitStatement(stmt);

        _il.BeginCatchBlock(typeof(Exception));
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.WrapException);
        _il.Emit(OpCodes.Stloc, caughtExceptionLocal);
        _il.EndExceptionBlock();

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
