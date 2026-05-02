using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// Arrow function collection and emission methods for the IL compiler.
/// </summary>
public partial class ILCompiler
{
    private readonly List<(Expr.ArrowFunction Arrow, HashSet<string> Captures)> _collectedArrows = [];
    // Maps each collected arrow to the module path that owns it (null = script/single-file
    // scope). Set during collection so body emission can restore the correct
    // per-module view of TopLevelStaticVars / captured-top-level-var fields —
    // body emission otherwise runs with a stale _modules.CurrentPath and
    // resolves against the wrong module's storage.
    private readonly Dictionary<Expr.ArrowFunction, string?> _arrowToModule = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Expr.ArrowFunction, Expr.ArrowFunction?> _arrowParent = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Expr.ArrowFunction> _arrowsNeedingFunctionDC = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Expr.ArrowFunction> _arrowsNeedingArrowDC = new(ReferenceEqualityComparer.Instance);
    // Maps each collected arrow (or `function` expression) to the AST node of
    // the top-level function that lexically contains it. Needed so
    // PropagateFunctionDCRequirements matches `$functionDC` field lookups
    // against the CORRECT enclosing function — not just any function whose
    // display class happens to have a field with a matching name. Before
    // this was tracked, two sibling module-level functions with a same-named
    // parameter (e.g. both taking `fn`) aliased onto whichever enclosing
    // function's display class was enumerated first, causing the later
    // function's captured param to read back null at runtime.
    //
    // We store the AST node rather than a qualified name because
    // CollectAndDefineArrowFunctions runs after module context is cleared, so
    // name qualification isn't available here. The AST node is stable; we
    // resolve it to the qualified name at propagate time via FunctionAstNodes.
    private readonly Dictionary<Expr.ArrowFunction, Stmt.Function> _arrowEnclosingFunction = new(ReferenceEqualityComparer.Instance);
    private Stmt.Function? _currentEnclosingFunctionStmt;
    private Expr.ArrowFunction? _currentParentArrow;
    private string? _currentCollectClassName;

    private void CollectAndDefineArrowFunctions(List<Stmt> statements)
    {
        CollectArrowsFromStatementsInCurrentModule(statements);
        FinalizeArrowFunctionCollection();
    }

    /// <summary>
    /// Walks statements under the current module context to populate
    /// <see cref="_collectedArrows"/> and the arrow→module map. Module compilation
    /// calls this once per module (with <c>_modules.CurrentPath</c> set);
    /// single-file compilation calls it once with the path unset.
    /// </summary>
    private void CollectArrowsFromStatementsInCurrentModule(List<Stmt> statements)
    {
        foreach (var stmt in statements)
        {
            CollectArrowsFromStmt(stmt);
        }
    }

