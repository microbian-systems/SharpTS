using System.Collections.Frozen;
using SharpTS.Parsing;
using SharpTS.TypeSystem.Exceptions;

namespace SharpTS.TypeSystem;

/// <summary>
/// Resolution of syntactic <see cref="TypeNode"/>s to semantic <see cref="TypeInfo"/> — the
/// node-first half of the type-AST migration (docs/plans/type-ast-design.md).
/// </summary>
/// <remarks>
/// Returns null for node kinds (or compositions) this slice doesn't resolve; callers fall back
/// to the authoritative string path. Resolution semantics deliberately REUSE the string path's
/// machinery where names are involved (a bare name has no string-scanning hazards), so the two
/// paths cannot diverge on lookup order, alias expansion, or scoping.
/// </remarks>
public partial class TypeChecker
{
    /// <summary>
    /// Resolves a type node to a <see cref="TypeInfo"/>, or null when the node (or a component)
    /// has no node-path resolution yet.
    /// </summary>
    internal TypeInfo? TryToTypeInfo(TypeNode node)
    {
        switch (node)
        {
            // A bare name resolves through the existing single-name path — type parameters,
            // aliases, primitives, classes, interfaces, the hot lib globals: identical semantics
            // by construction, with none of the scanning hazards strings have for COMPOSITE types.
            case NamedTypeNode { TypeArguments: null } named:
                return ToTypeInfo(named.Name);

            // Generic references resolve their argument nodes and reuse the SAME instantiation
            // machinery as the string path (built-in generics, utility types, generic
            // classes/interfaces/functions — including its TS2314 arity errors). Generic alias
            // references expand from their stored definition NODE when one exists, binding the
            // type parameters in a child scope instead of substituting argument strings.
            case NamedTypeNode { TypeArguments: { } argNodes } named:
            {
                List<TypeInfo> typeArgs = new(argNodes.Count);
                foreach (var argNode in argNodes)
                {
                    if (TryToTypeInfo(argNode) is not { } arg) return null;
                    typeArgs.Add(arg);
                }
                // ResolveGenericType handles built-in names BEFORE its alias lookup — a user
                // alias named e.g. `Partial` is shadowed. Mirror that precedence here.
                if (!IsBuiltInGenericName(named.Name) &&
                    _environment.GetGenericTypeAlias(named.Name) is { } alias)
                {
                    return alias.DefinitionNode is { } definitionNode
                        ? TryExpandGenericAliasFromNode(named.Name, definitionNode, alias.TypeParams, typeArgs)
                        : null;
                }
                return ResolveGenericType(named.Name, typeArgs);
            }

            case LiteralTypeNode lit:
                return lit.Value switch
                {
                    string str => new TypeInfo.StringLiteral(str),
                    double num => new TypeInfo.NumberLiteral(num),
                    bool b => new TypeInfo.BooleanLiteral(b),
                    _ => null,
                };

            case ArrayTypeNode arr:
                return TryToTypeInfo(arr.ElementType) is { } elem ? new TypeInfo.Array(elem) : null;

            case UnionTypeNode union:
            {
                List<TypeInfo> members = new(union.Members.Count);
                foreach (var member in union.Members)
                {
                    if (TryToTypeInfo(member) is not { } resolved) return null;
                    members.Add(resolved);
                }
                // Same normalization as the string path's union split: any absorbs, never drops.
                return members.Aggregate(CreateUnion);
            }

            case FunctionTypeNode fn:
            {
                TypeInfo? thisType = null;
                if (fn.ThisType is { } thisNode)
                {
                    if (TryToTypeInfo(thisNode) is not { } resolvedThis) return null;
                    thisType = resolvedThis;
                }
                if (!TryResolveParameters(fn.Parameters, out var paramTypes, out int requiredParams, out bool hasRestParam))
                    return null;
                if (TryToTypeInfo(fn.ReturnType) is not { } returnType) return null;
                return new TypeInfo.Function(paramTypes, returnType, requiredParams, hasRestParam, thisType);
            }

            // `new (…) => R` models as an object type carrying a single construct signature —
            // the same shape the string path produces for its "{ new (…) => R }" rendering.
            case ConstructorTypeNode ctor:
            {
                if (!TryResolveParameters(ctor.Parameters, out var paramTypes, out int requiredParams, out bool hasRestParam))
                    return null;
                if (TryToTypeInfo(ctor.ReturnType) is not { } returnType) return null;
                var signature = new TypeInfo.ConstructorSignature(null, paramTypes, returnType, requiredParams, hasRestParam);
                return new TypeInfo.Record(
                    FrozenDictionary<string, TypeInfo>.Empty,
                    ConstructorSignatures: [signature]);
            }

            case ObjectTypeNode obj:
                return TryResolveObjectType(obj);

            case TupleTypeNode tuple:
                return TryResolveTupleType(tuple);

            default:
                return null;
        }
    }

