using System.Reflection.Emit;
using SharpTS.Parsing;
using SharpTS.TypeSystem;

namespace SharpTS.Compilation;

public partial class GeneratorMoveNextEmitter
{
    #region Abstract Method Implementations from StatementEmitterBase

    protected override FieldBuilder? GetHoistedVariableField(string name) => _builder.GetVariableField(name);

    protected override void EmitReturn(Stmt.Return r)
    {
        // Evaluate the return value for its side effects — the expression may contain
        // `yield` or `yield*` sub-expressions whose state labels are registered by the
        // analyzer at the top of MoveNext. Skipping evaluation (the prior behaviour)
        // left those labels defined but never marked, which makes the metadata writer
        // fail with "Label N has not been marked" (surfaced via yaml's lexer.parseNext,
        // where every `case '…': return yield* this.parseFoo()` contributed one
        // orphaned state label).
        //
        // Per ECMA-262 27.3 / 14.5, the completed value of a generator is the return
        // expression's result; we store it in the Current field so callers can read
        // it via IEnumerator.Current after MoveNext returns false.
        if (r.Value != null)
        {
            EmitExpression(r.Value);
            EnsureBoxed();
            var returnValueTemp = _il.DeclareLocal(typeof(object));
            _il.Emit(OpCodes.Stloc, returnValueTemp);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloc, returnValueTemp);
            _il.Emit(OpCodes.Stfld, _builder.CurrentField);
        }

