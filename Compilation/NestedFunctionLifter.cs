using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// Relocates <b>non-capturing</b> nested function-like declarations to the module top level so
/// the mature top-level state-machine pipeline (<c>GeneratorStateMachineBuilder</c> /
/// <c>AsyncStateMachineBuilder</c> / <c>AsyncGeneratorStateMachineBuilder</c>) can lower them.
///
/// <para>Two shapes the in-place emitters cannot handle, and which this pass fixes by lifting:</para>
/// <list type="number">
/// <item><b>Case A</b> — a nested generator/async <c>function</c> declaration anywhere (the inner
/// function machinery would emit it as a plain method, so its <c>yield</c>/<c>await</c> fails:
/// "Yield not supported in this context"). Fixes the non-capturing half of #501.</item>
/// <item><b>Case B</b> — a plain <c>function</c> declaration whose nearest enclosing function-like
/// is itself a state machine (generator/async): the state-machine MoveNext emitter has no arm for
/// <c>Stmt.Function</c> ("Unhandled statement type in ILEmitter: Function"). Fixes #470.</item>
/// </list>
///
/// <para><b>Compile-path only.</b> Unlike <see cref="SharpTS.Parsing.GeneratorArrowLifter"/> (which
/// runs in the parser for both interpreter and compiler), this pass runs inside the IL compiler.
/// The interpreter handles nested declarations correctly via real closures, so relocating them
/// there would be a regression. Running here keeps the interpreter untouched.</para>
///
/// <para><b>Alias relocation.</b> Each lifted declaration is given a fresh, collision-proof name
/// (<c>__nestedFn_&lt;name&gt;_N</c>) and appended to the module body. A <c>var &lt;name&gt; =
/// __nestedFn_&lt;name&gt;_N;</c> alias is then inserted at the top of the original enclosing
/// statement list (so the body's references resolve to the relocated function through an ordinary
/// local binding), and into the lifted body itself when the function recurses. This avoids
/// scope-aware identifier renaming entirely — JavaScript's own scoping resolves the references via
/// the injected binding, and a nested redeclaration naturally shadows it.</para>
///
/// <para>The fresh top-level name is essential: compiled name resolution currently lets a top-level
/// <c>function</c> shadow a same-named local/parameter, so keeping the original name at module scope
/// would hijack unrelated same-named bindings elsewhere. A unique name no user code references can't.
/// The alias is a value reference to a top-level function inside the (possibly generator) enclosing
/// body — see the generator captured-field exclusion in <c>ILCompiler.Generators.cs</c> that makes
/// such references resolve correctly.</para>
///
/// <para><b>Safety guards</b> — the pass refuses to lift (the declaration stays nested and fails to
/// compile exactly as before — a clean failure, never a miscompile) when:</para>
/// <list type="bullet">
/// <item><b>Capturing</b> (#583 §1): a free variable resolves to an enclosing function scope
/// (<see cref="ClosureAnalyzer.GetCaptureSource"/> non-null). Moving it to the top level would
/// break the closure. The function's own name (recursion) is not treated as a capture.</item>
/// <item><b>Inside a namespace</b> (#583 §3): a namespace member is reachable by bare name, so
/// relocating a body out of the namespace would change how the names it references resolve. The
/// pass never descends into a <see cref="Stmt.Namespace"/>.</item>
/// <item><b>Name collides with a top-level binding</b>: the injected <c>var &lt;name&gt;</c> alias
/// would be hijacked by a same-named top-level <c>function</c> (the resolution quirk above), so a
/// candidate whose name matches an existing top-level binding is left nested.</item>
/// </list>
///
/// <para><b>Known limitation (#583 §2):</b> a relocated declaration becomes a single top-level
/// function, so its identity is shared across separate invocations of the enclosing function rather
/// than fresh per call. Same class of limitation as <see cref="GeneratorArrowLifter"/> / #534.</para>
/// </summary>
internal sealed class NestedFunctionLifter
{
    private readonly ClosureAnalyzer _analyzer;
    private readonly HashSet<Stmt.Function> _safeCandidates;
    private readonly List<Stmt.Function> _lifted = new();
    private int _counter;

    private NestedFunctionLifter(ClosureAnalyzer analyzer, HashSet<Stmt.Function> safeCandidates)
    {
        _analyzer = analyzer;
        _safeCandidates = safeCandidates;
    }

