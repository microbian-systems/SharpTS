using System.Collections;
using System.Runtime.ExceptionServices;
using SharpTS.Compilation;
using SharpTS.Parsing;
using SharpTS.Runtime.Exceptions;
using SharpTS.Execution;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime object representing an active generator instance.
/// </summary>
/// <remarks>
/// Created by <see cref="SharpTSGeneratorFunction"/> when called.
/// Uses thread-based coroutines for lazy evaluation — the generator body runs on a
/// background thread and suspends at each yield point via synchronization primitives.
/// This correctly handles infinite generators and lazy iteration.
/// </remarks>
/// <seealso cref="SharpTSGeneratorFunction"/>
/// <seealso cref="SharpTSIteratorResult"/>
public class SharpTSGenerator : IEnumerable<object?>, IDisposable, ITypeCategorized
{
    /// <inheritdoc />
    public TypeCategory RuntimeCategory => TypeCategory.Generator;
    private readonly Stmt.Function _declaration;
    private readonly RuntimeEnvironment _environment;
    private readonly Interpreter _interpreter;

    // Coroutine synchronization
    private Thread? _workerThread;
    private readonly ManualResetEventSlim _callerReady = new(false);
    private readonly ManualResetEventSlim _workerReady = new(false);

    // State
    private enum State { NotStarted, Suspended, Running, Completed }
    private State _state = State.NotStarted;
    private object? _yieldedValue;
    private object? _returnValue;
    // The value passed to the next/return call that resumes the suspended yield.
    // ECMA-262 §27.5.3.3: `yield expr` evaluates to the argument of the resuming
    // `next(v)`. Defaults to undefined so a yield resumed without an explicit value
    // (for...of, yield* delegation, bare next()) evaluates to undefined.
    private object? _sentValue = SharpTSUndefined.Instance;
    private bool _closed;
    private Exception? _workerException;

    // Caller state save/restore across yield points
    private RuntimeEnvironment? _callerEnv;
    private Func<object?, bool, object?>? _callerYieldCallback;

    public SharpTSGenerator(Stmt.Function declaration, RuntimeEnvironment environment, Interpreter interpreter)
    {
        _declaration = declaration;
        _environment = environment;
        _interpreter = interpreter;
    }

    /// <summary>
    /// Advances the generator to the next yield point, resuming the suspended
    /// <c>yield</c> with <c>undefined</c> (the implicit value for for...of,
    /// yield* delegation, and a bare <c>next()</c>).
    /// </summary>
    public SharpTSIteratorResult Next() => Next(SharpTSUndefined.Instance);

    /// <summary>
    /// Advances the generator to the next yield point.
    /// </summary>
    /// <param name="sentValue">
    /// The value the resumed <c>yield</c> expression evaluates to (ECMA-262 §27.5.3.3).
    /// Ignored on the first call, which only starts the body — there is no suspended
    /// yield waiting to receive it.
    /// </param>
    public SharpTSIteratorResult Next(object? sentValue)
    {
        if (_closed || _state == State.Completed)
            return new SharpTSIteratorResult(_returnValue, done: true);

        // Save caller's environment
        _callerEnv = _interpreter.Environment;

        if (_state == State.NotStarted)
        {
            _state = State.Running;
            _workerThread = new Thread(RunBody) { IsBackground = true, Name = "Generator" };
            _workerThread.Start();
        }
        else
        {
            // Resume: hand the sent value to the suspended yield, restore the
            // generator's environment, and signal the worker.
            _sentValue = sentValue;
            _state = State.Running;
            _callerReady.Set();
        }

        // Wait for worker to yield or complete
        _workerReady.Wait();
        _workerReady.Reset();

        if (_workerException != null)
        {
            var ex = _workerException;
            _workerException = null;
            _state = State.Completed;
            ExceptionDispatchInfo.Capture(ex).Throw();
        }

        if (_state == State.Completed)
            return new SharpTSIteratorResult(_returnValue, done: true);

        return new SharpTSIteratorResult(_yieldedValue, done: false);
    }

    /// <summary>
    /// Closes the generator and returns a result with the given value.
    /// </summary>
    public SharpTSIteratorResult Return(object? value = null)
    {
        _closed = true;
        _returnValue = value;

        // If the worker is suspended, wake it so it can exit
        if (_state == State.Suspended)
        {
            _callerReady.Set();
        }

        return new SharpTSIteratorResult(value, done: true);
    }

    /// <summary>
    /// Throws an exception at the current yield point.
    /// </summary>
    public SharpTSIteratorResult Throw(object? error = null)
    {
        _closed = true;

        if (_state == State.Suspended)
        {
            _callerReady.Set();
        }

        string message = error?.ToString() ?? "Generator.throw() called";
        throw new ThrowException(error ?? message);
    }

