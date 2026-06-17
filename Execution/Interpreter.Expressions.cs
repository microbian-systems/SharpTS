using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using SharpTS.Modules;
using SharpTS.Parsing;
using SharpTS.Parsing.Visitors;
using SharpTS.Runtime;
using SharpTS.Runtime.BuiltIns;
using SharpTS.Runtime.Exceptions;
using SharpTS.Runtime.Types;

namespace SharpTS.Execution;

// Note: This file uses InterpreterException for runtime errors

public partial class Interpreter
{
    /// <summary>
    /// Dispatches an expression to the appropriate evaluator using the registry.
    /// </summary>
    /// <param name="expr">The expression AST node to evaluate.</param>
    /// <returns>The runtime value produced by evaluating the expression.</returns>
    /// <remarks>
    /// Central dispatch point for all expression types. Handles literals, operators,
    /// function calls, property access, and control flow expressions.
    /// For async expressions (await), this will block synchronously. Use EvaluateAsync for fully async evaluation.
    /// </remarks>
    /// <summary>
    /// Evaluates an expression, returning a boxed object for compatibility with existing code.
    /// Prefer EvaluateRV() for new code to avoid boxing overhead.
    /// </summary>
    internal object? Evaluate(Expr expr)
    {
        return _registry.DispatchExpr(expr, this).ToObject();
    }

    /// <summary>
    /// Evaluates an expression, returning a RuntimeValue without boxing.
    /// This is the fast path — use this for new code.
    /// </summary>
    internal RuntimeValue EvaluateRV(Expr expr)
    {
        return _registry.DispatchExpr(expr, this);
    }

    // Expression handlers - called by the registry via RuntimeValue dispatch.
    // All Evaluate* methods return RuntimeValue directly — no FromBoxed in dispatch.

    internal RuntimeValue VisitComma(Expr.Comma comma) { Evaluate(comma.Left); return EvaluateRV(comma.Right); }
    internal RuntimeValue VisitBinary(Expr.Binary binary) => EvaluateBinary(binary);
    internal RuntimeValue VisitLogical(Expr.Logical logical) => EvaluateLogical(logical);
    internal RuntimeValue VisitNullishCoalescing(Expr.NullishCoalescing nc) => EvaluateNullishCoalescing(nc);
    internal RuntimeValue VisitTernary(Expr.Ternary ternary) => EvaluateTernary(ternary);
    internal RuntimeValue VisitGrouping(Expr.Grouping grouping) => EvaluateRV(grouping.Expression);
    internal RuntimeValue VisitLiteral(Expr.Literal literal) => EvaluateLiteral(literal);
    internal RuntimeValue VisitUnary(Expr.Unary unary) => EvaluateUnary(unary);
    internal RuntimeValue VisitDelete(Expr.Delete delete) => EvaluateDelete(delete);
    internal RuntimeValue VisitVariable(Expr.Variable variable) => LookupVariableRV(variable.Name, variable);
    internal RuntimeValue VisitAssign(Expr.Assign assign) => EvaluateAssign(assign);
    internal RuntimeValue VisitCall(Expr.Call call) => EvaluateCall(call);
    internal RuntimeValue VisitGet(Expr.Get get) => EvaluateGet(get);
    internal RuntimeValue VisitSet(Expr.Set set) => EvaluateSet(set);
    internal RuntimeValue VisitGetPrivate(Expr.GetPrivate gp) => EvaluateGetPrivate(gp);
    internal RuntimeValue VisitSetPrivate(Expr.SetPrivate sp) => EvaluateSetPrivate(sp);
    internal RuntimeValue VisitCallPrivate(Expr.CallPrivate cp) => EvaluateCallPrivate(cp);
    internal RuntimeValue VisitThis(Expr.This thisExpr) => EvaluateThis(thisExpr);
    internal RuntimeValue VisitNew(Expr.New newExpr) => EvaluateNew(newExpr);
    internal RuntimeValue VisitArrayLiteral(Expr.ArrayLiteral array) => EvaluateArray(array);
    internal RuntimeValue VisitObjectLiteral(Expr.ObjectLiteral obj) => EvaluateObject(obj);
    internal RuntimeValue VisitGetIndex(Expr.GetIndex getIndex) => EvaluateGetIndex(getIndex);
    internal RuntimeValue VisitSetIndex(Expr.SetIndex setIndex) => EvaluateSetIndex(setIndex);
    internal RuntimeValue VisitSuper(Expr.Super super) => EvaluateSuper(super);
    internal RuntimeValue VisitCompoundAssign(Expr.CompoundAssign compound) => EvaluateCompoundAssign(compound);
    internal RuntimeValue VisitCompoundSet(Expr.CompoundSet compoundSet) => EvaluateCompoundSet(compoundSet);
    internal RuntimeValue VisitCompoundSetIndex(Expr.CompoundSetIndex compoundSetIndex) => EvaluateCompoundSetIndex(compoundSetIndex);
    internal RuntimeValue VisitLogicalAssign(Expr.LogicalAssign logical) => EvaluateLogicalAssign(logical);
    internal RuntimeValue VisitLogicalSet(Expr.LogicalSet logicalSet) => EvaluateLogicalSet(logicalSet);
    internal RuntimeValue VisitLogicalSetIndex(Expr.LogicalSetIndex logicalSetIndex) => EvaluateLogicalSetIndex(logicalSetIndex);
    internal RuntimeValue VisitPrefixIncrement(Expr.PrefixIncrement prefix) => EvaluatePrefixIncrement(prefix);
    internal RuntimeValue VisitPostfixIncrement(Expr.PostfixIncrement postfix) => EvaluatePostfixIncrement(postfix);
    internal RuntimeValue VisitArrowFunction(Expr.ArrowFunction arrow) => EvaluateArrowFunction(arrow);
    internal RuntimeValue VisitTemplateLiteral(Expr.TemplateLiteral template) => EvaluateTemplateLiteral(template);
    internal RuntimeValue VisitTaggedTemplateLiteral(Expr.TaggedTemplateLiteral tagged) => EvaluateTaggedTemplateLiteral(tagged);
    internal RuntimeValue VisitSpread(Expr.Spread spread) => EvaluateRV(spread.Expression);
    internal RuntimeValue VisitTypeAssertion(Expr.TypeAssertion ta) => EvaluateRV(ta.Expression);
    internal RuntimeValue VisitSatisfies(Expr.Satisfies sat) => EvaluateRV(sat.Expression);
    internal RuntimeValue VisitNonNullAssertion(Expr.NonNullAssertion nna) => EvaluateRV(nna.Expression);
    internal RuntimeValue VisitAwait(Expr.Await awaitExpr) => throw new InterpreterException("'await' can only be used inside async functions.");
    internal RuntimeValue VisitDynamicImport(Expr.DynamicImport di) => EvaluateDynamicImport(di);
    internal RuntimeValue VisitImportMeta(Expr.ImportMeta im) => EvaluateImportMeta(im);
    internal RuntimeValue VisitYield(Expr.Yield yieldExpr) => EvaluateYield(yieldExpr);
    internal RuntimeValue VisitRegexLiteral(Expr.RegexLiteral regex) => RuntimeValue.FromObject(new SharpTSRegExp(regex.Pattern, regex.Flags));
    internal RuntimeValue VisitClassExpr(Expr.ClassExpr classExpr) => EvaluateClassExpression(classExpr);

