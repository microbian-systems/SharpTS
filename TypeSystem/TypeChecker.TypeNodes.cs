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

            default:
                return null;
        }
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
