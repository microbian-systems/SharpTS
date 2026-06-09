using System.Collections.Frozen;

namespace SharpTS.TypeSystem;

/// <summary>
/// Unified function-signature assignability: relates <see cref="TypeInfo.Function"/> and
/// <see cref="TypeInfo.GenericFunction"/> through one path, including contextual signature
/// instantiation when a generic source is assigned to a non-generic target.
/// </summary>
/// <remarks>
/// Before this, <c>GenericFunction</c> had no case in <c>IsCompatibleCore</c> at all, so any
/// comparison involving a generic signature fell through to the bare type-parameter rules or off
/// the end to <c>false</c> — incoherently (spuriously rejecting valid generic-source assignments
/// while silently accepting invalid non-generic-source-to-generic-target ones).
///
/// The model mirrors TypeScript's <c>signatureRelatedTo</c>:
/// <list type="bullet">
/// <item>Generic source → non-generic target: instantiate the source's type parameters by inferring
/// them from the target's parameters (un-inferred ones default to <c>{}</c>), then relate the
/// instantiated shape. This is why <c>aN = bN</c> (assign a generic fn to a concrete-typed slot)
/// type-checks while a return-only type parameter that can't be inferred collapses to <c>{}</c> and
/// fails the return check.</item>
/// <item>Everything else (non-generic source, or a generic <em>target</em>): relate the shapes
/// directly, leaving type parameters opaque. The existing type-parameter rules then correctly reject
/// a concrete source against a bare type parameter — so <c>bN = aN</c> (assign a concrete fn to a
/// generic-typed slot) is an error, since the target must hold for every instantiation.</item>
/// </list>
/// </remarks>
public partial class TypeChecker
{
    /// <summary>A callable signature reduced to its type parameters and a plain function shape.</summary>
    private readonly record struct NormalizedSignature(
        List<TypeInfo.TypeParameter>? TypeParams,
        TypeInfo.Function Func)
    {
        public bool IsGeneric => TypeParams is { Count: > 0 };
    }

    /// <summary>
    /// Reduces a function-like type to a <see cref="NormalizedSignature"/>, or null if it isn't a
    /// plain or generic function. Callable interfaces/object types are handled separately (via their
    /// call signatures) and are intentionally not folded in here.
    /// </summary>
    private static NormalizedSignature? NormalizeSignature(TypeInfo type) => type switch
    {
        TypeInfo.Function f => new NormalizedSignature(null, f),
        TypeInfo.GenericFunction gf => new NormalizedSignature(
            gf.TypeParams,
            new TypeInfo.Function(gf.ParamTypes, gf.ReturnType, gf.RequiredParams, gf.HasRestParam, gf.ThisType, gf.ParamNames)),
        _ => null,
    };

    /// <summary>
    /// True when <paramref name="source"/> is assignable to <paramref name="target"/> as a call
    /// signature. Applies contextual signature instantiation for a generic source against a
    /// non-generic target; otherwise relates the shapes with type parameters left opaque.
    /// </summary>
    private bool SignatureRelatedTo(NormalizedSignature source, NormalizedSignature target)
    {
        if (source.IsGeneric && !target.IsGeneric)
        {
            var instantiatedSource = InstantiateGenericSourceFromTarget(source, target.Func);
            return RelateFunctionShapes(target.Func, instantiatedSource);
        }

        // GATE (Increment 1): relating two generic signatures requires instantiating each against
        // the other (bidirectional contextual signature instantiation). Until that's implemented,
        // comparing their distinct opaque type parameters directly emits false positives, so defer
        // to the lenient result — the outcome these cases had before generic function-type
        // annotations parsed to GenericFunction. To be replaced by faithful both-generic relating.
        if (source.IsGeneric && target.IsGeneric)
            return true;

        return RelateFunctionShapes(target.Func, source.Func);
    }

