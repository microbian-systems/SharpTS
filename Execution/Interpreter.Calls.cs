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
        if (call.Callee is Expr.Variable globalVar &&
            GlobalFunctionRegistry.Instance.TryGetHandler(globalVar.Name.Lexeme, out var handler))
        {
            Func<Expr, ValueTask<object?>> evalWrapper = async expr => (await ctx.EvaluateExprAsync(expr)).ToObject();
            return await handler!(evalWrapper, call.Arguments, this);
        }

        object? callee = (await ctx.EvaluateExprAsync(call.Callee)).ToObject();

        List<object?> argumentsList = [];
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
            throw new InterpreterException("Can only call functions and classes.");
        }

        if (argumentsList.Count < function.Arity())
        {
            throw new InterpreterException($"Expected at least {function.Arity()} arguments but got {argumentsList.Count}.");
        }

        return function.Call(this, argumentsList);
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
        if (call.Callee is Expr.Variable globalVar &&
            GlobalFunctionRegistry.Instance.TryGetHandler(globalVar.Name.Lexeme, out var handler))
        {
            // Use cached wrapper to avoid per-call delegate allocation
            _syncEvalWrapperCached ??= expr => new ValueTask<object?>(_syncContext.EvaluateExprAsync(expr).Result.ToObject());
            return RuntimeValue.FromBoxed(handler!(_syncEvalWrapperCached, call.Arguments, this).GetAwaiter().GetResult());
        }

        object? callee = Evaluate(call.Callee);

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
        List<object?> argumentsList = [];
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
            throw new InterpreterException("Can only call functions and classes.");
        }

        if (argumentsList.Count < function.Arity())
        {
            throw new InterpreterException($"Expected at least {function.Arity()} arguments but got {argumentsList.Count}.");
        }

        return RuntimeValue.FromBoxed(function.Call(this, argumentsList));
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
                    return RuntimeValue.FromNumber(EvaluateBitwise(binary.Operator.Type, (int)l, (int)r));
                case OperatorDescriptor.UnsignedRightShift:
                    return RuntimeValue.FromNumber((double)((uint)(int)l >> ((int)r & 0x1F)));
            }
        }

        // Slow path: BigInt, string concatenation, mixed types, in, instanceof
        object? left = leftRV.ToObject();
        object? right = rightRV.ToObject();
        return RuntimeValue.FromBoxed(EvaluateBinaryOperation(binary.Operator, left, right));
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
            OperatorDescriptor.Comparison => EvaluateComparison(op.Type, (double)left!, (double)right!),
            OperatorDescriptor.Equality eq => EvaluateEquality(left, right, eq.IsStrict, eq.IsNegated),
            OperatorDescriptor.Bitwise or OperatorDescriptor.BitwiseShift =>
                EvaluateBitwise(op.Type, ToInt32(left), ToInt32(right)),
            OperatorDescriptor.UnsignedRightShift => (double)((uint)ToInt32(left) >> (ToInt32(right) & 0x1F)),
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
    /// Converts a runtime value to a 32-bit signed integer.
    /// </summary>
    /// <param name="value">The value to convert (expected to be a double).</param>
    /// <returns>The 32-bit integer representation.</returns>
    /// <remarks>
    /// Used for bitwise operations which operate on 32-bit integers per ECMAScript spec.
    /// </remarks>
    /// <seealso href="https://tc39.es/ecma262/#sec-toint32">ECMAScript ToInt32</seealso>
    private int ToInt32(object? value) => (int)(double)value!;

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
        if (left is not SharpTSInstance instance || right is not SharpTSClass targetClass)
            return false;
        SharpTSClass? current = instance.GetClass();
        while (current != null)
        {
            if (current.Name == targetClass.Name) return true;
            current = current.Superclass;
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
    private async Task<object?> EvaluateLogicalCoreAsync(
        TokenType op,
        Task<object?> leftTask,
        Func<Task<object?>> evaluateRightAsync)
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
    private async Task<object?> EvaluateNullishCoalescingCoreAsync(
        Task<object?> leftTask,
        Func<Task<object?>> evaluateRightAsync)
    {
        var left = await leftTask;
        return (left == null || left is Runtime.Types.SharpTSUndefined)
            ? await evaluateRightAsync()
            : left;
    }

    /// <summary>
    /// Async version of ternary operation core logic.
    /// Uses lazy evaluation via Func delegates to ensure only one branch is evaluated.
    /// </summary>
    private async Task<object?> EvaluateTernaryCoreAsync(
        Task<object?> conditionTask,
        Func<Task<object?>> evalThenAsync,
        Func<Task<object?>> evalElseAsync)
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
                TokenType.AMPERSAND_EQUAL => RuntimeValue.FromNumber((int)l & (int)r),
                TokenType.PIPE_EQUAL => RuntimeValue.FromNumber((int)l | (int)r),
                TokenType.CARET_EQUAL => RuntimeValue.FromNumber((int)l ^ (int)r),
                TokenType.LESS_LESS_EQUAL => RuntimeValue.FromNumber((int)l << ((int)r & 0x1F)),
                TokenType.GREATER_GREATER_EQUAL => RuntimeValue.FromNumber((int)l >> ((int)r & 0x1F)),
                TokenType.GREATER_GREATER_GREATER_EQUAL => RuntimeValue.FromNumber((uint)(int)l >> ((int)r & 0x1F)),
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
                TokenType.GREATER_GREATER_GREATER_EQUAL => (int)((uint)ToInt32(l2) >> (ToInt32(r2) & 0x1F)),
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
