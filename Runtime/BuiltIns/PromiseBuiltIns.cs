using SharpTS.Execution;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Built-in methods for Promise objects.
/// Provides both instance methods (.then, .catch, .finally) and
/// static methods (Promise.all, Promise.race, Promise.resolve, Promise.reject).
/// </summary>
public static class PromiseBuiltIns
{
    /// <summary>
    /// Gets an instance member of a Promise.
    /// The interpreter is passed at call time through BuiltInAsyncMethod.
    /// </summary>
    /// <remarks>
    /// For Promise subclass instances (#242), then/catch/finally construct
    /// their result promise through SpeciesConstructor(promise, %Promise%)
    /// (ECMA-262 §27.2.5.4 step 3, §7.3.22) — i.e. the value of
    /// <c>promise.constructor[Symbol.species]</c>, defaulting to the receiver's
    /// own class when not overridden and to <c>%Promise%</c> when the override
    /// yields <c>undefined</c>/<c>null</c> or <c>Promise</c> itself (#221).
    /// </remarks>
    public static object? GetMember(SharpTSPromise receiver, string name)
    {
        return name switch
        {
            "then" => new BuiltInAsyncMethod("then", 1, 2, (interp, recv, args) =>
                ThenImpl((SharpTSPromise)recv!, args, interp), speciesResolver: SpeciesResolver).Bind(receiver),

            "catch" => new BuiltInAsyncMethod("catch", 1, 1, (interp, recv, args) =>
                CatchImpl((SharpTSPromise)recv!, args, interp), speciesResolver: SpeciesResolver).Bind(receiver),

            "finally" => new BuiltInAsyncMethod("finally", 1, 1, (interp, recv, args) =>
                FinallyImpl((SharpTSPromise)recv!, args, interp), speciesResolver: SpeciesResolver).Bind(receiver),

            // ECMA-262 §27.2.5.1: Promise.prototype.constructor is %Promise%.
            // (Subclass instances report their own class — see
            // Interpreter.Properties.cs. then/catch/finally now consult
            // SpeciesConstructor via the SpeciesResolver pre-step, #221/#350.)
            "constructor" => Interpreter.PromiseGlobalValue,

            _ => null
        };
    }

    /// <summary>
    /// Gets a static method from the Promise namespace.
    /// </summary>
    public static ISharpTSCallable? GetStaticMethod(string name) => GetStaticMethod(name, null);

    /// <summary>
    /// Gets a static method from the Promise static side. When
    /// <paramref name="subclass"/> is a guest Promise subclass (#242), the
    /// returned method constructs its result promise through that class so
    /// inherited statics (e.g. <c>MyPromise.resolve(v)</c>) produce
    /// subclass-typed instances.
    /// </summary>
    public static ISharpTSCallable? GetStaticMethod(string name, SharpTSPromiseClass? subclass)
    {
        var factory = DerivedPromiseFactory(subclass);
        return name switch
        {
            "all" => new BuiltInAsyncMethod("all", 1, 1, (interp, _, args) =>
                AllImpl(args, interp), factory),

            "race" => new BuiltInAsyncMethod("race", 1, 1, (interp, _, args) =>
                RaceImpl(args, interp), factory),

            "resolve" => new BuiltInAsyncMethod("resolve", 0, 1, (_, _, args) =>
                ResolveImplAsync(args), factory),

            "reject" => new BuiltInAsyncMethod("reject", 1, 1, (_, _, args) =>
                Task.FromResult(RejectImpl(args)), factory),

            "allSettled" => new BuiltInAsyncMethod("allSettled", 1, 1, (interp, _, args) =>
                AllSettledImpl(args, interp), factory),

            "any" => new BuiltInAsyncMethod("any", 1, 1, (interp, _, args) =>
                AnyImpl(args, interp), factory),

            "withResolvers" => BuiltInMethod.CreateV2("withResolvers", 0, (interp, _, _) =>
                RuntimeValue.FromBoxed(WithResolversImpl(interp, factory))),

            _ => null
        };
    }

