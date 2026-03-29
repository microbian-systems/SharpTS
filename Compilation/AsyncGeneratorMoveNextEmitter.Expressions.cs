using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncGeneratorMoveNextEmitter
{
    // EmitExpression dispatch is inherited from ExpressionEmitterBase

    #region Yield Expressions

    protected override void EmitYield(Expr.Yield y)
    {
        int stateNumber = _currentSuspensionState++;
        var resumeLabel = _stateLabels[stateNumber];

        // Handle yield* delegation
        if (y.IsDelegating && y.Value != null)
        {
            EmitYieldStar(y, stateNumber, resumeLabel);
            return;
        }

        // 1. Emit the yield value (or null if no value)
        if (y.Value != null)
        {
            EmitExpression(y.Value);
            EnsureBoxed();
        }
        else
        {
            _il.Emit(OpCodes.Ldnull);
        }

        // 2. Store value in <>2__current field
        var valueTemp = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, valueTemp);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, valueTemp);
        _il.Emit(OpCodes.Stfld, _builder.CurrentField);

        // 3. Set state to the resume point
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, stateNumber);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // 4. Return ValueTask<bool>(true) - has value
        EmitReturnValueTaskBool(true);

        // 5. Mark the resume label (jumped to from state switch)
        _il.MarkLabel(resumeLabel);

        // 5a. Check __returnRequested flag (set by generator.return())
        // If true, jump to the enclosing finally cleanup path or complete the generator
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, _builder.ReturnRequestedField);
        var continueNormalLabel = _il.DefineLabel();
        _il.Emit(OpCodes.Brfalse, continueNormalLabel);

        if (_returnCleanupLabel != null)
        {
            // Inside a try/finally - jump to the afterTryBody label to execute finally
            _il.Emit(OpCodes.Br, _returnCleanupLabel.Value);
        }
        else
        {
            // Not inside try/finally - just complete the generator
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4, -2);
            _il.Emit(OpCodes.Stfld, _builder.StateField);
            EmitReturnValueTaskBool(false);
        }

        _il.MarkLabel(continueNormalLabel);

        // 6. Reset state to -1 (running)
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4_M1);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // 7. yield expression evaluates to undefined (null) when resumed
        _il.Emit(OpCodes.Ldnull);
        SetStackUnknown();
    }

    private void EmitYieldStar(Expr.Yield y, int stateNumber, Label resumeLabel)
    {
        // yield* delegates to another iterable (sync or async)
        var delegatedField = _builder.DelegatedAsyncEnumeratorField;

        if (delegatedField == null)
        {
            EmitYieldStarSync(y, stateNumber, resumeLabel);
            return;
        }

        // Check the type of the yield* expression to determine sync vs async
        // For now, try async first (check if it's IAsyncEnumerator), fall back to sync
        EmitYieldStarWithTypeCheck(y, stateNumber, resumeLabel, delegatedField);
    }

    private void EmitYieldStarWithTypeCheck(Expr.Yield y, int stateNumber, Label resumeLabel, FieldBuilder delegatedField)
    {
        // Structure:
        // 1. First-entry path: evaluate expression, check type, set up iteration, goto appropriate loop
        // 2. Resume path: check field type, dispatch to appropriate loop
        // 3. Sync loop
        // 4. Async loop
        // 5. End/cleanup

        var syncLoopLabel = _il.DefineLabel();
        var asyncLoopLabel = _il.DefineLabel();
        var syncSetupLabel = _il.DefineLabel();
        var asyncSetupLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();

        // === First entry path: evaluate and check type ===
        EmitExpression(y.Value!);
        EnsureBoxed();

        var iterableTemp = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, iterableTemp);

        // Check if it's IAsyncEnumerator<object> (async generators implement this)
        _il.Emit(OpCodes.Ldloc, iterableTemp);
        _il.Emit(OpCodes.Isinst, _types.IAsyncEnumeratorOfObject);
        _il.Emit(OpCodes.Brtrue, asyncSetupLabel);

        // Sync setup
        _il.MarkLabel(syncSetupLabel);
        _il.Emit(OpCodes.Ldloc, iterableTemp);
        _il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
        var getEnumerator = typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!;
        _il.Emit(OpCodes.Callvirt, getEnumerator);
        // Store in field
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldflda, delegatedField); // load field address for swap
        _il.Emit(OpCodes.Pop); // pop address
        var enumTemp = _il.DeclareLocal(typeof(System.Collections.IEnumerator));
        _il.Emit(OpCodes.Stloc, enumTemp);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, enumTemp);
        _il.Emit(OpCodes.Stfld, delegatedField);
        _il.Emit(OpCodes.Br, syncLoopLabel);

        // Async setup
        _il.MarkLabel(asyncSetupLabel);
        _il.Emit(OpCodes.Ldloc, iterableTemp);
        _il.Emit(OpCodes.Castclass, _types.IAsyncEnumeratorOfObject);
        var asyncEnumTemp = _il.DeclareLocal(_types.IAsyncEnumeratorOfObject);
        _il.Emit(OpCodes.Stloc, asyncEnumTemp);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, asyncEnumTemp);
        _il.Emit(OpCodes.Stfld, delegatedField);
        _il.Emit(OpCodes.Br, asyncLoopLabel);

        // === Resume path ===
        _il.MarkLabel(resumeLabel);
        // Reset state to -1 (running)
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4_M1);
        _il.Emit(OpCodes.Stfld, _builder.StateField);
        // Check field type to determine sync vs async
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, delegatedField);
        _il.Emit(OpCodes.Isinst, _types.IAsyncEnumeratorOfObject);
        _il.Emit(OpCodes.Brtrue, asyncLoopLabel);
        _il.Emit(OpCodes.Br, syncLoopLabel);

        // === Sync loop ===
        var syncLoopEnd = _il.DefineLabel();
        _il.MarkLabel(syncLoopLabel);
        var moveNext = typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!;
        var current = typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!;

        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, delegatedField);
        _il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerator));
        _il.Emit(OpCodes.Callvirt, moveNext);
        _il.Emit(OpCodes.Brfalse, syncLoopEnd);

        // Get current
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, delegatedField);
        _il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerator));
        _il.Emit(OpCodes.Callvirt, current);

        // Store in current field
        var syncValueTemp = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, syncValueTemp);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, syncValueTemp);
        _il.Emit(OpCodes.Stfld, _builder.CurrentField);

        // Set state and return
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, stateNumber);
        _il.Emit(OpCodes.Stfld, _builder.StateField);
        EmitReturnValueTaskBool(true);

        _il.MarkLabel(syncLoopEnd);
        _il.Emit(OpCodes.Br, endLabel);

        // === Async loop ===
        var asyncLoopEnd = _il.DefineLabel();
        _il.MarkLabel(asyncLoopLabel);

        // Call MoveNextAsync
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, delegatedField);
        _il.Emit(OpCodes.Castclass, _types.IAsyncEnumeratorOfObject);
        var moveNextAsync = _types.GetMethodNoParams(_types.IAsyncEnumeratorOfObject, "MoveNextAsync");
        _il.Emit(OpCodes.Callvirt, moveNextAsync);

        // Get result synchronously (simplified - full impl would suspend here)
        var valueTaskLocal = _il.DeclareLocal(_types.ValueTaskOfBool);
        _il.Emit(OpCodes.Stloc, valueTaskLocal);
        _il.Emit(OpCodes.Ldloca, valueTaskLocal);
        var getAwaiter = _types.GetMethodNoParams(_types.ValueTaskOfBool, "GetAwaiter");
        _il.Emit(OpCodes.Call, getAwaiter);
        var awaiterLocal = _il.DeclareLocal(_types.ValueTaskAwaiterOfBool);
        _il.Emit(OpCodes.Stloc, awaiterLocal);
        _il.Emit(OpCodes.Ldloca, awaiterLocal);
        var getResult = _types.GetMethodNoParams(_types.ValueTaskAwaiterOfBool, "GetResult");
        _il.Emit(OpCodes.Call, getResult);

        _il.Emit(OpCodes.Brfalse, asyncLoopEnd);

        // Get Current
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, delegatedField);
        _il.Emit(OpCodes.Castclass, _types.IAsyncEnumeratorOfObject);
        var currentGetter = _types.GetPropertyGetter(_types.IAsyncEnumeratorOfObject, "Current");
        _il.Emit(OpCodes.Callvirt, currentGetter);

        // Store in current field
        var asyncValueTemp = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, asyncValueTemp);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, asyncValueTemp);
        _il.Emit(OpCodes.Stfld, _builder.CurrentField);

        // Set state and return
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, stateNumber);
        _il.Emit(OpCodes.Stfld, _builder.StateField);
        EmitReturnValueTaskBool(true);

        _il.MarkLabel(asyncLoopEnd);

        // === End/cleanup ===
        _il.MarkLabel(endLabel);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Stfld, delegatedField);

        // yield* evaluates to undefined
        _il.Emit(OpCodes.Ldnull);
        SetStackUnknown();
    }

    private void EmitYieldStarSyncFromStack(int stateNumber, Label resumeLabel, FieldBuilder delegatedField)
    {
        // Stack has: [iterable]
        var getEnumerator = typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!;
        var moveNext = typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!;
        var current = typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!;

        var loopStart = _il.DefineLabel();
        var loopEnd = _il.DefineLabel();

        // Cast to IEnumerable and get enumerator
        _il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
        _il.Emit(OpCodes.Callvirt, getEnumerator);

        // Store enumerator in field
        var enumTemp = _il.DeclareLocal(typeof(System.Collections.IEnumerator));
        _il.Emit(OpCodes.Stloc, enumTemp);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, enumTemp);
        _il.Emit(OpCodes.Stfld, delegatedField);

        // Fall through to loop start
        _il.Emit(OpCodes.Br, loopStart);

        // Resume label
        _il.MarkLabel(resumeLabel);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4_M1);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // Loop start
        _il.MarkLabel(loopStart);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, delegatedField);
        _il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerator));
        _il.Emit(OpCodes.Callvirt, moveNext);
        _il.Emit(OpCodes.Brfalse, loopEnd);

        // Get current
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, delegatedField);
        _il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerator));
        _il.Emit(OpCodes.Callvirt, current);

        // Store in current field
        var valueTemp = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, valueTemp);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, valueTemp);
        _il.Emit(OpCodes.Stfld, _builder.CurrentField);

        // Set state and return
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, stateNumber);
        _il.Emit(OpCodes.Stfld, _builder.StateField);
        EmitReturnValueTaskBool(true);

        // Loop end
        _il.MarkLabel(loopEnd);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Stfld, delegatedField);

        _il.Emit(OpCodes.Ldnull);
        SetStackUnknown();
    }

    private void EmitYieldStarSync(Expr.Yield y, int stateNumber, Label resumeLabel)
    {
        // Sync yield* delegation using IEnumerable
        // The enumerator must be stored in a FIELD (not local) to persist across suspensions

        var delegatedField = _builder.DelegatedAsyncEnumeratorField;
        if (delegatedField == null)
        {
            // No field available - shouldn't happen if HasYieldStar was detected
            EmitExpression(y.Value!);
            EnsureBoxed();
            _il.Emit(OpCodes.Pop);
            _il.Emit(OpCodes.Ldnull);
            SetStackUnknown();
            return;
        }

        var getEnumerator = typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!;
        var moveNext = typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!;
        var current = typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!;

        var loopStart = _il.DefineLabel();
        var loopEnd = _il.DefineLabel();

        // Emit the iterable expression and get its enumerator
        EmitExpression(y.Value!);
        EnsureBoxed();
        _il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
        _il.Emit(OpCodes.Callvirt, getEnumerator);

        // Store enumerator in field (persists across suspensions)
        var enumTemp = _il.DeclareLocal(typeof(System.Collections.IEnumerator));
        _il.Emit(OpCodes.Stloc, enumTemp);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, enumTemp);
        _il.Emit(OpCodes.Stfld, delegatedField);

        // Fall through to loop start
        _il.Emit(OpCodes.Br, loopStart);

        // Resume label - jumped to from state switch when resuming after yield
        _il.MarkLabel(resumeLabel);
        // Reset state to -1 (running)
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4_M1);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // Loop start - check if more elements
        _il.MarkLabel(loopStart);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, delegatedField);
        _il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerator));
        _il.Emit(OpCodes.Callvirt, moveNext);
        _il.Emit(OpCodes.Brfalse, loopEnd);

        // Get current value
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, delegatedField);
        _il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerator));
        _il.Emit(OpCodes.Callvirt, current);

        // Store in <>2__current
        var valueTemp = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, valueTemp);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, valueTemp);
        _il.Emit(OpCodes.Stfld, _builder.CurrentField);

        // Set state to resume point
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, stateNumber);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // Return true (has value)
        EmitReturnValueTaskBool(true);

        // Loop end - delegation finished
        _il.MarkLabel(loopEnd);

        // Clear the delegated field
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Stfld, delegatedField);

        // yield* evaluates to undefined
        _il.Emit(OpCodes.Ldnull);
        SetStackUnknown();
    }

    #endregion

    #region Await Expressions

    protected override void EmitAwait(Expr.Await a)
    {
        int stateNumber = _currentSuspensionState++;
        var resumeLabel = _stateLabels[stateNumber];
        var continueLabel = _il.DefineLabel();

        // 1. Emit the awaited expression (should produce Task<object> or $Promise or any value)
        EmitExpression(a.Expression);
        EnsureBoxed();

        // 2. Convert to Task<object> - handle $Promise, Task<object>, or non-Task values
        // If it's a $Promise, extract its Task property
        // If it's already a Task<object>, use it directly
        // Otherwise, wrap in Task.FromResult (for non-promise values like numbers, strings, etc.)
        var taskLocal = _il.DeclareLocal(typeof(Task<object>));
        var isPromiseLabel = _il.DefineLabel();
        var isTaskLabel = _il.DefineLabel();
        var wrapValueLabel = _il.DefineLabel();
        var haveTaskLabel = _il.DefineLabel();

        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Isinst, _ctx!.Runtime!.TSPromiseType);
        _il.Emit(OpCodes.Brtrue, isPromiseLabel);

        // Not a $Promise - check if it's a Task<object>
        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Isinst, typeof(Task<object>));
        _il.Emit(OpCodes.Brtrue, isTaskLabel);

        // Not a Promise or Task - wrap in Task.FromResult
        _il.MarkLabel(wrapValueLabel);
        _il.Emit(OpCodes.Call, typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(typeof(object)));
        _il.Emit(OpCodes.Stloc, taskLocal);
        _il.Emit(OpCodes.Br, haveTaskLabel);

        // Is a Task<object> - use directly
        _il.MarkLabel(isTaskLabel);
        _il.Emit(OpCodes.Castclass, typeof(Task<object>));
        _il.Emit(OpCodes.Stloc, taskLocal);
        _il.Emit(OpCodes.Br, haveTaskLabel);

        // Is a $Promise - extract its Task property
        _il.MarkLabel(isPromiseLabel);
        _il.Emit(OpCodes.Castclass, _ctx.Runtime.TSPromiseType);
        _il.Emit(OpCodes.Callvirt, _ctx.Runtime.TSPromiseTaskGetter);
        _il.Emit(OpCodes.Stloc, taskLocal);

        _il.MarkLabel(haveTaskLabel);

        // 2b. Store the task in AwaitedTaskField (needed for continuation if not completed)
        // Stack: []
        _il.Emit(OpCodes.Ldarg_0);                // Stack: [this]
        _il.Emit(OpCodes.Ldloc, taskLocal);       // Stack: [this, task]
        _il.Emit(OpCodes.Stfld, _builder.AwaitedTaskField); // Stack: []
        _il.Emit(OpCodes.Ldloc, taskLocal);       // Stack: [task]

        // 3. Get awaiter: task.GetAwaiter()
        var getAwaiterMethod = typeof(Task<object>).GetMethod("GetAwaiter")!;
        _il.Emit(OpCodes.Call, getAwaiterMethod);

        // 4. Store awaiter to field
        var awaiterLocal = _il.DeclareLocal(_types.TaskAwaiterOfObject);
        _il.Emit(OpCodes.Stloc, awaiterLocal);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, awaiterLocal);
        _il.Emit(OpCodes.Stfld, _builder.AwaiterField);

        // 5. Check IsCompleted
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldflda, _builder.AwaiterField);
        var isCompletedGetter = _types.TaskAwaiterOfObject.GetProperty("IsCompleted")!.GetGetMethod()!;
        _il.Emit(OpCodes.Call, isCompletedGetter);
        _il.Emit(OpCodes.Brtrue, continueLabel);

        // 6. Not completed - suspend and return a pending ValueTask<bool>
        // Set state to resume point
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, stateNumber);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // For async generators, we need to return a ValueTask<bool> that will complete when the await completes
        // The simplest approach: wrap the continuing task
        // Create a continuation that resumes MoveNextAsync
        EmitAwaitSuspensionReturn();

        // 7. Resume point (jumped to from state switch)
        _il.MarkLabel(resumeLabel);

        // Reset state to -1 (running)
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4_M1);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // 8. Continue point (if was already completed)
        _il.MarkLabel(continueLabel);

        // 9. Get result: awaiter.GetResult()
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldflda, _builder.AwaiterField);
        var getResultMethod = _types.TaskAwaiterOfObject.GetMethod("GetResult")!;
        _il.Emit(OpCodes.Call, getResultMethod);

        // Result is now on stack
        SetStackUnknown();
    }

    private void EmitAwaitSuspensionReturn()
    {
        // Call emitted AsyncGeneratorAwaitContinue(task, generator)
        // This creates a proper continuation that calls MoveNextAsync after the await completes

        // Load the awaited task from field
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, _builder.AwaitedTaskField);

        // Load this (the generator) as IAsyncEnumerator<object>
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Castclass, _types.IAsyncEnumeratorOfObject);

        // Call AsyncGeneratorAwaitContinue(task, generator) - use emitted method for standalone support
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.AsyncGeneratorAwaitContinue);

        // Returns ValueTask<bool>, return it
        _il.Emit(OpCodes.Ret);
    }

    #endregion

    #region Arrow Function Expressions

    protected override void EmitArrowFunction(Expr.ArrowFunction af)
    {
        // Check for async arrow functions first
        if (af.IsAsync)
        {
            EmitAsyncArrowFunction(af);
            return;
        }

        // Get the method for this arrow function (pre-compiled)
        if (_ctx!.ArrowMethods == null || !_ctx.ArrowMethods.TryGetValue(af, out var method))
        {
            // Fallback if not found
            _il.Emit(OpCodes.Ldnull);
            SetStackUnknown();
            return;
        }

        // Check if this is a capturing arrow (has display class)
        if (_ctx.DisplayClasses != null && _ctx.DisplayClasses.TryGetValue(af, out var displayClass))
        {
            EmitCapturingArrowFunction(af, method, displayClass);
        }
        else
        {
            EmitNonCapturingArrowFunction(method);
        }
    }

    private void EmitAsyncArrowFunction(Expr.ArrowFunction af)
    {
        // For now, fallback to null for async arrow functions in async generators
        // Full implementation would need AsyncArrowBuilder support
        _il.Emit(OpCodes.Ldnull);
        SetStackUnknown();
    }

    private void EmitCapturingArrowFunction(Expr.ArrowFunction af, MethodBuilder method, TypeBuilder displayClass)
    {
        if (_ctx!.DisplayClassConstructors == null || !_ctx.DisplayClassConstructors.TryGetValue(af, out var displayCtor))
        {
            _il.Emit(OpCodes.Ldnull);
            SetStackUnknown();
            return;
        }

        _il.Emit(OpCodes.Newobj, displayCtor);

        if (_ctx.DisplayClassFields == null || !_ctx.DisplayClassFields.TryGetValue(af, out var fieldMap))
        {
            _il.Emit(OpCodes.Ldtoken, method);
            _il.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
            _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
            _il.Emit(OpCodes.Newobj, _ctx.Runtime!.TSFunctionCtor);
            SetStackUnknown();
            return;
        }

        // Populate captured fields
        foreach (var (capturedVar, field) in fieldMap)
        {
            _il.Emit(OpCodes.Dup);

            var hoistedField = _builder.GetVariableField(capturedVar);
            if (hoistedField != null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, hoistedField);
            }
            else if (capturedVar == "this" && _builder.ThisField != null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, _builder.ThisField);
            }
            else if (_ctx.Locals.TryGetLocal(capturedVar, out var local))
            {
                _il.Emit(OpCodes.Ldloc, local);
            }
            else
            {
                _il.Emit(OpCodes.Ldnull);
            }

            _il.Emit(OpCodes.Stfld, field);
        }

        _il.Emit(OpCodes.Ldtoken, method);
        _il.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
        _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
        _il.Emit(OpCodes.Newobj, _ctx.Runtime!.TSFunctionCtor);
        SetStackUnknown();
    }

    private void EmitNonCapturingArrowFunction(MethodBuilder method)
    {
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Ldtoken, method);
        _il.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
        _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
        _il.Emit(OpCodes.Newobj, _ctx!.Runtime!.TSFunctionCtor);
        SetStackUnknown();
    }

    #endregion

    #region Missing Abstract Implementations

    protected override void EmitSuper(Expr.Super s)
    {
        // Super not supported in async generators - push null
        _il.Emit(OpCodes.Ldnull);
        SetStackUnknown();
    }

    #endregion
}
