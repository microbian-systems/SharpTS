using SharpTS.Parsing;
using SharpTS.Parsing.Visitors;

namespace SharpTS.Compilation;

/// <summary>
/// Computes per-binding storage names for block-scoped (<c>let</c>/<c>const</c>) declarations inside a
/// generator body that <em>shadow</em> an enclosing binding of the same name.
/// </summary>
/// <remarks>
/// The generator state machine hoists every local that lives across a <c>yield</c> into a field keyed
/// by its source name (see <see cref="HoistingManager"/>), and resolves non-hoisted locals through a
/// flat, name-keyed local map. Both flatten lexical scope, so two block-scoped bindings that share a
/// name collide on a single slot: an inner <c>{ const r = 0 }</c> clobbers an outer <c>const r</c>
/// (#711). This pass walks the body with a scope stack and, for each inner <c>let</c>/<c>const</c> that
/// shadows an enclosing binding, assigns a fresh source-illegal storage name. It records — by AST-node
/// identity — the new name for the declaration and for every reference that lexically binds to it.
/// <see cref="GeneratorStateAnalyzer"/> and <see cref="GeneratorMoveNextEmitter"/> consult the map so the
/// hoist-vs-local decision and the field/local access become per-binding instead of per-name.
///
/// Restrictions that keep the rewrite sound:
/// <list type="bullet">
/// <item>Only <c>let</c>/<c>const</c> (<see cref="Stmt.Const"/>, <see cref="Stmt.Var"/> with
/// <c>IsVar == false</c>) declarations are renamed. <c>for</c>/<c>for-of</c>/<c>for-in</c>/<c>catch</c>
/// introduce scopes (so shadowing is detected accurately) but their loop-variable / catch-parameter
/// bindings are left alone.</item>
/// <item>A binding whose name is referenced by any nested closure (arrow/function/class) is left
/// untouched: those names flow through the closure-capture machinery (keyed by name), which this pass
/// does not rewrite, so renaming them would desync the capture (and the captured-name path shares the
/// same field, so it is never a renamed binding).</item>
/// </list>
/// No AST node is mutated and none is created, so TypeMap / ClosureAnalyzer node identity is preserved.
/// The walk is deterministic, so independent <see cref="GeneratorStateAnalyzer.Analyze"/> calls over the
/// same function (field definition vs. body emission) produce identical maps.
/// </remarks>
internal sealed class GeneratorBlockScopeRenamer : AstVisitorBase
{
    // Keyed by AST-node reference (records use value equality, so reference identity is mandatory here).
    private readonly Dictionary<object, string> _renames = new(ReferenceEqualityComparer.Instance);
    // Scope stack of name -> storage-name (storage == name when the binding is not renamed).
    private readonly List<Dictionary<string, string>> _scopes = [];
    private readonly HashSet<string> _closureReferenced = [];
    private int _counter;

    /// <summary>
    /// Returns the node→storage-name map for <paramref name="func"/>, empty when nothing shadows.
    /// </summary>
    public static IReadOnlyDictionary<object, string> Compute(Stmt.Function func)
    {
        var renamer = new GeneratorBlockScopeRenamer();
        new ClosureReferenceCollector(renamer._closureReferenced).Run(func);

        renamer.PushScope();
        // The generator's own name is in scope inside the body (a named function expression can call
        // itself by it); a nested-block let/const may shadow it, so seed it for shadow detection.
        // Never renamed (it is not a hoisted local — it resolves to the function/method itself).
        if (!string.IsNullOrEmpty(func.Name.Lexeme))
            renamer.CurrentScope[func.Name.Lexeme] = func.Name.Lexeme;
        // Parameters live in the function scope; an inner block let/const may shadow them. Never renamed.
        foreach (var p in func.Parameters)
            renamer.CurrentScope[p.Name.Lexeme] = p.Name.Lexeme;
        if (func.Body != null)
            foreach (var stmt in func.Body)
                renamer.Visit(stmt);
        renamer.PopScope();

        return renamer._renames;
    }

    private Dictionary<string, string> CurrentScope => _scopes[^1];
    private void PushScope() => _scopes.Add([]);
    private void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

    private bool ShadowsEnclosing(string name)
    {
        for (int i = _scopes.Count - 2; i >= 0; i--)
            if (_scopes[i].ContainsKey(name)) return true;
        return false;
    }

