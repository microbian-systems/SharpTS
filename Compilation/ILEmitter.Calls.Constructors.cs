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
    /// Fallback constructor emission for <c>new</c> expressions that didn't match a
    /// compile-time known class, class expression, or namespace class.
    ///
    /// For property-access callees like <c>new exports.Foo(...)</c>, evaluates the full
    /// callee and — if the runtime value is a .NET Type (stored by an earlier class-ref
    /// load) — constructs via reflection with ctor-arity-aware argument padding. If the
    /// runtime value is not a Type (e.g. a <c>$TSFunction</c> from a namespace import of
    /// a built-in module), falls back to pushing <c>null</c> rather than throwing, matching
    /// legacy behavior for those paths until a full TSFunction-as-constructor dispatch exists.
    /// </summary>
    private void EmitFallbackConstruction(string className, Expr.New n)
    {
        // Any callee that isn't a bare Variable goes through EmitCalleeExprConstruction
        // — it evaluates the expression once into a local and either constructs from a
        // Type or routes to NewOnFunction. Covers Get/GetIndex (mod.Foo, arr[0]),
        // TypeAssertion / Grouping / NonNullAssertion wrappers (very common — `(outer()
        // as any)('W')`), and direct Call/IIFE callees (`new (outer())('W')`).
        // Variable callees stay on the Ldloc-then-runtime-check path below so the
        // `if (!(this instanceof F)) return new F(args)` idiom (yallist, semver) keeps
        // its current Ldnull semantics for the unresolved-variable subcase — function-
        // identity and instanceof aren't yet aligned for $TSFunction values, so routing
        // those through NewOnFunction would recurse.
        if (n.Callee is not Expr.Variable)
        {
            EmitCalleeExprConstruction(n);
            return;
        }

        // Function declarations take priority over resolver/locals. The
        // resolver can spuriously claim ownership of names that match function
        // declarations (closure-analyzer-driven captures that resolve to null
        // at runtime). Function declarations are always reachable as static
        // methods on the Program class, so the explicit Functions check wins.
        // With Stage 0b (auto-prototype) + Stage 0c (NewOnFunction sets
        // prototype, instanceof walks chain), the yallist/semver
        // `if (!(this instanceof F)) return new F(args)` idiom no longer
        // recurses — the inner `new F(args)` constructs an instance whose
        // prototype chain links to F.prototype, so `this instanceof F` is
        // true on the outer recursive call and the redirect short-circuits.
        if (_ctx.Functions.TryGetValue(_ctx.ResolveFunctionName(className), out var funcMethod))
        {
            IL.Emit(OpCodes.Ldnull); // target (static method)
            IL.Emit(OpCodes.Ldtoken, funcMethod);
            if (_ctx.ProgramType != null)
            {
                IL.Emit(OpCodes.Ldtoken, _ctx.ProgramType);
                IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.MethodBase, "GetMethodFromHandle", _ctx.Types.RuntimeMethodHandle, _ctx.Types.RuntimeTypeHandle));
            }
            else
            {
                IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.MethodBase, "GetMethodFromHandle", _ctx.Types.RuntimeMethodHandle));
            }
            IL.Emit(OpCodes.Castclass, _ctx.Types.MethodInfo);
            int arity = 0;
            foreach (var param in funcMethod.GetParameters())
            {
                if (param.IsOptional) continue;
                if (param.ParameterType == typeof(List<object>)) continue;
                if (param.Name?.StartsWith("__") == true) continue;
                arity++;
            }
            IL.Emit(OpCodes.Ldstr, className);
            IL.Emit(OpCodes.Ldc_I4, arity);
            IL.Emit(OpCodes.Newobj, _ctx.Runtime!.TSFunctionCtorWithCache);
        }
        else
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
                // Last resort: evaluate the bare-Variable callee through the
                // standard expression emit path, which knows about non-resolver
                // globals (JSON, Math, Reflect, etc.) — those are non-constructable
                // singletons that should reach NewOnFunction's "not a constructor"
                // TypeError throw rather than vanish into a silent Ldnull.
                EmitExpression(n.Callee);
            }
        }

        // The loaded value is statically typed as `object` (DC fields, top-level static vars,
        // and captured fields are all object-typed). `EmitReflectionConstructFromType` expects
        // a `System.Type` on the stack — passing an `object` directly trips an ILVerify
        // `StackUnexpected` that the JIT sometimes tolerates and sometimes miscompiles into a
        // 0xC0000005 access violation when the value happens to not be a Type (e.g. a cross-
        // module CJS class reference loaded from the entry-point DC field as plain object).
        // Runtime-check for Type and fall through to null for non-Type values — mirrors
        // EmitCalleeExprConstruction's shape so both callee forms behave consistently.
        var objTemp = IL.DeclareLocal(_ctx.Types.Object);
        IL.Emit(OpCodes.Stloc, objTemp);

        var notTypeLabel = IL.DefineLabel();
        var doneLabel = IL.DefineLabel();

        IL.Emit(OpCodes.Ldloc, objTemp);
        IL.Emit(OpCodes.Isinst, _ctx.Types.Type);
        IL.Emit(OpCodes.Brfalse, notTypeLabel);

        IL.Emit(OpCodes.Ldloc, objTemp);
        IL.Emit(OpCodes.Castclass, _ctx.Types.Type);
        EmitReflectionConstructFromType(n);
        IL.Emit(OpCodes.Br, doneLabel);

        IL.MarkLabel(notTypeLabel);
        EmitNewOnFunctionCall(objTemp, n.Arguments);

        IL.MarkLabel(doneLabel);
    }

    /// <summary>
    /// For <c>new someExpr(...)</c> where <c>someExpr</c> is a property/index access or
    /// a call expression: evaluate, runtime-check for Type, and construct via reflection.
    /// Non-Type callable values (<c>$TSFunction</c>, <c>$BoundTSFunction</c>) route through
    /// <c>$Runtime.NewOnFunction</c> so the constructor body runs and <c>this</c> binds to
    /// the fresh instance — same JS <c>new</c> protocol the Ldloc branch in
    /// <see cref="EmitFallbackConstruction"/> uses. The callee-expression path doesn't
    /// hit the <c>if (!(this instanceof F)) return new F(args)</c> idiom that motivated
    /// the unresolved-variable Ldnull branch above (that idiom always names the
    /// constructor as a bare variable), so routing here is safe.
    /// </summary>
    private void EmitCalleeExprConstruction(Expr.New n)
    {
        EmitExpression(n.Callee);
        // Box value-typed callees (e.g. `new true;`, `new 1;`) before storing to an
        // object slot — without this the JIT crashes with a fatal CLR error on
        // the upcoming Stloc/Ldloc into `object`.
        EmitBoxIfNeeded(n.Callee);
        var objTemp = IL.DeclareLocal(_ctx.Types.Object);
        IL.Emit(OpCodes.Stloc, objTemp);

        var notTypeLabel = IL.DefineLabel();
        var doneLabel = IL.DefineLabel();

        IL.Emit(OpCodes.Ldloc, objTemp);
        IL.Emit(OpCodes.Isinst, _ctx.Types.Type);
        IL.Emit(OpCodes.Brfalse, notTypeLabel);

        // It's a Type — construct via reflection.
        IL.Emit(OpCodes.Ldloc, objTemp);
        IL.Emit(OpCodes.Castclass, _ctx.Types.Type);
        EmitReflectionConstructFromType(n);
        IL.Emit(OpCodes.Br, doneLabel);

        IL.MarkLabel(notTypeLabel);
        EmitNewOnFunctionCall(objTemp, n.Arguments);

        IL.MarkLabel(doneLabel);
    }

    /// <summary>
    /// Emits <c>$Runtime.NewOnFunction(callee, args)</c> — the JS <c>new</c> protocol
    /// for runtime-valued function callees (<c>$TSFunction</c>, <c>$BoundTSFunction</c>).
    /// Leaves the constructed object on the stack. Callee is read from
    /// <paramref name="calleeLocal"/>; args are evaluated and packed into an object[].
    /// </summary>
    private void EmitNewOnFunctionCall(LocalBuilder calleeLocal, List<Expr> arguments)
    {
        IL.Emit(OpCodes.Ldloc, calleeLocal);

        // args = new object[N] { arg0, arg1, ... }
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

        IL.Emit(OpCodes.Call, _ctx.Runtime!.NewOnFunction);
        SetStackUnknown();
    }

    /// <summary>
    /// Expects a Type on the stack. Emits IL to invoke the Type's first public constructor,
    /// padding the argument array to the ctor's declared arity with nulls so JS-style
    /// under-application still finds a matching overload.
    /// </summary>
    private void EmitReflectionConstructFromType(Expr.New n)
    {
        var typeLocal = IL.DeclareLocal(_ctx.Types.Type);
        IL.Emit(OpCodes.Stloc, typeLocal);

        List<LocalBuilder> argTemps = [];
        foreach (var arg in n.Arguments)
        {
            EmitExpression(arg);
            EmitBoxIfNeeded(arg);
            var t = IL.DeclareLocal(_ctx.Types.Object);
            IL.Emit(OpCodes.Stloc, t);
            argTemps.Add(t);
        }

        // ctor = type.GetConstructors()[0]
        var getConstructorsMethod = typeof(Type).GetMethod("GetConstructors", Type.EmptyTypes)!;
        IL.Emit(OpCodes.Ldloc, typeLocal);
        IL.Emit(OpCodes.Callvirt, getConstructorsMethod);
        IL.Emit(OpCodes.Ldc_I4_0);
        IL.Emit(OpCodes.Ldelem_Ref);
        var ctorLocal = IL.DeclareLocal(typeof(ConstructorInfo));
        IL.Emit(OpCodes.Stloc, ctorLocal);

        // arity = ctor.GetParameters().Length
        var getParametersMethod = typeof(MethodBase).GetMethod("GetParameters", Type.EmptyTypes)!;
        IL.Emit(OpCodes.Ldloc, ctorLocal);
        IL.Emit(OpCodes.Callvirt, getParametersMethod);
        IL.Emit(OpCodes.Ldlen);
        IL.Emit(OpCodes.Conv_I4);
        var arityLocal = IL.DeclareLocal(typeof(int));
        IL.Emit(OpCodes.Stloc, arityLocal);

        // args = new object[arity]; default-initialized to null.
        IL.Emit(OpCodes.Ldloc, arityLocal);
        IL.Emit(OpCodes.Newarr, _ctx.Types.Object);
        var argsArrayLocal = IL.DeclareLocal(_ctx.Types.ObjectArray);
        IL.Emit(OpCodes.Stloc, argsArrayLocal);

        for (int i = 0; i < argTemps.Count; i++)
        {
            var skipLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Ldc_I4, i);
            IL.Emit(OpCodes.Ldloc, arityLocal);
            IL.Emit(OpCodes.Bge, skipLabel);
            IL.Emit(OpCodes.Ldloc, argsArrayLocal);
            IL.Emit(OpCodes.Ldc_I4, i);
            IL.Emit(OpCodes.Ldloc, argTemps[i]);
            IL.Emit(OpCodes.Stelem_Ref);
            IL.MarkLabel(skipLabel);
        }

        // ctor.Invoke(args)
        var invokeMethod = typeof(ConstructorInfo).GetMethod("Invoke", [typeof(object[])])!;
        IL.Emit(OpCodes.Ldloc, ctorLocal);
        IL.Emit(OpCodes.Ldloc, argsArrayLocal);
        IL.Emit(OpCodes.Callvirt, invokeMethod);
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
