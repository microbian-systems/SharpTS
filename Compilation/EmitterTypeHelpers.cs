using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Helpers for resolving members on closed generic types when the type may or may not
/// contain a TypeBuilder among its generic arguments.
///
/// Background: <see cref="TypeBuilder.GetConstructor(Type, ConstructorInfo)"/> (and the
/// matching <c>GetMethod</c>/<c>GetField</c> overloads) require the closed generic's
/// argument list to include at least one TypeBuilder — they exist to bridge metadata
/// tokens for unbaked types referenced by IL. After <see cref="TypeBuilder.CreateType"/>:
///   - <see cref="System.Reflection.Emit.PersistedAssemblyBuilder"/> keeps types as
///     TypeBuilders until <c>Save()</c>, so <c>TypeBuilder.GetConstructor</c> still
///     applies for closed generics built from those types.
///   - The runtime <see cref="AssemblyBuilder"/> (used by tests' fast in-memory path)
///     bakes types eagerly to <c>RuntimeType</c>; <c>TypeBuilder.GetConstructor</c>
///     then throws <c>'type' must be or must contain a TypeBuilder</c>. Plain
///     <see cref="Type.GetConstructor(Type[])"/> is the right call there.
///
/// This helper auto-detects which path to take so emitter sites stay mode-agnostic.
/// </summary>
internal static class EmitterTypeHelpers
{
    public static ConstructorInfo ResolveConstructor(Type closedGeneric, ConstructorInfo openCtor)
    {
        if (ContainsTypeBuilder(closedGeneric))
            return TypeBuilder.GetConstructor(closedGeneric, openCtor);

        var paramTypes = Array.ConvertAll(openCtor.GetParameters(), p => p.ParameterType);
        return closedGeneric.GetConstructor(paramTypes)
            ?? throw new InvalidOperationException(
                $"No matching constructor on {closedGeneric} for open ctor {openCtor.DeclaringType}::{openCtor}");
    }

    public static MethodInfo ResolveMethod(Type closedGeneric, MethodInfo openMethod)
    {
        if (ContainsTypeBuilder(closedGeneric))
            return TypeBuilder.GetMethod(closedGeneric, openMethod);

        // RuntimeType: walk the closed generic's methods and match by name + signature
        // metadata tokens (parameter & return types resolved against the closed generic).
        var openParams = openMethod.GetParameters();
        foreach (var m in closedGeneric.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (m.Name != openMethod.Name) continue;
            var mp = m.GetParameters();
            if (mp.Length != openParams.Length) continue;
            return m;
        }
        throw new InvalidOperationException(
            $"No matching method on {closedGeneric} for open method {openMethod.DeclaringType}::{openMethod.Name}");
    }

    public static FieldInfo ResolveField(Type closedGeneric, FieldInfo openField)
    {
        if (ContainsTypeBuilder(closedGeneric))
            return TypeBuilder.GetField(closedGeneric, openField);

        return closedGeneric.GetField(openField.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"No matching field on {closedGeneric} for open field {openField.DeclaringType}::{openField.Name}");
    }

    private static bool ContainsTypeBuilder(Type t)
    {
        if (t is TypeBuilder) return true;
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            // TypeBuilder.MakeGenericType / TypeBuilder.MakeGenericMethod produce
            // TypeBuilderInstantiation — a Type whose generic definition is a TypeBuilder
            // even when all type arguments are plain RuntimeType. Routing through plain
            // Type.GetConstructor on those throws NotSupportedException.
            if (t.GetGenericTypeDefinition() is TypeBuilder) return true;
            foreach (var arg in t.GetGenericArguments())
                if (ContainsTypeBuilder(arg)) return true;
        }
        if (t.HasElementType && t.GetElementType() is { } elem)
            return ContainsTypeBuilder(elem);
        return false;
    }
}