    /// <summary>
    /// Mirror of the string path's <c>ParseInlineObjectTypeInfo</c>: fields with optional/method
    /// markers, index signatures by key kind, call/construct signatures, the
    /// pure-single-call-signature→Function rule, and the same Record shape otherwise.
    /// </summary>
    private TypeInfo? TryResolveObjectType(ObjectTypeNode obj)
    {
        Dictionary<string, TypeInfo> fields = [];
        HashSet<string> optionalFields = [];
        HashSet<string> methodMembers = [];
        TypeInfo? stringIndexType = null;
        TypeInfo? numberIndexType = null;
        TypeInfo? symbolIndexType = null;
        List<FunctionTypeNode> callSignatures = [];
        List<FunctionTypeNode> constructSignatures = [];

        foreach (var member in obj.Members)
        {
            switch (member)
            {
                case PropertyMemberNode prop:
                    if (TryToTypeInfo(prop.Type) is not { } propType) return null;
                    if (prop.IsMethod) methodMembers.Add(prop.Name);
                    if (prop.IsOptional) optionalFields.Add(prop.Name);
                    fields[prop.Name] = propType;
                    break;

                case IndexSignatureNode index:
                    if (TryToTypeInfo(index.ValueType) is not { } valueType) return null;
                    switch (index.KeyKind)
                    {
                        case "string": stringIndexType = valueType; break;
                        case "number": numberIndexType = valueType; break;
                        case "symbol": symbolIndexType = valueType; break;
                    }
                    break;

                case CallSignatureMemberNode call:
                    callSignatures.Add(call.Signature);
                    break;

                case ConstructSignatureMemberNode ctor:
                    constructSignatures.Add(ctor.Signature);
                    break;

                default:
                    return null;
            }
        }

        // A pure single call-signature object type is structurally a plain function type —
        // same rule (and same this-type retention) as the string path.
        if (callSignatures.Count == 1 && constructSignatures.Count == 0 && fields.Count == 0
            && stringIndexType == null && numberIndexType == null && symbolIndexType == null)
        {
            return TryToTypeInfo(callSignatures[0]);
        }

        List<TypeInfo.CallSignature>? recCallSigs = null;
        foreach (var signature in callSignatures)
        {
            // The string path's CallSignature copies drop a `this` type; mirror via the parts.
            if (TryToTypeInfo(signature) is not TypeInfo.Function f) return null;
            (recCallSigs ??= []).Add(new TypeInfo.CallSignature(null, f.ParamTypes, f.ReturnType, f.RequiredParams, f.HasRestParam, f.ParamNames));
        }
        List<TypeInfo.ConstructorSignature>? recCtorSigs = null;
        foreach (var signature in constructSignatures)
        {
            if (TryToTypeInfo(signature) is not TypeInfo.Function f) return null;
            (recCtorSigs ??= []).Add(new TypeInfo.ConstructorSignature(null, f.ParamTypes, f.ReturnType, f.RequiredParams, f.HasRestParam, f.ParamNames));
        }

        return new TypeInfo.Record(
            fields.ToFrozenDictionary(),
            stringIndexType,
            numberIndexType,
            symbolIndexType,
            optionalFields.Count > 0 ? optionalFields.ToFrozenSet() : null,
            CallSignatures: recCallSigs,
            ConstructorSignatures: recCtorSigs,
            MethodMembers: methodMembers.Count > 0 ? methodMembers.ToFrozenSet() : null);
    }