    /// <summary>
    /// Returns the derived-promise factory for a Promise subclass static-side
    /// constructor, or null for the base Promise (which keeps the default
    /// <see cref="SharpTSPromise"/> wrapping). Used by the static methods
    /// (<c>resolve</c>/<c>reject</c>/<c>all</c>/<c>race</c>/<c>allSettled</c>/
    /// <c>any</c>/<c>withResolvers</c>), which per ECMA-262 build their result
    /// through the receiver constructor <c>C</c> <em>directly</em> (e.g.
    /// §27.2.4.1 <c>Promise.all</c> step 2 <c>NewPromiseCapability(C)</c>) — no
    /// <c>@@species</c> indirection (that applies only to the prototype methods;
    /// see <see cref="SpeciesPromiseFactory"/>).
    /// </summary>
    private static Func<Interpreter, Task<object?>, SharpTSPromise>? DerivedPromiseFactory(object? receiverOrClass)
    {
        var klass = receiverOrClass switch
        {
            SharpTSPromiseSubclassInstance sub => sub.Klass,
            SharpTSPromiseClass pc => pc,
            _ => null
        };
        // PromiseBase itself (the bridge singleton for the built-in
        // constructor) keeps the plain wrapping.
        if (klass == null || ReferenceEquals(klass, SharpTSPromiseClass.PromiseBase))
            return null;
        return (interp, task) => klass.ConstructDerived(interp, task);
    }

    /// <summary>
    /// The synchronous SpeciesConstructor pre-step shared by
    /// <c>then</c>/<c>catch</c>/<c>finally</c> (passed to
    /// <see cref="BuiltInAsyncMethod"/> as its <c>speciesResolver</c>). Run at
    /// call time before the rejected-promise conversion, so a poisoned
    /// <c>constructor</c>/<c>@@species</c> getter throws synchronously (#350).
    /// </summary>
    private static readonly Func<Interpreter, object?, Func<Interpreter, Task<object?>, SharpTSPromise>?> SpeciesResolver =
        static (interp, recv) => ResolveResultPromiseFactory((SharpTSPromise)recv!, interp);

    /// <summary>
    /// Computes the result-promise factory for the prototype methods
    /// (<c>then</c>/<c>catch</c>/<c>finally</c>) per ECMA-262 §27.2.5.4 step 3,
    /// <c>SpeciesConstructor(promise, %Promise%)</c> (§7.3.22). Returns null to
    /// mean <c>%Promise%</c> (plain wrapping) or a factory that constructs the
    /// result through a guest Promise class.
    /// </summary>
    /// <remarks>
    /// §7.3.22 step 1 is <c>Get(promise, "constructor")</c>: an own
    /// <c>constructor</c> accessor installed via <c>Object.defineProperty</c>
    /// (a poisoned getter, test262 then/ctor-poisoned, #350) is invoked here and
    /// a throw propagates synchronously. The getter's RETURN value does not
    /// redirect species — the receiver's own class still drives the result; an
    /// own <c>constructor</c> that resolves to a DIFFERENT constructor (or an own
    /// data property, or the general non-Promise NewPromiseCapability) is the
    /// #349/#350 remainder, and is kept symmetric with the compiled
    /// <c>WrapDerivedPromiseResult</c> path. Absent an own override, the inherited
    /// <c>Promise.prototype.constructor</c> is the receiver's own class (subclass)
    /// or <c>%Promise%</c> (plain); a subclass then reads its static
    /// <c>@@species</c> (#221).
    /// </remarks>
    private static Func<Interpreter, Task<object?>, SharpTSPromise>? ResolveResultPromiseFactory(
        SharpTSPromise receiver, Interpreter interp)
    {
        if (receiver.TryGetAccessor("constructor", out var ctorGetter, out _) && ctorGetter != null)
            ctorGetter.Call(interp, []);   // side effect only: a poisoned getter throws → propagates

        // The receiver's own class drives species: a subclass reads its static
        // @@species (#221); a plain promise stays %Promise% (null factory).
        return receiver is SharpTSPromiseSubclassInstance sub
            ? SpeciesMaterializer(ResolveSpeciesConstructor(sub.Klass, interp))
            : null;
    }

    /// <summary>
    /// Wraps a resolved species class into a result-promise factory, or returns
    /// null (meaning <c>%Promise%</c>, the plain wrapping) when species is null.
    /// </summary>
    private static Func<Interpreter, Task<object?>, SharpTSPromise>? SpeciesMaterializer(SharpTSPromiseClass? species)
        => species == null ? null : (interp, task) => species.ConstructDerived(interp, task);

