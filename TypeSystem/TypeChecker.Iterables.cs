using SharpTS.Parsing;

using System.Collections.Frozen;

namespace SharpTS.TypeSystem;

/// <summary>
/// Structural typing of the sync iterable/iterator protocols (#485). SharpTS models
/// iterables/iterators nominally (dedicated <see cref="TypeInfo"/> records: Array, Set, Map, Iterator,
/// Generator, …); these helpers bridge to TypeScript's structural view so a hand-written object exposing
/// <c>[Symbol.iterator]()</c> / <c>next()</c> is recognized as an iterable/iterator and its element type
/// is derived from <c>next().value</c> rather than collapsing to <c>any</c>.
/// </summary>
public partial class TypeChecker
{
    /// <summary>
    /// Builds the structural shape of <c>IteratorResult&lt;T&gt;</c>: <c>{ value: T; done?: boolean }</c>.
    /// IteratorResult is a structural type in TS (a yield/return union); SharpTS keeps a single element
    /// type (as Iterator/Generator drop their TReturn/TNext) and makes <c>done</c> optional because
    /// IteratorYieldResult declares <c>done?: false</c>, so a bare <c>{ value }</c> is a valid result.
    /// </summary>
    private static TypeInfo.Record BuildIteratorResultType(TypeInfo element)
    {
        var fields = new Dictionary<string, TypeInfo>
        {
            ["value"] = element,
            ["done"] = new TypeInfo.Primitive(TokenType.TYPE_BOOLEAN),
        }.ToFrozenDictionary();
        return new TypeInfo.Record(fields, OptionalFields: new[] { "done" }.ToFrozenSet());
    }

    /// <summary>The declared return type of a callable member (an object's <c>next</c>/<c>[Symbol.iterator]</c>).</summary>
    private static TypeInfo? GetCallableReturnType(TypeInfo? callable) => callable switch
    {
        TypeInfo.Function f => f.ReturnType,
        TypeInfo.OverloadedFunction o => o.Implementation.ReturnType,
        _ => null
    };

    private static bool IsCallableMember(TypeInfo? member) =>
        member is TypeInfo.Function or TypeInfo.OverloadedFunction;

    /// <summary>
    /// Derives the element type of a structural ITERATOR source — an object that itself exposes a callable
    /// <c>next()</c>. The element is the <c>value</c> of <c>next()</c>'s IteratorResult. When <c>next</c> is
    /// present but its result is untyped (<c>any</c>) or its element can't be read precisely (e.g. a union
    /// result), the element is <c>any</c>, so the protocol still matches without inventing a spurious
    /// mismatch. Returns false only when there is no callable <c>next</c> at all.
    /// </summary>
    private bool TryGetStructuralIteratorElement(TypeInfo source, out TypeInfo elementType)
    {
        elementType = null!;
        var nextMember = GetMemberType(source, "next");
        if (!IsCallableMember(nextMember)) return false;

        elementType = ExtractIteratorResultValue(GetCallableReturnType(nextMember));
        return true;
    }

    /// <summary>
    /// Reads the element type out of a <c>next()</c> return type — the <c>value</c> of its IteratorResult.
    /// Only a single concrete object shape is read precisely; <c>any</c>, a shape without a derivable
    /// <c>value</c>, and anything else (e.g. a union of yield/return results we cannot split) yield
    /// <c>any</c>, so a structural iterator never produces a false element mismatch.
    /// </summary>
    private TypeInfo ExtractIteratorResultValue(TypeInfo? nextReturn) => nextReturn switch
    {
        null or TypeInfo.Any => new TypeInfo.Any(),
        TypeInfo.Record or TypeInfo.Interface or TypeInfo.Instance =>
            GetMemberType(nextReturn, "value") ?? new TypeInfo.Any(),
        _ => new TypeInfo.Any()
    };

    /// <summary>
    /// Derives the element type of a structural ITERABLE source — an object exposing
    /// <c>[Symbol.iterator](): Iterator&lt;T&gt;</c>. In declared/interface types the method is the named
    /// member <c>@@iterator</c>; in object literals a symbol-keyed method lands in the symbol index
    /// signature, so both are probed. The returned iterator's element is read via the dedicated records
    /// or, failing that, structurally via its <c>next()</c>. Returns false when no iterator factory exists.
    /// </summary>
    private bool TryGetStructuralIterableElement(TypeInfo source, out TypeInfo elementType)
    {
        elementType = null!;

        TypeInfo? iteratorFactory = GetMemberType(source, "@@iterator");
        if (!IsCallableMember(iteratorFactory) && source is TypeInfo.Record { SymbolIndexType: { } symIndex })
            iteratorFactory = symIndex;   // object-literal [Symbol.iterator]() lands in the symbol index
        if (!IsCallableMember(iteratorFactory)) return false;

        TypeInfo? iterator = GetCallableReturnType(iteratorFactory);
        if (iterator is null) { elementType = new TypeInfo.Any(); return true; }

        return TryGetIteratorElementFromReturn(iterator, out elementType);
    }

    /// <summary>
    /// Element of the iterator value returned by <c>[Symbol.iterator]()</c>: a dedicated iterator/generator
    /// record carries it directly, otherwise it is read structurally from the iterator's <c>next()</c>. An
    /// object that yields a <c>[Symbol.iterator]</c> is iterable even if its element is unknown, so this
    /// always succeeds (defaulting to <c>any</c>).
    /// </summary>
    private bool TryGetIteratorElementFromReturn(TypeInfo iterator, out TypeInfo elementType)
    {
        switch (iterator)
        {
            case TypeInfo.Iterator it: elementType = it.ElementType; return true;
            case TypeInfo.Generator g: elementType = g.YieldType; return true;
            case TypeInfo.Iterable ib: elementType = ib.ElementType; return true;
            default:
                if (TryGetStructuralIteratorElement(iterator, out elementType)) return true;
                elementType = new TypeInfo.Any();
                return true;
        }
    }

    /// <summary>
    /// The element type of any sync-iterable source, unifying the dedicated records, strings and
    /// structural iterables. Drives <c>for...of</c>, spread and <c>yield*</c> element binding and the
    /// <c>Iterable&lt;T&gt;</c> assignment target. Returns false for non-iterable types (callers decide
    /// whether that is an error or a fall-through to <c>any</c>). Tuples — handled specially elsewhere —
    /// and unions are intentionally excluded to preserve existing behavior.
    /// </summary>
    private bool TryGetIterableElementType(TypeInfo type, out TypeInfo elementType)
    {
        switch (type)
        {
            case TypeInfo.Array arr: elementType = arr.ElementType; return true;
            case TypeInfo.Set set: elementType = set.ElementType; return true;
            case TypeInfo.Map map: elementType = TypeInfo.Tuple.FromTypes([map.KeyType, map.ValueType], 2); return true;
            case TypeInfo.Iterator it: elementType = it.ElementType; return true;
            case TypeInfo.Generator gen: elementType = gen.YieldType; return true;
            case TypeInfo.Iterable iterable: elementType = iterable.ElementType; return true;
            case TypeInfo.String or TypeInfo.StringLiteral: elementType = new TypeInfo.String(); return true;
            case TypeInfo.Any: elementType = new TypeInfo.Any(); return true;
            case TypeInfo.Record or TypeInfo.Interface or TypeInfo.Instance:
                return TryGetStructuralIterableElement(type, out elementType);
            default:
                elementType = null!;
                return false;
        }
    }
}
