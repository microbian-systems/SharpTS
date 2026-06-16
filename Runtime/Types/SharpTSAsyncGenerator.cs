using SharpTS.Compilation;
using SharpTS.Parsing;
using SharpTS.Runtime.Exceptions;
using SharpTS.Execution;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime object representing an active async generator instance.
/// </summary>
/// <remarks>
/// Created by <see cref="SharpTSAsyncGeneratorFunction"/> when called.
/// Combines async execution (await) with generator semantics (yield).
/// Each call to next() returns a Promise that resolves to { value, done }.
/// </remarks>
/// <seealso cref="SharpTSAsyncGeneratorFunction"/>
/// <seealso cref="SharpTSGenerator"/>
public class SharpTSAsyncGenerator : ITypeCategorized
{
    /// <inheritdoc />
    public TypeCategory RuntimeCategory => TypeCategory.AsyncGenerator;

    private readonly Stmt.Function _declaration;
    private readonly RuntimeEnvironment _environment;
    private readonly Interpreter _interpreter;

    private List<object?>? _values = null;  // Collected yielded values (null = not yet executed)
    private int _index = 0;
    // The generator's completion value. Defaults to undefined so a body that falls off the end (or a
    // no-arg return) reports { value: undefined, done: true }, not C# null (#540).
    private object? _returnValue = SharpTSUndefined.Instance;
    private bool _closed = false;
    // Whether the one-time completion result has already been handed out. Once the body is drained
    // (or the generator is closed), the completion value is reported exactly once; every later next()
    // reports undefined (ECMA-262 §27.6.1.2 → CreateIterResultObject(undefined, true), #540).
    private bool _completionDelivered = false;
    // A throw (an uncaught guest `throw` or a rejected `await`) raised by the eagerly-drained body.
    // The body runs to completion on the first next(), but the throw must be observed from the next()
    // that follows the values yielded before it — so it is buffered here and rethrown by Next() once
    // those values are exhausted, rejecting that call's promise (#566). Null when the body did not throw.
    private Exception? _pendingException = null;

    public SharpTSAsyncGenerator(Stmt.Function declaration, RuntimeEnvironment environment, Interpreter interpreter)
    {
        _declaration = declaration;
        _environment = environment;
        _interpreter = interpreter;
    }

    /// <summary>
    /// Advances the async generator to the next yield point.
    /// Returns a Promise that resolves to { value, done } result object.
    /// </summary>
    public async Task<object?> Next()
    {
        // A finished/disposed generator (closed via return()/throw()) reports undefined; its completion
        // value was already delivered/consumed by that return()/throw() call (ECMA-262 §27.6.1.2, #540).
        if (_closed)
        {
            return new SharpTSIteratorResult(SharpTSUndefined.Instance, done: true);
        }

        // Execute the generator body on first call (async)
        if (_values == null)
        {
            await ExecuteBodyAsync();
        }

        // Defensive check: ExecuteBodyAsync should always initialize _values,
        // but verify to provide a clear error message if something goes wrong.
        if (_values == null)
        {
            throw new InvalidOperationException(
                "Internal error: Async generator body did not initialize values collection. " +
                "This indicates a bug in ExecuteBodyAsync.");
        }

        if (_index < _values.Count)
        {
            return new SharpTSIteratorResult(_values[_index++], done: false);
        }

        // Body fully drained. If it threw after the values above, surface that now so this next()'s
        // promise rejects — the throw is delivered after the preceding values, then the generator is
        // done (#566). Reported exactly once.
        if (_pendingException != null)
        {
            var ex = _pendingException;
            _pendingException = null;
            _closed = true;
            _completionDelivered = true;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
        }

        // Deliver the completion value once (the body's `return X`, or undefined when it ran off the
        // end), then undefined on every later call — the stale completion / last yielded value must
        // not replay forever (#540; mirrors the sync generator's done semantics).
        if (_completionDelivered)
        {
            return new SharpTSIteratorResult(SharpTSUndefined.Instance, done: true);
        }
        _completionDelivered = true;
        return new SharpTSIteratorResult(_returnValue, done: true);
    }

    /// <summary>
    /// Closes the async generator and returns a Promise resolving to { value, done: true }.
    /// </summary>
    public Task<object?> Return(object? value = null)
    {
        // return(v) reports { value: v, done: true } (echoing the argument, per ECMA-262 §27.6.1.3) and
        // closes the generator; a later next() then reports undefined via the `_closed` guard in Next()
        // rather than replaying v (#540). _completionDelivered is set so the once-only accounting stays
        // consistent even if the body had already run off the end before this return().
        _closed = true;
        _completionDelivered = true;
        // return() closes the generator and wins over a not-yet-observed body throw: the code after
        // the last yield never "runs" observably, so discard any buffered exception (#566).
        _pendingException = null;
        return Task.FromResult<object?>(new SharpTSIteratorResult(value, done: true));
    }