    /// <summary>
    /// Asynchronously dispatches an expression to the appropriate evaluator.
    /// </summary>
    /// <param name="expr">The expression AST node to evaluate.</param>
    /// <returns>A task that resolves to the runtime value produced by evaluating the expression.</returns>
    /// <remarks>
    /// Async version of Evaluate that properly handles await expressions without blocking.
    /// Used by async functions and arrow functions.
    /// </remarks>
    /// <summary>
    /// Asynchronously dispatches an expression to the appropriate evaluator.
    /// </summary>
    /// <param name="expr">The expression AST node to evaluate.</param>
    /// <returns>A task that resolves to the runtime value produced by evaluating the expression.</returns>
    /// <remarks>
    /// Async version of Evaluate that properly handles await expressions without blocking.
    /// Used by async functions and arrow functions.
    /// </remarks>
    internal async Task<RuntimeValue> EvaluateAsync(Expr expr)
    {
        switch (expr)
        {
            case Expr.Comma comma: await EvaluateAsync(comma.Left); return await EvaluateAsync(comma.Right);
            case Expr.Binary binary: return await EvaluateBinaryAsync(binary);
            case Expr.Logical logical: return await EvaluateLogicalAsync(logical);
            case Expr.NullishCoalescing nc: return await EvaluateNullishCoalescingAsync(nc);
            case Expr.Ternary ternary: return await EvaluateTernaryAsync(ternary);
            case Expr.Grouping grouping: return await EvaluateAsync(grouping.Expression);
            case Expr.Literal literal: return EvaluateLiteral(literal);
            case Expr.Unary unary: return await EvaluateUnaryAsync(unary);
            case Expr.Delete delete: return RuntimeValue.FromBoxed(await EvaluateDeleteAsync(delete));
            case Expr.Variable variable: return LookupVariableRV(variable.Name, variable);
            case Expr.Assign assign: return await EvaluateAssignAsync(assign);
            case Expr.Call call: return await EvaluateCallAsync(call);
            case Expr.Get get: return await EvaluateGetAsync(get);
            case Expr.Set set: return await EvaluateSetAsync(set);
            case Expr.GetPrivate gp: return await EvaluateGetPrivateAsync(gp);
            case Expr.SetPrivate sp: return await EvaluateSetPrivateAsync(sp);
            case Expr.CallPrivate cp: return await EvaluateCallPrivateAsync(cp);
            case Expr.This thisExpr: return EvaluateThis(thisExpr);
            case Expr.New newExpr: return await EvaluateNewAsync(newExpr);
            case Expr.ArrayLiteral array: return await EvaluateArrayAsync(array);
            case Expr.ObjectLiteral obj: return await EvaluateObjectAsync(obj);
            case Expr.GetIndex getIndex: return await EvaluateGetIndexAsync(getIndex);
            case Expr.SetIndex setIndex: return await EvaluateSetIndexAsync(setIndex);
            case Expr.Super super: return EvaluateSuper(super);
            case Expr.CompoundAssign compound: return await EvaluateCompoundAssignAsync(compound);
            case Expr.CompoundSet compoundSet: return await EvaluateCompoundSetAsync(compoundSet);
            case Expr.CompoundSetIndex compoundSetIndex: return await EvaluateCompoundSetIndexAsync(compoundSetIndex);
            case Expr.LogicalAssign logical: return await EvaluateLogicalAssignAsync(logical);
            case Expr.LogicalSet logicalSet: return await EvaluateLogicalSetAsync(logicalSet);
            case Expr.LogicalSetIndex logicalSetIndex: return await EvaluateLogicalSetIndexAsync(logicalSetIndex);
            case Expr.PrefixIncrement prefix: return await EvaluatePrefixIncrementAsync(prefix);
            case Expr.PostfixIncrement postfix: return await EvaluatePostfixIncrementAsync(postfix);
            case Expr.ArrowFunction arrow: return EvaluateArrowFunction(arrow);
            case Expr.TemplateLiteral template: return await EvaluateTemplateLiteralAsync(template);
            case Expr.TaggedTemplateLiteral tagged: return await EvaluateTaggedTemplateLiteralAsync(tagged);
            case Expr.Spread spread: return await EvaluateAsync(spread.Expression);
            case Expr.TypeAssertion ta: return await EvaluateAsync(ta.Expression);
            case Expr.Satisfies sat: return await EvaluateAsync(sat.Expression);
            case Expr.NonNullAssertion nna: return await EvaluateAsync(nna.Expression);
            case Expr.Await awaitExpr: return await EvaluateAwaitAsync(awaitExpr);
            case Expr.DynamicImport di: return EvaluateDynamicImport(di);
            case Expr.ImportMeta im: return EvaluateImportMeta(im);
            case Expr.Yield yieldExpr: return EvaluateYield(yieldExpr);
            case Expr.RegexLiteral regex: return RuntimeValue.FromObject(new SharpTSRegExp(regex.Pattern, regex.Flags));
            case Expr.ClassExpr classExpr: return EvaluateClassExpression(classExpr);
            default: throw new InvalidOperationException($"Runtime Error: Unhandled expression type in async Interpreter: {expr.GetType().Name}");
        }
    }

    /// <summary>
    /// Evaluates a yield expression, throwing YieldException for control flow.
    /// </summary>
    /// <param name="yieldExpr">The yield expression AST node.</param>
    /// <returns>Never returns normally - always throws YieldException.</returns>
    /// <remarks>
    /// Yield expressions suspend generator execution by throwing YieldException,
    /// which is caught by SharpTSGenerator.Next() to extract the yielded value.
    /// For yield*, the IsDelegating flag indicates delegation to another iterable.
    /// </remarks>
    private RuntimeValue EvaluateYield(Expr.Yield yieldExpr)
    {
        object? value = yieldExpr.Value != null ? Evaluate(yieldExpr.Value) : null;

        // If a yield callback is set (coroutine-based generator), call it directly
        // instead of throwing. This suspends the worker thread without unwinding
        // the interpreter's call stack, preserving environment state.
        if (YieldCallback != null)
        {
            var result = YieldCallback(value, yieldExpr.IsDelegating);
            // For a plain `yield`, the callback returns the exact value passed to the
            // resuming next(v) — delivered verbatim so `next(null)` yields null and a
            // bare next() yields undefined (the generator normalizes that to undefined).
            // For `yield*`, the completion value of a non-generator delegate is undefined;
            // coalesce null → undefined to preserve that.
            if (yieldExpr.IsDelegating)
                return RuntimeValue.FromBoxed(result ?? SharpTSUndefined.Instance);
            return RuntimeValue.FromBoxed(result);
        }

        throw new YieldException(value, yieldExpr.IsDelegating);
    }

    /// <summary>
    /// Evaluates an await expression, unwrapping the Promise value.
    /// </summary>
    private async Task<RuntimeValue> EvaluateAwaitAsync(Expr.Await awaitExpr)
    {
        object? value = (await EvaluateAsync(awaitExpr.Expression)).ToObject();

        // Unwrap Promise
        if (value is SharpTSPromise promise)
        {
            return RuntimeValue.FromBoxed(await AwaitPreservingEnvironment(promise.GetValueAsync()));
        }

        // Raw Task<object?> — returned by runtime methods (e.g., Web Streams
        // read()) that need to be awaitable in both interpreter and compiled
        // modes. Compiled mode's await already recognises Task<object>; adding
        // the interpreter branch lets a single BuiltInMethod body work in
        // both without wrapping in SharpTSPromise (which compiled await does
        // not unwrap).
        if (value is Task<object?> task)
        {
            return RuntimeValue.FromBoxed(await AwaitPreservingEnvironment(task));
        }

        // Thenable adoption (ECMA-262 await → PromiseResolve, §25.6.4.5 step 8 /
        // §27.2.1.3.2): an ordinary object exposing a callable `then` is adopted
        // by invoking then(resolve, reject) and awaiting the captured capability.
        // SharpTSPromise is handled above; only plain guest thenables reach here —
        // including the general non-Promise then/catch/finally species results
        // produced by NewPromiseCapability (#349).
        if (TryGetThenable(value, out var thenFn))
        {
            return RuntimeValue.FromBoxed(await AwaitPreservingEnvironment(AdoptThenable(value!, thenFn)));
        }

        // Await on non-Promise returns the value (TypeScript behavior)
        return RuntimeValue.FromBoxed(value);
    }

    /// <summary>
    /// Detects an ordinary guest thenable: a <see cref="SharpTSObject"/> or
    /// <see cref="SharpTSInstance"/> with a callable <c>then</c> member.
    /// Promise/Task values are recognised by the caller before this is reached.
    /// </summary>
    private bool TryGetThenable(object? value, [NotNullWhen(true)] out ISharpTSCallable? thenFn)
    {
        thenFn = null;
        if (value is not (SharpTSObject or SharpTSInstance))
            return false;
        if (GetProperty(value, "then") is ISharpTSCallable fn)
        {
            thenFn = fn;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Adopts a guest thenable into a host task: invokes <c>then(resolve, reject)</c>
    /// with the receiver bound, settling the returned task from whichever callback
    /// fires first. A synchronous throw from <c>then</c> rejects the task
    /// (ECMA-262 §27.2.1.3.2 — a throw after resolve/reject is ignored).
    /// </summary>
    private Task<object?> AdoptThenable(object thenable, ISharpTSCallable thenFn)
    {
        var tcs = new TaskCompletionSource<object?>();
        var resolve = new PromiseResolveCallback(v =>
        {
            // Flatten a promise resolution value the same way the executor
            // resolve callback does, so `await` yields the eventual value.
            if (v is SharpTSPromise inner)
                inner.Task.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        tcs.TrySetException(t.Exception!.InnerException ?? t.Exception);
                    else
                        tcs.TrySetResult(t.Result);
                }, TaskScheduler.Default);
            else
                tcs.TrySetResult(v);
        });
        var reject = new PromiseRejectCallback(r => tcs.TrySetException(new SharpTSPromiseRejectedException(r)));

        try
        {
            SharpTSClass.BindMethodToReceiver(thenFn, thenable).Call(this, [resolve, reject]);
        }
        catch (ThrowException tex)
        {
            tcs.TrySetException(new SharpTSPromiseRejectedException(tex.Value));
        }
        return tcs.Task;
    }