    /// <summary>
    /// Returns a statement list with all liftable non-capturing nested function-likes relocated to
    /// the top level. Returns the input list unchanged (by reference) when nothing needs lifting,
    /// so untouched programs pay only a cheap structural pre-scan.
    /// </summary>
    public static List<Stmt> Lift(List<Stmt> module)
    {
        // Cheap structural pre-scan: collect declarations whose SHAPE qualifies (case A/B), without
        // running closure analysis. The overwhelmingly common module has none, so we return early.
        var shapeCandidates = new List<Stmt.Function>();
        CollectShapeCandidates(module, enclosingIsStateMachine: false, insideFunction: false, shapeCandidates);
        if (shapeCandidates.Count == 0) return module;

        // Capture analysis is needed to tell a safe (module/global) reference from an unsafe
        // enclosing-scope capture. Run our own pass on the original AST; the main pipeline
        // re-analyses the transformed AST in Phase 2.
        var analyzer = new ClosureAnalyzer();
        analyzer.Analyze(module);

        // A candidate is liftable when it captures nothing from an enclosing scope and its name does
        // not collide with a top-level binding (which would hijack the injected alias).
        var reservedTopLevelNames = CollectTopLevelBindingNames(module);
        var safe = new HashSet<Stmt.Function>(ReferenceEqualityComparer.Instance);
        foreach (var f in shapeCandidates)
        {
            if (!IsNonCapturing(analyzer, f)) continue;
            if (reservedTopLevelNames.Contains(f.Name.Lexeme)) continue;
            safe.Add(f);
        }
        if (safe.Count == 0) return module;

        var lifter = new NestedFunctionLifter(analyzer, safe);
        var rewritten = lifter.ProcessTopLevel(module);
        if (lifter._lifted.Count == 0) return module;

        // Append lifted declarations (they hoist, so trailing position is runtime-equivalent and
        // lets the source-order type checker see any module-level bindings the body reads first).
        var result = new List<Stmt>(rewritten.Count + lifter._lifted.Count);
        result.AddRange(rewritten);
        result.AddRange(lifter._lifted);
        return result;
    }

    /// <summary>
    /// True when the SHAPE of <paramref name="f"/> requires lifting: it is itself a generator/async
    /// (case A), or it is a plain function whose nearest enclosing function-like is a state machine
    /// (case B). Overload signatures (no body) never qualify.
    /// </summary>
    private static bool IsCandidateShape(Stmt.Function f, bool enclosingIsStateMachine)
        => f.Body != null && (f.IsGenerator || f.IsAsync || enclosingIsStateMachine);

    /// <summary>
    /// A declaration is liftable only if every free variable resolves to module/global scope. A
    /// reference into an intermediate function scope is a real closure capture (#583 §1) and blocks
    /// lifting. The function's own name is excluded — recursion resolves through the self-alias the
    /// lifter injects into the relocated body, not a captured outer binding.
    /// </summary>
    private static bool IsNonCapturing(ClosureAnalyzer analyzer, Stmt.Function f)
    {
        foreach (var captured in analyzer.GetCaptures(f))
        {
            if (captured == f.Name.Lexeme) continue;
            if (analyzer.GetCaptureSource(f, captured) != null) return false;
        }
        return true;
    }

    #region Structural candidate collection (read-only, no closure analysis)

    private static void CollectShapeCandidates(List<Stmt> body, bool enclosingIsStateMachine, bool insideFunction, List<Stmt.Function> output)
    {
        foreach (var stmt in body)
            CollectShapeCandidatesStmt(stmt, enclosingIsStateMachine, insideFunction, output);
    }