    /// <summary>
    /// Computes SpeciesConstructor(promise, %Promise%) (ECMA-262 §7.3.22) for a
    /// Promise subclass receiver, returning the guest Promise class to construct
    /// the result through, or <c>null</c> to mean <c>%Promise%</c> (the plain
    /// built-in). Reads <c>C[@@species]</c> where <c>C</c> is the receiver's
    /// class: a declared <c>static get [Symbol.species]()</c> or an expando
    /// <c>(C as any)[Symbol.species]</c> (#262) override wins; absent either,
    /// the inherited <c>Promise[@@species]</c> (which returns <c>this</c>) makes
    /// the species the receiver's own class. An override that yields
    /// <c>undefined</c>/<c>null</c> or <c>Promise</c> itself resolves to
    /// <c>%Promise%</c>.
    /// </summary>
    /// <remarks>
    /// A species override that returns a non-Promise constructor (the general
    /// NewPromiseCapability path) is not yet supported and falls back to
    /// <c>%Promise%</c> — tracked by #349. A poisoned own <c>constructor</c>
    /// getter (<c>then/ctor-poisoned.js</c>, #350) is handled earlier, in
    /// <see cref="ResolveResultPromiseFactory"/>, which reads
    /// <c>Get(promise, "constructor")</c> before reaching here.
    /// </remarks>
    private static SharpTSPromiseClass? ResolveSpeciesConstructor(SharpTSPromiseClass klass, Interpreter interp)
    {
        object? species;
        if (klass.FindStaticSymbolGetter(SharpTSSymbol.Species) is { } getter)
            species = getter.BindStatic(klass).Call(interp, []);
        else if (klass.TryGetStaticBySymbol(SharpTSSymbol.Species, out var expando))
            species = expando;
        else
            // No own @@species: inherited Promise[@@species] returns `this`.
            return klass;

        return species switch
        {
            null or SharpTSUndefined => null,                                  // → %Promise%
            SharpTSBuiltInConstructor { Name: BuiltInNames.Promise } => null,  // `return Promise`
            SharpTSPromiseClass sc when ReferenceEquals(sc, SharpTSPromiseClass.PromiseBase) => null,
            SharpTSPromiseClass sc => sc,
            // Non-Promise species constructor: general NewPromiseCapability not
            // yet supported (#349) — fall back to %Promise%.
            _ => null
        };
    }

    #region Instance Methods

    /// <summary>
    /// Implementation of Promise.prototype.then(onFulfilled?, onRejected?)
    /// </summary>
    private static async Task<object?> ThenImpl(
        SharpTSPromise promise,
        List<object?> args,
        Interpreter interpreter)
    {
        var onFulfilled = args.Count > 0 ? args[0] as ISharpTSCallable : null;
        var onRejected = args.Count > 1 ? args[1] as ISharpTSCallable : null;

        // ECMA-262 §27.2.5.4: onRejected only handles rejection of the INPUT
        // promise. Guard only the input await with the rejection dispatch —
        // a throwing onFulfilled (or a rejecting thenable it returned) must
        // reject the output promise, not invoke this same then's onRejected
        // (#195). Handler invocation happens after this try.
        object? value;
        try
        {
            value = await promise.GetValueAsync();
        }
        catch (SharpTSPromiseRejectedException ex)
        {
            if (onRejected != null)
            {
                return await InvokeHandler(onRejected, ex.Reason, interpreter);
            }

            // No onRejected callback - re-throw to propagate rejection
            throw;
        }
        catch (AggregateException aggEx) when (aggEx.InnerException is SharpTSPromiseRejectedException rejEx)
        {
            if (onRejected != null)
            {
                return await InvokeHandler(onRejected, rejEx.Reason, interpreter);
            }
            throw rejEx;
        }

        // Fulfilled: call onFulfilled (its throw rejects the output promise)
        if (onFulfilled != null)
        {
            return await InvokeHandler(onFulfilled, value, interpreter);
        }

        // No onFulfilled callback - pass through value
        return value;
    }

