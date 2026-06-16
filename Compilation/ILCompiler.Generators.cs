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
        smBuilder.DefineStateMachine(funcName, analysis, isInstanceMethod: false, runtime: _runtime);

        _generators.StateMachines[qualifiedName] = smBuilder;
        _generators.Functions[qualifiedName] = funcStmt;

        // Record the AST node so PropagateFunctionDCRequirements can resolve arrows nested in this
        // generator back to its qualified name, and lift captured-and-mutated locals into a shared
        // function display class so a write inside an arrow/callback reaches the generator (#674).
        _closures.FunctionAstNodes[qualifiedName] = funcStmt;
        DefineGeneratorFunctionDisplayClass(funcStmt, qualifiedName, smBuilder);

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
    /// Lifts a generator's captured-AND-mutated locals into a function-level display class so an
    /// arrow/callback inside the generator body that writes such a variable shares storage with the
    /// generator instead of snapshotting it by value (#674). Read-only captures keep the existing
    /// by-value snapshot path. Mirrors the sync/async function-DC wiring, restricted to the write
    /// case the generator state machine could not previously honour. No-op when the generator has no
    /// write-captures (the common case), leaving fully-standalone output unchanged.
    /// </summary>
    private void DefineGeneratorFunctionDisplayClass(
        Stmt.Function funcStmt, string qualifiedName, GeneratorStateMachineBuilder smBuilder)
    {
        var mutatedCaptured = ComputeMutatedCapturedGeneratorVars(funcStmt);
        if (mutatedCaptured.Count == 0)
            return;

        RegisterFunctionDisplayClass(qualifiedName, mutatedCaptured);
        if (_closures.FunctionDisplayClasses.TryGetValue(qualifiedName, out var funcDC))
            smBuilder.DefineFunctionDisplayClassField(funcDC);
    }

    /// <summary>
    /// Returns the generator's own locals/parameters that are both captured by an inner arrow and
    /// written by one (the set that needs shared reference storage). Per-iteration <c>for (let…)</c>
    /// bindings are excluded — each iteration owns its binding (#649), so closures must snapshot them
    /// per iteration rather than share one function-DC cell.
    /// </summary>
    private HashSet<string> ComputeMutatedCapturedGeneratorVars(Stmt.Function funcStmt)
    {
        var capturedLocals = _closures.Analyzer.GetCapturedLocals(funcStmt);
        if (capturedLocals.Count == 0)
            return [];

        var result = new HashSet<string>(capturedLocals);
        result.IntersectWith(CollectGeneratorArrowCapturedWrites(funcStmt));
        if (result.Count == 0)
            return [];

        var perIteration = _closures.Analyzer.GetPerIterationLoopBindings(funcStmt);
        if (perIteration.Count > 0)
            result.ExceptWith(perIteration);
        return result;
    }

    /// <summary>
    /// Unions, over every arrow lexically inside the generator body (nested arrows included), the
    /// names the arrow assigns within its own scope that it also captures from an enclosing scope.
    /// Intersected by the caller with the generator's own captured locals to identify mutated
    /// generator captures.
    /// </summary>
    private HashSet<string> CollectGeneratorArrowCapturedWrites(Stmt.Function funcStmt)
    {
        var collector = new ArrowCollector();
        if (funcStmt.Body != null)
            foreach (var stmt in funcStmt.Body)
                collector.Visit(stmt);

        var writes = new HashSet<string>();
        foreach (var arrow in collector.Arrows)
        {
            // Only sync arrows share the generator's function DC — async arrows capture through
            // their own boxed state machine, the same scope the compile-time guard covers (#674).
            if (arrow.IsAsync)
                continue;
            var arrowWrites = CapturedWriteAnalysis.CollectImmediateWrites(arrow);
            arrowWrites.IntersectWith(_closures.Analyzer.GetCaptures(arrow));
            writes.UnionWith(arrowWrites);
        }
        return writes;
    }

    /// <summary>Collects every arrow function in a subtree, descending into nested arrows.</summary>
    private sealed class ArrowCollector : Parsing.Visitors.AstVisitorBase
    {
        public readonly List<Expr.ArrowFunction> Arrows = [];
        protected override void VisitArrowFunction(Expr.ArrowFunction expr)
        {
            Arrows.Add(expr);
            base.VisitArrowFunction(expr); // descend to find nested arrows
        }
    }

    /// <summary>
    /// Emits all generator state machine bodies.
    /// Called after all functions have been defined.
    /// </summary>
    private void EmitGeneratorStateMachineBodies()
    {
        var savedPath = _modules.CurrentPath;
        var savedNamespacePath = _currentNamespacePath;
        foreach (var (funcName, smBuilder) in _generators.StateMachines)
        {
            if (_functionDefinitionModule.TryGetValue(funcName, out var fnModule))
            {
                _modules.CurrentPath = NormalizeToEmissionPath(fnModule);
            }
            // Restore the enclosing namespace (null for non-namespace functions) so the
            // MoveNext body resolves namespace-level var/let/const by bare name (#567).
            _currentNamespacePath = _functionDefinitionNamespace.GetValueOrDefault(funcName);
            var funcStmt = _generators.Functions[funcName];
            var methodBuilder = _functions.Builders[funcName];

            // Emit the stub method body (creates and returns the state machine)
            EmitGeneratorStubMethod(methodBuilder, smBuilder, funcStmt, funcName);

            // Emit the MoveNext method body
            EmitGeneratorMoveNextBody(smBuilder, funcStmt, funcName);

            // Finalize the state machine type
            smBuilder.CreateType();
        }
        _modules.CurrentPath = savedPath;
        _currentNamespacePath = savedNamespacePath;
    }

    /// <summary>
    /// Emits the stub method that creates and initializes the generator state machine.
    /// </summary>
    private void EmitGeneratorStubMethod(
        MethodBuilder methodBuilder,
        GeneratorStateMachineBuilder smBuilder,
        Stmt.Function funcStmt,
        string qualifiedName)
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

        // Instantiate the function display class (#674) and seed any captured-and-mutated
        // parameters into it so an arrow that writes them shares the generator's storage. The
        // stub params are object-typed (BuildStateMachineStubParamTypes), so no boxing is needed.
        EmitGeneratorFunctionDCInit(il, smBuilder, funcStmt, qualifiedName, paramOffset: 0);

        // Captured outer-scope variables are NOT copied into the state machine here. Doing so
        // snapshotted their value at creation time, so a later mutation of the outer variable
        // was invisible to the running body (#541). MoveNext instead reads/writes them live
        // from their enclosing storage (top-level statics / entry-point display class), which
        // requires the corresponding fields to be set on the MoveNext CompilationContext below.

        // Return the state machine (which implements IEnumerable<object>)
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// With the state machine instance on the stack, news up the function display class (#674),
    /// stores it into the state machine's <c>&lt;&gt;__functionDC</c> field, and copies any
    /// captured-and-mutated parameters into it. Leaves the state machine reference on the stack
    /// (net stack effect zero). No-op when the generator has no function DC.
    /// </summary>
    private void EmitGeneratorFunctionDCInit(
        ILGenerator il,
        GeneratorStateMachineBuilder smBuilder,
        Stmt.Function funcStmt,
        string qualifiedName,
        int paramOffset)
    {
        if (smBuilder.FunctionDCField == null ||
            !_closures.FunctionDisplayClassCtors.TryGetValue(qualifiedName, out var dcCtor))
            return;

        il.Emit(OpCodes.Dup);                       // [sm, sm]
        il.Emit(OpCodes.Newobj, dcCtor);            // [sm, sm, dc]
        il.Emit(OpCodes.Stfld, smBuilder.FunctionDCField); // [sm]

        if (!_closures.FunctionDisplayClassFields.TryGetValue(qualifiedName, out var dcFields))
            return;

        for (int i = 0; i < funcStmt.Parameters.Count; i++)
        {
            var paramName = funcStmt.Parameters[i].Name.Lexeme;
            if (!dcFields.TryGetValue(paramName, out var dcField))
                continue;
            il.Emit(OpCodes.Dup);                   // [sm, sm]
            il.Emit(OpCodes.Ldfld, smBuilder.FunctionDCField); // [sm, dc]
            il.Emit(OpCodes.Ldarg, i + paramOffset);           // [sm, dc, arg]
            il.Emit(OpCodes.Stfld, dcField);        // [sm]
        }
    }

    /// <summary>
    /// Emits the MoveNext method body for a generator state machine.
    /// Uses GeneratorMoveNextEmitter to handle full generator body with yield expressions.
    /// </summary>
    private void EmitGeneratorMoveNextBody(GeneratorStateMachineBuilder smBuilder, Stmt.Function funcStmt, string qualifiedName)
    {
        var analysis = _generators.Analyzer.Analyze(funcStmt);

        // Create a compilation context for the state machine
        var il = smBuilder.MoveNextMethod.GetILGenerator();
        var ctx = new CompilationContext(il, _typeMapper, _functions.Builders, _classes.Builders, _namespaceFields, _namespaceVarFields, _types)
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
            CurrentNamespacePath = _currentNamespacePath,
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
            ClassRegistry = GetClassRegistry(),
            // Captured outer variables are read live (by reference) rather than snapshotted (#541).
            // These mirror the async-generator MoveNext context so reads/writes of top-level
            // variables go straight to their backing storage instead of a stale state-machine field.
            EntryPointDisplayClassFields = BuildEntryPointDisplayClassFieldsForModule(_modules.CurrentPath),
            CapturedTopLevelVars = BuildCapturedTopLevelVarsForModule(_modules.CurrentPath),
            EntryPointDisplayClassStaticField = _closures.EntryPointDisplayClassStaticField,
            TopLevelStaticVars = BuildTopLevelStaticVarsForModule(_modules.CurrentPath)
        };

        // Route reads/writes of captured-and-mutated locals through the shared function display
        // class (#674) and let capturing arrows thread it in. Only set when this generator has a
        // function DC; otherwise the existing by-value snapshot path is used unchanged.
        if (_closures.FunctionDisplayClassFields.TryGetValue(qualifiedName, out var funcDCFields))
        {
            ctx.FunctionDisplayClassFields = funcDCFields;
            ctx.CapturedFunctionLocals = [.. funcDCFields.Keys];
            ctx.ArrowFunctionDCFields = _closures.ArrowFunctionDCFields.Count > 0 ? _closures.ArrowFunctionDCFields : null;
        }

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
    private void EmitGeneratorMethodBody(MethodBuilder methodBuilder, Stmt.Function method, FieldInfo? fieldsField,
        bool isInstanceMethod = true, string? currentClassName = null)
    {
        // Analyze generator function to determine yield points and hoisted variables
        var analysis = _generators.Analyzer.Analyze(method);

        // Build state machine type. A static generator method (#692) has no `this`/instance fields, so it
        // is set up like a free function (isInstanceMethod: false, static stub). The type name uses the
        // MethodBuilder's (mangled) name so a private generator's `#p` lexeme doesn't put a `#` in it (#720).
        var smBuilder = new GeneratorStateMachineBuilder(_moduleBuilder, _types, _generators.StateMachineCounter++);
        smBuilder.DefineStateMachine(
            $"{methodBuilder.DeclaringType!.Name}_{methodBuilder.Name}",
            analysis,
            isInstanceMethod: isInstanceMethod,
            runtime: _runtime
        );

        // Emit stub method body (creates state machine and returns it)
        if (isInstanceMethod)
            EmitGeneratorInstanceStubMethod(methodBuilder, smBuilder, method.Parameters);
        else
            EmitGeneratorStaticStubMethod(methodBuilder, smBuilder, method.Parameters);

        // Create context for MoveNext emission
        var il = smBuilder.MoveNextMethod.GetILGenerator();
        var ctx = new CompilationContext(il, _typeMapper, _functions.Builders, _classes.Builders, _namespaceFields, _namespaceVarFields, _types)
        {
            FieldsField = fieldsField,
            IsInstanceMethod = isInstanceMethod,
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
            CurrentNamespacePath = _currentNamespacePath,
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
            // ES2022 Private Class Elements support for generator methods (a private generator threads
            // its QUALIFIED class name so nested private member access resolves under modules — #720).
            CurrentClassName = currentClassName ?? methodBuilder.DeclaringType?.Name,
            CurrentClassBuilder = methodBuilder.DeclaringType as TypeBuilder,
            // Registry services
            ClassRegistry = GetClassRegistry(),
            // Captured outer variables are read live (by reference), not snapshotted (#541).
            // TopLevelStaticVars covers module-level vars that aren't in the entry-point display class.
            EntryPointDisplayClassFields = BuildEntryPointDisplayClassFieldsForModule(_modules.CurrentPath),
            CapturedTopLevelVars = BuildCapturedTopLevelVarsForModule(_modules.CurrentPath),
            EntryPointDisplayClassStaticField = _closures.EntryPointDisplayClassStaticField,
            TopLevelStaticVars = BuildTopLevelStaticVarsForModule(_modules.CurrentPath)
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
        List<Stmt.Parameter> parameters)
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

        // Box value types since state machine fields are object-typed. Decide from the method's ACTUAL
        // IL signature (methodBuilder.GetParameters()), not the AST-resolved types: a private method's
        // parameters are all `object` slots, so boxing the AST-resolved value type (e.g. Double) would
        // mismatch the `object` argument actually loaded (StackUnexpected). Mirrors EmitAsyncStubMethod.
        var paramTypes = methodBuilder.GetParameters();

        // Copy parameters to state machine fields (instance methods start params at index 1)
        for (int i = 0; i < parameters.Count; i++)
        {
            var paramName = parameters[i].Name.Lexeme;
            var field = smBuilder.GetVariableField(paramName);
            if (field != null)
            {
                il.Emit(OpCodes.Dup);  // Keep state machine reference on stack
                il.Emit(OpCodes.Ldarg, i + 1);  // +1 because 'this' is at index 0

                if (i < paramTypes.Length && paramTypes[i].ParameterType.IsValueType)
                {
                    il.Emit(OpCodes.Box, paramTypes[i].ParameterType);
                }

                il.Emit(OpCodes.Stfld, field);
            }
        }

        // Captured outer-scope variables are NOT copied into the state machine (#541): see the
        // free-function stub above. MoveNext reads/writes them live from their backing storage,
        // wired through the CompilationContext in EmitGeneratorMethodBody.

        // Return the state machine (which implements IEnumerable<object>)
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the stub that creates the generator state machine for a STATIC method (#692): like the
    /// instance stub but with no <c>this</c> and parameters starting at arg 0 (no receiver slot).
    /// Value-type parameters are boxed into the object-typed state-machine fields.
    /// </summary>
    private void EmitGeneratorStaticStubMethod(
        MethodBuilder methodBuilder,
        GeneratorStateMachineBuilder smBuilder,
        List<Stmt.Parameter> parameters)
    {
        var il = methodBuilder.GetILGenerator();

        // Create new instance of the state machine
        il.Emit(OpCodes.Newobj, smBuilder.Constructor);

        // Box value types since state machine fields are object-typed. Decide from the method's ACTUAL
        // IL signature (methodBuilder.GetParameters()), not the AST-resolved types: a private static
        // method's parameters are all `object` slots, so boxing the AST-resolved value type would
        // mismatch the `object` argument actually loaded (StackUnexpected). Mirrors EmitAsyncStubMethod.
        var paramTypes = methodBuilder.GetParameters();

        // Copy parameters to state machine fields (static methods start params at index 0).
        for (int i = 0; i < parameters.Count; i++)
        {
            var field = smBuilder.GetVariableField(parameters[i].Name.Lexeme);
            if (field != null)
            {
                il.Emit(OpCodes.Dup);  // Keep state machine reference on stack
                il.Emit(OpCodes.Ldarg, i);

                if (i < paramTypes.Length && paramTypes[i].ParameterType.IsValueType)
                {
                    il.Emit(OpCodes.Box, paramTypes[i].ParameterType);
                }

                il.Emit(OpCodes.Stfld, field);
            }
        }

        // Return the state machine (which implements IEnumerable<object>)
        il.Emit(OpCodes.Ret);
    }
}
