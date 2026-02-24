using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Compilation.CallHandlers;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;
using SharpTS.TypeSystem;

namespace SharpTS.Compilation;

/// <summary>
/// Main call dispatch and function call emission methods for the IL emitter.
/// </summary>
public partial class ILEmitter
{
    /// <summary>
    /// Registry of call handlers for Chain of Responsibility dispatch.
    /// </summary>
    private static readonly CallHandlerRegistry _callHandlers = new();

    protected override void EmitCall(Expr.Call c)
    {
        // Try handler chain first (handles simple cases)
        if (_callHandlers.TryHandle(this, c))
            return;

        // Special case: super() or super.constructor() call in derived class
        if (c.Callee is Expr.Super superExpr && (superExpr.Method == null || superExpr.Method.Lexeme == "constructor"))
        {
            // Try class declaration constructors first
            // Use GetConstructor (not GetConstructorByQualifiedName) to resolve simple names like "Animal"
            // to qualified names like "$M_base_Animal" for cross-module inheritance
            var parentCtor = _ctx.CurrentSuperclassName != null
                ? _ctx.ClassRegistry?.GetConstructor(_ctx.CurrentSuperclassName)
                : null;
            if (parentCtor != null)
            {
                EmitSuperConstructorCall(parentCtor, c.Arguments);
                return;
            }

            // Try class expression constructors (for class expression inheritance)
            if (_ctx.CurrentClassExpr != null &&
                _ctx.ClassExprSuperclass?.TryGetValue(_ctx.CurrentClassExpr, out var superclassName) == true &&
                superclassName != null)
            {
                // Find parent constructor by superclass name (using variable name mapping)
                ConstructorBuilder? parentExprCtor = null;

                // Check class expression constructors using VarToClassExpr mapping
                if (_ctx.VarToClassExpr != null &&
                    _ctx.VarToClassExpr.TryGetValue(superclassName, out var parentClassExpr) &&
                    _ctx.ClassExprConstructors != null &&
                    _ctx.ClassExprConstructors.TryGetValue(parentClassExpr, out var exprCtor))
                {
                    parentExprCtor = exprCtor;
                }

                // If not found in class expressions, try class declarations
                if (parentExprCtor == null)
                {
                    parentExprCtor = _ctx.ClassRegistry?.GetConstructorByQualifiedName(superclassName);
                }

                if (parentExprCtor != null)
                {
                    EmitSuperConstructorCall(parentExprCtor, c.Arguments);
                    return;
                }
            }
        }

        // Special case: super.method() call in derived class (non-constructor)
        // Must use OpCodes.Call (non-virtual) to bypass virtual dispatch and call the base method directly.
        // Using Callvirt or MethodInfo.Invoke would dispatch to the derived override, causing infinite recursion.
        if (c.Callee is Expr.Super superMethodExpr && superMethodExpr.Method != null && superMethodExpr.Method.Lexeme != "constructor")
        {
            if (TryEmitSuperMethodCall(superMethodExpr.Method.Lexeme, c.Arguments))
                return;
        }

        // Special case: console methods (log, error, warn, info, debug, clear, time, timeEnd, timeLog)
        if (_helpers.TryEmitConsoleMethod(c,
            arg => { EmitExpression(arg); EmitBoxIfNeeded(arg); },
            _ctx.Runtime!))
        {
            return;
        }

        // Static type dispatch via registry (Math, JSON, Object, Array, Number, Promise)
        if (c.Callee is Expr.Get staticGet &&
            staticGet.Object is Expr.Variable staticVar &&
            _ctx.TypeEmitterRegistry != null)
        {
            var staticStrategy = _ctx.TypeEmitterRegistry.GetStaticStrategy(staticVar.Name.Lexeme);
            if (staticStrategy != null && staticStrategy.TryEmitStaticCall(this, staticGet.Name.Lexeme, c.Arguments))
            {
                SetStackUnknown();
                return;
            }
        }

        // Special case: globalThis.Math.floor(), globalThis.console.log(), etc.
        if (c.Callee is Expr.Get chainedGet &&
            chainedGet.Object is Expr.Get innerGet &&
            innerGet.Object is Expr.Variable globalThisVar &&
            globalThisVar.Name.Lexeme == "globalThis" &&
            _ctx.TypeEmitterRegistry != null)
        {
            string namespaceName = innerGet.Name.Lexeme;
            string methodName = chainedGet.Name.Lexeme;

            // Handle globalThis.console.log() specially
            if (namespaceName == "console")
            {
                var fakeCall = new Expr.Call(
                    new Expr.Get(new Expr.Variable(innerGet.Name), chainedGet.Name, false),
                    chainedGet.Name,
                    null, // TypeArgs
                    c.Arguments
                );
                if (_helpers.TryEmitConsoleMethod(fakeCall,
                    arg => { EmitExpression(arg); EmitBoxIfNeeded(arg); },
                    _ctx.Runtime!))
                {
                    return;
                }
            }

            // Use the static emitter for the inner namespace
            var staticStrategy = _ctx.TypeEmitterRegistry.GetStaticStrategy(namespaceName);
            if (staticStrategy != null && staticStrategy.TryEmitStaticCall(this, methodName, c.Arguments))
            {
                SetStackUnknown();
                return;
            }
        }

        // Special case: process.stdin.read(), process.stdout.write(), process.stderr.write()
        if (TryEmitProcessStreamCall(c))
        {
            return;
        }

        // Built-in module method calls (path.join, fs.readFileSync, etc.)
        if (c.Callee is Expr.Get builtInGet &&
            builtInGet.Object is Expr.Variable builtInVar &&
            _ctx.BuiltInModuleNamespaces != null &&
            _ctx.BuiltInModuleNamespaces.TryGetValue(builtInVar.Name.Lexeme, out var builtInModuleName) &&
            _ctx.BuiltInModuleEmitterRegistry?.GetEmitter(builtInModuleName) is { } builtInEmitter)
        {
            if (builtInEmitter.TryEmitMethodCall(this, builtInGet.Name.Lexeme, c.Arguments))
            {
                SetStackUnknown();
                return;
            }
        }

        // Special case: fs.promises.methodName() - emit direct method call instead of going through TSFunction
        // Pattern: c.Callee is Get(Get(Variable("fs"), "promises"), "methodName")
        if (c.Callee is Expr.Get fsPromisesMethodGet &&
            fsPromisesMethodGet.Object is Expr.Get fsPromisesGet &&
            fsPromisesGet.Name.Lexeme == "promises" &&
            fsPromisesGet.Object is Expr.Variable fsVar &&
            _ctx.BuiltInModuleNamespaces != null &&
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

        // Special case: __objectRest (internal helper for object rest patterns)
        if (c.Callee is Expr.Variable restVar && restVar.Name.Lexeme == "__objectRest")
        {
            if (c.Arguments.Count >= 2)
            {
                // Emit source object (now accepts object to support both dictionaries and class instances)
                EmitExpression(c.Arguments[0]);
                EmitBoxIfNeeded(c.Arguments[0]);

                // Emit exclude keys (List<object>)
                EmitExpression(c.Arguments[1]);
                EmitBoxIfNeeded(c.Arguments[1]);
                IL.Emit(OpCodes.Castclass, _ctx.Types.ListOfObject);

                IL.Emit(OpCodes.Call, _ctx.Runtime!.ObjectRest);
                return;
            }
        }

        // Special case: Symbol() constructor - creates unique symbols
        if (c.Callee is Expr.Variable symVar && symVar.Name.Lexeme == "Symbol")
        {
            if (c.Arguments.Count == 0)
            {
                // Symbol() with no description
                IL.Emit(OpCodes.Ldnull);
            }
            else
            {
                // Symbol(description) - emit the description argument
                EmitExpression(c.Arguments[0]);
                // Convert to string if needed
                IL.Emit(OpCodes.Call, _ctx.Runtime!.Stringify);
            }
            // Create new $TSSymbol instance
            IL.Emit(OpCodes.Newobj, _ctx.Runtime!.TSSymbolCtor);
            return;
        }

        // Special case: BigInt() constructor - converts number/string to bigint
        if (c.Callee is Expr.Variable bigIntVar && bigIntVar.Name.Lexeme == "BigInt")
        {
            if (c.Arguments.Count != 1)
                throw new CompileException("BigInt() requires exactly one argument.");

            EmitExpression(c.Arguments[0]);
            EmitBoxIfNeeded(c.Arguments[0]);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.CreateBigInt);
            SetStackUnknown();
            return;
        }

        // Special case: Date() function call - returns current date as string
        if (c.Callee is Expr.Variable dateVar && dateVar.Name.Lexeme == "Date")
        {
            // Date() without 'new' returns current date as string
            IL.Emit(OpCodes.Call, _ctx.Runtime!.CreateDateNoArgs);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.DateToString);
            return;
        }