    /// <summary>
    /// Invokes a then/catch reaction handler. A throwing handler rejects the
    /// output promise with the thrown value (ECMA-262 §27.2.5.4) instead of
    /// letting the guest ThrowException fault the task as a host error.
    /// A rejected promise returned by the handler propagates unchanged.
    /// </summary>
    private static async Task<object?> InvokeHandler(
        ISharpTSCallable handler, object? arg, Interpreter interpreter)
    {
        try
        {
            var result = CallCallback(handler, [arg], interpreter);
            return await UnwrapResult(result);
        }
        catch (Exceptions.ThrowException tex)
        {
            throw new SharpTSPromiseRejectedException(tex.Value);
        }
    }

    /// <summary>
    /// Implementation of Promise.prototype.catch(onRejected)
    /// Equivalent to .then(undefined, onRejected)
    /// </summary>
    private static async Task<object?> CatchImpl(
        SharpTSPromise promise,
        List<object?> args,
        Interpreter interpreter)
    {
        var onRejected = args.Count > 0 ? args[0] as ISharpTSCallable : null;

        try
        {
            // Wait for the promise to settle
            return await promise.GetValueAsync();
        }
        catch (SharpTSPromiseRejectedException ex)
        {
            if (onRejected != null)
            {
                return await InvokeHandler(onRejected, ex.Reason, interpreter);
            }
            throw;
        }
        catch (AggregateException aggEx) when (aggEx.InnerException is SharpTSPromiseRejectedException rejEx)
        {
            if (onRejected != null)
            {
                return await InvokeHandler(onRejected, rejEx.Reason, interpreter);
            }
            throw rejEx;
        }
    }

    /// <summary>
    /// Implementation of Promise.prototype.finally(onFinally)
    /// Callback receives no arguments and does not alter the result.
    /// </summary>
    private static async Task<object?> FinallyImpl(
        SharpTSPromise promise,
        List<object?> args,
        Interpreter interpreter)
    {
        var onFinally = args.Count > 0 ? args[0] as ISharpTSCallable : null;
        object? value = null;
        Exception? error = null;

        try
        {
            value = await promise.GetValueAsync();
        }
        catch (Exception ex)
        {
            error = ex;
        }

        // Call the finally callback (with no arguments)
        if (onFinally != null)
        {
            try
            {
                var result = CallCallback(onFinally, [], interpreter);
                // If callback returns a Promise, wait for it
                if (result is SharpTSPromise p)
                {
                    await p.GetValueAsync();
                }
            }
            catch (Exception callbackError)
            {
                // If callback throws, that becomes the new rejection reason
                throw new SharpTSPromiseRejectedException(callbackError.Message);
            }
        }

        // Re-throw original error or return original value
        if (error != null)
        {
            throw error;
        }

        return value;
    }

    #endregion

    #region Static Methods

    /// <summary>
    /// Implementation of Promise.all(iterable)
    /// Resolves when all promises resolve, rejects on first rejection.
    /// </summary>
    private static async Task<object?> AllImpl(List<object?> args, Interpreter interpreter)
    {
        if (args.Count == 0 || args[0] is not SharpTSArray array)
        {
            throw new Exception("Runtime Error: Promise.all requires an array argument.");
        }

        // Empty array resolves immediately to empty array
        if (array.Length == 0)
        {
            return new SharpTSArray([]);
        }

        var tasks = new List<Task<object?>>();

        foreach (var element in array)
        {
            if (element is SharpTSPromise promise)
            {
                tasks.Add(promise.GetValueAsync());
            }
            else
            {
                // Non-promise values are treated as immediately resolved
                tasks.Add(Task.FromResult(element));
            }
        }

        // Wait for all promises - will throw on first rejection
        var results = await Task.WhenAll(tasks);
        return new SharpTSArray(new List<object?>(results));
    }

    /// <summary>
    /// Implementation of Promise.race(iterable)
    /// Resolves/rejects with the first promise to settle.
    /// </summary>
    private static async Task<object?> RaceImpl(List<object?> args, Interpreter interpreter)
    {
        if (args.Count == 0 || args[0] is not SharpTSArray array)
        {
            throw new Exception("Runtime Error: Promise.race requires an array argument.");
        }

        // Empty array never settles — there are no competitors to race.
        // BuiltInAsyncMethod wraps this method's Task in the promise it hands
        // to the guest, so returning a SharpTSPromise here would settle that
        // outer promise immediately WITH a promise object (#196). Await a task
        // that never completes instead so the outer promise stays pending.
        if (array.Length == 0)
        {
            return await new TaskCompletionSource<object?>().Task;
        }

        var tasks = new List<Task<object?>>();

        foreach (var element in array)
        {
            if (element is SharpTSPromise promise)
            {
                tasks.Add(promise.GetValueAsync());
            }
            else
            {
                // Non-promise values are treated as immediately resolved
                tasks.Add(Task.FromResult(element));
            }
        }

        // Return the result of the first task to complete
        var completedTask = await Task.WhenAny(tasks);
        return await completedTask;
    }

