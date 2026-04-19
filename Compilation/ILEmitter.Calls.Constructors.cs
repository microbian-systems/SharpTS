using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// Object construction (new expressions) emission for the IL emitter.
/// Built-in constructors (40+ types) are handled by ExpressionEmitterBase.TryEmitBuiltInConstructor.
/// This override adds ILEmitter-specific features: namespace imports, imported class aliases,
/// external .NET types, class expressions, typed parameter conversion, and namespace class construction.
/// </summary>
public partial class ILEmitter
{
    protected override void EmitNew(Expr.New n)
    {
        // `new this(...)` inside a static method/accessor constructs the enclosing class.
        // The callee is Expr.This, which doesn't resolve to a class name via the normal
        // Variable/Get chain extraction — rewrite as Expr.Variable(CurrentClassName).
        if (n.Callee is Expr.This && !_ctx.IsInstanceMethod && _ctx.CurrentClassName != null)
        {
            var synthName = new Token(TokenType.IDENTIFIER, _ctx.CurrentClassName, null, 0);
            n = n with { Callee = new Expr.Variable(synthName) };
        }

        var (namespaceParts, className) = ExtractQualifiedNameFromCallee(n.Callee);

        // 1. Built-in constructors (Date, Map, Set, Promise, Headers, etc.) - handled by base
        if (namespaceParts.Count == 0 && n.Callee is Expr.Variable && TryEmitBuiltInConstructor(className, n.Arguments))
            return;

        // 2. Intl namespace constructors (Intl.NumberFormat, etc.) - handled by base
        if (TryEmitIntlConstructor(namespaceParts, className, n.Arguments))
            return;

        // 3. Module-qualified constructors (util.TextEncoder, etc.) - handled by base
        if (TryEmitModuleQualifiedConstructor(namespaceParts, className, n.Arguments))
            return;

        // 4. ILEmitter-specific: resolve class name with namespace imports and imported class aliases
        string resolvedClassName = ResolveClassNameForNew(namespaceParts, className);

        // 5. ILEmitter-specific: external .NET type construction
        if (_ctx.TypeMapper.ExternalTypes.TryGetValue(className, out var externalType) ||
            _ctx.TypeMapper.ExternalTypes.TryGetValue(resolvedClassName, out externalType))
        {
            EmitExternalTypeConstruction(externalType, n.Arguments);
            return;
        }

        // 6. Class declaration constructor with typed parameter emission
        var ctorBuilder = _ctx.ClassRegistry?.GetConstructorByQualifiedName(resolvedClassName);
        if (_ctx.Classes.TryGetValue(resolvedClassName, out var typeBuilder) && ctorBuilder != null)
        {
            EmitTypedClassConstruction(typeBuilder, ctorBuilder, resolvedClassName, n);
            return;
        }

        // 7. ILEmitter-specific: class expression constructor
        if (_ctx.VarToClassExpr != null &&
            _ctx.VarToClassExpr.TryGetValue(className, out var classExpr) &&
            _ctx.ClassExprConstructors != null &&
            _ctx.ClassExprConstructors.TryGetValue(classExpr, out var classExprCtor))
        {
            EmitTypedClassExprConstruction(classExprCtor, n);
            return;
        }

        // 8. ILEmitter-specific: namespace-qualified class (e.g., new Shapes.Circle(2))
        if (namespaceParts.Count > 0 && TryEmitNamespaceClassConstruction(namespaceParts, className, n.Arguments, n.TypeArgs))
            return;

        // 9. Fallback: local variable or resolver
        EmitFallbackConstruction(className, n);
    }

    /// <summary>
    /// Resolves class name considering namespace imports and imported class aliases.
    /// Extends base with additional external type checks.
    /// </summary>
    protected override string ResolveClassNameForNew(List<string> namespaceParts, string className)
    {
        if (namespaceParts.Count > 0)
        {
            string nsAlias = namespaceParts[0];
            if (_ctx.NamespaceImports?.TryGetValue(nsAlias, out var modulePath) == true)
            {
                if (_ctx.ExportedClasses?.TryGetValue(modulePath, out var exportedClasses) == true &&
                    exportedClasses.TryGetValue(className, out var qualifiedName))
                    return qualifiedName;

                string nsPath = string.Join("_", namespaceParts);
                return $"{nsPath}_{className}";
            }
            else
            {
                string nsPath = string.Join("_", namespaceParts);
                return $"{nsPath}_{className}";
            }
        }

        if (_ctx.ImportedClassAliases?.TryGetValue(className, out var importedClassName) == true)
            return importedClassName;

        return _ctx.ResolveClassName(className);
    }

