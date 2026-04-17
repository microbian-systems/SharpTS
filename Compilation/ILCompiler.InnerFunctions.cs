using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// Inner function declaration collection, definition, and emission for the IL compiler.
/// Treats inner function declarations like arrow functions: non-capturing ones become
/// static methods, capturing ones get display classes. Supports hoisting (function
/// declarations are available before their textual position in the source).
/// </summary>
public partial class ILCompiler
{
    // Inner function tracking (keyed by Stmt.Function reference identity)
    private readonly List<(Stmt.Function Func, HashSet<string> Captures, string EnclosingFunctionName)> _collectedInnerFunctions = [];
    private readonly Dictionary<Stmt.Function, MethodBuilder> _innerFunctionMethods = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Stmt.Function, TypeBuilder> _innerFunctionDisplayClasses = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Stmt.Function, Dictionary<string, FieldBuilder>> _innerFunctionDCFields = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Stmt.Function, ConstructorBuilder> _innerFunctionDCCtors = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Stmt.Function, FieldBuilder> _innerFunctionEntryPointDCFields = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Stmt.Function, FieldBuilder> _innerFunctionFunctionDCFields = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Stmt.Function, Type[]> _innerFunctionParamTypes = new(ReferenceEqualityComparer.Instance);

    // Nesting depth tracker: 0 = top level, 1+ = inside a function body
    private int _functionNestingDepth;

    // Tracks which enclosing function name each inner function belongs to
    private string? _currentEnclosingFunctionName;

    /// <summary>
    /// Collects an inner function declaration found during arrow function collection.
    /// Called from CollectArrowsFromStmt when nesting depth > 0.
    /// </summary>
    private void CollectInnerFunction(Stmt.Function funcStmt)
    {
        var captures = new HashSet<string>(_closures.Analyzer.GetCaptures(funcStmt));
        // Remove self-reference: the function's own name is not a true capture.
        // Self-calls are handled via InnerFunctionMethodsByName direct dispatch.
        captures.Remove(funcStmt.Name.Lexeme);
        _collectedInnerFunctions.Add((funcStmt, captures, _currentEnclosingFunctionName!));
    }

