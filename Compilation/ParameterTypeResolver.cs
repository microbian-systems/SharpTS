using SharpTS.Parsing;
using SharpTS.TypeSystem;
using TSTypeInfo = SharpTS.TypeSystem.TypeInfo;

namespace SharpTS.Compilation;

/// <summary>
/// Centralized parameter and return type resolution from TypeMap.
/// Provides typed .NET types instead of defaulting to object.
/// </summary>
public static class ParameterTypeResolver
{
    /// <summary>
    /// Resolves parameter types from TypeMap function type info.
    /// Falls back to object if type info is not available.
    /// </summary>
    /// <param name="parameters">Parameters from AST</param>
    /// <param name="typeMapper">TypeMapper for converting TypeInfo to .NET Type</param>
    /// <param name="funcType">Function type from TypeMap (may be null)</param>
    /// <returns>Array of .NET types for each parameter</returns>
    public static Type[] ResolveParameters(
        List<Stmt.Parameter> parameters,
        TypeMapper typeMapper,
        TSTypeInfo.Function? funcType)
    {
        if (funcType == null || funcType.ParamTypes.Count != parameters.Count)
        {
            // Fallback: try to resolve from parameter type annotations
            return parameters.Select(p => ResolveParameterType(p, typeMapper)).ToArray();
        }

        // Map each parameter type, but use 'object' for:
        // 1. Optional parameters without explicit defaults (preserves null-checking)
        // 2. BigInteger parameters (operations expect boxed values)
        // 3. Rest parameters — `$TSFunction.Invoke` / `AdjustArgs` only recognize
        //    `List<object>` when packing trailing args; using a typed list like
        //    `List<string>` for `...parts: string[]` breaks the dispatch path
        //    (method gets invoked without rest packing, trailing args are dropped).
        return funcType.ParamTypes
            .Select((pt, i) =>
            {
                var mappedType = typeMapper.MapTypeInfoStrict(pt);

                // BigInteger parameters need to stay as object because BigInt operations
                // in the emitter expect boxed values
                if (mappedType == typeof(System.Numerics.BigInteger))
                {
                    return typeof(object);
                }

                // If parameter is optional (no explicit default), use object so undefined
                // can be passed as the missing-argument sentinel (JS spec)
                if (i < parameters.Count &&
                    parameters[i].DefaultValue == null &&
                    parameters[i].IsOptional)
                {
                    return typeof(object);
                }

                // Rest parameter — dispatch helper expects List<object> as the marker.
                if (i < parameters.Count && parameters[i].IsRest)
                {
                    return typeof(List<object>);
                }

                // Function-typed params: runtime values are $TSFunction or other
                // callable classes (e.g. PromisifyCallback), not .NET Delegate
                // subclasses. Signatures that demand Delegate reject them at
                // MethodInfo.Invoke time.
                if (mappedType == typeof(Delegate) || mappedType.IsSubclassOf(typeof(Delegate)))
                {
                    return typeof(object);
                }

                return mappedType;
            })
            .ToArray();
    }

    /// <summary>
    /// Resolves a single parameter's type from its annotation or defaults to object.
    /// </summary>
    private static Type ResolveParameterType(Stmt.Parameter param, TypeMapper typeMapper)
    {
        if (param.Type == null)
            return typeof(object);

        // Parse the type annotation and map to .NET type
        var typeInfo = ParseTypeAnnotation(param.Type);
        return typeMapper.MapTypeInfoStrict(typeInfo);
    }

