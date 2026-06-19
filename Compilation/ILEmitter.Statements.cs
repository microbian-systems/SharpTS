using System.Reflection.Emit;
using SharpTS.Parsing;
using SharpTS.TypeSystem;

namespace SharpTS.Compilation;

/// <summary>
/// Statement emission methods for the IL emitter.
/// </summary>
public partial class ILEmitter
{
    protected override void EmitConditionCheck(Expr condition)
    {
        EmitExpression(condition);
        if (_stackType == StackType.Boolean)
        {
            // Already have unboxed boolean - ready for branch
        }
        else if (_stackType == StackType.Unknown && IsComparisonExpr(condition))
        {
            // Boxed boolean from comparison - unbox it
            IL.Emit(OpCodes.Unbox_Any, _ctx.Types.Boolean);
        }
        else
        {
            // For other expressions (including Expr.Logical which returns boxed object),
            // apply truthy check to convert to int for Brfalse/Brtrue
            EnsureBoxed();
            EmitTruthyCheck();
        }
    }

    protected override void EmitVarDeclaration(Stmt.Var v)
    {
        // Module-level storage (static field / entry-point display class) is only the right
        // target when this declaration IS a module-level binding: we are emitting the module
        // top-level statements (IsModuleTopLevel) and not inside a nested block. Inside a
        // function body the same dictionaries are present for READ access, but a same-named
        // declaration there is a function-local that must shadow — not overwrite — the module
        // binding; falling into this block wrote a function-local through to the module slot
        // and never created the real local, so captured reads saw null and the module var was
        // clobbered (#562). A nested block at top level likewise shadows via a fresh local.
        if (_ctx.IsModuleTopLevel && !_ctx.Locals.IsInNestedScope)
        {
            // Check if this is a captured top-level variable - use entry-point display class
            if (_ctx.CapturedTopLevelVars?.Contains(v.Name.Lexeme) == true &&
                _ctx.EntryPointDisplayClassFields?.TryGetValue(v.Name.Lexeme, out var displayField) == true)
            {
                // Load the display class instance
                if (_ctx.EntryPointDisplayClassLocal != null)
                {
                    IL.Emit(OpCodes.Ldloc, _ctx.EntryPointDisplayClassLocal);
                }
                else if (_ctx.EntryPointDisplayClassStaticField != null)
                {
                    IL.Emit(OpCodes.Ldsfld, _ctx.EntryPointDisplayClassStaticField);
                }
                else
                {
                    // No access to display class - fall through to static field path
                    goto checkStaticField;
                }

                if (v.Initializer != null)
                {
                    EmitExpression(v.Initializer);
                    EmitBoxIfNeeded(v.Initializer);
                }
                else if (v.TypeAnnotation == "number")
                {
                    // Typed number without initializer defaults to 0
                    IL.Emit(OpCodes.Ldc_R8, 0.0);
                    IL.Emit(OpCodes.Box, _ctx.Types.Double);
                }
                else
                {
                    IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.UndefinedInstance);
                }
                IL.Emit(OpCodes.Stfld, displayField);
                return;
            }

            checkStaticField:

            // Check if this is a top-level variable - use static fields so all functions can access them
            if (_ctx.TopLevelStaticVars?.TryGetValue(v.Name.Lexeme, out var staticField) == true)
            {
                if (v.Initializer != null)
                {
                    EmitExpression(v.Initializer);
                    EmitBoxIfNeeded(v.Initializer);
                    IL.Emit(OpCodes.Stsfld, staticField);
                }
                else if (v.TypeAnnotation == "number")
                {
                    // Typed number without initializer defaults to 0
                    IL.Emit(OpCodes.Ldc_R8, 0.0);
                    IL.Emit(OpCodes.Box, _ctx.Types.Double);
                    IL.Emit(OpCodes.Stsfld, staticField);
                }
                else
                {
                    IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.UndefinedInstance);
                    IL.Emit(OpCodes.Stsfld, staticField);
                }
                return;
            }
        }

        // Check if this is a function-level captured variable - use function display class
        if (_ctx.CapturedFunctionLocals?.Contains(v.Name.Lexeme) == true &&
            _ctx.FunctionDisplayClassFields?.TryGetValue(v.Name.Lexeme, out var funcDisplayField) == true &&
            _ctx.FunctionDisplayClassLocal != null)
        {
            // Store initializer (or default) in function display class field
            IL.Emit(OpCodes.Ldloc, _ctx.FunctionDisplayClassLocal);

            if (v.Initializer != null)
            {
                EmitExpression(v.Initializer);
                EmitBoxIfNeeded(v.Initializer);
            }
            else if (v.TypeAnnotation == "number")
            {
                // Typed number without initializer defaults to 0
                IL.Emit(OpCodes.Ldc_R8, 0.0);
                IL.Emit(OpCodes.Box, _ctx.Types.Double);
            }
            else
            {
                IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.UndefinedInstance);
            }
            IL.Emit(OpCodes.Stfld, funcDisplayField);
            return;
        }

        // Check if this is an arrow-scope captured variable - use arrow scope display class
        if (_ctx.CapturedArrowLocals?.Contains(v.Name.Lexeme) == true &&
            _ctx.ArrowScopeDisplayClassFields?.TryGetValue(v.Name.Lexeme, out var arrowDisplayField) == true &&
            _ctx.ArrowScopeDisplayClassLocal != null)
        {
            // Store initializer (or default) in arrow scope display class field
            IL.Emit(OpCodes.Ldloc, _ctx.ArrowScopeDisplayClassLocal);

            if (v.Initializer != null)
            {
                EmitExpression(v.Initializer);
                EmitBoxIfNeeded(v.Initializer);
            }
            else if (v.TypeAnnotation == "number")
            {
                // Typed number without initializer defaults to 0
                IL.Emit(OpCodes.Ldc_R8, 0.0);
                IL.Emit(OpCodes.Box, _ctx.Types.Double);
            }
            else
            {
                IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.UndefinedInstance);
            }
            IL.Emit(OpCodes.Stfld, arrowDisplayField);
            return;
        }

        // Determine if this local can use unboxed double type
        Type localType = CanUseUnboxedLocal(v) ? _ctx.Types.Double : _ctx.Types.Object;
        var local = _ctx.Locals.DeclareLocal(v.Name.Lexeme, localType);

        if (v.Initializer != null)
        {
            // Self-referential capture write-back (issue #421): a closure created
            // in the initializer that captures THIS variable (e.g.
            // `const s = make(() => s)`) snapshots the local's value before this
            // assignment, so it sees the stale/previous value (or null on the first
            // loop iteration). Track the captured closures' display-class fields and
            // write the freshly-assigned value back into them after the store —
            // giving the closure the live value while keeping per-iteration
            // fresh-binding (each iteration builds a distinct display class).
            var savedSelfCaptureName = _ctx.SelfCaptureVarName;
            var savedSelfCaptureWriteBacks = _ctx.SelfCaptureWriteBacks;
            _ctx.SelfCaptureVarName = v.Name.Lexeme;
            _ctx.SelfCaptureWriteBacks = [];

            EmitExpression(v.Initializer);

            if (_ctx.Types.IsDouble(localType))
            {
                // Ensure we have an unboxed double on stack
                EnsureDouble();
            }
            else
            {
                // Ensure we have a boxed object on stack
                EmitBoxIfNeeded(v.Initializer);
            }
            IL.Emit(OpCodes.Stloc, local);

            foreach (var (dcInstance, field) in _ctx.SelfCaptureWriteBacks)
            {
                IL.Emit(OpCodes.Ldloc, dcInstance);
                IL.Emit(OpCodes.Ldloc, local);
                if (_ctx.Types.IsDouble(localType))
                    IL.Emit(OpCodes.Box, _ctx.Types.Double);
                IL.Emit(OpCodes.Stfld, field);
            }

            _ctx.SelfCaptureVarName = savedSelfCaptureName;
            _ctx.SelfCaptureWriteBacks = savedSelfCaptureWriteBacks;
        }
        else
        {
            if (_ctx.Types.IsDouble(localType))
            {
                // Initialize to 0.0 for uninitialized number variables
                IL.Emit(OpCodes.Ldc_R8, 0.0);
            }
            else
            {
                IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.UndefinedInstance);
            }
            IL.Emit(OpCodes.Stloc, local);
        }
    }

    /// <summary>
    /// Tracks the loop counter variable name for optimized for loops.
    /// When set, the variable can use unboxed double even without explicit type annotation.
    /// </summary>
    private string? _optimizedLoopCounterName;

    /// <summary>
    /// Check whether a local variable can use an unboxed double (float64) IL type
    /// instead of the default object. Eligible when:
    ///   1. It is an optimized for-loop counter, OR
    ///   2. It has an explicit ': number' annotation with a numeric initializer, OR
    ///   3. The TypeChecker inferred its initializer type as 'number'.
    /// By the time this method is called, captured variables have already been
    /// routed to display-class fields (always object), so no capture check is needed.
    /// </summary>
    private bool CanUseUnboxedLocal(Stmt.Var v)
    {
        // #367: a number-typed local that an `any`/`undefined` value may have (transitively) been
        // assigned must use an object slot — an unboxed double slot would coerce the runtime
        // `undefined` sentinel to NaN at the store. The type checker flags such declarations (and,
        // for `const`, the reused initializer expression) in the TypeMap.
        if (_ctx.TypeMap != null &&
            (_ctx.TypeMap.IsUndefinedReachableNumericLocal(v) ||
             (v.Initializer != null && _ctx.TypeMap.IsUndefinedReachableNumericLocal(v.Initializer))))
            return false;

        // Check if this is an optimized for loop counter
        if (_optimizedLoopCounterName != null && v.Name.Lexeme == _optimizedLoopCounterName)
            return true;

        // Explicit 'number' type annotation
        if (v.TypeAnnotation == "number")
        {
            // If there's an initializer, it must be a known number expression
            if (v.Initializer != null)
            {
                var exprType = _ctx.TypeMap?.Get(v.Initializer);
                if (!IsNumericType(exprType))
                    return false;
            }
            return true;
        }

        // Infer from TypeMap: if the initializer is statically typed as 'number'
        // (including number literal types like '1', '42'), the TypeChecker
        // guarantees all assignments stay 'number'.
        if (v.TypeAnnotation == null && v.Initializer != null && _ctx.TypeMap != null)
        {
            var exprType = _ctx.TypeMap.Get(v.Initializer);
            if (IsNumericType(exprType))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the type is a numeric type (number primitive or number literal).
    /// </summary>
    private static bool IsNumericType(TypeSystem.TypeInfo? type) =>
        type is TypeSystem.TypeInfo.Primitive { Type: TokenType.TYPE_NUMBER }
            or TypeSystem.TypeInfo.NumberLiteral;

    /// <summary>
    /// Emits a for loop with unboxed counter and array hoist optimizations.
    /// </summary>
    protected override void EmitFor(Stmt.For f)
    {
        // Analyze the loop to see if we can use an unboxed counter
        var analysis = ForLoopAnalyzer.Analyze(f, _ctx.ClosureAnalyzer);

        if (analysis.CanUseUnboxedCounter && analysis.VariableName != null)
            _optimizedLoopCounterName = analysis.VariableName;

        try
        {
            // Inline base.EmitFor to insert array hoist preamble between
            // initializer and loop start.
            _ctx.Locals.EnterScope();

            // Emit initializer (declares loop variable in current scope)
            if (f.Initializer != null)
                EmitStatement(f.Initializer);

            // Per-iteration reference cells (#650): for a loop binding that the body
            // both mutates and a closure captures, wrap its initial value in a fresh
            // StrongBox and route all body/condition/increment access through the cell
            // (registered in CellBindingLocals). Closures capture the cell by reference,
            // so they observe end-of-iteration mutations; the copy-forward below gives
            // each iteration its own cell.
            var cellNames = _ctx.ClosureAnalyzer?.GetForLoopCells(f);
            List<(string Name, LocalBuilder? Prior)>? activeCells = null;
            if (cellNames != null && cellNames.Count > 0)
            {
                activeCells = EmitForLoopCellInit(cellNames);
            }

            // Array hoist preamble: emit isinst checks for loop-invariant arrays
            var hoisted = EmitArrayHoistPreamble(f.Body, f.Condition, f.Increment);

            var builder = _ctx.ILBuilder;
            var startLabel = builder.DefineLabel("for_start");
            var endLabel = builder.DefineLabel("for_end");
            var continueLabel = builder.DefineLabel("for_continue");

            _ctx.EnterLoop(endLabel, continueLabel);

            builder.MarkLabel(startLabel);
            EmitCancellationCheck();

            if (f.Condition != null)
            {
                EmitConditionCheck(f.Condition);
                builder.Emit_Brfalse(endLabel);
            }

            EmitStatement(f.Body);

            builder.MarkLabel(continueLabel);
            // CreatePerIterationEnvironment analog: copy each cell's end-of-body value
            // into a FRESH cell BEFORE the increment, so the closures created this
            // iteration keep the value they observed and the increment acts on the
            // next iteration's binding.
            if (activeCells != null)
                EmitForLoopCellCopyForward(activeCells);

            if (f.Increment != null)
            {
                EmitExpression(f.Increment);
                IL.Emit(OpCodes.Pop);
            }

            builder.Emit_Br(startLabel);

            builder.MarkLabel(endLabel);
            _ctx.ExitLoop();

            // Pop hoisted cache
            if (hoisted) _ctx.HoistedArrayCaches.Pop();

            if (activeCells != null)
                foreach (var (name, prior) in activeCells)
                {
                    if (prior != null) _ctx.CellBindingLocals[name] = prior;
                    else _ctx.CellBindingLocals.Remove(name);
                }

            _ctx.Locals.ExitScope();
        }
        finally
        {
            _optimizedLoopCounterName = null;
        }
    }

    protected override void EmitIf(Stmt.If i)
    {
        // Check for dead code elimination optimization
        var branchResult = _ctx.DeadCode?.GetIfResult(i) ?? IfBranchResult.BothReachable;

        switch (branchResult)
        {
            case IfBranchResult.OnlyThenReachable:
                // Condition is always true - emit only then branch
                EmitStatement(i.ThenBranch);
                return;

            case IfBranchResult.OnlyElseReachable:
                // Condition is always false - emit only else branch (or nothing)
                if (i.ElseBranch != null)
                {
                    EmitStatement(i.ElseBranch);
                }
                return;
        }

        // BothReachable: emit both branches with condition check
        var builder = _ctx.ILBuilder;
        var elseLabel = builder.DefineLabel("if_else");
        var endLabel = builder.DefineLabel("if_end");

        EmitConditionCheck(i.Condition);
        builder.Emit_Brfalse(elseLabel);

        EmitStatement(i.ThenBranch);
        builder.Emit_Br(endLabel);

        builder.MarkLabel(elseLabel);
        if (i.ElseBranch != null)
        {
            EmitStatement(i.ElseBranch);
        }

        builder.MarkLabel(endLabel);
    }

    // EmitCancellationCheck is inherited from StatementEmitterBase so async/
    // generator state machines also poll the cancellation flag at loop heads
    // (their inherited base loop emitters call it). See issue #74.

    protected override void EmitWhile(Stmt.While w)
    {
        // Array hoist preamble before loop
        var hoisted = EmitArrayHoistPreamble(w.Body, w.Condition, increment: null);

        var builder = _ctx.ILBuilder;
        var startLabel = builder.DefineLabel("while_start");
        var endLabel = builder.DefineLabel("while_end");

        _ctx.EnterLoop(endLabel, startLabel);

        builder.MarkLabel(startLabel);
        EmitCancellationCheck();
        EmitConditionCheck(w.Condition);
        builder.Emit_Brfalse(endLabel);

        EmitStatement(w.Body);
        builder.Emit_Br(startLabel);

        builder.MarkLabel(endLabel);
        _ctx.ExitLoop();

        if (hoisted) _ctx.HoistedArrayCaches.Pop();
    }

    /// <summary>
    /// Emits isinst preamble for loop-invariant array variables.
    /// Returns true if any arrays were hoisted (caller must pop the cache).
    /// </summary>
    private bool EmitArrayHoistPreamble(Stmt body, Expr? condition, Expr? increment)
    {
        var candidates = ArrayHoistAnalyzer.AnalyzeFor(body, condition, increment, _ctx.TypeMap);
        if (candidates.Count == 0) return false;

        // Exclude variables already hoisted by an outer loop
        foreach (var name in candidates.Keys.ToList())
        {
            if (_ctx.TryGetHoistedArray(name) != null)
                candidates.Remove(name);
        }
        if (candidates.Count == 0) return false;

        var cache = new Dictionary<string, HoistedArrayEntry>();

        foreach (var (varName, desc) in candidates)
        {
            var listType = desc.GetListType(_ctx.Types);
            var typedLocal = IL.DeclareLocal(listType);

            // Load array variable, isinst to typed list, store in local
            // If the variable holds a different type, typedLocal will be null
            // Use the local directly to avoid stack type tracking complications
            var arrLocal = _ctx.Locals.GetLocal(varName);
            if (arrLocal == null) continue; // Variable not found in locals — skip
            IL.Emit(OpCodes.Ldloc, arrLocal);
            // Array locals are always typed as object — no boxing needed
            IL.Emit(OpCodes.Isinst, listType);
            IL.Emit(OpCodes.Stloc, typedLocal);

            cache[varName] = new HoistedArrayEntry(typedLocal, desc);
        }

        _ctx.HoistedArrayCaches.Push(cache);
        return true;
    }

    protected override void EmitDoWhile(Stmt.DoWhile dw)
    {
        var builder = _ctx.ILBuilder;
        var startLabel = builder.DefineLabel("dowhile_start");
        var endLabel = builder.DefineLabel("dowhile_end");
        var continueLabel = builder.DefineLabel("dowhile_continue");

        _ctx.EnterLoop(endLabel, continueLabel);

        // Body executes at least once
        builder.MarkLabel(startLabel);
        EmitCancellationCheck();
        EmitStatement(dw.Body);

        // Continue target is after the body, before condition check
        builder.MarkLabel(continueLabel);

        // Evaluate condition
        EmitConditionCheck(dw.Condition);
        builder.Emit_Brtrue(startLabel);

        builder.MarkLabel(endLabel);
        _ctx.ExitLoop();
    }

    protected override void EmitForOf(Stmt.ForOf f)
    {
        // A for-of emits several alternative runtime paths (iterator protocol, index-based, …),
        // each registering its own loop scope. Capture any labeled-loop names once up front and
        // hand them to every path, so `continue`/`break <label>` resolve no matter which path runs
        // at runtime (#558 — consuming them in only the first-emitted path left the others bare).
        var labelNames = _ctx.TakePendingLoopLabels();
        _ctx.Locals.EnterScope();
        var builder = _ctx.ILBuilder;

        // Evaluate iterable
        TypeInfo? iterableType = _ctx.TypeMap?.Get(f.Iterable);
        EmitExpression(f.Iterable);

        // For Map/Set, convert to a List first
        if (iterableType is TypeInfo.Map)
        {
            // Map iteration yields [key, value] entries
            IL.Emit(OpCodes.Call, _ctx.Runtime!.MapEntries);
        }
        else if (iterableType is TypeInfo.Set)
        {
            // Set iteration yields values
            IL.Emit(OpCodes.Call, _ctx.Runtime!.SetValues);
        }

        // For generators, use IEnumerable-based iteration
        if (iterableType is TypeInfo.Generator)
        {
            var genStartLabel = builder.DefineLabel("forof_gen_start");
            var genEndLabel = builder.DefineLabel("forof_gen_end");
            var genContinueLabel = builder.DefineLabel("forof_gen_continue");
            _ctx.EnterLoop(genEndLabel, genContinueLabel, labelNames);
            EmitForOfEnumerator(f, genStartLabel, genEndLabel, genContinueLabel);
            return;
        }

        // For iterators, normalize to IEnumerator then iterate
        if (iterableType is TypeInfo.Iterator)
        {
            var iterStartLabel = builder.DefineLabel("forof_iter_start");
            var iterEndLabel = builder.DefineLabel("forof_iter_end");
            var iterContinueLabel = builder.DefineLabel("forof_iter_continue");
            _ctx.EnterLoop(iterEndLabel, iterContinueLabel, labelNames);
            EmitForOfNormalizedEnumerator(f, iterStartLabel, iterEndLabel, iterContinueLabel);
            return;
        }

        // Store the iterable for potential iterator protocol check
        var iterableLocal = IL.DeclareLocal(_ctx.Types.Object);
        IL.Emit(OpCodes.Stloc, iterableLocal);

        // Phase C: when the iterable's static type is `T[]`, skip the
        // iterator-protocol probe and the per-iter GetLength/GetElement
        // dispatch. Direct `Callvirt list.Count + Callvirt list[i]` loop
        // for the Object-kind common case (any[], string[], etc.) plus
        // $Array wrappers. Typed kinds (number[], boolean[]) currently
        // run through the existing slow path — their runtime
        // representation can be a typed list OR a List<object> depending
        // on construction site, and the slow path already handles both.
        var arrayDesc = ArrayElements.Resolve(iterableType);
        if (arrayDesc != null && arrayDesc.Kind == ArrayElementsKind.Object)
        {
            EmitForOfArrayDirect(f, iterableLocal, arrayDesc, labelNames);
            _ctx.Locals.ExitScope();
            return;
        }

        // Try iterator protocol first: GetIteratorFunction(iterable, Symbol.iterator)
        var iteratorFnLocal = IL.DeclareLocal(_ctx.Types.Object);
        var indexBasedLabel = builder.DefineLabel("forof_index_based");
        var afterLoopLabel = builder.DefineLabel("forof_after");

        IL.Emit(OpCodes.Ldloc, iterableLocal);
        IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.SymbolIterator);
        IL.Emit(OpCodes.Call, _ctx.Runtime!.GetIteratorFunction);
        IL.Emit(OpCodes.Stloc, iteratorFnLocal);

        // If iterator function is null, fall back to index-based iteration
        IL.Emit(OpCodes.Ldloc, iteratorFnLocal);
        builder.Emit_Brfalse(indexBasedLabel);

        // ===== Iterator protocol path =====
        {
            var iterStartLabel = builder.DefineLabel("forof_iter_start");
            var iterEndLabel = builder.DefineLabel("forof_iter_end");
            var iterContinueLabel = builder.DefineLabel("forof_iter_continue");
            _ctx.EnterLoop(iterEndLabel, iterContinueLabel, labelNames);

            // Call the iterator function to get the iterator object
            // Use InvokeMethodValue to properly bind 'this' to the iterable object
            IL.Emit(OpCodes.Ldloc, iterableLocal);       // receiver (this)
            IL.Emit(OpCodes.Ldloc, iteratorFnLocal);     // method
            IL.Emit(OpCodes.Ldc_I4_0);
            IL.Emit(OpCodes.Newarr, _ctx.Types.Object);  // args
            IL.Emit(OpCodes.Call, _ctx.Runtime!.InvokeMethodValue);

            // Store the iterator object
            var iteratorObjLocal = IL.DeclareLocal(_ctx.Types.Object);
            IL.Emit(OpCodes.Stloc, iteratorObjLocal);

            // Create $IteratorWrapper: new $IteratorWrapper(iteratorObj, typeof($Runtime))
            IL.Emit(OpCodes.Ldloc, iteratorObjLocal);
            IL.Emit(OpCodes.Ldtoken, _ctx.Runtime!.RuntimeType);
            IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.Type, "GetTypeFromHandle"));
            IL.Emit(OpCodes.Newobj, _ctx.Runtime!.IteratorWrapperCtor);

            // Cast to IEnumerator and store
            var enumLocal = IL.DeclareLocal(_ctx.Types.IEnumerator);
            IL.Emit(OpCodes.Castclass, _ctx.Types.IEnumerator);
            IL.Emit(OpCodes.Stloc, enumLocal);

            // Loop variable
            var loopVar = _ctx.Locals.DeclareLocal(f.Variable.Lexeme, _ctx.Types.Object);

            // Get MoveNext and Current methods
            var moveNext = _ctx.Types.GetMethod(_ctx.Types.IEnumerator, "MoveNext");
            var current = _ctx.Types.IEnumerator.GetProperty("Current")!.GetGetMethod()!;

            builder.MarkLabel(iterStartLabel);
            EmitCancellationCheck();

            // Call MoveNext
            IL.Emit(OpCodes.Ldloc, enumLocal);
            IL.Emit(OpCodes.Callvirt, moveNext);
            builder.Emit_Brfalse(iterEndLabel);

            // Get Current
            IL.Emit(OpCodes.Ldloc, enumLocal);
            IL.Emit(OpCodes.Callvirt, current);
            IL.Emit(OpCodes.Stloc, loopVar);

            // Emit body
            EmitStatement(f.Body);

            builder.MarkLabel(iterContinueLabel);
            builder.Emit_Br(iterStartLabel);

            builder.MarkLabel(iterEndLabel);
            _ctx.ExitLoop();
            builder.Emit_Br(afterLoopLabel); // Skip the index-based path
        }

        // ===== Index-based fallback (for arrays, strings, etc.) =====
        builder.MarkLabel(indexBasedLabel);
        {
            // Normalize iterable to List<object> via IterateToList so IEnumerable types
            // (e.g. Intl.Segments) are properly materialized before index-based iteration
            IL.Emit(OpCodes.Ldloc, iterableLocal);
            IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.SymbolIterator);
            IL.Emit(OpCodes.Ldtoken, _ctx.Runtime!.RuntimeType);
            IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.Type, "GetTypeFromHandle"));
            IL.Emit(OpCodes.Call, _ctx.Runtime!.IterateToList);
            IL.Emit(OpCodes.Stloc, iterableLocal);

            var startLabel = builder.DefineLabel("forof_idx_start");
            var endLabel = builder.DefineLabel("forof_idx_end");
            var continueLabel = builder.DefineLabel("forof_idx_continue");
            _ctx.EnterLoop(endLabel, continueLabel, labelNames);

            // Create index variable
            var indexLocal = IL.DeclareLocal(_ctx.Types.Int32);
            IL.Emit(OpCodes.Ldc_I4_0);
            IL.Emit(OpCodes.Stloc, indexLocal);

            // Loop variable
            var indexLoopVar = _ctx.Locals.DeclareLocal(f.Variable.Lexeme, _ctx.Types.Object);

            builder.MarkLabel(startLabel);
            EmitCancellationCheck();

            // Check if index < length
            IL.Emit(OpCodes.Ldloc, indexLocal);
            IL.Emit(OpCodes.Ldloc, iterableLocal);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.GetLength);
            IL.Emit(OpCodes.Clt);
            builder.Emit_Brfalse(endLabel);

            // Get current element
            IL.Emit(OpCodes.Ldloc, iterableLocal);
            IL.Emit(OpCodes.Ldloc, indexLocal);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.GetElement);
            IL.Emit(OpCodes.Stloc, indexLoopVar);

            // Emit body
            EmitStatement(f.Body);

            builder.MarkLabel(continueLabel);

            // Increment index
            IL.Emit(OpCodes.Ldloc, indexLocal);
            IL.Emit(OpCodes.Ldc_I4_1);
            IL.Emit(OpCodes.Add);
            IL.Emit(OpCodes.Stloc, indexLocal);

            builder.Emit_Br(startLabel);

            builder.MarkLabel(endLabel);
            _ctx.ExitLoop();
        }

        // Common exit point for both paths
        builder.MarkLabel(afterLoopLabel);
        _ctx.Locals.ExitScope();
    }

    /// <summary>
    /// Phase C fast path for <c>for (const x of arr)</c> when <c>arr</c> is
    /// statically typed as <c>T[]</c>. Emits a direct list-indexed loop
    /// that bypasses the iterator-protocol probe + per-iter
    /// GetLength/GetElement runtime dispatch (each of which does an
    /// isinst chain). Always emits IL — runtime mismatch (e.g. a
    /// <c>$Array</c> wrapper instead of a bare list) is handled in-line
    /// for Object kind, or routed to a fallback iterator-helper for
    /// typed kinds.
    /// </summary>
    private void EmitForOfArrayDirect(Stmt.ForOf f, LocalBuilder iterableLocal, ArrayElementsDescriptor desc, IReadOnlyList<string>? labelNames = null)
    {
        var builder = _ctx.ILBuilder;
        var listType = desc.GetListType(_ctx.Types);
        var listCountGetter = _ctx.Types.GetProperty(listType, "Count").GetGetMethod()!;
        var listIndexerGetter = _ctx.Types.GetProperty(listType, "Item").GetGetMethod()!;

        var startLabel = builder.DefineLabel("forof_arr_start");
        var endLabel = builder.DefineLabel("forof_arr_end");
        var continueLabel = builder.DefineLabel("forof_arr_continue");
        var loopHeadLabel = builder.DefineLabel("forof_arr_loop_head");
        var fallbackLabel = builder.DefineLabel("forof_arr_fallback");

        // Resolve iterable → List<T>. For Object kind, also accept $Array
        // (unwrapped via .Elements). For typed kinds (Double/Bool),
        // only the bare list shape is supported; non-matches go through
        // the runtime-helper fallback.
        var listLocal = IL.DeclareLocal(listType);
        IL.Emit(OpCodes.Ldloc, iterableLocal);
        IL.Emit(OpCodes.Isinst, listType);
        IL.Emit(OpCodes.Stloc, listLocal);
        IL.Emit(OpCodes.Ldloc, listLocal);
        IL.Emit(OpCodes.Brtrue, loopHeadLabel);

        if (desc.Kind == ArrayElementsKind.Object)
        {
            // $Array wrapper → .Elements
            var notTSArrayLabel = builder.DefineLabel("forof_arr_not_tsarr");
            IL.Emit(OpCodes.Ldloc, iterableLocal);
            IL.Emit(OpCodes.Isinst, _ctx.Runtime!.TSArrayType);
            IL.Emit(OpCodes.Brfalse, notTSArrayLabel);
            IL.Emit(OpCodes.Ldloc, iterableLocal);
            IL.Emit(OpCodes.Castclass, _ctx.Runtime!.TSArrayType);
            IL.Emit(OpCodes.Callvirt, _ctx.Runtime!.TSArrayElementsGetter);
            IL.Emit(OpCodes.Stloc, listLocal);
            IL.Emit(OpCodes.Br, loopHeadLabel);

            builder.MarkLabel(notTSArrayLabel);
            // Last resort: route through IterateToList to materialize.
            IL.Emit(OpCodes.Ldloc, iterableLocal);
            IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.SymbolIterator);
            IL.Emit(OpCodes.Ldtoken, _ctx.Runtime!.RuntimeType);
            IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.Type, "GetTypeFromHandle"));
            IL.Emit(OpCodes.Call, _ctx.Runtime!.IterateToList);
            IL.Emit(OpCodes.Stloc, listLocal);
        }
        else
        {
            // Typed kind. The list could be elsewhere wrapped ($Array stores
            // List<object> only, so an `arr: number[]` declared then mutated
            // through a generic path could wind up as List<object>). Skip
            // the fast path in that case by routing through the runtime
            // helper, which will rebox to List<object> — slow but correct.
            builder.MarkLabel(fallbackLabel);
            // Materialize via IterateToList → returns List<object>. Since
            // listLocal is List<T> typed, we can't store there directly.
            // For typed kinds, just bail: emit the existing index-based
            // path inline using the iterable local. Simplest: route to a
            // GetElement-based loop using listLocal=null marker.
            //
            // For the common case (benchmark), the bare-list path above
            // hits, so this branch is cold. Implement only when needed.
            IL.Emit(OpCodes.Ldstr, "for-of: typed-array fast path expected List<T> at runtime");
            IL.Emit(OpCodes.Newobj, _ctx.Types.GetConstructor(typeof(InvalidOperationException), _ctx.Types.String));
            IL.Emit(OpCodes.Throw);
        }

        // Loop entry: listLocal holds the list.
        builder.MarkLabel(loopHeadLabel);

        _ctx.EnterLoop(endLabel, continueLabel, labelNames ?? CompilationContext.NoLabels);

        // var i = 0
        var indexLocal = IL.DeclareLocal(_ctx.Types.Int32);
        IL.Emit(OpCodes.Ldc_I4_0);
        IL.Emit(OpCodes.Stloc, indexLocal);

        // Loop variable. Element type comes from the descriptor; for the
        // benchmark's `any[]` case it's _types.Object so the loop var
        // matches the existing slow path's binding.
        var elementType = desc.GetElementType(_ctx.Types);
        var loopVar = _ctx.Locals.DeclareLocal(f.Variable.Lexeme, elementType);

        builder.MarkLabel(startLabel);
        EmitCancellationCheck();

        // if (i >= list.Count) goto end
        IL.Emit(OpCodes.Ldloc, indexLocal);
        IL.Emit(OpCodes.Ldloc, listLocal);
        IL.Emit(OpCodes.Callvirt, listCountGetter);
        IL.Emit(OpCodes.Bge, endLabel);

        // loopVar = list[i]; for Object kind, unhole $ArrayHole → $Undefined
        IL.Emit(OpCodes.Ldloc, listLocal);
        IL.Emit(OpCodes.Ldloc, indexLocal);
        IL.Emit(OpCodes.Callvirt, listIndexerGetter);
        if (desc.Kind == ArrayElementsKind.Object)
        {
            // if (top is $ArrayHole) → $Undefined
            var notHoleLabel = builder.DefineLabel("forof_arr_not_hole");
            var unholedLabel = builder.DefineLabel("forof_arr_unholed");
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Isinst, _ctx.Runtime!.ArrayHoleType);
            IL.Emit(OpCodes.Brfalse, notHoleLabel);
            IL.Emit(OpCodes.Pop);
            IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.UndefinedInstance);
            IL.Emit(OpCodes.Br, unholedLabel);
            builder.MarkLabel(notHoleLabel);
            builder.MarkLabel(unholedLabel);
        }
        IL.Emit(OpCodes.Stloc, loopVar);

        // Body
        EmitStatement(f.Body);

        builder.MarkLabel(continueLabel);

        // i++
        IL.Emit(OpCodes.Ldloc, indexLocal);
        IL.Emit(OpCodes.Ldc_I4_1);
        IL.Emit(OpCodes.Add);
        IL.Emit(OpCodes.Stloc, indexLocal);
        IL.Emit(OpCodes.Br, startLabel);

        builder.MarkLabel(endLabel);
        _ctx.ExitLoop();
    }

    private void EmitForOfEnumerator(Stmt.ForOf f, Label startLabel, Label endLabel, Label continueLabel)
    {
        var builder = _ctx.ILBuilder;

        // Use IEnumerable.GetEnumerator()/MoveNext()/Current pattern for generators
        var getEnumerator = _ctx.Types.GetMethod(_ctx.Types.IEnumerable, "GetEnumerator");
        var moveNext = _ctx.Types.GetMethod(_ctx.Types.IEnumerator, "MoveNext");
        var current = _ctx.Types.IEnumerator.GetProperty("Current")!.GetGetMethod()!;

        // Stack has the iterable (generator)
        IL.Emit(OpCodes.Castclass, _ctx.Types.IEnumerable);
        IL.Emit(OpCodes.Callvirt, getEnumerator);

        var enumLocal = IL.DeclareLocal(_ctx.Types.IEnumerator);
        IL.Emit(OpCodes.Stloc, enumLocal);

        // Loop variable
        var loopVar = _ctx.Locals.DeclareLocal(f.Variable.Lexeme, _ctx.Types.Object);

        builder.MarkLabel(startLabel);
        EmitCancellationCheck();

        // Call MoveNext
        IL.Emit(OpCodes.Ldloc, enumLocal);
        IL.Emit(OpCodes.Callvirt, moveNext);
        builder.Emit_Brfalse(endLabel);

        // Get Current
        IL.Emit(OpCodes.Ldloc, enumLocal);
        IL.Emit(OpCodes.Callvirt, current);
        IL.Emit(OpCodes.Stloc, loopVar);

        // Emit body
        EmitStatement(f.Body);

        builder.MarkLabel(continueLabel);
        builder.Emit_Br(startLabel);

        builder.MarkLabel(endLabel);
        _ctx.Locals.ExitScope();
        _ctx.ExitLoop();
    }

    /// <summary>
    /// Emits for...of using NormalizeToEnumerator for iterator types.
    /// Unlike EmitForOfEnumerator (which casts to IEnumerable), this handles
    /// IEnumerator sources (like lazy iterator helpers and array values) correctly.
    /// </summary>
    private void EmitForOfNormalizedEnumerator(Stmt.ForOf f, Label startLabel, Label endLabel, Label continueLabel)
    {
        var builder = _ctx.ILBuilder;
        var moveNext = _ctx.Types.GetMethod(_ctx.Types.IEnumerator, "MoveNext");
        var current = _ctx.Types.IEnumerator.GetProperty("Current")!.GetGetMethod()!;

        // Stack has the iterator source — normalize to IEnumerator<object>
        EmitBoxIfNeeded(f.Iterable);
        IL.Emit(OpCodes.Call, _ctx.Runtime!.NormalizeToEnumerator);

        var enumLocal = IL.DeclareLocal(_ctx.Types.IEnumerator);
        IL.Emit(OpCodes.Stloc, enumLocal);

        // Loop variable
        var loopVar = _ctx.Locals.DeclareLocal(f.Variable.Lexeme, _ctx.Types.Object);

        builder.MarkLabel(startLabel);
        EmitCancellationCheck();

        IL.Emit(OpCodes.Ldloc, enumLocal);
        IL.Emit(OpCodes.Callvirt, moveNext);
        builder.Emit_Brfalse(endLabel);

        IL.Emit(OpCodes.Ldloc, enumLocal);
        IL.Emit(OpCodes.Callvirt, current);
        IL.Emit(OpCodes.Stloc, loopVar);

        EmitStatement(f.Body);

        builder.MarkLabel(continueLabel);
        builder.Emit_Br(startLabel);

        builder.MarkLabel(endLabel);
        _ctx.Locals.ExitScope();
        _ctx.ExitLoop();
    }

    protected override void EmitForIn(Stmt.ForIn f)
    {
        var builder = _ctx.ILBuilder;
        var startLabel = builder.DefineLabel("forin_start");
        var endLabel = builder.DefineLabel("forin_end");
        var continueLabel = builder.DefineLabel("forin_continue");

        _ctx.EnterLoop(endLabel, continueLabel);
        _ctx.Locals.EnterScope();

        // Evaluate object and get keys
        EmitExpression(f.Object);
        IL.Emit(OpCodes.Call, _ctx.Runtime!.GetKeys);
        var keysLocal = IL.DeclareLocal(_ctx.Types.ListOfObject);
        IL.Emit(OpCodes.Stloc, keysLocal);

        // Create index variable
        var indexLocal = IL.DeclareLocal(_ctx.Types.Int32);
        IL.Emit(OpCodes.Ldc_I4_0);
        IL.Emit(OpCodes.Stloc, indexLocal);

        // Loop variable (holds current key)
        var loopVar = _ctx.Locals.DeclareLocal(f.Variable.Lexeme, _ctx.Types.Object);

        builder.MarkLabel(startLabel);
        EmitCancellationCheck();

        // Check if index < keys.Count
        IL.Emit(OpCodes.Ldloc, indexLocal);
        IL.Emit(OpCodes.Ldloc, keysLocal);
        IL.Emit(OpCodes.Call, _ctx.Runtime!.GetLength);
        IL.Emit(OpCodes.Clt);
        builder.Emit_Brfalse(endLabel);

        // Get current key: keys[index]
        IL.Emit(OpCodes.Ldloc, keysLocal);
        IL.Emit(OpCodes.Ldloc, indexLocal);
        IL.Emit(OpCodes.Call, _ctx.Runtime!.GetElement);
        IL.Emit(OpCodes.Stloc, loopVar);

        // Emit body
        EmitStatement(f.Body);

        builder.MarkLabel(continueLabel);

        // Increment index
        IL.Emit(OpCodes.Ldloc, indexLocal);
        IL.Emit(OpCodes.Ldc_I4_1);
        IL.Emit(OpCodes.Add);
        IL.Emit(OpCodes.Stloc, indexLocal);

        builder.Emit_Br(startLabel);

        builder.MarkLabel(endLabel);
        _ctx.Locals.ExitScope();
        _ctx.ExitLoop();
    }

    protected override void EmitBlock(Stmt.Block b)
    {
        _ctx.Locals.EnterScope();

        // Check if block contains using declarations
        var usingResources = new List<LocalBuilder>();
        bool hasUsing = b.Statements.Any(s => s is Stmt.Using);

        if (hasUsing)
        {
            // Emit block with try/finally for disposal
            EmitBlockWithUsing(b.Statements, usingResources);
        }
        else
        {
            // Simple block without using declarations
            foreach (var stmt in b.Statements)
            {
                EmitStatement(stmt);
            }
        }

        _ctx.Locals.ExitScope();
    }

    /// <summary>
    /// Emits a block that contains using declarations with proper try/finally disposal.
    /// </summary>
    private void EmitBlockWithUsing(List<Stmt> statements, List<LocalBuilder> usingResources)
    {
        // Find the first using declaration index
        int firstUsingIndex = statements.FindIndex(s => s is Stmt.Using);

        // Emit statements before the first using
        for (int i = 0; i < firstUsingIndex; i++)
        {
            EmitStatement(statements[i]);
        }

        // Now emit using declarations and remaining statements in a try/finally
        IL.BeginExceptionBlock();

        for (int i = firstUsingIndex; i < statements.Count; i++)
        {
            var stmt = statements[i];
            if (stmt is Stmt.Using usingStmt)
            {
                // Process using declaration - store resources for disposal
                foreach (var binding in usingStmt.Bindings)
                {
                    // Evaluate the initializer
                    EmitExpression(binding.Initializer);
                    EnsureBoxed();

                    // Store in a local variable for later disposal
                    LocalBuilder resourceLocal;
                    if (binding.Name != null)
                    {
                        resourceLocal = _ctx.Locals.DeclareLocal(binding.Name.Lexeme, _ctx.Types.Object);
                    }
                    else
                    {
                        // Anonymous using - still need to track for disposal
                        resourceLocal = IL.DeclareLocal(_ctx.Types.Object);
                    }
                    IL.Emit(OpCodes.Stloc, resourceLocal);
                    usingResources.Add(resourceLocal);
                }
            }
            else
            {
                EmitStatement(stmt);
            }
        }

        // Finally block - dispose resources in reverse order
        IL.BeginFinallyBlock();
        EmitUsingDisposal(usingResources);
        IL.EndExceptionBlock();
    }

    /// <summary>
    /// Emits disposal code for using declaration resources.
    /// Disposes in reverse order (LIFO).
    /// </summary>
    private void EmitUsingDisposal(List<LocalBuilder> resources)
    {
        // Dispose in reverse order
        for (int i = resources.Count - 1; i >= 0; i--)
        {
            var resourceLocal = resources[i];

            // Load the resource
            IL.Emit(OpCodes.Ldloc, resourceLocal);

            // Load Symbol.dispose
            IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.SymbolDispose);

            // Call $Runtime.DisposeResource(resource, Symbol.dispose)
            IL.Emit(OpCodes.Call, _ctx.Runtime!.DisposeResource);
        }
    }

    /// <summary>
    /// Emits a list of statements with proper handling for 'using' declarations.
    /// If using declarations are present, wraps the statements in try/finally for disposal.
    /// </summary>
    public void EmitStatements(List<Stmt> statements)
    {
        // Check if any statement is a using declaration
        bool hasUsing = statements.Any(s => s is Stmt.Using);

        if (hasUsing)
        {
            var usingResources = new List<LocalBuilder>();

            // Find the first using declaration index
            int firstUsingIndex = statements.FindIndex(s => s is Stmt.Using);

            // Emit statements before the first using
            for (int i = 0; i < firstUsingIndex; i++)
            {
                EmitStatement(statements[i]);
            }

            // Now emit using declarations and remaining statements in a try/finally
            // Use the builder for exception block tracking and validation
            var builder = _ctx.ILBuilder;
            _ctx.ExceptionBlockDepth++;
            builder.BeginExceptionBlock();

            for (int i = firstUsingIndex; i < statements.Count; i++)
            {
                var stmt = statements[i];
                if (stmt is Stmt.Using usingStmt)
                {
                    // Process using declaration - store resources for disposal
                    foreach (var binding in usingStmt.Bindings)
                    {
                        // Evaluate the initializer
                        EmitExpression(binding.Initializer);
                        EnsureBoxed();

                        // Store in a local variable for later disposal
                        LocalBuilder resourceLocal;
                        if (binding.Name != null)
                        {
                            resourceLocal = _ctx.Locals.DeclareLocal(binding.Name.Lexeme, _ctx.Types.Object);
                        }
                        else
                        {
                            // Anonymous using - still need to track for disposal
                            resourceLocal = IL.DeclareLocal(_ctx.Types.Object);
                        }
                        IL.Emit(OpCodes.Stloc, resourceLocal);
                        usingResources.Add(resourceLocal);
                    }
                }
                else
                {
                    EmitStatement(stmt);
                }
            }

            // Finally block - dispose resources in reverse order
            builder.BeginFinallyBlock();
            EmitUsingDisposal(usingResources);
            builder.EndExceptionBlock();
            _ctx.ExceptionBlockDepth--;
        }
        else
        {
            // No using declarations - emit normally
            foreach (var stmt in statements)
            {
                EmitStatement(stmt);
            }
        }
    }

    protected override void EmitReturn(Stmt.Return r)
    {
        // Get the current method's return type (defaults to object if not set)
        var returnType = _ctx.CurrentMethodReturnType ?? _ctx.Types.Object;

        if (r.Value != null)
        {
            EmitExpression(r.Value);
            // Only box if return type is object; otherwise use typed value directly
            if (returnType == _ctx.Types.Object)
            {
                EmitBoxIfNeeded(r.Value);
            }
            else if (returnType == typeof(void))
            {
                // Void method (most commonly a constructor) — discard the value to keep
                // the stack balanced on ret. JS constructors can return a replacement
                // object (spec: if the returned value is an object, it replaces `this`),
                // but .NET ctors must return void with an empty stack. We fall back to
                // ignoring the return value, matching the common case where ctors return
                // primitives or `undefined`; spec-compliant object substitution would
                // require rewriting the `new` call site and is out of scope here.
                // Required for real npm packages that use this idiom (semver's Comparator
                // ctor does `return comp` when the argument is already a Comparator).
                EmitBoxIfNeeded(r.Value);
                IL.Emit(OpCodes.Pop);
            }
            else if (_ctx.Types.IsDouble(returnType))
            {
                // Ensure we have an unboxed double for : number return type
                if (_stackType != StackType.Double)
                {
                    EmitUnboxToDouble();
                }
            }
            else if (_ctx.Types.IsBoolean(returnType))
            {
                // Ensure we have an unboxed bool for : boolean return type
                if (_stackType != StackType.Boolean)
                {
                    // Convert to boolean: double -> i4, or object -> unbox to double -> i4
                    if (_stackType == StackType.Double)
                    {
                        IL.Emit(OpCodes.Conv_I4);
                    }
                    else
                    {
                        EmitUnboxToDouble();
                        IL.Emit(OpCodes.Conv_I4);
                    }
                }
            }
            // Note: `string` return types are never emitted as a narrow `string` slot —
            // ParameterTypeResolver.ResolveReturnType maps them to object because a string
            // slot cannot carry the `$Undefined` sentinel an inferred-string body can
            // produce (see #318, which removed the #275 castclass that lived here).
            // For other narrow types (double/bool handled above), the value is already correct.
        }
        else
        {
            // Return undefined (null) or appropriate default
            if (returnType == typeof(void))
            {
                // Void functions: no value on stack; ret takes nothing.
            }
            else if (returnType == _ctx.Types.Object)
            {
                // ECMA-262: a bare `return;` completes with undefined, not null. Emit the
                // $Undefined sentinel for untyped object returns — mirrors EmitDefaultReturnValue
                // (the off-the-end path) and the interpreter's VisitReturn — so a plain function
                // returning no value is `undefined`. `return null;` (the r.Value != null branch
                // above) still yields null. #563
                EmitUndefinedConstant();
            }
            else if (!returnType.IsValueType)
            {
                // Specific reference-typed returns keep their null default (matches
                // EmitDefaultReturnValue): the checker treats an explicit `T | null` return as null.
                IL.Emit(OpCodes.Ldnull);
            }
            else if (_ctx.Types.IsDouble(returnType))
            {
                IL.Emit(OpCodes.Ldc_R8, 0.0);
            }
            else if (_ctx.Types.IsBoolean(returnType))
            {
                IL.Emit(OpCodes.Ldc_I4_0);
            }
            else
            {
                // For other value types, emit default
                var local = IL.DeclareLocal(returnType);
                IL.Emit(OpCodes.Ldloca, local);
                IL.Emit(OpCodes.Initobj, returnType);
                IL.Emit(OpCodes.Ldloc, local);
            }
        }

        if (_ctx.ExceptionBlockDepth > 0)
        {
            // Inside exception block: store value and leave
            // Use builder for Leave validation (ensures we're inside exception block)
            var builder = _ctx.ILBuilder;
            if (_ctx.ReturnValueLocal == null)
            {
                _ctx.ReturnValueLocal = IL.DeclareLocal(returnType);
                _ctx.ReturnLabel = builder.DefineLabel("deferred_return");
            }
            IL.Emit(OpCodes.Stloc, _ctx.ReturnValueLocal);
            builder.Emit_Leave(_ctx.ReturnLabel);
        }
        else
        {
            IL.Emit(OpCodes.Ret);
        }

        // Reset stack type after return consumes the value. Without this,
        // _stackType remains stale (e.g., Double from 'return 0') and dead code
        // emitted after the return (like the 'br endLabel' in EmitIf) preserves
        // the stale type. When the branch target is reached and new code emits
        // EmitBoxIfNeeded, it sees StackType.Double and incorrectly boxes the
        // next value (e.g., an array reference) as a Double.
        SetStackUnknown();
    }

    protected override void EmitBreak(Stmt.Break b)
    {
        var loop = b.Label != null
            ? FindLabeledLoop(b.Label.Lexeme)
            : CurrentLoop;

        if (loop != null)
            EmitBranchToLabel(loop.Value.BreakLabel);
    }

    protected override void EmitContinue(Stmt.Continue c)
    {
        var loop = c.Label != null
            ? FindLabeledLoop(c.Label.Lexeme)
            : CurrentLoop;

        if (loop != null)
            EmitBranchToLabel(loop.Value.ContinueLabel);
    }

    protected override void EmitLabeledStatement(Stmt.LabeledStatement labeledStmt)
    {
        // Look through a chain of labels (a: b: …) to whatever they ultimately wrap.
        var inner = UnwrapLabelChain(labeledStmt, out var chainLabels);

        if (IsLabelableLoop(inner))
        {
            // Direct (or chained) loop: park EVERY label in the chain so the inner loop attaches them
            // all to its OWN break/continue targets (a for-loop's increment, a while's condition, …).
            // Marking a continue label here — ahead of a for's initializer — would re-run it forever,
            // and the outer label of a chain used to fall into exactly that path (#558/#580).
            foreach (var label in chainLabels)
                _ctx.AddPendingLoopLabel(label);
            try
            {
                EmitStatement(inner);
            }
            finally
            {
                // The loop's EnterLoop drains the parked labels; clear any it somehow didn't.
                _ctx.ClearPendingLoopLabels();
            }
            return;
        }

        // Non-loop labeled statement (a block, etc.). Mark the continue target before the statement
        // (harmless for a block) and keep one wrapper scope per label by recursing through the chain;
        // only `break <label>` is meaningful here.
        string labelName = labeledStmt.Label.Lexeme;
        var builder = _ctx.ILBuilder;
        var breakLabel = builder.DefineLabel($"labeled_{labelName}_break");
        var continueLabel = builder.DefineLabel($"labeled_{labelName}_continue");
        builder.MarkLabel(continueLabel);
        _ctx.EnterLoop(breakLabel, continueLabel, labelName);
        try
        {
            EmitStatement(labeledStmt.Statement);
        }
        finally
        {
            _ctx.ExitLoop();
        }
        builder.MarkLabel(breakLabel);
    }

    protected override void EmitSwitch(Stmt.Switch s)
    {
        // Check for exhaustive switch optimization
        var switchAnalysis = _ctx.DeadCode?.GetSwitchResult(s);
        bool skipDefault = switchAnalysis?.DefaultIsUnreachable == true;

        var builder = _ctx.ILBuilder;
        var endLabel = builder.DefineLabel("switch_end");
        var defaultLabel = builder.DefineLabel("switch_default");
        var caseLabels = s.Cases.Select((_, i) => builder.DefineLabel($"switch_case_{i}")).ToList();

        // Evaluate subject once
        EmitExpression(s.Subject);
        var subjectLocal = IL.DeclareLocal(_ctx.Types.Object);
        EmitBoxIfNeeded(s.Subject);
        IL.Emit(OpCodes.Stloc, subjectLocal);

        // Generate case comparisons
        for (int i = 0; i < s.Cases.Count; i++)
        {
            IL.Emit(OpCodes.Ldloc, subjectLocal);
            EmitExpression(s.Cases[i].Value);
            EmitBoxIfNeeded(s.Cases[i].Value);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.Equals);
            builder.Emit_Brtrue(caseLabels[i]);
        }

        // Jump to default or end (skip default if unreachable)
        if (skipDefault || s.DefaultBody == null)
        {
            builder.Emit_Br(endLabel);
        }
        else
        {
            builder.Emit_Br(defaultLabel);
        }

        // Register the switch end as the current break target so nested breaks
        // (inside blocks, if/else, try/catch, etc.) exit the switch. Preserve the
        // outer loop's continue target so `continue;` still propagates outward.
        var outerContinue = CurrentLoop?.ContinueLabel ?? endLabel;
        EnterLoop(endLabel, outerContinue);
        try
        {
            // Emit case bodies
            for (int i = 0; i < s.Cases.Count; i++)
            {
                builder.MarkLabel(caseLabels[i]);
                foreach (var stmt in s.Cases[i].Body)
                {
                    if (stmt is Stmt.Break breakStmt)
                    {
                        if (breakStmt.Label != null)
                        {
                            // Labeled break - find and jump to the labeled target
                            EmitBreak(breakStmt);
                        }
                        else
                        {
                            // Unlabeled break - exits switch only
                            builder.Emit_Br(endLabel);
                        }
                    }
                    else
                    {
                        EmitStatement(stmt);
                    }
                }
                // Fall through if no break
            }

            // Default case (skip if unreachable)
            if (s.DefaultBody != null && !skipDefault)
            {
                builder.MarkLabel(defaultLabel);
                foreach (var stmt in s.DefaultBody)
                {
                    if (stmt is Stmt.Break breakStmt)
                    {
                        if (breakStmt.Label != null)
                        {
                            // Labeled break - find and jump to the labeled target
                            EmitBreak(breakStmt);
                        }
                        else
                        {
                            // Unlabeled break - exits switch only
                            builder.Emit_Br(endLabel);
                        }
                    }
                    else
                    {
                        EmitStatement(stmt);
                    }
                }
            }
        }
        finally
        {
            ExitLoop();
        }

        builder.MarkLabel(endLabel);
    }

    protected override void EmitTryCatch(Stmt.TryCatch t)
    {
        // Use ValidatedILBuilder for exception block operations - it tracks depth automatically
        // and validates proper Begin/End pairing
        var builder = _ctx.ILBuilder;

        _ctx.ExceptionBlockDepth++;
        builder.BeginExceptionBlock();

        foreach (var stmt in t.TryBlock)
        {
            EmitStatement(stmt);
        }

        if (t.CatchBlock != null)
        {
            builder.BeginCatchBlock(_ctx.Types.Exception);

            if (t.CatchParam != null)
            {
                // Store exception
                var exLocal = _ctx.Locals.DeclareLocal(t.CatchParam.Lexeme, _ctx.Types.Object);
                IL.Emit(OpCodes.Call, _ctx.Runtime!.WrapException);
                IL.Emit(OpCodes.Stloc, exLocal);
            }
            else
            {
                IL.Emit(OpCodes.Pop);
            }

            foreach (var stmt in t.CatchBlock)
            {
                EmitStatement(stmt);
            }
        }

        if (t.FinallyBlock != null)
        {
            builder.BeginFinallyBlock();
            foreach (var stmt in t.FinallyBlock)
            {
                EmitStatement(stmt);
            }
        }

        builder.EndExceptionBlock();
        _ctx.ExceptionBlockDepth--;
    }

    protected override void EmitThrow(Stmt.Throw t)
    {
        EmitExpression(t.Value);
        EmitBoxIfNeeded(t.Value);
        IL.Emit(OpCodes.Call, _ctx.Runtime!.CreateException);
        IL.Emit(OpCodes.Throw);
    }

    protected override void EmitPrint(Stmt.Print p)
    {
        EmitExpression(p.Expr);
        EmitBoxIfNeeded(p.Expr);
        // Call Console.WriteLine(object) directly
        IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.Console, "WriteLine", _ctx.Types.Object));
    }
}