    private static void CollectShapeCandidatesStmt(Stmt stmt, bool enclosingIsStateMachine, bool insideFunction, List<Stmt.Function> output)
    {
        switch (stmt)
        {
            case Stmt.Function f when f.Body != null:
                // Only lift declarations nested INSIDE a function-like. A declaration that is only
                // inside a module-level block/loop is left alone: the closure analyzer attributes
                // block/loop-scoped captures to an enclosing function, but at module scope there is
                // none, so such a capture looks (falsely) module-global. Lifting it out of its block
                // would silently break the reference (e.g. a generator in a `for` capturing the loop
                // variable). Inside a function those captures are tracked correctly and blocked.
                if (insideFunction && IsCandidateShape(f, enclosingIsStateMachine))
                    output.Add(f);
                // A nested function's own body establishes a fresh enclosing kind for its children.
                CollectShapeCandidates(f.Body, f.IsGenerator || f.IsAsync, insideFunction: true, output);
                break;
            case Stmt.Block b:
                CollectShapeCandidates(b.Statements, enclosingIsStateMachine, insideFunction, output);
                break;
            case Stmt.Sequence s:
                CollectShapeCandidates(s.Statements, enclosingIsStateMachine, insideFunction, output);
                break;
            case Stmt.If i:
                CollectShapeCandidatesStmt(i.ThenBranch, enclosingIsStateMachine, insideFunction, output);
                if (i.ElseBranch != null) CollectShapeCandidatesStmt(i.ElseBranch, enclosingIsStateMachine, insideFunction, output);
                break;
            case Stmt.While w:
                CollectShapeCandidatesStmt(w.Body, enclosingIsStateMachine, insideFunction, output);
                break;
            case Stmt.DoWhile d:
                CollectShapeCandidatesStmt(d.Body, enclosingIsStateMachine, insideFunction, output);
                break;
            case Stmt.For fo:
                if (fo.Initializer != null) CollectShapeCandidatesStmt(fo.Initializer, enclosingIsStateMachine, insideFunction, output);
                CollectShapeCandidatesStmt(fo.Body, enclosingIsStateMachine, insideFunction, output);
                break;
            case Stmt.ForOf fof:
                CollectShapeCandidatesStmt(fof.Body, enclosingIsStateMachine, insideFunction, output);
                break;
            case Stmt.ForIn fin:
                CollectShapeCandidatesStmt(fin.Body, enclosingIsStateMachine, insideFunction, output);
                break;
            case Stmt.LabeledStatement l:
                CollectShapeCandidatesStmt(l.Statement, enclosingIsStateMachine, insideFunction, output);
                break;
            case Stmt.TryCatch t:
                CollectShapeCandidates(t.TryBlock, enclosingIsStateMachine, insideFunction, output);
                if (t.CatchBlock != null) CollectShapeCandidates(t.CatchBlock, enclosingIsStateMachine, insideFunction, output);
                if (t.FinallyBlock != null) CollectShapeCandidates(t.FinallyBlock, enclosingIsStateMachine, insideFunction, output);
                break;
            case Stmt.Switch sw:
                foreach (var c in sw.Cases) CollectShapeCandidates(c.Body, enclosingIsStateMachine, insideFunction, output);
                if (sw.DefaultBody != null) CollectShapeCandidates(sw.DefaultBody, enclosingIsStateMachine, insideFunction, output);
                break;
            case Stmt.Class cls:
                // Method bodies are function-likes regardless of where the class sits.
                foreach (var m in cls.Methods)
                    if (m.Body != null) CollectShapeCandidates(m.Body, m.IsGenerator || m.IsAsync, insideFunction: true, output);
                break;
            case Stmt.Export ex when ex.Declaration != null:
                CollectShapeCandidatesStmt(ex.Declaration, enclosingIsStateMachine, insideFunction, output);
                break;
            // Stmt.Namespace is intentionally NOT traversed (#583 §3 lift barrier).
        }
    }

    #endregion

    #region Transform (extracts safe candidates, injects aliases)

    /// <summary>
    /// Processes the module body. Top-level declarations are never lifted (already at module scope),
    /// but their bodies are walked so nested declarations can be extracted.
    /// </summary>
    private List<Stmt> ProcessTopLevel(List<Stmt> module)
    {
        var result = new List<Stmt>(module.Count);
        bool changed = false;
        foreach (var stmt in module)
        {
            var rewritten = ProcessStmt(stmt, enclosingIsStateMachine: false);
            if (!ReferenceEquals(rewritten, stmt)) changed = true;
            result.Add(rewritten);
        }
        return changed ? result : module;
    }

    /// <summary>
    /// Processes a statement list (a function body or block). Safe nested function-likes are moved to
    /// the module top level under a fresh name and replaced, at the top of this list, by a
    /// <c>var &lt;name&gt; = &lt;freshName&gt;;</c> alias so references in this scope still resolve.
    /// </summary>
    private List<Stmt> ProcessBody(List<Stmt> body, bool enclosingIsStateMachine)
    {
        List<Stmt>? result = null;
        List<Stmt>? aliases = null;
        for (int i = 0; i < body.Count; i++)
        {
            var stmt = body[i];

            if (stmt is Stmt.Function f && f.Body != null && _safeCandidates.Contains(f))
            {
                aliases ??= new List<Stmt>();
                aliases.Add(LiftCandidate(f));
                result ??= new List<Stmt>(body.GetRange(0, i));
                continue; // drop the declaration from this body
            }

            var rewritten = ProcessStmt(stmt, enclosingIsStateMachine);
            if (result != null)
                result.Add(rewritten);
            else if (!ReferenceEquals(rewritten, stmt))
                result = new List<Stmt>(body.GetRange(0, i)) { rewritten };
        }

        if (result == null) return body;
        if (aliases != null) result.InsertRange(0, aliases);
        return result;
    }

