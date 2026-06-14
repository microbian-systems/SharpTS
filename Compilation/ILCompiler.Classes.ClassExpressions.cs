using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;
using TSTypeInfo = SharpTS.TypeSystem.TypeInfo;

namespace SharpTS.Compilation;

public partial class ILCompiler
{
    /// <summary>
    /// Defines types for all collected class expressions.
    /// Class expressions are collected during arrow function collection phase.
    /// </summary>
    private void DefineClassExpressionTypes()
    {
        foreach (var classExpr in _classExprs.ToDefine)
        {
            DefineClassExpression(classExpr);
        }
    }

    /// <summary>
    /// Defines a single class expression type with typed properties, generics, and inheritance support.
    /// Method bodies are emitted later in EmitClassExpressionBodies.
    /// </summary>
    private void DefineClassExpression(Expr.ClassExpr classExpr)
    {
        string className = _classExprs.Names[classExpr];

        // Create TypeBuilder with appropriate attributes
        // Note: We create without parent initially, set it after defining generic params
        TypeAttributes typeAttrs = TypeAttributes.Public | TypeAttributes.Class;
        if (classExpr.IsAbstract)
        {
            typeAttrs |= TypeAttributes.Abstract;
        }

        var typeBuilder = _moduleBuilder.DefineType(
            className,
            typeAttrs
        );

        // Track superclass name for inheritance resolution
        string? superclassName = Expr.GetSuperclassLeafName(classExpr.SuperclassExpr);
        _classExprs.Superclass[classExpr] = superclassName;

        // Handle generic type parameters FIRST (before resolving superclass type args)
        GenericTypeParameterBuilder[]? classGenericParams = null;
        if (classExpr.TypeParams != null && classExpr.TypeParams.Count > 0)
        {
            string[] typeParamNames = classExpr.TypeParams.Select(tp => tp.Name.Lexeme).ToArray();
            classGenericParams = typeBuilder.DefineGenericParameters(typeParamNames);

            // Apply constraints
            for (int i = 0; i < classExpr.TypeParams.Count; i++)
            {
                var constraint = classExpr.TypeParams[i].Constraint;
                if (constraint != null)
                {
                    Type constraintType = ResolveConstraintType(constraint);
                    if (constraintType.IsInterface)
                        classGenericParams[i].SetInterfaceConstraints(constraintType);
                    else
                        classGenericParams[i].SetBaseTypeConstraint(constraintType);
                }
            }

            _classExprs.GenericParams[classExpr] = classGenericParams;
        }

        // NOW resolve superclass (may use our generic params for type arguments)
        Type? baseType = null;
        if (superclassName != null)
        {
            // Check class declarations first (with module resolution)
            var resolvedSuperName = GetDefinitionContext().ResolveClassName(superclassName);
            TypeBuilder? superTypeBuilder = null;

            if (_classes.Builders.TryGetValue(resolvedSuperName, out superTypeBuilder))
            {
                // Found in class declarations
            }
            else if (_classExprs.VarToClassExpr.TryGetValue(superclassName, out var parentClassExpr)
                     && _classExprs.Builders.TryGetValue(parentClassExpr, out var parentExprBuilder))
            {
                // Superclass is another class expression bound to a variable
                // (e.g. `const A = class {}; const B = class extends A {}`).
                // _classExprs.Names holds GENERATED names ($ClassExpr_N), so the
                // by-generated-name scan below never matches the source variable
                // name — without this, B's parent defaults to System.Object while
                // super() still chains A..ctor, tripping ILVerify CallCtor /
                // ThisUninitReturn (#287 family).
                superTypeBuilder = parentExprBuilder;
            }
            else
            {
                // Check other class expressions by their generated name
                foreach (var (expr, name) in _classExprs.Names)
                {
                    if (name == superclassName && _classExprs.Builders.TryGetValue(expr, out var superExprBuilder))
                    {
                        superTypeBuilder = superExprBuilder;
                        break;
                    }
                }
            }

            if (superTypeBuilder != null)
            {
                // Check for type arguments
                if (classExpr.SuperclassTypeArgs != null && classExpr.SuperclassTypeArgs.Count > 0)
                {
                    var typeArgs = ResolveSuperclassTypeArguments(
                        classExpr.SuperclassTypeArgs,
                        classGenericParams,
                        classExpr.TypeParams);
                    baseType = superTypeBuilder.MakeGenericType(typeArgs);
                }
                else
                {
                    baseType = superTypeBuilder;
                }
            }
        }

        // Set the parent type
        if (baseType != null)
        {
            typeBuilder.SetParent(baseType);
        }

        // Implement $IHasFields interface for unified property access
        // All classes implement this interface (including derived classes)
        typeBuilder.AddInterfaceImplementation(_runtime.IHasFieldsInterface);

        // Initialize tracking dictionaries for this class expression
        _classExprs.BackingFields[classExpr] = [];
        _classExprs.Properties[classExpr] = [];
        _classExprs.PropertyTypes[classExpr] = [];
        _classExprs.DeclaredProperties[classExpr] = [];
        _classExprs.ReadonlyProperties[classExpr] = [];
        _classExprs.StaticFields[classExpr] = [];
        _classExprs.StaticMethods[classExpr] = [];
        _classExprs.InstanceMethods[classExpr] = [];
        _classExprs.Getters[classExpr] = [];
        _classExprs.Setters[classExpr] = [];

        // Add _fields dictionary for dynamic property storage (extras)
        var fieldsField = typeBuilder.DefineField(
            "_fields",
            typeof(Dictionary<string, object>),
            FieldAttributes.Private
        );
        _typedInterop.ExtrasFields[className] = fieldsField;
        _classes.InstanceFieldsField[className] = fieldsField;

        // Define typed instance properties
        // Skip declare fields - they use _fields dictionary to support TypeScript null semantics.
        // Skip generic-parameter-typed fields - a backing field of type `T` (an open generic
        // parameter) cannot be widened to / narrowed from the object-typed $IHasFields slots
        // without box/unbox of `T`, which yields unverifiable IL. Like class declarations
        // (ILCompiler.Classes.cs), these fields live in the `_fields` dictionary instead (#291).
        foreach (var field in classExpr.Fields.Where(f => !f.IsStatic && !f.IsDeclare))
        {
            bool isGenericField = classGenericParams != null &&
                field.TypeAnnotation != null &&
                classGenericParams.Any(p => p.Name == field.TypeAnnotation);
            if (isGenericField)
                continue;
            DefineClassExpressionProperty(typeBuilder, classExpr, field, classGenericParams);
        }

        // Define static fields (use object type for compatibility with existing emission code)
        foreach (var field in classExpr.Fields.Where(f => f.IsStatic))
        {
            var staticField = typeBuilder.DefineField(
                field.Name.Lexeme,
                typeof(object),  // Keep as object type like class declarations
                FieldAttributes.Public | FieldAttributes.Static
            );
            _classExprs.StaticFields[classExpr][field.Name.Lexeme] = staticField;
        }

        // Define $IHasFields interface method stubs
        // Bodies are emitted later in EmitClassExpressionBody after method definitions are available
        DefineHasFieldsInterfaceMethods(typeBuilder, className, fieldsField);

        // Store the type builder
        _classExprs.Builders[classExpr] = typeBuilder;
    }

