using System.Reflection.Emit;
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

    #region Shared Call Dispatch

    /// <summary>
    /// Shared EmitCall implementation for async state machine emitters.
    /// Handles all 16 dispatch pathways with await-safe temp storage.
    /// ILEmitter and GeneratorMoveNextEmitter override with their own implementations.
    /// </summary>
    protected virtual void EmitCall(Expr.Call c)
    {
        // Handle console.log specially (handles both Variable and Get patterns)
        if (_helpers.TryEmitConsoleLog(c,
            arg => { EmitExpression(arg); EnsureBoxed(); },
            Ctx.Runtime!.ConsoleLog,
            Ctx.Runtime!.ConsoleLogMultiple))
        {
            return;
        }

        // Special case: super.method() call — emit non-virtual Call to base method
        if (c.Callee is Expr.Super superMethodExpr && superMethodExpr.Method != null && superMethodExpr.Method.Lexeme != "constructor")
        {
            if (TryEmitSuperMethodCall(superMethodExpr.Method.Lexeme, c.Arguments))
                return;
        }

        // Handle global functions by variable name
        if (c.Callee is Expr.Variable globalVar)
        {
            switch (globalVar.Name.Lexeme)
            {
                case "fetch":
                    EmitFetchCall(c.Arguments);
                    return;
                case "parseInt":
                    EmitGlobalParseInt(c.Arguments);
                    return;
                case "parseFloat":
                    EmitGlobalParseFloat(c.Arguments);
                    return;
                case "isNaN":
                    EmitGlobalIsNaN(c.Arguments);
                    return;
                case "isFinite":
                    EmitGlobalIsFinite(c.Arguments);
                    return;
                case "structuredClone":
                    EmitStructuredClone(c.Arguments);
                    return;
                case "setTimeout":
                    EmitSetTimeout(c.Arguments);
                    return;
                case "clearTimeout":
                    EmitClearTimeout(c.Arguments);
                    return;
                case "setInterval":
                    EmitSetInterval(c.Arguments);
                    return;
                case "clearInterval":
                    EmitClearInterval(c.Arguments);
                    return;
                case "queueMicrotask":
                    EmitQueueMicrotask(c.Arguments);
                    return;
                case "Symbol":
                    EmitSymbolCall(c.Arguments);
                    return;
                case "BigInt":
                    EmitBigIntCall(c.Arguments);
                    return;
                case "Date":
                    // Date() without 'new' returns current date as string
                    IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateDateNoArgs);
                    IL.Emit(OpCodes.Call, Ctx.Runtime!.DateToString);
                    return;
                case "Error" or "TypeError" or "RangeError" or "ReferenceError"
                    or "SyntaxError" or "URIError" or "EvalError" or "AggregateError":
                    EmitErrorCall(globalVar.Name.Lexeme, c.Arguments);
                    return;
            }
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

        // Handle Date.now() static method
        if (c.Callee is Expr.Get dateGet &&
            dateGet.Object is Expr.Variable dateStaticVar &&
            dateStaticVar.Name.Lexeme == "Date" &&
            dateGet.Name.Lexeme == "now")
        {
            IL.Emit(OpCodes.Call, Ctx.Runtime!.DateNow);
            IL.Emit(OpCodes.Box, Types.Double);
            return;
        }

        // Static type dispatch via registry (Math, JSON, Object, Array, Number, Promise, Symbol)
        if (c.Callee is Expr.Get staticGet &&
            staticGet.Object is Expr.Variable staticVar &&
            Ctx.TypeEmitterRegistry != null)
        {
            var staticStrategy = Ctx.TypeEmitterRegistry.GetStaticStrategy(staticVar.Name.Lexeme);
            if (staticStrategy != null && staticStrategy.TryEmitStaticCall(this, staticGet.Name.Lexeme, c.Arguments))
            {
                SetStackUnknown();
                return;
            }
        }

        // Built-in module method calls (fs.readFileSync, path.join, etc.)
        if (c.Callee is Expr.Get builtInGet &&
            builtInGet.Object is Expr.Variable builtInModuleVar &&
            Ctx.BuiltInModuleNamespaces != null &&
            Ctx.BuiltInModuleNamespaces.TryGetValue(builtInModuleVar.Name.Lexeme, out var builtInModuleName) &&
            Ctx.BuiltInModuleEmitterRegistry?.GetEmitter(builtInModuleName) is { } builtInModuleEmitter)
        {
            if (builtInModuleEmitter.TryEmitMethodCall(this, builtInGet.Name.Lexeme, c.Arguments))
            {
                SetStackUnknown();
                return;
            }
        }

        // Special case: fs.promises.methodName()
        if (c.Callee is Expr.Get fsPromisesMethodGet &&
            fsPromisesMethodGet.Object is Expr.Get fsPromisesGet &&
            fsPromisesGet.Name.Lexeme == "promises" &&
            fsPromisesGet.Object is Expr.Variable fsVar &&
            Ctx.BuiltInModuleNamespaces != null &&
            Ctx.BuiltInModuleNamespaces.TryGetValue(fsVar.Name.Lexeme, out var fsModuleName) &&
            fsModuleName == "fs" &&
            Ctx.BuiltInModuleEmitterRegistry?.GetEmitter("fs/promises") is { } fsPromisesEmitter)
        {
            if (fsPromisesEmitter.TryEmitMethodCall(this, fsPromisesMethodGet.Name.Lexeme, c.Arguments))
            {
                SetStackUnknown();
                return;
            }
        }

        // Handle Class.staticMethod() calls
        if (c.Callee is Expr.Get classStaticGet &&
            classStaticGet.Object is Expr.Variable classVar &&
            Ctx.Classes.TryGetValue(Ctx.ResolveClassName(classVar.Name.Lexeme), out _))
        {
            string resolvedClassName = Ctx.ResolveClassName(classVar.Name.Lexeme);
            if (Ctx.ClassRegistry!.TryGetStaticMethod(resolvedClassName, classStaticGet.Name.Lexeme, out var staticMethod))
            {
                var staticMethodParams = staticMethod!.GetParameters();
                var paramCount = staticMethodParams.Length;

                List<LocalBuilder> staticArgTemps = [];
                for (int i = 0; i < c.Arguments.Count; i++)
                {
                    EmitExpression(c.Arguments[i]);
                    EnsureBoxed();
                    var temp = IL.DeclareLocal(typeof(object));
                    IL.Emit(OpCodes.Stloc, temp);
                    staticArgTemps.Add(temp);
                }

                for (int i = 0; i < staticArgTemps.Count; i++)
                {
                    IL.Emit(OpCodes.Ldloc, staticArgTemps[i]);
                    if (i < staticMethodParams.Length)
                    {
                        var targetType = staticMethodParams[i].ParameterType;
                        if (targetType.IsValueType && targetType != typeof(object))
                        {
                            IL.Emit(OpCodes.Unbox_Any, targetType);
                        }
                    }
                }

                for (int i = c.Arguments.Count; i < paramCount; i++)
                {
                    EmitDefaultForType(staticMethodParams[i].ParameterType);
                }

                IL.Emit(OpCodes.Call, staticMethod);
                SetStackUnknown();
                return;
            }
        }

        // Handle Promise instance methods: promise.then/catch/finally
        if (c.Callee is Expr.Get methodGet)
        {
            string methodName = methodGet.Name.Lexeme;
            if (methodName is "then" or "catch" or "finally")
            {
                EmitPromiseInstanceMethodCall(methodGet.Object, methodName, c.Arguments);
                return;
            }

            // Try direct dispatch for known class instance methods
            if (TryEmitDirectMethodCall(methodGet.Object, methodName, c.Arguments))
                return;

            // Type-first dispatch: Use TypeEmitterRegistry if we have type information
            var objType = Ctx.TypeMap?.Get(methodGet.Object);
            if (objType != null && Ctx.TypeEmitterRegistry != null)
            {
                var strategy = Ctx.TypeEmitterRegistry.GetStrategy(objType);
                if (strategy != null && strategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                    return;

                // Handle union types
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
                                return;
                        }
                        if (hasStringMember)
                        {
                            var stringStrategy = Ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.String());
                            if (stringStrategy != null && stringStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                                return;
                        }
                        if (hasArrayMember)
                        {
                            var arrayStrategy = Ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.Array(new TypeSystem.TypeInfo.Any()));
                            if (arrayStrategy != null && arrayStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                                return;
                        }
                    }
                }
            }

            // Fallback: Method name-based dispatch for known built-in methods
            if (Ctx.TypeEmitterRegistry != null)
            {
                if (methodName is "charAt" or "substring" or "toUpperCase" or "toLowerCase"
                    or "trim" or "replace" or "split" or "startsWith" or "endsWith"
                    or "repeat" or "padStart" or "padEnd" or "charCodeAt" or "lastIndexOf"
                    or "trimStart" or "trimEnd" or "replaceAll" or "at" or "match" or "search")
                {
                    var stringStrategy = Ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.String());
                    if (stringStrategy != null && stringStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                        return;
                }

                if (methodName is "pop" or "shift" or "unshift" or "map" or "filter"
                    or "push" or "find" or "findIndex" or "some" or "every" or "reduce" or "join"
                    or "reverse" or "fill")
                {
                    var arrayStrategy = Ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.Array(new TypeSystem.TypeInfo.Any()));
                    if (arrayStrategy != null && arrayStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                        return;
                }
            }

            // Handle ambiguous methods (slice, concat, includes, indexOf)
            if (methodName is "slice" or "concat" or "includes" or "indexOf")
            {
                EmitAmbiguousMethodCall(methodGet.Object, methodName, c.Arguments);
                return;
            }
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

        // Check if it's a direct function call
        if (c.Callee is Expr.Variable funcVar && Ctx.Functions.TryGetValue(Ctx.ResolveFunctionName(funcVar.Name.Lexeme), out var funcMethod))
        {
            var parameters = funcMethod.GetParameters();
            List<LocalBuilder> directArgTemps = [];

            for (int i = 0; i < parameters.Length; i++)
            {
                if (i < c.Arguments.Count)
                {
                    EmitExpression(c.Arguments[i]);
                    EnsureBoxed();
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
                var temp = IL.DeclareLocal(typeof(object));
                IL.Emit(OpCodes.Stloc, temp);
                directArgTemps.Add(temp);
            }

            for (int i = 0; i < directArgTemps.Count; i++)
            {
                IL.Emit(OpCodes.Ldloc, directArgTemps[i]);
                if (i < parameters.Length)
                {
                    var targetType = parameters[i].ParameterType;
                    if (targetType.IsValueType && targetType != typeof(object))
                    {
                        IL.Emit(OpCodes.Unbox_Any, targetType);
                    }
                }
            }
            IL.Emit(OpCodes.Call, funcMethod);

            var returnType = funcMethod.ReturnType;
            if (returnType.IsValueType && returnType != typeof(void))
            {
                IL.Emit(OpCodes.Box, returnType);
            }
            else if (returnType == typeof(void))
            {
                IL.Emit(OpCodes.Ldnull);
            }
            SetStackUnknown();
            return;
        }

        // Generic call through TSFunction/InvokeValue
        EmitExpression(c.Callee);
        EnsureBoxed();
        var calleeTemp = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, calleeTemp);

        List<LocalBuilder> argTemps = [];
        foreach (var arg in c.Arguments)
        {
            EmitExpression(arg);
            EnsureBoxed();
            var temp = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, temp);
            argTemps.Add(temp);
        }

        IL.Emit(OpCodes.Ldloc, calleeTemp);

        IL.Emit(OpCodes.Ldc_I4, c.Arguments.Count);
        IL.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < argTemps.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            IL.Emit(OpCodes.Ldloc, argTemps[i]);
            IL.Emit(OpCodes.Stelem_Ref);
        }

        IL.Emit(OpCodes.Call, Ctx.Runtime!.InvokeValue);
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

    #endregion
}
