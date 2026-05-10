using SharpTS.Compilation;
using SharpTS.Parsing;
using SharpTS.Runtime;
using SharpTS.Runtime.BuiltIns;
using SharpTS.Runtime.Exceptions;
using SharpTS.Runtime.Types;

namespace SharpTS.Execution;

// Note: This file uses InterpreterException for runtime errors

public partial class Interpreter
{
    /// <summary>
    /// Evaluates a compound assignment expression on a variable (e.g., <c>x += 1</c>).
    /// </summary>
    /// <param name="compound">The compound assignment expression AST node.</param>
    /// <returns>The new value after the operation.</returns>
    /// <remarks>
    /// Retrieves the current value, applies the compound operator via
    /// <see cref="ApplyCompoundOperator"/>, and stores the result.
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/Addition_assignment">MDN Compound Assignment</seealso>
    private RuntimeValue EvaluateCompoundAssign(Expr.CompoundAssign compound)
    {
        RuntimeValue currentValue = _environment.Get(compound.Name);
        RuntimeValue addValue = EvaluateRV(compound.Value);
        RuntimeValue newValue = ApplyCompoundOperatorRV(compound.Operator.Type, currentValue, addValue);
        _environment.Assign(compound.Name, newValue);
        return newValue;
    }

    /// <summary>
    /// Evaluates a compound assignment expression on an object property (e.g., <c>obj.x += 1</c>).
    /// </summary>
    /// <param name="compound">The compound property assignment expression AST node.</param>
    /// <returns>The new value after the operation.</returns>
    /// <remarks>
    /// Works with both class instances (<see cref="SharpTSInstance"/>) and
    /// plain objects (<see cref="SharpTSObject"/>).
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/Addition_assignment">MDN Compound Assignment</seealso>
    private RuntimeValue EvaluateCompoundSet(Expr.CompoundSet compound)
    {
        object? obj = Evaluate(compound.Object);

        if (TryGetPropertyRV(obj, compound.Name, out RuntimeValue currentRV))
        {
            RuntimeValue addValue = EvaluateRV(compound.Value);
            RuntimeValue newValue = ApplyCompoundOperatorRV(compound.Operator.Type, currentRV, addValue);
            if (TrySetProperty(obj, compound.Name, newValue.ToObject()))
            {
                return newValue;
            }
        }

        throw new InterpreterException($"Only instances and objects have fields. Cannot compound-set '{compound.Name.Lexeme}' on {obj?.GetType().Name ?? "null"}.");
    }

    /// <summary>
    /// Evaluates a compound assignment expression on an array element (e.g., <c>arr[i] += 1</c>).
    /// </summary>
    /// <param name="compound">The compound index assignment expression AST node.</param>
    /// <returns>The new value after the operation.</returns>
    /// <remarks>
    /// Currently only supports array element compound assignment.
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/Addition_assignment">MDN Compound Assignment</seealso>
    private RuntimeValue EvaluateCompoundSetIndex(Expr.CompoundSetIndex compound)
    {
        object? obj = Evaluate(compound.Object);
        object? index = Evaluate(compound.Index);

        if (obj is SharpTSArray array && index is double idx)
        {
            RuntimeValue addValue = EvaluateRV(compound.Value);
            RuntimeValue newValue = ApplyCompoundOperatorRV(compound.Operator.Type, array.GetRV((int)idx), addValue);
            array.Set((int)idx, newValue.ToObject());
            return newValue;
        }

        throw new InterpreterException("Compound index assignment not supported on this type.");
    }