        // Special case: Date.now() static method
        if (c.Callee is Expr.Get dateGet &&
            dateGet.Object is Expr.Variable dateStaticVar &&
            dateStaticVar.Name.Lexeme == "Date" &&
            dateGet.Name.Lexeme == "now")
        {
            IL.Emit(OpCodes.Call, _ctx.Runtime!.DateNow);
            IL.Emit(OpCodes.Box, _ctx.Types.Double);
            return;
        }

        // Special case: Global parseInt()
        if (c.Callee is Expr.Variable parseIntVar && parseIntVar.Name.Lexeme == "parseInt")
        {
            EmitGlobalParseInt(c.Arguments);
            return;
        }

        // Special case: Global parseFloat()
        if (c.Callee is Expr.Variable parseFloatVar && parseFloatVar.Name.Lexeme == "parseFloat")
        {
            EmitGlobalParseFloat(c.Arguments);
            return;
        }

        // Special case: Global isNaN()
        if (c.Callee is Expr.Variable isNaNVar && isNaNVar.Name.Lexeme == "isNaN")
        {
            EmitGlobalIsNaN(c.Arguments);
            return;
        }

        // Special case: Global isFinite()
        if (c.Callee is Expr.Variable isFiniteVar && isFiniteVar.Name.Lexeme == "isFinite")
        {
            EmitGlobalIsFinite(c.Arguments);
            return;
        }

