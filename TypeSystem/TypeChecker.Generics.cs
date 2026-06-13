using SharpTS.TypeSystem.Exceptions;
using System.Collections.Frozen;

namespace SharpTS.TypeSystem;

/// <summary>
/// Generic type handling - core instantiation and type argument resolution.
/// </summary>
/// <remarks>
/// Contains core generic type methods:
/// ParseGenericTypeReference, ResolveGenericType, SplitTypeArguments, SubstituteTypeParamInString,
/// InstantiateGenericClass, InstantiateGenericInterface, InstantiateGenericFunction,
/// ResolveTypeArgumentsWithDefaults.
///
/// Related partial files:
/// - TypeChecker.Generics.Substitution.cs: Type parameter substitution and tuple flattening
/// - TypeChecker.Generics.Inference.cs: Type argument inference from call arguments
/// - TypeChecker.Generics.MappedTypes.cs: KeyOf evaluation and mapped type expansion
/// - TypeChecker.Generics.UtilityTypes.cs: Built-in utility type expansions (Partial, Required, etc.)
/// - TypeChecker.Generics.Conditional.cs: Conditional type evaluation and infer patterns
/// </remarks>
public partial class TypeChecker
{
    /// <summary>
    /// Parses a generic type reference like "Box&lt;number&gt;" or "Map&lt;string, number&gt;".
    /// Also handles array suffixes: "Partial&lt;T&gt;[]", "Box&lt;number&gt;[][]".
    /// </summary>
    private TypeInfo ParseGenericTypeReference(string typeName)
    {
        int openAngle = typeName.IndexOf('<');
        string baseName = typeName[..openAngle];

        // Find matching closing '>' respecting nested angle brackets
        // Skip `>` that is part of `=>` (arrow function syntax)
        int angleDepth = 0;
        int closeAngle = -1;
        for (int i = openAngle; i < typeName.Length; i++)
        {
            char c = typeName[i];
            if (c == '<') angleDepth++;
            else if (c == '>')
            {
                // Skip `>` that is part of `=>` (arrow function return type)
                if (i > 0 && typeName[i - 1] == '=')
                    continue;

                angleDepth--;
                if (angleDepth == 0)
                {
                    closeAngle = i;
                    break;
                }
            }
        }

        string argsStr = typeName[(openAngle + 1)..closeAngle];
        string suffix = typeName[(closeAngle + 1)..];

        // Split type arguments respecting nesting
        var typeArgStrings = SplitTypeArguments(argsStr);
        var typeArgs = typeArgStrings.Select(ToTypeInfo).ToList();

        return ResolveGenericType(baseName, typeArgs, typeArgStrings, suffix);
    }

    /// <summary>
    /// Resolves a generic type with pre-parsed TypeInfo arguments.
    /// Avoids string round-trip when TypeInfo objects are already available.
    /// </summary>
    /// <param name="baseName">The generic type name (e.g., "Promise", "Box").</param>
    /// <param name="typeArgs">The type arguments as TypeInfo objects.</param>
    /// <param name="suffix">Optional array suffix (e.g., "[][]").</param>
    /// <returns>The resolved type.</returns>
    private TypeInfo ResolveGenericType(string baseName, List<TypeInfo> typeArgs, string suffix = "")
    {
        // Lazily compute string representations only when needed for type alias expansion
        List<string>? typeArgStrings = null;
        return ResolveGenericType(baseName, typeArgs, typeArgStrings, suffix);
    }

