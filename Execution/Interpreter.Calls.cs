using SharpTS.Compilation;
using SharpTS.Parsing;
using SharpTS.Runtime;
using SharpTS.Runtime.BuiltIns;
using SharpTS.Runtime.Exceptions;
using SharpTS.Runtime.Types;
using SharpTS.TypeSystem;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpTS.Execution;

// Note: This file uses InterpreterException for runtime errors

public partial class Interpreter
{
    /// <summary>
    /// Calls a built-in method with pooled argument list to reduce allocations.
    /// Uses V2 (RuntimeValue) fast path when the method supports it, avoiding
    /// the legacy wrapper overhead.
    /// </summary>
    private async ValueTask<object?> CallBuiltInWithPooledArgs(
        IEvaluationContext ctx,
        ISharpTSCallable method,
        IReadOnlyList<Expr> arguments)
    {
        // V2 fast path: use RuntimeValue span instead of List<object?>
        if (method is ISharpTSCallableV2 v2 && method is BuiltInMethod bm && bm.HasV2Implementation)
        {
            var argCount = arguments.Count;
            var rented = ArrayPool<RuntimeValue>.Shared.Rent(Math.Max(argCount, 1));
            try
            {
                for (int i = 0; i < argCount; i++)
                {
                    rented[i] = await ctx.EvaluateExprAsync(arguments[i]);
                }
                return v2.CallV2(this, rented.AsSpan(0, argCount)).ToObject();
            }
            finally
            {
                ArrayPool<RuntimeValue>.Shared.Return(rented);
            }
        }

        // Legacy path
        var pooledList = ArgumentListPool.Rent();
        try
        {
            foreach (var arg in arguments)
            {
                pooledList.Add((await ctx.EvaluateExprAsync(arg)).ToObject());
            }
            return method.Call(this, pooledList);
        }
        finally
        {
            ArgumentListPool.Return(pooledList);
        }
    }

    /// <summary>
    /// Core implementation for evaluating function/method calls, shared between sync and async paths.
    /// Handles all special cases: console.log, built-in methods, Symbol, BigInt, Date, Error, timers, etc.
    /// </summary>
    /// <param name="ctx">The evaluation context for evaluating arguments.</param>
    /// <param name="call">The call expression AST node.</param>
    /// <returns>A ValueTask containing the return value of the called function.</returns>
    private async ValueTask<object?> EvaluateCallCore(IEvaluationContext ctx, Expr.Call call)
    {
        // Handle console.* methods
        if (call.Callee is Expr.Variable v && v.Name.Lexeme.StartsWith(BuiltInNames.ConsolePrefix, StringComparison.Ordinal))
        {
            var methodName = v.Name.Lexeme[(BuiltInNames.Console.Length + 1)..];
            var method = BuiltInRegistry.Instance.GetStaticMethod(BuiltInNames.Console, methodName);
            if (method != null)
            {
                return await CallBuiltInWithPooledArgs(ctx, method, call.Arguments);
            }
        }

        // Handle globalThis.console.* calls
        if (call.Callee is Expr.Get chainedGet &&
            chainedGet.Object is Expr.Get innerGet &&
            innerGet.Object is Expr.Variable globalThisVar &&
            globalThisVar.Name.Lexeme == BuiltInNames.GlobalThis &&
            innerGet.Name.Lexeme == BuiltInNames.Console)
        {
            var method = BuiltInRegistry.Instance.GetStaticMethod(BuiltInNames.Console, chainedGet.Name.Lexeme);
            if (method != null)
            {
                return await CallBuiltInWithPooledArgs(ctx, method, call.Arguments);
            }
        }

        // Handle globalThis.<namespace>.<method>() calls (e.g., globalThis.Math.floor())
        if (call.Callee is Expr.Get gtChainedGet &&
            gtChainedGet.Object is Expr.Get gtInnerGet &&
            gtInnerGet.Object is Expr.Variable gtVar &&
            gtVar.Name.Lexeme == BuiltInNames.GlobalThis)
        {
            var method = BuiltInRegistry.Instance.GetStaticMethod(gtInnerGet.Name.Lexeme, gtChainedGet.Name.Lexeme);
            if (method != null)
            {
                return await CallBuiltInWithPooledArgs(ctx, method, call.Arguments);
            }
        }

        // Handle built-in static methods: Object.keys(), Array.isArray(), JSON.parse(), etc.
        if (call.Callee is Expr.Get get &&
            get.Object is Expr.Variable nsVar)
        {
            var method = BuiltInRegistry.Instance.GetStaticMethod(nsVar.Name.Lexeme, get.Name.Lexeme);
            if (method != null)
            {
                return await CallBuiltInWithPooledArgs(ctx, method, call.Arguments);
            }
        }

        // Handle global functions via registry (Symbol, BigInt, parseInt, setTimeout, Error types, etc.)
        // Skip if the variable is shadowed by a module import (e.g., stdlib 'timers' / 'timers/promises'
        // re-exports setTimeout as a SharpTSFunction, which implements ISharpTSCallable).
        if (call.Callee is Expr.Variable globalVar)
        {
            var gvName = globalVar.Name.Lexeme;
            bool isShadowed = _environment.TryGet(gvName, out var shadowVal) &&
                shadowVal.ToObject() is ISharpTSCallable or ISharpTSAsyncCallable or Runtime.BuiltIns.BuiltInMethod;
            if (!isShadowed)
            {
                if (GlobalFunctionRegistry.Instance.TryGetHandlerV2(gvName, out var handlerV2))
                {
                    Func<Expr, ValueTask<RuntimeValue>> evalWrapper = expr => ctx.EvaluateExprAsync(expr);
                    return await handlerV2!(evalWrapper, call.Arguments, this);
                }
                if (GlobalFunctionRegistry.Instance.TryGetHandler(gvName, out var handler))
                {
                    Func<Expr, ValueTask<object?>> evalWrapperLegacy = async expr => (await ctx.EvaluateExprAsync(expr)).ToObject();
                    return RuntimeValue.FromBoxed(await handler!(evalWrapperLegacy, call.Arguments, this));
                }
            }
        }

        object? callee = (await ctx.EvaluateExprAsync(call.Callee)).ToObject();

        // Optional call: return undefined if callee is nullish.
        // Per ECMA-262 §13.3.9, the short-circuit extends to the entire chain.
        if ((call.Optional || HasOptionalInChain(call.Callee))
            && (callee == null || callee is Runtime.Types.SharpTSUndefined))
        {
            return SharpTSUndefined.Instance;
        }

        var argumentsList = ArgumentListPool.Rent();
        try
        {
            foreach (Expr argument in call.Arguments)
            {
                if (argument is Expr.Spread spread)
                {
                    object? spreadValue = (await ctx.EvaluateExprAsync(spread.Expression)).ToObject();
                    // Use GetIterableElements to support custom iterables with Symbol.iterator
                    argumentsList.AddRange(GetIterableElements(spreadValue));
                }
                else
                {
                    argumentsList.Add((await ctx.EvaluateExprAsync(argument)).ToObject());
                }
            }

            if (callee is not ISharpTSCallable function)
            {
                if ((call.Optional || HasOptionalInChain(call.Callee))
                    && (callee == null || callee is Runtime.Types.SharpTSUndefined))
                {
                    return Runtime.Types.SharpTSUndefined.Instance;
                }
                throw new InterpreterException($"Can only call functions and classes. (got {callee?.GetType().FullName ?? "null"})");
            }

            // Per ECMA-262 §10.2.1: missing arguments become undefined; do not
            // reject calls for user-defined functions. Built-in methods enforce
            // their own min-arity in BuiltInMethod.Call.
            return function.Call(this, argumentsList);
        }
        finally
        {
            ArgumentListPool.Return(argumentsList);
        }
    }