    /// <summary>
    /// Runs the cross-module finalization pass — propagates DC requirements,
    /// defines scope display classes, and defines per-arrow methods/display classes.
    /// Must run AFTER every module has been walked via
    /// <see cref="CollectArrowsFromStatementsInCurrentModule"/>.
    /// </summary>
    private void FinalizeArrowFunctionCollection()
    {
        // Propagate function DC requirements through arrow nesting
        // If an inner arrow needs $functionDC, parent arrows also need it
        PropagateFunctionDCRequirements();

        // Define arrow scope display classes (for arrow-local vars captured by nested arrows)
        // Skip async arrows - their bodies are emitted by async state machine emitters,
        // which don't support arrow scope DC instantiation
        foreach (var (arrow, _) in _collectedArrows)
        {
            if (!arrow.IsAsync)
            {
                DefineArrowScopeDisplayClass(arrow);
            }
        }

        // Propagate arrow scope DC requirements through arrow nesting
        PropagateArrowDCRequirements();

        // Define methods and display classes
        foreach (var (arrow, captures) in _collectedArrows)
        {
            // Skip async arrows - they're handled via DefineTopLevelAsyncArrows() or
            // DefineAsyncArrowStateMachines() (if inside an async function)
            if (arrow.IsAsync)
            {
                continue;
            }

            // Resolve typed parameters from annotations (optimization for numeric parameters)
            // Rest parameters always use List<object> to enable detection at invoke time
            var resolvedParamTypes = ParameterTypeResolver.ResolveParameters(
                arrow.Parameters, _typeMapper, null); // null funcType - use annotations only

            // Store resolved types for use during arrow body emission
            _closures.ArrowParameterTypes[arrow] = resolvedParamTypes;

            // Resolve typed return type from TypeMap (optimization for typed returns)
            // Arrow functions can use typed returns - reflection automatically boxes at the boundary.
            // However, we must be careful with expression body arrows that produce void (like console.log).
            Type returnType = _types.Object;
            var arrowTypeInfo = _typeMap?.Get(arrow);
            if (arrowTypeInfo is TypeSystem.TypeInfo.Function funcTypeInfo)
            {
                var logicalReturnType = ParameterTypeResolver.ResolveReturnType(
                    funcTypeInfo.ReturnType, isAsync: false, _typeMapper);

                // Use typed return if:
                // 1. Return type is not void (void methods need special handling)
                // 2. For expression body arrows, the expression must produce a value
                if (logicalReturnType != typeof(void))
                {
                    // For expression body arrows, check if the expression produces void
                    // (like console.log) - in that case, keep return type as object
                    bool isVoidExpressionBody = false;
                    if (arrow.ExpressionBody != null)
                    {
                        var exprType = _typeMap?.Get(arrow.ExpressionBody);
                        isVoidExpressionBody = exprType is TypeSystem.TypeInfo.Void;
                    }

                    if (!isVoidExpressionBody)
                    {
                        returnType = logicalReturnType;
                    }
                }
            }
            _closures.ArrowReturnTypes[arrow] = returnType;

            // For object methods, add __this as the first parameter
            Type[] paramTypes;
            if (arrow.HasOwnThis)
            {
                paramTypes = new Type[arrow.Parameters.Count + 1];
                paramTypes[0] = _types.Object;  // __this
                for (int i = 0; i < arrow.Parameters.Count; i++)
                    paramTypes[i + 1] = arrow.Parameters[i].IsRest ? _types.ListOfObject : resolvedParamTypes[i];
            }
            else
            {
                paramTypes = new Type[arrow.Parameters.Count];
                for (int i = 0; i < arrow.Parameters.Count; i++)
                    paramTypes[i] = arrow.Parameters[i].IsRest ? _types.ListOfObject : resolvedParamTypes[i];
            }

            // Check if arrow needs function DC or arrow DC (for itself or to pass to inner arrows)
            bool needsFunctionDCForArrow = _arrowsNeedingFunctionDC.Contains(arrow);
            bool needsArrowDCForArrow = _arrowsNeedingArrowDC.Contains(arrow);

            if (captures.Count == 0 && !needsFunctionDCForArrow && !needsArrowDCForArrow)
            {
                // Non-capturing and doesn't need function DC: static method on $Program
                // Use typed return when available - reflection automatically boxes at $TSFunction boundary
                // Assembly (internal) so iterator-helper fast-path emitters
                // in $Module_X types can `ldftn` directly into these arrow
                // bodies on $Program. Synthetic methods (compiler-generated
                // names like `<>Arrow_N`); access modifier is an internal
                // detail with no observable JS semantics.
                var methodBuilder = _programType.DefineMethod(
                    $"<>Arrow_{_closures.ArrowMethodCounter++}",
                    MethodAttributes.Assembly | MethodAttributes.Static,
                    returnType,
                    paramTypes
                );

                // Define parameter names (important for InvokeWithThis to detect __this)
                if (arrow.HasOwnThis)
                {
                    methodBuilder.DefineParameter(1, ParameterAttributes.None, "__this");
                    for (int i = 0; i < arrow.Parameters.Count; i++)
                        methodBuilder.DefineParameter(i + 2, ParameterAttributes.None, arrow.Parameters[i].Name.Lexeme);
                }
                else
                {
                    for (int i = 0; i < arrow.Parameters.Count; i++)
                        methodBuilder.DefineParameter(i + 1, ParameterAttributes.None, arrow.Parameters[i].Name.Lexeme);
                }

                _closures.ArrowMethods[arrow] = methodBuilder;
            }
            else
            {
                // Capturing: create display class
                var displayClass = _moduleBuilder.DefineType(
                    $"<>c__DisplayClass{_closures.DisplayClassCounter++}",
                    TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                    _types.Object
                );

                // Determine if any captured vars are top-level captured vars
                bool needsEntryPointDC = _closures.EntryPointDisplayClass != null &&
                    captures.Any(c => _closures.CapturedTopLevelVars.Contains(c));

                // Check if this arrow needs function DC (either directly or to propagate to inner arrows)
                bool needsFunctionDC = _arrowsNeedingFunctionDC.Contains(arrow);
                string? sourceFunction = needsFunctionDC && _closures.ArrowFunctionDCSource.TryGetValue(arrow, out var src) ? src : null;

                // Check if this arrow needs arrow scope DC
                bool needsArrowDC = _arrowsNeedingArrowDC.Contains(arrow);
                Expr.ArrowFunction? sourceArrow = needsArrowDC && _closures.ArrowScopeDCSource.TryGetValue(arrow, out var srcArrow) ? srcArrow : null;

                // Add fields for captured variables (except top-level, function-level, and arrow-scope captured vars)
                Dictionary<string, FieldBuilder> fieldMap = [];
                FieldBuilder? entryPointDCField = null;
                FieldBuilder? functionDCField = null;
                FieldBuilder? arrowDCField = null;

                if (needsEntryPointDC)
                {
                    // Add field to hold reference to entry-point display class
                    entryPointDCField = displayClass.DefineField("$entryPointDC", _closures.EntryPointDisplayClass!, FieldAttributes.Public);
                }

                if (needsFunctionDC && sourceFunction != null && _closures.FunctionDisplayClasses.TryGetValue(sourceFunction, out var funcDC))
                {
                    // Add field to hold reference to function display class
                    functionDCField = displayClass.DefineField("$functionDC", funcDC, FieldAttributes.Public);
                }

                if (needsArrowDC && sourceArrow != null && _closures.ArrowScopeDisplayClasses.TryGetValue(sourceArrow, out var arrowScopeDC))
                {
                    // Add field to hold reference to parent arrow scope display class
                    arrowDCField = displayClass.DefineField("$arrowDC", arrowScopeDC, FieldAttributes.Public);
                }

                foreach (var capturedVar in captures)
                {
                    // Skip top-level captured vars - they'll be accessed through $entryPointDC
                    if (_closures.CapturedTopLevelVars.Contains(capturedVar))
                        continue;

                    // Skip function-level captured vars - they'll be accessed through $functionDC.
                    // But only skip if the function DC is from a SYNC function (always populated).
                    // For ASYNC functions, the arrow might be nested inside an async arrow where
                    // $functionDC won't be populated, so keep the arrow's own field as fallback.
                    if (needsFunctionDC && sourceFunction != null &&
                        _closures.FunctionDisplayClassFields.TryGetValue(sourceFunction, out var funcFields) &&
                        funcFields.ContainsKey(capturedVar) &&
                        !_async.StateMachines.ContainsKey(sourceFunction))
                        continue;

                    // Skip arrow-scope captured vars - they'll be accessed through $arrowDC
                    if (needsArrowDC && sourceArrow != null &&
                        _closures.ArrowScopeDisplayClassFields.TryGetValue(sourceArrow, out var arrowFields) &&
                        arrowFields.ContainsKey(capturedVar))
                        continue;

                    var field = displayClass.DefineField(capturedVar, _types.Object, FieldAttributes.Public);
                    fieldMap[capturedVar] = field;
                }
                _closures.DisplayClassFields[arrow] = fieldMap;

                // Track $entryPointDC field for this arrow
                if (entryPointDCField != null)
                {
                    _closures.ArrowEntryPointDCFields[arrow] = entryPointDCField;
                }

                // Track $functionDC field for this arrow
                if (functionDCField != null)
                {
                    _closures.ArrowFunctionDCFields[arrow] = functionDCField;
                }

                // Track $arrowDC field for this arrow
                if (arrowDCField != null)
                {
                    _closures.ArrowScopeDCFields[arrow] = arrowDCField;
                }

                // Add default constructor
                var ctorBuilder = displayClass.DefineConstructor(
                    MethodAttributes.Public,
                    CallingConventions.Standard,
                    Type.EmptyTypes
                );
                var ctorIL = ctorBuilder.GetILGenerator();
                ctorIL.Emit(OpCodes.Ldarg_0);
                ctorIL.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.Object));
                ctorIL.Emit(OpCodes.Ret);
                _closures.DisplayClassConstructors[arrow] = ctorBuilder;

                // Add Invoke method
                // Use typed return when available - reflection automatically boxes at $TSFunction boundary
                var invokeMethod = displayClass.DefineMethod(
                    "Invoke",
                    MethodAttributes.Public,
                    returnType,
                    paramTypes
                );

                // Define parameter names (important for InvokeWithThis to detect __this)
                if (arrow.HasOwnThis)
                {
                    invokeMethod.DefineParameter(1, ParameterAttributes.None, "__this");
                    for (int i = 0; i < arrow.Parameters.Count; i++)
                        invokeMethod.DefineParameter(i + 2, ParameterAttributes.None, arrow.Parameters[i].Name.Lexeme);
                }
                else
                {
                    for (int i = 0; i < arrow.Parameters.Count; i++)
                        invokeMethod.DefineParameter(i + 1, ParameterAttributes.None, arrow.Parameters[i].Name.Lexeme);
                }

                _closures.DisplayClasses[arrow] = displayClass;
                _closures.ArrowMethods[arrow] = invokeMethod;
            }
        }
    }

    private void CollectArrowsFromStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case Stmt.Expression e:
                CollectArrowsFromExpr(e.Expr);
                break;
            case Stmt.Var v:
                if (v.Initializer != null)
                {
                    // If initializing with a class expression, track variable name → class expr mapping
                    if (v.Initializer is Expr.ClassExpr classExpr)
                    {
                        _classExprs.VarToClassExpr[v.Name.Lexeme] = classExpr;
                    }
                    CollectArrowsFromExpr(v.Initializer);
                }
                break;
            case Stmt.Const c:
                // If initializing with a class expression, track variable name → class expr mapping
                if (c.Initializer is Expr.ClassExpr classExprConst)
                {
                    _classExprs.VarToClassExpr[c.Name.Lexeme] = classExprConst;
                }
                CollectArrowsFromExpr(c.Initializer);
                break;
            case Stmt.Function f:
                // Skip overload signatures (no body)
                if (f.Body != null)
                {
                    // Track inner function declarations (nested inside another function)
                    if (_functionNestingDepth > 0)
                    {
                        CollectInnerFunction(f);
                    }

                    var previousEnclosing = _currentEnclosingFunctionName;
                    var previousEnclosingStmt = _currentEnclosingFunctionStmt;
                    if (_functionNestingDepth == 0)
                    {
                        // Top-level function: use its qualified name as enclosing context
                        _currentEnclosingFunctionName = GetDefinitionContext().GetQualifiedFunctionName(f.Name.Lexeme);
                        _currentEnclosingFunctionStmt = f;
                    }

                    _functionNestingDepth++;
                    foreach (var s in f.Body)
                        CollectArrowsFromStmt(s);
                    _functionNestingDepth--;

                    _currentEnclosingFunctionName = previousEnclosing;
                    _currentEnclosingFunctionStmt = previousEnclosingStmt;
                }
                foreach (var p in f.Parameters)
                    if (p.DefaultValue != null)
                        CollectArrowsFromExpr(p.DefaultValue);
                break;
            case Stmt.Class c:
                var previousClassName = _currentCollectClassName;
                _currentCollectClassName = c.Name.Lexeme;
                foreach (var method in c.Methods)
                {
                    // Skip overload signatures (no body)
                    if (method.Body != null)
                        CollectArrowsFromStmt(method);
                }
                _currentCollectClassName = previousClassName;
                break;
            case Stmt.If i:
                CollectArrowsFromExpr(i.Condition);
                CollectArrowsFromStmt(i.ThenBranch);
                if (i.ElseBranch != null)
                    CollectArrowsFromStmt(i.ElseBranch);
                break;
            case Stmt.While w:
                CollectArrowsFromExpr(w.Condition);
                CollectArrowsFromStmt(w.Body);
                break;
            case Stmt.For f:
                if (f.Initializer != null)
                    CollectArrowsFromStmt(f.Initializer);
                if (f.Condition != null)
                    CollectArrowsFromExpr(f.Condition);
                if (f.Increment != null)
                    CollectArrowsFromExpr(f.Increment);
                CollectArrowsFromStmt(f.Body);
                break;
            case Stmt.ForOf forOf:
                CollectArrowsFromExpr(forOf.Iterable);
                CollectArrowsFromStmt(forOf.Body);
                break;
            case Stmt.ForIn forIn:
                CollectArrowsFromExpr(forIn.Object);
                CollectArrowsFromStmt(forIn.Body);
                break;
            case Stmt.DoWhile dw:
                CollectArrowsFromStmt(dw.Body);
                CollectArrowsFromExpr(dw.Condition);
                break;
            case Stmt.LabeledStatement ls:
                CollectArrowsFromStmt(ls.Statement);
                break;
            case Stmt.StaticBlock sb:
                foreach (var s in sb.Body)
                    CollectArrowsFromStmt(s);
                break;
            case Stmt.Block b:
                foreach (var s in b.Statements)
                    CollectArrowsFromStmt(s);
                break;
            case Stmt.Sequence seq:
                foreach (var s in seq.Statements)
                    CollectArrowsFromStmt(s);
                break;
            case Stmt.Return r:
                if (r.Value != null)
                    CollectArrowsFromExpr(r.Value);
                break;
            case Stmt.Switch s:
                CollectArrowsFromExpr(s.Subject);
                foreach (var c in s.Cases)
                {
                    CollectArrowsFromExpr(c.Value);
                    foreach (var cs in c.Body)
                        CollectArrowsFromStmt(cs);
                }
                if (s.DefaultBody != null)
                    foreach (var ds in s.DefaultBody)
                        CollectArrowsFromStmt(ds);
                break;
            case Stmt.TryCatch t:
                foreach (var ts in t.TryBlock)
                    CollectArrowsFromStmt(ts);
                if (t.CatchBlock != null)
                    foreach (var cs in t.CatchBlock)
                        CollectArrowsFromStmt(cs);
                if (t.FinallyBlock != null)
                    foreach (var fs in t.FinallyBlock)
                        CollectArrowsFromStmt(fs);
                break;
            case Stmt.Throw th:
                CollectArrowsFromExpr(th.Value);
                break;
            case Stmt.Print p:
                CollectArrowsFromExpr(p.Expr);
                break;
            case Stmt.Using u:
                foreach (var binding in u.Bindings)
                    CollectArrowsFromExpr(binding.Initializer);
                break;
            case Stmt.Export exp:
                // Recurse into the wrapped declaration so arrows inside
                // `export const X = () => …` or `export function …` get
                // collected. Without this the arrow's method never gets
                // registered in _collectedArrows and EmitArrowFunction
                // silently emits `ldnull`, leaving the export field null.
                if (exp.Declaration != null)
                    CollectArrowsFromStmt(exp.Declaration);
                if (exp.DefaultExpr != null)
                    CollectArrowsFromExpr(exp.DefaultExpr);
                if (exp.ExportAssignment != null)
                    CollectArrowsFromExpr(exp.ExportAssignment);
                break;
        }
    }

    private void CollectArrowsFromExpr(Expr expr)
    {
        switch (expr)
        {
            case Expr.ArrowFunction af:
                var captures = _closures.Analyzer.GetCaptures(af);
                _collectedArrows.Add((af, captures));
                // Only record a module mapping when collection is under a real
                // module context. Single-file compile runs without setting
                // _modules.CurrentPath, and entering a null key would cause
                // body emission to overwrite the path with null, dropping any
                // outer context the caller set up.
                if (_modules.CurrentPath != null)
                {
                    _arrowToModule[af] = _modules.CurrentPath;
                }

                // Track enclosing class name for arrows that need class-scoped dispatch:
                // private method / private field access, `super` calls, etc. Recorded for
                // every arrow (sync + async) so EmitArrowBody can propagate it to the
                // compilation context — without this, a private method call inside an
                // arrow throws "class context not available" at runtime. Stored as the
                // QUALIFIED class name because the ClassRegistry keys private methods
                // by qualified name (simple names like "AST" miss the `$M_index_AST` key).
                if (_currentCollectClassName != null)
                {
                    _async.ArrowEnclosingClassNames[af] = GetDefinitionContext().GetQualifiedClassName(_currentCollectClassName);
                }

                // Track the immediately enclosing top-level function so we can
                // match `$functionDC` requests to the right display class
                // during PropagateFunctionDCRequirements.
                if (_currentEnclosingFunctionStmt != null)
                {
                    _arrowEnclosingFunction[af] = _currentEnclosingFunctionStmt;
                }

                // Track parent arrow for function DC propagation
                _arrowParent[af] = _currentParentArrow;
                var previousParent = _currentParentArrow;
                _currentParentArrow = af;

                // Entering an arrow body is the same as entering a function body for the
                // purpose of "inner function declaration" classification: `function X() {}`
                // inside an arrow is a nested declaration that should be hoisted into the
                // arrow's scope. Without incrementing the depth, CollectArrowsFromStmt's
                // `Stmt.Function f` case skips `CollectInnerFunction` (it only fires when
                // depth > 0), and the declaration never gets a MethodBuilder — references
                // to it inside the arrow then fall through to "Undefined variable" at
                // runtime. Real-world case: lodash's IIFE `(function() { ... })()` wraps
                // ~5000 lines of nested `function basePropertyOf(...)` declarations.
                _functionNestingDepth++;
                if (af.ExpressionBody != null)
                    CollectArrowsFromExpr(af.ExpressionBody);
                if (af.BlockBody != null)
                    foreach (var s in af.BlockBody)
                        CollectArrowsFromStmt(s);
                _functionNestingDepth--;

                _currentParentArrow = previousParent;
                break;
            case Expr.Binary b:
                CollectArrowsFromExpr(b.Left);
                CollectArrowsFromExpr(b.Right);
                break;
            case Expr.Logical l:
                CollectArrowsFromExpr(l.Left);
                CollectArrowsFromExpr(l.Right);
                break;
            case Expr.Unary u:
                CollectArrowsFromExpr(u.Right);
                break;
            case Expr.Delete d:
                CollectArrowsFromExpr(d.Operand);
                break;
            case Expr.Grouping g:
                CollectArrowsFromExpr(g.Expression);
                break;
            case Expr.Call c:
                CollectArrowsFromExpr(c.Callee);
                foreach (var arg in c.Arguments)
                    CollectArrowsFromExpr(arg);
                break;
            case Expr.Get g:
                CollectArrowsFromExpr(g.Object);
                break;
            case Expr.Set s:
                CollectArrowsFromExpr(s.Object);
                CollectArrowsFromExpr(s.Value);
                break;
            case Expr.GetIndex gi:
                CollectArrowsFromExpr(gi.Object);
                CollectArrowsFromExpr(gi.Index);
                break;
            case Expr.SetIndex si:
                CollectArrowsFromExpr(si.Object);
                CollectArrowsFromExpr(si.Index);
                CollectArrowsFromExpr(si.Value);
                break;
            case Expr.Assign a:
                CollectArrowsFromExpr(a.Value);
                break;
            case Expr.New n:
                foreach (var arg in n.Arguments)
                    CollectArrowsFromExpr(arg);
                break;
            case Expr.ArrayLiteral a:
                foreach (var elem in a.Elements)
                    CollectArrowsFromExpr(elem);
                break;
            case Expr.ObjectLiteral o:
                foreach (var prop in o.Properties)
                    CollectArrowsFromExpr(prop.Value);
                break;
            case Expr.Ternary t:
                CollectArrowsFromExpr(t.Condition);
                CollectArrowsFromExpr(t.ThenBranch);
                CollectArrowsFromExpr(t.ElseBranch);
                break;
            case Expr.NullishCoalescing nc:
                CollectArrowsFromExpr(nc.Left);
                CollectArrowsFromExpr(nc.Right);
                break;
            case Expr.TemplateLiteral tl:
                foreach (var e in tl.Expressions)
                    CollectArrowsFromExpr(e);
                break;
            case Expr.TaggedTemplateLiteral ttl:
                CollectArrowsFromExpr(ttl.Tag);
                foreach (var e in ttl.Expressions)
                    CollectArrowsFromExpr(e);
                break;
            case Expr.CompoundAssign ca:
                CollectArrowsFromExpr(ca.Value);
                break;
            case Expr.CompoundSet cs:
                CollectArrowsFromExpr(cs.Object);
                CollectArrowsFromExpr(cs.Value);
                break;
            case Expr.CompoundSetIndex csi:
                CollectArrowsFromExpr(csi.Object);
                CollectArrowsFromExpr(csi.Index);
                CollectArrowsFromExpr(csi.Value);
                break;
            case Expr.PrefixIncrement pi:
                CollectArrowsFromExpr(pi.Operand);
                break;
            case Expr.PostfixIncrement poi:
                CollectArrowsFromExpr(poi.Operand);
                break;
            case Expr.Await aw:
                CollectArrowsFromExpr(aw.Expression);
                break;
            case Expr.DynamicImport di:
                CollectArrowsFromExpr(di.PathExpression);
                break;
            case Expr.TypeAssertion ta:
                CollectArrowsFromExpr(ta.Expression);
                break;
            case Expr.Satisfies sat:
                CollectArrowsFromExpr(sat.Expression);
                break;
            case Expr.NonNullAssertion nna:
                CollectArrowsFromExpr(nna.Expression);
                break;
            case Expr.Spread sp:
                CollectArrowsFromExpr(sp.Expression);
                break;
            case Expr.Comma cm:
                CollectArrowsFromExpr(cm.Left);
                CollectArrowsFromExpr(cm.Right);
                break;
            case Expr.LogicalAssign la:
                CollectArrowsFromExpr(la.Value);
                break;
            case Expr.LogicalSet lsObj:
                CollectArrowsFromExpr(lsObj.Object);
                CollectArrowsFromExpr(lsObj.Value);
                break;
            case Expr.LogicalSetIndex lsi:
                CollectArrowsFromExpr(lsi.Object);
                CollectArrowsFromExpr(lsi.Index);
                CollectArrowsFromExpr(lsi.Value);
                break;
            case Expr.CallPrivate cp:
                CollectArrowsFromExpr(cp.Object);
                foreach (var arg in cp.Arguments)
                    CollectArrowsFromExpr(arg);
                break;
            case Expr.GetPrivate gp:
                CollectArrowsFromExpr(gp.Object);
                break;
            case Expr.SetPrivate sp2:
                CollectArrowsFromExpr(sp2.Object);
                CollectArrowsFromExpr(sp2.Value);
                break;
            case Expr.ClassExpr ce:
                // Collect the class expression for later definition
                CollectClassExpression(ce);
                // Also collect arrows inside class expression methods
                foreach (var method in ce.Methods)
                    if (method.Body != null)
                        foreach (var s in method.Body)
                            CollectArrowsFromStmt(s);
                // Collect arrows in field initializers
                foreach (var field in ce.Fields)
                    if (field.Initializer != null)
                        CollectArrowsFromExpr(field.Initializer);
                // Collect arrows in accessor bodies
                if (ce.Accessors != null)
                    foreach (var accessor in ce.Accessors)
                        foreach (var s in accessor.Body)
                            CollectArrowsFromStmt(s);
                break;
        }
    }

    /// <summary>
    /// Collects a class expression for later type definition.
    /// </summary>
    private void CollectClassExpression(Expr.ClassExpr classExpr)
    {
        if (_classExprs.Names.ContainsKey(classExpr))
            return; // Already collected

        // Generate unique name
        string className = classExpr.Name?.Lexeme ?? $"$ClassExpr_{++_classExprs.Counter}";
        _classExprs.Names[classExpr] = className;
        _classExprs.ToDefine.Add(classExpr);
    }

    /// <summary>
    /// Propagates function display class requirements through arrow nesting.
    /// If an inner arrow needs $functionDC, its parent arrows also need it to pass it through.
    /// </summary>
    private void PropagateFunctionDCRequirements()
    {
        // First, identify arrows that directly need $functionDC.
        //
        // An arrow needs $functionDC when it captures a variable that lives on
        // its enclosing top-level function's display class. We resolve the
        // enclosing function from `_arrowEnclosingFunction` (populated at
        // collection time) rather than iterating every registered display
        // class by name — the old "match on field name" heuristic aliased
        // same-named parameters across sibling module-level functions, so a
        // closure would read from another function's uninitialized display
        // class field and blow up with NullReferenceException at runtime.
        // Resolve the enclosing AST node back to the qualified function name
        // by scanning FunctionAstNodes (populated during DefineFunction per
        // module). This pair is one-to-one and the map is small.
        string? ResolveQualifiedName(Stmt.Function stmt)
        {
            foreach (var kv in _closures.FunctionAstNodes)
            {
                if (ReferenceEquals(kv.Value, stmt)) return kv.Key;
            }
            return null;
        }

        foreach (var (arrow, captures) in _collectedArrows)
        {
            if (!_arrowEnclosingFunction.TryGetValue(arrow, out var enclosingStmt))
                continue;
            var enclosingFuncName = ResolveQualifiedName(enclosingStmt);
            if (enclosingFuncName == null)
                continue;
            if (!_closures.FunctionDisplayClassFields.TryGetValue(enclosingFuncName, out var funcDCFields))
                continue;
            if (captures.Any(c => funcDCFields.ContainsKey(c)))
            {
                _arrowsNeedingFunctionDC.Add(arrow);
                _closures.ArrowFunctionDCSource[arrow] = enclosingFuncName;
            }
        }

        // Propagate requirements up the parent chain
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var arrow in _arrowsNeedingFunctionDC.ToList())
            {
                if (_arrowParent.TryGetValue(arrow, out var parent) && parent != null)
                {
                    if (!_arrowsNeedingFunctionDC.Contains(parent))
                    {
                        _arrowsNeedingFunctionDC.Add(parent);
                        // Inherit the source function from the child
                        if (_closures.ArrowFunctionDCSource.TryGetValue(arrow, out var source))
                        {
                            _closures.ArrowFunctionDCSource[parent] = source;
                        }
                        changed = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Creates a display class for an arrow function's captured local variables.
    /// This is needed when local variables are captured by inner arrow functions.
    /// </summary>
    private void DefineArrowScopeDisplayClass(Expr.ArrowFunction arrow)
    {
        var capturedLocals = _closures.Analyzer.GetCapturedLocals(arrow);
        if (capturedLocals.Count == 0)
            return;

        var displayClass = _moduleBuilder.DefineType(
            $"<>c__ArrowScopeDC{_closures.DisplayClassCounter++}",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object);

        var fieldMap = new Dictionary<string, FieldBuilder>();
        foreach (var varName in capturedLocals)
        {
            var field = displayClass.DefineField(varName, _types.Object, FieldAttributes.Public);
            fieldMap[varName] = field;
        }

        var ctor = displayClass.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes);
        var ctorIl = ctor.GetILGenerator();
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.Object));
        ctorIl.Emit(OpCodes.Ret);

        _closures.ArrowScopeDisplayClasses[arrow] = displayClass;
        _closures.ArrowScopeDisplayClassCtors[arrow] = ctor;
        _closures.ArrowScopeDisplayClassFields[arrow] = fieldMap;
    }

    /// <summary>
    /// Propagates arrow scope display class requirements through arrow nesting.
    /// If an inner arrow needs $arrowDC, its parent arrows also need it to pass it through.
    /// </summary>
    private void PropagateArrowDCRequirements()
    {
        // First, identify arrows that directly capture arrow-scope locals
        foreach (var (arrow, captures) in _collectedArrows)
        {
            foreach (var (parentArrow, arrowDCFields) in _closures.ArrowScopeDisplayClassFields)
            {
                if (captures.Any(c => arrowDCFields.ContainsKey(c)))
                {
                    _arrowsNeedingArrowDC.Add(arrow);
                    _closures.ArrowScopeDCSource[arrow] = parentArrow;
                    break;
                }
            }
        }

        // Propagate requirements up the parent chain
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var arrow in _arrowsNeedingArrowDC.ToList())
            {
                if (_arrowParent.TryGetValue(arrow, out var parent) && parent != null)
                {
                    // Don't propagate past the arrow that owns the DC
                    if (_closures.ArrowScopeDisplayClasses.ContainsKey(parent))
                        continue;

                    if (!_arrowsNeedingArrowDC.Contains(parent))
                    {
                        _arrowsNeedingArrowDC.Add(parent);
                        if (_closures.ArrowScopeDCSource.TryGetValue(arrow, out var source))
                        {
                            _closures.ArrowScopeDCSource[parent] = source;
                        }
                        changed = true;
                    }
                }
            }
        }
    }

    private void EmitArrowFunctionBodies()
    {
        var savedPath = _modules.CurrentPath;
        foreach (var (arrow, captures) in _collectedArrows)
        {
            // Skip async arrows - they're handled separately via AsyncArrowMoveNextEmitter
            // in ILCompiler.Async.cs
            if (arrow.IsAsync)
            {
                continue;
            }

            if (_arrowToModule.TryGetValue(arrow, out var arrowModule))
            {
                _modules.CurrentPath = NormalizeToEmissionPath(arrowModule);
            }

            var methodBuilder = _closures.ArrowMethods[arrow];

            // Check if this arrow needs function DC or arrow DC (either directly or to pass to inner arrows)
            // This must match the logic in CollectAndDefineArrowFunctions
            bool needsFunctionDCForArrow = _arrowsNeedingFunctionDC.Contains(arrow);
            bool needsArrowDCForArrow = _arrowsNeedingArrowDC.Contains(arrow);

            if (captures.Count == 0 && !needsFunctionDCForArrow && !needsArrowDCForArrow)
            {
                // Non-capturing and doesn't need function DC: emit body into static method
                EmitArrowBody(arrow, methodBuilder, null);
            }
            else
            {
                // Capturing or needs function DC: emit body into display class method
                var displayClass = _closures.DisplayClasses[arrow];
                EmitArrowBody(arrow, methodBuilder, displayClass);
            }
        }
        _modules.CurrentPath = savedPath;
    }

    private void EmitArrowBody(Expr.ArrowFunction arrow, MethodBuilder method, TypeBuilder? displayClass)
    {
        var il = method.GetILGenerator();

        // Get the resolved return type for this arrow (for typed return optimization)
        _closures.ArrowReturnTypes.TryGetValue(arrow, out var arrowReturnType);
        arrowReturnType ??= _types.Object;

        var ctx = new CompilationContext(il, _typeMapper, _functions.Builders, _classes.Builders, _types)
        {
            ClosureAnalyzer = _closures.Analyzer,
            ArrowMethods = _closures.ArrowMethods,
            DisplayClasses = _closures.DisplayClasses,
            DisplayClassFields = _closures.DisplayClassFields,
            DisplayClassConstructors = _closures.DisplayClassConstructors,
            FunctionRestParams = _functions.RestParams,
            FunctionsCapturingArguments = _functions.CapturingArguments,
            EnumMembers = _enums.Members,
            EnumReverse = _enums.Reverse,
            EnumKinds = _enums.Kinds,
            Runtime = _runtime,
            FunctionGenericParams = _functions.GenericParams,
            IsGenericFunction = _functions.IsGeneric,
            TypeMap = _typeMap,
            DeadCode = _deadCodeInfo,
            AsyncMethods = null,
            AsyncArrowBuilders = _async.ArrowBuilders.Count > 0 ? _async.ArrowBuilders : null,
            // Top-level variables for module-level access
            TopLevelStaticVars = BuildTopLevelStaticVarsForModule(_modules.CurrentPath),
            // Module support for multi-module compilation
            CurrentModulePath = _modules.CurrentPath,
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
            // Registry services
            ClassRegistry = GetClassRegistry(),
            // Propagate the enclosing class name so private-member dispatch (#field / #method
            // access, `super` lookups) works inside arrow bodies nested in class methods.
            // Without this, `this.#parseGlob()` inside an arrow throws "class context not
            // available" at runtime.
            CurrentClassName = _async.ArrowEnclosingClassNames.TryGetValue(arrow, out var enclosingClassName)
                ? enclosingClassName
                : null,
            // Entry-point display class for accessing captured top-level variables
            EntryPointDisplayClassFields = BuildEntryPointDisplayClassFieldsForModule(_modules.CurrentPath),
            CapturedTopLevelVars = BuildCapturedTopLevelVarsForModule(_modules.CurrentPath),
            ArrowEntryPointDCFields = _closures.ArrowEntryPointDCFields.Count > 0 ? _closures.ArrowEntryPointDCFields : null,
            EntryPointDisplayClassStaticField = _closures.EntryPointDisplayClassStaticField,
            // Function-level display class for nested arrow functions
            ArrowFunctionDCFields = _closures.ArrowFunctionDCFields.Count > 0 ? _closures.ArrowFunctionDCFields : null,
            // Arrow scope display class for nested arrow functions
            ArrowScopeDCFields = _closures.ArrowScopeDCFields.Count > 0 ? _closures.ArrowScopeDCFields : null,
            // Inner function metadata — required for any `function X() {}` declared INSIDE this
            // arrow body to be reachable from sibling statements. Without this, IIFE-style
            // wrappers that declare nested functions (lodash's `(function() { function basePropertyOf() {...} }())`)
            // crash at runtime with "Undefined variable" when earlier statements reference them.
            InnerFunctionMethods = _innerFunctionMethods,
            InnerFunctionDisplayClasses = _innerFunctionDisplayClasses,
            InnerFunctionDCFields = _innerFunctionDCFields,
            InnerFunctionDCCtors = _innerFunctionDCCtors,
            InnerFunctionEntryPointDCFields = _innerFunctionEntryPointDCFields,
            InnerFunctionFunctionDCFields = _innerFunctionFunctionDCFields,
            // CJS-context propagation — arrow bodies nested in a CJS module must see the
            // same `exports` binding and require() resolution as the enclosing module init.
            // Without this, bare `exports` inside an IIFE resolves to null and the lodash-
            // style feature detection `typeof exports == 'object' && exports && ...` fails.
            CurrentCjsExportsField = _modules.CurrentPath != null
                && _modules.CommonJsExportFields.TryGetValue(_modules.CurrentPath, out var cjsArrowExports)
                ? cjsArrowExports
                : null,
            ModuleResolver = _modules.Resolver,
            ModuleExportFields = _modules.ExportFields,
            ModuleInitMethods = _modules.InitMethods,
            ModuleImportFields = _modules.ImportFields,
            ModuleTypes = _modules.Types,
            CommonJsExportFields = _modules.CommonJsExportFields,
            CommonJsGetExportsMethods = _modules.CommonJsGetExportsMethods,
            // Typed return optimization - set return type so EmitReturn knows whether to box
            CurrentMethodReturnType = arrowReturnType
        };

        if (displayClass != null)
        {
            // Instance method on display class - this is arg 0
            ctx.IsInstanceMethod = true;

            // Use the pre-stored field mapping
            if (_closures.DisplayClassFields.TryGetValue(arrow, out var fieldMap))
            {
                ctx.CapturedFields = fieldMap;
            }
            else
            {
                ctx.CapturedFields = [];
            }

            // Set the $entryPointDC field if this arrow captures top-level variables
            if (_closures.ArrowEntryPointDCFields.TryGetValue(arrow, out var entryPointDCField))
            {
                ctx.CurrentArrowEntryPointDCField = entryPointDCField;
            }

            // Set the $functionDC field if this arrow captures function-level variables
            if (_closures.ArrowFunctionDCFields.TryGetValue(arrow, out var functionDCField))
            {
                ctx.CurrentArrowFunctionDCField = functionDCField;

                // Also set up the captured function locals info so LocalVariableResolver can use it
                if (_closures.ArrowFunctionDCSource.TryGetValue(arrow, out var sourceFuncName) &&
                    _closures.FunctionDisplayClassFields.TryGetValue(sourceFuncName, out var funcDCFields))
                {
                    ctx.FunctionDisplayClassFields = funcDCFields;
                    ctx.CapturedFunctionLocals = [.. funcDCFields.Keys];
                }
            }

            // Set the $arrowDC field if this arrow captures arrow-scope variables
            // from a parent arrow. Populate BOTH the parent-slot pair (for
            // LocalVariableResolver case 2c — arrows that also own a DC) and
            // the legacy own-slot pair (for arrows without their own DC,
            // which read parent fields through the same direct-field map). The
            // own slots may be overwritten later if this arrow turns out to
            // have its own arrow-scope DC; in that case the parent slots
            // remain intact so same-named parent fields are still reachable.
            if (_closures.ArrowScopeDCFields.TryGetValue(arrow, out var arrowScopeDCFieldRef))
            {
                ctx.CurrentArrowScopeDCField = arrowScopeDCFieldRef;

                if (_closures.ArrowScopeDCSource.TryGetValue(arrow, out var sourceArrowRef) &&
                    _closures.ArrowScopeDisplayClassFields.TryGetValue(sourceArrowRef, out var arrowDCFields))
                {
                    ctx.ParentArrowScopeDisplayClassFields = arrowDCFields;
                    ctx.ParentArrowCapturedLocals = [.. arrowDCFields.Keys];
                    // Legacy fallback: arrows without their own arrow-scope DC
                    // also see these as their "own" fields (read through
                    // $arrowDC) — preserved for compatibility with code paths
                    // that haven't been updated to check the parent slots.
                    ctx.ArrowScopeDisplayClassFields = arrowDCFields;
                    ctx.CapturedArrowLocals = [.. arrowDCFields.Keys];
                }
            }

            // Get resolved parameter types for this arrow (for typed parameter optimization)
            _closures.ArrowParameterTypes.TryGetValue(arrow, out var arrowParamTypes);

            // For object methods, __this is the first parameter after 'this' (display class)
            // Parameters start at index 1 (display class is arg 0)
            if (arrow.HasOwnThis)
            {
                // __this is at index 1, actual parameters start at index 2
                ctx.DefineParameter("__this", 1);
                for (int i = 0; i < arrow.Parameters.Count; i++)
                {
                    Type? paramType = arrowParamTypes != null && i < arrowParamTypes.Length ? arrowParamTypes[i] : null;
                    ctx.DefineParameter(arrow.Parameters[i].Name.Lexeme, i + 2, paramType);
                }
            }
            else
            {
                for (int i = 0; i < arrow.Parameters.Count; i++)
                {
                    Type? paramType = arrowParamTypes != null && i < arrowParamTypes.Length ? arrowParamTypes[i] : null;
                    ctx.DefineParameter(arrow.Parameters[i].Name.Lexeme, i + 1, paramType);
                }
            }
        }
        else
        {
            // Get resolved parameter types for this arrow (for typed parameter optimization)
            _closures.ArrowParameterTypes.TryGetValue(arrow, out var arrowParamTypes);

            // Static method - parameters start at index 0
            if (arrow.HasOwnThis)
            {
                // __this is at index 0, actual parameters start at index 1
                ctx.DefineParameter("__this", 0);
                for (int i = 0; i < arrow.Parameters.Count; i++)
                {
                    Type? paramType = arrowParamTypes != null && i < arrowParamTypes.Length ? arrowParamTypes[i] : null;
                    ctx.DefineParameter(arrow.Parameters[i].Name.Lexeme, i + 1, paramType);
                }
            }
            else
            {
                for (int i = 0; i < arrow.Parameters.Count; i++)
                {
                    Type? paramType = arrowParamTypes != null && i < arrowParamTypes.Length ? arrowParamTypes[i] : null;
                    ctx.DefineParameter(arrow.Parameters[i].Name.Lexeme, i, paramType);
                }
            }
        }

        // Create arrow scope display class instance if this arrow has captured locals
        if (_closures.ArrowScopeDisplayClasses.TryGetValue(arrow, out var arrowScopeDCType) &&
            _closures.ArrowScopeDisplayClassCtors.TryGetValue(arrow, out var arrowScopeDCCtor))
        {
            var arrowScopeDCLocal = il.DeclareLocal(arrowScopeDCType);
            il.Emit(OpCodes.Newobj, arrowScopeDCCtor);
            il.Emit(OpCodes.Stloc, arrowScopeDCLocal);
            ctx.ArrowScopeDisplayClassLocal = arrowScopeDCLocal;
            ctx.ArrowScopeDisplayClassFields = _closures.ArrowScopeDisplayClassFields[arrow];
            ctx.CapturedArrowLocals = _closures.Analyzer.GetCapturedLocals(arrow);

            // Initialize captured parameters into the arrow scope display class.
            // This mirrors the equivalent code in ILCompiler.Functions.cs lines 287-305.
            // Without this, inner arrows that read parameters via $arrowDC get null.
            for (int i = 0; i < arrow.Parameters.Count; i++)
            {
                var paramName = arrow.Parameters[i].Name.Lexeme;
                if (ctx.CapturedArrowLocals.Contains(paramName) &&
                    ctx.ArrowScopeDisplayClassFields.TryGetValue(paramName, out var arrowDCField))
                {
                    il.Emit(OpCodes.Ldloc, arrowScopeDCLocal);
                    // Arg index must match DefineParameter calls (lines 841-858 / 866-883):
                    //   instance (displayClass != null): params at i+1, or i+2 with HasOwnThis
                    //   static (displayClass == null):   params at i,   or i+1 with HasOwnThis
                    int argOffset = displayClass != null ? 1 : 0;
                    if (arrow.HasOwnThis) argOffset++;
                    il.Emit(OpCodes.Ldarg, i + argOffset);
                    il.Emit(OpCodes.Stfld, arrowDCField);
                }
            }
        }

        var emitter = new ILEmitter(ctx);

        // Emit default parameter checks
        emitter.EmitDefaultParameters(arrow.Parameters, displayClass != null, arrow.HasOwnThis);

        // Bind the `arguments` array-like for function expressions (HasOwnThis=true).
        // True arrows don't get their own `arguments` per ECMA-262. Matches the same
        // prologue used by Stmt.Function compilation and fixes lodash-style wrappers
        // like `function() { return fn.apply(this, arguments); }` emitted as arrows.
        if (arrow.HasOwnThis && arrow.BlockBody != null && ReferencesArgumentsIdentifier(arrow.BlockBody))
        {
            // argBase aligns with the parameter-index layout above:
            //   displayClass != null + HasOwnThis → params start at 2 (display, __this, params...)
            //   displayClass != null + !HasOwnThis → params start at 1 (display, params...)
            //   displayClass == null + HasOwnThis → params start at 1 (__this, params...)
            //   displayClass == null + !HasOwnThis → params start at 0
            int paramArgBase = (displayClass != null ? 1 : 0) + (arrow.HasOwnThis ? 1 : 0);
            _closures.ArrowParameterTypes.TryGetValue(arrow, out var resolvedArrowParamTypes);
            var argParamTypes = new Type[arrow.Parameters.Count];
            for (int i = 0; i < argParamTypes.Length; i++)
                argParamTypes[i] = resolvedArrowParamTypes != null && i < resolvedArrowParamTypes.Length
                    ? resolvedArrowParamTypes[i] ?? _types.Object
                    : _types.Object;
            // HasOwnThis methods declare __this as an explicit parameter, which means
            // $TSFunction.InvokeWithThis prepends the receiver into the args array
            // before calling Invoke. Strip that leading slot when reading from the
            // thread-static so `arguments` reflects only what the user passed.
            int leadingSkip = arrow.HasOwnThis ? 1 : 0;
            EmitArgumentsLocalPrologueCore(il, ctx, arrow.Parameters, argParamTypes, paramArgBase, leadingSkip);
        }

        if (arrow.ExpressionBody != null)
        {
            // Expression body: emit expression and return
            emitter.EmitExpression(arrow.ExpressionBody);

            // Handle return based on method return type
            if (arrowReturnType == _types.Object)
            {
                // Return type is object - box value types
                emitter.EmitBoxIfNeeded(arrow.ExpressionBody);
            }
            else if (_types.IsDouble(arrowReturnType))
            {
                // Return type is double - ensure unboxed double on stack
                emitter.EnsureDouble();
            }
            else if (_types.IsBoolean(arrowReturnType))
            {
                // Return type is bool - ensure unboxed bool on stack
                emitter.EnsureBoolean();
            }
            // For string and other reference types, no conversion needed

            il.Emit(OpCodes.Ret);
        }
        else if (arrow.BlockBody != null)
        {
            // Hoist nested `function X() {}` declarations into the arrow's scope. Creates
            // TSFunction locals before any body statement runs, mirroring top-level function
            // compilation at ILCompiler.Functions.cs. Without this, IIFE bodies that reference
            // nested function declarations before those declarations appear lexically (the
            // classic hoisting idiom lodash relies on) get "Undefined variable" at runtime.
            EmitInnerFunctionHoisting(il, ctx, arrow.BlockBody);
            // Block body: emit statements
            foreach (var stmt in arrow.BlockBody)
            {
                emitter.EmitStatement(stmt);
            }
            // Finalize any deferred returns from exception blocks
            if (emitter.HasDeferredReturns)
            {
                emitter.FinalizeReturns();
            }
            else
            {
                // Default return based on return type
                EmitDefaultReturnValue(il, arrowReturnType);
                il.Emit(OpCodes.Ret);
            }
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
        }

        // Include source line + module path in diagnostic so unmarked labels are actionable.
        var arrowLine = arrow.Parameters.FirstOrDefault()?.Name.Line
                        ?? arrow.Name?.Line;
        _arrowToModule.TryGetValue(arrow, out var arrowModulePath);
        var locHint = (arrowModulePath is not null || arrowLine is not null)
            ? $" [{arrowModulePath ?? "?"}:{arrowLine?.ToString() ?? "?"}]"
            : "";
        ILLabelValidator.Validate(il, $"arrow {method.DeclaringType?.FullName}::{method.Name}{locHint}");
    }
}