        // Special case: Global structuredClone()
        if (c.Callee is Expr.Variable structuredCloneVar && structuredCloneVar.Name.Lexeme == "structuredClone")
        {
            EmitStructuredClone(c.Arguments);
            return;
        }

        // Special case: Static method call on external .NET type (e.g., Console.WriteLine())
        if (c.Callee is Expr.Get externalStaticGet &&
            externalStaticGet.Object is Expr.Variable externalClassVar &&
            _ctx.TypeMapper?.ExternalTypes.TryGetValue(externalClassVar.Name.Lexeme, out var externalType) == true)
        {
            EmitExternalStaticMethodCall(externalType, externalStaticGet.Name.Lexeme, c.Arguments);
            return;
        }

        // Special case: Static method call on class (e.g., Counter.increment())
        if (c.Callee is Expr.Get classStaticGet &&
            classStaticGet.Object is Expr.Variable classVar &&
            _ctx.Classes.TryGetValue(_ctx.ResolveClassName(classVar.Name.Lexeme), out var classBuilder))
        {
            string resolvedClassName = _ctx.ResolveClassName(classVar.Name.Lexeme);
            // Use TryGetCallableStaticMethod to handle generic classes properly
            if (_ctx.ClassRegistry!.TryGetCallableStaticMethod(resolvedClassName, classStaticGet.Name.Lexeme, classBuilder, out var callableMethod))
            {
                var staticMethodParams = callableMethod!.GetParameters();
                var paramCount = staticMethodParams.Length;

                // Emit provided arguments with proper type conversions
                for (int i = 0; i < c.Arguments.Count; i++)
                {
                    EmitExpression(c.Arguments[i]);
                    if (i < staticMethodParams.Length)
                    {
                        EmitConversionForParameter(c.Arguments[i], staticMethodParams[i].ParameterType);
                    }
                    else
                    {
                        EmitBoxIfNeeded(c.Arguments[i]);
                    }
                }

                // Pad missing optional arguments with appropriate default values
                for (int i = c.Arguments.Count; i < paramCount; i++)
                {
                    EmitDefaultForType(staticMethodParams[i].ParameterType);
                }

                IL.Emit(OpCodes.Call, callableMethod);
                SetStackUnknown();
                return;
            }
        }

