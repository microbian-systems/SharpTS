using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// Async/await state machine compilation methods for the IL compiler.
/// </summary>
public partial class ILCompiler
{
    private void DefineAsyncFunction(Stmt.Function funcStmt)
    {
        // Analyze the async function for await points and hoisted variables
        var analysis = _async.Analyzer.Analyze(funcStmt);

        // Module-qualify the stub/registry keys (#418) so two modules that each declare a
        // same-named async function don't clobber each other. Single-file compilation returns
        // the simple name unchanged. The readable state-machine type name stays the simple name
        // (the builder's counter already disambiguates `<name>d__N`).
        var ctx = GetDefinitionContext();
        string qualifiedFunctionName = ctx.GetQualifiedFunctionName(funcStmt.Name.Lexeme);

        // Create state machine builder
        var smBuilder = new AsyncStateMachineBuilder(_moduleBuilder, _types, _async.StateMachineCounter++);
        var hasAsyncArrows = analysis.AsyncArrows.Count > 0;
        smBuilder.DefineStateMachine(funcStmt.Name.Lexeme, analysis, _types.Object, false, hasAsyncArrows);

        // Define stub method (returns Task<object>).
        // A trailing rest parameter is typed List<object> so the indirect
        // ($TSFunction.Invoke) call path packs trailing args into it (#426).
        var paramTypes = BuildStateMachineStubParamTypes(funcStmt);
        var stubMethod = _programType.DefineMethod(
            qualifiedFunctionName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            paramTypes
        );

        // Store for later body emission
        _functions.Builders[qualifiedFunctionName] = stubMethod;
        _async.StateMachines[qualifiedFunctionName] = smBuilder;
        _async.Functions[qualifiedFunctionName] = funcStmt;

        // Create function-level display class for captured locals (same as sync functions).
        // This enables closure mutation sharing between the async state machine and sync inner arrows.
        // Variables also captured by async arrows are excluded since they use the hoisted field mechanism.
        _closures.FunctionAstNodes[qualifiedFunctionName] = funcStmt;

        // Collect variables captured by async arrows. By default these can't use the function DC —
        // they're read through the boxed outer state machine in the arrow's MoveNext.
        var asyncCapturedVars = new HashSet<string>();
        foreach (var arrowInfo in analysis.AsyncArrows)
        {
            foreach (var capture in arrowInfo.Captures)
                asyncCapturedVars.Add(capture);
        }

        // EXCEPTION: a captured variable that a direct-child async arrow WRITES is promoted into the
        // function display class (a reference type) instead of staying on the boxed value-type state
        // machine. A boxed struct cannot be mutated in place by verifiable IL — `unbox` yields a
        // readonly managed pointer, so `stfld` through it fails verification and can drop the write in
        // complex state machines (#625). Routing the write through `outer.functionDC.field` (a class
        // reference) is verifiable. Read-only captures stay on the SM field (the existing, working
        // load path).
        var promotedToFunctionDC = ComputeArrowWrittenCapturesToPromote(analysis.AsyncArrows);
        asyncCapturedVars.ExceptWith(promotedToFunctionDC);

        // Filter out (still-)async-captured vars before creating the DC
        if (asyncCapturedVars.Count > 0)
            _closures.AsyncCapturedVarsExclusion[qualifiedFunctionName] = asyncCapturedVars;

        DefineFunctionDisplayClass(funcStmt, qualifiedFunctionName);

        // If a function DC was created, add a field on the state machine to hold it
        if (_closures.FunctionDisplayClasses.TryGetValue(qualifiedFunctionName, out var funcDC))
        {
            smBuilder.DefineFunctionDisplayClassField(funcDC);
        }

        // Build state machines for any async arrows found in this function
        DefineAsyncArrowStateMachines(analysis.AsyncArrows, smBuilder);
    }

    /// <summary>
    /// Returns the captured variables that a DIRECT-CHILD async arrow WRITES (#625): these must be
    /// promoted into the enclosing scope's reference-type function display class so the arrow can
    /// mutate them verifiably through <c>outer.functionDC.field</c> rather than <c>unbox</c>+<c>stfld</c>
    /// on the boxed value-type state machine (the latter is unverifiable and can drop the write).
    /// A variable also captured by a NESTED (grandchild) async arrow is excluded — the grandchild's
    /// DC-access path isn't wired (its outer reference points at the parent arrow, not the function),
    /// so only direct-child writes are promoted. Shared by free async functions, async methods (#682),
    /// each of which supplies its own <paramref name="asyncArrows"/> analysis.
    /// </summary>
    private static HashSet<string> ComputeArrowWrittenCapturesToPromote(
        List<AsyncStateAnalyzer.AsyncArrowInfo> asyncArrows)
    {
        var nestedAsyncArrowCaptures = new HashSet<string>();
        foreach (var arrowInfo in asyncArrows)
            if (arrowInfo.ParentArrow != null)
                nestedAsyncArrowCaptures.UnionWith(arrowInfo.Captures);

        var promoted = new HashSet<string>();
        foreach (var arrowInfo in asyncArrows)
        {
            if (arrowInfo.ParentArrow != null) continue; // direct children only
            var written = CapturedWriteAnalysis.CollectImmediateWrites(arrowInfo.Arrow);
            written.IntersectWith(arrowInfo.Captures);
            written.ExceptWith(nestedAsyncArrowCaptures);
            promoted.UnionWith(written);
        }
        return promoted;
    }

    /// <summary>
    /// #682: builds the method-scoped function display class holding the captures that a direct-child
    /// async arrow writes (<see cref="ComputeArrowWrittenCapturesToPromote"/>) and adds the DC field to
    /// the method's state machine. Async methods (instance and static) otherwise have no function-DC
    /// infrastructure, so a captured write would emit the unverifiable <c>unbox</c>+<c>stfld</c> on the
    /// boxed value-type state machine. <paramref name="dcKey"/> must be unique per method and disjoint
    /// from free-function registry keys (which never contain "::"). Returns <paramref name="dcKey"/> so
    /// the caller can thread it to <see cref="EmitAsyncStubMethod"/> and <see cref="WireAsyncMethodFunctionDC"/>.
    /// </summary>
    private string SetupAsyncMethodFunctionDC(AsyncStateMachineBuilder smBuilder, string dcKey,
        AsyncStateAnalyzer.AsyncFunctionAnalysis analysis)
    {
        var promoted = ComputeArrowWrittenCapturesToPromote(analysis.AsyncArrows);
        if (promoted.Count > 0)
        {
            RegisterFunctionDisplayClass(dcKey, promoted);
            if (_closures.FunctionDisplayClasses.TryGetValue(dcKey, out var dc))
                smBuilder.DefineFunctionDisplayClassField(dc);
        }
        return dcKey;
    }