    /// <summary>
    /// Core generic type resolution logic.
    /// </summary>
    private TypeInfo ResolveGenericType(string baseName, List<TypeInfo> typeArgs, List<string>? typeArgStrings, string suffix)
    {
        TypeInfo result;

        // Handle built-in generic types
        if (baseName is "Array" or "ReadonlyArray")
        {
            // Array<T> is the generic spelling of T[] — without this it would fall through the
            // user-generics lookup to `any`, making every Array<T>-typed position vacuously
            // compatible. ReadonlyArray<T> carries the readonly flag so an interface extending it
            // (DeepReadonlyArray) gets a read-only numeric index signature (#337 item 2).
            if (typeArgs.Count != 1)
            {
                throw new TypeCheckException($" {baseName} requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            }
            result = new TypeInfo.Array(typeArgs[0], IsReadonly: baseName == "ReadonlyArray");
        }
        else if (baseName == "Promise")
        {
            if (typeArgs.Count != 1)
            {
                throw new TypeCheckException($" Promise requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            }
            // Flatten nested Promises: Promise<Promise<T>> -> Promise<T>
            TypeInfo valueType = typeArgs[0];
            while (valueType is TypeInfo.Promise nested)
            {
                valueType = nested.ValueType;
            }
            result = new TypeInfo.Promise(valueType);
        }
        else if (baseName == "Generator")
        {
            if (typeArgs.Count != 1)
            {
                throw new TypeCheckException($" Generator requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            }
            result = new TypeInfo.Generator(typeArgs[0]);
        }
        else if (baseName == "AsyncGenerator")
        {
            if (typeArgs.Count != 1)
            {
                throw new TypeCheckException($" AsyncGenerator requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            }
            result = new TypeInfo.AsyncGenerator(typeArgs[0]);
        }
        // The built-in collections carry dedicated TypeInfo records (with IsCompatible + member-type
        // support) but were previously only constructed from `new Map()`/`new Set()` expressions — a
        // type REFERENCE `Map<K, V>` fell through to the `any` fallback below, so annotations and
        // conditional-type check sides lost their element types (#347, the `Map<…, infer V>` example).
        else if (baseName == "Map")
        {
            if (typeArgs.Count != 2)
                throw new TypeCheckException($" Map requires exactly 2 type arguments, got {typeArgs.Count}.", tsCode: "TS2314");
            result = new TypeInfo.Map(typeArgs[0], typeArgs[1]);
        }
        else if (baseName == "Set")
        {
            if (typeArgs.Count != 1)
                throw new TypeCheckException($" Set requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            result = new TypeInfo.Set(typeArgs[0]);
        }
        else if (baseName == "WeakMap")
        {
            if (typeArgs.Count != 2)
                throw new TypeCheckException($" WeakMap requires exactly 2 type arguments, got {typeArgs.Count}.", tsCode: "TS2314");
            result = new TypeInfo.WeakMap(typeArgs[0], typeArgs[1]);
        }
        else if (baseName == "WeakSet")
        {
            if (typeArgs.Count != 1)
                throw new TypeCheckException($" WeakSet requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            result = new TypeInfo.WeakSet(typeArgs[0]);
        }
        // Handle built-in utility types
        else if (baseName == "Partial")
        {
            if (typeArgs.Count != 1)
                throw new TypeCheckException($" Partial<T> requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandPartial(typeArgs[0]);
        }
        else if (baseName == "Required")
        {
            if (typeArgs.Count != 1)
                throw new TypeCheckException($" Required<T> requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandRequired(typeArgs[0]);
        }
        else if (baseName == "Readonly")
        {
            if (typeArgs.Count != 1)
                throw new TypeCheckException($" Readonly<T> requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandReadonly(typeArgs[0]);
        }
        else if (baseName == "Record")
        {
            if (typeArgs.Count != 2)
                throw new TypeCheckException($" Record<K, V> requires exactly 2 type arguments, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandRecordType(typeArgs[0], typeArgs[1]);
        }
        else if (baseName == "Pick")
        {
            if (typeArgs.Count != 2)
                throw new TypeCheckException($" Pick<T, K> requires exactly 2 type arguments, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandPick(typeArgs[0], typeArgs[1]);
        }
        else if (baseName == "Omit")
        {
            if (typeArgs.Count != 2)
                throw new TypeCheckException($" Omit<T, K> requires exactly 2 type arguments, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandOmit(typeArgs[0], typeArgs[1]);
        }
        // Additional utility types
        else if (baseName == "ReturnType")
        {
            if (typeArgs.Count != 1)
                throw new TypeCheckException($" ReturnType<T> requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandReturnType(typeArgs[0]);
        }
        else if (baseName == "Parameters")
        {
            if (typeArgs.Count != 1)
                throw new TypeCheckException($" Parameters<T> requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandParameters(typeArgs[0]);
        }
        else if (baseName == "ConstructorParameters")
        {
            if (typeArgs.Count != 1)
                throw new TypeCheckException($" ConstructorParameters<T> requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandConstructorParameters(typeArgs[0]);
        }
        else if (baseName == "InstanceType")
        {
            if (typeArgs.Count != 1)
                throw new TypeCheckException($" InstanceType<T> requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandInstanceType(typeArgs[0]);
        }
        else if (baseName == "ThisType")
        {
            if (typeArgs.Count != 1)
                throw new TypeCheckException($" ThisType<T> requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            // ThisType<T> is a marker type - it just wraps T for this-context typing
            result = typeArgs[0];
        }
        else if (baseName == "Awaited")
        {
            if (typeArgs.Count != 1)
                throw new TypeCheckException($" Awaited<T> requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandAwaited(typeArgs[0]);
        }
        else if (baseName == "NonNullable")
        {
            if (typeArgs.Count != 1)
                throw new TypeCheckException($" NonNullable<T> requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandNonNullable(typeArgs[0]);
        }
        else if (baseName == "Extract")
        {
            if (typeArgs.Count != 2)
                throw new TypeCheckException($" Extract<T, U> requires exactly 2 type arguments, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandExtract(typeArgs[0], typeArgs[1]);
        }
        else if (baseName == "Exclude")
        {
            if (typeArgs.Count != 2)
                throw new TypeCheckException($" Exclude<T, U> requires exactly 2 type arguments, got {typeArgs.Count}.", tsCode: "TS2314");
            result = ExpandExclude(typeArgs[0], typeArgs[1]);
        }
        else if (baseName is "Uppercase" or "Lowercase" or "Capitalize" or "Uncapitalize")
        {
            if (typeArgs.Count != 1)
                throw new TypeCheckException($" {baseName}<T> requires exactly 1 type argument, got {typeArgs.Count}.", tsCode: "TS2314");

            var operation = baseName switch
            {
                "Uppercase" => StringManipulation.Uppercase,
                "Lowercase" => StringManipulation.Lowercase,
                "Capitalize" => StringManipulation.Capitalize,
                "Uncapitalize" => StringManipulation.Uncapitalize,
                _ => throw new InvalidOperationException()
            };
            result = EvaluateIntrinsicStringType(typeArgs[0], operation);
        }
        else
        {
            // Check for generic type alias first
            var genericAlias = _environment.GetGenericTypeAlias(baseName);
            if (genericAlias != null)
            {
                var (definition, typeParamNames, _) = genericAlias.Value;
                if (typeArgs.Count != typeParamNames.Count)
                {
                    throw new TypeCheckException($" Type alias '{baseName}' requires {typeParamNames.Count} type argument(s), got {typeArgs.Count}.", tsCode: "TS2314");
                }

                // A type argument that mentions a still-open type variable (a mapped-type
                // parameter mid-parse) cannot be instantiated yet — eager expansion would
                // substitute an open term and derive a fresh instantiation key every round
                // (Part[P], Part[P][P], … — the #185 regress), never converging. Defer:
                // ExpandMappedType substitutes the concrete key into the arguments and
                // ExpandRecursiveTypeAlias finishes the instantiation on demand.
                if (typeArgs.Any(ContainsOpenTypeVariable))
                {
                    TypeInfo deferredAlias = new TypeInfo.RecursiveTypeAlias(baseName, typeArgs);
                    while (suffix.StartsWith("[]"))
                    {
                        deferredAlias = new TypeInfo.Array(deferredAlias);
                        suffix = suffix[2..];
                    }
                    return deferredAlias;
                }

                // Lazily compute string representations for type alias expansion
                typeArgStrings ??= typeArgs.Select(TypeInfoToString).ToList();

                // Create a unique key for this instantiation to detect recursive references
                string aliasKey = $"{baseName}<{string.Join(",", typeArgStrings)}>";
                _typeAliasExpansionStack ??= new HashSet<string>(StringComparer.Ordinal);

                // tsc bounds instantiation with a hard depth limit (checker.ts instantiationDepth →
                // TS2589). Without one, alias expansions whose instantiation key never repeats —
                // e.g. DeepReadonly<T> over a self-referential interface, where the still-unbound
                // mapped param accumulates: Part[P], Part[P][P], … — recurse until stack overflow
                // instead of hitting the same-key guard below. See #185.
                if (_typeAliasExpansionStack.Count >= MaxTypeAliasExpansionDepth)
                {
                    throw new TypeCheckException(
                        " Type instantiation is excessively deep and possibly infinite.",
                        tsCode: "TS2589");
                }

                // Recursive reference detected - return deferred placeholder
                if (_typeAliasExpansionStack.Contains(aliasKey))
                {
                    // Handle array suffix before returning
                    TypeInfo recursiveResult = new TypeInfo.RecursiveTypeAlias(baseName, typeArgs);
                    while (suffix.StartsWith("[]"))
                    {
                        recursiveResult = new TypeInfo.Array(recursiveResult);
                        suffix = suffix[2..];
                    }
                    return recursiveResult;
                }

                _typeAliasExpansionStack.Add(aliasKey);
                try
                {
                    // Substitute type parameters in the definition string
                    string expanded = definition;
                    for (int i = 0; i < typeParamNames.Count; i++)
                    {
                        // Replace type parameter with actual type argument string
                        expanded = SubstituteTypeParamInString(expanded, typeParamNames[i], typeArgStrings[i]);
                    }

                    // Parse the expanded definition
                    result = ToTypeInfo(expanded);

                    // The string pipeline substitutes arguments BEFORE parsing, so a distributive
                    // alias (`type Check<T> = T extends ...` — naked-param check) instantiated
                    // with a union parses as a union-checked conditional and would lose its
                    // distributivity. Recover the flag from the DECLARED definition's check side.
                    if (result is TypeInfo.ConditionalType parsedCond && !parsedCond.IsDistributive)
                    {
                        int declExtendsIdx = FindTopLevelKeyword(definition, " extends ");
                        if (declExtendsIdx > 0 && typeParamNames.Contains(definition[..declExtendsIdx].Trim()))
                            result = parsedCond with { IsDistributive = true };
                    }

                    // An instantiation whose arguments are fully concrete can apply its result
                    // now — downstream consumers (property access in particular) operate on
                    // the resolved type, not on a raw ConditionalType/MappedType node (#185).
                    // EvaluateConditionalType itself defers when the check type is still a
                    // naked type parameter, so generic-context instantiations stay deferred.
                    if (result is TypeInfo.ConditionalType condResult && !ContainsOpenTypeVariable(condResult))
                    {
                        result = EvaluateConditionalType(condResult);
                    }
                    // A mapped type over a DEFERRED key domain (a generic key-filter such as
                    // Pick<T, FunctionPropertyNames<T>>) must stay a MappedType node: enumerating
                    // it now would yield an empty object and lose the key filter, so it has to
                    // reach the relation rules deferred (#337 item 1, mirrors the conditional case
                    // just above). Concrete domains still expand eagerly for downstream consumers.
                    if (result is TypeInfo.MappedType mappedResult && !ContainsOpenTypeVariable(mappedResult)
                        && !IsDeferredKeyDomain(ResolveMappedKeyDomain(mappedResult)))
                    {
                        result = ExpandMappedType(mappedResult);
                    }

                    // Flatten any spread elements that contain concrete tuples
                    result = FlattenTupleSpreads(result);

                    // Validate spread constraints after type alias instantiation
                    ValidateSpreadConstraints(result);
                }
                finally
                {
                    _typeAliasExpansionStack.Remove(aliasKey);
                }
            }
            else
            {
                // Look up the generic definition
                TypeInfo? genericDef = _environment.Get(baseName);

                result = genericDef switch
                {
                    TypeInfo.GenericClass gc => new TypeInfo.Instance(InstantiateGenericClass(gc, typeArgs)),
                    TypeInfo.GenericInterface gi => InstantiateGenericInterface(gi, typeArgs),
                    TypeInfo.GenericFunction gf => InstantiateGenericFunction(gf, typeArgs),
                    _ => new TypeInfo.Any() // Unknown generic type - fallback to any
                };
            }
        }

        // Handle array suffix(es) after the generic type
        while (suffix.StartsWith("[]"))
        {
            result = new TypeInfo.Array(result);
            suffix = suffix[2..];
        }

        return result;
    }

    /// <summary>
    /// True when the type mentions a type variable that is currently open — a mapped-type
    /// parameter whose owning body is mid-parse (see <c>_openTypeVariablesInScope</c>).
    /// Such a type is not yet instantiable; generic alias references over it are deferred (#185).
    /// </summary>
    private static bool ContainsOpenTypeVariable(TypeInfo type)
    {
        if (_openTypeVariablesInScope is not { Count: > 0 }) return false;
        return Walk(type);

        static bool Walk(TypeInfo t) => t switch
        {
            TypeInfo.TypeParameter tp => _openTypeVariablesInScope!.Contains(tp.Name),
            TypeInfo.Array a => Walk(a.ElementType),
            TypeInfo.Union u => u.Types.Any(Walk),
            TypeInfo.Intersection i => i.Types.Any(Walk),
            TypeInfo.IndexedAccess ia => Walk(ia.ObjectType) || Walk(ia.IndexType),
            TypeInfo.KeyOf k => Walk(k.SourceType),
            TypeInfo.RecursiveTypeAlias rta => rta.TypeArguments?.Any(Walk) ?? false,
            TypeInfo.ConditionalType c => Walk(c.CheckType) || Walk(c.ExtendsType) || Walk(c.TrueType) || Walk(c.FalseType),
            TypeInfo.Promise p => Walk(p.ValueType),
            TypeInfo.Tuple tup => tup.Elements.Any(e => Walk(e.Type)) || (tup.RestElementType is { } rest && Walk(rest)),
            TypeInfo.Record r => r.Fields.Values.Any(Walk)
                || (r.StringIndexType is { } sit && Walk(sit))
                || (r.NumberIndexType is { } nit && Walk(nit))
                || (r.SymbolIndexType is { } yit && Walk(yit)),
            TypeInfo.Function f => f.ParamTypes.Any(Walk) || Walk(f.ReturnType),
            TypeInfo.MappedType m => Walk(m.Constraint) || Walk(m.ValueType) || (m.AsClause is { } asc && Walk(asc)),
            TypeInfo.IntrinsicStringType ist => Walk(ist.Inner),
            TypeInfo.TemplateLiteralType tlt => tlt.InterpolatedTypes.Any(Walk),
            _ => false
        };
    }

    /// <summary>
    /// Splits type arguments respecting nested angle brackets.
    /// </summary>
    private List<string> SplitTypeArguments(string argsStr)
    {
        List<string> args = [];
        int depth = 0;
        int start = 0;

        for (int i = 0; i < argsStr.Length; i++)
        {
            char c = argsStr[i];
            // Track all bracket types to handle tuples and function types in type arguments
            if (c == '<' || c == '[' || c == '(') depth++;
            else if (c == '>' || c == ']' || c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                args.Add(argsStr[start..i].Trim());
                start = i + 1;
            }
        }

        if (start < argsStr.Length)
        {
            args.Add(argsStr[start..].Trim());
        }

        return args;
    }

    /// <summary>
    /// Substitutes a type parameter name with an actual type string in a type definition.
    /// Handles word boundaries to avoid partial replacements (e.g., "T" shouldn't match "Type").
    /// </summary>
    private static string SubstituteTypeParamInString(string definition, string paramName, string replacement)
    {
        // Use word boundary matching to avoid partial replacements
        var result = new System.Text.StringBuilder();
        int i = 0;
        while (i < definition.Length)
        {
            // Check if we're at the start of paramName
            if (i + paramName.Length <= definition.Length &&
                definition.Substring(i, paramName.Length) == paramName)
            {
                // Check word boundaries
                bool startBoundary = i == 0 || !char.IsLetterOrDigit(definition[i - 1]);
                bool endBoundary = i + paramName.Length >= definition.Length ||
                                   !char.IsLetterOrDigit(definition[i + paramName.Length]);

                if (startBoundary && endBoundary)
                {
                    result.Append(replacement);
                    i += paramName.Length;
                    continue;
                }
            }
            result.Append(definition[i]);
            i++;
        }
        return result.ToString();
    }

    /// <summary>
    /// Instantiates a generic class with concrete type arguments.
    /// Supports default type parameters - missing arguments are filled with defaults.
    /// </summary>
    private TypeInfo InstantiateGenericClass(TypeInfo.GenericClass generic, List<TypeInfo> typeArgs)
    {
        // Fill in defaults for missing type arguments
        var resolvedTypeArgs = ResolveTypeArgumentsWithDefaults(generic.TypeParams, typeArgs, generic.Name);

        // Build substitution map first (needed for recursive constraints)
        Dictionary<string, TypeInfo> substitutions = [];
        for (int i = 0; i < resolvedTypeArgs.Count; i++)
        {
            substitutions[generic.TypeParams[i].Name] = resolvedTypeArgs[i];
        }

        // Validate constraints - substitute type params in constraint first to handle recursive constraints
        for (int i = 0; i < resolvedTypeArgs.Count; i++)
        {
            var tp = generic.TypeParams[i];
            if (tp.Constraint != null)
            {
                var substitutedConstraint = Substitute(tp.Constraint, substitutions);
                if (!IsCompatible(substitutedConstraint, resolvedTypeArgs[i]))
                {
                    throw new TypeCheckException($" Type '{resolvedTypeArgs[i]}' does not satisfy constraint '{substitutedConstraint}' for type parameter '{tp.Name}'.", tsCode: "TS2344");
                }
            }
        }

        return new TypeInfo.InstantiatedGeneric(generic, resolvedTypeArgs);
    }

    /// <summary>
    /// Instantiates a generic interface with concrete type arguments.
    /// Supports default type parameters - missing arguments are filled with defaults.
    /// </summary>
    private TypeInfo InstantiateGenericInterface(TypeInfo.GenericInterface generic, List<TypeInfo> typeArgs)
    {
        // Fill in defaults for missing type arguments
        var resolvedTypeArgs = ResolveTypeArgumentsWithDefaults(generic.TypeParams, typeArgs, generic.Name);

        // Build substitution map first (needed for recursive constraints like T extends TreeNode<T>)
        Dictionary<string, TypeInfo> substitutions = [];
        for (int i = 0; i < resolvedTypeArgs.Count; i++)
        {
            substitutions[generic.TypeParams[i].Name] = resolvedTypeArgs[i];
        }

        // Validate constraints - substitute type params in constraint first to handle recursive constraints
        for (int i = 0; i < resolvedTypeArgs.Count; i++)
        {
            var tp = generic.TypeParams[i];
            if (tp.Constraint != null)
            {
                // Substitute type parameters in the constraint (e.g., TreeNode<T> becomes TreeNode<MyNode>)
                var substitutedConstraint = Substitute(tp.Constraint, substitutions);
                if (!IsCompatible(substitutedConstraint, resolvedTypeArgs[i]))
                {
                    throw new TypeCheckException($" Type '{resolvedTypeArgs[i]}' does not satisfy constraint '{substitutedConstraint}' for type parameter '{tp.Name}'.", tsCode: "TS2344");
                }
            }
        }

        return new TypeInfo.InstantiatedGeneric(generic, resolvedTypeArgs);
    }

    /// <summary>
    /// Instantiates a generic function with concrete type arguments.
    /// Supports default type parameters - missing arguments are filled with defaults.
    /// </summary>
    private TypeInfo InstantiateGenericFunction(TypeInfo.GenericFunction generic, List<TypeInfo> typeArgs)
    {
        // Fill in defaults for missing type arguments
        var resolvedTypeArgs = ResolveTypeArgumentsWithDefaults(generic.TypeParams, typeArgs, "function");

        // Create substitution map first (needed for recursive constraints)
        Dictionary<string, TypeInfo> substitutions = [];
        for (int i = 0; i < resolvedTypeArgs.Count; i++)
        {
            substitutions[generic.TypeParams[i].Name] = resolvedTypeArgs[i];
        }

        // Validate constraints - substitute type params in constraint first to handle recursive constraints
        for (int i = 0; i < resolvedTypeArgs.Count; i++)
        {
            var tp = generic.TypeParams[i];
            if (tp.Constraint != null)
            {
                var substitutedConstraint = Substitute(tp.Constraint, substitutions);
                if (!IsCompatible(substitutedConstraint, resolvedTypeArgs[i]))
                {
                    throw new TypeCheckException($" Type '{resolvedTypeArgs[i]}' does not satisfy constraint '{substitutedConstraint}' for type parameter '{tp.Name}'.", tsCode: "TS2344");
                }
            }
        }

        // Substitute type parameters in the function signature
        var substitutedParams = generic.ParamTypes.Select(p => Substitute(p, substitutions)).ToList();
        var substitutedReturn = Substitute(generic.ReturnType, substitutions);

        return new TypeInfo.Function(substitutedParams, substitutedReturn, generic.RequiredParams, generic.HasRestParam);
    }

    /// <summary>
    /// Resolves type arguments, filling in defaults for missing arguments.
    /// </summary>
    /// <param name="typeParams">The type parameter definitions (with potential defaults).</param>
    /// <param name="typeArgs">The provided type arguments.</param>
    /// <param name="contextName">Name for error messages (e.g., class name).</param>
    /// <returns>Complete list of type arguments with defaults filled in.</returns>
    private List<TypeInfo> ResolveTypeArgumentsWithDefaults(
        List<TypeInfo.TypeParameter> typeParams,
        List<TypeInfo> typeArgs,
        string contextName)
    {
        // Count required type parameters (those without defaults)
        int requiredCount = typeParams.TakeWhile(tp => tp.Default == null).Count();

        if (typeArgs.Count < requiredCount)
        {
            throw new TypeCheckException($" Generic '{contextName}' requires at least {requiredCount} type argument(s), got {typeArgs.Count}.", tsCode: "TS2314");
        }

        if (typeArgs.Count > typeParams.Count)
        {
            throw new TypeCheckException($" Generic '{contextName}' has {typeParams.Count} type parameter(s), but got {typeArgs.Count} type argument(s).", tsCode: "TS2314");
        }

        // Build the resolved list
        List<TypeInfo> resolved = new(typeParams.Count);
        Dictionary<string, TypeInfo> substitutions = [];

        for (int i = 0; i < typeParams.Count; i++)
        {
            TypeInfo argType;
            if (i < typeArgs.Count)
            {
                // Use provided type argument
                argType = typeArgs[i];
            }
            else if (typeParams[i].Default != null)
            {
                // Use default, substituting any already-resolved type parameters
                argType = Substitute(typeParams[i].Default!, substitutions);
            }
            else
            {
                // Should not happen due to requiredCount check, but handle gracefully
                throw new TypeCheckException($" Missing type argument for type parameter '{typeParams[i].Name}' in generic '{contextName}'.", tsCode: "TS2314");
            }

            resolved.Add(argType);
            substitutions[typeParams[i].Name] = argType;
        }

        return resolved;
    }
}