    /// <summary>
    /// Instantiates a generic source signature against a concrete target by inferring each source
    /// type parameter from the corresponding target type, then substituting. This is a dedicated
    /// inference path — deliberately separate from the call-argument inference (<c>InferFromType</c>)
    /// so changing it can't perturb generic call type-checking.
    /// </summary>
    /// <remarks>
    /// Inference is variance-aware. A type parameter is collected from every structural position it
    /// occupies (recursing arrays, functions, objects, tuples, generic instantiations), and the
    /// candidates are combined by the variance of those positions:
    /// <list type="bullet">
    /// <item>seen in any <em>contravariant</em> (parameter) position → intersection: the chosen type
    /// must serve as every input it's used as, so <c>&lt;T&gt;(x: T, y: T)</c> matched against
    /// <c>(x: {a}, y: {a;b})</c> yields <c>{a;b}</c>, not a first-wins <c>{a}</c> that then fails the
    /// second parameter;</item>
    /// <item>otherwise (purely covariant) → union.</item>
    /// </list>
    /// Un-inferred parameters default to their constraint, or <c>{}</c> when unconstrained — matching
    /// TypeScript, where an uninferable (typically return-only) type parameter collapses to <c>{}</c>.
    /// </remarks>
    private TypeInfo.Function InstantiateGenericSourceFromTarget(NormalizedSignature source, TypeInfo.Function target)
    {
        var paramNames = source.TypeParams!.Select(tp => tp.Name).ToHashSet();
        var candidates = new Dictionary<string, List<TypeInfo>>();
        var sawContravariant = new HashSet<string>();

        int positions = Math.Min(source.Func.ParamTypes.Count, target.ParamTypes.Count);
        for (int i = 0; i < positions; i++)
            CollectInferenceCandidates(source.Func.ParamTypes[i], target.ParamTypes[i], contravariant: true, paramNames, candidates, sawContravariant);
        CollectInferenceCandidates(source.Func.ReturnType, target.ReturnType, contravariant: false, paramNames, candidates, sawContravariant);

        var inferred = new Dictionary<string, TypeInfo>();
        foreach (var tp in source.TypeParams!)
        {
            if (candidates.TryGetValue(tp.Name, out var cands) && cands.Count > 0)
            {
                inferred[tp.Name] = sawContravariant.Contains(tp.Name)
                    ? SimplifyIntersection(cands.Distinct().ToList())
                    : cands.Aggregate(CreateUnion);
            }
            else
            {
                inferred[tp.Name] = tp.Constraint ?? EmptyObjectType;
            }
        }

        var paramTypes = source.Func.ParamTypes.Select(p => Substitute(p, inferred)).ToList();
        var returnType = Substitute(source.Func.ReturnType, inferred);
        return new TypeInfo.Function(
            paramTypes, returnType, source.Func.RequiredParams, source.Func.HasRestParam,
            source.Func.ThisType, source.Func.ParamNames);
    }