    /// <summary>
    /// Evaluates a literal expression, wrapping BigInteger values in SharpTSBigInt.
    /// </summary>
    /// <param name="literal">The literal expression AST node.</param>
    /// <returns>The literal value, with BigInteger wrapped in SharpTSBigInt.</returns>
    private RuntimeValue EvaluateLiteral(Expr.Literal literal)
    {
        return literal.Value switch
        {
            null => RuntimeValue.Null,
            double d => RuntimeValue.FromNumber(d),
            int i => RuntimeValue.FromNumber(i),
            string s => RuntimeValue.FromString(s),
            bool b => RuntimeValue.FromBoolean(b),
            BigInteger bi => RuntimeValue.FromBigInt(new SharpTSBigInt(bi)),
            _ => RuntimeValue.FromBoxed(literal.Value)
        };
    }

    /// <summary>
    /// Evaluates a variable reference, looking up its value in the current environment.
    /// </summary>
    /// <param name="variable">The variable expression AST node.</param>
    /// <returns>The current value of the variable.</returns>
    /// <remarks>
    /// Uses side-channel resolution information if available, otherwise falls back
    /// to dynamic lookup via <see cref="RuntimeEnvironment"/>.
    /// </remarks>
    /// <seealso href="https://www.typescriptlang.org/docs/handbook/variable-declarations.html">TypeScript Variable Declarations</seealso>
    private object? EvaluateVariable(Expr.Variable variable)
    {
        return LookupVariable(variable.Name, variable);
    }

    /// <summary>
    /// Evaluates a template literal (backtick string) with embedded expressions.
    /// </summary>
    /// <param name="template">The template literal expression AST node.</param>
    /// <returns>The interpolated string result.</returns>
    /// <remarks>
    /// Alternates between static string parts and evaluated expressions,
    /// stringifying each expression result before concatenation.
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Template_literals">MDN Template Literals</seealso>
    private RuntimeValue EvaluateTemplateLiteral(Expr.TemplateLiteral template)
    {
        var evaluatedExprs = template.Expressions.Select(Evaluate).ToList();
        return RuntimeValue.FromString(BuildTemplateLiteralString(template.Strings, evaluatedExprs));
    }

    /// <summary>
    /// Evaluates a tagged template literal, invoking the tag function with strings and values.
    /// </summary>
    /// <param name="tagged">The tagged template literal AST node.</param>
    /// <returns>The result of calling the tag function.</returns>
    private RuntimeValue EvaluateTaggedTemplateLiteral(Expr.TaggedTemplateLiteral tagged)
    {
        object? tag = Evaluate(tagged.Tag);

        if (tag is not ISharpTSCallable callable)
            throw new InterpreterException("Tagged template tag must be a function.");

        // Create template strings array with raw property
        // Cooked values: null becomes undefined (or just null in our runtime)
        var cookedList = tagged.CookedStrings.Cast<object?>().ToList();
        var stringsArray = new SharpTSTemplateStringsArray(cookedList, tagged.RawStrings);

        // Evaluate all expression arguments
        List<object?> args = [stringsArray];
        foreach (var expr in tagged.Expressions)
            args.Add(Evaluate(expr));

        return RuntimeValue.FromBoxed(callable.Call(this, args));
    }

    /// <summary>
    /// Evaluates a super method access, binding the superclass method to the current instance.
    /// </summary>
    /// <param name="expr">The super expression AST node.</param>
    /// <returns>The bound method from the superclass.</returns>
    /// <remarks>
    /// Retrieves the superclass from the environment and looks up the method,
    /// then binds it to the current instance for proper <c>this</c> context.
    /// For constructor calls (super()), if no explicit constructor exists, returns a
    /// no-op callable to match TypeScript's implicit default constructor behavior.
    /// </remarks>
    /// <seealso href="https://www.typescriptlang.org/docs/handbook/2/classes.html#super-calls">TypeScript super Calls</seealso>
    private RuntimeValue EvaluateSuper(Expr.Super expr)
    {
        SharpTSClass superclass = _environment.Get(expr.Keyword).AsObject<SharpTSClass>()!;
        // `this` is usually a SharpTSInstance, but built-in-backed instances
        // (Array subclass instances) are SharpTSArrays — bind generically.
        object? thisReceiver = _environment.Get(new Token(TokenType.THIS, "this", null, 0)).ToObject();

        // super() with null Method means constructor call
        string methodName = expr.Method?.Lexeme ?? "constructor";
        ISharpTSCallable? method = superclass.FindMethod(methodName);

        // If no constructor exists, return a no-op callable for super() calls
        // This matches TypeScript's implicit default constructor behavior
        if (method == null && methodName == "constructor")
        {
            return RuntimeValue.FromObject(new NoOpCallable());
        }

        if (method == null)
        {
            throw new InterpreterException($"Undefined property '{methodName}'.");
        }

        return RuntimeValue.FromObject(SharpTSClass.BindMethodToReceiver(method, thisReceiver!));
    }

    /// <summary>
    /// A no-op callable used for super() calls when the parent class has no explicit constructor.
    /// </summary>
    private class NoOpCallable : ISharpTSCallable
    {
        public int Arity() => 0;
        public object? Call(Interpreter interpreter, List<object?> arguments) => null;
    }

    /// <summary>
    /// Evaluates an arrow function expression, creating a callable closure.
    /// </summary>
    /// <param name="arrow">The arrow function expression AST node.</param>
    /// <returns>A <see cref="SharpTSArrowFunction"/> or <see cref="SharpTSAsyncArrowFunction"/> that captures the current environment.</returns>
    /// <remarks>
    /// Arrow functions capture their lexical environment at creation time,
    /// enabling closures over outer variables. Async arrow functions return a Promise.
    /// For named function expressions, the function name is visible inside the function body
    /// for recursion, but not outside.
    /// </remarks>
    /// <seealso href="https://www.typescriptlang.org/docs/handbook/2/functions.html#arrow-functions">TypeScript Arrow Functions</seealso>
    private RuntimeValue EvaluateArrowFunction(Expr.ArrowFunction arrow)
    {
        // Named function expressions (e.g. `var f = function myFn(n) { return myFn(n-1); }`)
        // bind the self-reference inside the function's own call environment (alongside
        // parameters), not in an enclosing wrapper scope. This matches how the resolver
        // tracks the name — pushing ONE scope that contains the name plus params — so
        // the locals cache's distance values remain correct for outer-variable captures.
        // See SharpTSArrowFunction.Call for where the self-binding is installed.
        RuntimeEnvironment closure = _environment;

        ISharpTSCallable func;
        if (arrow.IsAsync)
        {
            func = new SharpTSAsyncArrowFunction(arrow, closure, arrow.HasOwnThis);
        }
        else if (arrow.IsGenerator)
        {
            // Generator function expressions - wrap in a generator-creating function
            // Note: This uses a different wrapper than Stmt.Function generators
            func = new SharpTSArrowGeneratorFunction(arrow, closure, arrow.HasOwnThis);
        }
        else
        {
            func = new SharpTSArrowFunction(arrow, closure, arrow.HasOwnThis);
        }

        return RuntimeValue.FromObject(func);
    }

    /// <summary>
    /// Gets the string name from a property key (sync version).
    /// </summary>
    private string GetPropertyKeyName(Expr.PropertyKey key)
    {
        return key switch
        {
            Expr.IdentifierKey ik => ik.Name.Lexeme,
            Expr.LiteralKey lk when lk.Literal.Type == TokenType.STRING => (string)lk.Literal.Literal!,
            Expr.LiteralKey lk when lk.Literal.Type == TokenType.NUMBER => lk.Literal.Literal!.ToString()!,
            Expr.ComputedKey ck => Evaluate(ck.Expression)?.ToString() ?? "undefined",
            _ => throw new InterpreterException("Invalid property key for accessor.")
        };
    }