    /// <summary>
    /// Implementation of Promise.allSettled(iterable)
    /// Returns array of outcome objects: {status: "fulfilled"|"rejected", value?: T, reason?: E}
    /// Never rejects - always resolves with all outcomes.
    /// </summary>
    private static async Task<object?> AllSettledImpl(List<object?> args, Interpreter interpreter)
    {
        if (args.Count == 0 || args[0] is not SharpTSArray array)
        {
            throw new Exception("Runtime Error: Promise.allSettled requires an array argument.");
        }

        // Empty array resolves immediately to empty array
        if (array.Length == 0)
        {
            return new SharpTSArray([]);
        }

        List<object?> results = [];

        foreach (var element in array)
        {
            try
            {
                object? value;
                if (element is SharpTSPromise promise)
                {
                    value = await promise.GetValueAsync();
                }
                else
                {
                    // Non-promise values are treated as immediately resolved
                    value = element;
                }

                // Create fulfilled outcome object
                var outcome = new SharpTSObject(new Dictionary<string, object?>
                {
                    ["status"] = "fulfilled",
                    ["value"] = value
                });
                results.Add(outcome);
            }
            catch (Exception ex)
            {
                // Create rejected outcome object carrying the guest rejection value
                var outcome = new SharpTSObject(new Dictionary<string, object?>
                {
                    ["status"] = "rejected",
                    ["reason"] = ExtractRejectionReason(ex)
                });
                results.Add(outcome);
            }
        }

        return new SharpTSArray(results);
    }

    /// <summary>
    /// State holder for Promise.any operation (used instead of ref since async methods can't have ref params)
    /// </summary>
    private class AnyState
    {
        public int PendingCount;
        public readonly List<object?> RejectionReasons = [];
        public readonly TaskCompletionSource<object?> Tcs = new();
        public readonly object Lock = new();
    }

    /// <summary>
    /// Implementation of Promise.any(iterable)
    /// First fulfilled promise wins. If all reject, throws AggregateError.
    /// </summary>
    private static async Task<object?> AnyImpl(List<object?> args, Interpreter interpreter)
    {
        if (args.Count == 0 || args[0] is not SharpTSArray array)
        {
            throw new Exception("Runtime Error: Promise.any requires an array argument.");
        }

        // Empty array rejects immediately with AggregateError. Must be a real
        // SharpTSAggregateError — the same representation `new AggregateError()`
        // produces — so `e instanceof AggregateError` holds (#232).
        if (array.Length == 0)
        {
            throw new SharpTSPromiseRejectedException(
                new SharpTSAggregateError(new SharpTSArray([])));
        }

        var state = new AnyState { PendingCount = array.Length };

        foreach (var element in array)
        {
            if (element is SharpTSPromise promise)
            {
                _ = ProcessPromiseForAny(promise.GetValueAsync(), state);
            }
            else
            {
                // Non-promise values are treated as immediately resolved - first one wins
                state.Tcs.TrySetResult(element);
            }
        }

        return await state.Tcs.Task;
    }

    /// <summary>
    /// Helper for Promise.any - processes a single promise.
    /// </summary>
    private static async Task ProcessPromiseForAny(Task<object?> task, AnyState state)
    {
        try
        {
            var result = await task;
            // First fulfillment wins
            state.Tcs.TrySetResult(result);
        }
        catch (Exception ex)
        {
            HandleRejectionForAny(ExtractRejectionReason(ex), state);
        }
    }

    /// <summary>
    /// Extracts the guest rejection value from a faulted-promise exception:
    /// the rejection Reason, a guest-thrown value (ThrowException from a
    /// `throw` inside an async function), either of those wrapped in the
    /// AggregateException that Task faults arrive in, or — last resort —
    /// the host exception message. Keeps `e.errors` / allSettled `reason`
    /// carrying what the promise actually rejected with (#232).
    /// </summary>
    private static object? ExtractRejectionReason(Exception ex)
    {
        if (ex is AggregateException agg && agg.InnerException is Exception inner)
            ex = inner;
        return ex switch
        {
            SharpTSPromiseRejectedException rejected => rejected.Reason,
            Exceptions.ThrowException thrown => thrown.Value,
            _ => ex.Message
        };
    }

