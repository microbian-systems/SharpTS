namespace SharpTS.Parsing;

/// <summary>
/// Implements JavaScript <c>var</c> hoisting as a parser-time AST transform.
/// </summary>
/// <remarks>
/// JavaScript <c>var</c> declarations are function-scoped and hoisted to the top of the
/// enclosing function (or top-level for module/script bodies). This means:
/// <code>
/// function f() {
///   if (cond) { var x = 1; }
///   return x; // visible — Node returns 1 or undefined, never ReferenceError
/// }
/// </code>
///
/// Rather than implementing function-scope semantics in the interpreter and IL compiler
/// directly, we transform the AST at parse time:
/// <list type="number">
/// <item>Walk all statements in a function/module body, recursing into nested blocks but NOT
/// into nested function declarations or function expressions (each has its own var scope).</item>
/// <item>Collect all <c>Stmt.Var</c> nodes whose <c>IsVar</c> flag is true.</item>
/// <item>Rewrite each in-place: <c>var x = expr</c> becomes a plain assignment expression
/// statement <c>x = expr</c>; <c>var x;</c> (no initializer) becomes a no-op.</item>
/// <item>Prepend synthetic <c>var x;</c> declarations at the top of the body (one per unique
/// hoisted name). These declare the binding in the function scope.</item>
/// </list>
///
/// After this pass the interpreter and IL compiler can treat the result as already-correctly-
/// scoped <c>let</c>-style declarations — no new runtime semantics needed.
///
/// <para>Limitations:</para>
/// <list type="bullet">
/// <item>Does not handle var inside arrow function bodies that are method/property values
/// (those go through their own collection paths).</item>
/// <item>Does not implement <c>var</c>'s redeclaration permissiveness (declaring the same
/// name twice is not an error in JS); the existing duplicate-binding behavior applies.</item>
/// <item>Does not currently descend into class bodies, getters, setters, or constructors;
/// those rely on being separate function scopes already.</item>
/// </list>
/// </remarks>
public static class VarHoister
{
    /// <summary>
    /// Hoists <c>var</c> declarations within the given body. Returns a new statement list
    /// with synthetic declarations at the top and original declarations rewritten as
    /// assignments. If no <c>var</c> declarations exist, returns the input list unchanged
    /// (a fast-path that avoids allocations for the common case).
    /// </summary>
    public static List<Stmt> Hoist(List<Stmt> body)
    {
        var collected = new List<Token>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rewritten = new List<Stmt>(body.Count);
        bool changed = false;

        foreach (var stmt in body)
        {
            var newStmt = RewriteAndCollect(stmt, collected, seen, isTopLevel: true, ref changed);
            rewritten.Add(newStmt);
        }

        if (collected.Count == 0)
        {
            return changed ? rewritten : body;
        }

        // Prepend synthetic `var name;` declarations for each unique hoisted name.
        var result = new List<Stmt>(collected.Count + rewritten.Count);
        foreach (var nameToken in collected)
        {
            result.Add(new Stmt.Var(nameToken, TypeAnnotation: null, Initializer: null, HasDefiniteAssignmentAssertion: false, IsVar: true));
        }
        result.AddRange(rewritten);
        return result;
    }

