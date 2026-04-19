using SharpTS.Compilation;
using SharpTS.Parsing;
using SharpTS.Runtime;
using SharpTS.Runtime.BuiltIns;
using SharpTS.Runtime.Exceptions;
using SharpTS.Runtime.Types;
using SharpTS.TypeSystem;

namespace SharpTS.Execution;

// Note: This file uses InterpreterException for runtime errors

/// <summary>
/// Async expression and statement evaluation for async/await support.
/// </summary>
public partial class Interpreter
{
    // ===================== Async Statement Execution =====================

    /// <summary>
    /// Asynchronously executes a block of statements.
    /// Uses registry-based dispatch via ExecuteStatementAsync.
    /// </summary>
    internal async Task<ExecutionResult> ExecuteBlockAsync(List<Stmt> statements, RuntimeEnvironment environment)
    {
        using (PushScope(environment))
        {
            foreach (Stmt statement in statements)
            {
                var result = await ExecuteStatementAsync(statement);
                if (result.IsAbrupt) return result;
            }
            return ExecutionResult.Success();
        }
    }

    private async Task<ExecutionResult> ExecuteForOfAsync(Stmt.ForOf forOf)
    {
        object? iterable = (await EvaluateAsync(forOf.Iterable)).ToObject();

        // For 'for await...of', check for async iterator protocol first
        if (forOf.IsAsync)
        {
            var asyncIterator = TryGetAsyncIterator(iterable);
            if (asyncIterator != null)
            {
                return await IterateAsyncIterator(asyncIterator, forOf);
            }
            // Fall through to sync iterator with async unwrap
        }

        // Check for Symbol.iterator protocol first (works for both sync and async for...of)
        var syncIterator = TryGetSymbolIterator(iterable);
        if (syncIterator != null)
        {
            foreach (var item in syncIterator)
            {
                // For 'for await...of', unwrap promises from sync iterators
                object? value = forOf.IsAsync && item is Task<object?> t ? await t : item;

                var result = await ExecuteLoopBodyAsync(forOf.Variable.Lexeme, value, forOf.Body);
                if (result.Type == ExecutionResult.ResultType.Break && result.TargetLabel == null) return ExecutionResult.Success();
                if (result.Type == ExecutionResult.ResultType.Continue && result.TargetLabel == null) continue;
                if (result.IsAbrupt) return result;

                // Process any pending timer callbacks
                ProcessPendingCallbacks();
            }
            return ExecutionResult.Success();
        }

        // Get elements based on iterable type
        IEnumerable<object?> items = iterable switch
        {
            SharpTSArray arr => arr.Elements,
            SharpTSMap map => map.Entries().Elements,      // yields [key, value] arrays
            SharpTSSet set => set.Values().Elements,       // yields values
            SharpTSIterator iter => iter.Elements,
            SharpTSGenerator gen => gen,                   // generators implement IEnumerable<object?>
            string s => s.Select(c => (object?)c.ToString()),
            _ => throw new InterpreterException("for...of requires an iterable (array, Map, Set, or iterator).")
        };

        foreach (var item in items)
        {
            // For 'for await...of' with sync iterables, unwrap promises
            object? value = forOf.IsAsync && item is Task<object?> t ? await t : item;

            var result = await ExecuteLoopBodyAsync(forOf.Variable.Lexeme, value, forOf.Body);
            var (shouldBreak, shouldContinue, abruptResult) = HandleLoopResult(result, null);
            if (shouldBreak) return ExecutionResult.Success();
            if (shouldContinue) continue;
            if (abruptResult.HasValue) return abruptResult.Value;

            // Process any pending timer callbacks
            ProcessPendingCallbacks();
        }

        return ExecutionResult.Success();
    }

    private async Task<ExecutionResult> ExecuteLoopBodyAsync(string varName, object? value, Stmt body)
    {
        RuntimeEnvironment loopEnv = new(_environment);
        loopEnv.Define(varName, value);

        RuntimeEnvironment prev = _environment;
        _environment = loopEnv;
        try
        {
            return await ExecuteStatementAsync(body);
        }
        finally
        {
            _environment = prev;
        }
    }

