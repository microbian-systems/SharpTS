using SharpTS.TypeSystem.Exceptions;
using SharpTS.Runtime.BuiltIns;
using System.Collections.Frozen;
using SharpTS.Parsing;

namespace SharpTS.TypeSystem;

/// <summary>
/// Type compatibility checking - structural and nominal typing.
/// </summary>
/// <remarks>
/// Core compatibility logic with memoization and IsCompatibleCore decision tree.
/// Related partial files:
/// - TypeChecker.Compatibility.Helpers.cs: Type predicates and class accessors
/// - TypeChecker.Compatibility.TypeGuards.cs: Control-flow type narrowing
/// - TypeChecker.Compatibility.Structural.cs: Duck typing and member access
/// - TypeChecker.Compatibility.Tuples.cs: Tuple and array compatibility
/// - TypeChecker.Compatibility.Callable.cs: Callable/constructable interfaces
/// - TypeChecker.Compatibility.TemplateLiterals.cs: Template literal patterns
/// </remarks>
public partial class TypeChecker
{
    /// <summary>
    /// Comparer for compatibility cache keys using TypeInfoEqualityComparer.
    /// Ensures structurally equivalent types (including those with List fields)
    /// are treated as equal keys.
    /// </summary>
    private sealed class CompatibilityCacheKeyComparer
        : IEqualityComparer<(TypeInfo Expected, TypeInfo Actual)>
    {
        public static readonly CompatibilityCacheKeyComparer Instance = new();

        public bool Equals((TypeInfo Expected, TypeInfo Actual) x,
                           (TypeInfo Expected, TypeInfo Actual) y)
        {
            return TypeInfoEqualityComparer.Instance.Equals(x.Expected, y.Expected)
                && TypeInfoEqualityComparer.Instance.Equals(x.Actual, y.Actual);
        }

        public int GetHashCode((TypeInfo Expected, TypeInfo Actual) obj)
        {
            return HashCode.Combine(
                TypeInfoEqualityComparer.Instance.GetHashCode(obj.Expected),
                TypeInfoEqualityComparer.Instance.GetHashCode(obj.Actual)
            );
        }
    }

    /// <summary>
    /// Fast identity-based cache for type compatibility.
    /// Uses reference equality for O(1) lookup when the same TypeInfo instances are used.
    /// Falls back to structural equality cache when reference equality misses.
    /// </summary>
    private sealed class IdentityCompatibilityCacheKey(TypeInfo expected, TypeInfo actual)
    {
        public readonly TypeInfo Expected = expected;
        public readonly TypeInfo Actual = actual;
    }

    private sealed class IdentityCacheKeyComparer : IEqualityComparer<IdentityCompatibilityCacheKey>
    {
        public static readonly IdentityCacheKeyComparer Instance = new();

        public bool Equals(IdentityCompatibilityCacheKey? x, IdentityCompatibilityCacheKey? y)
        {
            if (x == null || y == null) return x == y;
            // Use reference equality for fast comparison
            return ReferenceEquals(x.Expected, y.Expected) && ReferenceEquals(x.Actual, y.Actual);
        }

