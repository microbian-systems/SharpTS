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
    // The generator's completion value. Defaults to undefined so a body that falls off the
    // end (or a no-arg return()) reports { value: undefined, done: true }, not C# null.
    private object? _returnValue = SharpTSUndefined.Instance;
    // The value passed to the next/return call that resumes the suspended yield.
    // ECMA-262 §27.5.3.3: `yield expr` evaluates to the argument of the resuming
    // `next(v)`. Defaults to undefined so a yield resumed without an explicit value
    // (for...of, yield* delegation, bare next()) evaluates to undefined.
    private object? _sentValue = SharpTSUndefined.Instance;

    // How the suspended yield is resumed (ECMA-262 §27.5.3.4). A plain next(v) resumes
    // normally; return(v)/throw(e) inject an abrupt completion at the yield point so any
    // active try/finally blocks run before the generator settles. The resuming call sets
    // this (and _injectedValue) before signaling the worker, which reads it on wake.
    private enum ResumeKind { Next, Return, Throw }
    private ResumeKind _resumeKind = ResumeKind.Next;
    // Value carried by an abrupt resume: the return value for Return, the error for Throw.
    private object? _injectedValue;

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
    /// Rejects a re-entrant resume. ECMA-262 §27.5.3.3 (GeneratorValidate) requires
    /// <c>next</c>/<c>return</c>/<c>throw</c> on a generator whose state is <c>executing</c> to
    /// throw a <c>TypeError</c>. The only way a guest call observes <see cref="State.Running"/>
    /// is re-entrancy — the body advancing itself — because a non-re-entrant caller is blocked in
    /// <see cref="AwaitWorker"/> for the whole running window. Without this guard the re-entrant
    /// call (which runs on the worker thread) would signal the worker to resume itself and then
    /// wait on the same worker-ready event, deadlocking both threads (#515). Checked before the
    /// started/completed branches, matching the spec (executing is rejected before completed is
    /// observed).
    /// </summary>
    private void ThrowIfExecuting()
    {
        if (_state == State.Running)
            throw new ThrowException(new SharpTSTypeError("Generator is already running"));
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
        ThrowIfExecuting();

        // A finished or disposed generator yields nothing more. The completion value is
        // delivered exactly once (when the body finishes); later next() calls report
        // undefined (ECMA-262 §27.5.3.3 → CreateIterResultObject(undefined, true)).
        if (_closed || _state == State.Completed)
            return new SharpTSIteratorResult(SharpTSUndefined.Instance, done: true);

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
            _resumeKind = ResumeKind.Next;
            _sentValue = sentValue;
            _state = State.Running;
            _callerReady.Set();
        }

        return AwaitWorker();
    }

    /// <summary>
    /// Resumes a suspended generator with a <c>return</c> abrupt completion, running any
    /// active <c>try</c>/<c>finally</c> blocks before it settles as done
    /// (ECMA-262 §27.5.3.4). A not-yet-started generator is closed without running its
    /// body; an already-finished one simply reports the supplied value. If a <c>finally</c>
    /// block yields, the generator suspends there and this returns that yielded value.
    /// </summary>
    public SharpTSIteratorResult Return(object? value = null)
    {
        ThrowIfExecuting();

        // Never started: close without running the body — there is no finally to run.
        if (_state == State.NotStarted)
        {
            _state = State.Completed;
            return new SharpTSIteratorResult(value, done: true);
        }

        // Already finished/disposed: report { value, done: true } (no body to resume).
        if (_closed || _state == State.Completed)
            return new SharpTSIteratorResult(value, done: true);

        // Suspended: inject the return at the yield point so finally blocks run. The body's
        // completion (which a finally block may override) becomes the reported value.
        _callerEnv = _interpreter.Environment;
        _resumeKind = ResumeKind.Return;
        _injectedValue = value;
        _state = State.Running;
        _callerReady.Set();

        return AwaitWorker();
    }

    /// <summary>
    /// Resumes a suspended generator with a <c>throw</c> abrupt completion at the yield
    /// point, running active <c>catch</c>/<c>finally</c> blocks (ECMA-262 §27.5.3.4). If a
    /// <c>catch</c> handles it the generator continues (and may yield again); otherwise the
    /// error propagates to this caller. A not-yet-started or finished generator just throws.
    /// </summary>
    public SharpTSIteratorResult Throw(object? error = null)
    {
        ThrowIfExecuting();

        // Never started or already finished/disposed: nothing to resume — just throw.
        if (_state == State.NotStarted || _closed || _state == State.Completed)
        {
            _state = State.Completed;
            throw ThrowException.FromResult(error);
        }

        // Suspended: inject the throw at the yield point so catch/finally blocks run.
        _callerEnv = _interpreter.Environment;
        _resumeKind = ResumeKind.Throw;
        _injectedValue = error;
        _state = State.Running;
        _callerReady.Set();

        return AwaitWorker();
    }

    /// <summary>
    /// Blocks until the worker reaches its next yield or completes, then either rethrows a
    /// worker exception (an uncaught throw or a finally that threw) or builds the iterator
    /// result. Shared by <see cref="Next(object?)"/>, <see cref="Return"/> and <see cref="Throw"/>.
    /// </summary>
    private SharpTSIteratorResult AwaitWorker()
    {
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
            else if (result.Type == ExecutionResult.ResultType.Throw)
            {
                // An uncaught guest throw — from the body itself or from a throw(e) injection
                // that no catch handled — completes the generator abnormally and propagates
                // to the resuming next()/throw() caller.
                _workerException = ThrowException.FromResult(result.Value.ToObject());
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
        _callerReady.Wait();   // Wait for the resuming next/return/throw call
        _callerReady.Reset();

        // Save caller state (may have changed), restore generator state
        _callerEnv = _interpreter.Environment;
        _callerYieldCallback = _interpreter.YieldCallback;
        _interpreter.SetEnvironment(generatorEnv);
        _interpreter.YieldCallback = HandleYieldCallback;

        // Inject the abrupt completion requested by return(v)/throw(e) at this yield point
        // (ECMA-262 §27.5.3.4). The exception unwinds through any active try/finally blocks
        // on the worker's call stack — running their finally handlers — before settling.
        // Consume the resume kind so a later wake that doesn't set it (e.g. Dispose) resumes
        // normally instead of re-injecting a stale abrupt completion.
        var kind = _resumeKind;
        var injected = _injectedValue;
        _resumeKind = ResumeKind.Next;
        _injectedValue = null;
        switch (kind)
        {
            case ResumeKind.Return:
                throw new GeneratorReturnException(injected);
            case ResumeKind.Throw:
                throw ThrowException.FromResult(injected);
            default:
                return _sentValue;
        }
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
    // The generator's completion value. Defaults to undefined so a body that falls off the
    // end (or a no-arg return()) reports { value: undefined, done: true }, not C# null.
    private object? _returnValue = SharpTSUndefined.Instance;
    // The value passed to the resuming next(v); becomes the result of the suspended
    // yield (ECMA-262 §27.5.3.3). Defaults to undefined for implicit resumes.
    private object? _sentValue = SharpTSUndefined.Instance;

    // How the suspended yield is resumed (ECMA-262 §27.5.3.4): a plain next(v), or a
    // return(v)/throw(e) that injects an abrupt completion so active try/finally blocks run.
    private enum ResumeKind { Next, Return, Throw }
    private ResumeKind _resumeKind = ResumeKind.Next;
    // Value carried by an abrupt resume: the return value for Return, the error for Throw.
    private object? _injectedValue;

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

    /// <summary>
    /// Rejects a re-entrant resume with a <c>TypeError</c> (ECMA-262 §27.5.3.3). See
    /// <see cref="SharpTSGenerator.ThrowIfExecuting"/> — observing <see cref="State.Running"/>
    /// from a guest call means the body is advancing itself, which would otherwise deadlock (#515).
    /// </summary>
    private void ThrowIfExecuting()
    {
        if (_state == State.Running)
            throw new ThrowException(new SharpTSTypeError("Generator is already running"));
    }

    public SharpTSIteratorResult Next() => Next(SharpTSUndefined.Instance);

    /// <summary>
    /// Advances the generator, resuming the suspended <c>yield</c> with
    /// <paramref name="sentValue"/> (ECMA-262 §27.5.3.3). Ignored on the first call.
    /// </summary>
    public SharpTSIteratorResult Next(object? sentValue)
    {
        ThrowIfExecuting();

        // A finished/disposed generator delivers undefined; the completion value is reported
        // only once, when the body finishes (ECMA-262 §27.5.3.3).
        if (_closed || _state == State.Completed)
            return new SharpTSIteratorResult(SharpTSUndefined.Instance, done: true);

        _callerEnv = _interpreter.Environment;

        if (_state == State.NotStarted)
        {
            _state = State.Running;
            _workerThread = new Thread(RunBody) { IsBackground = true, Name = "ArrowGenerator" };
            _workerThread.Start();
        }
        else
        {
            _resumeKind = ResumeKind.Next;
            _sentValue = sentValue;
            _state = State.Running;
            _callerReady.Set();
        }

        return AwaitWorker();
    }

    /// <summary>
    /// Resumes a suspended generator with a <c>return</c> abrupt completion, running active
    /// <c>try</c>/<c>finally</c> blocks (ECMA-262 §27.5.3.4). See <see cref="SharpTSGenerator.Return"/>.
    /// </summary>
    public SharpTSIteratorResult Return(object? value = null)
    {
        ThrowIfExecuting();

        if (_state == State.NotStarted)
        {
            _state = State.Completed;
            return new SharpTSIteratorResult(value, done: true);
        }

        if (_closed || _state == State.Completed)
            return new SharpTSIteratorResult(value, done: true);

        _callerEnv = _interpreter.Environment;
        _resumeKind = ResumeKind.Return;
        _injectedValue = value;
        _state = State.Running;
        _callerReady.Set();

        return AwaitWorker();
    }

    /// <summary>
    /// Resumes a suspended generator with a <c>throw</c> abrupt completion, running active
    /// <c>catch</c>/<c>finally</c> blocks (ECMA-262 §27.5.3.4). See <see cref="SharpTSGenerator.Throw"/>.
    /// </summary>
    public SharpTSIteratorResult Throw(object? error = null)
    {
        ThrowIfExecuting();

        if (_state == State.NotStarted || _closed || _state == State.Completed)
        {
            _state = State.Completed;
            throw ThrowException.FromResult(error);
        }

        _callerEnv = _interpreter.Environment;
        _resumeKind = ResumeKind.Throw;
        _injectedValue = error;
        _state = State.Running;
        _callerReady.Set();

        return AwaitWorker();
    }

    /// <summary>
    /// Blocks until the worker yields again or completes, rethrowing a worker exception or
    /// building the iterator result. Shared by next/return/throw.
    /// </summary>
    private SharpTSIteratorResult AwaitWorker()
    {
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
                else if (result.Type == ExecutionResult.ResultType.Throw)
                {
                    // An uncaught guest throw — from the body or an unhandled throw(e)
                    // injection — propagates to the resuming next()/throw() caller.
                    _workerException = ThrowException.FromResult(result.Value.ToObject());
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

        // Inject the abrupt completion requested by return(v)/throw(e) (ECMA-262 §27.5.3.4),
        // unwinding through active try/finally blocks on the worker's call stack. Consume the
        // resume kind so a later non-setting wake (e.g. Dispose) resumes normally.
        var kind = _resumeKind;
        var injected = _injectedValue;
        _resumeKind = ResumeKind.Next;
        _injectedValue = null;
        switch (kind)
        {
            case ResumeKind.Return:
                throw new GeneratorReturnException(injected);
            case ResumeKind.Throw:
                throw ThrowException.FromResult(injected);
            default:
                return _sentValue;
        }
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