    /// <summary>
    /// Runs the generator body on the worker thread.
    /// Uses a yield callback to suspend execution at yield points without
    /// throwing exceptions, preserving the interpreter's call stack and environment.
    /// </summary>
    private void RunBody()
    {
        RuntimeEnvironment previousEnv = _interpreter.Environment;
        _interpreter.SetEnvironment(_environment);

        // Set yield callback so yield expressions suspend the thread
        // instead of throwing YieldException
        _callerYieldCallback = _interpreter.YieldCallback;
        _interpreter.YieldCallback = HandleYieldCallback;

        try
        {
            if (_declaration.Body == null || _declaration.Body.Count == 0)
                return;

            var result = _interpreter.ExecuteBlock(_declaration.Body, _environment);
            if (result.Type == ExecutionResult.ResultType.Return)
            {
                _returnValue = result.Value.ToObject();
            }
        }
        catch (Exception ex) when (ex is not ThreadInterruptedException)
        {
            _workerException = ex;
        }
        finally
        {
            _interpreter.YieldCallback = _callerYieldCallback;
            _interpreter.SetEnvironment(previousEnv);
            _state = State.Completed;
            _workerReady.Set();
        }
    }

    /// <summary>
    /// Yield callback invoked by the interpreter when a yield expression is evaluated.
    /// Suspends the worker thread until the next Next() call.
    /// For <c>yield*</c>, iterates the delegated iterable lazily and returns its
    /// completion value per ECMA-262 §14.4.14.
    /// </summary>
    private object? HandleYieldCallback(object? value, bool isDelegating)
    {
        if (isDelegating)
        {
            return DelegateYieldStar(_interpreter, this, value, v => SuspendWithValue(v), () => _closed);
        }
        // A plain `yield` evaluates to the value passed to the resuming next(v).
        return SuspendWithValue(value);
    }

    /// <summary>
    /// Shared <c>yield*</c> delegation loop used by both generator types.
    /// For inner generators, iterates via <c>Next()</c> and captures the
    /// completion value. For other iterables, iterates lazily with no return value.
    /// </summary>
    internal static object? DelegateYieldStar(Interpreter interpreter, object? outerGen, object? iterable, Action<object?> suspend, Func<bool> isClosed)
    {
        switch (iterable)
        {
            case SharpTSGenerator gen:
                while (true)
                {
                    if (isClosed()) return null;
                    var r = gen.Next();
                    if (r.Done) return r.Value;
                    suspend(r.Value);
                }
            case SharpTSArrowGenerator agen:
                while (true)
                {
                    if (isClosed()) return null;
                    var r = agen.Next();
                    if (r.Done) return r.Value;
                    suspend(r.Value);
                }
            default:
                foreach (var element in interpreter.GetIterableElements(iterable))
                {
                    if (isClosed()) return null;
                    suspend(element);
                }
                return null;
        }
    }

    /// <summary>
    /// Suspends the worker thread, passing a single yielded value to the caller.
    /// Returns the value supplied to the resuming <c>next(v)</c> call, which becomes
    /// the result of the <c>yield</c> expression.
    /// </summary>
    private object? SuspendWithValue(object? value)
    {
        _yieldedValue = value;
        _state = State.Suspended;

        // Save generator state, restore caller state
        var generatorEnv = _interpreter.Environment;
        _interpreter.SetEnvironment(_callerEnv!);
        _interpreter.YieldCallback = _callerYieldCallback;

        _workerReady.Set();    // Signal caller that value is ready
        _callerReady.Wait();   // Wait for next Next() call
        _callerReady.Reset();

        // Save caller state (may have changed), restore generator state
        _callerEnv = _interpreter.Environment;
        _callerYieldCallback = _interpreter.YieldCallback;
        _interpreter.SetEnvironment(generatorEnv);
        _interpreter.YieldCallback = HandleYieldCallback;

        return _sentValue;
    }

