using System.Collections;
using System.Runtime.ExceptionServices;
using SharpTS.Compilation;
using SharpTS.Parsing;
using SharpTS.Runtime.Exceptions;
using SharpTS.Execution;

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
public class SharpTSGenerator : IEnumerable<object?>, IDisposable
{
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
    private bool _closed;
    private Exception? _workerException;

    // Caller state save/restore across yield points
    private RuntimeEnvironment? _callerEnv;
    private Action<object?, bool>? _callerYieldCallback;

    public SharpTSGenerator(Stmt.Function declaration, RuntimeEnvironment environment, Interpreter interpreter)
    {
        _declaration = declaration;
        _environment = environment;
        _interpreter = interpreter;
    }

    /// <summary>
    /// Advances the generator to the next yield point.
    /// </summary>
    public SharpTSIteratorResult Next()
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
            // Resume: restore generator's environment and signal worker
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
    /// </summary>
    private void HandleYieldCallback(object? value, bool isDelegating)
    {
        if (isDelegating)
        {
            // yield* — iterate the delegated iterable, yielding each value
            var elements = _interpreter.GetIterableElements(value);
            foreach (var element in elements)
            {
                if (_closed) return;
                SuspendWithValue(element);
            }
        }
        else
        {
            SuspendWithValue(value);
        }
    }

    /// <summary>
    /// Suspends the worker thread, passing a single yielded value to the caller.
    /// </summary>
    private void SuspendWithValue(object? value)
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
    private bool _closed;
    private Exception? _workerException;

    private RuntimeEnvironment? _callerEnv;
    private Action<object?, bool>? _callerYieldCallback;

    public SharpTSArrowGenerator(Expr.ArrowFunction declaration, RuntimeEnvironment environment, Interpreter interpreter)
    {
        _declaration = declaration;
        _environment = environment;
        _interpreter = interpreter;
    }

    public SharpTSIteratorResult Next()
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

    private void HandleYieldCallback(object? value, bool isDelegating)
    {
        if (isDelegating)
        {
            var elements = _interpreter.GetIterableElements(value);
            foreach (var element in elements)
            {
                if (_closed) return;
                SuspendWithValue(element);
            }
        }
        else
        {
            SuspendWithValue(value);
        }
    }

    private void SuspendWithValue(object? value)
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