    /// <summary>
    /// Resolves the return type for a function.
    /// </summary>
    /// <param name="returnTypeInfo">Return type from TypeMap (may be null)</param>
    /// <param name="isAsync">Whether this is an async function</param>
    /// <param name="typeMapper">TypeMapper for conversion</param>
    /// <param name="returnMayBeUndefined">
    /// True when the type checker found a return value of static type <c>any</c>/<c>unknown</c>
    /// flowing into this <c>number</c>/<c>boolean</c> return (e.g. <c>return undefined as any</c>).
    /// An unboxed <c>double</c>/<c>bool</c> slot cannot carry the runtime <c>undefined</c> sentinel,
    /// so it is widened to <c>object</c> for just those functions to avoid silently coercing
    /// <c>undefined</c> to <c>NaN</c>/<c>false</c>. (#344)
    /// </param>
    /// <returns>.NET type for the return value</returns>
    public static Type ResolveReturnType(
        TSTypeInfo? returnTypeInfo,
        bool isAsync,
        TypeMapper typeMapper,
        bool returnMayBeUndefined = false)
    {
        Type baseType;

        if (returnTypeInfo == null)
        {
            baseType = typeof(object);
        }
        else
        {
            baseType = typeMapper.MapTypeInfoStrict(returnTypeInfo);

            // A `number`/`boolean` return reached by an `undefined`-admitting value (any/unknown)
            // must use an object slot — the unboxed double/bool slot would coerce the runtime
            // `undefined` sentinel to NaN/false. Only the genuinely-undefined-reachable functions
            // pay this; the common sound numeric/boolean return keeps its unboxed slot. (#344)
            if (returnMayBeUndefined && (baseType == typeof(double) || baseType == typeof(bool)))
            {
                baseType = typeof(object);
            }

            // BigInteger returns need to stay as object because BigInt operations
            // in the emitter expect boxed values
            if (baseType == typeof(System.Numerics.BigInteger))
            {
                baseType = typeof(object);
            }

            // Function types return $TSFunction objects, not Delegate, so use object
            if (baseType == typeof(Delegate) || baseType.IsSubclassOf(typeof(Delegate)))
            {
                baseType = typeof(object);
            }

            // String return slots are unsound. TS inference admits `undefined` in a
            // `string`-typed expression (e.g. `cond ? "x" : undefined` infers `string`;
            // an explicit `: string` annotation is rejected for `return undefined`, so
            // inference is the reachable path), but a .NET `string` slot cannot carry the
            // `$Undefined` sentinel. A castclass at the return site throws
            // InvalidCastException at runtime, and isinst/coercion corrupts `undefined`
            // into null/"undefined" (observable through Map keys, typeof, ===). Unlike
            // `double`/`bool` slots there is no boxing to avoid — strings are reference
            // types — so a `string` slot buys nothing. Fall back to object. (#318)
            if (baseType == typeof(string))
            {
                baseType = typeof(object);
            }

            // Nullable value types (like number | null -> double?) need to stay as object
            // because the emitter doesn't have special handling for Nullable<T>
            if (Nullable.GetUnderlyingType(baseType) != null)
            {
                baseType = typeof(object);
            }

            // Discriminated union structs (Union_*) don't work as method return types
            // because MethodInfo.Invoke boxes them opaquely — IsTruthy, equality comparers,
            // and property access can't see the underlying value. Fall back to object so
            // the raw primitives/strings are returned directly.
            if (baseType.IsValueType && baseType.Name.StartsWith("Union_"))
            {
                baseType = typeof(object);
            }

            // Typed array/map/set returns map to List<T>/Dictionary<,>/HashSet<> (the strict
            // collection types), but their runtime representation is a dynamic $Array/TSMap/TSSet
            // carried as object — not CLR-assignable to the declared collection slot. Returning
            // that value into a List<T> slot raises ILVerify StackUnexpected (and a castclass to
            // the collection would throw InvalidCastException at runtime). Fall back to object so
            // the dynamic value is returned directly. (#278)
            if (typeMapper.IsDynamicRuntimeCollection(baseType))
            {
                baseType = typeof(object);
            }
        }

        // Wrap async return types in Task<T>
        if (isAsync)
        {
            if (baseType == typeof(void))
                return typeof(Task);
            return typeof(Task<>).MakeGenericType(baseType);
        }

        return baseType;
    }

    /// <summary>
    /// Resolves parameter types for a class method.
    /// </summary>
    public static Type[] ResolveMethodParameters(
        string className,
        string methodName,
        List<Stmt.Parameter> parameters,
        TypeMapper typeMapper,
        TypeMap? typeMap)
    {
        if (typeMap == null)
            return parameters.Select(p => ResolveParameterType(p, typeMapper)).ToArray();

        var classType = typeMap.GetClassType(className);
        if (classType == null)
            return parameters.Select(p => ResolveParameterType(p, typeMapper)).ToArray();

        TSTypeInfo.Function? funcType = null;

        // Check instance methods
        if (classType.Methods.TryGetValue(methodName, out var methodType))
        {
            funcType = ExtractFunctionType(methodType);
        }
        // Check static methods
        else if (classType.StaticMethods.TryGetValue(methodName, out var staticMethodType))
        {
            funcType = ExtractFunctionType(staticMethodType);
        }

        if (funcType == null || funcType.ParamTypes.Count != parameters.Count)
            return parameters.Select(p => ResolveParameterType(p, typeMapper)).ToArray();

        // Map each parameter type, handling optional parameters and BigInteger
        return funcType.ParamTypes
            .Select((pt, i) =>
            {
                Type mappedType;
                try
                {
                    mappedType = typeMapper.MapTypeInfoStrict(pt);
                }
                catch
                {
                    // Union types may throw during early method definition phase
                    // when TypeBuilder isn't finalized yet. Fall back to object.
                    return typeof(object);
                }

                // BigInteger parameters need to stay as object because BigInt operations
                // in the emitter expect boxed values
                if (mappedType == typeof(System.Numerics.BigInteger))
                {
                    return typeof(object);
                }

                // If parameter is optional (no explicit default), use object so undefined
                // can be passed as the missing-argument sentinel (JS spec)
                if (i < parameters.Count &&
                    parameters[i].DefaultValue == null &&
                    parameters[i].IsOptional)
                {
                    return typeof(object);
                }

                return mappedType;
            })
            .ToArray();
    }