    /// <summary>
    /// Walks the expression to determine whether any <c>?.</c> in its postfix
    /// chain could have short-circuited. Per ECMA-262 §13.3.9 OptionalExpression,
    /// the entire chain short-circuits if any optional step returns null/undefined.
    /// </summary>
    private static bool HasOptionalInChain(Expr expr)
    {
        while (true)
        {
            switch (expr)
            {
                case Expr.Get g:
                    if (g.Optional) return true;
                    expr = g.Object;
                    continue;
                case Expr.GetIndex gi:
                    if (gi.Optional) return true;
                    expr = gi.Object;
                    continue;
                case Expr.Call c:
                    if (c.Optional) return true;
                    expr = c.Callee;
                    continue;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Calls a built-in method with sync evaluation.
    /// Uses V2 (RuntimeValue) fast path when the method supports it.
    /// </summary>
    private RuntimeValue CallBuiltInSync(ISharpTSCallable method, IReadOnlyList<Expr> arguments)
    {
        // V2 fast path — no boxing at all
        if (method is ISharpTSCallableV2 v2 && method is BuiltInMethod bm && bm.HasV2Implementation)
        {
            var argCount = arguments.Count;
            var rented = ArrayPool<RuntimeValue>.Shared.Rent(Math.Max(argCount, 1));
            try
            {
                for (int i = 0; i < argCount; i++)
                {
                    rented[i] = EvaluateRV(arguments[i]);
                }
                return v2.CallV2(this, rented.AsSpan(0, argCount));
            }
            finally
            {
                ArrayPool<RuntimeValue>.Shared.Return(rented);
            }
        }

        // Legacy path
        var pooledList = ArgumentListPool.Rent();
        try
        {
            foreach (var arg in arguments)
            {
                pooledList.Add(Evaluate(arg));
            }
            return RuntimeValue.FromBoxed(method.Call(this, pooledList));
        }
        finally
        {
            ArgumentListPool.Return(pooledList);
        }
    }

    /// <summary>
    /// Evaluates a function or method call expression.
    /// </summary>
    /// <param name="call">The call expression AST node.</param>
    /// <returns>The return value of the called function.</returns>
    /// <remarks>
    /// Handles special cases for <c>console.log</c>, <c>Object.*</c> static methods,
    /// and the internal <c>__objectRest</c> helper. Supports spread arguments.
    /// Validates arity before invoking the callable.
    /// </remarks>
    /// <seealso href="https://www.typescriptlang.org/docs/handbook/2/functions.html">TypeScript Functions</seealso>
    private RuntimeValue EvaluateCall(Expr.Call call)
    {
        // Handle console.* methods
        if (call.Callee is Expr.Variable v && v.Name.Lexeme.StartsWith(BuiltInNames.ConsolePrefix, StringComparison.Ordinal))
        {
            var methodName = v.Name.Lexeme[(BuiltInNames.Console.Length + 1)..];
            var method = BuiltInRegistry.Instance.GetStaticMethod(BuiltInNames.Console, methodName);
            if (method != null)
            {
                return CallBuiltInSync(method, call.Arguments);
            }
        }

        // Handle globalThis.console.* calls
        if (call.Callee is Expr.Get chainedGet &&
            chainedGet.Object is Expr.Get innerGet &&
            innerGet.Object is Expr.Variable globalThisVar &&
            globalThisVar.Name.Lexeme == BuiltInNames.GlobalThis &&
            innerGet.Name.Lexeme == BuiltInNames.Console)
        {
            var method = BuiltInRegistry.Instance.GetStaticMethod(BuiltInNames.Console, chainedGet.Name.Lexeme);
            if (method != null)
            {
                return CallBuiltInSync(method, call.Arguments);
            }
        }

        // Handle globalThis.<namespace>.<method>() calls (e.g., globalThis.Math.floor())
        if (call.Callee is Expr.Get gtChainedGet &&
            gtChainedGet.Object is Expr.Get gtInnerGet &&
            gtInnerGet.Object is Expr.Variable gtVar &&
            gtVar.Name.Lexeme == BuiltInNames.GlobalThis)
        {
            var method = BuiltInRegistry.Instance.GetStaticMethod(gtInnerGet.Name.Lexeme, gtChainedGet.Name.Lexeme);
            if (method != null)
            {
                return CallBuiltInSync(method, call.Arguments);
            }
        }

        // Handle built-in static methods: Object.keys(), Array.isArray(), JSON.parse(), etc.
        if (call.Callee is Expr.Get get &&
            get.Object is Expr.Variable nsVar)
        {
            var method = BuiltInRegistry.Instance.GetStaticMethod(nsVar.Name.Lexeme, get.Name.Lexeme);
            if (method != null)
            {
                return CallBuiltInSync(method, call.Arguments);
            }
        }

        // Handle global functions via registry (Symbol, BigInt, parseInt, setTimeout, Error types, etc.)
        // Skip if the variable is shadowed by a module import (e.g., stdlib 'timers' / 'timers/promises'
        // re-exports setTimeout as a SharpTSFunction, which implements ISharpTSCallable).
        if (call.Callee is Expr.Variable globalVar)
        {
            var gvName = globalVar.Name.Lexeme;
            bool isShadowed = _environment.TryGet(gvName, out var shadowVal) &&
                shadowVal.ToObject() is ISharpTSCallable or ISharpTSAsyncCallable or Runtime.BuiltIns.BuiltInMethod;
            if (!isShadowed)
            {
                if (GlobalFunctionRegistry.Instance.TryGetHandlerV2(gvName, out var handlerV2))
                {
                    _syncEvalWrapperV2Cached ??= expr => _syncContext.EvaluateExprAsync(expr);
                    return handlerV2!(_syncEvalWrapperV2Cached, call.Arguments, this).GetAwaiter().GetResult();
                }
                if (GlobalFunctionRegistry.Instance.TryGetHandler(gvName, out var handler))
                {
                    _syncEvalWrapperCached ??= expr => new ValueTask<object?>(_syncContext.EvaluateExprAsync(expr).Result.ToObject());
                    return RuntimeValue.FromBoxed(handler!(_syncEvalWrapperCached, call.Arguments, this).GetAwaiter().GetResult());
                }
            }
        }

        object? callee = Evaluate(call.Callee);

        // Optional call: return undefined if callee is nullish.
        // Per ECMA-262 §13.3.9, the short-circuit extends to the entire chain:
        // if any `?.` earlier in the callee expression returned null/undefined,
        // this call must also short-circuit.
        if ((call.Optional || HasOptionalInChain(call.Callee))
            && (callee == null || callee is Runtime.Types.SharpTSUndefined))
        {
            return RuntimeValue.Undefined;
        }

        // V2 fast path: no spread args and callee supports V2 — zero boxing
        if (callee is ISharpTSCallableV2 v2Callee && !HasSpreadArgs(call.Arguments))
        {
            int argCount = call.Arguments.Count;
            var rented = ArrayPool<RuntimeValue>.Shared.Rent(Math.Max(argCount, 1));
            try
            {
                for (int i = 0; i < argCount; i++)
                {
                    rented[i] = EvaluateRV(call.Arguments[i]);
                }
                return v2Callee.CallV2(this, rented.AsSpan(0, argCount));
            }
            finally
            {
                ArrayPool<RuntimeValue>.Shared.Return(rented);
            }
        }

        // Legacy path: spread args or non-V2 callables
        var argumentsList = ArgumentListPool.Rent();
        try
        {
            foreach (Expr argument in call.Arguments)
            {
                if (argument is Expr.Spread spread)
                {
                    object? spreadValue = Evaluate(spread.Expression);
                    // Use GetIterableElements to support custom iterables with Symbol.iterator
                    argumentsList.AddRange(GetIterableElements(spreadValue));
                }
                else
                {
                    argumentsList.Add(Evaluate(argument));
                }
            }

            if (callee is not ISharpTSCallable function)
            {
                if ((call.Optional || HasOptionalInChain(call.Callee))
                    && (callee == null || callee is Runtime.Types.SharpTSUndefined))
                {
                    return RuntimeValue.Undefined;
                }
                throw new InterpreterException($"Can only call functions and classes. (got {callee?.GetType().FullName ?? "null"} from callee={call.Callee.GetType().Name}:{(call.Callee is Expr.Variable vv2 ? vv2.Name.Lexeme : call.Callee is Expr.Get gg2 ? $"{(gg2.Object is Expr.Variable gov2 ? gov2.Name.Lexeme : gg2.Object.GetType().Name)}.{gg2.Name.Lexeme}" : call.Callee is Expr.GetIndex gi2 ? $"[{(gi2.Index is Expr.Literal il2 ? il2.Value : "?")}]" : "?")} at line {call.Paren.Line})");
            }

            // Per ECMA-262 §10.2.1: missing arguments become undefined; do not
            // reject calls for user-defined functions.
            return RuntimeValue.FromBoxed(function.Call(this, argumentsList));
        }
        finally
        {
            ArgumentListPool.Return(argumentsList);
        }
    }

    /// <summary>
    /// Checks if any arguments are spread expressions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasSpreadArgs(IReadOnlyList<Expr> arguments)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is Expr.Spread) return true;
        }
        return false;
    }

    /// <summary>
    /// Evaluates a binary operator expression.
    /// </summary>
    /// <param name="binary">The binary expression AST node.</param>
    /// <returns>The result of applying the operator to both operands.</returns>
    /// <remarks>
    /// Supports arithmetic (+, -, *, /, %, **), comparison (&gt;, &lt;, &gt;=, &lt;=, ==, ===, !=, !==),
    /// bitwise (&amp;, |, ^, &lt;&lt;, &gt;&gt;, &gt;&gt;&gt;), and special operators (in, instanceof).
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Guide/Expressions_and_operators">MDN Expressions and Operators</seealso>
    private RuntimeValue EvaluateBinary(Expr.Binary binary)
    {
        var leftRV = EvaluateRV(binary.Left);
        var rightRV = EvaluateRV(binary.Right);

        // Fast path: both operands are numbers — avoid boxing entirely
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
                    // Use Equals() not == for NaN consistency: double.NaN.Equals(NaN) is true in C#
                    // (matches existing interpreter behavior where NaN === NaN is true)
                    bool equal = l.Equals(r);
                    return RuntimeValue.FromBoolean(eq.IsNegated ? !equal : equal);
                case OperatorDescriptor.Bitwise or OperatorDescriptor.BitwiseShift:
                    return RuntimeValue.FromNumber(EvaluateBitwise(binary.Operator.Type, JsToInt32(l), JsToInt32(r)));
                case OperatorDescriptor.UnsignedRightShift:
                    return RuntimeValue.FromNumber((double)(JsToUint32(l) >> (JsToInt32(r) & 0x1F)));
            }
        }