    /// <summary>
    /// Defines a typed .NET property with backing field for a class expression field.
    /// </summary>
    private void DefineClassExpressionProperty(
        TypeBuilder typeBuilder,
        Expr.ClassExpr classExpr,
        Stmt.Field field,
        GenericTypeParameterBuilder[]? classGenericParams)
    {
        string fieldName = field.Name.Lexeme;
        string pascalName = NamingConventions.ToPascalCase(fieldName);
        Type propertyType = GetClassExprFieldType(classExpr, field, classGenericParams);

        // Track as declared property
        _classExprs.DeclaredProperties[classExpr].Add(pascalName);
        _classExprs.PropertyTypes[classExpr][pascalName] = propertyType;

        if (field.IsReadonly)
        {
            _classExprs.ReadonlyProperties[classExpr].Add(pascalName);
        }

        // Define private backing field
        var backingField = typeBuilder.DefineField(
            $"__{pascalName}",
            propertyType,
            FieldAttributes.Private
        );
        _classExprs.BackingFields[classExpr][pascalName] = backingField;

        // Define the property
        var property = typeBuilder.DefineProperty(
            pascalName,
            PropertyAttributes.None,
            propertyType,
            null
        );
        _classExprs.Properties[classExpr][pascalName] = property;

        // Define getter
        var getter = typeBuilder.DefineMethod(
            $"get_{pascalName}",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            propertyType,
            Type.EmptyTypes
        );
        var getterIL = getter.GetILGenerator();
        getterIL.Emit(OpCodes.Ldarg_0);
        getterIL.Emit(OpCodes.Ldfld, backingField);
        getterIL.Emit(OpCodes.Ret);
        property.SetGetMethod(getter);
        _classExprs.Getters[classExpr][pascalName] = getter;

        // Define setter (always needed for constructor initialization)
        var setter = typeBuilder.DefineMethod(
            $"set_{pascalName}",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            typeof(void),
            [propertyType]
        );
        var setterIL = setter.GetILGenerator();
        setterIL.Emit(OpCodes.Ldarg_0);
        setterIL.Emit(OpCodes.Ldarg_1);
        setterIL.Emit(OpCodes.Stfld, backingField);
        setterIL.Emit(OpCodes.Ret);

        // Only link setter to property for non-readonly
        if (!field.IsReadonly)
        {
            property.SetSetMethod(setter);
            _classExprs.Setters[classExpr][pascalName] = setter;
        }
    }

    /// <summary>
    /// Gets the .NET type for a class expression field.
    /// </summary>
    private Type GetClassExprFieldType(
        Expr.ClassExpr classExpr,
        Stmt.Field field,
        GenericTypeParameterBuilder[]? classGenericParams)
    {
        if (field.TypeAnnotation == null)
            return typeof(object);

        // Check generic type parameters
        if (classGenericParams != null)
        {
            var param = classGenericParams.FirstOrDefault(p => p.Name == field.TypeAnnotation);
            if (param != null)
                return param;
        }

        return TypeMapper.GetClrType(field.TypeAnnotation);
    }