        // Special case: Static method call on imported class (import X = require('./module') where module exports a class)
        if (c.Callee is Expr.Get importedClassStaticGet &&
            importedClassStaticGet.Object is Expr.Variable importedClassVar &&
            _ctx.ImportedClassAliases?.TryGetValue(importedClassVar.Name.Lexeme, out var importedQualifiedClassName) == true &&
            _ctx.Classes.TryGetValue(importedQualifiedClassName, out var importedClassBuilder))
        {
            if (_ctx.ClassRegistry!.TryGetCallableStaticMethod(importedQualifiedClassName, importedClassStaticGet.Name.Lexeme, importedClassBuilder, out var importedCallableMethod))
            {
                var importedMethodParams = importedCallableMethod!.GetParameters();
                var paramCount = importedMethodParams.Length;

                // Emit provided arguments with proper type conversions
                for (int i = 0; i < c.Arguments.Count; i++)
                {
                    EmitExpression(c.Arguments[i]);
                    if (i < importedMethodParams.Length)
                    {
                        EmitConversionForParameter(c.Arguments[i], importedMethodParams[i].ParameterType);
                    }
                    else
                    {
                        EmitBoxIfNeeded(c.Arguments[i]);
                    }
                }

                // Pad missing optional arguments
                for (int i = c.Arguments.Count; i < paramCount; i++)
                {
                    EmitDefaultForType(importedMethodParams[i].ParameterType);
                }

                IL.Emit(OpCodes.Call, importedCallableMethod);
                SetStackUnknown();
                return;
            }
        }

        // Special case: Static method call on class expression (const Factory = class { static create() { } }; Factory.create())
        if (c.Callee is Expr.Get classExprStaticGet &&
            classExprStaticGet.Object is Expr.Variable classExprVar &&
            _ctx.VarToClassExpr != null &&
            _ctx.VarToClassExpr.TryGetValue(classExprVar.Name.Lexeme, out var classExpr) &&
            _ctx.ClassExprStaticMethods != null &&
            _ctx.ClassExprStaticMethods.TryGetValue(classExpr, out var exprStaticMethods) &&
            exprStaticMethods.TryGetValue(classExprStaticGet.Name.Lexeme, out var exprStaticMethod))
        {
            var exprStaticMethodParams = exprStaticMethod.GetParameters();
            var paramCount = exprStaticMethodParams.Length;

            // Emit provided arguments with proper type conversions
            for (int i = 0; i < c.Arguments.Count; i++)
            {
                EmitExpression(c.Arguments[i]);
                if (i < exprStaticMethodParams.Length)
                {
                    EmitConversionForParameter(c.Arguments[i], exprStaticMethodParams[i].ParameterType);
                }
                else
                {
                    EmitBoxIfNeeded(c.Arguments[i]);
                }
            }

            // Pad missing optional arguments with appropriate default values
            for (int i = c.Arguments.Count; i < paramCount; i++)
            {
                EmitDefaultForType(exprStaticMethodParams[i].ParameterType);
            }

            IL.Emit(OpCodes.Call, exprStaticMethod);
            SetStackUnknown();
            return;
        }

        // Special case: this.method() in static context (static blocks, static methods)
        // In static blocks, 'this' refers to the class constructor, so this.method() calls static methods
        if (c.Callee is Expr.Get thisStaticGet &&
            thisStaticGet.Object is Expr.This &&
            !_ctx.IsInstanceMethod &&
            _ctx.CurrentClassBuilder != null)
        {
            // Use cached CurrentClassName instead of linear search
            string? currentClassName = _ctx.CurrentClassName;

            if (currentClassName != null &&
                _ctx.ClassRegistry!.TryGetCallableStaticMethod(currentClassName, thisStaticGet.Name.Lexeme, _ctx.CurrentClassBuilder, out var thisStaticMethod))
            {
                var staticMethodParams = thisStaticMethod!.GetParameters();
                var paramCount = staticMethodParams.Length;

                // Emit provided arguments with proper type conversions
                for (int i = 0; i < c.Arguments.Count; i++)
                {
                    EmitExpression(c.Arguments[i]);
                    if (i < staticMethodParams.Length)
                    {
                        EmitConversionForParameter(c.Arguments[i], staticMethodParams[i].ParameterType);
                    }
                    else
                    {
                        EmitBoxIfNeeded(c.Arguments[i]);
                    }
                }

                // Pad missing optional arguments with appropriate default values
                for (int i = c.Arguments.Count; i < paramCount; i++)
                {
                    EmitDefaultForType(staticMethodParams[i].ParameterType);
                }

                IL.Emit(OpCodes.Call, thisStaticMethod);
                SetStackUnknown();
                return;
            }
        }

