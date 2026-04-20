namespace SharpTS.Parsing;

/// <summary>
/// Lifts generator function EXPRESSIONS (represented as <see cref="Expr.ArrowFunction"/> with
/// <c>IsGenerator = true</c>) into top-level <see cref="Stmt.Function"/> declarations.
///
/// The IL compiler has a mature generator-declaration pipeline (<c>GeneratorStateMachineBuilder</c>
/// + <c>GeneratorMoveNextEmitter</c>) but no arrow-expression counterpart. Rather than duplicate
/// that infrastructure for the relatively rare generator-expression case, this pass rewrites
/// each occurrence so the existing declaration path handles it. Example:
/// <code>
/// Yallist.prototype[Symbol.iterator] = function* () {
///     for (let w = this.head; w; w = w.next) yield w.value;
/// }
/// </code>
/// becomes:
/// <code>
/// function __genArrow_0() {
///     for (let w = this.head; w; w = w.next) yield w.value;
/// }
/// Yallist.prototype[Symbol.iterator] = __genArrow_0;
/// </code>
///
/// The generator body's only non-local reference in the common prototype-iterator idiom is
/// <c>this</c>, which binds from the call site — not from the surrounding lexical scope — so
/// it is preserved across the lift. The rewriter returns the original AST node whenever no
/// change was required, which keeps downstream passes (e.g. the type checker) from seeing
/// fresh reference identities for untouched subtrees.
/// </summary>
internal sealed class GeneratorArrowLifter
{
    private readonly List<Stmt.Function> _liftedFunctions = new();
    private int _counter;

    public static List<Stmt> Lift(List<Stmt> body)
    {
        // Cheap pre-scan: if there are no generator function expressions anywhere, return the
        // input unchanged. This avoids walking the whole AST for every module — almost no
        // real-world CJS module has one. Uses object identity so we can return the input list
        // unchanged when no lifting is needed.
        if (!ContainsGeneratorArrow(body)) return body;

        var lifter = new GeneratorArrowLifter();
        var rewritten = new List<Stmt>(body.Count);
        foreach (var stmt in body)
        {
            rewritten.Add(lifter.RewriteStmt(stmt));
        }
        if (lifter._liftedFunctions.Count == 0) return body;

        var result = new List<Stmt>(lifter._liftedFunctions.Count + rewritten.Count);
        result.AddRange(lifter._liftedFunctions);
        result.AddRange(rewritten);
        return result;
    }

    private static bool ContainsGeneratorArrow(List<Stmt> body)
    {
        var scanner = new GeneratorArrowScanner();
        foreach (var stmt in body)
        {
            scanner.Visit(stmt);
            if (scanner.Found) return true;
        }
        return false;
    }

    private sealed class GeneratorArrowScanner : Visitors.AstVisitorBase
    {
        public bool Found { get; private set; }

        protected override void VisitArrowFunction(Expr.ArrowFunction expr)
        {
            if (expr.IsGenerator)
            {
                Found = true;
                ShouldContinue = false;
                return;
            }
            base.VisitArrowFunction(expr);
        }
    }

    private Stmt RewriteStmt(Stmt stmt)
    {
        return stmt switch
        {
            Stmt.Expression e => ReplaceIfChanged(e, e.Expr, RewriteExpr(e.Expr), (s, expr) => new Stmt.Expression(expr)),
            Stmt.Return r => r.Value is null ? r : ReplaceIfChanged(r, r.Value, RewriteExpr(r.Value), (s, expr) => new Stmt.Return(s.Keyword, expr)),
            Stmt.Var v => v.Initializer is null ? v : ReplaceIfChanged(v, v.Initializer, RewriteExpr(v.Initializer), (s, expr) => s with { Initializer = expr }),
            Stmt.Const c => ReplaceIfChanged(c, c.Initializer, RewriteExpr(c.Initializer), (s, expr) => s with { Initializer = expr }),
            Stmt.Block b => RewriteBlock(b),
            Stmt.If i => RewriteIf(i),
            Stmt.While w => RewriteWhile(w),
            Stmt.Function f => RewriteFunction(f),
            _ => stmt,
        };
    }

    private static Stmt ReplaceIfChanged<T>(T original, Expr originalChild, Expr newChild, Func<T, Expr, Stmt> factory)
        where T : Stmt
    {
        return ReferenceEquals(originalChild, newChild) ? original : factory(original, newChild);
    }

    private Stmt RewriteBlock(Stmt.Block b)
    {
        List<Stmt>? rewritten = null;
        for (int i = 0; i < b.Statements.Count; i++)
        {
            var original = b.Statements[i];
            var rewrittenStmt = RewriteStmt(original);
            if (!ReferenceEquals(original, rewrittenStmt))
            {
                rewritten ??= new List<Stmt>(b.Statements);
                rewritten[i] = rewrittenStmt;
            }
        }
        return rewritten is null ? b : new Stmt.Block(rewritten);
    }

    private Stmt RewriteIf(Stmt.If i)
    {
        var newCond = RewriteExpr(i.Condition);
        var newThen = RewriteStmt(i.ThenBranch);
        var newElse = i.ElseBranch is null ? null : RewriteStmt(i.ElseBranch);
        if (ReferenceEquals(newCond, i.Condition)
            && ReferenceEquals(newThen, i.ThenBranch)
            && (i.ElseBranch is null || ReferenceEquals(newElse, i.ElseBranch)))
            return i;
        return new Stmt.If(newCond, newThen, newElse);
    }