    /// <summary>
    /// Mirror of the string path's <c>ParseTupleTypeInfo</c>: same element kinds, the same
    /// trailing-rest rule (a last <c>...T[]</c> becomes the rest type; any other spread is a
    /// variadic element), and the same TS1257 required-after-optional rejection.
    /// </summary>
    private TypeInfo? TryResolveTupleType(TupleTypeNode tuple)
    {
        List<TypeInfo.TupleElement> elements = [];
        int requiredCount = 0;
        bool seenOptional = false;
        bool seenSpread = false;
        TypeInfo? restType = null;

        for (int i = 0; i < tuple.Elements.Count; i++)
        {
            var element = tuple.Elements[i];

            if (element.IsRest)
            {
                // Trailing ...T[] is the tuple's rest type; the parser only carries a node for a
                // spread when array-ness agrees between the string and structured views.
                if (i == tuple.Elements.Count - 1 && element.Type is ArrayTypeNode arr)
                {
                    if (TryToTypeInfo(arr.ElementType) is not { } rest) return null;
                    restType = rest;
                    break;
                }
                if (TryToTypeInfo(element.Type) is not { } spreadInner) return null;
                elements.Add(new TypeInfo.TupleElement(spreadInner, TupleElementKind.Spread, null));
                seenSpread = true;
                continue;
            }

            if (element.IsOptional)
            {
                seenOptional = true;
            }
            else if (seenOptional && !seenSpread)
            {
                throw new TypeCheckException("Required element cannot follow optional element in tuple.", tsCode: "TS1257");
            }

            if (TryToTypeInfo(element.Type) is not { } elementType) return null;
            elements.Add(new TypeInfo.TupleElement(
                elementType,
                element.IsOptional ? TupleElementKind.Optional : TupleElementKind.Required,
                element.Name));
            if (!element.IsOptional) requiredCount++;
        }

        return new TypeInfo.Tuple(elements, requiredCount, restType);
    }

    /// <summary>
    /// The generic names <see cref="ResolveGenericType"/> handles ahead of its alias lookup —
    /// kept in its branch order. A user alias with one of these names is shadowed by the
    /// built-in on BOTH paths.
    /// </summary>
    private static bool IsBuiltInGenericName(string name) => name is
        "Array" or "ReadonlyArray" or "Promise" or "Generator" or "AsyncGenerator" or
        "Partial" or "Required" or "Readonly" or "Record" or "Pick" or "Omit" or
        "ReturnType" or "Parameters" or "ConstructorParameters" or "InstanceType" or
        "ThisType" or "Awaited" or "NonNullable" or "Extract" or "Exclude" or
        "Uppercase" or "Lowercase" or "Capitalize" or "Uncapitalize";