    /// <summary>
    /// Defines methods and display classes for all collected inner functions.
    /// Non-capturing inner functions become static methods on $Program.
    /// Capturing ones get display classes with Invoke methods, mirroring the arrow pattern.
    /// </summary>
    private void DefineInnerFunctions()
    {
        foreach (var (func, captures, enclosingFuncName) in _collectedInnerFunctions)
        {
            // Resolve parameter types (use annotations only, no TypeMap function type)
            var resolvedParamTypes = ParameterTypeResolver.ResolveParameters(
                func.Parameters, _typeMapper, null);

            Type[] paramTypes = new Type[func.Parameters.Count];
            for (int i = 0; i < func.Parameters.Count; i++)
                paramTypes[i] = func.Parameters[i].IsRest ? _types.ListOfObject : resolvedParamTypes[i];

            // Store resolved param types for use during body emission
            _innerFunctionParamTypes[func] = resolvedParamTypes;

            // Return type is always object for inner functions (no TypeMap lookup)
            Type returnType = _types.Object;

            // Check if this inner function captures function-level variables
            bool needsFunctionDC = false;
            string? sourceFunctionForDC = null;
            if (_closures.FunctionDisplayClassFields.TryGetValue(enclosingFuncName, out var enclosingFuncDCFields))
            {
                if (captures.Any(c => enclosingFuncDCFields.ContainsKey(c)))
                {
                    needsFunctionDC = true;
                    sourceFunctionForDC = enclosingFuncName;
                }
            }

            // Check if any captured vars are top-level captured vars
            bool needsEntryPointDC = _closures.EntryPointDisplayClass != null &&
                captures.Any(c => _closures.CapturedTopLevelVars.Contains(c));

            if (captures.Count == 0 && !needsFunctionDC)
            {
                // Non-capturing: static method on $Program
                var methodBuilder = _programType.DefineMethod(
                    $"<>InnerFunc_{_closures.ArrowMethodCounter++}_{func.Name.Lexeme}",
                    MethodAttributes.Private | MethodAttributes.Static,
                    returnType,
                    paramTypes
                );

                for (int i = 0; i < func.Parameters.Count; i++)
                    methodBuilder.DefineParameter(i + 1, ParameterAttributes.None, func.Parameters[i].Name.Lexeme);

                _innerFunctionMethods[func] = methodBuilder;
            }
            else
            {
                // Capturing: create display class
                var displayClass = _moduleBuilder.DefineType(
                    $"<>c__InnerFuncDC{_closures.DisplayClassCounter++}_{func.Name.Lexeme}",
                    TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                    _types.Object
                );

                // Add fields for captured variables
                Dictionary<string, FieldBuilder> fieldMap = [];
                FieldBuilder? entryPointDCField = null;
                FieldBuilder? functionDCField = null;

                if (needsEntryPointDC)
                {
                    entryPointDCField = displayClass.DefineField("$entryPointDC", _closures.EntryPointDisplayClass!, FieldAttributes.Public);
                }

                if (needsFunctionDC && sourceFunctionForDC != null &&
                    _closures.FunctionDisplayClasses.TryGetValue(sourceFunctionForDC, out var funcDC))
                {
                    functionDCField = displayClass.DefineField("$functionDC", funcDC, FieldAttributes.Public);
                }

                foreach (var capturedVar in captures)
                {
                    // Skip top-level captured vars - accessed through $entryPointDC
                    if (_closures.CapturedTopLevelVars.Contains(capturedVar))
                        continue;

                    // Skip function-level captured vars - accessed through $functionDC
                    if (needsFunctionDC && sourceFunctionForDC != null &&
                        _closures.FunctionDisplayClassFields.TryGetValue(sourceFunctionForDC, out var funcFields) &&
                        funcFields.ContainsKey(capturedVar))
                        continue;

                    var field = displayClass.DefineField(capturedVar, _types.Object, FieldAttributes.Public);
                    fieldMap[capturedVar] = field;
                }

                _innerFunctionDCFields[func] = fieldMap;

                if (entryPointDCField != null)
                    _innerFunctionEntryPointDCFields[func] = entryPointDCField;

                if (functionDCField != null)
                    _innerFunctionFunctionDCFields[func] = functionDCField;

                // Default constructor
                var ctorBuilder = displayClass.DefineConstructor(
                    MethodAttributes.Public,
                    CallingConventions.Standard,
                    Type.EmptyTypes
                );
                var ctorIL = ctorBuilder.GetILGenerator();
                ctorIL.Emit(OpCodes.Ldarg_0);
                ctorIL.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.Object));
                ctorIL.Emit(OpCodes.Ret);
                _innerFunctionDCCtors[func] = ctorBuilder;

                // Invoke method on display class
                var invokeMethod = displayClass.DefineMethod(
                    "Invoke",
                    MethodAttributes.Public,
                    returnType,
                    paramTypes
                );

                for (int i = 0; i < func.Parameters.Count; i++)
                    invokeMethod.DefineParameter(i + 1, ParameterAttributes.None, func.Parameters[i].Name.Lexeme);

                _innerFunctionDisplayClasses[func] = displayClass;
                _innerFunctionMethods[func] = invokeMethod;
            }
        }
    }

    /// <summary>
    /// Emits method bodies for all collected inner functions.
    /// Follows the same pattern as EmitArrowBody.
    /// </summary>
    private void EmitInnerFunctionBodies()
    {
        foreach (var (func, captures, enclosingFuncName) in _collectedInnerFunctions)
        {
            var method = _innerFunctionMethods[func];
            var hasDisplayClass = _innerFunctionDisplayClasses.TryGetValue(func, out var displayClass);

            var il = ((MethodBuilder)method).GetILGenerator();

            // Get resolved parameter types for this inner function
            _innerFunctionParamTypes.TryGetValue(func, out var innerParamTypes);

            var ctx = new CompilationContext(il, _typeMapper, _functions.Builders, _classes.Builders, _types)
            {
                ClosureAnalyzer = _closures.Analyzer,
                ArrowMethods = _closures.ArrowMethods,
                DisplayClasses = _closures.DisplayClasses,
                DisplayClassFields = _closures.DisplayClassFields,
                DisplayClassConstructors = _closures.DisplayClassConstructors,
                FunctionRestParams = _functions.RestParams,
                EnumMembers = _enums.Members,
                EnumReverse = _enums.Reverse,
                EnumKinds = _enums.Kinds,
                Runtime = _runtime,
                FunctionGenericParams = _functions.GenericParams,
                IsGenericFunction = _functions.IsGeneric,
                TypeMap = _typeMap,
                DeadCode = _deadCodeInfo,
                TopLevelStaticVars = BuildTopLevelStaticVarsForModule(_modules.CurrentPath),
                CurrentModulePath = _modules.CurrentPath,
                ClassToModule = _modules.ClassToModule,
                FunctionToModule = _modules.FunctionToModule,
                EnumToModule = _modules.EnumToModule,
                DotNetNamespace = _modules.CurrentDotNetNamespace,
                TypeEmitterRegistry = _typeEmitterRegistry,
                BuiltInModuleEmitterRegistry = _builtInModuleEmitterRegistry,
                BuiltInModuleNamespaces = _builtInModuleNamespaces,
                BuiltInModuleMethodBindings = _builtInModuleMethodBindings,
                ClassExprBuilders = _classExprs.Builders,
                IsStrictMode = _isStrictMode,
                ClassRegistry = GetClassRegistry(),
                EntryPointDisplayClassFields = _closures.EntryPointDisplayClassFields.Count > 0 ? _closures.EntryPointDisplayClassFields : null,
                CapturedTopLevelVars = _closures.CapturedTopLevelVars.Count > 0 ? _closures.CapturedTopLevelVars : null,
                ArrowEntryPointDCFields = _closures.ArrowEntryPointDCFields.Count > 0 ? _closures.ArrowEntryPointDCFields : null,
                EntryPointDisplayClassStaticField = _closures.EntryPointDisplayClassStaticField,
                ArrowFunctionDCFields = _closures.ArrowFunctionDCFields.Count > 0 ? _closures.ArrowFunctionDCFields : null,
                ArrowScopeDCFields = _closures.ArrowScopeDCFields.Count > 0 ? _closures.ArrowScopeDCFields : null,
                InnerFunctionMethods = _innerFunctionMethods,
                InnerFunctionDisplayClasses = _innerFunctionDisplayClasses,
                InnerFunctionDCFields = _innerFunctionDCFields,
                InnerFunctionDCCtors = _innerFunctionDCCtors,
                InnerFunctionEntryPointDCFields = _innerFunctionEntryPointDCFields,
                InnerFunctionFunctionDCFields = _innerFunctionFunctionDCFields,
                CurrentMethodReturnType = _types.Object,
                // Enable self-reference: the function can call itself by name
                InnerFunctionMethodsByName = new Dictionary<string, MethodBuilder>
                {
                    [func.Name.Lexeme] = method
                },
                InnerFunctionDisplayClassesByName = hasDisplayClass
                    ? new Dictionary<string, TypeBuilder> { [func.Name.Lexeme] = displayClass! }
                    : []
            };

            if (hasDisplayClass)
            {
                // Instance method on display class - this is arg 0
                ctx.IsInstanceMethod = true;

                if (_innerFunctionDCFields.TryGetValue(func, out var fieldMap))
                    ctx.CapturedFields = fieldMap;
                else
                    ctx.CapturedFields = [];

                // Set $entryPointDC field
                if (_innerFunctionEntryPointDCFields.TryGetValue(func, out var epDCField))
                    ctx.CurrentArrowEntryPointDCField = epDCField;

                // Set $functionDC field
                if (_innerFunctionFunctionDCFields.TryGetValue(func, out var funcDCField))
                {
                    ctx.CurrentArrowFunctionDCField = funcDCField;

                    // Set up captured function locals info
                    bool needsFunctionDC = false;
                    string? sourceFuncName = null;
                    if (_closures.FunctionDisplayClassFields.TryGetValue(enclosingFuncName, out var enclosingDCFields))
                    {
                        if (captures.Any(c => enclosingDCFields.ContainsKey(c)))
                        {
                            needsFunctionDC = true;
                            sourceFuncName = enclosingFuncName;
                        }
                    }

                    if (needsFunctionDC && sourceFuncName != null &&
                        _closures.FunctionDisplayClassFields.TryGetValue(sourceFuncName, out var funcDCFields))
                    {
                        ctx.FunctionDisplayClassFields = funcDCFields;
                        ctx.CapturedFunctionLocals = [.. funcDCFields.Keys];
                    }
                }

                // Parameters start at index 1 (display class is arg 0)
                for (int i = 0; i < func.Parameters.Count; i++)
                {
                    Type? paramType = innerParamTypes != null && i < innerParamTypes.Length ? innerParamTypes[i] : null;
                    ctx.DefineParameter(func.Parameters[i].Name.Lexeme, i + 1, paramType);
                }
            }
            else
            {
                // Static method - parameters start at index 0
                for (int i = 0; i < func.Parameters.Count; i++)
                {
                    Type? paramType = innerParamTypes != null && i < innerParamTypes.Length ? innerParamTypes[i] : null;
                    ctx.DefineParameter(func.Parameters[i].Name.Lexeme, i, paramType);
                }
            }

            var emitter = new ILEmitter(ctx);

            // Emit default parameter checks
            emitter.EmitDefaultParameters(func.Parameters, hasDisplayClass, hasOwnThis: false);

            // Emit function body
            if (func.Body != null)
            {
                // Hoist inner functions within this inner function's body
                EmitInnerFunctionHoisting(il, ctx, func.Body);

                emitter.EmitStatements(func.Body);

                if (emitter.HasDeferredReturns)
                {
                    emitter.FinalizeReturns();
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Ret);
                }
            }
            else
            {
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
            }
        }
    }

    /// <summary>
    /// Emits hoisting code at the start of a function body for inner function declarations.
    /// Scans the body for Stmt.Function nodes and creates TSFunction locals for each.
    /// Also populates name-based lookup maps on the context for direct call dispatch.
    /// This implements JavaScript function declaration hoisting semantics.
    /// </summary>
    private void EmitInnerFunctionHoisting(ILGenerator il, CompilationContext ctx, List<Stmt> body)
    {
        foreach (var stmt in body)
        {
            if (stmt is not Stmt.Function funcStmt) continue;
            if (funcStmt.Body == null) continue; // Skip overload signatures
            if (!_innerFunctionMethods.TryGetValue(funcStmt, out var method)) continue;

            var funcName = funcStmt.Name.Lexeme;

            // Check if this inner function's name is stored in the enclosing function's display class.
            // This happens when the inner function references itself (self-reference is seen as a
            // captured outer variable by ClosureAnalyzer because the function name is declared
            // in the enclosing scope). We need to store the TSFunction in the DC field so that
            // LocalVariableResolver (which checks DC fields before locals) finds it correctly.
            FieldBuilder? funcDCStoreField = null;
            bool storeInFunctionDC = ctx.CapturedFunctionLocals?.Contains(funcName) == true &&
                ctx.FunctionDisplayClassFields?.TryGetValue(funcName, out funcDCStoreField) == true &&
                ctx.FunctionDisplayClassLocal != null;

            // Also declare a regular local (used when not stored in DC, or as fallback)
            LocalBuilder? local = null;
            if (!storeInFunctionDC)
                local = ctx.Locals.DeclareLocal(funcName, _types.Object);

            if (_innerFunctionDisplayClasses.TryGetValue(funcStmt, out var displayClass))
            {
                // Capturing: create display class instance, populate fields, create TSFunction
                var ctor = _innerFunctionDCCtors[funcStmt];
                il.Emit(OpCodes.Newobj, ctor);

                // Populate $entryPointDC field
                if (_innerFunctionEntryPointDCFields.TryGetValue(funcStmt, out var epDCField))
                {
                    if (ctx.EntryPointDisplayClassLocal != null)
                    {
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Ldloc, ctx.EntryPointDisplayClassLocal);
                        il.Emit(OpCodes.Stfld, epDCField);
                    }
                    else if (ctx.EntryPointDisplayClassStaticField != null)
                    {
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Ldsfld, ctx.EntryPointDisplayClassStaticField);
                        il.Emit(OpCodes.Stfld, epDCField);
                    }
                }

                // Populate $functionDC field
                if (_innerFunctionFunctionDCFields.TryGetValue(funcStmt, out var funcDCField))
                {
                    if (ctx.FunctionDisplayClassLocal != null)
                    {
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Ldloc, ctx.FunctionDisplayClassLocal);
                        il.Emit(OpCodes.Stfld, funcDCField);
                    }
                }

                // Populate captured variable fields
                if (_innerFunctionDCFields.TryGetValue(funcStmt, out var fieldMap))
                {
                    foreach (var (capturedVar, field) in fieldMap)
                    {
                        il.Emit(OpCodes.Dup);

                        if (ctx.TryGetParameter(capturedVar, out var argIndex))
                        {
                            il.Emit(OpCodes.Ldarg, argIndex);
                            if (ctx.TryGetParameterType(capturedVar, out var paramType) && paramType != null && paramType.IsValueType)
                                il.Emit(OpCodes.Box, paramType);
                        }
                        else if (ctx.CapturedFields != null && ctx.CapturedFields.TryGetValue(capturedVar, out var capturedField))
                        {
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldfld, capturedField);
                        }
                        else if (ctx.CapturedTopLevelVars?.Contains(capturedVar) == true &&
                                 ctx.EntryPointDisplayClassFields?.TryGetValue(capturedVar, out var epField) == true)
                        {
                            if (ctx.EntryPointDisplayClassLocal != null)
                                il.Emit(OpCodes.Ldloc, ctx.EntryPointDisplayClassLocal);
                            else if (ctx.EntryPointDisplayClassStaticField != null)
                                il.Emit(OpCodes.Ldsfld, ctx.EntryPointDisplayClassStaticField);
                            else
                                il.Emit(OpCodes.Ldnull);
                            il.Emit(OpCodes.Ldfld, epField);
                        }
                        else if (ctx.CapturedFunctionLocals?.Contains(capturedVar) == true &&
                                 ctx.FunctionDisplayClassFields?.TryGetValue(capturedVar, out var funcField) == true)
                        {
                            if (ctx.FunctionDisplayClassLocal != null)
                            {
                                il.Emit(OpCodes.Ldloc, ctx.FunctionDisplayClassLocal);
                                il.Emit(OpCodes.Ldfld, funcField);
                            }
                            else
                            {
                                il.Emit(OpCodes.Ldnull);
                            }
                        }
                        else if (ctx.TopLevelStaticVars != null && ctx.TopLevelStaticVars.TryGetValue(capturedVar, out var topField))
                        {
                            il.Emit(OpCodes.Ldsfld, topField);
                        }
                        else
                        {
                            var existingLocal = ctx.Locals.GetLocal(capturedVar);
                            if (existingLocal != null)
                                il.Emit(OpCodes.Ldloc, existingLocal);
                            else
                                il.Emit(OpCodes.Ldnull);
                        }

                        il.Emit(OpCodes.Stfld, field);
                    }
                }

                // Create TSFunction: new TSFunction(displayInstance, invokeMethod)
                // Stack has: displayInstance
                il.Emit(OpCodes.Ldtoken, method);
                il.Emit(OpCodes.Ldtoken, displayClass);
                il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
                il.Emit(OpCodes.Castclass, _types.MethodInfo);
                il.Emit(OpCodes.Newobj, _runtime.TSFunctionCtor);
            }
            else
            {
                // Non-capturing: new TSFunction(null, staticMethod)
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ldtoken, method);
                il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle));
                il.Emit(OpCodes.Castclass, _types.MethodInfo);
                il.Emit(OpCodes.Newobj, _runtime.TSFunctionCtor);
            }

            // Store TSFunction in the appropriate location
            if (storeInFunctionDC)
            {
                // Store in the enclosing function's display class field
                var temp = il.DeclareLocal(_types.Object);
                il.Emit(OpCodes.Stloc, temp);
                il.Emit(OpCodes.Ldloc, ctx.FunctionDisplayClassLocal!);
                il.Emit(OpCodes.Ldloc, temp);
                il.Emit(OpCodes.Stfld, funcDCStoreField!);
            }
            else
            {
                // Store in local variable
                il.Emit(OpCodes.Stloc, local!);
            }
        }
    }

    /// <summary>
    /// Finalizes inner function display class types.
    /// </summary>
    private void FinalizeInnerFunctionDisplayClasses()
    {
        foreach (var tb in _innerFunctionDisplayClasses.Values)
        {
            tb.CreateType();
        }
    }
}