    /// <summary>
    /// Helper for Promise.any - handles a rejection.
    /// </summary>
    private static void HandleRejectionForAny(object? reason, AnyState state)
    {
        lock (state.Lock)
        {
            state.RejectionReasons.Add(reason);
            state.PendingCount--;

            // If all promises rejected, reject with a real SharpTSAggregateError
            // so `e instanceof AggregateError` / `instanceof Error` hold (#232).
            if (state.PendingCount == 0)
            {
                var aggregateError = new SharpTSAggregateError(
                    new SharpTSArray(state.RejectionReasons));

                state.Tcs.TrySetException(new SharpTSPromiseRejectedException(aggregateError));
            }
        }
    }

    /// <summary>
    /// Implementation of Promise.resolve(value?)
    /// Returns the value directly - BuiltInAsyncMethod.Call wraps in a Promise.
    /// </summary>
    private static async Task<object?> ResolveImplAsync(List<object?> args)
    {
        var value = args.Count > 0 ? args[0] : null;

        // If already a Promise, await it to unwrap and avoid double-wrapping
        // (BuiltInAsyncMethod.Call will wrap the result in a new Promise)
        if (value is SharpTSPromise promise)
        {
            // Properly await the promise instead of blocking
            return await promise.GetValueAsync();
        }

        // Return the raw value - BuiltInAsyncMethod.Call will wrap it in a Promise
        return value;
    }

    /// <summary>
    /// Implementation of Promise.reject(reason)
    /// Throws an exception that BuiltInAsyncMethod.Call will convert to a rejected Promise.
    /// </summary>
    private static object? RejectImpl(List<object?> args)
    {
        var reason = args.Count > 0 ? args[0] : null;
        // Throw to let BuiltInAsyncMethod.Call create the rejected Promise
        throw new SharpTSPromiseRejectedException(reason);
    }

    /// <summary>
    /// Implementation of Promise.withResolvers()
    /// Returns {promise, resolve, reject} for external promise resolution.
    /// The promise comes from <paramref name="promiseFactory"/> when called
    /// off a Promise subclass (#242), else is a plain SharpTSPromise.
    /// </summary>
    private static object? WithResolversImpl(
        Interpreter interpreter,
        Func<Interpreter, Task<object?>, SharpTSPromise>? promiseFactory)
    {
        var tcs = new TaskCompletionSource<object?>();

        var resolveMethod = BuiltInMethod.CreateV2("resolve", 1, (_, _, args) =>
        {
            var value = args.Length > 0 ? args[0].ToObject() : null;
            tcs.TrySetResult(value);
            return RuntimeValue.Null;
        });

        var rejectMethod = BuiltInMethod.CreateV2("reject", 1, (_, _, args) =>
        {
            var reason = args.Length > 0 ? args[0].ToObject() : null;
            tcs.TrySetException(new SharpTSPromiseRejectedException(reason));
            return RuntimeValue.Null;
        });

        var promise = promiseFactory != null
            ? promiseFactory(interpreter, tcs.Task)
            : new SharpTSPromise(tcs.Task);

        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["promise"] = promise,
            ["resolve"] = resolveMethod,
            ["reject"] = rejectMethod
        });
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Calls a callback function with the given arguments.
    /// Handles both sync and async callables.
    /// </summary>
    private static object? CallCallback(ISharpTSCallable callback, List<object?> args, Interpreter interpreter)
    {
        return callback.Call(interpreter, args);
    }

    /// <summary>
    /// Unwraps a result that might be a Promise.
    /// If the result is a Promise, awaits it and flattens.
    /// </summary>
    /// <remarks>
    /// GetValueAsync() already contains a while-loop to flatten arbitrarily nested
    /// Promises, so we only need a single check here.
    /// </remarks>
    private static async Task<object?> UnwrapResult(object? result)
    {
        if (result is SharpTSPromise promise)
        {
            return await promise.GetValueAsync();
        }
        return result;
    }

    #endregion
}
