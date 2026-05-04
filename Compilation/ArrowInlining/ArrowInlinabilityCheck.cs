using SharpTS.Compilation.Emitters;
using SharpTS.Parsing;

namespace SharpTS.Compilation.ArrowInlining;

/// <summary>
/// Pure-function eligibility checks for inlining a literal arrow callback at
/// an iterator-helper call site. No IL emission. Mirrors the gate in
/// <see cref="ArrayEmitter"/>'s <c>TryEmitDirectDelegateCall</c> with three
/// added body-shape constraints: expression body (not block), AST node count
/// under threshold, and confirmed presence in <c>ArrowMethods</c> (which
/// proves the arrow was emitted as a non-capturing static method).
/// </summary>
/// <remarks>
/// <para><b>Why "ArrowMethods contains arrow" implies non-capturing.</b>
/// In <c>ILCompiler.ArrowFunctions.cs</c>, an arrow gets a static method on
/// <c>$Program</c> only when <c>captures.Count == 0</c> and it doesn't need
/// either a function or arrow display class. Capturing arrows go through
/// the display-class path and are not registered in <c>ArrowMethods</c>.
/// So a successful lookup is a hard non-capturing proof.</para>
/// <para><b>Why the AST node threshold.</b> Inlined bodies grow the call
/// site's IL. Hot files with many <c>arr.map(...)</c> calls bloat the
/// emitted method, pressuring tier-1 JIT budgets. <c>30</c> covers the
/// common shapes (<c>x => x*2</c>, <c>x => x.foo</c>, <c>x => x.length > 5
/// &amp;&amp; x.bar</c>) but not multi-line lambdas. Tunable if real
/// workloads suggest otherwise.</para>
/// </remarks>
public static class ArrowInlinabilityCheck
{
    /// <summary>
    /// Default body-size cap for inlining (counted in AST nodes via
    /// <see cref="CountAstNodes"/>). Bodies above this threshold fall
    /// through to the Direct delegate path.
    /// </summary>
    public const int DefaultMaxBodyNodeCount = 30;

    /// <summary>
    /// Escape hatch: when <c>SHARPTS_DISABLE_ARROW_INLINING=1</c> is set
    /// in the environment, the eligibility gate always returns false and
    /// the call site falls through to the Direct/slow path. Lets
    /// benchmarks compare the inliner against the Direct delegate path,
    /// and provides a one-flag-flip rollback if a regression surfaces in
    /// the wild.
    /// </summary>
    private static readonly bool DisabledByEnv =
        Environment.GetEnvironmentVariable("SHARPTS_DISABLE_ARROW_INLINING") == "1";