    /// <summary>
    /// Tries to get an async iterator from an object via Symbol.asyncIterator.
    /// Async generators are their own async iterators.
    /// </summary>
    private object? TryGetAsyncIterator(object? iterable)
    {
        // Async generators are their own async iterators
        if (iterable is SharpTSAsyncGenerator asyncGen)
        {
            return asyncGen;
        }

        // Web Streams ReadableStream: wrap in an async iterator that
        // delegates next() to a default reader's read(). Matches Node 18+
        // behaviour where `for await (const chunk of rs)` works natively.
        if (iterable is SharpTSReadableStream rs)
        {
            if (rs.Locked)
            {
                throw new InterpreterException("TypeError: ReadableStream is already locked to a reader");
            }
            var reader = new SharpTSReadableStreamDefaultReader(rs);
            rs.Reader = reader;
            return new SharpTSReadableStreamAsyncIterator(rs, reader);
        }

        if (iterable is SharpTSObject obj)
        {
            var asyncIteratorFn = obj.GetBySymbol(SharpTSSymbol.AsyncIterator);
            if (asyncIteratorFn != null)
            {
                // Bind 'this' if it's an arrow function
                if (asyncIteratorFn is SharpTSArrowFunction arrowFunc)
                    asyncIteratorFn = arrowFunc.Bind(obj);

                // Call the async iterator function
                if (asyncIteratorFn is ISharpTSCallable callable)
                    return callable.Call(this, []);
            }
        }
        else if (iterable is SharpTSInstance inst)
        {
            var asyncIteratorFn = inst.GetBySymbol(SharpTSSymbol.AsyncIterator);
            if (asyncIteratorFn != null)
            {
                if (asyncIteratorFn is ISharpTSCallable callable)
                    return callable.Call(this, []);
            }
        }
        return null;
    }

    /// <summary>
    /// Iterates an async iterator by repeatedly calling .next() and awaiting results.
    /// </summary>
    private async Task<ExecutionResult> IterateAsyncIterator(object asyncIterator, Stmt.ForOf forOf)
    {
        while (true)
        {
            // Call iterator.next()
            var nextResult = CallMethodOnObject(asyncIterator, "next", []);

            // Await the result if it's a promise/task
            if (nextResult is SharpTSPromise promise)
                nextResult = await promise.Task;
            else if (nextResult is Task<object?> task)
                nextResult = await task;

            // Check if the result is an iterator result object
            bool done = false;
            object? value = null;

            if (nextResult is SharpTSObject resultObj)
            {
                var doneVal = resultObj.GetProperty("done");
                done = IsTruthy(doneVal);
                value = resultObj.GetProperty("value");
            }
            else if (nextResult is SharpTSIteratorResult iterResult)
            {
                done = iterResult.Done;
                value = iterResult.Value;
            }
            // Plain Dictionary<string, object?> — used by runtime helpers like
            // Web Streams iterator results returned from ReadableStream.read().
            else if (nextResult is IDictionary<string, object?> dict)
            {
                if (dict.TryGetValue("done", out var d)) done = IsTruthy(d);
                if (dict.TryGetValue("value", out var v)) value = v;
            }

            if (done) break;

            var result = await ExecuteLoopBodyAsync(forOf.Variable.Lexeme, value, forOf.Body);
            var (shouldBreak, shouldContinue, abruptResult) = HandleLoopResult(result, null);
            if (shouldBreak) return ExecutionResult.Success();
            if (shouldContinue) continue;
            if (abruptResult.HasValue) return abruptResult.Value;

            // Process any pending timer callbacks
            ProcessPendingCallbacks();
        }

        return ExecutionResult.Success();
    }