    /// <summary>
    /// Throws an exception at the current yield point.
    /// Returns a Promise that rejects with the error.
    /// </summary>
    public Task<object?> Throw(object? error = null)
    {
        _closed = true;
        string message = error?.ToString() ?? "AsyncGenerator.throw() called";
        throw new ThrowException(error ?? message);
    }

    /// <summary>
    /// Executes the async generator body, collecting all yielded values.
    /// Handles both yield and await expressions.
    /// </summary>
    private async Task ExecuteBodyAsync()
    {
        _values = [];

        if (_declaration.Body == null || _declaration.Body.Count == 0)
        {
            return;
        }

        // Save and set the interpreter environment
        RuntimeEnvironment previousEnv = _interpreter.Environment;
        _interpreter.SetEnvironment(_environment);

        try
        {
            var result = await ExecuteStatementsAsync(_declaration.Body);
            if (result.Type == ExecutionResult.ResultType.Return)
            {
                _returnValue = result.Value.ToObject();
            }
            else if (result.Type == ExecutionResult.ResultType.Throw)
            {
                // Buffer rather than throw now: the body drains eagerly, so a throw after some yields
                // must surface from the next() that follows those values, not the first one (#566).
                // The original throw value is preserved through ThrowException (see SharpTSFunction.Call).
                _pendingException = ThrowException.FromResult(result.Value.ToObject());
            }
        }
        catch (Exception ex) when (ex is not YieldException)
        {
            // A host exception escaped the body — a rejected `await`, or a `throw` surfaced as a C#
            // exception. Buffer it so the post-drain next() rejects its promise with it (#566).
            _pendingException = ex;
        }
        finally
        {
            _interpreter.SetEnvironment(previousEnv);
        }
    }

    /// <summary>
    /// Recursively executes statements asynchronously, collecting yields.
    /// </summary>
    private async Task<ExecutionResult> ExecuteStatementsAsync(List<Stmt> statements)
    {
        foreach (var stmt in statements)
        {
            var result = await ExecuteStatementAsync(stmt);
            if (result.IsAbrupt) return result;
        }
        return ExecutionResult.Success();
    }