    /// <summary>
    /// Resolves the callback expression at an iterator-helper call site to a
    /// literal <see cref="Expr.ArrowFunction"/> AST node and validates it
    /// against the inlining gate. Accepts both the inline form
    /// (<c>arr.forEach(x =&gt; …)</c>) and the const-bound form
    /// (<c>const sq = x =&gt; …; arr.forEach(sq)</c>).
    /// </summary>
    /// <param name="emitter">Emitter context for compilation state lookup.</param>
    /// <param name="callbackArg">The single callback argument expression.</param>
    /// <param name="expectedParamCount">Required parameter count for the
    /// callback (1 for forEach/map/filter/find/etc., 2 for reduce). Arrows
    /// with FEWER parameters are accepted (JS callbacks can declare fewer
    /// params than the helper passes); arrows with more are rejected.</param>
    /// <param name="af">On success, the resolved arrow.</param>
    /// <returns>True iff the arrow is eligible for inline expansion.</returns>
    public static bool TryGetEligibleArrow(
        IEmitterContext emitter,
        Expr callbackArg,
        int expectedParamCount,
        out Expr.ArrowFunction af)
    {
        af = null!;
        if (DisabledByEnv) return false;
        var ctx = emitter.Context;

        // 1. Resolve to a literal arrow (inline or const-bound).
        Expr.ArrowFunction? resolved = callbackArg switch
        {
            Expr.ArrowFunction direct => direct,
            Expr.Variable v when ctx.ConstArrowBindings.TryGetValue(v.Name.Lexeme, out var bound) => bound,
            _ => null,
        };
        if (resolved is null) return false;

        // 2. Function-shape gates — same as TryEmitDirectDelegateCall.
        if (resolved.HasOwnThis || resolved.IsAsync || resolved.IsGenerator) return false;
        if (resolved.Parameters.Count > expectedParamCount) return false;
        foreach (var p in resolved.Parameters)
        {
            if (p.Type != null) return false;
            if (p.IsRest || p.IsOptional) return false;
            if (p.DefaultValue != null) return false;
        }

        // 3. Body shape: expression-bodied arrows always qualify (M1+);
        //    block-bodied arrows qualify when the body is the V1
        //    statement-shape allowlist (M5). Mixed: at least one shape
        //    must be present.
        if (resolved.ExpressionBody is null)
        {
            if (resolved.BlockBody is null) return false;
            if (!BlockBodyArrowEmitter.IsAllowedShape(resolved.BlockBody)) return false;
        }

        // 4. Capture-free proof: arrow must have been emitted as a static
        //    method on $Program. Capturing arrows go through display
        //    classes and aren't registered in ArrowMethods.
        if (!ctx.ArrowMethods.ContainsKey(resolved)) return false;

        // 5. Body size cap. Expression body is counted directly; block
        //    bodies count each statement's contained expressions.
        int nodeCount = resolved.ExpressionBody is { } eb
            ? CountAstNodes(eb)
            : CountStatementsAstNodes(resolved.BlockBody!);
        if (nodeCount > DefaultMaxBodyNodeCount) return false;

        // 6. Outer-binding shadow: LocalVariableResolver consults several
        //    binding sources BEFORE the Locals stack, so RegisterLocal-ing
        //    the arrow's param into the outer scope can't shadow same-named
        //    bindings in those sources. The body would then silently
        //    resolve to the outer binding — a miscompile. The static-method
        //    path doesn't have this problem (each arrow has its own frame).
        //    Decline; Direct path takes over.
        //
        //    Sources checked before Locals (per LocalVariableResolver):
        //      - Outer method parameters
        //      - CapturedFunctionLocals (function display class)
        //      - CapturedArrowLocals (own arrow display class)
        //      - ParentArrowCapturedLocals (parent arrow display class)
        foreach (var p in resolved.Parameters)
        {
            var name = p.Name.Lexeme;
            if (ctx.TryGetParameter(name, out _)) return false;
            if (ctx.CapturedFunctionLocals?.Contains(name) == true) return false;
            if (ctx.CapturedArrowLocals?.Contains(name) == true) return false;
            if (ctx.ParentArrowCapturedLocals?.Contains(name) == true) return false;
        }

        af = resolved;
        return true;
    }

    /// <summary>
    /// Counts AST nodes across a list of statements (block-body arrows
    /// from M5+). Each allowed statement contributes the count of its
    /// contained expressions plus a small constant for the statement
    /// itself. Unallowed statement types short-circuit; the eligibility
    /// gate would already have rejected them.
    /// </summary>
    public static int CountStatementsAstNodes(List<Stmt> statements)
    {
        int n = 0;
        foreach (var s in statements)
        {
            n++;
            switch (s)
            {
                case Stmt.Expression es: n += CountAstNodes(es.Expr); break;
                case Stmt.Var v: if (v.Initializer != null) n += CountAstNodes(v.Initializer); break;
                case Stmt.Const c: n += CountAstNodes(c.Initializer); break;
                case Stmt.Return r: if (r.Value != null) n += CountAstNodes(r.Value); break;
                case Stmt.If i:
                    n += CountAstNodes(i.Condition);
                    n += CountStatementsAstNodes([i.ThenBranch]);
                    if (i.ElseBranch != null) n += CountStatementsAstNodes([i.ElseBranch]);
                    break;
                case Stmt.Block b:
                    if (b.Statements != null) n += CountStatementsAstNodes(b.Statements);
                    break;
            }
        }
        return n;
    }