    /// <summary>
    /// Calls a method on an object by name.
    /// </summary>
    private object? CallMethodOnObject(object target, string methodName, List<object?> args)
    {
        if (target is SharpTSObject obj)
        {
            var method = obj.GetProperty(methodName);
            if (method != null)
            {
                if (method is SharpTSArrowFunction arrowFunc)
                    method = arrowFunc.Bind(obj);
                if (method is ISharpTSCallable callable)
                    return callable.Call(this, args);
            }
        }
        else if (target is SharpTSInstance inst)
        {
            // Try to find the method in the class
            var method = inst.GetClass().FindMethod(methodName);
            if (method != null)
            {
                var bound = SharpTSClass.BindMethod(method, inst);
                return bound.Call(this, args);
            }
        }
        else if (target is SharpTSGenerator gen)
        {
            // Handle generator methods
            return methodName switch
            {
                "next" => gen.Next(),
                "return" => gen.Return(args.Count > 0 ? args[0] : null),
                "throw" => gen.Throw(args.Count > 0 ? args[0] : null),
                _ => throw new InterpreterException($"Generator does not have method '{methodName}'.")
            };
        }
        else if (target is SharpTSAsyncGenerator asyncGen)
        {
            // Handle async generator methods
            return methodName switch
            {
                "next" => asyncGen.Next(),
                "return" => asyncGen.Return(args.Count > 0 ? args[0] : null),
                "throw" => asyncGen.Throw(args.Count > 0 ? args[0] : null),
                _ => throw new InterpreterException($"AsyncGenerator does not have method '{methodName}'.")
            };
        }

        throw new InterpreterException($"Cannot call method '{methodName}' on {target?.GetType().Name ?? "null"}.");
    }

    private async Task<ExecutionResult> ExecuteForInAsync(Stmt.ForIn forIn)
    {
        object? obj = (await EvaluateAsync(forIn.Object)).ToObject();

        IEnumerable<string> keys = obj switch
        {
            SharpTSObject o => o.Fields.Keys,
            SharpTSInstance inst => inst.GetFieldNames(),
            SharpTSArray arr => Enumerable.Range(0, arr.Elements.Count).Select(i => i.ToString()),
            // Plain Dictionary<string, object?> from runtime helpers (e.g.,
            // Web Streams iterator results) — see SharpTSReadableStream.MakeReadResult.
            IDictionary<string, object?> d => d.Keys,
            _ => throw new InterpreterException("for...in requires an object.")
        };

        foreach (var key in keys)
        {
            var result = await ExecuteLoopBodyAsync(forIn.Variable.Lexeme, key, forIn.Body);
            var (shouldBreak, shouldContinue, abruptResult) = HandleLoopResult(result, null);
            if (shouldBreak) return ExecutionResult.Success();
            if (shouldContinue) continue;
            if (abruptResult.HasValue) return abruptResult.Value;

            // Process any pending timer callbacks
            ProcessPendingCallbacks();
        }

        return ExecutionResult.Success();
    }

    private async Task<ExecutionResult> ExecuteSwitchAsync(Stmt.Switch switchStmt)
    {
        // Use async context with unified core
        return await ExecuteSwitchCore(_asyncContext, switchStmt);
    }

    private async Task<ExecutionResult> ExecuteTryCatchAsync(Stmt.TryCatch tryCatch)
    {
        // Use async context with unified core
        return await ExecuteTryCatchCore(_asyncContext, tryCatch);
    }

    // ===================== Async Statement Handlers for Registry =====================
    // These methods return ValueTask<ExecutionResult> for use with DispatchStmtAsync.
    // They wrap the existing async execution logic in ValueTask.

    internal async ValueTask<ExecutionResult> ExecuteBlockAsyncVT(Stmt.Block block)
    {
        return await ExecuteBlockAsync(block.Statements, new RuntimeEnvironment(_environment));
    }

    internal async ValueTask<ExecutionResult> ExecuteSequenceAsyncVT(Stmt.Sequence seq)
    {
        foreach (var s in seq.Statements)
        {
            var result = await ExecuteStatementAsync(s);
            if (result.IsAbrupt) return result;
        }
        return ExecutionResult.Success();
    }

    internal async ValueTask<ExecutionResult> ExecuteExpressionAsyncVT(Stmt.Expression exprStmt)
    {
        await EvaluateAsync(exprStmt.Expr);
        return ExecutionResult.Success();
    }

    internal async ValueTask<ExecutionResult> ExecuteIfAsyncVT(Stmt.If ifStmt)
    {
        if (IsTruthy(await EvaluateAsync(ifStmt.Condition)))
        {
            return await ExecuteStatementAsync(ifStmt.ThenBranch);
        }
        else if (ifStmt.ElseBranch != null)
        {
            return await ExecuteStatementAsync(ifStmt.ElseBranch);
        }
        return ExecutionResult.Success();
    }