    /// <summary>
    /// Gets the string name from a property key using an evaluation context.
    /// Shared between sync and async paths via IEvaluationContext.
    /// </summary>
    private async ValueTask<string> GetPropertyKeyNameCore(IEvaluationContext ctx, Expr.PropertyKey key)
    {
        return key switch
        {
            Expr.IdentifierKey ik => ik.Name.Lexeme,
            Expr.LiteralKey lk when lk.Literal.Type == TokenType.STRING => (string)lk.Literal.Literal!,
            Expr.LiteralKey lk when lk.Literal.Type == TokenType.NUMBER => lk.Literal.Literal!.ToString()!,
            Expr.ComputedKey ck => (await ctx.EvaluateExprAsync(ck.Expression)).ToObject()?.ToString() ?? "undefined",
            _ => throw new InterpreterException("Invalid property key for accessor.")
        };
    }

    /// <summary>
    /// Applies a property key-value pair to the target fields dictionaries (sync version).
    /// </summary>
    private void ApplyPropertyToFields(
        Expr.PropertyKey key,
        object? value,
        Dictionary<string, object?> stringFields,
        Dictionary<SharpTSSymbol, object?> symbolFields)
    {
        switch (key)
        {
            case Expr.IdentifierKey ik:
                stringFields[ik.Name.Lexeme] = value;
                break;
            case Expr.LiteralKey lk when lk.Literal.Type == TokenType.STRING:
                stringFields[(string)lk.Literal.Literal!] = value;
                break;
            case Expr.LiteralKey lk when lk.Literal.Type == TokenType.NUMBER:
                stringFields[lk.Literal.Literal!.ToString()!] = value;
                break;
            case Expr.ComputedKey ck:
                object? keyValue = Evaluate(ck.Expression);
                if (keyValue is SharpTSSymbol sym)
                    symbolFields[sym] = value;
                else if (keyValue is double numKey)
                    stringFields[numKey.ToString()] = value;
                else
                    stringFields[keyValue?.ToString() ?? "undefined"] = value;
                break;
        }
    }

    /// <summary>
    /// Applies a property key-value pair to the target fields dictionaries using an evaluation context.
    /// Shared between sync and async paths via IEvaluationContext.
    /// </summary>
    private async ValueTask ApplyPropertyToFieldsCore(
        IEvaluationContext ctx,
        Expr.PropertyKey key,
        object? value,
        Dictionary<string, object?> stringFields,
        Dictionary<SharpTSSymbol, object?> symbolFields)
    {
        switch (key)
        {
            case Expr.IdentifierKey ik:
                stringFields[ik.Name.Lexeme] = value;
                break;
            case Expr.LiteralKey lk when lk.Literal.Type == TokenType.STRING:
                stringFields[(string)lk.Literal.Literal!] = value;
                break;
            case Expr.LiteralKey lk when lk.Literal.Type == TokenType.NUMBER:
                stringFields[lk.Literal.Literal!.ToString()!] = value;
                break;
            case Expr.ComputedKey ck:
                object? keyValue = (await ctx.EvaluateExprAsync(ck.Expression)).ToObject();
                if (keyValue is SharpTSSymbol sym)
                    symbolFields[sym] = value;
                else if (keyValue is double numKey)
                    stringFields[numKey.ToString()] = value;
                else
                    stringFields[keyValue?.ToString() ?? "undefined"] = value;
                break;
        }
    }

    /// <summary>
    /// Core implementation for evaluating object literals, shared between sync and async paths.
    /// </summary>
    /// <param name="ctx">The evaluation context for evaluating property values and keys.</param>
    /// <param name="obj">The object literal expression AST node.</param>
    /// <returns>A ValueTask containing the evaluated object.</returns>
    private async ValueTask<object?> EvaluateObjectCore(IEvaluationContext ctx, Expr.ObjectLiteral obj)
    {
        Dictionary<string, object?> stringFields = [];
        Dictionary<SharpTSSymbol, object?> symbolFields = [];
        List<(string name, ISharpTSCallable getter)>? getters = null;
        List<(string name, ISharpTSCallable setter)>? setters = null;

        foreach (var prop in obj.Properties)
        {
            if (prop.IsSpread)
            {
                object? spreadValue = (await ctx.EvaluateExprAsync(prop.Value)).ToObject();
                ApplySpreadToFields(spreadValue, stringFields);
            }
            else if (prop.Kind == Expr.ObjectPropertyKind.Getter)
            {
                string name = await GetPropertyKeyNameCore(ctx, prop.Key!);
                var getter = CreateAccessorFunction(prop.Value);
                getters ??= [];
                getters.Add((name, getter));
            }
            else if (prop.Kind == Expr.ObjectPropertyKind.Setter)
            {
                string name = await GetPropertyKeyNameCore(ctx, prop.Key!);
                var setter = CreateSetterFunction(prop.Value, prop.SetterParam!);
                setters ??= [];
                setters.Add((name, setter));
            }
            else
            {
                object? value = (await ctx.EvaluateExprAsync(prop.Value)).ToObject();
                await ApplyPropertyToFieldsCore(ctx, prop.Key!, value, stringFields, symbolFields);
            }
        }

        var result = BuildObjectFromFields(stringFields, symbolFields);

        // Apply getters and setters
        if (getters != null)
        {
            foreach (var (name, getter) in getters)
            {
                result.DefineGetter(name, getter);
            }
        }
        if (setters != null)
        {
            foreach (var (name, setter) in setters)
            {
                result.DefineSetter(name, setter);
            }
        }

        return result;
    }

    /// <summary>
    /// Evaluates an object literal expression, creating a runtime object.
    /// </summary>
    /// <param name="obj">The object literal expression AST node.</param>
    /// <returns>A <see cref="SharpTSObject"/> containing the evaluated properties.</returns>
    /// <remarks>
    /// Supports spread properties (<c>...obj</c>) which copy all enumerable properties
    /// from the source object or instance.
    /// </remarks>
    /// <seealso href="https://www.typescriptlang.org/docs/handbook/2/objects.html">TypeScript Object Types</seealso>
    private RuntimeValue EvaluateObject(Expr.ObjectLiteral obj)
    {
        Dictionary<string, object?> stringFields = [];
        Dictionary<SharpTSSymbol, object?> symbolFields = [];
        List<(string name, ISharpTSCallable getter)>? getters = null;
        List<(string name, ISharpTSCallable setter)>? setters = null;

        foreach (var prop in obj.Properties)
        {
            if (prop.IsSpread)
            {
                object? spreadValue = Evaluate(prop.Value);
                ApplySpreadToFields(spreadValue, stringFields);
            }
            else if (prop.Kind == Expr.ObjectPropertyKind.Getter)
            {
                string name = GetPropertyKeyName(prop.Key!);
                var getter = CreateAccessorFunction(prop.Value);
                getters ??= [];
                getters.Add((name, getter));
            }
            else if (prop.Kind == Expr.ObjectPropertyKind.Setter)
            {
                string name = GetPropertyKeyName(prop.Key!);
                var setter = CreateSetterFunction(prop.Value, prop.SetterParam!);
                setters ??= [];
                setters.Add((name, setter));
            }
            else
            {
                object? value = Evaluate(prop.Value);
                ApplyPropertyToFields(prop.Key!, value, stringFields, symbolFields);
            }
        }

        var result = BuildObjectFromFields(stringFields, symbolFields);

        // Apply getters and setters
        if (getters != null)
        {
            foreach (var (name, getter) in getters)
            {
                result.DefineGetter(name, getter);
            }
        }
        if (setters != null)
        {
            foreach (var (name, setter) in setters)
            {
                result.DefineSetter(name, setter);
            }
        }

        return RuntimeValue.FromObject(result);
    }

    /// <summary>
    /// Creates an accessor function (getter) from the function expression.
    /// </summary>
    private SharpTSArrowFunction CreateAccessorFunction(Expr body)
    {
        // The body should be a function or arrow function expression
        if (body is Expr.ArrowFunction arrow)
        {
            return EvaluateArrowFunction(arrow).ToObject() as SharpTSArrowFunction
                   ?? throw new InterpreterException("Failed to create getter function.");
        }
        throw new InterpreterException("Getter must be a function expression.");
    }

