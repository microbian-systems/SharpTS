using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// Accessor (getter/setter) emission for class compilation.
/// </summary>
public partial class ILCompiler
{
    private void EmitAccessor(TypeBuilder typeBuilder, Stmt.Accessor accessor, FieldInfo fieldsField)
    {
        // Use PascalCase naming convention: get_<PascalPropertyName> or set_<PascalPropertyName>
        string pascalName = NamingConventions.ToPascalCase(accessor.Name.Lexeme);
        string methodName = accessor.Kind.Type == TokenType.GET
            ? $"get_{pascalName}"
            : $"set_{pascalName}";

        string className = typeBuilder.Name;
        MethodBuilder methodBuilder;

        // Check if accessor was pre-defined in DefineClassMethodsOnly
        if (_classes.PreDefinedAccessors.TryGetValue(className, out var preDefinedAcc) &&
            preDefinedAcc.TryGetValue(methodName, out var existingAccessor))
        {
            methodBuilder = existingAccessor;
        }
        else
        {
            // Define the accessor (fallback for when DefineClassMethodsOnly wasn't called)
            Type[] paramTypes = accessor.Kind.Type == TokenType.SET
                ? [typeof(object)]
                : [];

            MethodAttributes methodAttrs = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
            methodAttrs |= accessor.IsStatic ? MethodAttributes.Static : MethodAttributes.Virtual;
            if (accessor.IsAbstract)
            {
                methodAttrs |= MethodAttributes.Abstract;
            }

            methodBuilder = typeBuilder.DefineMethod(
                methodName,
                methodAttrs,
                typeof(object),
                paramTypes
            );

            // Static accessors register in StaticGetters/StaticSetters keyed by the original
            // (camelCase) name so ClassRegistry lookups match. Instance accessors register in
            // InstanceGetters/Setters keyed by PascalCase, preserving existing convention.
            if (accessor.Kind.Type == TokenType.GET)
            {
                if (accessor.IsStatic)
                {
                    if (!_classes.StaticGetters.TryGetValue(className, out var classStaticGetters))
                    {
                        classStaticGetters = [];
                        _classes.StaticGetters[className] = classStaticGetters;
                    }
                    classStaticGetters[accessor.Name.Lexeme] = methodBuilder;
                }
                else
                {
                    if (!_classes.InstanceGetters.TryGetValue(className, out var classGetters))
                    {
                        classGetters = [];
                        _classes.InstanceGetters[className] = classGetters;
                    }
                    classGetters[pascalName] = methodBuilder;
                }
            }
            else
            {
                if (accessor.IsStatic)
                {
                    if (!_classes.StaticSetters.TryGetValue(className, out var classStaticSetters))
                    {
                        classStaticSetters = [];
                        _classes.StaticSetters[className] = classStaticSetters;
                    }
                    classStaticSetters[accessor.Name.Lexeme] = methodBuilder;
                }
                else
                {
                    if (!_classes.InstanceSetters.TryGetValue(className, out var classSetters))
                    {
                        classSetters = [];
                        _classes.InstanceSetters[className] = classSetters;
                    }
                    classSetters[pascalName] = methodBuilder;
                }
            }
        }

        // Apply accessor-level decorators as .NET attributes
        if (_decoratorMode != DecoratorMode.None)
        {
            ApplyAccessorDecorators(accessor, methodBuilder);
        }

        // Abstract accessors have no body
        if (accessor.IsAbstract)
        {
            return;
        }

        var il = methodBuilder.GetILGenerator();
        var ctx = new CompilationContext(il, _typeMapper, _functions.Builders, _classes.Builders, _types)
        {
            FieldsField = fieldsField,
            IsInstanceMethod = !accessor.IsStatic,
            CurrentClassName = className,
            CurrentClassBuilder = typeBuilder,
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
            IsStrictMode = _isStrictMode,
            // Registry services
            ClassRegistry = GetClassRegistry()
        };

        // Add class generic type parameters to context
        if (_classes.GenericParams.TryGetValue(typeBuilder.Name, out var classGenericParams))
        {
            foreach (var gp in classGenericParams)
                ctx.GenericTypeParameters[gp.Name] = gp;
        }

        // Define setter parameter if applicable with typed parameter type.
        // For instance setters, arg0 is `this` and the value is at index 1.
        // For static setters, the value is at index 0 (no implicit receiver).
        if (accessor.Kind.Type == TokenType.SET && accessor.SetterParam != null)
        {
            var accessorParams = methodBuilder.GetParameters();
            Type? paramType = accessorParams.Length > 0 ? accessorParams[0].ParameterType : null;
            int paramIndex = accessor.IsStatic ? 0 : 1;
            ctx.DefineParameter(accessor.SetterParam.Name.Lexeme, paramIndex, paramType);
        }

        var emitter = new ILEmitter(ctx);

        foreach (var stmt in accessor.Body)
        {
            emitter.EmitStatement(stmt);
        }

        // Finalize any deferred returns from exception blocks
        if (emitter.HasDeferredReturns)
        {
            emitter.FinalizeReturns();
        }
        else
        {
            // Default return null
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
        }
    }
}