        // Generator return - set state to completed and return false
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, -2);
        _il.Emit(OpCodes.Stfld, _builder.StateField);
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Ret);
    }

    protected override void EmitTryCatch(Stmt.TryCatch t)
    {
        _il.BeginExceptionBlock();

        foreach (var stmt in t.TryBlock)
            EmitStatement(stmt);

        if (t.CatchBlock != null)
        {
            _il.BeginCatchBlock(typeof(Exception));

            if (t.CatchParam != null)
            {
                var exLocal = _il.DeclareLocal(typeof(object));
                _ctx!.Locals.RegisterLocal(t.CatchParam.Lexeme, exLocal);
                _il.Emit(OpCodes.Call, _ctx.Runtime!.WrapException);
                _il.Emit(OpCodes.Stloc, exLocal);
            }
            else
            {
                _il.Emit(OpCodes.Pop);
            }

            foreach (var stmt in t.CatchBlock)
                EmitStatement(stmt);
        }

        if (t.FinallyBlock != null)
        {
            _il.BeginFinallyBlock();
            foreach (var stmt in t.FinallyBlock)
                EmitStatement(stmt);
        }

        _il.EndExceptionBlock();
    }

    #endregion

    #region For...Of Loop Override (Hoisted Enumerator Support)

    /// <summary>
    /// Emits a for...of loop with hoisted enumerator support.
    /// When the loop contains yield statements, the enumerator is stored in a state machine field
    /// so it persists across yield boundaries.
    /// </summary>
    protected override void EmitForOf(Stmt.ForOf f)
    {
        // Check if this loop needs a hoisted enumerator
        var enumeratorField = _builder.GetEnumeratorField(f);

        if (enumeratorField == null)
        {
            // No yield inside this loop - use base implementation with local enumerator
            base.EmitForOf(f);
            return;
        }

        // Loop contains yield - use hoisted enumerator field
        var startLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();
        var continueLabel = _il.DefineLabel();

        // Get compile-time type info for the iterable expression
        TypeInfo? iterableType = _ctx!.TypeMap?.Get(f.Iterable);

        // Emit iterable and get enumerator
        EmitExpression(f.Iterable);
        EnsureBoxed();

        // Handle Map/Set specially - convert to List before iteration
        // This matches the behavior in ILEmitter.Statements.cs EmitForOf
        if (iterableType is TypeInfo.Map)
        {
            // Map iteration yields [key, value] entries (compile-time known)
            _il.Emit(OpCodes.Call, _ctx.Runtime!.MapEntries);
        }
        else if (iterableType is TypeInfo.Set)
        {
            // Set iteration yields values (compile-time known)
            _il.Emit(OpCodes.Call, _ctx.Runtime!.SetValues);
        }
        else
        {
            // Fallback: runtime type checking for Map/Set when compile-time
            // type isn't available. Each arm gates on whether the corresponding
            // $Runtime helper was emitted (null MethodBuilder ⇒ feature off).
            var hasMapEntries = _ctx.Runtime?.MapEntries != null;
            var hasSetValues = _ctx.Runtime?.SetValues != null;
            var iterableLocal = _il.DeclareLocal(_types.Object);
            _il.Emit(OpCodes.Stloc, iterableLocal);

            if (hasMapEntries || hasSetValues)
            {
                var afterMapSetLabel = _il.DefineLabel();
                var checkSetLabel = _il.DefineLabel();
                var dictionaryType = typeof(Dictionary<object, object?>);
                var hashSetType = typeof(HashSet<object>);

                if (hasMapEntries)
                {
                    // Check if iterable is Dictionary<object, object?> (Map)
                    _il.Emit(OpCodes.Ldloc, iterableLocal);
                    _il.Emit(OpCodes.Isinst, dictionaryType);
                    _il.Emit(OpCodes.Brfalse, checkSetLabel);

                    // It's a Map - call MapEntries to get List<object?>
                    _il.Emit(OpCodes.Ldloc, iterableLocal);
                    _il.Emit(OpCodes.Call, _ctx.Runtime!.MapEntries);
                    _il.Emit(OpCodes.Stloc, iterableLocal);
                    _il.Emit(OpCodes.Br, afterMapSetLabel);
                }

                if (hasSetValues)
                {
                    _il.MarkLabel(checkSetLabel);
                    _il.Emit(OpCodes.Ldloc, iterableLocal);
                    _il.Emit(OpCodes.Isinst, hashSetType);
                    _il.Emit(OpCodes.Brfalse, afterMapSetLabel);

                    // It's a Set - call SetValues to get List<object?>
                    _il.Emit(OpCodes.Ldloc, iterableLocal);
                    _il.Emit(OpCodes.Call, _ctx.Runtime!.SetValues);
                    _il.Emit(OpCodes.Stloc, iterableLocal);
                }
                else
                {
                    // Map-only: still need checkSetLabel as the Map arm's Brfalse target.
                    _il.MarkLabel(checkSetLabel);
                }

                _il.MarkLabel(afterMapSetLabel);
            }
            _il.Emit(OpCodes.Ldloc, iterableLocal);
        }

        // Get the enumerator from the (possibly converted) iterable
        _il.Emit(OpCodes.Castclass, _types.IEnumerable);
        _il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IEnumerable, "GetEnumerator"));

        // Store enumerator to hoisted field (need temp local for the stack swap)
        var tempLocal = _il.DeclareLocal(_types.IEnumerator);
        _il.Emit(OpCodes.Stloc, tempLocal);
        _il.Emit(OpCodes.Ldarg_0);  // this
        _il.Emit(OpCodes.Ldloc, tempLocal);
        _il.Emit(OpCodes.Stfld, enumeratorField);

        EnterLoop(endLabel, continueLabel);

        // Declare loop variable (may be hoisted or local)
        var loopVarLocal = DeclareLoopVariable(f.Variable.Lexeme);

        _il.MarkLabel(startLabel);

        // Check MoveNext - load enumerator from hoisted field
        _il.Emit(OpCodes.Ldarg_0);  // this
        _il.Emit(OpCodes.Ldfld, enumeratorField);
        _il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IEnumerator, "MoveNext"));
        _il.Emit(OpCodes.Brfalse, endLabel);

        // Set loop variable from Current
        EmitStoreLoopVariable(loopVarLocal, f.Variable.Lexeme, () =>
        {
            _il.Emit(OpCodes.Ldarg_0);  // this
            _il.Emit(OpCodes.Ldfld, enumeratorField);
            _il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.IEnumerator, "Current"));
        });

        // Emit body
        EmitStatement(f.Body);

        _il.MarkLabel(continueLabel);
        _il.Emit(OpCodes.Br, startLabel);

        _il.MarkLabel(endLabel);
        ExitLoop();
    }

    #endregion

    // Note: The following methods are inherited from StatementEmitterBase:
    // - EmitStatement (dispatch)
    // - EmitIf, EmitWhile, EmitDoWhile (control flow)
    // - EmitForIn (loops with DeclareLoopVariable/EmitStoreLoopVariable overrides)
    // - EmitBlock, EmitBreak, EmitContinue, EmitLabeledStatement
    // - EmitSwitch, EmitThrow, EmitPrint
}
