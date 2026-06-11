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

/// <summary>A parameter inside a function or constructor type: <c>x: T</c>, <c>x?: T</c>,
/// <c>...rest: T[]</c>. A bare name (<c>(x) =&gt; R</c>) gets an explicit <c>any</c> type node —
/// the name is a label only, never resolved. Not itself a type.</summary>
public sealed record ParameterTypeNode(string? Name, TypeNode Type, bool IsOptional, bool IsRest, int Line);

/// <summary>A function type: <c>(a: T) =&gt; R</c>, optionally with a leading
/// <c>this: X</c> pseudo-parameter (carried separately — it does not count toward arity).</summary>
public sealed record FunctionTypeNode(TypeNode? ThisType, List<ParameterTypeNode> Parameters, TypeNode ReturnType, int Line) : TypeNode(Line);

/// <summary>A constructor type: <c>new (a: T) =&gt; R</c>. Resolves to an object type carrying a
/// single construct signature, mirroring the string path's <c>{ new (…) =&gt; R }</c> rendering.
/// Generic constructor types (<c>new &lt;T&gt;(…) =&gt; R</c>) have no node yet (slice 3).</summary>
public sealed record ConstructorTypeNode(List<ParameterTypeNode> Parameters, TypeNode ReturnType, int Line) : TypeNode(Line);

/// <summary>An inline object type: <c>{ name: string; greet(x: number): void }</c>.
/// Mapped types (<c>{ [K in keyof T]: … }</c>) have no node (slice 2 follow-up).</summary>
public sealed record ObjectTypeNode(List<ObjectTypeMemberNode> Members, int Line) : TypeNode(Line);

/// <summary>A member of an <see cref="ObjectTypeNode"/>. Not itself a type.</summary>
public abstract record ObjectTypeMemberNode(int Line);

/// <summary>A property or method member: <c>name: T</c>, <c>name?: T</c>, <c>name(x: T): R</c>.
/// Computed names are carried in their string-path spelling (<c>@@iterator</c>). Method-syntax
/// members keep <see cref="IsMethod"/> so bivariant parameter relating survives
/// (the string path's <c>#m</c> marker).</summary>
public sealed record PropertyMemberNode(string Name, TypeNode Type, bool IsOptional, bool IsMethod, int Line) : ObjectTypeMemberNode(Line);

/// <summary>An index signature: <c>[k: string]: T</c>. <see cref="KeyKind"/> is
/// <c>string</c>, <c>number</c>, or <c>symbol</c>.</summary>
public sealed record IndexSignatureNode(string KeyKind, TypeNode ValueType, int Line) : ObjectTypeMemberNode(Line);

/// <summary>A call signature member: <c>(x: T): R</c>.</summary>
public sealed record CallSignatureMemberNode(FunctionTypeNode Signature, int Line) : ObjectTypeMemberNode(Line);

/// <summary>A construct signature member: <c>new (x: T): R</c>.</summary>
public sealed record ConstructSignatureMemberNode(FunctionTypeNode Signature, int Line) : ObjectTypeMemberNode(Line);

/// <summary>A tuple type: <c>[string, n?: number, ...rest: boolean[]]</c>.</summary>
public sealed record TupleTypeNode(List<TupleElementNode> Elements, int Line) : TypeNode(Line);

/// <summary>One tuple element: optionally named, optional (<c>?</c>), or a spread/rest
/// (<c>...T</c> / <c>...T[]</c>, distinguished at resolution by position and array-ness).
/// Not itself a type.</summary>
public sealed record TupleElementNode(string? Name, TypeNode Type, bool IsOptional, bool IsRest, int Line);

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