    /// <summary>
    /// Counts AST nodes in an expression subtree. Used as a coarse IL-size
    /// proxy: every node roughly maps to a small number of IL ops, so a
    /// bound on AST size bounds emitted IL size. Unrecognized node types
    /// contribute 1 — the count undercounts on edge-case nodes, which is
    /// conservative (we'd inline a marginally larger body, not a smaller
    /// one falsely above threshold).
    /// </summary>
    public static int CountAstNodes(Expr expr)
    {
        int count = 1;
        switch (expr)
        {
            case Expr.Comma c:
                count += CountAstNodes(c.Left) + CountAstNodes(c.Right);
                break;
            case Expr.Binary b:
                count += CountAstNodes(b.Left) + CountAstNodes(b.Right);
                break;
            case Expr.Logical l:
                count += CountAstNodes(l.Left) + CountAstNodes(l.Right);
                break;
            case Expr.NullishCoalescing nc:
                count += CountAstNodes(nc.Left) + CountAstNodes(nc.Right);
                break;
            case Expr.Ternary t:
                count += CountAstNodes(t.Condition) + CountAstNodes(t.ThenBranch) + CountAstNodes(t.ElseBranch);
                break;
            case Expr.Unary u:
                count += CountAstNodes(u.Right);
                break;
            case Expr.Delete d:
                count += CountAstNodes(d.Operand);
                break;
            case Expr.Grouping g:
                count += CountAstNodes(g.Expression);
                break;
            case Expr.Assign a:
                count += CountAstNodes(a.Value);
                break;
            case Expr.Call c:
                count += CountAstNodes(c.Callee);
                foreach (var a in c.Arguments) count += CountAstNodes(a);
                break;
            case Expr.Get g:
                count += CountAstNodes(g.Object);
                break;
            case Expr.Set s:
                count += CountAstNodes(s.Object) + CountAstNodes(s.Value);
                break;
            case Expr.GetPrivate gp:
                count += CountAstNodes(gp.Object);
                break;
            case Expr.SetPrivate sp:
                count += CountAstNodes(sp.Object) + CountAstNodes(sp.Value);
                break;
            case Expr.CallPrivate cp:
                count += CountAstNodes(cp.Object);
                foreach (var a in cp.Arguments) count += CountAstNodes(a);
                break;
            case Expr.New n:
                count += CountAstNodes(n.Callee);
                foreach (var a in n.Arguments) count += CountAstNodes(a);
                break;
            case Expr.ArrayLiteral al:
                foreach (var e in al.Elements) count += CountAstNodes(e);
                break;
            case Expr.ObjectLiteral ol:
                foreach (var p in ol.Properties)
                {
                    if (p.Value != null) count += CountAstNodes(p.Value);
                    if (p.Key is Expr.ComputedKey ck) count += CountAstNodes(ck.Expression);
                }
                break;
            case Expr.GetIndex gi:
                count += CountAstNodes(gi.Object) + CountAstNodes(gi.Index);
                break;
            case Expr.SetIndex si:
                count += CountAstNodes(si.Object) + CountAstNodes(si.Index) + CountAstNodes(si.Value);
                break;
            case Expr.CompoundAssign ca:
                count += CountAstNodes(ca.Value);
                break;
            case Expr.CompoundSet cs:
                count += CountAstNodes(cs.Object) + CountAstNodes(cs.Value);
                break;
            case Expr.CompoundSetIndex csi:
                count += CountAstNodes(csi.Object) + CountAstNodes(csi.Index) + CountAstNodes(csi.Value);
                break;
            case Expr.LogicalAssign la:
                count += CountAstNodes(la.Value);
                break;
            case Expr.LogicalSet ls:
                count += CountAstNodes(ls.Object) + CountAstNodes(ls.Value);
                break;
            case Expr.LogicalSetIndex lsi:
                count += CountAstNodes(lsi.Object) + CountAstNodes(lsi.Index) + CountAstNodes(lsi.Value);
                break;
            case Expr.PrefixIncrement pi:
                count += CountAstNodes(pi.Operand);
                break;
            case Expr.PostfixIncrement pf:
                count += CountAstNodes(pf.Operand);
                break;
            case Expr.TemplateLiteral tl:
                foreach (var p in tl.Expressions) count += CountAstNodes(p);
                break;
            case Expr.TaggedTemplateLiteral tt:
                count += CountAstNodes(tt.Tag);
                foreach (var p in tt.Expressions) count += CountAstNodes(p);
                break;
            case Expr.Spread sp:
                count += CountAstNodes(sp.Expression);
                break;
            case Expr.TypeAssertion ta:
                count += CountAstNodes(ta.Expression);
                break;
            case Expr.Satisfies sat:
                count += CountAstNodes(sat.Expression);
                break;
            case Expr.NonNullAssertion nn:
                count += CountAstNodes(nn.Expression);
                break;
            case Expr.Yield y:
                if (y.Value != null) count += CountAstNodes(y.Value);
                break;
            case Expr.Await aw:
                count += CountAstNodes(aw.Expression);
                break;
            case Expr.DynamicImport di:
                count += CountAstNodes(di.PathExpression);
                break;
            case Expr.ArrowFunction:
                // Nested arrow inside the body — count as a single node;
                // we don't try to recurse into nested arrow bodies because
                // those bodies are emitted as their own static methods, not
                // inlined here. Conservative.
                break;
            // Leaf nodes (Literal, Variable, This, Super, RegexLiteral,
            // ImportMeta, ClassExpr, ...) contribute the base 1.
        }
        return count;
    }
}