    /// <summary>
    /// Creates a setter function from the function expression and parameter.
    /// </summary>
    private SharpTSArrowFunction CreateSetterFunction(Expr body, Stmt.Parameter setterParam)
    {
        // The body should be a function expression
        if (body is Expr.ArrowFunction arrow)
        {
            return EvaluateArrowFunction(arrow).ToObject() as SharpTSArrowFunction
                   ?? throw new InterpreterException("Failed to create setter function.");
        }
        throw new InterpreterException("Setter must be a function expression.");
    }

    /// <summary>
    /// Applies a spread value's properties to the target fields dictionary.
    /// Shared between sync and async object evaluation paths.
    /// </summary>
    private static void ApplySpreadToFields(object? spreadValue, Dictionary<string, object?> stringFields)
    {
        if (spreadValue is SharpTSObject spreadObj)
        {
            foreach (var kv in spreadObj.Fields)
            {
                stringFields[kv.Key] = kv.Value;
            }
        }
        else if (spreadValue is SharpTSInstance inst)
        {
            foreach (var key in inst.GetFieldNames())
            {
                stringFields[key] = inst.GetRawField(key);
            }
        }
        // Plain Dictionary<string, object?> — used by runtime helpers like
        // Web Streams iterator results. Compiled mode already handles this in
        // MergeIntoObject; this branch keeps the interpreter at parity.
        else if (spreadValue is IDictionary<string, object?> dict)
        {
            foreach (var kv in dict)
            {
                stringFields[kv.Key] = kv.Value;
            }
        }
        else
        {
            throw new InterpreterException("Spread in object literal requires an object.");
        }
    }

    /// <summary>
    /// Core implementation for evaluating array literals, shared between sync and async paths.
    /// </summary>
    /// <param name="ctx">The evaluation context for evaluating elements.</param>
    /// <param name="array">The array literal expression AST node.</param>
    /// <returns>A ValueTask containing the evaluated array.</returns>
    private async ValueTask<object?> EvaluateArrayCore(IEvaluationContext ctx, Expr.ArrayLiteral array)
    {
        var evaluated = new List<(bool isSpread, object? value)>();
        for (int i = 0; i < array.Elements.Count; i++)
        {
            // Elided positions ([1, , 3]) become true holes, not undefined elements.
            if (array.IsHole(i))
            {
                evaluated.Add((false, ArrayHole.Instance));
                continue;
            }
            var e = array.Elements[i];
            var isSpread = e is Expr.Spread;
            var value = (await ctx.EvaluateExprAsync(isSpread ? ((Expr.Spread)e).Expression : e)).ToObject();
            evaluated.Add((isSpread, value));
        }
        return BuildArrayFromElements(evaluated);
    }

    /// <summary>
    /// Evaluates an array literal expression, creating a runtime array.
    /// </summary>
    /// <param name="array">The array literal expression AST node.</param>
    /// <returns>A <see cref="SharpTSArray"/> containing the evaluated elements.</returns>
    /// <remarks>
    /// Supports spread elements (<c>...arr</c>) which expand array contents inline.
    /// </remarks>
    /// <seealso href="https://www.typescriptlang.org/docs/handbook/2/everyday-types.html#arrays">TypeScript Arrays</seealso>
    private RuntimeValue EvaluateArray(Expr.ArrayLiteral array)
    {
        var evaluated = new List<(bool isSpread, object? value)>();
        for (int i = 0; i < array.Elements.Count; i++)
        {
            // Elided positions ([1, , 3]) become true holes, not undefined elements.
            if (array.IsHole(i))
            {
                evaluated.Add((false, ArrayHole.Instance));
                continue;
            }
            var e = array.Elements[i];
            var isSpread = e is Expr.Spread;
            var value = Evaluate(isSpread ? ((Expr.Spread)e).Expression : e);
            evaluated.Add((isSpread, value));
        }
        return RuntimeValue.FromObject(BuildArrayFromElements(evaluated));
    }

    /// <summary>
    /// Resolves an (object, index) pair to a typed IndexTarget for dispatch.
    /// </summary>
    /// <param name="obj">The object being indexed.</param>
    /// <param name="index">The index value.</param>
    /// <returns>An IndexTarget discriminated union representing the resolved target.</returns>
    private static IndexTarget ResolveIndexTarget(object? obj, object? index) => (obj, index) switch
    {
        (SharpTSArray array, double idx) => new IndexTarget.Array(array, (long)idx),
        (SharpTSTypedArray typedArray, double typedIdx) => new IndexTarget.TypedArray(typedArray, (int)typedIdx),
        (SharpTSBuffer buffer, double bufIdx) => new IndexTarget.Buffer(buffer, (int)bufIdx),
        (SharpTSEnum enumObj, double enumIdx) => new IndexTarget.EnumReverse(enumObj, enumIdx),
        (ConstEnumValues constEnum, _) => new IndexTarget.ConstEnumError(constEnum),
        // Symbol keys keep their identity through the symbol-dict path.
        (SharpTSObject symbolObj, SharpTSSymbol symbol) => new IndexTarget.ObjectSymbol(symbolObj, symbol),
        (SharpTSInstance symInstance, SharpTSSymbol symKey) => new IndexTarget.InstanceSymbol(symInstance, symKey),
        // Non-Symbol keys go through ECMA-262 §7.1.19 ToPropertyKey so `obj[-0]`
        // and `obj["0"]` resolve identically (and undefined/null/bool keys
        // stringify to "undefined"/"null"/"true"/"false" rather than landing in
        // the Unsupported bucket).
        (SharpTSObject sharpObj, _) => new IndexTarget.ObjectString(sharpObj, PropertyKeyConverter.ToPropertyKeyString(index)),
        (SharpTSInstance instance, _) => new IndexTarget.InstanceString(instance, PropertyKeyConverter.ToPropertyKeyString(index)),
        (SharpTSGlobalThis globalThis, string globalKey) => new IndexTarget.GlobalThis(globalThis, globalKey),
        (SharpTSHeaders headers, string headerKey) => new IndexTarget.HeadersString(headers, headerKey),
        // Class constructors accept expando statics (Node: `C["foo"] = 1`,
        // `C[Symbol.species] = P`). #262.
        (SharpTSClass cls, SharpTSSymbol clsSym) => new IndexTarget.ClassSymbol(cls, clsSym),
        (SharpTSClass cls, _) => new IndexTarget.ClassString(cls, PropertyKeyConverter.ToPropertyKeyString(index)),
        (string str, double strIdx) => new IndexTarget.StringChar(str, (int)strIdx),
        _ => new IndexTarget.Unsupported(obj, index)
    };

    /// <summary>
    /// Evaluates an index access expression (bracket notation).
    /// </summary>
    /// <param name="getIndex">The index access expression AST node.</param>
    /// <returns>The value at the specified index.</returns>
    /// <remarks>
    /// Supports array element access and enum reverse mapping (numeric value to name).
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/Property_accessors#bracket_notation">MDN Bracket Notation</seealso>
    private RuntimeValue EvaluateGetIndex(Expr.GetIndex getIndex)
    {
        object? obj = Evaluate(getIndex.Object);

        // Optional bracket access: return undefined if object is nullish
        if (getIndex.Optional && (obj == null || obj is Runtime.Types.SharpTSUndefined))
        {
            return RuntimeValue.Undefined;
        }

        object? index = Evaluate(getIndex.Index);
        return PerformIndexGet(getIndex.Object, obj, index);
    }