    // IEnumerable implementation for for...of integration
    public IEnumerator<object?> GetEnumerator()
    {
        while (true)
        {
            var result = Next();
            if (result.Done) yield break;
            yield return result.Value;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => "[object Generator]";

    public void Dispose()
    {
        _closed = true;
        if (_state == State.Suspended)
            _callerReady.Set();
        _callerReady.Dispose();
        _workerReady.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Runtime object representing an active generator instance created from a function expression.
/// </summary>
/// <remarks>
/// Similar to <see cref="SharpTSGenerator"/> but created from <see cref="Expr.ArrowFunction"/>
/// with IsGenerator=true instead of <see cref="Stmt.Function"/>.
/// Uses thread-based coroutines for lazy evaluation.
/// </remarks>
public class SharpTSArrowGenerator : IEnumerable<object?>, IDisposable
{
    private readonly Expr.ArrowFunction _declaration;
    private readonly RuntimeEnvironment _environment;
    private readonly Interpreter _interpreter;

    // Coroutine synchronization
    private Thread? _workerThread;
    private readonly ManualResetEventSlim _callerReady = new(false);
    private readonly ManualResetEventSlim _workerReady = new(false);

    // State
    private enum State { NotStarted, Suspended, Running, Completed }
    private State _state = State.NotStarted;
    private object? _yieldedValue;
    private object? _returnValue;
    // The value passed to the resuming next(v); becomes the result of the suspended
    // yield (ECMA-262 §27.5.3.3). Defaults to undefined for implicit resumes.
    private object? _sentValue = SharpTSUndefined.Instance;
    private bool _closed;
    private Exception? _workerException;

    private RuntimeEnvironment? _callerEnv;
    private Func<object?, bool, object?>? _callerYieldCallback;

    public SharpTSArrowGenerator(Expr.ArrowFunction declaration, RuntimeEnvironment environment, Interpreter interpreter)
    {
        _declaration = declaration;
        _environment = environment;
        _interpreter = interpreter;
    }

    public SharpTSIteratorResult Next() => Next(SharpTSUndefined.Instance);

    /// <summary>
    /// Advances the generator, resuming the suspended <c>yield</c> with
    /// <paramref name="sentValue"/> (ECMA-262 §27.5.3.3). Ignored on the first call.
    /// </summary>
    public SharpTSIteratorResult Next(object? sentValue)
    {
        if (_closed || _state == State.Completed)
            return new SharpTSIteratorResult(_returnValue, done: true);

        _callerEnv = _interpreter.Environment;

        if (_state == State.NotStarted)
        {
            _state = State.Running;
            _workerThread = new Thread(RunBody) { IsBackground = true, Name = "ArrowGenerator" };
            _workerThread.Start();
        }
        else
        {
            _sentValue = sentValue;
            _state = State.Running;
            _callerReady.Set();
        }

        _workerReady.Wait();
        _workerReady.Reset();

        if (_workerException != null)
        {
            var ex = _workerException;
            _workerException = null;
            _state = State.Completed;
            ExceptionDispatchInfo.Capture(ex).Throw();
        }

        if (_state == State.Completed)
            return new SharpTSIteratorResult(_returnValue, done: true);

        return new SharpTSIteratorResult(_yieldedValue, done: false);
    }

    public SharpTSIteratorResult Return(object? value = null)
    {
        _closed = true;
        _returnValue = value;
        if (_state == State.Suspended) _callerReady.Set();
        return new SharpTSIteratorResult(value, done: true);
    }

    public SharpTSIteratorResult Throw(object? error = null)
    {
        _closed = true;
        if (_state == State.Suspended) _callerReady.Set();
        string message = error?.ToString() ?? "Generator.throw() called";
        throw new ThrowException(error ?? message);
    }

    private void RunBody()
    {
        RuntimeEnvironment previousEnv = _interpreter.Environment;
        _interpreter.SetEnvironment(_environment);

        _callerYieldCallback = _interpreter.YieldCallback;
        _interpreter.YieldCallback = HandleYieldCallback;

        try
        {
            if (_declaration.ExpressionBody != null)
            {
                _returnValue = _interpreter.Evaluate(_declaration.ExpressionBody);
            }
            else if (_declaration.BlockBody != null && _declaration.BlockBody.Count > 0)
            {
                var result = _interpreter.ExecuteBlock(_declaration.BlockBody, _environment);
                if (result.Type == ExecutionResult.ResultType.Return)
                {
                    _returnValue = result.Value.ToObject();
                }
            }
        }
        catch (Exception ex) when (ex is not ThreadInterruptedException)
        {
            _workerException = ex;
        }
        finally
        {
            _interpreter.YieldCallback = _callerYieldCallback;
            _interpreter.SetEnvironment(previousEnv);
            _state = State.Completed;
            _workerReady.Set();
        }
    }

    private object? HandleYieldCallback(object? value, bool isDelegating)
    {
        if (isDelegating)
        {
            return SharpTSGenerator.DelegateYieldStar(_interpreter, this, value, v => SuspendWithValue(v), () => _closed);
        }
        return SuspendWithValue(value);
    }

    private object? SuspendWithValue(object? value)
    {
        _yieldedValue = value;
        _state = State.Suspended;

        // Save generator state, restore caller state
        var generatorEnv = _interpreter.Environment;
        _interpreter.SetEnvironment(_callerEnv!);
        _interpreter.YieldCallback = _callerYieldCallback;

        _workerReady.Set();
        _callerReady.Wait();
        _callerReady.Reset();

        // Save caller state (may have changed), restore generator state
        _callerEnv = _interpreter.Environment;
        _callerYieldCallback = _interpreter.YieldCallback;
        _interpreter.SetEnvironment(generatorEnv);
        _interpreter.YieldCallback = HandleYieldCallback;

        return _sentValue;
    }

    public IEnumerator<object?> GetEnumerator()
    {
        while (true)
        {
            var result = Next();
            if (result.Done) yield break;
            yield return result.Value;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => "[object Generator]";

    public void Dispose()
    {
        _closed = true;
        if (_state == State.Suspended) _callerReady.Set();
        _callerReady.Dispose();
        _workerReady.Dispose();
        GC.SuppressFinalize(this);
    }
}
