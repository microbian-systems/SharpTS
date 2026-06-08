using SharpTS.Runtime.BuiltIns;
using SharpTS.Parsing;
using System.Collections.Frozen;

namespace SharpTS.TypeSystem;

/// <summary>
/// Helper methods for type compatibility checking - type predicates and class accessors.
/// </summary>
public partial class TypeChecker
{
    /// <summary>
    /// Generic helper for type checking with union support.
    /// Checks if a type matches a predicate, with automatic handling for Any, Union, and TypeParameter types.
    /// </summary>
    /// <param name="t">The type to check.</param>
    /// <param name="baseTypeCheck">Predicate for checking base (non-Any, non-Union) types.</param>
    /// <returns>True if the type matches or is Any, or if all union members match, or if TypeParameter constraint matches.</returns>
    private bool IsTypeOfKind(TypeInfo t, Func<TypeInfo, bool> baseTypeCheck) =>
        baseTypeCheck(t) ||
        t is TypeInfo.Any ||
        (t is TypeInfo.Union u && u.FlattenedTypes.All(inner => IsTypeOfKind(inner, baseTypeCheck))) ||
        (t is TypeInfo.TypeParameter tp && tp.Constraint != null && IsTypeOfKind(tp.Constraint, baseTypeCheck));

    private bool IsNumber(TypeInfo t) =>
        IsTypeOfKind(t, type =>
            type is TypeInfo.Primitive p && p.Type == TokenType.TYPE_NUMBER ||
            type is TypeInfo.NumberLiteral);

    private bool IsString(TypeInfo t) =>
        IsTypeOfKind(t, type =>
            type is TypeInfo.String ||
            type is TypeInfo.StringLiteral);

    private bool IsBigInt(TypeInfo t) =>
        IsTypeOfKind(t, type => type is TypeInfo.BigInt);

    /// <summary>
    /// Checks if a type is a primitive (not valid as WeakMap key or WeakSet value).
    /// </summary>
    private bool IsPrimitiveType(TypeInfo t) => t is TypeInfo.String or TypeInfo.Primitive or TypeInfo.StringLiteral or TypeInfo.NumberLiteral or TypeInfo.BooleanLiteral or TypeInfo.BigInt or TypeInfo.Symbol or TypeInfo.UniqueSymbol;

    /// <summary>
    /// Checks if a type is an object type (has properties that could be mutated).
    /// Used for determining if passing to a function should invalidate property narrowings.
    /// </summary>
    private static bool IsObjectType(TypeInfo t) => t is TypeInfo.Record
        or TypeInfo.Interface
        or TypeInfo.Instance
        or TypeInfo.Class
        or TypeInfo.GenericClass
        or TypeInfo.InstantiatedGeneric
        or TypeInfo.Array
        or TypeInfo.Map
        or TypeInfo.Set;

    private static TypeInfo? GetSuperclass(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.Superclass, gc => gc.Superclass);

    /// <summary>
    /// True when <paramref name="cls"/> (or any class in its hierarchy) carries a nominal brand,
    /// i.e. declares a private or protected member. TypeScript compares classes structurally for
    /// assignment except when the target type is so branded, in which case it requires the source
    /// to originate from the same class.
    /// </summary>
    private static bool HasNominalClassBrand(TypeInfo.Class cls)
    {
        TypeInfo? current = cls;
        while (current is TypeInfo.Class c)
        {
            var core = c.Core;
            if (core.PrivateFieldTypes.Count > 0 || core.PrivateMethodTypes.Count > 0)
                return true;
            foreach (var access in core.FieldAccess.Values)
                if (access != AccessModifier.Public) return true;
            foreach (var access in core.MethodAccess.Values)
                if (access != AccessModifier.Public) return true;
            current = GetSuperclass(current);
        }
        return false;
    }

    private static bool IsPublicMember(FrozenDictionary<string, AccessModifier> access, string name)
        => !access.TryGetValue(name, out var mod) || mod == AccessModifier.Public;