        public int GetHashCode(IdentityCompatibilityCacheKey obj)
        {
            // Use RuntimeHelpers.GetHashCode for identity-based hash
            return HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Expected),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Actual)
            );
        }
    }

    // Fast identity-based cache (first level)
    private Dictionary<IdentityCompatibilityCacheKey, bool>? _identityCompatibilityCache;

    // Track compatibility checks in progress for co-induction cycle detection
    // Uses identity-based comparison since we need to detect the exact same type pair
    private HashSet<IdentityCompatibilityCacheKey>? _compatibilityInProgress;

    // Depth counter to prevent stack overflow with deeply nested recursive type checks
    private int _compatibilityCheckDepth;
    private const int MaxCompatibilityCheckDepth = 50;

    /// <summary>
    /// Checks type compatibility with two-level memoization and co-inductive cycle detection.
    /// Level 1: Fast identity-based cache using reference equality (O(1) for same instances)
    /// Level 2: Structural equality cache for different instances with same structure
    /// Co-induction: If checking the same type pair (by identity) that's already in progress,
    /// assume compatible to break infinite recursion with recursive types.
    /// Depth limit: At max depth, assume compatible as a fallback for deeply nested checks.
    /// </summary>
    private bool IsCompatible(TypeInfo expected, TypeInfo actual)
    {
        // Level 1: Fast identity-based cache (reference equality)
        _identityCompatibilityCache ??= new(IdentityCacheKeyComparer.Instance);
        var identityKey = new IdentityCompatibilityCacheKey(expected, actual);

        if (_identityCompatibilityCache.TryGetValue(identityKey, out var identityCached))
            return identityCached;

        // Level 2: Structural equality cache (for different instances with same structure)
        _compatibilityCache ??= new(CompatibilityCacheKeyComparer.Instance);
        var structuralKey = (expected, actual);

        if (_compatibilityCache.TryGetValue(structuralKey, out var structuralCached))
        {
            // Store in identity cache for future fast lookups
            _identityCompatibilityCache[identityKey] = structuralCached;
            return structuralCached;
        }

        // Co-induction: if this exact type pair (by identity) is already being checked,
        // assume compatible to break cycle. This is safe because if the types were
        // incompatible, we would have found that incompatibility before recursing back.
        _compatibilityInProgress ??= new(IdentityCacheKeyComparer.Instance);
        if (!_compatibilityInProgress.Add(identityKey))
        {
            // Already checking this pair - assume compatible (co-induction)
            return true;
        }

        // Depth limit: at max depth, assume compatible as fallback
        // This handles cases where co-induction doesn't catch the cycle (different actual values)
        if (_compatibilityCheckDepth >= MaxCompatibilityCheckDepth)
        {
            _compatibilityInProgress.Remove(identityKey);
            return true;
        }

        _compatibilityCheckDepth++;
        try
        {
            // Cache miss - compute result
            var result = IsCompatibleCore(expected, actual);

            // Store in both caches
            _compatibilityCache[structuralKey] = result;
            _identityCompatibilityCache[identityKey] = result;

            return result;
        }
        finally
        {
            _compatibilityCheckDepth--;
            _compatibilityInProgress.Remove(identityKey);
        }
    }

    /// <summary>
    /// Expands a recursive type alias placeholder to its full type.
    /// Used for lazy expansion during compatibility checks.
    /// Uses _expandedTypeAliasCache to ensure the same TypeInfo object is reused,
    /// enabling identity-based caching to break infinite recursion.
    /// </summary>
    /// <param name="rta">The recursive type alias to expand.</param>
    /// <returns>The expanded type.</returns>
    private TypeInfo ExpandRecursiveTypeAlias(TypeInfo.RecursiveTypeAlias rta)
    {
        // For generic aliases, create a cache key that includes type arguments
        string cacheKey = rta.TypeArguments is { Count: > 0 }
            ? $"{rta.AliasName}<{string.Join(",", rta.TypeArguments.Select(t => t.ToString()))}>"
            : rta.AliasName;

        // Check cache first - reusing the same TypeInfo object is crucial for breaking recursion
        _expandedTypeAliasCache ??= new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
        if (_expandedTypeAliasCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        if (++_typeAliasExpansionDepth > MaxTypeAliasExpansionDepth)
        {
            _typeAliasExpansionDepth--;
            throw new TypeCheckException(
                $"Type alias '{rta.AliasName}' circularly references itself.", tsCode: "TS2456");
        }

        // Set up the expansion stack to prevent infinite recursion when ToTypeInfo
        // encounters nested references to the same alias
        _typeAliasExpansionStack ??= new HashSet<string>(StringComparer.Ordinal);
        bool addedToStack = _typeAliasExpansionStack.Add(rta.AliasName);

        try
        {
            TypeInfo expanded;
            if (rta.TypeArguments is { Count: > 0 })
            {
                // Generic recursive alias - resolve directly with TypeInfo arguments
                // This avoids the TypeInfo -> string -> TypeInfo round-trip
                expanded = ResolveGenericType(rta.AliasName, rta.TypeArguments);
            }
            else
            {
                // Non-generic recursive alias
                var alias = _environment.GetTypeAlias(rta.AliasName);
                if (alias != null)
                {
                    expanded = ToTypeInfo(alias);
                }
                else
                {
                    throw new TypeCheckException($"Unknown type '{rta.AliasName}'.", tsCode: "TS2304");
                }
            }

            // Cache the expanded type
            _expandedTypeAliasCache[cacheKey] = expanded;
            return expanded;
        }
        finally
        {
            if (addedToStack)
            {
                _typeAliasExpansionStack.Remove(rta.AliasName);
            }
            _typeAliasExpansionDepth--;
        }
    }

    /// <summary>
    /// Converts a TypeInfo back to a string representation for re-parsing.
    /// Used when expanding recursive type aliases with type arguments.
    /// </summary>
    private static string TypeInfoToString(TypeInfo type) => type switch
    {
        TypeInfo.String => "string",
        TypeInfo.Primitive { Type: TokenType.TYPE_NUMBER } => "number",
        TypeInfo.Primitive { Type: TokenType.TYPE_BOOLEAN } => "boolean",
        TypeInfo.Void => "void",
        TypeInfo.Null => "null",
        TypeInfo.Undefined => "undefined",
        TypeInfo.Unknown => "unknown",
        TypeInfo.Never => "never",
        TypeInfo.Any => "any",
        TypeInfo.Symbol => "symbol",
        TypeInfo.BigInt => "bigint",
        TypeInfo.Object => "object",
        TypeInfo.StringLiteral sl => $"\"{sl.Value}\"",
        TypeInfo.NumberLiteral nl => nl.Value.ToString(),
        TypeInfo.BooleanLiteral bl => bl.Value ? "true" : "false",
        TypeInfo.Array arr => $"{TypeInfoToString(arr.ElementType)}[]",
        TypeInfo.Union u => string.Join(" | ", u.FlattenedTypes.Select(TypeInfoToString)),
        TypeInfo.Intersection i => string.Join(" & ", i.FlattenedTypes.Select(TypeInfoToString)),
        TypeInfo.Tuple t => $"[{string.Join(", ", t.ElementTypes.Select(TypeInfoToString))}]",
        TypeInfo.Record r => $"{{ {string.Join("; ", r.Fields.Select(f => $"{f.Key}: {TypeInfoToString(f.Value)}"))} }}",
        TypeInfo.Class c => c.Name,
        TypeInfo.Instance inst when inst.ClassType is TypeInfo.Class c => c.Name,
        TypeInfo.Interface itf => itf.Name,
        TypeInfo.TypeParameter tp => tp.Name,
        TypeInfo.RecursiveTypeAlias rta => rta.TypeArguments is { Count: > 0 }
            ? $"{rta.AliasName}<{string.Join(", ", rta.TypeArguments.Select(TypeInfoToString))}>"
            : rta.AliasName,
        _ => type.ToString() ?? "any"
    };

    /// <summary>
    /// Core type compatibility logic without caching.
    /// </summary>
    private bool IsCompatibleCore(TypeInfo expected, TypeInfo actual)
    {
        if (expected is TypeInfo.Any or TypeInfo.Inferred || actual is TypeInfo.Any or TypeInfo.Inferred) return true;

        // strictNullChecks: off — null/undefined are assignable to every type except `never`.
        // Checked early so it short-circuits before any expected-type-specific rejection.
        if (!_strictNullChecks && actual is TypeInfo.Null or TypeInfo.Undefined)
            return expected is not TypeInfo.Never;

        // Expand recursive type aliases lazily
        if (expected is TypeInfo.RecursiveTypeAlias expectedRTA)
        {
            return IsCompatible(ExpandRecursiveTypeAlias(expectedRTA), actual);
        }
        if (actual is TypeInfo.RecursiveTypeAlias actualRTA)
        {
            return IsCompatible(expected, ExpandRecursiveTypeAlias(actualRTA));
        }

        // Type predicate compatibility:
        // - Regular type predicate (x is T): expects boolean return
        // - Assertion type predicate (asserts x is T): expects void return (function throws if assertion fails)
        // - AssertsNonNull (asserts x): expects void return
        if (expected is TypeInfo.TypePredicate pred)
        {
            if (pred.IsAssertion)
            {
                // Assertion predicates return void (or throw)
                return actual is TypeInfo.Void or TypeInfo.Never;
            }
            else
            {
                // Regular type predicates return boolean
                return actual is TypeInfo.Primitive { Type: Parsing.TokenType.TYPE_BOOLEAN }
                    or TypeInfo.BooleanLiteral;
            }
        }
        if (expected is TypeInfo.AssertsNonNull)
        {
            // AssertsNonNull returns void (or throws)
            return actual is TypeInfo.Void or TypeInfo.Never;
        }

        // Type-parameter compatibility (TypeScript: "type parameters are not assignable to one
        // another unless directly or indirectly constrained to one another").
        if (expected is TypeInfo.TypeParameter expectedTp && actual is TypeInfo.TypeParameter actualTp)
        {
            // The same parameter, or a source transitively constrained to the target (U extends … extends T).
            return expectedTp.Name == actualTp.Name || TypeParameterConstrainedTo(actualTp, expectedTp.Name);
        }

        // Expected is a bare type parameter and the source is some other type. An arbitrary concrete
        // type is NOT assignable to a type parameter — only `never`. (any / inferred and, under
        // non-strict, null / undefined are already accepted earlier in IsCompatibleCore; a source type
        // parameter is handled by the case above.) This is the strict TypeScript rule.
        if (expected is TypeInfo.TypeParameter)
        {
            return actual is TypeInfo.Never;
        }

        // Source is a type parameter assigned to a non-parameter target: it is assignable wherever its
        // apparent (constraint) type is assignable. Also assignable into a union that contains it.
        if (actual is TypeInfo.TypeParameter actualTpOnly)
        {
            if (expected is TypeInfo.Any or TypeInfo.Unknown) return true;
            if (expected is TypeInfo.Union expUnionForTp &&
                expUnionForTp.FlattenedTypes.Any(t =>
                    t is TypeInfo.TypeParameter unionTp && unionTp.Name == actualTpOnly.Name))
            {
                return true;
            }
            var apparent = ApparentTypeOf(actualTpOnly);
            return apparent != null && IsCompatible(expected, apparent);
        }

        // never as actual: assignable to anything (bottom type)
        if (actual is TypeInfo.Never) return true;

        // never as expected: nothing assignable to never except never
        if (expected is TypeInfo.Never) return actual is TypeInfo.Never;

        // unknown as expected: anything can be assigned TO unknown (top type)
        if (expected is TypeInfo.Unknown) return true;

        // unknown as actual: can only be assigned to unknown or any
        if (actual is TypeInfo.Unknown)
            return expected is TypeInfo.Unknown || expected is TypeInfo.Any;

        // object type: accepts non-primitive, non-null values
        if (expected is TypeInfo.Object)
        {
            if (actual is TypeInfo.Never) return true;  // never is bottom type
            if (actual is TypeInfo.Any) return true;    // any is assignable to anything
            if (actual is TypeInfo.Object) return true; // object to object
            if (IsPrimitiveType(actual)) return false;  // reject primitives
            if (actual is TypeInfo.Null or TypeInfo.Undefined) return false;
            // Accept: Record, Array, Instance, Class, Function, Map, Set, etc.
            return true;
        }

        // object as actual: can only assign to object, any, unknown
        if (actual is TypeInfo.Object)
        {
            return expected is TypeInfo.Object or TypeInfo.Any or TypeInfo.Unknown;
        }

        // Null compatibility (strictNullChecks: on — the off case is handled early in IsCompatibleCore)
        if (actual is TypeInfo.Null)
        {
            if (expected is TypeInfo.Union u && u.ContainsNull) return true;
            if (expected is TypeInfo.Null) return true;
            return false;
        }

        // Undefined compatibility (strictNullChecks: on)
        if (actual is TypeInfo.Undefined)
        {
            if (expected is TypeInfo.Union u && u.ContainsUndefined) return true;
            if (expected is TypeInfo.Undefined) return true;
            return false;
        }

        // Literal type compatibility - literal to literal (must have same value)
        if (expected is TypeInfo.StringLiteral sl1 && actual is TypeInfo.StringLiteral sl2)
            return sl1.Value == sl2.Value;
        if (expected is TypeInfo.NumberLiteral nl1 && actual is TypeInfo.NumberLiteral nl2)
            return nl1.Value == nl2.Value;
        if (expected is TypeInfo.BooleanLiteral bl1 && actual is TypeInfo.BooleanLiteral bl2)
            return bl1.Value == bl2.Value;

        // Literal to primitive widening
        if (expected is TypeInfo.String && actual is TypeInfo.StringLiteral)
            return true;
        if (expected is TypeInfo.Primitive { Type: TokenType.TYPE_NUMBER } && actual is TypeInfo.NumberLiteral)
            return true;
        if (expected is TypeInfo.Primitive { Type: TokenType.TYPE_BOOLEAN } && actual is TypeInfo.BooleanLiteral)
            return true;

        // Template literal type compatibility

        // Template literal widens to string
        if (expected is TypeInfo.String && actual is TypeInfo.TemplateLiteralType)
            return true;

        // String literal matches template literal pattern
        if (expected is TypeInfo.TemplateLiteralType expectedTL && actual is TypeInfo.StringLiteral actualSL)
            return MatchesTemplateLiteralPattern(expectedTL, actualSL.Value);

        // Template literal to template literal: structural compatibility
        if (expected is TypeInfo.TemplateLiteralType expTL && actual is TypeInfo.TemplateLiteralType actTL)
            return TemplatePatternStructurallyCompatible(expTL, actTL);

        // Intrinsic string type: evaluate and check
        if (actual is TypeInfo.IntrinsicStringType ist)
        {
            var evaluated = EvaluateIntrinsicStringType(ist.Inner, ist.Operation);
            return IsCompatible(expected, evaluated);
        }

        // Union-to-union: each type in actual must be compatible with at least one type in expected
        if (expected is TypeInfo.Union expectedUnion && actual is TypeInfo.Union actualUnion)
        {
            var expectedTypes = expectedUnion.FlattenedTypes;
            var actualTypes = actualUnion.FlattenedTypes;
            return actualTypes.All(actualType =>
                expectedTypes.Any(expectedType => IsCompatible(expectedType, actualType)));
        }

        // Union as expected: actual must match at least one member
        if (expected is TypeInfo.Union expUnion)
        {
            var expTypes = expUnion.FlattenedTypes;
            return expTypes.Any(t => IsCompatible(t, actual));
        }

        // Union as actual: all members must be compatible with expected
        if (actual is TypeInfo.Union actUnion)
        {
            var actTypes = actUnion.FlattenedTypes;
            return actTypes.All(t => IsCompatible(expected, t));
        }

        // Intersection as expected: actual must satisfy ALL member types
        if (expected is TypeInfo.Intersection expIntersection)
        {
            var expTypes = expIntersection.FlattenedTypes;
            return expTypes.All(t => IsCompatible(t, actual));
        }

        // Intersection as actual: satisfies expected if any member does
        // (because intersection value has all the properties of all its constituents)
        if (actual is TypeInfo.Intersection actIntersection)
        {
            var actTypes = actIntersection.FlattenedTypes;
            return actTypes.Any(t => IsCompatible(expected, t));
        }

        // KeyOf type compatibility - must evaluate to compare
        // Special handling for keyof T where T is a type parameter - don't try to expand
        if (expected is TypeInfo.KeyOf expectedKeyOf)
        {
            // If source is a type parameter, don't expand to avoid infinite recursion
            if (expectedKeyOf.SourceType is TypeInfo.TypeParameter)
            {
                // keyof T is compatible with string, number, symbol, or any
                return actual is TypeInfo.String or TypeInfo.StringLiteral or TypeInfo.Any or
                       TypeInfo.Primitive { Type: Parsing.TokenType.TYPE_NUMBER } or
                       TypeInfo.NumberLiteral or TypeInfo.Symbol or TypeInfo.TypeParameter;
            }
            TypeInfo expandedExpected = EvaluateKeyOf(expectedKeyOf.SourceType);
            return IsCompatible(expandedExpected, actual);
        }
        if (actual is TypeInfo.KeyOf actualKeyOf)
        {
            // If source is a type parameter, don't expand to avoid infinite recursion
            if (actualKeyOf.SourceType is TypeInfo.TypeParameter)
            {
                // keyof T is compatible with string, number, symbol, or any
                return expected is TypeInfo.String or TypeInfo.StringLiteral or TypeInfo.Any or
                       TypeInfo.Primitive { Type: Parsing.TokenType.TYPE_NUMBER } or
                       TypeInfo.NumberLiteral or TypeInfo.Symbol or TypeInfo.KeyOf;
            }
            TypeInfo expandedActual = EvaluateKeyOf(actualKeyOf.SourceType);
            return IsCompatible(expected, expandedActual);
        }

        // Mapped type compatibility - expand lazily then compare
        if (expected is TypeInfo.MappedType expectedMapped)
        {
            TypeInfo expandedExpected = ExpandMappedType(expectedMapped);
            return IsCompatible(expandedExpected, actual);
        }
        if (actual is TypeInfo.MappedType actualMapped)
        {
            TypeInfo expandedActual = ExpandMappedType(actualMapped);
            return IsCompatible(expected, expandedActual);
        }

        // Indexed access type compatibility - resolve then compare
        if (expected is TypeInfo.IndexedAccess expectedIA)
        {
            TypeInfo expandedExpected = ResolveIndexedAccess(expectedIA, new Dictionary<string, TypeInfo>());
            return IsCompatible(expandedExpected, actual);
        }
        if (actual is TypeInfo.IndexedAccess actualIA)
        {
            TypeInfo expandedActual = ResolveIndexedAccess(actualIA, new Dictionary<string, TypeInfo>());
            return IsCompatible(expected, expandedActual);
        }

        // Conditional type compatibility - evaluate then compare
        if (expected is TypeInfo.ConditionalType expectedCond)
        {
            TypeInfo expandedExpected = EvaluateConditionalType(expectedCond);
            return IsCompatible(expandedExpected, actual);
        }
        if (actual is TypeInfo.ConditionalType actualCond)
        {
            TypeInfo expandedActual = EvaluateConditionalType(actualCond);
            return IsCompatible(expected, expandedActual);
        }

        // InferredTypeParameter should not appear in compatibility checks
        // (they should be resolved during conditional type evaluation)
        if (expected is TypeInfo.InferredTypeParameter || actual is TypeInfo.InferredTypeParameter)
        {
            return false; // Unresolved infer parameters are not compatible with anything
        }

        // Enum compatibility: primitive values are assignable to their enum type
        // (e.g., Direction.Up which is typed as 'number' can be assigned to Direction)
        if (expected is TypeInfo.Enum expectedEnum)
        {
            // Same enum type is compatible
            if (actual is TypeInfo.Enum actualEnum && expectedEnum.Name == actualEnum.Name)
                return true;

            // Numeric enum accepts number
            if (expectedEnum.Kind == EnumKind.Numeric &&
                actual is TypeInfo.Primitive { Type: TokenType.TYPE_NUMBER })
                return true;

            // String enum accepts string
            if (expectedEnum.Kind == EnumKind.String && actual is TypeInfo.String)
                return true;

            // Heterogeneous enum accepts both
            if (expectedEnum.Kind == EnumKind.Heterogeneous &&
                (actual is TypeInfo.String || actual is TypeInfo.Primitive { Type: TokenType.TYPE_NUMBER }))
                return true;

            return false;
        }

        // Enum as actual: can be assigned to compatible primitive type
        // (e.g., a Direction variable can be used where a number is expected)
        if (actual is TypeInfo.Enum actualEnumType)
        {
            if (actualEnumType.Kind == EnumKind.Numeric &&
                expected is TypeInfo.Primitive { Type: TokenType.TYPE_NUMBER })
                return true;

            if (actualEnumType.Kind == EnumKind.String && expected is TypeInfo.String)
                return true;

            if (actualEnumType.Kind == EnumKind.Heterogeneous &&
                (expected is TypeInfo.String || expected is TypeInfo.Primitive { Type: TokenType.TYPE_NUMBER }))
                return true;
        }

        if (expected is TypeInfo.Primitive p1 && actual is TypeInfo.Primitive p2)
        {
            return p1.Type == p2.Type;
        }

        // String type compatibility
        if (expected is TypeInfo.String && actual is TypeInfo.String)
        {
            return true;
        }

        // UniqueSymbol type compatibility (nominal typing)
        // unique symbol -> unique symbol: must be same declaration
        if (expected is TypeInfo.UniqueSymbol expectedUnique)
        {
            if (actual is TypeInfo.UniqueSymbol actualUnique)
                return expectedUnique.DeclarationId == actualUnique.DeclarationId;
            return false; // regular symbol NOT assignable to unique symbol
        }

        // Symbol type compatibility
        // symbol accepts both symbol and unique symbol (unique symbol is subtype of symbol)
        if (expected is TypeInfo.Symbol)
        {
            return actual is TypeInfo.Symbol or TypeInfo.UniqueSymbol;
        }

        // BigInt type compatibility
        if (expected is TypeInfo.BigInt && actual is TypeInfo.BigInt)
        {
            return true;
        }

        // Buffer type compatibility
        if (expected is TypeInfo.Buffer && actual is TypeInfo.Buffer)
        {
            return true;
        }

        // Promise type compatibility - Promise<A> is compatible with Promise<B> if A is compatible with B
        if (expected is TypeInfo.Promise expPromise && actual is TypeInfo.Promise actPromise)
        {
            return IsCompatible(expPromise.ValueType, actPromise.ValueType);
        }

        // Map type compatibility - Map<K1, V1> is compatible with Map<K2, V2> if K1=K2 and V1=V2
        if (expected is TypeInfo.Map expMap && actual is TypeInfo.Map actMap)
        {
            return IsCompatible(expMap.KeyType, actMap.KeyType) &&
                   IsCompatible(expMap.ValueType, actMap.ValueType);
        }

        // Set type compatibility - Set<T1> is compatible with Set<T2> if T1=T2
        if (expected is TypeInfo.Set expSet && actual is TypeInfo.Set actSet)
        {
            return IsCompatible(expSet.ElementType, actSet.ElementType);
        }

        // WeakMap type compatibility - WeakMap<K1, V1> is compatible with WeakMap<K2, V2> if K1=K2 and V1=V2
        if (expected is TypeInfo.WeakMap expWeakMap && actual is TypeInfo.WeakMap actWeakMap)
        {
            return IsCompatible(expWeakMap.KeyType, actWeakMap.KeyType) &&
                   IsCompatible(expWeakMap.ValueType, actWeakMap.ValueType);
        }

        // WeakSet type compatibility - WeakSet<T1> is compatible with WeakSet<T2> if T1=T2
        if (expected is TypeInfo.WeakSet expWeakSet && actual is TypeInfo.WeakSet actWeakSet)
        {
            return IsCompatible(expWeakSet.ElementType, actWeakSet.ElementType);
        }

        // WeakRef type compatibility - WeakRef<T1> is compatible with WeakRef<T2> if T1=T2
        if (expected is TypeInfo.WeakRef expWeakRef && actual is TypeInfo.WeakRef actWeakRef)
        {
            return IsCompatible(expWeakRef.TargetType, actWeakRef.TargetType);
        }

        if (expected is TypeInfo.Instance i1 && actual is TypeInfo.Instance i2)
        {
            // Handle InstantiatedGeneric expected type - check if actual's class hierarchy includes it
            if (i1.ClassType is TypeInfo.InstantiatedGeneric expectedIG)
            {
                // Check if actual is also the same InstantiatedGeneric
                if (i2.ClassType is TypeInfo.InstantiatedGeneric actualIG)
                {
                    // Same generic definition and compatible type arguments (variance-aware)
                    if (expectedIG.GenericDefinition is TypeInfo.GenericClass gc1 &&
                        actualIG.GenericDefinition is TypeInfo.GenericClass gc2 &&
                        gc1.Name == gc2.Name)
                    {
                        if (expectedIG.TypeArguments.Count != actualIG.TypeArguments.Count)
                            return false;
                        // Check type arguments respecting variance annotations
                        if (!AreTypeArgumentsCompatible(gc1.TypeParams, expectedIG.TypeArguments, actualIG.TypeArguments))
                            return false;
                        return true;
                    }
                    // Check if actualIG's hierarchy includes expectedIG
                    return IsInSuperclassChain(actualIG, expectedIG);
                }

                // Check if actual is a regular Class that extends the expected InstantiatedGeneric
                // e.g., NumberBox extends Box<number>, checking if NumberBox assignable to Box<number>
                if (i2.ClassType is TypeInfo.Class actualClassForIG)
                {
                    return IsInSuperclassChain(actualClassForIG, expectedIG);
                }
            }

            // Handle regular Class comparison (including MutableClass resolution)
            // Use ResolvedClassType to handle MutableClass instances that may occur during signature collection
            var resolvedExpected = i1.ResolvedClassType;
            var resolvedActual = i2.ResolvedClassType;

            if (resolvedExpected is TypeInfo.Class expectedClass && resolvedActual is TypeInfo.Class actualClass)
            {
                // Check direct class hierarchy (by name)
                TypeInfo? current = actualClass;
                while (current != null)
                {
                    if (current is TypeInfo.Class cls && cls.Name == expectedClass.Name) return true;
                    current = GetSuperclass(current);
                }

                // Structural compatibility (TypeScript): when the target class carries no nominal
                // brand, a source instance is assignable if it structurally provides the target's
                // public members and satisfies its index signatures (generic args substituted). A
                // branded or member-less/index-less target stays nominal (handled by the walk above,
                // preserving subclass-safety).
                if (StructurallyAssignableToClassTarget(expectedClass, i2)) return true;
            }
            // Handle MutableClass (unfrozen) comparison by name - occurs during signature collection
            else if (resolvedExpected is TypeInfo.MutableClass mc1 && resolvedActual is TypeInfo.MutableClass mc2)
            {
                return mc1.Name == mc2.Name;
            }

            // Mixed case: InstantiatedGeneric vs regular Class - not compatible unless in hierarchy
            return false;
        }

        // Structural (TypeScript): an unbranded target class instance — including a generic-class
        // instantiation like `A<Base>` — accepts any structurally-matching object-like source (an
        // interface value or object literal/record). Class-instance sources are handled by the
        // Instance-vs-Instance block above; the reverse directions (a class instance assignable to an
        // interface/record) are handled by the structural paths below.
        if (expected is TypeInfo.Instance targetInst &&
            actual is TypeInfo.Interface or TypeInfo.Record &&
            StructurallyAssignableToClassTarget(targetInst.ResolvedClassType, actual))
        {
            return true;
        }

        if (expected is TypeInfo.Interface itf)
        {
            // Callable interface: the source must satisfy the call signatures.
            if (itf.IsCallable)
            {
                if (actual is TypeInfo.Function func)
                    return FunctionMatchesCallSignatures(func, itf.CallSignatures!);

                if (GetCallSignatures(actual) is { } actualCallSigs)
                {
                    foreach (var es in itf.CallSignatures!)
                        if (!actualCallSigs.Any(@as => IsCompatible(CallSignatureToFunction(es), CallSignatureToFunction(@as))))
                            return false;
                    // Non-signature members (if any) still checked by the structural path below.
                    if (itf.Members.Count == 0) return true;
                }
                // Other actuals (e.g. generic functions) may still be callable via downstream
                // paths — fall through rather than rejecting here.
            }

            // Constructable interface: the source must satisfy the construct signatures.
            if (itf.IsConstructable)
            {
                if (actual is TypeInfo.Class cls)
                    return ClassMatchesConstructorSignatures(cls, itf.ConstructorSignatures!);

                if (GetConstructorSignatures(actual) is { } actualCtorSigs)
                {
                    if (!ConstructorSignaturesSatisfiedBy(itf.ConstructorSignatures!, actualCtorSigs)) return false;
                    if (itf.Members.Count == 0) return true;
                }
                else if (actual is not TypeInfo.Any)
                {
                    return false; // a constructable interface is not satisfied by a non-constructable source
                }
            }

            // If actual is also an interface, compare member-to-member structurally
            if (actual is TypeInfo.Interface actualItf)
            {
                // Check that actual has all required members with compatible types (including inherited)
                var allExpectedMembers = itf.GetAllMembers().ToDictionary(m => m.Key, m => m.Value);
                var allExpectedOptional = itf.GetAllOptionalMembers().ToHashSet();
                var allActualMembers = actualItf.GetAllMembers().ToDictionary(m => m.Key, m => m.Value);

                foreach (var member in allExpectedMembers)
                {
                    if (!allActualMembers.TryGetValue(member.Key, out var actualMemberType))
                    {
                        // Member missing - check if optional
                        if (!allExpectedOptional.Contains(member.Key))
                            return false;
                    }
                    else if (!IsCompatible(member.Value, actualMemberType))
                    {
                        return false;
                    }
                }
                return IndexSignaturesSatisfied(itf, actualItf);
            }
            // Use GetAllMembers to include inherited members when checking structural compatibility
            var allMembers = itf.GetAllMembers().ToDictionary(m => m.Key, m => m.Value);
            var allOptional = itf.GetAllOptionalMembers().ToHashSet();
            return CheckStructuralCompatibility(allMembers, actual, allOptional)
                && IndexSignaturesSatisfied(itf, actual);
        }

        // Handle InstantiatedGeneric interface (e.g., Container<number>)
        if (expected is TypeInfo.InstantiatedGeneric expectedInterfaceIG &&
            expectedInterfaceIG.GenericDefinition is TypeInfo.GenericInterface gi)
        {
            // Check if actual is also the same generic interface with different type arguments
            if (actual is TypeInfo.InstantiatedGeneric actualInterfaceIG &&
                actualInterfaceIG.GenericDefinition is TypeInfo.GenericInterface actualGI &&
                gi.Name == actualGI.Name)
            {
                // Same generic interface - check type arguments with variance
                if (expectedInterfaceIG.TypeArguments.Count != actualInterfaceIG.TypeArguments.Count)
                    return false;
                if (!AreTypeArgumentsCompatible(gi.TypeParams, expectedInterfaceIG.TypeArguments, actualInterfaceIG.TypeArguments))
                    return false;
                return true;
            }

            // Build substitution map for structural comparison
            Dictionary<string, TypeInfo> subs = [];
            for (int i = 0; i < gi.TypeParams.Count; i++)
                subs[gi.TypeParams[i].Name] = expectedInterfaceIG.TypeArguments[i];

            // Substitute type parameters in interface members
            Dictionary<string, TypeInfo> substitutedMembers = [];
            foreach (var kvp in gi.Members)
                substitutedMembers[kvp.Key] = Substitute(kvp.Value, subs);

            return CheckStructuralCompatibility(substitutedMembers, actual, gi.OptionalMembers);
        }

        if (expected is TypeInfo.Array a1 && actual is TypeInfo.Array a2)
        {
            return IsCompatible(a1.ElementType, a2.ElementType);
        }

        // Callable / constructable inline object types: `{ (x): T }`, `{ new (x): T }`, and mixed
        // forms with named fields. Mirrors the callable-interface handling above.
        if (expected is TypeInfo.Record exSigRec && (exSigRec.IsCallable || exSigRec.IsConstructable))
        {
            if (exSigRec.IsCallable)
            {
                if (actual is TypeInfo.Function callableFunc)
                {
                    if (!FunctionMatchesCallSignatures(callableFunc, exSigRec.CallSignatures!)) return false;
                }
                else if (GetCallSignatures(actual) is { } actualSigs)
                {
                    // Callable interface/object source: each expected signature must be matched.
                    foreach (var es in exSigRec.CallSignatures!)
                        if (!actualSigs.Any(@as => IsCompatible(CallSignatureToFunction(es), CallSignatureToFunction(@as))))
                            return false;
                }
                // Other actuals (e.g. generic functions) may still be callable via downstream paths.
            }

            if (exSigRec.IsConstructable)
            {
                if (actual is TypeInfo.Class constructableCls)
                {
                    if (!ClassMatchesConstructorSignatures(constructableCls, exSigRec.ConstructorSignatures!)) return false;
                }
                else if (GetConstructorSignatures(actual) is { } actualCtorSigs)
                {
                    if (!ConstructorSignaturesSatisfiedBy(exSigRec.ConstructorSignatures!, actualCtorSigs)) return false;
                }
                else if (actual is not TypeInfo.Any)
                {
                    return false; // a constructable object type is not satisfied by a non-constructable source
                }
            }

            // Mixed object types still must match their named fields structurally.
            if (exSigRec.Fields.Count > 0)
                return CheckStructuralCompatibility(exSigRec.Fields, actual, exSigRec.OptionalFields);
            return true;
        }

        // Record-to-Record compatibility (inline object types)
        if (expected is TypeInfo.Record expRecord && actual is TypeInfo.Record actRecord)
        {
            // All required fields in expected must exist in actual with compatible types
            // Optional fields can be omitted
            foreach (var (name, expectedFieldType) in expRecord.Fields)
            {
                if (!actRecord.Fields.TryGetValue(name, out var actualFieldType))
                {
                    // Field missing - only OK if the field is optional
                    if (!expRecord.IsFieldOptional(name))
                        return false;
                }
                else if (!IsCompatible(expectedFieldType, actualFieldType))
                {
                    return false;
                }
            }
            // Named fields match; the target's index signatures (if any) must also be satisfied by
            // the source's members and own index signatures.
            return IndexSignaturesSatisfied(expRecord, actRecord);
        }

        // Record constraint compatibility with types that have members (String, Array, etc.)
        // This handles cases like `T extends { length: number }` with strings or arrays
        if (expected is TypeInfo.Record expRec)
        {
            // Use CheckStructuralCompatibility to check if actual type has all required fields
            return CheckStructuralCompatibility(expRec.Fields, actual, expRec.OptionalFields)
                && IndexSignaturesSatisfied(expRec, actual);
        }

        // Tuple-to-tuple compatibility
        if (expected is TypeInfo.Tuple expTuple && actual is TypeInfo.Tuple actTuple)
        {
            return IsTupleCompatible(expTuple, actTuple);
        }

        // Tuple assignable to array (e.g., [string, number] -> (string | number)[])
        if (expected is TypeInfo.Array expArr && actual is TypeInfo.Tuple actTuple2)
        {
            return IsTupleToArrayCompatible(expArr, actTuple2);
        }

        // Array assignable to tuple (limited - only for rest tuples or all-optional)
        if (expected is TypeInfo.Tuple expTuple2 && actual is TypeInfo.Array actArr)
        {
            return IsArrayToTupleCompatible(expTuple2, actArr);
        }

        if (expected is TypeInfo.Void && actual is TypeInfo.Void) return true;

        // OverloadedFunction expected: actual function must satisfy all overload signatures
        // This handles cases like interface method overloads being satisfied by a union-parameter function
        if (expected is TypeInfo.OverloadedFunction overloadedFunc && actual is TypeInfo.Function actualFunc)
        {
            // The actual function must satisfy ALL overload signatures
            foreach (var signature in overloadedFunc.Signatures)
            {
                if (!IsFunctionCompatibleWithSignature(actualFunc, signature))
                    return false;
            }
            return true;
        }

        // Function type compatibility
        // A callable interface or object type (`{ (x): T }`) is assignable to a function-typed
        // target when one of its call signatures satisfies that function type.
        if (expected is TypeInfo.Function expectedFunc && GetCallSignatures(actual) is { } sourceCallSigs)
        {
            return CallableAssignableToFunction(sourceCallSigs, expectedFunc);
        }

        if (expected is TypeInfo.Function f1 && actual is TypeInfo.Function f2)
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

        return false;
    }

    /// <summary>
    /// Checks if an actual function can satisfy a specific signature from an overloaded function.
    /// Used for structural typing when an interface has overloaded methods.
    /// The actual function with union parameters can satisfy multiple specific signatures.
    /// </summary>
    private bool IsFunctionCompatibleWithSignature(TypeInfo.Function actualFunc, TypeInfo.Function signature)
    {
        // The actual function must have at least as many parameters as the signature requires
        // But it can have more parameters (they'd be optional or union parameters)
        if (actualFunc.ParamTypes.Count < signature.MinArity)
            return false;

        // For each parameter position in the signature, the signature's param type
        // must be assignable to the actual param type (contravariance)
        // This ensures the actual function can accept calls matching the signature
        for (int i = 0; i < signature.ParamTypes.Count && i < actualFunc.ParamTypes.Count; i++)
        {
            // Signature param must be assignable to actual param (contravariance for function params)
            // If signature expects (number), actual can accept (number | string)
            if (!IsCompatible(actualFunc.ParamTypes[i], signature.ParamTypes[i]))
                return false;
        }

        // Return type: actual must be assignable to signature (covariance)
        if (signature.ReturnType is not TypeInfo.Void)
        {
            if (!IsCompatible(signature.ReturnType, actualFunc.ReturnType))
                return false;
        }

        return true;
    }
}