    /// <summary>
    /// #682: wires <paramref name="ctx"/> so the async method body (AsyncMoveNextEmitter) reads/writes
    /// the promoted captures via <c>this.functionDC.field</c> and a nested async arrow
    /// (AsyncArrowMoveNextEmitter, sharing this ctx) reaches them via <c>outer.functionDC.field</c>.
    /// No-op when nothing was promoted (no DC field on the state machine).
    /// </summary>
    private void WireAsyncMethodFunctionDC(CompilationContext ctx, AsyncStateMachineBuilder smBuilder, string dcKey)
    {
        if (smBuilder.FunctionDCField != null &&
            _closures.FunctionDisplayClassFields.TryGetValue(dcKey, out var dcFields))
        {
            ctx.FunctionDisplayClassFields = dcFields;
            ctx.CapturedFunctionLocals = new HashSet<string>(dcFields.Keys);
            ctx.OuterFunctionDCField = smBuilder.FunctionDCField;
        }
    }

    private void DefineAsyncArrowStateMachines(
        List<AsyncStateAnalyzer.AsyncArrowInfo> asyncArrows,
        AsyncStateMachineBuilder outerBuilder)
    {
        // Get all hoisted fields from the function's state machine
        Dictionary<string, FieldBuilder> functionHoistedFields = [];
        foreach (var (name, field) in outerBuilder.HoistedParameters)
            functionHoistedFields[name] = field;
        foreach (var (name, field) in outerBuilder.HoistedLocals)
            functionHoistedFields[name] = field;

        // Sort arrows by nesting level to ensure parents are defined before children
        var sortedArrows = asyncArrows.OrderBy(a => a.NestingLevel).ToList();

        // Build a set of arrows that have nested async children
        var arrowsWithNestedChildren = new HashSet<Expr.ArrowFunction>(ReferenceEqualityComparer.Instance);
        foreach (var arrowInfo in sortedArrows)
        {
            if (arrowInfo.ParentArrow != null)
            {
                arrowsWithNestedChildren.Add(arrowInfo.ParentArrow);
            }
        }

        foreach (var arrowInfo in sortedArrows)
        {
            // Create a dedicated analyzer for this arrow's await points
            var arrowAnalysis = AnalyzeAsyncArrow(arrowInfo.Arrow);

            // Create state machine builder for the async arrow
            var arrowBuilder = new AsyncArrowStateMachineBuilder(
                _moduleBuilder,
                _types,
                arrowInfo.Arrow,
                arrowInfo.Captures,
                _async.ArrowCounter++);

            // Determine the outer state machine type and hoisted fields
            Type outerStateMachineType;
            Dictionary<string, FieldBuilder> outerHoistedFields;

            // Check if this arrow has nested async children
            bool hasNestedChildren = arrowsWithNestedChildren.Contains(arrowInfo.Arrow);

            if (arrowInfo.ParentArrow == null)
            {
                // Direct child of the function - use function's state machine
                outerStateMachineType = outerBuilder.StateMachineType;
                outerHoistedFields = functionHoistedFields;
                _async.ArrowOuterBuilders[arrowInfo.Arrow] = outerBuilder;
            }
            else
            {
                // Nested arrow - use parent arrow's state machine
                if (!_async.ArrowBuilders.TryGetValue(arrowInfo.ParentArrow, out var parentBuilder))
                {
                    throw new CompileException(
                        $"Parent async arrow not found. Nesting level: {arrowInfo.NestingLevel}");
                }

                outerStateMachineType = parentBuilder.StateMachineType;

                // Get hoisted fields from parent arrow - includes its parameters, locals, and captured fields
                outerHoistedFields = [];
                foreach (var (name, field) in parentBuilder.ParameterFields)
                    outerHoistedFields[name] = field;
                foreach (var (name, field) in parentBuilder.LocalFields)
                    outerHoistedFields[name] = field;
                // Also include captured fields - they're accessible through parent's outer reference
                // These are "transitive" captures - we need to go through parent's <>__outer to access them
                HashSet<string> transitiveCaptures = [];
                foreach (var (name, field) in parentBuilder.CapturedFieldMap)
                {
                    outerHoistedFields[name] = field;
                    transitiveCaptures.Add(name);
                }
                // Also include parent's transitive captures (for deeper nesting)
                foreach (var name in parentBuilder.TransitiveCaptures)
                {
                    transitiveCaptures.Add(name);
                }

                _async.ArrowParentBuilders[arrowInfo.Arrow] = parentBuilder;

                // Pass transitive info for nested arrows
                arrowBuilder.DefineStateMachine(
                    outerStateMachineType,
                    outerHoistedFields,
                    arrowAnalysis.AwaitCount,
                    arrowInfo.Arrow.Parameters,
                    arrowAnalysis.HoistedLocals,
                    transitiveCaptures,
                    parentBuilder.OuterStateMachineField,
                    parentBuilder.OuterStateMachineType,
                    hasNestedChildren);

                // Define the stub method that will be called to invoke the async arrow
                arrowBuilder.DefineStubMethod(_programType);
                MarkPadsUndefined(arrowBuilder.StubMethod); // #640

                _async.ArrowBuilders[arrowInfo.Arrow] = arrowBuilder;
                continue; // Already handled the full setup
            }

            arrowBuilder.DefineStateMachine(
                outerStateMachineType,
                outerHoistedFields,
                arrowAnalysis.AwaitCount,
                arrowInfo.Arrow.Parameters,
                arrowAnalysis.HoistedLocals,
                hasNestedAsyncArrows: hasNestedChildren);

            // Define the stub method that will be called to invoke the async arrow
            arrowBuilder.DefineStubMethod(_programType);
            MarkPadsUndefined(arrowBuilder.StubMethod); // #640

            _async.ArrowBuilders[arrowInfo.Arrow] = arrowBuilder;
        }
    }

    /// <summary>
    /// Analyzes an async arrow function to determine its await points and hoisted variables.
    /// Uses pooled HashSets for intermediate analysis to reduce allocations.
    /// </summary>
    private (int AwaitCount, HashSet<string> HoistedLocals) AnalyzeAsyncArrow(Expr.ArrowFunction arrow)
    {
        var awaitCount = 0;
        var seenAwait = false;

        // Clear and reuse pooled HashSets
        _async.DeclaredVars.Clear();
        _async.UsedAfterAwait.Clear();
        _async.DeclaredBeforeAwait.Clear();

        // Add parameters as declared variables
        foreach (var param in arrow.Parameters)
        {
            _async.DeclaredVars.Add(param.Name.Lexeme);
            _async.DeclaredBeforeAwait.Add(param.Name.Lexeme);
        }

        // Analyze expression body or block body
        if (arrow.ExpressionBody != null)
        {
            AnalyzeArrowExprForAwaits(arrow.ExpressionBody, ref awaitCount, ref seenAwait,
                _async.DeclaredVars, _async.UsedAfterAwait, _async.DeclaredBeforeAwait);
        }
        else if (arrow.BlockBody != null)
        {
            foreach (var stmt in arrow.BlockBody)
            {
                AnalyzeArrowStmtForAwaits(stmt, ref awaitCount, ref seenAwait,
                    _async.DeclaredVars, _async.UsedAfterAwait, _async.DeclaredBeforeAwait);
            }
        }

        // Variables that need hoisting: declared before await AND used after await
        // This result must be a new allocation since ownership is transferred to caller
        var hoistedLocals = new HashSet<string>(_async.DeclaredBeforeAwait);
        hoistedLocals.IntersectWith(_async.UsedAfterAwait);

        // Remove parameters from hoisted locals (they're stored separately)
        foreach (var param in arrow.Parameters)
            hoistedLocals.Remove(param.Name.Lexeme);

        return (awaitCount, hoistedLocals);
    }