    /// <summary>
    /// Relocates <paramref name="f"/> to the module top level under a fresh name (recursing first so
    /// its own nested candidates are extracted) and returns the <c>var</c> alias that should stand in
    /// for it in the original scope.
    /// </summary>
    private Stmt LiftCandidate(Stmt.Function f)
    {
        var freshName = $"__nestedFn_{f.Name.Lexeme}_{_counter++}";
        var freshToken = new Token(TokenType.IDENTIFIER, freshName, null, f.Name.Line);

        var liftedBody = ProcessBody(f.Body!, f.IsGenerator || f.IsAsync);

        // Recursion: the relocated body still calls itself by the original name. Bind that name to
        // the fresh declaration with a self-alias at the top of the body.
        if (_analyzer.GetCaptures(f).Contains(f.Name.Lexeme))
        {
            var withSelfAlias = new List<Stmt>(liftedBody.Count + 1) { MakeAlias(f.Name, freshToken) };
            withSelfAlias.AddRange(liftedBody);
            liftedBody = withSelfAlias;
        }

        _lifted.Add(f with { Name = freshToken, Body = liftedBody });
        return MakeAlias(f.Name, freshToken);
    }

    /// <summary>Builds <c>var &lt;original&gt; = &lt;fresh&gt;;</c>.</summary>
    private static Stmt MakeAlias(Token original, Token fresh)
        => new Stmt.Var(original, TypeAnnotation: null, Initializer: new Expr.Variable(fresh), IsVar: true);

    private Stmt ProcessStmt(Stmt stmt, bool enclosingIsStateMachine)
    {
        switch (stmt)
        {
            case Stmt.Function f when f.Body != null:
            {
                // Not lifted (not a safe candidate), but its body may contain nested candidates.
                var nb = ProcessBody(f.Body, f.IsGenerator || f.IsAsync);
                return ReferenceEquals(nb, f.Body) ? f : f with { Body = nb };
            }
            case Stmt.Block b:
            {
                var nb = ProcessBody(b.Statements, enclosingIsStateMachine);
                return ReferenceEquals(nb, b.Statements) ? b : new Stmt.Block(nb);
            }
            case Stmt.Sequence s:
            {
                var nb = ProcessBody(s.Statements, enclosingIsStateMachine);
                return ReferenceEquals(nb, s.Statements) ? s : new Stmt.Sequence(nb);
            }
            case Stmt.If i:
            {
                var nt = ProcessStmt(i.ThenBranch, enclosingIsStateMachine);
                var ne = i.ElseBranch != null ? ProcessStmt(i.ElseBranch, enclosingIsStateMachine) : null;
                if (ReferenceEquals(nt, i.ThenBranch) && (i.ElseBranch == null || ReferenceEquals(ne, i.ElseBranch)))
                    return i;
                return new Stmt.If(i.Condition, nt, ne);
            }
            case Stmt.While w:
            {
                var nb = ProcessStmt(w.Body, enclosingIsStateMachine);
                return ReferenceEquals(nb, w.Body) ? w : new Stmt.While(w.Condition, nb);
            }
            case Stmt.DoWhile d:
            {
                var nb = ProcessStmt(d.Body, enclosingIsStateMachine);
                return ReferenceEquals(nb, d.Body) ? d : new Stmt.DoWhile(nb, d.Condition);
            }
            case Stmt.For fo:
            {
                var ni = fo.Initializer != null ? ProcessStmt(fo.Initializer, enclosingIsStateMachine) : null;
                var nb = ProcessStmt(fo.Body, enclosingIsStateMachine);
                if ((fo.Initializer == null || ReferenceEquals(ni, fo.Initializer)) && ReferenceEquals(nb, fo.Body))
                    return fo;
                return new Stmt.For(ni, fo.Condition, fo.Increment, nb);
            }
            case Stmt.ForOf fof:
            {
                var nb = ProcessStmt(fof.Body, enclosingIsStateMachine);
                return ReferenceEquals(nb, fof.Body) ? fof : fof with { Body = nb };
            }
            case Stmt.ForIn fin:
            {
                var nb = ProcessStmt(fin.Body, enclosingIsStateMachine);
                return ReferenceEquals(nb, fin.Body) ? fin : fin with { Body = nb };
            }
            case Stmt.LabeledStatement l:
            {
                var ni = ProcessStmt(l.Statement, enclosingIsStateMachine);
                return ReferenceEquals(ni, l.Statement) ? l : new Stmt.LabeledStatement(l.Label, ni);
            }
            case Stmt.TryCatch t:
            {
                var nt = ProcessBody(t.TryBlock, enclosingIsStateMachine);
                var nc = t.CatchBlock != null ? ProcessBody(t.CatchBlock, enclosingIsStateMachine) : null;
                var nfb = t.FinallyBlock != null ? ProcessBody(t.FinallyBlock, enclosingIsStateMachine) : null;
                if (ReferenceEquals(nt, t.TryBlock)
                    && (t.CatchBlock == null || ReferenceEquals(nc, t.CatchBlock))
                    && (t.FinallyBlock == null || ReferenceEquals(nfb, t.FinallyBlock)))
                    return t;
                return t with { TryBlock = nt, CatchBlock = nc, FinallyBlock = nfb };
            }
            case Stmt.Switch sw:
            {
                List<Stmt.SwitchCase>? newCases = null;
                for (int i = 0; i < sw.Cases.Count; i++)
                {
                    var c = sw.Cases[i];
                    var nb = ProcessBody(c.Body, enclosingIsStateMachine);
                    if (!ReferenceEquals(nb, c.Body))
                    {
                        newCases ??= new List<Stmt.SwitchCase>(sw.Cases);
                        newCases[i] = new Stmt.SwitchCase(c.Value, nb);
                    }
                }
                var newDefault = sw.DefaultBody != null ? ProcessBody(sw.DefaultBody, enclosingIsStateMachine) : null;
                bool defaultChanged = sw.DefaultBody != null && !ReferenceEquals(newDefault, sw.DefaultBody);
                if (newCases == null && !defaultChanged) return sw;
                return new Stmt.Switch(sw.Subject, newCases ?? sw.Cases, defaultChanged ? newDefault : sw.DefaultBody);
            }
            case Stmt.Class cls:
                return ProcessClass(cls);
            case Stmt.Export ex when ex.Declaration != null:
            {
                var nd = ProcessStmt(ex.Declaration, enclosingIsStateMachine);
                return ReferenceEquals(nd, ex.Declaration) ? ex : ex with { Declaration = nd };
            }
            // Stmt.Namespace is intentionally NOT traversed (#583 §3 lift barrier).
            default:
                return stmt;
        }
    }