    /// <summary>
    /// Returns the parameter type of <paramref name="f"/> at <paramref name="index"/>, expanding a
    /// trailing rest parameter to its element type so it covers any position at or beyond the rest
    /// slot (e.g. <c>(...a: number[])</c> yields <c>number</c> for every position). Returns null when
    /// the position is past a non-rest parameter list.
    /// </summary>
    private static TypeInfo? EffectiveParamType(TypeInfo.Function f, int index)
    {
        int count = f.ParamTypes.Count;
        if (f.HasRestParam && count > 0)
        {
            int restIndex = count - 1;
            if (index < restIndex) return f.ParamTypes[index];
            return f.ParamTypes[restIndex] is TypeInfo.Array arr ? arr.ElementType : f.ParamTypes[restIndex];
        }
        return index < count ? f.ParamTypes[index] : null;
    }

    /// <summary>
    /// Collects the public instance members (fields, methods, getters) of a class and its
    /// superclasses into a structural member map. Derived members shadow inherited ones.
    /// Used to check structural assignability against an unbranded target class.
    /// </summary>
    private static Dictionary<string, TypeInfo> CollectPublicInstanceMembers(TypeInfo.Class cls)
    {
        Dictionary<string, TypeInfo> members = [];
        TypeInfo? current = cls;
        while (current is TypeInfo.Class c)
        {
            var core = c.Core;
            foreach (var (name, type) in core.FieldTypes)
                if (IsPublicMember(core.FieldAccess, name) && !members.ContainsKey(name))
                    members[name] = type;
            foreach (var (name, type) in core.Methods)
                // The constructor is keyed as a method named "constructor" but is not part of the
                // instance type's structural surface in TypeScript — exclude it.
                if (name != "constructor" && IsPublicMember(core.MethodAccess, name) && !members.ContainsKey(name))
                    members[name] = type;
            foreach (var (name, type) in core.Getters)
                if (!members.ContainsKey(name))
                    members[name] = type;
            current = GetSuperclass(current);
        }
        return members;
    }

    private static FrozenDictionary<string, TypeInfo>? GetMethods(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.Methods, gc => gc.Methods);

    private static string? GetClassName(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.Name, gc => gc.Name);

    private static FrozenDictionary<string, TypeInfo>? GetStaticMethods(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.StaticMethods, gc => gc.StaticMethods);

    private static FrozenDictionary<string, TypeInfo>? GetStaticProperties(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.StaticProperties, gc => gc.StaticProperties);

    /// <summary>
    /// Converts a class-like type to a TypeInfo.Class for walking hierarchy.
    /// Returns null if the type is not class-like.
    /// </summary>
    private static TypeInfo.Class? AsClass(TypeInfo? classType) => classType switch
    {
        TypeInfo.Class c => c,
        _ => null
    };

    private static FrozenDictionary<string, TypeInfo>? GetFieldTypes(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.FieldTypes, gc => gc.FieldTypes);

    private static FrozenDictionary<string, TypeInfo>? GetGetters(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.Getters, gc => gc.Getters);

    private static FrozenDictionary<string, TypeInfo>? GetSetters(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.Setters, gc => gc.Setters);

    private static FrozenDictionary<string, AccessModifier>? GetMethodAccess(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.MethodAccess, gc => gc.MethodAccess);

    private static FrozenDictionary<string, AccessModifier>? GetFieldAccess(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.FieldAccess, gc => gc.FieldAccess);

    private static FrozenSet<string>? GetReadonlyFields(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.ReadonlyFields, gc => gc.ReadonlyFields);

    private static FrozenSet<string>? GetAbstractMethods(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.AbstractMethodSet, gc => gc.AbstractMethodSet);

    private static FrozenSet<string>? GetAbstractGetters(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.AbstractGetterSet, gc => gc.AbstractGetterSet);

    private static FrozenSet<string>? GetAbstractSetters(TypeInfo? classType) =>
        ClassInfoAccessor.Get(classType, c => c.AbstractSetterSet, gc => gc.AbstractSetterSet);
}