    /// <summary>
    /// Evaluates a logical assignment expression on a variable (e.g., <c>x &&= y</c>, <c>x ||= y</c>, <c>x ??= y</c>).
    /// </summary>
    /// <param name="logical">The logical assignment expression AST node.</param>
    /// <returns>The result of the logical assignment (the final value of x).</returns>
    /// <remarks>
    /// Unlike compound assignment, logical assignment has short-circuit semantics:
    /// - <c>x &&= y</c>: Only assigns y to x if x is truthy
    /// - <c>x ||= y</c>: Only assigns y to x if x is falsy
    /// - <c>x ??= y</c>: Only assigns y to x if x is null/undefined
    /// </remarks>
    private RuntimeValue EvaluateLogicalAssign(Expr.LogicalAssign logical)
    {
        RuntimeValue currentValue = _environment.Get(logical.Name);

        switch (logical.Operator.Type)
        {
            case TokenType.AND_AND_EQUAL:
                if (!currentValue.IsTruthy()) return currentValue;
                break;
            case TokenType.OR_OR_EQUAL:
                if (currentValue.IsTruthy()) return currentValue;
                break;
            case TokenType.QUESTION_QUESTION_EQUAL:
                if (!currentValue.IsNullish) return currentValue;
                break;
        }

        // Short-circuit condition not met, evaluate and assign
        RuntimeValue newValue = EvaluateRV(logical.Value);
        _environment.Assign(logical.Name, newValue);
        return newValue;
    }