    /// <summary>
    /// Performs an index read on already-evaluated operands. Shared by the sync
    /// evaluator and the async evaluator (Interpreter.Async.cs) so both contexts
    /// dispatch identically. <paramref name="objectExpr"/> is the unevaluated
    /// receiver expression, needed only to synthesize a Get for the dot-notation
    /// fallback path.
    /// </summary>
    private RuntimeValue PerformIndexGet(Expr objectExpr, object? obj, object? index)
    {
        // Proxy: intercept index access via get trap
        if (obj is SharpTSProxy proxy)
        {
            string key = index?.ToString() ?? "";
            return proxy.TrapGetRV(key, this);
        }

        // JS functions are objects — bracket access reads user properties.
        if (obj is SharpTSFunction fn)
        {
            // Symbol-keyed bracket access (`fn[Symbol.species]`) routes
            // through the per-instance symbol map. Set via the matching
            // symbol-keyed branch in EvaluateSetIndex. Symbol-keyed
            // accessors (`Object.defineProperty(fn, sym, {get, set})`)
            // win over data values — test262's
            // species-ctor-species-get-err.js installs a throwing getter
            // here that SpeciesConstructor must propagate.
            if (index is SharpTSSymbol fnSym)
            {
                if (fn.TryGetSymbolAccessor(fnSym, out var symGetter, out _) && symGetter != null)
                    return RuntimeValue.FromBoxed(symGetter.Call(this, []));
                if (fn.TryGetSymbolProperty(fnSym, out var symVal))
                    return RuntimeValue.FromBoxed(symVal ?? SharpTSUndefined.Instance);
            }
            string fnKey = index?.ToString() ?? "";
            if (fn.TryGetProperty(fnKey, out var propVal))
                return RuntimeValue.FromBoxed(propVal);
            return RuntimeValue.Undefined;
        }
        // Same for arrow functions / function expressions.
        if (obj is SharpTSArrowFunction afn)
        {
            if (index is SharpTSSymbol arrSym)
            {
                if (afn.TryGetSymbolAccessor(arrSym, out var arrGetter, out _) && arrGetter != null)
                    return RuntimeValue.FromBoxed(arrGetter.Call(this, []));
                if (afn.TryGetSymbolProperty(arrSym, out var arrSymVal))
                    return RuntimeValue.FromBoxed(arrSymVal ?? SharpTSUndefined.Instance);
            }
            string arrKey = index?.ToString() ?? "";
            if (afn.TryGetProperty(arrKey, out var arrPropVal))
                return RuntimeValue.FromBoxed(arrPropVal);
            return RuntimeValue.Undefined;
        }

        // Built-in namespace singletons and prototype objects resolve dot-notation access via
        // BuiltInRegistry.GetInstanceMember or hand-written fallbacks in EvaluateGetOnFallback
        // (SharpTSArrayGlobal, SharpTSArrayPrototype, SharpTSBuiltInConstructor, etc.).
        // ResolveIndexTarget has no switch cases for them, so bracket access
        // (`Object['create']`, `Array.prototype['pop']`) would otherwise throw
        // "Index access not supported". Lodash relies on these idioms when walking
        // constructors through a lookup table. Mirror the dot-notation path by
        // synthesizing a Get expression and reusing EvaluateGetOnObject.
        if (obj != null && index is string strIndexKey)
        {
            var syntheticName = new Token(TokenType.IDENTIFIER, strIndexKey, null, 0);
            var syntheticGet = new Expr.Get(objectExpr, syntheticName, Optional: false);
            return EvaluateGetOnObject(syntheticGet, obj);
        }

        // Math[n] (numeric index) should behave the same as Math.n — strings
        // and doubles are both legal keys on an extensible object.
        if (obj is SharpTSMath math)
        {
            var key = Stringify(index);
            if (math.HasExtra(key)) return RuntimeValue.FromBoxed(math.TryGetExtra(key));
            return RuntimeValue.FromBoxed(Runtime.BuiltIns.MathBuiltIns.GetMember(key) ?? SharpTSUndefined.Instance);
        }

        // ECMA-262 §22.2.5: RegExp.prototype has well-known-symbol-keyed methods
        // (@@match, @@matchAll, @@replace, @@search, @@split). These are bracket-only
        // and don't appear in ResolveIndexTarget — dispatch them here.
        if (obj is SharpTSRegExp regexObj && index is SharpTSSymbol regexSym)
        {
            // A user-set own symbol property (`re[Symbol.match] = false`) shadows
            // the inherited RegExp.prototype well-known-symbol method — IsRegExp
            // depends on this override winning.
            if (regexObj.TryGetSymbolProperty(regexSym, out var ownSym))
                return RuntimeValue.FromBoxed(ownSym ?? SharpTSUndefined.Instance);
            var member = Runtime.BuiltIns.RegExpBuiltIns.GetSymbolMember(regexObj, regexSym);
            if (member is BuiltInMethod bim) return RuntimeValue.FromBoxed(bim.Bind(regexObj));
            return RuntimeValue.FromBoxed(member ?? SharpTSUndefined.Instance);
        }

        return RuntimeValue.FromBoxed(ResolveIndexTarget(obj, index) switch
        {
            IndexTarget.Array t => t.Target.Get(t.Index),
            IndexTarget.TypedArray t => t.Target[t.Index],
            IndexTarget.Buffer t => t.Target[t.Index],
            IndexTarget.EnumReverse t => t.Target.GetReverse(t.Index),
            IndexTarget.ConstEnumError t => throw new InterpreterException(
                $"Runtime Error: Cannot use index access on const enum '{t.Target.Name}'. Const enum members can only be accessed by name."),
            IndexTarget.ObjectString t => t.Target.GetProperty(t.Key),
            IndexTarget.ObjectSymbol t => t.Target.GetBySymbol(t.Key) ?? SharpTSUndefined.Instance,
            IndexTarget.InstanceString t => t.Target.Get(new Token(TokenType.IDENTIFIER, t.Key, null, 0)),
            IndexTarget.InstanceSymbol t => GetInstanceSymbolValue(t.Target, t.Key),
            // String keys on classes are normally intercepted by the synthetic-Get
            // path above; this arm catches non-string keys (numbers, booleans)
            // after ToPropertyKey normalization.
            IndexTarget.ClassString t => EvaluateGetOnClass(t.Target, t.Key),
            IndexTarget.ClassSymbol t => GetClassSymbolValue(t.Target, t.Key),
            IndexTarget.GlobalThis t => t.Target.GetProperty(t.Key),
            IndexTarget.HeadersString t => (object?)t.Target.Get(t.Key) ?? SharpTSUndefined.Instance,
            IndexTarget.StringChar t => (t.Index >= 0 && t.Index < t.Target.Length)
                ? (object)t.Target[t.Index].ToString()
                : SharpTSUndefined.Instance,
            IndexTarget.Unsupported => throw new InterpreterException("Index access not supported on this type."),
            _ => throw new InterpreterException("Index access not supported on this type.")
        });
    }

    /// <summary>
    /// Reads a symbol-keyed member from an instance: own symbol data property
    /// first, then a declared symbol-keyed getter (`get [Symbol.x]()`) from the
    /// class chain.
    /// </summary>
    private object? GetInstanceSymbolValue(SharpTSInstance instance, SharpTSSymbol symbol)
    {
        object? own = instance.GetBySymbol(symbol);
        if (own != null) return own;
        if (instance.GetClass().FindSymbolGetter(symbol) is { } getter)
        {
            return getter.Bind(instance).Call(this, []);
        }
        // Symbol-keyed method (`[Symbol.iterator]() {...}`): return the method bound to the
        // instance (not invoked), exactly like `instance.namedMethod` returns a bound method.
        if (instance.GetClass().FindSymbolMethod(symbol) is { } method)
        {
            return SharpTSClass.BindMethod(method, instance);
        }
        return SharpTSUndefined.Instance;
    }

    /// <summary>
    /// Reads a symbol-keyed static from a class: a declared static symbol-keyed
    /// getter (`static get [Symbol.species]()`) wins over expando data statics.
    /// </summary>
    private object? GetClassSymbolValue(SharpTSClass klass, SharpTSSymbol symbol)
    {
        if (klass.FindStaticSymbolGetter(symbol) is { } getter)
        {
            return getter.BindStatic(klass).Call(this, []);
        }
        // Static symbol-keyed method (`static [Symbol.hasInstance]() {...}`): return it bound
        // to the class receiver where possible (SharpTSFunction); other callable kinds are
        // returned as-is.
        if (klass.FindStaticSymbolMethod(symbol) is { } method)
        {
            return method is SharpTSFunction f ? f.BindStatic(klass) : method;
        }
        return klass.TryGetStaticBySymbol(symbol, out var value)
            ? value ?? SharpTSUndefined.Instance
            : SharpTSUndefined.Instance;
    }

    /// <summary>
    /// Evaluates an index assignment expression (bracket notation with assignment).
    /// </summary>
    /// <param name="setIndex">The index assignment expression AST node.</param>
    /// <returns>The assigned value.</returns>
    /// <remarks>
    /// Currently only supports array element assignment.
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/Property_accessors#bracket_notation">MDN Bracket Notation</seealso>
    private RuntimeValue EvaluateSetIndex(Expr.SetIndex setIndex)
    {
        object? obj = Evaluate(setIndex.Object);
        object? index = Evaluate(setIndex.Index);
        object? value = Evaluate(setIndex.Value);
        return PerformIndexSet(obj, index, value);
    }