    private Stmt RewriteWhile(Stmt.While w)
    {
        var newCond = RewriteExpr(w.Condition);
        var newBody = RewriteStmt(w.Body);
        if (ReferenceEquals(newCond, w.Condition) && ReferenceEquals(newBody, w.Body)) return w;
        return new Stmt.While(newCond, newBody);
    }

    private Stmt RewriteFunction(Stmt.Function f)
    {
        if (f.Body is null) return f;
        List<Stmt>? rewritten = null;
        for (int i = 0; i < f.Body.Count; i++)
        {
            var original = f.Body[i];
            var rewrittenStmt = RewriteStmt(original);
            if (!ReferenceEquals(original, rewrittenStmt))
            {
                rewritten ??= new List<Stmt>(f.Body);
                rewritten[i] = rewrittenStmt;
            }
        }
        return rewritten is null ? f : f with { Body = rewritten };
    }

    private Expr RewriteExpr(Expr expr)
    {
        switch (expr)
        {
            case Expr.ArrowFunction af when af.IsGenerator && af.BlockBody is not null:
                return LiftGeneratorArrow(af);
            case Expr.ArrowFunction af:
            {
                List<Stmt>? newBody = af.BlockBody is null ? null : RewriteListIfChanged(af.BlockBody, RewriteStmt);
                var newExpr = af.ExpressionBody is null ? null : RewriteExpr(af.ExpressionBody);
                bool bodyChanged = !ReferenceEquals(newBody, af.BlockBody);
                bool exprChanged = !ReferenceEquals(newExpr, af.ExpressionBody);
                if (!bodyChanged && !exprChanged) return af;
                return af with { BlockBody = newBody, ExpressionBody = newExpr };
            }
            case Expr.Assign a:
            {
                var v = RewriteExpr(a.Value);
                return ReferenceEquals(v, a.Value) ? a : new Expr.Assign(a.Name, v);
            }
            case Expr.Set s:
            {
                var o = RewriteExpr(s.Object);
                var v = RewriteExpr(s.Value);
                return ReferenceEquals(o, s.Object) && ReferenceEquals(v, s.Value) ? s : new Expr.Set(o, s.Name, v);
            }
            case Expr.SetIndex si:
            {
                var o = RewriteExpr(si.Object);
                var idx = RewriteExpr(si.Index);
                var v = RewriteExpr(si.Value);
                return ReferenceEquals(o, si.Object) && ReferenceEquals(idx, si.Index) && ReferenceEquals(v, si.Value)
                    ? si : new Expr.SetIndex(o, idx, v);
            }
            case Expr.Binary b:
            {
                var l = RewriteExpr(b.Left); var r = RewriteExpr(b.Right);
                return ReferenceEquals(l, b.Left) && ReferenceEquals(r, b.Right) ? b : new Expr.Binary(l, b.Operator, r);
            }
            case Expr.Logical lg:
            {
                var l = RewriteExpr(lg.Left); var r = RewriteExpr(lg.Right);
                return ReferenceEquals(l, lg.Left) && ReferenceEquals(r, lg.Right) ? lg : new Expr.Logical(l, lg.Operator, r);
            }
            case Expr.Call c:
            {
                var callee = RewriteExpr(c.Callee);
                var args = RewriteListIfChanged(c.Arguments, RewriteExpr);
                return ReferenceEquals(callee, c.Callee) && ReferenceEquals(args, c.Arguments)
                    ? c : new Expr.Call(callee, c.Paren, c.TypeArgs, args, c.Optional);
            }
            case Expr.Get g:
            {
                var o = RewriteExpr(g.Object);
                return ReferenceEquals(o, g.Object) ? g : new Expr.Get(o, g.Name, g.Optional);
            }
            case Expr.GetIndex gi:
            {
                var o = RewriteExpr(gi.Object);
                var idx = RewriteExpr(gi.Index);
                return ReferenceEquals(o, gi.Object) && ReferenceEquals(idx, gi.Index)
                    ? gi : new Expr.GetIndex(o, idx, gi.Optional);
            }
            default:
                return expr;
        }
    }

    private static List<T> RewriteListIfChanged<T>(List<T> source, Func<T, T> rewrite)
    {
        List<T>? rewritten = null;
        for (int i = 0; i < source.Count; i++)
        {
            var original = source[i];
            var next = rewrite(original);
            if (!ReferenceEquals(original, next))
            {
                rewritten ??= new List<T>(source);
                rewritten[i] = next;
            }
        }
        return rewritten ?? source;
    }

    private Expr LiftGeneratorArrow(Expr.ArrowFunction af)
    {
        var name = $"__genArrow_{_counter++}";
        var nameToken = new Token(TokenType.IDENTIFIER, name, null, af.Parameters.FirstOrDefault()?.Name.Line ?? 1);

        // Recurse into the body first so nested generator arrows are also lifted.
        var rewrittenBody = RewriteListIfChanged(af.BlockBody!, RewriteStmt);

        var funcStmt = new Stmt.Function(
            Name: nameToken,
            TypeParams: af.TypeParams,
            ThisType: af.ThisType,
            Parameters: af.Parameters,
            Body: rewrittenBody,
            ReturnType: af.ReturnType,
            IsAsync: af.IsAsync,
            IsGenerator: true);

        _liftedFunctions.Add(funcStmt);
        return new Expr.Variable(nameToken);
    }
}