    private string? Resolve(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var storage)) return storage;
        return null;
    }

    private void DeclareBlockScoped(object node, string name)
    {
        // Same-scope redeclaration is a TypeScript error; keep the first binding.
        if (CurrentScope.ContainsKey(name)) return;

        if (ShadowsEnclosing(name) && !_closureReferenced.Contains(name))
        {
            var storage = $"<{name}>__bs{_counter++}";
            CurrentScope[name] = storage;
            _renames[node] = storage;
        }
        else
        {
            CurrentScope[name] = name;
        }
    }

    private void RecordRef(object node, string name)
    {
        var storage = Resolve(name);
        if (storage != null && storage != name)
            _renames[node] = storage;
    }

    #region Declarations

    protected override void VisitConst(Stmt.Const stmt)
    {
        base.VisitConst(stmt);   // initializer is evaluated before the binding enters scope
        DeclareBlockScoped(stmt, stmt.Name.Lexeme);
    }

    protected override void VisitVar(Stmt.Var stmt)
    {
        base.VisitVar(stmt);
        if (stmt.IsVar)
        {
            // `var` is function-scoped: record it in the bottom scope so a later block-scoped
            // let/const that shadows it is detected, but never rename it (one binding per name).
            _scopes[0].TryAdd(stmt.Name.Lexeme, stmt.Name.Lexeme);
        }
        else
        {
            DeclareBlockScoped(stmt, stmt.Name.Lexeme);
        }
    }

    #endregion

    #region References

    protected override void VisitVariable(Expr.Variable expr) => RecordRef(expr, expr.Name.Lexeme);

    protected override void VisitAssign(Expr.Assign expr)
    {
        RecordRef(expr, expr.Name.Lexeme);
        base.VisitAssign(expr);
    }

    protected override void VisitCompoundAssign(Expr.CompoundAssign expr)
    {
        RecordRef(expr, expr.Name.Lexeme);
        base.VisitCompoundAssign(expr);
    }

    protected override void VisitLogicalAssign(Expr.LogicalAssign expr)
    {
        RecordRef(expr, expr.Name.Lexeme);
        base.VisitLogicalAssign(expr);
    }

    // Increment/decrement record the operand Variable (via the default traversal → VisitVariable),
    // matching how the emitter resolves it (it reads/writes through the operand variable node).

    #endregion

    #region Scope-introducing statements

    protected override void VisitBlock(Stmt.Block stmt)
    {
        PushScope();
        base.VisitBlock(stmt);
        PopScope();
    }

    protected override void VisitFor(Stmt.For stmt)
    {
        // The for-statement owns a scope for any let/const declared in its initializer.
        PushScope();
        base.VisitFor(stmt);
        PopScope();
    }

    protected override void VisitForOf(Stmt.ForOf stmt)
    {
        Visit(stmt.Iterable);   // iterable is evaluated in the enclosing scope
        PushScope();
        CurrentScope[stmt.Variable.Lexeme] = stmt.Variable.Lexeme;   // loop var: detected, not renamed
        Visit(stmt.Body);
        PopScope();
    }

    protected override void VisitForIn(Stmt.ForIn stmt)
    {
        Visit(stmt.Object);
        PushScope();
        CurrentScope[stmt.Variable.Lexeme] = stmt.Variable.Lexeme;
        Visit(stmt.Body);
        PopScope();
    }

    protected override void VisitSwitch(Stmt.Switch stmt)
    {
        Visit(stmt.Subject);
        PushScope();   // all cases share one block scope (ECMA-262 14.12)
        foreach (var c in stmt.Cases)
        {
            Visit(c.Value);
            foreach (var s in c.Body) Visit(s);
        }
        if (stmt.DefaultBody != null)
            foreach (var s in stmt.DefaultBody) Visit(s);
        PopScope();
    }

    protected override void VisitTryCatch(Stmt.TryCatch stmt)
    {
        PushScope();
        foreach (var s in stmt.TryBlock) Visit(s);
        PopScope();

        if (stmt.CatchBlock != null)
        {
            PushScope();
            if (stmt.CatchParam != null)
                CurrentScope[stmt.CatchParam.Lexeme] = stmt.CatchParam.Lexeme;   // catch param: detected, not renamed
            foreach (var s in stmt.CatchBlock) Visit(s);
            PopScope();
        }

        if (stmt.FinallyBlock != null)
        {
            PushScope();
            foreach (var s in stmt.FinallyBlock) Visit(s);
            PopScope();
        }
    }

    #endregion

    #region Opaque nodes (own scopes, not hoisted into this generator)

    protected override void VisitArrowFunction(Expr.ArrowFunction expr) { }
    protected override void VisitFunction(Stmt.Function stmt) { }
    protected override void VisitClass(Stmt.Class stmt) { }
    protected override void VisitClassExpr(Expr.ClassExpr expr) { }

    #endregion

    /// <summary>
    /// Collects every variable name referenced anywhere inside a nested closure (arrow / function /
    /// class) of the generator body. Names in this set are off-limits to renaming because the closure
    /// capture machinery keys them by source name and this pass does not rewrite closure interiors.
    /// </summary>
    private sealed class ClosureReferenceCollector : AstVisitorBase
    {
        private readonly HashSet<string> _names;
        private int _depth;

        public ClosureReferenceCollector(HashSet<string> names) => _names = names;

        public void Run(Stmt.Function func)
        {
            if (func.Body != null)
                foreach (var s in func.Body) Visit(s);
        }

        protected override void VisitArrowFunction(Expr.ArrowFunction expr) { _depth++; base.VisitArrowFunction(expr); _depth--; }
        protected override void VisitFunction(Stmt.Function stmt) { _depth++; base.VisitFunction(stmt); _depth--; }
        protected override void VisitClass(Stmt.Class stmt) { _depth++; base.VisitClass(stmt); _depth--; }
        protected override void VisitClassExpr(Expr.ClassExpr expr) { _depth++; base.VisitClassExpr(expr); _depth--; }

        protected override void VisitVariable(Expr.Variable expr) { if (_depth > 0) _names.Add(expr.Name.Lexeme); }
        protected override void VisitAssign(Expr.Assign expr) { if (_depth > 0) _names.Add(expr.Name.Lexeme); base.VisitAssign(expr); }
        protected override void VisitCompoundAssign(Expr.CompoundAssign expr) { if (_depth > 0) _names.Add(expr.Name.Lexeme); base.VisitCompoundAssign(expr); }
        protected override void VisitLogicalAssign(Expr.LogicalAssign expr) { if (_depth > 0) _names.Add(expr.Name.Lexeme); base.VisitLogicalAssign(expr); }
    }
}