    /// <summary>
    /// Recursively walks a statement, collecting hoisted var names and rewriting var
    /// declarations as assignments. Returns the (possibly rewritten) statement.
    /// </summary>
    /// <param name="isTopLevel">True if this statement is directly inside the function/module
    /// body. Top-level vars are still rewritten to assignments (so the synthetic declarations
    /// at the top can hold the binding) but they appear in the same source-order position.</param>
    private static Stmt RewriteAndCollect(Stmt stmt, List<Token> collected, HashSet<string> seen, bool isTopLevel, ref bool changed)
    {
        switch (stmt)
        {
            case Stmt.Var v when v.IsVar:
            {
                // Top-level vars (directly in the function/module body) are already hoisted by
                // virtue of being at the top. Leave them alone — only rewrite nested vars.
                if (isTopLevel)
                {
                    return v;
                }

                if (seen.Add(v.Name.Lexeme))
                {
                    collected.Add(v.Name);
                }
                changed = true;
                if (v.Initializer == null)
                {
                    // `var x;` → no-op (the synthetic declaration handles binding creation)
                    return new Stmt.Expression(new Expr.Literal(null));
                }
                // `var x = expr` → `x = expr;`
                return new Stmt.Expression(new Expr.Assign(v.Name, v.Initializer));
            }

            case Stmt.Sequence seq:
            {
                var newStmts = new List<Stmt>(seq.Statements.Count);
                bool seqChanged = false;
                foreach (var inner in seq.Statements)
                {
                    var rewritten = RewriteAndCollect(inner, collected, seen, isTopLevel, ref seqChanged);
                    newStmts.Add(rewritten);
                }
                if (seqChanged)
                {
                    changed = true;
                    return new Stmt.Sequence(newStmts);
                }
                return seq;
            }

            case Stmt.Block block:
            {
                var newStmts = new List<Stmt>(block.Statements.Count);
                bool blockChanged = false;
                foreach (var inner in block.Statements)
                {
                    var rewritten = RewriteAndCollect(inner, collected, seen, isTopLevel: false, ref blockChanged);
                    newStmts.Add(rewritten);
                }
                if (blockChanged)
                {
                    changed = true;
                    return new Stmt.Block(newStmts);
                }
                return block;
            }

            case Stmt.If ifStmt:
            {
                bool ifChanged = false;
                var newThen = RewriteAndCollect(ifStmt.ThenBranch, collected, seen, isTopLevel: false, ref ifChanged);
                Stmt? newElse = ifStmt.ElseBranch != null
                    ? RewriteAndCollect(ifStmt.ElseBranch, collected, seen, isTopLevel: false, ref ifChanged)
                    : null;
                if (ifChanged)
                {
                    changed = true;
                    return new Stmt.If(ifStmt.Condition, newThen, newElse);
                }
                return ifStmt;
            }

            case Stmt.While whileStmt:
            {
                bool whileChanged = false;
                var newBody = RewriteAndCollect(whileStmt.Body, collected, seen, isTopLevel: false, ref whileChanged);
                if (whileChanged)
                {
                    changed = true;
                    return new Stmt.While(whileStmt.Condition, newBody);
                }
                return whileStmt;
            }

            case Stmt.DoWhile doWhile:
            {
                bool dwChanged = false;
                var newBody = RewriteAndCollect(doWhile.Body, collected, seen, isTopLevel: false, ref dwChanged);
                if (dwChanged)
                {
                    changed = true;
                    return new Stmt.DoWhile(newBody, doWhile.Condition);
                }
                return doWhile;
            }

            case Stmt.For forStmt:
            {
                bool forChanged = false;
                Stmt? newInit = forStmt.Initializer != null
                    ? RewriteAndCollect(forStmt.Initializer, collected, seen, isTopLevel: false, ref forChanged)
                    : null;
                var newBody = RewriteAndCollect(forStmt.Body, collected, seen, isTopLevel: false, ref forChanged);
                if (forChanged)
                {
                    changed = true;
                    return new Stmt.For(newInit, forStmt.Condition, forStmt.Increment, newBody);
                }
                return forStmt;
            }

            case Stmt.ForOf forOf:
            {
                bool fofChanged = false;
                var newBody = RewriteAndCollect(forOf.Body, collected, seen, isTopLevel: false, ref fofChanged);
                if (fofChanged)
                {
                    changed = true;
                    return forOf with { Body = newBody };
                }
                return forOf;
            }

            case Stmt.ForIn forIn:
            {
                bool finChanged = false;
                var newBody = RewriteAndCollect(forIn.Body, collected, seen, isTopLevel: false, ref finChanged);
                if (finChanged)
                {
                    changed = true;
                    return forIn with { Body = newBody };
                }
                return forIn;
            }

            case Stmt.LabeledStatement labeled:
            {
                bool lblChanged = false;
                var newInner = RewriteAndCollect(labeled.Statement, collected, seen, isTopLevel: false, ref lblChanged);
                if (lblChanged)
                {
                    changed = true;
                    return new Stmt.LabeledStatement(labeled.Label, newInner);
                }
                return labeled;
            }

            case Stmt.TryCatch tryCatch:
            {
                bool tcChanged = false;
                var newTry = RewriteList(tryCatch.TryBlock, collected, seen, ref tcChanged);
                List<Stmt>? newCatch = tryCatch.CatchBlock != null
                    ? RewriteList(tryCatch.CatchBlock, collected, seen, ref tcChanged)
                    : null;
                List<Stmt>? newFinally = tryCatch.FinallyBlock != null
                    ? RewriteList(tryCatch.FinallyBlock, collected, seen, ref tcChanged)
                    : null;
                if (tcChanged)
                {
                    changed = true;
                    return tryCatch with { TryBlock = newTry, CatchBlock = newCatch, FinallyBlock = newFinally };
                }
                return tryCatch;
            }

            case Stmt.Switch switchStmt:
            {
                bool swChanged = false;
                var newCases = new List<Stmt.SwitchCase>(switchStmt.Cases.Count);
                foreach (var c in switchStmt.Cases)
                {
                    var newCaseStmts = RewriteList(c.Body, collected, seen, ref swChanged);
                    newCases.Add(new Stmt.SwitchCase(c.Value, newCaseStmts));
                }
                List<Stmt>? newDefault = switchStmt.DefaultBody != null
                    ? RewriteList(switchStmt.DefaultBody, collected, seen, ref swChanged)
                    : null;
                if (swChanged)
                {
                    changed = true;
                    return switchStmt with { Cases = newCases, DefaultBody = newDefault };
                }
                return switchStmt;
            }

            // Don't recurse into nested function declarations — they have their own var scope.
            // Top-level Stmt.Function will be hoisted on its own (when its body is parsed).
            case Stmt.Function:
                return stmt;

            default:
                return stmt;
        }
    }

    /// <summary>
    /// Helper for rewriting a list of statements (used by TryCatch/Switch which carry
    /// <c>List&lt;Stmt&gt;</c> directly rather than wrapping in a Block).
    /// </summary>
    private static List<Stmt> RewriteList(List<Stmt> list, List<Token> collected, HashSet<string> seen, ref bool changed)
    {
        var result = new List<Stmt>(list.Count);
        foreach (var s in list)
        {
            result.Add(RewriteAndCollect(s, collected, seen, isTopLevel: false, ref changed));
        }
        return result;
    }
}