    /// <summary>
    /// Executes a single statement asynchronously, handling yield and await expressions.
    /// </summary>
    private async Task<ExecutionResult> ExecuteStatementAsync(Stmt stmt)
    {
        switch (stmt)
        {
            case Stmt.Expression exprStmt:
                try
                {
                    await EvaluateAsync(exprStmt.Expr);
                }
                catch (YieldException yield)
                {
                    await HandleYieldAsync(yield);
                }
                return ExecutionResult.Success();

            case Stmt.Block block:
                if (block.Statements != null)
                {
                    var blockEnv = new RuntimeEnvironment(_interpreter.Environment);
                    RuntimeEnvironment prevEnv = _interpreter.Environment;
                    _interpreter.SetEnvironment(blockEnv);
                    try
                    {
                        return await ExecuteStatementsAsync(block.Statements);
                    }
                    finally
                    {
                        _interpreter.SetEnvironment(prevEnv);
                    }
                }
                return ExecutionResult.Success();

            case Stmt.Var varStmt:
                object? value = null;
                try
                {
                    if (varStmt.Initializer != null)
                    {
                        value = await EvaluateAsync(varStmt.Initializer);
                    }
                }
                catch (YieldException yield)
                {
                    await HandleYieldAsync(yield);
                    value = null;
                }
                _environment.Define(varStmt.Name.Lexeme, value);
                return ExecutionResult.Success();

            case Stmt.If ifStmt:
                object? condition;
                try
                {
                    condition = await EvaluateAsync(ifStmt.Condition);
                }
                catch (YieldException yield)
                {
                    await HandleYieldAsync(yield);
                    condition = false;
                }

                if (IsTruthy(condition))
                {
                    return await ExecuteStatementAsync(ifStmt.ThenBranch);
                }
                else if (ifStmt.ElseBranch != null)
                {
                    return await ExecuteStatementAsync(ifStmt.ElseBranch);
                }
                return ExecutionResult.Success();

            case Stmt.While whileStmt:
                while (true)
                {
                    object? whileCond;
                    try
                    {
                        whileCond = await EvaluateAsync(whileStmt.Condition);
                    }
                    catch (YieldException yield)
                    {
                        await HandleYieldAsync(yield);
                        whileCond = false;
                    }

                    if (!IsTruthy(whileCond)) break;

                    var result = await ExecuteStatementAsync(whileStmt.Body);
                    if (result.Type == ExecutionResult.ResultType.Break && result.TargetLabel == null) break;
                    if (result.Type == ExecutionResult.ResultType.Continue && result.TargetLabel == null) continue;
                    if (result.IsAbrupt) return result;
                }
                return ExecutionResult.Success();

            case Stmt.For forStmt:
                // Execute initializer once
                if (forStmt.Initializer != null)
                {
                    var initResult = await ExecuteStatementAsync(forStmt.Initializer);
                    if (initResult.IsAbrupt) return initResult;
                }

                // Loop
                while (true)
                {
                    // Check condition
                    if (forStmt.Condition != null)
                    {
                        object? forCond;
                        try
                        {
                            forCond = await EvaluateAsync(forStmt.Condition);
                        }
                        catch (YieldException yield)
                        {
                            await HandleYieldAsync(yield);
                            forCond = false;
                        }

                        if (!IsTruthy(forCond)) break;
                    }

                    // Execute body
                    var result = await ExecuteStatementAsync(forStmt.Body);

                    if (result.Type == ExecutionResult.ResultType.Break && result.TargetLabel == null)
                        break;

                    // On continue OR normal completion, execute increment
                    if (result.Type == ExecutionResult.ResultType.Continue && result.TargetLabel == null)
                    {
                        // Execute increment before continuing
                        if (forStmt.Increment != null)
                        {
                            try
                            {
                                await EvaluateAsync(forStmt.Increment);
                            }
                            catch (YieldException yield)
                            {
                                await HandleYieldAsync(yield);
                            }
                        }
                        continue;
                    }

                    if (result.IsAbrupt) return result;

                    // Normal completion: execute increment
                    if (forStmt.Increment != null)
                    {
                        try
                        {
                            await EvaluateAsync(forStmt.Increment);
                        }
                        catch (YieldException yield)
                        {
                            await HandleYieldAsync(yield);
                        }
                    }
                }
                return ExecutionResult.Success();

            case Stmt.ForOf forOf:
                object? iterable;
                try
                {
                    iterable = await EvaluateAsync(forOf.Iterable);
                }
                catch (YieldException yield)
                {
                    await HandleYieldAsync(yield);
                    iterable = new SharpTSArray([]);
                }

                IEnumerable<object?> elements = GetIterableElements(iterable);
                foreach (var element in elements)
                {
                    var loopEnv = new RuntimeEnvironment(_interpreter.Environment);
                    loopEnv.Define(forOf.Variable.Lexeme, element);

                    RuntimeEnvironment prevEnv = _interpreter.Environment;
                    _interpreter.SetEnvironment(loopEnv);
                    try
                    {
                        var result = await ExecuteStatementAsync(forOf.Body);
                        if (result.Type == ExecutionResult.ResultType.Break && result.TargetLabel == null) break;
                        if (result.Type == ExecutionResult.ResultType.Continue && result.TargetLabel == null) continue;
                        if (result.IsAbrupt) return result;
                    }
                    finally
                    {
                        _interpreter.SetEnvironment(prevEnv);
                    }
                }
                return ExecutionResult.Success();

            case Stmt.Return returnStmt:
                // A value-less `return;` (or running off the end) completes with undefined, not C#
                // null; only an explicit `return null;` reports null (#540, mirrors the sync gen).
                object? returnValue = SharpTSUndefined.Instance;
                if (returnStmt.Value != null)
                {
                    try
                    {
                        returnValue = await EvaluateAsync(returnStmt.Value);
                    }
                    catch (YieldException yield)
                    {
                        await HandleYieldAsync(yield);
                    }
                }
                return ExecutionResult.Return(returnValue);

            case Stmt.TryCatch tryCatch:
                ExecutionResult tryResult;
                try
                {
                    tryResult = await ExecuteStatementsAsync(tryCatch.TryBlock);
                }
                catch (Exception ex) when (ex is not YieldException)
                {
                    // A host exception escaped the try body — most often a rejected `await`
                    // (SharpTSPromiseRejectedException) or a guest `throw` surfaced as a C#
                    // exception. Convert it to a guest Throw so this try's catch/finally run (#617).
                    tryResult = ExecutionResult.Throw(_interpreter.TranslateException(ex));
                }

                if (tryResult.Type == ExecutionResult.ResultType.Throw && tryCatch.CatchBlock != null)
                {
                    var catchEnv = new RuntimeEnvironment(_interpreter.Environment);
                    // `catch {}` (no binding) is valid — only define the param when present, and bind
                    // the unwrapped guest value rather than the boxed RuntimeValue struct.
                    if (tryCatch.CatchParam != null)
                        catchEnv.Define(tryCatch.CatchParam.Lexeme,
                            _interpreter.CoerceCaughtValueForBinding(tryResult.Value.ToObject()));
                    RuntimeEnvironment prevEnv = _interpreter.Environment;
                    _interpreter.SetEnvironment(catchEnv);
                    try
                    {
                        tryResult = await ExecuteStatementsAsync(tryCatch.CatchBlock);
                    }
                    catch (Exception ex) when (ex is not YieldException)
                    {
                        tryResult = ExecutionResult.Throw(_interpreter.TranslateException(ex));
                    }
                    finally
                    {
                        _interpreter.SetEnvironment(prevEnv);
                    }
                }

                if (tryCatch.FinallyBlock != null)
                {
                    ExecutionResult finallyResult;
                    try
                    {
                        finallyResult = await ExecuteStatementsAsync(tryCatch.FinallyBlock);
                    }
                    catch (Exception ex) when (ex is not YieldException)
                    {
                        finallyResult = ExecutionResult.Throw(_interpreter.TranslateException(ex));
                    }
                    if (finallyResult.IsAbrupt) return finallyResult;
                }
                return tryResult;

            default:
                // For other statements, delegate to the interpreter's async handler
                try
                {
                    return await _interpreter.ExecuteBlockAsync([stmt], _environment);
                }
                catch (YieldException yield)
                {
                    await HandleYieldAsync(yield);
                    return ExecutionResult.Success();
                }
        }
    }

