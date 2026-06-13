using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncGeneratorMoveNextEmitter
{
    // EmitStatement: inherited from StatementEmitterBase (handles all statement types
    // including Using, DeclareModule, DeclareGlobal that were previously missing here)

    protected override FieldBuilder? GetHoistedVariableField(string name) => _builder.GetVariableField(name);

    protected override void EmitReturn(Stmt.Return r)
    {
        // Async generator return - store the return value in Current (read as the completion value),
        // then complete the state machine — unless an enclosing flag-based try/finally must run first.
        if (r.Value != null)
        {
            // Evaluate fully before touching the frame: the value may itself contain a yield/await
            // whose suspension `ret`s out, which is only legal with an empty evaluation stack (so the
            // `this` for the Stfld is loaded only after the value is in a local).
            EmitExpression(r.Value);
            EnsureBoxed();
            var returnValueTemp = _il.DeclareLocal(typeof(object));
            _il.Emit(OpCodes.Stloc, returnValueTemp);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloc, returnValueTemp);
            _il.Emit(OpCodes.Stfld, _builder.CurrentField);
        }

        // Set state to -2 (completed). The direct path below returns immediately; a routed return
        // re-asserts this at its terminal, since a yielding finally would overwrite it.
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, -2);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // Inside a flag-based try/finally, the enclosing finally(s) must run before the generator
        // actually completes. Route the return through them instead of returning directly (a `ret`
        // here is also illegal — this return is emitted outside the protected segment, but a finally
        // is emitted after it). See AsyncGeneratorMoveNextEmitter.Statements.TryCatch.cs.
        if (_protectedRegionDepth == 0)
        {
            var chain = ActiveFinallyFrames();
            if (chain.Count > 0)
            {
                RegisterReturnTerminal();
                RouteThroughFinallys(chain, ExitCodeReturn);
                return;
            }
        }

        EmitReturnValueTaskBool(false);
    }

    // EmitIf, EmitWhile: inherited from StatementEmitterBase (identical logic)

    protected override void EmitForOf(Stmt.ForOf f)
    {
        if (f.IsAsync)
        {
            EmitForAwaitOf(f);
            return;
        }

        // Check if this loop needs a hoisted enumerator (contains yield/await)
        var enumeratorField = _builder.GetEnumeratorField(f);

        if (enumeratorField == null)
        {
            // No suspension inside this loop - delegate to base (uses
            // DeclareLoopVariable/EmitStoreLoopVariable overrides for hoisted fields)
            base.EmitForOf(f);
            return;
        }

        // Loop contains yield/await - use hoisted enumerator field
        EmitForOfWithHoistedEnumerator(f, enumeratorField);
    }

    private void EmitForOfWithHoistedEnumerator(Stmt.ForOf f, FieldBuilder enumeratorField)
    {
        // For...of loop with hoisted enumerator (contains yield/await)
        // The enumerator is stored in a state machine field so it persists across suspension boundaries
        string varName = f.Variable.Lexeme;
        var varField = _builder.GetVariableField(varName);

        var getEnumerator = typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!;
        var moveNext = typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!;
        var current = typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!;

        // Emit the iterable and get enumerator
        EmitExpression(f.Iterable);
        EnsureBoxed();
        _il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
        _il.Emit(OpCodes.Callvirt, getEnumerator);

        // Store enumerator to hoisted field (need temp local for the stack swap)
        var tempLocal = _il.DeclareLocal(typeof(System.Collections.IEnumerator));
        _il.Emit(OpCodes.Stloc, tempLocal);
        _il.Emit(OpCodes.Ldarg_0);  // this
        _il.Emit(OpCodes.Ldloc, tempLocal);
        _il.Emit(OpCodes.Stfld, enumeratorField);

        var startLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();
        var continueLabel = _il.DefineLabel();

        EnterLoop(endLabel, continueLabel);

        _il.MarkLabel(startLabel);

        // Check MoveNext - load enumerator from hoisted field
        _il.Emit(OpCodes.Ldarg_0);  // this
        _il.Emit(OpCodes.Ldfld, enumeratorField);
        _il.Emit(OpCodes.Callvirt, moveNext);
        _il.Emit(OpCodes.Brfalse, endLabel);

        // Set loop variable from Current (loaded from hoisted enumerator field)
        if (varField != null)
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldarg_0);  // this for enumerator field
            _il.Emit(OpCodes.Ldfld, enumeratorField);
            _il.Emit(OpCodes.Callvirt, current);
            _il.Emit(OpCodes.Stfld, varField);
        }
        else
        {
            var varLocal = _il.DeclareLocal(typeof(object));
            _ctx!.Locals.RegisterLocal(varName, varLocal);
            _il.Emit(OpCodes.Ldarg_0);  // this for enumerator field
            _il.Emit(OpCodes.Ldfld, enumeratorField);
            _il.Emit(OpCodes.Callvirt, current);
            _il.Emit(OpCodes.Stloc, varLocal);
        }

        EmitStatement(f.Body);

        _il.MarkLabel(continueLabel);
        _il.Emit(OpCodes.Br, startLabel);

        _il.MarkLabel(endLabel);
        ExitLoop();
    }

    private void EmitForAwaitOf(Stmt.ForOf f)
    {
        // for await...of iterates over async iterables
        // We use the $IAsyncGenerator.next() method which returns Task<object>
        // The result is a dictionary with { value, done } properties

        string varName = f.Variable.Lexeme;
        var varField = _builder.GetVariableField(varName);

        // Emit the async iterable expression
        EmitExpression(f.Iterable);
        EnsureBoxed();

        // Cast to $IAsyncGenerator interface
        var asyncGenInterface = _ctx!.Runtime!.AsyncGeneratorInterfaceType;
        _il.Emit(OpCodes.Castclass, asyncGenInterface);

        // Store the async generator in a local
        var asyncGenLocal = _il.DeclareLocal(asyncGenInterface);
        _il.Emit(OpCodes.Stloc, asyncGenLocal);

        var startLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();
        var cleanupLabel = _il.DefineLabel();
        var continueLabel = _il.DefineLabel();

        // Break goes to cleanup (calls generator.return()), not directly to end
        EnterLoop(cleanupLabel, continueLabel);

        _il.MarkLabel(startLabel);

        // Call next() which returns Task<object>
        _il.Emit(OpCodes.Ldloc, asyncGenLocal);
        _il.Emit(OpCodes.Callvirt, _ctx.Runtime.AsyncGeneratorNextMethod);

        // Await the Task<object> - get result synchronously for now
        // (full async continuation would require state machine suspension)
        var taskLocal = _il.DeclareLocal(_types.TaskOfObject);
        _il.Emit(OpCodes.Stloc, taskLocal);
        _il.Emit(OpCodes.Ldloc, taskLocal);
        var getAwaiter = _types.GetMethodNoParams(_types.TaskOfObject, "GetAwaiter");
        _il.Emit(OpCodes.Call, getAwaiter);
        var awaiterLocal = _il.DeclareLocal(_types.TaskAwaiterOfObject);
        _il.Emit(OpCodes.Stloc, awaiterLocal);

        // Result local for storing the result
        var resultLocal = _il.DeclareLocal(_types.Object);
        var getResultSuccessLabel = _il.DefineLabel();

        // Wrap GetResult() in try-catch for proper error propagation from rejected promises
        _il.BeginExceptionBlock();

        _il.Emit(OpCodes.Ldloca, awaiterLocal);
        var getResult = _types.GetMethodNoParams(_types.TaskAwaiterOfObject, "GetResult");
        _il.Emit(OpCodes.Call, getResult);
        _il.Emit(OpCodes.Stloc, resultLocal);
        _il.Emit(OpCodes.Leave, getResultSuccessLabel);

        _il.BeginCatchBlock(typeof(Exception));
        // Re-throw wrapped exception for proper propagation
        _il.Emit(OpCodes.Call, _ctx.Runtime.WrapException);
        _il.Emit(OpCodes.Call, _ctx.Runtime.CreateException);
        _il.Emit(OpCodes.Throw);
        _il.EndExceptionBlock();

        _il.MarkLabel(getResultSuccessLabel);

        // Result is a Dictionary<string, object> with { value, done }
        _il.Emit(OpCodes.Stloc, resultLocal);

        // Check if done: GetProperty(result, "done")
        // IMPORTANT: Use strict boolean check, not IsTruthy - IsTruthy treats 0 as falsy
        // which would incorrectly end the loop when yielding zero
        _il.Emit(OpCodes.Ldloc, resultLocal);
        _il.Emit(OpCodes.Ldstr, "done");
        _il.Emit(OpCodes.Call, _ctx.Runtime.GetProperty);

        // Check if it's a boxed bool true (strict check)
        var notDoneLabel = _il.DefineLabel();
        _il.Emit(OpCodes.Isinst, typeof(bool));
        _il.Emit(OpCodes.Brfalse, notDoneLabel);  // Not a bool - continue loop

        // It's a bool - unbox and check if true
        _il.Emit(OpCodes.Ldloc, resultLocal);
        _il.Emit(OpCodes.Ldstr, "done");
        _il.Emit(OpCodes.Call, _ctx.Runtime.GetProperty);
        _il.Emit(OpCodes.Unbox_Any, typeof(bool));
        _il.Emit(OpCodes.Brtrue, endLabel);  // done === true -> exit loop (no cleanup needed)

        _il.MarkLabel(notDoneLabel);

        // Get value: GetProperty(result, "value")
        _il.Emit(OpCodes.Ldloc, resultLocal);
        _il.Emit(OpCodes.Ldstr, "value");
        _il.Emit(OpCodes.Call, _ctx.Runtime.GetProperty);

        // Assign to loop variable
        if (varField != null)
        {
            var valueTemp = _il.DeclareLocal(_types.Object);
            _il.Emit(OpCodes.Stloc, valueTemp);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloc, valueTemp);
            _il.Emit(OpCodes.Stfld, varField);
        }
        else
        {
            var varLocal = _il.DeclareLocal(_types.Object);
            _ctx.Locals.RegisterLocal(varName, varLocal);
            _il.Emit(OpCodes.Stloc, varLocal);
        }

        EmitStatement(f.Body);

        _il.MarkLabel(continueLabel);
        _il.Emit(OpCodes.Br, startLabel);

        // Cleanup on break: call generator.return(null) to trigger finally blocks
        _il.MarkLabel(cleanupLabel);
        _il.Emit(OpCodes.Ldloc, asyncGenLocal);
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Callvirt, _ctx.Runtime.AsyncGeneratorReturnMethod);
        // Await the Task<object> result and discard it
        var cleanupTaskLocal = _il.DeclareLocal(_types.TaskOfObject);
        _il.Emit(OpCodes.Stloc, cleanupTaskLocal);
        _il.Emit(OpCodes.Ldloc, cleanupTaskLocal);
        _il.Emit(OpCodes.Call, getAwaiter);
        var cleanupAwaiterLocal = _il.DeclareLocal(_types.TaskAwaiterOfObject);
        _il.Emit(OpCodes.Stloc, cleanupAwaiterLocal);
        _il.Emit(OpCodes.Ldloca, cleanupAwaiterLocal);
        _il.Emit(OpCodes.Call, getResult);
        _il.Emit(OpCodes.Pop);
        _il.Emit(OpCodes.Br, endLabel);

        _il.MarkLabel(endLabel);
        ExitLoop();
    }

    // EmitPrint, EmitDoWhile, EmitForIn, EmitSwitch:
    // inherited from StatementEmitterBase (identical logic; EmitForIn uses
    // DeclareLoopVariable/EmitStoreLoopVariable overrides for hoisted fields)
    // Note: base EmitSwitch also fixes a bug where labeled breaks inside switch
    // cases were incorrectly treated as switch breaks.
    //
    // EmitReturn (above), EmitBreak/EmitContinue/EmitThrow, the loop-scope methods, and the
    // try/catch emission all live in AsyncGeneratorMoveNextEmitter.Statements.TryCatch.cs — they
    // share the unified exit-scope stack that routes non-local exits through enclosing finallys (#559).

    protected override void EmitBranchToLabel(Label target)
    {
        // Use Leave instead of Br when inside exception-protected regions
        if (_ctx!.ExceptionBlockDepth > 0)
            _il.Emit(OpCodes.Leave, target);
        else
            _il.Emit(OpCodes.Br, target);
    }
}
