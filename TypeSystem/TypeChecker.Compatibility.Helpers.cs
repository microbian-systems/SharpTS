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
