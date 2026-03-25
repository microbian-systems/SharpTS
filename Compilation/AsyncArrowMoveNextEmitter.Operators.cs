using System.Reflection.Emit;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncArrowMoveNextEmitter
{
    protected override void EmitBinary(Expr.Binary b)
    {
        EmitExpression(b.Left);
        EnsureBoxed();
        EmitExpression(b.Right);
        EnsureBoxed();

        // Use consolidated binary operator helper
        if (!_helpers.TryEmitBinaryOperator(b.Operator.Type, _ctx!.Runtime!.Add, _ctx!.Runtime!.Equals))
        {
            // Unsupported operator - return null
            _il.Emit(OpCodes.Pop);
            _il.Emit(OpCodes.Ldnull);
        }
        SetStackUnknown();
    }

    protected override void EmitCall(Expr.Call c)
    {
        // Handle console.log specially (handles both Variable and Get patterns)
        if (_helpers.TryEmitConsoleLog(c,
            arg => { EmitExpression(arg); EnsureBoxed(); },
            _ctx!.Runtime!.ConsoleLog,
            _ctx!.Runtime!.ConsoleLogMultiple))
        {
            return;
        }

        // Handle fetch() - global async HTTP function
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

        // Static type dispatch via registry (Math, JSON, Object, Array, Number, Promise, Symbol)
        if (c.Callee is Expr.Get staticGet &&
            staticGet.Object is Expr.Variable staticVar &&
            _ctx?.TypeEmitterRegistry != null)
        {
            var staticStrategy = _ctx.TypeEmitterRegistry.GetStaticStrategy(staticVar.Name.Lexeme);
            if (staticStrategy != null && staticStrategy.TryEmitStaticCall(this, staticGet.Name.Lexeme, c.Arguments))
            {
                SetStackUnknown();
                return;
            }
        }

        // Built-in module method calls (fs.readFileSync, path.join, etc.)
        if (c.Callee is Expr.Get builtInGet &&
            builtInGet.Object is Expr.Variable builtInModuleVar &&
            _ctx!.BuiltInModuleNamespaces != null &&
            _ctx.BuiltInModuleNamespaces.TryGetValue(builtInModuleVar.Name.Lexeme, out var builtInModuleName) &&
            _ctx.BuiltInModuleEmitterRegistry?.GetEmitter(builtInModuleName) is { } builtInModuleEmitter)
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
            _ctx!.BuiltInModuleNamespaces != null &&
            _ctx.BuiltInModuleNamespaces.TryGetValue(fsVar.Name.Lexeme, out var fsModuleName) &&
            fsModuleName == "fs" &&
            _ctx.BuiltInModuleEmitterRegistry?.GetEmitter("fs/promises") is { } fsPromisesEmitter)
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
            _ctx!.Classes.TryGetValue(_ctx.ResolveClassName(classVar.Name.Lexeme), out var classBuilder))
        {
            string resolvedClassName = _ctx.ResolveClassName(classVar.Name.Lexeme);
            if (_ctx.ClassRegistry!.TryGetStaticMethod(resolvedClassName, classStaticGet.Name.Lexeme, out var staticMethod))
            {
                var staticMethodParams = staticMethod!.GetParameters();
                var paramCount = staticMethodParams.Length;

                List<LocalBuilder> staticArgTemps = [];
                for (int i = 0; i < c.Arguments.Count; i++)
                {
                    EmitExpression(c.Arguments[i]);
                    EnsureBoxed();
                    var temp = _il.DeclareLocal(typeof(object));
                    _il.Emit(OpCodes.Stloc, temp);
                    staticArgTemps.Add(temp);
                }

                for (int i = 0; i < staticArgTemps.Count; i++)
                {
                    _il.Emit(OpCodes.Ldloc, staticArgTemps[i]);
                    if (i < staticMethodParams.Length)
                    {
                        var targetType = staticMethodParams[i].ParameterType;
                        if (targetType.IsValueType && targetType != typeof(object))
                        {
                            _il.Emit(OpCodes.Unbox_Any, targetType);
                        }
                    }
                }

                for (int i = c.Arguments.Count; i < paramCount; i++)
                {
                    EmitDefaultForType(staticMethodParams[i].ParameterType);
                }

                _il.Emit(OpCodes.Call, staticMethod);
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
            var objType = _ctx?.TypeMap?.Get(methodGet.Object);
            if (objType != null && _ctx?.TypeEmitterRegistry != null)
            {
                var strategy = _ctx.TypeEmitterRegistry.GetStrategy(objType);
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
                            var bufferStrategy = _ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.Buffer());
                            if (bufferStrategy != null && bufferStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                                return;
                        }
                        if (hasStringMember)
                        {
                            var stringStrategy = _ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.String());
                            if (stringStrategy != null && stringStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                                return;
                        }
                        if (hasArrayMember)
                        {
                            var arrayStrategy = _ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.Array(new TypeSystem.TypeInfo.Any()));
                            if (arrayStrategy != null && arrayStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                                return;
                        }
                    }
                }
            }

            // Fallback: Method name-based dispatch for known built-in methods
            if (_ctx?.TypeEmitterRegistry != null)
            {
                if (methodName is "charAt" or "substring" or "toUpperCase" or "toLowerCase"
                    or "trim" or "replace" or "split" or "startsWith" or "endsWith"
                    or "repeat" or "padStart" or "padEnd" or "charCodeAt" or "lastIndexOf"
                    or "trimStart" or "trimEnd" or "replaceAll" or "at" or "match" or "search")
                {
                    var stringStrategy = _ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.String());
                    if (stringStrategy != null && stringStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, c.Arguments))
                        return;
                }

                if (methodName is "pop" or "shift" or "unshift" or "map" or "filter"
                    or "push" or "find" or "findIndex" or "some" or "every" or "reduce" or "join"
                    or "reverse" or "fill")
                {
                    var arrayStrategy = _ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.Array(new TypeSystem.TypeInfo.Any()));
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
            _ctx!.BuiltInModuleMethodBindings?.TryGetValue(builtInVar.Name.Lexeme, out var binding) == true)
        {
            var builtInEmitter = _ctx.BuiltInModuleEmitterRegistry?.GetEmitter(binding.ModuleName);
            if (builtInEmitter != null && builtInEmitter.TryEmitMethodCall(this, binding.MethodName, c.Arguments))
            {
                SetStackUnknown();
                return;
            }
        }

        // Check if it's a direct function call
        if (c.Callee is Expr.Variable funcVar && _ctx!.Functions.TryGetValue(_ctx.ResolveFunctionName(funcVar.Name.Lexeme), out var funcMethod))
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
                    _il.Emit(OpCodes.Ldnull);
                }
                var temp = _il.DeclareLocal(typeof(object));
                _il.Emit(OpCodes.Stloc, temp);
                directArgTemps.Add(temp);
            }

            for (int i = 0; i < directArgTemps.Count; i++)
            {
                _il.Emit(OpCodes.Ldloc, directArgTemps[i]);
                if (i < parameters.Length)
                {
                    var targetType = parameters[i].ParameterType;
                    if (targetType.IsValueType && targetType != typeof(object))
                    {
                        _il.Emit(OpCodes.Unbox_Any, targetType);
                    }
                }
            }
            _il.Emit(OpCodes.Call, funcMethod);

            var returnType = funcMethod.ReturnType;
            if (returnType.IsValueType && returnType != typeof(void))
            {
                _il.Emit(OpCodes.Box, returnType);
            }
            else if (returnType == typeof(void))
            {
                _il.Emit(OpCodes.Ldnull);
            }
            SetStackUnknown();
            return;
        }

        // Generic call through TSFunction/InvokeValue
        EmitExpression(c.Callee);
        EnsureBoxed();
        var calleeTemp = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, calleeTemp);

        List<LocalBuilder> argTemps = [];
        foreach (var arg in c.Arguments)
        {
            EmitExpression(arg);
            EnsureBoxed();
            var temp = _il.DeclareLocal(typeof(object));
            _il.Emit(OpCodes.Stloc, temp);
            argTemps.Add(temp);
        }

        _il.Emit(OpCodes.Ldloc, calleeTemp);

        _il.Emit(OpCodes.Ldc_I4, c.Arguments.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < argTemps.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldloc, argTemps[i]);
            _il.Emit(OpCodes.Stelem_Ref);
        }

        _il.Emit(OpCodes.Call, _ctx!.Runtime!.InvokeValue);
        SetStackUnknown();
    }

    #region Call Helpers

    private bool TryEmitDirectMethodCall(Expr receiver, string methodName, List<Expr> arguments)
    {
        var receiverType = _ctx?.TypeMap?.Get(receiver);
        if (receiverType is not TypeSystem.TypeInfo.Instance instance)
            return false;

        string? simpleClassName = instance.ClassType switch
        {
            TypeSystem.TypeInfo.Class classType => classType.Name,
            _ => null
        };
        if (simpleClassName == null)
            return false;

        string className = _ctx!.ResolveClassName(simpleClassName);
        var methodBuilder = _ctx.ResolveInstanceMethod(className, methodName);
        if (methodBuilder == null)
            return false;

        if (!_ctx.Classes.TryGetValue(className, out var classType2))
            return false;

        var methodParams = methodBuilder.GetParameters();
        int expectedParamCount = methodParams.Length;

        List<LocalBuilder> argTemps = [];
        foreach (var arg in arguments)
        {
            EmitExpression(arg);
            EnsureBoxed();
            var temp = _il.DeclareLocal(typeof(object));
            _il.Emit(OpCodes.Stloc, temp);
            argTemps.Add(temp);
        }

        EmitExpression(receiver);
        EnsureBoxed();
        _il.Emit(OpCodes.Castclass, classType2);

        for (int i = 0; i < argTemps.Count; i++)
        {
            _il.Emit(OpCodes.Ldloc, argTemps[i]);
            if (i < methodParams.Length)
            {
                var targetType = methodParams[i].ParameterType;
                if (targetType.IsValueType && targetType != typeof(object))
                {
                    _il.Emit(OpCodes.Unbox_Any, targetType);
                }
            }
        }

        for (int i = arguments.Count; i < expectedParamCount; i++)
        {
            EmitDefaultForType(methodParams[i].ParameterType);
        }

        _il.Emit(OpCodes.Callvirt, methodBuilder);
        SetStackUnknown();
        return true;
    }

    private void EmitFetchCall(List<Expr> arguments)
    {
        if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { _il.Emit(OpCodes.Ldnull); }
        if (arguments.Count > 1) { EmitExpression(arguments[1]); EnsureBoxed(); } else { _il.Emit(OpCodes.Ldnull); }
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.Fetch);
        SetStackUnknown();
    }

    private void EmitPromiseInstanceMethodCall(Expr promise, string methodName, List<Expr> arguments)
    {
        EmitExpression(promise);
        EnsureBoxed();
        _il.Emit(OpCodes.Castclass, typeof(Task<object?>));

        switch (methodName)
        {
            case "then":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { _il.Emit(OpCodes.Ldnull); }
                if (arguments.Count > 1) { EmitExpression(arguments[1]); EnsureBoxed(); } else { _il.Emit(OpCodes.Ldnull); }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.PromiseThen);
                break;
            case "catch":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { _il.Emit(OpCodes.Ldnull); }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.PromiseCatch);
                break;
            case "finally":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { _il.Emit(OpCodes.Ldnull); }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.PromiseFinally);
                break;
        }
        SetStackUnknown();
    }

    private void EmitAmbiguousMethodCall(Expr obj, string methodName, List<Expr> arguments)
    {
        EmitExpression(obj);
        EnsureBoxed();
        var objLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, objLocal);

        var isStringLabel = _il.DefineLabel();
        var isListLabel = _il.DefineLabel();
        var doneLabel = _il.DefineLabel();

        _il.Emit(OpCodes.Ldloc, objLocal);
        _il.Emit(OpCodes.Isinst, typeof(string));
        _il.Emit(OpCodes.Brtrue, isStringLabel);
        _il.Emit(OpCodes.Br, isListLabel);

        // String path
        _il.MarkLabel(isStringLabel);
        _il.Emit(OpCodes.Ldloc, objLocal);
        _il.Emit(OpCodes.Castclass, typeof(string));
        switch (methodName)
        {
            case "includes":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); _il.Emit(OpCodes.Castclass, typeof(string)); }
                else { _il.Emit(OpCodes.Ldstr, ""); }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.StringIncludes);
                _il.Emit(OpCodes.Box, typeof(bool));
                break;
            case "indexOf":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); _il.Emit(OpCodes.Castclass, typeof(string)); }
                else { _il.Emit(OpCodes.Ldstr, ""); }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.StringIndexOf);
                _il.Emit(OpCodes.Box, typeof(double));
                break;
            case "slice":
                _il.Emit(OpCodes.Ldc_I4, arguments.Count);
                _il.Emit(OpCodes.Ldc_I4, arguments.Count);
                _il.Emit(OpCodes.Newarr, typeof(object));
                for (int i = 0; i < arguments.Count; i++)
                {
                    _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldc_I4, i);
                    EmitExpression(arguments[i]); EnsureBoxed();
                    _il.Emit(OpCodes.Stelem_Ref);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.StringSlice);
                break;
            case "concat":
                _il.Emit(OpCodes.Ldc_I4, arguments.Count);
                _il.Emit(OpCodes.Newarr, typeof(object));
                for (int i = 0; i < arguments.Count; i++)
                {
                    _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldc_I4, i);
                    EmitExpression(arguments[i]); EnsureBoxed();
                    _il.Emit(OpCodes.Stelem_Ref);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.StringConcat);
                break;
        }
        _il.Emit(OpCodes.Br, doneLabel);

        // List path
        _il.MarkLabel(isListLabel);
        _il.Emit(OpCodes.Ldloc, objLocal);
        _il.Emit(OpCodes.Castclass, typeof(List<object>));
        switch (methodName)
        {
            case "includes":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { _il.Emit(OpCodes.Ldnull); }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.ArrayIncludes);
                _il.Emit(OpCodes.Box, typeof(bool));
                break;
            case "indexOf":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { _il.Emit(OpCodes.Ldnull); }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.ArrayIndexOf);
                _il.Emit(OpCodes.Box, typeof(double));
                break;
            case "slice":
                _il.Emit(OpCodes.Ldc_I4, arguments.Count);
                _il.Emit(OpCodes.Newarr, typeof(object));
                for (int i = 0; i < arguments.Count; i++)
                {
                    _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldc_I4, i);
                    EmitExpression(arguments[i]); EnsureBoxed();
                    _il.Emit(OpCodes.Stelem_Ref);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.ArraySlice);
                break;
            case "concat":
                if (arguments.Count > 0) { EmitExpression(arguments[0]); EnsureBoxed(); } else { _il.Emit(OpCodes.Ldnull); }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.ArrayConcat);
                break;
        }

        _il.MarkLabel(doneLabel);
        SetStackUnknown();
    }

    private void EmitDefaultForType(Type type)
    {
        if (type == typeof(double)) { _il.Emit(OpCodes.Ldc_R8, 0.0); }
        else if (type == typeof(int)) { _il.Emit(OpCodes.Ldc_I4_0); }
        else if (type == typeof(bool)) { _il.Emit(OpCodes.Ldc_I4_0); }
        else if (type == typeof(float)) { _il.Emit(OpCodes.Ldc_R4, 0.0f); }
        else if (type == typeof(long)) { _il.Emit(OpCodes.Ldc_I8, 0L); }
        else if (type.IsValueType)
        {
            var local = _il.DeclareLocal(type);
            _il.Emit(OpCodes.Ldloca, local);
            _il.Emit(OpCodes.Initobj, type);
            _il.Emit(OpCodes.Ldloc, local);
        }
        else { _il.Emit(OpCodes.Ldnull); }
    }

    #endregion

    protected override void EmitAwait(Expr.Await aw)
    {
        int stateNum = _currentState++;
        var continueLabel = _il.DefineLabel();

        if (!_builder.AwaiterFields.TryGetValue(stateNum, out var awaiterField))
        {
            throw new CompileException($"No awaiter field for state {stateNum}");
        }

        // 1. Emit the awaited expression (should produce Task<object> or $Promise or any value)
        EmitExpression(aw.Expression);
        EnsureBoxed();

        // 2. Convert to Task<object> - handle $Promise, Task<object>, or non-Task values
        // If it's a $Promise, extract its Task property
        // If it's already a Task<object>, use it directly
        // Otherwise, wrap in Task.FromResult (for non-promise values like numbers, strings, etc.)
        var taskLocal = _il.DeclareLocal(typeof(Task<object>));
        var isPromiseLabel = _il.DefineLabel();
        var isTaskLabel = _il.DefineLabel();
        var wrapValueLabel = _il.DefineLabel();
        var haveTaskLabel = _il.DefineLabel();

        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Isinst, _ctx!.Runtime!.TSPromiseType);
        _il.Emit(OpCodes.Brtrue, isPromiseLabel);

        // Not a $Promise - check if it's a Task<object>
        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Isinst, typeof(Task<object>));
        _il.Emit(OpCodes.Brtrue, isTaskLabel);

        // Not a Promise or Task - wrap in Task.FromResult
        _il.MarkLabel(wrapValueLabel);
        _il.Emit(OpCodes.Call, typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(typeof(object)));
        _il.Emit(OpCodes.Stloc, taskLocal);
        _il.Emit(OpCodes.Br, haveTaskLabel);

        // Is a Task<object> - use directly
        _il.MarkLabel(isTaskLabel);
        _il.Emit(OpCodes.Castclass, typeof(Task<object>));
        _il.Emit(OpCodes.Stloc, taskLocal);
        _il.Emit(OpCodes.Br, haveTaskLabel);

        // Is a $Promise - extract its Task property
        _il.MarkLabel(isPromiseLabel);
        _il.Emit(OpCodes.Castclass, _ctx.Runtime.TSPromiseType);
        _il.Emit(OpCodes.Callvirt, _ctx.Runtime.TSPromiseTaskGetter);
        _il.Emit(OpCodes.Stloc, taskLocal);

        _il.MarkLabel(haveTaskLabel);
        _il.Emit(OpCodes.Ldloc, taskLocal);

        // 3. Get awaiter: task.GetAwaiter()
        _il.Emit(OpCodes.Call, _builder.GetTaskGetAwaiterMethod());

        // 4. Store awaiter to field
        var awaiterLocal = _il.DeclareLocal(_builder.AwaiterType);
        _il.Emit(OpCodes.Stloc, awaiterLocal);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, awaiterLocal);
        _il.Emit(OpCodes.Stfld, awaiterField);

        // 5. Check IsCompleted
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldflda, awaiterField);
        _il.Emit(OpCodes.Call, _builder.GetAwaiterIsCompletedGetter());
        _il.Emit(OpCodes.Brtrue, continueLabel);

        // 6. Not completed - suspend
        // this.<>1__state = stateNum
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, stateNum);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // builder.AwaitUnsafeOnCompleted(ref awaiter, ref this)
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldflda, _builder.BuilderField);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldflda, awaiterField);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Call, _builder.GetBuilderAwaitUnsafeOnCompletedMethod());

        // return (exit MoveNext)
        _il.Emit(OpCodes.Leave, _exitLabel);

        // 7. Resume point (jumped to from state switch)
        if (stateNum < _stateLabels.Count)
            _il.MarkLabel(_stateLabels[stateNum]);

        // Reset state to -1 (running)
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4_M1);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // 8. Continue point (if was already completed)
        _il.MarkLabel(continueLabel);

        // 9. Get result: awaiter.GetResult()
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldflda, awaiterField);
        _il.Emit(OpCodes.Call, _builder.GetAwaiterGetResultMethod());

        // Result is now on stack
        SetStackUnknown();
    }
}