    /// <summary>
    /// Defines method signatures for all class expressions.
    /// Called after DefineClassExpressionTypes.
    /// </summary>
    private void DefineClassExpressionMethods()
    {
        foreach (var classExpr in _classExprs.ToDefine)
        {
            DefineClassExpressionMethodSignatures(classExpr);
        }
    }

    /// <summary>
    /// Defines method and constructor signatures for a class expression.
    /// </summary>
    private void DefineClassExpressionMethodSignatures(Expr.ClassExpr classExpr)
    {
        if (!_classExprs.Builders.TryGetValue(classExpr, out var typeBuilder))
            return;

        string className = _classExprs.Names[classExpr];

        // Find user-defined constructor or use default
        var constructor = classExpr.Methods.FirstOrDefault(m => m.Name.Lexeme == "constructor" && m.Body != null);
        var ctorParamTypes = constructor?.Parameters.Select(_ => typeof(object)).ToArray() ?? [];

        var ctorBuilder = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            ctorParamTypes
        );
        _classExprs.Constructors[classExpr] = ctorBuilder;
        _classes.Constructors[className] = ctorBuilder;

        // Define static methods
        foreach (var method in classExpr.Methods.Where(m => m.Body != null && m.IsStatic && m.Name.Lexeme != "constructor"))
        {
            var paramTypes = method.Parameters.Select(_ => typeof(object)).ToArray();
            Type returnType = method.IsAsync ? _types.TaskOfObject : typeof(object);

            var methodBuilder = typeBuilder.DefineMethod(
                method.Name.Lexeme,
                MethodAttributes.Public | MethodAttributes.Static,
                returnType,
                paramTypes
            );
            _classExprs.StaticMethods[classExpr][method.Name.Lexeme] = methodBuilder;
        }

        // Define instance methods
        foreach (var method in classExpr.Methods.Where(m => m.Body != null && !m.IsStatic && m.Name.Lexeme != "constructor"))
        {
            var paramTypes = method.Parameters.Select(_ => typeof(object)).ToArray();

            MethodAttributes methodAttrs = MethodAttributes.Public | MethodAttributes.Virtual;
            if (method.IsAbstract)
                methodAttrs |= MethodAttributes.Abstract;

            Type returnType = method.IsAsync ? typeof(Task<object>) : typeof(object);

            var methodBuilder = typeBuilder.DefineMethod(
                method.Name.Lexeme,
                methodAttrs,
                returnType,
                paramTypes
            );
            _classExprs.InstanceMethods[classExpr][method.Name.Lexeme] = methodBuilder;
        }