    internal async ValueTask<ExecutionResult> ExecuteWhileAsyncVT(Stmt.While whileStmt)
    {
        while (IsTruthy(await EvaluateAsync(whileStmt.Condition)))
        {
            var result = await ExecuteStatementAsync(whileStmt.Body);
            var (shouldBreak, shouldContinue, abruptResult) = HandleLoopResult(result, null);
            if (shouldBreak) return ExecutionResult.Success();
            if (shouldContinue) continue;
            if (abruptResult.HasValue) return abruptResult.Value;
            ProcessPendingCallbacks();
        }
        return ExecutionResult.Success();
    }

    internal async ValueTask<ExecutionResult> ExecuteDoWhileAsyncVT(Stmt.DoWhile doWhileStmt)
    {
        do
        {
            var result = await ExecuteStatementAsync(doWhileStmt.Body);
            var (shouldBreak, shouldContinue, abruptResult) = HandleLoopResult(result, null);
            if (shouldBreak) return ExecutionResult.Success();
            if (shouldContinue) continue;
            if (abruptResult.HasValue) return abruptResult.Value;
            ProcessPendingCallbacks();
        } while (IsTruthy(await EvaluateAsync(doWhileStmt.Condition)));
        return ExecutionResult.Success();
    }

    internal async ValueTask<ExecutionResult> ExecuteForAsyncVT(Stmt.For forStmt)
    {
        RuntimeEnvironment loopEnv = new(_environment);
        using (PushScope(loopEnv))
        {
            if (forStmt.Initializer != null)
                await ExecuteStatementAsync(forStmt.Initializer);
            while (forStmt.Condition == null || IsTruthy(await EvaluateAsync(forStmt.Condition)))
            {
                var result = await ExecuteStatementAsync(forStmt.Body);
                if (result.Type == ExecutionResult.ResultType.Break && result.TargetLabel == null) break;
                if (result.Type == ExecutionResult.ResultType.Continue && result.TargetLabel == null)
                {
                    if (forStmt.Increment != null)
                        await EvaluateAsync(forStmt.Increment);
                    await Task.Yield();
                    continue;
                }
                if (result.IsAbrupt) return result;
                if (forStmt.Increment != null)
                    await EvaluateAsync(forStmt.Increment);
                ProcessPendingCallbacks();
            }
            return ExecutionResult.Success();
        }
    }

    internal async ValueTask<ExecutionResult> ExecuteForOfAsyncVT(Stmt.ForOf forOf)
    {
        return await ExecuteForOfAsync(forOf);
    }

    internal async ValueTask<ExecutionResult> ExecuteForInAsyncVT(Stmt.ForIn forIn)
    {
        return await ExecuteForInAsync(forIn);
    }

    internal async ValueTask<ExecutionResult> ExecuteSwitchAsyncVT(Stmt.Switch switchStmt)
    {
        return await ExecuteSwitchCore(_asyncContext, switchStmt);
    }

    internal async ValueTask<ExecutionResult> ExecuteTryCatchAsyncVT(Stmt.TryCatch tryCatch)
    {
        return await ExecuteTryCatchCore(_asyncContext, tryCatch);
    }

    internal async ValueTask<ExecutionResult> ExecuteThrowAsyncVT(Stmt.Throw throwStmt)
    {
        return ExecutionResult.Throw((await EvaluateAsync(throwStmt.Value)).ToObject());
    }

    internal async ValueTask<ExecutionResult> ExecuteVarAsyncVT(Stmt.Var varStmt)
    {
        object? value = null;
        if (varStmt.Initializer != null)
        {
            value = (await EvaluateAsync(varStmt.Initializer)).ToObject();
        }
        _environment.Define(varStmt.Name.Lexeme, value);
        return ExecutionResult.Success();
    }

    internal async ValueTask<ExecutionResult> ExecuteConstAsyncVT(Stmt.Const constStmt)
    {
        object? constValue = (await EvaluateAsync(constStmt.Initializer)).ToObject();
        _environment.Define(constStmt.Name.Lexeme, constValue);
        return ExecutionResult.Success();
    }