    /// <summary>
    /// Evaluates a logical assignment expression on an object property (e.g., <c>obj.x &&= y</c>).
    /// </summary>
    private RuntimeValue EvaluateLogicalSet(Expr.LogicalSet logical)
    {
        object? obj = Evaluate(logical.Object);

        if (!TryGetPropertyRV(obj, logical.Name, out RuntimeValue currentRV))
        {
            throw new InterpreterException($"Only instances and objects have fields. Cannot logical-get '{logical.Name.Lexeme}' on {obj?.GetType().Name ?? "null"}.");
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

        // Short-circuit condition not met, evaluate and assign
        RuntimeValue newValue = EvaluateRV(logical.Value);
        if (!TrySetProperty(obj, logical.Name, newValue.ToObject()))
        {
            throw new InterpreterException($"Only instances and objects have fields. Cannot logical-set '{logical.Name.Lexeme}' on {obj?.GetType().Name ?? "null"}.");
        }
        return newValue;
    }

    /// <summary>
    /// Evaluates a logical assignment expression on an array element (e.g., <c>arr[i] &&= y</c>).
    /// </summary>
    private RuntimeValue EvaluateLogicalSetIndex(Expr.LogicalSetIndex logical)
    {
        object? obj = Evaluate(logical.Object);
        object? index = Evaluate(logical.Index);

        if (obj is SharpTSArray array && index is double idx)
        {
            RuntimeValue currentRV = array.GetRV((int)idx);

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

            // Short-circuit condition not met, evaluate and assign
            RuntimeValue newValue = EvaluateRV(logical.Value);
            array.Set((int)idx, newValue.ToObject());
            return newValue;
        }

        throw new InterpreterException("Logical index assignment not supported on this type.");
    }

    /// <summary>
    /// Evaluates a prefix increment or decrement expression (<c>++x</c> or <c>--x</c>).
    /// </summary>
    /// <param name="prefix">The prefix increment expression AST node.</param>
    /// <returns>The new value after incrementing/decrementing.</returns>
    /// <remarks>
    /// Prefix operators modify the value and return the new value.
    /// Supports variables, property access, and index access as operands.
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/Increment">MDN Increment Operator</seealso>
    private RuntimeValue EvaluatePrefixIncrement(Expr.PrefixIncrement prefix)
    {
        double delta = prefix.Operator.Type == TokenType.PLUS_PLUS ? 1 : -1;
        return EvaluateIncrement(prefix.Operand, delta, returnOld: false);
    }

    /// <summary>
    /// Evaluates a postfix increment or decrement expression (<c>x++</c> or <c>x--</c>).
    /// </summary>
    /// <param name="postfix">The postfix increment expression AST node.</param>
    /// <returns>The original value before incrementing/decrementing.</returns>
    /// <remarks>
    /// Postfix operators modify the value but return the old value.
    /// Supports variables, property access, and index access as operands.
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/Increment">MDN Increment Operator</seealso>
    private RuntimeValue EvaluatePostfixIncrement(Expr.PostfixIncrement postfix)
    {
        double delta = postfix.Operator.Type == TokenType.PLUS_PLUS ? 1 : -1;
        return EvaluateIncrement(postfix.Operand, delta, returnOld: true);
    }

    /// <summary>
    /// Evaluates the plus operator, handling both addition and string concatenation.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The sum if both are numbers; otherwise the concatenated string.</returns>
    /// <remarks>
    /// If either operand is a string, performs string concatenation.
    /// Otherwise performs numeric addition.
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/Addition">MDN Addition Operator</seealso>
    private object EvaluatePlus(object? left, object? right)
    {
        if (left is double l && right is double r) return l + r;
        if (left is string || right is string)
        {
            // Avoid calling Stringify on values that are already strings
            return string.Concat(
                left as string ?? Stringify(left),
                right as string ?? Stringify(right));
        }
        throw new InterpreterException("Operands must be two numbers or two strings.");
    }

    /// <summary>
    /// Evaluates a unary operator expression.
    /// </summary>
    /// <param name="unary">The unary expression AST node.</param>
    /// <returns>The result of applying the unary operator.</returns>
    /// <remarks>
    /// Supports logical NOT (<c>!</c>), numeric negation (<c>-</c>),
    /// typeof operator, and bitwise NOT (<c>~</c>).
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/typeof">MDN typeof</seealso>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/Logical_NOT">MDN Logical NOT</seealso>
    private RuntimeValue EvaluateUnary(Expr.Unary unary)
    {
        // typeof never throws on undeclared variables - it returns "undefined"
        if (unary.Operator.Type == TokenType.TYPEOF && unary.Right is Expr.Variable)
        {
            RuntimeValue right;
            try { right = EvaluateRV(unary.Right); }
            catch (InterpreterException) { right = RuntimeValue.Undefined; }
            return RuntimeValue.FromString(right.TypeofString());
        }

        // Fast path for common unary operations on RuntimeValue
        var rv = EvaluateRV(unary.Right);
        return EvaluateUnaryOperationRV(unary.Operator, rv);
    }

    /// <summary>
    /// RuntimeValue-native unary operation — avoids boxing for all cases including BigInt.
    /// </summary>
    private RuntimeValue EvaluateUnaryOperationRV(Token op, RuntimeValue rv)
    {
        switch (op.Type)
        {
            case TokenType.BANG:
                return RuntimeValue.FromBoolean(!rv.IsTruthy());
            case TokenType.PLUS when rv.IsNumber:
                return rv;
            case TokenType.PLUS when rv.IsBigInt:
                // Unary `+` on BigInt throws TypeError in JS.
                throw new InterpreterException("TypeError: Cannot convert a BigInt value to a number");
            case TokenType.PLUS:
                return RuntimeValue.FromNumber(CoerceToNumber(rv));
            case TokenType.MINUS when rv.IsNumber:
                return RuntimeValue.FromNumber(-rv.AsNumber());
            case TokenType.MINUS when rv.IsBigInt:
                return RuntimeValue.FromBigInt(new SharpTSBigInt(-rv.AsBigInt().Value));
            case TokenType.MINUS:
                return RuntimeValue.FromNumber(-CoerceToNumber(rv));
            case TokenType.TYPEOF:
                return RuntimeValue.FromString(rv.TypeofString());
            case TokenType.VOID:
                return RuntimeValue.Undefined;
            case TokenType.TILDE when rv.IsNumber:
                return RuntimeValue.FromNumber(~(int)rv.AsNumber());
            case TokenType.TILDE when rv.IsBigInt:
                return RuntimeValue.FromBigInt(new SharpTSBigInt(~rv.AsBigInt().Value));
            case TokenType.TILDE:
                return RuntimeValue.FromNumber(~(int)CoerceToNumber(rv));
            default:
                return RuntimeValue.Undefined;
        }
    }

    /// <summary>
    /// JS ToNumber coercion for a RuntimeValue. Mirrors the ECMAScript
    /// abstract operation: null→0, undefined→NaN, true→1, false→0,
    /// strings parsed (empty/whitespace→0, otherwise NaN on parse failure).
    /// Numbers pass through unchanged.
    /// </summary>
    private static double CoerceToNumber(RuntimeValue rv)
    {
        if (rv.IsNumber) return rv.AsNumber();
        if (rv.IsBoolean) return rv.AsBoolean() ? 1.0 : 0.0;
        if (rv.Kind == ValueKind.Null) return 0.0;
        if (rv.Kind == ValueKind.Undefined) return double.NaN;
        if (rv.IsString)
        {
            var s = rv.AsString().Trim();
            if (s.Length == 0) return 0.0;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d;
            return double.NaN;
        }
        return double.NaN;
    }

    /// <summary>
    /// Evaluates the delete operator.
    /// </summary>
    /// <param name="delete">The delete expression AST node.</param>
    /// <returns>true if deletion succeeded, false otherwise.</returns>
    /// <remarks>
    /// - delete obj.prop: removes property from object, returns true
    /// - delete obj[key]: removes computed property from object, returns true
    /// - delete variable: throws SyntaxError in strict mode, returns false in sloppy mode
    /// - delete on non-existent property: returns true
    /// </remarks>
    private RuntimeValue EvaluateDelete(Expr.Delete delete)
    {
        bool strictMode = _environment.IsStrictMode;

        bool result = delete.Operand switch
        {
            Expr.Get get => DeleteProperty(get, strictMode),
            Expr.GetIndex getIndex => DeleteIndexedProperty(getIndex, strictMode),
            Expr.Variable v when strictMode =>
                throw StrictModeErrors.SyntaxError($"Delete of unqualified identifier '{v.Name.Lexeme}' in strict mode"),
            Expr.Variable v =>
                SloppyModeWarnings.WarnAndReturn(false, "delete variable", $"delete {v.Name.Lexeme} returns false in sloppy mode"),
            _ => true // Deleting other expressions returns true but does nothing
        };
        return RuntimeValue.FromBoolean(result);
    }

    /// <summary>
    /// Async version of EvaluateDelete.
    /// </summary>
    private async Task<object> EvaluateDeleteAsync(Expr.Delete delete)
    {
        bool strictMode = _environment.IsStrictMode;

        return delete.Operand switch
        {
            Expr.Get get => await DeletePropertyAsync(get, strictMode),
            Expr.GetIndex getIndex => await DeleteIndexedPropertyAsync(getIndex, strictMode),
            Expr.Variable v when strictMode =>
                throw StrictModeErrors.SyntaxError($"Delete of unqualified identifier '{v.Name.Lexeme}' in strict mode"),
            Expr.Variable v =>
                SloppyModeWarnings.WarnAndReturn(false, "delete variable", $"delete {v.Name.Lexeme} returns false in sloppy mode"),
            _ => true
        };
    }

    /// <summary>
    /// Deletes a property from an object (delete obj.prop).
    /// In strict mode, throws TypeError for frozen/sealed objects.
    /// </summary>
    private bool DeleteProperty(Expr.Get get, bool strictMode)
    {
        object? obj = Evaluate(get.Object);
        string name = get.Name.Lexeme;

        if (obj is SharpTSProxy proxy)
            return proxy.TrapDeleteProperty(name, this);

        return obj switch
        {
            SharpTSObject tsObj => tsObj.DeletePropertyStrict(name, strictMode),
            SharpTSInstance tsInst => tsInst.DeleteFieldStrict(name, strictMode),
            Dictionary<string, object?> dict => dict.Remove(name),
            _ => true // Deleting non-existent property on primitive returns true
        };
    }

    /// <summary>
    /// Async version of DeleteProperty.
    /// In strict mode, throws TypeError for frozen/sealed objects.
    /// </summary>
    private async Task<bool> DeletePropertyAsync(Expr.Get get, bool strictMode)
    {
        object? obj = (await EvaluateAsync(get.Object)).ToObject();
        string name = get.Name.Lexeme;

        if (obj is SharpTSProxy proxy)
            return proxy.TrapDeleteProperty(name, this);

        return obj switch
        {
            SharpTSObject tsObj => tsObj.DeletePropertyStrict(name, strictMode),
            SharpTSInstance tsInst => tsInst.DeleteFieldStrict(name, strictMode),
            Dictionary<string, object?> dict => dict.Remove(name),
            _ => true
        };
    }

    /// <summary>
    /// Deletes a computed property from an object (delete obj[key]).
    /// In strict mode, throws TypeError for frozen/sealed objects.
    /// </summary>
    private bool DeleteIndexedProperty(Expr.GetIndex getIndex, bool strictMode)
    {
        object? obj = Evaluate(getIndex.Object);
        object? key = Evaluate(getIndex.Index);

        // Handle proxy
        if (obj is SharpTSProxy proxy)
        {
            string proxyKey = key is SharpTSSymbol ? key.ToString()! : Stringify(key);
            return proxy.TrapDeleteProperty(proxyKey, this);
        }

        // Handle symbol keys
        if (key is SharpTSSymbol symbol)
        {
            return obj switch
            {
                SharpTSObject tsObj => tsObj.DeleteBySymbolStrict(symbol, strictMode),
                SharpTSInstance tsInst => tsInst.DeleteBySymbolStrict(symbol, strictMode),
                _ => true
            };
        }

        string keyStr = Stringify(key);

        return obj switch
        {
            SharpTSObject tsObj => tsObj.DeletePropertyStrict(keyStr, strictMode),
            SharpTSInstance tsInst => tsInst.DeleteFieldStrict(keyStr, strictMode),
            Dictionary<string, object?> dict => dict.Remove(keyStr),
            _ => true
        };
    }

    /// <summary>
    /// Async version of DeleteIndexedProperty.
    /// In strict mode, throws TypeError for frozen/sealed objects.
    /// </summary>
    private async Task<bool> DeleteIndexedPropertyAsync(Expr.GetIndex getIndex, bool strictMode)
    {
        object? obj = (await EvaluateAsync(getIndex.Object)).ToObject();
        object? key = (await EvaluateAsync(getIndex.Index)).ToObject();

        // Handle proxy
        if (obj is SharpTSProxy proxy)
        {
            string proxyKey = key is SharpTSSymbol ? key.ToString()! : Stringify(key);
            return proxy.TrapDeleteProperty(proxyKey, this);
        }

        // Handle symbol keys
        if (key is SharpTSSymbol symbol)
        {
            return obj switch
            {
                SharpTSObject tsObj => tsObj.DeleteBySymbolStrict(symbol, strictMode),
                SharpTSInstance tsInst => tsInst.DeleteBySymbolStrict(symbol, strictMode),
                _ => true
            };
        }

        string keyStr = Stringify(key);

        return obj switch
        {
            SharpTSObject tsObj => tsObj.DeletePropertyStrict(keyStr, strictMode),
            SharpTSInstance tsInst => tsInst.DeleteFieldStrict(keyStr, strictMode),
            Dictionary<string, object?> dict => dict.Remove(keyStr),
            _ => true
        };
    }

    /// <summary>
    /// Core unary operation logic, shared between sync and async evaluation.
    /// </summary>
    private object? EvaluateUnaryOperation(Token op, object? right)
    {
        return op.Type switch
        {
            TokenType.BANG => !IsTruthy(right),
            TokenType.PLUS when right is SharpTSBigInt =>
                throw new InterpreterException("TypeError: Cannot convert a BigInt value to a number"),
            TokenType.PLUS when right is System.Numerics.BigInteger =>
                throw new InterpreterException("TypeError: Cannot convert a BigInt value to a number"),
            TokenType.PLUS => CoerceToNumber(RuntimeValue.FromBoxed(right)),
            TokenType.MINUS when right is SharpTSBigInt bi => new SharpTSBigInt(-bi.Value),
            TokenType.MINUS when right is System.Numerics.BigInteger biVal => new SharpTSBigInt(-biVal),
            TokenType.MINUS => -(double)right!,
            TokenType.TYPEOF => GetTypeofString(right),
            TokenType.VOID => SharpTSUndefined.Instance,
            TokenType.TILDE when right is SharpTSBigInt bi => new SharpTSBigInt(~bi.Value),
            TokenType.TILDE when right is System.Numerics.BigInteger biVal => new SharpTSBigInt(~biVal),
            TokenType.TILDE => (double)(~ToInt32(right)),
            _ => null
        };
    }

    /// <summary>
    /// Returns the typeof string for a runtime value.
    /// </summary>
    /// <param name="value">The value to get the type of.</param>
    /// <returns>The JavaScript/TypeScript type string ("undefined", "boolean", "number", "string", "function", or "object").</returns>
    /// <remarks>
    /// Maps runtime types to JavaScript typeof results. Null returns "undefined",
    /// functions and classes return "function", everything else returns "object".
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/typeof">MDN typeof</seealso>
    private string GetTypeofString(object? value) => value switch
    {
        null => "object",  // JavaScript quirk: typeof null === "object"
        SharpTSUndefined => "undefined",
        bool => "boolean",
        double => "number",
        string => "string",
        SharpTSSymbol => "symbol",
        SharpTSBigInt or System.Numerics.BigInteger => "bigint",
        SharpTSProxy proxy => proxy.IsCallable ? "function" : "object",
        SharpTSFunction or SharpTSArrowFunction or SharpTSClass or BuiltInMethod or ISharpTSCallable => "function",
        // Node/JS quirk: `typeof Buffer === 'function'` even though our Buffer
        // is a singleton namespace object. Match this explicitly so bare
        // references behave like the other global constructors.
        Runtime.Types.SharpTSBufferConstructor => "function",
        _ => "object"
    };

    /// <summary>
    /// Determines if a value is truthy in JavaScript/TypeScript semantics.
    /// </summary>
    /// <param name="obj">The value to check.</param>
    /// <returns><c>true</c> if the value is truthy; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Falsy values include null, undefined, false, 0, NaN, and "".
    /// All other values are truthy.
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Glossary/Truthy">MDN Truthy</seealso>
    private static bool IsTruthy(object? obj) => RuntimeTypes.IsTruthy(obj);
    private static bool IsTruthy(RuntimeValue rv) => rv.IsTruthy();

    /// <summary>
    /// Determines if two values are equal using loose equality (<c>==</c>).
    /// </summary>
    /// <param name="a">The first value.</param>
    /// <param name="b">The second value.</param>
    /// <returns><c>true</c> if the values are loosely equal; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Uses object equality. Both null values are equal.
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/Equality">MDN Equality</seealso>
    private bool IsEqual(object? a, object? b)
    {
        // null == null, undefined == undefined, null == undefined (loose equality)
        bool aIsNullish = a == null || a is SharpTSUndefined;
        bool bIsNullish = b == null || b is SharpTSUndefined;
        if (aIsNullish && bIsNullish) return true;
        if (aIsNullish || bIsNullish) return false;
        return a!.Equals(b);
    }

    /// <summary>
    /// Determines if two values are equal using strict equality (<c>===</c>).
    /// </summary>
    /// <param name="a">The first value.</param>
    /// <param name="b">The second value.</param>
    /// <returns><c>true</c> if the values are strictly equal (same type and value); otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Checks type equality first, then value equality. No type coercion is performed.
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/Strict_equality">MDN Strict Equality</seealso>
    private bool IsStrictEqual(object? a, object? b)
    {
        // In TypeScript/JS, === checks both value and type
        // null === null and undefined === undefined, but NOT null === undefined
        if (a == null && b == null) return true;
        if (a is SharpTSUndefined && b is SharpTSUndefined) return true;
        if (a == null || b == null || a is SharpTSUndefined || b is SharpTSUndefined) return false;
        if (a.GetType() != b.GetType()) return false;
        // ECMA-262 7.2.16 IsStrictlyEqual: NaN is never equal to anything,
        // including itself. Object.Equals defers to Double.Equals which
        // treats NaN as equal to itself; check explicitly.
        if (a is double da && b is double db && (double.IsNaN(da) || double.IsNaN(db)))
            return false;
        return a.Equals(b);
    }

    /// <summary>
    /// Evaluates the <c>in</c> operator to check property existence.
    /// </summary>
    /// <param name="left">The property name to check for.</param>
    /// <param name="right">The object to check in.</param>
    /// <returns><c>true</c> if the property exists; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Works with objects, instances, and arrays (checking index existence).
    /// </remarks>
    /// <seealso href="https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Operators/in">MDN in Operator</seealso>
    private object EvaluateIn(object? left, object? right)
    {
        // 'in' operator checks if a property exists in an object
        // Handle proxy has trap
        if (right is SharpTSProxy proxy)
        {
            string proxyKey = left is SharpTSSymbol ? left.ToString()! : (left?.ToString() ?? "");
            return proxy.TrapHas(proxyKey, this);
        }

        // Handle symbol keys specially
        if (left is SharpTSSymbol symbol)
        {
            if (right is SharpTSObject symObj)
            {
                return symObj.HasSymbolProperty(symbol);
            }
            if (right is SharpTSInstance symInst)
            {
                return symInst.HasSymbolProperty(symbol);
            }
            // Symbols can't be in arrays or other types
            if (right is SharpTSArray)
            {
                return false;
            }
            throw new InterpreterException("'in' operator requires an object on the right side.");
        }

        string key = left?.ToString() ?? "";

        if (right is SharpTSObject obj)
        {
            return obj.HasProperty(key);
        }
        if (right is SharpTSInstance instance)
        {
            return instance.HasProperty(key);
        }
        if (right is SharpTSArray arr)
        {
            // `i in arr` is false for holes per ECMA-262 HasProperty. A length-5
            // array with a[2] missing returns false for `"2" in arr` but true for
            // `"0" in arr` (present).
            if (key == "length") return true;
            if (double.TryParse(key, out double index))
            {
                int i = (int)index;
                return arr.HasIndex(i);
            }
            return arr.HasNamedProperty(key);
        }

        throw new InterpreterException("'in' operator requires an object on the right side.");
    }

    /// <summary>
    /// Converts a runtime value to its string representation.
    /// </summary>
    /// <param name="obj">The value to stringify.</param>
    /// <returns>The string representation of the value.</returns>
    internal string Stringify(object? obj)
    {
        if (obj == null) return "null";
        if (obj is SharpTSUndefined) return "undefined";
        if (obj is bool b) return b ? "true" : "false";
        if (obj is double d)
        {
            string text = d.ToString();
            if (text.EndsWith(".0"))
            {
                text = text.Substring(0, text.Length - 2);
            }
            return text;
        }

        if (obj is SharpTSArray array)
        {
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < array.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Stringify(array[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        if (obj is SharpTSObject sharpObj)
        {
            // ECMA-262 §7.1.17 ToString of an object goes through ToPrimitive,
            // which (for hint "string") tries the object's own toString
            // method first. The previous shortcut returned "[object Object]"
            // unconditionally, which silently swallowed throwing user
            // toStrings — test262 patterns like
            // `{toString: () => { throw }}` rely on the abrupt completion
            // propagating up. Only invoke the user-installed toString;
            // skip the prototype to preserve the cheap default for plain
            // records (which would otherwise spin up a call frame for
            // every Stringify).
            if (sharpObj.HasProperty("toString")
                && sharpObj.GetProperty("toString") is ISharpTSCallable userToString)
            {
                var coerced = FunctionBuiltIns.CallWithThis(this, userToString, sharpObj, []);
                if (coerced is string strRes) return strRes;
                return Stringify(coerced);
            }
            return "[object Object]";
        }

        if (obj is SharpTSInstance instance)
        {
            return "[object " + instance.GetClass().Name + "]";
        }

        return obj.ToString()!;
    }
}
