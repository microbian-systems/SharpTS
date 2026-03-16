using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncGeneratorMoveNextEmitter
{
    public override void EmitStatement(Stmt stmt)
    {
        switch (stmt)
        {
            case Stmt.Expression e:
                EmitExpression(e.Expr);
                _il.Emit(OpCodes.Pop);
                break;

            case Stmt.Var v:
                EmitVarDeclaration(v);
                break;

            case Stmt.Const c:
                EmitVarDeclaration(new Stmt.Var(c.Name, c.TypeAnnotation, c.Initializer));
                break;

            case Stmt.Return r:
                EmitReturn(r);
                break;

            case Stmt.If i:
                EmitIf(i);
                break;

            case Stmt.While w:
                EmitWhile(w);
                break;

            case Stmt.Block b:
                if (b.Statements != null)
                    foreach (var s in b.Statements)
                        EmitStatement(s);
                break;

            case Stmt.Sequence seq:
                foreach (var s in seq.Statements)
                    EmitStatement(s);
                break;

            case Stmt.Print p:
                EmitPrint(p);
                break;

            case Stmt.ForOf f:
                EmitForOf(f);
                break;

            case Stmt.DoWhile dw:
                EmitDoWhile(dw);
                break;

            case Stmt.For f:
                EmitFor(f);
                break;

            case Stmt.ForIn fi:
                EmitForIn(fi);
                break;

            case Stmt.Throw t:
                EmitThrow(t);
                break;

            case Stmt.Switch s:
                EmitSwitch(s);
                break;

            case Stmt.TryCatch tc:
                EmitTryCatch(tc);
                break;

            case Stmt.Break b:
                EmitBreak(b);
                break;

            case Stmt.Continue c:
                EmitContinue(c);
                break;

            case Stmt.LabeledStatement ls:
                EmitLabeledStatement(ls);
                break;

            default:
                break;
        }
    }

    protected override FieldBuilder? GetHoistedVariableField(string name) => _builder.GetVariableField(name);

    protected override void EmitReturn(Stmt.Return r)
    {
        // Async generator return - store return value in Current and set state to completed
        // The return value will be available in the {value: returnValue, done: true} result
        if (r.Value != null)
        {
            // Evaluate return value and store in CurrentField
            _il.Emit(OpCodes.Ldarg_0);
            EmitExpression(r.Value);
            EnsureBoxed();
            _il.Emit(OpCodes.Stfld, _builder.CurrentField);
        }

        // Set state to -2 (completed)
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, -2);
        _il.Emit(OpCodes.Stfld, _builder.StateField);
        EmitReturnValueTaskBool(false);
    }

    protected override void EmitIf(Stmt.If i)
    {
        var elseLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();

        EmitExpression(i.Condition);
        EnsureBoxed();
        EmitTruthyCheck();
        _il.Emit(OpCodes.Brfalse, elseLabel);

        EmitStatement(i.ThenBranch);
        _il.Emit(OpCodes.Br, endLabel);

        _il.MarkLabel(elseLabel);
        if (i.ElseBranch != null)
            EmitStatement(i.ElseBranch);

        _il.MarkLabel(endLabel);
    }

    protected override void EmitWhile(Stmt.While w)
    {
        var startLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();
        var continueLabel = _il.DefineLabel();

        EnterLoop(endLabel, continueLabel);

        _il.MarkLabel(startLabel);
        EmitExpression(w.Condition);
        EnsureBoxed();
        EmitTruthyCheck();
        _il.Emit(OpCodes.Brfalse, endLabel);

        EmitStatement(w.Body);

        _il.MarkLabel(continueLabel);
        _il.Emit(OpCodes.Br, startLabel);

        _il.MarkLabel(endLabel);
        ExitLoop();
    }

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
            // No suspension inside this loop - use local enumerator
            EmitForOfWithLocalEnumerator(f);
            return;
        }

        // Loop contains yield/await - use hoisted enumerator field
        EmitForOfWithHoistedEnumerator(f, enumeratorField);
    }

    private void EmitForOfWithLocalEnumerator(Stmt.ForOf f)
    {
        string varName = f.Variable.Lexeme;
        var varField = _builder.GetVariableField(varName);

        EmitExpression(f.Iterable);
        EnsureBoxed();

        var getEnumerator = typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!;
        var moveNext = typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!;
        var current = typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!;

        _il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
        _il.Emit(OpCodes.Callvirt, getEnumerator);

        var enumLocal = _il.DeclareLocal(typeof(System.Collections.IEnumerator));
        _il.Emit(OpCodes.Stloc, enumLocal);

        var startLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();
        var continueLabel = _il.DefineLabel();

        EnterLoop(endLabel, continueLabel);

        _il.MarkLabel(startLabel);
        _il.Emit(OpCodes.Ldloc, enumLocal);
        _il.Emit(OpCodes.Callvirt, moveNext);
        _il.Emit(OpCodes.Brfalse, endLabel);

        if (varField != null)
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloc, enumLocal);
            _il.Emit(OpCodes.Callvirt, current);
            _il.Emit(OpCodes.Stfld, varField);
        }
        else
        {
            var varLocal = _il.DeclareLocal(typeof(object));
            _ctx!.Locals.RegisterLocal(varName, varLocal);
            _il.Emit(OpCodes.Ldloc, enumLocal);
            _il.Emit(OpCodes.Callvirt, current);
            _il.Emit(OpCodes.Stloc, varLocal);
        }

        EmitStatement(f.Body);

        _il.MarkLabel(continueLabel);
        _il.Emit(OpCodes.Br, startLabel);

        _il.MarkLabel(endLabel);
        ExitLoop();
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
        var continueLabel = _il.DefineLabel();

        EnterLoop(endLabel, continueLabel);

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
        _il.Emit(OpCodes.Brtrue, endLabel);  // done === true -> exit loop

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

        _il.MarkLabel(endLabel);
        ExitLoop();
    }

    protected override void EmitPrint(Stmt.Print p)
    {
        EmitExpression(p.Expr);
        EnsureBoxed();
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.ConsoleLog);
    }

    protected override void EmitDoWhile(Stmt.DoWhile dw)
    {
        var startLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();
        var continueLabel = _il.DefineLabel();

        EnterLoop(endLabel, continueLabel);

        _il.MarkLabel(startLabel);
        EmitStatement(dw.Body);

        _il.MarkLabel(continueLabel);

        EmitExpression(dw.Condition);
        EnsureBoxed();
        EmitTruthyCheck();
        _il.Emit(OpCodes.Brtrue, startLabel);

        _il.MarkLabel(endLabel);
        ExitLoop();
    }

    protected override void EmitForIn(Stmt.ForIn f)
    {
        var startLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();
        var continueLabel = _il.DefineLabel();

        string varName = f.Variable.Lexeme;
        var varField = _builder.GetVariableField(varName);

        EmitExpression(f.Object);
        EnsureBoxed();
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.GetKeys);
        var keysLocal = _il.DeclareLocal(typeof(List<object>));
        _il.Emit(OpCodes.Stloc, keysLocal);

        var indexLocal = _il.DeclareLocal(typeof(int));
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Stloc, indexLocal);

        LocalBuilder? loopVar = null;
        if (varField == null)
        {
            loopVar = _il.DeclareLocal(typeof(object));
            _ctx!.Locals.RegisterLocal(varName, loopVar);
        }

        EnterLoop(endLabel, continueLabel);

        _il.MarkLabel(startLabel);

        _il.Emit(OpCodes.Ldloc, indexLocal);
        _il.Emit(OpCodes.Ldloc, keysLocal);
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.GetLength);
        _il.Emit(OpCodes.Clt);
        _il.Emit(OpCodes.Brfalse, endLabel);

        _il.Emit(OpCodes.Ldloc, keysLocal);
        _il.Emit(OpCodes.Ldloc, indexLocal);
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.GetElement);

        if (varField != null)
        {
            var keyTemp = _il.DeclareLocal(typeof(object));
            _il.Emit(OpCodes.Stloc, keyTemp);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloc, keyTemp);
            _il.Emit(OpCodes.Stfld, varField);
        }
        else
        {
            _il.Emit(OpCodes.Stloc, loopVar!);
        }

        EmitStatement(f.Body);

        _il.MarkLabel(continueLabel);

        _il.Emit(OpCodes.Ldloc, indexLocal);
        _il.Emit(OpCodes.Ldc_I4_1);
        _il.Emit(OpCodes.Add);
        _il.Emit(OpCodes.Stloc, indexLocal);

        _il.Emit(OpCodes.Br, startLabel);

        _il.MarkLabel(endLabel);
        ExitLoop();
    }

    protected override void EmitThrow(Stmt.Throw t)
    {
        EmitExpression(t.Value);
        EnsureBoxed();
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.CreateException);
        _il.Emit(OpCodes.Throw);
    }

    protected override void EmitSwitch(Stmt.Switch s)
    {
        var endLabel = _il.DefineLabel();
        var defaultLabel = _il.DefineLabel();
        var caseLabels = s.Cases.Select(_ => _il.DefineLabel()).ToList();

        EmitExpression(s.Subject);
        EnsureBoxed();
        var subjectLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, subjectLocal);

        for (int i = 0; i < s.Cases.Count; i++)
        {
            _il.Emit(OpCodes.Ldloc, subjectLocal);
            EmitExpression(s.Cases[i].Value);
            EnsureBoxed();
            _il.Emit(OpCodes.Call, _ctx!.Runtime!.Equals);
            _il.Emit(OpCodes.Brtrue, caseLabels[i]);
        }

        if (s.DefaultBody == null)
            _il.Emit(OpCodes.Br, endLabel);
        else
            _il.Emit(OpCodes.Br, defaultLabel);

        for (int i = 0; i < s.Cases.Count; i++)
        {
            _il.MarkLabel(caseLabels[i]);
            foreach (var stmt in s.Cases[i].Body)
            {
                if (stmt is Stmt.Break)
                    _il.Emit(OpCodes.Br, endLabel);
                else
                    EmitStatement(stmt);
            }
        }

        if (s.DefaultBody != null)
        {
            _il.MarkLabel(defaultLabel);
            foreach (var stmt in s.DefaultBody)
            {
                if (stmt is Stmt.Break)
                    _il.Emit(OpCodes.Br, endLabel);
                else
                    EmitStatement(stmt);
            }
        }

        _il.MarkLabel(endLabel);
    }

    protected override void EmitBranchToLabel(Label target)
    {
        // Use Leave instead of Br when inside exception-protected regions
        if (_ctx!.ExceptionBlockDepth > 0)
            _il.Emit(OpCodes.Leave, target);
        else
            _il.Emit(OpCodes.Br, target);
    }

    protected override void EmitTryCatch(Stmt.TryCatch t)
    {
        // Check if this try block contains any suspension points (yield or await)
        bool hasSuspensionsInTry = ContainsSuspension(t.TryBlock);
        bool hasSuspensionsInCatch = t.CatchBlock != null && ContainsSuspension(t.CatchBlock);
        bool hasSuspensionsInFinally = t.FinallyBlock != null && ContainsSuspension(t.FinallyBlock);

        if (hasSuspensionsInTry || hasSuspensionsInCatch || hasSuspensionsInFinally)
        {
            // Cannot use IL exception blocks when suspension points exist inside them
            // because: (1) state switch can't jump into protected regions,
            // (2) yield/return can't use 'ret' inside try blocks,
            // (3) 'leave' from try/finally would trigger finally prematurely on yield.
            // Use flag-based exception tracking instead.
            EmitTryCatchWithSuspensions(t);
        }
        else
        {
            // No suspension points - safe to use IL exception blocks
            EmitSimpleTryCatch(t);
        }
    }

    private void EmitSimpleTryCatch(Stmt.TryCatch t)
    {
        _ctx!.ExceptionBlockDepth++;
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
        _ctx!.ExceptionBlockDepth--;
    }

    private void EmitTryCatchWithSuspensions(Stmt.TryCatch t)
    {
        // Flag-based exception tracking: instead of IL exception blocks,
        // wrap synchronous segments in mini try/catch blocks and track
        // exceptions via a local variable.
        var caughtExceptionLocal = _il.DeclareLocal(typeof(object));
        var afterTryBodyLabel = _il.DefineLabel();
        var afterTryCatchLabel = _il.DefineLabel();

        // Initialize caught exception to null
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Stloc, caughtExceptionLocal);

        // Emit try body with segmented exception handling
        EmitTryBodyWithSuspensions(t.TryBlock, caughtExceptionLocal, afterTryBodyLabel);

        _il.MarkLabel(afterTryBodyLabel);

        // Handle catch block
        if (t.CatchBlock != null)
        {
            var skipCatchLabel = _il.DefineLabel();
            _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
            _il.Emit(OpCodes.Brfalse, skipCatchLabel);

            // Store exception in catch param if needed
            if (t.CatchParam != null)
            {
                var exLocal = _il.DeclareLocal(typeof(object));
                _ctx!.Locals.RegisterLocal(t.CatchParam.Lexeme, exLocal);
                _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
                _il.Emit(OpCodes.Stloc, exLocal);
            }

            // Clear the exception since catch handles it
            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Stloc, caughtExceptionLocal);

            // Emit catch block statements
            foreach (var stmt in t.CatchBlock)
                EmitStatement(stmt);

            _il.MarkLabel(skipCatchLabel);
        }

        // Handle finally block - always runs
        if (t.FinallyBlock != null)
        {
            foreach (var stmt in t.FinallyBlock)
                EmitStatement(stmt);

            // After finally, rethrow if exception was not handled by catch
            if (t.CatchBlock == null)
            {
                var noExceptionLabel = _il.DefineLabel();
                _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
                _il.Emit(OpCodes.Brfalse, noExceptionLabel);

                // Rethrow the pending exception
                _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.CreateException);
                _il.Emit(OpCodes.Throw);

                _il.MarkLabel(noExceptionLabel);
            }
        }

        _il.MarkLabel(afterTryCatchLabel);
    }

    private void EmitTryBodyWithSuspensions(List<Stmt> tryBody, LocalBuilder caughtExceptionLocal, Label afterTryLabel)
    {
        // Walk through try body statements. Synchronous segments (no yield/await)
        // are wrapped in mini try/catch blocks. Suspension-containing statements
        // are emitted normally (outside any IL exception block) so the state switch
        // can reach their resume labels.
        List<Stmt> syncSegment = [];

        foreach (var stmt in tryBody)
        {
            if (ContainsSuspensionInStmt(stmt))
            {
                // Emit accumulated sync statements in mini try/catch
                if (syncSegment.Count > 0)
                {
                    EmitSyncSegmentInTry(syncSegment, caughtExceptionLocal);
                    syncSegment.Clear();
                }

                // Check exception before continuing with suspension
                _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
                _il.Emit(OpCodes.Brtrue, afterTryLabel);

                // Emit the suspension-containing statement normally
                EmitStatement(stmt);
            }
            else
            {
                syncSegment.Add(stmt);
            }
        }

        // Emit remaining sync statements
        if (syncSegment.Count > 0)
        {
            EmitSyncSegmentInTry(syncSegment, caughtExceptionLocal);
        }
    }

    private void EmitSyncSegmentInTry(List<Stmt> statements, LocalBuilder caughtExceptionLocal)
    {
        // Skip if exception already caught
        var skipLabel = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, caughtExceptionLocal);
        _il.Emit(OpCodes.Brtrue, skipLabel);

        // Wrap sync statements in a mini try/catch
        _il.BeginExceptionBlock();
        foreach (var stmt in statements)
            EmitStatement(stmt);

        _il.BeginCatchBlock(typeof(Exception));
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.WrapException);
        _il.Emit(OpCodes.Stloc, caughtExceptionLocal);

        _il.EndExceptionBlock();

        _il.MarkLabel(skipLabel);
    }

    #region Suspension Detection

    private static bool ContainsSuspension(List<Stmt> statements)
    {
        foreach (var stmt in statements)
        {
            if (ContainsSuspensionInStmt(stmt))
                return true;
        }
        return false;
    }

    private static bool ContainsSuspensionInStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case Stmt.Expression e:
                return ContainsSuspensionInExpr(e.Expr);
            case Stmt.Var v:
                return v.Initializer != null && ContainsSuspensionInExpr(v.Initializer);
            case Stmt.Const c:
                return ContainsSuspensionInExpr(c.Initializer);
            case Stmt.Return r:
                return r.Value != null && ContainsSuspensionInExpr(r.Value);
            case Stmt.If i:
                return ContainsSuspensionInExpr(i.Condition) ||
                       ContainsSuspensionInStmt(i.ThenBranch) ||
                       (i.ElseBranch != null && ContainsSuspensionInStmt(i.ElseBranch));
            case Stmt.While w:
                return ContainsSuspensionInExpr(w.Condition) || ContainsSuspensionInStmt(w.Body);
            case Stmt.DoWhile dw:
                return ContainsSuspensionInStmt(dw.Body) || ContainsSuspensionInExpr(dw.Condition);
            case Stmt.For f:
                return (f.Initializer != null && ContainsSuspensionInStmt(f.Initializer)) ||
                       (f.Condition != null && ContainsSuspensionInExpr(f.Condition)) ||
                       (f.Increment != null && ContainsSuspensionInExpr(f.Increment)) ||
                       ContainsSuspensionInStmt(f.Body);
            case Stmt.ForOf fo:
                return ContainsSuspensionInExpr(fo.Iterable) || ContainsSuspensionInStmt(fo.Body);
            case Stmt.ForIn fi:
                return ContainsSuspensionInExpr(fi.Object) || ContainsSuspensionInStmt(fi.Body);
            case Stmt.Block b:
                return b.Statements != null && ContainsSuspension(b.Statements);
            case Stmt.Sequence seq:
                return ContainsSuspension(seq.Statements);
            case Stmt.TryCatch t:
                return ContainsSuspension(t.TryBlock) ||
                       (t.CatchBlock != null && ContainsSuspension(t.CatchBlock)) ||
                       (t.FinallyBlock != null && ContainsSuspension(t.FinallyBlock));
            default:
                return false;
        }
    }

    private static bool ContainsSuspensionInExpr(Expr expr)
    {
        switch (expr)
        {
            case Expr.Yield:
            case Expr.Await:
                return true;
            case Expr.Comma c:
                return ContainsSuspensionInExpr(c.Left) || ContainsSuspensionInExpr(c.Right);
            case Expr.Binary b:
                return ContainsSuspensionInExpr(b.Left) || ContainsSuspensionInExpr(b.Right);
            case Expr.Logical l:
                return ContainsSuspensionInExpr(l.Left) || ContainsSuspensionInExpr(l.Right);
            case Expr.Unary u:
                return ContainsSuspensionInExpr(u.Right);
            case Expr.Delete d:
                return ContainsSuspensionInExpr(d.Operand);
            case Expr.Grouping g:
                return ContainsSuspensionInExpr(g.Expression);
            case Expr.Call c:
                if (ContainsSuspensionInExpr(c.Callee)) return true;
                foreach (var arg in c.Arguments)
                    if (ContainsSuspensionInExpr(arg)) return true;
                return false;
            case Expr.Assign a:
                return ContainsSuspensionInExpr(a.Value);
            case Expr.Ternary t:
                return ContainsSuspensionInExpr(t.Condition) ||
                       ContainsSuspensionInExpr(t.ThenBranch) ||
                       ContainsSuspensionInExpr(t.ElseBranch);
            case Expr.Get g:
                return ContainsSuspensionInExpr(g.Object);
            case Expr.Set s:
                return ContainsSuspensionInExpr(s.Object) || ContainsSuspensionInExpr(s.Value);
            default:
                return false;
        }
    }

    #endregion

}
