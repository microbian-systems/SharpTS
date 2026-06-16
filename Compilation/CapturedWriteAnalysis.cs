using SharpTS.Parsing;
using SharpTS.Parsing.Visitors;

namespace SharpTS.Compilation;

/// <summary>
/// Determines which variables an arrow/function body assigns to within its <em>own</em> scope —
/// descending through blocks, loops, conditionals and call arguments, but NOT into nested arrows
/// or function declarations (those introduce their own bindings, so a write there is not a write
/// to this scope's captures). Intersecting the result with a closure's capture set identifies the
/// captures it mutates, which the compiler must back with shared (reference-type) storage rather
/// than a by-value snapshot.
/// </summary>
internal static class CapturedWriteAnalysis
{
    /// <summary>
    /// Returns the names assigned within <paramref name="arrow"/>'s own scope.
    /// </summary>
    public static HashSet<string> CollectImmediateWrites(Expr.ArrowFunction arrow)
    {
        var collector = new WrittenNameCollector();
        if (arrow.ExpressionBody != null)
            collector.Visit(arrow.ExpressionBody);
        if (arrow.BlockBody != null)
            foreach (var stmt in arrow.BlockBody)
                collector.Visit(stmt);
        return collector.Names;
    }

    private sealed class WrittenNameCollector : AstVisitorBase
    {
        public readonly HashSet<string> Names = [];

        protected override void VisitAssign(Expr.Assign expr)
        {
            Names.Add(expr.Name.Lexeme);
            base.VisitAssign(expr);
        }

        protected override void VisitCompoundAssign(Expr.CompoundAssign expr)
        {
            Names.Add(expr.Name.Lexeme);
            base.VisitCompoundAssign(expr);
        }

        protected override void VisitLogicalAssign(Expr.LogicalAssign expr)
        {
            Names.Add(expr.Name.Lexeme);
            base.VisitLogicalAssign(expr);
        }

        protected override void VisitPrefixIncrement(Expr.PrefixIncrement expr)
        {
            if (expr.Operand is Expr.Variable v) Names.Add(v.Name.Lexeme);
            base.VisitPrefixIncrement(expr);
        }

        protected override void VisitPostfixIncrement(Expr.PostfixIncrement expr)
        {
            if (expr.Operand is Expr.Variable v) Names.Add(v.Name.Lexeme);
            base.VisitPostfixIncrement(expr);
        }

        // Stop at nested closure boundaries: their assignments target their own bindings (or
        // deeper captures), so they must not be attributed to the enclosing scope's captures.
        protected override void VisitArrowFunction(Expr.ArrowFunction expr) { }
        protected override void VisitFunction(Stmt.Function stmt) { }
    }
}