        // Special case: Array/String methods
        if (c.Callee is Expr.Get methodGet)
        {
            EmitMethodCall(methodGet, c.Arguments);
            return;
        }

        // Regular function call (named top-level function)
        // First check for built-in module method bindings (e.g., import { readFile } from 'fs/promises')
        // This handles direct calls like readFile(...) by emitting direct method calls instead of TSFunction
        if (c.Callee is Expr.Variable builtInMethodVar &&
            _ctx.BuiltInModuleMethodBindings?.TryGetValue(builtInMethodVar.Name.Lexeme, out var binding) == true)
        {
            var methodBindingEmitter = _ctx.BuiltInModuleEmitterRegistry?.GetEmitter(binding.ModuleName);
            if (methodBindingEmitter != null && methodBindingEmitter.TryEmitMethodCall(this, binding.MethodName, c.Arguments))
            {
                return;
            }
        }

        // Check for async functions
        if (c.Callee is Expr.Variable asyncVar && _ctx.AsyncMethods?.TryGetValue(asyncVar.Name.Lexeme, out var asyncMethod) == true)
        {
            EmitAsyncFunctionCall(asyncMethod, c.Arguments);
            return;
        }

        if (c.Callee is Expr.Variable funcVar)
        {
            // Resolve function name (may be module-qualified in multi-module compilation)
            string resolvedFuncName = _ctx.ResolveFunctionName(funcVar.Name.Lexeme);

            // Check if this is an imported function (from another module)
            // If so, we must use the import field (in TopLevelStaticVars) instead of direct call,
            // because cross-module method token references don't work correctly
            bool isImportedFunction = _ctx.TopLevelStaticVars?.ContainsKey(funcVar.Name.Lexeme) == true;

            if (!isImportedFunction && _ctx.Functions.TryGetValue(resolvedFuncName, out var methodBuilder))
            {
                // Determine target method (may be generic instantiation)
                MethodInfo targetMethod = methodBuilder;

                // Handle generic function call (e.g., identity<number>(42))
                if (_ctx.IsGenericFunction?.TryGetValue(resolvedFuncName, out var isGeneric) == true && isGeneric)
                {
                    if (c.TypeArgs != null && c.TypeArgs.Count > 0)
                    {
                        // Explicit type arguments
                        Type[] typeArgs = c.TypeArgs.Select(ResolveTypeArg).ToArray();
                        targetMethod = methodBuilder.MakeGenericMethod(typeArgs);
                    }
                    else
                    {
                        // Type inference fallback - use constraint type or object
                        var genericParams = _ctx.FunctionGenericParams![resolvedFuncName];
                        Type[] inferredArgs = new Type[genericParams.Length];
                        for (int i = 0; i < genericParams.Length; i++)
                        {
                            // Use the base type constraint if available, otherwise object
                            var baseConstraint = genericParams[i].BaseType;
                            inferredArgs[i] = (baseConstraint != null && !_ctx.Types.IsObject(baseConstraint))
                                ? baseConstraint
                                : _ctx.Types.Object;
                        }
                        targetMethod = methodBuilder.MakeGenericMethod(inferredArgs);
                    }
                }

                var paramCount = targetMethod.GetParameters().Length;

                // Check if this function has a rest parameter
                (int RestParamIndex, int RegularParamCount) restInfo = default;
                bool hasRestParam = _ctx.FunctionRestParams?.TryGetValue(resolvedFuncName, out restInfo) == true;
                bool hasSpreads = c.Arguments.Any(a => a is Expr.Spread);

                if (hasRestParam)
                {
                    int regularCount = restInfo.RegularParamCount;
                    int restIndex = restInfo.RestParamIndex;

                    // Emit regular arguments (up to rest param index)
                    for (int i = 0; i < Math.Min(regularCount, c.Arguments.Count); i++)
                    {
                        if (c.Arguments[i] is Expr.Spread spread)
                        {
                            // Spread in regular position - just emit the expression
                            EmitExpression(spread.Expression);
                            EmitBoxIfNeeded(spread.Expression);
                        }
                        else
                        {
                            EmitExpression(c.Arguments[i]);
                            EmitBoxIfNeeded(c.Arguments[i]);
                        }
                    }

                    // Pad regular args with nulls if needed
                    for (int i = c.Arguments.Count; i < regularCount; i++)
                    {
                        IL.Emit(OpCodes.Ldnull);
                    }

                    // Create array for rest parameter from remaining arguments
                    int restArgsCount = Math.Max(0, c.Arguments.Count - regularCount);
                    if (hasSpreads && restArgsCount > 0)
                    {
                        // Has spreads in rest args - use ExpandCallArgs helper
                        IL.Emit(OpCodes.Ldc_I4, restArgsCount);
                        IL.Emit(OpCodes.Newarr, _ctx.Types.Object);
                        for (int i = 0; i < restArgsCount; i++)
                        {
                            IL.Emit(OpCodes.Dup);
                            IL.Emit(OpCodes.Ldc_I4, i);
                            var arg = c.Arguments[regularCount + i];
                            if (arg is Expr.Spread spread)
                            {
                                EmitExpression(spread.Expression);
                                EmitBoxIfNeeded(spread.Expression);
                            }
                            else
                            {
                                EmitExpression(arg);
                                EmitBoxIfNeeded(arg);
                            }
                            IL.Emit(OpCodes.Stelem_Ref);
                        }

                        // Emit isSpread array
                        IL.Emit(OpCodes.Ldc_I4, restArgsCount);
                        IL.Emit(OpCodes.Newarr, _ctx.Types.Boolean);
                        for (int i = 0; i < restArgsCount; i++)
                        {
                            if (c.Arguments[regularCount + i] is Expr.Spread)
                            {
                                IL.Emit(OpCodes.Dup);
                                IL.Emit(OpCodes.Ldc_I4, i);
                                IL.Emit(OpCodes.Ldc_I4_1);
                                IL.Emit(OpCodes.Stelem_I1);
                            }
                        }
                        // Pass Symbol.iterator and runtimeType for iterator protocol support
                        IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.SymbolIterator);
                        IL.Emit(OpCodes.Ldtoken, _ctx.Runtime!.RuntimeType);
                        IL.Emit(OpCodes.Call, Types.TypeGetTypeFromHandle);
                        IL.Emit(OpCodes.Call, _ctx.Runtime!.ExpandCallArgs);
                        IL.Emit(OpCodes.Call, _ctx.Runtime!.CreateArray);
                    }
                    else if (restArgsCount > 0)
                    {
                        // No spreads - simple array creation
                        IL.Emit(OpCodes.Ldc_I4, restArgsCount);
                        IL.Emit(OpCodes.Newarr, _ctx.Types.Object);
                        for (int i = 0; i < restArgsCount; i++)
                        {
                            IL.Emit(OpCodes.Dup);
                            IL.Emit(OpCodes.Ldc_I4, i);
                            EmitExpression(c.Arguments[regularCount + i]);
                            EmitBoxIfNeeded(c.Arguments[regularCount + i]);
                            IL.Emit(OpCodes.Stelem_Ref);
                        }
                        IL.Emit(OpCodes.Call, _ctx.Runtime!.CreateArray);
                    }
                    else
                    {
                        // No rest args - empty array
                        IL.Emit(OpCodes.Ldc_I4, 0);
                        IL.Emit(OpCodes.Newarr, _ctx.Types.Object);
                        IL.Emit(OpCodes.Call, _ctx.Runtime!.CreateArray);
                    }
                }
                else
                {
                    // No rest param - select overload based on argument count
                    // Check if we need to use an overload (fewer args than params)
                    if (c.Arguments.Count < paramCount &&
                        _ctx.FunctionOverloads != null &&
                        _ctx.FunctionOverloads.TryGetValue(resolvedFuncName, out var overloads))
                    {
                        // Find the overload matching our argument count
                        var matchingOverload = overloads.FirstOrDefault(o =>
                            o.GetParameters().Length == c.Arguments.Count);
                        if (matchingOverload != null)
                        {
                            targetMethod = matchingOverload;
                            paramCount = c.Arguments.Count; // Update param count for typed emission
                        }
                    }

                    // Get target parameter types for proper conversion
                    var targetParams = targetMethod.GetParameters();

                    // Emit arguments with proper type conversions
                    for (int i = 0; i < c.Arguments.Count; i++)
                    {
                        var arg = c.Arguments[i];
                        if (arg is Expr.Spread spread)
                        {
                            // Spread in non-rest function - just emit the expression
                            EmitExpression(spread.Expression);
                            EmitBoxIfNeeded(spread.Expression);
                        }
                        else
                        {
                            EmitExpression(arg);
                            // Convert to target parameter type
                            if (i < targetParams.Length)
                            {
                                EmitConversionForParameter(arg, targetParams[i].ParameterType);
                            }
                            else
                            {
                                EmitBoxIfNeeded(arg);
                            }
                        }
                    }

                    // Only pad with nulls if no matching overload was found
                    // and we still need more arguments
                    for (int i = c.Arguments.Count; i < paramCount; i++)
                    {
                        var paramType = targetParams[i].ParameterType;
                        EmitDefaultForType(paramType);
                    }
                }

                IL.Emit(OpCodes.Call, targetMethod);
                // Handle typed return values - box value types so callers receive object
                BoxReturnValueIfNeeded(targetMethod.ReturnType);
                return;
            }
        }

        // Inner function direct call (recursive self-calls and sibling inner function calls)
        if (c.Callee is Expr.Variable innerFuncVar &&
            _ctx.InnerFunctionMethodsByName?.TryGetValue(innerFuncVar.Name.Lexeme, out var innerMethod) == true)
        {
            EmitInnerFunctionDirectCall(innerFuncVar.Name.Lexeme, innerMethod, c.Arguments);
            return;
        }

        // Function value call (variable holding TSFunction, or direct arrow call)
        EmitFunctionValueCall(c);
    }

    private void EmitFunctionValueCall(Expr.Call c)
    {
        // Emit the callee and store in a local (may be $TSFunction, $BoundTSFunction, or other callable)
        var calleeLocal = IL.DeclareLocal(_ctx.Types.Object);
        EmitExpression(c.Callee);
        EmitBoxIfNeeded(c.Callee);
        IL.Emit(OpCodes.Stloc, calleeLocal);

        // Check if any argument is a spread
        bool hasSpreads = c.Arguments.Any(a => a is Expr.Spread);

        if (!hasSpreads)
        {
            // Simple case: no spreads
            IL.Emit(OpCodes.Ldc_I4, c.Arguments.Count);
            IL.Emit(OpCodes.Newarr, _ctx.Types.Object);

            for (int i = 0; i < c.Arguments.Count; i++)
            {
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldc_I4, i);
                EmitExpression(c.Arguments[i]);
                EmitBoxIfNeeded(c.Arguments[i]);
                IL.Emit(OpCodes.Stelem_Ref);
            }
        }
        else
        {
            // Complex case: has spreads, use ExpandCallArgs
            // First emit args array
            IL.Emit(OpCodes.Ldc_I4, c.Arguments.Count);
            IL.Emit(OpCodes.Newarr, _ctx.Types.Object);

            for (int i = 0; i < c.Arguments.Count; i++)
            {
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldc_I4, i);
                if (c.Arguments[i] is Expr.Spread spread)
                {
                    EmitExpression(spread.Expression);
                    EmitBoxIfNeeded(spread.Expression);
                }
                else
                {
                    EmitExpression(c.Arguments[i]);
                    EmitBoxIfNeeded(c.Arguments[i]);
                }
                IL.Emit(OpCodes.Stelem_Ref);
            }

            // Now emit isSpread bool array
            IL.Emit(OpCodes.Ldc_I4, c.Arguments.Count);
            IL.Emit(OpCodes.Newarr, _ctx.Types.Boolean);

            for (int i = 0; i < c.Arguments.Count; i++)
            {
                if (c.Arguments[i] is Expr.Spread)
                {
                    IL.Emit(OpCodes.Dup);
                    IL.Emit(OpCodes.Ldc_I4, i);
                    IL.Emit(OpCodes.Ldc_I4_1); // true
                    IL.Emit(OpCodes.Stelem_I1);
                }
            }

            // Pass Symbol.iterator and runtimeType for iterator protocol support
            IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.SymbolIterator);
            IL.Emit(OpCodes.Ldtoken, _ctx.Runtime!.RuntimeType);
            IL.Emit(OpCodes.Call, Types.TypeGetTypeFromHandle);

            // Call ExpandCallArgs
            IL.Emit(OpCodes.Call, _ctx.Runtime!.ExpandCallArgs);
        }

        // Call InvokeMethodValue(receiver, function, args)
        // This handles $TSFunction directly and falls back to InvokeValue for other callables
        // (like $BoundTSFunction which also has an Invoke method)
        var argsLocal = IL.DeclareLocal(_ctx.Types.ObjectArray);
        IL.Emit(OpCodes.Stloc, argsLocal);
        IL.Emit(OpCodes.Ldnull);  // receiver = null for non-method calls
        IL.Emit(OpCodes.Ldloc, calleeLocal);  // function
        IL.Emit(OpCodes.Ldloc, argsLocal);  // args
        IL.Emit(OpCodes.Call, _ctx.Runtime!.InvokeMethodValue);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits a direct call to an inner function method.
    /// For non-capturing inner functions, emits a static Call.
    /// For capturing inner functions (display class), emits Ldarg_0 + Callvirt on the Invoke method.
    /// Handles parameter type conversion (args are boxed objects, parameters may be typed).
    /// </summary>
    private void EmitInnerFunctionDirectCall(string funcName, MethodBuilder innerMethod, List<Expr> arguments)
    {
        bool isCapturing = _ctx.InnerFunctionDisplayClassesByName?.ContainsKey(funcName) == true;

        if (isCapturing)
        {
            // Capturing: the inner function is an instance method on the display class.
            // Load 'this' (display class instance) as the call target.
            IL.Emit(OpCodes.Ldarg_0);
        }

        // Get parameter info for type conversion
        var parameters = innerMethod.GetParameters();

        // Emit arguments with proper type conversion
        for (int i = 0; i < arguments.Count; i++)
        {
            EmitExpression(arguments[i]);
            if (i < parameters.Length)
            {
                EmitConversionForParameter(arguments[i], parameters[i].ParameterType);
            }
            else
            {
                EmitBoxIfNeeded(arguments[i]);
            }
        }

        // Pad missing arguments with defaults
        for (int i = arguments.Count; i < parameters.Length; i++)
        {
            EmitDefaultForType(parameters[i].ParameterType);
        }

        if (isCapturing)
        {
            IL.Emit(OpCodes.Callvirt, innerMethod);
        }
        else
        {
            IL.Emit(OpCodes.Call, innerMethod);
        }

        // Return type is always object for inner functions
        SetStackUnknown();
    }

    /// <summary>
    /// Resolves a type argument string to a .NET Type for generic instantiation.
    /// </summary>
    private Type ResolveTypeArg(string typeArg)
    {
        return typeArg switch
        {
            "number" => _ctx.Types.Double,
            "string" => _ctx.Types.String,
            "boolean" => _ctx.Types.Boolean,
            _ when _ctx.GenericTypeParameters.TryGetValue(typeArg, out var gp) => gp,
            _ when _ctx.Classes.TryGetValue(_ctx.ResolveClassName(typeArg), out var tb) => tb,
            _ => _ctx.Types.Object
        };
    }
}
