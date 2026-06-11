using System.Collections.Frozen;
using SharpTS.Parsing;

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

            // Generic references are slice 2 (alias expansion needs argument strings today).
            case NamedTypeNode:
                return null;

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

            default:
                return null;
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