    internal async ValueTask<ExecutionResult> ExecuteReturnAsyncVT(Stmt.Return returnStmt)
    {
        object? returnValue = null;
        if (returnStmt.Value != null) returnValue = (await EvaluateAsync(returnStmt.Value)).ToObject();
        return ExecutionResult.Return(returnValue);
    }

    internal async ValueTask<ExecutionResult> ExecutePrintAsyncVT(Stmt.Print printStmt)
    {
        Out.WriteLine(Stringify((await EvaluateAsync(printStmt.Expr)).ToObject()));
        return ExecutionResult.Success();
    }

    // ===================== Async Expression Helpers =====================

    private async Task<RuntimeValue> EvaluateBinaryAsync(Expr.Binary binary)
    {
        var leftRV = await EvaluateAsync(binary.Left);
        var rightRV = await EvaluateAsync(binary.Right);

        // Fast path: both operands are numbers
        if (leftRV.IsNumber && rightRV.IsNumber)
        {
            double l = leftRV.AsNumber(), r = rightRV.AsNumber();
            var desc = SemanticOperatorResolver.Resolve(binary.Operator.Type);
            switch (desc)
            {
                case OperatorDescriptor.Plus:
                    return RuntimeValue.FromNumber(l + r);
                case OperatorDescriptor.Arithmetic:
                    return RuntimeValue.FromNumber(EvaluateArithmetic(binary.Operator.Type, l, r));
                case OperatorDescriptor.Power:
                    return RuntimeValue.FromNumber(Math.Pow(l, r));
                case OperatorDescriptor.Comparison:
                    return RuntimeValue.FromBoolean(EvaluateComparison(binary.Operator.Type, l, r));
                case OperatorDescriptor.Equality eq:
                    bool equal = l.Equals(r);
                    return RuntimeValue.FromBoolean(eq.IsNegated ? !equal : equal);
                case OperatorDescriptor.Bitwise or OperatorDescriptor.BitwiseShift:
                    return RuntimeValue.FromNumber(EvaluateBitwise(binary.Operator.Type, (int)l, (int)r));
                case OperatorDescriptor.UnsignedRightShift:
                    return RuntimeValue.FromNumber((double)((uint)(int)l >> ((int)r & 0x1F)));
            }
        }

        return EvaluateBinaryOperationRV(binary.Operator, leftRV, rightRV);
    }

    private Task<RuntimeValue> EvaluateLogicalAsync(Expr.Logical logical) =>
        EvaluateLogicalCoreAsync(
            logical.Operator.Type,
            EvaluateAsync(logical.Left),
            () => EvaluateAsync(logical.Right));

    private Task<RuntimeValue> EvaluateNullishCoalescingAsync(Expr.NullishCoalescing nc) =>
        EvaluateNullishCoalescingCoreAsync(
            EvaluateAsync(nc.Left),
            () => EvaluateAsync(nc.Right));

    private Task<RuntimeValue> EvaluateTernaryAsync(Expr.Ternary ternary) =>
        EvaluateTernaryCoreAsync(
            EvaluateAsync(ternary.Condition),
            () => EvaluateAsync(ternary.ThenBranch),
            () => EvaluateAsync(ternary.ElseBranch));

    private async Task<RuntimeValue> EvaluateUnaryAsync(Expr.Unary unary)
    {
        // typeof never throws on undeclared variables
        if (unary.Operator.Type == TokenType.TYPEOF && unary.Right is Expr.Variable)
        {
            RuntimeValue right;
            try { right = await EvaluateAsync(unary.Right); }
            catch (InterpreterException) { right = RuntimeValue.Undefined; }
            return RuntimeValue.FromString(right.TypeofString());
        }

        var rv = await EvaluateAsync(unary.Right);
        return EvaluateUnaryOperationRV(unary.Operator, rv);
    }

    private async ValueTask<RuntimeValue> EvaluateAssignAsync(Expr.Assign assign)
    {
        var rv = await EvaluateAsync(assign.Value);
        object? value = rv.ToObject();

        if (_locals.TryGetValue(assign, out int distance))
        {
            _environment.AssignAt(distance, assign.Name, value);
        }
        else
        {
            _environment.Assign(assign.Name, value);
        }

        return rv;
    }