        // Define user-defined accessors (overrides property accessors)
        if (classExpr.Accessors != null)
        {
            foreach (var accessor in classExpr.Accessors)
            {
                // Symbol-keyed computed accessor (#281): define a synthetic
                // $sym_get/set_N method recorded in _classes.SymbolAccessors keyed
                // by this class's generated name. It is registered in the class-
                // expression .cctor and dispatched through the $Runtime symbol-
                // accessor registry, mirroring the class-declaration path (#266).
                if (accessor.ComputedKey != null)
                {
                    DefineSymbolAccessorMethod(typeBuilder, accessor);
                    continue;
                }
                string accessorName = accessor.Name.Lexeme;
                string pascalName = NamingConventions.ToPascalCase(accessorName);
                string methodName = accessor.Kind.Type == TokenType.GET
                    ? $"get_{pascalName}"
                    : $"set_{pascalName}";

                Type[] paramTypes = accessor.Kind.Type == TokenType.SET
                    ? [typeof(object)]
                    : [];

                MethodAttributes methodAttrs = MethodAttributes.Public | MethodAttributes.SpecialName |
                                              MethodAttributes.HideBySig | MethodAttributes.Virtual;
                if (accessor.IsAbstract)
                    methodAttrs |= MethodAttributes.Abstract;

                var methodBuilder = typeBuilder.DefineMethod(
                    methodName,
                    methodAttrs,
                    typeof(object),
                    paramTypes
                );

                if (accessor.Kind.Type == TokenType.GET)
                    _classExprs.Getters[classExpr][pascalName] = methodBuilder;
                else
                    _classExprs.Setters[classExpr][pascalName] = methodBuilder;
            }
        }
    }

    /// <summary>
    /// Emits method bodies for all class expressions.
    /// Called after class declaration method emission.
    /// </summary>
    private void EmitClassExpressionBodies()
    {
        foreach (var classExpr in _classExprs.ToDefine)
        {
            EmitClassExpressionBody(classExpr);
        }
    }

    /// <summary>
    /// Emits all method bodies for a single class expression.
    /// </summary>
    private void EmitClassExpressionBody(Expr.ClassExpr classExpr)
    {
        if (!_classExprs.Builders.TryGetValue(classExpr, out var typeBuilder))
            return;

        string className = _classExprs.Names[classExpr];
        var fieldsField = _classes.InstanceFieldsField[className];

        // Emit static constructor if there are static field initializers
        EmitClassExpressionStaticConstructor(classExpr, typeBuilder);

        // Emit instance constructor
        EmitClassExpressionConstructor(classExpr, typeBuilder, fieldsField);

        // Emit instance method bodies
        foreach (var method in classExpr.Methods.Where(m => m.Body != null && !m.IsStatic && m.Name.Lexeme != "constructor"))
        {
            EmitClassExpressionMethod(classExpr, typeBuilder, method, fieldsField);
        }

        // Emit static method bodies
        foreach (var method in classExpr.Methods.Where(m => m.Body != null && m.IsStatic))
        {
            EmitClassExpressionStaticMethodBody(classExpr, method);
        }

        // Emit user-defined accessor bodies
        if (classExpr.Accessors != null)
        {
            foreach (var accessor in classExpr.Accessors)
            {
                // Symbol-keyed accessors are emitted below from _classes.SymbolAccessors
                // (their synthetic methods aren't in _classExprs.Getters/Setters).
                if (!accessor.IsAbstract && accessor.ComputedKey == null)
                {
                    EmitClassExpressionAccessor(classExpr, typeBuilder, accessor, fieldsField);
                }
            }
        }

        // Emit symbol-keyed computed accessor bodies (#281)
        EmitClassExpressionSymbolAccessors(classExpr, typeBuilder, fieldsField);

        // Emit $IHasFields interface method bodies (now that method builders are available)
        EmitHasFieldsInterfaceMethodBodies(className, classExpr);
    }

    /// <summary>
    /// Creates a CompilationContext for class expression method emission.
    /// </summary>
    private CompilationContext CreateClassExpressionContext(
        ILGenerator il,
        Expr.ClassExpr classExpr,
        TypeBuilder typeBuilder,
        FieldInfo? fieldsField)
    {
        string className = _classExprs.Names[classExpr];

        return new CompilationContext(il, _typeMapper, _functions.Builders, _classes.Builders, _types)
        {
            FieldsField = fieldsField,
            ClosureAnalyzer = _closures.Analyzer,
            ArrowMethods = _closures.ArrowMethods,
            ConstArrowBindings = _closures.ConstArrowBindings,
            DisplayClasses = _closures.DisplayClasses,
            DisplayClassFields = _closures.DisplayClassFields,
            DisplayClassConstructors = _closures.DisplayClassConstructors,
            CurrentClassBuilder = typeBuilder,
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
            CurrentModulePath = _modules.CurrentPath,
            ClassToModule = _modules.ClassToModule,
            FunctionToModule = _modules.FunctionToModule,
            EnumToModule = _modules.EnumToModule,
            DotNetNamespace = _modules.CurrentDotNetNamespace,
            PropertyBackingFields = _typedInterop.PropertyBackingFields,
            ClassProperties = _typedInterop.ClassProperties,
            DeclaredPropertyNames = _typedInterop.DeclaredPropertyNames,
            ReadonlyPropertyNames = _typedInterop.ReadonlyPropertyNames,
            PropertyTypes = _typedInterop.PropertyTypes,
            ExtrasFields = _typedInterop.ExtrasFields,
            UnionGenerator = _unionGenerator,
            TypeEmitterRegistry = _typeEmitterRegistry,
            BuiltInModuleEmitterRegistry = _builtInModuleEmitterRegistry,
            BuiltInModuleNamespaces = _builtInModuleNamespaces,
            BuiltInModuleMethodBindings = GetCurrentBuiltInMethodBindings(),
            ImportedNames = _importedNames,
            ClassExprBuilders = _classExprs.Builders,
            ClassExprBackingFields = _classExprs.BackingFields,
            ClassExprProperties = _classExprs.Properties,
            ClassExprPropertyTypes = _classExprs.PropertyTypes,
            ClassExprDeclaredProperties = _classExprs.DeclaredProperties,
            ClassExprReadonlyProperties = _classExprs.ReadonlyProperties,
            ClassExprStaticFields = _classExprs.StaticFields,
            ClassExprStaticMethods = _classExprs.StaticMethods,
            ClassExprInstanceMethods = _classExprs.InstanceMethods,
            ClassExprGetters = _classExprs.Getters,
            ClassExprSetters = _classExprs.Setters,
            ClassExprConstructors = _classExprs.Constructors,
            ClassExprGenericParams = _classExprs.GenericParams,
            ClassExprSuperclass = _classExprs.Superclass,
            CurrentClassExpr = classExpr,
            VarToClassExpr = _classExprs.VarToClassExpr,
            IsStrictMode = _isStrictMode,
            // Registry services
            ClassRegistry = GetClassRegistry(),
            // Module-level / captured top-level variable access — without these a
            // class-expression method or accessor body referencing a top-level
            // binding throws ReferenceError at runtime (same omission #300 fixed
            // for class-declaration accessor bodies).
            TopLevelStaticVars = BuildClassMethodTopLevelStaticVarsForModule(_modules.CurrentPath),
            CapturedTopLevelVars = BuildCapturedTopLevelVarsForModule(_modules.CurrentPath),
            EntryPointDisplayClassFields = BuildEntryPointDisplayClassFieldsForModule(_modules.CurrentPath),
            EntryPointDisplayClassStaticField = _closures.EntryPointDisplayClassStaticField
        };
    }

    /// <summary>
    /// Emits static constructor for class expression static field initializers and static blocks.
    /// </summary>
    private void EmitClassExpressionStaticConstructor(Expr.ClassExpr classExpr, TypeBuilder typeBuilder)
    {
        bool hasStaticFields = classExpr.Fields.Any(f => f.IsStatic && f.Initializer != null);
        bool hasStaticInitializers = classExpr.StaticInitializers?.Count > 0;
        // Symbol-keyed computed accessors (#281) register in the .cctor, keyed by
        // this class's generated name (mirrors the class-declaration path #266).
        bool hasSymbolAccessors = _classes.SymbolAccessors.ContainsKey(typeBuilder.Name);

        if (!hasStaticFields && !hasStaticInitializers && !hasSymbolAccessors) return;

        var cctor = typeBuilder.DefineConstructor(
            MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            Type.EmptyTypes
        );

        var il = cctor.GetILGenerator();
        var ctx = CreateClassExpressionContext(il, classExpr, typeBuilder, null);
        ctx.IsStaticConstructorContext = true;
        var emitter = new ILEmitter(ctx);

        // Process StaticInitializers if available (preserves declaration order)
        if (hasStaticInitializers)
        {
            foreach (var initializer in classExpr.StaticInitializers!)
            {
                switch (initializer)
                {
                    case Stmt.Field field when field.IsStatic && field.Initializer != null:
                        var staticField = _classExprs.StaticFields[classExpr][field.Name.Lexeme];
                        emitter.EmitExpression(field.Initializer);
                        if (staticField.FieldType == typeof(object))
                            emitter.EmitBoxIfNeeded(field.Initializer);
                        il.Emit(OpCodes.Stsfld, staticField);
                        break;

                    case Stmt.StaticBlock block:
                        foreach (var stmt in block.Body)
                            emitter.EmitStatement(stmt);
                        break;
                }
            }
        }
        else
        {
            // Fallback for backward compatibility (fields only)
            foreach (var field in classExpr.Fields.Where(f => f.IsStatic && f.Initializer != null))
            {
                var staticField = _classExprs.StaticFields[classExpr][field.Name.Lexeme];
                emitter.EmitExpression(field.Initializer!);
                if (staticField.FieldType == typeof(object))
                    emitter.EmitBoxIfNeeded(field.Initializer!);
                il.Emit(OpCodes.Stsfld, staticField);
            }
        }

        // Register symbol-keyed computed accessors (#281) in the runtime registry,
        // keyed by this class's Type so dynamic bracket get/set can dispatch them.
        EmitSymbolAccessorRegistrations(emitter, il, typeBuilder);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits instance constructor for a class expression.
    /// </summary>
    private void EmitClassExpressionConstructor(Expr.ClassExpr classExpr, TypeBuilder typeBuilder, FieldInfo fieldsField)
    {
        string className = _classExprs.Names[classExpr];
        var ctorBuilder = _classExprs.Constructors[classExpr];
        var constructor = classExpr.Methods.FirstOrDefault(m => m.Name.Lexeme == "constructor" && m.Body != null);

        var il = ctorBuilder.GetILGenerator();
        var ctx = CreateClassExpressionContext(il, classExpr, typeBuilder, fieldsField);
        ctx.IsInstanceMethod = true;

        // Add generic type parameters to context
        if (_classExprs.GenericParams.TryGetValue(classExpr, out var genericParams))
        {
            foreach (var gp in genericParams)
                ctx.GenericTypeParameters[gp.Name] = gp;
        }

        // Initialize _fields dictionary FIRST
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stfld, fieldsField);

        // Emit constructor body first if present (contains super() call)
        var emitter = new ILEmitter(ctx);

        // Determine if we need to call base constructor automatically
        // If there's an explicit constructor body, it should contain super() call
        bool hasExplicitSuperCall = constructor?.Body?.Any(stmt => ContainsSuperCall(stmt)) ?? false;

        if (!hasExplicitSuperCall)
        {
            // No explicit super() - call parent constructor automatically.
            il.Emit(OpCodes.Ldarg_0);

            // When the superclass is another emitted class (declaration or
            // expression), its base is a TypeBuilder — Type.GetConstructor is
            // not supported on an unbaked TypeBuilder, so resolve the parent's
            // ConstructorBuilder from the registries instead (#287 family). The
            // implicit derived constructor forwards no args, so supply defaults
            // for any base ctor parameters (parameterless in the common case).
            var baseCtor = ResolveClassExprImplicitBaseConstructor(classExpr);
            if (baseCtor != null)
            {
                foreach (var p in baseCtor.GetParameters())
                    emitter.EmitDefaultForType(p.ParameterType);
                il.Emit(OpCodes.Call, baseCtor);
            }
            else
            {
                Type baseType = typeBuilder.BaseType ?? typeof(object);
                var fallbackCtor = baseType.GetConstructor([]) ?? _types.ObjectDefaultCtor;
                il.Emit(OpCodes.Call, fallbackCtor);
            }
        }
        if (constructor != null)
        {
            // Define parameters with typed parameter types from constructor signature
            var ctorParams = ctorBuilder.GetParameters();
            for (int i = 0; i < constructor.Parameters.Count; i++)
            {
                Type? paramType = i < ctorParams.Length ? ctorParams[i].ParameterType : null;
                ctx.DefineParameter(constructor.Parameters[i].Name.Lexeme, i + 1, paramType);
            }

            emitter.EmitDefaultParameters(constructor.Parameters, true);

            if (constructor.Body != null)
            {
                foreach (var stmt in constructor.Body)
                {
                    emitter.EmitStatement(stmt);
                }
            }
        }

        // Emit instance field initializers to backing fields AFTER super() call
        foreach (var field in classExpr.Fields.Where(f => !f.IsStatic && f.Initializer != null))
        {
            string fieldName = field.Name.Lexeme;
            string pascalName = NamingConventions.ToPascalCase(fieldName);

            if (_classExprs.BackingFields[classExpr].TryGetValue(pascalName, out var backingField))
            {
                // Store in backing field
                il.Emit(OpCodes.Ldarg_0);
                emitter.EmitExpression(field.Initializer!);

                Type targetType = _classExprs.PropertyTypes[classExpr][pascalName];
                EmitTypeConversion(il, emitter, field.Initializer!, targetType);

                il.Emit(OpCodes.Stfld, backingField);
            }
            else
            {
                // Fallback to _fields dictionary
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, fieldsField);
                il.Emit(OpCodes.Ldstr, fieldName);
                emitter.EmitExpression(field.Initializer!);
                emitter.EmitBoxIfNeeded(field.Initializer!);
                il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectSetItem);
            }
        }

        // Initialize instance declare fields (without initializers) to null in _fields dictionary
        // TypeScript semantics: uninitialized fields return null/undefined, not CLR defaults
        foreach (var field in classExpr.Fields.Where(f =>
            !f.IsStatic && !f.IsPrivate && f.IsDeclare && f.Initializer == null && f.ComputedKey == null))
        {
            string fieldName = field.Name.Lexeme;
            // Store null in _fields dictionary
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, fieldsField);
            il.Emit(OpCodes.Ldstr, fieldName);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectSetItem);
        }

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Resolves the parent constructor for a class expression's implicit
    /// (no explicit super()) base-constructor call. The parent may be another
    /// class expression bound to a variable or a class declaration; in both
    /// cases the parent's base type is an unbaked TypeBuilder for which
    /// Type.GetConstructor throws, so the ConstructorBuilder is pulled from the
    /// emit-time registries instead. Returns null when the superclass is a
    /// baked/built-in type (caller falls back to reflection).
    /// </summary>
    private System.Reflection.ConstructorInfo? ResolveClassExprImplicitBaseConstructor(Expr.ClassExpr classExpr)
    {
        if (!_classExprs.Superclass.TryGetValue(classExpr, out var superName) || superName == null)
            return null;

        // Parent is another class expression bound to a variable.
        if (_classExprs.VarToClassExpr.TryGetValue(superName, out var parentExpr)
            && _classExprs.Constructors.TryGetValue(parentExpr, out var exprCtor))
            return exprCtor;

        // Parent is a class declaration.
        var resolved = GetDefinitionContext().ResolveClassName(superName);
        if (_classes.Constructors.TryGetValue(resolved, out var declCtor))
            return declCtor;

        return null;
    }

    /// <summary>
    /// Checks if a statement contains a super() call.
    /// </summary>
    private static bool ContainsSuperCall(Stmt stmt)
    {
        return stmt switch
        {
            Stmt.Expression expr => ContainsSuperCallInExpr(expr.Expr),
            Stmt.Block block => block.Statements.Any(ContainsSuperCall),
            Stmt.If ifStmt => ContainsSuperCallInExpr(ifStmt.Condition) ||
                              ContainsSuperCall(ifStmt.ThenBranch) ||
                              (ifStmt.ElseBranch != null && ContainsSuperCall(ifStmt.ElseBranch)),
            _ => false
        };
    }

    /// <summary>
    /// Checks if an expression contains a super() call.
    /// </summary>
    private static bool ContainsSuperCallInExpr(Expr expr)
    {
        return expr switch
        {
            Expr.Call call => call.Callee is Expr.Super,
            Expr.Binary bin => ContainsSuperCallInExpr(bin.Left) || ContainsSuperCallInExpr(bin.Right),
            Expr.Logical log => ContainsSuperCallInExpr(log.Left) || ContainsSuperCallInExpr(log.Right),
            Expr.Grouping grp => ContainsSuperCallInExpr(grp.Expression),
            _ => false
        };
    }

    /// <summary>
    /// Emits an instance method body for a class expression.
    /// </summary>
    private void EmitClassExpressionMethod(
        Expr.ClassExpr classExpr,
        TypeBuilder typeBuilder,
        Stmt.Function method,
        FieldInfo fieldsField)
    {
        if (method.IsAbstract) return;

        if (!_classExprs.InstanceMethods[classExpr].TryGetValue(method.Name.Lexeme, out var methodBuilder))
            return;

        // Handle async methods via state machine
        if (method.IsAsync)
        {
            EmitClassExpressionAsyncMethod(classExpr, typeBuilder, method, methodBuilder, fieldsField);
            return;
        }

        var il = methodBuilder.GetILGenerator();
        var ctx = CreateClassExpressionContext(il, classExpr, typeBuilder, fieldsField);
        ctx.IsInstanceMethod = true;

        // Define parameters with typed parameter types from method signature
        var methodParams = methodBuilder.GetParameters();
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            Type? paramType = i < methodParams.Length ? methodParams[i].ParameterType : null;
            ctx.DefineParameter(method.Parameters[i].Name.Lexeme, i + 1, paramType);
        }

        var emitter = new ILEmitter(ctx);
        emitter.EmitDefaultParameters(method.Parameters, true);

        if (method.Body != null)
        {
            foreach (var stmt in method.Body)
            {
                emitter.EmitStatement(stmt);
            }
        }

        if (emitter.HasDeferredReturns)
        {
            emitter.FinalizeReturns();
        }
        else
        {
            // Falling off the end completes with `undefined` (ECMA-262). Route through
            // EmitDefaultReturnValue so an `object` slot materializes the `$Undefined`
            // sentinel instead of CLR null. (#588)
            EmitDefaultReturnValue(il, methodBuilder.ReturnType);
            il.Emit(OpCodes.Ret);
        }
    }

    /// <summary>
    /// Emits a static method body for a class expression.
    /// </summary>
    private void EmitClassExpressionStaticMethodBody(Expr.ClassExpr classExpr, Stmt.Function method)
    {
        if (!_classExprs.StaticMethods[classExpr].TryGetValue(method.Name.Lexeme, out var methodBuilder))
            return;

        var typeBuilder = _classExprs.Builders[classExpr];

        if (method.IsAsync)
        {
            EmitClassExpressionStaticAsyncMethod(classExpr, typeBuilder, method, methodBuilder);
            return;
        }

        var il = methodBuilder.GetILGenerator();
        var ctx = CreateClassExpressionContext(il, classExpr, typeBuilder, null);
        ctx.IsInstanceMethod = false;

        // Define parameters with typed parameter types from method signature (starting at index 0 for static)
        var methodParams = methodBuilder.GetParameters();
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            Type? paramType = i < methodParams.Length ? methodParams[i].ParameterType : null;
            ctx.DefineParameter(method.Parameters[i].Name.Lexeme, i, paramType);
        }

        var emitter = new ILEmitter(ctx);
        emitter.EmitDefaultParameters(method.Parameters, false);

        if (method.Body != null)
        {
            foreach (var stmt in method.Body)
            {
                emitter.EmitStatement(stmt);
            }
        }

        if (emitter.HasDeferredReturns)
        {
            emitter.FinalizeReturns();
        }
        else
        {
            // Falling off the end completes with `undefined` (ECMA-262). Route through
            // EmitDefaultReturnValue so an `object` slot materializes the `$Undefined`
            // sentinel instead of CLR null. (#588)
            EmitDefaultReturnValue(il, methodBuilder.ReturnType);
            il.Emit(OpCodes.Ret);
        }
    }

    /// <summary>
    /// Emits an accessor body for a class expression.
    /// </summary>
    /// <summary>
    /// Emits the bodies of the synthetic symbol-keyed accessor methods (#281)
    /// recorded for a class expression by <see cref="DefineSymbolAccessorMethod"/>.
    /// Uses the class-expression compilation context (so `this`, fields, and
    /// top-level bindings resolve) rather than the class-declaration accessor path.
    /// </summary>
    private void EmitClassExpressionSymbolAccessors(
        Expr.ClassExpr classExpr,
        TypeBuilder typeBuilder,
        FieldInfo fieldsField)
    {
        if (!_classes.SymbolAccessors.TryGetValue(typeBuilder.Name, out var list))
            return;

        foreach (var (accessor, methodBuilder) in list)
        {
            var il = methodBuilder.GetILGenerator();
            var ctx = CreateClassExpressionContext(il, classExpr, typeBuilder, accessor.IsStatic ? null : fieldsField);
            ctx.IsInstanceMethod = !accessor.IsStatic;

            if (accessor.Kind.Type == TokenType.SET && accessor.SetterParam != null)
            {
                var accessorParams = methodBuilder.GetParameters();
                Type? paramType = accessorParams.Length > 0 ? accessorParams[0].ParameterType : null;
                // Instance setter: arg0 is `this`, value at index 1. Static: value at index 0.
                int paramIndex = accessor.IsStatic ? 0 : 1;
                ctx.DefineParameter(accessor.SetterParam.Name.Lexeme, paramIndex, paramType);
            }

            var emitter = new ILEmitter(ctx);
            foreach (var stmt in accessor.Body)
                emitter.EmitStatement(stmt);

            if (emitter.HasDeferredReturns)
                emitter.FinalizeReturns();
            else
            {
                // Falling off the end completes with `undefined` (ECMA-262); the slot is
                // `object`, so emit the `$Undefined` sentinel instead of CLR null. (#588)
                EmitDefaultReturnValue(il, methodBuilder.ReturnType);
                il.Emit(OpCodes.Ret);
            }
        }
    }

    private void EmitClassExpressionAccessor(
        Expr.ClassExpr classExpr,
        TypeBuilder typeBuilder,
        Stmt.Accessor accessor,
        FieldInfo fieldsField)
    {
        string pascalName = NamingConventions.ToPascalCase(accessor.Name.Lexeme);
        MethodBuilder? methodBuilder = accessor.Kind.Type == TokenType.GET
            ? _classExprs.Getters[classExpr].GetValueOrDefault(pascalName)
            : _classExprs.Setters[classExpr].GetValueOrDefault(pascalName);

        if (methodBuilder == null) return;

        var il = methodBuilder.GetILGenerator();
        var ctx = CreateClassExpressionContext(il, classExpr, typeBuilder, fieldsField);
        ctx.IsInstanceMethod = true;

        if (accessor.Kind.Type == TokenType.SET && accessor.SetterParam != null)
        {
            // Get typed parameter type from accessor method signature
            var accessorParams = methodBuilder.GetParameters();
            Type? paramType = accessorParams.Length > 0 ? accessorParams[0].ParameterType : null;
            ctx.DefineParameter(accessor.SetterParam.Name.Lexeme, 1, paramType);
        }

        var emitter = new ILEmitter(ctx);

        foreach (var stmt in accessor.Body)
        {
            emitter.EmitStatement(stmt);
        }

        if (emitter.HasDeferredReturns)
        {
            emitter.FinalizeReturns();
        }
        else
        {
            // Falling off the end completes with `undefined` (ECMA-262). Route through
            // EmitDefaultReturnValue so an `object` slot materializes the `$Undefined`
            // sentinel instead of CLR null. (#588)
            EmitDefaultReturnValue(il, methodBuilder.ReturnType);
            il.Emit(OpCodes.Ret);
        }
    }

    /// <summary>
    /// Emits an async instance method for a class expression using state machine.
    /// </summary>
    private void EmitClassExpressionAsyncMethod(
        Expr.ClassExpr classExpr,
        TypeBuilder typeBuilder,
        Stmt.Function method,
        MethodBuilder methodBuilder,
        FieldInfo fieldsField)
    {
        // For now, emit a simple wrapper that runs synchronously and returns Task.FromResult
        // Full state machine support can be added later if needed
        var il = methodBuilder.GetILGenerator();
        var ctx = CreateClassExpressionContext(il, classExpr, typeBuilder, fieldsField);
        ctx.IsInstanceMethod = true;

        // Define parameters with typed parameter types from method signature
        var methodParams = methodBuilder.GetParameters();
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            Type? paramType = i < methodParams.Length ? methodParams[i].ParameterType : null;
            ctx.DefineParameter(method.Parameters[i].Name.Lexeme, i + 1, paramType);
        }

        var emitter = new ILEmitter(ctx);
        emitter.EmitDefaultParameters(method.Parameters, true);

        // Emit body statements
        if (method.Body != null)
        {
            foreach (var stmt in method.Body)
            {
                emitter.EmitStatement(stmt);
            }
        }

        // Return Task.FromResult(null)
        if (!emitter.HasDeferredReturns)
        {
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(typeof(object)));
            il.Emit(OpCodes.Ret);
        }
        else
        {
            emitter.FinalizeReturns();
        }
    }

    /// <summary>
    /// Emits an async static method for a class expression.
    /// </summary>
    private void EmitClassExpressionStaticAsyncMethod(
        Expr.ClassExpr classExpr,
        TypeBuilder typeBuilder,
        Stmt.Function method,
        MethodBuilder methodBuilder)
    {
        var il = methodBuilder.GetILGenerator();
        var ctx = CreateClassExpressionContext(il, classExpr, typeBuilder, null);
        ctx.IsInstanceMethod = false;

        // Define parameters with typed parameter types from method signature
        var methodParams = methodBuilder.GetParameters();
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            Type? paramType = i < methodParams.Length ? methodParams[i].ParameterType : null;
            ctx.DefineParameter(method.Parameters[i].Name.Lexeme, i, paramType);
        }

        var emitter = new ILEmitter(ctx);
        emitter.EmitDefaultParameters(method.Parameters, false);

        // Emit body statements
        if (method.Body != null)
        {
            foreach (var stmt in method.Body)
            {
                emitter.EmitStatement(stmt);
            }
        }

        // Return Task.FromResult(null)
        if (!emitter.HasDeferredReturns)
        {
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(typeof(object)));
            il.Emit(OpCodes.Ret);
        }
        else
        {
            emitter.FinalizeReturns();
        }
    }
}
