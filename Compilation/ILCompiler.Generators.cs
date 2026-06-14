using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// Generator function compilation for the IL compiler.
/// Handles the definition and emission of generator state machines.
/// </summary>
public partial class ILCompiler
{
    /// <summary>
    /// Top-level (module-scope) function names, keyed by module path ("" for single-file).
    /// A generator references a top-level function by treating its name as a captured outer variable
    /// (it is not declared inside the generator). Generators wrongly materialise such captures as
    /// state-machine fields that the kickoff never populates for functions — leaving them null, so a
    /// value-use (<c>const h = helper; h()</c> / <c>yield helper</c>) reads null and throws "object
    /// is not a function". Async/async-generator bodies don't create these fields and instead resolve
    /// the name through the normal function-value path. Excluding such names from the generator's
    /// captured fields lets its MoveNext resolver fall through to <c>TryEmitGlobalVariable</c>'s
    /// function path too.
    ///
    /// <para>Keyed PER MODULE, not unioned: a name that is a top-level function in one module may be
    /// a genuinely-captured binding (e.g. a module-level <c>const</c>, whose captured field IS
    /// populated) in another. Excluding it there would strip a needed field. Only a name that is a
    /// top-level function in the GENERATOR'S OWN module resolves correctly through the function-value
    /// fallback, so only those are excluded.</para>
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _topLevelFunctionNamesByModule = new();