    private async Task<RuntimeValue> EvaluateCallAsync(Expr.Call call)
    {
        // Use async context with unified core - handles all special cases
        return RuntimeValue.FromBoxed(await EvaluateCallCore(_asyncContext, call));
    }

    private async Task<RuntimeValue> EvaluateGetAsync(Expr.Get get)
    {
        // Handle namespace static property access (e.g., Number.MAX_VALUE, Number.NaN)
        // These namespaces don't have runtime values, but have static properties
        if (get.Object is Expr.Variable nsVar)
        {
            var member = BuiltInRegistry.Instance.GetStaticMethod(nsVar.Name.Lexeme, get.Name.Lexeme);
            if (member != null)
            {
                // If it's a constant (like Number.MAX_VALUE), it's wrapped in a BuiltInMethod
                // that returns the value when invoked with no args
                if (member is BuiltInMethod bm && bm.MinArity == 0 && bm.MaxArity == 0)
                {
                    // It's a constant property, invoke it to get the value
                    return RuntimeValue.FromBoxed(bm.Call(this, []));
                }
                return RuntimeValue.FromObject(member);
            }
        }

        object? obj = (await EvaluateAsync(get.Object)).ToObject();
        return EvaluateGetOnObject(get, obj);
    }

    private async Task<RuntimeValue> EvaluateSetAsync(Expr.Set set)
    {
        object? obj = (await EvaluateAsync(set.Object)).ToObject();
        object? value = (await EvaluateAsync(set.Value)).ToObject();
        return EvaluateSetOnObjectRV(set, obj, value);
    }

    private async Task<RuntimeValue> EvaluateNewAsync(Expr.New newExpr)
    {
        // Use async context with unified core - handles all built-in types
        return RuntimeValue.FromBoxed(await EvaluateNewCore(_asyncContext, newExpr));
    }

    private async Task<RuntimeValue> EvaluateArrayAsync(Expr.ArrayLiteral array)
    {
        // Use async context with unified core
        return RuntimeValue.FromBoxed(await EvaluateArrayCore(_asyncContext, array));
    }

    private async Task<RuntimeValue> EvaluateObjectAsync(Expr.ObjectLiteral obj)
    {
        // Use async context with unified core
        return RuntimeValue.FromBoxed(await EvaluateObjectCore(_asyncContext, obj));
    }

    private async Task<RuntimeValue> EvaluateGetIndexAsync(Expr.GetIndex getIndex)
    {
        object? obj = (await EvaluateAsync(getIndex.Object)).ToObject();

        // Optional bracket access: return undefined if object is nullish
        if (getIndex.Optional && (obj == null || obj is Runtime.Types.SharpTSUndefined))
        {
            return RuntimeValue.Undefined;
        }

        object? index = (await EvaluateAsync(getIndex.Index)).ToObject();
        return RuntimeValue.FromBoxed(EvaluateIndexGet(obj, index));
    }

    private async Task<RuntimeValue> EvaluateSetIndexAsync(Expr.SetIndex setIndex)
    {
        object? obj = (await EvaluateAsync(setIndex.Object)).ToObject();
        object? index = (await EvaluateAsync(setIndex.Index)).ToObject();
        object? value = (await EvaluateAsync(setIndex.Value)).ToObject();
        return RuntimeValue.FromBoxed(EvaluateIndexSet(obj, index, value));
    }

    private async Task<RuntimeValue> EvaluateCompoundAssignAsync(Expr.CompoundAssign compound)
    {
        var currentRV = _environment.Get(compound.Name);
        var operandRV = await EvaluateAsync(compound.Value);
        var result = ApplyCompoundOperatorRV(compound.Operator.Type, currentRV, operandRV);
        _environment.Assign(compound.Name, result.ToObject());
        return result;
    }

    private async Task<RuntimeValue> EvaluateCompoundSetAsync(Expr.CompoundSet compoundSet)
    {
        object? obj = (await EvaluateAsync(compoundSet.Object)).ToObject();
        RuntimeValue currentRV = EvaluateGetOnObject(new Expr.Get(compoundSet.Object, compoundSet.Name), obj);
        var operandRV = await EvaluateAsync(compoundSet.Value);
        RuntimeValue result = ApplyCompoundOperatorRV(compoundSet.Operator.Type, currentRV, operandRV);
        object? resultObj = result.ToObject();
        EvaluateSetOnObject(new Expr.Set(compoundSet.Object, compoundSet.Name, new Expr.Literal(resultObj)), obj, resultObj);
        return result;
    }

