using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public abstract partial class AsyncFunctionMoveNextEmitter
{
    protected override void EmitTryCatch(Stmt.TryCatch t)
    {
        // Check if this try block contains any await points
        bool hasAwaitsInTry = ContainsAwait(t.TryBlock);
        bool hasAwaitsInCatch = t.CatchBlock != null && ContainsAwait(t.CatchBlock);
        bool hasAwaitsInFinally = t.FinallyBlock != null && ContainsAwait(t.FinallyBlock);

        if (hasAwaitsInTry || hasAwaitsInCatch || hasAwaitsInFinally)
        {
            // Complex case: await inside protected region
            EmitTryCatchWithAwaits(t, hasAwaitsInTry, hasAwaitsInCatch, hasAwaitsInFinally);
        }
        else
        {
            // Simple case: no awaits in protected regions
            EmitSimpleTryCatch(t);
        }
    }

    private void EmitSimpleTryCatch(Stmt.TryCatch t)
    {
        // A real IL protected region is open across the whole try/catch/finally. A `br`/`ret` directly
        // out of it is illegal, so a non-local break/continue crossing it must use `Leave` instead —
        // which also runs this (no-await) finally. ExceptionBlockDepth drives the Leave-vs-Br choice in
        // EmitBranchToLabel; it is incremented only here (not in the flag path's mini try/catch
        // segments), so a break targeting a loop nested inside the try stays a legal in-region `Br`
        // while an escaping break/continue leaves via `Leave` (#727).
        Ctx.ExceptionBlockDepth++;
        IL.BeginExceptionBlock();

        // Emit try block statements
        foreach (var stmt in t.TryBlock)
            EmitStatement(stmt);

        // Emit catch block if present
        if (t.CatchBlock != null)
        {
            IL.BeginCatchBlock(typeof(Exception));

            if (t.CatchParam != null)
            {
                // Create local for the exception parameter
                var exLocal = IL.DeclareLocal(typeof(object));
                Ctx.Locals.RegisterLocal(t.CatchParam.Lexeme, exLocal);

                // Wrap the .NET exception to TypeScript exception object
                IL.Emit(OpCodes.Call, Ctx.Runtime!.WrapException);
                IL.Emit(OpCodes.Stloc, exLocal);
            }
            else
            {
                // No catch parameter - just pop the exception
                IL.Emit(OpCodes.Pop);
            }

            // Emit catch block statements
            foreach (var stmt in t.CatchBlock)
                EmitStatement(stmt);
        }

        // Emit finally block if present
        if (t.FinallyBlock != null)
        {
            IL.BeginFinallyBlock();
            foreach (var stmt in t.FinallyBlock)
                EmitStatement(stmt);
        }

        IL.EndExceptionBlock();
        Ctx.ExceptionBlockDepth--;
    }

    private void EmitTryCatchWithAwaits(Stmt.TryCatch t, bool hasAwaitsInTry, bool hasAwaitsInCatch, bool hasAwaitsInFinally)
    {
        // For try blocks with awaits, we need to use a flag-based approach:
        // 1. Use a local to track if an exception was caught
        // 2. Emit try body with special handling around each await
        // 3. Check exception flag after try/await completion

        // Create locals for exception tracking
        var caughtExceptionLocal = IL.DeclareLocal(typeof(object));
        var skipCatchLabel = IL.DefineLabel();
        var afterTryCatchLabel = IL.DefineLabel();

        // Initialize caught exception to null
        IL.Emit(OpCodes.Ldnull);
        IL.Emit(OpCodes.Stloc, caughtExceptionLocal);

        // A finally must run on every path that leaves the try — including a non-local break / continue
        // / return that crosses it. Push a FinallyScope before emitting the try body so those exits
        // (emitted at the top level by EmitTryBodyWithAwaits / EmitReturn) route through it before
        // transferring control; the cleanup label is the finally's entry. The frame stays open across
        // the catch too, so an exit from the catch body runs this finally as well (#774).
        FinallyScope? frame = null;
        Label cleanupLabel = default;
        if (t.FinallyBlock != null)
        {
            cleanupLabel = IL.DefineLabel();
            frame = new FinallyScope { CleanupLabel = cleanupLabel };
            _exitScopes.Add(frame);
        }

        if (hasAwaitsInTry)
        {
            // Emit try body with segmented exception handling
            EmitTryBodyWithAwaits(t.TryBlock, caughtExceptionLocal);
        }
        else if (hasAwaitsInFinally)
        {
            // No awaits in try but awaits in finally - need to capture exception from try
            // so we can run the finally with awaits before rethrowing. The try body runs inside a real
            // IL exception block, so a non-local exit crossing it must Leave, not Br — bump
            // ExceptionBlockDepth so EmitBranchToLabel / the routing pick Leave to the cleanup (#774).
            Ctx.ExceptionBlockDepth++;
            IL.BeginExceptionBlock();
            foreach (var stmt in t.TryBlock)
                EmitStatement(stmt);

            // Always catch to capture exception for finally handling
            IL.BeginCatchBlock(typeof(Exception));
            IL.Emit(OpCodes.Call, Ctx.Runtime!.WrapException);
            IL.Emit(OpCodes.Stloc, caughtExceptionLocal);

            IL.EndExceptionBlock();
            Ctx.ExceptionBlockDepth--;
        }
        else
        {
            // No awaits in try or finally - use normal try block. Same real-IL-block reasoning as above:
            // bump ExceptionBlockDepth so a non-local exit crossing this try Leaves to the cleanup (#774).
            Ctx.ExceptionBlockDepth++;
            IL.BeginExceptionBlock();
            foreach (var stmt in t.TryBlock)
                EmitStatement(stmt);

            if (t.CatchBlock != null)
            {
                IL.BeginCatchBlock(typeof(Exception));
                IL.Emit(OpCodes.Call, Ctx.Runtime!.WrapException);
                IL.Emit(OpCodes.Stloc, caughtExceptionLocal);
            }
            IL.EndExceptionBlock();
            Ctx.ExceptionBlockDepth--;
        }

        // Cleanup entry: normal completion falls here, and a routed non-local exit branches here. The
        // catch is gated on caughtExceptionLocal below, which is null on every exit path, so those
        // paths skip the catch and flow straight into the finally (#774).
        if (frame != null)
            IL.MarkLabel(cleanupLabel);

        // Check if we need to run catch block
        if (t.CatchBlock != null)
        {
            IL.Emit(OpCodes.Ldloc, caughtExceptionLocal);
            IL.Emit(OpCodes.Brfalse, skipCatchLabel);

            // Store exception in catch param if needed
            if (t.CatchParam != null)
            {
                var exLocal = IL.DeclareLocal(typeof(object));
                Ctx.Locals.RegisterLocal(t.CatchParam.Lexeme, exLocal);
                IL.Emit(OpCodes.Ldloc, caughtExceptionLocal);
                IL.Emit(OpCodes.Stloc, exLocal);
            }

            // Clear the exception local since catch handled it
            IL.Emit(OpCodes.Ldnull);
            IL.Emit(OpCodes.Stloc, caughtExceptionLocal);

            // Emit catch block (may contain awaits)
            // If we're inside an outer try context, wrap catch statements in try/catch
            // so that throws propagate to the outer try's exception handling
            var outerExceptionLocal = _currentTryCatchExceptionLocal;
            if (outerExceptionLocal != null && outerExceptionLocal != caughtExceptionLocal)
            {
                // We're nested inside another try-with-awaits
                // Wrap catch block in try/catch to propagate exceptions outward
                IL.BeginExceptionBlock();
                foreach (var stmt in t.CatchBlock)
                    EmitStatement(stmt);
                IL.BeginCatchBlock(typeof(Exception));
                IL.Emit(OpCodes.Call, Ctx.Runtime!.WrapException);
                IL.Emit(OpCodes.Stloc, outerExceptionLocal);
                IL.EndExceptionBlock();
            }
            else
            {
                foreach (var stmt in t.CatchBlock)
                    EmitStatement(stmt);
            }

            // Fall through to finally (don't skip it)
            IL.MarkLabel(skipCatchLabel);
        }

        // The finally itself is outside its own scope: an exit within the finally body runs the
        // *enclosing* finallys, not this one. Pop before emitting the body (#774).
        if (frame != null)
            _exitScopes.RemoveAt(_exitScopes.Count - 1);

        // Finally block - must always execute
        if (t.FinallyBlock != null)
        {
            if (hasAwaitsInFinally)
            {
                // Finally with awaits - need special handling
                // The finally must run regardless of exception, so we emit it
                // and track if there's a pending exception to rethrow after
                EmitFinallyBodyWithAwaits(t.FinallyBlock, caughtExceptionLocal);
            }
            else
            {
                // No awaits in finally - emit directly
                foreach (var stmt in t.FinallyBlock)
                    EmitStatement(stmt);
            }

            // Dispatch any pending non-local exit (return / break / continue) that routed through this
            // finally: run the terminal action, or chain to the next outer finally (#774). On a normal
            // or exception path <>pendingExit is 0 and this falls through unchanged.
            EmitFinallyDispatch(frame!);

            // After finally, check if we need to rethrow a pending exception
            // (but only if there was no catch block that handled it)
            if (t.CatchBlock == null)
            {
                var noExceptionLabel = IL.DefineLabel();
                IL.Emit(OpCodes.Ldloc, caughtExceptionLocal);
                IL.Emit(OpCodes.Brfalse, noExceptionLabel);

                // Rethrow the exception
                IL.Emit(OpCodes.Ldloc, caughtExceptionLocal);
                IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateException);
                IL.Emit(OpCodes.Throw);

                IL.MarkLabel(noExceptionLabel);
            }
        }

        IL.MarkLabel(afterTryCatchLabel);
    }

    private void EmitFinallyBodyWithAwaits(List<Stmt> finallyBody, LocalBuilder caughtExceptionLocal)
    {
        // For finally with awaits, we use a similar strategy to try with awaits:
        // - Segment the code around awaits
        // - Each segment runs regardless of exception state
        // - After all awaits complete, we can rethrow if needed

        // Unlike try, we don't wrap in try/catch - finally must run even if
        // statements throw, but if finally itself throws, that replaces the original exception

        foreach (var stmt in finallyBody)
        {
            EmitStatement(stmt);
        }
    }

    private void EmitTryBodyWithAwaits(List<Stmt> tryBody, LocalBuilder caughtExceptionLocal)
    {
        // Strategy:
        // 1. Wrap synchronous segments in try blocks
        // 2. For awaits, wrap the GetResult call in try/catch using context fields
        // 3. After each await, check if an exception was caught

        // Set context for await exception handling
        var previousExceptionLocal = _currentTryCatchExceptionLocal;
        var previousSkipLabel = _currentTryCatchSkipLabel;
        var afterTryLabel = IL.DefineLabel();
        _currentTryCatchExceptionLocal = caughtExceptionLocal;
        _currentTryCatchSkipLabel = afterTryLabel;

        List<Stmt> syncSegment = [];

        void FlushSegment()
        {
            if (syncSegment.Count == 0)
                return;
            // Skip the segment if an earlier one already threw (its exception heads to the catch).
            var skipSegmentLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Ldloc, caughtExceptionLocal);
            IL.Emit(OpCodes.Brtrue, skipSegmentLabel);
            EmitSegmentInTry(syncSegment, caughtExceptionLocal);
            IL.MarkLabel(skipSegmentLabel);
            syncSegment.Clear();
        }

        foreach (var stmt in tryBody)
        {
            // A "segment breaker" must be emitted at the top level rather than inside a mini IL
            // try/catch: a suspension point (await) whose resume label can't be branched into a
            // protected region, or a non-local exit (break/continue/return) whose `Br`/`Leave` out of
            // the try targets the enclosing loop — both illegal inside the segment's real IL block. An
            // escaping break/continue branches with `Br` at this top level (ExceptionBlockDepth is 0),
            // which is what makes it legal; previously it landed inside a segment and `Br`'d out of the
            // mini try (BranchOutOfTry → invalid IL) (#727).
            if (ContainsAwait([stmt]) || ContainsEscapingExit(stmt, insideLoop: false, insideSwitch: false))
            {
                FlushSegment();

                // If an earlier segment threw, skip this suspension/exit and fall through to the catch.
                var skipLabel = IL.DefineLabel();
                IL.Emit(OpCodes.Ldloc, caughtExceptionLocal);
                IL.Emit(OpCodes.Brtrue, skipLabel);

                // EmitAwait checks _currentTryCatchExceptionLocal and wraps GetResult in try/catch; a
                // break/continue/return emits its jump here, outside any real IL exception block.
                EmitStatement(stmt);

                IL.MarkLabel(skipLabel);
            }
            else
            {
                syncSegment.Add(stmt);
            }
        }

        FlushSegment();

        IL.MarkLabel(afterTryLabel);

        // Restore context (the skip label must be restored alongside its
        // exception local — nulling it left outer-try awaits after a nested
        // try with no exit target).
        _currentTryCatchExceptionLocal = previousExceptionLocal;
        _currentTryCatchSkipLabel = previousSkipLabel;
    }

    private void EmitSegmentInTry(List<Stmt> statements, LocalBuilder caughtExceptionLocal)
    {
        IL.BeginExceptionBlock();
        foreach (var stmt in statements)
            EmitStatement(stmt);

        IL.BeginCatchBlock(typeof(Exception));
        IL.Emit(OpCodes.Call, Ctx.Runtime!.WrapException);
        IL.Emit(OpCodes.Stloc, caughtExceptionLocal);

        IL.EndExceptionBlock();
    }

    protected bool ContainsAwait(List<Stmt> statements)
    {
        foreach (var stmt in statements)
        {
            if (ContainsAwaitInStmt(stmt))
                return true;
        }
        return false;
    }

    protected bool ContainsAwaitInStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case Stmt.Expression e:
                return ContainsAwaitInExpr(e.Expr);
            case Stmt.Var v:
                return v.Initializer != null && ContainsAwaitInExpr(v.Initializer);
            case Stmt.Const c:
                return ContainsAwaitInExpr(c.Initializer);
            case Stmt.Return r:
                return r.Value != null && ContainsAwaitInExpr(r.Value);
            case Stmt.If i:
                return ContainsAwaitInExpr(i.Condition) ||
                       ContainsAwaitInStmt(i.ThenBranch) ||
                       (i.ElseBranch != null && ContainsAwaitInStmt(i.ElseBranch));
            case Stmt.While w:
                return ContainsAwaitInExpr(w.Condition) || ContainsAwaitInStmt(w.Body);
            case Stmt.DoWhile dw:
                return ContainsAwaitInStmt(dw.Body) || ContainsAwaitInExpr(dw.Condition);
            case Stmt.For f:
                return (f.Initializer != null && ContainsAwaitInStmt(f.Initializer)) ||
                       (f.Condition != null && ContainsAwaitInExpr(f.Condition)) ||
                       (f.Increment != null && ContainsAwaitInExpr(f.Increment)) ||
                       ContainsAwaitInStmt(f.Body);
            case Stmt.ForOf fo:
                // `for await…of` always suspends (it awaits iterator.next()/return()), even when the
                // iterable and body contain no explicit await — so a try enclosing one must take the
                // flag-based path, not a real IL try (whose resume labels would be branched into) (#631).
                return fo.IsAsync || ContainsAwaitInExpr(fo.Iterable) || ContainsAwaitInStmt(fo.Body);
            case Stmt.ForIn fi:
                return ContainsAwaitInExpr(fi.Object) || ContainsAwaitInStmt(fi.Body);
            case Stmt.Block b:
                return ContainsAwait(b.Statements);
            case Stmt.Sequence seq:
                return ContainsAwait(seq.Statements);
            case Stmt.TryCatch t:
                return ContainsAwait(t.TryBlock) ||
                       (t.CatchBlock != null && ContainsAwait(t.CatchBlock)) ||
                       (t.FinallyBlock != null && ContainsAwait(t.FinallyBlock));
            default:
                return false;
        }
    }

    protected bool ContainsAwaitInExpr(Expr expr)
    {
        switch (expr)
        {
            case Expr.Await:
                return true;
            case Expr.Comma c:
                return ContainsAwaitInExpr(c.Left) || ContainsAwaitInExpr(c.Right);
            case Expr.Binary b:
                return ContainsAwaitInExpr(b.Left) || ContainsAwaitInExpr(b.Right);
            case Expr.Logical l:
                return ContainsAwaitInExpr(l.Left) || ContainsAwaitInExpr(l.Right);
            case Expr.Unary u:
                return ContainsAwaitInExpr(u.Right);
            case Expr.Delete d:
                return ContainsAwaitInExpr(d.Operand);
            case Expr.Grouping g:
                return ContainsAwaitInExpr(g.Expression);
            case Expr.Call c:
                if (ContainsAwaitInExpr(c.Callee)) return true;
                foreach (var arg in c.Arguments)
                    if (ContainsAwaitInExpr(arg)) return true;
                return false;
            case Expr.Assign a:
                return ContainsAwaitInExpr(a.Value);
            case Expr.Ternary t:
                return ContainsAwaitInExpr(t.Condition) ||
                       ContainsAwaitInExpr(t.ThenBranch) ||
                       ContainsAwaitInExpr(t.ElseBranch);
            case Expr.Get g:
                return ContainsAwaitInExpr(g.Object);
            case Expr.Set s:
                return ContainsAwaitInExpr(s.Object) || ContainsAwaitInExpr(s.Value);
            default:
                return false;
        }
    }
}