    /// <summary>
    /// Records the top-level function names in <paramref name="statements"/> under
    /// <paramref name="moduleKey"/> (<c>""</c> for single-file). Called once per module after
    /// nested-function lifting, so relocated functions (now top-level) are included.
    /// </summary>
    private void CollectTopLevelFunctionNames(IEnumerable<Stmt> statements, string moduleKey)
    {
        if (!_topLevelFunctionNamesByModule.TryGetValue(moduleKey, out var set))
        {
            set = new HashSet<string>();
            _topLevelFunctionNamesByModule[moduleKey] = set;
        }
        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case Stmt.Function f when f.Body != null:
                    set.Add(f.Name.Lexeme);
                    break;
                case Stmt.Export { Declaration: Stmt.Function ef } when ef.Body != null:
                    set.Add(ef.Name.Lexeme);
                    break;
            }
        }
    }

    /// <summary>
    /// Returns <paramref name="analysis"/> with the current module's top-level function names removed
    /// from its captured variables, so the generator state machine does not define (never-populated)
    /// fields for them. See <see cref="_topLevelFunctionNamesByModule"/>.
    /// </summary>
    private GeneratorStateAnalyzer.GeneratorFunctionAnalysis ExcludeTopLevelFunctionsFromCaptures(
        GeneratorStateAnalyzer.GeneratorFunctionAnalysis analysis)
    {
        if (!_topLevelFunctionNamesByModule.TryGetValue(_modules.CurrentPath ?? "", out var names)
            || names.Count == 0)
            return analysis;
        if (!analysis.CapturedVariables.Overlaps(names)) return analysis;
        var filtered = new HashSet<string>(analysis.CapturedVariables);
        filtered.ExceptWith(names);
        return analysis with { CapturedVariables = filtered };
    }

    /// <summary>
    /// Defines a generator function and its state machine.
    /// </summary>
    private void DefineGeneratorFunction(Stmt.Function funcStmt)
    {
        string funcName = funcStmt.Name.Lexeme;

        // Module-qualify the stub/registry keys (#418) so two modules that each declare a
        // same-named generator function don't clobber each other. Single-file compilation
        // returns the simple name unchanged. The readable state-machine type name keeps the
        // simple name (the builder's counter already disambiguates `<name>d__N`).
        string qualifiedName = GetDefinitionContext().GetQualifiedFunctionName(funcName);

        // Analyze the generator function for yield points and hoisted variables
        var analysis = _generators.Analyzer.Analyze(funcStmt);

        // Create the state machine builder
        var smBuilder = new GeneratorStateMachineBuilder(_moduleBuilder, _types, _generators.StateMachineCounter++);
        smBuilder.DefineStateMachine(funcName, ExcludeTopLevelFunctionsFromCaptures(analysis), isInstanceMethod: false, runtime: _runtime);

        _generators.StateMachines[qualifiedName] = smBuilder;
        _generators.Functions[qualifiedName] = funcStmt;

        // Define the stub method that creates and returns the state machine.
        // A trailing rest parameter is typed List<object> so the indirect
        // ($TSFunction.Invoke) call path packs trailing args into it (#426).
        var paramTypes = BuildStateMachineStubParamTypes(funcStmt);
        var methodBuilder = _programType.DefineMethod(
            qualifiedName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.IEnumerableOfObject,  // Generator returns IEnumerable<object>
            paramTypes
        );

        _functions.Builders[qualifiedName] = methodBuilder;

        // Track rest parameter info (keyed by the qualified name so ResolveFunctionName-based
        // call-site lookups in ExpressionEmitterBase find it).
        var restParam = funcStmt.Parameters.FirstOrDefault(p => p.IsRest);
        if (restParam != null)
        {
            int restIndex = funcStmt.Parameters.IndexOf(restParam);
            int regularCount = funcStmt.Parameters.Count(p => !p.IsRest);
            _functions.RestParams[qualifiedName] = (restIndex, regularCount);
        }
    }

    /// <summary>
    /// Emits all generator state machine bodies.
    /// Called after all functions have been defined.
    /// </summary>
    private void EmitGeneratorStateMachineBodies()
    {
        var savedPath = _modules.CurrentPath;
        foreach (var (funcName, smBuilder) in _generators.StateMachines)
        {
            if (_functionDefinitionModule.TryGetValue(funcName, out var fnModule))
            {
                _modules.CurrentPath = NormalizeToEmissionPath(fnModule);
            }
            var funcStmt = _generators.Functions[funcName];
            var methodBuilder = _functions.Builders[funcName];
            var analysis = _generators.Analyzer.Analyze(funcStmt);

            // Emit the stub method body (creates and returns the state machine)
            EmitGeneratorStubMethod(methodBuilder, smBuilder, funcStmt, analysis);

            // Emit the MoveNext method body
            EmitGeneratorMoveNextBody(smBuilder, funcStmt);

            // Finalize the state machine type
            smBuilder.CreateType();
        }
        _modules.CurrentPath = savedPath;
    }

    /// <summary>
    /// Emits the stub method that creates and initializes the generator state machine.
    /// </summary>
    private void EmitGeneratorStubMethod(
        MethodBuilder methodBuilder,
        GeneratorStateMachineBuilder smBuilder,
        Stmt.Function funcStmt,
        GeneratorStateAnalyzer.GeneratorFunctionAnalysis analysis)
    {
        var il = methodBuilder.GetILGenerator();

        // Create new instance of the state machine using the constructor builder
        il.Emit(OpCodes.Newobj, smBuilder.Constructor);

        // Copy parameters to state machine fields
        for (int i = 0; i < funcStmt.Parameters.Count; i++)
        {
            var paramName = funcStmt.Parameters[i].Name.Lexeme;
            var field = smBuilder.GetVariableField(paramName);
            if (field != null)
            {
                il.Emit(OpCodes.Dup);  // Keep state machine reference on stack
                il.Emit(OpCodes.Ldarg, i);
                il.Emit(OpCodes.Stfld, field);
            }
        }

        // Copy captured outer scope variables to state machine fields
        var moduleCapturedVars = BuildCapturedTopLevelVarsForModule(_modules.CurrentPath);
        var moduleEntryPointFields = BuildEntryPointDisplayClassFieldsForModule(_modules.CurrentPath);
        foreach (var capturedVar in analysis.CapturedVariables)
        {
            var capturedField = smBuilder.CapturedVariables.GetValueOrDefault(capturedVar);
            if (capturedField == null) continue;

            // Try to load from entry-point display class (captured top-level variables)
            if (moduleCapturedVars?.Contains(capturedVar) == true &&
                moduleEntryPointFields?.TryGetValue(capturedVar, out var entryPointField) == true)
            {
                il.Emit(OpCodes.Dup);  // Keep state machine reference on stack
                if (_closures.EntryPointDisplayClassStaticField != null)
                {
                    il.Emit(OpCodes.Ldsfld, _closures.EntryPointDisplayClassStaticField);
                    il.Emit(OpCodes.Ldfld, entryPointField);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Stfld, capturedField);
            }
            // Try to load from top-level static vars (non-captured module-level variables)
            else if (_topLevelStaticVars.TryGetValue(capturedVar, out var staticField))
            {
                il.Emit(OpCodes.Dup);  // Keep state machine reference on stack
                il.Emit(OpCodes.Ldsfld, staticField);
                il.Emit(OpCodes.Stfld, capturedField);
            }
        }

        // Return the state machine (which implements IEnumerable<object>)
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the MoveNext method body for a generator state machine.
    /// Uses GeneratorMoveNextEmitter to handle full generator body with yield expressions.
    /// </summary>
    private void EmitGeneratorMoveNextBody(GeneratorStateMachineBuilder smBuilder, Stmt.Function funcStmt)
    {
        var analysis = _generators.Analyzer.Analyze(funcStmt);

        // Create a compilation context for the state machine
        var il = smBuilder.MoveNextMethod.GetILGenerator();
        var ctx = new CompilationContext(il, _typeMapper, _functions.Builders, _classes.Builders, _types)
        {
            ClosureAnalyzer = _closures.Analyzer,
            ArrowMethods = _closures.ArrowMethods,
            ConstArrowBindings = _closures.ConstArrowBindings,
            DisplayClasses = _closures.DisplayClasses,
            DisplayClassFields = _closures.DisplayClassFields,
            DisplayClassConstructors = _closures.DisplayClassConstructors,
            FunctionRestParams = _functions.RestParams,
            FunctionsCapturingArguments = _functions.CapturingArguments,
            EnumMembers = _enums.Members,
            EnumReverse = _enums.Reverse,
            EnumKinds = _enums.Kinds,
            Runtime = _runtime,
            FunctionGenericParams = _functions.GenericParams,
            IsGenericFunction = _functions.IsGeneric,
            TypeMap = _typeMap,
            DeadCode = _deadCodeInfo,
            AsyncMethods = null,
            // Module support for multi-module compilation
            CurrentModulePath = _modules.CurrentPath,
            ClassToModule = _modules.ClassToModule,
            FunctionToModule = _modules.FunctionToModule,
            EnumToModule = _modules.EnumToModule,
            DotNetNamespace = _modules.CurrentDotNetNamespace,
            TypeEmitterRegistry = _typeEmitterRegistry,
            BuiltInModuleEmitterRegistry = _builtInModuleEmitterRegistry,
            BuiltInModuleNamespaces = _builtInModuleNamespaces,
            BuiltInModuleMethodBindings = GetCurrentBuiltInMethodBindings(),
            ImportedNames = _importedNames,
            ClassExprBuilders = _classExprs.Builders,
            // Check for function-level "use strict" directive
            IsStrictMode = _isStrictMode || CheckForUseStrict(funcStmt.Body),
            // Registry services
            ClassRegistry = GetClassRegistry()
        };

        // Use the new emitter for full generator body emission
        var emitter = new GeneratorMoveNextEmitter(smBuilder, analysis, _types);
        emitter.EmitMoveNext(funcStmt.Body, ctx);

        ILLabelValidator.Validate(smBuilder.MoveNextMethod.GetILGenerator(),
            $"generator MoveNext {funcStmt.Name.Lexeme}");
    }

    /// <summary>
    /// Emits the body of an instance generator method using a state machine.
    /// Called for class methods marked with IsGenerator = true.
    /// </summary>
    private void EmitGeneratorMethodBody(MethodBuilder methodBuilder, Stmt.Function method, FieldInfo fieldsField)
    {
        // Analyze generator function to determine yield points and hoisted variables
        var analysis = _generators.Analyzer.Analyze(method);

        // Build state machine type for instance method
        var smBuilder = new GeneratorStateMachineBuilder(_moduleBuilder, _types, _generators.StateMachineCounter++);
        smBuilder.DefineStateMachine(
            $"{methodBuilder.DeclaringType!.Name}_{method.Name.Lexeme}",
            ExcludeTopLevelFunctionsFromCaptures(analysis),
            isInstanceMethod: true,  // This is an instance method
            runtime: _runtime
        );

        // Emit stub method body (creates state machine and returns it)
        EmitGeneratorInstanceStubMethod(methodBuilder, smBuilder, method.Parameters, analysis);

        // Create context for MoveNext emission
        var il = smBuilder.MoveNextMethod.GetILGenerator();
        var ctx = new CompilationContext(il, _typeMapper, _functions.Builders, _classes.Builders, _types)
        {
            FieldsField = fieldsField,
            IsInstanceMethod = true,
            ClosureAnalyzer = _closures.Analyzer,
            ArrowMethods = _closures.ArrowMethods,
            ConstArrowBindings = _closures.ConstArrowBindings,
            DisplayClasses = _closures.DisplayClasses,
            DisplayClassFields = _closures.DisplayClassFields,
            DisplayClassConstructors = _closures.DisplayClassConstructors,
            FunctionRestParams = _functions.RestParams,
            FunctionsCapturingArguments = _functions.CapturingArguments,
            EnumMembers = _enums.Members,
            EnumReverse = _enums.Reverse,
            EnumKinds = _enums.Kinds,
            Runtime = _runtime,
            FunctionGenericParams = _functions.GenericParams,
            IsGenericFunction = _functions.IsGeneric,
            TypeMap = _typeMap,
            DeadCode = _deadCodeInfo,
            AsyncMethods = null,
            // Module support for multi-module compilation
            CurrentModulePath = _modules.CurrentPath,
            ClassToModule = _modules.ClassToModule,
            FunctionToModule = _modules.FunctionToModule,
            EnumToModule = _modules.EnumToModule,
            DotNetNamespace = _modules.CurrentDotNetNamespace,
            TypeEmitterRegistry = _typeEmitterRegistry,
            BuiltInModuleEmitterRegistry = _builtInModuleEmitterRegistry,
            BuiltInModuleNamespaces = _builtInModuleNamespaces,
            BuiltInModuleMethodBindings = GetCurrentBuiltInMethodBindings(),
            ImportedNames = _importedNames,
            ClassExprBuilders = _classExprs.Builders,
            IsStrictMode = _isStrictMode || CheckForUseStrict(method.Body),
            // ES2022 Private Class Elements support for generator methods
            CurrentClassName = methodBuilder.DeclaringType?.Name,
            CurrentClassBuilder = methodBuilder.DeclaringType as TypeBuilder,
            // Registry services
            ClassRegistry = GetClassRegistry(),
            // Entry-point display class for captured top-level variables
            EntryPointDisplayClassFields = BuildEntryPointDisplayClassFieldsForModule(_modules.CurrentPath),
            CapturedTopLevelVars = BuildCapturedTopLevelVarsForModule(_modules.CurrentPath),
            EntryPointDisplayClassStaticField = _closures.EntryPointDisplayClassStaticField
        };

        // Emit MoveNext body
        var moveNextEmitter = new GeneratorMoveNextEmitter(smBuilder, analysis, _types);
        moveNextEmitter.EmitMoveNext(method.Body, ctx);

        ILLabelValidator.Validate(il,
            $"generator method MoveNext {methodBuilder.DeclaringType?.Name}::{method.Name.Lexeme}");

        // Finalize the state machine type
        smBuilder.CreateType();
    }

    /// <summary>
    /// Emits the stub method that creates and initializes the generator state machine for an instance method.
    /// The stub copies 'this' and parameters to the state machine, then returns it.
    /// </summary>
    private void EmitGeneratorInstanceStubMethod(
        MethodBuilder methodBuilder,
        GeneratorStateMachineBuilder smBuilder,
        List<Stmt.Parameter> parameters,
        GeneratorStateAnalyzer.GeneratorFunctionAnalysis analysis)
    {
        var il = methodBuilder.GetILGenerator();

        // Create new instance of the state machine
        il.Emit(OpCodes.Newobj, smBuilder.Constructor);

        // Copy 'this' to state machine's ThisField if it exists
        if (smBuilder.ThisField != null)
        {
            il.Emit(OpCodes.Dup);  // Keep state machine reference on stack
            il.Emit(OpCodes.Ldarg_0);  // Load 'this' (instance methods have 'this' at arg 0)
            il.Emit(OpCodes.Stfld, smBuilder.ThisField);
        }

        // Get the typed parameter types for the method
        // We need to box value types since state machine fields are object-typed
        string? className = methodBuilder.DeclaringType?.Name;
        string methodName = methodBuilder.Name;
        Type[] paramTypes = className != null
            ? ParameterTypeResolver.ResolveMethodParameters(className, methodName, parameters, _typeMapper, _typeMap)
            : parameters.Select(_ => typeof(object)).ToArray();

        // Copy parameters to state machine fields (instance methods start params at index 1)
        for (int i = 0; i < parameters.Count; i++)
        {
            var paramName = parameters[i].Name.Lexeme;
            var field = smBuilder.GetVariableField(paramName);
            if (field != null)
            {
                il.Emit(OpCodes.Dup);  // Keep state machine reference on stack
                il.Emit(OpCodes.Ldarg, i + 1);  // +1 because 'this' is at index 0

                // Box value types since state machine fields are object-typed
                if (i < paramTypes.Length && paramTypes[i].IsValueType)
                {
                    il.Emit(OpCodes.Box, paramTypes[i]);
                }

                il.Emit(OpCodes.Stfld, field);
            }
        }

        // Copy captured outer scope variables to state machine fields
        var moduleCapturedVars = BuildCapturedTopLevelVarsForModule(_modules.CurrentPath);
        var moduleEntryPointFields = BuildEntryPointDisplayClassFieldsForModule(_modules.CurrentPath);
        foreach (var capturedVar in analysis.CapturedVariables)
        {
            var capturedField = smBuilder.CapturedVariables.GetValueOrDefault(capturedVar);
            if (capturedField == null) continue;

            // Try to load from entry-point display class (captured top-level variables)
            if (moduleCapturedVars?.Contains(capturedVar) == true &&
                moduleEntryPointFields?.TryGetValue(capturedVar, out var entryPointField) == true)
            {
                il.Emit(OpCodes.Dup);  // Keep state machine reference on stack
                if (_closures.EntryPointDisplayClassStaticField != null)
                {
                    il.Emit(OpCodes.Ldsfld, _closures.EntryPointDisplayClassStaticField);
                    il.Emit(OpCodes.Ldfld, entryPointField);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Stfld, capturedField);
            }
            // Try to load from top-level static vars (non-captured module-level variables)
            else if (_topLevelStaticVars.TryGetValue(capturedVar, out var staticField))
            {
                il.Emit(OpCodes.Dup);  // Keep state machine reference on stack
                il.Emit(OpCodes.Ldsfld, staticField);
                il.Emit(OpCodes.Stfld, capturedField);
            }
        }

        // Return the state machine (which implements IEnumerable<object>)
        il.Emit(OpCodes.Ret);
    }
}