    private async Task<RuntimeValue> EvaluateCompoundSetIndexAsync(Expr.CompoundSetIndex compoundSetIndex)
    {
        object? obj = (await EvaluateAsync(compoundSetIndex.Object)).ToObject();
        object? index = (await EvaluateAsync(compoundSetIndex.Index)).ToObject();
        object? currentValue = EvaluateIndexGet(obj, index);
        object? operandValue = (await EvaluateAsync(compoundSetIndex.Value)).ToObject();
        object? result = ApplyCompoundOperator(compoundSetIndex.Operator.Type, currentValue, operandValue);
        return RuntimeValue.FromBoxed(EvaluateIndexSet(obj, index, result));
    }

    private async Task<RuntimeValue> EvaluateLogicalAssignAsync(Expr.LogicalAssign logical)
    {
        var currentRV = _environment.Get(logical.Name);

        switch (logical.Operator.Type)
        {
            case TokenType.AND_AND_EQUAL:
                if (!IsTruthy(currentRV)) return currentRV;
                break;
            case TokenType.OR_OR_EQUAL:
                if (IsTruthy(currentRV)) return currentRV;
                break;
            case TokenType.QUESTION_QUESTION_EQUAL:
                if (!currentRV.IsNullish) return currentRV;
                break;
        }

        var newRV = await EvaluateAsync(logical.Value);
        _environment.Assign(logical.Name, newRV.ToObject());
        return newRV;
    }

    private async Task<RuntimeValue> EvaluateLogicalSetAsync(Expr.LogicalSet logical)
    {
        object? obj = (await EvaluateAsync(logical.Object)).ToObject();

        if (!TryGetPropertyRV(obj, logical.Name, out RuntimeValue currentRV))
        {
            throw new InterpreterException("Only instances and objects have fields.");
        }

        switch (logical.Operator.Type)
        {
            case TokenType.AND_AND_EQUAL:
                if (!currentRV.IsTruthy()) return currentRV;
                break;
            case TokenType.OR_OR_EQUAL:
                if (currentRV.IsTruthy()) return currentRV;
                break;
            case TokenType.QUESTION_QUESTION_EQUAL:
                if (!currentRV.IsNullish) return currentRV;
                break;
        }

        var newRV = await EvaluateAsync(logical.Value);
        object? newValue = newRV.ToObject();
        if (!TrySetProperty(obj, logical.Name, newValue))
        {
            throw new InterpreterException("Only instances and objects have fields.");
        }
        return newRV;
    }

    private async Task<RuntimeValue> EvaluateLogicalSetIndexAsync(Expr.LogicalSetIndex logical)
    {
        object? obj = (await EvaluateAsync(logical.Object)).ToObject();
        object? index = (await EvaluateAsync(logical.Index)).ToObject();
        object? currentValue = EvaluateIndexGet(obj, index);

        switch (logical.Operator.Type)
        {
            case TokenType.AND_AND_EQUAL:
                if (!IsTruthy(currentValue)) return RuntimeValue.FromBoxed(currentValue);
                break;
            case TokenType.OR_OR_EQUAL:
                if (IsTruthy(currentValue)) return RuntimeValue.FromBoxed(currentValue);
                break;
            case TokenType.QUESTION_QUESTION_EQUAL:
                if (currentValue != null) return RuntimeValue.FromBoxed(currentValue);
                break;
        }

        object? newValue = (await EvaluateAsync(logical.Value)).ToObject();
        return RuntimeValue.FromBoxed(EvaluateIndexSet(obj, index, newValue));
    }

    private async Task<RuntimeValue> EvaluateTemplateLiteralAsync(Expr.TemplateLiteral template)
    {
        var evaluatedExprs = new List<object?>();
        foreach (var expr in template.Expressions)
        {
            evaluatedExprs.Add((await EvaluateAsync(expr)).ToObject());
        }
        return RuntimeValue.FromString(BuildTemplateLiteralString(template.Strings, evaluatedExprs));
    }