    /// <summary>
    /// Recursively records, for each named type parameter of the source, the target types appearing
    /// at the same structural position — tracking whether the position is contravariant so candidates
    /// can later be combined correctly. Mirrors function-parameter contravariance (recursing into a
    /// nested function flips the variance of its parameters).
    /// </summary>
    private void CollectInferenceCandidates(
        TypeInfo sourceType, TypeInfo targetType, bool contravariant,
        HashSet<string> paramNames, Dictionary<string, List<TypeInfo>> candidates, HashSet<string> sawContravariant)
    {
        switch (sourceType)
        {
            case TypeInfo.TypeParameter tp when paramNames.Contains(tp.Name):
                if (!candidates.TryGetValue(tp.Name, out var list))
                    candidates[tp.Name] = list = [];
                list.Add(targetType);
                if (contravariant) sawContravariant.Add(tp.Name);
                return;

            case TypeInfo.Array sa when targetType is TypeInfo.Array ta:
                CollectInferenceCandidates(sa.ElementType, ta.ElementType, contravariant, paramNames, candidates, sawContravariant);
                return;

            case TypeInfo.Function sf when targetType is TypeInfo.Function tf:
                int n = Math.Min(sf.ParamTypes.Count, tf.ParamTypes.Count);
                for (int i = 0; i < n; i++)
                    // Parameter positions of a nested function flip the variance.
                    CollectInferenceCandidates(sf.ParamTypes[i], tf.ParamTypes[i], !contravariant, paramNames, candidates, sawContravariant);
                CollectInferenceCandidates(sf.ReturnType, tf.ReturnType, contravariant, paramNames, candidates, sawContravariant);
                return;

            case TypeInfo.Record sr when targetType is TypeInfo.Record tr:
                foreach (var (fieldName, fieldType) in sr.Fields)
                    if (tr.Fields.TryGetValue(fieldName, out var targetFieldType))
                        CollectInferenceCandidates(fieldType, targetFieldType, contravariant, paramNames, candidates, sawContravariant);
                return;

            case TypeInfo.Tuple stup when targetType is TypeInfo.Tuple ttup:
                int m = Math.Min(stup.Elements.Count, ttup.Elements.Count);
                for (int i = 0; i < m; i++)
                    CollectInferenceCandidates(stup.Elements[i].Type, ttup.Elements[i].Type, contravariant, paramNames, candidates, sawContravariant);
                return;

            case TypeInfo.InstantiatedGeneric sg when targetType is TypeInfo.InstantiatedGeneric tg:
                int k = Math.Min(sg.TypeArguments.Count, tg.TypeArguments.Count);
                for (int i = 0; i < k; i++)
                    CollectInferenceCandidates(sg.TypeArguments[i], tg.TypeArguments[i], contravariant, paramNames, candidates, sawContravariant);
                return;
        }
    }

    /// <summary>The empty object type <c>{}</c> — the default for an uninferable type parameter.</summary>
    private static readonly TypeInfo.Record EmptyObjectType = new(FrozenDictionary<string, TypeInfo>.Empty);

    /// <summary>
    /// Core function-shape assignability: <paramref name="f2"/> (source) assignable to
    /// <paramref name="f1"/> (target). Parameter arity/positions with rest-parameter expansion, then
    /// return-type covariance with the void-return special case. Extracted verbatim from the former
    /// inline <c>Function</c>-vs-<c>Function</c> block so non-generic behavior is unchanged.
    /// </summary>
    private bool RelateFunctionShapes(TypeInfo.Function f1, TypeInfo.Function f2)
    {
        // Source (f2) must not require more parameters than the target (f1) can supply.
        // A rest parameter on the target lets it supply unboundedly many, so the count check
        // only applies when the target has no rest parameter.
        if (!f1.HasRestParam && f2.MinArity > f1.ParamTypes.Count) return false;

        // Compare parameter positions, expanding a rest parameter to its element type so it
        // covers the other side's fixed parameters (e.g. `(...a: number[])` matches `(a, b)`).
        int f1Fixed = f1.HasRestParam ? f1.ParamTypes.Count - 1 : f1.ParamTypes.Count;
        int f2Fixed = f2.HasRestParam ? f2.ParamTypes.Count - 1 : f2.ParamTypes.Count;
        int positions = Math.Max(f1Fixed, f2Fixed);
        for (int i = 0; i < positions; i++)
        {
            var fp1 = EffectiveParamType(f1, i);
            var fp2 = EffectiveParamType(f2, i);
            // A position absent on one side (and not covered by a rest param) is unconstrained.
            if (fp1 is null || fp2 is null) continue;
            if (!IsCompatible(fp1, fp2)) return false;
        }
        // When both have rest parameters, their element types must also be compatible.
        if (f1.HasRestParam && f2.HasRestParam)
        {
            var e1 = EffectiveParamType(f1, f1.ParamTypes.Count);
            var e2 = EffectiveParamType(f2, f2.ParamTypes.Count);
            if (e1 is not null && e2 is not null && !IsCompatible(e1, e2)) return false;
        }
        // Return type: if expected return type is void, any return type is acceptable
        // This is standard TypeScript behavior - void context ignores the return value
        if (f1.ReturnType is TypeInfo.Void) return true;
        // Otherwise, actual must be compatible with expected
        return IsCompatible(f1.ReturnType, f2.ReturnType);
    }
}