    /// <summary>
    /// Expands a generic alias from its definition node: the type parameters are bound to the
    /// (already-resolved) arguments in a child scope and the definition resolves node-first —
    /// no argument-string substitution, no definition re-parse. Mirrors the string path's
    /// guards exactly: TS2314 arity, open-type-variable deferral, the TS2589 depth limit, the
    /// recursion placeholder (same instantiation key derivation), and the same post-expansion
    /// passes. Null (component without node support) falls back to the string path.
    /// </summary>
    private TypeInfo? TryExpandGenericAliasFromNode(
        string baseName, TypeNode definitionNode, List<string> typeParamNames, List<TypeInfo> typeArgs)
    {
        if (typeArgs.Count != typeParamNames.Count)
        {
            throw new TypeCheckException(
                $" Type alias '{baseName}' requires {typeParamNames.Count} type argument(s), got {typeArgs.Count}.",
                tsCode: "TS2314");
        }

        // Open type variables (a mapped-type parameter mid-parse) defer instantiation, exactly
        // like the string path (#185).
        if (typeArgs.Any(ContainsOpenTypeVariable))
            return new TypeInfo.RecursiveTypeAlias(baseName, typeArgs);

        var typeArgStrings = typeArgs.Select(TypeInfoToString).ToList();
        string aliasKey = $"{baseName}<{string.Join(",", typeArgStrings)}>";
        _typeAliasExpansionStack ??= new HashSet<string>(StringComparer.Ordinal);

        if (_typeAliasExpansionStack.Count >= MaxTypeAliasExpansionDepth)
        {
            throw new TypeCheckException(
                " Type instantiation is excessively deep and possibly infinite.",
                tsCode: "TS2589");
        }

        if (_typeAliasExpansionStack.Contains(aliasKey))
            return new TypeInfo.RecursiveTypeAlias(baseName, typeArgs);

        _typeAliasExpansionStack.Add(aliasKey);
        try
        {
            var aliasEnv = new TypeEnvironment(_environment);
            for (int i = 0; i < typeParamNames.Count; i++)
                aliasEnv.DefineTypeParameter(typeParamNames[i], typeArgs[i]);

            TypeInfo? result;
            using (new EnvironmentScope(this, aliasEnv))
            {
                result = TryToTypeInfo(definitionNode);
            }
            if (result is null) return null;

            // A nested alias may have expanded via the string path and produced a deferred
            // conditional/mapped form — apply the same post-expansion passes as the string path.
            if (result is TypeInfo.ConditionalType condResult && !ContainsOpenTypeVariable(condResult))
                result = EvaluateConditionalType(condResult);
            if (result is TypeInfo.MappedType mappedResult && !ContainsOpenTypeVariable(mappedResult))
                result = ExpandMappedType(mappedResult);

            result = FlattenTupleSpreads(result);
            ValidateSpreadConstraints(result);
            return result;
        }
        finally
        {
            _typeAliasExpansionStack.Remove(aliasKey);
        }
    }

    /// <summary>
    /// Resolves a function/constructor type node's parameter list with the string path's arity
    /// accounting: optional/rest parameters are not required, and nothing after the first
    /// optional/rest parameter counts as required. False when any parameter type lacks a node-path
    /// resolution (the whole signature then falls back to the string).
    /// </summary>
    private bool TryResolveParameters(
        List<ParameterTypeNode> parameters,
        out List<TypeInfo> paramTypes,
        out int requiredParams,
        out bool hasRestParam)
    {
        paramTypes = new List<TypeInfo>(parameters.Count);
        requiredParams = 0;
        hasRestParam = false;
        bool sawOptionalOrRest = false;

        foreach (var parameter in parameters)
        {
            if (TryToTypeInfo(parameter.Type) is not { } paramType) return false;
            paramTypes.Add(paramType);

            if (parameter.IsRest)
            {
                hasRestParam = true;
                sawOptionalOrRest = true;
            }
            else if (parameter.IsOptional)
            {
                sawOptionalOrRest = true;
            }
            else if (!sawOptionalOrRest)
            {
                requiredParams++;
            }
        }
        return true;
    }

    /// <summary>
    /// Resolves a variable annotation node-first with string fallback, recording coverage stats.
    /// </summary>
    private TypeInfo? ResolveAnnotation(string? annotation, TypeNode? annotationNode)
    {
        if (annotationNode is not null && TryToTypeInfo(annotationNode) is { } fromNode)
        {
            TypeNodeStats.NodeHits++;
            return fromNode;
        }
        if (annotation is null) return null;
        TypeNodeStats.StringFallbacks++;
        return ToTypeInfo(annotation);
    }
}
