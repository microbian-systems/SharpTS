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
    /// <param name="typeMap">
    /// TypeMap used to consult the #372 undefined-reachable-parameter flag. May be null (e.g. arrow
    /// resolution without a recorded function type), in which case no parameter is widened on that basis.
    /// </param>
    /// <returns>Array of .NET types for each parameter</returns>
    public static Type[] ResolveParameters(
        List<Stmt.Parameter> parameters,
        TypeMapper typeMapper,
        TSTypeInfo.Function? funcType,
        TypeMap? typeMap = null)
    {
        if (funcType == null || funcType.ParamTypes.Count != parameters.Count)
        {
            // Fallback: try to resolve from parameter type annotations
            return parameters.Select(p => WidenIfUndefinedReachableParam(ResolveParameterType(p, typeMapper), p, typeMap)).ToArray();
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

                // If parameter is optional (no explicit default), use object so undefined
                // can be passed as the missing-argument sentinel (JS spec)
                if (i < parameters.Count &&
                    parameters[i].DefaultValue == null &&
                    parameters[i].IsOptional)
                {
                    return typeof(object);
                }

                // Rest parameter — dispatch helper expects List<object> as the marker. Must precede
                // the runtime-slot fallback below, which would otherwise rewrite this List<object>
                // marker to object as a "dynamic runtime collection".
                if (i < parameters.Count && parameters[i].IsRest)
                {
                    return typeof(List<object>);
                }

                // Fall back to object for any slot whose runtime value is carried as a boxed object:
                // BigInteger, $TSFunction (mapped Delegate), Date/RegExp, $Array/$Map/$Set, Nullable,
                // Union_* structs, and `T | undefined` unions that can hold the $Undefined sentinel.
                // (#278/#568/#573)
                mappedType = SoundRuntimeSlotType(mappedType, typeMapper, pt);

                return WidenIfUndefinedReachableParam(mappedType, parameters[i], typeMap);
            })
            .ToArray();
    }

    /// <summary>
    /// #372: widen a <c>number</c>/<c>boolean</c> parameter slot (unboxed <c>double</c>/<c>bool</c>) to
    /// <c>object</c> when the type checker flagged it as possibly holding the runtime <c>undefined</c>
    /// sentinel (a body reassignment of an <c>any</c>/<c>undefined</c> value, e.g.
    /// <c>function p(x: number) { x = undefined as any; }</c>). The unboxed slot cannot carry the
    /// sentinel — it would coerce to <c>NaN</c>/<c>false</c> (or raw garbage for a never-stored slot).
    /// A no-op for any other type, so it is safe to apply at every parameter mapping site.
    /// </summary>
    private static Type WidenIfUndefinedReachableParam(Type mapped, Stmt.Parameter param, TypeMap? typeMap)
    {
        if ((mapped == typeof(double) || mapped == typeof(bool)) &&
            typeMap != null && typeMap.IsUndefinedReachableNumericParam(param))
            return typeof(object);
        return mapped;
    }

    /// <summary>
    /// Adjusts a CLR type produced by <see cref="TypeMapper.MapTypeInfoStrict"/> so the slot can hold
    /// the boxed SharpTS runtime value that a compiled TS parameter or return actually carries.
    /// <c>MapTypeInfoStrict</c> yields precise CLR types for typed .NET interop (e.g. <c>DateTime</c>,
    /// <c>Regex</c>, <c>List&lt;T&gt;</c>, <c>Nullable&lt;T&gt;</c>, <c>Union_*</c> structs), but the
    /// runtime representation of those TS values is a reference object (<c>$TSDate</c>, <c>$RegExp</c>,
    /// <c>$Array</c>, the <c>$Undefined</c> sentinel, ...) carried as <see cref="object"/>. Storing the
    /// runtime value into the precise slot fails ILVerify (<c>StackUnexpected</c>) and either throws
    /// <see cref="InvalidCastException"/> (value types / <c>castclass</c>) or silently corrupts the
    /// value, so those slots fall back to <see cref="object"/>. Slots whose mapped type already matches
    /// the runtime value (<c>double</c>/<c>bool</c>/<c>string</c>/user classes) are returned unchanged.
    /// (#278/#568/#573)
    /// </summary>
    /// <param name="declaredType">The static TS type of the slot, when known. Used to detect cases the
    /// mapped CLR type alone cannot distinguish — e.g. <c>string | undefined</c> maps to the same
    /// <c>System.String</c> as a plain <c>string</c>, but only the union can carry the <c>$Undefined</c>
    /// sentinel and therefore needs an object slot.</param>
    private static Type SoundRuntimeSlotType(Type mapped, TypeMapper typeMapper, TSTypeInfo? declaredType)
    {
        // A union that admits `undefined` can hold the $Undefined sentinel object, which is neither a
        // CLR null nor an instance of the non-nullish member's mapped type. The mapped type alone can't
        // reveal this (`string | undefined` and `string` both map to System.String), so consult the
        // declared type: a `string` slot throws InvalidCastException when the sentinel is stored, and a
        // `number | undefined` -> Nullable<double> slot produces unverifiable IL. (#568)
        if (declaredType is TSTypeInfo.Union { ContainsUndefined: true })
            return typeof(object);

        // BigInt operations in the emitter expect boxed values.
        if (mapped == typeof(System.Numerics.BigInteger))
            return typeof(object);

        // Function slots hold $TSFunction / other callable classes, not a .NET Delegate subclass.
        if (mapped == typeof(Delegate) || mapped.IsSubclassOf(typeof(Delegate)))
            return typeof(object);

        // Nullable<T> (e.g. `number | null` -> double?) has no emitter support; the runtime value is a
        // boxed primitive or null carried as object.
        if (Nullable.GetUnderlyingType(mapped) != null)
            return typeof(object);

        // Discriminated union structs (Union_*) are boxed opaquely by MethodInfo.Invoke; truthiness,
        // equality and member access can't see the underlying value.
        if (mapped.IsValueType && mapped.Name.StartsWith("Union_", StringComparison.Ordinal))
            return typeof(object);

        // Date/RegExp map (strictly) to the BCL DateTime/Regex, but the runtime values are the emitted
        // $TSDate/$RegExp reference types carried as object. A DateTime (value type) slot throws
        // InvalidCastException at the call site and StackUnexpected when a `$Runtime.DateGet*` helper
        // (declared `(object)`) is invoked on it; a Regex slot fails ILVerify the same way. (#573)
        if (mapped == typeMapper.Types.DateTime || mapped == typeMapper.Types.Regex)
            return typeof(object);

        // Typed array/map/set slots map to List<T>/Dictionary<,>/HashSet<>, but the runtime values are
        // dynamic $Array/$Map/$Set carried as object — not CLR-assignable to the declared collection. (#278)
        if (typeMapper.IsDynamicRuntimeCollection(mapped))
            return typeof(object);

        return mapped;
    }

    /// <summary>
    /// Resolves a single parameter's type from its annotation or defaults to object.
    /// </summary>
    private static Type ResolveParameterType(Stmt.Parameter param, TypeMapper typeMapper)
    {
        if (param.Type == null)
            return typeof(object);

        // Parse the type annotation and map to .NET type, falling back to object for slots whose
        // runtime value is carried as a boxed object (Date/RegExp/collections/Nullable/`T | undefined`).
        var typeInfo = ParseTypeAnnotation(param.Type);
        return SoundRuntimeSlotType(typeMapper.MapTypeInfoStrict(typeInfo), typeMapper, typeInfo);
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

            // String return slots are unsound. TS inference admits `undefined` in a
            // `string`-typed expression (e.g. `cond ? "x" : undefined` infers `string`;
            // an explicit `: string` annotation is rejected for `return undefined`, so
            // inference is the reachable path), but a .NET `string` slot cannot carry the
            // `$Undefined` sentinel. A castclass at the return site throws
            // InvalidCastException at runtime, and isinst/coercion corrupts `undefined`
            // into null/"undefined" (observable through Map keys, typeof, ===). Unlike
            // `double`/`bool` slots there is no boxing to avoid — strings are reference
            // types — so a `string` slot buys nothing. Fall back to object. (#318)
            //
            // (This is return-specific: an explicit `: string` *parameter* genuinely holds a
            // System.String and keeps its slot; only inferred `string` returns admit undefined.)
            if (baseType == typeof(string))
            {
                baseType = typeof(object);
            }

            // Fall back to object for any return whose runtime value is carried as a boxed object:
            // BigInteger, $TSFunction (mapped Delegate), Date/RegExp, $Array/$Map/$Set collections,
            // Nullable<T>, Union_* structs, and `T | undefined` unions. Shared with the parameter
            // slot resolution so both stay consistent. (#278/#568/#573)
            baseType = SoundRuntimeSlotType(baseType, typeMapper, returnTypeInfo);

            // A non-async function/arrow declared to return `Promise<T>` maps (strictly) to
            // Task<T>/Task, but its body never produces a real CLR Task — it returns the runtime
            // `$TSPromise` carried as object (e.g. the timers/promises re-export wrappers, which
            // return $Runtime.SetTimeoutPromise(...)). That object is not CLR-assignable to the
            // Task slot, so the `ret` raises ILVerify StackUnexpected; the JIT tolerates the
            // reference-type store (the program still runs), but `--verify` rejects it, and a
            // castclass at the return site would throw InvalidCastException since the value is not
            // a Task. Fall back to object so the dynamic promise is returned directly. Async
            // functions don't reach this resolver — they hardcode a Task<object> stub whose state
            // machine builds a real Task. (#393)
            if (!isAsync && (baseType == typeof(Task) ||
                (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(Task<>))))
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
            return parameters.Select(p => WidenIfUndefinedReachableParam(ResolveParameterType(p, typeMapper), p, typeMap)).ToArray();

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

                // If parameter is optional (no explicit default), use object so undefined
                // can be passed as the missing-argument sentinel (JS spec)
                if (i < parameters.Count &&
                    parameters[i].DefaultValue == null &&
                    parameters[i].IsOptional)
                {
                    return typeof(object);
                }

                // Fall back to object for slots whose runtime value is carried as a boxed object
                // (BigInteger, $TSFunction, Date/RegExp, collections, Nullable, Union_* structs, and
                // `T | undefined` unions). Rest params carry their own dispatch marker — leave them
                // untouched. (#278/#568/#573)
                if (i >= parameters.Count || !parameters[i].IsRest)
                    mappedType = SoundRuntimeSlotType(mappedType, typeMapper, pt);

                return i < parameters.Count
                    ? WidenIfUndefinedReachableParam(mappedType, parameters[i], typeMap)
                    : mappedType;
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
            return parameters.Select(p => WidenIfUndefinedReachableParam(ResolveParameterType(p, typeMapper), p, typeMap)).ToArray();

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

                // Fall back to object for slots whose runtime value is carried as a boxed object
                // (BigInteger, $TSFunction, Date/RegExp, collections, Nullable, Union_* structs, and
                // `T | undefined` unions). Rest params carry their own dispatch marker — leave them
                // untouched. (#278/#568/#573)
                if (i >= parameters.Count || !parameters[i].IsRest)
                    mappedType = SoundRuntimeSlotType(mappedType, typeMapper, pt);

                return i < parameters.Count
                    ? WidenIfUndefinedReachableParam(mappedType, parameters[i], typeMap)
                    : mappedType;
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