    /// <summary>
    /// Performs an index assignment on already-evaluated operands. Shared by the
    /// sync evaluator and the async evaluator (Interpreter.Async.cs) so both
    /// contexts dispatch identically.
    /// </summary>
    private RuntimeValue PerformIndexSet(object? obj, object? index, object? value)
    {
        bool strictMode = _environment.IsStrictMode;

        // Proxy: intercept index assignment via set trap
        if (obj is SharpTSProxy proxy)
        {
            string key = index?.ToString() ?? "";
            return proxy.TrapSetRV(key, value, this);
        }

        // JS functions are objects — support bracket property assignment.
        if (obj is SharpTSFunction fn)
        {
            // Symbol-keyed assignment (`fn[Symbol.species] = ...`) routes to
            // the per-instance symbol map so SpeciesConstructor lookups
            // round-trip. Without this branch the key gets stringified to
            // "Symbol(...)" via ToString and is invisible to symbol-keyed
            // reads.
            if (index is SharpTSSymbol fnSym)
            {
                fn.SetBySymbol(fnSym, value);
                return RuntimeValue.FromBoxed(value);
            }
            fn.SetProperty(index?.ToString() ?? "", value);
            return RuntimeValue.FromBoxed(value);
        }
        if (obj is SharpTSArrowFunction afn)
        {
            if (index is SharpTSSymbol arrSym)
            {
                afn.SetBySymbol(arrSym, value);
                return RuntimeValue.FromBoxed(value);
            }
            afn.SetProperty(index?.ToString() ?? "", value);
            return RuntimeValue.FromBoxed(value);
        }

        // Math is an extensible object — allow Math[n] = v alongside Math.foo = v.
        if (obj is SharpTSMath math)
        {
            math.SetExtra(index?.ToString() ?? "", value);
            return RuntimeValue.FromBoxed(value);
        }

        // RegExp symbol-keyed assignment (`re[Symbol.match] = false`) stores an
        // own symbol property that shadows the inherited prototype method —
        // IsRegExp (§22.2.7.2) reads it via Get(re, @@match). Without this branch
        // the symbol key falls through to the Unsupported bucket and throws.
        if (obj is SharpTSRegExp regexSet && index is SharpTSSymbol regexSetSym)
        {
            regexSet.SetBySymbol(regexSetSym, value);
            return RuntimeValue.FromBoxed(value);
        }

        // Built-in callables expose `name` / `length` as non-writable own
        // properties (ECMA-262 §17). Index assignment to those should silently
        // no-op in sloppy mode and throw TypeError in strict mode — test262's
        // verifyProperty / isWritable rely on the TypeError shape to classify
        // writability. Other keys on a callable (user-stored fn[k] = v) aren't
        // supported here either; let them fall through to the spec-conformant
        // error.
        if (obj is ISharpTSCallable && obj is not SharpTSFunction
            && index?.ToString() is "name" or "length")
        {
            if (strictMode)
                throw new ThrowException(new Runtime.Types.SharpTSTypeError(
                    $"Cannot assign to read only property '{index}' of function"));
            return RuntimeValue.FromBoxed(value);
        }

        var target = ResolveIndexTarget(obj, index);

        if (target is IndexTarget.EnumReverse or IndexTarget.ConstEnumError)
            throw new InterpreterException("Index assignment not supported on enum types.");

        switch (target)
        {
            case IndexTarget.Array t:
                if (strictMode) t.Target.SetStrict(t.Index, value, strictMode);
                else t.Target.Set(t.Index, value);
                break;

            case IndexTarget.TypedArray t:
                t.Target[t.Index] = value;
                break;

            case IndexTarget.Buffer t:
                t.Target[t.Index] = value is double d ? d : Convert.ToDouble(value);
                break;

            case IndexTarget.ObjectString t:
                if (strictMode) t.Target.SetPropertyStrict(t.Key, value, strictMode);
                else t.Target.SetProperty(t.Key, value);
                break;

            case IndexTarget.ObjectSymbol t:
                if (strictMode) t.Target.SetBySymbolStrict(t.Key, value, strictMode);
                else t.Target.SetBySymbol(t.Key, value);
                break;

            case IndexTarget.InstanceString t:
                // Bracket assignment must invoke a declared setter / auto-accessor, mirroring
                // dot assignment (SharpTSInstance.Set) and JS [[Set]] semantics. Without this the
                // write lands in _fields while reads keep hitting the getter, silently
                // desynchronizing the two (#290). Plain data writes stay on SetRawField so the
                // defineProperty `writable:false` flag continues to be honored.
                if (t.Target.GetClass().FindSetter(t.Key) != null
                    || t.Target.GetClass().HasInstanceAutoAccessor(t.Key))
                {
                    // Set/SetStrict invoke the setter only when the instance has an interpreter
                    // reference; the dot-assignment path primes it the same way (cold instances
                    // created by `new` don't carry one until first member access).
                    t.Target.SetInterpreter(this);
                    var setToken = new Token(TokenType.IDENTIFIER, t.Key, null, 0);
                    if (strictMode) t.Target.SetStrict(setToken, value, strictMode);
                    else t.Target.Set(setToken, value);
                }
                else if (strictMode) t.Target.SetRawFieldStrict(t.Key, value, strictMode);
                else t.Target.SetRawField(t.Key, value);
                break;

            case IndexTarget.InstanceSymbol t:
                // A declared symbol-keyed setter (`set [Symbol.x](v)`) intercepts
                // assignment unless shadowed by an own symbol data property.
                if (t.Target.GetBySymbol(t.Key) == null
                    && t.Target.GetClass().FindSymbolSetter(t.Key) is { } symSetter)
                {
                    symSetter.Bind(t.Target).Call(this, [value]);
                    break;
                }
                if (strictMode) t.Target.SetBySymbolStrict(t.Key, value, strictMode);
                else t.Target.SetBySymbol(t.Key, value);
                break;

            case IndexTarget.ClassString t:
                t.Target.SetStaticProperty(t.Key, value);
                break;

            case IndexTarget.ClassSymbol t:
                if (t.Target.FindStaticSymbolSetter(t.Key) is { } staticSymSetter)
                {
                    staticSymSetter.BindStatic(t.Target).Call(this, [value]);
                    break;
                }
                t.Target.SetStaticBySymbol(t.Key, value);
                break;

            case IndexTarget.GlobalThis t:
                t.Target.SetProperty(t.Key, value);
                break;

            default:
                throw new InterpreterException("Index assignment not supported on this type.");
        }
        return RuntimeValue.FromBoxed(value);
    }

    /// <summary>
    /// Evaluates a dynamic import expression, returning a Promise of the module namespace.
    /// </summary>
    /// <param name="di">The dynamic import expression AST node.</param>
    /// <returns>A <see cref="SharpTSPromise"/> that resolves to the module namespace object.</returns>
    /// <remarks>
    /// Dynamic imports allow runtime module loading with expression paths.
    /// The returned Promise resolves to an object containing all module exports.
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/import">MDN Dynamic Import</seealso>
    private RuntimeValue EvaluateDynamicImport(Expr.DynamicImport di)
    {
        return RuntimeValue.FromObject(new SharpTSPromise(DynamicImportAsync(di)));
    }

    /// <summary>
    /// Evaluates an import.meta expression, returning an object with module metadata.
    /// </summary>
    private RuntimeValue EvaluateImportMeta(Expr.ImportMeta im)
    {
        // Get current module path
        string path = _currentModule?.Path ?? "";
        string url = path;

        // Convert to file:// URL format if it's a file path
        if (!string.IsNullOrEmpty(url) && !url.StartsWith("file://"))
        {
            url = "file:///" + url.Replace("\\", "/");
        }

        return RuntimeValue.FromObject(new SharpTSObject(new Dictionary<string, object?>
        {
            ["url"] = url,
            ["filename"] = path,
            ["dirname"] = Path.GetDirectoryName(path) ?? ""
        }));
    }

    /// <summary>
    /// Asynchronously loads a module dynamically.
    /// </summary>
    private async Task<object?> DynamicImportAsync(Expr.DynamicImport di)
    {
        // Evaluate the path expression
        object? pathValue = Evaluate(di.PathExpression);
        string specifier = pathValue?.ToString()
            ?? throw new InterpreterException("Dynamic import path cannot be null.");

        // Create resolver if needed (single-file mode without module context)
        _moduleResolver ??= new ModuleResolver(
            _currentModule?.Path ?? Directory.GetCurrentDirectory());

        string currentPath = _currentModule?.Path ?? Directory.GetCurrentDirectory();
        string absolutePath = _moduleResolver.ResolveModulePath(specifier, currentPath);

        // Check if module is already loaded
        if (_loadedModules.TryGetValue(absolutePath, out var cached))
        {
            return cached.ExportsAsObject();
        }

        // Load and execute the module
        ParsedModule module = _moduleResolver.LoadModule(absolutePath);

        // Type check the new module (optional - errors become Promise rejections)
        // Note: Skipping type checking for dynamic imports for flexibility

        // Execute the module
        ExecuteModule(module);

        // Return the module namespace
        return _loadedModules[absolutePath].ExportsAsObject();
    }