    private void AnalyzeArrowStmtForAwaits(Stmt stmt, ref int awaitCount, ref bool seenAwait,
        HashSet<string> declaredVariables, HashSet<string> usedAfterAwait, HashSet<string> declaredBeforeAwait)
    {
        switch (stmt)
        {
            case Stmt.Var v:
                declaredVariables.Add(v.Name.Lexeme);
                if (!seenAwait)
                    declaredBeforeAwait.Add(v.Name.Lexeme);
                if (v.Initializer != null)
                    AnalyzeArrowExprForAwaits(v.Initializer, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Stmt.Const c:
                declaredVariables.Add(c.Name.Lexeme);
                if (!seenAwait)
                    declaredBeforeAwait.Add(c.Name.Lexeme);
                AnalyzeArrowExprForAwaits(c.Initializer, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Stmt.Expression e:
                AnalyzeArrowExprForAwaits(e.Expr, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Stmt.Return r:
                if (r.Value != null)
                    AnalyzeArrowExprForAwaits(r.Value, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Stmt.If i:
                AnalyzeArrowExprForAwaits(i.Condition, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowStmtForAwaits(i.ThenBranch, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                if (i.ElseBranch != null)
                    AnalyzeArrowStmtForAwaits(i.ElseBranch, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Stmt.While w:
                AnalyzeArrowExprForAwaits(w.Condition, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowStmtForAwaits(w.Body, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Stmt.ForOf f:
                declaredVariables.Add(f.Variable.Lexeme);
                if (!seenAwait)
                    declaredBeforeAwait.Add(f.Variable.Lexeme);
                AnalyzeArrowExprForAwaits(f.Iterable, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowStmtForAwaits(f.Body, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Stmt.For forStmt:
                if (forStmt.Initializer != null)
                    AnalyzeArrowStmtForAwaits(forStmt.Initializer, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                if (forStmt.Condition != null)
                    AnalyzeArrowExprForAwaits(forStmt.Condition, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                if (forStmt.Increment != null)
                    AnalyzeArrowExprForAwaits(forStmt.Increment, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowStmtForAwaits(forStmt.Body, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Stmt.Block b:
                foreach (var s in b.Statements)
                    AnalyzeArrowStmtForAwaits(s, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Stmt.Sequence seq:
                foreach (var s in seq.Statements)
                    AnalyzeArrowStmtForAwaits(s, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Stmt.TryCatch t:
                foreach (var ts in t.TryBlock)
                    AnalyzeArrowStmtForAwaits(ts, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                if (t.CatchBlock != null)
                {
                    if (t.CatchParam != null)
                    {
                        declaredVariables.Add(t.CatchParam.Lexeme);
                        if (!seenAwait)
                            declaredBeforeAwait.Add(t.CatchParam.Lexeme);
                    }
                    foreach (var cs in t.CatchBlock)
                        AnalyzeArrowStmtForAwaits(cs, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                }
                if (t.FinallyBlock != null)
                    foreach (var fs in t.FinallyBlock)
                        AnalyzeArrowStmtForAwaits(fs, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Stmt.Switch s:
                AnalyzeArrowExprForAwaits(s.Subject, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                foreach (var c in s.Cases)
                {
                    AnalyzeArrowExprForAwaits(c.Value, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                    foreach (var cs in c.Body)
                        AnalyzeArrowStmtForAwaits(cs, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                }
                if (s.DefaultBody != null)
                    foreach (var ds in s.DefaultBody)
                        AnalyzeArrowStmtForAwaits(ds, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Stmt.Throw th:
                AnalyzeArrowExprForAwaits(th.Value, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Stmt.Print p:
                AnalyzeArrowExprForAwaits(p.Expr, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
        }
    }

    private void AnalyzeArrowExprForAwaits(Expr expr, ref int awaitCount, ref bool seenAwait,
        HashSet<string> declaredVariables, HashSet<string> usedAfterAwait, HashSet<string> declaredBeforeAwait)
    {
        switch (expr)
        {
            case Expr.Await a:
                awaitCount++;
                seenAwait = true;
                AnalyzeArrowExprForAwaits(a.Expression, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.Variable v:
                if (seenAwait && declaredVariables.Contains(v.Name.Lexeme))
                    usedAfterAwait.Add(v.Name.Lexeme);
                break;
            case Expr.Assign a:
                if (seenAwait && declaredVariables.Contains(a.Name.Lexeme))
                    usedAfterAwait.Add(a.Name.Lexeme);
                AnalyzeArrowExprForAwaits(a.Value, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.Binary b:
                AnalyzeArrowExprForAwaits(b.Left, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowExprForAwaits(b.Right, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.Logical l:
                AnalyzeArrowExprForAwaits(l.Left, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowExprForAwaits(l.Right, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.Unary u:
                AnalyzeArrowExprForAwaits(u.Right, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.Delete d:
                AnalyzeArrowExprForAwaits(d.Operand, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.Grouping g:
                AnalyzeArrowExprForAwaits(g.Expression, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.Call c:
                AnalyzeArrowExprForAwaits(c.Callee, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                foreach (var arg in c.Arguments)
                    AnalyzeArrowExprForAwaits(arg, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.Get g:
                AnalyzeArrowExprForAwaits(g.Object, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.Set s:
                AnalyzeArrowExprForAwaits(s.Object, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowExprForAwaits(s.Value, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.GetIndex gi:
                AnalyzeArrowExprForAwaits(gi.Object, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowExprForAwaits(gi.Index, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.SetIndex si:
                AnalyzeArrowExprForAwaits(si.Object, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowExprForAwaits(si.Index, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowExprForAwaits(si.Value, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.New n:
                foreach (var arg in n.Arguments)
                    AnalyzeArrowExprForAwaits(arg, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.ArrayLiteral a:
                foreach (var elem in a.Elements)
                    AnalyzeArrowExprForAwaits(elem, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.ObjectLiteral o:
                foreach (var prop in o.Properties)
                    AnalyzeArrowExprForAwaits(prop.Value, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.Ternary t:
                AnalyzeArrowExprForAwaits(t.Condition, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowExprForAwaits(t.ThenBranch, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowExprForAwaits(t.ElseBranch, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.NullishCoalescing nc:
                AnalyzeArrowExprForAwaits(nc.Left, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowExprForAwaits(nc.Right, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.TemplateLiteral tl:
                foreach (var e in tl.Expressions)
                    AnalyzeArrowExprForAwaits(e, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.TaggedTemplateLiteral ttl:
                AnalyzeArrowExprForAwaits(ttl.Tag, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                foreach (var e in ttl.Expressions)
                    AnalyzeArrowExprForAwaits(e, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.CompoundAssign ca:
                if (seenAwait && declaredVariables.Contains(ca.Name.Lexeme))
                    usedAfterAwait.Add(ca.Name.Lexeme);
                AnalyzeArrowExprForAwaits(ca.Value, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.CompoundSet cs:
                AnalyzeArrowExprForAwaits(cs.Object, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowExprForAwaits(cs.Value, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.CompoundSetIndex csi:
                AnalyzeArrowExprForAwaits(csi.Object, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowExprForAwaits(csi.Index, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                AnalyzeArrowExprForAwaits(csi.Value, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.PrefixIncrement pi:
                AnalyzeArrowExprForAwaits(pi.Operand, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.PostfixIncrement poi:
                AnalyzeArrowExprForAwaits(poi.Operand, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.ArrowFunction:
                // Nested arrows don't contribute to this arrow's await analysis
                break;
            case Expr.TypeAssertion ta:
                AnalyzeArrowExprForAwaits(ta.Expression, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.Satisfies sat:
                AnalyzeArrowExprForAwaits(sat.Expression, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.NonNullAssertion nna:
                AnalyzeArrowExprForAwaits(nna.Expression, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
            case Expr.Spread sp:
                AnalyzeArrowExprForAwaits(sp.Expression, ref awaitCount, ref seenAwait, declaredVariables, usedAfterAwait, declaredBeforeAwait);
                break;
        }
    }

    private void EmitAsyncStateMachineBodies()
    {
        var savedPath = _modules.CurrentPath;
        var savedNamespacePath = _currentNamespacePath;
        foreach (var (funcName, smBuilder) in _async.StateMachines)
        {
            if (_functionDefinitionModule.TryGetValue(funcName, out var fnModule))
            {
                _modules.CurrentPath = NormalizeToEmissionPath(fnModule);
            }
            // Restore the enclosing namespace (null for non-namespace functions) so the
            // MoveNext body resolves namespace-level var/let/const by bare name (#567).
            _currentNamespacePath = _functionDefinitionNamespace.GetValueOrDefault(funcName);
            var func = _async.Functions[funcName];
            var stubMethod = _functions.Builders[funcName];
            var analysis = _async.Analyzer.Analyze(func);

            // Emit stub method body
            EmitAsyncStubMethod(stubMethod, smBuilder, func.Parameters);

            // Create context for MoveNext emission
            var il = smBuilder.MoveNextMethod.GetILGenerator();
            var ctx = new CompilationContext(il, _typeMapper, _functions.Builders, _classes.Builders, _namespaceFields, _namespaceVarFields, _types)
            {
                Runtime = _runtime,
                ClosureAnalyzer = _closures.Analyzer,
                ArrowMethods = _closures.ArrowMethods,
            ConstArrowBindings = _closures.ConstArrowBindings,
                DisplayClasses = _closures.DisplayClasses,
                DisplayClassFields = _closures.DisplayClassFields,
                DisplayClassConstructors = _closures.DisplayClassConstructors,
                EnumMembers = _enums.Members,
                EnumReverse = _enums.Reverse,
                EnumKinds = _enums.Kinds,
                TopLevelStaticVars = BuildTopLevelStaticVarsForModule(_modules.CurrentPath),
                FunctionRestParams = _functions.RestParams,
                FunctionsCapturingArguments = _functions.CapturingArguments,
                FunctionGenericParams = _functions.GenericParams,
                IsGenericFunction = _functions.IsGeneric,
                TypeMap = _typeMap,
                DeadCode = _deadCodeInfo,
                AsyncMethods = null,
                AsyncArrowBuilders = _async.ArrowBuilders,
                AsyncArrowOuterBuilders = _async.ArrowOuterBuilders,
                AsyncArrowParentBuilders = _async.ArrowParentBuilders,
                // Module support for multi-module compilation
                CurrentModulePath = _modules.CurrentPath,
                CurrentNamespacePath = _currentNamespacePath,
                ModuleResolver = _modules.Resolver,
                CommonJsExportFields = _modules.CommonJsExportFields,
                CommonJsGetExportsMethods = _modules.CommonJsGetExportsMethods,
                ClassToModule = _modules.ClassToModule,
                FunctionToModule = _modules.FunctionToModule,
                EnumToModule = _modules.EnumToModule,
                DotNetNamespace = _modules.CurrentDotNetNamespace,
                TypeEmitterRegistry = _typeEmitterRegistry,
                BuiltInModuleEmitterRegistry = _builtInModuleEmitterRegistry,
                BuiltInModuleNamespaces = _builtInModuleNamespaces,
                BuiltInModuleMethodBindings = GetCurrentBuiltInMethodBindings(),
                ImportedNames = _importedNames,
                ClassExprBuilders = _classExprs.Builders,
                // Check for function-level "use strict" directive
                IsStrictMode = _isStrictMode || CheckForUseStrict(func.Body),
                // Registry services
                ClassRegistry = GetClassRegistry(),
                // Entry-point display class for captured top-level variables
                EntryPointDisplayClassFields = BuildEntryPointDisplayClassFieldsForModule(_modules.CurrentPath),
                CapturedTopLevelVars = BuildCapturedTopLevelVarsForModule(_modules.CurrentPath),
                ArrowEntryPointDCFields = _closures.ArrowEntryPointDCFields.Count > 0 ? _closures.ArrowEntryPointDCFields : null,
                EntryPointDisplayClassStaticField = _closures.EntryPointDisplayClassStaticField,
                // Function display class for captured locals (closure mutation sharing with arrows)
                ArrowFunctionDCFields = _closures.ArrowFunctionDCFields.Count > 0 ? _closures.ArrowFunctionDCFields : null,
            };

            // Set function DC info if this async function has captured locals.
            // funcName is already the module-qualified registry key (#418) — and the
            // closure registries are keyed by that same qualified name — so use it
            // directly rather than re-qualifying (which would double-prefix).
            var qualifiedName = funcName;
            if (_closures.FunctionDisplayClassFields.TryGetValue(qualifiedName, out var funcDCFields))
            {
                ctx.FunctionDisplayClassFields = funcDCFields;
                ctx.CapturedFunctionLocals = new HashSet<string>(funcDCFields.Keys);
            }
            // Carry the function's own DC field so async arrows nested in this function can reach
            // promoted captures through `outer.functionDC.field` (#625). Propagated to the arrow
            // MoveNext context in EmitAsyncArrowMoveNext.
            ctx.OuterFunctionDCField = smBuilder.FunctionDCField;

            // Emit MoveNext body
            var moveNextEmitter = new AsyncMoveNextEmitter(smBuilder, analysis, _types);
            moveNextEmitter.EmitMoveNext(func.Body, ctx, _types.Object, func.Parameters);
            ILLabelValidator.Validate(smBuilder.MoveNextMethod.GetILGenerator(),
                $"async MoveNext {funcName}");

            // Emit async arrow MoveNext bodies
            foreach (var arrowInfo in analysis.AsyncArrows)
            {
                if (_async.ArrowBuilders.TryGetValue(arrowInfo.Arrow, out var arrowBuilder))
                {
                    EmitAsyncArrowMoveNext(arrowBuilder, arrowInfo.Arrow, ctx);
                }
            }

            // Finalize state machine type
            smBuilder.CreateType();
        }
        _modules.CurrentPath = savedPath;
        _currentNamespacePath = savedNamespacePath;

        // Finalize nested async arrow state machine types. Standalone (top-level) arrows are
        // finalized in EmitTopLevelAsyncArrowBodies AFTER their MoveNext is emitted — creating
        // them here would freeze the type before that pass can define spill fields (#400/#414).
        foreach (var (_, arrowBuilder) in _async.ArrowBuilders)
        {
            if (arrowBuilder.IsStandalone) continue;
            arrowBuilder.CreateType();
        }
    }

    private void EmitAsyncArrowMoveNext(AsyncArrowStateMachineBuilder arrowBuilder, Expr.ArrowFunction arrow, CompilationContext parentCtx)
    {
        // Create IL generator for the arrow's MoveNext
        var il = arrowBuilder.MoveNextMethod.GetILGenerator();

        // Create analysis for this arrow
        var arrowAnalysis = AnalyzeAsyncArrow(arrow);
        var analysis = new AsyncStateAnalyzer.AsyncFunctionAnalysis(
            arrowAnalysis.AwaitCount,
            [], // We'll regenerate await points during emission
            arrowAnalysis.HoistedLocals,
            new HashSet<string>(arrow.Parameters.Select(p => p.Name.Lexeme)),
            false, // HasTryCatch - will be detected during emission
            arrowBuilder.Captures.Contains("this"),
            [] // No nested async arrows handled yet
        );

        // Create a new context for arrow MoveNext emission
        var ctx = new CompilationContext(il, parentCtx.TypeMapper, parentCtx.Functions, parentCtx.Classes, parentCtx.NamespaceFields, parentCtx.NamespaceVarFields, parentCtx.Types)
        {
            Runtime = parentCtx.Runtime,
            ClosureAnalyzer = parentCtx.ClosureAnalyzer,
            ArrowMethods = parentCtx.ArrowMethods,
            ConstArrowBindings = parentCtx.ConstArrowBindings,
            DisplayClasses = parentCtx.DisplayClasses,
            DisplayClassFields = parentCtx.DisplayClassFields,
            DisplayClassConstructors = parentCtx.DisplayClassConstructors,
            EnumMembers = parentCtx.EnumMembers,
            EnumReverse = parentCtx.EnumReverse,
            EnumKinds = parentCtx.EnumKinds,
            TopLevelStaticVars = parentCtx.TopLevelStaticVars,
            FunctionRestParams = parentCtx.FunctionRestParams,
            FunctionGenericParams = parentCtx.FunctionGenericParams,
            IsGenericFunction = parentCtx.IsGenericFunction,
            TypeMap = parentCtx.TypeMap,
            DeadCode = parentCtx.DeadCode,
            AsyncMethods = null,
            AsyncArrowBuilders = _async.ArrowBuilders,
            AsyncArrowOuterBuilders = _async.ArrowOuterBuilders,
            AsyncArrowParentBuilders = _async.ArrowParentBuilders,
            // Inherit module support from parent context
            CurrentModulePath = parentCtx.CurrentModulePath,
            ClassToModule = parentCtx.ClassToModule,
            FunctionToModule = parentCtx.FunctionToModule,
            EnumToModule = parentCtx.EnumToModule,
            TypeEmitterRegistry = parentCtx.TypeEmitterRegistry,
            ClassExprBuilders = parentCtx.ClassExprBuilders,
            IsStrictMode = parentCtx.IsStrictMode,
            // ES2022 Private Class Elements support - inherit from parent context
            CurrentClassName = parentCtx.CurrentClassName,
            CurrentClassBuilder = parentCtx.CurrentClassBuilder,
            // Registry services
            ClassRegistry = parentCtx.ClassRegistry,
            // Entry-point display class for captured top-level variables
            EntryPointDisplayClassFields = parentCtx.EntryPointDisplayClassFields,
            CapturedTopLevelVars = parentCtx.CapturedTopLevelVars,
            EntryPointDisplayClassStaticField = parentCtx.EntryPointDisplayClassStaticField,
            // Captured locals promoted into the enclosing function's display class (#625): the
            // arrow reads/writes them through `outer.functionDC.field` rather than mutating the
            // boxed value-type state machine in place (unverifiable). Only fields the function
            // actually placed in its DC are listed here, so a name present means "route via DC".
            FunctionDisplayClassFields = parentCtx.FunctionDisplayClassFields,
            OuterFunctionDCField = parentCtx.OuterFunctionDCField,
        };

        // Create arrow-specific emitter
        var arrowEmitter = new AsyncArrowMoveNextEmitter(arrowBuilder, analysis, _types);

        // Get the body statements
        List<Stmt> bodyStatements;
        if (arrow.BlockBody != null)
        {
            bodyStatements = arrow.BlockBody;
        }
        else if (arrow.ExpressionBody != null)
        {
            // Create a synthetic return statement for expression body arrows
            var returnToken = new Token(TokenType.RETURN, "return", null, 0);
            bodyStatements = [new Stmt.Return(returnToken, arrow.ExpressionBody)];
        }
        else
        {
            bodyStatements = [];
        }

        arrowEmitter.EmitMoveNext(bodyStatements, ctx, _types.Object, arrow.Parameters);
        ILLabelValidator.Validate(arrowBuilder.MoveNextMethod.GetILGenerator(),
            $"async arrow MoveNext");
    }

    private void EmitAsyncStubMethod(
        MethodBuilder stubMethod,
        AsyncStateMachineBuilder smBuilder,
        List<Stmt.Parameter> parameters,
        bool isInstanceMethod = false,
        FieldBuilder? asyncLockField = null,
        FieldBuilder? lockReentrancyField = null,
        string? functionDCKey = null)
    {
        var il = stubMethod.GetILGenerator();
        var smLocal = il.DeclareLocal(smBuilder.StateMachineType);

        // var sm = default(<StateMachine>);
        il.Emit(OpCodes.Ldloca, smLocal);
        il.Emit(OpCodes.Initobj, smBuilder.StateMachineType);

        // Copy 'this' to state machine if this is an instance method and uses 'this'
        if (isInstanceMethod && smBuilder.ThisField != null)
        {
            il.Emit(OpCodes.Ldloca, smLocal);
            il.Emit(OpCodes.Ldarg_0);  // 'this' is arg 0 for instance methods
            il.Emit(OpCodes.Stfld, smBuilder.ThisField);
        }

        // Copy @lock field references to state machine if this method has @lock decorator
        // We copy these directly here so MoveNext doesn't need to cast ThisField
        if (smBuilder.HasLockDecorator && asyncLockField != null && lockReentrancyField != null)
        {
            if (isInstanceMethod)
            {
                // Instance method: sm.<>__asyncLockRef = this._asyncLock;
                il.Emit(OpCodes.Ldloca, smLocal);
                il.Emit(OpCodes.Ldarg_0);  // 'this'
                il.Emit(OpCodes.Ldfld, asyncLockField);
                il.Emit(OpCodes.Stfld, smBuilder.AsyncLockRefField!);

                // sm.<>__lockReentrancyRef = this._lockReentrancy;
                il.Emit(OpCodes.Ldloca, smLocal);
                il.Emit(OpCodes.Ldarg_0);  // 'this'
                il.Emit(OpCodes.Ldfld, lockReentrancyField);
                il.Emit(OpCodes.Stfld, smBuilder.LockReentrancyRefField!);
            }
            else
            {
                // Static method: sm.<>__asyncLockRef = ClassName._staticAsyncLock;
                il.Emit(OpCodes.Ldloca, smLocal);
                il.Emit(OpCodes.Ldsfld, asyncLockField);  // Static field - no 'this'
                il.Emit(OpCodes.Stfld, smBuilder.AsyncLockRefField!);

                // sm.<>__lockReentrancyRef = ClassName._staticLockReentrancy;
                il.Emit(OpCodes.Ldloca, smLocal);
                il.Emit(OpCodes.Ldsfld, lockReentrancyField);  // Static field - no 'this'
                il.Emit(OpCodes.Stfld, smBuilder.LockReentrancyRefField!);
            }
        }

        // Copy parameters to state machine fields
        // For instance methods, parameters start at arg 1 (arg 0 is 'this')
        // Hoisted parameter fields are typed as 'object', so we need to box value types
        int paramOffset = isInstanceMethod ? 1 : 0;
        var stubParams = stubMethod.GetParameters();
        for (int i = 0; i < parameters.Count; i++)
        {
            string paramName = parameters[i].Name.Lexeme;
            if (smBuilder.HoistedParameters.TryGetValue(paramName, out var field))
            {
                il.Emit(OpCodes.Ldloca, smLocal);
                il.Emit(OpCodes.Ldarg, i + paramOffset);
                // Box value types since hoisted fields are typed as object
                if (i < stubParams.Length && stubParams[i].ParameterType.IsValueType)
                {
                    il.Emit(OpCodes.Box, stubParams[i].ParameterType);
                }
                il.Emit(OpCodes.Stfld, field);
            }
        }

        // Initialize function display class for closure mutation sharing.
        // For top-level async functions the stub method name is already the module-qualified
        // registry/closure key (#418), so it's used directly. Async methods (#682) supply an
        // explicit key (their stub name is just the bare method name, which isn't a registry key).
        if (smBuilder.FunctionDCField != null)
        {
            var qualifiedName = functionDCKey ?? stubMethod.Name;
            if (_closures.FunctionDisplayClassCtors.TryGetValue(qualifiedName, out var dcCtor))
            {
                il.Emit(OpCodes.Ldloca, smLocal);
                il.Emit(OpCodes.Newobj, dcCtor);
                il.Emit(OpCodes.Stfld, smBuilder.FunctionDCField);

                // Copy captured parameters into the function DC
                if (_closures.FunctionDisplayClassFields.TryGetValue(qualifiedName, out var dcFields))
                {
                    for (int j = 0; j < parameters.Count; j++)
                    {
                        string pName = parameters[j].Name.Lexeme;
                        if (dcFields.TryGetValue(pName, out var dcField))
                        {
                            il.Emit(OpCodes.Ldloca, smLocal);
                            il.Emit(OpCodes.Ldfld, smBuilder.FunctionDCField);
                            il.Emit(OpCodes.Ldarg, j + paramOffset);
                            if (j < stubParams.Length && stubParams[j].ParameterType.IsValueType)
                                il.Emit(OpCodes.Box, stubParams[j].ParameterType);
                            il.Emit(OpCodes.Stfld, dcField);
                        }
                    }
                }
            }
        }

        // sm.<>t__builder = AsyncTaskMethodBuilder<T>.Create();
        il.Emit(OpCodes.Ldloca, smLocal);
        il.Emit(OpCodes.Call, smBuilder.GetBuilderCreateMethod());
        il.Emit(OpCodes.Stfld, smBuilder.BuilderField);

        // sm.<>1__state = -1;
        il.Emit(OpCodes.Ldloca, smLocal);
        il.Emit(OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Stfld, smBuilder.StateField);

        // If this function has async arrows, we need to box the state machine first
        // and store the boxed reference so async arrows can share the same instance
        if (smBuilder.SelfBoxedField != null)
        {
            // This function defines async arrows that must share its boxed state machine.
            // Box once and run the box via a verifiable ref (see helper for the #414 fix).
            StateMachineEmitHelpers.EmitSelfBoxedStartAndReturnTask(
                il,
                smLocal,
                smBuilder.StateMachineType,
                smBuilder.SelfBoxedField,
                smBuilder.BuilderField,
                smBuilder.GetBuilderStartMethod(),
                smBuilder.GetBuilderTaskGetter(),
                _types);
        }
        else
        {
            // Standard path: use stack-based state machine (runtime boxes internally)
            // sm.<>t__builder.Start(ref sm);
            il.Emit(OpCodes.Ldloca, smLocal);
            il.Emit(OpCodes.Ldflda, smBuilder.BuilderField);
            il.Emit(OpCodes.Ldloca, smLocal);
            il.Emit(OpCodes.Call, smBuilder.GetBuilderStartMethod());

            // return sm.<>t__builder.Task;
            il.Emit(OpCodes.Ldloca, smLocal);
            il.Emit(OpCodes.Ldflda, smBuilder.BuilderField);
            il.Emit(OpCodes.Call, smBuilder.GetBuilderTaskGetter());
            il.Emit(OpCodes.Ret);
        }
    }

    private void EmitAsyncMethodBody(MethodBuilder methodBuilder, Stmt.Function method, FieldInfo fieldsField)
    {
        // Analyze async function to determine await points and hoisted variables
        var analysis = _async.Analyzer.Analyze(method);

        // Check if method has @lock decorator
        bool hasLock = HasLockDecorator(method);

        // Build state machine type
        var smBuilder = new AsyncStateMachineBuilder(_moduleBuilder, _types, _async.StateMachineCounter++);
        var hasAsyncArrows = analysis.AsyncArrows.Count > 0;
        smBuilder.DefineStateMachine(
            $"{methodBuilder.DeclaringType!.Name}_{method.Name.Lexeme}",
            analysis,
            _types.Object,
            isInstanceMethod: true,  // This is an instance method
            hasAsyncArrows: hasAsyncArrows,
            hasLock: hasLock
        );

        // #682: a direct-child async arrow that WRITES a captured method-local must mutate it through
        // a reference-type function display class, not the boxed value-type state machine (unverifiable
        // unbox → readonly pointer). Async instance methods have no function-DC by default; wire one.
        string methodDCKey = SetupAsyncMethodFunctionDC(
            smBuilder, $"{methodBuilder.DeclaringType!.Name}::{method.Name.Lexeme}", analysis);

        // Build state machines for any async arrows found in this method
        DefineAsyncArrowStateMachines(analysis.AsyncArrows, smBuilder);

        // Get lock fields if @lock decorator is present
        FieldBuilder? asyncLockField = null;
        FieldBuilder? lockReentrancyField = null;
        if (hasLock)
        {
            var className = methodBuilder.DeclaringType!.Name;
            _locks.AsyncLockFields.TryGetValue(className, out asyncLockField);
            _locks.ReentrancyFields.TryGetValue(className, out lockReentrancyField);
        }

        // Emit stub method body (creates state machine and starts it)
        EmitAsyncStubMethod(
            methodBuilder,
            smBuilder,
            method.Parameters,
            isInstanceMethod: true,
            asyncLockField,
            lockReentrancyField,
            functionDCKey: methodDCKey);

        // Create context for MoveNext emission
        var il = smBuilder.MoveNextMethod.GetILGenerator();
        var ctx = new CompilationContext(il, _typeMapper, _functions.Builders, _classes.Builders, _namespaceFields, _namespaceVarFields, _types)
        {
            FieldsField = fieldsField,
            IsInstanceMethod = true,
            ClosureAnalyzer = _closures.Analyzer,
            ArrowMethods = _closures.ArrowMethods,
            ConstArrowBindings = _closures.ConstArrowBindings,
            DisplayClasses = _closures.DisplayClasses,
            DisplayClassFields = _closures.DisplayClassFields,
            DisplayClassConstructors = _closures.DisplayClassConstructors,
            FunctionRestParams = _functions.RestParams,
            FunctionsCapturingArguments = _functions.CapturingArguments,
            EnumMembers = _enums.Members,
            EnumReverse = _enums.Reverse,
            EnumKinds = _enums.Kinds,
            TopLevelStaticVars = BuildTopLevelStaticVarsForModule(_modules.CurrentPath),
            Runtime = _runtime,
            FunctionGenericParams = _functions.GenericParams,
            IsGenericFunction = _functions.IsGeneric,
            TypeMap = _typeMap,
            DeadCode = _deadCodeInfo,
            AsyncMethods = null,
            AsyncArrowBuilders = _async.ArrowBuilders,
            AsyncArrowOuterBuilders = _async.ArrowOuterBuilders,
            AsyncArrowParentBuilders = _async.ArrowParentBuilders,
            // Module support for multi-module compilation
            CurrentModulePath = _modules.CurrentPath,
            CurrentNamespacePath = _currentNamespacePath,
            ClassToModule = _modules.ClassToModule,
            FunctionToModule = _modules.FunctionToModule,
            EnumToModule = _modules.EnumToModule,
            DotNetNamespace = _modules.CurrentDotNetNamespace,
            // @lock decorator support
            SyncLockFields = _locks.SyncLockFields,
            AsyncLockFields = _locks.AsyncLockFields,
            LockReentrancyFields = _locks.ReentrancyFields,
            StaticSyncLockFields = _locks.StaticSyncLockFields,
            StaticAsyncLockFields = _locks.StaticAsyncLockFields,
            StaticLockReentrancyFields = _locks.StaticReentrancyFields,
            TypeEmitterRegistry = _typeEmitterRegistry,
            BuiltInModuleEmitterRegistry = _builtInModuleEmitterRegistry,
            BuiltInModuleNamespaces = _builtInModuleNamespaces,
                BuiltInModuleMethodBindings = GetCurrentBuiltInMethodBindings(),
                ImportedNames = _importedNames,
            ClassExprBuilders = _classExprs.Builders,
            IsStrictMode = _isStrictMode,
            // ES2022 Private Class Elements support for async methods
            CurrentClassName = methodBuilder.DeclaringType?.Name,
            CurrentClassBuilder = methodBuilder.DeclaringType as TypeBuilder,
            // Registry services
            ClassRegistry = GetClassRegistry(),
            // Entry-point display class for captured top-level variables
            EntryPointDisplayClassFields = BuildEntryPointDisplayClassFieldsForModule(_modules.CurrentPath),
            CapturedTopLevelVars = BuildCapturedTopLevelVarsForModule(_modules.CurrentPath),
            ArrowEntryPointDCFields = _closures.ArrowEntryPointDCFields.Count > 0 ? _closures.ArrowEntryPointDCFields : null,
            EntryPointDisplayClassStaticField = _closures.EntryPointDisplayClassStaticField
        };

        // #682: route promoted captures through the method's function display class (shared by the
        // method body and its nested async arrows via this ctx).
        WireAsyncMethodFunctionDC(ctx, smBuilder, methodDCKey);

        // Emit MoveNext body
        // Note: @lock fields are stored directly in the state machine (AsyncLockRefField, LockReentrancyRefField)
        // so the emitter doesn't need external references to the class's lock fields
        var moveNextEmitter = new AsyncMoveNextEmitter(smBuilder, analysis, _types);
        moveNextEmitter.EmitMoveNext(method.Body, ctx, _types.Object, method.Parameters);
        ILLabelValidator.Validate(smBuilder.MoveNextMethod.GetILGenerator(),
            $"async method MoveNext {methodBuilder.DeclaringType?.Name}::{method.Name.Lexeme}");

        // Emit MoveNext bodies for async arrows. Delegate to the shared EmitAsyncArrowMoveNext (the
        // same path free async functions use) rather than emitting inline. The inline path reused this
        // METHOD's ctx — whose `IL` points at the method's MoveNext, not the arrow's — so any emitter
        // strategy that emits via `ctx.IL` (e.g. Promise.resolve) wrote into the method's IL stream
        // after its `ret`, producing invalid IL (InvalidProgramException for any suspending arrow in an
        // async method). EmitAsyncArrowMoveNext builds a fresh per-arrow ctx whose `IL` is the arrow's,
        // restoring the `ctx.IL == emitter.IL` invariant the strategies rely on, and it propagates the
        // #682 function-DC fields we set on `ctx` above.
        foreach (var arrowInfo in analysis.AsyncArrows)
        {
            if (_async.ArrowBuilders.TryGetValue(arrowInfo.Arrow, out var arrowBuilder))
            {
                EmitAsyncArrowMoveNext(arrowBuilder, arrowInfo.Arrow, ctx);
            }
        }

        // Finalize async arrow state machine types
        foreach (var arrowInfo in analysis.AsyncArrows)
        {
            if (_async.ArrowBuilders.TryGetValue(arrowInfo.Arrow, out var arrowBuilder))
            {
                arrowBuilder.CreateType();
            }
        }

        // Finalize state machine type
        smBuilder.CreateType();
    }

    /// <summary>
    /// Defines state machines for top-level async arrow functions (not inside any async function).
    /// These are standalone async arrows that don't have an outer state machine context.
    /// </summary>
    private void DefineTopLevelAsyncArrows()
    {
        // Find all async arrows that were collected but not processed by DefineAsyncArrowStateMachines
        foreach (var (arrow, captures) in _collectedArrows)
        {
            // Only process async arrows that aren't already handled
            if (!arrow.IsAsync || _async.ArrowBuilders.ContainsKey(arrow))
            {
                continue;
            }

            // #721: an async arrow whose enclosing async function/method builds an async state machine
            // is given its real (nested) builder by that machine's phase-6 emission — but only AFTER
            // this earlier pass runs. Free async functions register their arrows before this pass (the
            // ContainsKey check above already skips those); class async methods do not, so a standalone
            // builder defined here would be a dead, never-invoked duplicate of the nested one. Skip an
            // arrow that an enclosing async state machine will claim; an arrow behind a sync arrow/method,
            // or a genuinely top-level one, is not claimed and still needs the standalone builder.
            if (IsClaimedByEnclosingAsyncStateMachine(arrow))
            {
                continue;
            }

            // Analyze this arrow for await points and hoisted locals
            var arrowAnalysis = AnalyzeAsyncArrow(arrow);

            // Create a standalone async arrow builder
            var arrowBuilder = new AsyncArrowStateMachineBuilder(
                _moduleBuilder,
                _types,
                arrow,
                captures,
                _async.ArrowCounter++);

            // Define standalone state machine (no outer reference)
            arrowBuilder.DefineStateMachineStandalone(
                arrowAnalysis.AwaitCount,
                arrow.Parameters,
                arrowAnalysis.HoistedLocals);

            // Define the stub method
            arrowBuilder.DefineStubMethod(_programType);
            MarkPadsUndefined(arrowBuilder.StubMethod); // #640

            // Store the builder
            _async.ArrowBuilders[arrow] = arrowBuilder;
        }
    }

    /// <summary>
    /// True when <paramref name="arrow"/> will be given a nested async state machine by an enclosing
    /// async function/method's phase-6 emission (<see cref="DefineAsyncArrowStateMachines"/>), so this
    /// pass must not also create a standalone (dead-duplicate) builder for it (#721). An async function's
    /// state machine claims its async arrow descendants reachable through a chain of <em>async</em>
    /// arrows; a sync arrow (or sync function) on the way up breaks that chain — arrows behind one get
    /// their own standalone builder instead. Walks the enclosing-callable chain to decide.
    /// </summary>
    private bool IsClaimedByEnclosingAsyncStateMachine(Expr.ArrowFunction arrow)
    {
        object? enclosing = _arrowEnclosingCallable.GetValueOrDefault(arrow);
        while (enclosing is Expr.ArrowFunction parentArrow)
        {
            if (!parentArrow.IsAsync)
                return false; // a sync arrow does not build a state machine to claim descendants
            enclosing = _arrowEnclosingCallable.GetValueOrDefault(parentArrow);
        }
        return enclosing is Stmt.Function { IsAsync: true };
    }

    /// <summary>
    /// Emits MoveNext bodies for top-level async arrows.
    /// </summary>
    private void EmitTopLevelAsyncArrowBodies()
    {
        var savedPath = _modules.CurrentPath;
        foreach (var (arrow, arrowBuilder) in _async.ArrowBuilders)
        {
            // Only process standalone arrows (not nested in async functions)
            if (!arrowBuilder.IsStandalone)
            {
                continue;
            }

            if (_arrowToModule.TryGetValue(arrow, out var arrowModule))
            {
                _modules.CurrentPath = NormalizeToEmissionPath(arrowModule);
            }

            // Create IL generator for the arrow's MoveNext
            var il = arrowBuilder.MoveNextMethod.GetILGenerator();

            // Create analysis for this arrow
            var arrowAnalysis = AnalyzeAsyncArrow(arrow);
            var analysis = new AsyncStateAnalyzer.AsyncFunctionAnalysis(
                arrowAnalysis.AwaitCount,
                [], // Await points are regenerated during emission
                arrowAnalysis.HoistedLocals,
                new HashSet<string>(arrow.Parameters.Select(p => p.Name.Lexeme)),
                false, // HasTryCatch - detected during emission
                false, // UsesThis - standalone arrows don't have 'this' binding by default
                []     // No nested async arrows handled in this pass
            );

            // Determine if this arrow is inside a class (for private field access)
            string? enclosingClassName = null;
            TypeBuilder? enclosingClassBuilder = null;
            if (_async.ArrowEnclosingClassNames.TryGetValue(arrow, out var className))
            {
                // Resolve qualified class name (same as EmitMethod uses)
                enclosingClassName = _modules.CurrentDotNetNamespace != null
                    ? $"{_modules.CurrentDotNetNamespace}.{className}"
                    : _modules.ClassToModule.TryGetValue(className, out var modulePath)
                        ? $"$M_{System.IO.Path.GetFileNameWithoutExtension(modulePath)}_{className}"
                        : className;
                _classes.Builders.TryGetValue(enclosingClassName, out enclosingClassBuilder);
            }

            // Create context for MoveNext emission
            var ctx = new CompilationContext(il, _typeMapper, _functions.Builders, _classes.Builders, _namespaceFields, _namespaceVarFields, _types)
            {
                Runtime = _runtime,
                ClosureAnalyzer = _closures.Analyzer,
                ArrowMethods = _closures.ArrowMethods,
            ConstArrowBindings = _closures.ConstArrowBindings,
                DisplayClasses = _closures.DisplayClasses,
                DisplayClassFields = _closures.DisplayClassFields,
                DisplayClassConstructors = _closures.DisplayClassConstructors,
                EnumMembers = _enums.Members,
                EnumReverse = _enums.Reverse,
                EnumKinds = _enums.Kinds,
                TopLevelStaticVars = BuildTopLevelStaticVarsForModule(_modules.CurrentPath),
                FunctionRestParams = _functions.RestParams,
                FunctionsCapturingArguments = _functions.CapturingArguments,
                FunctionGenericParams = _functions.GenericParams,
                IsGenericFunction = _functions.IsGeneric,
                TypeMap = _typeMap,
                DeadCode = _deadCodeInfo,
                AsyncMethods = null,
                AsyncArrowBuilders = _async.ArrowBuilders,
                AsyncArrowOuterBuilders = _async.ArrowOuterBuilders,
                AsyncArrowParentBuilders = _async.ArrowParentBuilders,
                CurrentModulePath = _modules.CurrentPath,
                CurrentNamespacePath = _currentNamespacePath,
                ClassToModule = _modules.ClassToModule,
                FunctionToModule = _modules.FunctionToModule,
                EnumToModule = _modules.EnumToModule,
                DotNetNamespace = _modules.CurrentDotNetNamespace,
                TypeEmitterRegistry = _typeEmitterRegistry,
                BuiltInModuleEmitterRegistry = _builtInModuleEmitterRegistry,
                BuiltInModuleNamespaces = _builtInModuleNamespaces,
                BuiltInModuleMethodBindings = GetCurrentBuiltInMethodBindings(),
                ImportedNames = _importedNames,
                ClassExprBuilders = _classExprs.Builders,
                IsStrictMode = _isStrictMode,
                // ES2022 Private Class Elements support
                CurrentClassName = enclosingClassName,
                CurrentClassBuilder = enclosingClassBuilder,
                ClassRegistry = GetClassRegistry(),
                EntryPointDisplayClassFields = BuildEntryPointDisplayClassFieldsForModule(_modules.CurrentPath),
                CapturedTopLevelVars = BuildCapturedTopLevelVarsForModule(_modules.CurrentPath),
                ArrowEntryPointDCFields = _closures.ArrowEntryPointDCFields.Count > 0 ? _closures.ArrowEntryPointDCFields : null,
                EntryPointDisplayClassStaticField = _closures.EntryPointDisplayClassStaticField
            };

            // Create arrow-specific emitter
            var arrowEmitter = new AsyncArrowMoveNextEmitter(arrowBuilder, analysis, _types);

            // Get the body statements
            List<Stmt> bodyStatements;
            if (arrow.BlockBody != null)
            {
                bodyStatements = arrow.BlockBody;
            }
            else if (arrow.ExpressionBody != null)
            {
                // Create a synthetic return statement for expression body arrows
                var returnToken = new Token(TokenType.RETURN, "return", null, 0);
                bodyStatements = [new Stmt.Return(returnToken, arrow.ExpressionBody)];
            }
            else
            {
                bodyStatements = [];
            }

            arrowEmitter.EmitMoveNext(bodyStatements, ctx, _types.Object, arrow.Parameters);
            ILLabelValidator.Validate(arrowBuilder.MoveNextMethod.GetILGenerator(),
                $"standalone async arrow MoveNext");

            // Finalize the type
            arrowBuilder.CreateType();
        }
        _modules.CurrentPath = savedPath;
    }
}