    private async Task<RuntimeValue> EvaluateTaggedTemplateLiteralAsync(Expr.TaggedTemplateLiteral tagged)
    {
        object? tag = (await EvaluateAsync(tagged.Tag)).ToObject();

        if (tag is not Runtime.Types.ISharpTSCallable callable)
            throw new InterpreterException("Tagged template tag must be a function.");

        var cookedList = tagged.CookedStrings.Cast<object?>().ToList();
        var stringsArray = new Runtime.Types.SharpTSTemplateStringsArray(cookedList, tagged.RawStrings);

        List<object?> args = [stringsArray];
        foreach (var expr in tagged.Expressions)
            args.Add((await EvaluateAsync(expr)).ToObject());

        return RuntimeValue.FromBoxed(callable.Call(this, args));
    }

    // Helper methods for index operations
    private object? EvaluateIndexGet(object? obj, object? index)
    {
        // Proxy interception for index access
        if (obj is SharpTSProxy proxy)
        {
            string key = index is SharpTSSymbol ? index.ToString()! : Stringify(index);
            return proxy.TrapGet(key, this);
        }

        if (obj is SharpTSArray array && index is double idx)
        {
            return array.Get((int)idx);
        }
        if (obj is SharpTSEnum enumObj && index is double enumIdx)
        {
            return enumObj.GetReverse(enumIdx);
        }
        // Handle symbol keys - return undefined for missing symbol properties
        if (index is SharpTSSymbol symbol)
        {
            if (obj is SharpTSObject symObj)
            {
                return symObj.GetBySymbol(symbol) ?? SharpTSUndefined.Instance;
            }
            if (obj is SharpTSInstance symInst)
            {
                return symInst.GetBySymbol(symbol) ?? SharpTSUndefined.Instance;
            }
        }
        if (obj is SharpTSObject sharpObj && index is string strKey)
        {
            return sharpObj.GetProperty(strKey);
        }
        if (obj is SharpTSObject numObj && index is double numKey)
        {
            return numObj.GetProperty(numKey.ToString());
        }
        if (obj is SharpTSInstance instance && index is string instanceKey)
        {
            return instance.Get(new Token(TokenType.IDENTIFIER, instanceKey, null, 0));
        }
        if (obj is SharpTSHeaders headers && index is string headerKey)
        {
            return (object?)headers.Get(headerKey) ?? SharpTSUndefined.Instance;
        }
        if (obj is string str && index is double strIdx)
        {
            int i = (int)strIdx;
            return (i >= 0 && i < str.Length) ? (object)str[i].ToString() : SharpTSUndefined.Instance;
        }
        throw new InterpreterException("Index access not supported on this type.");
    }

    private object? EvaluateIndexSet(object? obj, object? index, object? value)
    {
        // Proxy interception for index set
        if (obj is SharpTSProxy proxy)
        {
            string key = index is SharpTSSymbol ? index.ToString()! : Stringify(index);
            proxy.TrapSet(key, value, this);
            return value;
        }

        bool strictMode = _environment.IsStrictMode;

        if (obj is SharpTSArray array && index is double idx)
        {
            if (strictMode)
            {
                array.SetStrict((int)idx, value, strictMode);
            }
            else
            {
                array.Set((int)idx, value);
            }
            return value;
        }
        if (obj is SharpTSObject sharpObj && index is string strKey)
        {
            if (strictMode)
            {
                sharpObj.SetPropertyStrict(strKey, value, strictMode);
            }
            else
            {
                sharpObj.SetProperty(strKey, value);
            }
            return value;
        }
        if (obj is SharpTSObject numObj && index is double numKey)
        {
            if (strictMode)
            {
                numObj.SetPropertyStrict(numKey.ToString(), value, strictMode);
            }
            else
            {
                numObj.SetProperty(numKey.ToString(), value);
            }
            return value;
        }
        if (obj is SharpTSInstance instance && index is string instanceKey)
        {
            if (strictMode)
            {
                instance.SetRawFieldStrict(instanceKey, value, strictMode);
            }
            else
            {
                instance.SetRawField(instanceKey, value);
            }
            return value;
        }
        throw new InterpreterException("Index assignment not supported on this type.");
    }
}