    /// <summary>
    /// Evaluates an expression asynchronously, handling await and yield expressions.
    /// </summary>
    private async Task<object?> EvaluateAsync(Expr expr)
    {
        // Check for await expression
        if (expr is Expr.Await awaitExpr)
        {
            // Recursively evaluate the inner expression (may contain await)
            var value = await EvaluateAsync(awaitExpr.Expression);
            // Handle SharpTSPromise (wraps Task<object?>)
            if (value is SharpTSPromise promise)
            {
                return await promise.Task;
            }
            if (value is Task<object?> task)
            {
                return await task;
            }
            return value;
        }

        // Check for yield expression - evaluate its value asynchronously
        if (expr is Expr.Yield yieldExpr)
        {
            object? value = null;
            if (yieldExpr.Value != null)
            {
                value = await EvaluateAsync(yieldExpr.Value);
            }
            throw new YieldException(value, yieldExpr.IsDelegating);
        }

        // For other expressions, evaluate asynchronously to support nested await
        return (await _interpreter.EvaluateAsync(expr)).ToObject();
    }

    /// <summary>
    /// Handles a yield exception by collecting the value.
    /// </summary>
    private async Task HandleYieldAsync(YieldException yield)
    {
        if (yield.IsDelegating)
        {
            // yield* - delegate to another iterable
            var value = yield.Value;

            // If delegating to an async iterable, await each value
            if (value is SharpTSAsyncGenerator asyncGen)
            {
                while (true)
                {
                    var result = await asyncGen.Next();
                    if (result is SharpTSIteratorResult ir)
                    {
                        if (ir.Done) break;
                        _values!.Add(ir.Value);
                    }
                    else
                    {
                        _values!.Add(result);
                    }
                }
            }
            else
            {
                var elements = GetIterableElements(value);
                foreach (var element in elements)
                {
                    _values!.Add(element);
                }
            }
        }
        else
        {
            // If yielding a promise, await it first
            var value = yield.Value;
            if (value is Task<object?> task)
            {
                value = await task;
            }
            _values!.Add(value);
        }
    }

    /// <summary>
    /// Gets elements from an iterable value.
    /// </summary>
    private static IEnumerable<object?> GetIterableElements(object? value)
    {
        return value switch
        {
            SharpTSArray array => array,
            SharpTSGenerator gen => gen,
            SharpTSIterator iter => iter.Elements,
            SharpTSMap map => map.Entries().Elements,
            SharpTSSet set => set.Values().Elements,
            string s => s.Select(c => (object?)c.ToString()),
            IEnumerable<object?> enumerable => enumerable,
            null => [],
            _ => throw new Exception($"Runtime Error: Cannot iterate over non-iterable value.")
        };
    }

    private static bool IsTruthy(object? obj) => RuntimeTypes.IsTruthy(obj);

    public override string ToString() => "[object AsyncGenerator]";
}