    /// <summary>
    /// Resolves return type for a class method.
    /// </summary>
    public static Type ResolveMethodReturnType(
        string className,
        string methodName,
        bool isAsync,
        TypeMapper typeMapper,
        TypeMap? typeMap)
    {
        if (typeMap == null)
            return isAsync ? typeof(Task<object>) : typeof(object);

        var classType = typeMap.GetClassType(className);
        if (classType == null)
            return isAsync ? typeof(Task<object>) : typeof(object);

        TSTypeInfo.Function? funcType = null;

        // Check instance methods
        if (classType.Methods.TryGetValue(methodName, out var methodType))
        {
            funcType = ExtractFunctionType(methodType);
        }
        // Check static methods
        else if (classType.StaticMethods.TryGetValue(methodName, out var staticMethodType))
        {
            funcType = ExtractFunctionType(staticMethodType);
        }

        if (funcType == null)
            return isAsync ? typeof(Task<object>) : typeof(object);

        return ResolveReturnType(funcType.ReturnType, isAsync, typeMapper);
    }

    /// <summary>
    /// Resolves constructor parameter types for a class.
    /// </summary>
    public static Type[] ResolveConstructorParameters(
        string className,
        List<Stmt.Parameter> parameters,
        TypeMapper typeMapper,
        TypeMap? typeMap)
    {
        if (typeMap == null)
            return parameters.Select(p => ResolveParameterType(p, typeMapper)).ToArray();

        var classType = typeMap.GetClassType(className);
        if (classType == null)
            return parameters.Select(p => ResolveParameterType(p, typeMapper)).ToArray();

        if (!classType.Methods.TryGetValue("constructor", out var ctorTypeInfo))
            return parameters.Select(p => ResolveParameterType(p, typeMapper)).ToArray();

        var funcType = ExtractFunctionType(ctorTypeInfo);
        if (funcType == null || funcType.ParamTypes.Count != parameters.Count)
            return parameters.Select(p => ResolveParameterType(p, typeMapper)).ToArray();

        // Map each parameter type, handling optional parameters and BigInteger
        return funcType.ParamTypes
            .Select((pt, i) =>
            {
                Type mappedType;
                try
                {
                    mappedType = typeMapper.MapTypeInfoStrict(pt);
                }
                catch (NotSupportedException)
                {
                    // Union types may throw during early definition phase
                    return typeof(object);
                }

                // BigInteger parameters need to stay as object
                if (mappedType == typeof(System.Numerics.BigInteger))
                {
                    return typeof(object);
                }

                // Optional parameters without defaults should use object for null-checking
                if (i < parameters.Count &&
                    parameters[i].DefaultValue == null &&
                    parameters[i].IsOptional)
                {
                    if (mappedType.IsValueType)
                    {
                        return typeof(object);
                    }
                }

                return mappedType;
            })
            .ToArray();
    }

    /// <summary>
    /// Extracts parameter types from a method TypeInfo.
    /// </summary>
    private static Type[] ExtractParameterTypes(TSTypeInfo methodType, int paramCount, TypeMapper typeMapper)
    {
        var funcType = ExtractFunctionType(methodType);
        if (funcType == null || funcType.ParamTypes.Count != paramCount)
            return Enumerable.Repeat(typeof(object), paramCount).ToArray();

        return funcType.ParamTypes
            .Select(pt => typeMapper.MapTypeInfoStrict(pt))
            .ToArray();
    }

    /// <summary>
    /// Extracts Function type from a method TypeInfo (handles overloads).
    /// </summary>
    private static TSTypeInfo.Function? ExtractFunctionType(TSTypeInfo methodType)
    {
        return methodType switch
        {
            TSTypeInfo.Function f => f,
            TSTypeInfo.OverloadedFunction of => of.Implementation,
            _ => null
        };
    }

    /// <summary>
    /// Parses a type annotation string into TypeInfo.
    /// Delegates to centralized PrimitiveTypeMappings for consistency.
    /// </summary>
    private static TSTypeInfo ParseTypeAnnotation(string typeAnnotation) =>
        PrimitiveTypeMappings.ParseAnnotation(typeAnnotation);
}