    /// <summary>
    /// Emits class construction with typed parameter conversion (EmitConversionForParameter)
    /// and generic class support. This is more optimal than the base's EnsureBoxed approach.
    /// </summary>
    private void EmitTypedClassConstruction(TypeBuilder typeBuilder, ConstructorBuilder ctorBuilder, string resolvedClassName, Expr.New n)
    {
        Type targetType = typeBuilder;
        ConstructorInfo targetCtor = ctorBuilder;

        // Handle generic class instantiation (e.g., new Box<number>(42))
        if (n.TypeArgs != null && n.TypeArgs.Count > 0 &&
            _ctx.ClassRegistry!.GetGenericParams(resolvedClassName) != null)
        {
            Type[] typeArgs = n.TypeArgs.Select(ResolveTypeArg).ToArray();
            targetType = typeBuilder.MakeGenericType(typeArgs);
            targetCtor = TypeBuilder.GetConstructor(targetType, ctorBuilder);
        }

        var ctorParams = ctorBuilder.GetParameters();
        int expectedParamCount = ctorParams.Length;

        // Emit arguments with proper type conversions
        for (int i = 0; i < n.Arguments.Count; i++)
        {
            EmitExpression(n.Arguments[i]);
            if (i < ctorParams.Length)
                EmitConversionForParameter(n.Arguments[i], ctorParams[i].ParameterType);
            else
                EmitBoxIfNeeded(n.Arguments[i]);
        }

        for (int i = n.Arguments.Count; i < expectedParamCount; i++)
            EmitDefaultForType(ctorParams[i].ParameterType);

        IL.Emit(OpCodes.Newobj, targetCtor);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits class expression construction with typed parameter conversion.
    /// </summary>
    private void EmitTypedClassExprConstruction(ConstructorBuilder classExprCtor, Expr.New n)
    {
        var ctorParams = classExprCtor.GetParameters();
        int expectedParamCount = ctorParams.Length;

        for (int i = 0; i < n.Arguments.Count; i++)
        {
            EmitExpression(n.Arguments[i]);
            if (i < ctorParams.Length)
                EmitConversionForParameter(n.Arguments[i], ctorParams[i].ParameterType);
            else
                EmitBoxIfNeeded(n.Arguments[i]);
        }

        for (int i = n.Arguments.Count; i < expectedParamCount; i++)
            EmitDefaultForType(ctorParams[i].ParameterType);

        IL.Emit(OpCodes.Newobj, classExprCtor);
        SetStackUnknown();
    }

    /// <summary>
    /// Fallback: try to load class from local variable or resolver and use Activator.CreateInstance.
    /// </summary>
    private void EmitFallbackConstruction(string className, Expr.New n)
    {
        var local = _ctx.Locals.GetLocal(className);
        if (local != null)
        {
            IL.Emit(OpCodes.Ldloc, local);
        }
        else if (_resolver.TryLoadVariable(className) != null)
        {
            // Variable loaded onto stack by the resolver
        }
        else
        {
            IL.Emit(OpCodes.Ldnull);
            return;
        }

        // Type reference on stack - use Activator.CreateInstance(Type, object[])
        IL.Emit(OpCodes.Ldc_I4, n.Arguments.Count);
        IL.Emit(OpCodes.Newarr, _ctx.Types.Object);

        for (int i = 0; i < n.Arguments.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            EmitExpression(n.Arguments[i]);
            EmitBoxIfNeeded(n.Arguments[i]);
            IL.Emit(OpCodes.Stelem_Ref);
        }

        var createInstanceMethod = _ctx.Types.GetMethod(_ctx.Types.Activator, "CreateInstance", _ctx.Types.Type, _ctx.Types.ObjectArray);
        IL.Emit(OpCodes.Call, createInstanceMethod!);
    }

    /// <summary>
    /// Tries to emit class construction for a namespace-qualified class (e.g., new Shapes.Circle(2)).
    /// </summary>
    private bool TryEmitNamespaceClassConstruction(List<string> namespaceParts, string className, List<Expr> arguments, List<string>? typeArgs)
    {
        string nsPath = namespaceParts[0];
        if (_ctx.NamespaceFields == null || !_ctx.NamespaceFields.TryGetValue(nsPath, out var nsField))
            return false;

        IL.Emit(OpCodes.Ldsfld, nsField);

        for (int i = 1; i < namespaceParts.Count; i++)
        {
            nsPath = $"{nsPath}.{namespaceParts[i]}";
            if (_ctx.NamespaceFields.TryGetValue(nsPath, out var nestedField))
            {
                IL.Emit(OpCodes.Pop);
                IL.Emit(OpCodes.Ldsfld, nestedField);
            }
            else
            {
                IL.Emit(OpCodes.Ldstr, namespaceParts[i]);
                IL.Emit(OpCodes.Call, _ctx.Runtime!.TSNamespaceGet);
            }
        }

        IL.Emit(OpCodes.Ldstr, className);
        IL.Emit(OpCodes.Call, _ctx.Runtime!.TSNamespaceGet);

        if (typeArgs != null && typeArgs.Count > 0)
        {
            IL.Emit(OpCodes.Castclass, _ctx.Types.Type);
            IL.Emit(OpCodes.Ldc_I4, typeArgs.Count);
            IL.Emit(OpCodes.Newarr, _ctx.Types.Type);

            for (int i = 0; i < typeArgs.Count; i++)
            {
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldc_I4, i);
                Type resolvedType = ResolveTypeArg(typeArgs[i]);
                IL.Emit(OpCodes.Ldtoken, resolvedType);
                IL.Emit(OpCodes.Call, Types.TypeGetTypeFromHandle);
                IL.Emit(OpCodes.Stelem_Ref);
            }

            var makeGenericTypeMethod = typeof(Type).GetMethod("MakeGenericType", [typeof(Type[])]);
            IL.Emit(OpCodes.Callvirt, makeGenericTypeMethod!);
        }

        IL.Emit(OpCodes.Ldc_I4, arguments.Count);
        IL.Emit(OpCodes.Newarr, _ctx.Types.Object);

        for (int i = 0; i < arguments.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            EmitExpression(arguments[i]);
            EmitBoxIfNeeded(arguments[i]);
            IL.Emit(OpCodes.Stelem_Ref);
        }

        var createInstanceMethod = _ctx.Types.GetMethod(_ctx.Types.Activator, "CreateInstance", _ctx.Types.Type, _ctx.Types.ObjectArray);
        IL.Emit(OpCodes.Call, createInstanceMethod!);

        SetStackUnknown();
        return true;
    }
}