    // Counter for generating unique anonymous class expression names
    private int _classExprCounter = 0;

    /// <summary>
    /// Evaluates a class expression and returns the SharpTSClass object.
    /// Unlike class declarations, the class is not added to the environment.
    /// </summary>
    private RuntimeValue EvaluateClassExpression(Expr.ClassExpr classExpr)
    {
        // Generate name for anonymous classes
        string className = classExpr.Name?.Lexeme ?? $"$ClassExpr_{++_classExprCounter}";

        // Resolve superclass if present
        object? superclass = null;
        if (classExpr.SuperclassExpr != null)
        {
            superclass = Evaluate(classExpr.SuperclassExpr);
            // `extends Array` (#233): substitute the SharpTSArrayClass bridge,
            // mirroring VisitClass.
            if (superclass is SharpTSArrayGlobal)
            {
                superclass = SharpTSArrayClass.ArrayBase;
            }
            // `extends Promise` (#242): same substitution, mirroring VisitClass.
            if (superclass is SharpTSBuiltInConstructor { Name: BuiltInNames.Promise })
            {
                superclass = SharpTSPromiseClass.PromiseBase;
            }
            if (superclass is not SharpTSClass)
            {
                if (superclass is SharpTSBuiltInConstructor builtInCtor)
                {
                    throw new InterpreterException(
                        $"Class expression cannot extend built-in '{builtInCtor.Name}': subclassing this built-in is not supported yet.");
                }
                throw new InterpreterException("Superclass must be a class.");
            }
        }

        // Create environment for class body
        RuntimeEnvironment classEnv = _environment;

        // If named, define the name in class body scope for self-reference
        if (classExpr.Name != null)
        {
            classEnv = new RuntimeEnvironment(_environment);
            classEnv.Define(classExpr.Name.Lexeme, null); // Placeholder for self-reference
        }

        if (classExpr.SuperclassExpr != null)
        {
            classEnv = new RuntimeEnvironment(classEnv);
            classEnv.Define("super", superclass);
        }

        using (PushScope(classEnv))
        {
            Dictionary<string, ISharpTSCallable> methods = [];
            Dictionary<string, ISharpTSCallable> staticMethods = [];
            Dictionary<string, object?> staticProperties = [];
            List<Stmt.Field> instanceFields = [];

            // Check if we have static initializers for proper ordering
            bool hasStaticInitializers = classExpr.StaticInitializers != null && classExpr.StaticInitializers.Count > 0;

            // Process fields
            foreach (Stmt.Field field in classExpr.Fields)
            {
                if (field.IsStatic)
                {
                    if (!hasStaticInitializers)
                    {
                        // Old behavior: evaluate immediately
                        object? fieldValue = field.Initializer != null
                            ? Evaluate(field.Initializer)
                            : null;
                        staticProperties[field.Name.Lexeme] = fieldValue;
                    }
                    // else: will be evaluated via StaticInitializers with proper 'this' binding
                }
                else
                {
                    instanceFields.Add(field);
                }
            }

            // Process methods (skip overload signatures with no body)
            foreach (Stmt.Function method in classExpr.Methods.Where(m => m.Body != null))
            {
                // Create the appropriate function type based on async/generator flags
                ISharpTSCallable func;
                if (method.IsAsync)
                    func = new SharpTSAsyncFunction(method, _environment);
                else if (method.IsGenerator)
                    func = new SharpTSGeneratorFunction(method, _environment);
                else
                    func = new SharpTSFunction(method, _environment);

                if (method.IsStatic)
                {
                    staticMethods[method.Name.Lexeme] = func;
                }
                else
                {
                    methods[method.Name.Lexeme] = func;
                }
            }

            // Create accessor functions
            Dictionary<string, SharpTSFunction> getters = [];
            Dictionary<string, SharpTSFunction> setters = [];
            List<(SharpTSSymbol Symbol, SharpTSFunction Func, bool IsStatic, bool IsGetter)>? symbolAccessors = null;

            if (classExpr.Accessors != null)
            {
                foreach (var accessor in classExpr.Accessors)
                {
                    var funcStmt = new Stmt.Function(
                        accessor.Name,
                        null,
                        null,
                        accessor.SetterParam != null ? [accessor.SetterParam] : [],
                        accessor.Body,
                        accessor.ReturnType);

                    SharpTSFunction func = new(funcStmt, _environment);
                    bool isGetter = accessor.Kind.Type == TokenType.GET;
                    string nameKey = accessor.Name.Lexeme;

                    // Computed accessor names, evaluated at class-definition time
                    // (mirrors VisitClass).
                    if (accessor.ComputedKey != null)
                    {
                        object? key = Evaluate(accessor.ComputedKey);
                        if (key is SharpTSSymbol symbolKey)
                        {
                            (symbolAccessors ??= []).Add((symbolKey, func, accessor.IsStatic, isGetter));
                            continue;
                        }
                        nameKey = PropertyKeyConverter.ToPropertyKeyString(key);
                    }

                    if (isGetter)
                    {
                        getters[nameKey] = func;
                    }
                    else
                    {
                        setters[nameKey] = func;
                    }
                }
            }

            // Mirror VisitClass: Error/Array/Promise superclasses need their
            // dedicated SharpTSClass subtypes so instances get the right backing.
            SharpTSClass klass = superclass is SharpTSErrorClass errorSuper
                ? new SharpTSErrorClass(
                    className,
                    errorSuper,
                    methods,
                    staticMethods,
                    staticProperties,
                    getters,
                    setters,
                    classExpr.IsAbstract,
                    instanceFields)
                : superclass is SharpTSArrayClass arraySuper
                ? new SharpTSArrayClass(
                    className,
                    arraySuper,
                    methods,
                    staticMethods,
                    staticProperties,
                    getters,
                    setters,
                    classExpr.IsAbstract,
                    instanceFields)
                : superclass is SharpTSPromiseClass promiseSuper
                ? new SharpTSPromiseClass(
                    className,
                    promiseSuper,
                    methods,
                    staticMethods,
                    staticProperties,
                    getters,
                    setters,
                    classExpr.IsAbstract,
                    instanceFields)
                : new SharpTSClass(
                    className,
                    (SharpTSClass?)superclass,
                    methods,
                    staticMethods,
                    staticProperties,
                    getters,
                    setters,
                    classExpr.IsAbstract,
                    instanceFields);

            if (symbolAccessors != null)
            {
                foreach (var (symbol, func, isStatic, isGetter) in symbolAccessors)
                {
                    klass.AddSymbolAccessor(symbol, func, isStatic, isGetter);
                }
            }

            // Execute static initializers in declaration order (if present)
            if (hasStaticInitializers)
            {
                // Create temporary environment with 'this' bound to the class
                // Also make the class name available so code like Foo.x works
                var staticEnv = new RuntimeEnvironment(_environment);
                staticEnv.Define("this", klass);
                if (classExpr.Name != null)
                {
                    staticEnv.Define(classExpr.Name.Lexeme, klass);
                }

                using (PushScope(staticEnv))
                {
                    foreach (var initializer in classExpr.StaticInitializers!)
                    {
                        switch (initializer)
                        {
                            case Stmt.Field field when field.IsStatic:
                                object? fieldValue = field.Initializer != null
                                    ? Evaluate(field.Initializer)
                                    : null;
                                klass.SetStaticProperty(field.Name.Lexeme, fieldValue);
                                break;

                            case Stmt.StaticBlock block:
                                foreach (var blockStmt in block.Body)
                                {
                                    var result = Execute(blockStmt);
                                    if (result.IsAbrupt)
                                    {
                                        // Handle throw from static block
                                        if (result.Type == ExecutionResult.ResultType.Throw)
                                        {
                                            throw new InterpreterException($"Error in static block: {Stringify(result.Value.ToObject())}");
                                        }
                                        // Return, break, continue are not allowed (validated by type checker)
                                    }
                                }
                                break;
                        }
                    }
                }
            }

            // Update self-reference if named
            if (classExpr.Name != null)
            {
                classEnv.Assign(classExpr.Name, klass);
            }

            return RuntimeValue.FromObject(klass);
        }
    }
}
