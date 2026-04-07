using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Compilation.CallHandlers;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public abstract partial class ExpressionEmitterBase
{
    /// <summary>
    /// Returns the hoisted 'this' field from the state machine builder, if available.
    /// Override in emitters that support class context (async methods, async generators).
    /// Arrow functions return null since they don't have their own 'this'.
    /// </summary>
    protected virtual FieldBuilder? GetThisField() => null;

    /// <summary>
    /// Shared call handler registry used by all emitter types.
    /// Handlers are stateless — all state comes from IEmitterContext.Context.
    /// </summary>
    protected static readonly CallHandlerRegistry _callHandlers = new();

    #region Shared Call Dispatch

    /// <summary>
    /// Shared EmitCall implementation used by all emitter types.
    /// Uses the handler chain for pattern-based dispatch, then falls through
    /// to remaining cases (super, class statics, method calls, direct function calls).
    /// </summary>
    protected virtual void EmitCall(Expr.Call c)
    {
        // CommonJS: literal require('./literal') → direct $GetExports() call.
        // Placed at the top so it works in async/generator state machine emitters too
        // (which inherit this method without overriding the prefix dispatch).
        if (TryEmitCjsRequireCall(c))
            return;

        // Handler chain covers: console methods, fetch, parseInt/parseFloat/isNaN/isFinite,
        // setTimeout/clearTimeout/setInterval/clearInterval/queueMicrotask, Symbol/BigInt/Date/Error,
        // Date.now(), Math/JSON/Object/Array/Number/Promise/Symbol statics, built-in module methods,
        // __objectRest
        if (_callHandlers.TryHandle(this, c))
            return;

        // Special case: super.method() call — emit non-virtual Call to base method
        if (c.Callee is Expr.Super superMethodExpr && superMethodExpr.Method != null && superMethodExpr.Method.Lexeme != "constructor")
        {
            if (TryEmitSuperMethodCall(superMethodExpr.Method.Lexeme, c.Arguments))
                return;
        }

        // Handle structuredClone() global function
        if (c.Callee is Expr.Variable scVar && scVar.Name.Lexeme == "structuredClone")
        {
            EmitStructuredClone(c.Arguments);
            return;
        }

        // Handle fetch() with type assertions/groupings unwrapped
        if (c.Callee is not Expr.Variable)
        {
            Expr fetchCallee = c.Callee;
            bool unwrapped = true;
            while (unwrapped)
            {
                unwrapped = false;
                if (fetchCallee is Expr.TypeAssertion ta2) { fetchCallee = ta2.Expression; unwrapped = true; }
                if (fetchCallee is Expr.Grouping g2) { fetchCallee = g2.Expression; unwrapped = true; }
            }
            if (fetchCallee is Expr.Variable fetchVar && fetchVar.Name.Lexeme == "fetch")
            {
                EmitFetchCall(c.Arguments);
                return;
            }
        }

        // Handle Expr.Get callee patterns (module.promises, class statics, instance methods)
        if (c.Callee is Expr.Get methodGet)
        {
            if (TryEmitGetCalleeViaBaseClass(c, methodGet))
                return;

            // For state machine emitters: fall through to function value call
            // ILEmitter overrides EmitCall and routes to EmitMethodCall instead
        }

        // Check if it's a built-in module method binding (e.g., import { readFile } from 'fs/promises')
        if (c.Callee is Expr.Variable builtInVar &&
            Ctx.BuiltInModuleMethodBindings?.TryGetValue(builtInVar.Name.Lexeme, out var binding) == true)
        {
            var builtInEmitter = Ctx.BuiltInModuleEmitterRegistry?.GetEmitter(binding.ModuleName);
            if (builtInEmitter != null && builtInEmitter.TryEmitMethodCall(this, binding.MethodName, c.Arguments))
            {
                SetStackUnknown();
                return;
            }
        }

        // Direct function call with full support: generics, rest params, overloads, spreads
        if (c.Callee is Expr.Variable funcVar)
        {
            string resolvedFuncName = Ctx.ResolveFunctionName(funcVar.Name.Lexeme);

            // Imported functions must go through TopLevelStaticVars (cross-module tokens don't work)
            bool isImportedFunction = Ctx.TopLevelStaticVars?.ContainsKey(funcVar.Name.Lexeme) == true;

            if (!isImportedFunction && Ctx.Functions.TryGetValue(resolvedFuncName, out var methodBuilder))
            {
                MethodInfo targetMethod = methodBuilder;

                // Generic function instantiation
                if (Ctx.IsGenericFunction?.TryGetValue(resolvedFuncName, out var isGeneric) == true && isGeneric)
                {
                    if (c.TypeArgs != null && c.TypeArgs.Count > 0)
                    {
                        Type[] typeArgs = c.TypeArgs.Select(ResolveTypeArg).ToArray();
                        targetMethod = methodBuilder.MakeGenericMethod(typeArgs);
                    }
                    else
                    {
                        var genericParams = Ctx.FunctionGenericParams![resolvedFuncName];
                        Type[] inferredArgs = new Type[genericParams.Length];
                        for (int i = 0; i < genericParams.Length; i++)
                        {
                            var baseConstraint = genericParams[i].BaseType;
                            inferredArgs[i] = (baseConstraint != null && !Types.IsObject(baseConstraint))
                                ? baseConstraint
                                : Types.Object;
                        }
                        targetMethod = methodBuilder.MakeGenericMethod(inferredArgs);
                    }
                }

                var paramCount = targetMethod.GetParameters().Length;

                // Rest parameter handling
                (int RestParamIndex, int RegularParamCount) restInfo = default;
                bool hasRestParam = Ctx.FunctionRestParams?.TryGetValue(resolvedFuncName, out restInfo) == true;

                if (hasRestParam)
                {
                    EmitRestParameterCall(c.Arguments, restInfo.RegularParamCount);
                }
                else
                {
                    // Overload resolution: find matching overload for argument count
                    if (c.Arguments.Count < paramCount &&
                        Ctx.FunctionOverloads != null &&
                        Ctx.FunctionOverloads.TryGetValue(resolvedFuncName, out var overloads))
                    {
                        var matchingOverload = overloads.FirstOrDefault(o =>
                            o.GetParameters().Length == c.Arguments.Count);
                        if (matchingOverload != null)
                        {
                            targetMethod = matchingOverload;
                            paramCount = c.Arguments.Count;
                        }
                    }

                    // Store each converted arg to a temp for await-safety
                    // (async emitters may yield during EmitExpression)
                    var targetParams = targetMethod.GetParameters();
                    List<(LocalBuilder Local, Type ParamType)> callArgTemps = [];

                    for (int i = 0; i < c.Arguments.Count; i++)
                    {
                        var arg = c.Arguments[i];
                        Type paramType = i < targetParams.Length ? targetParams[i].ParameterType : Types.Object;
                        if (arg is Expr.Spread spread)
                        {
                            EmitExpression(spread.Expression);
                            EnsureBoxed();
                            paramType = Types.Object;
                        }
                        else
                        {
                            EmitExpression(arg);
                            if (i < targetParams.Length)
                                EmitConversionForParameter(arg, targetParams[i].ParameterType);
                            else
                                EnsureBoxed();
                        }
                        var temp = IL.DeclareLocal(paramType);
                        IL.Emit(OpCodes.Stloc, temp);
                        callArgTemps.Add((temp, paramType));
                    }

                    for (int i = c.Arguments.Count; i < paramCount; i++)
                    {
                        EmitDefaultForType(targetParams[i].ParameterType);
                        var temp = IL.DeclareLocal(targetParams[i].ParameterType);
                        IL.Emit(OpCodes.Stloc, temp);
                        callArgTemps.Add((temp, targetParams[i].ParameterType));
                    }

                    // Load all args back onto stack for the call
                    foreach (var (local, _) in callArgTemps)
                        IL.Emit(OpCodes.Ldloc, local);
                }

                IL.Emit(OpCodes.Call, targetMethod);
                BoxReturnValueIfNeeded(targetMethod.ReturnType);
                return;
            }
        }

        // Inner function direct call (recursive self-calls and sibling inner function calls)
        if (c.Callee is Expr.Variable innerFuncVar &&
            Ctx.InnerFunctionMethodsByName?.TryGetValue(innerFuncVar.Name.Lexeme, out var innerMethod) == true)
        {
            EmitInnerFunctionDirectCall(innerFuncVar.Name.Lexeme, innerMethod, c.Arguments);
            return;
        }

        // Function value call with spread support
        EmitFunctionValueCall(c);
    }

    /// <summary>
    /// Emits arguments for a function call with rest parameter, handling spreads.
    /// Emits regular args first, then builds an array for the rest parameter.
    /// </summary>
    private void EmitRestParameterCall(List<Expr> arguments, int regularCount)
    {
        bool hasSpreads = arguments.Any(a => a is Expr.Spread);

        // Emit regular arguments (before rest param)
        for (int i = 0; i < Math.Min(regularCount, arguments.Count); i++)
        {
            if (arguments[i] is Expr.Spread spread)
            {
                EmitExpression(spread.Expression);
                EnsureBoxed();
            }
            else
            {
                EmitExpression(arguments[i]);
                EnsureBoxed();
            }
        }

        // Pad regular args with nulls if needed
        for (int i = arguments.Count; i < regularCount; i++)
            IL.Emit(OpCodes.Ldnull);

        // Build rest parameter array from remaining arguments
        int restArgsCount = Math.Max(0, arguments.Count - regularCount);
        if (hasSpreads && restArgsCount > 0)
        {
            EmitSpreadArray(arguments, regularCount, restArgsCount);
            EmitExpandCallArgs();
            IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateArray);
        }
        else if (restArgsCount > 0)
        {
            IL.Emit(OpCodes.Ldc_I4, restArgsCount);
            IL.Emit(OpCodes.Newarr, Types.Object);
            for (int i = 0; i < restArgsCount; i++)
            {
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldc_I4, i);
                EmitExpression(arguments[regularCount + i]);
                EnsureBoxed();
                IL.Emit(OpCodes.Stelem_Ref);
            }
            IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateArray);
        }
        else
        {
            // Empty rest array
            IL.Emit(OpCodes.Ldc_I4_0);
            IL.Emit(OpCodes.Newarr, Types.Object);
            IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateArray);
        }
    }

    /// <summary>
    /// Emits an args array and isSpread bool array for arguments starting at offset.
    /// Leaves both arrays on the stack (args array first, then isSpread array on top).
    /// </summary>
    private void EmitSpreadArray(List<Expr> arguments, int offset, int count)
    {
        IL.Emit(OpCodes.Ldc_I4, count);
        IL.Emit(OpCodes.Newarr, Types.Object);
        for (int i = 0; i < count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            var arg = arguments[offset + i];
            if (arg is Expr.Spread spread)
            {
                EmitExpression(spread.Expression);
                EnsureBoxed();
            }
            else
            {
                EmitExpression(arg);
                EnsureBoxed();
            }
            IL.Emit(OpCodes.Stelem_Ref);
        }

        // isSpread bool array
        IL.Emit(OpCodes.Ldc_I4, count);
        IL.Emit(OpCodes.Newarr, Types.Boolean);
        for (int i = 0; i < count; i++)
        {
            if (arguments[offset + i] is Expr.Spread)
            {
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldc_I4, i);
                IL.Emit(OpCodes.Ldc_I4_1);
                IL.Emit(OpCodes.Stelem_I1);
            }
        }
    }

    /// <summary>
    /// Emits the ExpandCallArgs call with Symbol.iterator and runtime type arguments.
    /// Expects args array and isSpread array on the stack.
    /// </summary>
    private void EmitExpandCallArgs()
    {
        IL.Emit(OpCodes.Ldsfld, Ctx.Runtime!.SymbolIterator);
        IL.Emit(OpCodes.Ldtoken, Ctx.Runtime!.RuntimeType);
        IL.Emit(OpCodes.Call, Types.TypeGetTypeFromHandle);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.ExpandCallArgs);
    }

    /// <summary>
    /// Tries to handle an Expr.Get callee via shared dispatch patterns:
    /// module.promises, class statics, Promise.then/catch/finally, type-first dispatch,
    /// fallback string/array methods, ambiguous methods.
    /// Returns true if handled, false to fall through to emitter-specific instance method dispatch.
    /// ILEmitter calls this before its EmitMethodCall; state machine emitters use it as their
    /// only Expr.Get dispatch (with InvokeMethodValue as the final fallback).
    /// </summary>
    protected bool TryEmitGetCalleeViaBaseClass(Expr.Call c, Expr.Get methodGet)
    {
        // Handler chain: static types, Date.now, built-in modules, process streams,
        // globalThis chaining, imported/class-expr/this statics, etc.
        if (_callHandlers.TryHandle(this, c))
            return true;

        // module.promises.methodName() (fs.promises, dns.promises, stream.promises)
        if (methodGet.Object is Expr.Get promisesGet &&
            promisesGet.Name.Lexeme == "promises" &&
            promisesGet.Object is Expr.Variable promisesModuleVar &&
            Ctx.BuiltInModuleNamespaces != null &&
            Ctx.BuiltInModuleNamespaces.TryGetValue(promisesModuleVar.Name.Lexeme, out var promisesModuleName) &&
            Ctx.BuiltInModuleEmitterRegistry?.GetEmitter(promisesModuleName + "/promises") is { } promisesEmitter)
        {
            if (promisesEmitter.TryEmitMethodCall(this, methodGet.Name.Lexeme, c.Arguments))
            {
                SetStackUnknown();
                return true;
            }
        }

        // Class.staticMethod() with generic class support
        if (methodGet.Object is Expr.Variable classVar &&
            Ctx.Classes.TryGetValue(Ctx.ResolveClassName(classVar.Name.Lexeme), out var classBuilder))
        {
            string resolvedClassName = Ctx.ResolveClassName(classVar.Name.Lexeme);
            if (Ctx.ClassRegistry!.TryGetCallableStaticMethod(resolvedClassName, methodGet.Name.Lexeme, classBuilder, out var callableMethod))
            {
                var staticMethodParams = callableMethod!.GetParameters();
                var paramCount = staticMethodParams.Length;

                for (int i = 0; i < c.Arguments.Count; i++)
                {
                    EmitExpression(c.Arguments[i]);
                    if (i < staticMethodParams.Length)
                        EmitConversionForParameter(c.Arguments[i], staticMethodParams[i].ParameterType);
                    else
                        EnsureBoxed();
                }

                for (int i = c.Arguments.Count; i < paramCount; i++)
                    EmitDefaultForType(staticMethodParams[i].ParameterType);

                IL.Emit(OpCodes.Call, callableMethod);
                SetStackUnknown();
                return true;
            }
        }

        // Promise instance methods: promise.then/catch/finally
        string methodName = methodGet.Name.Lexeme;
        if (methodName is "then" or "catch" or "finally")
        {
            EmitPromiseInstanceMethodCall(methodGet.Object, methodName, c.Arguments);
            return true;
        }

        // Direct dispatch for known class instance methods
        if (TryEmitDirectMethodCall(methodGet.Object, methodName, c.Arguments))
            return true;

        // Type-first dispatch via TypeEmitterRegistry
        var objType = Ctx.TypeMap?.Get(methodGet.Object);
        if (objType != null && Ctx.TypeEmitterRegistry != null)
        {
            var strategy = Ctx.TypeEmitterRegistry.GetStrategy(objType);
            if (strategy != null && strategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                return true;

            // Union type handling
            if (objType is TypeSystem.TypeInfo.Union union)
            {
                bool hasBufferMember = union.Types.Any(t => t is TypeSystem.TypeInfo.Buffer);
                bool hasStringMember = union.Types.Any(t => t is TypeSystem.TypeInfo.String or TypeSystem.TypeInfo.StringLiteral);
                bool hasArrayMember = union.Types.Any(t => t is TypeSystem.TypeInfo.Array);

                bool isAmbiguousMethod = methodName is "slice" or "concat" or "includes" or "indexOf" or "toString";
                int typesWithMethod = 0;
                if (hasBufferMember && isAmbiguousMethod) typesWithMethod++;
                if (hasStringMember && isAmbiguousMethod) typesWithMethod++;
                if (hasArrayMember && isAmbiguousMethod) typesWithMethod++;

                if (typesWithMethod <= 1)
                {
                    if (hasBufferMember)
                    {
                        var bufferStrategy = Ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.Buffer());
                        if (bufferStrategy != null && bufferStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                            return true;
                    }
                    if (hasStringMember)
                    {
                        var stringStrategy = Ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.String());
                        if (stringStrategy != null && stringStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                            return true;
                    }
                    if (hasArrayMember)
                    {
                        var arrayStrategy = Ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.Array(new TypeSystem.TypeInfo.Any()));
                        if (arrayStrategy != null && arrayStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                            return true;
                    }
                }
            }
        }

        // Fallback: method name-based dispatch for known built-in methods
        if (Ctx.TypeEmitterRegistry != null)
        {
            if (methodName is "charAt" or "substring" or "toUpperCase" or "toLowerCase"
                or "trim" or "replace" or "split" or "startsWith" or "endsWith"
                or "repeat" or "padStart" or "padEnd" or "charCodeAt" or "lastIndexOf"
                or "trimStart" or "trimEnd" or "replaceAll" or "at" or "match" or "search")
            {
                var stringStrategy = Ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.String());
                if (stringStrategy != null && stringStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                    return true;
            }

            if (methodName is "pop" or "shift" or "unshift" or "map" or "filter"
                or "push" or "find" or "findIndex" or "some" or "every" or "reduce" or "join"
                or "reverse" or "fill")
            {
                var arrayStrategy = Ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.Array(new TypeSystem.TypeInfo.Any()));
                if (arrayStrategy != null && arrayStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                    return true;
            }
        }

        // Ambiguous methods (slice, concat, includes, indexOf)
        if (methodName is "slice" or "concat" or "includes" or "indexOf")
        {
            EmitAmbiguousMethodCall(methodGet.Object, methodName, c.Arguments);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Emits a function value call with full spread argument support.
    /// Uses temp locals for await-safety (async emitters may yield during EmitExpression).
    /// </summary>
    private void EmitFunctionValueCall(Expr.Call c)
    {
        // Store callee in temp (await-safe)
        EmitExpression(c.Callee);
        EnsureBoxed();
        var calleeLocal = IL.DeclareLocal(Types.Object);
        IL.Emit(OpCodes.Stloc, calleeLocal);

        // Evaluate all arguments into temps first (await-safe for async emitters)
        List<LocalBuilder> argTemps = [];
        List<bool> isSpread = [];
        foreach (var arg in c.Arguments)
        {
            if (arg is Expr.Spread spread)
            {
                EmitExpression(spread.Expression);
                EnsureBoxed();
                isSpread.Add(true);
            }
            else
            {
                EmitExpression(arg);
                EnsureBoxed();
                isSpread.Add(false);
            }
            var temp = IL.DeclareLocal(Types.Object);
            IL.Emit(OpCodes.Stloc, temp);
            argTemps.Add(temp);
        }

        bool hasSpreads = isSpread.Any(s => s);

        if (!hasSpreads)
        {
            // Simple case: build args array from temps
            IL.Emit(OpCodes.Ldc_I4, argTemps.Count);
            IL.Emit(OpCodes.Newarr, Types.Object);
            for (int i = 0; i < argTemps.Count; i++)
            {
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldc_I4, i);
                IL.Emit(OpCodes.Ldloc, argTemps[i]);
                IL.Emit(OpCodes.Stelem_Ref);
            }
        }
        else
        {
            // Spread case: build args array from temps
            IL.Emit(OpCodes.Ldc_I4, argTemps.Count);
            IL.Emit(OpCodes.Newarr, Types.Object);
            for (int i = 0; i < argTemps.Count; i++)
            {
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldc_I4, i);
                IL.Emit(OpCodes.Ldloc, argTemps[i]);
                IL.Emit(OpCodes.Stelem_Ref);
            }

            // Build isSpread bool array
            IL.Emit(OpCodes.Ldc_I4, argTemps.Count);
            IL.Emit(OpCodes.Newarr, Types.Boolean);
            for (int i = 0; i < argTemps.Count; i++)
            {
                if (isSpread[i])
                {
                    IL.Emit(OpCodes.Dup);
                    IL.Emit(OpCodes.Ldc_I4, i);
                    IL.Emit(OpCodes.Ldc_I4_1);
                    IL.Emit(OpCodes.Stelem_I1);
                }
            }

            // ExpandCallArgs
            EmitExpandCallArgs();
        }

        // Call InvokeMethodValue(receiver=null, function, args)
        var argsLocal = IL.DeclareLocal(Types.ObjectArray);
        IL.Emit(OpCodes.Stloc, argsLocal);
        IL.Emit(OpCodes.Ldnull);
        IL.Emit(OpCodes.Ldloc, calleeLocal);
        IL.Emit(OpCodes.Ldloc, argsLocal);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.InvokeMethodValue);
        SetStackUnknown();
    }

    #endregion

    #region Call Helpers

    protected bool TryEmitDirectMethodCall(Expr receiver, string methodName, List<Expr> arguments)
    {
        var receiverType = Ctx.TypeMap?.Get(receiver);
        if (receiverType is not TypeSystem.TypeInfo.Instance instance)
            return false;

        string? simpleClassName = instance.ClassType switch
        {
            TypeSystem.TypeInfo.Class classType => classType.Name,
            _ => null
        };
        if (simpleClassName == null)
            return false;

        string className = Ctx.ResolveClassName(simpleClassName);
        var methodBuilder = Ctx.ResolveInstanceMethod(className, methodName);
        if (methodBuilder == null)
            return false;

        if (!Ctx.Classes.TryGetValue(className, out var classType2))
            return false;

        var methodParams = methodBuilder.GetParameters();
        int expectedParamCount = methodParams.Length;

        List<LocalBuilder> argTemps = [];
        foreach (var arg in arguments)
        {
            EmitExpression(arg);
            EnsureBoxed();
            var temp = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, temp);
            argTemps.Add(temp);
        }

        EmitExpression(receiver);
        EnsureBoxed();
        IL.Emit(OpCodes.Castclass, classType2);

        for (int i = 0; i < argTemps.Count; i++)
        {
            IL.Emit(OpCodes.Ldloc, argTemps[i]);
            if (i < methodParams.Length)
            {
                var targetType = methodParams[i].ParameterType;
                if (targetType.IsValueType && targetType != typeof(object))
                {
                    IL.Emit(OpCodes.Unbox_Any, targetType);
                }
            }
        }

        for (int i = arguments.Count; i < expectedParamCount; i++)
        {
            EmitDefaultForType(methodParams[i].ParameterType);
        }

        IL.Emit(OpCodes.Callvirt, methodBuilder);
        SetStackUnknown();
        return true;
    }

    /// <summary>
    /// Emits a non-virtual call to a base class method for super.method().
    /// Returns false by default (arrow functions don't support super).
    /// Override in emitters that have access to a hoisted 'this' field.
    /// </summary>
    protected virtual bool TryEmitSuperMethodCall(string methodName, List<Expr> arguments)
    {
        var thisField = GetThisField();

        string? superclassName = Ctx.CurrentSuperclassName;

        if (superclassName == null && Ctx.CurrentClassName != null)
        {
            superclassName = Ctx.ClassRegistry?.GetSuperclass(
                Ctx.CurrentClassBuilder?.FullName ?? Ctx.CurrentClassName)
                ?? Ctx.ClassRegistry?.GetSuperclass(Ctx.CurrentClassName);
        }

        if (superclassName == null && Ctx.CurrentClassExpr != null)
        {
            Ctx.ClassExprSuperclass?.TryGetValue(Ctx.CurrentClassExpr, out superclassName);
        }

        if (superclassName == null)
            return false;

        string resolvedSuperName = Ctx.ResolveClassName(superclassName);
        var methodBuilder = Ctx.ResolveInstanceMethod(resolvedSuperName, methodName);
        if (methodBuilder == null)
            return false;

        var methodParams = methodBuilder.GetParameters();

        List<LocalBuilder> argTemps = [];
        foreach (var arg in arguments)
        {
            EmitExpression(arg);
            EnsureBoxed();
            var temp = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, temp);
            argTemps.Add(temp);
        }

        // Load hoisted 'this' from state machine
        if (thisField != null)
        {
            IL.Emit(OpCodes.Ldarg_0);
            IL.Emit(OpCodes.Ldfld, thisField);
        }
        else
        {
            IL.Emit(OpCodes.Ldnull);
        }

        for (int i = 0; i < argTemps.Count; i++)
        {
            IL.Emit(OpCodes.Ldloc, argTemps[i]);
            if (i < methodParams.Length)
            {
                var targetType = methodParams[i].ParameterType;
                if (targetType.IsValueType && targetType != typeof(object))
                {
                    IL.Emit(OpCodes.Unbox_Any, targetType);
                }
            }
        }

        for (int i = arguments.Count; i < methodParams.Length; i++)
        {
            EmitDefaultForType(methodParams[i].ParameterType);
        }

        IL.Emit(OpCodes.Call, methodBuilder);
        SetStackUnknown();
        return true;
    }

    #region Global Function Helpers

    protected void EmitGlobalParseInt(List<Expr> arguments)
    {
        if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
        if (arguments.Count > 1) { EmitExpression(arguments[1]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldc_I4, 10); IL.Emit(OpCodes.Box, Types.Int32); }
        IL.Emit(OpCodes.Call, Ctx.Runtime!.NumberParseInt);
        IL.Emit(OpCodes.Box, Types.Double);
    }

    protected void EmitGlobalParseFloat(List<Expr> arguments)
    {
        if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
        IL.Emit(OpCodes.Call, Ctx.Runtime!.NumberParseFloat);
        IL.Emit(OpCodes.Box, Types.Double);
    }

    protected void EmitGlobalIsNaN(List<Expr> arguments)
    {
        if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
        IL.Emit(OpCodes.Call, Ctx.Runtime!.GlobalIsNaN);
        IL.Emit(OpCodes.Box, Types.Boolean);
    }

    protected void EmitGlobalIsFinite(List<Expr> arguments)
    {
        if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
        IL.Emit(OpCodes.Call, Ctx.Runtime!.GlobalIsFinite);
        IL.Emit(OpCodes.Box, Types.Boolean);
    }

    protected void EmitStructuredClone(List<Expr> arguments)
    {
        if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
        if (arguments.Count > 1) { EmitExpression(arguments[1]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
        IL.Emit(OpCodes.Call, Ctx.Runtime!.StructuredCloneClone);
    }

    protected void EmitSetTimeout(List<Expr> arguments)
    {
        if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
        if (arguments.Count > 1) { EmitExpression(arguments[1]); if (arguments[1] is not Expr.Literal { Value: double }) IL.Emit(OpCodes.Unbox_Any, Types.Double); } else { IL.Emit(OpCodes.Ldc_R8, 0.0); }
        EmitTimerArgsArray(arguments, 2);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.SetTimeout);
        SetStackUnknown();
    }

    protected void EmitClearTimeout(List<Expr> arguments)
    {
        if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
        IL.Emit(OpCodes.Call, Ctx.Runtime!.ClearTimeout);
        IL.Emit(OpCodes.Ldnull);
    }

    protected void EmitSetInterval(List<Expr> arguments)
    {
        if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
        if (arguments.Count > 1) { EmitExpression(arguments[1]); if (arguments[1] is not Expr.Literal { Value: double }) IL.Emit(OpCodes.Unbox_Any, Types.Double); } else { IL.Emit(OpCodes.Ldc_R8, 0.0); }
        EmitTimerArgsArray(arguments, 2);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.SetInterval);
        SetStackUnknown();
    }

    protected void EmitClearInterval(List<Expr> arguments)
    {
        if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
        IL.Emit(OpCodes.Call, Ctx.Runtime!.ClearInterval);
        IL.Emit(OpCodes.Ldnull);
    }

    protected void EmitQueueMicrotask(List<Expr> arguments)
    {
        if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
        IL.Emit(OpCodes.Call, Ctx.Runtime!.QueueMicrotask);
        IL.Emit(OpCodes.Ldsfld, Ctx.Runtime!.UndefinedInstance);
    }

    private void EmitTimerArgsArray(List<Expr> arguments, int startIndex)
    {
        if (arguments.Count > startIndex)
        {
            IL.Emit(OpCodes.Ldc_I4, arguments.Count - startIndex);
            IL.Emit(OpCodes.Newarr, Types.Object);
            for (int i = startIndex; i < arguments.Count; i++)
            {
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldc_I4, i - startIndex);
                EmitExpression(arguments[i]);
                EnsureBoxed();
                IL.Emit(OpCodes.Stelem_Ref);
            }
        }
        else
        {
            IL.Emit(OpCodes.Ldc_I4_0);
            IL.Emit(OpCodes.Newarr, Types.Object);
        }
    }

    protected void EmitSymbolCall(List<Expr> arguments)
    {
        if (arguments.Count == 0) { IL.Emit(OpCodes.Ldnull); }
        else { EmitExpression(arguments[0]); IL.Emit(OpCodes.Call, Ctx.Runtime!.Stringify); }
        IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSSymbolCtor);
    }

    protected void EmitBigIntCall(List<Expr> arguments)
    {
        if (arguments.Count != 1)
            throw new Diagnostics.Exceptions.CompileException("BigInt() requires exactly one argument.");
        EmitExpression(arguments[0]);
        EnsureBoxed();
        IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateBigInt);
        SetStackUnknown();
    }

    protected void EmitErrorCall(string errorTypeName, List<Expr> arguments)
    {
        IL.Emit(OpCodes.Ldstr, errorTypeName);
        IL.Emit(OpCodes.Ldc_I4, arguments.Count);
        IL.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < arguments.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            EmitExpression(arguments[i]);
            EnsureBoxed();
            IL.Emit(OpCodes.Stelem_Ref);
        }
        IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateError);
    }

    #endregion

    protected void EmitFetchCall(List<Expr> arguments)
    {
        if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
        if (arguments.Count > 1) { EmitExpression(arguments[1]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
        IL.Emit(OpCodes.Call, Ctx.Runtime!.Fetch);
        SetStackUnknown();
    }

    protected void EmitPromiseInstanceMethodCall(Expr promise, string methodName, List<Expr> arguments)
    {
        EmitExpression(promise);
        EnsureBoxed();
        IL.Emit(OpCodes.Castclass, typeof(Task<object?>));

        switch (methodName)
        {
            case "then":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
                if (arguments.Count > 1) { EmitExpression(arguments[1]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.PromiseThen);
                break;
            case "catch":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.PromiseCatch);
                break;
            case "finally":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.PromiseFinally);
                break;
        }
        SetStackUnknown();
    }

    protected void EmitAmbiguousMethodCall(Expr obj, string methodName, List<Expr> arguments)
    {
        EmitExpression(obj);
        EnsureBoxed();
        var objLocal = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, objLocal);

        var isStringLabel = IL.DefineLabel();
        var isListLabel = IL.DefineLabel();
        var doneLabel = IL.DefineLabel();

        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Isinst, typeof(string));
        IL.Emit(OpCodes.Brtrue, isStringLabel);
        IL.Emit(OpCodes.Br, isListLabel);

        // String path
        IL.MarkLabel(isStringLabel);
        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Castclass, typeof(string));
        switch (methodName)
        {
            case "includes":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); IL.Emit(OpCodes.Castclass, typeof(string)); }
                else { IL.Emit(OpCodes.Ldstr, ""); }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.StringIncludes);
                IL.Emit(OpCodes.Box, typeof(bool));
                break;
            case "indexOf":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); IL.Emit(OpCodes.Castclass, typeof(string)); }
                else { IL.Emit(OpCodes.Ldstr, ""); }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.StringIndexOf);
                IL.Emit(OpCodes.Box, typeof(double));
                break;
            case "slice":
                IL.Emit(OpCodes.Ldc_I4, arguments.Count);
                IL.Emit(OpCodes.Ldc_I4, arguments.Count);
                IL.Emit(OpCodes.Newarr, typeof(object));
                for (int i = 0; i < arguments.Count; i++)
                {
                    IL.Emit(OpCodes.Dup); IL.Emit(OpCodes.Ldc_I4, i);
                    EmitExpression(arguments[i]); EnsureBoxed();
                    IL.Emit(OpCodes.Stelem_Ref);
                }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.StringSlice);
                break;
            case "concat":
                IL.Emit(OpCodes.Ldc_I4, arguments.Count);
                IL.Emit(OpCodes.Newarr, typeof(object));
                for (int i = 0; i < arguments.Count; i++)
                {
                    IL.Emit(OpCodes.Dup); IL.Emit(OpCodes.Ldc_I4, i);
                    EmitExpression(arguments[i]); EnsureBoxed();
                    IL.Emit(OpCodes.Stelem_Ref);
                }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.StringConcat);
                break;
        }
        IL.Emit(OpCodes.Br, doneLabel);

        // List path
        IL.MarkLabel(isListLabel);
        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Castclass, typeof(List<object>));
        switch (methodName)
        {
            case "includes":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.ArrayIncludes);
                IL.Emit(OpCodes.Box, typeof(bool));
                break;
            case "indexOf":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.ArrayIndexOf);
                IL.Emit(OpCodes.Box, typeof(double));
                break;
            case "slice":
                IL.Emit(OpCodes.Ldc_I4, arguments.Count);
                IL.Emit(OpCodes.Newarr, typeof(object));
                for (int i = 0; i < arguments.Count; i++)
                {
                    IL.Emit(OpCodes.Dup); IL.Emit(OpCodes.Ldc_I4, i);
                    EmitExpression(arguments[i]); EnsureBoxed();
                    IL.Emit(OpCodes.Stelem_Ref);
                }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.ArraySlice);
                break;
            case "concat":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { IL.Emit(OpCodes.Ldnull); }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.ArrayConcat);
                break;
        }

        IL.MarkLabel(doneLabel);
        SetStackUnknown();
    }

    protected void EmitDefaultForType(Type type)
    {
        if (type == typeof(double)) { IL.Emit(OpCodes.Ldc_R8, 0.0); }
        else if (type == typeof(int)) { IL.Emit(OpCodes.Ldc_I4_0); }
        else if (type == typeof(bool)) { IL.Emit(OpCodes.Ldc_I4_0); }
        else if (type == typeof(float)) { IL.Emit(OpCodes.Ldc_R4, 0.0f); }
        else if (type == typeof(long)) { IL.Emit(OpCodes.Ldc_I8, 0L); }
        else if (type.IsValueType)
        {
            var local = IL.DeclareLocal(type);
            IL.Emit(OpCodes.Ldloca, local);
            IL.Emit(OpCodes.Initobj, type);
            IL.Emit(OpCodes.Ldloc, local);
        }
        else { IL.Emit(OpCodes.Ldnull); }
    }

    /// <summary>
    /// Emits conversion from the current stack value to the target parameter type.
    /// Handles boxing for object, unboxing for value types, union types, and pass-through for matching types.
    /// </summary>
    protected void EmitConversionForParameter(Expr expr, Type targetType)
    {
        // If target is object, box value types
        if (targetType == typeof(object))
        {
            EnsureBoxed();
            return;
        }

        // Check if the expression produces a matching type
        if (expr is Expr.Literal lit)
        {
            if (lit.Value is double && targetType == typeof(double))
                return;
            if (lit.Value is bool && targetType == typeof(bool))
                return;
            if (lit.Value is string && targetType == typeof(string))
                return;
        }

        // If stack has a known type matching target, no conversion needed
        if (StackType == StackType.Double && targetType == typeof(double))
            return;
        if (StackType == StackType.Boolean && targetType == typeof(bool))
            return;

        // Check if target is a union type using marker interface
        if (UnionTypeHelper.IsUnionType(targetType))
        {
            // Determine source type from stack or expression
            Type? sourceType = StackType switch
            {
                StackType.Double => typeof(double),
                StackType.Boolean => typeof(bool),
                StackType.String => typeof(string),
                _ => null
            };

            if (sourceType == null && expr is Expr.Literal exprLit)
            {
                sourceType = exprLit.Value switch
                {
                    double => typeof(double),
                    string => typeof(string),
                    bool => typeof(bool),
                    _ => null
                };
            }

            if (sourceType != null && Ctx.UnionGenerator != null)
            {
                var implicitOp = Ctx.UnionGenerator.GetImplicitConversion(targetType, sourceType);
                if (implicitOp != null)
                {
                    IL.Emit(OpCodes.Call, implicitOp);
                    return;
                }
            }

            // Fallback: box and create default union
            EnsureBoxed();
            var valueLocal = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, valueLocal);
            var unionLocal = IL.DeclareLocal(targetType);
            IL.Emit(OpCodes.Ldloca, unionLocal);
            IL.Emit(OpCodes.Initobj, targetType);
            IL.Emit(OpCodes.Ldloc, unionLocal);
            return;
        }

        // If target is a value type and we have an object, unbox
        if (targetType.IsValueType && StackType != StackType.Double && StackType != StackType.Boolean)
        {
            IL.Emit(OpCodes.Unbox_Any, targetType);
        }
    }

    /// <summary>
    /// Handles return value from a direct function call — boxes value types, pushes null for void.
    /// </summary>
    protected void BoxReturnValueIfNeeded(Type returnType)
    {
        if (returnType == typeof(void))
        {
            IL.Emit(OpCodes.Ldnull);
            SetStackUnknown();
        }
        else if (Types.IsDouble(returnType))
        {
            SetStackType(StackType.Double);
        }
        else if (Types.IsBoolean(returnType))
        {
            SetStackType(StackType.Boolean);
        }
        else if (returnType.IsValueType)
        {
            IL.Emit(OpCodes.Box, returnType);
            SetStackUnknown();
        }
        else if (Types.IsString(returnType))
        {
            StackType = StackType.String;
        }
        else
        {
            SetStackUnknown();
        }
    }

    /// <summary>
    /// Emits a super() constructor call in a derived class.
    /// </summary>
    protected void EmitSuperConstructorCall(ConstructorBuilder parentCtor, List<Expr> arguments)
    {
        IL.Emit(OpCodes.Ldarg_0);

        var ctorParams = parentCtor.GetParameters();
        for (int i = 0; i < arguments.Count; i++)
        {
            EmitExpression(arguments[i]);
            if (i < ctorParams.Length)
                EmitConversionForParameter(arguments[i], ctorParams[i].ParameterType);
            else
                EnsureBoxed();
        }

        for (int i = arguments.Count; i < ctorParams.Length; i++)
            EmitDefaultForType(ctorParams[i].ParameterType);

        ConstructorInfo ctorToCall = parentCtor;
        Type? baseType = Ctx.CurrentClassBuilder?.BaseType;
        if (baseType != null && baseType.IsGenericType && baseType.IsConstructedGenericType)
            ctorToCall = TypeBuilder.GetConstructor(baseType, parentCtor);

        IL.Emit(OpCodes.Call, ctorToCall);
        IL.Emit(OpCodes.Ldnull);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits a call to an async function, synchronously awaiting the result.
    /// </summary>
    protected void EmitAsyncFunctionCall(System.Reflection.MethodInfo asyncMethod, List<Expr> arguments)
    {
        var asyncMethodParams = asyncMethod.GetParameters();
        var paramCount = asyncMethodParams.Length;

        for (int i = 0; i < arguments.Count; i++)
        {
            EmitExpression(arguments[i]);
            if (i < asyncMethodParams.Length)
                EmitConversionForParameter(arguments[i], asyncMethodParams[i].ParameterType);
            else
                EnsureBoxed();
        }

        for (int i = arguments.Count; i < paramCount; i++)
            EmitDefaultForType(asyncMethodParams[i].ParameterType);

        IL.Emit(OpCodes.Call, asyncMethod);

        Type returnType = asyncMethod.ReturnType;
        if (returnType == Types.Task || returnType.FullName == "System.Threading.Tasks.Task")
        {
            var getAwaiter = Types.GetMethod(Types.Task, "GetAwaiter");
            var awaiterType = Types.TaskAwaiter;
            var getResult = Types.GetMethod(awaiterType, "GetResult");

            var taskLocal = IL.DeclareLocal(Types.Task);
            IL.Emit(OpCodes.Stloc, taskLocal);
            IL.Emit(OpCodes.Ldloca, taskLocal);
            IL.Emit(OpCodes.Call, getAwaiter);

            var awaiterLocal = IL.DeclareLocal(awaiterType);
            IL.Emit(OpCodes.Stloc, awaiterLocal);
            IL.Emit(OpCodes.Ldloca, awaiterLocal);
            IL.Emit(OpCodes.Call, getResult);

            IL.Emit(OpCodes.Ldnull);
        }
        else if (returnType.IsGenericType && returnType.GetGenericTypeDefinition().FullName == "System.Threading.Tasks.Task`1")
        {
            var getAwaiter = returnType.GetMethod("GetAwaiter")!;
            var awaiterType = getAwaiter.ReturnType;
            var getResult = awaiterType.GetMethod("GetResult")!;

            var taskLocal = IL.DeclareLocal(returnType);
            IL.Emit(OpCodes.Stloc, taskLocal);
            IL.Emit(OpCodes.Ldloca, taskLocal);
            IL.Emit(OpCodes.Call, getAwaiter);

            var awaiterLocal = IL.DeclareLocal(awaiterType);
            IL.Emit(OpCodes.Stloc, awaiterLocal);
            IL.Emit(OpCodes.Ldloca, awaiterLocal);
            IL.Emit(OpCodes.Call, getResult);

            Type resultType = returnType.GetGenericArguments()[0];
            if (resultType.IsValueType)
                IL.Emit(OpCodes.Box, resultType);
        }
    }

    /// <summary>
    /// Emits a direct call to an inner function method (recursive or sibling).
    /// </summary>
    protected void EmitInnerFunctionDirectCall(string funcName, MethodBuilder innerMethod, List<Expr> arguments)
    {
        bool isCapturing = Ctx.InnerFunctionDisplayClassesByName?.ContainsKey(funcName) == true;

        if (isCapturing)
            IL.Emit(OpCodes.Ldarg_0);

        var parameters = innerMethod.GetParameters();
        for (int i = 0; i < arguments.Count; i++)
        {
            EmitExpression(arguments[i]);
            if (i < parameters.Length)
                EmitConversionForParameter(arguments[i], parameters[i].ParameterType);
            else
                EnsureBoxed();
        }

        for (int i = arguments.Count; i < parameters.Length; i++)
            EmitDefaultForType(parameters[i].ParameterType);

        IL.Emit(isCapturing ? OpCodes.Callvirt : OpCodes.Call, innerMethod);
        SetStackUnknown();
    }

    /// <summary>
    /// Tries to emit process.stdin.read(), process.stdout.write(), process.stderr.write().
    /// </summary>
    protected bool TryEmitProcessStreamCall(Expr.Call c)
    {
        if (c.Callee is not Expr.Get methodGet)
            return false;
        if (methodGet.Object is not Expr.Get streamGet)
            return false;
        if (streamGet.Object is not Expr.Variable processVar || processVar.Name.Lexeme != "process")
            return false;

        string streamName = streamGet.Name.Lexeme;
        string methodName = methodGet.Name.Lexeme;

        switch (streamName)
        {
            case "stdin" when methodName == "read":
                IL.Emit(OpCodes.Call, Ctx.Runtime!.StdinRead);
                SetStackUnknown();
                return true;

            case "stdout" when methodName == "write":
                if (c.Arguments.Count > 0) { EmitExpression(c.Arguments[0]); EnsureBoxed(); }
                else { IL.Emit(OpCodes.Ldstr, ""); }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.StdoutWrite);
                SetStackUnknown();
                return true;

            case "stderr" when methodName == "write":
                if (c.Arguments.Count > 0) { EmitExpression(c.Arguments[0]); EnsureBoxed(); }
                else { IL.Emit(OpCodes.Ldstr, ""); }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.StderrWrite);
                SetStackUnknown();
                return true;

            default:
                return false;
        }
    }

    #endregion
}
