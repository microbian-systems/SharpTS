namespace SharpTS.TypeSystem;

/// <summary>
/// Callable and constructable interface matching for type compatibility.
/// </summary>
public partial class TypeChecker
{
    /// <summary>
    /// Checks if a function type matches any of the call signatures in an interface.
    /// Used for assigning functions to callable interface types.
    /// </summary>
    private bool FunctionMatchesCallSignatures(TypeInfo.Function func, List<TypeInfo.CallSignature> callSignatures)
    {
        return callSignatures.Any(sig => FunctionMatchesCallSignature(func, sig));
    }

    /// <summary>
    /// Checks if a function type matches a single call signature. A call signature <c>(p): r</c>
    /// is itself a function type, so assignability is just function-to-function assignability —
    /// route through the shared logic so void-return covariance and parameter-arity rules stay
    /// consistent in one place.
    /// </summary>
    private bool FunctionMatchesCallSignature(TypeInfo.Function func, TypeInfo.CallSignature sig)
    {
        // Generic call signatures are complex to match structurally - defer to the actual call.
        if (sig.IsGeneric)
            return false;

        return IsCompatible(CallSignatureToFunction(sig), func);
    }

    /// <summary>Views a call signature as the function type it denotes.</summary>
    private static TypeInfo.Function CallSignatureToFunction(TypeInfo.CallSignature sig) =>
        new(sig.ParamTypes, sig.ReturnType, sig.RequiredParams, sig.HasRestParam, ThisType: null, sig.ParamNames);

    /// <summary>
    /// Call signatures carried by a callable type (interface or inline object type), or null if the
    /// type is not callable. Used to make callable interfaces and object types interchangeable in
    /// assignability checks.
    /// </summary>
    private static List<TypeInfo.CallSignature>? GetCallSignatures(TypeInfo type) => type switch
    {
        TypeInfo.Interface { IsCallable: true } itf => itf.CallSignatures,
        TypeInfo.Record { IsCallable: true } rec => rec.CallSignatures,
        _ => null,
    };

    /// <summary>
    /// Checks that every call signature of a callable source type is assignable to the target
    /// function type — i.e. the callable can stand in for the function.
    /// </summary>
    private bool CallableAssignableToFunction(List<TypeInfo.CallSignature> sourceSignatures, TypeInfo.Function target)
    {
        foreach (var sig in sourceSignatures)
        {
            if (sig.IsGeneric) continue; // best-effort: skip generic signatures
            if (IsCompatible(target, CallSignatureToFunction(sig)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Construct signatures carried by a constructable type (interface or inline object type), or
    /// null if the type is not constructable.
    /// </summary>
    private static List<TypeInfo.ConstructorSignature>? GetConstructorSignatures(TypeInfo type) => type switch
    {
        TypeInfo.Interface { IsConstructable: true } itf => itf.ConstructorSignatures,
        TypeInfo.Record { IsConstructable: true } rec => rec.ConstructorSignatures,
        _ => null,
    };

    /// <summary>Views a construct signature as the (constructor) function type it denotes.</summary>
    private static TypeInfo.Function ConstructorSignatureToFunction(TypeInfo.ConstructorSignature sig) =>
        new(sig.ParamTypes, sig.ReturnType, sig.RequiredParams, sig.HasRestParam, ThisType: null, sig.ParamNames);

    /// <summary>
    /// Checks that every construct signature required by the target is satisfied by one of the
    /// source's construct signatures (parameter contravariance, return covariance via the shared
    /// function-compatibility logic).
    /// </summary>
    private bool ConstructorSignaturesSatisfiedBy(
        List<TypeInfo.ConstructorSignature> targetSignatures,
        List<TypeInfo.ConstructorSignature> sourceSignatures)
    {
        foreach (var ts in targetSignatures)
        {
            if (ts.IsGeneric) continue; // best-effort: skip generic signatures
            var targetFunc = ConstructorSignatureToFunction(ts);
            if (!sourceSignatures.Any(ss => !ss.IsGeneric && IsCompatible(targetFunc, ConstructorSignatureToFunction(ss))))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if a class matches any of the constructor signatures in an interface.
    /// Used for assigning classes to constructable interface types.
    /// </summary>
    private bool ClassMatchesConstructorSignatures(TypeInfo.Class cls, List<TypeInfo.ConstructorSignature> constructorSignatures)
    {
        return constructorSignatures.Any(sig => ClassMatchesConstructorSignature(cls, sig));
    }

    /// <summary>
    /// Checks if a class matches a single constructor signature.
    /// </summary>
    private bool ClassMatchesConstructorSignature(TypeInfo.Class cls, TypeInfo.ConstructorSignature sig)
    {
        // Generic signatures need special handling
        if (sig.IsGeneric)
        {
            // For generic constructor signatures, defer to actual instantiation
            return false;
        }

        // Get the class constructor
        if (!cls.Methods.TryGetValue("constructor", out var ctorTypeInfo))
        {
            // No constructor - check if signature accepts zero arguments
            return sig.MinArity == 0;
        }

        // Handle constructor type (may be Function or OverloadedFunction)
        if (ctorTypeInfo is TypeInfo.OverloadedFunction overloadedCtor)
        {
            // Check if any overload matches
            return overloadedCtor.Signatures.Any(ctorSig => ConstructorSignatureMatches(ctorSig, sig));
        }
        else if (ctorTypeInfo is TypeInfo.Function ctorFunc)
        {
            return ConstructorSignatureMatches(ctorFunc, sig);
        }

        return false;
    }

    /// <summary>
    /// Checks if a constructor function signature matches a constructor signature from an interface.
    /// </summary>
    private bool ConstructorSignatureMatches(TypeInfo.Function ctorFunc, TypeInfo.ConstructorSignature sig)
    {
        // Check parameter count compatibility
        if (ctorFunc.MinArity > sig.ParamTypes.Count)
            return false;

        if (ctorFunc.ParamTypes.Count < sig.MinArity)
            return false;

        // Check parameter type compatibility (contravariant)
        int paramCount = Math.Min(ctorFunc.ParamTypes.Count, sig.ParamTypes.Count);
        for (int i = 0; i < paramCount; i++)
        {
            if (!IsCompatible(ctorFunc.ParamTypes[i], sig.ParamTypes[i]))
                return false;
        }

        // Note: Constructor return type is handled by the class - we don't check it here
        // The sig.ReturnType specifies what the new expression produces, which is determined by the class itself
        return true;
    }
}