    private Stmt ProcessClass(Stmt.Class cls)
    {
        List<Stmt.Function>? newMethods = null;
        for (int i = 0; i < cls.Methods.Count; i++)
        {
            var m = cls.Methods[i];
            if (m.Body == null) continue;
            var nb = ProcessBody(m.Body, m.IsGenerator || m.IsAsync);
            if (!ReferenceEquals(nb, m.Body))
            {
                newMethods ??= new List<Stmt.Function>(cls.Methods);
                newMethods[i] = m with { Body = nb };
            }
        }
        return newMethods == null ? cls : cls with { Methods = newMethods };
    }

    #endregion

    /// <summary>
    /// Collects the names of all module top-level bindings. A lifted declaration whose name matches
    /// one is left nested, because the injected <c>var</c> alias would otherwise be hijacked by the
    /// same-named top-level binding under the current name-resolution rules. Deliberately
    /// over-inclusive (type-only names too) — a false positive only declines a lift, which is safe.
    /// </summary>
    private static HashSet<string> CollectTopLevelBindingNames(List<Stmt> module)
    {
        var names = new HashSet<string>();
        foreach (var stmt in module)
            AddBindingName(stmt, names);
        return names;
    }

    private static void AddBindingName(Stmt stmt, HashSet<string> names)
    {
        switch (stmt)
        {
            case Stmt.Function f: names.Add(f.Name.Lexeme); break;
            case Stmt.Class c: names.Add(c.Name.Lexeme); break;
            case Stmt.Var v: names.Add(v.Name.Lexeme); break;
            case Stmt.Const co: names.Add(co.Name.Lexeme); break;
            case Stmt.Enum e: names.Add(e.Name.Lexeme); break;
            case Stmt.Namespace ns: names.Add(ns.Name.Lexeme); break;
            case Stmt.Interface itf: names.Add(itf.Name.Lexeme); break;
            case Stmt.TypeAlias ta: names.Add(ta.Name.Lexeme); break;
            case Stmt.Import imp:
                if (imp.DefaultImport != null) names.Add(imp.DefaultImport.Lexeme);
                if (imp.NamespaceImport != null) names.Add(imp.NamespaceImport.Lexeme);
                if (imp.NamedImports != null)
                    foreach (var spec in imp.NamedImports)
                        names.Add(spec.LocalName?.Lexeme ?? spec.Imported.Lexeme);
                break;
            case Stmt.Export ex when ex.Declaration != null:
                AddBindingName(ex.Declaration, names);
                break;
        }
    }
}