        // Slow path: BigInt, string concatenation, mixed types, in, instanceof
        return EvaluateBinaryOperationRV(binary.Operator, leftRV, rightRV);
    }

    /// <summary>
    /// RuntimeValue-native binary operation — avoids boxing for BigInt, string concat, equality, etc.
    /// </summary>
    private RuntimeValue EvaluateBinaryOperationRV(Token op, RuntimeValue leftRV, RuntimeValue rightRV)
    {
        // Check for BigInt (Kind == BigInt, not Object)
        System.Numerics.BigInteger? leftBigInt = leftRV.IsBigInt ? leftRV.AsBigInt().Value : null;
        System.Numerics.BigInteger? rightBigInt = rightRV.IsBigInt ? rightRV.AsBigInt().Value : null;

        if (leftBigInt.HasValue || rightBigInt.HasValue)
        {
            return EvaluateBigIntBinaryRV(op.Type, leftRV, rightRV, leftBigInt, rightBigInt);
        }

        object? left = leftRV.ToObject();
        object? right = rightRV.ToObject();

        var desc = SemanticOperatorResolver.Resolve(op.Type);

        return desc switch
        {
            OperatorDescriptor.Plus when left is string || right is string =>
                RuntimeValue.FromString(string.Concat(
                    left as string ?? Stringify(left),
                    right as string ?? Stringify(right))),
            OperatorDescriptor.Plus => RuntimeValue.FromNumber((double)left! + (double)right!),
            OperatorDescriptor.Arithmetic => RuntimeValue.FromNumber(EvaluateArithmetic(op.Type, (double)left!, (double)right!)),
            OperatorDescriptor.Power => RuntimeValue.FromNumber(Math.Pow((double)left!, (double)right!)),
            // JS AbstractRelationalComparison: string vs string → lexicographic.
            OperatorDescriptor.Comparison =>
                left is string ls && right is string rs
                    ? RuntimeValue.FromBoolean(EvaluateStringComparison(op.Type, ls, rs))
                    : RuntimeValue.FromBoolean(EvaluateComparison(op.Type, (double)left!, (double)right!)),
            OperatorDescriptor.Equality eq => RuntimeValue.FromBoolean(EvaluateEquality(left, right, eq.IsStrict, eq.IsNegated)),
            OperatorDescriptor.Bitwise or OperatorDescriptor.BitwiseShift =>
                RuntimeValue.FromNumber(EvaluateBitwise(op.Type, ToInt32(left), ToInt32(right))),
            OperatorDescriptor.UnsignedRightShift => RuntimeValue.FromNumber((double)(ToUint32(left) >> (ToInt32(right) & 0x1F))),
            OperatorDescriptor.In => RuntimeValue.FromBoxed(EvaluateIn(left, right)),
            OperatorDescriptor.InstanceOf => RuntimeValue.FromBoxed(EvaluateInstanceof(left, right)),
            _ => RuntimeValue.Undefined
        };
    }

    /// <summary>
    /// Core binary operation logic, shared between sync and async evaluation.
    /// Uses SemanticOperatorResolver for centralized operator dispatch.
    /// </summary>
    private object? EvaluateBinaryOperation(Token op, object? left, object? right)
    {
        // Check for bigint operations first
        var leftBigInt = GetBigIntValue(left);
        var rightBigInt = GetBigIntValue(right);

        if (leftBigInt.HasValue || rightBigInt.HasValue)
        {
            return EvaluateBigIntBinary(op.Type, left, right, leftBigInt, rightBigInt);
        }

        var desc = SemanticOperatorResolver.Resolve(op.Type);

        return desc switch
        {
            OperatorDescriptor.Plus => EvaluatePlus(left, right),
            OperatorDescriptor.Arithmetic => EvaluateArithmetic(op.Type, (double)left!, (double)right!),
            OperatorDescriptor.Power => Math.Pow((double)left!, (double)right!),
            // JS AbstractRelationalComparison: if both are strings, compare
            // lexicographically; otherwise coerce to number.
            OperatorDescriptor.Comparison =>
                left is string ls && right is string rs
                    ? EvaluateStringComparison(op.Type, ls, rs)
                    : EvaluateComparison(op.Type, (double)left!, (double)right!),
            OperatorDescriptor.Equality eq => EvaluateEquality(left, right, eq.IsStrict, eq.IsNegated),
            OperatorDescriptor.Bitwise or OperatorDescriptor.BitwiseShift =>
                EvaluateBitwise(op.Type, ToInt32(left), ToInt32(right)),
            OperatorDescriptor.UnsignedRightShift => (double)(ToUint32(left) >> (ToInt32(right) & 0x1F)),
            OperatorDescriptor.In => EvaluateIn(left, right),
            OperatorDescriptor.InstanceOf => EvaluateInstanceof(left, right),
            _ => null
        };
    }

    /// <summary>
    /// Evaluates arithmetic operators (-, *, /, %).
    /// </summary>
    private static double EvaluateArithmetic(TokenType op, double left, double right) => op switch
    {
        TokenType.MINUS => left - right,
        TokenType.STAR => left * right,
        TokenType.SLASH => left / right,
        TokenType.PERCENT => left % right,
        _ => throw new InterpreterException($"Unknown arithmetic operator: {op}")
    };

    /// <summary>
    /// Evaluates comparison operators (&lt;, &gt;, &lt;=, &gt;=).
    /// </summary>
    private static bool EvaluateComparison(TokenType op, double left, double right) => op switch
    {
        TokenType.LESS => left < right,
        TokenType.GREATER => left > right,
        TokenType.LESS_EQUAL => left <= right,
        TokenType.GREATER_EQUAL => left >= right,
        _ => throw new InterpreterException($"Unknown comparison operator: {op}")
    };

    /// <summary>
    /// Evaluates string relational comparison using lexicographic ordering
    /// (JS AbstractRelationalComparison when both operands are strings).
    /// </summary>
    private static bool EvaluateStringComparison(TokenType op, string left, string right)
    {
        int cmp = string.CompareOrdinal(left, right);
        return op switch
        {
            TokenType.LESS => cmp < 0,
            TokenType.GREATER => cmp > 0,
            TokenType.LESS_EQUAL => cmp <= 0,
            TokenType.GREATER_EQUAL => cmp >= 0,
            _ => throw new InterpreterException($"Unknown comparison operator: {op}")
        };
    }

    /// <summary>
    /// Evaluates equality operators (==, ===, !=, !==).
    /// </summary>
    private bool EvaluateEquality(object? left, object? right, bool isStrict, bool isNegated)
    {
        bool result = isStrict ? IsStrictEqual(left, right) : IsEqual(left, right);
        return isNegated ? !result : result;
    }

    /// <summary>
    /// Evaluates bitwise operators (&amp;, |, ^, &lt;&lt;, &gt;&gt;).
    /// </summary>
    private static double EvaluateBitwise(TokenType op, int left, int right) => op switch
    {
        TokenType.AMPERSAND => left & right,
        TokenType.PIPE => left | right,
        TokenType.CARET => left ^ right,
        TokenType.LESS_LESS => left << (right & 0x1F),
        TokenType.GREATER_GREATER => left >> (right & 0x1F),
        _ => throw new InterpreterException($"Unknown bitwise operator: {op}")
    };

    private System.Numerics.BigInteger? GetBigIntValue(object? value) => value switch
    {
        SharpTSBigInt bi => bi.Value,
        System.Numerics.BigInteger biVal => biVal,
        _ => null
    };

    private RuntimeValue EvaluateBigIntBinaryRV(TokenType op, RuntimeValue leftRV, RuntimeValue rightRV,
        System.Numerics.BigInteger? leftBi, System.Numerics.BigInteger? rightBi)
    {
        // Equality operators allow mixed types (bigint with anything)
        if (BigIntOperatorHelper.IsEqualityOperator(op))
        {
            if (!leftBi.HasValue || !rightBi.HasValue)
            {
                // Mixed types: equality is false, inequality is true
                return RuntimeValue.FromBoolean(op is TokenType.BANG_EQUAL or TokenType.BANG_EQUAL_EQUAL);
            }
            return BigIntOperatorHelper.EvaluateBinaryRV(op, leftBi.Value, rightBi.Value);
        }

        // All other operators require both to be bigint
        if (!leftBi.HasValue || !rightBi.HasValue)
            throw new InterpreterException("Cannot mix bigint and other types in operations.");

        return BigIntOperatorHelper.EvaluateBinaryRV(op, leftBi.Value, rightBi.Value);
    }

    private object EvaluateBigIntBinary(TokenType op, object? left, object? right,
        System.Numerics.BigInteger? leftBi, System.Numerics.BigInteger? rightBi)
    {
        // Equality operators allow mixed types (bigint with anything)
        if (BigIntOperatorHelper.IsEqualityOperator(op))
        {
            if (!leftBi.HasValue || !rightBi.HasValue)
            {
                // Mixed types: equality is false, inequality is true
                return op is TokenType.BANG_EQUAL or TokenType.BANG_EQUAL_EQUAL;
            }
            return BigIntOperatorHelper.EvaluateBinary(op, leftBi.Value, rightBi.Value);
        }

        // All other operators require both to be bigint
        if (!leftBi.HasValue || !rightBi.HasValue)
            throw new InterpreterException("Cannot mix bigint and other types in operations.");

        // Use centralized helper for all BigInt operations
        return BigIntOperatorHelper.EvaluateBinary(op, leftBi.Value, rightBi.Value);
    }

    /// <summary>
    /// Converts a boxed double to a 32-bit signed integer per ECMA-262 ToInt32.
    /// </summary>
    /// <seealso href="https://tc39.es/ecma262/#sec-toint32">ECMAScript ToInt32</seealso>
    private static int ToInt32(object? value) => JsToInt32((double)value!);

    /// <summary>
    /// Converts a boxed double to a 32-bit unsigned integer per ECMA-262 ToUint32.
    /// </summary>
    /// <seealso href="https://tc39.es/ecma262/#sec-touint32">ECMAScript ToUint32</seealso>
    private static uint ToUint32(object? value) => JsToUint32((double)value!);

    // ECMA-262 ToInt32 / ToUint32 on a double. Unlike C#'s saturating (int) cast,
    // non-finite → 0 and out-of-range doubles wrap modulo 2^32. Mirrors JsToInt32 in
    // Compilation/RuntimeEmitter.CoreUtilities.cs so interpreted and compiled modes agree.
    private static int JsToInt32(double n)
    {
        if (!double.IsFinite(n)) return 0;
        double t = n >= 0 ? Math.Floor(n) : Math.Ceiling(n);
        double mod = t - Math.Floor(t / 4294967296.0) * 4294967296.0;
        return mod >= 2147483648.0 ? (int)(mod - 4294967296.0) : (int)mod;
    }

    private static uint JsToUint32(double n)
    {
        if (!double.IsFinite(n)) return 0;
        double t = n >= 0 ? Math.Floor(n) : Math.Ceiling(n);
        return (uint)(t - Math.Floor(t / 4294967296.0) * 4294967296.0);
    }

    /// <summary>
    /// Evaluates the <c>instanceof</c> operator.
    /// </summary>
    /// <param name="left">The instance to check.</param>
    /// <param name="right">The class to check against.</param>
    /// <returns><c>true</c> if the instance is of the specified class or a subclass; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Walks up the inheritance chain to check if the instance's class or any superclass
    /// matches the target class name.
    /// </remarks>
    /// <seealso href="https://www.typescriptlang.org/docs/handbook/2/narrowing.html#instanceof-narrowing">TypeScript instanceof Narrowing</seealso>
    private object EvaluateInstanceof(object? left, object? right)
    {
        // Built-in constructor instanceof: val instanceof Map, val instanceof Set, etc.
        if (right is SharpTSBuiltInConstructor ctor)
        {
            return ctor.Name switch
            {
                "Map" => left is SharpTSMap,
                "Set" => left is SharpTSSet,
                "WeakMap" => left is SharpTSWeakMap,
                "WeakSet" => left is SharpTSWeakSet,
                "Date" => left is SharpTSDate,
                "RegExp" => left is SharpTSRegExp,
                "TextEncoder" => left is SharpTSTextEncoder,
                "TextDecoder" => left is SharpTSTextDecoder,
                "Promise" => left is SharpTSPromise || left is System.Threading.Tasks.Task,
                "Buffer" => left is SharpTSBuffer,
                _ => false
            };
        }

        // Buffer is registered as a namespace singleton (SharpTSBufferConstructor),
        // not as a SharpTSBuiltInConstructor, so `x instanceof Buffer` lands here
        // with the singleton object on the RHS. Match it explicitly.
        if (right is SharpTSBufferConstructor)
            return left is SharpTSBuffer;

        // Constructor-function instanceof (JS `new Func()` pattern).
        // An object is `instanceof Func` when any link in its prototype
        // chain === Func.prototype.
        if (right is SharpTSFunction ctorFn)
        {
            if (!ctorFn.TryGetProperty("prototype", out var protoObj))
                return false;
            object? current = left;
            // Walk __proto__ chain; cap at a sensible depth to avoid cycles.
            for (int i = 0; i < 64 && current is SharpTSObject curObj; i++)
            {
                if (curObj.HasProperty("__proto__")
                    && curObj.GetProperty("__proto__") is var p
                    && ReferenceEquals(p, protoObj))
                {
                    return true;
                }
                current = curObj.HasProperty("__proto__") ? curObj.GetProperty("__proto__") : null;
                if (ReferenceEquals(current, curObj)) break;
            }
            return false;
        }

        if (right is not SharpTSClass targetClass)
            return false;

        // Standard path: SharpTSInstance — walk the class chain
        if (left is SharpTSInstance instance)
        {
            SharpTSClass? current = instance.GetClass();
            while (current != null)
            {
                if (current.Name == targetClass.Name) return true;
                current = current.Superclass;
            }
            return false;
        }

        // Legacy path: SharpTSError instances created via BuiltInConstructorFactory
        // (not SharpTSInstance, but should still pass instanceof Error/TypeError/etc.)
        if (left is SharpTSError error && targetClass is SharpTSErrorClass)
        {
            // Walk the error type hierarchy: TypeError extends Error, etc.
            var errorName = error.Name; // e.g. "TypeError"
            var targetName = targetClass.Name; // e.g. "Error"
            if (errorName == targetName) return true;
            // All built-in error subtypes extend Error
            if (targetName == "Error") return true;
            return false;
        }

        return false;
    }

    /// <summary>
    /// Core logical operation logic, shared between sync and async evaluation.
    /// Uses lazy evaluation via Func delegate to preserve short-circuit semantics.
    /// </summary>
    /// <param name="op">The operator type (OR_OR or AND_AND).</param>
    /// <param name="left">The already-evaluated left operand.</param>
    /// <param name="evaluateRight">A function to evaluate the right operand (only called if needed).</param>
    /// <returns>The value that determined the result.</returns>
    private object? EvaluateLogicalCore(TokenType op, object? left, Func<object?> evaluateRight)
    {
        if (op == TokenType.OR_OR)
            return IsTruthy(left) ? left : evaluateRight();
        return !IsTruthy(left) ? left : evaluateRight();
    }

    /// <summary>
    /// Evaluates a logical operator expression (AND/OR) with short-circuit evaluation.
    /// Uses RuntimeValue throughout to avoid boxing on the fast path.
    /// </summary>
    private RuntimeValue EvaluateLogical(Expr.Logical logical)
    {
        var left = EvaluateRV(logical.Left);
        if (logical.Operator.Type == TokenType.OR_OR)
            return IsTruthy(left) ? left : EvaluateRV(logical.Right);
        return !IsTruthy(left) ? left : EvaluateRV(logical.Right);
    }

    /// <summary>
    /// Core nullish coalescing logic, shared between sync and async evaluation.
    /// </summary>
    private object? EvaluateNullishCoalescingCore(object? left, Func<object?> evaluateRight) =>
        (left == null || left is Runtime.Types.SharpTSUndefined) ? evaluateRight() : left;

    /// <summary>
    /// Evaluates the nullish coalescing operator (<c>??</c>).
    /// Uses RuntimeValue throughout — IsNullish check avoids boxing.
    /// </summary>
    private RuntimeValue EvaluateNullishCoalescing(Expr.NullishCoalescing nc)
    {
        var left = EvaluateRV(nc.Left);
        return left.IsNullish ? EvaluateRV(nc.Right) : left;
    }

    /// <summary>
    /// Core ternary operation logic, shared between sync and async evaluation.
    /// </summary>
    private object? EvaluateTernaryCore(object? condition, Func<object?> evalThen, Func<object?> evalElse) =>
        IsTruthy(condition) ? evalThen() : evalElse();

    /// <summary>
    /// Evaluates a ternary conditional expression (<c>?:</c>).
    /// Uses RuntimeValue — IsTruthy check on condition avoids boxing.
    /// </summary>
    private RuntimeValue EvaluateTernary(Expr.Ternary ternary)
    {
        var condition = EvaluateRV(ternary.Condition);
        return IsTruthy(condition) ? EvaluateRV(ternary.ThenBranch) : EvaluateRV(ternary.ElseBranch);
    }

    // ===================== Async Core Methods =====================

    /// <summary>
    /// Async version of logical operation core logic.
    /// Uses lazy evaluation via Func delegate to preserve short-circuit semantics.
    /// </summary>
    private async Task<RuntimeValue> EvaluateLogicalCoreAsync(
        TokenType op,
        Task<RuntimeValue> leftTask,
        Func<Task<RuntimeValue>> evaluateRightAsync)
    {
        var left = await leftTask;
        if (op == TokenType.OR_OR)
            return IsTruthy(left) ? left : await evaluateRightAsync();
        return !IsTruthy(left) ? left : await evaluateRightAsync();
    }

    /// <summary>
    /// Async version of nullish coalescing core logic.
    /// Uses lazy evaluation via Func delegate to preserve short-circuit semantics.
    /// </summary>
    private async Task<RuntimeValue> EvaluateNullishCoalescingCoreAsync(
        Task<RuntimeValue> leftTask,
        Func<Task<RuntimeValue>> evaluateRightAsync)
    {
        var left = await leftTask;
        return left.IsNullish
            ? await evaluateRightAsync()
            : left;
    }

    /// <summary>
    /// Async version of ternary operation core logic.
    /// Uses lazy evaluation via Func delegates to ensure only one branch is evaluated.
    /// </summary>
    private async Task<RuntimeValue> EvaluateTernaryCoreAsync(
        Task<RuntimeValue> conditionTask,
        Func<Task<RuntimeValue>> evalThenAsync,
        Func<Task<RuntimeValue>> evalElseAsync)
    {
        var condition = await conditionTask;
        return IsTruthy(condition)
            ? await evalThenAsync()
            : await evalElseAsync();
    }

    /// <summary>
    /// Applies a compound assignment operator to two values.
    /// </summary>
    /// <param name="op">The compound operator token type (e.g., PLUS_EQUAL, MINUS_EQUAL).</param>
    /// <param name="left">The current value of the target.</param>
    /// <param name="right">The value to combine with.</param>
    /// <returns>The result of the operation.</returns>
    /// <remarks>
    /// Supports arithmetic (+=, -=, *=, /=, %=) and bitwise (&amp;=, |=, ^=, &lt;&lt;=, &gt;&gt;=, &gt;&gt;&gt;=)
    /// compound operators. String concatenation is handled for +=.
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/Addition_assignment">MDN Compound Assignment</seealso>
    private object? ApplyCompoundOperator(TokenType op, object? left, object? right)
    {
        return ApplyCompoundOperatorRV(op, RuntimeValue.FromBoxed(left), RuntimeValue.FromBoxed(right)).ToObject();
    }

    /// <summary>
    /// RuntimeValue-native compound operator — avoids boxing for numeric operations.
    /// </summary>
    private RuntimeValue ApplyCompoundOperatorRV(TokenType op, RuntimeValue left, RuntimeValue right)
    {
        // Fast path: both numeric (most common for compound assignment)
        if (left.IsNumber && right.IsNumber)
        {
            double l = left.AsNumberUnsafe(), r = right.AsNumberUnsafe();
            return op switch
            {
                TokenType.PLUS_EQUAL => RuntimeValue.FromNumber(l + r),
                TokenType.MINUS_EQUAL => RuntimeValue.FromNumber(l - r),
                TokenType.STAR_EQUAL => RuntimeValue.FromNumber(l * r),
                TokenType.SLASH_EQUAL => RuntimeValue.FromNumber(l / r),
                TokenType.PERCENT_EQUAL => RuntimeValue.FromNumber(l % r),
                TokenType.AMPERSAND_EQUAL => RuntimeValue.FromNumber(JsToInt32(l) & JsToInt32(r)),
                TokenType.PIPE_EQUAL => RuntimeValue.FromNumber(JsToInt32(l) | JsToInt32(r)),
                TokenType.CARET_EQUAL => RuntimeValue.FromNumber(JsToInt32(l) ^ JsToInt32(r)),
                TokenType.LESS_LESS_EQUAL => RuntimeValue.FromNumber(JsToInt32(l) << (JsToInt32(r) & 0x1F)),
                TokenType.GREATER_GREATER_EQUAL => RuntimeValue.FromNumber(JsToInt32(l) >> (JsToInt32(r) & 0x1F)),
                TokenType.GREATER_GREATER_GREATER_EQUAL => RuntimeValue.FromNumber(JsToUint32(l) >> (JsToInt32(r) & 0x1F)),
                _ => throw new InterpreterException($"Unknown compound operator: {op}")
            };
        }

        // String concatenation path
        if (op == TokenType.PLUS_EQUAL && left.IsString)
        {
            return RuntimeValue.FromString(string.Concat(left.AsStringUnsafe(), Stringify(right.ToObject())));
        }

        // Fallback for mixed types
        object? l2 = left.ToObject(), r2 = right.ToObject();
        return op switch
        {
            TokenType.PLUS_EQUAL => RuntimeValue.FromBoxed(
                l2 is string s ? string.Concat(s, Stringify(r2)) : (double)l2! + (double)r2!),
            _ => RuntimeValue.FromNumber(op switch
            {
                TokenType.MINUS_EQUAL => (double)l2! - (double)r2!,
                TokenType.STAR_EQUAL => (double)l2! * (double)r2!,
                TokenType.SLASH_EQUAL => (double)l2! / (double)r2!,
                TokenType.PERCENT_EQUAL => (double)l2! % (double)r2!,
                TokenType.AMPERSAND_EQUAL => ToInt32(l2) & ToInt32(r2),
                TokenType.PIPE_EQUAL => ToInt32(l2) | ToInt32(r2),
                TokenType.CARET_EQUAL => ToInt32(l2) ^ ToInt32(r2),
                TokenType.LESS_LESS_EQUAL => ToInt32(l2) << (ToInt32(r2) & 0x1F),
                TokenType.GREATER_GREATER_EQUAL => ToInt32(l2) >> (ToInt32(r2) & 0x1F),
                TokenType.GREATER_GREATER_GREATER_EQUAL => (double)(ToUint32(l2) >> (ToInt32(r2) & 0x1F)),
                _ => throw new InterpreterException($"Unknown compound operator: {op}")
            })
        };
    }

    // ===================== Shared Builder Helpers =====================

    /// <summary>
    /// Builds a SharpTSArray from evaluated elements, handling spread elements.
    /// Shared between sync and async array evaluation paths.
    /// </summary>
    /// <param name="evaluatedElements">Tuples of (isSpread, evaluatedValue).</param>
    /// <returns>A new SharpTSArray containing all elements.</returns>
    private SharpTSArray BuildArrayFromElements(IEnumerable<(bool isSpread, object? value)> evaluatedElements)
    {
        List<object?> elements = [];
        foreach (var (isSpread, value) in evaluatedElements)
        {
            if (isSpread)
            {
                // Use GetIterableElements to support custom iterables with Symbol.iterator
                elements.AddRange(GetIterableElements(value));
            }
            else
            {
                elements.Add(value);
            }
        }
        return new SharpTSArray(elements);
    }

    /// <summary>
    /// Builds a SharpTSObject from evaluated properties.
    /// Shared between sync and async object evaluation paths.
    /// </summary>
    /// <param name="stringFields">String-keyed properties.</param>
    /// <param name="symbolFields">Symbol-keyed properties.</param>
    /// <returns>A new SharpTSObject with all properties set.</returns>
    private static SharpTSObject BuildObjectFromFields(
        Dictionary<string, object?> stringFields,
        Dictionary<SharpTSSymbol, object?> symbolFields)
    {
        var result = new SharpTSObject(stringFields);
        foreach (var (sym, val) in symbolFields)
        {
            result.SetBySymbol(sym, val);
        }
        return result;
    }

    /// <summary>
    /// Builds a template literal string from strings and evaluated expressions.
    /// Shared between sync and async template literal evaluation paths.
    /// </summary>
    /// <param name="strings">The static string parts of the template.</param>
    /// <param name="evaluatedExprs">The evaluated expression values.</param>
    /// <returns>The interpolated string result.</returns>
    private string BuildTemplateLiteralString(IReadOnlyList<string> strings, IReadOnlyList<object?> evaluatedExprs)
    {
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < strings.Count; i++)
        {
            result.Append(strings[i]);
            if (i < evaluatedExprs.Count)
            {
                result.Append(Stringify(evaluatedExprs[i]));
            }
        }
        return result.ToString();
    }
}
