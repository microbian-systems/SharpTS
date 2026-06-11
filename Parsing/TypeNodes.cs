namespace SharpTS.Parsing;

/// <summary>
/// Syntactic type nodes — the structured form of a written type annotation.
/// </summary>
/// <remarks>
/// First layer of the two-layer type model (see <c>docs/plans/type-ast-design.md</c>):
/// <c>TypeNode</c> records what was WRITTEN and where; the checker resolves nodes to
/// <see cref="SharpTS.TypeSystem.TypeInfo"/> (what it MEANS) in scope. During the incremental
/// migration the parser builds nodes opportunistically alongside the legacy annotation strings:
/// a construct without node support yields no node and consumers fall back to the string path,
/// which remains authoritative until the migration completes.
/// </remarks>
public abstract record TypeNode(int Line);

/// <summary>A type reference by name: <c>Foo</c>, <c>Box&lt;string&gt;</c>. Type arguments are
/// null for a bare reference. Also covers primitive/keyword names (<c>string</c>, <c>void</c>,
/// …) — resolution treats them uniformly, exactly like the string path.</summary>
public sealed record NamedTypeNode(string Name, List<TypeNode>? TypeArguments, int Line) : TypeNode(Line);

/// <summary>A literal type: <c>"ok"</c>, <c>42</c>, <c>true</c>.</summary>
public sealed record LiteralTypeNode(object? Value, int Line) : TypeNode(Line);

/// <summary>An array type via the suffix syntax: <c>T[]</c>.</summary>
public sealed record ArrayTypeNode(TypeNode ElementType, int Line) : TypeNode(Line);

/// <summary>A union type: <c>A | B | C</c>.</summary>
public sealed record UnionTypeNode(List<TypeNode> Members, int Line) : TypeNode(Line);

/// <summary>
/// Counters proving how much of the corpus the node path already covers — read by tests and
/// dumped by the checker when <c>SHARPTS_TYPENODE_STATS=1</c>. Coarse by design (spike
/// instrumentation, not telemetry).
/// </summary>
public static class TypeNodeStats
{
    /// <summary>Annotations resolved through the node path.</summary>
    public static long NodeHits;

    /// <summary>Annotations that had no node (unsupported construct) and used the string path.</summary>
    public static long StringFallbacks;

    public static void Reset() { NodeHits = 0; StringFallbacks = 0; }
}
